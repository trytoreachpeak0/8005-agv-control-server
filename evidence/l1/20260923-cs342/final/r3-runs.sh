#!/usr/bin/env bash
# control-server#342 round 3: discrimination on the changed model (test files of 9966beca, first committed as a083b84a
# and amended for a doc-comment cref only), plus product mutants in cs342-pr343 (a detached worktree at cb44fb60).
set -u
S="${S:?set S to a scratch directory for the reports}"
W="${W:?set W to the directory holding the cs342 worktrees}"
SRC=$W/cs342-8005-agv-control-server
M=$W/cs342-pr343
EXE=tests/ControlServer.Tests/bin/Release/net8.0/win-x64/ControlServer.Tests.exe
FILES="Directory.Packages.props tests/ControlServer.Tests/ControlServer.Tests.csproj tests/ControlServer.Tests/packages.lock.json tests/ControlServer.Tests/ReconnectModel.cs tests/ControlServer.Tests/ReconnectModelTests.cs tests/ControlServer.Tests/ReconnectModelRegressionTests.cs"
PINNED="synthetic [CutAfter(0), Round(7s), BeginHandshake, CompleteHandshake, Arrive, CutAfter(0), Round(8s)]"

copy_tests() { for f in $FILES; do cp $SRC/$f $1/$f; done; }

mutate() {
  python - "$1" "$2" "$3" <<'PYEOF'
import sys
p, oldf, newf = sys.argv[1:4]
old = open(oldf, encoding='utf-8', newline='').read()
new = open(newf, encoding='utf-8', newline='').read()
s = open(p, encoding='utf-8', newline='').read()
n = s.count(old)
print('mutation matches', n, 'in', p)
if n != 1:
    raise SystemExit(1)
open(p, 'w', encoding='utf-8', newline='').write(s.replace(old, new))
PYEOF
}

build() { dotnet build tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-incremental 2>&1 | grep -E "Error\(s\)"; }

measure() {
  local name=$1
  CS342_ITER=300 CS342_SHRINK_ITER=300 CS342_REPORT="$(cygpath -w $S)\\r3-measure-$name.txt" $EXE -method ControlServer.Tests.ReconnectModelTests.PrototypeMeasurement -explicit only 2>&1 | tail -1
  grep -E "^[A-Za-z]+: [0-9]+ of |^entry request|^rounds from|^  by code|^DifferentMessageAccepted by where|^rounds that failed|^per combination excluding" "$S/r3-measure-$name.txt"
}

regression() {
  $EXE -class ControlServer.Tests.ReconnectModelRegressionTests > "$S/r3-regression-$1.log" 2>&1
  grep -E "\[FAIL\]|Total:" "$S/r3-regression-$1.log" | sed 's/ControlServer.Tests.ReconnectModelRegressionTests.//'
}

replay_pinned() {
  CS342_SEQUENCES="$PINNED" CS342_REPORT="$(cygpath -w $S)\\r3-pinned-$1.txt" $EXE -method ControlServer.Tests.ReconnectModelTests.ReplaySequences -explicit only 2>&1 | tail -1
  grep -E "^== |^violation|Round\(8s\)" "$S/r3-pinned-$1.txt"
}

for w in cs342-af01fd27 cs342-62d5c560 cs342-18172346; do
  cd $W/$w || exit 1
  c=$(git rev-parse --short HEAD)
  echo "== $w $c"
  git status --short src | head -3
  copy_tests $W/$w
  build
  measure $c
  regression $c
done

cd $SRC || exit 1
c=$(git rev-parse --short HEAD)
echo "== branch $c"
git status --short | head -3
build
measure $c
regression $c

ENGINE=src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs
PROC=src/ControlServer.Host/Transport/OnboardMessageProcessor.cs
MODEL=tests/ControlServer.Tests/ReconnectModel.cs
printf '                CarriesACodeThatNamesAWaitOnAPerson(current) ||\n' > "$S/mut-guard-old.txt"
printf '' > "$S/mut-guard-new.txt"
printf '        if (await NameSilentOnboardSessionAsync(runtime, session, stops, now, cancellationToken)\n                .ConfigureAwait(false))\n        {\n            return;\n        }\n' > "$S/mut-silent-old.txt"
printf '        _ = await NameSilentOnboardSessionAsync(runtime, session, stops, now, cancellationToken)\n                .ConfigureAwait(false);\n' > "$S/mut-silent-new.txt"
printf " &&\n                heard.At <= now && now - heard.At <= SessionLiveness.Timeout;" > "$S/mut-window-old.txt"
printf ";" > "$S/mut-window-new.txt"
printf '        root["sessionGeneration"] = 0;\n' > "$S/mut-rebind-old.txt"
printf '        root["sessionGeneration"] = 0;\n        root["payload"] = null;\n' > "$S/mut-rebind-new.txt"

cd $M || exit 1
echo "== mutants in cs342-pr343 $(git rev-parse --short HEAD)"
git status --short src | head -3
copy_tests $M

echo "-- M-guard: CarriesACodeThatNamesAWaitOnAPerson removed from NameFailedAdvanceAsync"
mutate $ENGINE "$S/mut-guard-old.txt" "$S/mut-guard-new.txt" || exit 1
git diff --stat -- src
build
measure mutant-guard
regression mutant-guard
git checkout -- $ENGINE && echo "restored: $(git diff --stat -- src | wc -l) changed lines in src"

echo "-- M-silent: the silence check no longer ends the round (publish after judging, not instead of)"
mutate $ENGINE "$S/mut-silent-old.txt" "$S/mut-silent-new.txt" || exit 1
git diff --stat -- src
build
regression mutant-silent
replay_pinned mutant-silent

echo "-- M-silent + M-guard, new model (window)"
mutate $ENGINE "$S/mut-guard-old.txt" "$S/mut-guard-new.txt" || exit 1
git diff --stat -- src
build
replay_pinned mutant-silent-guard-new-model
measure mutant-silent-guard-new-model

echo "-- M-silent + M-guard, old model (no window)"
mutate $MODEL "$S/mut-window-old.txt" "$S/mut-window-new.txt" || exit 1
build
replay_pinned mutant-silent-guard-old-model
measure mutant-silent-guard-old-model
copy_tests $M
git checkout -- $ENGINE && echo "restored: $(git diff --stat -- src | wc -l) changed lines in src"

echo "-- M-rebind: handshake replay equivalence hash ignores the payload"
mutate $PROC "$S/mut-rebind-old.txt" "$S/mut-rebind-new.txt" || exit 1
git diff --stat -- src
build
measure mutant-rebind
$EXE -method ControlServer.Tests.ReconnectModelRegressionTests.ASafetyChangeResentWithDifferentContentUnderItsMessageIdIsRefused > "$S/r3-regression-mutant-rebind.log" 2>&1; grep -E "\[FAIL\]|Total:" "$S/r3-regression-mutant-rebind.log"
git checkout -- $PROC && echo "restored: $(git diff --stat -- src | wc -l) changed lines in src"

echo "-- M-guard-replay (same as round 2): CaptureFirstResponseAsync equivalence check removed"
printf '            if (replayEquivalenceHash is null ||\n                replayEquivalenceHash(existing.RequestJson) != replayEquivalenceHash(requestJson))\n            {\n' > "$S/mut-capture-old.txt"
printf '            if (replayEquivalenceHash is null)\n            {\n' > "$S/mut-capture-new.txt"
STORE=src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs
mutate $STORE "$S/mut-capture-old.txt" "$S/mut-capture-new.txt" || exit 1
git diff --stat -- src
build
measure mutant-replay-guard
$EXE -method ControlServer.Tests.ReconnectModelRegressionTests.ASafetyChangeResentWithDifferentContentUnderItsMessageIdIsRefused > "$S/r3-regression-mutant-replay-guard.log" 2>&1; grep -E "\[FAIL\]|Total:" "$S/r3-regression-mutant-replay-guard.log"
git checkout -- $STORE && echo "restored: $(git diff --stat -- src | wc -l) changed lines in src"
git status --short src
echo "== done"
