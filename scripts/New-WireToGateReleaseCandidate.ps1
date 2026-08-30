<#
.SYNOPSIS
Assembles the WIRE_TO_GATE MVP release candidate from the two product repositories.

.DESCRIPTION
Builds the ControlServer deployment package from this repository's HEAD and the OnboardHmi
executable from a throwaway clone of the read-only onboard repository, then derives the joint
release identity, the dependency and license inventory, the secret scan and the artifact hashes.

The onboard repository is read-only for agents, so this script never writes into an existing
onboard working tree: it clones the requested commit into the output root and builds there.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,
    [Parameter(Mandatory = $true)]
    [string]$OnboardCommit,
    [string]$OnboardRepositoryUrl = 'https://github.com/trytoreachpeak0/8005-agv-onboard-hmi.git',
    [string]$OnboardBranch = 'OnboardHmi_MVP',
    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OnboardSdkVersion = '8.0.424'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if ($env:WIRE_TO_GATE_DOTNET_EXE) { $env:WIRE_TO_GATE_DOTNET_EXE } else { 'dotnet' }
$resolvedOutput = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputRoot)
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')

if ($OnboardCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'OnboardCommit must be the full 40-character commit hash.'
}
if (Test-Path -LiteralPath $resolvedOutput) {
    throw "OutputRoot already exists; use a new empty destination: $resolvedOutput"
}
if (Test-Path -LiteralPath "$resolvedOutput-onboard-src") {
    throw "The onboard build directory already exists; use a new destination: $resolvedOutput-onboard-src"
}

function Invoke-Native([string]$Executable, [string[]]$Arguments, [string]$FailureMessage) {
    $output = @(& $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage (exit $LASTEXITCODE): $($output | Select-Object -Last 20 | Join-String -Separator '; ')"
    }
    return $output
}

function Get-BuildWarningCount([string[]]$Output) {
    $warnings = @($Output | Where-Object { $_ -match '\swarning\s[A-Za-z]+[0-9]+:' })
    return $warnings.Count
}

function Get-FileInventory([string]$Path) {
    $inventory = @(Get-ChildItem -LiteralPath $Path -File -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                path = [IO.Path]::GetRelativePath($Path, $_.FullName).Replace('\', '/')
                length = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        })
    return $inventory
}

function Get-PackageLicense([string]$Id, [string]$Version) {
    $record = [ordered]@{
        id = $Id
        version = $Version
        license = 'UNRESOLVED'
        licenseKind = 'UNRESOLVED'
        projectUrl = ''
        authors = ''
        nuspecFound = $false
    }
    $packageRoot = Join-Path $env:USERPROFILE ('.nuget\packages\{0}\{1}' -f $Id.ToLowerInvariant(), $Version.ToLowerInvariant())
    $nuspecPath = Join-Path $packageRoot ('{0}.nuspec' -f $Id.ToLowerInvariant())
    if (-not (Test-Path -LiteralPath $nuspecPath -PathType Leaf)) { return $record }
    $record.nuspecFound = $true
    [xml]$nuspec = Get-Content -Raw -LiteralPath $nuspecPath
    $metadata = $nuspec.package.metadata
    $record.projectUrl = [string]$metadata.projectUrl
    $record.authors = [string]$metadata.authors
    $licenseNode = $metadata.SelectSingleNode('*[local-name()="license"]')
    if ($licenseNode) {
        $record.licenseKind = [string]$licenseNode.GetAttribute('type')
        $record.license = [string]$licenseNode.InnerText
    }
    elseif (-not [string]::IsNullOrWhiteSpace([string]$metadata.licenseUrl)) {
        $record.licenseKind = 'url'
        $record.license = [string]$metadata.licenseUrl
    }
    return $record
}

function Get-DependencyInventory([string]$ProjectOrSolution, [string]$Label) {
    $listOutput = @(Invoke-Native $dotnet @('list', $ProjectOrSolution, 'package', '--include-transitive', '--format', 'json') `
        "Unable to list packages for $Label")
    $document = ($listOutput -join "`n") | ConvertFrom-Json
    $packages = @{}
    foreach ($project in $document.projects) {
        foreach ($framework in $project.frameworks) {
            foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                if ($null -eq $package) { continue }
                $key = '{0}|{1}' -f $package.id, $package.resolvedVersion
                if (-not $packages.ContainsKey($key)) {
                    $packages[$key] = [ordered]@{ id = $package.id; version = $package.resolvedVersion }
                }
            }
        }
    }
    $records = @($packages.Values |
        Sort-Object { $_.id }, { $_.version } |
        ForEach-Object { Get-PackageLicense $_.id $_.version })
    return [ordered]@{
        component = $Label
        target = $ProjectOrSolution
        packageCount = $records.Count
        unresolvedLicenseCount = @($records | Where-Object { $_.license -eq 'UNRESOLVED' }).Count
        packages = $records
    }
}

$secretRules = @(
    [ordered]@{ name = 'private-key-block'; pattern = '-----BEGIN [A-Z ]*PRIVATE KEY-----' },
    [ordered]@{ name = 'pkcs12-password-literal'; pattern = '(?i)"(certificatePassword|pfxPassword)"\s*:\s*"[^"]+"' },
    [ordered]@{ name = 'inline-api-key'; pattern = '(?i)"(callApiKey|apiKey|sharedSecret|credential|password)"\s*:\s*"[^"]{6,}"' },
    [ordered]@{ name = 'bearer-literal'; pattern = '(?i)authorization\s*[:=]\s*"?bearer\s+[A-Za-z0-9._\-]{12,}' },
    [ordered]@{ name = 'aws-access-key-id'; pattern = 'AKIA[0-9A-Z]{16}' },
    [ordered]@{ name = 'github-token'; pattern = 'gh[pousr]_[A-Za-z0-9]{16,}' }
)
$secretFileExtensions = @('.pfx', '.p12', '.pem', '.key', '.jks', '.keystore')
$secretScanExtensions = @('.json', '.config', '.xml', '.ps1', '.psm1', '.cmd', '.bat', '.md', '.txt', '.yml', '.yaml', '.cs', '.props', '.targets', '.ndjson')

function Invoke-SecretScan([string]$Path, [string]$Label) {
    $findings = @()
    $keyMaterialFiles = @()
    foreach ($file in Get-ChildItem -LiteralPath $Path -File -Recurse) {
        $extension = $file.Extension.ToLowerInvariant()
        if ($secretFileExtensions -contains $extension) {
            $keyMaterialFiles += [IO.Path]::GetRelativePath($Path, $file.FullName).Replace('\', '/')
            continue
        }
        if (-not ($secretScanExtensions -contains $extension)) { continue }
        if ($file.Length -gt 4MB) { continue }
        $lineNumber = 0
        foreach ($line in [IO.File]::ReadLines($file.FullName)) {
            $lineNumber++
            foreach ($rule in $secretRules) {
                if ($line -match $rule.pattern) {
                    $findings += [ordered]@{
                        scope = $Label
                        path = [IO.Path]::GetRelativePath($Path, $file.FullName).Replace('\', '/')
                        line = $lineNumber
                        rule = $rule.name
                    }
                }
            }
        }
    }
    return [ordered]@{
        scope = $Label
        scannedRoot = $Path
        ruleNames = @($secretRules | ForEach-Object { $_.name })
        keyMaterialFileExtensions = $secretFileExtensions
        keyMaterialFiles = @($keyMaterialFiles)
        findings = @($findings)
        findingCount = @($findings).Count
        matchedValuesDisclosed = $false
    }
}

# --- ControlServer identity -------------------------------------------------
$controlServerStatus = @(Invoke-Native 'git' @('-C', $root, 'status', '--porcelain') 'Unable to inspect the ControlServer repository')
if ($controlServerStatus.Count -gt 0) {
    throw 'The ControlServer repository must be clean before assembling a release candidate.'
}
$controlServerCommit = @(Invoke-Native 'git' @('-C', $root, 'rev-parse', 'HEAD') 'Unable to resolve the ControlServer commit')[0].Trim()
$controlServerBranch = @(Invoke-Native 'git' @('-C', $root, 'rev-parse', '--abbrev-ref', 'HEAD') 'Unable to resolve the ControlServer branch')[0].Trim()

New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
$controlServerPackage = Join-Path $resolvedOutput 'controlserver'
$onboardPackage = Join-Path $resolvedOutput 'onboard-hmi'
$onboardSource = "$resolvedOutput-onboard-src"
$inventoryDirectory = Join-Path $resolvedOutput 'inventory'
$releaseScriptDirectory = Join-Path $resolvedOutput 'scripts'
New-Item -ItemType Directory -Path $inventoryDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $releaseScriptDirectory -Force | Out-Null

# --- ControlServer package --------------------------------------------------
$publishScript = Join-Path $PSScriptRoot 'Publish-ControlServer.ps1'
$controlServerOutput = @(Invoke-Native 'pwsh' @(
    '-NoProfile', '-File', $publishScript,
    '-OutputPath', $controlServerPackage,
    '-RuntimeIdentifier', $RuntimeIdentifier) 'ControlServer publish failed')
$controlServerWarnings = Get-BuildWarningCount $controlServerOutput
if ($controlServerWarnings -ne 0) {
    throw "The ControlServer publish reported $controlServerWarnings warnings."
}

# --- OnboardHmi package from a throwaway clone ------------------------------
Invoke-Native 'git' @('clone', '--quiet', '--branch', $OnboardBranch, $OnboardRepositoryUrl, $onboardSource) `
    'Unable to clone the onboard repository' | Out-Null
Invoke-Native 'git' @('-C', $onboardSource, 'checkout', '--quiet', '--detach', $OnboardCommit) `
    'Unable to check out the requested onboard commit' | Out-Null
$onboardResolved = @(Invoke-Native 'git' @('-C', $onboardSource, 'rev-parse', 'HEAD') 'Unable to resolve the onboard commit')[0].Trim()
if ($onboardResolved -ne $OnboardCommit) {
    throw "The onboard clone resolved to $onboardResolved instead of $OnboardCommit."
}
$onboardStatus = @(Invoke-Native 'git' @('-C', $onboardSource, 'status', '--porcelain') 'Unable to inspect the onboard clone')
if ($onboardStatus.Count -gt 0) {
    throw 'The onboard clone is not clean immediately after checkout.'
}
$onboardSdkPinned = -not (Test-Path -LiteralPath (Join-Path $onboardSource 'global.json') -PathType Leaf)
if ($onboardSdkPinned) {
    $globalJson = [ordered]@{
        sdk = [ordered]@{ version = $OnboardSdkVersion; rollForward = 'disable'; allowPrerelease = $false }
    }
    [IO.File]::WriteAllText(
        (Join-Path $onboardSource 'global.json'),
        ($globalJson | ConvertTo-Json -Depth 4),
        [Text.UTF8Encoding]::new($false))
}
$onboardProject = Join-Path $onboardSource 'src\SQCD.Agv.Wpf\SQCD.Agv.Wpf.csproj'
$onboardOutput = @(Invoke-Native $dotnet @(
    'publish', $onboardProject, '--configuration', 'Release',
    '--runtime', $RuntimeIdentifier, '--self-contained', 'true',
    '--output', $onboardPackage) 'OnboardHmi publish failed')
$onboardWarnings = Get-BuildWarningCount $onboardOutput
if ($onboardWarnings -ne 0) {
    throw "The OnboardHmi publish reported $onboardWarnings warnings."
}

# The onboard application reads only appsettings.json from its own directory, so the shipped
# production configuration is a full replacement rather than an overlay. Derive it from the
# repository's own example and stamp the commit this package was actually built from, because
# the development default carries an older literal.
$onboardDefaultSettings = Get-Content -Raw -LiteralPath (Join-Path $onboardPackage 'appsettings.json') | ConvertFrom-Json
$onboardDeclaredCommit = [string]$onboardDefaultSettings.wireToGate.onboardBuildCommit
$onboardExamplePath = Join-Path $onboardSource 'src\SQCD.Agv.Wpf\appsettings.Production.example.json'
if (-not (Test-Path -LiteralPath $onboardExamplePath -PathType Leaf)) {
    throw 'The onboard repository does not contain appsettings.Production.example.json.'
}
$onboardTemplate = Get-Content -Raw -LiteralPath $onboardExamplePath
if ($onboardTemplate -notmatch 'REPLACE_WITH_40_CHARACTER_GIT_COMMIT') {
    throw 'The onboard production example no longer carries the build-commit placeholder.'
}
$onboardTemplate = $onboardTemplate.Replace('REPLACE_WITH_40_CHARACTER_GIT_COMMIT', $onboardResolved)
$onboardTemplatePath = Join-Path $onboardPackage 'appsettings.Production.template.json'
[IO.File]::WriteAllText($onboardTemplatePath, $onboardTemplate, [Text.UTF8Encoding]::new($false))
$onboardRemainingPlaceholders = @([regex]::Matches($onboardTemplate, 'REPLACE_[A-Z0-9_]+') |
    ForEach-Object { $_.Value } | Sort-Object -Unique)

# --- Protocol identity, derived from the published ControlServer settings ----
$publishedSettingsPath = Join-Path $controlServerPackage 'appsettings.json'
if (-not (Test-Path -LiteralPath $publishedSettingsPath -PathType Leaf)) {
    throw 'The published ControlServer package does not contain appsettings.json.'
}
$publishedSettings = Get-Content -Raw -LiteralPath $publishedSettingsPath | ConvertFrom-Json
$protocol = $publishedSettings.ProtocolCandidate
if ($null -eq $protocol -or $protocol.approvalStatus -ne 'APPROVED_RELEASE') {
    throw 'The published ControlServer package is not bound to an approved protocol release.'
}

# --- Deliverable documents and operator scripts -----------------------------
foreach ($name in @('Install-ControlServerLocal.ps1', 'Uninstall-ControlServerLocal.ps1', 'Publish-ControlServer.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $releaseScriptDirectory $name) -Force
}
$releaseDocument = Join-Path $root 'docs\RELEASE-CANDIDATE.md'
if (-not (Test-Path -LiteralPath $releaseDocument -PathType Leaf)) {
    throw 'docs/RELEASE-CANDIDATE.md is missing; the release candidate has no operator entry point.'
}
Copy-Item -LiteralPath $releaseDocument -Destination (Join-Path $resolvedOutput 'RELEASE-CANDIDATE.md') -Force

# --- Inventories ------------------------------------------------------------
$controlServerDependencies = Get-DependencyInventory (Join-Path $root 'src\ControlServer.Host\ControlServer.Host.csproj') 'controlserver'
$onboardDependencies = Get-DependencyInventory $onboardProject 'onboard-hmi'
[IO.File]::WriteAllText(
    (Join-Path $inventoryDirectory 'dependencies-controlserver.json'),
    ($controlServerDependencies | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText(
    (Join-Path $inventoryDirectory 'dependencies-onboard.json'),
    ($onboardDependencies | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))

$secretScan = [ordered]@{
    schemaVersion = 1
    scopes = @(
        (Invoke-SecretScan $controlServerPackage 'release/controlserver'),
        (Invoke-SecretScan $onboardPackage 'release/onboard-hmi'),
        (Invoke-SecretScan $releaseScriptDirectory 'release/scripts'),
        (Invoke-SecretScan (Join-Path $root 'src') 'source/controlserver'),
        (Invoke-SecretScan (Join-Path $onboardSource 'src') 'source/onboard-hmi')
    )
}
$secretScan.totalFindingCount = @($secretScan.scopes | ForEach-Object { $_.findingCount } | Measure-Object -Sum).Sum
$secretScan.totalKeyMaterialFileCount = @($secretScan.scopes | ForEach-Object { $_.keyMaterialFiles.Count } | Measure-Object -Sum).Sum
[IO.File]::WriteAllText(
    (Join-Path $inventoryDirectory 'secret-scan.json'),
    ($secretScan | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))

# --- Joint release manifest -------------------------------------------------
$controlServerFiles = @(Get-FileInventory $controlServerPackage)
$onboardFiles = @(Get-FileInventory $onboardPackage)
$releaseManifest = [ordered]@{
    schemaVersion = 1
    product = 'WIRE_TO_GATE MVP Release Candidate'
    runId = $runId
    createdAt = [DateTimeOffset]::UtcNow.ToString('O')
    runtimeIdentifier = $RuntimeIdentifier
    components = [ordered]@{
        controlServer = [ordered]@{
            repository = 'https://github.com/trytoreachpeak0/8005-agv-control-server.git'
            branch = $controlServerBranch
            commit = $controlServerCommit
            worktreeCleanAtStart = $true
            selfContained = $true
            buildWarnings = $controlServerWarnings
            relativePath = 'controlserver'
            entryPoint = 'controlserver/ControlServer.Host.exe'
            fileCount = $controlServerFiles.Count
            files = $controlServerFiles
        }
        onboardHmi = [ordered]@{
            repository = $OnboardRepositoryUrl
            branch = $OnboardBranch
            commit = $onboardResolved
            agentWriteAccess = 'read-only'
            builtFromThrowawayClone = $true
            sdkPinnedByReleaseScript = $onboardSdkPinned
            sdkVersion = $OnboardSdkVersion
            selfContained = $true
            buildWarnings = $onboardWarnings
            relativePath = 'onboard-hmi'
            entryPoint = 'onboard-hmi/SQCD.Agv.Wpf.exe'
            configuration = [ordered]@{
                loadedFile = 'appsettings.json'
                overlaySupported = $false
                productionTemplate = 'onboard-hmi/appsettings.Production.template.json'
                declaredBuildCommitInDefaultSettings = $onboardDeclaredCommit
                declaredBuildCommitMatchesBuild = ($onboardDeclaredCommit -eq $onboardResolved)
                remainingSitePlaceholders = @($onboardRemainingPlaceholders)
                logDirectory = 'onboard-hmi/logs'
            }
            fileCount = $onboardFiles.Count
            files = $onboardFiles
        }
        protocol = [ordered]@{
            repository = 'https://github.com/trytoreachpeak0/8005-agv-protocol.git'
            tag = $protocol.tag
            commit = $protocol.repositoryCommit
            releaseVersion = $protocol.releaseVersion
            manifestSha256 = $protocol.manifestSha256
            schemaBundleSha256 = $protocol.schemaBundleSha256
            vectorsSha256 = $protocol.vectorsSha256
            profileId = $protocol.profileId
            protocolVersion = $protocol.protocolVersion
            approvalStatus = $protocol.approvalStatus
            identitySource = 'controlserver/appsettings.json:ProtocolCandidate'
        }
    }
    inventory = [ordered]@{
        dependencies = @('inventory/dependencies-controlserver.json', 'inventory/dependencies-onboard.json')
        controlServerPackageCount = $controlServerDependencies.packageCount
        onboardPackageCount = $onboardDependencies.packageCount
        unresolvedLicenseCount = $controlServerDependencies.unresolvedLicenseCount + $onboardDependencies.unresolvedLicenseCount
        secretScan = 'inventory/secret-scan.json'
        secretScanFindingCount = $secretScan.totalFindingCount
        secretScanKeyMaterialFileCount = $secretScan.totalKeyMaterialFileCount
    }
    operatorEntryPoints = [ordered]@{
        document = 'RELEASE-CANDIDATE.md'
        install = 'scripts/Install-ControlServerLocal.ps1'
        uninstall = 'scripts/Uninstall-ControlServerLocal.ps1'
        rebuild = 'scripts/Publish-ControlServer.ps1'
    }
}
$manifestPath = Join-Path $resolvedOutput 'release-manifest.json'
[IO.File]::WriteAllText($manifestPath, ($releaseManifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

$sumsBuilder = [Text.StringBuilder]::new()
foreach ($file in Get-FileInventory $resolvedOutput) {
    if ($file.path -eq 'SHA256SUMS.txt') { continue }
    [void]$sumsBuilder.AppendLine(('{0}  {1}' -f $file.sha256, $file.path))
}
$sumsPath = Join-Path $resolvedOutput 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sumsPath, $sumsBuilder.ToString(), [Text.UTF8Encoding]::new($false))

Write-Output "Release candidate: $resolvedOutput"
Write-Output "ControlServer commit: $controlServerCommit"
Write-Output "OnboardHmi commit: $onboardResolved"
Write-Output "Protocol: $($protocol.tag) $($protocol.repositoryCommit)"
Write-Output "Release manifest SHA-256: $((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant())"
Write-Output "SHA256SUMS SHA-256: $((Get-FileHash -LiteralPath $sumsPath -Algorithm SHA256).Hash.ToLowerInvariant())"
Write-Output "Secret scan findings: $($secretScan.totalFindingCount); key material files: $($secretScan.totalKeyMaterialFileCount)"
