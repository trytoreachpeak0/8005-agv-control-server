#!/usr/bin/env bash
# control-server PR #347 (hmi#206 review A2): reverse verification of the two-cell snapshot-revision guard. Each state:
# one exact replacement (exits unless it matches once), --no-incremental Release rebuild that must print "0 Error(s)",
# the new test N times, then every ReconnectModel* class once; the model file is restored from a backup and its SHA-256
# checked.
set -u
cd /c/Users/szy/Desktop/8005-workspace-v2/worktrees/hmi206-8005-agv-control-server || exit 1
F=tests/ControlServer.Tests/ReconnectModel.cs
OUT=${1:?output file}
N=${2:-10}
E=tests/ControlServer.Tests/bin/Release/net8.0/win-x64/ControlServer.Tests.exe
BACKUP="$(dirname "$OUT")/ReconnectModel.cs.guard.bak"
cp -p "$F" "$BACKUP"
SHA=$(sha256sum "$F" | cut -d' ' -f1)
TEST=ControlServer.Tests.ReconnectModelRegressionTests.TheHandshakeSnapshotTakesTheNextRevisionOnlyWhenTheHandshakeResentASafetyChange
FROM='_resentSafetyChangeThisHandshake ? checked(_acceptedSafetyVersion + 1) : _acceptedSafetyVersion;'

state() { # name replacement
  local name=$1 to=$2
  echo "=== $name" >> "$OUT"
  if [ -n "$to" ]; then
    FROM="$FROM" TO="$to" python -c "
import os,io,sys
p='$F'
s=io.open(p,encoding='utf-8',newline='').read()
n=s.count(os.environ['FROM'])
if n!=1: print('replacement matched',n,'times'); sys.exit(3)
io.open(p,'w',encoding='utf-8',newline='').write(s.replace(os.environ['FROM'],os.environ['TO']))
" >> "$OUT" || exit 3
  fi
  git diff -U0 -- "$F" | grep -E '^[+-][^+-]' >> "$OUT"
  local build
  build=$(timeout 1200 dotnet build tests/ControlServer.Tests -c Release --no-incremental -v q -nologo 2>&1 | grep -E 'Error\(s\)')
  echo "build: $build" >> "$OUT"
  case "$build" in *" 0 Error(s)"*) ;; *) echo "build failed" >> "$OUT"; exit 4;; esac
  for i in $(seq 1 "$N"); do
    local res
    res=$(timeout 600 $E -method "$TEST" 2>&1)
    echo "run $i: $(echo "$res" | grep -E 'Total:' | sed -E 's/, Time:.*//')" >> "$OUT"
    echo "$res" | grep -E '\[FAIL\]|Expected:|Actual:|violation' | sed -E 's/^\s+/  /' >> "$OUT"
  done
  echo "all ReconnectModel* classes once:" >> "$OUT"
  timeout 1500 $E -class "ControlServer.Tests.ReconnectModel*" 2>&1 | grep -E '\[FAIL\]|Total:' >> "$OUT"
  cp -p "$BACKUP" "$F"; touch "$F"
  [ "$(sha256sum "$F" | cut -d' ' -f1)" = "$SHA" ] && echo "restored OK" >> "$OUT" || { echo "restore MISMATCH" >> "$OUT"; exit 5; }
}

: > "$OUT"
echo "head $(git rev-parse HEAD); N=$N" >> "$OUT"
state "FIXED" ""
state "G1 always the next revision" 'checked(_acceptedSafetyVersion + 1);'
state "G2 always the accepted revision (the old onboard rule)" '_acceptedSafetyVersion;'
timeout 1200 dotnet build tests/ControlServer.Tests -c Release --no-incremental -v q -nologo 2>&1 | grep -E 'Error\(s\)' >> "$OUT"
echo done >> "$OUT"
