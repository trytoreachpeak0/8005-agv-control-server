#Requires -Version 7

<#
待送点缺电、一篮没装：旅程原地收尾，车成空闲车，进充电队列、被派去充电（program#184 第一波集成验证必测清单第 ① 条；
control-server#709 的缺电收尾、#712 的待送点不空闲返回、#713 的收尾原因 CHARGING_REQUIRED，与批次 9 的充电分配接在一起）。

各票自己的 L2 只证了自己那一段：#709/#713 证到「旅程收尾、需求放回」为止，#712 证「待送点上不空闲返回」，充电分配的场景都从
停在关卡的空闲车起步。这三段接起来有一处没人看过：#712 让只接待送的车在待送点上不空闲返回，而充电队排除挂着用途的车
（ChargingAllocator 的 CHARGING_VEHICLE_HAS_PURPOSE）——收尾之后车若被别的用途占住、或被判成「待送点上留着」而不进队，它会在
待送点上一直停到没电。本场景就看这一处。

**装置**：同 staging-only-vehicle-stays-at-staging-origin 两台只接待送的合成车、空闲返回打开、两个等待点；外加充电桩 211（setup 文件写了
为什么）。开局两台车各被空闲返回送到一个等待点，场景把这两趟开完。

**一、缺电收尾**：一条待送需求派给其中一台车（记为 V），V 开到待送点 305，没人扫码。电量压到 25（编排器的默认测试策略：强制充电线 30）。
服务端在待送点上结束这一站：旅程 Completed / STAGING_CHARGING_REQUIRED，空清单的 stopEndedReason 是 CHARGING_REQUIRED；需求放回待派
（RELEASED_FOR_REDISPATCH），仍 Accepted、不抑制（L2-SLC-01）。

**二、空闲、不空闲返回、不再接这条需求**：收尾之后 V 没有 IDLE_RETURN 用途占有、没有开往等待点的单（control-server#712）；放回的需求
不派给 V（缺电的车不接运输，mandatory-charge-vehicle-takes-no-transport 的规则），另一台车可以接（L2-SLC-02）。

**三、进充电队、被派去充电**：V 被分到 211，充电周期 EN_ROUTE、充电单被 RIoT 确认、V 持有 CHARGING 用途（L2-SLC-03）；单跑完，
V 在 211 上 CHARGING，211 是这一趟的占用（L2-SLC-04）。

**红证据**（缺陷版本）：去掉 #709 的缺电收尾，V 一直挂在待送点的旅程上，L2-SLC-01 等不到收尾、L2-SLC-03 等不到周期；让充电队把
「停在待送点、只接待送」的车当作有用途，L2-SLC-03 变红。

**探针不取闭包**，理由见 CargoHoldingCommon.ps1 开头。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SingleRow.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Chargers.psm1') -Force
# Add-L2RealNotReached only: it marks criteria a failed premise left unreached, and has nothing of the real rig in it.
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection

$staging = 305
$charger = 211
$chargerName = '充电点1'
$enterExit = 212
$lowBattery = 25
$otherKey = 'BROKERX-L2-0002'
$all = @('L2-SLC-01', 'L2-SLC-02', 'L2-SLC-03', 'L2-SLC-04')

# 每台车的合成对端，按 AgvId 认（OnboardPeers 的每一项是 Name／Port／AgvId／Double）。
function Get-Peer([string]$AgvId) { return ($Context.OnboardPeers | Where-Object { $_.AgvId -eq $AgvId }).Double }
$peerByKey = @{
    [string]$Context.VehicleKey = Get-Peer $Context.AgvId
    $otherKey                   = Get-Peer 'AGV-L2-002'
}

function Publish-Demand([string]$Suffix) {
    $guid = [guid]::NewGuid()
    $journal.Note("Publishing STAGING_TO_WIRE demand $Suffix $($guid.ToString('N')).")
    $null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot = "L2-SLC-$Suffix-$($Context.RunId)"; area = 'N1-3'; eqp = 'EQP-L2-SLC-01'
        package = 'L2-PACKAGE'; maxBoxCount = 4; workType = 'STAGING_TO_WIRE'
    })
    return $guid.ToString('D')
}

function Get-Count([string]$Sql) { return [int](Invoke-L2Query -Connection $connection -Sql $Sql)[0].Total }

function Get-Runtime([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, VehicleKey, Stage, IFNULL(BlockReasonCode, '') AS BlockReasonCode, ChargingPolicyVersion " +
        "FROM JourneyRuntimes WHERE DemandId = '$DemandId' ORDER BY CreatedAt DESC LIMIT 1")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$Where) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT UpperId, OrderId, Status, VehicleKey, DestinationStationId FROM OrderIntents WHERE $Where ORDER BY CreatedAt DESC LIMIT 1")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Claim([string]$VehicleKey) {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT Purpose, JourneyId FROM VehiclePurposeClaims WHERE VehicleKey = '$VehicleKey'")
    if ($null -eq $row) { return '(none)' }
    return "$($row.Purpose) $($row.JourneyId)"
}

# 这辆车的充电周期（只会有一个）：两行读成一个什么也不等于的替身（L2SingleRow.psm1）。
function Get-Cycle([string]$VehicleKey) {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, StationId, WireState, IFNULL(UpperId, '') AS UpperId, IFNULL(OrderConfirmedAt, '') AS OrderConfirmedAt " +
        "FROM ChargingCycles WHERE VehicleKey = '$VehicleKey'")
}

function Get-ChargerHeld {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT State, VehicleKey, JourneyId FROM StationExclusivities WHERE MapId = $($Context.MapId) AND StationId = $charger")
    if ($null -eq $row) { return '(none)' }
    return "$($row.State) $($row.VehicleKey) $($row.JourneyId)"
}

function Get-Position([string]$VehicleKey) {
    $vehicle = @($riot.Snapshot().body.vehicles) | Where-Object { $null -ne $_ -and [string]$_.deviceKey -eq $VehicleKey } | Select-Object -First 1
    return $vehicle ? [int]$vehicle.currentPosition : $null
}

# RIoT 把单置为执行中并绑车，车动起来，然后停在目的站上，单置为完成（同 staging-only-vehicle-stays-at-staging-origin）。
function Move-Vehicle([string]$VehicleKey, [object]$Intent, [int]$Station) {
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 3; executeVehicleKey = $VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $Intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Station; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 5 })
}

function Wait-Confirmed([string]$Where, [string]$Label) {
    return Wait-L2Condition -Description "the $Label intent was confirmed" -Journal $journal -Criterion "intent-$Label" `
        -TimeoutSeconds 120 -Probe { Get-Intent $Where } -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
}

# --- 0. 充电桩登记、路网拉起来；开局两趟空闲返回开完 ------------------------------------------------------------------

$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{
        mapId = $Context.MapId; stationId = $charger; enterExitStationId = $enterExit; expandDeparture = $false
        chargeIntervalSeconds = 1; chargePercentPerInterval = 2
    })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet @([string]$Context.VehicleKey, $otherKey) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 staging-low-battery-nothing-loaded-charges: station $charger"
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

$journal.Note('Both synthetic peers stop answering sublot entry requests: nobody scans at the staging station.')
foreach ($peer in $peerByKey.Values) { $null = $peer.Command('Put', 'policy', @{ sublot = 'Silent' }) }

foreach ($key in $peerByKey.Keys) {
    $intent = Wait-Confirmed "VehicleKey = '$key' AND Purpose = 'TO_WAITING_POINT'" "idle-return-$key"
    Move-Vehicle $key $intent ([int]$intent.DestinationStationId)
}
$null = Wait-L2Condition -Description 'both opening idle returns converged at their waiting points' -Journal $journal `
    -Criterion 'opening-idle-returns-converged' -TimeoutSeconds 60 `
    -Probe { Get-Count "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE JourneyId LIKE 'idle-return:%' AND Stage = 'Completed'" } `
    -Until { param($v) $v -ge 2 }

# --- 一、V 开到待送点、没人扫码，电量压到强制充电线以下 --------------------------------------------------------------

$demand = Publish-Demand 'A'
$runtime = Wait-L2Condition -Description 'the staging demand was dispatched' -Journal $journal -Criterion 'dispatched' `
    -TimeoutSeconds 120 -Probe { Get-Runtime $demand } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$keyV = [string]$runtime.VehicleKey
$journeyId = [string]$runtime.JourneyId
$journal.Note("V = $keyV, journey $journeyId, charging policy version $($runtime.ChargingPolicyVersion).")
Move-Vehicle $keyV (Wait-Confirmed "DemandId = '$demand' AND Purpose = 'TO_PICKUP'" 'to-pickup') $staging
$null = Wait-L2Condition -Description 'V waits for a sublot at the staging station' -Journal $journal -Criterion 'awaiting-sublot' `
    -TimeoutSeconds 120 -Probe { [string](Get-Runtime $demand).Stage } -Until { param($v) $v -eq 'AwaitingSublot' }
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal

$journal.Note("V's battery drops to $lowBattery, below the default test policy's mandatory charge line of 30.")
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $keyV; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0; currentPosition = $staging; battery = $lowBattery
})

$closed = Wait-L2ConditionOrLast -Description 'the journey closed at the staging station for want of charge' -Journal $journal `
    -Criterion 'closed' -TimeoutSeconds 60 -Probe { Get-Runtime $demand } `
    -Until { param($v) $null -ne $v -and [string]$v.JourneyId -eq $journeyId -and [string]$v.Stage -eq 'Completed' }
$membership = Read-L2SingleRow -Connection $connection -Sql (
    "SELECT IFNULL(RemovalReason, '') AS RemovalReason FROM JourneyDemands WHERE DemandId = '$demand' AND JourneyId = '$journeyId'")
$status = [string](Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demand'")[0].Status
$suppressions = Get-Count "SELECT COUNT(*) AS Total FROM TransportDemandSuppressions WHERE DemandId = '$demand'"
# Invoke-L2Query 把整组行原样交回：先赋值再逐行取（CargoHoldingCommon.ps1 的那条注释）。
$worklistRows = Invoke-L2Query -Connection $connection -Sql (
    "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'CurrentStopWorklistSnapshot'")
$reasons = @(foreach ($row in $worklistRows) { [string](([string]$row.PayloadJson | ConvertFrom-Json -Depth 20).payload.stopEndedReason) })
$closedOk = $null -ne $closed -and [string]$closed.Stage -eq 'Completed' -and [string]$closed.BlockReasonCode -eq 'STAGING_CHARGING_REQUIRED'
$assertions.Add(
    'L2-SLC-01', '待送点缺电、一篮没装：旅程原地收尾（STAGING_CHARGING_REQUIRED），空清单说 CHARGING_REQUIRED；需求放回、仍 Accepted、不抑制',
    ($closedOk -and $reasons -contains 'CHARGING_REQUIRED' -and $null -ne $membership -and
        [string]$membership.RemovalReason -eq 'RELEASED_FOR_REDISPATCH' -and $status -eq 'Accepted' -and $suppressions -eq 0),
    'Completed/STAGING_CHARGING_REQUIRED, CHARGING_REQUIRED, RELEASED_FOR_REDISPATCH/Accepted, 抑制 0',
    "$(${closed}?.Stage)/$(${closed}?.BlockReasonCode), [$($reasons -join ',')], $(${membership}?.RemovalReason)/$status, 抑制 $suppressions")
if (-not $closedOk) {
    Add-L2RealNotReached $assertions @($all | Where-Object { $_ -ne 'L2-SLC-01' }) '旅程没有在待送点因缺电收尾，后面几条的前提不成立'
    return
}

# --- 二、空闲、不空闲返回、缺电的车不再接这条需求 ----------------------------------------------------------------

$idleIntents = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE VehicleKey = '$keyV' AND Purpose = 'TO_WAITING_POINT'"
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$idleIntentsAfter = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE VehicleKey = '$keyV' AND Purpose = 'TO_WAITING_POINT'"
$redispatchedToV = Get-Count ("SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE DemandId = '$demand' AND VehicleKey = '$keyV' " +
    "AND JourneyId <> '$journeyId'")
$redispatchedToV += Get-Count ("SELECT COUNT(*) AS Total FROM JourneyDemands d JOIN JourneyRuntimes r ON r.JourneyId = d.JourneyId " +
    "WHERE d.DemandId = '$demand' AND d.JourneyId <> '$journeyId' AND r.VehicleKey = '$keyV'")
$claimV = Get-Claim $keyV
$assertions.Add(
    'L2-SLC-02', '收尾之后 V 不空闲返回（没有新的开往等待点的单、没有 IDLE_RETURN 用途），放回的需求不派给缺电的 V',
    ($idleIntentsAfter -eq $idleIntents -and -not $claimV.StartsWith('IDLE_RETURN') -and $redispatchedToV -eq 0),
    "开往等待点的单 $idleIntents 张不变 / 无 IDLE_RETURN / 没再派给 V",
    "开往等待点的单 $idleIntents → $idleIntentsAfter / 用途 $claimV / 再派给 V $redispatchedToV 次")

# --- 三、进充电队、被派去充电 ----------------------------------------------------------------------------------

$cycle = Wait-L2ConditionOrLast -Description "V's charging cycle was allocated and its order confirmed" -Journal $journal `
    -Criterion 'charge-order-confirmed' -TimeoutSeconds 120 -Probe { Get-Cycle $keyV } `
    -Until { param($v) $null -ne $v -and [string]$v.WireState -eq 'EN_ROUTE' -and [string]$v.UpperId -ne '' -and [string]$v.OrderConfirmedAt -ne '' }
$claimV = Get-Claim $keyV
$enRoute = $null -ne $cycle -and [string]$cycle.WireState -eq 'EN_ROUTE' -and [int]$cycle.StationId -eq $charger -and
    [string]$cycle.UpperId -ne '' -and [string]$cycle.OrderConfirmedAt -ne ''
$assertions.Add(
    'L2-SLC-03', 'V 进了充电队：分到 211，周期 EN_ROUTE、充电单被 RIoT 确认，V 持有这一趟的 CHARGING 用途',
    ($enRoute -and $claimV -eq "CHARGING $($cycle.JourneyId)"),
    "EN_ROUTE @$charger confirmed / CHARGING <cycle journey>",
    $(if ($null -eq $cycle) { "(no cycle) / $claimV" } else { "$($cycle.WireState) @$($cycle.StationId) upperId '$($cycle.UpperId)' confirmed '$($cycle.OrderConfirmedAt)' / $claimV" }))
if (-not $enRoute) {
    Add-L2RealNotReached $assertions @('L2-SLC-04') 'V 没有被分到充电桩，到桩充电无从谈起'
    return
}

$null = $riot.Command('Put', "orders/$($cycle.UpperId)", @{ orderState = 3; executeVehicleKey = $keyV })
$null = $riot.Command('Put', "orders/$($cycle.UpperId)", @{ orderState = 5 })
$charging = Wait-L2ConditionOrLast -Description 'V is CHARGING at the charger' -Journal $journal -Criterion 'cycle-charging' `
    -TimeoutSeconds 60 -Probe { "$([string](Get-Cycle $keyV).WireState) | $(Get-ChargerHeld) | at $(Get-Position $keyV)" } `
    -Until { param($v) $v -eq "CHARGING | OCCUPIED $keyV $($cycle.JourneyId) | at $charger" }
$assertions.Add(
    'L2-SLC-04', '充电单跑完：V 停在 211 上 CHARGING，211 是这一趟的占用',
    ($charging -eq "CHARGING | OCCUPIED $keyV $($cycle.JourneyId) | at $charger"),
    "CHARGING | OCCUPIED $keyV $($cycle.JourneyId) | at $charger", $charging)

$journal.Note('待送点缺电、一篮没装：旅程原地收尾、需求放回，车不空闲返回、进充电队被派去充电。')
