#Requires -Version 7

<#
引擎陈旧态的三种触发，各一份 fail-closed 证据。

`route-graph-engine` 证的是引擎接通了、快照不陈旧、派车放行。这一条证反面：三种触发各来一次，
每一次都能在服务端自己的库里读到具体是哪一条，并且**本轮不派车**。规格 5.6 的兜底与 8.3 的
出口要的都是这个——「陈旧即不派车」如果没有一条真的把它逼陈旧的运行，就只是一句注释。

三条的顺序不能换，理由是它们的可清除性不同：

| 触发 | 清除 |
| --- | --- |
| 超 TTL（运行态过期） | 下一次及时的刷新自己就清掉 |
| 动态代价由空变非空 | 不清。等待改变不了「图已经不是路由的完整说明」 |
| 边组指纹变化 | 不清，同上 |

所以先做能清的那条，再做两条不能清的；后一条把前一条的原因覆盖掉，正好逐条读得到。反过来
排，第一条黏住之后剩下两条永远读不出来。

**需求在第一次陈旧成立之后才发布。**它必须一直是候选才能一路带着阻断原因走完三条；一旦快照
恢复可用，它会被立刻受理并派车，后两条就没有对象了。这条场景因此全程没有一台车上路——那正是
fail-closed 的样子。
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
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')

function Get-Snapshot {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT * FROM RouteGraphSnapshots WHERE MapId = $($Context.MapId)"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-StaleReason {
    $snapshot = Get-Snapshot
    if ($null -eq $snapshot) { return $null }
    if ($null -eq $snapshot.StaleReason) { return '' }
    return [string]$snapshot.StaleReason
}

function Get-BacklogReason {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].ReasonCode
}

# --- 0. 先有一张健康的快照，三种触发才有「变化」可言 -----------------------------------------------

# 指纹与动态代价都是与**上一次**比出来的：首次取到的空指纹不算变化，首次读到的空代价不算「由空
# 变非空」。所以基线必须先建立，否则引擎会在第一个 tick 就陈旧，而那是另一回事。
$null = Wait-L2Condition -Description 'the engine took its first complete snapshot' `
    -Journal $journal -Criterion 'route-graph-baseline' -TimeoutSeconds 120 `
    -Probe {
        $snapshot = Get-Snapshot
        if ($null -eq $snapshot) { return $null }
        if ([int]$snapshot.DesignEdgeCount -le 0) { return $null }
        if ($null -eq $snapshot.EdgeGroupRefreshedAt) { return $null }
        if ($null -eq $snapshot.DynamicRouteCostObservedAt) { return $null }
        return 'ready'
    } `
    -Until { param($v) $v -eq 'ready' }

# 陈旧原因是在动态代价那次保存之后、由 EvaluateStalenessAsync 另外清掉的（RouteGraphRefresher.RefreshOnceAsync），
# 上面等齐的三样落库时它可能还是新行起步的 SNAPSHOT_NEVER_REFRESHED，所以再等它清掉（control-server#193 普查）。
$baseline = Wait-L2ConditionOrLast -Description 'the first complete snapshot is not stale' `
    -Journal $journal -Criterion 'route-graph-baseline-fresh' -TimeoutSeconds 30 `
    -Probe { Get-Snapshot } `
    -Until { param($s) $null -eq $s.StaleReason -or [string]$s.StaleReason -eq '' }
$assertions.Add(
    'L2-RGS-01',
    '基线快照建立：设计态、边组指纹、动态代价都取过一次，且不陈旧',
    ($null -eq $baseline.StaleReason -or [string]$baseline.StaleReason -eq ''),
    '(无陈旧原因)',
    $(if ($null -eq $baseline.StaleReason) { '(null)' } else { [string]$baseline.StaleReason }))

$assertions.Add(
    'L2-RGS-02',
    '边组指纹是空串而不是「没取过」（合成 RIoT 与 map25 一样一个边组都没有）',
    (([string]$baseline.EdgeGroupFingerprint -eq '') -and $null -ne $baseline.EdgeGroupRefreshedAt),
    "'' + 已取过",
    "'$([string]$baseline.EdgeGroupFingerprint)' + $(if ($null -eq $baseline.EdgeGroupRefreshedAt) { '未取过' } else { '已取过' })")

# --- 1. 超 TTL：刷新自身慢过运行态预算 -------------------------------------------------------------

$journal.Note('Slowing the fake RIoT so one refresh outlives the runtime budget.')
$null = $riot.Command('Put', 'faults/http', @{ mode = 'Delay'; delayMs = 2000 })

$ttlReason = Wait-L2Condition -Description 'the runtime state aged out inside a single refresh' `
    -Journal $journal -Criterion 'stale-reason' -TimeoutSeconds 180 `
    -Probe { Get-StaleReason } `
    -Until { param($v) $v -eq 'ROUTE_GRAPH_RUNTIME_STATE_EXPIRED' }

$assertions.Add(
    'L2-RGS-03',
    '触发一（超 TTL）：快照进陈旧态，原因是 ROUTE_GRAPH_RUNTIME_STATE_EXPIRED',
    ($ttlReason -eq 'ROUTE_GRAPH_RUNTIME_STATE_EXPIRED'),
    'ROUTE_GRAPH_RUNTIME_STATE_EXPIRED',
    $ttlReason)

# 现在才发需求：从这里到场景结束，快照一直不可用，它因此一直是候选。
$journal.Note("Publishing demand $demandIdWire against a stale graph.")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = "L2-RGS-$($Context.RunId)"
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$ttlBacklog = Wait-L2Condition -Description 'the demand was blocked by the stale graph' `
    -Journal $journal -Criterion 'backlog-reason' -TimeoutSeconds 180 `
    -Probe { Get-BacklogReason } `
    -Until { param($v) $v -eq 'ROUTE_GRAPH_RUNTIME_STATE_EXPIRED' }

$assertions.Add(
    'L2-RGS-04',
    '触发一 fail-closed：候选被挡住，原因直指引擎而不是笼统的「无候选」',
    ($ttlBacklog -eq 'ROUTE_GRAPH_RUNTIME_STATE_EXPIRED'),
    'ROUTE_GRAPH_RUNTIME_STATE_EXPIRED',
    $ttlBacklog)

# --- 2. 动态代价由空变非空 ------------------------------------------------------------------------

# 趁延迟还开着切过去：两条都成立时 EvaluateStalenessAsync 报的是黏住的那条，也就是这一条。
# 顺序因此是确定的，不靠抢时间。
$journal.Note('RIoT starts reporting a dynamic route cost.')
$null = $riot.Command('Put', 'dynamic-route-costs', @{ costs = @{ '25:12' = 18000.0 } })

$dynamicReason = Wait-L2Condition -Description 'the snapshot went stale on dynamic route cost' `
    -Journal $journal -Criterion 'stale-reason' -TimeoutSeconds 180 `
    -Probe { Get-StaleReason } `
    -Until { param($v) $v -eq 'ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED' }

$assertions.Add(
    'L2-RGS-05',
    '触发二（动态代价由空变非空）：原因是 ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED',
    ($dynamicReason -eq 'ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED'),
    'ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED',
    $dynamicReason)

# 延迟已经没用了：这一条不会因为刷新变快而清掉，正是它与上一条的区别。撤掉延迟也让剩下的断言
# 跑得快，并且顺带证了「变快也不解除」。
$journal.Note('Removing the delay; the dynamic-cost block is not the kind that waiting fixes.')
$null = $riot.Command('Put', 'faults/http', @{ mode = 'Normal' })

$null = Wait-L2Iterations -Riot $riot -Count 3 -TimeoutSeconds 180 -Journal $journal
$stillStale = Get-StaleReason
$assertions.Add(
    'L2-RGS-06',
    '刷新恢复正常之后它仍然陈旧：动态代价这条不因等待而解除',
    ($stillStale -eq 'ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED'),
    'ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED',
    $stillStale)

$dynamicBacklog = Wait-L2Condition -Description 'the demand is now blocked on the dynamic cost' `
    -Journal $journal -Criterion 'backlog-reason' -TimeoutSeconds 180 `
    -Probe { Get-BacklogReason } `
    -Until { param($v) $v -eq 'ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED' }

$assertions.Add(
    'L2-RGS-07',
    '触发二 fail-closed：阻断原因跟着换成这一条',
    ($dynamicBacklog -eq 'ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED'),
    'ROUTE_GRAPH_DYNAMIC_ROUTE_COST_APPEARED',
    $dynamicBacklog)

# --- 3. 边组指纹变化 ------------------------------------------------------------------------------

$journal.Note('An edge group appears on the Map under the graph.')
$null = $riot.Command('Put', 'edge-groups', @{
    mapId  = $Context.MapId
    groups = @{ 'L2-ELEVATOR' = @(3, 4) }
})

$fingerprintReason = Wait-L2Condition -Description 'the snapshot went stale on the fingerprint change' `
    -Journal $journal -Criterion 'stale-reason' -TimeoutSeconds 180 `
    -Probe { Get-StaleReason } `
    -Until { param($v) $v -eq 'ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED' }

$assertions.Add(
    'L2-RGS-08',
    '触发三（边组指纹变化）：原因是 ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED',
    ($fingerprintReason -eq 'ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED'),
    'ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED',
    $fingerprintReason)

$changed = Get-Snapshot
$assertions.Add(
    'L2-RGS-09',
    '指纹确实换了值，而不是只翻了个标志位',
    ([string]$changed.EdgeGroupFingerprint -ne [string]$baseline.EdgeGroupFingerprint -and
        [string]$changed.EdgeGroupFingerprint -ne ''),
    "≠ '$([string]$baseline.EdgeGroupFingerprint)'",
    "'$([string]$changed.EdgeGroupFingerprint)'")

$fingerprintBacklog = Wait-L2Condition -Description 'the demand is now blocked on the fingerprint change' `
    -Journal $journal -Criterion 'backlog-reason' -TimeoutSeconds 180 `
    -Probe { Get-BacklogReason } `
    -Until { param($v) $v -eq 'ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED' }

$assertions.Add(
    'L2-RGS-10',
    '触发三 fail-closed：阻断原因跟着换成这一条',
    ($fingerprintBacklog -eq 'ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED'),
    'ROUTE_GRAPH_EDGE_GROUP_FINGERPRINT_CHANGED',
    $fingerprintBacklog)

# --- 4. 三条陈旧下来，什么都没被派出去 -------------------------------------------------------------

# 「本轮不派车」如果只看阻断原因，证不到位：原因写对了但单还是建了，才是真正贵的那种缺陷。
$runtimes = Invoke-L2Query -Connection $connection -Sql 'SELECT * FROM JourneyRuntimes'
$assertions.Add(
    'L2-RGS-11',
    '三条陈旧全程没有建出任何 journey',
    ($runtimes.Count -eq 0),
    0,
    $runtimes.Count)

$intents = Invoke-L2Query -Connection $connection -Sql 'SELECT * FROM OrderIntents'
$assertions.Add(
    'L2-RGS-12',
    '一张 RIoT move 单都没建',
    ($intents.Count -eq 0),
    0,
    $intents.Count)

$accepted = Invoke-L2Query -Connection $connection -Sql 'SELECT * FROM AcceptedDemands'
$assertions.Add(
    'L2-RGS-13',
    '需求始终没有被受理',
    ($accepted.Count -eq 0),
    0,
    $accepted.Count)

$journal.Note('三种陈旧触发各取到一次，每一次都能追到具体哪一条，且三次都没派出任何一趟。')
