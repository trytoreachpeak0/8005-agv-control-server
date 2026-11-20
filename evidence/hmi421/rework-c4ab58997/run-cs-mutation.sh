#!/usr/bin/env bash
# usage: run-cs-mutation.sh <name> <python-mutator> ; run from the control-server worktree
set -o pipefail
M=/c/work/8005-scratch/hmi421-mutations; name=$1; mut=$2
f=src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs
F="FullyQualifiedName~AResubmissionOfAnAuthorizedPressIsAnsweredAsItWasDespite|FullyQualifiedName~ASecondIdOverALoadWhoseCancellationAwaitsItsResultIsRejected|FullyQualifiedName~AResubmissionOfARefusedCancellationReplaysTheFirstRefusal|FullyQualifiedName~ARefusedCancellationIdAgainWithOtherContentIsABusinessIdContentConflict|FullyQualifiedName~AResubmissionOfACancellationRefusedBeforeArrivalReplaysTheRefusal|FullyQualifiedName~WhileARefusedSafetyMessageLeavesTheBaselineUntrusted|FullyQualifiedName~ACancellationFromAnotherVehicleIsRefused|FullyQualifiedName~AfterARejectionALaterPressWithProvenReadingsIsAuthorized"
python "$mut" "$f" || exit 9
[ "$(git diff --name-only | wc -l)" = 1 ] || { echo "not exactly one file changed"; git checkout -- "$f"; exit 8; }
git diff > $M/cs-$name.diff
{ dotnet build tests/ControlServer.Tests -c Release --no-incremental -v q && dotnet test tests/ControlServer.Tests -c Release --no-build --filter "$F" --logger "console;verbosity=normal"; } > $M/cs-$name.log 2>&1
echo "exit=$?" >> $M/cs-$name.log
git checkout -- "$f"
grep -E "总计|exit=" $M/cs-$name.log
grep -E "^\s+(失败|已通过) ControlServer" $M/cs-$name.log | sed 's/ControlServer.Tests.//'
