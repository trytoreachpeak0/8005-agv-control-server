#Requires -Version 7

<#
车走过一站之后，发给车载端的计划只剩剩余停靠，途中追加照样受理（control-server#749，派车问题 1；REQ-0196 经 CP-0015 修订，
需求基线 v1.14.0；协议 wire notes「remaining plan」、向量 DISPLAY_REMAINING_JOURNEY_PLAN）。

形状：两条 WIRE_TO_GATE 需求，甲在 12 号站（N1-3）取货，乙在 11 号站（C15-13）取货、途中追加进同一趟，两条都在关卡 210 卸，
停靠是「12 取、11 取、210 卸」。车在 12 号站装完甲、离站之后：
- 车载端确认的那一版计划是「11 取、210 卸」两条腿，序号 1、2，没有 12 号站那条、没有 COMPLETED（L2-RPP-02）。
  修之前这一版带着 12 号站那条 COMPLETED 腿，三条腿——这一条就是红证据；
- 库里 12 号站那个停靠是 COMPLETED、序位仍是 1：只是不下发，整趟的序位不变（L2-RPP-03）；
- 再追加第三条需求丙（N1-7，取货也在已走过的 12 号站），它被受理进同一趟，重发的计划只数剩余：「11 取、12 取、210 卸」三条，
  车载端确认；走过的那一站不在里面（L2-RPP-04、-05）。

9 段只数剩余、整趟停靠上限 20（可配）的数值判定在 L1（RemainingJourneyPlanTests）：合成地图只有三个站，要让旧门禁在这里拒
（已完成 + 剩余 + 2 > 9）得先走完七站，机时是这条的好几倍，而判定本身 L1 已按边界值钉住。这里证的是链路：服务端下发、车载端确认的
计划真的变短了，走过一站之后追加照样走得通。

读取纪律（README 第 14 条）：计划与停靠状态落在不同的提交里，凡同时读两样的探针都放进 Invoke-L2ReadSnapshot，等待条件只等被等的那一样。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ReadSnapshot.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$mes = $Context.MesIngest
$riot = $Context.Riot

$firstGuid = [guid]::NewGuid()
$secondGuid = [guid]::NewGuid()
$thirdGuid = [guid]::NewGuid()
$firstId = $firstGuid.ToString('D')
$secondId = $secondGuid.ToString('D')
$thirdId = $thirdGuid.ToString('D')
$first = @{ Wire = $firstGuid.ToString('N'); Sublot = "L2-RPP-A-$($Context.RunId)"; Area = 'N1-3' }
$second = @{ Wire = $secondGuid.ToString('N'); Sublot = "L2-RPP-B-$($Context.RunId)"; Area = 'C15-13' }
$third = @{ Wire = $thirdGuid.ToString('N'); Sublot = "L2-RPP-C-$($Context.RunId)"; Area = 'N1-7' }
$firstStation = 12
$secondStation = 11
$gateStation = [int]$Context.GateStationRiotId

# 探针跑在脚本自己的作用域里（multi-stop-append-same-zone 开头那段）：不取闭包，下面这几个函数在 Wait-L2Condition 调用时还在栈上。
function Invoke-Scalar([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Publish-Demand([hashtable]$demand) {
    $journal.Note("Publishing demand $($demand.Wire) (sublot $($demand.Sublot), area $($demand.Area)).")
    $null = $mes.Command('Put', "demands/$($demand.Wire)", @{
        sublot      = $demand.Sublot
        area        = $demand.Area
        eqp         = "EQP-L2-RPP-$($demand.Area)"
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

# 发件箱里最新的一版计划：腿按线上顺序，带确认与否。
function Get-LastPlan {
    $row = Invoke-Scalar ("SELECT MessageId, PayloadJson, AcknowledgedAt FROM ProtocolOutbox " +
        "WHERE MessageType = 'UpcomingStopPlanSnapshot' ORDER BY CreatedAt DESC, MessageId DESC LIMIT 1")
    if ($null -eq $row) { return $null }
    $payload = (ConvertFrom-Json ([string]$row.PayloadJson)).payload
    return [pscustomobject]@{
        MessageId    = [string]$row.MessageId
        Revision     = [long]$payload.planRevision
        Acknowledged = ($null -ne $row.AcknowledgedAt -and [string]$row.AcknowledgedAt -ne '')
        Legs         = @($payload.legs | ForEach-Object {
            [pscustomobject]@{
                MovementLegId = [string]$_.movementLegId
                Sequence      = [int]$_.sequence
                StationId     = [string]$_.stationId
                State         = [string]$_.state
            }
        })
    }
}

function Format-Plan([object]$plan) {
    if ($null -eq $plan) { return '(no plan)' }
    $legs = ($plan.Legs | ForEach-Object { "$($_.Sequence):$($_.StationId):$($_.State)" }) -join ' '
    return "r$($plan.Revision) $(if ($plan.Acknowledged) { 'acked' } else { 'pending' }) [$legs]"
}

function Get-Stops([string]$journeyId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT StopId, Sequence, StopRole, StationRiotId, StationId, MovementLegId, Status FROM JourneyStops " +
        "WHERE JourneyId = '$journeyId' ORDER BY Sequence")
    return , $rows
}

# --- 1. 车站在关卡上，路网拉起来（multi-stop-append-same-zone 第 1、1b 步） -------------------------------------------

$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'IDLE'
    movementState   = 'MT_FINISHED'
    speed           = 0
    currentPosition = $gateStation
})
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

# --- 2. 甲受理、乙途中追加：停靠是 12 取、11 取、210 卸 ---------------------------------------------------------------

Publish-Demand $first
$null = Wait-L2Condition -Description 'the first demand was accepted and the vehicle set off' `
    -Journal $journal -Criterion 'first-journey-stage' -TimeoutSeconds 120 `
    -Probe { $r = Invoke-Scalar "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$firstId'"; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$journeyId = [string](Invoke-Scalar "SELECT JourneyId FROM JourneyRuntimes WHERE DemandId = '$firstId'").JourneyId

Publish-Demand $second
$stopsAppended = Wait-L2ConditionOrLast -Description 'the second demand joined the journey: pickup 12, pickup 11, unload at the gate' `
    -Journal $journal -Criterion 'stops-after-second-append' -TimeoutSeconds 120 `
    -Probe { ((Get-Stops $journeyId) | ForEach-Object { "$($_.StopRole)@$($_.StationRiotId)" }) -join ' ' } `
    -Until { param($v) $v -eq "PICKUP@$firstStation PICKUP@$secondStation UNLOAD@$gateStation" }
$assertions.Add(
    'L2-RPP-01',
    '乙途中追加进甲那一趟：停靠是 12 取、11 取、关卡卸（乙的卸货并进既有的关卡停靠）',
    ($stopsAppended -eq "PICKUP@$firstStation PICKUP@$secondStation UNLOAD@$gateStation"),
    "PICKUP@$firstStation PICKUP@$secondStation UNLOAD@$gateStation",
    $stopsAppended)
if ($stopsAppended -ne "PICKUP@$firstStation PICKUP@$secondStation UNLOAD@$gateStation") {
    throw 'L2-RPP-01 did not hold: every later criterion needs the three-stop journey.'
}
$stops = Get-Stops $journeyId
$passedStop = $stops[0]
$currentStop = $stops[1]
$gateStop = $stops[2]

# --- 3. 车开到 12 号站，合成对端自动录入装货，离站等待 5 秒后离站 ---------------------------------------------------

$intent = Wait-L2Condition -Description "the first demand's pickup order was confirmed" `
    -Journal $journal -Criterion 'first-pickup-intent' -TimeoutSeconds 60 `
    -Probe { Invoke-Scalar "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$firstId' AND Purpose = 'TO_PICKUP'" } `
    -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
$journal.Note("Vehicle drives to station $firstStation and comes to rest.")
$null = $riot.Command('Put', "orders/$([string]$intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = [string]$intent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $firstStation; processingOrder = $false; clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$([string]$intent.UpperId)", @{ orderState = 5 })

# 车离开 12 号站那一刻（停靠 COMPLETED）之后，服务端在路上重发一版计划（RefreshUpcomingStopPlanAsync 按内容比较）。等的是
# 「最新一版里 11 号站那条腿是 ACTIVE、且车载端确认了」——修之前与修之后都会出现这一版，差别只在它带不带 12 号站那条，所以
# 等待条件不偏向任何一边，红绿由下面的断言判。停靠状态与计划落在不同提交里，放进一个读快照（README 第 14 条）。
$afterLeaving = Wait-L2ConditionOrLast -Description 'the vehicle left station 12 and acknowledged the plan sent on the way to 11' `
    -Journal $journal -Criterion 'plan-after-leaving-first-stop' -TimeoutSeconds 180 `
    -Probe {
        Invoke-L2ReadSnapshot -Connection $connection -Read {
            $plan = Get-LastPlan
            [pscustomobject]@{
                Plan       = $plan
                PassedDone = [string](Invoke-Scalar "SELECT Status FROM JourneyStops WHERE StopId = '$([string]$passedStop.StopId)'").Status
            }
        }
    } `
    -Until {
        param($v)
        $v.Plan -and $v.Plan.Acknowledged -and
            @($v.Plan.Legs | Where-Object { $_.MovementLegId -eq [string]$currentStop.MovementLegId -and $_.State -eq 'ACTIVE' }).Count -eq 1
    }
$journal.Observe('plan-after-leaving-first-stop', (Format-Plan $afterLeaving.Plan), @{ plan = $afterLeaving.Plan })

$expectedAfterLeaving = "1:$([string]$currentStop.StationId):ACTIVE 2:$([string]$gateStop.StationId):PLANNED"
$actualAfterLeaving = if ($afterLeaving.Plan) {
    ($afterLeaving.Plan.Legs | ForEach-Object { "$($_.Sequence):$($_.StationId):$($_.State)" }) -join ' '
} else { '(no plan)' }
$assertions.Add(
    'L2-RPP-02',
    '车离开 12 号站之后车载端确认的计划只有剩余停靠：11 取（ACTIVE，序号 1）、关卡卸（PLANNED，序号 2），没有 12 号站那条、没有 COMPLETED（DISPLAY_REMAINING_JOURNEY_PLAN）',
    ($null -ne $afterLeaving.Plan -and $afterLeaving.Plan.Acknowledged -and $actualAfterLeaving -eq $expectedAfterLeaving -and
        @($afterLeaving.Plan.Legs | Where-Object { $_.MovementLegId -eq [string]$passedStop.MovementLegId }).Count -eq 0),
    "acked [$expectedAfterLeaving]",
    (Format-Plan $afterLeaving.Plan))

$assertions.Add(
    'L2-RPP-03',
    '库里 12 号站那个停靠已 COMPLETED、序位仍是 1：走过的站只是不下发，整趟的序位不变',
    ($afterLeaving.PassedDone -eq 'COMPLETED' -and
        [int](Invoke-Scalar "SELECT Sequence FROM JourneyStops WHERE StopId = '$([string]$passedStop.StopId)'").Sequence -eq 1),
    'COMPLETED / sequence 1',
    "$($afterLeaving.PassedDone) / sequence $([int](Invoke-Scalar "SELECT Sequence FROM JourneyStops WHERE StopId = '$([string]$passedStop.StopId)'").Sequence)")

# --- 4. 走过一站之后再追加丙：受理进同一趟，重发的计划只数剩余 ----------------------------------------------------------

Publish-Demand $third
$memberships = Wait-L2ConditionOrLast -Description 'the third demand joined the journey after a stop was passed' `
    -Journal $journal -Criterion 'third-membership' -TimeoutSeconds 120 `
    -Probe { [int](Invoke-Scalar "SELECT COUNT(*) AS N FROM JourneyDemands WHERE JourneyId = '$journeyId' AND RemovedAt IS NULL").N } `
    -Until { param($v) $v -ge 3 }
$backlog = Invoke-Scalar "SELECT ReasonCode, AcceptedAt FROM JourneyBacklog WHERE DemandId = '$thirdId'"
$assertions.Add(
    'L2-RPP-04',
    '走过一站之后追加的丙被受理进同一趟：归属三条，积压行有受理时刻，没有被任何 EN_ROUTE_APPEND_* 挡住',
    ($memberships -eq 3 -and $null -ne $backlog -and $null -ne $backlog.AcceptedAt -and [string]$backlog.AcceptedAt -ne '' -and
        [string]$backlog.ReasonCode -notlike 'EN_ROUTE_APPEND_*'),
    '3 memberships / accepted',
    "$memberships memberships / $(if ($null -eq $backlog) { '(no backlog row)' } else { "$([string]$backlog.ReasonCode) accepted=$(if ([string]$backlog.AcceptedAt -ne '') { 'yes' } else { '-' })" })")

# 丙的取货是一个新停靠（12 号站已走过，不能并进那个已完成的停靠），卸货并进关卡。重发的计划：11 取（当前）、12 取、关卡卸。
$afterThird = Wait-L2ConditionOrLast -Description "the plan carrying the third demand's pickup was re-sent and acknowledged" `
    -Journal $journal -Criterion 'plan-after-third-append' -TimeoutSeconds 120 `
    -Probe { Get-LastPlan } `
    -Until {
        param($v)
        $v -and $v.Acknowledged -and @($v.Legs | Where-Object { $_.StationId -eq [string]$passedStop.StationId }).Count -ge 1
    }
$journal.Observe('plan-after-third-append', (Format-Plan $afterThird), @{ plan = $afterThird })
$expectedAfterThird = "1:$([string]$currentStop.StationId):ACTIVE 2:$([string]$passedStop.StationId):PLANNED 3:$([string]$gateStop.StationId):PLANNED"
$actualAfterThird = if ($afterThird) { ($afterThird.Legs | ForEach-Object { "$($_.Sequence):$($_.StationId):$($_.State)" }) -join ' ' } else { '(no plan)' }
$assertions.Add(
    'L2-RPP-05',
    '追加丙之后重发、车载端确认的计划只数剩余：11 取（当前，序号 1）、12 取（丙，新停靠）、关卡卸——三条，走过的 12 号站那个已完成停靠不在里面',
    ($null -ne $afterThird -and $afterThird.Acknowledged -and $actualAfterThird -eq $expectedAfterThird -and
        @($afterThird.Legs | Where-Object { $_.MovementLegId -eq [string]$passedStop.MovementLegId -or $_.State -eq 'COMPLETED' }).Count -eq 0),
    "acked [$expectedAfterThird]",
    (Format-Plan $afterThird))

$journal.Note('走过一站之后：车载端确认的计划只有剩余停靠，追加照样受理，重发的计划仍只数剩余。')
