#Requires -Version 7
# control-server#510 review S1 evidence: the product's one SaveChanges in ConfirmUnableToChargeAsync split in two with 3 s
# between them (the pause first, the cycle and the rest after), then each of UTC and CWP run with the whole-line wait
# (scenario at 319343d1) and with the narrow wait in a read snapshot (HEAD). Product and scenarios restored from git.
param([string[]]$Only)
$ErrorActionPreference = 'Stop'
$repo = 'C:/Users/szy/Desktop/8005-workspace-v2/worktrees/cs510-8005-agv-control-server'
$here = $PSScriptRoot
$product = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.UnableToCharge.cs'
$utc = 'scripts/l2/scenarios/charging-unable-to-charge-pauses-charger.ps1'
$cwp = 'scripts/l2/scenarios/charging-clearance-to-waiting-point.ps1'
$anchor = "            RiotContractVersion = UnableToChargeFacts.VerifiedRiotContract,`r`n        });`r`n"
$split = $anchor +
    "        // control-server#510 review S1 evidence only: the pause saved on its own, the rest 3 s later.`r`n" +
    "        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);`r`n" +
    "        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);`r`n"
$runs = @(
    @{ Id = 's1-1-utc-wholeline'; Scenario = 'charging-unable-to-charge-pauses-charger'; File = $utc; Variant = 'utc-wholeline.ps1' }
    @{ Id = 's1-2-utc-narrow';    Scenario = 'charging-unable-to-charge-pauses-charger'; File = $utc; Variant = $null }
    @{ Id = 's1-3-cwp-wholeline'; Scenario = 'charging-clearance-to-waiting-point'; File = $cwp; Variant = 'cwp-wholeline.ps1' }
    @{ Id = 's1-4-cwp-narrow';    Scenario = 'charging-clearance-to-waiting-point'; File = $cwp; Variant = $null }
)
if ($Only) { $runs = @($runs | Where-Object { $_.Id -in $Only }) }
$summary = [System.Collections.Generic.List[string]]::new()
Push-Location $repo
try {
    if (git status --porcelain) { throw 'worktree not clean' }
    $head = git rev-parse HEAD
    $text = [IO.File]::ReadAllText((Join-Path $repo $product))
    if (-not $text.Contains("`r`n")) { $anchor = $anchor.Replace("`r`n", "`n"); $split = $split.Replace("`r`n", "`n") }
    $count = ([regex]::Matches($text, [regex]::Escape($anchor))).Count
    if ($count -ne 1) { throw "product anchor matched $count times" }
    [IO.File]::WriteAllText((Join-Path $repo $product), $text.Replace($anchor, $split))
    git diff -- $product | Set-Content -LiteralPath (Join-Path $here 'product-split.diff')
    $built = $false
    try {
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
            $line = "$($run.Id) exit=$code seconds=$([int]$clock.Elapsed.TotalSeconds) head=$head (product split, not committed)"
            $summary.Add($line)
            Write-Host $line
        }
    } finally {
        git checkout -q -- $product
    }
    if (git status --porcelain) { throw 'worktree not clean afterwards' }
} finally {
    Pop-Location
    $summary | Set-Content -LiteralPath (Join-Path $here 'summary.txt')
}
