#Requires -Version 7

<#
G3 `FP-IS-03` 的第二条场景：状态未知的结果在车载端重启后对账。协议向量 `CV-OPERATION-RESULT-UNKNOWN-RECONCILE`：
`OperationResult` → `DurableAck` → `RecoveryStateReport` → `OperationResult` → `DurableAck`；服务端 `NEVER_TREAT_UNKNOWN_AS_SUCCESS`、
`RECONCILE_FROM_REPORTED_JOURNAL`，车载端 `REPORT_UNKNOWN_AS_UNKNOWN`、`REPLAY_RESULT_ON_RECONNECT`。

装置与 `FP-IS-01`/`02` 相同：真服务端 + 真车载端 WPF + 真 slots-simulator + 假 RIoT + 假 MesIngest。

**怎么得到一份状态未知的结果。**见 `G3RecoveryCommon.ps1` 的 `Invoke-G3UnknownLoad`（control-server#128 起）：车载端报
`WAITING_OPERATOR` 之后断电，断电期间空仓门被关上，重启后中断结算报 `overallOutcome = UNKNOWN`。此前这里是「关门不放货、
等车载端操作员超时」，onboard-hmi#72 之后 v2 车载端空关即重开、不再交那份结果，走不通了。FP-IS-07 的
`g3-exception-resume` 走同一段前置，两条的断言号各归各的切片。

**两次重启。**第一次重启造出那份 UNKNOWN，它在第一次重启后的会话里第一次发出、被确认。向量要的是「已确认的结果 →
重启 → 新会话的报告列出它 → 同号补发」，所以场景再重启一次（车载端只把已确认、仍未结清的结果列进 `pendingResults`）。
`Context.RestartOnboard` 停掉车载端进程，用同一份暂存副本、配置与日志重新拉起；重启之后车载端报的，是它从自己日志里
恢复出来的东西。两端这一半都是 2026-09-13 按用户裁定补上的：服务端 `docs/defects/20260913-unknown-result-never-reconciled.md`，
车载端 `docs/W2G_UNKNOWN_RESULT_RECONCILE.md`。

**第一份结果要在重启前记下来。**服务端收件箱按 `messageId` 存一行；补发只差会话代号，被判为等价重放后，那一行的请求、
内容哈希与首次应答会换成补发的那份。所以场景在第二次重启前先把第一份的请求与应答抄下来。

**断言只读服务端的库、模拟器与假 RIoT 快照。**
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'G3RecoveryCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$simulator = $Context.Simulator
$connection = $Context.Connection

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: the replay comes from the onboard journal across a restart.'
}

# --- 1. 需求受理、到取货点、录入；装载在等人时断电，重启后以 UNKNOWN 结束 -------------------------------------

$load = Invoke-G3UnknownLoad $Context 'G3-03U'
$demandId = $load.DemandId
$attemptId = $load.AttemptId
$loadSlot = $load.Slot
$first = $load.First

function Get-Stage { return Get-G3Scalar $connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'" }

$sessionBefore = Get-G3Session $connection
$unlockingBefore = @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' }).Count
$slotBefore = Get-G3SlotState $simulator $loadSlot
$journal.Note("First result $($first.MessageId): $($first.Payload.overallOutcome) in generation $($first.Generation); slot $loadSlot is $slotBefore.")

# --- 2. 车载端再重启：报告列出待结清、补发、确认 ----------------------------------------------------------

$restartAt = [DateTimeOffset]::UtcNow
$null = & $Context.RestartOnboard
$replayed = Wait-L2Condition -Description 'the restarted onboard replayed the result in its new session' `
    -Journal $journal -Criterion 'replayed-result' -TimeoutSeconds 240 `
    -Probe {
        $row = @((Get-G3Inbound $connection 'OperationResult') | Where-Object { $_.MessageId -eq $first.MessageId })[0]
        if ($null -ne $row -and $row.Generation -gt $first.Generation -and $null -ne $row.ResponseLine) { $row } else { $null }
    } -Until { param($v) $null -ne $v }
# 补发之后再给服务端几轮：它若要据此改动什么（提交、派车、就绪），应当已经发生了。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

$report = @((Get-G3Inbound $connection 'RecoveryStateReport') | Where-Object { $_.Generation -eq $replayed.Generation })[0]
$pending = if ($null -ne $report) { @($report.Payload.pendingResults) } else { @() }
$orderOk = $null -ne $report -and $first.At -lt $restartAt -and
    $first.Response -eq 'DurableAck' -and [string]$first.ResponsePayload.acceptedContentSha256 -eq $first.ContentHash -and
    $report.At -gt $restartAt -and $report.At -lt $replayed.At -and
    $replayed.Response -eq 'DurableAck' -and [long]$replayed.ResponseLine.sessionGeneration -eq $replayed.Generation -and
    [string]$replayed.ResponsePayload.acceptedContentSha256 -eq $replayed.ContentHash -and $replayed.ContentHash -ne $first.ContentHash
$assertions.Add(
    'G3-03-08',
    '消息顺序与向量一致：结果 → 它的 DurableAck → 重启后新会话的恢复状态报告 → 同一 messageId 的结果按新会话代号补发 → 补发的 DurableAck（CV-OPERATION-RESULT-UNKNOWN-RECONCILE orderedExpectedMessages）',
    $orderOk,
    "Result(g$($first.Generation)) < Ack < RecoveryStateReport(g$($replayed.Generation)) < Result(g$($replayed.Generation), 同号) < Ack(新行哈希)",
    "Result g$($first.Generation) ack=$($first.Response) / Report $(if ($report) { "g$($report.Generation)" } else { '(none)' }) / Replay g$($replayed.Generation) ack=$($replayed.Response) / 有序=$orderOk")

$named = @($pending | Where-Object { [string]$_.messageId -eq $first.MessageId -and [string]$_.messageType -eq 'OperationResult' -and [string]$_.businessId -eq $attemptId })
$assertions.Add(
    'G3-03-09',
    '车载端如实报未知并从日志补发：结果 overallOutcome 为 UNKNOWN；重启后的报告把这次操作列为未结清、把这份结果列进 pendingResults；补发的载荷与第一份逐字段相同（REPORT_UNKNOWN_AS_UNKNOWN / REPLAY_RESULT_ON_RECONNECT）',
    ([string]$first.Payload.overallOutcome -eq 'UNKNOWN' -and $null -ne $report -and
        [string]$report.Payload.unsettledSlotOperationAttemptId -eq $attemptId -and $named.Count -eq 1 -and
        $replayed.PayloadJson -eq $first.PayloadJson),
    "UNKNOWN / unsettled=$attemptId / pendingResults 含该结果 / 载荷相同",
    "$($first.Payload.overallOutcome) / unsettled=$(if ($report) { $report.Payload.unsettledSlotOperationAttemptId } else { '(no report)' }) / pendingResults $($pending.Count) 条、含该结果 $($named.Count) / 载荷相同=$($replayed.PayloadJson -eq $first.PayloadJson)")

$results = (Invoke-L2Query -Connection $connection -Sql "SELECT OverallOutcome FROM OperationResults WHERE SlotOperationAttemptId = '$attemptId'")
$loadStatus = Get-G3Scalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
$demandStatus = Get-G3Scalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
$gateIntents = Get-G3Count $connection "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'"
$gateOrders = @($riot.Snapshot().body.orders | Where-Object { [string]$_.upperId -like '*GATE*' }).Count
$stage = Get-Stage
$assertions.Add(
    'G3-03-10',
    '未知不当成功、不重复提交：这次操作只有一行结果且为 UNKNOWN，装载仍 RecoveryRequired，需求未成功，旅程仍停在 Blocked，没有去关卡的意图也没有关卡单（NEVER_TREAT_UNKNOWN_AS_SUCCESS / forbidden unknown-as-success、duplicate-business-commit）',
    ($results.Count -eq 1 -and [string]$results[0].OverallOutcome -eq 'UNKNOWN' -and $loadStatus -eq 'RecoveryRequired' -and
        $demandStatus -ne 'Succeeded' -and $stage -eq 'Blocked' -and $gateIntents -eq 0 -and $gateOrders -eq 0),
    '1 行 UNKNOWN / RecoveryRequired / 未成功 / Blocked / 意图 0 / 关卡单 0',
    "$($results.Count) 行 $(if ($results.Count -ge 1) { $results[0].OverallOutcome }) / $loadStatus / $demandStatus / $stage / 意图 $gateIntents / 关卡单 $gateOrders")

$sessionAfter = Get-G3Session $connection
$assertions.Add(
    'G3-03-11',
    '服务端按报告的日志对账且不提前就绪：新会话代次大于重启前，补发之后待结清列表已清空，会话仍 RecoveryRequired（RECONCILE_FROM_REPORTED_JOURNAL / forbidden ready-before-reconciliation）',
    ([long]$sessionAfter.SessionGeneration -gt [long]$sessionBefore.SessionGeneration -and
        [string]$sessionAfter.PendingResultIdsJson -eq '[]' -and [string]$sessionAfter.Readiness -eq 'RecoveryRequired'),
    "代次 >$($sessionBefore.SessionGeneration) / [] / RecoveryRequired",
    "代次 $($sessionAfter.SessionGeneration) / $($sessionAfter.PendingResultIdsJson) / $($sessionAfter.Readiness) ($($sessionAfter.ReasonCode))")

$unlockingAfter = @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' }).Count
$slotAfter = Get-G3SlotState $simulator $loadSlot
$assertions.Add(
    'G3-03-12',
    '补发不碰物理：重启之后这次操作没有再开锁，装载仓仍是关门、空、锁上、开锁输出复位（forbidden duplicate-slot-unlock / finalState NO_UNPROVEN_STATE）',
    ($unlockingAfter -eq $unlockingBefore -and $slotAfter -eq 'CLOSED/EMPTY/1/0' -and $slotAfter -eq $slotBefore),
    "UNLOCKING $unlockingBefore 次不变 / CLOSED/EMPTY/1/0",
    "UNLOCKING $unlockingBefore → $unlockingAfter / $slotBefore → $slotAfter")

$journal.Note('FP-IS-03: an UNKNOWN load result was reported, named as pending after an onboard restart, replayed and reconciled without success.')
