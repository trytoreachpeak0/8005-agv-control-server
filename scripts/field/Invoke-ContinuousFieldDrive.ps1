#Requires -Version 7

<#
.SYNOPSIS
    Keeps a vehicle working journey after journey with nobody at it: every pickup loaded, every gate unloaded,
    for as long as the gates stay open. The operator's hands for a production-like run on the slot simulator.

.DESCRIPTION
    Invoke-FullLoopFieldDrive.ps1 plays a window: two journeys, scenes placed on chosen stops, evidence judged at
    the end. This plays none of that. It waits for whatever journey the engine starts, serves it the way a
    diligent operator would, and waits for the next -- the shape of a plant that simply runs. The loop is
    Invoke-FieldContinuousDrive in FieldOperator.psm1, rehearsed by the L2 scenario
    real-onboard-field-continuous-rehearsal; this script adds the preflight, the event log and the stop file.

    What it does NOT do, each on purpose:

      * Dispatch or close the gates. Set-JourneyRuntime.ps1 -Dispatch stays an authorisation given in the
        conversation, and one authorisation covers a continuous run until the gates are closed.
      * Repair anything. A Blocked journey, or one the acts fail on -ResumeAttempts times in a row, ends the drive
        with exit code 2 and the reason in the event log. Settling a journey writes the production database and
        is a separate, authorised act.
      * Judge. It writes an event log, not evidence.
      * Work on the real slot IO module. Its acts put cargo into slots through the simulator; on real IO a basket
        is a basket, and the preflight refuses.

    Stopping. Close the gates the moment the journey in flight completes -- 13-close-gates-when-idle.ps1 exists
    for that race -- then create the stop file. The drive exits 0 once no journey is unresolved. The stop file is
    only read while idle, so it never strands a journey half driven.

    Resuming. The drive picks a journey up wherever it stands -- a stop past its scan has its open load served, a
    gate mid-unload is served from there -- so starting this script again after it died mid-journey is the
    recovery, not a special mode.

    Monitoring. Every step overwrites continuous-drive-heartbeat.json; every event (started, journey-completed,
    journey-failed, stopped, aborted) appends one line to continuous-drive-<yyyyMMdd>.jsonl, both in -LogRoot.

    Run it from PowerShell, not Git Bash: MSYS ssh/scp mangle Windows remote paths.

.EXAMPLE
    pwsh -File .\Invoke-ContinuousFieldDrive.ps1
#>
[CmdletBinding()]
param(
    [string]$VehicleHost = 'agv01',
    [string]$ServerHost = 'factory01',
    [string]$LogRoot = (Join-Path $env:LOCALAPPDATA 'AgvOpsStatus'),
    # Default: continuous-drive.stop in -LogRoot.
    [string]$StopFile,
    # 0 runs until the stop file appears.
    [int]$MaxJourneys = 0,
    [int]$ResumeAttempts = 3,
    # Per pickup stop, drawn independently: the chance that nobody scans (T, the station deadline ends the demand)
    # and that the operator presses 取消装货 before the scan (X). The rest is a plain load. Both suppress the real
    # demand for good, so both default to 0.
    [ValidateRange(0, 100)][int]$NoSublotPercent = 0,
    [ValidateRange(0, 100)][int]$CancelPercent = 0
)
if ($NoSublotPercent + $CancelPercent -gt 100) {
    throw "-NoSublotPercent ($NoSublotPercent) and -CancelPercent ($CancelPercent) add up to more than 100."
}

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Import-Module (Join-Path $PSScriptRoot 'FieldOperator.psm1') -Force

$null = New-Item -ItemType Directory -Path $LogRoot -Force
$StopFile = $StopFile ? $StopFile : (Join-Path $LogRoot 'continuous-drive.stop')
$heartbeatPath = Join-Path $LogRoot 'continuous-drive-heartbeat.json'
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$utf8 = [Text.UTF8Encoding]::new($false)

function Write-DriveEvent([string]$Name, [System.Collections.IDictionary]$Data = @{}) {
    $entry = [ordered]@{ at = [DateTimeOffset]::Now.ToString('o'); runId = $runId; event = $Name }
    foreach ($key in $Data.Keys) { $entry[$key] = $Data[$key] }
    $path = Join-Path $LogRoot "continuous-drive-$([DateTime]::Now.ToString('yyyyMMdd')).jsonl"
    [IO.File]::AppendAllText($path, ($entry | ConvertTo-Json -Compress -Depth 12) + "`n", $utf8)
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $Name $($Data.Count ? ($Data | ConvertTo-Json -Compress -Depth 6) : '')" -ForegroundColor Green
}

function Write-Heartbeat([string]$Message, [string]$Position) {
    $beat = [ordered]@{ at = [DateTimeOffset]::Now.ToString('o'); runId = $runId; pid = $PID; message = $Message; position = $Position }
    try { [IO.File]::WriteAllText($heartbeatPath, ($beat | ConvertTo-Json -Compress), $utf8) } catch { }
}

# --- preflight: read-only, and every refusal before anything is driven -----------------------------------

if (Test-Path -LiteralPath $StopFile) {
    throw "The stop file $StopFile exists, so this drive would stop as soon as it saw no journey. Delete it first."
}

Write-Host "[$(Get-Date -Format 'HH:mm:ss')] Preflight on $VehicleHost and $ServerHost" -ForegroundColor Green
$settingsRaw = & ssh -o BatchMode=yes $VehicleHost "pwsh -NoProfile -Command `"Get-Content -Raw -LiteralPath 'C:\8005\OnboardHmi\appsettings.json'`"" 2>$null
if (-not $settingsRaw) { throw "Could not read C:\8005\OnboardHmi\appsettings.json on $VehicleHost." }
$settings = ($settingsRaw | Out-String) | ConvertFrom-Json -AsHashtable
$ioModule = "$($settings.ioModule.host):$($settings.ioModule.port)"
if ($ioModule -notmatch '^(127\.0\.0\.1|localhost):') {
    throw "$VehicleHost reads IO from $ioModule, not the slot simulator. These acts load through the simulator; on the real module a basket has to be put in by hand."
}
$faceScript = 'remote-ops/onboard-hmi/scripts/15-set-automation-face.ps1'
if (-not $settings.automation.enabled) {
    throw "The onboard automation face is off on $VehicleHost (a deployment resets it). Stop the client, run $faceScript -ReviewReference <the record permitting it>, and start the client again."
}
if ($settings.environment -eq 'Production' -and -not $settings.automation.productionReviewReference) {
    throw "automation.enabled is true on $VehicleHost but automation.productionReviewReference is empty, so the client refuses to start in Production. Re-run $faceScript -ReviewReference <the record permitting it>."
}
$agvId = [string]$settings.agvId

$field = New-FieldOperator -VehicleHost $VehicleHost -ServerHost $ServerHost -AgvId $agvId -Log {
    param($message)
    Write-Host "  [$(Get-Date -Format 'HH:mm:ss')] $message" -ForegroundColor Cyan
    Write-Heartbeat $message $null
}
try {
    $snapshot = Get-FieldOnboardSnapshot -Field $field
} catch {
    throw "$($_.Exception.Message) Start the client with remote-ops/onboard-hmi/scripts/10-start-onboard-stack.ps1, or restart it if its settings were edited while it ran."
}
if ([string]$snapshot.agvId -ne $agvId) { throw "The onboard face reports agvId $($snapshot.agvId), the settings say $agvId." }
$null = Get-FieldSimulatorSlot -Field $field -SlotNo 1

$session = Get-FieldSession -Field $field
if (-not $session -or [string]$session.Readiness -ne 'Ready') {
    # The restart that -Dispatch makes re-establishes the session, so this is a warning, not a refusal.
    Write-Warning "The server holds $agvId's session as $($session ? "$($session.Readiness) ($($session.ReasonCode))" : '(no row)'), not Ready."
}
$inFlight = Get-FieldJourney -Field $field

Write-DriveEvent 'started' ([ordered]@{
        vehicleHost = $VehicleHost; serverHost = $ServerHost; agvId = $agvId; ioModule = $ioModule
        session     = $session ? "$($session.Readiness) generation $($session.SessionGeneration)" : $null
        inFlight    = $inFlight ? "$($inFlight.JourneyId) $($inFlight.CurrentStopSequence)/$($inFlight.Stage)" : $null
        stopFile    = $StopFile; maxJourneys = $MaxJourneys; pid = $PID
    })
Write-Heartbeat 'started' $null

# --- the drive -------------------------------------------------------------------------------------------

$onPoll = {
    param($journey)
    Write-Heartbeat ($journey ? 'driving' : 'idle') ($journey ? "$($journey.JourneyId) $($journey.CurrentStopSequence)/$($journey.Stage)" : $null)
}

$onJourney = {
    param($record)
    $loads = @($record.Acts | Where-Object { $_.Act -in 'N', 'N-RESUMED', 'X', 'T' } |
        ForEach-Object {
            [ordered]@{
                stop        = $_.Sequence; act = $_.Act; sublot = $_.Sublot; status = $_.Status
                suppression = $_.PSObject.Properties['Suppression'] ? $_.Suppression : $null
                resumed     = ($_.Act -eq 'N-RESUMED')
            }
        })
    $unloads = @($record.Acts | Where-Object { $_.Act -eq 'UNLOAD' } | ForEach-Object { @($_.Operations.Values) })
    Write-DriveEvent "journey-$($record.Outcome -eq 'Completed' ? 'completed' : 'failed')" ([ordered]@{
            journeyId = $record.JourneyId; outcome = $record.Outcome; pickedUpAt = $record.PickedUpAt
            startedAt = $record.StartedAt; completedAt = $record.CompletedAt; attempts = $record.Attempts
            loads     = $loads; unloads = $unloads; errors = @($record.Errors)
        })
}

$chooseAct = {
    param($journeyId, $sequence)
    $roll = [Random]::Shared.Next(100)
    $act = ($roll -lt $NoSublotPercent) ? 'T' : (($roll -lt $NoSublotPercent + $CancelPercent) ? 'X' : 'N')
    Write-DriveEvent 'act-chosen' ([ordered]@{ journeyId = $journeyId; stop = $sequence; roll = $roll; act = $act })
    $act
}

$exitCode = 0
try {
    $count = Invoke-FieldContinuousDrive -Field $field -MaxJourneys $MaxJourneys -ResumeAttempts $ResumeAttempts `
        -ShouldStop { Test-Path -LiteralPath $StopFile } -OnPoll $onPoll -OnJourney $onJourney -ChooseAct $chooseAct
    Write-DriveEvent 'stopped' ([ordered]@{
            journeysCompleted = $count
            reason            = ($MaxJourneys -gt 0 -and $count -ge $MaxJourneys) ? 'max-journeys' : 'stop-file'
        })
    Write-Heartbeat 'stopped' $null
} catch {
    Write-DriveEvent 'aborted' ([ordered]@{ error = $_.Exception.Message })
    Write-Heartbeat "aborted: $($_.Exception.Message)" $null
    $exitCode = 2
}
exit $exitCode
