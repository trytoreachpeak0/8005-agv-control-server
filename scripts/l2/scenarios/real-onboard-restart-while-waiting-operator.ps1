#Requires -Version 7

<#
客户端在「开了锁、等操作员」时退出，重启之后这一次装载有没有出口（8005-agv-program#40）。

**现场实测过它没有。**2026-09-11 现场窗口一（证据 `evidence/field/20260911-FW-SC1-operator-inaction/`，
帧 `03-deadlocked-recovery-refused`）：停靠 2 开锁等操作员时客户端被关掉，重启后两端互相等对方——

- 车载端握手如实上报那次没了结的 attempt（`unsettledSlotOperationAttemptId`、
  `ACTIVE_UNLOCK_SET`），**但不补交 `OperationResult`**；它只在收到命令或恢复动作时才结算一次
  attempt。
- 服务端因此判 `PENDING_FACT_RECONCILIATION_REQUIRED`，会话不 `Ready`，于是**不重放装货命令**
  （重放只给 `Ready` 的会话）；它没有结果，仓位操作停在 `Prepared`，引擎只在 `RecoveryRequired`
  时才 `Block`，旅程停在 `AwaitingLoadResult`。
- 恢复入口要求旅程是 `Blocked`，HMI 上点「补偿清空」被拒 `RECOVERY_DEMAND_NOT_BLOCKED`。

断电、进程崩溃、Windows 更新重启都落在同一格，所以这是产品缺口不是操作失误。

## 为什么是杀进程，不是断网

执行器挂在车载端服务的生命周期上，不跟连接走：**断网重连时操作还在跑**，而握手上报的
`RecoveryStateReport` 与进程重启时一字不差。只有进程真的没了，那次 attempt 才成了没人认领的
孤儿。`StopComponent` 用的是 `Kill`，与断电同形——不给进程任何收尾的机会。

## 现场那一步照抄现场

客户端死着的时候，操作员把门关上、没放货。这正是 2026-09-11 的现场：用户在关客户端时确认 3、4
号仓门关着、没放料。所以重启那一刻车辆读到的是**门已闭、已锁、开锁输出已复位、仓里是空的**——
三个物理字段全都读得到，唯一不知道的是这次操作本该怎么往下走。

因为仓是空的，补偿向量对它一次 IO 都不碰就报 `ALL_EMPTY`（`real-onboard-recovery-compensate-load`
文件头第 3 条，红证据 `-002`）。**这一条不证补偿在 Modbus 上怎么清仓，那是上一条场景的事**；
它证的是从「重启」到「对账完成」这条链有没有断口。

## 判据怎么红

修之前这条链断在 `L2-RW-04`（旅程到不了 `Blocked`）与 `L2-RW-06`（恢复会话被拒
`RECOVERY_DEMAND_NOT_BLOCKED`）。这两条用不抛异常的等待写：超时不是场景出错，是判据的实际值，
要让它进判据表而不是进 `Last observed:`。会话被拒之后后面的判据无从谈起，场景就地结束。

`L2-RW-11`/`L2-RW-12`（8005-agv-program#46）同样写法：对账之后会话回不到 `Ready`，车就接不了下一单。

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

function Get-SlotReading([int]$slotNo) {
    $slot = Get-Slot $slotNo
    return "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
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

function Get-InboxRows([string]$messageType) {
    return @(Invoke-L2Query -Connection $connection `
        -Sql "SELECT RequestJson, FirstResponseJson FROM ProtocolInbox WHERE MessageType = '$messageType' ORDER BY ReceivedAt")
}

function Get-OperationResults([string]$attemptId) {
    return @(Invoke-L2Query -Connection $connection `
        -Sql "SELECT OverallOutcome, SupersededByResultId FROM OperationResults
              WHERE SlotOperationAttemptId = '$attemptId' ORDER BY ReceivedAt")
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
        -Sql 'SELECT SessionGeneration, Readiness, ReasonCode, PendingAttemptIdsJson FROM SessionRecoveries'
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

<#
Wait-L2Condition 超时就抛异常。这条场景有两条判据**修之前必然超时**，而超时正是它们的实际值——
抛出去就只剩一行 `Last observed:`，判据表里什么都没有。所以超时时再读一次世界，交回给判据。
#>
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

# --- 3. 起点：车载端开了锁、在等操作员 -------------------------------------------------------------

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

# 等它自己发 WAITING_OPERATOR：那一刻它确实观测到门开着、在等人，正是现场关客户端的那一格。
$null = Wait-L2Condition -Description 'the onboard is waiting for the operator on the target slot' `
    -Journal $journal -Criterion 'load-waiting-operator' -TimeoutSeconds 120 `
    -Probe { Get-PhaseCount $attemptId 'WAITING_OPERATOR' } -Until { param($v) $v -ge 1 }

$membership = Invoke-L2Query -Connection $connection `
    -Sql "SELECT LoadCommandMessageId FROM JourneyDemands WHERE DemandId = '$demandId'"
$loadCommandMessageId = [string]$membership[0].LoadCommandMessageId
$loadCommandPendingBefore = Invoke-L2Query -Connection $connection `
    -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$loadCommandMessageId'"
$loadCommandWasPending = ($loadCommandPendingBefore.Count -eq 1 -and
    (Test-L2Null $loadCommandPendingBefore[0].AcknowledgedAt))

$doorAtWait = [string](Get-Slot $loadSlot).doorState
$statusAtWait = [string](Get-LoadOperation).Status
$assertions.Add(
    'L2-RW-01', '起点到位：车载端开了锁、在等操作员，门开着，仓位操作 Prepared，装货命令挂着',
    ($doorAtWait -eq 'OPEN' -and $statusAtWait -eq 'Prepared' -and $loadCommandWasPending),
    'OPEN / Prepared / 装货命令挂着',
    "$doorAtWait / $statusAtWait / $(if ($loadCommandWasPending) { '装货命令挂着' } else { '装货命令不是挂着的' })")

# --- 4. 客户端没了：杀进程，与断电同形 -------------------------------------------------------------

& $Context.StopComponent 'onboard-hmi'

# 进程没了之后服务端一个字都没收到：结果只可能在重启之后才来，否则下面的判据证的就不是重启。
$resultsWhileDown = (Get-OperationResults $attemptId).Count
$statusWhileDown = [string](Get-LoadOperation).Status
$assertions.Add(
    'L2-RW-02', '客户端退出时没有留下结果：OperationResults 0 行，仓位操作仍是 Prepared',
    ($resultsWhileDown -eq 0 -and $statusWhileDown -eq 'Prepared'),
    '0 行 / Prepared', "$resultsWhileDown 行 / $statusWhileDown")

# 现场那一步：客户端死着的时候操作员把门关上，没放货。
$journal.Note("While the onboard is down the operator closes slot $loadSlot without loading it.")
$null = $simulator.Command('Post', "slots/$loadSlot/close-door", @{})
$closed = Wait-L2Condition -Description 'the slot reads closed, empty, locked and reset' `
    -Journal $journal -Criterion 'slot-closed-empty' -TimeoutSeconds 30 `
    -Probe { Get-SlotReading $loadSlot } -Until { param($v) $v -eq 'CLOSED/EMPTY/1/0' }
$journal.Note("Slot $loadSlot now reads $closed.")

# --- 5. 重启 -------------------------------------------------------------------------------------

$reportsBefore = Get-InboxCount 'RecoveryStateReport'
$onboard = & $Context.RelaunchOnboard

$null = Wait-L2Condition -Description 'the relaunched onboard sent its RecoveryStateReport' `
    -Journal $journal -Criterion 'relaunch-report' -TimeoutSeconds 120 `
    -Probe { Get-InboxCount 'RecoveryStateReport' } -Until { param($v) $v -gt $reportsBefore }
$report = ([string](Get-InboxRows 'RecoveryStateReport')[-1].RequestJson | ConvertFrom-Json).payload
$assertions.Add(
    'L2-RW-03', '重启后车载端握手如实上报那一次未结的 attempt',
    ([string]$report.unsettledSlotOperationAttemptId -eq $attemptId),
    $attemptId, [string]$report.unsettledSlotOperationAttemptId)

# --- 6. 这一次 attempt 有没有结论 ------------------------------------------------------------------

$blocked = Wait-L2ConditionOrLast -Description 'the journey blocked on the interrupted load' `
    -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe {
        $runtime = Get-Runtime
        $operation = Get-LoadOperation
        "$([string]$runtime.Stage) / $([string]$runtime.BlockReasonCode) / $([string]$operation.Status)"
    } `
    -Until { param($v) $v -eq 'Blocked / LOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired' }
$results = Get-OperationResults $attemptId
$assertions.Add(
    'L2-RW-04', '重启之后这次装载拿到结论：服务端判 RecoveryRequired 并让旅程停摆，恢复入口才有地方落',
    ($blocked -eq 'Blocked / LOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired' -and $results.Count -eq 1),
    'Blocked / LOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired，结果 1 份',
    "$blocked，结果 $($results.Count) 份")

# 车辆交上来的那一份，每个仓位的物理字段必须是它重启后读到的真实读数：门闭、已锁、复位、空仓。
# 结论可以是「不知道该怎么往下走」，但不能把读得到的东西报成不知道。
$resultRows = Get-InboxRows 'OperationResult'
$resultPayload = if ($resultRows.Count -gt 0) { ([string]$resultRows[-1].RequestJson | ConvertFrom-Json).payload } else { $null }
$resultSlot = if ($resultPayload) { @($resultPayload.slotResults | Where-Object { [int]$_.slotNo -eq $loadSlot })[0] } else { $null }
$resultShape = if ($resultSlot) {
    "$([string]$resultPayload.overallOutcome) / $([string]$resultSlot.outcome) / " +
        "$([string]$resultSlot.finalPhysicalState) / $([string]$resultSlot.lockState) / $([string]$resultSlot.unlockOutputState)"
} else { '(no result)' }
$assertions.Add(
    'L2-RW-05', '那份结论报的是 UNKNOWN，但仓位的三个物理字段是重启后的真实读数：EMPTY / LOCKED / RESET',
    ($resultShape -eq 'UNKNOWN / UNKNOWN / EMPTY / LOCKED / RESET'),
    'UNKNOWN / UNKNOWN / EMPTY / LOCKED / RESET', $resultShape)

# --- 7. 按下「补偿清空」 ---------------------------------------------------------------------------

$compensationAvailable = Wait-L2ConditionOrLast -Description 'the compensation entry became available on the HMI' `
    -Criterion 'onboard-compensation-entry' -TimeoutSeconds 120 `
    -Probe { $onboard.ButtonEnabled($compensationButton) } -Until { param($v) $v }
if (-not $compensationAvailable) {
    $assertions.Add(
        'L2-RW-06', "重启后 HMI 上有可用的「$compensationButton」，按下去能开出恢复会话",
        $false, '入口可用 / 会话 OPEN', '入口不可用')
    $journal.Note('Scenario stopped: no compensation entry after the relaunch.')
    return
}

$requestsBefore = Get-InboxCount 'ExceptionRecoverySessionRequested'
$journal.Note("Invoking the $compensationButton button and confirming the modal dialog.")
$onboard.InvokeButton($compensationButton)
$null = $onboard.Confirm($compensationButton)

# 会话开出来，或者请求被拒——两者都是答案，等到其中一个为止。
$sessionAnswer = Wait-L2ConditionOrLast -Description 'the server answered the recovery session request' `
    -Criterion 'recovery-session-answer' -TimeoutSeconds 90 `
    -Probe {
        $session = Get-RecoverySession
        if ($null -ne $session) { return "OPENED:$([string]$session.State)" }
        $requests = Get-InboxRows 'ExceptionRecoverySessionRequested'
        if ($requests.Count -le $requestsBefore) { return 'PENDING' }
        $response = [string]$requests[-1].FirstResponseJson | ConvertFrom-Json
        if ($response.messageType -eq 'ExceptionRecoverySessionRejected') {
            return "REJECTED:$([string]$response.payload.problem.reasonCode)"
        }
        return 'PENDING'
    } `
    -Until { param($v) $v -ne 'PENDING' }
$assertions.Add(
    'L2-RW-06', "重启后按「$compensationButton」开得出恢复会话，不被拒 RECOVERY_DEMAND_NOT_BLOCKED",
    ($sessionAnswer -like 'OPENED:*'), 'OPENED', $sessionAnswer)
if ($sessionAnswer -notlike 'OPENED:*') {
    $journal.Note("Scenario stopped: the recovery session was not opened ($sessionAnswer).")
    return
}

# --- 8. 补偿握手走到对账 ---------------------------------------------------------------------------

$workflowState = Wait-L2Condition -Description 'the server reconciled the compensation workflow' `
    -Journal $journal -Criterion 'compensation-reconciled' -TimeoutSeconds 180 `
    -Probe { $row = Get-CompensationWorkflow; if ($row) { [string]$row.State } else { $null } } `
    -Until { param($v) $v -in @('Reconciled', 'RecoveryRequired') }
$compensationRows = Get-InboxRows 'LoadCompensationResult'
$compensationOutcome = if ($compensationRows.Count -gt 0) {
    [string]([string]$compensationRows[-1].RequestJson | ConvertFrom-Json).payload.overallOutcome
} else { '(no result)' }
$assertions.Add(
    'L2-RW-07', '补偿握手走完：车载端报 ALL_EMPTY，服务端对账 Reconciled',
    ($workflowState -eq 'Reconciled' -and $compensationOutcome -eq 'ALL_EMPTY'),
    'Reconciled / ALL_EMPTY', "$workflowState / $compensationOutcome")

$finalReading = Get-SlotReading $loadSlot
$assertions.Add(
    'L2-RW-08', '现场收在安全状态：门关、仓空、已锁、开锁输出复位',
    ($finalReading -eq 'CLOSED/EMPTY/1/0'), 'CLOSED/EMPTY/1/0', $finalReading)

# --- 9. 终局：这一单判死、旅程收尾、悬空命令结算 ----------------------------------------------------

$stage = Wait-L2Condition -Description 'the journey recorded the compensation as its terminal reason' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'Completed' }
$runtime = Get-Runtime
$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
$demandStatus = if ($demandRows.Count -gt 0) { [string]$demandRows[0].Status } else { '(none)' }
$assertions.Add(
    'L2-RW-09', '补偿终结这一单：需求 Cancelled，旅程以 CANCELLED_BY_LOAD_COMPENSATION 收尾',
    ($demandStatus -eq 'Cancelled' -and $stage -eq 'Completed' -and
        [string]$runtime.BlockReasonCode -eq 'CANCELLED_BY_LOAD_COMPENSATION'),
    'Cancelled / Completed / CANCELLED_BY_LOAD_COMPENSATION',
    "$demandStatus / $stage / $([string]$runtime.BlockReasonCode)")

$settled = Wait-L2ConditionOrLast -Description 'the dangling LoadBatch command was settled' `
    -Criterion 'load-command-settled' -TimeoutSeconds 90 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$loadCommandMessageId'"
        if ($rows.Count -eq 0) { return '(no row)' }
        if (Test-L2Null $rows[0].AcknowledgedAt) { return 'pending' }
        return [string]$rows[0].AcknowledgedAt
    } `
    -Until { param($v) $v -notin @('pending', '(no row)') }
$assertions.Add(
    'L2-RW-10', '重启前挂着的那条装货命令被补偿这一路结算掉，不会被重放进后来的会话',
    ($loadCommandWasPending -and $settled -notin @('pending', '(no row)')),
    '重启前挂着 / 补偿后已结算',
    "$(if ($loadCommandWasPending) { '重启前挂着' } else { '重启前就不是挂着的' }) / $settled")

# --- 10. 车还回来：会话回到 Ready，下一条需求派得出去、车收得下扫码 ---------------------------------
#
# 8005-agv-program#46。这一段之前只记不判：`-004`/`-005` 两次绿里，对账完成之后会话一直是
# `RecoveryRequired / PENDING_FACT_RECONCILIATION_REQUIRED`，车辆早清掉的那个 attempt 还挂在服务端。
# 救完一趟旅程车却接不了下一单，唯一的出路是再重启一次客户端——正是 #40 刚修掉的那一类。
#
# 服务端库里 Ready 还不够：它说的是服务端怎么想，不是车知不知道。所以第二条判据要车辆自己收下下一站
# 的扫码——车载端只在自己的会话是 READY 时才放行提交（`WireToGateBusinessService.CanSubmitSublot`），
# 服务端的 SublotSubmitted 落库就是车辆知道了的证据。

$sessionAfter = Wait-L2ConditionOrLast -Description 'the session returned to Ready after the compensation' `
    -Criterion 'session-ready-after-compensation' -TimeoutSeconds 60 `
    -Probe {
        $row = Get-SessionRecovery
        "$([string]$row.Readiness) / $([string]$row.ReasonCode)"
    } `
    -Until { param($v) $v -eq 'Ready / READY' }
$pendingAfter = [string](Get-SessionRecovery).PendingAttemptIdsJson
$assertions.Add(
    'L2-RW-11', '补偿对账之后服务端会话自己回到 Ready，不必再重启客户端',
    ($sessionAfter -eq 'Ready / READY'), 'Ready / READY',
    "$sessionAfter（握手上报的 pending attempts $pendingAfter）")
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

# 先等服务端进入 AwaitingSublot 再扫：车载端提交之后不清空上一轮的录入请求，输入框可能早就是可用的
# （scripts/l2/README.md 多需求那几条）。
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
        # (-007 died on exactly that, the moment a second SublotSubmitted landed).
        $count = 0
        foreach ($row in (Get-InboxRows 'SublotSubmitted')) {
            if (([string]$row.RequestJson | ConvertFrom-Json).payload.sublot -eq $nextSublot) { $count++ }
        }
        $count
    } `
    -Until { param($v) $v -ge 1 }
$assertions.Add(
    'L2-RW-12', '车还回来了：下一条需求被受理并派车，车在取货点收下扫码并交给服务端',
    ($nextSubmitted -ge 1), "SublotSubmitted($nextSublot) 1 条", "$nextSubmitted 条")

$journal.Note(
    'Scenario finished: an onboard killed while waiting for the operator came back, settled the ' +
    'interrupted attempt, the compensation handshake closed the demand, and the vehicle took the next one.')
