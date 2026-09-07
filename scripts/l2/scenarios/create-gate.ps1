#Requires -Version 7

<#
建单前置门禁在真链路里工作。

证四件事：目录被完整确认过一次并因此新鲜（`REQ-0302`）；建单那一刻两个端点被冻结在当时的目录
修订上（`REQ-0305`）；门禁真的调了 `getRouteCostsBy`，两个证据源分别落进审计行且各叫各的名字
（`REQ-0207` 与 `CP-0001`）；这一趟照常派出去。

**未批准的负例不在这里，在 `create-gate-unapproved`。**那条是规格 8.6 要的硬阻断负向证据：
把两个参数拿掉，证明它真的什么都不建。分歧阻断的负例在 L1（`CreateGateTests`）——L2 造分歧要
给假 RIoT seed 一个与自建图相反的答案，那条链路本身已经由本场景证过了。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$mes = $Context.MesIngest
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-CG-$($Context.RunId)"

# --- 1. 目录先被确认一次 ---------------------------------------------------------------------

# 派车 worker 每轮先读目录再决策，读全了才算一次完整确认。这一条同时证明了确认真的挂在循环里。
$confirmedAt = Wait-L2Condition -Description 'the Map/Station catalog was completely confirmed' `
    -Journal $journal -Criterion 'catalog-confirmation' -TimeoutSeconds 90 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT * FROM MapStationCatalogStates WHERE MapId = $($Context.MapId)"
        if ($rows.Count -eq 0) { return '' }
        return [string]$rows[0].LastCompleteConfirmationAt
    } `
    -Until { param($v) $v -ne '' }

$catalogState = (Invoke-L2Query -Connection $connection `
    -Sql "SELECT * FROM MapStationCatalogStates WHERE MapId = $($Context.MapId)")[0]

$assertions.Add(
    'L2-CG-01',
    '目录处于 Fresh 且有一次完整确认',
    ([string]$catalogState.State -eq 'Fresh' -and $confirmedAt -ne ''),
    'Fresh + 已确认',
    "$([string]$catalogState.State) + $confirmedAt")

# 两个批准值随状态一起留痕：事后能核对这条状态是按哪两个值判的。
$assertions.Add(
    'L2-CG-02',
    '判定所依据的两个已批准值被一并记下（30 秒周期 / 300 秒最大未确认）',
    ([int]$catalogState.ApprovedSyncPeriodSeconds -eq 30 -and
     [int]$catalogState.ApprovedMaxUnconfirmedSeconds -eq 300),
    '30 / 300',
    "$([int]$catalogState.ApprovedSyncPeriodSeconds) / $([int]$catalogState.ApprovedMaxUnconfirmedSeconds)")

# --- 2. 建单，看门禁与冻结 -----------------------------------------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot) with the create gate in the chain.")
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
    'L2-CG-03',
    '门禁在链路里放行，派车照常走通',
    ($stage -eq 'AwaitingPickupArrival'),
    'AwaitingPickupArrival',
    $stage)

# --- 3. REQ-0305：两个端点被冻结 ------------------------------------------------------------------

$frozen = Invoke-L2Query -Connection $connection `
    -Sql "SELECT * FROM FrozenDemandStations WHERE DemandId = '$demandId' ORDER BY Role"

$assertions.Add(
    'L2-CG-04',
    '取货端与卸货端两个端点都被冻结',
    ($frozen.Count -eq 2),
    2,
    $frozen.Count)

if ($frozen.Count -eq 2) {
    $pickup = $frozen | Where-Object { [string]$_.Role -eq 'Pickup' }
    $dropoff = $frozen | Where-Object { [string]$_.Role -eq 'Dropoff' }
    $assertions.Add(
        'L2-CG-05',
        '冻结的端点带着当时的目录修订（不是 0）',
        ([long]$pickup.CatalogRevision -ne 0),
        '非 0',
        [string]$pickup.CatalogRevision)
    $assertions.Add(
        'L2-CG-06',
        '卸货端冻的是关卡站',
        ([int]$dropoff.StationId -eq $Context.GateStationRiotId),
        [string]$Context.GateStationRiotId,
        [string]$dropoff.StationId)
}

# --- 4. 两个证据源各自具名地进了审计 ---------------------------------------------------------------

$audit = Invoke-L2Query -Connection $connection `
    -Sql "SELECT * FROM CreateGateAudit WHERE DemandId = '$demandId'"

$assertions.Add(
    'L2-CG-07',
    '门禁写了一条 Allowed 审计（同一裁决不重复写，所以只有一条）',
    ($audit.Count -eq 1 -and [string]$audit[0].Verdict -eq 'Allowed'),
    '1 条 Allowed',
    $(if ($audit.Count -eq 0) { '(无审计行)' } else { "$($audit.Count) 条 / $([string]$audit[0].Verdict)" }))

if ($audit.Count -eq 1) {
    # RIoT 的答案叫 RouteCost，自建图的答案叫 GraphTraversalCost，两列都非空——门禁真的把两个源
    # 放在一起看过了，而不是只问了一个。
    $assertions.Add(
        'L2-CG-08',
        'RIoT 的 RouteCost 与自建图的遍历代价分列两栏，都有值',
        ($null -ne $audit[0].RiotRouteCostMm -and [string]$audit[0].RiotRouteCostMm -ne '' -and
         $null -ne $audit[0].GraphTraversalCostMm -and [string]$audit[0].GraphTraversalCostMm -ne ''),
        '两栏都有值',
        "RIoT=$([string]$audit[0].RiotRouteCostMm) / Graph=$([string]$audit[0].GraphTraversalCostMm)")

    $assertions.Add(
        'L2-CG-09',
        '两个源都说可达，没有分歧要处置',
        ([long]$audit[0].RiotRouteCostMm -ge 0 -and [int]$audit[0].GraphReachable -eq 1 -and
         ($null -eq $audit[0].ConflictDetail -or [string]$audit[0].ConflictDetail -eq '')),
        '可达 + 可达 + 无分歧',
        "RIoT=$([long]$audit[0].RiotRouteCostMm) / GraphReachable=$([int]$audit[0].GraphReachable) / Conflict=$([string]$audit[0].ConflictDetail)")
}

# --- 5. REQ-0308 的展示面：正式 API 读得回来 -------------------------------------------------------

# 「在 ControlServer 向维护管理员和系统管理员展示」不是一句期望——读一次正式端点，看它答的
# 就是那一行按原因去重的目录级状态。
$displayed = Invoke-RestMethod -Method Get -Uri "http://127.0.0.1:$($Context.HealthPort)/api/runtime/catalog-availability"
$displayedForMap = @($displayed | Where-Object { [int]$_.mapId -eq $Context.MapId })

$assertions.Add(
    'L2-CG-10',
    '目录级状态经正式 API 读得回来，一张图一行（按原因去重，不是每轮一条）',
    ($displayedForMap.Count -eq 1 -and [string]$displayedForMap[0].state -eq 'Fresh'),
    '1 行 Fresh',
    $(if ($displayedForMap.Count -eq 0) { '(无行)' } else { "$($displayedForMap.Count) 行 / $([string]$displayedForMap[0].state)" }))

$journal.Note('建单前置门禁在真链路里工作：目录被完整确认、端点被冻结、两个证据源分别落进审计。')
