#Requires -Version 7
# cs#653 local real-rig round: two scenarios, each with -ScreenState plus the watcher and once without.
$ErrorActionPreference = 'Continue'
$repo = 'F:/8005-wt/cs653-8005-agv-control-server'
$onboard = 'F:/8005-wt/rig-cs653-8005-agv-onboard-hmi'
$simulator = 'F:/8005-wt/rig-cs653-slots-simulator'
$reader = 'F:/8005-scratch/cs653-reader-8005-workspace/remote-ops/onboard-hmi/scripts/18-read-hmi-screen-state.ps1'
$root = 'F:/8005-scratch/cs653-rig'
$runs = @(
    @{ N = '01'; Scenario = 'real-onboard-compensate-then-reconnect'; Screen = $true }
    @{ N = '02'; Scenario = 'real-onboard-compensate-then-reconnect'; Screen = $false }
    @{ N = '03'; Scenario = 'real-onboard-restart-with-open-recovery-session'; Screen = $true }
    @{ N = '04'; Scenario = 'real-onboard-restart-with-open-recovery-session'; Screen = $false }
)
$results = foreach ($run in $runs) {
    $name = "$($run.N)-$($run.Scenario)$(if ($run.Screen) { '-screen-state' } else { '' })"
    $evidence = Join-Path $root $name
    $started = Get-Date
    $watcher = $null
    $arguments = @('-NoProfile', '-File', "$repo/scripts/l2/Invoke-L2Scenario.ps1", '-Scenario', $run.Scenario,
        '-EvidenceRoot', $evidence, '-Repository', $repo, '-OnboardRepository', $onboard, '-SimulatorRepository', $simulator,
        '-BatchId', 'cs653')
    if ($run.N -ne '01') { $arguments += '-SkipBuild' }
    if ($run.Screen) {
        $arguments += @('-ScreenState', '-ScreenStatePauseSeconds', '300')
        $watcher = Start-Process pwsh -PassThru -NoNewWindow -RedirectStandardOutput "$evidence.watcher.log" -RedirectStandardError "$evidence.watcher.err.log" `
            -ArgumentList @('-NoProfile', '-File', "$repo/scripts/l2/Watch-L2ScreenStatePauses.ps1", '-EvidenceRoot', $evidence,
                '-ReaderScript', $reader, '-Repetitions', '10', '-TimeoutMinutes', '60')
    }
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $name starting"
    & pwsh @arguments *> "$evidence.log"
    $exit = $LASTEXITCODE
    if ($watcher) { $null = $watcher.WaitForExit(120000); if (-not $watcher.HasExited) { $watcher.Kill() } }
    $outcome = try { (Get-Content -LiteralPath (Join-Path $evidence 'assertions.json') -Raw | ConvertFrom-Json).outcome } catch { '(no assertions.json)' }
    $line = [ordered]@{ run = $name; exit = $exit; outcome = $outcome; minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1) }
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $name -> $outcome (exit $exit, $($line.minutes) min)"
    [pscustomobject]$line
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'results.json') -Encoding utf8NoBOM
$results | Format-Table -AutoSize | Out-String | Write-Host
