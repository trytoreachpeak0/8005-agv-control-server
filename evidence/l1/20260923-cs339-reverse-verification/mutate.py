import os, subprocess, sys, re

ROOT = r"C:\Users\szy\Desktop\8005-workspace-v2\worktrees\cs339-8005-agv-control-server"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "mutations")
os.makedirs(OUT, exist_ok=True)
ENGINE = os.path.join(ROOT, r"src\ControlServer.Host\Runtime\JourneyRuntimeEngine.cs")
CURSOR = os.path.join(ROOT, r"src\ControlServer.Host\Runtime\JourneyStopCursor.cs")
PUBLISHER = os.path.join(ROOT, r"src\ControlServer.Host\Transport\OnboardJourneyPublisher.cs")
FILTER = "FullyQualifiedName~RefilledStationDeadlineReachesVehicleTests|FullyQualifiedName~ArrivalPublishInterruptedThenReconnectedTests"

STALE_CHECK = ("        if (sent == StationDepartureDeadline(runtime, runtimeOptions.StationDepartureWaitTimeout))\n"
               "        {\n            return null;\n        }")
NO_REISSUE = "        if (queued is not null)\n        {\n            return null;\n        }"
ARRIVAL_WORKLIST = ("            keepAcknowledgedIgnoring: NothingButTheEnvelope).ConfigureAwait(false);\n"
                    "        await RetireSupersededSnapshotAsync(PickupDispatchPlanMessageId(runtime)")
ARRIVAL_WORKLIST_IGNORING_DEADLINE = (
    "            keepAcknowledgedIgnoring: new HashSet<string>([\"stationDepartureDeadlineAt\"], StringComparer.Ordinal)).ConfigureAwait(false);\n"
    "        await RetireSupersededSnapshotAsync(PickupDispatchPlanMessageId(runtime)")
SUPERSEDED = ("        await ArrivalBusinessStateQueuedAsync(stop, cancellationToken).ConfigureAwait(false) is { } queued &&\n"
              "        queued < StopRevision(runtime.VehicleBusinessRevision, stop);")

MUTATIONS = [
    ("R7-kept-when-acknowledged-regardless-of-content", PUBLISHER,
     [("            SaysTheSameIgnoring(existing.PayloadJson, candidateWire, keepAcknowledgedIgnoring))", "            keepAcknowledgedIgnoring.Count >= 0)")]),
    ("R1-no-reissue", ENGINE, [(STALE_CHECK, NO_REISSUE)]),
    ("R1b-no-reissue-and-deadline-ignored-again", ENGINE,
     [(STALE_CHECK, NO_REISSUE), (ARRIVAL_WORKLIST, ARRIVAL_WORKLIST_IGNORING_DEADLINE)]),
    ("R2-refills-not-in-revision", CURSOR,
     [("FirstWorklistRevisionAt(journeyBase, stop) + DoneAt(stop) + stop.WorklistRefills;",
       "FirstWorklistRevisionAt(journeyBase, stop) + DoneAt(stop);")]),
    ("R3-refills-not-in-versions", CURSOR,
     [("Math.Max(1, AllAtStop(stop).Count) + stop.WorklistRefills;", "Math.Max(1, AllAtStop(stop).Count);")]),
    ("R4-superseded-always-true", ENGINE,
     [(SUPERSEDED, "        await Task.FromResult(dbContext is not null);")]),
    ("R5-arrived-by-stage-only", ENGINE,
     [(" ||\n                           await ArrivalBusinessStateQueuedAsync(stop, cancellationToken).ConfigureAwait(false) is not null;",
       ";")]),
    ("R6-superseded-never", ENGINE,
     [(SUPERSEDED, "        await Task.FromResult(dbContext is null);")]),
]

only = sys.argv[1:]
for name, path, pairs in MUTATIONS:
    if only and name not in only:
        continue
    log = os.path.join(OUT, name + ".log")
    original = open(path, "rb").read()
    text = original.decode("utf-8")
    counts = [text.count(old) for old, _ in pairs]
    with open(log, "w", encoding="utf-8") as f:
        f.write(f"mutation {name}: {os.path.basename(path)} matches={counts}\n")
        if any(c != 1 for c in counts):
            f.write("NOT APPLIED\n")
            print(name, "NOT APPLIED", counts)
            continue
        try:
            mutated = text
            for old, new in pairs:
                mutated = mutated.replace(old, new)
            open(path, "wb").write(mutated.encode("utf-8"))
            run = dict(cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
            f.write(subprocess.run(["git", "diff"], **run).stdout)
            b = subprocess.run(["dotnet", "build", r"tests\ControlServer.Tests\ControlServer.Tests.csproj",
                                "-c", "Release", "-nologo", "-v", "q"], **run)
            errs = [l for l in b.stdout.splitlines() if " error " in l or "Error(s)" in l]
            f.write("\n".join(sorted(set(errs))) + "\n")
            if b.returncode != 0:
                print(name, "BUILD FAILED", *sorted(set(errs))[:3], sep="\n  ")
                continue
            t = subprocess.run(["dotnet", "test", r"tests\ControlServer.Tests\ControlServer.Tests.csproj", "-c", "Release",
                                "--no-build", "--filter", FILTER], **run)
            f.write(t.stdout or "")
            failed = re.findall(r"^\s+Failed (.+?) \[", t.stdout or "", re.M)
            summary = [l for l in (t.stdout or "").splitlines() if "Passed!" in l or "Failed!" in l]
            print(name, summary, *failed, sep="\n  ")
        finally:
            open(path, "wb").write(original)
            os.utime(path, None)
print("restored; git status:")
print(subprocess.run(["git", "status", "--short"], cwd=ROOT, capture_output=True, text=True).stdout)
