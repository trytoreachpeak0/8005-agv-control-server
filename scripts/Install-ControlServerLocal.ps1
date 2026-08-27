[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [Parameter(Mandatory = $true)]
    [string]$ResultPath,
    [string]$DiagnosticPath,
    [switch]$InstallCurrentUserRoot,
    [switch]$CopyUserRiotSecretToMachine
)

$ErrorActionPreference = 'Stop'
$serviceName = '8005 AGV ControlServer'
$installPath = 'C:\Program Files\8005 AGV\ControlServer'
$dataRoot = 'C:\ProgramData\8005\ControlServer'
$certificateDirectory = Join-Path $dataRoot 'certs'
$certificatePath = Join-Path $certificateDirectory 'localhost.pfx'
$backupRoot = 'C:\ProgramData\8005\ControlServer-backups'
$resolvedPackage = [IO.Path]::GetFullPath($PackagePath)
$resolvedResult = [IO.Path]::GetFullPath($ResultPath)
$resolvedDiagnostic = if ([string]::IsNullOrWhiteSpace($DiagnosticPath)) { $null } else { [IO.Path]::GetFullPath($DiagnosticPath) }
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$backupPath = Join-Path $backupRoot $runId
$rootCertificate = $null
$leafCertificate = $null
$trustedRootThumbprint = $null
$serviceCreated = $false
$installCreated = $false
$dataRootExisted = Test-Path -LiteralPath $dataRoot
$dataBackupCreated = $false
$oldRiotMachine = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', 'Machine')
$oldCertificatePassword = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD', 'Machine')

function Assert-Administrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Install-ControlServerLocal.ps1 must run from an elevated PowerShell process.'
    }
}

function Write-Diagnostic([string]$Message) {
    if ([string]::IsNullOrWhiteSpace($resolvedDiagnostic)) { return }
    $directory = Split-Path -Parent $resolvedDiagnostic
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $line = '{0} {1}{2}' -f [DateTimeOffset]::UtcNow.ToString('O'), $Message, [Environment]::NewLine
    [IO.File]::AppendAllText($resolvedDiagnostic, $line, [Text.UTF8Encoding]::new($false))
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

function Set-RestrictedRegistryAcl([string]$Path) {
    $security = [Security.AccessControl.RegistrySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sidValue in @('S-1-5-18', 'S-1-5-32-544')) {
        $sid = [Security.Principal.SecurityIdentifier]::new($sidValue)
        $rule = [Security.AccessControl.RegistryAccessRule]::new(
            $sid,
            [Security.AccessControl.RegistryRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]::ContainerInherit,
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
    $packagePrefix = $Path.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($file in $manifest.files) {
        $candidate = [IO.Path]::GetFullPath((Join-Path $Path $file.path))
        if (-not $candidate.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Package manifest contains an escaping path: $($file.path)"
        }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "Package file is missing: $($file.path)"
        }
        $actual = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $file.sha256) {
            throw "Package file hash mismatch: $($file.path)"
        }
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

function Invoke-LiveCheck {
    $response = Invoke-RestMethod -Uri 'https://localhost:58007/health/live' -Method Get -TimeoutSec 10
    if ($response.status -ne 'live') { throw 'HTTPS live check returned an unexpected response.' }
}

function Remove-CertificateByThumbprint([string]$StorePath, [string]$Thumbprint) {
    if ([string]::IsNullOrWhiteSpace($Thumbprint)) { return }
    $candidate = Join-Path $StorePath $Thumbprint
    if (Test-Path -LiteralPath $candidate) { Remove-Item -LiteralPath $candidate -Force }
}

Assert-Administrator
if (-not $InstallCurrentUserRoot) {
    throw 'Explicit -InstallCurrentUserRoot authorization is required.'
}
if (-not $CopyUserRiotSecretToMachine) {
    throw 'Explicit -CopyUserRiotSecretToMachine authorization is required.'
}
if (-not (Test-Path -LiteralPath $resolvedPackage -PathType Container)) {
    throw "Package path does not exist: $resolvedPackage"
}
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "Service already exists; this first-install script will not replace it: $serviceName"
}
if (Test-Path -LiteralPath $installPath) {
    throw "Install path already exists; this first-install script will not replace it: $installPath"
}

$manifest = Test-PackageManifest $resolvedPackage
$riotUser = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', 'User')
if ([string]::IsNullOrWhiteSpace($riotUser)) {
    throw 'User-scope CONTROL_SERVER_RIOT_CALL_API_KEY is missing.'
}
if (-not [string]::IsNullOrWhiteSpace($oldRiotMachine) -and $oldRiotMachine -cne $riotUser) {
    throw 'Machine-scope RIoT credential already exists with different content; refusing overwrite.'
}
$onboardCredential = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CREDENTIAL', 'Machine')
if ([string]::IsNullOrWhiteSpace($onboardCredential)) {
    throw 'Machine-scope CONTROL_SERVER_ONBOARD_CREDENTIAL is missing.'
}
Write-Diagnostic 'preflight-complete'

try {
    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
    if ($dataRootExisted) {
        $existingData = Get-ChildItem -LiteralPath $dataRoot -Force
        if ($existingData) {
            $dataBackup = Join-Path $backupPath 'data-root'
            New-Item -ItemType Directory -Path $dataBackup -Force | Out-Null
            foreach ($item in $existingData) { Copy-Item -LiteralPath $item.FullName -Destination $dataBackup -Recurse -Force }
            $dataBackupCreated = $true
        }
    }
    Write-Diagnostic 'backup-complete'

    New-Item -ItemType Directory -Path $installPath -Force | Out-Null
    $installCreated = $true
    foreach ($item in Get-ChildItem -LiteralPath $resolvedPackage -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $installPath -Recurse -Force
    }
    Write-Diagnostic 'package-copy-complete'

    New-Item -ItemType Directory -Path $certificateDirectory -Force | Out-Null
    Set-RestrictedDirectoryAcl $certificateDirectory

    $passwordBytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($passwordBytes)
    $certificatePassword = [Convert]::ToBase64String($passwordBytes)
    $securePassword = ConvertTo-SecureString -String $certificatePassword -AsPlainText -Force

    $rootCertificate = New-SelfSignedCertificate -Type Custom `
        -Subject "CN=8005 AGV ControlServer Local Development Root $runId" `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable `
        -KeyUsage CertSign, CRLSign, DigitalSignature `
        -TextExtension @('2.5.29.19={critical}{text}ca=true&pathlength=0') `
        -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter ((Get-Date).AddYears(1))
    $leafCertificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=localhost' -DnsName 'localhost' `
        -Signer $rootCertificate -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
        -KeyExportPolicy Exportable -KeyUsage DigitalSignature, KeyEncipherment `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.1') `
        -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter ((Get-Date).AddMonths(6))
    Write-Diagnostic 'certificate-generation-complete'

    Export-PfxCertificate -Cert $leafCertificate -FilePath $certificatePath -Password $securePassword | Out-Null
    $rootPublicPath = Join-Path $certificateDirectory 'localhost-development-root.cer'
    Export-Certificate -Cert $rootCertificate -FilePath $rootPublicPath -Type CERT | Out-Null
    $trustedRoot = Import-Certificate -FilePath $rootPublicPath -CertStoreLocation 'Cert:\CurrentUser\Root'
    $trustedRootThumbprint = $trustedRoot.Thumbprint
    Remove-CertificateByThumbprint 'Cert:\CurrentUser\My' $leafCertificate.Thumbprint
    Remove-CertificateByThumbprint 'Cert:\CurrentUser\My' $rootCertificate.Thumbprint

    [Environment]::SetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', $riotUser, 'Machine')
    [Environment]::SetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD', $certificatePassword, 'Machine')
    Write-Diagnostic 'machine-secret-injection-complete'

    $configuration = [ordered]@{
        Health = [ordered]@{ url = 'https://localhost:58007' }
        OnboardTransport = [ordered]@{
            enabled = $true
            listenAddress = '127.0.0.1'
            port = 58005
            serverCertificatePath = $certificatePath
            serverCertificatePasswordEnvironmentVariable = 'CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD'
            credentialEnvironmentVariable = 'CONTROL_SERVER_ONBOARD_CREDENTIAL'
            allowInsecureLoopback = $false
        }
        OnboardSafetyProjection = [ordered]@{
            enabled = $true
            requireHttps = $true
            credentialEnvironmentVariable = 'CONTROL_SERVER_ONBOARD_CREDENTIAL'
        }
        JourneyRuntime = [ordered]@{ enabled = $false }
    }
    $configurationPath = Join-Path $installPath 'appsettings.Production.json'
    [IO.File]::WriteAllText(
        $configurationPath,
        ($configuration | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false))
    Write-Diagnostic 'configuration-complete'

    Set-RestrictedDirectoryAcl $installPath
    Set-RestrictedDirectoryAcl $dataRoot

    $executable = Join-Path $installPath 'ControlServer.Host.exe'
    New-Service -Name $serviceName -BinaryPathName ('"{0}"' -f $executable) `
        -DisplayName $serviceName -Description '8005 AGV WIRE_TO_GATE ControlServer' `
        -StartupType Automatic | Out-Null
    $serviceCreated = $true
    $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
    $serviceEnvironment = @(
        "CONTROL_SERVER_RIOT_CALL_API_KEY=$riotUser",
        "CONTROL_SERVER_ONBOARD_CREDENTIAL=$onboardCredential",
        "CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD=$certificatePassword"
    )
    New-ItemProperty -LiteralPath $serviceRegistryPath -Name Environment -PropertyType MultiString `
        -Value $serviceEnvironment -Force | Out-Null
    Set-RestrictedRegistryAcl $serviceRegistryPath
    Write-Diagnostic 'service-installation-complete'

    Start-Service -Name $serviceName
    Wait-ServiceState 'Running'
    Invoke-LiveCheck
    Stop-Service -Name $serviceName
    Wait-ServiceState 'Stopped'
    Start-Service -Name $serviceName
    Wait-ServiceState 'Running'
    Invoke-LiveCheck
    Restart-Service -Name $serviceName -Force
    Wait-ServiceState 'Running'
    Invoke-LiveCheck
    Write-Diagnostic 'service-lifecycle-checks-complete'

    $version = Invoke-RestMethod -Uri 'https://localhost:58007/version' -Method Get -TimeoutSec 10
    $resultDirectory = Split-Path -Parent $resolvedResult
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    $result = [ordered]@{
        schemaVersion = 1
        result = 'PASS'
        runId = $runId
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
        sourceCommit = $manifest.sourceCommit
        packageManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $resolvedPackage 'deployment-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        configurationSha256 = (Get-FileHash -LiteralPath $configurationPath -Algorithm SHA256).Hash.ToLowerInvariant()
        serviceName = $serviceName
        serviceAccount = 'LocalSystem'
        serviceStatus = (Get-Service -Name $serviceName).Status.ToString()
        serviceStartType = (Get-CimInstance Win32_Service -Filter "Name='$serviceName'").StartMode
        httpsEndpoint = 'https://localhost:58007'
        onboardTransportEndpoint = 'tls://127.0.0.1:58005'
        certificate = [ordered]@{
            dnsNames = @('localhost')
            leafThumbprint = $leafCertificate.Thumbprint
            trustedRootThumbprint = $trustedRootThumbprint
            trustStore = 'CurrentUser/Root'
            privateKeyFile = $certificatePath
        }
        externalSecrets = [ordered]@{
            riotMachineScopePresent = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', 'Machine'))
            onboardMachineScopePresent = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CREDENTIAL', 'Machine'))
            certificatePasswordMachineScopePresent = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD', 'Machine'))
            serviceSpecificEnvironmentPresent = (Get-ItemProperty -LiteralPath $serviceRegistryPath -Name Environment -ErrorAction Stop).Environment.Count -eq 3
            valuesDisclosed = $false
        }
        checks = @('package-hashes', 'sqlite-migrations-at-start', 'https-live-after-start', 'stop-start', 'restart', 'https-version')
        journeyRuntimeEnabled = $false
        riotMutationPerformed = $false
        orderCreated = $false
        vehicleMoved = $false
        protocolTag = $version.protocolTag
        protocolCommit = $version.protocolCommit
        backupPath = $backupPath
    }
    [IO.File]::WriteAllText($resolvedResult, ($result | ConvertTo-Json -Depth 7), [Text.UTF8Encoding]::new($false))
    Write-Diagnostic 'result-written'
    Write-Output "ControlServer local deployment PASS. Result: $resolvedResult"
}
catch {
    Write-Diagnostic ("failure: {0}" -f $_.Exception.Message)
    if ($serviceCreated) {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $serviceName | Out-Null
    }
    if ($installCreated -and (Test-Path -LiteralPath $installPath)) {
        Remove-Item -LiteralPath $installPath -Recurse -Force
    }
    if (Test-Path -LiteralPath $dataRoot) {
        Get-ChildItem -LiteralPath $dataRoot -Force | Remove-Item -Recurse -Force
        if ($dataBackupCreated) {
            foreach ($item in Get-ChildItem -LiteralPath (Join-Path $backupPath 'data-root') -Force) {
                Copy-Item -LiteralPath $item.FullName -Destination $dataRoot -Recurse -Force
            }
        }
        elseif (-not $dataRootExisted) {
            Remove-Item -LiteralPath $dataRoot -Force
        }
    }
    Remove-CertificateByThumbprint 'Cert:\CurrentUser\Root' $trustedRootThumbprint
    if ($leafCertificate) { Remove-CertificateByThumbprint 'Cert:\CurrentUser\My' $leafCertificate.Thumbprint }
    if ($rootCertificate) { Remove-CertificateByThumbprint 'Cert:\CurrentUser\My' $rootCertificate.Thumbprint }
    [Environment]::SetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', $oldRiotMachine, 'Machine')
    [Environment]::SetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD', $oldCertificatePassword, 'Machine')
    Write-Diagnostic 'rollback-complete'
    throw
}
