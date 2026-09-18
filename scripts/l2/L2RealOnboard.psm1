#Requires -Version 7

<#
Shared reads and drivers for the four real-onboard scenarios of control-server#88 (program#61's paired fixes):
real-onboard-durable-ack-lost, real-onboard-compensate-then-reconnect,
real-onboard-restart-while-waiting-operator and real-onboard-cancellation-authorization-lost.

A new file rather than more of L2.psm1, for the reason L2Change.psm1 gives: batch-5 tickets add shared L2 helpers in
parallel, and each taking a file of its own keeps them out of each other's module. Nor does it borrow
scenarios/G3RecoveryCommon.ps1, whose drivers look the same: control-server#128 is rewriting that file's preconditions,
and the one those recovery scenarios share -- close the door empty and wait for a FAILED result -- is unreachable on a v2
onboard, which reopens an empty door instead (onboard-hmi#72, program#55). The dialog helpers below are the same
technique as that file's and carry the same reasons.

A scenario imports it next to the orchestrator's copy of L2.psm1:

    Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force

It imports L2.psm1 without -Force, so it reuses the orchestrator's copy: the L2Journal a scenario holds is that copy's
class. Probes built in here are closures (GetNewClosure), so they carry their own variables into Wait-L2Condition.

Everything read here comes from the three places the rig reads: the server's SQLite store, the slots simulator's
snapshot and the protocol fault proxy's traffic log. The HMI is only driven; the one thing read off it is whether an
entry is offered, which is the precondition for pressing it, not a business fact.
#>

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'L2.psm1')

function Test-L2RealPresent([object]$Value) {
    return ($null -ne $Value -and $Value -isnot [System.DBNull] -and [string]$Value -ne '')
}

function ConvertTo-L2RealInstant([object]$Value) {
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture)
}

# One column called Value from the first row, as a string; $null when there is no row.
function Get-L2RealScalar([object]$Connection, [string]$Sql) {
    $rows = Invoke-L2Query -Connection $Connection -Sql $Sql
    if ($rows.Count -eq 0) { return $null }
    if (-not (Test-L2RealPresent $rows[0].Value)) { return $null }
    return [string]$rows[0].Value
}

function Get-L2RealCount([object]$Connection, [string]$Sql) {
    $rows = Invoke-L2Query -Connection $Connection -Sql $Sql
    return [int]$rows[0].Total
}

<#
What the onboard sent of one message type, in the order the server received it. The inbox keeps one row per messageId:
a same-id replay the server judged equivalent overwrites that row, so how often a message crossed the wire is the proxy's
to say, not this. Response is the first line of the server's first answer, ResponseTypes every line of it (an ack is
often followed by a SessionReadiness in the same answer).
#>
function Get-L2RealInbound([object]$Connection, [string]$MessageType) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT MessageId, RequestJson, FirstResponseJson, ReceivedAt FROM ProtocolInbox " +
        "WHERE MessageType = '$MessageType' ORDER BY ReceivedAt, MessageId")
    $messages = foreach ($row in $rows) {
        $request = [string]$row.RequestJson | ConvertFrom-Json -DateKind String
        $answers = if (Test-L2RealPresent $row.FirstResponseJson) {
            @(([string]$row.FirstResponseJson) -split "`n" | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json -DateKind String })
        } else { @() }
        $first = if ($answers.Count -gt 0) { $answers[0] } else { $null }
        [pscustomobject]@{
            MessageId       = [string]$row.MessageId
            At              = ConvertTo-L2RealInstant $row.ReceivedAt
            Generation      = $request.sessionGeneration
            Payload         = $request.payload
            PayloadJson     = $request.payload | ConvertTo-Json -Depth 20 -Compress
            Response        = if ($null -ne $first) { [string]$first.messageType } else { '' }
            ResponsePayload = if ($null -ne $first -and $first.PSObject.Properties['payload']) { $first.payload } else { $null }
            ResponseTypes   = @($answers | ForEach-Object { [string]$_.messageType })
            Answers         = $answers
        }
    }
    return , @($messages)
}

# What the server queued for the vehicle of one message type, in the order it was created.
function Get-L2RealOutbound([object]$Connection, [string]$MessageType) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT MessageId, PayloadJson, CreatedAt, AcknowledgedAt, FencedAt FROM ProtocolOutbox " +
        "WHERE MessageType = '$MessageType' ORDER BY CreatedAt, MessageId")
    $messages = foreach ($row in $rows) {
        [pscustomobject]@{
            MessageId    = ([string]$row.MessageId).ToLowerInvariant()
            MessageType  = $MessageType
            At           = ConvertTo-L2RealInstant $row.CreatedAt
            Payload      = ([string]$row.PayloadJson | ConvertFrom-Json -DateKind String).payload
            Acknowledged = Test-L2RealPresent $row.AcknowledgedAt
            Fenced       = Test-L2RealPresent $row.FencedAt
        }
    }
    return , @($messages)
}

function Get-L2RealProgress([object]$Connection, [string]$AttemptId) {
    $mine = foreach ($message in (Get-L2RealInbound $Connection 'OperationProgress')) {
        if ([string]$message.Payload.slotOperationAttemptId -ne $AttemptId) { continue }
        [pscustomobject]@{
            At     = $message.At
            Phase  = [string]$message.Payload.phase
            Active = @($message.Payload.activeUnlockSlots | ForEach-Object { [int]$_ })
        }
    }
    return , @($mine)
}

# The vehicle's session row: SessionGeneration, Readiness, ReasonCode.
function Get-L2RealSession([object]$Connection, [string]$AgvId) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT SessionGeneration, Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$AgvId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Format-L2RealSession([object]$Row) {
    if ($null -eq $Row) { return '(no session)' }
    return "gen $($Row.SessionGeneration) / $($Row.Readiness) / $($Row.ReasonCode)"
}

# door/cargo/lockFeedbackRaw/unlockOutputRaw, the simulator's physical record of one slot.
function Get-L2RealSlotReading([object]$Simulator, [int]$SlotNo) {
    $slot = @($Simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $SlotNo })[0]
    return "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
}

function Format-L2RealSlots([object[]]$Slots) { return (@($Slots | ForEach-Object { [int]$_ } | Sort-Object) -join ',') }

function Add-L2RealNotReached([object]$Assertions, [string[]]$Ids, [string]$Why) {
    foreach ($id in $Ids) {
        $Assertions.Add($id, "未到达：$Why", $false, '(reached)', "(not reached) $Why")
    }
}

<#
Wait-L2Condition, except that a timeout is an answer rather than an error: the probe is read once more and its value
returned, so it can go into the criteria table. A criterion whose failure *is* a timeout (the server never granted
readiness, the replay was never acknowledged) would otherwise leave nothing but a "Last observed:" line behind.
#>
function Wait-L2RealOrLast {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][string]$Criterion,
        [Parameter(Mandatory)][scriptblock]$Probe,
        [Parameter(Mandatory)][scriptblock]$Until,
        [Parameter(Mandatory)][object]$Journal,
        [int]$TimeoutSeconds = 60
    )
    try {
        return Wait-L2Condition -Description $Description -Journal $Journal -Criterion $Criterion `
            -TimeoutSeconds $TimeoutSeconds -Probe $Probe -Until $Until
    } catch {
        $Journal.Note("Not reached: $($_.Exception.Message)")
        return & $Probe
    }
}

# --- the HMI ---------------------------------------------------------------------------------------------------------

# An entry's visibility and enablement both follow its CanRequest* property, so absent and disabled read alike.
function Wait-L2RealButtonOffered([object]$Onboard, [object]$Journal, [string]$Name, [string]$Criterion, [int]$TimeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $available = [bool]$Onboard.ButtonAvailable($Name)
        $Journal.Observe($Criterion, $available, $null)
        if ($available -or [DateTimeOffset]::UtcNow -ge $deadline) { return $available }
        Start-Sleep -Milliseconds 500
    }
}

if (-not ('L2Real.DialogNative' -as [type])) {
    Add-Type -Namespace 'L2Real' -Name 'DialogNative' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
public static extern bool PostMessage(System.IntPtr hWnd, uint msg, System.IntPtr wParam, System.IntPtr lParam);
'@
}

<#
Answers a modal dialog: waits for a window titled $Title, presses the button whose AutomationId is $ButtonAutomationId
and checks the window really closed. $false when no such dialog appeared within $AppearSeconds.

A failed UIA Invoke falls back to posting BM_CLICK to the button's own window: a freshly restarted onboard often does not
hold the foreground, and Invoke on a MessageBox button then throws while the onboard receives nothing (G3 resume-001,
2026-09-14). Posting is asynchronous and needs no foreground.
#>
function Invoke-L2RealDialogButton([object]$Onboard, [object]$Journal, [string]$Title, [string]$ButtonAutomationId, [int]$AppearSeconds) {
    $isButton = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $ButtonAutomationId)
    $appearDeadline = [DateTimeOffset]::UtcNow.AddSeconds($AppearSeconds)
    $answerDeadline = $null
    $pressed = $false
    while ($true) {
        # Select-Object rather than [0]: under StrictMode an index into the empty list a closed dialog leaves is an
        # error, and the dialog closing is exactly what a successful press looks like.
        $dialog = $Onboard.Windows() | Where-Object { $_.Current.Name -eq $Title } | Select-Object -First 1
        if ($null -eq $dialog) {
            if ($pressed) { return $true }
            if ([DateTimeOffset]::UtcNow -ge $appearDeadline) { return $false }
            Start-Sleep -Milliseconds 250
            continue
        }
        if ($null -eq $answerDeadline) { $answerDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15) }
        if ([DateTimeOffset]::UtcNow -ge $answerDeadline) {
            throw "Dialog '$Title' is still open 15s after its button $ButtonAutomationId was pressed."
        }
        $button = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $isButton)
        if ($null -eq $button) { throw "Dialog '$Title' has no button with AutomationId '$ButtonAutomationId'." }
        try {
            $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        } catch {
            $handle = [IntPtr]$button.Current.NativeWindowHandle
            $Journal.Note("Invoke on '$Title' button $ButtonAutomationId failed ($($_.Exception.Message)); posting BM_CLICK to hwnd $handle.")
            if ($handle -eq [IntPtr]::Zero -or -not [L2Real.DialogNative]::PostMessage($handle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)) {
                throw "Could not press '$Title' button ${ButtonAutomationId}: no window handle to post BM_CLICK to."
            }
        }
        $pressed = $true
        Start-Sleep -Milliseconds 500
    }
}

<#
Presses an entry and answers its confirmation with Yes (IDYES = 6). A button that has just appeared can read enabled
while the window is still laying out, and a press then lands nowhere (G3 manual-002, 2026-09-14), so it must stay enabled
for a second first; with no confirmation within 10 seconds it is pressed again, at most three times. No confirmation
means no request left the vehicle, so pressing again cannot duplicate one.
#>
function Invoke-L2RealConfirmedButton([object]$Onboard, [object]$Journal, [string]$Name, [string]$DialogTitle, [int]$Attempts = 3) {
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $stableSince = $null
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            if ([bool]$Onboard.ButtonAvailable($Name)) {
                if ($null -eq $stableSince) { $stableSince = [DateTimeOffset]::UtcNow }
                elseif (([DateTimeOffset]::UtcNow - $stableSince).TotalSeconds -ge 1) { break }
            } else { $stableSince = $null }
            Start-Sleep -Milliseconds 200
        }
        $Journal.Note("Pressing $Name (attempt $attempt).")
        $Onboard.InvokeButton($Name)
        if (Invoke-L2RealDialogButton $Onboard $Journal $DialogTitle '6' 10) { return $true }
        $Journal.Note("No '$DialogTitle' dialog after pressing $Name (attempt $attempt).")
    }
    throw "Pressing '$Name' never raised the '$DialogTitle' confirmation in $Attempts attempts."
}

# Dismisses a notice with a single OK button. Win32 does not always give that one button IDOK, so both ids are tried.
function Confirm-L2RealNotice([object]$Onboard, [object]$Journal, [string]$Title, [int]$AppearSeconds = 5) {
    foreach ($id in @('2', '1')) {
        try { return Invoke-L2RealDialogButton $Onboard $Journal $Title $id $AppearSeconds }
        catch { if ($_.Exception.Message -notlike '*has no button with AutomationId*') { throw } }
    }
    throw "Notice '$Title' has neither button 2 nor button 1."
}

# --- the vehicle's movements, which RIoT and the real vehicle make on site ------------------------------------------

function Move-L2RealVehicleTo([object]$Context, [object]$Intent, [int]$StationRiotId, [string]$Label) {
    $riot = $Context.Riot
    $Context.Journal.Note("Vehicle departs for $Label.")
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $Intent.OrderId
    })
    $Context.Journal.Note("Vehicle arrives at $Label and comes to rest.")
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $StationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 5 })
}

# The confirmed order intent of one purpose (TO_PICKUP, TO_GATE) for a demand.
function Wait-L2RealIntent([object]$Context, [string]$DemandId, [string]$Purpose, [int]$TimeoutSeconds = 120) {
    $connection = $Context.Connection
    return Wait-L2Condition -Description "the $Purpose intent for $DemandId was confirmed" `
        -Journal $Context.Journal -Criterion "$($Purpose.ToLowerInvariant())-intent" -TimeoutSeconds $TimeoutSeconds `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection `
                -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$DemandId' AND Purpose = '$Purpose'"
            if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
        }.GetNewClosure() `
        -Until { param($v) $null -ne $v }
}

function Get-L2RealStage([object]$Connection, [string]$DemandId) {
    return Get-L2RealScalar $Connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$DemandId'"
}

<#
A demand from publication to "the onboard is waiting for the operator on the load slot": the fake MesIngest publishes it,
the vehicle drives to the pickup, the sublot goes in through UI Automation, the server issues the load command and the
onboard opens the first slot and reports WAITING_OPERATOR. That phase is sent only after the lock feedback has been
stable and the unlock output has reset (scripts/l2/README.md item 7), so from here on the door really is open and the
onboard is really waiting -- the moment every one of these scenarios injects its fault at.

Returns DemandId, AttemptId, Slot (the one open now), TargetSlots and Sublot.
#>
function Start-L2RealLoad([object]$Context, [string]$SublotPrefix, [int]$MaxBoxCount = 4) {
    $journal = $Context.Journal
    $connection = $Context.Connection
    $onboard = $Context.Onboard

    $demandGuid = [guid]::NewGuid()
    $demandId = $demandGuid.ToString('D')
    $sublot = "$SublotPrefix-$($Context.RunId)"

    $journal.Note("Publishing demand $($demandGuid.ToString('N')) (sublot $sublot, $MaxBoxCount boxes).")
    $null = $Context.MesIngest.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
        sublot = $sublot; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = $MaxBoxCount
    })
    $null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { Get-L2RealStage $connection $demandId }.GetNewClosure() `
        -Until { param($v) $v -eq 'AwaitingPickupArrival' }
    $intent = Wait-L2RealIntent $Context $demandId 'TO_PICKUP'
    Move-L2RealVehicleTo $Context $intent $Context.PickupStationRiotId 'the pickup station'

    $null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
        -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
        -Probe { $onboard.CanSubmit() }.GetNewClosure() -Until { param($v) $v }
    $journal.Note("Typing sublot $sublot into ScanTextBox through UI Automation.")
    $onboard.SetSublot($sublot)
    $null = Wait-L2Condition -Description 'the manual submit button became enabled' `
        -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
        -Probe { $onboard.SubmitReady() }.GetNewClosure() -Until { param($v) $v }
    $onboard.Submit()
    $journal.Note('Manual submit invoked.')

    $attemptId = Wait-L2Condition -Description 'the server issued the load command' `
        -Journal $journal -Criterion 'load-attempt' -TimeoutSeconds 180 `
        -Probe { Get-L2RealScalar $connection "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'" }.GetNewClosure() `
        -Until { param($v) $v }
    $targetSlots = @([string](Get-L2RealScalar $connection "SELECT TargetSlotsJson AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'") |
        ConvertFrom-Json | ForEach-Object { [int]$_ })
    $waiting = Wait-L2Condition -Description 'the onboard is waiting for the operator on the load slot' `
        -Journal $journal -Criterion 'load-waiting-operator' -TimeoutSeconds 120 `
        -Probe { @((Get-L2RealProgress $connection $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })[0] }.GetNewClosure() `
        -Until { param($v) $null -ne $v }
    if ($waiting.Active.Count -ne 1) {
        throw "The onboard reported WAITING_OPERATOR on $($waiting.Active.Count) slots at once; these scenarios drive one slot at a time."
    }
    $slot = [int]$waiting.Active[0]
    $journal.Note("Load $attemptId targets slots $($targetSlots -join ', '); slot $slot is open and the onboard waits for the operator.")

    return [pscustomobject]@{
        DemandId    = $demandId
        AttemptId   = $attemptId
        Slot        = $slot
        TargetSlots = $targetSlots
        Sublot      = $sublot
    }
}

# --- the protocol fault proxy -----------------------------------------------------------------------------------------

# The relay's traffic log: connections (connection, openedAt, closedAt, closedBy), lines (connection, direction,
# messageType, messageId, correlationId, sessionGeneration, acceptedMessageType, dropped) and drops.
function Get-L2RealTraffic([object]$Proxy) { return $Proxy.Snapshot().body.traffic }

# The lines of one connection in order, as 'onboard->server:SessionHello' and the like. A dropped line is marked.
function Get-L2RealConnectionSequence([object]$Traffic, [int]$ConnectionNumber) {
    return , @(@($Traffic.lines) | Where-Object { [int]$_.connection -eq $ConnectionNumber } | ForEach-Object {
            "$($_.direction):$($_.messageType)$(if ($_.dropped) { '(dropped)' })"
        })
}

function Format-L2RealConnections([object]$Traffic) {
    return (@(@($Traffic.connections) | ForEach-Object {
                "#$($_.connection): $(if ($null -eq $_.closedAt) { 'open' } else { $_.closedBy })"
            }) -join '; ')
}

Export-ModuleMember -Function @(
    'Test-L2RealPresent', 'ConvertTo-L2RealInstant', 'Get-L2RealScalar', 'Get-L2RealCount',
    'Get-L2RealInbound', 'Get-L2RealOutbound', 'Get-L2RealProgress', 'Get-L2RealSession', 'Format-L2RealSession',
    'Get-L2RealSlotReading', 'Format-L2RealSlots', 'Add-L2RealNotReached', 'Wait-L2RealOrLast',
    'Wait-L2RealButtonOffered', 'Invoke-L2RealDialogButton', 'Invoke-L2RealConfirmedButton', 'Confirm-L2RealNotice',
    'Move-L2RealVehicleTo', 'Wait-L2RealIntent', 'Get-L2RealStage', 'Start-L2RealLoad',
    'Get-L2RealTraffic', 'Get-L2RealConnectionSequence', 'Format-L2RealConnections')
