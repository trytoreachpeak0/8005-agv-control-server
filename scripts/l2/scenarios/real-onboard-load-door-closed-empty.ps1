#Requires -Version 7

<#
装货时操作员把仓门带上了，篮子没放进去——车反复重开，不判失败，不进恢复。

ADR-cross-0058 决策 1、2、5、6 的车载端一半，在真 Modbus 上验。这三种「操作员不作为」里，
本条是**仓门已闭**那一路：光幕稳定读到与预期相反的状态。

**为什么这条必须走真装置。**判据全在 IO 层：光幕 DI 的极性、锁反馈稳定 `feedbackStableMs`
之后才算数、开锁 DO 由硬件自动复位。合成对端按策略应答装卸，「关了门但没放料」在那边压根
表达不出来——它会绿，而绿的是另一件事。

**它钉的是一次行为反转。**同样的物理状态，在 2026-09-08 那趟真车上（demand `Q26091238-17`）
的结局是：车载端等满 120 秒 `OperationTimeout`，报一份 `overallOutcome = UNKNOWN`，服务端判
`RecoveryRequired`，旅程 Blocked，而 `wireToGate.recoveryResumeEnabled` 出厂为 `false`，恢复
入口根本不出现——唯一出路是从控制端跑 `12-reset-journey-state.ps1`。那正是 ADR-cross-0040 禁止
的事：**软件不得把「人没放料」当成传感器故障**。现在同样的物理状态应当只是「再提示一次」。

三幕，各钉一条：

1. **关门不放料 → 自动重开，不设上限。**两轮，每轮都要看到新的 `UNLOCKING` 与门重新弹开。
   两轮而不是一轮：一轮只证明「重开过一次」，`OnboardController` 那条老路径带
   `MaxReopenAttempts`（默认 2）也能做到；ADR 要的是不设上限。
2. **门开着不动 → 提示节拍到期只再提示，不重复脉冲。**决策 3 把 `OperationTimeout` 降成提示
   节拍，`WaitForTargetOrOppositeAsync` 的两个等待都要求锁已闭，所以门开着时两个都不成立，
   走的是超时那条出路。**这一幕要花满一个 `OperationTimeout`（120 秒）**，而且判据是否定的：
   `UNLOCKING` 的条数不许变。对一把已经开着的锁再打一次脉冲没有意义。
3. **最后放料关门 → 正常提交。**证明前两幕没有把这次操作弄坏：它一直是同一个 attempt，
   一份结果都没发过，最终照常 `Committed`。

全程还有一条贯穿的否定判据：`StationOperations` 不许出现 `RecoveryRequired`，`OperationResults`
一行都不许有，会话 readiness 不许离开 `Ready`，HMI 上的恢复入口不许出现。

断言只从服务端 SQLite 与模拟器 `/snapshot` 读。UI 只用来驱动，以及读「恢复入口在不在」——
那是能不能操作的前提，不是业务事实。
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

function Get-LoadOperation {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT SlotOperationAttemptId, Status FROM StationOperations " +
        "WHERE DemandId = '$demandId' AND OperationType = 'Load'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 车载端每走一个相位就发一条 OperationProgress，服务端原样存进 ProtocolInbox。这是唯一一处
# 能看到「车载端现在到哪一步、这是第几轮」的地方，而且是服务端自己收到的事实，不是从界面上
# 读来的。`promptRound` 不上线——协议一行没动，它只掺进车载端本地的去重键——所以「第几轮」
# 在这里数的是消息条数。
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
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT RequestJson FROM ProtocolInbox WHERE MessageType = 'SublotSubmitted'"
        if ($rows.Count -eq 0) { return $null }
        return [string]$rows[0].RequestJson
    } `
    -Until { param($v) $v -like "*$sublot*" }

# --- 3. 车载端开锁，等它自己说在等操作员 ------------------------------------------------------------

$attemptId = Wait-L2Condition -Description 'the server issued the Load command' `
    -Journal $journal -Criterion 'load-attempt' -TimeoutSeconds 180 `
    -Probe { $row = Get-LoadOperation; if ($row) { [string]$row.SlotOperationAttemptId } else { $null } } `
    -Until { param($v) $v }

$unlocking = Wait-L2Condition -Description 'the onboard started unlocking for the load' `
    -Journal $journal -Criterion 'load-unlocking' -TimeoutSeconds 120 `
    -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'UNLOCKING' })[0] } `
    -Until { param($v) $v }
$slots = @($unlocking.activeUnlockSlots)
if ($slots.Count -ne 1) {
    throw ("This scenario drives one slot; the load command targets $($slots.Count) " +
        "($($slots -join ', ')).")
}
$slotNo = [int]$slots[0]

# 绝不能一看到 UNLOCKING 就动手：车载端要求锁反馈稳定 feedbackStableMs(300ms) 才认，
# WAITING_OPERATOR 是它自己发的、说明它确实观测到了门开着的那一段。
$null = Wait-L2Condition -Description "the onboard is waiting for the operator on slot $slotNo" `
    -Journal $journal -Criterion 'load-waiting-operator' -TimeoutSeconds 120 `
    -Probe { Get-PhaseCount $attemptId 'WAITING_OPERATOR' } -Until { param($v) $v -ge 1 }

$physical = Get-SlotPhysical $slotNo
$assertions.Add(
    'L2-DC-01', '车载端说在等操作员时，它要开的那个仓门确实开着、货位是空的',
    ($physical -eq 'OPEN/EMPTY/0/0'),
    'OPEN/EMPTY/0/0', $physical)

# --- 4. 第一幕：关门不放料，两轮 -------------------------------------------------------------------

# 每一轮都完整校一遍，而不是最后数一次总数：中间某一轮没重开、门没弹开，只看总数是看不出来的。
$reopenRounds = 2
for ($round = 1; $round -le $reopenRounds; $round++) {
    $base = ($round - 1) * 3 + 2
    $unlockingBefore = Get-PhaseCount $attemptId 'UNLOCKING'
    $waitingBefore = Get-PhaseCount $attemptId 'WAITING_OPERATOR'
    $journal.Note("Round ${round}: closing slot $slotNo without putting any cargo in it.")
    $null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})

    # 关门到位、锁反馈回到 1、货位仍是空——这正是「与预期相反的明确状态」，
    # 也是决策 2 与「传感器不可信」的分界线：这里没有任何东西是未知的。
    $closed = Wait-L2Condition -Description "slot $slotNo is closed and locked with no cargo (round $round)" `
        -Journal $journal -Criterion "reopen-$round-closed-empty" -TimeoutSeconds 60 `
        -Probe { Get-SlotPhysical $slotNo } -Until { param($v) $v -eq 'CLOSED/EMPTY/1/0' }
    $assertions.Add(
        "L2-DC-$('{0:d2}' -f $base)",
        "第 $round 轮：操作员关门但没放料，仓位读数是明确的相反态而不是 UNKNOWN",
        ($closed -eq 'CLOSED/EMPTY/1/0'),
        'CLOSED/EMPTY/1/0', $closed)

    # 决策 1 的核心：读到相反态就自动再打一次开锁脉冲。不判失败、不进恢复、不设上限。
    $unlockingAfter = Wait-L2Condition -Description "the onboard re-pulsed the unlock for round $round" `
        -Journal $journal -Criterion "reopen-$round-unlocking" -TimeoutSeconds 60 `
        -Probe { Get-PhaseCount $attemptId 'UNLOCKING' } -Until { param($v) $v -gt $unlockingBefore }
    $assertions.Add(
        "L2-DC-$('{0:d2}' -f ($base + 1))",
        "第 $round 轮：车载端自动重新开锁并再次提示，没有把「人没放料」判成失败",
        ($unlockingAfter -gt $unlockingBefore),
        "UNLOCKING > $unlockingBefore", $unlockingAfter)

    # 门弹开只是脉冲到了 IO；下一轮能不能动手，要等车载端**自己**再发一条 WAITING_OPERATOR。
    # 它在锁反馈稳定 feedbackStableMs 并且开锁输出确认复位之后才发得出来，所以它同时是
    # 「重开这一轮走完了」和「现在关门才算数」两件事的判据。
    #
    # 第一版就是照门开着直接关的，`20260909-real-onboard-load-door-closed-empty-001` 红在这里：
    # 第 1 轮门在 16.4155 弹开，脚本 16.4557 就把它关上了——**隔了 40 ms**，开锁输出那时还是 1
    # （时间线上留着 `CLOSED/EMPTY/1/1`）。车载端于是从来没观测到一个稳定的「已开锁」状态，
    # 卡在 `WaitForLockerAsync` 里，第 3 次 UNLOCKING 永远不来。这是 README 第 7 条那个坑的
    # 另一种长相：脚本比人快。
    $waitingAfter = Wait-L2Condition -Description "the onboard is waiting for the operator again (round $round)" `
        -Journal $journal -Criterion "reopen-$round-waiting-operator" -TimeoutSeconds 60 `
        -Probe { Get-PhaseCount $attemptId 'WAITING_OPERATOR' } -Until { param($v) $v -gt $waitingBefore }
    $reopened = [string](Get-Slot $slotNo).doorState
    $assertions.Add(
        "L2-DC-$('{0:d2}' -f ($base + 2))",
        "第 $round 轮：重开脉冲真的走到了 IO——门又弹开、开锁输出复位、车载端重新等操作员",
        ($reopened -eq 'OPEN' -and $waitingAfter -gt $waitingBefore),
        "OPEN / WAITING_OPERATOR > $waitingBefore",
        "$reopened / $waitingAfter")
}

# 两轮之后仍然什么都没有结算，这是与旧行为的分界：同样的物理状态，旧路径此时已经
# RecoveryRequired 并停摆了。
$operation = Get-LoadOperation
$assertions.Add(
    'L2-DC-08', "重开 $reopenRounds 轮之后装载操作仍在进行，没有进人工恢复",
    ($null -ne $operation -and [string]$operation.Status -eq 'Prepared'),
    'Prepared', $(if ($operation) { [string]$operation.Status } else { '(no operation row)' }))

$resultCount = Get-ResultCount $attemptId
$assertions.Add(
    'L2-DC-09', '一份 OperationResult 都没发过——操作还没结束，无所谓成败',
    ($resultCount -eq 0),
    0, $resultCount)

$session = Get-Session
$assertions.Add(
    'L2-DC-10', '会话全程停在 Ready：开着的仓门由本服务端自己下的命令解释，不是会话故障',
    ($null -ne $session -and [string]$session.Readiness -eq 'Ready'),
    'Ready', $(if ($session) { "$($session.Readiness) / $($session.ReasonCode)" } else { '(no session row)' }))

$recoveryVisible = $onboard.RecoveryAvailable()
$assertions.Add(
    'L2-DC-11', 'HMI 上没有出现恢复入口——操作员迟疑不需要管理员凭据',
    (-not $recoveryVisible),
    $false, $recoveryVisible)

# --- 5. 第二幕：门开着不动，等满一个 OperationTimeout ----------------------------------------------

# 这一幕是否定判据，而且要花满 120 秒。`WaitForTargetOrOppositeAsync` 的两个等待都要求锁已闭，
# 门开着时两个都不成立，走的是超时那条出路——决策 3 把它降成了提示节拍：再提示一次，
# **不重复脉冲**。对一把已经开着的锁再打一次开锁 DO 没有意义。
$unlockingBeforeIdle = Get-PhaseCount $attemptId 'UNLOCKING'
$waitingBeforeIdle = Get-PhaseCount $attemptId 'WAITING_OPERATOR'
$journal.Note(
    "Leaving slot $slotNo open and untouched for a full operationTimeoutMs (120 s expected); " +
    "UNLOCKING=$unlockingBeforeIdle WAITING_OPERATOR=$waitingBeforeIdle before the idle window.")

$waitingAfterIdle = Wait-L2Condition -Description 'the prompt cadence fired again on an open door' `
    -Journal $journal -Criterion 'idle-prompt-again' -TimeoutSeconds 240 `
    -Probe { Get-PhaseCount $attemptId 'WAITING_OPERATOR' } -Until { param($v) $v -gt $waitingBeforeIdle }
$assertions.Add(
    'L2-DC-12', '门一直开着时提示节拍到期又提示了一次，操作没有因此结束',
    ($waitingAfterIdle -gt $waitingBeforeIdle),
    "WAITING_OPERATOR > $waitingBeforeIdle", $waitingAfterIdle)

$unlockingAfterIdle = Get-PhaseCount $attemptId 'UNLOCKING'
$assertions.Add(
    'L2-DC-13', '那一次提示没有跟着一次重复脉冲：门本来就开着，再开一次没有意义',
    ($unlockingAfterIdle -eq $unlockingBeforeIdle),
    $unlockingBeforeIdle, $unlockingAfterIdle)

$operation = Get-LoadOperation
$resultCount = Get-ResultCount $attemptId
$assertions.Add(
    'L2-DC-14', '等满一个 OperationTimeout 之后仍然没有结果、没有恢复——它不再是判死依据',
    ($null -ne $operation -and [string]$operation.Status -eq 'Prepared' -and $resultCount -eq 0),
    'Prepared / 0 result',
    "$(if ($operation) { [string]$operation.Status } else { '(none)' }) / $resultCount result")

# --- 6. 第三幕：真的放料关门，照常提交 --------------------------------------------------------------

$journal.Note("Operator finally puts the cargo in slot $slotNo and closes the door.")
$null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = 'OCCUPIED' })
$null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})

$loadPhysical = Wait-L2Condition -Description 'the loaded slot is closed, locked and occupied' `
    -Journal $journal -Criterion 'load-slot-physical' -TimeoutSeconds 60 `
    -Probe { Get-SlotPhysical $slotNo } -Until { param($v) $v -eq 'CLOSED/OCCUPIED/1/0' }
$assertions.Add(
    'L2-DC-15', '最后一轮走通真 Modbus 闭环：放料、关门、锁反馈回到 1、开锁输出复位',
    ($loadPhysical -eq 'CLOSED/OCCUPIED/1/0'),
    'CLOSED/OCCUPIED/1/0', $loadPhysical)

$stage = Wait-L2Condition -Description 'the load committed and the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$assertions.Add(
    'L2-DC-16', '前两幕没有把这次装载弄坏，它照常提交并进入去关卡那一段',
    ($stage -eq 'AwaitingGateArrival'),
    'AwaitingGateArrival', $stage)

$operation = Get-LoadOperation
$assertions.Add(
    'L2-DC-17', '全程只有一个 attempt，最终 Committed——重开不新建操作',
    ($null -ne $operation -and [string]$operation.Status -eq 'Committed' -and
        [string]$operation.SlotOperationAttemptId -eq $attemptId),
    "Committed / $attemptId",
    $(if ($operation) { "$([string]$operation.Status) / $([string]$operation.SlotOperationAttemptId)" }
      else { '(no operation row)' }))

$resultCount = Get-ResultCount $attemptId
$assertions.Add(
    'L2-DC-18', '整趟只发了一份 OperationResult，就是最后成功的那一份',
    ($resultCount -eq 1),
    1, $resultCount)

$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-DC-19', '需求从头到尾没有被判 RecoveryRequired',
    ($demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'Accepted'),
    'Accepted', $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' }))

$journal.Note('Scenario finished.')
