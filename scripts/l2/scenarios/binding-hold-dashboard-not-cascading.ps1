#Requires -Version 7

<#
看板按 Map + TASK_TYPE 暂停，只停该任务类型、不连带其它（批次6-06，control-server#162；规格 8.3 批次 6 机制判据 ⑤）。

  1. 经看板应用的确认页提交，暂停 STAGING_TO_WIRE。确认页不带自动刷新；提交之后回到看板主页。
  2. 随后一条 WIRE_TO_GATE 需求照常受理并走完两段——暂停没有连带到同图的另一个任务类型。看板主页 STAGING_TO_WIRE
     那一行显示「已暂停」、来源「看板人工」。
  3. 第二条 WIRE_TO_GATE 需求受理、走到关卡腿（关卡单已建且确认）之后，再经看板暂停 WIRE_TO_GATE。这一趟照常走完：
     已建单的 RIoT 订单不改单、不换站、不取消。
  4. 车空出来之后，第三条 WIRE_TO_GATE 需求不受理，JourneyBacklog 的原因是已暂停（批次6-04 的准入判据）。
  5. 看板页面与服务端都没有解除入口，两条暂停到场景结束仍然成立。

红证据取法（缺陷版本，本地临时提交，不推送）：暂停只按 mapId 落（整图全停）→ 第 2 步「WIRE_TO_GATE 被受理」变红。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2TaskTypeHolds.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$serverBase = "http://127.0.0.1:$($Context.HealthPort)"
# The admission reason for a held task type, as batch 6-04 (control-server#160) registered it.
$heldReasonCode = 'TASK_TYPE_HELD'

function Format-Holds([object[]]$Holds) {
    if (@($Holds).Count -eq 0) { return '(none)' }
    return (@($Holds) | ForEach-Object { "$($_.TaskType)/$($_.Source)/$($_.ReasonCode)" }) -join ', '
}

# --- 1. 看板人工暂停 STAGING_TO_WIRE ----------------------------------------------------------------

$bindingsBefore = Get-L2ActiveBindings -Context $Context
$staging = Submit-L2DashboardHold -Context $Context -TaskType 'STAGING_TO_WIRE' -Reason '派工待送取货点被料车占住'
$assertions.Add(
    'L2-BH-01', '确认页不自动刷新，带着这一行的 Map 与任务类型；提交后 303 回到看板主页',
    (-not $staging.ConfirmationPage.Contains('http-equiv="refresh"') -and
        $staging.ConfirmationPage.Contains('name="taskType" value="STAGING_TO_WIRE"') -and
        $staging.StatusCode -eq 303 -and $staging.Location -eq '/'),
    'no refresh, taskType=STAGING_TO_WIRE, 303 → /',
    "refresh=$($staging.ConfirmationPage.Contains('http-equiv=""refresh""')), $($staging.StatusCode) → $($staging.Location)")

$holds = Wait-L2Condition -Description 'the STAGING_TO_WIRE hold is standing' `
    -Journal $journal -Criterion 'hold-staging' -TimeoutSeconds 30 `
    -Probe { $rows = Get-L2TaskTypeHolds -Context $Context; , $rows } `
    -Until { param($v) @($v | Where-Object { $_.TaskType -eq 'STAGING_TO_WIRE' }).Count -eq 1 }
$assertions.Add(
    'L2-BH-02', '暂停只落在 Map 25 的 STAGING_TO_WIRE 上，来源看板人工；WIRE_TO_GATE 没有暂停',
    (@($holds).Count -eq 1 -and $holds[0].TaskType -eq 'STAGING_TO_WIRE' -and $holds[0].Source -eq 'MANUAL'),
    'STAGING_TO_WIRE/MANUAL/DASHBOARD_MANUAL_HOLD', (Format-Holds $holds))

# --- 2. 同图的 WIRE_TO_GATE 不受连带：受理并走完两段 -----------------------------------------------------

$first = New-L2WireToGateDemand -Context $Context -Label 'first'
$firstGate = Invoke-L2JourneyToGateLeg -Context $Context -Demand $first
$firstStage = Complete-L2JourneyAtGate -Context $Context -Demand $first -GateIntent $firstGate
$firstReason = Get-L2BacklogReason -Context $Context -Demand $first
$assertions.Add(
    'L2-BH-03', 'STAGING_TO_WIRE 暂停期间，WIRE_TO_GATE 需求照常受理并走完两段（不连带）',
    ($firstReason -eq 'ACCEPTED' -and $firstStage -eq 'Completed'),
    'ACCEPTED → Completed', "$firstReason → $firstStage")

$stagingRow = Wait-L2Condition -Description 'the dashboard shows STAGING_TO_WIRE held by a person' `
    -Journal $journal -Criterion 'dashboard-staging-row' -TimeoutSeconds 30 `
    -Probe { Get-L2DashboardBindingRow -Context $Context -TaskType 'STAGING_TO_WIRE' } `
    -Until { param($v) $null -ne $v -and $v.Contains('已暂停') }
$gateRowBefore = Get-L2DashboardBindingRow -Context $Context -TaskType 'WIRE_TO_GATE'
$assertions.Add(
    'L2-BH-04', '看板主页 STAGING_TO_WIRE 那一行显示已暂停、来源看板人工与理由；WIRE_TO_GATE 那一行正常',
    ($stagingRow.Contains('已暂停') -and $stagingRow.Contains('看板人工') -and $stagingRow.Contains('派工待送取货点被料车占住') -and
        $null -ne $gateRowBefore -and $gateRowBefore.Contains('正常')),
    'STAGING_TO_WIRE: 已暂停 看板人工 …；WIRE_TO_GATE: 正常', "$stagingRow | $gateRowBefore")

# --- 3. 已建关卡单的那一趟在暂停 WIRE_TO_GATE 之后照常完成 ---------------------------------------------------

$second = New-L2WireToGateDemand -Context $Context -Label 'second'
$secondGate = Invoke-L2JourneyToGateLeg -Context $Context -Demand $second
$ordersBefore = @($riot.Snapshot().body.orders | Where-Object { $_.upperId -eq $secondGate.UpperId })

$gate = Submit-L2DashboardHold -Context $Context -TaskType 'WIRE_TO_GATE' -Reason '关卡门口在施工'
$holds = Wait-L2Condition -Description 'the WIRE_TO_GATE hold is standing' `
    -Journal $journal -Criterion 'hold-gate' -TimeoutSeconds 30 `
    -Probe { $rows = Get-L2TaskTypeHolds -Context $Context; , $rows } `
    -Until { param($v) @($v | Where-Object { $_.TaskType -eq 'WIRE_TO_GATE' }).Count -eq 1 }
$assertions.Add(
    'L2-BH-05', '经看板再暂停 WIRE_TO_GATE：提交 303，两条人工暂停各落在自己的任务类型上',
    ($gate.StatusCode -eq 303 -and @($holds).Count -eq 2 -and
        @($holds | Where-Object { $_.Source -ne 'MANUAL' }).Count -eq 0),
    '303; STAGING_TO_WIRE/MANUAL, WIRE_TO_GATE/MANUAL', "$($gate.StatusCode); $(Format-Holds $holds)")

$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$secondGateAfter = Get-L2Intent -Context $Context -Demand $second -Purpose 'TO_GATE'
$assertions.Add(
    'L2-BH-06', '暂停之后已建的关卡单不改单、不换站、不取消：同一个 UpperId 与 OrderId，仍是 CONFIRMED',
    ($secondGateAfter.UpperId -eq $secondGate.UpperId -and $secondGateAfter.OrderId -eq $secondGate.OrderId -and
        $secondGateAfter.Status -eq 'CONFIRMED' -and $ordersBefore.Count -eq 1),
    "$($secondGate.UpperId) / $($secondGate.OrderId) / CONFIRMED",
    "$($secondGateAfter.UpperId) / $($secondGateAfter.OrderId) / $($secondGateAfter.Status)")

$secondStage = Complete-L2JourneyAtGate -Context $Context -Demand $second -GateIntent $secondGate
$assertions.Add(
    'L2-BH-07', '暂停前已建关卡单的那一趟照常走完',
    ($secondStage -eq 'Completed'), 'Completed', $secondStage)

# --- 4. 暂停之后的新 WIRE_TO_GATE 需求不受理 ----------------------------------------------------------------

$third = New-L2WireToGateDemand -Context $Context -Label 'third'
$thirdReason = Wait-L2Condition -Description 'the third demand is kept back because its task type is held' `
    -Journal $journal -Criterion 'backlog-third' -TimeoutSeconds 60 `
    -Probe { Get-L2BacklogReason -Context $Context -Demand $third } `
    -Until { param($v) $null -ne $v -and $v -ne 'ACCEPTED' }
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$thirdStage = Get-L2JourneyStage -Context $Context -Demand $third
$thirdReason = Get-L2BacklogReason -Context $Context -Demand $third
$assertions.Add(
    'L2-BH-08', '车空闲时，WIRE_TO_GATE 暂停后的新需求不受理，JourneyBacklog 原因为已暂停',
    ($null -eq $thirdStage -and $thirdReason -eq $heldReasonCode),
    "no journey / $heldReasonCode", "$(if ($thirdStage) { $thirdStage } else { 'no journey' }) / $thirdReason")

# --- 5. 没有解除入口 -------------------------------------------------------------------------------

$page = Get-L2DashboardPage -Context $Context
$dashboardRelease = Invoke-WebRequest -NoProxy -TimeoutSec 10 -Method Post -SkipHttpErrorCheck -MaximumRedirection 0 `
    -Uri "$($Context.DashboardUrl)/actions/task-type-hold-release" -Headers @{ Origin = $Context.DashboardUrl } `
    -ContentType 'application/x-www-form-urlencoded' -Body "mapId=$($Context.MapId)&taskType=WIRE_TO_GATE"
$serverDelete = Invoke-WebRequest -NoProxy -TimeoutSec 10 -Method Delete -SkipHttpErrorCheck `
    -Uri "$serverBase/api/task-type-holds"
$serverRelease = Invoke-WebRequest -NoProxy -TimeoutSec 10 -Method Post -SkipHttpErrorCheck `
    -Uri "$serverBase/api/task-type-holds/release" -ContentType 'application/json' `
    -Body (@{ mapId = $Context.MapId; taskType = 'WIRE_TO_GATE' } | ConvertTo-Json -Compress)
$statuses = @([int]$dashboardRelease.StatusCode, [int]$serverDelete.StatusCode, [int]$serverRelease.StatusCode)
$holds = Get-L2TaskTypeHolds -Context $Context
$assertions.Add(
    'L2-BH-09', '看板页面没有解除字样，看板与服务端的解除请求都不是成功响应，两条暂停仍然成立',
    (-not $page.Contains('解除') -and @($statuses | Where-Object { $_ -ge 200 -and $_ -lt 400 }).Count -eq 0 -and
        @($holds).Count -eq 2),
    'no 解除 on page; every release attempt >= 400; 2 holds standing',
    "解除 on page: $($page.Contains('解除')); statuses $($statuses -join ', '); $(Format-Holds $holds)")

$bindingsAfter = Get-L2ActiveBindings -Context $Context
$assertions.Add(
    'L2-BH-10', '暂停不改绑定：生效绑定集版本与每条绑定前后相同',
    ($bindingsAfter -eq $bindingsBefore -and -not [string]::IsNullOrEmpty($bindingsBefore)),
    $bindingsBefore, $bindingsAfter)

$journal.Note('Scenario finished.')
