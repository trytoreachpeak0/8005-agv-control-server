#Requires -Version 7

<#
货不在原仓、重建停住之后，人经 control-server#299 的入口把这趟转进车载端异常处置会话，维护人员在车上交接（control-server#345，
交接衔接 a/b/c）。真车载端 WPF + 真 slots-simulator。

修之前这一格走不通，L1 在修复之前的 fp/v2-impl 上实读过（StoppedRebuildExitTests.ACargoNotInPlaceIsHandedToTheExceptionSessionAndEndsThere
的前半段）：车载端要开的会话被服务端以 RECOVERY_DEMAND_NOT_BLOCKED 拒（停住的旅程在到站阶段，不是 Blocked），会话就绪是 READY，
而车载端只在 RECOVERY_REQUIRED 时出「故障交接」入口。本场景把这条链在真车载端上走一遍：

  1. 装货提交、建出 TO_GATE 单；车停在取货站上，TO_GATE 单被报 FAILED，服务端记故障、绑定车上的货；车静止在已知站点，不急停。
  2. 故障期间仓里的货没了：装货那一仓的光幕被固定成「没挡住」（模拟器不许门关着取货，光幕就是车载端读货物的那一路 DI）。
  3. 人工清除故障：车上有货，处置 REBUILD_SCHEDULED，等车拿出清除之后的货物快照。
  4. 车载端的快照显示目标仓空：重建停住，旅程码 OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE，不建新单。
  5. 人工重建在这里不许（409，只能交接）；人工转交接被受理（HandoffPrepared），旅程转 Blocked、会话转 CARGO_HANDOFF_REQUIRED；
     同一请求再来一次 AlreadyDone。
  6. 车载端给出「故障交接」入口，维护人员按下并确认，线上走完 CV-FAULT-CARGO-HANDOFF。
  7. 交接收敛：需求 Cancelled，旅程以 TERMINATED_BY_FAULT_CARGO_HANDOFF 收尾，故障货物绑定以 HANDED_OFF_IN_EXCEPTION_SESSION 了结，
     停住的重建记录 ENDED，全程只有一张 TO_GATE 单，会话回到 Ready。
  8. 同一台车接得了下一单。

**读取函数的写法。**`Invoke-L2Query` 与 `Get-G3Inbound`／`Get-G3Outbound` 都以 `return , @(...)` 返回：一律先赋值再用，
不写 `@(f)`，也不写 `@(f) | ...`；要走管道时写 `(f) | ...`。夹具里一条需求装一仓，所以这里没有「至少两行」可凑的表；
按条件筛的地方都在判据里把筛出的条数一并断言（筛成空会红，而不是在空集上恒真）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'G3RecoveryCommon.ps1')
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$onboard = $Context.Onboard
$simulator = $Context.Simulator
$connection = $Context.Connection
$agvId = [string]$Context.AgvId
$recoveryUri = "http://127.0.0.1:$($Context.HealthPort)/api/safety/v1/vehicle-fault-recoveries"

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: the handoff entry is the onboard HMI.'
}
if (-not $Context.FaultRecoveryCredential) {
    throw 'The setup file must turn VehicleFaultRecovery on; without it this scenario proves nothing.'
}

$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')
$sublot = "L2-RH-$($Context.RunId)"
$ids = @('L2-RH-01', 'L2-RH-02', 'L2-RH-03', 'L2-RH-04', 'L2-RH-05', 'L2-RH-06', 'L2-RH-07', 'L2-RH-08', 'L2-RH-09')

function Get-Journey {
    # 先赋值再用（见文件头）。
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT JourneyId, Stage, BlockReasonCode, GateUpperId FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Bindings {
    # 这条需求的故障货物绑定，全部行（活的与了结的）。先赋值再用。
    return Invoke-L2Query -Connection $connection `
        -Sql "SELECT CargoBindingId, ReleasedAt, ReleasedReason FROM FaultedVehicleCargo WHERE DemandId = '$demandId'"
}

function Get-Rebuild([string]$endedUpperId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT State, StoppedReason, OperatorId FROM OwnOrderRebuilds WHERE EndedUpperId = '$endedUpperId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Invoke-Exit([string]$action, [string]$label) {
    $body = @{
        agvId         = $agvId
        operatorId    = 'L2-OPERATOR-345'
        action        = $action
        faultRemedied = $true
        note          = "L2 $($Context.RunId)"
    }
    $response = Invoke-WebRequest -Method Post -Uri $recoveryUri -SkipHttpErrorCheck -TimeoutSec 60 `
        -Headers @{ Authorization = "Bearer $($Context.FaultRecoveryCredential)" } `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json)
    # A refusal is application/problem+json, which Invoke-WebRequest does not treat as text: Content arrives as bytes.
    $text = if ($response.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($response.Content)
    } else {
        [string]$response.Content
    }
    $journal.Note("Fault recovery $action ($label) -> $([int]$response.StatusCode): $text")
    return [pscustomobject]@{
        Status = [int]$response.StatusCode
        Body   = if ($text) { $text | ConvertFrom-Json } else { $null }
    }
}

function Get-Field([object]$response, [string]$name) {
    # A refusal's problem body has no `outcome`; under StrictMode reading it throws instead of failing the criterion.
    if ($null -eq $response.Body -or -not ($response.Body.PSObject.Properties.Name -contains $name)) { return '' }
    return [string]$response.Body.$name
}

function Get-Reasons([object]$response) {
    # `return , @(...)` so that one reason stays a one-element array.
    if ($null -eq $response.Body -or -not ($response.Body.PSObject.Properties.Name -contains 'reasons')) { return , @() }
    return , @($response.Body.reasons | ForEach-Object { [string]$_ })
}

function Get-TriggerCount {
    $invocations = @($riot.Snapshot().body.commandInvocations)
    return @($invocations | Where-Object { [string]$_.commandType -eq 'triggerEmergency' }).Count
}

# 与 real-onboard-normal-load 的 Invoke-SlotOperation 同一个道理：等车载端自己报 WAITING_OPERATOR 再放货，
# 不在看到门开的那一刻动手（锁反馈要稳定 300 ms 才算数）。
function Invoke-Load {
    $attemptId = Wait-L2Condition -Description 'the server issued the load command' `
        -Journal $journal -Criterion 'load-attempt' -TimeoutSeconds 180 `
        -Probe { Get-G3Scalar $connection "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'" } `
        -Until { param($v) $v }
    $waiting = Wait-L2Condition -Description 'the onboard is waiting for the operator' `
        -Journal $journal -Criterion 'load-waiting-operator' -TimeoutSeconds 120 `
        -Probe { @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })[0] } `
        -Until { param($v) $null -ne $v }
    if (@($waiting.Active).Count -ne 1) {
        throw "This scenario drives one slot per load; the command targets $(@($waiting.Active).Count) ($(@($waiting.Active) -join ', '))."
    }
    $slot = [int]$waiting.Active[0]
    $journal.Note("Onboard is waiting on slot $slot; the operator puts the basket in and closes the door.")
    $null = $simulator.Command('Put', "slots/$slot/cargo", @{ state = 'OCCUPIED' })
    $null = $simulator.Command('Post', "slots/$slot/close-door", @{})
    $null = Wait-L2Condition -Description "slot $slot reads closed, occupied, locked and reset" `
        -Journal $journal -Criterion 'load-slot-physical' -TimeoutSeconds 60 `
        -Probe { Get-G3SlotState $simulator $slot } -Until { param($v) $v -eq 'CLOSED/OCCUPIED/1/0' }
    return [pscustomobject]@{ AttemptId = $attemptId; Slot = $slot }
}

# --- 1. 受理、到取货站、UIA 录入、装货提交，建出 TO_GATE 单 ----------------------------------------------------

$journal.Note("Publishing demand $($demandGuid.ToString('N')) (sublot $sublot).")
$null = $Context.MesIngest.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = $sublot; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})
$pickup = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 120 `
    -Probe { Get-Intent 'TO_PICKUP' } -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }

$journal.Note('Vehicle drives to the pickup station and comes to rest there.')
$null = $riot.Command('Put', "orders/$($pickup.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = $pickup.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.PickupStationRiotId; processingOrder = $false; clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($pickup.UpperId)", @{ orderState = 5 })

$null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
$onboard.SetSublot($sublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()

$load = Invoke-Load
$gate = Wait-L2Condition -Description 'the load committed and the TO_GATE intent was confirmed' `
    -Journal $journal -Criterion 'to-gate-intent' -TimeoutSeconds 180 `
    -Probe { Get-Intent 'TO_GATE' } -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }

# --- 2. TO_GATE 单被报 FAILED；车静止在取货站上 ---------------------------------------------------------------

# 车留在取货站上、MT_FINISHED、速度 0：位置已知、没在动，服务端不急停（VehicleFaultCoordinator.RequiresEscalation）。
# 急停锁住的车清除不了故障，那一格是 vehicle-fault-operator-clearance 的事，不在这里。
$journal.Note("RIoT reports $($gate.UpperId) FAILED while the vehicle stands at the pickup station.")
$null = $riot.Command('Put', "orders/$($gate.UpperId)", @{ orderState = 4; executeVehicleKey = $Context.VehicleKey })
$faulted = Wait-L2ConditionOrLast -Description 'the server recorded the fault and bound the cargo on board' `
    -Journal $journal -Criterion 'fault-recorded' -TimeoutSeconds 90 `
    -Probe {
        $journey = Get-Journey
        $bindings = Get-Bindings
        $live = @($bindings | Where-Object { -not (Test-G3Present $_.ReleasedAt) })
        [pscustomobject]@{
            Code  = if ($journey) { [string]$journey.BlockReasonCode } else { '' }
            Level = [string](Get-G3Scalar $connection "SELECT Level AS Value FROM VehicleFaultStates WHERE AgvId = '$agvId'")
            Live  = $live.Count
        }
    } `
    -Until { param($v) $v.Code -eq 'VEHICLE_ORDER_FAILED' -and $v.Level -notin @('', 'None') -and $v.Live -eq 1 }
$triggersAtFault = Get-TriggerCount
$assertions.Add(
    'L2-RH-01',
    'TO_GATE 单 FAILED：旅程码 VEHICLE_ORDER_FAILED，故障已记，车上的货有一条活着的故障货物绑定；车静止在已知站点，没有急停',
    ($faulted.Code -eq 'VEHICLE_ORDER_FAILED' -and $faulted.Level -notin @('', 'None') -and $faulted.Live -eq 1 -and $triggersAtFault -eq 0),
    'VEHICLE_ORDER_FAILED / 故障已记 / 活绑定 1 / 急停 0',
    "$($faulted.Code) / 故障 $($faulted.Level) / 活绑定 $($faulted.Live) / 急停 $triggersAtFault")
if ($faulted.Code -ne 'VEHICLE_ORDER_FAILED' -or $triggersAtFault -ne 0) {
    Add-G3NotReached $assertions ($ids | Select-Object -Skip 1) '故障没有按预期记下，或车被急停锁住（清除入口会拒）'
    return
}

# --- 3. 故障期间货没了：装货那一仓的光幕固定成「没挡住」 --------------------------------------------------------

# 模拟器不许门关着取货（DOOR_NOT_OPEN），而车载端读货物用的就是光幕那一路 DI。读出现在（有货）的原始值，把它固定成反面。
$slotBefore = @($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $load.Slot })
if ($slotBefore.Count -ne 1) { throw "The simulator snapshot has $($slotBefore.Count) rows for slot $($load.Slot)." }
$emptyRaw = 1 - [int]$slotBefore[0].lightCurtainRaw
$journal.Note("While the vehicle is faulted the basket goes missing: slot $($load.Slot) light curtain forced to $emptyRaw (unobstructed).")
$null = $simulator.Command('Put', "slots/$($load.Slot)/light-curtain-override", @{ mode = "FIXED_$emptyRaw" })
$null = Wait-L2Condition -Description "slot $($load.Slot) light curtain reads $emptyRaw" `
    -Journal $journal -Criterion 'light-curtain-empty' -TimeoutSeconds 30 `
    -Probe { [int]@($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $load.Slot })[0].lightCurtainRaw } `
    -Until { param($v) $v -eq $emptyRaw }

# --- 4. 人工清除故障：车上有货，等清除之后的快照 ------------------------------------------------------------------

$cleared = Invoke-Exit 'CLEAR_FAULT' 'clear'
$afterClear = Get-Journey
$assertions.Add(
    'L2-RH-02',
    '人工清除故障：200 Cleared / REBUILD_SCHEDULED，旅程码 VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD（车上有货，重建要等清除之后的货物快照）',
    ($cleared.Status -eq 200 -and (Get-Field $cleared 'outcome') -eq 'Cleared' -and (Get-Field $cleared 'disposition') -eq 'REBUILD_SCHEDULED' -and
        $null -ne $afterClear -and [string]$afterClear.BlockReasonCode -in @('VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD', 'OWN_ORDER_REBUILD_WAITING_CARGO_EVIDENCE', 'OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE')),
    '200 Cleared REBUILD_SCHEDULED / VEHICLE_FAULT_CLEARED_CARGO_ON_BOARD（或已往后走）',
    "$($cleared.Status) $(Get-Field $cleared 'outcome') $(Get-Field $cleared 'disposition') / $(if ($afterClear) { [string]$afterClear.BlockReasonCode } else { '(无旅程)' })")

# --- 5. 车载端的快照显示目标仓空：重建停住，不建新单 ----------------------------------------------------------------

$stopped = Wait-L2ConditionOrLast -Description 'the rebuild stopped on a snapshot that shows the slot empty' `
    -Journal $journal -Criterion 'cargo-not-in-place' -TimeoutSeconds 120 `
    -Probe {
        $journey = Get-Journey
        $record = Get-Rebuild ([string]$gate.UpperId)
        [pscustomobject]@{
            Stage  = if ($journey) { [string]$journey.Stage } else { '' }
            Code   = if ($journey) { [string]$journey.BlockReasonCode } else { '' }
            State  = if ($record) { [string]$record.State } else { '' }
            Reason = if ($record) { [string]$record.StoppedReason } else { '' }
            Gates  = Get-G3Count $connection "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'"
        }
    } `
    -Until { param($v) $v.Code -eq 'OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE' -and $v.State -eq 'STOPPED' }
$assertions.Add(
    'L2-RH-03',
    '清除之后车载端的快照显示目标仓空：重建停住（STOPPED / CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS），旅程码 OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE、仍在去关卡的阶段；只有原来那一张 TO_GATE 单',
    ($stopped.Code -eq 'OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE' -and $stopped.State -eq 'STOPPED' -and
        $stopped.Reason -eq 'CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS' -and $stopped.Stage -eq 'AwaitingGateArrival' -and $stopped.Gates -eq 1),
    'AwaitingGateArrival / OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE / STOPPED CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS / TO_GATE 1',
    "$($stopped.Stage) / $($stopped.Code) / $($stopped.State) $($stopped.Reason) / TO_GATE $($stopped.Gates)")
if ($stopped.Code -ne 'OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE') {
    Add-G3NotReached $assertions ($ids | Select-Object -Skip 3) '重建没有因货不在原仓停住'
    return
}

# --- 6. 人工重建不许；转交接被受理；重复一次 AlreadyDone ------------------------------------------------------------

$rebuildRefused = Invoke-Exit 'REBUILD_STOPPED_ORDER' 'rebuild refused'
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal
$afterRefusal = Get-Journey
$assertions.Add(
    'L2-RH-04',
    '货不在原仓时人工重建不许：409 OWN_ORDER_REBUILD_EXIT_CARGO_NOT_IN_PLACE（REQ-0238 只许交接），旅程码与单数不变',
    ($rebuildRefused.Status -eq 409 -and (Get-Reasons $rebuildRefused) -contains 'OWN_ORDER_REBUILD_EXIT_CARGO_NOT_IN_PLACE' -and
        $null -ne $afterRefusal -and [string]$afterRefusal.BlockReasonCode -eq 'OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE' -and
        (Get-G3Count $connection "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'") -eq 1),
    '409 OWN_ORDER_REBUILD_EXIT_CARGO_NOT_IN_PLACE / OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE / TO_GATE 1',
    "$($rebuildRefused.Status) $((Get-Reasons $rebuildRefused) -join ',') / $(if ($afterRefusal) { [string]$afterRefusal.BlockReasonCode } else { '(无旅程)' }) / TO_GATE $(Get-G3Count $connection "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'")")

$prepared = Invoke-Exit 'PREPARE_CARGO_HANDOFF' 'handoff'
$handoffState = Wait-L2ConditionOrLast -Description 'the journey is held for the handoff and the session asks for it' `
    -Journal $journal -Criterion 'handoff-prepared' -TimeoutSeconds 60 `
    -Probe {
        $journey = Get-Journey
        $record = Get-Rebuild ([string]$gate.UpperId)
        $session = Get-G3Session $connection
        [pscustomobject]@{
            Journey   = if ($journey) { "$($journey.Stage)/$($journey.BlockReasonCode)" } else { '' }
            Record    = if ($record) { [string]$record.State } else { '' }
            Readiness = if ($session) { "$($session.Readiness)/$($session.ReasonCode)" } else { '' }
        }
    } `
    -Until { param($v) $v.Journey -eq 'Blocked/OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF' -and $v.Readiness -eq 'RecoveryRequired/CARGO_HANDOFF_REQUIRED' }
$assertions.Add(
    'L2-RH-05',
    '人工转交接被受理（200 HandoffPrepared / AWAITING_CARGO_HANDOFF）：旅程转 Blocked、码 OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF，重建记录 AWAITING_CARGO_HANDOFF；向车要快照之后会话转 RecoveryRequired / CARGO_HANDOFF_REQUIRED',
    ($prepared.Status -eq 200 -and (Get-Field $prepared 'outcome') -eq 'HandoffPrepared' -and (Get-Field $prepared 'disposition') -eq 'AWAITING_CARGO_HANDOFF' -and
        $handoffState.Journey -eq 'Blocked/OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF' -and $handoffState.Record -eq 'AWAITING_CARGO_HANDOFF' -and
        $handoffState.Readiness -eq 'RecoveryRequired/CARGO_HANDOFF_REQUIRED'),
    '200 HandoffPrepared AWAITING_CARGO_HANDOFF / Blocked/OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF / AWAITING_CARGO_HANDOFF / RecoveryRequired/CARGO_HANDOFF_REQUIRED',
    "$($prepared.Status) $(Get-Field $prepared 'outcome') $(Get-Field $prepared 'disposition') / $($handoffState.Journey) / $($handoffState.Record) / $($handoffState.Readiness)")

$preparedAgain = Invoke-Exit 'PREPARE_CARGO_HANDOFF' 'handoff again'
$recordAgain = Get-Rebuild ([string]$gate.UpperId)
$assertions.Add(
    'L2-RH-06',
    '同一转交接请求再来一次：200 AlreadyDone，记录仍是 AWAITING_CARGO_HANDOFF',
    ($preparedAgain.Status -eq 200 -and (Get-Field $preparedAgain 'outcome') -eq 'AlreadyDone' -and
        $null -ne $recordAgain -and [string]$recordAgain.State -eq 'AWAITING_CARGO_HANDOFF'),
    '200 AlreadyDone / AWAITING_CARGO_HANDOFF',
    "$($preparedAgain.Status) $(Get-Field $preparedAgain 'outcome') / $(if ($recordAgain) { [string]$recordAgain.State } else { '(没有记录)' })")

# --- 7. 车载端给出「故障交接」入口，维护人员交接 --------------------------------------------------------------------

$offered = Wait-G3ButtonOffered $onboard $journal '故障交接' 'onboard-fault-cargo-entry' 90
if (-not $offered) {
    Add-G3NotReached $assertions ($ids | Select-Object -Skip 6) '车载端没有给出「故障交接」入口'
    return
}
$requestedAt = [DateTimeOffset]::UtcNow
$journal.Note('Maintenance presses 故障交接 and confirms.')
$null = Invoke-G3ConfirmedButton $onboard $journal '故障交接' '故障货物交接'

$result = Wait-L2Condition -Description 'the server received FaultCargoRecoveryResult, or the onboard reported a refusal' `
    -Journal $journal -Criterion 'fault-cargo-result' -TimeoutSeconds 120 `
    -Probe {
        # 先赋值再筛：Get-G3Inbound 以 `return , @(...)` 返回。
        $inbound = Get-G3Inbound $connection 'FaultCargoRecoveryResult'
        $received = @($inbound | Where-Object { [string]$_.Payload.demandId -eq $demandId })
        if ($received.Count -ge 1) { $received[0] }
        elseif (@($onboard.WindowTitles()) -contains '故障交接失败') { 'REFUSED' }
        else { $null }
    } -Until { param($v) $null -ne $v }
if ($result -is [string]) {
    Add-G3NotReached $assertions ($ids | Select-Object -Skip 6) '故障交接未被接受（车载端弹出「故障交接失败」）'
    return
}
$actionId = [string]$result.Payload.recoveryActionId
$null = Wait-L2Condition -Description 'the handoff workflow settled' `
    -Journal $journal -Criterion 'fault-cargo-workflow' -TimeoutSeconds 30 `
    -Probe { Get-G3Scalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'" } `
    -Until { param($v) $v -in @('Reconciled', 'RecoveryRequired') }

$inboundActions = Get-G3Inbound $connection 'RecoveryActionSubmitted'
$actions = @($inboundActions | Where-Object { [string]$_.Payload.demandId -eq $demandId })
$outboundCommands = Get-G3Outbound $connection 'FaultCargoRecoveryCommand'
$commands = @($outboundCommands | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$workflow = Invoke-L2Query -Connection $connection -Sql "SELECT State, Outcome FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'"
$assertions.Add(
    'L2-RH-07',
    '车载端的交接走完 CV-FAULT-CARGO-HANDOFF：RecoveryActionSubmitted(FAULT_CARGO_HANDOFF) 被接受，一条 FaultCargoRecoveryCommand，结果 HANDED_OFF；工作流 Reconciled / HANDED_OFF',
    ($actions.Count -eq 1 -and $actions[0].Response -eq 'RecoveryActionAccepted' -and
        [string]$actions[0].ResponsePayload.acceptedAction -eq 'FAULT_CARGO_HANDOFF' -and $actions[0].At -ge $requestedAt.AddSeconds(-1) -and
        $commands.Count -eq 1 -and [string]$result.Payload.overallOutcome -eq 'HANDED_OFF' -and
        $workflow.Count -eq 1 -and [string]$workflow[0].State -eq 'Reconciled' -and [string]$workflow[0].Outcome -eq 'HANDED_OFF'),
    'Action×1 RecoveryActionAccepted FAULT_CARGO_HANDOFF / Command×1 / HANDED_OFF / Reconciled HANDED_OFF',
    "Action×$($actions.Count)$(if ($actions.Count -ge 1) { " $($actions[0].Response) $($actions[0].ResponsePayload.acceptedAction)" }) / Command×$($commands.Count) / $($result.Payload.overallOutcome) / $(if ($workflow.Count -eq 1) { "$($workflow[0].State) $($workflow[0].Outcome)" } else { "(工作流 $($workflow.Count) 行)" })")

# --- 8. 交接收敛：需求、旅程、绑定、重建记录、会话 -----------------------------------------------------------------

$settled = Wait-L2ConditionOrLast -Description 'the handoff settled on the server and the session came back ready' `
    -Journal $journal -Criterion 'handoff-settled' -TimeoutSeconds 60 `
    -Probe {
        $journey = Get-Journey
        $record = Get-Rebuild ([string]$gate.UpperId)
        $session = Get-G3Session $connection
        $bindings = Get-Bindings
        [pscustomobject]@{
            Demand    = [string](Get-G3Scalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'")
            Journey   = if ($journey) { "$($journey.Stage)/$($journey.BlockReasonCode)" } else { '' }
            Record    = if ($record) { [string]$record.State } else { '' }
            Readiness = if ($session) { [string]$session.Readiness } else { '' }
            Bindings  = $bindings.Count
            Released  = @($bindings | Where-Object { (Test-G3Present $_.ReleasedAt) -and [string]$_.ReleasedReason -eq 'HANDED_OFF_IN_EXCEPTION_SESSION' }).Count
            Gates     = Get-G3Count $connection "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'"
        }
    } `
    -Until { param($v) $v.Journey -eq 'Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF' -and $v.Readiness -eq 'Ready' -and $v.Record -eq 'ENDED' }
$assertions.Add(
    'L2-RH-08',
    '交接收敛：需求 Cancelled，旅程 Completed / TERMINATED_BY_FAULT_CARGO_HANDOFF，这条需求唯一的故障货物绑定以 HANDED_OFF_IN_EXCEPTION_SESSION 了结，停住的重建记录 ENDED，全程只有一张 TO_GATE 单，会话回到 Ready',
    ($settled.Demand -eq 'Cancelled' -and $settled.Journey -eq 'Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF' -and
        $settled.Bindings -eq 1 -and $settled.Released -eq 1 -and $settled.Record -eq 'ENDED' -and $settled.Gates -eq 1 -and $settled.Readiness -eq 'Ready'),
    'Cancelled / Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / 绑定 1 了结 1 / ENDED / TO_GATE 1 / Ready',
    "$($settled.Demand) / $($settled.Journey) / 绑定 $($settled.Bindings) 了结 $($settled.Released) / $($settled.Record) / TO_GATE $($settled.Gates) / $($settled.Readiness)")

Add-G3VehicleReleasedForNextDemand $Context 'L2-RH-09' `
    '交接收敛之后车放出来了：这条需求的 TO_PICKUP 占用已释放，同一台车在 60 秒内接了下一单（旅程到 AwaitingPickupArrival、意图 CONFIRMED、没有停摆原因码）' `
    $demandId 'L2-RH'

$journal.Note('货不在原仓、重建停住之后：人工重建被拒，人工转交接被受理，车载端给出故障交接入口并交接，绑定了结、记录 ENDED、会话回到 Ready，车接下一单。')
