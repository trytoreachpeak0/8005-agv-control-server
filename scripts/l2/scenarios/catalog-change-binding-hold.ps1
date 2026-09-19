#Requires -Version 7

<#
目录变化按稳定身份分类、影响只收敛到受影响的任务类型（批次6-06，control-server#162；REQ-0341、REQ-0342、REQ-0345）。

  1. 经假 RIoT 的 PUT /control/v1/maps/{mapId}/stations 把站点 230（STAGING_TO_WIRE 绑定的站）改名。下一次目录完整确认之后，
     STAGING_TO_WIRE 出现来源「目录变化」、原因「站点改名」的暂停；绑定内容一字不改；WIRE_TO_GATE 没有暂停。
  2. 同一次运行里一条 WIRE_TO_GATE 需求照常受理，走到关卡腿、关卡单已建（不连带）。
  3. 再把站点 210（WIRE_TO_GATE 绑定的关卡）从目录删掉：WIRE_TO_GATE 出现「站点已不在目录」的暂停。此前已建关卡单的那一趟
     不被取消，照常走完。
  4. 车空出来之后，新的 WIRE_TO_GATE 需求不受理，原因码恰好是 TASK_TYPE_BINDING_STATION_NOT_IN_CATALOG（L2-CC-07）：
     改名、删除的绑定站由固定站视图按本轮目录先挡住，此时还轮不到暂停。
  5. 同一个变化连续多轮只一条暂停；目录变化记录 230 恰好一行 RENAMED、210 恰好一行 REMOVED——删 210 换了整张图的修订，
     230 那条改名不因此再记一行（L2-CC-08，control-server#201）；看板显示两条暂停及其来源。
  6. 把 210 以原名称放回目录，目录完整确认过几轮之后，第 4 步那条还在等的需求与一条新需求都仍不受理，原因码 TASK_TYPE_HELD，
     WIRE_TO_GATE 那条暂停仍未解除（L2-CC-11）。这是暂停独有的作用：站点恢复不自动解暂停，要 FieldOps 解除。

红证据取法（缺陷版本）：按旧票 #54 的读法把「只改名」判为无变化 → 第 1 步「出现站点改名暂停」变红。
control-server#201 的两份：去掉 BoundFixedTaskStationResolver 里 holds.Holds(taskType) 那一段 → L2-CC-11 红
（站点恢复后需求被受理），L2-CC-07 仍绿；修复之前的 905ffd1d → L2-CC-08 红（230 两行 RENAMED）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2TaskTypeHolds.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mapId = $Context.MapId

function Format-Holds([object[]]$Holds) {
    if (@($Holds).Count -eq 0) { return '(none)' }
    return (@($Holds) | ForEach-Object { "$($_.TaskType)/$($_.Source)/$($_.ReasonCode)" }) -join ', '
}

function Set-Stations([hashtable]$Stations) {
    $journal.Note("Fake RIoT stations on map ${mapId} become: " +
        (($Stations.Keys | Sort-Object { [int]$_ } | ForEach-Object { "$_=$($Stations[$_])" }) -join ', '))
    $null = $riot.Command('Put', "maps/$mapId/stations", @{ stations = $Stations })
}

function Get-CatalogChanges {
    $rows = Invoke-L2Query -Connection $Context.Connection -Sql (
        "SELECT StationRiotId, PreviousStationName, CurrentStationName, ChangeKind, HoldId FROM TaskTypeStationCatalogChanges " +
        "WHERE MapId = $mapId ORDER BY ObservedAt, ChangeId")
    return , $rows
}

$bindingsBefore = Get-L2ActiveBindings -Context $Context

# --- 1. 230 改名：STAGING_TO_WIRE 暂停，原因站点改名 ------------------------------------------------------

$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal
$holdsBefore = Get-L2TaskTypeHolds -Context $Context
Set-Stations @{ '210' = '关卡'; '12' = 'N1-3_N1-7'; '11' = 'C15-13'; '230' = '派工待送取货-临时堆放' }
$holds = Wait-L2Condition -Description 'the rename of station 230 holds STAGING_TO_WIRE' `
    -Journal $journal -Criterion 'hold-renamed' -TimeoutSeconds 60 `
    -Probe { $rows = Get-L2TaskTypeHolds -Context $Context; , $rows } `
    -Until { param($v) @($v | Where-Object { $_.TaskType -eq 'STAGING_TO_WIRE' }).Count -ge 1 }
$staging = @($holds | Where-Object { $_.TaskType -eq 'STAGING_TO_WIRE' })
$assertions.Add(
    'L2-CC-01', '230 只改名：STAGING_TO_WIRE 出现来源目录变化、原因站点改名的暂停，WIRE_TO_GATE 没有暂停',
    (@($holdsBefore).Count -eq 0 -and $staging.Count -eq 1 -and $staging[0].Source -eq 'CATALOG_CHANGE' -and
        $staging[0].ReasonCode -eq 'STATION_RENAMED' -and
        @($holds | Where-Object { $_.TaskType -eq 'WIRE_TO_GATE' }).Count -eq 0),
    '(none) → STAGING_TO_WIRE/CATALOG_CHANGE/STATION_RENAMED', "$(Format-Holds $holdsBefore) → $(Format-Holds $holds)")

$bindingsAfterRename = Get-L2ActiveBindings -Context $Context
$assertions.Add(
    'L2-CC-02', '改名之后绑定内容一字不改：生效版本、站点 id 与记下的旧名称都不变',
    ($bindingsAfterRename -eq $bindingsBefore -and $bindingsBefore.Contains('STAGING_TO_WIRE=230/派工待送取货/')),
    $bindingsBefore, $bindingsAfterRename)

# --- 2. 同一次运行里 WIRE_TO_GATE 照常受理 -----------------------------------------------------------------

$first = New-L2WireToGateDemand -Context $Context -Label 'first'
$firstGate = Invoke-L2JourneyToGateLeg -Context $Context -Demand $first
$firstReason = Get-L2BacklogReason -Context $Context -Demand $first
# A demand accepted by an earlier round reads DEMAND_ALREADY_ACCEPTED on later rounds (the other vehicle is still being
# served), so "accepted" is either code; what matters is that the journey exists and ran.
$assertions.Add(
    'L2-CC-03', 'STAGING_TO_WIRE 因目录变化暂停期间，WIRE_TO_GATE 需求照常受理并建出关卡单（不连带）',
    ($firstReason -in @('ACCEPTED', 'DEMAND_ALREADY_ACCEPTED') -and $firstGate.Status -eq 'CONFIRMED'),
    'ACCEPTED or DEMAND_ALREADY_ACCEPTED / TO_GATE CONFIRMED', "$firstReason / TO_GATE $($firstGate.Status)")

# --- 3. 210 从目录删掉：WIRE_TO_GATE 暂停，原因站点已不在目录；已建关卡单那一趟不被取消 --------------------------------

Set-Stations @{ '12' = 'N1-3_N1-7'; '11' = 'C15-13'; '230' = '派工待送取货-临时堆放' }
$holds = Wait-L2Condition -Description 'the removal of station 210 holds WIRE_TO_GATE' `
    -Journal $journal -Criterion 'hold-removed' -TimeoutSeconds 60 `
    -Probe { $rows = Get-L2TaskTypeHolds -Context $Context; , $rows } `
    -Until { param($v) @($v | Where-Object { $_.TaskType -eq 'WIRE_TO_GATE' }).Count -ge 1 }
$gateHolds = @($holds | Where-Object { $_.TaskType -eq 'WIRE_TO_GATE' })
$assertions.Add(
    'L2-CC-04', '210 删掉：WIRE_TO_GATE 出现来源目录变化、原因站点已不在目录的暂停',
    ($gateHolds.Count -eq 1 -and $gateHolds[0].Source -eq 'CATALOG_CHANGE' -and $gateHolds[0].ReasonCode -eq 'STATION_NOT_IN_CATALOG'),
    'WIRE_TO_GATE/CATALOG_CHANGE/STATION_NOT_IN_CATALOG', (Format-Holds $gateHolds))

$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$firstGateAfter = Get-L2Intent -Context $Context -Demand $first -Purpose 'TO_GATE'
$assertions.Add(
    'L2-CC-05', '此前已建的关卡单不被取消、不改单：同一个 UpperId 与 OrderId，仍是 CONFIRMED',
    ($firstGateAfter.UpperId -eq $firstGate.UpperId -and $firstGateAfter.OrderId -eq $firstGate.OrderId -and
        $firstGateAfter.Status -eq 'CONFIRMED'),
    "$($firstGate.UpperId) / $($firstGate.OrderId) / CONFIRMED",
    "$($firstGateAfter.UpperId) / $($firstGateAfter.OrderId) / $($firstGateAfter.Status)")

$firstStage = Complete-L2JourneyAtGate -Context $Context -Demand $first -GateIntent $firstGate
$assertions.Add(
    'L2-CC-06', '已建关卡单的那一趟照常走完', ($firstStage -eq 'Completed'), 'Completed', $firstStage)

# --- 4. 车空出来之后，新的 WIRE_TO_GATE 需求不受理 --------------------------------------------------------

$notInCatalog = 'TASK_TYPE_BINDING_STATION_NOT_IN_CATALOG'
$second = New-L2WireToGateDemand -Context $Context -Label 'second'
$null = Wait-L2ConditionOrLast -Description 'the second demand is kept back for the gate missing from the catalog' `
    -Journal $journal -Criterion 'backlog-second' -TimeoutSeconds 60 `
    -Probe { Get-L2BacklogReason -Context $Context -Demand $second } `
    -Until { param($v) $v -eq $notInCatalog }
# The reason is written every round; read it again rounds later, so the verdict is what the rounds settled on and not
# the first answer one of them gave.
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$secondStage = Get-L2JourneyStage -Context $Context -Demand $second
$secondReason = Get-L2BacklogReason -Context $Context -Demand $second
$assertions.Add(
    'L2-CC-07', "车空闲时，关卡已不在目录的 WIRE_TO_GATE 新需求不受理，原因码恰好是 $notInCatalog（本轮目录先挡住，还轮不到暂停）",
    ($null -eq $secondStage -and $secondReason -eq $notInCatalog),
    "no journey / $notInCatalog", "$(if ($secondStage) { $secondStage } else { 'no journey' }) / $secondReason")

# --- 5. 同一变化多轮只一条暂停；目录变化记录与看板 ---------------------------------------------------------

$holds = Get-L2TaskTypeHolds -Context $Context
$changes = Get-CatalogChanges
# Removing 210 changed the whole Map's revision while 230 stayed renamed to the same name under the same hold: one change,
# one row (control-server#201). Exactly two rows, so a duplicate turns this red rather than passing as ">= 1".
$assertions.Add(
    'L2-CC-08', '同一变化跑过多轮仍只有两条暂停；目录变化记录 230 恰好一行改名、210 恰好一行删除（删 210 换了修订也不重复记 230），各指向自己那条暂停',
    (@($holds).Count -eq 2 -and @($changes).Count -eq 2 -and
        @($changes | Where-Object { $_.StationRiotId -eq 230 -and $_.ChangeKind -eq 'RENAMED' }).Count -eq 1 -and
        @($changes | Where-Object { $_.StationRiotId -eq 210 -and $_.ChangeKind -eq 'REMOVED' }).Count -eq 1 -and
        @($changes | Where-Object { [string]::IsNullOrEmpty([string]$_.HoldId) }).Count -eq 0),
    '2 holds; changes: 230 RENAMED, 210 REMOVED, each with a hold',
    "$(Format-Holds $holds); changes: $((@($changes) | ForEach-Object { "$($_.StationRiotId) $($_.ChangeKind)" }) -join ', ')")

$stagingRow = Wait-L2Condition -Description 'the dashboard shows both catalog holds' `
    -Journal $journal -Criterion 'dashboard-rows' -TimeoutSeconds 30 `
    -Probe { Get-L2DashboardBindingRow -Context $Context -TaskType 'STAGING_TO_WIRE' } `
    -Until { param($v) $null -ne $v -and $v.Contains('目录变化') }
$gateRow = Get-L2DashboardBindingRow -Context $Context -TaskType 'WIRE_TO_GATE'
$assertions.Add(
    'L2-CC-09', '看板显示两条暂停及其来源：STAGING_TO_WIRE 目录变化／站点改名，WIRE_TO_GATE 目录变化／站点已不在目录',
    ($stagingRow.Contains('已暂停') -and $stagingRow.Contains('目录变化') -and $stagingRow.Contains('站点改名') -and
        $null -ne $gateRow -and $gateRow.Contains('已暂停') -and $gateRow.Contains('目录变化') -and $gateRow.Contains('站点已不在目录')),
    'STAGING_TO_WIRE: 已暂停 目录变化 站点改名；WIRE_TO_GATE: 已暂停 目录变化 站点已不在目录', "$stagingRow | $gateRow")

$bindingsAfter = Get-L2ActiveBindings -Context $Context
$assertions.Add(
    'L2-CC-10', '目录变化不自动改写绑定：场景结束时生效绑定集与开始时相同，没有按名称重绑',
    ($bindingsAfter -eq $bindingsBefore), $bindingsBefore, $bindingsAfter)

# --- 6. 210 以原名称放回目录：新需求仍以 TASK_TYPE_HELD 挡住，暂停仍未解除 ------------------------------------------
#
# Until here every refusal of a WIRE_TO_GATE demand came from the catalog itself; nothing above tells a hold apart from the
# station simply being gone. With 210 back under its bound name the catalog check passes again, so what still refuses
# the demand can only be the hold -- which only FieldOps releases (control-server#201, review F of #162).

# Assigned before filtering: Get-L2TaskTypeHolds returns its rows wrapped once, and piping the call straight into
# Where-Object would hand the filter the whole list as one object.
$holdsBeforeReturn = Get-L2TaskTypeHolds -Context $Context
$gateHold = @($holdsBeforeReturn | Where-Object { $_.TaskType -eq 'WIRE_TO_GATE' })
Set-Stations @{ '210' = '关卡'; '12' = 'N1-3_N1-7'; '11' = 'C15-13'; '230' = '派工待送取货-临时堆放' }
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
# Two demands are waiting now: the second, kept back since step 4, and a third raised after the gate came back. Both are
# judged: the older one is first in line, so with the hold not consulted it is the one that gets taken.
$third = New-L2WireToGateDemand -Context $Context -Label 'third'
function Get-WaitingDemands {
    , @(foreach ($demand in @($second, $third)) {
            [pscustomobject]@{
                Stage  = Get-L2JourneyStage -Context $Context -Demand $demand
                Reason = Get-L2BacklogReason -Context $Context -Demand $demand
            }
        })
}
function Test-HeldBack([object[]]$Waiting) {
    @($Waiting | Where-Object { $null -eq $_.Stage -and $_.Reason -eq 'TASK_TYPE_HELD' }).Count -eq 2
}
function Format-Waiting([object[]]$Waiting) {
    (@('second', 'third') | ForEach-Object -Begin { $i = 0 } -Process {
            $w = $Waiting[$i++]
            "${_}: $(if ($w.Stage) { $w.Stage } else { 'no journey' }) / $(if ($w.Reason) { $w.Reason } else { '(no reason)' })"
        }) -join '; '
}
$waiting = Wait-L2ConditionOrLast -Description 'both waiting demands are kept back by the hold after the gate came back' `
    -Journal $journal -Criterion 'backlog-after-return' -TimeoutSeconds 60 `
    -Probe { Get-WaitingDemands } -Until { param($v) Test-HeldBack $v }
if (Test-HeldBack $waiting) {
    # Read again rounds later: the verdict is what the rounds settled on. Skipped once the answer is already no -- a
    # taken demand sends the runtime off to RIoT, and nothing more needs to be seen for the verdict.
    $null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
    $waiting = Get-WaitingDemands
}
$holdsAfterReturn = Get-L2TaskTypeHolds -Context $Context
$gateHoldAfter = @($holdsAfterReturn | Where-Object { $_.TaskType -eq 'WIRE_TO_GATE' })
$assertions.Add(
    'L2-CC-11', '210 以原名称放回目录、确认过几轮之后，等着的与新来的 WIRE_TO_GATE 需求都仍不受理，原因码 TASK_TYPE_HELD，那条暂停仍未解除（站点恢复不自动解暂停）',
    ((Test-HeldBack $waiting) -and $gateHold.Count -eq 1 -and $gateHoldAfter.Count -eq 1 -and
        $gateHoldAfter[0].HoldId -eq $gateHold[0].HoldId),
    "second: no journey / TASK_TYPE_HELD; third: no journey / TASK_TYPE_HELD; hold " +
        "$(if ($gateHold.Count -eq 1) { $gateHold[0].HoldId } else { '(none)' }) unreleased",
    "$(Format-Waiting $waiting); hold $(if ($gateHoldAfter.Count -eq 1) { $gateHoldAfter[0].HoldId } else { Format-Holds $gateHoldAfter })")

$journal.Note('Scenario finished.')
