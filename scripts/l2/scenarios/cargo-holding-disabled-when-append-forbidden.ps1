#Requires -Version 7

<#
本区不允许途中追加时不持货（批次7-07，control-server#212；ADR-cross-0057）。

持货等单等的是「有人追加进来」。本区不许追加时等不来任何东西，持货只会让每一趟平白多停一个持货超时——所以这时装货阶段
照批次7-07 之前的样子走：装完即 CLOSED/PLANNED_LOADING_COMPLETE，车过了站点等待就离站；而且**不多发任何一张快照**，
车载端看到的车辆业务状态快照逐行与批次7-07 之前相同（L1 的对照在 Batch7CargoHoldingTests 里逐字比；这里在跨进程的装置上
看它的三个可观测后果）。

「不许追加」有两种写法，都必须得到这个结果，所以同一台服务端上走两趟：
1. 第一趟：参数一版，途中追加上限 0（setup 预置）；
2. 服务端不停，经 FieldOps 导入一版上限为空（未配置）的参数；
3. 第二趟：同样的判据再判一遍。

每一趟的三个判据：
- 装货阶段从没出现过 CARGO_HOLDING_WAIT / VEHICLE_FULL，最后落在 CLOSED/PLANNED_LOADING_COMPLETE；
- 装完到关卡腿建单不超过 25 秒（站点等待 10 秒；持货的话至少 40 秒）；
- 发给车的快照里没有任何一张带持货期限，状态只有 LOADING 与 CLOSED/PLANNED_LOADING_COMPLETE。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
. (Join-Path $PSScriptRoot 'CargoHoldingCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$zone = [string]$Context.DispatchZone
# 本趟之前已经在发件箱里的快照：每趟只判自己那一段。
$seenSnapshots = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

function Get-TripIntent([string]$DemandId, [string]$Purpose) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT UpperId, OrderId, Status, CreatedAt FROM OrderIntents WHERE DemandId = '$DemandId' AND Purpose = '$Purpose'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Invoke-Trip([string]$Label, [string]$IdPrefix, [string]$What) {
    $demand = New-L2CargoDemand $Label 'N1-3' 1 $Context.RunId
    Publish-L2CargoDemand $Context $demand
    $journey = Wait-L2Condition -Description "demand $Label was accepted" -Journal $journal -Criterion "journey-$Label" `
        -TimeoutSeconds 120 -Probe { Get-L2CargoJourney $connection $demand.Id } `
        -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
    $journeyId = [string]$journey.JourneyId
    $null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $Context.PickupStationRiotId

    # 离站前那一段每一次读到的装货阶段都记下来：WAIT 哪怕只出现过一瞬也算持货了。
    $phases = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    $gateIntent = Wait-L2ConditionOrLast -Description "demand $Label's vehicle left for the gate" -Journal $journal `
        -Criterion "gate-intent-$Label" -TimeoutSeconds 120 `
        -Probe {
            $row = Get-L2CargoJourney $connection $demand.Id
            if ($null -ne $row) { $null = $phases.Add((Format-L2CargoJourney $row).Split(' ')[1]) }
            Get-TripIntent $demand.Id 'TO_GATE'
        } `
        -Until { param($v) $null -ne $v }
    $row = Get-L2CargoJourney $connection $demand.Id
    $startedAt = if ($null -eq $row.CargoHoldingStartedAt -or [string]$row.CargoHoldingStartedAt -eq '') { $null } else {
        [DateTimeOffset]::Parse([string]$row.CargoHoldingStartedAt, [Globalization.CultureInfo]::InvariantCulture) }
    $leftAt = if ($null -eq $gateIntent) { $null } else {
        [DateTimeOffset]::Parse([string]$gateIntent.CreatedAt, [Globalization.CultureInfo]::InvariantCulture) }
    $held = if ($null -ne $startedAt -and $null -ne $leftAt) { $leftAt - $startedAt } else { $null }
    $journal.Note("Trip $Label ($What): phases seen $(@($phases) -join ','); loaded at $startedAt, gate leg at $leftAt.")

    $assertions.Add(
        "$IdPrefix-1", "$What：装货阶段从没进入持货等单或整车满，离站前已是 CLOSED/PLANNED_LOADING_COMPLETE",
        (@($phases | Where-Object { $_ -like 'CARGO_HOLDING_WAIT*' -or $_ -like 'VEHICLE_FULL*' }).Count -eq 0 -and
            [string]$row.LoadingPhaseState -eq 'CLOSED' -and [string]$row.LoadingClosedReason -eq 'PLANNED_LOADING_COMPLETE'),
        'no WAIT/FULL; CLOSED/PLANNED_LOADING_COMPLETE', "seen $(@($phases) -join ','); now $(Format-L2CargoJourney $row)")
    $assertions.Add(
        "$IdPrefix-2", "$What：装完到关卡腿建单不超过 25 秒（站点等待 10 秒；持货的话至少 40 秒）",
        ($null -ne $held -and $held -le [TimeSpan]::FromSeconds(25)),
        '<= 25 s', $(if ($null -eq $held) { "(loaded $startedAt, gate leg $leftAt)" } else { "$([Math]::Round($held.TotalSeconds, 1)) s" }))

    $null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $Context.GateStationRiotId
    $done = Wait-L2ConditionOrLast -Description "trip $Label completed" -Journal $journal -Criterion "completed-$Label" `
        -TimeoutSeconds 120 -Probe { Get-L2CargoJourney $connection $demand.Id } `
        -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'Completed' }

    $all = Get-L2LoadingPhaseSnapshots $connection
    $mine = @($all | Where-Object { -not $seenSnapshots.Contains($_.MessageId) })
    foreach ($snapshot in $all) { $null = $seenSnapshots.Add($snapshot.MessageId) }
    $journal.Observe("loading-phase-snapshots-$Label", (Format-L2LoadingPhaseSnapshots $mine), @{ snapshots = $mine })
    $labels = @($mine | ForEach-Object { if ($_.Reason) { "$($_.State)/$($_.Reason)" } else { $_.State } } | Sort-Object -Unique)
    $withDeadline = @($mine | Where-Object { $null -ne $_.Deadline })
    $assertions.Add(
        "$IdPrefix-3", "$What：这一趟发给车的快照没有一张带持货期限，状态只有 LOADING 与 CLOSED/PLANNED_LOADING_COMPLETE；旅程走完",
        ($mine.Count -ge 1 -and $withDeadline.Count -eq 0 -and
            @($labels | Where-Object { $_ -notin @('LOADING', 'CLOSED/PLANNED_LOADING_COMPLETE') }).Count -eq 0 -and
            $null -ne $done -and [string]$done.Stage -eq 'Completed'),
        'LOADING, CLOSED/PLANNED_LOADING_COMPLETE; no deadline; Completed',
        "$($labels -join ', '); $($withDeadline.Count) with deadline; $(Format-L2CargoJourney $done)")
}

Initialize-L2CargoRig $Context

# --- 1. 上限为 0 -----------------------------------------------------------------------------------------

$preset = Invoke-L2Query -Connection $connection -Sql (
    "SELECT p.EnRouteAdditionMaxPathCostIncrease AS Allowance FROM DispatchZoneParameters p " +
    "WHERE p.DispatchZone = '$zone' AND p.Version = (SELECT MAX(Version) FROM DispatchZoneParameterVersions)")
$assertions.Add(
    'L2-CHD-01', "前置：当前参数版本里 $zone 的途中追加上限是 0",
    ($preset.Count -eq 1 -and [string]$preset[0].Allowance -eq '0'), '0',
    $(if ($preset.Count -eq 0) { '(no row)' } else { [string]$preset[0].Allowance }))

Invoke-Trip 'A' 'L2-CHD-02' '上限为 0'

# --- 2. 服务端不停，导入一版「未配置」 -----------------------------------------------------------------------

$csv = Join-Path $Context.SnapshotRoot 'dispatch-zone-parameters-unconfigured.csv'
[IO.File]::WriteAllText($csv, "dispatch_zone,en_route_addition_max_path_cost_increase_mm,starvation_threshold_seconds`n$zone,,`n",
    [Text.UTF8Encoding]::new($false))
$imported = & $Context.InvokeFieldOps -Arguments @('import-dispatch-zone-parameters', '--input', $csv)
$current = Invoke-L2Query -Connection $connection -Sql (
    "SELECT p.EnRouteAdditionMaxPathCostIncrease AS Allowance FROM DispatchZoneParameters p " +
    "WHERE p.DispatchZone = '$zone' AND p.Version = (SELECT MAX(Version) FROM DispatchZoneParameterVersions)")
$assertions.Add(
    'L2-CHD-03', "服务端不停导入一版新参数：$zone 的途中追加上限为空（未配置）",
    ([string]$imported.outcome -eq 'OK' -and $current.Count -eq 1 -and
        ($null -eq $current[0].Allowance -or [string]$current[0].Allowance -eq '')),
    'OK, (unconfigured)',
    "$($imported.outcome), $(if ($current.Count -eq 0) { '(no row)' } else { "'$($current[0].Allowance)'" })")

# --- 3. 未配置 -------------------------------------------------------------------------------------------

Invoke-Trip 'B' 'L2-CHD-04' '上限未配置'

$journal.Note('本区不许途中追加（上限 0 或未配置）时不持货：装完即关闭、过了站点等待就离站、快照里没有持货期限。')
