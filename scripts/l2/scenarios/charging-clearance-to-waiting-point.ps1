#Requires -Version 7

<#
清桩中的车自己开往等待点，到点即完成清桩（批次9-11，control-server#409；REQ-0178、REQ-0179 的系统证明、REQ-0180、REQ-0293～0296）：
一台车到桩充不上（注入 407802 + 最终 HANG）→ 暂停桩、清桩中 → 旧单在 RIoT 里被人结束 → 没有合格的等待点时原地排队、不猜别的站 →
等待点空出来的那一轮，清桩车 A 与空闲返回车 B 同一轮争它，只有一个成功（A：引擎推进在派车轮末尾的空闲返回评估之前）→ A 开过去 →
到点证据满足的那一轮桩的独占才释放，之前一直在 → 桩的暂停仍在。

**装置**：两台车都停在关卡 210（节点 5）。A 低电（25），去 211 充电；B 电量 80，开场就空闲返回到 214（214 先只对 B 开放，见 setup 文件）。
B 的空闲返回单由场景驱动到点、收敛成 214 的占用。开关 JourneyRuntime:ClearanceToWaitingPointEnabled 打开。

**第一个事实（无合格点，不猜站）**：A 进清桩中、旧单在 RIoT 里结束之后，214 对 A 关着（之后改成开放，但被 B 占着），215 不可达：A 原地排队、
码 CHARGING_CLEARANCE_NO_WAITING_POINT；另等十秒：A 没有任何清桩移动的意图、名下除了 211 没有别的站点独占、RIoT 上没有它的第二张单。

**第二个事实（同一轮争最后一个点）**：B 离开 214 回到 210，离点清扫在那一轮开头放掉 214；同一轮里 A 先拿到（清桩承诺在引擎推进里，
空闲返回评估在派车轮末尾）。另等十秒：214 一直是 A 那趟充电旅程的预占，B 没有空闲返回占有。

**第三个事实（到点才放桩，另等）**：A 的清桩移动单建成、在路上：另等十秒，211 一直是这一趟的独占、清桩记录一直没完成。然后 RIoT 报单完成、
车停在 214：那一轮里清桩完成（证明 ARRIVED_AT_WAITING_POINT、等待点 214）、211 释放（CHARGER_RELEASED_ON_CLEARANCE_AT_WAITING_POINT）、
周期以 CHARGING_UNABLE_TO_CHARGE_CLEARED_AT_WAITING_POINT 结束、用途放开、214 转为 A 的占用、旅程收尾；211 的暂停仍在（没有恢复）。

**红证据（缺陷版本）**：把「无合格点」改成回退到任意站点（例如桩旁的 212），A 被派去登记外的站，L2-CWP-02 变红。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SingleRow.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Chargers.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2WaitingPoints.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$riot = $Context.Riot

$charger = 211
$chargerName = '充电点1'
$point = 214
$vehicleA = $Context.VehicleKey
$vehicleB = 'BROKERX-L2-0002'
$serverLog = Join-Path $Context.LogRoot 'control-server.out.log'

function Read-SharedText([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { return [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
}

# Counted in SQL, so there is always exactly one row to read (README, "whole-array return").
function Get-Count([string]$sql) { [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N }

function Get-Cycle {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, WireState, Phase, IFNULL(UpperId, '') AS UpperId, IFNULL(EndReason, '') AS EndReason " +
        "FROM ChargingCycles")
}

# Who holds a station, as one line: "<state> <vehicle> <journey>", or "(none)".
function Get-Held([int]$station) {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT State, VehicleKey, JourneyId FROM StationExclusivities WHERE MapId = $($Context.MapId) AND StationId = $station")
    if ($null -eq $row) { return '(none)' }
    return "$($row.State) $($row.VehicleKey) $($row.JourneyId)"
}

function Get-Claim([string]$vehicleKey) {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT Purpose, JourneyId FROM VehiclePurposeClaims WHERE VehicleKey = '$vehicleKey'")
    if ($null -eq $row) { return '(none)' }
    return "$($row.Purpose) $($row.JourneyId)"
}

function Get-Holds {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT h.""Trigger"" AS Trig, (SELECT COUNT(*) FROM ChargingStationRecoveries r WHERE r.HoldId = h.HoldId) AS Recovered " +
        "FROM ChargingStationAllocationHolds h WHERE h.MapId = $($Context.MapId) AND h.StationId = $charger")
    if ($rows.Count -eq 0) { return '(none)' }
    return (@($rows | ForEach-Object { "$($_.Trig) recovered=$($_.Recovered)" }) -join '; ')
}

function Get-Clearance {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CASE WHEN CompletedAt IS NULL THEN 'open' ELSE 'completed' END AS Completion, IFNULL(Proof, '') AS Proof, " +
        "IFNULL(WaitingPointStationId, 0) AS Point FROM StationClearances")
}

function Get-JourneyCode([string]$journeyId) {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT Stage, IFNULL(BlockReasonCode, '') AS Code FROM JourneyRuntimes WHERE JourneyId = '$journeyId'")
    if ($null -eq $row) { return '(none)' }
    return "$($row.Stage) $($row.Code)"
}

function Get-ClearanceIntent {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT UpperId, IFNULL(OrderId, '') AS OrderId, Status, DestinationStationId FROM OrderIntents " +
        "WHERE Purpose = 'CLEARANCE_TO_WAITING_POINT' AND VehicleKey = '$vehicleA' ORDER BY CreatedAt DESC LIMIT 1")
}

function Get-IntentCount([string]$vehicleKey) {
    Get-Count "SELECT COUNT(*) AS N FROM OrderIntents WHERE VehicleKey = '$vehicleKey'"
}

function Set-Vehicle([string]$vehicleKey, [int]$position, [int]$battery) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $position; battery = $battery; processingOrder = $false; clearOrderTaskId = $true
    })
}

# RIoT puts the order to executing on the vehicle, the vehicle drives, stops on the target, the order completes.
function Complete-Move([string]$vehicleKey, [string]$upperId, [string]$orderId, [int]$station, [int]$battery) {
    $null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $vehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $vehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $orderId
    })
    Set-Vehicle $vehicleKey $station $battery
    $null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })
}

# --- 0. 前置：桩登记到假 RIoT 与名册，路网起来 ------------------------------------------------------------------

$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{
        mapId = $Context.MapId; stationId = $charger; chargeIntervalSeconds = 1; chargePercentPerInterval = 5
    })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet @($vehicleA, $vehicleB) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 charging-clearance-to-waiting-point: station $charger"
$journal.Observe('charger-roster', [string]$roster.version, @{ import = $roster })

$null = Wait-L2Condition -Description 'the route graph engine finished a refresh cycle' `
    -Journal $journal -Criterion 'route-graph-ready' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql ("SELECT DesignEdgeCount, RuntimeRefreshedAt, StaleReason FROM RouteGraphSnapshots " +
                "WHERE MapId = $($Context.MapId)")
        if ($rows.Count -eq 0) { return $false }
        return [int]$rows[0].DesignEdgeCount -gt 0 -and
            $null -ne $rows[0].RuntimeRefreshedAt -and [string]$rows[0].RuntimeRefreshedAt -ne '' -and
            ($null -eq $rows[0].StaleReason -or [string]$rows[0].StaleReason -eq '')
    } `
    -Until { param($v) $v }

# --- 1. A 低电去充；B 空闲返回到 214 并收敛 ------------------------------------------------------------------------

Set-Vehicle $vehicleA $Context.GateStationRiotId 25
Set-Vehicle $vehicleB $Context.GateStationRiotId 80

$idleIntent = Wait-L2Condition -Description "B's idle return to $point was confirmed by RIoT" `
    -Journal $journal -Criterion 'b-idle-return-confirmed' -TimeoutSeconds 120 `
    -Probe {
        Read-L2SingleRow -Connection $connection -Sql (
            "SELECT UpperId, IFNULL(OrderId, '') AS OrderId, Status FROM OrderIntents " +
            "WHERE Purpose = 'TO_WAITING_POINT' AND VehicleKey = '$vehicleB' AND DestinationStationId = $point")
    } `
    -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
Complete-Move $vehicleB ([string]$idleIntent.UpperId) ([string]$idleIntent.OrderId) $point 80
$bOnPoint = Wait-L2Condition -Description "B's idle return converged on $point" `
    -Journal $journal -Criterion 'b-converged' -TimeoutSeconds 60 `
    -Probe { Get-Held $point } -Until { param($v) $v.StartsWith("OCCUPIED $vehicleB") }

$upperId = Wait-L2Condition -Description "A's charge order was confirmed by RIoT" `
    -Journal $journal -Criterion 'charge-order-confirmed' -TimeoutSeconds 120 `
    -Probe {
        $cycle = Get-Cycle
        if ($null -eq $cycle -or [string]$cycle.WireState -ne 'EN_ROUTE' -or [string]$cycle.UpperId -eq '') { return $null }
        return [string]$cycle.UpperId
    } `
    -Until { param($v) $null -ne $v }
$journeyId = [string](Get-Cycle).JourneyId

# --- 2. A 到桩充不上：407802 + HANG，进清桩中 ------------------------------------------------------------------------

$null = $riot.Command('Put', 'charging/faults', @{ upperId = $upperId; startOutcome = 'CannotCharge' })
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $vehicleA })
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })
Set-Vehicle $vehicleA $charger 25

$clearing = Wait-L2Condition -Description 'the server confirmed A cannot charge and put it in the clearing loop' `
    -Journal $journal -Criterion 'unable-to-charge-confirmed' -TimeoutSeconds 60 `
    -Probe { $cycle = Get-Cycle; "$(Get-Holds) | $(${cycle}?.Phase) | $(Get-Claim $vehicleA)" } `
    -Until { param($v) $v.StartsWith('UNABLE_TO_CHARGE_CONFIRMED') }
$assertions.Add(
    'L2-CWP-01',
    'A 在 211 上充不上（407802 + HANG）：桩暂停（UNABLE_TO_CHARGE_CONFIRMED，未恢复），周期清桩中，A 的用途 CLEARING_MAINTENANCE；B 收敛占着 214',
    ($clearing -eq "UNABLE_TO_CHARGE_CONFIRMED recovered=0 | CLEARING | CLEARING_MAINTENANCE $journeyId" -and
        $bOnPoint.StartsWith("OCCUPIED $vehicleB")),
    "UNABLE_TO_CHARGE_CONFIRMED recovered=0 | CLEARING | CLEARING_MAINTENANCE $journeyId / 214 OCCUPIED $vehicleB",
    "$clearing / 214 $bOnPoint")

# --- 3. 旧单在 RIoT 里结束；214 改成对全部车辆开放，但被 B 占着：无合格点，不猜站（另等） --------------------------------

$reopen = Invoke-L2WaitingPointImport -Points @(
    [ordered]@{ StationId = $point; StationName = '等待点1'; Enabled = $true; VehicleScope = @() },
    [ordered]@{ StationId = 215; StationName = '等待点2'; Enabled = $true; VehicleScope = @() }
) -MapId $Context.MapId -Fleet @($vehicleA, $vehicleB) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -StageRoot $Context.SnapshotRoot
$journal.Observe('waiting-points-reopened', 'all vehicles', @{ import = $reopen })
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 2 })

# Not a throwing wait: a version that sends A somewhere instead must go red on L2-CWP-02, which says what it did.
$null = Wait-L2ConditionOrLast -Description 'A queued with no eligible waiting point' `
    -Journal $journal -Criterion 'no-waiting-point' -TimeoutSeconds 60 `
    -Probe { Get-JourneyCode $journeyId } -Until { param($v) $v.EndsWith('CHARGING_CLEARANCE_NO_WAITING_POINT') }
$queued = "AwaitingPickupArrival CHARGING_CLEARANCE_NO_WAITING_POINT | 0 clearance intents | 1 stations held by A | 1 intents of A | 214 OCCUPIED $vehicleB"
$stays = Wait-L2ConditionOrLast -Description 'A was sent somewhere while no waiting point was eligible (it may not be)' `
    -Journal $journal -Criterion 'queued-no-guess' -TimeoutSeconds 10 `
    -Probe {
        "$(Get-JourneyCode $journeyId) | " +
            "$(Get-Count "SELECT COUNT(*) AS N FROM OrderIntents WHERE Purpose = 'CLEARANCE_TO_WAITING_POINT'") clearance intents | " +
            "$(Get-Count "SELECT COUNT(*) AS N FROM StationExclusivities WHERE VehicleKey = '$vehicleA'") stations held by A | " +
            "$(Get-IntentCount $vehicleA) intents of A | 214 $((Get-Held $point) -replace ' idle-return:.*$', '')"
    } `
    -Until { param($v) $v -ne $queued }
$warned = ([regex]::Matches((Read-SharedText $serverLog), 'CHARGING_CLEARANCE_NO_WAITING_POINT: vehicle')).Count
$assertions.Add(
    'L2-CWP-02',
    '旧单结束、214 被 B 占着、215 不可达：A 原地排队（CHARGING_CLEARANCE_NO_WAITING_POINT），另等十秒没有清桩移动意图、名下只有 211、只有那一张充电单；不猜别的站（REQ-0178）；告警恰好一次',
    ($stays -eq $queued -and $warned -eq 1),
    "$queued / warned once",
    "$stays / warned $warned")

# --- 4. B 离开 214：同一轮里 A 先拿到，B 没拿到（另等） --------------------------------------------------------------

Set-Vehicle $vehicleB $Context.GateStationRiotId 80
$winner = Wait-L2Condition -Description '214 was taken again once B left it' `
    -Journal $journal -Criterion 'point-retaken' -TimeoutSeconds 60 `
    -Probe { Get-Held $point } -Until { param($v) $v.StartsWith('RESERVED') }
$expectedWinner = "RESERVED $vehicleA $journeyId | (none)"
$contest = Wait-L2ConditionOrLast -Description 'the last waiting point changed hands or B committed too (neither may)' `
    -Journal $journal -Criterion 'one-winner' -TimeoutSeconds 10 `
    -Probe { "$(Get-Held $point) | $(Get-Claim $vehicleB)" } `
    -Until { param($v) $v -ne $expectedWinner }
$assertions.Add(
    'L2-CWP-03',
    'B 离开 214、离点清扫放掉它的那一轮，清桩车 A 与空闲返回车 B 争这最后一个点：只有一个成功——A（清桩承诺在引擎推进里，空闲返回评估在派车轮末尾）；另等十秒，214 一直是 A 那趟充电旅程的预占，B 没有空闲返回占有',
    ($winner.StartsWith("RESERVED $vehicleA") -and $contest -eq $expectedWinner),
    $expectedWinner,
    "$winner / $contest")

# --- 5. A 在路上：桩的独占一直在（另等） ----------------------------------------------------------------------------

$move = Wait-L2Condition -Description "A's clearance move was confirmed by RIoT" `
    -Journal $journal -Criterion 'clearance-move-confirmed' -TimeoutSeconds 60 `
    -Probe { Get-ClearanceIntent } -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
$null = $riot.Command('Put', "orders/$($move.UpperId)", @{ orderState = 3; executeVehicleKey = $vehicleA })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $vehicleA; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = [string]$move.OrderId
})
$onTheWay = "RESERVED $vehicleA $journeyId | open | RESERVED $vehicleA $journeyId"
$held = Wait-L2ConditionOrLast -Description 'the charger was released before A arrived (it may not be)' `
    -Journal $journal -Criterion 'charger-held-on-the-way' -TimeoutSeconds 10 `
    -Probe { $clearance = Get-Clearance; "$(Get-Held $charger) | $(${clearance}?.Completion) | $(Get-Held $point)" } `
    -Until { param($v) $v -ne $onTheWay }
$assertions.Add(
    'L2-CWP-04',
    'A 的清桩移动单建成（单段 move，目标 214），车在路上：另等十秒，211 一直是这一趟的独占、清桩记录一直未完成、214 一直是预占',
    ($held -eq $onTheWay -and [int]$move.DestinationStationId -eq $point),
    "$onTheWay / to $point",
    "$held / to $($move.DestinationStationId)")

# --- 6. 到点：同一次保存里清桩完成、放桩、214 转占用、旅程收尾；暂停仍在 -----------------------------------------------

Set-Vehicle $vehicleA $point 25
$null = $riot.Command('Put', "orders/$($move.UpperId)", @{ orderState = 5 })
$arrived = Wait-L2Condition -Description 'the clearance completed at the waiting point' `
    -Journal $journal -Criterion 'cleared-at-waiting-point' -TimeoutSeconds 60 `
    -Probe {
        $cycle = Get-Cycle
        $clearance = Get-Clearance
        $record = Read-L2SingleRow -Connection $connection -Sql (
            "SELECT IFNULL(ReleaseReason, '') AS Reason FROM StationExclusivityRecords " +
            "WHERE MapId = $($Context.MapId) AND StationId = $charger AND JourneyId = '$journeyId'")
        "$(Get-Held $charger) | $(${record}?.Reason) | $(${cycle}?.Phase) $(${cycle}?.EndReason) | " +
            "$(Get-JourneyCode $journeyId) | $(Get-Claim $vehicleA) | $(Get-Holds) | " +
            "$(${clearance}?.Completion) $(${clearance}?.Proof) $(${clearance}?.Point) | $(Get-Held $point)"
    } `
    -Until { param($v) $v.StartsWith('(none)') }
$expectedArrived = '(none) | CHARGER_RELEASED_ON_CLEARANCE_AT_WAITING_POINT | ENDED CHARGING_UNABLE_TO_CHARGE_CLEARED_AT_WAITING_POINT | ' +
    'Completed CHARGING_UNABLE_TO_CHARGE_CLEARED_AT_WAITING_POINT | (none) | UNABLE_TO_CHARGE_CONFIRMED recovered=0 | ' +
    "completed ARRIVED_AT_WAITING_POINT $point | OCCUPIED $vehicleA $journeyId"
$assertions.Add(
    'L2-CWP-05',
    '到点证据满足的那一轮：清桩完成（ARRIVED_AT_WAITING_POINT，等待点 214）、211 释放（CHARGER_RELEASED_ON_CLEARANCE_AT_WAITING_POINT）、周期以 CHARGING_UNABLE_TO_CHARGE_CLEARED_AT_WAITING_POINT 结束、旅程收尾、用途放开、214 转为 A 的占用；211 的暂停仍在（没有恢复）',
    ($arrived -eq $expectedArrived),
    $expectedArrived,
    $arrived)

$commands = Get-Count "SELECT COUNT(*) AS N FROM RiotOrderCommandAudit"
$assertions.Add(
    'L2-CWP-06',
    '全程本服务端没有发任何订单命令（不取消旧单——开关默认关——也不取消清桩移动单）',
    ($commands -eq 0),
    0,
    $commands)

$journal.Note('清桩开往等待点：无合格点时原地排队不猜站；最后一个点同一轮只有一个成功；到点那一轮才放桩，暂停仍在。')
