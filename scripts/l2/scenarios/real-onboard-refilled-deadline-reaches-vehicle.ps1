#Requires -Version 7

<#
control-server#339：等录入时链路断一次，重连后服务端重填的离站期限要送到真车载端——车上那一站的期限与服务端判定用的一致。

**为什么要在真车载端上看。**v2 车载端不作废也不重新计满期限，永远照它采纳的最新一版 `CurrentStopWorklistSnapshot` 上的
`stationDepartureDeadlineAt` 显示倒计时（cs#331 作者读车载端 `1184bb07`，本票评论）。服务端在断联时作废本轮期限、重连后从那一刻
重填（ADR-cross-0055）。本票之前重填只发生在服务端：车上的倒计时比服务端的早结束，或者已显示「已到期，等待本站结束」而服务端刚重新
计满。车上显示什么只有真车载端说得出来，合成对端的同一件事由 `station-deadline-sublot-timeout` 的 `L2-SD-16` 从服务端发件箱那一侧看。

**判据读三处：服务端库、车载端日志库、协议故障代理。**车载端采纳了什么记在它的 `WireToGateAppliedJourneySnapshots` 表里
（`PayloadJson` 是采纳的那一版 payload 原文），服务端判定用的期限是旅程行上的 `StationDepartureWaitStartedAt` 加本装置的期限时长。
**判据要的是两边相等、并且等于重填之后的期限**，不是「车收到了新一版」：新一版带着旧期限、或车采纳的仍是旧一版，都算失败。

**断开靠 `tools/ControlServer.ProtocolFaultProxy` 的 `POST /control/v1/disconnect`**：不丢任何行，只断开，车自己重连
（与 `real-onboard-compensate-then-reconnect` 同一个做法）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$proxy = $Context.ProtocolProxy

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: its assertions read the onboard journal.'
}
if ($null -eq $proxy) { throw 'This scenario needs ProtocolFaultProxy = $true in its setup file.' }

# 与 setup.psd1 的 StationDepartureWaitTimeout 相同。
$window = [TimeSpan]::FromMinutes(5)
$laterIds = @('L2-RD-02', 'L2-RD-03', 'L2-RD-04', 'L2-RD-05', 'L2-RD-06')

$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')

function ConvertTo-Instant($value) {
    if (-not (Test-L2RealPresent $value)) { return $null }
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

# 服务端此刻判定用的期限：旅程行上的起点加期限时长；起点为空（断联那一轮作废了、还没重填）时为空。
function Get-ServerDeadline {
    $start = ConvertTo-Instant (Get-L2RealScalar $connection (
        "SELECT StationDepartureWaitStartedAt AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'"))
    return [pscustomobject]@{ Start = $start; Deadline = $(if ($null -ne $start) { $start.Add($window) } else { $null }) }
}

# 车载端采纳的那一版清单：日志库里这一类快照号最大的那一行。没有采纳过任何一版时为空。
function Get-HeldWorklist {
    $journalConnection = [Microsoft.Data.Sqlite.SqliteConnection]::new(
        "Data Source=$($Context.OnboardJournalPath);Mode=ReadOnly")
    $journalConnection.Open()
    try {
        $rows = Invoke-L2Query -Connection $journalConnection -Sql (
            "SELECT MessageId, Revision, PayloadJson FROM WireToGateAppliedJourneySnapshots WHERE MessageType = 'CurrentStopWorklistSnapshot'")
    } finally {
        $journalConnection.Dispose()
    }
    if ($rows.Count -eq 0) { return $null }
    $top = @($rows | Sort-Object { [long]$_.Revision } -Descending)[0]
    $payload = [string]$top.PayloadJson | ConvertFrom-Json -DateKind String
    return [pscustomobject]@{
        MessageId = [string]$top.MessageId
        Revision  = [long]$top.Revision
        Deadline  = ConvertTo-Instant $payload.stationDepartureDeadlineAt
    }
}

# 服务端发给这条需求的清单，连同确认与否。
function Get-ServerWorklists {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, PayloadJson, AcknowledgedAt FROM ProtocolOutbox WHERE MessageType = 'CurrentStopWorklistSnapshot'")
    $mine = foreach ($row in $rows) {
        $payload = ([string]$row.PayloadJson | ConvertFrom-Json -DateKind String).payload
        if (@($payload.items | Where-Object { [string]$_.demandId -eq $demandId }).Count -gt 0) {
            [pscustomobject]@{
                MessageId    = [string]$row.MessageId
                Revision     = [long]$payload.worklistRevision
                Deadline     = ConvertTo-Instant $payload.stationDepartureDeadlineAt
                Acknowledged = Test-L2RealPresent $row.AcknowledgedAt
            }
        }
    }
    return , @($mine)
}

function Format-Instant($value) { if ($null -eq $value) { '(null)' } else { $value.ToString('o') } }

# --- 1. 车到取货点，停在等录入；车上那一版清单的期限与服务端一致 ----------------------------------------------

$journal.Note("Publishing demand $($demandGuid.ToString('N')).")
$null = $Context.MesIngest.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = "L2-RD-$($Context.RunId)"; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})
$intent = Wait-L2RealIntent $Context $demandId 'TO_PICKUP'
Move-L2RealVehicleTo $Context $intent $Context.PickupStationRiotId 'the pickup station'
$null = Wait-L2Condition -Description 'the journey waits for a sublot and the vehicle acknowledged its worklist' `
    -Journal $journal -Criterion 'arrived-worklist-acknowledged' -TimeoutSeconds 120 `
    -Probe {
        $stage = Get-L2RealStage $connection $demandId
        $acknowledged = @((Get-ServerWorklists) | Where-Object { $_.Acknowledged }).Count
        "$stage / $acknowledged"
    } `
    -Until { param($v) $v -match '^AwaitingSublot / [1-9]' }

$arrival = Get-ServerDeadline
$arrivalWorklists = Get-ServerWorklists
$arrivalRevision = ($arrivalWorklists | Measure-Object -Property Revision -Maximum).Maximum
$heldAtArrival = Wait-L2RealOrLast -Description 'the onboard journal holds the arrival worklist' `
    -Journal $journal -Criterion 'arrival-worklist-held' -TimeoutSeconds 30 `
    -Probe { Get-HeldWorklist } -Until { param($v) $null -ne $v -and $v.Revision -eq $arrivalRevision }
$arrivalAgrees = ($null -ne $arrival.Deadline -and $null -ne $heldAtArrival -and
    $heldAtArrival.Revision -eq $arrivalRevision -and $heldAtArrival.Deadline -eq $arrival.Deadline)
$assertions.Add(
    'L2-RD-01', '前提：到站之后，车载端采纳的清单就是到站那一版，期限与服务端一致（到站起点加 5 分钟）',
    $arrivalAgrees,
    "r$arrivalRevision / $(Format-Instant $arrival.Deadline)",
    $(if ($heldAtArrival) { "r$($heldAtArrival.Revision) / $(Format-Instant $heldAtArrival.Deadline)" } else { '(车载端日志库里没有清单)' }))
if (-not $arrivalAgrees) {
    Add-L2RealNotReached $assertions $laterIds '到站那一版就没对上，后面的判据无从谈起'
    return
}

# --- 2. 期限走掉一截之后断开一次，车自己重连 -----------------------------------------------------------------

# 让钟走几秒：重填出来的期限要与到站那一个不同，否则「两边一致」在修前修后都成立。
$null = Wait-L2Iterations -Riot $Context.Riot -Count 3 -Journal $journal
$sessionBefore = Get-L2RealSession $connection $Context.AgvId
$lastBefore = [int]@((Get-L2RealTraffic $proxy).connections)[-1].connection
$journal.Note("Disconnecting the relay (last connection so far #$lastBefore).")
$closed = @($proxy.Command('Post', 'disconnect', @{}).body.connections)
$journal.Note("The relay closed connection(s) $($closed -join ', ').")

$sessionAfter = Wait-L2RealOrLast -Description 'the session was Ready again in a newer generation after the reconnect' `
    -Journal $journal -Criterion 'session-ready-after-reconnect' -TimeoutSeconds 90 `
    -Probe { Get-L2RealSession $connection $Context.AgvId } `
    -Until { param($v)
        $null -ne $v -and [long]$v.SessionGeneration -gt [long]$sessionBefore.SessionGeneration -and
            [string]$v.Readiness -eq 'Ready' }
$reconnected = ($null -ne $sessionAfter -and [long]$sessionAfter.SessionGeneration -gt [long]$sessionBefore.SessionGeneration -and
    [string]$sessionAfter.Readiness -eq 'Ready')
$assertions.Add(
    'L2-RD-02', '断开一次之后会话在新的一代回到 Ready',
    $reconnected, "gen > $($sessionBefore.SessionGeneration) / Ready", (Format-L2RealSession $sessionAfter))
if (-not $reconnected) {
    Add-L2RealNotReached $assertions @('L2-RD-03', 'L2-RD-04', 'L2-RD-05', 'L2-RD-06') '重连没有回到 Ready'
    return
}

# 重填是会话回到 Ready 之后引擎某一轮的写入，不与重连同一次提交，所以等（scripts/l2/README.md 第 14 条）。
$refilled = Wait-L2RealOrLast -Description 'the runtime refilled the station departure wait after the reconnect' `
    -Journal $journal -Criterion 'wait-refilled' -TimeoutSeconds 60 `
    -Probe { Get-ServerDeadline } -Until { param($v) $null -ne $v.Start -and $v.Start -gt $arrival.Start }
$refilledOk = ($null -ne $refilled.Start -and $refilled.Start -gt $arrival.Start)
$assertions.Add(
    'L2-RD-03', '服务端按 ADR-cross-0055 重填了期限：起点换成重连之后的时刻',
    $refilledOk, "> $(Format-Instant $arrival.Start)", (Format-Instant $refilled.Start))
if (-not $refilledOk) {
    Add-L2RealNotReached $assertions @('L2-RD-04', 'L2-RD-05', 'L2-RD-06') '服务端没有重填期限'
    return
}

# --- 3. 车上那一站的期限与服务端一致，并且就是重填之后的那一个 ------------------------------------------------

# 新的一版清单与重填同一次保存，车的采纳与确认是之后的另一次写入，所以等；等不到时把最后一次读数交给判据。
# 服务端的期限在同一次读里一起取：判据比的是同一时刻的两边。
$agreement = Wait-L2RealOrLast -Description 'the onboard holds a worklist carrying the server''s refilled deadline' `
    -Journal $journal -Criterion 'refilled-deadline-held' -TimeoutSeconds 60 `
    -Probe { [pscustomobject]@{ Server = Get-ServerDeadline; Held = Get-HeldWorklist } } `
    -Until { param($v)
        $null -ne $v.Held -and $null -ne $v.Server.Deadline -and
            $v.Held.Deadline -eq $v.Server.Deadline -and $v.Server.Deadline -eq $refilled.Deadline }
$held = $agreement.Held
$assertions.Add(
    'L2-RD-04', '车载端采纳的清单期限等于服务端此刻判定用的期限，并且等于重填之后的期限（不是到站那一个）',
    ($null -ne $held -and $held.Deadline -eq $agreement.Server.Deadline -and $held.Deadline -eq $refilled.Deadline -and
        $held.Deadline -ne $arrival.Deadline),
    "车上 = 服务端 = $(Format-Instant $refilled.Deadline)（到站那一个是 $(Format-Instant $arrival.Deadline)）",
    "车上 $(if ($held) { "r$($held.Revision) $(Format-Instant $held.Deadline)" } else { '(none)' }) / 服务端 $(Format-Instant $agreement.Server.Deadline)")
$assertions.Add(
    'L2-RD-05', '车载端采纳的是一版号更大的清单：重填作为新的一版下发，不是同号改内容',
    ($null -ne $held -and $held.Revision -gt $arrivalRevision),
    "r > $arrivalRevision", $(if ($held) { "r$($held.Revision)" } else { '(none)' }))

# 录入请求跟着那一版重发：旧的一张随修订号变化在车上作废，服务端最后发出的那张要答车手上这一版。
$entryRevision = Get-L2RealScalar $connection (
    "SELECT json_extract(PayloadJson, '$.payload.worklistRevision') AS Value FROM ProtocolOutbox " +
    "WHERE MessageType = 'SublotEntryRequested' ORDER BY rowid DESC LIMIT 1")
$stage = Get-L2RealStage $connection $demandId
$assertions.Add(
    'L2-RD-06', '服务端最后发出的录入请求答的是车手上那一版清单，旅程仍在等录入',
    ($null -ne $held -and [string]$entryRevision -eq [string]$held.Revision -and $stage -eq 'AwaitingSublot'),
    "录入请求 r$(if ($held) { $held.Revision }) / AwaitingSublot", "录入请求 r$entryRevision / $stage")

# 先赋值再用：Get-ServerWorklists 是 `return , @(...)`，`@(Get-ServerWorklists) | ForEach-Object` 会把整张列表当成一个元素，
# 两版清单时 `$_.Deadline` 是数组（本场景 c0c5aa81 上的预检就是这样在最后一行抛的，只有一版时成员枚举恰好给出标量）。
$finalWorklists = Get-ServerWorklists
$journal.Observe('worklists',
    (($finalWorklists | ForEach-Object { "r$($_.Revision) $(Format-Instant $_.Deadline) ack=$($_.Acknowledged)" }) -join '；'),
    @{ held = $held; server = $agreement.Server; arrival = $arrival })
$journal.Note('Scenario finished.')
