#Requires -Version 7

<#
清桩选点看点上有没有车（control-server#798）：清桩开往等待点之前只看占用记录，另一辆车停在没有占用行的等待点上时，清桩车照样被派过去，
开到点前停下等放行、一直堵在路上——桩腾出来了，车却卡在路上。

**装置**：两台车都停在关卡 210（节点 5）。A 低电（25），去 211 充电、充不上（注入 407802 + 最终 HANG）进清桩中；B 电量 80，接一条搬运需求
（A 低于强制充电线，不接搬运），在途中停在等待点 214 上：B 有旅程、不是空闲车，214 上没有占用行。215 不可达。空闲返回关着。

**第一个事实（点上有车不派，另等）**：A 的旧单在 RIoT 里结束之后，A 原地排队、码 CHARGING_CLEARANCE_NO_WAITING_POINT，告警（事件 2301）里 214 的
逐点原因是 WAITING_POINT_STOOD_ON；另等十秒：A 没有任何清桩移动的意图、214 一直没有占用行、RIoT 上没有 A 的第二张单。

**第二个事实（车走了照常派，不误伤）**：B 离开 214（回到 210，旅程照旧在途），A 那一轮就预占 214。

**红证据（缺陷版本）**：去掉 IdleReturnEvaluator.ExcludePointAsync 里「点上有车」那一格，A 在旧单结束的那一轮就预占 214 并建单，L2-CSO-02 变红。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SingleRow.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ReadSnapshot.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Chargers.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$riot = $Context.Riot
$mes = $Context.MesIngest

$charger = 211
$chargerName = '充电点1'
$point = 214
$vehicleA = $Context.VehicleKey
$vehicleB = 'BROKERX-L2-0002'
$agvB = 'AGV-L2-002'
$demandId = [guid]::NewGuid()
$serverLog = Join-Path $Context.LogRoot 'control-server.out.log'

function Read-SharedText([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { return [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
}

# Counted in SQL, so there is always exactly one row to read (README, "whole-array return").
function Get-Count([string]$sql) { [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N }

function Get-Cycle {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, WireState, Phase, IFNULL(UpperId, '') AS UpperId FROM ChargingCycles")
}

# Who holds a station, as one line: "<state> <vehicle> <journey>", or "(none)".
function Get-Held([int]$station) {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT State, VehicleKey, JourneyId FROM StationExclusivities WHERE MapId = $($Context.MapId) AND StationId = $station")
    if ($null -eq $row) { return '(none)' }
    return "$($row.State) $($row.VehicleKey) $($row.JourneyId)"
}

function Get-JourneyCode([string]$journeyId) {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT Stage, IFNULL(BlockReasonCode, '') AS Code FROM JourneyRuntimes WHERE JourneyId = '$journeyId'")
    if ($null -eq $row) { return '(none)' }
    return "$($row.Stage) $($row.Code)"
}

function Set-Vehicle([string]$vehicleKey, [int]$position, [int]$battery) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $position; battery = $battery; processingOrder = $false; clearOrderTaskId = $true
    })
}

# B on its way, stopped on a station (waiting at a checkpoint, say): its order runs, it does not move.
function Stop-OnTheWay([string]$vehicleKey, [int]$position, [string]$orderId) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $vehicleKey; procState = 'RUNNING'; movementState = 'MT_WAIT_FOR_CHECKPOINT'; speed = 0
        currentPosition = $position; battery = 80; processingOrder = $true; orderTaskId = $orderId
    })
}

# --- 0. 前置：桩登记到假 RIoT 与名册，路网起来 ------------------------------------------------------------------

$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{
        mapId = $Context.MapId; stationId = $charger; chargeIntervalSeconds = 1; chargePercentPerInterval = 5
    })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet @($vehicleA, $vehicleB) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 charging-clearance-skips-stood-on-waiting-point: station $charger"
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

# --- 1. A 低电去充；B 接一条搬运需求，在途中停在 214 上 -----------------------------------------------------------

Set-Vehicle $vehicleA $Context.GateStationRiotId 25
Set-Vehicle $vehicleB $Context.GateStationRiotId 80

$upperId = Wait-L2Condition -Description "A's charge order was confirmed by RIoT" `
    -Journal $journal -Criterion 'charge-order-confirmed' -TimeoutSeconds 120 `
    -Probe {
        $cycle = Get-Cycle
        if ($null -eq $cycle -or [string]$cycle.WireState -ne 'EN_ROUTE' -or [string]$cycle.UpperId -eq '') { return $null }
        return [string]$cycle.UpperId
    } `
    -Until { param($v) $null -ne $v }
$journeyId = [string](Get-Cycle).JourneyId

# A is below its mandatory charge line and committed to charging: only B can take the demand.
$null = $mes.Command('Put', "demands/$($demandId.ToString('N'))", @{
    sublot = "L2-CSO-$($Context.RunId)"; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})
$transport = Wait-L2Condition -Description "B's transport order was confirmed by RIoT" `
    -Journal $journal -Criterion 'b-transport-confirmed' -TimeoutSeconds 120 `
    -Probe {
        Read-L2SingleRow -Connection $connection -Sql (
            "SELECT j.JourneyId, j.AgvId, j.Stage, i.UpperId, IFNULL(i.OrderId, '') AS OrderId, i.Status FROM JourneyRuntimes j " +
            "JOIN OrderIntents i ON i.UpperId = j.PickupUpperId WHERE j.DemandId = '$($demandId.ToString('D'))'")
    } `
    -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
$null = $riot.Command('Put', "orders/$($transport.UpperId)", @{ orderState = 3; executeVehicleKey = $vehicleB })
Stop-OnTheWay $vehicleB $point ([string]$transport.OrderId)

# --- 2. A 到桩充不上：407802 + HANG，进清桩中 ------------------------------------------------------------------------

$null = $riot.Command('Put', 'charging/faults', @{ upperId = $upperId; startOutcome = 'CannotCharge' })
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $vehicleA })
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })
Set-Vehicle $vehicleA $charger 25

$clearing = Wait-L2ConditionOrLast -Description 'the server confirmed A cannot charge and put it in the clearing loop' `
    -Journal $journal -Criterion 'unable-to-charge-confirmed' -TimeoutSeconds 60 `
    -Probe { $cycle = Get-Cycle; "$(${cycle}?.Phase)" } `
    -Until { param($v) $v -eq 'CLEARING' }
$bJourney = Get-JourneyCode ([string]$transport.JourneyId)
$assertions.Add(
    'L2-CSO-01',
    'A 在 211 上充不上，周期清桩中；B 接了搬运需求、旅程在途，停在 214 上（RIoT 报的当前站），214 没有占用行',
    ($clearing -eq 'CLEARING' -and [string]$transport.AgvId -eq $agvB -and $bJourney.StartsWith('AwaitingPickupArrival') -and
        (Get-Held $point) -eq '(none)'),
    "CLEARING / $agvB AwaitingPickupArrival / 214 (none)",
    "$clearing / $($transport.AgvId) $bJourney / 214 $(Get-Held $point)")

# --- 3. 旧单在 RIoT 里结束：B 站在 214 上，A 不被派去，原地排队（另等） ---------------------------------------------

$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 2 })

# Not a throwing wait: a version that sends A to 214 must go red on L2-CSO-02, which says what it did.
$null = Wait-L2ConditionOrLast -Description 'A queued with no eligible waiting point' `
    -Journal $journal -Criterion 'no-waiting-point' -TimeoutSeconds 60 `
    -Probe { Get-JourneyCode $journeyId } -Until { param($v) $v.EndsWith('CHARGING_CLEARANCE_NO_WAITING_POINT') }
$queued = "AwaitingPickupArrival CHARGING_CLEARANCE_NO_WAITING_POINT | 0 clearance intents | 214 (none)"
$stays = Wait-L2ConditionOrLast -Description 'A was sent to the point B stands on (it may not be)' `
    -Journal $journal -Criterion 'not-sent-to-stood-on-point' -TimeoutSeconds 10 `
    -Probe {
        "$(Get-JourneyCode $journeyId) | " +
            "$(Get-Count "SELECT COUNT(*) AS N FROM OrderIntents WHERE Purpose = 'CLEARANCE_TO_WAITING_POINT'") clearance intents | " +
            "214 $(Get-Held $point)"
    } `
    -Until { param($v) $v -ne $queued }
$warning = [regex]::Matches((Read-SharedText $serverLog), 'CHARGING_CLEARANCE_NO_WAITING_POINT: vehicle [^\r\n]*') |
    Select-Object -First 1 -ExpandProperty Value
$stoodOn = $null -ne $warning -and $warning.Contains("$point=WAITING_POINT_STOOD_ON")
$assertions.Add(
    'L2-CSO-02',
    '旧单结束、B 站在 214 上（没有占用行）、215 不可达：A 原地排队（CHARGING_CLEARANCE_NO_WAITING_POINT），另等十秒没有清桩移动意图、214 一直没有占用行；告警里 214 的逐点原因是 WAITING_POINT_STOOD_ON',
    ($stays -eq $queued -and $stoodOn),
    "$queued / 214=WAITING_POINT_STOOD_ON",
    "$stays / $warning")

# --- 4. B 离开 214（旅程照旧在途）：A 那一轮就预占 214 ----------------------------------------------------------------

Stop-OnTheWay $vehicleB $Context.GateStationRiotId ([string]$transport.OrderId)
$taken = Wait-L2ConditionOrLast -Description 'A took 214 once B had left it' `
    -Journal $journal -Criterion 'point-taken-after-b-left' -TimeoutSeconds 60 `
    -Probe { Get-Held $point } -Until { param($v) $v -ne '(none)' }
$assertions.Add(
    'L2-CSO-03',
    'B 离开 214（回到 210，旅程照旧在途）：A 预占 214（点上没车、没有占用行时照常派，不误伤）',
    ($taken -eq "RESERVED $vehicleA $journeyId"),
    "RESERVED $vehicleA $journeyId",
    $taken)

$journal.Note('清桩选点看点上有没有车：B 在途停在 214 上时 A 不被派去、原地排队；B 离开后 A 照常预占 214。')
