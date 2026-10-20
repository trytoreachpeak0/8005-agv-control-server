#Requires -Version 7
# Runs the report step's parsing and Format-NightlyG3Comment call from g3.yml, offline, for the inputs main's trigger
# sends and for a hand dispatch that sends none (control-server#807).
param([string]$Root = (Resolve-Path "$PSScriptRoot/../../..").Path)
Import-Module (Join-Path $Root 'scripts/NightlyG3.psm1') -Force
$step = [regex]::Match((Get-Content -Raw (Join-Path $Root '.github/workflows/g3.yml')), '(?ms)# All optional:.*?-RoundStartedAtUtc \$roundStarted').Value
if (-not $step) { throw 'report step not found' }
$verdicts = @('staged', 'restart', 'demand-bearing', 'journey' | ForEach-Object { [pscustomobject]@{ runner = $_; result = 'PASS'; failed = @(); detail = '' } })
foreach ($case in @(
        @{ Name = 'from the trigger, queued behind the rig'; Cron = '37 14 * * *'; At = '2026-10-20T17:19:11Z'; Deadline = '2026-10-20T23:00:00.0000000+00:00'; Started = '2026-10-20T17:21:03.1234567+00:00'; Round = '2026-10-20T18:30:00.0000000+00:00' },
        @{ Name = 'by hand, no trigger inputs'; Cron = ''; At = ''; Deadline = '2026-10-20T09:00:00.0000000+00:00'; Started = '2026-10-20T06:00:00.0000000+00:00' },
        @{ Name = 'gate never ran'; Cron = '37 14 * * *'; At = 'not a time'; Deadline = ''; Started = '2026-10-20T14:40:00Z' })) {
    $env:G3_TRIGGER_CRON = $case.Cron; $env:G3_TRIGGERED_AT = $case.At; $env:G3_DEADLINE = $case.Deadline; $env:G3_ROUND_STARTED = "$($case.Round)"; $env:G3_NIGHT_START_HOUR_CST = '19'; $env:G3_JOB_RESULT = 'success'; $env:G3_REF = 'fp/v2-impl'
    $runUrl = 'https://example/run/1'; $kind = 'schedule'; $commits = [ordered]@{ controlServer = 'a' * 40 }; $stoppedBy = ''; $notes = @()
    $started = [DateTimeOffset]::Parse($case.Started)
    Invoke-Expression $step
    Write-Host "--- $($case.Name)"
    Write-Host (($body -split "`n")[0..2] -join "`n")
}
