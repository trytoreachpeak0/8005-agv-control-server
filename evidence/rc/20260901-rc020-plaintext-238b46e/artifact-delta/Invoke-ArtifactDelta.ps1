<#
.SYNOPSIS
File-level delta between this release candidate and the previous one, plus the apphost analysis.

.DESCRIPTION
Compares the two manifests' per-file hashes. Managed assemblies get a fresh MVID on every build, so
a DIFF row on a .dll is rebuild noise and proves nothing -- the symbol probe is what carries the
content claim. The native apphost .exe is the interesting case: it is not compiled from this
source, so its bytes should only move where the Win32 version resource embeds InformationalVersion
(which carries the git commit). This script checks that by locating every differing byte.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NewManifest,
    [Parameter(Mandatory)][string]$OldManifest,
    [Parameter(Mandatory)][string]$NewRoot,
    [Parameter(Mandatory)][string]$OldRoot,
    [Parameter(Mandatory)][string]$OutputJson
)

$ErrorActionPreference = 'Stop'

$new = Get-Content -Raw -LiteralPath $NewManifest | ConvertFrom-Json
$old = Get-Content -Raw -LiteralPath $OldManifest | ConvertFrom-Json

function Get-DifferingRanges([string]$PathA, [string]$PathB) {
    $a = [IO.File]::ReadAllBytes($PathA)
    $b = [IO.File]::ReadAllBytes($PathB)
    if ($a.Length -ne $b.Length) { return @{ lengthsEqual = $false; ranges = @() } }
    $ranges = @()
    $start = -1
    for ($i = 0; $i -lt $a.Length; $i++) {
        if ($a[$i] -ne $b[$i]) { if ($start -lt 0) { $start = $i } }
        elseif ($start -ge 0) { $ranges += [ordered]@{ start = $start; end = $i - 1 }; $start = -1 }
    }
    if ($start -ge 0) { $ranges += [ordered]@{ start = $start; end = $a.Length - 1 } }
    return @{ lengthsEqual = $true; ranges = @($ranges) }
}

$componentReports = @()
foreach ($component in @(
    @{ key = 'controlServer'; relative = 'controlserver' },
    @{ key = 'onboardHmi'; relative = 'onboard-hmi' })) {
    $n = @{}; foreach ($f in $new.components.($component.key).files) { $n[$f.path] = $f.sha256 }
    $o = @{}; foreach ($f in $old.components.($component.key).files) { $o[$f.path] = $f.sha256 }
    $same = 0; $changed = @(); $added = @(); $removed = @()
    foreach ($k in $n.Keys) {
        if (-not $o.ContainsKey($k)) { $added += $k; continue }
        if ($n[$k] -ceq $o[$k]) { $same++ } else { $changed += $k }
    }
    foreach ($k in $o.Keys) { if (-not $n.ContainsKey($k)) { $removed += $k } }
    $componentReports += [ordered]@{
        component = $component.key
        relativePath = $component.relative
        same = $same
        changed = @($changed | Sort-Object)
        added = @($added | Sort-Object)
        removed = @($removed | Sort-Object)
    }
}

$apphosts = @()
foreach ($relative in @('controlserver/ControlServer.Host.exe', 'onboard-hmi/SQCD.Agv.Wpf.exe')) {
    $newPath = Join-Path $NewRoot ($relative -replace '/', '\')
    $oldPath = Join-Path $OldRoot ($relative -replace '/', '\')
    $delta = Get-DifferingRanges $newPath $oldPath
    $flat = @($delta.ranges | ForEach-Object { $_.start..$_.end }) | Sort-Object
    $apphosts += [ordered]@{
        path = $relative
        newProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($newPath).ProductVersion
        oldProductVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($oldPath).ProductVersion
        lengthsEqual = $delta.lengthsEqual
        differingByteCount = $flat.Count
        firstDifferingOffset = if ($flat.Count) { '0x{0:X}' -f $flat[0] } else { $null }
        lastDifferingOffset = if ($flat.Count) { '0x{0:X}' -f $flat[-1] } else { $null }
        differingRegionSpan = if ($flat.Count) { $flat[-1] - $flat[0] + 1 } else { 0 }
        differingRegionCount = @($delta.ranges).Count
    }
}

$document = [ordered]@{
    schemaVersion = 1
    newRoot = $NewRoot
    oldRoot = $OldRoot
    components = @($componentReports)
    apphosts = @($apphosts)
}
[IO.File]::WriteAllText($OutputJson, ($document | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$document | ConvertTo-Json -Depth 5
