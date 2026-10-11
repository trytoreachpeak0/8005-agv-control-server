#Requires -Version 7
# cs#543 red/green evidence: A-C demand-bearing (site 1), D-F recovery via the headless self-check (site 2).
param([string[]]$Only)
$ErrorActionPreference = 'Continue'
$wt = 'C:/Users/szy/Desktop/8005-workspace-v2/worktrees/cs543-8005-agv-control-server'
$f = 'F:/8005-scratch/cs543-runners'
$ev = 'F:/8005-scratch/cs543-evidence'
$new = "$wt/scripts/run-staged-g3.ps1"
$mut = '1a4452d1bcc9694d400cfe54ea8af5f3ef030ca9'
$clean = '88f60e448'
$clean = (& git -C $wt rev-parse $clean).Trim()
$heavy = 'C:/Users/szy/Desktop/8005-workspace-v2/Invoke-HeavyLocal.ps1'
$runs = @(
  @{ Id='A'; Name='demand-bearing-A-old-judgment-silent-mutation'; Kind='red'; Runner="$f/old/run-staged-g3.ps1"; Commit=$mut },
  @{ Id='B'; Name='demand-bearing-B-new-judgment-silent-mutation'; Kind='red'; Runner=$new; Commit=$mut },
  @{ Id='C'; Name='demand-bearing-C-new-judgment-unmutated'; Kind='green'; Runner=$new; Commit=$clean },
  @{ Id='D'; Name='recovery-D-old-judgment-drop-rule-removed'; Kind='red'; Runner="$f/site2-old-mut/run-staged-g3.ps1"; Commit=$clean },
  @{ Id='E'; Name='recovery-E-new-judgment-drop-rule-removed'; Kind='red'; Runner="$f/site2-new-mut/run-staged-g3.ps1"; Commit=$clean },
  @{ Id='F'; Name='recovery-F-new-judgment-unmutated'; Kind='green'; Runner=$new; Commit=$clean }
)
foreach ($r in $runs) {
  if ($Only -and $r.Id -notin $Only) { continue }
  $freeGb = [math]::Round((Get-PSDrive C).Free / 1GB, 1)
  Write-Host "=== C: free $freeGb GB before $($r.Id)"
  if ($freeGb -lt 15) { Write-Host "=== STOP: C: below 15 GB, not starting $($r.Id)"; break }
  $root = "$ev/$($r.Kind)/$($r.Name)"
  $stage = "F:/8005-scratch/cs543-stage-$($r.Id)"
  Write-Host "=== $($r.Name) start $(Get-Date -Format o) runner=$($r.Runner) server=$($r.Commit)"
  do {
    if ($r.Id -in 'A','B','C') {
      & pwsh -NoProfile -File $heavy -Ticket 'cs#543' pwsh -NoProfile -File "$wt/scripts/run-demand-bearing-g3-vectors.ps1" -StageRoot $stage -EvidenceRoot $root -SelfCheckControlServerCommit $r.Commit -SharedRunnerSource $r.Runner
    } else {
      & pwsh -NoProfile -File $heavy -Ticket 'cs#543' pwsh -NoProfile -File "$wt/scripts/Test-StagedG3ContentConflict.ps1" -ControlServerCommit $r.Commit -StageRoot $stage -EvidenceRoot $root -Expect Pass -RunnerPath $r.Runner -ControlServerRepository $wt
    }
    $code = $LASTEXITCODE
    if ($code -eq 3) { Write-Host "heavy-local gate busy (exit 3), retry in 5 min"; Start-Sleep -Seconds 300 }
  } while ($code -eq 3)
  Write-Host "=== $($r.Name) exit $code $(Get-Date -Format o)"
  if ($r.Id -in 'A','B','C') {
    $p = Join-Path $root 'probe-result.json'
    if (Test-Path $p) { (Get-Content -Raw $p | ConvertFrom-Json).cases | Where-Object case -eq 'resultFromASupersededSessionGenerationIsRefused' | ConvertTo-Json -Depth 10 -Compress }
    else { Write-Host 'no probe-result.json' }
  } else {
    $t = Join-Path $root 'recovery-probe-events.ndjson'
    if (Test-Path $t) { Get-Content $t | Where-Object { $_ -match 'forced-mechanical-command-lost-to-disconnect' } } else { Write-Host 'no recovery transcript' }
    $p = Join-Path $root 'business-fault-proxy-events.ndjson'
    if (Test-Path $p) { Get-Content $p | Where-Object { $_ -match 'ForcedMechanicalRecoveryCommand' } }
  }
  Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}
