#Requires -Version 7

<#
Shared drivers and reads for batch 6's two journey G3 scenarios (control-server#164): g3-task-type-admission-fail-closed
(FP-IS-10) and g3-reversed-direction-journey (FP-IS-11).

A new file rather than more of L2RealOnboard.psm1 or the orchestrator, for the reason L2RealOnboard.psm1 gives: batch-6
tickets add L2 helpers in parallel, and control-server#159 owns Invoke-L2Scenario.ps1's defaults this batch.

A scenario imports it next to the orchestrator's copy of L2.psm1:

    Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2TaskTypeJourney.psm1') -Force

What is new here is one read off the HMI that is a business fact rather than a precondition: the two elements
onboard-hmi#115 put next to the stop line, AutomationId StopDirection (取货 or 卸货, from the worklist item's stopRole or
the current leg's legType) and AutomationId TaskType (the worklist item's workType as text, empty without an item).
Both are TextBlocks, so UI Automation names them by their text. They are read by AutomationId and judged by what the
text says, never compared against a caption in full, so rewording a caption does not turn G3 red.

Everything else a scenario concludes still comes from the server's SQLite store, the slots simulator's snapshot and the
fake RIoT's snapshot.
#>

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'L2.psm1')
Import-Module (Join-Path $PSScriptRoot 'L2RealOnboard.psm1')

# --- the HMI's stop line ---------------------------------------------------------------------------------------------

<#
What the stop line shows now: Direction and TaskType as their text, '' when an element shows nothing and $null when it is
not in the automation tree at all (an HMI without onboard-hmi#115). The element is looked up again on every read: the
main window re-renders the line whenever a snapshot is applied, and a cached element would read a detached peer.
#>
function Get-L2StopFacts([object]$Onboard) {
    $read = {
        param([string]$AutomationId)
        $element = $Onboard.Element('AutomationId', $AutomationId)
        if (-not $element) { return $null }
        try { return [string]$element.Current.Name }
        catch [System.Windows.Automation.ElementNotAvailableException] { return $null }
    }
    return [pscustomobject]@{
        Direction = & $read 'StopDirection'
        TaskType  = & $read 'TaskType'
    }
}

function Format-L2StopFacts([object]$Facts) {
    if ($null -eq $Facts) { return '(not read)' }
    $show = { param($v) if ($null -eq $v) { '(absent)' } elseif ($v -eq '') { '(empty)' } else { "「$v」" } }
    return "StopDirection=$(& $show $Facts.Direction) TaskType=$(& $show $Facts.TaskType)"
}

<#
Waits until the stop line satisfies $Until and returns what it showed; on timeout returns the last reading instead of
throwing, so the reading goes into the criteria table (a snapshot is applied and rendered asynchronously, and "the HMI
never showed it" is the finding, not a runner error). Every reading is journalled under $Criterion.
#>
function Wait-L2StopFacts {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][string]$Criterion,
        [Parameter(Mandatory)][scriptblock]$Until,
        [int]$TimeoutSeconds = 30
    )
    $onboard = $Context.Onboard
    $facts = Wait-L2RealOrLast -Description $Description -Criterion $Criterion -Journal $Context.Journal `
        -TimeoutSeconds $TimeoutSeconds -Probe { Get-L2StopFacts $onboard }.GetNewClosure() -Until $Until
    $Context.Journal.Note("Stop line at ${Criterion}: $(Format-L2StopFacts $facts)")
    return $facts
}

# --- the server's journey snapshots ---------------------------------------------------------------------------------

<#
The plan and worklist snapshots the server queued for one demand, in the order it created them. A plan belongs to the
demand when one of its legs names it, a worklist when one of its items does; a snapshot of another demand's journey is
never counted. Each carries its legs or items as the wire has them.
#>
function Get-L2DemandJourneySnapshots([object]$Connection, [string]$DemandId) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT MessageId, MessageType, PayloadJson, CreatedAt, AcknowledgedAt, FencedAt FROM ProtocolOutbox " +
        "WHERE MessageType IN ('UpcomingStopPlanSnapshot', 'CurrentStopWorklistSnapshot') ORDER BY CreatedAt, MessageId")
    $mine = foreach ($row in $rows) {
        $payload = ([string]$row.PayloadJson | ConvertFrom-Json -DateKind String).payload
        $isPlan = [string]$row.MessageType -eq 'UpcomingStopPlanSnapshot'
        $legs = @(if ($isPlan) { @($payload.legs) | Sort-Object { [int]$_.sequence } })
        $items = @(if (-not $isPlan) { @($payload.items) })
        $owners = if ($isPlan) { @($legs | ForEach-Object { [string]$_.demandId }) } else { @($items | ForEach-Object { [string]$_.demandId }) }
        if ($owners -notcontains $DemandId) { continue }
        [pscustomobject]@{
            MessageId    = [string]$row.MessageId
            Type         = [string]$row.MessageType
            At           = ConvertTo-L2RealInstant $row.CreatedAt
            Revision     = if ($isPlan) { [long]$payload.planRevision } else { [long]$payload.worklistRevision }
            StationId    = if ($isPlan) { $null } else { [string]$payload.stationId }
            Legs         = $legs
            Items        = $items
            Acknowledged = Test-L2RealPresent $row.AcknowledgedAt
            Fenced       = Test-L2RealPresent $row.FencedAt
        }
    }
    return , @($mine)
}

# 'TO_PICKUP@station:STATE,TO_DROPOFF@station:STATE' for a plan; 'PICKUP/WORK_TYPE@station' for a worklist.
function Format-L2JourneySnapshot([object]$Snapshot) {
    if ($Snapshot.Type -eq 'UpcomingStopPlanSnapshot') {
        return "plan r$($Snapshot.Revision) " + (@($Snapshot.Legs | ForEach-Object { "$($_.legType)@$($_.stationId):$($_.state)" }) -join ',')
    }
    return "worklist r$($Snapshot.Revision) @$($Snapshot.StationId) " +
        (@($Snapshot.Items | ForEach-Object { "$($_.stopRole)/$($_.workType)" }) -join ',')
}

# --- driving one journey on the real rig ----------------------------------------------------------------------------

<#
One station operation, driven the way real-onboard-normal-load drives it: wait for the server's command and for the
onboard's own WAITING_OPERATOR (sent only after the lock feedback has been stable, so the door really is open), note the
door, run $WhileWaiting (a read that must see the stop as the operator does), place or take the cargo in the simulator
and close the door. One slot per operation: a multi-slot command fails here rather than hanging on a later criterion.
#>
function Invoke-L2TaskTypeStationOperation {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$DemandId,
        [Parameter(Mandatory)][string]$OperationType,
        [Parameter(Mandatory)][string]$CargoState,
        [scriptblock]$WhileWaiting
    )
    $journal = $Context.Journal
    $connection = $Context.Connection
    $simulator = $Context.Simulator
    $attemptId = Wait-L2Condition -Description "the server issued the $OperationType command" `
        -Journal $journal -Criterion "$OperationType-attempt" -TimeoutSeconds 180 `
        -Probe { Get-L2RealScalar $connection "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$DemandId' AND OperationType = '$OperationType'" }.GetNewClosure() `
        -Until { param($v) $v }
    $targetSlots = @([string](Get-L2RealScalar $connection "SELECT TargetSlotsJson AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'") |
        ConvertFrom-Json | ForEach-Object { [int]$_ })
    $waiting = Wait-L2Condition -Description "the onboard is waiting for the operator on the $OperationType slot" `
        -Journal $journal -Criterion "$OperationType-waiting-operator" -TimeoutSeconds 120 `
        -Probe { @((Get-L2RealProgress $connection $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })[0] }.GetNewClosure() `
        -Until { param($v) $null -ne $v }
    if ($waiting.Active.Count -ne 1) {
        throw "The onboard reported WAITING_OPERATOR on $($waiting.Active.Count) slots at once for the $OperationType; these scenarios drive one slot."
    }
    $slot = [int]$waiting.Active[0]
    $door = [string](@($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $slot })[0].doorState)
    $whileWaitingResult = if ($null -ne $WhileWaiting) { & $WhileWaiting } else { $null }
    $journal.Note("$OperationType $attemptId targets slots $(Format-L2RealSlots $targetSlots); slot $slot is open (door $door), setting cargo to $CargoState.")
    $null = $simulator.Command('Put', "slots/$slot/cargo", @{ state = $CargoState })
    $null = $simulator.Command('Post', "slots/$slot/close-door", @{})
    return [pscustomobject]@{
        AttemptId      = $attemptId
        TargetSlots    = $targetSlots
        OpenedSlot     = $slot
        DoorWhenWaited = $door
        WhileWaiting   = $whileWaitingResult
    }
}

<#
One demand from "accepted" to "completed" on the real rig, with the HMI's stop line read at the points a scenario judges
it at. The demand must already be published. $OriginRiotId and $DestinationRiotId are where the vehicle is sent: the
scenario says where the route should run, and the fake RIoT puts the vehicle wherever it is told -- whether the server
planned that stop is what the scenario then asserts, from the server's own snapshots and orders.

The second leg's order is found as "the demand's other confirmed intent", not by purpose name, so a reversed journey is
driven by the same code whatever the server calls that intent.

Returns every reading and every id the scenario needs: StopFacts (en-route-to-origin, at-origin, at-destination,
completed), Load, Unload, OriginIntent, DestinationIntent, Stage, and the instants the vehicle was put at each stop.
#>
function Invoke-L2TaskTypeJourney {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$DemandId,
        [Parameter(Mandatory)][string]$Sublot,
        [Parameter(Mandatory)][int]$OriginRiotId,
        [Parameter(Mandatory)][int]$DestinationRiotId
    )
    $journal = $Context.Journal
    $connection = $Context.Connection
    $onboard = $Context.Onboard
    $readings = [ordered]@{}

    $null = Wait-L2Condition -Description "demand $DemandId was accepted and dispatched to its first stop" `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { Get-L2RealStage $connection $DemandId }.GetNewClosure() -Until { param($v) $v -eq 'AwaitingPickupArrival' }
    $originIntent = Wait-L2RealIntent $Context $DemandId 'TO_PICKUP'
    $null = Wait-L2Condition -Description 'the onboard acknowledged the plan sent before the first arrival' `
        -Journal $journal -Criterion 'dispatch-plan-acknowledged' -TimeoutSeconds 60 `
        -Probe { @((Get-L2DemandJourneySnapshots $connection $DemandId) | Where-Object { $_.Type -eq 'UpcomingStopPlanSnapshot' -and $_.Acknowledged }).Count }.GetNewClosure() `
        -Until { param($v) $v -ge 1 }
    # With only the plan applied the line shows the current leg's direction; that it shows one at all is what makes an
    # empty task type here a reading of a rendered plan rather than of a window that has not caught up.
    $readings['en-route-to-origin'] = Wait-L2StopFacts -Context $Context -Criterion 'stop-line:en-route-to-origin' `
        -Description 'the HMI shows the planned direction before the first arrival' -Until { param($f) $f.Direction -ne '' -and $null -ne $f.Direction }

    Move-L2RealVehicleTo $Context $originIntent $OriginRiotId "the first stop ($OriginRiotId)"
    $originArrivedAt = [DateTimeOffset]::UtcNow
    $null = Wait-L2Condition -Description 'the server adopted the arrival and asks for the sublot' `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { Get-L2RealStage $connection $DemandId }.GetNewClosure() -Until { param($v) $v -eq 'AwaitingSublot' }
    $null = Wait-L2Condition -Description 'the onboard acknowledged the worklist at the first stop' `
        -Journal $journal -Criterion 'origin-worklist-acknowledged' -TimeoutSeconds 60 `
        -Probe { @((Get-L2DemandJourneySnapshots $connection $DemandId) | Where-Object { $_.Type -eq 'CurrentStopWorklistSnapshot' -and $_.Acknowledged }).Count }.GetNewClosure() `
        -Until { param($v) $v -ge 1 }
    $readings['at-origin'] = Wait-L2StopFacts -Context $Context -Criterion 'stop-line:at-origin' `
        -Description 'the HMI shows the first stop''s direction and task type' `
        -Until { param($f) (Test-L2RealPresent $f.Direction) -and (Test-L2RealPresent $f.TaskType) }

    $null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
        -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
        -Probe { $onboard.CanSubmit() }.GetNewClosure() -Until { param($v) $v }
    $journal.Note("Typing sublot $Sublot into ScanTextBox through UI Automation.")
    $onboard.SetSublot($Sublot)
    $null = Wait-L2Condition -Description 'the manual submit button became enabled' `
        -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
        -Probe { $onboard.SubmitReady() }.GetNewClosure() -Until { param($v) $v }
    $onboard.Submit()
    $journal.Note('Manual submit invoked.')

    $load = Invoke-L2TaskTypeStationOperation -Context $Context -DemandId $DemandId -OperationType 'Load' -CargoState 'OCCUPIED'
    $null = Wait-L2Condition -Description 'the load committed and the journey left for its second stop' `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
        -Probe { Get-L2RealStage $connection $DemandId }.GetNewClosure() -Until { param($v) $v -eq 'AwaitingGateArrival' }
    $destinationIntent = Wait-L2Condition -Description 'the second leg''s intent was confirmed' `
        -Journal $journal -Criterion 'destination-intent' -TimeoutSeconds 120 `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection -Sql (
                "SELECT UpperId, OrderId, Status, Purpose FROM OrderIntents WHERE DemandId = '$DemandId' AND Purpose <> 'TO_PICKUP'")
            if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
        }.GetNewClosure() -Until { param($v) $null -ne $v }

    $destinationDepartedAt = [DateTimeOffset]::UtcNow
    Move-L2RealVehicleTo $Context $destinationIntent $DestinationRiotId "the second stop ($DestinationRiotId)"
    $destinationArrivedAt = [DateTimeOffset]::UtcNow
    # Read while the onboard waits for the operator at the second stop, before the door closes: that is the stop line the
    # operator unloads by, and once the unload commits the server moves the worklist on.
    $originDirection = $readings['at-origin'].Direction
    $unload = Invoke-L2TaskTypeStationOperation -Context $Context -DemandId $DemandId -OperationType 'Unload' -CargoState 'EMPTY' `
        -WhileWaiting {
            Wait-L2StopFacts -Context $Context -Criterion 'stop-line:at-destination' `
                -Description 'the HMI shows the second stop''s direction and task type' `
                -Until { param($f) (Test-L2RealPresent $f.Direction) -and (Test-L2RealPresent $f.TaskType) -and $f.Direction -ne $originDirection }.GetNewClosure()
        }.GetNewClosure()
    $readings['at-destination'] = $unload.WhileWaiting

    $stage = Wait-L2RealOrLast -Description 'the journey completed' -Criterion 'journey-stage' -Journal $journal `
        -TimeoutSeconds 180 -Probe { Get-L2RealStage $connection $DemandId }.GetNewClosure() -Until { param($v) $v -eq 'Completed' }
    $readings['completed'] = Get-L2StopFacts $onboard
    $journal.Note("Stop line at stop-line:completed: $(Format-L2StopFacts $readings['completed'])")

    return [pscustomobject]@{
        StopFacts         = $readings
        Load              = $load
        Unload            = $unload
        OriginIntent      = $originIntent
        DestinationIntent = $destinationIntent
        Stage             = $stage
        # When the fake RIoT put the vehicle at each stop, on this machine's clock -- the one the server stamps its
        # rows with. What happened between two of these happened at that stop.
        OriginArrivedAt       = $originArrivedAt
        DestinationDepartedAt = $destinationDepartedAt
        DestinationArrivedAt  = $destinationArrivedAt
    }
}

Export-ModuleMember -Function Get-L2StopFacts, Format-L2StopFacts, Wait-L2StopFacts,
    Get-L2DemandJourneySnapshots, Format-L2JourneySnapshot,
    Invoke-L2TaskTypeStationOperation, Invoke-L2TaskTypeJourney
