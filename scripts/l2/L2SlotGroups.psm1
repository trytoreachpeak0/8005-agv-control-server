#Requires -Version 7

<#
Batch 4's slot-group reads and assertions for L2 scenarios (control-server#71).

A new file rather than more of L2.psm1, for the reason L2Change.psm1 gives: batches 4 and 5 add shared L2
helpers in parallel, and each taking a file of its own keeps them out of each other's module. A scenario that
wants it imports it next to the orchestrator's copy of L2.psm1:

    Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SlotGroups.psm1') -Force

Everything here reads the server's own database, the way the rest of the rig does. In particular a vehicle's
slot groups come from SlotModelSlots through the record the server resolves that vehicle's model from, never
from a range written into a script: "slots 1-4 are FRONT" is a property of today's approved model, and a
scenario that assumed it would keep passing on the day a vehicle is bound to a different one.
#>

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'L2.psm1')

function ConvertTo-L2SqlLiteral([string]$Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

<#
Which slot model a vehicle is bound to, and the group of each of its slots: physical slot number to SlotPosition.

Resolved in the order VehicleSlotModelResolver uses (control-server#66), so a scenario reads the grouping
dispatch reads: the vehicle's active slot configuration if it has one; otherwise the model its newest published
IO binding references; otherwise nothing. A newest binding tied across two models is unresolved there too, and
returns $null here rather than a guess. Returns $null when unresolved, else an object with AgvId,
SlotModelVersionId, Source (ActiveSlotConfiguration or LatestPublishedIoBinding) and Positions, a sorted
dictionary of slot number to SlotPosition.
#>
function Get-L2VehicleSlotPositions {
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][string]$AgvId
    )

    $agv = ConvertTo-L2SqlLiteral $AgvId
    $modelId = $null
    $source = $null
    $active = Invoke-L2Query -Connection $Connection `
        -Sql "SELECT SlotModelVersionId FROM ActiveSlotConfigurations WHERE AgvId = $agv"
    if ($active.Count -gt 0) {
        $modelId = [string]$active[0].SlotModelVersionId
        $source = 'ActiveSlotConfiguration'
    } else {
        $bindings = Invoke-L2Query -Connection $Connection `
            -Sql "SELECT SlotModelVersionId, CreatedAt FROM SlotIoBindings WHERE AgvId = $agv AND Status = 'PUBLISHED'"
        if ($bindings.Count -eq 0) { return $null }
        # Compared as instants, not as the stored text: the server stores DateTimeOffset with its offset, and
        # two spellings of one instant must tie the way they tie in VehicleSlotModelResolver.
        $stamped = @($bindings | ForEach-Object {
                [pscustomobject]@{ Model = [string]$_.SlotModelVersionId; At = [DateTimeOffset]::Parse([string]$_.CreatedAt, [Globalization.CultureInfo]::InvariantCulture) } })
        # Sorted as longs: Measure-Object's maximum is a double, and ticks are past the range a double holds exactly.
        $newest = $stamped | ForEach-Object { $_.At.UtcTicks } | Sort-Object -Descending | Select-Object -First 1
        $models = @($stamped | Where-Object { $_.At.UtcTicks -eq $newest } | ForEach-Object { $_.Model } |
            Sort-Object -Unique -CaseSensitive)
        if ($models.Count -ne 1) { return $null }
        $modelId = $models[0]
        $source = 'LatestPublishedIoBinding'
    }

    $slots = Invoke-L2Query -Connection $Connection -Sql @"
SELECT PhysicalSlotNumber, SlotPosition FROM SlotModelSlots
WHERE SlotModelVersionId = $(ConvertTo-L2SqlLiteral $modelId)
ORDER BY PhysicalSlotNumber
"@
    if ($slots.Count -eq 0) { return $null }
    # Keyed by slot number and sorted. Not [ordered]@{}: an int indexer on an OrderedDictionary is a position.
    $positions = [System.Collections.Generic.SortedDictionary[int, string]]::new()
    foreach ($slot in $slots) { $positions[[int]$slot.PhysicalSlotNumber] = [string]$slot.SlotPosition }
    return [pscustomobject]@{
        AgvId              = $AgvId
        SlotModelVersionId = $modelId
        Source             = $source
        Positions          = $positions
    }
}

<#
The slots the server counts available for a vehicle's current session, ascending.

Read off the two handshake snapshots the server stored for that session generation -- the facts dispatch judges
availability from -- with the same rule JourneyRuntimeEngine.SlotAvailable applies: the CapabilitySnapshot says
OPERABLE, ENABLED, EMPTY, LOCKED and RESET, and the SafetyStateSnapshot agrees on EMPTY, LOCKED and RESET.
Returns an empty array when the vehicle has no session or either snapshot is missing.
#>
function Get-L2AvailableSlots {
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][string]$AgvId
    )

    $session = Invoke-L2Query -Connection $Connection `
        -Sql "SELECT SessionGeneration FROM SessionRecoveries WHERE AgvId = $(ConvertTo-L2SqlLiteral $AgvId)"
    if ($session.Count -eq 0) { return , @() }
    $generation = [long]$session[0].SessionGeneration

    $latest = @{}
    $rows = Invoke-L2Query -Connection $Connection -Sql @"
SELECT MessageType, ReceivedAt, RequestJson FROM ProtocolInbox
WHERE MessageType IN ('CapabilitySnapshot', 'SafetyStateSnapshot')
"@
    foreach ($row in $rows) {
        $envelope = [string]$row.RequestJson | ConvertFrom-Json
        if ([string]$envelope.agvId -cne $AgvId -or [long]$envelope.sessionGeneration -ne $generation) { continue }
        $at = [DateTimeOffset]::Parse([string]$row.ReceivedAt, [Globalization.CultureInfo]::InvariantCulture)
        $type = [string]$row.MessageType
        if (-not $latest.ContainsKey($type) -or $latest[$type].At -lt $at) {
            $latest[$type] = [pscustomobject]@{ At = $at; Payload = $envelope.payload }
        }
    }
    if (-not $latest.ContainsKey('CapabilitySnapshot') -or -not $latest.ContainsKey('SafetyStateSnapshot')) {
        return , @()
    }

    $safety = @{}
    foreach ($slot in $latest['SafetyStateSnapshot'].Payload.slotStates) { $safety[[int]$slot.slotNo] = $slot }
    $available = @($latest['CapabilitySnapshot'].Payload.slotStates | Where-Object {
            $other = $safety[[int]$_.slotNo]
            $null -ne $other -and
            $_.operability -ceq 'OPERABLE' -and $_.administrativeAvailability -ceq 'ENABLED' -and
            $_.physicalState -ceq 'EMPTY' -and $_.lockState -ceq 'LOCKED' -and $_.unlockOutputState -ceq 'RESET' -and
            $other.physicalState -ceq 'EMPTY' -and $other.lockState -ceq 'LOCKED' -and $other.unlockOutputState -ceq 'RESET'
        } | ForEach-Object { [int]$_.slotNo } | Sort-Object)
    return , $available
}

<#
The judgement behind Assert-L2SlotGroupTargets, with no database: do these target slots all belong to the group,
come in ascending order, and are they exactly the lowest-numbered N available slots of that group (N being how
many targets there are)?

Returns Passed, Expected (the slots that would have satisfied it, or $null when the group does not have N
available slots), Actual, and Reason naming the first thing that failed. Kept separate so the rule itself can be
shown to pass and to fail on constructed input (Test-L2SlotGroups.ps1).
#>
function Test-L2SlotGroupTargets {
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][int[]]$TargetSlots,
        # Slot number to SlotPosition, as Get-L2VehicleSlotPositions returns in Positions.
        [Parameter(Mandatory)][System.Collections.IDictionary]$SlotPositions,
        [Parameter(Mandatory)][string]$SlotPosition,
        [Parameter(Mandatory)][AllowEmptyCollection()][int[]]$AvailableSlots
    )

    $actual = @($TargetSlots)
    $groupAvailable = @($AvailableSlots | Sort-Object -Unique | Where-Object {
            $SlotPositions.ContainsKey($_) -and [string]$SlotPositions[$_] -ceq $SlotPosition })
    $expected = if ($actual.Count -gt 0 -and $groupAvailable.Count -ge $actual.Count) {
        @($groupAvailable | Select-Object -First $actual.Count)
    } else { $null }

    $reason = $null
    $outside = @($actual | Where-Object { -not $SlotPositions.ContainsKey($_) -or [string]$SlotPositions[$_] -cne $SlotPosition })
    if ($actual.Count -eq 0) {
        $reason = 'there are no target slots'
    } elseif ($outside.Count -gt 0) {
        $reason = "slot(s) $($outside -join ', ') are not in group $SlotPosition"
    } elseif ((@($actual | Sort-Object) -join ',') -ne ($actual -join ',') -or
              @($actual | Sort-Object -Unique).Count -ne $actual.Count) {
        $reason = 'the target slots are not strictly ascending'
    } elseif ($null -eq $expected) {
        $reason = "group $SlotPosition has only $($groupAvailable.Count) available slot(s) for $($actual.Count) target(s)"
    } elseif (($expected -join ',') -ne ($actual -join ',')) {
        $reason = "they are not the lowest-numbered available slots of group $SlotPosition"
    }

    return [pscustomobject]@{
        Passed   = ($null -eq $reason)
        Expected = $expected
        Actual   = $actual
        Reason   = $reason
    }
}

<#
Adds one assertion: the demand's journey targets slots that all belong to SlotPosition, ascending, and are
exactly the lowest-numbered N available slots of that group on the vehicle it was dispatched to.

Target slots come from JourneyRuntimes.TargetSlotsJson, the vehicle's groups from Get-L2VehicleSlotPositions, and
the available slots from Get-L2AvailableSlots unless the scenario passes -AvailableSlots itself -- which it should
whenever availability can have changed since dispatch, since the snapshot this reads is the session's current
one. Returns the Test-L2SlotGroupTargets result.
#>
function Assert-L2SlotGroupTargets {
    param(
        [Parameter(Mandatory)][object]$Assertions,
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][string]$DemandId,
        [Parameter(Mandatory)][string]$SlotPosition,
        [int[]]$AvailableSlots,
        [string]$Description
    )

    $journey = Invoke-L2Query -Connection $Connection `
        -Sql "SELECT AgvId, TargetSlotsJson FROM JourneyRuntimes WHERE DemandId = $(ConvertTo-L2SqlLiteral $DemandId)"
    if ($journey.Count -eq 0) {
        $Assertions.Add($Id, ($Description ?? "需求 $DemandId 的目标仓位属于 $SlotPosition 组"), $false,
            "a journey for $DemandId", '(no journey row)')
        return [pscustomobject]@{ Passed = $false; Expected = $null; Actual = @(); Reason = 'no journey row' }
    }
    $agvId = [string]$journey[0].AgvId
    $targets = @([string]$journey[0].TargetSlotsJson | ConvertFrom-Json | ForEach-Object { [int]$_ })
    $positions = Get-L2VehicleSlotPositions -Connection $Connection -AgvId $agvId
    $available = if ($PSBoundParameters.ContainsKey('AvailableSlots')) { @($AvailableSlots) }
                 else { Get-L2AvailableSlots -Connection $Connection -AgvId $agvId }

    $result = if ($null -eq $positions) {
        [pscustomobject]@{ Passed = $false; Expected = $null; Actual = $targets; Reason = "vehicle $agvId has no resolvable slot model" }
    } else {
        Test-L2SlotGroupTargets -TargetSlots $targets -SlotPositions $positions.Positions `
            -SlotPosition $SlotPosition -AvailableSlots $available
    }
    $expectedText = if ($null -ne $result.Expected) { "[$($result.Expected -join ',')]（$SlotPosition 组最小的可用仓，升序）" }
                    else { "$SlotPosition 组内升序的最小可用仓" }
    $actualText = "[$($targets -join ',')] on $agvId" + $(if ($result.Reason) { "：$($result.Reason)" } else { '' })
    $Assertions.Add($Id,
        ($Description ?? "需求 $DemandId 的目标仓位全部属于 $SlotPosition 组、升序，且恰好是该组编号最小的 $($targets.Count) 个可用仓"),
        [bool]$result.Passed, $expectedText, $actualText)
    return $result
}

<#
The demand's current StructuralDispatchBlocks rows, one per reason code, uncleared ones only unless -IncludeCleared.
Returns an array, empty when there is none.
#>
function Get-L2StructuralDispatchBlock {
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][string]$DemandId,
        [switch]$IncludeCleared
    )

    $filter = if ($IncludeCleared) { '' } else { ' AND ClearedAt IS NULL' }
    $rows = Invoke-L2Query -Connection $Connection `
        -Sql "SELECT * FROM StructuralDispatchBlocks WHERE DemandId = $(ConvertTo-L2SqlLiteral $DemandId)$filter ORDER BY ReasonCode"
    return , $rows
}

<#
The demand's JourneyBacklog row -- the reason code of its latest dispatch verdict -- or $null when it has none.
#>
function Get-L2JourneyBacklogRow {
    param(
        [Parameter(Mandatory)][object]$Connection,
        [Parameter(Mandatory)][string]$DemandId
    )

    $rows = Invoke-L2Query -Connection $Connection `
        -Sql "SELECT * FROM JourneyBacklog WHERE DemandId = $(ConvertTo-L2SqlLiteral $DemandId)"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

Export-ModuleMember -Function Get-L2VehicleSlotPositions, Get-L2AvailableSlots, Test-L2SlotGroupTargets,
    Assert-L2SlotGroupTargets, Get-L2StructuralDispatchBlock, Get-L2JourneyBacklogRow
