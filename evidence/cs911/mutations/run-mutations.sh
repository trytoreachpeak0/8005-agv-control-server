#!/usr/bin/env bash
# control-server#911 mutations. Usage: bash run-mutations.sh <result-file>. Each mutation edits one product file in place,
# checks that exactly one file changed, runs the #911 tests (TransportDemandLegTests, TransportDemandLegMigrationTests),
# and restores that one file. Every mutation must turn at least one test red. Optional second argument: the first mutation to
# run (round 1 stopped after M6 because a concurrent git command held the index lock when M6 was restored; see summary-round1.txt).
# Run nothing else against this repository while it runs.
set -o pipefail
cd C:/work/8005-wt/cs911-8005-agv-control-server || exit 9
out="$1"; : > "$out"
from="${2:-M1}"; started=""
filter="FullyQualifiedName~ControlServer.Tests.TransportDemandLegTests|FullyQualifiedName~ControlServer.Tests.TransportDemandLegMigrationTests"
mutate() { # name file perl-expr
  local name="$1" file="$2" expr="$3"
  if [ -z "$started" ]; then [ "${name%%-*}" = "$from" ] || return 0; started=1; fi
  [ -z "$(git status --porcelain -- src tests)" ] || { echo "$name: tree dirty before" >>"$out"; exit 8; }
  perl -0pi -e "$expr" "$file"
  local changed; changed=$(git diff --numstat -- src tests | awk '{print $1"+"$2" "$3}')
  if [ "$(git diff --name-only -- src tests | wc -l)" != "1" ]; then echo "$name: mutation did not apply ($changed)" >>"$out"; git checkout -- "$file"; return; fi
  echo "== $name ($changed)" >>"$out"
  pwsh -File C:/work/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket cs#911 dotnet test tests/ControlServer.Tests -c Release --filter "$filter" > "$out.$name.log" 2>&1
  echo "exit=$?" >>"$out"
  grep -E "^\s+(失败|Failed) ControlServer|已通过!|失败!|error CS" "$out.$name.log" | sed -E 's/ \[[0-9 .ms]+\]$//' >>"$out"
  git checkout -- "$file"
}
I=src/ControlServer.Application/TransportDemandIdentity.cs
C12=src/ControlServer.Host/Runtime/Dispatch/Criteria/TransportDemandKeyAlreadyAcceptedCriterion.cs
S=src/ControlServer.Infrastructure/Persistence/MultiDemandJourneyStores.cs
X=src/ControlServer.Host/Runtime/Dispatch/StarvationExclusions.cs
P=src/ControlServer.Host/Runtime/PickupStopTermination.cs
W=src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs
M=src/ControlServer.Infrastructure/Persistence/Migrations/20261123074049_TransportDemandLeg.cs
mutate M1-no-leg-at-all "$I" 's/&& !string\.IsNullOrWhiteSpace\(step\) \? step : NoLeg/&& !string.IsNullOrWhiteSpace(step) && step.Length < 0 ? step : NoLeg/'
mutate M2-criterion12-by-key "$C12" 's/\.WhereIdentity\(identity\)/.Where(row => row.TransportDemandKey == identity.TransportDemandKey)/'
mutate M3-suppression-read-by-key "$S" 's/\.WhereIdentity\(identity\)\.SingleOrDefaultAsync\(cancellationToken\)/.Where(item => item.TransportDemandKey == identity.TransportDemandKey).FirstOrDefaultAsync(cancellationToken)/'
mutate M4-starvation-by-key "$X" 's/if \(suppressed\.Contains\(identity\) \|\|/if (suppressed.Any(entry => entry.TransportDemandKey == identity.TransportDemandKey) ||/'
mutate M5-ending-skips-by-key "$P" 's/\.WhereIdentity\(identity\)\.AnyAsync\(cancellationToken\)/.Where(row => row.TransportDemandKey == identity.TransportDemandKey).AnyAsync(cancellationToken)/'
mutate M6-ending-writes-no-leg "$P" 's/TransportLeg = identity\.TransportLeg,/TransportLeg = TransportDemandIdentity.NoLeg,/'
mutate M7-completion-replay-by-key "$W" 's/(TransportDemandCompletionRow\? replay = await dbContext\.TransportDemandCompletions\s*)\.WhereIdentity\(demand\.Identity\(\)\)/$1.Where(row => row.TransportDemandKey == demand.TransportDemandKey)/'
mutate M8-completion-writes-no-leg "$W" 's/(TransportDemandKey = transportDemandKey,\s*)TransportLeg = demand\.TransportLeg,/$1TransportLeg = TransportDemandIdentity.NoLeg,/'
mutate M9-acceptance-writes-no-leg "$W" 's/TransportLeg = TransportDemandIdentity\.Of\(snapshot\)\.TransportLeg,/TransportLeg = TransportDemandIdentity.NoLeg,/'
mutate M10-backfill-skips-suppressions "$M" 's/(UPDATE "TransportDemandSuppressions".*?)WHERE EXISTS \(/$1WHERE 0 AND EXISTS (/s'
mutate M11-backfill-every-work-type "$M" 's/WHERE "WorkType" = \x27STAGING_TO_WIRE\x27/WHERE "WorkType" <> \x27\x27/'
mutate M12-rollback-without-collapse "$M" 's/            CollapseLegsForRollback\(migrationBuilder\);\n//'
git status --short -- src tests >>"$out"
echo DONE >>"$out"
