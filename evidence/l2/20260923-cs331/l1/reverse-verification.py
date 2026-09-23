import io, os, re, shutil, subprocess, sys
WT = r'C:\Users\szy\Desktop\8005-workspace-v2\worktrees\cs331-8005-agv-control-server'
OUT = os.path.dirname(os.path.abspath(__file__))
STORE = os.path.join(WT, r'src\ControlServer.Infrastructure\Persistence\WireToGateStore.cs')
ENGINE = os.path.join(WT, r'src\ControlServer.Host\Runtime\JourneyRuntimeEngine.cs')
A = 'ArrivalPublishInterruptedThenReconnectedTests.'
P = 'OnboardJourneyPublisherTests.'
GUARD = P + 'AnAcknowledgedSnapshotRepublishedUnchangedIntoANewGenerationIsLeftAsAcknowledged'
ACK_DIFF = P + 'AnAcknowledgedSnapshotRepublishedWithDifferentContentIsStillRefused'
ACK_OLDER = P + 'AnAcknowledgedSnapshotRepublishedIntoAnOlderGenerationIsStillRefused'
UNACK_DIFF = P + 'AnUnacknowledgedSnapshotRepublishedWithDifferentContentIsStillRefused'
UNACK_REWRITE = P + 'UnchangedSnapshotRepublishesIntoAnAdvancingSessionGeneration'
OWN = A + 'OnTheOwnOrderAFailedReplayNamesItselfAndTheNextRoundGoesBackToSessionNotReady'
PAST_OWN_RECONNECT = 'PickupDispatchPlanPastOwnOrderTests.AReconnectStillOnTheOwnOrderGetsThePlanReplayedOnlyOnceItsHandshakeIsDone'
SKIP = '        if (existing.AcknowledgedAt is not null && sameSemanticMessage && candidateGeneration > storedGeneration)\n        {\n            return;\n        }\n'
COND = 'existing.AcknowledgedAt is not null && sameSemanticMessage && candidateGeneration > storedGeneration'
MUTATIONS = [
  ('M1 revert fix: no acked skip', [(STORE, SKIP, '')],
   {A+'TheEntryRequestReachesTheVehicleAfterAReconnectInterruptedTheArrivalPublish', A+'TheFailedAdvanceCodeIsClearedByAWaitingRoundThatGetsThroughAgain', GUARD}),
  ('M2 skip drops sameSemantic', [(STORE, COND, 'existing.AcknowledgedAt is not null && candidateGeneration > storedGeneration')], {ACK_DIFF}),
  ('M3 skip drops generation advance', [(STORE, COND, 'existing.AcknowledgedAt is not null && sameSemanticMessage')], {ACK_OLDER}),
  ('M9 skip drops acknowledged', [(STORE, COND, 'sameSemanticMessage && candidateGeneration > storedGeneration')], {UNACK_REWRITE, OWN, PAST_OWN_RECONNECT}),
  ('M10 skip only needs generation advance', [(STORE, COND, 'candidateGeneration > storedGeneration')], {UNACK_REWRITE, OWN, PAST_OWN_RECONNECT, ACK_DIFF, UNACK_DIFF}),
  ('M4 no NameFailedAdvance', [(ENGINE, '                await NameFailedAdvanceAsync(runtime, cancellationToken).ConfigureAwait(false);\n',
                                 '                if (Environment.TickCount64 < 0) await NameFailedAdvanceAsync(runtime, cancellationToken).ConfigureAwait(false);\n')],
   {A+'AFailedAdvanceNamesItselfOnTheBoardInsteadOfTheCodeLeftBeforeIt', A+'TheFailedAdvanceKeepsTheTimeItStartedWhileEveryRoundFailsTheSameWay',
    A+'TheFailedAdvanceCodeIsClearedByAWaitingRoundThatGetsThroughAgain', A+'OnTheOwnOrderAFailedReplayNamesItselfAndTheNextRoundGoesBackToSessionNotReady'}),
  ('M5 NameFailedAdvance overwrites Blocked', [(ENGINE, 'current.Stage is JourneyRuntimeStage.Blocked or JourneyRuntimeStage.Completed ||', 'current.Stage is JourneyRuntimeStage.Completed ||')],
   {A+'ABlockedJourneyKeepsTheCodeNamingItsRecoveryWhenItsRoundFails'}),
  ('M6 no ClearFailedAdvance', [(ENGINE, '            await ClearFailedAdvanceAsync(runtime, cancellationToken).ConfigureAwait(false);\n',
                                  '            if (Environment.TickCount64 < 0) await ClearFailedAdvanceAsync(runtime, cancellationToken).ConfigureAwait(false);\n')],
   {A+'TheFailedAdvanceCodeIsClearedByAWaitingRoundThatGetsThroughAgain'}),
  ('M7 repeated failure restarts since', [
      (ENGINE, '                string.Equals(current.BlockReasonCode, AdvanceFailedReason, StringComparison.Ordinal))', '                Environment.TickCount64 < 0)'),
      (ENGINE, '            current.SetBlockReason(AdvanceFailedReason, now);', '            current.SetBlockReason(null, now);\n            current.SetBlockReason(AdvanceFailedReason, now);')],
   {A+'TheFailedAdvanceKeepsTheTimeItStartedWhileEveryRoundFailsTheSameWay'}),
  ('M8 Clear clears any code', [(ENGINE, '        if (!string.Equals(runtime.BlockReasonCode, AdvanceFailedReason, StringComparison.Ordinal))\n        {\n            return;\n        }\n\n        if (dbContext.ChangeTracker',
                                   '        if (runtime.BlockReasonCode is null)\n        {\n            return;\n        }\n\n        if (dbContext.ChangeTracker')],
   {A+'OnTheOwnOrderAFailedReplayNamesItselfAndTheNextRoundGoesBackToSessionNotReady'}),
]
files = {STORE, ENGINE}
backup = {f: io.open(f, 'rb').read() for f in files}
for f, b in backup.items():
    shutil.copyfile(f, os.path.join(OUT, os.path.basename(f) + '.orig'))
report = []
try:
    for name, edits, expected in MUTATIONS:
        for f, b in backup.items():
            io.open(f, 'wb').write(b)
        for f, old, new in edits:
            text = io.open(f, 'rb').read().decode('utf-8')
            n = text.count(old)
            if n != 1:
                print(f'{name}: pattern matched {n} times in {os.path.basename(f)}; stop'); sys.exit(2)
            io.open(f, 'wb').write(text.replace(old, new).encode('utf-8'))
        r = subprocess.run(['dotnet', 'test', r'tests\ControlServer.Tests\ControlServer.Tests.csproj', '--nologo',
                            '--filter', 'FullyQualifiedName~ArrivalPublishInterruptedThenReconnectedTests|FullyQualifiedName~OnboardJourneyPublisherTests|FullyQualifiedName~PickupDispatchPlanPastOwnOrderTests'],
                           cwd=WT, capture_output=True, text=True, encoding='utf-8', errors='replace')
        out = r.stdout + r.stderr
        io.open(os.path.join(OUT, name.split()[0] + '.log'), 'w', encoding='utf-8').write(out)
        if re.search(r'\berror CS\d+|Build FAILED', out):
            report.append(f'{name}: BUILD FAILED'); continue
        summary = re.findall(r'(Passed!|Failed!)\s+- Failed:\s+(\d+), Passed:\s+(\d+)', out)
        failed = set(re.findall(r'\] ControlServer\.Tests\.(\S+) \[FAIL\]', out)) | set(re.findall(r'ControlServer\.Tests\.(\S+) \[FAIL\]', out))
        verdict = 'MATCH' if failed == expected else 'MISMATCH'
        report.append(f'{name}: {verdict} summary={summary} red={sorted(failed)} expected={sorted(expected)}')
finally:
    for f, b in backup.items():
        io.open(f, 'wb').write(b)
    restored = all(io.open(f, 'rb').read() == b for f, b in backup.items())
    report.append(f'restored byte-identical: {restored}')
    io.open(os.path.join(OUT, 'report.txt'), 'w', encoding='utf-8').write('\n'.join(report) + '\n')
    print('\n'.join(report))
