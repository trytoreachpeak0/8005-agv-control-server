#Requires -Version 7

<#
会话在仓门开着时建立、随后门锁上，车要能去充电（control-server#865；10-23 agv02 第 50 代会话：14:38:12 握手时 1 号仓开着，14:39:15
锁上，之后 16:25～16:44 五条 2241 仍报 SLOT_1_NOT_LOCKED，电量掉到断电）。

**装置**：单车停在关卡 210（节点 5）。合成车载端握手快照里 1 号仓 UNLOCKED（setup 的 SlotStates），安全摘要是安全的——那一代会话 READY，
派车看的五个布尔都过，挡住它的只有出发前安全门读逐仓锁态的那一项。场景把站 211 登记成假 RIoT 的充电桩、导入名册，打开合成车载端的
「应答快照请求」（默认关）。编排器的默认测试策略：强制充电线 30。

**前提（L2-DOL-01）**：服务端收到的握手快照里 1 号仓确实是 UNLOCKED。种子配错了，后面每一条都在证明另一回事。

**第一个事实（门真开着不分桩，L2-DOL-02）**：电量设成 25，之后另等二十秒，一直没有充电周期。门锁没证明仍不出发。

**第二个事实（锁上之后去充电，L2-DOL-03）**：场景报 1 号仓 LOCKED（发一条 SafetyStateChanged，affectedSlots 1..8，与真车载端一样不带逐仓
状态）。服务端要一张新快照、合成车载端答回 1 号仓 LOCKED，下一轮分到 211、建单、RIoT 确认。等的是开往 211 的单被确认；同一个探针里记下
服务端收到的最新一张快照的 1 号仓锁态与版本——它在因果上先于分桩落库。

**红证据（缺陷版本）**：服务端不追要快照时，逐仓锁态一直是握手那一张，L2-DOL-03 等满超时变红。
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
$enterExit = 212
$vehicleKey = $Context.VehicleKey

# Counted in SQL: a query with no rows comes back as $null, and @($null).Count is 1.
function Get-Count([string]$sql) { [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N }

# Slot 1's lock in the newest SafetyStateSnapshot the server holds, with its safetyStateVersion: "<version> <lockState>".
function Get-LatestSnapshotSlot1 {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT json_extract(RequestJson, '$.payload.safetyStateVersion') AS Version, " +
        "json_extract(RequestJson, '$.payload.slotStates[0].lockState') AS LockState FROM ProtocolInbox " +
        "WHERE MessageType = 'SafetyStateSnapshot' ORDER BY json_extract(RequestJson, '$.payload.safetyStateVersion') DESC LIMIT 1")
    if ($null -eq $row) { return '(none)' }
    return "$($row.Version) $($row.LockState)"
}

function Get-Cycle {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, WireState, StationId, IFNULL(UpperId, '') AS UpperId FROM ChargingCycles")
}

function Get-SnapshotRequestsSeen {
    @($onboard.Snapshot().body.wire |
        Where-Object { $_.direction -eq 'in' -and $_.messageType -eq 'SafetyStateSnapshotRequested' }).Count
}

# --- 0. 前置：桩登记到假 RIoT 与名册，路网起来，合成车载端应答快照请求 ----------------------------------------------

$null = $onboard.Command('Put', 'policy', @{ answerSafetySnapshotRequests = $true })
$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{
        mapId = $Context.MapId; stationId = $charger; enterExitStationId = $enterExit; expandDeparture = $true
        chargeIntervalSeconds = 1; chargePercentPerInterval = 5
    })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet @($vehicleKey) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 charging-door-open-at-handshake-then-locked: station $charger"
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

$handshake = Get-LatestSnapshotSlot1
$assertions.Add(
    'L2-DOL-01',
    '前提：服务端收到的握手快照里 1 号仓是 UNLOCKED（会话在仓门开着时建立）',
    ($handshake -like '* UNLOCKED'),
    '<version> UNLOCKED',
    $handshake)

# --- 1. 低电、门真开着：不分桩 -------------------------------------------------------------------------------------

$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.GateStationRiotId; battery = 25
})

# 另等二十秒：门开着的这段时间里会不会分到桩（它不能）。
$openCycles = Wait-L2ConditionOrLast -Description 'a charger was allocated while slot 1 stood open (it must not)' `
    -Journal $journal -Criterion 'no-cycle-while-open' -TimeoutSeconds 20 `
    -Probe { Get-Count 'SELECT COUNT(*) AS N FROM ChargingCycles' } `
    -Until { param($v) $v -gt 0 }
$assertions.Add(
    'L2-DOL-02',
    '电量 25（低于强制线 30）、1 号仓开着：另等二十秒，一直没有充电周期（门锁没证明仍不出发）',
    ($openCycles -eq 0),
    '0',
    [string]$openCycles)

# --- 2. 门锁上：服务端要新快照，车去充电 ---------------------------------------------------------------------------

$requestsBefore = Get-SnapshotRequestsSeen
$null = $onboard.Command('Put', 'safety', @{ slotLocks = @{ '1' = 'LOCKED' }; affectedSlots = @(1, 2, 3, 4, 5, 6, 7, 8) })

$after = Wait-L2ConditionOrLast -Description 'the charge order to 211 was confirmed after slot 1 was locked' `
    -Journal $journal -Criterion 'charge-order-confirmed-after-lock' -TimeoutSeconds 90 `
    -Probe {
        $cycle = Get-Cycle
        $state = if ($null -eq $cycle) { '(none)' } else { "$($cycle.WireState) $($cycle.StationId) $(if ([string]$cycle.UpperId -ne '') { 'ordered' } else { 'unordered' })" }
        "$state | $(Get-LatestSnapshotSlot1)"
    } `
    -Until { param($v) $v.StartsWith("EN_ROUTE $charger ordered |") }
$requestsAfter = Get-SnapshotRequestsSeen
$journal.Observe('snapshot-requests-after-lock', ($requestsAfter - $requestsBefore), @{ before = $requestsBefore; after = $requestsAfter })
$assertions.Add(
    'L2-DOL-03',
    '1 号仓锁上（SafetyStateChanged 不带逐仓状态）：服务端要来一张新快照、1 号仓 LOCKED，随后分到 211、开往 211 的单已确认',
    ($after -like "EN_ROUTE $charger ordered | * LOCKED"),
    "EN_ROUTE $charger ordered | <version> LOCKED",
    $after)
