import os, shutil, subprocess, sys

ROOT = r"C:\Users\szy\Desktop\8005-workspace-v2\worktrees\cs316-8005-agv-control-server"
OUT = r"C:\Users\szy\AppData\Local\Temp\claude\C--Users-szy-Desktop-8005-workspace-v2\9f53d2d3-c6da-483d-a3b3-80ec8dc2c94b\scratchpad\cs316-mutations"
ENGINE = r"src\ControlServer.Host\Runtime\JourneyRuntimeEngine.cs"
RUNNER = r"src\ControlServer.Host\Runtime\Dispatch\DispatchRoundRunner.cs"
RELEASE = r"src\ControlServer.Host\Runtime\Release\DemandReleaseService.cs"

FILTER = ("FullyQualifiedName~InTransitOrderStallTests|FullyQualifiedName~Batch7DemandReleaseServiceTests|"
          "FullyQualifiedName~AJourneyWhoseOrderHangsTakesNoAppendedDemand|"
          "FullyQualifiedName~AnInTransitVehicleTakesAnAppendedDemandIntoItsExistingJourney|"
          "FullyQualifiedName~AStalledInTransitOrderIsShownWithAChineseDescription")

MUTATIONS = {
    "M1-engine-never-names": [(ENGINE,
        "        RiotOrderObservation order = arrival.Order;\n        string? reason = (order.Kind, order.OrderState) switch",
        "        RiotOrderObservation order = arrival.Order;\n        if (Environment.TickCount64 >= 0) return false;\n        string? reason = (order.Kind, order.OrderState) switch")],
    "M2-underway-only": [(ENGINE,
        "row.Stage == JourneyRuntimeStage.Blocked || IsStalledOrderReason(row.BlockReasonCode))",
        "row.Stage == JourneyRuntimeStage.Blocked)")],
    "M3-plan-query-only": [(RUNNER,
        "if (runtime is null || JourneyRuntimeEngine.IsStalledOrderReason(runtime.BlockReasonCode))",
        "if (runtime is null)")],
    "M2+M3-both-exclusions": [(ENGINE,
        "row.Stage == JourneyRuntimeStage.Blocked || IsStalledOrderReason(row.BlockReasonCode))",
        "row.Stage == JourneyRuntimeStage.Blocked)"),
        (RUNNER,
        "if (runtime is null || JourneyRuntimeEngine.IsStalledOrderReason(runtime.BlockReasonCode))",
        "if (runtime is null)")],
    "M4-no-release-trigger": [(RELEASE,
        "                    ? currentOrderEnded\n",
        "                    ? null\n")],
    "M5-trust-the-code": [(RELEASE,
        "        RiotOrderObservation order;\n        try\n",
        "        if (Environment.TickCount64 >= 0) return JourneyRuntimeEngine.OrderEndedWithoutArrivalReason;\n        RiotOrderObservation order;\n        try\n")],
    "M6-silence-overwrites": [(ENGINE,
        "            !IsStalledOrderReason(runtime.BlockReasonCode) &&\n",
        "")],
    "M7-never-clears": [(ENGINE,
        "            if (IsStalledOrderReason(runtime.BlockReasonCode))\n            {\n                runtime.SetBlockReason(null, now);",
        "            if (Environment.TickCount64 < 0)\n            {\n                runtime.SetBlockReason(null, now);")],
}

def main():
    os.makedirs(OUT, exist_ok=True)
    names = sys.argv[1:] or list(MUTATIONS)
    for name in names:
        edits = MUTATIONS[name]
        backups = {}
        try:
            for path, old, new in edits:
                full = os.path.join(ROOT, path)
                if full not in backups:
                    backups[full] = full + ".mutbak"
                    shutil.copyfile(full, backups[full])
                text = open(full, encoding="utf-8", newline="").read()
                count = text.count(old)
                if count != 1:
                    print(f"{name}: pattern matched {count} times in {path}; aborting", flush=True)
                    raise SystemExit(2)
                open(full, "w", encoding="utf-8", newline="").write(text.replace(old, new))
            diff = subprocess.run(["git", "diff", "--numstat"], cwd=ROOT, capture_output=True, text=True).stdout
            print(f"== {name} diff: {diff.strip()!r}", flush=True)
            log = os.path.join(OUT, f"{name}.txt")
            with open(log, "w", encoding="utf-8") as handle:
                result = subprocess.run(
                    ["dotnet", "test", r"tests\ControlServer.Tests\ControlServer.Tests.csproj", "-c", "Release",
                     "--filter", FILTER],
                    cwd=ROOT, stdout=handle, stderr=subprocess.STDOUT)
            lines = open(log, encoding="utf-8", errors="replace").read().splitlines()
            summary = [line for line in lines if " error " in line or line.strip().startswith("Failed ")
                       or "Passed!" in line or "Failed!" in line or "0 Error(s)" in line]
            print(f"== {name} exit {result.returncode}", flush=True)
            for line in summary[:40]:
                print("   " + line.strip(), flush=True)
        finally:
            for full, backup in backups.items():
                shutil.copyfile(backup, full)
                os.remove(backup)
    status = subprocess.run(["git", "status", "--short"], cwd=ROOT, capture_output=True, text=True).stdout
    print(f"== restored; git status: {status.strip()!r}", flush=True)

main()
