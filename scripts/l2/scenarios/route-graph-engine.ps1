#Requires -Version 7

<#
路网引擎开着跑一趟派车。

证的是引擎在真链路里工作：它把五个 imap 端点读回来、建出快照、判定不陈旧，可达性判据据此
放行，派车照常成功。引擎是派车链路的硬依赖——它不工作，这一趟就不会有单。

**不可达的负例不在这里，在 L1。**合成种子的图是个环，任意两站互相可达，做不出不可达的反例；
真要在 L2 造一个，得给假 RIoT 加一条“把某条边下线”的控制命令，而那属于把装置越造越像被测物。
分层是清楚的：L2 证这条链路真的接通了，L1 证判定本身对不对
（RouteGraphDispatchTests 里可达、不可达、位置未知、陈旧、引擎关闭五种都有）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$mes = $Context.MesIngest
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-RG-$($Context.RunId)"

function Get-Snapshot {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT * FROM RouteGraphSnapshots WHERE MapId = $($Context.MapId)"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# --- 1. 引擎自己先把图拉起来 ---------------------------------------------------------------------

# 派车 worker 每轮先刷新再决策，所以这一条同时证明了刷新真的挂在循环里。
$edgeCount = Wait-L2Condition -Description 'the engine fetched a design state' `
    -Journal $journal -Criterion 'route-graph-design' -TimeoutSeconds 90 `
    -Probe { $s = Get-Snapshot; if ($s) { [int]$s.DesignEdgeCount } else { 0 } } `
    -Until { param($v) $v -gt 0 }

# 设计态、运行态、边组、动态代价与陈旧判定是一次刷新里前后几次保存（RouteGraphRefresher.RefreshOnceAsync），
# 设计态有了不等于后面几样也落库了，而新建的快照行起步就带 SNAPSHOT_NEVER_REFRESHED。所以 L2-RG-03 到 L2-RG-05
# 要读的几样一起等齐再读（control-server#193 普查）；等不到就拿最后一次读到的快照下判据。
$snapshot = Wait-L2ConditionOrLast -Description 'the first refresh cycle finished and cleared the stale reason' `
    -Journal $journal -Criterion 'route-graph-settled' -TimeoutSeconds 30 `
    -Probe { Get-Snapshot } `
    -Until { param($s) $null -ne $s.RuntimeRefreshedAt -and [string]$s.RuntimeRefreshedAt -ne '' -and
        $null -ne $s.EdgeGroupRefreshedAt -and ($null -eq $s.StaleReason -or [string]$s.StaleReason -eq '') }
$assertions.Add(
    'L2-RG-01',
    '设计态读回了种子图的八条有向边',
    ([int]$snapshot.DesignEdgeCount -eq 8),
    8,
    [int]$snapshot.DesignEdgeCount)

$assertions.Add(
    'L2-RG-02',
    '设计态读回了三个站点',
    ([int]$snapshot.DesignStationCount -eq 3),
    3,
    [int]$snapshot.DesignStationCount)

# 运行态是另一条周期，它有没有跑过看的是自己的时刻字段。
$assertions.Add(
    'L2-RG-03',
    '运行态周期也跑过（移除集已读回，空是正常答案）',
    ($null -ne $snapshot.RuntimeRefreshedAt -and [string]$snapshot.RuntimeRefreshedAt -ne ''),
    '(非空)',
    $(if ($null -eq $snapshot.RuntimeRefreshedAt) { '(null)' } else { [string]$snapshot.RuntimeRefreshedAt }))

# map25 一个边组都没有，指纹因此是空串。空串不是「没取过」——那是 EdgeGroupRefreshedAt 为 null。
$assertions.Add(
    'L2-RG-04',
    '边组指纹为空串且已取过一次（合成 RIoT 与 map25 一样没有边组）',
    (([string]$snapshot.EdgeGroupFingerprint -eq '') -and $null -ne $snapshot.EdgeGroupRefreshedAt),
    "'' + 已取过",
    "'$([string]$snapshot.EdgeGroupFingerprint)' + $(if ($null -eq $snapshot.EdgeGroupRefreshedAt) { '未取过' } else { '已取过' })")

$assertions.Add(
    'L2-RG-05',
    '快照不是陈旧态',
    ($null -eq $snapshot.StaleReason -or [string]$snapshot.StaleReason -eq ''),
    '(无陈旧原因)',
    $(if ($null -eq $snapshot.StaleReason) { '(null)' } else { [string]$snapshot.StaleReason }))

# --- 2. 派车照常走通，说明可达性判据放行了 --------------------------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot) with the engine enabled.")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$stage = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$demandId'"
        if ($rows.Count -eq 0) { return $null }
        return [string]$rows[0].Stage
    } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$assertions.Add(
    'L2-RG-06',
    '引擎开着时派车照常走通（可达性判据放行）',
    ($stage -eq 'AwaitingPickupArrival'),
    'AwaitingPickupArrival',
    $stage)

$backlog = Invoke-L2Query -Connection $connection `
    -Sql "SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-RG-07',
    '候选判定结果是 ACCEPTED，没有被任何 ROUTE_GRAPH_* 原因挡住',
    ($backlog.Count -eq 1 -and [string]$backlog[0].ReasonCode -eq 'ACCEPTED'),
    'ACCEPTED',
    $(if ($backlog.Count -eq 1) { [string]$backlog[0].ReasonCode } else { '(无 backlog 行)' }))

$journal.Note('引擎在真链路里工作：双周期都跑过、快照不陈旧、可达性判据放行、派车成立。')
