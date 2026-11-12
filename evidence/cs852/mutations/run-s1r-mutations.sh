#!/usr/bin/env bash
# cs#852 review S1 mutations on 7ff428189: one line of OnboardMessageProcessor.cs each, runtime-false conditions,
# restored with git checkout after each run.
set -o pipefail
WT=C:/work/8005-wt/cs852-8005-agv-control-server
OUT=C:/work/8005-scratch/cs852/mutations
P=src/ControlServer.Host/Transport/OnboardMessageProcessor.cs
F='Environment.TickCount64 < 0'
cd "$WT" || exit 1

run() {
  local name=$1 file=$2 line=$3 expr=$4
  git diff --quiet || { echo "dirty before $name"; exit 1; }
  sed -i "${line}${expr}" "$file"
  local changed; changed=$(git diff --name-only)
  local lines; lines=$(git diff -U0 | grep -c '^+[^+]')
  if [ "$changed" != "$file" ] || [ "$lines" != "1" ]; then
    echo "$name: mutation did not change exactly one line of $file ($changed / $lines)"; git checkout -- "$file"; exit 1
  fi
  git diff > "$OUT/$name.diff"
  if dotnet build tests/ControlServer.Tests -v q -nologo > "$OUT/$name-build.txt" 2>&1; then
    pwsh -File C:/work/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket cs#852 dotnet test tests/ControlServer.Tests --no-build \
      --filter "FullyQualifiedName~RecoveryStateMachineG2Tests" > "$OUT/$name.txt" 2>&1
    echo "$name exit=$? $(grep -E '失败!|已通过!' "$OUT/$name.txt")"
    grep -E '^\s*失败 ' "$OUT/$name.txt" | sed 's/^\s*失败 ControlServer.Tests.RecoveryStateMachineG2Tests./    /'
  else
    echo "$name: build failed"; grep ' error ' "$OUT/$name-build.txt" | head -3
  fi
  git checkout -- "$file"
  git diff --quiet || { echo "not restored after $name"; exit 1; }
}

run S1ar-wait-not-in-reask-condition $P 382 "s/state.ReleaseReaskReadingsSince is not null) \&\&/(state.ReleaseReaskReadingsSince is not null \&\& $F)) \&\&/"
run S1br-wait-never-marked          $P 1151 "s/state.ReleaseReaskReadingsSince ??= timeProvider.GetUtcNow();/if ($F) state.ReleaseReaskReadingsSince ??= timeProvider.GetUtcNow();/"
run S1cr-snapshot-does-not-clear    $P 642 "s/state.ReleaseReaskReadingsSince = null;/if ($F) state.ReleaseReaskReadingsSince = null;/"
run S1dr-no-overdue-warning         $P 390 "s/waited >= ReleaseReaskReadingsOverdueAfter)/waited >= ReleaseReaskReadingsOverdueAfter \&\& $F)/"
dotnet build tests/ControlServer.Tests -v q -nologo > "$OUT/restore-build-s1r.txt" 2>&1; echo "restore build exit=$?"
