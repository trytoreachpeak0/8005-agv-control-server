#!/usr/bin/env bash
# control-server PR #347 (hmi#206): large exploration on the committed model and on mutations of it. Each mutation: exact
# replacements (each must match once), --no-incremental Release rebuild that must print "0 Error(s)", PrototypeMeasurement
# with CS342_ITER, then the model file is restored from a backup and its SHA-256 checked.
set -u
cd /c/Users/szy/Desktop/8005-workspace-v2/worktrees/hmi206-8005-agv-control-server || exit 1
F=tests/ControlServer.Tests/ReconnectModel.cs
OUT=${1:?output dir}
mkdir -p "$OUT"
E=tests/ControlServer.Tests/bin/Release/net8.0/win-x64/ControlServer.Tests.exe
BACKUP="$OUT/ReconnectModel.cs.bak"
cp -p "$F" "$BACKUP"
SHA=$(sha256sum "$F" | cut -d' ' -f1)

replace() { # from to
  FROM="$1" TO="$2" python -c "
import os,io,sys
p='$F'
s=io.open(p,encoding='utf-8',newline='').read()
n=s.count(os.environ['FROM'])
if n!=1: print('replacement matched',n,'times'); sys.exit(3)
io.open(p,'w',encoding='utf-8',newline='').write(s.replace(os.environ['FROM'],os.environ['TO']))
" || exit 3
}

explore() { # name iterations
  local name=$1 iter=$2 log="$OUT/$1.log"
  { echo "head $(git rev-parse HEAD) state=$name iterations=$iter"; git diff -U0 -- "$F" | grep -E '^[+-][^+-]'; } > "$log"
  local build
  build=$(timeout 1200 dotnet build tests/ControlServer.Tests -c Release --no-incremental -v q -nologo 2>&1 | grep -E 'Error\(s\)')
  echo "build: $build" >> "$log"
  case "$build" in *" 0 Error(s)"*) ;; *) echo "build failed" >> "$log"; exit 4;; esac
  CS342_ITER=$iter CS342_REPORT="$OUT/$name-report.txt" timeout 3000 $E -method ControlServer.Tests.ReconnectModelTests.PrototypeMeasurement -explicit only >> "$log" 2>&1
  cp -p "$BACKUP" "$F"; touch "$F"
  [ "$(sha256sum "$F" | cut -d' ' -f1)" = "$SHA" ] && echo "restored OK" >> "$log" || { echo "restore MISMATCH" >> "$log"; exit 5; }
}

explore E1-new-vehicle 1000

# R2a: the coordinator's reverse -- a change while not publishable goes back to "journal only, resend at the next handshake";
# the republish after the handshake stays.
replace '            _safety = kind;
            if (!_connected || _handshakeOpen)
            {
                return $"not published: session not publishable, state kept for the republish after the next handshake{mapped}";
            }

            long version' '            _safety = kind;
            long version'
replace '            _unacknowledged.Add((messageId, line));
' '            _unacknowledged.Add((messageId, line));
            if (!_connected || _handshakeOpen)
            {
                return $"v{version} journalled, not sent{mapped}";
            }
'
explore R2a-journal-only-keep-republish 300

# R2b: both halves back (journal only, no republish) -- the 9aad1a3c model.
replace '            _safety = kind;
            if (!_connected || _handshakeOpen)
            {
                return $"not published: session not publishable, state kept for the republish after the next handshake{mapped}";
            }

            long version' '            _safety = kind;
            long version'
replace '            _unacknowledged.Add((messageId, line));
' '            _unacknowledged.Add((messageId, line));
            if (!_connected || _handshakeOpen)
            {
                return $"v{version} journalled, not sent{mapped}";
            }
'
replace '            string republished = await RepublishAfterHandshakeAsync();' '            string republished = Environment.TickCount64 >= 0 ? "skipped (R2b)" : await RepublishAfterHandshakeAsync();'
explore R2b-journal-only-no-republish 300

# R3: the republish reuses the accepted revision instead of taking the next one.
replace '            return await SafetyChangeAsync(_safety, SafetyDelivery.Delivered) ?? "no answer";' '            _acceptedSafetyVersion--;
            return await SafetyChangeAsync(_safety, SafetyDelivery.Delivered) ?? "no answer";'
explore R3-republish-reuses-accepted 300

# R1: the handshake snapshot back to the old onboard rule, on the new model.
replace '_resentSafetyChangeThisHandshake ? checked(_acceptedSafetyVersion + 1) : _acceptedSafetyVersion;' '_acceptedSafetyVersion;'
explore R1-old-snapshot-rule-new-model 300

timeout 1200 dotnet build tests/ControlServer.Tests -c Release --no-incremental -v q -nologo 2>&1 | grep -E 'Error\(s\)' > "$OUT/final-rebuild.txt"
echo done > "$OUT/DONE"
