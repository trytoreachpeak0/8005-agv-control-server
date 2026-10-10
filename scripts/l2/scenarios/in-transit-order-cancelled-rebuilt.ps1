#Requires -Version 7

<#
开往取货站的单被人在 RIoT 里取消：旅程先说出原因、挡住报警，延迟之后服务端为同一辆车、同一条需求重建这张单，车接着走
（control-server#318 的来源一；#316 的「挡住报警」现在是重建前的过渡）。

用户 2026-09-22 定：RIoT 里取消在途单多半是误操作，应为同一辆车、同一条需求重建订单，不释放、不改派；同日晚又定为
自动重建、不等人确认（「直接自己恢复好了，不用人确定，因为一般没人盯着系统」），带三道护栏。本场景走来源一的主路：
  1. 旅程写 ORDER_ENDED_WITHOUT_ARRIVAL，这张单停在原处（#316）；
  2. 延迟之内：不释放、不改派、不重建，也不对已终结的单发任何命令——延迟就是护栏一，给现场的人反应时间；
  3. 延迟之后：RIoT 上多了一张新单，同一辆车、同一个取货站、服务端自己的单号（W2G- 开头）；服务端这一侧是同一趟旅程、
     同一条需求，取货停靠改指向新单，新单的意图已确认，旅程码清掉；仍然没有任何订单命令。

延迟在 setup 里调成 8 秒（产品默认 30 秒），只为省机时。

负判据要有界：转过几轮之后再判，只读一次会在服务端还没来得及做错事时误绿。第二个事实（新单在 RIoT 上、意图已确认）按
scripts/l2/README.md 第 14 条用 Wait-L2ConditionOrLast 等，不读一次就断言。

红证据（修复之前的 fp/v2-impl）：L2-OC-04 等不到新单，超时（「挡住等人」那一版什么都不建）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$mes = $Context.MesIngest
$riot = $Context.Riot

$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')

function Invoke-Scalar([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# --- 1. 受理、派往取货站、车在路上 --------------------------------------------------------------------

$journal.Note("Publishing demand $($demandGuid.ToString('N')) (area N1-3).")
$null = $mes.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = "L2-OC-$($Context.RunId)"; area = 'N1-3'
    eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})

$first = Wait-L2Condition -Description 'the vehicle took the demand and set off to its pickup' `
    -Journal $journal -Criterion 'first-journey' -TimeoutSeconds 120 `
    -Probe {
        Invoke-Scalar ("SELECT r.JourneyId, r.AgvId, r.VehicleKey, r.Stage, r.PickupUpperId, r.DispatchGeneration, o.OrderId, o.Status, " +
            "o.DestinationStationId FROM JourneyRuntimes r JOIN OrderIntents o ON o.UpperId = r.PickupUpperId WHERE r.DemandId = '$demandId'")
    } `
    -Until { param($v) $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' -and [string]$v.Status -eq 'CONFIRMED' }
$pickupStop = Invoke-Scalar ("SELECT StopId, Sequence, StationId FROM JourneyStops " +
    "WHERE JourneyId = '$([string]$first.JourneyId)' AND UpperId = '$([string]$first.PickupUpperId)'")

$null = $riot.Command('Put', "orders/$([string]$first.PickupUpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'PROCESSING_ORDER'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = [string]$first.OrderId; currentPosition = 0
})
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal

# --- 2. 人在 RIoT 里取消这张单，车停在路上 ------------------------------------------------------------

$journal.Note("A person cancels $([string]$first.PickupUpperId) in RIoT.")
$null = $riot.Command('Put', "orders/$([string]$first.PickupUpperId)", @{ orderState = 2 })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    processingOrder = $false; clearOrderTaskId = $true; currentPosition = 0
})

$named = Wait-L2Condition -Description 'the journey names the order that ended without an arrival' `
    -Journal $journal -Criterion 'order-ended-named' -TimeoutSeconds 60 `
    -Probe {
        Invoke-Scalar "SELECT Stage, BlockReasonCode, BlockReasonSince FROM JourneyRuntimes WHERE JourneyId = '$([string]$first.JourneyId)'"
    } `
    -Until { param($v) $v -and [string]$v.BlockReasonCode -eq 'ORDER_ENDED_WITHOUT_ARRIVAL' }
$assertions.Add(
    'L2-OC-01',
    '订单被取消后旅程写 ORDER_ENDED_WITHOUT_ARRIVAL，仍停在开往取货站',
    ([string]$named.Stage -eq 'AwaitingPickupArrival' -and [string]$named.BlockReasonCode -eq 'ORDER_ENDED_WITHOUT_ARRIVAL'),
    'AwaitingPickupArrival / ORDER_ENDED_WITHOUT_ARRIVAL',
    "$($named.Stage) / $($named.BlockReasonCode)")

# --- 3. 延迟之内：不释放、不改派、不重建、不发命令 --------------------------------------------------------

$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal

$membership = Invoke-Scalar ("SELECT RemovedAt, RemovalReason FROM JourneyDemands " +
    "WHERE JourneyId = '$([string]$first.JourneyId)' AND DemandId = '$demandId'")
$journeys = [int](Invoke-Scalar "SELECT COUNT(*) AS N FROM JourneyRuntimes WHERE DemandId = '$demandId'").N
$intents = [int](Invoke-Scalar "SELECT COUNT(*) AS N FROM OrderIntents WHERE DemandId = '$demandId'").N
$held = Invoke-Scalar "SELECT Stage, BlockReasonCode, BlockReasonSince FROM JourneyRuntimes WHERE JourneyId = '$([string]$first.JourneyId)'"
$assertions.Add(
    'L2-OC-02',
    '延迟之内不释放、不改派、不重建：归属未移除，仍只有一趟旅程、一张移动意图，码与开始时刻不变',
    (($null -eq $membership.RemovedAt -or [string]$membership.RemovedAt -eq '') -and $journeys -eq 1 -and $intents -eq 1 -and
        [string]$held.BlockReasonCode -eq 'ORDER_ENDED_WITHOUT_ARRIVAL' -and [string]$held.BlockReasonSince -eq [string]$named.BlockReasonSince),
    "未移除 / 1 趟 / 1 张 / ORDER_ENDED_WITHOUT_ARRIVAL / $($named.BlockReasonSince)",
    "$(if ($null -eq $membership.RemovedAt -or [string]$membership.RemovedAt -eq '') { '未移除' } else { "已移除 $($membership.RemovalReason)" }) / $journeys 趟 / $intents 张 / $($held.BlockReasonCode) / $($held.BlockReasonSince)")

$audit = [int](Invoke-Scalar 'SELECT COUNT(*) AS N FROM RiotOrderCommandAudit').N
$calls = @($riot.Snapshot().body.commandInvocations).Count
$assertions.Add(
    'L2-OC-03',
    '没有发出任何订单命令或急停（包括对已终结的单再发取消）',
    ($audit -eq 0 -and $calls -eq 0),
    '0 / 0',
    "$audit / $calls")

# --- 4. 延迟之后：同车同需求，重建出一张去同一个取货站的新单 ------------------------------------------------

# 两个事实落在不同的写入里：停靠改指向新单与新意图在一次保存里，RIoT 上的单与意图的确认在之后。所以等的是后一个，
# 读不到就把最后一次读到的样子交给判据（README 第 14 条）。
$rebuilt = Wait-L2ConditionOrLast -Description 'the order was rebuilt for the same vehicle and demand, and confirmed' `
    -Journal $journal -Criterion 'order-rebuilt' -TimeoutSeconds 60 `
    -Probe {
        Invoke-Scalar ("SELECT s.StopId, s.Sequence, s.StationId, s.UpperId, o.DemandId, o.VehicleKey, o.DestinationStationId, " +
            "o.Status, o.OrderId, r.JourneyId, r.Stage, r.BlockReasonCode FROM JourneyStops s " +
            "JOIN OrderIntents o ON o.UpperId = s.UpperId JOIN JourneyRuntimes r ON r.JourneyId = s.JourneyId " +
            "WHERE s.StopId = '$([string]$pickupStop.StopId)'")
    } `
    -Until { param($v) $v -and [string]$v.UpperId -ne [string]$first.PickupUpperId -and [string]$v.Status -eq 'CONFIRMED' }
$riotOrder = @($riot.Snapshot().body.orders | Where-Object { $rebuilt -and [string]$_.upperId -eq [string]$rebuilt.UpperId })
$assertions.Add(
    'L2-OC-04',
    '延迟之后重建：取货停靠（同一个停靠、同一序位、同一站）改指向一张服务端自己的新单，意图已确认，需求与车都是原来那条、那辆，目标站不变；RIoT 上有这张单、指派给同一辆车',
    ($null -ne $rebuilt -and [string]$rebuilt.UpperId -ne [string]$first.PickupUpperId -and [string]$rebuilt.UpperId -like 'W2G-*' -and
        [string]$rebuilt.Status -eq 'CONFIRMED' -and [string]$rebuilt.DemandId -eq $demandId -and
        [string]$rebuilt.VehicleKey -eq [string]$first.VehicleKey -and
        [int]$rebuilt.DestinationStationId -eq [int]$first.DestinationStationId -and
        [int]$rebuilt.Sequence -eq [int]$pickupStop.Sequence -and [string]$rebuilt.StationId -eq [string]$pickupStop.StationId -and
        $riotOrder.Count -eq 1 -and [string]$riotOrder[0].appointVehicleKey -eq [string]$first.VehicleKey),
    "新单号 W2G-… / CONFIRMED / $demandId / $($first.VehicleKey) / 站 $($first.DestinationStationId) / 序位 $($pickupStop.Sequence) / RIoT 1 张",
    $(if ($null -eq $rebuilt) { '(停靠读不到)' } else {
        "$($rebuilt.UpperId) / $($rebuilt.Status) / $($rebuilt.DemandId) / $($rebuilt.VehicleKey) / 站 $($rebuilt.DestinationStationId) / 序位 $($rebuilt.Sequence) / RIoT $($riotOrder.Count) 张" }))

$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$after = Invoke-Scalar "SELECT Stage, BlockReasonCode FROM JourneyRuntimes WHERE JourneyId = '$([string]$first.JourneyId)'"
$journeys = [int](Invoke-Scalar "SELECT COUNT(*) AS N FROM JourneyRuntimes WHERE DemandId = '$demandId'").N
$intents = [int](Invoke-Scalar "SELECT COUNT(*) AS N FROM OrderIntents WHERE DemandId = '$demandId'").N
$record = Invoke-Scalar "SELECT State, Source, NewUpperId FROM OwnOrderRebuilds WHERE EndedUpperId = '$([string]$first.PickupUpperId)'"
$audit = [int](Invoke-Scalar 'SELECT COUNT(*) AS N FROM RiotOrderCommandAudit').N
$assertions.Add(
    'L2-OC-05',
    '重建之后：仍是同一趟旅程、开往取货站，码清掉；恰好两张移动意图（旧单留作记录）、一条重建记录（REBUILT、来源 ORDER_CANCELLED_IN_RIOT）；仍没有任何订单命令',
    ([string]$after.Stage -eq 'AwaitingPickupArrival' -and ($null -eq $after.BlockReasonCode -or [string]$after.BlockReasonCode -eq '') -and
        $journeys -eq 1 -and $intents -eq 2 -and $null -ne $record -and [string]$record.State -eq 'REBUILT' -and
        [string]$record.Source -eq 'ORDER_CANCELLED_IN_RIOT' -and $audit -eq 0),
    'AwaitingPickupArrival / (无码) / 1 趟 / 2 张 / REBUILT ORDER_CANCELLED_IN_RIOT / 0 条命令',
    "$($after.Stage) / $(if ($after.BlockReasonCode) { $after.BlockReasonCode } else { '(无码)' }) / $journeys 趟 / $intents 张 / $(if ($record) { "$($record.State) $($record.Source)" } else { '(没有重建记录)' }) / $audit 条命令")

$journal.Note('人在 RIoT 里取消在途单之后，旅程说出原因，延迟之内不释放、不改派、不重建、不发命令；延迟之后为同车同需求重建，旅程接着走。')
