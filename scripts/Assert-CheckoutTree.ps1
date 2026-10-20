#Requires -Version 7

<#
.SYNOPSIS
    Fails a job whose actions/checkout left a different tree from the one it asked for.

.DESCRIPTION
    The step "Check the checkout is the tree it asked for", right after every checkout of this repository on a
    self-hosted runner. A runner reuses one directory for every job of this repository, and on 2026-10-20 an earlier
    job's sparse-checkout setting survived actions/checkout@v7's `git sparse-checkout disable`: the checkout printed
    "not uptodate; will not remove from working tree", exited 0 and left out nearly every file (l2 run 39457961360),
    so the job went red on MSB1009 two steps later with nothing pointing at the cause. Test-CheckoutSparseState.ps1
    has the whole story.

    Red (exit 1, CHECKOUT_TREE_INCOMPLETE and an ::error:: per finding) when any of these holds:
      - .git/config sets core.sparseCheckout. Our checkouts never write it there; when it is there, an earlier job's
        state is in force, and the next job's checkout will apply it too;
      - a file the checkout asked for is left out (skip-worktree), or one it left out is checked out;
      - a tracked file that is not left out is missing from the worktree.
    What the checkout asked for: CHECKOUT_SPARSE (or -SparseCheckout), the checkout's own sparse-checkout list, one
    directory per line, in cone mode: the files at the top level and everything under each directory. Empty is the
    whole tree.

    Green prints CHECKOUT_TREE_OK with the counts. A second or two on the full repository.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/Assert-CheckoutTree.ps1
#>
[CmdletBinding()]
param(
    [string]$Path = '.',
    [string[]]$SparseCheckout = @(($env:CHECKOUT_SPARSE ?? '') -split '\r?\n' | ForEach-Object Trim | Where-Object { $_ })
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = (Resolve-Path -LiteralPath $Path).ProviderPath
$cone = @($SparseCheckout | ForEach-Object { $_.Trim('/') } | Where-Object { $_ })
$problems = [System.Collections.Generic.List[string]]::new()
function Get-Sample([string[]]$Paths) {
    $shown = @($Paths | Select-Object -First 10) -join ', '
    if ($Paths.Count -gt 10) { "$shown and $($Paths.Count - 10) more" } else { $shown }
}

$localSparse = & git config --file (Join-Path $repository '.git/config') --get core.sparseCheckout
if ($localSparse) {
    $problems.Add("core.sparseCheckout=$localSparse is set in .git/config. An earlier job's sparse-checkout state is in force, " +
        "which actions/checkout's 'git sparse-checkout disable' cannot clear (the step before the checkout should have).")
}

$listing = & git -C $repository ls-files -t -z
if ($LASTEXITCODE -ne 0) { throw "git ls-files -t in $repository exited $LASTEXITCODE" }
$entries = @(($listing -join '') -split "`0" | Where-Object { $_ })
function Test-InCone([string]$File) {
    if ($cone.Count -eq 0 -or -not $File.Contains('/')) { return $true }
    foreach ($dir in $cone) { if ($File.StartsWith("$dir/", [StringComparison]::OrdinalIgnoreCase)) { return $true } }
    return $false
}
$leftOutWanted = [System.Collections.Generic.List[string]]::new()
$presentUnwanted = [System.Collections.Generic.List[string]]::new()
$leftOut = 0
foreach ($entry in $entries) {
    $tag = $entry.Substring(0, 1); $file = $entry.Substring(2)
    $skipped = $tag -ceq 'S'
    if ($skipped) { $leftOut++ }
    $wanted = Test-InCone $file
    if ($skipped -and $wanted) { $leftOutWanted.Add($file) }
    if (-not $skipped -and -not $wanted) { $presentUnwanted.Add($file) }
}
$asked = if ($cone.Count -eq 0) { 'the whole tree' } else { "the top level and $($cone -join ', ')" }
if ($leftOutWanted.Count -gt 0) {
    $problems.Add("$($leftOutWanted.Count) file(s) the checkout asked for ($asked) are left out by sparse checkout: $(Get-Sample $leftOutWanted)")
}
if ($presentUnwanted.Count -gt 0) {
    $problems.Add("$($presentUnwanted.Count) file(s) outside what the checkout asked for ($asked) are checked out, so its sparse-checkout list was not applied: $(Get-Sample $presentUnwanted)")
}

$deleted = @((& git -C $repository ls-files --deleted -z) -join '' -split "`0" | Where-Object { $_ })
if ($LASTEXITCODE -ne 0) { throw "git ls-files --deleted in $repository exited $LASTEXITCODE" }
if ($deleted.Count -gt 0) {
    $problems.Add("$($deleted.Count) tracked file(s) are missing from the worktree: $(Get-Sample $deleted)")
}

if ($problems.Count -gt 0) {
    foreach ($problem in $problems) { Write-Host "::error title=Checkout incomplete::$problem" }
    Write-Host "CHECKOUT_TREE_INCOMPLETE: $repository at $(& git -C $repository rev-parse HEAD). See scripts/Test-CheckoutSparseState.ps1."
    exit 1
}
Write-Host "CHECKOUT_TREE_OK: $($entries.Count) tracked file(s), $leftOut left out as asked ($asked)"
exit 0
