#Requires -Version 7

<#
.SYNOPSIS
    Collects the evidence for the slot-convergence field window -- the three operator-inaction
    scenarios of ADR-cross-0058 run on agv01, against a real Modbus IO module or (unattended, since
    2026-09-11) against the vehicle's own slot simulator.

.DESCRIPTION
    This script does not drive a vehicle and does not decide whether the window passed on the
    physical side. Somebody opens and closes doors, withholds cargo, and watches the HMI -- a person
    on the real module, Invoke-SlotConvergenceFieldDrive.ps1 on the simulator, which also calls the
    checkpoints and writes the record -- and those facts reach the evidence through the field record.
    The record's ioKind says which, and the window-level assertions follow it. What this script does is take
    the facts that are already written down by the two ends -- the server's own SQLite store and the
    onboard logs -- freeze them, and compute the assertions ADR-cross-0058 can actually be judged by.

    Two modes, because scenario C's key value cannot be observed after the fact. "The deadline
    expired and twenty minutes later it still had not ended" is a statement about a moment that is
    gone by the time the window is over: once the operator closes the door, the settlement writes
    over the very state the assertion needs. So the window is recorded as a series of checkpoints
    taken while it runs, and judged once at the end.

        -Checkpoint <label>   Freeze the current state into snapshots/<nn>-<label>/ and append one
                              timeline line. Run it as many times as the window needs. The first
                              call creates the evidence directory.
        -Finalize             Take a last checkpoint, read the field record, compute the assertions
                              and write assertions.json plus SUMMARY.md. Refuses to run twice.

    Evidence discipline (evidence/field/README.md) is enforced, with one deliberate difference from
    a gate runner: -EvidenceRoot must NOT exist on the first -Checkpoint, and after that it must,
    because the directory grows across the window. It stays append-only either way -- a checkpoint
    never rewrites an earlier one, and -Finalize refuses to overwrite an existing assertions.json.

    The production database is never copied into the evidence directory. It is a live plant store
    holding journeys that have nothing to do with this window, and evidence/ is tracked by git. The
    whole file goes to -StageRoot outside the repository (which is also what run-demand-bearing-g3-vectors.ps1
    wants for -FieldRunRoot); what lands in the evidence directory is the rows belonging to this
    window's demands, and nothing else.

.EXAMPLE
    # While the window runs, at the moments that matter:
    .\Invoke-SlotConvergenceFieldWindow.ps1 -EvidenceRoot ..\..\evidence\field\20260910-slot-convergence-w1 -Checkpoint 'c-deadline-reached'
    .\Invoke-SlotConvergenceFieldWindow.ps1 -EvidenceRoot ..\..\evidence\field\20260910-slot-convergence-w1 -Checkpoint 'c-plus-20min'

.EXAMPLE
    # Once the three scenarios are done and the field record is filled in:
    .\Invoke-SlotConvergenceFieldWindow.ps1 -EvidenceRoot ..\..\evidence\field\20260910-slot-convergence-w1 `
        -Finalize -RecordPath .\records\20260910-slot-convergence.json
#>
[CmdletBinding(DefaultParameterSetName = 'Checkpoint')]
param(
    # The evidence directory for this window. Must not exist on the first -Checkpoint; must exist
    # afterwards. Never overwritten -- a correction goes to a new directory naming this one.
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,

    # Freeze the current state under this label. Sorted into the snapshot sequence by call order.
    [Parameter(Mandatory, ParameterSetName = 'Checkpoint')]
    [string]$Checkpoint,

    # Take the last checkpoint, then judge the window and write the summary.
    [Parameter(Mandatory, ParameterSetName = 'Finalize')]
    [switch]$Finalize,

    # The filled-in field record. See templates/slot-convergence-window.json.
    [Parameter(Mandatory, ParameterSetName = 'Finalize')]
    [string]$RecordPath,

    # SSH aliases from remote-ops. The control host reaches both; nothing here runs on the vehicle.
    [string]$ServerHost = 'factory01',
    [string]$VehicleHost = 'agv01',

    [string]$ServerDatabase = 'C:\ProgramData\8005\ControlServer\data\controlserver.db',
    [string]$VehicleLogDirectory = 'C:\8005\OnboardHmi\logs',

    # Where the full database copies land. Outside the repository on purpose -- see .DESCRIPTION.
    [string]$StageRoot = (Join-Path $env:USERPROFILE 'w2g-stage\field'),

    # Skip the SSH collection and read this database file instead. For a dry run against a copy.
    [string]$DatabaseSnapshot,

    # Directory holding Microsoft.Data.Sqlite and SQLitePCLRaw. Defaults to this repository's build
    # output; the L2 module borrows them from whatever build is under test rather than taking a
    # dependency of its own, and this script borrows them the same way.
    [string]$HostDirectory,

    # How long scenario C must still be waiting past the deadline (SC1-C-04). Twenty minutes is the
    # ticket's number and the field value. Only a rehearsal on the L2 rig passes less, and the value is
    # written into assertions.json and SUMMARY.md so a short hold can never pass as a field window.
    [double]$MinimumHoldMinutes = 20,

    # This script lives in scripts/field, so the repository root is two levels up.
    [string]$Repository = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Every path parameter is made absolute against $PWD before anything touches it. PowerShell's
# cmdlets resolve a relative path against $PWD while .NET's file APIs resolve it against the
# process's own current directory, and Set-Location moves only the first. This script uses both
# -- New-Item to create the directories, [IO.File]::WriteAllText to write into them -- so a
# relative -EvidenceRoot creates the tree in one place and writes to another, which surfaces as
# "could not find a part of the path" naming a directory that was just created successfully.
function Resolve-AbsolutePath {
    param([string]$Path)
    if (-not $Path) { return $Path }
    return [IO.Path]::GetFullPath([IO.Path]::Combine($PWD.ProviderPath, $Path))
}

$EvidenceRoot = Resolve-AbsolutePath $EvidenceRoot
$StageRoot = Resolve-AbsolutePath $StageRoot
if ($RecordPath) { $RecordPath = Resolve-AbsolutePath $RecordPath }
if ($DatabaseSnapshot) { $DatabaseSnapshot = Resolve-AbsolutePath $DatabaseSnapshot }
if ($HostDirectory) { $HostDirectory = Resolve-AbsolutePath $HostDirectory }

Import-Module (Join-Path $Repository 'scripts/l2/L2.psm1') -Force

$windowId = 'FW-SC1'
$runId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ')
$snapshotRoot = Join-Path $EvidenceRoot 'snapshots'
$logRoot = Join-Path $EvidenceRoot 'logs'
$timelinePath = Join-Path $EvidenceRoot 'timeline.jsonl'
$assertionsPath = Join-Path $EvidenceRoot 'assertions.json'
$label = $Finalize ? 'finalize' : $Checkpoint

if ($Finalize) {
    if (-not (Test-Path -LiteralPath $EvidenceRoot -PathType Container)) {
        throw "No evidence directory at $EvidenceRoot. Take at least one -Checkpoint during the window before finalising it."
    }
    if (Test-Path -LiteralPath $assertionsPath) {
        throw "$assertionsPath already exists. Evidence is append-only: a correction goes to a new directory whose SUMMARY.md names this one."
    }
    if (-not (Test-Path -LiteralPath $RecordPath -PathType Leaf)) {
        throw "No field record at $RecordPath (copy templates/slot-convergence-window.json and fill it in)."
    }
} elseif (Test-Path -LiteralPath $EvidenceRoot) {
    if (-not (Test-Path -LiteralPath $timelinePath -PathType Leaf)) {
        throw "$EvidenceRoot exists but carries no timeline.jsonl, so it is not this script's evidence directory. Evidence is never overwritten -- pick a new directory."
    }
    if (Test-Path -LiteralPath $assertionsPath) {
        throw "$EvidenceRoot is already finalised. Later observations go to a new directory naming this one."
    }
}

function Get-RemoteJson {
    param([string]$Alias, [string]$Path)

    # Single-line only. A multi-line block piped to `pwsh -Command -` over SSH silently does not run
    # -- exit code 0, no output, no error -- which reads exactly like "checked, found nothing".
    $raw = & ssh $Alias "pwsh -NoProfile -Command `"Get-Content -Raw -LiteralPath '$Path'`"" 2>$null
    if (-not $raw) { return $null }
    try { return ($raw | ConvertFrom-Json -AsHashtable) } catch { return $null }
}

$manifestPath = 'D:\zhengyushao\ControlServer\release-manifest.json'

# A window is one package. Appending to a directory whose last checkpoint was taken against another
# package silently merges two windows: 20260910-FW-SC1-operator-inaction holds five frames from the
# afternoon run (package 20260910T022619Z, a session stuck outside Ready), it is not finalised, and the
# commands on 8005-agv-program#19 name exactly that path -- so a re-run pasted from there would judge
# SC1-W-03 red on frames it never took. Checked before anything is created or copied.
if (-not $DatabaseSnapshot -and (Test-Path -LiteralPath $snapshotRoot -PathType Container)) {
    $lastFrame = Get-ChildItem -LiteralPath $snapshotRoot -Directory | Sort-Object Name | Select-Object -Last 1
    $lastIdentityPath = $lastFrame ? (Join-Path $lastFrame.FullName 'identity.json') : $null
    $lastRunId = ($lastIdentityPath -and (Test-Path -LiteralPath $lastIdentityPath -PathType Leaf)) `
        ? [string](Get-Content -LiteralPath $lastIdentityPath -Raw | ConvertFrom-Json).packageRunId : $null
    if ($lastRunId) {
        $liveRunId = [string](Get-RemoteJson -Alias $ServerHost -Path $manifestPath)?.runId
        if (-not $liveRunId) {
            throw "Cannot read the live package's runId from ${ServerHost}:$manifestPath, so whether $EvidenceRoot belongs to this package is unknown. Refusing to append."
        }
        if ($liveRunId -ne $lastRunId) {
            throw "$EvidenceRoot was recorded against package $lastRunId, but ${ServerHost} now runs package $liveRunId. That is a different window -- start a new evidence directory and name this one in its SUMMARY.md."
        }
    }
}

New-Item -ItemType Directory -Path $snapshotRoot -Force | Out-Null
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

function Add-TimelineEvent {
    param([string]$Kind, [hashtable]$Data = @{})

    $event = [ordered]@{ at = (Get-Date).ToString('o'); kind = $Kind; runId = $runId }
    foreach ($key in $Data.Keys) { $event[$key] = $Data[$key] }
    Add-Content -LiteralPath $timelinePath -Value ($event | ConvertTo-Json -Depth 12 -Compress) -Encoding utf8
}

function Write-Json {
    param([string]$Path, [object]$Value)

    # An empty array pipes nothing into ConvertTo-Json, which yields $null and writes a zero-byte
    # file -- the one shape the "every table is written even when empty" rule exists to avoid, since
    # a zero-byte file parses as neither "[]" nor anything else and reads as a truncated write.
    $json = ($Value | ConvertTo-Json -Depth 16) ?? '[]'
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

# --- collection ---------------------------------------------------------------------------------

$existing = @(Get-ChildItem -LiteralPath $snapshotRoot -Directory -ErrorAction SilentlyContinue)
$sequence = '{0:d2}' -f ($existing.Count + 1)
$checkpointDirectory = Join-Path $snapshotRoot "$sequence-$label"
New-Item -ItemType Directory -Path $checkpointDirectory -Force | Out-Null

if ($DatabaseSnapshot) {
    if (-not (Test-Path -LiteralPath $DatabaseSnapshot -PathType Leaf)) {
        throw "No such database snapshot: $DatabaseSnapshot"
    }
    $databasePath = (Resolve-Path -LiteralPath $DatabaseSnapshot).Path
    $collectedFrom = 'local-snapshot'
} else {
    # The server owns this file and is running. Copying the three WAL-mode files together is what
    # the staged G3 runs already do; taking only the .db reads whatever was last checkpointed, which
    # during a live window is minutes behind the state being judged.
    $stageDirectory = Join-Path $StageRoot "$windowId-$runId"
    New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
    foreach ($suffix in @('', '-wal', '-shm')) {
        $remote = "$ServerDatabase$suffix"
        & scp -q "${ServerHost}:$remote" $stageDirectory 2>&1 |
            Tee-Object -FilePath (Join-Path $logRoot "$sequence-scp-database.log") -Append | Out-Null
    }
    $databasePath = Join-Path $stageDirectory (Split-Path -Leaf $ServerDatabase)
    if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf)) {
        throw "Failed to copy $ServerDatabase from $ServerHost -- see $logRoot/$sequence-scp-database.log"
    }
    $collectedFrom = "${ServerHost}:$ServerDatabase"

    # Onboard logs are the only place the vehicle's own view is written down. They are copied on
    # every checkpoint because the client rolls them and a later copy can be missing an earlier line.
    #
    # The directory does not exist until the client has been started once -- a freshly deployed
    # vehicle that has not been launched yet has no logs, which is the state "deploy stops after the
    # copy" leaves behind. That is recorded rather than swallowed: a checkpoint with no onboard logs
    # and no note reads exactly like a checkpoint whose copy failed.
    $onboardLogs = Join-Path $checkpointDirectory 'onboard-logs'
    New-Item -ItemType Directory -Path $onboardLogs -Force | Out-Null
    & scp -q -r "${VehicleHost}:$VehicleLogDirectory/*" $onboardLogs 2>&1 |
        Tee-Object -FilePath (Join-Path $logRoot "$sequence-scp-onboard-logs.log") -Append | Out-Null
    # The server's own log, tailed rather than copied whole -- it is a day-long ndjson and the
    # interesting part is always the last few minutes. When a field window goes wrong this is the
    # first scene: the database says what state things ended in, the log says what threw.
    # Sent as an encoded command rather than an inline quoted string. Nesting PowerShell quoting
    # inside ssh inside PowerShell is where this silently produced nothing the first time: the
    # remote saw a mangled command, wrote nothing to stdout, and the checkpoint looked complete.
    $serverLogPath = Join-Path $checkpointDirectory 'controlserver-tail.ndjson'
    $tailScript = @'
$f = Get-ChildItem 'C:\ProgramData\8005\ControlServer\logs' -Filter 'controlserver-*.ndjson' |
    Sort-Object LastWriteTime | Select-Object -Last 1
if ($f) { Get-Content -LiteralPath $f.FullName -Tail 800 }
'@
    $encodedTail = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($tailScript))
    $tail = & ssh $ServerHost "pwsh -NoProfile -EncodedCommand $encodedTail" 2>$null
    if ($tail) {
        [IO.File]::WriteAllText($serverLogPath, (($tail -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
    } else {
        Add-TimelineEvent -Kind 'server-log-absent' -Data @{
            sequence = $sequence; source = "${ServerHost}:C:\ProgramData\8005\ControlServer\logs"
        }
    }

    $onboardLogCount = @(Get-ChildItem -LiteralPath $onboardLogs -Recurse -File -ErrorAction SilentlyContinue).Count
    if ($onboardLogCount -eq 0) {
        Add-TimelineEvent -Kind 'onboard-logs-absent' -Data @{
            sequence = $sequence
            source   = "${VehicleHost}:$VehicleLogDirectory"
            note     = 'No onboard logs were copied. Expected before the client has ever been started; investigate otherwise.'
        }
    }
}

Add-TimelineEvent -Kind 'checkpoint-started' -Data @{
    label = $label; sequence = $sequence; database = $collectedFrom
}

# --- the two ends' identity ---------------------------------------------------------------------

$identity = [ordered]@{
    runId    = $runId
    windowId = $windowId
    checkpointLabel = $label
    database = $collectedFrom
}

if (-not $DatabaseSnapshot) {
    $onboardSettings = Get-RemoteJson -Alias $VehicleHost -Path 'C:\8005\OnboardHmi\appsettings.json'
    if ($onboardSettings) {
        $identity['onboardRecoveryResumeEnabled'] = $onboardSettings.wireToGate.recoveryResumeEnabled
        $identity['onboardIoModule'] = "$($onboardSettings.ioModule.host):$($onboardSettings.ioModule.port)"
    }
    # Both ends ship in ONE package, so the server's manifest carries the vehicle's commit too --
    # there is no manifest on the vehicle to read, and asking for one finds nothing. The full
    # manifest is written out whole (it inventories every file's SHA-256) but only the three
    # identities go into identity.json: a summary nobody can read is a summary nobody checks.
    $manifest = Get-RemoteJson -Alias $ServerHost -Path $manifestPath
    if ($manifest) {
        Write-Json -Path (Join-Path $checkpointDirectory 'release-manifest.json') -Value $manifest
        $identity['packageRunId'] = $manifest.runId
        $identity['packageCreatedAt'] = $manifest.createdAt
        foreach ($component in @('controlServer', 'onboardHmi', 'protocol')) {
            $entry = $manifest.components[$component]
            if (-not $entry) { continue }
            $identity[$component] = [ordered]@{
                commit = $entry.commit
                branch = $entry.branch
                tag    = $entry.tag
            }
        }
    }
}

# --- the window's own rows ----------------------------------------------------------------------

if (-not $HostDirectory) {
    # The runtime identifier subdirectory is where a win-x64 build actually puts them; the plain
    # framework directory is only populated by a build without a RID.
    $candidates = @(
        (Join-Path $Repository 'src/ControlServer.Host/bin/Release/net8.0/win-x64'),
        (Join-Path $Repository 'src/ControlServer.Host/bin/Release/net8.0'),
        (Join-Path $Repository 'src/ControlServer.Host/bin/Debug/net8.0/win-x64'),
        (Join-Path $Repository 'src/ControlServer.Host/bin/Debug/net8.0'))
    $HostDirectory = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Microsoft.Data.Sqlite.dll') } | Select-Object -First 1
}
if (-not $HostDirectory) {
    throw "Microsoft.Data.Sqlite was not found under src/ControlServer.Host/bin. Build the host once (.\scripts\build.ps1) or pass -HostDirectory."
}

$connection = Open-L2Database -HostDirectory $HostDirectory -DatabasePath $databasePath

# Only this window's demands are exported. The production store carries journeys belonging to the
# plant's own work, and evidence/ is tracked by git -- a blanket SELECT * would commit them.
function Get-WindowRows {
    param([string[]]$DemandIds, [string[]]$AttemptIds, [string]$AgvId)

    $demandList = ($DemandIds | ForEach-Object { "'$_'" }) -join ','
    $attemptList = ($AttemptIds | Where-Object { $_ } | ForEach-Object { "'$_'" }) -join ','
    $rows = [ordered]@{}

    if ($demandList) {
        $rows['AcceptedDemands'] = Invoke-L2Query -Connection $connection `
            -Sql "SELECT * FROM AcceptedDemands WHERE DemandId IN ($demandList)"
        $rows['JourneyDemands'] = Invoke-L2Query -Connection $connection `
            -Sql "SELECT * FROM JourneyDemands WHERE DemandId IN ($demandList)"
        $rows['StationOperations'] = Invoke-L2Query -Connection $connection `
            -Sql "SELECT * FROM StationOperations WHERE DemandId IN ($demandList)"
        $rows['OrderIntents'] = Invoke-L2Query -Connection $connection `
            -Sql "SELECT * FROM OrderIntents WHERE DemandId IN ($demandList)"
        $rows['TransportDemandSuppressions'] = Invoke-L2Query -Connection $connection `
            -Sql "SELECT * FROM TransportDemandSuppressions WHERE DemandId IN ($demandList)"
        # Only this window's load commands, and only whether they were settled. A determinate failure
        # has to settle its LoadBatch (8005-agv-program#39): left pending, it is replayed into every
        # later session under a new generation and the peer tears that session down.
        $commandIds = @($rows['JourneyDemands'] | Where-Object { $_.LoadCommandMessageId } |
            ForEach-Object { "'$($_.LoadCommandMessageId)'" }) -join ','
        if ($commandIds) {
            $rows['ProtocolOutbox'] = Invoke-L2Query -Connection $connection `
                -Sql "SELECT MessageId, MessageType, AcknowledgedAt FROM ProtocolOutbox WHERE MessageId IN ($commandIds)"
        }
        $journeyIds = @($rows['JourneyDemands'] | ForEach-Object { "'$($_.JourneyId)'" }) -join ','
        if ($journeyIds) {
            $rows['JourneyRuntimes'] = Invoke-L2Query -Connection $connection `
                -Sql "SELECT * FROM JourneyRuntimes WHERE JourneyId IN ($journeyIds)"
            $rows['JourneyStops'] = Invoke-L2Query -Connection $connection `
                -Sql "SELECT * FROM JourneyStops WHERE JourneyId IN ($journeyIds)"
        }
    }
    $operationAttempts = @($rows['StationOperations'] | ForEach-Object { [string]$_.SlotOperationAttemptId })
    $allAttempts = @($operationAttempts + $AttemptIds | Where-Object { $_ } | Sort-Object -Unique)
    $attemptList = ($allAttempts | ForEach-Object { "'$_'" }) -join ','
    if ($attemptList) {
        $rows['OperationResults'] = Invoke-L2Query -Connection $connection `
            -Sql "SELECT * FROM OperationResults WHERE SlotOperationAttemptId IN ($attemptList)"
    }
    # Session rows are exported whole when the caller has no agvId to filter by, which is every
    # checkpoint taken during the window -- the field record does not exist yet. Without this the
    # session series is empty at exactly the moments SC1-W-03 is judged from, and the assertion
    # fails for want of data rather than for want of Ready. The fleet has three vehicles, so the
    # whole table is a handful of rows and carries no journey of the plant's own.
    $agvFilter = $AgvId ? " WHERE AgvId = '$AgvId'" : ''
    $rows['SessionRecoveries'] = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM SessionRecoveries$agvFilter"
    $rows['ExceptionRecoverySessions'] = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM ExceptionRecoverySessions$agvFilter"
    # ProtocolInbox is where the vehicle's own phase reporting lands, and it is the only place
    # "which round is this" can be counted from a fact the server received rather than from a
    # screen someone was looking at. Filtered by attempt so the plant's other traffic stays out.
    if ($attemptList) {
        $inbox = Invoke-L2Query -Connection $connection `
            -Sql "SELECT MessageId, MessageType, ReceivedAt, RequestJson FROM ProtocolInbox ORDER BY ReceivedAt"
        $rows['ProtocolInbox'] = @($inbox | Where-Object {
            $text = [string]$_.RequestJson
            $allAttempts | Where-Object { $text -like "*$_*" }
        })
    }
    return $rows
}

# A checkpoint taken before the record exists cannot filter by demand, so it freezes the shape the
# window is judged on: the vehicle's session and whatever demands are live right now. The demand
# ids come from the record at -Finalize, and the final checkpoint carries the full set.
$recordedDemands = @()
$recordedAttempts = @()
$recordedAgvId = $null
$record = $null
if ($Finalize) {
    $record = Get-Content -LiteralPath $RecordPath -Raw | ConvertFrom-Json -AsHashtable
    $recordedAgvId = $record.agvId
    foreach ($scenario in $record.scenarios) {
        if ($scenario.demandId) { $recordedDemands += $scenario.demandId }
        if ($scenario.slotOperationAttemptId) { $recordedAttempts += $scenario.slotOperationAttemptId }
    }
} else {
    $live = Invoke-L2Query -Connection $connection `
        -Sql "SELECT DemandId FROM AcceptedDemands WHERE Status NOT IN ('Succeeded','Cancelled') ORDER BY AcceptedAt DESC LIMIT 8"
    $recordedDemands = @($live | ForEach-Object { [string]$_.DemandId })
}

$rows = Get-WindowRows -DemandIds $recordedDemands -AttemptIds $recordedAttempts -AgvId $recordedAgvId

# Every table is written even when it has no rows. A missing db-StationOperations.json and one
# holding [] are the same picture to a reader and completely different facts -- "the query was never
# run" versus "there was nothing there at that moment", and scenario C is judged on the second.
foreach ($table in @('AcceptedDemands', 'JourneyDemands', 'JourneyRuntimes', 'JourneyStops',
                     'StationOperations', 'OrderIntents', 'OperationResults',
                     'TransportDemandSuppressions', 'ProtocolOutbox',
                     'SessionRecoveries', 'ExceptionRecoverySessions', 'ProtocolInbox')) {
    Write-Json -Path (Join-Path $checkpointDirectory "db-$table.json") -Value @($rows[$table])
}
Write-Json -Path (Join-Path $checkpointDirectory 'identity.json') -Value $identity

$stageSummary = @($rows['JourneyRuntimes'] | Where-Object { $_ } |
    ForEach-Object { "$($_.Stage)/$($_.BlockReasonCode ?? '-')" })
Add-TimelineEvent -Kind 'checkpoint-written' -Data @{
    label = $label
    sequence = $sequence
    directory = "snapshots/$sequence-$label"
    demands = $recordedDemands
    stages = $stageSummary
}

if (-not $Finalize) {
    try { $connection.Close(); $connection.Dispose() } catch { }
    Write-Host "$windowId checkpoint $sequence-$label -> $checkpointDirectory"
    # Explicit, because $LASTEXITCODE still carries whatever scp last returned -- and a checkpoint
    # that copied no onboard logs (the normal state before the client is first started) would
    # otherwise report itself as failed.
    exit 0
}

# --- assertions ---------------------------------------------------------------------------------

$assertions = New-L2Assertions
$facts = [ordered]@{}

# Which IO the window ran on decides what the record can carry. On the real module a person opened the
# doors and photographed them; on the vehicle's slot simulator (the unattended windows, 2026-09-11 on)
# scripts/field/FieldOperator.psm1 played every hand and wrote the record itself, and there is nothing to
# photograph. The record says which; an old hand-filled record without the field is read from the IO
# address the vehicle was actually configured with.
$ioAddress = [string]($identity['onboardIoModule'] ?? $record.ioModule)
$ioKind = [string]($record.ioKind ?? (($ioAddress -match '^(127\.0\.0\.1|localhost):') ? 'SIMULATOR' : 'REAL_MODULE'))
$facts['ioKind'] = $ioKind
$facts['minimumHoldMinutes'] = $MinimumHoldMinutes
if ($record.drivenBy) { $facts['drivenBy'] = [string]$record.drivenBy }

function Get-ScenarioRecord {
    param([string]$Id)
    return @($record.scenarios | Where-Object { $_.id -eq $Id })[0]
}

# Local, as in every L2 scenario: L2.psm1 does not export it. Rows straight from Invoke-L2Query carry
# DBNull for a SQL NULL, which neither `-not` nor `??` treats as null.
function Test-L2Null($value) {
    return ($null -eq $value -or $value -is [System.DBNull])
}

function Get-Rows {
    param([string]$Table, [scriptblock]$Where)
    return @($rows[$Table] | Where-Object $Where)
}

function Get-LoadOperation {
    param([string]$DemandId)
    return @(Get-Rows -Table 'StationOperations' -Where { $_.DemandId -eq $DemandId -and $_.OperationType -eq 'Load' })[0]
}

function Get-PhaseCount {
    # -Inbox reads an earlier checkpoint's rows instead of the final ones: "no second pulse while the
    # door stood open" is a claim about the stretch before anyone closed it.
    param([string]$AttemptId, [string]$Phase, [object[]]$Inbox = @($rows['ProtocolInbox']))

    $count = 0
    foreach ($row in @($Inbox)) {
        if ([string]$row.MessageType -ne 'OperationProgress') { continue }
        try { $payload = ([string]$row.RequestJson | ConvertFrom-Json).payload } catch { continue }
        if ($payload.slotOperationAttemptId -eq $AttemptId -and $payload.phase -eq $Phase) { $count++ }
    }
    return $count
}

function Get-Session {
    return @($rows['SessionRecoveries'])[0]
}

function Get-CheckpointDirectory {
    param([string]$Label)
    return @(Get-ChildItem -LiteralPath $snapshotRoot -Directory |
        Where-Object { $_.Name -like "*-$Label" } | Sort-Object Name)[0]
}

function Get-CheckpointRows {
    <#
    Reads a table back out of an earlier checkpoint. Scenario C's key value -- still not settled
    twenty minutes past the deadline -- is a statement about a moment that the settlement itself
    overwrites, so it can only be judged from the checkpoint taken at that moment.

    Returns $null when the checkpoint is not there at all and an empty array when it is there and
    held no such rows. Those are different failures -- "nobody took that checkpoint" versus "the
    journey was not in the state the scenario assumed" -- and collapsing them sends the reader
    looking for a missing directory that exists.
    #>
    param([string]$Label, [string]$Table)

    $directory = Get-CheckpointDirectory -Label $Label
    if (-not $directory) { return $null }
    $path = Join-Path $directory.FullName "db-$Table.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return @() }
    $content = Get-Content -LiteralPath $path -Raw
    if (-not $content.Trim()) { return @() }
    return @($content | ConvertFrom-Json)
}

function Get-MissingRowReason {
    param([string]$Label)
    return (Get-CheckpointDirectory -Label $Label) `
        ? "checkpoint $Label 在，但里面没有这一趟的旅程行" `
        : "没有 $Label 这个 checkpoint"
}

# --- scenario A: the operator opens the door and walks away (decisions 1 and 2) -------------------

$scenarioA = Get-ScenarioRecord -Id 'A'
if ($scenarioA) {
    $operation = Get-LoadOperation -DemandId $scenarioA.demandId
    $attemptId = $operation ? [string]$operation.SlotOperationAttemptId : [string]$scenarioA.slotOperationAttemptId
    $unlocking = Get-PhaseCount -AttemptId $attemptId -Phase 'UNLOCKING'
    $waiting = Get-PhaseCount -AttemptId $attemptId -Phase 'WAITING_OPERATOR'
    $facts['A.slotOperationAttemptId'] = $attemptId
    $facts['A.phaseCounts'] = "UNLOCKING=$unlocking WAITING_OPERATOR=$waiting"

    $assertions.Add('SC1-A-01', '开门不放料：车载端自动重发开锁脉冲，轮次不设上限（现场至少走到第 3 轮）',
        ($unlocking -ge 3), 'UNLOCKING >= 3', $unlocking)

    # The prompt cadence -- a full OperationTimeout with the door open re-prompts but does not
    # re-pulse -- used to be judged here. It moved to scenario C (SC1-C-06): the plant's station
    # deadline is five minutes, so holding a door open for 120 s at this stop and still loading
    # before the deadline is a race, while scenario C's door stands open for 25 minutes anyway.
    # What this stop has to show instead is that the reopen loop has a way out: the cargo goes in.
    $assertions.Add('SC1-A-02', '重开几轮之后照常放料，装载提交——不设上限的重开有出口',
        ($operation -and [string]$operation.Status -eq 'Committed'),
        'Committed', ($operation ? [string]$operation.Status : '(没有装载操作行)'))

    $failedResults = @(Get-Rows -Table 'OperationResults' -Where {
        $_.SlotOperationAttemptId -eq $attemptId -and [string]$_.OverallOutcome -eq 'FAILED' })
    $assertions.Add('SC1-A-03', '人没放料没有被判成失败：这个 attempt 上没有 FAILED 结果',
        ($failedResults.Count -eq 0), 0, $failedResults.Count)

    $demandRow = @(Get-Rows -Table 'AcceptedDemands' -Where { $_.DemandId -eq $scenarioA.demandId })[0]
    $assertions.Add('SC1-A-04', '需求没有被判 RecoveryRequired',
        ($demandRow -and [string]$demandRow.Status -ne 'RecoveryRequired'),
        '不是 RecoveryRequired', ($demandRow ? [string]$demandRow.Status : '(没有这一行)'))

    # Unattended, this is read from the onboard automation snapshot's availableRecoveryActions, narrowed to
    # the protocol's three recovery actions: with the window open, 取消装货 and 修正装货 show during an
    # ordinary load and are not an entry that needs an administrator (FieldOperator.psm1 says why).
    $assertions.Add('SC1-A-05', "HMI 上没有出现恢复入口——操作员迟疑不需要管理员凭据（$($scenarioA.observedBy ? '驱动脚本读车载端快照 availableRecoveryActions 里的三个恢复动作' : '现场观察')）",
        ($false -eq $scenarioA.observedRecoveryEntryVisible),
        $false, $scenarioA.observedRecoveryEntryVisible)
}

# --- scenario B: closed, empty, past the deadline (decision 5) ------------------------------------

$scenarioB = Get-ScenarioRecord -Id 'B'
if ($scenarioB) {
    $operation = Get-LoadOperation -DemandId $scenarioB.demandId
    $attemptId = $operation ? [string]$operation.SlotOperationAttemptId : [string]$scenarioB.slotOperationAttemptId
    $result = @(Get-Rows -Table 'OperationResults' -Where { $_.SlotOperationAttemptId -eq $attemptId } |
        Sort-Object ReceivedAt)[-1]
    $facts['B.slotOperationAttemptId'] = $attemptId
    $facts['B.operationStatus'] = $operation ? [string]$operation.Status : '(没有这一行)'

    $assertions.Add('SC1-B-01', '车载端报 FAILED',
        ($result -and [string]$result.OverallOutcome -eq 'FAILED'),
        'FAILED', ($result ? [string]$result.OverallOutcome : '(没有结果行)'))

    # This is the whole of decision 5 in one row: determinate failure is what the server writes when
    # all three physical fields are unambiguous. RecoveryRequired here would mean the vehicle
    # reported something it could not read, which is a different scenario.
    $evidence = $null
    if ($result -and $result.EvidenceJson) {
        try { $evidence = @([string]$result.EvidenceJson | ConvertFrom-Json) } catch { $evidence = $null }
    }
    # EvidenceJson is written by JsonSerializer with default options, so SlotBusinessState arrives as
    # its number (Empty=0, Occupied=1, Unknown=2), not its name. Comparing against 'Unknown' alone
    # never matched, and this assertion stayed green on exactly the slot it exists to catch.
    $stateNames = @{ '0' = 'Empty'; '1' = 'Occupied'; '2' = 'Unknown' }
    $stateName = { param($state) $stateNames[[string]$state] ?? [string]$state }
    $determinate = $evidence -and @($evidence | Where-Object {
        (& $stateName $_.State) -eq 'Unknown' -or -not $_.DoorLocked -or -not $_.UnlockOutputReset }).Count -eq 0
    $facts['B.slotEvidence'] = $evidence ? (($evidence | ForEach-Object {
        "slot$($_.SlotNumber):State=$(& $stateName $_.State)/DoorLocked=$($_.DoorLocked)/UnlockOutputReset=$($_.UnlockOutputReset)" }) -join '; ') : '(没有 EvidenceJson)'

    $assertions.Add('SC1-B-02', '三个物理字段都是明确的：State 不是 Unknown、锁已闭、开锁输出已复位',
        [bool]$determinate, '三字段齐全且明确', $facts['B.slotEvidence'])

    $assertions.Add('SC1-B-03', '服务端走确定失败而不是 RecoveryRequired',
        ($operation -and [string]$operation.Status -eq 'Failed'),
        'Failed', $facts['B.operationStatus'])

    # 8005-agv-program#39 changed this. The demand used to be left Accepted for a
    # LoadTaskCancellation that nothing ever raises, and the journey sat at the stop for good; the
    # server now ends the demand itself, suppresses its business key and settles the load command.
    $demandRow = @(Get-Rows -Table 'AcceptedDemands' -Where { $_.DemandId -eq $scenarioB.demandId })[0]
    $suppression = @(Get-Rows -Table 'TransportDemandSuppressions' -Where { $_.DemandId -eq $scenarioB.demandId })[0]
    $membership = @(Get-Rows -Table 'JourneyDemands' -Where { $_.DemandId -eq $scenarioB.demandId })[0]
    $command = $membership ? @(Get-Rows -Table 'ProtocolOutbox' -Where { $_.MessageId -eq $membership.LoadCommandMessageId })[0] : $null
    $commandSettled = $command -and -not (Test-L2Null $command.AcknowledgedAt)
    $facts['B.demandEnd'] = "$($demandRow ? $demandRow.Status : '(没有需求行)') / " +
        "$($suppression ? $suppression.ReasonCode : '(没有抑制行)') / " +
        "$($command ? ($commandSettled ? 'command settled' : 'command pending') : '(没有装货命令行)')"
    $assertions.Add('SC1-B-04', '确定失败之后服务端自己终结这条需求：Cancelled、按 CANCELLED_BY_STATION_TIMEOUT 永久抑制、装货命令已结算',
        ($demandRow -and [string]$demandRow.Status -eq 'Cancelled' -and
            $suppression -and [string]$suppression.ReasonCode -eq 'CANCELLED_BY_STATION_TIMEOUT' -and $commandSettled),
        'Cancelled / CANCELLED_BY_STATION_TIMEOUT / command settled', $facts['B.demandEnd'])

    # The exit #39 was about. "Not at this stop" would be too strong: another loading round at the same
    # stop is a legitimate way out. Stuck is exactly this stop, still waiting for a load result.
    $runtimeRow = $membership ? @(Get-Rows -Table 'JourneyRuntimes' -Where { $_.JourneyId -eq $membership.JourneyId })[0] : $null
    $facts['B.journeyPosition'] = $runtimeRow ? "$($runtimeRow.CurrentStopSequence)/$($runtimeRow.Stage)" : '(没有旅程行)'
    $assertions.Add('SC1-B-06', '确定失败之后旅程自己离开这一格，不需要任何人按任何按钮',
        ($runtimeRow -and -not ([string]$runtimeRow.CurrentStopSequence -eq [string]$membership.StopSequence -and
            [string]$runtimeRow.Stage -eq 'AwaitingLoadResult')),
        "不是 $($membership ? $membership.StopSequence : '?')/AwaitingLoadResult", $facts['B.journeyPosition'])

    # A single point, and the description says so. Whether the session held Ready for the whole
    # window is a different claim and it is SC1-W-03's, judged from the checkpoint series.
    $session = Get-Session
    $assertions.Add('SC1-B-05', '结算之后会话仍在 Ready：确定失败是业务结果，不是会话故障',
        ($session -and [string]$session.Readiness -eq 'Ready'),
        'Ready', ($session ? "$($session.Readiness) / $($session.ReasonCode)" : '(没有会话行)'))
}

# --- scenario C: door left open, past the deadline (decision 4) -----------------------------------

$scenarioC = Get-ScenarioRecord -Id 'C'
if ($scenarioC) {
    $journeyRow = @(Get-Rows -Table 'JourneyDemands' -Where { $_.DemandId -eq $scenarioC.demandId })[0]
    $runtimeRow = $journeyRow ? @(Get-Rows -Table 'JourneyRuntimes' -Where { $_.JourneyId -eq $journeyRow.JourneyId })[0] : $null
    $operation = Get-LoadOperation -DemandId $scenarioC.demandId
    $facts['C.finalStage'] = $runtimeRow ? "$($runtimeRow.Stage) / $($runtimeRow.BlockReasonCode ?? '-')" : '(没有旅程行)'

    # The two checkpoints are the assertion. Named in the record so a window that had to take them
    # under different labels still judges the right ones.
    $deadlineLabel = [string]($scenarioC.deadlineCheckpoint ?? 'c-deadline-reached')
    $plus20Label = [string]($scenarioC.stillWaitingCheckpoint ?? 'c-plus-20min')

    $atDeadline = Get-CheckpointRows -Label $deadlineLabel -Table 'JourneyRuntimes'
    $atDeadlineRow = @($atDeadline | Where-Object { -not $journeyRow -or $_.JourneyId -eq $journeyRow.JourneyId })[0]
    $assertions.Add('SC1-C-01', '站点期限到期时告警挂上了 STATION_TIMEOUT_DOOR_NOT_CLOSED',
        ($atDeadlineRow -and [string]$atDeadlineRow.BlockReasonCode -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED'),
        'STATION_TIMEOUT_DOOR_NOT_CLOSED',
        ($atDeadlineRow ? [string]$atDeadlineRow.BlockReasonCode : "($(Get-MissingRowReason -Label $deadlineLabel))"))

    $assertions.Add('SC1-C-02', '期限到期时停靠没有被关闭，stage 停在 AwaitingLoadResult 而不是 Blocked',
        ($atDeadlineRow -and [string]$atDeadlineRow.Stage -eq 'AwaitingLoadResult'),
        'AwaitingLoadResult',
        ($atDeadlineRow ? [string]$atDeadlineRow.Stage : "($(Get-MissingRowReason -Label $deadlineLabel))"))

    $atPlus20 = Get-CheckpointRows -Label $plus20Label -Table 'JourneyRuntimes'
    $atPlus20Row = @($atPlus20 | Where-Object { -not $journeyRow -or $_.JourneyId -eq $journeyRow.JourneyId })[0]
    $plus20Operations = Get-CheckpointRows -Label $plus20Label -Table 'StationOperations'
    $plus20Load = @($plus20Operations | Where-Object { $_.DemandId -eq $scenarioC.demandId -and $_.OperationType -eq 'Load' })[0]

    # The ticket names this value: waiting does not decay into ending.
    $assertions.Add('SC1-C-03', "期限到期后再等 $MinimumHoldMinutes 分钟依然不结束——等待不会自己退化成结束",
        ($atPlus20Row -and [string]$atPlus20Row.Stage -eq 'AwaitingLoadResult' -and
            [string]$atPlus20Row.BlockReasonCode -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED' -and
            $plus20Load -and [string]$plus20Load.Status -eq 'Prepared'),
        'AwaitingLoadResult / STATION_TIMEOUT_DOOR_NOT_CLOSED / Prepared',
        ($atPlus20Row ? "$($atPlus20Row.Stage) / $($atPlus20Row.BlockReasonCode) / $($plus20Load ? $plus20Load.Status : '(没有装载操作行)')"
                      : "($(Get-MissingRowReason -Label $plus20Label))"))

    $deadlineAt = $scenarioC.deadlineAt ? [datetimeoffset]::Parse([string]$scenarioC.deadlineAt) : $null
    $stillWaitingAt = $scenarioC.stillWaitingObservedAt ? [datetimeoffset]::Parse([string]$scenarioC.stillWaitingObservedAt) : $null
    $gap = ($deadlineAt -and $stillWaitingAt) ? ($stillWaitingAt - $deadlineAt) : $null
    $assertions.Add('SC1-C-04', "那次复查确实在期限之后 $MinimumHoldMinutes 分钟以上",
        ($gap -and $gap.TotalMinutes -ge $MinimumHoldMinutes),
        ">= $MinimumHoldMinutes 分钟", ($gap ? ('{0:N1} 分钟' -f $gap.TotalMinutes) : '(现场记录没有填这两个时刻)'))

    # Judged at the +20 min checkpoint, before anyone touched the door: by then it has stood open for
    # well over one OperationTimeout (120 s on agv01), so the vehicle has re-prompted -- and must still
    # have pulsed exactly once, because re-opening a lock that is already open is not a prompt.
    $plus20Inbox = Get-CheckpointRows -Label $plus20Label -Table 'ProtocolInbox'
    $plus20Attempt = $plus20Load ? [string]$plus20Load.SlotOperationAttemptId : $null
    $plus20Unlocking = ($plus20Attempt -and $null -ne $plus20Inbox) ? (Get-PhaseCount -AttemptId $plus20Attempt -Phase 'UNLOCKING' -Inbox $plus20Inbox) : $null
    $plus20Waiting = ($plus20Attempt -and $null -ne $plus20Inbox) ? (Get-PhaseCount -AttemptId $plus20Attempt -Phase 'WAITING_OPERATOR' -Inbox $plus20Inbox) : $null
    $assertions.Add('SC1-C-06', '门一直开着：晾过 OperationTimeout 之后提示还在走而脉冲只打过一次——提示节拍不重复开一把已经开着的锁',
        ($null -ne $plus20Unlocking -and $plus20Unlocking -eq 1 -and $plus20Waiting -ge 2),
        'UNLOCKING = 1 / WAITING_OPERATOR >= 2',
        ($null -ne $plus20Unlocking ? "UNLOCKING $plus20Unlocking / WAITING_OPERATOR $plus20Waiting"
                                    : "($(Get-MissingRowReason -Label $plus20Label))"))

    # Closing the door does not settle on the first round: decision 1 wins once more and re-opens it
    # (#25 measured 370 ms), and only the next opposite state settles. Hence "the round after".
    $assertions.Add('SC1-C-05', '关门之后按真实 IO 读数结算（决策 1 会先重开一轮），告警随之消失',
        ($runtimeRow -and [string]$runtimeRow.BlockReasonCode -ne 'STATION_TIMEOUT_DOOR_NOT_CLOSED' -and
            $operation -and [string]$operation.Status -in @('Committed', 'Failed')),
        '告警清空 / 操作已结算', $facts['C.finalStage'] + ' / ' + ($operation ? [string]$operation.Status : '(没有装载操作行)'))

}

# --- window-level ---------------------------------------------------------------------------------

$photoPointers = @($record.photoPointers) + @($record.scenarios | ForEach-Object { $_.photoPointers }) |
    Where-Object { $_ }
if ($ioKind -eq 'SIMULATOR') {
    # Nobody stood at the vehicle, so there is no photograph to point at -- and asking for one would only
    # teach the next window to invent a pointer. What replaces it is that the record was written by the
    # driver that played the operator, from what it did, rather than typed afterwards.
    $assertions.Add('SC1-W-01', '三个场景都有现场记录，且记录由驱动脚本按实际动作写出（模拟器 IO 下无人到场，照片不适用）',
        (@($record.scenarios).Count -ge 3 -and [bool]$record.drivenBy),
        '3 个场景 / drivenBy 非空',
        "$(@($record.scenarios).Count) 个场景 / drivenBy=$($record.drivenBy ?? '(空)')")
} else {
    $assertions.Add('SC1-W-01', '三个场景都有现场记录，且留下了照片指针（照片本身不进 git）',
        (@($record.scenarios).Count -ge 3 -and $photoPointers.Count -gt 0),
        '3 个场景 / 至少一个照片指针',
        "$(@($record.scenarios).Count) 个场景 / $($photoPointers.Count) 个指针")
}

$assertions.Add('SC1-W-02', '窗口内恢复入口是开着的——否则决策 2 只验证了一半',
    ($true -eq $record.recoveryWindowOpen),
    $true, $record.recoveryWindowOpen)

# The one assertion that actually spans the window. Every checkpoint carries the vehicle's session
# row, so "it never left Ready" is answerable -- from the series, never from the final state, which
# a session that failed and recovered would look exactly like.
$checkpointDirectories = @(Get-ChildItem -LiteralPath $snapshotRoot -Directory | Sort-Object Name)
$readinessSeries = foreach ($directory in $checkpointDirectories) {
    $path = Join-Path $directory.FullName 'db-SessionRecoveries.json'
    $sessionRows = (Test-Path -LiteralPath $path -PathType Leaf) ? @(Get-Content -LiteralPath $path -Raw | ConvertFrom-Json) : @()
    $row = @($sessionRows | Where-Object { -not $recordedAgvId -or $_.AgvId -eq $recordedAgvId })[0]
    "$($directory.Name)=$($row ? $row.Readiness : '(没有会话行)')"
}
$readinessSeries = @($readinessSeries)
$assertions.Add('SC1-W-03', '每一个 checkpoint 上会话都停在 Ready——全程没有把开着的仓门当成会话故障',
    ($readinessSeries.Count -gt 0 -and @($readinessSeries | Where-Object { $_ -notlike '*=Ready' }).Count -eq 0),
    '每个 checkpoint 都是 Ready', ($readinessSeries -join '; '))

try { $connection.Close(); $connection.Dispose() } catch { }

$outcome = $assertions.AllPassed() ? 'PASS' : 'FAIL'

$document = [ordered]@{
    schemaVersion = 1
    window        = $windowId
    windowName    = '现场窗口一：三个操作员不作为场景'
    runId         = $runId
    outcome       = $outcome
    identity      = $identity
    fieldRecord   = $record
    facts         = $facts
    assertions    = $assertions.Items
}
Write-Json -Path $assertionsPath -Value $document

$assertionRows = ($assertions.Items | ForEach-Object {
    $expected = ($_.expected | Out-String).Trim() -replace '\r?\n', ' '
    $actual = ($_.actual | Out-String).Trim() -replace '\r?\n', ' '
    "| $($_.id) | $($_.description) | $($_.outcome) | ``$expected`` | ``$actual`` |"
}) -join "`n"
$factRows = ($facts.Keys | ForEach-Object { "| ``$_`` | ``$($facts[$_])`` |" }) -join "`n"
$photoRows = ($photoPointers | ForEach-Object { "- ``$_``" }) -join "`n"
$checkpointRows = (@(Get-ChildItem -LiteralPath $snapshotRoot -Directory | Sort-Object Name) | ForEach-Object {
    "- ``snapshots/$($_.Name)/``" }) -join "`n"

$simulated = $ioKind -eq 'SIMULATOR'
$titleSuffix = $simulated ? '在车上的 slots-simulator 上无人驱动验收' : '在真实 IO 模块上验收'
$peopleCell = $simulated ? "无人到场，$($record.drivenBy ?? '(未记录驱动)')" : (@($record.observers) -join '、')
$photoSection = $simulated ? '模拟器 IO 下无人到场，没有照片；现场记录由驱动脚本按实际动作写出。' : $photoRows
$provenParagraph = $simulated ? @"
**证明了**：ADR-cross-0058 的决策 1、2、4、5 在 ``$($record.agvId)``（$($record.site)）上、IO 接 slots-simulator
（``$ioAddress``）时，**软件闭环**的行为与判据一致；操作员的每一个动作由驱动脚本冒充，记录与 checkpoint 由它按实际动作写出。
车是不是真的在线路上走，这份证据不回答——现场窗口是，L2 彩排不是，看「现场」一栏。

**没有证明**：光幕极性、锁反馈时序与机械弹开行为。ADR-cross-0058 原话是模拟器证明不了这三样，而本窗口的 IO 正是模拟器——
这是 2026-09-11 改为全部无人值守时有意放弃的，见地图 Out of scope。
"@ : @"
**证明了**：ADR-cross-0058 的决策 1、2、4、5 在 ``agv01`` 配真实 Modbus IO 模块
（``$ioAddress``）上的行为与判据一致——光幕极性、锁反馈时序与机械弹开
行为都是真的，这正是模拟器证明不了的那三样。
"@
$holdNote = ($MinimumHoldMinutes -lt 20) `
    ? "**注意：本次按 ``-MinimumHoldMinutes $MinimumHoldMinutes`` 判场景 C，低于现场要求的 20 分钟，只能算彩排，不能当现场窗口的证据。**" `
    : ''

$summary = @"
# 现场窗口一证据：三个操作员不作为场景$titleSuffix

$holdNote

结论：**$outcome**

对应 [ADR-cross-0058](../../../8005-agv-program/docs/adr/cross/0058-slot-convergence.md) 的决策 1、2、4、5，
以及地图票 $($simulated ? '[现场窗口一（无人）](https://github.com/trytoreachpeak0/8005-agv-program/issues/45)' : '[现场窗口一](https://github.com/trytoreachpeak0/8005-agv-program/issues/19)')。

## 身份

| 项 | 值 |
| --- | --- |
| runId | ``$runId`` |
| 窗口 | ``$windowId`` 装卸站收敛语义 |
| 现场 | $($record.site) |
| 现场人员 | $peopleCell |
| agvId | ``$($record.agvId)`` |
| IO | ``$ioAddress``（$ioKind） |
| 场景 C 等待下限 | $MinimumHoldMinutes 分钟 |
| 恢复窗口 | $($record.recoveryWindowOpen ? '窗口内开启' : '**未开启**') |
| 数据库 | ``$($identity.database)`` |

**这个窗口号与 ``evidence/field/README.md`` 里的 ``W1``/``W2``/``W3`` 不是一回事**：那三个是仓位配置就绪
那条线的窗口（车辆资格 / 多车与等待点 / 自动充电）。这里的 ``$windowId`` 是装卸站收敛语义的第一次现场窗口。

## 判据

| 判据 | 说明 | 结论 | 期望 | 实际 |
| --- | --- | --- | --- | --- |
$assertionRows

## 实测事实

不作判据，但下一次窗口和现场归因都要用：

| 项 | 值 |
| --- | --- |
$factRows

## 照片指针

照片本身不进 git，这里只留指针。

$photoSection

## checkpoint 序列

场景 C 的关键值是「期限到期后再等 20 分钟依然不结束」，而结算本身会覆盖掉那一刻的状态——
所以它只能由当时抓下的 checkpoint 回答，不能事后从库里读。

$checkpointRows

## 目录内容

- ``assertions.json`` —— 机器可读的判据结论，含现场记录原文与实测事实
- ``timeline.jsonl`` —— 一行一次 checkpoint，只追加
- ``logs/`` —— 每次远端拷贝的输出
- ``snapshots/<序号>-<label>/`` —— 每个 checkpoint 当时的库行、两端安装清单与车载端日志

**整个 ``controlserver.db`` 不在这里**。它是生产库，装着与本窗口无关的旅程，而 ``evidence/`` 进 git。
完整库落在 ``$StageRoot`` 下的 ``$windowId-<runId>``，需要时按 runId 找；
``run-demand-bearing-g3-vectors.ps1`` 的 ``-FieldRunRoot`` 要的就是那个形状。

## 这份证据证明了什么，没证明什么

$provenParagraph

**没有证明**：完整闭环。受理→取货→录 SUBLOT→装货→安全检查→去关卡→卸货→收尾，以及自动充电与取消订单
两条支路，属于现场窗口二（[#20](https://github.com/trytoreachpeak0/8005-agv-program/issues/20)），不在这份证据里。

**没有证明**：另外两台车。本窗口只在 ``$($record.agvId)`` 上跑；三台上位机是同一个镜像的克隆，但 IO 接线是
逐车的，一台的绿说明不了另外两台。
"@

[IO.File]::WriteAllText((Join-Path $EvidenceRoot 'SUMMARY.md'), $summary + "`n", [Text.UTF8Encoding]::new($false))
Add-TimelineEvent -Kind 'window-finalised' -Data @{ outcome = $outcome }

Write-Host "$windowId $outcome -> $EvidenceRoot"
if ($outcome -ne 'PASS') {
    # Red evidence stays exactly where it is. A correction goes to a new directory naming this one.
    exit 1
}
