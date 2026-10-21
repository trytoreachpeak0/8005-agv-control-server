#!/usr/bin/env bash
# control-server#310 mutation self-check: each mutant changes exactly one file, is built and run, then that file is restored.
set -u -o pipefail
cd /c/work/8005-wt/cs310-8005-agv-control-server || exit 2
out=/c/work/8005-scratch/cs310/mutations
mkdir -p "$out"
peer=src/ControlServer.Host/Transport/OnboardPeer.cs
server=src/ControlServer.Host/Transport/OnboardTcpServer.cs
filter="FullyQualifiedName~OnboardPowerLossReconnectTests|FullyQualifiedName~OnboardTcpServerTests"

run() {
  local name=$1 file=$2 expr=$3
  sed -i "$expr" "$file"
  local changed
  changed=$(git diff --name-only | wc -l)
  if [ "$changed" -ne 1 ] || git diff --quiet -- "$file"; then
    echo "$name: mutation did not apply to exactly $file (changed=$changed)" | tee -a "$out/summary.txt"
    git checkout -- "$file"
    return
  fi
  git diff -- "$file" > "$out/$name.diff"
  if ! dotnet build tests/ControlServer.Tests -c Release -v q -nologo > "$out/$name.build.txt" 2>&1; then
    echo "$name: BUILD FAILED" | tee -a "$out/summary.txt"
  else
    dotnet test tests/ControlServer.Tests -c Release --no-build --filter "$filter" --logger "console;verbosity=normal" > "$out/$name.txt" 2>&1
    echo "$name: exit=$? $(grep -E '^\s+Failed ControlServer' "$out/$name.txt" | sed -E 's/.*Tests\.//; s/ \[.*//' | tr '\n' ' ')" | tee -a "$out/summary.txt"
  fi
  git checkout -- "$file"
}

: > "$out/summary.txt"
run M1-no-takeover-at-hello "$server" 's/_peer\.TakeOver(helloSession/false \&\& _peer.TakeOver(helloSession/'
run M2-takeover-ignores-generation "$peer" 's/                superseded\.SessionGeneration >= generation)/                false)/'
run M3-takeover-on-any-hello "$server" 's/^\(\s*\)_peer\.BeginHandshake(helloAgvId, connection);/\1_peer.BeginHandshake(helloAgvId, connection); _peer.TakeOver(helloAgvId, long.MaxValue, connection);/'
run M4-takeover-does-not-close "$peer" 's/^        superseded\.Connection\.Close();$//'
run M5-superseded-end-logged-as-1003 "$server" 's/catch (Exception error) when (label\.Connection?\.Superseded == true)/catch (Exception error) when (label.Connection is null)/'
run M6-attach-never-takes-over "$peer" 's/if (existing\.SessionGeneration >= generation)/if (existing.SessionGeneration >= long.MinValue)/'
dotnet build tests/ControlServer.Tests -c Release -v q -nologo > "$out/restore.build.txt" 2>&1
git status --short | tee -a "$out/summary.txt"
