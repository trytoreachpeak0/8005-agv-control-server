#Requires -Version 7

<#
VEHICLE_FULL 在离开最后一个装货停靠之前仍接追加（批次7-07，control-server#212；ADR-cross-0057、ADR-cross-0059）。

「满」是按上一轮候选判出来的，不是一道门：它说的是「现在等着的单都装不进来」，不是「什么都不许再进来」。车还没离开
最后一个装货站，一条放得下的新单出现了，照样该追加——关门的是离站（CLOSED），不是满。

形状与 cargo-holding-side-full 相同，只是在车判满之后、离站之前多发一条放得下的单：
1. 需求甲 FRONT 4 花篮（12 号站），需求乙 REAR 2 花篮（11 号站，车去第一站的路上追加进来）。两站都装完，FRONT 无空仓，
   REAR 剩两个空仓，车在 11 号站上持货等单。
2. 需求丁 REAR 3 花篮：只因本车货物占着 REAR 而装不下 → VEHICLE_FULL。
3. 需求戊 REAR 2 花篮：放得下。它追加进这趟旅程，而此刻车仍是 VEHICLE_FULL、仍停在 11 号站、站点等待还没结束。
   加入之后再转几轮，车仍是 VEHICLE_FULL（戊把 REAR 剩下的两个仓预留满了）。
4. 车开到戊的停靠（同在 11 号站、另开的一个停靠）把戊装上，离开时以 CLOSED/VEHICLE_FULL 关闭；判满之后发给车的快照里
   不再出现 WAIT 或 LOADING。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
. (Join-Path $PSScriptRoot 'CargoHoldingCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection

$a = New-L2CargoDemand 'A' 'N1-3' 4 $Context.RunId
$b = New-L2CargoDemand 'B' 'C15-13' 2 $Context.RunId
$d = New-L2CargoDemand 'D' 'C15-13' 3 $Context.RunId
$e = New-L2CargoDemand 'E' 'C15-13' 2 $Context.RunId

Initialize-L2CargoRig $Context

# --- 1. 甲、乙两站装完，车在 11 号站持货等单 --------------------------------------------------------------

Publish-L2CargoDemand $Context $a
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
Publish-L2CargoDemand $Context $b
$null = Wait-L2Condition -Description 'demand B joined the journey' -Journal $journal -Criterion 'b-joined' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $b.Id } -Until { param($v) $null -ne $v }

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $Context.PickupStationRiotId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId 11
$waiting = Wait-L2ConditionOrLast -Description 'both pickups loaded and the vehicle is holding at station 11' -Journal $journal `
    -Criterion 'phase-wait' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql (
            "SELECT r.Stage, r.LoadingPhaseState, r.LoadingClosedReason, r.FullSlotPositionsJson, d.Status AS DemandStatus " +
            "FROM JourneyRuntimes r JOIN JourneyDemands d ON d.JourneyId = r.JourneyId " +
            "WHERE r.JourneyId = '$journeyId' AND d.DemandId = '$($b.Id)'")
        if ($rows.Count -eq 0) { return $null }
        return $rows[0]
    } `
    -Until { param($v) $null -ne $v -and [string]$v.DemandStatus -eq 'LOADED' -and [string]$v.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' }
$assertions.Add(
    'L2-VFA-01', '甲、乙两站装完：FRONT 无空仓、REAR 剩两个，车在 11 号站持货等单',
    ($null -ne $waiting -and [string]$waiting.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and
        [string]$waiting.FullSlotPositionsJson -ceq '["FRONT"]'),
    'CARGO_HOLDING_WAIT, ["FRONT"]',
    $(if ($null -eq $waiting) { '(no row)' } else { "$($waiting.Stage) $($waiting.LoadingPhaseState) ($($waiting.DemandStatus)), $($waiting.FullSlotPositionsJson)" }))

# --- 2. 需求丁：只因本车货物装不下 → VEHICLE_FULL -----------------------------------------------------------

Publish-L2CargoDemand $Context $d
$full = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('VEHICLE_FULL', 'CLOSED') -Criterion 'phase-full' -TimeoutSeconds 60
$assertions.Add(
    'L2-VFA-02', '需求丁（REAR 3 花篮）只因本车货物装不下：车进入 VEHICLE_FULL，仍停在 11 号站',
    ([string]$full.LoadingPhaseState -eq 'VEHICLE_FULL' -and [string]$full.Stage -eq 'AwaitingStationDeparture'),
    'VEHICLE_FULL, AwaitingStationDeparture', (Format-L2CargoJourney $full))

# --- 3. 需求戊：放得下，满了也照样追加 -----------------------------------------------------------------------

# 从判满到戊加入，每一次读到的装货阶段都记下来，必须全是 VEHICLE_FULL。只断「戊进来了」是不够的：一个「满了就不接追加」
# 的实现会让丁在下一轮被判成 LOADING_PHASE_CLOSED 而不再是「本车货物占侧」，REAR 随之不算满，车退回 CARGO_HOLDING_WAIT，
# 戊就在退回的那一轮进来了——结果一样，但车是在「不满」的时候接的。第一版这条场景看不出这种翻转，红证据 red-6a 就是它。
$phasesUntilJoined = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
Publish-L2CargoDemand $Context $e
$joined = Wait-L2ConditionOrLast -Description 'demand E joined the full vehicle' -Journal $journal -Criterion 'e-joined' `
    -TimeoutSeconds 60 `
    -Probe {
        $row = Get-L2CargoJourney $connection $a.Id
        if ($null -ne $row) { $null = $phasesUntilJoined.Add((Format-L2CargoJourney $row).Split(' ')[1]) }
        Get-L2CargoJourney $connection $e.Id
    } `
    -Until { param($v) $null -ne $v }
# 追加那一刻车还没离站：11 号站那个停靠还开着。停靠状态只在离站那一次保存里改，读它比读阶段更直接。
$stationStop = Invoke-L2Query -Connection $connection -Sql (
    "SELECT s.Status FROM JourneyStops s JOIN JourneyDemands d ON d.PickupStopId = s.StopId " +
    "WHERE d.DemandId = '$($b.Id)'")
$assertions.Add(
    'L2-VFA-03', '需求戊（REAR 2 花篮）追加进了这趟旅程：VEHICLE_FULL 在离开最后一个装货站之前仍接追加；追加时 11 号站的停靠还没完成',
    ($null -ne $joined -and [string]$joined.JourneyId -eq $journeyId -and
        $stationStop.Count -eq 1 -and [string]$stationStop[0].Status -notin @('COMPLETED', 'REMOVED')),
    "joined $journeyId / station-11 stop still open",
    "$(if ($joined) { "joined $($joined.JourneyId) ($(Format-L2CargoJourney $joined))" } else { 'not joined' }) / " +
    "$(if ($stationStop.Count -eq 0) { '(no stop)' } else { $stationStop[0].Status })")

$backlogE = Get-L2CargoBacklog $connection $e.Id
$assertions.Add(
    'L2-VFA-04', '需求戊是被受理的：积压行上有受理时刻',
    ($null -ne $backlogE -and $null -ne $backlogE.AcceptedAt -and [string]$backlogE.AcceptedAt -ne ''),
    'accepted', $(if ($null -eq $backlogE) { '(no backlog row)' } else { "$($backlogE.ReasonCode) accepted=$($backlogE.AcceptedAt)" }))

# 加入之后再转几轮才读：加入与重判不在同一次保存里，加入那一刻读到的还是加入之前的判定（审查 M3——第一版在这里读，
# 「有待装就先判 LOADING」的错误实现照样绿）。戊加入后 REAR 已被它预留满，两侧都满，车必须仍是 VEHICLE_FULL。
$null = Wait-L2Iterations -Riot $Context.Riot -Count 3 -Journal $journal
$fullAfterJoin = Get-L2CargoJourney $connection $a.Id
$assertions.Add(
    'L2-VFA-05', '从判满到需求戊加入、再到加入之后又转了三轮：装货阶段一直是 VEHICLE_FULL，没有退回持货等单或装货',
    (($phasesUntilJoined -join ',') -ceq 'VEHICLE_FULL' -and [string]$fullAfterJoin.LoadingPhaseState -eq 'VEHICLE_FULL'),
    'only VEHICLE_FULL', "seen $($phasesUntilJoined -join ','); three rounds after join $(Format-L2CargoJourney $fullAfterJoin)")

# --- 4. 戊装上车：满了接进来的单照样装（票面判据 ⑥「装满后到离开最后装货停靠前仍接能装入的候选」的「装入」） -------------

# 戊的取货停靠与乙同在 11 号站，是另开的一个停靠（当前停靠不并），所以按停靠等，不按站等。
$eStop = Invoke-L2Query -Connection $connection -Sql "SELECT PickupStopId FROM JourneyDemands WHERE DemandId = '$($e.Id)'"
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId 11 ([string]$eStop[0].PickupStopId)
$loadedE = Wait-L2ConditionOrLast -Description 'demand E was loaded' -Journal $journal -Criterion 'e-loaded' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM JourneyDemands WHERE DemandId = '$($e.Id)'"
        if ($rows.Count -eq 0) { return $null }
        return [string]$rows[0].Status
    } `
    -Until { param($v) $v -eq 'LOADED' }
$closed = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('CLOSED') -Criterion 'phase-closed' -TimeoutSeconds 180
$assertions.Add(
    'L2-VFA-07', '需求戊装上了车，车离开它那个取货停靠（最后一个装货停靠）时以 VEHICLE_FULL 关闭',
    ($loadedE -eq 'LOADED' -and [string]$closed.LoadingPhaseState -eq 'CLOSED' -and [string]$closed.LoadingClosedReason -eq 'VEHICLE_FULL'),
    'E LOADED / CLOSED/VEHICLE_FULL', "E $loadedE / $(Format-L2CargoJourney $closed)")

$snapshots = Get-L2LoadingPhaseSnapshots $connection
$journal.Observe('loading-phase-snapshots', (Format-L2LoadingPhaseSnapshots $snapshots), @{ snapshots = $snapshots })
# 车那一侧看到的也一样：第一张 FULL 之后没有任何一张 WAIT 或 LOADING，直到关闭。
$firstFull = @($snapshots | Where-Object { $_.State -eq 'VEHICLE_FULL' } | Select-Object -First 1)
# 整个 if 包进 @()：if 语句把空数组交给赋值时会展开成 $null，严格模式下取 .Count 就抛（第一次正式跑就栽在这里，
# 红证据那次列表非空所以没撞上）。
$reopenedAfterFull = @(if ($firstFull.Count -gt 0) {
    $snapshots | Where-Object { $_.Revision -gt $firstFull[0].Revision -and $_.State -in @('CARGO_HOLDING_WAIT', 'LOADING') } })
$assertions.Add(
    'L2-VFA-06', '发给车的快照在第一张 VEHICLE_FULL 之后没有再出现 CARGO_HOLDING_WAIT 或 LOADING',
    ($firstFull.Count -eq 1 -and $reopenedAfterFull.Count -eq 0),
    'a FULL, then neither WAIT nor LOADING', "$(Format-L2LoadingPhaseSnapshots $snapshots)")

$journal.Note('整车满之后、离开最后一个装货站之前，一条放得下的单照样追加进来、装上车：关门的是离站，不是满。')
