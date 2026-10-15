#Requires -Version 7

<#
真装置：待送点上车缺电而车上有货，自动出发、先送货，旅程收尾之后进充电分配（program#184 第一波集成准备；control-server#713 的
CP-0013 一支，向量 CV-STAGING-CHARGING-REQUIRED-DEPARTURE-WITH-CARGO；用户 10-14 改定「先送货再充电」）。真车载端 WPF + 真
slots-simulator + 充电桩 211。

**形状**：一条待送需求甲（N1-3 → 12 号站，1 个花篮），车开到派工待送站 305，甲经 UIA 录入、装上。之后没有等着的需求，车在待送点上
持货等单（control-server#710）——这是前提，几轮之后再读一次确认它没走。然后电量压到 25（编排器的默认测试策略：强制充电线 30）。
服务端判缺电带货：装货阶段以 CHARGING_REQUIRED 关闭，带着甲走正常离站，不插充电停靠；车到 12 号站卸下甲、旅程收尾之后，充电分配接手，
把车分到 211。

判据（`L2-RSC-*`）：
- 01 前提：甲 Committed，车在 305 持货等单（CARGO_HOLDING_WAIT），几轮之后仍没有离站核验；屏上「出发」仍可按（车在等客户）。
- 02 缺电出发：装货阶段 CLOSED / CHARGING_REQUIRED，屏上 LoadingClosedReason 读到 CHARGING_REQUIRED；服务端发出开往卸货站的离站核验；
  这趟旅程的停靠里没有充电停靠，此刻也没有充电周期（先送货）。
- 03 送达：车到 12 号站卸下甲（Committed）、甲 Succeeded、旅程 Completed。
- 04 进充电分配：旅程收尾之后车被分到 211，周期 EN_ROUTE、充电单被 RIoT 确认、车持有 CHARGING 用途；单跑完，周期 CHARGING，屏上
  ChargingStatus 读到 CHARGING（real-onboard-charging-cycle 的读法）。

**红证据**：本条是真装置场景，红证据在 control-server#713 的单测与合成装置上取（去掉缺电带货一支，车在待送点上一直等，02 等不到关闭）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SingleRow.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Chargers.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2MultiStopJourney.psm1') -Force
. (Join-Path $PSScriptRoot 'StagingDepartureRigCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$riot = $Context.Riot
$onboard = $Context.Onboard

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: its criteria read the shipped HMI through UI Automation.'
}

$all = @('L2-RSC-01', 'L2-RSC-02', 'L2-RSC-03', 'L2-RSC-04')
$vehicleKey = [string]$Context.VehicleKey
$charger = 211
$chargerName = '充电点1'
$enterExit = 212
$lowBattery = 25
# 甲（N1-3）卸在 12 号站 `N1-3_N1-7`（setup 的 Stations）。
$dropOffRiotId = 12
$a = New-L2StagingDemand 'A' 'N1-3' 'L2-RSC' $Context.RunId

function Get-Cycle {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, StationId, WireState, IFNULL(UpperId, '') AS UpperId, IFNULL(OrderConfirmedAt, '') AS OrderConfirmedAt " +
        "FROM ChargingCycles WHERE VehicleKey = '$vehicleKey'")
}

function Get-Claim {
    $row = Read-L2SingleRow -Connection $connection -Sql "SELECT Purpose, JourneyId FROM VehiclePurposeClaims WHERE VehicleKey = '$vehicleKey'"
    if ($null -eq $row) { return '(none)' }
    return "$($row.Purpose) $($row.JourneyId)"
}

# --- 0. 充电桩登记到假 RIoT 与名册 ------------------------------------------------------------------------------------

$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{
        mapId = $Context.MapId; stationId = $charger; enterExitStationId = $enterExit; expandDeparture = $false
        chargeIntervalSeconds = 1; chargePercentPerInterval = 2
    })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet @($vehicleKey) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 real-onboard-staging-low-battery-departs-with-cargo: station $charger"
$journal.Observe('charger-roster', [string]$roster.version, @{ import = $roster })

# --- 1. 甲在待送点装上，车持货等单 ------------------------------------------------------------------------------------

Initialize-L2CargoRig $Context
Assert-L2RigBaselineSlots $Context 1
Publish-L2StagingDemand $Context $a 'EQP-L2-RSC-01'
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $script:StagingStationRiotId
$loadA = Invoke-L2RigLoad $Context $journeyId $a

# 车在待送点上带货等客户的装货阶段有两种：分区许途中追加时 CARGO_HOLDING_WAIT；不许时（本 setup 没给追加上限）装完本站就是
# CLOSED / PLANNED_LOADING_COMPLETE，车照样不走（control-server#709；缺电带货一支两种都管）。第一次真装置跑只认前一种，
# 把后一种当成「没在等」（证据 r3/real-onboard-staging-low-battery-departs-with-cargo-1）。
$waitingPhases = @('CARGO_HOLDING_WAIT', 'CLOSED/PLANNED_LOADING_COMPLETE')
function Get-Phase {
    $row = Get-L2StagingJourney $connection $journeyId
    if ($null -eq $row) { return $null }
    return ([string]$row.LoadingPhaseState -eq 'CLOSED') ? "CLOSED/$($row.LoadingClosedReason)" : [string]$row.LoadingPhaseState
}
$holding = Wait-L2RealOrLast -Description 'the vehicle waits with its cargo at the staging station' -Journal $journal -Criterion 'holding' `
    -TimeoutSeconds 60 -Probe { Get-Phase } -Until { param($v) $v -in $waitingPhases }
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
# Whole array already (return , ...): wrapped in @() its Count would be 1 whatever it held (Test-L2WholeArrayReturn).
$checksWhileHolding = Get-L2DepartureCheckTargets $connection
$stillHolding = Get-Phase
$offered = Test-L2DepartureButtonPressable $onboard
$assertions.Add(
    'L2-RSC-01', '前提：甲 Committed，车在待送点上带货等客户（CARGO_HOLDING_WAIT 或 CLOSED/PLANNED_LOADING_COMPLETE），几轮之后不变、没有离站核验，屏上「出发」仍可按',
    ($loadA.Status -eq 'Committed' -and $holding -in $waitingPhases -and $stillHolding -eq $holding -and
        $checksWhileHolding.Count -eq 0 -and $offered),
    'A Committed / the same waiting phase twice / 离站核验 0 / 出发 pressable',
    "A $($loadA.Status) / $holding then $stillHolding / 离站核验 $($checksWhileHolding.Count) / 出发 $(if ($offered) { 'pressable' } else { 'not offered' })")
if ($stillHolding -notin $waitingPhases -or $checksWhileHolding.Count -ne 0) {
    Add-L2RealNotReached $assertions @($all | Where-Object { $_ -ne 'L2-RSC-01' }) '车没有在待送点上持货等单，缺电出发无从区分'
    return
}

# --- 2. 缺电：自动出发、先送货 --------------------------------------------------------------------------------------

$journal.Note("Battery drops to $lowBattery, below the default test policy's mandatory charge line of 30.")
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $script:StagingStationRiotId; battery = $lowBattery
})
$closed = Wait-L2RealOrLast -Description 'the loading phase closed for want of charge' -Journal $journal -Criterion 'closed-charging-required' `
    -TimeoutSeconds 60 -Probe { Get-L2StagingJourney $connection $journeyId } `
    -Until { param($v) $null -ne $v -and [string]$v.LoadingPhaseState -eq 'CLOSED' -and [string]$v.LoadingClosedReason -eq 'CHARGING_REQUIRED' }
$leaving = Wait-L2RealOrLast -Description 'a departure check towards the drop-off was sent' -Journal $journal -Criterion 'leaving' `
    -TimeoutSeconds 60 -Probe { Get-L2DepartureCheckTargets $connection } `
    -Until { param($v) @($v | Where-Object { $_ -ne $script:StagingStationName }).Count -ge 1 }
$screenReason = Wait-L2RealOrLast -Description 'the HMI shows the loading phase closed for charging' -Journal $journal `
    -Criterion 'loading-closed-on-screen' -TimeoutSeconds 30 -Probe { Get-L2LoadingPhaseLine $onboard 'LoadingClosedReason' } `
    -Until { param($v) $v -eq 'CHARGING_REQUIRED' }
# Invoke-L2Query hands the rows back whole (return , $rows): assigned first, then walked (CargoHoldingCommon.ps1).
$stopRows = Get-L2JourneyStops $connection $journeyId
$stopRoles = @(foreach ($stop in $stopRows) { "$($stop.StopRole)@$($stop.StationRiotId)" })
$cycleBeforeDelivery = Get-Cycle
$assertions.Add(
    'L2-RSC-02', '缺电带货：装货阶段 CLOSED/CHARGING_REQUIRED、屏上读到 CHARGING_REQUIRED，发出开往卸货站的离站核验；不插充电停靠、此刻没有充电周期',
    ($null -ne $closed -and [string]$closed.LoadingClosedReason -eq 'CHARGING_REQUIRED' -and $screenReason -eq 'CHARGING_REQUIRED' -and
        @($leaving | Where-Object { $_ -ne $script:StagingStationName }).Count -ge 1 -and
        @($stopRoles | Where-Object { $_ -like "*@$charger" }).Count -eq 0 -and $null -eq $cycleBeforeDelivery),
    "CLOSED/CHARGING_REQUIRED / screen CHARGING_REQUIRED / check to the drop-off / no stop @$charger / no cycle",
    "$(${closed}?.LoadingPhaseState)/$(${closed}?.LoadingClosedReason) / screen '$screenReason' / checks to [$(@($leaving) -join ', ')] / stops [$($stopRoles -join ' ')] / cycle $(if ($null -eq $cycleBeforeDelivery) { '(none)' } else { $cycleBeforeDelivery.WireState })")

# --- 3. 送达 -------------------------------------------------------------------------------------------------------

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $dropOffRiotId
$unload = Invoke-L2RigUnloadNext $Context $journeyId @()
$completed = Wait-L2RealOrLast -Description 'the journey completed at the drop-off' -Journal $journal -Criterion 'journey-completed' `
    -TimeoutSeconds 120 -Probe { [string](Get-L2StagingJourney $connection $journeyId).Stage } -Until { param($v) $v -eq 'Completed' }
$statusA = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($a.Id)'"
$assertions.Add(
    'L2-RSC-03', '车到 12 号站卸下甲（Committed）、甲 Succeeded、旅程 Completed',
    ($unload.DemandId -eq $a.Id -and $unload.Status -eq 'Committed' -and $statusA -eq 'Succeeded' -and $completed -eq 'Completed'),
    "unload $($a.Id) Committed / Succeeded / Completed",
    "unload $($unload.DemandId) $($unload.Status) / $statusA / $completed")
if ($completed -ne 'Completed') {
    Add-L2RealNotReached $assertions @('L2-RSC-04') '旅程没有收尾，充电分配接手的前提不成立'
    return
}

# --- 4. 进充电分配 ---------------------------------------------------------------------------------------------------

$cycle = Wait-L2RealOrLast -Description 'the charging cycle was allocated and its order confirmed' -Journal $journal `
    -Criterion 'charge-order-confirmed' -TimeoutSeconds 120 -Probe { Get-Cycle } `
    -Until { param($v) $null -ne $v -and [string]$v.WireState -eq 'EN_ROUTE' -and [string]$v.UpperId -ne '' -and [string]$v.OrderConfirmedAt -ne '' }
$enRoute = $null -ne $cycle -and [string]$cycle.WireState -eq 'EN_ROUTE' -and [int]$cycle.StationId -eq $charger -and
    [string]$cycle.UpperId -ne '' -and [string]$cycle.OrderConfirmedAt -ne ''
$claim = Get-Claim
$charging = '(not reached)'
$screenCharging = $null
if ($enRoute) {
    $null = $riot.Command('Put', "orders/$($cycle.UpperId)", @{ orderState = 3; executeVehicleKey = $vehicleKey })
    $null = $riot.Command('Put', "orders/$($cycle.UpperId)", @{ orderState = 5 })
    $charging = Wait-L2RealOrLast -Description 'the cycle is CHARGING at the charger' -Journal $journal -Criterion 'cycle-charging' `
        -TimeoutSeconds 60 -Probe { [string](Get-Cycle).WireState } -Until { param($v) $v -eq 'CHARGING' }
    $screenCharging = Wait-L2RealOrLast -Description 'the HMI shows CHARGING' -Journal $journal -Criterion 'charging-on-screen' `
        -TimeoutSeconds 30 -Probe { Get-L2LoadingPhaseLine $onboard 'ChargingStatus' } -Until { param($v) $v -eq 'CHARGING' }
}
$assertions.Add(
    'L2-RSC-04', '旅程收尾之后进充电分配：分到 211，周期 EN_ROUTE、充电单被确认、车持有 CHARGING 用途；单跑完周期 CHARGING，屏上 ChargingStatus 读到 CHARGING',
    ($enRoute -and $claim -eq "CHARGING $($cycle.JourneyId)" -and $charging -eq 'CHARGING' -and $screenCharging -eq 'CHARGING'),
    "EN_ROUTE @$charger confirmed / CHARGING <cycle journey> / CHARGING / screen CHARGING",
    "$(if ($null -eq $cycle) { '(no cycle)' } else { "$($cycle.WireState) @$($cycle.StationId) confirmed '$($cycle.OrderConfirmedAt)'" }) / $claim / $charging / screen '$screenCharging'")

$journal.Note('真装置：待送点缺电而车上有货，自动出发先送货，旅程收尾之后进充电分配。')
