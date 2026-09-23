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

MUTATIONS = [
    ('M01', 'guard 1 (delay) off', ENGINE,
     '            if (now < rebuild.DueAt)\n',
     '            if (' + OFF + 'now < rebuild.DueAt)\n'),
    ('M02', 'guard 2 (vehicle condition) off', ENGINE,
     '            if (vehicle.Length > 0)\n',
     '            if (' + OFF + 'vehicle.Length > 0)\n'),
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
    ('M06', 'behind the gate, any not-ready session may create', MAIN,
     '                    await MayCreateBehindTheGateAsync(runtime, cancellationToken).ConfigureAwait(false),\n',
     '                    await MayCreateBehindTheGateAsync(runtime, cancellationToken).ConfigureAwait(false) || Environment.TickCount64 >= 0,\n'),
    ('M07', 'record key random instead of derived from the ended upperId', STORE,
     '        JourneyPlanBuilder.StableGuid(endedUpperId, "own-order-rebuild");\n',
     '        Guid.NewGuid().ToString("D");\n'),
    ('M08', "an order this server cancelled itself is rebuilt like any other", ENGINE,
     '                    row => row.TargetUpperId == upperId && row.CommandType == RiotCommandTypeNames.CancelOrder,\n',
     '                    row => ' + OFF + 'row.TargetUpperId == upperId && row.CommandType == RiotCommandTypeNames.CancelOrder,\n'),
    ('M09', 'release service releases or cancels a stalled or rebuilding journey', RELEASE,
     '            await OrderStalledOrRebuildingAsync(journey, cancellationToken).ConfigureAwait(false))\n',
     '            ' + OFF + 'await OrderStalledOrRebuildingAsync(journey, cancellationToken).ConfigureAwait(false))\n'),
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
     '        OwnOrderRebuildWaitingVehicleReason or OwnOrderRebuildBlockedByCreateGateReason or\n        OwnOrderRebuildOrderUnconfirmedReason or OwnOrderRebuildStoppedReason or\n        OwnOrderRebuildWaitingCargoEvidenceReason or OwnOrderRebuildCargoNotInPlaceReason;\n',
     '        "MUTATION_NOT_A_CODE";\n'),
    ('M15', 'the rebuilt order skips the create gate', ENGINE,
     '            if (!gate.IsAllowed)\n',
     '            if (' + OFF + '!gate.IsAllowed)\n'),
    ('M16', 'an unconfirmed create is taken as rebuilt', ENGINE,
     '        if (result.Outcome != MovementDispatchOutcome.Confirmed)\n',
     '        if (' + OFF + 'result.Outcome != MovementDispatchOutcome.Confirmed)\n'),
    ('M17', 'REQ-0361 source rule off: any later problem repeats a cancellation', STORE,
     '        first != OwnOrderRebuildSources.CancelledInRiot || again == OwnOrderRebuildSources.CancelledInRiot;\n',
     '        Environment.TickCount64 >= 0 || first != OwnOrderRebuildSources.CancelledInRiot || again == OwnOrderRebuildSources.CancelledInRiot;\n'),
    ('M18', 'REQ-0362 a snapshot from before the clearance counts', ENGINE,
     '            .Where(row => row.ReceivedAt > rebuild.RecordedAt && SnapshotOf(row) == runtime.AgvId)\n',
     '            .Where(row => (Environment.TickCount64 >= 0 || row.ReceivedAt > rebuild.RecordedAt) && SnapshotOf(row) == runtime.AgvId)\n'),
    ('M19', 'REQ-0362 a cargo slot need not read OCCUPIED', ENGINE,
     '            if (physical != "OCCUPIED" || locked != "LOCKED" || output != "RESET")\n',
     '            if ((Environment.TickCount64 < 0 && physical != "OCCUPIED") || locked != "LOCKED" || output != "RESET")\n'),
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
