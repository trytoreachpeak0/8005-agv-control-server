#Requires -Version 7

<#
一份真的 UNKNOWN 报上去、车上的人按下「补偿清空」之后，那条五步握手到底走不走得完。

`real-onboard-recovery-entry-on-unknown` **有意停在按钮前**：它只回答「人在车上有没有出路」。
这一条从它的终点接着走——开恢复会话、报动作、服务端授权、车载端在真 Modbus 上再闭环一次、
替换结论并对账——那是另一条判据链，混进上一条只会让一次失败说不清是哪一件事。

**为什么值得单独跑一趟。**入口开了是 2026-09-10 才被看见的事实（服务端 `8c6d400` 修在 09-04，
那条 L2 六天没跑到那条判据）。而**入口之后的握手，到今天没有任何一层证过**：服务端授权侧有 L1
（`RecoveryStateMachineG2Tests.AfterARefusedResultResumeIsRefusedButCompensationIsAuthorized`），
车载端请求侧有实现（`WireToGateBusinessService.RecoveryVectors.cs`），**两端合起来跑通过没有，
不知道**——合成对端不走界面，`load-result-requires-recovery` 明写「到 Blocked 为止」，G3 的恢复
向量走的也是合成对端。在真车上发现它断了，代价是一整个现场窗口；在这里发现，代价是一趟运行。

## 按的是「补偿清空」，不是「申请恢复」

HMI 上这是**两个按钮**，绑在两个不同的 `Can...` 属性上，走两条不同的向量：

- 「申请恢复」→ `RequestWireToGateRecoveryAsync` → `RESUME_AFTER_REPAIR`，恢复的是**停在物理断点、
  车辆还握着那个断点**的操作。本场景的状态里它会被服务端拒绝，而且拒得对：车辆记录结果时把
  attempt 与 `OperationContext` 一并清空，断点没了。
- 「补偿清空」→ `RequestLoadCompensationAsync` → `COMPENSATE_LOAD_ALL_EMPTY`，前置只要求「操作是
  Load 且 `RecoveryRequired`」，正是本场景制造的状态。

上一条场景的 `L2-RR-06` 读的是「申请恢复」在不在，它回答的是「有没有出路」这个更弱的问题。这一条
读的是「补偿清空」，因为要按下去的是它。

## 现场那三步不是布景，每一步都由执行器要求着

上一条场景留下的现场是 `lock-feedback-override FIXED_1`：锁反馈钉死在「已锁」，门却真的开着。
维护人员到场之后做三件事——**发现仓里真有一箱货、关门、修好传感器**——每一件都由
`WireToGateRecoveryVectorExecutor` 的代码要求着，都是 2026-09-10 实测出来的，不是想当然：

1. **门必须先关好并锁上。**`ValidateInitialSnapshot` 在开锁之前逐仓检查
   `UnlockOutputRaw is false` 与 `locker.IsLocked`，不满足就当场判 `LOCK_NOT_CLOSED` / 
   `UNLOCK_OUTPUT_NOT_RESET` 并返回 `FAILED`——**车辆不会去驱动一扇已经开着的门**。所以
   「关门」是补偿的入场券，HMI 上「申请恢复」那段确认词写的「所有目标仓门已锁好」就是这一条。
2. **传感器必须先修好。**清空一个仓位的第一步是 `PulseUnlockAsync` 之后等
   `locker.IsKnown && !locker.IsLocked`（`UnlockFeedbackTimeout` 3 s）；锁反馈永远读「已锁」
   时这一步必然超时，结果是又一份 `UNKNOWN`。那是「传感器坏了就补偿不了」，不是「握手断了」，
   而本票问的是后者。`mode = AUTO` 等价于现场把传感器修好。
3. **仓里得真有货，否则整个向量一次 IO 都不碰。**这一条是首跑红出来的，红证据
   `evidence/l2/20260910-real-onboard-recovery-compensate-load-002`：执行器在 `!correction`
   分支里对每个 `!locker.HasCargo` 的仓位**直接标 `COMPLETED`**，然后 `PREPARING` → 结束。
   那一跑服务端侧全绿——工作流 `Reconciled`、`Outcome = ALL_EMPTY`——**握手确实走完了，只是
   车载端什么都没做**，因为空仓本来就是它要达到的终态。判据要的「在真 Modbus 上把仓位清空」
   于是一次都没发生。

**那箱货是哪来的：车辆放弃之后放进去的。**这不是为了凑判据编的顺序，它正是这份 `UNKNOWN`
盖住的窗口——车辆报完不确定结论就不再看那一仓了，而门还开着，操作员并不知道它已经放弃。
维护人员到场时发现仓里有货，这恰恰是 `COMPENSATE_LOAD_ALL_EMPTY` 这个名字要处理的局面：
这一单要判死，而货得先出来。

（货不能更早放：装载还在跑的时候放进去，车辆读到的就是「已锁 + 有货」这个**达成态**，
决策 1 的目标态闭环会判成功，`UNKNOWN` 根本不会发生。）

## 补偿不产生「替换结果」

本票原文预期「`OperationResults` 两行，第一行 `SupersededByResultId` 指向第二行」。**那是
`RESUME_AFTER_REPAIR` 的形状**：只有 `WireToGateStore` 里那条 `RequireResumeAuthorizationAsync`
路径会把旧结果标成被替换。`LoadCompensationResult` 根本不走 `OperationResults`，它走
`OnboardRecoveryCoordinator.ApplyCurrentResultAsync`，终局是**把这一单判死**：需求 `Cancelled`、
仓位操作 `Cancelled`、业务键永久抑制、旅程记 `CANCELLED_BY_LOAD_COMPENSATION`。
`L2-RC-13` 就钉这一条，免得下一个人照着票里那句话去找第二行。

断言仍然只从服务端 SQLite 与模拟器 `/snapshot` 读。UI 只用来驱动。
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
$simulator = $Context.Simulator
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

$compensationButton = '补偿清空'

# SQLite 的可空列读回来可能是 [System.DBNull] 而不是 $null，两者都要当成空。
function Test-L2Null($value) {
    return ($null -eq $value) -or ($value -is [System.DBNull])
}

function Get-Runtime {
    # HasConversion<string>：这些列存的是枚举成员名，按序数读会抛异常。
    $rows = Get-L2Journey -Connection $connection -DemandId $demandId
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stage {
    $runtime = Get-Runtime
    if ($null -eq $runtime) { return $null }
    return [string]$runtime.Stage
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Slot([int]$slotNo) {
    return $simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $slotNo }
}

function Get-LoadOperation {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT SlotOperationAttemptId, Status FROM StationOperations
              WHERE DemandId = '$demandId' AND OperationType = 'Load'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Progress([string]$attemptId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT RequestJson FROM ProtocolInbox WHERE MessageType = 'OperationProgress' ORDER BY ReceivedAt"
    $matched = foreach ($row in $rows) {
        $payload = ([string]$row.RequestJson | ConvertFrom-Json).payload
        if ($payload.slotOperationAttemptId -eq $attemptId) { $payload }
    }
    return @($matched)
}

function Get-PhaseCount([string]$attemptId, [string]$phase) {
    return @(Get-Progress $attemptId | Where-Object { $_.phase -eq $phase }).Count
}

function Get-InboxCount([string]$messageType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT COUNT(*) AS Total FROM ProtocolInbox WHERE MessageType = '$messageType'"
    return [int]$rows[0].Total
}

# 车载端实发的那一份报文。服务端把证据折进自己的行时会丢掉 reasonCodes 之类的字段，而这条场景
# 要读的正是那些，所以判据读收件箱里的原报文——它同样是服务端自己的库，不是走捷径。
function Get-LastPayload([string]$messageType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT RequestJson FROM ProtocolInbox WHERE MessageType = '$messageType' ORDER BY ReceivedAt"
    if ($rows.Count -eq 0) { return $null }
    return ([string]$rows[-1].RequestJson | ConvertFrom-Json).payload
}

function Get-RecoverySession {
    $rows = Invoke-L2Query -Connection $connection -Sql 'SELECT * FROM ExceptionRecoverySessions'
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-CompensationWorkflow {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT * FROM RecoveryWorkflows WHERE WorkflowType = 'COMPENSATE_LOAD_ALL_EMPTY'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-SessionRecovery {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql 'SELECT Readiness, ReasonCode, PendingAttemptIdsJson FROM SessionRecoveries'
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-InboxRows([string]$messageType) {
    return @(Invoke-L2Query -Connection $connection `
        -Sql "SELECT RequestJson FROM ProtocolInbox WHERE MessageType = '$messageType' ORDER BY ReceivedAt")
}

# Wait-L2Condition 超时就抛异常，判据表里什么都不留。第 9 段的两条修之前必然超时（8005-agv-program#46），
# 超时正是它们的实际值，所以超时时再读一次世界交回给判据。
function Wait-L2ConditionOrLast {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][string]$Criterion,
        [Parameter(Mandatory)][scriptblock]$Probe,
        [Parameter(Mandatory)][scriptblock]$Until,
        [int]$TimeoutSeconds = 60
    )
    try {
        return Wait-L2Condition -Description $Description -Journal $journal -Criterion $Criterion `
            -TimeoutSeconds $TimeoutSeconds -Probe $Probe -Until $Until
    } catch {
        $journal.Note("Not reached: $($_.Exception.Message)")
        return & $Probe
    }
}

# 等车载端把门开到「在等操作员」为止。绝不能一看到 UNLOCKING 就动手：车载端要求锁反馈稳定
# feedbackStableMs 才认，WAITING_OPERATOR 是它自己发的、说明它确实观测到了门开着的那一段。
function Wait-WaitingOperator([string]$attemptId, [string]$criterion, [int]$Before) {
    $null = Wait-L2Condition -Description 'the onboard is waiting for the operator on the target slot' `
        -Journal $journal -Criterion $criterion -TimeoutSeconds 120 `
        -Probe { Get-PhaseCount $attemptId 'WAITING_OPERATOR' } `
        -Until { param($v) $v -gt $Before }
}

# --- 1. 需求出现，服务端受理并派车去取货点 ----------------------------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot) to the fake MesIngest catalog.")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$null = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-UpperId -purpose 'TO_PICKUP'; if ($row) { [string]$row.Status } else { $null } } `
    -Until { param($v) $v -eq 'CONFIRMED' }
$pickupIntent = Get-UpperId -purpose 'TO_PICKUP'

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

# --- 2. UIA 代替操作员扫码 -------------------------------------------------------------------------

$null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }

$journal.Note("Typing sublot $sublot into ScanTextBox through UI Automation.")
$onboard.SetSublot($sublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()
$journal.Note('Manual submit invoked.')

$null = Wait-L2Condition -Description 'the server received SublotSubmitted from the real onboard' `
    -Journal $journal -Criterion 'sublot-submitted' -TimeoutSeconds 60 `
    -Probe { Get-InboxCount 'SublotSubmitted' } -Until { param($v) $v -ge 1 }

# --- 3. 制造起点：锁反馈失效，车报回一份真的 UNKNOWN ------------------------------------------------
#
# 这一段与 real-onboard-recovery-entry-on-unknown 完全同形，那条场景把每一步为什么这样写都写清楚
# 了。这里只留一条判据钉住起点确实到位——本场景要证的是从这里往后的那条链。

$loadOperation = Wait-L2Condition -Description 'the server issued the load command' `
    -Journal $journal -Criterion 'load-attempt' -TimeoutSeconds 180 `
    -Probe { Get-LoadOperation } -Until { param($v) $null -ne $v }
$attemptId = [string]$loadOperation.SlotOperationAttemptId

$unlocking = Wait-L2Condition -Description 'the onboard started unlocking for the load' `
    -Journal $journal -Criterion 'load-unlocking' -TimeoutSeconds 120 `
    -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'UNLOCKING' })[-1] } `
    -Until { param($v) $v }
$activeSlots = @($unlocking.activeUnlockSlots)
if ($activeSlots.Count -ne 1) {
    throw "This scenario drives one slot; the load command targets $($activeSlots.Count) ($($activeSlots -join ', '))."
}
$loadSlot = [int]$activeSlots[0]
Wait-WaitingOperator $attemptId 'load-waiting-operator' 0

# 这一条永远等不到 LoadResult 的 LoadBatch 命令，要在补偿之前把 messageId 记下来：补偿结算掉它
# 之后它就不在未结算的那一批里了，而「它被结算了」正是本场景最贵的一条判据（8005-agv-program#28
# 查实的第二个洞——悬着的命令会被重放进后来的每一个会话并撕掉它）。
$membership = Invoke-L2Query -Connection $connection `
    -Sql "SELECT LoadCommandMessageId FROM JourneyDemands WHERE DemandId = '$demandId'"
$loadCommandMessageId = [string]$membership[0].LoadCommandMessageId
$loadCommandPendingBefore = Invoke-L2Query -Connection $connection `
    -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$loadCommandMessageId'"
$loadCommandWasPending = ($loadCommandPendingBefore.Count -eq 1 -and
    (Test-L2Null $loadCommandPendingBefore[0].AcknowledgedAt))

$journal.Note("Jamming slot $loadSlot lock feedback at LOCKED while the door stands open.")
$null = $simulator.Command('Put', "slots/$loadSlot/lock-feedback-override", @{ mode = 'FIXED_1' })

$stage = Wait-L2Condition -Description 'the journey blocked on the incomplete load result' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'Blocked' }
$blockReason = [string](Get-Runtime).BlockReasonCode
$loadStatus = [string](Get-LoadOperation).Status
$resultPayload = Get-LastPayload 'OperationResult'
$assertions.Add(
    'L2-RC-01', '起点到位：车报回一份 UNKNOWN，服务端判 RecoveryRequired 并停摆',
    ($stage -eq 'Blocked' -and $blockReason -eq 'LOAD_RESULT_REQUIRES_RECOVERY' -and
        $loadStatus -eq 'RecoveryRequired' -and
        $null -ne $resultPayload -and [string]$resultPayload.overallOutcome -eq 'UNKNOWN'),
    'Blocked / LOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired / UNKNOWN',
    "$stage / $blockReason / $loadStatus / $(if ($resultPayload) { [string]$resultPayload.overallOutcome } else { '(no result)' })")

# --- 4. 维护人员到场：发现仓里有货、关门、修传感器 -------------------------------------------------
#
# 三件事都是执行器的硬前置，见文件头。顺序也是现场的顺序：先看清仓里是什么，再把门关好，
# 最后修传感器——`FIXED_1` 期间读数就是「已锁」，门关上之后真实读数也是「已锁」，换成 AUTO
# 的那一刻读数不跳变，跳变的只是它从此说的是真话。

$journal.Note("Maintenance finds cargo in slot $loadSlot, closes it and repairs the lock feedback sensor.")
$null = $simulator.Command('Put', "slots/$loadSlot/cargo", @{ state = 'OCCUPIED' })
$null = $simulator.Command('Post', "slots/$loadSlot/close-door", @{})
$null = $simulator.Command('Put', "slots/$loadSlot/lock-feedback-override", @{ mode = 'AUTO' })

$repaired = Wait-L2Condition -Description 'the repaired slot reads closed, locked and loaded for real' `
    -Journal $journal -Criterion 'slot-repaired' -TimeoutSeconds 30 `
    -Probe {
        $slot = Get-Slot $loadSlot
        "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
    } `
    -Until { param($v) $v -eq 'CLOSED/OCCUPIED/1/0' }
$assertions.Add(
    'L2-RC-02', '现场读数变成真的：门关、仓里有货、锁反馈有效、开锁输出已复位——这正是补偿向量的入场券',
    ($repaired -eq 'CLOSED/OCCUPIED/1/0'),
    'CLOSED/OCCUPIED/1/0', $repaired)

# --- 5. 按下「补偿清空」 ---------------------------------------------------------------------------

$compensationAvailable = Wait-L2Condition -Description 'the compensation entry became available on the HMI' `
    -Journal $journal -Criterion 'onboard-compensation-entry' -TimeoutSeconds 120 `
    -Probe { $onboard.ButtonEnabled($compensationButton) } -Until { param($v) $v }
$assertions.Add(
    'L2-RC-03', "HMI 上出现可用的「$compensationButton」入口（它绑 CanRequestLoadCompensation，与「申请恢复」是两个按钮）",
    [bool]$compensationAvailable, $true, $compensationAvailable)

# 这两个基线必须在按下按钮之前取。补偿命令一到车上，车载端立刻就发新的
# UNLOCKING/WAITING_OPERATOR，而下面那几条握手判据自己要花几百毫秒——等它们读完再取基线，
# 基线里就已经含着要等的那一条了，于是「又开锁了一次」永远等不到。README 第 14 条那一类
# 「读完一个就顺手读下一个」的竞态，这是第四例；红证据
# `evidence/l2/20260910-real-onboard-recovery-compensate-load-004`，实测车辆在命令下发后
# 106 ms 就打了脉冲，而基线晚了它 90 ms。
$unlockingBefore = Get-PhaseCount $attemptId 'UNLOCKING'
$waitingBefore = Get-PhaseCount $attemptId 'WAITING_OPERATOR'

$journal.Note("Invoking the $compensationButton button and confirming the modal dialog.")
$onboard.InvokeButton($compensationButton)
$null = $onboard.Confirm($compensationButton)

# 第一步：开恢复会话。凭据两端各从自己的环境变量读，服务端只认
# MAINTENANCE_ADMINISTRATOR / SYSTEM_ADMINISTRATOR 两个角色。
$session = Wait-L2Condition -Description 'the server opened an exception recovery session' `
    -Journal $journal -Criterion 'recovery-session-open' -TimeoutSeconds 90 `
    -Probe { Get-RecoverySession } -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-RC-04', '第一步握手：恢复会话已开，作用域是这一单这一次 attempt，管理员角色已认证',
    ([string]$session.DemandId -eq $demandId -and
        [string]$session.AdministratorRole -eq 'MAINTENANCE_ADMINISTRATOR' -and
        ([string]$session.SlotsJson) -match "\b$loadSlot\b"),
    "$demandId / MAINTENANCE_ADMINISTRATOR / 含仓位 $loadSlot",
    "$([string]$session.DemandId) / $([string]$session.AdministratorRole) / $([string]$session.SlotsJson)")

# 第二、三步：报动作，服务端授权。COMPENSATE_LOAD_ALL_EMPTY 是唯一一个授权时**不当场下发命令**的
# 动作——工作流先进 AwaitingAuthorization，等车载端再发一条 LoadCompensationRequested 才下发。
$workflow = Wait-L2Condition -Description 'the server authorized COMPENSATE_LOAD_ALL_EMPTY' `
    -Journal $journal -Criterion 'recovery-action-authorized' -TimeoutSeconds 90 `
    -Probe { Get-CompensationWorkflow } -Until { param($v) $null -ne $v }
$session = Get-RecoverySession
$assertions.Add(
    'L2-RC-05', '第二、三步握手：车载端报的动作是 COMPENSATE_LOAD_ALL_EMPTY，服务端授权并选定它',
    ([string]$session.SelectedAction -eq 'COMPENSATE_LOAD_ALL_EMPTY' -and
        [string]$workflow.DemandId -eq $demandId -and
        [string]$workflow.SlotOperationAttemptId -eq $attemptId),
    "COMPENSATE_LOAD_ALL_EMPTY / $demandId / $attemptId",
    "$([string]$session.SelectedAction) / $([string]$workflow.DemandId) / $([string]$workflow.SlotOperationAttemptId)")

$commandBound = Wait-L2Condition -Description 'the compensation command was queued to the vehicle' `
    -Journal $journal -Criterion 'compensation-command' -TimeoutSeconds 90 `
    -Probe {
        $row = Get-CompensationWorkflow
        if ($null -eq $row -or (Test-L2Null $row.CommandMessageId)) { return 'pending' }
        return [string]$row.CommandMessageType
    } `
    -Until { param($v) $v -ne 'pending' }
$assertions.Add(
    'L2-RC-06', '服务端在收到 LoadCompensationRequested 之后才下发补偿命令，并把它绑在工作流上',
    ($commandBound -eq 'LoadCompensationCommand' -and (Get-InboxCount 'LoadCompensationRequested') -ge 1),
    'LoadCompensationCommand / 收到 1 条 LoadCompensationRequested',
    "$commandBound / 收到 $(Get-InboxCount 'LoadCompensationRequested') 条")

# --- 6. 第四步：车载端在真 Modbus 上把仓位清空 -----------------------------------------------------

$unlockingAfter = Wait-L2Condition -Description 'the vehicle re-opened the slot for the compensation' `
    -Journal $journal -Criterion 'compensation-unlocking' -TimeoutSeconds 120 `
    -Probe { Get-PhaseCount $attemptId 'UNLOCKING' } `
    -Until { param($v) $v -gt $unlockingBefore }

# 门必须真的再弹开一次。上面那条 progress 只说车载端认为自己在开锁，这一条说现场确实动了——
# 补偿这一步是它与「服务端自己在库里改几行」的全部区别，也是首跑（证据 `-002`）没能证到的东西。
$reopened = Wait-L2Condition -Description 'the door popped open again under the compensation pulse' `
    -Journal $journal -Criterion 'compensation-door-open' -TimeoutSeconds 60 `
    -Probe { [string](Get-Slot $loadSlot).doorState } -Until { param($v) $v -eq 'OPEN' }
$assertions.Add(
    'L2-RC-07', '第四步握手：车载端在真 Modbus 上重新开锁，门再弹开一次',
    ($unlockingAfter -gt $unlockingBefore -and $reopened -eq 'OPEN'),
    "UNLOCKING > $unlockingBefore / 门 OPEN",
    "UNLOCKING = $unlockingAfter / 门 $reopened")

# 等它自己发 WAITING_OPERATOR 再动手，不看门的物理状态推进——README 第 7、13 条，两次都花掉一整趟。
Wait-WaitingOperator $attemptId 'compensation-waiting-operator' $waitingBefore
$journal.Note("Maintenance takes the cargo out of slot $loadSlot and closes the door.")
$null = $simulator.Command('Put', "slots/$loadSlot/cargo", @{ state = 'EMPTY' })
$null = $simulator.Command('Post', "slots/$loadSlot/close-door", @{})

# --- 7. 第五步：替换结论并对账 ---------------------------------------------------------------------

$null = Wait-L2Condition -Description 'the vehicle reported the compensation result' `
    -Journal $journal -Criterion 'compensation-result' -TimeoutSeconds 120 `
    -Probe { Get-InboxCount 'LoadCompensationResult' } -Until { param($v) $v -ge 1 }
$compensationPayload = Get-LastPayload 'LoadCompensationResult'
$compensatedSlot = @($compensationPayload.slotResults | Where-Object { [int]$_.slotNo -eq $loadSlot })[0]
$assertions.Add(
    'L2-RC-08', '车载端报回 ALL_EMPTY，那一仓 COMPLETED/EMPTY/LOCKED/RESET——服务端的对账判据要的正是这四样',
    ([string]$compensationPayload.overallOutcome -eq 'ALL_EMPTY' -and
        [string]$compensatedSlot.outcome -eq 'COMPLETED' -and
        [string]$compensatedSlot.finalPhysicalState -eq 'EMPTY' -and
        [string]$compensatedSlot.lockState -eq 'LOCKED' -and
        [string]$compensatedSlot.unlockOutputState -eq 'RESET'),
    'ALL_EMPTY / COMPLETED / EMPTY / LOCKED / RESET',
    ("$([string]$compensationPayload.overallOutcome) / $([string]$compensatedSlot.outcome) / " +
        "$([string]$compensatedSlot.finalPhysicalState) / $([string]$compensatedSlot.lockState) / " +
        "$([string]$compensatedSlot.unlockOutputState)"))

$workflowState = Wait-L2Condition -Description 'the server reconciled the compensation workflow' `
    -Journal $journal -Criterion 'compensation-reconciled' -TimeoutSeconds 90 `
    -Probe { $row = Get-CompensationWorkflow; if ($row) { [string]$row.State } else { $null } } `
    -Until { param($v) $v -in @('Reconciled', 'RecoveryRequired') }
$assertions.Add(
    'L2-RC-09', '第五步握手走完：服务端对账通过，工作流 Reconciled 而不是又一次 RecoveryRequired',
    ($workflowState -eq 'Reconciled'), 'Reconciled', $workflowState)

# 车报的那四个字段来自它自己的传感器读数；这一条从模拟器读同一时刻的现场，两边对得上才算数。
$finalSlot = Get-Slot $loadSlot
$finalReading = "$($finalSlot.doorState)/$($finalSlot.cargoState)/$($finalSlot.lockFeedbackRaw)/$($finalSlot.unlockOutputRaw)"
$assertions.Add(
    'L2-RC-10', '现场收在安全状态：门关、仓空、已锁、开锁输出复位',
    ($finalReading -eq 'CLOSED/EMPTY/1/0'),
    'CLOSED/EMPTY/1/0', $finalReading)

# --- 8. 终局：这一单被判死，不是被恢复着跑下去 -----------------------------------------------------

$demandStatus = Wait-L2Condition -Description 'the demand was settled by the compensation' `
    -Journal $journal -Criterion 'demand-settled' -TimeoutSeconds 90 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
        if ($rows.Count -eq 0) { return $null }
        return [string]$rows[0].Status
    } `
    -Until { param($v) $v -eq 'Cancelled' }
$operationStatus = [string](Get-LoadOperation).Status
$assertions.Add(
    'L2-RC-11', '补偿终结这一单：需求与仓位操作都判 Cancelled',
    ($demandStatus -eq 'Cancelled' -and $operationStatus -eq 'Cancelled'),
    'Cancelled / Cancelled', "$demandStatus / $operationStatus")

$stage = Wait-L2Condition -Description 'the journey recorded the compensation as its terminal reason' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'Completed' }
$runtime = Get-Runtime
$suppression = Invoke-L2Query -Connection $connection `
    -Sql "SELECT DemandId, ReasonCode FROM TransportDemandSuppressions WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-RC-12', '旅程以 CANCELLED_BY_LOAD_COMPENSATION 收尾，同一个理由把业务键永久抑制',
    ($stage -eq 'Completed' -and
        [string]$runtime.BlockReasonCode -eq 'CANCELLED_BY_LOAD_COMPENSATION' -and
        $suppression.Count -eq 1 -and
        [string]$suppression[0].ReasonCode -eq 'CANCELLED_BY_LOAD_COMPENSATION'),
    'Completed / CANCELLED_BY_LOAD_COMPENSATION / 抑制 1 条',
    ("$stage / $([string]$runtime.BlockReasonCode) / 抑制 $($suppression.Count) 条" +
        $(if ($suppression.Count -eq 1) { " ($([string]$suppression[0].ReasonCode))" } else { '' })))

# 本票原文预期这里有两行、第一行 SupersededByResultId 指向第二行。那是 RESUME_AFTER_REPAIR 的
# 形状——只有 WireToGateStore 的 RequireResumeAuthorizationAsync 那条路会标替换。补偿的结论根本
# 不经过 OperationResults，它把这一单判死。钉住这一条，免得下一个人照着那句话去找第二行。
$results = Invoke-L2Query -Connection $connection `
    -Sql "SELECT OverallOutcome, SupersededByResultId FROM OperationResults
          WHERE SlotOperationAttemptId = '$attemptId' ORDER BY ReceivedAt"
$assertions.Add(
    'L2-RC-13', '补偿不产生替换结果：OperationResults 仍只有那一份 UNKNOWN，且没有被标成被替换',
    ($results.Count -eq 1 -and [string]$results[0].OverallOutcome -eq 'UNKNOWN' -and
        (Test-L2Null $results[0].SupersededByResultId)),
    '1 份 / UNKNOWN / 未被替换',
    "$($results.Count) 份 / $(if ($results.Count -ge 1) { [string]$results[0].OverallOutcome } else { '(none)' }) / " +
        "$(if ($results.Count -ge 1 -and -not (Test-L2Null $results[0].SupersededByResultId)) { '已被替换' } else { '未被替换' })")

# 本场景最贵的一条。它永远等不到 LoadResult，只有关闭的批次会结算它；悬着就会被重放进后来的
# 每一个会话，对端按「同一个业务 id 内容变了」拒收并撕掉会话。判据在补偿之前先确认它确实挂着，
# 否则「已结算」可以被一个不存在的行满足。
$settled = Wait-L2Condition -Description 'the dangling LoadBatch command was settled by the compensation' `
    -Journal $journal -Criterion 'load-command-settled' -TimeoutSeconds 90 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$loadCommandMessageId'"
        if ($rows.Count -eq 0) { return $null }
        if (Test-L2Null $rows[0].AcknowledgedAt) { return 'pending' }
        return [string]$rows[0].AcknowledgedAt
    } `
    -Until { param($v) $v -ne 'pending' }
$assertions.Add(
    'L2-RC-14', '那条永远等不到 LoadResult 的 LoadBatch 命令，被补偿这一路同样结算掉了',
    ($loadCommandWasPending -and $settled -ne 'pending'),
    '补偿前挂着 / 补偿后已结算',
    "$(if ($loadCommandWasPending) { '补偿前挂着' } else { '补偿前就不是挂着的' }) / $settled")

# --- 9. 车还回来：会话回到 Ready，下一条需求派得出去、车收得下扫码 ----------------------------------
#
# 8005-agv-program#46。`-005`/`-006`/`-007` 三次绿里，这一单判死之后会话一直停在
# `RecoveryRequired / OPERATION_RECOVERY_REQUIRED`，而那时仓位操作早已 `Cancelled`：就绪只在握手、
# OperationResult 与 SafetyStateChanged 时重算，补偿对账之后没人再算一次。车载端只在自己的会话是
# READY 时才放行扫码，所以「车收下下一站的扫码」证的是车辆也被告知了，不只是服务端库里改了。

$sessionAfter = Wait-L2ConditionOrLast -Description 'the session returned to Ready after the compensation' `
    -Criterion 'session-ready-after-compensation' -TimeoutSeconds 60 `
    -Probe {
        $row = Get-SessionRecovery
        "$([string]$row.Readiness) / $([string]$row.ReasonCode)"
    } `
    -Until { param($v) $v -eq 'Ready / READY' }
$assertions.Add(
    'L2-RC-15', '补偿对账之后服务端会话自己回到 Ready，不是停在 OPERATION_RECOVERY_REQUIRED',
    ($sessionAfter -eq 'Ready / READY'), 'Ready / READY', $sessionAfter)
if ($sessionAfter -ne 'Ready / READY') {
    $journal.Note('Scenario stopped: the session did not return to Ready after the compensation.')
    return
}

$nextDemandGuid = [guid]::NewGuid()
$nextDemandIdWire = $nextDemandGuid.ToString('N')
$nextDemandId = $nextDemandGuid.ToString('D')
$nextSublot = "L2-SUBLOT-$($Context.RunId)-NEXT"

$journal.Note("Publishing the next demand $nextDemandIdWire (sublot $nextSublot).")
$null = $mes.Command('Put', "demands/$nextDemandIdWire", @{
    sublot      = $nextSublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Condition -Description 'the next demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'next-journey-stage' -TimeoutSeconds 120 `
    -Probe { $rows = Get-L2Journey -Connection $connection -DemandId $nextDemandId; if ($rows.Count -gt 0) { [string]$rows[0].Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$nextIntent = Wait-L2Condition -Description 'the next TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'next-to-pickup-intent' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$nextDemandId' AND Purpose = 'TO_PICKUP'"
        if ($rows.Count -gt 0) { $rows[0] } else { $null }
    } `
    -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }

$journal.Note('Vehicle drives to the pickup station for the next demand.')
$null = $riot.Command('Put', "orders/$($nextIntent.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $nextIntent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $Context.PickupStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($nextIntent.UpperId)", @{ orderState = 5 })

# 先等服务端进入 AwaitingSublot 再扫：车载端提交之后不清空上一轮的录入请求，输入框可能早就是可用的。
$null = Wait-L2Condition -Description 'the next journey waits for a sublot' `
    -Journal $journal -Criterion 'next-awaiting-sublot' -TimeoutSeconds 120 `
    -Probe { $rows = Get-L2Journey -Connection $connection -DemandId $nextDemandId; if ($rows.Count -gt 0) { [string]$rows[0].Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingSublot' }
$null = Wait-L2Condition -Description 'the onboard HMI accepts sublot entry for the next demand' `
    -Journal $journal -Criterion 'next-onboard-can-submit' -TimeoutSeconds 120 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
$onboard.SetSublot($nextSublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled for the next sublot' `
    -Journal $journal -Criterion 'next-onboard-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()

$nextSubmitted = Wait-L2ConditionOrLast -Description 'the vehicle sent the next sublot to the server' `
    -Criterion 'next-sublot-submitted' -TimeoutSeconds 60 `
    -Probe {
        # foreach, not a pipeline: Invoke-L2Query hands back its rows as one object, so piping them
        # gives Where-Object the whole array as a single $_ and joins every RequestJson into one string
        # (-009 died on exactly that, the moment a second SublotSubmitted landed).
        $count = 0
        foreach ($row in (Get-InboxRows 'SublotSubmitted')) {
            if (([string]$row.RequestJson | ConvertFrom-Json).payload.sublot -eq $nextSublot) { $count++ }
        }
        $count
    } `
    -Until { param($v) $v -ge 1 }
$assertions.Add(
    'L2-RC-16', '车还回来了：下一条需求被受理并派车，车在取货点收下扫码并交给服务端',
    ($nextSubmitted -ge 1), "SublotSubmitted($nextSublot) 1 条", "$nextSubmitted 条")

$journal.Note(
    'Scenario finished: the five-step compensation handshake completed end to end, the demand ' +
    'was settled by CANCELLED_BY_LOAD_COMPENSATION, and the vehicle took the next demand.')
