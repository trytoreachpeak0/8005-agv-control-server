<#
.SYNOPSIS
Falsifiability harness for the release-candidate scan gate (map ticket 24, item 4).

.DESCRIPTION
Ticket 21 found that New-WireToGateReleaseCandidate.ps1 wrote its secret-scan and license findings
into JSON and then packaged regardless: a source tree carrying "apiKey": "..." still produced a PASS
release. This harness proves the gate that was added actually fails, and that the version before it
did not -- a green gate is not evidence until it has been shown to turn red.

It never runs a release build. It lifts the shipped rules, the scan and the gate out of the committed
script by AST, so it exercises the code that ships rather than a copy of it, and runs them against
synthetic trees and inventories.

.PARAMETER OutputPath
Where the machine-readable assertion record is written.

.PARAMETER BaselineCommit
The commit whose version of the release script is used as the "before the fix" comparison.
#>
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot 'scan-gate-result.json'),
    [string]$BaselineCommit = '0e4d471'
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$scriptRelativePath = 'scripts/New-WireToGateReleaseCandidate.ps1'
$scriptPath = Join-Path $repository ($scriptRelativePath -replace '/', '\')

function Get-AstFromText([string]$Text, [string]$Label) {
    $parseErrors = $null
    $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$parseErrors)
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) {
        throw "$Label does not parse: $($parseErrors[0].Message)"
    }
    return $ast
}

function Get-FunctionText($Ast, [string]$Name, [string]$Label) {
    $matches = @($Ast.FindAll(
        { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name },
        $true))
    if ($matches.Count -ne 1) {
        throw "Expected exactly one function $Name in $Label, found $($matches.Count)."
    }
    return $matches[0].Extent.Text
}

function Test-FunctionExists($Ast, [string]$Name) {
    return @($Ast.FindAll(
        { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name },
        $true)).Count -gt 0
}

function Get-AssignmentText($Ast, [string]$VariableName, [string]$Label) {
    $matches = @($Ast.FindAll(
        {
            param($node)
            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $node.Left.VariablePath.UserPath -eq $VariableName
        },
        $true))
    if ($matches.Count -ne 1) {
        throw "Expected exactly one assignment to `$$VariableName in $Label, found $($matches.Count)."
    }
    return $matches[0].Extent.Text
}

# --- lift the shipped code out of the working tree and out of the baseline ---
$currentText = Get-Content -Raw -LiteralPath $scriptPath
$currentAst = Get-AstFromText $currentText 'the current release script'
$currentSha256 = (Get-FileHash -LiteralPath $scriptPath -Algorithm SHA256).Hash.ToLowerInvariant()

$baselineText = (& git -C $repository show "${BaselineCommit}:${scriptRelativePath}" | Out-String)
if ($LASTEXITCODE -ne 0) { throw "Unable to read $scriptRelativePath at $BaselineCommit" }
$baselineAst = Get-AstFromText $baselineText "the release script at $BaselineCommit"
$baselineResolvedCommit = (& git -C $repository rev-parse $BaselineCommit).Trim()

$currentSurface = @(
    (Get-AssignmentText $currentAst 'secretRules' 'the current release script'),
    (Get-AssignmentText $currentAst 'secretFileExtensions' 'the current release script'),
    (Get-AssignmentText $currentAst 'secretScanExtensions' 'the current release script'),
    (Get-AssignmentText $currentAst 'unresolvedLicenseAllowlist' 'the current release script'),
    (Get-FunctionText $currentAst 'Invoke-SecretScan' 'the current release script'),
    (Get-FunctionText $currentAst 'Get-NuGetGlobalPackagesRoot' 'the current release script'),
    (Get-FunctionText $currentAst 'Assert-ReleaseScanGate' 'the current release script'),
    (Get-FunctionText $currentAst 'Get-PackageLicense' 'the current release script')
) -join "`n`n"
. ([ScriptBlock]::Create($currentSurface))

# The baseline's Get-PackageLicense is renamed on the way in so both versions can be called side by
# side in one process; nothing else about it is altered.
$baselineLicenseText = (Get-FunctionText $baselineAst 'Get-PackageLicense' "the release script at $BaselineCommit") `
    -replace '^function\s+Get-PackageLicense', 'function Get-PackageLicenseBaseline'
. ([ScriptBlock]::Create($baselineLicenseText))

$assertions = [System.Collections.Generic.List[object]]::new()
function Add-Assertion([int]$Number, [string]$Name, [bool]$Passed, [string]$Observed) {
    $assertions.Add([ordered]@{ number = $Number; assertion = $Name; result = $(if ($Passed) { 'PASS' } else { 'FAIL' }); observed = $Observed })
}

function Invoke-Gate($SecretScan, $Inventories) {
    try {
        $record = Assert-ReleaseScanGate -SecretScan $SecretScan -DependencyInventories $Inventories `
            -UnresolvedLicenseAllowlist $unresolvedLicenseAllowlist
        return [ordered]@{ threw = $false; message = ''; record = $record }
    }
    catch {
        return [ordered]@{ threw = $true; message = $_.Exception.Message; record = $null }
    }
}

function New-Inventory([string]$Component, [object[]]$Packages) {
    return [ordered]@{ component = $Component; packages = @($Packages) }
}
function New-Package([string]$Id, [string]$Version, [string]$License) {
    return [ordered]@{ id = $Id; version = $Version; license = $License }
}

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('w2g-scan-gate-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
try {
    # --- 1-2: a clean tree passes both the scan and the gate ------------------
    $cleanTree = Join-Path $sandbox 'clean'
    New-Item -ItemType Directory -Path $cleanTree -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $cleanTree 'appsettings.json') -Encoding utf8 `
        -Value '{ "wireToGate": { "onboardPort": 58005 } }'
    $cleanScan = [ordered]@{ scopes = @((Invoke-SecretScan $cleanTree 'harness/clean')) }
    Add-Assertion 1 'Clean tree produces no secret-scan finding' `
        ($cleanScan.scopes[0].findingCount -eq 0) "findingCount=$($cleanScan.scopes[0].findingCount)"

    $cleanInventory = New-Inventory 'controlserver' @(
        (New-Package 'Serilog' '4.0.0' 'MIT'),
        (New-Package 'RIoT.Sdk.Core' '0.1.0-controlserver.2' 'UNRESOLVED'),
        (New-Package 'RIoT.Sdk.Facade' '0.1.0-controlserver.2' 'UNRESOLVED'),
        (New-Package 'RIoT.Sdk.Generated' '0.1.0-controlserver.2' 'UNRESOLVED'))
    $cleanGate = Invoke-Gate $cleanScan @($cleanInventory)
    Add-Assertion 2 'Gate passes a clean scan with only allowlisted unresolved licenses' `
        (-not $cleanGate.threw) "threw=$($cleanGate.threw)"

    # --- 3-4: the planted secret is found, and now blocks the release --------
    $plantedTree = Join-Path $sandbox 'planted'
    New-Item -ItemType Directory -Path $plantedTree -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $plantedTree 'appsettings.json') -Encoding utf8 `
        -Value '{ "riot": { "apiKey": "PLANTED-NOT-A-REAL-SECRET-0001" } }'
    $plantedScan = [ordered]@{ scopes = @((Invoke-SecretScan $plantedTree 'harness/planted')) }
    Add-Assertion 3 'Planted "apiKey" literal is detected by the shipped rules' `
        ($plantedScan.scopes[0].findingCount -eq 1 -and $plantedScan.scopes[0].findings[0].rule -eq 'inline-api-key') `
        "findingCount=$($plantedScan.scopes[0].findingCount), rule=$($plantedScan.scopes[0].findings[0].rule)"

    $plantedGate = Invoke-Gate $plantedScan @($cleanInventory)
    Add-Assertion 4 'Gate fails the release on a secret-scan finding' `
        ($plantedGate.threw -and $plantedGate.message -match 'matched rule inline-api-key') `
        "threw=$($plantedGate.threw)"

    # --- 5: the failure message locates the finding without printing it ------
    Add-Assertion 5 'Gate failure message discloses no matched value' `
        ($plantedGate.threw -and $plantedGate.message -notmatch 'PLANTED-NOT-A-REAL-SECRET-0001' -and
            $plantedGate.message -match 'appsettings\.json:1') `
        'message carries path:line:rule only'

    # --- 6: key material blocks the release too ------------------------------
    $keyTree = Join-Path $sandbox 'keymaterial'
    New-Item -ItemType Directory -Path $keyTree -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $keyTree 'server.pfx') -Encoding utf8 -Value 'not-a-real-pfx'
    $keyScan = [ordered]@{ scopes = @((Invoke-SecretScan $keyTree 'harness/keymaterial')) }
    $keyGate = Invoke-Gate $keyScan @($cleanInventory)
    Add-Assertion 6 'Gate fails the release on a key-material file' `
        ($keyGate.threw -and $keyGate.message -match 'key material file server\.pfx') "threw=$($keyGate.threw)"

    # --- 7: an unlisted unresolved license blocks the release ----------------
    $unlistedInventory = New-Inventory 'controlserver' (@($cleanInventory.packages) +
        @((New-Package 'Contoso.Mystery' '2.3.4' 'UNRESOLVED')))
    $unlistedGate = Invoke-Gate $cleanScan @($unlistedInventory)
    Add-Assertion 7 'Gate fails on an unresolved license that is not allowlisted' `
        ($unlistedGate.threw -and $unlistedGate.message -match 'Contoso\.Mystery' -and
            $unlistedGate.message -notmatch 'RIoT\.Sdk') "threw=$($unlistedGate.threw)"

    # --- 8: the baseline did none of this ------------------------------------
    $baselineHasGate = Test-FunctionExists $baselineAst 'Assert-ReleaseScanGate'
    $baselineGatesCounts = $baselineText -match 'totalFindingCount[^\r\n]*throw|throw[^\r\n]*totalFindingCount|unresolvedLicenseCount[^\r\n]*throw'
    Add-Assertion 8 "Baseline $BaselineCommit had no scan gate at all" `
        ((-not $baselineHasGate) -and (-not $baselineGatesCounts)) `
        "Assert-ReleaseScanGate present=$baselineHasGate, throws on the counts=$baselineGatesCounts"

    # --- 9-10: NUGET_PACKAGES is honoured now and was not before -------------
    $fakeNuGetRoot = Join-Path $sandbox 'nuget-global'
    $fakePackageDirectory = Join-Path $fakeNuGetRoot 'contoso.harness\1.0.0'
    New-Item -ItemType Directory -Path $fakePackageDirectory -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $fakePackageDirectory 'contoso.harness.nuspec') -Encoding utf8 -Value @'
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>Contoso.Harness</id>
    <version>1.0.0</version>
    <authors>Harness</authors>
    <projectUrl>https://example.invalid/harness</projectUrl>
    <license type="expression">MIT</license>
  </metadata>
</package>
'@

    $savedNuGetPackages = $env:NUGET_PACKAGES
    try {
        $env:NUGET_PACKAGES = $fakeNuGetRoot
        $currentLicense = Get-PackageLicense 'Contoso.Harness' '1.0.0'
        $baselineLicense = Get-PackageLicenseBaseline 'Contoso.Harness' '1.0.0'
    }
    finally {
        if ($null -eq $savedNuGetPackages) { Remove-Item Env:\NUGET_PACKAGES -ErrorAction SilentlyContinue }
        else { $env:NUGET_PACKAGES = $savedNuGetPackages }
    }
    Add-Assertion 9 'Get-PackageLicense resolves through NUGET_PACKAGES' `
        ($currentLicense.nuspecFound -and $currentLicense.license -eq 'MIT') `
        "nuspecFound=$($currentLicense.nuspecFound), license=$($currentLicense.license)"
    Add-Assertion 10 "Baseline $BaselineCommit returned UNRESOLVED for the same package" `
        ((-not $baselineLicense.nuspecFound) -and $baselineLicense.license -eq 'UNRESOLVED') `
        "nuspecFound=$($baselineLicense.nuspecFound), license=$($baselineLicense.license)"

    # --- 11: the two failure modes compose ----------------------------------
    Add-Assertion 11 'A release that would have shipped with a planted secret and an unlisted license now fails on both' `
        (($r = Invoke-Gate $plantedScan @($unlistedInventory)).threw -and
            $r.message -match 'inline-api-key' -and $r.message -match 'Contoso\.Mystery') `
        "threw=$($r.threw)"

    # --- 12: the release candidate that already exists would pass this gate --
    # The gate is not retroactive -- the 2eeb6f0 package was built before it existed. Running it over
    # that run's own archived inventory answers whether the shipped package would have been blocked.
    $archivedInventoryRoot = Join-Path (Split-Path -Parent $PSScriptRoot) '20260830-isolated-install-2eeb6f0\release-artifacts\inventory'
    $archivedScan = Get-Content -Raw -LiteralPath (Join-Path $archivedInventoryRoot 'secret-scan.json') | ConvertFrom-Json
    $archivedInventories = @('dependencies-controlserver.json', 'dependencies-onboard.json' |
        ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $archivedInventoryRoot $_) | ConvertFrom-Json })
    $archivedGate = Invoke-Gate $archivedScan $archivedInventories
    Add-Assertion 12 'The archived 2eeb6f0 release inventory passes the new gate' `
        (-not $archivedGate.threw) ("threw=$($archivedGate.threw); " +
            "message=$(if ($archivedGate.threw) { $archivedGate.message } else { '(none)' })")
}
finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

$runnerCommit = (& git -C $repository rev-parse HEAD).Trim()
$result = [ordered]@{
    schemaVersion = 1
    ticket = 'wire-to-gate-ai-implementation-kit/24'
    subject = 'New-WireToGateReleaseCandidate.ps1 scan gate and NuGet package root'
    createdAt = [DateTimeOffset]::UtcNow.ToString('O')
    repositoryCommitAtRun = $runnerCommit
    subjectScript = [ordered]@{
        path = $scriptRelativePath
        sha256 = $currentSha256
        surfaceLiftedByAst = @('secretRules', 'secretFileExtensions', 'secretScanExtensions',
            'unresolvedLicenseAllowlist', 'Invoke-SecretScan', 'Get-NuGetGlobalPackagesRoot',
            'Assert-ReleaseScanGate', 'Get-PackageLicense')
    }
    baseline = [ordered]@{ requested = $BaselineCommit; commit = $baselineResolvedCommit }
    releaseBuildPerformed = $false
    assertionCount = $assertions.Count
    failedAssertionCount = @($assertions | Where-Object { $_.result -ne 'PASS' }).Count
    assertions = @($assertions)
}
$result.result = if ($result.failedAssertionCount -eq 0) { 'PASS' } else { 'FAIL' }
[IO.File]::WriteAllText($OutputPath, ($result | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))

$assertions | ForEach-Object { '{0,2}. [{1}] {2} -- {3}' -f $_.number, $_.result, $_.assertion, $_.observed }
Write-Output ''
Write-Output "$($result.result): $($result.assertionCount - $result.failedAssertionCount)/$($result.assertionCount) assertions"
Write-Output "Result: $OutputPath"
if ($result.failedAssertionCount -ne 0) { exit 1 }
