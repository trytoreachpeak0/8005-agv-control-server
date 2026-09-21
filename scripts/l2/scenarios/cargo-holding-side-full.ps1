#Requires -Version 7

<#
持货等单到按侧判满：两侧各以一种方式满，车从 CARGO_HOLDING_WAIT 进 VEHICLE_FULL，离开最后一个装货停靠时关闭
（批次7-07，control-server#212；REQ-0354、ADR-cross-0057、ADR-cross-0059）。

一侧「满」有两种，这条场景让两侧各占一种，一次看全：
- FRONT 是**没有空仓了**：需求甲 4 个花篮，正好把 FRONT 组的四个仓装满。
- REAR 是**有一条候选只因本车货物占着这一侧而装不下**：需求乙 2 个花篮装进 REAR 之后还剩两个空仓，需求丁要 3 个——
  它放得进一个空的 REAR 组（4 个仓），只是放不进这辆车此刻的 REAR 组，派车判 SLOT_GROUP_OCCUPIED_BY_OWN_CARGO。

所以只有需求丁出现之后车才满：之前 REAR 还有空仓、也没有这样的候选，状态必须停在 CARGO_HOLDING_WAIT。这条是整条场景
最要紧的一处——「FRONT 满了就判满」是最容易写出来的错误实现，而它在需求丁出现之前就会把车放走。

中途还顺带看两件事：
- 追加进来的第二个停靠（需求乙在 11 号站）让状态从 WAIT 回到 LOADING，装完再回 WAIT；
- **期限不因新停靠重算**：两个停靠上发的 WAIT 快照，cargoHoldingDeadlineAt 是同一个时刻（起算点是第一次装货落定）。

车离开最后一个装货停靠之后是 CLOSED/VEHICLE_FULL，需求丁此后的积压理由变成 LOADING_PHASE_CLOSED——关闭的判据排在仓位
判据之前（Order 99 对 100），车关了就不再有「装不下」这一说。
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

Initialize-L2CargoRig $Context

# --- 1. 需求甲：装满 FRONT，进入持货等单 ------------------------------------------------------------------

Publish-L2CargoDemand $Context $a
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $Context.PickupStationRiotId

$afterA = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('CARGO_HOLDING_WAIT', 'VEHICLE_FULL', 'CLOSED') `
    -Criterion 'phase-after-a'
$assertions.Add(
    'L2-CHS-01', '需求甲装完、FRONT 已无空仓，但 REAR 还空着：车进入持货等单（CARGO_HOLDING_WAIT），不是 VEHICLE_FULL',
    ([string]$afterA.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and $null -ne $afterA.CargoHoldingStartedAt -and
        [string]$afterA.CargoHoldingStartedAt -ne ''),
    'CARGO_HOLDING_WAIT, started', "$(Format-L2CargoJourney $afterA), started '$($afterA.CargoHoldingStartedAt)'")
$startedAt = [string]$afterA.CargoHoldingStartedAt

# --- 2. 需求乙：追加进来，新开一个停靠；装完 REAR 还剩两个空仓 ------------------------------------------------

Publish-L2CargoDemand $Context $b
$joined = Wait-L2ConditionOrLast -Description 'demand B joined the journey' -Journal $journal -Criterion 'b-joined' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $b.Id } -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-CHS-02', '需求乙追加进了持货等单的这辆车（同一趟旅程）',
    ($null -ne $joined -and [string]$joined.JourneyId -eq $journeyId), $journeyId,
    $(if ($null -eq $joined) { '(未加入)' } else { [string]$joined.JourneyId }))

$null = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('LOADING') -Criterion 'phase-loading-again' -TimeoutSeconds 60
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId 11
$afterB = Wait-L2ConditionOrLast -Description 'demand B was loaded and the vehicle is holding again' -Journal $journal `
    -Criterion 'phase-after-b' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql (
            "SELECT r.LoadingPhaseState, r.LoadingClosedReason, r.CargoHoldingStartedAt, r.FullSlotPositionsJson, d.Status AS DemandStatus " +
            "FROM JourneyRuntimes r JOIN JourneyDemands d ON d.JourneyId = r.JourneyId " +
            "WHERE r.JourneyId = '$journeyId' AND d.DemandId = '$($b.Id)'")
        if ($rows.Count -eq 0) { return $null }
        return $rows[0]
    } `
    -Until { param($v) $null -ne $v -and [string]$v.DemandStatus -eq 'LOADED' -and [string]$v.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' }
$assertions.Add(
    'L2-CHS-03', '需求乙装完、REAR 还有两个空仓：仍是 CARGO_HOLDING_WAIT，起算点没有因为新停靠重置',
    ($null -ne $afterB -and [string]$afterB.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and
        [string]$afterB.CargoHoldingStartedAt -eq $startedAt),
    "CARGO_HOLDING_WAIT, started $startedAt",
    $(if ($null -eq $afterB) { '(no row)' } else { "$($afterB.LoadingPhaseState) ($($afterB.DemandStatus)), started $($afterB.CargoHoldingStartedAt)" }))

# 再转几轮：「还没满」要在车有机会判满的那几轮里都成立，读一次会在判满还没发生时误绿。
$null = Wait-L2Iterations -Riot $Context.Riot -Count 5 -Journal $journal
$stillWaiting = Get-L2CargoJourney $connection $a.Id
$assertions.Add(
    'L2-CHS-04', '又转了五轮，REAR 仍有空仓且没有「只因本车货物装不下」的候选：状态仍是 CARGO_HOLDING_WAIT，FRONT 单侧满不算整车满',
    ([string]$stillWaiting.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT'),
    'CARGO_HOLDING_WAIT', "$(Format-L2CargoJourney $stillWaiting) full sides $($stillWaiting.FullSlotPositionsJson)")

# --- 3. 需求丁：REAR 只因本车货物装不下 → VEHICLE_FULL -------------------------------------------------------

Publish-L2CargoDemand $Context $d
$full = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('VEHICLE_FULL', 'CLOSED') -Criterion 'phase-full' -TimeoutSeconds 90
$backlogD = Get-L2CargoBacklog $connection $d.Id
$assertions.Add(
    'L2-CHS-05', '需求丁（3 花篮）只因本车货物占着 REAR 而装不下：车进入 VEHICLE_FULL，满的两侧是 FRONT 与 REAR',
    ([string]$full.LoadingPhaseState -in @('VEHICLE_FULL', 'CLOSED') -and
        ([string]$full.LoadingPhaseState -ne 'CLOSED' -or [string]$full.LoadingClosedReason -eq 'VEHICLE_FULL') -and
        [string]$full.FullSlotPositionsJson -ceq '["FRONT","REAR"]'),
    'VEHICLE_FULL, ["FRONT","REAR"]', "$(Format-L2CargoJourney $full), $($full.FullSlotPositionsJson), D backlog $(if ($backlogD) { $backlogD.ReasonCode } else { '-' })")

# --- 4. 离开最后一个装货停靠：CLOSED/VEHICLE_FULL ---------------------------------------------------------

$closed = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('CLOSED') -Criterion 'phase-closed' -TimeoutSeconds 120
$assertions.Add(
    'L2-CHS-06', '车离开最后一个装货停靠：装货阶段关闭，理由 VEHICLE_FULL',
    ([string]$closed.LoadingPhaseState -eq 'CLOSED' -and [string]$closed.LoadingClosedReason -eq 'VEHICLE_FULL'),
    'CLOSED/VEHICLE_FULL', (Format-L2CargoJourney $closed))

$backlogAfter = Wait-L2ConditionOrLast -Description "demand D's backlog reason became LOADING_PHASE_CLOSED" -Journal $journal `
    -Criterion 'd-reason-closed' -TimeoutSeconds 60 `
    -Probe { Get-L2CargoBacklog $connection $d.Id } -Until { param($v) $null -ne $v -and [string]$v.ReasonCode -eq 'LOADING_PHASE_CLOSED' }
$dJourney = Get-L2CargoJourney $connection $d.Id
$assertions.Add(
    'L2-CHS-07', '需求丁没有进这趟旅程，关闭之后它的积压理由是 LOADING_PHASE_CLOSED',
    ($null -eq $dJourney -and $null -ne $backlogAfter -and [string]$backlogAfter.ReasonCode -eq 'LOADING_PHASE_CLOSED'),
    'no journey / LOADING_PHASE_CLOSED',
    "$(if ($dJourney) { "journey $($dJourney.JourneyId)" } else { 'no journey' }) / $(if ($backlogAfter) { $backlogAfter.ReasonCode } else { '(no backlog row)' })")

# --- 5. 发给车的快照：经过的状态、同一个期限 -----------------------------------------------------------------

$snapshots = Get-L2LoadingPhaseSnapshots $connection
$journal.Observe('loading-phase-snapshots', (Format-L2LoadingPhaseSnapshots $snapshots), @{ snapshots = $snapshots })

# 车载端按修订号采纳，修订号严格递增、不重号。
$revisions = @($snapshots | ForEach-Object { $_.Revision })
$distinct = @($revisions | Sort-Object -Unique)
$assertions.Add(
    'L2-CHS-08', '车辆业务状态快照的修订号不重号（车载端号同内容不同会当场拆会话）',
    ($distinct.Count -eq $revisions.Count), "$($revisions.Count) distinct", "$($distinct.Count) of $($revisions.Count)")

# 状态的次序：去掉相邻重复之后必须恰好是这一串——到站 LOADING、WAIT、LOADING（追加）、WAIT、FULL、CLOSED。比整串而不是只比尾巴：
# 开头多出或少了一张（例如不持货时本不该发的那张），只比尾巴看不见。
$sequence = [Collections.Generic.List[string]]::new()
foreach ($snapshot in $snapshots) {
    $label = if ($snapshot.Reason) { "$($snapshot.State)/$($snapshot.Reason)" } else { $snapshot.State }
    if ($sequence.Count -eq 0 -or $sequence[$sequence.Count - 1] -ne $label) { $sequence.Add($label) }
}
$expectedSequence = 'LOADING CARGO_HOLDING_WAIT LOADING CARGO_HOLDING_WAIT VEHICLE_FULL CLOSED/VEHICLE_FULL'
$assertions.Add(
    'L2-CHS-09', '车收到的装货阶段依次是：到站装货 → 持货等单 → 追加后回到装货 → 持货等单 → 整车满 → 因满关闭',
    (($sequence -join ' ') -ceq $expectedSequence), $expectedSequence, ($sequence -join ' '))

# 从第一张 WAIT 起，之后每一张（追加后的 LOADING、第二站的 WAIT、FULL、CLOSED）都带同一个期限：起算点不因新停靠重算，
# 进入 CLOSED 也保留原值、不清空（program#94 语义表，审查 M1）。
$firstWait = @($snapshots | Where-Object { $_.State -eq 'CARGO_HOLDING_WAIT' } | Select-Object -First 1)
$deadlines = @(if ($firstWait.Count -gt 0) {
    $snapshots | Where-Object { $_.Revision -ge $firstWait[0].Revision } | ForEach-Object { if ($_.Deadline) { $_.Deadline.ToString('o') } else { '(null)' } } |
        Sort-Object -Unique })
# 起算点为空（车从没进入持货等单，L2-CHS-01 已经红了）时照样判、判红，不让解析空串的异常顶替这一条的结论：
# 「一侧满就算整车满」的注入（red-1）正是这样，第一版在这里抛。
$expectedDeadline = if ([string]::IsNullOrEmpty($startedAt)) { '(no holding start, see L2-CHS-01)' } else {
    ([DateTimeOffset]::Parse($startedAt, [Globalization.CultureInfo]::InvariantCulture) + [TimeSpan]::FromMinutes(10)).ToString('o') }
$assertions.Add(
    'L2-CHS-10', '从第一张 WAIT 起，之后每一张快照（含 LOADING、FULL 与 CLOSED）都带同一个期限，等于第一次装货落定 + 持货超时（十分钟）',
    ($deadlines.Count -eq 1 -and $deadlines[0] -eq $expectedDeadline),
    $expectedDeadline, $(if ($deadlines.Count -eq 0) { '(no snapshot from the first WAIT on)' } else { $deadlines -join ', ' }))

$journal.Note('两侧各以一种方式满：FRONT 无空仓、REAR 只因本车货物装不下；车从持货等单进整车满，离站即关闭。')
