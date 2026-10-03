#Requires -Version 7

<#
一个桩、三台车的「充满让桩」（批次9-07，control-server#405；REQ-0173、REQ-0281、REQ-0290；规格 8.3、8.5）：A 在 211 上充到 COMPLETE，
B、C 低电在队里等桩；来一条需求，只有 A 能接（B、C 低于强制充电线）——A 被派走、离桩之后桩释放，电量最低的 C 取得预占。

**装置**：三台车停在关卡 210。站 211 登记成假 RIoT 的充电桩（每秒涨 5%），经 FieldOps 导入名册。默认测试策略：强制充电线 30、完成阈值 80。

**顺序**：A 先压到 25，B、C 留在 50，所以只有 A 进队、取得 211；A 的单确认之后才把 B 压到 20、C 压到 15——这样谁先拿到桩不靠名册次序。

**第一个事实**：A 在 211 上 COMPLETE（用途放开、211 仍是 A 的占用）；B、C 在队里，另等十秒，两台都没有用途占有、站点独占或订单意图
（充满的车不主动腾桩，服务端也不替它腾）。

**第二个事实**：发一条需求。A 仍报 CHARGING，只有放开了「本周期已充满」那一支它才接得了；B、C 低于强制充电线，接不了。需求派给 A、单已确认，
此刻 211 仍是 A 的占用。让 RIoT 执行那张单、A 到机台 12：另等，211 释放（CHARGER_RELEASED_ON_DEPARTURE）之后 C 取得预占，B 仍在队里。

**红证据（缺陷版本）**：把电量判据改回「报 CHARGING 一律不派」。A 充满后永远接不了需求、永远不离桩，B、C 永远等——L2-CFY-03 变红
（「充满的车占桩饿死低电车」，规格 8.5）。
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
$mes = $Context.MesIngest

$charger = 211
$chargerName = '充电点1'
$machine = 12
$a = [pscustomobject]@{ AgvId = $Context.AgvId; VehicleKey = $Context.VehicleKey }
$b = [pscustomobject]@{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
$c = [pscustomobject]@{ AgvId = 'AGV-L2-003'; VehicleKey = 'BROKERX-L2-0003' }
$fleetKeys = [string[]]@($a.VehicleKey, $b.VehicleKey, $c.VehicleKey)

# Counted in SQL: a query with no rows comes back as $null, and @($null).Count is 1.
function Get-Count([string]$sql) { [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N }

function Set-Battery([string]$vehicleKey, [int]$battery) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Context.GateStationRiotId; battery = $battery
    })
}

# Who holds the charger, as one line.
function Get-ChargerHeld {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT State, VehicleKey FROM StationExclusivities WHERE MapId = $($Context.MapId) AND StationId = $charger")
    if ($null -eq $row) { return '(none)' }
    return "$($row.State) $($row.VehicleKey)"
}

# A's one cycle that has not ended.
function Get-CycleOf([string]$vehicleKey) {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, WireState, IFNULL(UpperId, '') AS UpperId FROM ChargingCycles " +
        "WHERE VehicleKey = '$vehicleKey' AND Phase <> 'ENDED'")
}

# What B and C hold, one line: claims, stations and order intents of each.
function Get-QueueFootprint {
    (@($b, $c) | ForEach-Object {
        $key = $_.VehicleKey
        "$($_.AgvId): $(Get-Count "SELECT COUNT(*) AS N FROM VehiclePurposeClaims WHERE VehicleKey = '$key'") claims, " +
            "$(Get-Count "SELECT COUNT(*) AS N FROM StationExclusivities WHERE VehicleKey = '$key'") stations, " +
            "$(Get-Count "SELECT COUNT(*) AS N FROM OrderIntents WHERE VehicleKey = '$key'") intents"
    }) -join '; '
}

# --- 0. 前置：桩登记到假 RIoT 与名册，路网起来 ------------------------------------------------------------------

Set-Battery $a.VehicleKey 50
Set-Battery $b.VehicleKey 50
Set-Battery $c.VehicleKey 50
$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{ mapId = $Context.MapId; stationId = $charger; chargeIntervalSeconds = 1; chargePercentPerInterval = 5 })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet $fleetKeys -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 charging-full-vehicle-yields-charger: station $charger"
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

# --- 1. A 先进队、取得 211；之后 B、C 才落到线下 --------------------------------------------------------------

Set-Battery $a.VehicleKey 25
$upperId = Wait-L2Condition -Description "$($a.AgvId)'s charge order was confirmed by RIoT" `
    -Journal $journal -Criterion 'a-charge-order-confirmed' -TimeoutSeconds 120 `
    -Probe {
        $cycle = Get-CycleOf $a.VehicleKey
        if ($null -eq $cycle -or [string]$cycle.WireState -ne 'EN_ROUTE' -or [string]$cycle.UpperId -eq '') { return $null }
        return [string]$cycle.UpperId
    } `
    -Until { param($v) $null -ne $v }
Set-Battery $b.VehicleKey 20
Set-Battery $c.VehicleKey 15

$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $a.VehicleKey })
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })

$null = Wait-L2Condition -Description "$($a.AgvId)'s cycle is COMPLETE" `
    -Journal $journal -Criterion 'a-complete' -TimeoutSeconds 120 `
    -Probe { [string](Get-CycleOf $a.VehicleKey).WireState } `
    -Until { param($v) $v -eq 'COMPLETE' }

# 另等：充满的 A 不主动腾桩；B、C 在队里什么也拿不到。
$queued = Wait-L2ConditionOrLast -Description 'the charger changed hands or a queued vehicle got something (neither may)' `
    -Journal $journal -Criterion 'full-vehicle-keeps-the-charger' -TimeoutSeconds 10 `
    -Probe { "$(Get-ChargerHeld) | $(Get-QueueFootprint) | $((@($riot.Snapshot().body.orders | Where-Object { $null -ne $_ })).Count) orders" } `
    -Until { param($v) $v -ne "OCCUPIED $($a.VehicleKey) | $($b.AgvId): 0 claims, 0 stations, 0 intents; $($c.AgvId): 0 claims, 0 stations, 0 intents | 1 orders" }
$expectedQueued = "OCCUPIED $($a.VehicleKey) | $($b.AgvId): 0 claims, 0 stations, 0 intents; $($c.AgvId): 0 claims, 0 stations, 0 intents | 1 orders"
$claimA = Read-L2SingleRow -Connection $connection -Sql "SELECT Purpose FROM VehiclePurposeClaims WHERE VehicleKey = '$($a.VehicleKey)'"
$assertions.Add(
    'L2-CFY-01',
    'A 在 211 上充到 COMPLETE：充电用途放开、211 仍是 A 的占用；B、C 在队里，另等十秒，两台都没有用途、站点独占或订单意图，RIoT 上仍只有 A 那一张充电单',
    ($queued -eq $expectedQueued -and $null -eq $claimA),
    "$expectedQueued / A no claim",
    "$queued / A claim $(${claimA}?.Purpose)")

# --- 2. 只有 A 接得了：需求派给 A；离桩之后 211 给电量最低的 C ----------------------------------------------------

$guid = [guid]::NewGuid()
$demand = $guid.ToString('D')
$journal.Note("Publishing demand $($guid.ToString('N')).")
$null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
    sublot = "L2-CFY-$($Context.RunId)"; area = 'N1-3'; eqp = 'EQP-L2-CFY-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})
$pickup = Wait-L2ConditionOrLast -Description 'the demand went to the full vehicle and its pickup order was confirmed' `
    -Journal $journal -Criterion 'full-vehicle-took-the-demand' -TimeoutSeconds 60 `
    -Probe {
        Read-L2SingleRow -Connection $connection -Sql (
            "SELECT UpperId, IFNULL(OrderId, '') AS OrderId, Status, VehicleKey FROM OrderIntents " +
            "WHERE DemandId = '$demand' AND Purpose = 'TO_PICKUP'")
    } `
    -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
$heldAtDispatch = Get-ChargerHeld
$assertions.Add(
    'L2-CFY-02',
    '需求派给了充满的 A（仍报 CHARGING；B、C 低于强制充电线），开往取货站的单已确认；此刻 211 仍是 A 的占用',
    ($null -ne $pickup -and [string]$pickup.VehicleKey -eq $a.VehicleKey -and [string]$pickup.Status -eq 'CONFIRMED' -and
        $heldAtDispatch -eq "OCCUPIED $($a.VehicleKey)"),
    "$($a.VehicleKey) CONFIRMED / OCCUPIED $($a.VehicleKey)",
    "$(${pickup}?.VehicleKey) $(${pickup}?.Status) / $heldAtDispatch")

if ($null -ne $pickup -and [string]$pickup.Status -eq 'CONFIRMED') {
    $null = $riot.Command('Put', "orders/$($pickup.UpperId)", @{ orderState = 3; executeVehicleKey = $a.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $a.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $machine; processingOrder = $true; orderTaskId = [string]$pickup.OrderId
    })
}

$handedOver = Wait-L2ConditionOrLast -Description 'the charger went to the lowest queued vehicle after the full one left' `
    -Journal $journal -Criterion 'charger-yielded' -TimeoutSeconds 60 `
    -Probe {
        $release = Read-L2SingleRow -Connection $connection -Sql (
            "SELECT IFNULL(r.ReleaseReason, '') AS Reason FROM StationExclusivityRecords r " +
            "WHERE r.MapId = $($Context.MapId) AND r.StationId = $charger AND r.VehicleKey = '$($a.VehicleKey)'")
        "$(Get-ChargerHeld) | A released $(${release}?.Reason) | B $(Get-Count "SELECT COUNT(*) AS N FROM VehiclePurposeClaims WHERE VehicleKey = '$($b.VehicleKey)'") claims"
    } `
    -Until { param($v) $v.StartsWith("RESERVED $($c.VehicleKey)") }
$assertions.Add(
    'L2-CFY-03',
    'A 离桩（到了机台 12、不再充电）之后 211 释放（CHARGER_RELEASED_ON_DEPARTURE），电量最低的 C 取得预占；B 仍在队里',
    ($handedOver -eq "RESERVED $($c.VehicleKey) | A released CHARGER_RELEASED_ON_DEPARTURE | B 0 claims"),
    "RESERVED $($c.VehicleKey) | A released CHARGER_RELEASED_ON_DEPARTURE | B 0 claims",
    $handedOver)

$journal.Note('一桩三车的「充满让桩」：充满的 A 接活离桩，桩释放后给了队里电量最低的 C。')
