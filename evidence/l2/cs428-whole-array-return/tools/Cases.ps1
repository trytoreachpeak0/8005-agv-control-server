#Requires -Version 7
<#
The cases Test-CriteriaOffline.ps1 replays: one per read control-server#428 changed.

    File      the scenario file the read is cut out of (old form from the base commit, new form from the tree)
    Lines     the read's line range(s) in that file; line numbers are the same in both forms, every edit kept its line
    Functions functions of that file to define first, by name
    Probe     a line inside the Wait-L2Condition probe to cut out instead; the probe is what is observed
    Observe   the criterion, verbatim from the file (refused if it is not there) -- or a call, with ObserveIsCall
    Setup     what the scenario's variables held at that point, read from the same database with Query (never injected)
    Inject    regex: only a query matching it is doubled/emptied (default: every query of the case)
    Stage     a synthetic scenario whose kept database to use      | one of the two
    Snapshot  an evidence snapshots directory to rebuild one from  |

Where a case uses ANOTHER scenario's database it says so in Note: the read is the same SQL against the same table,
and what is being measured is the shape of the read, not that scenario's outcome.
#>
$g3 = 'evidence/g3/cs390-journey-fa4a5ce3/scenarios'
$scenarios = 'scripts/l2/scenarios'

$cases = [System.Collections.Generic.List[hashtable]]::new()

# ---------------------------------------------------------------------------- "exactly N rows" criteria

$cases.Add(@{
        Id = 'L2-CPM-00'; Kind = 'count'; File = "$scenarios/charging-policy-missing-vehicle-not-commissioned.ps1"
        Stage = 'charging-policy-missing-vehicle-not-commissioned'; Lines = @(57, 61)
        Setup = '$coveredVehicleKey = [string](Query "SELECT VehicleKey AS V FROM ChargingPolicyVehicleScopes")[0].V'
        Observe = '$policyRows.Count -eq 1 -and [string]$policyRows[0].VehicleKey -eq $coveredVehicleKey -and [string]$policyRows[0].Source -eq ''L2_PRESET'''
    })
$cases.Add(@{
        Id = 'L2-MCT-00'; Kind = 'count'; File = "$scenarios/mandatory-charge-vehicle-takes-no-transport.ps1"
        Stage = 'mandatory-charge-vehicle-takes-no-transport'; Lines = @(92, 95)
        Setup = '$version = [string](Query "SELECT Version AS V FROM ChargingPolicyActivations ORDER BY Sequence DESC LIMIT 1")[0].V'
        Observe = '$active.Count -eq 1 -and [string]$active[0].Version -eq $version -and
        [int]$active[0].Entry -eq 30 -and [int]$active[0].Margin -eq 20 -and [int]$active[0].Estimate -eq 0'
    })
$cases.Add(@{
        Id = 'L2-AAU-04'; Kind = 'count'; File = "$scenarios/area-assignment-unmapped-silent.ps1"
        Stage = 'area-assignment-unmapped-silent'; Lines = @(159, 167)
        Setup = @'
$mapped = [pscustomobject]@{ Id = [string](Query "SELECT ConsumerId AS V FROM ConfigurationConsumerBindings WHERE ConsumerKind = 'TransportDemand' AND ObjectKind = 'DispatchZoneAreaAssignment'")[0].V }
$current = [pscustomobject]@{ version = (Query "SELECT MAX(Version) AS V FROM DispatchZoneAreaAssignmentVersions")[0].V }
$zone = [string](Query "SELECT DispatchZone AS V FROM JourneyRuntimes WHERE DemandId = '$($mapped.Id)'")[0].V
'@
        Observe = '$freeze.Count -eq 1 -and
        [long]$freeze[0].FrozenVersion -eq [long]$freeze[0].CurrentVersion -and
        [long]$freeze[0].FrozenVersion -eq [long]$current.version -and
        [string]$freeze[0].FrozenSnapshotId -eq [string]$freeze[0].VersionSnapshotId -and
        [string]$freeze[0].DispatchZone -eq $zone'
    })
$cases.Add(@{
        Id = 'L2-SRJ-10'; Kind = 'count'; File = "$scenarios/sublot-rejected-after-entry.ps1"
        Stage = 'sublot-rejected-after-entry'; Lines = @(336, 337)
        Setup = @'
$demandId = [string](Query "SELECT DemandId AS V FROM StationOperations WHERE OperationType = 'Load'")[0].V
$sublot = [string](Query "SELECT SublotId AS V FROM StationOperations WHERE OperationType = 'Load'")[0].V
'@
        Observe = '$attempts.Count -eq 1 -and [string]$attempts[0].SublotId -eq $sublot'
    })
$cases.Add(@{
        Id = 'G3-02-47 results'; Kind = 'count'; File = "$scenarios/g3-sublot-rejected.ps1"
        Snapshot = "$g3/g3-sublot-rejected/snapshots"; Lines = @(391, 391)
        Setup = '$attemptId = [string](Query "SELECT SlotOperationAttemptId AS V FROM OperationResults")[0].V'
        Observe = '$results.Count -eq 1 -and [string]$results[0].OverallOutcome -eq ''COMPLETED'''
    })
$cases.Add(@{
        Id = 'G3-02-35 workflow'; Kind = 'count'; File = "$scenarios/g3-load-cancellation-before-load.ps1"
        Snapshot = "$g3/g3-load-cancellation-before-load/snapshots"; Lines = @(256, 257); Functions = 'Test-Present'
        Setup = '$cancellationId = [string](Query "SELECT WorkflowId AS V FROM RecoveryWorkflows")[0].V'
        Observe = '$workflow.Count -eq 1 -and [string]$workflow[0].WorkflowType -eq ''LOAD_CANCELLATION'' -and
        -not (Test-Present $workflow[0].SlotOperationAttemptId) -and [string]$workflow[0].SlotsJson -eq ''[]'''
    })
$cases.Add(@{
        Id = 'G3-02-35 lease'; Kind = 'count'; File = "$scenarios/g3-load-cancellation-before-load.ps1"
        Snapshot = "$g3/g3-load-cancellation-before-load/snapshots"; Lines = @(258, 259); Functions = @('Test-Present', 'ConvertTo-Instant')
        Setup = '$demandId = [string](Query "SELECT DemandId AS V FROM JourneyDemands")[0].V'
        Observe = '$null -ne $releasedAt'
        Note = 'The query ends in LIMIT 1, so a second row cannot come from the database; doubled is the wrapper''s.'
    })
$cases.Add(@{
        Id = 'G3-02-36 entryRequest'; Kind = 'count'; File = "$scenarios/g3-load-cancellation-before-load.ps1"
        Stage = 'load-cancelled-before-sublot'; Lines = @(278, 279); Functions = 'Test-Present'
        Setup = '$waiting = [pscustomobject]@{ SublotRequestMessageId = [string](Query "SELECT MessageId AS V FROM ProtocolOutbox WHERE MessageType = ''SublotEntryRequested'' AND AcknowledgedAt IS NOT NULL")[0].V }'
        Observe = '$entryRequest.Count -eq 1 -and (Test-Present $entryRequest[0].AcknowledgedAt)'
        Note = 'ProtocolOutbox is not in the evidence snapshots; the database is the synthetic twin load-cancelled-before-sublot. MessageId is the primary key, so a second row cannot come from the database.'
    })

# ---------------------------------------------------------------------------- diagnostic text built on a count

$cases.Add(@{
        Id = 'G3-02-45 pendingEntry (actual text)'; Kind = 'text'; File = "$scenarios/g3-sublot-rejected.ps1"
        Stage = 'sublot-rejected-after-entry'; Lines = @(319, 320)
        Setup = '$waiting = [pscustomobject]@{ SublotRequestMessageId = [string](Query "SELECT MessageId AS V FROM ProtocolOutbox WHERE MessageType = ''SublotEntryRequested''")[0].V }'
        Observe = '$(if ($pendingEntry.Count -eq 1) { $pendingEntry[0].AcknowledgedAt } else { ''(no row)'' })'
        Note = 'ProtocolOutbox is not in the evidence snapshots; the database is the synthetic twin sublot-rejected-after-entry. Not part of the verdict of G3-02-45, only of its actual text.'
    })
$cases.Add(@{
        Id = 'G3RecoveryCommon backlogText'; Kind = 'text'; File = "$scenarios/G3RecoveryCommon.ps1"
        Snapshot = "$g3/g3-load-cancellation-before-load/snapshots"; Lines = @(370, 373); Functions = 'Test-G3Present'
        Setup = '$nextDemandId = [string](Query "SELECT DemandId AS V FROM JourneyBacklog")[0].V'
        Observe = '$backlogText'
    })
$cases.Add(@{
        Id = 'L2-UE-04/08 footprint'; Kind = 'text'; File = "$scenarios/real-onboard-unload-not-emptied.ps1"
        Stage = 'sublot-rejected-after-entry'
        Lines = @(@(62, 62), @(116, 116)); Functions = @('Get-Count', 'Get-RecoveryFootprint'); Inject = 'FROM SessionRecoveries'
        Setup = @'
$Context = [pscustomobject]@{ AgvId = [string](Query "SELECT AgvId AS V FROM SessionRecoveries")[0].V }
$demandId = [string](Query "SELECT DemandId AS V FROM AcceptedDemands")[0].V
'@
        Observe = '$footprint -eq $noRecovery'
        Note = 'Database of the synthetic sublot-rejected-after-entry (a run with no recovery): the evidence snapshots of this scenario write an empty table as an empty file, so its two empty recovery tables cannot be rebuilt. A ">= 1 row, the first one Ready" read, so doubled stays True by design; what changes is empty: a verdict instead of a throw.'
    })
$cases.Add(@{
        Id = 'G3-12-04 Format-Plan (text)'; Kind = 'text'; File = "$scenarios/g3-waiting-point-idle-return.ps1"
        Snapshot = "$g3/g3-waiting-point-idle-return/snapshots"; Functions = @('Get-PlanLegs', 'Format-Plan'); Site = 98
        Setup = @'
$plan = [pscustomobject]@{ Acknowledged = $true; Payload = [pscustomobject]@{ planRevision = 3; legs = @(
    [pscustomobject]@{ sequence = 2; stopPurposeCategory = 'BUSINESS'; stationId = 'GATE'; state = 'PLANNED' }
    [pscustomobject]@{ sequence = 1; stopPurposeCategory = 'BUSINESS'; stationId = 'N1-3'; state = 'ARRIVED' }) } }
'@
        Observe = 'Format-Plan $plan'; ObserveIsCall = $true
        Note = 'No query: the plan is a two-leg object built here. The old form prints [1 2:BUSINESS BUSINESS@...], which is what control-server#390 saw in G3-12-04.'
    })
$cases.Add(@{
        Id = 'real-onboard-mixed-side-one-stop:174 (text)'; Kind = 'text'; File = "$scenarios/real-onboard-mixed-side-one-stop.ps1"
        Snapshot = "$g3/g3-multi-stop-plan/snapshots"; Functions = 'Get-AcknowledgedWorklists'; Site = 174
        Setup = @'
$mixedStationName = 'N1-3'; $demandIds = @('d')
function Get-L2JourneyWireSnapshots { return , @(
    [pscustomobject]@{ Type = 'CurrentStopWorklistSnapshot'; Acknowledged = $true; StationId = 'N1-3'; Revision = 1 }
    [pscustomobject]@{ Type = 'CurrentStopWorklistSnapshot'; Acknowledged = $true; StationId = 'N1-3'; Revision = 2 }) }
function Format-L2WireSnapshot($snapshot) { return "r$($snapshot.Revision)" }
'@
        Observe = "((Get-AcknowledgedWorklists `$mixedStationName) | ForEach-Object { Format-L2WireSnapshot `$_ }) -join ' | '"
        ObserveOld = "(@(Get-AcknowledgedWorklists `$mixedStationName) | ForEach-Object { Format-L2WireSnapshot `$_ }) -join ' | '"
        Note = 'No query: Get-L2JourneyWireSnapshots is replaced by two acknowledged worklists with the module''s return shape.'
    })

# ---------------------------------------------------------------------------- (...)[0], then a property read

$cases.Add(@{
        Id = 'g3-sublot-rejected dispatched'; Kind = 'index'; File = "$scenarios/g3-sublot-rejected.ps1"
        Snapshot = "$g3/g3-sublot-rejected/snapshots"; Lines = @(194, 195)
        Setup = '$demandId = [string](Query "SELECT DemandId AS V FROM JourneyRuntimes")[0].V'
        Observe = '[int]$dispatched.ExpectedBasketCount'
    })
$cases.Add(@{
        Id = 'G3-02-43 refusedRuntime'; Kind = 'index'; File = "$scenarios/g3-sublot-rejected.ps1"
        Snapshot = "$g3/g3-sublot-rejected/snapshots"; Lines = @(293, 294)
        Setup = '$demandId = [string](Query "SELECT DemandId AS V FROM JourneyRuntimes")[0].V'
        Observe = '[string]$refusedRuntime.Stage'
        Note = 'The snapshot is the end of the run, so the stage read here is the final one, not AwaitingSublot.'
    })
$cases.Add(@{
        Id = 'G3-02-47 finalRuntime'; Kind = 'index'; File = "$scenarios/g3-sublot-rejected.ps1"
        Snapshot = "$g3/g3-sublot-rejected/snapshots"; Lines = @(395, 395); Functions = 'Test-Present'
        Setup = '$demandId = [string](Query "SELECT DemandId AS V FROM JourneyRuntimes")[0].V'
        Observe = '-not (Test-Present $finalRuntime.BlockReasonCode)'
    })
$cases.Add(@{
        Id = 'L2-TTAB-01 freeze'; Kind = 'index'; File = "$scenarios/task-type-binding-admits-bound-station.ps1"
        Stage = 'task-type-binding-admits-bound-station'; Lines = @(115, 126)
        Setup = '$demand = [pscustomobject]@{ Id = [string](Query "SELECT DemandId AS V FROM FrozenDemandStations WHERE Role = ''Dropoff''")[0].V }; $boundStation = 220'
        Observe = '[int]$freeze.DropoffStationId -eq $boundStation'
    })
$cases.Add(@{
        Id = 'stop-ended bStopRow'; Kind = 'index'; File = "$scenarios/stop-ended-journey-continues.ps1"
        Stage = 'stop-ended-journey-continues'; Lines = @(92, 93)
        Setup = '$bStop = [pscustomobject]@{ StopId = [string](Query "SELECT StopId AS V FROM JourneyStops WHERE StationRiotId = 11")[0].V }'
        Observe = '[string]$bStopRow[0].StationId'
    })
$cases.Add(@{
        Id = 'stop-ended gateStopRow'; Kind = 'index'; File = "$scenarios/stop-ended-journey-continues.ps1"
        Stage = 'stop-ended-journey-continues'; Lines = @(196, 197)
        Setup = @'
$Context = [pscustomobject]@{ GateStationRiotId = 210 }
$journeyId = [string](Query "SELECT JourneyId AS V FROM JourneyStops WHERE StationRiotId = 210")[0].V
'@
        Observe = '[string]$gateStopRow[0].StationId'
    })

# ---------------------------------------------------------------------------- first row, or $null

$backlogDemand = '$demandId = [string](Query "SELECT DemandId AS V FROM JourneyBacklog")[0].V'
$runtimeDemand = '$demandId = [string](Query "SELECT DemandId AS V FROM JourneyRuntimes")[0].V'
$intentDemand = '$demandId = [string](Query "SELECT DemandId AS V FROM OrderIntents WHERE Purpose = ''TO_PICKUP''")[0].V; $purpose = ''TO_PICKUP'''
foreach ($name in 'area-assignment-unmapped-silent', 'area-eqp-unique-across-task-types', 'task-type-binding-admits-bound-station', 'task-type-binding-missing-not-cascading') {
    $cases.Add(@{
            Id = "$name Get-Backlog"; Kind = 'first-row'; File = "$scenarios/$name.ps1"; Stage = 'mandatory-charge-vehicle-takes-no-transport'
            Functions = 'Get-Backlog'; Setup = $backlogDemand; Observe = 'Get-Backlog $demandId'; ObserveIsCall = $true; Site = 'Get-Backlog'
            Note = 'Database of mandatory-charge-vehicle-takes-no-transport, the scenario that ends with a backlog row; same SQL.'
        })
}
foreach ($name in 'area-eqp-unique-across-task-types', 'task-type-binding-admits-bound-station', 'task-type-binding-missing-not-cascading') {
    $cases.Add(@{
            Id = "$name Get-Stage"; Kind = 'first-row'; File = "$scenarios/$name.ps1"; Stage = $name
            Functions = 'Get-Stage'; Setup = $runtimeDemand; Observe = 'Get-Stage $demandId'; ObserveIsCall = $true; Site = 'Get-Stage'
        })
    $cases.Add(@{
            Id = "$name Get-Intent"; Kind = 'first-row'; File = "$scenarios/$name.ps1"; Stage = $name
            Functions = 'Get-Intent'; Setup = $intentDemand; Observe = 'Get-Intent $demandId $purpose'; ObserveIsCall = $true; Site = 'Get-Intent'
        })
}
$cases.Add(@{
        Id = 'mandatory-charge Get-Journey'; Kind = 'first-row'; File = "$scenarios/mandatory-charge-vehicle-takes-no-transport.ps1"
        Stage = 'mandatory-charge-vehicle-takes-no-transport'; Functions = 'Get-Journey'; Setup = $runtimeDemand
        Observe = 'Get-Journey'; ObserveIsCall = $true; Site = 'Get-Journey'
    })
$cases.Add(@{
        Id = 'charging-thresholds Get-ActiveVersion'; Kind = 'first-row'; File = "$scenarios/charging-thresholds-relation-refused.ps1"
        Stage = 'charging-thresholds-relation-refused'; Functions = 'Get-ActiveVersion'
        Observe = 'Get-ActiveVersion'; ObserveIsCall = $true; Site = 'Get-ActiveVersion'
    })
$cases.Add(@{
        Id = 'area-assignment-unmapped-silent probe journey-stage'; Kind = 'first-row'; File = "$scenarios/area-assignment-unmapped-silent.ps1"
        Stage = 'area-assignment-unmapped-silent'; Probe = 154
        Setup = '$mapped = [pscustomobject]@{ Id = [string](Query "SELECT DemandId AS V FROM JourneyRuntimes")[0].V }'
    })
$cases.Add(@{
        Id = 'charging-policy-missing probe journey-accepted'; Kind = 'first-row'; File = "$scenarios/charging-policy-missing-vehicle-not-commissioned.ps1"
        Stage = 'charging-policy-missing-vehicle-not-commissioned'; Probe = 88; Setup = $runtimeDemand
    })
$cases.Add(@{
        Id = 'charging-policy-missing probe pickup-confirmed'; Kind = 'first-row'; File = "$scenarios/charging-policy-missing-vehicle-not-commissioned.ps1"
        Stage = 'charging-policy-missing-vehicle-not-commissioned'; Probe = 119
        Setup = '$journey = [pscustomobject]@{ PickupUpperId = [string](Query "SELECT PickupUpperId AS V FROM JourneyRuntimes")[0].V }'
    })
$cases.Add(@{
        Id = 'charging-thresholds probe dispatched'; Kind = 'first-row'; File = "$scenarios/charging-thresholds-relation-refused.ps1"
        Stage = 'charging-thresholds-relation-refused'; Probe = 108; Setup = $runtimeDemand
    })
$cases.Add(@{
        Id = 'mandatory-charge probe backlog-reason'; Kind = 'first-row'; File = "$scenarios/mandatory-charge-vehicle-takes-no-transport.ps1"
        Stage = 'mandatory-charge-vehicle-takes-no-transport'; Probe = 117; Setup = $backlogDemand
    })

return , $cases.ToArray()
