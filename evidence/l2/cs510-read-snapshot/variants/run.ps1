#Requires -Version 7
# control-server#510 evidence runs: each variant copied over the scenario, run once on slot 0, restored from git.
param([string[]]$Only)
$ErrorActionPreference = 'Stop'
$repo = 'C:/Users/szy/Desktop/8005-workspace-v2/worktrees/cs510-8005-agv-control-server'
$here = $PSScriptRoot
$utc = 'scripts/l2/scenarios/charging-unable-to-charge-pauses-charger.ps1'
$cwp = 'scripts/l2/scenarios/charging-clearance-to-waiting-point.ps1'
$runs = @(
    @{ Id = '1-utc-new';     Scenario = 'charging-unable-to-charge-pauses-charger'; File = $utc; Variant = $null }
    @{ Id = '2-utc-old-inj'; Scenario = 'charging-unable-to-charge-pauses-charger'; File = $utc; Variant = 'utc-old-inj.ps1' }
    @{ Id = '3-utc-b-inj';   Scenario = 'charging-unable-to-charge-pauses-charger'; File = $utc; Variant = 'utc-b-inj.ps1' }
    @{ Id = '4-utc-new-inj'; Scenario = 'charging-unable-to-charge-pauses-charger'; File = $utc; Variant = 'utc-new-inj.ps1' }
    @{ Id = '5-cwp-new';     Scenario = 'charging-clearance-to-waiting-point'; File = $cwp; Variant = $null }
    @{ Id = '6-cwp-old-inj'; Scenario = 'charging-clearance-to-waiting-point'; File = $cwp; Variant = 'cwp-old-inj.ps1' }
    @{ Id = '7-cwp-b-inj';   Scenario = 'charging-clearance-to-waiting-point'; File = $cwp; Variant = 'cwp-b-inj.ps1' }
    @{ Id = '8-cwp-new-inj'; Scenario = 'charging-clearance-to-waiting-point'; File = $cwp; Variant = 'cwp-new-inj.ps1' }
    @{ Id = '9-cfc-new';     Scenario = 'charging-full-cycle'; File = $null; Variant = $null }
)
if ($Only) { $runs = @($runs | Where-Object { $_.Id -in $Only }) }
$built = $false
$summary = [System.Collections.Generic.List[string]]::new()
Push-Location $repo
try {
    if (git status --porcelain) { throw 'worktree not clean' }
    $head = git rev-parse HEAD
    foreach ($run in $runs) {
        $evidence = Join-Path $here "ev/$($run.Id)"
        if ($run.Variant) { Copy-Item -LiteralPath (Join-Path $here $run.Variant) -Destination $run.File -Force }
        $clock = [Diagnostics.Stopwatch]::StartNew()
        try {
            $arguments = @('-NoProfile', '-File', 'scripts/l2/Invoke-L2Scenario.ps1', '-Scenario', $run.Scenario, '-EvidenceRoot', $evidence)
            if ($built) { $arguments += '-SkipBuild' }
            & pwsh @arguments *> (Join-Path $here "$($run.Id).console.log")
            $code = $LASTEXITCODE
            $built = $true
        } finally {
            if ($run.Variant) { git checkout -q -- $run.File }
        }
        $fired = (Select-String -Path (Join-Path $here "$($run.Id).console.log") -Pattern 'CS510_INJECTION_(FIRED|TIMED_OUT)' -AllMatches |
            ForEach-Object { $_.Matches.Value }) -join ','
        $line = "$($run.Id) exit=$code seconds=$([int]$clock.Elapsed.TotalSeconds) injection=$fired head=$head"
        $summary.Add($line)
        Write-Host $line
        if (git status --porcelain) { throw "worktree not clean after $($run.Id)" }
    }
} finally {
    Pop-Location
    $summary | Set-Content -LiteralPath (Join-Path $here 'summary.txt')
}
