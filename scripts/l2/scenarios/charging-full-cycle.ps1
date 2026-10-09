#Requires -Version 7

<#
一台车走完一整个自动充电周期（批次9-07，control-server#405；REQ-0173、REQ-0281、REQ-0282）：压到强制充电线以下 → 分配、去桩、
到桩、CHARGING → 电量按假 RIoT 的速率涨到完成阈值 → COMPLETE、CHARGING 用途放开、桩仍占用 → 来一条需求，车被派走、假 RIoT 在那张单
队首插了 act(78,2,0) → 车离开桩之后，桩的独占才释放。

**装置**：单车停在关卡 210（节点 5）。场景把站 211 登记成假 RIoT 的充电桩（每秒涨 5%，进出点 212、离桩单经过进出点），并经 FieldOps 的
import-charger-roster 导入名册。编排器的默认测试策略：强制充电线 30、完成阈值 80。

**第一个事实（到桩、充电、充满）**：电量设成 25，车被分配到 211、建单；场景把那张单推到完成，假 RIoT 报车在 211 上充电。服务端先转占用、
进 CHARGING，然后电量涨过 80：周期 COMPLETE，CHARGING 用途释放（原因 CHARGING_COMPLETE），充电旅程收尾，211 仍是这辆车的占用。之后另等
十秒：服务端没有为离桩建任何单（RIoT 上仍只有那一张充电单）。

**第二个事实（派走那一刻桩仍占用、离桩之后才释放）**：发一条需求，派给这辆车，开往取货站的单已确认、假 RIoT 在它队首插了 act(78,2,0)。
从这一刻起另等十秒，211 一直是这一趟的占用——不读一次就断言（scripts/l2/README.md 第 14 条）。然后让 RIoT 执行那张单（假 RIoT 的离桩动作
停止充电）、车到了机台 12：另等释放，原因 CHARGER_RELEASED_ON_DEPARTURE，周期以 CHARGING_DEPARTED 收尾、回 NOT_CHARGING。
队首的 act(78,2,0) 只作为 RIoT 行为的观测记下，不当离桩证据（调度 09-29，PR #414 审查）。

**红证据（缺陷版本）**：离桩清扫把「这辆车已有下一趟旅程」当成离桩证据（下达即释放），L2-CFC-04 变红。
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
$enterExit = 212
$machine = 12
$vehicleKey = $Context.VehicleKey

# Counted in SQL: a query with no rows comes back as $null, and @($null).Count is 1.
function Get-Count([string]$sql) { [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N }

# The one charging cycle of this run (L2SingleRow.psm1: two rows read as a stand-in that equals nothing below).
function Get-Cycle {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, WireState, Phase, IFNULL(UpperId, '') AS UpperId, IFNULL(EndReason, '') AS EndReason, " +
        "IFNULL(ArrivedAt, '') AS ArrivedAt, IFNULL(CompletedAt, '') AS CompletedAt, IFNULL(ReleasedAt, '') AS ReleasedAt " +
        "FROM ChargingCycles")
}

# Who holds the charger, as one line: "<state> <vehicle> <journey>", or "(none)".
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

function Get-RiotOrders {
    @($riot.Snapshot().body.orders | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_.upperId })
}

function Get-Vehicle {
    @($riot.Snapshot().body.vehicles | Where-Object { $null -ne $_ -and [string]$_.deviceKey -eq $vehicleKey })[0]
}

function Publish-Demand {
    $guid = [guid]::NewGuid()
    $journal.Note("Publishing demand $($guid.ToString('N')).")
    $null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot = "L2-CFC-$($Context.RunId)"; area = 'N1-3'; eqp = 'EQP-L2-CFC-01'
        package = 'L2-PACKAGE'; maxBoxCount = 4
    })
    return $guid.ToString('D')
}

# --- 0. 前置：桩登记到假 RIoT 与名册，路网起来 ------------------------------------------------------------------

$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{
        mapId = $Context.MapId; stationId = $charger; enterExitStationId = $enterExit; expandDeparture = $true
        chargeIntervalSeconds = 1; chargePercentPerInterval = 5
    })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet @($vehicleKey) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 charging-full-cycle: station $charger"
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

# The charge order runs and finishes: the fake RIoT starts charging and puts the vehicle on the charger.
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $vehicleKey })
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })

$charging = Wait-L2Condition -Description 'the cycle is CHARGING at the charger' `
    -Journal $journal -Criterion 'cycle-charging' -TimeoutSeconds 60 `
    -Probe { $cycle = Get-Cycle; "$(${cycle}?.WireState) | $(Get-ChargerHeld)" } `
    -Until { param($v) $v.StartsWith('CHARGING |') -or $v.StartsWith('COMPLETE |') }
$cycle = Get-Cycle
$journeyId = [string]$cycle.JourneyId
$assertions.Add(
    'L2-CFC-01',
    '充电单完成、车在 211 上报 CHARGING：211 由预占转为这一趟的占用，周期进 CHARGING（或已涨满到 COMPLETE），记下了到桩时刻',
    ($charging -in @("CHARGING | OCCUPIED $vehicleKey $journeyId", "COMPLETE | OCCUPIED $vehicleKey $journeyId") -and
        [string]$cycle.ArrivedAt -ne ''),
    "CHARGING | OCCUPIED $vehicleKey $journeyId (arrived)",
    "$charging (arrived '$($cycle.ArrivedAt)')")

# --- 2. 充满：COMPLETE、用途放开、桩仍占用、不为离桩建单 --------------------------------------------------------

$complete = Wait-L2Condition -Description 'the cycle is COMPLETE' `
    -Journal $journal -Criterion 'cycle-complete' -TimeoutSeconds 90 `
    -Probe { [string](Get-Cycle).WireState } `
    -Until { param($v) $v -eq 'COMPLETE' }
$journey = Read-L2SingleRow -Required -Connection $connection -Sql (
    "SELECT Stage, IFNULL(BlockReasonCode, '') AS Code FROM JourneyRuntimes WHERE JourneyId = '$journeyId'")
$released = Read-L2SingleRow -Required -Connection $connection -Sql (
    "SELECT IFNULL(ReleaseReason, '') AS Reason FROM VehiclePurposeClaimRecords WHERE JourneyId = '$journeyId'")
$battery = [int](Get-Vehicle).battery
$state = "$complete | claim $(Get-Claim) | $(Get-ChargerHeld) | journey $($journey.Stage) $($journey.Code) | released $($released.Reason)"
$expectedComplete = "COMPLETE | claim (none) | OCCUPIED $vehicleKey $journeyId | journey Completed CHARGING_COMPLETE | released CHARGING_COMPLETE"
$assertions.Add(
    'L2-CFC-02',
    '电量涨过完成阈值 80：周期 COMPLETE，CHARGING 用途释放（原因 CHARGING_COMPLETE），充电旅程以 CHARGING_COMPLETE 收尾；211 仍是这辆车的占用',
    ($state -eq $expectedComplete -and $battery -ge 80),
    "$expectedComplete / battery >= 80",
    "$state / battery $battery")

# 另等：充满之后的十几轮里，服务端不为离桩建任何单，车也不被移动。
$riotAfterFull = Wait-L2ConditionOrLast -Description 'an order to take the full vehicle off the charger appeared (none may)' `
    -Journal $journal -Criterion 'no-departure-order-while-idle' -TimeoutSeconds 10 `
    -Probe { "$((Get-RiotOrders) -join ',') | $(Get-ChargerHeld)" } `
    -Until { param($v) $v -ne "$upperId | OCCUPIED $vehicleKey $journeyId" }
$assertions.Add(
    'L2-CFC-03',
    '充满之后没有新的需求：服务端不为离桩建任何单（RIoT 上仍只有那一张充电单），211 仍被这辆车占着',
    ($riotAfterFull -eq "$upperId | OCCUPIED $vehicleKey $journeyId"),
    "$upperId | OCCUPIED $vehicleKey $journeyId",
    $riotAfterFull)

# --- 3. 派走：下达那一刻桩仍占用 ---------------------------------------------------------------------------------

$demand = Publish-Demand
$pickup = Wait-L2Condition -Description 'the pickup order of the demand was confirmed' `
    -Journal $journal -Criterion 'pickup-confirmed' -TimeoutSeconds 120 `
    -Probe {
        Read-L2SingleRow -Connection $connection -Sql (
            "SELECT UpperId, IFNULL(OrderId, '') AS OrderId, Status, VehicleKey FROM OrderIntents " +
            "WHERE DemandId = '$demand' AND Purpose = 'TO_PICKUP'")
    } `
    -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
$pickupOrder = @($riot.Snapshot().body.orders | Where-Object { $null -ne $_ -and [string]$_.upperId -eq [string]$pickup.UpperId })[0]
$head = $pickupOrder.missions[0]
# The control plane's snapshot writes an act without actionParam2 (only the data-plane answer in the real RIoT's field
# names carries it), so a missing actionParam2 reads as '-'; when it is there it must be 0.
$param2 = $head.PSObject.Properties['actionParam2']
$headText = "$($head.type) $($head.actionId) $($head.actionParam1) $(if ($null -eq $param2) { '-' } else { $param2.Value })"
$journal.Observe('departure-order-head', $headText, @{ order = $pickupOrder })

# 另等：单已下达、车还在 211 的这段时间里，211 会不会被放掉（它不能）。
$whileStill = Wait-L2ConditionOrLast -Description 'the charger was released while the vehicle still stood on it (it must not)' `
    -Journal $journal -Criterion 'occupied-after-next-order' -TimeoutSeconds 10 `
    -Probe { "$(Get-ChargerHeld) | $([string](Get-Cycle).WireState)" } `
    -Until { param($v) $v -ne "OCCUPIED $vehicleKey $journeyId | COMPLETE" }
$assertions.Add(
    'L2-CFC-04',
    '需求派给了充满的这辆车（仍报 CHARGING），开往取货站的单已确认、假 RIoT 在它队首插了 act(78,2,0)；从下达起另等十秒，211 一直是充电那一趟的占用、周期一直是 COMPLETE（下达下一单不释放）',
    ([string]$pickup.VehicleKey -eq $vehicleKey -and $headText -in @('act 78 2 0', 'act 78 2 -') -and
        $whileStill -eq "OCCUPIED $vehicleKey $journeyId | COMPLETE"),
    "$vehicleKey / act 78 2 0|- / OCCUPIED $vehicleKey $journeyId | COMPLETE",
    "$($pickup.VehicleKey) / $headText / $whileStill")

# --- 4. 离桩：RIoT 执行那张单（离桩动作停止充电），车到机台 12；三项确认之后才释放 ---------------------------------

$null = $riot.Command('Put', "orders/$($pickup.UpperId)", @{ orderState = 3; executeVehicleKey = $vehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $vehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = [string]$pickup.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $machine; processingOrder = $false; clearOrderTaskId = $true
})

# One read snapshot (control-server#510): the cycle is read before the release, so a release committed between the two came
# back next to the cycle from before it.
# Deliberately waits only for the charger released, and compares the whole line: inside one read snapshot, the first read that shows the charger released
# shows everything committed with it. That pins the premise this criterion rests on -- releasing the charger and ending the cycle are one transaction (ChargingAllocator.CloseCompletedCycleAsync) -- so splitting
# that commit in the product turns this red, where waiting for the whole line would wait out the split and stay green
# (control-server#510, review S1).
$expectedDeparture = '(none) | CHARGER_RELEASED_ON_DEPARTURE | ENDED NOT_CHARGING CHARGING_DEPARTED'
$afterDeparture = Wait-L2ConditionOrLast -Description 'the charger was released on departure' `
    -Journal $journal -Criterion 'charger-released-on-departure' -TimeoutSeconds 60 `
    -Probe {
        Invoke-L2ReadSnapshot -Connection $connection -Read {
            $cycle = Get-Cycle
            $record = Read-L2SingleRow -Connection $connection -Sql (
                "SELECT IFNULL(ReleaseReason, '') AS Reason FROM StationExclusivityRecords " +
                "WHERE MapId = $($Context.MapId) AND StationId = $charger AND JourneyId = '$journeyId'")
            "$(Get-ChargerHeld) | $(${record}?.Reason) | $(${cycle}?.Phase) $(${cycle}?.WireState) $(${cycle}?.EndReason)"
        }
    } `
    -Until { param($v) $v.StartsWith('(none)') }
$vehicle = Get-Vehicle
$assertions.Add(
    'L2-CFC-05',
    '车离开 211、不再充电、桩可确认空闲之后：211 的独占释放（原因 CHARGER_RELEASED_ON_DEPARTURE），周期以 CHARGING_DEPARTED 收尾、回 NOT_CHARGING',
    ($afterDeparture -eq $expectedDeparture -and
        [string]$vehicle.batteryState -eq 'NO_CHARGE' -and [string]$vehicle.currentPosition -eq [string]$machine),
    '(none) | CHARGER_RELEASED_ON_DEPARTURE | ENDED NOT_CHARGING CHARGING_DEPARTED / NO_CHARGE at 12',
    "$afterDeparture / $($vehicle.batteryState) at $($vehicle.currentPosition)")

$journal.Note('一个完整的自动充电周期：去桩、充电、充满放开用途而桩仍占用、派走那一刻桩不放、离桩之后才释放。')
