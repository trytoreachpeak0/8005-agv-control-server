#Requires -Version 7

<#
真车载端的装货结果已经被服务端收下，`DurableAck` 却在回车上的路上丢了：车重连之后要补发同一条 `OperationResult`，
服务端要按首次受理重签确认、不掐连接，而且补发之后同一条连接上照常走完握手、会话回到 `Ready`。

**来源与成对的两端票。**`trytoreachpeak0/8005-agv-program#61` 审计第 4 节 ①：服务端 `e90e924e`（v2 上是
control-server#77，批次5-08）与车载端 `3ecb490`＋`a56a59d`（v2 上是 onboard-hmi#69，批次5-14）「必须一起过 L2
durable-ack-lost 场景」。MVP 线的参照是 `ControlServer_MVP` 同名场景：control-server#33 的 `-003` 红（补发之后车等
不到 readiness、超时再重连一次），`-004`／`-005` 绿；control-server#30 是更早的一格（服务端把只差
`sessionGeneration` 的补发判内容冲突、掐连接）。

**v2 的判据不照搬 MVP 的那一份。**两端在 v2 上各改了什么，读代码定下来的：
- 车载端（`WireToGateSessionClient.ConnectAndRecoverAsync`，onboard-hmi#69）：重连后 `SessionHello` →
  `SessionAccepted` → 逐条补发没被确认的持久报文（原 `messageId`、原 `sentAt`，只换 `sessionGeneration`）→ 能力快照
  → 安全快照 → 告警快照 → **一份新 `messageId` 的 `RecoveryStateReport`** → 等 `SessionReadiness`。唯一不补发的持久
  报文是 `RecoveryStateReport`：没被确认的旧报告由新报告取代（program#56 对 control-server#33 的结论，
  `CV-SESSION-RECONNECT-DURING-RECOVERY`；cs#87 的 `$recoveryResubmitPass` 按同一条改写）。所以 `OperationResult`
  仍按原编号补发，恢复报告不按原编号补发——两条判据都在下面。
- 服务端（`OnboardMessageProcessor`，control-server#77）：只差 `sessionGeneration` 的持久报文补发按首次受理重签
  `DurableAck`，判等只在收件箱一处。

**丢 ack 靠 `tools/ControlServer.ProtocolFaultProxy`**：车载端连代理，代理逐行转发；场景布下「丢一次
`acceptedMessageType = OperationResult` 的 `DurableAck`」，代理在服务端写出那条 ack 时不转发、两头都断。服务端那一侧
的提交是真的，车那一侧没收到 ack 也是真的——从两端看，这就是提交之后链路掉了。

业务链路与 `real-onboard-normal-load` 相同，只在装货结果那一处注入。出厂配置，不开恢复入口。`L2-DA-00`～`08` 只读三处：
服务端库、代理的 `/snapshot`（每条连接上走过哪些报文，只有信封身份）、模拟器的 `/snapshot`。

**唯一读车载端界面的是 `L2-DA-09`**（onboard-hmi#124，由该票越界、调度授权）。丢掉的是一份**已完成**装货的确认：
车重连、补发被确认之后，车载端曾在下一次开锁之前一直把这次装货投影成「上次装货操作未完成……需要管理员恢复」
（`RecoveryRequired`，连带 `ONBOARD_SLOT_OPERATION_UNFINISHED`）。这件事服务端与代理都看不见——服务端的告警快照按车
只存最新一份，收尾时早被下一单覆盖——所以从会话重回 `Ready` 起、到卸货的等操作员为止，经 UIA 轮询 HMI 窗口里每个元素
的名字（WPF 文本元素的 UIA 名字就是显示的文字；这句话同时进当前操作指引与日志列表）。服务端告警快照那一行只在同一窗口里
顺带读、记进 journal 作诊断，不作判据：它的上报节奏不归这里管，拿它判会成为假红源。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2HmiPhraseWatch.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$simulator = $Context.Simulator
$connection = $Context.Connection
$proxy = $Context.ProtocolProxy

if ($null -eq $proxy) { throw 'This scenario needs ProtocolFaultProxy = $true in its setup file.' }

function Get-ProxyLines([string]$direction, [string]$messageType) {
    return , @(@((Get-L2RealTraffic $proxy).lines) | Where-Object { $_.direction -eq $direction -and $_.messageType -eq $messageType })
}

<#
`L2-DA-09` 的取样（onboard-hmi#124；judgement 与计数在 control-server#204 收紧）。每调用一次扫一遍 HMI 主窗口的全部元素，
记下名字里含「上次装货操作未完成」的；顺带读服务端告警快照那一行，只作诊断。

**只有读全了的那一轮才算一次「看」**（`L2HmiPhraseWatch.psm1`）：这条判据否定「那句话出现过」，而支撑这句否定的
全部证据就是扫了多少轮，所以读失败的轮必须单独记，不能混进 `Scans`——否则「10 轮 0 次检出」也可能是「10 轮什么都
没读到」。扫描本身从不抛出：它挂在业务等待的探针里，一个 UIA 异常抛出去会被 `Wait-L2Condition` 当成读空，把业务等待
拖到超时，报出来的是一条等待超时而不是 UIA 读失败（PR #196 审查点名的假红来源）。
#>
$unfinishedPhrase = '上次装货操作未完成'
$unfinishedWatch = New-L2HmiPhraseWatch -Phrase $unfinishedPhrase
$unfinishedAlarmSeen = $false
# 每轮重取 Window：驱动把它缓存在 Attach() 那一刻，窗口没了之后 FindAll 抛的是 ElementNotAvailable，
# 而那正是要记成失败轮、不是记成一次「看」的情形。
$unfinishedElements = {
    $window = $Context.Onboard.Window
    if (-not $window) { return $null }
    return $window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}
function Watch-UnfinishedProjection {
    $null = Invoke-L2HmiPhraseScan -Watch $unfinishedWatch -ElementSource $unfinishedElements
    # 诊断读也包起来。它和扫描一样挂在业务等待的探针里，抛出去同样会被读成「业务条件还没成立」，
    # 把一次数据库抖动变成一条业务等待超时。
    try {
        $alarms = Get-L2RealScalar $connection "SELECT AlarmsJson AS Value FROM OnboardAlarmSnapshots WHERE AgvId = '$($Context.AgvId)'"
        if ([string]$alarms -like '*ONBOARD_SLOT_OPERATION_UNFINISHED*') { $script:unfinishedAlarmSeen = $true }
    } catch { }
    return $unfinishedWatch.Seen.Count
}

<#
丢 ack 之后的连接各自是怎么结束的。判在场景收尾（或补发判红之后），不在补发刚被确认的那一刻：MVP 的 `-002` 在那一刻
取样，读到「2 条连接、0 条开着」，而车 2 秒之后就开了第 3 条——条件在读的那一刻碰巧成立。

两条判据分属两个缺陷，所以分开判：
- `L2-DA-07` 是 control-server#30 那一格：服务端掐连接。丢 ack 之后只要有一条连接不是车自己关的、也不是到收尾还开着，就是红。
- `L2-DA-08` 是 control-server#33 那一格：补发之后车等不到 `SessionReadiness`、超时自己断开再握手一次，多出来的那次重连。
#>
function Add-ConnectionAssertions([int]$dropConnection) {
    $traffic = Get-L2RealTraffic $proxy
    $connections = @($traffic.connections)
    $shape = Format-L2RealConnections $traffic
    $cutByPeer = @($connections | Where-Object {
            [int]$_.connection -gt $dropConnection -and $null -ne $_.closedAt -and $_.closedBy -ne 'onboard closed'
        })
    $assertions.Add(
        'L2-DA-07', '补发之后没有哪条连接被服务端掐掉：丢 ack 之后的连接要么车自己关的、要么到收尾还开着',
        ($cutByPeer.Count -eq 0), '0 connections after the drop ended by anyone but the onboard',
        "$($cutByPeer.Count) ($shape)")

    $open = @($connections | Where-Object { $null -eq $_.closedAt })
    $assertions.Add(
        'L2-DA-08', '丢一次 ack 只换来一次重连：之后只有一条连接，而且到收尾还开着',
        ($connections.Count -eq $dropConnection + 1 -and $open.Count -eq 1),
        "$($dropConnection + 1) connections, 1 open",
        "$($connections.Count) connections, $($open.Count) open ($shape)")
}

# --- 0. 车载端确实走代理，然后布下丢 ack 的计划 -------------------------------------------------------------------

# 少了这一条，「没丢成」与「车根本没经过代理」在后面的判据上长得一样。
$hellos = Wait-L2Condition -Description 'the onboard session was established through the protocol fault proxy' `
    -Journal $journal -Criterion 'proxy-session' -TimeoutSeconds 30 `
    -Probe { (Get-ProxyLines 'onboard->server' 'SessionHello').Count } -Until { param($v) $v -ge 1 }
$assertions.Add(
    'L2-DA-00', '车载端的会话经协议故障代理建立（否则丢 ack 注入不到这条链路上）',
    ($hellos -ge 1), '>= 1 SessionHello through the proxy', $hellos)

$journal.Note('Arming the proxy: drop the first DurableAck for an OperationResult and close that connection.')
$null = $proxy.Command('Put', 'drop-durable-ack', @{ acceptedMessageType = 'OperationResult'; count = 1 })

# --- 1. 需求受理、车到取货点、UIA 录入、车在等操作员 ---------------------------------------------------------------

$load = Start-L2RealLoad $Context 'L2-DA'
$demandId = $load.DemandId
$sessionBefore = Get-L2RealSession $connection $Context.AgvId

# --- 2. 操作员放货关门；装货结果被服务端收下，ack 在代理处丢掉 ------------------------------------------------------

$journal.Note("Operator loads slot $($load.Slot) and closes it.")
$null = $simulator.Command('Put', "slots/$($load.Slot)/cargo", @{ state = 'OCCUPIED' })
$null = $simulator.Command('Post', "slots/$($load.Slot)/close-door", @{})

$drop = Wait-L2Condition -Description 'the proxy dropped the DurableAck for the load result' `
    -Journal $journal -Criterion 'ack-dropped' -TimeoutSeconds 120 `
    -Probe { $drops = @((Get-L2RealTraffic $proxy).drops); if ($drops.Count -gt 0) { $drops[0] } else { $null } } `
    -Until { param($v) $null -ne $v }
$resultId = [string]$drop.acceptedMessageId
$dropConnection = [int]$drop.connection
$journal.Note("Dropped the DurableAck for OperationResult $resultId on connection $dropConnection.")

# 服务端在写出 ack 之前已经提交（CaptureFirstResponseAsync 先提交事务再返回应答），这里读到的是那次提交。
$loadStatus = Wait-L2Condition -Description 'the load result is committed on the server' `
    -Journal $journal -Criterion 'load-committed' -TimeoutSeconds 30 `
    -Probe { Get-L2RealScalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$($load.AttemptId)'" } `
    -Until { param($v) $v -eq 'Committed' }
$acceptedType = Get-L2RealScalar $connection "SELECT MessageType AS Value FROM ProtocolInbox WHERE MessageId = '$resultId'"
$assertions.Add(
    'L2-DA-01', '丢掉的是一份服务端已经收下的装货结果：ProtocolInbox 有这一行，装货已 Committed',
    ($acceptedType -eq 'OperationResult' -and $loadStatus -eq 'Committed'),
    'OperationResult / Committed', "$acceptedType / $loadStatus")

# --- 3. 车重连并补发；补发要被确认 -------------------------------------------------------------------------------

$replayAcked = Wait-L2RealOrLast -Description "the replayed OperationResult $resultId was acknowledged on a later connection" `
    -Journal $journal -Criterion 'replay-acknowledged' -TimeoutSeconds 90 `
    -Probe {
        @((Get-ProxyLines 'server->onboard' 'DurableAck') | Where-Object {
                $_.correlationId -eq $resultId -and [int]$_.connection -gt $dropConnection -and -not $_.dropped
            }).Count
    } `
    -Until { param($v) $v -ge 1 }

$sends = @((Get-ProxyLines 'onboard->server' 'OperationResult') | Where-Object { $_.messageId -eq $resultId })
$firstSend = @($sends | Where-Object { [int]$_.connection -eq $dropConnection })
$replays = @($sends | Where-Object { [int]$_.connection -gt $dropConnection })
$replayShape = if ($firstSend.Count -eq 1 -and $replays.Count -ge 1) {
    "first on #$dropConnection at generation $($firstSend[0].sessionGeneration); $($replays.Count) replay(s), " +
    "first on #$($replays[0].connection) at generation $($replays[0].sessionGeneration); acked on a later connection $replayAcked time(s)"
} else {
    "$($firstSend.Count) first send(s), $($replays.Count) replay(s), acked $replayAcked time(s)"
}
$assertions.Add(
    'L2-DA-02', '车重连后在新会话里以原 messageId 补发 OperationResult、sessionGeneration 换成新的，服务端按首次受理重签 DurableAck（control-server#77）',
    ($firstSend.Count -eq 1 -and $replays.Count -ge 1 -and
        [long]$replays[0].sessionGeneration -gt [long]$firstSend[0].sessionGeneration -and [int]$replayAcked -ge 1),
    'same messageId replayed at a newer generation and acknowledged', $replayShape)

# 补发所在那条连接上的握手顺序（onboard-hmi#69）。按行在这条连接上出现的先后判，不按时间戳。
$replayConnection = if ($replays.Count -ge 1) { [int]$replays[0].connection } else { $dropConnection + 1 }
$null = Wait-L2RealOrLast -Description "the handshake on connection $replayConnection reached SessionReadiness after the new report" `
    -Journal $journal -Criterion 'handshake-completed' -TimeoutSeconds 60 `
    -Probe { (Get-L2RealConnectionSequence (Get-L2RealTraffic $proxy) $replayConnection) -join ' ' } `
    -Until { param($v) $v -match 'onboard->server:RecoveryStateReport.*server->onboard:SessionReadiness' }
$traffic = Get-L2RealTraffic $proxy
$lines = @(@($traffic.lines) | Where-Object { [int]$_.connection -eq $replayConnection })
function IndexOf([scriptblock]$match) {
    for ($i = 0; $i -lt $lines.Count; $i++) { if (& $match $lines[$i]) { return $i } }
    return -1
}
$iHello = IndexOf { param($l) $l.direction -eq 'onboard->server' -and $l.messageType -eq 'SessionHello' }
$iAccepted = IndexOf { param($l) $l.direction -eq 'server->onboard' -and $l.messageType -eq 'SessionAccepted' }
$iReplay = IndexOf { param($l) $l.direction -eq 'onboard->server' -and $l.messageType -eq 'OperationResult' -and $l.messageId -eq $resultId }
$iReplayAck = IndexOf { param($l) $l.direction -eq 'server->onboard' -and $l.messageType -eq 'DurableAck' -and $l.correlationId -eq $resultId }
$iCapability = IndexOf { param($l) $l.direction -eq 'onboard->server' -and $l.messageType -eq 'CapabilitySnapshot' }
$iSafety = IndexOf { param($l) $l.direction -eq 'onboard->server' -and $l.messageType -eq 'SafetyStateSnapshot' }
$iReport = IndexOf { param($l) $l.direction -eq 'onboard->server' -and $l.messageType -eq 'RecoveryStateReport' }
$iReadiness = -1
for ($i = $lines.Count - 1; $i -gt $iReport -and $iReport -ge 0; $i--) {
    if ($lines[$i].direction -eq 'server->onboard' -and $lines[$i].messageType -eq 'SessionReadiness') { $iReadiness = $i; break }
}
$handshakeInOrder = $iHello -eq 0 -and $iAccepted -gt $iHello -and $iReplay -gt $iAccepted -and $iReplayAck -gt $iReplay -and
    $iCapability -gt $iReplayAck -and $iSafety -gt $iCapability -and $iReport -gt $iSafety -and $iReadiness -gt $iReport

$reportsBefore = @((Get-ProxyLines 'onboard->server' 'RecoveryStateReport') | Where-Object { [int]$_.connection -le $dropConnection } | ForEach-Object { [string]$_.messageId })
$reportsAfter = @((Get-ProxyLines 'onboard->server' 'RecoveryStateReport') | Where-Object { [int]$_.connection -gt $dropConnection } | ForEach-Object { [string]$_.messageId })
$reportReplayed = @($reportsAfter | Where-Object { $_ -in $reportsBefore })
$assertions.Add(
    'L2-DA-03', '补发之后同一条连接照常走完握手：Hello → Accepted → 补发 → 其 ack → 能力快照 → 安全快照 → 新 messageId 的 RecoveryStateReport → SessionReadiness；旧的恢复报告一条都不按原编号补发（onboard-hmi#69，CV-SESSION-RECONNECT-DURING-RECOVERY）',
    ($handshakeInOrder -and $reportsAfter.Count -ge 1 -and $reportReplayed.Count -eq 0),
    "按序 / 新报告 >= 1 / 旧报告补发 0",
    "#$replayConnection $((Get-L2RealConnectionSequence $traffic $replayConnection) -join ' ') / 新报告 $($reportsAfter.Count) / 旧报告补发 $($reportReplayed.Count)")

$sessionAfter = Wait-L2RealOrLast -Description 'the session was Ready again in a newer generation' `
    -Journal $journal -Criterion 'session-ready-after-replay' -TimeoutSeconds 60 `
    -Probe { Get-L2RealSession $connection $Context.AgvId } `
    -Until { param($v)
        $null -ne $v -and [long]$v.SessionGeneration -gt [long]$sessionBefore.SessionGeneration -and
            [string]$v.Readiness -eq 'Ready' -and [string]$v.ReasonCode -eq 'READY' }
$assertions.Add(
    'L2-DA-04', '服务端在新世代把会话判回 Ready：本代次能力快照、安全快照与恢复状态报告齐全',
    ($null -ne $sessionAfter -and [long]$sessionAfter.SessionGeneration -gt [long]$sessionBefore.SessionGeneration -and
        [string]$sessionAfter.Readiness -eq 'Ready' -and [string]$sessionAfter.ReasonCode -eq 'READY'),
    "gen > $($sessionBefore.SessionGeneration) / Ready / READY", (Format-L2RealSession $sessionAfter))

# L2-DA-09 的窗口从这里开始：会话已在新世代回到 Ready，车载端的恢复判断就在这条 readiness 上跑（不早一步）。先连扫
# 10 秒，缺陷版本在 Ready 之后几毫秒内就发布那条投影；之后每一个等待的探针都再扫一遍，直到卸货在等操作员。
#
# 定时长采样，不是等待：绿的那次运行按设计就是扫满 10 秒一次都不检出，拿会写「Not reached」的等待函数来做，
# 等于每次绿运行都在 journal 里留一条「未到达」给人误读（PR #196 审查第三条）。这里记的是读数。
$journal.Note('L2-DA-09 window opens: sampling the HMI for 10 s from the session being Ready again.')
$sample = Invoke-L2HmiPhraseSample -Watch $unfinishedWatch -ElementSource $unfinishedElements -DurationSeconds 10 `
    -Journal $journal -Criterion 'unfinished-projection'
$journal.Note("L2-DA-09 first window: $sample")
# 分段记账。判据文字说的是「重回 Ready 到卸货等操作员之间」都在看，而这个 10 秒采样窗一段就产出二十几轮
# （实测 22 轮，全程 70 轮）——只判一个全程总数的话，后面三处业务探针里的扫描被删掉、或 UIA 句柄在这
# 10 秒之后失效，「从第 10 秒到卸货等操作员」那二十多秒一眼都没看，总数仍然 >= 10、判据照样 PASS。
# 那正是本票要消灭的那一类「数字比它知道的说得多」。
$cleanAfterFirstWindow = $unfinishedWatch.CleanScans

# 服务端日志只做诊断，不当判据：判据读的是库和代理。
$logRoot = Join-Path (Split-Path -Parent $Context.SnapshotRoot) 'logs'
$conflicts = @(Get-ChildItem -LiteralPath $logRoot -Filter 'control-server*.log' |
    Select-String -SimpleMatch 'MessageId was replayed with different normalized content').Count
$journal.Note("ControlServer logged $conflicts 'MessageId was replayed with different normalized content' conflict(s).")

if ([int]$replayAcked -lt 1) {
    Add-L2RealNotReached $assertions @('L2-DA-05', 'L2-DA-06', 'L2-DA-09') "补发一直没被确认（stage $(Get-L2RealStage $connection $demandId)）"
    Add-ConnectionAssertions -dropConnection $dropConnection
    $journal.Note('Scenario stopped after the replay verdict.')
    return
}

# --- 4. 旅程照常走完，结果只记了一次 ---------------------------------------------------------------------------

$null = Wait-L2Condition -Description 'the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { $null = Watch-UnfinishedProjection; Get-L2RealStage $connection $demandId } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$gateIntent = Wait-L2RealIntent $Context $demandId 'TO_GATE' 60
Move-L2RealVehicleTo $Context $gateIntent $Context.GateStationRiotId 'the gate'

$unloadAttempt = Wait-L2Condition -Description 'the server issued the unload command' `
    -Journal $journal -Criterion 'unload-attempt' -TimeoutSeconds 180 `
    -Probe {
        $null = Watch-UnfinishedProjection
        Get-L2RealScalar $connection "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Unload'"
    } `
    -Until { param($v) $v }
$unloadWaiting = Wait-L2Condition -Description 'the onboard is waiting for the operator at the gate' `
    -Journal $journal -Criterion 'unload-waiting-operator' -TimeoutSeconds 120 `
    -Probe {
        $null = Watch-UnfinishedProjection
        @((Get-L2RealProgress $connection $unloadAttempt) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })[0]
    } `
    -Until { param($v) $null -ne $v }
$null = Watch-UnfinishedProjection
$cleanInSecondLeg = $unfinishedWatch.CleanScans - $cleanAfterFirstWindow
$journal.Note("L2-DA-09 window closed at the unload's WAITING_OPERATOR after $($unfinishedWatch.CleanScans) clean and " +
    "$($unfinishedWatch.FailedScans) failed HMI scans (first 10 s window $cleanAfterFirstWindow, business probes after it $cleanInSecondLeg); " +
    "server alarm snapshot showed ONBOARD_SLOT_OPERATION_UNFINISHED: $unfinishedAlarmSeen (diagnostic only).")
# 干净扫描不足 10 轮也红，而且红的原因说得出来：「UIA 读失败 N 轮」，不是一条等待超时。
$assertions.Add(
    'L2-DA-09', '确认丢失的那次装货已完成：会话重回 Ready 之后、卸货等操作员之前，HMI 上从未出现「上次装货操作未完成」；开头 10 秒采样窗与之后到卸货等操作员为止**各**至少 10 轮把整棵 UIA 树读全了（onboard-hmi#124，计数与分段在 control-server#204 收紧）',
    ($cleanAfterFirstWindow -ge 10 -and $cleanInSecondLeg -ge 10 -and $unfinishedWatch.Seen.Count -eq 0),
    '采样窗 ≥ 10 轮 / 之后 ≥ 10 轮 / 检出 0 次',
    "$(Format-L2HmiPhraseWatch $unfinishedWatch)（采样窗 $cleanAfterFirstWindow 轮 / 之后 $cleanInSecondLeg 轮）")
$unloadSlot = [int]$unloadWaiting.Active[0]
$journal.Note("Operator empties slot $unloadSlot and closes it.")
$null = $simulator.Command('Put', "slots/$unloadSlot/cargo", @{ state = 'EMPTY' })
$null = $simulator.Command('Post', "slots/$unloadSlot/close-door", @{})

$stage = Wait-L2RealOrLast -Description 'the journey completed at the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-L2RealStage $connection $demandId } -Until { param($v) $v -eq 'Completed' }
$assertions.Add('L2-DA-05', '补发被确认之后旅程照常走完', ($stage -eq 'Completed'), 'Completed', $stage)

$loadResults = Get-L2RealCount $connection "SELECT COUNT(*) AS Total FROM OperationResults WHERE SlotOperationAttemptId = '$($load.AttemptId)'"
$unloadStatus = Get-L2RealScalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$unloadAttempt'"
$demandStatus = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-DA-06', '装货结果只记了一次，卸货 Committed，需求 Succeeded',
    ($loadResults -eq 1 -and $unloadStatus -eq 'Committed' -and $demandStatus -eq 'Succeeded'),
    '1 / Committed / Succeeded', "$loadResults / $unloadStatus / $demandStatus")

Add-ConnectionAssertions -dropConnection $dropConnection

$journal.Note('Scenario finished.')
