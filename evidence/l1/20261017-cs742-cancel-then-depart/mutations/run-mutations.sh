#!/usr/bin/env bash
# cs#742 local slot round 1 (segment one): L1 mutations, one test class each, through Invoke-HeavyLocal.
set -u -o pipefail
cd F:/8005-wt/fti-742-8005-agv-control-server || exit 9
SCRATCH=C:/Users/szy/AppData/Local/Temp/claude/C--Users-szy-Desktop-8005-workspace-v2/bd99f982-fb6d-4faa-9d08-595c7537c5ad/scratchpad
OUT="$SCRATCH/mutations"
mkdir -p "$OUT"
SUMMARY="$OUT/summary.txt"
: > "$SUMMARY"
HEAVY=C:/Users/szy/Desktop/8005-workspace-v2/Invoke-HeavyLocal.ps1
BASE_HEAD=$(git rev-parse HEAD)

# name|filter|description
ROUNDS=(
  "M1|CancelledLastEntryEndsIntoDepartureWaitTests|review 1: EveryDemandAtCurrentStopEnded counts Loaded as ended"
  "M2|CancelledLastEntryEndsIntoDepartureWaitTests|review 2: new branch ahead of the same-station grace"
  "M3|CancelledLastEntryEndsIntoDepartureWaitTests.AStopFoundEmptiedInTheStoreSendsTheVehicleOnInTheFirstRound|review 3: JourneyRuntimeEngine.cs as at a799c998 (replay test, red first)"
  "M4|StaleWorklistDepartureTests|review 4: version loop checks offset 0 only"
  "M5|StaleWorklistDepartureTests|review 5: correction check never refuses"
  "M6|StagingLoadCancellationTests|review 6: both staging exclusions of the new branch removed (expected: no red)"
  "M7|StaleWorklistDepartureTests|own: condition 1 range check removed (expected: no red, the per-stop message id lookup also refuses)"
  "M8|StaleWorklistDepartureTests|own: more-loaded side never refuses on time"
  "M9|StaleWorklistDepartureTests|own: fewer-loaded side removed"
  "M10|StagingLoadCancellationTests|own: staging exclusion of the stale rule removed"
  "M11|StaleWorklistDepartureTests|own: condition 3 items check removed"
)

for round in "${ROUNDS[@]}"; do
  IFS='|' read -r name filter description <<< "$round"
  if [ -n "$(git status --porcelain)" ]; then echo "$name: work tree not clean before the round, stopping" | tee -a "$SUMMARY"; exit 8; fi
  if [ "$name" = "M3" ]; then
    file=src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs
    git show a799c998:"$file" > "$file" || { echo "M3: base checkout failed" | tee -a "$SUMMARY"; exit 7; }
  else
    file=$(python "$SCRATCH/mutate.py" "$name") || { echo "$name: mutation did not apply" | tee -a "$SUMMARY"; exit 7; }
  fi
  changed=$(git diff --name-only | wc -l)
  if [ "$changed" -ne 1 ]; then echo "$name: $changed files changed, expected 1, stopping" | tee -a "$SUMMARY"; git checkout -q HEAD -- "$file"; exit 6; fi
  git commit -q -am "tmp: cs#742 mutation $name (local only, reset after the round)" || { echo "$name: temporary commit failed" | tee -a "$SUMMARY"; git checkout -q HEAD -- "$file"; exit 6; }
  start=$(date '+%H:%M:%S')
  pwsh -File "$HEAVY" -Ticket 'cs#742' dotnet test tests/ControlServer.Tests --filter "FullyQualifiedName~$filter" > "$OUT/$name.txt" 2>&1
  code=$?
  end=$(date '+%H:%M:%S')
  git reset -q --soft HEAD~1
  git checkout -q HEAD -- "$file"
  git reset -q -- "$file" 2>/dev/null
  if [ "$(git rev-parse HEAD)" != "$BASE_HEAD" ] || [ -n "$(git status --porcelain)" ]; then
    echo "$name: restore left HEAD $(git rev-parse --short HEAD) or a dirty tree, stopping" | tee -a "$SUMMARY"; exit 5
  fi
  fails=$(grep -oE "ControlServer\.Tests\.[A-Za-z]+\.[A-Za-z]+ \[FAIL\]" "$OUT/$name.txt" | sed 's/ \[FAIL\]//; s/ControlServer\.Tests\.//' | sort -u | paste -sd ',' -)
  totals=$(grep -E "^(Failed|Passed)!" "$OUT/$name.txt" | tail -1)
  echo "$name | $start-$end | exit $code | red: ${fails:-none} | $totals | $description" | tee -a "$SUMMARY"
  if [ "$code" -eq 3 ]; then echo "exit 3 from Invoke-HeavyLocal, stopping as instructed" | tee -a "$SUMMARY"; exit 3; fi
done
echo "all rounds done, HEAD $(git rev-parse --short HEAD), tree clean" | tee -a "$SUMMARY"
