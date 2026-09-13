#Requires -Version 7

<#
G3 `FP-IS-02` 的第二条场景：装载进行中由操作员取消，全部仓位证空。协议向量 `CV-LOAD-CANCELLATION-ALL-EMPTY`；
同一切片的另外两条向量在 `g3-pickup-load-and-correction`。

**取消的时机是查代码定下来的。**两端各有一道门：
- 车载端只在「有一笔未结算的装载」时给「取消装货」按钮（`CanRequestLoadCancellation` →
  `HasRecoveryVectorOrLoadOperation`），装载一旦记下结果就没了；
- 服务端只在需求仍 `Accepted`、装载不是 `RecoveryRequired` 时授权（`AuthorizeLoadCancellationAsync`）。装载失败
  之后再取消会被拒，L1 `FailedCompensationResultIsDurableReplayableAndNeverReleasesDemandOrVehicle` 证的就是这条。
两道门同时开着的只有「装载命令已下、结果还没出」这一段，也就是操作员站在开着的仓门前的时候。车载端的确认框
要操作员确认「目标仓门已锁好」，所以操作员先把空仓门带上，再点取消。

**两仓。**车载端逐仓开锁，第一仓开着时操作员决定不装，第二仓始终没开过。取消要证「全部授权仓位为空」，两仓
才证得出没开过的那一仓也被算进去了，而且没有为了证空再去开它。

**取消收尾后场景不停。**原装载的执行器还在等第一仓放货，要等满车载端的操作超时（120s）才交出结果，那份结果
在取消已经收敛之后才到服务端。收敛之后的状态经不经得起这份迟到的结果，正是向量 `finalState`
（`NO_DUPLICATE_COMMIT` / `NO_UNPROVEN_STATE`）要的东西，所以等它到了、服务端再转几轮，才下终态判据。

**断言只读服务端的库与模拟器快照。**UI 只驱动。
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
$sublot = "G3-02C-$($Context.RunId)"
$maxBoxCount = 8

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

# 车载端发来的一类线上消息，按服务端收下的先后。Response 是服务端第一份应答的消息类型，ResponsePayload 是它的
# 载荷（LoadCancellationStartRequested 的应答就是授权本身）。
function Get-Inbound([string]$messageType) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, RequestJson, FirstResponseJson, ReceivedAt FROM ProtocolInbox " +
        "WHERE MessageType = '$messageType' ORDER BY ReceivedAt, MessageId")
    $messages = foreach ($row in $rows) {
        # 应答可以是多行：OperationResult 与 SafetyStateChanged 的应答是 DurableAck 再跟一行 SessionReadiness。
        # 第一行才是对这条消息本身的应答。
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

function Get-Progress([string]$attemptId) {
    $all = Get-Inbound 'OperationProgress'
    $mine = foreach ($message in $all) {
        if ([string]$message.Payload.slotOperationAttemptId -ne $attemptId) { continue }
        [pscustomobject]@{
            At     = $message.At
            Phase  = [string]$message.Payload.phase
            Active = @($message.Payload.activeUnlockSlots | ForEach-Object { [int]$_ })
        }
    }
    return , @($mine)
}

function Get-SlotState([int]$slotNo) {
    $slot = @($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $slotNo })[0]
    return "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
}

function Format-Slots([object[]]$slots) { return (@($slots | ForEach-Object { [int]$_ } | Sort-Object) -join ',') }

function Wait-Offered([string]$buttonName, [string]$criterion, [int]$timeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($timeoutSeconds)
    while ($true) {
        $available = [bool]$onboard.ButtonAvailable($buttonName)
        $journal.Observe($criterion, $available, $null)
        if ($available -or [DateTimeOffset]::UtcNow -ge $deadline) { return $available }
        Start-Sleep -Milliseconds 500
    }
}

function Add-NotReached([string[]]$ids, [string]$why) {
    foreach ($id in $ids) {
        $assertions.Add($id, "未到达：$why", $false, '(reached)', "(not reached) $why")
    }
}

# --- 1. 需求受理、车到取货点、UIA 录入 sublot ------------------------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot, $maxBoxCount boxes).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = $maxBoxCount
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

# --- 2. 装载开始，第一仓开着时操作员决定不装，把空仓门带上 --------------------------------------------

$attemptId = Wait-L2Condition -Description 'the server issued the load command' `
    -Journal $journal -Criterion 'load-attempt' -TimeoutSeconds 180 `
    -Probe { Get-Scalar "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'" } `
    -Until { param($v) $v }
$targetSlots = @([string](Get-Scalar "SELECT TargetSlotsJson AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'") |
    ConvertFrom-Json | ForEach-Object { [int]$_ })
$journal.Note("Load $attemptId targets slots $($targetSlots -join ', ').")

$waiting = Wait-L2Condition -Description 'the onboard is waiting for the operator on the first slot' `
    -Journal $journal -Criterion 'load-waiting-operator-1' -TimeoutSeconds 120 `
    -Probe { @((Get-Progress $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })[0] } `
    -Until { param($v) $null -ne $v }
if ($waiting.Active.Count -ne 1) {
    throw "The onboard reported WAITING_OPERATOR on $($waiting.Active.Count) slots at once; the executor walks one slot at a time."
}
$openedSlot = [int]$waiting.Active[0]
$journal.Note("Slot $openedSlot is open ($(Get-SlotState $openedSlot)); the operator decides not to load and closes it empty.")
$null = $simulator.Command('Post', "slots/$openedSlot/close-door", @{})
$null = Wait-L2Condition -Description "slot $openedSlot is closed, locked and empty" `
    -Journal $journal -Criterion 'opened-slot-closed-empty' -TimeoutSeconds 30 `
    -Probe { Get-SlotState $openedSlot } -Until { param($v) $v -eq 'CLOSED/EMPTY/1/0' }

# --- 3. UIA 代替操作员点「取消装货」 ------------------------------------------------------------------

$cancellationIds = @('G3-02-22', 'G3-02-23', 'G3-02-24', 'G3-02-25', 'G3-02-26', 'G3-02-27')
$offered = Wait-Offered '取消装货' 'onboard-cancellation-entry' 60
$assertions.Add(
    'G3-02-21',
    '装载进行中（命令已下、结果未出），车载端界面上出现可用的「取消装货」入口',
    $offered, $true, $offered)
if (-not $offered) {
    Add-NotReached $cancellationIds '车载端没有给出「取消装货」入口'
    $journal.Note('Scenario finished at the missing load cancellation entry.')
    return
}

$journal.Note('Operator presses 取消装货 and confirms.')
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
    Add-NotReached $cancellationIds '车载端没有发出取消请求（弹出「取消装货失败」）'
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
$authorizationText = if ($null -ne $authorization) {
    "$($request.Response) $([string]$authorization.decision) $(Format-Slots $authorization.slots)"
} else { "$($request.Response) (no payload)" }
if ($cancellationResult -is [string]) {
    $assertions.Add(
        'G3-02-22',
        '取消经服务端显式授权，范围就是这笔装载的全部授权仓位',
        $false, "LoadCancellationAuthorization AUTHORIZED $(Format-Slots $targetSlots)", $authorizationText)
    Add-NotReached @('G3-02-23', 'G3-02-24', 'G3-02-25', 'G3-02-26', 'G3-02-27') '取消没有执行（车载端弹出「取消装货失败」）'
    return
}

$workflowState = Wait-L2Condition -Description 'the cancellation workflow settled' `
    -Journal $journal -Criterion 'cancellation-workflow' -TimeoutSeconds 30 `
    -Probe { Get-Scalar "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$cancellationId'" } `
    -Until { param($v) $v -in @('Reconciled', 'RecoveryRequired') }

$assertions.Add(
    'G3-02-22',
    '取消经服务端显式授权：应答是 LoadCancellationAuthorization AUTHORIZED，指向这条需求、这笔装载，范围就是全部授权仓位（AUTHORIZE_CANCELLATION_EXPLICITLY）',
    ($request.Response -eq 'LoadCancellationAuthorization' -and $null -ne $authorization -and
        [string]$authorization.decision -eq 'AUTHORIZED' -and [string]$authorization.cancellationId -eq $cancellationId -and
        [string]$authorization.demandId -eq $demandId -and [string]$authorization.slotOperationAttemptId -eq $attemptId -and
        (Format-Slots $authorization.slots) -eq (Format-Slots $targetSlots)),
    "LoadCancellationAuthorization AUTHORIZED $(Format-Slots $targetSlots)",
    $authorizationText)

$requests = @((Get-Inbound 'LoadCancellationStartRequested') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
$results = @((Get-Inbound 'LoadCancellationResult') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
$assertions.Add(
    'G3-02-23',
    '消息顺序与向量一致：LoadCancellationStartRequested → LoadCancellationAuthorization → LoadCancellationResult → DurableAck，各一次；车载端没有在授权之前单方面报取消结果（CV-LOAD-CANCELLATION-ALL-EMPTY orderedExpectedMessages / NEVER_CANCEL_UNILATERALLY）',
    ($requests.Count -eq 1 -and $results.Count -eq 1 -and $requests[0].Response -eq 'LoadCancellationAuthorization' -and
        $requests[0].At -lt $results[0].At -and $results[0].Response -eq 'DurableAck'),
    'StartRequested → Authorization < Result → DurableAck，各 1',
    ("StartRequested×$($requests.Count)$(if ($requests.Count -ge 1) { "→$($requests[0].Response)" }) / " +
     "Result×$($results.Count)$(if ($results.Count -ge 1) { "→$($results[0].Response)" }) / " +
     "有序=$(if ($requests.Count -ge 1 -and $results.Count -ge 1) { $requests[0].At -lt $results[0].At } else { '(incomplete)' })"))

$progressAfterRequest = @((Get-Progress $attemptId) | Where-Object { $_.At -gt $request.At })
$unlocksAfterRequest = @($progressAfterRequest | Where-Object { $_.Phase -eq 'UNLOCKING' })
$allUnlocks = @((Get-Progress $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' } | ForEach-Object { $_.Active } | ForEach-Object { [int]$_ })
$assertions.Add(
    'G3-02-24',
    "证空不靠开门：取消请求之后车载端没有再开任何仓；整个 attempt 只开过装载时的第 $openedSlot 仓一次，没开过的仓始终没开（forbidden: duplicate-slot-unlock、expanded-active-unlock-set）",
    ($unlocksAfterRequest.Count -eq 0 -and ($allUnlocks -join ',') -eq "$openedSlot"),
    "取消后开锁 0 次 / 全程开锁 $openedSlot",
    "取消后开锁 $($unlocksAfterRequest.Count) 次 / 全程开锁 $($allUnlocks -join ',')")

$slotResults = @($cancellationResult.Payload.slotResults)
$slotResultText = ($slotResults | Sort-Object { [int]$_.slotNo } | ForEach-Object {
    "$($_.slotNo)=$($_.outcome)/$($_.finalPhysicalState)/$($_.lockState)/$($_.unlockOutputState)"
}) -join ' '
$expectedSlotResults = ($targetSlots | Sort-Object | ForEach-Object { "$_=COMPLETED/EMPTY/LOCKED/RESET" }) -join ' '
$physicalAtCancellation = ($targetSlots | Sort-Object | ForEach-Object { "$_=$(Get-SlotState $_)" }) -join ' '
$assertions.Add(
    'G3-02-25',
    '车载端证明全部授权仓位为空：ALL_EMPTY，每个仓都是空、锁上、开锁输出复位；模拟器上的物理状态与之一致（PROVE_ALL_SLOTS_EMPTY）',
    ([string]$cancellationResult.Payload.overallOutcome -eq 'ALL_EMPTY' -and $slotResultText -eq $expectedSlotResults -and
        $physicalAtCancellation -eq (($targetSlots | Sort-Object | ForEach-Object { "$_=CLOSED/EMPTY/1/0" }) -join ' ')),
    "ALL_EMPTY $expectedSlotResults / $(($targetSlots | Sort-Object | ForEach-Object { "$_=CLOSED/EMPTY/1/0" }) -join ' ')",
    "$([string]$cancellationResult.Payload.overallOutcome) $slotResultText / $physicalAtCancellation")

function Get-Settlement {
    [pscustomobject]@{
        Workflow  = Get-Scalar "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$cancellationId'"
        Demand    = Get-Scalar "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
        Operation = Get-Scalar "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
        Released  = Test-Present (Get-Scalar "SELECT ReleasedAt AS Value FROM VehicleDispatchLeases WHERE DemandId = '$demandId'")
        Journey   = "$(Get-Stage)/$(Get-Scalar "SELECT BlockReasonCode AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")"
        ToGate    = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'"
    }
}

function Format-Settlement([object]$s) {
    return "工作流 $($s.Workflow) / 需求 $($s.Demand) / 装载 $($s.Operation) / 租约释放=$($s.Released) / 旅程 $($s.Journey) / TO_GATE $($s.ToGate)"
}

function Test-Settled([object]$s) {
    return ($s.Workflow -eq 'Reconciled' -and $s.Demand -eq 'Cancelled' -and $s.Operation -eq 'Cancelled' -and
        $s.Released -and $s.Journey -eq 'Completed/CANCELLED_BY_OPERATOR' -and $s.ToGate -eq 0)
}

$expectedSettlement = '工作流 Reconciled / 需求 Cancelled / 装载 Cancelled / 租约释放=True / 旅程 Completed/CANCELLED_BY_OPERATOR / TO_GATE 0'
$atCancellation = Get-Settlement
$assertions.Add(
    'G3-02-26',
    '取消收敛：工作流 Reconciled，需求 Cancelled，装载 Cancelled，车辆租约释放，旅程以 CANCELLED_BY_OPERATOR 收尾、没有去关卡（RECONCILE_EMPTY_FINAL_STATE）',
    (Test-Settled $atCancellation),
    $expectedSettlement,
    (Format-Settlement $atCancellation))

# --- 4. 原装载的迟到结果 -----------------------------------------------------------------------------

# 不用 Wait-L2Condition：车载端若在取消时就收掉了原装载，结果根本不会来，那是更好的行为而不是故障。等满
# 车载端操作超时再加余量，来了就记下，没来也记下。
$journal.Note('Waiting for the original load executor to give up (onboard operation timeout, about 120s).')
$lateDeadline = [DateTimeOffset]::UtcNow.AddSeconds(240)
$lateResults = @()
while ([DateTimeOffset]::UtcNow -lt $lateDeadline) {
    $lateResults = @((Get-Inbound 'OperationResult') | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $attemptId })
    $journal.Observe('late-load-result', $lateResults.Count, $null)
    if ($lateResults.Count -ge 1) { break }
    Start-Sleep -Seconds 1
}
$lateText = if ($lateResults.Count -ge 1) {
    "迟到结果 $([string]$lateResults[0].Payload.overallOutcome)→$($lateResults[0].Response)"
} else { '240s 内没有迟到结果' }
$journal.Note("Original load: $lateText.")

# 让旅程运行时在这份结果之后再转几轮，再判「什么都没有被改回去」。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

$final = Get-Settlement
$recoveryRequired = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId' AND Status = 'RecoveryRequired'"
$completions = Get-Count "SELECT COUNT(*) AS Total FROM TransportDemandCompletions WHERE DemandId = '$demandId'"
$orders = @($riot.Snapshot().body.orders)
$finalPhysical = ($targetSlots | Sort-Object | ForEach-Object { "$_=$(Get-SlotState $_)" }) -join ' '
$assertions.Add(
    'G3-02-27',
    '终态经得起原装载的迟到结果：取消的收敛一项都没被改回去，没有任何操作落到 RecoveryRequired、没有需求完成记录，RIoT 上只有那一张取货单，仓位物理上全空、锁上（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE；forbidden: duplicate-business-commit、unknown-as-success）',
    ((Test-Settled $final) -and $recoveryRequired -eq 0 -and $completions -eq 0 -and $orders.Count -eq 1 -and
        $finalPhysical -eq (($targetSlots | Sort-Object | ForEach-Object { "$_=CLOSED/EMPTY/1/0" }) -join ' ')),
    "$expectedSettlement / RecoveryRequired 0 / 完成记录 0 / RIoT 单 1 / $(($targetSlots | Sort-Object | ForEach-Object { "$_=CLOSED/EMPTY/1/0" }) -join ' ')",
    "$(Format-Settlement $final) / RecoveryRequired $recoveryRequired / 完成记录 $completions / RIoT 单 $($orders.Count) / $finalPhysical / $lateText")

$journal.Note('FP-IS-02: load cancelled mid-operation with explicit authorization, every slot proven empty, final state checked after the late load result.')
