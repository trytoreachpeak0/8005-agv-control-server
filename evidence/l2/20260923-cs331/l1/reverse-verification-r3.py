"""cs#331 第二轮审查之后的反向验证：两条基线（旧提交）与 D1-D5、N1-N3 变异。

每一项先写下预期变红的用例；替换必须恰好匹配一次，否则整轮停下；每一项之后按字节还原，结束时核对。
"""
import io
import os
import re
import subprocess
import sys

WT = r'C:\Users\szy\Desktop\8005-workspace-v2\worktrees\cs331-8005-agv-control-server'
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'run3')
os.makedirs(OUT, exist_ok=True)


def path(rel):
    return os.path.join(WT, rel.replace('/', os.sep))


STORE = 'src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs'
ENGINE = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs'
PUBLISHER = 'src/ControlServer.Host/Transport/OnboardJourneyPublisher.cs'
DASHBOARD = 'src/ControlServer.Host/Dashboard/BlockedJourneysQueryEndpoint.cs'
PUBLISHER_TESTS = 'tests/ControlServer.Tests/OnboardJourneyPublisherTests.cs'
DASHBOARD_TESTS = 'tests/ControlServer.Tests/Batch7CargoHoldingDashboardTests.cs'
FILES = [STORE, ENGINE, PUBLISHER, DASHBOARD, PUBLISHER_TESTS, DASHBOARD_TESTS]

A = 'ArrivalPublishInterruptedThenReconnectedTests.'
P = 'OnboardJourneyPublisherTests.'
PD = 'PickupDispatchPlanPastOwnOrderTests.'
IMMEDIATE = A + 'TheEntryRequestReachesTheVehicleAfterAReconnectInterruptedTheArrivalPublish'
CLOSED_GATE = A + 'TheEntryRequestReachesTheVehicleWhenARoundBehindTheClosedGateVoidedTheWaitBeforeTheReconnect'
BEYOND_DEADLINE = A + 'AnAcknowledgedWorklistThatDiffersBeyondItsDeadlineIsStillRefusedAndTheBoardSaysSo'
CLEARED = A + 'TheFailedAdvanceCodeIsClearedByAWaitingRoundThatGetsThroughAgain'
BLOCKED = A + 'ABlockedJourneyKeepsTheCodeNamingItsRecoveryWhenItsRoundFails'
OWN_ORDER = A + 'OnTheOwnOrderThreeFailedReplaysKeepTheSessionNotReadyCodeAndItsStart'
P_SAME = P + 'AnAcknowledgedSnapshotRepublishedUnchangedIntoANewGenerationIsLeftAsAcknowledged'
P_DIFF = P + 'AnAcknowledgedSnapshotRepublishedWithDifferentContentIsStillRefused'
P_OLDER = P + 'AnAcknowledgedSnapshotRepublishedIntoAnOlderGenerationIsStillRefused'


def row(code, stage):
    return A + f'AFailedAdvanceLeavesACodeThatNamesAWaitOnAPersonAsItIs(code: "{code}", stage: {stage})'


ALL_ROWS = {row(c, s) for c, s in [
    ('VEHICLE_ORDER_FAILED', 'AwaitingPickupArrival'), ('ORDER_HANG', 'AwaitingPickupArrival'),
    ('ORDER_STATE_UNRECOGNIZED', 'AwaitingPickupArrival'), ('ORDER_ENDED_WITHOUT_ARRIVAL', 'AwaitingPickupArrival'),
    ('TASK_TYPE_NOT_ALLOWED_AT_STATION', 'AwaitingGateArrival')]}

FILTER_ALL = ('FullyQualifiedName~ArrivalPublishInterruptedThenReconnectedTests|'
              'FullyQualifiedName~OnboardJourneyPublisherTests|'
              'FullyQualifiedName~PickupDispatchPlanPastOwnOrderTests|'
              'FullyQualifiedName~OnboardSessionLostBlockTests')
FILTER_ARRIVAL = 'FullyQualifiedName~ArrivalPublishInterruptedThenReconnectedTests'

SKIP_CONDITION = ('        if (keepAcknowledgedIgnoring is not null &&\n'
                  '            existing is { AcknowledgedAt: not null } &&\n'
                  '            string.Equals(existing.MessageType, messageType, StringComparison.Ordinal) &&\n'
                  '            SaysTheSameIgnoring(existing.PayloadJson, candidateWire, keepAcknowledgedIgnoring))')

# (名字, 编辑列表, 筛选, 预期变红集合或 None=只记录)
# 编辑：('text', 文件, 旧, 新) 或 ('rev', 文件, 提交)
MUTATIONS = [
    ('B0 af01fd27 implementation', [('rev', f, 'af01fd27') for f in FILES], FILTER_ARRIVAL, None),
    ('B1 62d5c560 implementation', [('rev', f, '62d5c560') for f in FILES], FILTER_ARRIVAL, None),
    ('D1 no reuse of the acknowledged row',
     [('text', PUBLISHER, SKIP_CONDITION,
       SKIP_CONDITION.replace('if (keepAcknowledgedIgnoring is not null &&', 'if (Environment.TickCount64 < 0 && keepAcknowledgedIgnoring is not null &&'))],
     FILTER_ALL, {IMMEDIATE, CLOSED_GATE, CLEARED, P_SAME}),
    ('D2 reuse whenever acknowledged, content-blind',
     [('text', PUBLISHER, '            SaysTheSameIgnoring(existing.PayloadJson, candidateWire, keepAcknowledgedIgnoring))',
       '            (Environment.TickCount64 >= 0 || SaysTheSameIgnoring(existing.PayloadJson, candidateWire, keepAcknowledgedIgnoring)))')],
     FILTER_ALL, {BEYOND_DEADLINE, P_DIFF, P_OLDER}),
    ('D3 worklist deadline not ignored',
     [('text', ENGINE, 'new HashSet<string>(["stationDepartureDeadlineAt"], StringComparer.Ordinal);',
       'new HashSet<string>([], StringComparer.Ordinal);')],
     FILTER_ALL, {CLOSED_GATE}),
    ('D4 older generation allowed',
     [('text', PUBLISHER, '        if (Generation(candidateWire) < Generation(storedWire))',
       '        if (Environment.TickCount64 < 0 && Generation(candidateWire) < Generation(storedWire))')],
     FILTER_ALL, {P_OLDER}),
    ('D5 reuse unacknowledged rows too',
     [('text', PUBLISHER, '            existing is { AcknowledgedAt: not null } &&', '            existing is not null &&')],
     FILTER_ALL, set()),
    ('N1 NameFailedAdvance ignores the shared wait predicate',
     [('text', ENGINE, '                CarriesACodeThatNamesAWaitOnAPerson(current) ||\n', '')],
     FILTER_ALL, {BLOCKED} | ALL_ROWS),
    ('N2 NOT_READY overwritten even when the session is not ready',
     [('text', ENGINE,
       '                (string.Equals(current.BlockReasonCode, JourneyWaitClassification.SessionNotReadyReason, StringComparison.Ordinal) &&',
       '                (Environment.TickCount64 < 0 && string.Equals(current.BlockReasonCode, JourneyWaitClassification.SessionNotReadyReason, StringComparison.Ordinal) &&')],
     FILTER_ALL, {OWN_ORDER}),
    ('N3 shared predicate forgets VEHICLE_ORDER_FAILED',
     [('text', ENGINE,
       '        string.Equals(runtime.BlockReasonCode, VehicleFaultEvidence.OrderFailed, StringComparison.Ordinal) ||\n'
       '        IsStalledOrderReason(runtime.BlockReasonCode) ||',
       '        IsStalledOrderReason(runtime.BlockReasonCode) ||')],
     FILTER_ALL, {row('VEHICLE_ORDER_FAILED', 'AwaitingPickupArrival')}),
]

backup = {f: io.open(path(f), 'rb').read() for f in FILES}
report = []
try:
    for name, edits, test_filter, expected in MUTATIONS:
        for f, b in backup.items():
            io.open(path(f), 'wb').write(b)
        for edit in edits:
            if edit[0] == 'rev':
                _, f, rev = edit
                blob = subprocess.run(['git', 'show', f'{rev}:{f}'], cwd=WT, capture_output=True, check=True).stdout
                io.open(path(f), 'wb').write(blob)
            else:
                _, f, old, new = edit
                text = io.open(path(f), 'rb').read().decode('utf-8')
                n = text.count(old)
                if n != 1:
                    print(f'{name}: pattern matched {n} times in {f}; stop')
                    sys.exit(2)
                io.open(path(f), 'wb').write(text.replace(old, new).encode('utf-8'))
        r = subprocess.run(['dotnet', 'test', r'tests\ControlServer.Tests\ControlServer.Tests.csproj', '--nologo',
                            '--filter', test_filter],
                           cwd=WT, capture_output=True, text=True, encoding='utf-8', errors='replace')
        out = r.stdout + r.stderr
        io.open(os.path.join(OUT, name.split()[0] + '.log'), 'w', encoding='utf-8').write(out)
        if re.search(r'\berror CS\d+|Build FAILED', out):
            report.append(f'{name}: BUILD FAILED')
            continue
        summary = re.findall(r'(Passed!|Failed!)\s+- Failed:\s+(\d+), Passed:\s+(\d+)', out)
        failed = {m.strip() for m in re.findall(r'\[xUnit\.net [^\]]*\]\s+ControlServer\.Tests\.(.+?) \[FAIL\]', out)}
        if expected is None:
            report.append(f'{name}: RECORDED summary={summary} red={sorted(failed)}')
        else:
            verdict = 'MATCH' if failed == expected else 'MISMATCH'
            report.append(f'{name}: {verdict} summary={summary} red={sorted(failed)} expected={sorted(expected)}')
finally:
    for f, b in backup.items():
        io.open(path(f), 'wb').write(b)
    restored = all(io.open(path(f), 'rb').read() == b for f, b in backup.items())
    report.append(f'restored byte-identical: {restored}')
    io.open(os.path.join(OUT, 'report.txt'), 'w', encoding='utf-8').write('\n'.join(report) + '\n')
    print('\n'.join(report))
