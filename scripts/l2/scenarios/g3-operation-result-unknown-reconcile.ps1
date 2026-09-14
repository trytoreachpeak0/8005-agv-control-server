#Requires -Version 7

<#
G3 `FP-IS-03` 的第二条场景：状态未知的结果在车载端重启后对账。协议向量 `CV-OPERATION-RESULT-UNKNOWN-RECONCILE`：
`OperationResult` → `DurableAck` → `RecoveryStateReport` → `OperationResult` → `DurableAck`；服务端 `NEVER_TREAT_UNKNOWN_AS_SUCCESS`、
`RECONCILE_FROM_REPORTED_JOURNAL`，车载端 `REPORT_UNKNOWN_AS_UNKNOWN`、`REPLAY_RESULT_ON_RECONNECT`。

装置与 `FP-IS-01`/`02` 相同：真服务端 + 真车载端 WPF + 真 slots-simulator + 假 RIoT + 假 MesIngest。

**怎么得到一份状态未知的结果。**与 `real-onboard-recovery-entry-missing` 同一个办法：车载端报 `WAITING_OPERATOR` 之后关门、
不放货。车载端等满自己的操作员超时（`workflow.operationTimeoutMs`，120 秒）才报结果，`overallOutcome = UNKNOWN`。
这一步要花满两分钟，理由见那条场景的说明：不在暂存副本里调短安全相关的时序。

**重启。**`Context.RestartOnboard` 停掉车载端进程，用同一份暂存副本、配置与日志重新拉起。重启之后车载端报的，
是它从自己日志里恢复出来的东西。两端这一半都是 2026-09-13 按用户裁定补上的：服务端
`docs/defects/20260913-unknown-result-never-reconciled.md`，车载端 `docs/W2G_UNKNOWN_RESULT_RECONCILE.md`。

**第一份结果要在重启前记下来。**服务端收件箱按 `messageId` 存一行；补发只差会话代号，被判为等价重放后，那一行的请求、
内容哈希与首次应答会换成补发的那份。所以场景在重启前先把第一份的请求与应答抄下来。

**断言只读服务端的库、模拟器与假 RIoT 快照。**
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

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: the replay comes from the onboard journal across a restart.'
}

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "G3-03U-$($Context.RunId)"

function Get-Count([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    return [int]$rows[0].Total
}

function Get-Scalar([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Value
}

function Get-Stage { return Get-Scalar "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'" }

function ConvertTo-Instant([object]$value) {
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

# 一行收件箱：请求整行、它的会话代号与载荷、内容哈希、首次应答的第一行（DurableAck）。
function ConvertTo-InboxMessage([object]$row) {
    $request = [string]$row.RequestJson | ConvertFrom-Json
    $firstLine = @(([string]$row.FirstResponseJson) -split "`n" | Where-Object { $_ })[0]
    return [pscustomobject]@{
        MessageId   = [string]$row.MessageId
        At          = ConvertTo-Instant $row.ReceivedAt
        Generation  = [long]$request.sessionGeneration
        Payload     = $request.payload
        PayloadJson = $request.payload | ConvertTo-Json -Depth 20 -Compress
        ContentHash = [string]$row.ContentHash
        Ack         = if ($firstLine) { $firstLine | ConvertFrom-Json } else { $null }
    }
}

function Get-Inbound([string]$messageType) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, RequestJson, ContentHash, FirstResponseJson, ReceivedAt FROM ProtocolInbox " +
        "WHERE MessageType = '$messageType' ORDER BY ReceivedAt, MessageId")
    $messages = foreach ($row in $rows) { ConvertTo-InboxMessage $row }
    return , @($messages)
}

function Get-Progress([string]$attemptId) {
    $mine = foreach ($message in (Get-Inbound 'OperationProgress')) {
        if ([string]$message.Payload.slotOperationAttemptId -ne $attemptId) { continue }
        [pscustomobject]@{ At = $message.At; Phase = [string]$message.Payload.phase; Active = @($message.Payload.activeUnlockSlots | ForEach-Object { [int]$_ }) }
    }
    return , @($mine)
}

function Get-Session {
    $rows = Invoke-L2Query -Connection $connection -Sql 'SELECT SessionGeneration, Readiness, ReasonCode, PendingResultIdsJson FROM SessionRecoveries'
    return $rows[0]
}

function Get-SlotState([int]$slotNo) {
    $slot = @($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $slotNo })[0]
    return "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
}

# --- 1. 需求受理、到取货点、录入 ------------------------------------------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot = $sublot; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})
$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$intent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'"
        if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
    } -Until { param($v) $null -ne $v }

$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = $intent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.PickupStationRiotId; processingOrder = $false; clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })

$null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
$onboard.SetSublot($sublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()

# --- 2. 关上空门：装载以 UNKNOWN 结束 ---------------------------------------------------------------------

$attemptId = Wait-L2Condition -Description 'the server issued the load command' `
    -Journal $journal -Criterion 'load-attempt' -TimeoutSeconds 180 `
    -Probe { Get-Scalar "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'" } `
    -Until { param($v) $v }
$waiting = Wait-L2Condition -Description 'the onboard is waiting for the operator' `
    -Journal $journal -Criterion 'load-waiting-operator' -TimeoutSeconds 120 `
    -Probe { @((Get-Progress $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })[0] } `
    -Until { param($v) $null -ne $v }
$loadSlot = [int]$waiting.Active[0]
$journal.Note("Operator closes slot $loadSlot without the basket; waiting out the onboard operation timeout (about 120s).")
$null = $simulator.Command('Post', "slots/$loadSlot/close-door", @{})

$first = Wait-L2Condition -Description 'the onboard reported the load result and the server acknowledged it' `
    -Journal $journal -Criterion 'unknown-result' -TimeoutSeconds 240 `
    -Probe { @((Get-Inbound 'OperationResult') | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $attemptId })[0] } `
    -Until { param($v) $null -ne $v -and $null -ne $v.Ack }
$null = Wait-L2Condition -Description 'the journey blocked on the unknown load result' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 60 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'Blocked' }
$sessionBefore = Get-Session
$unlockingBefore = @((Get-Progress $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' }).Count
$slotBefore = Get-SlotState $loadSlot
$journal.Note("First result $($first.MessageId): $($first.Payload.overallOutcome) in generation $($first.Generation); slot $loadSlot is $slotBefore.")

# --- 3. 车载端重启：报告列出待结清、补发、确认 ------------------------------------------------------------

$restartAt = [DateTimeOffset]::UtcNow
$null = & $Context.RestartOnboard
$onboard = $Context.Onboard
$replayed = Wait-L2Condition -Description 'the restarted onboard replayed the result in its new session' `
    -Journal $journal -Criterion 'replayed-result' -TimeoutSeconds 240 `
    -Probe {
        $row = @((Get-Inbound 'OperationResult') | Where-Object { $_.MessageId -eq $first.MessageId })[0]
        if ($null -ne $row -and $row.Generation -gt $first.Generation -and $null -ne $row.Ack) { $row } else { $null }
    } -Until { param($v) $null -ne $v }
# 补发之后再给服务端几轮：它若要据此改动什么（提交、派车、就绪），应当已经发生了。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

$report = @((Get-Inbound 'RecoveryStateReport') | Where-Object { $_.Generation -eq $replayed.Generation })[0]
$pending = if ($null -ne $report) { @($report.Payload.pendingResults) } else { @() }
$orderOk = $null -ne $report -and $first.At -lt $restartAt -and
    [string]$first.Ack.messageType -eq 'DurableAck' -and [string]$first.Ack.payload.acceptedContentSha256 -eq $first.ContentHash -and
    $report.At -gt $restartAt -and $report.At -lt $replayed.At -and
    [string]$replayed.Ack.messageType -eq 'DurableAck' -and [long]$replayed.Ack.sessionGeneration -eq $replayed.Generation -and
    [string]$replayed.Ack.payload.acceptedContentSha256 -eq $replayed.ContentHash -and $replayed.ContentHash -ne $first.ContentHash
$assertions.Add(
    'G3-03-08',
    '消息顺序与向量一致：结果 → 它的 DurableAck → 重启后新会话的恢复状态报告 → 同一 messageId 的结果按新会话代号补发 → 补发的 DurableAck（CV-OPERATION-RESULT-UNKNOWN-RECONCILE orderedExpectedMessages）',
    $orderOk,
    "Result(g$($first.Generation)) < Ack < RecoveryStateReport(g$($replayed.Generation)) < Result(g$($replayed.Generation), 同号) < Ack(新行哈希)",
    "Result g$($first.Generation) ack=$(if ($first.Ack) { $first.Ack.messageType }) / Report $(if ($report) { "g$($report.Generation)" } else { '(none)' }) / Replay g$($replayed.Generation) ack=$(if ($replayed.Ack) { $replayed.Ack.messageType }) / 有序=$orderOk")

$named = @($pending | Where-Object { [string]$_.messageId -eq $first.MessageId -and [string]$_.messageType -eq 'OperationResult' -and [string]$_.businessId -eq $attemptId })
$assertions.Add(
    'G3-03-09',
    '车载端如实报未知并从日志补发：结果 overallOutcome 为 UNKNOWN；重启后的报告把这次操作列为未结清、把这份结果列进 pendingResults；补发的载荷与第一份逐字段相同（REPORT_UNKNOWN_AS_UNKNOWN / REPLAY_RESULT_ON_RECONNECT）',
    ([string]$first.Payload.overallOutcome -eq 'UNKNOWN' -and $null -ne $report -and
        [string]$report.Payload.unsettledSlotOperationAttemptId -eq $attemptId -and $named.Count -eq 1 -and
        $replayed.PayloadJson -eq $first.PayloadJson),
    "UNKNOWN / unsettled=$attemptId / pendingResults 含该结果 / 载荷相同",
    "$($first.Payload.overallOutcome) / unsettled=$(if ($report) { $report.Payload.unsettledSlotOperationAttemptId } else { '(no report)' }) / pendingResults $($pending.Count) 条、含该结果 $($named.Count) / 载荷相同=$($replayed.PayloadJson -eq $first.PayloadJson)")

$results = (Invoke-L2Query -Connection $connection -Sql "SELECT OverallOutcome FROM OperationResults WHERE SlotOperationAttemptId = '$attemptId'")
$loadStatus = Get-Scalar "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
$demandStatus = Get-Scalar "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
$gateIntents = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'"
$gateOrders = @($riot.Snapshot().body.orders | Where-Object { [string]$_.upperId -like '*GATE*' }).Count
$stage = Get-Stage
$assertions.Add(
    'G3-03-10',
    '未知不当成功、不重复提交：这次操作只有一行结果且为 UNKNOWN，装载仍 RecoveryRequired，需求未成功，旅程仍停在 Blocked，没有去关卡的意图也没有关卡单（NEVER_TREAT_UNKNOWN_AS_SUCCESS / forbidden unknown-as-success、duplicate-business-commit）',
    ($results.Count -eq 1 -and [string]$results[0].OverallOutcome -eq 'UNKNOWN' -and $loadStatus -eq 'RecoveryRequired' -and
        $demandStatus -ne 'Succeeded' -and $stage -eq 'Blocked' -and $gateIntents -eq 0 -and $gateOrders -eq 0),
    '1 行 UNKNOWN / RecoveryRequired / 未成功 / Blocked / 意图 0 / 关卡单 0',
    "$($results.Count) 行 $(if ($results.Count -ge 1) { $results[0].OverallOutcome }) / $loadStatus / $demandStatus / $stage / 意图 $gateIntents / 关卡单 $gateOrders")

$sessionAfter = Get-Session
$assertions.Add(
    'G3-03-11',
    '服务端按报告的日志对账且不提前就绪：新会话代次大于重启前，补发之后待结清列表已清空，会话仍 RecoveryRequired（RECONCILE_FROM_REPORTED_JOURNAL / forbidden ready-before-reconciliation）',
    ([long]$sessionAfter.SessionGeneration -gt [long]$sessionBefore.SessionGeneration -and
        [string]$sessionAfter.PendingResultIdsJson -eq '[]' -and [string]$sessionAfter.Readiness -eq 'RecoveryRequired'),
    "代次 >$($sessionBefore.SessionGeneration) / [] / RecoveryRequired",
    "代次 $($sessionAfter.SessionGeneration) / $($sessionAfter.PendingResultIdsJson) / $($sessionAfter.Readiness) ($($sessionAfter.ReasonCode))")

$unlockingAfter = @((Get-Progress $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' }).Count
$slotAfter = Get-SlotState $loadSlot
$assertions.Add(
    'G3-03-12',
    '补发不碰物理：重启之后这次操作没有再开锁，装载仓仍是关门、空、锁上、开锁输出复位（forbidden duplicate-slot-unlock / finalState NO_UNPROVEN_STATE）',
    ($unlockingAfter -eq $unlockingBefore -and $slotAfter -eq 'CLOSED/EMPTY/1/0' -and $slotAfter -eq $slotBefore),
    "UNLOCKING $unlockingBefore 次不变 / CLOSED/EMPTY/1/0",
    "UNLOCKING $unlockingBefore → $unlockingAfter / $slotBefore → $slotAfter")

$journal.Note('FP-IS-03: an UNKNOWN load result was reported, named as pending after an onboard restart, replayed and reconciled without success.')
