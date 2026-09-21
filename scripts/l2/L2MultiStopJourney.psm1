#Requires -Version 7

<#
Reads for batch 7's two multi-demand real-rig scenarios (control-server#218): g3-multi-stop-plan (FP-IS-08, the journey
G3 runner) and real-onboard-mixed-side-one-stop (the real-rig L2, in no runner).

A new file, as L2TaskTypeJourney.psm1 was for batch 6: that module drives one demand from pickup to gate and control-server
#203 owns it, and every function here is about more than one demand on one journey. Only reads and one sampler live here;
the driving is in scenarios/MultiStopRigCommon.ps1, dot-sourced, so its probes need no closures (see that file).

A scenario imports it next to the orchestrator's copy of L2.psm1:

    Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2MultiStopJourney.psm1') -Force

What is read off the HMI (onboard-hmi#134, PR #143):

- the plan legs list, AutomationId JourneyPlanLegs. Each row's Grid carries AutomationProperties.ItemStatus
  'sequence|stopPurposeCategory|state', and **that value is not in the UI Automation tree**: a Grid has no automation peer,
  so the row is represented by the ItemsControl's DataItem, whose ItemStatus is empty. Measured with the same XAML shape
  on 2026-09-22 (evidence/b7-15/uia-probe/). So a row is read by its first TextBlock, which shows the leg's sequence
  (JourneyPlanLegRow.SequenceText), and by the DataItem's Name, which UIA takes from the row record's ToString and which
  therefore carries 'ItemStatus = ...' as text. The order of the rows is the order the HMI renders them in, which is
  what NEVER_REORDER_LEGS_LOCALLY is about. docs/defects/20260922-journey-plan-legs-item-status-not-in-uia-tree.md.
- the worklist rows, AutomationId WorklistItems: a ListItem per item whose first TextBlock is the sublot, and one
  TextBlock with AutomationId WorklistItemSide whose ItemStatus (readable -- a TextBlock has a peer) is FRONT, REAR, BOTH,
  UNKNOWN or UNASSIGNED.
- the loading-phase lines CargoHoldingCountdown and LoadingClosedReason, TextBlocks with a readable ItemStatus. Collapsed
  lines are not in the tree at all, so "absent" reads as $null.

Every element is looked up again on every read: the lists are rebuilt whenever a snapshot is applied, and a cached element
would read a detached peer.
#>

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'L2.psm1')
Import-Module (Join-Path $PSScriptRoot 'L2RealOnboard.psm1')

function Get-L2UiaChildren([object]$Element) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $children = [System.Collections.Generic.List[object]]::new()
    $child = $walker.GetFirstChild($Element)
    while ($null -ne $child) {
        $children.Add($child)
        $child = $walker.GetNextSibling($child)
    }
    return , $children.ToArray()
}

function Get-L2UiaTexts([object]$Element) {
    $isText = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    return , @($Element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $isText) | ForEach-Object { [string]$_.Current.Name })
}

<#
The plan legs as the HMI shows them, in the order it shows them: one object per row with Sequence (the int in the row's
first TextBlock, $null when that is not a number), ItemStatus (parsed out of the DataItem's Name, $null when absent) and
Texts. $null when the list is not in the tree at all -- an HMI without onboard-hmi#134, or a window that is gone.

Returns a single-layer array wrapped on the way out, so an empty list stays an array: assign it, never wrap the call in @().
#>
function Get-L2PlanLegRows([object]$Onboard) {
    try {
        $list = $Onboard.Element('AutomationId', 'JourneyPlanLegs')
        if (-not $list) { return $null }
        $rows = foreach ($item in (Get-L2UiaChildren $list)) {
            $texts = Get-L2UiaTexts $item
            $sequence = $null
            if ($texts.Count -ge 1 -and $texts[0] -match '^\s*(\d+)\s*$') { $sequence = [int]$Matches[1] }
            $name = [string]$item.Current.Name
            $status = if ($name -match 'ItemStatus = ([^,}\s]+)') { $Matches[1] } else { $null }
            [pscustomobject]@{ Sequence = $sequence; ItemStatus = $status; Texts = $texts }
        }
        return , @($rows)
    } catch [System.Windows.Automation.ElementNotAvailableException] {
        return $null
    }
}

function Format-L2PlanLegRows([object]$Rows) {
    if ($null -eq $Rows) { return '(list absent)' }
    if (@($Rows).Count -eq 0) { return '(no rows)' }
    return (@($Rows) | ForEach-Object {
            $seq = if ($null -eq $_.Sequence) { '?' } else { $_.Sequence }
            $status = if ($null -eq $_.ItemStatus) { '' } else { "[$($_.ItemStatus)]" }
            "$seq$status"
        }) -join ','
}

<#
The worklist rows as the HMI shows them: Sublot (the row's first TextBlock) and Side (WorklistItemSide's ItemStatus), in
the order shown. $null when the list is not in the tree. Wrapped like Get-L2PlanLegRows.
#>
function Get-L2WorklistRows([object]$Onboard) {
    try {
        $list = $Onboard.Element('AutomationId', 'WorklistItems')
        if (-not $list) { return $null }
        $isSide = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'WorklistItemSide')
        $rows = foreach ($item in (Get-L2UiaChildren $list)) {
            $texts = Get-L2UiaTexts $item
            $side = $item.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $isSide)
            [pscustomobject]@{
                Sublot = if ($texts.Count -ge 1) { $texts[0] } else { $null }
                Side   = if ($side) { [string]$side.Current.ItemStatus } else { $null }
            }
        }
        return , @($rows)
    } catch [System.Windows.Automation.ElementNotAvailableException] {
        return $null
    }
}

function Format-L2WorklistRows([object]$Rows) {
    if ($null -eq $Rows) { return '(list absent)' }
    if (@($Rows).Count -eq 0) { return '(no rows)' }
    return (@($Rows) | ForEach-Object { "$($_.Sublot)/$($_.Side)" }) -join ','
}

# The ItemStatus of one of the loading-phase lines (CargoHoldingCountdown, LoadingClosedReason); $null when collapsed.
function Get-L2LoadingPhaseLine([object]$Onboard, [string]$AutomationId) {
    try {
        $line = $Onboard.Element('AutomationId', $AutomationId)
        if (-not $line) { return $null }
        return [string]$line.Current.ItemStatus
    } catch [System.Windows.Automation.ElementNotAvailableException] {
        return $null
    }
}

<#
Every plan and worklist snapshot the server queued that names one of $DemandIds, in the order it created them. Unlike
L2TaskTypeJourney.psm1's Get-L2DemandJourneySnapshots, the legs stay in the order the payload carries them: that order
is what ORDER_LEGS_BY_SEQUENCE is about, and a reader that sorts would make the criterion true by construction.
WireSequences is that order's sequence numbers; Legs is the same legs sorted by sequence, for everything else.

A plan names its journey's anchor demand on every leg (JourneyPlanBuilder), so the plans of a multi-demand journey are
found through the first demand; worklists name each item's own demand. Wrapped like the readers above.
#>
function Get-L2JourneyWireSnapshots([object]$Connection, [string[]]$DemandIds) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT MessageId, MessageType, PayloadJson, CreatedAt, AcknowledgedAt, FencedAt FROM ProtocolOutbox " +
        "WHERE MessageType IN ('UpcomingStopPlanSnapshot', 'CurrentStopWorklistSnapshot') ORDER BY CreatedAt, MessageId")
    $mine = foreach ($row in $rows) {
        $payload = ([string]$row.PayloadJson | ConvertFrom-Json -DateKind String).payload
        $isPlan = [string]$row.MessageType -eq 'UpcomingStopPlanSnapshot'
        $wireLegs = @(if ($isPlan) { @($payload.legs) })
        $items = @(if (-not $isPlan) { @($payload.items) })
        $owners = if ($isPlan) { @($wireLegs | ForEach-Object { [string]$_.demandId }) } else { @($items | ForEach-Object { [string]$_.demandId }) }
        if (@($owners | Where-Object { $_ -in $DemandIds }).Count -eq 0) { continue }
        [pscustomobject]@{
            MessageId     = [string]$row.MessageId
            Type          = [string]$row.MessageType
            At            = ConvertTo-L2RealInstant $row.CreatedAt
            Revision      = if ($isPlan) { [long]$payload.planRevision } else { [long]$payload.worklistRevision }
            StationId     = if ($isPlan) { $null } else { [string]$payload.stationId }
            WireSequences = @($wireLegs | ForEach-Object { $_.sequence })
            Legs          = @($wireLegs | Sort-Object { [int]$_.sequence })
            Items         = $items
            Acknowledged  = Test-L2RealPresent $row.AcknowledgedAt
            Fenced        = Test-L2RealPresent $row.FencedAt
        }
    }
    return , @($mine)
}

# 'plan r5 1:TO_PICKUP@N1-3_N1-7:ACTIVE:BUSINESS,...' or 'worklist r7 @站 sublot/PICKUP,...'
function Format-L2WireSnapshot([object]$Snapshot) {
    if ($Snapshot.Type -eq 'UpcomingStopPlanSnapshot') {
        return "plan r$($Snapshot.Revision) wire[$(@($Snapshot.WireSequences) -join ',')] " +
            (@($Snapshot.Legs | ForEach-Object { "$($_.sequence):$($_.legType)@$($_.stationId):$($_.state):$($_.stopPurposeCategory)" }) -join ',')
    }
    return "worklist r$($Snapshot.Revision) @$($Snapshot.StationId) " + (@($Snapshot.Items | ForEach-Object { "$($_.sublot)/$($_.stopRole)" }) -join ',')
}

<#
Samples the simulator snapshot every 100 ms on its own thread and emits a record each time the set of slots that are not
closed-and-locked changes: door open, lock feedback other than locked, or the unlock output still on. The same sampler as
real-onboard-cancellation-authorization-lost's L2-CAL-10 (that one is local to its scenario, so it is repeated here
rather than imported). The scenario thread spends seconds inside single waits and cannot sample itself. The job stops
itself after $Minutes in case the scenario never reaches Stop-L2DoorSampler.
#>
function Start-L2DoorSampler([object]$Simulator, [int]$Minutes = 20) {
    $uri = "$($Simulator.BaseUrl)/$($Simulator.Prefix)/snapshot"
    return Start-ThreadJob -ArgumentList $uri, $Minutes -ScriptBlock {
        param($uri, $minutes)
        $deadline = [DateTimeOffset]::UtcNow.AddMinutes($minutes)
        $last = $null
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            try {
                $snapshot = Invoke-RestMethod -Uri $uri -TimeoutSec 5
                $notLocked = @($snapshot.slots | Where-Object {
                        $_.doorState -ne 'CLOSED' -or [int]$_.lockFeedbackRaw -ne 1 -or [int]$_.unlockOutputRaw -ne 0
                    } | ForEach-Object { [int]$_.slotNo } | Sort-Object)
                $key = $notLocked -join ','
                if ($key -ne $last) {
                    [pscustomobject]@{ At = [DateTimeOffset]::UtcNow.ToString('o'); Slots = $notLocked }
                    $last = $key
                }
            } catch {
                [pscustomobject]@{ At = [DateTimeOffset]::UtcNow.ToString('o'); Error = $_.Exception.Message }
            }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Stop-L2DoorSampler([object]$Job) {
    Stop-Job $Job
    $changes = @(Receive-Job $Job)
    Remove-Job $Job -Force
    return , $changes
}

Export-ModuleMember -Function Get-L2PlanLegRows, Format-L2PlanLegRows, Get-L2WorklistRows, Format-L2WorklistRows,
    Get-L2LoadingPhaseLine, Get-L2JourneyWireSnapshots, Format-L2WireSnapshot, Start-L2DoorSampler, Stop-L2DoorSampler
