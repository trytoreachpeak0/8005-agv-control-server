#Requires -Version 7

<#
Map 级改名检测（control-server#186；REQ-0341、REQ-0340）：同一 mapId 下地图改名，暂停该图生效绑定的全部任务类型，直到现场接受新名、
解除暂停。

  1. 地图列表读不到（假 RIoT 只让 getALLMapInfoExcludeMapJson 答 500）的那几轮：不加暂停，基线不变（L2-MR-01）。
  2. 改名之前：一条 WIRE_TO_GATE 需求照常受理，走到关卡、走完（L2-MR-02）。
  3. 经假 RIoT 的 PUT /control/v1/maps/{mapId}/name 改名：WIRE_TO_GATE 与 STAGING_TO_WIRE 各一条来源「目录变化」、原因码 MAP_RENAMED
     的暂停（L2-MR-03）；车空闲时新的 WIRE_TO_GATE 需求不受理，原因码 TASK_TYPE_HELD（L2-MR-04）；看板显示「目录变化」「地图改名」
     （L2-MR-05）；基线仍是旧名、新名待接受（L2-MR-06）。
  4. 没接受新名之前，FieldOps 解除暂停被拒，原因码 MAP_RENAME_NOT_ACCEPTED，暂停仍在（L2-MR-07）。
  5. FieldOps accept-map-name 接受新名，再逐个任务类型 release-task-type-station-hold：暂停全部解除，基线换成新名（L2-MR-08）；
     第 3 步那条等着的需求随即受理，走到关卡腿（L2-MR-09）。

红证据取法（缺陷版本）：在本票改动之前的产品代码上跑（本票先红提交：假 RIoT 与本场景已在、产品代码与 fp/v2-impl 相同）。
今天改名后照常派车，所以 L2-MR-03、L2-MR-04 红；那之后 accept-map-name 这个动词不存在，场景在第 5 步抛错结束。
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
$newName = "老厂前线new_wk-L2-$($Context.RunId)"
$stations = @{ '210' = '关卡'; '12' = 'N1-3_N1-7'; '11' = 'C15-13'; '230' = '派工待送取货' }

function Format-Holds([object[]]$Holds) {
    if (@($Holds).Count -eq 0) { return '(none)' }
    return (@($Holds) | ForEach-Object { "$($_.TaskType)/$($_.Source)/$($_.ReasonCode)" }) -join ', '
}

# The baseline row, or $null -- also when the table does not exist, which is the code before this ticket: the red run
# must turn red on the criteria about dispatch and holds, not end on a missing table before it reaches them.
function Get-Baseline {
    try {
        $rows = Invoke-L2Query -Connection $Context.Connection -Sql (
            "SELECT Name, PendingName, AcceptedBy FROM MapNameBaselines WHERE MapId = $mapId")
    } catch {
        if ($_.Exception.Message -notlike '*no such table*') { throw }
        return $null
    }
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Format-Baseline([object]$Baseline) {
    if ($null -eq $Baseline) { return '(no baseline)' }
    return "name=$($Baseline.Name) pending=$(if ($null -eq $Baseline.PendingName) { '(none)' } else { $Baseline.PendingName })"
}

function Get-MapName {
    $snapshot = $riot.Snapshot()
    return [string](@($snapshot.body.mapNames) | Where-Object { $_.mapId -eq $mapId } | Select-Object -First 1).name
}

# FieldOps exits 1 on a refusal, which $Context.InvokeFieldOps turns into an exception; the refusal is what is asserted.
function Invoke-FieldOpsRefusal([string[]]$Arguments) {
    try {
        $null = & $Context.InvokeFieldOps -Arguments $Arguments
        return '(accepted)'
    } catch {
        return $_.Exception.Message
    }
}

$seededName = Get-MapName
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal

# --- 1. 地图列表读不到：不加暂停、基线不变 ------------------------------------------------------------------------

$baselineBefore = Get-Baseline
$journal.Note('Fake RIoT: only the Map list answers 500 from now on.')
$null = $riot.Command('Put', 'maps/list-fault', @{ serverError = $true })
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$holdsDuringFault = Get-L2TaskTypeHolds -Context $Context
$baselineDuringFault = Get-Baseline
$null = $riot.Command('Put', 'maps/list-fault', @{ serverError = $false })
$journal.Note('Fake RIoT: the Map list answers again.')
$assertions.Add(
    'L2-MR-01', '地图列表读不到的几轮不算改名：没有暂停，基线与读失败之前相同',
    (@($holdsDuringFault).Count -eq 0 -and (Format-Baseline $baselineDuringFault) -eq (Format-Baseline $baselineBefore)),
    "(none); $(Format-Baseline $baselineBefore)", "$(Format-Holds $holdsDuringFault); $(Format-Baseline $baselineDuringFault)")

# --- 2. 改名之前：照常派车 ------------------------------------------------------------------------------------------

$first = New-L2WireToGateDemand -Context $Context -Label 'first'
$firstGate = Invoke-L2JourneyToGateLeg -Context $Context -Demand $first
$firstStage = Complete-L2JourneyAtGate -Context $Context -Demand $first -GateIntent $firstGate
$assertions.Add(
    'L2-MR-02', "改名之前（地图名 $seededName）WIRE_TO_GATE 需求照常受理、建出关卡单并走完",
    ($firstGate.Status -eq 'CONFIRMED' -and $firstStage -eq 'Completed'),
    'TO_GATE CONFIRMED / Completed', "TO_GATE $($firstGate.Status) / $firstStage")

# --- 3. 同一 mapId 下改名：全部绑定的任务类型暂停，新需求不受理 ---------------------------------------------------------

$journal.Note("Fake RIoT: map $mapId is renamed from $seededName to $newName under the same id.")
$null = $riot.Command('Put', "maps/$mapId/name", @{ name = $newName })
$holds = Wait-L2ConditionOrLast -Description 'the rename holds both bound task types' `
    -Journal $journal -Criterion 'holds-after-rename' -TimeoutSeconds 60 `
    -Probe { $rows = Get-L2TaskTypeHolds -Context $Context; , $rows } `
    -Until { param($v) @($v | Where-Object { $_.ReasonCode -eq 'MAP_RENAMED' }).Count -ge 2 }
$renameHolds = @($holds | Where-Object { $_.Source -eq 'CATALOG_CHANGE' -and $_.ReasonCode -eq 'MAP_RENAMED' })
$assertions.Add(
    'L2-MR-03', '改名之后 WIRE_TO_GATE 与 STAGING_TO_WIRE 各恰好一条来源目录变化、原因码 MAP_RENAMED 的暂停，没有别的暂停',
    (@($holds).Count -eq 2 -and $renameHolds.Count -eq 2 -and
        (@($renameHolds | ForEach-Object { $_.TaskType } | Sort-Object) -join ',') -eq 'STAGING_TO_WIRE,WIRE_TO_GATE'),
    'STAGING_TO_WIRE/CATALOG_CHANGE/MAP_RENAMED, WIRE_TO_GATE/CATALOG_CHANGE/MAP_RENAMED', (Format-Holds $holds))

$second = New-L2WireToGateDemand -Context $Context -Label 'second'
$null = Wait-L2ConditionOrLast -Description 'the second demand is kept back by the rename hold' `
    -Journal $journal -Criterion 'backlog-second' -TimeoutSeconds 60 `
    -Probe { Get-L2BacklogReason -Context $Context -Demand $second } `
    -Until { param($v) $v -eq 'TASK_TYPE_HELD' }
# The reason is written every round; read it again rounds later, so the verdict is what the rounds settled on and not the
# first answer one of them gave.
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$secondStage = Get-L2JourneyStage -Context $Context -Demand $second
$secondReason = Get-L2BacklogReason -Context $Context -Demand $second
$assertions.Add(
    'L2-MR-04', '改名之后车空闲，新的 WIRE_TO_GATE 需求连续几轮都不受理，原因码 TASK_TYPE_HELD',
    ($null -eq $secondStage -and $secondReason -eq 'TASK_TYPE_HELD'),
    'no journey / TASK_TYPE_HELD', "$(if ($secondStage) { $secondStage } else { 'no journey' }) / $secondReason")

$gateRow = Wait-L2ConditionOrLast -Description 'the dashboard shows the rename hold' `
    -Journal $journal -Criterion 'dashboard-row' -TimeoutSeconds 30 `
    -Probe { Get-L2DashboardBindingRow -Context $Context -TaskType 'WIRE_TO_GATE' } `
    -Until { param($v) $null -ne $v -and $v.Contains('地图改名') }
$stagingRow = Get-L2DashboardBindingRow -Context $Context -TaskType 'STAGING_TO_WIRE'
$assertions.Add(
    'L2-MR-05', '看板暂停卡片上两个任务类型都显示已暂停、来源目录变化、原因地图改名',
    ($null -ne $gateRow -and $gateRow.Contains('已暂停') -and $gateRow.Contains('目录变化') -and $gateRow.Contains('地图改名') -and
        $null -ne $stagingRow -and $stagingRow.Contains('已暂停') -and $stagingRow.Contains('目录变化') -and $stagingRow.Contains('地图改名')),
    'WIRE_TO_GATE、STAGING_TO_WIRE: 已暂停 目录变化 地图改名', "$gateRow | $stagingRow")

$pending = Get-Baseline
$assertions.Add(
    'L2-MR-06', '基线仍是改名前的名称，新名称待接受',
    ($null -ne $pending -and $pending.Name -eq $seededName -and $pending.PendingName -eq $newName),
    "name=$seededName pending=$newName", (Format-Baseline $pending))

# --- 4. 没接受新名之前，解除被拒 --------------------------------------------------------------------------------------

# The catalog FieldOps revalidates against: the stations the fake RIoT serves, which the server has confirmed every round.
$catalogPath = Join-Path $Context.SnapshotRoot 'map-rename-catalog.json'
[ordered]@{
    mapId    = $mapId
    stations = @($stations.GetEnumerator() | Sort-Object { [int]$_.Key } | ForEach-Object {
            [ordered]@{ stationId = [int]$_.Key; stationName = $_.Value }
        })
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $catalogPath -Encoding utf8NoBOM
function Get-ReleaseArguments([string]$TaskType) {
    return @('release-task-type-station-hold', '--map', [string]$mapId, '--task-type', $TaskType,
        '--site-verification', 'L2-SYNTHETIC-SITE-CHECK', '--catalog', $catalogPath, '--reason', 'L2 地图改名复核')
}
$early = Invoke-FieldOpsRefusal (Get-ReleaseArguments 'WIRE_TO_GATE')
$holdsAfterEarly = Get-L2TaskTypeHolds -Context $Context
$assertions.Add(
    'L2-MR-07', '接受新名之前解除 WIRE_TO_GATE 的暂停被拒，原因码 MAP_RENAME_NOT_ACCEPTED，两条暂停都还在',
    ($early.Contains('MAP_RENAME_NOT_ACCEPTED') -and @($holdsAfterEarly).Count -eq 2),
    'refused MAP_RENAME_NOT_ACCEPTED; 2 holds', "$early; $(Format-Holds $holdsAfterEarly)")

# --- 5. 接受新名、逐个解除：恢复派车 --------------------------------------------------------------------------------

$accepted = & $Context.InvokeFieldOps -Arguments @(
    'accept-map-name', '--map', [string]$mapId, '--map-name', $newName, '--reason', 'L2 现场核对地图改名')
$releases = @(foreach ($taskType in @('WIRE_TO_GATE', 'STAGING_TO_WIRE')) {
        & $Context.InvokeFieldOps -Arguments (Get-ReleaseArguments $taskType)
    })
$holdsAfterRelease = Get-L2TaskTypeHolds -Context $Context
$accepting = Get-Baseline
$assertions.Add(
    'L2-MR-08', 'accept-map-name 与两次 release-task-type-station-hold 都成功：没有未解除的暂停，基线换成新名、没有待接受的名称',
    ($accepted.outcome -eq 'OK' -and @($releases | Where-Object { $_.outcome -eq 'OK' }).Count -eq 2 -and
        @($holdsAfterRelease).Count -eq 0 -and $null -ne $accepting -and $accepting.Name -eq $newName -and
        $null -eq $accepting.PendingName),
    "accept OK, 2 releases OK; (none); name=$newName pending=(none)",
    "accept $($accepted.outcome), releases $(@($releases | ForEach-Object { $_.outcome }) -join '/'); " +
        "$(Format-Holds $holdsAfterRelease); $(Format-Baseline $accepting)")

$secondGate = Invoke-L2JourneyToGateLeg -Context $Context -Demand $second
$holdsAtEnd = Get-L2TaskTypeHolds -Context $Context
$assertions.Add(
    'L2-MR-09', '接受新名并解除暂停之后，等着的那条需求受理、建出关卡单；新名称连读多轮也不再加暂停',
    ($secondGate.Status -eq 'CONFIRMED' -and @($holdsAtEnd).Count -eq 0),
    'TO_GATE CONFIRMED; (none)', "TO_GATE $($secondGate.Status); $(Format-Holds $holdsAtEnd)")

$journal.Note('Scenario finished.')
