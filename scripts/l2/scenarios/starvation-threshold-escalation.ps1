#Requires -Version 7

<#
防饥饿超时层与升级告警（批次7-09，control-server#214；REQ-0202、REQ-0203、REQ-0210 后半）。

一台车，两遍，同一个服务端：

第一遍，阈值未配置（现场上线时实际走的路径，7-17 的阈值还没批）。车忙着跑 BUSY 时，积压里有等了 5 分钟的 WIRE_TO_GATE
（HUNGRY）与刚建的 STAGING_TO_WIRE（STAGING1）。车空出来：
  - L2-STE-01：先受理 STAGING1——没有阈值就没有超时层，只计龄、不升级；
  - L2-STE-02：HUNGRY 没有升级告警标记，服务端日志里也没有它的升级告警。

第二遍，经 FieldOps 正式导入本区阈值 60 秒（control-server#216 的导入动词）。HUNGRY 此时已等了 5 分钟以上：
  - L2-STE-03：导入之后 HUNGRY 被标记升级，记下的参数版本就是刚导入的那一版；
  - L2-STE-04（第二个事实，另等）：服务端日志里恰好一条 HUNGRY 的升级告警；
再来一条刚建的 STAGING_TO_WIRE（STAGING2），把 STAGING1 走完、车空出来：
  - L2-STE-05：先受理超时的 HUNGRY，越过未超时的 STAGING2（REQ-0202「超时层高于所有未超时的带」）；
  - L2-STE-06：HUNGRY 从告警到受理，又过了好几轮，告警标记时刻没变、日志里仍只有一条（按任务去重）。

红证据（缺陷版本）：阈值未配置时也升级（TaskStarvation.Assess 把空阈值当 0），第一遍 L2-STE-01、02 变红。

需求的本地建单时刻由场景写死，理由见 TaskPriorityCommon.ps1。
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
$zone = $Context.DispatchZone
$thresholdSeconds = 60
$now = [DateTimeOffset]::UtcNow

$busy = New-L2PriorityDemand 'BUSY' 'WIRE_TO_GATE' $now.AddMinutes(-30) $Context.RunId
$hungry = New-L2PriorityDemand 'HUNGRY' 'WIRE_TO_GATE' $now.AddMinutes(-5) $Context.RunId
$staging1 = New-L2PriorityDemand 'STAGING1' 'STAGING_TO_WIRE' $now.AddSeconds(-5) $Context.RunId

# 包一层再返回：记录可能为空，而 return 会把数组展开一层，不包的话空数组到了调用方是 $null。
function Get-EscalationRecords([string]$DemandId) {
    return , (Get-L2PriorityWarningRecords $Context @('Starvation escalation', $DemandId))
}

function Test-Escalated([object]$Row) {
    return $null -ne $Row -and -not [string]::IsNullOrEmpty([string]$Row.StarvationEscalatedAt)
}

# --- 第一遍：阈值未配置 ---------------------------------------------------------------------------------------

Publish-L2PriorityDemand $Context $busy
$null = Wait-L2PriorityStage $Context $busy.Id 'AwaitingPickupArrival' 'demand BUSY was accepted and the vehicle is on its way' 90
Publish-L2PriorityDemand $Context $hungry
Publish-L2PriorityDemand $Context $staging1
$null = Wait-L2Condition -Description 'both waiting demands have a backlog row, judged while the vehicle was busy' `
    -Journal $journal -Criterion 'backlog-rows' -TimeoutSeconds 60 `
    -Probe { @($hungry, $staging1 | Where-Object { $null -ne (Get-L2PriorityBacklog $connection $_.Id) }).Count } `
    -Until { param($v) $v -eq 2 }

Complete-L2PriorityJourney $Context $busy
$first = Wait-L2Condition -Description 'the freed vehicle accepted one of the two waiting demands (no threshold configured)' `
    -Journal $journal -Criterion 'first-accepted' -TimeoutSeconds 90 `
    -Probe { Get-L2PriorityAccepted $connection @($hungry, $staging1) } `
    -Until { param($v) $v -ne '' }
$assertions.Add(
    'L2-STE-01', '阈值未配置：等了 5 分钟的 WIRE_TO_GATE 不升级，车空出来先受理 STAGING_TO_WIRE（REQ-0203 降级：只计龄、不升级）',
    ($first -ceq 'STAGING1'), 'STAGING1', $first)
# 第二个事实，另等（README 第 14 条，审查低 4）：受理 STAGING1 的那一轮，轮末汇总的提交晚于受理的提交，受理一出现就直读
# 升级标记，读到的可能是汇总还没写的时候。等 HUNGRY 在之后的某一轮被重新判过（LastSeenAt 晚于 STAGING1 的受理时刻）：
# 轮次是串行的，那时受理 STAGING1 那一轮的汇总必然已经提交。「有版本、阈值留空」这种未配置形状这里不跑，由 L1 覆盖
# （Batch7TaskPriorityOrderingTests.WithNoApprovedThresholdNothingEscalatesButTheAgeIsStillCounted 的 threshold-empty 一例、
# Batch7StarvationEscalationTests.WithNoThresholdConfiguredNothingIsEscalatedUntilOneIsImported(true)）。
$staging1Backlog = Get-L2PriorityBacklog $connection $staging1.Id
$staging1AcceptedAt = if ($null -ne $staging1Backlog -and -not [string]::IsNullOrEmpty([string]$staging1Backlog.AcceptedAt)) {
    [DateTimeOffset]::Parse([string]$staging1Backlog.AcceptedAt, [Globalization.CultureInfo]::InvariantCulture)
} else { $null }
$hungryUnconfigured = if ($null -eq $staging1AcceptedAt) { Get-L2PriorityBacklog $connection $hungry.Id } else {
    Wait-L2ConditionOrLast -Description 'HUNGRY was judged again after STAGING1 was accepted, so that round has ended' `
        -Journal $journal -Criterion 'hungry-rejudged' -TimeoutSeconds 60 `
        -Probe { Get-L2PriorityBacklog $connection $hungry.Id } `
        -Until { param($v) $null -ne $v -and
            [DateTimeOffset]::Parse([string]$v.LastSeenAt, [Globalization.CultureInfo]::InvariantCulture) -gt $staging1AcceptedAt }
}
$hungryRejudged = $null -ne $staging1AcceptedAt -and $null -ne $hungryUnconfigured -and
    [DateTimeOffset]::Parse([string]$hungryUnconfigured.LastSeenAt, [Globalization.CultureInfo]::InvariantCulture) -gt $staging1AcceptedAt
$unconfiguredRecords = Get-EscalationRecords $hungry.Id
$assertions.Add(
    'L2-STE-02', '阈值未配置：受理 STAGING1 那一轮结束之后，HUNGRY 没有升级告警标记，服务端日志里也没有它的升级告警',
    ($hungryRejudged -and -not (Test-Escalated $hungryUnconfigured) -and $unconfiguredRecords.Count -eq 0),
    'round over, no mark, 0 records',
    "rejudged $hungryRejudged, mark '$(if ($hungryUnconfigured) { $hungryUnconfigured.StarvationEscalatedAt })', $($unconfiguredRecords.Count) record(s)")

# --- 第二遍：导入 60 秒阈值 -----------------------------------------------------------------------------------

$csv = Join-Path $Context.SnapshotRoot 'starvation-threshold.csv'
# 无 BOM 的 UTF-8、LF 换行；途中追加那一列留空（本区仍禁止途中追加）。喂进去的这份留在证据目录。
[IO.File]::WriteAllText(
    $csv,
    "dispatch_zone,en_route_addition_max_path_cost_increase_mm,starvation_threshold_seconds`n$zone,,$thresholdSeconds`n",
    [Text.UTF8Encoding]::new($false))
$import = & $Context.InvokeFieldOps -Arguments @('import-dispatch-zone-parameters', '--input', $csv)
$journal.Observe('dispatch-zone-parameters-import', [string]$import.outcome, @{ output = $import })
$importedVersion = [long]$import.version

$hungryEscalated = Wait-L2ConditionOrLast -Description 'HUNGRY was marked escalated after the threshold was imported' `
    -Journal $journal -Criterion 'escalation-mark' -TimeoutSeconds 60 `
    -Probe { Get-L2PriorityBacklog $connection $hungry.Id } `
    -Until { param($v) Test-Escalated $v }
$markVersion = if (Test-Escalated $hungryEscalated) { [string]$hungryEscalated.StarvationEscalationParameterVersion } else { '' }
$assertions.Add(
    'L2-STE-03', "导入 $thresholdSeconds 秒阈值之后 HUNGRY 被标记升级，记下的参数版本是刚导入的那一版",
    ((Test-Escalated $hungryEscalated) -and $markVersion -eq "$importedVersion"),
    "escalated under version $importedVersion",
    "import $($import.outcome) v$importedVersion; mark '$(if ($hungryEscalated) { $hungryEscalated.StarvationEscalatedAt })' version '$markVersion'")
$escalatedAt = if (Test-Escalated $hungryEscalated) { [string]$hungryEscalated.StarvationEscalatedAt } else { '' }

# 日志行在标记那次提交之后才写，另等（README 第 14 条）。
$records = Wait-L2ConditionOrLast -Description 'the server logged the escalation of HUNGRY' `
    -Journal $journal -Criterion 'escalation-log' -TimeoutSeconds 30 `
    -Probe { , (Get-EscalationRecords $hungry.Id) } `
    -Until { param($v) $v.Count -ge 1 }
$assertions.Add(
    'L2-STE-04', '服务端日志里恰好一条 HUNGRY 的升级告警',
    (@($records).Count -eq 1), '1', "$(@($records).Count)")

$staging2 = New-L2PriorityDemand 'STAGING2' 'STAGING_TO_WIRE' ([DateTimeOffset]::UtcNow) $Context.RunId
Publish-L2PriorityDemand $Context $staging2
$null = Wait-L2Condition -Description 'STAGING2 has a backlog row, judged while the vehicle was busy with STAGING1' `
    -Journal $journal -Criterion 'backlog-rows' -TimeoutSeconds 60 `
    -Probe { Get-L2PriorityBacklog $connection $staging2.Id } `
    -Until { param($v) $null -ne $v }

# 走完第一遍接走的那一条（正确的版本里是 STAGING1；缺陷版本接走的是 HUNGRY，照样走完，让后面的判据落下来而不是等一趟不存在的旅程）。
Complete-L2PriorityJourney $Context $(if ($first -ceq 'HUNGRY') { $hungry } else { $staging1 })
$second = Wait-L2Condition -Description 'the freed vehicle accepted HUNGRY or STAGING2' `
    -Journal $journal -Criterion 'second-accepted' -TimeoutSeconds 90 `
    -Probe { Get-L2PriorityAccepted $connection @($hungry, $staging2) } `
    -Until { param($v) $v -ne '' }
$assertions.Add(
    'L2-STE-05', '超时的 HUNGRY 越过刚建、未超时的 STAGING_TO_WIRE 先被受理（REQ-0202：超时层高于所有未超时的带）',
    ($second -ceq 'HUNGRY'), 'HUNGRY', $second)

$hungryAfter = Get-L2PriorityBacklog $connection $hungry.Id
$recordsAfter = Get-EscalationRecords $hungry.Id
$assertions.Add(
    'L2-STE-06', 'HUNGRY 从告警到受理隔了好几轮：告警标记时刻没变，日志里仍只有一条（按任务去重）',
    ((Test-Escalated $hungryAfter) -and [string]$hungryAfter.StarvationEscalatedAt -eq $escalatedAt -and $recordsAfter.Count -eq 1),
    "mark $escalatedAt, 1 record",
    "mark '$(if ($hungryAfter) { $hungryAfter.StarvationEscalatedAt })', $($recordsAfter.Count) record(s)")

$journal.Note('Scenario finished.')
