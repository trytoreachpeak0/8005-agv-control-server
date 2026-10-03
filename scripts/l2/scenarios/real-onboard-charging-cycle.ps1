#Requires -Version 7

<#
真装置：一台车的自动充电周期（批次9-07，control-server#405；车载端半边 onboard-hmi#220）。

**形状**：车停在关卡 210，电量压到 25（默认测试策略强制充电线 30、完成阈值 80）→ 分配到 211，CHARGER 腿到车载端 → 到桩、充电
（假 RIoT 每秒涨 2%）→ 充满（COMPLETE）→ 发一条需求，车被派走 → 12 号站装货 → 离桩之后 211 释放 → 关卡卸货。

**判据**（真车载端看得见的那一层，读 UIA；服务端的库只作对照）：
- L2-ROC-01：CHARGER 腿到了车载端——一条 CHARGER、ACTIVE 腿的计划与 activePurpose=CHARGING 的业务状态都被真车载端确认。
- L2-ROC-02：到桩充电时界面 ChargingStatus 报 CHARGING；车停在桩上那十秒里车载端每次读都不能提交（不在桩上装货）。
- L2-ROC-03：充满：界面 ChargingStatus 报 COMPLETE，仍不能提交（还停在桩上）；211 仍是这一趟的占用、服务端没有为离桩建单。
- L2-ROC-04：派走之后在取货站能录入（hmi#220 审查给本票的跨票前提：离桩接活的新计划里不留充电腿，否则车到取货站仍被判为充电停靠、
  拒装停住）——被确认的最新计划不含 CHARGER 腿，12 号站装货 Committed。
- L2-ROC-05：离桩之后 211 以 CHARGER_RELEASED_ON_DEPARTURE 释放，界面 ChargingStatus 回 NOT_CHARGING；卸货、需求 Succeeded。

**红证据**：本条是真装置场景，红证据在合成场景 charging-full-cycle（下达即释放）与 L1 上取；本条的读法先在合成装置上离线核过
（工作区 evidence/cs405/rig-prep/）。
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
. (Join-Path $PSScriptRoot 'MultiStopRigCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$onboard = $Context.Onboard
$simulator = $Context.Simulator
$riot = $Context.Riot

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: its criteria read the shipped HMI through UI Automation.'
}

$pickupRiotId = $Context.PickupStationRiotId
$gateRiotId = $Context.GateStationRiotId
$charger = 211
$vehicleKey = $Context.VehicleKey
$all = @('L2-ROC-01', 'L2-ROC-02', 'L2-ROC-03', 'L2-ROC-04', 'L2-ROC-05')

$a = New-L2CargoDemand 'A' 'N1-3' 1 $Context.RunId
$a.Sublot = "L2-ROC-A-$($Context.RunId)"

function Get-ChargingStatus { return Get-L2LoadingPhaseLine $onboard 'ChargingStatus' }

function Get-ChargerHeld {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT State, JourneyId FROM StationExclusivities WHERE MapId = $($Context.MapId) AND StationId = $charger")
    if ($null -eq $row) { return '(none)' }
    return "$($row.State) $($row.JourneyId)"
}

function Get-PlanLegs([object]$Plan) { return , @(@($Plan.Payload.legs) | Sort-Object { [int]$_.sequence }) }
function Test-ChargerPlan([object]$Plan) {
    $legs = Get-PlanLegs $Plan
    return $legs.Count -eq 1 -and [string]$legs[0].stopPurposeCategory -eq 'CHARGER'
}
function Get-Field([object]$State, [string]$Name) {
    $property = $State.Payload.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# --- 1. 低电：CHARGER 腿到车载端 ---------------------------------------------------------------------------------

Initialize-L2CargoRig $Context
Assert-L2RigBaselineSlots $Context 1
$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{ mapId = $Context.MapId; stationId = $charger; chargeIntervalSeconds = 1; chargePercentPerInterval = 2 })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = '充电点1' }) `
    -MapId $Context.MapId -Fleet @($vehicleKey) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 real-onboard-charging-cycle: station $charger"
$journal.Observe('charger-roster', [string]$roster.version, @{ import = $roster })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $gateRiotId; battery = 25
})

$chargeIntent = Wait-L2ConditionOrLast -Description 'the charge order to the charger was confirmed' -Journal $journal `
    -Criterion 'charge-intent' -TimeoutSeconds 120 `
    -Probe {
        Read-L2SingleRow -Connection $connection -Sql (
            "SELECT UpperId, IFNULL(OrderId, '') AS OrderId, Status FROM OrderIntents WHERE Purpose = 'TO_CHARGER'")
    } `
    -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
if ($null -eq $chargeIntent -or [string]$chargeIntent.Status -ne 'CONFIRMED') {
    $journal.Note("No confirmed charge order. Session $(Format-L2RealSession (Get-L2RealSession $connection $Context.AgvId)); charger $(Get-ChargerHeld)")
    Add-L2RealNotReached $assertions $all '电量压到强制充电线以下，却没有一张开往充电桩的单被确认'
    return
}
$chargingJourneyId = [string](Read-L2SingleRow -Required -Connection $connection -Sql 'SELECT JourneyId FROM ChargingCycles').JourneyId

$enRoute = Wait-L2ConditionOrLast -Description 'the onboard acknowledged the CHARGER plan and the CHARGING business state' `
    -Journal $journal -Criterion 'charger-leg-acknowledged' -TimeoutSeconds 60 `
    -Probe {
        $plan = @((Get-L2RealOutbound $connection 'UpcomingStopPlanSnapshot') | Where-Object {
                (Test-ChargerPlan $_) -and [string](Get-PlanLegs $_)[0].state -eq 'ACTIVE' -and $_.Acknowledged }) | Select-Object -First 1
        $state = @((Get-L2RealOutbound $connection 'VehicleBusinessStateSnapshot') | Where-Object {
                (Get-Field $_ 'activePurpose') -eq 'CHARGING' -and $_.Acknowledged }) | Select-Object -First 1
        "$(if ($null -ne $plan) { 'plan acknowledged' } else { 'no plan' }) / $(if ($null -ne $state) { 'CHARGING acknowledged' } else { 'no state' })"
    } `
    -Until { param($v) $v -eq 'plan acknowledged / CHARGING acknowledged' }
$assertions.Add(
    'L2-ROC-01',
    '低电：一条 CHARGER、ACTIVE 腿的计划与 activePurpose=CHARGING 的业务状态都到了真车载端并被确认',
    ($enRoute -eq 'plan acknowledged / CHARGING acknowledged'),
    'plan acknowledged / CHARGING acknowledged',
    $enRoute)

# --- 2. 到桩充电：界面报 CHARGING，不开录入 ------------------------------------------------------------------------

$null = Move-L2RealVehicleTo $Context $chargeIntent $charger 'the charger'
$charging = Wait-L2ConditionOrLast -Description 'the HMI shows CHARGING' -Journal $journal -Criterion 'hmi-charging' `
    -TimeoutSeconds 60 -Probe { Get-ChargingStatus } -Until { param($v) $v -eq 'CHARGING' }
$atCharger = Wait-L2ConditionOrLast -Description 'the HMI opened entry at the charger (it must not)' -Journal $journal `
    -Criterion 'hmi-no-entry-while-charging' -TimeoutSeconds 10 `
    -Probe { [bool]$onboard.CanSubmit() } -Until { param($v) $v }
$assertions.Add(
    'L2-ROC-02',
    '到桩充电：界面 ChargingStatus 报 CHARGING；车停在桩上那十秒里每次读车载端都不能提交',
    ($charging -eq 'CHARGING' -and $atCharger -eq $false),
    'CHARGING / CanSubmit False',
    "$charging / CanSubmit $atCharger")

# --- 3. 充满：界面报 COMPLETE，仍在桩上、仍不能提交 -----------------------------------------------------------------

$complete = Wait-L2ConditionOrLast -Description 'the HMI shows COMPLETE' -Journal $journal -Criterion 'hmi-complete' `
    -TimeoutSeconds 120 -Probe { Get-ChargingStatus } -Until { param($v) $v -eq 'COMPLETE' }
$fullAtCharger = Wait-L2ConditionOrLast -Description 'the full vehicle left the charger, or entry opened (neither may happen)' `
    -Journal $journal -Criterion 'full-and-still-at-charger' -TimeoutSeconds 10 `
    -Probe {
        $orders = @($riot.Snapshot().body.orders | Where-Object { $null -ne $_ }).Count
        "$(Get-ChargerHeld) | $orders orders | CanSubmit $([bool]$onboard.CanSubmit())"
    } `
    -Until { param($v) $v -ne "OCCUPIED $chargingJourneyId | 1 orders | CanSubmit False" }
$assertions.Add(
    'L2-ROC-03',
    '充满：界面 ChargingStatus 报 COMPLETE；之后十秒里 211 仍是这一趟的占用、RIoT 上仍只有那一张充电单（不为离桩建单）、车载端仍不能提交',
    ($complete -eq 'COMPLETE' -and $fullAtCharger -eq "OCCUPIED $chargingJourneyId | 1 orders | CanSubmit False"),
    "COMPLETE / OCCUPIED $chargingJourneyId | 1 orders | CanSubmit False",
    "$complete / $fullAtCharger")

# --- 4. 派走：新计划不含充电腿，取货站能录入 ------------------------------------------------------------------------

Publish-L2CargoDemand $Context $a
$journeyA = Wait-L2Condition -Description 'demand A was accepted and the vehicle set off' -Journal $journal -Criterion 'journey-a' `
    -TimeoutSeconds 120 -Probe { Get-L2CargoJourney $connection $a.Id } `
    -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyAId = [string]$journeyA.JourneyId
$null = Wait-L2ConditionOrLast -Description "the onboard acknowledged journey A's plan" -Journal $journal `
    -Criterion 'plan-a-acknowledged' -TimeoutSeconds 60 `
    -Probe {
        @((Get-L2JourneyWireSnapshots $connection @($a.Id)) | Where-Object {
                $_.Type -eq 'UpcomingStopPlanSnapshot' -and $_.Acknowledged }) | Select-Object -First 1
    } `
    -Until { param($v) $null -ne $v }
$latestPlan = @((Get-L2RealOutbound $connection 'UpcomingStopPlanSnapshot') | Where-Object { $_.Acknowledged }) | Select-Object -Last 1
$latestLegs = if ($null -ne $latestPlan) { Get-PlanLegs $latestPlan } else { @() }
$chargerLegs = @($latestLegs | Where-Object { [string]$_.stopPurposeCategory -eq 'CHARGER' }).Count
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyAId $pickupRiotId
$loadA = Invoke-L2RigLoad $Context $journeyAId $a
$assertions.Add(
    'L2-ROC-04',
    '充满的车被派走：被确认的最新一版计划不含 CHARGER 腿；车到 12 号站，车载端能录入、装货 Committed（充满离桩接走后，在取货站能录入）',
    ($null -ne $latestPlan -and $latestLegs.Count -ge 1 -and $chargerLegs -eq 0 -and [string]$loadA.Status -eq 'Committed'),
    '0 CHARGER legs / load Committed',
    "$chargerLegs CHARGER legs of $($latestLegs.Count) / load $($loadA.Status)")

# --- 5. 离桩释放，界面回 NOT_CHARGING；卸货 ------------------------------------------------------------------------

$released = Wait-L2ConditionOrLast -Description 'the charger was released on departure and the HMI left the cycle' `
    -Journal $journal -Criterion 'charger-released' -TimeoutSeconds 30 `
    -Probe {
        $record = Read-L2SingleRow -Connection $connection -Sql (
            "SELECT IFNULL(ReleaseReason, '') AS Reason FROM StationExclusivityRecords " +
            "WHERE MapId = $($Context.MapId) AND StationId = $charger AND JourneyId = '$chargingJourneyId'")
        "$(Get-ChargerHeld) | $(${record}?.Reason) | $(Get-ChargingStatus)"
    } `
    -Until { param($v) $v -eq '(none) | CHARGER_RELEASED_ON_DEPARTURE | NOT_CHARGING' }
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyAId $gateRiotId
$null = Invoke-L2RigUnloadNext $Context $journeyAId @()
$status = Wait-L2ConditionOrLast -Description 'demand A succeeded' -Journal $journal -Criterion 'demand-a-succeeded' `
    -TimeoutSeconds 120 -Probe { Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($a.Id)'" } `
    -Until { param($v) $v -eq 'Succeeded' }
$assertions.Add(
    'L2-ROC-05',
    '车离开 211 之后：211 以 CHARGER_RELEASED_ON_DEPARTURE 释放，界面 ChargingStatus 回 NOT_CHARGING；卸货之后需求 Succeeded',
    ($released -eq '(none) | CHARGER_RELEASED_ON_DEPARTURE | NOT_CHARGING' -and $status -eq 'Succeeded'),
    '(none) | CHARGER_RELEASED_ON_DEPARTURE | NOT_CHARGING / Succeeded',
    "$released / $status")

$journal.Note("Real onboard: charging journey $chargingJourneyId charged, completed, and journey $journeyAId took the vehicle away.")
