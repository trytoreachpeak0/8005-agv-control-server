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
<#
.SYNOPSIS
Every non-TO_PICKUP order intent of a demand, ordered by UpperId.

.DESCRIPTION
ORDER BY UpperId because SQLite promises no row order and the caller takes the first row: without it
the same database and the same demand could answer differently on two runs (control-server#164
review). One journey carries one second leg today, so the ordering changes no result now -- what it
guards is the day that premise stops holding.

Returns a single-layer array, wrapped on the way out so that a one-row or empty answer stays an
array. Do not wrap it again at the call site.
#>
function Get-L2SecondLegIntents {
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][string]$DemandId)

    return , @(Invoke-L2Query -Connection $Connection -Sql (
        "SELECT UpperId, OrderId, Status, Purpose FROM OrderIntents " +
        "WHERE DemandId = '$DemandId' AND Purpose <> 'TO_PICKUP' ORDER BY UpperId"))
}

<#
.SYNOPSIS
Waits for the demand's one second-leg intent to be confirmed, and refuses to guess when there is more than one.

.DESCRIPTION
Its own function rather than a block inside the journey driver, so that Test-L2SecondLegIntentWait.ps1
can drive THIS code with a stubbed Invoke-L2Query. A structural copy in a test would be a different
thing than what runs -- measured, when the first attempt at that copy put the probe outside the module
and so had no cross-module resolution left to get wrong, and came out green on code that was broken.

Three things here are load-bearing, and all three are about the same property of Wait-L2Condition: its
poll is `try { $last = & $Probe } catch { $last = $null }` (`L2.psm1:24`), a deliberate and necessary
tolerance for a transient read, which flattens every failure inside a probe into one symptom -- a
timeout. So a probe may contain nothing that fails silently:

1. The count check is OUTSIDE the wait. Thrown from inside the probe it is not merely ineffective; it
   becomes "timed out waiting for the second leg's intent", which reads as the server never confirming
   one. Finding out otherwise costs a real-rig round.
2. It is checked on BOTH exits. When only the later-sorting intent is confirmed the probe answers
   $null for the full timeout, so the failure path has to check too.
3. The failure path's own read may not replace the failure. A server that is gone is one of the
   reasons the wait timed out, and then that read throws -- taking with it the only message that says
   what was being waited for. Only the count check itself may replace it, because that message says
   more than the timeout does.
#>
function Wait-L2SecondLegIntent {
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][string]$DemandId,
        [int]$TimeoutSeconds = 120,
        [object]$Journal)

    $assertOneSecondLeg = {
        param($rows)
        if (@($rows).Count -le 1) { return }
        throw ("Demand $DemandId has $(@($rows).Count) non-TO_PICKUP order intents " +
            "($(@($rows | ForEach-Object { "$($_.Purpose)/$($_.UpperId)" }) -join ', ')); " +
            'this driver assumes one second leg and would silently judge whichever sorted first.')
    }.GetNewClosure()
    try {
        $destinationIntent = Wait-L2Condition -Description 'the second leg''s intent was confirmed' `
            -Journal $Journal -Criterion 'destination-intent' -TimeoutSeconds $TimeoutSeconds `
            -Probe {
                $rows = Get-L2SecondLegIntents -Connection $Connection -DemandId $DemandId
                if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
            }.GetNewClosure() -Until { param($v) $null -ne $v }
    } catch {
        $rowsAfterFailure = $null
        try { $rowsAfterFailure = Get-L2SecondLegIntents -Connection $Connection -DemandId $DemandId } catch { }
        if ($null -ne $rowsAfterFailure) { & $assertOneSecondLeg $rowsAfterFailure }
        throw
    }
    & $assertOneSecondLeg (Get-L2SecondLegIntents -Connection $Connection -DemandId $DemandId)
    return $destinationIntent
}

<#
.SYNOPSIS
Waits until an acknowledged worklist exists for a station OTHER than the one just finished.

.DESCRIPTION
This replaces a count. control-server#204 wrote the second stop's wait as "at least 2 acknowledged
CurrentStopWorklistSnapshot", which is not the thing it needs to know -- it is a proxy that happens to
equal it while every stop emits exactly one worklist.

Revise the FIRST stop's worklist and the proxy breaks in the one direction that matters: the count
reaches 2 before the second stop's worklist is acknowledged, the wait passes at its first poll, and the
stop line is then read while the onboard may still be showing the previous stop. The safety net fails
in the only situation it exists for. (It has never yet fired at all -- in both scenarios the callback
that runs it only triggers after the server has sent the operation command, by which time the worklist
is long acknowledged. So this is not a fix for an observed red; it is removing a premise nobody was
carrying. control-server#265.)

Two properties are load-bearing, both for reasons already paid for in this file:

1. The empty-PreviousStationId check is OUTSIDE the wait, and it throws rather than degrading. With an
   empty previous station, "a station other than the previous one" matches EVERY acknowledged worklist
   and the wait passes at the first poll -- the same vacuous-criterion shape this function exists to
   remove, in a new disguise. It cannot live inside the probe: Wait-L2Condition's poll is
   `try { $last = & $Probe } catch { $last = $null }` (L2.psm1:24), so a throw in there becomes a
   timeout whose message names the wrong thing.
2. The probe returns the station id rather than a boolean, so the journal records WHICH station
   satisfied it. `destination-worklist-acknowledged` used to record a count, which cannot distinguish
   "the second stop's worklist arrived" from "the first stop's worklist was revised".
#>
function Wait-L2WorklistAcknowledgedAtOtherStation {
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][string]$DemandId,
        # The station whose worklist was already acknowledged -- the one this wait must NOT accept.
        [Parameter(Mandatory)][AllowEmptyString()][string]$PreviousStationId,
        [Parameter(Mandatory)][string]$Criterion,
        [Parameter(Mandatory)][string]$Description,
        [int]$TimeoutSeconds = 60,
        [object]$Journal)

    if (-not (Test-L2RealPresent $PreviousStationId)) {
        throw ("Cannot wait for a worklist at a station other than '$PreviousStationId': the previous " +
            'station id is empty, so every acknowledged worklist would satisfy the criterion and the ' +
            'wait would pass at its first poll. Whoever read that id got nothing back -- fix that read ' +
            'rather than this wait (control-server#265).')
    }
    return Wait-L2Condition -Description $Description -Journal $Journal -Criterion $Criterion `
        -TimeoutSeconds $TimeoutSeconds -Probe {
            # The parentheses around the call are load-bearing. Get-L2DemandJourneySnapshots returns
            # `, @($mine)`, a single-layer array, and the three ways of piping it are NOT equivalent:
            # `f | Where` and `@(f) | Where` both hand Where-Object ONE object that happens to be the
            # whole list, while `(f) | Where` unrolls it. Measured. Dropping them here read two
            # worklists as one row whose StationId was 'STATION-A STATION-B'.
            $other = @((Get-L2DemandJourneySnapshots $Connection $DemandId) | Where-Object {
                $_.Type -eq 'CurrentStopWorklistSnapshot' -and $_.Acknowledged -and
                (Test-L2RealPresent $_.StationId) -and $_.StationId -ne $PreviousStationId })
            if ($other.Count -ge 1) { [string]$other[0].StationId } else { $null }
        }.GetNewClosure() -Until { param($v) $null -ne $v }
}

function Invoke-L2TaskTypeJourney {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$DemandId,
        [Parameter(Mandatory)][string]$Sublot,
        [Parameter(Mandatory)][int]$OriginRiotId,
        [Parameter(Mandatory)][int]$DestinationRiotId,
        # Run once the first plan is acknowledged, before the vehicle moves: judgments about the route the server
        # planned belong here, so they are written even when a server that planned another route leaves the rest of
        # the drive to time out.
        [scriptblock]$BeforeFirstArrival
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
    if ($null -ne $BeforeFirstArrival) { & $BeforeFirstArrival $originIntent }

    Move-L2RealVehicleTo $Context $originIntent $OriginRiotId "the first stop ($OriginRiotId)"
    $originArrivedAt = [DateTimeOffset]::UtcNow
    $null = Wait-L2Condition -Description 'the server adopted the arrival and asks for the sublot' `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { Get-L2RealStage $connection $DemandId }.GetNewClosure() -Until { param($v) $v -eq 'AwaitingSublot' }
    # Yields the station id rather than a count, because the second stop's wait below has to know WHICH
    # station was just finished, and reading it here costs no extra query. One consequence is deliberate:
    # this wait now also requires that id to be PRESENT, so an empty one times out here instead of
    # passing. That is the trade we want -- an empty id would make the second stop's criterion match
    # every acknowledged worklist, which is the failure control-server#265 exists to remove.
    $originWorklistStation = Wait-L2Condition -Description 'the onboard acknowledged the worklist at the first stop' `
        -Journal $journal -Criterion 'origin-worklist-acknowledged' -TimeoutSeconds 60 `
        -Probe {
            # Parentheses around the call, see Wait-L2WorklistAcknowledgedAtOtherStation: without them
            # the whole list arrives as one object and every station id collapses into one string.
            $acknowledged = @((Get-L2DemandJourneySnapshots $connection $DemandId) | Where-Object {
                $_.Type -eq 'CurrentStopWorklistSnapshot' -and $_.Acknowledged -and (Test-L2RealPresent $_.StationId) })
            if ($acknowledged.Count -ge 1) { [string]$acknowledged[0].StationId } else { $null }
        }.GetNewClosure() -Until { param($v) $null -ne $v }
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
    # 真有第二条意图时抛错，不悄悄取第一条：那说明这条需求的意图比这个驱动设想的多，往下跑出来的每一条
    # 判据都会是关于「碰巧排在前面的那一条」的，而判据表上看不出这件事。排序的理由见 Get-L2SecondLegIntents。
    #
    $destinationIntent = Wait-L2SecondLegIntent -Connection $connection -DemandId $DemandId -Journal $journal

    $destinationDepartedAt = [DateTimeOffset]::UtcNow
    Move-L2RealVehicleTo $Context $destinationIntent $DestinationRiotId "the second stop ($DestinationRiotId)"
    $destinationArrivedAt = [DateTimeOffset]::UtcNow
    # Read while the onboard waits for the operator at the second stop, before the door closes: that is the stop line the
    # operator unloads by, and once the unload commits the server moves the worklist on.
    $originDirection = $readings['at-origin'].Direction
    # 先等这一站的清单被车载端确认，再读它的停靠行——与第一站（上面 origin-worklist-acknowledged）同形。
    # 停靠行显示的是车载端**应用了的那份清单**，所以「清单已确认」是这条读数的前提而不是它的结论；
    # 不等就读，读到的可能是上一站的行或一行还没更新的旧值，而判据分辨不出这两种情况
    # （control-server#164 审查；README 第 14 条）。
    #
    # **判据是「站点不同」，不是「条数够了」**（control-server#265）。原来写的是「已确认的清单 >= 2 份」，
    # 那是个代理指标：它等于「第二站的清单已确认」，靠的是「每站恰好一份清单」这个场景层面的巧合。
    # 第一站的清单一旦改版，条数会在第二站确认之前就满到 2，等待第一次探测就通过——**保险在它唯一
    # 该生效的场合失效**。改用站点比较，判的就是那件事本身。
    $unload = Invoke-L2TaskTypeStationOperation -Context $Context -DemandId $DemandId -OperationType 'Unload' -CargoState 'EMPTY' `
        -WhileWaiting {
            # 这两个 scriptblock 都**不能**再 `.GetNewClosure()`。它们写在一个已经是闭包的块里，而在闭包
            # 内部调用 `GetNewClosure()` 捕获到的是一个空作用域——实测：外层闭包读得到 $DemandId，
            # 它里面再取一次闭包就读成空。后果不是值不对，是**判据必然红**：探针拿空需求号去查，永远数到
            # 0 份已确认的清单，60 秒超时（实跑 rig-01，见 evidence/l2/cs203-rig-01-nested-closure-red/）。
            # 不取闭包才是对的：scriptblock 记住定义它的作用域，那正是这个回调的作用域，隔着
            # Wait-L2Condition、Wait-L2StopFacts 两层函数调用也读得到。
            #
            # 下面那条 `-ne $originDirection` 原来也带 `.GetNewClosure()`，**同一个毛病，在本票之前就有**：
            # $originDirection 读成 $null，于是那一项等于「Direction 非空」，而同一条判据前面已经判过非空了。
            # 换句话说「第二站的方向与第一站不同」这半句**一直没有判别力**，一并修掉。
            $null = Wait-L2WorklistAcknowledgedAtOtherStation -Connection $connection -DemandId $DemandId `
                -PreviousStationId $originWorklistStation -Criterion 'destination-worklist-acknowledged' `
                -Description 'the onboard acknowledged the worklist at the second stop' -Journal $journal
            Wait-L2StopFacts -Context $Context -Criterion 'stop-line:at-destination' `
                -Description 'the HMI shows the second stop''s direction and task type' `
                -Until { param($f) (Test-L2RealPresent $f.Direction) -and (Test-L2RealPresent $f.TaskType) -and $f.Direction -ne $originDirection }
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

# Get-L2SecondLegIntents is exported although only this module calls it, and that is not tidiness --
# it is what makes it callable at all. A scriptblock that has been through .GetNewClosure() resolves
# command names against the global table, not against the module it was written in, so a closure here
# cannot see an unexported function of this same file. It does not say so: the call throws, and
# Wait-L2Condition's poll swallows every probe exception (`L2.psm1:24`), so the whole thing surfaces
# 120 seconds later as "timed out waiting for the second leg's intent" -- on the rig, where that reads
# as a product fault. Measured, not reasoned: scripts/l2/Test-L2ProbeClosureResolvable.ps1 holds the
# counterexample and fails this repository's CI if any probe closure calls an unexported sibling.
Export-ModuleMember -Function Get-L2StopFacts, Format-L2StopFacts, Wait-L2StopFacts,
    Get-L2DemandJourneySnapshots, Format-L2JourneySnapshot, Get-L2SecondLegIntents, Wait-L2SecondLegIntent,
    Wait-L2WorklistAcknowledgedAtOtherStation,
    Invoke-L2TaskTypeStationOperation, Invoke-L2TaskTypeJourney
