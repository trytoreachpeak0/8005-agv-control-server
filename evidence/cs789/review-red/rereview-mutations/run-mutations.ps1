#Requires -Version 7
# cs#839 re-review: each check this round added, deleted one at a time; the door test classes must go red, then restored.
param([string]$Out = 'C:/work/8005-scratch/cs789/rr-mut2')
$repo = 'C:/work/8005-wt/cs789-8005-agv-control-server'
Set-Location $repo
[IO.Directory]::SetCurrentDirectory($repo)
New-Item -ItemType Directory -Force $Out | Out-Null
$readings = 'src/ControlServer.Host/Runtime/Faults/InTransitDoorReadings.cs'
$facts = 'src/ControlServer.Host/Runtime/Faults/InTransitDoorFacts.cs'
$cases = @(
    @{ Name = 'a-not-locked-between-needs-an-episode'; File = $readings;
       Old = '(episode is not null || doors.State != InTransitDoorState.ProvenLocked)'; New = '(episode is not null)' },
    @{ Name = 'a-run-broken-by-not-locked-ignored'; File = $readings;
       Old = '(doors.State == InTransitDoorState.SlotStateUnknown && doors.UnknownRunAfterNotLocked)'; New = '(doors.State == InTransitDoorState.SlotStateUnknown && false)' },
    @{ Name = 'a-run-broken-by-not-locked-not-recorded'; File = $facts;
       Old = 'unknownRunAfterNotLocked = !summary.SlotStateUnknown && !summary.AllTargetSlotsLocked &&'; New = 'unknownRunAfterNotLocked = false && !summary.AllTargetSlotsLocked &&' },
    @{ Name = 'moving-filter-removed'; File = $readings;
       Old = 'sample.State == InTransitDoorState.NotLocked && sample.Moving'; New = 'sample.State == InTransitDoorState.NotLocked' },
    @{ Name = 'm1-locked-between-does-not-split'; File = $readings;
       Old = "            if (sample.State == InTransitDoorState.ProvenLocked)`n            {`n                inRun = false;"; New = "            if (sample.State == InTransitDoorState.ProvenLocked)`n            {`n                inRun = !started && inRun;" },
    @{ Name = 'since-per-episode-again'; File = $readings;
       Old = 'return vehicles.TryGetValue(agvId, out Vehicle? vehicle) ? vehicle.ReadUpTo : null;'; New = 'return vehicles.TryGetValue(agvId, out Vehicle? vehicle) && vehicle.Episode is not null ? vehicle.ReadUpTo : null;' }
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
    git diff | Set-Content (Join-Path $Out "$($case.Name).diff")
    try {
        $log = Join-Path $Out "$($case.Name).txt"
        dotnet test tests/ControlServer.Tests -c Release --filter 'FullyQualifiedName~InTransitDoorReadingRestoredReleaseTests|FullyQualifiedName~InTransitDoorUnknownGraceTests' 2>&1 | Set-Content $log
        $exit = $LASTEXITCODE
        $line = (Select-String -Path $log -Pattern '(Passed|Failed)!\s+-' | Select-Object -Last 1).Line
        $errors = (Select-String -Path $log -Pattern ' error CS' | Select-Object -First 1).Line
        $failed = (Select-String -Path $log -Pattern '^\s+Failed ControlServer' | ForEach-Object { $_.Line.Trim() }) -join ' || '
        "CASE $($case.Name) exit=$exit $line $errors FAILED: $failed" | Add-Content $summary
    } finally {
        git checkout -- $case.File
    }
}
"DONE dirty=$(@(git status --porcelain).Count)" | Add-Content $summary
