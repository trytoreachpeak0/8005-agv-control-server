"""Mutation runs for control-server#186. One mutation at a time, each written down with the tests expected to go red
before it runs. For each: back the file up, replace exactly one occurrence (anything else aborts the whole batch),
show the diff stat, `dotnet build --no-incremental`, `dotnet test --no-build` over the ticket's test classes, restore
from the backup and touch the file (a restored old timestamp would let an incremental build skip it). Every result is
written to results.json next to this file, with the red test names read from the test output and checked against the
summary's Failed count.

Run from the worktree root: python evidence/cs186/mutations/run-mutations.py [ids...]
"""
import json
import os
import re
import shutil
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
FILTER = ("FullyQualifiedName~MapRename|FullyQualifiedName~MapNameBaseline|"
          "FullyQualifiedName~HttpRiotMapNameCatalog|FullyQualifiedName~FakeRiotMapName|"
          "FullyQualifiedName~TaskTypeStationStartupTests")
CONV = "src/ControlServer.Host/Runtime/TaskTypeStations/MapRenameHoldConvergence.cs"
ENGINE = "src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs"
GATEWAY = "src/ControlServer.Infrastructure/Adapters/HttpRiotMovementGateway.cs"
ACCEPT = "src/ControlServer.Application/MapNameBaselineAcceptanceService.cs"
STORE = "src/ControlServer.Infrastructure/Persistence/MapNameBaselineStore.cs"
ACTIVATION_STORE = "src/ControlServer.Infrastructure/Persistence/TaskTypeStationActivationStore.cs"
STARTUP = "src/ControlServer.Host/Runtime/TaskTypeStations/TaskTypeStationStartup.cs"
RELEASE = "src/ControlServer.Application/TaskTypeStationActivationService.cs"
FIXTURE = "tests/ControlServer.Tests/RiotReplays/map-list-2026-09-28.json"

MUTATIONS = [
    {
        "id": "M1", "file": CONV,
        "models": "A blank name in the listing is taken as data: the guard against blank names is dropped.",
        "old": "if (entries.Length != 1 || string.IsNullOrWhiteSpace(entries[0].Name))",
        "new": "if (entries.Length != 1)",
        "expect": ["MapRenameHoldConvergenceTests.AMapTheListingDoesNotNameUsablyIsNotARename(shape: \"empty\")",
                   "MapRenameHoldConvergenceTests.AMapTheListingDoesNotNameUsablyIsNotARename(shape: \"blank\")"],
    },
    {
        "id": "M2", "file": CONV,
        "models": "An unreadable Map list is treated fail-closed as a rename of every Map already known.",
        "old": "RiotMapNameListing listing = await mapNames.ReadMapNamesAsync(cancellationToken).ConfigureAwait(false);",
        "new": ("RiotMapNameListing listing;\n        try { listing = await mapNames.ReadMapNamesAsync(cancellationToken)"
                ".ConfigureAwait(false); }\n        catch (InvalidDataException) { listing = new(timeProvider.GetUtcNow(), "
                "[.. dbContext.Set<MapNameBaselineRow>().Select(row => row.MapId).ToList()"
                ".Select(id => new RiotMapName(id, \"(unreadable)\"))]); }"),
        "expect": ["MapRenameHoldConvergenceTests.AFailedReadThrowsAndLeavesBaselineAndHoldsExactlyAsTheyWere",
                   "MapRenameHoldConvergenceTests.HoldsAndBaselineStandThroughFailedAndMissingReadsAndTheUnacceptedRenameStillHoldsAfter",
                   "MapRenameEngineTests.AFailedMapListReadNeitherStopsTheRoundNorHolds(failure: \"invalid\")"],
    },
    {
        "id": "M3", "file": CONV,
        "models": "A Map the listing leaves out is treated as renamed.",
        "old": "        return observed;\n    }\n",
        "new": ("        foreach (int missing in dbContext.Set<MapNameBaselineRow>().Select(row => row.MapId).ToList()"
                ".Where(id => listing.Maps.All(map => map.MapId != id)))\n        {\n"
                "            observed.Add(await ConvergeAsync(missing, \"(missing)\", now, cancellationToken).ConfigureAwait(false));\n"
                "        }\n        return observed;\n    }\n"),
        "expect": ["MapRenameHoldConvergenceTests.AMapTheListingDoesNotNameUsablyIsNotARename(shape: \"missing\")",
                   "MapRenameHoldConvergenceTests.HoldsAndBaselineStandThroughFailedAndMissingReadsAndTheUnacceptedRenameStillHoldsAfter"],
    },
    {
        "id": "M4", "file": CONV,
        "models": "First sight holds the Map's bound task types as if it were a change.",
        "old": ("            case MapRenameObservationKind.BaselineEstablished:\n"
                "                await baselines.EstablishAsync(mapId, name, now, cancellationToken).ConfigureAwait(false);\n"),
        "new": ("            case MapRenameObservationKind.BaselineEstablished:\n"
                "                await baselines.EstablishAsync(mapId, name, now, cancellationToken).ConfigureAwait(false);\n"
                "                TaskTypeStationBindingSetVersion? first = await bindings.ReadActiveAsync(mapId, cancellationToken).ConfigureAwait(false);\n"
                "                foreach (string firstTaskType in first?.Bindings.Select(binding => binding.TaskType).Distinct() ?? [])\n"
                "                {\n"
                "                    await MapRenameHoldWriter.RaiseAsync(dbContext, holds, audit, first!, firstTaskType, name, name, deployment.Value, \"FIRST_SIGHT\", now, cancellationToken).ConfigureAwait(false);\n"
                "                }\n"),
        "expect": ["MapRenameHoldConvergenceTests.TheFirstNameSeenForAMapBecomesItsBaselineAndHoldsNothing",
                   "MapRenameHoldConvergenceTests.TheSameNameAgainChangesNothing",
                   "MapRenameHoldConvergenceTests.AMapTheListingDoesNotNameUsablyIsNotARename(shape: \"missing\")",
                   "MapRenameHoldConvergenceTests.AMapTheListingDoesNotNameUsablyIsNotARename(shape: \"empty\")",
                   "MapRenameHoldConvergenceTests.AMapTheListingDoesNotNameUsablyIsNotARename(shape: \"blank\")",
                   "MapRenameHoldConvergenceTests.AMapTheListingDoesNotNameUsablyIsNotARename(shape: \"duplicate\")",
                   "MapRenameHoldConvergenceTests.AFailedReadThrowsAndLeavesBaselineAndHoldsExactlyAsTheyWere",
                   "MapRenameEngineTests.MapIdentityIsNeitherTheBaselineNorComparedWithIt"],
    },
    {
        "id": "M5", "file": ENGINE,
        "models": "The baseline is seeded from JourneyRuntime:mapIdentity before RIoT is read.",
        "old": ("            await mapRenameHolds.ObserveAsync(cancellationToken).ConfigureAwait(false);\n"),
        "new": ("            await dbContext.Database.ExecuteSqlInterpolatedAsync($\"INSERT OR IGNORE INTO MapNameBaselines (MapId, Name, "
                "EstablishedAt) VALUES ({runtimeOptions.MapId}, {runtimeOptions.MapIdentity}, {timeProvider.GetUtcNow()})\", "
                "cancellationToken).ConfigureAwait(false);\n"
                "            await mapRenameHolds.ObserveAsync(cancellationToken).ConfigureAwait(false);\n"),
        "expect": ["MapRenameEngineTests.MapIdentityIsNeitherTheBaselineNorComparedWithIt",
                   "MapRenameEngineTests.AFailedMapListReadNeitherStopsTheRoundNorHolds(failure: \"invalid\")",
                   "MapRenameEngineTests.AFailedMapListReadNeitherStopsTheRoundNorHolds(failure: \"http\")",
                   "MapRenameEngineTests.AFailedMapListReadNeitherStopsTheRoundNorHolds(failure: \"timeout\")"],
    },
    {
        "id": "M6", "file": CONV,
        "models": "Every task type there is gets held, bound or not (the ticket's literal wording).",
        "old": ": [.. active.Bindings.Select(binding => binding.TaskType).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];",
        "new": ": [.. TransportTaskTypes.All.Order(StringComparer.Ordinal)];",
        "expect": ["MapRenameHoldConvergenceTests.ARenameUnderTheSameMapIdHoldsEveryBoundTaskTypeOfThatMapAndNothingOnAnotherMap",
                   "MapRenameHoldConvergenceTests.ARenameWritesOneDetectionAuditAndOneHoldAuditPerTaskTypeAndARepeatWritesNothingMore",
                   "MapRenameHoldConvergenceTests.ATaskTypeBoundWhileTheRenameIsPendingIsHeldOnTheNextObservation",
                   "MapRenameHoldConvergenceTests.NamesAreComparedByteForByteSoAWidthOrCaseDifferenceIsARename",
                   "MapRenameHoldConvergenceTests.HoldsAndBaselineStandThroughFailedAndMissingReadsAndTheUnacceptedRenameStillHoldsAfter",
                   "MapRenameEngineTests.AnUnboundTaskTypeDoesNotDispatchAndBoundDuringAPendingRenameItIsHeldUntilTheRenameIsAccepted",
                   "MapRenameEngineTests.ARenameStopsDispatchFromItsRoundAndAcceptingTheNameThenReleasingTheHoldResumesIt",
                   "MapRenameEngineTests.MapIdentityIsNeitherTheBaselineNorComparedWithIt"],
    },
    {
        "id": "M7", "file": RELEASE,
        "models": "The release verb stops checking for an unaccepted rename.",
        "old": "if (mapName?.PendingName is { } pendingName)",
        "new": "if (mapName?.PendingName is { } pendingName && pendingName.Length < 0)",
        # 082a6115: red 1 (AHoldReleaseIsRefusedWhileTheMapCarriesAnUnacceptedRenameAndGoesThroughOnceItIsAccepted).
        # From 08d1a39c the release transaction re-reads the pending name and refuses by itself (review suggestion 1),
        # so taking out the service's check alone is expected to survive -- the second fence, as with M8a.
        "expect": [] if os.environ.get("CS186_REVIEWED") else
                  ["MapNameBaselineAcceptanceTests.AHoldReleaseIsRefusedWhileTheMapCarriesAnUnacceptedRenameAndGoesThroughOnceItIsAccepted"],
    },
    {
        "id": "M8a", "file": ACCEPT,
        "models": "The service stops comparing the typed name with the pending one (the store's own check stays).",
        "old": "if (!string.Equals(baseline.PendingName, mapName, StringComparison.Ordinal))",
        "new": "if (mapName is null)",
        "expect": [],
    },
    {
        "id": "M8b", "file": STORE,
        "models": "The store accepts whatever name it is handed.",
        "old": ("            if (row is null || !string.Equals(row.PendingName, acceptedName, StringComparison.Ordinal))\n"
                "            {\n                return null;\n            }\n"),
        "new": ("            if (row is null)\n            {\n                return null;\n            }\n"),
        "also": [("                .Where(candidate => candidate.MapId == mapId && candidate.PendingName == acceptedName)",
                  "                .Where(candidate => candidate.MapId == mapId)")],
        "expect": ["MapNameBaselineAcceptanceTests.TheStoreAcceptsOnlyTheNameThatIsStillPendingAndOtherwiseWritesNothing"],
    },
    {
        "id": "M9", "file": CONV,
        "models": "A rename back to the baseline releases the rename holds by itself.",
        "old": ("                await baselines.SetPendingAsync(mapId, null, null, cancellationToken).ConfigureAwait(false);\n"),
        "new": ("                await baselines.SetPendingAsync(mapId, null, null, cancellationToken).ConfigureAwait(false);\n"
                "                foreach (TaskTypeStationHold back in (await holds.ListUnreleasedAsync(mapId, cancellationToken).ConfigureAwait(false))"
                ".Where(hold => hold.ReasonCode == MapNameHoldReasons.MapRenamed))\n"
                "                {\n"
                "                    await holds.ReleaseAsync(back.HoldId, \"auto:renamed-back\", now, cancellationToken).ConfigureAwait(false);\n"
                "                }\n"),
        "expect": ["MapRenameHoldConvergenceTests.ARenameBackToTheBaselineClearsThePendingNameButLeavesTheHoldsForTheField"],
    },
    {
        "id": "M10", "file": FIXTURE,
        "models": "RIoT answers one Map's source as a number instead of a string (fixture change, not product).",
        "old": "\"source\": \"upload\"",
        "new": "\"source\": 1",
        "expect": ["HttpRiotMapNameCatalogTests.TheRealRiotAnswerOf20260928ParsesThroughTheAdapterWithMap26UnderItsName",
                   "HttpRiotMapNameCatalogTests.AnEnumValueTheSdkDoesNotKnowStillReadsEveryMapAndItsName(key: \"syncState\", known: \"partition\", unknown: \"resyncing\")",
                   "HttpRiotMapNameCatalogTests.AnEnumValueTheSdkDoesNotKnowStillReadsEveryMapAndItsName(key: \"source\", known: \"fetch\", unknown: \"importedFromCloud\")",
                   "HttpRiotMapNameCatalogTests.AnEnumValueTheSdkDoesNotKnowStillReadsEveryMapAndItsName(key: \"state\", known: \"activated\", unknown: \"archived\")"],
    },
    {
        "id": "M11", "file": GATEWAY,
        "models": "The adapter swallows an unreadable Map list and answers an empty one.",
        "old": "throw new InvalidDataException(\"RIoT Map list response was not valid.\", error);",
        "new": "return new RiotMapNameListing(timeProvider.GetUtcNow(), []);",
        "expect": ["HttpRiotMapNameCatalogTests.AReadThatDoesNotYieldANonEmptyListThrowsInsteadOfAnsweringEmpty(status: OK, body: \"{\\\"code\\\":\\\"500\\\",\\\"message\\\":\\\"失败\\\",\\\"result\\\":nu\"···)",
                   "HttpRiotMapNameCatalogTests.AReadThatDoesNotYieldANonEmptyListThrowsInsteadOfAnsweringEmpty(status: OK, body: \"not json\")",
                   "HttpRiotMapNameCatalogTests.AReadThatDoesNotYieldANonEmptyListThrowsInsteadOfAnsweringEmpty(status: InternalServerError, body: \"{\\\"code\\\":\\\"500\\\"}\")",
                   "FakeRiotMapNameTests.AMapListFaultFailsOnlyTheMapListAndClearingItAnswersAgain"],
    },
    {
        "id": "M12", "file": ENGINE,
        "models": "A failed Map list read ends the round instead of being logged and skipped.",
        "old": "            LogMapRenameObservationFailed(logger, error);\n",
        "new": "            LogMapRenameObservationFailed(logger, error);\n            throw;\n",
        "expect": ["MapRenameEngineTests.AFailedMapListReadNeitherStopsTheRoundNorHolds(failure: \"invalid\")",
                   "MapRenameEngineTests.AFailedMapListReadNeitherStopsTheRoundNorHolds(failure: \"http\")",
                   "MapRenameEngineTests.AFailedMapListReadNeitherStopsTheRoundNorHolds(failure: \"timeout\")"],
    },
    {
        "id": "M13", "file": CONV,
        "models": "Names are compared ignoring case.",
        "old": "if (string.Equals(baseline.Name, name, StringComparison.Ordinal))",
        "new": "if (string.Equals(baseline.Name, name, StringComparison.OrdinalIgnoreCase))",
        "expect": ["MapRenameHoldConvergenceTests.NamesAreComparedByteForByteSoAWidthOrCaseDifferenceIsARename"],
    },
    # --- PR #378 review round (08d1a39c) ---
    {
        "id": "MX1", "file": CONV,
        "models": "While a rename is pending, a task type bound since is never held (the reviewer's MX1).",
        "old": "bool writes = !string.Equals(baseline.PendingName, name, StringComparison.Ordinal) || unheld.Length > 0;",
        "new": "bool writes = !string.Equals(baseline.PendingName, name, StringComparison.Ordinal) || false;",
        "expect": ["MapRenameHoldConvergenceTests.ATaskTypeBoundWhileTheRenameIsPendingIsHeldOnTheNextObservation",
                   "MapRenameEngineTests.AnUnboundTaskTypeDoesNotDispatchAndBoundDuringAPendingRenameItIsHeldUntilTheRenameIsAccepted"],
    },
    {
        "id": "MX2", "file": ENGINE,
        "models": "A failed Map list read lets go of every MAP_RENAMED hold (the reviewer's MX2).",
        "old": "            LogMapRenameObservationFailed(logger, error);" + chr(10),
        "new": ("            LogMapRenameObservationFailed(logger, error);" + chr(10) +
                "            await dbContext.Set<TaskTypeStationHoldRow>().Where(row => row.ReasonCode == \"MAP_RENAMED\" && row.ReleasedAt == null)"
                ".ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ReleasedAt, timeProvider.GetUtcNow()), cancellationToken)"
                ".ConfigureAwait(false);" + chr(10)),
        "expect": ["MapRenameEngineTests.AfterARenameAFailedMapListReadDoesNotLetTheHeldTaskTypeDispatch"],
    },
    {
        "id": "R1", "file": ACTIVATION_STORE,
        "models": "The release transaction no longer re-reads the pending name (review suggestion 1 undone).",
        "old": "                throw new MapRenamePendingException(mapId, mapName.Name, pendingName);",
        "new": "                _ = pendingName;",
        "expect": ["MapNameBaselineAcceptanceTests.ARenameSeenBetweenTheReleaseChecksAndItsTransactionStillRefusesTheRelease"],
    },
    {
        "id": "R2", "file": ACTIVATION_STORE,
        "models": "An activation under a pending rename no longer holds its task types (review suggestion 2 undone).",
        "old": "            await HoldUnderPendingRenameAsync(attempt, at, cancellationToken);" + chr(10),
        "new": "",
        "expect": ["MapNameBaselineAcceptanceTests.AnActivationUnderAPendingRenameHoldsEveryTaskTypeOfTheVersionItMakesActive(renameBetweenTheTwoSteps: False)",
                   "MapNameBaselineAcceptanceTests.AnActivationUnderAPendingRenameHoldsEveryTaskTypeOfTheVersionItMakesActive(renameBetweenTheTwoSteps: True)"],
    },
    {
        "id": "R3", "file": CONV,
        "models": "A failure on one Map ends the whole observation again (review suggestion 3 undone).",
        "old": "observed.Add(await ConvergeIsolatedAsync(map.Key, entries[0].Name, now, cancellationToken).ConfigureAwait(false));",
        "new": "observed.Add(await ConvergeAsync(map.Key, entries[0].Name, now, cancellationToken).ConfigureAwait(false));",
        "expect": ["MapRenameHoldConvergenceTests.AFailureOnOneUnrelatedMapDoesNotKeepAMapAfterItFromBeingObserved"],
    },
    # --- PR #378 incremental review (6e363d7c) ---
    {
        "id": "R4", "file": STARTUP,
        "models": "The startup preset no longer holds its task types under a pending rename (incremental review item 1 undone).",
        "old": "        await MapRenameHoldWriter.HoldUnderPendingRenameAsync(" + chr(10) + "            context,",
        "new": "        if (now < DateTimeOffset.MinValue.AddYears(1)) await MapRenameHoldWriter.HoldUnderPendingRenameAsync(" + chr(10) + "            context,",
        "expect": ["TaskTypeStationStartupTests.AFirstStartUnderAPendingMapRenameHoldsEveryTaskTypeThePresetBinds"],
    },
    {
        "id": "R5", "file": CONV,
        "models": "The Map name check runs inside an outer transaction as if it could still isolate one Map (item 2 undone).",
        "old": "        if (dbContext.Database.CurrentTransaction is not null)",
        "new": "        if (dbContext.Database.CurrentTransaction is not null && mapNames is null)",
        "expect": ["MapRenameHoldConvergenceTests.ObservingInsideSomeoneElsesTransactionIsRefusedBecauseOneMapCouldNotBeRolledBackAlone"],
    },
]


def run(args, log):
    with open(log, "w", encoding="utf-8") as out:
        return subprocess.run(args, stdout=out, stderr=subprocess.STDOUT).returncode


def red_names(text):
    names = [m.group(1).strip() for m in re.finditer(r"^\s+Failed ControlServer\.Tests\.(.+?) \[[^\]]+\]\s*$", text, re.M)]
    summary = re.search(r"Failed:\s+(\d+), Passed:\s+(\d+)", text)
    return names, (int(summary.group(1)), int(summary.group(2))) if summary else None


def lines_of(text):
    """Failing test -> the line of its own test file the stack names last."""
    out = {}
    for block in re.split(r"\n\s+Failed ControlServer\.Tests\.", text)[1:]:
        name = block.split(" [")[0].strip()
        where = re.findall(r"\\(\w+Tests\.cs):line (\d+)", block)
        message = re.search(r"Error Message:\s*\n\s*(.+)", block)
        out[name] = {"at": f"{where[-1][0]}:{where[-1][1]}" if where else None,
                     "message": message.group(1).strip()[:160] if message else None}
    return out


def main():
    wanted = set(sys.argv[1:])
    results_path = os.path.join(HERE, os.environ.get("CS186_RESULTS", "results.json"))
    results = json.load(open(results_path, encoding="utf-8")) if os.path.exists(results_path) else {}
    for mutation in MUTATIONS:
        if wanted and mutation["id"] not in wanted:
            continue
        mid, path = mutation["id"], mutation["file"]
        backup = os.path.join(HERE, f"{mid}.backup")
        shutil.copyfile(path, backup)
        try:
            text = open(path, encoding="utf-8", newline="").read()
            # The anchors are written with LF. A working copy with CRLF line ends (dotnet format writes them) gets the same
            # anchors with CRLF, so a multi-line anchor still matches exactly once instead of silently matching nothing.
            crlf = chr(13) + chr(10)
            for old, new in [(mutation["old"], mutation["new"])] + mutation.get("also", []):
                if crlf in text:
                    old, new = old.replace(chr(10), crlf), new.replace(chr(10), crlf)
                count = text.count(old)
                if count != 1:
                    sys.exit(f"{mid}: expected exactly one match in {path}, found {count}; batch aborted")
                text = text.replace(old, new)
            open(path, "w", encoding="utf-8", newline="").write(text)
            stat = subprocess.run(["git", "diff", "--stat", "--", path], capture_output=True, text=True).stdout.strip()
            print(f"{mid}: {stat}", flush=True)
            if not stat:
                sys.exit(f"{mid}: git sees no change in {path}; batch aborted")
            build_log = os.path.join(HERE, f"{mid}.build.log")
            build = run(["dotnet", "build", "tests/ControlServer.Tests/ControlServer.Tests.csproj", "-c", "Release",
                         "--no-incremental", "--nologo", "-v", "q"], build_log)
            test_log = os.path.join(HERE, f"{mid}.test.log")
            test = run(["dotnet", "test", "tests/ControlServer.Tests/ControlServer.Tests.csproj", "-c", "Release",
                        "--no-build", "--nologo", "--filter", FILTER], test_log) if build == 0 else None
            output = open(test_log, encoding="utf-8", errors="replace").read() if test is not None else ""
            reds, summary = red_names(output)
            if summary is not None and summary[0] != len(reds):
                sys.exit(f"{mid}: read {len(reds)} red names but the summary says Failed {summary[0]}; batch aborted")
            expected = mutation["expect"]
            results[mid] = {
                "file": path, "models": mutation["models"], "diffStat": stat,
                "buildExit": build, "testExit": test, "failed": summary[0] if summary else None,
                "passed": summary[1] if summary else None,
                "expected": expected, "red": reds,
                "expectedButGreen": [name for name in expected if name not in reds],
                "redButNotExpected": [name for name in reds if name not in expected],
                "where": lines_of(output),
            }
            print(f"{mid}: build {build}, test {test}, red {len(reds)} "
                  f"(missed {len(results[mid]['expectedButGreen'])}, extra {len(results[mid]['redButNotExpected'])})", flush=True)
        finally:
            shutil.copyfile(backup, path)
            os.utime(path, None)
            os.remove(backup)
        json.dump(results, open(results_path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    left = subprocess.run(["git", "status", "--short", "--", "src", "tests", "tools"], capture_output=True, text=True).stdout
    print("worktree after restore:", left.strip() or "clean")


if __name__ == "__main__":
    main()
