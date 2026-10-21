#Requires -Version 7
# cs#839 third re-review: the release's last check and its parts, broken one at a time; the door test classes must go red,
# then restored. A case whose build fails is reported as INVALID, not as red.
param([string]$Out = 'C:/work/8005-scratch/cs789/rr3-mut')
$repo = 'C:/work/8005-wt/cs789-8005-agv-control-server'
Set-Location $repo
[IO.Directory]::SetCurrentDirectory($repo)
New-Item -ItemType Directory -Force $Out | Out-Null
$readings = 'src/ControlServer.Host/Runtime/Faults/InTransitDoorReadings.cs'
$facts = 'src/ControlServer.Host/Runtime/Faults/InTransitDoorFacts.cs'
$engine = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.InTransitDoors.cs'
$coordinator = 'src/ControlServer.Host/Runtime/Faults/VehicleFaultCoordinator.cs'
$manual = 'src/ControlServer.Host/Runtime/LoadedEmergencyStopRelease.cs'
$cases = @(
    @{ Name = 'gate-removed'; File = $coordinator;
       Old = "            context.NoDoorReadNotLockedInTransit is not { } noDoorReadNotLocked ||`n            !await noDoorReadNotLocked(fault.EnteredAt, cancellationToken).ConfigureAwait(false))";
       New = '            context.NoDoorReadNotLockedInTransit is null)' },
    @{ Name = 'gate-sees-no-not-locked'; File = $facts;
       Old = 'if (StateOf(summary) == InTransitDoorState.NotLocked)'; New = 'if (StateOf(summary) == InTransitDoorState.Stale)' },
    @{ Name = 'gate-accepts-an-incomplete-window'; File = $facts;
       Old = "        return false;`n    }`n`n    private static InTransitDoorState StateOf"; New = "        return true;`n    }`n`n    private static InTransitDoorState StateOf" },
    @{ Name = 'gate-window-starts-at-the-fault'; File = $engine;
       Old = '            : orderCreatedAt;'; New = '            : faultEnteredAt ?? orderCreatedAt;' },
    @{ Name = 'manual-entry-skips-the-gate'; File = $manual;
       Old = 'return await InTransitDoorFacts.NoDoorReadNotLockedSinceAsync('; New = 'return true || await InTransitDoorFacts.NoDoorReadNotLockedSinceAsync(' }
)
$summary = Join-Path $Out 'summary.txt'
Set-Content $summary ''
foreach ($case in $cases) {
    if (@(git status --porcelain).Count -ne 0) { "ABORT dirty before $($case.Name)" | Add-Content $summary; exit 2 }
    $path = Join-Path $repo $case.File
    $text = [IO.File]::ReadAllText($path)
    $hits = ([regex]::Matches($text, [regex]::Escape($case.Old))).Count
    if ($hits -ne 1) { "CASE $($case.Name) SKIPPED hits=$hits" | Add-Content $summary; continue }
    [IO.File]::WriteAllText($path, $text.Replace($case.Old, $case.New))
    try {
        $changed = @(git status --porcelain)
        if ($changed.Count -ne 1) { "CASE $($case.Name) ABORT changed=$($changed.Count)" | Add-Content $summary; continue }
        git diff | Set-Content (Join-Path $Out "$($case.Name).diff")
        $log = Join-Path $Out "$($case.Name).txt"
        dotnet test tests/ControlServer.Tests -c Release --filter 'FullyQualifiedName~InTransitDoorReadingRestoredReleaseTests|FullyQualifiedName~InTransitDoorUnknownGraceTests' 2>&1 | Set-Content $log
        $exit = $LASTEXITCODE
        $errors = @(Select-String -Path $log -Pattern ' error CS|Build FAILED' | Select-Object -First 1)
        $line = (Select-String -Path $log -Pattern '(Passed|Failed)!\s+-' | Select-Object -Last 1).Line
        if ($errors.Count -gt 0 -or -not $line) {
            "CASE $($case.Name) INVALID exit=$exit build: $($errors[0].Line)" | Add-Content $summary
            continue
        }
        $failed = (Select-String -Path $log -Pattern '^\s+Failed ControlServer' | ForEach-Object { $_.Line.Trim() }) -join ' || '
        "CASE $($case.Name) exit=$exit $line FAILED: $failed" | Add-Content $summary
    } finally {
        git checkout -- $case.File
    }
}
"DONE dirty=$(@(git status --porcelain).Count)" | Add-Content $summary
