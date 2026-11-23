#!/usr/bin/env bash
# control-server#912 review rework mutations (round 4, on dce9a7653; see summary.txt). Same rules as run-mutations.sh.
set -o pipefail
cd C:/work/8005-wt/cs912-8005-agv-control-server || exit 9
out="$1"; : > "$out"
filter="FullyQualifiedName~MultiVehicleExecutionTests.AVehicleWhoseOnboardDisconnected|FullyQualifiedName~MultiVehicleExecutionTests.AnIdleReturnCommittedBeforeTheDisconnect|FullyQualifiedName~MultiVehicleExecutionTests.AConnectionThatEnds|FullyQualifiedName~JourneyRuntimeWorkerDepartureSafetyTests|FullyQualifiedName~OwnOrderRebuildTests"
mutate() {
  local name="$1" file="$2" expr="$3"
  [ -z "$(git status --porcelain)" ] || { echo "$name: tree dirty before" >>"$out"; exit 8; }
  perl -0pi -e "$expr" "$file"
  if [ "$(git diff --name-only | wc -l)" != "1" ]; then echo "$name: mutation did not apply" >>"$out"; git checkout -- "$file"; return; fi
  echo "== $name ($(git diff --numstat | awk '{print $1"+"$2" "$3}'))" >>"$out"
  git diff > "$out.$name.diff"
  pwsh -File C:/work/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket cs#912 dotnet test tests/ControlServer.Tests -c Release --filter "$filter" > "$out.$name.log" 2>&1
  echo "exit=$?" >>"$out"
  grep -E "^\s+(失败|Failed) ControlServer|已通过!|失败!|error CS" "$out.$name.log" >>"$out"
  git checkout -- "$file"
}
E=src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs
G=src/ControlServer.Host/Runtime/Dispatch/NonBusinessDepartureGate.cs
mutate M6-departure-leg-unguarded "$E" 's/if \(!onboardFacts\.IsConnectedAs\(runtime\.AgvId, session\.SessionGeneration\)\)/if (!onboardFacts.IsConnectedAs(runtime.AgvId, session.SessionGeneration) \&\& runtime.AgvId.Length < 0)/'
mutate M4b-session-gap-cell-unguarded "$G" 's/if \(!onboardFacts\.IsConnectedAs\(agvId, session\.SessionGeneration\)\)/if (!onboardFacts.IsConnectedAs(agvId, session.SessionGeneration) \&\& agvId.Length < 0)/'
echo DONE >>"$out"
