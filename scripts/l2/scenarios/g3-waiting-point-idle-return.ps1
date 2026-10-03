#Requires -Version 7

<#
G3 `FP-IS-12`：空闲返回（批次8-19，control-server#390）。协议向量 `CV-WAITING-POINT-IDLE-RETURN`。

两端的半边：服务端 control-server#390（承诺物化、执行、到点收敛、离点释放），车载端 onboard-hmi#217（到站那一格把等待点当
非业务停靠显示，AutomationId `IdleReturnStatus`，ItemStatus 为 `EN_ROUTE_TO_WAITING_POINT`／`AT_WAITING_POINT`）。合成对端上
同一条链路是 `waiting-point-exclusive-reserve-occupy-release`（只看服务端的库）；这里换成真车载端 WPF 与真模拟器，守的是
hmi#217 与本票之间的两条跨票契约：

1. 释放 `IDLE_RETURN` 时服务端显式发一张 `activePurpose` 不再是 `IDLE_RETURN` 的车辆业务状态（G3-12-03）。
2. 计划里等待点腿的状态跟事实走：还停在点上时这条腿是 `ARRIVED`，不标 `COMPLETED`、不删（G3-12-03，持续断言）；
   车被派走之后，下一趟的计划整体替换它，不留 `ARRIVED` 的等待点腿（G3-12-04）。
3. 两条落到操作员看得见的那一格上：空闲返回释放后接新旅程，到取货站录入照常可用（G3-12-05）。

**形状**：一辆车，车先停在关卡 210、没有需求 → 空闲返回开往等待点 214（setup 放在节点 6）→ 到点收敛 → 需求甲把车派走 →
12 号站装甲（录入可用）→ 214 凭离点证据释放 → 关卡卸甲，旅程 Completed。

**为什么先空闲返回、后发需求**：编排器进场景前一刻激活充电策略，那是空闲返回判定的最后一道前提，所以空停在关卡上的车一上来
就承诺空闲返回。合成场景 `waiting-point-exclusive-reserve-occupy-release` 第一次跑时先发了需求，空闲返回在需求被受理之前就
承诺了，场景等搬运、超时。先发需求的写法判的是一场竞速，这里不写。

判据：
- G3-12-01（服务端，向量顺序）：空闲返回是一趟没有需求的旅程，开往 214 的单段移动；途中的计划（一条 `WAITING_POINT`、`ACTIVE`
  的腿）先于 `activePurpose = IDLE_RETURN` 的业务状态进发件箱，两张都被真车载端确认。
- G3-12-02（车载端）：确认之后界面到站那一格报 `EN_ROUTE_TO_WAITING_POINT`。
- G3-12-03（两端，契约 1、2）：到点收敛（214 转占用、用途释放）；收尾那张计划的等待点腿是 `ARRIVED`，收尾那张业务状态的
  `activePurpose` 不是 `IDLE_RETURN`，两张都被确认；界面报 `AT_WAITING_POINT`，并在其后十秒里一直是（不退回、不清空）。
- G3-12-04（两端，契约 2）：甲派走之后，被确认的最新一版计划不含等待点腿；界面到站那一格不再报空闲返回的任何值。
- G3-12-05（两端，契约 3）：车到 12 号站，车载端能录入、甲装货提交；214 的独占以 `DEPARTED_STATION` 释放。
- G3-12-06（终态）：甲装一次、卸一次、Succeeded，旅程 Completed。
- G3-12-07（车载端 `NEVER_LOAD_AT_WAITING_POINT`）：车停在等待点上那十秒，车载端每次读都不能提交，服务端也没建装卸操作。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2MultiStopJourney.psm1') -Force
. (Join-Path $PSScriptRoot 'MultiStopRigCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$onboard = $Context.Onboard
$simulator = $Context.Simulator

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: its onboard assertions read the shipped HMI through UI Automation.'
}

$pickupRiotId = $Context.PickupStationRiotId
$gateRiotId = $Context.GateStationRiotId
$waitingPoint = 214
$enRoute = 'EN_ROUTE_TO_WAITING_POINT'
$atPoint = 'AT_WAITING_POINT'

$a = New-L2CargoDemand 'A' 'N1-3' 1 $Context.RunId
$a.Sublot = "G3-12-A-$($Context.RunId)"

# The station cell's raw status; '' when the cell shows neither an idle return nor a clearance (the HMI binds an empty string).
function Get-IdleReturnStatus {
    return Get-L2LoadingPhaseLine $onboard 'IdleReturnStatus'
}

function Get-IdleReturn {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, Stage, BlockReasonCode, PickupUpperId, DemandId FROM JourneyRuntimes " +
        "WHERE JourneyId LIKE 'idle-return:%' ORDER BY CreatedAt DESC")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Held([int]$Station) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT VehicleKey, JourneyId, State, StationKind FROM StationExclusivities " +
        "WHERE MapId = $($Context.MapId) AND StationId = $Station")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Held-Text([object]$Held) {
    if ($null -eq $Held) { return '(none)' }
    return "$($Held.State) $($Held.StationKind) $($Held.JourneyId)"
}

# A plan's legs by sequence, and whether it is an idle-return plan (every leg a WAITING_POINT leg).
function Get-PlanLegs([object]$Plan) { return , @(@($Plan.Payload.legs) | Sort-Object { [int]$_.sequence }) }
function Test-WaitingPointPlan([object]$Plan) {
    $legs = Get-PlanLegs $Plan
    return $legs.Count -eq 1 -and [string]$legs[0].stopPurposeCategory -eq 'WAITING_POINT'
}
function Format-Plan([object]$Plan) {
    if ($null -eq $Plan) { return '(none)' }
    $legs = ((Get-PlanLegs $Plan) | ForEach-Object { "$($_.sequence):$($_.stopPurposeCategory)@$($_.stationId):$($_.state)" }) -join ','
    return "plan r$($Plan.Payload.planRevision) [$legs] ack=$($Plan.Acknowledged)"
}

# activePurpose of a business state; $null when the payload carries none (the field is optional).
function Get-Purpose([object]$State) {
    $property = $State.Payload.PSObject.Properties['activePurpose']
    if ($null -eq $property) { return $null }
    return $property.Value
}
function Format-State([object]$State) {
    if ($null -eq $State) { return '(none)' }
    $purpose = Get-Purpose $State
    return "business r$($State.Payload.vehicleBusinessStateRevision) purpose=$(if ($null -eq $purpose) { 'null' } else { $purpose }) ack=$($State.Acknowledged)"
}

# --- 1. 车停在关卡上、没有需求 ---------------------------------------------------------------------------------

Initialize-L2CargoRig $Context
Assert-L2RigBaselineSlots $Context 1

# --- 2. 空闲返回：承诺、物化、过安全门、建单；途中计划先于业务状态，两张都被确认 ---------------------------------------------------

$idleIntent = Wait-L2ConditionOrLast -Description 'the idle return order to the waiting point was confirmed' -Journal $journal `
    -Criterion 'idle-return-intent' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql (
            "SELECT UpperId, OrderId, Status, DemandId FROM OrderIntents WHERE Purpose = 'TO_WAITING_POINT'")
        if ($rows.Count -eq 0) { $null } else { $rows[0] }
    } `
    -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
if ($null -eq $idleIntent -or [string]$idleIntent.Status -ne 'CONFIRMED') {
    $idle = Get-IdleReturn
    $journal.Note("No confirmed idle-return order. Idle return: $(if ($idle) { "$($idle.JourneyId) $($idle.Stage) $($idle.BlockReasonCode)" } else { '(none)' }); session $(Format-L2RealSession (Get-L2RealSession $connection $Context.AgvId))")
    Add-L2RealNotReached $assertions @('G3-12-01', 'G3-12-02', 'G3-12-03', 'G3-12-04', 'G3-12-05', 'G3-12-06', 'G3-12-07') `
        '车空闲、没有需求，却没有一张开往等待点的单被确认（没有承诺、没物化，或出发前安全门没过）'
    return
}
$idle = Get-IdleReturn
$idleJourneyId = [string]$idle.JourneyId

$enRouteSnapshots = Wait-L2ConditionOrLast -Description 'the onboard acknowledged the en-route plan and the IDLE_RETURN business state' `
    -Journal $journal -Criterion 'en-route-snapshots-acknowledged' -TimeoutSeconds 60 `
    -Probe {
        $plan = @((Get-L2RealOutbound $connection 'UpcomingStopPlanSnapshot') | Where-Object {
                (Test-WaitingPointPlan $_) -and [string](Get-PlanLegs $_)[0].state -eq 'ACTIVE' }) | Select-Object -First 1
        $state = @((Get-L2RealOutbound $connection 'VehicleBusinessStateSnapshot') | Where-Object {
                (Get-Purpose $_) -eq 'IDLE_RETURN' }) | Select-Object -First 1
        [pscustomobject]@{ Plan = $plan; State = $state }
    } `
    -Until { param($v) $null -ne $v.Plan -and $null -ne $v.State -and $v.Plan.Acknowledged -and $v.State.Acknowledged }
$enRoutePlan = $enRouteSnapshots.Plan
$enRouteState = $enRouteSnapshots.State
$reserved = Get-Held $waitingPoint
$assertions.Add(
    'G3-12-01',
    '车空闲、没有需求：空闲返回是一趟没有需求的旅程，开往 214 的单段移动已确认、214 是它的在途预占；途中计划（一条 WAITING_POINT、ACTIVE 的腿）先于 activePurpose=IDLE_RETURN 的业务状态进发件箱，两张都被真车载端确认（CV-WAITING-POINT-IDLE-RETURN 的消息顺序）',
    ([string]::IsNullOrEmpty([string]$idle.DemandId) -and [string]::IsNullOrEmpty([string]$idleIntent.DemandId) -and
        [string]$idleIntent.UpperId -eq [string]$idle.PickupUpperId -and
        $null -ne $reserved -and [string]$reserved.State -eq 'RESERVED' -and [string]$reserved.JourneyId -eq $idleJourneyId -and
        $null -ne $enRoutePlan -and $null -ne $enRouteState -and $enRoutePlan.Acknowledged -and $enRouteState.Acknowledged -and
        $enRoutePlan.At -lt $enRouteState.At),
    "无需求 / 214 RESERVED $idleJourneyId / 计划先于业务状态、都已确认",
    "demand '$($idle.DemandId)'/'$($idleIntent.DemandId)' / $(Held-Text $reserved) / $(Format-Plan $enRoutePlan) @ $(${enRoutePlan}?.At) / $(Format-State $enRouteState) @ $(${enRouteState}?.At)")

$shownEnRoute = Wait-L2ConditionOrLast -Description 'the HMI shows the vehicle en route to the waiting point' -Journal $journal `
    -Criterion 'hmi-en-route' -TimeoutSeconds 30 -Probe { Get-IdleReturnStatus } -Until { param($v) $v -eq $enRoute }
$assertions.Add(
    'G3-12-02',
    '车载端确认了途中两张之后，到站那一格报 EN_ROUTE_TO_WAITING_POINT（TREAT_WAITING_POINT_AS_NON_BUSINESS_STOP）',
    ($shownEnRoute -eq $enRoute),
    $enRoute,
    "'$shownEnRoute'")

# --- 3. 到点收敛：收尾那张计划仍是 ARRIVED 的等待点腿、业务状态撤下 IDLE_RETURN；界面在点待命并一直是 -------------------------------

$null = Move-L2RealVehicleTo $Context $idleIntent $waitingPoint 'waiting point'
$closed = Wait-L2ConditionOrLast -Description 'the idle return converged at the waiting point' -Journal $journal `
    -Criterion 'idle-return-converged' -TimeoutSeconds 60 -Probe { Get-IdleReturn } `
    -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'Completed' }
$closure = Wait-L2ConditionOrLast -Description 'the onboard acknowledged the closing plan and business state' -Journal $journal `
    -Criterion 'closure-snapshots-acknowledged' -TimeoutSeconds 60 `
    -Probe {
        $plan = @((Get-L2RealOutbound $connection 'UpcomingStopPlanSnapshot') | Where-Object { $_.At -gt $enRoutePlan.At }) |
            Select-Object -Last 1
        $state = @((Get-L2RealOutbound $connection 'VehicleBusinessStateSnapshot') | Where-Object { $_.At -gt $enRouteState.At }) |
            Select-Object -Last 1
        [pscustomobject]@{ Plan = $plan; State = $state }
    } `
    -Until { param($v) $null -ne $v.Plan -and $null -ne $v.State -and $v.Plan.Acknowledged -and $v.State.Acknowledged }
$shownAtPoint = Wait-L2ConditionOrLast -Description 'the HMI shows the vehicle at the waiting point' -Journal $journal `
    -Criterion 'hmi-at-point' -TimeoutSeconds 30 -Probe { Get-IdleReturnStatus } -Until { param($v) $v -eq $atPoint }
# Held over time, not read once: a cell that falls back to "journey not synchronised" a few seconds later is the defect P4 names,
# and an entry that opens a few seconds later is a load at the waiting point. Both halves read together, every poll.
$heldReading = Wait-L2ConditionOrLast -Description 'the HMI stopped showing the vehicle at the waiting point, or opened entry (it must not)' `
    -Journal $journal -Criterion 'hmi-still-at-point' -TimeoutSeconds 10 `
    -Probe { [pscustomobject]@{ Status = Get-IdleReturnStatus; CanSubmit = [bool]$onboard.CanSubmit() } } `
    -Until { param($v) $v.Status -ne $atPoint -or $v.CanSubmit }
$heldAtPoint = $heldReading.Status
# Invoke-L2Query returns its rows whole (return , $rows): assign, do not wrap. Wrapped, an empty result is one element that is an
# empty array, and reading OperationType off it throws -- the first G3 run of this scenario (cs390-journey-fa4a5ce3) stopped here.
$operationsAtPoint = Invoke-L2Query -Connection $connection -Sql (
    'SELECT SlotOperationAttemptId, OperationType FROM StationOperations')
$assertions.Add(
    'G3-12-07',
    '车停在等待点上：车载端不开放录入（十秒里每次读都不能提交），服务端也没有建任何装卸操作——此刻一条需求都还没有（NEVER_LOAD_AT_WAITING_POINT）',
    ($shownAtPoint -eq $atPoint -and -not $heldReading.CanSubmit -and $operationsAtPoint.Count -eq 0),
    "界面 $atPoint、不能提交 / 0 笔操作",
    "界面 '$($heldReading.Status)'、CanSubmit=$($heldReading.CanSubmit) / 操作 $(($operationsAtPoint | ForEach-Object { "$($_.OperationType):$($_.SlotOperationAttemptId)" }) -join ',')")
$occupied = Get-Held $waitingPoint
$claimRelease = Invoke-L2Query -Connection $connection -Sql (
    "SELECT ReleaseReason FROM VehiclePurposeClaimRecords WHERE JourneyId = '$idleJourneyId'")
$closingPlan = $closure.Plan
$closingState = $closure.State
$closingLegs = if ($null -ne $closingPlan) { Get-PlanLegs $closingPlan } else { @() }
$assertions.Add(
    'G3-12-03',
    '到点收敛：214 转为这一趟的在点占用、IDLE_RETURN 用途释放；收尾那张计划仍是那一条等待点腿、状态 ARRIVED（不标 COMPLETED、不删），收尾那张业务状态的 activePurpose 不是 IDLE_RETURN，两张都被确认；界面报 AT_WAITING_POINT，并在其后十秒里一直是',
    ($null -ne $closed -and [string]$closed.Stage -eq 'Completed' -and [string]::IsNullOrEmpty([string]$closed.BlockReasonCode) -and
        $null -ne $occupied -and [string]$occupied.State -eq 'OCCUPIED' -and [string]$occupied.JourneyId -eq $idleJourneyId -and
        $claimRelease.Count -eq 1 -and [string]$claimRelease[0].ReleaseReason -eq 'IDLE_RETURN_CONVERGED_AT_WAITING_POINT' -and
        $null -ne $closingPlan -and $closingLegs.Count -eq 1 -and [string]$closingLegs[0].stopPurposeCategory -eq 'WAITING_POINT' -and
        [string]$closingLegs[0].state -eq 'ARRIVED' -and $closingPlan.Acknowledged -and
        $null -ne $closingState -and (Get-Purpose $closingState) -ne 'IDLE_RETURN' -and $closingState.Acknowledged -and
        $shownAtPoint -eq $atPoint -and $heldAtPoint -eq $atPoint),
    "Completed / 214 OCCUPIED / 用途 IDLE_RETURN_CONVERGED_AT_WAITING_POINT / 计划 [1:WAITING_POINT@…:ARRIVED] 已确认 / 业务状态非 IDLE_RETURN 已确认 / 界面 $atPoint 十秒不变",
    "$(${closed}?.Stage) $(${closed}?.BlockReasonCode) / $(Held-Text $occupied) / 用途 $(($claimRelease | ForEach-Object { $_.ReleaseReason }) -join ',') / $(Format-Plan $closingPlan) / $(Format-State $closingState) / 界面 '$shownAtPoint' → '$heldAtPoint'")

# --- 4. 甲把车派走：新计划整体替换，不留等待点腿；界面不再报空闲返回 ---------------------------------------------------------------

Publish-L2CargoDemand $Context $a
$journeyA = Wait-L2Condition -Description 'demand A was accepted and the vehicle set off' -Journal $journal -Criterion 'journey-a' `
    -TimeoutSeconds 120 -Probe { Get-L2CargoJourney $connection $a.Id } `
    -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyAId = [string]$journeyA.JourneyId
$planA = Wait-L2ConditionOrLast -Description "the onboard acknowledged journey A's plan" -Journal $journal `
    -Criterion 'plan-a-acknowledged' -TimeoutSeconds 60 `
    -Probe {
        @((Get-L2JourneyWireSnapshots $connection @($a.Id)) | Where-Object {
                $_.Type -eq 'UpcomingStopPlanSnapshot' -and $_.Acknowledged }) | Select-Object -First 1
    } `
    -Until { param($v) $null -ne $v }
$latestPlan = @((Get-L2RealOutbound $connection 'UpcomingStopPlanSnapshot') | Where-Object { $_.Acknowledged }) | Select-Object -Last 1
$shownAfterDispatch = Wait-L2ConditionOrLast -Description 'the HMI stopped showing an idle return' -Journal $journal `
    -Criterion 'hmi-no-idle-return' -TimeoutSeconds 30 -Probe { Get-IdleReturnStatus } `
    -Until { param($v) $v -notin @($enRoute, $atPoint) }
$latestLegs = if ($null -ne $latestPlan) { Get-PlanLegs $latestPlan } else { @() }
$assertions.Add(
    'G3-12-04',
    '甲把车派走：被确认的最新一版计划是甲那一趟的，不含等待点腿（不留 ARRIVED 的等待点腿、不排在业务腿前面）；界面到站那一格不再报 EN_ROUTE_TO_WAITING_POINT／AT_WAITING_POINT',
    ($null -ne $planA -and $null -ne $latestPlan -and $latestLegs.Count -ge 1 -and
        @($latestLegs | Where-Object { [string]$_.stopPurposeCategory -eq 'WAITING_POINT' }).Count -eq 0 -and
        @($latestLegs | Where-Object { [string]$_.demandId -eq $a.Id }).Count -ge 1 -and
        $shownAfterDispatch -notin @($enRoute, $atPoint)),
    '最新已确认计划属于甲、无等待点腿 / 界面不报空闲返回',
    "$(Format-Plan $latestPlan) / 界面 '$shownAfterDispatch'")

# --- 5. 12 号站录入照常可用，甲装货提交；214 凭离点证据释放 -----------------------------------------------------------------------

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyAId $pickupRiotId
$loadA = Invoke-L2RigLoad $Context $journeyAId $a
$released = Wait-L2ConditionOrLast -Description 'the waiting point was released on departure evidence' -Journal $journal `
    -Criterion 'waiting-point-released' -TimeoutSeconds 30 -Probe { Get-Held $waitingPoint } -Until { param($v) $null -eq $v }
$record = Invoke-L2Query -Connection $connection -Sql (
    "SELECT ReleaseReason FROM StationExclusivityRecords WHERE MapId = $($Context.MapId) AND StationId = $waitingPoint " +
    "AND JourneyId = '$idleJourneyId'")
$assertions.Add(
    'G3-12-05',
    '空闲返回释放后接新旅程：车到 12 号站，车载端录入可用、甲的装货提交；214 的独占以 DEPARTED_STATION 释放',
    ([string]$loadA.Status -eq 'Committed' -and $null -eq $released -and $record.Count -eq 1 -and
        [string]$record[0].ReleaseReason -eq 'DEPARTED_STATION'),
    'load A Committed / 214 released DEPARTED_STATION',
    "load A $($loadA.Status) / held $(Held-Text $released) / record $(($record | ForEach-Object { $_.ReleaseReason }) -join ',')")

# --- 6. 终态 -------------------------------------------------------------------------------------------------------

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyAId $gateRiotId
$unloadA = Invoke-L2RigUnloadNext $Context $journeyAId @()
$stageA = Wait-L2ConditionOrLast -Description 'journey A completed' -Journal $journal -Criterion 'journey-a-completed' `
    -TimeoutSeconds 120 -Probe { Get-L2JourneyStage $connection $journeyAId } -Until { param($v) $v -eq 'Completed' }
$ops = Invoke-L2Query -Connection $connection -Sql (
    "SELECT OperationType, Status FROM StationOperations WHERE DemandId = '$($a.Id)'")
$status = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($a.Id)'"
$loads = @($ops | Where-Object { [string]$_.OperationType -eq 'Load' -and [string]$_.Status -eq 'Committed' }).Count
$unloads = @($ops | Where-Object { [string]$_.OperationType -eq 'Unload' -and [string]$_.Status -eq 'Committed' }).Count
$perDemand = "A:$status/ops $($ops.Count)/load $loads/unload $unloads"
$expected = 'A:Succeeded/ops 2/load 1/unload 1'
$slotReading = "$($loadA.OpenedSlot)=$(Get-L2RealSlotReading $simulator $loadA.OpenedSlot)"
$assertions.Add(
    'G3-12-06',
    '甲一次装、一次卸、都 Committed，需求 Succeeded，旅程 Completed；装过的仓最后关门、空、锁上、开锁输出复位',
    ($stageA -eq 'Completed' -and $perDemand -eq $expected -and $slotReading -match '=CLOSED/EMPTY/1/0$'),
    "Completed / $expected / 仓 CLOSED/EMPTY/1/0",
    "$stageA / $perDemand / $slotReading")

$journal.Note("FP-IS-12: idle return $idleJourneyId went to $waitingPoint, converged, and the vehicle was taken away by journey $journeyAId (unload $($unloadA.AttemptId)).")
