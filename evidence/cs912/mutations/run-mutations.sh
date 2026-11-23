#!/usr/bin/env bash
# control-server#912 mutations. Usage: bash run-mutations.sh <result-file>. Each mutation edits one file in place, checks that
# exactly one file changed, runs the DisconnectedOnboard tests, and restores that file. Round 1 ran M1-M4 with the first four
# tests (M3 survived); round 2 ran M3 alone with the fifth test added (see summary.txt).
set -o pipefail
cd C:/work/8005-wt/cs912-8005-agv-control-server || exit 9
out="$1"; : > "$out"
filter="FullyQualifiedName~MultiVehicleExecutionTests.AVehicleWhoseOnboardDisconnected|FullyQualifiedName~MultiVehicleExecutionTests.AnIdleReturnCommittedBeforeTheDisconnect|FullyQualifiedName~MultiVehicleExecutionTests.AConnectionThatEndsWhileTheDemandIsBeingAccepted"
mutate() { # name file perl-expr
  local name="$1" file="$2" expr="$3"
  [ -z "$(git status --porcelain)" ] || { echo "$name: tree dirty before" >>"$out"; exit 8; }
  perl -0pi -e "$expr" "$file"
  local changed; changed=$(git diff --numstat | awk '{print $1"+"$2" "$3}')
  if [ "$(git diff --name-only | wc -l)" != "1" ]; then echo "$name: mutation did not apply ($changed)" >>"$out"; git checkout -- "$file"; return; fi
  echo "== $name ($changed)" >>"$out"
  pwsh -File C:/work/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket cs#912 dotnet test tests/ControlServer.Tests -c Release --filter "$filter" > "$out.$name.log" 2>&1
  echo "exit=$?" >>"$out"
  grep -E "^\s+(失败|Failed) ControlServer|已通过!|失败!|error CS" "$out.$name.log" >>"$out"
  git checkout -- "$file"
}
R=src/ControlServer.Host/Runtime/Dispatch/OnboardDispatchFactsReader.cs
D=src/ControlServer.Host/Runtime/Dispatch/DispatchRoundRunner.cs
G=src/ControlServer.Host/Runtime/Dispatch/NonBusinessDepartureGate.cs
mutate M1-reader-ignores-connection "$R" 's/facts is not null && IsConnectedAs\(agvId, facts.SessionGeneration\) \? facts : null/facts is not null \&\& (IsConnectedAs(agvId, facts.SessionGeneration) || agvId.Length > 0) ? facts : null/'
mutate M2-round-read-unguarded "$D" 's/\.ReadOnboardFactsForNewPurposeAsync\(vehicle\.AgvId, linked\.Token\)/.ReadOnboardFactsAsync(vehicle.AgvId, linked.Token)/'
mutate M3-final-recheck-unguarded "$D" 's/onboardFacts\.ReadOnboardFactsForNewPurposeAsync\(fleetVehicle\.AgvId, cancellationToken\)/onboardFacts.ReadOnboardFactsAsync(fleetVehicle.AgvId, cancellationToken)/'
mutate M4-departure-gate-unguarded "$G" 's/if \(!onboardFacts\.IsConnectedAs\(agvId, session\.SessionGeneration\)\)/if (!onboardFacts.IsConnectedAs(agvId, session.SessionGeneration) \&\& agvId.Length < 0)/'
git status --short >>"$out"
echo DONE >>"$out"
