#Requires -Version 7

<#
.SYNOPSIS
    Runs field window two unattended: two real journeys on a vehicle whose IO is its own slot simulator,
    with a charging errand between them, playing every operator hand, taking the collector's checkpoints,
    writing the field record and finalizing the evidence (8005-agv-program#20).

.DESCRIPTION
    The plan, in the order the plant sees it:

      Journey 1   pickup stop 1: the vehicle asks for the sublot and nobody scans (T, decision 7 --
                  the station deadline ends the demand, suppresses it, and the journey moves on);
                  pickup stop 2: 取消装货 before scanning (X); every further pickup loaded normally;
                  at the gate the first slot opened is closed twice with the basket still inside before
                  it is emptied (NE). T and X both come before the first load commits, so the holding
                  limit (ADR-cross-0057, 30 min from the first commit) is not spent on them.
      Restart 1   13-close-gates-when-idle.ps1 stops the service the instant journey 1 completes and
                  Set-JourneyRuntime.ps1 -Off brings it back: a real service restart with the vehicle
                  standing still, which is exactly how docs/defects/20260908-session-recovery-required-
                  never-clears-while-vehicle-idle.md was found. This script waits for the new session to
                  be Ready and measures it (R1).
      Charging    Set-JourneyRuntime.ps1 -Dispatch with a temporary charge-threshold override (trigger just
                  above the battery, resume a few percent higher; the user's decision of 2026-09-11 on #20,
                  because waiting for 20 % would take a day with the gates open). The engine sends the
                  vehicle to the charger, it draws current, reaches the resume level and is released (CH).
      Journey 2   the demand the released vehicle takes, loaded and unloaded normally -- the full loop.
                  Scenes journey 1 could not host (too few pickup stops) are played here instead.
      Restart 2   the same restart once journey 2 completes; Set-JourneyRuntime.ps1 -Off also drops the
                  threshold override, so the window ends on the shipped 20 / 80 (R2).

    What it does NOT do, each on purpose:

      * Dispatch, either time. Set-JourneyRuntime.ps1 -Dispatch stays a per-occasion authorisation given
        in the conversation (RELEASE-CANDIDATE.md section 11). This script says when it is waiting for one.
      * Close the gates. Start 13-close-gates-when-idle.ps1 -Force -JourneyTimeoutSeconds 14400 each time
        this script prints "Journey <id>", never earlier: with no unresolved journey the watcher latches
        onto the newest row -- during the charging errand that is journey 1, already Completed -- and stops
        the service on the spot, errand and all.
      * Open the recovery window. Nothing here needs it: 取消装货 with a pending sublot entry needs only a
        Ready session and the operator id the vehicle carries in its machine environment.

    Run it from PowerShell, not Git Bash: the collector copies the database with scp, and Git Bash's MSYS
    scp mangles Windows remote paths.

.EXAMPLE
    # Before the first authorised -Dispatch; start 13-close-gates-when-idle.ps1 after each "Journey <id>" line:
    .\Invoke-FullLoopFieldDrive.ps1 -EvidenceRoot ..\..\evidence\field\20260912-FW-FL2-unattended
#>
[CmdletBinding()]
param(
    # A new directory. The collector creates it on the first checkpoint and refuses one that exists.
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,

    [string]$VehicleHost = 'agv01',
    [string]$ServerHost = 'factory01',

    [int]$NoSublotStop = 1,
    [int]$CancelStop = 2,
    [int]$NotEmptiedRounds = 2,

    # How long to wait for each authorised dispatch to produce what it should: a journey, or a charging run.
    [int]$JourneyWaitMinutes = 60,
    # From the charging run's creation to its release. The battery climbs about 16 %/h on the pad.
    [int]$ChargeWaitMinutes = 240,
    # From a journey completing to the restarted service holding a Ready session.
    [int]$RestartWaitMinutes = 30,

    # The station deadline the plant runs; the collector judges scenario T against it.
    [double]$SublotWaitMinutes = 5,

    # End after journey 1 and its restart, without the charging errand and journey 2.
    [switch]$SkipCharging,

    [string]$Site = '老厂',
    [string]$StageRoot = (Join-Path $env:USERPROFILE 'w2g-stage\field'),
    [switch]$SkipFinalize
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$EvidenceRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($PWD.ProviderPath, $EvidenceRoot))
$StageRoot = [IO.Path]::GetFullPath([IO.Path]::Combine($PWD.ProviderPath, $StageRoot))
$collector = Join-Path $PSScriptRoot 'Invoke-SlotConvergenceFieldWindow.ps1'
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$runStage = Join-Path $StageRoot "FW-FL2-drive-$runId"

Import-Module (Join-Path $PSScriptRoot 'FieldOperator.psm1') -Force

if ($NoSublotStop -eq $CancelStop) { throw 'Scenario T and scenario X cannot share a stop.' }
if (Test-Path -LiteralPath $EvidenceRoot) {
    throw "$EvidenceRoot exists. A window's evidence goes to a new directory; the collector refuses to append across windows."
}

function Write-Phase([string]$Message) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $Message" -ForegroundColor Green
}

function Invoke-Checkpoint([string]$Label) {
    Write-Phase "checkpoint $Label"
    # Out-Host, not the pipeline: whatever the collector prints must not become part of an act's result.
    & pwsh -NoProfile -File $collector -EvidenceRoot $EvidenceRoot -Checkpoint $Label -WindowId FW-FL2 `
        -ServerHost $ServerHost -VehicleHost $VehicleHost | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Collector checkpoint '$Label' failed with exit code $LASTEXITCODE." }
}
$onCheckpoint = { param($label) Invoke-Checkpoint $label }

function Get-ServerChargeOverride {
    $raw = & ssh -o BatchMode=yes $ServerHost "pwsh -NoProfile -Command `"Get-Content -Raw -LiteralPath 'C:\Program Files\8005 AGV\ControlServer\appsettings.Production.json'`"" 2>$null
    if (-not $raw) { return $null }
    $runtime = (($raw | Out-String) | ConvertFrom-Json -AsHashtable)['JourneyRuntime'] ?? @{}
    if ($null -eq $runtime['chargeTriggerBatteryPercent'] -and $null -eq $runtime['chargeResumeBatteryPercent']) { return $null }
    return @{ trigger = $runtime['chargeTriggerBatteryPercent']; resume = $runtime['chargeResumeBatteryPercent'] }
}

# --- preflight: read-only, and every refusal before anything on the vehicle is touched -----------------

Write-Phase "Preflight on $VehicleHost and $ServerHost"
$settingsRaw = & ssh -o BatchMode=yes $VehicleHost "pwsh -NoProfile -Command `"Get-Content -Raw -LiteralPath 'C:\8005\OnboardHmi\appsettings.json'`"" 2>$null
if (-not $settingsRaw) { throw "Could not read C:\8005\OnboardHmi\appsettings.json on $VehicleHost." }
$settings = ($settingsRaw | Out-String) | ConvertFrom-Json -AsHashtable
$ioModule = "$($settings.ioModule.host):$($settings.ioModule.port)"
if ($ioModule -notmatch '^(127\.0\.0\.1|localhost):') {
    throw "$VehicleHost reads IO from $ioModule, not the slot simulator. Run 09-switch-io-module.ps1 -Simulator first (a deployment resets it)."
}
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
    throw "$($_.Exception.Message) The settings on $VehicleHost already open the face: start the client with remote-ops/onboard-hmi/scripts/10-start-onboard-stack.ps1, or restart it if its settings were edited while it ran."
}
if ([string]$snapshot.agvId -ne $agvId) { throw "The onboard face reports agvId $($snapshot.agvId), the settings say $agvId." }
$null = Get-FieldSimulatorSlot -Field $field -SlotNo 1

$session = Get-FieldSession -Field $field
if (-not $session -or [string]$session.Readiness -ne 'Ready') {
    throw "The server does not hold $agvId's session as Ready ($(($session | ConvertTo-Json -Compress) ?? 'no row'))."
}
$active = Get-FieldJourney -Field $field
if ($active -and -not ([int]$active.CurrentStopSequence -eq 1 -and [string]$active.Stage -eq 'AwaitingPickupArrival')) {
    throw "Journey $($active.JourneyId) is already at $($active.CurrentStopSequence)/$($active.Stage). The plan maps scenes onto stops of a fresh journey; settle that one first."
}
if (Get-ServerChargeOverride) {
    throw "appsettings.Production.json on $ServerHost already carries a charge-threshold override. Journey 1 must run on the shipped thresholds; run Set-JourneyRuntime.ps1 -Off first."
}
Write-Host "  agvId $agvId, IO $ioModule, recovery window $($recoveryWindowOpen ? 'open (not needed)' : 'closed'), session Ready generation $($session.SessionGeneration)"
Write-Warning 'This script neither dispatches nor closes the gates. Start 13-close-gates-when-idle.ps1 after each "Journey <id>" line -- started earlier it closes the gates at once.'

$null = New-Item -ItemType Directory -Path $runStage -Force
Invoke-Checkpoint '00-ready'

# --- driving one journey ----------------------------------------------------------------------------------

$played = [ordered]@{ T = $null; X = $null; NE = $null; NECheckpoint = $null }
$acts = [ordered]@{}

function Invoke-JourneyDrive {
    param([string]$JourneyId, [string]$Tag, [int]$TStop, [int]$XStop)

    $handled = @{}
    $generation = $null
    while ($true) {
        $journey = Get-FieldJourney -Field $field -JourneyId $JourneyId
        $stage = [string]$journey.Stage
        $sequence = [int]$journey.CurrentStopSequence
        # The session generation the restart after this journey is measured against. Read while the journey
        # is still under way, never after Completed: by then the gate watcher may already have restarted the
        # service, and a baseline read after the restart would wait for a generation that never comes.
        if ($stage -eq 'Completed') { return $generation }
        $generation = [long](Get-FieldSession -Field $field).SessionGeneration
        if ($stage -eq 'Blocked') { throw "Journey $JourneyId blocked at stop ${sequence}: $($journey.BlockReasonCode)." }
        if ($stage -eq 'AwaitingSublot' -and -not $handled.ContainsKey($sequence)) {
            $handled[$sequence] = $true
            if (-not $played.T -and $sequence -eq $TStop) {
                Write-Phase "$Tag stop ${sequence}: nobody scans (T)"
                $played.T = Invoke-FieldActNoSublot -Field $field -JourneyId $JourneyId -Sequence $sequence -OnCheckpoint $onCheckpoint
                $acts["$Tag-stop-$sequence"] = $played.T
            } elseif (-not $played.X -and $sequence -eq $XStop) {
                Write-Phase "$Tag stop ${sequence}: 取消装货 before scanning (X)"
                $played.X = Invoke-FieldActCancelBeforeSublot -Field $field -JourneyId $JourneyId -Sequence $sequence -OnCheckpoint $onCheckpoint
                $acts["$Tag-stop-$sequence"] = $played.X
            } else {
                Write-Phase "$Tag stop ${sequence}: load"
                $acts["$Tag-stop-$sequence"] = Invoke-FieldActLoad -Field $field -JourneyId $JourneyId -Sequence $sequence
            }
            continue
        }
        if ($stage -eq 'AwaitingUnloadResult') {
            $rounds = $played.NE ? 0 : $NotEmptiedRounds
            Write-Phase "$Tag gate: unload$($rounds ? " (first slot closed $rounds times with the basket inside: NE)" : '')"
            $unload = Invoke-FieldActUnload -Field $field -JourneyId $JourneyId -NotEmptiedRounds $rounds -OnCheckpoint $onCheckpoint `
                -NotEmptiedCheckpoint "$($Tag.ToLowerInvariant())-ne-reopened"
            if ($unload.NotEmptied) {
                $played.NE = $unload.NotEmptied
                $played.NECheckpoint = $unload.NotEmptiedCheckpoint
            }
            $acts["$Tag-gate"] = $unload
            continue
        }
        Start-Sleep -Seconds 5
    }
}

function Wait-RestartAfter {
    param([string]$JourneyId, [long]$GenerationBefore, [datetimeoffset]$CompletedAt, [string]$Checkpoint)

    Write-Phase "Journey $JourneyId completed; waiting up to $RestartWaitMinutes min for the gate watcher's restart to end in a Ready session"
    $observed = Wait-FieldSessionAfterRestart -Field $field -GenerationBefore $GenerationBefore -TimeoutSeconds ($RestartWaitMinutes * 60) `
        -OnCheckpoint $onCheckpoint -ReadyCheckpoint $Checkpoint
    $startedAt = Get-FieldServerStartedAt -Field $field
    if (-not $startedAt -or $startedAt -lt $CompletedAt) {
        Write-Warning "The service process started at $($startedAt ?? '(unknown)'), not after journey $JourneyId completed at $($CompletedAt.ToString('o')). The new session did not come from the watcher's restart; the collector will judge it as it is."
    }
    $observed | Add-Member -NotePropertyName ServerStartedAt -NotePropertyValue ($startedAt ? $startedAt.ToString('o') : $null)
    $observed | Add-Member -NotePropertyName AfterJourneyId -NotePropertyValue $JourneyId
    return $observed
}

function Save-Acts {
    [IO.File]::WriteAllText((Join-Path $runStage 'acts.json'), ([ordered]@{ acts = $acts; played = $played } | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}

$journeys = [System.Collections.Generic.List[object]]::new()
$restarts = [System.Collections.Generic.List[object]]::new()
$charging = $null
$chargeOverride = $null

try {
    # --- journey 1 ----------------------------------------------------------------------------------------
    Write-Phase "Waiting up to $JourneyWaitMinutes min for the first authorised dispatch"
    $journey = Wait-FieldCondition -Field $field -Description "a journey for $agvId" -TimeoutSeconds ($JourneyWaitMinutes * 60) `
        -Probe { Get-FieldJourney -Field $field } -Until { param($j) $j }
    $journey1 = [string]$journey.JourneyId
    Write-Phase "Journey $journey1"
    $journeys.Add(@{ journeyId = $journey1; role = 'T/X/NE' })

    $generation = Invoke-JourneyDrive -JourneyId $journey1 -Tag 'J1' -TStop $NoSublotStop -XStop $CancelStop
    $journey1CompletedAt = [DateTimeOffset]::UtcNow
    $restarts.Add((Wait-RestartAfter -JourneyId $journey1 -GenerationBefore $generation -CompletedAt $journey1CompletedAt -Checkpoint 'r1-ready'))
    Save-Acts

    if (-not $SkipCharging) {
        # --- the charging errand ------------------------------------------------------------------------
        Write-Phase ("Waiting up to $JourneyWaitMinutes min for the second authorised dispatch, with a charge-threshold override " +
            '(Set-JourneyRuntime.ps1 -Dispatch -ChargeTriggerBatteryPercent <above the battery> -ChargeResumeBatteryPercent <a few % higher>)')
        $charging = Wait-FieldChargingStage -Field $field -Stage AwaitingChargerArrival -After $journey1CompletedAt `
            -TimeoutSeconds ($JourneyWaitMinutes * 60) -OnCheckpoint $onCheckpoint -Checkpoint 'charge-dispatched'
        $chargeOverride = Get-ServerChargeOverride
        Write-Phase "Charging run $($charging.ChargingRunId) at $($charging.TriggeredAtBatteryPercent)%; override $(($chargeOverride | ConvertTo-Json -Compress) ?? '(none)')"
        $charging = Wait-FieldChargingStage -Field $field -Stage Charging -Tracker $charging -TimeoutSeconds 1800 `
            -OnCheckpoint $onCheckpoint -Checkpoint 'charge-charging'
        $charging = Wait-FieldChargingStage -Field $field -Stage Completed -Tracker $charging -TimeoutSeconds ($ChargeWaitMinutes * 60) `
            -OnCheckpoint $onCheckpoint -Checkpoint 'charge-released'
        Write-Phase "Charging run released at $($charging.ReleasedAtBatteryPercent)%"
        Save-Acts

        # --- journey 2 ----------------------------------------------------------------------------------
        $journey = Wait-FieldCondition -Field $field -Description 'the journey the released vehicle takes' -TimeoutSeconds 600 `
            -Probe { Get-FieldJourney -Field $field } -Until { param($j) $j -and [string]$j.JourneyId -ne $journey1 }
        $journey2 = [string]$journey.JourneyId
        Write-Phase "Journey $journey2"
        $journeys.Add(@{ journeyId = $journey2; role = '充电之后' })
        # Scenes journey 1 had no stop for are played here, from the first pickup on.
        $tStop = $played.T ? 0 : 1
        $xStop = $played.X ? 0 : ($played.T ? 1 : 2)
        $generation = Invoke-JourneyDrive -JourneyId $journey2 -Tag 'J2' -TStop $tStop -XStop $xStop
        $journey2CompletedAt = [DateTimeOffset]::UtcNow
        $restarts.Add((Wait-RestartAfter -JourneyId $journey2 -GenerationBefore $generation -CompletedAt $journey2CompletedAt -Checkpoint 'r2-ready'))
    }
} catch {
    Write-Warning "Window aborted: $($_.Exception.Message)"
    try { Invoke-Checkpoint 'aborted' } catch { Write-Warning "Could not take the aborted checkpoint: $($_.Exception.Message)" }
    Save-Acts
    throw
}
Save-Acts

# --- record and verdict --------------------------------------------------------------------------------------

$record = New-FullLoopWindowRecord -AgvId $agvId -IoModule $ioModule -DriverRunId $runId -Site $Site `
    -Journeys $journeys.ToArray() -ActT $played.T -ActX $played.X -NotEmptied $played.NE -NotEmptiedCheckpoint $played.NECheckpoint `
    -Charging $charging -ChargeOverride $chargeOverride -Restarts $restarts.ToArray() -RecoveryWindowOpen $recoveryWindowOpen
$recordPath = Join-Path $runStage 'field-record.json'
[IO.File]::WriteAllText($recordPath, ($record | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
Write-Phase "Field record written to $recordPath"
foreach ($scene in 'T', 'X', 'NE') {
    if (-not $played[$scene]) { Write-Warning "Scene $scene was never played; the collector will judge the record as it is." }
}

if ($SkipFinalize) {
    Write-Phase "Finalize skipped. Run: $collector -EvidenceRoot $EvidenceRoot -Finalize -RecordPath $recordPath -WindowId FW-FL2 -SublotWaitMinutes $SublotWaitMinutes"
    return
}
& pwsh -NoProfile -File $collector -EvidenceRoot $EvidenceRoot -Finalize -RecordPath $recordPath -WindowId FW-FL2 `
    -SublotWaitMinutes $SublotWaitMinutes -ServerHost $ServerHost -VehicleHost $VehicleHost
exit $LASTEXITCODE
