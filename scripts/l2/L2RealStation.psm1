#Requires -Version 7

<#
Station-side helpers for the real-onboard rig (control-server#86): the leg every ADR-cross-0058 scenario
walks before its own story starts, and the reads its criteria share.

A new file rather than more of L2.psm1, for the reason L2Change.psm1 gives: batch 5 tickets add shared L2
helpers in parallel, and each taking a file of its own keeps them out of each other's module. A scenario that
wants it imports it next to the orchestrator's copy of L2.psm1:

    Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealStation.psm1') -Force

Everything a criterion reads comes from the server's own database or the simulator's snapshot. The onboard UI
is driven, never read for a business fact.
#>

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'L2.psm1')
Import-Module (Join-Path $PSScriptRoot 'L2Change.psm1')

# "door/cargo/lockFeedbackRaw/unlockOutputRaw" of one slot, straight from the simulator's snapshot.
function Get-L2SlotPhysical {
    param([Parameter(Mandatory)][object]$Simulator, [Parameter(Mandatory)][int]$SlotNo)
    $slot = @($Simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $SlotNo })[0]
    return "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
}

<#
One message type the vehicle sent, in the order the server received it: MessageId, At (the server's
ReceivedAt), Payload, and Response -- the type of the server's first answer. The server's inbox is the one
place that shows what the vehicle actually said, and it is the server's own record rather than the UI.
#>
function Get-L2Inbound {
    param([Parameter(Mandatory)][object]$Connection, [Parameter(Mandatory)][string]$MessageType)
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT MessageId, RequestJson, FirstResponseJson, ReceivedAt FROM ProtocolInbox " +
        "WHERE MessageType = '$MessageType' ORDER BY ReceivedAt, MessageId")
    $messages = foreach ($row in $rows) {
        $first = [string]$row.FirstResponseJson
        # A response can span lines (DurableAck followed by SessionReadiness); the first answers the message.
        $response = if ($first) { ($first -split "`n")[0] | ConvertFrom-Json } else { $null }
        [pscustomobject]@{
            MessageId       = [string]$row.MessageId
            At              = [DateTimeOffset]::Parse([string]$row.ReceivedAt, [Globalization.CultureInfo]::InvariantCulture)
            Payload         = ([string]$row.RequestJson | ConvertFrom-Json).payload
            Response        = if ($null -ne $response) { [string]$response.messageType } else { '' }
            ResponsePayload = if ($null -ne $response -and $response.PSObject.Properties['payload']) { $response.payload } else { $null }
        }
    }
    return , @($messages)
}

# The OperationProgress messages of one attempt, oldest first, as At / Phase / Slots.
function Get-L2OperationProgress {
    param([Parameter(Mandatory)][object]$Connection, [Parameter(Mandatory)][string]$AttemptId)
    $mine = foreach ($message in (Get-L2Inbound -Connection $Connection -MessageType 'OperationProgress')) {
        if ([string]$message.Payload.slotOperationAttemptId -ne $AttemptId) { continue }
        [pscustomobject]@{
            At    = $message.At
            Phase = [string]$message.Payload.phase
            Slots = @($message.Payload.activeUnlockSlots | ForEach-Object { [int]$_ })
        }
    }
    return , @($mine)
}

# How many OperationProgress messages of one phase an attempt has sent so far.
function Get-L2PhaseCount {
    param([Parameter(Mandatory)][object]$Connection, [Parameter(Mandatory)][string]$AttemptId, [Parameter(Mandatory)][string]$Phase)
    return @((Get-L2OperationProgress -Connection $Connection -AttemptId $AttemptId) | Where-Object { $_.Phase -eq $Phase }).Count
}

# The OperationResult messages the vehicle sent for one attempt.
function Get-L2OperationResults {
    param([Parameter(Mandatory)][object]$Connection, [Parameter(Mandatory)][string]$AttemptId)
    return , @((Get-L2Inbound -Connection $Connection -MessageType 'OperationResult') |
        Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $AttemptId })
}

# One row of a demand's journey runtime, or $null.
function Get-L2Runtime {
    param([Parameter(Mandatory)][object]$Connection, [Parameter(Mandatory)][string]$DemandId)
    $rows = Invoke-L2Query -Connection $Connection -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$DemandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# The station operation of one type for a demand, or $null.
function Get-L2StationOperation {
    param([Parameter(Mandatory)][object]$Connection, [Parameter(Mandatory)][string]$DemandId, [Parameter(Mandatory)][string]$OperationType)
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT SlotOperationAttemptId, Status, TargetSlotsJson FROM StationOperations " +
        "WHERE DemandId = '$DemandId' AND OperationType = '$OperationType'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# SQLite text to an instant; $null for an empty column.
function ConvertTo-L2Instant($Value) {
    if ($null -eq $Value -or [string]::IsNullOrEmpty([string]$Value)) { return $null }
    if ($Value -is [DateTimeOffset]) { return $Value }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture)
}

<#
Puts one vehicle's order through the RIoT double and parks it at a station: executing, running, then idle at
the station with the order finished. The same sequence real-onboard-normal-load writes out inline.
#>
function Invoke-L2DriveTo {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][object]$Intent,
        [Parameter(Mandatory)][int]$StationRiotId
    )
    $riot = $Context.Riot
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{
        orderState        = 3
        executeVehicleKey = $Context.VehicleKey
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey      = $Context.VehicleKey
        procState       = 'RUNNING'
        movementState   = 'MT_RUNNING'
        speed           = 0.8
        processingOrder = $true
        orderTaskId     = $Intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey       = $Context.VehicleKey
        procState        = 'IDLE'
        movementState    = 'MT_FINISHED'
        speed            = 0
        currentPosition  = $StationRiotId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 5 })
}

# Waits for a demand's intent of one purpose to be CONFIRMED and returns its row.
function Wait-L2ConfirmedIntent {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$DemandId,
        [Parameter(Mandatory)][string]$Purpose,
        [int]$TimeoutSeconds = 60
    )
    $connection = $Context.Connection
    return Wait-L2Condition -Description "the $Purpose intent was confirmed" `
        -Journal $Context.Journal -Criterion "$($Purpose.ToLowerInvariant())-intent" -TimeoutSeconds $TimeoutSeconds `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection `
                -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$DemandId' AND Purpose = '$Purpose'"
            if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
        } `
        -Until { param($v) $null -ne $v }
}

<#
Publishes one demand, drives the vehicle to the pickup station and enters its sublot through UI Automation,
up to the server receiving SublotSubmitted. Every ADR-cross-0058 scenario starts with exactly this; what
differs is what the operator does once the door opens.

The station deadline starts at arrival (control-server#79), so everything here runs inside it: the scenario's
setup file has to leave room for the 4.7-5.4 s this leg measures on the real rig (scripts/l2/README.md).
#>
function Invoke-L2PickupAndScan {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$DemandIdWire,
        [Parameter(Mandatory)][string]$DemandId,
        [Parameter(Mandatory)][string]$Sublot
    )
    $journal = $Context.Journal
    $connection = $Context.Connection
    $onboard = $Context.Onboard

    $journal.Note("Publishing demand $DemandIdWire (sublot $Sublot) to the fake MesIngest catalog.")
    $null = $Context.MesIngest.Command('Put', "demands/$DemandIdWire", @{
        sublot      = $Sublot
        area        = 'N1-3'
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
    $null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { $r = Get-L2Runtime -Connection $connection -DemandId $DemandId; if ($r) { [string]$r.Stage } else { $null } } `
        -Until { param($v) $v -eq 'AwaitingPickupArrival' }
    $intent = Wait-L2ConfirmedIntent -Context $Context -DemandId $DemandId -Purpose 'TO_PICKUP'

    $journal.Note('Vehicle drives to the pickup station and comes to rest.')
    Invoke-L2DriveTo -Context $Context -Intent $intent -StationRiotId $Context.PickupStationRiotId

    $null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
        -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
        -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
    $journal.Note("Typing sublot $Sublot into ScanTextBox through UI Automation.")
    $onboard.SetSublot($Sublot)
    $null = Wait-L2Condition -Description 'the manual submit button became enabled' `
        -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
        -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
    $onboard.Submit()
    $journal.Note('Manual submit invoked.')

    $null = Wait-L2Condition -Description 'the server received SublotSubmitted from the real onboard' `
        -Journal $journal -Criterion 'sublot-submitted' -TimeoutSeconds 60 `
        -Probe { @((Get-L2Inbound -Connection $connection -MessageType 'SublotSubmitted') | Where-Object { [string]$_.Payload.sublot -eq $Sublot }).Count } `
        -Until { param($v) $v -ge 1 }
}

<#
Waits for the server to issue a slot operation of one type for the demand and for the onboard to report it is
waiting for the operator, and returns AttemptId and SlotNo.

Never act on the door as soon as it is open: the onboard needs the lock feedback stable for feedbackStableMs
before it counts the unlock, and WAITING_OPERATOR is the message it sends only after that (README item 7).
The scenarios here drive one slot per operation, so a command for more fails on the spot.
#>
function Wait-L2WaitingOperator {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$DemandId,
        [Parameter(Mandatory)][string]$OperationType
    )
    $journal = $Context.Journal
    $connection = $Context.Connection
    $prefix = $OperationType.ToLowerInvariant()
    $attemptId = Wait-L2Condition -Description "the server issued the $OperationType command" `
        -Journal $journal -Criterion "$prefix-attempt" -TimeoutSeconds 180 `
        -Probe { $op = Get-L2StationOperation -Connection $connection -DemandId $DemandId -OperationType $OperationType; if ($op) { [string]$op.SlotOperationAttemptId } else { $null } } `
        -Until { param($v) $v }
    $waiting = Wait-L2Condition -Description "the onboard is waiting for the operator on the $OperationType" `
        -Journal $journal -Criterion "$prefix-waiting-operator" -TimeoutSeconds 120 `
        -Probe { @((Get-L2OperationProgress -Connection $connection -AttemptId $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })[0] } `
        -Until { param($v) $null -ne $v }
    if ($waiting.Slots.Count -ne 1) {
        throw "This scenario drives one slot per operation; the $OperationType reported $($waiting.Slots.Count) active slots ($($waiting.Slots -join ', '))."
    }
    return [pscustomobject]@{ AttemptId = $attemptId; SlotNo = [int]$waiting.Slots[0] }
}

<#
The operator shuts the door over the wrong occupancy -- an empty slot on a load, a full one on an unload --
and the vehicle reopens it on its own (ADR-cross-0058 decision 1).

Returns once the onboard is waiting for the operator again, which is the moment the next close counts: it
sends that WAITING_OPERATOR only after the new unlock is stable and its output reset. Closing again on the
door being open instead is how 20260909-real-onboard-load-door-closed-empty-001 went red on MVP: 40 ms
after the pop, with the unlock output still 1.

The simulator snapshot has no pulse counter and its unlock output is a 500 ms pulse, too short to count by
polling. What it does show is the door: its automation API cannot open a door (only close-door exists), and
the slot pops open only when the unlock output fires. So a door back at OPEN after a close is the pulse having
reached the IO, and the onboard's UNLOCKING count says it was the vehicle that sent it. Both are returned;
the scenario asserts them.
#>
function Invoke-L2CloseOverOppositeState {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$AttemptId,
        [Parameter(Mandatory)][int]$SlotNo,
        [Parameter(Mandatory)][string]$Criterion
    )
    $connection = $Context.Connection
    $simulator = $Context.Simulator
    $Context.Journal.Note("${Criterion}: closing slot $SlotNo over the opposite occupancy.")
    $change = Wait-L2Change -Description "the onboard reopened slot $SlotNo and waits for the operator again ($Criterion)" `
        -Journal $Context.Journal -Criterion $Criterion -TimeoutSeconds 90 `
        -Baseline {
            [pscustomobject]@{
                Unlocking = Get-L2PhaseCount -Connection $connection -AttemptId $AttemptId -Phase 'UNLOCKING'
                Waiting   = Get-L2PhaseCount -Connection $connection -AttemptId $AttemptId -Phase 'WAITING_OPERATOR'
                Physical  = Get-L2SlotPhysical -Simulator $simulator -SlotNo $SlotNo
            }
        } `
        -Action { $simulator.Command('Post', "slots/$SlotNo/close-door", @{}) } `
        -Probe {
            $progress = Get-L2OperationProgress -Connection $connection -AttemptId $AttemptId
            [pscustomobject]@{
                Unlocking   = @($progress | Where-Object { $_.Phase -eq 'UNLOCKING' }).Count
                Waiting     = @($progress | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' }).Count
                LastUnlock  = @($progress | Where-Object { $_.Phase -eq 'UNLOCKING' } | Select-Object -Last 1)[0]
                Physical    = Get-L2SlotPhysical -Simulator $simulator -SlotNo $SlotNo
            }
        } `
        -Until { param($before, $now) $now.Unlocking -gt $before.Unlocking -and $now.Waiting -gt $before.Waiting -and $now.Physical -like 'OPEN/*/0/0' }
    return [pscustomobject]@{
        Before       = $change.Baseline
        After        = $change.Value
        ReopenedAt   = $change.Value.LastUnlock.At
    }
}

Export-ModuleMember -Function Get-L2SlotPhysical, Get-L2Inbound, Get-L2OperationProgress, Get-L2PhaseCount,
    Get-L2OperationResults, Get-L2Runtime, Get-L2StationOperation, ConvertTo-L2Instant, Invoke-L2DriveTo,
    Wait-L2ConfirmedIntent, Invoke-L2PickupAndScan, Wait-L2WaitingOperator, Invoke-L2CloseOverOppositeState
