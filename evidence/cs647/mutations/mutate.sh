#!/usr/bin/env bash
# cs#647 mutation run: each mutation edits only EnRouteAppendPlanner.cs, runs one test class, restores.
set -u -o pipefail
cd F:/8005-wt/cs647-8005-agv-control-server || exit 1
F=src/ControlServer.Host/Runtime/Dispatch/EnRouteAppendPlanner.cs
OUT=F:/8005-scratch/cs647/mutations
mkdir -p "$OUT"
[ -z "$(git status --porcelain)" ] || { echo "worktree dirty, abort"; exit 1; }

run() {
  name=$1; shift
  while [ $# -ge 2 ]; do
    FROM="$1" TO="$2" perl -0pi -e 's/\Q$ENV{FROM}\E/$ENV{TO}/ or die "pattern not found\n"' "$F" || { git checkout -- "$F"; echo "$name: PATTERN NOT FOUND"; return; }
    shift 2
  done
  changed=$(git status --porcelain | wc -l)
  [ "$changed" -eq 1 ] || { echo "$name: expected 1 changed file, got $changed"; git status --porcelain; exit 1; }
  git diff -U0 -- "$F" > "$OUT/$name.diff"
  dotnet test tests/ControlServer.Tests --filter "FullyQualifiedName~Batch7EnRouteAppendPlannerTests" > "$OUT/$name.txt" 2>&1
  git checkout -- "$F"
  [ -z "$(git status --porcelain)" ] || { echo "$name: restore failed"; exit 1; }
  echo "== $name"; grep -E "^\s+Failed |Passed!|Failed!|error CS" "$OUT/$name.txt"
}

run M1-no-tie-break \
  '(marginal == best.MarginalCostMm && loadsBeforeUnload < bestLoadsBeforeUnload))' 'false)'
run M2-tie-break-on-equal-count \
  'loadsBeforeUnload < bestLoadsBeforeUnload))' 'loadsBeforeUnload <= bestLoadsBeforeUnload))'
run M3-preference-over-cost \
  'marginal < best.MarginalCostMm ||' 'loadsBeforeUnload < bestLoadsBeforeUnload || (loadsBeforeUnload == bestLoadsBeforeUnload && marginal < best.MarginalCostMm) ||'
run M4-adjacent-only \
  'for (int unload = pickup + 1; unload < stops.Count; unload++)' 'for (int unload = pickup + 1; unload < Math.Min(pickup + 2, stops.Count); unload++)'
run M5-ignore-station \
  ' &&
                    stops[unload].StationRiotId == stops[pickup].StationRiotId)' ')'
run M6-ignore-role \
  'if (stops[unload].Role == JourneyStopRoles.Unload &&' 'if (stops.Count >= 0 &&'   'if (stops[pickup].Role != JourneyStopRoles.Pickup)' 'if (stops.Count < 0)'
git status --porcelain
echo done
