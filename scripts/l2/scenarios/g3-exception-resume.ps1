#Requires -Version 7

<#
G3 `FP-IS-07` 的第一条场景，证两条向量：

- `CV-OPERATION-RESULT-UNKNOWN-RECONCILE`：装载以 UNKNOWN 结束 → 车载端重启 → 新会话的恢复状态报告列出待结清结果 → 同号补发。
  与 `FP-IS-03` 的 `g3-operation-result-unknown-reconcile` 同一段流程，这里另起断言号（G3-07-01..05）归 `FP-IS-07`：同一条断言
  不能在一份声明里算给两片（`Assert-G3ClaimCoversReport`）。
- `CV-EXCEPTION-RESUME`：`ExceptionRecoverySessionRequested` → `ExceptionRecoverySessionOpened` → `RecoveryActionSubmitted` →
  `RecoveryActionAccepted` → `SlotOperationResumeCommand` → `OperationResult`。

**为什么恢复之前必须先重启。**服务端授权 `RESUME_AFTER_REPAIR` 要一个已证实的物理断点：会话行上的未结清尝试是这笔装载、
检查点是 `PREPARED` / `ACTIVE_UNLOCK_SET` / `SAFE_FINISH_REACHED`（`ValidateActionPreconditions`）。这两项只在恢复状态报告里
报上来；装载超时发生在会话中途，服务端手上的还是装载之前那次握手报的值。重启让车载端从日志里重报一份，恰好也是
第一条向量要的那一步。

**恢复之后操作员这次放了货。**车载端按授权重开原仓，场景放货关门，车载端报 `COMPLETED`，服务端用它替换那份 UNKNOWN。
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
    throw 'This scenario needs the real onboard rig: the restart and the recovery entry are the onboard HMI.'
}

# --- 1. 装载以 UNKNOWN 结束 ---------------------------------------------------------------------------

$load = Invoke-G3UnknownLoad $Context 'G3-07R'
$demandId = $load.DemandId
$attemptId = $load.AttemptId
$first = $load.First
$sessionBefore = Get-G3Session $connection
$unlockingBefore = @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' }).Count
$waitingBefore = @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' }).Count
$slotBefore = Get-G3SlotState $simulator $load.Slot

# --- 2. 重启：报告列出待结清、补发、确认 ----------------------------------------------------------------

$restartAt = [DateTimeOffset]::UtcNow
$null = & $Context.RestartOnboard
$onboard = $Context.Onboard
$replayed = Wait-L2Condition -Description 'the restarted onboard replayed the result in its new session' `
    -Journal $journal -Criterion 'replayed-result' -TimeoutSeconds 240 `
    -Probe {
        $row = @((Get-G3Inbound $connection 'OperationResult') | Where-Object { $_.MessageId -eq $first.MessageId })[0]
        if ($null -ne $row -and $row.Generation -gt $first.Generation -and $null -ne $row.ResponseLine) { $row } else { $null }
    } -Until { param($v) $null -ne $v }
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

$report = @((Get-G3Inbound $connection 'RecoveryStateReport') | Where-Object { $_.Generation -eq $replayed.Generation })[0]
$pending = if ($null -ne $report) { @($report.Payload.pendingResults) } else { @() }
$orderOk = $null -ne $report -and $first.At -lt $restartAt -and
    $first.Response -eq 'DurableAck' -and [string]$first.ResponsePayload.acceptedContentSha256 -eq $first.ContentHash -and
    $report.At -gt $restartAt -and $report.At -lt $replayed.At -and
    $replayed.Response -eq 'DurableAck' -and [long]$replayed.ResponseLine.sessionGeneration -eq $replayed.Generation -and
    [string]$replayed.ResponsePayload.acceptedContentSha256 -eq $replayed.ContentHash -and $replayed.ContentHash -ne $first.ContentHash
$assertions.Add(
    'G3-07-01',
    '消息顺序与向量一致：结果 → DurableAck → 重启后新会话的恢复状态报告 → 同一 messageId 的结果按新会话代号补发 → 补发的 DurableAck（CV-OPERATION-RESULT-UNKNOWN-RECONCILE orderedExpectedMessages）',
    $orderOk,
    "Result(g$($first.Generation)) < Ack < RecoveryStateReport(g$($replayed.Generation)) < Result(g$($replayed.Generation), 同号) < Ack(新行哈希)",
    "Result g$($first.Generation) ack=$($first.Response) / Report $(if ($report) { "g$($report.Generation)" } else { '(none)' }) / Replay g$($replayed.Generation) ack=$($replayed.Response) / 有序=$orderOk")

$named = @($pending | Where-Object { [string]$_.messageId -eq $first.MessageId -and [string]$_.messageType -eq 'OperationResult' -and [string]$_.businessId -eq $attemptId })
$assertions.Add(
    'G3-07-02',
    '车载端如实报未知并从日志补发：结果 overallOutcome 为 UNKNOWN；重启后的报告列出未结清尝试与这份结果；补发载荷与第一份逐字段相同（REPORT_UNKNOWN_AS_UNKNOWN / REPLAY_RESULT_ON_RECONNECT）',
    ([string]$first.Payload.overallOutcome -eq 'UNKNOWN' -and $null -ne $report -and
        [string]$report.Payload.unsettledSlotOperationAttemptId -eq $attemptId -and $named.Count -eq 1 -and
        $replayed.PayloadJson -eq $first.PayloadJson),
    "UNKNOWN / unsettled=$attemptId / pendingResults 含该结果 / 载荷相同",
    "$($first.Payload.overallOutcome) / unsettled=$(if ($report) { $report.Payload.unsettledSlotOperationAttemptId } else { '(no report)' }) / pendingResults $($pending.Count) 条、含该结果 $($named.Count) / 载荷相同=$($replayed.PayloadJson -eq $first.PayloadJson)")

$resultsBeforeResume = (Invoke-L2Query -Connection $connection -Sql "SELECT OverallOutcome FROM OperationResults WHERE SlotOperationAttemptId = '$attemptId'")
$loadStatusBeforeResume = Get-G3Scalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
$stageBeforeResume = Get-G3Scalar $connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'"
$assertions.Add(
    'G3-07-03',
    '未知不当成功、不重复提交：补发之后这次操作仍只有一行 UNKNOWN 结果，装载仍 RecoveryRequired，旅程仍 Blocked（NEVER_TREAT_UNKNOWN_AS_SUCCESS / forbidden unknown-as-success、duplicate-business-commit）',
    ($resultsBeforeResume.Count -eq 1 -and [string]$resultsBeforeResume[0].OverallOutcome -eq 'UNKNOWN' -and
        $loadStatusBeforeResume -eq 'RecoveryRequired' -and $stageBeforeResume -eq 'Blocked'),
    '1 行 UNKNOWN / RecoveryRequired / Blocked',
    "$($resultsBeforeResume.Count) 行 $(if ($resultsBeforeResume.Count -ge 1) { $resultsBeforeResume[0].OverallOutcome }) / $loadStatusBeforeResume / $stageBeforeResume")

$sessionAfterReplay = Get-G3Session $connection
$assertions.Add(
    'G3-07-04',
    '服务端按报告的日志对账且不提前就绪：新会话代次大于重启前，补发之后待结清列表已清空，会话仍 RecoveryRequired（RECONCILE_FROM_REPORTED_JOURNAL / forbidden ready-before-reconciliation）',
    ([long]$sessionAfterReplay.SessionGeneration -gt [long]$sessionBefore.SessionGeneration -and
        [string]$sessionAfterReplay.PendingResultIdsJson -eq '[]' -and [string]$sessionAfterReplay.Readiness -eq 'RecoveryRequired'),
    "代次 >$($sessionBefore.SessionGeneration) / [] / RecoveryRequired",
    "代次 $($sessionAfterReplay.SessionGeneration) / $($sessionAfterReplay.PendingResultIdsJson) / $($sessionAfterReplay.Readiness) ($($sessionAfterReplay.ReasonCode))")

$unlockingAfterReplay = @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' }).Count
$slotAfterReplay = Get-G3SlotState $simulator $load.Slot
$assertions.Add(
    'G3-07-05',
    '补发不碰物理：重启与补发之后这次操作没有再开锁，装载仓仍关门、空、锁上、输出复位（forbidden duplicate-slot-unlock / NO_UNPROVEN_STATE）',
    ($unlockingAfterReplay -eq $unlockingBefore -and $slotAfterReplay -eq 'CLOSED/EMPTY/1/0' -and $slotAfterReplay -eq $slotBefore),
    "UNLOCKING $unlockingBefore 次不变 / CLOSED/EMPTY/1/0",
    "UNLOCKING $unlockingBefore → $unlockingAfterReplay / $slotBefore → $slotAfterReplay")

# --- 3. 维护人员在车上申请恢复原操作 -------------------------------------------------------------------

$resumeIds = @('G3-07-06', 'G3-07-07', 'G3-07-08', 'G3-07-09', 'G3-07-10', 'G3-07-11')
$offered = Wait-G3ButtonOffered $onboard $journal '申请恢复' 'onboard-resume-entry' 120
if (-not $offered) {
    Add-G3NotReached $assertions $resumeIds '重启后车载端没有给出「申请恢复」入口'
    return
}
$journal.Note('Maintenance presses 申请恢复 and confirms.')
$null = Invoke-G3ConfirmedButton $onboard $journal '申请恢复' '申请恢复原操作'

$resumeCommand = Wait-L2Condition -Description 'the server issued SlotOperationResumeCommand, or the onboard reported a refusal' `
    -Journal $journal -Criterion 'resume-command' -TimeoutSeconds 90 `
    -Probe {
        $commands = @((Get-G3Outbound $connection 'SlotOperationResumeCommand') | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $attemptId })
        if ($commands.Count -ge 1) { $commands[0] }
        elseif (@($onboard.WindowTitles()) -contains '恢复申请失败') { 'REFUSED' }
        else { $null }
    } -Until { param($v) $null -ne $v }
if ($resumeCommand -is [string]) {
    Add-G3NotReached $assertions $resumeIds '恢复申请未被接受（车载端弹出「恢复申请失败」）'
    return
}

$reopened = Wait-L2Condition -Description 'the onboard reopened the authorized slot and waits for the operator' `
    -Journal $journal -Criterion 'resume-waiting-operator' -TimeoutSeconds 120 `
    -Probe { @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' } | Select-Object -Skip $waitingBefore)[0] } `
    -Until { param($v) $null -ne $v }
$journal.Note("Operator loads the basket into slot $($load.Slot) and closes it.")
$null = $simulator.Command('Put', "slots/$($load.Slot)/cargo", @{ state = 'OCCUPIED' })
$null = $simulator.Command('Post', "slots/$($load.Slot)/close-door", @{})

$resumed = Wait-L2Condition -Description 'the resumed load reported its outcome and the server acknowledged it' `
    -Journal $journal -Criterion 'resumed-result' -TimeoutSeconds 180 `
    -Probe {
        @((Get-G3Inbound $connection 'OperationResult') | Where-Object {
            [string]$_.Payload.slotOperationAttemptId -eq $attemptId -and $_.MessageId -ne $first.MessageId -and $null -ne $_.ResponseLine })[0]
    } -Until { param($v) $null -ne $v }
$null = Wait-L2Condition -Description 'the resumed load committed' `
    -Journal $journal -Criterion 'load-status' -TimeoutSeconds 60 `
    -Probe { Get-G3Scalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'" } `
    -Until { param($v) $v -in @('Committed', 'RecoveryRequired') }
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

$sessionRequest = @((Get-G3Inbound $connection 'ExceptionRecoverySessionRequested') | Where-Object { [string]$_.Payload.demandId -eq $demandId })[0]
$action = @((Get-G3Inbound $connection 'RecoveryActionSubmitted') | Where-Object { [string]$_.Payload.demandId -eq $demandId })[0]
$resumeOrderOk = $null -ne $sessionRequest -and $null -ne $action -and
    $sessionRequest.Response -eq 'ExceptionRecoverySessionOpened' -and $action.Response -eq 'RecoveryActionAccepted' -and
    [string]$action.ResponsePayload.acceptedAction -eq 'RESUME_AFTER_REPAIR' -and
    $sessionRequest.At -le $action.At -and $action.At -le $resumeCommand.At -and $resumeCommand.At -lt $resumed.At
$assertions.Add(
    'G3-07-06',
    '消息顺序与向量一致：ExceptionRecoverySessionRequested → Opened → RecoveryActionSubmitted(RESUME_AFTER_REPAIR) → Accepted → SlotOperationResumeCommand → OperationResult（CV-EXCEPTION-RESUME orderedExpectedMessages）',
    $resumeOrderOk,
    'SessionRequested→Opened ≤ ActionSubmitted→Accepted(RESUME_AFTER_REPAIR) ≤ ResumeCommand < OperationResult',
    "SessionRequested→$(if ($sessionRequest) { $sessionRequest.Response } else { '(none)' }) / Action→$(if ($action) { "$($action.Response) $($action.ResponsePayload.acceptedAction)" } else { '(none)' }) / 有序=$resumeOrderOk")

$recoverySession = if ($null -ne $sessionRequest -and $sessionRequest.ResponsePayload) {
    (Invoke-L2Query -Connection $connection -Sql (
        "SELECT AdministratorId, AdministratorRole, DemandId, SlotsJson, State, SelectedAction FROM ExceptionRecoverySessions " +
        "WHERE ExceptionRecoverySessionId = '$([string]$sessionRequest.ResponsePayload.exceptionRecoverySessionId)'"))[0]
} else { $null }
$assertions.Add(
    'G3-07-07',
    '恢复会话只为已验证的管理员打开：会话记下车上配置的管理员与 MAINTENANCE_ADMINISTRATOR 角色，范围是这条需求与装载仓（OPEN_RECOVERY_SESSION_FOR_VERIFIED_ADMINISTRATOR）',
    ($null -ne $recoverySession -and [string]$recoverySession.AdministratorId -eq [string]$sessionRequest.Payload.administrator.operatorId -and
        (Test-G3Present $recoverySession.AdministratorId) -and [string]$recoverySession.AdministratorRole -eq 'MAINTENANCE_ADMINISTRATOR' -and
        [string]$recoverySession.DemandId -eq $demandId -and (Format-G3Slots ([string]$recoverySession.SlotsJson | ConvertFrom-Json)) -eq (Format-G3Slots $load.TargetSlots)),
    "管理员 $(if ($sessionRequest) { $sessionRequest.Payload.administrator.operatorId }) / MAINTENANCE_ADMINISTRATOR / $demandId / $(Format-G3Slots $load.TargetSlots)",
    $(if ($recoverySession) { "$($recoverySession.AdministratorId) / $($recoverySession.AdministratorRole) / $($recoverySession.DemandId) / $($recoverySession.SlotsJson)" } else { '(no session row)' }))

$resumeUnlocks = @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' -and $_.At -gt $resumeCommand.At })
$resumeUnlockSlots = @($resumeUnlocks | ForEach-Object { $_.Active } | ForEach-Object { [int]$_ } | Sort-Object -Unique)
$assertions.Add(
    'G3-07-08',
    '只恢复授权的范围：恢复命令指向原尝试、原仓位与已证实的检查点；恢复之后车载端只重开了这个仓，且只开一次（AUTHORIZE_RESUME_SCOPE / RESUME_ONLY_AUTHORIZED_SCOPE / forbidden expanded-active-unlock-set）',
    ([string]$resumeCommand.Payload.slotOperationAttemptId -eq $attemptId -and
        (Format-G3Slots $resumeCommand.Payload.slots) -eq (Format-G3Slots $load.TargetSlots) -and
        [string]$resumeCommand.Payload.provenRecoveryCheckpoint -in @('PREPARED', 'ACTIVE_UNLOCK_SET', 'SAFE_FINISH_REACHED') -and
        $resumeUnlocks.Count -eq 1 -and ($resumeUnlockSlots -join ',') -eq (Format-G3Slots $load.TargetSlots)),
    "attempt $attemptId / 仓 $(Format-G3Slots $load.TargetSlots) / 已证实检查点 / 恢复后开锁 1 次、仓 $(Format-G3Slots $load.TargetSlots)",
    "attempt $($resumeCommand.Payload.slotOperationAttemptId) / 仓 $(Format-G3Slots $resumeCommand.Payload.slots) / $($resumeCommand.Payload.provenRecoveryCheckpoint) / 恢复后开锁 $($resumeUnlocks.Count) 次、仓 $($resumeUnlockSlots -join ',')")

$resultRows = (Invoke-L2Query -Connection $connection -Sql (
    "SELECT ResultId, OverallOutcome, SupersededByResultId FROM OperationResults WHERE SlotOperationAttemptId = '$attemptId' ORDER BY ReceivedAt"))
$assertions.Add(
    'G3-07-09',
    '车载端报恢复后的结果：COMPLETED，服务端以它替换那份 UNKNOWN（第一行被第二行替换，第二行存活）（REPORT_RESUMED_OUTCOME）',
    ([string]$resumed.Payload.overallOutcome -eq 'COMPLETED' -and $resultRows.Count -eq 2 -and
        [string]$resultRows[0].OverallOutcome -eq 'UNKNOWN' -and [string]$resultRows[0].SupersededByResultId -eq [string]$resultRows[1].ResultId -and
        [string]$resultRows[1].ResultId -eq $resumed.MessageId -and -not (Test-G3Present $resultRows[1].SupersededByResultId)),
    'COMPLETED / UNKNOWN 被替换 → COMPLETED 存活',
    "$($resumed.Payload.overallOutcome) / $(($resultRows | ForEach-Object { "$($_.OverallOutcome)$(if (Test-G3Present $_.SupersededByResultId) { '(被替换)' })" }) -join ' → ')")

$workflow = (Invoke-L2Query -Connection $connection -Sql (
    "SELECT State FROM RecoveryWorkflows WHERE WorkflowType = 'RESUME_AFTER_REPAIR' AND SlotOperationAttemptId = '$attemptId'"))
$workflowState = if ($workflow.Count -ge 1) { [string]$workflow[0].State } else { '(none)' }
$loadStatus = Get-G3Scalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
$sessionState = if ($null -ne $sessionRequest -and $sessionRequest.ResponsePayload) {
    Get-G3Scalar $connection "SELECT State AS Value FROM ExceptionRecoverySessions WHERE ExceptionRecoverySessionId = '$([string]$sessionRequest.ResponsePayload.exceptionRecoverySessionId)'"
} else { '(none)' }
$stage = Get-G3Scalar $connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'"
$commits = Get-G3Count $connection "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load' AND Status = 'Committed'"
$assertions.Add(
    'G3-07-10',
    '恢复收敛且只提交一次：恢复工作流 Reconciled，恢复会话 CLOSED，装载 Committed 且只有这一笔，旅程离开 Blocked 继续往下走（finalState NO_DUPLICATE_COMMIT）',
    ($workflowState -eq 'Reconciled' -and $sessionState -eq 'CLOSED' -and $loadStatus -eq 'Committed' -and $commits -eq 1 -and
        $stage -notin @('Blocked', 'AwaitingLoadResult')),
    'Reconciled / CLOSED / Committed ×1 / 旅程已离开 Blocked',
    "$workflowState / $sessionState / $loadStatus ×$commits / $stage")

$slotFinal = Get-G3SlotState $simulator $load.Slot
$assertions.Add(
    'G3-07-11',
    '终态物理状态已证实：装载仓关门、有货、锁上、开锁输出复位，与车载端报的 COMPLETED 一致（finalState NO_UNPROVEN_STATE）',
    ($slotFinal -eq 'CLOSED/OCCUPIED/1/0'),
    'CLOSED/OCCUPIED/1/0',
    $slotFinal)

$journal.Note('FP-IS-07: an UNKNOWN load was reconciled across an onboard restart, resumed on authorization and committed once.')
