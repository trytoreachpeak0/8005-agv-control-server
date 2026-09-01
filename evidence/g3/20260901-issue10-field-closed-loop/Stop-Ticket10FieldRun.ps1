[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    # Red side only. Skips the process kill loop so the detectors below can be re-run against a
    # mutated copy of a finished run. Nothing else changes: the red side executes this same file.
    [switch]$DetectorsOnly
)

# Ends a run started by Start-Ticket10FieldRun.ps1 and reads the outcome out of the isolated store
# into machine-readable assertions. Kept separate so the observation window is decided on the floor
# rather than by a launcher timeout.
#
# Unlike the ticket 14 stop script there is no trust to remove: this run installed no certificate.
# In its place we take the observations the run itself never wrote — the certificate store digests,
# the absence of TLS diagnostics in the host log, and the production service's PID and listeners.

$ErrorActionPreference = 'Stop'
$start = Get-Content -LiteralPath (Join-Path $RunRoot 'run-start.json') -Raw | ConvertFrom-Json

if (-not $DetectorsOnly) {
    foreach ($id in @($start.onboardPid, $start.hostPid, $start.simulatorPid)) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(20000) | Out-Null }
    }
    Start-Sleep -Seconds 3
}

function Get-StoreDigest {
    param(
        [System.Security.Cryptography.X509Certificates.StoreName]$Name,
        [System.Security.Cryptography.X509Certificates.StoreLocation]$Location
    )
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($Name, $Location)
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
    $thumbprints = @($store.Certificates | ForEach-Object { $_.Thumbprint } | Sort-Object)
    $store.Close(); $store.Dispose()
    $canonical = $thumbprints -join "`n"
    $digest = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
    return [pscustomobject]@{ Store = "$Location\$Name"; Count = $thumbprints.Count; Digest = $digest }
}

$storeDigestsAtStop = @(
    (Get-StoreDigest -Name Root -Location CurrentUser),
    (Get-StoreDigest -Name My -Location CurrentUser),
    (Get-StoreDigest -Name Root -Location LocalMachine),
    (Get-StoreDigest -Name My -Location LocalMachine)
)
# Build the comparison lines as variables first. A format operator written directly inside a method
# call argument list binds its commas to the call, not to -f, and the Add() then never runs.
$storeDrift = [System.Collections.Generic.List[string]]::new()
foreach ($stop in $storeDigestsAtStop) {
    $before = @($start.storeDigestsAtStart | Where-Object { $_.Store -eq $stop.Store })
    if ($before.Count -ne 1) {
        $line = 'MISSING_BASELINE {0}' -f $stop.Store
        $storeDrift.Add($line)
        continue
    }
    if ($before[0].Digest -ne $stop.Digest -or [int]$before[0].Count -ne [int]$stop.Count) {
        $line = '{0} count {1}->{2} digest {3}->{4}' -f `
            $stop.Store, $before[0].Count, $stop.Count, $before[0].Digest, $stop.Digest
        $storeDrift.Add($line)
    }
}

$keyMaterial = @(Get-ChildItem -LiteralPath $RunRoot -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.pfx', '.pem', '.cer', '.crt', '.key', '.p12' } |
    ForEach-Object { $_.FullName })

$outLog = Join-Path $RunRoot 'host.out.log'
$errorLog = Join-Path $RunRoot 'host.err.log'
$tlsDiagnostics = @(Get-ChildItem -LiteralPath $RunRoot -Recurse -File -Include '*.log', '*.ndjson' -ErrorAction SilentlyContinue |
    Select-String -Pattern 'Schannel|SslStream|AuthenticationException|X509|certificate|https://' -AllMatches |
    ForEach-Object { '{0}:{1}' -f $_.Filename, $_.LineNumber })

$productionService = Get-CimInstance -ClassName Win32_Service -Filter "Name='8005 AGV ControlServer'"
$productionPid = [int]$productionService.ProcessId
$productionPorts = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
    Where-Object { $_.OwningProcess -eq $productionPid } |
    ForEach-Object { '{0}:{1}' -f $_.LocalAddress, $_.LocalPort } | Sort-Object)

$package = Join-Path $start.releaseRoot 'controlserver'
Add-Type -Path (Join-Path $package 'Microsoft.Data.Sqlite.dll')
$snapshot = Join-Path $RunRoot 'final.db'
Copy-Item -LiteralPath $start.databasePath -Destination $snapshot -Force
foreach ($suffix in '-wal', '-shm') {
    if (Test-Path -LiteralPath "$($start.databasePath)$suffix") {
        Copy-Item -LiteralPath "$($start.databasePath)$suffix" -Destination "$snapshot$suffix" -Force
    }
}
$connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$snapshot")
$connection.Open()
function Get-Scalar([string]$Sql) {
    $command = $connection.CreateCommand(); $command.CommandText = $Sql
    $value = $command.ExecuteScalar()
    return $(if ($null -eq $value -or $value -is [DBNull]) { $null } else { $value })
}
function Get-Rows([string]$Sql) {
    $command = $connection.CreateCommand(); $command.CommandText = $Sql
    $reader = $command.ExecuteReader()
    $rows = [System.Collections.Generic.List[object]]::new()
    while ($reader.Read()) {
        $row = [ordered]@{}
        for ($i = 0; $i -lt $reader.FieldCount; $i++) {
            $row[$reader.GetName($i)] = if ($reader.IsDBNull($i)) { $null } else { $reader.GetValue($i).ToString() }
        }
        $rows.Add([pscustomobject]$row)
    }
    $reader.Close()
    return $rows
}

$journey = Get-Rows 'SELECT DemandId, Stage, PickupStationId, PickupStationRiotId, GateStationId, GateStationRiotId, ExpectedBasketCount, DispatchGeneration, VehicleBusinessRevision, WorklistRevision, PlanRevision, BlockReasonCode, CreatedAt, UpdatedAt FROM "JourneyRuntimes"'
$sessions = Get-Rows 'SELECT AgvId, SessionGeneration, Readiness, ReasonCode FROM "SessionRecoveries"'
$orders = Get-Rows 'SELECT UpperId, Purpose, TargetStationId, DestinationStationId, Status, OrderId FROM "OrderIntents"'
$dispatch = Get-Rows 'SELECT Sequence, UpperId, Phase, Outcome, ReceiptClassification, OccurredAt FROM "RiotDispatchAuditEvents" ORDER BY Sequence'
$operations = Get-Rows 'SELECT OperationType, Status, SublotId FROM "StationOperations"'
$backlog = Get-Rows 'SELECT ReasonCode, COUNT(*) AS ReasonRows FROM "JourneyBacklog" GROUP BY ReasonCode ORDER BY ReasonRows DESC'
$counts = [ordered]@{}
foreach ($table in 'AcceptedDemands', 'JourneyRuntimes', 'JourneyBacklog', 'OrderIntents',
                   'RiotDispatchAuditEvents', 'StationOperations', 'OperationResults',
                   'ProtocolInbox', 'ProtocolOutbox') {
    $counts[$table] = [int](Get-Scalar "SELECT COUNT(*) FROM `"$table`"")
}
$connection.Close()
[Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()

$stage = @($journey | ForEach-Object { $_.Stage }) -join ','
$blocked = @($journey | ForEach-Object { $_.BlockReasonCode } | Where-Object { $_ }) -join ','
$stillListening = @(Get-NetTCPConnection -State Listen `
        -LocalPort $start.controlPort, $start.healthPort, 1502, 58006 -ErrorAction SilentlyContinue |
    ForEach-Object { $_.LocalPort })

$result = [ordered]@{
    schemaVersion          = 1
    runKind                = $start.runKind
    releaseRoot            = $start.releaseRoot
    serverCommit           = $start.serverCommit
    onboardCommit          = $start.onboardCommit
    protocolTag            = $start.protocolTag
    dispatchGeneration     = $start.dispatchGeneration
    operatorId             = $start.operatorId
    createGateOpen         = $start.createGateOpen
    startedAt              = $start.startedAt
    finishedAt             = [DateTimeOffset]::UtcNow.ToString('O')
    journeyStage           = $stage
    blockReasonCode        = $blocked
    completed              = ($stage -eq 'Completed' -and [string]::IsNullOrEmpty($blocked))
    portsReleased          = ($stillListening.Count -eq 0)
    hostStderrEmpty        = ((Test-Path $errorLog) -and ((Get-Item $errorLog).Length -eq 0))
    hostStdoutLines        = $(if (Test-Path $outLog) { @(Get-Content -LiteralPath $outLog).Count } else { 0 })
    storeDigestsAtStop     = $storeDigestsAtStop
    storeDrift             = @($storeDrift)
    keyMaterialFiles       = $keyMaterial
    tlsDiagnosticHits      = $tlsDiagnostics
    productionServicePid   = $productionPid
    productionServicePidUnchanged = ([int]$start.productionServicePid -eq $productionPid)
    productionServicePorts = $productionPorts
    counts                 = $counts
    backlogByReason        = $backlog
    journey                = $journey
    sessions               = $sessions
    orderIntents           = $orders
    dispatchAudit          = $dispatch
    stationOperations      = $operations
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RunRoot 'run-result.json') -Encoding utf8NoBOM

$line = "stage={0} block={1} completed={2} orders={3} dispatchEvents={4} operations={5} portsReleased={6} storeDrift={7} keyMaterial={8} tlsHits={9} prodPidUnchanged={10}" -f `
    $stage, $blocked, $result.completed, $counts.OrderIntents, $counts.RiotDispatchAuditEvents,
    $counts.StationOperations, $result.portsReleased, $storeDrift.Count, $keyMaterial.Count,
    $tlsDiagnostics.Count, $result.productionServicePidUnchanged
Write-Output $line
