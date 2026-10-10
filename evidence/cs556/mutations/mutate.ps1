#Requires -Version 7
# cs#556 mutations of the fix in OnboardRecoveryCoordinator.cs. Run from the worktree root on a clean, committed tree.
# Each mutation rebuilds (no --no-build) and runs the cs#556 tests; the file is restored with git checkout afterwards.
$ErrorActionPreference = 'Stop'
$file = 'src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs'
if (git status --porcelain -- $file) { throw "$file is dirty; commit before mutating." }
$filter = 'FullyQualifiedName~RecoveryStateMachineG2Tests.AForcedResult|FullyQualifiedName~RecoveryStateMachineG2Tests.AFailedForcedResult|FullyQualifiedName~RecoveryStateMachineG2Tests.ALoadCancellationResultAtTheCurrentGeneration'
$mutations = [ordered]@{
    # A message type no result has, rather than a constant false: the compiler would flag the call as unreachable.
    'M0 no update at all' = @('messageType == "ForcedMechanicalRecoveryResult" && outcome', 'messageType == "ForcedMechanicalRecoveryResult-M0" && outcome')
    'M1 raise-only dropped (lowers too)' = @('connection.ReportedForcedRecoveryGeneration >= generation)', 'connection.ReportedForcedRecoveryGeneration == generation)')
    'M2 historical result also counts' = @('            workflow.State = RecoveryWorkflowState.HistoricalOnly;', "            workflow.State = RecoveryWorkflowState.HistoricalOnly;`n            if (messageType == `"ForcedMechanicalRecoveryResult`")`n                await TakeForcedResultAsReportedGenerationAsync(agvId, sessionGeneration, resultGeneration, cancellationToken).ConfigureAwait(false);")
    'M3 administrator-closed counts' = @('&& awaitedResult &&', '&& (awaitedResult || true) &&')
    'M5 FAILED counts too (outcome dropped)' = @('&& outcome == "MECHANICALLY_ISOLATED" &&', '&&')
    # R3 alone is now equivalent: only a forced result has the outcome MECHANICALLY_ISOLATED. R3b drops both checks.
    'R3 any result type (type check dropped)' = @('if (messageType == "ForcedMechanicalRecoveryResult" && outcome', 'if (outcome')
    'R3b any result type, any outcome' = @('if (messageType == "ForcedMechanicalRecoveryResult" && outcome == "MECHANICALLY_ISOLATED" && awaitedResult', 'if (awaitedResult')
    'R4 any generation (both equalities dropped)' = @('resultGeneration == currentGeneration && resultGeneration == workflow.ForcedRecoveryGeneration)', 'true)')
    'M4 any connection counts' = @('connection is null || connection.SessionGeneration != sessionGeneration ||', 'connection is null ||')
}
foreach ($name in $mutations.Keys) {
    $from, $to = $mutations[$name]
    $text = [IO.File]::ReadAllText($file)
    if (([regex]::Matches($text, [regex]::Escape($from))).Count -ne 1) { throw "$name : anchor not unique or missing" }
    [IO.File]::WriteAllText($file, $text.Replace($from, $to))
    "=== $name"
    $out = dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --filter $filter 2>&1
    $code = $LASTEXITCODE
    $out | Where-Object { $_ -cmatch 'error CS|^\s+Failed |Passed!|Failed!' } | ForEach-Object { $_.Trim() }
    "exit $code -> $(if ($out -cmatch 'error CS') { 'BUILD FAILED (not a verdict)' } elseif ($code -ne 0) { 'KILLED' } else { 'SURVIVED' })"
    git checkout -- $file
}
