#Requires -Version 7

<#
指派版本冻结，导入新版本不停车生效（control-server#75，规格 8.3 批次 4 场景⑤；REQ-0350）。

分区归属表给每个区域号（AREA）指派开哪一侧仓门的分组。需求在受理、分配目标仓位时冻结当时那一版表，装货与卸货都按
冻结的版本执行；之后导入的新版本只管新受理的需求。场景把 #68（导入与预览）、#72（冻结）、#73（按组选仓）串成一趟：

  1. 版本 1（默认前置按边车导入）：N1-3 → FRONT。放需求甲（N1-3，2 花篮），受理后目标仓是 FRONT 组最小的两个，
     冻结行版本 1。
  2. 需求甲装完货、车正在开往关卡的途中，导入版本 2：N1-3 → REAR。导入输出的预览恰好列出需求甲（原分组 FRONT、
     新分组 REAR）；服务端进程没换（同一个 PID、同一个启动时刻）、车的会话代没变、车没有停下来等空闲——不停车生效。
  3. 需求甲到关卡卸货：卸货命令的 slots 仍是受理时的目标仓，冻结行仍是版本 1。
  4. 放需求乙（N1-3，2 花篮）：受理后目标仓是 REAR 组最小的两个，冻结行版本 2。
  5. 把版本 1 的内容原样再导入（回滚）：形成版本 3，内容与版本 1 相同，导入审计多一条。

分组从 SlotModelSlots 经该车的记录读（L2SlotGroups.psm1），脚本里不写 1～4／5～8。断言对端收到的命令与库里的状态时，
都先用 Wait-L2Condition 等到它出现，再取样断言。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SlotGroups.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection

$area = 'N1-3'
# L2-PACKAGE 每篮 4 盒（编排器导入的包装容量），8 盒就是 2 个花篮。
$basketCount = 2
$maxBoxCount = 8
$csvRoot = $Context.SnapshotRoot

function Publish-Demand([string]$Suffix) {
    $guid = [guid]::NewGuid()
    $sublot = "L2-AVF-$Suffix-$($Context.RunId)"
    $journal.Note("Publishing demand $($guid.ToString('N')) (sublot $sublot, area $area, $maxBoxCount boxes).")
    $null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot      = $sublot
        area        = $area
        # One machine for both demands: an AREA naming two EQPs in the catalog is AREA_EQP_NOT_UNIQUE, and the first
        # demand stays listed after its journey completes.
        eqp         = 'EQP-L2-AVF-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = $maxBoxCount
    })
    return $guid.ToString('D')
}

function Get-Runtime([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$DemandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-TargetSlots([string]$DemandId) {
    $runtime = Get-Runtime $DemandId
    if ($null -eq $runtime) { return , @() }
    return , @([string]$runtime.TargetSlotsJson | ConvertFrom-Json | ForEach-Object { [int]$_ })
}

function Get-Intent([string]$DemandId, [string]$Purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$DemandId' AND Purpose = '$Purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 这条需求冻结的归属表版本，没有冻结行时为 $null。
function Get-FrozenVersion([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql @"
SELECT FrozenVersion FROM ConfigurationConsumerBindings
WHERE ConsumerKind = 'TransportDemand' AND ObjectKind = 'DispatchZoneAreaAssignment' AND ConsumerId = '$DemandId'
"@
    if ($rows.Count -eq 0) { return $null }
    if ($rows.Count -gt 1) { return -1 }
    return [long]$rows[0].FrozenVersion
}

function Get-Count([string]$Sql) {
    return [int](Invoke-L2Query -Connection $connection -Sql $Sql)[0].N
}

function Get-ImportAuditCount {
    return Get-Count "SELECT COUNT(*) AS N FROM BusinessAuditRecords WHERE Action = 'DISPATCH_ZONE_AREA_ASSIGNMENT_VERSION_IMPORTED'"
}

function Get-SessionGeneration {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT SessionGeneration FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'"
    if ($rows.Count -eq 0) { return $null }
    return [long]$rows[0].SessionGeneration
}

# 服务端进程的身份：在健康端口上监听的那个进程的 PID 与启动时刻。只比 PID 挡不住「重启后恰好拿到同一个号」。
function Get-ServerIdentity {
    $ids = @(Get-L2ListeningProcess -Port $Context.HealthPort)
    if ($ids.Count -ne 1) { return "listeners: $($ids -join ',')" }
    $process = Get-Process -Id $ids[0] -ErrorAction SilentlyContinue
    if (-not $process) { return "pid $($ids[0]) (gone)" }
    return "pid $($ids[0]) $($process.ProcessName) started $($process.StartTime.ToString('o'))"
}

function Get-CommandSlots([string]$DemandId, [string]$OperationType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'SlotOperationCommand' ORDER BY CreatedAt, MessageId"
    $commands = @($rows | ForEach-Object { ([string]$_.PayloadJson | ConvertFrom-Json).payload } |
        Where-Object { [string]$_.demandId -eq $DemandId -and [string]$_.operationType -ceq $OperationType })
    return , $commands
}

function Import-Assignments([string]$Name, [object[]]$Rows) {
    $csv = Join-Path $csvRoot "$Name.csv"
    $lines = @('area,dispatch_zone,slot_position') + @($Rows | ForEach-Object { "$($_.area),$($_.dispatchZone),$($_.slotPosition)" })
    # 无 BOM 的 UTF-8、LF 换行：工具文档里的受控格式。喂进去的这份留在证据目录。
    [IO.File]::WriteAllText($csv, (($lines -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
    $result = & $Context.InvokeFieldOps -Arguments @('import-area-assignments', '--input', $csv)
    $journal.Observe("area-assignment-import:$Name", $result.outcome, @{ output = $result })
    return $result
}

function Start-Leg([object]$Intent) {
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $Intent.OrderId
    })
}

function Complete-Leg([object]$Intent, [int]$StationRiotId) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $StationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 5 })
}

function Wait-Stage([string]$DemandId, [string]$Stage, [string]$Description, [int]$TimeoutSeconds = 120) {
    return Wait-L2Condition -Description $Description -Journal $journal -Criterion 'journey-stage' `
        -TimeoutSeconds $TimeoutSeconds `
        -Probe { $r = Get-Runtime $DemandId; if ($r) { [string]$r.Stage } else { $null } } `
        -Until { param($v) $v -eq $Stage }
}

function Wait-Intent([string]$DemandId, [string]$Purpose) {
    return Wait-L2Condition -Description "the $Purpose intent was confirmed" `
        -Journal $journal -Criterion "$($Purpose.ToLowerInvariant())-intent" -TimeoutSeconds 60 `
        -Probe { Get-Intent $DemandId $Purpose } -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
}

# --- 0. 前置：版本 1 就是边车那张表 ------------------------------------------------------------------

$version1 = & $Context.InvokeFieldOps -Arguments @('area-assignments')
$version1Entries = @($version1.entries | ForEach-Object { "$($_.area)/$($_.dispatchZone)/$($_.slotPosition)" })
$assertions.Add(
    'L2-AVF-01', "前置：当前归属表是版本 1，只有 $area → FRONT 一行",
    ([string]$version1.outcome -eq 'OK' -and [long]$version1.version -eq 1 -and
        ($version1Entries -join ',') -ceq "$area/$($Context.DispatchZone)/FRONT"),
    "OK / version 1 / $area/$($Context.DispatchZone)/FRONT",
    "$($version1.outcome) / version $($version1.version) / $($version1Entries -join ',')")
$positions = Get-L2VehicleSlotPositions -Connection $connection -AgvId $Context.AgvId
if ($null -eq $positions) { throw "Vehicle $($Context.AgvId) has no resolvable slot model; the preseed did not bind it." }
$journal.Note("Vehicle $($Context.AgvId) slot model $($positions.SlotModelVersionId) via $($positions.Source): " +
    (($positions.Positions.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))

# --- 1. 需求甲在版本 1 下受理：FRONT 组最小的两个仓，冻结版本 1 ----------------------------------------

$demandA = Publish-Demand 'A'
$null = Wait-Stage $demandA 'AwaitingPickupArrival' 'demand A was accepted and dispatched to the pickup station' 90
$null = Wait-L2Condition -Description 'demand A froze an area assignment version' `
    -Journal $journal -Criterion 'freeze-row' -TimeoutSeconds 30 `
    -Probe { Get-FrozenVersion $demandA } -Until { param($v) $null -ne $v }

$targetsA = Get-TargetSlots $demandA
$null = Assert-L2SlotGroupTargets -Assertions $assertions -Id 'L2-AVF-02' -Connection $connection `
    -DemandId $demandA -SlotPosition 'FRONT' `
    -Description "需求甲（$basketCount 花篮）在版本 1 下受理：目标仓全部属于 FRONT 组、升序，且恰好是该组编号最小的 $basketCount 个可用仓"
$frozenA = Get-FrozenVersion $demandA
$assertions.Add('L2-AVF-03', '需求甲的冻结行是版本 1', ($frozenA -eq 1), '1', "$frozenA")

# --- 2. 需求甲在途（装完货、正开往关卡）时导入版本 2：N1-3 → REAR -----------------------------------------

$pickupIntent = Wait-Intent $demandA 'TO_PICKUP'
$journal.Note('Vehicle drives to the pickup station and comes to rest.')
Start-Leg $pickupIntent
Complete-Leg $pickupIntent $Context.PickupStationRiotId
$null = Wait-Stage $demandA 'AwaitingGateArrival' 'demand A loaded and its journey reached the gate leg'
$gateIntent = Wait-Intent $demandA 'TO_GATE'

$journal.Note('Vehicle sets off for the gate; the new table is imported while it is on the way.')
Start-Leg $gateIntent
# 等服务端读到车在跑，再导入：这样「车没停下来」是服务端也看见的事实，不只是替身里的一个字段。
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal

$serverBefore = Get-ServerIdentity
$sessionBefore = Get-SessionGeneration
$import2 = Import-Assignments 'area-assignments-v2-n1-3-rear' @(
    [pscustomobject]@{ area = $area; dispatchZone = $Context.DispatchZone; slotPosition = 'REAR' })
$vehicleAtImport = @($riot.Snapshot().body.vehicles | Where-Object { [string]$_.deviceKey -eq $Context.VehicleKey })
$stageAtImport = [string](Get-Runtime $demandA).Stage

$assertions.Add(
    'L2-AVF-04', "导入版本 2（$area → REAR）成功",
    ([string]$import2.outcome -eq 'OK' -and [long]$import2.version -eq 2 -and [int]$import2.entryCount -eq 1),
    'OK / version 2 / 1 entry', "$($import2.outcome) / version $($import2.version) / $($import2.entryCount) entries")

$preview = @($import2.preview)
$previewText = if ($preview.Count -eq 0) { '(empty)' } else {
    ($preview | ForEach-Object { "$($_.demandId) $($_.area) v$($_.frozenVersion) $($_.frozenSlotPosition)->$($_.newSlotPosition)" }) -join '; ' }
$assertions.Add(
    'L2-AVF-05', '导入预览恰好列出在途的需求甲：原分组 FRONT（冻结版本 1）、新分组 REAR',
    ($preview.Count -eq 1 -and [string]$preview[0].demandId -eq $demandA -and [string]$preview[0].area -ceq $area -and
        [long]$preview[0].frozenVersion -eq 1 -and [string]$preview[0].frozenSlotPosition -ceq 'FRONT' -and
        [string]$preview[0].newSlotPosition -ceq 'REAR'),
    "$demandA $area v1 FRONT->REAR", $previewText)

# 导入之后再让派车循环转几轮，服务端仍是同一个进程、同一代会话：生效不靠重启，也不靠重连。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$serverAfter = Get-ServerIdentity
$sessionAfter = Get-SessionGeneration
$assertions.Add(
    'L2-AVF-06', '不停车生效之一：导入前后服务端是同一个进程（PID 与启动时刻都不变）',
    ($serverBefore -like 'pid *started*' -and $serverAfter -ceq $serverBefore),
    $serverBefore, $serverAfter)
$assertions.Add(
    'L2-AVF-07', '不停车生效之二：导入前后车的会话代不变',
    ($null -ne $sessionBefore -and $sessionAfter -eq $sessionBefore),
    "$sessionBefore", "$sessionAfter")
$vehicleText = if ($vehicleAtImport.Count -eq 1) { "procState $($vehicleAtImport[0].procState)" } else { "$($vehicleAtImport.Count) vehicles" }
$assertions.Add(
    'L2-AVF-08', '不停车生效之三：导入时车在开往关卡的途中（RIoT 报 RUNNING、旅程在 AwaitingGateArrival），不需要空闲',
    ($vehicleAtImport.Count -eq 1 -and [string]$vehicleAtImport[0].procState -ceq 'RUNNING' -and $stageAtImport -eq 'AwaitingGateArrival'),
    'procState RUNNING / AwaitingGateArrival', "$vehicleText / $stageAtImport")

# --- 3. 需求甲到关卡卸货：仍开受理时的那一组，冻结行仍是版本 1 -----------------------------------------------

$journal.Note('Vehicle arrives at the gate and comes to rest.')
Complete-Leg $gateIntent $Context.GateStationRiotId
$null = Wait-L2Condition -Description 'the UNLOAD command for demand A was sent to the vehicle' `
    -Journal $journal -Criterion 'unload-command' -TimeoutSeconds 120 `
    -Probe { (Get-CommandSlots $demandA 'UNLOAD').Count } -Until { param($v) $v -ge 1 }
$stageA = Wait-Stage $demandA 'Completed' 'demand A completed at the gate'

$unloads = Get-CommandSlots $demandA 'UNLOAD'
$unloadSlots = @($unloads | ForEach-Object { (@($_.slots | ForEach-Object { [int]$_ }) -join ',') } | Sort-Object -Unique)
$assertions.Add(
    'L2-AVF-09', '需求甲走完：卸货命令的 slots 仍是受理时的目标仓（FRONT 组），没有按版本 2 改开 REAR',
    ($stageA -eq 'Completed' -and $unloads.Count -ge 1 -and $unloadSlots.Count -eq 1 -and $unloadSlots[0] -eq ($targetsA -join ',')),
    "Completed / [$($targetsA -join ',')]",
    "$stageA / $($unloads.Count) command(s), slots: $(if ($unloadSlots.Count -eq 0) { '(none)' } else { ($unloadSlots | ForEach-Object { "[$_]" }) -join ' ' })")
$frozenA = Get-FrozenVersion $demandA
$assertions.Add('L2-AVF-10', '需求甲走完之后，冻结行仍是版本 1', ($frozenA -eq 1), '1', "$frozenA")

# --- 4. 需求乙在版本 2 下受理：REAR 组最小的两个仓，冻结版本 2 ------------------------------------------

$demandB = Publish-Demand 'B'
$null = Wait-Stage $demandB 'AwaitingPickupArrival' 'demand B was accepted and dispatched to the pickup station' 90
$null = Wait-L2Condition -Description 'demand B froze an area assignment version' `
    -Journal $journal -Criterion 'freeze-row' -TimeoutSeconds 30 `
    -Probe { Get-FrozenVersion $demandB } -Until { param($v) $null -ne $v }
$null = Assert-L2SlotGroupTargets -Assertions $assertions -Id 'L2-AVF-11' -Connection $connection `
    -DemandId $demandB -SlotPosition 'REAR' `
    -Description "需求乙（$basketCount 花篮）在版本 2 下受理：目标仓全部属于 REAR 组、升序，且恰好是该组编号最小的 $basketCount 个可用仓"
$frozenB = Get-FrozenVersion $demandB
$assertions.Add('L2-AVF-12', '需求乙的冻结行是版本 2', ($frozenB -eq 2), '2', "$frozenB")

# --- 5. 回滚：把版本 1 的内容原样再导入，形成版本 3 ---------------------------------------------------------

$source = & $Context.InvokeFieldOps -Arguments @('area-assignments', '--version', '1')
$auditsBefore = Get-ImportAuditCount
$import3 = Import-Assignments 'area-assignments-v3-rollback-to-v1' @($source.entries)
$version3 = & $Context.InvokeFieldOps -Arguments @('area-assignments', '--version', '3')
$version3Entries = @($version3.entries | ForEach-Object { "$($_.area)/$($_.dispatchZone)/$($_.slotPosition)" })
$sourceEntries = @($source.entries | ForEach-Object { "$($_.area)/$($_.dispatchZone)/$($_.slotPosition)" })
$auditsAfter = Get-ImportAuditCount
$assertions.Add(
    'L2-AVF-13', '回滚：版本 1 的内容原样导入，形成新的版本 3（不是改回版本 1），内容与哈希都与版本 1 相同',
    ([string]$import3.outcome -eq 'OK' -and [long]$import3.version -eq 3 -and [string]$version3.outcome -eq 'OK' -and
        ($version3Entries -join ',') -ceq ($sourceEntries -join ',') -and
        [string]$version3.contentSha256 -ceq [string]$source.contentSha256),
    "OK / version 3 / $($sourceEntries -join ',') / sha256 $($source.contentSha256)",
    "$($import3.outcome) / version $($import3.version) / $($version3Entries -join ',') / sha256 $($version3.contentSha256)")
$assertions.Add(
    'L2-AVF-14', '回滚在导入审计里多记一条',
    ($auditsAfter -eq $auditsBefore + 1),
    "$($auditsBefore + 1)", "$auditsAfter")

$frozenB = Get-FrozenVersion $demandB
$rollbackPreview = @($import3.preview)
$assertions.Add(
    'L2-AVF-15', '回滚不改变已冻结的需求：需求乙仍冻结版本 2，回滚预览恰好列出在途的需求乙（REAR → FRONT）',
    ($frozenB -eq 2 -and $rollbackPreview.Count -eq 1 -and [string]$rollbackPreview[0].demandId -eq $demandB -and
        [string]$rollbackPreview[0].frozenSlotPosition -ceq 'REAR' -and [string]$rollbackPreview[0].newSlotPosition -ceq 'FRONT'),
    "frozen 2 / $demandB REAR->FRONT",
    "frozen $frozenB / $(($rollbackPreview | ForEach-Object { "$($_.demandId) $($_.frozenSlotPosition)->$($_.newSlotPosition)" }) -join '; ')")

$journal.Note('需求甲冻结版本 1 并按它卸货，途中导入的版本 2 不停车生效、只管需求乙，回滚形成版本 3。')
