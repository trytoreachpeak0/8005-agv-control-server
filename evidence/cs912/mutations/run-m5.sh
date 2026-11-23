set -o pipefail
cd C:/work/8005-wt/cs912-8005-agv-control-server || exit 9
out="$1"; : > "$out"
F=src/ControlServer.Host/Runtime/JourneyRuntimeEngine.OwnOrderRebuild.cs
[ -z "$(git status --porcelain)" ] || { echo dirty >>"$out"; exit 8; }
perl -0pi -e 's/onboardFacts\.ReadOnboardFactsForNewPurposeAsync\(runtime\.AgvId, cancellationToken\)/onboardFacts.ReadOnboardFactsAsync(runtime.AgvId, cancellationToken)/' $F
git diff --numstat >>"$out"; git diff > "$out.diff"
[ "$(git diff --name-only | wc -l)" = "1" ] || { echo "not applied" >>"$out"; exit 7; }
pwsh -File C:/work/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket cs#912 dotnet test tests/ControlServer.Tests -c Release --filter "FullyQualifiedName~OwnOrderRebuildTests|FullyQualifiedName~MultiVehicleExecutionTests.AVehicleWhoseOnboardDisconnected|FullyQualifiedName~MultiVehicleExecutionTests.AnIdleReturnCommittedBeforeTheDisconnect|FullyQualifiedName~MultiVehicleExecutionTests.AConnectionThatEnds" > "$out.log" 2>&1
echo "exit=$?" >>"$out"
grep -E "^\s+失败 ControlServer|已通过!|失败!|error CS" "$out.log" >>"$out"
git checkout -- $F
