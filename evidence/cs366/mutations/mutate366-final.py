# cs#366 reverse verification: each mutation states its expected red set before it runs.
# Replacement must hit exactly the stated count; build is --no-incremental; exit codes recorded.
import os, re, shutil, subprocess, sys, datetime

WT = r"C:\Users\szy\Desktop\8005-workspace-v2\worktrees\cs366-8005-agv-control-server"
OUT = sys.argv[1]
ENGINE = r"src\ControlServer.Host\Runtime\JourneyRuntimeEngine.OwnOrderRebuild.cs"
REBUILDS = r"src\ControlServer.Host\Runtime\OwnOrderRebuilds.cs"
FILTER = ("FullyQualifiedName~OwnOrderRebuild|FullyQualifiedName~StoppedRebuildExitTests|"
          "FullyQualifiedName~EmergencyReleaseVersusOwnOrderRebuildTests|FullyQualifiedName~VehicleFaultRecovery|"
          "FullyQualifiedName~FailedOrderBeforeConfirmationTests")
P = "ControlServer.Tests."

# Cases added after the first two rounds (review M1, M2 and the coordinator's extra cells).
M1_HELD = {P + 'OwnOrderRebuildCargoProofTests.ADecidedRebuildNeverSentThatIsHeldOnceIsAskedForAFreshSnapshotAndRebuilt(hold: "' + h + '")'
           for h in ("riot-unreadable", "emergency", "session-not-ready")}
M1_HANDOFF = {P + "StoppedRebuildExitTests.ADecidedRebuildNeverSentThatReadsTheSlotEmptyStopsAndIsHandedOverToTheEnd"}
M2_RESTART = {P + 'StoppedRebuildExitTests.ADecidedRebuildThatCrashedBeforeItsCreateRereadsTheCargoOnRestart(mode: "' + m + '")'
              for m in ("stop", "handoff")}
CANCEL_WINDOW_B = {P + "OwnOrderRebuildCargoProofTests.ASnapshotFromWithinTheDelayDoesNotProveTheCargoOfACancelledTrip"}
REQUEST_BEFORE_DUE = {P + "OwnOrderRebuildCargoProofTests.ARequestAnsweredJustBeforeTheDueTimeIsMadeAgainAtTheDueRound"}
HEARTBEAT_CANCELLED = {P + 'OwnOrderRebuildCargoProofTests.AVehicleThatOnlySendsHeartbeatsIsAskedAgainAfterTheDueTimeAndTheTripIsRebuilt(source: "cancelled")'}

MUTATIONS = [
    ("M1-cancel-source-carries-no-cargo", ENGINE,
     "            OwnOrderRebuildSources.CancelledInRiot => await dbContext.Set<JourneyDemandRow>().AsNoTracking()",
     "            OwnOrderRebuildSources.CancelledInRiot when false => await dbContext.Set<JourneyDemandRow>().AsNoTracking()",
     1,
     {P + "OwnOrderRebuildCargoProofTests." + n for n in [
         "AnEmptySlotAfterTheCancellationStopsTheRebuildForAPerson",
         "WithoutASnapshotAfterTheCancellationTheRebuildWaits",
         "AnInconclusiveSnapshotWaitsAndACargoShownInPlaceRebuildsToTheGate",
         "CargoProvenThenTakenUnderSomeoneElsesStopIsNotRebuilt",
         "ASnapshotReceivedBeforeTheRecoveryDoesNotCount",
         "WhileTheSessionIsNotReadyOnItsOwnOrderASnapshotDoesNotCountAndReadinessAsksForAFreshOne",
         "OneEmptySlotAmongTheLoadedDemandsStopsTheWholeTrip",
         "AGateOrderCancelledBeforeConfirmationWithCargoOnBoardNeedsTheSameProof",
         "WhileTheVehicleStaysHeldTheRecordIsWrittenOnceAndTheFirstFreeRoundMovesTheFloor"]}
     | {P + 'StoppedRebuildExitTests.ACancelledTripWhoseCargoIsNotInPlaceIsHandedToTheExceptionSessionAndEndsThere(session: "ready")',
        P + 'StoppedRebuildExitTests.ACancelledTripWhoseCargoIsNotInPlaceIsHandedToTheExceptionSessionAndEndsThere(session: "not-ready-on-own-order")',
        P + "OwnOrderRebuildTests.ALoadedOrderCancelledInRiotIsRebuiltToTheSameUnloadStop",
        P + "OwnOrderRebuildTests.ARebuildToTheUnloadStopWaitsWhileTheOnboardSaysASlotIsUnlocked",
        P + "EmergencyReleaseVersusOwnOrderRebuildTests.UnderSomeoneElsesStopAnEmptiedVehicleWhoseOwnOrderIsCancelledIsNotRebuiltAndStopsForAPerson"} | M1_HELD | M1_HANDOFF | M2_RESTART | CANCEL_WINDOW_B | REQUEST_BEFORE_DUE),
    ("M2-floor-without-due", ENGINE,
     "                rebuild.RecordedAt, rebuild.DueAt, rebuild.CargoEvidenceNotBefore ?? DateTimeOffset.MinValue,",
     "                rebuild.RecordedAt, rebuild.RecordedAt, rebuild.CargoEvidenceNotBefore ?? DateTimeOffset.MinValue,",
     1,
     {P + "OwnOrderRebuildCargoProofTests.FaultWindowBASnapshotFromWithinTheDelayDoesNotProveTheCargoAtTheRebuild"} | CANCEL_WINDOW_B | REQUEST_BEFORE_DUE),
    ("M3-free-round-keeps-floor", ENGINE,
     "            rebuild.CargoEvidenceNotBefore = now;\n            OwnOrderRebuilds.WithdrawCargoEvidenceRequest(rebuild);",
     "            _ = now;\n            _ = rebuild;",
     1,
     {P + "OwnOrderRebuildCargoProofTests." + n for n in [
         "CargoProvenThenTakenUnderSomeoneElsesStopIsNotRebuilt",
         "ASnapshotReceivedBeforeTheRecoveryDoesNotCount",
         "WhileTheSessionIsNotReadyOnItsOwnOrderASnapshotDoesNotCountAndReadinessAsksForAFreshOne",
         "FaultWindowACargoProvenThenTakenWhileTheVehicleIsHeldIsNotRebuilt",
         "WhileTheVehicleStaysHeldTheRecordIsWrittenOnceAndTheFirstFreeRoundMovesTheFloor"]}
     | {P + "EmergencyReleaseVersusOwnOrderRebuildTests.UnderSomeoneElsesStopAnEmptiedVehicleWhoseOwnOrderIsCancelledIsNotRebuiltAndStopsForAPerson",
        P + 'VehicleFaultRecoveryTests.AClearanceWhileTheSessionIsNotReadyOnItsOwnOrderRebuildsOnceTheSessionIsReady(cargo: "loaded")'}),
    ("M4-stale-request-not-withdrawn", ENGINE,
     "(rebuild.CargoEvidenceRequestedAt is not { } requestedAt || requestedAt <= notBefore ||",
     "(rebuild.CargoEvidenceRequestedAt is not { } requestedAt || false ||",
     1,
     {P + "OwnOrderRebuildCargoProofTests.ARequestAnsweredJustBeforeTheDueTimeIsMadeAgainAtTheDueRound"}),
    ("M5-claim-skips-cancel-source", REBUILDS,
     "                             (row.Source == OwnOrderRebuildSources.CancelledInRiot &&",
     "                             (row.Source == OwnOrderRebuildSources.CancelledInRiot && false &&",
     1,
     {P + "OwnOrderRebuildCargoProofTests.WhileTheSessionIsNotReadyOnItsOwnOrderASnapshotDoesNotCountAndReadinessAsksForAFreshOne",
      P + "OwnOrderRebuildCargoProofTests.WhileTheVehicleStaysHeldTheRecordIsWrittenOnceAndTheFirstFreeRoundMovesTheFloor"} | M1_HELD | M1_HANDOFF | HEARTBEAT_CANCELLED | REQUEST_BEFORE_DUE),
    ("M6-snapshot-of-any-vehicle", ENGINE,
     "            if (SnapshotOf(json) == runtime.AgvId)",
     "            if (true || SnapshotOf(json) == runtime.AgvId)",
     1,
     {P + "VehicleFaultRecoveryTests.Req0362ASnapshotFromAnotherVehicleSaysNothingAboutThisOnesCargo"}),
    ("M7-claim-pending-only", REBUILDS,
     "OwnOrderRebuildStates.Ordering &&",
     "OwnOrderRebuildStates.Ordering && false &&",
     2,
     {P + "StoppedRebuildExitTests.ADecidedRebuildNeverSentThatReadsTheSlotEmptyStopsAndIsHandedOverToTheEnd"}
     | {P + 'OwnOrderRebuildCargoProofTests.ADecidedRebuildNeverSentThatIsHeldOnceIsAskedForAFreshSnapshotAndRebuilt(hold: "' + h + '")'
        for h in ("riot-unreadable", "emergency", "session-not-ready")}),
    ("F-proof-latched", ENGINE,
     "        DateTimeOffset notBefore = new[]",
     "        if (rebuild.CargoProvenAt is not null) return false;\n        DateTimeOffset notBefore = new[]",
     1,
     M2_RESTART | M1_HANDOFF),
]


def run(args, log):
    r = subprocess.run(args, cwd=WT, capture_output=True, text=True, encoding="utf-8", errors="replace")
    with open(log, "w", encoding="utf-8") as f:
        f.write(r.stdout)
        f.write(r.stderr)
    return r.returncode, r.stdout + r.stderr


def build(log):
    code, out = run(["dotnet", "build", r"tests\ControlServer.Tests\ControlServer.Tests.csproj", "--no-incremental"], log)
    errors = re.search(r"(\d+) Error\(s\)", out)
    return code, errors.group(1) if errors else "?"


def failed_names(out):
    # A Theory display name carries spaces and quotes: take everything up to the trailing " [<duration>]".
    return {m.group(1).strip() for m in re.finditer(r"^\s+Failed (ControlServer\.Tests\.\S.*?) \[[^\]]*\]\s*$", out, re.M)}


os.makedirs(OUT, exist_ok=True)
summary = open(os.path.join(OUT, "summary.txt"), "w", encoding="utf-8")
summary.write(f"cs#366 mutations, {datetime.datetime.now().isoformat()}, HEAD {subprocess.check_output(['git','rev-parse','HEAD'], cwd=WT, text=True).strip()}\n")
all_ok = True
ONLY = set(sys.argv[2].split(",")) if len(sys.argv) > 2 else None
for name, rel, old, new, count, expected in MUTATIONS:
    if ONLY and name.split("-")[0] not in ONLY:
        continue
    path = os.path.join(WT, rel)
    backup = path + ".mut-backup"
    shutil.copy2(path, backup)
    try:
        text = open(path, encoding="utf-8", newline="").read()
        nl = "\r\n" if "\r\n" in text else "\n"
        o, n = old.replace("\n", nl), new.replace("\n", nl)
        hits = text.count(o)
        summary.write(f"=== {name} ({rel}) replacements: {hits} (expected {count})\n")
        if hits != count:
            summary.write("    SKIPPED: replacement count mismatch\n")
            all_ok = False
            continue
        open(path, "w", encoding="utf-8", newline="").write(text.replace(o, n))
        diff = subprocess.run(["git", "diff", "--numstat", "--", rel], cwd=WT, capture_output=True, text=True).stdout.strip()
        summary.write(f"    diff: {diff}\n")
        bcode, berr = build(os.path.join(OUT, f"{name}-build.txt"))
        summary.write(f"    build exit {bcode}, {berr} Error(s)\n")
        tcode, out = run(["dotnet", "test", r"tests\ControlServer.Tests\ControlServer.Tests.csproj", "--no-build",
                          "--filter", FILTER, "--logger", "console;verbosity=normal"], os.path.join(OUT, f"{name}-test.txt"))
        got = failed_names(out)
        totals = re.findall(r"^\s*(Total tests: \d+|Passed: \d+|Failed: \d+)", out, re.M)
        summary.write(f"    test exit {tcode}; {', '.join(t.strip() for t in totals)}\n")
        where = {}
        for m in re.finditer(r"^\s+Failed (ControlServer\.Tests\.\S.*?) \[[^\]]*\]\s*$", out, re.M):
            hit = re.search(r"(\w+\.cs):line (\d+)", out[m.end():m.end() + 8000])
            where[m.group(1).strip()] = f"{hit.group(1)}:{hit.group(2)}" if hit else "?"
        for t in sorted(got):
            summary.write(f"    RED {t} @ {where.get(t, '?')}{'' if t in expected else '   <-- NOT EXPECTED'}\n")
        for t in sorted(expected - got):
            summary.write(f"    MISSING (expected red, stayed green) {t}\n")
        verdict = "as expected" if got == expected else "DIFFERS from expectation"
        if got != expected:
            all_ok = False
        summary.write(f"    verdict: {len(got)} red, expected {len(expected)} -> {verdict}\n")
    finally:
        shutil.copy2(backup, path)
        os.remove(backup)
        clean = subprocess.run(["git", "status", "--porcelain", "--", "src"], cwd=WT, capture_output=True, text=True).stdout.strip()
        summary.write(f"    src clean after restore: '{clean}'\n")
    summary.flush()

bcode, berr = build(os.path.join(OUT, "restore-build.txt"))
summary.write(f"=== rebuild after all restores: exit {bcode}, {berr} Error(s)\n")
summary.write(f"=== overall: {'all as expected' if all_ok else 'SEE DIFFERENCES ABOVE'}\n")
summary.close()
print(open(os.path.join(OUT, "summary.txt"), encoding="utf-8").read())
