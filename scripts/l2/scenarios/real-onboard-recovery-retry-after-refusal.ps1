#Requires -Version 7

<#
恢复会话请求被服务端拒过一次之后，同一个 attempt 还能再请求并把补偿走完。8005-agv-program#49。

现场（[救出卡在停靠 2 的旅程 54d2cf63](https://github.com/trytoreachpeak0/8005-agv-program/issues/44)）的顺序是：
旅程还到不了 `Blocked` 时有人按了「补偿清空」，服务端正确地回 `RECOVERY_DEMAND_NOT_BLOCKED`；等旅程真的
`Blocked` 了再按，两次都是 `HTTP 409 ControlServer在旅程会话期间关闭了连接。`，服务端同一时刻抛
`ProtocolContentConflictException: MessageId was replayed with different normalized content.`。车载端的
请求 id 由 attempt 算出来，第二次按下带着同一个 messageId、却是新的 `verifiedAt`/`reason`/`sentAt`，而服务端
`ProtocolInbox` 按整行字节把 messageId 绑死在首个应答上。

**「旅程还没 Blocked」怎么在这里造出来。**`Blocked` 只由 `JourneyRuntimeEngine` 写，引擎每一轮第一件事是读
RIoT 站点目录并认出关卡站（`RequireFixedStation`），认不出就整轮 `return`（`LogMapStationCatalogFailed`）。所以在
车报回 `UNKNOWN` 之前把关卡站从假 RIoT 的地图上拿掉：仓位操作转 `RecoveryRequired` 与会话就绪都是传输层写的，
照常发生；旅程停在 `AwaitingLoadResult` 转不了 `Blocked`。现场是两个旅程门 `Off`，引擎同样整轮不做事——形状相同，
来路不同。

**不能用 `faults/http`。**`-001` 用的就是它，红在场景自己：那个开关是整个假 RIoT 的，车载端也从它读车辆状态，
于是车报 `SafetyUnknownPresent` / `VEHICLE_NOT_READY`，会话在结果出来之前就掉进 `DEPARTURE_SAFETY_NOT_READY`，
车连 `OperationResult` 都发不出去（`WIRE_TO_GATE_NOT_READY`）。只动地图上的关卡站，车载端碰不到；取货站一个不少，
准入策略绑定的站点集合不变（`auto-charge-endurance` 证据 001 踩过的坑）。

判据的核心是 `L2-RAR-05`/`-06`：两次请求是两条不同 messageId 的报文、各自拿到自己的应答，而车载端从第一次
按下到收尾没有重连过——服务端一次都没掐连接。车载端 `96c7513`（修复之前）上的 `-003` 与现场原样：第一次
`409 / RECOVERY_DEMAND_NOT_BLOCKED`，第二次 `409 ControlServer在旅程会话期间关闭了连接。`，服务端只收到一条请求。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection

Import-Module (Join-Path $Context.Repository 'scripts/field/FieldOperator.psm1') -Force

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-RAR-$($Context.RunId)"
$action = 'COMPENSATE_LOAD_ALL_EMPTY'

$field = New-FieldOperator -Connection $connection -AgvId $Context.AgvId `
    -SimulatorPort $Context.SimulatorHttpPort -AutomationPort $Context.OnboardAutomationPort `
    -Log { param($message) $journal.Note("field-operator: $message") }.GetNewClosure()

# Every connection the vehicle opens starts with one SessionHello, so this counts connections.
function Get-SessionHelloCount {
    $rows = @(Invoke-L2Query -Connection $connection `
        -Sql "SELECT COUNT(*) AS N FROM ProtocolInbox WHERE MessageType = 'SessionHello'")
    return [int]$rows[0].N
}

# --- 1. 需求出现，车开到取货点 ------------------------------------------------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$intent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'"
        if ($rows.Count -eq 0) { $null } else { $rows[0] }
    } `
    -Until { param($v) [string]$v.Status -eq 'CONFIRMED' }

$journeyId = [string](Get-FieldJourney -Field $field).JourneyId

$journal.Note('Vehicle drives to the pickup station.')
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'
    speed = 0.8; processingOrder = $true; orderTaskId = $intent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.PickupStationRiotId; processingOrder = $false; clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })

# --- 2. 扫码开锁；引擎停摆之后才让车报回真的 UNKNOWN ------------------------------------------------------

$load = Start-FieldStopLoad -Field $field -JourneyId $journeyId -Sequence 1 -ArrivalTimeoutSeconds 120

$currentMap = @($riot.Snapshot().body.maps | Where-Object { $_.mapId -eq $Context.MapId })
if ($currentMap.Count -ne 1) { throw "Fake RIoT does not serve map $($Context.MapId)." }
$fullStations = @{}
foreach ($station in $currentMap[0].stations) { $fullStations["$($station.id)"] = $station.name }
$withoutGate = @{}
foreach ($key in $fullStations.Keys) {
    if ($key -ne "$($Context.GateStationRiotId)") { $withoutGate[$key] = $fullStations[$key] }
}
if ($withoutGate.Count -ne $fullStations.Count - 1) { throw "Gate station $($Context.GateStationRiotId) is not on map $($Context.MapId)." }
$journal.Note("Gate station $($Context.GateStationRiotId) removed from the RIoT map: every engine iteration returns before it can block the journey.")
$null = $riot.Command('Put', "maps/$($Context.MapId)/stations", @{ stations = $withoutGate })
$null = Invoke-FieldSimulatorCommand -Field $field -Method PUT -Path "/slots/$($load.SlotNo)/lock-feedback-override" -Body @{ mode = 'FIXED_1' }

$unknown = Wait-L2Condition -Description 'the load required recovery while the engine was stalled' -Journal $journal `
    -Criterion 'recovery-required-not-blocked' -TimeoutSeconds 180 `
    -Probe {
        $operation = Get-FieldOperation -Field $field -DemandId $load.DemandId
        $journey = Get-FieldJourney -Field $field -JourneyId $journeyId
        "$($operation.Status) / $($journey.Stage)"
    } `
    -Until { param($v) $v -like 'RecoveryRequired / *' }
$assertions.Add(
    'L2-RAR-01', '车报回真的 UNKNOWN、仓位操作 RecoveryRequired，而引擎停摆让旅程停在 AwaitingLoadResult 转不了 Blocked',
    ($unknown -eq 'RecoveryRequired / AwaitingLoadResult'), 'RecoveryRequired / AwaitingLoadResult', $unknown)

# 维护人员三步，与 Invoke-FieldActUnknownLoad 一致：补偿命令要求目标仓已锁、开锁输出已复位。
$null = Invoke-FieldSimulatorCommand -Field $field -Method PUT -Path "/slots/$($load.SlotNo)/cargo" -Body @{ state = 'OCCUPIED' }
$null = Invoke-FieldSimulatorCommand -Field $field -Method POST -Path "/slots/$($load.SlotNo)/close-door"
$null = Invoke-FieldSimulatorCommand -Field $field -Method PUT -Path "/slots/$($load.SlotNo)/lock-feedback-override" -Body @{ mode = 'AUTO' }
$null = Wait-L2Condition -Description "slot $($load.SlotNo) reads closed, locked and loaded" -Journal $journal `
    -Criterion 'slot-repaired' -TimeoutSeconds 60 `
    -Probe { Get-FieldSimulatorSlot -Field $field -SlotNo $load.SlotNo } `
    -Until { param($s) $s.doorState -eq 'CLOSED' -and $s.cargoState -eq 'OCCUPIED' -and -not $s.lockFeedbackPending -and [int]$s.unlockOutputRaw -eq 0 }

# --- 3. 旅程还没 Blocked 时按「补偿清空」：服务端拒绝 ------------------------------------------------------

try {
    $null = Wait-L2Condition -Description "the vehicle offers $action" -Journal $journal `
        -Criterion 'compensation-offered' -TimeoutSeconds 60 `
        -Probe { @((Get-FieldOnboardSnapshot -Field $field).state.availableRecoveryActions) -join ',' } `
        -Until { param($a) @("$a" -split ',') -contains $action }
} catch {
    # Press anyway: the automation face answers with the first precondition keeping the button
    # hidden, which is the diagnosis a timeout alone would throw away.
    $journal.Note("Not offered: $($_.Exception.Message) Pressing anyway to read the vehicle's own reason.")
}
$hellosBefore = Get-SessionHelloCount

$refused = Invoke-FieldFace -Field $field -Face Onboard -Method POST -Path '/recovery/requests' -Envelope -Body @{
    action = $action
    reason = 'L2：旅程还没 Blocked 时的补偿清空'
}
$refusedCode = [string](Get-FieldProperty $refused.body 'reasonCode')
$stateJson = (Get-FieldOnboardSnapshot -Field $field).state | ConvertTo-Json -Depth 3 -Compress
$journal.Note("First press: HTTP $($refused.status) reasonCode=$refusedCode; onboard state $($stateJson.Substring(0, [Math]::Min(2000, $stateJson.Length)))")
$assertions.Add(
    'L2-RAR-02', '旅程还没 Blocked 时的补偿清空被服务端拒绝，原因码原样回到自动化面',
    ([int]$refused.status -eq 409 -and $refusedCode -eq 'RECOVERY_DEMAND_NOT_BLOCKED'),
    '409 / RECOVERY_DEMAND_NOT_BLOCKED', "$($refused.status) / $refusedCode")

# --- 4. 引擎恢复，旅程 Blocked，再按一次：补偿走完 --------------------------------------------------------

$null = $riot.Command('Put', "maps/$($Context.MapId)/stations", @{ stations = $fullStations })
$blocked = Wait-L2Condition -Description 'the journey blocked once the engine ran again' -Journal $journal `
    -Criterion 'journey-blocked' -TimeoutSeconds 120 `
    -Probe {
        $journey = Get-FieldJourney -Field $field -JourneyId $journeyId
        "$($journey.Stage) / $($journey.BlockReasonCode)"
    } `
    -Until { param($v) $v -eq 'Blocked / LOAD_RESULT_REQUIRES_RECOVERY' }
$assertions.Add(
    'L2-RAR-03', '引擎恢复之后旅程转 Blocked / LOAD_RESULT_REQUIRES_RECOVERY',
    ($blocked -eq 'Blocked / LOAD_RESULT_REQUIRES_RECOVERY'), 'Blocked / LOAD_RESULT_REQUIRES_RECOVERY', $blocked)

$compensation = $null
$compensationError = $null
try {
    $compensation = Invoke-FieldActCompensate -Field $field -DemandId $load.DemandId -AttemptId $load.AttemptId `
        -Reason 'L2：旅程 Blocked 之后再请求一次' -TimeoutSeconds 180
} catch {
    $compensationError = $_.Exception.Message
    $journal.Note("Second request did not complete: $compensationError")
}
$assertions.Add(
    'L2-RAR-04', '同一个 attempt 被拒过之后再请求，服务端对账 Reconciled、结论 ALL_EMPTY，需求判 Cancelled',
    ($null -ne $compensation -and $compensation.WorkflowState -eq 'Reconciled' -and
        $compensation.Outcome -eq 'ALL_EMPTY' -and $compensation.DemandStatus -eq 'Cancelled'),
    'Reconciled / ALL_EMPTY / Cancelled',
    ($null -ne $compensation) ? "$($compensation.WorkflowState) / $($compensation.Outcome) / $($compensation.DemandStatus)" : "未走完：$compensationError")

# --- 5. 两次请求是两条报文，服务端一次都没掐连接 ---------------------------------------------------------

$requests = Invoke-L2Query -Connection $connection `
    -Sql "SELECT MessageId, FirstResponseJson FROM ProtocolInbox WHERE MessageType = 'ExceptionRecoverySessionRequested' ORDER BY ReceivedAt"
# foreach, not a pipeline: piping Invoke-L2Query rows joins their string columns (README item 22).
# The red runs held one request row and never showed it; -005 held two and threw here.
$answers = @()
$messageIds = @()
foreach ($row in $requests) {
    $first = ([string]$row.FirstResponseJson -split "`n")[0] | ConvertFrom-Json
    $reason = ($first.messageType -eq 'ExceptionRecoverySessionRejected') ? " $($first.payload.problem.reasonCode)" : ''
    $answers += "$($first.messageType)$reason"
    $messageIds += [string]$row.MessageId
}
$distinctIds = @($messageIds | Sort-Object -Unique).Count
$assertions.Add(
    'L2-RAR-05', '两次按下是两条 messageId 不同的会话请求，各自拿到自己的应答：先拒绝、后开出会话',
    ($requests.Count -eq 2 -and $distinctIds -eq 2 -and
        $answers[0] -eq 'ExceptionRecoverySessionRejected RECOVERY_DEMAND_NOT_BLOCKED' -and
        $answers[1] -eq 'ExceptionRecoverySessionOpened'),
    '2 条 / 2 个 id / ExceptionRecoverySessionRejected RECOVERY_DEMAND_NOT_BLOCKED → ExceptionRecoverySessionOpened',
    "$($requests.Count) 条 / $distinctIds 个 id / $($answers -join ' → ')")

$stage = '(not reached)'
if ($null -ne $compensation) {
    # Reconciling the workflow and completing the journey are separate writes (README item 14).
    $stage = Wait-L2Condition -Description 'the journey completed on the compensation' -Journal $journal `
        -Criterion 'journey-completed' -TimeoutSeconds 120 `
        -Probe { [string](Get-FieldJourney -Field $field -JourneyId $journeyId).Stage } -Until { param($v) $v -eq 'Completed' }
}

# Judged after the journey completed, so a dropped connection has had seconds to come back as a
# fresh SessionHello. When the compensation failed, give the vehicle its reconnect before judging:
# -003 read right at the conflict, before the vehicle had redialled, and passed this on a dropped
# connection.
if ($null -eq $compensation) {
    try {
        $null = Wait-L2Condition -Description 'the vehicle reconnected after the refusal' -Journal $journal `
            -Criterion 'vehicle-reconnected' -TimeoutSeconds 20 `
            -Probe { Get-SessionHelloCount } -Until { param($n) [int]$n -gt $hellosBefore }
    } catch {
        $journal.Note("No reconnect within 20 s: $($_.Exception.Message)")
    }
}
$hellosAfter = Get-SessionHelloCount
$assertions.Add(
    'L2-RAR-06', '从第一次按下到收尾，车载端没有重连——服务端没有因为内容冲突掐连接',
    ($hellosAfter -eq $hellosBefore), "SessionHello $hellosBefore 条不变", "SessionHello $hellosBefore → $hellosAfter 条")

$slot = Get-FieldSimulatorSlot -Field $field -SlotNo $load.SlotNo
$reading = "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
$assertions.Add(
    'L2-RAR-07', '单需求旅程以补偿收尾，现场收在安全状态：门关、仓空、已锁、开锁输出复位',
    ($stage -eq 'Completed' -and $reading -eq 'CLOSED/EMPTY/1/0'), 'Completed / CLOSED/EMPTY/1/0', "$stage / $reading")

$journal.Note('Scenario finished.')
