#requires -Version 7
# Ticket 03 acceptance: install -> start -> stop -> start -> forced restart -> uninstall on an
# ISOLATED instance. Different service name, install root, data root, backup root and ports, so the
# installed production service (58005/58007) is never touched. Must run elevated.
#
# -Root is a scratch directory holding the published package plus this run's lifecycle and evidence
# output. Publish the package with scripts\Publish-ControlServer.ps1 -OutputPath <Root>\package first,
# so the install result records the commit under test.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Root,
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'

$root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Root)
$package = if ([string]::IsNullOrWhiteSpace($PackagePath)) { Join-Path $root 'package' } else { $PackagePath }
$lifecycle = Join-Path $root 'lifecycle'
$evidence = Join-Path $root 'evidence'
$scripts = Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))) 'scripts'

if (-not (Test-Path -LiteralPath (Join-Path $package 'ControlServer.Host.exe') -PathType Leaf)) {
    throw "No published package at $package. Run scripts\Publish-ControlServer.ps1 -OutputPath '$package' first."
}

if (Test-Path -LiteralPath $lifecycle) { Remove-Item -LiteralPath $lifecycle -Recurse -Force }
New-Item -ItemType Directory -Path $lifecycle, $evidence -Force | Out-Null

$serviceName = '8005 AGV ControlServer Ticket03 Probe'
$installRoot = Join-Path $lifecycle 'install'
$dataRoot = Join-Path $lifecycle 'data'
$backupRoot = Join-Path $lifecycle 'backups'
$onboardPort = 58405
$healthPort = 58407
$certificatePasswordVariable = 'CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD'

function Get-CurrentUserRootThumbprints {
    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::Root,
        [Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        return @($store.Certificates | ForEach-Object { $_.Thumbprint } | Sort-Object)
    }
    finally { $store.Close(); $store.Dispose() }
}

function Get-ProductionSnapshot {
    $service = Get-Service -Name '8005 AGV ControlServer' -ErrorAction SilentlyContinue
    $ports = @(Get-Process -Name 'ControlServer.Host' -ErrorAction SilentlyContinue | ForEach-Object {
        Get-NetTCPConnection -State Listen -OwningProcess $_.Id -ErrorAction SilentlyContinue
    } | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" } | Sort-Object -Unique)
    return [ordered]@{ status = if ($service) { $service.Status.ToString() } else { $null }; listeners = $ports }
}

$before = [ordered]@{
    currentUserRootThumbprints = Get-CurrentUserRootThumbprints
    machineCertificatePasswordPresent = -not [string]::IsNullOrWhiteSpace(
        [Environment]::GetEnvironmentVariable($certificatePasswordVariable, 'Machine'))
    production = Get-ProductionSnapshot
}

$installResult = Join-Path $evidence 'install-result.json'
$installDiagnostic = Join-Path $evidence 'install-diagnostic.log'
$uninstallResult = Join-Path $evidence 'uninstall-result.json'
foreach ($stale in @($installResult, $installDiagnostic, $uninstallResult)) {
    if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Force }
}

$installTranscript = Join-Path $evidence 'install-console.log'
$uninstallTranscript = Join-Path $evidence 'uninstall-console.log'

& "$scripts\Install-ControlServerLocal.ps1" `
    -PackagePath $package -ResultPath $installResult -DiagnosticPath $installDiagnostic `
    -ServiceName $serviceName -InstallRoot $installRoot -DataRoot $dataRoot -BackupRoot $backupRoot `
    -OnboardPort $onboardPort -HealthPort $healthPort `
    -SkipMachineEnvironmentInjection *>&1 | Tee-Object -FilePath $installTranscript

$afterInstall = [ordered]@{
    currentUserRootThumbprints = Get-CurrentUserRootThumbprints
    certsDirectoryPresent = Test-Path -LiteralPath (Join-Path $dataRoot 'certs')
    keyMaterialFiles = @(Get-ChildItem -LiteralPath $lifecycle -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in @('.pfx', '.pem', '.cer', '.p12', '.key') } |
        ForEach-Object { $_.FullName })
    serviceEnvironment = @([Microsoft.Win32.Registry]::LocalMachine.OpenSubKey(
        "SYSTEM\CurrentControlSet\Services\$serviceName").GetValue('Environment') |
        ForEach-Object { ($_ -split '=')[0] })
    machineCertificatePasswordPresent = -not [string]::IsNullOrWhiteSpace(
        [Environment]::GetEnvironmentVariable($certificatePasswordVariable, 'Machine'))
    production = Get-ProductionSnapshot
}

& "$scripts\Uninstall-ControlServerLocal.ps1" `
    -ServiceName $serviceName -InstallRoot $installRoot -DataRoot $dataRoot `
    -ResultPath $uninstallResult -RemoveDataRoot -ConfirmUninstall *>&1 |
    Tee-Object -FilePath $uninstallTranscript

$after = [ordered]@{
    currentUserRootThumbprints = Get-CurrentUserRootThumbprints
    production = Get-ProductionSnapshot
}

$report = [ordered]@{
    installResult = Get-Content -Raw -LiteralPath $installResult | ConvertFrom-Json
    uninstallResult = Get-Content -Raw -LiteralPath $uninstallResult | ConvertFrom-Json
    diagnosticSteps = @(Get-Content -LiteralPath $installDiagnostic | ForEach-Object { ($_ -split ' ', 2)[1] })
    currentUserRootUnchangedAcrossInstall =
        (@(Compare-Object $before.currentUserRootThumbprints $afterInstall.currentUserRootThumbprints).Count -eq 0)
    currentUserRootUnchangedAcrossUninstall =
        (@(Compare-Object $before.currentUserRootThumbprints $after.currentUserRootThumbprints).Count -eq 0)
    currentUserRootCountBefore = $before.currentUserRootThumbprints.Count
    certsDirectoryPresentAfterInstall = $afterInstall.certsDirectoryPresent
    keyMaterialFilesUnderInstall = $afterInstall.keyMaterialFiles
    serviceEnvironmentVariableNames = $afterInstall.serviceEnvironment
    machineCertificatePasswordUntouched =
        ($before.machineCertificatePasswordPresent -eq $afterInstall.machineCertificatePasswordPresent)
    productionBefore = $before.production
    productionAfterInstall = $afterInstall.production
    productionAfterUninstall = $after.production
}
$json = $report | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText((Join-Path $evidence 'lifecycle-report.json'), $json, [Text.UTF8Encoding]::new($false))
$json
