#Requires -Version 7

<#
.SYNOPSIS
    Removes the v2 parallel ControlServer instance from factory01, item by item, without
    touching the MVP deployment. Runs ON the factory server.

.DESCRIPTION
    control-server#262. The product uninstaller removes a service, its install root and
    optionally its data root. Everything else a parallel deployment leaves behind -- the
    FakeMesIngest task and directory, two firewall rules, both package generations, leftover
    staging directories, the backups, the ops directory, a machine-scope variable -- is walked
    here from Get-ParallelInstanceFootprint, which is derived from the same layout the installer
    takes every path and name from.

    Also the documented recovery from a half-finished first install, which has no .previous
    generation to roll back to.

    Which definition. By default the one recorded by the installer at the start of the last
    install or rollback (installed-instance.json beside this script), and only if that is absent
    the instance.json the control host last copied. The control host overwrites instance.json on
    every deploy; an uninstall must remove what was installed, not what today's definition says
    (control-server#262 re-review, M2).

    What stops it. The order and the stop conditions are Invoke-ParallelRemovalSequence's, not
    this script's, so that Test-ParallelInstance.ps1 can prove them without a machine:

      * the definition is asserted first, as for an install;
      * the service goes first, through the product uninstaller, and ANY failure there -- above
        all its "Refusing to uninstall the production deployment" -- aborts everything else
        (control-server#262 re-review, S1: the first version caught that refusal like any other
        error and then deleted the MVP's directories itself);
      * every directory, including each match of a staging glob, must pass the allowlist
        (canonical, directly under a known root, named with the V2 marker) and the production
        denylist immediately before it is deleted, and a refusal stops the directory phase;
      * the MVP service is fingerprinted before and after.

    Kept unless -RemoveData: the data root (SQLite), the upgrade backups and the ops directory.
    Never removed: the user-scope CONTROL_SERVER_RIOT_CALL_API_KEY, which the MVP installer reads.

    Non-interactive use. ConfirmImpact is High, so a manual run prompts. Over a non-interactive
    ssh session a prompt hangs; pass -Confirm:$false there (19-deploy-control-server-parallel.ps1
    does), or -WhatIf to see the plan.

.PARAMETER InstanceDefinitionPath
    Override the definition. Default: installed-instance.json beside this script, else
    instance.json beside this script.

.PARAMETER ConfirmUninstall
    Required. Uninstalling is destructive; the switch makes that an explicit argument.

.PARAMETER RemoveData
    Also remove the data root, the backups and the ops directory.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
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

if (-not $InstanceDefinitionPath) {
    $InstanceDefinitionPath = @(
        (Join-Path $PSScriptRoot 'installed-instance.json')
        (Join-Path $PSScriptRoot 'instance.json')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $InstanceDefinitionPath) {
        throw "No installed-instance.json or instance.json beside $PSCommandPath; pass -InstanceDefinitionPath."
    }
}
Write-Step "Definition: $InstanceDefinitionPath"

# Asserted even though nothing is being installed: the checks that refuse a definition naming
# the MVP's service, paths or ports are exactly the checks an uninstall most needs. The create
# gate is not a question for an uninstall, hence the switch.
$definition = Read-ParallelInstanceDefinition -Path $InstanceDefinitionPath
$null = Assert-ParallelInstanceDefinition -Definition $definition -AllowRiotCreateDispatch
$layout = Get-ParallelInstanceLayout -Definition $definition
$footprint = @(Get-ParallelInstanceFootprint -Definition $definition)

Write-Step "Uninstalling $($definition['instanceId']) (service '$($layout.ServiceName)')"
foreach ($item in $footprint) {
    $action = ($item.Data -and -not $RemoveData) ? 'keep (data)' : 'remove'
    Write-Step ("  {0,-11} {1,-18} {2}" -f $action, $item.Kind, $item.Name)
}
Write-Step '  keep        UserEnvironment    CONTROL_SERVER_RIOT_CALL_API_KEY (shared with the MVP deployment)'

if (-not $PSCmdlet.ShouldProcess($layout.ServiceName, 'Uninstall the v2 parallel instance')) {
    Write-Step 'WhatIf: nothing removed.'
    return
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must run elevated.'
}

$mvpBefore = Get-MvpFingerprint
Write-Step ("MVP service before: " + (Format-MvpFingerprint $mvpBefore))

# ------------------------------------------------------------------ the actions ---
# Only side effects live here. Order, stop conditions and the path guards are
# Invoke-ParallelRemovalSequence's.

$actions = @{
    Service = {
        param($item)
        if (-not (Get-Service -Name $item.Name -ErrorAction SilentlyContinue)) { return }
        # Three places, in order. The installed package's own copy matches what is installed;
        # but a first install that failed after the product installer succeeded has no package
        # root -- the package was still in a staging directory the installer's finally block
        # removed -- and that half-installed case is exactly the one this script is the
        # recovery for. So the control host ships the product uninstaller beside this script too.
        $candidates = @(
            (Join-Path $layout.PackageRoot 'scripts\Uninstall-ControlServerLocal.ps1')
            (Join-Path $layout.PreviousRoot 'scripts\Uninstall-ControlServerLocal.ps1')
            (Join-Path $PSScriptRoot 'Uninstall-ControlServerLocal.ps1')
        )
        $productUninstaller = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if (-not $productUninstaller) {
            throw "the product uninstaller was not found at any of: $($candidates -join '; ')"
        }
        Write-Step "  using product uninstaller $productUninstaller"
        New-Item -ItemType Directory -Path $layout.ResultRoot -Force | Out-Null
        $resultPath = Join-Path $layout.ResultRoot ("uninstall-{0:yyyyMMdd-HHmmss}.json" -f (Get-Date))
        # The data root is named so the product script can report on it; it is removed only with
        # -RemoveData, later, through the same guards as every other directory. A throw from the
        # product script -- including its production refusal -- propagates, and the sequence
        # aborts on it.
        & $productUninstaller -ServiceName $item.Name -InstallRoot $layout.InstallRoot `
            -DataRoot $layout.DataRoot -ResultPath $resultPath -ConfirmUninstall
    }
    ScheduledTask = {
        param($item)
        if (Get-ScheduledTask -TaskName $item.Name -ErrorAction SilentlyContinue) {
            Stop-ScheduledTask -TaskName $item.Name -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $item.Name -Confirm:$false
        }
    }
    Process = {
        param($item)
        # A detached process the task started may outlive it and hold the port. Only processes
        # whose executable lives under this instance's own double directory are stopped.
        $prefix = $layout.FakeInstallRoot.TrimEnd('\') + '\'
        Get-Process -Name 'ControlServer.FakeMesIngest' -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } |
            Stop-Process -Force
    }
    FirewallRule = {
        param($item)
        Remove-NetFirewallRule -DisplayName $item.Name -ErrorAction SilentlyContinue
    }
    MachineEnvironment = {
        param($item)
        if ($item.Name -ceq (Get-ParallelInstanceName).ProductionCertificatePasswordVariable) {
            throw "refusing to remove $($item.Name): it is the MVP's variable"
        }
        [Environment]::SetEnvironmentVariable($item.Name, $null, 'Machine')
    }
    DirectoryPattern = {
        param($item)
        $parent = Split-Path -Parent $item.Name
        $filter = Split-Path -Leaf $item.Name
        @(Get-ChildItem -Path $parent -Directory -Filter $filter -ErrorAction SilentlyContinue | ForEach-Object FullName)
    }
    Directory = {
        param($item)
        if (Test-Path -LiteralPath $item.Name) {
            Remove-Item -LiteralPath $item.Name -Recurse -Force
        }
    }
}

$outcome = Invoke-ParallelRemovalSequence -Footprint $footprint -Actions $actions -RemoveData:$RemoveData

# ------------------------------------------------------------------------ report ---

Assert-MvpUntouched -Before $mvpBefore -After (Get-MvpFingerprint)
Write-Step 'MVP service unchanged.'
foreach ($line in $outcome.Removed) { Write-Step "  removed  $line" }
foreach ($line in $outcome.Failed) { Write-Warning "  FAILED   $line" }
if ($outcome.Aborted) {
    throw "Uninstall ABORTED: $($outcome.AbortReason)"
}
if ($outcome.Failed.Count -gt 0) {
    throw "$($outcome.Failed.Count) item(s) could not be removed; see above."
}
Write-Step 'Parallel instance uninstalled.'
