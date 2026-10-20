#Requires -Version 7

<#
.SYNOPSIS
    Decides which self-check steps of test.yml run for this event (control-server#803).

.DESCRIPTION
    Measured over the 132 successful test jobs from 2026-10-11 (the v2 workspace's
    ci-research/migration-speedup-candidates.md, 3.1): the self-checks before Test took a median of 600 s of a 21m40s
    job, the suite 477 s. Most pull requests change src/ and tests/, which almost none of them read. The user decided on
    2026-10-20 that a pull request skips each self-check whose inputs it does not change, the way control-server#630
    skips the L2 round.

    TestSelfCheckScope.psd1 holds the table; its header says what a step's inputs are. In short: the step's script, every
    file that script reaches by name (worked out here, from the checkout, so it cannot go stale), and the few inputs a
    name cannot show, listed in the table.

    What it assumes, and what would break it: a self-check reaches a file only by writing its name or its path, or
    through an input the table lists. A script that built a file name from a variable, or listed a directory, without the
    table saying so, would read something no string names. Test-TestSelfCheckScope.ps1 resolves every literal load of
    every step's scripts on disk as an independent check of the names, but it cannot see a variable either.

    Never skipped: an event that is not a pull request (a push, a dispatched run), a change list that is empty because it
    could not be read, and a change to anything in the table's Always (test.yml, the table, this script).

    -ChangedPath is git's own spelling (forward slashes, relative to the repository root); matched case-insensitively, so
    a differently cased spelling counts. A step whose DecidedBy names another selector is left to it and not answered here.

    Returns Reason and Steps, one per step it decides: Key, Name, Run, Reason, Hits (the changed paths that count) and
    Reached (the files its script reaches by name, and the directories, ending in '/'). With -GitHubOutput, appends
    <Key>=true or <Key>=false per step; with -StepSummary, writes the skipped steps there.

.EXAMPLE
    pwsh -NoProfile -File scripts/Select-TestSelfCheckRun.ps1 -ChangedPath (git diff --no-renames --name-only HEAD^1 HEAD)
#>
[CmdletBinding()]
param(
    [string[]]$ChangedPath = @(),
    [switch]$NotAPullRequest,
    [string]$Repository = (Split-Path -Parent $PSScriptRoot),
    [string]$ScopeFile = (Join-Path $PSScriptRoot 'TestSelfCheckScope.psd1'),
    [string]$GitHubOutput,
    [string]$StepSummary
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Repository = [IO.Path]::GetFullPath($Repository)
$scope = Import-PowerShellDataFile -LiteralPath $ScopeFile
$ownSteps = @($scope.Steps | Where-Object { -not $_.ContainsKey('DecidedBy') })
$ignoreCase = [StringComparison]::OrdinalIgnoreCase

function ConvertTo-RepositoryPath([string]$FullName) { [IO.Path]::GetRelativePath($Repository, $FullName).Replace('\', '/') }

# Does a changed path count for one input pattern: a prefix ending in '/', a pattern holding '*', or a file.
function Test-Matches([string]$Path, [string]$Pattern) {
    if ($Pattern.EndsWith('/')) { return $Path.StartsWith($Pattern, $ignoreCase) }
    if ($Pattern.Contains('*')) { return $Path -like $Pattern }
    return [string]::Equals($Path, $Pattern, $ignoreCase)
}

$changed = @($ChangedPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim().Replace('\', '/') })

# --- What each step's script reaches by name. ------------------------------------------------------------------------------
# Leaf name -> repository paths, for every file under scripts/ in the checkout and every changed path under scripts/ (a
# deleted file, or a rename's old name, is gone from the checkout but may still be named by code the pull request kept).
$leaves = [Collections.Generic.Dictionary[string, Collections.Generic.List[string]]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-Leaf([string]$RepositoryPath) {
    $leaf = $RepositoryPath.Substring($RepositoryPath.LastIndexOf('/') + 1)
    if (-not $leaves.ContainsKey($leaf)) { $leaves[$leaf] = [Collections.Generic.List[string]]::new() }
    if (-not $leaves[$leaf].Contains($RepositoryPath)) { $leaves[$leaf].Add($RepositoryPath) }
}
$scriptsDir = Join-Path $Repository 'scripts'
if (Test-Path -LiteralPath $scriptsDir) {
    foreach ($file in Get-ChildItem -LiteralPath $scriptsDir -Recurse -File) { Add-Leaf (ConvertTo-RepositoryPath $file.FullName) }
}
foreach ($path in $changed) { if ($path.StartsWith('scripts/', $ignoreCase)) { Add-Leaf $path } }
$leafNames = [string[]]@($leaves.Keys)

# What one file names: files by leaf (under scripts/), and repository paths, files or directories. Read once per file.
$namedCache = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
function Get-Named([string]$RepositoryPath) {
    if ($namedCache.ContainsKey($RepositoryPath)) { return $namedCache[$RepositoryPath] }
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $prefixes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $full = [IO.Path]::Combine($Repository, $RepositoryPath)
    $directory = [IO.Path]::GetDirectoryName($full)
    if ($RepositoryPath -match '\.ps[dm]?1$' -and [IO.File]::Exists($full)) {
        # The parser's string tokens: quoted strings, here-strings and bare words (a bare word is a string token too), and
        # never a comment. Tokens rather than the syntax tree's string nodes because walking the tree with a script-block
        # predicate costs seconds over the scripts a large self-check reaches.
        $tokens = $null
        $null = [System.Management.Automation.Language.Parser]::ParseFile($full, [ref]$tokens, [ref]$null)
        $strings = [string[]]@(foreach ($token in $tokens) { if ($token -is [System.Management.Automation.Language.StringToken]) { $token.Value } })
        # One text, the strings separated by a character no script writes, so that the work below is a few .NET calls
        # rather than a PowerShell loop over a hundred thousand words.
        $separator = [string][char]1
        $text = $separator + [string]::Join($separator, $strings) + $separator
        # Leaf names: every run of file-name characters, split at slashes too.
        $words = [Collections.Generic.HashSet[string]]::new([string[]][regex]::Split($text, '[^A-Za-z0-9_.\-]+'), [StringComparer]::OrdinalIgnoreCase)
        $words.IntersectWith($leafNames)
        foreach ($leaf in $words) { foreach ($hit in $leaves[$leaf]) { $null = $files.Add($hit) } }
        # Paths: a run with at least one slash, written relative to the script's own directory ($PSScriptRoot), or to the
        # repository root.
        foreach ($match in [regex]::Matches($text, '[A-Za-z0-9_.\-]+(?:[/\\][A-Za-z0-9_.\-]+)+')) {
            $path = $match.Value.Replace('\', '/')
            $start = $text.LastIndexOf($separator, $match.Index, [StringComparison]::Ordinal) + 1
            $string = $text.Substring($start, $text.IndexOf($separator, $match.Index, [StringComparison]::Ordinal) - $start)
            # A directory only from a string that is a path, not from one in a sentence: "经 scripts/parallel（19 号）部署时"
            # in a message is not a read of everything under scripts/parallel. A file is narrow enough either way.
            $pathLike = $string -notmatch '\s'
            $stripped = $path
            while ($stripped.StartsWith('./') -or $stripped.StartsWith('../')) { $stripped = $stripped.Substring($stripped.IndexOf('/') + 1) }
            foreach ($candidate in [IO.Path]::GetFullPath([IO.Path]::Combine($directory, $path)), [IO.Path]::GetFullPath([IO.Path]::Combine($Repository, $stripped))) {
                $relative = ConvertTo-RepositoryPath $candidate
                # Not the repository itself, nothing outside it.
                if ($relative -eq '.' -or $relative.StartsWith('../')) { continue }
                if ([IO.File]::Exists($candidate)) { $null = $files.Add($relative) }
                elseif ($pathLike -and [IO.Directory]::Exists($candidate)) { $null = $prefixes.Add($relative.TrimEnd('/') + '/') }
            }
        }
    }
    $named = [pscustomobject]@{ Files = $files; Prefixes = $prefixes }
    $namedCache[$RepositoryPath] = $named
    return $named
}

function Get-Reached([string]$Script) {
    $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $prefixes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $queue = [Collections.Generic.Queue[string]]::new()
    $null = $files.Add($Script)
    $queue.Enqueue($Script)
    while ($queue.Count -gt 0) {
        $named = Get-Named $queue.Dequeue()
        foreach ($prefix in $named.Prefixes) { $null = $prefixes.Add($prefix) }
        foreach ($file in $named.Files) { if ($files.Add($file)) { $queue.Enqueue($file) } }
    }
    return [pscustomobject]@{ Files = $files; Prefixes = $prefixes }
}

# --- The decision. ----------------------------------------------------------------------------------------------------------
$always = @($changed | Where-Object { $path = $_; @($scope.Always | Where-Object { Test-Matches $path $_ }).Count -gt 0 })
$runAll = if ($NotAPullRequest) { 'runs: not a pull request -- a push or a dispatched run never skips a self-check' }
elseif ($changed.Count -eq 0) { 'runs: the changed paths are unknown, so nothing can be ruled out' }
elseif ($always.Count -gt 0) { "runs: the pull request changes $($always[0]), which every self-check depends on" }
else { $null }

$steps = foreach ($entry in $ownSteps) {
    # NameClosure = $false: the script only, for a self-check whose strings name paths as test data rather than as reads.
    $reached = if ($entry.ContainsKey('NameClosure') -and -not $entry.NameClosure) {
        [pscustomobject]@{ Files = [Collections.Generic.HashSet[string]]::new([string[]]@($entry.Script), [StringComparer]::OrdinalIgnoreCase)
            Prefixes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase) }
    } else { Get-Reached $entry.Script }
    $hits = [Collections.Generic.List[string]]::new()
    foreach ($path in $changed) {
        $counts = $reached.Files.Contains($path) -or
            @($reached.Prefixes | Where-Object { $path.StartsWith($_, $ignoreCase) }).Count -gt 0 -or
            @($entry.Inputs | Where-Object { Test-Matches $path $_ }).Count -gt 0
        if ($counts) { $hits.Add($path) }
    }
    $run = $null -ne $runAll -or $hits.Count -gt 0
    $reason = if ($null -ne $runAll) { $runAll }
    elseif ($hits.Count -gt 0) { "runs: $($hits.Count) changed path(s) are its inputs, first $($hits[0])" }
    else { 'skipped: none of the changed paths is its input' }
    [pscustomobject]@{
        Key = $entry.Key; Name = $entry.Name; Run = $run; Reason = $reason
        Hits = @($hits); Reached = @(@($reached.Files) + @($reached.Prefixes) | Sort-Object)
    }
}
$steps = @($steps)
$skipped = @($steps | Where-Object { -not $_.Run })
$reason = if ($null -ne $runAll) { $runAll }
elseif ($skipped.Count -eq 0) { "runs all $($steps.Count): every self-check has a changed input" }
else { "skipped $($skipped.Count) of $($steps.Count) self-checks: this pull request changes none of their inputs" }

Write-Host "test.yml self-checks: $reason"
foreach ($step in $steps) { Write-Host ("  {0,-4} {1} -- {2}" -f $(if ($step.Run) { 'run' } else { 'skip' }), $step.Name, $step.Reason) }
if (-not [string]::IsNullOrEmpty($GitHubOutput)) {
    Add-Content -LiteralPath $GitHubOutput -Value @($steps | ForEach-Object { "$($_.Key)=$($_.Run.ToString().ToLowerInvariant())" })
}
if (-not [string]::IsNullOrEmpty($StepSummary) -and $skipped.Count -gt 0) {
    $lines = @("### 本 PR 跳过了 $($skipped.Count) 个自检，绿不代表它们通过", '',
        '它们的输入都没有改动（control-server#803，scripts/TestSelfCheckScope.psd1）。push、手动派发与改了 test.yml 的 PR 全跑。', '') +
        @($skipped | ForEach-Object { "- $($_.Name)" })
    Add-Content -LiteralPath $StepSummary -Value $lines
}
[pscustomobject]@{ Reason = $reason; Steps = $steps }
