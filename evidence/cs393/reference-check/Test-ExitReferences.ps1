#Requires -Version 7
<#
control-server#393: every repository path the exit report and its defect entries cite must exist in what is committed.

"Exists" means tracked, or untracked and not ignored -- what `git add` would commit -- so a file kept out by .gitignore (the
*.db stores) does not count. Cited are:
  - markdown link targets, resolved against the citing file;
  - backticked tokens that start with a repository top-level directory (evidence/, docs/, scripts/, src/, tests/, tools/,
    vendor/, .github/), with a :line suffix dropped, '*' taken as a glob and '-01..03' expanded;
  - backticked bare file names (Foo.cs, Bar.ps1:12), looked up by file name anywhere in the repository.
A path that is not found here is looked up in the onboard repository (-OnboardRepository), since the report cites both. A
token with '…' or '<' in it is a pattern for the reader, not a path, and is listed as not checkable. -Pending names paths
that are created after this commit on purpose (the package evidence, the real-rig rerun); they are listed apart.
Exit 1 when any cited path is missing.
#>
param(
    [string]$Repository = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path,
    [Parameter(Mandatory)][string]$OnboardRepository,
    [string[]]$Files = @('docs/batch-8-v2-exit-report.md', 'docs/defects/20261009-real-rig-restart-recovery-first-press-cs307-shape.md',
        'docs/defects/20261010-staged-g3-content-conflict-expects-disconnect.md',
        'docs/defects/20261010-journey-g3-two-scenarios-first-run-stale-scripts.md',
        'docs/defects/20261010-journey-g3-uia-fixture-reads-not-told-from-product.md'),
    [string[]]$Pending = @(),
    # The program repository: the report cites its change proposals and site procedures.
    [string]$ProgramRepository = '',
    # A directory whose top-level files the report cites by name (the workspace root's Invoke-HeavyLocal.ps1).
    [string]$WorkspaceRoot = '',
    # Backticked tokens that look like paths but are not (a branch name).
    [string[]]$NotPaths = @()
)
$ErrorActionPreference = 'Stop'

function Get-Committable([string]$Root) {
    $paths = @(git -C $Root ls-files --cached --others --exclude-standard)
    $set = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $dirs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($p in $paths) {
        $null = $set.Add($p); $null = $names.Add(($p -split '/')[-1])
        $parts = $p -split '/'
        for ($i = 1; $i -lt $parts.Count; $i++) { $null = $dirs.Add(($parts[0..($i - 1)] -join '/')) }
    }
    return [pscustomobject]@{ Files = $set; Dirs = $dirs; Names = $names; All = $paths }
}
# pwsh -File passes a comma list as one string.
$Pending = @($Pending | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$NotPaths = @($NotPaths | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$here = Get-Committable $Repository
$onboard = Get-Committable $OnboardRepository
$program = if ($ProgramRepository) { Get-Committable $ProgramRepository } else { $null }

function Test-In([object]$Index, [string]$Path) {
    $p = $Path.TrimEnd('/')
    if ($p.Contains('*')) {
        $regex = '^' + ([regex]::Escape($p) -replace '\\\*', '[^/]*') + '(/|$)'
        return @($Index.All | Where-Object { $_ -match $regex }).Count -gt 0
    }
    return $Index.Files.Contains($p) -or $Index.Dirs.Contains($p)
}

$top = '^(evidence|docs|scripts|src|tests|tools|vendor|\.github)/'
$rows = [System.Collections.Generic.List[object]]::new()
foreach ($file in $Files) {
    $text = Get-Content -LiteralPath (Join-Path $Repository $file) -Raw
    $dir = Split-Path $file -Parent
    $cited = [System.Collections.Generic.List[object]]::new()
    foreach ($m in [regex]::Matches($text, '\]\(([^)\s]+)\)')) {
        $target = $m.Groups[1].Value
        if ($target -match '^(https?:|#|mailto:)') { continue }
        $resolved = [IO.Path]::GetRelativePath($Repository, [IO.Path]::GetFullPath((Join-Path (Join-Path $Repository $dir) $target))).Replace('\', '/')
        $cited.Add(@{ Kind = 'link'; Token = $target; Path = $resolved })
    }
    foreach ($m in [regex]::Matches($text, '`([^`\s]+)`')) {
        $token = $m.Groups[1].Value
        if ($token -match $top) {
            $cited.Add(@{ Kind = 'path'; Token = $token; Path = ($token -replace ':\d+(-\d+)?$', '') })
        } elseif ($token -match '^[\w.-]+\.(cs|ps1|psm1|psd1|md|json|yml|csv|tsv|txt|xaml)(:\d+(-\d+)?)?$') {
            $cited.Add(@{ Kind = 'name'; Token = $token; Path = ($token -replace ':\d+(-\d+)?$', '') })
        }
    }
    foreach ($c in $cited) {
        $paths = @($c.Path)
        if ($c.Path -match '^(.*-)(\d\d)\.\.(\d\d)(/?.*)$') {
            $paths = @([int]$Matches[2]..[int]$Matches[3] | ForEach-Object { '{0}{1:D2}{2}' -f $Matches[1], $_, $Matches[4] })
        }
        foreach ($p in $paths) {
            $verdict = if ($p -in $NotPaths) { 'NOT_A_PATH' }
                elseif ($p -match '[…<>]') { 'NOT_CHECKABLE' }
                elseif (@($Pending | Where-Object { $p.StartsWith($_) }).Count -gt 0) { 'PENDING' }
                elseif ($c.Kind -eq 'name') {
                    if ($here.Names.Contains($p)) { 'OK' }
                    elseif ($onboard.Names.Contains($p)) { 'OK_ONBOARD' }
                    elseif ($program -and $program.Names.Contains($p)) { 'OK_PROGRAM' }
                    elseif ($WorkspaceRoot -and (Test-Path -LiteralPath (Join-Path $WorkspaceRoot $p))) { 'OK_WORKSPACE' }
                    else { 'MISSING' }
                }
                elseif (Test-In $here $p) { 'OK' }
                elseif (Test-In $onboard $p) { 'OK_ONBOARD' }
                elseif ($program -and (Test-In $program $p)) { 'OK_PROGRAM' }
                else { 'MISSING' }
            $rows.Add([pscustomobject]@{ File = $file; Kind = $c.Kind; Token = $c.Token; Path = $p; Verdict = $verdict })
        }
    }
}
$unique = @($rows | Sort-Object File, Path, Verdict -Unique)
$unique | ForEach-Object { "{0}`t{1}`t{2}`t{3}" -f $_.Verdict, $_.File, $_.Kind, $_.Path }
''
'by verdict: ' + (($unique | Group-Object Verdict | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', ')
$missing = @($unique | Where-Object Verdict -eq 'MISSING')
exit ($missing.Count -eq 0 ? 0 : 1)
