#Requires -Version 7

<#
分区归属表是唯一的 AREA 执行白名单：批次 4 出口场景⑥（control-server#72，REQ-0191、REQ-0350）。

分区归属表把每个区域号（AREA）归入一个调度分区，并指派它的机台开哪一侧仓门。批次 4 之前，服务端判断「这条需求
归不归我执行」只看 AREA 是不是 N 开头；它碰巧成立，是因为 map 25 上执行的 11 个 AREA 全是 N 开头，装片机台
（T 开头）一进来就被挡。现在这张表就是白名单：表里没有的 AREA 不执行，而且是**静默**的——共晶、低温共晶这类
AREA 本来就是靠「不配映射」排除在外，它们留在全厂投影里，不该变成告警。

分区归属表由边车 `AreaAssignments` 给出，只含 `N1-3` 与一个 T 开头 AREA；编排器的默认前置（control-server#71）
在服务端就绪后经同一个 `ControlServer.FieldOps.exe` 入库已批准八仓事实、逐车绑定、导入这张表，和现场的路一样。
场景再往假 MesIngest 放三条需求：

- `N1-7`：假 RIoT 默认就有站点 `N1-3_N1-7`，又是 N 开头，旧规则下会被派车。表里没有它，所以始终不受理，积压
  原因 `OUT_OF_SCOPE_AREA`，不形成结构性派车阻断，服务端日志里没有关于它的 Warning 以上记录。
- T 开头 AREA：表里有它，但地图上没有它的站点。它过了白名单，被后面的站点解析挡住——原因不再是
  `OUT_OF_SCOPE_AREA`，这正是要证的。
- `N1-3`：正常受理，冻结行的版本等于受理时的当前版本，路线调度区取自表。

#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$mes = $Context.MesIngest
$connection = $Context.Connection

# 与边车里那张表一致；场景第 1 段读回服务端库里的当前版本，核对导入的正是这张。
$zone = 'MAP-25-WIRE_TO_GATE'
$mappedTArea = 'T5-2'
$serverLog = Join-Path (Split-Path -Parent $Context.SnapshotRoot) 'logs/control-server.out.log'

function New-Demand([string]$label) {
    $guid = [guid]::NewGuid()
    # MesIngest 报不带连字符的 demandId，服务端入口处归一化成规范 UUID：替身按前者发，断言按后者查。
    return [pscustomobject]@{
        Label  = $label
        Wire   = $guid.ToString('N')
        Id     = $guid.ToString('D')
        Sublot = "L2-SUBLOT-$label-$($Context.RunId)"
    }
}

function Publish-Demand([object]$demand, [string]$area, [string]$eqp) {
    $journal.Note("Publishing demand $($demand.Wire) ($($demand.Label), AREA $area) to the fake MesIngest catalog.")
    $null = $mes.Command('Put', "demands/$($demand.Wire)", @{
        sublot      = $demand.Sublot
        area        = $area
        eqp         = $eqp
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

function Get-Backlog([string]$demandId) {
    $rows = @(Invoke-L2Query -Connection $connection -Sql "SELECT ReasonCode, AcceptedAt FROM JourneyBacklog WHERE DemandId = '$demandId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Count([string]$sql) {
    return [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N
}

function Get-AcceptanceRowCount([string]$demandId) {
    return Get-Count @"
SELECT (SELECT COUNT(*) FROM AcceptedDemands WHERE DemandId = '$demandId')
     + (SELECT COUNT(*) FROM JourneyRuntimes WHERE DemandId = '$demandId')
     + (SELECT COUNT(*) FROM OrderIntents WHERE DemandId = '$demandId')
     + (SELECT COUNT(*) FROM ConfigurationConsumerBindings WHERE ConsumerId = '$demandId') AS N
"@
}

<#
服务端日志里 Warning 及以上、提到这条需求的记录。Serilog 控制台格式一条记录以 `[时:分:秒 级别]` 开头，可能跨多行
（异常栈、SQL），所以按记录切分再判，不按行。日志文件此刻仍被服务端写着，用共享读打开。
#>
function Get-WarningRecordsMentioning([string[]]$needles) {
    $stream = [IO.File]::Open($serverLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $text = [IO.StreamReader]::new($stream).ReadToEnd()
    }
    finally {
        $stream.Dispose()
    }
    $matched = [Collections.Generic.List[string]]::new()
    foreach ($record in [regex]::Split($text, '(?m)^(?=\[\d{2}:\d{2}:\d{2} [A-Z]{3}\] )')) {
        if ($record -notmatch '^\[\d{2}:\d{2}:\d{2} (WRN|ERR|FTL)\] ') { continue }
        foreach ($needle in $needles) {
            if ($record.Contains($needle, [StringComparison]::OrdinalIgnoreCase)) {
                $matched.Add($record.Trim())
                break
            }
        }
    }
    return $matched.ToArray()
}

# --- 1. 前置：默认前置导入的分区归属表就是边车给的那张，只含 N1-3 与一个 T 开头 AREA -----------------

$current = & $Context.InvokeFieldOps -Arguments @('area-assignments')
$currentEntries = (@($current.entries | ForEach-Object { "$([string]$_.area)/$([string]$_.dispatchZone)/$([string]$_.slotPosition)" } | Sort-Object)) -join ','
$expectedEntries = (@("N1-3/$zone/FRONT", "$mappedTArea/$zone/REAR") | Sort-Object) -join ','
$versions = Get-Count 'SELECT COUNT(*) AS N FROM DispatchZoneAreaAssignmentVersions'
$assertions.Add(
    'L2-AAU-01', "默认前置按边车导入了分区归属表：只有一版，只含 N1-3 与 $mappedTArea，N1-7 不在表里",
    ([string]$current.outcome -eq 'OK' -and $versions -eq 1 -and $currentEntries -eq $expectedEntries),
    "OK / 1 version / $expectedEntries",
    "$($current.outcome) / $versions version / $currentEntries")

# --- 2. N1-7 与 T 开头 AREA 的需求先上：观察几轮 -----------------------------------------------------

# 先于 N1-3 放：单车一旦受理 N1-3 就不再做派车发现，那之后才出现的需求连积压行都不会有。
$unmapped = New-Demand 'N1-7'
$tArea = New-Demand $mappedTArea
Publish-Demand $unmapped 'N1-7' 'EQP-L2-N17'
Publish-Demand $tArea $mappedTArea 'EQP-L2-T52'

$null = Wait-L2Condition -Description 'both demands were judged and written to the backlog' `
    -Journal $journal -Criterion 'backlog-rows' -TimeoutSeconds 60 `
    -Probe { @($unmapped.Id, $tArea.Id | Where-Object { $null -ne (Get-Backlog $_) }).Count } `
    -Until { param($v) $v -eq 2 }
# 不是一轮的偶然：再让派车循环转几圈，原因仍然是那一个。
$null = Wait-L2Iterations -Riot $Context.Riot -Count 3 -Journal $journal

$unmappedBacklog = Get-Backlog $unmapped.Id
$assertions.Add(
    'L2-AAU-02', 'N1-7（N 开头、地图上有站点，但表里没有）被白名单挡住：积压原因 OUT_OF_SCOPE_AREA，未受理',
    ([string]$unmappedBacklog.ReasonCode -eq 'OUT_OF_SCOPE_AREA' -and
        [string]::IsNullOrEmpty([string]$unmappedBacklog.AcceptedAt) -and
        (Get-AcceptanceRowCount $unmapped.Id) -eq 0),
    'OUT_OF_SCOPE_AREA / not accepted / 0 rows',
    "$($unmappedBacklog.ReasonCode) / AcceptedAt=$($unmappedBacklog.AcceptedAt) / $(Get-AcceptanceRowCount $unmapped.Id) rows")

$tBacklog = Get-Backlog $tArea.Id
$assertions.Add(
    'L2-AAU-03', "$mappedTArea（T 开头、表里有）通过白名单：被后面的判据挡住，原因不再是 OUT_OF_SCOPE_AREA",
    ([string]$tBacklog.ReasonCode -ne 'OUT_OF_SCOPE_AREA' -and -not [string]::IsNullOrEmpty([string]$tBacklog.ReasonCode)),
    'any reason but OUT_OF_SCOPE_AREA (no station on the map: AREA_STATION_NOT_FOUND)',
    [string]$tBacklog.ReasonCode)

# --- 3. N1-3 的需求：正常受理，冻结版本等于当前版本 ----------------------------------------------------

$mapped = New-Demand 'N1-3'
Publish-Demand $mapped 'N1-3' 'EQP-L2-N13'
$stage = Wait-L2Condition -Description 'the N1-3 demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe {
        $rows = @(Invoke-L2Query -Connection $connection -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$($mapped.Id)'")
        if ($rows.Count -eq 0) { $null } else { [string]$rows[0].Stage }
    } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$freeze = @(Invoke-L2Query -Connection $connection -Sql @"
SELECT b.FrozenVersion AS FrozenVersion, b.SnapshotId AS FrozenSnapshotId, v.SnapshotId AS VersionSnapshotId,
       (SELECT MAX(Version) FROM DispatchZoneAreaAssignmentVersions) AS CurrentVersion,
       r.DispatchZone AS DispatchZone
FROM ConfigurationConsumerBindings b
JOIN DispatchZoneAreaAssignmentVersions v ON v.Version = b.FrozenVersion
JOIN JourneyRuntimes r ON r.DemandId = b.ConsumerId
WHERE b.ConsumerKind = 'TransportDemand' AND b.ObjectKind = 'DispatchZoneAreaAssignment' AND b.ConsumerId = '$($mapped.Id)'
"@)
$assertions.Add(
    'L2-AAU-04', 'N1-3 正常受理：冻结行的版本等于当前版本、快照是那一版的快照，路线调度区取自表',
    ($stage -eq 'AwaitingPickupArrival' -and $freeze.Count -eq 1 -and
        [long]$freeze[0].FrozenVersion -eq [long]$freeze[0].CurrentVersion -and
        [long]$freeze[0].FrozenVersion -eq [long]$current.version -and
        [string]$freeze[0].FrozenSnapshotId -eq [string]$freeze[0].VersionSnapshotId -and
        [string]$freeze[0].DispatchZone -eq $zone),
    "AwaitingPickupArrival / frozen v$($current.version) = current / own snapshot / $zone",
    $(if ($freeze.Count -ne 1) { "$stage / $($freeze.Count) freeze rows" } else {
        "$stage / frozen v$($freeze[0].FrozenVersion), current v$($freeze[0].CurrentVersion) / " +
        "$($freeze[0].FrozenSnapshotId -eq $freeze[0].VersionSnapshotId) / $($freeze[0].DispatchZone)" }))

# --- 4. 静默：始终不受理，没有结构性阻断，没有 Warning ------------------------------------------------

$unmappedBacklog = Get-Backlog $unmapped.Id
$assertions.Add(
    'L2-AAU-05', 'N1-3 受理之后，N1-7 仍然一行受理记录、旅程、订单意图、冻结行都没有，积压原因仍是 OUT_OF_SCOPE_AREA',
    ((Get-AcceptanceRowCount $unmapped.Id) -eq 0 -and [string]$unmappedBacklog.ReasonCode -eq 'OUT_OF_SCOPE_AREA'),
    '0 rows / OUT_OF_SCOPE_AREA',
    "$(Get-AcceptanceRowCount $unmapped.Id) rows / $($unmappedBacklog.ReasonCode)")

$blocks = Get-Count 'SELECT COUNT(*) AS N FROM StructuralDispatchBlocks'
$assertions.Add(
    'L2-AAU-06', 'StructuralDispatchBlocks 无行：未映射 AREA 不是结构性派车阻断',
    ($blocks -eq 0), '0', $blocks)

$warnings = @(Get-WarningRecordsMentioning @($unmapped.Id, $unmapped.Wire, 'OUT_OF_SCOPE_AREA'))
$assertions.Add(
    'L2-AAU-07', '服务端日志里没有提到 N1-7 这条需求（两种写法）或 OUT_OF_SCOPE_AREA 的 Warning 及以上记录',
    ($warnings.Count -eq 0 -and (Test-Path -LiteralPath $serverLog)),
    '0 records', $(if ($warnings.Count -eq 0) { '0 records' } else { ($warnings | Select-Object -First 3) -join ' | ' }))

$journal.Note('表里没有的 AREA 静默不执行，表里有的 T 开头 AREA 通过白名单，N1-3 受理并冻结当前版本。')
