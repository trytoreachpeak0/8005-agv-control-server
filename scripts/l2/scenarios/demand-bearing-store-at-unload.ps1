#Requires -Version 7

<#
需求承载 G3 的合成库生成器（control-server#453）。**它不是 L2 判据**，不登记进 l2.yml 的 CI 清单：
它的产物是一份库，由 run-demand-bearing-g3-vectors.ps1 恢复之后再去断言。

那个 runner 原来恢复的是 2026-08-29 agv01 真车运行留下的库（fullloop-20260829T131549Z），那个目录已经丢了，
而且只有真车、真 RIoT 才能再造。runner 要的其实只是库的形状：一条受理的需求，装货已 Committed，卸货命令已下发
而结果没回（Prepared），一辆车一行会话恢复，三张收尾表都是空的。这里用合成车载端加假 RIoT 走到那一步：
卸货应答挂起（unloadResult = Manual），旅程停在 AwaitingUnloadResult，然后把库用 VACUUM INTO 导出成一份
一致的快照。

导出位置是本次证据根下的 demand-bearing-store/controlserver.db。下面的 L2-DBS-* 是生成器的自检：导出的那份
不是 runner 要的形状就当场红，而不是让 runner 在一个小时以后才说「库不对」。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$connection = $Context.Connection

$storeRoot = Join-Path (Split-Path -Parent $Context.SnapshotRoot) 'demand-bearing-store'
$storePath = Join-Path $storeRoot 'controlserver.db'

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

function Get-Stage {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

function Get-Intent([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Complete-Leg([string]$UpperId, [string]$StationRiotId) {
    $null = $riot.Command('Put', "orders/$UpperId", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey       = $Context.VehicleKey
        procState        = 'IDLE'
        movementState    = 'MT_FINISHED'
        speed            = 0
        currentPosition  = $StationRiotId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$UpperId", @{ orderState = 5 })
}

# --- 走到闸口，卸货应答挂起 -------------------------------------------------------------------------

$null = $onboard.Command('Put', 'policy', @{ unloadResult = 'Manual' })

$journal.Note("Publishing demand $demandIdWire (sublot $sublot).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 90 `
    -Probe { $row = Get-Intent -purpose 'TO_PICKUP'; if ($row -and $row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }
$journal.Note('Vehicle drives to the pickup station and comes to rest.')
Complete-Leg -UpperId $pickupIntent.UpperId -StationRiotId $Context.PickupStationRiotId

$gateIntent = Wait-L2Condition -Description 'the load committed and the TO_GATE intent was confirmed' `
    -Journal $journal -Criterion 'to-gate-intent' -TimeoutSeconds 120 `
    -Probe { $row = Get-Intent -purpose 'TO_GATE'; if ($row -and $row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }
$journal.Note('Vehicle drives to the gate and comes to rest.')
Complete-Leg -UpperId $gateIntent.UpperId -StationRiotId $Context.GateStationRiotId

$null = Wait-L2Condition -Description 'the unload command went out and the journey waits for its result' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingUnloadResult' }
# The peer holding the command is what makes the Prepared row "sent, no result yet" rather than "about to be sent".
$null = Wait-L2Condition -Description 'the unload command is pending on the synthetic peer' `
    -Journal $journal -Criterion 'pending-unload' -TimeoutSeconds 30 `
    -Probe { @($onboard.Snapshot().body.pending | Where-Object { $_.messageType -eq 'SlotOperationCommand' }).Count } `
    -Until { param($v) $v -ge 1 }

# --- 导出 -------------------------------------------------------------------------------------------

# VACUUM INTO writes a consistent snapshot of the live database, WAL included, into a single new file, so nothing has to
# stop the server first and no -wal or -shm file has to travel with it.
$null = New-Item -ItemType Directory -Path $storeRoot -Force
$exporter = [Microsoft.Data.Sqlite.SqliteConnection]::new(
    "Data Source=$($connection.DataSource);Mode=ReadOnly;Cache=Private;Pooling=False;Default Timeout=30")
try {
    $exporter.Open()
    $command = $exporter.CreateCommand()
    $command.CommandText = 'VACUUM INTO $path'
    $null = $command.Parameters.AddWithValue('$path', $storePath)
    $null = $command.ExecuteNonQuery()
    $command.Dispose()
}
finally {
    $exporter.Dispose()
}
$journal.Note("Exported the store to $storePath.")

# --- 自检：导出的那份是 runner 要的形状 ---------------------------------------------------------------

$store = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$storePath;Mode=ReadOnly;Pooling=False")
$store.Open()
try {
    $operations = (Invoke-L2Query -Connection $store -Sql 'SELECT OperationType, Status FROM StationOperations')
    $prepared = @($operations | Where-Object { [string]$_.Status -eq 'Prepared' })
    $committed = @($operations | Where-Object { [string]$_.Status -eq 'Committed' })
    $assertions.Add(
        'L2-DBS-01', '导出的库里恰好一条 Prepared 的卸货操作、至少一条 Committed 的装货操作',
        ($prepared.Count -eq 1 -and [string]$prepared[0].OperationType -eq 'Unload' -and
            @($committed | Where-Object { [string]$_.OperationType -eq 'Load' }).Count -ge 1),
        'Prepared Unload ×1 / Committed Load ≥1',
        (@($operations | ForEach-Object { "$($_.OperationType):$($_.Status)" }) -join ', '))

    $results = (Invoke-L2Query -Connection $store -Sql 'SELECT ResultId FROM OperationResults').Count
    $sessions = (Invoke-L2Query -Connection $store -Sql 'SELECT AgvId FROM SessionRecoveries')
    $assertions.Add(
        'L2-DBS-02', '卸货结果没回（结果只有装货那一条），一辆车一行会话恢复',
        ($results -eq 1 -and $sessions.Count -eq 1 -and [string]$sessions[0].AgvId -eq $Context.AgvId),
        "1 result / 1 session row ($($Context.AgvId))",
        "$results results / $($sessions.Count) session rows ($(@($sessions | ForEach-Object AgvId) -join ', '))")

    $demand = (Invoke-L2Query -Connection $store -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'")
    $closures = foreach ($table in @('UnloadBatches', 'StopClosures', 'TransportDemandCompletions')) {
        "$table=$((Invoke-L2Query -Connection $store -Sql "SELECT 1 AS One FROM $table").Count)"
    }
    $assertions.Add(
        'L2-DBS-03', '需求已受理未收尾，三张收尾表都是空的',
        ($demand.Count -eq 1 -and [string]$demand[0].Status -eq 'Accepted' -and
            (@($closures) -join ',') -eq 'UnloadBatches=0,StopClosures=0,TransportDemandCompletions=0'),
        'Accepted / UnloadBatches=0,StopClosures=0,TransportDemandCompletions=0',
        "$(@($demand | ForEach-Object Status) -join ',') / $(@($closures) -join ',')")
}
finally {
    $store.Dispose()
}

$journal.Note('Generator finished.')
