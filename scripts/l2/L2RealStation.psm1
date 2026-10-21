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
Import-Module (Join-Path $PSScriptRoot 'L2ConditionOrLast.psm1')

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
Whether the onboard screen offers 「重新开门」 now (onboard-hmi#385). The button binds IsEnabled and Visibility to
the same flag, so absent and disabled read alike, as for 「申请恢复」. Addressed by AutomationId: that is what the
onboard's screen-state map names it by (HmiScreenBindings, ReopenOperation).
#>
function Test-L2ReopenOffered {
    param([Parameter(Mandatory)][object]$Onboard)
    $button = $Onboard.Element('AutomationId', 'ReopenOperation')
    if (-not $button) { return $false }
    return [bool]$button.Current.IsEnabled
}

<#
「重新开门」 as the screen shows it, for the evidence: "absent", or "offered"/"disabled" with the button's ItemStatus, which
the onboard binds to ReopenWaitStatus -- SHUT_OVER_OPPOSITE:<slot> or REOPEN_NOT_RELEASED:<slot> while it waits for a
press (onboard-hmi#385). Read only for the evidence and for telling an eaten press apart; no criterion compares it.
#>
function Get-L2ReopenScreen {
    param([Parameter(Mandatory)][object]$Onboard)
    $button = $Onboard.Element('AutomationId', 'ReopenOperation')
    if (-not $button) { return 'absent' }
    return "$(if ($button.Current.IsEnabled) { 'offered' } else { 'disabled' }) status='$([string]$button.Current.ItemStatus)'"
}

<#
Presses 「重新开门」. On v2 it has no confirmation (onboard-hmi#385): one press is one more unlock pulse. A press the
onboard does not take -- nothing waits for it any more -- answers with a modal 「无法重新开门」, which the caller
looks for when that is the case it expects.
#>
function Invoke-L2ReopenButton {
    param([Parameter(Mandatory)][object]$Onboard)
    $button = $Onboard.Element('AutomationId', 'ReopenOperation')
    if (-not $button) { throw "No element with AutomationId 'ReopenOperation'." }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

<#
Watches one slot for Seconds after its door was shut, on every poll rather than once at the end (README item 14):
a door the vehicle reopened by itself could be shut again before a single read at the end, but its UNLOCKING could
not be taken back, so that count is watched on every poll too.

Returns HeldShut -- the lock read latched at least once, every reading from the first latched one on was
CLOSED/*/1/0 (door shut, lock latched, unlock output reset), and the attempt's UNLOCKING count never rose above
Unlocking -- with the evidence: the readings in the order they changed, each with its offset from the start, and
the highest UNLOCKING count seen.
#>
function Watch-L2SlotHeldShut {
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][object]$Simulator,
        [Parameter(Mandatory)][string]$AttemptId,
        [Parameter(Mandatory)][int]$SlotNo,
        [Parameter(Mandatory)][int]$Unlocking,
        [Parameter(Mandatory)][int]$Seconds
    )
    $started = [DateTimeOffset]::UtcNow
    $end = $started.AddSeconds($Seconds)
    $readings = [System.Collections.Generic.List[string]]::new()
    $last = $null
    $latched = $false
    $broken = $false
    $maxUnlocking = $Unlocking
    $polls = 0
    do {
        $now = [DateTimeOffset]::UtcNow
        $physical = Get-L2SlotPhysical -Simulator $Simulator -SlotNo $SlotNo
        $count = Get-L2PhaseCount -Connection $Connection -AttemptId $AttemptId -Phase 'UNLOCKING'
        $polls++
        if ($count -gt $maxUnlocking) { $maxUnlocking = $count }
        if ($physical -ne $last) {
            $readings.Add("+$([math]::Round(($now - $started).TotalSeconds, 1))s $physical")
            $last = $physical
        }
        if ($physical -like 'CLOSED/*/1/0') { $latched = $true }
        elseif ($latched) { $broken = $true }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $end)
    return [pscustomobject]@{
        HeldShut       = ($latched -and -not $broken -and $maxUnlocking -eq $Unlocking)
        Readings       = ($readings -join ' -> ')
        UnlockingAfter = $maxUnlocking
        Polls          = $polls
    }
}

<#
The operator shuts the door over the wrong occupancy -- an empty slot on a load, a full one on an unload -- and
then presses 「重新开门」 on the onboard screen to have it opened again: ADR-cross-0058 decision 1 as CP-0019
rewrote it (requirements baseline v1.18.0, onboard-hmi#385).

Until onboard-hmi#385 the vehicle reopened such a door by itself, about 0.4 s after it read shut (within about 3 s
of the close on this rig: README item 11, CI run 38107859209). It no longer does: the slot stays shut and locked,
nothing is pulsed, and the screen offers 「重新开门」 (AutomationId ReopenOperation, no confirmation) until someone
presses it. So this does three things and returns the evidence of each:

1. Shuts the door and watches it for HoldSeconds (Watch-L2SlotHeldShut): door shut, lock latched, unlock output
   reset, and no UNLOCKING from the vehicle. HeldShut is the verdict and Held the evidence; the scenario asserts
   HeldShut, which is what tells this onboard from one that still reopens by itself.
2. If the slot was held, waits for the screen to offer 「重新开门」 and presses it (PressedAt).
3. Waits for the slot to reopen and for the onboard to wait for the operator again -- the moment the next close
   counts: it sends that WAITING_OPERATOR only after the new unlock is stable and its output reset. Closing again
   on the door being open instead is how 20260909-real-onboard-load-door-closed-empty-001 went red on MVP: 40 ms
   after the pop, with the unlock output still 1. A press the safety hold ate (the button offered again, no new
   UNLOCKING) gets one more press, logged; Reopened says whether a press opened the slot, Press and Presses say how,
   or what the screen (Get-L2ReopenScreen) and the slot showed when none did. None of these waits throws: the
   caller's criterion carries the outcome (PR #838 review M3, L1).

An onboard that still reopens by itself is not pressed for: step 3 then waits for its own reopen, against the
baseline taken before the close, so the scenario goes on to its later criteria and the red stays on HeldShut.
Step 3 accepting a reopen of the vehicle's own is only safe because step 1 is asserted; dropping that assertion
is the change that would let the old onboard pass.

The simulator snapshot has no pulse counter and its unlock output is a 500 ms pulse, too short to count by
polling. What it does show is the door: its automation API cannot open a door (only close-door exists), and the
slot pops open only when the unlock output fires. So a door back at OPEN after a close is the pulse having
reached the IO, and the onboard's UNLOCKING count says it was the vehicle that sent it. Before and After count
from before the close, as they always have, so one press is After.Unlocking = Before.Unlocking + 1.
#>
function Invoke-L2CloseOverOppositeState {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$AttemptId,
        [Parameter(Mandatory)][int]$SlotNo,
        [Parameter(Mandatory)][string]$Criterion,
        [int]$HoldSeconds = 5,
        [int]$OfferTimeoutSeconds = 30
    )
    $connection = $Context.Connection
    $simulator = $Context.Simulator
    $onboard = $Context.Onboard
    $journal = $Context.Journal

    $before = [pscustomobject]@{
        Unlocking = Get-L2PhaseCount -Connection $connection -AttemptId $AttemptId -Phase 'UNLOCKING'
        Waiting   = Get-L2PhaseCount -Connection $connection -AttemptId $AttemptId -Phase 'WAITING_OPERATOR'
        Physical  = Get-L2SlotPhysical -Simulator $simulator -SlotNo $SlotNo
    }
    $journal.Observe("$Criterion-baseline", $before, $null)
    $journal.Note("${Criterion}: closing slot $SlotNo over the opposite occupancy.")
    $null = $simulator.Command('Post', "slots/$SlotNo/close-door", @{})

    $held = Watch-L2SlotHeldShut -Connection $connection -Simulator $simulator -AttemptId $AttemptId -SlotNo $SlotNo `
        -Unlocking $before.Unlocking -Seconds $HoldSeconds
    $journal.Observe("$Criterion-held-shut", $held, $null)

    $probe = {
        $progress = Get-L2OperationProgress -Connection $connection -AttemptId $AttemptId
        [pscustomobject]@{
            Unlocking  = @($progress | Where-Object { $_.Phase -eq 'UNLOCKING' }).Count
            Waiting    = @($progress | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' }).Count
            LastUnlock = @($progress | Where-Object { $_.Phase -eq 'UNLOCKING' } | Select-Object -Last 1)[0]
            Physical   = Get-L2SlotPhysical -Simulator $simulator -SlotNo $SlotNo
        }
    }
    $reopened = { param($b, $now) $now.Unlocking -gt $b.Unlocking -and $now.Waiting -gt $b.Waiting -and $now.Physical -like 'OPEN/*/0/0' }
    $pressedAt = $null
    $presses = [System.Collections.Generic.List[string]]::new()
    $outcome = $null
    if ($held.HeldShut) {
        # Every wait here answers with its last reading instead of throwing (README item 14's first reading rule), so a
        # press that never opens lands in the caller's criterion with the screen state, not as a bare "Timed out".
        $offered = Wait-L2ConditionOrLast -Description "the onboard offers 重新开门 for slot $SlotNo ($Criterion)" `
            -Journal $journal -Criterion "$Criterion-reopen-offered" -TimeoutSeconds $OfferTimeoutSeconds `
            -Probe { Test-L2ReopenOffered -Onboard $onboard } -Until { param($v) $v }
        if (-not $offered) {
            $outcome = "屏上 $OfferTimeoutSeconds 秒没有给出「重新开门」（$(Get-L2ReopenScreen -Onboard $onboard)）"
            $after = & $probe
        }
        # At most two presses. A press that meets the safety hold is spent on it: the onboard pulses nothing, and once a
        # pulse is allowed again it offers the button anew rather than opening by itself (onboard-hmi#385, review M3 of
        # PR #390). That shape -- offered again, no new UNLOCKING -- is what an operator would answer with a second press,
        # so it gets one, and both presses are in the evidence. Anything else is not retried.
        for ($attempt = 1; $offered -and $attempt -le 2; $attempt++) {
            # The baseline is read right before the press, so the UNLOCKING waited for is this press's own.
            $baseline = & $probe
            $at = [DateTimeOffset]::UtcNow
            if ($null -eq $pressedAt) { $pressedAt = $at }
            Invoke-L2ReopenButton -Onboard $onboard
            $journal.Note("${Criterion}: pressed 重新开门 on slot $SlotNo (press $attempt).")
            $after = Wait-L2ConditionOrLast -Description "slot $SlotNo reopened on press $attempt and the onboard waits for the operator again ($Criterion)" `
                -Journal $journal -Criterion "$Criterion-press-$attempt" -TimeoutSeconds 30 `
                -Probe $probe -Until { param($now) & $reopened $baseline $now }
            if (& $reopened $baseline $after) {
                $presses.Add("第 $attempt 次按键后重开")
                $outcome = if ($attempt -eq 1) { 'Reopened' } else { 'ReopenedOnSecondPress' }
                break
            }
            $screen = Get-L2ReopenScreen -Onboard $onboard
            $eaten = $screen -like 'offered*' -and $after.Unlocking -eq $baseline.Unlocking
            $presses.Add("第 $attempt 次按键 30 秒没有重开：屏上 $screen，UNLOCKING $($baseline.Unlocking)→$($after.Unlocking)，$($after.Physical)" +
                $(if ($eaten) { '，按键被安全暂缓吃掉（屏上又给出「重新开门」、没有开锁）' } else { '' }))
            $journal.Note("${Criterion}: $($presses[$presses.Count - 1])")
            if (-not $eaten) { break }
        }
        if ($offered -and $null -eq $outcome) { $outcome = $presses -join '；' }
    } else {
        $journal.Note("${Criterion}: slot $SlotNo was not held shut ($($held.Readings); UNLOCKING $($before.Unlocking) -> $($held.UnlockingAfter)); not pressing.")
        $after = Wait-L2ConditionOrLast -Description "slot $SlotNo reopened without a press and the onboard waits for the operator again ($Criterion)" `
            -Journal $journal -Criterion $Criterion -TimeoutSeconds 90 `
            -Probe $probe -Until { param($now) & $reopened $before $now }
        $outcome = '没按（车没有锁着门等按键）'
    }
    $journal.Observe("$Criterion-press", [pscustomobject]@{ Outcome = $outcome; Presses = ($presses -join '；') }, $null)
    return [pscustomobject]@{
        Before     = $before
        After      = $after
        HeldShut   = $held.HeldShut
        Held       = $held
        PressedAt  = $pressedAt
        # Reopened by a press: the first, or a second one after the first met the safety hold.
        Reopened   = $outcome -in @('Reopened', 'ReopenedOnSecondPress')
        Press      = $outcome
        Presses    = ($presses -join '；')
        ReopenedAt = if ($null -ne $after.LastUnlock) { $after.LastUnlock.At } else { $null }
    }
}

<#
The hold of Invoke-L2CloseOverOppositeState as the actual column of a criterion, so a red says whether the vehicle
sent an UNLOCKING by itself, the door came back open, or the lock never latched.
#>
function Format-L2HeldShut {
    param([Parameter(Mandatory)][object]$Reopen)
    $held = $Reopen.Held
    $verdict = if ($Reopen.HeldShut) { '空关后车没有自己重开' } else { '空关后车自己重开了或门没锁住' }
    return "$verdict（UNLOCKING $($Reopen.Before.Unlocking)→$($held.UnlockingAfter)；$($held.Readings)）"
}

<#
The press of Invoke-L2CloseOverOppositeState as the actual column: reopened on the first press, on a second one after
the safety hold ate the first (both presses named), or what the screen and the slot showed when it did not reopen.
#>
function Format-L2Press {
    param([Parameter(Mandatory)][object]$Reopen)
    switch ($Reopen.Press) {
        'Reopened' { return '按了重新开门、重开' }
        'ReopenedOnSecondPress' { return "按了两次重新开门、第二次重开（$($Reopen.Presses)）" }
        default { return [string]$Reopen.Press }
    }
}

<#
"No unlock after a slot fault declaration was applied" (REQ-0359), as the operator would test it after CP-0019: shut the
declared slot's door once, watch the screen for Window, and press 「重新开门」 if it is offered. Shared by L2-RSFD-07
(real-onboard-slot-fault-declaration) and G3-07-70 (g3-slot-fault-declaration), so the two cannot drift apart again
(PR #838 review M2: G3-07-70 had kept the pre-CP-0019 form after L2-RSFD-07 was changed).

Before CP-0019 the vehicle reopened such a door by itself while it still ran the attempt, so "the door is still shut
after the window" was enough. After it nothing reopens unless someone presses, and that reading passes whether or not
the declaration stopped the executor (review M2 of PR onboard-hmi#390). So this passes two ways and says which:

- the screen never offers 「重新开门」 within Window -- the run was stopped and nothing waits for a press; or
- it is offered, pressed, and refused (the onboard's 「无法重新开门」 box, closed here) or opens nothing.

Either way the door stays shut and latched throughout (Watch-L2SlotHeldShut, after the shut and after the press) and
the attempt's UNLOCKING count stays at UnlockingBefore. It relies on the onboard sending the UNLOCKING progress before
its declaration gate refuses the pulse (WireToGateSlotOperationExecutor: SendProgressAsync, then ThrowIfDeclared under
_declarationGate): a run that was not stopped turns the press into one more UNLOCKING even though the door stays shut,
which is how the declaration-abort mutation went red (CI run 39553412432). If the onboard ever checks the gate first, a
press would leave no trace here but the refusal box, and this would need another witness.

Returns Passed, Path (which way, for the evidence), ShutHold, PressHold, Physical, UnlockingAfter and Actual (the
criterion's actual column).
#>
function Test-L2NoUnlockAfterDeclaration {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$AttemptId,
        [Parameter(Mandatory)][int]$SlotNo,
        [Parameter(Mandatory)][int]$UnlockingBefore,
        [Parameter(Mandatory)][string]$Criterion,
        [int]$WindowSeconds = 10
    )
    $connection = $Context.Connection
    $simulator = $Context.Simulator
    $onboard = $Context.Onboard
    $journal = $Context.Journal
    $refusedTitle = '无法重新开门'

    $journal.Note("${Criterion}: shutting declared slot $SlotNo once; before CP-0019 a run still going would reopen it, after it the screen would offer 重新开门.")
    $null = $simulator.Command('Post', "slots/$SlotNo/close-door", @{})
    $shutAt = [DateTimeOffset]::UtcNow
    # A whole window, looking at the screen on every poll; stops as soon as it is offered.
    $offeredAt = $null
    do {
        if (Test-L2ReopenOffered -Onboard $onboard) { $offeredAt = [DateTimeOffset]::UtcNow; break }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $shutAt.AddSeconds($WindowSeconds))
    $shutHold = Watch-L2SlotHeldShut -Connection $connection -Simulator $simulator -AttemptId $AttemptId -SlotNo $SlotNo `
        -Unlocking $UnlockingBefore -Seconds 1
    if ($null -eq $offeredAt) {
        $path = "空关后 $WindowSeconds 秒屏上没有「重新开门」"
        $pressHold = $shutHold
    } else {
        $journal.Note("${Criterion}: 重新开门 was offered $([math]::Round(($offeredAt - $shutAt).TotalSeconds, 1)) s after the shut ($(Get-L2ReopenScreen -Onboard $onboard)); pressing it.")
        Invoke-L2ReopenButton -Onboard $onboard
        # A refused press answers with a MessageBox that has only an OK button. Which Win32 control id that button has
        # depends on the box's button set, so IDCANCEL (2) is tried before IDOK (1); by AutomationId for README item 12's reason.
        $dialog = $null
        foreach ($id in '2', '1') {
            try {
                $null = $onboard.Confirm($refusedTitle, $id, 5)
                $dialog = "弹出「$refusedTitle」（已按 $id 关闭）"
                break
            } catch {
                if ($_.Exception.Message -notlike '*has no button*') { $dialog = "没有弹出「$refusedTitle」"; break }
            }
        }
        if ($null -eq $dialog) { $dialog = "弹出「$refusedTitle」，找不到确定键" }
        $pressHold = Watch-L2SlotHeldShut -Connection $connection -Simulator $simulator -AttemptId $AttemptId -SlotNo $SlotNo `
            -Unlocking $UnlockingBefore -Seconds $WindowSeconds
        $path = "空关后 $([math]::Round(($offeredAt - $shutAt).TotalSeconds, 1)) 秒屏上给了「重新开门」，按下：$dialog"
    }
    $physical = Get-L2SlotPhysical -Simulator $simulator -SlotNo $SlotNo
    $unlockingAfter = Get-L2PhaseCount -Connection $connection -AttemptId $AttemptId -Phase 'UNLOCKING'
    $result = [pscustomobject]@{
        Passed         = ($shutHold.HeldShut -and $pressHold.HeldShut -and $physical -like 'CLOSED/*/1/0' -and $unlockingAfter -eq $UnlockingBefore)
        Path           = $path
        ShutHold       = $shutHold
        PressHold      = $pressHold
        Physical       = $physical
        UnlockingAfter = $unlockingAfter
        Actual         = "$path / 空关后 $($shutHold.Readings)$(if ($null -ne $offeredAt) { "；按键后 $($pressHold.Readings)" }) / $physical / " +
            "UNLOCKING $unlockingAfter（窗口内最多 $([math]::Max($shutHold.UnlockingAfter, $pressHold.UnlockingAfter))）"
    }
    $journal.Observe("$Criterion-no-unlock-after-declaration", $result, $null)
    return $result
}

Export-ModuleMember -Function Get-L2SlotPhysical, Get-L2Inbound, Get-L2OperationProgress, Get-L2PhaseCount,
    Get-L2OperationResults, Get-L2Runtime, Get-L2StationOperation, ConvertTo-L2Instant, Invoke-L2DriveTo,
    Wait-L2ConfirmedIntent, Invoke-L2PickupAndScan, Wait-L2WaitingOperator, Invoke-L2CloseOverOppositeState,
    Test-L2ReopenOffered, Get-L2ReopenScreen, Invoke-L2ReopenButton, Watch-L2SlotHeldShut, Format-L2HeldShut, Format-L2Press,
    Test-L2NoUnlockAfterDeclaration
