#Requires -Version 7

<#
仓位临时禁用不形成结构性派车阻断（control-server#74，规格 8.3 批次 4 场景③b；REQ-0352、REQ-0210）。

这是负向证据。REQ-0352 说「仓位临时禁用造成的不足不属于此情形」：禁用的仓只是暂时不能用，解禁就能装，所以它是正常积压，
不能像超大需求那样立即告警、叫人改人工送。

握手种子把 1、2 号仓的管理可用性报成 DISABLED，N1-3 指 FRONT，放一条要 3 个花篮的 N1-3 需求。本车 FRONT 组物理上有
4 个仓，装得下 3 个；但去掉禁用的两个只剩 2 个可用。断言：
  - 前置：1、2 号仓正是本车 FRONT 组的，服务端算出的 FRONT 组可用仓只剩 2 个，而该组物理仓位数够装 3 个花篮；
  - 积压原因是「所需分组暂时空仓不足」（SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE），再转几轮仍是；
  - 不受理：没有受理行、没有旅程、没有建 RIoT 单；
  - StructuralDispatchBlocks 没有这条需求的行（含已清除的），服务端日志里没有关于它的「形成」Warning。
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
$disabledSlots = @(1, 2)
$basketCount = 3
# L2-PACKAGE 每篮 4 盒。
$maxBoxCount = $basketCount * 4
$waitingReason = 'SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE'
$serverLog = Join-Path (Split-Path -Parent $Context.SnapshotRoot) 'logs/control-server.out.log'

function Get-Count([string]$Sql) {
    return [int](Invoke-L2Query -Connection $connection -Sql $Sql)[0].Total
}

# --- 0. 前置：禁用的正是 FRONT 组的仓，组内可用仓不够、物理仓位数够 ----------------------------------------

$positions = Get-L2VehicleSlotPositions -Connection $connection -AgvId $Context.AgvId
if ($null -eq $positions) { throw "Vehicle $($Context.AgvId) has no resolvable slot model; the preseed did not bind it." }
$groupSlots = @($positions.Positions.GetEnumerator() | Where-Object { $_.Value -ceq $group } | ForEach-Object { [int]$_.Key } | Sort-Object)
$available = Get-L2AvailableSlots -Connection $connection -AgvId $Context.AgvId
$groupAvailable = @($available | Where-Object { $groupSlots -contains $_ })
$disabledInGroup = @($disabledSlots | Where-Object { $groupSlots -contains $_ })
$assertions.Add(
    'L2-SGD-01', "前置：禁用的 [$($disabledSlots -join ',')] 属于本车 $group 组；组内可用仓少于 $basketCount 个，组的物理仓位数不少于 $basketCount 个",
    ($disabledInGroup.Count -eq $disabledSlots.Count -and $groupAvailable.Count -lt $basketCount -and
        $groupSlots.Count -ge $basketCount -and @($groupAvailable | Where-Object { $disabledSlots -contains $_ }).Count -eq 0),
    "$group physical [$($groupSlots -join ',')] >= $basketCount, available in group < $basketCount, disabled not available",
    "$group physical [$($groupSlots -join ',')], available in group [$($groupAvailable -join ',')], all available [$($available -join ',')]")

# --- 1. 需求等在「所需分组暂时空仓不足」 ----------------------------------------------------------------

$guid = [guid]::NewGuid()
$demandId = $guid.ToString('D')
$journal.Note("Publishing demand $($guid.ToString('N')) (area N1-3, $maxBoxCount boxes = $basketCount baskets).")
$null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
    sublot      = "L2-SGD-$($Context.RunId)"
    area        = 'N1-3'
    eqp         = 'EQP-L2-SGD-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = $maxBoxCount
})

$reason = Wait-L2Condition -Description "the demand is held back as $waitingReason" `
    -Journal $journal -Criterion 'backlog-reason' -TimeoutSeconds 90 `
    -Probe { $row = Get-L2JourneyBacklogRow -Connection $connection -DemandId $demandId; if ($row) { [string]$row.ReasonCode } else { $null } } `
    -Until { param($v) $v -ceq $waitingReason }
# A few more rounds, so both "not accepted" and "no block" are a steady state rather than the first round's verdict.
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal

$backlog = Get-L2JourneyBacklogRow -Connection $connection -DemandId $demandId
$reasonAfterRounds = if ($backlog) { [string]$backlog.ReasonCode } else { '(no backlog row)' }
$assertions.Add(
    'L2-SGD-02', '积压原因为「所需分组暂时空仓不足」，再过三轮仍是',
    ($reason -ceq $waitingReason -and $reasonAfterRounds -ceq $waitingReason),
    $waitingReason, "first $reason, after three rounds $reasonAfterRounds")

$accepted = Get-Count "SELECT COUNT(*) AS Total FROM AcceptedDemands WHERE DemandId = '$demandId'"
$journeys = Get-Count "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE DemandId = '$demandId'"
$riotOrders = @($riot.Snapshot().body.orders).Count
$assertions.Add(
    'L2-SGD-03', '不受理：没有受理行、没有旅程、没有建 RIoT 单',
    ($accepted -eq 0 -and $journeys -eq 0 -and $riotOrders -eq 0),
    'AcceptedDemands 0, JourneyRuntimes 0, RIoT orders 0',
    "AcceptedDemands $accepted, JourneyRuntimes $journeys, RIoT orders $riotOrders")

# --- 2. 负向证据：没有结构性派车阻断，也没有告警 -----------------------------------------------------------

$blocks = @(Get-L2StructuralDispatchBlock -Connection $connection -DemandId $demandId -IncludeCleared)
$assertions.Add(
    'L2-SGD-04', 'StructuralDispatchBlocks 没有这条需求的行（含已清除的）：仓位临时禁用不是结构性阻断',
    ($blocks.Count -eq 0), 0, $blocks.Count)

$stream = [IO.File]::Open($serverLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
try {
    $log = [IO.StreamReader]::new($stream).ReadToEnd()
}
finally {
    $stream.Dispose()
}
$raised = [regex]::Matches($log, 'Structural dispatch block raised[^\r\n]*' + [regex]::Escape($demandId)).Count
$assertions.Add(
    'L2-SGD-05', '服务端日志里没有这条需求的「结构性派车阻断形成」记录',
    ($raised -eq 0 -and (Test-Path -LiteralPath $serverLog)), '0 records', "$raised records")

$journal.Note('仓位临时禁用造成的不足只进正常积压，不受理、不形成结构性派车阻断、不告警。')
