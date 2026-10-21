#Requires -Version 7

<#
目录时间窗（control-server#827；REQ-0366，CP-0020）：服务端出厂的 30 分钟，一台空闲车，两条 WIRE_TO_GATE。

  1. STALE：MesIngest 首次读到（假 MesIngest 的 createdAt）在 40 分钟前。车空着，它被判过、不派：
     - L2-CAW-01：积压原因 DEMAND_OLDER_THAN_CATALOG_WINDOW、没有旅程、没有订单意图；
     - L2-CAW-02：/api/dashboard/dispatch-backlog 照样列出它，原因码带中文说明（不像 DEMAND_LEFT_CATALOG 那样消失）。
  2. FRESH：首次读到在 1 分钟前。同一台车接走、走完：
     - L2-CAW-03：FRESH 被受理并送达（旅程 Completed）。
  3. 第二个事实（另等，README 第 14 条）：FRESH 送达之后车又空了，STALE 在其后的某一轮被重新判过，仍不派、原因码不变：
     - L2-CAW-04：LastSeenAt 晚于 FRESH 送达的时刻，未受理，原因 DEMAND_OLDER_THAN_CATALOG_WINDOW，全程只有 FRESH 一趟旅程。

先后：STALE 先发、先判定，FRESH 后发；两条事实各等各的，不在同一次提交里直读。

红证据（缺陷版本）：去掉时间窗（CatalogAgeWindow.Refusal 恒放行，开轮判据与受理前重判一起去掉）：STALE 第一步就被空闲车接走，
L2-CAW-01、02 红；FRESH 等不到车（车在跑 STALE），L2-CAW-03 红；L2-CAW-04 红。只去掉判据不够：受理前重判是第二道，
仍会在受理那一刻挡下 STALE（见 PR 的变异 M1）。

建单时刻由场景写死，理由与公共函数见 TaskPriorityCommon.ps1。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
. (Join-Path $PSScriptRoot 'TaskPriorityCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$windowCode = 'DEMAND_OLDER_THAN_CATALOG_WINDOW'
$backlogEndpoint = "http://127.0.0.1:$($Context.HealthPort)/api/dashboard/dispatch-backlog"
$now = [DateTimeOffset]::UtcNow

$stale = New-L2PriorityDemand 'STALE' 'WIRE_TO_GATE' $now.AddMinutes(-40) $Context.RunId
$fresh = New-L2PriorityDemand 'FRESH' 'WIRE_TO_GATE' $now.AddMinutes(-1) $Context.RunId

function Get-Count([string]$Sql) { return [int](Invoke-L2Query -Connection $connection -Sql $Sql)[0].Total }

# --- 1. 车空着，超窗的 STALE 被判过、不派 ------------------------------------------------------------------

Publish-L2PriorityDemand $Context $stale
$staleJudged = Wait-L2ConditionOrLast -Description 'STALE was judged by an idle vehicle and refused for the catalog age window' `
    -Journal $journal -Criterion 'stale-refused' -TimeoutSeconds 60 `
    -Probe { Get-L2PriorityBacklog $connection $stale.Id } `
    -Until { param($v) $null -ne $v -and [string]$v.ReasonCode -eq $windowCode }
$staleRuntimes = Get-Count "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE DemandId = '$($stale.Id)'"
$staleIntents = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$($stale.Id)'"
$assertions.Add(
    'L2-CAW-01', '首次读到在 40 分钟前的 STALE，车空着也不派：积压原因 DEMAND_OLDER_THAN_CATALOG_WINDOW，没有旅程、没有订单意图（REQ-0366）',
    ($null -ne $staleJudged -and [string]$staleJudged.ReasonCode -eq $windowCode -and
        [string]::IsNullOrEmpty([string]$staleJudged.AcceptedAt) -and $staleRuntimes -eq 0 -and $staleIntents -eq 0),
    "$windowCode / not accepted / 0 journeys / 0 intents",
    "$(if ($staleJudged) { "$($staleJudged.ReasonCode) / AcceptedAt='$($staleJudged.AcceptedAt)'" } else { '(no backlog row)' }) / $staleRuntimes journeys / $staleIntents intents")

$board = Invoke-RestMethod -Uri $backlogEndpoint -NoProxy -TimeoutSec 10
$row = @($board.backlog | Where-Object { [string]$_.demandId -eq $stale.Id })
$assertions.Add(
    'L2-CAW-02', '/api/dashboard/dispatch-backlog 列出 STALE，原因码 DEMAND_OLDER_THAN_CATALOG_WINDOW 带中文说明（仍在等车列表里，不像离开目录那样消失）',
    ($row.Count -eq 1 -and [string]$row[0].reasonCode -eq $windowCode -and
        [string]$row[0].reasonDescription -match '\p{IsCJKUnifiedIdeographs}'),
    "one row / $windowCode / Chinese description",
    $(if ($row.Count -ne 1) { "$($row.Count) rows" } else { "$($row[0].reasonCode) / $($row[0].reasonDescription)" }))

# --- 2. 窗内的 FRESH 被同一台车接走、送达 ----------------------------------------------------------------

Publish-L2PriorityDemand $Context $fresh
$freshStage = Wait-L2ConditionOrLast -Description 'FRESH was accepted and its vehicle sent to the pickup' `
    -Journal $journal -Criterion 'fresh-accepted' -TimeoutSeconds 90 `
    -Probe { $r = Get-L2PriorityRuntime $connection $fresh.Id; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }
if ($freshStage -ne 'AwaitingPickupArrival') {
    $assertions.Add(
        'L2-CAW-03', '首次读到在 1 分钟前的 FRESH 被受理并送达（旅程 Completed）',
        $false, 'Completed', $(if ($freshStage) { $freshStage } else { '(no journey)' }))
    $journal.Note('FRESH was never accepted; the rest of the scenario cannot run.')
    return
}
Complete-L2PriorityJourney $Context $fresh
$freshRuntime = Get-L2PriorityRuntime $connection $fresh.Id
$assertions.Add(
    'L2-CAW-03', '首次读到在 1 分钟前的 FRESH 被受理并送达（旅程 Completed）',
    ($null -ne $freshRuntime -and [string]$freshRuntime.Stage -eq 'Completed'),
    'Completed', $(if ($freshRuntime) { [string]$freshRuntime.Stage } else { '(no journey)' }))
$deliveredAt = [DateTimeOffset]::UtcNow

# --- 3. 第二个事实：车又空了，STALE 在之后的一轮被重新判过，仍不派 ----------------------------------------------

$staleAfter = Wait-L2ConditionOrLast -Description 'STALE was judged again after FRESH was delivered, by the idle vehicle' `
    -Journal $journal -Criterion 'stale-rejudged' -TimeoutSeconds 60 `
    -Probe { Get-L2PriorityBacklog $connection $stale.Id } `
    -Until { param($v) $null -ne $v -and
        [DateTimeOffset]::Parse([string]$v.LastSeenAt, [Globalization.CultureInfo]::InvariantCulture) -gt $deliveredAt }
$rejudged = $null -ne $staleAfter -and
    [DateTimeOffset]::Parse([string]$staleAfter.LastSeenAt, [Globalization.CultureInfo]::InvariantCulture) -gt $deliveredAt
$journeys = Get-Count 'SELECT COUNT(*) AS Total FROM JourneyRuntimes'
$assertions.Add(
    'L2-CAW-04', 'FRESH 送达、车又空了之后，STALE 被重新判过，仍未受理、原因不变；全程只有 FRESH 一趟旅程',
    ($rejudged -and [string]::IsNullOrEmpty([string]$staleAfter.AcceptedAt) -and
        [string]$staleAfter.ReasonCode -eq $windowCode -and $journeys -eq 1),
    "re-judged after $($deliveredAt.ToString('o')) / not accepted / $windowCode / 1 journey",
    "LastSeenAt $(if ($staleAfter) { $staleAfter.LastSeenAt } else { '(no row)' }) / AcceptedAt '$(if ($staleAfter) { $staleAfter.AcceptedAt })' / $(if ($staleAfter) { $staleAfter.ReasonCode }) / $journeys journeys")

$journal.Note('Scenario finished.')
