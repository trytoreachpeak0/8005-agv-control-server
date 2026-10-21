#Requires -Version 7
# cs#839 second re-review: each check this round added or pinned, broken one at a time; the door test classes must go red,
# then restored. A case whose build fails is reported as INVALID, not as red.
param([string]$Out = 'C:/work/8005-scratch/cs789/rr2-mut')
$repo = 'C:/work/8005-wt/cs789-8005-agv-control-server'
Set-Location $repo
[IO.Directory]::SetCurrentDirectory($repo)
New-Item -ItemType Directory -Force $Out | Out-Null
$readings = 'src/ControlServer.Host/Runtime/Faults/InTransitDoorReadings.cs'
$facts = 'src/ControlServer.Host/Runtime/Faults/InTransitDoorFacts.cs'
$engine = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.InTransitDoors.cs'
$cases = @(
    @{ Name = 'H-reconnect-window-reads-no-between'; File = $facts;
       Old = 'if (since is DateTimeOffset from)'; New = 'if (since is DateTimeOffset from && from == DateTimeOffset.MinValue)' },
    @{ Name = 'M-engine-does-not-pass-the-cut'; File = $engine;
       Old = 'runtime.AgvId, doors, doorFaultInEffect, now, earlierReadingsUnread);'; New = 'runtime.AgvId, doors, doorFaultInEffect, now, false);' },
    @{ Name = 'M-cut-does-not-void-history'; File = $readings;
       Old = 'if (doorFaultInEffect && earlierReadingsUnread && vehicle.Episode is not null)'; New = 'if (doorFaultInEffect && earlierReadingsUnread && !earlierReadingsUnread && vehicle.Episode is not null)' },
    @{ Name = 'MA-parse-vehicleStopped-always-false'; File = $facts;
       Old = 'safety.TryGetProperty("vehicleStopped", out JsonElement stopped) && stopped.ValueKind == JsonValueKind.True);'; New = 'false);' },
    @{ Name = 'MD-drop-before-notLocked-check'; File = $readings;
       Old = '(ReadNotLockedWhileMovingBetween(doors) && (episode is not null || doors.State != InTransitDoorState.ProvenLocked))';
       New = '(ReadNotLockedWhileMovingBetween(doors) && ((episode is not null && !(episode is { Handed: false } && vehicle.LastObservedAt is DateTimeOffset l0 && now - l0 > UnobservedGraceTimeout)) || doors.State != InTransitDoorState.ProvenLocked))' }
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
