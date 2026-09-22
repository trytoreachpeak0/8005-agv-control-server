"""Reverse verification for control-server#299: undo one piece of the fix at a time, run the related tests, record
which ones go red, restore from a byte-for-byte backup. A replacement that does not match exactly once aborts."""
import hashlib, re, shutil, subprocess, sys, os

ROOT = r'C:\Users\szy\Desktop\8005-workspace-v2\worktrees\cs299-8005-agv-control-server'
SVC = 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.cs'
SUP = 'src/ControlServer.Host/Runtime/Commands/EmergencyStopSupervisor.cs'
EP = 'src/ControlServer.Host/Runtime/VehicleFaultRecoveryEndpoints.cs'
DASH = 'src/ControlServer.Host/Dashboard/BlockedJourneysQueryEndpoint.cs'
FILTER = ('FullyQualifiedName~VehicleFaultRecovery|FullyQualifiedName~EmergencyStopSupervisorTests'
          '|FullyQualifiedName~Batch7CargoHoldingDashboardTests')

M = [
 ('M1 automatic release does not ask for unfinished orders', [(SUP,
  '        if (!orders.IsKnown)\n        {\n            obstacles.Add("EMERGENCY_VEHICLE_ORDERS_UNKNOWN");\n        }\n'
  '        else if (orders.HasUnfinishedOrder == true)\n        {\n            obstacles.Add("EMERGENCY_VEHICLE_ORDER_NOT_FINISHED");\n        }\n\n'
  '        if (fault is null)',
  '        if (fault is null)')]),
 ('M2 no transaction around disposition and clearance', [
  (SVC, '        await using IDbContextTransaction transaction =\n            await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);',
        '        IDbContextTransaction? transaction = null;'),
  (SVC, '        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);',
        '        if (transaction is not null) { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); }')]),
 ('M3 the journey is not disposed of', [(SVC,
  '        string disposition = await DisposeOfTheJourneyAsync(subject.AgvId, standing.Journey, now, cancellationToken)\n            .ConfigureAwait(false);',
  '        string disposition = VehicleFaultRecoveryDispositions.None;')]),
 ('M4 an unreadable current order counts as ended', [(SVC,
  '                yield return "FAULT_RECOVERY_CURRENT_ORDER_UNKNOWN";\n                yield break;',
  '                yield break;')]),
 ('M5 SUCCESS counts as ended', [(SVC,
  'OrderState: RiotOrderState.Failed }:',
  'OrderState: RiotOrderState.Failed or RiotOrderState.Success }:')]),
 ('M6 an engaged latch is not checked', [(SVC,
  '        else if (emergency.IsLatched)\n        {\n            yield return "FAULT_RECOVERY_EMERGENCY_LATCHED";\n        }\n',
  '')]),
 ('M7 the runtime gate is not taken for a clearance', [(SVC,
  '        using IDisposable? round = await gate.TryEnterAsync(gateWait, cancellationToken).ConfigureAwait(false);',
  '        using IDisposable? round = gate is null ? null : new System.IO.MemoryStream();')]),
 ('M8 no AlreadyCleared short cut', [(SVC,
  '        fault is { Level: VehicleFaultLevel.None, ClearedReason: string reason } &&\n',
  '        Environment.TickCount64 < 0 && fault is { Level: VehicleFaultLevel.None, ClearedReason: string reason } &&\n')]),
 ('M9 cargo on board is released like an empty vehicle', [(SVC,
  '        if (mayCarry)\n', '        if (mayCarry && Environment.TickCount64 < 0)\n')]),
 ('M10 CANCELLED and DELETED count as FAILED again (review M1)', [(SVC,
  '                yield return "FAULT_RECOVERY_CURRENT_ORDER_CANCELLED_IN_RIOT";\n                yield break;\n',
  '                yield break;\n')]),
 ('M11 the credential is not compared', [(EP,
  '            !FixedTimeEquals(expected, header.Parameter))',
  '            (Environment.TickCount64 < 0 && !FixedTimeEquals(expected, header.Parameter)))')]),
 ('M12 no dashboard description for the cargo-on-board code', [(DASH,
  '            [Runtime.Faults.VehicleFaultRecoveryService.CargoOnBoardReason] =\n'
  '                "车辆故障已由人工清除，但车上可能有货：货物绑定保留，需求不改派，旅程停在这里等人处置"\n'
  '                + "（服务端目前没有让它继续的入口，找值班工程师）。这辆车不接新单",\n', '')]),
 ('M14 a request that timed out on the gate goes ahead anyway', [(SVC,
  '\n        if (round is null)\n', '\n        if (round is null && Environment.TickCount64 < 0)\n')]),
 ('M15 a third type takes the gate', [(SVC,
  'public sealed class VehicleFaultRecoveryService(',
  'internal sealed class GateProbeHolder(JourneyMutationGate held)\n{\n    public System.Threading.Tasks.Task<System.IDisposable> Take() => held.EnterAsync(default);\n}\n\npublic sealed class VehicleFaultRecoveryService(')]),
 ('M16 cargo on board keeps its arrival stage and only changes the code', [(SVC,
  '            runtime.Stage = JourneyRuntimeStage.Blocked;\n', '')]),
 ('M13 a stop still open is not checked', [(SVC,
  '        else if (stopOpen)\n        {\n            yield return "FAULT_RECOVERY_EMERGENCY_STOP_OPEN";\n        }\n', '')]),
 ('M17 the state is not compared under the gate (review M2)', [(SVC,
  '        if (!standing.Agrees(reading))\n        {\n            return Refused(["FAULT_RECOVERY_STATE_CHANGED"], standing.Fault?.FaultGeneration);\n        }\n', '')]),
 ('M18 already-cleared answers before the person is judged (review L3)', [(SVC,
  '            return person.Count > 0 ? Refused(person, fault!.FaultGeneration) : AlreadyCleared(fault!);\n',
  '            return AlreadyCleared(fault!);\n', 2)]),
 ('M19 RIoT is read under the gate (review M2)', [(SVC,
  '        bool stopOpen = await emergencyStop.HasOpenEpisodeAsync(subject, cancellationToken).ConfigureAwait(false);\n',
  '        _ = await orderFacts.ReadUnfinishedOrdersAsync(subject.DeviceKey, cancellationToken).ConfigureAwait(false);\n'
  '        bool stopOpen = await emergencyStop.HasOpenEpisodeAsync(subject, cancellationToken).ConfigureAwait(false);\n')]),
 ('M20 the resume issues its continue under the gate (review M2)', [
  (SVC, '        using (IDisposable? round = await gate.TryEnterAsync(gateWait, cancellationToken).ConfigureAwait(false))\n        {\n',
        '        IDisposable? round = await gate.TryEnterAsync(gateWait, cancellationToken).ConfigureAwait(false);\n        {\n'),
  (SVC, '        return resumed.Resumed\n', '        round?.Dispose();\n        return resumed.Resumed\n')]),
]

def sha(p):
    return hashlib.sha256(open(os.path.join(ROOT, p), 'rb').read()).hexdigest()

def run(cmd):
    r = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace')
    return r.returncode, r.stdout + r.stderr

only = sys.argv[1:]
for name, edits in M:
    if only and name.split()[0] not in only:
        continue
    files = sorted({e[0] for e in edits})
    backups = {f: open(os.path.join(ROOT, f), 'rb').read() for f in files}
    before = {f: sha(f) for f in files}
    try:
        for edit in edits:
            f, old, new = edit[:3]
            want = edit[3] if len(edit) > 3 else 1
            path = os.path.join(ROOT, f)
            s = open(path, encoding='utf-8', newline='').read()
            n = s.count(old)
            if n != want:
                print(f'{name}: replacement matched {n} times in {f}; aborting', flush=True)
                sys.exit(1)
            open(path, 'w', encoding='utf-8', newline='').write(s.replace(old, new))
        diff = run(['git', 'diff', '--numstat'])[1].strip()
        code, out = run(['dotnet', 'build', 'tests/ControlServer.Tests/ControlServer.Tests.csproj', '-c', 'Release', '--nologo'])
        errors = re.search(r'(\d+) Error\(s\)', out)
        if code != 0 or not errors or errors.group(1) != '0':
            print(f'{name}: BUILD FAILED, not a valid probe\n{out[-3000:]}', flush=True)
            continue
        code, out = run(['dotnet', 'test', 'tests/ControlServer.Tests/ControlServer.Tests.csproj', '-c', 'Release',
                         '--no-build', '--filter', FILTER])
        failed = sorted(set(re.findall(r'^\s+Failed (ControlServer\.Tests\.[^\s(]+)', out, re.M)))
        summary = re.findall(r'(Failed!|Passed!)\s+- Failed:\s+(\d+), Passed:\s+(\d+)', out)
        print(f'== {name}\n   diff: {diff}\n   summary: {summary}\n   red ({len(failed)}):', flush=True)
        for t in failed:
            print('     ' + t.replace('ControlServer.Tests.', ''), flush=True)
    finally:
        for f, b in backups.items():
            open(os.path.join(ROOT, f), 'wb').write(b)
        after = {f: sha(f) for f in files}
        if after != before:
            print(f'{name}: RESTORE MISMATCH {files}', flush=True)
            sys.exit(2)
print('restored clean:', run(['git', 'status', '--short'])[1].strip() or '(clean)', flush=True)
