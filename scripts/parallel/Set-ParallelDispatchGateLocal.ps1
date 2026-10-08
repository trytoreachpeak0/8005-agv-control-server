#Requires -Version 7

<#
.SYNOPSIS
    Closes or opens the v2 parallel instance's RIoT dispatch gate (RiotCreateDispatch.enabled) on
    factory01, and restarts its service. Runs ON the factory server.

.DESCRIPTION
    control-server#472. Replaces editing C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json
    by hand and restarting '8005 AGV ControlServer V2', which an upgrade or rollback of this instance
    needs first (UPGRADE_REFUSED_DISPATCH_OPEN, control-server#454). The hand edit had three ways to go
    wrong: the MVP's file and service are one '.V2' away, an editor can re-encode the Chinese agvId, and
    a file edited without a restart leaves the running gate open.

    AUTHORIZATION. This changes a live service on the machine that serves production. Run it only when
    the user has authorized it for that occasion -- in practice, right before an authorized v2 upgrade
    on factory01. It never touches the MVP, but it does stop and start the V2 service.

    The order, the refusals and the checks are Invoke-ParallelDispatchGateChange's (ParallelHost.psm1),
    so Test-ParallelInstance.ps1 proves them without a machine. In short: closing refuses while any
    journey is not Completed, opening while any journey has an order that was or may have been sent to
    RIoT; both read the instance's own SQLite store read-only, once with the service running and once
    after it stopped; the flag is written with the section found ignoring case and the whole file read
    back; the service is restarted and its process must have started after the write. Every refusal
    names the file and the service and says what was and was not changed. The MVP service is
    fingerprinted before and after.

    Which instance. The definition the installer recorded at the last install or rollback,
    <opsRoot>\installed-instance.json, passed as -InstanceDefinitionPath: a gate belongs to what is
    installed, not to what the control host's copy says today. Every name and path comes from
    Get-ParallelInstanceLayout. There is no default: this script runs from its own per-commit directory
    (<opsRoot>\dispatch-gate\<commit>, beside the two modules it imports), where no definition lies.

    Non-interactive use. ConfirmImpact is High, so a manual run prompts; over ssh pass -Confirm:$false
    (20-set-control-server-parallel-dispatch-gate.ps1 does), or -WhatIf to see the plan and what the
    checks say now, without touching anything.

.PARAMETER State
    Closed (RiotCreateDispatch.enabled false) or Open (true).

.PARAMETER InstanceDefinitionPath
    Required: the installed definition, <opsRoot>\installed-instance.json (for the shipped definition,
    D:\zhengyushao\control-server-v2-ops\installed-instance.json). 20-set-control-server-parallel-dispatch-gate.ps1
    passes it.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Closed', 'Open')][string] $State,

    # Not Mandatory, so that a run without it is refused with the explanation below instead of a prompt
    # that hangs a non-interactive ssh session.
    [string] $InstanceDefinitionPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

Import-Module (Join-Path $PSScriptRoot 'ParallelInstance.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ParallelHost.psm1') -Force

function Write-Step {
    param([string] $Message)
    Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Message)
}

if (-not $InstanceDefinitionPath) {
    # No literal path here: Test-ParallelInstance.ps1 holds this script to names and paths from the layout.
    throw ('No -InstanceDefinitionPath. Pass the definition the installer recorded, <opsRoot>\installed-instance.json ' +
        '(the opsRoot of the shipped definition is control-server-v2-ops on D:) -- not a copy beside this script, which ' +
        'runs from its own per-commit directory. 20-set-control-server-parallel-dispatch-gate.ps1 passes it. Nothing was changed.')
}
if (-not (Test-Path -LiteralPath $InstanceDefinitionPath -PathType Leaf)) {
    throw "No installed definition at ${InstanceDefinitionPath}: the v2 instance was never installed through this path, so it has no gate to set. Nothing was changed."
}
# Asserted as for an uninstall: the checks that refuse the MVP's service, paths or ports are the ones
# that matter here. The gate's value in the definition is not a question for this script, nor the
# foreign order cancel gate.
# Pinned by Test-ParallelInstance.ps1: -State Closed is Close, -State Open is Open, and nowhere else is it decided.
# Decided before the assertion, which depends on it (control-server#518).
$direction = ConvertTo-ParallelGateDirection -State $State
$definition = Read-ParallelInstanceDefinition -Path $InstanceDefinitionPath
# -ForStopDirection for Close only: closing must work on an instance installed before taskTypeStations existed,
# with the module 20 copies today; opening such an instance is refused like an install (control-server#518).
$null = Assert-ParallelInstanceDefinition -Definition $definition -AllowRiotCreateDispatch -AllowRiotForeignOrderCancel -ForStopDirection:($direction -ceq 'Close')
$layout = Get-ParallelInstanceLayout -Definition $definition
$serviceName = $layout.ServiceName
$configurationPath = "$($layout.InstallRoot)\appsettings.Production.json"

if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
    throw "No installed configuration at $configurationPath (service '$serviceName'). Nothing was changed."
}
$configuration = Get-Content -LiteralPath $configurationPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12
$databasePath = Resolve-ParallelInstanceDatabasePath -Configuration $configuration -DataRoot $layout.DataRoot -ConfigurationPath $configurationPath

Write-Step "Definition: $InstanceDefinitionPath ($($definition['instanceId']))"
Write-Step "Service: '$serviceName'  configuration: $configurationPath"
Write-Step "Journey state (read-only): $databasePath"
Write-Step "Requested: RiotCreateDispatch.enabled=$($State -eq 'Open' ? 'true' : 'false')"

$actions = @{
    ServiceStatus = {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        $service ? [string] $service.Status : $null
    }
    ReadState = { Get-ParallelJourneyDispatchState -DatabasePath $databasePath -AssemblyDirectory $layout.InstallRoot }
    StopService = { Stop-Service -Name $serviceName -Force }
    StartService = { Start-Service -Name $serviceName }
    ProcessStartTimeUtc = { Get-ParallelServiceProcessStartTimeUtc -ServiceName $serviceName }
}

# The dry run's promise -- nothing stopped, started or written -- rests on this branch. Test-ParallelInstance.ps1
# pins its condition and that it calls nothing that changes the machine: a broken condition here would make
# "-WhatIf -State Open" open the gate for real, and journeys waiting for it would create orders at once.
if (-not $PSCmdlet.ShouldProcess($serviceName, "Set RiotCreateDispatch.enabled=$($State -eq 'Open' ? 'true' : 'false') and restart")) {
    # Read-only preview: what the checks say right now. Nothing is stopped or written.
    $journeyState = Get-ParallelJourneyDispatchState -DatabasePath $databasePath -AssemblyDirectory $layout.InstallRoot
    $refusal = Get-ParallelDispatchGateRefusal -Direction $direction -State $journeyState -ServiceName $serviceName -DatabasePath $databasePath
    Write-Step "WhatIf: service status '$((Get-Service -Name $serviceName -ErrorAction SilentlyContinue)?.Status ?? 'absent')'."
    Write-Step ($refusal ? "WhatIf: would refuse now: $refusal" : 'WhatIf: the journey check would let this through now (it is repeated after the service stops).')
    return
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must run elevated. Nothing was changed.'
}

$mvpBefore = Get-MvpFingerprint
Write-Step ("MVP service before: " + (Format-MvpFingerprint $mvpBefore))
try {
    $result = Invoke-ParallelDispatchGateChange -Direction $direction -ConfigurationPath $configurationPath `
        -ServiceName $serviceName -DatabasePath $databasePath -Actions $actions
    Write-Step $result.Message
    # The verdict comes from what was done, not from what was asked.
    if ($result.Now -ne ($State -eq 'Open')) {
        throw "GATE_STATE_MISMATCH: -State $State was asked for, but RiotCreateDispatch.enabled is now $($result.Now) in $configurationPath."
    }
} finally {
    $mvpAfter = Get-MvpFingerprint
    Write-Step ("MVP service after:  " + (Format-MvpFingerprint $mvpAfter))
    Assert-MvpUntouched -Before $mvpBefore -After $mvpAfter
}
Write-Step ("PASS: RiotCreateDispatch.enabled on '$serviceName' was $($result.Previous ?? '(absent)'), is now $($result.Now)" +
    $(if ($result.Changed) { ', changed by this run' } else { ', unchanged (already so)' }) + '. The MVP service was not involved.')
