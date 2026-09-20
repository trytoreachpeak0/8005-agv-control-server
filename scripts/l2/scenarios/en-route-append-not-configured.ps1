#Requires -Version 7

<#
本区没有派车参数时，在途车一条追加都接不到（批次7-06，control-server#211；REQ-0198）。

这条场景证的是**这次改动对今天的现场没有任何影响**。每区派车参数没有全项目默认值，要一份一份批准
（REQ-0203，批次7-11）；批准之前，途中追加的上限就是「没有」，而「没有」在 REQ-0198 里的意思是
本区禁止追加，不是「随便延」。所以在参数批准之前，一辆在途车的行为与本票之前一模一样：它照常跑自己的
旅程，新需求排队等下一辆空闲车。

**这是本票最要紧的一条安全保证，也是最容易被写反的一条**：把「未配置」当成「不限制」只是一处判空的
写法差别，而后果是车队在没人点头的情况下开始绕路。它在 L1 有一条手算的用例
（`Batch7EnRouteAppendPlannerTests.AZoneWithNoParametersRefusesEveryAppend`），这里跑的是同一句话在
真链路上的样子：同一台服务端、同一个派车轮次、同一份判据链。

场景与 `multi-stop-append-same-zone` 逐字同形，只差 setup 里那一行参数。两条一起看才说明问题：
有参数就追加，没参数就不追加，别的都没动。
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
$versions = [int](Invoke-Scalar 'SELECT COUNT(*) AS N FROM DispatchZoneParameterVersions').N
$assertions.Add(
    'L2-ENC-01',
    '本区一版派车参数都没有：这正是参数批准之前现场的样子',
    ($versions -eq 0),
    0,
    $versions)

# --- 2. 第一条需求：空闲车接走 ---------------------------------------------------------------------

Publish-Demand $firstGuid.ToString('N') "L2-ENC-A-$($Context.RunId)" 'N1-3'
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
    'L2-ENC-02',
    '第一条需求被一辆空闲车接走，车已在途',
    ($firstStage -eq 'AwaitingPickupArrival'),
    'AwaitingPickupArrival',
    $firstStage)

$journeyId = [string](Invoke-Scalar "SELECT JourneyId FROM JourneyRuntimes WHERE DemandId = '$firstId'").JourneyId

# --- 3. 第二条需求：被判成本区未配置，一直留在积压里 ------------------------------------------------

Publish-Demand $secondGuid.ToString('N') "L2-ENC-B-$($Context.RunId)" 'C15-13'
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
    'L2-ENC-03',
    '第二条需求被判为本区未配置途中追加（EN_ROUTE_APPEND_NOT_CONFIGURED），不是被别的什么挡住',
    ($reason -eq 'EN_ROUTE_APPEND_NOT_CONFIGURED'),
    'EN_ROUTE_APPEND_NOT_CONFIGURED',
    $reason)

# 原因码对了还不够：这条场景真正要证的是「什么都没发生」。再放几轮过去，确认它不是恰好还没轮到。
#
# 数的是轮次不是秒数（scripts/l2/README.md「两条贯穿始终的规则」）。拒绝要稳，判据的强度就是
# 「服务端又得到了几次机会」——一句 Start-Sleep -Seconds 6 在快机器上放过去七八轮，在慢机器上
# 可能只有一两轮，而两种情形下这条场景都照样绿。等 mapStationReads 涨 4 次，机会次数就是四次，
# 与机器快慢无关；真卡住时报的也是「哪一条判据一直没成立」，不是一个光秃秃的超时。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

$memberships = [int](Invoke-Scalar ("SELECT COUNT(*) AS N FROM JourneyDemands " +
    "WHERE JourneyId = '$journeyId' AND RemovedAt IS NULL")).N
$assertions.Add(
    'L2-ENC-04',
    '那趟旅程仍然只带着一条需求：追加一次都没有发生',
    ($memberships -eq 1),
    1,
    $memberships)

$stops = [int](Invoke-Scalar "SELECT COUNT(*) AS N FROM JourneyStops WHERE JourneyId = '$journeyId'").N
$assertions.Add(
    'L2-ENC-05',
    '停靠仍然是两个：计划一个字都没被改写',
    ($stops -eq 2),
    2,
    $stops)

$accepted = Invoke-Scalar "SELECT AcceptedAt FROM JourneyBacklog WHERE DemandId = '$secondId'"
$assertions.Add(
    'L2-ENC-06',
    '第二条需求没有被受理过：积压行上没有受理时刻，它在等下一辆空闲车',
    ($null -ne $accepted -and ($null -eq $accepted.AcceptedAt -or [string]$accepted.AcceptedAt -eq '')),
    '(无受理时刻)',
    $(if ($null -eq $accepted) { '(无 backlog 行)' }
      elseif ($null -eq $accepted.AcceptedAt) { '(无受理时刻)' } else { [string]$accepted.AcceptedAt }))

# 计划也不该被重发：没有追加就没有该改的东西，多发一版会让车白重放一次。
$plans = [int](Invoke-Scalar ("SELECT COUNT(*) AS N FROM ProtocolOutbox " +
    "WHERE MessageType = 'UpcomingStopPlanSnapshot'")).N
$assertions.Add(
    'L2-ENC-07',
    '计划只发过一版：没有追加，就没有整体重发',
    ($plans -eq 1),
    1,
    $plans)

$journal.Note('本区未配置派车参数时，在途车一条追加都接不到：旅程、停靠、计划三样一个字都没变。')
