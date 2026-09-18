#Requires -Version 7

<#
G3 `FP-IS-02`：扫码前取消（批次 5，control-server#87）。协议向量 `CV-LOAD-CANCELLATION-BEFORE-LOAD`，
`protocol-v2.0.0` 新增；同一切片的其余向量在 `g3-pickup-load-and-correction`、`g3-load-cancellation`、`g3-sublot-rejected`。

两端的半边：服务端 control-server#83（授权时 `slots` 为空、收到 `ALL_EMPTY` 空结果才终结需求），车载端
onboard-hmi#76（录入请求挂着、本站什么都没下发时给「取消装货」，报不带逐仓结果的 `ALL_EMPTY`）。合成对端的同一条
路径在 `load-cancelled-before-sublot`，这里换成真车载端 WPF 与真模拟器，证的是两端的实现接得上。

形状照向量的四步：`LoadCancellationStartRequested`（不带 attempt）→ `LoadCancellationAuthorization`（`AUTHORIZED`，
`slots` 为空）→ `LoadCancellationResult`（`ALL_EMPTY`，`slotResults` 为空）→ `DurableAck`。

**「没有开过任何仓门」从模拟器读，不从车载端的话里读。**车到站之前记下模拟器上全部仓位的状态，收尾之后再读一遍，
两份必须逐仓相同；车载端对这一笔没有报过任何 `OperationProgress`。

**断言只读服务端的库与模拟器快照。**UI 只驱动；从界面上读的只有「取消装货」在不在、有没有弹出拒绝框。
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
    throw 'This scenario needs the real onboard rig: the operator actions go through its HMI.'
}

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "G3-02B-$($Context.RunId)"

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

function Test-Present([object]$value) { return ($null -ne $value -and [string]$value -ne '') }

function ConvertTo-Instant([object]$value) {
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

# 车载端发来的一类线上消息，按服务端收下的先后；Response／ResponsePayload 是服务端对它的第一份应答。
function Get-Inbound([string]$messageType) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, RequestJson, FirstResponseJson, ReceivedAt FROM ProtocolInbox " +
        "WHERE MessageType = '$messageType' ORDER BY ReceivedAt, MessageId")
    $messages = foreach ($row in $rows) {
        $response = if (Test-Present $row.FirstResponseJson) {
            ([string]$row.FirstResponseJson -split "`n")[0] | ConvertFrom-Json
        } else { $null }
        [pscustomobject]@{
            MessageId       = [string]$row.MessageId
            At              = ConvertTo-Instant $row.ReceivedAt
            Payload         = ([string]$row.RequestJson | ConvertFrom-Json).payload
            Response        = if ($null -ne $response) { [string]$response.messageType } else { '' }
            ResponsePayload = if ($null -ne $response -and $response.PSObject.Properties['payload']) { $response.payload } else { $null }
        }
    }
    return , @($messages)
}

# 模拟器上每个仓的门／货／锁反馈／开锁输出，按仓号。
function Get-AllSlotStates {
    return (@($simulator.Snapshot().slots | Sort-Object { [int]$_.slotNo } | ForEach-Object {
                "$($_.slotNo)=$($_.doorState)/$($_.cargoState)/$($_.lockFeedbackRaw)/$($_.unlockOutputRaw)"
            }) -join ' ')
}

function Add-NotReached([string[]]$ids, [string]$why) {
    foreach ($id in $ids) {
        $assertions.Add($id, "未到达：$why", $false, '(reached)', "(not reached) $why")
    }
}

# --- 1. 需求受理、车到取货点，停在等录入 ------------------------------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot, 4 boxes).")
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

$intent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'"
        if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
    } `
    -Until { param($v) $null -ne $v }

$slotsBefore = Get-AllSlotStates
$journal.Note("Simulator slots before arrival: $slotsBefore")

$journal.Note('Vehicle departs for the pickup station.')
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $intent.OrderId
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
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })

$waiting = Wait-L2Condition -Description 'the stop is waiting for a sublot' `
    -Journal $journal -Criterion 'awaiting-sublot' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT Stage, SublotRequestMessageId FROM JourneyRuntimes WHERE DemandId = '$demandId'"
        if ($rows.Count -eq 1 -and [string]$rows[0].Stage -eq 'AwaitingSublot') { $rows[0] } else { $null }
    } `
    -Until { param($v) $null -ne $v }
# 车载端录入框可用，说明录入请求已经到了车上、挂着没人回答——这正是扫码前取消的前提。
$null = Wait-L2Condition -Description 'the onboard HMI holds the entry request open' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 60 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }

# --- 2. 操作员不扫码，经 UIA 按「取消装货」并确认 --------------------------------------------------

$laterIds = @('G3-02-32', 'G3-02-33', 'G3-02-34', 'G3-02-35', 'G3-02-36')
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
while ($true) {
    $offered = [bool]$onboard.ButtonAvailable('取消装货')
    $journal.Observe('onboard-cancellation-before-sublot-entry', $offered, $null)
    if ($offered -or [DateTimeOffset]::UtcNow -ge $deadline) { break }
    Start-Sleep -Milliseconds 500
}
$assertions.Add(
    'G3-02-31',
    '车到站、录入请求挂着、还没录入任何子批时，车载端界面上出现可用的「取消装货」入口（不依赖恢复开关）',
    $offered, $true, $offered)
if (-not $offered) {
    Add-NotReached $laterIds '车载端没有给出扫码前的「取消装货」入口'
    return
}

$journal.Note('Operator finds nothing to load, presses 取消装货 and confirms.')
$onboard.InvokeButton('取消装货')
$null = $onboard.Confirm('取消装货')

$request = Wait-L2Condition -Description 'the server received LoadCancellationStartRequested, or the onboard refused it' `
    -Journal $journal -Criterion 'cancellation-requested' -TimeoutSeconds 60 `
    -Probe {
        $received = @((Get-Inbound 'LoadCancellationStartRequested') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
        if ($received.Count -ge 1) { $received[0] }
        elseif (@($onboard.WindowTitles()) -contains '取消装货失败') { 'REFUSED_BY_ONBOARD' }
        else { $null }
    } `
    -Until { param($v) $null -ne $v }
if ($request -is [string]) {
    Add-NotReached $laterIds '车载端没有发出取消请求（弹出「取消装货失败」）'
    return
}
$cancellationId = [string]$request.Payload.cancellationId

$cancellationResult = Wait-L2Condition -Description 'the server received the LoadCancellationResult, or the onboard gave up' `
    -Journal $journal -Criterion 'cancellation-result' -TimeoutSeconds 90 `
    -Probe {
        $received = @((Get-Inbound 'LoadCancellationResult') | Where-Object { [string]$_.Payload.cancellationId -eq $cancellationId })
        if ($received.Count -ge 1) { $received[0] }
        elseif (@($onboard.WindowTitles()) -contains '取消装货失败') { 'REFUSED' }
        else { $null }
    } `
    -Until { param($v) $null -ne $v }

$authorization = $request.ResponsePayload
$authorizedSlots = if ($null -ne $authorization -and $authorization.PSObject.Properties['slots']) { @($authorization.slots) } else { @() }
$authorizationText = if ($null -ne $authorization) {
    "$($request.Response) $([string]$authorization.decision) slots=[$($authorizedSlots -join ',')] attempt=$([string]$request.Payload.slotOperationAttemptId)"
} else { "$($request.Response) (no payload)" }
$assertions.Add(
    'G3-02-32',
    '取消经服务端授权且不带仓位操作：请求不带 slotOperationAttemptId，应答是 LoadCancellationAuthorization AUTHORIZED，指向这条需求、这次取消，slots 为空（AUTHORIZE_CANCELLATION_WITHOUT_SLOT_OPERATION）',
    ($request.Response -eq 'LoadCancellationAuthorization' -and $null -ne $authorization -and
        [string]$authorization.decision -eq 'AUTHORIZED' -and [string]$authorization.cancellationId -eq $cancellationId -and
        [string]$authorization.demandId -eq $demandId -and $authorizedSlots.Count -eq 0 -and
        -not (Test-Present $request.Payload.slotOperationAttemptId)),
    'LoadCancellationAuthorization AUTHORIZED slots=[] attempt=',
    $authorizationText)
if ($cancellationResult -is [string]) {
    Add-NotReached @('G3-02-33', 'G3-02-34', 'G3-02-35', 'G3-02-36') '车载端没有报出取消结果（弹出「取消装货失败」）'
    return
}

$ended = Wait-L2Condition -Description 'the stop was ended by the cancellation' `
    -Journal $journal -Criterion 'stop-ended' -TimeoutSeconds 60 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'Completed' }
# 与旅程转 Completed 同一次提交之外，车辆租约与看板投影可能晚一轮；让运行时再转几轮再读终态。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

$requests = @((Get-Inbound 'LoadCancellationStartRequested') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
$results = @((Get-Inbound 'LoadCancellationResult') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
$assertions.Add(
    'G3-02-33',
    '消息顺序与向量一致：LoadCancellationStartRequested → LoadCancellationAuthorization → LoadCancellationResult → DurableAck，各一次；车载端没有在授权之前单方面报结果（CV-LOAD-CANCELLATION-BEFORE-LOAD orderedExpectedMessages / NEVER_CANCEL_UNILATERALLY）',
    ($requests.Count -eq 1 -and $results.Count -eq 1 -and $requests[0].Response -eq 'LoadCancellationAuthorization' -and
        $requests[0].At -lt $results[0].At -and $results[0].Response -eq 'DurableAck'),
    'StartRequested → Authorization < Result → DurableAck，各 1',
    ("StartRequested×$($requests.Count)$(if ($requests.Count -ge 1) { "→$($requests[0].Response)" }) / " +
     "Result×$($results.Count)$(if ($results.Count -ge 1) { "→$($results[0].Response)" }) / " +
     "有序=$(if ($requests.Count -ge 1 -and $results.Count -ge 1) { $requests[0].At -lt $results[0].At } else { '(incomplete)' })"))

$slotResults = @($cancellationResult.Payload.slotResults)
$progress = Get-Count "SELECT COUNT(*) AS Total FROM ProtocolInbox WHERE MessageType = 'OperationProgress'"
$slotsAfter = Get-AllSlotStates
$assertions.Add(
    'G3-02-34',
    '车载端报 ALL_EMPTY 且不碰仓位 IO：结果不带逐仓条目，车载端没有报过任何 OperationProgress，模拟器上每个仓的门、货、锁反馈、开锁输出与到站前逐仓相同（REPORT_ALL_EMPTY_WITHOUT_SLOT_IO；forbidden: duplicate-slot-unlock、expanded-active-unlock-set）',
    ([string]$cancellationResult.Payload.overallOutcome -eq 'ALL_EMPTY' -and $slotResults.Count -eq 0 -and
        $progress -eq 0 -and $slotsAfter -eq $slotsBefore),
    "ALL_EMPTY / 0 条逐仓结果 / 0 条进度 / $slotsBefore",
    "$([string]$cancellationResult.Payload.overallOutcome) / $($slotResults.Count) 条逐仓结果 / $progress 条进度 / $slotsAfter")

$workflow = @(Invoke-L2Query -Connection $connection `
        -Sql "SELECT WorkflowType, State, SlotOperationAttemptId, SlotsJson, ResultMessageId FROM RecoveryWorkflows WHERE WorkflowId = '$cancellationId'")
$lease = @(Invoke-L2Query -Connection $connection -Sql "SELECT ReleasedAt FROM VehicleDispatchLeases WHERE DemandId = '$demandId'")
$releasedAt = if ($lease.Count -eq 1 -and (Test-Present $lease[0].ReleasedAt)) { ConvertTo-Instant $lease[0].ReleasedAt } else { $null }
$demandStatus = Get-Scalar "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
$blockReason = Get-Scalar "SELECT BlockReasonCode AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'"
$workflowText = if ($workflow.Count -eq 1) {
    "$($workflow[0].WorkflowType) / attempt=$($workflow[0].SlotOperationAttemptId) / $($workflow[0].SlotsJson) / 收下 $($workflow[0].ResultMessageId) / $($workflow[0].State)"
} else { '(no workflow row)' }
$assertions.Add(
    'G3-02-35',
    '只在收到 ALL_EMPTY 结果之后终结：取消工作流无 attempt、仓集合为空、收下的正是车报的那条结果并已收敛；需求 Cancelled、旅程以 CANCELLED_BY_OPERATOR 收尾，车辆租约的释放时刻不早于服务端收到结果（TERMINATE_ONLY_ON_ALL_EMPTY_RESULT）',
    ($workflow.Count -eq 1 -and [string]$workflow[0].WorkflowType -eq 'LOAD_CANCELLATION' -and
        -not (Test-Present $workflow[0].SlotOperationAttemptId) -and [string]$workflow[0].SlotsJson -eq '[]' -and
        [string]$workflow[0].ResultMessageId -eq $results[0].MessageId -and [string]$workflow[0].State -eq 'Reconciled' -and
        $demandStatus -eq 'Cancelled' -and $ended -eq 'Completed' -and $blockReason -eq 'CANCELLED_BY_OPERATOR' -and
        $null -ne $releasedAt -and $releasedAt -ge $results[0].At),
    "LOAD_CANCELLATION / attempt= / [] / 收下 $($results[0].MessageId) / Reconciled；Cancelled / Completed/CANCELLED_BY_OPERATOR / 释放 >= $($results[0].At.ToString('O'))",
    "$workflowText；$demandStatus / $ended/$blockReason / 释放 $(if ($null -ne $releasedAt) { $releasedAt.ToString('O') } else { '(not released)' })")

$operations = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId'"
$commands = Get-Count "SELECT COUNT(*) AS Total FROM ProtocolOutbox WHERE MessageType = 'SlotOperationCommand'"
$entryRequest = @(Invoke-L2Query -Connection $connection `
        -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$($waiting.SublotRequestMessageId)'")
$submissions = Get-Count "SELECT COUNT(*) AS Total FROM ProtocolInbox WHERE MessageType = 'SublotSubmitted'"
$toGate = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'"
$recoveryRequired = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE Status = 'RecoveryRequired'"
$orders = @($riot.Snapshot().body.orders)
$entrySettled = $entryRequest.Count -eq 1 -and (Test-Present $entryRequest[0].AcknowledgedAt)
$assertions.Add(
    'G3-02-36',
    '终态没有仓位命令、没有动过的门：这条需求 0 笔仓位操作、全程 0 条 SlotOperationCommand、0 条录入提交，那条没人回答的录入请求已结算，没有去关卡的单，RIoT 上只有那一张取货单，没有 RecoveryRequired（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE；forbidden: duplicate-riot-order、duplicate-business-commit）',
    ($operations -eq 0 -and $commands -eq 0 -and $submissions -eq 0 -and $entrySettled -and $toGate -eq 0 -and
        $orders.Count -eq 1 -and $recoveryRequired -eq 0),
    '操作 0 / 命令 0 / 提交 0 / 录入请求已结算 / TO_GATE 0 / RIoT 单 1 / RecoveryRequired 0',
    "操作 $operations / 命令 $commands / 提交 $submissions / 录入请求已结算=$entrySettled / TO_GATE $toGate / RIoT 单 $($orders.Count) / RecoveryRequired $recoveryRequired")

$journal.Note('FP-IS-02: cancelled before any sublot entry -- authorized without slots, ALL_EMPTY reported without slot IO, terminated on the result.')
