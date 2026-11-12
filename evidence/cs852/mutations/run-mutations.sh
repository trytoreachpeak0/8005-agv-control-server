#!/usr/bin/env bash
# cs#852 mutations: one line of one file each, runtime-false conditions, restored with git checkout after each run.
set -o pipefail
WT=C:/work/8005-wt/cs852-8005-agv-control-server
OUT=C:/work/8005-scratch/cs852/mutations
C=src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs
P=src/ControlServer.Host/Transport/OnboardMessageProcessor.cs
F='Environment.TickCount64 < 0'
T='Environment.TickCount64 > 0'
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

run M01-refusal-ignores-grant   $C 2833 "s/is { } ungranted)/is { } ungranted \&\& $F)/"
run M02-readings-ignore-grant   $C 2648 "s/is { } ungranted)/is { } ungranted \&\& $F)/"
run M03-answer-ignores-grant    $C 2758 "s/is { } ungranted)/is { } ungranted \&\& $F)/"
run M04-no-reask-limit          $C 2826 "s/attempt >= ReleaseCheckReaskLimit)/attempt >= ReleaseCheckReaskLimit \&\& $F)/"
run M05-fixed-check-message-id  $C 2868 "s/attempt == 0 ?/attempt == 0 || $T ?/"
run M06-fixed-check-id          $C 2872 "s/attempt == 0 ?/attempt == 0 || $T ?/"
run M07-reask-skips-readings    $C 2668 "s/if (!proven)/if (!proven \&\& (attempt == 0 || $F))/"
run M08-old-check-not-settled   $C 2838 "s/await store.SettleAnsweredCommandAsync/if ($F) await store.SettleAnsweredCommandAsync/"
run M09-old-check-id-correlates $C 2740 "s/== correlationId);/== correlationId || ReleaseCheckId(row.WorkflowId, 0) == correlationId);/"
run M10-no-readings-request     $P 1125 "s/ReleaseCheckRefusal.AskedAgain)/ReleaseCheckRefusal.AskedAgain \&\& $F)/"
run M11-expired-spends-as-before $C 2820 "s/if (reasonCode != PredepartureCheckExpired)/if (reasonCode != PredepartureCheckExpired || $T)/"
run M12-not-allowed-asked-again $C 2820 "s/if (reasonCode != PredepartureCheckExpired)/if (reasonCode != PredepartureCheckExpired \&\& $F)/"
dotnet build tests/ControlServer.Tests -v q -nologo > "$OUT/restore-build.txt" 2>&1; echo "restore build exit=$?"
