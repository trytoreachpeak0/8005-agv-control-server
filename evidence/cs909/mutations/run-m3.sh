#!/usr/bin/env bash
# cs#909 mutations: each applies exactly one textual change to one file, runs the test class, restores that file only.
set -o pipefail
cd C:/work/8005-wt/cs909-8005-agv-control-server || exit 9
OUT=C:/work/8005-scratch/cs909-mutations
RW=src/ControlServer.Host/Runtime/RemainingStopWorklist.cs
CO=src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs
mutate() { # name file old new
  local name=$1 file=$2 old=$3 new=$4
  python -I - "$file" "$old" "$new" <<'PY' || { echo "$name: mutation did not apply" | tee -a $OUT/summary.txt; return; }
import sys
p,old,new=sys.argv[1],sys.argv[2],sys.argv[3]
s=open(p,encoding='utf-8').read()
assert s.count(old)==1, s.count(old)
open(p,'w',encoding='utf-8',newline='').write(s.replace(old,new))
PY
  changed=$(git status --porcelain -- src | wc -l)
  [ "$changed" = "1" ] || { echo "$name: expected one changed file, got $changed" | tee -a $OUT/summary.txt; git checkout -- "$file"; return; }
  git diff -- "$file" > $OUT/$name.diff
  pwsh -File C:/work/8005-workspace-v2/Invoke-HeavyLocal.ps1 -Ticket cs#909 pwsh -NoProfile -Command 'dotnet build tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-incremental -v q && dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~CancelBeforeScanRefreshesWorklistTests"' > $OUT/$name.log 2>&1
  local rc=$?
  git checkout -- "$file"
  echo "$name: exit=$rc $(grep -E '失败!|通过!' $OUT/$name.log | tail -1)" | tee -a $OUT/summary.txt
  grep -E "^\s+失败 " $OUT/$name.log | sed 's/^/    /' | tee -a $OUT/summary.txt
}
mutate M3-no-entry-request "$RW" "        if (runtime.Stage == JourneyRuntimeStage.AwaitingSublot)
        {" "        if (runtime.Stage == JourneyRuntimeStage.AwaitingSublot && runtime.AgvId.Length < 0)
        {"
echo "final status: $(git status --porcelain -- src | wc -l) changed src files" | tee -a $OUT/summary.txt
