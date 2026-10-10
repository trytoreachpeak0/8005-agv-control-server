#Requires -Version 7
<#
cs#393 evidence trim, coordinator's option A (2026-10-10): in the three red journey rounds keep every red scenario whole and,
for each PASS scenario, only assertions.json, SUMMARY.md and timeline.jsonl. Top-level files and every directory other than
scenarios/ are kept. The fourth round is not touched.

Before removing anything, the whole round is copied to $Backup. Every removed file is listed with its size and SHA-256 in
$Manifest, so the originals can be matched to what the repository no longer carries.
#>
param(
    [string]$Root = 'C:/Users/szy/Desktop/8005-workspace-v2/worktrees/cs393-8005-agv-control-server/evidence/g3',
    [string]$Backup = 'C:/w2g/cs393/g3-journey-originals',
    [Parameter(Mandatory)][string]$Manifest
)
$ErrorActionPreference = 'Stop'
$keep = 'assertions.json', 'SUMMARY.md', 'timeline.jsonl'
$rows = [System.Collections.Generic.List[string]]::new()
$rows.Add("round`tscenario`trelativePath`tbytes`tsha256")
$null = New-Item -ItemType Directory -Path $Backup -Force
foreach ($r in '5f3adc42', '3411887d', '1f63fe0b') {
    $name = "20261010-protocol-v3.0.0-journey-$r"
    $d = Join-Path $Root $name
    $copy = Join-Path $Backup $name
    if (-not (Test-Path $copy)) { Copy-Item -LiteralPath $d -Destination $copy -Recurse }
    $before = (Get-ChildItem $d -Recurse -File).Count
    if ((Get-ChildItem $copy -Recurse -File).Count -ne $before) { throw "backup of $name is incomplete" }
    foreach ($s in Get-ChildItem (Join-Path $d 'scenarios') -Directory) {
        $a = Get-Content (Join-Path $s.FullName 'assertions.json') -Raw | ConvertFrom-Json -AsHashtable
        if ($a['outcome'] -ne 'PASS') { continue }
        foreach ($f in Get-ChildItem $s.FullName -Recurse -File) {
            if ($f.DirectoryName -eq $s.FullName -and $f.Name -in $keep) { continue }
            $rel = [IO.Path]::GetRelativePath($d, $f.FullName).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $backupHash = (Get-FileHash -LiteralPath (Join-Path $copy $rel) -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($hash -ne $backupHash) { throw "backup differs for $name/$rel" }
            $rows.Add("$r`t$($s.Name)`t$rel`t$($f.Length)`t$hash")
            Remove-Item -LiteralPath $f.FullName -Force
        }
        Get-ChildItem $s.FullName -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending |
            Where-Object { -not (Get-ChildItem $_.FullName -Force) } | Remove-Item -Force
    }
    '{0}: {1} files before, {2} after' -f $name, $before, (Get-ChildItem $d -Recurse -File).Count
}
[IO.File]::WriteAllLines($Manifest, $rows, [Text.UTF8Encoding]::new($false))
'removed {0} files, manifest {1}' -f ($rows.Count - 1), $Manifest
