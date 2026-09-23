"""cs#331 第三轮审查之后的反向验证：基线 B2（046a7b72 实现）、E1-E5（本轮新改动）、D1-D5 与 N1-N3（按新用例更新预期）。

每一项先写下预期变红的用例；替换必须恰好匹配一次，否则整轮停下；每一项之后按字节还原，结束时核对。
"""
import io
import os
import re
import subprocess
import sys

WT = r'C:\Users\szy\Desktop\8005-workspace-v2\worktrees\cs331-8005-agv-control-server'
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'run4')
os.makedirs(OUT, exist_ok=True)


def path(rel):
    return os.path.join(WT, rel.replace('/', os.sep))


STORE = 'src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs'
ENGINE = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs'
PUBLISHER = 'src/ControlServer.Host/Transport/OnboardJourneyPublisher.cs'
DASHBOARD = 'src/ControlServer.Host/Dashboard/BlockedJourneysQueryEndpoint.cs'
FILES = [STORE, ENGINE, PUBLISHER, DASHBOARD]

A = 'ArrivalPublishInterruptedThenReconnectedTests.'
P = 'OnboardJourneyPublisherTests.'
S = 'OnboardSessionLostBlockTests.'
IMMEDIATE = A + 'TheEntryRequestReachesTheVehicleAfterAReconnectInterruptedTheArrivalPublish'
CLOSED_GATE = A + 'TheEntryRequestReachesTheVehicleWhenARoundBehindTheClosedGateVoidedTheWaitBeforeTheReconnect'
BEYOND_DEADLINE = A + 'AnAcknowledgedWorklistThatDiffersBeyondItsDeadlineIsStillRefusedAndTheBoardSaysSo'
CLEARED = A + 'TheFailedAdvanceCodeIsClearedByAWaitingRoundThatGetsThroughAgain'
BLOCKED = A + 'ABlockedJourneyKeepsTheCodeNamingItsRecoveryWhenItsRoundFails'
OWN_ORDER = A + 'OnTheOwnOrderThreeFailedReplaysKeepTheSessionNotReadyCodeAndItsStart'
TWICE = A + 'TheEntryRequestReachesTheVehicleAfterASecondDisconnectLandsOnItOnceThePlanWasAcknowledged'
CUT_WORKLIST = A + 'ACutOnTheWorklistFailsOneRoundThenRecoversOnceTheVehicleConfirmsTheReplayedWorklist'
UNRELATED = A + 'OnTheOwnOrderAFailureUnrelatedToTheConnectionStillNamesItself'
P_SAME = P + 'AnAcknowledgedSnapshotRepublishedUnchangedIntoANewGenerationIsLeftAsAcknowledged'
P_DIFF = P + 'AnAcknowledgedSnapshotRepublishedWithDifferentContentIsStillRefused'
P_OLDER = P + 'AnAcknowledgedSnapshotRepublishedIntoAnOlderGenerationIsStillRefused'
SILENT_EXISTING = S + 'ASilentSessionDoesNotOverwriteAFailedOrderAsTheReason'


def row(code, stage):
    return A + f'AFailedAdvanceLeavesACodeThatNamesAWaitOnAPersonAsItIs(code: "{code}", stage: {stage})'


def silent(code):
    return S + f'ASilentSessionDoesNotOverwriteACodeThatNamesAWaitOnAPerson(code: "{code}")'


ALL_ROWS = {row(c, s) for c, s in [
    ('VEHICLE_ORDER_FAILED', 'AwaitingPickupArrival'), ('ORDER_HANG', 'AwaitingPickupArrival'),
    ('ORDER_STATE_UNRECOGNIZED', 'AwaitingPickupArrival'), ('ORDER_ENDED_WITHOUT_ARRIVAL', 'AwaitingPickupArrival'),
    ('TASK_TYPE_NOT_ALLOWED_AT_STATION', 'AwaitingGateArrival'),
    ('STATION_TIMEOUT_DOOR_NOT_CLOSED', 'AwaitingLoadResult'),
    ('LOAD_CORRECTION_IN_PROGRESS', 'AwaitingStationDeparture')]}
NEW_ROWS = {row('STATION_TIMEOUT_DOOR_NOT_CLOSED', 'AwaitingLoadResult'),
            row('LOAD_CORRECTION_IN_PROGRESS', 'AwaitingStationDeparture')}
NEW_SILENT = {silent('STATION_TIMEOUT_DOOR_NOT_CLOSED'), silent('LOAD_CORRECTION_IN_PROGRESS')}

FILTER_ALL = ('FullyQualifiedName~ArrivalPublishInterruptedThenReconnectedTests|'
              'FullyQualifiedName~OnboardJourneyPublisherTests|'
              'FullyQualifiedName~PickupDispatchPlanPastOwnOrderTests|'
              'FullyQualifiedName~OnboardSessionLostBlockTests')

SKIP_CONDITION = ('        if (keepAcknowledgedIgnoring is not null &&\n'
                  '            existing is { AcknowledgedAt: not null } &&\n'
                  '            string.Equals(existing.MessageType, messageType, StringComparison.Ordinal) &&\n'
                  '            SaysTheSameIgnoring(existing.PayloadJson, candidateWire, keepAcknowledgedIgnoring))')
PLAN_OPT_IN = ('            cancellationToken,\n'
               '            keepAcknowledgedIgnoring: NothingButTheEnvelope).ConfigureAwait(false);\n'
               '        await PublishEntryRequestAsync(')
PLAN_NO_OPT_IN = ('            cancellationToken).ConfigureAwait(false);\n'
                  '        await PublishEntryRequestAsync(')
ENTRY_OPT_IN = '            runtime, stops, session, cancellationToken, keepAcknowledgedIgnoring: NothingButTheEnvelope)'
ENTRY_NO_OPT_IN = '            runtime, stops, session, cancellationToken)'
TWO_STATION_CODES = (' ||\n'
                     '        string.Equals(runtime.BlockReasonCode, StationTimeoutDoorNotClosedReason, StringComparison.Ordinal) ||\n'
                     '        string.Equals(runtime.BlockReasonCode, LoadCorrectionInProgressReason, StringComparison.Ordinal);')

MUTATIONS = [
    ('B2 046a7b72 implementation', [('rev', f, '046a7b72') for f in FILES],
     {TWICE, UNRELATED} | NEW_ROWS | NEW_SILENT),
    ('E1 arrival plan without the opt-in', [('text', ENGINE, PLAN_OPT_IN, PLAN_NO_OPT_IN)], {TWICE}),
    ('E2 arrival plan and entry request without the opt-in',
     [('text', ENGINE, PLAN_OPT_IN, PLAN_NO_OPT_IN), ('text', ENGINE, ENTRY_OPT_IN, ENTRY_NO_OPT_IN)], {TWICE}),
    ('E3 entry request without the opt-in', [('text', ENGINE, ENTRY_OPT_IN, ENTRY_NO_OPT_IN)], set()),
    ('E4 shared predicate without the two station codes', [('text', ENGINE, TWO_STATION_CODES, ';')],
     NEW_ROWS | NEW_SILENT),
    ('E5 NOT_READY kept whatever the failure',
     [('text', ENGINE, '                 IsTransportFailure(failure) &&\n', '')], {UNRELATED}),
    ('D1 no reuse of the acknowledged row',
     [('text', PUBLISHER, SKIP_CONDITION,
       SKIP_CONDITION.replace('if (keepAcknowledgedIgnoring is not null &&',
                              'if (Environment.TickCount64 < 0 && keepAcknowledgedIgnoring is not null &&'))],
     {IMMEDIATE, CLOSED_GATE, CLEARED, P_SAME, TWICE, CUT_WORKLIST}),
    ('D2 reuse whenever acknowledged, content-blind',
     [('text', PUBLISHER, '            SaysTheSameIgnoring(existing.PayloadJson, candidateWire, keepAcknowledgedIgnoring))',
       '            (Environment.TickCount64 >= 0 || SaysTheSameIgnoring(existing.PayloadJson, candidateWire, keepAcknowledgedIgnoring)))')],
     {BEYOND_DEADLINE, P_DIFF, P_OLDER}),
    ('D3 worklist deadline not ignored',
     [('text', ENGINE, 'new HashSet<string>(["stationDepartureDeadlineAt"], StringComparer.Ordinal);',
       'new HashSet<string>([], StringComparer.Ordinal);')],
     {CLOSED_GATE, CUT_WORKLIST}),
    ('D4 older generation allowed',
     [('text', PUBLISHER, '        if (Generation(candidateWire) < Generation(storedWire))',
       '        if (Environment.TickCount64 < 0 && Generation(candidateWire) < Generation(storedWire))')],
     {P_OLDER}),
    ('D5 reuse unacknowledged rows too',
     [('text', PUBLISHER, '            existing is { AcknowledgedAt: not null } &&', '            existing is not null &&')],
     {CUT_WORKLIST}),
    ('N1 NameFailedAdvance ignores the shared wait predicate',
     [('text', ENGINE, '                CarriesACodeThatNamesAWaitOnAPerson(current) ||\n', '')],
     {BLOCKED} | ALL_ROWS),
    ('N2 NOT_READY overwritten even when the session is not ready',
     [('text', ENGINE,
       '                (string.Equals(current.BlockReasonCode, JourneyWaitClassification.SessionNotReadyReason, StringComparison.Ordinal) &&',
       '                (Environment.TickCount64 < 0 && string.Equals(current.BlockReasonCode, JourneyWaitClassification.SessionNotReadyReason, StringComparison.Ordinal) &&')],
     {OWN_ORDER}),
    ('N3 shared predicate forgets VEHICLE_ORDER_FAILED',
     [('text', ENGINE,
       '        string.Equals(runtime.BlockReasonCode, VehicleFaultEvidence.OrderFailed, StringComparison.Ordinal) ||\n'
       '        IsStalledOrderReason(runtime.BlockReasonCode) ||',
       '        IsStalledOrderReason(runtime.BlockReasonCode) ||')],
     {row('VEHICLE_ORDER_FAILED', 'AwaitingPickupArrival'), SILENT_EXISTING}),
]

backup = {f: io.open(path(f), 'rb').read() for f in FILES}
report = []
try:
    for name, edits, expected in MUTATIONS:
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
                            '--filter', FILTER_ALL],
                           cwd=WT, capture_output=True, text=True, encoding='utf-8', errors='replace')
        out = r.stdout + r.stderr
        io.open(os.path.join(OUT, name.split()[0] + '.log'), 'w', encoding='utf-8').write(out)
        if re.search(r'\berror CS\d+|Build FAILED', out):
            report.append(f'{name}: BUILD FAILED')
            continue
        summary = re.findall(r'(Passed!|Failed!)\s+- Failed:\s+(\d+), Passed:\s+(\d+)', out)
        failed = {m.strip() for m in re.findall(r'\[xUnit\.net [^\]]*\]\s+ControlServer\.Tests\.(.+?) \[FAIL\]', out)}
        verdict = 'MATCH' if failed == expected else 'MISMATCH'
        report.append(f'{name}: {verdict} summary={summary} red={sorted(failed)} expected={sorted(expected)}')
finally:
    for f, b in backup.items():
        io.open(path(f), 'wb').write(b)
    restored = all(io.open(path(f), 'rb').read() == b for f, b in backup.items())
    report.append(f'restored byte-identical: {restored}')
    io.open(os.path.join(OUT, 'report.txt'), 'w', encoding='utf-8').write('\n'.join(report) + '\n')
    print('\n'.join(report))
