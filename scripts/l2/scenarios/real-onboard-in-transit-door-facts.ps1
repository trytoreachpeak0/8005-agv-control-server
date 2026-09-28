#Requires -Version 7

<#
control-server#335（REQ-0246）的真装置场景：真车载端在本服务端在途单上正常行驶、途中重连，服务端不按住、不急停；
有货的仓锁反馈在行驶中变成 0（没锁），服务端对本车本单 OrderHold、按不住就急停；RIoT 把单停住、闩锁锁上之后，
锁反馈恢复，服务端自动解除急停（用户 2026-09-28 选的 A），单仍停着，没有 CONTINUE。

**为什么要真车载端。**真车载端在本服务端的在途单上会从服务端的车辆安全接口读到本车有未终结的单（cs#138），会话整段
RecoveryRequired / DEPARTURE_SAFETY_NOT_READY，到站才回来；门锁摘要照发。合成车载端永远报安全、会话永远就绪，看不到这一段，
所以「正常行驶不误按」只有这里证得了。开工前这条场景是只记录不判的探针（run 36387029532），那次量到：门锁摘要全程可取、
最旧 33 秒不变而心跳约 2 秒一次、途中重连 2.24 秒恢复、锁反馈变 0 后 116 毫秒到库。

**假 RIoT 不替服务端改状态**：Hold 不会让单变 PAUSED，急停不会锁闩锁。「按不住」是默认；「单已停住」「闩锁锁上/放开」由场景
照真实 RIoT（riot-behavior-lab BC-ORDER-006、BC-VEH-005）的样子摆出来。

**失联不在这里。**车载端不说话时本票不下命令（用户 2026-09-20 把失联时的 Hold 留到批次 9）。途中重连在静默窗口内，本来就不该触发。

判据只从服务端库、假 RIoT 的控制面与模拟器读；抽样写进 snapshots/door-facts-*.json，作为这一轮的旁证。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$riot = $Context.Riot
$simulator = $Context.Simulator
$proxy = $Context.ProtocolProxy
$onboard = $Context.Onboard
$agvId = $Context.AgvId

if ($null -eq $proxy) { throw 'This scenario needs ProtocolFaultProxy = $true in its setup file.' }

$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')
$sublot = "L2-DOORFACTS-$($Context.RunId)"

function ConvertTo-Instant($value) {
    if (-not (Test-L2RealPresent $value)) { return $null }
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

# Every safety message the server holds for this vehicle, oldest first by receive time, parsed. The rig's inbox is small.
function Get-SafetyMessages {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageType, RequestJson, ReceivedAt FROM ProtocolInbox " +
        "WHERE MessageType IN ('SafetyStateSnapshot', 'SafetyStateChanged')")
    $parsed = foreach ($row in $rows) {
        $envelope = [string]$row.RequestJson | ConvertFrom-Json -DateKind String
        if ([string]$envelope.agvId -ne $agvId) { continue }
        [pscustomobject]@{
            MessageType = [string]$row.MessageType
            Generation  = [long]$envelope.sessionGeneration
            Version     = [long]$envelope.payload.safetyStateVersion
            ObservedAt  = [string]$envelope.payload.observedAt
            ReceivedAt  = ConvertTo-Instant $row.ReceivedAt
            Safety      = $envelope.payload.safety
        }
    }
    return , @($parsed | Sort-Object ReceivedAt)
}

# The newest inbound message of one generation, by the server's receive time.
function Get-LastInboundAt([long]$generation) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT RequestJson, ReceivedAt FROM ProtocolInbox"
    $latest = $null
    foreach ($row in $rows) {
        $envelope = [string]$row.RequestJson | ConvertFrom-Json -DateKind String
        # Every inbound row is scanned, and StrictMode throws on a missing property.
        if ($null -eq $envelope.PSObject.Properties['agvId'] -or [string]$envelope.agvId -ne $agvId) { continue }
        if ($null -eq $envelope.PSObject.Properties['sessionGeneration']) { continue }
        if ([long]$envelope.sessionGeneration -ne $generation) { continue }
        $at = ConvertTo-Instant $row.ReceivedAt
        if ($null -eq $latest -or $at -gt $latest) { $latest = $at }
    }
    return $latest
}

# One sample of everything the server knows about this vehicle's doors right now.
function Get-DoorFactsSample([string]$phase) {
    $now = [DateTimeOffset]::UtcNow
    $sessionRows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT SessionGeneration, Readiness, ReasonCode, SafetyRevision, DepartureSafe, SafetyReasonCodesJson, " +
        "SafetyUnknownPresent FROM SessionRecoveries WHERE AgvId = '$agvId'")
    $journeyRows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT Stage, BlockReasonCode FROM JourneyRuntimes WHERE DemandId = '$demandId'")
    $session = if ($sessionRows.Count -gt 0) { $sessionRows[0] } else { $null }
    $matching = $null
    $lastInbound = $null
    if ($null -ne $session) {
        $generation = [long]$session.SessionGeneration
        if (Test-L2RealPresent $session.SafetyRevision) {
            $revision = [long]$session.SafetyRevision
            $candidates = @((Get-SafetyMessages) | Where-Object { $_.Generation -eq $generation -and $_.Version -eq $revision })
            # StrictMode Latest throws on an out-of-range index, so an empty list is checked rather than indexed.
            if ($candidates.Count -gt 0) { $matching = $candidates[$candidates.Count - 1] }
        }
        $lastInbound = Get-LastInboundAt $generation
    }
    return [pscustomobject]@{
        Phase               = $phase
        At                  = $now.ToString('o')
        Generation          = if ($session) { [long]$session.SessionGeneration } else { $null }
        Readiness           = if ($session) { [string]$session.Readiness } else { $null }
        ReasonCode          = if ($session) { [string]$session.ReasonCode } else { $null }
        SafetyRevision      = if ($session -and (Test-L2RealPresent $session.SafetyRevision)) { [long]$session.SafetyRevision } else { $null }
        RowDepartureSafe    = if ($session) { $session.DepartureSafe } else { $null }
        RowReasonCodes      = if ($session) { [string]$session.SafetyReasonCodesJson } else { $null }
        SummaryMessageType  = if ($matching) { $matching.MessageType } else { $null }
        SummaryReceivedAt   = if ($matching) { $matching.ReceivedAt.ToString('o') } else { $null }
        SummaryAgeMs        = if ($matching) { [int]($now - $matching.ReceivedAt).TotalMilliseconds } else { $null }
        Summary             = if ($matching) { $matching.Safety } else { $null }
        LastInboundAgeMs    = if ($lastInbound) { [int]($now - $lastInbound).TotalMilliseconds } else { $null }
        Stage               = if ($journeyRows.Count -gt 0) { [string]$journeyRows[0].Stage } else { $null }
        BlockReasonCode     = if ($journeyRows.Count -gt 0) { [string]$journeyRows[0].BlockReasonCode } else { $null }
    }
}

function Invoke-Sampling([string]$phase, [int]$seconds, [int]$pollMilliseconds = 500) {
    $samples = [System.Collections.Generic.List[object]]::new()
    $until = [DateTimeOffset]::UtcNow.AddSeconds($seconds)
    while ([DateTimeOffset]::UtcNow -lt $until) {
        $samples.Add((Get-DoorFactsSample $phase))
        Start-Sleep -Milliseconds $pollMilliseconds
    }
    return , $samples.ToArray()
}

function Save-Evidence([string]$name, [object]$value) {
    $path = Join-Path $Context.SnapshotRoot "door-facts-$name.json"
    $value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    $journal.Note("Wrote $path.")
}

function Get-SlotReading([int]$slotNo) {
    $slot = @($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $slotNo })[0]
    return "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
}

function Start-Drive([object]$intent) {
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $intent.OrderId
        # Between stations RIoT reports station 0 (ADR-cross-0060); L2-DF-08 needs the vehicle there to see the release kept.
        currentPosition = 0
    })
}

function Complete-Drive([object]$intent, [int]$stationRiotId) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $stationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })
}

function Get-Stage { return Get-L2RealStage $connection $demandId }

function Get-AttemptId([string]$operationType) {
    return Get-L2RealScalar $connection (
        "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = '$operationType'")
}

function Invoke-SlotOperation([string]$operationType, [string]$cargoState) {
    $attemptId = Wait-L2Condition -Description "the server issued the $operationType command" `
        -Journal $journal -Criterion "$operationType-attempt" -TimeoutSeconds 180 `
        -Probe { Get-AttemptId $operationType } -Until { param($v) $v }
    $waiting = Wait-L2Condition -Description "the onboard is waiting for the operator for the $operationType" `
        -Journal $journal -Criterion "$operationType-waiting-operator" -TimeoutSeconds 120 `
        -Probe { @((Get-L2RealProgress $connection $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })[0] } `
        -Until { param($v) $null -ne $v }
    if ($waiting.Active.Count -ne 1) { throw "This scenario drives one slot per operation; got $($waiting.Active.Count)." }
    $slotNo = [int]$waiting.Active[0]
    $null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = $cargoState })
    $null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})
    return $slotNo
}


function Get-Attempts([string]$commandType) {
    # 调用方先赋值再用，不要直接送进管道：`Invoke-L2Query` 的 `return , $rows` 包装穿得过一层 return。
    return Invoke-L2Query -Connection $connection -Sql @"
SELECT CommandType, AgvId, TargetUpperId, AttemptNumber, IssuedAt, Outcome, FaultGeneration, ReceiptJson
FROM RiotOrderCommandAudit WHERE CommandType = '$commandType' ORDER BY AttemptNumber
"@
}

function Get-RiotInvocations([string]$commandType) {
    return , @(@($riot.Snapshot().body.commandInvocations) | Where-Object { [string]$_.commandType -eq $commandType })
}

function Get-FaultCommandCounts {
    $holds = Get-Attempts 'OrderHold'
    $triggers = Get-Attempts 'triggerEmergency'
    $cancels = Get-Attempts 'CANCEL'
    return "$($holds.Count)/$($triggers.Count)/$($cancels.Count)"
}

function Get-Instant($value) {
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

# --- 1. 需求受理；取货段（空车）行驶 12 秒：不按住 ---------------------------------------------------------

$journal.Note("Publishing demand $($demandGuid.ToString('N')) (sublot $sublot).")
$null = $Context.MesIngest.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = $sublot; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})
$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$pickupIntent = Wait-L2RealIntent $Context $demandId 'TO_PICKUP'

$journal.Note('Vehicle departs for the pickup station; sampling for 12 s.')
Start-Drive $pickupIntent
$pickupSamples = Invoke-Sampling 'P-pickup-drive' 12
Save-Evidence 'P-pickup-drive' @{ Samples = $pickupSamples }
$pickupCounts = Get-FaultCommandCounts
$assertions.Add(
    'L2-DF-01', '真车载端空车行驶 12 秒（会话因本单未就绪）：不 Hold、不急停、不 Cancel',
    ($pickupSamples.Count -ge 10 -and $pickupCounts -eq '0/0/0'),
    '>= 10 samples, 0/0/0', "$($pickupSamples.Count) samples, $pickupCounts")
Complete-Drive $pickupIntent $Context.PickupStationRiotId

# --- 2. 装货 --------------------------------------------------------------------------------------------------------

$null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
$onboard.SetSublot($sublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()
$loadSlot = Invoke-SlotOperation 'Load' 'OCCUPIED'
$null = Wait-L2Condition -Description 'the loaded slot is closed, locked and occupied' `
    -Journal $journal -Criterion 'load-slot-physical' -TimeoutSeconds 60 `
    -Probe { Get-SlotReading $loadSlot } -Until { param($v) $v -eq 'CLOSED/OCCUPIED/1/0' }
$null = Wait-L2Condition -Description 'the load committed and the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$gateIntent = Wait-L2RealIntent $Context $demandId 'TO_GATE'
$gateUpperId = [string]$gateIntent.UpperId
$gateOrderId = [string]$gateIntent.OrderId

# --- 3. 关卡段（有货）行驶 12 秒、途中断线重连一次：不按住 ---------------------------------------------------

$journal.Note("Vehicle departs for the gate with slot $loadSlot loaded; sampling for 12 s.")
Start-Drive $gateIntent
$gateSamples = Invoke-Sampling 'A-gate-drive' 12
Save-Evidence 'A-gate-drive' @{ LoadSlot = $loadSlot; Samples = $gateSamples }

$oldGeneration = (Get-DoorFactsSample 'D-before').Generation
$disconnectAt = [DateTimeOffset]::UtcNow
$null = $proxy.Command('Post', 'disconnect', @{})
$journal.Note("Disconnected the onboard link at $($disconnectAt.ToString('o')); old generation $oldGeneration.")
$reconnected = Wait-L2Condition -Description 'the new generation reported its safety snapshot' `
    -Journal $journal -Criterion 'reconnect-safety' -TimeoutSeconds 60 -PollMilliseconds 100 `
    -Probe { $sample = Get-DoorFactsSample 'D-reconnect'; if ($sample.Generation -gt $oldGeneration -and $null -ne $sample.Summary) { $sample } else { $null } } `
    -Until { param($v) $null -ne $v }
$afterReconnect = Invoke-Sampling 'D-after-reconnect' 5
Save-Evidence 'D-reconnect' @{ OldGeneration = $oldGeneration; DisconnectAt = $disconnectAt.ToString('o'); Reconnected = $reconnected; Samples = $afterReconnect }
$gateCounts = Get-FaultCommandCounts
$assertions.Add(
    'L2-DF-02', '真车载端有货行驶 12 秒并途中重连一次：不 Hold、不急停、不 Cancel',
    ($gateSamples.Count -ge 10 -and $gateCounts -eq '0/0/0'),
    '>= 10 samples, 0/0/0', "$($gateSamples.Count) samples, $gateCounts")

# --- 4. 有货的仓锁反馈变 0：本车本单 Hold，按不住、车在动，急停一次 --------------------------------------------

$lockAt = [DateTimeOffset]::UtcNow
$null = $simulator.Command('Put', "slots/$loadSlot/lock-feedback-override", @{ mode = 'FIXED_0' })
$journal.Note("Lock feedback of slot $loadSlot forced to 0 while driving.")
$null = Wait-L2Condition -Description 'the server issued the emergency stop' `
    -Journal $journal -Criterion 'trigger-issued' -TimeoutSeconds 60 `
    -Probe { (Get-Attempts 'triggerEmergency').Count } -Until { param($v) $v -ge 1 }
$trigger = Wait-L2ConditionOrLast -Description 'the trigger attempt was settled' `
    -Journal $journal -Criterion 'trigger-settled' -TimeoutSeconds 30 `
    -Probe { $rows = Get-Attempts 'triggerEmergency'; if ($rows.Count -gt 0) { $rows[0] } else { $null } } `
    -Until { param($row) -not [string]::IsNullOrEmpty([string]$row.ReceiptJson) }
$holds = Get-Attempts 'OrderHold'
$hold = if ($holds.Count -gt 0) { $holds[0] } else { $null }
$heldCalls = Get-RiotInvocations 'CMD_ORDER_HELD'
$assertions.Add(
    'L2-DF-03', '锁反馈变 0：Hold 恰好一条，打在本车、关卡段这一张单上，RIoT 侧收到的 CMD_ORDER_HELD 也打在这张 orderId 上',
    ($holds.Count -eq 1 -and [string]$hold.AgvId -eq $agvId -and [string]$hold.TargetUpperId -eq $gateUpperId -and
        $heldCalls.Count -ge 1 -and @($heldCalls | Where-Object { [string]$_.target -ne $gateOrderId }).Count -eq 0),
    "1 / $agvId / $gateUpperId / -> $gateOrderId",
    $(if ($hold) { "$($holds.Count) / $([string]$hold.AgvId) / $([string]$hold.TargetUpperId) / -> $((@($heldCalls) | ForEach-Object { $_.target }) -join ',')" } else { '0' }))
$assertions.Add(
    'L2-DF-04', '按不住、车还在动：急停恰好一条，打在这台车上，而且在 Hold 之后',
    ((Get-Attempts 'triggerEmergency').Count -eq 1 -and [string]$trigger.AgvId -eq $agvId -and
        $null -ne $hold -and (Get-Instant $hold.IssuedAt) -le (Get-Instant $trigger.IssuedAt)),
    "1 / $agvId / hold first",
    "$((Get-Attempts 'triggerEmergency').Count) / $([string]$trigger.AgvId) / hold $(if ($hold) { [string]$hold.IssuedAt } else { '-' }) trigger $([string]$trigger.IssuedAt)")
$faults = Invoke-L2Query -Connection $connection -Sql "SELECT EvidenceCode FROM VehicleFaultStates WHERE AgvId = '$agvId'"
$assertions.Add(
    'L2-DF-05', '故障事实记的是门锁症状',
    ($faults.Count -eq 1 -and [string]$faults[0].EvidenceCode -eq 'VEHICLE_DOOR_NOT_PROVEN_LOCKED'),
    'VEHICLE_DOOR_NOT_PROVEN_LOCKED', $(if ($faults.Count -gt 0) { [string]$faults[0].EvidenceCode } else { '-' }))

# --- 5. RIoT 停住单、锁上闩锁；锁反馈恢复：自动解除，单仍停着，没有 CONTINUE ----------------------------------------

$journal.Note('RIoT parks the order (PAUSED 7) and latches the emergency stop.')
$null = $riot.Command('Put', "orders/$gateUpperId", @{ orderState = 7 })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'USER_FORCE_IDLE'; movementState = 'MT_PAUSED'; speed = 0; emergencyState = 'CAN_RECOVER'
})
# Rounds, not seconds: under load on vm01 a fixed sleep can end before a single round has run (review suggestion).
$null = Wait-L2Iterations -Riot $riot -Count 3 -TimeoutSeconds 60 -Journal $journal
$assertions.Add(
    'L2-DF-06', '锁反馈仍是 0 时不解除', ((Get-Attempts 'cancelEmergency').Count -eq 0), 0, (Get-Attempts 'cancelEmergency').Count)

$null = $simulator.Command('Put', "slots/$loadSlot/lock-feedback-override", @{ mode = 'AUTO' })
$journal.Note("Lock feedback of slot $loadSlot back to AUTO.")
$null = Wait-L2Condition -Description 'the server released the emergency stop' `
    -Journal $journal -Criterion 'release-issued' -TimeoutSeconds 60 `
    -Probe { (Get-Attempts 'cancelEmergency').Count } -Until { param($v) $v -ge 1 }
$release = Wait-L2ConditionOrLast -Description 'the release attempt was settled' `
    -Journal $journal -Criterion 'release-settled' -TimeoutSeconds 30 `
    -Probe { $rows = Get-Attempts 'cancelEmergency'; if ($rows.Count -gt 0) { $rows[0] } else { $null } } `
    -Until { param($row) -not [string]::IsNullOrEmpty([string]$row.ReceiptJson) }
$releaseReason = ([string]$release.ReceiptJson | ConvertFrom-Json).Reason
$assertions.Add(
    'L2-DF-07', '锁反馈恢复后自动解除一次，原因记门锁原因消除',
    ((Get-Attempts 'cancelEmergency').Count -eq 1 -and $releaseReason -eq 'EMERGENCY_DOOR_CAUSE_REMOVED'),
    '1 / EMERGENCY_DOOR_CAUSE_REMOVED', "$((Get-Attempts 'cancelEmergency').Count) / $releaseReason")

$null = $riot.Command('Put', 'vehicle', @{ vehicleKey = $Context.VehicleKey; emergencyState = 'OK' })
$afterRelease = Invoke-Sampling 'L-after-release' 5
$null = Wait-L2Iterations -Riot $riot -Count 5 -TimeoutSeconds 60 -Journal $journal
Save-Evidence 'L-release' @{ LockAt = $lockAt.ToString('o'); Samples = $afterRelease }
$continues = Get-Attempts 'OrderContinue'
$assertions.Add(
    'L2-DF-08', '解除之后单仍停着：没有 CONTINUE，没有第二次急停，没有 Cancel',
    ($continues.Count -eq 0 -and (Get-RiotInvocations 'CMD_ORDER_CONTINUE_FROM_HELD').Count -eq 0 -and
        (Get-Attempts 'triggerEmergency').Count -eq 1 -and (Get-Attempts 'CANCEL').Count -eq 0),
    '0 continue / 1 trigger / 0 cancel',
    "$($continues.Count) continue / $((Get-Attempts 'triggerEmergency').Count) trigger / $((Get-Attempts 'CANCEL').Count) cancel")

Save-Evidence 'all-safety-messages' (Get-SafetyMessages)
Save-Evidence 'final' (Get-DoorFactsSample 'final')
$journal.Note('Scenario finished; the held order waits for a person, as designed.')
