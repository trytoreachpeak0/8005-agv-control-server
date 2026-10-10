#Requires -Version 7

<#
一辆在途车与一辆空闲车在同一张候选表上竞争，同一轮里两条路各走一次（批次7-06，control-server#211；
REQ-0200、REQ-0205）。

在此之前在途车根本不参加轮次：它走一条一律拒绝的占位路径，所以「哪辆车接这条需求」的答案只可能是
某辆空闲车。本票让两种车同台，身份本身不产生优先级——谁接得了、谁的边际成本低，就是谁。

**怎么证「两条路都真的走了」**：同一轮里放两条需求进来，车队里一台在途一台空闲。如果只有空闲车那条路
是活的，两条需求会一条被接、一条留在积压里等下一轮（一辆车一轮只接一条）；如果只有在途车那条路是活的，
它们会都挤进同一趟旅程。两条都被接走、而且分别落在两辆车上，只有在两条路同时活着时才出现。

两台车分别停在两个站上，第一条需求把其中一台变成在途——**哪一台由成本决定，这条场景不猜也不挑**。
写死「关卡上那台会先接单」试过一次，跑出来是另一台：两站到取货站的代价在合成地图上差得极小，
而一个靠站点摆位去猜胜负的断言，证的是那张图而不是这次改动。

所以判据只看结构，与谁是谁无关：三条需求全被接走，两台车各带一趟，其中一趟带两条需求（追加发生过）、
另一趟带一条（新建发生过）。**只有两条路同时活着才会是这个形状**——只有空闲车那条活着，会是一条被接、
一条留在积压里等下一轮（一辆车一轮只接一条）；只有在途车那条活着，两条会挤进同一趟。

**它不证「身份不产生优先级」**，因为一辆车一轮只接一条，两条需求本来就必须分给两台车。优先级那一句的
证据在 L1：`DispatchVehicleOrdering` 的排序层里没有任何一层读「这辆车是不是在途」
（`RouteGraphDispatchTests` 与 `SlotGroupSelectionTests` 的车辆侧用例守着它）。
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

$idleVehicleKey = 'BROKERX-L2-0002'
$idleAgvId = 'AGV-L2-002'
$idleStationRiotId = 11

$guids = @{ A = [guid]::NewGuid(); B = [guid]::NewGuid(); C = [guid]::NewGuid() }
$aId = $guids.A.ToString('D')

function Invoke-Scalar([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Publish-Demand([guid]$guid, [string]$name, [string]$area) {
    $journal.Note("Publishing demand $name (area $area).")
    $null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot = "L2-IIC-$name-$($Context.RunId)"; area = $area
        eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
    })
}

# --- 1. 两台车各就各位，图拉起来 -------------------------------------------------------------------

$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'IDLE'
    movementState   = 'MT_FINISHED'
    speed           = 0
    currentPosition = $Context.GateStationRiotId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $idleVehicleKey
    procState       = 'IDLE'
    movementState   = 'MT_FINISHED'
    speed           = 0
    currentPosition = $idleStationRiotId
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

# --- 2. 第一条需求把其中一台车变成在途 --------------------------------------------------------------

Publish-Demand $guids.A 'A' 'N1-3'
$firstAgv = Wait-L2Condition -Description 'the first demand put one vehicle under way' `
    -Journal $journal -Criterion 'first-journey-agv' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT AgvId, Stage FROM JourneyRuntimes WHERE DemandId = '$aId'"
        if ($rows.Count -eq 0) { return $null }
        if ([string]$rows[0].Stage -ne 'AwaitingPickupArrival') { return $null }
        return [string]$rows[0].AgvId
    } `
    -Until { param($v) $null -ne $v }

# 谁接到第一条这条场景不挑——它要的只是「此后一台在途、一台空闲」。
$journal.Note("Demand A went to $firstAgv; it is now the vehicle under way.")
$assertions.Add(
    'L2-IIC-01',
    '第一条需求让车队里的一台车进入在途状态，另一台仍空闲',
    ($firstAgv -eq $Context.AgvId -or $firstAgv -eq $idleAgvId),
    "$($Context.AgvId) 或 $idleAgvId",
    $firstAgv)

$underWayJourneyId = [string](Invoke-Scalar "SELECT JourneyId FROM JourneyRuntimes WHERE DemandId = '$aId'").JourneyId

# --- 3. 两条需求同时进来：一条走追加，一条走新建 ----------------------------------------------------

Publish-Demand $guids.B 'B' 'N1-7'
Publish-Demand $guids.C 'C' 'C15-13'

$journeys = Wait-L2Condition -Description 'both new demands were taken, by two different vehicles' `
    -Journal $journal -Criterion 'journeys' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql ("SELECT COUNT(*) AS N FROM JourneyDemands WHERE RemovedAt IS NULL")
        return [int]$rows[0].N
    } `
    -Until { param($v) $v -ge 3 }

$assertions.Add(
    'L2-IIC-03',
    '三条需求都进了某一趟旅程：没有一条被留在积压里等下一轮',
    ($journeys -eq 3),
    3,
    $journeys)

$runtimes = Invoke-L2Query -Connection $connection `
    -Sql 'SELECT JourneyId, AgvId, DemandId FROM JourneyRuntimes ORDER BY AgvId'
$journal.Observe('journeys', (($runtimes | ForEach-Object { "$($_.AgvId)=$($_.DemandId)" }) -join ' '), @{ runtimes = $runtimes })

$assertions.Add(
    'L2-IIC-04',
    '两台车各带一趟旅程：两条路在同一轮里各走了一次',
    ($runtimes.Count -eq 2),
    2,
    $runtimes.Count)

# 在途车那一趟带两条需求：有一条是追加进去的，不是另起一趟。
$underWayMemberships = [int](Invoke-Scalar ("SELECT COUNT(*) AS N FROM JourneyDemands " +
    "WHERE JourneyId = '$underWayJourneyId' AND RemovedAt IS NULL")).N
$assertions.Add(
    'L2-IIC-05',
    '在途车那一趟带了两条需求：追加这条路走通了',
    ($underWayMemberships -eq 2),
    2,
    $underWayMemberships)

# 另一趟只带一条：新建这条路也走通了。两趟加起来三条，与上面那条「三条都被接走」对得上，
# 所以不可能是「两条都追加进了同一趟」或者「有一条其实没被接」。
$otherJourney = @($runtimes | Where-Object { [string]$_.JourneyId -ne $underWayJourneyId }) | Select-Object -First 1
$otherMemberships = if ($null -eq $otherJourney) { -1 } else {
    [int](Invoke-Scalar ("SELECT COUNT(*) AS N FROM JourneyDemands " +
        "WHERE JourneyId = '$([string]$otherJourney.JourneyId)' AND RemovedAt IS NULL")).N
}
$assertions.Add(
    'L2-IIC-06',
    '另一台车那一趟只带一条需求：新建这条路同一轮里也走了一次',
    ($otherMemberships -eq 1),
    1,
    $otherMemberships)

# 在途车那一趟的停靠：A 的取货（当前下一站，不可改）、B 的取货、两条共用的卸货。
$stops = Invoke-L2Query -Connection $connection `
    -Sql ("SELECT Sequence, StopRole, StationRiotId FROM JourneyStops " +
        "WHERE JourneyId = '$underWayJourneyId' ORDER BY Sequence")
$shape = ($stops | ForEach-Object { "$($_.Sequence):$($_.StopRole)@$($_.StationRiotId)" }) -join ' '
$assertions.Add(
    'L2-IIC-07',
    '在途车的计划是三个停靠，序位 1 仍是它原本就要去的那一站（REQ-0196）',
    ($stops.Count -eq 3 -and [int]$stops[0].Sequence -eq 1 -and [string]$stops[0].StopRole -eq 'PICKUP'),
    '3 个停靠，1:PICKUP 在前',
    "$($stops.Count) 个停靠：$shape")

$journal.Note('在途车与空闲车在同一张候选表上：同一轮里一条走了追加、一条走了新建，三条需求一条不剩。')
