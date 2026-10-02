#Requires -Version 7

<#
充电中断即两侧隔离（批次9-09，control-server#407；REQ-0285、REQ-0286）：压到强制充电线以下 → 分配、去桩、到桩、CHARGING → 假 RIoT 在 45%
停止充电（完成阈值 80 之前，没有任何离桩单）→ 服务端连续两次读到 NO_CHARGE，形成中断确认：桩的分配暂停与车的充电资格暂停同一次写下，
根因 UNKNOWN，车的用途转 CLEARING_MAINTENANCE，周期进清桩中（UNABLE_TO_CHARGE），车留在原地。

**装置**：单车停在关卡 210（节点 5）。场景把站 211 登记成假 RIoT 的充电桩（每 2 秒涨 2%），经 FieldOps 的 import-charger-roster 导入名册，
给这辆车置「到 45% 中断」的注入。编排器的默认测试策略：强制充电线 30、完成阈值 80。隔离的出口在 setup 里配齐（名单 + Host 入口）。

**第一个事实（形成、两侧隔离）**：L2-CII-01 等到两种暂停与清桩中都在，一次读出来断言。

**第二个事实（之后若干轮没有新单、没有第二条暂停）**：L2-CII-02 另等十秒（Wait-L2ConditionOrLast），不读一次就断言（scripts/l2/README.md
第 14 条）：RIoT 上一直只有那一张充电单，命令审计里订单命令 0 条，桩一直是这一趟的占用，两侧各一条暂停。

**红证据（缺陷版本）**：只暂停桩、不暂停车（control-server#407 的变异 M01），L2-CII-01 变红。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SingleRow.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Chargers.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$riot = $Context.Riot

$charger = 211
$chargerName = '充电点1'
$interruptAt = 45
$vehicleKey = $Context.VehicleKey

# Counted in SQL: a query with no rows comes back as an empty array, and in SQL the count is always exactly one row.
function Get-Count([string]$sql) { [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N }

function Get-Cycle {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, WireState, Phase, IFNULL(UpperId, '') AS UpperId, IFNULL(FirstChargingSeenAt, '') AS ChargingSeen " +
        "FROM ChargingCycles")
}

function Get-ChargerHeld {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT State, VehicleKey, JourneyId FROM StationExclusivities WHERE MapId = $($Context.MapId) AND StationId = $charger")
    if ($null -eq $row) { return '(none)' }
    return "$($row.State) $($row.VehicleKey) $($row.JourneyId)"
}

function Get-Claim {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT Purpose, JourneyId FROM VehiclePurposeClaims WHERE VehicleKey = '$vehicleKey'")
    if ($null -eq $row) { return '(none)' }
    return "$($row.Purpose) $($row.JourneyId)"
}

# Both sides as one line: "<station holds> | <vehicle holds>", each "<trigger/reason> <root cause>" joined, or "(none)".
function Get-Pauses {
    $station = Invoke-L2Query -Connection $connection -Sql (
        "SELECT h.""Trigger"" AS Trig, h.RootCause FROM ChargingStationAllocationHolds h " +
        "WHERE h.MapId = $($Context.MapId) AND h.StationId = $charger")
    $vehicle = Invoke-L2Query -Connection $connection -Sql (
        "SELECT Reason FROM VehicleChargingEligibilityHolds WHERE VehicleKey = '$vehicleKey'")
    $s = if ($station.Count -eq 0) { '(none)' } else { (@($station | ForEach-Object { "$($_.Trig) $($_.RootCause)" }) -join '; ') }
    $v = if ($vehicle.Count -eq 0) { '(none)' } else { (@($vehicle | ForEach-Object { [string]$_.Reason }) -join '; ') }
    return "$s | $v"
}

function Get-RiotOrders {
    @($riot.Snapshot().body.orders | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_.upperId })
}

# --- 0. 前置：桩登记到假 RIoT 与名册，路网起来 ------------------------------------------------------------------

$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{
        mapId = $Context.MapId; stationId = $charger; chargeIntervalSeconds = 2; chargePercentPerInterval = 2
    })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet @($vehicleKey) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 charging-interruption-isolates: station $charger"
$journal.Observe('charger-roster', [string]$roster.version, @{ import = $roster })

$null = Wait-L2Condition -Description 'the route graph engine finished a refresh cycle' `
    -Journal $journal -Criterion 'route-graph-ready' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql ("SELECT DesignEdgeCount, RuntimeRefreshedAt, StaleReason FROM RouteGraphSnapshots " +
                "WHERE MapId = $($Context.MapId)")
        if ($rows.Count -eq 0) { return $false }
        return [int]$rows[0].DesignEdgeCount -gt 0 -and
            $null -ne $rows[0].RuntimeRefreshedAt -and [string]$rows[0].RuntimeRefreshedAt -ne '' -and
            ($null -eq $rows[0].StaleReason -or [string]$rows[0].StaleReason -eq '')
    } `
    -Until { param($v) $v }

# --- 1. 低电：分配、建单、到桩、充电 ----------------------------------------------------------------------------

$null = $riot.Command('Put', 'charging/faults', @{ vehicleKey = $vehicleKey; interruptAtPercent = $interruptAt })
$journal.Note("Injected: charging stops by itself at $interruptAt % (below the completion threshold 80), with no departure order.")
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.GateStationRiotId; battery = 25
})

$upperId = Wait-L2Condition -Description 'the charge order was confirmed by RIoT' `
    -Journal $journal -Criterion 'charge-order-confirmed' -TimeoutSeconds 120 `
    -Probe {
        $cycle = Get-Cycle
        if ($null -eq $cycle -or [string]$cycle.WireState -ne 'EN_ROUTE' -or [string]$cycle.UpperId -eq '') { return $null }
        return [string]$cycle.UpperId
    } `
    -Until { param($v) $null -ne $v }
$journeyId = [string](Get-Cycle).JourneyId

$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $vehicleKey })
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })

# --- 2. 中断：形成、两侧同时暂停、进清桩中 --------------------------------------------------------------------------

$formed = Wait-L2Condition -Description 'the interruption was confirmed and both sides paused' `
    -Journal $journal -Criterion 'interruption-isolated' -TimeoutSeconds 120 `
    -Probe {
        $cycle = Get-Cycle
        "$(Get-Pauses) | $(${cycle}?.Phase) $(${cycle}?.WireState)"
    } `
    -Until { param($v) $v.EndsWith('| CLEARING UNABLE_TO_CHARGE') }
$cycle = Get-Cycle
$journey = Read-L2SingleRow -Required -Connection $connection -Sql (
    "SELECT Stage, IFNULL(BlockReasonCode, '') AS Code FROM JourneyRuntimes WHERE JourneyId = '$journeyId'")
$state = "$formed | claim $(Get-Claim) | $(Get-ChargerHeld) | journey $($journey.Stage) $($journey.Code) | charging seen $([string]$cycle.ChargingSeen -ne '')"
$expected = "INTERRUPTION_CONFIRMED UNKNOWN | INTERRUPTION_CONFIRMED | CLEARING UNABLE_TO_CHARGE | claim CLEARING_MAINTENANCE $journeyId | " +
    "OCCUPIED $vehicleKey $journeyId | journey AwaitingPickupArrival CHARGING_INTERRUPTION_CONFIRMED | charging seen True"
$assertions.Add(
    'L2-CII-01',
    '充着电在 45% 停了（完成阈值 80 之前）：桩的分配暂停与车的充电资格暂停都写下（INTERRUPTION_CONFIRMED，根因 UNKNOWN），周期进清桩中、线上 UNABLE_TO_CHARGE，用途转 CLEARING_MAINTENANCE，211 仍是这一趟的占用',
    ($state -eq $expected),
    $expected,
    $state)

# --- 3. 另等：之后若干轮没有新单、没有第二条暂停、不发命令 --------------------------------------------------------------

$quiet = "$upperId | 0 commands | OCCUPIED $vehicleKey $journeyId | INTERRUPTION_CONFIRMED UNKNOWN | INTERRUPTION_CONFIRMED"
$after = Wait-L2ConditionOrLast -Description 'a new order, a command or a second pause appeared after the interruption (none may)' `
    -Journal $journal -Criterion 'nothing-after-interruption' -TimeoutSeconds 10 `
    -Probe {
        $commands = Get-Count "SELECT COUNT(*) AS N FROM RiotOrderCommandAudit"
        "$((Get-RiotOrders) -join ',') | $commands commands | $(Get-ChargerHeld) | $(Get-Pauses)"
    } `
    -Until { param($v) $v -ne $quiet }
$assertions.Add(
    'L2-CII-02',
    '形成之后另等十秒：RIoT 上一直只有那一张充电单（不在原桩重启、不换桩、不移动），命令审计里订单命令 0 条，211 一直是这一趟的占用，两侧各只有一条暂停',
    ($after -eq $quiet),
    $quiet,
    $after)

$journal.Note('充电中断：两侧同时暂停、车留在原地进清桩中，之后不建单、不发命令。')
