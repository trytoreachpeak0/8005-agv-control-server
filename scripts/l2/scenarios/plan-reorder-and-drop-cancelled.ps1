#Requires -Version 7

<#
取消一条需求之后，它留下的空停靠从计划里删掉，改过的计划整体重发给车（批次7-10，control-server#215；REQ-0197、
ADR-cross-0053）。

一辆车承载三条 STAGING_TO_WIRE 需求：A 先被受理（取货派工待送站 305、卸货机台 12），B、C 追加进同一趟旅程
（卸货机台 11、13）。车到 305、没人扫码，操作员在扫码前取消 A。断言：
  - A 的归属终结，它自己的卸货停靠（机台 12）标 REMOVED——那个停靠上已经没有剩余作业；
  - 车上收到的最后一版计划里没有机台 12 那条腿，计划修订号比取消之前前进了；
  - 当前下一站（A 的取货停靠）没有被删：REQ-0197「当前下一站不删」；
  - B、C 的归属仍在，它们的腿仍在计划里。

**这条场景不证「其余照常完成」**：现有 L2 没有一个把多需求旅程跑到完成，那要一整套多停靠驾驶编排。
那一半在 L1：Batch7ThreeStopJourneyTests.AfterOneDemandEndsAndItsStopsAreRemovedTheOtherRunsToCompletion
（调度 2026-09-21 定，PR 里写明）。换序是否发生只记进日志、不断言——终结之后换不换取决于路网上的代价，
那是 L1（Batch7PlanRevisionStageTests）用手算代价钉住的事。

红证据（缺陷版本）：取消后不修订计划——「A 的卸货停靠 REMOVED」与「最后一版计划里没有机台 12」变红。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$mes = $Context.MesIngest
$riot = $Context.Riot
$onboard = $Context.Onboard

$stagingStationRiotId = 305
$demands = [ordered]@{
    A = @{ Guid = [guid]::NewGuid(); Area = 'N1-3'; Eqp = 'EQP-L2-PRD-A' }
    B = @{ Guid = [guid]::NewGuid(); Area = 'C15-13'; Eqp = 'EQP-L2-PRD-B' }
    C = @{ Guid = [guid]::NewGuid(); Area = 'N1-5'; Eqp = 'EQP-L2-PRD-C' }
}
$aId = $demands.A.Guid.ToString('D')
$bId = $demands.B.Guid.ToString('D')
$cId = $demands.C.Guid.ToString('D')

function Invoke-Scalar([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Publish-Demand([string]$name) {
    $demand = $demands[$name]
    $journal.Note("Publishing STAGING_TO_WIRE demand $name $($demand.Guid.ToString('N')) (area $($demand.Area)).")
    $null = $mes.Command('Put', "demands/$($demand.Guid.ToString('N'))", @{
        sublot = "L2-PRD-$name-$($Context.RunId)"; area = $demand.Area; eqp = $demand.Eqp
        package = 'L2-PACKAGE'; maxBoxCount = 4; workType = 'STAGING_TO_WIRE'
    })
}

# 车最后收到的那一版计划：修订号与每条腿的站点。
function Get-LastPlan {
    $row = Invoke-Scalar ("SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'UpcomingStopPlanSnapshot' " +
        "ORDER BY CreatedAt DESC, MessageId DESC LIMIT 1")
    if ($null -eq $row) { return $null }
    $payload = (ConvertFrom-Json ([string]$row.PayloadJson)).payload
    return [pscustomobject]@{
        Revision = [long]$payload.planRevision
        Stations = @($payload.legs | ForEach-Object { [string]$_.stationId })
    }
}

# --- 1. 车在关卡上，路网拉起来；合成对端不回答录入请求（到站之后要等操作员取消） ----------------------

$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.GateStationRiotId
})
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Silent' })
$null = Wait-L2Condition -Description 'the route graph engine finished a refresh cycle' `
    -Journal $journal -Criterion 'route-graph-ready' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql ("SELECT DesignEdgeCount, RuntimeRefreshedAt, StaleReason FROM RouteGraphSnapshots " +
                "WHERE MapId = $($Context.MapId)")
        if ($rows.Count -eq 0) { return $false }
        return [int]$rows[0].DesignEdgeCount -gt 0 -and
            $null -ne $rows[0].RuntimeRefreshedAt -and [string]$rows[0].RuntimeRefreshedAt -ne '' -and
            ($null -eq $rows[0].StaleReason -or [string]$rows[0].StaleReason -eq '')
    } `
    -Until { param($v) $v }

# --- 2. A 受理，B、C 追加进同一趟 --------------------------------------------------------------------

Publish-Demand 'A'
$journey = Wait-L2Condition -Description 'demand A was accepted and the vehicle set off to the staging station' `
    -Journal $journal -Criterion 'a-accepted' -TimeoutSeconds 120 `
    -Probe { Invoke-Scalar "SELECT JourneyId, Stage, PickupUpperId FROM JourneyRuntimes WHERE DemandId = '$aId'" } `
    -Until { param($v) $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId

Publish-Demand 'B'
Publish-Demand 'C'
$members = Wait-L2Condition -Description 'demands B and C joined the journey under way' `
    -Journal $journal -Criterion 'b-c-appended' -TimeoutSeconds 180 `
    -Probe {
        [int](Invoke-Scalar ("SELECT COUNT(*) AS N FROM JourneyDemands WHERE JourneyId = '$journeyId' " +
            "AND RemovedAt IS NULL")).N
    } `
    -Until { param($v) $v -ge 3 }
$assertions.Add(
    'L2-PRD-01',
    '三条需求都在同一趟旅程上（A 受理、B 与 C 追加）',
    ($members -eq 3),
    3,
    $members)

$aStops = Invoke-Scalar ("SELECT PickupStopId, UnloadStopId FROM JourneyDemands " +
    "WHERE JourneyId = '$journeyId' AND DemandId = '$aId'")
$aUnload = Invoke-Scalar "SELECT StopId, StationId, StationRiotId FROM JourneyStops WHERE StopId = '$([string]$aStops.UnloadStopId)'"
$stopsBefore = Invoke-L2Query -Connection $connection `
    -Sql "SELECT StopId, Sequence, StopRole, StationRiotId, Status FROM JourneyStops WHERE JourneyId = '$journeyId' ORDER BY Sequence"
$journal.Observe('stops-before-cancel',
    (($stopsBefore | ForEach-Object { "$($_.Sequence):$($_.StopRole)@$($_.StationRiotId)" }) -join ' '),
    @{ stops = $stopsBefore })

# 取消之前车上那一版计划里一定有 A 的卸货站：没有这一条，下面「没有它」在计划根本没重发时也成立。
$planBefore = Wait-L2Condition -Description "the plan on the vehicle carries A's unload station before the cancellation" `
    -Journal $journal -Criterion 'plan-before' -TimeoutSeconds 60 `
    -Probe { Get-LastPlan } `
    -Until { param($v) $v -and $v.Stations -contains [string]$aUnload.StationId -and $v.Stations.Count -ge 4 }

# --- 3. 车到派工待送站，没人扫码 ----------------------------------------------------------------------

$intent = Wait-L2Condition -Description "demand A's pickup order was confirmed" `
    -Journal $journal -Criterion 'a-pickup-intent' -TimeoutSeconds 60 `
    -Probe { Invoke-Scalar "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE UpperId = '$([string]$journey.PickupUpperId)'" } `
    -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
$null = $riot.Command('Put', "orders/$([string]$intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = [string]$intent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $stagingStationRiotId; processingOrder = $false; clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$([string]$intent.UpperId)", @{ orderState = 5 })

$null = Wait-L2Condition -Description 'the vehicle stands at the staging station waiting for a sublot' `
    -Journal $journal -Criterion 'a-waiting' -TimeoutSeconds 120 `
    -Probe { [string](Invoke-Scalar "SELECT Stage FROM JourneyRuntimes WHERE JourneyId = '$journeyId'").Stage } `
    -Until { param($v) $v -eq 'AwaitingSublot' }

# 修订号的比较基准取在到站之后、取消之前（审查 M6）：到站本身就让计划修订号加一（到站那一版计划），基准若取在
# 到站之前，「修订号前进」恒真，判不出取消有没有让计划重发。等到站那一版到车上、而且仍带着 A 的卸货站再取。
$planAtStation = Wait-L2Condition -Description "the arrival plan reached the vehicle, still carrying A's unload station" `
    -Journal $journal -Criterion 'plan-at-station' -TimeoutSeconds 60 `
    -Probe { Get-LastPlan } `
    -Until { param($v) $v -and $v.Revision -gt $planBefore.Revision -and $v.Stations -contains [string]$aUnload.StationId }

# --- 4. 操作员在扫码前取消 A ---------------------------------------------------------------------------

$cancellationId = [guid]::NewGuid().ToString('D')
$journal.Note("Operator cancels demand A before any sublot entry ($cancellationId).")
$null = $onboard.Command('Put', "load-cancellations/$cancellationId", @{
    demandId = $aId; slotOperationAttemptId = $null; reason = '现场确认本站没有 A 要装的货。'
})

$ended = Wait-L2Condition -Description "demand A ended and its unload stop was removed" `
    -Journal $journal -Criterion 'a-ended' -TimeoutSeconds 90 `
    -Probe {
        Invoke-Scalar ("SELECT d.Status AS Membership, s.Status AS UnloadStop FROM JourneyDemands d " +
            "JOIN JourneyStops s ON s.StopId = d.UnloadStopId WHERE d.JourneyId = '$journeyId' AND d.DemandId = '$aId'")
    } `
    -Until { param($v) $v -and [string]$v.Membership -eq 'TERMINATED' -and [string]$v.UnloadStop -eq 'REMOVED' }

$assertions.Add(
    'L2-PRD-02',
    'A 终结（扫码前取消），它自己的卸货停靠（机台 12）因为没有剩余作业被删',
    ([string]$ended.Membership -eq 'TERMINATED' -and [string]$ended.UnloadStop -eq 'REMOVED'),
    'TERMINATED / REMOVED',
    "$($ended.Membership) / $($ended.UnloadStop)")

$planAfter = Wait-L2Condition -Description "a revised plan without A's unload station reached the vehicle" `
    -Journal $journal -Criterion 'plan-after' -TimeoutSeconds 60 `
    -Probe { Get-LastPlan } `
    -Until { param($v) $v -and $v.Stations -notcontains [string]$aUnload.StationId }

$assertions.Add(
    'L2-PRD-03',
    '车上最后一版计划里没有机台 12 那条腿，计划修订号前进（整体替换下发，ADR-cross-0053）',
    ($planAfter.Stations -notcontains [string]$aUnload.StationId -and $planAfter.Revision -gt $planAtStation.Revision),
    "无 $([string]$aUnload.StationId) / 修订号 > $($planAtStation.Revision)（到站那一版）",
    "$(($planAfter.Stations) -join ',') / 修订号 $($planAfter.Revision)")

$aPickup = Invoke-Scalar "SELECT Status FROM JourneyStops WHERE StopId = '$([string]$aStops.PickupStopId)'"
$assertions.Add(
    'L2-PRD-04',
    '当前下一站（A 的取货停靠）没有被删（REQ-0197：当前下一站不删不换）',
    ([string]$aPickup.Status -ne 'REMOVED'),
    '不是 REMOVED',
    [string]$aPickup.Status)

$others = Invoke-L2Query -Connection $connection -Sql (
    "SELECT d.DemandId, d.Status, d.RemovedAt, s.StationId FROM JourneyDemands d " +
    "JOIN JourneyStops s ON s.StopId = d.UnloadStopId " +
    "WHERE d.JourneyId = '$journeyId' AND d.DemandId IN ('$bId', '$cId')")
$othersInForce = @($others | Where-Object { $null -eq $_.RemovedAt -or [string]$_.RemovedAt -eq '' }).Count
$othersInPlan = @($others | Where-Object { $planAfter.Stations -contains [string]$_.StationId }).Count
$assertions.Add(
    'L2-PRD-05',
    'B、C 的归属仍在，它们的卸货腿仍在最后一版计划里',
    ($othersInForce -eq 2 -and $othersInPlan -eq 2),
    '2 / 2',
    "$othersInForce / $othersInPlan")

$stopsAfter = Invoke-L2Query -Connection $connection `
    -Sql "SELECT StopId, Sequence, StopRole, StationRiotId, Status FROM JourneyStops WHERE JourneyId = '$journeyId' ORDER BY Sequence"
$journal.Observe('stops-after-cancel',
    (($stopsAfter | ForEach-Object { "$($_.Sequence):$($_.StopRole)@$($_.StationRiotId)/$($_.Status)" }) -join ' '),
    @{ stops = $stopsAfter })

$journal.Note('取消 A 之后，它的卸货停靠被删，改过的计划整体重发给车；当前下一站没删，B、C 照旧在计划里。')
