#Requires -Version 7

<#
超时不放货：装载跑掉车载端自己的操作员超时，旅程停摆，整台车停摆。

方案第 4 节标 ★ 的三条之一，也是 2026-09-03 现场那一下午真正止步的地方。现场的形状是：车载端跑掉
了自己的 workflow.operationTimeoutMs（120 s），**上报了一份不完美的 OperationResult**，服务端
ApplyOperationResultAsync 判 RecoveryRequired，journey 进 Blocked / LOAD_RESULT_REQUIRES_RECOVERY。

所以这里用 Manual 策略加一次 completed=false、**且仓位状态 UNKNOWN** 的应答，**不是 Silent**。
UNKNOWN 那一半同样是前提：ADR-cross-0058 决策 5 之后，仓位状态明确的装载失败结算为确定失败而不再
进恢复，只有说不清发生了什么的结果才走这条路。Silent 复现的是另一件事：
车载端根本不应答，StationOperations 停在 Prepared，AdvanceAsync 的 AwaitingLoadResult 分支走
else { return; }，旅程停在 AwaitingLoadResult 而**不会进 Blocked**。那也是一条值得有的场景，但它
不是这一条。

后半段证的是 4ad840b 修的第二件事：车载端关机之后，「在等哪一种恢复」这个唯一的诊断还在。现场留下
的记录读作 Blocked / ONBOARD_SESSION_NOT_READY，两个词都没说清在等什么。

**这条场景到 Blocked 为止，不跑到「恢复并继续」。**出口是五步恢复握手，而**合成对端不发起它**。

两端的能力现在都在了：车载端在 8005-agv-onboard-hmi 的 60a0efd 实现了 RESUME_AFTER_REPAIR 的
车载端路径；服务端在 1372a89 补上了最后一段——同一操作在拿到合法授权后收得下那一份替换
OperationResult，并校验恢复 action、原始命令哈希、需求和仓位范围（L1 在
RecoveryStateMachineG2Tests）。所以卡住这条场景的**不再是能力缺失，是这一层用的对端**：
tools/ControlServer.FakeOnboard 按策略应答，不会自己发起会话申请和恢复动作。

给它编出那五条出站消息只会让这条场景变绿而证不出任何新东西——服务端那一侧 L1 已经证过了。要把
「恢复并继续装载」跑成真的，得写一条 Onboard = 'Real' 的场景，让出厂的那个 WPF 自己走完握手。
在那之前，停在「正确地停摆」是这一层能诚实证明的全部。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

# 第二条需求：用来证明这台车在 Blocked 期间不再受理任何新活。
$nextGuid = [guid]::NewGuid()
$nextIdWire = $nextGuid.ToString('N')
$nextId = $nextGuid.ToString('D')
$nextSublot = "L2-SUBLOT-NEXT-$($Context.RunId)"

function Get-Runtime([string]$id) {
    # HasConversion<string>：这些列存的是枚举成员名，按序数读会抛异常。
    $rows = Get-L2Journey -Connection $connection -DemandId $id
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stage([string]$id) {
    $runtime = Get-Runtime $id
    if ($null -eq $runtime) { return $null }
    return [string]$runtime.Stage
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-PendingOperationKey {
    $pending = @($onboard.Snapshot().body.pending |
        Where-Object { $_.messageType -eq 'SlotOperationCommand' })
    if ($pending.Count -eq 0) { return $null }
    return [string]$pending[0].key
}

# --- 1. 走到装载指令下发为止，和 normal-load 一样 ---------------------------------------------------

# 唯一注入的一处：装载结果挂起，由场景决定它是什么。其余三类应答保持 Auto。
$null = $onboard.Command('Put', 'policy', @{ loadResult = 'Manual' })

$journal.Note("Publishing demand $demandIdWire (sublot $sublot).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage $demandId } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-UpperId -purpose 'TO_PICKUP'; if ($row -and $row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }

$journal.Note('Vehicle departs for the pickup station.')
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $pickupIntent.OrderId
})

$journal.Note('Vehicle arrives at the pickup station and comes to rest.')
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $Context.PickupStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 5 })

# --- 2. 装载指令到了车载端，操作员超时跑掉了 ---------------------------------------------------------

$operationKey = Wait-L2Condition -Description 'the load command reached the peer and is waiting for a result' `
    -Journal $journal -Criterion 'pending-operation' -TimeoutSeconds 120 `
    -Probe { Get-PendingOperationKey } -Until { param($v) $null -ne $v }

$stage = Get-Stage $demandId
$assertions.Add(
    'L2-LR-01', '装载指令已下发，旅程在等结果',
    ($stage -eq 'AwaitingLoadResult'), 'AwaitingLoadResult', $stage)

# determinate = $false 是这条场景的**前提**，不是可调参数。ADR-cross-0058 决策 5 之后，判定的分界
# 线画在确定性上而不在 FAILED 这个词上：仓位状态明确、门已锁、开锁输出已复位的装载失败结算为
# StationOperationStatus.Failed，原地等 LoadTaskCancellation，**不 Block**。那正是这条场景验不到的
# 东西。要 RecoveryRequired 就得有一份真正说不清的结果，所以仓位报 UNKNOWN。
#
# 决策 5 落地时（`5f8f5a7d`）这条场景没跟着换载体，于是它连挂了五次 CI：合成对端当时只会发
# EMPTY + LOCKED + RESET，那在旧语义下是唯一的失败形状、在新语义下成了确定失败，旅程不再 Block，
# 场景等 Blocked 等到 120 秒超时。同一次改动里那六条 L1 测试是换了载体的，这一层漏了。
$journal.Note("Operator timeout ran out on the peer; it reports an OperationResult it cannot account for ($operationKey).")
$null = $onboard.Command('Put', "answer/$operationKey", @{ completed = $false; determinate = $false })

# --- 3. 服务端判 RecoveryRequired，旅程停摆 ---------------------------------------------------------

$stage = Wait-L2Condition -Description 'the journey blocked on the incomplete load result' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage $demandId } -Until { param($v) $v -eq 'Blocked' }
$assertions.Add('L2-LR-02', '旅程进入 Blocked', ($stage -eq 'Blocked'), 'Blocked', $stage)

$blockReason = [string](Get-Runtime $demandId).BlockReasonCode
$assertions.Add(
    'L2-LR-03', '阻塞原因是 LOAD_RESULT_REQUIRES_RECOVERY',
    ($blockReason -eq 'LOAD_RESULT_REQUIRES_RECOVERY'), 'LOAD_RESULT_REQUIRES_RECOVERY', $blockReason)

$loadRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'"
$assertions.Add(
    'L2-LR-04', '装载操作判 RecoveryRequired（不是 Committed，也不是停在 Prepared）',
    ($loadRows.Count -eq 1 -and [string]$loadRows[0].Status -eq 'RecoveryRequired'),
    'RecoveryRequired', $(if ($loadRows.Count -eq 1) { [string]$loadRows[0].Status } else { '(no load row)' }))

$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-LR-05', '需求状态转为 RecoveryRequired',
    ($demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'RecoveryRequired'),
    'RecoveryRequired', $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' }))

# 没有出发前安全检查，也没有第二条 RIoT 单：装载没成，车不该被派去关卡。
$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-LR-06', '装载没成时不建 TO_GATE 单（RIoT 单仍只有一条）',
    ($riotOrders.Count -eq 1), 1, $riotOrders.Count)

# --- 4. 会话离开 Ready，阻塞诊断必须留下来 ----------------------------------------------------------

# 装载失败把开锁输出留在未复位的状态，车载端据实报告——这是现场那台车真实会报的东西，也是让会话
# 离开 Ready 的那条路。
#
# 注意：**光是把对端进程杀掉并不会让会话离开 Ready。**服务端的 SessionRecoveries 行不由连接断开
# 驱动，存活性走的是另一条路——ReadOnboardFactsAsync 给该会话代次最后一条入站消息计龄
# （JourneyRuntimeEngine.cs 里那段注释写的就是「a dead peer leaves a Ready row」）。这条场景第
# 一次写成「杀进程然后等 Readiness 翻转」，等满 60 秒读到的仍然是 Ready。
$journal.Note('The failed load left the unlock output unreset; the peer reports it.')
$null = $onboard.Command('Put', 'safety', @{
    departureSafe         = $false
    allUnlockOutputsReset = $false
    reasonCodes           = @('UNLOCK_OUTPUT_NOT_RESET')
})

# 等的是**服务端已经消化了那条不安全事实**，不是「会话离开了 Ready」。原来等的是后者，而自
# 2026-09-04（`147f02c`）起，被拒的装载结果自己就会把会话推离 Ready，探针于是在安全事实还没到达
# 时就立刻返回，读到的是上一条原因码 `OPERATION_RECOVERY_REQUIRED`。等 `DepartureSafe` 翻成 0 才
# 是这一步真正在等的事；判据本身仍然是从原因码读出来的，没有变成「等 X 再断言 X」。
$null = Wait-L2Condition -Description 'the server consumed the unsafe departure fact' `
    -Journal $journal -Criterion 'session-departure-safe' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT DepartureSafe FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'"
        if ($rows.Count -eq 0) { return $null }
        return [string]$rows[0].DepartureSafe
    } -Until { param($v) $v -eq '0' -or $v -eq 'False' }
$sessionRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'"
$sessionReason = [string]$sessionRows[0].ReasonCode
$journal.Observe('session-readiness', "$([string]$sessionRows[0].Readiness) / $sessionReason", $null)
# 开锁未复位本来会被「这是服务端自己的命令造成的」豁免，但那条豁免要求存在一个 Prepared 的站点操作。
# 操作已经转为 RecoveryRequired，豁免因此不再成立——这是对的：需要恢复的命令不再是在途命令。
$assertions.Add(
    'L2-LR-07', '装载转入恢复后，它造成的不安全不再被自身命令豁免',
    ($sessionReason -eq 'DEPARTURE_SAFETY_NOT_READY'), 'DEPARTURE_SAFETY_NOT_READY', $sessionReason)

# 否定判据要有界：让运行时确实又跑几轮，再说它没有把原因抹掉。不用 sleep。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$runtime = Get-Runtime $demandId
$assertions.Add(
    'L2-LR-08', '会话离开 Ready 后阻塞原因不被 ONBOARD_SESSION_NOT_READY 覆盖',
    ([string]$runtime.Stage -eq 'Blocked' -and
     [string]$runtime.BlockReasonCode -eq 'LOAD_RESULT_REQUIRES_RECOVERY'),
    'Blocked / LOAD_RESULT_REQUIRES_RECOVERY',
    "$([string]$runtime.Stage) / $([string]$runtime.BlockReasonCode)")

# --- 5. 车开走电源关掉去处置，诊断依然在 ------------------------------------------------------------

# 收尾时的快照抓不到一个已经被停掉的替身，所以先把它自己的那一份存进证据。
[IO.File]::WriteAllText(
    (Join-Path $Context.SnapshotRoot 'fake-onboard-before-shutdown.json'),
    ($onboard.Snapshot() | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))

$journal.Note('Vehicle is powered down for the repair, as it is on site.')
& $Context.StopComponent 'fake-onboard'

$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$runtime = Get-Runtime $demandId
$assertions.Add(
    'L2-LR-09', '车载端关机后阻塞原因仍在',
    ([string]$runtime.Stage -eq 'Blocked' -and
     [string]$runtime.BlockReasonCode -eq 'LOAD_RESULT_REQUIRES_RECOVERY'),
    'Blocked / LOAD_RESULT_REQUIRES_RECOVERY',
    "$([string]$runtime.Stage) / $([string]$runtime.BlockReasonCode)")

# --- 6. 这台车不再受理任何新需求 --------------------------------------------------------------------

$journal.Note("Publishing a second demand $nextIdWire (sublot $nextSublot) while the vehicle is blocked.")
$null = $mes.Command('Put', "demands/$nextIdWire", @{
    sublot      = $nextSublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

# 一条 Blocked 的 journey 仍然占着唯一的 active 位，所以 DiscoverAndAcceptAsync 根本不会被调用：
# 新需求连一条 backlog 记录都不该有。这正是 ADR-cross-0006 与 ADR-cross-0015 要求的——仓位物理
# 状态未经证实、dispatch lease 仍被持有时，不许把车派去干别的。
$nextBacklog = Invoke-L2Query -Connection $connection `
    -Sql "SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$nextId'"
$assertions.Add(
    'L2-LR-10', 'Blocked 期间新需求连候选评估都没进（没有 backlog 记录）',
    ($nextBacklog.Count -eq 0), 0, $nextBacklog.Count)

$nextAccepted = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$nextId'"
$assertions.Add(
    'L2-LR-11', '新需求没有被受理',
    ($nextAccepted.Count -eq 0), 0, $nextAccepted.Count)

$assertions.Add(
    'L2-LR-12', '新需求没有 journey',
    ($null -eq (Get-Stage $nextId)), '(none)', $(Get-Stage $nextId) ?? '(none)')

$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-LR-13', 'Blocked 期间没有为任何需求派过车（RIoT 单仍只有一条）',
    ($riotOrders.Count -eq 1), 1, $riotOrders.Count)

# 出口是五步恢复握手，合成对端不发起，所以这条场景到此为止。两端能力都已具备，欠的是一条
# Onboard = 'Real' 的场景，见文件头的说明。
$journal.Note('Scenario finished at Blocked; the recovery exit needs a real-onboard scenario.')
