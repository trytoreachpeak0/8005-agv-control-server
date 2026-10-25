#Requires -Version 7

<#
一台车的充电单在 RIoT 一直排队、不派给车，占着唯一的桩（control-server#863，2026-10-22 agv03 占 211 近三小时、agv02 低电分不到桩）。

**装置**：两台车停在关卡（站 210，节点 5），都在默认测试策略的强制充电线 30 之上。站 211「充电点1」在节点 6，场景把它登记成假 RIoT 的
充电桩，并经 FieldOps 导入名册。假 RIoT 建出的单停在 1 QUEUEING、executeVehicleKey "--"——场景从不推它，正是 10-22 那张单的样子。
服务端的点名窗口设成 30 秒（setup 的 ChargingOrderUndispatchedAlarmAfter，默认 5 分钟）。

**第一个事实**：主车压到 20%，取得 211 的预占与 CHARGING 用途，单确认（周期 EN_ROUTE）。第二台随后压到 25%：它在队里，报「211 已被预占」。

**第二个事实**：窗口过后，主车那趟充电写上 CHARGING_ORDER_NOT_DISPATCHED，服务端日志出现事件 2284 那一行；211 仍是主车的预占，RIoT 上那张单仍是
1，命令审计里一条订单命令也没有（不取消、不放桩，REQ-0165、REQ-0169）。等的是「码写上」这第二个事实，不读一次就断言。

**现场处置**（票面第 2 条）：主车在 RIoT 上掉线（status 0，10-22 18:27 之后就是这样），人在 RIoT 上取消这张单（orderState 2）。服务端照取消走：
ORDER_ENDED_WITHOUT_ARRIVAL、不重建；车证明不了停稳，211 照旧预占。L2-R11 经主车的车载端确认清桩：CONFIRMED、stationReleased=true，
之后第二台取得 211 的预占与 CHARGING 用途。主车的充电单始终只建过一张。

**红证据（缺陷版本）**：fp/v2-impl 3e62d65b9（本票之前）上，窗口过后旅程上没有任何码、日志里没有 2284，L2-COND-01 变红；之后人工清桩也会被拒
（车不在任何清桩接得住的状态里），L2-COND-02 跟着红。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SingleRow.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Chargers.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$riot = $Context.Riot
$onboard = $Context.Onboard

$charger = 211
$chargerName = '充电点1'
$main = [pscustomobject]@{ AgvId = $Context.AgvId; VehicleKey = $Context.VehicleKey }
$second = [pscustomobject]@{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
$fleetKeys = [string[]]@($main.VehicleKey, $second.VehicleKey)
$serverLog = Join-Path $Context.LogRoot 'control-server.out.log'

function Read-SharedText([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { return [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
}

# Counted in SQL: a query with no rows comes back as $null, and @($null).Count is 1.
function Get-Count([string]$sql) { [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N }

function Set-Vehicle([string]$vehicleKey, [int]$battery, [int]$status = 1) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $vehicleKey; status = $status; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Context.GateStationRiotId; battery = $battery
    })
}

# Who holds the charger, as one line: "<state> <vehicle> <journey>", or "(none)".
function Get-ChargerHeld {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT State, VehicleKey, JourneyId FROM StationExclusivities WHERE MapId = $($Context.MapId) AND StationId = $charger")
    if ($null -eq $row) { return '(none)' }
    return "$($row.State) $($row.VehicleKey) $($row.JourneyId)"
}

function Get-Claim([string]$vehicleKey) {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT Purpose, JourneyId FROM VehiclePurposeClaims WHERE VehicleKey = '$vehicleKey'")
    if ($null -eq $row) { return '(none)' }
    return "$($row.Purpose) $($row.JourneyId)"
}

function Get-JourneyCode([string]$journeyId) {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT IFNULL(BlockReasonCode, '') AS Code FROM JourneyRuntimes WHERE JourneyId = '$journeyId'")
    if ($null -eq $row) { return '(no journey)' }
    return [string]$row.Code
}

function Get-RiotOrderState([string]$upperId) {
    $order = @($riot.Snapshot().body.orders | Where-Object { $null -ne $_ -and [string]$_.upperId -eq $upperId })
    if ($order.Count -ne 1) { return "($($order.Count) orders)" }
    return "$($order[0].orderState) $($order[0].executeVehicleKey)"
}

$undispatchedLine = 'CHARGING_ORDER_NOT_DISPATCHED: charge order'
function Get-UndispatchedLines { ([regex]::Matches((Read-SharedText $serverLog), [regex]::Escape($undispatchedLine))).Count }

# --- 0. 前置：两台车在关卡、电量在线之上；桩登记到假 RIoT 与名册；路网起来 --------------------------------------

Set-Vehicle $main.VehicleKey 50
Set-Vehicle $second.VehicleKey 50
$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{ mapId = $Context.MapId; stationId = $charger; chargeIntervalSeconds = 60; chargePercentPerInterval = 1 })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet $fleetKeys -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 charging-order-not-dispatched-holds-charger: station $charger"
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

# --- 1. 第一个事实：主车取得 211，单确认；第二台排队 ---------------------------------------------------------------

Set-Vehicle $main.VehicleKey 20
$upperId = Wait-L2Condition -Description 'the main vehicle''s charge order was confirmed by RIoT' `
    -Journal $journal -Criterion 'charge-order-confirmed' -TimeoutSeconds 120 `
    -Probe {
        $row = Read-L2SingleRow -Connection $connection -Sql (
            "SELECT UpperId FROM ChargingCycles WHERE WireState = 'EN_ROUTE' AND UpperId IS NOT NULL " +
            "AND VehicleKey = '$($main.VehicleKey)'")
        if ($null -eq $row) { return $null }
        return [string]$row.UpperId
    } `
    -Until { param($v) $null -ne $v }
$journeyId = [string](Read-L2SingleRow -Required -Connection $connection -Sql (
    "SELECT JourneyId FROM ChargingCycles WHERE UpperId = '$upperId'")).JourneyId

Set-Vehicle $second.VehicleKey 25
$queuedLine = "Charging not allocated for vehicle $($second.AgvId): CHARGING_NO_CHARGER_AVAILABLE. battery=25; $charger=CHARGER_RESERVED_OR_OCCUPIED"
$queued = Wait-L2ConditionOrLast -Description 'the second vehicle reported it is queued for the reserved charger' `
    -Journal $journal -Criterion 'second-vehicle-queued' -TimeoutSeconds 30 `
    -Probe { if ((Read-SharedText $serverLog).Contains($queuedLine)) { 'reported' } else { 'silent' } } `
    -Until { param($v) $v -eq 'reported' }
$heldByMain = "RESERVED $($main.VehicleKey) $journeyId"
$assertions.Add(
    'L2-COND-00',
    '前置：主车 20% 取得 211 的预占与 CHARGING 用途，它的充电单 RIoT 收下、停在 1 QUEUEING 没派车（executeVehicleKey --）；第二台 25% 排队，报「211 已被预占」',
    ($queued -eq 'reported' -and (Get-ChargerHeld) -eq $heldByMain -and (Get-Claim $main.VehicleKey) -eq "CHARGING $journeyId" -and
        (Get-Claim $second.VehicleKey) -eq '(none)' -and (Get-RiotOrderState $upperId) -eq '1 --'),
    "reported / $heldByMain / CHARGING $journeyId / (none) / 1 --",
    "$queued / $(Get-ChargerHeld) / $(Get-Claim $main.VehicleKey) / $(Get-Claim $second.VehicleKey) / $(Get-RiotOrderState $upperId)")

# --- 2. 第二个事实：窗口过后点名，什么也不取消、不放 ---------------------------------------------------------------

$named = Wait-L2ConditionOrLast -Description 'the undispatched charge order was named' `
    -Journal $journal -Criterion 'order-not-dispatched-named' -TimeoutSeconds 90 `
    -Probe {
        "$(Get-JourneyCode $journeyId) | $(Get-UndispatchedLines) alarms | $(Get-ChargerHeld) | $(Get-RiotOrderState $upperId) | " +
            "$(Get-Count 'SELECT COUNT(*) AS N FROM RiotOrderCommandAudit') order commands | second $(Get-Claim $second.VehicleKey)"
    } `
    -Until { param($v) $v.StartsWith('CHARGING_ORDER_NOT_DISPATCHED |') }
$expectedNamed = "CHARGING_ORDER_NOT_DISPATCHED | 1 alarms | $heldByMain | 1 -- | 0 order commands | second (none)"
$assertions.Add(
    'L2-COND-01',
    '单一直不派车，30 秒窗口过后：主车那趟充电写上 CHARGING_ORDER_NOT_DISPATCHED、服务端日志告警一次（事件 2284）；211 仍是主车的预占，' +
        'RIoT 上那张单仍是 1 QUEUEING、没有执行车，命令审计里没有任何订单命令（不取消、不放桩），第二台仍分不到',
    ($named -eq $expectedNamed),
    $expectedNamed,
    $named)

# --- 3. 现场处置：车掉线，人在 RIoT 取消单，人工清桩确认，桩给第二台 ---------------------------------------------------

Set-Vehicle $main.VehicleKey 8 0
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 2 })
$ended = Wait-L2ConditionOrLast -Description 'the cancelled charge order was taken as ended without arrival' `
    -Journal $journal -Criterion 'order-ended-without-arrival' -TimeoutSeconds 30 `
    -Probe { "$(Get-JourneyCode $journeyId) | $(Get-ChargerHeld)" } `
    -Until { param($v) $v.StartsWith('ORDER_ENDED_WITHOUT_ARRIVAL |') }

$clearanceId = [guid]::NewGuid().ToString('D')
$sent = $onboard.Command('Put', "station-clearances/$clearanceId", @{ stationId = $chargerName; operatorId = 'L2-R11' })
$journal.Observe('station-clearance-sent', $clearanceId, @{ answer = $sent.body })
$decided = Wait-L2Condition -Description 'the server decided the clearance confirmation' `
    -Journal $journal -Criterion 'clearance-decided' -TimeoutSeconds 30 `
    -Probe {
        $row = Read-L2SingleRow -Connection $connection -Sql (
            "SELECT Outcome, StationReleased FROM ManualStationClearanceConfirmations WHERE ConfirmationRequestId = '$clearanceId'")
        if ($null -eq $row) { return $null }
        "$($row.Outcome) released=$($row.StationReleased)"
    } `
    -Until { param($v) $null -ne $v }

$handedOver = Wait-L2ConditionOrLast -Description 'the second vehicle was committed to the freed charger' `
    -Journal $journal -Criterion 'second-vehicle-committed' -TimeoutSeconds 60 `
    -Probe {
        $held = Get-ChargerHeld
        "$($held.Split(' ')[0..1] -join ' ') | $((Get-Claim $second.VehicleKey).Split(' ')[0])"
    } `
    -Until { param($v) $v -eq "RESERVED $($second.VehicleKey) | CHARGING" }
$mainCreates = Get-Count "SELECT COUNT(*) AS N FROM OrderIntents WHERE VehicleKey = '$($main.VehicleKey)' AND Purpose = 'TO_CHARGER'"
$rebuilds = Get-Count 'SELECT COUNT(*) AS N FROM OwnOrderRebuilds'
$expectedHandover = "ORDER_ENDED_WITHOUT_ARRIVAL | $heldByMain / CONFIRMED released=1 / RESERVED $($second.VehicleKey) | CHARGING / 1 main intents, 0 rebuilds"
$actualHandover = "$ended / $decided / $handedOver / $mainCreates main intents, $rebuilds rebuilds"
$assertions.Add(
    'L2-COND-02',
    '主车在 RIoT 掉线、人在 RIoT 取消那张单：服务端照取消走（ORDER_ENDED_WITHOUT_ARRIVAL、不重建），车证明不了停稳，211 仍预占；' +
        'L2-R11 确认清桩：CONFIRMED、stationReleased=true；之后第二台取得 211 的预占与 CHARGING 用途。主车的充电单只建过一张',
    ($actualHandover -eq $expectedHandover),
    $expectedHandover,
    $actualHandover)

$journal.Note('充电单一直不派车：窗口过后点名告警，不取消、不放桩；人在 RIoT 取消并确认清桩之后，桩给了等着的低电车。')
