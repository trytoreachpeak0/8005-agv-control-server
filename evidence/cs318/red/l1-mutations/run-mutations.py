"""control-server#318 reverse verification: undo one guard or one decision at a time, rebuild, run the related test classes,
record which tests go red, restore the file from its backup and check its hash.

Run from the worktree root:  python evidence/cs318/red/l1-mutations/run-mutations.py
A mutation whose text does not occur exactly once stops the run (an injection that did not apply looks green).
A build that is not "0 Error(s)" stops the run (the tests would run the previous binaries).
"""
import hashlib
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path.cwd()
OUT = ROOT / 'evidence/cs318/red/l1-mutations'
FILTER = '|'.join('FullyQualifiedName~' + name for name in [
    'OwnOrderRebuildTests', 'VehicleFaultRecovery', 'Batch7DemandReleaseServiceTests', 'InTransitOrderStallTests',
    'Batch7CargoHoldingDashboardTests', 'OwnOrderRebuildCargoEvidenceRequestTests', 'MultiVehicleExecutionTests'])

ENGINE = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.OwnOrderRebuild.cs'
MAIN = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs'
STORE = 'src/ControlServer.Host/Runtime/OwnOrderRebuilds.cs'
RECOVERY = 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.cs'
RELEASE = 'src/ControlServer.Host/Runtime/Release/DemandReleaseService.cs'
DASHBOARD = 'src/ControlServer.Host/Dashboard/BlockedJourneysQueryEndpoint.cs'
PROCESSOR = 'src/ControlServer.Host/Transport/OnboardMessageProcessor.cs'

OFF = 'Environment.TickCount64 < 0 && '
OPTIONS = 'src/ControlServer.Host/Runtime/JourneyRuntimeOptions.cs'

MUTATIONS = [
    ('M01', 'guard 1 (delay) off', ENGINE,
     '            if (now < rebuild.DueAt)\n',
     '            if (' + OFF + 'now < rebuild.DueAt)\n'),
    # M02 and M15 re-pointed in the fourth run: the checks moved into HeldBeforeCreateAsync (review M1), four spaces less
    ('M02', 'guard 2 (vehicle condition) off', ENGINE,
     '        if (vehicle.Length > 0)\n',
     '        if (' + OFF + 'vehicle.Length > 0)\n'),
    ('M03', 'guard 3 (a second problem within the window, REQ-0361) off', STORE,
     '            .Any(earlier => earlier.IncidentAt >= since && earlier.IncidentAt <= incidentAt &&\n',
     '            .Any(earlier => Environment.TickCount64 < 0 && earlier.IncidentAt >= since && earlier.IncidentAt <= incidentAt &&\n'),
    ('M04', "#299's release on clearance restored (nothing on board: release and close; cargo: Blocked for a person)", RECOVERY,
     '''        JourneyStopRow stop = (await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false))
            .Current;
''',
     '''        if (mayCarry)
        {
            runtime.Stage = JourneyRuntimeStage.Blocked;
            runtime.SetBlockReason(CargoOnBoardReason, now);
            runtime.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return "HELD_FOR_PERSON";
        }

        if (Environment.TickCount64 >= 0)
        {
            JourneyDemandRow[] waiting = [.. memberships.Where(row => row.Status == JourneyDemandStatuses.PendingLoad)];
            await JourneyPlanRevisionStage.StageAsync(
                dbContext, runtime.JourneyId, [.. waiting.Select(row => row.DemandId)], currentStopMayGo: true, routing: null,
                cancellationToken).ConfigureAwait(false);
            await new PickupStopTermination(dbContext)
                .StageJourneyClosureAsync(runtime, DemandReleaseReasons.Released, now, cancellationToken)
                .ConfigureAwait(false);
            foreach (JourneyDemandRow membership in waiting)
            {
                await DemandReleaseService.StageReleasedMembershipAsync(dbContext, membership, now, cancellationToken)
                    .ConfigureAwait(false);
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return "RELEASED_FOR_REDISPATCH";
        }

        JourneyStopRow stop = (await JourneyStopCursor.LoadAsync(dbContext, runtime, cancellationToken).ConfigureAwait(false))
            .Current;
'''),
    ('M05', 'no rebuild behind the readiness gate', MAIN,
     '        if (await OwnOrderRebuilds.ForStopAsync(dbContext, stops.Current, cancellationToken).ConfigureAwait(false) is not null)\n',
     '        if (' + OFF + 'await OwnOrderRebuilds.ForStopAsync(dbContext, stops.Current, cancellationToken).ConfigureAwait(false) is not null)\n'),
    # M06 re-pointed in the fourth run: MayCreateBehindTheGateAsync is gone (review M2), the rebuild behind the gate creates nothing
    ('M06', 'behind the readiness gate, the rebuild may create', MAIN,
     '                    runtime, stops.Current, currentMap, mayCreate: false, reasonOnceRebuilt: null, cancellationToken)\n',
     '                    runtime, stops.Current, currentMap, mayCreate: Environment.TickCount64 >= 0, reasonOnceRebuilt: null, cancellationToken)\n'),
    ('M07', 'record key random instead of derived from the ended upperId', STORE,
     '        JourneyPlanBuilder.StableGuid(endedUpperId, "own-order-rebuild");\n',
     '        Guid.NewGuid().ToString("D");\n'),
    ('M08', "an order this server cancelled itself is rebuilt like any other", ENGINE,
     '                    row => row.TargetUpperId == upperId && row.CommandType == RiotCommandTypeNames.CancelOrder,\n',
     '                    row => ' + OFF + 'row.TargetUpperId == upperId && row.CommandType == RiotCommandTypeNames.CancelOrder,\n'),
    ('M09', 'release service releases or cancels a stalled or rebuilding journey', RELEASE,
     '            await OrderStalledOrRebuildingAsync(journey, stops.Current, cancellationToken).ConfigureAwait(false))\n',
     '            Environment.TickCount64 < 0 && await OrderStalledOrRebuildingAsync(journey, stops.Current, cancellationToken).ConfigureAwait(false))\n'),
    ('M10', 'guard 2 does not read the vehicle map', ENGINE,
     '                !string.Equals(vehicle.CurrentMap, runtime.MapIdentity, StringComparison.Ordinal))\n',
     '                ' + OFF + '!string.Equals(vehicle.CurrentMap, runtime.MapIdentity, StringComparison.Ordinal))\n'),
    ('M11', 'a stopped record is found only by its ended order', STORE,
     '                     (row.EndedUpperId == stop.UpperId || row.NewUpperId == stop.UpperId))),\n',
     '                     row.EndedUpperId == stop.UpperId)),\n'),
    ('M12', 'the cargo binding is not settled when the rebuilt order is confirmed', ENGINE,
     '        if (rebuild.Source == OwnOrderRebuildSources.FaultClearedCargoOnBoard)\n',
     '        if (' + OFF + 'rebuild.Source == OwnOrderRebuildSources.FaultClearedCargoOnBoard)\n'),
    ('M13', 'the stopped code has no dashboard description', DASHBOARD,
     '            [JourneyRuntimeEngine.OwnOrderRebuildStoppedReason] =\n',
     '            ["MUTATION_REMOVED_DESCRIPTION"] =\n'),
    ('M14', 'the rebuild codes are not in the stalled-order family', MAIN,
     '        OwnOrderRebuildWaitingVehicleReason or OwnOrderRebuildBlockedByCreateGateReason or\n        OwnOrderRebuildOrderUnconfirmedReason or OwnOrderRebuildStoppedReason or\n        OwnOrderRebuildWaitingCargoEvidenceReason or OwnOrderRebuildCargoNotInPlaceReason or\n        OwnOrderRebuildCargoUnprovenReason or\n        OwnOrderRebuildVehicleIneligibleReason;\n',
     '        "MUTATION_NOT_A_CODE";\n'),
    ('M15', 'the rebuilt order skips the create gate', ENGINE,
     '        if (!gate.IsAllowed)\n',
     '        if (' + OFF + '!gate.IsAllowed)\n'),
    ('M16', 'an unconfirmed create is taken as rebuilt', ENGINE,
     '        if (result.Outcome != MovementDispatchOutcome.Confirmed)\n',
     '        if (' + OFF + 'result.Outcome != MovementDispatchOutcome.Confirmed)\n'),
    ('M17', 'REQ-0361 source rule off: any later problem repeats a cancellation', STORE,
     '        first != OwnOrderRebuildSources.CancelledInRiot || again == OwnOrderRebuildSources.CancelledInRiot;\n',
     '        Environment.TickCount64 >= 0 || first != OwnOrderRebuildSources.CancelledInRiot || again == OwnOrderRebuildSources.CancelledInRiot;\n'),
    ('M18', 'REQ-0362 a snapshot from before the clearance counts', ENGINE,
     '            .Where(row => row.ReceivedAt > rebuild.RecordedAt)\n',
     '            .Where(row => Environment.TickCount64 >= 0 || row.ReceivedAt > rebuild.RecordedAt)\n'),
    ('M19', 'REQ-0362 a cargo slot need not read OCCUPIED', ENGINE,
     '            else if (physical != "OCCUPIED" || locked != "LOCKED" || output != "RESET")\n',
     '            else if ((Environment.TickCount64 < 0 && physical != "OCCUPIED") || locked != "LOCKED" || output != "RESET")\n'),
    ('M20', 'REQ-0362 unknownPresent is not read', ENGINE,
     '        if (!payload.GetProperty("safety").TryGetProperty("unknownPresent", out JsonElement unknown) ||\n            unknown.ValueKind != JsonValueKind.False)\n',
     '        if (Environment.TickCount64 < 0 && (!payload.GetProperty("safety").TryGetProperty("unknownPresent", out JsonElement unknown) ||\n            unknown.ValueKind != JsonValueKind.False))\n'),
    ('M21', 'REQ-0362 the request is never recorded, so it is not throttled', STORE,
     '                    .SetProperty(row => row.CargoEvidenceRequestedGeneration, generation)\n',
     '                    .SetProperty(row => row.CargoEvidenceRequestedGeneration, (long?)null)\n'),
    ('M22', 'REQ-0362 the recovery report that ends the handshake may carry the request', PROCESSOR,
     '        if (state.HandshakeCompleted && messageType != "RecoveryStateReport" &&\n',
     '        if (state.HandshakeCompleted && (Environment.TickCount64 >= 0 || messageType != "RecoveryStateReport") &&\n'),
    ('M23', 'REQ-0362 the request is claimed inside the handshake too', PROCESSOR,
     '        if (state.HandshakeCompleted && messageType != "RecoveryStateReport" &&\n',
     '        if ((Environment.TickCount64 >= 0 || state.HandshakeCompleted) && messageType != "RecoveryStateReport" &&\n'),
    ('M24', "the release's own cancellation, read by the engine first, counts as a stalled order", RELEASE,
     '        if (string.Equals(journey.BlockReasonCode, JourneyRuntimeEngine.OrderEndedWithoutArrivalReason, StringComparison.Ordinal) &&\n',
     '        if (Environment.TickCount64 < 0 && string.Equals(journey.BlockReasonCode, JourneyRuntimeEngine.OrderEndedWithoutArrivalReason, StringComparison.Ordinal) &&\n'),
    ('M25', "REQ-0360 the rebuild revives the journey's terminated demands", ENGINE,
     '            rebuild.State = OwnOrderRebuildStates.Ordering;\n',
     '            foreach (JourneyDemandRow revived in await dbContext.Set<JourneyDemandRow>().Where(row => row.JourneyId == runtime.JourneyId).ToArrayAsync(cancellationToken)) { revived.Status = JourneyDemandStatuses.PendingLoad; }\n            rebuild.State = OwnOrderRebuildStates.Ordering;\n'),
    # fourth run: the independent review's M1, M2, S1..S6
    ('M26', 'review M1: a rebuild decided before a crash is created without asking the vehicle and the gate again', ENGINE,
     '                     .SingleAsync(cancellationToken).ConfigureAwait(false) == 0)\n',
     '                     .SingleAsync(cancellationToken).ConfigureAwait(false) == 0 && Environment.TickCount64 < 0)\n'),
    ('M27', "review M2: Onboard's departure summary is not read", ENGINE,
     '        else if (!VehicleDynamicFactsCriterion.SaysTheVehicleMayDepart(onboard))\n',
     '        else if (' + OFF + '!VehicleDynamicFactsCriterion.SaysTheVehicleMayDepart(onboard))\n'),
    ('M28', 'review S1: eligibility is not asked before the rebuild', ENGINE,
     '        if (stop.StopRole == JourneyStopRoles.Pickup &&\n',
     '        if (' + OFF + 'stop.StopRole == JourneyStopRoles.Pickup &&\n'),
    ('M29', "review S1: the release service holds a rebuild the engine gave up for eligibility", RELEASE,
     '                           row.StoppedReason == OwnOrderRebuilds.VehicleNoLongerEligible,\n',
     '                           row.StoppedReason == OwnOrderRebuilds.VehicleNoLongerEligible && Environment.TickCount64 < 0,\n'),
    ('M30', 'review S2: a FAILED before confirmation is not recorded as a fault', ENGINE,
     '        if (order is { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Failed })\n',
     '        if (' + OFF + 'order is { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Failed })\n'),
    ('M31', 'review S2: a cancellation before confirmation stops without the window (the first version)', ENGINE,
     '        if (order is { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Cancelled or RiotOrderState.Deleted })\n',
     '        if (' + OFF + 'order is { Kind: RiotOrderObservationKind.Terminal, OrderState: RiotOrderState.Cancelled or RiotOrderState.Deleted })\n'),
    ('M32', 'review S2: the clearance does not read a terminal-reconciled intent', RECOVERY,
     '        if (intent is { Status: "CONFIRMED" or "TERMINAL_RECONCILIATION_REQUIRED", OrderId: not null })\n',
     '        if (intent is { Status: "CONFIRMED", OrderId: not null })\n'),
    ('M33', 'review S2: the next record does not end the FAILED one', STORE,
     '                     .Where(row => row.NewUpperId == endedUpperId && row.State == OwnOrderRebuildStates.Failed)\n',
     '                     .Where(row => Environment.TickCount64 < 0 && row.NewUpperId == endedUpperId && row.State == OwnOrderRebuildStates.Failed)\n'),
    ('M34', 'review S2: an ENDED record counts as a rebuild under way for the release', RELEASE,
     '                              row.State != OwnOrderRebuildStates.Ended &&\n',
     '                              (Environment.TickCount64 >= 0 || row.State != OwnOrderRebuildStates.Ended) &&\n'),
    ('M35', 'review S4: an inconclusive snapshot is taken as proof', ENGINE,
     '                if (evidence.NotShown is null && evidence.Unproven is not null)\n',
     '                if (' + OFF + 'evidence.NotShown is null && evidence.Unproven is not null)\n'),
    ('M36', 'review S4: an EMPTY slot is only inconclusive', ENGINE,
     '            if (physical == "EMPTY")\n',
     '            if (' + OFF + 'physical == "EMPTY")\n'),
    ('M37', 'review S4: an inconclusive snapshot is never asked for again', ENGINE,
     '                        now - evidence.ReceivedAt >= CargoEvidenceReaskInterval)\n',
     '                        ' + OFF + 'now - evidence.ReceivedAt >= CargoEvidenceReaskInterval)\n'),
    ('M38', "review S3: another vehicle's snapshot counts", ENGINE,
     '            if (SnapshotOf(json) == runtime.AgvId)\n',
     '            if (Environment.TickCount64 >= 0 || SnapshotOf(json) == runtime.AgvId)\n'),
    ('M39', 'review S3: an unsettled load is not looked at', ENGINE,
     '        if (loads.FirstOrDefault(row => row.Status is StationOperationStatus.Prepared or StationOperationStatus.RecoveryRequired)\n',
     '        if (' + OFF + 'loads.FirstOrDefault(row => row.Status is StationOperationStatus.Prepared or StationOperationStatus.RecoveryRequired)\n'),
    ('M40', 'review S3: no committed load is not looked at', ENGINE,
     '        if (cargoSlots.Length == 0)\n',
     '        if (' + OFF + 'cargoSlots.Length == 0)\n'),
    ('M41', 'review S3: a cargo slot missing from the snapshot is passed over', ENGINE,
     '                unproven.Add($"SLOT_{slot}:NOT_REPORTED");\n',
     '                _ = slot;\n'),
    ('M42', 'review S6: a zero delay is accepted', OPTIONS,
     '        if (options.OwnOrderRebuildDelay <= TimeSpan.Zero || options.OwnOrderRebuildDelay > TimeSpan.FromMinutes(10))\n',
     '        if (options.OwnOrderRebuildDelay < TimeSpan.Zero || options.OwnOrderRebuildDelay > TimeSpan.FromMinutes(10))\n'),
    ('M43', 'review S2: the FAILED new order is fed to the fault model only once', ENGINE,
     '            await ObserveRebuiltOrderFailureAsync(runtime, rebuild, cancellationToken).ConfigureAwait(false);\n',
     '            _ = rebuild;\n'),
]


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def run(cmd, log):
    result = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace')
    log.write(result.stdout + result.stderr)
    return result


def main():
    selected = set(sys.argv[1:])
    summary = []
    for key, what, path, old, new in MUTATIONS:
        if selected and key not in selected:
            continue
        file = ROOT / path
        original = file.read_bytes()
        digest = sha(file)
        text = original.decode('utf-8')
        eol = '\r\n' if '\r\n' in text else '\n'
        body = text.replace('\r\n', '\n')
        count = body.count(old)
        if count != 1:
            print(f'{key}: the mutation text occurs {count} times in {path}; stopping.')
            sys.exit(1)
        file.write_bytes(body.replace(old, new).replace('\n', eol).encode('utf-8'))
        log_path = OUT / f'{key}.log'
        try:
            with open(log_path, 'w', encoding='utf-8', newline='\n') as log:
                log.write(f'# {key}: {what}\n# file: {path}\n# diff lines changed: {len(old.splitlines())} -> {len(new.splitlines())}\n')
                build = run(['dotnet', 'build', 'tests/ControlServer.Tests/ControlServer.Tests.csproj', '-c', 'Release'], log)
                if ' 0 Error(s)' not in build.stdout:
                    print(f'{key}: the build did not succeed; stopping (see {log_path}).')
                    sys.exit(1)
                test = run(['dotnet', 'test', 'tests/ControlServer.Tests/ControlServer.Tests.csproj', '-c', 'Release', '--no-build',
                            '--filter', FILTER], log)
        finally:
            file.write_bytes(original)
            assert sha(file) == digest, f'{path} was not restored'
        failed = sorted(set(re.findall(r'^\s+Failed (ControlServer\.Tests\.\S+)', test.stdout, re.MULTILINE)))
        totals = re.findall(r'(Failed!|Passed!)\s+-\s+Failed:\s+(\d+), Passed:\s+(\d+)', test.stdout)
        summary.append((key, what, totals[-1] if totals else ('?', '?', '?'), failed))
        print(f'{key}: {totals[-1] if totals else "no totals"} -> {len(failed)} red')
    with open(OUT / 'summary.txt', 'a', encoding='utf-8', newline='\n') as out:
        for key, what, totals, failed in summary:
            out.write(f'{key} {what}\n  totals: {" ".join(totals)}\n')
            for name in failed:
                out.write(f'  red: {name}\n')
            if not failed:
                out.write('  red: (none)\n')
    # the tree is left as it was found; rebuild so later runs use the real binaries
    subprocess.run(['dotnet', 'build', 'tests/ControlServer.Tests/ControlServer.Tests.csproj', '-c', 'Release'], cwd=ROOT,
                   capture_output=True)


if __name__ == '__main__':
    main()
