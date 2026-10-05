[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$StageRoot,
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,
    # Optional since control-server#453. Given, it is a run root produced by an authorised field run: its
    # controlserver.db is the demand-bearing store this runner restores. Nothing here writes to it; the
    # copy in StageRoot is what the server opens. A store the synthetic rig wrote is refused
    # (control-server#460, Assert-FieldRunStoreIsNotGenerated): this path exempts the store's protocolCommit
    # and calls the store field state, and neither holds for a generated one.
    # Left out, the store is generated on the spot: the bound ControlServer commit's own L2 scenario
    # demand-bearing-store-at-unload drives a synthetic peer and the fake RIoT to the same shape and
    # exports it. The only field store ever used (fullloop-20260829T131549Z, agv01 and the real RIoT)
    # was lost, and making another needs a real vehicle; a generated one is weaker evidence, and the
    # run says so in storeProvenance.
    [string]$FieldRunRoot,
    # Selects what this run CERTIFIES, not what it runs. A G3 run is one end-to-end scenario against
    # real peers, not a filterable set of tests, so -Slice narrows the evidence written and never the
    # scenario driven: with it, one gate-result.json for that slice; without it, one for each slice
    # this runner claims. Naming a slice this runner does not claim is refused before anything is
    # created -- see scripts/g3-slice-evidence.ps1 for the claim table and the 2026-09-09 ruling.
    [ValidatePattern('^FP-IS-(0[0-9]|1[0-5])$')][string]$Slice,
    # For checking a change before the shared binding has moved, as run-journey-g3.ps1 has it: clone this
    # ControlServer commit instead of the bound one. The run records controlServerCommitSource =
    # SELF_CHECK_OVERRIDE in run-result.json and every gate-result.json, and is not gate evidence: since
    # control-server#460 every slice of it is graded formalSlicePass false, formalSliceWithheldReason
    # SELF_CHECK_OVERRIDE (g3-slice-evidence.ps1, Get-G3FormalSlicePass). The binding
    # itself moves only in an exit ticket's first step (control-server#453 needed one: its generator scenario
    # exists only in commits that contain it). There is no onboard counterpart: this runner clones no onboard.
    [ValidatePattern('^[0-9a-f]{40}$')][string]$SelfCheckControlServerCommit,
    [string]$ControlServerRepository = (Split-Path -Parent $PSScriptRoot),
    # Both the peer commit binding and the synthetic-peer harness are owned by the staged G3 runner and
    # read back from it rather than restated here, so the two runners can never drift apart.
    [string]$SharedRunnerSource = (Join-Path $PSScriptRoot 'run-staged-g3.ps1'),
    [string]$CommitBindingFunctionSource = (Join-Path $PSScriptRoot 'run-staged-g3-restart.ps1')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Plaintext loopback, like the restart runner: this exercises the message plane against a restored
# store, not the transport, so it installs no temporary trust root and runs unattended. The ports sit
# clear of the field run (58105/58107) and the staged runners (58205/58207) so an accidental overlap
# fails to bind instead of silently talking to the wrong server.
$controlPort = 58305
$healthPort = 58307
$runStartedAt = [DateTimeOffset]::UtcNow
$runId = $runStartedAt.ToString('yyyyMMddTHHmmssfffZ')

function Get-ScriptFunction {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The function source does not exist: $Path"
    }
    $parseErrors = $null
    $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) {
        throw "The function source does not parse: $Path"
    }
    $definitions = @($ast.FindAll(
        { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                       $node.Name -eq $Name },
        $true))
    if ($definitions.Count -ne 1) {
        throw "Expected exactly one function '$Name' in $Path, found $($definitions.Count)."
    }
    return $definitions[0].Extent.Text
}

function Get-HarnessSource {
    param([Parameter(Mandatory)][string]$Path)

    $parseErrors = $null
    $tokens = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) {
        throw "The harness source does not parse: $Path"
    }
    $literals = @($tokens | Where-Object {
        $_.Kind -eq 'HereStringLiteral' -and $_.Value.Contains('class StagedG3TlsHarness')
    })
    if ($literals.Count -ne 1) {
        throw "Expected exactly one harness here-string in $Path, found $($literals.Count)."
    }
    return $literals[0].Value
}

# Defined by the staged restart runner; taken from there rather than copied, for the same reason the
# commit values themselves are.
Invoke-Expression (Get-ScriptFunction -Path $CommitBindingFunctionSource -Name 'Get-SharedCommitBinding')
# The staged runner's error report (control-server#306), taken the same way (control-server#460): printed and
# saved as runner-error.json directly after the run's try, before anything can throw over it.
Invoke-Expression (Get-ScriptFunction -Path $SharedRunnerSource -Name 'Write-StagedRunError')

# What marks a store as written by the synthetic L2 rig rather than by a field run, read from the baseline
# Read-ControlDatabase takes of it. Invoke-L2Scenario.ps1 names every vehicle BROKERX-L2-<n> / AGV-L2-<n> and
# every L2 scenario's sublot L2-SUBLOT-..., which becomes the TransportDemandKey; no field run carries either
# (the 2026-08-29 store reads Q26081298-1, BROKERX-0c20..., 老厂前线新多仓位1). Empty for a field store.
function Get-GeneratedStoreMarkers {
    param([Parameter(Mandatory)]$Baseline)

    $markers = [System.Collections.Generic.List[string]]::new()
    foreach ($row in @($Baseline.acceptedDemandRows)) {
        if ("$($row['transportDemandKey'])" -like 'L2-SUBLOT-*') {
            $markers.Add("AcceptedDemands.TransportDemandKey=$($row['transportDemandKey'])")
        }
    }
    foreach ($row in @($Baseline.vehicleClaimRecordRows)) {
        if ("$($row['vehicleKey'])" -like 'BROKERX-L2-*') {
            $markers.Add("VehiclePurposeClaimRecords.VehicleKey=$($row['vehicleKey'])")
        }
    }
    foreach ($row in @($Baseline.sessionRecoveryRows)) {
        if ("$($row['agvId'])" -like 'AGV-L2-*') { $markers.Add("SessionRecoveries.AgvId=$($row['agvId'])") }
    }
    return ,$markers
}

# control-server#460. -FieldRunRoot says "this is real state an authorised field run wrote", and the run then
# exempts the store's protocolCommit from the identity assertion (TICKET_17) and writes that note into the
# evidence. cs#453's review fed a generated store through that path and got a PASS stamped as field state:
# the exemption, and the field-state claim, were both available to a store that never saw a vehicle. So a
# FIELD_RUN store that carries the rig's marks is refused before the server is started on it.
function Assert-FieldRunStoreIsNotGenerated {
    param(
        [Parameter(Mandatory)][string]$StoreSource,
        [Parameter(Mandatory)]$Baseline
    )

    if ($StoreSource -ne 'FIELD_RUN') { return }
    $markers = Get-GeneratedStoreMarkers -Baseline $Baseline
    if ($markers.Count -ne 0) {
        throw ('FIELD_RUN_STORE_IS_GENERATED: -FieldRunRoot was given a store the synthetic L2 rig wrote, not a ' +
               'field run: ' + ($markers -join '; ') + '. Leave -FieldRunRoot out to generate a store on purpose; ' +
               'the field-store exemption and the field-state note do not apply to it.')
    }
}

$commitBinding = Get-SharedCommitBinding -Path $SharedRunnerSource
$ControlServerCommit = $commitBinding['ControlServerCommit']
$OnboardCommit = $commitBinding['OnboardCommit']
$SimulatorCommit = $commitBinding['SimulatorCommit']
$ProtocolCommit = $commitBinding['ProtocolCommit']
$sharedRunnerSha256 = (Get-FileHash -LiteralPath $SharedRunnerSource -Algorithm SHA256).Hash.ToLowerInvariant()
$commitBindingFunctionSha256 =
    (Get-FileHash -LiteralPath $CommitBindingFunctionSource -Algorithm SHA256).Hash.ToLowerInvariant()

$G3RunKind = 'DEMAND_BEARING_G3_RESULT_AND_RIOT_UNKNOWN_VECTORS_NO_MOVEMENT'
. (Join-Path $PSScriptRoot 'g3-slice-evidence.ps1')

# control-server#466: the repository this script lives in (not -ControlServerRepository), whether any path
# parameter that decides what the run reads was moved off its default -- -SharedRunnerSource also supplies the
# harness this run compiles and its error report -- and the binding as HEAD committed it, which the commits read
# off -SharedRunnerSource are compared with. Before anything is written.
$runnerProvenance = Get-G3RunnerProvenance -ScriptRoot $PSScriptRoot -Inputs ([ordered]@{
        SharedRunnerSource = @{ Given = $SharedRunnerSource; Default = (Join-Path $PSScriptRoot 'run-staged-g3.ps1') }
        CommitBindingFunctionSource = @{ Given = $CommitBindingFunctionSource; Default = (Join-Path $PSScriptRoot 'run-staged-g3-restart.ps1') }
        ControlServerRepository = @{ Given = $ControlServerRepository; Default = (Split-Path -Parent $PSScriptRoot) }
    })
Write-G3RunnerProvenance -Provenance $runnerProvenance
$runnerCommit = $runnerProvenance.runnerCommit
if ($null -eq $runnerCommit) { throw "Unable to read the runner commit: $($runnerProvenance.runnerSource)" }
$runnerWorktreeClean = $runnerProvenance.runnerWorktreeClean
$bindingSources = Get-G3CommitSources -Binding ($runnerProvenance.bindingAtHead ?? $commitBinding) -Actual $commitBinding
$controlServerCommitSource = $bindingSources['controlServerCommitSource']
if (-not [string]::IsNullOrEmpty($SelfCheckControlServerCommit)) {
    $ControlServerCommit = $SelfCheckControlServerCommit
    $controlServerCommitSource = 'SELF_CHECK_OVERRIDE'
}
$onboardCommitSource = $bindingSources['onboardCommitSource']
$simulatorCommitSource = $bindingSources['simulatorCommitSource']
$protocolCommitSource = $bindingSources['protocolCommitSource']
# Before the clones and the builds, not after: naming a slice this runner cannot certify
# should cost a message, not an hour of cloning and publishing.
if (-not [string]::IsNullOrEmpty($Slice)) { Assert-G3SliceIsClaimedBy -RunKind $G3RunKind -Slice $Slice }

if (Test-Path -LiteralPath $StageRoot) { throw "StageRoot must not already exist: $StageRoot" }
if (Test-Path -LiteralPath $EvidenceRoot) { throw "EvidenceRoot must not already exist: $EvidenceRoot" }
# FIELD_RUN restores a store a real vehicle wrote; SYNTHETIC_RIG generates one from the bound commit.
$storeSource = if ([string]::IsNullOrEmpty($FieldRunRoot)) { 'SYNTHETIC_RIG' } else { 'FIELD_RUN' }
$storeGeneratorScenario = 'demand-bearing-store-at-unload'
if ($storeSource -eq 'FIELD_RUN') {
    $fieldDatabase = Join-Path $FieldRunRoot 'controlserver.db'
    if (-not (Test-Path -LiteralPath $fieldDatabase -PathType Leaf)) {
        throw "FieldRunRoot has no controlserver.db: $FieldRunRoot"
    }
} else {
    # The generator comes from the bound clone, like run-journey-g3.ps1's scenarios: the commit that writes the
    # store is the commit under test, and a commit older than control-server#453 has none. Asked of the
    # repository before anything is created: raised later, inside the run, the gate-result writer's own refusal
    # of a store that was never read is what the operator would see instead.
    & git -C $ControlServerRepository cat-file -e "${ControlServerCommit}^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw ("The ControlServer commit $ControlServerCommit ($controlServerCommitSource) does not exist in " +
               "$ControlServerRepository. Fetch it first.")
    }
    & git -C $ControlServerRepository cat-file -e "${ControlServerCommit}:scripts/l2/scenarios/$storeGeneratorScenario.ps1" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw ("The ControlServer commit $ControlServerCommit ($controlServerCommitSource) has no $storeGeneratorScenario " +
               'scenario. Move the binding to a commit that has it, pass -SelfCheckControlServerCommit for a self-check, ' +
               'or pass -FieldRunRoot.')
    }
}
New-Item -ItemType Directory -Path $StageRoot, $EvidenceRoot | Out-Null
# Absolute from here on. Unlike run-journey-g3.ps1, this runner does not hand an $EvidenceRoot-derived
# path to any child process today -- measured, not assumed, with the taint analysis kept at
# evidence/g3/cs264-path-audit/. Its Push-Location window wraps the child call and nothing else, and
# every write from this process happens outside it, so a relative -EvidenceRoot would in fact survive.
#
# It is absolutised anyway, because "safe" here rests on a fact about today's call sites rather than on
# anything structural: the day someone passes an evidence path to a child that runs inside the stage
# tree, the failure is silent and expensive. run-journey-g3.ps1 carries what that looks like, and it
# cost a G3 slot to find out (control-server#211). One line here means nobody has to find out twice.
$EvidenceRoot = (Resolve-Path -LiteralPath $EvidenceRoot).Path
# $StageRoot is a different matter: its derived paths -- $sourcesRoot and the publish directories --
# ARE handed to children that run with their working directory inside the stage tree, in this runner
# too. A relative -StageRoot is resolved by this process against the operator's directory and by those
# children against their own. This one is not defence in depth.
$StageRoot = (Resolve-Path -LiteralPath $StageRoot).Path

$sourcesRoot = Join-Path $StageRoot 'sources'
$publishRoot = Join-Path $StageRoot 'publish'
$runtimeRoot = Join-Path $StageRoot 'runtime'
$logsRoot = Join-Path $EvidenceRoot 'logs'
New-Item -ItemType Directory -Path $sourcesRoot, $publishRoot, $runtimeRoot, $logsRoot | Out-Null

$controlSource = Join-Path $sourcesRoot 'control-server'
$controlPublish = Join-Path $publishRoot 'control-server'
$controlDatabasePath = Join-Path $runtimeRoot 'controlserver.db'

$commands = [System.Collections.Generic.List[object]]::new()

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$LogPath
    )

    $startedAt = [DateTimeOffset]::UtcNow
    Push-Location $WorkingDirectory
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    $output | Out-File -LiteralPath $LogPath -Encoding utf8NoBOM
    $commands.Add([ordered]@{
        name = $Name
        workingDirectory = $WorkingDirectory
        file = $FilePath
        arguments = $Arguments
        startedAtUtc = $startedAt
        exitCode = $exitCode
        log = [IO.Path]::GetRelativePath($EvidenceRoot, $LogPath).Replace('\', '/')
    })
    if ($exitCode -ne 0) {
        # The tail of the log in the message itself (control-server#460): the message is what runner-error.json
        # and the console carry, and "see the log" alone is what made the cause something to go and dig for.
        $tail = @($output | ForEach-Object { "$_" } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Last 15)
        throw ("$Name exited with code $exitCode. See $LogPath" +
               $(if ($tail.Count -ne 0) { ". Last lines:`n" + ($tail -join "`n") } else { '' }))
    }
    return @($output)
}

function Get-Sha256Text {
    param([Parameter(Mandatory)][string]$Text)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Wait-HttpJson {
    param([string]$Uri, [int]$TimeoutSeconds = 60)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try { return Invoke-RestMethod -Uri $Uri -TimeoutSec 2 }
        catch { Start-Sleep -Milliseconds 250 }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Uri"
}

function Stop-ProcessSafely {
    param([Diagnostics.Process]$Process)
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force
            $Process.WaitForExit(10000) | Out-Null
        }
    }
    catch {
        # Cleanup is best effort; the port and process checks below report what actually happened.
    }
}

function Invoke-SqliteRows {
    param(
        [Parameter(Mandatory)][string]$DatabasePath,
        [Parameter(Mandatory)][string]$Sql,
        [Parameter(Mandatory)][string[]]$Columns
    )

    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$DatabasePath;Mode=ReadOnly")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $Sql
        $reader = $command.ExecuteReader()
        try {
            $rows = @()
            while ($reader.Read()) {
                $row = [ordered]@{}
                for ($index = 0; $index -lt $Columns.Length; $index++) {
                    $row[$Columns[$index]] = if ($reader.IsDBNull($index)) { $null } else { $reader.GetValue($index) }
                }
                $rows += $row
            }
            # Comma operator: a bare single-row result is unwrapped on return, and an ordered
            # dictionary indexed with [0] then yields its first *value* instead of the row.
            return ,$rows
        }
        finally {
            $reader.Dispose()
            $command.Dispose()
        }
    }
    finally {
        $connection.Dispose()
    }
}

function Invoke-SqliteScalarLong {
    param(
        [Parameter(Mandatory)][string]$DatabasePath,
        [Parameter(Mandatory)][string]$Sql
    )

    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$DatabasePath;Mode=ReadOnly")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = $Sql
            return [long]$command.ExecuteScalar()
        }
        finally { $command.Dispose() }
    }
    finally { $connection.Dispose() }
}

# Read only while no process owns the file: the store runs in WAL mode, and a read-only handle must
# not be the one that has to recover an unclean write-ahead log.
function Get-FileFingerprint {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        creationTimeUtc = $item.CreationTimeUtc.ToString('O')
        length = $item.Length
    }
}

# Subset comparison on durable identity columns: every row observed before a restart must still be
# present, column for column, after it. A recreated store fails this; a store that merely grew passes.
function Test-RowsPreserved {
    param(
        [object[]]$Before,
        [object[]]$After,
        [Parameter(Mandatory)][string[]]$IdentityColumns
    )

    if ($null -eq $Before -or $null -eq $After) { return $false }
    if (@($Before).Count -eq 0) { return $false }
    $afterKeys = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($row in @($After)) {
        [void]$afterKeys.Add((($IdentityColumns | ForEach-Object { [string]$row[$_] }) -join "`u{001f}"))
    }
    foreach ($row in @($Before)) {
        $key = ($IdentityColumns | ForEach-Object { [string]$row[$_] }) -join "`u{001f}"
        if (-not $afterKeys.Contains($key)) { return $false }
    }
    return $true
}

function Read-ControlDatabase {
    $countedTables = @(
        'OrderIntents', 'RiotDispatchAuditEvents', 'AcceptedDemands', 'VehiclePurposeClaimRecords',
        'StationOperations', 'OperationResults', 'UnloadBatches', 'StopClosures',
        'TransportDemandCompletions', 'ProtocolInbox')
    $counts = [ordered]@{}
    foreach ($table in $countedTables) {
        # The claim record table (control-server#386/#387) does not exist yet in a field store restored from before
        # batch 8; the baseline reads it before the server has migrated that store. Absent counts as 0.
        $present = Invoke-SqliteScalarLong -DatabasePath $controlDatabasePath `
            -Sql "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '$table'"
        $counts[$table] = if ($present -eq 0) { 0 } else {
            Invoke-SqliteScalarLong -DatabasePath $controlDatabasePath -Sql "SELECT COUNT(*) FROM $table"
        }
    }

    return [ordered]@{
        file = Get-FileFingerprint -Path $controlDatabasePath
        counts = $counts
        acceptedDemandRows = Invoke-SqliteRows -DatabasePath $controlDatabasePath `
            -Sql 'SELECT DemandId, TransportDemandKey, DemandRevision, Status FROM AcceptedDemands ORDER BY DemandId' `
            -Columns @('demandId', 'transportDemandKey', 'demandRevision', 'status')
        # The vehicle's occupancy history since control-server#387 retired the dispatch lease: one record per claim.
        # Empty before the server has migrated a store from before batch 8 (the table is not there yet).
        vehicleClaimRecordRows = if ($counts['VehiclePurposeClaimRecords'] -eq 0) { @() } else {
            Invoke-SqliteRows -DatabasePath $controlDatabasePath `
                -Sql 'SELECT JourneyId, VehicleKey, AcquiredAt, ReleasedAt FROM VehiclePurposeClaimRecords ORDER BY JourneyId, AcquiredAt' `
                -Columns @('journeyId', 'vehicleKey', 'acquiredAt', 'releasedAt')
        }
        auditRows = Invoke-SqliteRows -DatabasePath $controlDatabasePath -Sql @'
SELECT UpperId, DispatchGeneration, Sequence, Phase, Outcome, EligibilityBasis,
       HttpStatusCode, BusinessCode, ResultPresent, ReturnedOrderId
  FROM RiotDispatchAuditEvents
 ORDER BY UpperId, Sequence
'@ -Columns @('upperId', 'dispatchGeneration', 'sequence', 'phase', 'outcome', 'eligibilityBasis',
              'httpStatusCode', 'businessCode', 'resultPresent', 'returnedOrderId')
        orderIntentRows = Invoke-SqliteRows -DatabasePath $controlDatabasePath `
            -Sql 'SELECT UpperId, Purpose, Status, OrderId, DestinationStationId FROM OrderIntents ORDER BY UpperId' `
            -Columns @('upperId', 'purpose', 'status', 'orderId', 'destinationStationId')
        stationOperationRows = Invoke-SqliteRows -DatabasePath $controlDatabasePath `
            -Sql 'SELECT SlotOperationAttemptId, DemandId, OperationType, Status, ForcedRecoveryGeneration, TargetSlotsJson, EvidenceJson FROM StationOperations ORDER BY CreatedAt' `
            -Columns @('slotOperationAttemptId', 'demandId', 'operationType', 'status', 'forcedRecoveryGeneration', 'targetSlotsJson', 'evidenceJson')
        operationResultRows = Invoke-SqliteRows -DatabasePath $controlDatabasePath `
            -Sql 'SELECT ResultId, SlotOperationAttemptId, AgvId, ForcedRecoveryGeneration, HistoricalOnly FROM OperationResults ORDER BY ReceivedAt' `
            -Columns @('resultId', 'slotOperationAttemptId', 'agvId', 'forcedRecoveryGeneration', 'historicalOnly')
        sessionRecoveryRows = Invoke-SqliteRows -DatabasePath $controlDatabasePath `
            -Sql 'SELECT AgvId, SessionGeneration, Readiness, ReasonCode, ProtocolCommit FROM SessionRecoveries ORDER BY AgvId' `
            -Columns @('agvId', 'sessionGeneration', 'readiness', 'reasonCode', 'protocolCommit')
        resultInboxRows = Invoke-SqliteRows -DatabasePath $controlDatabasePath `
            -Sql "SELECT MessageId, ContentHash, FirstResponseJson FROM ProtocolInbox WHERE MessageType = 'OperationResult' ORDER BY ReceivedAt" `
            -Columns @('messageId', 'contentHash', 'firstResponseJson')
    }
}

$control = $null
$runError = $null
$storeGenerator = $null
$fieldDatabaseSha256 = $null
$probeResult = $null
$handshakeResult = $null
$version = $null
$versionAfterRestart = $null
$baseline = $null
$afterProbe = $null
$final = $null
$restart = [ordered]@{
    firstHostProcessId = $null
    firstHostExitedBeforeRestart = $null
    secondHostProcessId = $null
}
$credential = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()
$probeTranscript = Join-Path $EvidenceRoot 'probe-transcript.ndjson'

try {
    Invoke-LoggedCommand -Name 'clone-control-server' -WorkingDirectory $sourcesRoot -FilePath 'git' `
        -Arguments @('-c', 'core.autocrlf=false', 'clone', '--no-hardlinks', '--no-checkout',
                     $ControlServerRepository, $controlSource) `
        -LogPath (Join-Path $logsRoot 'clone-control-server.log') | Out-Null
    & git -C $controlSource config core.autocrlf false
    if ($LASTEXITCODE -ne 0) { throw 'Unable to set core.autocrlf=false for the ControlServer clone' }
    Invoke-LoggedCommand -Name 'checkout-control-server' -WorkingDirectory $controlSource -FilePath 'git' `
        -Arguments @('checkout', '--detach', $ControlServerCommit) `
        -LogPath (Join-Path $logsRoot 'checkout-control-server.log') | Out-Null
    $checkedOut = (& git -C $controlSource rev-parse HEAD).Trim()
    if ($checkedOut -ne $ControlServerCommit -or @(& git -C $controlSource status --porcelain).Count -ne 0) {
        throw "The ControlServer clone is not a clean exact checkout of $ControlServerCommit"
    }

    if ($storeSource -eq 'SYNTHETIC_RIG') {
        # Checked against the repository before the run started; the clone is that commit.
        $rig = Join-Path $controlSource 'scripts\l2\Invoke-L2Scenario.ps1'
        $storeGeneratorEvidence = Join-Path $EvidenceRoot 'store-generator'
        $generatorAssertions = Join-Path $storeGeneratorEvidence 'assertions.json'
        try {
            Invoke-LoggedCommand -Name 'generate-demand-bearing-store' -WorkingDirectory $controlSource -FilePath 'pwsh' `
                -Arguments @('-NoProfile', '-File', $rig,
                             '-Scenario', $storeGeneratorScenario,
                             '-EvidenceRoot', $storeGeneratorEvidence,
                             '-Repository', $controlSource) `
                -LogPath (Join-Path $logsRoot 'generate-demand-bearing-store.log') | Out-Null
        }
        catch {
            # The rig exits 1 on any outcome but PASS, so its own verdict is only in its assertions.json: put it
            # in front of the exit code (control-server#460).
            $verdict = if (Test-Path -LiteralPath $generatorAssertions -PathType Leaf) {
                $failed = Get-Content -Raw -LiteralPath $generatorAssertions | ConvertFrom-Json
                "The store generator reported $($failed.outcome): $($failed.failureReason). "
            } else { 'The store generator wrote no assertions.json. ' }
            throw ($verdict + $_.Exception.Message)
        }
        $generatorDocument = Get-Content -Raw -LiteralPath $generatorAssertions | ConvertFrom-Json
        if ($generatorDocument.outcome -ne 'PASS') {
            throw ("The store generator did not pass: $($generatorDocument.outcome): $($generatorDocument.failureReason). " +
                   "See $storeGeneratorEvidence")
        }
        # Out of the evidence tree: the store is an input, and the restored copy's rows are recorded below.
        $generatedRoot = Join-Path $StageRoot 'generated-store'
        New-Item -ItemType Directory -Path $generatedRoot | Out-Null
        $fieldDatabase = Join-Path $generatedRoot 'controlserver.db'
        Move-Item -LiteralPath (Join-Path $storeGeneratorEvidence 'demand-bearing-store\controlserver.db') `
            -Destination $fieldDatabase
        Remove-Item -LiteralPath (Join-Path $storeGeneratorEvidence 'demand-bearing-store') -Recurse -Force
        $storeGenerator = [ordered]@{
            scenario = $storeGeneratorScenario
            evidence = [IO.Path]::GetRelativePath($EvidenceRoot, $storeGeneratorEvidence).Replace('\', '/')
            runId = $generatorDocument.runId
            outcome = $generatorDocument.outcome
            rig = $generatorDocument.identity.rig
            controlServerCommit = $generatorDocument.identity.controlServerCommit
        }
    }

    Invoke-LoggedCommand -Name 'publish-control-server' -WorkingDirectory $controlSource -FilePath 'dotnet' `
        -Arguments @('publish', '.\src\ControlServer.Host\ControlServer.Host.csproj', '-c', 'Release', '-o', $controlPublish) `
        -LogPath (Join-Path $logsRoot 'publish-control-server.log') | Out-Null
    Add-Type -Path (Join-Path $controlPublish 'Microsoft.Data.Sqlite.dll')

    # The field store is copied whole -- database, write-ahead log and shared-memory file -- so the
    # server opens exactly the state the field run left, including anything still only in the WAL.
    foreach ($suffix in @('', '-wal', '-shm')) {
        $sourceFile = $fieldDatabase + $suffix
        if (Test-Path -LiteralPath $sourceFile -PathType Leaf) {
            Copy-Item -LiteralPath $sourceFile -Destination ($controlDatabasePath + $suffix) -Force
        }
    }
    $fieldDatabaseSha256 = (Get-FileHash -LiteralPath $fieldDatabase -Algorithm SHA256).Hash.ToLowerInvariant()

    $baseline = Read-ControlDatabase
    Assert-FieldRunStoreIsNotGenerated -StoreSource $storeSource -Baseline $baseline
    $preparedRows =@($baseline.stationOperationRows | Where-Object { $_['status'] -eq 'Prepared' })
    $committedRows = @($baseline.stationOperationRows | Where-Object { $_['status'] -eq 'Committed' })
    if ($preparedRows.Count -ne 1) {
        throw "The restored store must hold exactly one Prepared station operation, found $($preparedRows.Count)."
    }
    if ($committedRows.Count -lt 1) {
        throw 'The restored store must hold at least one Committed station operation.'
    }
    if (@($baseline.sessionRecoveryRows).Count -ne 1) {
        throw 'The restored store must hold exactly one session recovery row.'
    }
    $prepared = $preparedRows[0]
    $committed = $committedRows[0]
    $agvId = [string]$baseline.sessionRecoveryRows[0]['agvId']
    $preparedSlots = [int[]](([string]$prepared['targetSlotsJson']) | ConvertFrom-Json)
    $committedSlots = [int[]](([string]$committed['targetSlotsJson']) | ConvertFrom-Json)

    $controlEnvironment = @{
        'CONTROL_SERVER_ONBOARD_CREDENTIAL' = $credential
        'ConnectionStrings__ControlServer' = "Data Source=$controlDatabasePath"
        'Health__url' = "http://127.0.0.1:$healthPort"
        'OnboardTransport__listenAddress' = '127.0.0.1'
        'OnboardTransport__port' = [string]$controlPort
        'OnboardTransport__credentialEnvironmentVariable' = 'CONTROL_SERVER_ONBOARD_CREDENTIAL'
        # The runtime stays off and both external adapters point at a dead port: this run must not be
        # able to accept a demand, place an order or move anything, whatever the restored store holds.
        'JourneyRuntime__enabled' = 'false'
        'RiotCreateDispatch__enabled' = 'false'
        'RiotAbsentAtObservationCreateExperiment__enabled' = 'false'
        'OnboardSafetyProjection__enabled' = 'false'
        'MesIngest__baseUrl' = 'http://127.0.0.1:1'
        'RIoT__baseUrl' = 'http://127.0.0.1:1'
        'ControlServerBuild__commit' = $ControlServerCommit
    }
    # The host is launched twice, so the launch shape is a function: an ordinal that differs only in
    # the log file name is what keeps the two phases comparable.
    function Start-ControlServer {
        param([Parameter(Mandatory)][int]$Ordinal)
        return Start-Process -FilePath 'dotnet' `
            -ArgumentList @(Join-Path $controlPublish 'ControlServer.Host.dll') `
            -WorkingDirectory $controlPublish `
            -RedirectStandardOutput (Join-Path $logsRoot "control-$Ordinal.out.log") `
            -RedirectStandardError (Join-Path $logsRoot "control-$Ordinal.err.log") `
            -Environment $controlEnvironment -WindowStyle Hidden -PassThru
    }

    $control = Start-ControlServer -Ordinal 1
    $restart.firstHostProcessId = $control.Id
    $version = Wait-HttpJson -Uri "http://127.0.0.1:$healthPort/version"

    Add-Type -TypeDefinition (Get-HarnessSource -Path $SharedRunnerSource) -Language CSharp
    $probeJson = [StagedG3TlsHarness]::RunDemandBearingResultProbeAsync(
        $controlPort,
        $credential,
        $agvId,
        [string]$prepared['demandId'],
        [string]$prepared['slotOperationAttemptId'],
        ([string]$prepared['operationType']).ToUpperInvariant(),
        $preparedSlots,
        [string]$committed['slotOperationAttemptId'],
        ([string]$committed['operationType']).ToUpperInvariant(),
        $committedSlots,
        $probeTranscript,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    [IO.File]::WriteAllText(
        (Join-Path $EvidenceRoot 'probe-result.json'), $probeJson, [Text.UTF8Encoding]::new($false))
    $probeResult = $probeJson | ConvertFrom-Json

    # Ticket 20 could assert that a restarted server keeps its store, but not that it keeps a demand
    # and a vehicle lease, because those rows only exist once a demand has been accepted. This is the
    # form in which that can be asserted: kill the host, bring it back onto the same file, and require
    # the demand and lease rows to come back column for column.
    Stop-ProcessSafely -Process $control
    $restart.firstHostExitedBeforeRestart = $control.HasExited
    Start-Sleep -Seconds 2
    $afterProbe = Read-ControlDatabase

    $control = Start-ControlServer -Ordinal 2
    $restart.secondHostProcessId = $control.Id
    $versionAfterRestart = Wait-HttpJson -Uri "http://127.0.0.1:$healthPort/version"
    $handshakeJson = [StagedG3TlsHarness]::RunSessionHandshakeProbeAsync(
        $controlPort, $credential, $agvId, 'after-restart',
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    [IO.File]::WriteAllText(
        (Join-Path $EvidenceRoot 'handshake-after-restart.json'), $handshakeJson,
        [Text.UTF8Encoding]::new($false))
    $handshakeResult = $handshakeJson | ConvertFrom-Json
}
catch {
    $runError = $_
}
finally {
    Stop-ProcessSafely -Process $control
    Start-Sleep -Seconds 2
    if (Test-Path -LiteralPath $controlDatabasePath -PathType Leaf) {
        try { $final = Read-ControlDatabase } catch { if ($null -eq $runError) { $runError = $_ } }
    }
}

# First, before any judgement and before Write-G3GateResults (control-server#460): on an errored run the gate-result
# writer refuses a store that was never read or a version that was never fetched, and until this call that refusal
# was all the run printed -- the generator's exit, the store's shape or the host that never came up stayed in a
# log. Test-G3EvidenceHonesty.ps1 asserts that this call directly follows the try.
Write-StagedRunError -ErrorRecord $runError -EvidenceRoot $EvidenceRoot -RunLabel 'Demand-bearing G3'

$portsReleased = @(Get-NetTCPConnection -State Listen -LocalPort $controlPort, $healthPort `
        -ErrorAction SilentlyContinue).Count -eq 0

function Get-Case {
    param([string]$Name)
    if ($null -eq $probeResult) { return $null }
    return @($probeResult.cases | Where-Object { $_.case -eq $Name })[0]
}

function Test-Case {
    param([string]$Name)
    $case = Get-Case -Name $Name
    return $null -ne $case -and $case.status -eq 'PASS'
}

# --- the demand-bearing half of the RIoT create plane, read out of the restored field store ---------
# Every leg of a fresh dispatch generation reconciles by upperId before creating. RIoT answers a
# never-created upperId with HTTP 200 and business code 0 but no result, which is UNKNOWN rather than
# NotFound, so this is not an occasional fault to inject: it is on the path of every real create, and
# what has to be asserted is that the server treats it as an exact absent-at-observation and creates
# once, rather than confirming an order it never saw.
# Assigned inside an if *statement* rather than from an if *expression*: an expression that yields a
# single-element array has that element unrolled by the output stream, and a bare [ordered] row then
# answers .Count with its key count instead of 1 -- a row set that silently reports 5 rows.
$auditRows = @()
if ($null -ne $baseline) { $auditRows = @($baseline.auditRows) }
$auditLegs = @($auditRows | ForEach-Object { [string]$_['upperId'] } | Sort-Object -Unique)
$unknownRows = @($auditRows | Where-Object {
    $_['phase'] -eq 'PRE_CREATE_RECONCILIATION' -and $_['outcome'] -eq 'UNKNOWN' })

$riotUnknownObservedPass = $auditLegs.Count -ge 1 -and $unknownRows.Count -eq $auditLegs.Count -and
    @($unknownRows | Where-Object {
        $_['eligibilityBasis'] -ne 'ABSENT_AT_OBSERVATION_IDEMPOTENT_CREATE' -or
        [long]$_['sequence'] -ne 1 }).Count -eq 0

# An exact absent-at-observation, not a transport failure: a result actually came back and carried no
# order. Confirming absence any other way would make the idempotent create unsafe.
$riotUnknownIsExactAbsencePass = $unknownRows.Count -ge 1 -and
    @($unknownRows | Where-Object {
        $null -eq $_['resultPresent'] -or [long]$_['resultPresent'] -ne 0 -or
        $null -ne $_['returnedOrderId'] }).Count -eq 0

$riotUnknownCreatesExactlyOncePass = $auditLegs.Count -ge 1
foreach ($leg in $auditLegs) {
    $legRows = @($auditRows | Where-Object { [string]$_['upperId'] -eq $leg })
    $phases = @($legRows | ForEach-Object { [string]$_['phase'] })
    $sequences = @($legRows | ForEach-Object { [long]$_['sequence'] })
    $expectedPhases = @('PRE_CREATE_RECONCILIATION', 'CREATE_DISPATCH', 'CREATE_REQUEST',
                        'CREATE_RESPONSE', 'POST_CREATE_RECONCILIATION')
    if (@(Compare-Object -ReferenceObject $expectedPhases -DifferenceObject $phases -SyncWindow 0).Count -ne 0 -or
        @(Compare-Object -ReferenceObject @(1, 2, 3, 4, 5) -DifferenceObject $sequences -SyncWindow 0).Count -ne 0) {
        $riotUnknownCreatesExactlyOncePass = $false
        break
    }
}

# The order the server ends up trusting is the one it created, not one it inferred from the UNKNOWN.
$riotUnknownResolvesToTheCreatedOrderPass = $auditLegs.Count -ge 1
foreach ($leg in $auditLegs) {
    $confirmed = @($auditRows | Where-Object {
        [string]$_['upperId'] -eq $leg -and $_['phase'] -eq 'POST_CREATE_RECONCILIATION' })
    $created = @($auditRows | Where-Object {
        [string]$_['upperId'] -eq $leg -and $_['phase'] -eq 'CREATE_RESPONSE' })
    $intent = @($baseline.orderIntentRows | Where-Object { [string]$_['upperId'] -eq $leg })
    if ($confirmed.Count -ne 1 -or $created.Count -ne 1 -or $intent.Count -ne 1 -or
        $confirmed[0]['outcome'] -ne 'CONFIRMED' -or $created[0]['outcome'] -ne 'ACCEPTED' -or
        [string]$created[0]['returnedOrderId'] -ne [string]$confirmed[0]['returnedOrderId'] -or
        [string]$confirmed[0]['returnedOrderId'] -ne [string]$intent[0]['orderId'] -or
        $intent[0]['status'] -ne 'CONFIRMED') {
        $riotUnknownResolvesToTheCreatedOrderPass = $false
        break
    }
}

# --- the OperationResult plane, driven live against the restored store -----------------------------
$preparedAttemptId = $null
if ($null -ne $baseline) {
    $baselinePreparedRows = @($baseline.stationOperationRows | Where-Object { $_['status'] -eq 'Prepared' })
    if ($baselinePreparedRows.Count -eq 1) {
        $preparedAttemptId = [string]$baselinePreparedRows[0]['slotOperationAttemptId']
    }
}
$acceptedMessageId = if ($null -ne $probeResult) { [string]$probeResult.acceptedMessageId } else { $null }

$resultAcceptedPass = Test-Case -Name 'preparedAttemptAcceptsItsFirstResult'
$resultReplayPass = Test-Case -Name 'identicalResultReplayReturnsTheStoredAcknowledgement'
$resultContentConflictPass = Test-Case -Name 'sameMessageIdWithDifferentContentIsRefused'
$resultRenumberConflictPass = Test-Case -Name 'sameAttemptAndGenerationUnderANewMessageIdIsRefused'
$committedAttemptConflictPass = Test-Case -Name 'alreadyCommittedAttemptRefusesASecondResult'
$staleGenerationPass = Test-Case -Name 'resultFromASupersededSessionGenerationIsRefused'

# The replay is only evidence if it did not also process the message twice: one inbox row, one result
# row, and the operation moved to Committed exactly once.
$finalPreparedRows = @()
$preparedResultRows = @()
$acceptedInboxRows = @()
if ($null -ne $final) {
    $finalPreparedRows = @($final.stationOperationRows | Where-Object {
        [string]$_['slotOperationAttemptId'] -eq $preparedAttemptId })
    $preparedResultRows = @($final.operationResultRows | Where-Object {
        [string]$_['slotOperationAttemptId'] -eq $preparedAttemptId })
    $acceptedInboxRows = @($final.resultInboxRows | Where-Object {
        [string]$_['messageId'] -eq $acceptedMessageId })
}
$finalPreparedRow = $null
if ($finalPreparedRows.Count -eq 1) { $finalPreparedRow = $finalPreparedRows[0] }

$resultCommittedOncePass = $null -ne $finalPreparedRow -and
    $finalPreparedRow['status'] -eq 'Committed' -and
    -not [string]::IsNullOrWhiteSpace([string]$finalPreparedRow['evidenceJson']) -and
    $preparedResultRows.Count -eq 1 -and
    [string]$preparedResultRows[0]['resultId'] -eq $acceptedMessageId -and
    [long]$preparedResultRows[0]['historicalOnly'] -eq 0 -and
    $acceptedInboxRows.Count -eq 1

# The unload result closes the demand, so the four facts it commits atomically have to be there and
# the vehicle lease released -- the part of the plane a staged run could never reach.
$demandClosureRowsPass = $null -ne $baseline -and $null -ne $final -and
    [long]$baseline.counts['UnloadBatches'] -eq 0 -and
    [long]$final.counts['UnloadBatches'] -eq 1 -and
    [long]$baseline.counts['StopClosures'] -eq 0 -and
    [long]$final.counts['StopClosures'] -eq 1 -and
    [long]$baseline.counts['TransportDemandCompletions'] -eq 0 -and
    [long]$final.counts['TransportDemandCompletions'] -eq 1

# Nothing left this machine. The restored store gains business rows -- that is the vector -- but the
# tables that only an external call can grow must be byte-for-byte the same count as the baseline.
# The claim records are compared from after the first host, not from the baseline: that host migrated the restored
# store, and the migration backfills a record per claim and per released lease it finds (control-server#387). What
# only an acceptance can add is what must not grow from there on.
$externalTables = @('OrderIntents', 'RiotDispatchAuditEvents', 'AcceptedDemands')
$noExternalSideEffectsPass = $null -ne $baseline -and $null -ne $final -and $null -ne $afterProbe -and
    @($externalTables | Where-Object {
        [long]$baseline.counts[$_] -ne [long]$final.counts[$_] }).Count -eq 0 -and
    [long]$afterProbe.counts['VehiclePurposeClaimRecords'] -eq [long]$final.counts['VehiclePurposeClaimRecords'] -and
    [long]$final.counts['StationOperations'] -eq [long]$baseline.counts['StationOperations']

# --- the demand-bearing half of the process restart vector ------------------------------------------
# A restart the OS did not actually perform proves nothing, so the replaced process identity carries
# the claim -- SessionAccepted.serverInstanceId cannot, being scoped per connection.
$hostReplacedPass = $null -ne $restart.firstHostProcessId -and $null -ne $restart.secondHostProcessId -and
    $restart.firstHostProcessId -ne $restart.secondHostProcessId -and
    $restart.firstHostExitedBeforeRestart -eq $true

$demandSurvivesRestartPass = $null -ne $afterProbe -and $null -ne $final -and
    $null -ne $afterProbe.file -and $null -ne $final.file -and
    $afterProbe.file.creationTimeUtc -eq $final.file.creationTimeUtc -and
    [long]$afterProbe.counts['AcceptedDemands'] -eq [long]$final.counts['AcceptedDemands'] -and
    (Test-RowsPreserved -Before $afterProbe.acceptedDemandRows -After $final.acceptedDemandRows `
        -IdentityColumns @('demandId', 'transportDemandKey', 'demandRevision', 'status'))

$vehicleClaimRecordSurvivesRestartPass = $null -ne $afterProbe -and $null -ne $final -and
    [long]$afterProbe.counts['VehiclePurposeClaimRecords'] -eq [long]$final.counts['VehiclePurposeClaimRecords'] -and
    (Test-RowsPreserved -Before $afterProbe.vehicleClaimRecordRows -After $final.vehicleClaimRecordRows `
        -IdentityColumns @('journeyId', 'vehicleKey', 'acquiredAt', 'releasedAt'))

# The restarted host has to be serving that same store, not a fresh one: a new session on the old
# file continues the generation sequence instead of restarting it at 1.
$restartedHostServesTheSameStorePass = $null -ne $handshakeResult -and $null -ne $afterProbe -and
    $null -ne $versionAfterRestart -and
    $versionAfterRestart.protocolCommit -eq $ProtocolCommit -and
    [string]$handshakeResult.serverBuildCommit -eq $ControlServerCommit -and
    [long]$handshakeResult.sessionGeneration -eq
        [long]$afterProbe.sessionRecoveryRows[0]['sessionGeneration'] + 1

# The identity this run can actually be held to: the protocol the running host reports, the build
# under test, and the fact that the restored store was read at all.
#
# The store's own recorded protocolCommit used to be a seventh conjunct here, and it is not one any
# more for a field store (ticket 24, on the user's ruling of 2026-09-09). It asks what protocol the field
# run of 2026-08-29 was speaking -- protocol-v0.1.1 -- which no v2-identity run can ever satisfy, short of
# re-collecting a field run under v2. Leaving it inside the conjunction made the whole assertion
# permanently red, and a permanently red assertion cannot report the six checks that are still
# meaningful: an exemption written against the assertion's NAME would have swallowed them.
# So for a field store it is recorded below instead of asserted, and the exemption is written against
# that one fact.
#
# A generated store (control-server#453) is written by the bound commit itself, speaking the bound
# protocol, so for it the conjunct is back: a generated store that recorded any other protocol was not
# written by what this run claims to test. The exemption is the field store's and goes no further.
# On that path the conjunct holds by construction: the server refuses to write SessionRecoveries.ProtocolCommit
# unless it equals the identity compiled into the build (WireToGateStore.ValidateProtocolIdentity, called from
# BeginSessionRecoveryAsync). So it guards "this store was not written by this build" and catches no product
# regression; a mutation can only reach it by editing the exported file.
$storeProtocolCommit = if ($null -ne $baseline) {
    [string]$baseline.sessionRecoveryRows[0]['protocolCommit']
} else {
    $null
}
$storeProtocolCommitMatches = ($null -ne $storeProtocolCommit) -and ($storeProtocolCommit -eq $ProtocolCommit)
# Written beside the PASS so a reader can tell whether the seventh conjunct was checked at all: on a field store a
# PASS here says nothing about the store's protocol, on a generated one it does.
$protocolCommitConjunct = if ($storeSource -eq 'FIELD_RUN') {
    "FIELD_RUN: seventh conjunct exempt (TICKET_17); store protocolCommit $storeProtocolCommit recorded, not asserted"
} else {
    "SYNTHETIC_RIG: seventh conjunct in force; store protocolCommit $storeProtocolCommit must equal bound " +
    "$ProtocolCommit -> $(if ($storeProtocolCommitMatches) { 'equal' } else { 'NOT equal' })"
}
$protocolBindingPass = $null -ne $version -and
    $version.protocolCommit -eq $ProtocolCommit -and
    $version.protocolTag -eq 'protocol-v3.0.0' -and
    $null -ne $probeResult -and
    [string]$probeResult.serverBuildCommit -eq $ControlServerCommit -and
    $null -ne $baseline -and
    ($storeSource -eq 'FIELD_RUN' -or $storeProtocolCommitMatches)

# For a field store: recorded, not asserted. matchesBoundProtocolCommit is written exactly as measured --
# false is the expected value for the 2026-08-29 store, and it is stated rather than omitted: an exemption
# means this fact does not decide the slice, not that the evidence stops saying it.
$fieldStoreProvenanceRecord = if ($storeSource -eq 'FIELD_RUN') {
    [ordered]@{
        storeSource = $storeSource
        protocolCommit = $storeProtocolCommit
        matchesBoundProtocolCommit = $storeProtocolCommitMatches
        protocolCommitAsserted = $false
        protocolCommitConjunct = $protocolCommitConjunct
        exemption = 'TICKET_17_KNOWN_EXEMPTION_FIELD_STORE_HISTORY'
        note = 'The restored store is real state written by an authorised field run (the one used up ' +
               'to batch 7 was 2026-08-29, protocol-v0.1.1). Its recorded protocolCommit is that history, ' +
               'not a statement about the build under test, so it is recorded and not asserted. Ruled a ' +
               'known exemption by the user on 2026-09-09; scope is this fact alone.'
        # The RIoT create audit read out of the same restored store. Asserted until the control-server#60
        # review (2026-09-18): every row was written by the field run's build before this server started,
        # so it judged that history rather than the bound commit. Recorded exactly as measured instead.
        riotCreateAuditHistory = [ordered]@{
            legs = $auditLegs.Count
            preCreateReconciliationObservedUnknownOnEveryLeg = $riotUnknownObservedPass
            unknownWasAnExactAbsentAtObservation = $riotUnknownIsExactAbsencePass
            unknownStillCreatedExactlyOncePerLeg = $riotUnknownCreatesExactlyOncePass
            unknownResolvedToTheOrderItCreated = $riotUnknownResolvesToTheCreatedOrderPass
            note = 'History of the field run, not a judgment of the build under test. ' +
                   'Recorded, not asserted, by the user ruling on control-server#60 (2026-09-18).'
        }
    }
} else {
    [ordered]@{
        storeSource = $storeSource
        protocolCommit = $storeProtocolCommit
        matchesBoundProtocolCommit = $storeProtocolCommitMatches
        protocolCommitAsserted = $true
        protocolCommitConjunct = $protocolCommitConjunct
        exemption = $null
        generator = $storeGenerator
        note = 'Generated on the spot by the bound ControlServer commit on the synthetic rig: a synthetic ' +
               'onboard peer and the fake RIoT, not a real vehicle and not the real RIoT. Weaker than a ' +
               'field store in two ways (control-server#453): the state was not written by a real demand on a ' +
               'real vehicle; and it was written by the build under test in its own schema, so restoring it ' +
               'migrates nothing -- the restart and side-effect assertions no longer also show that a store ' +
               'another build wrote, in an older schema, is taken over. Its protocolCommit is asserted inside ' +
               'protocolAndBuildIdentityBoundToTheSharedBinding, where it holds by construction: it can only ' +
               'show the store was written by this build, not catch a product regression.'
        # The fake RIoT does not answer a never-created upperId the way the real one does, so these say
        # nothing about RIoT; kept so the shape of the record does not depend on where the store came from.
        riotCreateAuditHistory = [ordered]@{
            legs = $auditLegs.Count
            preCreateReconciliationObservedUnknownOnEveryLeg = $riotUnknownObservedPass
            unknownWasAnExactAbsentAtObservation = $riotUnknownIsExactAbsencePass
            unknownStillCreatedExactlyOncePerLeg = $riotUnknownCreatesExactlyOncePass
            unknownResolvedToTheOrderItCreated = $riotUnknownResolvesToTheCreatedOrderPass
            note = 'Written against the fake RIoT, whose answers are not the real RIoT''s. Recorded, not ' +
                   'asserted, and not evidence about RIoT.'
        }
    }
}

$configuration = [ordered]@{
    loopbackOnly = $true
    tls = $false
    temporaryTrustRootInstalled = $false
    unattended = $true
    storeProvenance = [ordered]@{
        storeSource = $storeSource
        fieldRunRoot = $FieldRunRoot
        fieldDatabaseSha256 = $fieldDatabaseSha256
        generator = $storeGenerator
        note = if ($storeSource -eq 'FIELD_RUN') {
            'Restored from an authorised field run. The store is real state produced by a real ' +
            'demand; the build under test is the bound ControlServer commit, which is not ' +
            'necessarily the build that wrote it.'
        } else {
            'Generated by the bound ControlServer commit on the synthetic rig (synthetic onboard peer, ' +
            'fake RIoT, fake MesIngest). Weaker than a field store: no real vehicle, no real RIoT, and no ' +
            'cross-build restore -- the store is in the build''s own schema, so nothing is migrated.'
        }
    }
    commitBinding = [ordered]@{
        source = [IO.Path]::GetRelativePath($ControlServerRepository, $SharedRunnerSource).Replace('\', '/')
        sourceSha256 = $sharedRunnerSha256
        functionSource = [IO.Path]::GetRelativePath($ControlServerRepository, $CommitBindingFunctionSource).Replace('\', '/')
        functionSourceSha256 = $commitBindingFunctionSha256
        readFrom = 'param-block-defaults'
        controlServer = $ControlServerCommit
        controlServerCommitSource = $controlServerCommitSource
        onboardHmi = $OnboardCommit
        slotsSimulator = $SimulatorCommit
        protocol = $ProtocolCommit
    }
    ports = [ordered]@{ controlPlaintext = $controlPort; controlHealth = $healthPort }
    journeyRuntimeEnabled = $false
    riotCreateDispatchEnabled = $false
    mesIngestBaseUrl = 'http://127.0.0.1:1'
    riotBaseUrl = 'http://127.0.0.1:1'
    realExternalCredentialsUsed = $false
    realRiotOrderCreated = $false
    movementCommandSent = $false
    stationOperationRowFabricated = $false
}
$configurationJson = $configuration | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText(
    (Join-Path $EvidenceRoot 'configuration.json'), $configurationJson, [Text.UTF8Encoding]::new($false))

$secretLeakFiles = [System.Collections.Generic.List[string]]::new()
foreach ($file in @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File)) {
    try {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        if ($text.Contains($credential, [StringComparison]::Ordinal)) {
            $secretLeakFiles.Add([IO.Path]::GetRelativePath($EvidenceRoot, $file.FullName).Replace('\', '/'))
        }
    }
    catch {
        # Evidence is text-only in this runner; unreadable files are covered by the artifact hash list.
    }
}

$assertions = [ordered]@{
    protocolAndBuildIdentityBoundToTheSharedBinding = $protocolBindingPass
    preparedAttemptAcceptsItsFirstResult = $resultAcceptedPass
    identicalResultReplayReturnsTheStoredAcknowledgement = $resultReplayPass
    sameMessageIdWithDifferentContentIsRefused = $resultContentConflictPass
    sameAttemptAndGenerationUnderANewMessageIdIsRefused = $resultRenumberConflictPass
    alreadyCommittedAttemptRefusesASecondResult = $committedAttemptConflictPass
    resultFromASupersededSessionGenerationIsRefused = $staleGenerationPass
    replayedResultWasNotProcessedTwice = $resultCommittedOncePass
    unloadResultClosedTheDemandAtomically = $demandClosureRowsPass
    controlServerHostProcessWasActuallyReplaced = $hostReplacedPass
    acceptedDemandSurvivesTheHostRestart = $demandSurvivesRestartPass
    vehicleClaimRecordSurvivesTheHostRestart = $vehicleClaimRecordSurvivesRestartPass
    restartedHostServesTheSameStore = $restartedHostServesTheSameStorePass
    noMovementOrExternalSideEffects = $noExternalSideEffectsPass
    listenersReleased = $portsReleased
    secretScan = $secretLeakFiles.Count -eq 0
}
$assertionReport = [ordered]@{}
foreach ($name in $assertions.Keys) {
    $assertionReport[$name] = if ($assertions[$name]) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
}
$failedAssertions = @($assertions.Keys | Where-Object { -not $assertions[$_] })

$status = if ($null -ne $runError) {
    'INCONCLUSIVE_RUNNER_ERROR'
} elseif ($failedAssertions.Count -eq 0) {
    'DEMAND_BEARING_G3_VECTORS_PASS'
} else {
    'DEMAND_BEARING_SLICE_FAIL'
}

# One record for the gate results, the classification and run-result.json alike: the classification reads its
# controlServerCommitSource to decide whether this run tested the shared binding (control-server#460).
$commitsRecord = [ordered]@{
    controlServer = $ControlServerCommit
    controlServerCommitSource = $controlServerCommitSource
    onboardHmi = $OnboardCommit
    onboardCommitSource = $onboardCommitSource
    slotsSimulator = $SimulatorCommit
    simulatorCommitSource = $simulatorCommitSource
    protocol = $ProtocolCommit
    protocolCommitSource = $protocolCommitSource
    runner = $runnerCommit
    runnerWorktreeCleanAtStart = $runnerWorktreeClean
    runnerSource = $runnerProvenance.runnerSource
}

$gateResultPaths = Write-G3GateResults -RunKind $G3RunKind -EvidenceRoot $EvidenceRoot `
    -AssertionReport $assertionReport -Slice $Slice -RunnerErrored:($null -ne $runError) -Context @{
        runId = $runId
        startedAt = $runStartedAt.ToString('O')
        commits = $commitsRecord

        # Read back from the server this run actually talked to rather than restated from a constant:
        # this runner clones no protocol repository, so the identity it can honestly cite is the one
        # the running host reported. Null when the run never got a version, which is the same case
        # that grades every slice INCONCLUSIVE.
        protocolReleaseVersion = $version.protocolReleaseVersion
        protocolTag = $version.protocolTag
        protocolProfileId = $version.profileId
        protocolVersion = $version.protocolVersion
        protocolApprovalStatus = $version.approvalStatus
        protocolRepositoryCommit = $ProtocolCommit
        protocolManifestSha256 = $version.manifestSha256
        protocolSchemaBundleSha256 = $version.schemaBundleSha256
        protocolVectorsSha256 = $version.vectorsSha256
        sliceIndexPath = (Join-Path $ControlServerRepository 'vendor\8005-agv-protocol\integration-slices\index.json')
        sliceIndexSource = 'vendor/8005-agv-protocol/integration-slices/index.json'
        # Only this runner restores a field store, so only this runner's gate results carry the
        # section. The other two would carry a permanently empty one, which reads as a missing
        # measurement rather than an inapplicable one.
        fieldStoreProvenance = $fieldStoreProvenanceRecord
    }

# The secret scan above ran before these files existed. They carry only derived identity, status and
# assertion names, so this throws rather than recording a leak: a value that reached them is already
# sealed into evidence, and the run must not finish claiming it scanned clean.
foreach ($gateResultPath in $gateResultPaths) {
    if ((Get-Content -Raw -LiteralPath $gateResultPath).Contains($credential, [StringComparison]::Ordinal)) {
        throw "A gate result carries a run secret: $gateResultPath"
    }
}

$artifactFiles = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File |
    Where-Object { $_.Name -ne 'run-result.json' } |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($EvidenceRoot, $_.FullName).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            length = $_.Length
        }
    })

$result = [ordered]@{
    schemaVersion = '1.0.0'
    runKind = 'DEMAND_BEARING_G3_RESULT_AND_RIOT_UNKNOWN_VECTORS_NO_MOVEMENT'
    runId = $runId
    startedAtUtc = $runStartedAt
    completedAtUtc = [DateTimeOffset]::UtcNow
    status = $status
    classification = (New-G3Classification -RunKind $G3RunKind -RunStatus $status `
        -AssertionReport $assertionReport -Commits $commitsRecord -RunnerErrored:($null -ne $runError))
    gateResults = @($gateResultPaths | ForEach-Object {
        [IO.Path]::GetRelativePath($EvidenceRoot, $_).Replace('\', '/') })
    # Same record the per-slice gate results carry, at run level so a reader of this file alone can
    # see which field run's history the store carries without opening a slice directory.
    fieldStoreProvenance = $fieldStoreProvenanceRecord
    # Where the restored store came from, at the top so it is the first thing a reader of a PASS meets.
    storeProvenance = $configuration.storeProvenance
    commits = $commitsRecord

    configurationSha256 = Get-Sha256Text $configurationJson
    configuration = $configuration
    commands = @($commands)
    assertions = $assertionReport
    # What an assertion actually checked, where a PASS alone would not say. Only names whose meaning depends on the run.
    assertionDetails = [ordered]@{
        protocolAndBuildIdentityBoundToTheSharedBinding = $protocolCommitConjunct
    }
    failedAssertions = $failedAssertions
    probe = $probeResult
    handshakeAfterRestart = $handshakeResult
    restart = $restart
    controlDatabaseBaseline = $baseline
    controlDatabaseAfterProbe = $afterProbe
    controlDatabaseFinal = $final
    controlServerVersion = $version
    controlServerVersionAfterRestart = $versionAfterRestart
    error = if ($null -ne $runError) {
        [ordered]@{ type = $runError.Exception.GetType().FullName; message = $runError.Exception.Message }
    } else { $null }
    secretLeakFiles = @($secretLeakFiles)
    evidenceFiles = $artifactFiles
}

$resultJson = $result | ConvertTo-Json -Depth 40
[IO.File]::WriteAllText(
    (Join-Path $EvidenceRoot 'run-result.json'), $resultJson, [Text.UTF8Encoding]::new($false))
$resultJson

if ($status -ne 'DEMAND_BEARING_G3_VECTORS_PASS' -or $secretLeakFiles.Count -ne 0) {
    throw "Demand-bearing G3 vectors did not pass: $status. Evidence: $EvidenceRoot"
}
