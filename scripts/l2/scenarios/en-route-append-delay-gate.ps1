#Requires -Version 7

<#
延迟门禁把一次太贵的追加挡下来（批次7-06，control-server#211；REQ-0198）。

这条场景证的是**门禁真的在判代价，而不是在判有没有配置**。本区配了上限，只是配得极紧（一毫米），
而这次追加要让车折返去另一个站——增量远不止一毫米，于是被拒，原因码是
`EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED` 而不是 `EN_ROUTE_APPEND_NOT_CONFIGURED`。

**它与 `en-route-append-not-configured` 是一对**，两条场景除了 setup 里那一行上限之外逐字相同。
一条证「本区不许追加」，一条证「这一次追加太贵」；只跑其中一条，把未配置误判成上限为零（或者反过来）
都看不出来，因为两种写法都会让追加不发生。分得清它们的是原因码，而现场看到的正是原因码。

增量的数值判定不在这里，在 `Batch7EnRouteAppendPlannerTests`——那里有一对只差一毫米的用例，
钉住门禁是「大于」而不是「大于等于」。这里跑的是同一句话在真链路上的样子。
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

function Invoke-Scalar([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Publish-Demand([string]$wire, [string]$sublot, [string]$area) {
    $journal.Note("Publishing demand $wire (sublot $sublot, area $area).")
    $null = $mes.Command('Put', "demands/$wire", @{
        sublot = $sublot; area = $area; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
    })
}

# --- 1. 车站在关卡上，路网图拉起来 -----------------------------------------------------------------

$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'IDLE'
    movementState   = 'MT_FINISHED'
    speed           = 0
    currentPosition = $Context.GateStationRiotId
})

# 图必须齐：图没齐时追加会以 ROUTE_GRAPH_NEVER_REFRESHED 被拒，那也是「没追加」，但原因不是这条场景
# 要证的那个。等齐了再发第二条需求，拒绝就只能来自本区未配置。
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

# 参数表是空的——这是编排器的默认前置，这条场景靠它，所以先把它断出来而不是假定它。
# 参数确实配上了，而且是那个极紧的值——这条场景靠它，所以先断出来而不是假定它。
$configured = Invoke-Scalar ("SELECT EnRouteAdditionMaxPathCostIncrease AS Allowance FROM DispatchZoneParameters " +
    "WHERE DispatchZone = '$($Context.DispatchZone)'")
$assertions.Add(
    'L2-EDG-01',
    '本区配了途中追加上限，值是一毫米：第一道门（本区未配置）过得去',
    ($null -ne $configured -and [long]$configured.Allowance -eq 1),
    1,
    $(if ($null -eq $configured) { '(本区无参数行)' } else { [string]$configured.Allowance }))

# --- 2. 第一条需求：空闲车接走 ---------------------------------------------------------------------

Publish-Demand $firstGuid.ToString('N') "L2-EDG-A-$($Context.RunId)" 'N1-3'
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
    'L2-EDG-02',
    '第一条需求被一辆空闲车接走，车已在途',
    ($firstStage -eq 'AwaitingPickupArrival'),
    'AwaitingPickupArrival',
    $firstStage)

$journeyId = [string](Invoke-Scalar "SELECT JourneyId FROM JourneyRuntimes WHERE DemandId = '$firstId'").JourneyId

# --- 3. 第二条需求：被判成本区未配置，一直留在积压里 ------------------------------------------------

Publish-Demand $secondGuid.ToString('N') "L2-EDG-B-$($Context.RunId)" 'C15-13'
$reason = Wait-L2Condition -Description 'the round judged the second demand against the in-transit chain' `
    -Journal $journal -Criterion 'second-demand-reason' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$secondId'"
        if ($rows.Count -eq 0) { return $null }
        return [string]$rows[0].ReasonCode
    } `
    -Until { param($v) $null -ne $v }

$assertions.Add(
    'L2-EDG-03',
    '第二条需求被延迟门禁挡住（EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED），而不是被判成本区未配置',
    ($reason -eq 'EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED'),
    'EN_ROUTE_APPEND_DELAY_GATE_EXCEEDED',
    $reason)

# 原因码对了还不够：这条场景真正要证的是「什么都没发生」。再跑一会儿，确认它不是恰好还没轮到。
$journal.Note('Letting several more dispatch rounds pass, to show the refusal is steady rather than a race.')
Start-Sleep -Seconds 6

$memberships = [int](Invoke-Scalar ("SELECT COUNT(*) AS N FROM JourneyDemands " +
    "WHERE JourneyId = '$journeyId' AND RemovedAt IS NULL")).N
$assertions.Add(
    'L2-EDG-04',
    '那趟旅程仍然只带着一条需求：追加一次都没有发生',
    ($memberships -eq 1),
    1,
    $memberships)

$stops = [int](Invoke-Scalar "SELECT COUNT(*) AS N FROM JourneyStops WHERE JourneyId = '$journeyId'").N
$assertions.Add(
    'L2-EDG-05',
    '停靠仍然是两个：计划一个字都没被改写',
    ($stops -eq 2),
    2,
    $stops)

$accepted = Invoke-Scalar "SELECT AcceptedAt FROM JourneyBacklog WHERE DemandId = '$secondId'"
$assertions.Add(
    'L2-EDG-06',
    '第二条需求没有被受理过：积压行上没有受理时刻，它在等下一辆空闲车',
    ($null -ne $accepted -and ($null -eq $accepted.AcceptedAt -or [string]$accepted.AcceptedAt -eq '')),
    '(无受理时刻)',
    $(if ($null -eq $accepted) { '(无 backlog 行)' }
      elseif ($null -eq $accepted.AcceptedAt) { '(无受理时刻)' } else { [string]$accepted.AcceptedAt }))

# 计划也不该被重发：没有追加就没有该改的东西，多发一版会让车白重放一次。
$plans = [int](Invoke-Scalar ("SELECT COUNT(*) AS N FROM ProtocolOutbox " +
    "WHERE MessageType = 'UpcomingStopPlanSnapshot'")).N
$assertions.Add(
    'L2-EDG-07',
    '计划只发过一版：没有追加，就没有整体重发',
    ($plans -eq 1),
    1,
    $plans)

$journal.Note('上限一毫米时，一次要折返的追加被延迟门禁挡下：旅程、停靠、计划三样一个字都没变。')
