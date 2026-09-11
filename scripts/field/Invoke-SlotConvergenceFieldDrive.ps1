#Requires -Version 7

<#
.SYNOPSIS
    Runs the slot-convergence field window unattended: plays the operator on a real vehicle whose IO is
    its own slot simulator, takes the collector's checkpoints at the moments that matter, writes the
    field record, and finalizes the evidence.

.DESCRIPTION
    Since 2026-09-11 nobody opens a door, loads a box or presses the HMI during an acceptance window
    (8005-agv-program#43). The vehicle still drives its real route; its IO is the slot simulator
    (09-switch-io-module.ps1 -Simulator), and everything a person used to do goes through two loopback
    faces on the vehicle -- the simulator's automation plane and the onboard client's automation face --
    reached over SSH by scripts/field/FieldOperator.psm1. The acts in that module are the ones the L2
    scenarios real-onboard-field-window-rehearsal and real-onboard-field-operator-compensate run on the
    local rig, so what was rehearsed is what runs here.

    The plan is the one 8005-agv-program#19 settled on and #45 inherits: scenario A at pickup stop 1,
    scenario C followed by B at stop 2, every further pickup loaded normally, every slot emptied at the
    gate. The journey's composition belongs to the plant's demands, not to this script, so a journey with
    a single pickup ends the window with B and C never played; the collector then fails SC1-W-01, which
    is the truthful outcome.

    Three things it does NOT do, each on purpose:

      * Dispatch. Set-JourneyRuntime.ps1 -Dispatch stays an authorisation given in the conversation for
        each window (RELEASE-CANDIDATE.md section 11): the vehicle really moves. This script waits for
        the journey that dispatch creates and refuses to start against one already under way.
      * Close the gates when the journey completes. The runtime takes the next demand two seconds after
        Completed, which is less than one SSH round trip; remote-ops/onboard-hmi/scripts/
        13-close-gates-when-idle.ps1 watches from the server itself. Start it alongside.
      * Compensation. Compensating one demand of a multi-demand journey leaves the journey Blocked for
        good -- JourneyRuntimeEngine returns on Blocked, and the recovery coordinator only moves the stage
        when the whole journey is settled -- so pressing 补偿清空 mid-window would strand the other stops'
        cargo aboard. Invoke-FieldActCompensate exists and is rehearsed; this runner does not call it until
        that is fixed.

    Run it from PowerShell, not Git Bash: the collector copies the database with scp, and Git Bash's MSYS
    scp mangles Windows remote paths into "No such file or directory".

.EXAMPLE
    # With 13-close-gates-when-idle.ps1 running, and before the authorised -Dispatch:
    .\Invoke-SlotConvergenceFieldDrive.ps1 -EvidenceRoot ..\..\evidence\field\20260912-FW-SC1-unattended
#>
[CmdletBinding()]
param(
    # A new directory. The collector creates it on the first checkpoint and refuses one that exists.
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,

    [string]$VehicleHost = 'agv01',
    [string]$ServerHost = 'factory01',

    [int]$ScenarioAStop = 1,
    [int]$ScenarioCBStop = 2,
    [int]$ReopenRounds = 2,

    # Past the station deadline, before the door is closed. Twenty minutes is the ticket's number; the
    # collector judges SC1-C-04 against the same value and flags anything lower as a rehearsal.
    [double]$HoldMinutes = 20,

    # How long to wait for the authorised dispatch to produce a journey.
    [int]$JourneyWaitMinutes = 60,

    [string]$Site = '老厂',
    [string]$StageRoot = (Join-Path $env:USERPROFILE 'w2g-stage\field'),

    # Take every checkpoint and write the record, but leave -Finalize to a person reading the window.
    [switch]$SkipFinalize
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$EvidenceRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($PWD.ProviderPath, $EvidenceRoot))
$StageRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($PWD.ProviderPath, $StageRoot))
$collector = Join-Path $PSScriptRoot 'Invoke-SlotConvergenceFieldWindow.ps1'
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$runStage = Join-Path $StageRoot "FW-SC1-drive-$runId"

Import-Module (Join-Path $PSScriptRoot 'FieldOperator.psm1') -Force

if ($ScenarioAStop -eq $ScenarioCBStop) { throw 'Scenario A and scenario C/B cannot share a stop.' }
if (Test-Path -LiteralPath $EvidenceRoot) {
    throw "$EvidenceRoot exists. A window's evidence goes to a new directory; the collector refuses to append across windows."
}

function Write-Phase([string]$Message) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $Message" -ForegroundColor Green
}

function Invoke-Checkpoint([string]$Label) {
    Write-Phase "checkpoint $Label"
    & pwsh -NoProfile -File $collector -EvidenceRoot $EvidenceRoot -Checkpoint $Label -ServerHost $ServerHost -VehicleHost $VehicleHost
    if ($LASTEXITCODE -ne 0) { throw "Collector checkpoint '$Label' failed with exit code $LASTEXITCODE." }
}

# --- preflight: read-only, and every refusal before anything on the vehicle is touched -----------------

Write-Phase "Preflight on $VehicleHost and $ServerHost"
$settingsRaw = & ssh -o BatchMode=yes $VehicleHost "pwsh -NoProfile -Command `"Get-Content -Raw -LiteralPath 'C:\8005\OnboardHmi\appsettings.json'`"" 2>$null
if (-not $settingsRaw) { throw "Could not read C:\8005\OnboardHmi\appsettings.json on $VehicleHost." }
$settings = ($settingsRaw | Out-String) | ConvertFrom-Json -AsHashtable
$ioModule = "$($settings.ioModule.host):$($settings.ioModule.port)"
if ($ioModule -notmatch '^(127\.0\.0\.1|localhost):') {
    # On the real module the cargo is real cargo and the door a real solenoid; there are no hands to
    # automate. A redeployment renders the IO back to the real module, which is exactly when this bites.
    throw "$VehicleHost reads IO from $ioModule, not the slot simulator. Run 09-switch-io-module.ps1 -Simulator first (a deployment resets it)."
}
# The face ships shut and every deployment renders it shut again. Opening it is one command in the
# workspace's remote-ops, with the client stopped; naming that command here saves a window from
# discovering it one act later (8005-agv-program#50).
$faceScript = 'remote-ops/onboard-hmi/scripts/15-set-automation-face.ps1'
if (-not $settings.automation.enabled) {
    throw "The onboard automation face is off on $VehicleHost (automation.enabled false; a deployment resets it). Stop the client, run $faceScript -ReviewReference <the record permitting it>, and start the client again."
}
if ($settings.environment -eq 'Production' -and -not $settings.automation.productionReviewReference) {
    throw "automation.enabled is true on $VehicleHost but automation.productionReviewReference is empty, so the client refuses to start in Production. Stop the client and re-run $faceScript -ReviewReference <the record permitting it>."
}
$recoveryWindowOpen = [bool]$settings.wireToGate.recoveryResumeEnabled
$agvId = [string]$settings.agvId

$field = New-FieldOperator -VehicleHost $VehicleHost -ServerHost $ServerHost -AgvId $agvId
try {
    $snapshot = Get-FieldOnboardSnapshot -Field $field
} catch {
    # The settings above say the face is on, so what is left is a client that is not running, or one
    # whose settings were edited by hand while it ran ($faceScript itself refuses a running client).
    throw "$($_.Exception.Message) The settings on $VehicleHost already open the face: start the client with remote-ops/onboard-hmi/scripts/10-start-onboard-stack.ps1, or restart it if its settings were edited while it ran."
}
if ([string]$snapshot.agvId -ne $agvId) { throw "The onboard face reports agvId $($snapshot.agvId), the settings say $agvId." }
$null = Get-FieldSimulatorSlot -Field $field -SlotNo 1

$session = @(Invoke-FieldQuery -Field $field -Sql "SELECT Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$($agvId.Replace("'", "''"))'")
if ($session.Count -ne 1 -or [string]$session[0].Readiness -ne 'Ready') {
    throw "The server does not hold $agvId's session as Ready ($(($session | ConvertTo-Json -Compress) ?? 'no row')). A window that starts outside Ready fails SC1-W-03 on its first checkpoint."
}
$active = Get-FieldJourney -Field $field
if ($active -and -not ([int]$active.CurrentStopSequence -eq 1 -and [string]$active.Stage -eq 'AwaitingPickupArrival')) {
    throw "Journey $($active.JourneyId) is already at $($active.CurrentStopSequence)/$($active.Stage). The plan maps scenarios onto stops of a fresh journey; settle that one first."
}
Write-Host "  agvId $agvId, IO $ioModule, recovery window $($recoveryWindowOpen ? 'open' : 'CLOSED'), session Ready"
if (-not $recoveryWindowOpen) {
    Write-Warning 'The recovery window is closed; SC1-W-02 will fail. 14-set-recovery-window.ps1 opens it.'
}
Write-Warning 'This script does not close the journey gates. 13-close-gates-when-idle.ps1 must be running.'

$null = New-Item -ItemType Directory -Path $runStage -Force
Invoke-Checkpoint '00-ready'

# --- the journey -----------------------------------------------------------------------------------------

Write-Phase "Waiting up to $JourneyWaitMinutes min for the authorised dispatch"
$journey = Wait-FieldCondition -Field $field -Description "a journey for $agvId" -TimeoutSeconds ($JourneyWaitMinutes * 60) `
    -Probe { Get-FieldJourney -Field $field } -Until { param($j) $j }
$journeyId = [string]$journey.JourneyId
Write-Phase "Journey $journeyId"

$acts = [ordered]@{}
$actA = $null
$actCB = $null
$handled = @{}
try {
    while ($true) {
        $journey = Get-FieldJourney -Field $field -JourneyId $journeyId
        $stage = [string]$journey.Stage
        $sequence = [int]$journey.CurrentStopSequence
        if ($stage -eq 'Completed') { break }
        if ($stage -eq 'Blocked') {
            throw "Journey $journeyId blocked at stop ${sequence}: $($journey.BlockReasonCode)."
        }
        if ($stage -eq 'AwaitingSublot' -and -not $handled.ContainsKey($sequence)) {
            $handled[$sequence] = $true
            if ($sequence -eq $ScenarioAStop) {
                Write-Phase "Stop ${sequence}: scenario A"
                $actA = Invoke-FieldActReopen -Field $field -JourneyId $journeyId -Sequence $sequence -Rounds $ReopenRounds
                $acts["stop-$sequence"] = $actA
            } elseif ($sequence -eq $ScenarioCBStop) {
                Write-Phase "Stop ${sequence}: scenario C, then B ($HoldMinutes min past the deadline)"
                $actCB = Invoke-FieldActDoorLeftOpen -Field $field -JourneyId $journeyId -Sequence $sequence `
                    -HoldMinutes $HoldMinutes -OnCheckpoint { param($label) Invoke-Checkpoint $label }
                $acts["stop-$sequence"] = $actCB
            } else {
                Write-Phase "Stop ${sequence}: load"
                $acts["stop-$sequence"] = Invoke-FieldActLoad -Field $field -JourneyId $journeyId -Sequence $sequence
            }
            continue
        }
        if ($stage -eq 'AwaitingUnloadResult') {
            Write-Phase 'Gate: unload'
            $acts['gate'] = Invoke-FieldActUnload -Field $field -JourneyId $journeyId
            continue
        }
        Start-Sleep -Seconds 5
    }
} catch {
    # The state that stopped the window is the evidence of why; freeze it before anything else happens.
    Write-Warning "Window aborted: $($_.Exception.Message)"
    try { Invoke-Checkpoint 'aborted' } catch { Write-Warning "Could not take the aborted checkpoint: $($_.Exception.Message)" }
    [IO.File]::WriteAllText((Join-Path $runStage 'acts.json'), ($acts | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    throw
}
Write-Phase "Journey $journeyId completed"

# --- record and verdict --------------------------------------------------------------------------------------

$record = New-FieldWindowRecord -AgvId $agvId -IoModule $ioModule -RecoveryWindowOpen $recoveryWindowOpen `
    -DriverRunId $runId -Site $Site -ActA $actA -ActCB $actCB
$recordPath = Join-Path $runStage 'field-record.json'
[IO.File]::WriteAllText($recordPath, ($record | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $runStage 'acts.json'), ($acts | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Write-Phase "Field record written to $recordPath"
if (-not $actA -or -not $actCB) {
    Write-Warning "The journey did not reach every planned stop (A at ${ScenarioAStop}: $([bool]$actA), C/B at ${ScenarioCBStop}: $([bool]$actCB)). The collector will judge the record as it is."
}

if ($SkipFinalize) {
    Write-Phase "Finalize skipped. Run: $collector -EvidenceRoot $EvidenceRoot -Finalize -RecordPath $recordPath -MinimumHoldMinutes $HoldMinutes"
    return
}
& pwsh -NoProfile -File $collector -EvidenceRoot $EvidenceRoot -Finalize -RecordPath $recordPath `
    -MinimumHoldMinutes ([Math]::Min(20, $HoldMinutes)) -ServerHost $ServerHost -VehicleHost $VehicleHost
exit $LASTEXITCODE
