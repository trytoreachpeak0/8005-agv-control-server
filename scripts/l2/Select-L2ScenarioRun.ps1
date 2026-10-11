#Requires -Version 7

<#
.SYNOPSIS
    Decides whether l2.yml's scenarios job runs the synthetic L2 round for this event (control-server#630).

.DESCRIPTION
    On 2026-10-11 the one runner that takes this job ran 25 rounds of about 30 minutes each, and 8 of the 19 pull
    requests behind them changed nothing the round consumes (the v2 workspace's ci-research/l2-second-runner.md). The
    user decided that day that such a pull request ends the job green without running a scenario, the reason printed.

    The rule leans one way: a path is left out only when it is known not to reach the round, and everything else --
    including anything new or unrecognised -- runs it. Left out:

      docs/, evidence/, tests/      tests/ is compiled by ControlServer.sln in this job's build, but no L2 process
                                    runs any of it; a test that stops compiling is test.yml's red, which it already is.
      README.md, CLAUDE.md, ControlServer.http
      .github/workflows/test.yml, g3.yml, release.yml   l2.yml itself, and any other file under .github/, runs it.
      A file directly under scripts/ that no L2 code names: the G3 runners, the install scripts, their self-checks.

    "Named" is worked out from the checkout, not listed here, so it cannot go stale: every string in the code of
    scripts/l2 (*.ps1, *.psm1, *.psd1, read through the PowerShell parser, so comments do not count) and the whole text
    of l2.yml; a file directly under scripts/ whose name appears in one of those strings is an input, and its own strings
    join the search, until nothing new is named. Today that brings in DesktopLock.psm1 (the orchestrator imports it) and
    LoopbackListener.psm1 (an L2 self-check's). Subdirectories of scripts/ other than l2 (parallel/, field/) are always
    inputs: nothing here reads their names.

    What it assumes, and what would break it: L2 code reaches a file outside scripts/l2 only by writing its name. A
    script that loaded files by listing a directory (Get-ChildItem scripts/*.psm1) would load something no string
    names. The two that list scripts/ today, Test-L2ClosureCapture.ps1 and Test-L2WholeArrayReturn.ps1, are test.yml's
    self-checks and are not run by this job.

    Never skipped: an event that is not a pull request (a push, a dispatched run -- a backstop, a consecutive run, a
    real rig), a pull request body with an L2-Consecutive line (it asks for this round by name), and a change list that
    is empty because it could not be read.

    -ChangedPath is git's own spelling (forward slashes, relative to the repository root). Directory prefixes and the
    listed files match case-sensitively, so a differently-cased spelling is not recognised and runs the round; a name
    found in L2 code matches case-insensitively, as Windows resolves it.

    Returns Run, Reason, Inputs (the changed paths that count) and LeftOut; with -GitHubOutput, also appends run=true or
    run=false to that file.

.EXAMPLE
    pwsh -NoProfile -File scripts/l2/Select-L2ScenarioRun.ps1 -ChangedPath (git diff --no-renames --name-only HEAD^1 HEAD)
#>
[CmdletBinding()]
param(
    [string[]]$ChangedPath = @(),
    [switch]$NotAPullRequest,
    [string]$PullRequestBody,
    [string]$Repository = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$GitHubOutput
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$leftOutPrefixes = @('docs/', 'evidence/', 'tests/')
$leftOutFiles = @('README.md', 'CLAUDE.md', 'ControlServer.http',
    '.github/workflows/test.yml', '.github/workflows/g3.yml', '.github/workflows/release.yml')

# Every string L2 code holds, joined into one text to search; files directly under scripts/ that any of it names join it.
function Get-NamedScriptRootLeaves([string]$Root) {
    $pieces = [Collections.Generic.List[string]]::new()
    function Add-CodeStrings([string]$File) {
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($File, [ref]$null, [ref]$null)
        foreach ($node in $ast.FindAll({ param($n)
                    $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
                    $n -is [System.Management.Automation.Language.ExpandableStringExpressionAst] }, $true)) {
            $pieces.Add($node.Value)
        }
    }
    $l2 = Join-Path $Root 'scripts/l2'
    if (Test-Path -LiteralPath $l2) {
        foreach ($file in Get-ChildItem -LiteralPath $l2 -Recurse -File -Include *.ps1, *.psm1, *.psd1) { Add-CodeStrings $file.FullName }
    }
    $workflow = Join-Path $Root '.github/workflows/l2.yml'
    if (Test-Path -LiteralPath $workflow) { $pieces.Add((Get-Content -Raw -LiteralPath $workflow)) }

    $scriptsDir = Join-Path $Root 'scripts'
    $candidates = @(if (Test-Path -LiteralPath $scriptsDir) { Get-ChildItem -LiteralPath $scriptsDir -File })
    $named = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    do {
        $text = $pieces -join "`n"
        $added = $false
        foreach ($candidate in $candidates) {
            if ($named.Contains($candidate.Name)) { continue }
            if ($text.IndexOf($candidate.Name, [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
            $null = $named.Add($candidate.Name)
            $added = $true
            if ($candidate.Extension -in '.ps1', '.psm1', '.psd1') { Add-CodeStrings $candidate.FullName }
        }
    } while ($added)
    return , $named
}

$changed = @($ChangedPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() })
$inputs = [Collections.Generic.List[string]]::new()
$leftOut = [Collections.Generic.List[string]]::new()

$consecutive = -not [string]::IsNullOrEmpty($PullRequestBody) -and
    [regex]::IsMatch($PullRequestBody, '(?im)^[ \t>*-]*L2-Consecutive[ \t]*:')

if ($NotAPullRequest) {
    $decision = [pscustomobject]@{ Run = $true; Reason = 'runs: not a pull request -- a push or a dispatched run is never skipped' }
} elseif ($consecutive) {
    $decision = [pscustomobject]@{ Run = $true; Reason = 'runs: the pull request body has an L2-Consecutive line' }
} elseif ($changed.Count -eq 0) {
    $decision = [pscustomobject]@{ Run = $true; Reason = 'runs: the changed paths are unknown, so nothing can be ruled out' }
} else {
    $named = $null
    foreach ($path in $changed) {
        $isLeftOut = $leftOutFiles -ccontains $path -or
            @($leftOutPrefixes | Where-Object { $path.StartsWith($_, [StringComparison]::Ordinal) }).Count -gt 0
        if (-not $isLeftOut -and $path -cmatch '^scripts/[^/]+$') {
            if ($null -eq $named) { $named = Get-NamedScriptRootLeaves $Repository }
            $isLeftOut = -not $named.Contains($path.Substring('scripts/'.Length))
        }
        if ($isLeftOut) { $leftOut.Add($path) } else { $inputs.Add($path) }
    }
    $decision = if ($inputs.Count -gt 0) {
        [pscustomobject]@{ Run = $true; Reason = "runs: $($inputs.Count) of the $($changed.Count) changed path(s) can reach the L2 round, first $($inputs[0])" }
    } else {
        [pscustomobject]@{ Run = $false; Reason = ("skipped: none of the $($changed.Count) changed path(s) reaches the L2 round " +
                '(only docs/, evidence/, tests/, root documents, the other workflows, or scripts/ files no L2 code names)') }
    }
}
$decision | Add-Member -NotePropertyName Inputs -NotePropertyValue @($inputs)
$decision | Add-Member -NotePropertyName LeftOut -NotePropertyValue @($leftOut)

Write-Host "Synthetic L2 round: $($decision.Reason)"
if (-not [string]::IsNullOrEmpty($GitHubOutput)) {
    Add-Content -LiteralPath $GitHubOutput -Value "run=$($decision.Run.ToString().ToLowerInvariant())"
}
$decision
