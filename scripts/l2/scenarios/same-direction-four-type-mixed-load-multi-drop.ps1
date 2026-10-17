#Requires -Version 7

<#
一车混装 agv01 的四类——WIRE_TO_GATE、WIRE_TO_OPTICAL、WIRE_TO_NITROGEN、WIRE_TO_FULL_INSPECTION——停站时途中追加，依次到四个终点
卸货（control-server#737；program#195 决定 1、5：agv01 跑四类，不互斥，一趟可以混装）。

另开一个场景，不在 same-direction-mixed-load-multi-drop 上加第四单：那个场景的第二段（延迟门负向）按它的路网手算了 30000 这个
门限，第四个卸货站会改动它的计划代价；那一段证的事（混装之后第二单被延迟门挡住）与第七类无关，留在原处不动。本场景只做第一段
的四类版本，把第七类放在最后、用「车在最后一个装货站持货时同站又来一条」这种跑法加进来。

站点、路网与上限的取值和理由在 setup 文件里，406「全检区」是模拟站点。步骤：
  1. 甲（WIRE_TO_GATE，AREA N1-3，12 号站）被空闲车受理；车到 12 号站装完，持货等单。
  2. 车停在 12 号站时追加乙（WIRE_TO_OPTICAL，AREA C15-13，11 号站）。车到 11 号站装完乙，持货等单。
  3. 车停在 11 号站时追加丙（WIRE_TO_NITROGEN，AREA N1-7，12 号站）。车回 12 号站装完丙，持货等单。
  4. 车停在 12 号站时追加丁（WIRE_TO_FULL_INSPECTION，AREA N2-5，12 号站）：同站的新停靠，车不动、逻辑上第二次到站，装完丁，
     持货到期离站。
  5. 依次到四个卸货停靠卸货，四单都 Completed。
判据：
  L2-SDM4-01  四单同一趟旅程、任务类型各自保留（受理行的 WorkType、冻结的卸货站）；库里只有一条旅程；
  L2-SDM4-02  三次追加都发生在停站窗口内（不证行驶中拒绝）：加入时刻晚于车在那一站装货落定、早于车离开那一站的下一条移动
              订单；加入那一刻旅程停在站上（AwaitingStationDeparture），当前停靠就是那一站——一次读出，不分两次；
  L2-SDM4-03  计划里有四个不同的卸货停靠，各是对应那一单的卸货停靠、站号是该单类型绑定的站；丁的取货是 12 号站上排在丙之后的
              另一个停靠（当前停靠不并，REQ-0196）；
  L2-SDM4-04  四单装进不同仓位、各在自己 AREA 指派的那一组；
  L2-SDM4-05..08  每个卸货站卸的正是对应那一单装上车的那些仓（四样仓位整串等于目标仓，别的单在这一站没有新的卸货命令）；
  L2-SDM4-09  四单都结清：旅程 Completed 之后另等受理行 Succeeded（README 第 14 条，Wait-L2ConditionOrLast）；
  L2-SDM4-10  三次追加记下的每区参数版本都是 setup 写入的那一版，上限 50000。

本类进 ExecutableTaskTypes 之前，第 4 步丁不会加入，场景在那里以丁的积压原因停下——那就是本场景的红证据（见 PR）。

**合成装置证不了「行驶中拒绝追加」**，理由同 same-direction-mixed-load-multi-drop。

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
$bindings = @{ WIRE_TO_GATE = 210; WIRE_TO_OPTICAL = 13; WIRE_TO_NITROGEN = 305; WIRE_TO_FULL_INSPECTION = 406 }

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
        Sublot        = "L2-SDM4-$Label-$($Context.RunId)"
    }
}

function Publish-MixDemand([hashtable]$Demand) {
    $journal.Note("Publishing demand $($Demand.Label): $($Demand.TaskType) $($Demand.Wire) (AREA $($Demand.Area), station $($Demand.PickupStation)).")
    # 同一个 AREA 的需求共用一台 EQP：一个 AREA 在目录里挂两台 EQP 是 AREA_EQP_NOT_UNIQUE（TaskPriorityCommon.ps1 同一条）。
    $null = $Context.MesIngest.Command('Put', "demands/$($Demand.Wire)", @{
        sublot      = $Demand.Sublot
        workType    = $Demand.TaskType
        area        = $Demand.Area
        eqp         = "EQP-L2-SDM4-$($Demand.Area)"
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
    $null = Wait-L2Condition -Description "demand $($Demand.Label) was loaded" -Journal $journal -Criterion "$Criterion-loaded" `
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
function Add-WhileStopped([hashtable]$Demand, [hashtable]$Holder) {
    Publish-MixDemand $Demand
    $join = Wait-L2ConditionOrLast -Description "demand $($Demand.Label) joined the journey while the vehicle stood at station $($Holder.PickupStation)" `
        -Journal $journal -Criterion "join-$($Demand.Label)" -TimeoutSeconds 60 `
        -Probe { Get-JoinFact $Demand.Id } -Until { param($v) $null -ne $v }
    $journal.Observe("join-$($Demand.Label)",
        $(if ($join) { "$($join.JourneyId) at $($join.AddedAt), stage $($join.Stage), current stop $($join.CurrentStopId)@$($join.CurrentStation)" } else { 'not joined' }),
        @{ join = $join })
    return $join
}

function Assert-Joined([hashtable]$Demand, [object]$Join, [string]$JourneyId) {
    if ($null -eq $Join -or [string]$Join.JourneyId -ne $JourneyId) {
        throw "Demand $($Demand.Label) ($($Demand.TaskType)) did not join journey $JourneyId; backlog reason '$((Get-L2CargoBacklog $connection $Demand.Id).ReasonCode)'. Every later criterion needs it."
    }
}

$a = New-MixDemand 'A' 'WIRE_TO_GATE' 'N1-3' 12 'FRONT'
$b = New-MixDemand 'B' 'WIRE_TO_OPTICAL' 'C15-13' 11 'REAR'
$c = New-MixDemand 'C' 'WIRE_TO_NITROGEN' 'N1-7' 12 'FRONT'
$d = New-MixDemand 'D' 'WIRE_TO_FULL_INSPECTION' 'N2-5' 12 'FRONT'
$mix = @($a, $b, $c, $d)

Initialize-L2CargoRig $Context

# --- 1. 甲：空闲车受理，到 12 号站装完、持货 --------------------------------------------------------------

Publish-MixDemand $a
$journey = Wait-L2Condition -Description 'demand A was accepted by the idle vehicle' -Journal $journal -Criterion 'journey-a' `
    -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId 12 ([string](Get-DemandMembership $a.Id).PickupStopId)
$holdA = Wait-LoadedAndHolding $a 'a'
$journal.Note("After A: $(Format-L2CargoJourney $holdA).")

# --- 2. 车停在 12 号站：追加乙；到 11 号站装完、持货 -------------------------------------------------------

$joinB = Add-WhileStopped $b $a
Assert-Joined $b $joinB $journeyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId 11 ([string](Get-DemandMembership $b.Id).PickupStopId)
$holdB = Wait-LoadedAndHolding $b 'b'
$journal.Note("After B: $(Format-L2CargoJourney $holdB).")

# --- 3. 车停在 11 号站：追加丙；回 12 号站装完、持货 -------------------------------------------------------

$joinC = Add-WhileStopped $c $b
Assert-Joined $c $joinC $journeyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId 12 ([string](Get-DemandMembership $c.Id).PickupStopId)
$holdC = Wait-LoadedAndHolding $c 'c'
$journal.Note("After C: $(Format-L2CargoJourney $holdC).")

# --- 4. 车停在 12 号站：追加丁（第七类）；同站的新停靠，车不动、逻辑上第二次到站，装完 -------------------------------

$joinD = Add-WhileStopped $d $c
Assert-Joined $d $joinD $journeyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId 12 ([string](Get-DemandMembership $d.Id).PickupStopId)
$holdD = Wait-LoadedAndHolding $d 'd'
$journal.Note("After D: $(Format-L2CargoJourney $holdD).")

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
    'L2-SDM4-01', '四单在同一趟旅程里，任务类型各自保留：受理行的 WorkType 是各自的类型，冻结的卸货站是各自类型绑定的站；库里只有一条旅程',
    (($kept -join ' ') -ceq ($expectedKept -join ' ') -and $runtimes -eq 1),
    "$($expectedKept -join ' ') / 1 journey", "$($kept -join ' ') / $runtimes journey(s)")

# --- 判据：追加只发生在停站时 --------------------------------------------------------------------------------

$stoppedFacts = foreach ($pair in @(
        @{ Joined = $b; Join = $joinB; Holder = $a }
        @{ Joined = $c; Join = $joinC; Holder = $b }
        @{ Joined = $d; Join = $joinD; Holder = $c })) {
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
    'L2-SDM4-02', '本趟三次追加都发生在停站窗口内（不证行驶中拒绝）：乙在车停于 12 号站、丙在车停于 11 号站、丁在车再停于 12 号站时加入，各自晚于前一单装完、早于离站订单；加入那一刻旅程停在站上、当前停靠就是那一站',
    (@($stoppedFacts | Where-Object { $_.Ok }).Count -eq 3),
    'B at station 12, C at station 11, D at station 12: loaded <= joined < next departure order; AwaitingStationDeparture',
    (($stoppedFacts | ForEach-Object { $_.Text }) -join ' | '))

# --- 判据：四个不同的卸货停靠；丁的取货是同站的另一个停靠 ---------------------------------------------------------

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
$cPickup = @($stops | Where-Object { [string]$_.StopId -eq [string]$memberships['C'].PickupStopId })
$dPickup = @($stops | Where-Object { [string]$_.StopId -eq [string]$memberships['D'].PickupStopId })
$dAfterC = $cPickup.Count -eq 1 -and $dPickup.Count -eq 1 -and [string]$cPickup[0].StopId -ne [string]$dPickup[0].StopId -and
    [int]$dPickup[0].StationRiotId -eq 12 -and [int]$dPickup[0].Sequence -gt [int]$cPickup[0].Sequence
$assertions.Add(
    'L2-SDM4-03', '计划里有四个不同的卸货停靠，站号各不相同，各是对应那一单的卸货停靠、落在该单类型绑定的站；丁的取货是 12 号站上排在丙之后的另一个停靠',
    ($unloadStops.Count -eq 4 -and @($unloadStops | ForEach-Object { [int]$_.StationRiotId } | Sort-Object -Unique).Count -eq 4 -and
        ($unloadOwners -join ' ') -ceq (($mix | ForEach-Object { "$($_.Label)->$($_.DropStation)" }) -join ' ') -and $dAfterC),
    '4 unload stops / A->210 B->13 C->305 D->406 / D picks up at its own stop on 12 after C',
    "$($unloadStops.Count) unload stops ($shape) / $($unloadOwners -join ' ') / D after C: $dAfterC")

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
    'L2-SDM4-04', '四单装进不同仓位，各在自己 AREA 指派的那一组（甲 N1-3、丙 N1-7、丁 N2-5 指 FRONT，乙 C15-13 指 REAR）',
    (@($slotFacts | Where-Object { $_.Ok }).Count -eq 4 -and @($allSlots | Sort-Object -Unique).Count -eq 4),
    'A FRONT, B REAR, C FRONT, D FRONT; four distinct slots',
    (($slotFacts | ForEach-Object { "$($_.Label) [$($_.Slots -join ',')] $($_.Groups -join ',')" }) -join '; '))

# --- 5. 依次到四个卸货停靠，每站卸的正是对应那一单 -------------------------------------------------------------

$byUnloadStop = @{}
foreach ($demand in $mix) { $byUnloadStop[[string]$memberships[$demand.Label].UnloadStopId] = $demand }
$unloadIds = @('L2-SDM4-05', 'L2-SDM4-06', 'L2-SDM4-07', 'L2-SDM4-08')
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
    # 装货一侧也比，理由同 same-direction-mixed-load-multi-drop（审查 M1）：只比卸货一侧管不住装错仓。
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

# --- 判据：结清（Completed 之后另等受理行 Succeeded，第二个事实） -------------------------------------------------

$settled = Wait-L2ConditionOrLast -Description 'the journey completed and all four demands were settled' `
    -Journal $journal -Criterion 'mix-settled' -TimeoutSeconds 120 `
    -Probe {
        $ids = ($mix | ForEach-Object { "'$($_.Id)'" }) -join ','
        $rows = Invoke-L2Query -Connection $connection -Sql @"
SELECT (SELECT Stage FROM JourneyRuntimes WHERE JourneyId = '$journeyId') AS Stage,
       (SELECT COUNT(*) FROM AcceptedDemands WHERE DemandId IN ($ids) AND Status = 'Succeeded') AS Succeeded
"@
        $rows[0]
    } `
    -Until { param($v) [string]$v.Stage -eq 'Completed' -and [int]$v.Succeeded -eq 4 }
$assertions.Add(
    'L2-SDM4-09', '旅程 Completed，四单的受理行都是 Succeeded',
    ([string]$settled.Stage -eq 'Completed' -and [int]$settled.Succeeded -eq 4),
    'Completed / 4 Succeeded', "$($settled.Stage) / $($settled.Succeeded) Succeeded")

$presetVersion = [long](Invoke-L2Query -Connection $connection -Sql 'SELECT MAX(Version) AS V FROM DispatchZoneParameterVersions')[0].V
$presetAllowance = Read-L2SingleRow -Connection $connection -Required -Sql (
    "SELECT EnRouteAdditionMaxPathCostIncrease AS Allowance FROM DispatchZoneParameters WHERE Version = $presetVersion AND DispatchZone = '$zone'")
$appendVersions = @($joinB, $joinC, $joinD | ForEach-Object { [string]$_.DispatchZoneParameterVersion })
$assertions.Add(
    'L2-SDM4-10', "三次追加记下的每区参数版本都是 setup 写入的那一版，那一版里 $zone 的上限是 50000",
    (($appendVersions -join ',') -eq "$presetVersion,$presetVersion,$presetVersion" -and [string]$presetAllowance.Allowance -eq '50000'),
    "$presetVersion,$presetVersion,$presetVersion / 50000", "$($appendVersions -join ',') / $($presetAllowance.Allowance)")

$journal.Note('一车混装四类：停站时三次追加（第七类在最后一个装货站持货时同站加入）、四个终点依次卸货、四单结清。')
