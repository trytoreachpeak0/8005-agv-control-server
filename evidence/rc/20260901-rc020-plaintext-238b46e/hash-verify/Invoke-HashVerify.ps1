<#
.SYNOPSIS
Verifies every line of a release candidate's SHA256SUMS.txt against the bytes on disk, then proves
the same check goes red on a mutated copy.

.DESCRIPTION
The red side never touches the artifact: one file is copied outside the release root, a single byte
is appended to the copy, and the same comparison is run against that copy. A checker that cannot be
made to report MISMATCH is not evidence that the artifact is intact.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseRoot,
    [Parameter(Mandatory)][string]$RedSideWorkspace,
    [Parameter(Mandatory)][string]$OutputJson,
    [string]$RedSideRelativePath = 'controlserver/appsettings.json'
)

$ErrorActionPreference = 'Stop'

$sumsPath = Join-Path $ReleaseRoot 'SHA256SUMS.txt'
$expected = @{}
foreach ($line in [IO.File]::ReadAllLines($sumsPath)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $hash, $relative = $line -split '  ', 2
    $expected[$relative] = $hash
}

$ok = 0
$mismatch = @()
$missing = @()
foreach ($relative in $expected.Keys) {
    $path = Join-Path $ReleaseRoot ($relative -replace '/', '\')
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $missing += $relative; continue }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ceq $expected[$relative]) { $ok++ } else { $mismatch += $relative }
}

# Files present in the release root but absent from SHA256SUMS.txt would be unhashed payload.
$unlisted = @(Get-ChildItem -LiteralPath $ReleaseRoot -File -Recurse |
    ForEach-Object { [IO.Path]::GetRelativePath($ReleaseRoot, $_.FullName).Replace('\', '/') } |
    Where-Object { $_ -ne 'SHA256SUMS.txt' -and -not $expected.ContainsKey($_) })

# --- red side, on a copy outside the release root ---------------------------
if (Test-Path -LiteralPath $RedSideWorkspace) { Remove-Item -LiteralPath $RedSideWorkspace -Recurse -Force }
New-Item -ItemType Directory -Path $RedSideWorkspace -Force | Out-Null
$sourcePath = Join-Path $ReleaseRoot ($RedSideRelativePath -replace '/', '\')
$copyPath = Join-Path $RedSideWorkspace 'mutated-copy.bin'
Copy-Item -LiteralPath $sourcePath -Destination $copyPath -Force
$greenOnCopy = (Get-FileHash -LiteralPath $copyPath -Algorithm SHA256).Hash.ToLowerInvariant()
Add-Content -LiteralPath $copyPath -Value 'x' -NoNewline
$redOnCopy = (Get-FileHash -LiteralPath $copyPath -Algorithm SHA256).Hash.ToLowerInvariant()
$artifactStillMatches = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $expected[$RedSideRelativePath]

$document = [ordered]@{
    schemaVersion = 1
    releaseRoot = $ReleaseRoot
    sumsPath = $sumsPath
    recordedFileCount = $expected.Count
    ok = $ok
    mismatch = @($mismatch)
    missing = @($missing)
    unlistedFilesInReleaseRoot = @($unlisted)
    redSide = [ordered]@{
        method = 'one byte appended to a copy taken outside the release root; the artifact is never modified'
        relativePath = $RedSideRelativePath
        copyPath = $copyPath
        expectedHash = $expected[$RedSideRelativePath]
        copyBeforeMutation = $greenOnCopy
        copyAfterMutation = $redOnCopy
        beforeVerdict = if ($greenOnCopy -ceq $expected[$RedSideRelativePath]) { 'OK' } else { 'MISMATCH' }
        afterVerdict = if ($redOnCopy -ceq $expected[$RedSideRelativePath]) { 'OK' } else { 'MISMATCH' }
        artifactUnchangedAfterRedSide = $artifactStillMatches
    }
}
$document.result = if ($mismatch.Count -eq 0 -and $missing.Count -eq 0 -and $unlisted.Count -eq 0 -and
    $document.redSide.beforeVerdict -eq 'OK' -and $document.redSide.afterVerdict -eq 'MISMATCH' -and
    $artifactStillMatches) { 'PASS' } else { 'FAIL' }

[IO.File]::WriteAllText($OutputJson, ($document | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$document | ConvertTo-Json -Depth 4
