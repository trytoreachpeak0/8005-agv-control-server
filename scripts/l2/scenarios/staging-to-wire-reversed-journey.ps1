#Requires -Version 7

<#
STAGING_TO_WIRE 反向旅程（control-server#163，规格 21.2 第 2 条、向量 CV-REVERSED-DIRECTION-JOURNEY）。

放一条 STAGING_TO_WIRE 需求（AREA N1-3，机台站 12），断言：
  - 方向由任务类型规则决定：第一腿 TO_PICKUP 到派工待送站 305，第二腿 TO_DROPOFF 到 AREA 机台站 12，
    计划快照里的腿序、站点与 publicStationFunction（为空）照此；两条移动订单的目标站照此；
  - 清单快照 stopRole：派工待送站是 PICKUP，机台站是 DROPOFF；
  - 站点任务类型准入冻结在卸货那次操作上（AdmissionDecisionSnapshots 按 SlotOperationAttemptId 查），装货那次没有；
  - 旅程走完。
然后同一轮运行里放一条 WIRE_TO_GATE 需求，照常走完：机台站取货、关卡卸货，准入仍冻结在装货那次操作上。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection

$stagingStationRiotId = 305
$stagingStationName = '派工待送取货'
$machineStationRiotId = 12
$machineStationName = 'N1-3_N1-7'

function Publish-Demand([string]$Suffix, [string]$WorkType) {
    $guid = [guid]::NewGuid()
    $sublot = "L2-S2W-$Suffix-$($Context.RunId)"
    $journal.Note("Publishing $WorkType demand $($guid.ToString('N')) (sublot $sublot, area N1-3).")
    $null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot      = $sublot
        area        = 'N1-3'
        # One machine for both demands: an AREA naming two EQPs in the catalog is AREA_EQP_NOT_UNIQUE, across task
        # types since control-server#160, and the first demand stays listed after its journey completes.
        eqp         = 'EQP-L2-S2W-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
        workType    = $WorkType
    })
    return $guid.ToString('D')
}

function Get-Runtime([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$DemandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$DemandId, [string]$Purpose) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT UpperId, OrderId, Status, DestinationStationId, TargetStationId FROM OrderIntents " +
        "WHERE DemandId = '$DemandId' AND Purpose = '$Purpose'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Payloads([string]$MessageType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = '$MessageType' ORDER BY CreatedAt, MessageId"
    # Unrolled, not `return ,`: callers pipe this, and a wrapped array reaches Where-Object as one object whose
    # stationId and items are every snapshot's at once (the first run of this scenario failed on exactly that).
    return @($rows | ForEach-Object { ([string]$_.PayloadJson | ConvertFrom-Json -Depth 20).payload })
}

function Get-Admissions([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT a.SlotOperationAttemptId, a.StationId, a.TaskType, a.Allowed, s.OperationType " +
        "FROM AdmissionDecisionSnapshots a JOIN StationOperations s ON s.SlotOperationAttemptId = a.SlotOperationAttemptId " +
        "WHERE s.DemandId = '$DemandId'")
    return , @($rows)
}

# 车从当前位置开到 $StationRiotId 并停稳，照 normal-load 的写法。
function Move-Vehicle([object]$Intent, [int]$StationRiotId) {
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $Intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $StationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 5 })
}

# 一条需求从受理走到 Completed：先到 $FirstStationRiotId，再到 $SecondStationRiotId。
function Complete-Journey([string]$DemandId, [int]$FirstStationRiotId, [int]$SecondStationRiotId, [string]$Label) {
    $pickupIntent = Wait-L2Condition -Description "the $Label TO_PICKUP intent was confirmed" `
        -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
        -Probe { Get-Intent $DemandId 'TO_PICKUP' } -Until { param($v) [string]$v.Status -eq 'CONFIRMED' }
    $journal.Note("$Label vehicle drives to station $FirstStationRiotId and comes to rest.")
    Move-Vehicle $pickupIntent $FirstStationRiotId
    $null = Wait-L2Condition -Description "the $Label load committed and the journey reached its second leg" `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { [string](Get-Runtime $DemandId).Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
    $secondIntent = Wait-L2Condition -Description "the $Label second-leg intent was confirmed" `
        -Journal $journal -Criterion 'to-gate-intent' -TimeoutSeconds 60 `
        -Probe { Get-Intent $DemandId 'TO_GATE' } -Until { param($v) [string]$v.Status -eq 'CONFIRMED' }
    $journal.Note("$Label vehicle drives to station $SecondStationRiotId and comes to rest.")
    Move-Vehicle $secondIntent $SecondStationRiotId
    return Wait-L2Condition -Description "the $Label journey completed" `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { [string](Get-Runtime $DemandId).Stage } -Until { param($v) $v -eq 'Completed' }
}

# --- 1. STAGING_TO_WIRE：受理后计划与订单都按规则方向排 -------------------------------------------------

$reverseDemand = Publish-Demand 'REVERSE' 'STAGING_TO_WIRE'
$null = Wait-L2Condition -Description 'the STAGING_TO_WIRE demand was accepted and dispatched' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { $r = Get-Runtime $reverseDemand; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$runtime = Get-Runtime $reverseDemand
$assertions.Add(
    'L2-S2W-01', '反向旅程的取货站是派工待送站，卸货站是需求 AREA 的机台站',
    ([int]$runtime.PickupStationRiotId -eq $stagingStationRiotId -and [int]$runtime.GateStationRiotId -eq $machineStationRiotId),
    "pickup $stagingStationRiotId, drop-off $machineStationRiotId",
    "pickup $($runtime.PickupStationRiotId), drop-off $($runtime.GateStationRiotId)")

$firstPlan = Wait-L2Condition -Description 'the dispatch plan snapshot was sent' `
    -Journal $journal -Criterion 'plan-snapshot' -TimeoutSeconds 60 `
    -Probe { @(Get-Payloads 'UpcomingStopPlanSnapshot' | Where-Object { @($_.legs | Where-Object { [string]$_.demandId -eq $reverseDemand }).Count -gt 0 }) | Select-Object -First 1 } `
    -Until { param($v) $null -ne $v }
$legs = @($firstPlan.legs | Sort-Object { [int]$_.sequence })
$observedLegs = ($legs | ForEach-Object { "$($_.sequence):$($_.legType)@$($_.stationId)" }) -join ' '
$assertions.Add(
    'L2-S2W-02', '计划快照先 TO_PICKUP 到派工待送站、后 TO_DROPOFF 到机台站，publicStationFunction 为空',
    ($observedLegs -ceq "1:TO_PICKUP@$stagingStationName 2:TO_DROPOFF@$machineStationName" -and
        @($legs | Where-Object { $null -ne $_.publicStationFunction }).Count -eq 0),
    "1:TO_PICKUP@$stagingStationName 2:TO_DROPOFF@$machineStationName, no publicStationFunction",
    "$observedLegs; publicStationFunction: $((@($legs | ForEach-Object { if ($null -eq $_.publicStationFunction) { 'null' } else { $_.publicStationFunction } })) -join ',')")

# --- 2. 走完两段 -----------------------------------------------------------------------------------------

$stage = Complete-Journey $reverseDemand $stagingStationRiotId $machineStationRiotId 'STAGING_TO_WIRE'
$assertions.Add('L2-S2W-03', 'STAGING_TO_WIRE 旅程先装后卸走到 Completed', ($stage -eq 'Completed'), 'Completed', $stage)

$intents = @('TO_PICKUP', 'TO_GATE' | ForEach-Object { [int](Get-Intent $reverseDemand $_).DestinationStationId })
$assertions.Add(
    'L2-S2W-04', '两条移动订单的目标站依次是派工待送站与机台站',
    (($intents -join ',') -eq "$stagingStationRiotId,$machineStationRiotId"),
    "$stagingStationRiotId,$machineStationRiotId", ($intents -join ','))

$worklists = @(Get-Payloads 'CurrentStopWorklistSnapshot' | Where-Object {
        @($_.items | Where-Object { [string]$_.demandId -eq $reverseDemand }).Count -gt 0 })
$roles = @($worklists | ForEach-Object {
        $item = @($_.items | Where-Object { [string]$_.demandId -eq $reverseDemand })[0]
        "$($_.stationId):$($item.stopRole)" } | Select-Object -Unique)
$assertions.Add(
    'L2-S2W-05', '清单快照：派工待送站 stopRole 为 PICKUP，机台站为 DROPOFF',
    (($roles -join ' ') -ceq "${stagingStationName}:PICKUP ${machineStationName}:DROPOFF"),
    "${stagingStationName}:PICKUP ${machineStationName}:DROPOFF", ($roles -join ' '))

$admissions = Get-Admissions $reverseDemand
$observedAdmissions = ($admissions | ForEach-Object { "$($_.OperationType):$($_.StationId)/$($_.TaskType)/$($_.Allowed)" }) -join ' '
$reverseRuntime = Get-Runtime $reverseDemand
$assertions.Add(
    'L2-S2W-06', '准入决策只冻结在卸货那次操作上，站点是机台站、任务类型 STAGING_TO_WIRE',
    ($admissions.Count -eq 1 -and
        [string]$admissions[0].SlotOperationAttemptId -eq [string]$reverseRuntime.UnloadSlotOperationAttemptId -and
        [string]$admissions[0].StationId -ceq $machineStationName -and
        [string]$admissions[0].TaskType -ceq 'STAGING_TO_WIRE'),
    "one decision on UnloadSlotOperationAttemptId: $machineStationName/STAGING_TO_WIRE",
    "$($admissions.Count) decision(s): $observedAdmissions")

# --- 3. 同一轮运行里 WIRE_TO_GATE 照常 -------------------------------------------------------------------

$forwardDemand = Publish-Demand 'FORWARD' 'WIRE_TO_GATE'
$null = Wait-L2Condition -Description 'the WIRE_TO_GATE demand was accepted and dispatched' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { $r = Get-Runtime $forwardDemand; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$forwardRuntime = Get-Runtime $forwardDemand
$stage = Complete-Journey $forwardDemand $machineStationRiotId $Context.GateStationRiotId 'WIRE_TO_GATE'
$forwardAdmissions = Get-Admissions $forwardDemand
$assertions.Add(
    'L2-S2W-07', 'WIRE_TO_GATE 照常：机台站取货、关卡卸货、走完，准入冻结在装货那次操作上',
    ([int]$forwardRuntime.PickupStationRiotId -eq $machineStationRiotId -and
        [int]$forwardRuntime.GateStationRiotId -eq $Context.GateStationRiotId -and $stage -eq 'Completed' -and
        $forwardAdmissions.Count -eq 1 -and
        [string]$forwardAdmissions[0].SlotOperationAttemptId -eq [string]$forwardRuntime.LoadSlotOperationAttemptId -and
        [string]$forwardAdmissions[0].TaskType -ceq 'WIRE_TO_GATE'),
    "pickup $machineStationRiotId, drop-off $($Context.GateStationRiotId), Completed, one decision on the load",
    "pickup $($forwardRuntime.PickupStationRiotId), drop-off $($forwardRuntime.GateStationRiotId), $stage, " +
        "$($forwardAdmissions.Count) decision(s): $(($forwardAdmissions | ForEach-Object { "$($_.OperationType):$($_.StationId)/$($_.TaskType)" }) -join ' ')")

$journal.Note('Scenario finished.')
