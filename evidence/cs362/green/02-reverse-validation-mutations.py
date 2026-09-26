import sys, subprocess
ENGINE = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs'
M = {
    # R1: drop the re-check under the write lock entirely.
    'R1': (ENGINE,
           '                    if (!await EnteredDemandStillLoadableAsync(runtime, stops, entered, cancellationToken)\n                            .ConfigureAwait(false))',
           '                    if (runtime.AgvId.Length < 0)'),
    # R2: keep the re-check but only on the open cancellation (the old guarantee's shape).
    'R2': (ENGINE,
           '        if (demandNow != DemandExecutionStatus.Accepted)\n        {\n            return false;\n        }',
           '        if (runtime.AgvId.Length < 0)\n        {\n            return false;\n        }'),
    # R3: re-check the demand but not the membership.
    'R3': (ENGINE,
           '        if (!string.Equals(membershipNow, entered.Membership.Status, StringComparison.Ordinal))\n        {\n            return false;\n        }',
           '        if (runtime.AgvId.Length < 0)\n        {\n            return false;\n        }'),
}
M['R23'] = [M['R2'], M['R3']]
# R4: re-check everything but the journey's stage.
M['R4'] = (ENGINE,
           '        if (stageNow != JourneyRuntimeStage.AwaitingSublot)\n        {\n            return false;\n        }',
           '        if (runtime.AgvId.Length < 0)\n        {\n            return false;\n        }')
M['R24'] = [M['R2'], M['R4']]
out = sys.argv[1]
filt = sys.argv[2]
for k in sys.argv[3:]:
    pairs = M[k] if isinstance(M[k], list) else [M[k]]
    f = pairs[0][0]
    raw = open(f, 'rb').read()
    s = raw.decode('utf-8')
    crlf = '\r\n' in s
    t = s.replace('\r\n', '\n')
    counts = [t.count(a) for _, a, _ in pairs]
    if any(n != 1 for n in counts):
        line = f"{k} MATCH={counts} (not run)"
        print(line); open(out, 'a', encoding='utf-8').write(line + '\n'); continue
    for _, a, b in pairs:
        t = t.replace(a, b)
    open(f, 'wb').write((t.replace('\n', '\r\n') if crlf else t).encode('utf-8'))
    try:
        d = subprocess.run(['git', 'diff', '--numstat', f], capture_output=True, text=True).stdout.strip()
        r = subprocess.run('dotnet build tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-incremental',
                           shell=True, capture_output=True, text=True, encoding='utf-8', errors='replace')
        ok = ' 0 Error(s)' in r.stdout
        res = subprocess.run(f'dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-build --filter "{filt}"',
                             shell=True, capture_output=True, text=True, encoding='utf-8', errors='replace').stdout or ''
    finally:
        open(f, 'wb').write(raw)
    lines = res.splitlines(); fails = []
    for i, l in enumerate(lines):
        if l.strip().startswith('Failed ControlServer'):
            msg = ' | '.join(x.strip() for x in lines[i + 2:i + 5])[:420]
            fails.append(l.strip() + '\n      ' + msg)
    summ = [l.strip() for l in lines if 'Passed!' in l or 'Failed!' in l]
    line = f"{k} diff={d} build={'ok' if ok else 'FAIL'} {summ}\n  " + "\n  ".join(fails)
    print(line); open(out, 'a', encoding='utf-8').write(line + '\n')
print(subprocess.run(['git', 'status', '--short', 'src'], capture_output=True, text=True).stdout or 'src clean')
