#!/usr/bin/env bash
# cs#904 mutation run. Each mutation edits exactly one file, rebuilds with --no-incremental,
# runs StagingLoadCancellationTests, records the outcome, then restores only that file.
set -euo pipefail
WT=C:/work/8005-wt/cs904-8005-agv-control-server
OUT=C:/work/8005-scratch/cs904-mutations
HL=C:/work/8005-workspace-v2/Invoke-HeavyLocal.ps1
cd "$WT"
test -z "$(git status --porcelain -- src tests)" || { echo "worktree not clean"; exit 2; }
HEAD_SHA=$(git rev-parse HEAD)
F=src/ControlServer.Host/Runtime/StagingPickupStops.cs

mutate() {
  local name=$1 expr=$2
  echo "== $name" | tee -a "$OUT/summary.txt"
  sed -i "$expr" "$F"
  local changed
  changed=$(git diff --name-only | wc -l)
  [ "$changed" -eq 1 ] || { echo "expected exactly one changed file, got $changed"; git checkout -- "$F"; exit 3; }
  git diff > "$OUT/$name.diff"
  [ -s "$OUT/$name.diff" ] || { echo "sed changed nothing"; exit 4; }
  pwsh -File "$HL" -Ticket cs#904 dotnet build tests/ControlServer.Tests/ControlServer.Tests.csproj --no-incremental -v q -nologo > "$OUT/$name.build.log" 2>&1 || { echo "build failed" | tee -a "$OUT/summary.txt"; git checkout -- "$F"; return; }
  pwsh -File "$HL" -Ticket cs#904 dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj --no-build \
    --filter "FullyQualifiedName~StagingLoadCancellationTests" > "$OUT/$name.test.log" 2>&1 || true
  grep -E "^\s+失败 |失败!|已通过!" "$OUT/$name.test.log" | tee -a "$OUT/summary.txt" || true
  git checkout -- "$F"
}

echo "base $HEAD_SHA $(date -Iseconds)" > "$OUT/summary.txt"
# M1: the engine's per-round recognition ignores removed memberships again.
mutate M1-recognize-live-only 's/IsStagingPickupAsync(dbContext, stop, EverAtStop(stops, stop)/IsStagingPickupAsync(dbContext, stop, stops.AllAtStop(stop)/'
# M2: the out-of-engine entry (receipts, dashboard, recovery coordinator) ignores removed memberships again.
mutate M2-current-live-only 's/IsStagingPickupAsync(dbContext, current, EverAtStop(stops, current)/IsStagingPickupAsync(dbContext, current, stops.AllAtStop(current)/'
# M3: the helper drops the removed memberships (both entries at once: the pre-fix behaviour).
mutate M3-helper-drops-left 's/\[\.\. stops\.AllAtStop(stop), \.\. stops\.LeftAtStop(stop)\]/[.. stops.AllAtStop(stop)]/'
git diff --quiet || { echo "worktree dirty after mutations"; exit 5; }
echo "done $(date -Iseconds)" >> "$OUT/summary.txt"
