#Requires -Version 7

<#
批次10-02（control-server#546）四条「同向一类绑定完备、走完一趟」场景共用的部分。由各场景点号引入，不是场景本身，没有
setup 文件，编排器也不会单独运行它（与 CargoHoldingCommon.ps1、TaskPriorityCommon.ps1 同一种安排）。

规格 8.3 批次 10：「同向四类各一条 L2：缺绑定不投运、绑定完备后走完一趟，含 AREA 端点按侧取仓」；REQ-0184、REQ-0335、
REQ-0334。四个场景的 setup 形状相同，只差任务类型与绑定站：
- 假 RIoT 站表整张替换：关卡 210、机台站 12（N1-3_N1-7）、11 号站（C15-13），再加本类的固定站，站名不是 AREA 格式，
  四个场景各用一个站号（401～404）；
- 预置配置里本类与 WIRE_TO_GATE 都已绑定（REQ-0334：一站不被两类绑定）；
- 分区归属表把挂在同一个机台站 12 上的两个 AREA 分到两侧：N1-3 指 REAR、N1-7 指 FRONT。

每个场景先后放两条本类需求（N1-3 一条、N1-7 一条，各 2 个花篮），各自走完一趟，每条断言：
  -01 受理：旅程进入 AwaitingPickupArrival，受理行的任务类型是本类（没受理时实际值写积压原因，例如批次10-01 之前的
      TASK_TYPE_NOT_YET_EXECUTABLE）；
  -02 两端没有对调：取货在 AREA 机台站 12、卸货在本类绑定站，不是关卡 210；冻结的卸货站与两条腿的订单终点都对得上；
  -03 目标仓全部落在该需求 AREA 指派的那一组、升序，且是该组编号最小的 2 个可用仓（L2SlotGroups.psm1）；
  -04 机台站下发的 LOAD 与绑定站下发的 UNLOAD，slots 都等于目标仓；
  -05 卸货完成、需求结清：旅程 Completed，卸货操作 Committed，受理行 Succeeded（另等一次，不与 Completed 同读）。

**探针不取闭包**，理由见 CargoHoldingCommon.ps1 开头：不取闭包的脚本块记得定义它的作用域，Wait-L2Condition 调它时
这些函数还在栈上，参数读得到。
#>

Set-StrictMode -Version Latest

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SlotGroups.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SingleRow.psm1') -Force

$script:SameDirectionMachineStation = 12
$script:SameDirectionGateStation = 210
# L2-PACKAGE 每篮 4 盒（编排器导入的包装容量），8 盒就是 2 个花篮。
$script:SameDirectionBaskets = 2

function Get-L2SameDirectionRuntime([object]$Connection, [string]$DemandId) {
    return Read-L2SingleRow -Connection $Connection -Sql (
        "SELECT Stage, PickupStationRiotId, GateStationRiotId, TargetSlotsJson, ExpectedBasketCount, BlockReasonCode " +
        "FROM JourneyRuntimes WHERE DemandId = '$DemandId'")
}

function Get-L2SameDirectionIntent([object]$Connection, [string]$DemandId, [string]$Purpose) {
    return Read-L2SingleRow -Connection $Connection -Sql (
        "SELECT UpperId, OrderId, Status, DestinationStationId FROM OrderIntents WHERE DemandId = '$DemandId' AND Purpose = '$Purpose'")
}

# 受理与否一次读出：旅程阶段、受理行的任务类型、积压原因。没受理时后两样说明为什么。
function Get-L2SameDirectionAdmission([object]$Connection, [string]$DemandId) {
    $rows = Invoke-L2Query -Connection $Connection -Sql @"
SELECT (SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$DemandId') AS Stage,
       (SELECT WorkType FROM AcceptedDemands WHERE DemandId = '$DemandId') AS WorkType,
       (SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$DemandId') AS ReasonCode
"@
    return $rows[0]
}

function Get-L2SameDirectionCommandSlots([object]$Connection, [string]$DemandId, [string]$OperationType) {
    $rows = Invoke-L2Query -Connection $Connection `
        -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'SlotOperationCommand' ORDER BY CreatedAt, MessageId"
    $commands = @($rows | ForEach-Object { ([string]$_.PayloadJson | ConvertFrom-Json).payload } |
        Where-Object { [string]$_.demandId -eq $DemandId -and [string]$_.operationType -ceq $OperationType })
    return , $commands
}

# 车从当前位置开到 $StationRiotId 并停稳，照 staging-to-wire-slot-group 的写法。
function Move-L2SameDirectionVehicle([object]$Context, [object]$Intent, [int]$StationRiotId) {
    $riot = $Context.Riot
    $Context.Journal.Note("Vehicle drives to station $StationRiotId and comes to rest.")
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $Intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $StationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 5 })
}

# 一条本类需求：发布、受理、两端、按侧取仓、走完、装卸命令、结清。
function Test-L2SameDirectionDemand {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$TaskType,
        [Parameter(Mandatory)][int]$BoundStationRiotId,
        [Parameter(Mandatory)][string]$Area,
        [Parameter(Mandatory)][string]$SlotPosition,
        [Parameter(Mandatory)][string]$IdPrefix
    )

    $journal = $Context.Journal
    $assertions = $Context.Assertions
    $connection = $Context.Connection
    $machine = $script:SameDirectionMachineStation
    $gate = $script:SameDirectionGateStation
    $baskets = $script:SameDirectionBaskets

    $guid = [guid]::NewGuid()
    $demandId = $guid.ToString('D')
    $sublot = "L2-SDJ-$Area-$($Context.RunId)"
    $journal.Note("Publishing $TaskType demand $($guid.ToString('N')) (sublot $sublot, AREA $Area, $baskets baskets).")
    # 同一个 AREA 的需求共用一台 EQP：一个 AREA 在目录里挂两台 EQP 是 AREA_EQP_NOT_UNIQUE（TaskPriorityCommon.ps1 同一条）。
    $null = $Context.MesIngest.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot      = $sublot
        workType    = $TaskType
        area        = $Area
        eqp         = "EQP-L2-SDJ-$Area"
        package     = 'L2-PACKAGE'
        maxBoxCount = 4 * $baskets
    })

    # --- 受理 ---
    # 超时不抛：没受理时把积压原因带进判据表，再停下（后面每一步都要这趟旅程）。批次10-01 之前这里读到的是
    # TASK_TYPE_NOT_YET_EXECUTABLE，那就是本组场景的红证据。
    $admission = Wait-L2ConditionOrLast -Description "the $TaskType demand ($Area) was accepted and dispatched to the machine station" `
        -Journal $journal -Criterion "$IdPrefix-admission" -TimeoutSeconds 90 `
        -Probe { Get-L2SameDirectionAdmission $connection $demandId } `
        -Until { param($v) [string]$v.Stage -eq 'AwaitingPickupArrival' }
    $accepted = [string]$admission.Stage -eq 'AwaitingPickupArrival' -and [string]$admission.WorkType -ceq $TaskType
    $assertions.Add(
        "$IdPrefix-01", "$TaskType 需求（$Area）被受理：旅程进入 AwaitingPickupArrival，受理行的任务类型是 $TaskType",
        $accepted,
        "AwaitingPickupArrival / $TaskType",
        "$(if ([string]::IsNullOrEmpty([string]$admission.Stage)) { '(no journey)' } else { $admission.Stage }) / " +
        "$(if ([string]::IsNullOrEmpty([string]$admission.WorkType)) { '(not accepted)' } else { $admission.WorkType }) / " +
        "backlog reason $(if ([string]::IsNullOrEmpty([string]$admission.ReasonCode)) { '(none)' } else { $admission.ReasonCode })")
    if (-not $accepted) {
        throw "$IdPrefix-01 did not hold: the $TaskType demand ($Area) was not accepted (backlog reason '$($admission.ReasonCode)'); every later criterion needs its journey."
    }

    # --- 两端：取货在 AREA 机台站，卸货在本类绑定站，不对调 ---
    $runtime = Get-L2SameDirectionRuntime $connection $demandId
    $pickupIntent = Wait-L2Condition -Description "the $Area TO_PICKUP intent was confirmed" `
        -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
        -Probe { Get-L2SameDirectionIntent $connection $demandId 'TO_PICKUP' } `
        -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
    $frozen = Read-L2SingleRow -Connection $connection -Required `
        -Sql "SELECT StationId FROM FrozenDemandStations WHERE DemandId = '$demandId' AND Role = 'Dropoff'"

    $targets = @([string]$runtime.TargetSlotsJson | ConvertFrom-Json | ForEach-Object { [int]$_ })
    $null = Assert-L2SlotGroupTargets -Assertions $assertions -Id "$IdPrefix-03" -Connection $connection `
        -DemandId $demandId -SlotPosition $SlotPosition `
        -Description "$Area 指 ${SlotPosition}：取货时目标仓全部属于本车 $SlotPosition 组、升序，且恰好是该组编号最小的 $baskets 个可用仓"

    Move-L2SameDirectionVehicle $Context $pickupIntent $machine
    $null = Wait-L2Condition -Description "the $Area load committed and the journey reached the drop-off leg" `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { $r = Get-L2SameDirectionRuntime $connection $demandId; if ($r) { [string]$r.Stage } else { $null } } `
        -Until { param($v) $v -eq 'AwaitingGateArrival' }
    $dropoffIntent = Wait-L2Condition -Description "the $Area drop-off-leg intent was confirmed" `
        -Journal $journal -Criterion 'to-gate-intent' -TimeoutSeconds 60 `
        -Probe { Get-L2SameDirectionIntent $connection $demandId 'TO_GATE' } `
        -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
    $assertions.Add(
        "$IdPrefix-02", "$TaskType（$Area）两端没有对调：取货在 AREA 机台站 $machine、卸货在本类绑定站 $BoundStationRiotId（不是关卡 $gate）；冻结的卸货站与两条腿的订单终点一致",
        ([int]$runtime.PickupStationRiotId -eq $machine -and [int]$runtime.GateStationRiotId -eq $BoundStationRiotId -and
            [string]$frozen.StationId -eq [string]$BoundStationRiotId -and
            [string]$pickupIntent.DestinationStationId -eq [string]$machine -and
            [string]$dropoffIntent.DestinationStationId -eq [string]$BoundStationRiotId),
        "pickup $machine / drop-off $BoundStationRiotId / frozen $BoundStationRiotId / legs $machine -> $BoundStationRiotId",
        "pickup $($runtime.PickupStationRiotId) / drop-off $($runtime.GateStationRiotId) / frozen $($frozen.StationId) / " +
        "legs $($pickupIntent.DestinationStationId) -> $($dropoffIntent.DestinationStationId)")

    # --- 走完：卸货在绑定站 ---
    Move-L2SameDirectionVehicle $Context $dropoffIntent $BoundStationRiotId
    $stage = Wait-L2ConditionOrLast -Description "the $Area journey completed at the bound station $BoundStationRiotId" `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { $r = Get-L2SameDirectionRuntime $connection $demandId; if ($r) { [string]$r.Stage } else { $null } } `
        -Until { param($v) $v -eq 'Completed' }

    $sent = @()
    foreach ($type in 'LOAD', 'UNLOAD') {
        # Assigned before it is piped: the result set would otherwise arrive as one element (README).
        $commands = Get-L2SameDirectionCommandSlots $connection $demandId $type
        $slots = @($commands | ForEach-Object { (@($_.slots | ForEach-Object { [int]$_ }) -join ',') } | Sort-Object -Unique)
        $sent += "$type " + $(if ($slots.Count -eq 0) { '(none)' } else { ($slots | ForEach-Object { "[$_]" }) -join ' ' })
    }
    $expectedSent = "LOAD [$($targets -join ',')]; UNLOAD [$($targets -join ',')]"
    $assertions.Add(
        "$IdPrefix-04", "$TaskType（$Area）：机台站下发的 LOAD 与绑定站下发的 UNLOAD，slots 都等于目标仓",
        (($sent -join '; ') -ceq $expectedSent), $expectedSent, ($sent -join '; '))

    # 结清是另一件事：受理行的终态不一定与旅程 Completed 同一次提交，另等一次。
    $settled = Wait-L2ConditionOrLast -Description "the $Area demand was settled" `
        -Journal $journal -Criterion "$IdPrefix-settled" -TimeoutSeconds 60 `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection -Sql @"
SELECT (SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId') AS DemandStatus,
       (SELECT COUNT(*) FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Unload' AND Status = 'Committed') AS Unloads
"@
            $rows[0]
        } `
        -Until { param($v) [string]$v.DemandStatus -eq 'Succeeded' -and [int]$v.Unloads -eq 1 }
    $assertions.Add(
        "$IdPrefix-05", "$TaskType（$Area）卸货完成、需求结清：旅程 Completed，卸货操作 Committed，受理行 Succeeded",
        ($stage -eq 'Completed' -and [string]$settled.DemandStatus -eq 'Succeeded' -and [int]$settled.Unloads -eq 1),
        'Completed / 1 committed unload / Succeeded',
        "$stage / $($settled.Unloads) committed unload(s) / $($settled.DemandStatus)")
}

<#
一个场景的全部：前置核对（站表里有本类的固定站、名字不是 AREA 格式；归属表两侧），然后 N1-3（REAR）与 N1-7（FRONT）
各走一趟。
#>
function Invoke-L2SameDirectionJourneyScenario {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$TaskType,
        [Parameter(Mandatory)][int]$BoundStationRiotId,
        [Parameter(Mandatory)][string]$BoundStationName,
        [Parameter(Mandatory)][string]$IdPrefix
    )

    $journal = $Context.Journal
    $assertions = $Context.Assertions
    $connection = $Context.Connection

    $mapStations = @(@($Context.Riot.Snapshot().body.maps | Where-Object { [int]$_.mapId -eq $Context.MapId }) |
        ForEach-Object { $_.stations })
    $bound = @($mapStations | Where-Object { [int]$_.id -eq $BoundStationRiotId })
    $table = & $Context.InvokeFieldOps -Arguments @('area-assignments')
    $entries = @($table.entries | ForEach-Object { "$($_.area)/$($_.slotPosition)" } | Sort-Object)
    $assertions.Add(
        "$IdPrefix-00", "前置：本类固定站 $BoundStationRiotId「$BoundStationName」在地图上、站名不是 AREA 格式；归属表 N1-3 → REAR、N1-7 → FRONT（同挂机台站 12）",
        ($bound.Count -eq 1 -and [string]$bound[0].name -ceq $BoundStationName -and
            [string]$bound[0].name -cnotmatch '^[A-Z][A-Z0-9]*-[0-9]+(_[A-Z][A-Z0-9]*-[0-9]+){0,2}$' -and
            [string]$table.outcome -eq 'OK' -and ($entries -join ',') -ceq 'N1-3/REAR,N1-7/FRONT'),
        "$BoundStationRiotId=$BoundStationName / N1-3/REAR,N1-7/FRONT",
        "$(if ($bound.Count -eq 1) { "$BoundStationRiotId=$($bound[0].name)" } else { "$($bound.Count) stations with id $BoundStationRiotId" }) / $($entries -join ',')")

    $positions = Get-L2VehicleSlotPositions -Connection $connection -AgvId $Context.AgvId
    if ($null -eq $positions) { throw "Vehicle $($Context.AgvId) has no resolvable slot model; the preseed did not bind it." }
    $journal.Note("Vehicle $($Context.AgvId) slot model $($positions.SlotModelVersionId) via $($positions.Source): " +
        (($positions.Positions.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))

    Test-L2SameDirectionDemand -Context $Context -TaskType $TaskType -BoundStationRiotId $BoundStationRiotId `
        -Area 'N1-3' -SlotPosition 'REAR' -IdPrefix "$IdPrefix-REAR"
    Test-L2SameDirectionDemand -Context $Context -TaskType $TaskType -BoundStationRiotId $BoundStationRiotId `
        -Area 'N1-7' -SlotPosition 'FRONT' -IdPrefix "$IdPrefix-FRONT"

    $journal.Note("$TaskType：两条需求各在 AREA 机台站 12 按侧取仓、到绑定站 $BoundStationRiotId 卸货，都已结清。")
}
