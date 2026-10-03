#Requires -Version 7

<#
一般充电异常不暂停桩（批次9-08，control-server#406；REQ-0175 的负向）：到桩后单停在 HANG 而开始充电的动作不带 407802（带的是别的码），或者带 407802
而车并不停在桩上——任何一条单独出现都不形成「已确认充不上」、不暂停桩、没有暂停事件，单照原样以 ORDER_HANG 交人。

**装置**：单车停在关卡 210（节点 5）。场景把站 211 登记成假 RIoT 的充电桩、经 FieldOps 导入名册；编排器的默认测试策略：强制充电线 30。

**第一种（单独的 HANG）**：那张充电单被注入 HangOnly、结果码 1（调度 09-29：「HANG 加非 407802 的码」不是充不上），推到完成——假 RIoT 把它挂在 9；
车停在 211 上。另等十五秒（scripts/l2/README.md 第 14 条：用等待的返回值断言）：没有暂停、周期仍在途、用途仍是 CHARGING、旅程是 ORDER_HANG。

**第二种（未到桩）**：同一张单先放回 3，改注入 CannotCharge，再推到完成——这一次动作带 407802、单停在 9，可车报在关卡 210，不在桩上。另等十五秒，
同样没有暂停。

**红证据（缺陷版本）**：判据放宽成「单独 HANG 即确认」（结果码、动作与位置都不看），第一种就形成确认，L2-GFD-01 变红。
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
$vehicleKey = $Context.VehicleKey

# Counted in SQL: a query with no rows comes back as $null, and @($null).Count is 1.
function Get-Count([string]$sql) { [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N }

function Get-Cycle {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, WireState, Phase, IFNULL(UpperId, '') AS UpperId FROM ChargingCycles")
}

function Get-Claim {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT Purpose FROM VehiclePurposeClaims WHERE VehicleKey = '$vehicleKey'")
    if ($null -eq $row) { return '(none)' }
    return [string]$row.Purpose
}

function Set-Vehicle([int]$position) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $position; battery = 25
    })
}

# "<holds> holds | <cycle state> <phase> | <claim> | <journey code>": the state that must not move.
function Get-State([string]$journeyId) {
    $cycle = Get-Cycle
    $journey = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT IFNULL(BlockReasonCode, '') AS Code FROM JourneyRuntimes WHERE JourneyId = '$journeyId'")
    "$(Get-Count "SELECT COUNT(*) AS N FROM ChargingStationAllocationHolds") holds | $(${cycle}?.WireState) $(${cycle}?.Phase) | " +
        "$(Get-Claim) | $(${journey}?.Code)"
}

# --- 0. 前置 ------------------------------------------------------------------------------------------------------

$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{
        mapId = $Context.MapId; stationId = $charger; chargeIntervalSeconds = 1; chargePercentPerInterval = 5
    })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet @($vehicleKey) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 charging-general-fault-does-not-pause: station $charger"
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

Set-Vehicle $Context.GateStationRiotId
$upperId = Wait-L2Condition -Description 'the charge order was confirmed by RIoT' `
    -Journal $journal -Criterion 'charge-order-confirmed' -TimeoutSeconds 120 `
    -Probe {
        $cycle = Get-Cycle
        if ($null -eq $cycle -or [string]$cycle.WireState -ne 'EN_ROUTE' -or [string]$cycle.UpperId -eq '') { return $null }
        return [string]$cycle.UpperId
    } `
    -Until { param($v) $null -ne $v }
$journeyId = [string](Get-Cycle).JourneyId

# --- 1. 第一种：到桩、HANG，结果码不是 407802 -------------------------------------------------------------------------

$null = $riot.Command('Put', 'charging/faults', @{ upperId = $upperId; startOutcome = 'HangOnly'; hangResultCode = 1 })
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $vehicleKey })
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })
Set-Vehicle $charger
$null = Wait-L2Condition -Description 'the server named the HANG' `
    -Journal $journal -Criterion 'hang-named' -TimeoutSeconds 60 `
    -Probe { Get-State $journeyId } `
    -Until { param($v) $v.EndsWith('| ORDER_HANG') }
$steady = '0 holds | EN_ROUTE ACTIVE | CHARGING | ORDER_HANG'
$hangAlone = Wait-L2ConditionOrLast -Description 'a hold appeared for a HANG without 407802 (none may)' `
    -Journal $journal -Criterion 'hang-alone-does-not-pause' -TimeoutSeconds 15 `
    -Probe { Get-State $journeyId } `
    -Until { param($v) $v -ne $steady }
$hung = @($riot.Snapshot().body.orders | Where-Object { $null -ne $_ -and [string]$_.upperId -eq $upperId })[0]
$codes = @($hung.missions | Where-Object { [string]$_.type -eq 'act' } | ForEach-Object { [string]$_.resultCode }) -join ','
$assertions.Add(
    'L2-GFD-01',
    '到桩后单停在 HANG（9），开始充电的动作带的是结果码 1、不是 407802：另等十五秒，没有暂停事件，周期仍在途（EN_ROUTE／ACTIVE），用途仍是 CHARGING，旅程照原样是 ORDER_HANG',
    ($hangAlone -eq $steady -and [int]$hung.orderState -eq 9 -and $codes -eq '1'),
    "$steady / order 9 / act code 1",
    "$hangAlone / order $($hung.orderState) / act code $codes")

# --- 2. 第二种：407802 + HANG，但车不在桩上 --------------------------------------------------------------------------

$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3 })
$null = $riot.Command('Put', 'charging/faults', @{ upperId = $upperId; startOutcome = 'CannotCharge' })
Set-Vehicle $Context.GateStationRiotId
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })
# The order went through 3 on the way: the HANG is named afresh before the window starts.
$null = Wait-L2Condition -Description 'the server named the second HANG' `
    -Journal $journal -Criterion 'second-hang-named' -TimeoutSeconds 60 `
    -Probe {
        $order = @($riot.Snapshot().body.orders | Where-Object { $null -ne $_ -and [string]$_.upperId -eq $upperId })[0]
        "$($order.orderState) | $(Get-State $journeyId)"
    } `
    -Until { param($v) $v.StartsWith('9 |') -and $v.EndsWith('| ORDER_HANG') }
$notThere = Wait-L2ConditionOrLast -Description 'a hold appeared for a vehicle not on the charger (none may)' `
    -Journal $journal -Criterion 'not-at-charger-does-not-pause' -TimeoutSeconds 15 `
    -Probe { Get-State $journeyId } `
    -Until { param($v) $v -ne $steady }
$hung = @($riot.Snapshot().body.orders | Where-Object { $null -ne $_ -and [string]$_.upperId -eq $upperId })[0]
$codes = @($hung.missions | Where-Object { [string]$_.type -eq 'act' } | ForEach-Object { [string]$_.resultCode }) -join ','
$assertions.Add(
    'L2-GFD-02',
    '同一张单改为带 407802 停在 HANG，可车报在关卡 210、不在 211 上（未到桩）：另等十五秒，同样没有暂停事件，周期、用途与 ORDER_HANG 都不变',
    ($notThere -eq $steady -and [int]$hung.orderState -eq 9 -and $codes -eq '407802'),
    "$steady / order 9 / act code 407802",
    "$notThere / order $($hung.orderState) / act code $codes")

$journal.Note('一般异常（单独的 HANG、不在桩上的 407802）不暂停桩，单照原样交人。')
