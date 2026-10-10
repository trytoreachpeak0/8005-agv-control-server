#Requires -Version 7

<#
.SYNOPSIS
    Decides whether test.yml runs the staged content-conflict self-check (Test-StagedG3ContentConflict.ps1) for this
    event (control-server#567).

.DESCRIPTION
    That self-check takes about 200 s (clone, build, publish, start a ControlServer, four probes), and the coordinator
    ruled on 2026-10-10 that not every pull request pays for it: it runs when a pull request changes what it checks --
    the staged runner it takes its harness and judgments from, the shared evidence script, the L2 helpers, itself and
    this selector, the server's transport, store and problem codes, or its appsettings.json -- and is skipped, with the
    reason printed, otherwise. An event that is not a pull request (a hand-dispatched run) always runs it. test.yml does
    not run on a push to fp/v2-impl, so there is no automatic backstop on the integration branch until the nightly G3
    takes this check over.

    -ChangedPath is git's own spelling (forward slashes, relative to the repository root), matched case-sensitively.
    Returns Run and Reason; with -GitHubOutput, also appends run=true or run=false to that file.

.EXAMPLE
    pwsh -NoProfile -File scripts/Select-StagedG3ContentConflictRun.ps1 -ChangedPath (git diff --name-only HEAD^1 HEAD)
#>
[CmdletBinding()]
param(
    [string[]]$ChangedPath = @(),
    [switch]$NotAPullRequest,
    [string]$GitHubOutput
)

$ErrorActionPreference = 'Stop'

# Whole files, then directory prefixes (each ending in '/').
$files = @(
    'scripts/run-staged-g3.ps1'
    'scripts/g3-slice-evidence.ps1'
    'scripts/Test-StagedG3ContentConflict.ps1'
    'scripts/Select-StagedG3ContentConflictRun.ps1'
    # What the judgments also read (review B3 of PR #574): the store behind Get-ContentConflictVerdict, the problem codes,
    # and appsettings.json, from which the self-check takes the ProtocolCandidate identity.
    'src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs'
    'src/ControlServer.Domain/ProtocolErrorCodes.cs'
    'src/ControlServer.Host/appsettings.json'
)
$prefixes = @(
    'scripts/l2/'
    'src/ControlServer.Host/Transport/'
)

$changed = @($ChangedPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() })
if ($NotAPullRequest) {
    $decision = [pscustomobject]@{ Run = $true; Reason = 'not a pull request: the content-conflict self-check always runs' }
} else {
    $hits = @($changed | Where-Object {
            $path = $_
            $files -ccontains $path -or @($prefixes | Where-Object { $path.StartsWith($_, [StringComparison]::Ordinal) }).Count -gt 0
        })
    $decision = if ($hits.Count -gt 0) {
        [pscustomobject]@{ Run = $true; Reason = "runs: this pull request changes $($hits.Count) path(s) it checks, first $($hits[0])" }
    } else {
        [pscustomobject]@{ Run = $false; Reason = ("skipped: none of the $($changed.Count) changed path(s) is one it checks " +
                "($($files -join ', '), or under $($prefixes -join ', '))") }
    }
}

Write-Host "Staged content-conflict self-check: $($decision.Reason)"
if (-not [string]::IsNullOrEmpty($GitHubOutput)) {
    Add-Content -LiteralPath $GitHubOutput -Value "run=$($decision.Run.ToString().ToLowerInvariant())"
}
$decision
