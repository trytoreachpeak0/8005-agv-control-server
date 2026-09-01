[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [switch]$AsJson
)

# Read-only view of a run started by Start-Ticket10FieldRun.ps1. Everything comes out of a snapshot
# copy of the isolated store, so polling never touches the live database or the running products.
# Microsoft.Data.Sqlite is loaded from the release package rather than the run root, because
# Add-Type -Path locks the file it loads and the run root has to stay deletable.

$ErrorActionPreference = 'Stop'
$start = Get-Content -LiteralPath (Join-Path $RunRoot 'run-start.json') -Raw | ConvertFrom-Json
$package = Join-Path $start.releaseRoot 'controlserver'
Add-Type -Path (Join-Path $package 'Microsoft.Data.Sqlite.dll')

$snapshot = Join-Path $RunRoot "watch-$([Guid]::NewGuid().ToString('N').Substring(0,8)).db"
Copy-Item -LiteralPath $start.databasePath -Destination $snapshot -Force
foreach ($suffix in '-wal', '-shm') {
    if (Test-Path -LiteralPath "$($start.databasePath)$suffix") {
        Copy-Item -LiteralPath "$($start.databasePath)$suffix" -Destination "$snapshot$suffix" -Force
    }
}
$connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$snapshot")
$connection.Open()

function Get-Rows([string]$Sql) {
    $command = $connection.CreateCommand()
    $command.CommandText = $Sql
    $reader = $command.ExecuteReader()
    $rows = [System.Collections.Generic.List[object]]::new()
    while ($reader.Read()) {
        $row = [ordered]@{}
        for ($i = 0; $i -lt $reader.FieldCount; $i++) {
            $row[$reader.GetName($i)] = if ($reader.IsDBNull($i)) { $null } else { $reader.GetValue($i) }
        }
        $rows.Add([pscustomobject]$row)
    }
    $reader.Close()
    return $rows
}

$state = [ordered]@{
    observedAt = [DateTimeOffset]::Now.ToString('HH:mm:ss')
    journey    = Get-Rows 'SELECT DemandId, Stage, PickupStationId, GateStationId, ExpectedBasketCount, DispatchGeneration, WorklistRevision, PlanRevision, BlockReasonCode, UpdatedAt FROM "JourneyRuntimes"'
    session    = Get-Rows 'SELECT AgvId, SessionGeneration, Readiness, ReasonCode, UpdatedAt FROM "SessionRecoveries"'
    orders     = Get-Rows 'SELECT UpperId, Purpose, TargetStationId, DestinationStationId, Status, OrderId FROM "OrderIntents"'
    dispatch   = Get-Rows 'SELECT Sequence, UpperId, Phase, Outcome, ReceiptClassification, OccurredAt FROM "RiotDispatchAuditEvents" ORDER BY Sequence'
    operations = Get-Rows 'SELECT OperationType, Status, SublotId, SlotOperationAttemptId FROM "StationOperations"'
    backlog    = Get-Rows 'SELECT ReasonCode, COUNT(*) AS ReasonRows FROM "JourneyBacklog" GROUP BY ReasonCode ORDER BY ReasonRows DESC'
}
$connection.Close()
[Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
Remove-Item -LiteralPath $snapshot -Force -ErrorAction SilentlyContinue
foreach ($suffix in '-wal', '-shm') { Remove-Item -LiteralPath "$snapshot$suffix" -Force -ErrorAction SilentlyContinue }

if ($AsJson) { return ($state | ConvertTo-Json -Depth 6) }

$summary = "[{0}] stage={1} block={2} readiness={3}/{4} orders={5} dispatchEvents={6} operations={7}" -f `
    $state.observedAt,
    (@($state.journey | ForEach-Object { $_.Stage }) -join ','),
    (@($state.journey | ForEach-Object { $_.BlockReasonCode } | Where-Object { $_ }) -join ','),
    (@($state.session | ForEach-Object { $_.Readiness }) -join ','),
    (@($state.session | ForEach-Object { $_.ReasonCode }) -join ','),
    $state.orders.Count, $state.dispatch.Count, $state.operations.Count
Write-Output $summary
if ($state.backlog.Count -gt 0) { $state.backlog | Format-Table -AutoSize | Out-String -Width 200 }
if ($state.orders.Count -gt 0) { $state.orders | Format-Table -AutoSize | Out-String -Width 200 }
if ($state.dispatch.Count -gt 0) { $state.dispatch | Format-Table -AutoSize | Out-String -Width 200 }
if ($state.operations.Count -gt 0) { $state.operations | Format-Table -AutoSize | Out-String -Width 200 }
