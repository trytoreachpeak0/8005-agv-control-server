[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [Parameter(Mandatory = $true)]
    [string]$ResultPath,
    [string]$DiagnosticPath,
    [string]$ServiceName = '8005 AGV ControlServer',
    [string]$InstallRoot = 'C:\Program Files\8005 AGV\ControlServer',
    [string]$DataRoot = 'C:\ProgramData\8005\ControlServer',
    [string]$BackupRoot = 'C:\ProgramData\8005\ControlServer-backups',
    [ValidateRange(1, 65535)]
    [int]$OnboardPort = 58005,
    [ValidateRange(1, 65535)]
    [int]$HealthPort = 58007,
    [switch]$InstallCurrentUserRoot,
    [switch]$CopyUserRiotSecretToMachine,
    [switch]$SkipMachineEnvironmentInjection
)

$ErrorActionPreference = 'Stop'

function Resolve-FullPath([string]$Path) {
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

$serviceName = $ServiceName
$installPath = Resolve-FullPath $InstallRoot
$dataRoot = Resolve-FullPath $DataRoot
$certificateDirectory = Join-Path $dataRoot 'certs'
$certificatePath = Join-Path $certificateDirectory 'localhost.pfx'
$logDirectory = Join-Path $dataRoot 'logs'
$backupRoot = Resolve-FullPath $BackupRoot
$resolvedPackage = Resolve-FullPath $PackagePath
$resolvedResult = Resolve-FullPath $ResultPath
$resolvedDiagnostic = if ([string]::IsNullOrWhiteSpace($DiagnosticPath)) { $null } else { Resolve-FullPath $DiagnosticPath }
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$backupPath = Join-Path $backupRoot $runId
$healthOrigin = "https://localhost:$HealthPort"
$rootCertificate = $null
$leafCertificate = $null
$trustedRootThumbprint = $null
$serviceCreated = $false
$installCreated = $false
$dataRootExisted = Test-Path -LiteralPath $dataRoot
$dataBackupCreated = $false
$oldRiotMachine = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', 'Machine')
$oldCertificatePassword = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD', 'Machine')
$machineEnvironmentInjected = $false

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

function Set-RestrictedRegistryAcl([Microsoft.Win32.RegistryKey]$Key) {
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
    $Key.SetAccessControl($security)
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
    $body = @(& "$env:SystemRoot\System32\curl.exe" --fail --silent --show-error `
        --noproxy localhost --ssl-revoke-best-effort --max-time 10 `
        "$healthOrigin/health/live" 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Schannel HTTPS live check failed with exit code $LASTEXITCODE`: $($body -join ' ')"
    }
    $response = $body | ConvertFrom-Json
    if ($response.status -ne 'live') { throw 'HTTPS live check returned an unexpected response.' }
}

function Get-ReadyCheck {
    $body = @(& "$env:SystemRoot\System32\curl.exe" --silent --show-error `
        --noproxy localhost --ssl-revoke-best-effort --max-time 10 `
        "$healthOrigin/health/ready" 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Schannel HTTPS ready check failed with exit code $LASTEXITCODE`: $($body -join ' ')"
    }
    $response = $body | ConvertFrom-Json
    if ($response.status -eq 'not-ready' -and $response.reason -eq 'DATABASE_UNAVAILABLE') {
        throw 'Readiness reports the database is unavailable after migration.'
    }
    if ($response.status -ne 'ready' -and $response.status -ne 'not-ready') {
        throw 'HTTPS ready check returned an unexpected response.'
    }
    return $response
}

function Get-VersionCheck {
    $body = @(& "$env:SystemRoot\System32\curl.exe" --fail --silent --show-error `
        --noproxy localhost --ssl-revoke-best-effort --max-time 10 `
        "$healthOrigin/version" 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Schannel HTTPS version check failed with exit code $LASTEXITCODE`: $($body -join ' ')"
    }
    return $body | ConvertFrom-Json
}

function Remove-CertificateByThumbprint([string]$StoreName, [string]$Thumbprint) {
    if ([string]::IsNullOrWhiteSpace($Thumbprint)) { return }
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        $StoreName,
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        $matches = $store.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $Thumbprint,
            $false)
        foreach ($match in $matches) { $store.Remove($match) }
    }
    finally {
        $store.Close()
    }
}

Assert-Administrator
if (-not $InstallCurrentUserRoot) {
    throw 'Explicit -InstallCurrentUserRoot authorization is required.'
}
if (-not $CopyUserRiotSecretToMachine -and -not $SkipMachineEnvironmentInjection) {
    throw 'Explicit -CopyUserRiotSecretToMachine authorization is required unless -SkipMachineEnvironmentInjection is used.'
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
if (-not $SkipMachineEnvironmentInjection -and
    -not [string]::IsNullOrWhiteSpace($oldRiotMachine) -and $oldRiotMachine -cne $riotUser) {
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
    Remove-CertificateByThumbprint 'My' $leafCertificate.Thumbprint
    Remove-CertificateByThumbprint 'My' $rootCertificate.Thumbprint

    if (-not $SkipMachineEnvironmentInjection) {
        [Environment]::SetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', $riotUser, 'Machine')
        [Environment]::SetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD', $certificatePassword, 'Machine')
        $machineEnvironmentInjected = $true
    }
    Write-Diagnostic ("machine-secret-injection-complete injected={0}" -f $machineEnvironmentInjected)

    $databasePath = Join-Path (Join-Path $dataRoot 'data') 'controlserver.db'
    $configuration = [ordered]@{
        Health = [ordered]@{ url = $healthOrigin }
        ConnectionStrings = [ordered]@{ ControlServer = "Data Source=$databasePath" }
        OnboardTransport = [ordered]@{
            enabled = $true
            listenAddress = '127.0.0.1'
            port = $OnboardPort
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
        Serilog = [ordered]@{
            WriteTo = @(
                [ordered]@{
                    Name = 'File'
                    Args = [ordered]@{
                        path = (Join-Path $logDirectory 'controlserver-.ndjson')
                        formatter = 'Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact'
                        rollingInterval = 'Day'
                        retainedFileCountLimit = 14
                        shared = $true
                    }
                }
            )
        }
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
    $binaryPath = '"{0}" --contentRoot "{1}" --environment Production' -f $executable, $installPath
    New-Service -Name $serviceName -BinaryPathName $binaryPath `
        -DisplayName $serviceName -Description '8005 AGV WIRE_TO_GATE ControlServer' `
        -StartupType Automatic | Out-Null
    $serviceCreated = $true
    $serviceRegistrySubKey = "SYSTEM\CurrentControlSet\Services\$serviceName"
    [string[]]$serviceEnvironment = @(
        'DOTNET_ENVIRONMENT=Production',
        "CONTROL_SERVER_RIOT_CALL_API_KEY=$riotUser",
        "CONTROL_SERVER_ONBOARD_CREDENTIAL=$onboardCredential",
        "CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD=$certificatePassword"
    )
    $serviceRegistryKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($serviceRegistrySubKey, $true)
    if ($null -eq $serviceRegistryKey) { throw 'The new service registry key cannot be opened.' }
    try {
        $serviceRegistryKey.SetValue('Environment', $serviceEnvironment, [Microsoft.Win32.RegistryValueKind]::MultiString)
        Set-RestrictedRegistryAcl $serviceRegistryKey
        $serviceEnvironmentVerified = @($serviceRegistryKey.GetValue('Environment')).Count -eq $serviceEnvironment.Count
    }
    finally {
        $serviceRegistryKey.Close()
    }
    Write-Diagnostic 'service-installation-complete'

    Start-Service -Name $serviceName
    Wait-ServiceState 'Running'
    Invoke-LiveCheck
    $firstReadiness = Get-ReadyCheck
    Stop-Service -Name $serviceName
    Wait-ServiceState 'Stopped'
    Start-Service -Name $serviceName
    Wait-ServiceState 'Running'
    Invoke-LiveCheck
    Restart-Service -Name $serviceName -Force
    Wait-ServiceState 'Running'
    Invoke-LiveCheck
    Write-Diagnostic 'service-lifecycle-checks-complete'

    $databaseCreated = Test-Path -LiteralPath $databasePath -PathType Leaf
    if (-not $databaseCreated) { throw "The service did not create the SQLite database at $databasePath." }
    $logFiles = @()
    if (Test-Path -LiteralPath $logDirectory -PathType Container) {
        $logFiles = @(Get-ChildItem -LiteralPath $logDirectory -File | Sort-Object Name |
            ForEach-Object { $_.Name })
    }
    if ($logFiles.Count -eq 0) { throw "The service did not write a log file under $logDirectory." }

    $version = Get-VersionCheck
    $resultDirectory = Split-Path -Parent $resolvedResult
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    $result = [ordered]@{
        schemaVersion = 2
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
        installRoot = $installPath
        dataRoot = $dataRoot
        databasePath = $databasePath
        databaseCreatedByMigration = $databaseCreated
        logDirectory = $logDirectory
        logFiles = @($logFiles)
        backupRoot = $backupRoot
        httpsEndpoint = $healthOrigin
        onboardTransportEndpoint = "tls://127.0.0.1:$OnboardPort"
        firstStartReadiness = [ordered]@{
            status = $firstReadiness.status
            reason = $firstReadiness.reason
        }
        certificate = [ordered]@{
            dnsNames = @('localhost')
            leafThumbprint = $leafCertificate.Thumbprint
            trustedRootThumbprint = $trustedRootThumbprint
            trustStore = 'CurrentUser/Root'
            privateKeyFile = $certificatePath
        }
        externalSecrets = [ordered]@{
            machineEnvironmentInjected = $machineEnvironmentInjected
            riotMachineScopePresent = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', 'Machine'))
            onboardMachineScopePresent = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CREDENTIAL', 'Machine'))
            certificatePasswordMachineScopePresent = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD', 'Machine'))
            serviceSpecificEnvironmentPresent = $serviceEnvironmentVerified
            valuesDisclosed = $false
        }
        checks = @('package-hashes', 'sqlite-migrations-at-start', 'https-live-after-start', 'https-ready-after-start', 'stop-start', 'restart', 'https-version', 'log-file-written')
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
    $originalError = $_
    $rollbackErrors = [Collections.Generic.List[string]]::new()
    Write-Diagnostic ("failure: {0}" -f $originalError.Exception.Message)
    try {
        if ($serviceCreated) {
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            & sc.exe delete $serviceName | Out-Null
        }
    }
    catch { $rollbackErrors.Add("service: $($_.Exception.Message)") }
    try {
        if ($installCreated -and (Test-Path -LiteralPath $installPath)) {
            Remove-Item -LiteralPath $installPath -Recurse -Force
        }
    }
    catch { $rollbackErrors.Add("install-path: $($_.Exception.Message)") }
    try {
        if ($machineEnvironmentInjected) {
            [Environment]::SetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', $oldRiotMachine, 'Machine')
            [Environment]::SetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD', $oldCertificatePassword, 'Machine')
        }
    }
    catch { $rollbackErrors.Add("machine-environment: $($_.Exception.Message)") }
    try {
        Remove-CertificateByThumbprint 'Root' $trustedRootThumbprint
        if ($leafCertificate) { Remove-CertificateByThumbprint 'My' $leafCertificate.Thumbprint }
        if ($rootCertificate) { Remove-CertificateByThumbprint 'My' $rootCertificate.Thumbprint }
    }
    catch { $rollbackErrors.Add("certificates: $($_.Exception.Message)") }
    try {
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
    }
    catch { $rollbackErrors.Add("data-root: $($_.Exception.Message)") }
    Write-Diagnostic ("rollback-complete errors={0}" -f $rollbackErrors.Count)
    if ($rollbackErrors.Count -gt 0) {
        throw "Deployment failed and rollback reported: $($rollbackErrors -join '; ')"
    }
    throw $originalError
}
