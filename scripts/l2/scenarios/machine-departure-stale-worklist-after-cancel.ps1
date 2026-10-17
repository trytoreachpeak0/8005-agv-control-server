#Requires -Version 7

<#
机台取货停靠上装一篮、取消另一条之后，按着被只补期限的一版顶掉的旧清单号点出发：受理（control-server#742 扩项，CP-0015）。

现场：agv03 10-17 140 站（T2-5_T3-5）本站装上一篮之后取消了另外两条，取消那一轮发了空清单（30），引擎随即补发只多一个期限的空清单（31）；
操作员按着 30 点的出发晚 62 ms 到，答 WORKLIST_REVISION_STALE，3 分半后第二次按成。修之后新旧两版没有实质差别（同一停靠、都没有待做项、
本站已装没变、之后没开过纠错）时照样受理。

形状：需求甲在 12 号站（N1-3），丁、乙在 11 号站（C15-13），先丁后乙，都在车去 12 号站的路上追加进来，并进同一个 11 号站停靠。甲装上；车到 11 号站，
合成车载端只录丁、丁装上，乙扫码前取消。取消那一版空清单被补期限的下一版顶掉之后，合成车载端显式带取消那一版的号点出发。竞态不靠时机碰：
合成车载端的出发入口可以指定 worklistRevision（tools/ControlServer.FakeOnboard/CurrentStopDeparture.cs）。

判据：
1. 前置：乙、丁进了甲那一趟旅程，并进同一个 11 号站停靠。
2. 丁装上、乙终结，取消那一版之后服务端又发了一版本停靠的清单（号更大），两版都没有待做项。
3. 出发请求带的是取消那一版的号（比当前号小），答 ACCEPTED。
4. 离站核验在受理之后发出，早于这一站的离站期限（第二个事实另等，README 第 14 条）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Change.psm1') -Force
. (Join-Path $PSScriptRoot 'CargoHoldingCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$onboard = $Context.Onboard

$a = New-L2CargoDemand 'A' 'N1-3' 1 $Context.RunId
$b = New-L2CargoDemand 'B' 'C15-13' 1 $Context.RunId
$d = New-L2CargoDemand 'D' 'C15-13' 1 $Context.RunId

function ConvertTo-Instant($value) {
    if ($null -eq $value -or [string]$value -eq '') { return $null }
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

function Format-Instant($value) {
    if ($null -eq $value) { return '(none)' }
    return $value.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Members([string]$JourneyId) {
    $map = @{}
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT DemandId, Status, PickupStopId FROM JourneyDemands WHERE JourneyId = '$JourneyId' AND RemovedAt IS NULL")
    foreach ($row in $rows) { $map[[string]$row.DemandId] = $row }
    return $map
}

# 一个站上发给车的每一版清单：号、条数、期限。按号排。
function Get-StationWorklists([string]$StationId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'CurrentStopWorklistSnapshot'")
    $list = foreach ($row in $rows) {
        $payload = ([string]$row.PayloadJson | ConvertFrom-Json -DateKind String).payload
        if ([string]$payload.stationId -ne $StationId) { continue }
        [pscustomobject]@{
            Revision = [long]$payload.worklistRevision
            Items    = @($payload.items).Count
            Deadline = ConvertTo-Instant $payload.stationDepartureDeadlineAt
        }
    }
    return , @($list | Sort-Object Revision)
}

function Format-Worklists([object[]]$Worklists) {
    return (@($Worklists) | ForEach-Object { "$($_.Revision)x$($_.Items)@$(Format-Instant $_.Deadline)" }) -join ' '
}

function Get-DepartureCheckQueuedAt([string]$StopId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT o.CreatedAt FROM JourneyStops s JOIN ProtocolOutbox o ON o.MessageId = s.DepartureSafetyCheckMessageId " +
        "WHERE s.StopId = '$StopId'")
    if ($rows.Count -eq 0) { return $null }
    return ConvertTo-Instant $rows[0].CreatedAt
}

function Get-DepartureResult([string]$departureRequestId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT PayloadJson, CreatedAt FROM ProtocolOutbox WHERE MessageType = 'CurrentStopDepartureResult'")
    foreach ($row in $rows) {
        $payload = ([string]$row.PayloadJson | ConvertFrom-Json -DateKind String).payload
        if ([string]$payload.departureRequestId -eq $departureRequestId) {
            return [pscustomobject]@{
                Outcome  = [string]$payload.outcome
                Reason   = if ($null -eq $payload.problem) { $null } else { [string]$payload.problem.reasonCode }
                Current  = [long]$payload.currentWorklistRevision
                QueuedAt = ConvertTo-Instant $row.CreatedAt
            }
        }
    }
    return $null
}

function Get-PeerCancellation([string]$cancellationId) {
    $body = (Invoke-RestMethod -Uri "$($onboard.BaseUrl)/$($onboard.Prefix)/load-cancellations" -TimeoutSec 10).body
    return @($body.cancellations) | Where-Object { [string]$_.cancellationId -eq $cancellationId } | Select-Object -First 1
}

function Get-HeldEntryRequest([string]$KeyPrefix) {
    return @(@($onboard.Snapshot().body.pending) | Where-Object {
            [string]$_.messageType -eq 'SublotEntryRequested' -and ([string]$_.key).StartsWith($KeyPrefix, [StringComparison]::Ordinal)
        }) | Select-Object -Last 1
}

Initialize-L2CargoRig $Context

# --- 1. 甲受理；乙、丁追加，并进同一个 11 号站停靠 ------------------------------------------------------

Publish-L2CargoDemand $Context $a
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
# 丁先于乙：录入请求的期待子批按加入旅程的先后排，合成车载端手动应答录的是第一个，于是录进去的是丁，乙留给取消。
Publish-L2CargoDemand $Context $d
$null = Wait-L2Condition -Description 'demand D joined the journey' -Journal $journal -Criterion 'd-joined' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $d.Id } -Until { param($v) $null -ne $v }
Publish-L2CargoDemand $Context $b
$joined = Wait-L2ConditionOrLast -Description 'demand B joined the journey' -Journal $journal -Criterion 'b-joined' -TimeoutSeconds 120 `
    -Probe { Get-Members $journeyId } -Until { param($v) $v.ContainsKey($b.Id) }
$assertions.Add(
    'L2-MDS-01', '前置：乙、丁进了甲那一趟旅程，并进同一个 11 号站停靠',
    ($joined.ContainsKey($b.Id) -and $joined.ContainsKey($d.Id) -and
        [string]$joined[$b.Id].PickupStopId -eq [string]$joined[$d.Id].PickupStopId),
    'B and D on one stop',
    "B $(if ($joined.ContainsKey($b.Id)) { $joined[$b.Id].PickupStopId } else { '(missing)' }), D $(if ($joined.ContainsKey($d.Id)) { $joined[$d.Id].PickupStopId } else { '(missing)' })")

# --- 2. 12 号站装甲；11 号站只录丁、丁装上 ----------------------------------------------------------------

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $Context.PickupStationRiotId
$null = Wait-L2Condition -Description 'demand A was loaded' -Journal $journal -Criterion 'a-loaded' -TimeoutSeconds 120 `
    -Probe { [string](Get-Members $journeyId)[$a.Id].Status } -Until { param($v) $v -eq 'LOADED' }

# 11 号站的录入请求挂着不答，由场景只把丁录进去。
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Manual' })
$bStop = Move-L2CargoVehicleToCurrentStop $Context $journeyId 11
$bStopRow = Invoke-L2Query -Connection $connection -Sql (
    "SELECT StationId, OperationSessionId FROM JourneyStops WHERE StopId = '$($bStop.StopId)'")
$bStationId = [string]$bStopRow[0].StationId
$keyPrefix = "sublot:$([string]$bStopRow[0].OperationSessionId):"
$held = Wait-L2Condition -Description 'the entry request at station 11 is held on the peer' -Journal $journal `
    -Criterion 'entry-held' -TimeoutSeconds 60 -Probe { Get-HeldEntryRequest $keyPrefix } -Until { param($v) $null -ne $v }
$null = $onboard.Command('Put', "answer/$([string]$held.key)", @{ completed = $true })
$null = Wait-L2Condition -Description 'demand D was loaded at station 11' -Journal $journal -Criterion 'd-loaded' -TimeoutSeconds 120 `
    -Probe { [string](Get-Members $journeyId)[$d.Id].Status } -Until { param($v) $v -eq 'LOADED' }

# --- 3. 乙扫码前取消；取消那一版被补期限的下一版顶掉 ---------------------------------------------------------

$cancellationId = [guid]::NewGuid().ToString('D')
$journal.Note("Operator cancels demand B before any sublot entry ($cancellationId).")
$null = $onboard.Command('Put', "load-cancellations/$cancellationId", @{
    demandId               = $b.Id
    slotOperationAttemptId = $null
    reason                 = '现场确认本站没有要装的货。'
})
$null = Wait-L2ConditionOrLast -Description "the peer's ALL_EMPTY result was acknowledged" -Journal $journal `
    -Criterion 'b-result-acknowledged' -TimeoutSeconds 60 `
    -Probe { Get-PeerCancellation $cancellationId } -Until { param($v) $v -and $null -ne $v.resultAcknowledgedAt }
$ended = Wait-L2ConditionOrLast -Description 'the cancellation ended demand B' -Journal $journal -Criterion 'b-ended' -TimeoutSeconds 60 `
    -Probe { [string](Get-Members $journeyId)[$b.Id].Status } -Until { param($v) $v -eq 'TERMINATED' }
# 取消那一版（本站最后一版无待做项、无期限的空清单）之后，引擎补发一版带期限的空清单。另等它：它是引擎下一轮的另一次写入。
$worklists = Wait-L2ConditionOrLast -Description 'a deadline refresh replaced the cancellation worklist' -Journal $journal `
    -Criterion 'refresh-replaced' -TimeoutSeconds 60 `
    -Probe { Get-StationWorklists $bStationId } `
    -Until { param($v) @($v | Where-Object { $_.Items -eq 0 }).Count -ge 2 }
$empties = @($worklists | Where-Object { $_.Items -eq 0 })
$stale = if ($empties.Count -ge 2) { $empties[0] } else { $null }
$current = if ($empties.Count -ge 2) { $empties[-1] } else { $null }
$assertions.Add(
    'L2-MDS-02', '丁装上、乙终结；取消那一版之后服务端又发了一版本停靠清单（号更大），两版都没有待做项',
    ([string](Get-Members $journeyId)[$d.Id].Status -eq 'LOADED' -and $ended -eq 'TERMINATED' -and
        $null -ne $stale -and $current.Revision -gt $stale.Revision),
    'D LOADED, B TERMINATED, two empty worklists', "B $ended; worklists $(Format-Worklists $worklists)")

# --- 4. 带旧号点出发：受理；离站核验早于期限 -----------------------------------------------------------------

$departureRequestId = [guid]::NewGuid().ToString('D')
$sent = $onboard.Command('Put', "current-stop-departures/$departureRequestId", @{
    worklistRevision        = $(if ($null -eq $stale) { 0 } else { $stale.Revision })
    withoutOperationSession = $true
    operatorId              = 'L2-OPERATOR'
})
$journal.Observe('departure-sent', "revision $($sent.body.worklistRevision)", @{ request = $sent.body })
$result = Wait-L2ConditionOrLast -Description 'the departure was answered' -Journal $journal -Criterion 'departure-answered' `
    -TimeoutSeconds 60 -Probe { Get-DepartureResult $departureRequestId } -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-MDS-03', '出发请求带的是取消那一版的号（比当前号小），新旧两版没有实质差别，答 ACCEPTED',
    ($null -ne $result -and $null -ne $stale -and [long]$sent.body.worklistRevision -lt $result.Current -and $result.Outcome -eq 'ACCEPTED'),
    "ACCEPTED for revision $(if ($stale) { $stale.Revision } else { '?' })",
    $(if ($null -eq $result) { '(no answer)' } else { "$($result.Outcome) $($result.Reason) for revision $($sent.body.worklistRevision), current $($result.Current)" }))

$deadline = if ($null -eq $current) { $null } else { $current.Deadline }
$departed = Wait-L2ConditionOrLast -Description 'the departure check of station 11 went out' -Journal $journal `
    -Criterion 'departure-check' -TimeoutSeconds 60 `
    -Probe { Get-DepartureCheckQueuedAt ([string]$bStop.StopId) } -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-MDS-04', '受理之后离站核验发出，早于这一站的离站期限',
    ($null -ne $departed -and $null -ne $deadline -and $departed -lt $deadline),
    "departure check before $(Format-Instant $deadline)", "departure check $(Format-Instant $departed)")

$journal.Note('带被只补期限的一版顶掉的旧号点出发，新旧两版没有实质差别，服务端受理并离站。')
