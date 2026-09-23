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
]
only = sys.argv[1:]
for name, path, old, new, flt in M:
    if only and name not in only: continue
    src = io.open(path, encoding='utf-8', newline='').read()
    n = src.count(old)
    if n != 1:
        print(f"{name}: MATCHED {n} TIMES, SKIPPED"); continue
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
