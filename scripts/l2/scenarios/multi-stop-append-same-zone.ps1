#Requires -Version 7

<#
一辆在途车接下第二条需求：同一分区、同一趟旅程、一张多停靠的计划（批次7-06，control-server#211；
REQ-0195、REQ-0196、REQ-0198、REQ-0205）。

这条场景证的是**途中追加这条链路真的接通了**：服务端把一条新需求判给一辆已经在路上的车，把它的两个停靠插进
那辆车现有的计划，并且把改过的计划整体重发给车（ADR-cross-0053）。四道门的数值判定不在这里，在
`Batch7EnRouteAppendPlannerTests`——那里的每个增量都是手算的，而合成地图只有三个站，做不出有说服力的代价算例。
分层与 `route-graph-engine` 那条一致：L2 证链路接通，L1 证判定本身。

形状是刻意挑的，一次看两种插入位：
- 第二条需求的**取货**在另一个站（`C15-13`，站 11），所以它是一个新开的停靠；
- 两条需求的**卸货**都是关卡（站 210），而关卡此刻不是当前下一站，所以第二条并进了既有的那个卸货停靠。

于是停靠从两个变成三个而不是四个，而归属从一条变成两条——「并入」与「新开」在同一次追加里各发生一次。

**当前下一站不可改**（REQ-0196）在这里是可观测的：第一条需求的取货停靠序位仍是 1，车照样开向它。
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

$firstGuid = [guid]::NewGuid()
$secondGuid = [guid]::NewGuid()
$firstId = $firstGuid.ToString('D')
$secondId = $secondGuid.ToString('D')
$first = @{ Wire = $firstGuid.ToString('N'); Id = $firstId; Sublot = "L2-MSA-A-$($Context.RunId)"; Area = 'N1-3' }
$second = @{ Wire = $secondGuid.ToString('N'); Id = $secondId; Sublot = "L2-MSA-B-$($Context.RunId)"; Area = 'C15-13' }
# 探针跑在脚本自己的作用域里，所以它只用脚本作用域的简单变量，既不调用下面那几个 helper 也不加
# .GetNewClosure()——闭包会把探针搬进另一个 session state，helper 在那里查不到，而查不到的表现
# 是探针一直返回空，看起来像「服务端没反应」。这条场景第一次跑就栽在这里，查了一轮才看出来。

function Invoke-Scalar([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-JourneyId([string]$demandId) {
    $row = Invoke-Scalar "SELECT JourneyId FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($null -eq $row) { return $null }
    return [string]$row.JourneyId
}

function Publish-Demand([hashtable]$demand) {
    $journal.Note("Publishing demand $($demand.Wire) (sublot $($demand.Sublot), area $($demand.Area)).")
    $null = $mes.Command('Put', "demands/$($demand.Wire)", @{
        sublot      = $demand.Sublot
        area        = $demand.Area
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

# --- 1. 车先站在关卡上 -----------------------------------------------------------------------------

# 追加要算路径代价，而代价是从车此刻所在的站算起的；车的位置未知时服务端 fail closed，一律不追加
# （与可达性判据同一条道理）。所以这条场景先把车放在一个确定的站上，否则它证不出任何关于追加的事。
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'IDLE'
    movementState   = 'MT_FINISHED'
    speed           = 0
    currentPosition = $Context.GateStationRiotId
})

# --- 1b. 等路网引擎把图拉起来 -----------------------------------------------------------------------

# 途中追加的代价是计划路径代价，而代价来自路网引擎。引擎还没刷新过时算不出任何一段，判据 fail closed，
# 追加会以 ROUTE_GRAPH_NEVER_REFRESHED 被拒——那是对的（延迟门禁保护的是既有需求的交付，算不出就不能放行），
# 但它会让这条场景看起来像「追加坏了」。所以先等图齐，与 route-graph-engine 那条用同一个判据。
#
# 注意空闲车那一侧不受影响：可达性判据在引擎未就绪时不淘汰车（REQ-0207 的「算不出成本不等于到不了」），
# 所以第一条需求在图齐之前也接得下——这条场景第一次跑就是那个样子，只有第二条被挡。
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

# --- 2. 第一条需求：一辆空闲车接走，开始在途 --------------------------------------------------------

Publish-Demand $first
$firstStage = Wait-L2Condition -Description 'the first demand was accepted and the vehicle set off' `
    -Journal $journal -Criterion 'first-journey-stage' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$firstId'"
        if ($rows.Count -eq 0) { return $null }
        return [string]$rows[0].Stage
    } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$assertions.Add(
    'L2-MSA-01',
    '第一条需求被一辆空闲车接走，车已在途（AwaitingPickupArrival）',
    ($firstStage -eq 'AwaitingPickupArrival'),
    'AwaitingPickupArrival',
    $firstStage)

$journeyId = Get-JourneyId $firstId
$journal.Note("Journey $journeyId is under way; publishing the second demand into it.")

$stopsBefore = [int](Invoke-Scalar "SELECT COUNT(*) AS N FROM JourneyStops WHERE JourneyId = '$journeyId'").N
# 追加之前，车正驶向的那一站长什么样。REQ-0196 要的是它「没被动」，而「没被动」只能拿追加前后的同一个
# 停靠去比——判成「序位 1 是一个不在关卡的 PICKUP」是不够的，第二条需求的取货同样满足那个描述。
$firstStopBefore = Invoke-Scalar ("SELECT StopId, StationRiotId FROM JourneyStops " +
    "WHERE JourneyId = '$journeyId' AND Sequence = 1")
$journal.Observe(
    'current-next-stop-before-append',
    "$($firstStopBefore.StopId)@$($firstStopBefore.StationRiotId)",
    @{ stopId = [string]$firstStopBefore.StopId; stationRiotId = [int]$firstStopBefore.StationRiotId })
$assertions.Add(
    'L2-MSA-02',
    '受理写下两个停靠：取货与卸货各一个',
    ($stopsBefore -eq 2),
    2,
    $stopsBefore)

# --- 3. 第二条需求：追加进那辆在途车，而不是另起一趟 ------------------------------------------------

Publish-Demand $second
$memberships = Wait-L2Condition -Description 'the second demand joined the journey already under way' `
    -Journal $journal -Criterion 'journey-memberships' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT COUNT(*) AS N FROM JourneyDemands WHERE JourneyId = '$journeyId' AND RemovedAt IS NULL"
        return [int]$rows[0].N
    } `
    -Until { param($v) $v -ge 2 }

$assertions.Add(
    'L2-MSA-03',
    '第二条需求进了同一趟旅程：归属两条（REQ-0205 在途车参与竞争）',
    ($memberships -eq 2),
    2,
    $memberships)

# 另起一趟是这条场景最要防的失败形态：那会让两条需求各占一辆车，而车队里只有一辆。
$runtimes = [int](Invoke-Scalar 'SELECT COUNT(*) AS N FROM JourneyRuntimes').N
$assertions.Add(
    'L2-MSA-04',
    '没有另起一趟旅程：整个库里仍然只有一条 JourneyRuntime',
    ($runtimes -eq 1),
    1,
    $runtimes)

# --- 4. 计划变成三个停靠：新开一个、并入一个 --------------------------------------------------------

# 不写成 @(Invoke-L2Query ...)：这个 helper 以 `return , $rows` 返回，外面再包一层 @() 得到的是
# 「一个元素、那个元素是三行的数组」，后面 $_.Sequence 于是成员展开成三个值，看起来像一行里塞了三个停靠。
$stops = Invoke-L2Query -Connection $connection `
    -Sql ("SELECT StopId, Sequence, StopRole, StationRiotId FROM JourneyStops " +
        "WHERE JourneyId = '$journeyId' ORDER BY Sequence")
$shape = ($stops | ForEach-Object { "$($_.Sequence):$($_.StopRole)@$($_.StationRiotId)" }) -join ' '
$journal.Observe('journey-stops', $shape, @{ stops = $stops })

$assertions.Add(
    'L2-MSA-05',
    '停靠从两个变成三个：第二条需求的取货新开一个停靠，它的卸货并进了既有的关卡停靠（不是四个）',
    ($stops.Count -eq 3),
    3,
    $stops.Count)

# REQ-0196：车正驶向的那一站不能被插到前面去，也不能被改。
$firstStop = @($stops | Where-Object { [int]$_.Sequence -eq 1 }) | Select-Object -First 1
$assertions.Add(
    'L2-MSA-06',
    '当前下一站没被动：序位 1 还是追加之前那一个停靠，站号也没被改写（REQ-0196）',
    ($null -ne $firstStop -and
        [string]$firstStop.StopId -eq [string]$firstStopBefore.StopId -and
        [int]$firstStop.StationRiotId -eq [int]$firstStopBefore.StationRiotId),
    "$($firstStopBefore.StopId)@$($firstStopBefore.StationRiotId)",
    $(if ($null -eq $firstStop) { '(缺行)' } else { "$($firstStop.StopId)@$($firstStop.StationRiotId)" }))

# 序位连续从 1 起：车上看到的次序就是计划的次序，中间不能有空档。
$sequences = @($stops | ForEach-Object { [int]$_.Sequence })
$assertions.Add(
    'L2-MSA-07',
    '三个停靠的序位是 1,2,3，连续且无重复',
    (($sequences -join ',') -eq '1,2,3'),
    '1,2,3',
    ($sequences -join ','))

# --- 5. 改过的计划整体重发给车（ADR-cross-0053） ----------------------------------------------------

# 追加在序列中间插进了停靠，而车手里那张计划是插入之前的。判据是内容：发件箱里最后一版计划的腿数
# 必须是三条——按修订号看不出该不该重发，因为当前停靠的序位一个都没动。
$planLegs = Wait-L2Condition -Description 'the server re-sent the whole plan with three legs' `
    -Journal $journal -Criterion 'plan-legs' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql ("SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'UpcomingStopPlanSnapshot' " +
                "ORDER BY CreatedAt DESC, MessageId DESC LIMIT 1")
        if ($rows.Count -eq 0) { return 0 }
        return @((ConvertFrom-Json ([string]$rows[0].PayloadJson)).payload.legs).Count
    } `
    -Until { param($v) $v -ge 3 }

$assertions.Add(
    'L2-MSA-08',
    '车上收到的最后一版计划带三条腿：计划是整体替换下发的（ADR-cross-0053）',
    ($planLegs -eq 3),
    3,
    $planLegs)

# --- 6. 第二条需求走的是追加那条路，不是新建 --------------------------------------------------------

# 判据是「它被受理过」，不是理由字面上等于 ACCEPTED：轮次每几秒跑一次，下一轮这条需求已经在
# 本轮的已受理集合里，判据链第一道就把它挡成 DEMAND_ALREADY_ACCEPTED，理由被盖掉。AcceptedAt 不会被盖掉。
# 拿理由的字面值当判据，等于让这条断言的成败取决于场景跑得多快——那不是它要证的事。
$backlog = Invoke-Scalar ("SELECT ReasonCode, AcceptedAt FROM JourneyBacklog " +
    "WHERE DemandId = '$secondId'")
$assertions.Add(
    'L2-MSA-09',
    '第二条需求被受理过：积压行上有受理时刻，没有被任何 EN_ROUTE_APPEND_* 挡住',
    ($null -ne $backlog -and $null -ne $backlog.AcceptedAt -and [string]$backlog.AcceptedAt -ne '' -and
        [string]$backlog.ReasonCode -notlike 'EN_ROUTE_APPEND_*'),
    '(有受理时刻，理由不是 EN_ROUTE_APPEND_*)',
    $(if ($null -eq $backlog) { '(无 backlog 行)' }
      else { "$([string]$backlog.ReasonCode) accepted=$(if ($null -eq $backlog.AcceptedAt) { '-' } else { 'yes' })" }))

# 「并入」这件事单独断一次，而不是只数三个停靠：两条需求的卸货都是关卡，关卡上必须只有一个停靠行。
# 多出一行就是「同站又新开了一个停靠」——停靠总数会变成四个，但四个也可能是别的形状造成的，
# 这一条把它钉在关卡这个站上。
$gateStops = [int](Invoke-Scalar ("SELECT COUNT(*) AS N FROM JourneyStops " +
    "WHERE JourneyId = '$journeyId' AND StationRiotId = $($Context.GateStationRiotId)")).N
$assertions.Add(
    'L2-MSA-10',
    '关卡上只有一个停靠：第二条需求的卸货并进了既有那一个，没有同站再开一个',
    ($gateStops -eq 1),
    1,
    $gateStops)

$journal.Note('在途车接下了第二条需求：同一趟旅程、三个停靠（新开一个、并入一个）、计划整体重发，占用没有多认领。')
