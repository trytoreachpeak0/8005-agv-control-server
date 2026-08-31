<#
.SYNOPSIS
Byte-level symbol probe over two release-candidate packages.

.DESCRIPTION
A managed rebuild produces a fresh MVID every time, so an assembly hash proves nothing about
content. Method, property and type names, however, are written verbatim (UTF-8) into the assembly
metadata string heap. Searching the shipped bytes for a name is therefore a direct content check.

Every .dll and .exe in the component directory is scanned, not just the product assemblies, so an
ABSENT verdict covers the whole shipped package. Bytes are decoded with Latin-1 (a byte-preserving
1:1 map) and matched with an ordinal IndexOf, which is byte equality for the ASCII names probed
here -- UTF-16 user-string literals such as "serverCertificatePath" cannot alias an ASCII match.

Each symbol is classified PRESENT/ABSENT per package. Symbols expected to disappear must be ABSENT
in the new package and PRESENT in the old one; control symbols must be PRESENT in both, which is
what makes an ABSENT a real negative rather than a broken reader.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$NewRoot,
    [Parameter(Mandatory)][string]$OldRoot,
    [Parameter(Mandatory)][string]$OutputJson
)

$ErrorActionPreference = 'Stop'

$probes = @(
    # --- ControlServer -------------------------------------------------------
    @{ component = 'controlserver'; symbol = 'OnboardTlsCertificateLoader'; expectation = 'removed' }
    @{ component = 'controlserver'; symbol = 'CreateTransportStreamAsync'; expectation = 'removed' }
    @{ component = 'controlserver'; symbol = 'AllowInsecureLoopback'; expectation = 'removed' }
    @{ component = 'controlserver'; symbol = 'ServerCertificatePasswordEnvironmentVariable'; expectation = 'removed' }
    @{ component = 'controlserver'; symbol = 'RequireHttps'; expectation = 'removed' }
    @{ component = 'controlserver'; symbol = 'OnboardTransportOptionsValidator'; expectation = 'added' }
    @{ component = 'controlserver'; symbol = 'OnboardTcpServer'; expectation = 'control' }
    @{ component = 'controlserver'; symbol = 'HandleClientAsync'; expectation = 'control' }
    # --- OnboardHmi ----------------------------------------------------------
    @{ component = 'onboard-hmi'; symbol = 'ValidateServerCertificate'; expectation = 'removed' }
    @{ component = 'onboard-hmi'; symbol = 'CreateTrustedHttpClient'; expectation = 'removed' }
    @{ component = 'onboard-hmi'; symbol = 'ServerCertificateSha256'; expectation = 'removed' }
    @{ component = 'onboard-hmi'; symbol = 'UseTls'; expectation = 'removed' }
    @{ component = 'onboard-hmi'; symbol = 'CreateTransportStreamAsync'; expectation = 'removed' }
    @{ component = 'onboard-hmi'; symbol = 'RejectRemovedTransportKeys'; expectation = 'added' }
    @{ component = 'onboard-hmi'; symbol = 'WireToGateSessionClient'; expectation = 'control' }
    @{ component = 'onboard-hmi'; symbol = 'ControlServerVehicleSafetySignalProvider'; expectation = 'control' }
)

# The two products' own assemblies. Everything else in a self-contained package is framework or
# third-party code that neither round of this change touched.
$productAssemblyPattern = '/(ControlServer\.[A-Za-z]+|SQCD\.Agv\.[A-Za-z]+)\.(dll|exe)$'
function Test-ProductAssembly([string]$RelativePath) {
    return $RelativePath -match $productAssemblyPattern
}

$latin1 = [Text.Encoding]::GetEncoding(28591)
$hits = @{}          # "<side>|<component>|<symbol>" -> string[] relative paths
$scannedCounts = @{} # "<side>|<component>" -> int

foreach ($side in @(@{ key = 'new'; root = $NewRoot }, @{ key = 'old'; root = $OldRoot })) {
    foreach ($component in @('controlserver', 'onboard-hmi')) {
        $componentProbes = @($probes | Where-Object { $_.component -eq $component })
        $files = @(Get-ChildItem -LiteralPath (Join-Path $side.root $component) -File -Recurse |
            Where-Object { $_.Extension -in @('.dll', '.exe') } |
            Sort-Object FullName)
        $scannedCounts["$($side.key)|$component"] = $files.Count
        foreach ($file in $files) {
            $text = $latin1.GetString([IO.File]::ReadAllBytes($file.FullName))
            $relative = [IO.Path]::GetRelativePath($side.root, $file.FullName).Replace('\', '/')
            foreach ($probe in $componentProbes) {
                if ($text.IndexOf($probe.symbol, [StringComparison]::Ordinal) -ge 0) {
                    $key = '{0}|{1}|{2}' -f $side.key, $component, $probe.symbol
                    if (-not $hits.ContainsKey($key)) { $hits[$key] = @() }
                    $hits[$key] += $relative
                }
            }
            $text = $null
        }
    }
}

$rows = @()
foreach ($probe in $probes) {
    # @($hits[$missingKey]) is @($null), whose Count is 1 -- that would report every probe PRESENT.
    $newKey = 'new|{0}|{1}' -f $probe.component, $probe.symbol
    $oldKey = 'old|{0}|{1}' -f $probe.component, $probe.symbol
    $newHits = if ($hits.ContainsKey($newKey)) { @($hits[$newKey]) } else { @() }
    $oldHits = if ($hits.ContainsKey($oldKey)) { @($hits[$oldKey]) } else { @() }
    $newProductHits = @($newHits | Where-Object { Test-ProductAssembly $_ })
    $oldProductHits = @($oldHits | Where-Object { Test-ProductAssembly $_ })
    $newState = if ($newProductHits.Count -gt 0) { 'PRESENT' } else { 'ABSENT' }
    $oldState = if ($oldProductHits.Count -gt 0) { 'PRESENT' } else { 'ABSENT' }
    $expectedNew = switch ($probe.expectation) { 'removed' { 'ABSENT' } 'added' { 'PRESENT' } 'control' { 'PRESENT' } }
    $expectedOld = switch ($probe.expectation) { 'removed' { 'PRESENT' } 'added' { 'ABSENT' } 'control' { 'PRESENT' } }
    $rows += [ordered]@{
        component = $probe.component
        symbol = $probe.symbol
        expectation = $probe.expectation
        expectedOldState = $expectedOld
        expectedNewState = $expectedNew
        # Verdict is judged over the product assemblies only. A framework assembly that ships in
        # both packages can carry the same name for its own unrelated API (ASP.NET Core's
        # RequireHttpsAttribute is one), and that is not this product's code.
        oldState = $oldState
        newState = $newState
        oldProductFiles = $oldProductHits
        newProductFiles = $newProductHits
        oldPackageState = if ($oldHits.Count -gt 0) { 'PRESENT' } else { 'ABSENT' }
        newPackageState = if ($newHits.Count -gt 0) { 'PRESENT' } else { 'ABSENT' }
        oldNonProductFiles = @($oldHits | Where-Object { -not (Test-ProductAssembly $_) })
        newNonProductFiles = @($newHits | Where-Object { -not (Test-ProductAssembly $_) })
        verdict = if ($newState -eq $expectedNew -and $oldState -eq $expectedOld) { 'AS_EXPECTED' } else { 'UNEXPECTED' }
    }
}

$document = [ordered]@{
    schemaVersion = 1
    newRoot = $NewRoot
    oldRoot = $OldRoot
    scannedExtensions = @('.dll', '.exe')
    scannedFileCounts = $scannedCounts
    matching = 'raw bytes decoded Latin-1, ordinal IndexOf, case-sensitive'
    probes = @($rows)
    unexpectedCount = @($rows | Where-Object { $_.verdict -eq 'UNEXPECTED' }).Count
}
[IO.File]::WriteAllText($OutputJson, ($document | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))

$rows | ForEach-Object { [pscustomobject]$_ } |
    Format-Table component, symbol, expectation, oldState, newState, verdict -AutoSize
"scanned files: $(($scannedCounts.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' ')"
"unexpected = $($document.unexpectedCount)"
