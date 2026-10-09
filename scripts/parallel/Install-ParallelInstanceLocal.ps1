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
      * what the manual station clearance exit needs (control-server#454), on the install, upgrade
        and rollback paths alike: the VehicleFaultRecovery and FieldOperatorRoles sections, the
        fault recovery credential in the service's Environment (from deploy-config.json's
        faultRecoveryCredential, or carried over from the service), an empty roster file if none
        exists, and a CLEARANCE_EXIT_READINESS line read back from what the service will read --
        a broken state throws, an unavailable exit warns as CLEARANCE_EXIT_UNAVAILABLE;
      * the resident FakeMesIngest, as a scheduled task;
      * firewall rules for this instance's two ports, named so they cannot be confused with the
        MVP's;
      * one previous generation kept on disk, so -Rollback is a real operation.

    TWIN SCRIPT. The MVP deployment's equivalent is
    remote-ops/factory-server/scripts/control-server/Install-ControlServerRemote.ps1 in the
    8005-workspace repository. The two are deliberately separate scripts, which means a fix to
    one does not reach the other by itself. When you change something here that touches the
    machine rather than this instance -- a cleanup glob, a firewall rule, a shared environment
    variable -- look at that file too. The first case of this was found in review: both
    carried the same unprefixed '*.incoming-*' cleanup, copied across, and each one deleted
    the other's staging directory.

    Two isolation details that are not obvious and that this script depends on:

      * -SkipMachineEnvironmentInjection rather than -CopyUserRiotSecretToMachine. The
        machine-scope CONTROL_SERVER_RIOT_CALL_API_KEY is a shared resource the MVP install
        already set; the parallel instance reads it and never writes it. It does write the
        user-scope copy, which the product installer reads -- and which the MVP deployment
        writes too, so the two share one key there as well. The service's own registry
        Environment carries what it needs at run time.
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

.PARAMETER AllowRiotForeignOrderCancel
    Deploy a definition whose riotForeignOrderCancel gate is open (control-server#330). The
    instance then cancels any order RIoT shows running on one of its vehicles that it did not
    create -- including a person's manual move or charge order, or an experiment's -- which
    stops that vehicle where it is. Authorized on its own, apart from placing orders, and never
    by a deployment. With the gate closed such an order is only held and alarmed.

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

    [switch] $AllowRiotCreateDispatch,

    [switch] $AllowRiotForeignOrderCancel
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

Import-Module (Join-Path $PSScriptRoot 'ParallelInstance.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ParallelHost.psm1') -Force

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

# ------------------------------------------------------- the secrets file ---

# deploy-config.json holds the RIoT call API key, the MesIngest shared secret and (control-server#454) the
# fault recovery credential in plain text.
# From the moment the control host copied it, every way out of this script removes it: this try
# starts before the first check, so a refused definition, a refused config path, a missing
# elevation and a failure half way through all reach the finally below. Anything that cannot be
# removed safely is reported as SECRET_FILE_LEFT_BEHIND -- never silently (S1 re-review, round 3).
# Only the two Import-Module lines above run outside it; without them there is no way to delete
# the file by the rules, and the control host checks for it again after this script exits.
$layout = $null
$installSucceeded = $false
try {

    Assert-Administrator

    # ------------------------------------------------------- definition and layout ---

    $definition = Read-ParallelInstanceDefinition -Path $InstanceDefinitionPath
    $null = Assert-ParallelInstanceDefinition -Definition $definition -AllowRiotCreateDispatch:$AllowRiotCreateDispatch `
        -AllowRiotForeignOrderCancel:$AllowRiotForeignOrderCancel
    Write-Step "Instance definition accepted: $($definition['instanceId'])"

    # Every path and name below comes from Get-ParallelInstanceLayout -- the same source the
    # uninstaller's footprint is derived from, so what an install creates and what an uninstall
    # removes are one list by construction (control-server#262 re-review, M4). Do not read a path
    # or a service/task name out of $definition here: Test-ParallelInstance.ps1 fails on it.
    $layout = Get-ParallelInstanceLayout -Definition $definition

    # The secrets file. Only the layout's own path is accepted, and it must be a plain file: this
    # script deletes it when it finishes, and used to delete whatever path it was handed (S1
    # re-review, round 3). Checked before anything else happens, so a wrong path costs nothing.
    # Install only: -DeploymentConfigPath belongs to the Install parameter set, and -Rollback returns
    # before the try/finally that deletes it.
    if (-not $Rollback) {
        $configRefusal = Test-ParallelInstanceDeploymentConfigPath -Path $DeploymentConfigPath -Layout $layout
        if ($configRefusal) { throw "-DeploymentConfigPath $configRefusal." }
    }
    $serviceName = $layout.ServiceName
    $installRoot = $layout.InstallRoot
    $dataRoot = $layout.DataRoot
    $backupRoot = $layout.BackupRoot
    $packageRoot = $layout.PackageRoot
    $opsRoot = $layout.OpsRoot
    $previousRoot = $layout.PreviousRoot
    $resultRoot = $layout.ResultRoot
    $onboardPort = [int] $definition['onboardPort']
    $healthPort = [int] $definition['healthPort']
    $listenAddress = [string] $definition['listenAddress']
    $healthBindAddress = [string] $definition['healthBindAddress']
    $healthOrigin = "http://${healthBindAddress}:$healthPort"

    # This instance's own name, never the MVP's. Update-ControlServerLocal.ps1 deletes the
    # machine-scope variable it is told about; pointing it at a name only this instance uses is
    # what keeps that deletion from reaching the production deployment.
    $certificatePasswordVariable = $layout.CertificatePasswordVariable

    New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
    $runId = Get-Date -Format 'yyyyMMdd-HHmmss'
    $resultPath = Join-Path $resultRoot "install-$runId.json"
    $diagnosticPath = Join-Path $resultRoot "install-$runId.log"

    $mvpBefore = Get-MvpFingerprint
    Write-Step ("MVP service before: " + (Format-MvpFingerprint $mvpBefore))

    function Get-ServiceEnvironment {
        # This service's registry Environment (REG_MULTI_SZ); empty when the service does not exist.
        $registryKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("SYSTEM\CurrentControlSet\Services\$serviceName", $false)
        if ($null -eq $registryKey) { return [string[]] @() }
        try { return [string[]] @($registryKey.GetValue('Environment', [string[]] @())) } finally { $registryKey.Close() }
    }

    function Set-ServiceEnvironment {
        param([string[]] $Environment)
        $key = "SYSTEM\CurrentControlSet\Services\$serviceName"
        $registryKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($key, $true)
        if ($null -eq $registryKey) { throw "Cannot open the service registry key: $key" }
        try {
            $registryKey.SetValue('Environment', $Environment, [Microsoft.Win32.RegistryValueKind]::MultiString)
        } finally {
            $registryKey.Close()
        }
    }

    # ------------------------------------------------- refusals, before anything changes ---

    # Every check that can refuse runs here, before the installed definition is re-recorded, before a
    # rollback swaps directories, before anything is unpacked -- so "Nothing was stopped or changed"
    # in a refusal is true (control-server#454 incremental review, item 4).

    # The pre-install check, with no condition around it (control-server#454): with the service
    # present, refuse while RIoT dispatch is open (stopping the service would stop the runtime's
    # fault supervision of a vehicle that may be under way, review S3); refuse when the installed
    # configuration is missing or unreadable -- fail closed, instead of a -Rollback that swaps the
    # package directories and only then finds out; and refuse when the file was written after the
    # running process started, or either time is unknown -- a hand edit nobody restarted for is not
    # what the process is doing (incremental review, item 2). Invoke-ParallelProductUpgrade asks
    # about dispatch again right before it stops the service.
    # ReadAllText, not Get-Content -Raw: the latter returns $null for an empty file, which would read
    # as "missing" rather than "unreadable".
    $installedConfigurationPath = Join-Path $installRoot 'appsettings.Production.json'
    $installedConfigurationExists = Test-Path -LiteralPath $installedConfigurationPath -PathType Leaf
    $preInstallRefusal = Get-ParallelPreInstallRefusal -ServiceExists ([bool] (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) `
        -ServiceStatus ([string] (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)?.Status) `
        -ServiceName $serviceName -ConfigurationPath $installedConfigurationPath `
        -ConfigurationText ($installedConfigurationExists ? [IO.File]::ReadAllText($installedConfigurationPath) : $null) `
        -ConfigurationWriteTimeUtc ($installedConfigurationExists ? [IO.File]::GetLastWriteTimeUtc($installedConfigurationPath) : $null) `
        -ProcessStartTimeUtc (Get-ParallelServiceProcessStartTimeUtc -ServiceName $serviceName)
    if ($preInstallRefusal) { throw $preInstallRefusal }

    # A rollback needs a previous generation the product script can install from; checked before the
    # definition is re-recorded and before any directory is swapped (incremental review, item 4).
    if ($Rollback -and -not (Test-Path -LiteralPath (Join-Path $previousRoot 'controlserver') -PathType Container)) {
        throw "No previous generation to roll back to: $previousRoot\controlserver does not exist. Nothing was stopped or changed."
    }

    # The fault recovery credential (control-server#454): the control host's value when it sent one
    # (install only: a rollback has no deploy-config.json), otherwise what the service already holds --
    # read now, before a first install, or a rollback that lands in the product's first-install
    # branch, rebuilds the Environment. With the entry on and neither, this refuses here, before
    # anything changes.
    $config = $Rollback ? $null : (Get-Content -Raw -LiteralPath $DeploymentConfigPath -Encoding utf8 | ConvertFrom-Json)
    $faultRecoveryCredential = Resolve-ParallelFaultRecoveryCredential -Definition $definition `
        -Supplied (($null -ne $config -and $config.PSObject.Properties.Name -contains 'faultRecoveryCredential') ? [string] $config.faultRecoveryCredential : $null) `
        -Carried (Get-ParallelServiceEnvironmentEntry -Environment (Get-ServiceEnvironment) -Name (Get-ParallelInstanceName).FaultRecoveryCredentialVariable)

    # The definition this install (or rollback) runs with, recorded before anything changes. The
    # uninstaller reads this copy in preference to instance.json, which the control host
    # overwrites on every deploy and every rollback: an uninstall must remove what was installed,
    # not what the control host's definition says today (control-server#262 re-review, M2). Written
    # first so that a half-finished first install -- the case the uninstaller is the recovery for --
    # has it too.
    Copy-Item -LiteralPath $InstanceDefinitionPath -Destination $layout.InstalledDefinitionPath -Force
    Write-Step "Installed definition recorded at $($layout.InstalledDefinitionPath)"

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
            # The upgrade script's preflight refuses an installed configuration whose JourneyRuntime is
            # on, and this instance's overlay writes true. Invoke-ParallelProductUpgrade (ParallelHost.psm1)
            # refuses while RIoT dispatch is open, stops the service, sets the flag false so the
            # upgrade's lifecycle check, backup and rollback all hold false, and reports a failure as
            # JOURNEY_RUNTIME_LEFT_DISABLED. Set-InstanceConfiguration writes the definition's value
            # back only after this returned (control-server#454).
            Write-Step "Upgrading $serviceName with Update-ControlServerLocal.ps1"
            $null = Invoke-ParallelProductUpgrade -ConfigurationPath (Join-Path $installRoot 'appsettings.Production.json') -ServiceName $serviceName -Actions @{
                StopService = {
                    Stop-Service -Name $serviceName -Force
                    (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
                }
                InvokeUpdate = {
                    & (Join-Path $scripts 'Update-ControlServerLocal.ps1') `
                        -PackagePath $payload -ResultPath $resultPath -DiagnosticPath $diagnosticPath `
                        -ServiceName $serviceName -InstallRoot $installRoot -DataRoot $dataRoot `
                        -BackupRoot $backupRoot -CertificatePasswordVariable $certificatePasswordVariable `
                        -VerifySafetyProjectionReadOnly
                }
                ServiceStatus = { [string] (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)?.Status }
            }
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

            Run on every path -- first install, upgrade, rollback -- and so is everything the manual
            station clearance exit needs (control-server#454): its two sections in the file, the
            fault recovery credential in the service's Environment, a roster file to point at, and
            a readiness line read back from what the service will actually read. A first install
            rewrites both the file and the Environment; anything merged in by hand used to be lost
            there without a word.
        #>
        param([AllowNull()][AllowEmptyString()][string] $FaultRecoveryCredential)

        $configurationPath = Join-Path $installRoot 'appsettings.Production.json'
        $rosterPath = $layout.FieldOperatorRosterPath
        # Merge and check every overlay value, write the credential, create an empty roster if there
        # is none (never overwriting one), judge the clearance exit -- throwing CLEARANCE_EXIT_BROKEN
        # before any restart -- and only then restart (ParallelHost.psm1).
        $readiness = Invoke-ParallelInstanceConfigurationStep -ConfigurationPath $configurationPath -Definition $definition `
            -Credential $FaultRecoveryCredential -CredentialVariable (Get-ParallelInstanceName).FaultRecoveryCredentialVariable `
            -RosterPath $rosterPath -Actions @{
                GetEnvironment = { Get-ServiceEnvironment }
                SetEnvironment = { param([string[]] $Environment) Set-ServiceEnvironment $Environment }
                RestartService = { Restart-Service -Name $serviceName -Force }
            }
        Write-Step "Configuration overlay merged into $configurationPath and verified (vehicle identity, MesIngest origin, clearance exit sections); credential $([string]::IsNullOrWhiteSpace($FaultRecoveryCredential) ? 'not supplied' : 'written, value not shown'); $serviceName restarted"
        Write-Step $readiness.Line
        Write-Output $readiness.Line
        if (@($readiness.Reasons).Count -gt 0) {
            Write-Warning ("CLEARANCE_EXIT_UNAVAILABLE ($(@($readiness.Reasons) -join ',')): a vehicle that cannot charge stays on " +
                'ORDER_HANG and isolation is not written (server alarms 2271/2272). Intended in phase 1; otherwise fill in ' +
                "the roster at $rosterPath and set vehicleFaultRecovery / fieldOperatorRoles in the instance definition.")
        }
    }

    function Install-FakeMesIngest {
        <#
            The resident demand source. A scheduled task rather than a service, for the reason the
            dashboard is one: the double has no UseWindowsService and the service control manager
            kills an executable that never reports in.
        #>
        param([string] $Zip)

        $fakeInstallRoot = $layout.FakeInstallRoot
        $taskName = $layout.TaskName
        $seedPath = $layout.SeedPath
        $fakePort = [int] $definition['fakeMesIngest']['port']
        $logPath = $layout.FakeLogPath

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
            # Every delete in this script goes through Remove-ParallelInstanceDirectory, which re-checks
            # the exact path (both path layers, and that it is not a junction) instead of relying on
            # the one assertion at the top (control-server#262 S1 re-review, question 4).
            Remove-ParallelInstanceDirectory -Path $fakeInstallRoot
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

        # The task runs the double itself, not a pwsh script: on factory01 a SYSTEM task whose action
        # was pwsh -File Start-FakeMesIngestResident.ps1 never started a PowerShell host
        # (control-server#512). That script now only re-seeds, from the operations directory beside
        # this one, and is no longer copied into the double's directory.
        # The task layer lives in ParallelHost.psm1 so Test-FakeMesIngestScheduledTask.ps1 registers
        # and starts exactly this.
        $action = Get-ParallelFakeMesIngestTaskAction -ExecutablePath $executable -Port $fakePort -WorkingDirectory $fakeInstallRoot
        $registeredAt = Register-ParallelFakeMesIngestTask -TaskName $taskName -Action $action -LogPath $logPath `
            -Description '8005 AGV ControlServer v2 parallel instance: injected MES demand, loopback only'
        Write-Step "Scheduled task '$taskName' registered and started ($($action.Execute) $($action.Argument))"

        $content = Wait-ParallelFakeMesIngestTask -TaskName $taskName -Port $fakePort -ExecutablePath $executable -Since $registeredAt
        Write-Step "FakeMesIngest live: $content"

        # Seeding from here, once. In this task form nothing re-seeds after the double restarts (a
        # reboot, the task's restart): the catalog is then the double's own empty one until somebody
        # runs Start-FakeMesIngestResident.ps1 in the operations directory (README.md).
        $seeded = Invoke-ParallelFakeMesIngestSeed -Port $fakePort -SeedPath $seedPath -LogPath $logPath
        Write-Step "FakeMesIngest seeded from ${seedPath}: $seeded"
    }

    # ---------------------------------------------------------------------- rollback ---

    if ($Rollback) {
        # The previous generation and the credential were checked before anything changed (above).
        Write-Step "Rolling back $serviceName to the package in $previousRoot"

        # Swap rather than copy, so rolling back a rollback is the same operation again.
        $swap = Join-Path $layout.PackageParent ($layout.RollbackFilter.Replace('*', $runId))
        if (Test-Path -LiteralPath $packageRoot) { Move-Item -LiteralPath $packageRoot -Destination $swap }
        Move-Item -LiteralPath $previousRoot -Destination $packageRoot
        if (Test-Path -LiteralPath $swap) { Move-Item -LiteralPath $swap -Destination $previousRoot }

        Invoke-ProductInstaller -PackageDirectory $packageRoot
        # The same proof of completion the install path asks for (incremental review, item 4).
        if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
            throw "The product installer produced no result file at $resultPath."
        }
        Write-Step "Installer result written to $resultPath"
        # The overlay is reapplied: an upgrade keeps the existing appsettings.Production.json, but a
        # rollback to a generation installed before some overlay key existed would otherwise come
        # back without it.
        Set-InstanceConfiguration -FaultRecoveryCredential $faultRecoveryCredential
        Install-FakeMesIngest -Zip ''
        Assert-MvpUntouched -Before $mvpBefore -After (Get-MvpFingerprint)
        Write-Step "Rolled back. Result: $resultPath"
        Write-Step 'The onboard half was NOT rolled back. The two ends do not negotiate versions.'
        return
    }

    # ------------------------------------------------------------------------ verify ---

    # There is no automatic rollback across the steps after the product installer. That installer
    # rolls itself back if *it* fails; everything this script does after it -- overlay, token,
    # double, task, firewall, generation swap -- can fail and leave a half-installed instance, and a
    # first install has no .previous to swap back to. The finally block below says so explicitly
    # rather than leaving an operator to infer it from a stack trace. Recovery is documented in
    # wire-to-gate-parallel-cd.md section 11 and is Uninstall-ParallelInstanceLocal.ps1.
    $completed = $false
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

        # $config (deploy-config.json) and $faultRecoveryCredential were read before anything changed.

        # ------------------------------------------------------------------ unpack ---

        $staged = Join-Path $layout.PackageParent ($layout.IncomingFilter.Replace('*', $runId))
        Remove-ParallelInstanceDirectory -Path $staged
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
        # The user-scope key is shared too: the MVP deployment writes the same variable on every
        # install. Overwriting it is harmless only while both deployments use one key, so say
        # plainly that it happened rather than implying the user scope is this instance's alone.
        Write-Step 'RIoT key written to the user scope (shared with the MVP deployment, same value today); machine scope read, not written'

        # ----------------------------------------------------------------- install ---

        Invoke-ProductInstaller -PackageDirectory $staged

        if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
            throw "The product installer produced no result file at $resultPath."
        }
        Write-Step "Installer result written to $resultPath"

        Set-InstanceConfiguration -FaultRecoveryCredential $faultRecoveryCredential

        # ------------------------------------------------- MesIngest bearer token ---

        # Same reason the MVP path injects it: the product scripts do not, and ControlServer
        # becomes a MesIngest caller the moment JourneyRuntime is enabled -- which for this
        # instance it is. The double does not check the token; the variable is set anyway so that
        # pointing this instance at a real MesIngest later is a configuration change rather than a
        # redeployment.
        if ($config.PSObject.Properties.Name -contains 'mesIngestSharedSecret' -and $config.mesIngestSharedSecret) {
            Set-ServiceEnvironment (Set-ParallelServiceEnvironmentEntry -Environment (Get-ServiceEnvironment) `
                    -Name 'CONTROL_SERVER_MES_INGEST_SHARED_SECRET' -Value ([string] $config.mesIngestSharedSecret))
            Restart-Service -Name $serviceName -Force
            Write-Step 'MesIngest bearer token added to this service''s environment; service restarted'
        }

        # --------------------------------------------------------- fake MES ingest ---

        Install-FakeMesIngest -Zip $FakeMesIngestZip

        # ---------------------------------------------------------------- firewall ---

        # Named after this instance, so that removing it later cannot remove the MVP's rules and so
        # that an operator listing the rules can tell which service each one opens.
        $rulePorts = @($onboardPort, $healthPort)
        for ($i = 0; $i -lt $rulePorts.Count; $i++) {
            $port = $rulePorts[$i]
            $ruleName = $layout.FirewallRules[$i]
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

        Remove-ParallelInstanceDirectory -Path $previousRoot
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
        $completed = $true

    } finally {
        if (-not $completed) {
            Write-Warning 'The parallel install did NOT complete. The instance may be half-installed.'
            Write-Warning 'Nothing was rolled back after the product installer returned. Check the MVP first:'
            Write-Warning ("  MVP now: " + (Format-MvpFingerprint (Get-MvpFingerprint)))
            Write-Warning ("  MVP before: " + (Format-MvpFingerprint $mvpBefore))
            Write-Warning 'Then remove the partial instance with Uninstall-ParallelInstanceLocal.ps1 (see wire-to-gate-parallel-cd.md section 11).'
        }
        # Prefixed with this instance's own leaf name. The unprefixed '*.incoming-*' this used to
        # be matched every sibling of the package root -- and the MVP package root is a sibling
        # (both live in D:\zhengyushao), so each deployment's cleanup deleted the other's staging
        # directory whenever their windows overlapped. The MVP twin, Install-ControlServerRemote.ps1,
        # had the same line and was fixed in the same change (control-server#262 review, finding 1).
        # Each match is re-checked before it is deleted: the filter is ours, but what it matches on
        # the day is the machine's. A refusal here is reported and the rest carries on -- this is
        # best-effort cleanup inside a finally, and a throw from it would hide the real failure.
        Get-ChildItem -LiteralPath $layout.PackageParent -Directory -Filter $layout.IncomingFilter -ErrorAction SilentlyContinue |
            ForEach-Object {
                $directory = $_.FullName
                try { Remove-ParallelInstanceDirectory -Path $directory }
                catch { Write-Warning "Staging cleanup skipped '$directory': $($_.Exception.Message)" }
            }
    }

    $installSucceeded = $true
} finally {
    # -Rollback has no config file: the parameter belongs to the Install parameter set.
    if (-not $Rollback) {
        $residue = Remove-ParallelInstanceDeploymentConfig -Path $DeploymentConfigPath -Layout $layout -FallbackDirectory $PSScriptRoot
        if ($residue) {
            $message = "SECRET_FILE_LEFT_BEHIND: $DeploymentConfigPath still holds the plaintext RIoT API key, MesIngest shared secret and fault recovery credential and must be removed by hand: $residue."
            Write-Warning $message
            # After a failure the warning stands beside the real error; after a success it is the
            # error, because an install that leaves its secrets on disk has not succeeded.
            if ($installSucceeded) { throw $message }
        }
    }
}
