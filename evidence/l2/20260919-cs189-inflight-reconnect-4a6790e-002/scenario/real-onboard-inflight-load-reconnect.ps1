#Requires -Version 7

<#
control-server#189 第一步的复现场景（一次性，调试用，不进服务端仓）：装货进行中断一次链路，看重连之后这次装货能不能收尾。

cs#167 的 debug-001 在车载端 3547a97 上看到：代理断一次链路，车重连成新代次，服务端判 RecoveryRequired /
PENDING_FACT_RECONCILIATION_REQUIRED 等这次装货的结果；车载端装货做完后发结果，报 WIRE_TO_GATE_NOT_READY，结果没发出去。
本场景在含 onboard-hmi#120、#119 的车载端顶端重做一次，并多看一步：

1. 装货开门、车载端在等操作员（WAITING_OPERATOR）时，经协议故障代理断开一次（不丢任何行，车自己重连）。
2. 重连后记下服务端对新代次的判定与它在等的 attempt。
3. 操作员放货关门（模拟器），装货在车上结束。等 45 秒，看这次装货的 OperationResult 有没有到服务端、会话回没回 Ready。
4. 再断开一次（兜底探针）：新握手之后车载端会按 journal 做一次「中断结算」，那条路走的是允许 RecoveryRequired 的发送口。
   看这一次结果能不能送达、会话能不能回 Ready。它回答的是「互等靠不靠一次无关的会话变化解开」，不是期望的修法。

判据写的是票面「第二步」的目标行为（结果送达、会话 Ready、装货 Committed、不重复结算），所以判据红 = 缺陷复现。
判据只读服务端库、代理流量与模拟器快照；车载端日志由编排器收进 logs/onboard-app/。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$simulator = $Context.Simulator
$connection = $Context.Connection
$proxy = $Context.ProtocolProxy
$agvId = $Context.AgvId

if ($null -eq $proxy) { throw 'This scenario needs ProtocolFaultProxy = $true in its setup file.' }

function Get-SessionDetail {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT SessionGeneration, Readiness, ReasonCode, PendingAttemptIdsJson, PendingResultIdsJson, " +
        "UnsettledSlotOperationAttemptId, ProvenRecoveryCheckpoint, ActiveUnlockSlotsJson FROM SessionRecoveries " +
        "WHERE AgvId = '$agvId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Format-SessionDetail([object]$row) {
    if ($null -eq $row) { return '(no session)' }
    return ("gen $($row.SessionGeneration) / $($row.Readiness) / $($row.ReasonCode) / pendingAttempts $($row.PendingAttemptIdsJson) / " +
        "pendingResults $($row.PendingResultIdsJson) / unsettled $($row.UnsettledSlotOperationAttemptId) / " +
        "checkpoint $($row.ProvenRecoveryCheckpoint) / activeUnlock $($row.ActiveUnlockSlotsJson)")
}

function Get-Results([string]$attemptId) {
    return , @((Get-L2RealInbound $connection 'OperationResult') |
        Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $attemptId })
}

function Get-OperationStatus([string]$attemptId) {
    return Get-L2RealScalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
}

# Every line of one connection, with the time, as the proxy logged it -- the protocol sequence the report quotes.
function Write-ConnectionLines([int]$number) {
    foreach ($line in @(@((Get-L2RealTraffic $proxy).lines) | Where-Object { [int]$_.connection -eq $number })) {
        if ([string]$line.messageType -in 'Heartbeat', 'HeartbeatAck') { continue }
        $journal.Note("wire #$number $($line.at) $($line.direction) $($line.messageType)$(if ($line.acceptedMessageType) { " ack=$($line.acceptedMessageType)" })")
    }
}

function Invoke-Disconnect([string]$why) {
    $journal.Note("Dropping the onboard link through the protocol fault proxy: $why")
    return @($proxy.Command('Post', 'disconnect', @{}).body.connections)
}

# --- 0. 车载端确实走代理 ---------------------------------------------------------------------------------------------

$hellos = Wait-L2Condition -Description 'the onboard session was established through the protocol fault proxy' `
    -Journal $journal -Criterion 'proxy-session' -TimeoutSeconds 30 `
    -Probe { @(@((Get-L2RealTraffic $proxy).lines) | Where-Object { $_.direction -eq 'onboard->server' -and $_.messageType -eq 'SessionHello' }).Count } `
    -Until { param($v) $v -ge 1 }
$assertions.Add('L2-IR-00', '车载端的会话经协议故障代理建立（否则断开注入不到这条链路上）',
    ($hellos -ge 1), '>= 1 SessionHello through the proxy', $hellos)

# --- 1. 装货开门、车载端在等操作员 --------------------------------------------------------------------------------

$load = Start-L2RealLoad $Context 'L2-CS189'
$attemptId = $load.AttemptId
$slot = $load.Slot
$session0 = Get-SessionDetail
$generation0 = [long]$session0.SessionGeneration
$journal.Note("Before the drop: $(Format-SessionDetail $session0).")

# --- 2. 断一次链路，车重连 ---------------------------------------------------------------------------------------

$closed1 = @(Invoke-Disconnect 'load in flight, onboard waiting for the operator')
$droppedAt = [DateTimeOffset]::UtcNow
$session1 = Wait-L2RealOrLast -Description 'the onboard reconnected in a new generation and the server decided its readiness' `
    -Journal $journal -Criterion 'reconnected' -TimeoutSeconds 60 `
    -Probe { Get-SessionDetail } `
    -Until { param($v) $null -ne $v -and [long]$v.SessionGeneration -gt $generation0 -and [string]$v.ReasonCode -ne 'RECOVERY_RECONCILIATION_PENDING' }
# The handshake answers in well under a second; give the mid-session snapshot request its answer too.
Start-Sleep -Seconds 3
$session1 = Get-SessionDetail
$generation1 = [long]$session1.SessionGeneration
$journal.Note("After the first reconnect: $(Format-SessionDetail $session1).")
$assertions.Add('L2-IR-01',
    '重连后服务端在等这次装货的结果：新代次、RecoveryRequired、PENDING_FACT_RECONCILIATION_REQUIRED，待对账 attempt 含本次装货（CV-CONNECTION-LOSS-SAFE-FINISH 的 NEVER_READY_BEFORE_RECONCILIATION）',
    ($generation1 -gt $generation0 -and [string]$session1.Readiness -eq 'RecoveryRequired' -and
        [string]$session1.ReasonCode -eq 'PENDING_FACT_RECONCILIATION_REQUIRED' -and
        ([string]$session1.PendingAttemptIdsJson).Contains($attemptId)),
    "gen > $generation0 / RecoveryRequired / PENDING_FACT_RECONCILIATION_REQUIRED / pending 含 $attemptId",
    "断 $($closed1.Count) 条 / $(Format-SessionDetail $session1)")

# --- 3. 操作员放货关门，装货在车上结束 -----------------------------------------------------------------------------

$journal.Note("Operator loads slot $slot and closes the door (simulator).")
$null = $simulator.Command('Put', "slots/$slot/cargo", @{ state = 'OCCUPIED' })
$null = $simulator.Command('Post', "slots/$slot/close-door", @{})
$physical = Wait-L2RealOrLast -Description 'the loaded slot is closed, locked and occupied' `
    -Journal $journal -Criterion 'load-slot-physical' -TimeoutSeconds 60 `
    -Probe { Get-L2RealSlotReading $simulator $slot } -Until { param($v) $v -eq 'CLOSED/OCCUPIED/1/0' }
$closedAt = [DateTimeOffset]::UtcNow

$results = Wait-L2RealOrLast -Description 'the OperationResult of the in-flight load reached the server' `
    -Journal $journal -Criterion 'result-after-reconnect' -TimeoutSeconds 45 `
    -Probe { Get-Results $attemptId } -Until { param($v) @($v).Count -ge 1 }
# Whatever arrived, let readiness follow it before reading.
Start-Sleep -Seconds 3
$session2 = Get-SessionDetail
$status2 = Get-OperationStatus $attemptId
$results = @(Get-Results $attemptId)
$progressAfterDrop = @((Get-L2RealProgress $connection $attemptId) | Where-Object { $_.At -gt $droppedAt })
$journal.Note("45 s after the load finished: $(Format-SessionDetail $session2); operation $status2; results $($results.Count); progress after the drop $($progressAfterDrop.Count) ($(@($progressAfterDrop | ForEach-Object Phase) -join ','))")
$assertions.Add('L2-IR-02',
    '装货在车上结束后，这次装货的 OperationResult 送达服务端并被受理，会话回到 Ready、装货 Committed（ADR-cross-0028／0029：未就绪期间允许结果补报）',
    ($physical -eq 'CLOSED/OCCUPIED/1/0' -and $results.Count -ge 1 -and [string]$session2.Readiness -eq 'Ready' -and $status2 -eq 'Committed'),
    'CLOSED/OCCUPIED/1/0 / 结果 >= 1 / Ready / Committed',
    "$physical / 结果 $($results.Count) / $(Format-SessionDetail $session2) / $status2")
$assertions.Add('L2-IR-03',
    '（观察）断线重连后这次装货的 OperationProgress 仍送达服务端',
    ($progressAfterDrop.Count -ge 1),
    '>= 1 progress after the drop',
    "$($progressAfterDrop.Count) ($(@($progressAfterDrop | ForEach-Object Phase) -join ','))")

# --- 4. 兜底探针：再断一次，看中断结算那条路能不能把结果送到 ------------------------------------------------------

$generation2 = [long]$session2.SessionGeneration
$closed2 = @(Invoke-Disconnect 'probe: does a second handshake settle the finished load?')
$session3 = Wait-L2RealOrLast -Description 'the onboard reconnected a second time' `
    -Journal $journal -Criterion 'reconnected-2' -TimeoutSeconds 60 `
    -Probe { Get-SessionDetail } `
    -Until { param($v) $null -ne $v -and [long]$v.SessionGeneration -gt $generation2 -and [string]$v.ReasonCode -ne 'RECOVERY_RECONCILIATION_PENDING' }
$results3 = Wait-L2RealOrLast -Description 'the OperationResult reached the server after the second reconnect' `
    -Journal $journal -Criterion 'result-after-reconnect-2' -TimeoutSeconds 45 `
    -Probe { Get-Results $attemptId } -Until { param($v) @($v).Count -ge 1 }
$results3 = @($results3)
$session3 = Wait-L2RealOrLast -Description 'the session is Ready after the second reconnect' `
    -Journal $journal -Criterion 'ready-after-reconnect-2' -TimeoutSeconds 20 `
    -Probe { Get-SessionDetail } -Until { param($v) $null -ne $v -and [string]$v.Readiness -eq 'Ready' }
$status3 = Get-OperationStatus $attemptId
$stage3 = Get-L2RealStage $connection $load.DemandId
$journal.Note("After the second reconnect: $(Format-SessionDetail $session3); operation $status3; stage $stage3; results $($results3.Count).")
$assertions.Add('L2-IR-04',
    '（探针）第二次断线重连后，结果经车载端的中断结算送达，会话回 Ready、装货 Committed、旅程走到关卡段',
    ($results3.Count -ge 1 -and [string]$session3.Readiness -eq 'Ready' -and $status3 -eq 'Committed' -and $stage3 -eq 'AwaitingGateArrival'),
    '结果 >= 1 / Ready / Committed / AwaitingGateArrival',
    "断 $($closed2.Count) 条 / 结果 $($results3.Count) / $(Format-SessionDetail $session3) / $status3 / $stage3")

$results3 = @(Get-Results $attemptId)
$assertions.Add('L2-IR-05',
    '同一 attempt 至多一条 OperationResult（服务端收件箱按 messageId 一行），不重复结算',
    ($results3.Count -le 1),
    '<= 1', "$($results3.Count) ($(@($results3 | ForEach-Object { "$($_.MessageId) gen $($_.Generation) outcome $($_.Payload.overallOutcome) -> $($_.ResponseTypes -join ',')" }) -join '; '))")

# --- 5. 线上序列留档 ----------------------------------------------------------------------------------------------

$traffic = Get-L2RealTraffic $proxy
# hmi#124 saw, on its G2 double, the onboard answer the server's post-reconnect journey snapshot with ProtocolProblem and
# disconnect. Whether the real pair does the same is part of what this run records.
$problems = @(@($traffic.lines) | Where-Object { [string]$_.messageType -eq 'ProtocolProblem' })
$snapshotsAfterDrop = @(@($traffic.lines) | Where-Object {
        [int]$_.connection -gt 1 -and $_.direction -eq 'server->onboard' -and
        [string]$_.messageType -in 'VehicleBusinessStateSnapshot', 'CurrentStopWorklistSnapshot', 'UpcomingStopPlanSnapshot' })
$assertions.Add('L2-IR-06',
    '（观察，hmi#124 线索）全程没有 ProtocolProblem；重连后的连接上服务端发过的行程快照条数照实记录',
    ($problems.Count -eq 0),
    '0 ProtocolProblem',
    "$($problems.Count) ProtocolProblem ($(@($problems | ForEach-Object { "#$($_.connection) $($_.direction) $($_.at)" }) -join '; ')) / 重连后行程快照 $($snapshotsAfterDrop.Count) 条 ($(@($snapshotsAfterDrop | ForEach-Object { "#$($_.connection) $($_.messageType)" }) -join ', '))")
$journal.Note("Connections: $(Format-L2RealConnections $traffic)")
foreach ($number in @(@($traffic.connections) | ForEach-Object { [int]$_.connection })) { Write-ConnectionLines $number }
$journal.Note('Scenario finished.')
