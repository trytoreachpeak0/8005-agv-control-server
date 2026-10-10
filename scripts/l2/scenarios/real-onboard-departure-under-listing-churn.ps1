#Requires -Version 7

<#
全厂订单在变时，车照样能离站（control-server#573）。真车载端 WPF + 真 slots-simulator，不进任何 G3 runner、不认领切片。

**现场。**cs#566（2026-10-10，agv02）：全厂约 250 张别的产线的非终态单分 3 页，订单一直在变，cs#525 的分页一致性每一两分钟判一次
「读不全」，给车载端的 vehicle-safety 投影回一次 `RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN`。真车载端据此报 `VEHICLE_NOT_READY`
约 1 秒，服务端在会话未就绪那一轮清掉 `StationDepartureWaitStartedAt`，恢复后重新计满——车在站 15 卡了 25 分钟以上。

**装置。**假 RIoT 的 `faults/nonfinal-listing-churn` 在非终态单清单后面补 250 张别的产线的单（3 页），并且按**确定性计数**搅：每 N 次读
搅第 N 次（读到第 2 页时总数多一张），一次只搅一遍、从不连续。离站场景 N=40（2.5%），离站等待 180 秒。合成车载端永远报安全、不拉投影，
所以只有真车载端看得到这件事。

**强度从哪来。**现场 10-10 18 时～10-11 00 时 v2 实例日志实测，单次读不全约 0.2%～0.5%，最坏的 5 分钟约 1.3%（PR 正文有出处）。
2.5% 约为现场的 5 倍。**必须是计数，不能是按概率随机**：修复后一次请求最多读 3 遍，被搅的读相隔 N 次，所以 3 遍至多撞上 1 遍，
按构造不会闪；随机的话，修复后每次拉取闪的概率是 p³，判据就靠运气了。计数由 `FakeRiotTests` 的离线自检钉住。

**修复前**一读不全就闪。装置上全部调用方合计每秒约 1.8 次清单读，每 40 次搅 1 次即每 22 秒左右 1 次，车载端那条读约占 55%，
约每 40 秒闪一次；180 秒等待里至少闪一次的概率约 99%，`L2-LC-02`、`L2-LC-03` 红。20 秒的等待做不到这一点（修复前一半以上能走）。
**修复后**投影读不全时在同一次请求里当场重读，车载端不闪，等满 180 秒就走。

**为什么装完才搅。**这个场景要的红是离站等待那一段（现场就是那一段）。第一版从车到站就开始搅，修复前的 CI 真装置
（run 38067577157）红在了装货：车载端开锁后进入等操作员，正撞上一次未就绪，那条 `OperationProgress`（`WAITING_OPERATOR`）
没发出去（车载端日志「仓位操作进度未能发送……InvalidOperationException」），服务端看不到、场景等它 120 秒超时。那是同一个缺陷的
另一个后果，但它让判据表一条都没走到。所以注入在装货提交之后才打开，装货那一段由 `real-onboard-load-under-listing-churn` 判。

判据（`L2-LC-*`）：
- 01 注入确实打中了：装货提交到离站之间，假 RIoT 记下的被搅乱的读至少 3 次（180 秒约 300 次读、约 7 次被搅）。否则 02、03 的绿什么也不证明。
- 02 车离站：离站等待 + 60 秒余量之内（离站场景 240 秒）建出 TO_GATE 单并走到 `AwaitingGateArrival`。
- 03 车载端没闪：装货提交之后、TO_GATE 建单之前，服务端没收到任何一条带 `VEHICLE_NOT_READY` 的 `SafetyStateChanged`。
  建单之后车载端看到本服务端自己的在途单而报未就绪是设计如此（cs#138），不在窗口里。
- 04 旅程照常走完，`Completed`。

`-ChurnFrom PickupArrival`（场景 `real-onboard-load-under-listing-churn` 传它）从车到取货站就开始搅，N=4（25%，仍是计数、仍不连续），
离站等待 20 秒、预算 80 秒——装货只有十几秒，N=40 打不中。多判装货那一段：
- 05 装货期间注入确实打中了：车到站到装货提交之间至少搅乱 5 次。
- 06 真车载端的 `WAITING_OPERATOR` 进度送到了服务端（120 秒内）。修复前这一条就是 run 38067577157 的样子：开锁后撞上一次未就绪，
  进度发不出去、此后不补发，服务端干等（`evidence/cs573/rig-red-load-phase-38067577157`）。不补发是车载端另一个缺陷，调度另开票；
  本票修好的是让会话不再因清单搅动而闪。
- 07 装货照常提交。
然后照样判 01～04（01 的门槛是 5）。

03 读的收件箱行都写在 TO_GATE 建单之前，02 等到阶段之后它们已经落库，所以在 02 之后读一次即可；时间比较在 PowerShell 里做，
不在 SQL 里做（SQLite 里的 DateTimeOffset 是带偏移的文本）。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][object]$Context,
    # LoadCommitted: churn from the load commit on (this scenario). PickupArrival: churn from arrival at the pickup on,
    # through the load (real-onboard-load-under-listing-churn passes it; see that file).
    [ValidateSet('LoadCommitted', 'PickupArrival')][string]$ChurnFrom = 'LoadCommitted'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

# Every Period-th read churned, Burst 1: a deterministic count, never a draw (header, "强度从哪来"). The departure budget is the
# setup's StationDepartureWaitTimeout plus 60 seconds; both scenarios' setups carry their own wait.
$plan = if ($ChurnFrom -eq 'PickupArrival') {
    @{ Period = 4; Burst = 1; DepartureBudgetSeconds = 80; MinimumChurned = 5 }
} else {
    @{ Period = 40; Burst = 1; DepartureBudgetSeconds = 240; MinimumChurned = 3 }
}

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
    # Not a throw: under PickupArrival a missing WAITING_OPERATOR is what L2-LC-06 judges, and run 38067577157 is what it
    # looks like before the fix (evidence/cs573/rig-red-load-phase-38067577157).
    $waiting = [int](Wait-L2ConditionOrLast -Description "the onboard is waiting for the operator on slot $slotNo" `
        -Journal $journal -Criterion "$operationType-waiting-operator" -TimeoutSeconds 120 `
        -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'WAITING_OPERATOR' }).Count } `
        -Until { param($v) $v -ge 1 })
    if ($waiting -lt 1) { return [pscustomobject]@{ SlotNo = $slotNo; WaitingReported = $false } }
    $null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = $cargoState })
    $null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})
    return [pscustomobject]@{ SlotNo = $slotNo; WaitingReported = $true }
}

function Set-ListingChurn {
    $journal.Note("Arming the non-final listing churn: 250 padding orders, every $($plan.Period)th read churned, never two in a row.")
    $null = $riot.Command('Put', 'faults/nonfinal-listing-churn', @{ padding = 250; period = $plan.Period; burst = $plan.Burst })
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

# --- 2. 录入、装货（LoadCommitted 时全厂订单还没开始变，见头注释「为什么装完才搅」） ------------------------------

$churnedAtArrival = $null
if ($ChurnFrom -eq 'PickupArrival') {
    Set-ListingChurn
    $churnedAtArrival = Get-ChurnedReads
}

$null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
$onboard.SetSublot($sublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()

$load = Invoke-SlotOperation -operationType 'Load' -cargoState 'OCCUPIED'
if ($ChurnFrom -eq 'PickupArrival') {
    $assertions.Add(
        'L2-LC-06', '装货时全厂订单在变，真车载端的 WAITING_OPERATOR 进度照样送到服务端（120 秒内）',
        $load.WaitingReported, $true, $load.WaitingReported)
}
if (-not $load.WaitingReported) {
    if ($ChurnFrom -ne 'PickupArrival') { throw 'The onboard never reported WAITING_OPERATOR for the load.' }
    $null = $riot.Command('Put', 'faults/nonfinal-listing-churn', @{ padding = 0; period = 0; burst = 0 })
    $journal.Note('The load never reached the operator; skipping the rest.')
    return
}
$loadStatus = Wait-L2ConditionOrLast -Description 'the load committed' `
    -Journal $journal -Criterion 'load-committed' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT Status FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'"
        if ($rows.Count -eq 0) { $null } else { [string]$rows[0].Status }
    } `
    -Until { param($v) $v -eq 'Committed' }
$committedAt = [datetimeoffset]::UtcNow
if ($ChurnFrom -eq 'PickupArrival') {
    $churnedDuringLoad = (Get-ChurnedReads) - $churnedAtArrival
    $assertions.Add(
        'L2-LC-05', '装货期间全厂订单确实在变：假 RIoT 至少搅乱了 5 次清单读取',
        ($churnedDuringLoad -ge 5), '>= 5', $churnedDuringLoad)
    $assertions.Add(
        'L2-LC-07', '装货在清单搅动下照常提交（Committed）',
        ($loadStatus -eq 'Committed'), 'Committed', $loadStatus)
}
elseif ($loadStatus -ne 'Committed') {
    throw "The load did not commit: $loadStatus."
}

# --- 3. 全厂订单开始变（PickupArrival 时早已在变）：250 张别的产线的单，每 N 次读搅第 N 次（计数，不连续） -------

if ($ChurnFrom -eq 'LoadCommitted') { Set-ListingChurn }
$churnedAtCommit = Get-ChurnedReads
$journal.Note("Load committed at $($committedAt.ToString('o')); churn armed with $churnedAtCommit churned reads so far.")

# --- 4. 离站等待（离站场景 180 秒、装货场景 20 秒），全厂订单一直在变 -------------------------------------------------------------

$stage = Wait-L2ConditionOrLast -Description 'the vehicle left the pickup station for the gate' `
    -Journal $journal -Criterion 'departed' -TimeoutSeconds $plan.DepartureBudgetSeconds `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$churnedAtDeparture = Get-ChurnedReads
$gateIntent = Get-Intent 'TO_GATE'

$assertions.Add(
    'L2-LC-01', "离站等待期间全厂订单确实在变：假 RIoT 至少搅乱了 $($plan.MinimumChurned) 次清单读取",
    (($churnedAtDeparture - $churnedAtCommit) -ge $plan.MinimumChurned),
    ">= $($plan.MinimumChurned)", ($churnedAtDeparture - $churnedAtCommit))
$assertions.Add(
    'L2-LC-02', "全厂订单在变时，车等满离站等待后照样离站（$($plan.DepartureBudgetSeconds) 秒内走到 AwaitingGateArrival、TO_GATE 单已确认）",
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
