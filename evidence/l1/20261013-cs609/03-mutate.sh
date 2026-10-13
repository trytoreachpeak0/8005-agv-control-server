#!/usr/bin/env bash
# cs#609 mutation round on 79674669. One file per round; checked; restored; tree checked clean.
set -u -o pipefail
WT=F:/8005-wt/cs609-8005-agv-control-server
OUT=F:/8005-scratch/cs609-mutations
mkdir -p "$OUT"
cd "$WT" || exit 9
PSM=scripts/parallel/ParallelInstance.psm1
KIT=tests/ControlServer.Tests/JourneyRuntimeWorkerTestKit.cs
DEF=scripts/parallel/instance-factory01-v2.json
INST=scripts/parallel/Install-ParallelInstanceLocal.ps1
CSPROJ=tests/ControlServer.Tests/ControlServer.Tests.csproj

clean() { [ -z "$(git status --porcelain)" ]; }
log() { echo "[$(date '+%H:%M:%S')] $*" | tee -a "$OUT/summary.txt"; }

# $1 id, $2 file, $3 perl expression
mutate() {
  clean || { log "$1 ABORT tree not clean before"; exit 8; }
  perl -0pi -e "$3" "$2"
  local changed; changed=$(git diff --name-only | wc -l)
  local lines; lines=$(git diff --numstat | awk '{s+=$1+$2} END {print s+0}')
  if [ "$changed" -ne 1 ] || [ "$lines" -eq 0 ]; then
    log "$1 VOID mutation did not apply (files=$changed lines=$lines)"; git checkout -q -- "$2"; return 1
  fi
  git diff > "$OUT/$1.diff"
  log "$1 applied to $2 ($lines changed lines)"
}
restore() { git checkout -q -- "$1"; clean && log "$2 restored, tree clean" || { log "$2 RESTORE FAILED"; exit 7; }; }

selftest() { # $1 id
  pwsh -NoProfile -File scripts/parallel/Test-ParallelInstance.ps1 > "$OUT/$1-selftest.txt" 2>&1
  local rc=$?
  log "$1 selftest exit=$rc $(tail -1 "$OUT/$1-selftest.txt")"
  grep "^  FAIL" "$OUT/$1-selftest.txt" | sed "s/^/    $1 /" | tee -a "$OUT/summary.txt" | head -0
}
dotest() { # $1 id, $2 filter
  dotnet test $CSPROJ --no-build -nologo --filter "$2" > "$OUT/$1-dotnet.txt" 2>&1
  local rc=$?
  log "$1 dotnet exit=$rc $(grep -E '^(Passed|Failed)!' "$OUT/$1-dotnet.txt" | tail -1)"
  grep -E "^\s+Failed " "$OUT/$1-dotnet.txt" | sed "s/^/    $1 /" | tee -a "$OUT/summary.txt" | head -0
}

log "start HEAD=$(git rev-parse HEAD)"
dotnet build $CSPROJ -v q -nologo > "$OUT/build-head.txt" 2>&1; log "build HEAD exit=$?"

# M2: a definition back to 00:30:00 (selftest + S1 binding test; tests read the repo files at run time)
if mutate M2 $DEF 's/"cargoHoldingTimeout": "00:05:00"/"cargoHoldingTimeout": "00:30:00"/'; then
  selftest M2; dotest M2 "FullyQualifiedName~EveryShippedDefinitionBindsTheApprovedPairOverThePackageAppSettings"; restore $DEF M2; fi

# M3: the explicit-presence check does nothing
if mutate M3 $PSM "s/(\\} elseif \\(-not \\(Test-KeyPresent -Node \\\$journey -Key 'cargoHoldingTimeout'\\)\\) \\{\\r?\\n)\\s+\\\$failures \\+= 'journeyRuntime\\.cargoHoldingTimeout must be set explicitly[^\\n]*\\n/\$1/"; then
  selftest M3; restore $PSM M3; fi

# M4: the ceiling check never fires
if mutate M4 $PSM 's/elseif \(\$holdingSpan -gt \[timespan\]::FromMinutes\(5\)\)/elseif (\$false)/'; then
  selftest M4; restore $PSM M4; fi

# P1: the -ForStopDirection exemption removed
if mutate P1 $PSM 's/        if \(\$ForStopDirection\) \{\n            # Tolerated: see above\. Installing/        if (\$false) {\n            # Tolerated: see above. Installing/'; then
  selftest P1; restore $PSM P1; fi

# P2: the installer passes -ForStopDirection
if mutate P2 $INST 's/(\$null = Assert-ParallelInstanceDefinition -Definition \$definition -AllowRiotCreateDispatch:\$AllowRiotCreateDispatch `\r?\n\s+-AllowRiotForeignOrderCancel:\$AllowRiotForeignOrderCancel)/$1 -ForStopDirection/'; then
  selftest P2; restore $INST P2; fi

# P3a: -gt becomes -ge (00:05:00 itself refused)
if mutate P3a $PSM 's/elseif \(\$holdingSpan -gt \[timespan\]::FromMinutes\(5\)\)/elseif (\$holdingSpan -ge [timespan]::FromMinutes(5))/'; then
  selftest P3a; restore $PSM P3a; fi

# P3b: the ceiling moves to six minutes
if mutate P3b $PSM 's/elseif \(\$holdingSpan -gt \[timespan\]::FromMinutes\(5\)\)/elseif (\$holdingSpan -gt [timespan]::FromMinutes(6))/'; then
  selftest P3b; restore $PSM P3b; fi

# M1: the fixture's explicit thirty minutes removed (needs a build)
if mutate M1 $KIT 's/,\n(\s+\/\/ Thirty minutes, stated:.*?\n)(\s+\/\/.*?\n){2}\s+CargoHoldingTimeout = TimeSpan\.FromMinutes\(30\)\n/,\n/s'; then
  dotnet build $CSPROJ -v q -nologo > "$OUT/M1-build.txt" 2>&1; log "M1 build exit=$?"
  dotest M1 "FullyQualifiedName~Batch7CargoHoldingTests|FullyQualifiedName~Batch7StationYieldTests"
  restore $KIT M1; fi

# P6: #676 head merged locally on a throwaway branch, never pushed
P6HEAD=1da4ae770
git switch -q -c tmp/cs609-p6 && log "P6 on tmp/cs609-p6"
if git merge -q --no-edit $P6HEAD > "$OUT/P6-merge.txt" 2>&1; then
  log "P6 merge clean: $(git rev-parse --short HEAD) ($(git diff --name-only HEAD~1 HEAD | wc -l) files from #676 side vs ours)"
  selftest P6
  grep -c "cargo holding: instance-factory01-v2.*states journeyRuntime.cargoHoldingTimeout" "$OUT/P6-selftest.txt" | sed 's/^/    P6 per-definition holding cases: /' | tee -a "$OUT/summary.txt"
  dotnet build $CSPROJ -v q -nologo > "$OUT/P6-build.txt" 2>&1; log "P6 build exit=$?"
  dotest P6 "FullyQualifiedName~ParallelInstanceEffectiveConfigurationTests"
else
  log "P6 MERGE CONFLICT"; git diff --name-only --diff-filter=U | sed 's/^/    P6 conflict /' | tee -a "$OUT/summary.txt"; git merge --abort
fi
git switch -q change/cs609-cargo-holding-5min && git branch -q -D tmp/cs609-p6 && clean && log "P6 back on branch, tmp deleted, tree clean, HEAD=$(git rev-parse --short HEAD)"
dotnet build $CSPROJ -v q -nologo > "$OUT/build-final.txt" 2>&1; log "rebuild HEAD exit=$?"
log "done"
