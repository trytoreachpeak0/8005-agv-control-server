#Requires -Version 7

<#
充电无进展即两侧隔离（批次9-09，control-server#407；REQ-0285、REQ-0286、REQ-0282）：压到强制充电线以下 → 分配、去桩、到桩、CHARGING →
假 RIoT 一直报 CHARGING、电量停在 25 → 稳定期（5 秒）之后开观察窗口，起点落库、新样本不改它 → 窗口（20 秒）满、增量 0 小于 3%：
形成无进展确认，桩的分配暂停与车的充电资格暂停同一次写下（NO_PROGRESS_CONFIRMED，根因 UNKNOWN），周期进清桩中。

**装置**：单车停在关卡 210（节点 5）。站 211 是假 RIoT 的充电桩，名册经 FieldOps 导入，这辆车置「不涨」注入。策略经 setup 的
ChargingPolicy.Progress 缩短观察参数（稳定期 5 秒、窗口 20 秒、最小增量 3%），其余同默认测试策略；这是本周期冻结的那一版。

**第一个事实（窗口起点不被新样本重置）**：等到观察窗口开了，记下起点；L2-CNP-01 等到形成，断言形成时周期上记的起点仍是开窗那一刻的
（落库的起点只在窗口作废或另开时才变，形成那一次保存不改它），且形成时刻距起点不少于 20 秒。

**第二个事实（两侧隔离、之后不建单不发命令）**：L2-CNP-02 一次读出两侧暂停与清桩中；L2-CNP-03 另等十秒（Wait-L2ConditionOrLast）：
RIoT 上一直只有那一张充电单、命令审计 0 条、211 一直是这一趟的占用、两侧各一条暂停。

**红证据（缺陷版本）**：窗口起点每个样本都重置（control-server#407 的变异 M02），窗口永远满不了，L2-CNP-01 等到超时变红。
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
$window = 20
$vehicleKey = $Context.VehicleKey

# Counted in SQL: a query with no rows comes back as an empty array, and in SQL the count is always exactly one row.
function Get-Count([string]$sql) { [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N }

function Get-Cycle {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, WireState, Phase, IFNULL(UpperId, '') AS UpperId, ChargingPolicyVersion, " +
        "IFNULL(ObservationWindowStartedAt, '') AS WindowStart, IFNULL(ObservationWindowStartPercent, '') AS WindowPercent " +
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
        mapId = $Context.MapId; stationId = $charger; chargeIntervalSeconds = 1; chargePercentPerInterval = 5
    })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet @($vehicleKey) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 charging-no-progress-isolates: station $charger"
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

# --- 1. 低电：分配、建单、到桩、充电（不涨）------------------------------------------------------------------------

$null = $riot.Command('Put', 'charging/faults', @{ vehicleKey = $vehicleKey; noProgress = $true })
$journal.Note('Injected: CHARGING is reported and the battery does not move.')
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

# --- 2. 开窗：记下起点 ----------------------------------------------------------------------------------------------

# The start is read as soon as it is there, whatever the cycle has moved on to since: with a 20 s window the confirmation
# can come before this probe's next read, and confirming does not clear the start (fail-1, evidence/cs407/l2).
$opened = Wait-L2Condition -Description 'the no-progress observation window opened' `
    -Journal $journal -Criterion 'window-opened' -TimeoutSeconds 90 `
    -Probe { $cycle = Get-Cycle; "$(${cycle}?.WindowPercent)|$(${cycle}?.WindowStart)" } `
    -Until { param($v) $v -match '^25\|.+' }
$windowStart = $opened.Substring(3)
$journal.Observe('window-start', $windowStart, @{ seen = $opened })

# --- 3. 形成：窗口满、增量不足 ----------------------------------------------------------------------------------------

$formed = Wait-L2Condition -Description 'no progress was confirmed and both sides paused' `
    -Journal $journal -Criterion 'no-progress-isolated' -TimeoutSeconds 90 `
    -Probe { $cycle = Get-Cycle; "$(Get-Pauses) | $(${cycle}?.Phase) $(${cycle}?.WireState)" } `
    -Until { param($v) $v.EndsWith('| CLEARING UNABLE_TO_CHARGE') }
$cycle = Get-Cycle
$hold = Read-L2SingleRow -Required -Connection $connection -Sql (
    "SELECT HeldAt FROM ChargingStationAllocationHolds WHERE MapId = $($Context.MapId) AND StationId = $charger")
$elapsed = ([DateTimeOffset]::Parse([string]$hold.HeldAt) - [DateTimeOffset]::Parse($windowStart)).TotalSeconds
$assertions.Add(
    'L2-CNP-01',
    "观察窗口起点开窗之后不被新样本重置：形成时周期上的起点仍是开窗那一刻（$windowStart），形成时刻距起点不少于窗口 $window 秒",
    ([string]$cycle.WindowStart -eq $windowStart -and $elapsed -ge $window),
    "window start $windowStart / >= $window s",
    "window start $($cycle.WindowStart) / $([math]::Round($elapsed, 1)) s")

$journey = Read-L2SingleRow -Required -Connection $connection -Sql (
    "SELECT Stage, IFNULL(BlockReasonCode, '') AS Code FROM JourneyRuntimes WHERE JourneyId = '$journeyId'")
$state = "$formed | claim $(Get-Claim) | $(Get-ChargerHeld) | journey $($journey.Stage) $($journey.Code)"
$expected = "NO_PROGRESS_CONFIRMED UNKNOWN | NO_PROGRESS_CONFIRMED | CLEARING UNABLE_TO_CHARGE | claim CLEARING_MAINTENANCE $journeyId | " +
    "OCCUPIED $vehicleKey $journeyId | journey AwaitingPickupArrival CHARGING_NO_PROGRESS_CONFIRMED"
$assertions.Add(
    'L2-CNP-02',
    '一直报 CHARGING、电量不涨：窗口满后桩的分配暂停与车的充电资格暂停都写下（NO_PROGRESS_CONFIRMED，根因 UNKNOWN），周期进清桩中，用途转 CLEARING_MAINTENANCE，211 仍是这一趟的占用',
    ($state -eq $expected),
    $expected,
    $state)

# --- 4. 另等：之后若干轮没有新单、没有第二条暂停、不发命令 --------------------------------------------------------------

$quiet = "$upperId | 0 commands | OCCUPIED $vehicleKey $journeyId | NO_PROGRESS_CONFIRMED UNKNOWN | NO_PROGRESS_CONFIRMED"
$after = Wait-L2ConditionOrLast -Description 'a new order, a command or a second pause appeared after no progress (none may)' `
    -Journal $journal -Criterion 'nothing-after-no-progress' -TimeoutSeconds 10 `
    -Probe {
        $commands = Get-Count "SELECT COUNT(*) AS N FROM RiotOrderCommandAudit"
        "$((Get-RiotOrders) -join ',') | $commands commands | $(Get-ChargerHeld) | $(Get-Pauses)"
    } `
    -Until { param($v) $v -ne $quiet }
$assertions.Add(
    'L2-CNP-03',
    '形成之后另等十秒：RIoT 上一直只有那一张充电单，命令审计 0 条，211 一直是这一趟的占用，两侧各只有一条暂停',
    ($after -eq $quiet),
    $quiet,
    $after)

$journal.Note('充电无进展：窗口起点不被样本重置，窗口满后两侧同时暂停，之后不建单、不发命令。')
