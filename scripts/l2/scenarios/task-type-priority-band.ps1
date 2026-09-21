#Requires -Version 7

<#
任务类型优先级带（批次7-09，control-server#214；REQ-0202、REQ-0201）。

一台车，先让它忙着跑一趟 WIRE_TO_GATE（需求 BUSY）。车在途时积压里先后出现两条需求：
  - OLD：WIRE_TO_GATE，本地建单于 20 分钟前（普通带、等得更久）；
  - STAGING：STAGING_TO_WIRE，本地建单于 1 分钟前（最高带、新）。
两条都被在途那几轮判过（积压里都有行）之后，把 BUSY 走完，车空出来。断言：
  - L2-TPB-01：空出来之后第一条被受理的是 STAGING，不是等得更久的 OLD；
  - L2-TPB-02（第二个事实，另等）：STAGING 受理之后的下一轮，OLD 被重新判过、仍未受理——车已经给了 STAGING。
  - L2-TPB-03：本区阈值未配置，两条都没有升级告警标记。

红证据（缺陷版本）：从 DispatchCandidateOrdering.Layers() 去掉优先级带那一层，OLD 按年龄先被受理，L2-TPB-01 变红。

没有配每区参数（默认「未配置」），这条场景同时是阈值未批准时的现场行为：只有优先级带与年龄在起作用。
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
$now = [DateTimeOffset]::UtcNow

$busy = New-L2PriorityDemand 'BUSY' 'WIRE_TO_GATE' $now.AddMinutes(-30) $Context.RunId
$old = New-L2PriorityDemand 'OLD' 'WIRE_TO_GATE' $now.AddMinutes(-20) $Context.RunId
$staging = New-L2PriorityDemand 'STAGING' 'STAGING_TO_WIRE' $now.AddMinutes(-1) $Context.RunId

# --- 1. 车忙着：受理 BUSY ----------------------------------------------------------------------------------

Publish-L2PriorityDemand $Context $busy
$null = Wait-L2PriorityStage $Context $busy.Id 'AwaitingPickupArrival' 'demand BUSY was accepted and the vehicle is on its way' 90

# --- 2. 车在途时，OLD 与 STAGING 先后进积压，两条都被判过 ------------------------------------------------------

Publish-L2PriorityDemand $Context $old
Publish-L2PriorityDemand $Context $staging
$judged = Wait-L2Condition -Description 'both waiting demands have a backlog row, judged while the vehicle was busy' `
    -Journal $journal -Criterion 'backlog-rows' -TimeoutSeconds 60 `
    -Probe { @($old, $staging | Where-Object { $null -ne (Get-L2PriorityBacklog $connection $_.Id) }).Count } `
    -Until { param($v) $v -eq 2 }
$journal.Note("Both waiting demands judged while BUSY was under way ($judged rows); neither accepted yet: " +
    "'$(Get-L2PriorityAccepted $connection @($old, $staging))'.")

# --- 3. 车跑完 BUSY 空出来，看先接谁 ---------------------------------------------------------------------------

Complete-L2PriorityJourney $Context $busy
$first = Wait-L2Condition -Description 'the freed vehicle accepted one of the two waiting demands' `
    -Journal $journal -Criterion 'first-accepted' -TimeoutSeconds 90 `
    -Probe { Get-L2PriorityAccepted $connection @($old, $staging) } `
    -Until { param($v) $v -ne '' }
$assertions.Add(
    'L2-TPB-01', '车空出来之后先受理刚建的 STAGING_TO_WIRE，而不是等了 20 分钟的 WIRE_TO_GATE（REQ-0202：STAGING_TO_WIRE 独占最高初始带）',
    ($first -ceq 'STAGING'), 'STAGING', $first)

# 第二个事实：STAGING 受理之后，OLD 在之后的某一轮被重新判过（LastSeenAt 晚于 STAGING 的受理时刻）且仍未受理。
# 两者不在同一次提交里，所以另等，不直读（README 第 14 条）。
$stagingAcceptedAt = [DateTimeOffset]::Parse(
    [string](Get-L2PriorityBacklog $connection $staging.Id).AcceptedAt, [Globalization.CultureInfo]::InvariantCulture)
$oldAfter = Wait-L2ConditionOrLast -Description 'OLD was judged again after STAGING was accepted' `
    -Journal $journal -Criterion 'old-rejudged' -TimeoutSeconds 60 `
    -Probe { Get-L2PriorityBacklog $connection $old.Id } `
    -Until { param($v) $null -ne $v -and
        [DateTimeOffset]::Parse([string]$v.LastSeenAt, [Globalization.CultureInfo]::InvariantCulture) -gt $stagingAcceptedAt }
$oldRejudged = $null -ne $oldAfter -and
    [DateTimeOffset]::Parse([string]$oldAfter.LastSeenAt, [Globalization.CultureInfo]::InvariantCulture) -gt $stagingAcceptedAt
$oldAccepted = $null -ne $oldAfter -and -not [string]::IsNullOrEmpty([string]$oldAfter.AcceptedAt)
$assertions.Add(
    'L2-TPB-02', 'STAGING 受理之后，OLD 被重新判过、仍未受理（车已经给了 STAGING）',
    ($oldRejudged -and -not $oldAccepted),
    "re-judged after $($stagingAcceptedAt.ToString('o')), not accepted",
    "LastSeenAt $(if ($oldAfter) { $oldAfter.LastSeenAt } else { '(no row)' }), AcceptedAt '$(if ($oldAfter) { $oldAfter.AcceptedAt })', reason $(if ($oldAfter) { $oldAfter.ReasonCode })")

$escalated = @($old, $staging | Where-Object {
        $row = Get-L2PriorityBacklog $connection $_.Id
        $null -ne $row -and -not [string]::IsNullOrEmpty([string]$row.StarvationEscalatedAt)
    } | ForEach-Object { $_.Label })
$assertions.Add(
    'L2-TPB-03', '本区阈值未配置：OLD 等了 20 分钟也不升级，两条都没有升级告警标记（REQ-0203 降级）',
    ($escalated.Count -eq 0), '(none)', $(if ($escalated.Count -eq 0) { '(none)' } else { $escalated -join ',' }))

$journal.Note('Scenario finished.')
