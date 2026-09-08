#Requires -Version 7

<#
真车载端的装载失败后，车上发起不了任何恢复。

**这条场景 2026-09-04 改过一次向量，原来是错的。**它原名 resume-after-repair，制造「装载跑完、仓位
全空」然后去等 `RESUME_AFTER_REPAIR` 的入口。那两件事对不上：

- `RESUME_AFTER_REPAIR` 恢复的是**停在物理断点、车辆还握着那个断点**的操作，所以服务端要一个已证实的
  checkpoint（`PREPARED` / `ACTIVE_UNLOCK_SET` / `SAFE_FINISH_REACHED`）。车辆一旦记录了结果，断点就
  没了：车载端把 attempt 与 `OperationContext` 一并清空
  （`WireToGateSlotOperationExecutor.cs:130`），并拒绝 checkpoint 为 `ResultRecorded` 的恢复命令
  （同文件 `:641`）。**两端独立地做了同一判断**，协议向量只规定消息顺序、没排除这条路。
- 本场景制造的状态——装载跑完、门关着锁上了、仓位仍是空的——对应的是
  `COMPENSATE_LOAD_ALL_EMPTY`。它的服务端前置只要求「操作是 Load 且 `RecoveryRequired`」，**实测在
  这个状态下正常授权**（L1：`RecoveryStateMachineG2Tests.AfterARefusedResultResumeIsRefusedButCompensationIsAuthorized`）。

要证 `CV-EXCEPTION-RESUME`，需要的是「跑到一半没出结果」——第 4 节恢复表里的「装载中途车载端重启」。
那条要给装置加重启组件的能力，还没有。

**所以这条场景现在证的是那个真实的缺口：授权是齐的，入口是缺的。**服务端知道要恢复、也授权得了适配
的动作，而车上的操作员打不开那扇门——车载端的恢复入口按「会话进入 `RecoveryRequired`」显示，服务端
在这个状态下却把会话判为 `Ready`（`DecideReadinessAsync` 不看
`StationOperationStatus.RecoveryRequired`）。见
`docs/defects/20260904-recovery-required-never-reaches-session-state.md`。

**装载是怎么失败的，这里是刻意选的。**等到车载端自己报 `WAITING_OPERATOR` 之后再关门，但**不放
货**——门关了、锁上了、开锁输出复位了，唯独货物事实与期望不符。这是现场真会发生的一种：操作员把
门带上了，篮子没放进去。比起「脚本比人快、关门早于锁反馈稳定」那种竞态（见
`real-onboard-normal-load` 里 `Invoke-SlotOperation` 的注释），它是确定的。

**代价是这一步要花满 120 秒。**首跑实测：门在 19:59:41 关上，`OperationResult` 20:01:41 才发出，
正好是车载端的 `workflow.operationTimeoutMs`。车载端不把「门关了但货不对」当即判失败，它等满自己的
操作员超时，然后报一份 `overallOutcome = UNKNOWN` 的结果——不是 `FAILED`。服务端一样判
`RecoveryRequired`，因为判据是「没有安全完成」而不是「报了失败」。

不去 stage 副本里调短那个超时：它是安全相关的时序，调短之后这条场景证的就是一份没人真的在跑的
配置了。两分钟买一个真实的失败，值。

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

function Get-InboxCount([string]$messageType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT COUNT(*) AS Total FROM ProtocolInbox WHERE MessageType = '$messageType'"
    return [int]$rows[0].Total
}

function Get-OutboxCount([string]$messageType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT COUNT(*) AS Total FROM ProtocolOutbox WHERE MessageType = '$messageType'"
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

# --- 3. 装载失败：门关上了，货没放进去 --------------------------------------------------------------

$attemptId = Wait-L2Condition -Description 'the server issued the load command' `
    -Journal $journal -Criterion 'load-attempt' -TimeoutSeconds 180 `
    -Probe { Get-LoadAttemptId } -Until { param($v) $v }

$loadSlot = Wait-WaitingOperator $attemptId 'load'
$journal.Note("Operator closes slot $loadSlot without putting the basket in; the load will fail.")
$null = $simulator.Command('Post', "slots/$loadSlot/close-door", @{})
# 车载端要等满 workflow.operationTimeoutMs(120s) 才把这一份结果发出来，所以这个等待比别处宽。
$journal.Note('Waiting out the onboard operation timeout (about 120s) before the result arrives.')

$stage = Wait-L2Condition -Description 'the journey blocked on the incomplete load result' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 240 `
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

$emptySlot = Get-Slot $loadSlot
$assertions.Add(
    'L2-RR-04', '仓位物理事实与失败一致：门关着、锁上了，但仍然是空的',
    ("$($emptySlot.doorState)/$($emptySlot.cargoState)" -eq 'CLOSED/EMPTY'),
    'CLOSED/EMPTY', "$($emptySlot.doorState)/$($emptySlot.cargoState)")

# --- 4. 维护人员从 HMI 申请恢复 --------------------------------------------------------------------

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
    'L2-RR-05', '停摆之后车载端 HMI 上出现可用的恢复入口（任何一种恢复动作都行）',
    $recoveryAvailable, $true, $recoveryAvailable)

# 这条判据现在是红的，而且**红得是对的**：这就是那个缺口。服务端在同一状态下授权得了
# `COMPENSATE_LOAD_ALL_EMPTY`（有 L1 为证），但车上没人能来请求——入口按会话 `RecoveryRequired`
# 开门，而会话是 `Ready`。
#
# 场景到此为止。后面原本有八条判据描述 resume 的完整流程（下发授权、沿用原仓位、真 Modbus 再闭环、
# 替换结果、工作流对账、旅程继续），它们在 2026-09-04 一并删了：那是**另一条向量**的流程，本场景
# 制造的状态根本走不到，留着只会让人以为它们迟早会变绿。等入口通了、车载端的
# `COMPENSATE_LOAD_ALL_EMPTY` 也实现了，再按 compensate 的语义重新写它的下半段。
$journal.Note(
    'Scenario finished at the missing recovery entry: the server would authorize ' +
    'COMPENSATE_LOAD_ALL_EMPTY here, but the vehicle cannot open a recovery session.')
