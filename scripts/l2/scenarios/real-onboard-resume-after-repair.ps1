#Requires -Version 7

<#
真车载端的恢复并继续装载：装载先失败一次，维护人员现场处置后从 HMI 申请恢复，同一操作提交一份
替换 OperationResult，旅程接着往下走。

这是 `load-result-requires-recovery` 一直缺的那半条。那条到 Blocked 为止，因为合成对端不会自己
发起恢复；这条用出厂的那个 WPF，让它自己走完 `CV-EXCEPTION-RESUME` 的六条消息：

    ExceptionRecoverySessionRequested → ExceptionRecoverySessionOpened
    → RecoveryActionSubmitted → RecoveryActionAccepted
    → SlotOperationResumeCommand → OperationResult

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

恢复之后**门会再开一次**：车载端只把已经处于期望终态的仓位记为完成，这个仓位还是空的，所以它
沿用原始命令继续执行。那一次才把货放进去。

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
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Stage, BlockReasonCode FROM JourneyRuntimes WHERE DemandId = '$demandId'"
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
    'L2-RR-05', '停摆之后车载端 HMI 上出现可用的「申请恢复」入口',
    $recoveryAvailable, $true, $recoveryAvailable)

if (-not $recoveryAvailable) {
    # 到此为止，剩下的判据一条都不记：没做过的事不该有结论。
    $journal.Note(
        'The onboard never offered a usable recovery entry; the scenario stops here rather than ' +
        'recording verdicts for steps it never reached.')
    return
}

$journal.Note('Maintenance administrator requests recovery from the onboard HMI.')
$onboard.RequestRecovery()
$null = $onboard.Confirm('申请恢复原操作')
$journal.Note('Confirmed the on-site safety dialog.')

$null = Wait-L2Condition -Description 'the server opened the exception recovery session' `
    -Journal $journal -Criterion 'recovery-session-requested' -TimeoutSeconds 60 `
    -Probe { Get-InboxCount 'ExceptionRecoverySessionRequested' } -Until { param($v) $v -ge 1 }
$null = Wait-L2Condition -Description 'the onboard selected RESUME_AFTER_REPAIR' `
    -Journal $journal -Criterion 'recovery-action-submitted' -TimeoutSeconds 60 `
    -Probe { Get-InboxCount 'RecoveryActionSubmitted' } -Until { param($v) $v -ge 1 }
$resumeCommands = Wait-L2Condition -Description 'the server issued SlotOperationResumeCommand' `
    -Journal $journal -Criterion 'resume-command' -TimeoutSeconds 60 `
    -Probe { Get-OutboxCount 'SlotOperationResumeCommand' } -Until { param($v) $v -ge 1 }

$actionRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT WorkflowType, State FROM RecoveryWorkflows WHERE SlotOperationAttemptId = '$attemptId'"
$assertions.Add(
    'L2-RR-06', '走的是 RESUME_AFTER_REPAIR，服务端只下发了一条恢复授权',
    ($actionRows.Count -eq 1 -and [string]$actionRows[0].WorkflowType -eq 'RESUME_AFTER_REPAIR' -and
        $resumeCommands -eq 1),
    'RESUME_AFTER_REPAIR / 1 条授权',
    "$(if ($actionRows.Count -ge 1) { [string]$actionRows[0].WorkflowType } else { '(none)' }) / $resumeCommands 条授权")

# --- 5. 恢复执行：仓位还是空的，所以门会再开一次，这次把货放进去 ------------------------------------

$resumeSlot = Wait-WaitingOperator $attemptId 'resume'
$assertions.Add(
    'L2-RR-07', '恢复沿用原始命令的仓位，不重新选仓',
    ($resumeSlot -eq $loadSlot), $loadSlot, $resumeSlot)

$journal.Note("Operator puts the basket into slot $resumeSlot and closes the door.")
$null = $simulator.Command('Put', "slots/$resumeSlot/cargo", @{ state = 'OCCUPIED' })
$null = $simulator.Command('Post', "slots/$resumeSlot/close-door", @{})

$loadPhysical = Wait-L2Condition -Description 'the recovered slot is closed, locked and occupied' `
    -Journal $journal -Criterion 'resume-slot-physical' -TimeoutSeconds 60 `
    -Probe {
        $slot = Get-Slot $resumeSlot
        "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
    } `
    -Until { param($v) $v -eq 'CLOSED/OCCUPIED/1/0' }
$assertions.Add(
    'L2-RR-08', '恢复走的仍是真 Modbus 闭环：车载端再次开锁、放货、关门、锁反馈回到 1、开锁输出复位',
    ($loadPhysical -eq 'CLOSED/OCCUPIED/1/0'), 'CLOSED/OCCUPIED/1/0', $loadPhysical)

# --- 6. 服务端收下那一份替换结果 -------------------------------------------------------------------

$loadStatus = Wait-L2Condition -Description 'the load operation committed on the replacement result' `
    -Journal $journal -Criterion 'load-status' -TimeoutSeconds 120 `
    -Probe { Get-LoadStatus } -Until { param($v) $v -eq 'Committed' }
$assertions.Add(
    'L2-RR-09', '同一操作凭替换结果提交（Committed）',
    ($loadStatus -eq 'Committed'), 'Committed', $loadStatus)

# 这条是服务端那一半在库里的形状：两份结果都还在，第一份指向第二份。失败的那份是车载端当时确实
# 报了什么的记录，不该被覆盖掉。
$results = Get-Results $attemptId
$superseded = if ($results.Count -eq 2) { [string]$results[0].SupersededByResultId } else { '' }
$replacementId = if ($results.Count -eq 2) { [string]$results[1].ResultId } else { '' }
$assertions.Add(
    'L2-RR-10', '失败结果留作记录并指向替换结果，替换结果自己仍然存活',
    ($results.Count -eq 2 -and $superseded -eq $replacementId -and
        [string]::IsNullOrEmpty([string]$results[1].SupersededByResultId) -and
        [string]$results[1].OverallOutcome -eq 'COMPLETED'),
    '2 份，第一份 superseded 指向第二份，第二份 COMPLETED',
    "$($results.Count) 份，superseded=$superseded，replacement=$replacementId")

$workflowRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT State FROM RecoveryWorkflows WHERE SlotOperationAttemptId = '$attemptId'"
$sessionRows = Invoke-L2Query -Connection $connection -Sql "SELECT State FROM ExceptionRecoverySessions"
$assertions.Add(
    'L2-RR-11', '恢复工作流对账完成，恢复会话关闭',
    ($workflowRows.Count -eq 1 -and [string]$workflowRows[0].State -eq 'Reconciled' -and
        $sessionRows.Count -eq 1 -and [string]$sessionRows[0].State -eq 'CLOSED'),
    'Reconciled / CLOSED',
    "$(if ($workflowRows.Count -ge 1) { [string]$workflowRows[0].State } else { '(none)' }) / $(if ($sessionRows.Count -ge 1) { [string]$sessionRows[0].State } else { '(none)' })")

# --- 7. 旅程接着往下走 -----------------------------------------------------------------------------

$stage = Wait-L2Condition -Description 'the journey resumed and reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$assertions.Add(
    'L2-RR-12', '恢复之后旅程不再停摆，继续到关卡段',
    ($stage -eq 'AwaitingGateArrival'), 'AwaitingGateArrival', $stage)

$gateIntent = Get-UpperId -purpose 'TO_GATE'
$assertions.Add(
    'L2-RR-13', '恢复后才建 TO_GATE 单，且全程只此一条',
    ($null -ne $gateIntent -and $gateIntent.Status -eq 'CONFIRMED' -and
        @($riot.Snapshot().body.orders).Count -eq 2),
    'CONFIRMED / 2 条 RIoT 单',
    "$(if ($gateIntent) { $gateIntent.Status } else { '(no intent)' }) / $(@($riot.Snapshot().body.orders).Count) 条 RIoT 单")

$journal.Note('Scenario finished: the load recovered and the journey continued.')
