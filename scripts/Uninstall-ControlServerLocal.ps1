[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServiceName,
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [Parameter(Mandatory = $true)]
    [string]$DataRoot,
    [Parameter(Mandatory = $true)]
    [string]$ResultPath,
    [switch]$RemoveDataRoot,
    [switch]$ConfirmUninstall,
    [switch]$AllowProductionService
)

$ErrorActionPreference = 'Stop'

function Resolve-FullPath([string]$Path) {
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

$productionServiceName = '8005 AGV ControlServer'
$productionInstallRoot = 'C:\Program Files\8005 AGV\ControlServer'
$productionDataRoot = 'C:\ProgramData\8005\ControlServer'
$installPath = Resolve-FullPath $InstallRoot
$dataPath = Resolve-FullPath $DataRoot
$resolvedResult = Resolve-FullPath $ResultPath
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')

function Assert-Administrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Uninstall-ControlServerLocal.ps1 must run from an elevated PowerShell process.'
    }
}

Assert-Administrator
if (-not $ConfirmUninstall) {
    throw 'Explicit -ConfirmUninstall authorization is required.'
}
$targetsProduction = $ServiceName -eq $productionServiceName -or
    $installPath -eq (Resolve-FullPath $productionInstallRoot) -or
    $dataPath -eq (Resolve-FullPath $productionDataRoot)
if ($targetsProduction -and -not $AllowProductionService) {
    throw "Refusing to uninstall the production deployment without -AllowProductionService: $ServiceName"
}

$serviceRemoved = $false
$serviceExisted = $false
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    $serviceExisted = $true
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while ((Get-Service -Name $ServiceName).Status -ne 'Stopped' -and [DateTimeOffset]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 250
        }
    }
    & sc.exe delete $ServiceName | Out-Null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -and [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }
    $serviceRemoved = -not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
    if (-not $serviceRemoved) { throw "The service was not removed within 30 seconds: $ServiceName" }
}

$installRootRemoved = $false
if (Test-Path -LiteralPath $installPath) {
    Remove-Item -LiteralPath $installPath -Recurse -Force
    $installRootRemoved = -not (Test-Path -LiteralPath $installPath)
    if (-not $installRootRemoved) { throw "The install root was not removed: $installPath" }
}

$dataRootRemoved = $false
$dataRootRetained = $false
if (Test-Path -LiteralPath $dataPath) {
    if ($RemoveDataRoot) {
        Remove-Item -LiteralPath $dataPath -Recurse -Force
        $dataRootRemoved = -not (Test-Path -LiteralPath $dataPath)
        if (-not $dataRootRemoved) { throw "The data root was not removed: $dataPath" }
    }
    else {
        $dataRootRetained = $true
    }
}

$listeningPorts = @()
$netstat = @(& "$env:SystemRoot\System32\netstat.exe" -ano)
foreach ($line in $netstat) {
    if ($line -match '^\s*TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+(\d+)\s*$') {
        $listeningPorts += [int]$Matches[1]
    }
}

$resultDirectory = Split-Path -Parent $resolvedResult
New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
$result = [ordered]@{
    schemaVersion = 1
    result = 'PASS'
    runId = $runId
    completedAt = [DateTimeOffset]::UtcNow.ToString('O')
    serviceName = $ServiceName
    serviceExisted = $serviceExisted
    serviceRemoved = $serviceRemoved
    installRoot = $installPath
    installRootRemoved = $installRootRemoved
    installRootPresent = (Test-Path -LiteralPath $installPath)
    dataRoot = $dataPath
    dataRootRemoved = $dataRootRemoved
    dataRootRetained = $dataRootRetained
    dataRootPresent = (Test-Path -LiteralPath $dataPath)
    productionServicePresent = [bool](Get-Service -Name $productionServiceName -ErrorAction SilentlyContinue)
    productionServiceStatus = (Get-Service -Name $productionServiceName -ErrorAction SilentlyContinue).Status.ToString()
    listeningPortsAfterUninstall = @($listeningPorts | Sort-Object -Unique)
}
[IO.File]::WriteAllText($resolvedResult, ($result | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Write-Output "ControlServer uninstall PASS. Result: $resolvedResult"
