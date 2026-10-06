import io, subprocess, sys, os
root = r"C:\Users\szy\Desktop\8005-workspace-v2\worktrees\cs499-8005-agv-control-server"
rel = r"src\ControlServer.Host\Runtime\BlockedJourneyRelease.cs"
eng = r"src\ControlServer.Host\Runtime\JourneyRuntimeEngine.cs"
M = {
 "M1-no-release": (rel, "        if (runtime.Stage != JourneyRuntimeStage.Blocked ||\n", "        if (true || runtime.Stage != JourneyRuntimeStage.Blocked ||\n"),
 "M2-no-status-check": (rel, "            statusBeforeEnding != StationOperationStatus.RecoveryRequired)", "            false)"),
 "M3-no-unconverged-check": (rel, "                            row.Status == StationOperationStatus.RecoveryRequired),", "                            row.Status == StationOperationStatus.RecoveryRequired) && false,"),
 "M4-no-at-this-stop": (rel, "        if (!blockedOnIt)\n", "        if (ended is null)\n"),
 "M5-engine-no-ended-progress": (eng, "        return endedHere.Length > 0 &&\n", "        return false && endedHere.Length > 0 &&\n"),
 "M6-engine-no-op-exists": (eng, "        return endedHere.Length > 0 &&\n               await", "        return endedHere.Length > 0 ||\n               await"),
 "M7-keep-block-code": (rel, "        runtime.SetBlockReason(null, now);\n", ""),
}
which = sys.argv[1:] or list(M)
out = open(os.path.join(os.path.dirname(__file__), "mutations.txt"), "a", encoding="utf-8")
for name in which:
    path, old, new = M[name]
    full = os.path.join(root, path)
    original = io.open(full, encoding="utf-8", newline="").read()
    assert original.count(old) == 1, name
    try:
        io.open(full, "w", encoding="utf-8", newline="").write(original.replace(old, new))
        b = subprocess.run(["dotnet","build",r"tests\ControlServer.Tests\ControlServer.Tests.csproj","-c","Release","--no-incremental"], cwd=root, capture_output=True, text=True, encoding="utf-8", errors="replace")
        if b.returncode != 0:
            out.write(f"{name}: BUILD FAILED\n" + b.stdout[-2000:] + "\n"); out.flush(); continue
        t = subprocess.run(["dotnet","test",r"tests\ControlServer.Tests\ControlServer.Tests.csproj","-c","Release","--no-build","--filter","FullyQualifiedName~RecoveryEndingReleasesBlockedJourneyTests"], cwd=root, capture_output=True, text=True, encoding="utf-8", errors="replace")
        failed = [l.strip() for l in t.stdout.splitlines() if l.strip().startswith("Failed ControlServer")]
        summary = [l.strip() for l in t.stdout.splitlines() if "Failed!" in l or "Passed!" in l]
        out.write(f"{name}: exit={t.returncode} {summary}\n" + "\n".join("   " + f for f in failed) + "\n"); out.flush()
    finally:
        io.open(full, "w", encoding="utf-8", newline="").write(original)
b = subprocess.run(["dotnet","build",r"tests\ControlServer.Tests\ControlServer.Tests.csproj","-c","Release","--no-incremental"], cwd=root, capture_output=True, text=True)
out.write(f"restored build exit={b.returncode}\n")
