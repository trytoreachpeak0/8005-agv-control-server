#Requires -Version 7

<#
.SYNOPSIS
    Removes the v2 parallel ControlServer instance from factory01, item by item, without
    touching the MVP deployment. Runs ON the factory server.

.DESCRIPTION
    control-server#262 review, finding 6. The product uninstaller removes a service, its install
    root and optionally its data root. Everything else a parallel deployment leaves behind --
    the FakeMesIngest scheduled task and directory, two firewall rules, both package
    generations, the staging directory, the backups, the ops directory, a machine-scope
    variable -- had no removal path. This script walks Get-ParallelInstanceFootprint, the same
    list the installer's names come from, so a thing the installer creates cannot be a thing
    this forgets.

    It is also the documented recovery from a half-finished first install, which has no
    .previous generation to roll back to.

    Three guards, in the order they act:

      1. the definition is asserted first, exactly as for an install -- a definition that
         names the production service, paths or ports is refused before anything is removed;
      2. every directory is checked again, just before deletion, against the production paths.
         Derived paths such as <packageRoot>.previous never appear in the definition, and a
         derivation is not a guarantee;
      3. the MVP service is fingerprinted before and after.

    Kept unless -RemoveData: the data root (the SQLite database), the upgrade backups and the
    ops directory (install results and logs). They are the record of what the instance did.

    Never removed: the user-scope CONTROL_SERVER_RIOT_CALL_API_KEY. The MVP deployment reads
    the same variable on every install; removing it here would break the next MVP deployment.

.PARAMETER InstanceDefinitionPath
    The instance definition this deployment was installed from.

.PARAMETER ConfirmUninstall
    Required. Uninstalling is destructive; the switch makes that an explicit argument, like
    the product uninstaller's.

.PARAMETER RemoveData
    Also remove the data root, the backups and the ops directory.

.EXAMPLE
    pwsh -File Uninstall-ParallelInstanceLocal.ps1 -InstanceDefinitionPath instance.json -ConfirmUninstall -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)]
    [string] $InstanceDefinitionPath,

    [switch] $ConfirmUninstall,

    [switch] $RemoveData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

Import-Module (Join-Path $PSScriptRoot 'ParallelInstance.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ParallelHost.psm1') -Force

function Write-Step {
    param([string] $Message)
    Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Message)
}

if (-not $ConfirmUninstall) {
    throw 'Explicit -ConfirmUninstall is required.'
}

# Asserted even though nothing is being installed: the checks that refuse a definition naming
# the MVP's service, paths or ports are exactly the checks an uninstall most needs.
$definition = Read-ParallelInstanceDefinition -Path $InstanceDefinitionPath
$null = Assert-ParallelInstanceDefinition -Definition $definition -AllowRiotCreateDispatch
$footprint = @(Get-ParallelInstanceFootprint -Definition $definition)
$serviceName = [string] $definition['serviceName']
$fakeInstallRoot = [string] $definition['fakeMesIngest']['installRoot']

Write-Step "Uninstalling $($definition['instanceId']) (service '$serviceName')"
foreach ($item in $footprint) {
    $action = ($item.Data -and -not $RemoveData) ? 'keep (data)' : 'remove'
    Write-Step ("  {0,-8} {1,-18} {2}" -f $action, $item.Kind, $item.Name)
}
Write-Step '  keep     UserEnvironment    CONTROL_SERVER_RIOT_CALL_API_KEY (shared with the MVP deployment)'

$whatIf = -not $PSCmdlet.ShouldProcess($serviceName, 'Uninstall the v2 parallel instance')
if ($whatIf) {
    Write-Step 'WhatIf: nothing removed.'
    return
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must run elevated.'
}

$mvpBefore = Get-MvpFingerprint
Write-Step ("MVP service before: " + (Format-MvpFingerprint $mvpBefore))

$removed = [System.Collections.Generic.List[string]]::new()
$failed = [System.Collections.Generic.List[string]]::new()

function Invoke-Removal {
    param([string] $Label, [scriptblock] $Action)
    try {
        & $Action
        $removed.Add($Label)
    } catch {
        # Carry on: a half-removed instance is better diagnosed from one complete list of what
        # did and did not go than from the first exception.
        $failed.Add("$Label : $($_.Exception.Message)")
    }
}

# ------------------------------------------------------ the double, then the server ---

foreach ($item in $footprint | Where-Object Kind -eq 'ScheduledTask') {
    Invoke-Removal "task '$($item.Name)'" {
        if (Get-ScheduledTask -TaskName $item.Name -ErrorAction SilentlyContinue) {
            Stop-ScheduledTask -TaskName $item.Name -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $item.Name -Confirm:$false
        }
    }
}
# The task is gone but a detached process it started may not be, and it holds the port. Only
# processes whose executable lives under this instance's own directory are stopped.
Invoke-Removal 'FakeMesIngest process' {
    $prefix = $fakeInstallRoot.TrimEnd('\') + '\'
    Get-Process -Name 'ControlServer.FakeMesIngest' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force
}

foreach ($item in $footprint | Where-Object Kind -eq 'Service') {
    Invoke-Removal "service '$($item.Name)' + install root (product uninstaller)" {
        if (-not (Get-Service -Name $item.Name -ErrorAction SilentlyContinue)) { return }
        # Three places, in order. The installed package's own copy matches what is installed;
        # but a first install that failed after the product installer succeeded has no package
        # root at all -- the package was still in <packageRoot>.incoming-*, which the
        # installer's finally block removed -- and that half-installed case is exactly the one
        # this script is the documented recovery for. So the control host also ships the
        # product uninstaller beside this script every time (19-deploy-control-server-parallel.ps1).
        $packageRoot = [string] $definition['packageRoot']
        $candidates = @(
            (Join-Path $packageRoot 'scripts\Uninstall-ControlServerLocal.ps1')
            (Join-Path "$packageRoot.previous" 'scripts\Uninstall-ControlServerLocal.ps1')
            (Join-Path $PSScriptRoot 'Uninstall-ControlServerLocal.ps1')
        )
        $productUninstaller = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if (-not $productUninstaller) {
            throw "The product uninstaller was not found at any of: $($candidates -join '; '). Remove the service with sc.exe delete by hand."
        }
        Write-Step "  using product uninstaller $productUninstaller"
        $resultPath = Join-Path ([string] $definition['opsRoot']) ("uninstall-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))
        New-Item -ItemType Directory -Path (Split-Path -Parent $resultPath) -Force | Out-Null
        # The data root is passed so the product script can report on it; it is removed only
        # with -RemoveData, below, through the same production-path check as everything else.
        & $productUninstaller -ServiceName $item.Name `
            -InstallRoot ([string] $definition['installRoot']) -DataRoot ([string] $definition['dataRoot']) `
            -ResultPath $resultPath -ConfirmUninstall
    }
}

foreach ($item in $footprint | Where-Object Kind -eq 'FirewallRule') {
    Invoke-Removal "firewall rule '$($item.Name)'" {
        Remove-NetFirewallRule -DisplayName $item.Name -ErrorAction SilentlyContinue
    }
}

foreach ($item in $footprint | Where-Object Kind -eq 'MachineEnvironment') {
    Invoke-Removal "machine variable $($item.Name)" {
        $names = Get-ParallelInstanceName
        if ($item.Name -ceq $names.ProductionCertificatePasswordVariable) {
            throw "refusing to remove $($item.Name): it is the MVP's variable"
        }
        [Environment]::SetEnvironmentVariable($item.Name, $null, 'Machine')
    }
}

# ----------------------------------------------------------------- directories ---

foreach ($item in $footprint | Where-Object Kind -eq 'Directory') {
    if ($item.Data -and -not $RemoveData) { continue }
    Invoke-Removal "directory $($item.Name)" {
        if (Test-ParallelInstancePathIsProduction -Path $item.Name) {
            throw 'refusing: this path equals, contains or sits inside a production path'
        }
        if (Test-Path -LiteralPath $item.Name) {
            Remove-Item -LiteralPath $item.Name -Recurse -Force
        }
    }
}

# ------------------------------------------------------------------------ report ---

Assert-MvpUntouched -Before $mvpBefore -After (Get-MvpFingerprint)
Write-Step 'MVP service unchanged.'
foreach ($line in $removed) { Write-Step "  removed  $line" }
foreach ($line in $failed) { Write-Warning "  FAILED   $line" }
if ($failed.Count -gt 0) {
    throw "$($failed.Count) item(s) could not be removed; see above."
}
Write-Step 'Parallel instance uninstalled.'
