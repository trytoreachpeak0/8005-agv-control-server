#Requires -Version 7

<#
control-server#230（cs#36 后续）：恢复会话**已经开成**、车载端自己也记下了会话 id，随后断电。重启之后恢复
入口还在不在，补偿走不走得到对账。

**要证的机制。**车载端只在内存里留着当前恢复会话（`WireToGateBusinessService._recoverySessionSnapshot`），
日志库里只存会话 id 与动作向量。WPF 重启之后这份快照只能靠一条路拿回来：服务端在每次 `RecoveryStateReport`
之后，把「既没确认、也没被新 revision fence」的 `ExceptionRecoverySessionSnapshot` 重放进新会话
（`OnboardRecoveryCoordinator.ReplayPendingCommandsAsync`）。这条路断了，重启后的车按恢复入口会在**本地**抛
`RECOVERY_SESSION_STATE_PENDING`——日志库里有向量、内存里没有快照，车载端
`WireToGateBusinessService.RecoveryVectors.cs` 的 `RequestRecoveryActionVectorCoreAsync` 第一段就拦下来，
一条消息都不发。

**与 `real-onboard-restart-with-open-recovery-session` 的分工。**那一条让开会话的应答在路上丢掉，所以车放弃了
这次申请，日志库里**没有**会话 id，重启后走的是「快照重放回来、从零提交动作」；快照重放断了时它走到的是
「再申请被 `RECOVERY_SESSION_ALREADY_OPEN` 拒」。本条盖的是 cs#36 的另一条死路，也是现场更常见的那条：会话
真的开成了。

**会话怎么在重启时既开着、车又记着它。**v2 车载端的每个恢复入口都是一次按下连发「申请会话 → 提交动作」，中间
没有可以停下来的地方，所以「会话开成但服务端一条动作都没收到」这一格用现有工具造不出来（见文末「没有覆盖到
的一格」）。本场景让**受理的应答**在路上丢掉：按「补偿清空」之前让协议故障代理
（`tools/ControlServer.ProtocolFaultProxy`）丢一条 `RecoveryActionAccepted`、链路不断。于是

- 服务端这边：会话开了、动作受理了（会话 `ACTION_SELECTED`、revision 2、工作流 `AwaitingAuthorization`），
  补偿命令还没发——它要等车载端的 `LoadCompensationRequested` 才下发，所以服务端不会自己往前走；
- 车载端这边：`WriteRecoveryVectorPreparedAsync` 已经把会话 id 与动作 id 落了盘（那是发动作之前的一步），
  然后等满 `messageTimeoutMs` 没等到受理，弹「补偿清空失败」。

现场就是这个样子：维护人员按了「补偿清空」，受理还在路上，车没电了。

**为什么落盘这件事可以只看服务端。**车载端落盘（`WriteRecoveryVectorPreparedAsync`）在代码里先于发出
`RecoveryActionSubmitted`，所以「服务端收到了那条动作」在因果上蕴含「车载端已经落盘」。这个推断依赖当前实现的
语句顺序，所以本场景不只靠它：`L2-RAO-03` 直接读车载端日志库对一次。**这是本场景唯一一条读车载端库的判据**，
因为要证的那一格（车记着会话、内存里没有快照）本来就在车载端的持久状态里；业务事实仍然只从服务端库、模拟器快照
与代理流量记录读。两者不一致时，判据会把两边的值一起抄出来。

**重启之后按「补偿清空」走的是哪一条。**重放回来的快照 `selectedAction` 已经是 `COMPENSATE_LOAD_ALL_EMPTY`，
与这次按下的动作相同，所以车载端走 `RequestRecoveryActionVectorCoreAsync` 尾部那条早退分支：不再提交动作，直接
续发 `LoadCompensationRequested`。因此「动作只被提交过一次」是本场景的判据之一（`L2-RAO-06`），而不是巧合。

**一格顺带记下来，不是本票要改的。**重启前操作员看到的是「补偿清空失败」，而服务端其实已经受理了。此时不重启、
直接再按一次，走的也是这条早退分支，一样会成功。将来有人动早退分支的条件时，要知道它同时承载着「重启后」与
「重按」两种情形。

**没有覆盖到的一格。**「会话开成、服务端一条恢复动作都没收到，然后断电」——那要让上行的 `RecoveryActionSubmitted`
丢掉，而协议故障代理只丢服务端发往车载端方向的行（`ProtocolRelay.cs` 的 `TryDropMessage` 与 `TryDropAck` 都在
`direction == ServerToOnboard` 之下）。那一格因此不在本场景里，也不在任何场景里；扩代理不属于本票。

断言只读服务端的库（收件箱、发件箱、会话与工作流表）、模拟器快照与协议故障代理的流量记录，外加上面说明过的
`L2-RAO-03` 那一次车载端日志库读取。界面只用来驱动与等待（入口出现、「补偿清空失败」提示框），等不到时记未到达，
不进判据条件。场景没走到的判据记为未到达（FAIL），不会被跳过。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'G3RecoveryCommon.ps1')
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$onboard = $Context.Onboard
$simulator = $Context.Simulator
$connection = $Context.Connection
$proxy = $Context.ProtocolProxy

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: the restart and the recovery entry are the onboard HMI.'
}
if ($null -eq $proxy) { throw 'This scenario needs ProtocolFaultProxy = $true in its setup file.' }

$allIds = @(1..9 | ForEach-Object { 'L2-RAO-{0:D2}' -f $_ })
function Get-RaoIds([int]$From) { return , @($allIds | Select-Object -Skip ($From - 1)) }

# 严格模式下读不存在的 JSON 字段会抛；序列化器可能省掉 null 字段，所以可有可无的字段都经这里读。
function Get-RaoField([object]$Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# 不抛的等待：判据要走到的那几步超时了要记成判据，而不是一句 failureReason。
function Wait-RaoValue([string]$Criterion, [int]$TimeoutSeconds, [scriptblock]$Probe) {
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

# 这个会话的全部快照行，按入发件箱的先后。重放进新会话时同一行的 sessionGeneration 被改写
# （RefreshOutboundEnvelopeAsync），那就是「服务端把它重放了」在库里的样子。
function Get-RaoSnapshots([string]$SessionId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, PayloadJson, CreatedAt, AcknowledgedAt, FencedAt FROM ProtocolOutbox " +
        "WHERE MessageType = 'ExceptionRecoverySessionSnapshot' ORDER BY CreatedAt, MessageId")
    $mine = foreach ($row in $rows) {
        $envelope = [string]$row.PayloadJson | ConvertFrom-Json
        $payload = $envelope.payload
        if ([string](Get-RaoField $payload 'exceptionRecoverySessionId') -ne $SessionId) { continue }
        [pscustomobject]@{
            MessageId      = [string]$row.MessageId
            Generation     = [long]$envelope.sessionGeneration
            Revision       = [long](Get-RaoField $payload 'recoverySessionRevision')
            State          = [string](Get-RaoField $payload 'state')
            SelectedAction = [string](Get-RaoField $payload 'selectedAction')
            AllowedActions = @(Get-RaoField $payload 'allowedActions' | ForEach-Object { [string]$_ })
            PayloadJson    = $payload | ConvertTo-Json -Depth 20 -Compress
            Acknowledged   = Test-G3Present $row.AcknowledgedAt
            Fenced         = Test-G3Present $row.FencedAt
        }
    }
    return , @($mine)
}

function Get-RaoRecoverySessions {
    return Invoke-L2Query -Connection $connection -Sql (
        "SELECT ExceptionRecoverySessionId, RequestId, DemandId, State, SelectedAction, Revision FROM ExceptionRecoverySessions " +
        "WHERE AgvId = '$($Context.AgvId)' ORDER BY OpenedAt")
}

function Get-RaoWorkflows([string]$SessionId) {
    return Invoke-L2Query -Connection $connection -Sql (
        "SELECT WorkflowId, WorkflowType, State, CommandMessageId FROM RecoveryWorkflows " +
        "WHERE ExceptionRecoverySessionId = '$SessionId' ORDER BY CreatedAt, WorkflowId")
}

function Format-RaoSnapshots([object[]]$Snapshots) {
    return (@($Snapshots | ForEach-Object {
                "r$($_.Revision) $($_.State)/$(if ($_.SelectedAction) { $_.SelectedAction } else { '-' }) g$($_.Generation) ack=$($_.Acknowledged) fenced=$($_.Fenced)"
            }) -join ' → ')
}

# 车载端自己的日志库，只读。本场景要证的那一格就在这里（记着会话 id、内存里没有快照），所以它进 L2-RAO-03 的
# 判据，而不是像别的真装置场景那样只抄进时间线。读不到时返回 $null，判据据此记未到达。
function Get-RaoOnboardRecoveryState([string]$When) {
    try {
        $journalConnection = [Microsoft.Data.Sqlite.SqliteConnection]::new(
            "Data Source=$($Context.OnboardJournalPath);Mode=ReadOnly")
        $journalConnection.Open()
        try {
            $rows = Invoke-L2Query -Connection $journalConnection -Sql 'SELECT ContentJson FROM WireToGateRecoveryState WHERE Id = 1'
        } finally {
            $journalConnection.Dispose()
        }
        if ($rows.Count -eq 0) { $journal.Note("Onboard journal recovery state ($When): (no row)"); return $null }
        $state = [string]$rows[0].ContentJson | ConvertFrom-Json
        $vector = Get-RaoField $state 'recoveryVector'
        $result = [pscustomobject]@{
            SessionId      = [string](Get-RaoField $state 'exceptionRecoverySessionId')
            RequestId      = [string](Get-RaoField $state 'recoverySessionRequestId')
            ActionId       = [string](Get-RaoField $state 'recoveryActionId')
            VectorType     = [string](Get-RaoField $vector 'vectorType')
            VectorPrimary  = [string](Get-RaoField $vector 'primaryId')
        }
        $journal.Note(
            "Onboard journal recovery state ($When): exceptionRecoverySessionId=$($result.SessionId); " +
            "recoverySessionRequestId=$($result.RequestId); recoveryActionId=$($result.ActionId); " +
            "recoveryVector=$(if ($result.VectorType) { "$($result.VectorType)/$($result.VectorPrimary)" } else { '(none)' })")
        return $result
    } catch {
        $journal.Note("Could not read the onboard journal recovery state ($When): $($_.Exception.Message)")
        return $null
    }
}

function Format-RaoOnboardState([object]$State) {
    if ($null -eq $State) { return '(unreadable)' }
    return "会话 '$($State.SessionId)'、动作 '$($State.ActionId)'、向量 $(if ($State.VectorType) { "$($State.VectorType)/$($State.VectorPrimary)" } else { '(none)' })"
}

# --- 1. 装载以 UNKNOWN 结束 ---------------------------------------------------------------------------

$load = Invoke-G3UnknownLoad $Context 'RAO'
# 前置会重启一次车载端（control-server#128），Context.Onboard 换成了新进程的驱动；脚本开头取的那个指向已关掉的
# 窗口，什么按钮都找不到（control-server#222）。scripts/l2/Test-L2OnboardHandleAfterRestart.ps1 守这一条。
$onboard = $Context.Onboard
$demandId = $load.DemandId
$attemptId = $load.AttemptId
$loadStatus = Get-G3Scalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
$journeyBefore = "$(Get-G3Scalar $connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")/$(Get-G3Scalar $connection "SELECT BlockReasonCode AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")"
$assertions.Add(
    'L2-RAO-01',
    '前提：真车载端报 overallOutcome=UNKNOWN，服务端判装载 RecoveryRequired、旅程 Blocked',
    ([string]$load.First.Payload.overallOutcome -eq 'UNKNOWN' -and $loadStatus -eq 'RecoveryRequired' -and $journeyBefore -like 'Blocked/*'),
    'UNKNOWN / RecoveryRequired / Blocked',
    "$($load.First.Payload.overallOutcome) / $loadStatus / $journeyBefore")

# --- 2. 重启前把恢复会话开成、动作也受理：受理的应答在路上丢了 ---------------------------------------------

if (-not (Wait-G3ButtonOffered $onboard $journal '补偿清空' 'onboard-compensation-entry-before-restart' 90)) {
    Add-G3NotReached $assertions (Get-RaoIds 2) '装载停摆后车载端没有给出「补偿清空」入口，开不出恢复会话'
    return
}
$journal.Note('Arming the proxy: drop the first RecoveryActionAccepted and keep the link up.')
$null = $proxy.Command('Put', 'drop-message', @{ messageType = 'RecoveryActionAccepted'; count = 1 })
$journal.Note('Maintenance presses 补偿清空 before any restart; the session opens and the action is accepted, but the answer that says so is lost on the way.')
try {
    $null = Invoke-G3ConfirmedButton $onboard $journal '补偿清空' '补偿清空'
} catch {
    Add-G3NotReached $assertions (Get-RaoIds 2) "按「补偿清空」没有得到确认框：$($_.Exception.Message)"
    return
}

# 第一个事实：服务端开了会话、应答了，而且那条应答**没有**被丢——本场景与 cs#222 那条的分界就在这里。
$sessionRequest = Wait-RaoValue 'session-opened-answer-delivered' 90 {
    @((Get-G3Inbound $connection 'ExceptionRecoverySessionRequested') | Where-Object {
        [string]$_.Payload.demandId -eq $demandId -and $_.Response -eq 'ExceptionRecoverySessionOpened' }) |
        Select-Object -First 1
}
if ($null -eq $sessionRequest) {
    Add-G3NotReached $assertions (Get-RaoIds 2) '按「补偿清空」之后 90 秒内服务端没有开出恢复会话'
    return
}
$sessionId = [string](Get-RaoField $sessionRequest.ResponsePayload 'exceptionRecoverySessionId')

# 第二个事实：动作被受理，而受理的那条应答被代理丢了。按动作请求的 messageId 对上丢弃记录，不是随便哪一次丢弃
# （scripts/l2/README.md 第 14 条）。
$actionSubmit = Wait-RaoValue 'action-accepted-answer-dropped' 90 {
    $accepted = @((Get-G3Inbound $connection 'RecoveryActionSubmitted') | Where-Object {
        [string]$_.Payload.demandId -eq $demandId -and [string]$_.Payload.action -eq 'COMPENSATE_LOAD_ALL_EMPTY' -and
        $_.Response -eq 'RecoveryActionAccepted' })
    if ($accepted.Count -eq 0) { return $null }
    $dropped = @(@((Get-L2RealTraffic $proxy).drops) | Where-Object {
        [string]$_.acceptedMessageType -eq 'RecoveryActionAccepted' -and [string]$_.acceptedMessageId -eq $accepted[0].MessageId })
    if ($dropped.Count -ge 1) { $accepted[0] } else { $null }
}
if ($null -eq $actionSubmit) {
    Add-G3NotReached $assertions (Get-RaoIds 2) '按「补偿清空」之后 90 秒内没有看到服务端受理动作、代理丢掉那条受理应答'
    return
}

# 第三个事实：车自己放弃了这次按下（等满 messageTimeoutMs，弹「补偿清空失败」）。之后它不会再为这次按下发消息，
# 「重启后第一次按下」与「动作只提交过一次」才判得起来。提示框只是驱动与等待，不作判据。
$gaveUp = Wait-RaoValue 'onboard-gave-up-action' 120 {
    if (@($onboard.WindowTitles()) -contains '补偿清空失败') { $true } else { $null }
}
if ($null -eq $gaveUp) {
    Add-G3NotReached $assertions (Get-RaoIds 2) '受理应答被丢之后 120 秒内车载端没有放弃这次按下（没有「补偿清空失败」），「重启后第一次按下」无从判起'
    return
}
try { $null = Confirm-G3Notice $onboard '补偿清空失败' $journal } catch { $journal.Note("Dismissing 补偿清空失败 failed: $($_.Exception.Message)") }

$sessionsBefore = Get-RaoRecoverySessions
$sessionRow = @($sessionsBefore | Where-Object { [string]$_.ExceptionRecoverySessionId -eq $sessionId }) | Select-Object -First 1
$workflowsBefore = Get-RaoWorkflows $sessionId
$workflowBefore = $workflowsBefore | Select-Object -First 1
$commandsBefore = @((Get-G3Outbound $connection 'LoadCompensationCommand') | Where-Object {
    [string]$_.Payload.exceptionRecoverySessionId -eq $sessionId })
$assertions.Add(
    'L2-RAO-02',
    '重启前会话开成、动作已受理、受理应答被丢：SessionRequested → Opened 送达，ActionSubmitted(COMPENSATE_LOAD_ALL_EMPTY) → Accepted 而这条应答被代理按 messageId 丢掉（车放弃按下之后才判）；这辆车只有这一个会话行，ACTION_SELECTED、选中补偿、revision 2；工作流恰一个 AwaitingAuthorization 且还没发出补偿命令',
    ((Test-G3Present $sessionId) -and $sessionsBefore.Count -eq 1 -and $null -ne $sessionRow -and
        [string]$sessionRow.State -eq 'ACTION_SELECTED' -and
        [string]$sessionRow.SelectedAction -eq 'COMPENSATE_LOAD_ALL_EMPTY' -and [long]$sessionRow.Revision -eq 2 -and
        $workflowsBefore.Count -eq 1 -and [string]$workflowBefore.WorkflowType -eq 'COMPENSATE_LOAD_ALL_EMPTY' -and
        [string]$workflowBefore.State -eq 'AwaitingAuthorization' -and -not (Test-G3Present $workflowBefore.CommandMessageId) -and
        $commandsBefore.Count -eq 0),
    '会话行 1：ACTION_SELECTED、COMPENSATE_LOAD_ALL_EMPTY、r2 / 工作流 1：AwaitingAuthorization、无命令 / 补偿命令 0',
    "会话行 $($sessionsBefore.Count)：$(if ($sessionRow) { "$($sessionRow.State)、动作='$($sessionRow.SelectedAction)'、r$($sessionRow.Revision)" } else { '(none)' }) / " +
    "工作流 $($workflowsBefore.Count)：$(if ($workflowBefore) { "$($workflowBefore.WorkflowType)、$($workflowBefore.State)、命令='$($workflowBefore.CommandMessageId)'" } else { '(none)' }) / " +
    "补偿命令 $($commandsBefore.Count)")

if ($null -eq $sessionRow -or [string]$sessionRow.State -eq 'CLOSED') {
    Add-G3NotReached $assertions (Get-RaoIds 3) '重启前没有得到一个开着的恢复会话，走不到「会话开成后断电」这一格'
    return
}

$onboardBefore = Get-RaoOnboardRecoveryState 'before restart'
$assertions.Add(
    'L2-RAO-03',
    '断电时车载端自己已经记着这次会话：日志库 WireToGateRecoveryState 的 exceptionRecoverySessionId 等于服务端开出的会话 id，动作向量与动作 id 也已落盘。这是本场景唯一读车载端库的判据——要证的那一格（车记着会话、内存里没有快照）就在它的持久状态里',
    ($null -ne $onboardBefore -and $onboardBefore.SessionId -eq $sessionId -and
        (Test-G3Present $onboardBefore.ActionId) -and $onboardBefore.VectorType -eq 'LOAD_COMPENSATION'),
    "会话 '$sessionId'、动作 id 非空、向量 LOAD_COMPENSATION",
    (Format-RaoOnboardState $onboardBefore))

$generationBefore = [long](Get-G3Session $connection).SessionGeneration
$snapshotsBefore = Get-RaoSnapshots $sessionId
$pendingSnapshot = @($snapshotsBefore | Where-Object { -not $_.Acknowledged -and -not $_.Fenced }) | Select-Object -First 1
$assertions.Add(
    'L2-RAO-04',
    '重启前有一份可重放的快照：这个会话在发件箱里最后一份既未确认也未 fence，ACTION_SELECTED、r2、选中 COMPENSATE_LOAD_ALL_EMPTY、绑在当前会话代次；更早的 OPEN r1 已被它 fence',
    ($null -ne $pendingSnapshot -and $pendingSnapshot.State -eq 'ACTION_SELECTED' -and $pendingSnapshot.Revision -eq 2 -and
        $pendingSnapshot.SelectedAction -eq 'COMPENSATE_LOAD_ALL_EMPTY' -and $pendingSnapshot.Generation -eq $generationBefore -and
        @($snapshotsBefore | Where-Object { -not $_.Acknowledged -and -not $_.Fenced }).Count -eq 1 -and
        @($snapshotsBefore | Where-Object { $_.Revision -eq 1 -and -not $_.Fenced }).Count -eq 0),
    "待重放恰 1 份：r2 ACTION_SELECTED/COMPENSATE_LOAD_ALL_EMPTY g$generationBefore；r1 已 fence",
    "$($snapshotsBefore.Count) 份：$(Format-RaoSnapshots $snapshotsBefore)")
if ($null -eq $pendingSnapshot) {
    Add-G3NotReached $assertions (Get-RaoIds 5) '重启前发件箱里没有一份待重放的快照，重启后的车拿不回会话'
    return
}

# --- 3. 会话开着、车也记着它时断电 -----------------------------------------------------------------------

$restartAt = [DateTimeOffset]::UtcNow
$null = & $Context.RestartOnboard
$onboard = $Context.Onboard

$report = Wait-RaoValue 'restarted-recovery-state-report' 240 {
    @((Get-G3Inbound $connection 'RecoveryStateReport') | Where-Object { $_.Generation -gt $generationBefore -and $null -ne $_.ResponseLine }) |
        Select-Object -First 1
}
if ($null -eq $report) {
    Add-G3NotReached $assertions (Get-RaoIds 5) "重启后的车载端 240 秒内没有在新会话（代次 > $generationBefore）里报恢复状态"
    return
}
$replayed = Wait-RaoValue 'pending-snapshot-replayed' 60 {
    (Get-RaoSnapshots $sessionId) | Where-Object { $_.MessageId -eq $pendingSnapshot.MessageId -and $_.Generation -ge $report.Generation } |
        Select-Object -First 1
}
$snapshotsAfterReplay = Get-RaoSnapshots $sessionId
$replayedRow = $snapshotsAfterReplay | Where-Object { $_.MessageId -eq $pendingSnapshot.MessageId } | Select-Object -First 1
$assertions.Add(
    'L2-RAO-05',
    '服务端在重启后的恢复状态报告之后把那份快照重放进新会话：同一 messageId 的发件箱行改绑到新代次，载荷不变（仍 ACTION_SELECTED r2），仍未确认、未 fence，没有另起快照（ReplayPendingCommandsAsync）',
    ($report.At -gt $restartAt -and $report.Generation -gt $generationBefore -and $null -ne $replayed -and
        $replayed.Generation -eq $report.Generation -and $replayed.PayloadJson -eq $pendingSnapshot.PayloadJson -and
        -not $replayed.Acknowledged -and -not $replayed.Fenced -and
        $snapshotsAfterReplay.Count -eq $snapshotsBefore.Count),
    "RecoveryStateReport g>$generationBefore 在重启之后 / 同一行 g=报告代次、r2 ACTION_SELECTED、载荷相同、ack=False fenced=False / 快照仍 $($snapshotsBefore.Count) 份",
    "RecoveryStateReport g$($report.Generation) 重启后=$($report.At -gt $restartAt) / " +
    "$(if ($replayedRow) { "同一行 g$($replayedRow.Generation) r$($replayedRow.Revision) $($replayedRow.State) 载荷相同=$($replayedRow.PayloadJson -eq $pendingSnapshot.PayloadJson) ack=$($replayedRow.Acknowledged) fenced=$($replayedRow.Fenced)" } else { '(row gone)' }) / " +
    "快照 $($snapshotsAfterReplay.Count) 份")
$null = Get-RaoOnboardRecoveryState 'after restart, before pressing 补偿清空'

# --- 4. 重启后的车按「补偿清空」 -------------------------------------------------------------------------

function Invoke-RaoCompensation([string]$Criterion) {
    try {
        $null = Invoke-G3ConfirmedButton $onboard $journal '补偿清空' '补偿清空'
    } catch {
        $journal.Note("补偿清空 press did not raise its confirmation: $($_.Exception.Message)")
        return 'NO_CONFIRMATION'
    }
    $outcome = Wait-RaoValue $Criterion 120 {
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
$firstPress = if ($offered) { Invoke-RaoCompensation 'compensation-first-press' } else { 'NOT_OFFERED' }
$firstAccepted = $firstPress -isnot [string]
$result = if ($firstAccepted) { $firstPress } else { $null }
$retryText = ''
if (-not $firstAccepted) {
    $journal.Note("First 补偿清空 after the restart did not reach a LoadCompensationResult: $firstPress.")
    if ($firstPress -eq 'REFUSED') {
        try { $null = Confirm-G3Notice $onboard '补偿清空失败' $journal } catch { $journal.Note("Dismissing 补偿清空失败 failed: $($_.Exception.Message)") }
    }
    $null = Get-RaoOnboardRecoveryState 'after the first 补偿清空 was not accepted'
    # 只为区分「一时的」与「一直的」：判据 06 只看第一次按下。
    Start-Sleep -Seconds 10
    if (Wait-G3ButtonOffered $onboard $journal '补偿清空' 'onboard-compensation-entry-retry' 60) {
        $secondPress = Invoke-RaoCompensation 'compensation-second-press'
        if ($secondPress -is [string]) {
            $retryText = "；再按一次：$secondPress"
            if ($secondPress -eq 'REFUSED') {
                try { $null = Confirm-G3Notice $onboard '补偿清空失败' $journal } catch { }
            }
            $null = Get-RaoOnboardRecoveryState 'after the second 补偿清空 was not accepted'
        } else {
            $retryText = '；再按一次：被接受'
            $result = $secondPress
        }
    } else {
        $retryText = '；再按一次：入口 60 秒内没有再出现'
    }
}

$requestsAfterRestart = @((Get-G3Inbound $connection 'ExceptionRecoverySessionRequested') | Where-Object { $_.At -gt $restartAt })
$sessionsAfter = Get-RaoRecoverySessions
$assertions.Add(
    'L2-RAO-06',
    '重启后的恢复入口可用：第一次按「补偿清空」就走到补偿结果（没有「补偿清空失败」，即车载端没有在本地抛 RECOVERY_SESSION_STATE_PENDING）；重启后没有再申请会话；这辆车始终只有一个恢复会话行，还是重启前那一个',
    ($firstAccepted -and $requestsAfterRestart.Count -eq 0 -and $sessionsAfter.Count -eq 1 -and
        [string]$sessionsAfter[0].ExceptionRecoverySessionId -eq $sessionId),
    "第一次按下即有 LoadCompensationResult / 重启后 SessionRequested 0 / 会话行 1，仍是 $sessionId",
    "第一次按下：$(if ($firstAccepted) { '有 LoadCompensationResult' } else { $firstPress })$retryText / 重启后 SessionRequested $($requestsAfterRestart.Count)" +
    "$(if ($requestsAfterRestart.Count -ge 1) { "（$(@($requestsAfterRestart | ForEach-Object { $_.Response }) -join ',')）" }) / " +
    "会话行 $($sessionsAfter.Count)：$(@($sessionsAfter | ForEach-Object { $_.ExceptionRecoverySessionId }) -join ',')")

if ($null -eq $result) {
    Add-G3NotReached $assertions (Get-RaoIds 7) "重启后「补偿清空」没有走到 LoadCompensationResult（$firstPress$retryText）"
    $journal.Note("Recovery snapshots of session ${sessionId} at the end: $(Format-RaoSnapshots (Get-RaoSnapshots $sessionId))")
    return
}

$actionId = [string]$result.Payload.recoveryActionId
$null = Wait-RaoValue 'compensation-workflow-settled' 30 {
    $state = Get-G3Scalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'"
    if ($state -in @('Reconciled', 'RecoveryRequired')) { $state } else { $null }
}
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$readySession = Wait-RaoValue 'session-readiness-ready' 60 {
    $current = Get-G3Session $connection
    if ([string]$current.Readiness -eq 'Ready') { $current } else { $null }
}

# 重启之后车走的是「快照里已经选中了这个动作」那条早退分支：不再提交动作，直接续发补偿请求。所以这里判的是
# 动作**全程**只提交过一次，而且那一次在重启之前；重启之后只有补偿请求、命令与结果。
$actions = @((Get-G3Inbound $connection 'RecoveryActionSubmitted') | Where-Object {
    [string]$_.Payload.exceptionRecoverySessionId -eq $sessionId })
$compensateActions = @($actions | Where-Object { [string]$_.Payload.action -eq 'COMPENSATE_LOAD_ALL_EMPTY' })
$otherActions = @($actions | Where-Object { [string]$_.Payload.action -ne 'COMPENSATE_LOAD_ALL_EMPTY' })
$compensationRequests = @((Get-G3Inbound $connection 'LoadCompensationRequested') | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$commands = @((Get-G3Outbound $connection 'LoadCompensationCommand') | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$results = @((Get-G3Inbound $connection 'LoadCompensationResult') | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$orderOk = $compensateActions.Count -eq 1 -and $otherActions.Count -eq 0 -and
    $compensationRequests.Count -eq 1 -and $commands.Count -eq 1 -and $results.Count -eq 1 -and
    [string]$compensateActions[0].Payload.recoveryActionId -eq $actionId -and
    $compensateActions[0].At -lt $restartAt -and
    $restartAt -lt $compensationRequests[0].At -and
    $compensationRequests[0].At -le $commands[0].At -and $commands[0].At -lt $results[0].At -and $results[0].Response -eq 'DurableAck' -and
    [string]$commands[0].Payload.exceptionRecoverySessionId -eq $sessionId -and [string]$commands[0].Payload.slotOperationAttemptId -eq $attemptId -and
    (Format-G3Slots $commands[0].Payload.slots) -eq (Format-G3Slots $load.TargetSlots) -and
    [string]$results[0].Payload.overallOutcome -eq 'ALL_EMPTY' -and
    $compensationRequests[0].Generation -ge $report.Generation -and $results[0].Generation -ge $report.Generation
$assertions.Add(
    'L2-RAO-07',
    '动作只被提交过一次，补偿在重启后的新会话里走完：这个会话下 RecoveryActionSubmitted 恰一条、动作是 COMPENSATE_LOAD_ALL_EMPTY、发生在重启之前，重启之后不再提交；随后 LoadCompensationRequested → LoadCompensationCommand（指向这个会话、这笔装载与装载仓）→ LoadCompensationResult(ALL_EMPTY) → DurableAck 各一次、按序、都在重启之后、代次 ≥ 重启后的报告代次',
    $orderOk,
    "ActionSubmitted×1（重启前，COMPENSATE_LOAD_ALL_EMPTY，id $actionId）/ 重启后 CompensationRequested、Command、Result 各 1，按序，代次 ≥ $($report.Generation)，命令会话 $sessionId / attempt $attemptId / 仓 $(Format-G3Slots $load.TargetSlots)，ALL_EMPTY",
    "ActionSubmitted×$($compensateActions.Count)$(if ($compensateActions.Count -ge 1) { "（重启前=$($compensateActions[0].At -lt $restartAt)，id $($compensateActions[0].Payload.recoveryActionId)→$($compensateActions[0].Response)）" })" +
    "$(if ($otherActions.Count -ge 1) { "；同会话其它动作：$(@($otherActions | ForEach-Object { "$($_.Payload.action)→$($_.Response)" }) -join '，')" }) / " +
    "CompensationRequested×$($compensationRequests.Count) / Command×$($commands.Count) / Result×$($results.Count)" +
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
    'L2-RAO-08',
    '补偿走到对账且车回到 Ready：工作流 Reconciled、恢复会话 CLOSED、需求与装载 Cancelled、租约释放、旅程 Completed/CANCELLED_BY_LOAD_COMPENSATION、会话 Readiness=Ready；仓位关门、空、锁上、输出复位，补偿没有开锁。口径与 g3-exception-compensate、real-onboard-restart-with-open-recovery-session 相同',
    ($workflowState -eq 'Reconciled' -and $sessionState -eq 'CLOSED' -and $demandStatus -eq 'Cancelled' -and $loadStatusFinal -eq 'Cancelled' -and
        $released -and $journeyFinal -eq 'Completed/CANCELLED_BY_LOAD_COMPENSATION' -and $null -ne $readySession -and
        $physical -eq $expectedPhysical -and $unlocksAfterPress.Count -eq 0),
    "Reconciled / CLOSED / Cancelled / Cancelled / 释放 / Completed/CANCELLED_BY_LOAD_COMPENSATION / Ready / $expectedPhysical / 开锁 0",
    "$workflowState / $sessionState / $demandStatus / $loadStatusFinal / 释放=$released / $journeyFinal / " +
    "$($sessionFinal.Readiness)($($sessionFinal.ReasonCode)) / $physical / 开锁 $($unlocksAfterPress.Count)")

$finalSnapshots = Get-RaoSnapshots $sessionId
$nonClosed = @($finalSnapshots | Where-Object { $_.State -ne 'CLOSED' })
$closed = @($finalSnapshots | Where-Object { $_.State -eq 'CLOSED' })
$replayedFinal = $finalSnapshots | Where-Object { $_.MessageId -eq $pendingSnapshot.MessageId } | Select-Object -First 1
$closedAcked = if ($closed.Count -ge 1) { [string]$closed[0].Acknowledged } else { '(no CLOSED snapshot)' }
$journal.Note("Recovery snapshots of session ${sessionId}: $(Format-RaoSnapshots $finalSnapshots). CLOSED acknowledged: $closedAcked (not judged: only the CLOSED revision is acknowledged, control-server#31).")
$assertions.Add(
    'L2-RAO-09',
    '开着的快照从没被确认：这个会话每一份非 CLOSED 快照（含重放回来那份 ACTION_SELECTED r2）都未确认，并由下一 revision fence；最后恰有一份 CLOSED。CLOSED 是否被确认只抄在实际值里，不作判据（车载端只确认 CLOSED，control-server#31）',
    ($null -ne $replayedFinal -and $replayedFinal.State -eq 'ACTION_SELECTED' -and $replayedFinal.Revision -eq 2 -and
        $nonClosed.Count -ge 1 -and @($nonClosed | Where-Object { $_.Acknowledged -or -not $_.Fenced }).Count -eq 0 -and
        $closed.Count -eq 1 -and $finalSnapshots[-1].State -eq 'CLOSED'),
    '非 CLOSED 全部 ack=False fenced=True，重放那份 ACTION_SELECTED r2 在其中，最后一份 CLOSED',
    "$(Format-RaoSnapshots $finalSnapshots)（CLOSED ack=$closedAcked）")

$journal.Note('control-server#230: a recovery session that was opened and whose action was accepted survived a power cut through the replayed snapshot, and the compensation was carried to reconciliation without a second action.')
