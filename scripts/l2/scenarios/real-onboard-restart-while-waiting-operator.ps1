#Requires -Version 7

<#
车载端在「开了锁、等操作员」时进程没了，它不在的时候操作员照常把货放好、把门关上；重启之后车载端要按实时 IO 补交
这一次装货的结果，服务端照常提交、会话回到 `Ready`、不进恢复，旅程照常走完。

**来源与成对的两端票。**`trytoreachpeak0/8005-agv-program#61` 审计第 4 节 ②：车载端 `6846e98`（v2 上是
onboard-hmi#70，批次5-15，中断结算）与服务端结算恢复命令的半边 control-server#78（批次5-09）「快照确认与恢复命令
结算两半一起验」。这一条验的是 ADR-cross-0058 决策 2 的正面：重启后按实时 IO 结算，开过的仓全到最终态（已知、锁闭、
占用符合、开锁输出复位）报 `COMPLETED`，否则 `UNKNOWN`。它的反面（门关了但没放货 → `UNKNOWN` → 补偿）在
`real-onboard-compensate-then-reconnect` 的前半段。

**MVP 线参照**：`ControlServer_MVP` 同名场景。那边 2026-09-11 现场窗口一实测过两端互相等（车载端握手上报了没了结的
attempt 却不补交结果，服务端不判 Ready、不 Block，恢复入口要求 Blocked 而被拒），修复后 cs#40 回归 `-010` 绿。MVP 版
走的是「空关 → UNKNOWN → 补偿」；v2 版按票面改走正面，反面挪进 `real-onboard-compensate-then-reconnect`。

**为什么是杀进程，不是断网**：执行器挂在车载端服务的生命周期上，不跟连接走，断网重连时操作还在跑。只有进程真的没了，
那次 attempt 才成了没人认领的孤儿。`StopComponent` 用的是 `Kill`，与断电同形。

判据只读服务端库与模拟器快照，不读车载端界面文字。
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

# The load command queued for this attempt: the SlotOperationCommand whose payload names it.
function Get-LoadCommand([string]$attemptId) {
    return @((Get-L2RealOutbound $connection 'SlotOperationCommand') | Where-Object {
            [string]$_.Payload.slotOperationAttemptId -eq $attemptId }) | Select-Object -First 1
}

# --- 1. 起点：车载端开了锁、在等操作员 ---------------------------------------------------------------------------

$load = Start-L2RealLoad $Context 'L2-RW'
$demandId = $load.DemandId
$attemptId = $load.AttemptId
$slot = $load.Slot

$doorAtWait = (Get-L2RealSlotReading $simulator $slot) -split '/' | Select-Object -First 1
$statusAtWait = Get-L2RealScalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
$commandAtWait = Get-LoadCommand $attemptId
$assertions.Add(
    'L2-RW-01', '起点到位：车载端开了锁、在等操作员，门开着，装货操作 Prepared，装货命令还没被结算',
    ($doorAtWait -eq 'OPEN' -and $statusAtWait -eq 'Prepared' -and $null -ne $commandAtWait -and -not $commandAtWait.Acknowledged),
    'OPEN / Prepared / 命令未结算',
    "$doorAtWait / $statusAtWait / $(if ($null -eq $commandAtWait) { '(no command)' } elseif ($commandAtWait.Acknowledged) { '命令已结算' } else { '命令未结算' })")

# --- 2. 进程没了；操作员照常放货、关门 ---------------------------------------------------------------------------

$sessionBefore = Get-L2RealSession $connection $Context.AgvId
# 杀之前的两个基线：服务端收下过多少条报文，以及旅程运行时转到第几轮。下面两条都要用到。
$inboxBefore = Get-L2RealCount $connection 'SELECT COUNT(*) AS Total FROM ProtocolInbox'
$roundsBefore = [long]$Context.Riot.Snapshot().body.mapStationReads
& $Context.StopComponent 'onboard-hmi'

# 进程没了之后服务端一个字都没收到：结果只可能在重启之后才来，否则下面的判据证的就不是重启。
#
# 杀完立刻读是不够的（control-server#204）：车载端可能在被杀前一刻把结果发了出去，而服务端还没落库，
# 那一刻读到的「0 行」说的是「还没写进来」，不是「它没发过」——而后面每一条判据都会把重启后那条结果
# 当成重启的产物。这是假绿，不是假红。
#
# 票面给的两个等待对象在这条 rig 上都不存在：服务端在连接结束时只解绑路由、不写库（OnboardPeer.Detach），
# 而这条场景的 setup 是纯真装置、没有协议故障代理，看不到连接关闭。所以等的是这条判据真正需要的那个
# 事实本身——**服务端不会再往收件箱里写东西了**。车载端进程已经没了，不可能再发；收件箱行数稳定下来，
# 就意味着它在死前发出的一切都已经落库。余量按数量级取：一条报文从读到落库是毫秒级的事。
# 光有「行数不再增加」是不够的：**服务端自己卡住时，行数一样不增加**（在等锁、线程池饿死、正在重连），
# 而那不是「它发出的一切都落库了」，是「什么都没在处理」——两种状态含义相反，弱判据分不开。所以再要两件事：
#   - 等待期间旅程运行时至少又转了两轮（假 RIoT 的 mapStationReads，与 Wait-L2Iterations 同一个计数）。
#     **它断的是 JourneyRuntimeEngine 那个循环还在转，不是车载端入站通路还在转**——入站 TCP 读循环卡死、
#     消息处理器死锁在非数据库的锁上，都不会让 mapStationReads 停。所以它只排除「整个服务端不动了」这一类，
#     真正把「那条结果是重启之后来的」钉死的是下面第 3 节的 `L2-RW-04`。
#
# 杀前的收件箱行数只记下来、写进「实际」栏作诊断，**不进判据**：`ProtocolInbox` 只增不减，
# 「行数不少于基线」恒真，写成合取项会看起来像一条判据而一件事都不证明。
$inboxSettleFor = [TimeSpan]::FromSeconds(2)
$settleRounds = 2
$inboxCount = -1
$inboxStableSince = [DateTimeOffset]::UtcNow
$settled = Wait-L2RealOrLast -Description 'the server kept running yet stopped writing to its inbox after the onboard process went away' `
    -Journal $journal -Criterion 'inbox-settled' -TimeoutSeconds 60 `
    -Probe {
        $now = [DateTimeOffset]::UtcNow
        $count = Get-L2RealCount $connection 'SELECT COUNT(*) AS Total FROM ProtocolInbox'
        if ($count -ne $script:inboxCount) { $script:inboxCount = $count; $script:inboxStableSince = $now }
        [pscustomobject]@{
            Count     = $count
            StableFor = $now - $script:inboxStableSince
            Rounds    = [long]$Context.Riot.Snapshot().body.mapStationReads - $roundsBefore
        }
    } `
    -Until { param($v) $v.StableFor -ge $inboxSettleFor -and $v.Rounds -ge $settleRounds }
$settledText = if ($null -eq $settled) { '(收件箱读不到)' } else {
    "收件箱 $($settled.Count) 行（杀前 $inboxBefore），稳定 $([math]::Round($settled.StableFor.TotalSeconds, 1)) s，其间运行时转了 $($settled.Rounds) 轮" }
$journal.Note("The onboard process is gone and the server's inbox settled while the runtime kept turning: $settledText.")

$resultsWhileDown = Get-L2RealCount $connection "SELECT COUNT(*) AS Total FROM OperationResults WHERE SlotOperationAttemptId = '$attemptId'"
$statusWhileDown = Get-L2RealScalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
$assertions.Add(
    'L2-RW-02', "车载端退出时没有留下结果：服务端的旅程运行时仍在转（又转过 $settleRounds 轮以上）而收件箱不再增长，此时 OperationResults 0 行，装货操作仍是 Prepared",
    ($null -ne $settled -and $settled.StableFor -ge $inboxSettleFor -and $settled.Rounds -ge $settleRounds -and
        $resultsWhileDown -eq 0 -and $statusWhileDown -eq 'Prepared'),
    "收件箱稳定 $($inboxSettleFor.TotalSeconds) s 以上 / 运行时 $settleRounds 轮以上 / 0 行 / Prepared",
    "$settledText / $resultsWhileDown 行 / $statusWhileDown")

$journal.Note("While the onboard is down the operator loads slot $slot and closes it.")
$null = $simulator.Command('Put', "slots/$slot/cargo", @{ state = 'OCCUPIED' })
$null = $simulator.Command('Post', "slots/$slot/close-door", @{})
$closed = Wait-L2Condition -Description 'the slot reads closed, occupied, locked and reset' `
    -Journal $journal -Criterion 'slot-closed-occupied' -TimeoutSeconds 30 `
    -Probe { Get-L2RealSlotReading $simulator $slot } -Until { param($v) $v -eq 'CLOSED/OCCUPIED/1/0' }
$journal.Note("Slot $slot now reads $closed.")

# --- 3. 重启：车载端从自己的 journal 恢复，按实时 IO 结算 --------------------------------------------------------

# Assigned first: @() around the call would keep the returned array as one element and always count 1.
$reportsBeforeRows = Get-L2RealInbound $connection 'RecoveryStateReport'
$reportsBefore = $reportsBeforeRows.Count
# 重启的时刻。`L2-RW-02` 否定「退出时留下了结果」，而它读的是一个瞬间的快照；把那句否定钉死的是它的
# 正面——重启之后收到的那条结果是**唯一**的一条，而且是在重启之后才到的。审查（cs#204 独立审查中等 4）
# 指出 `Rounds >= 2` 只证明引擎循环在转、不证明入站通路在转，这一条补的就是那个缺口。
$restartAt = [DateTimeOffset]::UtcNow
$null = & $Context.RestartOnboard

$report = Wait-L2Condition -Description 'the restarted onboard sent its RecoveryStateReport' `
    -Journal $journal -Criterion 'restart-report' -TimeoutSeconds 120 `
    -Probe { $all = Get-L2RealInbound $connection 'RecoveryStateReport'; if ($all.Count -gt $reportsBefore) { $all[-1] } else { $null } } `
    -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-RW-03', '重启后车载端握手如实上报那一次没了结的 attempt',
    ([string]$report.Payload.unsettledSlotOperationAttemptId -eq $attemptId),
    $attemptId, [string]$report.Payload.unsettledSlotOperationAttemptId)

$result = Wait-L2RealOrLast -Description 'the restarted onboard settled the interrupted load and the server acknowledged it' `
    -Journal $journal -Criterion 'interrupted-result' -TimeoutSeconds 120 `
    -Probe { @((Get-L2RealInbound $connection 'OperationResult') | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $attemptId })[0] } `
    -Until { param($v) $null -ne $v -and $v.Response -eq 'DurableAck' }
$slotResult = if ($null -ne $result) { @($result.Payload.slotResults | Where-Object { [int]$_.slotNo -eq $slot })[0] } else { $null }
$resultShape = if ($null -ne $slotResult) {
    "$($result.Payload.overallOutcome) / $($slotResult.outcome) / $($slotResult.finalPhysicalState) / $($slotResult.lockState) / $($slotResult.unlockOutputState) → $($result.Response)"
} else { '(no result)' }
# 这一次 attempt 的结果**只有一条，而且是重启之后才到的**——`L2-RW-02` 那句「退出时没留下结果」的正面。
# 它比 `L2-RW-02` 里那条「运行时又转了两轮」硬：那一条只说明 JourneyRuntimeEngine 的循环在转，不说明
# 车载端入站通路在转（审查 cs#204 中等 4）。这里读的是入站通路的产物本身。
#
# 顺带记下一处既有的部分兜底：上面取结果用的是 `@(...)[0]`，**取的是最早的那一条**。真要是退出前
# 留下过一条，`[0]` 拿到的就是那一条，`$resultsAfterRestart` 会是 2、`At` 也会早于 `$restartAt`，
# 两个合取项都会红。这不是刻意设计的防线，但它确实在挡，写下来免得下次有人把它改成 `[-1]`。
$resultsForAttempt = @((Get-L2RealInbound $connection 'OperationResult') | Where-Object {
        [string]$_.Payload.slotOperationAttemptId -eq $attemptId })
$resultAt = if ($null -ne $result) { $result.At } else { $null }
$resultAfterRestart = ($null -ne $resultAt -and $resultAt -gt $restartAt)
$assertions.Add(
    'L2-RW-04', '重启后车载端按实时 IO 补交结果：仓位全到最终态，报 COMPLETED，物理字段是重启后读到的 OCCUPIED / LOCKED / RESET，服务端 DurableAck 收下；这一次 attempt 的结果只有这一条，而且是重启之后才到的（ADR-cross-0058 决策 2）',
    ($resultShape -eq 'COMPLETED / COMPLETED / OCCUPIED / LOCKED / RESET → DurableAck' -and
        $resultsForAttempt.Count -eq 1 -and $resultAfterRestart),
    'COMPLETED / COMPLETED / OCCUPIED / LOCKED / RESET → DurableAck / 结果 1 条 / 晚于重启',
    "$resultShape / 结果 $($resultsForAttempt.Count) 条 / $(if ($null -eq $resultAt) { '(无结果)' } elseif ($resultAfterRestart) { "晚于重启 $([math]::Round(($resultAt - $restartAt).TotalSeconds,1)) s" } else { '**早于重启**' })")

# --- 4. 不进恢复：会话回到 Ready，装货提交，旅程往关卡走 ---------------------------------------------------------

$sessionAfter = Wait-L2RealOrLast -Description 'the session was Ready again in a newer generation' `
    -Journal $journal -Criterion 'session-ready-after-restart' -TimeoutSeconds 60 `
    -Probe { Get-L2RealSession $connection $Context.AgvId } `
    -Until { param($v)
        $null -ne $v -and [long]$v.SessionGeneration -gt [long]$sessionBefore.SessionGeneration -and
            [string]$v.Readiness -eq 'Ready' -and [string]$v.ReasonCode -eq 'READY' }
$stage = Wait-L2RealOrLast -Description 'the load committed and the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-L2RealStage $connection $demandId } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$loadStatus = Get-L2RealScalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
$recoverySessions = Get-L2RealCount $connection 'SELECT COUNT(*) AS Total FROM ExceptionRecoverySessions'
$workflows = Get-L2RealCount $connection 'SELECT COUNT(*) AS Total FROM RecoveryWorkflows'
$blockReason = Get-L2RealScalar $connection "SELECT BlockReasonCode AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-RW-05', '不进恢复：服务端在新世代判会话 Ready，装货 Committed，旅程直接往关卡走、从未停摆，没有恢复会话也没有恢复工作流',
    ($null -ne $sessionAfter -and [string]$sessionAfter.Readiness -eq 'Ready' -and
        [long]$sessionAfter.SessionGeneration -gt [long]$sessionBefore.SessionGeneration -and
        $loadStatus -eq 'Committed' -and $stage -eq 'AwaitingGateArrival' -and $null -eq $blockReason -and
        $recoverySessions -eq 0 -and $workflows -eq 0),
    "gen > $($sessionBefore.SessionGeneration) Ready / Committed / AwaitingGateArrival / 无停摆 / 恢复会话 0 / 工作流 0",
    "$(Format-L2RealSession $sessionAfter) / $loadStatus / $stage / 停摆 $(if ($blockReason) { $blockReason } else { '无' }) / 恢复会话 $recoverySessions / 工作流 $workflows")

# 重启前挂着的那条装货命令被这份结果结算掉，不会被重放进之后的会话。结算是引擎某一轮里的写入
# （JourneyRuntimeEngine → SettleAnsweredCommandAsync），不与阶段推进必然同一次提交，所以要等，不直读。
$commandAfter = Wait-L2RealOrLast -Description 'the load command was settled by the result' `
    -Journal $journal -Criterion 'load-command-settled' -TimeoutSeconds 30 `
    -Probe { Get-LoadCommand $attemptId } -Until { param($v) $null -ne $v -and $v.Acknowledged }
$assertions.Add(
    'L2-RW-06', '重启前挂着的装货命令被补交的结果结算掉',
    ($null -ne $commandAfter -and $commandAfter.Acknowledged), '已结算',
    $(if ($null -eq $commandAfter) { '(no command)' } elseif ($commandAfter.Acknowledged) { '已结算' } else { '仍挂着' }))

if ($stage -ne 'AwaitingGateArrival') {
    Add-L2RealNotReached $assertions @('L2-RW-07') "旅程没走到关卡（stage $stage）"
    return
}

# --- 5. 旅程照常走完 ------------------------------------------------------------------------------------------

$gateIntent = Wait-L2RealIntent $Context $demandId 'TO_GATE' 60
Move-L2RealVehicleTo $Context $gateIntent $Context.GateStationRiotId 'the gate'
$unloadAttempt = Wait-L2Condition -Description 'the server issued the unload command' `
    -Journal $journal -Criterion 'unload-attempt' -TimeoutSeconds 180 `
    -Probe { Get-L2RealScalar $connection "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Unload'" } `
    -Until { param($v) $v }
$unloadWaiting = Wait-L2Condition -Description 'the onboard is waiting for the operator at the gate' `
    -Journal $journal -Criterion 'unload-waiting-operator' -TimeoutSeconds 120 `
    -Probe { @((Get-L2RealProgress $connection $unloadAttempt) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })[0] } `
    -Until { param($v) $null -ne $v }
$unloadSlot = [int]$unloadWaiting.Active[0]
$null = $simulator.Command('Put', "slots/$unloadSlot/cargo", @{ state = 'EMPTY' })
$null = $simulator.Command('Post', "slots/$unloadSlot/close-door", @{})

$final = Wait-L2RealOrLast -Description 'the journey completed at the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-L2RealStage $connection $demandId } -Until { param($v) $v -eq 'Completed' }
$demandStatus = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
$loadResults = Get-L2RealCount $connection "SELECT COUNT(*) AS Total FROM OperationResults WHERE SlotOperationAttemptId = '$attemptId'"
$assertions.Add(
    'L2-RW-07', '旅程照常走完：卸的是装货那一仓，需求 Succeeded，装货结果只记了一次',
    ($final -eq 'Completed' -and $demandStatus -eq 'Succeeded' -and $unloadSlot -eq $slot -and $loadResults -eq 1),
    "Completed / Succeeded / 仓 $slot / 1", "$final / $demandStatus / 仓 $unloadSlot / $loadResults")

$journal.Note('Scenario finished: an onboard killed while waiting for the operator settled the interrupted load from live IO and the journey went on without recovery.')
