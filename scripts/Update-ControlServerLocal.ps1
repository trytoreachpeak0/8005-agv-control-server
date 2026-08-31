[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [Parameter(Mandatory = $true)]
    [string]$ResultPath,
    [string]$DiagnosticPath,
    [switch]$VerifySafetyProjectionReadOnly
)

$ErrorActionPreference = 'Stop'
$serviceName = '8005 AGV ControlServer'
$installPath = 'C:\Program Files\8005 AGV\ControlServer'
$dataRoot = 'C:\ProgramData\8005\ControlServer'
$backupRoot = 'C:\ProgramData\8005\ControlServer-backups'
$resolvedPackage = [IO.Path]::GetFullPath($PackagePath)
$resolvedResult = [IO.Path]::GetFullPath($ResultPath)
$resolvedDiagnostic = if ([string]::IsNullOrWhiteSpace($DiagnosticPath)) { $null } else { [IO.Path]::GetFullPath($DiagnosticPath) }
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$backupPath = Join-Path $backupRoot "$runId-upgrade"
$stagingPath = "C:\Program Files\8005 AGV\ControlServer.staging.$runId"
$certificateDirectory = Join-Path $dataRoot 'certs'
$certificatePasswordVariable = 'CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD'
$oldCertificatePassword = [Environment]::GetEnvironmentVariable($certificatePasswordVariable, 'Machine')
$replacementInstalled = $false
$serviceStopped = $false
$backupComplete = $false
$certificatePasswordRemoved = $false
$certificateDirectoryRemoved = $false
# Replaced from the retained configuration once it has been migrated; the health endpoint is wherever
# that file binds Kestrel, which is no longer necessarily loopback.
$checkHost = '127.0.0.1'
$checkOrigin = 'http://127.0.0.1:58007'

function Write-Diagnostic([string]$Message) {
    if ([string]::IsNullOrWhiteSpace($resolvedDiagnostic)) { return }
    $directory = Split-Path -Parent $resolvedDiagnostic
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $line = '{0} {1}{2}' -f [DateTimeOffset]::UtcNow.ToString('O'), $Message, [Environment]::NewLine
    [IO.File]::AppendAllText($resolvedDiagnostic, $line, [Text.UTF8Encoding]::new($false))
}

function Assert-Administrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Update-ControlServerLocal.ps1 must run elevated.'
    }
}

function Set-RestrictedDirectoryAcl([string]$Path) {
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sidValue in @('S-1-5-18', 'S-1-5-32-544')) {
        $sid = [Security.Principal.SecurityIdentifier]::new($sidValue)
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $security
}

function Test-PackageManifest([string]$Path) {
    $manifestPath = Join-Path $Path 'deployment-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'Package deployment-manifest.json is missing.'
    }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne '8005 AGV ControlServer') {
        throw 'Package manifest identity is invalid.'
    }
    $prefix = $Path.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($file in $manifest.files) {
        $candidate = [IO.Path]::GetFullPath((Join-Path $Path $file.path))
        if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Package manifest contains an escaping path: $($file.path)"
        }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "Package file is missing: $($file.path)"
        }
        $actual = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $file.sha256) { throw "Package file hash mismatch: $($file.path)" }
    }
    return $manifest
}

function Wait-ServiceState([string]$ExpectedStatus, [int]$Seconds = 30) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        $service = Get-Service -Name $serviceName -ErrorAction Stop
        if ($service.Status.ToString() -eq $ExpectedStatus) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Service did not reach $ExpectedStatus within $Seconds seconds."
}

function Invoke-JsonGet([string]$Uri) {
    $body = @(& "$env:SystemRoot\System32\curl.exe" --fail --silent --show-error `
        --noproxy $checkHost --max-time 10 $Uri 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "HTTP GET failed with exit code $LASTEXITCODE`: $($body -join ' ')" }
    return $body | ConvertFrom-Json
}

function Invoke-SafetyProjection {
    $credential = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CREDENTIAL', 'Machine')
    if ([string]::IsNullOrWhiteSpace($credential)) { throw 'Machine Onboard credential is missing.' }
    Add-Type -AssemblyName System.Net.Http
    $client = [System.Net.Http.HttpClient]::new()
    $client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $credential)
    try {
        $response = $client.GetAsync("$checkOrigin/api/onboard/v1/vehicle-safety").GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "Safety projection returned HTTP $([int]$response.StatusCode)."
        }
        $result = $body | ConvertFrom-Json
        if ($result.motionState -notin @('STOPPED', 'MOVING', 'UNKNOWN')) {
            throw 'Safety projection returned an invalid motion state.'
        }
        return $result
    }
    finally {
        $client.Dispose()
    }
}

<#
.SYNOPSIS
Rewrites the retained production configuration into the plaintext key set.
.DESCRIPTION
The upgrade carries the installed appsettings.Production.json forward untouched, so an installation
made by a TLS-era installer would hand the new binary keys it now rejects on purpose -- the upgrade
would fail at start and roll straight back. Stripping the removed keys here is what makes the
upgrade path survive the transport change; the values are gone from the product, not merely ignored.
#>
function Convert-RetainedConfigurationToPlaintext([string]$Path) {
    $configuration = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    $removedKeys = [Collections.Generic.List[string]]::new()
    foreach ($removal in @(
        @{ Section = 'OnboardTransport'; Key = 'serverCertificatePath' },
        @{ Section = 'OnboardTransport'; Key = 'serverCertificatePasswordEnvironmentVariable' },
        @{ Section = 'OnboardTransport'; Key = 'allowInsecureLoopback' },
        @{ Section = 'OnboardSafetyProjection'; Key = 'requireHttps' })) {
        $section = $configuration.$($removal.Section)
        if ($null -ne $section -and $section.PSObject.Properties.Name -contains $removal.Key) {
            $section.PSObject.Properties.Remove($removal.Key)
            $removedKeys.Add("$($removal.Section):$($removal.Key)")
        }
    }
    $healthUrlRewritten = $false
    if ($null -ne $configuration.Health -and $configuration.Health.url -is [string] -and
        $configuration.Health.url.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
        $configuration.Health.url = 'http://' + $configuration.Health.url.Substring('https://'.Length)
        $healthUrlRewritten = $true
    }
    [IO.File]::WriteAllText(
        $Path,
        ($configuration | ConvertTo-Json -Depth 10),
        [Text.UTF8Encoding]::new($false))
    return [ordered]@{
        removedKeys = @($removedKeys)
        healthUrlRewritten = $healthUrlRewritten
        healthUrl = $configuration.Health.url
    }
}

Assert-Administrator
foreach ($path in @($resolvedResult, $resolvedDiagnostic, $stagingPath, $backupPath)) {
    if (-not [string]::IsNullOrWhiteSpace($path) -and (Test-Path -LiteralPath $path)) {
        throw "Output already exists: $path"
    }
}
if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { throw 'ControlServer service is missing.' }
if (-not (Test-Path -LiteralPath $installPath -PathType Container)) { throw 'ControlServer install path is missing.' }
if (-not (Test-Path -LiteralPath $dataRoot -PathType Container)) { throw 'ControlServer data root is missing.' }
$manifest = Test-PackageManifest $resolvedPackage
$productionConfiguration = Join-Path $installPath 'appsettings.Production.json'
if (-not (Test-Path -LiteralPath $productionConfiguration -PathType Leaf)) {
    throw 'Installed production configuration is missing.'
}
$configuration = Get-Content -Raw -LiteralPath $productionConfiguration | ConvertFrom-Json
if ($configuration.JourneyRuntime.enabled -ne $false) { throw 'JourneyRuntime must remain disabled during upgrade.' }
Write-Diagnostic 'preflight-complete'

try {
    Stop-Service -Name $serviceName
    Wait-ServiceState 'Stopped'
    $serviceStopped = $true
    Write-Diagnostic 'service-stopped'

    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
    Set-RestrictedDirectoryAcl $backupPath
    $installBackupPath = New-Item -ItemType Directory -Path (Join-Path $backupPath 'install')
    $dataBackupPath = New-Item -ItemType Directory -Path (Join-Path $backupPath 'data-root')
    foreach ($item in Get-ChildItem -LiteralPath $installPath -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $installBackupPath.FullName -Recurse -Force
    }
    foreach ($item in Get-ChildItem -LiteralPath $dataRoot -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $dataBackupPath.FullName -Recurse -Force
    }
    $backupComplete = $true
    Write-Diagnostic 'backup-complete'

    New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $resolvedPackage -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $stagingPath -Recurse -Force
    }
    Copy-Item -LiteralPath $productionConfiguration -Destination $stagingPath -Force
    $configurationMigration = Convert-RetainedConfigurationToPlaintext (Join-Path $stagingPath 'appsettings.Production.json')
    $healthBinding = [Uri]$configurationMigration.healthUrl
    $checkHost = if ($healthBinding.Host -in @('0.0.0.0', '*', '+', '::', '[::]')) {
        '127.0.0.1'
    } else {
        $healthBinding.Host
    }
    $checkOrigin = "http://${checkHost}:$($healthBinding.Port)"
    Set-RestrictedDirectoryAcl $stagingPath
    Write-Diagnostic ("staging-complete removedKeys={0} healthUrlRewritten={1}" -f
        $configurationMigration.removedKeys.Count, $configurationMigration.healthUrlRewritten)

    # The certificate directory is inside the data root, so the backup above already holds it and the
    # rollback path restores it with everything else.
    if (Test-Path -LiteralPath $certificateDirectory) {
        Remove-Item -LiteralPath $certificateDirectory -Recurse -Force
        $certificateDirectoryRemoved = -not (Test-Path -LiteralPath $certificateDirectory)
        if (-not $certificateDirectoryRemoved) {
            throw "The certificate directory was not removed: $certificateDirectory"
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($oldCertificatePassword)) {
        [Environment]::SetEnvironmentVariable($certificatePasswordVariable, $null, 'Machine')
        $certificatePasswordRemoved = [string]::IsNullOrWhiteSpace(
            [Environment]::GetEnvironmentVariable($certificatePasswordVariable, 'Machine'))
        if (-not $certificatePasswordRemoved) {
            throw "The machine-scope $certificatePasswordVariable was not removed."
        }
    }
    Write-Diagnostic ("certificate-cleanup-complete directory={0} password={1}" -f
        $certificateDirectoryRemoved, $certificatePasswordRemoved)

    Remove-Item -LiteralPath $installPath -Recurse -Force
    Move-Item -LiteralPath $stagingPath -Destination $installPath
    $replacementInstalled = $true
    Write-Diagnostic 'replacement-installed'

    Start-Service -Name $serviceName
    Wait-ServiceState 'Running'
    $live = Invoke-JsonGet "$checkOrigin/health/live"
    if ($live.status -ne 'live') { throw 'Live endpoint returned an unexpected response.' }
    $version = Invoke-JsonGet "$checkOrigin/version"
    $safety = if ($VerifySafetyProjectionReadOnly) { Invoke-SafetyProjection } else { $null }
    Restart-Service -Name $serviceName -Force
    Wait-ServiceState 'Running'
    $liveAfterRestart = Invoke-JsonGet "$checkOrigin/health/live"
    if ($liveAfterRestart.status -ne 'live') { throw 'Post-restart live endpoint returned an unexpected response.' }
    if ($VerifySafetyProjectionReadOnly) { $safety = Invoke-SafetyProjection }
    Write-Diagnostic 'lifecycle-and-readonly-checks-complete'

    $resultDirectory = Split-Path -Parent $resolvedResult
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    $result = [ordered]@{
        schemaVersion = 1
        result = 'PASS'
        runId = $runId
        sourceCommit = $manifest.sourceCommit
        packageManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $resolvedPackage 'deployment-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        configurationSha256 = (Get-FileHash -LiteralPath (Join-Path $installPath 'appsettings.Production.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        serviceStatus = (Get-Service -Name $serviceName).Status.ToString()
        healthEndpoint = $checkOrigin
        transport = 'plaintext'
        certificateRemoval = [ordered]@{
            removedConfigurationKeys = @($configurationMigration.removedKeys)
            healthUrlRewrittenToHttp = $configurationMigration.healthUrlRewritten
            certificateDirectory = $certificateDirectory
            certificateDirectoryRemoved = $certificateDirectoryRemoved
            certificateDirectoryPresent = (Test-Path -LiteralPath $certificateDirectory)
            machineCertificatePasswordRemoved = $certificatePasswordRemoved
            machineCertificatePasswordPresent = -not [string]::IsNullOrWhiteSpace(
                [Environment]::GetEnvironmentVariable($certificatePasswordVariable, 'Machine'))
            # CurrentUser\Root is out of reach of the service account that runs this upgrade, so a
            # root imported by an earlier -InstallCurrentUserRoot stays put. RELEASE-CANDIDATE.md
            # carries the manual removal; reporting it here as cleaned would be a false green.
            currentUserRootCertificateRemoved = $false
            currentUserRootCertificateRemovalIsManual = $true
        }
        protocolTag = $version.protocolTag
        protocolCommit = $version.protocolCommit
        safetyProjection = if ($safety) { [ordered]@{
            httpStatus = 200
            motionState = $safety.motionState
            source = $safety.source
            reasonCount = @($safety.reasonCodes).Count
        }} else { $null }
        journeyRuntimeEnabled = $false
        riotMutationPerformed = $false
        orderCreated = $false
        vehicleMoved = $false
        backupPath = $backupPath
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    [IO.File]::WriteAllText($resolvedResult, ($result | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Write-Diagnostic 'result-written'
}
catch {
    $original = $_
    Write-Diagnostic ("failure: {0}" -f $original.Exception.Message)
    try {
        try { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue } catch { }
        if ($backupComplete) {
            if (Test-Path -LiteralPath $installPath) {
                Remove-Item -LiteralPath $installPath -Recurse -Force
            }
            New-Item -ItemType Directory -Path $installPath -Force | Out-Null
            foreach ($item in Get-ChildItem -LiteralPath (Join-Path $backupPath 'install') -Force) {
                Copy-Item -LiteralPath $item.FullName -Destination $installPath -Recurse -Force
            }

            if (Test-Path -LiteralPath $dataRoot) {
                Remove-Item -LiteralPath $dataRoot -Recurse -Force
            }
            New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
            foreach ($item in Get-ChildItem -LiteralPath (Join-Path $backupPath 'data-root') -Force) {
                Copy-Item -LiteralPath $item.FullName -Destination $dataRoot -Recurse -Force
            }
        }
        if (Test-Path -LiteralPath $stagingPath) {
            Remove-Item -LiteralPath $stagingPath -Recurse -Force
        }
        if ($certificatePasswordRemoved) {
            [Environment]::SetEnvironmentVariable($certificatePasswordVariable, $oldCertificatePassword, 'Machine')
        }
        if ($serviceStopped) {
            Start-Service -Name $serviceName
            Wait-ServiceState 'Running'
        }
        Write-Diagnostic 'rollback-complete'
    }
    catch {
        $rollback = $_
        Write-Diagnostic ("rollback-failure: {0}" -f $rollback.Exception.Message)
        throw [AggregateException]::new('ControlServer upgrade and rollback both failed.', @($original.Exception, $rollback.Exception))
    }
    throw $original
}
