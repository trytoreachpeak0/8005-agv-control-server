#Requires -Version 7

<#
.SYNOPSIS
    Self-check for Select-StagedG3ContentConflictRun.ps1 (control-server#567): which pull requests run the staged
    content-conflict self-check in test.yml.

.DESCRIPTION
    Under a second, pure input. The content-conflict self-check takes about 200 s, so test.yml runs it only when a pull
    request changes what it checks. A selector that stopped matching would skip it for ever and stay green, so every
    path it must run on is checked, every near miss it must not run on, and the reason it prints either way.

    Exits 1 when any case comes out the other way, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\Test-StagedG3ContentConflictScope.ps1
#>
[CmdletBinding()]
param(
    [string]$Selector = (Join-Path $PSScriptRoot 'Select-StagedG3ContentConflictRun.ps1')
)

$ErrorActionPreference = 'Stop'
$wrong = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail) {
    if (-not $Ok) { $script:wrong++ }
    Write-Host ("{0}  {1}{2}" -f $(if ($Ok) { 'ok  ' } else { 'BAD ' }), $Name, $(if ($Ok) { '' } else { " -> $Detail" }))
}

$tokens = $null; $errors = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile($Selector, [ref]$tokens, [ref]$errors)
Check 'the selector parses' ($errors.Count -eq 0) "$($errors.Count) parse errors"

$runs = @(
    'scripts/run-staged-g3.ps1'
    'scripts/g3-slice-evidence.ps1'
    'scripts/l2/L2.psm1'
    'scripts/l2/scenarios/g3-automatic-charging-cycle.ps1'
    'scripts/Test-StagedG3ContentConflict.ps1'
    'scripts/Select-StagedG3ContentConflictRun.ps1'
    'src/ControlServer.Host/Transport/OnboardJourneyPublisher.cs'
    # What the judgments also depend on (review B3 of PR #574): the store they read, the problem codes, the candidate identity.
    'src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs'
    'src/ControlServer.Domain/ProtocolErrorCodes.cs'
    'src/ControlServer.Host/appsettings.json'
)
foreach ($path in $runs) {
    $decision = & $Selector -ChangedPath @('docs/README.md', $path)
    Check "runs when $path changed" ($decision.Run -eq $true -and $decision.Reason -like "*$path*") ($decision | ConvertTo-Json -Compress)
}

$skips = @(
    'docs/README.md'
    'scripts/run-staged-g3-restart.ps1'
    'scripts/l2-notes.md'
    'scripts/Test-G3EvidenceHonesty.ps1'
    'src/ControlServer.Host/Program.cs'
    'src/ControlServer.Host/TransportNotes.md'
    'src/ControlServer.Host/appsettings.Development.json'
    'src/ControlServer.Infrastructure/Persistence/WireToGateStoreNotes.md'
    'tests/ControlServer.Tests/scripts/l2/fixture.txt'
    'Scripts/run-staged-g3.ps1'
)
$decision = & $Selector -ChangedPath $skips
Check 'skips when only near misses changed, and says why' ($decision.Run -eq $false -and $decision.Reason -like '*skipped*' -and
    $decision.Reason -like '*10 changed*') ($decision | ConvertTo-Json -Compress)

$decision = & $Selector -ChangedPath @()
Check 'skips on a pull request that changes nothing' ($decision.Run -eq $false) ($decision | ConvertTo-Json -Compress)

$decision = & $Selector -NotAPullRequest
Check 'runs on an event that is not a pull request (a dispatched run on the integration branch is a backstop)' (
    $decision.Run -eq $true -and $decision.Reason -like '*not a pull request*') ($decision | ConvertTo-Json -Compress)

$output = Join-Path ([IO.Path]::GetTempPath()) "cs567-scope-$([guid]::NewGuid().ToString('N').Substring(0, 8)).txt"
try {
    $null = & $Selector -ChangedPath @('scripts/run-staged-g3.ps1') -GitHubOutput $output
    $null = & $Selector -ChangedPath @('docs/README.md') -GitHubOutput $output
    $lines = @(Get-Content -LiteralPath $output)
    Check 'writes run=true and run=false to GITHUB_OUTPUT' (($lines -join '|') -eq 'run=true|run=false') ($lines -join '|')
} finally {
    Remove-Item -LiteralPath $output -ErrorAction SilentlyContinue
}

if ($wrong -gt 0) {
    Write-Host "$wrong case(s) came out the other way."
    exit 1
}
Write-Host 'All cases as expected.'
exit 0
