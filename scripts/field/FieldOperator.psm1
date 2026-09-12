#Requires -Version 7

<#
.SYNOPSIS
    Plays every human part of a WIRE_TO_GATE station window -- the operator's two hands, the
    maintenance technician who finds a broken sensor, the administrator who presses a recovery
    button -- so the window runs with nobody at the vehicle.

.DESCRIPTION
    One set of acts, two transports. That is the whole design, and the reason is where the acts get
    proven. A field window costs a real trip and a -Dispatch authorisation; an L2 run costs minutes.
    So the acts are written once, here, and run against either:

        Local    the L2 real-onboard rig on this machine: both automation faces on loopback ports,
                 the server's SQLite file opened read-only in-process.
        Remote   a vehicle and the factory server over SSH: every face call is a short pwsh script
                 executed on the vehicle (both faces bind 127.0.0.1 on purpose), every query runs
                 on the server against its own Microsoft.Data.Sqlite.

    A green L2 run of an act is therefore evidence about the code that goes to the field, not about
    a sibling of it. remote-ops/onboard-hmi/scripts/11-drive-journey.ps1 still drives one plain
    operation; it predates this module and is deliberately not extended.

    What an act waits for is always the vehicle's own statement, never the simulator's physical
    state. Three lessons, each of which cost a run (scripts/l2/README.md items 7, 13 and 14):

      * WAITING_OPERATOR, not doorState OPEN, says the vehicle watched the door settle open. Acting
        on OPEN lands inside the unlock pulse.
      * After a reopen, the next WAITING_OPERATOR -- not the door popping -- says closing counts
        again.
      * Every "did it happen again" baseline is taken BEFORE the action it measures. A baseline read
        after the action can already contain the event, and the wait never ends.

    Acts drive; they do not judge. The evidence collector (Invoke-SlotConvergenceFieldWindow.ps1)
    and the L2 scenarios judge. An act throws only when the world is not in a state the act can
    continue from, and says which state it saw.
#>

Set-StrictMode -Version Latest

# --- the vehicle's two faces -------------------------------------------------------------------------

# Executed verbatim in both transports: in-process for Local, as the body of `ssh <vehicle> pwsh
# -EncodedCommand` for Remote. It is text rather than a function for that reason, and it does not rely
# on StrictMode being off or on.
#
# Both faces take the same three-field envelope on every mutating call -- runId (the face's own),
# commandId and expectedRevision -- and refuse a stale revision with 409 REVISION_CONFLICT. The
# revision is read in the same script, immediately before the call: over SSH a read and a post in
# two round trips are 1.5 s apart, and the onboard revision fingerprints the whole vehicle snapshot,
# so it moves while nobody is doing anything.
$script:FaceSource = @'
function Invoke-FieldFaceCall {
    param(
        [int]$Port,
        [string]$Method,
        [string]$Path,
        [string]$BodyBase64,
        [switch]$Envelope,
        [int]$Attempts = 4
    )
    $base = "http://127.0.0.1:$Port/api/v1"
    $template = if ($BodyBase64) {
        [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($BodyBase64)) | ConvertFrom-Json -AsHashtable
    } else { @{} }
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $body = @{} + $template
        if ($Envelope) {
            $snapshot = (Invoke-WebRequest -Uri "$base/snapshot" -TimeoutSec 20).Content | ConvertFrom-Json -AsHashtable
            $body['runId'] = $snapshot['runId']
            $body['expectedRevision'] = $snapshot['revision']
            $body['commandId'] = [guid]::NewGuid().ToString('D')
        }
        $request = @{ Uri = "$base$Path"; Method = $Method; TimeoutSec = 30; SkipHttpErrorCheck = $true }
        if ($Method -ne 'GET') {
            $request['Body'] = $body | ConvertTo-Json -Compress -Depth 16
            $request['ContentType'] = 'application/json'
        }
        try {
            $response = Invoke-WebRequest @request
        } catch {
            return [ordered]@{ status = 0; error = $_.Exception.Message; sent = $body; attempts = $attempt }
        }
        $parsed = $null
        try { $parsed = $response.Content | ConvertFrom-Json -AsHashtable -Depth 64 } catch { }
        $reason = if ($parsed -is [System.Collections.IDictionary]) { $parsed['reasonCode'] } else { $null }
        # Retrying cannot double-apply: a revision conflict is the face saying nothing was done.
        if ([int]$response.StatusCode -eq 409 -and $reason -eq 'REVISION_CONFLICT' -and $Envelope -and $attempt -lt $Attempts) {
            continue
        }
        return [ordered]@{
            status   = [int]$response.StatusCode
            body     = $parsed
            raw      = ($null -eq $parsed) ? [string]$response.Content : $null
            sent     = $body
            attempts = $attempt
        }
    }
}
'@

$script:QuerySource = @'
function Invoke-FieldServerQuery {
    param([string]$DatabasePath, [string]$SqliteAssembly, [string]$SqlBase64)
    $sql = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($SqlBase64))
    if (-not ('Microsoft.Data.Sqlite.SqliteConnection' -as [type])) { Add-Type -Path $SqliteAssembly }
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$DatabasePath;Mode=ReadOnly")
    $connection.Open()
    try {
        $command = $connection.CreateCommand()
        $command.CommandText = $sql
        $reader = $command.ExecuteReader()
        $rows = [System.Collections.Generic.List[object]]::new()
        while ($reader.Read()) {
            $row = [ordered]@{}
            for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                $row[$reader.GetName($i)] = $reader.IsDBNull($i) ? $null : $reader.GetValue($i)
            }
            $rows.Add($row)
        }
        $reader.Close()
        return , $rows.ToArray()
    } finally {
        $connection.Close()
    }
}
'@

# Everything the remote side prints goes after this marker. ssh folds the server's banner and any
# profile noise into the same stream, and a JSON parse that starts at the first '{' breaks the day a
# banner contains one.
$script:Marker = '@@FIELD-OPERATOR@@'

function Invoke-FieldRemoteScript {
    param([Parameter(Mandatory)][string]$HostAlias, [Parameter(Mandatory)][string]$Script)

    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Script))
    $raw = & ssh -o BatchMode=yes -o ConnectTimeout=15 $HostAlias "pwsh -NoProfile -NonInteractive -EncodedCommand $encoded" 2>&1
    $text = ($raw | Out-String)
    $index = $text.LastIndexOf($script:Marker)
    if ($LASTEXITCODE -ne 0 -or $index -lt 0) {
        throw "ssh $HostAlias failed (exit $LASTEXITCODE): $($text.Trim())"
    }
    return $text.Substring($index + $script:Marker.Length).Trim()
}

function ConvertTo-Base64Utf8([string]$Text) {
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Text))
}

<#
The one object every act takes. Local needs the rig's open SQLite connection and its two ports;
Remote needs nothing but the SSH aliases, because the defaults are the production paths.
#>
function New-FieldOperator {
    [CmdletBinding(DefaultParameterSetName = 'Remote')]
    param(
        [Parameter(Mandatory, ParameterSetName = 'Local')][object]$Connection,
        [Parameter(ParameterSetName = 'Remote')][string]$VehicleHost = 'agv01',
        [Parameter(ParameterSetName = 'Remote')][string]$ServerHost = 'factory01',
        [Parameter(ParameterSetName = 'Remote')][string]$ServerDatabase = 'C:\ProgramData\8005\ControlServer\data\controlserver.db',
        [Parameter(ParameterSetName = 'Remote')][string]$ServerInstallRoot = 'C:\Program Files\8005 AGV\ControlServer',
        [Parameter(Mandatory)][string]$AgvId,
        [int]$SimulatorPort = 58006,
        [int]$AutomationPort = 58007,
        # Called with (message) for every step an act takes. The L2 scenarios hand in the journal.
        [scriptblock]$Log = { param($message) Write-Host "  [$(Get-Date -Format 'HH:mm:ss')] $message" -ForegroundColor Cyan }
    )

    $local = $PSCmdlet.ParameterSetName -eq 'Local'
    return [pscustomobject]@{
        Mode              = $PSCmdlet.ParameterSetName
        Connection        = $local ? $Connection : $null
        VehicleHost       = $local ? $null : $VehicleHost
        ServerHost        = $local ? $null : $ServerHost
        ServerDatabase    = $local ? $null : $ServerDatabase
        ServerInstallRoot = $local ? $null : $ServerInstallRoot
        SqliteAssembly    = $null
        AgvId             = $AgvId
        SimulatorPort     = $SimulatorPort
        AutomationPort    = $AutomationPort
        Log               = $Log
        # Over SSH every probe already costs a round trip; sleeping on top of it only slows the act.
        PollMilliseconds  = $local ? 250 : 0
    }
}

function Write-FieldLog {
    param([Parameter(Mandatory)][object]$Field, [Parameter(Mandatory)][string]$Message)
    # Discarded for the same reason as -OnCheckpoint's output: every act logs, so a Log that returned
    # anything would leak into every act's return value.
    $null = & $Field.Log $Message
}

function Invoke-FieldQuery {
    param([Parameter(Mandatory)][object]$Field, [Parameter(Mandatory)][string]$Sql)

    if ($Field.Mode -eq 'Local') {
        $command = $Field.Connection.CreateCommand()
        $command.CommandText = $Sql
        $reader = $command.ExecuteReader()
        $rows = [System.Collections.Generic.List[object]]::new()
        while ($reader.Read()) {
            $row = [ordered]@{}
            for ($i = 0; $i -lt $reader.FieldCount; $i++) {
                $row[$reader.GetName($i)] = $reader.IsDBNull($i) ? $null : $reader.GetValue($i)
            }
            $rows.Add([pscustomobject]$row)
        }
        $reader.Close()
        $command.Dispose()
        return , $rows.ToArray()
    }

    if (-not $Field.SqliteAssembly) {
        # Resolved once. The install root is several hundred files and the search is most of the cost
        # of a query otherwise.
        $found = Invoke-FieldRemoteScript -HostAlias $Field.ServerHost -Script (
            "`$p = (Get-ChildItem -LiteralPath '$($Field.ServerInstallRoot)' -Filter 'Microsoft.Data.Sqlite.dll' -Recurse | Select-Object -First 1).FullName`n" +
            "'$script:Marker' + `$p")
        if (-not $found) { throw "No Microsoft.Data.Sqlite.dll under $($Field.ServerHost):$($Field.ServerInstallRoot)." }
        $Field.SqliteAssembly = $found
    }
    $script = $script:QuerySource + "`n" +
        "`$rows = Invoke-FieldServerQuery -DatabasePath '$($Field.ServerDatabase)' -SqliteAssembly '$($Field.SqliteAssembly)' -SqlBase64 '$(ConvertTo-Base64Utf8 $Sql)'`n" +
        "'$script:Marker' + (ConvertTo-Json -InputObject @(`$rows) -Depth 6 -Compress)"
    $json = Invoke-FieldRemoteScript -HostAlias $Field.ServerHost -Script $script
    return , @($json | ConvertFrom-Json -Depth 16)
}

<#
Calls one of the two vehicle faces. Returns status, parsed body and what was sent -- the caller reads
reasonCode and decides, because a refusal is sometimes the answer an act is waiting for.
#>
function Invoke-FieldFace {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][ValidateSet('Simulator', 'Onboard')][string]$Face,
        [ValidateSet('GET', 'POST', 'PUT')][string]$Method = 'GET',
        [Parameter(Mandatory)][string]$Path,
        [hashtable]$Body,
        [switch]$Envelope
    )

    $port = ($Face -eq 'Simulator') ? $Field.SimulatorPort : $Field.AutomationPort
    $bodyBase64 = $Body ? (ConvertTo-Base64Utf8 ($Body | ConvertTo-Json -Compress -Depth 16)) : ''
    $call = "Invoke-FieldFaceCall -Port $port -Method $Method -Path '$Path' -BodyBase64 '$bodyBase64'" + ($Envelope ? ' -Envelope' : '')

    if ($Field.Mode -eq 'Local') {
        . ([scriptblock]::Create($script:FaceSource))
        $result = Invoke-Expression $call
        # Same shape as the remote path, which comes back through JSON.
        return ($result | ConvertTo-Json -Depth 32 -Compress | ConvertFrom-Json -Depth 64)
    }
    $json = Invoke-FieldRemoteScript -HostAlias $Field.VehicleHost -Script (
        $script:FaceSource + "`n`$r = $call`n'$script:Marker' + (`$r | ConvertTo-Json -Depth 32 -Compress)")
    return ($json | ConvertFrom-Json -Depth 64)
}

function Get-FieldProperty {
    # StrictMode Latest throws on a missing member, and JSON leaves members out rather than nulling them.
    param([AllowNull()][object]$Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    return $property ? $property.Value : $null
}

function Invoke-FieldSimulatorCommand {
    param([Parameter(Mandatory)][object]$Field, [string]$Method = 'POST', [Parameter(Mandatory)][string]$Path, [hashtable]$Body)

    $result = Invoke-FieldFace -Field $Field -Face Simulator -Method $Method -Path $Path -Body $Body -Envelope
    if ([int]$result.status -lt 200 -or [int]$result.status -ge 300) {
        $detail = (Get-FieldProperty $result 'body') ? ($result.body | ConvertTo-Json -Compress -Depth 8) : (Get-FieldProperty $result 'error') ?? (Get-FieldProperty $result 'raw')
        throw "Simulator $Method $Path -> HTTP $($result.status): $detail"
    }
    return $result.body
}

function Get-FieldSimulatorSlot {
    param([Parameter(Mandatory)][object]$Field, [Parameter(Mandatory)][int]$SlotNo)

    $result = Invoke-FieldFace -Field $Field -Face Simulator -Path '/snapshot'
    if ([int]$result.status -ne 200) {
        throw "The slot simulator's automation plane is not answering on port $($Field.SimulatorPort): $((Get-FieldProperty $result 'error') ?? $result.status)"
    }
    return @($result.body.slots | Where-Object { [int]$_.slotNo -eq $SlotNo })[0]
}

function Get-FieldOnboardSnapshot {
    param([Parameter(Mandatory)][object]$Field)

    $result = Invoke-FieldFace -Field $Field -Face Onboard -Path '/snapshot'
    if ([int]$result.status -ne 200) {
        # The likeliest cause is not a bug: a deployment renders appsettings.json from the production
        # template, and the template ships the face off.
        throw ("The onboard automation face is not answering on port $($Field.AutomationPort) " +
            "($((Get-FieldProperty $result 'error') ?? "HTTP $($result.status)")). It needs automation.enabled " +
            'and a non-placeholder automation.productionReviewReference, and a deployment resets both.')
    }
    return $result.body
}

<#
Polls until Until accepts what Probe returned, and returns that value. A probe that throws counts as
"not yet": over SSH a single dropped round trip is not a reason to fail an act that waits minutes.
The last error is kept, so a wait that never succeeds says why.

Probes see the calling act's variables through dynamic scope, which is why none of them is a
GetNewClosure(): a closure made inside a module is rebound to a fresh dynamic module and loses every
function and $script: variable this module does not export. Measured, not assumed. The price is that a
probe must not use a name this function declares ($Field is the one it shares, and it is the same object).
#>
function Wait-FieldCondition {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Probe,
        [Parameter(Mandatory)][scriptblock]$Until,
        [int]$TimeoutSeconds = 120,
        # Fails the wait immediately, with the value it saw. For states the act cannot come back from.
        [scriptblock]$Abort
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = $null
    $lastError = $null
    while ($true) {
        try {
            $last = & $Probe
            $lastError = $null
        } catch {
            $last = $null
            $lastError = $_.Exception.Message
        }
        if ($null -ne $last) {
            if (& $Until $last) { return $last }
            if ($Abort -and (& $Abort $last)) {
                throw "Gave up waiting for: $Description. Saw a state the act cannot continue from: $($last | ConvertTo-Json -Compress -Depth 6)"
            }
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            $seen = ($null -eq $last) ? "(nothing$($lastError ? "; last error: $lastError" : ''))" : ($last | ConvertTo-Json -Compress -Depth 6)
            throw "Timed out after ${TimeoutSeconds}s waiting for: $Description. Last observed: $seen"
        }
        if ($Field.PollMilliseconds -gt 0) { Start-Sleep -Milliseconds $Field.PollMilliseconds }
    }
}

# --- the server's view -------------------------------------------------------------------------------

function ConvertTo-SqlLiteral([string]$Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

<#
The vehicle's journey. With -JourneyId it is that journey whatever its stage; without it, the newest one
that has not completed -- the plant's store holds every journey the vehicle ever drove.
#>
function Get-FieldJourney {
    param([Parameter(Mandatory)][object]$Field, [string]$JourneyId)

    $filter = $JourneyId ? "JourneyId = $(ConvertTo-SqlLiteral $JourneyId)" `
                         : "AgvId = $(ConvertTo-SqlLiteral $Field.AgvId) AND Stage <> 'Completed'"
    $rows = Invoke-FieldQuery -Field $Field -Sql (
        'SELECT JourneyId, Stage, CurrentStopSequence, BlockReasonCode, LoadingClosedReason FROM JourneyRuntimes ' +
        "WHERE $filter ORDER BY UpdatedAt DESC LIMIT 1")
    return ($rows.Count -eq 0) ? $null : $rows[0]
}

function Get-FieldStopSafetyCheck {
    # The departure safety check a stop is asking under, and the answer it consumed, if any.
    param([Parameter(Mandatory)][object]$Field, [Parameter(Mandatory)][string]$JourneyId, [Parameter(Mandatory)][int]$Sequence)

    $rows = Invoke-FieldQuery -Field $Field -Sql (
        'SELECT PreDepartureSafetyCheckId, PreDepartureSafetyCheckMessageId, ConsumedSafetyResultMessageId FROM JourneyStops ' +
        "WHERE JourneyId = $(ConvertTo-SqlLiteral $JourneyId) AND Sequence = $Sequence")
    return ($rows.Count -eq 0) ? $null : $rows[0]
}

function Get-FieldPosition {
    # "<stop>/<stage>" in one string, so "at stop n in stage s" is one comparison.
    param([Parameter(Mandatory)][object]$Field, [Parameter(Mandatory)][string]$JourneyId)
    $journey = Get-FieldJourney -Field $Field -JourneyId $JourneyId
    return $journey ? "$($journey.CurrentStopSequence)/$($journey.Stage)" : '(no journey)'
}

function Get-FieldStopDemand {
    param([Parameter(Mandatory)][object]$Field, [Parameter(Mandatory)][string]$JourneyId, [Parameter(Mandatory)][int]$Sequence)

    $rows = Invoke-FieldQuery -Field $Field -Sql (
        'SELECT d.DemandId, a.Sublot FROM JourneyDemands d JOIN AcceptedDemands a ON a.DemandId = d.DemandId ' +
        "WHERE d.JourneyId = $(ConvertTo-SqlLiteral $JourneyId) AND d.StopSequence = $Sequence")
    if ($rows.Count -ne 1) {
        # One demand per pickup stop is how the engine composes journeys today. Two at one stop would
        # need the act to know which sublot the vehicle is asking for, and guessing is how a scan lands
        # on the wrong request.
        throw "Stop $Sequence of journey $JourneyId carries $($rows.Count) demands; the acts drive exactly one."
    }
    return $rows[0]
}

function Get-FieldOperation {
    param([Parameter(Mandatory)][object]$Field, [Parameter(Mandatory)][string]$DemandId, [string]$OperationType = 'Load')

    $rows = Invoke-FieldQuery -Field $Field -Sql (
        'SELECT SlotOperationAttemptId, Status FROM StationOperations ' +
        "WHERE DemandId = $(ConvertTo-SqlLiteral $DemandId) AND OperationType = $(ConvertTo-SqlLiteral $OperationType)")
    return ($rows.Count -eq 0) ? $null : $rows[0]
}

<#
What the vehicle reported for one attempt, in arrival order: phase and the slots it named. The LIKE is
a prefilter only -- ProtocolInbox holds every heartbeat the plant ever sent, and parsing all of it per
poll is what 8005-agv-program#42 had to take out of the engine.
#>
function Get-FieldProgress {
    param([Parameter(Mandatory)][object]$Field, [Parameter(Mandatory)][string]$AttemptId)

    $rows = Invoke-FieldQuery -Field $Field -Sql (
        "SELECT RequestJson FROM ProtocolInbox WHERE MessageType = 'OperationProgress' " +
        "AND RequestJson LIKE $(ConvertTo-SqlLiteral "%$AttemptId%") ORDER BY ReceivedAt")
    $events = foreach ($row in $rows) {
        $payload = ([string]$row.RequestJson | ConvertFrom-Json -Depth 32).payload
        if ((Get-FieldProperty $payload 'slotOperationAttemptId') -ne $AttemptId) { continue }
        [pscustomobject]@{
            Phase = [string]$payload.phase
            Slots = @(Get-FieldProperty $payload 'activeUnlockSlots' | ForEach-Object { [int]$_ })
        }
    }
    return , @($events)
}

function Get-FieldPhaseCounts {
    param([Parameter(Mandatory)][object]$Field, [Parameter(Mandatory)][string]$AttemptId)

    $events = Get-FieldProgress -Field $Field -AttemptId $AttemptId
    $waiting = @($events | Where-Object Phase -eq 'WAITING_OPERATOR')
    $lastWaiting = ($waiting.Count -gt 0) ? $waiting[-1] : $null
    return [pscustomobject]@{
        Unlocking   = @($events | Where-Object Phase -eq 'UNLOCKING').Count
        Waiting     = $waiting.Count
        WaitingSlot = ($lastWaiting -and $lastWaiting.Slots.Count -eq 1) ? $lastWaiting.Slots[0] : 0
        Events      = $events
    }
}

<#
Whether the vehicle reopened a slot after a baseline: an UNLOCKING naming it, and AFTER that UNLOCKING a
WAITING_OPERATOR naming it. The order is the point, not the counts. The first version compared counts
against the baseline -- more UNLOCKING and more WAITING_OPERATOR -- and on the L2 rehearsal
(evidence/l2/20260911-real-onboard-field-window-rehearsal-001) a prompt-cadence WAITING_OPERATOR that
the vehicle sends every OperationTimeout landed 290 ms BEFORE the reopen's UNLOCKING. Both counts were
up, the act closed the door while the unlock pulse was still out, the vehicle never saw the lock settle
open, and an honest determinate failure came back UNKNOWN. Without -SlotNo, any slot the UNLOCKING names
that is not in -Exclude. Returns the slot, or 0.
#>
function Get-FieldReopenedSlot {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Events, [int]$BaseCount = 0, [int]$SlotNo = 0, [int[]]$Exclude = @())

    for ($i = $BaseCount; $i -lt $Events.Count; $i++) {
        $entry = $Events[$i]
        if ($entry.Phase -ne 'UNLOCKING' -or $entry.Slots.Count -ne 1) { continue }
        $slot = [int]$entry.Slots[0]
        if (($SlotNo -gt 0 -and $slot -ne $SlotNo) -or $Exclude -contains $slot) { continue }
        for ($j = $i + 1; $j -lt $Events.Count; $j++) {
            if ($Events[$j].Phase -eq 'WAITING_OPERATOR' -and $Events[$j].Slots -contains $slot) { return $slot }
        }
    }
    return 0
}

function Get-FieldUnlockCountForSlot {
    param([Parameter(Mandatory)][object[]]$Events, [Parameter(Mandatory)][int]$SlotNo)
    return @($Events | Where-Object { $_.Phase -eq 'UNLOCKING' -and $_.Slots -contains $SlotNo }).Count
}

$script:TerminalOperationStatuses = @('Committed', 'Failed', 'RecoveryRequired', 'Cancelled')

# --- the operator's hands --------------------------------------------------------------------------------

<#
Puts the cargo state in, lets the light curtain catch up, closes the door.

-NoSettle is for a close the vehicle is about to answer with a reopen. The vehicle re-pulses about
370 ms after it reads the opposite state (8005-agv-program#25), which is less than one SSH round trip,
so a wait for "closed and locked" would usually never see it. Those closes are judged by the vehicle's
reaction instead.
#>
function Invoke-FieldCloseSlot {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][int]$SlotNo,
        [ValidateSet('EMPTY', 'OCCUPIED')][string]$Cargo,
        [switch]$NoSettle
    )

    if ($Cargo) {
        $null = Invoke-FieldSimulatorCommand -Field $Field -Method PUT -Path "/slots/$SlotNo/cargo" -Body @{ state = $Cargo }
        # The vehicle reads the raw light curtain, not the logical cargo state, and the simulator models
        # the feedback delay between them. Closing first leaves the slot reading empty to the vehicle.
        # Polarity is per-slot configuration, so "no LIGHT_CURTAIN_MISMATCH with the feedback settled"
        # is the test, never a raw level.
        $null = Wait-FieldCondition -Field $Field -Description "slot $SlotNo light curtain to settle on $Cargo" -TimeoutSeconds 60 `
            -Probe { Get-FieldSimulatorSlot -Field $Field -SlotNo $SlotNo } `
            -Until { param($s) $s.cargoState -eq $Cargo -and -not $s.lightCurtainFeedbackPending -and @($s.faults) -notcontains 'LIGHT_CURTAIN_MISMATCH' }
    }
    $null = Invoke-FieldSimulatorCommand -Field $Field -Method POST -Path "/slots/$SlotNo/close-door"
    Write-FieldLog $Field "slot ${SlotNo}: door closed$($Cargo ? " with cargo $Cargo" : '')"
    if ($NoSettle) { return $null }

    return Wait-FieldCondition -Field $Field -Description "slot $SlotNo to read closed with the lock feedback settled" -TimeoutSeconds 60 `
        -Probe { Get-FieldSimulatorSlot -Field $Field -SlotNo $SlotNo } `
        -Until { param($s) $s.doorState -eq 'CLOSED' -and -not $s.lockFeedbackPending -and @($s.faults) -notcontains 'LOCK_FEEDBACK_MISMATCH' -and [int]$s.unlockOutputRaw -eq 0 }
}

<#
Serves every slot of one operation the way a diligent operator would -- cargo state in, door closed --
until the server records the operation in a terminal status. Slots are opened one at a time, each
announced by its own WAITING_OPERATOR.

A slot already served that gets unlocked again means the vehicle read the opposite of what was put in.
That is not something to paper over by serving it twice; it throws, naming the slot.
#>
function Invoke-FieldServeOperation {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][string]$DemandId,
        [Parameter(Mandatory)][string]$AttemptId,
        [ValidateSet('Load', 'Unload')][string]$OperationType = 'Load',
        [Parameter(Mandatory)][ValidateSet('EMPTY', 'OCCUPIED')][string]$Cargo,
        # Slot -> UNLOCKING count for that slot when it was served, for slots an earlier step already handled.
        [hashtable]$Served = @{},
        [int]$TimeoutSeconds = 600
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $operation = Get-FieldOperation -Field $Field -DemandId $DemandId -OperationType $OperationType
        if ($operation -and [string]$operation.Status -in $script:TerminalOperationStatuses) {
            Write-FieldLog $Field "$OperationType $AttemptId settled as $($operation.Status)"
            return [pscustomobject]@{ Status = [string]$operation.Status; ServedSlots = @($Served.Keys | Sort-Object) }
        }
        $counts = Get-FieldPhaseCounts -Field $Field -AttemptId $AttemptId
        foreach ($slot in @($Served.Keys)) {
            if ((Get-FieldUnlockCountForSlot -Events $counts.Events -SlotNo $slot) -gt $Served[$slot]) {
                throw "Slot $slot of $OperationType $AttemptId was unlocked again after it was served with $Cargo; the vehicle read the opposite state."
            }
        }
        $slotNo = $counts.WaitingSlot
        if ($slotNo -gt 0 -and -not $Served.ContainsKey($slotNo)) {
            Write-FieldLog $Field "$OperationType ${AttemptId}: vehicle waits on slot $slotNo; serving it with $Cargo"
            $null = Invoke-FieldCloseSlot -Field $Field -SlotNo $slotNo -Cargo $Cargo
            $after = Get-FieldProgress -Field $Field -AttemptId $AttemptId
            $Served[$slotNo] = Get-FieldUnlockCountForSlot -Events $after -SlotNo $slotNo
            continue
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Timed out after ${TimeoutSeconds}s serving $OperationType $AttemptId; status $($operation ? $operation.Status : '(no row)'), waiting slot $slotNo, served $(@($Served.Keys) -join ',')."
        }
        Start-Sleep -Milliseconds ([Math]::Max($Field.PollMilliseconds, 500))
    }
}

# --- acts --------------------------------------------------------------------------------------------------

<#
Waits until one pickup stop asks the operator for a sublot, on both ends, and returns the stop's demand,
its sublot and the station deadline the vehicle was given. Every act at a pickup stop starts here.

It waits for the SERVER to be in this stop's AwaitingSublot before it looks at the vehicle. The vehicle
keeps the previous stop's entry request after a submit, so canSubmitSublot is already true when it
rolls into the next stop, still naming the old sublot (scripts/l2/README.md, multi-demand notes). The
sublot has to be the one this stop's demand carries, and it has to be among what the vehicle expects.
#>
function Wait-FieldSublotRequest {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][string]$JourneyId,
        [Parameter(Mandatory)][int]$Sequence,
        [int]$ArrivalTimeoutSeconds = 1800
    )

    $null = Wait-FieldCondition -Field $Field -Description "stop $Sequence to wait for a sublot" -TimeoutSeconds $ArrivalTimeoutSeconds `
        -Probe { Get-FieldPosition -Field $Field -JourneyId $JourneyId } `
        -Until { param($p) $p -eq "$Sequence/AwaitingSublot" } `
        -Abort { param($p) $p -like '*/Blocked' -or $p -like '*/Completed' }
    $serverWaitingAt = [DateTimeOffset]::UtcNow
    $demand = Get-FieldStopDemand -Field $Field -JourneyId $JourneyId -Sequence $Sequence
    $sublot = [string]$demand.Sublot

    $ready = Wait-FieldCondition -Field $Field -Description "the vehicle to ask for sublot $sublot at stop $Sequence" -TimeoutSeconds 120 `
        -Probe { Get-FieldOnboardSnapshot -Field $Field } `
        -Until { param($s) $s.state.canSubmitSublot -and @($s.state.expectedSublots) -contains $sublot }
    $deadlineAt = Get-FieldProperty (Get-FieldProperty (Get-FieldProperty $ready.state 'wireToGateJourney') 'currentStopWorklist') 'stationDepartureDeadlineAt'

    return [pscustomobject]@{
        Sequence        = $Sequence
        DemandId        = [string]$demand.DemandId
        Sublot          = $sublot
        ServerWaitingAt = $serverWaitingAt.ToString('o')
        DeadlineAt      = $deadlineAt ? ([datetimeoffset]$deadlineAt).ToString('o') : $null
    }
}

<#
Scans at one pickup stop and returns once the vehicle says it is waiting for the operator on the first
slot.
#>
function Start-FieldStopLoad {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][string]$JourneyId,
        [Parameter(Mandatory)][int]$Sequence,
        [int]$ArrivalTimeoutSeconds = 1800
    )

    $request = Wait-FieldSublotRequest -Field $Field -JourneyId $JourneyId -Sequence $Sequence -ArrivalTimeoutSeconds $ArrivalTimeoutSeconds
    $demand = [pscustomobject]@{ DemandId = $request.DemandId }
    $sublot = $request.Sublot
    $deadlineAt = $request.DeadlineAt

    $submit = Invoke-FieldFace -Field $Field -Face Onboard -Method POST -Path '/sublots/submit' -Body @{ sublot = $sublot } -Envelope
    if ([int]$submit.status -ne 200 -or -not (Get-FieldProperty $submit.body 'accepted')) {
        throw "Stop ${Sequence}: the vehicle refused sublot ${sublot}: HTTP $($submit.status) $(Get-FieldProperty $submit.body 'reasonCode') $(Get-FieldProperty $submit 'error')".Trim()
    }
    Write-FieldLog $Field "stop ${Sequence}: submitted sublot $sublot (demand $($demand.DemandId))"

    $operation = Wait-FieldCondition -Field $Field -Description "the load command at stop $Sequence" -TimeoutSeconds 120 `
        -Probe { Get-FieldOperation -Field $Field -DemandId $demand.DemandId } -Until { param($o) $o }
    $attemptId = [string]$operation.SlotOperationAttemptId

    $counts = Wait-FieldCondition -Field $Field -Description "the vehicle to wait for the operator at stop $Sequence" -TimeoutSeconds 120 `
        -Probe { Get-FieldPhaseCounts -Field $Field -AttemptId $attemptId } `
        -Until { param($c) $c.WaitingSlot -gt 0 }
    Write-FieldLog $Field "stop ${Sequence}: vehicle waits on slot $($counts.WaitingSlot) (attempt $attemptId)"

    return [pscustomobject]@{
        Sequence   = $Sequence
        DemandId   = [string]$demand.DemandId
        Sublot     = $sublot
        AttemptId  = $attemptId
        SlotNo     = $counts.WaitingSlot
        DeadlineAt = $deadlineAt ? ([datetimeoffset]$deadlineAt).ToString('o') : $null
    }
}

<#
availableRecoveryActions is exactly the set of recovery-area buttons the HMI shows at this instant -- but
not all of them are a recovery entry. With the recovery window open, 取消装货 (LOAD_CANCELLATION) is
offered during every load in flight and 修正装货 (LOAD_CORRECTION) after one completes: operator business
actions that need no administrator proof. The first rehearsal counted them and judged scenario A red for
an HMI that was behaving exactly as designed. What SC1-A-05 asks -- did the vehicle treat operator
hesitation as something needing an administrator -- is only answered by the protocol's three recovery
actions, the ones the onboard's own OnboardAutomationRecoveryActions lists first.
#>
$script:RecoveryEntryActions = @('RESUME_AFTER_REPAIR', 'COMPENSATE_LOAD_ALL_EMPTY', 'FAULT_CARGO_HANDOFF')

function Get-FieldAvailableRecoveryActions {
    param([Parameter(Mandatory)][object]$Field)
    return , @((Get-FieldOnboardSnapshot -Field $Field).state.availableRecoveryActions | ForEach-Object { [string]$_ })
}

<#
Waits for the vehicle's answer to an empty close: either a reopen (UNLOCKING and then WAITING_OPERATOR
both past their baselines, on the same slot) or a terminal status. Which of the two came is the result.
#>
function Wait-FieldCloseAnswer {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][object]$Load,
        [Parameter(Mandatory)][object]$Before,
        [int]$TimeoutSeconds = 180
    )

    return Wait-FieldCondition -Field $Field -Description "the vehicle's answer to closing slot $($Load.SlotNo) empty" -TimeoutSeconds $TimeoutSeconds `
        -Probe {
            $operation = Get-FieldOperation -Field $Field -DemandId $Load.DemandId
            $counts = Get-FieldPhaseCounts -Field $Field -AttemptId $Load.AttemptId
            [pscustomobject]@{
                Status    = $operation ? [string]$operation.Status : '(no row)'
                Reopened  = (Get-FieldReopenedSlot -Events $counts.Events -BaseCount $Before.Events.Count -SlotNo $Load.SlotNo) -gt 0
                Unlocking = $counts.Unlocking
                Waiting   = $counts.Waiting
            }
        } `
        -Until { param($v) $v.Status -in $script:TerminalOperationStatuses -or $v.Reopened }
}

<#
Scenario A (ADR-cross-0058 decisions 1 and 2): the operator closes the door on an empty slot, the vehicle
reopens it by itself, and again, and then the cargo goes in and the load commits.

The rounds must finish inside the station deadline -- after it the vehicle settles an opposite state
as FAILED, and that is scenario B. So a round is only started with -MinimumSecondsLeft to spare; with
less, the act loads at once and reports how many rounds it managed, rather than turning A into B.
#>
function Invoke-FieldActReopen {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][string]$JourneyId,
        [Parameter(Mandatory)][int]$Sequence,
        [int]$Rounds = 2,
        [int]$MinimumSecondsLeft = 90,
        [int]$ArrivalTimeoutSeconds = 1800
    )

    $load = Start-FieldStopLoad -Field $Field -JourneyId $JourneyId -Sequence $Sequence -ArrivalTimeoutSeconds $ArrivalTimeoutSeconds
    $actionsSeen = [System.Collections.Generic.SortedSet[string]]::new()
    foreach ($a in (Get-FieldAvailableRecoveryActions -Field $Field)) { $null = $actionsSeen.Add($a) }
    $completed = 0
    for ($round = 1; $round -le $Rounds; $round++) {
        if ($load.DeadlineAt -and ([datetimeoffset]$load.DeadlineAt - [DateTimeOffset]::UtcNow).TotalSeconds -lt $MinimumSecondsLeft) {
            Write-FieldLog $Field "stop ${Sequence}: less than ${MinimumSecondsLeft}s to the station deadline; loading after $completed round(s)"
            break
        }
        $before = Get-FieldPhaseCounts -Field $Field -AttemptId $load.AttemptId
        $null = Invoke-FieldCloseSlot -Field $Field -SlotNo $load.SlotNo -NoSettle
        $answer = Wait-FieldCloseAnswer -Field $Field -Load $load -Before $before
        if ($answer.Status -in $script:TerminalOperationStatuses) {
            throw "Stop ${Sequence} round ${round}: an empty close settled the load as $($answer.Status) instead of reopening. That is scenario B, and A cannot continue."
        }
        foreach ($a in (Get-FieldAvailableRecoveryActions -Field $Field)) { $null = $actionsSeen.Add($a) }
        $completed = $round
        Write-FieldLog $Field "stop $Sequence round ${round}: vehicle reopened slot $($load.SlotNo) (UNLOCKING $($before.Unlocking)->$($answer.Unlocking), WAITING_OPERATOR $($before.Waiting)->$($answer.Waiting))"
    }

    $unlockAtServe = Get-FieldUnlockCountForSlot -Events (Get-FieldProgress -Field $Field -AttemptId $load.AttemptId) -SlotNo $load.SlotNo
    $null = Invoke-FieldCloseSlot -Field $Field -SlotNo $load.SlotNo -Cargo OCCUPIED
    $served = Invoke-FieldServeOperation -Field $Field -DemandId $load.DemandId -AttemptId $load.AttemptId -Cargo OCCUPIED `
        -Served @{ $load.SlotNo = $unlockAtServe }
    $final = Get-FieldPhaseCounts -Field $Field -AttemptId $load.AttemptId

    return [pscustomobject]@{
        Act                  = 'A'
        Sequence             = $Sequence
        DemandId             = $load.DemandId
        Sublot               = $load.Sublot
        AttemptId            = $load.AttemptId
        SlotNo               = $load.SlotNo
        DeadlineAt           = $load.DeadlineAt
        RoundsRequested      = $Rounds
        RoundsCompleted      = $completed
        Unlocking            = $final.Unlocking
        Waiting              = $final.Waiting
        RecoveryEntryVisible = @($actionsSeen | Where-Object { $_ -in $script:RecoveryEntryActions }).Count -gt 0
        ActionsSeen          = @($actionsSeen)
        Status               = $served.Status
        ServedSlots          = $served.ServedSlots
    }
}

<#
Scenario C, then B, at one stop (ADR-cross-0058 decisions 4, then 1 and 5).

C: the operator opens the door and walks away. At the deadline the server raises
STATION_TIMEOUT_DOOR_NOT_CLOSED and does not end the stop; -HoldMinutes past the deadline it still has
not. B: the operator comes back and closes the door on the empty slot. Decision 1 wins one more round
and reopens it; the next empty close settles as FAILED, and the journey leaves the stop on its own.

-OnCheckpoint is called with each label at the moment that label names. The collector's judgement of C
rests on those moments -- a settlement writes over the very state they freeze -- so the call is made
before the act touches anything again. Whatever the callback writes to the pipeline is discarded: the
field driver's callback runs the collector, whose output once became part of this act's return value
and broke the record written from it (8005-agv-program#45).
#>
function Invoke-FieldActDoorLeftOpen {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][string]$JourneyId,
        [Parameter(Mandatory)][int]$Sequence,
        [double]$HoldMinutes = 20,
        [scriptblock]$OnCheckpoint = { param($label) },
        [string]$DeadlineCheckpoint = 'c-deadline-reached',
        [string]$StillWaitingCheckpoint = 'c-plus-20min',
        [string]$SettledCheckpoint = 'b-settled',
        [int]$ArrivalTimeoutSeconds = 1800
    )

    $load = Start-FieldStopLoad -Field $Field -JourneyId $JourneyId -Sequence $Sequence -ArrivalTimeoutSeconds $ArrivalTimeoutSeconds
    $walkAway = Get-FieldPhaseCounts -Field $Field -AttemptId $load.AttemptId
    Write-FieldLog $Field "stop ${Sequence}: operator walks away from open slot $($load.SlotNo); deadline $($load.DeadlineAt ?? '(not published)')"

    $expected = "$Sequence/AwaitingLoadResult"
    $alarmTimeout = $load.DeadlineAt ? [int][Math]::Max(120, ([datetimeoffset]$load.DeadlineAt - [DateTimeOffset]::UtcNow).TotalSeconds + 180) : 1800
    $null = Wait-FieldCondition -Field $Field -Description "STATION_TIMEOUT_DOOR_NOT_CLOSED at stop $Sequence" -TimeoutSeconds $alarmTimeout `
        -Probe {
            $j = Get-FieldJourney -Field $Field -JourneyId $JourneyId
            [pscustomobject]@{ Position = "$($j.CurrentStopSequence)/$($j.Stage)"; Block = [string]$j.BlockReasonCode }
        } `
        -Until { param($v) $v.Position -eq $expected -and $v.Block -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED' } `
        -Abort { param($v) $v.Position -ne $expected }
    $alarmAt = [DateTimeOffset]::UtcNow
    Write-FieldLog $Field "stop ${Sequence}: alarm STATION_TIMEOUT_DOOR_NOT_CLOSED raised; checkpoint $DeadlineCheckpoint"
    $null = & $OnCheckpoint $DeadlineCheckpoint

    # Measured from the published deadline when there is one, so "HoldMinutes past the deadline" is true
    # of the deadline and not only of when this script happened to notice the alarm.
    $holdFrom = $load.DeadlineAt ? [datetimeoffset]$load.DeadlineAt : $alarmAt
    $holdUntil = $holdFrom.AddMinutes($HoldMinutes)
    if ($holdUntil -lt $alarmAt) { $holdUntil = $alarmAt }
    while ([DateTimeOffset]::UtcNow -lt $holdUntil) {
        $journey = Get-FieldJourney -Field $Field -JourneyId $JourneyId
        $operation = Get-FieldOperation -Field $Field -DemandId $load.DemandId
        $slot = Get-FieldSimulatorSlot -Field $Field -SlotNo $load.SlotNo
        $state = "$($journey.CurrentStopSequence)/$($journey.Stage)/$($journey.BlockReasonCode)/$($operation.Status)/$($slot.doorState)"
        if ($state -ne "$expected/STATION_TIMEOUT_DOOR_NOT_CLOSED/Prepared/OPEN") {
            throw "Stop ${Sequence}: while the door stood open past the deadline the state changed to $state. Waiting decayed into something else, and that is the finding -- C cannot be held."
        }
        $remaining = ($holdUntil - [DateTimeOffset]::UtcNow).TotalSeconds
        if ($remaining -gt 0) { Start-Sleep -Seconds ([Math]::Min(30, [Math]::Ceiling($remaining))) }
    }
    $stillWaitingAt = [DateTimeOffset]::UtcNow
    Write-FieldLog $Field "stop ${Sequence}: still waiting $HoldMinutes min past the deadline; checkpoint $StillWaitingCheckpoint"
    $null = & $OnCheckpoint $StillWaitingCheckpoint
    $held = Get-FieldPhaseCounts -Field $Field -AttemptId $load.AttemptId

    # B. Decision 1 usually wins one more round after the deadline (#25 measured 370 ms), but not by
    # contract, so the answer to each empty close is read rather than assumed.
    $closes = 0
    $graceReopens = 0
    $status = $null
    while ($null -eq $status) {
        if ($closes -ge 3) {
            throw "Stop ${Sequence}: three empty closes after the deadline and the load has not settled; the vehicle keeps reopening."
        }
        $before = Get-FieldPhaseCounts -Field $Field -AttemptId $load.AttemptId
        $null = Invoke-FieldCloseSlot -Field $Field -SlotNo $load.SlotNo -NoSettle
        $closes++
        $answer = Wait-FieldCloseAnswer -Field $Field -Load $load -Before $before -TimeoutSeconds 300
        if ($answer.Status -in $script:TerminalOperationStatuses) {
            $status = $answer.Status
        } else {
            $graceReopens++
            Write-FieldLog $Field "stop ${Sequence}: empty close $closes answered with a reopen"
        }
    }
    Write-FieldLog $Field "stop ${Sequence}: load settled as $status after $closes empty close(s)"

    $left = Wait-FieldCondition -Field $Field -Description "the journey to leave $expected" -TimeoutSeconds 180 `
        -Probe { Get-FieldPosition -Field $Field -JourneyId $JourneyId } `
        -Until { param($p) $p -ne $expected }
    $null = & $OnCheckpoint $SettledCheckpoint

    return [pscustomobject]@{
        Act                    = 'CB'
        Sequence               = $Sequence
        DemandId               = $load.DemandId
        Sublot                 = $load.Sublot
        AttemptId              = $load.AttemptId
        SlotNo                 = $load.SlotNo
        DeadlineAt             = $load.DeadlineAt
        AlarmObservedAt        = $alarmAt.ToString('o')
        StillWaitingObservedAt = $stillWaitingAt.ToString('o')
        HoldMinutes            = $HoldMinutes
        DeadlineCheckpoint     = $DeadlineCheckpoint
        StillWaitingCheckpoint = $StillWaitingCheckpoint
        SettledCheckpoint      = $SettledCheckpoint
        WalkAwayCounts         = "UNLOCKING=$($walkAway.Unlocking) WAITING_OPERATOR=$($walkAway.Waiting)"
        HeldCounts             = "UNLOCKING=$($held.Unlocking) WAITING_OPERATOR=$($held.Waiting)"
        EmptyCloses            = $closes
        GraceReopens           = $graceReopens
        Status                 = $status
        PositionAfter          = $left
    }
}

<#
A plain load: scan, cargo into every slot the vehicle opens, doors closed.
#>
function Invoke-FieldActLoad {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][string]$JourneyId,
        [Parameter(Mandatory)][int]$Sequence,
        [int]$ArrivalTimeoutSeconds = 1800
    )

    $load = Start-FieldStopLoad -Field $Field -JourneyId $JourneyId -Sequence $Sequence -ArrivalTimeoutSeconds $ArrivalTimeoutSeconds
    $served = Invoke-FieldServeOperation -Field $Field -DemandId $load.DemandId -AttemptId $load.AttemptId -Cargo OCCUPIED
    return [pscustomobject]@{
        Act         = 'N'
        Sequence    = $Sequence
        DemandId    = $load.DemandId
        Sublot      = $load.Sublot
        AttemptId   = $load.AttemptId
        SlotNo      = $load.SlotNo
        Status      = $served.Status
        ServedSlots = $served.ServedSlots
    }
}

<#
Where one demand stands on the server: its status, the suppression reason if it was ended for good, how
many slot operations it ever had, and where its journey is now. Read in one probe, because "the demand
was cancelled" and "the journey left the stop" are written by the engine in one iteration but read here
in two round trips -- judging one and then reading the other is the race scripts/l2/README.md item 14
keeps counting.
#>
function Get-FieldDemandSettlement {
    param([Parameter(Mandatory)][object]$Field, [Parameter(Mandatory)][string]$JourneyId, [Parameter(Mandatory)][string]$DemandId)

    $id = ConvertTo-SqlLiteral $DemandId
    $rows = Invoke-FieldQuery -Field $Field -Sql (
        'SELECT a.Status, ' +
        "(SELECT s.ReasonCode FROM TransportDemandSuppressions s WHERE s.DemandId = $id LIMIT 1) AS Suppression, " +
        "(SELECT COUNT(*) FROM StationOperations o WHERE o.DemandId = $id) AS Operations " +
        "FROM AcceptedDemands a WHERE a.DemandId = $id")
    $journey = Get-FieldJourney -Field $Field -JourneyId $JourneyId
    $row = ($rows.Count -gt 0) ? $rows[0] : $null
    return [pscustomobject]@{
        Status      = $row ? [string]$row.Status : '(no row)'
        Suppression = $row ? [string]$row.Suppression : ''
        Operations  = $row ? [int]$row.Operations : 0
        Position    = $journey ? "$($journey.CurrentStopSequence)/$($journey.Stage)" : '(no journey)'
    }
}

<#
Decision 7 (ADR-cross-0055, SublotWaitTimeout): the vehicle reaches a pickup stop, asks for the sublot,
and nobody ever scans it. The server ends the demand at the station deadline on its own --
CANCELLED_BY_STATION_TIMEOUT, suppressed for good -- and the journey leaves the stop, rather than
holding the vehicle in AwaitingSublot forever.

The act does nothing on purpose once the vehicle has asked. What it waits for is the demand ended AND
suppressed AND the journey somewhere other than this stop's AwaitingSublot, in one probe.
#>
function Invoke-FieldActNoSublot {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][string]$JourneyId,
        [Parameter(Mandatory)][int]$Sequence,
        [scriptblock]$OnCheckpoint = { param($label) },
        [string]$SettledCheckpoint = 't-settled',
        # How long past the published deadline to keep waiting. The engine polls every two seconds; the
        # rest is SSH round trips.
        [int]$GraceSeconds = 180,
        [int]$ArrivalTimeoutSeconds = 1800
    )

    $request = Wait-FieldSublotRequest -Field $Field -JourneyId $JourneyId -Sequence $Sequence -ArrivalTimeoutSeconds $ArrivalTimeoutSeconds
    $vehicleAskedAt = [DateTimeOffset]::UtcNow
    Write-FieldLog $Field "stop ${Sequence}: the vehicle asks for sublot $($request.Sublot) and nobody scans it; deadline $($request.DeadlineAt ?? '(not published)')"

    $waiting = "$Sequence/AwaitingSublot"
    $timeout = $request.DeadlineAt `
        ? [int][Math]::Max(60, ([datetimeoffset]$request.DeadlineAt - [DateTimeOffset]::UtcNow).TotalSeconds + $GraceSeconds) `
        : 1800
    $settled = Wait-FieldCondition -Field $Field -Description "the station deadline to end demand $($request.DemandId) at stop $Sequence" -TimeoutSeconds $timeout `
        -Probe { Get-FieldDemandSettlement -Field $Field -JourneyId $JourneyId -DemandId $request.DemandId } `
        -Until { param($v) $v.Status -eq 'Cancelled' -and $v.Suppression -and $v.Position -ne $waiting } `
        -Abort { param($v) $v.Status -in @('Loaded', 'Succeeded') -or $v.Operations -gt 0 -or $v.Position -like '*/Blocked' }
    $settledAt = [DateTimeOffset]::UtcNow
    Write-FieldLog $Field "stop ${Sequence}: demand $($request.DemandId) $($settled.Status) ($($settled.Suppression)); journey now at $($settled.Position)"
    $null = & $OnCheckpoint $SettledCheckpoint

    return [pscustomobject]@{
        Act               = 'T'
        Sequence          = $Sequence
        DemandId          = $request.DemandId
        Sublot            = $request.Sublot
        ServerWaitingAt   = $request.ServerWaitingAt
        VehicleAskedAt    = $vehicleAskedAt.ToString('o')
        DeadlineAt        = $request.DeadlineAt
        SettledObservedAt = $settledAt.ToString('o')
        Status            = $settled.Status
        Suppression       = $settled.Suppression
        Operations        = $settled.Operations
        PositionAfter     = $settled.Position
        SettledCheckpoint = $SettledCheckpoint
    }
}

<#
取消订单 before anything is loaded: the vehicle asks for the sublot, the operator presses 取消装货 instead.
That button is the onboard's LOAD_CANCELLATION with a pending sublot entry, and it needs no recovery
window -- only a connected Ready session and the operator id the vehicle carries in its machine
environment (WireToGateBusinessService.CanUseStopOperator).

Whether it worked is read from the server, never from the face's answer (8005-agv-program#51): an
authorisation the vehicle did not receive still cancels the demand, and a synchronous refusal says
nothing was sent. So a non-200 is only thrown once the server has had time to show it did nothing.
#>
function Invoke-FieldActCancelBeforeSublot {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][string]$JourneyId,
        [Parameter(Mandatory)][int]$Sequence,
        [string]$Reason = '现场窗口二：本站没有货，扫码前取消本站装货',
        [scriptblock]$OnCheckpoint = { param($label) },
        [string]$SettledCheckpoint = 'x-settled',
        [int]$ArrivalTimeoutSeconds = 1800
    )

    $request = Wait-FieldSublotRequest -Field $Field -JourneyId $JourneyId -Sequence $Sequence -ArrivalTimeoutSeconds $ArrivalTimeoutSeconds
    $action = 'LOAD_CANCELLATION'
    $offered = Wait-FieldCondition -Field $Field -Description "the vehicle to offer $action at stop $Sequence" -TimeoutSeconds 120 `
        -Probe { Get-FieldOnboardSnapshot -Field $Field } `
        -Until { param($s) @($s.state.availableRecoveryActions) -contains $action }

    $response = Invoke-FieldFace -Field $Field -Face Onboard -Method POST -Path '/recovery/requests' -Envelope -Body @{
        action = $action
        reason = $Reason
    }
    # 202 is "still running after 15 s": replaying the very same body reads the final answer.
    while ([int]$response.status -eq 202) {
        Start-Sleep -Seconds 3
        $response = Invoke-FieldFace -Field $Field -Face Onboard -Method POST -Path '/recovery/requests' `
            -Body ([hashtable]($response.sent | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable))
    }
    $faceAnswer = "HTTP $($response.status) $(Get-FieldProperty (Get-FieldProperty $response 'body') 'reasonCode') $(Get-FieldProperty $response 'error')".Trim()
    Write-FieldLog $Field "stop ${Sequence}: pressed $action for demand $($request.DemandId): $faceAnswer"

    $waiting = "$Sequence/AwaitingSublot"
    try {
        $settled = Wait-FieldCondition -Field $Field -Description "demand $($request.DemandId) to be cancelled by the operator at stop $Sequence" `
            -TimeoutSeconds (([int]$response.status -eq 200) ? 180 : 60) `
            -Probe { Get-FieldDemandSettlement -Field $Field -JourneyId $JourneyId -DemandId $request.DemandId } `
            -Until { param($v) $v.Status -eq 'Cancelled' -and $v.Suppression -and $v.Position -ne $waiting } `
            -Abort { param($v) $v.Status -in @('Loaded', 'Succeeded') -or $v.Operations -gt 0 -or $v.Position -like '*/Blocked' }
    } catch {
        throw "Stop ${Sequence}: $action did not cancel demand $($request.DemandId) (the face answered $faceAnswer). $($_.Exception.Message)"
    }
    $settledAt = [DateTimeOffset]::UtcNow
    Write-FieldLog $Field "stop ${Sequence}: demand $($request.DemandId) $($settled.Status) ($($settled.Suppression)); journey now at $($settled.Position)"
    $null = & $OnCheckpoint $SettledCheckpoint

    return [pscustomobject]@{
        Act               = 'X'
        Sequence          = $Sequence
        DemandId          = $request.DemandId
        Sublot            = $request.Sublot
        DeadlineAt        = $request.DeadlineAt
        ActionsOffered    = @($offered.state.availableRecoveryActions | ForEach-Object { [string]$_ })
        CommandId         = [string](Get-FieldProperty $response.sent 'commandId')
        FaceAnswer        = $faceAnswer
        SettledObservedAt = $settledAt.ToString('o')
        Status            = $settled.Status
        Suppression       = $settled.Suppression
        Operations        = $settled.Operations
        PositionAfter     = $settled.Position
        SettledCheckpoint = $SettledCheckpoint
    }
}

<#
Makes a load come back UNKNOWN for a reason that is really unknown, then leaves the slot the way a
maintenance technician would before pressing a recovery button.

The unknown is a broken lock sensor: once the vehicle waits for the operator, the lock feedback is
jammed at "locked" while the door stands open. The vehicle reads a stable opposite state, re-pulses,
never sees "unlocked", and its UnlockFeedbackTimeout ends in the one catch that still produces UNKNOWN
after decision 1 (8005-agv-program#26). Light-curtain overrides cannot do this: a curtain pinned at 0 or
1 is still a known reading.

Then maintenance. Each step is a precondition the recovery vector enforces, not staging
(scripts/l2/scenarios/real-onboard-recovery-compensate-load.ps1 has the measurements):
cargo is found in the slot -- an already-empty slot makes the vector report ALL_EMPTY without one IO
call --, the door is closed and locked, the sensor is repaired.
#>
function Invoke-FieldActUnknownLoad {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][string]$JourneyId,
        [Parameter(Mandatory)][int]$Sequence,
        [int]$ArrivalTimeoutSeconds = 1800
    )

    $load = Start-FieldStopLoad -Field $Field -JourneyId $JourneyId -Sequence $Sequence -ArrivalTimeoutSeconds $ArrivalTimeoutSeconds
    $null = Invoke-FieldSimulatorCommand -Field $Field -Method PUT -Path "/slots/$($load.SlotNo)/lock-feedback-override" -Body @{ mode = 'FIXED_1' }
    Write-FieldLog $Field "stop ${Sequence}: lock feedback of slot $($load.SlotNo) jammed at LOCKED while the door is open"

    $blocked = Wait-FieldCondition -Field $Field -Description "the load at stop $Sequence to require recovery" -TimeoutSeconds 180 `
        -Probe {
            $operation = Get-FieldOperation -Field $Field -DemandId $load.DemandId
            $journey = Get-FieldJourney -Field $Field -JourneyId $JourneyId
            [pscustomobject]@{ Status = [string]$operation.Status; Stage = [string]$journey.Stage; Block = [string]$journey.BlockReasonCode }
        } `
        -Until { param($v) $v.Status -eq 'RecoveryRequired' -and $v.Stage -eq 'Blocked' } `
        -Abort { param($v) $v.Status -in @('Committed', 'Failed', 'Cancelled') }

    $null = Invoke-FieldSimulatorCommand -Field $Field -Method PUT -Path "/slots/$($load.SlotNo)/cargo" -Body @{ state = 'OCCUPIED' }
    $null = Invoke-FieldSimulatorCommand -Field $Field -Method POST -Path "/slots/$($load.SlotNo)/close-door"
    $null = Invoke-FieldSimulatorCommand -Field $Field -Method PUT -Path "/slots/$($load.SlotNo)/lock-feedback-override" -Body @{ mode = 'AUTO' }
    $repaired = Wait-FieldCondition -Field $Field -Description "slot $($load.SlotNo) to read closed, locked and loaded for real" -TimeoutSeconds 60 `
        -Probe { Get-FieldSimulatorSlot -Field $Field -SlotNo $load.SlotNo } `
        -Until { param($s) $s.doorState -eq 'CLOSED' -and $s.cargoState -eq 'OCCUPIED' -and -not $s.lockFeedbackPending -and -not $s.lightCurtainFeedbackPending -and @($s.faults).Count -eq 0 -and [int]$s.unlockOutputRaw -eq 0 }
    Write-FieldLog $Field "stop ${Sequence}: maintenance found cargo in slot $($load.SlotNo), closed it and repaired the sensor"

    return [pscustomobject]@{
        Act        = 'U'
        Sequence   = $Sequence
        DemandId   = $load.DemandId
        Sublot     = $load.Sublot
        AttemptId  = $load.AttemptId
        SlotNo     = $load.SlotNo
        Status     = $blocked.Status
        BlockCode  = $blocked.Block
        SlotAfter  = "$($repaired.doorState)/$($repaired.cargoState)/$($repaired.lockFeedbackRaw)/$($repaired.unlockOutputRaw)"
    }
}

<#
Presses 补偿清空 (COMPENSATE_LOAD_ALL_EMPTY) through the onboard automation face and plays maintenance
until the server reconciles the workflow.

The request is the button's own request: it carries no operator, no credential and no verification
method (8005-agv-program#41), so it only works inside a recovery window opened with
14-set-recovery-window.ps1. Refusals come back two ways. One is synchronous: 409 REJECTED with the
vehicle's or the server's reason code. The other arrives after ACCEPTED -- the server's
LoadCompensationRejected or ExceptionRecoverySessionRejected -- and on the vehicle it only becomes an
operator event. This act reads it where it is stored for good, the server's ProtocolOutbox, rather than
asking the vehicle to repeat it.

The vehicle may or may not open a slot. A slot already in the target state is reported ALL_EMPTY without
a single IO call (scripts/l2/README.md item 16), which is the shape of rescuing a journey whose door was
closed empty long ago. So the act serves whatever the vehicle opens and finishes on the workflow.
#>
function Invoke-FieldActCompensate {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][string]$DemandId,
        [Parameter(Mandatory)][string]$AttemptId,
        [Parameter(Mandatory)][string]$Reason,
        [int]$TimeoutSeconds = 600
    )

    $action = 'COMPENSATE_LOAD_ALL_EMPTY'
    $null = Wait-FieldCondition -Field $Field -Description "the vehicle to offer $action" -TimeoutSeconds 180 `
        -Probe { Get-FieldOnboardSnapshot -Field $Field } `
        -Until { param($s) @($s.state.availableRecoveryActions) -contains $action }

    $rejectionSql = "SELECT COUNT(*) AS N FROM ProtocolOutbox WHERE MessageType IN ('LoadCompensationRejected', 'ExceptionRecoverySessionRejected')"
    $rejectionsBefore = [int](Invoke-FieldQuery -Field $Field -Sql $rejectionSql)[0].N
    $before = Get-FieldPhaseCounts -Field $Field -AttemptId $AttemptId
    $served = @{}

    $response = Invoke-FieldFace -Field $Field -Face Onboard -Method POST -Path '/recovery/requests' -Envelope -Body @{
        action = $action
        reason = $Reason
    }
    # 202 is "still running after 15 s": replaying the very same body reads the final answer.
    while ([int]$response.status -eq 202) {
        Start-Sleep -Seconds 3
        $response = Invoke-FieldFace -Field $Field -Face Onboard -Method POST -Path '/recovery/requests' `
            -Body ([hashtable]($response.sent | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable))
    }
    if ([int]$response.status -ne 200) {
        throw "$action was refused by the vehicle: HTTP $($response.status) $(Get-FieldProperty $response.body 'reasonCode') $(Get-FieldProperty $response 'error')".Trim()
    }
    Write-FieldLog $Field "$action accepted for demand $DemandId (command $($response.sent.commandId))"

    $workflowSql = "SELECT State, Outcome FROM RecoveryWorkflows WHERE WorkflowType = '$action' " +
        "AND DemandId = $(ConvertTo-SqlLiteral $DemandId) ORDER BY UpdatedAt DESC LIMIT 1"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $state = $null
    while ($true) {
        $workflow = @(Invoke-FieldQuery -Field $Field -Sql $workflowSql)
        $state = ($workflow.Count -gt 0) ? [string]$workflow[0].State : $null
        if ($state -in @('Reconciled', 'RecoveryRequired')) { break }

        $rejections = [int](Invoke-FieldQuery -Field $Field -Sql $rejectionSql)[0].N
        if ($rejections -gt $rejectionsBefore) {
            $row = @(Invoke-FieldQuery -Field $Field -Sql (
                "SELECT MessageType, PayloadJson FROM ProtocolOutbox WHERE MessageType IN ('LoadCompensationRejected', 'ExceptionRecoverySessionRejected') ORDER BY CreatedAt DESC LIMIT 1"))[0]
            throw "$action was accepted by the vehicle and then refused by the server: $($row.MessageType) $($row.PayloadJson)"
        }

        $counts = Get-FieldPhaseCounts -Field $Field -AttemptId $AttemptId
        $opened = Get-FieldReopenedSlot -Events $counts.Events -BaseCount $before.Events.Count -Exclude @($served.Keys)
        if ($opened -gt 0) {
            Write-FieldLog $Field "compensation: vehicle waits on slot $opened; maintenance takes the cargo out"
            $null = Invoke-FieldCloseSlot -Field $Field -SlotNo $opened -Cargo EMPTY
            $served[$opened] = $true
            continue
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Timed out after ${TimeoutSeconds}s waiting for $action to reconcile; workflow state $($state ?? '(no workflow row)')."
        }
        Start-Sleep -Milliseconds ([Math]::Max($Field.PollMilliseconds, 500))
    }
    if ($state -ne 'Reconciled') {
        throw "$action ended in $state, not Reconciled; outcome $($workflow[0].Outcome)."
    }

    $demandStatus = Wait-FieldCondition -Field $Field -Description "demand $DemandId to be settled by the compensation" -TimeoutSeconds 120 `
        -Probe { [string](@(Invoke-FieldQuery -Field $Field -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = $(ConvertTo-SqlLiteral $DemandId)")[0].Status) } `
        -Until { param($s) $s -eq 'Cancelled' }
    Write-FieldLog $Field "$action reconciled; demand $DemandId $demandStatus"

    return [pscustomobject]@{
        Act           = 'C'
        DemandId      = $DemandId
        AttemptId     = $AttemptId
        CommandId     = [string]$response.sent.commandId
        WorkflowState = $state
        Outcome       = [string]$workflow[0].Outcome
        DemandStatus  = $demandStatus
        ServedSlots   = @($served.Keys | Sort-Object)
    }
}

<#
The unload side of decision 1, with ADR-cross-0015's asymmetry: an unload has no cancellation branch. The
operator closes the door with the basket still inside; the vehicle reads the opposite state and reopens,
round after round, and the only way the operation ends is the cargo actually coming out. Returns once
-Rounds reopens were seen, with the vehicle waiting on the slot again and the cargo still in it -- the
caller empties it.

Throws if a close with the cargo inside settles the operation at all: that would be the cancellation
branch the ADR says does not exist.
#>
function Invoke-FieldNotEmptiedRounds {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][string]$AttemptId,
        [Parameter(Mandatory)][int]$SlotNo,
        [Parameter(Mandatory)][int]$Rounds
    )

    $statusSql = "SELECT DemandId, Status FROM StationOperations WHERE SlotOperationAttemptId = $(ConvertTo-SqlLiteral $AttemptId)"
    for ($round = 1; $round -le $Rounds; $round++) {
        $before = Get-FieldPhaseCounts -Field $Field -AttemptId $AttemptId
        $null = Invoke-FieldCloseSlot -Field $Field -SlotNo $SlotNo -Cargo OCCUPIED -NoSettle
        $answer = Wait-FieldCondition -Field $Field -Description "the vehicle's answer to closing gate slot $SlotNo with the cargo still in (round $round)" -TimeoutSeconds 180 `
            -Probe {
                # Assigned, not piped: Invoke-FieldQuery returns its rows as ONE pipeline object.
                $operationRows = Invoke-FieldQuery -Field $Field -Sql $statusSql
                $operation = ($operationRows.Count -gt 0) ? $operationRows[0] : $null
                $counts = Get-FieldPhaseCounts -Field $Field -AttemptId $AttemptId
                [pscustomobject]@{
                    Status    = $operation ? [string]$operation.Status : '(no row)'
                    Reopened  = (Get-FieldReopenedSlot -Events $counts.Events -BaseCount $before.Events.Count -SlotNo $SlotNo) -gt 0
                    Unlocking = $counts.Unlocking
                    Waiting   = $counts.Waiting
                }
            } `
            -Until { param($v) $v.Status -in $script:TerminalOperationStatuses -or $v.Reopened }
        if ($answer.Status -in $script:TerminalOperationStatuses) {
            throw "Gate slot ${SlotNo} round ${round}: closing it with the cargo still inside settled unload $AttemptId as $($answer.Status). An unload has no such exit."
        }
        Write-FieldLog $Field "gate slot $SlotNo round ${round}: closed with the cargo still in; the vehicle reopened it (UNLOCKING $($before.Unlocking)->$($answer.Unlocking), WAITING_OPERATOR $($before.Waiting)->$($answer.Waiting))"
    }
    $final = Get-FieldPhaseCounts -Field $Field -AttemptId $AttemptId
    $operationRows = Invoke-FieldQuery -Field $Field -Sql $statusSql
    $operation = $operationRows[0]
    return [pscustomobject]@{
        DemandId       = [string]$operation.DemandId
        AttemptId      = $AttemptId
        SlotNo         = $SlotNo
        Rounds         = $Rounds
        StatusAtRounds = [string]$operation.Status
        Unlocking      = $final.Unlocking
        Waiting        = $final.Waiting
    }
}

<#
At the gate: takes the cargo out of every slot the vehicle opens, one unload operation after another,
until the journey completes. With -NotEmptiedRounds, the first slot the vehicle opens is closed that many
times with the cargo still inside before it is emptied (Invoke-FieldNotEmptiedRounds), and -OnCheckpoint
is called at that moment -- the operation is still open and the journey still waiting on it, a state the
emptying overwrites.
#>
function Invoke-FieldActUnload {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][string]$JourneyId,
        [int]$TimeoutSeconds = 1800,
        [int]$NotEmptiedRounds = 0,
        [scriptblock]$OnCheckpoint = { param($label) },
        [string]$NotEmptiedCheckpoint = 'ne-reopened'
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $results = [ordered]@{}
    $servedByAttempt = @{}
    $notEmptied = $null
    $unloadSql = 'SELECT o.DemandId, o.SlotOperationAttemptId, o.Status FROM StationOperations o ' +
        'JOIN JourneyDemands d ON d.DemandId = o.DemandId ' +
        "WHERE d.JourneyId = $(ConvertTo-SqlLiteral $JourneyId) AND o.OperationType = 'Unload'"
    while ($true) {
        $journey = Get-FieldJourney -Field $Field -JourneyId $JourneyId
        if ([string]$journey.Stage -eq 'Completed') {
            # Read the operations once more AFTER seeing Completed. The last unload commits and the journey
            # completes between two polls, so a loop that only records what it saw before Completed drops
            # it (8005-agv-program#45, rehearsal -009: three unloads Committed in the database, two in the
            # result). Once the journey is Completed every unload is terminal, so this read is complete.
            foreach ($operation in (Invoke-FieldQuery -Field $Field -Sql $unloadSql)) {
                $results[[string]$operation.SlotOperationAttemptId] = [string]$operation.Status
            }
            break
        }
        if ([string]$journey.Stage -eq 'Blocked') {
            throw "Journey $JourneyId blocked at the gate: $($journey.BlockReasonCode)."
        }
        $operations = Invoke-FieldQuery -Field $Field -Sql $unloadSql
        foreach ($operation in $operations) {
            $attemptId = [string]$operation.SlotOperationAttemptId
            if ([string]$operation.Status -in $script:TerminalOperationStatuses) {
                $results[$attemptId] = [string]$operation.Status
                continue
            }
            if (-not $servedByAttempt.ContainsKey($attemptId)) { $servedByAttempt[$attemptId] = @{} }
            $counts = Get-FieldPhaseCounts -Field $Field -AttemptId $attemptId
            $slotNo = $counts.WaitingSlot
            if ($slotNo -gt 0 -and -not $servedByAttempt[$attemptId].ContainsKey($slotNo)) {
                if ($NotEmptiedRounds -gt 0 -and -not $notEmptied) {
                    $notEmptied = Invoke-FieldNotEmptiedRounds -Field $Field -AttemptId $attemptId -SlotNo $slotNo -Rounds $NotEmptiedRounds
                    $null = & $OnCheckpoint $NotEmptiedCheckpoint
                }
                Write-FieldLog $Field "gate: unload $attemptId waits on slot $slotNo; taking the cargo out"
                $null = Invoke-FieldCloseSlot -Field $Field -SlotNo $slotNo -Cargo EMPTY
                $servedByAttempt[$attemptId][$slotNo] = $true
            }
        }
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Timed out after ${TimeoutSeconds}s unloading journey $JourneyId; stage $($journey.Stage)."
        }
        Start-Sleep -Milliseconds ([Math]::Max($Field.PollMilliseconds, 500))
    }
    Write-FieldLog $Field "journey $JourneyId completed at the gate"
    return [pscustomobject]@{
        Act                  = 'UNLOAD'
        Operations           = $results
        Slots                = @($servedByAttempt.Values | ForEach-Object { $_.Keys } | Sort-Object)
        NotEmptied           = $notEmptied
        NotEmptiedCheckpoint = $notEmptied ? $NotEmptiedCheckpoint : $null
    }
}

# --- between journeys ------------------------------------------------------------------------------------

function Get-FieldSession {
    param([Parameter(Mandatory)][object]$Field)
    $rows = Invoke-FieldQuery -Field $Field -Sql (
        'SELECT SessionGeneration, Readiness, ReasonCode, UpdatedAt FROM SessionRecoveries ' +
        "WHERE AgvId = $(ConvertTo-SqlLiteral $Field.AgvId)")
    return ($rows.Count -eq 0) ? $null : $rows[0]
}

<#
When the server's process was started, for measuring a restart from. Remote only: the service is read on
the server itself. On the L2 rig the scenario restarts the process and knows the time already.
#>
function Get-FieldServerStartedAt {
    param([Parameter(Mandatory)][object]$Field, [string]$ServiceName = '8005 AGV ControlServer')

    if ($Field.Mode -eq 'Local') { return $null }
    $script = @"
`$service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$($ServiceName.Replace("'", "''"))'"
`$started = (`$service -and `$service.ProcessId) ? (Get-Process -Id `$service.ProcessId).StartTime.ToUniversalTime().ToString('o') : ''
'$script:Marker' + `$started
"@
    $text = Invoke-FieldRemoteScript -HostAlias $Field.ServerHost -Script $script
    return $text ? [datetimeoffset]$text : $null
}

<#
After a service restart with the vehicle standing still: waits for a session generation newer than
-GenerationBefore to be Ready, and returns every (generation, readiness, reason) the server held on the way.
The defect this watches for (docs/defects/20260908-session-recovery-required-never-clears-while-vehicle-idle.md)
is a new generation that lands in RecoveryRequired and never leaves it while nothing moves -- 6 min 36 s on
the plant until somebody restarted the client. The series is what tells that apart from a slow reconnect.
#>
function Wait-FieldSessionAfterRestart {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][long]$GenerationBefore,
        [int]$TimeoutSeconds = 600,
        [scriptblock]$OnCheckpoint = { param($label) },
        [string]$ReadyCheckpoint = 'restart-ready'
    )

    $until = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $series = [System.Collections.Generic.List[object]]::new()
    $lastKey = $null
    $firstNewAt = $null
    while ($true) {
        $session = $null
        try { $session = Get-FieldSession -Field $Field } catch { }
        if ($session) {
            $generation = [long]$session.SessionGeneration
            $key = "$generation/$($session.Readiness)/$($session.ReasonCode)"
            if ($key -ne $lastKey) {
                $lastKey = $key
                $series.Add([pscustomobject]@{
                    ObservedAt = [DateTimeOffset]::UtcNow.ToString('o')
                    Generation = $generation
                    Readiness  = [string]$session.Readiness
                    ReasonCode = [string]$session.ReasonCode
                    UpdatedAt  = [string]$session.UpdatedAt
                })
                Write-FieldLog $Field "session: generation $generation $($session.Readiness)$($session.ReasonCode ? " ($($session.ReasonCode))" : '')"
            }
            if ($generation -gt $GenerationBefore -and -not $firstNewAt) { $firstNewAt = [DateTimeOffset]::UtcNow }
            if ($generation -gt $GenerationBefore -and [string]$session.Readiness -eq 'Ready') { break }
        }
        if ([DateTimeOffset]::UtcNow -ge $until) {
            throw "Timed out after ${TimeoutSeconds}s waiting for a session newer than generation $GenerationBefore to be Ready. Seen: $(($series | ForEach-Object { "$($_.Generation)/$($_.Readiness)/$($_.ReasonCode)" }) -join ' -> ')"
        }
        Start-Sleep -Milliseconds ([Math]::Max($Field.PollMilliseconds, 500))
    }
    $readyAt = [DateTimeOffset]::UtcNow
    $null = & $OnCheckpoint $ReadyCheckpoint

    return [pscustomobject]@{
        GenerationBefore             = $GenerationBefore
        GenerationAfter              = [long]$series[$series.Count - 1].Generation
        FirstNewGenerationObservedAt = $firstNewAt.ToString('o')
        ReadyObservedAt              = $readyAt.ToString('o')
        Series                       = $series.ToArray()
        ReadyCheckpoint              = $ReadyCheckpoint
    }
}

<#
The charging errand between two journeys (ADR-cross-0057's auto charging, JourneyRuntimeEngine
AdvanceAutoChargingAsync): waits for the vehicle's charging run to reach -Stage, keeping every
(stage, block reason) it sees on the way in -Tracker, and calls -OnCheckpoint when it gets there.

Called once per stage and handed the tracker back each time, because on the L2 rig the scenario has to
play RIoT between the stages -- drive the vehicle to the pad, raise the battery -- and on the plant the
driver simply calls the three in a row. The first call names the run: the oldest one created after
-After, which is when the previous journey was seen completed.

BlockReasonCode is overwritten on every iteration, so CHARGER_NOT_ENGAGED -- standing on the pad without
drawing current, the one failure the errand cannot fix by waiting -- is only visible to something that was
watching when it was written. That is the reason for the tracker.
#>
function Wait-FieldChargingStage {
    param(
        [Parameter(Mandatory)][object]$Field,
        [Parameter(Mandatory)][ValidateSet('AwaitingChargerArrival', 'Charging', 'Completed')][string]$Stage,
        [object]$Tracker,
        [datetimeoffset]$After,
        [int]$TimeoutSeconds = 3600,
        [scriptblock]$OnCheckpoint = { param($label) },
        [string]$Checkpoint
    )

    if (-not $Tracker) {
        if (-not $PSBoundParameters.ContainsKey('After')) { throw 'The first call names the run: pass -After.' }
        $Tracker = [pscustomobject]@{
            After                     = $After.ToString('o')
            ChargingRunId             = $null
            UpperId                   = $null
            ChargerStationRiotId      = $null
            TriggeredAtBatteryPercent = $null
            ReleasedAtBatteryPercent  = $null
            CreatedAt                 = $null
            CompletedObservedAt       = $null
            RunsAfter                 = 0
            Seen                      = [System.Collections.Generic.List[object]]::new()
            Checkpoints               = [ordered]@{}
        }
    }
    $rank = @{ AwaitingChargerArrival = 1; Charging = 2; Completed = 3 }
    $after = [datetimeoffset]$Tracker.After
    $until = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $sql = 'SELECT ChargingRunId, Stage, BlockReasonCode, TriggeredAtBatteryPercent, ReleasedAtBatteryPercent, ' +
        "UpperId, ChargerStationRiotId, CreatedAt FROM AutoChargingRuns WHERE AgvId = $(ConvertTo-SqlLiteral $Field.AgvId)"
    $lastQueryError = $null
    while ($true) {
        $runs = @()
        try {
            # Assigned before filtering: Invoke-FieldQuery returns its rows as ONE pipeline object. Filtered
            # and ordered here, not in SQL: SQLite stores DateTimeOffset as text and cannot order it.
            $allRuns = Invoke-FieldQuery -Field $Field -Sql $sql
            $runs = @($allRuns | ForEach-Object { $_ } | Where-Object { [datetimeoffset]$_.CreatedAt -gt $after } |
                Sort-Object { [datetimeoffset]$_.CreatedAt })
            $lastQueryError = $null
        } catch {
            # Over SSH one dropped round trip is not a reason to fail a wait measured in tens of minutes.
            $lastQueryError = $_.Exception.Message
        }
        $Tracker.RunsAfter = [Math]::Max($Tracker.RunsAfter, $runs.Count)
        # Select-Object, not [0]: StrictMode Latest throws on indexing an empty array.
        $run = $Tracker.ChargingRunId ? ($runs | Where-Object { $_.ChargingRunId -eq $Tracker.ChargingRunId } | Select-Object -First 1) `
                                      : ($runs | Select-Object -First 1)
        if ($run) {
            if (-not $Tracker.ChargingRunId) {
                $Tracker.ChargingRunId = [string]$run.ChargingRunId
                $Tracker.UpperId = [string]$run.UpperId
                $Tracker.ChargerStationRiotId = [int]$run.ChargerStationRiotId
                $Tracker.TriggeredAtBatteryPercent = [int]$run.TriggeredAtBatteryPercent
                $Tracker.CreatedAt = [string]$run.CreatedAt
                Write-FieldLog $Field "charging run $($run.ChargingRunId) started at $($run.TriggeredAtBatteryPercent)% towards station $($run.ChargerStationRiotId)"
            }
            $key = "$($run.Stage)/$($run.BlockReasonCode)"
            $lastSeen = ($Tracker.Seen.Count -gt 0) ? $Tracker.Seen[$Tracker.Seen.Count - 1] : $null
            if (-not $lastSeen -or "$($lastSeen.Stage)/$($lastSeen.BlockReasonCode)" -ne $key) {
                $Tracker.Seen.Add([pscustomobject]@{
                    ObservedAt      = [DateTimeOffset]::UtcNow.ToString('o')
                    Stage           = [string]$run.Stage
                    BlockReasonCode = [string]$run.BlockReasonCode
                })
                Write-FieldLog $Field "charging run: $($run.Stage)$($run.BlockReasonCode ? " ($($run.BlockReasonCode))" : '')"
            }
            if ($rank[[string]$run.Stage] -ge $rank[$Stage]) {
                if ([string]$run.Stage -eq 'Completed') {
                    $Tracker.ReleasedAtBatteryPercent = [int]$run.ReleasedAtBatteryPercent
                    $Tracker.CompletedObservedAt = [DateTimeOffset]::UtcNow.ToString('o')
                }
                break
            }
        }
        if ([DateTimeOffset]::UtcNow -ge $until) {
            throw "Timed out after ${TimeoutSeconds}s waiting for the charging run to reach $Stage. Seen: $(($Tracker.Seen | ForEach-Object { "$($_.Stage)/$($_.BlockReasonCode)" }) -join ' -> ')$($Tracker.ChargingRunId ? '' : ' (no run created after ' + $Tracker.After + ')')$($lastQueryError ? "; last query error: $lastQueryError" : '')"
        }
        Start-Sleep -Seconds 2
    }
    if ($Checkpoint) {
        $Tracker.Checkpoints[$Stage] = $Checkpoint
        $null = & $OnCheckpoint $Checkpoint
    }
    return $Tracker
}

<#
The field record of window two, written from what the acts did. The collector's FW-FL2 finalize reads it:
journeyIds says whose rows to export, each scenario names the demand and attempt it is judged on.
#>
function New-FullLoopWindowRecord {
    param(
        [Parameter(Mandatory)][string]$AgvId,
        [Parameter(Mandatory)][string]$IoModule,
        [Parameter(Mandatory)][string]$DriverRunId,
        [Parameter(Mandatory)][string]$Site,
        [Parameter(Mandatory)][object[]]$Journeys,
        [object]$ActT,
        [object]$ActX,
        [object]$NotEmptied,
        [string]$NotEmptiedCheckpoint,
        [object]$Charging,
        [hashtable]$ChargeOverride,
        [object[]]$Restarts = @(),
        [bool]$RecoveryWindowOpen = $false,
        # A resumed journey leaving the stop an aborted window left it standing at (8005-agv-program#52).
        [object]$DepartureSafetyReask,
        # The scenes this window set out to play. The finalize judges a missing scene as a failure only when it
        # is owed here: a charging-only window does not owe T, X and NE, which an earlier window already played.
        [ValidateSet('T', 'X', 'NE', 'N', 'CH', 'R')]
        [string[]]$ScenesOwed = @('T', 'X', 'NE', 'N', 'CH', 'R')
    )

    $scenarios = [System.Collections.Generic.List[object]]::new()
    if ($ActT) {
        $scenarios.Add([ordered]@{
            id                = 'T'
            name              = '到站不录入 SUBLOT：站点期限到期，服务端以 CANCELLED_BY_STATION_TIMEOUT 终结并永久抑制，车被释放'
            demandId          = $ActT.DemandId
            sublot            = $ActT.Sublot
            stopSequence      = $ActT.Sequence
            serverWaitingAt   = $ActT.ServerWaitingAt
            vehicleAskedAt    = $ActT.VehicleAskedAt
            deadlineAt        = $ActT.DeadlineAt
            settledObservedAt = $ActT.SettledObservedAt
            positionAfter     = $ActT.PositionAfter
            settledCheckpoint = $ActT.SettledCheckpoint
            # Set only on an act an earlier, aborted window played and a resumed window carried in.
            carriedFrom       = Get-FieldProperty $ActT 'CarriedFrom'
            carriedNote       = Get-FieldProperty $ActT 'CarriedNote'
        })
    }
    if ($ActX) {
        $scenarios.Add([ordered]@{
            id                = 'X'
            name              = '到站还没装货就取消：扫码前按「取消装货」，需求 Cancelled 并按 CANCELLED_BY_OPERATOR 抑制，旅程自己离站'
            demandId          = $ActX.DemandId
            sublot            = $ActX.Sublot
            stopSequence      = $ActX.Sequence
            commandId         = $ActX.CommandId
            faceAnswer        = $ActX.FaceAnswer
            actionsOffered    = @($ActX.ActionsOffered)
            settledObservedAt = $ActX.SettledObservedAt
            positionAfter     = $ActX.PositionAfter
            settledCheckpoint = $ActX.SettledCheckpoint
            carriedFrom       = Get-FieldProperty $ActX 'CarriedFrom'
            carriedNote       = Get-FieldProperty $ActX 'CarriedNote'
        })
    }
    if ($DepartureSafetyReask) {
        $scenarios.Add([ordered]@{
            id             = 'S52'
            name           = '缺陷 #52 修复的现场观测：停在出车前安全检查上、回答早已过期的在途旅程，开门后换检查 id 重问并自己离站'
            journeyId      = $DepartureSafetyReask.JourneyId
            stopSequence   = $DepartureSafetyReask.Sequence
            positionBefore = $DepartureSafetyReask.PositionBefore
            checkIdBefore  = $DepartureSafetyReask.CheckIdBefore
            consumedBefore = $DepartureSafetyReask.ConsumedBefore
            positionAfter  = $DepartureSafetyReask.PositionAfter
            checkIdAfter   = $DepartureSafetyReask.CheckIdAfter
            consumedAfter  = $DepartureSafetyReask.ConsumedAfter
            leftObservedAt = $DepartureSafetyReask.LeftObservedAt
            checkpoint     = $DepartureSafetyReask.Checkpoint
        })
    }
    if ($NotEmptied) {
        $scenarios.Add([ordered]@{
            id                     = 'NE'
            name                   = '关卡卸货未取空：关门时货还在，车反复重开，没有取消分支，取空才提交'
            demandId               = $NotEmptied.DemandId
            slotOperationAttemptId = $NotEmptied.AttemptId
            slotNo                 = $NotEmptied.SlotNo
            rounds                 = $NotEmptied.Rounds
            statusAtRounds         = $NotEmptied.StatusAtRounds
            observedRounds         = "UNLOCKING=$($NotEmptied.Unlocking) WAITING_OPERATOR=$($NotEmptied.Waiting)"
            checkpoint             = $NotEmptiedCheckpoint
        })
    }
    if ($Charging) {
        $scenarios.Add([ordered]@{
            id                        = 'CH'
            name                      = '两趟之间自动充电：低于触发线自己去充电桩，接上电，到恢复线释放，接着受理下一单'
            chargingRunId             = $Charging.ChargingRunId
            upperId                   = $Charging.UpperId
            chargerStationRiotId      = $Charging.ChargerStationRiotId
            triggeredAtBatteryPercent = $Charging.TriggeredAtBatteryPercent
            releasedAtBatteryPercent  = $Charging.ReleasedAtBatteryPercent
            after                     = $Charging.After
            completedObservedAt       = $Charging.CompletedObservedAt
            runsAfter                 = $Charging.RunsAfter
            seen                      = @($Charging.Seen)
            checkpoints               = $Charging.Checkpoints
            thresholdOverride         = $ChargeOverride
        })
    }
    $index = 0
    foreach ($restart in $Restarts) {
        $index++
        $scenarios.Add([ordered]@{
            id                           = "R$index"
            name                         = '车静止时服务重启：新会话自己回到 Ready，不停在 RecoveryRequired'
            afterJourneyId               = $restart.AfterJourneyId
            serverStartedAt              = $restart.ServerStartedAt
            generationBefore             = $restart.GenerationBefore
            generationAfter              = $restart.GenerationAfter
            firstNewGenerationObservedAt = $restart.FirstNewGenerationObservedAt
            readyObservedAt              = $restart.ReadyObservedAt
            series                       = @($restart.Series)
            checkpoint                   = $restart.ReadyCheckpoint
        })
    }

    return [ordered]@{
        windowId           = 'FW-FL2'
        date               = (Get-Date).ToString('yyyy-MM-dd')
        site               = $Site
        observers          = @()
        drivenBy           = "FieldOperator.psm1 run $DriverRunId"
        agvId              = $AgvId
        ioModule           = $IoModule
        ioKind             = ($IoModule -match '^(127\.0\.0\.1|localhost):') ? 'SIMULATOR' : 'REAL_MODULE'
        recoveryWindowOpen = $RecoveryWindowOpen
        journeys           = @($Journeys)
        journeyIds         = @($Journeys | ForEach-Object { [string]$_.journeyId })
        photoPointers      = @()
        scenesOwed         = @($ScenesOwed)
        scenarios          = $scenarios.ToArray()
    }
}

<#
The field record the collector's -Finalize reads, written from what the acts did rather than typed by a
person. Fields a person used to fill that have no meaning with nobody there -- photos, observers -- are
not faked: ioKind says which kind of window this was, and the collector judges accordingly.
#>
function New-FieldWindowRecord {
    param(
        [Parameter(Mandatory)][string]$AgvId,
        [Parameter(Mandatory)][string]$IoModule,
        [Parameter(Mandatory)][bool]$RecoveryWindowOpen,
        [Parameter(Mandatory)][string]$DriverRunId,
        [Parameter(Mandatory)][string]$Site,
        [object]$ActA,
        [object]$ActCB
    )

    $ioKind = ($IoModule -match '^(127\.0\.0\.1|localhost):') ? 'SIMULATOR' : 'REAL_MODULE'
    $scenarios = [System.Collections.Generic.List[object]]::new()
    if ($ActA) {
        $scenarios.Add([ordered]@{
            id                           = 'A'
            name                         = '开门不放料：自动重发开锁脉冲，不判失败、不进恢复、不设次数上限，最后放料提交'
            demandId                     = $ActA.DemandId
            slotNo                       = $ActA.SlotNo
            slotOperationAttemptId       = $ActA.AttemptId
            observedRounds               = "空关 $($ActA.RoundsCompleted)/$($ActA.RoundsRequested) 轮；UNLOCKING=$($ActA.Unlocking) WAITING_OPERATOR=$($ActA.Waiting)"
            observedRecoveryEntryVisible = [bool]$ActA.RecoveryEntryVisible
            observedBy                   = 'onboard automation snapshot availableRecoveryActions ∩ RESUME_AFTER_REPAIR/COMPENSATE_LOAD_ALL_EMPTY/FAULT_CARGO_HANDOFF'
            observedAvailableActions     = @($ActA.ActionsSeen)
            photoPointers                = @()
        })
    }
    if ($ActCB) {
        $scenarios.Add([ordered]@{
            id                     = 'B'
            name                   = '仓门已闭超时：确定失败结算，不进恢复，服务端自己以 CANCELLED_BY_STATION_TIMEOUT 终结需求、旅程离开本站'
            demandId               = $ActCB.DemandId
            slotNo                 = $ActCB.SlotNo
            slotOperationAttemptId = $ActCB.AttemptId
            observedSlotReading    = "空关 $($ActCB.EmptyCloses) 次，其中 $($ActCB.GraceReopens) 次换来重开；结算 $($ActCB.Status)；之后 $($ActCB.PositionAfter)"
            photoPointers          = @()
        })
        $scenarios.Add([ordered]@{
            id                     = 'C'
            name                   = '仓门未闭超时：告警 STATION_TIMEOUT_DOOR_NOT_CLOSED，不结束本站'
            demandId               = $ActCB.DemandId
            slotNo                 = $ActCB.SlotNo
            slotOperationAttemptId = $ActCB.AttemptId
            deadlineAt             = $ActCB.DeadlineAt ?? $ActCB.AlarmObservedAt
            alarmObservedAt        = $ActCB.AlarmObservedAt
            stillWaitingObservedAt = $ActCB.StillWaitingObservedAt
            deadlineCheckpoint     = $ActCB.DeadlineCheckpoint
            stillWaitingCheckpoint = $ActCB.StillWaitingCheckpoint
            observedSlotReading    = "走开时 $($ActCB.WalkAwayCounts)；期限后 $($ActCB.HoldMinutes) 分钟 $($ActCB.HeldCounts)"
            photoPointers          = @()
        })
    }

    return [ordered]@{
        windowId           = 'FW-SC1'
        date               = (Get-Date).ToString('yyyy-MM-dd')
        site               = $Site
        observers          = @()
        drivenBy           = "FieldOperator.psm1 run $DriverRunId"
        agvId              = $AgvId
        ioModule           = $IoModule
        ioKind             = $ioKind
        recoveryWindowOpen = $RecoveryWindowOpen
        photoPointers      = @()
        scenarios          = $scenarios.ToArray()
    }
}

Export-ModuleMember -Function New-FieldOperator, Invoke-FieldQuery, Invoke-FieldFace, Invoke-FieldSimulatorCommand,
    Get-FieldSimulatorSlot, Get-FieldOnboardSnapshot, Wait-FieldCondition, Get-FieldJourney, Get-FieldPosition,
    Get-FieldStopDemand, Get-FieldOperation, Get-FieldProgress, Get-FieldPhaseCounts, Invoke-FieldCloseSlot,
    Invoke-FieldServeOperation, Start-FieldStopLoad, Invoke-FieldActReopen, Invoke-FieldActDoorLeftOpen,
    Invoke-FieldActLoad, Invoke-FieldActUnknownLoad, Invoke-FieldActCompensate, Invoke-FieldActUnload,
    New-FieldWindowRecord, Get-FieldProperty,
    Wait-FieldSublotRequest, Get-FieldDemandSettlement, Invoke-FieldActNoSublot, Invoke-FieldActCancelBeforeSublot,
    Invoke-FieldNotEmptiedRounds, Get-FieldSession, Get-FieldServerStartedAt, Wait-FieldSessionAfterRestart,
    Wait-FieldChargingStage, New-FullLoopWindowRecord, Get-FieldStopSafetyCheck
