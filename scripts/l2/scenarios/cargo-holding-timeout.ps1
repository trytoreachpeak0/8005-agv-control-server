#Requires -Version 7

<#
持货等单超时：没人来追加，车在最后一个装货站上等到期限，然后以 CARGO_HOLDING_TIMEOUT 关闭装货阶段、带着已装的货离站
（批次7-07，control-server#212；ADR-cross-0057）。

持货超时 40 秒，站点等待 10 秒。需求甲只占 1 个花篮，两侧都远没满，所以车装完就进 CARGO_HOLDING_WAIT：
1. 站点等待早已过去、期限还没到的时候（起算后约 30 秒），车**仍在站上**：阶段还是 AwaitingStationDeparture，关卡腿没有建单。
   这一条区分「持货在起作用」与「站点等待到了就走」——后者是批次7-07 之前的行为，也是最容易退化回去的样子。
2. 期限一到，装货阶段 CLOSED/CARGO_HOLDING_TIMEOUT，关闭时刻不早于期限；随后车离站开向关卡。
3. 关闭之后发的需求戊（11 号站，本来放得下）不进这趟旅程，积压理由 LOADING_PHASE_CLOSED。车队只有这一辆车，它此刻在途，
   所以戊只能等——它不被这辆车接走，就是「关了就不再接」。

WAIT 快照里的 cargoHoldingDeadlineAt 等于起算点 + 40 秒：车载端据此显示倒计时，差一点操作员看到的就是另一个时刻。
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
$timeout = [TimeSpan]::FromSeconds(40)

$a = New-L2CargoDemand 'A' 'N1-3' 1 $Context.RunId
$e = New-L2CargoDemand 'E' 'C15-13' 1 $Context.RunId

function Get-GateIntentCount([string]$JourneyId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT COUNT(*) AS N FROM OrderIntents i JOIN JourneyStops s ON s.UpperId = i.UpperId " +
        "WHERE s.JourneyId = '$JourneyId' AND s.StopRole <> 'PICKUP'")
    return [int]$rows[0].N
}

Initialize-L2CargoRig $Context

# --- 1. 需求甲装完，进入持货等单 ------------------------------------------------------------------------

Publish-L2CargoDemand $Context $a
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $Context.PickupStationRiotId

$waiting = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('CARGO_HOLDING_WAIT', 'VEHICLE_FULL', 'CLOSED') `
    -Criterion 'phase-wait'
$assertions.Add(
    'L2-CHT-01', '需求甲（1 花篮）装完、两侧都没满：车进入 CARGO_HOLDING_WAIT，起算点已落库',
    ([string]$waiting.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and $null -ne $waiting.CargoHoldingStartedAt -and
        [string]$waiting.CargoHoldingStartedAt -ne ''),
    'CARGO_HOLDING_WAIT, started', "$(Format-L2CargoJourney $waiting), started '$($waiting.CargoHoldingStartedAt)'")
# 后面每一步都以起算点为基准，没有它就量不了。抛一句说明为什么停，而不是让解析空串的异常顶替 L2-CHT-01 的结论。
if ($null -eq $waiting -or [string]::IsNullOrEmpty([string]$waiting.CargoHoldingStartedAt)) {
    throw 'L2-CHT-01 did not hold: the journey has no CargoHoldingStartedAt, and every later criterion is measured from it.'
}
$startedAt = [DateTimeOffset]::Parse([string]$waiting.CargoHoldingStartedAt, [Globalization.CultureInfo]::InvariantCulture)
$deadline = $startedAt + $timeout
$journal.Note("Cargo holding started at $($startedAt.ToString('o')); the deadline is $($deadline.ToString('o')).")

# --- 2. 站点等待已过、期限未到：车仍在站上 -----------------------------------------------------------------

# 服务端与场景在同一台机器上，时钟是同一个。
$probeAt = $startedAt + [TimeSpan]::FromSeconds(30)
$sleep = $probeAt - [DateTimeOffset]::UtcNow
if ($sleep -gt [TimeSpan]::Zero) { Start-Sleep -Milliseconds ([int]$sleep.TotalMilliseconds) }
$mid = Get-L2CargoJourney $connection $a.Id
$gateIntentsMid = Get-GateIntentCount $journeyId
$observedAt = [DateTimeOffset]::UtcNow
$assertions.Add(
    'L2-CHT-02', '起算后约 30 秒（站点等待 10 秒早已过去，期限 40 秒未到）：车仍在站上持货，关卡腿没有建单',
    ($observedAt -lt $deadline -and [string]$mid.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and
        [string]$mid.Stage -eq 'AwaitingStationDeparture' -and $gateIntentsMid -eq 0),
    "before deadline / CARGO_HOLDING_WAIT / AwaitingStationDeparture / 0 gate intents",
    "$(if ($observedAt -lt $deadline) { 'before' } else { 'AFTER' }) deadline ($($observedAt.ToString('o'))) / " +
    "$(Format-L2CargoJourney $mid) / $gateIntentsMid gate intents")

# --- 3. 期限到：CLOSED/CARGO_HOLDING_TIMEOUT，车离站 --------------------------------------------------------

$closed = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('CLOSED') -Criterion 'phase-closed' -TimeoutSeconds 90
$closedSnapshot = @((Get-L2LoadingPhaseSnapshots $connection $Context.AgvId) | Where-Object { $_.State -eq 'CLOSED' }) | Select-Object -First 1
$closedRow = if ($null -eq $closedSnapshot) { $null } else {
    Invoke-L2Query -Connection $connection -Sql "SELECT CreatedAt FROM ProtocolOutbox WHERE MessageId = '$($closedSnapshot.MessageId)'" }
$closedAt = if ($null -eq $closedRow -or $closedRow.Count -eq 0) { $null } else {
    [DateTimeOffset]::Parse([string]$closedRow[0].CreatedAt, [Globalization.CultureInfo]::InvariantCulture) }
$assertions.Add(
    'L2-CHT-03', '期限一到装货阶段关闭，理由 CARGO_HOLDING_TIMEOUT；发给车的那张关闭快照不早于期限',
    ([string]$closed.LoadingPhaseState -eq 'CLOSED' -and [string]$closed.LoadingClosedReason -eq 'CARGO_HOLDING_TIMEOUT' -and
        $null -ne $closedSnapshot -and $closedSnapshot.Reason -eq 'CARGO_HOLDING_TIMEOUT' -and
        $null -ne $closedAt -and $closedAt -ge $deadline),
    "CLOSED/CARGO_HOLDING_TIMEOUT at or after $($deadline.ToString('o'))",
    "$(Format-L2CargoJourney $closed), snapshot $(if ($closedSnapshot) { "$($closedSnapshot.State)/$($closedSnapshot.Reason)" } else { '(none)' }) at $(if ($closedAt) { $closedAt.ToString('o') } else { '-' })")

$gateIntents = Wait-L2ConditionOrLast -Description 'the vehicle left for the gate' -Journal $journal -Criterion 'gate-intent' `
    -TimeoutSeconds 60 -Probe { Get-GateIntentCount $journeyId } -Until { param($v) $v -ge 1 }
$assertions.Add(
    'L2-CHT-04', '关闭之后车带着已装的货离站：关卡腿建了单',
    ($gateIntents -ge 1), '>= 1', $gateIntents)

# --- 4. 关闭之后的需求不进这趟 -------------------------------------------------------------------------------

Publish-L2CargoDemand $Context $e
$backlog = Wait-L2ConditionOrLast -Description "demand E was judged against the closed vehicle" -Journal $journal `
    -Criterion 'e-reason' -TimeoutSeconds 60 `
    -Probe { Get-L2CargoBacklog $connection $e.Id } -Until { param($v) $null -ne $v -and [string]$v.ReasonCode -eq 'LOADING_PHASE_CLOSED' }
$eJourney = Get-L2CargoJourney $connection $e.Id
$assertions.Add(
    'L2-CHT-05', '关闭之后发的需求戊没有进这趟旅程，积压理由 LOADING_PHASE_CLOSED',
    ($null -eq $eJourney -and $null -ne $backlog -and [string]$backlog.ReasonCode -eq 'LOADING_PHASE_CLOSED'),
    'no journey / LOADING_PHASE_CLOSED',
    "$(if ($eJourney) { "journey $($eJourney.JourneyId)" } else { 'no journey' }) / $(if ($backlog) { $backlog.ReasonCode } else { '(no backlog row)' })")

# --- 5. WAIT 快照带的期限 ---------------------------------------------------------------------------------

$snapshots = Get-L2LoadingPhaseSnapshots $connection $Context.AgvId
$journal.Observe('loading-phase-snapshots', (Format-L2LoadingPhaseSnapshots $snapshots), @{ snapshots = $snapshots })
# WAIT 与之后的 CLOSED 都带这个期限：进入 CLOSED 保留原值、不清空（program#94 语义表，审查 M1）——车载端在「等单已到期」
# 那一行仍要显示它。
$firstWait = @($snapshots | Where-Object { $_.State -eq 'CARGO_HOLDING_WAIT' } | Select-Object -First 1)
$deadlines = @(if ($firstWait.Count -gt 0) {
    $snapshots | Where-Object { $_.Revision -ge $firstWait[0].Revision } | ForEach-Object { if ($_.Deadline) { $_.Deadline.ToString('o') } else { '(null)' } } |
        Sort-Object -Unique })
$closedWithDeadline = @($snapshots | Where-Object { $_.State -eq 'CLOSED' -and $null -ne $_.Deadline })
$assertions.Add(
    'L2-CHT-06', 'WAIT 快照与之后的 CLOSED 快照都带 cargoHoldingDeadlineAt，等于起算点 + 40 秒',
    ($deadlines.Count -eq 1 -and $deadlines[0] -eq $deadline.ToString('o') -and $closedWithDeadline.Count -ge 1),
    $deadline.ToString('o'), $(if ($deadlines.Count -eq 0) { '(no snapshot from the first WAIT on)' } else { $deadlines -join ', ' }))

$journal.Note('没有追加：车在站上持货到期限，以 CARGO_HOLDING_TIMEOUT 关闭后离站，之后的需求不再进这趟。')
