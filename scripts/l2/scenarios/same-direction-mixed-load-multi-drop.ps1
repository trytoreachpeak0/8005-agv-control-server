#Requires -Version 7

<#
一车混装 WIRE_TO_GATE、WIRE_TO_OPTICAL、WIRE_TO_NITROGEN，停站时途中追加，依次到三个终点卸货（批次10-02，control-server#546；
用户 2026-10-10 要求，经 Coordinator 10 转达：「加，补进批次 10 的联调测试」）。

起因是用户问「agv01 能不能一直在焊线键合区收料，三种都能收，最后再依次跑到对应地方卸货」。规则上允许：在途链只看候选需求
自己的类型（InTransitDispatchAdmissionChain、VehicleTaskTypeAdmissionCriterion、WorkTypeScopeCriterion），不同卸货站各自成停靠
（EnRouteAppendCriterion），同站同角色才合并（EnRouteAppendPlanner）。别的场景不覆盖这种跑法，这一条覆盖。

站点、路网与上限的取值和理由在 setup 文件里。第一段（三单混装）：
  1. 甲（WIRE_TO_GATE，AREA N1-3，12 号站）被空闲车受理；车到 12 号站装完，持货等单。
  2. 车停在 12 号站时追加乙（WIRE_TO_OPTICAL，AREA C15-13，11 号站）。车到 11 号站装完乙，持货等单。
  3. 车停在 11 号站时追加丙（WIRE_TO_NITROGEN，AREA N1-7，12 号站）。车回 12 号站装完丙，持货到期离站。
  4. 依次到三个卸货停靠卸货，三单都 Completed。
判据：
  L2-SDMX-01  三单同一趟旅程、任务类型各自保留（受理行的 WorkType、冻结的卸货站）；
  L2-SDMX-02  本趟两次追加都发生在停站窗口内（不证行驶中拒绝）：乙、丙各自的加入时刻，晚于车在那一站装货落定，早于车离开那一站的下一条移动订单；
              加入那一刻旅程停在站上（AwaitingStationDeparture）——一次读出，不分两次；
  L2-SDMX-03  计划里有三个不同的卸货停靠，各是对应那一单的卸货停靠，站号是该单类型绑定的站；
  L2-SDMX-04  三单装进不同仓位、各在自己 AREA 指派的那一组；
  L2-SDMX-05..07  每个卸货站卸的正是对应那一单装上车的那些仓：到站后等到该单的卸货提交，此刻别的单在这一站没有任何
              卸货命令；该单的 LOAD 命令、Load 提交、UNLOAD 命令、Unload 提交四样仓位整串等于它的目标仓（只比卸货一侧
              管不住装错仓，审查 M1）；
  L2-SDMX-08  三单都结清（Completed 之后另等受理行 Succeeded）；
  L2-SDMX-09  分区参数确实被用上：乙、丙的归属行记下的参数版本是 setup 写入的那一版，那一版的上限是 50000。
第二段（负向，en-route-append-delay-gate 已用 WIRE_TO_GATE 证过这一门，这里只证「混装之后第二单被延迟门挡住」）：
  5. 不停服务端，经 FieldOps 正式导入一版新参数，上限 30000。
  6. 丁（WIRE_TO_GATE，N2-5，12 号站）受理、装完、持货；车停在 12 号站时放戊（WIRE_TO_OPTICAL，C15-14，11 号站），
     形状同乙，要 40000 > 30000：
  L2-SDMX-10  戊的追加被拒，积压原因 EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED，没有进丁的旅程（先以丁已受理、车在站上垫底）；
  L2-SDMX-11  丁照常走完到关卡卸货、结清，旅程里只有它一单。

**合成装置证不了「行驶中拒绝追加」。**现场那条规则（用户 09-22 定）靠真车载端在有在途单时报未就绪、在途链以
ONBOARD_FACTS_NOT_READY 拒绝来实现；合成车载端总报安全，服务端不会拒。所以 L2-SDMX-02 证的是「这一趟里的两次追加确实都
发生在车停在站上的时候」，不是「行驶中会被拒」。

**探针不取闭包**，理由见 CargoHoldingCommon.ps1 开头。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SlotGroups.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SingleRow.psm1') -Force
. (Join-Path $PSScriptRoot 'CargoHoldingCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$zone = 'MAP-25-WIRE_TO_GATE'

# 与 setup 的站表、绑定一致。
$bindings = @{ WIRE_TO_GATE = 210; WIRE_TO_OPTICAL = 13; WIRE_TO_NITROGEN = 305 }

function New-MixDemand([string]$Label, [string]$TaskType, [string]$Area, [int]$PickupStation, [string]$SlotPosition) {
    $guid = [guid]::NewGuid()
    return @{
        Label         = $Label
        TaskType      = $TaskType
        Area          = $Area
        PickupStation = $PickupStation
        DropStation   = $bindings[$TaskType]
        SlotPosition  = $SlotPosition
        Wire          = $guid.ToString('N')
        Id            = $guid.ToString('D')
        Sublot        = "L2-SDMX-$Label-$($Context.RunId)"
    }
}

function Publish-MixDemand([hashtable]$Demand) {
    $journal.Note("Publishing demand $($Demand.Label): $($Demand.TaskType) $($Demand.Wire) (AREA $($Demand.Area), station $($Demand.PickupStation)).")
    # 同一个 AREA 的需求共用一台 EQP：一个 AREA 在目录里挂两台 EQP 是 AREA_EQP_NOT_UNIQUE（TaskPriorityCommon.ps1 同一条）。
    $null = $Context.MesIngest.Command('Put', "demands/$($Demand.Wire)", @{
        sublot      = $Demand.Sublot
        workType    = $Demand.TaskType
        area        = $Demand.Area
        eqp         = "EQP-L2-SDMX-$($Demand.Area)"
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

function ConvertTo-Instant([object]$Value) {
    if ($null -eq $Value -or [string]$Value -eq '') { return $null }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture)
}

# 一条需求加入旅程的那一刻：归属行、加入时刻、那一刻旅程的阶段与当前停靠，一次读出（同一次查询，同一个快照）。
function Get-JoinFact([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT d.JourneyId, d.AddedAt, d.Status, d.DispatchZoneParameterVersion, r.Stage, " +
        "(SELECT s.StopId FROM JourneyStops s WHERE s.JourneyId = d.JourneyId AND s.Status NOT IN ('COMPLETED', 'REMOVED') " +
        " ORDER BY s.Sequence LIMIT 1) AS CurrentStopId, " +
        "(SELECT s.StationRiotId FROM JourneyStops s WHERE s.JourneyId = d.JourneyId AND s.Status NOT IN ('COMPLETED', 'REMOVED') " +
        " ORDER BY s.Sequence LIMIT 1) AS CurrentStation " +
        "FROM JourneyDemands d JOIN JourneyRuntimes r ON r.JourneyId = d.JourneyId " +
        "WHERE d.DemandId = '$DemandId' AND d.RemovedAt IS NULL")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 某需求在某站装货落定的时刻（StationOperations 的 Load 提交）。
function Get-LoadCommittedAt([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT CommittedAt FROM StationOperations WHERE DemandId = '$DemandId' AND OperationType = 'Load' AND Status = 'Committed'")
    if ($rows.Count -ne 1) { return $null }
    return ConvertTo-Instant $rows[0].CommittedAt
}

# 这趟旅程里晚于 $After 建的第一条移动订单的建立时刻：车离开它此刻所在那一站的那一条。
function Get-NextDepartureOrderAt([string]$JourneyId, [DateTimeOffset]$After) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT i.CreatedAt FROM OrderIntents i JOIN JourneyStops s ON s.UpperId = i.UpperId WHERE s.JourneyId = '$JourneyId'")
    $later = @($rows | ForEach-Object { ConvertTo-Instant $_.CreatedAt } | Where-Object { $null -ne $_ -and $_ -gt $After } | Sort-Object)
    if ($later.Count -eq 0) { return $null }
    return $later[0]
}

function Get-JourneyStops([string]$JourneyId) {
    return Invoke-L2Query -Connection $connection -Sql (
        "SELECT StopId, Sequence, StopRole, StationRiotId, Status FROM JourneyStops WHERE JourneyId = '$JourneyId' ORDER BY Sequence")
}

function Get-DemandMembership([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, PickupStopId, UnloadStopId, TargetSlotsJson, Status FROM JourneyDemands " +
        "WHERE DemandId = '$DemandId' AND RemovedAt IS NULL")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-UnloadCommandCount([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'SlotOperationCommand'"
    return @($rows | ForEach-Object { ([string]$_.PayloadJson | ConvertFrom-Json).payload } |
        Where-Object { [string]$_.demandId -eq $DemandId -and [string]$_.operationType -ceq 'UNLOAD' }).Count
}

# 某需求的 LOAD 或 UNLOAD 命令下发过的仓位：每条命令一串，去重。
function Get-CommandSlots([string]$DemandId, [string]$OperationType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'SlotOperationCommand'"
    $slots = @($rows | ForEach-Object { ([string]$_.PayloadJson | ConvertFrom-Json).payload } |
        Where-Object { [string]$_.demandId -eq $DemandId -and [string]$_.operationType -ceq $OperationType } |
        ForEach-Object { (@($_.slots | ForEach-Object { [int]$_ }) -join ',') } | Sort-Object -Unique)
    return , $slots
}

function Format-Slots([object]$Json) {
    return (@([string]$Json | ConvertFrom-Json | ForEach-Object { [int]$_ }) -join ',')
}

# 车停在当前停靠的站上、该需求装货已落定、装货阶段进入持货等单（或已满、已关）。
function Wait-LoadedAndHolding([hashtable]$Demand, [string]$Criterion) {
    $loaded = Wait-L2Condition -Description "demand $($Demand.Label) was loaded" -Journal $journal -Criterion "$Criterion-loaded" `
        -TimeoutSeconds 120 `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM JourneyDemands WHERE DemandId = '$($Demand.Id)' AND RemovedAt IS NULL"
            if ($rows.Count -eq 0) { return $null }
            return [string]$rows[0].Status
        } `
        -Until { param($v) $v -eq 'LOADED' }
    return Wait-L2LoadingPhase -Context $Context -DemandId $Demand.Id -States @('CARGO_HOLDING_WAIT', 'VEHICLE_FULL', 'CLOSED') `
        -Criterion "$Criterion-holding" -TimeoutSeconds 60
}

# 追加一条，车停在 $Holder 装货的那一站上：发布、等它加入（一次读出加入时刻与那一刻的旅程阶段、当前停靠）。
function Add-WhileStopped([hashtable]$Demand, [hashtable]$Holder, [string]$JourneyId) {
    Publish-MixDemand $Demand
    $join = Wait-L2ConditionOrLast -Description "demand $($Demand.Label) joined the journey while the vehicle stood at station $($Holder.PickupStation)" `
        -Journal $journal -Criterion "join-$($Demand.Label)" -TimeoutSeconds 60 `
        -Probe { Get-JoinFact $Demand.Id } -Until { param($v) $null -ne $v }
    $journal.Observe("join-$($Demand.Label)",
        $(if ($join) { "$($join.JourneyId) at $($join.AddedAt), stage $($join.Stage), current stop $($join.CurrentStopId)@$($join.CurrentStation)" } else { 'not joined' }),
        @{ join = $join })
    return $join
}

# ====================================================================================================
# 第一段：三单混装
# ====================================================================================================

$a = New-MixDemand 'A' 'WIRE_TO_GATE' 'N1-3' 12 'FRONT'
$b = New-MixDemand 'B' 'WIRE_TO_OPTICAL' 'C15-13' 11 'REAR'
$c = New-MixDemand 'C' 'WIRE_TO_NITROGEN' 'N1-7' 12 'FRONT'
$mix = @($a, $b, $c)

Initialize-L2CargoRig $Context

# --- 1. 甲：空闲车受理，到 12 号站装完、持货 --------------------------------------------------------------

Publish-MixDemand $a
$journey = Wait-L2Condition -Description 'demand A was accepted by the idle vehicle' -Journal $journal -Criterion 'journey-a' `
    -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
$aStop = Get-DemandMembership $a.Id
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId 12 ([string]$aStop.PickupStopId)
$holdA = Wait-LoadedAndHolding $a 'a'
$journal.Note("After A: $(Format-L2CargoJourney $holdA).")

# --- 2. 车停在 12 号站：追加乙；到 11 号站装完、持货 -------------------------------------------------------

$joinB = Add-WhileStopped $b $a $journeyId
if ($null -eq $joinB -or [string]$joinB.JourneyId -ne $journeyId) {
    throw "Demand B did not join journey $journeyId; backlog reason '$((Get-L2CargoBacklog $connection $b.Id).ReasonCode)'. Every later criterion needs it."
}
$bStop = Get-DemandMembership $b.Id
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId 11 ([string]$bStop.PickupStopId)
$holdB = Wait-LoadedAndHolding $b 'b'
$journal.Note("After B: $(Format-L2CargoJourney $holdB).")

# --- 3. 车停在 11 号站：追加丙；回 12 号站装完 ------------------------------------------------------------

$joinC = Add-WhileStopped $c $b $journeyId
if ($null -eq $joinC -or [string]$joinC.JourneyId -ne $journeyId) {
    throw "Demand C did not join journey $journeyId; backlog reason '$((Get-L2CargoBacklog $connection $c.Id).ReasonCode)'. Every later criterion needs it."
}
$cStop = Get-DemandMembership $c.Id
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId 12 ([string]$cStop.PickupStopId)
$holdC = Wait-LoadedAndHolding $c 'c'
$journal.Note("After C: $(Format-L2CargoJourney $holdC).")

# --- 判据：同一趟、类型各自保留 ------------------------------------------------------------------------------

$kept = @($mix | ForEach-Object {
        $id = $_.Id
        $row = (Invoke-L2Query -Connection $connection -Sql @"
SELECT (SELECT WorkType FROM AcceptedDemands WHERE DemandId = '$id') AS WorkType,
       (SELECT StationId FROM FrozenDemandStations WHERE DemandId = '$id' AND Role = 'Dropoff') AS Dropoff,
       (SELECT JourneyId FROM JourneyDemands WHERE DemandId = '$id' AND RemovedAt IS NULL) AS JourneyId
"@)[0]
        "$($_.Label):$($row.WorkType)->$($row.Dropoff)$(if ([string]$row.JourneyId -eq $journeyId) { '' } else { " (journey $($row.JourneyId))" })"
    })
$expectedKept = @($mix | ForEach-Object { "$($_.Label):$($_.TaskType)->$($_.DropStation)" })
$runtimes = [int](Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM JourneyRuntimes')[0].N
$assertions.Add(
    'L2-SDMX-01', '三单在同一趟旅程里，任务类型各自保留：受理行的 WorkType 是各自的类型，冻结的卸货站是各自类型绑定的站；库里只有一条旅程',
    (($kept -join ' ') -ceq ($expectedKept -join ' ') -and $runtimes -eq 1),
    "$($expectedKept -join ' ') / 1 journey", "$($kept -join ' ') / $runtimes journey(s)")

# --- 判据：追加只发生在停站时 --------------------------------------------------------------------------------

$stoppedFacts = foreach ($pair in @(@{ Joined = $b; Join = $joinB; Holder = $a }, @{ Joined = $c; Join = $joinC; Holder = $b })) {
    $loadedAt = Get-LoadCommittedAt $pair.Holder.Id
    $addedAt = ConvertTo-Instant $pair.Join.AddedAt
    $departAt = if ($null -eq $loadedAt) { $null } else { Get-NextDepartureOrderAt $journeyId $loadedAt }
    $holderStop = (Get-DemandMembership $pair.Holder.Id).PickupStopId
    [pscustomobject]@{
        Label = $pair.Joined.Label
        Ok    = ($null -ne $loadedAt -and $null -ne $addedAt -and $null -ne $departAt -and
            $loadedAt -le $addedAt -and $addedAt -lt $departAt -and
            [string]$pair.Join.Stage -eq 'AwaitingStationDeparture' -and
            [string]$pair.Join.CurrentStopId -eq [string]$holderStop -and [int]$pair.Join.CurrentStation -eq $pair.Holder.PickupStation)
        Text  = "$($pair.Joined.Label): $($pair.Holder.Label) loaded $(if ($loadedAt) { $loadedAt.ToString('HH:mm:ss.fff') } else { '-' }) <= " +
            "joined $(if ($addedAt) { $addedAt.ToString('HH:mm:ss.fff') } else { '-' }) < " +
            "next departure order $(if ($departAt) { $departAt.ToString('HH:mm:ss.fff') } else { '-' }); " +
            "stage $($pair.Join.Stage), current stop $($pair.Join.CurrentStopId)@$($pair.Join.CurrentStation)"
    }
}
$stoppedFacts = @($stoppedFacts)
$assertions.Add(
    'L2-SDMX-02', '本趟两次追加都发生在停站窗口内（不证行驶中拒绝）：乙在车停于 12 号站（甲装完之后、离站订单之前）加入，丙在车停于 11 号站（乙装完之后、离站订单之前）加入；加入那一刻旅程停在站上、当前停靠就是那一站',
    (@($stoppedFacts | Where-Object { $_.Ok }).Count -eq 2),
    "B at station 12, C at station 11: loaded <= joined < next departure order; AwaitingStationDeparture",
    (($stoppedFacts | ForEach-Object { $_.Text }) -join ' | '))

# --- 判据：三个不同的卸货停靠 ---------------------------------------------------------------------------------

$stops = Get-JourneyStops $journeyId
$shape = ($stops | ForEach-Object { "$($_.Sequence):$($_.StopRole)@$($_.StationRiotId)" }) -join ' '
$journal.Observe('journey-stops', $shape, @{ stops = $stops })
$unloadStops = @($stops | Where-Object { [string]$_.StopRole -ceq 'UNLOAD' })
$memberships = @{}
foreach ($demand in $mix) { $memberships[$demand.Label] = Get-DemandMembership $demand.Id }
$unloadOwners = @($mix | ForEach-Object {
        $label = $_.Label
        $stop = @($unloadStops | Where-Object { [string]$_.StopId -eq [string]$memberships[$label].UnloadStopId })
        "$label->$(if ($stop.Count -eq 1) { $stop[0].StationRiotId } else { "($($stop.Count) stops)" })"
    })
$assertions.Add(
    'L2-SDMX-03', '计划里有三个不同的卸货停靠，站号各不相同，各是对应那一单的卸货停靠、落在该单类型绑定的站',
    ($unloadStops.Count -eq 3 -and @($unloadStops | ForEach-Object { [int]$_.StationRiotId } | Sort-Object -Unique).Count -eq 3 -and
        ($unloadOwners -join ' ') -ceq (($mix | ForEach-Object { "$($_.Label)->$($_.DropStation)" }) -join ' ')),
    "3 unload stops / A->210 B->13 C->305",
    "$($unloadStops.Count) unload stops ($shape) / $($unloadOwners -join ' ')")

# --- 判据：不同仓位、各在自己的组 ------------------------------------------------------------------------------

$positions = Get-L2VehicleSlotPositions -Connection $connection -AgvId $Context.AgvId
if ($null -eq $positions) { throw "Vehicle $($Context.AgvId) has no resolvable slot model; the preseed did not bind it." }
$slotFacts = @($mix | ForEach-Object {
        $slots = @([string]$memberships[$_.Label].TargetSlotsJson | ConvertFrom-Json | ForEach-Object { [int]$_ })
        $groups = @($slots | ForEach-Object { [string]$positions.Positions[$_] } | Sort-Object -Unique)
        [pscustomobject]@{ Label = $_.Label; Slots = $slots; Ok = ($slots.Count -eq 1 -and ($groups -join ',') -ceq $_.SlotPosition); Groups = $groups }
    })
$allSlots = @($slotFacts | ForEach-Object { $_.Slots })
$assertions.Add(
    'L2-SDMX-04', '三单装进不同仓位，各在自己 AREA 指派的那一组（甲 N1-3、丙 N1-7 指 FRONT，乙 C15-13 指 REAR）',
    (@($slotFacts | Where-Object { $_.Ok }).Count -eq 3 -and @($allSlots | Sort-Object -Unique).Count -eq 3),
    'A FRONT, B REAR, C FRONT; three distinct slots',
    (($slotFacts | ForEach-Object { "$($_.Label) [$($_.Slots -join ',')] $($_.Groups -join ',')" }) -join '; '))

# --- 4. 依次到三个卸货停靠，每站卸的正是对应那一单 -------------------------------------------------------------

$byUnloadStop = @{}
foreach ($demand in $mix) { $byUnloadStop[[string]$memberships[$demand.Label].UnloadStopId] = $demand }
$unloadIds = @('L2-SDMX-05', 'L2-SDMX-06', 'L2-SDMX-07')
$index = 0
foreach ($stop in $unloadStops) {
    $owner = $byUnloadStop[[string]$stop.StopId]
    $others = @($mix | Where-Object { $_.Label -ne $owner.Label })
    $before = @{}
    foreach ($other in $others) { $before[$other.Label] = Get-UnloadCommandCount $other.Id }
    $null = Move-L2CargoVehicleToCurrentStop $Context $journeyId ([int]$stop.StationRiotId) ([string]$stop.StopId)
    $unload = Wait-L2ConditionOrLast -Description "demand $($owner.Label)'s unload committed at station $($stop.StationRiotId)" `
        -Journal $journal -Criterion "unload-$($owner.Label)" -TimeoutSeconds 120 `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection -Sql (
                "SELECT TargetSlotsJson FROM StationOperations WHERE DemandId = '$($owner.Id)' AND OperationType = 'Unload' AND Status = 'Committed'")
            if ($rows.Count -eq 0) { return $null }
            return $rows[0]
        } `
        -Until { param($v) $null -ne $v }
    $sentHere = @($others | ForEach-Object { "$($_.Label)+$((Get-UnloadCommandCount $_.Id) - $before[$_.Label])" })
    $target = Format-Slots $memberships[$owner.Label].TargetSlotsJson
    # 卸货命令、卸货提交与归属行的目标仓同出一源（卸货命令直接取 TargetSlotsJson），只比这三样管不住「装错了仓」：
    # 把乙、丙装进甲的仓、再到各自的站去卸空着的目标仓，三样照样相等（审查 M1，变异 M2）。所以装货一侧也要比——
    # 这一单的 LOAD 命令与 Load 那条站点操作的仓位——四样整串相等，才算这一站卸的正是装上车的那些仓。
    $loadOp = Read-L2SingleRow -Connection $connection -Required -Sql (
        "SELECT TargetSlotsJson FROM StationOperations WHERE DemandId = '$($owner.Id)' AND OperationType = 'Load' AND Status = 'Committed'")
    $sent = @()
    foreach ($type in 'LOAD', 'UNLOAD') {
        $slots = Get-CommandSlots $owner.Id $type
        $sent += "$type " + $(if ($slots.Count -eq 0) { '(none)' } else { ($slots | ForEach-Object { "[$_]" }) -join ' ' })
    }
    $sent += "Load op [$(Format-Slots $loadOp.TargetSlotsJson)]"
    $sent += "Unload op $(if ($unload) { "[$(Format-Slots $unload.TargetSlotsJson)]" } else { '(not committed)' })"
    $expectedSent = "LOAD [$target]; UNLOAD [$target]; Load op [$target]; Unload op [$target]"
    $assertions.Add(
        $unloadIds[$index], "卸货站 $($stop.StationRiotId)（第 $($index + 1) 个卸货停靠）卸的正是 $($owner.Label)（$($owner.TaskType)）装上车的那些仓：LOAD 命令、Load 提交、UNLOAD 命令、Unload 提交的仓位都等于它的目标仓，别的单在这一站没有新的卸货命令",
        (($sent -join '; ') -ceq $expectedSent -and @($sentHere | Where-Object { $_ -notlike '*+0' }).Count -eq 0),
        "$expectedSent; others +0",
        "$($sent -join '; '); $($sentHere -join ' ')")
    $index++
}

# --- 判据：结清 ------------------------------------------------------------------------------------------

$settled = Wait-L2ConditionOrLast -Description 'the journey completed and all three demands were settled' `
    -Journal $journal -Criterion 'mix-settled' -TimeoutSeconds 120 `
    -Probe {
        $ids = ($mix | ForEach-Object { "'$($_.Id)'" }) -join ','
        $rows = Invoke-L2Query -Connection $connection -Sql @"
SELECT (SELECT Stage FROM JourneyRuntimes WHERE JourneyId = '$journeyId') AS Stage,
       (SELECT COUNT(*) FROM AcceptedDemands WHERE DemandId IN ($ids) AND Status = 'Succeeded') AS Succeeded
"@
        $rows[0]
    } `
    -Until { param($v) [string]$v.Stage -eq 'Completed' -and [int]$v.Succeeded -eq 3 }
$assertions.Add(
    'L2-SDMX-08', '旅程 Completed，三单的受理行都是 Succeeded',
    ([string]$settled.Stage -eq 'Completed' -and [int]$settled.Succeeded -eq 3),
    'Completed / 3 Succeeded', "$($settled.Stage) / $($settled.Succeeded) Succeeded")

$presetVersion = [long](Invoke-L2Query -Connection $connection -Sql 'SELECT MAX(Version) AS V FROM DispatchZoneParameterVersions')[0].V
$presetAllowance = Read-L2SingleRow -Connection $connection -Required -Sql (
    "SELECT EnRouteAdditionMaxPathCostIncrease AS Allowance FROM DispatchZoneParameters WHERE Version = $presetVersion AND DispatchZone = '$zone'")
$appendVersions = @($joinB, $joinC | ForEach-Object { [string]$_.DispatchZoneParameterVersion })
$assertions.Add(
    'L2-SDMX-09', "两次追加记下的每区参数版本都是 setup 写入的那一版，那一版里 $zone 的上限是 50000",
    (($appendVersions -join ',') -eq "$presetVersion,$presetVersion" -and [string]$presetAllowance.Allowance -eq '50000'),
    "$presetVersion,$presetVersion / 50000", "$($appendVersions -join ',') / $($presetAllowance.Allowance)")

# ====================================================================================================
# 第二段（负向）：上限改成 30000，混装之后的第二单被延迟门挡住
# ====================================================================================================

$csv = Join-Path $Context.SnapshotRoot 'dispatch-zone-parameters-30000.csv'
[IO.File]::WriteAllText($csv, "dispatch_zone,en_route_addition_max_path_cost_increase_mm,starvation_threshold_seconds`n$zone,30000,`n",
    [Text.UTF8Encoding]::new($false))
$imported = & $Context.InvokeFieldOps -Arguments @('import-dispatch-zone-parameters', '--input', $csv)
$current = Invoke-L2Query -Connection $connection -Sql (
    "SELECT p.EnRouteAdditionMaxPathCostIncrease AS Allowance FROM DispatchZoneParameters p " +
    "WHERE p.DispatchZone = '$zone' AND p.Version = (SELECT MAX(Version) FROM DispatchZoneParameterVersions)")
$journal.Observe('dispatch-zone-parameters-import', [string]$imported.outcome, @{ output = $imported })
if ([string]$imported.outcome -ne 'OK' -or $current.Count -ne 1 -or [string]$current[0].Allowance -ne '30000') {
    throw "The 30000 allowance did not take effect (import $($imported.outcome), current '$(if ($current.Count -gt 0) { $current[0].Allowance })'); the negative half needs it."
}

$d = New-MixDemand 'D' 'WIRE_TO_GATE' 'N2-5' 12 'FRONT'
$e = New-MixDemand 'E' 'WIRE_TO_OPTICAL' 'C15-14' 11 'REAR'

Publish-MixDemand $d
$dJourney = Wait-L2Condition -Description 'demand D was accepted by the idle vehicle' -Journal $journal -Criterion 'journey-d' `
    -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $d.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$dJourneyId = [string]$dJourney.JourneyId
$dStop = Get-DemandMembership $d.Id
$null = Move-L2CargoVehicleToCurrentStop $Context $dJourneyId 12 ([string]$dStop.PickupStopId)
$holdD = Wait-LoadedAndHolding $d 'd'
$journal.Note("After D: $(Format-L2CargoJourney $holdD).")

# 垫底：丁已受理、车停在 12 号站持货（上面两次等待）。戊放进来之后等它的积压原因落到延迟门。
Publish-MixDemand $e
$eFact = Wait-L2ConditionOrLast -Description 'demand E was refused by the delay gate' -Journal $journal -Criterion 'e-reason' `
    -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql @"
SELECT (SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$($e.Id)') AS ReasonCode,
       (SELECT AcceptedAt FROM JourneyBacklog WHERE DemandId = '$($e.Id)') AS AcceptedAt,
       (SELECT COUNT(*) FROM JourneyDemands WHERE DemandId = '$($e.Id)') AS Memberships,
       (SELECT Stage FROM JourneyRuntimes WHERE JourneyId = '$dJourneyId') AS DStage
"@
        $rows[0]
    } `
    -Until { param($v) [string]$v.ReasonCode -ceq 'EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED' }
$assertions.Add(
    'L2-SDMX-10', '上限 30000 时，车停在 12 号站载着丁，戊（同乙的形状，要 40000）的追加被拒：积压原因 EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED，没有受理、没进丁的旅程',
    ([string]$eFact.ReasonCode -ceq 'EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED' -and [string]::IsNullOrEmpty([string]$eFact.AcceptedAt) -and
        [int]$eFact.Memberships -eq 0 -and [string]$eFact.DStage -eq 'AwaitingStationDeparture'),
    'EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED / not accepted / 0 memberships / D at station',
    "$($eFact.ReasonCode) / $(if ([string]::IsNullOrEmpty([string]$eFact.AcceptedAt)) { 'not accepted' } else { 'ACCEPTED' }) / " +
    "$($eFact.Memberships) memberships / D $($eFact.DStage)")

# 丁持货到期离站，到关卡卸货、结清；旅程里只有它一单。
$dDrop = (Get-DemandMembership $d.Id).UnloadStopId
$null = Move-L2CargoVehicleToCurrentStop $Context $dJourneyId 210 ([string]$dDrop)
$dSettled = Wait-L2ConditionOrLast -Description 'demand D completed at the gate and was settled' -Journal $journal -Criterion 'd-settled' `
    -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql @"
SELECT (SELECT Stage FROM JourneyRuntimes WHERE JourneyId = '$dJourneyId') AS Stage,
       (SELECT Status FROM AcceptedDemands WHERE DemandId = '$($d.Id)') AS DemandStatus,
       (SELECT COUNT(*) FROM JourneyDemands WHERE JourneyId = '$dJourneyId') AS Members
"@
        $rows[0]
    } `
    -Until { param($v) [string]$v.Stage -eq 'Completed' -and [string]$v.DemandStatus -eq 'Succeeded' }
$assertions.Add(
    'L2-SDMX-11', '丁照常到关卡卸货、结清，它那一趟旅程里只有它一单',
    ([string]$dSettled.Stage -eq 'Completed' -and [string]$dSettled.DemandStatus -eq 'Succeeded' -and [int]$dSettled.Members -eq 1),
    'Completed / Succeeded / 1 member', "$($dSettled.Stage) / $($dSettled.DemandStatus) / $($dSettled.Members) member(s)")

$journal.Note('一车混装三类：停站时两次追加、三个终点依次卸货、三单结清；上限改小后混装之后的第二单被延迟门挡住，原有的单照常完成。')
