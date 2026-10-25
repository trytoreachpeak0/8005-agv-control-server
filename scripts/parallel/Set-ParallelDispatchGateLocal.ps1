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
    so Test-ParallelInstance.ps1 proves them without a machine. In short: closing refuses while a
    journey is not Completed on a vehicle that may be under way -- one RIoT does not show standing still
    with no unfinished order, or one holding a fault (8005-workspace#68: a vehicle parked on its charger
    no longer holds it) -- and opening while a journey has an order that was or may have been sent to
    RIoT, unless it is CONFIRMED and RIoT shows that order ended (SUCCESS, FAILED, CANCELED); both read
    the instance's own SQLite store read-only and RIoT (GET only), once with the service running and
    once after it stopped; the flag is written with the section found ignoring case and the whole file read
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

.PARAMETER RecoveryJourney
    8005-workspace#61, with -State Closed only. The journeys vehicles stuck on a slot operation result are recovered
    from, as ConvertTo-ParallelRecoveryJourneyArgument encodes them (base64 of [{ journeyId, agvId, vehicleKey }, ...];
    20 passes one per -RecoveryJourneyId). Closing then lets each through while it is on its vehicle, awaits recovery and
    its vehicle holds no fault; everything else is judged as without it (Get-ParallelDispatchGateRefusal). Opening is not widened: the
    journey ends Completed through control-server#771. Without it nothing changes.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    # Exactly as spelled (8005-workspace#61): 'closed' used to bind and then be taken for Open.
    [Parameter(Mandatory = $true)][ValidateSet('Closed', 'Open', IgnoreCase = $false)][string] $State,

    # Not Mandatory, so that a run without it is refused with the explanation below instead of a prompt
    # that hangs a non-interactive ssh session.
    [string] $InstanceDefinitionPath,

    [string] $RecoveryJourney
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
# 8005-workspace#61: closing only, decoded before anything is read; a malformed argument refuses here.
if ($RecoveryJourney -and $direction -cne 'Close') {
    throw 'GATE_RECOVERY_JOURNEY_CLOSE_ONLY: -RecoveryJourney widens closing the gate only; opening is not widened (8005-workspace#61). Nothing was changed.'
}
$recovery = $RecoveryJourney ? @(ConvertFrom-ParallelRecoveryJourneyArgument -Encoded $RecoveryJourney) : $null
$recoveryJourneyId = $recovery ? @($recovery | ForEach-Object { $_['journeyId'] }) : $null
$definition = Read-ParallelInstanceDefinition -Path $InstanceDefinitionPath
# -ForStopDirection for Close only: closing must work on an instance installed before taskTypeStations existed,
# with the module 20 copies today; opening such an instance is refused like an install (control-server#518).
# -ProductionCutover from the installed record itself (control-server#564): it got there only through an install that carried
# the switch. Opening the gate on a cutover instance is authorized per run like any other opening.
$null = Assert-ParallelInstanceDefinition -Definition $definition -AllowRiotCreateDispatch -AllowRiotForeignOrderCancel -ForStopDirection:($direction -ceq 'Close') `
    -ProductionCutover:(Test-ParallelProductionCutoverDefinition -Definition $definition)
$layout = Get-ParallelInstanceLayout -Definition $definition
$serviceName = $layout.ServiceName
$configurationPath = "$($layout.InstallRoot)\appsettings.Production.json"

if (-not (Test-Path -LiteralPath $configurationPath -PathType Leaf)) {
    throw "No installed configuration at $configurationPath (service '$serviceName'). Nothing was changed."
}
$configuration = Get-Content -LiteralPath $configurationPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12
$databasePath = Resolve-ParallelInstanceDatabasePath -Configuration $configuration -DataRoot $layout.DataRoot -ConfigurationPath $configurationPath
# 8005-workspace#68: RIoT is read where the installed service reads it -- appsettings.json with the Production file over it,
# the key from the service's own Environment (the product installer writes it there) or else the machine scope. A connection
# that cannot be told is not thrown here: every vehicle and every order then judges as not read, and refuses.
$baseConfigurationPath = "$($layout.InstallRoot)\appsettings.json"
$baseConfiguration = (Test-Path -LiteralPath $baseConfigurationPath -PathType Leaf) ?
    (Get-Content -LiteralPath $baseConfigurationPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12) : $null
$serviceRegistry = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("SYSTEM\CurrentControlSet\Services\$serviceName", $false)
$serviceEnvironment = $serviceRegistry ? [string[]] @($serviceRegistry.GetValue('Environment', [string[]] @())) : [string[]] @()
if ($serviceRegistry) { $serviceRegistry.Close() }
$riotConnection = Resolve-ParallelRiotGateConnection -BaseConfiguration $baseConfiguration -Configuration $configuration -ServiceEnvironment $serviceEnvironment `
    -MachineVariable { param([string] $Name) [Environment]::GetEnvironmentVariable($Name, 'Machine') }

Write-Step "Definition: $InstanceDefinitionPath ($($definition['instanceId']))"
Write-Step "Service: '$serviceName'  configuration: $configurationPath"
Write-Step "Journey state (read-only): $databasePath"
Write-Step ($riotConnection.Contains('Error') ? "RIoT: cannot be read -- $($riotConnection['Error']) Every vehicle and order will judge as not read." :
    "RIoT (GET only, with each read of the journey state): $($riotConnection['BaseUrl']), key from $($riotConnection['KeyVariable']) in $($riotConnection['KeySource'])")
Write-Step "Requested: RiotCreateDispatch.enabled=$($State -eq 'Open' ? 'true' : 'false')"
foreach ($named in @($recovery | Where-Object { $_ })) { Write-Step "Recovery journey (workspace#61): $($named['journeyId']) on $($named['agvId']) / $($named['vehicleKey'])" }

$actions = @{
    ServiceStatus = {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        $service ? [string] $service.Status : $null
    }
    # 8005-workspace#68: RIoT with every read of the state, so the read after the stop asks RIoT again too.
    ReadState = {
        Read-ParallelRiotGateFacts -State (Get-ParallelJourneyDispatchState -DatabasePath $databasePath -AssemblyDirectory $layout.InstallRoot -RecoveryJourneyId $recoveryJourneyId) `
            -Direction $direction -Connection $riotConnection
    }
    StopService = { Stop-Service -Name $serviceName -Force }
    StartService = { Start-Service -Name $serviceName }
    ProcessStartTimeUtc = { Get-ParallelServiceProcessStartTimeUtc -ServiceName $serviceName }
}

# The dry run's promise -- nothing stopped, started or written -- rests on this branch. Test-ParallelInstance.ps1
# pins its condition and that it calls nothing that changes the machine: a broken condition here would make
# "-WhatIf -State Open" open the gate for real, and journeys waiting for it would create orders at once.
if (-not $PSCmdlet.ShouldProcess($serviceName, "Set RiotCreateDispatch.enabled=$($State -eq 'Open' ? 'true' : 'false') and restart")) {
    # Read-only preview: what the checks say right now. Nothing is stopped or written.
    $journeyState = Read-ParallelRiotGateFacts -State (Get-ParallelJourneyDispatchState -DatabasePath $databasePath -AssemblyDirectory $layout.InstallRoot -RecoveryJourneyId $recoveryJourneyId) `
        -Direction $direction -Connection $riotConnection
    $refusal = Get-ParallelDispatchGateRefusal -Direction $direction -State $journeyState -ServiceName $serviceName -DatabasePath $databasePath -RecoveryJourney $recovery
    $notInFlight = @(Get-ParallelRecoveryJourneyNotInFlight -State $journeyState -RecoveryJourney $recovery)
    if ($notInFlight.Count -gt 0) { Write-Step "WhatIf: named but not in flight (nothing to waive): $($notInFlight -join ', ')" }
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
# control-server#564 review M3: opening the gate of a production cutover needs the MVP stopped and disabled -- after a
# rollback the MVP drives agv01 again. Closing is never refused.
if ($direction -ceq 'Open' -and (Test-ParallelProductionCutoverDefinition -Definition $definition)) {
    $mvpEvidence = Get-ParallelMvpRuntimeEvidence
    $mvpRefusal = Get-ParallelCutoverMvpRefusal -Fingerprint $mvpBefore -Action 'Opening the gate' `
        -ListeningPorts $mvpEvidence.ListeningPorts -ProcessPaths $mvpEvidence.ProcessPaths
    if ($mvpRefusal) { throw $mvpRefusal }
}
try {
    $result = Invoke-ParallelDispatchGateChange -Direction $direction -ConfigurationPath $configurationPath `
        -ServiceName $serviceName -DatabasePath $databasePath -Actions $actions -RecoveryJourney $recovery
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
