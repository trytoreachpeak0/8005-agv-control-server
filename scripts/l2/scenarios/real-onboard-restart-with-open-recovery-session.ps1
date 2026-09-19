#Requires -Version 7

<#
control-server#36：开着恢复会话时重启车载端，重连之后恢复入口还在不在。

**要证的机制。**车载端只在内存里留着当前恢复会话（`WireToGateBusinessService._recoverySessionSnapshot`），日志库里只存
会话 id。WPF 重启之后这份快照只能靠一条路拿回来：服务端在每次 `RecoveryStateReport` 之后，把「既没确认、也没被新
revision fence」的 `ExceptionRecoverySessionSnapshot` 重放进新会话（`OnboardRecoveryCoordinator.ReplayPendingCommandsAsync`）。
这条路断了，重启后的车按恢复入口会抛 `RECOVERY_SESSION_STATE_PENDING`（日志库里有会话 id、内存里没有快照），
再申请又被服务端以已有开着的会话拒绝。已有的 v2 场景都是先重启、后开会话，覆盖不到这一格。

**会话怎么在重启时「开着」。**v2 车载端的每个恢复入口都是一次按下连发「申请会话 → 提交动作」，中间没有可以
停下来的地方；协议流也没有可暂停的故障代理（`ClockSkewProxy` 只转发车辆安全投影）。唯一确定的办法是让动作被
服务端拒绝、会话留在 OPEN：装载以 UNKNOWN 结束后、**重启之前**按「申请恢复」。服务端授权 `RESUME_AFTER_REPAIR`
要一个已证实的物理断点，而断点只在恢复状态报告里报上来，重启前服务端手上的还是装载之前那次握手的值，所以动作被
`PROVEN_RECOVERY_CHECKPOINT_REQUIRED` 拒绝，会话 OPEN、revision 1、没有工作流（`ValidateActionPreconditions`）。
现场就是这个样子：维护人员先按了「申请恢复」被拒，车随后断电。

**重启之后按「补偿清空」。**门关着、仓是空的，补偿不开门就能证空，服务端授权补偿只看「装载是 Load 且
RecoveryRequired」，与 `g3-exception-compensate` 同一条路，只是会话来自重启前、快照来自重放。

**快照确认这一半与主线不同。**主线车载端 onboard-hmi#41/#42 让车只确认 CLOSED 快照；v2 车载端（w2g/b3-on-v2）
没有这两个提交，对 `ExceptionRecoverySessionSnapshot` 一份都不回 `SnapshotAppliedAck`。所以这里判的是
「非 CLOSED 的快照一份都没被确认、旧 revision 由新 revision fence」；CLOSED 那一份确认没确认只抄进判据实际值，
不作判据——那是 control-server#31 在主线修掉、v2 还没带上的另一件事，不是本票要证的。

**不属于任何 G3 片**，不进 `scripts/run-journey-g3.ps1` / `scripts/g3-slice-evidence.ps1`。

断言只读服务端的库（收件箱、发件箱、会话与工作流表）与模拟器快照；车载端日志库只读、只抄进时间线作诊断。
界面只用来驱动与等待入口出现。场景没走到的判据记为未到达（FAIL），不会被跳过。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'G3RecoveryCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$onboard = $Context.Onboard
$simulator = $Context.Simulator
$connection = $Context.Connection

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: the restart and the recovery entry are the onboard HMI.'
}

$allIds = @(1..8 | ForEach-Object { 'L2-ROS-{0:D2}' -f $_ })
function Get-RosIds([int]$From) { return , @($allIds | Select-Object -Skip ($From - 1)) }

# 严格模式下读不存在的 JSON 字段会抛；序列化器可能省掉 null 字段，所以可有可无的字段都经这里读。
function Get-RosField([object]$Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# 不抛的等待：判据要走到的那几步超时了要记成判据，而不是一句 failureReason。
function Wait-RosValue([string]$Criterion, [int]$TimeoutSeconds, [scriptblock]$Probe) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $value = $null
        try { $value = & $Probe } catch { $value = $null }
        $journal.Observe($Criterion, $(if ($null -eq $value) { 'waiting' } else { 'reached' }), $null)
        if ($null -ne $value) { return $value }
        if ([DateTimeOffset]::UtcNow -ge $deadline) { return $null }
        Start-Sleep -Milliseconds 500
    }
}

# 这个会话的全部快照行，按入发件箱的先后。重放进新会话时同一行的 sessionGeneration 被改写（RefreshOutboundEnvelopeAsync），
# 那就是「服务端把它重放了」在库里的样子。
function Get-RosSnapshots([string]$SessionId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, PayloadJson, CreatedAt, AcknowledgedAt, FencedAt FROM ProtocolOutbox " +
        "WHERE MessageType = 'ExceptionRecoverySessionSnapshot' ORDER BY CreatedAt, MessageId")
    $mine = foreach ($row in $rows) {
        $envelope = [string]$row.PayloadJson | ConvertFrom-Json
        $payload = $envelope.payload
        if ([string](Get-RosField $payload 'exceptionRecoverySessionId') -ne $SessionId) { continue }
        [pscustomobject]@{
            MessageId      = [string]$row.MessageId
            Generation     = [long]$envelope.sessionGeneration
            Revision       = [long](Get-RosField $payload 'recoverySessionRevision')
            State          = [string](Get-RosField $payload 'state')
            SelectedAction = [string](Get-RosField $payload 'selectedAction')
            AllowedActions = @(Get-RosField $payload 'allowedActions' | ForEach-Object { [string]$_ })
            PayloadJson    = $payload | ConvertTo-Json -Depth 20 -Compress
            Acknowledged   = Test-G3Present $row.AcknowledgedAt
            Fenced         = Test-G3Present $row.FencedAt
        }
    }
    return , @($mine)
}

function Get-RosRecoverySessions {
    return Invoke-L2Query -Connection $connection -Sql (
        "SELECT ExceptionRecoverySessionId, RequestId, DemandId, State, SelectedAction, Revision FROM ExceptionRecoverySessions " +
        "WHERE AgvId = '$($Context.AgvId)' ORDER BY OpenedAt")
}

function Format-RosSnapshots([object[]]$Snapshots) {
    return (@($Snapshots | ForEach-Object { "r$($_.Revision) $($_.State) g$($_.Generation) ack=$($_.Acknowledged) fenced=$($_.Fenced)" }) -join ' → ')
}

# 车载端自己的日志库，只读。里面的恢复状态解释得了入口为什么被拒（会话 id、动作 id 是不是还挂着），但不作判据。
function Write-RosOnboardRecoveryState([string]$When) {
    try {
        $journalConnection = [Microsoft.Data.Sqlite.SqliteConnection]::new(
            "Data Source=$($Context.OnboardJournalPath);Mode=ReadOnly")
        $journalConnection.Open()
        try {
            $rows = Invoke-L2Query -Connection $journalConnection -Sql 'SELECT ContentJson FROM WireToGateRecoveryState WHERE Id = 1'
        } finally {
            $journalConnection.Dispose()
        }
        if ($rows.Count -eq 0) { $journal.Note("Onboard journal recovery state ($When): (no row)"); return }
        $state = [string]$rows[0].ContentJson | ConvertFrom-Json
        $fields = foreach ($name in @('unsettledSlotOperationAttemptId', 'provenRecoveryCheckpoint', 'recoverySessionRequestId',
                'exceptionRecoverySessionId', 'recoveryActionId', 'recoveryActionRequestId')) {
            "$name=$(Get-RosField $state $name)"
        }
        $vector = Get-RosField $state 'recoveryVector'
        $vectorText = if ($null -eq $vector) { '(none)' } else { "$(Get-RosField $vector 'vectorType')/$(Get-RosField $vector 'primaryId')" }
        $journal.Note("Onboard journal recovery state ($When): $($fields -join '; '); recoveryVector=$vectorText")
    } catch {
        $journal.Note("Could not read the onboard journal recovery state ($When): $($_.Exception.Message)")
    }
}

# --- 1. 装载以 UNKNOWN 结束 ---------------------------------------------------------------------------

$load = Invoke-G3UnknownLoad $Context 'ROS'
# 前置会重启一次车载端（control-server#128），Context.Onboard 换成了新进程的驱动；脚本开头取的那个指向已关掉的窗口，
# 什么按钮都找不到，「申请恢复」因此永远等不到（control-server#222）。
$onboard = $Context.Onboard
$demandId = $load.DemandId
$attemptId = $load.AttemptId
$loadStatus = Get-G3Scalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
$journeyBefore = "$(Get-G3Scalar $connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")/$(Get-G3Scalar $connection "SELECT BlockReasonCode AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")"
$assertions.Add(
    'L2-ROS-01',
    '前提：真车载端报 overallOutcome=UNKNOWN，服务端判装载 RecoveryRequired、旅程 Blocked',
    ([string]$load.First.Payload.overallOutcome -eq 'UNKNOWN' -and $loadStatus -eq 'RecoveryRequired' -and $journeyBefore -like 'Blocked/*'),
    'UNKNOWN / RecoveryRequired / Blocked',
    "$($load.First.Payload.overallOutcome) / $loadStatus / $journeyBefore")

# --- 2. 重启前开出恢复会话：「申请恢复」被拒，会话留在 OPEN ------------------------------------------------

if (-not (Wait-G3ButtonOffered $onboard $journal '申请恢复' 'onboard-resume-entry-before-restart' 90)) {
    Add-G3NotReached $assertions (Get-RosIds 2) '装载停摆后车载端没有给出「申请恢复」入口，开不出恢复会话'
    return
}
$journal.Note('Maintenance presses 申请恢复 before any restart; the server has no proven checkpoint yet and should refuse the action, leaving the session OPEN.')
try {
    $null = Invoke-G3ConfirmedButton $onboard $journal '申请恢复' '申请恢复原操作'
} catch {
    Add-G3NotReached $assertions (Get-RosIds 2) "按「申请恢复」没有得到确认框：$($_.Exception.Message)"
    return
}

$resumeAnswer = Wait-RosValue 'resume-action-answered' 90 {
    $answeredActions = @((Get-G3Inbound $connection 'RecoveryActionSubmitted') | Where-Object {
        [string]$_.Payload.demandId -eq $demandId -and $null -ne $_.ResponseLine })
    $answeredRequests = @((Get-G3Inbound $connection 'ExceptionRecoverySessionRequested') | Where-Object {
        [string]$_.Payload.demandId -eq $demandId -and $null -ne $_.ResponseLine })
    if ($answeredActions.Count -ge 1) { $answeredActions[0] }
    elseif ($answeredRequests.Count -ge 1 -and $answeredRequests[0].Response -ne 'ExceptionRecoverySessionOpened') { $answeredRequests[0] }
    else { $null }
}
if ($null -eq $resumeAnswer) {
    Add-G3NotReached $assertions (Get-RosIds 2) '按「申请恢复」之后 90 秒内服务端没有收到并应答恢复动作'
    return
}
try { $null = Confirm-G3Notice $onboard '恢复申请失败' $journal } catch { $journal.Note("Dismissing 恢复申请失败 failed: $($_.Exception.Message)") }

$sessionRequest = @((Get-G3Inbound $connection 'ExceptionRecoverySessionRequested') | Where-Object { [string]$_.Payload.demandId -eq $demandId }) | Select-Object -First 1
$sessionId = if ($null -ne $sessionRequest -and $sessionRequest.Response -eq 'ExceptionRecoverySessionOpened') {
    [string](Get-RosField $sessionRequest.ResponsePayload 'exceptionRecoverySessionId')
} else { '' }
$resumeAction = @((Get-G3Inbound $connection 'RecoveryActionSubmitted') | Where-Object { [string]$_.Payload.demandId -eq $demandId }) | Select-Object -First 1
$resumeProblem = if ($null -ne $resumeAction) { [string](Get-RosField (Get-RosField $resumeAction.ResponsePayload 'problem') 'reasonCode') } else { '' }
$sessionsBefore = Get-RosRecoverySessions
$sessionRow = @($sessionsBefore | Where-Object { [string]$_.ExceptionRecoverySessionId -eq $sessionId }) | Select-Object -First 1
$workflowsBefore = if (Test-G3Present $sessionId) {
    Get-G3Count $connection "SELECT COUNT(*) AS Total FROM RecoveryWorkflows WHERE ExceptionRecoverySessionId = '$sessionId'"
} else { -1 }
$assertions.Add(
    'L2-ROS-02',
    '重启前服务端有一个开着的恢复会话：SessionRequested → Opened；RESUME_AFTER_REPAIR 因没有已证实断点被拒（PROVEN_RECOVERY_CHECKPOINT_REQUIRED）；这辆车只有这一个会话行，OPEN、未选动作、revision 1、没有工作流',
    ((Test-G3Present $sessionId) -and $null -ne $resumeAction -and
        $resumeAction.Response -eq 'RecoveryActionRejected' -and [string]$resumeAction.Payload.action -eq 'RESUME_AFTER_REPAIR' -and
        $resumeProblem -eq 'PROVEN_RECOVERY_CHECKPOINT_REQUIRED' -and $sessionsBefore.Count -eq 1 -and $null -ne $sessionRow -and
        [string]$sessionRow.State -eq 'OPEN' -and -not (Test-G3Present $sessionRow.SelectedAction) -and [long]$sessionRow.Revision -eq 1 -and
        $workflowsBefore -eq 0),
    'Opened / RESUME_AFTER_REPAIR→RecoveryActionRejected(PROVEN_RECOVERY_CHECKPOINT_REQUIRED) / 会话行 1：OPEN、无动作、r1 / 工作流 0',
    "$(if ($sessionRequest) { $sessionRequest.Response } else { '(no session request)' }) / " +
    "$(if ($resumeAction) { "$($resumeAction.Payload.action)→$($resumeAction.Response)($resumeProblem)" } else { '(no action)' }) / " +
    "会话行 $($sessionsBefore.Count)：$(if ($sessionRow) { "$($sessionRow.State)、动作='$($sessionRow.SelectedAction)'、r$($sessionRow.Revision)" } else { '(none)' }) / 工作流 $workflowsBefore")

if ($null -eq $sessionRow -or [string]$sessionRow.State -ne 'OPEN') {
    Add-G3NotReached $assertions (Get-RosIds 3) '重启前没有得到一个 OPEN 的恢复会话，走不到「开着会话重启」这一格'
    return
}

$generationBefore = [long](Get-G3Session $connection).SessionGeneration
$snapshotsBefore = Get-RosSnapshots $sessionId
$openSnapshot = $snapshotsBefore | Select-Object -First 1
$assertions.Add(
    'L2-ROS-03',
    '重启前那份 OPEN 快照可重放：这个会话在发件箱只有一份快照，OPEN、r1、允许 COMPENSATE_LOAD_ALL_EMPTY、绑在当前会话代次，未确认、未 fence',
    ($snapshotsBefore.Count -eq 1 -and $openSnapshot.State -eq 'OPEN' -and $openSnapshot.Revision -eq 1 -and
        $openSnapshot.AllowedActions -contains 'COMPENSATE_LOAD_ALL_EMPTY' -and $openSnapshot.Generation -eq $generationBefore -and
        -not $openSnapshot.Acknowledged -and -not $openSnapshot.Fenced),
    "1 份：r1 OPEN g$generationBefore ack=False fenced=False，允许动作含 COMPENSATE_LOAD_ALL_EMPTY",
    "$($snapshotsBefore.Count) 份：$(Format-RosSnapshots $snapshotsBefore)$(if ($openSnapshot) { "，允许动作 $($openSnapshot.AllowedActions -join ',')" })")
if ($null -eq $openSnapshot) {
    Add-G3NotReached $assertions (Get-RosIds 4) '重启前发件箱里没有这个会话的快照'
    return
}
Write-RosOnboardRecoveryState 'before restart'

# --- 3. 会话开着时重启车载端 ----------------------------------------------------------------------------

$restartAt = [DateTimeOffset]::UtcNow
$null = & $Context.RestartOnboard
$onboard = $Context.Onboard

$report = Wait-RosValue 'restarted-recovery-state-report' 240 {
    @((Get-G3Inbound $connection 'RecoveryStateReport') | Where-Object { $_.Generation -gt $generationBefore -and $null -ne $_.ResponseLine }) |
        Select-Object -First 1
}
if ($null -eq $report) {
    Add-G3NotReached $assertions (Get-RosIds 4) "重启后的车载端 240 秒内没有在新会话（代次 > $generationBefore）里报恢复状态"
    return
}
$replayed = Wait-RosValue 'open-snapshot-replayed' 60 {
    (Get-RosSnapshots $sessionId) | Where-Object { $_.MessageId -eq $openSnapshot.MessageId -and $_.Generation -ge $report.Generation } |
        Select-Object -First 1
}
$snapshotsAfterReplay = Get-RosSnapshots $sessionId
$replayedRow = $snapshotsAfterReplay | Where-Object { $_.MessageId -eq $openSnapshot.MessageId } | Select-Object -First 1
$assertions.Add(
    'L2-ROS-04',
    '服务端在重启后的恢复状态报告之后把那份 OPEN 快照重放进新会话：同一 messageId 的发件箱行改绑到新代次，载荷不变（仍 OPEN r1），仍未确认、未 fence，没有另起快照（ReplayPendingCommandsAsync）',
    ($report.At -gt $restartAt -and $report.Generation -gt $generationBefore -and $null -ne $replayed -and
        $replayed.Generation -eq $report.Generation -and $replayed.PayloadJson -eq $openSnapshot.PayloadJson -and
        -not $replayed.Acknowledged -and -not $replayed.Fenced -and $snapshotsAfterReplay.Count -eq 1),
    "RecoveryStateReport g>$generationBefore 在重启之后 / 同一行 g=报告代次、r1 OPEN、载荷相同、ack=False fenced=False / 快照 1 份",
    "RecoveryStateReport g$($report.Generation) 重启后=$($report.At -gt $restartAt) / " +
    "$(if ($replayedRow) { "同一行 g$($replayedRow.Generation) r$($replayedRow.Revision) $($replayedRow.State) 载荷相同=$($replayedRow.PayloadJson -eq $openSnapshot.PayloadJson) ack=$($replayedRow.Acknowledged) fenced=$($replayedRow.Fenced)" } else { '(row gone)' }) / " +
    "快照 $($snapshotsAfterReplay.Count) 份")
Write-RosOnboardRecoveryState 'after restart, before pressing 补偿清空'

# --- 4. 重启后的车按「补偿清空」 -------------------------------------------------------------------------

function Invoke-RosCompensation([string]$Criterion) {
    try {
        $null = Invoke-G3ConfirmedButton $onboard $journal '补偿清空' '补偿清空'
    } catch {
        $journal.Note("补偿清空 press did not raise its confirmation: $($_.Exception.Message)")
        return 'NO_CONFIRMATION'
    }
    $outcome = Wait-RosValue $Criterion 120 {
        $received = @((Get-G3Inbound $connection 'LoadCompensationResult') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
        if ($received.Count -ge 1) { $received[0] }
        elseif (@($onboard.WindowTitles()) -contains '补偿清空失败') { 'REFUSED' }
        else { $null }
    }
    if ($null -eq $outcome) { return 'NO_ANSWER' }
    return $outcome
}

$offered = Wait-G3ButtonOffered $onboard $journal '补偿清空' 'onboard-compensation-entry-after-restart' 120
$pressAt = [DateTimeOffset]::UtcNow
$firstPress = if ($offered) { Invoke-RosCompensation 'compensation-first-press' } else { 'NOT_OFFERED' }
$firstAccepted = $firstPress -isnot [string]
$result = if ($firstAccepted) { $firstPress } else { $null }
$retryText = ''
if (-not $firstAccepted) {
    $journal.Note("First 补偿清空 after the restart did not reach a LoadCompensationResult: $firstPress.")
    if ($firstPress -eq 'REFUSED') {
        try { $null = Confirm-G3Notice $onboard '补偿清空失败' $journal } catch { $journal.Note("Dismissing 补偿清空失败 failed: $($_.Exception.Message)") }
    }
    Write-RosOnboardRecoveryState 'after the first 补偿清空 was not accepted'
    # 只为区分「一时的」与「一直的」：判据 05 只看第一次按下。
    Start-Sleep -Seconds 10
    if (Wait-G3ButtonOffered $onboard $journal '补偿清空' 'onboard-compensation-entry-retry' 60) {
        $secondPress = Invoke-RosCompensation 'compensation-second-press'
        if ($secondPress -is [string]) {
            $retryText = "；再按一次：$secondPress"
            if ($secondPress -eq 'REFUSED') {
                try { $null = Confirm-G3Notice $onboard '补偿清空失败' $journal } catch { }
            }
            Write-RosOnboardRecoveryState 'after the second 补偿清空 was not accepted'
        } else {
            $retryText = '；再按一次：被接受'
            $result = $secondPress
        }
    } else {
        $retryText = '；再按一次：入口 60 秒内没有再出现'
    }
}

$requestsAfterRestart = @((Get-G3Inbound $connection 'ExceptionRecoverySessionRequested') | Where-Object { $_.At -gt $restartAt })
$sessionsAfter = Get-RosRecoverySessions
$compensateActions = @((Get-G3Inbound $connection 'RecoveryActionSubmitted') | Where-Object {
    [string]$_.Payload.demandId -eq $demandId -and [string]$_.Payload.action -eq 'COMPENSATE_LOAD_ALL_EMPTY' })
$firstCompensate = $compensateActions | Select-Object -First 1
$assertions.Add(
    'L2-ROS-05',
    '重启后的恢复入口可用：第一次按「补偿清空」就走到补偿结果（没有「补偿清空失败」即没有 RECOVERY_SESSION_STATE_PENDING 一类的拒绝）；重启后没有再申请会话、没有 ALREADY_OPEN 一类的会话拒绝；COMPENSATE_LOAD_ALL_EMPTY 挂在重启前那个会话上、在新代次里被 Accepted；这辆车始终只有一个恢复会话',
    ($firstAccepted -and $requestsAfterRestart.Count -eq 0 -and $sessionsAfter.Count -eq 1 -and $compensateActions.Count -eq 1 -and
        $firstCompensate.Response -eq 'RecoveryActionAccepted' -and [string]$firstCompensate.Payload.exceptionRecoverySessionId -eq $sessionId -and
        $firstCompensate.Generation -ge $report.Generation -and $firstCompensate.At -gt $restartAt),
    "第一次按下即有 LoadCompensationResult / 重启后 SessionRequested 0 / 会话行 1 / COMPENSATE×1 → Accepted，会话 $sessionId，g≥$($report.Generation)",
    "第一次按下：$(if ($firstAccepted) { '有 LoadCompensationResult' } else { $firstPress })$retryText / 重启后 SessionRequested $($requestsAfterRestart.Count)" +
    "$(if ($requestsAfterRestart.Count -ge 1) { "（$(@($requestsAfterRestart | ForEach-Object { $_.Response }) -join ',')）" }) / 会话行 $($sessionsAfter.Count) / " +
    "COMPENSATE×$($compensateActions.Count)$(if ($firstCompensate) { " → $($firstCompensate.Response)，会话 $($firstCompensate.Payload.exceptionRecoverySessionId)，g$($firstCompensate.Generation)" })")

if ($null -eq $result) {
    Add-G3NotReached $assertions (Get-RosIds 6) "重启后「补偿清空」没有走到 LoadCompensationResult（$firstPress$retryText）"
    $journal.Note("Recovery snapshots of session $sessionId at the end: $(Format-RosSnapshots (Get-RosSnapshots $sessionId))")
    return
}

$actionId = [string]$result.Payload.recoveryActionId
$null = Wait-RosValue 'compensation-workflow-settled' 30 {
    $state = Get-G3Scalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'"
    if ($state -in @('Reconciled', 'RecoveryRequired')) { $state } else { $null }
}
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$readySession = Wait-RosValue 'session-readiness-ready' 60 {
    $current = Get-G3Session $connection
    if ([string]$current.Readiness -eq 'Ready') { $current } else { $null }
}

# 按 recoveryActionId 与动作一起选。车载端的动作 id 跨按下保留（服务端按它去重、被拒时不落任何行），所以重启前被拒的
# 那次 RESUME_AFTER_REPAIR 与这次补偿带着同一个 recoveryActionId，只是 messageId 不同（ros-002，2026-09-14）。
$sameIdActions = @((Get-G3Inbound $connection 'RecoveryActionSubmitted') | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$actions = @($sameIdActions | Where-Object { [string]$_.Payload.action -eq 'COMPENSATE_LOAD_ALL_EMPTY' })
$otherSameIdActions = @($sameIdActions | Where-Object { [string]$_.Payload.action -ne 'COMPENSATE_LOAD_ALL_EMPTY' })
$compensationRequests = @((Get-G3Inbound $connection 'LoadCompensationRequested') | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$commands = @((Get-G3Outbound $connection 'LoadCompensationCommand') | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$results = @((Get-G3Inbound $connection 'LoadCompensationResult') | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$orderOk = $actions.Count -eq 1 -and $compensationRequests.Count -eq 1 -and $commands.Count -eq 1 -and $results.Count -eq 1 -and
    $actions[0].Response -eq 'RecoveryActionAccepted' -and [string](Get-RosField $actions[0].ResponsePayload 'acceptedAction') -eq 'COMPENSATE_LOAD_ALL_EMPTY' -and
    $restartAt -lt $actions[0].At -and $actions[0].At -le $compensationRequests[0].At -and
    $compensationRequests[0].At -le $commands[0].At -and $commands[0].At -lt $results[0].At -and $results[0].Response -eq 'DurableAck' -and
    [string]$commands[0].Payload.exceptionRecoverySessionId -eq $sessionId -and [string]$commands[0].Payload.slotOperationAttemptId -eq $attemptId -and
    (Format-G3Slots $commands[0].Payload.slots) -eq (Format-G3Slots $load.TargetSlots) -and
    [string]$results[0].Payload.overallOutcome -eq 'ALL_EMPTY' -and
    $actions[0].Generation -ge $report.Generation -and $compensationRequests[0].Generation -ge $report.Generation -and $results[0].Generation -ge $report.Generation -and
    @($otherSameIdActions | Where-Object { $_.Response -ne 'RecoveryActionRejected' -or $_.At -gt $restartAt }).Count -eq 0
$assertions.Add(
    'L2-ROS-06',
    '补偿在重启后的新会话里按向量走完，各一次：ActionSubmitted(COMPENSATE_LOAD_ALL_EMPTY) → Accepted → LoadCompensationRequested → LoadCompensationCommand（指向重启前那个会话、这笔装载与装载仓）→ LoadCompensationResult(ALL_EMPTY) → DurableAck；同一 recoveryActionId 下别的动作只能是重启前被拒的那次',
    $orderOk,
    "各 1，重启之后按序，代次 ≥ $($report.Generation)，命令会话 $sessionId / attempt $attemptId / 仓 $(Format-G3Slots $load.TargetSlots)，ALL_EMPTY；同 id 其它动作全是重启前的 RecoveryActionRejected",
    "同 id 其它动作：$(if ($otherSameIdActions.Count -eq 0) { '无' } else { (@($otherSameIdActions | ForEach-Object { "$($_.Payload.action)→$($_.Response)(g$($_.Generation)，重启前=$($_.At -lt $restartAt))" }) -join '，') }) / " +
    "Action×$($actions.Count) / CompensationRequested×$($compensationRequests.Count) / Command×$($commands.Count) / Result×$($results.Count)" +
    "$(if ($results.Count -ge 1) { " $($results[0].Payload.overallOutcome)→$($results[0].Response)" })" +
    "$(if ($commands.Count -ge 1) { " / 命令会话 $($commands[0].Payload.exceptionRecoverySessionId) 仓 $(Format-G3Slots $commands[0].Payload.slots)" }) / 有序=$orderOk")

$workflowState = Get-G3Scalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'"
$sessionState = Get-G3Scalar $connection "SELECT State AS Value FROM ExceptionRecoverySessions WHERE ExceptionRecoverySessionId = '$sessionId'"
$demandStatus = Get-G3Scalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
$loadStatusFinal = Get-G3Scalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
$journeyFinal = "$(Get-G3Scalar $connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")/$(Get-G3Scalar $connection "SELECT BlockReasonCode AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")"
$released = Test-G3Present (Get-G3Scalar $connection "SELECT ReleasedAt AS Value FROM VehicleDispatchLeases WHERE DemandId = '$demandId'")
$sessionFinal = Get-G3Session $connection
$physical = ($load.TargetSlots | Sort-Object | ForEach-Object { "$_=$(Get-G3SlotState $simulator $_)" }) -join ' '
$expectedPhysical = ($load.TargetSlots | Sort-Object | ForEach-Object { "$_=CLOSED/EMPTY/1/0" }) -join ' '
$unlocksAfterPress = @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' -and $_.At -gt $pressAt })
$assertions.Add(
    'L2-ROS-07',
    '补偿走到对账且车回到 Ready：工作流 Reconciled、恢复会话 CLOSED、需求与装载 Cancelled、租约释放、旅程 Completed/CANCELLED_BY_LOAD_COMPENSATION、会话 Readiness=Ready；仓位关门、空、锁上、输出复位，补偿没有开锁',
    ($workflowState -eq 'Reconciled' -and $sessionState -eq 'CLOSED' -and $demandStatus -eq 'Cancelled' -and $loadStatusFinal -eq 'Cancelled' -and
        $released -and $journeyFinal -eq 'Completed/CANCELLED_BY_LOAD_COMPENSATION' -and $null -ne $readySession -and
        $physical -eq $expectedPhysical -and $unlocksAfterPress.Count -eq 0),
    "Reconciled / CLOSED / Cancelled / Cancelled / 释放 / Completed/CANCELLED_BY_LOAD_COMPENSATION / Ready / $expectedPhysical / 开锁 0",
    "$workflowState / $sessionState / $demandStatus / $loadStatusFinal / 释放=$released / $journeyFinal / " +
    "$($sessionFinal.Readiness)($($sessionFinal.ReasonCode)) / $physical / 开锁 $($unlocksAfterPress.Count)")

$finalSnapshots = Get-RosSnapshots $sessionId
$nonClosed = @($finalSnapshots | Where-Object { $_.State -ne 'CLOSED' })
$closed = @($finalSnapshots | Where-Object { $_.State -eq 'CLOSED' })
$openFinal = $finalSnapshots | Where-Object { $_.MessageId -eq $openSnapshot.MessageId } | Select-Object -First 1
$closedAcked = if ($closed.Count -ge 1) { [string]$closed[0].Acknowledged } else { '(no CLOSED snapshot)' }
$journal.Note("Recovery snapshots of session ${sessionId}: $(Format-RosSnapshots $finalSnapshots). CLOSED acknowledged: $closedAcked (not judged: the v2 onboard carries no SnapshotAppliedAck for recovery sessions).")
$assertions.Add(
    'L2-ROS-08',
    '开着的快照从没被确认：这个会话每一份非 CLOSED 快照（含重启前那份 OPEN r1）都未确认，并由下一 revision fence；最后恰有一份 CLOSED。CLOSED 是否被确认只抄在实际值里，不作判据（v2 车载端未带 onboard-hmi#41/#42）',
    ($null -ne $openFinal -and $openFinal.State -eq 'OPEN' -and $openFinal.Revision -eq 1 -and
        $nonClosed.Count -ge 1 -and @($nonClosed | Where-Object { $_.Acknowledged -or -not $_.Fenced }).Count -eq 0 -and
        $closed.Count -eq 1 -and $finalSnapshots[-1].State -eq 'CLOSED'),
    '非 CLOSED 全部 ack=False fenced=True，OPEN r1 在其中，最后一份 CLOSED',
    "$(Format-RosSnapshots $finalSnapshots)（CLOSED ack=$closedAcked）")

$journal.Note('control-server#36: a recovery session left OPEN across an onboard restart came back through the replayed snapshot and was compensated to reconciliation.')
