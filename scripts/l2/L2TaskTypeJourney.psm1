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
    # 真有第二条意图时抛错，不悄悄取第一条：那说明这条需求的意图比这个驱动设想的多，往下跑出来的每一条
    # 判据都会是关于「碰巧排在前面的那一条」的，而判据表上看不出这件事。排序的理由见 Get-L2SecondLegIntents。
    #
    # **这个检查绝不能写进 -Probe。**`Wait-L2Condition` 的轮询是 `try { $last = & $Probe } catch { $last = $null }`
    # （`L2.psm1:24`），探针里抛出的异常会被整个吞掉——检查写在里面不是不生效，是退化成 120 秒后的
    # 「等待第二腿意图确认超时」。那句话读起来像服务端没确认意图，真因一个字都到不了读证据的人手里，
    # 而查清它要再烧一次真装置机时。那个 catch 是所有场景共用的、必要的瞬时错误容错，不能为这里改。
    #
    # 等到了要查，等不到也要查：只有排在后面那条被确认时，探针一直返回 $null，走的是超时那条路径。
    $assertOneSecondLeg = {
        param($rows)
        if (@($rows).Count -le 1) { return }
        throw ("Demand $DemandId has $(@($rows).Count) non-TO_PICKUP order intents " +
            "($(@($rows | ForEach-Object { "$($_.Purpose)/$($_.UpperId)" }) -join ', ')); " +
            'this driver assumes one second leg and would silently judge whichever sorted first.')
    }.GetNewClosure()
    try {
        $destinationIntent = Wait-L2Condition -Description 'the second leg''s intent was confirmed' `
            -Journal $journal -Criterion 'destination-intent' -TimeoutSeconds 120 `
            -Probe {
                $rows = Get-L2SecondLegIntents -Connection $connection -DemandId $DemandId
                if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
            }.GetNewClosure() -Until { param($v) $null -ne $v }
    } catch {
        & $assertOneSecondLeg (Get-L2SecondLegIntents -Connection $connection -DemandId $DemandId)
        throw
    }
    & $assertOneSecondLeg (Get-L2SecondLegIntents -Connection $connection -DemandId $DemandId)

    $destinationDepartedAt = [DateTimeOffset]::UtcNow
    Move-L2RealVehicleTo $Context $destinationIntent $DestinationRiotId "the second stop ($DestinationRiotId)"
    $destinationArrivedAt = [DateTimeOffset]::UtcNow
    # Read while the onboard waits for the operator at the second stop, before the door closes: that is the stop line the
    # operator unloads by, and once the unload commits the server moves the worklist on.
    $originDirection = $readings['at-origin'].Direction
    # 先等这一站的清单被车载端确认，再读它的停靠行——与第一站（上面 origin-worklist-acknowledged）同形。
    # 停靠行显示的是车载端**应用了的那份清单**，所以「清单已确认」是这条读数的前提而不是它的结论；
    # 不等就读，读到的可能是上一站的行或一行还没更新的旧值，而判据分辨不出这两种情况
    # （control-server#164 审查；README 第 14 条）。第二站的清单是第二份，所以门槛是 >= 2。
    $worklistsAtDestination = 2
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
            $null = Wait-L2Condition -Description 'the onboard acknowledged the worklist at the second stop' `
                -Journal $journal -Criterion 'destination-worklist-acknowledged' -TimeoutSeconds 60 `
                -Probe { @((Get-L2DemandJourneySnapshots $connection $DemandId) | Where-Object { $_.Type -eq 'CurrentStopWorklistSnapshot' -and $_.Acknowledged }).Count } `
                -Until { param($v) $v -ge $worklistsAtDestination }
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
    Get-L2DemandJourneySnapshots, Format-L2JourneySnapshot, Get-L2SecondLegIntents,
    Invoke-L2TaskTypeStationOperation, Invoke-L2TaskTypeJourney
