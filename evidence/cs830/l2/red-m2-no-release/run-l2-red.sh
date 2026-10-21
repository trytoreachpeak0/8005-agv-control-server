#!/usr/bin/env bash
set -o pipefail
W=C:/work/8005-wt/cs-830-8005-agv-control-server
cd $W || exit 9
while pwsh -File C:/work/8005-workspace-v2/Get-HeavyLocalStatus.ps1 2>&1 | grep -q "Invoke-L2Scenario"; do sleep 30; done
[ -z "$(git status --porcelain)" ] || { echo "dirty"; exit 8; }
F=src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs
n=$(grep -c '            membership.TargetSlotsJson = loadedJson;' $F); [ "$n" = 1 ] || { echo "match=$n"; exit 7; }
sed -i 's/^            membership.TargetSlotsJson = loadedJson;$/            _ = loadedJson;/' $F
[ "$(git diff --name-only)" = "$F" ] || { echo "unexpected diff"; git checkout HEAD -- $F; exit 6; }
pwsh -File C:/work/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket cs#830 pwsh -File scripts/l2/Invoke-L2Scenario.ps1 -Scenario load-remaining-baskets-skipped -EvidenceRoot C:/work/8005-scratch/cs830-l2/red-m2-no-release > C:/work/8005-scratch/cs830-l2/red-m2.log 2>&1
rc=$?
git checkout HEAD -- $F
echo "l2-exit=$rc"; tail -2 C:/work/8005-scratch/cs830-l2/red-m2.log; git status --porcelain
