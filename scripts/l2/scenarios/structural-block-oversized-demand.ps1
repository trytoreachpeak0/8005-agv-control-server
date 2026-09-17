#Requires -Version 7

<#
超大需求的结构性派车阻断（control-server#74，规格 8.3 批次 4 场景③a、8.5；REQ-0210、REQ-0352）。

批次 4 起一条需求只装进它 AREA 指派的那一侧仓位分组，不跨组。于是花篮数超过同图全部车辆该组物理仓位数的需求，换哪辆车、
等多久都装不下，只能人工送。这种需求现场取不到，只能在这里构造，而且不得「不触发即通过」：场景先证明它真的触发了。

边车把 N1-3 指 FRONT。场景按库内车型读出本车 FRONT 组的物理仓位数（已批准八仓是 4），放一条要多一个花篮的 N1-3 需求
（默认模型下 5 个花篮，即 20 盒）。断言：
  - StructuralDispatchBlocks 里恰好一行，原因码 EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP，首次形成时间就是需求第一次被判定
    的那一轮（与积压行的 FirstSeenAt 相同）；
  - 再转几轮，仍是同一行：首次形成时间不变、最近仍成立时间前进，没有第二行；服务端日志里这条需求的形成 Warning 只有一条；
  - 需求始终未受理，没有旅程、没有建 RIoT 单；
  - 从假 MesIngest 删掉这条需求后，告警被清除，表里仍是那一行。

合成装置只有一台车，所以「同图全部车辆」在这里就是这一台；多车里「另一台够就不算」由 L1 的 StructuralDispatchBlockTests 证。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SlotGroups.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection

$group = 'FRONT'
$reasonCode = 'EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP'
# L2-PACKAGE 每篮 4 盒。
$boxesPerBasket = 4
$serverLog = Join-Path (Split-Path -Parent $Context.SnapshotRoot) 'logs/control-server.out.log'

function Get-Count([string]$Sql) {
    return [int](Invoke-L2Query -Connection $connection -Sql $Sql)[0].Total
}

function ConvertTo-Instant([object]$Value) {
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Blocks([string]$DemandId) {
    return , @(Get-L2StructuralDispatchBlock -Connection $connection -DemandId $DemandId -IncludeCleared)
}

<#
服务端日志里 Warning 及以上、同时提到全部 needles 的记录。Serilog 控制台格式一条记录以 `[时:分:秒 级别]` 开头，可能跨
多行，所以按记录切分再判。日志文件此刻仍被服务端写着，用共享读打开。
#>
function Get-WarningRecords([string[]]$Needles) {
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
        if (@($Needles | Where-Object { -not $record.Contains($_, [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0) {
            $matched.Add($record.Trim())
        }
    }
    return , $matched.ToArray()
}

# --- 0. 前置：本车 FRONT 组的物理仓位数，需求比它多一个花篮 ------------------------------------------------

$positions = Get-L2VehicleSlotPositions -Connection $connection -AgvId $Context.AgvId
if ($null -eq $positions) { throw "Vehicle $($Context.AgvId) has no resolvable slot model; the preseed did not bind it." }
$groupSlots = @($positions.Positions.GetEnumerator() | Where-Object { $_.Value -ceq $group } | ForEach-Object { [int]$_.Key })
$basketCount = $groupSlots.Count + 1
$maxBoxCount = $basketCount * $boxesPerBasket
$assertions.Add(
    'L2-SBO-01', "前置：本车 $group 组有物理仓位，需求要 $basketCount 个花篮，比该组物理仓位数多一个（默认模型下 5 个）",
    ($groupSlots.Count -ge 1 -and $basketCount -gt $groupSlots.Count),
    "$group physical slots < $basketCount baskets",
    "$group physical slots $($groupSlots.Count), baskets $basketCount ($maxBoxCount boxes)")

# --- 1. 放需求：第一轮就形成一条阻断 ---------------------------------------------------------------------

$guid = [guid]::NewGuid()
$wireId = $guid.ToString('N')
$demandId = $guid.ToString('D')
$journal.Note("Publishing demand $wireId (area N1-3, $maxBoxCount boxes = $basketCount baskets).")
$null = $mes.Command('Put', "demands/$wireId", @{
    sublot      = "L2-SBO-$($Context.RunId)"
    area        = 'N1-3'
    eqp         = 'EQP-L2-SBO-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = $maxBoxCount
})

$null = Wait-L2Condition -Description 'a structural dispatch block was raised for the oversized demand' `
    -Journal $journal -Criterion 'structural-block' -TimeoutSeconds 90 `
    -Probe { (Get-Blocks $demandId).Count } -Until { param($v) $v -ge 1 }

$first = Get-Blocks $demandId
$backlog = Get-L2JourneyBacklogRow -Connection $connection -DemandId $demandId
$backlogReason = if ($backlog) { [string]$backlog.ReasonCode } else { '(no backlog row)' }
$assertions.Add(
    'L2-SBO-02', "StructuralDispatchBlocks 恰好一行，原因码 $reasonCode，仍成立；积压原因同码",
    ($first.Count -eq 1 -and [string]$first[0].ReasonCode -ceq $reasonCode -and
        [string]::IsNullOrEmpty([string]$first[0].ClearedAt) -and $backlogReason -ceq $reasonCode),
    "1 row / $reasonCode / uncleared / backlog $reasonCode",
    $(if ($first.Count -eq 0) { "0 rows / backlog $backlogReason" } else {
        "$($first.Count) rows / $($first[0].ReasonCode) / ClearedAt=$($first[0].ClearedAt) / backlog $backlogReason" }))

$firstRaisedAt = ConvertTo-Instant $first[0].FirstRaisedAt
$firstSeenAt = if ($backlog) { ConvertTo-Instant $backlog.FirstSeenAt } else { $null }
$assertions.Add(
    'L2-SBO-03', '首次形成时间就是需求出现后的第一轮：等于积压行首次被判定的时间',
    ($null -ne $firstSeenAt -and $firstRaisedAt -eq $firstSeenAt),
    "FirstRaisedAt = backlog FirstSeenAt ($firstSeenAt)", "FirstRaisedAt $firstRaisedAt")

$detail = [string]$first[0].DetailJson | ConvertFrom-Json
$assertions.Add(
    'L2-SBO-04', "明细记下花篮数 $basketCount、分组 $group 与全车队该组最大物理仓位数 $($groupSlots.Count)",
    ([int]$detail.expectedBasketCount -eq $basketCount -and [string]$detail.slotPosition -ceq $group -and
        [int]$detail.largestPhysicalSlotCount -eq $groupSlots.Count),
    "$basketCount / $group / $($groupSlots.Count)",
    "$($detail.expectedBasketCount) / $($detail.slotPosition) / $($detail.largestPhysicalSlotCount)")

# --- 2. 持续成立：同一行，只有最近仍成立时间前进 ----------------------------------------------------------

$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$held = Get-Blocks $demandId
$lastSeenBefore = ConvertTo-Instant $first[0].LastSeenAt
$heldSummary = if ($held.Count -eq 0) { '0 rows' } else {
    "$($held.Count) rows / FirstRaisedAt $(ConvertTo-Instant $held[0].FirstRaisedAt) / LastSeenAt $(ConvertTo-Instant $held[0].LastSeenAt) / ClearedAt=$($held[0].ClearedAt)" }
$assertions.Add(
    'L2-SBO-05', '再转三轮仍是同一行：首次形成时间不变、最近仍成立时间前进、没有第二行、未清除',
    ($held.Count -eq 1 -and (ConvertTo-Instant $held[0].FirstRaisedAt) -eq $firstRaisedAt -and
        (ConvertTo-Instant $held[0].LastSeenAt) -gt $lastSeenBefore -and [string]::IsNullOrEmpty([string]$held[0].ClearedAt)),
    "1 row / FirstRaisedAt $firstRaisedAt / LastSeenAt > $lastSeenBefore / uncleared",
    $heldSummary)

$raisedWarnings = Get-WarningRecords @('Structural dispatch block raised', $demandId)
$assertions.Add(
    'L2-SBO-06', '服务端日志里这条需求的「形成」Warning 只有一条：刷新不重复写',
    ($raisedWarnings.Count -eq 1 -and (Test-Path -LiteralPath $serverLog)),
    '1 record', "$($raisedWarnings.Count) records")

$accepted = Get-Count "SELECT COUNT(*) AS Total FROM AcceptedDemands WHERE DemandId = '$demandId'"
$journeys = Get-Count "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE DemandId = '$demandId'"
$riotOrders = @($riot.Snapshot().body.orders).Count
$assertions.Add(
    'L2-SBO-07', '不派车、不部分装入：没有受理行、没有旅程、没有建 RIoT 单',
    ($accepted -eq 0 -and $journeys -eq 0 -and $riotOrders -eq 0),
    'AcceptedDemands 0, JourneyRuntimes 0, RIoT orders 0',
    "AcceptedDemands $accepted, JourneyRuntimes $journeys, RIoT orders $riotOrders")

# --- 3. 需求离开目录：告警清除 -----------------------------------------------------------------------

$journal.Note("Deleting demand $wireId from the fake MesIngest catalog.")
$null = $mes.Command('Delete', "demands/$wireId", @{})
$null = Wait-L2Condition -Description 'the structural dispatch block was cleared once the demand left the catalog' `
    -Journal $journal -Criterion 'structural-block-cleared' -TimeoutSeconds 90 `
    -Probe { @(Get-Blocks $demandId | Where-Object { -not [string]::IsNullOrEmpty([string]$_.ClearedAt) }).Count } `
    -Until { param($v) $v -ge 1 }

$cleared = Get-Blocks $demandId
$uncleared = @(Get-L2StructuralDispatchBlock -Connection $connection -DemandId $demandId)
$accepted = Get-Count "SELECT COUNT(*) AS Total FROM AcceptedDemands WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-SBO-08', '删掉需求后告警清除：表里仍只有那一行、已记清除时间、首次形成时间不变，需求从未受理',
    ($cleared.Count -eq 1 -and $uncleared.Count -eq 0 -and -not [string]::IsNullOrEmpty([string]$cleared[0].ClearedAt) -and
        (ConvertTo-Instant $cleared[0].FirstRaisedAt) -eq $firstRaisedAt -and $accepted -eq 0),
    '1 row / cleared / same FirstRaisedAt / never accepted',
    $(if ($cleared.Count -eq 0) { '0 rows' } else {
        "$($cleared.Count) rows / ClearedAt=$($cleared[0].ClearedAt) / FirstRaisedAt $(ConvertTo-Instant $cleared[0].FirstRaisedAt) / accepted $accepted" }))

$journal.Note('超大需求立即形成一条结构性派车阻断，持续成立只刷新，需求离开目录后清除，始终未派车。')
