#Requires -Version 7

<#
全厂订单在变时，车照样能离站（control-server#573）。真车载端 WPF + 真 slots-simulator，不进任何 G3 runner、不认领切片。

**现场。**cs#566（2026-10-10，agv02）：全厂约 250 张别的产线的非终态单分 3 页，订单一直在变，cs#525 的分页一致性每一两分钟判一次
「读不全」，给车载端的 vehicle-safety 投影回一次 `RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN`。真车载端据此报 `VEHICLE_NOT_READY`
约 1 秒，服务端在会话未就绪那一轮清掉 `StationDepartureWaitStartedAt`，恢复后重新计满——车在站 15 卡了 25 分钟以上。

**装置。**假 RIoT 的 `faults/nonfinal-listing-churn` 在非终态单清单后面补 250 张别的产线的单（3 页），并且每 10 次读里连续 4 次
「读到第 2 页时总数多了一张」。离站等待配 20 秒。合成车载端永远报安全、不拉投影，所以只有真车载端看得到这件事。

**修复前**（cs#573 之前）每 10 次读就有 4 次让投影回未知，车载端每几秒闪一次未就绪，20 秒的离站等待一次都走不满：`L2-LC-02`、
`L2-LC-03` 红。**修复后**投影读不全时当场再读、仍不全就沿用 3 秒内读全的那份，车载端不闪，等满 20 秒就走。

判据（`L2-LC-*`）：
- 01 注入确实打中了：装货提交到离站之间，假 RIoT 记下的被搅乱的读至少 5 次。否则 02、03 的绿什么也不证明。
- 02 车离站：装货提交后 20 秒离站等待 + 60 秒余量之内建出 TO_GATE 单并走到 `AwaitingGateArrival`。
- 03 车载端没闪：装货提交之后、TO_GATE 建单之前，服务端没收到任何一条带 `VEHICLE_NOT_READY` 的 `SafetyStateChanged`。
  建单之后车载端看到本服务端自己的在途单而报未就绪是设计如此（cs#138），不在窗口里。
- 04 旅程照常走完，`Completed`。

03 读的收件箱行都写在 TO_GATE 建单之前，02 等到阶段之后它们已经落库，所以在 02 之后读一次即可；时间比较在 PowerShell 里做，
不在 SQL 里做（SQLite 里的 DateTimeOffset 是带偏移的文本）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$simulator = $Context.Simulator
$connection = $Context.Connection

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: only the real onboard polls the vehicle-safety projection.'
}

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

function Get-Stage {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

function Get-Intent([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status, CreatedAt, CreateDispatchArmedAt FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

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

function Get-ChurnedReads { [long]$riot.Snapshot().body.nonFinalListingChurn.churnedReads }

# SafetyStateChanged lines with VEHICLE_NOT_READY the server received inside [from, to), compared as instants.
function Get-NotReadyReports([datetimeoffset]$from, [datetimeoffset]$to) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT RequestJson, ReceivedAt FROM ProtocolInbox WHERE MessageType = 'SafetyStateChanged'"
    $hits = foreach ($row in $rows) {
        $receivedAt = [datetimeoffset]::Parse([string]$row.ReceivedAt, [cultureinfo]::InvariantCulture)
        if ($receivedAt -lt $from -or $receivedAt -ge $to) { continue }
        if ([string]$row.RequestJson -cmatch '"VEHICLE_NOT_READY"') { $receivedAt.ToString('o') }
    }
    return @($hits)
}

# Same drive as real-onboard-normal-load's Invoke-SlotOperation: wait for WAITING_OPERATOR before touching the slot.
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
        throw "This scenario drives one slot per operation; the $operationType command targets $($slots.Count)."
    }
    $slotNo = [int]$slots[0]
    $null = Wait-L2Condition -Description "the onboard is waiting for the operator on slot $slotNo" `
        -Journal $journal -Criterion "$operationType-waiting-operator" -TimeoutSeconds 120 `
        -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'WAITING_OPERATOR' }).Count } `
        -Until { param($v) $v -ge 1 }
    $null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = $cargoState })
    $null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})
    return $slotNo
}

# --- 1. 需求、派车、到取货站 ------------------------------------------------------------------------

$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})
$null = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 120 `
    -Probe { $row = Get-Intent 'TO_PICKUP'; if ($row) { [string]$row.Status } else { $null } } `
    -Until { param($v) $v -eq 'CONFIRMED' }
$pickupIntent = Get-Intent 'TO_PICKUP'

$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = $pickupIntent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.PickupStationRiotId; processingOrder = $false; clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 5 })

# --- 2. 全厂订单开始变：250 张别的产线的单，每 10 次读里连续 4 次读到一半总数变了 ---------------------------

$journal.Note('Arming the non-final listing churn: 250 padding orders, 4 churned reads in every 10.')
$null = $riot.Command('Put', 'faults/nonfinal-listing-churn', @{ padding = 250; period = 10; burst = 4 })

# --- 3. 录入、装货 ----------------------------------------------------------------------------------

$null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
$onboard.SetSublot($sublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()

$loadSlot = Invoke-SlotOperation -operationType 'Load' -cargoState 'OCCUPIED'
$null = Wait-L2Condition -Description 'the load committed' `
    -Journal $journal -Criterion 'load-committed' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT Status FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'"
        if ($rows.Count -eq 0) { $null } else { [string]$rows[0].Status }
    } `
    -Until { param($v) $v -eq 'Committed' }
$committedAt = [datetimeoffset]::UtcNow
$churnedAtCommit = Get-ChurnedReads
$journal.Note("Load committed at $($committedAt.ToString('o')); $churnedAtCommit churned reads so far.")

# --- 4. 离站等待 20 秒，全厂订单一直在变 -------------------------------------------------------------

$stage = Wait-L2ConditionOrLast -Description 'the vehicle left the pickup station for the gate' `
    -Journal $journal -Criterion 'departed' -TimeoutSeconds 80 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$churnedAtDeparture = Get-ChurnedReads
$gateIntent = Get-Intent 'TO_GATE'

$assertions.Add(
    'L2-LC-01', '离站等待期间全厂订单确实在变：假 RIoT 至少搅乱了 5 次清单读取',
    (($churnedAtDeparture - $churnedAtCommit) -ge 5),
    '>= 5', ($churnedAtDeparture - $churnedAtCommit))
$assertions.Add(
    'L2-LC-02', '全厂订单在变时，车等满 20 秒离站等待后照样离站（80 秒内走到 AwaitingGateArrival、TO_GATE 单已确认）',
    ($stage -eq 'AwaitingGateArrival' -and $null -ne $gateIntent -and [string]$gateIntent.Status -eq 'CONFIRMED'),
    'AwaitingGateArrival / CONFIRMED',
    "$stage / $(if ($gateIntent) { [string]$gateIntent.Status } else { '(no TO_GATE intent)' })")

# The window ends where this server's own order begins to exist; with no gate intent it runs to now.
$windowEnd = [datetimeoffset]::UtcNow
if ($null -ne $gateIntent) {
    # Armed is stamped just before the create request goes out; the order cannot be on RIoT earlier.
    $armed = [string]$gateIntent.CreateDispatchArmedAt
    $windowEnd = [datetimeoffset]::Parse(
        ([string]::IsNullOrEmpty($armed) ? [string]$gateIntent.CreatedAt : $armed), [cultureinfo]::InvariantCulture)
}
# Read once the stage has moved: every SafetyStateChanged before the gate order was received before that.
$notReady = @(Get-NotReadyReports $committedAt $windowEnd)
$assertions.Add(
    'L2-LC-03', '装货提交到建 TO_GATE 单之间，真车载端一次都没报 VEHICLE_NOT_READY（投影没闪）',
    ($notReady.Count -eq 0),
    0, "$($notReady.Count) $($notReady -join ', ')")

# --- 5. 停止搅动，开到关卡卸货，旅程走完 -------------------------------------------------------------

$null = $riot.Command('Put', 'faults/nonfinal-listing-churn', @{ padding = 0; period = 0; burst = 0 })
if ($stage -ne 'AwaitingGateArrival') {
    $journal.Note('The vehicle never left; skipping the gate leg.')
    return
}

$null = $riot.Command('Put', "orders/$($gateIntent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = $gateIntent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.GateStationRiotId; processingOrder = $false; clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($gateIntent.UpperId)", @{ orderState = 5 })

$null = Invoke-SlotOperation -operationType 'Unload' -cargoState 'EMPTY'
$final = Wait-L2ConditionOrLast -Description 'the journey completed at the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'Completed' }
$assertions.Add('L2-LC-04', '旅程走到 Completed', ($final -eq 'Completed'), 'Completed', $final)

$journal.Note('Scenario finished.')
