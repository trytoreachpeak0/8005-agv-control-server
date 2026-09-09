#Requires -Version 7

<#
关卡卸货时操作员把门带上了，篮子没取走——车反复重开，直到取空为止，没有别的出路。

ADR-cross-0058 决策 1 的卸货侧，加 ADR-cross-0015 的不对称：**卸货采用 UnloadCompletionRequired，
不存在取消分支**。装货那边「确定失败」是一条真出口（服务端 `ApplyOperationResultAsync` 的
`determinateFailure` 只对 Load 成立），卸货没有——仓位里还有货就得继续闭环，直到取空、锁闭并
可靠上报。这条场景就是把那句话钉在真 IO 上。

**它与 `real-onboard-load-door-closed-empty` 不是同一条。**那条验的是装货侧，出口是最终装上；
这条验的是**没有取消分支**这件事本身：反复闭环之后需求既没有被取消、也没有进恢复，唯一的
终结方式是货真的被取走。两条的物理动作看着像，服务端那半边的判定规则不一样。

**为什么必须走真装置。**判据是光幕 DI 在「门已闭、货还在」这一刻的读数，以及车载端据此
重新输出开锁脉冲。合成对端按策略应答卸货，这一格在那边不存在。

流程：正常装一趟货 → 开到关卡 → 关门但不取货，两轮 → 中途校验没有任何终结发生 →
最后真的取走 → 照常 `Committed`、旅程 `Completed`。

断言只从服务端 SQLite 与模拟器 `/snapshot` 读。UI 只用来驱动。
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

function Get-Stage {
    $rows = Get-L2Journey -Connection $connection -DemandId $demandId
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
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

function Get-SlotPhysical([int]$slotNo) {
    $slot = Get-Slot $slotNo
    return "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
}

function Get-Operation([string]$operationType) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT SlotOperationAttemptId, Status FROM StationOperations " +
        "WHERE DemandId = '$demandId' AND OperationType = '$operationType'")
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

function Get-ResultCount([string]$attemptId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT COUNT(*) AS Total FROM OperationResults WHERE SlotOperationAttemptId = '$attemptId'")
    return [int]$rows[0].Total
}

function Get-Session {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

<#
等车载端把某个仓位开到「在等操作员」为止，返回它要开的那个仓号。

绝不能一看到 UNLOCKING 就动手：车载端要求锁反馈稳定 feedbackStableMs(300ms) 才认，
WAITING_OPERATOR 是它自己发的、说明它确实观测到了门开着的那一段。
#>
function Wait-WaitingOperator([string]$attemptId, [string]$criterionPrefix) {
    $unlocking = Wait-L2Condition -Description "the onboard started unlocking ($criterionPrefix)" `
        -Journal $journal -Criterion "$criterionPrefix-unlocking" -TimeoutSeconds 120 `
        -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'UNLOCKING' })[0] } `
        -Until { param($v) $v }
    $slots = @($unlocking.activeUnlockSlots)
    if ($slots.Count -ne 1) {
        throw ("This scenario drives one slot per operation; the command targets $($slots.Count) " +
            "($($slots -join ', ')).")
    }
    $slotNo = [int]$slots[0]
    $null = Wait-L2Condition -Description "the onboard is waiting for the operator on slot $slotNo" `
        -Journal $journal -Criterion "$criterionPrefix-waiting-operator" -TimeoutSeconds 120 `
        -Probe { Get-PhaseCount $attemptId 'WAITING_OPERATOR' } -Until { param($v) $v -ge 1 }
    return $slotNo
}

# --- 1. 需求出现，车去取货点，UIA 录条码 ------------------------------------------------------------

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

# --- 2. 正常装一趟货：这一段不是本条要证的，走通就行 ------------------------------------------------

$loadAttempt = Wait-L2Condition -Description 'the server issued the Load command' `
    -Journal $journal -Criterion 'load-attempt' -TimeoutSeconds 180 `
    -Probe { $row = Get-Operation 'Load'; if ($row) { [string]$row.SlotOperationAttemptId } else { $null } } `
    -Until { param($v) $v }
$loadSlot = Wait-WaitingOperator $loadAttempt 'load'
$journal.Note("Loading slot $loadSlot normally.")
$null = $simulator.Command('Put', "slots/$loadSlot/cargo", @{ state = 'OCCUPIED' })
$null = $simulator.Command('Post', "slots/$loadSlot/close-door", @{})

$loadPhysical = Wait-L2Condition -Description 'the loaded slot is closed, locked and occupied' `
    -Journal $journal -Criterion 'load-slot-physical' -TimeoutSeconds 60 `
    -Probe { Get-SlotPhysical $loadSlot } -Until { param($v) $v -eq 'CLOSED/OCCUPIED/1/0' }
$assertions.Add(
    'L2-UE-01', '装货一次到位，卸货那半边是从一个干净的起点开始的',
    ($loadPhysical -eq 'CLOSED/OCCUPIED/1/0'),
    'CLOSED/OCCUPIED/1/0', $loadPhysical)

$null = Wait-L2Condition -Description 'the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }

# --- 3. 开到关卡 -----------------------------------------------------------------------------------

$gateIntent = Get-UpperId -purpose 'TO_GATE'
$journal.Note('Vehicle departs for the gate.')
$null = $riot.Command('Put', "orders/$($gateIntent.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $gateIntent.OrderId
})
$journal.Note('Vehicle arrives at the gate and comes to rest.')
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $Context.GateStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($gateIntent.UpperId)", @{ orderState = 5 })

# --- 4. 卸货：关门但不取货，两轮 -------------------------------------------------------------------

$unloadAttempt = Wait-L2Condition -Description 'the server issued the Unload command' `
    -Journal $journal -Criterion 'unload-attempt' -TimeoutSeconds 180 `
    -Probe { $row = Get-Operation 'Unload'; if ($row) { [string]$row.SlotOperationAttemptId } else { $null } } `
    -Until { param($v) $v }
$unloadSlot = Wait-WaitingOperator $unloadAttempt 'unload'
$assertions.Add(
    'L2-UE-02', '关卡卸的是装货时用的那个仓位',
    ($unloadSlot -eq $loadSlot),
    $loadSlot, $unloadSlot)

$reopenRounds = 2
for ($round = 1; $round -le $reopenRounds; $round++) {
    $base = ($round - 1) * 3 + 3
    $unlockingBefore = Get-PhaseCount $unloadAttempt 'UNLOCKING'
    $waitingBefore = Get-PhaseCount $unloadAttempt 'WAITING_OPERATOR'
    $journal.Note("Round ${round}: closing slot $unloadSlot without taking the cargo out.")
    $null = $simulator.Command('Post', "slots/$unloadSlot/close-door", @{})

    $closed = Wait-L2Condition -Description "slot $unloadSlot is closed and locked, still occupied (round $round)" `
        -Journal $journal -Criterion "reopen-$round-closed-occupied" -TimeoutSeconds 60 `
        -Probe { Get-SlotPhysical $unloadSlot } -Until { param($v) $v -eq 'CLOSED/OCCUPIED/1/0' }
    $assertions.Add(
        "L2-UE-$('{0:d2}' -f $base)",
        "第 $round 轮：门关了货还在，仓位读数是明确的相反态而不是 UNKNOWN",
        ($closed -eq 'CLOSED/OCCUPIED/1/0'),
        'CLOSED/OCCUPIED/1/0', $closed)

    $unlockingAfter = Wait-L2Condition -Description "the onboard re-pulsed the unlock for round $round" `
        -Journal $journal -Criterion "reopen-$round-unlocking" -TimeoutSeconds 60 `
        -Probe { Get-PhaseCount $unloadAttempt 'UNLOCKING' } -Until { param($v) $v -gt $unlockingBefore }
    $assertions.Add(
        "L2-UE-$('{0:d2}' -f ($base + 1))",
        "第 $round 轮：车载端自动重新开锁并再次提示，继续闭环而不是判失败",
        ($unlockingAfter -gt $unlockingBefore),
        "UNLOCKING > $unlockingBefore", $unlockingAfter)

    # 等的是车载端自己再发一条 WAITING_OPERATOR，不是门弹开这个物理事实——它在锁反馈稳定
    # 且开锁输出确认复位之后才发得出来。照门开着直接关下一轮，会在开锁输出还是 1 的时候
    # 把门关上，车载端从此观测不到稳定的「已开锁」，整轮卡死。装货那条场景的
    # `20260909-real-onboard-load-door-closed-empty-001` 就是这么红的，间隔 40 ms。
    $waitingAfter = Wait-L2Condition -Description "the onboard is waiting for the operator again (round $round)" `
        -Journal $journal -Criterion "reopen-$round-waiting-operator" -TimeoutSeconds 60 `
        -Probe { Get-PhaseCount $unloadAttempt 'WAITING_OPERATOR' } -Until { param($v) $v -gt $waitingBefore }
    $reopened = [string](Get-Slot $unloadSlot).doorState
    $assertions.Add(
        "L2-UE-$('{0:d2}' -f ($base + 2))",
        "第 $round 轮：重开脉冲真的走到了 IO——门又弹开、开锁输出复位、车载端重新等操作员",
        ($reopened -eq 'OPEN' -and $waitingAfter -gt $waitingBefore),
        "OPEN / WAITING_OPERATOR > $waitingBefore",
        "$reopened / $waitingAfter")
}

# 这是本条与装货侧的分界，也是 ADR-cross-0015 那句话唯一能被证伪的地方：卸货没有取消分支，
# 所以两轮之后不许出现任何一种终结——不 Committed、不 Failed、不 RecoveryRequired、需求不取消。
$operation = Get-Operation 'Unload'
$assertions.Add(
    'L2-UE-09', "重开 $reopenRounds 轮之后卸货操作仍在进行：既没有确定失败，也没有进恢复",
    ($null -ne $operation -and [string]$operation.Status -eq 'Prepared'),
    'Prepared', $(if ($operation) { [string]$operation.Status } else { '(no operation row)' }))

$resultCount = Get-ResultCount $unloadAttempt
$assertions.Add(
    'L2-UE-10', '一份卸货 OperationResult 都没发过——UnloadCompletionRequired 没有中途结算这回事',
    ($resultCount -eq 0),
    0, $resultCount)

$stage = Get-Stage
$assertions.Add(
    'L2-UE-11', '旅程原地等在卸货结果上，没有被任何取消分支带走',
    ($stage -eq 'AwaitingUnloadResult'),
    'AwaitingUnloadResult', $stage)

$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-UE-12', '需求没有被取消也没有被判 RecoveryRequired',
    ($demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'Accepted'),
    'Accepted', $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' }))

$session = Get-Session
$assertions.Add(
    'L2-UE-13', '会话全程停在 Ready：开着的仓门由本服务端自己下的命令解释',
    ($null -ne $session -and [string]$session.Readiness -eq 'Ready'),
    'Ready', $(if ($session) { "$($session.Readiness) / $($session.ReasonCode)" } else { '(no session row)' }))

$recoveryVisible = $onboard.RecoveryAvailable()
$assertions.Add(
    'L2-UE-14', 'HMI 上没有出现恢复入口',
    (-not $recoveryVisible),
    $false, $recoveryVisible)

# --- 5. 货真的被取走，闭环才结束 -------------------------------------------------------------------

$journal.Note("Operator finally takes the cargo out of slot $unloadSlot and closes the door.")
$null = $simulator.Command('Put', "slots/$unloadSlot/cargo", @{ state = 'EMPTY' })
$null = $simulator.Command('Post', "slots/$unloadSlot/close-door", @{})

$unloadPhysical = Wait-L2Condition -Description 'the unloaded slot is closed, locked and empty' `
    -Journal $journal -Criterion 'unload-slot-physical' -TimeoutSeconds 60 `
    -Probe { Get-SlotPhysical $unloadSlot } -Until { param($v) $v -eq 'CLOSED/EMPTY/1/0' }
$assertions.Add(
    'L2-UE-15', '唯一的终结方式是取空：取走之后仓位回到空、门关、锁上',
    ($unloadPhysical -eq 'CLOSED/EMPTY/1/0'),
    'CLOSED/EMPTY/1/0', $unloadPhysical)

$stage = Wait-L2Condition -Description 'the journey completed at the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'Completed' }
$assertions.Add('L2-UE-16', 'journey 走到 Completed', ($stage -eq 'Completed'), 'Completed', $stage)

$operation = Get-Operation 'Unload'
$assertions.Add(
    'L2-UE-17', '全程只有一个卸货 attempt，最终 Committed——重开不新建操作',
    ($null -ne $operation -and [string]$operation.Status -eq 'Committed' -and
        [string]$operation.SlotOperationAttemptId -eq $unloadAttempt),
    "Committed / $unloadAttempt",
    $(if ($operation) { "$([string]$operation.Status) / $([string]$operation.SlotOperationAttemptId)" }
      else { '(no operation row)' }))

$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-UE-18', '需求终态为 Succeeded',
    ($demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'Succeeded'),
    'Succeeded', $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' }))

$journal.Note('Scenario finished.')
