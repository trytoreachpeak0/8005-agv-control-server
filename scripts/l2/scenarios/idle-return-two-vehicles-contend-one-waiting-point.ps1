#Requires -Version 7

<#
两台空闲车抢一个等待点：只有一辆形成空闲返回承诺，另一辆什么也没拿到；承诺之后来的搬运不派给它（批次8-18，
control-server#389；REQ-0291、REQ-0292、REQ-0293 前半句）。

**装置**：两台车都停在关卡（站 210，假地图节点 5）。登记两个等待点，但只有 214 在路网上（节点 6，一条边就到）；215 不在任何
节点上，空闲返回判它不可达。setup 文件写了为什么不用白名单造这个局面。没有需求时，任务优先派车什么也不派，两辆车都是「本轮没被
选中」的空闲车，派车轮末尾对它们评估空闲返回。

**第一个事实**：恰好一辆车持有 IDLE_RETURN 用途占有，同一趟（同一个 JourneyId）预占着 214；另一辆既没有占有也没有预占；215 没人
预占。「恰好一辆」是一个要持续成立的状态，不是读一次的值：先等到出现第一条承诺，再用 Wait-L2ConditionOrLast 另等十几秒（十几轮派车）
看有没有出现第二条，最后断言仍是一条。只读一次会在第二辆车还没轮到时误绿。

**第二个事实**：再来一条搬运需求，它派给没承诺的那辆车，不派给已承诺的那辆；已承诺那辆的占有与预占原样留着（不取消、不换点）。
同样另等一段再断言「原样」。

批次8-19（control-server#390）起承诺会被执行：下一轮物化成一趟空闲返回旅程、建开往 214 的单段移动。本场景不驱动 RIoT 执行那张单
（它停在排队），所以车一直在途、承诺与预占原样留着；到点、收敛与离点释放在 waiting-point-exclusive-reserve-occupy-release。
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

$vehicles = @(
    [pscustomobject]@{ AgvId = $Context.AgvId; VehicleKey = $Context.VehicleKey },
    [pscustomobject]@{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
)

# Invoke-L2Query 以 `return , $rows` 返回；这里原样再转一次（`return , (...)`），调用方拿到的仍是那个数组。
# 不在外面包 @()：那会得到单元素数组套数组（three-synthetic-peers 的注释）。
function Get-IdleReturnClaims {
    return , (Invoke-L2Query -Connection $connection -Sql (
        "SELECT VehicleKey, JourneyId, ClaimedAt FROM VehiclePurposeClaims WHERE Purpose = 'IDLE_RETURN' ORDER BY VehicleKey"))
}

function Get-Exclusivities {
    return , (Invoke-L2Query -Connection $connection -Sql (
        "SELECT MapId, StationId, StationKind, State, VehicleKey, JourneyId, WaitingPointVersion FROM StationExclusivities " +
        "ORDER BY StationId"))
}

# --- 1. 两台车都停在关卡，路网拉起来 ----------------------------------------------------------------------

foreach ($vehicle in $vehicles) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey      = $vehicle.VehicleKey
        procState       = 'IDLE'
        movementState   = 'MT_FINISHED'
        speed           = 0
        currentPosition = $Context.GateStationRiotId
    })
}

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

# --- 2. 第一个事实：恰好一辆承诺，预占 214 ------------------------------------------------------------------

$null = Wait-L2Condition -Description 'one idle vehicle committed to an idle return' `
    -Journal $journal -Criterion 'first-commitment' -TimeoutSeconds 120 `
    -Probe { (Get-IdleReturnClaims).Count } `
    -Until { param($v) $v -ge 1 }

# 另等：十几轮派车里有没有出现第二条承诺。正常情形等满超时，拿最后一次读数。
$claimCount = Wait-L2ConditionOrLast -Description 'a second idle return commitment appears (it must not)' `
    -Journal $journal -Criterion 'idle-return-claims-after-window' -TimeoutSeconds 15 `
    -Probe { (Get-IdleReturnClaims).Count } `
    -Until { param($v) $v -ge 2 }
$claims = Get-IdleReturnClaims
$stations = Get-Exclusivities
$journal.Observe('idle-return-commitment',
    (($claims | ForEach-Object { "$($_.VehicleKey)=$($_.JourneyId)" }) -join ' '),
    @{ claims = $claims; stations = $stations })

$assertions.Add(
    'L2-IRC-01',
    '两台空闲车里恰好一辆形成空闲返回承诺（另等十几轮之后仍是一辆）',
    ($claimCount -eq 1 -and $claims.Count -eq 1),
    1,
    "$claimCount / $($claims.Count)")

# 哪一辆承诺了，按 214 的预占持有者认；预占与占有同一次保存，两者一致由 L2-IRC-02 断言。
$committed = if ($stations.Count -ge 1) { $vehicles | Where-Object { $_.VehicleKey -eq [string]$stations[0].VehicleKey } } else { $null }
$other = if ($null -ne $committed) { $vehicles | Where-Object { $_.VehicleKey -ne $committed.VehicleKey } } else { $null }
$committedKey = if ($null -ne $committed) { $committed.VehicleKey } else { '(none)' }
$committedJourney = if ($claims.Count -ge 1) { [string]$claims[0].JourneyId } else { '(none)' }

$assertions.Add(
    'L2-IRC-02',
    '承诺那一辆同一趟预占着 214，是等待点、在途预占；除此之外没有任何站点独占（215 没人预占）',
    ($stations.Count -eq 1 -and [int]$stations[0].StationId -eq 214 -and [string]$stations[0].State -eq 'RESERVED' -and
        [string]$stations[0].StationKind -eq 'WAITING_POINT' -and $claims.Count -eq 1 -and
        [string]$claims[0].VehicleKey -eq $committedKey -and [string]$stations[0].JourneyId -eq $committedJourney),
    "214 RESERVED WAITING_POINT $committedKey $committedJourney",
    (($stations | ForEach-Object { "$($_.StationId) $($_.State) $($_.StationKind) $($_.VehicleKey) $($_.JourneyId)" }) -join '; '))

$otherKey = if ($null -ne $other) { $other.VehicleKey } else { '(none)' }
$otherClaims = Invoke-L2Query -Connection $connection -Sql "SELECT Purpose FROM VehiclePurposeClaims WHERE VehicleKey = '$otherKey'"
$assertions.Add(
    'L2-IRC-03',
    '另一辆没有任何用途占有：它的承诺没形成，也没留下半截',
    ($null -ne $other -and $otherClaims.Count -eq 0),
    0,
    "$otherKey : $($otherClaims.Count)")

# 批次8-19（control-server#390）：承诺在下一轮物化成恰好一趟空闲返回旅程、一张开往 214 的意图，都是那一趟；没有第二份，也没有搬运。
$idleJourneys = Invoke-L2Query -Connection $connection -Sql (
    "SELECT JourneyId, AgvId FROM JourneyRuntimes WHERE JourneyId LIKE 'idle-return:%'")
$transportCount = [int](Invoke-L2Query -Connection $connection -Sql (
    "SELECT COUNT(*) AS N FROM JourneyRuntimes WHERE JourneyId NOT LIKE 'idle-return:%'"))[0].N
$idleIntents = Invoke-L2Query -Connection $connection -Sql (
    "SELECT UpperId, DestinationStationId FROM OrderIntents WHERE Purpose = 'TO_WAITING_POINT'")
$assertions.Add(
    'L2-IRC-04',
    '承诺物化成恰好一趟空闲返回旅程（就是承诺那一趟）与一张开往 214 的意图，没有第二份，也没有搬运旅程',
    ($idleJourneys.Count -eq 1 -and [string]$idleJourneys[0].JourneyId -eq $committedJourney -and $transportCount -eq 0 -and
        $idleIntents.Count -eq 1 -and [int]$idleIntents[0].DestinationStationId -eq 214),
    "1 idle return $committedJourney / 0 transport / 1 intent to 214",
    "$($idleJourneys.Count) idle returns $(($idleJourneys | ForEach-Object { $_.JourneyId }) -join ',') / $transportCount transport / $($idleIntents.Count) intents")

# --- 3. 第二个事实：之后来的搬运不派给已承诺那辆 ------------------------------------------------------------

$demand = [guid]::NewGuid()
$demandId = $demand.ToString('D')
$journal.Note("Publishing demand $demandId after the commitment.")
$null = $mes.Command('Put', "demands/$($demand.ToString('N'))", @{
    sublot = "L2-IRC-$($Context.RunId)"; area = 'N1-3'
    eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})

$takenBy = Wait-L2Condition -Description 'the demand published after the commitment was taken' `
    -Journal $journal -Criterion 'demand-taken-by' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql "SELECT AgvId FROM JourneyRuntimes WHERE DemandId = '$demandId'"
        if ($rows.Count -eq 0) { return $null }
        return [string]$rows[0].AgvId
    } `
    -Until { param($v) $null -ne $v }

$assertions.Add(
    'L2-IRC-05',
    '承诺之后来的搬运派给了没承诺的那辆，不派给已承诺的那辆',
    ($null -ne $other -and $takenBy -eq $other.AgvId),
    $(if ($null -ne $other) { $other.AgvId } else { '(没有另一辆)' }),
    $takenBy)

# 另等：已承诺那辆的占有与预占有没有被取消、换点或换成搬运。它们与受理不在同一次写入里，所以不在受理之后读一次就断言。
$expectedAfterward = "IDLE_RETURN $committedJourney | 214 $committedJourney"
$afterward = Wait-L2ConditionOrLast -Description 'the committed vehicle lost or changed its idle return (it must not)' `
    -Journal $journal -Criterion 'commitment-after-transport' -TimeoutSeconds 10 `
    -Probe {
        $claim = Invoke-L2Query -Connection $connection -Sql (
            "SELECT Purpose, JourneyId FROM VehiclePurposeClaims WHERE VehicleKey = '$committedKey'")
        $held = Invoke-L2Query -Connection $connection -Sql (
            "SELECT StationId, JourneyId FROM StationExclusivities WHERE VehicleKey = '$committedKey'")
        $claimText = if ($claim.Count -eq 1) { "$($claim[0].Purpose) $($claim[0].JourneyId)" } else { "$($claim.Count) claims" }
        $heldText = if ($held.Count -eq 1) { "$($held[0].StationId) $($held[0].JourneyId)" } else { "$($held.Count) stations" }
        return "$claimText | $heldText"
    } `
    -Until { param($v) $v -ne $expectedAfterward }

$assertions.Add(
    'L2-IRC-06',
    '已承诺那辆的空闲返回原样留着：同一趟、同一个点，没被搬运取消、换点或抢走',
    ($afterward -eq $expectedAfterward),
    $expectedAfterward,
    $afterward)

$journal.Note('两辆空闲车抢一个等待点：一辆承诺、一辆什么也没拿到；之后的搬运派给了没承诺的那辆，承诺原样留着。')
