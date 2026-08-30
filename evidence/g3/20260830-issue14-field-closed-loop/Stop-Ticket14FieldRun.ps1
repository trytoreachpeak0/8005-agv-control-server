[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunRoot)

# Ends a run started by Start-Ticket14FieldRun.ps1, removes the temporary trust it installed, and
# reads the outcome out of the isolated store into machine-readable assertions. Kept separate so the
# observation window is decided on the floor rather than by a launcher timeout.

$ErrorActionPreference = 'Stop'
$start = Get-Content -LiteralPath (Join-Path $RunRoot 'run-start.json') -Raw | ConvertFrom-Json

foreach ($id in @($start.onboardPid, $start.hostPid, $start.simulatorPid)) {
    $process = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(20000) | Out-Null }
}
Start-Sleep -Seconds 3

$cleanup = [System.Security.Cryptography.X509Certificates.X509Store]::new(
    [System.Security.Cryptography.X509Certificates.StoreName]::Root,
    [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
$cleanup.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
foreach ($certificate in @($cleanup.Certificates.Find(
            [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $start.certificateThumbprint, $false))) {
    $cleanup.Remove($certificate)
}
$cleanup.Close(); $cleanup.Dispose()
$verify = [System.Security.Cryptography.X509Certificates.X509Store]::new(
    [System.Security.Cryptography.X509Certificates.StoreName]::Root,
    [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
$verify.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
$trustRemaining = @($verify.Certificates.Find(
        [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
        $start.certificateThumbprint, $false)).Count
$verify.Close(); $verify.Dispose()
Remove-Item -LiteralPath (Join-Path $RunRoot 'loopback-server.pfx') -Force -ErrorAction SilentlyContinue

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
$counts = [ordered]@{}
foreach ($table in 'AcceptedDemands', 'JourneyRuntimes', 'OrderIntents', 'RiotDispatchAuditEvents',
                   'StationOperations', 'OperationResults', 'ProtocolInbox', 'ProtocolOutbox') {
    $counts[$table] = [int](Get-Scalar "SELECT COUNT(*) FROM `"$table`"")
}
$connection.Close()
[Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()

$errorLog = Join-Path $RunRoot 'host.err.log'
$stage = @($journey | ForEach-Object Stage) -join ','
$blocked = @($journey | ForEach-Object BlockReasonCode | Where-Object { $_ }) -join ','
$stillListening = @(Get-NetTCPConnection -State Listen `
        -LocalPort $start.controlPort, $start.healthPort, 1502, 58006 -ErrorAction SilentlyContinue)

$result = [ordered]@{
    schemaVersion      = 1
    runKind            = $start.runKind
    releaseRoot        = $start.releaseRoot
    serverCommit       = $start.serverCommit
    onboardCommit      = $start.onboardCommit
    protocolTag        = $start.protocolTag
    dispatchGeneration = $start.dispatchGeneration
    operatorId         = $start.operatorId
    createGateOpen     = $start.createGateOpen
    startedAt          = $start.startedAt
    finishedAt         = [DateTimeOffset]::UtcNow.ToString('O')
    journeyStage       = $stage
    blockReasonCode    = $blocked
    completed          = ($stage -eq 'Completed' -and [string]::IsNullOrEmpty($blocked))
    portsReleased      = ($stillListening.Count -eq 0)
    hostStderrEmpty    = ((Test-Path $errorLog) -and ((Get-Item $errorLog).Length -eq 0))
    trustRemaining     = $trustRemaining
    counts             = $counts
    journey            = $journey
    sessions           = $sessions
    orderIntents       = $orders
    dispatchAudit      = $dispatch
    stationOperations  = $operations
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RunRoot 'run-result.json') -Encoding utf8NoBOM
"stage={0} block={1} completed={2} orders={3} dispatchEvents={4} operations={5} portsReleased={6} trustRemaining={7}" -f `
    $stage, $blocked, $result.completed, $counts.OrderIntents, $counts.RiotDispatchAuditEvents,
    $counts.StationOperations, $result.portsReleased, $trustRemaining
