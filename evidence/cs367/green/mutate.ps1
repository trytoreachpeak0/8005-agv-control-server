#Requires -Version 7
# Runs each mutation of the cs#367 fix: apply (exactly one match), rebuild without incremental, run the related tests,
# record which tests fail, restore the file from HEAD. Output: one block per mutation in results.txt.
param(
    [Parameter(Mandatory)] [string] $Worktree,
    [Parameter(Mandatory)] [string] $OutFile,
    [string[]] $Only = @()
)
$ErrorActionPreference = 'Stop'
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
Set-Location $Worktree
$engine = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs'
$recovery = 'src/ControlServer.Host/Runtime/Faults/VehicleFaultRecoveryService.cs'
$rebuild = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.OwnOrderRebuild.cs'
$dirty = git status --porcelain -- src tests
if ($dirty) { throw "working tree not clean, refusing to mutate:`n$($dirty -join "`n")" }
$filter = 'FullyQualifiedName~FailedOrderBeforeConfirmationTests|FullyQualifiedName~VehicleFaultRecoveryTests|FullyQualifiedName~FailedOrderBehindSessionGateTests|FullyQualifiedName~OwnOrderRebuild|FullyQualifiedName~InTransitOrderStall|FullyQualifiedName~EmergencyReleaseVersusOwnOrderRebuildTests|FullyQualifiedName~StoppedRebuildExitTests|FullyQualifiedName~PickupDispatchPlanPastOwnOrderTests|FullyQualifiedName~Batch7DemandRelease'

$mutations = [ordered]@{
    'M0-revert-src' = @{ Revert = $true }
    'M1-front-no-observe' = @{ File = $engine
        From = "result.Outcome == MovementDispatchOutcome.TerminalReconciliationRequired &&`n            await NameOrderEndedBeforeConfirmationAsync"
        To = "false && result.Outcome == MovementDispatchOutcome.TerminalReconciliationRequired &&`n            await NameOrderEndedBeforeConfirmationAsync" }
    'M2-behind-disabled' = @{ File = $engine
        From = "        if (intent.CreateAttemptCount == 0)`n        {`n            return false;"
        To = "        if (intent.CreateAttemptCount >= 0)`n        {`n            return false;" }
    'M3-clear-no-hold-recognition' = @{ File = $recovery
        From = "long generation = standing.FaultGeneration;`n        return await"
        To = "long generation = standing.FaultGeneration;`n        return false && await" }
    'M4-clear-ignores-generation' = @{ File = $recovery
        From = " &&`n                       row.FaultGeneration == generation,"
        To = "," }
    'M5-behind-creates' = @{ File = $engine
        From = "        if (intent.CreateAttemptCount == 0)`n        {`n            return false;"
        To = "        if (intent.CreateAttemptCount < 0)`n        {`n            return false;" }
    'M7-observe-once' = @{ File = $engine
        From = "            return false;`n        }`n`n        RiotOrderObservation order;`n        try`n        {`n            order = await vehicleFacts.ReconcileByUpperIdAsync(upperId, cancellationToken)"
        To = "            return false;`n        }`n        if (string.Equals(runtime.BlockReasonCode, VehicleFaultEvidence.OrderFailed, StringComparison.Ordinal)) { return true; }`n`n        RiotOrderObservation order;`n        try`n        {`n            order = await vehicleFacts.ReconcileByUpperIdAsync(upperId, cancellationToken)" }
    'M9-failed-only' = @{ File = $engine
        From = "        return await NameStalledOrderAsync(runtime, intent, order, cancellationToken, reasonOnceMovedOn).ConfigureAwait(false);"
        To = "        return await ObserveOrderFailureAsync(runtime, intent, order, cancellationToken).ConfigureAwait(false);" }
    'M10-no-rebuild-delay' = @{ File = $rebuild
        From = "            if (now < rebuild.DueAt)"
        To = "            if (now < rebuild.RecordedAt)" }
    'M8-behind-confirmed-not-named' = @{ File = $engine
        From = "            if (result.Outcome == MovementDispatchOutcome.Confirmed)`n            {`n                // Read afresh"
        To = "            if (result.Outcome == MovementDispatchOutcome.CreateDispatchDisabled)`n            {`n                // Read afresh" }
}

"# cs#367 mutations at $(git rev-parse HEAD) $(Get-Date -Format o)" | Set-Content $OutFile
"# only: $($Only -join ' ') ($($Only.Count))" | Add-Content $OutFile
foreach ($o in $Only) { if (-not $mutations.Contains($o)) { throw "unknown mutation $o" } }
foreach ($name in $mutations.Keys) {
    if ($Only.Count -gt 0 -and $name -notin $Only) { continue }
    $m = $mutations[$name]
    if ($m.Revert) {
        git checkout 71e62d85 -- $engine $recovery $rebuild
        $changed = (git diff --stat HEAD -- src | Select-Object -Last 1)
        "== $name : src reverted to 71e62d85 ($changed)" | Add-Content $OutFile
    }
    else {
        $text = [IO.File]::ReadAllText((Join-Path $Worktree $m.File))
        $count = ([regex]::Matches($text, [regex]::Escape($m.From))).Count
        if ($count -ne 1) {
            "== $name : ANCHOR MATCHED $count TIMES, NOT RUN" | Add-Content $OutFile
            continue
        }
        [IO.File]::WriteAllText((Join-Path $Worktree $m.File), $text.Replace($m.From, $m.To))
        $lines = (git diff --numstat -- $m.File)
        "== $name : applied ($lines)" | Add-Content $OutFile
    }
    try {
        $build = dotnet build tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-incremental 2>&1
        $errors = ($build | Select-String -Pattern '^\s+(\d+) Error\(s\)').Matches.Groups[1].Value
        "   build errors: $errors" | Add-Content $OutFile
        if ($errors -ne '0') { continue }
        $test = dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-build --filter $filter 2>&1
        "   exit: $LASTEXITCODE" | Add-Content $OutFile
        $test | Select-String -Pattern '^\s+Failed ControlServer\.Tests\.(.+?) \[\d' | ForEach-Object { "   RED $($_.Matches.Groups[1].Value)" } | Add-Content $OutFile
        $test | Select-String -Pattern '(Failed|Passed)!\s+-' | ForEach-Object { "   $($_.Line.Trim())" } | Add-Content $OutFile
    }
    finally {
        git checkout HEAD -- $engine $recovery $rebuild
    }
}
# Leave the build matching HEAD.
dotnet build tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-incremental 2>&1 | Select-String 'Error\(s\)' | ForEach-Object { "# final build: $($_.Line.Trim())" } | Add-Content $OutFile
"# done" | Add-Content $OutFile
