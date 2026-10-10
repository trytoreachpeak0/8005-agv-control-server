import sys, subprocess
# Mutations for control-server#362's re-check under the write lock. Each replaces a check with a condition that is
# false at run time but not provably so to the compiler: `if (false)` is an unreachable-code warning (an error here)
# and `entered is null` trips the nullable analysis, so either would build nothing and test nothing.
ENGINE = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs'
FALSE_IF = '        if (runtime.AgvId.Length < 0)\n        {\n            return false;\n        }'
M = {
    # R1: drop the re-check under the write lock entirely.
    'R1': (ENGINE,
           '                    if (!await EnteredDemandStillLoadableAsync(runtime, stops, entered, cancellationToken)\n                            .ConfigureAwait(false))',
           '                    if (runtime.AgvId.Length < 0)'),
    # R2: re-check everything but the demand's status.
    'R2': (ENGINE,
           '        if (demandNow != DemandExecutionStatus.Accepted)\n        {\n            return false;\n        }',
           FALSE_IF),
    # R3: re-check everything but the membership.
    'R3': (ENGINE,
           '        if (!string.Equals(membershipNow, entered.Membership.Status, StringComparison.Ordinal))\n        {\n            return false;\n        }',
           FALSE_IF),
    # R4: re-check everything but the journey's stage.
    'R4': (ENGINE,
           '        if (stageNow != JourneyRuntimeStage.AwaitingSublot)\n        {\n            return false;\n        }',
           FALSE_IF),
    # R5: re-check everything but the open cancellation at the stop.
    'R5': (ENGINE,
           '        return !await OpenCancellationAtCurrentStopAsync(stops, cancellationToken).ConfigureAwait(false);',
           '        return runtime.AgvId.Length >= 0;'),
}
M['R23'] = [M['R2'], M['R3']]
M['R24'] = [M['R2'], M['R4']]

out = sys.argv[1]
filt = sys.argv[2]


def write(line):
    print(line)
    open(out, 'a', encoding='utf-8').write(line + '\n')


for k in sys.argv[3:]:
    pairs = M[k] if isinstance(M[k], list) else [M[k]]
    f = pairs[0][0]
    raw = open(f, 'rb').read()
    s = raw.decode('utf-8')
    crlf = '\r\n' in s
    t = s.replace('\r\n', '\n')
    counts = [t.count(a) for _, a, _ in pairs]
    if any(n != 1 for n in counts):
        write(f"{k} MATCH={counts} (not run)")
        continue
    for _, a, b in pairs:
        t = t.replace(a, b)
    open(f, 'wb').write((t.replace('\n', '\r\n') if crlf else t).encode('utf-8'))
    res, code = '', None
    try:
        d = subprocess.run(['git', 'diff', '--numstat', f], capture_output=True, text=True).stdout.strip()
        r = subprocess.run('dotnet build tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-incremental',
                           shell=True, capture_output=True, text=True, encoding='utf-8', errors='replace')
        ok = r.returncode == 0 and ' 0 Error(s)' in r.stdout
        if ok:
            t2 = subprocess.run(
                f'dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-build --filter "{filt}"',
                shell=True, capture_output=True, text=True, encoding='utf-8', errors='replace')
            res, code = t2.stdout or '', t2.returncode
    finally:
        open(f, 'wb').write(raw)
    if not ok:
        errs = sorted({l.strip() for l in r.stdout.splitlines() if ' error ' in l})[:3]
        write(f"{k} diff={d} build=FAIL (not tested)\n  " + "\n  ".join(errs))
        continue
    lines = res.splitlines()
    fails = []
    for i, l in enumerate(lines):
        if l.strip().startswith('Failed ControlServer'):
            msg = ' | '.join(x.strip() for x in lines[i + 2:i + 5])[:420]
            fails.append(l.strip() + '\n      ' + msg)
    summ = [l.strip() for l in lines if 'Passed!' in l or 'Failed!' in l]
    write(f"{k} diff={d} build=ok test_exit={code} {summ}\n  " + "\n  ".join(fails))
print(subprocess.run(['git', 'status', '--short', 'src'], capture_output=True, text=True).stdout or 'src clean')
