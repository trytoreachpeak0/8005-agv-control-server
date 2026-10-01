#Requires -Version 7

<#
G3 `FP-IS-13`：自动充电周期（批次9-07，control-server#405）。协议向量 `CV-AUTOMATIC-CHARGING-CYCLE`。

两端的半边：服务端 control-server#404（分配、预占、去桩）与 #405（到桩、充电、充满放用途、离桩释放），车载端 onboard-hmi#220（充电停靠是
非业务停靠：显示充电状态、不开录入；AutomationId `ChargingStatus` 的 ItemStatus 是 chargingCycleState 原值）。合成对端上同一条链路是
`charging-full-cycle`（只看服务端的库）；这里换成真车载端 WPF 与真模拟器。

**形状**：一辆车停在关卡 210，电量压到 25（默认测试策略强制充电线 30、完成阈值 80）→ 分配到 211、建单 → 到桩、充电（假 RIoT 每秒涨 1%）
→ 充电中发一条需求甲，它一直不派 → 充满（COMPLETE）→ 甲派给这辆车 → 12 号站装甲（录入可用）→ 211 凭离桩三项确认释放 → 关卡卸甲。

判据（向量的 productAssertions 与 orderedExpectedMessages 各有一条对应，用自己的断言名——疑点 29）：
- G3-13-01（服务端，`orderedExpectedMessages`）：充电旅程是一趟没有需求的旅程；途中的计划（一条 `CHARGER`、`ACTIVE` 的腿）先于
  `activePurpose=CHARGING` 的业务状态进发件箱，两张都被真车载端确认。
- G3-13-02（服务端，`CLAIM_VEHICLE_FOR_CHARGING_PURPOSE`）：从分配到充满，这辆车一直只持有那一份 `CHARGING` 用途，211 从预占到占用一直是这一趟的。
- G3-13-03（车载端）：到桩充电时界面 `ChargingStatus` 报 `CHARGING`；车停在桩上那十秒里车载端每次读都不能提交、服务端没有建装卸操作。
- G3-13-04（服务端，`NEVER_DISPATCH_DURING_CHARGING`）：充电中、低于完成阈值时，甲十秒里一直没派给它（没有旅程、没有它的单）。
- G3-13-05（两端）：充满：收尾计划仍是那一条 `CHARGER` 腿、`ARRIVED`，收尾业务状态 `chargingCycleState=COMPLETE`、`activePurpose` 为空，两张都被确认；
  界面报 `COMPLETE`；211 仍是这一趟的占用。
- G3-13-06（两端，hmi#220 跨票契约）：甲派给这辆车，被确认的最新一版计划属于甲、不含 `CHARGER` 腿；车到 12 号站录入可用、甲装货提交；
  211 以 `CHARGER_RELEASED_ON_DEPARTURE` 释放、周期以 `CHARGING_DEPARTED` 收尾。
- G3-13-07（终态，`forbiddenSideEffects`）：甲装一次、卸一次、Succeeded，旅程 Completed；RIoT 上恰好一张充电单（`duplicate-riot-order`）。
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
    throw 'This scenario needs the real onboard rig: its onboard assertions read the shipped HMI through UI Automation.'
}

$pickupRiotId = $Context.PickupStationRiotId
$gateRiotId = $Context.GateStationRiotId
$charger = 211
$chargerName = '充电点1'
$vehicleKey = $Context.VehicleKey
$all = @('G3-13-01', 'G3-13-02', 'G3-13-03', 'G3-13-04', 'G3-13-05', 'G3-13-06', 'G3-13-07')

$a = New-L2CargoDemand 'A' 'N1-3' 1 $Context.RunId
$a.Sublot = "G3-13-A-$($Context.RunId)"

function Get-ChargingStatus { return Get-L2LoadingPhaseLine $onboard 'ChargingStatus' }

function Get-Cycle {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, WireState, Phase, IFNULL(UpperId, '') AS UpperId, IFNULL(EndReason, '') AS EndReason FROM ChargingCycles")
}

function Get-ChargerHeld {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT State, JourneyId FROM StationExclusivities WHERE MapId = $($Context.MapId) AND StationId = $charger")
    if ($null -eq $row) { return '(none)' }
    return "$($row.State) $($row.JourneyId)"
}

function Get-Claim {
    $row = Read-L2SingleRow -Connection $connection -Sql "SELECT Purpose, JourneyId FROM VehiclePurposeClaims WHERE VehicleKey = '$vehicleKey'"
    if ($null -eq $row) { return '(none)' }
    return "$($row.Purpose) $($row.JourneyId)"
}

function Get-PlanLegs([object]$Plan) { return , @(@($Plan.Payload.legs) | Sort-Object { [int]$_.sequence }) }
function Test-ChargerPlan([object]$Plan) {
    $legs = Get-PlanLegs $Plan
    return $legs.Count -eq 1 -and [string]$legs[0].stopPurposeCategory -eq 'CHARGER'
}
function Format-Plan([object]$Plan) {
    if ($null -eq $Plan) { return '(none)' }
    $legs = ((Get-PlanLegs $Plan) | ForEach-Object { "$($_.sequence):$($_.stopPurposeCategory)@$($_.stationId):$($_.state)" }) -join ','
    return "plan r$($Plan.Payload.planRevision) [$legs] ack=$($Plan.Acknowledged)"
}
function Get-Field([object]$State, [string]$Name) {
    $property = $State.Payload.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}
function Format-State([object]$State) {
    if ($null -eq $State) { return '(none)' }
    $purpose = Get-Field $State 'activePurpose'
    return "business r$($State.Payload.vehicleBusinessStateRevision) purpose=$(if ($null -eq $purpose) { 'null' } else { $purpose }) " +
        "cycle=$(Get-Field $State 'chargingCycleState') ack=$($State.Acknowledged)"
}

# --- 1. 车停在关卡上；桩登记到假 RIoT 与名册；电量压到强制充电线以下 --------------------------------------------------

Initialize-L2CargoRig $Context
Assert-L2RigBaselineSlots $Context 1
$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{ mapId = $Context.MapId; stationId = $charger; chargeIntervalSeconds = 1; chargePercentPerInterval = 1 })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet @($vehicleKey) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "G3 g3-automatic-charging-cycle: station $charger"
$journal.Observe('charger-roster', [string]$roster.version, @{ import = $roster })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $gateRiotId; battery = 25
})

# --- 2. 分配、建单：途中计划先于业务状态，两张都被确认 ---------------------------------------------------------------

$chargeIntent = Wait-L2ConditionOrLast -Description 'the charge order to the charger was confirmed' -Journal $journal `
    -Criterion 'charge-intent' -TimeoutSeconds 120 `
    -Probe {
        Read-L2SingleRow -Connection $connection -Sql (
            "SELECT UpperId, IFNULL(OrderId, '') AS OrderId, Status, IFNULL(DemandId, '') AS DemandId FROM OrderIntents " +
            "WHERE Purpose = 'TO_CHARGER'")
    } `
    -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
if ($null -eq $chargeIntent -or [string]$chargeIntent.Status -ne 'CONFIRMED') {
    $journal.Note("No confirmed charge order. Session $(Format-L2RealSession (Get-L2RealSession $connection $Context.AgvId)); claim $(Get-Claim); charger $(Get-ChargerHeld)")
    Add-L2RealNotReached $assertions $all '电量压到强制充电线以下，却没有一张开往充电桩的单被确认（没有分配、或出发前安全门没过）'
    return
}
$cycle = Get-Cycle
$chargingJourneyId = [string]$cycle.JourneyId
$claimAtAllocation = Get-Claim

$enRouteSnapshots = Wait-L2ConditionOrLast -Description 'the onboard acknowledged the en-route charger plan and the CHARGING business state' `
    -Journal $journal -Criterion 'en-route-snapshots-acknowledged' -TimeoutSeconds 60 `
    -Probe {
        $plan = @((Get-L2RealOutbound $connection 'UpcomingStopPlanSnapshot') | Where-Object {
                (Test-ChargerPlan $_) -and [string](Get-PlanLegs $_)[0].state -eq 'ACTIVE' }) | Select-Object -First 1
        $state = @((Get-L2RealOutbound $connection 'VehicleBusinessStateSnapshot') | Where-Object {
                (Get-Field $_ 'activePurpose') -eq 'CHARGING' -and (Get-Field $_ 'chargingCycleState') -eq 'EN_ROUTE' }) | Select-Object -First 1
        [pscustomobject]@{ Plan = $plan; State = $state }
    } `
    -Until { param($v) $null -ne $v.Plan -and $null -ne $v.State -and $v.Plan.Acknowledged -and $v.State.Acknowledged }
$journey = Read-L2SingleRow -Required -Connection $connection -Sql (
    "SELECT IFNULL(DemandId, '') AS DemandId FROM JourneyRuntimes WHERE JourneyId = '$chargingJourneyId'")
$assertions.Add(
    'G3-13-01',
    '低电：充电旅程是一趟没有需求的旅程，开往 211 的充电单已确认；途中计划（一条 CHARGER、ACTIVE 的腿）先于 activePurpose=CHARGING、EN_ROUTE 的业务状态进发件箱，两张都被真车载端确认（CV-AUTOMATIC-CHARGING-CYCLE 的消息顺序）',
    ([string]$journey.DemandId -eq '' -and [string]$chargeIntent.DemandId -eq '' -and
        $null -ne $enRouteSnapshots.Plan -and $null -ne $enRouteSnapshots.State -and
        $enRouteSnapshots.Plan.Acknowledged -and $enRouteSnapshots.State.Acknowledged -and
        $enRouteSnapshots.Plan.At -lt $enRouteSnapshots.State.At),
    '无需求 / 计划先于业务状态、都已确认',
    "demand '$($journey.DemandId)'/'$($chargeIntent.DemandId)' / $(Format-Plan $enRouteSnapshots.Plan) @ $(${enRouteSnapshots}?.Plan?.At) / $(Format-State $enRouteSnapshots.State) @ $(${enRouteSnapshots}?.State?.At)")

# --- 3. 到桩、充电：界面报 CHARGING，不开录入 ----------------------------------------------------------------------

$null = Move-L2RealVehicleTo $Context $chargeIntent $charger 'the charger'
$charging = Wait-L2ConditionOrLast -Description 'the cycle is CHARGING and the HMI shows it' -Journal $journal `
    -Criterion 'charging-shown' -TimeoutSeconds 60 `
    -Probe { "$([string](Get-Cycle).WireState) | $(Get-ChargerHeld) | $(Get-ChargingStatus)" } `
    -Until { param($v) $v -eq "CHARGING | OCCUPIED $chargingJourneyId | CHARGING" }
# Held over time, not read once: an entry that opens a few seconds later is a load at the charger.
$heldReading = Wait-L2ConditionOrLast -Description 'the HMI opened entry at the charger (it must not)' `
    -Journal $journal -Criterion 'hmi-no-entry-at-charger' -TimeoutSeconds 10 `
    -Probe { [pscustomobject]@{ Status = Get-ChargingStatus; CanSubmit = [bool]$onboard.CanSubmit() } } `
    -Until { param($v) $v.CanSubmit }
# Invoke-L2Query returns its rows whole: assign, do not wrap.
$operationsAtCharger = Invoke-L2Query -Connection $connection -Sql 'SELECT SlotOperationAttemptId, OperationType FROM StationOperations'
$assertions.Add(
    'G3-13-03',
    '到桩充电：211 转为这一趟的占用，周期 CHARGING，界面 ChargingStatus 报 CHARGING；车停在桩上那十秒里车载端每次读都不能提交，服务端也没有建任何装卸操作',
    ($charging -eq "CHARGING | OCCUPIED $chargingJourneyId | CHARGING" -and -not $heldReading.CanSubmit -and
        $operationsAtCharger.Count -eq 0),
    "CHARGING | OCCUPIED $chargingJourneyId | CHARGING / 不能提交 / 0 笔操作",
    "$charging / CanSubmit=$($heldReading.CanSubmit) / 操作 $(($operationsAtCharger | ForEach-Object { "$($_.OperationType):$($_.SlotOperationAttemptId)" }) -join ',')")

# --- 4. 充电中、低于完成阈值：甲不派 ------------------------------------------------------------------------------

Publish-L2CargoDemand $Context $a
$notDispatched = Wait-L2ConditionOrLast -Description 'demand A went to the charging vehicle (it must not)' -Journal $journal `
    -Criterion 'no-dispatch-while-charging' -TimeoutSeconds 10 `
    -Probe {
        $journeys = [int](Invoke-L2Query -Connection $connection -Sql (
                "SELECT COUNT(*) AS N FROM JourneyDemands WHERE DemandId = '$($a.Id)'"))[0].N
        "$journeys journeys | $([string](Get-Cycle).WireState) | $(Get-Claim)"
    } `
    -Until { param($v) -not $v.StartsWith('0 journeys | CHARGING') }
$assertions.Add(
    'G3-13-04',
    '充电中、低于完成阈值：甲发布之后十秒里一直没派给这辆车，周期一直是 CHARGING，用途一直是那一份 CHARGING（NEVER_DISPATCH_DURING_CHARGING）',
    ($notDispatched -eq "0 journeys | CHARGING | CHARGING $chargingJourneyId"),
    "0 journeys | CHARGING | CHARGING $chargingJourneyId",
    $notDispatched)

# --- 5. 充满：收尾计划留 ARRIVED 的充电腿，业务状态 COMPLETE、用途为空；211 仍占用 ----------------------------------------

$claimUntilComplete = Wait-L2ConditionOrLast -Description 'the cycle completed' -Journal $journal -Criterion 'cycle-complete' `
    -TimeoutSeconds 120 -Probe { "$([string](Get-Cycle).WireState) | $(Get-Claim)" } `
    -Until { param($v) $v.StartsWith('COMPLETE') -or ($v -ne "CHARGING | CHARGING $chargingJourneyId" -and $v -ne "EN_ROUTE | CHARGING $chargingJourneyId") }
$assertions.Add(
    'G3-13-02',
    '从分配到充满，这辆车一直只持有那一份 CHARGING 用途（分配时、充电中都是同一趟），充满时放开（CLAIM_VEHICLE_FOR_CHARGING_PURPOSE）',
    ($claimAtAllocation -eq "CHARGING $chargingJourneyId" -and $claimUntilComplete -eq 'COMPLETE | (none)'),
    "CHARGING $chargingJourneyId → COMPLETE | (none)",
    "$claimAtAllocation → $claimUntilComplete")

$closure = Wait-L2ConditionOrLast -Description 'the onboard acknowledged the COMPLETE plan and business state' -Journal $journal `
    -Criterion 'complete-snapshots-acknowledged' -TimeoutSeconds 60 `
    -Probe {
        $plan = @((Get-L2RealOutbound $connection 'UpcomingStopPlanSnapshot') | Where-Object { Test-ChargerPlan $_ }) | Select-Object -Last 1
        $state = @((Get-L2RealOutbound $connection 'VehicleBusinessStateSnapshot') | Where-Object {
                (Get-Field $_ 'chargingCycleState') -eq 'COMPLETE' }) | Select-Object -Last 1
        [pscustomobject]@{ Plan = $plan; State = $state }
    } `
    -Until { param($v) $null -ne $v.Plan -and $null -ne $v.State -and $v.Plan.Acknowledged -and $v.State.Acknowledged }
$shownComplete = Wait-L2ConditionOrLast -Description 'the HMI shows COMPLETE' -Journal $journal -Criterion 'hmi-complete' `
    -TimeoutSeconds 30 -Probe { Get-ChargingStatus } -Until { param($v) $v -eq 'COMPLETE' }
$closingLegs = if ($null -ne $closure.Plan) { Get-PlanLegs $closure.Plan } else { @() }
$heldAtComplete = Get-ChargerHeld
$assertions.Add(
    'G3-13-05',
    '充满：收尾计划仍是那一条 CHARGER 腿、ARRIVED，收尾业务状态 chargingCycleState=COMPLETE、activePurpose 为空，两张都被确认；界面 ChargingStatus 报 COMPLETE；211 仍是这一趟的占用',
    ($closingLegs.Count -eq 1 -and [string]$closingLegs[0].stopPurposeCategory -eq 'CHARGER' -and [string]$closingLegs[0].state -eq 'ARRIVED' -and
        $closure.Plan.Acknowledged -and $null -ne $closure.State -and $null -eq (Get-Field $closure.State 'activePurpose') -and
        $closure.State.Acknowledged -and $shownComplete -eq 'COMPLETE' -and $heldAtComplete -eq "OCCUPIED $chargingJourneyId"),
    "计划 [1:CHARGER@…:ARRIVED] 已确认 / 业务状态 COMPLETE、用途 null 已确认 / 界面 COMPLETE / OCCUPIED $chargingJourneyId",
    "$(Format-Plan $closure.Plan) / $(Format-State $closure.State) / 界面 '$shownComplete' / $heldAtComplete")

# --- 6. 甲派给这辆车：新计划不含充电腿；12 号站录入可用；211 凭离桩证据释放 -----------------------------------------------

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
$heldAtDispatch = Get-ChargerHeld

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyAId $pickupRiotId
$loadA = Invoke-L2RigLoad $Context $journeyAId $a
$released = Wait-L2ConditionOrLast -Description 'the charger was released on departure' -Journal $journal `
    -Criterion 'charger-released' -TimeoutSeconds 30 `
    -Probe {
        $record = Read-L2SingleRow -Connection $connection -Sql (
            "SELECT IFNULL(ReleaseReason, '') AS Reason FROM StationExclusivityRecords " +
            "WHERE MapId = $($Context.MapId) AND StationId = $charger AND JourneyId = '$chargingJourneyId'")
        $cycleNow = Get-Cycle
        "$(Get-ChargerHeld) | $(${record}?.Reason) | $(${cycleNow}?.Phase) $(${cycleNow}?.EndReason)"
    } `
    -Until { param($v) $v.StartsWith('(none)') }
$assertions.Add(
    'G3-13-06',
    '甲派给充满的这辆车：下达那一刻 211 仍是充电那一趟的占用；被确认的最新一版计划属于甲、不含 CHARGER 腿；车到 12 号站车载端录入可用、甲装货提交；211 以 CHARGER_RELEASED_ON_DEPARTURE 释放、周期以 CHARGING_DEPARTED 收尾',
    ($heldAtDispatch -eq "OCCUPIED $chargingJourneyId" -and $latestLegs.Count -ge 1 -and
        @($latestLegs | Where-Object { [string]$_.stopPurposeCategory -eq 'CHARGER' }).Count -eq 0 -and
        @($latestLegs | Where-Object { [string]$_.demandId -eq $a.Id }).Count -ge 1 -and
        [string]$loadA.Status -eq 'Committed' -and $released -eq '(none) | CHARGER_RELEASED_ON_DEPARTURE | ENDED CHARGING_DEPARTED'),
    "OCCUPIED $chargingJourneyId / 最新已确认计划属于甲、无充电腿 / load A Committed / (none) | CHARGER_RELEASED_ON_DEPARTURE | ENDED CHARGING_DEPARTED",
    "$heldAtDispatch / $(Format-Plan $latestPlan) / load A $($loadA.Status) / $released")

# --- 7. 终态 -------------------------------------------------------------------------------------------------------

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyAId $gateRiotId
$unloadA = Invoke-L2RigUnloadNext $Context $journeyAId @()
$stageA = Wait-L2ConditionOrLast -Description 'journey A completed' -Journal $journal -Criterion 'journey-a-completed' `
    -TimeoutSeconds 120 -Probe { Get-L2JourneyStage $connection $journeyAId } -Until { param($v) $v -eq 'Completed' }
$ops = Invoke-L2Query -Connection $connection -Sql "SELECT OperationType, Status FROM StationOperations WHERE DemandId = '$($a.Id)'"
$status = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($a.Id)'"
$loads = @($ops | Where-Object { [string]$_.OperationType -eq 'Load' -and [string]$_.Status -eq 'Committed' }).Count
$unloads = @($ops | Where-Object { [string]$_.OperationType -eq 'Unload' -and [string]$_.Status -eq 'Committed' }).Count
$chargeOrders = @($riot.Snapshot().body.orders | Where-Object { $null -ne $_ -and [string]$_.upperId -like 'W2G-CHARGE-*' }).Count
$perDemand = "A:$status/ops $($ops.Count)/load $loads/unload $unloads/charge orders $chargeOrders"
$expected = 'A:Succeeded/ops 2/load 1/unload 1/charge orders 1'
$slotReading = "$($loadA.OpenedSlot)=$(Get-L2RealSlotReading $simulator $loadA.OpenedSlot)"
$assertions.Add(
    'G3-13-07',
    '甲一次装、一次卸、都 Committed，需求 Succeeded，旅程 Completed；RIoT 上恰好一张充电单（没有重复的单）；装过的仓最后关门、空、锁上、开锁输出复位',
    ($stageA -eq 'Completed' -and $perDemand -eq $expected -and $slotReading -match '=CLOSED/EMPTY/1/0$'),
    "Completed / $expected / 仓 CLOSED/EMPTY/1/0",
    "$stageA / $perDemand / $slotReading")

$journal.Note("FP-IS-13: charging journey $chargingJourneyId charged at $charger, completed, and the vehicle was taken away by journey $journeyAId (unload $($unloadA.AttemptId)).")
