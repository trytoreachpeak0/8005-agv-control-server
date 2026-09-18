#Requires -Version 7

<#
补偿清空对账之后，服务端发给车的 `ExceptionRecoverySessionSnapshot`（已是 CLOSED）与 `LoadCompensationCommand` 要都已
结清；链路断一次、车重连之后，它们一条都不该被重放进新会话，车还要接得了下一单。

**来源与成对的两端票。**`trytoreachpeak0/8005-agv-program#61` 审计第 4 节 ②「快照确认与恢复命令结算两半一起验」：
- 服务端半边 control-server#78（批次5-09，MVP `219b033f`）：恢复结果记下即结算它回答的恢复命令；
  `ReplayPendingCommandsAsync` 从此不再把失败或成功的补偿命令重放进之后的每个会话。
- 车载端半边 onboard-hmi#70（批次5-15，MVP `a696add`＋`86fe0a4`）：对恢复会话快照回 `SnapshotAppliedAck`，但只确认
  CLOSED 那一份；开着的不确认，重启后的车还能靠重放拿回。只移一半会出现「CLOSED 快照被重放进每个新会话」或「开着的
  会话被确认丢掉」之一。
MVP 线参照：`ControlServer_MVP` 同名场景，control-server#31：`-001` 红（补偿会话留下的出站报文被重放进新会话）、`-003` 绿。

**前半段怎么造出 `UNKNOWN`，与 MVP 不同。**MVP 版经车载端自动化面造 `UNKNOWN`；v2 没有那个面，而 v2 的恢复类 G3 场景
共用的「空关仓门、等车载端超时报 FAILED」在 v2 上不可达（onboard-hmi#72 之后空关即重开，control-server#128）。v2 上
真实可达的办法是 ADR-cross-0058 决策 2 的反面：车在等操作员时进程没了，它不在的时候门被空着关上，重启后中断结算
（onboard-hmi#70）按实时 IO 判：门关了、锁上了、输出复位了，唯独仓里没有货，不是装货的最终态，报 `UNKNOWN`。
那一仓空着、关着、锁着，所以补偿不开任何门就能证 `ALL_EMPTY`。

**断开靠 `tools/ControlServer.ProtocolFaultProxy` 的 `POST /control/v1/disconnect`**：不丢任何行，只断开，车自己重连。
之后发生的一切都是两端出厂代码对「新会话」的处理，不是一条被丢掉的 ack 引出的补发（那是 `real-onboard-durable-ack-lost`）。

判据只读三处：服务端库、代理快照（每条连接上服务端发过哪些 messageId）、模拟器快照。不读车载端界面文字。
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

if ($null -eq $proxy) { throw 'This scenario needs ProtocolFaultProxy = $true in its setup file.' }
$laterIds = @('L2-CR-02', 'L2-CR-03', 'L2-CR-04', 'L2-CR-05', 'L2-CR-06', 'L2-CR-07', 'L2-CR-08')

# 补偿会话留下的出站报文：恢复会话快照与补偿命令，按创建先后。
function Get-RecoveryOutbox {
    return , @(@(Get-L2RealOutbound $connection 'ExceptionRecoverySessionSnapshot') + @(Get-L2RealOutbound $connection 'LoadCompensationCommand') |
            Sort-Object At)
}

# Only the recovery session snapshot carries a state; the compensation command has none, and StrictMode throws on
# reading a property that is not there (compensate-then-reconnect-001 died on exactly that).
function Get-SnapshotState([object]$row) {
    $property = $row.Payload.PSObject.Properties['state']
    return $(if ($null -ne $property) { [string]$property.Value } else { '' })
}

function Format-Outbox([object[]]$rows) {
    return (@($rows | ForEach-Object {
                "$($_.MessageType)[$($_.MessageId.Substring(0, 8))] state=$(Get-SnapshotState $_) ack=$($_.Acknowledged) fenced=$($_.Fenced)"
            }) -join '; ')
}

# --- 0. 车载端确实走代理 -------------------------------------------------------------------------------------------

# 少了这一条，「断开没断成」与「车根本没经过代理」在后面的判据上长得一样。
$hellos = Wait-L2Condition -Description 'the onboard session was established through the protocol fault proxy' `
    -Journal $journal -Criterion 'proxy-session' -TimeoutSeconds 30 `
    -Probe { @(@((Get-L2RealTraffic $proxy).lines) | Where-Object { $_.direction -eq 'onboard->server' -and $_.messageType -eq 'SessionHello' }).Count } `
    -Until { param($v) $v -ge 1 }
$assertions.Add(
    'L2-CR-00', '车载端的会话经协议故障代理建立（否则断开注入不到这条链路上）',
    ($hellos -ge 1), '>= 1 SessionHello through the proxy', $hellos)

# --- 1. 等人时进程没了，门被空着关上；重启后中断结算报 UNKNOWN ----------------------------------------------------

$load = Start-L2RealLoad $Context 'L2-CR'
$demandId = $load.DemandId
$attemptId = $load.AttemptId

& $Context.StopComponent 'onboard-hmi'
$journal.Note("While the onboard is down the operator closes slot $($load.Slot) without loading it.")
$null = $simulator.Command('Post', "slots/$($load.Slot)/close-door", @{})
$null = Wait-L2Condition -Description 'the slot reads closed, empty, locked and reset' `
    -Journal $journal -Criterion 'slot-closed-empty' -TimeoutSeconds 30 `
    -Probe { Get-L2RealSlotReading $simulator $load.Slot } -Until { param($v) $v -eq 'CLOSED/EMPTY/1/0' }
$onboard = & $Context.RestartOnboard

$unknown = Wait-L2RealOrLast -Description 'the restarted onboard settled the interrupted load as UNKNOWN and the journey blocked' `
    -Journal $journal -Criterion 'unknown-load' -TimeoutSeconds 120 `
    -Probe {
        $result = @((Get-L2RealInbound $connection 'OperationResult') | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $attemptId })[0]
        "$(if ($result) { $result.Payload.overallOutcome } else { '(no result)' }) / " +
        "$(Get-L2RealScalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'") / " +
        "$(Get-L2RealStage $connection $demandId)"
    } `
    -Until { param($v) $v -eq 'UNKNOWN / RecoveryRequired / Blocked' }
$assertions.Add(
    'L2-CR-01', '前半段到位：重启后中断结算把空着关上的装货报 UNKNOWN，服务端判 RecoveryRequired，旅程停摆',
    ($unknown -eq 'UNKNOWN / RecoveryRequired / Blocked'), 'UNKNOWN / RecoveryRequired / Blocked', $unknown)
if ($unknown -ne 'UNKNOWN / RecoveryRequired / Blocked') {
    Add-L2RealNotReached $assertions $laterIds '没有造出 UNKNOWN 装货结果'
    return
}

# --- 2. 维护人员按「补偿清空」，补偿对账 ------------------------------------------------------------------------

$offered = Wait-L2RealButtonOffered $onboard $journal '补偿清空' 'onboard-compensation-entry' 90
if (-not $offered) {
    Add-L2RealNotReached $assertions $laterIds '车载端没有给出「补偿清空」入口'
    return
}
$null = Invoke-L2RealConfirmedButton $onboard $journal '补偿清空' '补偿清空'
$compensation = Wait-L2Condition -Description 'the server received LoadCompensationResult, or the onboard reported a refusal' `
    -Journal $journal -Criterion 'compensation-result' -TimeoutSeconds 120 `
    -Probe {
        $received = @((Get-L2RealInbound $connection 'LoadCompensationResult') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
        if ($received.Count -ge 1) { $received[0] }
        elseif (@($onboard.WindowTitles()) -contains '补偿清空失败') { 'REFUSED' }
        else { $null }
    } -Until { param($v) $null -ne $v }
if ($compensation -is [string]) {
    Add-L2RealNotReached $assertions $laterIds '补偿清空未被接受（车载端弹出「补偿清空失败」）'
    return
}
$actionId = [string]$compensation.Payload.recoveryActionId
$settled = Wait-L2RealOrLast -Description 'the compensation reconciled and ended the demand' `
    -Journal $journal -Criterion 'compensation-reconciled' -TimeoutSeconds 60 `
    -Probe {
        "$(Get-L2RealScalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'") / " +
        "$($compensation.Payload.overallOutcome) / " +
        "$(Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'") / " +
        "$(Get-L2RealStage $connection $demandId)/$(Get-L2RealScalar $connection "SELECT BlockReasonCode AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")"
    } `
    -Until { param($v) $v -eq 'Reconciled / ALL_EMPTY / Cancelled / Completed/CANCELLED_BY_LOAD_COMPENSATION' }
$assertions.Add(
    'L2-CR-02', '补偿清空走到对账：工作流 Reconciled、ALL_EMPTY，需求 Cancelled，旅程以 CANCELLED_BY_LOAD_COMPENSATION 收尾',
    ($settled -eq 'Reconciled / ALL_EMPTY / Cancelled / Completed/CANCELLED_BY_LOAD_COMPENSATION'),
    'Reconciled / ALL_EMPTY / Cancelled / Completed/CANCELLED_BY_LOAD_COMPENSATION', $settled)

$sessionBefore = Wait-L2RealOrLast -Description 'the session returned to Ready after the compensation' `
    -Journal $journal -Criterion 'session-ready-after-compensation' -TimeoutSeconds 60 `
    -Probe { Get-L2RealSession $connection $Context.AgvId } `
    -Until { param($v) $null -ne $v -and [string]$v.Readiness -eq 'Ready' -and [string]$v.ReasonCode -eq 'READY' }
$readyBefore = ($null -ne $sessionBefore -and [string]$sessionBefore.Readiness -eq 'Ready' -and [string]$sessionBefore.ReasonCode -eq 'READY')
$assertions.Add(
    'L2-CR-03', '补偿对账之后会话回到 Ready（断开之前的前提）',
    $readyBefore, 'Ready / READY', (Format-L2RealSession $sessionBefore))
if (-not $readyBefore) {
    Add-L2RealNotReached $assertions @('L2-CR-04', 'L2-CR-05', 'L2-CR-06', 'L2-CR-07', 'L2-CR-08') '补偿之后会话没回到 Ready'
    return
}

# 补偿会话留下的出站报文到这里都该结清了：CLOSED 的恢复会话快照由车回 SnapshotAppliedAck 确认（onboard-hmi#70），
# 被取代的旧 revision 由服务端 fence；补偿命令由 LoadCompensationResult 结算（control-server#78）。既没确认也没 fence
# 的行，会被 ReplayPendingCommandsAsync 重放进之后的每一个会话。
$live = Wait-L2RealOrLast -Description 'the recovery session messages were settled after the compensation' `
    -Journal $journal -Criterion 'recovery-outbox-settled' -TimeoutSeconds 30 `
    -Probe { @((Get-RecoveryOutbox) | Where-Object { -not $_.Acknowledged -and -not $_.Fenced }).Count } `
    -Until { param($v) $v -eq 0 }
$recoveryBefore = Get-RecoveryOutbox
$closedSnapshots = @($recoveryBefore | Where-Object { $_.MessageType -eq 'ExceptionRecoverySessionSnapshot' -and (Get-SnapshotState $_) -eq 'CLOSED' })
$assertions.Add(
    'L2-CR-04', '补偿对账之后，恢复会话快照与补偿命令都已结清：CLOSED 那份快照被车确认，旧 revision 被取代，补偿命令被补偿结果结算',
    ($recoveryBefore.Count -ge 2 -and [int]$live -eq 0 -and $closedSnapshots.Count -ge 1 -and @($closedSnapshots | Where-Object { -not $_.Acknowledged }).Count -eq 0),
    '>= 2 recovery rows, 0 live, CLOSED snapshot acknowledged',
    "$($recoveryBefore.Count) rows, $live live ($(Format-Outbox $recoveryBefore))")

# --- 3. 断开一次，不丢任何行；车自己重连 ------------------------------------------------------------------------

$lastBefore = [int]@((Get-L2RealTraffic $proxy).connections)[-1].connection
$journal.Note("Disconnecting the relay (last connection so far #$lastBefore).")
$closed = @($proxy.Command('Post', 'disconnect', @{}).body.connections)
$journal.Note("The relay closed connection(s) $($closed -join ', ').")

$sessionAfter = Wait-L2RealOrLast -Description 'the session was Ready again in a newer generation after the reconnect' `
    -Journal $journal -Criterion 'session-ready-after-reconnect' -TimeoutSeconds 90 `
    -Probe { Get-L2RealSession $connection $Context.AgvId } `
    -Until { param($v)
        $null -ne $v -and [long]$v.SessionGeneration -gt [long]$sessionBefore.SessionGeneration -and
            [string]$v.Readiness -eq 'Ready' -and [string]$v.ReasonCode -eq 'READY' }
$assertions.Add(
    'L2-CR-05', '重连之后会话在新世代回到 Ready',
    ($null -ne $sessionAfter -and [long]$sessionAfter.SessionGeneration -gt [long]$sessionBefore.SessionGeneration -and
        [string]$sessionAfter.Readiness -eq 'Ready'),
    "gen > $($sessionBefore.SessionGeneration) / Ready / READY", (Format-L2RealSession $sessionAfter))

# 给重放与车的应答留出时间：引擎再转几轮，服务端该发的都发了，车要撕会话也撕了。
$null = Wait-L2Iterations -Riot $Context.Riot -Count 4 -Journal $journal

# --- 4. 重放了什么 ----------------------------------------------------------------------------------------------

$sentAfter = @(@((Get-L2RealTraffic $proxy).lines) | Where-Object { $_.direction -eq 'server->onboard' -and [int]$_.connection -gt $lastBefore })
$sentIds = @($sentAfter | ForEach-Object { ([string]$_.messageId).ToLowerInvariant() })
$replayed = @($recoveryBefore | Where-Object { $sentIds -contains $_.MessageId })
$journal.Note("Server->onboard after the disconnect: $(@($sentAfter | ForEach-Object { "#$($_.connection) $($_.messageType)" }) -join ', ').")
$assertions.Add(
    'L2-CR-06', '补偿会话留下的恢复会话快照与补偿命令，一条都没有被重放进新会话',
    ($replayed.Count -eq 0), "0 of $($recoveryBefore.Count) replayed",
    "$($replayed.Count) of $($recoveryBefore.Count) replayed$(if ($replayed.Count -gt 0) { " ($(Format-Outbox $replayed))" })")
$journal.Note("Recovery outbox after the reconnect: $(Format-Outbox (Get-RecoveryOutbox)).")

# --- 5. 车还回来：下一条需求被受理，车在取货点收下扫码 ------------------------------------------------------------

$nextSubmitted = 0
$nextError = $null
try {
    $nextGuid = [guid]::NewGuid()
    $nextDemandId = $nextGuid.ToString('D')
    $nextSublot = "L2-CR-$($Context.RunId)-NEXT"
    $null = $Context.MesIngest.Command('Put', "demands/$($nextGuid.ToString('N'))", @{
        sublot = $nextSublot; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
    })
    $nextIntent = Wait-L2RealIntent $Context $nextDemandId 'TO_PICKUP'
    Move-L2RealVehicleTo $Context $nextIntent $Context.PickupStationRiotId 'the pickup station for the next demand'
    # 先等服务端进入 AwaitingSublot 再录入：车载端提交之后不清空上一轮的录入请求，输入框可能早就是可用的。
    $null = Wait-L2Condition -Description 'the next journey waits for a sublot' -Journal $journal `
        -Criterion 'next-awaiting-sublot' -TimeoutSeconds 120 `
        -Probe { Get-L2RealStage $connection $nextDemandId } -Until { param($v) $v -eq 'AwaitingSublot' }
    $null = Wait-L2Condition -Description 'the onboard HMI accepts sublot entry for the next demand' -Journal $journal `
        -Criterion 'next-onboard-can-submit' -TimeoutSeconds 120 -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
    $onboard.SetSublot($nextSublot)
    $null = Wait-L2Condition -Description 'the manual submit button became enabled for the next sublot' -Journal $journal `
        -Criterion 'next-onboard-submit-ready' -TimeoutSeconds 30 -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
    $onboard.Submit()
    $nextSubmitted = Wait-L2RealOrLast -Description 'the vehicle sent the next sublot to the server' -Journal $journal `
        -Criterion 'next-sublot-submitted' -TimeoutSeconds 60 `
        -Probe { @((Get-L2RealInbound $connection 'SublotSubmitted') | Where-Object { [string]$_.Payload.sublot -eq $nextSublot }).Count } `
        -Until { param($v) $v -ge 1 }
} catch {
    $nextError = $_.Exception.Message
    $journal.Note("Next demand did not get through: $nextError")
}
$assertions.Add(
    'L2-CR-07', '重连之后车还接得了单：下一条需求被受理并派车，车在取货点收下扫码并交给服务端',
    ([int]$nextSubmitted -ge 1), 'SublotSubmitted 1 条',
    $(if ($nextError) { "未走到：$nextError" } else { "SublotSubmitted $nextSubmitted 条" }))

# 在收尾判连接，不在重连刚完成的那一刻（real-onboard-durable-ack-lost 头注释里 MVP -002 那一例）。
$traffic = Get-L2RealTraffic $proxy
$connections = @($traffic.connections)
$open = @($connections | Where-Object { $null -eq $_.closedAt })
$assertions.Add(
    'L2-CR-08', '断开一次只换来一次重连：之后只有一条连接，而且到收尾还开着（没有哪一端撕会话）',
    ($connections.Count -eq $lastBefore + 1 -and $open.Count -eq 1),
    "$($lastBefore + 1) connections, 1 open",
    "$($connections.Count) connections, $($open.Count) open ($(Format-L2RealConnections $traffic))")

$journal.Note('Scenario finished.')
