#Requires -Version 7

<#
真车载端报出一份 UNKNOWN 之后，车上的操作员打得开恢复入口。

**这条场景 2026-09-10 改了向量，同时改了名，因为它证的事情反过来了。**它原名
`real-onboard-recovery-entry-missing`，钉的是一个缺口——服务端知道要恢复、也授权得了适配的动作，
而车上的操作员打不开那扇门，所以最后一条判据长期是红的、而且红得对。

**那个缺口 2026-09-04 就补上了，没人看见。**服务端 `8c6d400`（fix(session): 会话不再在有操作待恢复
时报 READY）给 `GetRecoveryReason` 加了 `OPERATION_RECOVERY_REQUIRED`，会话从此不再在有操作待恢复
时报 `Ready`；`docs/defects/20260904-recovery-required-never-reaches-session-state.md` 里已经写着
「入口开了」，并留了一条 `onboard-recovery-entry: True` 的时间线。而这条 L2 在那之后**一次都没有
在能跑通的状态下跑过**：决策 1 落地后它的旧向量（关门不放货）不再产出任何结果，整条场景卡在
`AwaitingLoadResult`，见 `evidence/l2/20260909-recovery-entry-missing-after-target-state-loop-001`。
换完向量第一次跑，最后一条判据就是绿的——**这条场景现在是那个修复的回归守卫**，不再是缺口的记录。

## 更早的一次改向量（2026-09-04）：从 RESUME_AFTER_REPAIR 改到 COMPENSATE_LOAD_ALL_EMPTY

原名 resume-after-repair，制造「装载跑完、仓位全空」然后去等 `RESUME_AFTER_REPAIR` 的入口。
那两件事对不上：`RESUME_AFTER_REPAIR` 恢复的是**停在物理断点、车辆还握着那个断点**的操作，
车辆一旦记录了结果，断点就没了——车载端把 attempt 与 `OperationContext` 一并清空，并拒绝
checkpoint 为 `ResultRecorded` 的恢复命令，**两端独立地做了同一判断**。而本场景制造的状态对应的是
`COMPENSATE_LOAD_ALL_EMPTY`，它的服务端前置只要求「操作是 Load 且 `RecoveryRequired`」，实测在
这个状态下正常授权（L1：`RecoveryStateMachineG2Tests.AfterARefusedResultResumeIsRefusedButCompensationIsAuthorized`）。

## 这一次改向量（2026-09-10）：从「关门不放货」改到「锁反馈失效」

**ADR-cross-0058 决策 1 把「关门不放货」这条路整个拿掉了。**那个物理状态现在是目标态闭环里的
*相反态*：车载端重新打一次开锁脉冲、再提示一次，**不再产出任何结果**。实测复跑
`evidence/l2/20260909-recovery-entry-missing-after-target-state-loop-001`：旅程 240 秒里一直停在
`AwaitingLoadResult`，一条判据都没走到。

**决策 2 留给人工恢复的只剩三件事：仓位状态未知、锁闭反馈无效、开锁输出无法确认复位。**本场景改用
其中第二件：`lock-feedback-override FIXED_1` 把这一仓的锁反馈 DI 钉死在「已锁」。模拟器的
`RebuildLockFeedbackNoLock` / `ScheduleLockFeedbackNoLock` 都让覆盖压过门的物理状态，所以**门照常
弹开，锁反馈从此不再变**——这正是 L1 里 `fixture.Io.JamLock(0)` 那一路
（`WireToGateSlotOperationExecutorTests.SlotsNeverStartedKeepRealReadingsAndCarryNoReasonCodes`）。

车载端于是走进 `WireToGateSlotOperationExecutor` 里那个
`catch (IOException or TimeoutException or InvalidDataException)` 分支——**这是
`overallOutcome = UNKNOWN` 在决策 1 之后唯一的产地**——报一份 `UNKNOWN`，服务端照旧判
`RecoveryRequired`（判据是「没有安全完成」，不是「报了失败」；`FAILED` 那条是决策 5 的确定失败，
走的是另一条路，见 `real-onboard-station-timeout-door-open`）。

**注入的时机是等到车载端自己发出 `WAITING_OPERATOR` 之后。**那一刻门已经弹开、锁反馈已经稳定读到
「未锁」，车辆正停在 `WaitForTargetOrOppositeAsync` 里等操作员。此时钉死锁反馈，车辆立刻读到一个
稳定的**相反态**（已锁 + 没货），按决策 1 重打一次开锁脉冲——而这一次它再也等不到锁反馈从「已锁」
变成「未锁」，`UnlockFeedbackTimeout`(3 s) 到期抛 `TimeoutException`。

三个好处，都不是顺带的：

- **它知道该注入哪一仓。**装载仓号由服务端选，只有 `UNLOCKING` 那条 progress 才说得出来。
- **它先让决策 1 真的跑一轮**，再让传感器在等待中失效——那是现场真会发生的顺序，也是 L1
  `IoGoingUnknownDuringOperatorWaitStillEntersRecovery` 的形状。
- **它便宜。**改向量之前这一步要花满车载端的 `workflow.operationTimeoutMs`(120 s)；现在是一轮
  重开加 3 秒开锁反馈超时。

## 报回来的那份结果里，哪一句是谎

覆盖只钉锁反馈这一位，门是真开着的。所以车辆报的 `lockState` 是 `LOCKED`，而模拟器上的
`doorState` 是 `OPEN`——**两者的分歧就是「未知」的实体**。这条场景不是靠让车辆说假话来制造
`UNKNOWN` 的，是靠让它的传感器说假话；车辆自己说不清，才是要人去现场看的理由。

断言仍然只从服务端 SQLite 与模拟器 `/snapshot` 读。UI 只用来驱动——从控件读到的只有「现在允不允许
录入」和「恢复入口在不在」，那是能不能操作的前提，不是业务事实。
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

function Get-LoadAttemptId {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT SlotOperationAttemptId FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].SlotOperationAttemptId
}

function Get-LoadStatus {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Status FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Status
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

# 车载端实发的那一份结果报文。服务端把仓位证据折进 OperationResults.EvidenceJson 时只留下
# 三个物理字段，reasonCodes 不在其中——而「为什么是 UNKNOWN」正是这条场景要读的东西，
# 所以判据读收件箱里的原报文。它同样是服务端自己的库，不是走捷径。
function Get-ResultPayload([string]$attemptId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT RequestJson FROM ProtocolInbox WHERE MessageType = 'OperationResult' ORDER BY ReceivedAt"
    $matched = foreach ($row in $rows) {
        $payload = ([string]$row.RequestJson | ConvertFrom-Json).payload
        if ($payload.slotOperationAttemptId -eq $attemptId) { $payload }
    }
    return @($matched)[-1]
}

function Get-InboxCount([string]$messageType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT COUNT(*) AS Total FROM ProtocolInbox WHERE MessageType = '$messageType'"
    return [int]$rows[0].Total
}

# 同一 attempt 的所有结果，按到达顺序。恢复之后应当有两行，第一行的 SupersededByResultId 指向
# 第二行——这正是服务端「一次替换」在库里的样子。
function Get-Results([string]$attemptId) {
    return @(Invoke-L2Query -Connection $connection -Sql (
        "SELECT ResultId, OverallOutcome, SupersededByResultId FROM OperationResults " +
        "WHERE SlotOperationAttemptId = '$attemptId' ORDER BY ReceivedAt"))
}

# 等车载端把门开到「在等操作员」为止。绝不能一看到 UNLOCKING 就动手：车载端要求锁反馈稳定
# feedbackStableMs 才认，WAITING_OPERATOR 是它自己发的、说明它确实观测到了门开着的那一段。
function Wait-WaitingOperator([string]$attemptId, [string]$criterionPrefix) {
    $unlocking = Wait-L2Condition -Description 'the onboard started unlocking for the load' `
        -Journal $journal -Criterion "$criterionPrefix-unlocking" -TimeoutSeconds 120 `
        -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'UNLOCKING' })[-1] } `
        -Until { param($v) $v }
    $slots = @($unlocking.activeUnlockSlots)
    if ($slots.Count -ne 1) {
        throw ("This scenario drives one slot; the load command targets $($slots.Count) " +
            "($($slots -join ', ')).")
    }
    $slotNo = [int]$slots[0]

    $before = $script:WaitingOperatorSeen
    $null = Wait-L2Condition -Description "the onboard is waiting for the operator on slot $slotNo" `
        -Journal $journal -Criterion "$criterionPrefix-waiting-operator" -TimeoutSeconds 120 `
        -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'WAITING_OPERATOR' }).Count } `
        -Until { param($v) $v -gt $before }
    $script:WaitingOperatorSeen = @(Get-Progress $attemptId |
        Where-Object { $_.phase -eq 'WAITING_OPERATOR' }).Count
    return $slotNo
}

$script:WaitingOperatorSeen = 0

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

# --- 3. 装载失败：门开着的时候锁反馈失效 ------------------------------------------------------------

$attemptId = Wait-L2Condition -Description 'the server issued the load command' `
    -Journal $journal -Criterion 'load-attempt' -TimeoutSeconds 180 `
    -Probe { Get-LoadAttemptId } -Until { param($v) $v }

$loadSlot = Wait-WaitingOperator $attemptId 'load'

# 车辆正停在 WaitForTargetOrOppositeAsync 里等操作员，门开着、锁反馈稳定读到「未锁」。
# 现在钉死锁反馈 DI：车辆立刻读到一个稳定的相反态（已锁 + 没货），按决策 1 重打一次开锁
# 脉冲，而这一次它再也等不到「未锁」——UnlockFeedbackTimeout 到期就是那份 UNKNOWN。
$journal.Note("Jamming slot $loadSlot lock feedback at LOCKED while the door stands open.")
$null = $simulator.Command('Put', "slots/$loadSlot/lock-feedback-override", @{ mode = 'FIXED_1' })

$unlockingBefore = @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'UNLOCKING' }).Count
$null = Wait-L2Condition -Description 'the onboard saw the opposite state and re-pulsed the unlock' `
    -Journal $journal -Criterion 'load-unlocking-after-jam' -TimeoutSeconds 120 `
    -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'UNLOCKING' }).Count } `
    -Until { param($v) $v -gt $unlockingBefore }

$stage = Wait-L2Condition -Description 'the journey blocked on the incomplete load result' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'Blocked' }
$blockReason = [string](Get-Runtime).BlockReasonCode
$assertions.Add(
    'L2-RR-01', '真车载端报回不完美的装载结果，旅程停摆在 LOAD_RESULT_REQUIRES_RECOVERY',
    ($stage -eq 'Blocked' -and $blockReason -eq 'LOAD_RESULT_REQUIRES_RECOVERY'),
    'Blocked / LOAD_RESULT_REQUIRES_RECOVERY', "$stage / $blockReason")

$loadStatus = Get-LoadStatus
$assertions.Add(
    'L2-RR-02', '装载操作判 RecoveryRequired',
    ($loadStatus -eq 'RecoveryRequired'), 'RecoveryRequired', $loadStatus)

$firstResults = Get-Results $attemptId
$assertions.Add(
    'L2-RR-03', '失败结果已落库，且是这个 attempt 唯一一份存活结果',
    ($firstResults.Count -eq 1 -and [string]$firstResults[0].OverallOutcome -ne 'COMPLETED' -and
        [string]::IsNullOrEmpty([string]$firstResults[0].SupersededByResultId)),
    '1 份，未 COMPLETED，未被替换',
    "$($firstResults.Count) 份，$(if ($firstResults.Count -ge 1) { [string]$firstResults[0].OverallOutcome } else { '(none)' })")

# 这一份必须是**真的** UNKNOWN：决策 1 之后，UNKNOWN 只出自执行器那个 catch 分支，而等不到锁反馈
# 的 TimeoutException 映射出来的理由码正是 ACTION_NOT_ALLOWED_IN_STATE。理由码不许为空，否则这条
# 场景就退回成「车报了个不完美结果」，说不清是哪一类。
$resultPayload = Get-ResultPayload $attemptId
$failedSlot = @($resultPayload.slotResults | Where-Object { [int]$_.slotNo -eq $loadSlot })[0]
$failedOutcome = [string]$failedSlot.outcome
$failedReasons = @($failedSlot.reasonCodes)
$assertions.Add(
    'L2-RR-04', '这一份 UNKNOWN 出自锁反馈失效，不是「人没放料」的另一种说法',
    ([string]$resultPayload.overallOutcome -eq 'UNKNOWN' -and $failedOutcome -eq 'UNKNOWN' -and
        $failedReasons -contains 'ACTION_NOT_ALLOWED_IN_STATE'),
    'UNKNOWN / UNKNOWN / ACTION_NOT_ALLOWED_IN_STATE',
    "$([string]$resultPayload.overallOutcome) / $failedOutcome / $($failedReasons -join ',')")

# 覆盖只钉锁反馈这一位，门是真开着的。车辆报 LOCKED 而模拟器报 OPEN——两者的分歧就是「未知」的
# 实体。这条场景不是靠让车辆说假话来制造 UNKNOWN，是靠让它的传感器说假话。
$jammedSlot = Get-Slot $loadSlot
$assertions.Add(
    'L2-RR-05', '车辆报的锁态与现场相反：它说已锁，门其实开着——这就是要人去看的理由',
    ([string]$failedSlot.lockState -eq 'LOCKED' -and [string]$jammedSlot.doorState -eq 'OPEN' -and
        [string]$jammedSlot.cargoState -eq 'EMPTY'),
    '车报 LOCKED / 现场 OPEN / EMPTY',
    "车报 $([string]$failedSlot.lockState) / 现场 $($jammedSlot.doorState) / $($jammedSlot.cargoState)")

# --- 4. 维护人员从 HMI 能不能把恢复发起来 -----------------------------------------------------------

# 这是这条场景真正要证的东西：**操作员在车上能不能把恢复发起来**。合成对端证不了它，因为它根本不
# 走界面。所以这里读的是恢复按钮在不在、可不可点——那是能不能操作的前提，不是业务事实。
#
# 不用 Wait-L2Condition：超时抛异常只会在证据里留下一句 failureReason，而这一步值得有一条具名判据。
$recoveryDeadline = [DateTimeOffset]::UtcNow.AddSeconds(120)
$recoveryAvailable = $false
while ($true) {
    $recoveryAvailable = $onboard.RecoveryAvailable()
    $journal.Observe('onboard-recovery-entry', $recoveryAvailable, $null)
    if ($recoveryAvailable) { break }
    if ([DateTimeOffset]::UtcNow -ge $recoveryDeadline) { break }
    Start-Sleep -Milliseconds 500
}
$assertions.Add(
    'L2-RR-06', '停摆之后车载端 HMI 上出现可用的恢复入口（任何一种恢复动作都行）',
    $recoveryAvailable, $true, $recoveryAvailable)

# 这条判据**长期是红的，而且红得对**，现在绿了。分水岭是服务端 `8c6d400`：入口按会话
# `RecoveryRequired` 开门，而在那之前 `DecideReadinessAsync` 不看 `StationOperationStatus`，
# 会话停在 `Ready`。绿证据里 `snapshots/db-SessionRecoveries.json` 的
# `Readiness=RecoveryRequired` / `ReasonCode=OPERATION_RECOVERY_REQUIRED` 就是它。
#
# 场景到此为止，**它有意不去点那个按钮**。按下去之后是五步恢复握手加一次真 Modbus 再闭环，
# 那是另一条判据链，值得单独一条场景；混进来只会让这一条同时说两件事，而其中一件失败时
# 说不清是哪一件。这条只回答一个问题：一份真的 UNKNOWN 报上去之后，人在车上有没有出路。
$journal.Note(
    'Scenario finished at the recovery entry: the server authorizes COMPENSATE_LOAD_ALL_EMPTY ' +
    'here, and the entry is now reachable on the vehicle. Whether the handshake then completes ' +
    'is a different scenario.')
