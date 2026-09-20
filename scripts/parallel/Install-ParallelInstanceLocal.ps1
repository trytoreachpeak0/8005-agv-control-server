#Requires -Version 7

<#
.SYNOPSIS
    Installs or upgrades the v2 parallel ControlServer instance on factory01, beside the MVP
    service, without touching it. Runs ON the factory server.

.DESCRIPTION
    control-server#262. From roughly 2026-10-08 two ControlServer instances share this machine:
    the MVP service driving agv01 for real customers, and this one driving agv02/agv03 on
    injected test demand. This script is the second one's installer.

    It is orchestration only. The install itself is done by the product scripts that ship in
    the package -- scripts\Install-ControlServerLocal.ps1 and scripts\Update-ControlServerLocal.ps1
    -- which already take every isolation parameter this needs: service name, install root,
    data root, backup root, ports and bind addresses. Nothing here reimplements their backup,
    ACL, lifecycle-check or rollback behaviour, and nothing here changes their defaults, so the
    MVP deployment path is bit-for-bit what it was.

    What this script adds around them:

      * the instance definition is asserted before anything is stopped or unpacked, so a
        definition naming agv01, the production ports or an inherited RouteGraph section is
        refused while the machine is still untouched;
      * the MVP service is fingerprinted before and after, and a change is an error rather than
        something to notice later;
      * the configuration overlay the product installer does not write -- MesIngest pointed at
        the local double, the JourneyRuntime identity, the RouteGraph section, the RIoT create
        gate -- merged onto appsettings.Production.json;
      * the resident FakeMesIngest, as a scheduled task;
      * firewall rules for this instance's two ports, named so they cannot be confused with the
        MVP's;
      * one previous generation kept on disk, so -Rollback is a real operation.

    Two isolation details that are not obvious and that this script depends on:

      * -SkipMachineEnvironmentInjection rather than -CopyUserRiotSecretToMachine. The
        machine-scope CONTROL_SERVER_RIOT_CALL_API_KEY is a shared resource the MVP install
        already set; the parallel instance reads it and the user-scope copy, and writes
        neither. The service's own registry Environment carries what it needs.
      * -CertificatePasswordVariable is given a name of this instance's own.
        Update-ControlServerLocal.ps1 *deletes* the machine-scope variable it is pointed at, as
        plaintext-era cleanup. Left at its default, upgrading the parallel instance would
        delete the MVP's.

.PARAMETER PackageZip
    The release zip already copied to this instance's staging directory.

.PARAMETER ExpectedSha256
    Hash the control host verified before copying. Verified again here: a truncated scp is
    exactly the failure this catches.

.PARAMETER DeploymentConfigPath
    JSON holding the secrets. Deleted when this script finishes, however it finishes.

.PARAMETER InstanceDefinitionPath
    The instance definition (scripts/parallel/instance-factory01-v2.json), copied here by the
    control host.

.PARAMETER FakeMesIngestZip
    Self-contained publish of ControlServer.FakeMesIngest. Omit on an upgrade that keeps the
    double it already deployed.

.PARAMETER AllowRiotCreateDispatch
    Deploy a definition whose riotCreateDispatch gate is open. Placing RIoT orders moves a
    vehicle; that is authorized one run at a time with somebody on site, never by a deployment.

.PARAMETER Rollback
    Reinstall the previous generation from <packageRoot>.previous. Downloads nothing.
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Install')]
    [string] $PackageZip,

    [Parameter(Mandatory = $true, ParameterSetName = 'Install')]
    [string] $ExpectedSha256,

    [Parameter(Mandatory = $true, ParameterSetName = 'Install')]
    [string] $DeploymentConfigPath,

    [Parameter(Mandatory = $true, ParameterSetName = 'Install')]
    [string] $FakeMesIngestZip,

    [Parameter(Mandatory = $true, ParameterSetName = 'Rollback')]
    [switch] $Rollback,

    [Parameter(Mandatory = $true)]
    [string] $InstanceDefinitionPath,

    [switch] $AllowRiotCreateDispatch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

Import-Module (Join-Path $PSScriptRoot 'ParallelInstance.psm1') -Force

$productionServiceName = '8005 AGV ControlServer'

function Write-Step {
    param([string] $Message)
    Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Message)
}

function Assert-Administrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must run elevated.'
    }
}

function Get-MvpFingerprint {
    <#
        Enough of the MVP service to notice if this deployment moved it: whether it exists, its
        status, start type, binary path and the pid it is running under. Compared before and
        after, because "I did not touch it" is a claim worth having evidence for on a machine
        that is serving customers.
    #>
    $service = Get-Service -Name $productionServiceName -ErrorAction SilentlyContinue
    if (-not $service) { return [ordered]@{ present = $false } }
    $wmi = Get-CimInstance -ClassName Win32_Service -Filter "Name='$productionServiceName'" -ErrorAction SilentlyContinue
    return [ordered]@{
        present = $true
        status = [string] $service.Status
        startType = [string] $service.StartType
        pathName = $wmi ? [string] $wmi.PathName : '(unavailable)'
        processId = $wmi ? [int] $wmi.ProcessId : 0
    }
}

function Assert-MvpUntouched {
    param($Before, $After)
    $diff = @()
    foreach ($key in $Before.Keys) {
        if ("$($Before[$key])" -cne "$($After[$key])") {
            $diff += "$key : '$($Before[$key])' -> '$($After[$key])'"
        }
    }
    if ($diff.Count -gt 0) {
        throw ("The MVP service changed during this deployment, which must never happen: " +
            ($diff -join '; '))
    }
}

Assert-Administrator

# ------------------------------------------------------- definition and layout ---

$definition = Read-ParallelInstanceDefinition -Path $InstanceDefinitionPath
$null = Assert-ParallelInstanceDefinition -Definition $definition -AllowRiotCreateDispatch:$AllowRiotCreateDispatch
Write-Step "Instance definition accepted: $($definition['instanceId'])"

$serviceName = [string] $definition['serviceName']
$installRoot = [string] $definition['installRoot']
$dataRoot = [string] $definition['dataRoot']
$backupRoot = [string] $definition['backupRoot']
$packageRoot = [string] $definition['packageRoot']
$opsRoot = [string] $definition['opsRoot']
$previousRoot = "$packageRoot.previous"
$resultRoot = Join-Path $opsRoot 'results'
$onboardPort = [int] $definition['onboardPort']
$healthPort = [int] $definition['healthPort']
$listenAddress = [string] $definition['listenAddress']
$healthBindAddress = [string] $definition['healthBindAddress']
$healthOrigin = "http://${healthBindAddress}:$healthPort"

# This instance's own name, never the MVP's. Update-ControlServerLocal.ps1 deletes the
# machine-scope variable it is told about; pointing it at a name only this instance uses is
# what keeps that deletion from reaching the production deployment.
$certificatePasswordVariable = 'CONTROL_SERVER_V2_ONBOARD_CERTIFICATE_PASSWORD'

New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$resultPath = Join-Path $resultRoot "install-$runId.json"
$diagnosticPath = Join-Path $resultRoot "install-$runId.log"

$mvpBefore = Get-MvpFingerprint
Write-Step ("MVP service before: " + (($mvpBefore.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ' '))

function Invoke-ProductInstaller {
    <#
        Which of the two product scripts applies is a property of the machine, not of the
        caller's intent -- the same decision Install-ControlServerRemote.ps1 makes for the MVP,
        so the control host runs one command for a first install and for every one after it.
    #>
    param([string] $PackageDirectory)

    $scripts = Join-Path $PackageDirectory 'scripts'
    $payload = Join-Path $PackageDirectory 'controlserver'
    if (-not (Test-Path -LiteralPath $payload -PathType Container)) {
        throw "The package has no controlserver directory: $payload"
    }

    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        Write-Step "Upgrading $serviceName with Update-ControlServerLocal.ps1"
        & (Join-Path $scripts 'Update-ControlServerLocal.ps1') `
            -PackagePath $payload -ResultPath $resultPath -DiagnosticPath $diagnosticPath `
            -ServiceName $serviceName -InstallRoot $installRoot -DataRoot $dataRoot `
            -BackupRoot $backupRoot -CertificatePasswordVariable $certificatePasswordVariable `
            -VerifySafetyProjectionReadOnly
    } else {
        Write-Step "First install of $serviceName with Install-ControlServerLocal.ps1"
        & (Join-Path $scripts 'Install-ControlServerLocal.ps1') `
            -PackagePath $payload -ResultPath $resultPath -DiagnosticPath $diagnosticPath `
            -ServiceName $serviceName -InstallRoot $installRoot -DataRoot $dataRoot `
            -BackupRoot $backupRoot `
            -ListenAddress $listenAddress -HealthBindAddress $healthBindAddress `
            -OnboardPort $onboardPort -HealthPort $healthPort `
            -SkipMachineEnvironmentInjection
    }
}

function Set-InstanceConfiguration {
    <#
        The product installer writes a fixed set of keys and stops. Everything else this
        instance needs is merged on top of the file it wrote.

        The merge is per key, exactly as .NET configuration is. That is also why it is
        necessary: Install-ControlServerLocal.ps1 writing JourneyRuntime = { enabled = false }
        leaves agvId and vehicleKey coming from the package's own appsettings.json, where they
        still name agv01 -- the production vehicle. An instance deployed without this overlay
        would look isolated and be pointed at the wrong car.
    #>
    $configurationPath = Join-Path $installRoot 'appsettings.Production.json'
    if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
        throw "The product installer wrote no configuration at $configurationPath."
    }
    $current = Get-Content -LiteralPath $configurationPath -Raw -Encoding utf8 |
        ConvertFrom-Json -AsHashtable -Depth 12
    $overlay = New-ParallelInstanceConfigurationOverlay -Definition $definition
    $merged = Merge-ConfigurationTree -Base $current -Overlay $overlay
    [IO.File]::WriteAllText(
        $configurationPath,
        (ConvertTo-Json -InputObject $merged -Depth 12),
        [Text.UTF8Encoding]::new($false))
    Write-Step "Configuration overlay merged into $configurationPath"

    # Read it back and assert the three keys that decide what this instance does. A merge that
    # silently dropped one of them would leave a service that starts, answers health and drives
    # the wrong vehicle on real demand.
    $verify = Get-Content -LiteralPath $configurationPath -Raw -Encoding utf8 |
        ConvertFrom-Json -AsHashtable -Depth 12
    $expected = @{
        'JourneyRuntime.vehicleKey' = [string] $definition['journeyRuntime']['vehicleKey']
        'JourneyRuntime.agvId' = [string] $definition['journeyRuntime']['agvId']
        'MesIngest.baseUrl' = [string] $definition['mesIngest']['baseUrl']
    }
    foreach ($pair in $expected.GetEnumerator()) {
        $parts = $pair.Key.Split('.')
        $actual = [string] $verify[$parts[0]][$parts[1]]
        if ($actual -cne $pair.Value) {
            throw "$($pair.Key) is '$actual' after the merge; expected '$($pair.Value)'."
        }
    }
    Write-Step 'Configuration verified: vehicle identity and MesIngest origin are this instance''s own'
    Restart-Service -Name $serviceName -Force
    Write-Step "$serviceName restarted with the merged configuration"
}

function Install-FakeMesIngest {
    <#
        The resident demand source. A scheduled task rather than a service, for the reason the
        dashboard is one: the double has no UseWindowsService and the service control manager
        kills an executable that never reports in.
    #>
    param([string] $Zip)

    $fake = $definition['fakeMesIngest']
    $fakeInstallRoot = [string] $fake['installRoot']
    $taskName = [string] $fake['taskName']
    $seedPath = [string] $fake['seedPath']
    $fakePort = [int] $fake['port']
    $logPath = Join-Path $opsRoot 'logs\fake-mes-ingest.log'

    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Write-Step "Stopping the existing $taskName"
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    # The task is gone but the process it started is not: it was launched detached, and an
    # orphan still holds the port the new one is about to bind.
    Get-Process -Name 'ControlServer.FakeMesIngest' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($fakeInstallRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue

    if ($Zip) {
        if (Test-Path -LiteralPath $fakeInstallRoot) {
            Remove-Item -LiteralPath $fakeInstallRoot -Recurse -Force
        }
        Expand-Archive -LiteralPath $Zip -DestinationPath $fakeInstallRoot -Force
        Write-Step "FakeMesIngest unpacked to $fakeInstallRoot"
    }

    $executable = Join-Path $fakeInstallRoot 'ControlServer.FakeMesIngest.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "FakeMesIngest executable missing: $executable"
    }

    # The seed file is created empty rather than left absent. An absent file and an empty
    # catalog behave the same way, but the file is where somebody adds a demand later, and a
    # path that does not exist is a path nobody finds.
    if (-not (Test-Path -LiteralPath $seedPath -PathType Leaf)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $seedPath) -Force | Out-Null
        [IO.File]::WriteAllText(
            $seedPath,
            (ConvertTo-Json -InputObject ([ordered]@{ demands = @() }) -Depth 5),
            [Text.UTF8Encoding]::new($false))
        Write-Step "Empty seed file created at $seedPath"
    }

    $runner = Join-Path $fakeInstallRoot 'Start-FakeMesIngestResident.ps1'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-FakeMesIngestResident.ps1') -Destination $runner -Force

    $argument = ('-NoProfile -ExecutionPolicy Bypass -File "{0}" -ExecutablePath "{1}" -Port {2} -SeedPath "{3}" -LogPath "{4}"' -f
        $runner, $executable, $fakePort, $seedPath, $logPath)
    $action = New-ScheduledTaskAction -Execute 'pwsh.exe' -Argument $argument -WorkingDirectory $fakeInstallRoot
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings `
        -Description '8005 AGV ControlServer v2 parallel instance: injected MES demand, loopback only' | Out-Null
    Start-ScheduledTask -TaskName $taskName
    Write-Step "Scheduled task '$taskName' registered and started"

    $deadline = [datetime]::UtcNow.AddSeconds(120)
    while ([datetime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri "http://127.0.0.1:$fakePort/control/v1/health" `
                -NoProxy -TimeoutSec 5 -UseBasicParsing
            if ($response.StatusCode -eq 200) {
                Write-Step "FakeMesIngest live: $($response.Content)"
                return
            }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    throw "FakeMesIngest did not answer http://127.0.0.1:$fakePort/control/v1/health within 120 s. Log: $logPath"
}

# ---------------------------------------------------------------------- rollback ---

if ($Rollback) {
    if (-not (Test-Path -LiteralPath $previousRoot -PathType Container)) {
        throw "No previous generation at $previousRoot. Nothing to roll back to."
    }
    Write-Step "Rolling back $serviceName to the package in $previousRoot"

    # Swap rather than copy, so rolling back a rollback is the same operation again.
    $swap = "$packageRoot.rollback-$runId"
    if (Test-Path -LiteralPath $packageRoot) { Move-Item -LiteralPath $packageRoot -Destination $swap }
    Move-Item -LiteralPath $previousRoot -Destination $packageRoot
    if (Test-Path -LiteralPath $swap) { Move-Item -LiteralPath $swap -Destination $previousRoot }

    Invoke-ProductInstaller -PackageDirectory $packageRoot
    # The overlay is reapplied: an upgrade keeps the existing appsettings.Production.json, but a
    # rollback to a generation installed before some overlay key existed would otherwise come
    # back without it.
    Set-InstanceConfiguration
    Install-FakeMesIngest -Zip ''
    Assert-MvpUntouched -Before $mvpBefore -After (Get-MvpFingerprint)
    Write-Step "Rolled back. Result: $resultPath"
    Write-Step 'The onboard half was NOT rolled back. The two ends do not negotiate versions.'
    return
}

# ------------------------------------------------------------------------ verify ---

try {
    foreach ($pair in @(@{ Path = $PackageZip; Name = 'package' }, @{ Path = $FakeMesIngestZip; Name = 'FakeMesIngest package' })) {
        if (-not (Test-Path -LiteralPath $pair.Path -PathType Leaf)) {
            throw "$($pair.Name) not found: $($pair.Path)"
        }
    }
    $actual = (Get-FileHash -LiteralPath $PackageZip -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = $ExpectedSha256.ToLowerInvariant()
    if ($actual -ne $expectedHash) {
        throw "Package hash mismatch on the server. Expected $expectedHash, got $actual."
    }
    Write-Step "Package hash verified ($actual)"

    $config = Get-Content -Raw -LiteralPath $DeploymentConfigPath -Encoding utf8 | ConvertFrom-Json

    # ------------------------------------------------------------------ unpack ---

    $staged = "$packageRoot.incoming-$runId"
    if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Recurse -Force }
    Expand-Archive -LiteralPath $PackageZip -DestinationPath $staged -Force
    Write-Step "Unpacked to $staged"

    $manifestPath = Join-Path $staged 'release-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'The package carries no release-manifest.json; refusing to install it.'
    }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath -Encoding utf8 | ConvertFrom-Json
    Write-Step ('Release: controlserver {0}  onboard {1}' -f
        $manifest.components.controlServer.commit.Substring(0, 7),
        $manifest.components.onboardHmi.commit.Substring(0, 7))

    # ----------------------------------------------------------------- secrets ---

    # User scope only. The machine scope is shared with the MVP deployment and this instance
    # neither needs to write it (-SkipMachineEnvironmentInjection) nor may: the product
    # installer throws when a machine-scope RIoT key disagrees with the user-scope one, and
    # overwriting it would be this deployment reaching into the production one.
    [Environment]::SetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', $config.riotCallApiKey, 'User')
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CREDENTIAL', 'Machine'))) {
        throw ('Machine-scope CONTROL_SERVER_ONBOARD_CREDENTIAL is missing. It is set by the MVP ' +
            'deployment and shared by both instances; this script will not create it, because ' +
            'creating it would mean guessing a credential the vehicles already use.')
    }
    Write-Step 'Secrets placed in the user scope; the shared machine scope was read, not written'

    # ----------------------------------------------------------------- install ---

    Invoke-ProductInstaller -PackageDirectory $staged

    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw "The product installer produced no result file at $resultPath."
    }
    Write-Step "Installer result written to $resultPath"

    Set-InstanceConfiguration

    # ------------------------------------------------- MesIngest bearer token ---

    # Same reason the MVP path injects it: the product scripts do not, and ControlServer
    # becomes a MesIngest caller the moment JourneyRuntime is enabled -- which for this
    # instance it is. The double does not check the token; the variable is set anyway so that
    # pointing this instance at a real MesIngest later is a configuration change rather than a
    # redeployment.
    if ($config.PSObject.Properties.Name -contains 'mesIngestSharedSecret' -and $config.mesIngestSharedSecret) {
        $key = "SYSTEM\CurrentControlSet\Services\$serviceName"
        $registryKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($key, $true)
        if ($null -eq $registryKey) { throw "Cannot open the service registry key: $key" }
        try {
            [string[]]$environment = @($registryKey.GetValue('Environment'))
            $environment = @($environment | Where-Object { $_ -notlike 'CONTROL_SERVER_MES_INGEST_SHARED_SECRET=*' })
            $environment += "CONTROL_SERVER_MES_INGEST_SHARED_SECRET=$($config.mesIngestSharedSecret)"
            $registryKey.SetValue('Environment', $environment, [Microsoft.Win32.RegistryValueKind]::MultiString)
        } finally {
            $registryKey.Close()
        }
        Restart-Service -Name $serviceName -Force
        Write-Step 'MesIngest bearer token added to this service''s environment; service restarted'
    }

    # --------------------------------------------------------- fake MES ingest ---

    Install-FakeMesIngest -Zip $FakeMesIngestZip

    # ---------------------------------------------------------------- firewall ---

    # Named after this instance, so that removing it later cannot remove the MVP's rules and so
    # that an operator listing the rules can tell which service each one opens.
    foreach ($port in @($onboardPort, $healthPort)) {
        $ruleName = "8005 AGV ControlServer V2 $port"
        Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
            -Protocol TCP -LocalPort $port -Profile Any | Out-Null
    }
    Write-Step "Firewall open for TCP $onboardPort and $healthPort (V2 rules only)"

    # ------------------------------------------------------------------- check ---

    $live = Invoke-WebRequest -Uri "$healthOrigin/health/live" -NoProxy -TimeoutSec 15 -UseBasicParsing
    if ($live.StatusCode -ne 200) { throw "health/live returned $($live.StatusCode)." }
    Write-Step "health/live 200: $($live.Content)"

    # ------------------------------------------------------- generation swap ---

    if (Test-Path -LiteralPath $previousRoot) { Remove-Item -LiteralPath $previousRoot -Recurse -Force }
    if (Test-Path -LiteralPath $packageRoot) { Move-Item -LiteralPath $packageRoot -Destination $previousRoot }
    Move-Item -LiteralPath $staged -Destination $packageRoot
    Write-Step "Package at $packageRoot; previous generation kept at $previousRoot"

    # --------------------------------------------------------------- MVP check ---

    Assert-MvpUntouched -Before $mvpBefore -After (Get-MvpFingerprint)
    Write-Step 'MVP service unchanged (status, start type, binary path and pid all identical)'

    Write-Step 'Parallel deployment complete.'
    Write-Output "RESULT_PATH=$resultPath"
    Write-Output "ONBOARD_ENDPOINT=tcp://${listenAddress}:$onboardPort"
    Write-Output "HEALTH_ENDPOINT=$healthOrigin"
    Write-Output "FAKE_MES_INGEST=http://127.0.0.1:$($definition['fakeMesIngest']['port'])"

} finally {
    if ($DeploymentConfigPath -and (Test-Path -LiteralPath $DeploymentConfigPath)) {
        Remove-Item -LiteralPath $DeploymentConfigPath -Force -ErrorAction SilentlyContinue
    }
    Get-ChildItem -Path (Split-Path -Parent $packageRoot) -Directory -Filter '*.incoming-*' -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}
