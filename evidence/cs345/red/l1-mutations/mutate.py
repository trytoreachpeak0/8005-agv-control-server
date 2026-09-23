import io, sys, subprocess, shutil, os, re
root = r'C:/Users/szy/Desktop/8005-workspace-v2/worktrees/cs345-8005-agv-control-server'
os.chdir(root)
# name, file, old, new, filter
M = [
 ("M1-window-not-restarted", "src/ControlServer.Host/Runtime/OwnOrderRebuilds.cs",
  "            stopped.IncidentAt = now;\n", "", "FullyQualifiedName~StoppedRebuildExitTests"),
 ("M2-not-only-stopped", "src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs",
  "            _ => ExitNotStoppedReason,\n", "            _ => null,\n", "FullyQualifiedName~StoppedRebuildExitTests"),
 ("M3-no-idempotency", "src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs",
  "            await LastRebuildWasAPersonsAsync(runtime, cancellationToken).ConfigureAwait(false))", "            false)", "FullyQualifiedName~StoppedRebuildExitTests"),
 ("M4-old-snapshot-counts", "src/ControlServer.Host/Runtime/OwnOrderRebuilds.cs",
  "            stopped.RecordedAt = now;\n", "", "FullyQualifiedName~StoppedRebuildExitTests"),
 ("M5-delay-waited", "src/ControlServer.Host/Runtime/OwnOrderRebuilds.cs",
  "            stopped.DueAt = now;\n", "            stopped.DueAt = now + TimeSpan.FromSeconds(30);\n", "FullyQualifiedName~StoppedRebuildExitTests"),
 ("M6-cargo-stop-not-refused", "src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs",
  "                ExitCargoNotInPlaceReason,\n", "                null,\n", "FullyQualifiedName~StoppedRebuildExitTests"),
 ("N1-live-binding-ignored", "src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs",
  "                     await faults.ReadLiveCargoAsync(agvId, cancellationToken).ConfigureAwait(false) is not null)", "                     false)", "FullyQualifiedName~StoppedRebuildExitTests"),
 ("N2-memberships-ignored", "src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs",
  "            else if (memberships.Any(row => row.Status is not", "            else if (false && memberships.Any(row => row.Status is not", "FullyQualifiedName~StoppedRebuildExitTests"),
 ("N3-riot-not-read", "src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs",
  "            reasons.AddRange(VehicleOrderReasons(orders));\n", "", "FullyQualifiedName~StoppedRebuildExitTests"),
 ("N4-no-giveup-idempotency", "src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs",
  "                await LastJourneyWasGivenUpAsync(agvId, cancellationToken).ConfigureAwait(false))", "                false)", "FullyQualifiedName~StoppedRebuildExitTests"),
 ("N5-closure-not-sent", "src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs",
  "        await JourneyClosure.SendAsync(publisher, dbContext, agvId, cancellationToken).ConfigureAwait(false);\n", "        _ = publisher;\n", "FullyQualifiedName~StoppedRebuildExitTests|FullyQualifiedName~JourneyClosureSingleExitArchitectureTests"),
 ('H1-readiness-input-removed', 'src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs',
  '                     !cargoHandoffAwaited &&\n', '', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('H2-readiness-any-blocked', 'src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs',
  ' &&\n                           journey.BlockReasonCode == AwaitingCargoHandoffJourneyReason,', ',', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('H3-readiness-any-vehicle', 'src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs',
  'journey => journey.AgvId == agvId && journey.Stage == JourneyRuntimeStage.Blocked', 'journey => journey.Stage == JourneyRuntimeStage.Blocked', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('H4-journey-not-blocked', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  '        runtime.Stage = JourneyRuntimeStage.Blocked;\n', '', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('H5-binding-not-released', 'src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs',
  '            cargo.ReleasedAt = now;\n', '', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('H6-claim-not-extended', 'src/ControlServer.Host/Runtime/OwnOrderRebuilds.cs',
  ' ||\n                           row.State == OwnOrderRebuildStates.AwaitingCargoHandoff) &&\n                          (row.CargoEvidenceRequestedGeneration != generation ||', ') &&\n                          (row.CargoEvidenceRequestedGeneration != generation ||', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('H7-nothing-on-board-allowed', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  '            refusal = ExitNothingOnBoardReason;\n', '            refusal = null;\n', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('N1b-live-binding-ignored', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  '        await faults.ReadLiveCargoAsync(agvId, cancellationToken).ConfigureAwait(false) is not null;', '        false;', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('N2b-memberships-ignored', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  '                       row.Status != JourneyDemandStatuses.Terminated,', '                       row.Status != JourneyDemandStatuses.Terminated && false,', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('H6a-claim-read-not-extended', 'src/ControlServer.Host/Runtime/OwnOrderRebuilds.cs',
  'row.CargoProvenAt == null) ||\n                           row.State == OwnOrderRebuildStates.AwaitingCargoHandoff) &&\n                          (row.CargoEvidenceRequestedGeneration != generation ||\n                           (ready && !row.CargoEvidenceRequestedWhileReady)))\n            .Select', 'row.CargoProvenAt == null)) &&\n                          (row.CargoEvidenceRequestedGeneration != generation ||\n                           (ready && !row.CargoEvidenceRequestedWhileReady)))\n            .Select', 'FullyQualifiedName~StoppedRebuildExitTests'),
 # ---- 独立审查（f848101c）之后的修改：M1、S1、S2、S3、S4、S5。第六个元素是替换点应当命中的次数（默认 1）。 ----
 # 就绪按记录判（M1），取代上面的 H2、H3（它们改的那段代码已不存在，所以匹配 0 次）。
 ('H2r-readiness-ignores-record', 'src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs',
  'record => record.AgvId == agvId && record.State == OwnOrderRebuildStates.AwaitingCargoHandoff &&', 'record => record.AgvId == agvId &&', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('H3r-readiness-any-vehicle', 'src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs',
  'record => record.AgvId == agvId && record.State', 'record => record.State', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('H2b-readiness-ignores-blocked', 'src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs',
  'journey => journey.JourneyId == record.JourneyId && journey.Stage == JourneyRuntimeStage.Blocked),', 'journey => journey.JourneyId == record.JourneyId),', 'FullyQualifiedName~StoppedRebuildExitTests'),
 # 快照请求（S3、以及取代 H6、H6a）：两处查询同改。
 ('S3-claim-ignores-blocked', 'src/ControlServer.Host/Runtime/OwnOrderRebuilds.cs',
  'journey => journey.JourneyId == row.JourneyId && journey.Stage == JourneyRuntimeStage.Blocked))) &&', 'journey => journey.JourneyId == row.JourneyId))) &&', 'FullyQualifiedName~StoppedRebuildExitTests', 2),
 ('H6r-claim-not-extended', 'src/ControlServer.Host/Runtime/OwnOrderRebuilds.cs',
  '(row.State == OwnOrderRebuildStates.AwaitingCargoHandoff &&', '(false &&', 'FullyQualifiedName~StoppedRebuildExitTests', 2),
 # 交接没有一次成功时的出口（M1）。
 ('M1a-giveup-rejects-handoff-state', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  '            string? refusal = trip.Refusal == ExitAwaitingHandoffReason ? null : trip.Refusal;\n', '            string? refusal = trip.Refusal;\n', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('M1b-prepare-rejects-handoff-state', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  'trip.Refusal is ExitCargoNotInPlaceReason or ExitAwaitingHandoffReason ? null', 'trip.Refusal is ExitCargoNotInPlaceReason ? null', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('M1d-handoff-record-not-read', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  '        else if (runtime is { Stage: JourneyRuntimeStage.Blocked })\n', '        else if (agvId.Length < 0)\n', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('M1e-rebuild-accepts-handoff-state', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  '            { State: OwnOrderRebuildStates.AwaitingCargoHandoff } => ExitAwaitingHandoffReason,\n', '            { State: OwnOrderRebuildStates.AwaitingCargoHandoff } => null,\n', 'FullyQualifiedName~StoppedRebuildExitTests'),
 # 放弃只给窗口内二次出问题（S1），急停与故障（S5）。
 ('S1-giveup-before-confirmation', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  'if (refusal is null && trip.Stopped is { StoppedReason: OwnOrderRebuilds.EndedBeforeConfirmation })', 'if (refusal is null && agvId.Length < 0)', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('S5a-giveup-ignores-latch', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  '            reasons.AddRange(EmergencyReasons(\n                emergency, await emergencyStop.HasOpenEpisodeAsync(subject, cancellationToken).ConfigureAwait(false)));\n', '            _ = emergency;\n', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('S5b-giveup-ignores-fault', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  'if (InEffect(await faults.ReadAsync(agvId, cancellationToken).ConfigureAwait(false)))', 'if (InEffect(await faults.ReadAsync(agvId, cancellationToken).ConfigureAwait(false)) && agvId.Length < 0)', 'FullyQualifiedName~StoppedRebuildExitTests'),
 # 终结清单与署名（S2）。
 ('S2a-giveup-operator-not-kept', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  '            trip.Stopped!.OperatorId = request.OperatorId;\n            trip.Stopped.State = OwnOrderRebuildStates.Ended;\n', '            trip.Stopped!.State = OwnOrderRebuildStates.Ended;\n', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('S2b-terminated-not-returned', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  'VehicleFaultRecoveryDispositions.TripTerminated, null, terminated);', 'VehicleFaultRecoveryDispositions.TripTerminated, null);', 'FullyQualifiedName~StoppedRebuildExitTests|FullyQualifiedName~VehicleFaultRecoveryEndpointsTests'),
 ('S2c-prepare-operator-not-kept', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  '        trip.Stopped!.OperatorId = request.OperatorId;\n        trip.Stopped.State = OwnOrderRebuildStates.AwaitingCargoHandoff;\n', '        trip.Stopped!.State = OwnOrderRebuildStates.AwaitingCargoHandoff;\n', 'FullyQualifiedName~StoppedRebuildExitTests'),
 # 测试缺口（S4）。
 ('S4a-forced-mechanical-not-settled', 'src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs',
  '        if (messageType is "FaultCargoRecoveryResult" or "ForcedMechanicalRecoveryResult")\n        {\n            await SettleHandedOffCargoAsync', '        if (messageType is "FaultCargoRecoveryResult")\n        {\n            await SettleHandedOffCargoAsync', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('S4b-giveup-ignores-person', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  '        List<string> reasons = [.. PersonReasons(request)];\n        RiotVehicleEmergencyObservation emergency', '        List<string> reasons = [];\n        RiotVehicleEmergencyObservation emergency', 'FullyQualifiedName~StoppedRebuildExitTests'),
 ('S4c-prepare-ignores-person', 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.StoppedRebuild.cs',
  '        string agvId = request.Subject.AgvId;\n        List<string> reasons = [.. PersonReasons(request)];\n        using IDisposable? round', '        string agvId = request.Subject.AgvId;\n        List<string> reasons = [];\n        using IDisposable? round', 'FullyQualifiedName~StoppedRebuildExitTests'),
]
only = sys.argv[1:]
for entry in M:
    name, path, old, new, flt = entry[:5]
    want = entry[5] if len(entry) > 5 else 1
    if only and name not in only: continue
    src = io.open(path, encoding='utf-8', newline='').read()
    n = src.count(old)
    if n != want:
        print(f"{name}: MATCHED {n} TIMES (want {want}), SKIPPED"); continue
    shutil.copyfile(path, path + '.bak')
    try:
        io.open(path, 'w', encoding='utf-8', newline='').write(src.replace(old, new))
        diff = subprocess.run(['git','diff','--numstat',path],capture_output=True,text=True).stdout.strip()
        print(f"== {name}: applied ({diff})", flush=True)
        b = subprocess.run(['dotnet','build','tests/ControlServer.Tests/ControlServer.Tests.csproj','-c','Release','--no-incremental'],capture_output=True,text=True,encoding='utf-8',errors='replace')
        print('  build:', [l.strip() for l in b.stdout.splitlines() if 'Error(s)' in l or 'error CS' in l][:4])
        r = subprocess.run(['dotnet','test','tests/ControlServer.Tests/ControlServer.Tests.csproj','-c','Release','--no-build','--filter',flt],capture_output=True,text=True,encoding='utf-8',errors='replace')
        out = r.stdout + r.stderr
        hits = [l for l in out.splitlines() if l.strip().startswith('Failed ControlServer') or 'Passed!' in l or 'Failed!' in l or 'error CS' in l]
        for l in hits:
            print('  ' + l.strip()[:220])
        if not hits:
            print('  NO SUMMARY; tail:'); [print('   ', l[:200]) for l in out.splitlines()[-15:]]
    finally:
        shutil.move(path + '.bak', path)
        os.utime(path, None)
print(subprocess.run(['git','status','--short'],capture_output=True,text=True).stdout or 'clean')
