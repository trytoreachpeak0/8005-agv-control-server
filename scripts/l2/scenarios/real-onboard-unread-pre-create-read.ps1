#Requires -Version 7

<#
control-server#375 在真车载端下的两格：取货腿、关卡腿各有一次建单前的对账读没读到，之后补建，各只建一张。

修之前，建单前那一次读只要回的是非精确的 Unknown（SDK 超时、503），意图就被标成 RESULT_UNKNOWN，
此后 RIoT 明确答「没有这张单」也永不建单，旅程停在那一段腿上，现场只能改库。修之后，从没发出过的意图在 RIoT
答「没有」时照常建；而补建发生在离站核验（取货腿是受理时的派车准入）之后若干轮，所以补建前要重看一遍车况与建单门禁，
车况那一组判据里有车载端报上来的离站摘要——**这正是合成对端证明不了的部分**：合成对端永远报安全，
真车载端的会话与安全摘要是它自己按 IO 判出来的。关卡腿那一段更要紧：补建时车上有货，摘要要说目标仓全锁、
开锁输出全复位、没有未知项。

注入只打一处：假 RIoT 的 `PUT /faults/absent-order-reads`，让接下来一次「按 upperId 读一张不存在的单」
回 503。它不碰车辆读取、站点目录、订单列表，也不按时间生效，所以打中的是哪一次读由假 RIoT 自己记下
（`absentOrderReadFaults.failedUpperIds`）。判据先等它记下、再核它打中的就是那一段腿的单——不在意图行一出现
就读快照，那一刻那次读可能还没发生（审查 S6）。

判据只从服务端 SQLite 与假 RIoT `/snapshot` 读。
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

function Get-Intent([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql ("SELECT MovementLegId, UpperId, OrderId, Status, CreateAttemptCount FROM OrderIntents " +
              "WHERE DemandId = '$demandId' AND Purpose = '$purpose'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-AuditChain([string]$movementLegId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql ("SELECT Sequence, Phase, Outcome, ReceiptClassification, FailureCategory, AttemptId FROM RiotDispatchAuditEvents " +
              "WHERE MovementLegId = '$movementLegId' ORDER BY Sequence")
    return @(foreach ($row in $rows) {
        "$([string]$row.Phase)/$([string]$row.Outcome)/$([string]$row.ReceiptClassification)/$([string]$row.FailureCategory)"
    })
}

function Get-Stage {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

# The upperIds the armed fault has failed so far, joined: a string, so Wait-L2Condition's probe never hands back an array.
function Get-FailedReads { return (@($riot.Snapshot().body.absentOrderReadFaults.failedUpperIds) -join ',') }

function Get-Slot([int]$slotNo) {
    return $simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $slotNo }
}

function Get-AttemptId([string]$operationType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT SlotOperationAttemptId FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = '$operationType'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].SlotOperationAttemptId
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

# 同 real-onboard-normal-load：等车载端自己报「在等操作员」（锁反馈稳定、开锁输出复位之后才发），再摆货关门。
function Invoke-SlotOperation([string]$operationType, [string]$cargoState) {
    $attemptId = Wait-L2Condition -Description "the server issued the $operationType command" `
        -Journal $journal -Criterion "$operationType-attempt" -TimeoutSeconds 180 `
        -Probe { Get-AttemptId $operationType } -Until { param($v) $v }
    $unlocking = Wait-L2Condition -Description "the onboard started unlocking for the $operationType" `
        -Journal $journal -Criterion "$operationType-unlocking" -TimeoutSeconds 120 `
        -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'UNLOCKING' })[0] } `
        -Until { param($v) $v }
    $slots = @($unlocking.activeUnlockSlots)
    if ($slots.Count -ne 1) {
        throw ("This scenario drives one slot per operation; the $operationType command targets " +
            "$($slots.Count) ($($slots -join ', ')).")
    }
    $slotNo = [int]$slots[0]
    $null = Wait-L2Condition -Description "the onboard is waiting for the operator on slot $slotNo" `
        -Journal $journal -Criterion "$operationType-waiting-operator" -TimeoutSeconds 120 `
        -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'WAITING_OPERATOR' }).Count } `
        -Until { param($v) $v -ge 1 }
    $journal.Note("Onboard is waiting on slot $slotNo; setting cargo to $cargoState.")
    $null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = $cargoState })
    $null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})
    return $slotNo
}

# 一段腿的三条判据：注入打中的正是这段腿的单；补建后确认、建单计数 1；审计链先读没读到、再读答没有、才 arm，且只 arm 一次。
function Assert-RetriedLeg([string]$purpose, [int]$failedIndex, [string]$idPrefix) {
    $failedJoined = Wait-L2Condition -Description "the armed read fault hit a read of the $purpose order" `
        -Journal $journal -Criterion "$purpose-read-fault-hit" -TimeoutSeconds 120 `
        -Probe { Get-FailedReads } -Until { param($v) $v -and @($v -split ',').Count -ge ($failedIndex + 1) }
    $failed = @($failedJoined -split ',')
    $intent = Wait-L2Condition -Description "the $purpose intent was written" `
        -Journal $journal -Criterion "$purpose-intent-written" -TimeoutSeconds 60 `
        -Probe { Get-Intent $purpose } -Until { param($v) $v }
    $remaining = [int]$riot.Snapshot().body.absentOrderReadFaults.remaining
    $journal.Note("Absent-order read faults: remaining $remaining, failed [$failedJoined].")
    $assertions.Add(
        "$idPrefix-A", "注入只打中一次，打中的是 $purpose 那张单的读",
        ($failed.Count -eq ($failedIndex + 1) -and $failed[$failedIndex] -eq [string]$intent.UpperId -and $remaining -eq 0),
        "[$failedIndex] $($intent.UpperId) / remaining 0", "[$failedJoined] / remaining $remaining")

    $status = Wait-L2Condition -Description "the $purpose intent was created after the unread read and confirmed" `
        -Journal $journal -Criterion "$purpose-confirmed" -TimeoutSeconds 120 `
        -Probe { $row = Get-Intent $purpose; if ($row) { [string]$row.Status } else { $null } } `
        -Until { param($v) $v -eq 'CONFIRMED' }
    $intent = Get-Intent $purpose
    $chain = Get-AuditChain ([string]$intent.MovementLegId)
    $journal.Note("$purpose audit chain: $($chain -join ' -> ')")
    $assertions.Add(
        "$idPrefix-B", "读没读到之后，$purpose 照常建出并确认，建单计数恰好是 1",
        ($status -eq 'CONFIRMED' -and [int]$intent.CreateAttemptCount -eq 1),
        'CONFIRMED / 1', "$status / $($intent.CreateAttemptCount)")

    # 第二次读只可能来自到站等待阶段的 EnsureMovementConfirmedAsync（第一次建单尝试那一轮只调一次派车），而那条路径
    # 对从没发出过的意图先过车况检查与建单门禁，不过就不读单。所以 NOT_FOUND 在链上，说明那一轮两道检查都放行了（推的）。
    $firstThree = @($chain | Select-Object -First 3)
    $assertions.Add(
        "$idPrefix-C", "$purpose 审计链：建单前读没读到 → 建单前读答没有 → 才 arm 建单；只 arm 一次",
        ($firstThree.Count -eq 3 -and
            $firstThree[0] -like 'PRE_CREATE_RECONCILIATION/UNKNOWN/SdkFailure/*' -and
            $firstThree[1] -like 'PRE_CREATE_RECONCILIATION/NOT_FOUND/*' -and
            $firstThree[2] -like 'CREATE_DISPATCH/ARMED/*' -and
            @($chain | Where-Object { $_ -like 'CREATE_DISPATCH/*' }).Count -eq 1),
        'PRE/UNKNOWN/SdkFailure -> PRE/NOT_FOUND -> CREATE_DISPATCH/ARMED (x1)', ($chain -join ' -> '))
    return $intent
}

function Invoke-Arrival([object]$intent, [int]$stationRiotId) {
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $stationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })
}

# --- 1. 取货腿：只让下一次「读一张不存在的单」失败，然后发需求 ---------------------------------------------

$armed = $riot.Command('Put', 'faults/absent-order-reads', @{ count = 1 })
$journal.Note("Armed one failing read of an absent order: remaining $($armed.body.absentOrderReadFaults.remaining).")
$journal.Note("Publishing demand $demandIdWire (sublot $sublot) to the fake MesIngest catalog.")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$pickup = Assert-RetriedLeg -purpose 'TO_PICKUP' -failedIndex 0 -idPrefix 'L2-UR-01'

$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-UR-02', 'RIoT 上只有一张单，就是取货腿那一张',
    ($riotOrders.Count -eq 1 -and [string]$riotOrders[0].upperId -eq [string]$pickup.UpperId),
    "1 / $($pickup.UpperId)", "$($riotOrders.Count) / $(($riotOrders | ForEach-Object { $_.upperId }) -join ', ')")

# --- 2. 补建的取货单走完：车到站，真车载端开放录入，UIA 录入，装货 -----------------------------------------

$journal.Note('Vehicle drives the retried TO_PICKUP order and arrives.')
Invoke-Arrival -intent $pickup -stationRiotId $Context.PickupStationRiotId
$stage = Wait-L2Condition -Description 'the journey trusted the pickup arrival and waits for a sublot' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingSublot' }
$canSubmit = Wait-L2Condition -Description 'the real onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
$assertions.Add(
    'L2-UR-03', '补建的取货单走完：服务端采信到站，真车载端开放录入',
    ($stage -eq 'AwaitingSublot' -and $canSubmit),
    'AwaitingSublot / can submit', "$stage / $canSubmit")

# 关卡腿的注入在录入之前布好：录入、装货、离站核验都不按单号读不存在的单，下一次这样的读就是关卡腿建单前那一次。
# L2-UR-04-A 核的正是这一点：打中的第二个 upperId 必须是关卡腿那张。
$armed = $riot.Command('Put', 'faults/absent-order-reads', @{ count = 1 })
$journal.Note("Armed one more failing read of an absent order: remaining $($armed.body.absentOrderReadFaults.remaining).")

$journal.Note("Typing sublot $sublot into ScanTextBox through UI Automation.")
$onboard.SetSublot($sublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()
$journal.Note('Manual submit invoked.')
$loadSlot = Invoke-SlotOperation -operationType 'Load' -cargoState 'OCCUPIED'
$journal.Note("Loaded slot $loadSlot.")

# --- 3. 关卡腿：离站核验之后建单前那次读失败；车上有货，补建前车况读真车载端的离站摘要 --------------------------

$stage = Wait-L2Condition -Description 'the load committed and the journey left for the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$gate = Assert-RetriedLeg -purpose 'TO_GATE' -failedIndex 1 -idPrefix 'L2-UR-04'

$upperIds = @(@($riot.Snapshot().body.orders) | ForEach-Object { [string]$_.upperId } | Sort-Object)
$expectedIds = @(@([string]$pickup.UpperId, [string]$gate.UpperId) | Sort-Object)
$assertions.Add(
    'L2-UR-05', 'RIoT 上共两张单：取货腿一张、关卡腿一张',
    (($upperIds -join ',') -eq ($expectedIds -join ',')),
    ($expectedIds -join ', '), ($upperIds -join ', '))

$journal.Note('Scenario finished.')
