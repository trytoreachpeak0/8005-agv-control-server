#!/usr/bin/env bash
# cs#906 mutation run. Each mutation edits exactly one file at one checked line, rebuilds with
# --no-incremental, runs the relevant classes, records the outcome, then restores only that file.
set -euo pipefail
set -o pipefail
WT=C:/work/8005-wt/cs906-8005-agv-control-server
OUT=C:/work/8005-scratch/cs906-mutations
HL=C:/work/8005-workspace-v2/Invoke-HeavyLocal.ps1
cd "$WT"
test -z "$(git status --porcelain -- src tests)" || { echo "worktree not clean"; exit 2; }
HEAD_SHA=$(git rev-parse HEAD)
ENGINE=src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs
FILTER='FullyQualifiedName~StagingLoadCancellationTests|FullyQualifiedName~LoadEndedAtASecondPickupTests|FullyQualifiedName~Batch7CargoHoldingDashboardTests|FullyQualifiedName~ReconnectModelTests|FullyQualifiedName~JourneyStopSequenceRaceTests'

# mutate <name> <file> <line> <expected substring on that line> <sed expression applied to that line>
mutate() {
  local name=$1 file=$2 line=$3 expect=$4 expr=$5
  echo "== $name ($file:$line)" | tee -a "$OUT/summary.txt"
  sed -n "${line}p" "$file" | grep -qF -- "$expect" || { echo "line $line does not hold the expected text" | tee -a "$OUT/summary.txt"; exit 5; }
  sed -i "${line}${expr}" "$file"
  local changed
  changed=$(git diff --name-only | wc -l)
  [ "$changed" -eq 1 ] || { echo "expected exactly one changed file, got $changed"; git checkout -- "$file"; exit 3; }
  git diff > "$OUT/$name.diff"
  [ -s "$OUT/$name.diff" ] || { echo "sed changed nothing"; exit 4; }
  if ! pwsh -File "$HL" -Ticket cs#906 dotnet build tests/ControlServer.Tests/ControlServer.Tests.csproj --no-incremental -v q -nologo > "$OUT/$name.build.log" 2>&1; then
    echo "build failed" | tee -a "$OUT/summary.txt"; git checkout -- "$file"; return
  fi
  pwsh -File "$HL" -Ticket cs#906 dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj --no-build \
    --filter "$FILTER" > "$OUT/$name.test.log" 2>&1 || true
  grep -E "^\s+失败 |失败!|已通过!" "$OUT/$name.test.log" | tee -a "$OUT/summary.txt" || true
  git checkout -- "$file"
}

echo "base $HEAD_SHA $(date -Iseconds)" > "$OUT/summary.txt"

# M1: the self-healing resume is gated on recognising the stop as staging again (the cs#904 shape).
mutate M1-resume-needs-staging "$ENGINE" 1886 'bool releasedWhileLoadingHere = resumingAfterCommit && !loadedHere &&' \
  's/!loadedHere \&\&$/!loadedHere \&\& IsStagingPickup(stops.Current) \&\&/'
# M2: the unexplained shape throws every round again (the field exception), via a runtime-true condition.
mutate M2-hold-throws-again "$ENGINE" 1322 'if (string.Equals(runtime.BlockReasonCode, LoadResultWithNothingLoadingReason' \
  's/^/        if (runtime.JourneyId.Length > 0) { throw new InvalidDataException($"Journey {runtime.JourneyId} waits for a load result with no demand loading at its stop."); }\n/'
# M3: the code is never cleared once the database is put right.
mutate M3-never-cleared "$ENGINE" 1900 'if (string.Equals(runtime.BlockReasonCode, LoadResultWithNothingLoadingReason' \
  's/LoadResultWithNothingLoadingReason, StringComparison/LoadResultWithNothingLoadingReason + "-M3", StringComparison/'
# M4: the hold logs and writes every round instead of once.
mutate M4-logs-every-round "$ENGINE" 1322 'if (string.Equals(runtime.BlockReasonCode, LoadResultWithNothingLoadingReason' \
  's/LoadResultWithNothingLoadingReason, StringComparison/LoadResultWithNothingLoadingReason + "-M4", StringComparison/'
# M5: the engine lets the held vehicle into underWay (offered en-route appends).
mutate M5-engine-underway "$ENGINE" 931 'string.Equals(row.BlockReasonCode, LoadResultWithNothingLoadingReason' \
  's/LoadResultWithNothingLoadingReason, StringComparison/LoadResultWithNothingLoadingReason + "-M5", StringComparison/'
# M6: the dispatch round reads an en-route plan for the held journey.
mutate M6-dispatch-plan src/ControlServer.Host/Runtime/Dispatch/DispatchRoundRunner.cs 763 'JourneyRuntimeEngine.LoadResultWithNothingLoadingReason' \
  's/LoadResultWithNothingLoadingReason, StringComparison/LoadResultWithNothingLoadingReason + "-M6", StringComparison/'
# M7: the release service releases from the held journey.
mutate M7-release-service src/ControlServer.Host/Runtime/Release/DemandReleaseService.cs 163 'JourneyRuntimeEngine.LoadResultWithNothingLoadingReason' \
  's/LoadResultWithNothingLoadingReason, StringComparison/LoadResultWithNothingLoadingReason + "-M7", StringComparison/'
# M8: the code is not a wait on a person (a failed advance may overwrite it).
mutate M8-not-wait-on-person "$ENGINE" 6867 'string.Equals(runtime.BlockReasonCode, LoadResultWithNothingLoadingReason' \
  's/LoadResultWithNothingLoadingReason, StringComparison/LoadResultWithNothingLoadingReason + "-M8", StringComparison/'
# M9: the readiness gate overwrites it with the session code.
mutate M9-gate-overwrites "$ENGINE" 1457 '!string.Equals(runtime.BlockReasonCode, LoadResultWithNothingLoadingReason' \
  's/LoadResultWithNothingLoadingReason, StringComparison/LoadResultWithNothingLoadingReason + "-M9", StringComparison/'
# M10: the dashboard has no description for it.
mutate M10-no-dashboard-text src/ControlServer.Host/Dashboard/BlockedJourneysQueryEndpoint.cs 136 '[JourneyRuntimeEngine.LoadResultWithNothingLoadingReason]' \
  's/LoadResultWithNothingLoadingReason\]/LoadResultWithNothingLoadingReason + "-M10"]/'

echo "done $(date -Iseconds)" | tee -a "$OUT/summary.txt"
