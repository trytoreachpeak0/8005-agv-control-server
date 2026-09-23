"""control-server#330 reverse verification: undo one guard or one decision at a time, rebuild, run the related tests, record
which tests go red, restore the file from its backup and check its hash.

Run from the worktree root:  python evidence/cs330/red/l1-mutations/run-mutations.py [M01 M02 ...]
A mutation whose text does not occur exactly once stops the run (an injection that did not apply looks green).
A build that is not "0 Error(s)" stops the run (the tests would run the previous binaries).
Every mutation guards itself with Environment.TickCount64, which the compiler cannot fold, so no dead-code warning turns
into a build error under TreatWarningsAsErrors.
"""
import hashlib
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path.cwd()
OUT = ROOT / 'evidence/cs330/red/l1-mutations'
FILTER = '|'.join('FullyQualifiedName~' + name for name in [
    'ForeignRunningOrderTests', 'BlockedJourneyDashboardTests', 'PickupDispatchPlanPastOwnOrderTests', 'OwnOrderRebuildTests',
    'MultiVehicleExecutionTests.AVehicleAForeignOrderHoldsTakesNoAppendedDemandUntilTheOrderHasEnded'])

SUP = 'src/ControlServer.Host/Runtime/ForeignOrders/ForeignRunningOrderSupervisor.cs'
FR = 'src/ControlServer.Host/Runtime/ForeignOrders/ForeignRunningOrders.cs'
ENG = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs'
EXPL = 'src/ControlServer.Host/Dashboard/OwnMovementOrderExplanation.cs'
BLOCKED = 'src/ControlServer.Host/Dashboard/BlockedJourneysQueryEndpoint.cs'
DASH = 'src/ControlServer.Host/Dashboard/ForeignRunningOrdersQueryEndpoint.cs'
ROWS = 'src/ControlServer.Infrastructure/Persistence/Rows/ForeignRiotOrderRows.cs'

ON = 'Environment.TickCount64 >= 0'
OFF = 'Environment.TickCount64 < 0'

# (key, what, [(file, old, new), ...])
MUTATIONS = [
    ('M01', 'ownership: every running order taken for this server\'s own (a foreign order let through)',
     [(SUP, '        if (await dbContext.OrderIntents.AsNoTracking()\n',
       f'        if ({ON} || await dbContext.OrderIntents.AsNoTracking()\n')]),
    ('M02', 'ownership: the OrderIntent check off (this server\'s own order judged without it)',
     [(SUP, '.AnyAsync(row => row.OrderId == orderId || (upperId != null && row.UpperId == upperId), cancellationToken)',
       f'.AnyAsync(row => {OFF} && (row.OrderId == orderId || (upperId != null && row.UpperId == upperId)), cancellationToken)')]),
    ('M03', 'ownership: an intent matched by upperId only, not by RIoT orderId',
     [(SUP, '.AnyAsync(row => row.OrderId == orderId || (upperId',
       f'.AnyAsync(row => ({OFF} && row.OrderId == orderId) || (upperId')]),
    ('M04', 'ownership: the order command audit check off',
     [(SUP, '        if (await dbContext.RiotOrderCommandAudit.AsNoTracking()\n',
       f'        if ({OFF} && await dbContext.RiotOrderCommandAudit.AsNoTracking()\n')]),
    ('M05', 'ownership: the upperId-shaped-as-ours check off',
     [(SUP, '        if (upperId is not null &&\n            upperId.TrimStart()',
       f'        if ({OFF} && upperId is not null &&\n            upperId.TrimStart()')]),
    ('M06', 'ownership: the prefix compared case-sensitively',
     [(SUP, 'OwnUpperIdPrefix, StringComparison.OrdinalIgnoreCase)', 'OwnUpperIdPrefix, StringComparison.Ordinal)')]),
    ('M07', 'ownership: leading whitespace before the prefix not trimmed',
     [(SUP, 'upperId.TrimStart().StartsWith(', 'upperId.StartsWith(')]),
    ('M08', 'scope: a QUEUEING order counted as running',
     [(FR, 'order.OrderState is RiotOrderState.Executing or RiotOrderState.Paused or RiotOrderState.Hang &&',
       'order.OrderState is RiotOrderState.Queueing or RiotOrderState.Executing or RiotOrderState.Paused or RiotOrderState.Hang &&')]),
    ('M09', 'scope: the "--" placeholder guard off (expected covered by the roster lookup)',
     [(FR, '!string.Equals(order.ExecuteVehicleKey.Trim(), UnassignedVehicleKey, StringComparison.Ordinal);',
       f'({OFF} || !string.Equals(order.ExecuteVehicleKey.Trim(), UnassignedVehicleKey, StringComparison.Ordinal));')]),
    ('M10', 'scope: an order on a vehicle that is not ours taken for one on ours',
     [(SUP, '        ours.TryGetValue(order.ExecuteVehicleKey!.Trim(), out FleetVehicle? vehicle)\n',
       '        (ours.TryGetValue(order.ExecuteVehicleKey!.Trim(), out FleetVehicle? vehicle) || (vehicle = ours.Values.First()) is not null)\n')]),
    ('M11', 'scope: an archived vehicle still counted as ours',
     [(SUP, '.Where(vehicle => !archived.Contains(vehicle.AgvId))',
       f'.Where(vehicle => {ON} || !archived.Contains(vehicle.AgvId))')]),
    ('M12', 'guard: the re-read before the cancel ignored (cancelled though no longer running on our vehicle)',
     [(SUP, '        if (RunningOnOurs(order, ours) is null)\n        {',
       f'        if ({OFF} && RunningOnOurs(order, ours) is null)\n        {{')]),
    ('M13', 'guard: "the same vehicle" dropped from the re-read',
     [(SUP, '        if (RunningOnOurs(order, ours) is { } elsewhere &&\n',
       f'        if ({OFF} && RunningOnOurs(order, ours) is {{ }} elsewhere &&\n')]),
    ('M14', 'guard: an incomplete re-read acted on (expected covered by the explicit-ending read)',
     [(SUP, '        if (!reread.IsComplete)\n', f'        if ({OFF} && !reread.IsComplete)\n')]),
    ('M15', 'once: an armed attempt no longer stops a second cancel',
     [(SUP, '        if (armed is not null)\n', f'        if ({OFF} && armed is not null)\n')]),
    ('M16', 'once: M15 and a cancelled order coming back to our vehicle taken up as never cancelled',
     [(SUP, '        if (armed is not null)\n', f'        if ({OFF} && armed is not null)\n'),
      (SUP, '                row.State = row.CancelCommandAuditId is not null\n',
       f'                row.State = {OFF} && row.CancelCommandAuditId is not null\n')]),
    ('M17', 'only the cancel: HELD sent instead',
     [(SUP, 'RiotOrderCommandKind.Cancel, row.RiotOrderId, ForeignRunningOrders.CancelReason',
       'RiotOrderCommandKind.Hold, row.RiotOrderId, ForeignRunningOrders.CancelReason')]),
    ('M18', 'hand-over: a cancel that did not take is never handed to a person',
     [(SUP, '            now - sentAt < CancelSettleTime)\n', f'            now - sentAt < CancelSettleTime || {ON})\n')]),
    ('M19', 'ending: SUSPENDED read as an explicit ending',
     [(FR, 'RiotOrderState.Cancelled or RiotOrderState.Failed or RiotOrderState.Success or RiotOrderState.Deleted;',
       'RiotOrderState.Cancelled or RiotOrderState.Failed or RiotOrderState.Success or RiotOrderState.Deleted or RiotOrderState.Suspended;')]),
    ('M20', 'listing: an incomplete listing acted on (expected covered by the explicit-ending read)',
     [(SUP, '        if (listing.IsComplete)\n', f'        if ({ON} || listing.IsComplete)\n')]),
    ('M21', '0/1 gate: a held vehicle offered as free for new dispatch',
     [(ENG, '.Where(vehicle => !busy.Contains(vehicle.AgvId) && !heldByForeignOrder.Contains(vehicle.AgvId))',
       '.Where(vehicle => !busy.Contains(vehicle.AgvId))')]),
    ('M22', '0/1 gate: a held vehicle offered for appended demand',
     [(ENG, '                              !heldByForeignOrder.Contains(vehicle.AgvId))\n', f'                              {ON})\n')]),
    ('M23', 'explanation: a foreign order on the vehicle ignored',
     [(EXPL, '            foreignRunningOrderHoldsVehicle ||\n', f'            ({OFF} && foreignRunningOrderHoldsVehicle) ||\n')]),
    ('M24', 'explanation: the engine (#314 plan) passes "no foreign order"',
     [(ENG, '                ownOrderInFlight,\n                foreignOrderHoldsVehicle))',
       '                ownOrderInFlight,\n                false))')]),
    ('M25', 'explanation: the blocked-journey dashboard passes "no foreign order"',
     [(BLOCKED, '            ownOrderInFlight,\n            foreignOrderHoldsVehicle);', '            ownOrderInFlight,\n            false);')]),
    ('M26', 'isolation: a failing supervision ends the round',
     [(ENG, '            await foreignOrders.SuperviseAsync(cancellationToken).ConfigureAwait(false);\n        }\n'
            '        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)\n',
       '            await foreignOrders.SuperviseAsync(cancellationToken).ConfigureAwait(false);\n        }\n'
       f'        catch (Exception error) when ({OFF} && (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested))\n')]),
    ('M27', 'dashboard: rows that hold nothing listed too',
     [(DASH, '            .Where(row => holding.Contains(row.State))\n', f'            .Where(row => {ON} || holding.Contains(row.State))\n')]),
    ('M28', 'ending: an explicit ending does not release the vehicle',
     [(SUP, '        row.State = ForeignRiotOrderStates.Ended;\n',
       f'        row.State = {ON} ? row.State : ForeignRiotOrderStates.Ended;\n')]),
    # ---- added with the independent review's fixes (M1, S1, S3, L5) ----
    ('M29', 'cancel gate: ignored (a cancel sent while the gate is closed)',
     [(SUP, '        if (!cancelGate.Value.Enabled)\n', f'        if ({OFF} && !cancelGate.Value.Enabled)\n')]),
    ('M30', 'cancel gate: an order held for a closed gate never taken up once it is opened',
     [(SUP, '(mayCancel && row.State == ForeignRiotOrderStates.HeldCancelNotAuthorized)',
       f'({OFF} && mayCancel && row.State == ForeignRiotOrderStates.HeldCancelNotAuthorized)')]),
    ('M31', 'cancel gate: HELD_CANCEL_NOT_AUTHORIZED does not hold the vehicle',
     [(ROWS, 'HeldUnproven, HeldCancelNotAuthorized, Unsettled];', 'HeldUnproven, Unsettled];')]),
    ('M32', 'unsettled: an order that left the listing without ending never goes to a person',
     [(SUP, '            if (row.State != ForeignRiotOrderStates.Unsettled && now - row.LastSeenRunningAt >= CancelSettleTime)\n',
       f'            if ({OFF} && row.State != ForeignRiotOrderStates.Unsettled && now - row.LastSeenRunningAt >= CancelSettleTime)\n')]),
    ('M33', 'unsettled: UNSETTLED does not hold the vehicle',
     [(ROWS, 'HeldUnproven, HeldCancelNotAuthorized, Unsettled];', 'HeldUnproven, HeldCancelNotAuthorized];')]),
    ('M34', 'cancel result: not rewritten when the order ends (a hand-over stays STILL_RUNNING)',
     [(SUP, '            row.CancelResult = orderState == RiotOrderState.Cancelled\n',
       '            row.CancelResult ??= orderState == RiotOrderState.Cancelled\n')]),
    ('M35', 'blocked-journey card: no note pointing at the foreign order',
     [(BLOCKED, '        if (!foreignOrderHoldsVehicle)\n', f'        if ({ON} || !foreignOrderHoldsVehicle)\n')]),
]


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def run(cmd, log):
    result = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace')
    log.write(result.stdout + result.stderr)
    return result


def main():
    selected = set(sys.argv[1:])
    for key, what, edits in MUTATIONS:
        if selected and key not in selected:
            continue
        originals = {}
        for path, _, _ in edits:
            originals.setdefault(path, ((ROOT / path).read_bytes(), sha(ROOT / path)))
        bodies = {path: data.decode('utf-8').replace('\r\n', '\n') for path, (data, _) in originals.items()}
        for path, old, new in edits:
            count = bodies[path].count(old)
            if count != 1:
                print(f'{key}: the mutation text occurs {count} times in {path}; stopping.')
                sys.exit(1)
            bodies[path] = bodies[path].replace(old, new)
        log_path = OUT / f'{key}.log'
        try:
            for path, body in bodies.items():
                eol = '\r\n' if b'\r\n' in originals[path][0] else '\n'
                (ROOT / path).write_bytes(body.replace('\n', eol).encode('utf-8'))
            with open(log_path, 'w', encoding='utf-8', newline='\n') as log:
                log.write(f'# {key}: {what}\n')
                for path, old, new in edits:
                    log.write(f'# file: {path}\n# - {old!r}\n# + {new!r}\n')
                build = run(['dotnet', 'build', 'tests/ControlServer.Tests/ControlServer.Tests.csproj', '-c', 'Release'], log)
                if ' 0 Error(s)' not in build.stdout:
                    print(f'{key}: the build did not succeed; stopping (see {log_path}).')
                    sys.exit(1)
                test = run(['dotnet', 'test', 'tests/ControlServer.Tests/ControlServer.Tests.csproj', '-c', 'Release', '--no-build',
                            '--filter', FILTER], log)
        finally:
            for path, (data, digest) in originals.items():
                (ROOT / path).write_bytes(data)
                assert sha(ROOT / path) == digest, f'{path} was not restored'
        failed = sorted(set(re.findall(r'^\s+Failed (ControlServer\.Tests\.\S+)', test.stdout, re.MULTILINE)))
        totals = re.findall(r'(Failed!|Passed!)\s+-\s+Failed:\s+(\d+), Passed:\s+(\d+)', test.stdout)
        total = totals[-1] if totals else ('?', '?', '?')
        print(f'{key}: {" ".join(total)} -> {len(failed)} red', flush=True)
        # written as each mutation finishes, so a run that stops later keeps what it has
        with open(OUT / 'summary.txt', 'a', encoding='utf-8', newline='\n') as out:
            out.write(f'{key} {what}\n  totals: {" ".join(total)}\n')
            for name in failed:
                out.write(f'  red: {name}\n')
            if not failed:
                out.write('  red: (none)\n')
    # the tree is left as it was found; rebuild so later runs use the real binaries
    subprocess.run(['dotnet', 'build', 'tests/ControlServer.Tests/ControlServer.Tests.csproj', '-c', 'Release'], cwd=ROOT,
                   capture_output=True)


if __name__ == '__main__':
    main()
