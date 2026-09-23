import io, os, re, subprocess, sys, time

ROOT = r'C:\Users\szy\Desktop\8005-workspace-v2\worktrees\cs331-8005-agv-control-server'
ENGINE = os.path.join(ROOT, r'src\ControlServer.Host\Runtime\JourneyRuntimeEngine.cs')
OUTDIR = r'C:\Users\szy\AppData\Local\Temp\claude\C--Users-szy-Desktop-8005-workspace-v2\4e49a3b9-e2dd-48ec-8d7b-87cd8aecd97e\scratchpad\mut\run5'
os.makedirs(OUTDIR, exist_ok=True)
FILTER = 'FullyQualifiedName~ArrivalPublishInterruptedThenReconnectedTests|FullyQualifiedName~OnboardSessionLostBlockTests'

MUTATIONS = [
    ('R1', 'IsTransportFailure no longer counts ObjectDisposedException',
     'if (current is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)',
     'if (current is IOException or System.Net.Sockets.SocketException)',
     ['OnTheOwnOrderATransportFailureInAnyShapeKeepsTheSessionNotReadyCode(shape: "disposed-connection")']),
    ('R2', 'IsTransportFailure looks at the top-level exception only',
     'for (Exception? current = failure; current is not null; current = current.InnerException)',
     'for (Exception? current = failure; current is not null; current = null)',
     ['OnTheOwnOrderATransportFailureInAnyShapeKeepsTheSessionNotReadyCode(shape: "wrapped-io")']),
    ('R3', 'the shared judgement drops PRE_DEPARTURE_SAFETY_NOT_VALID',
     'string.Equals(runtime.BlockReasonCode, LoadCorrectionInProgressReason, StringComparison.Ordinal) ||\n'
     '        string.Equals(runtime.BlockReasonCode, PreDepartureSafetyNotValidReason, StringComparison.Ordinal);',
     'string.Equals(runtime.BlockReasonCode, LoadCorrectionInProgressReason, StringComparison.Ordinal);',
     ['AFailedAdvanceLeavesACodeThatNamesAWaitOnAPersonAsItIs(code: "PRE_DEPARTURE_SAFETY_NOT_VALID", stage: AwaitingDepartureSafety)',
      'ASilentSessionDoesNotOverwriteACodeThatNamesAWaitOnAPerson(code: "PRE_DEPARTURE_SAFETY_NOT_VALID")']),
]

original = open(ENGINE, 'rb').read()
text = original.decode('utf-8')
nl = '\r\n' if '\r\n' in text else '\n'
report = []
all_ok = True
try:
    for key, what, old, new, expected in MUTATIONS:
        old_n = old.replace('\n', nl)
        new_n = new.replace('\n', nl)
        count = text.count(old_n)
        if count != 1:
            report.append(f'{key}: replacement matched {count} times, aborting')
            all_ok = False
            break
        open(ENGINE, 'wb').write(text.replace(old_n, new_n).encode('utf-8'))
        log = os.path.join(OUTDIR, f'{key}.log')
        with open(log, 'wb') as fh:
            rc = subprocess.run(
                ['dotnet', 'test', r'tests\ControlServer.Tests\ControlServer.Tests.csproj', '-c', 'Release',
                 '--filter', FILTER],
                cwd=ROOT, stdout=fh, stderr=subprocess.STDOUT).returncode
        out = open(log, encoding='utf-8', errors='replace').read()
        open(ENGINE, 'wb').write(original)
        restored = open(ENGINE, 'rb').read() == original
        if re.search(r'error CS\d+', out):
            report.append(f'{key}: COMPILE ERROR, see {log}; restored byte-identical: {restored}')
            all_ok = False
            continue
        failed = sorted(set(m.group(1).strip() for m in re.finditer(
            r'^\s+Failed ControlServer\.Tests\.\w+\.(.+?) \[', out, re.M)))
        summary = re.findall(r'(Failed!|Passed!)\s+-\s+Failed:\s+(\d+), Passed:\s+(\d+)', out)
        match = failed == sorted(expected)
        all_ok &= match and restored
        report.append(f'{key}: {what}\n  exit={rc} summary={summary}\n  expected red={sorted(expected)}\n'
                      f'  actual red={failed}\n  matches expectation: {match}\n  restored byte-identical: {restored}')
finally:
    if open(ENGINE, 'rb').read() != original:
        open(ENGINE, 'wb').write(original)
        report.append('final safety restore performed')

text_out = '\n'.join(report) + f'\nALL AS EXPECTED: {all_ok}\n'
open(os.path.join(OUTDIR, 'report.txt'), 'w', encoding='utf-8', newline='\n').write(text_out)
print(text_out)
