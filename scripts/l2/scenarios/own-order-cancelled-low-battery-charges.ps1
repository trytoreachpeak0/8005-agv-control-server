#Requires -Version 7

<#
本服务端自建的取货单在 RIoT 被取消，重建到点时车已低于强制充电线、车上没货：不重建，需求放回积压，车被派去充电（control-server#770）。

10-18 生产：agv01 的取货单被人在 RIoT 取消，服务端按 control-server#318 自动重建，建单时电量 9%、强制充电线 40%，车照样被派回取货站。
新派车受 BatteryEligibility 管，重建不受。本场景接起三段：#318 的取消与重建记录、#770 的电量闸、释放服务与批次 9 的充电分配。

**装置**：单车停在关卡附近，充电桩 211 登记到假 RIoT 与名册，路网起来（setup 文件写了为什么）。编排器的默认测试策略：强制充电线 30。

**一、派往取货站、低电、单被取消**：一条需求派给这辆车，取货单 RIoT 确认、车在跑。电量掉到 25，接着人在 RIoT 取消这张单、车停住。
旅程先写 ORDER_ENDED_WITHOUT_ARRIVAL（#318 的过渡码）。

**二、到点不重建（L2-OCB-01）**：重建记录停在 STOPPED / MANDATORY_CHARGE_REQUIRED（等待原因写明读数与线），这辆车、这条需求没有第二张
移动意图，RIoT 上也没有服务端为它新建的单。负判据有界：等的是记录停下；记录停下之后另转几轮再数单。

**三、需求放回（L2-OCB-02）**：释放服务把需求放回待派（RELEASED_FOR_REDISPATCH），仍 Accepted、不抑制；旅程 Completed。没有任何订单
命令——被取消的那张单已经终结，不必再取消。

**四、进充电队（L2-OCB-03）**：车没有用途，被分到 211，充电周期 EN_ROUTE、充电单被 RIoT 确认，车持有 CHARGING 用途。

读取纪律（scripts/l2/README.md 第 14 条）：记录停下与旅程码在同一轮的两次保存里，旅程码后存，等后存的那样再读记录；释放在另一个服务里，
等旅程 Completed 再读归属与积压（释放一次提交）；充电周期按 Wait-L2ConditionOrLast 等确认，读不到就把最后一次的样子交给判据。

**红证据**：修复之前（fp/v2-impl@c0389b68）重建不查电量，到点建出一张 W2G- 新取货单，记录 REBUILT，L2-OCB-01 红；需求挂在车上不放，02、03
记为未到达。L1 同一处的红在 OwnOrderRebuildBatteryGateTests。
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
$connection = $Context.Connection
$riot = $Context.Riot
$mes = $Context.MesIngest

$charger = 211
$chargerName = '充电点1'
$enterExit = 212
$lowBattery = 25
$vehicleKey = [string]$Context.VehicleKey
$all = @('L2-OCB-01', 'L2-OCB-02', 'L2-OCB-03')

function Get-Count([string]$Sql) { return [int](Invoke-L2Query -Connection $connection -Sql $Sql)[0].N }

function Get-Claim {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT Purpose, JourneyId FROM VehiclePurposeClaims WHERE VehicleKey = '$vehicleKey'")
    if ($null -eq $row) { return '(none)' }
    return "$($row.Purpose) $($row.JourneyId)"
}

function Get-Cycle {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, StationId, WireState, IFNULL(UpperId, '') AS UpperId, IFNULL(OrderConfirmedAt, '') AS OrderConfirmedAt " +
        "FROM ChargingCycles WHERE VehicleKey = '$vehicleKey'")
}

# --- 0. 充电桩登记、路网拉起来 -------------------------------------------------------------------------------------

$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{
        mapId = $Context.MapId; stationId = $charger; enterExitStationId = $enterExit; expandDeparture = $false
        chargeIntervalSeconds = 1; chargePercentPerInterval = 2
    })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet @($vehicleKey) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 own-order-cancelled-low-battery-charges: station $charger"
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

# --- 一、派往取货站、低电、单被取消 -----------------------------------------------------------------------------------

$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')
$journal.Note("Publishing demand $($demandGuid.ToString('N')) (area N1-3).")
$null = $mes.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = "L2-OCB-$($Context.RunId)"; area = 'N1-3'
    eqp = 'EQP-L2-OCB-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})

$first = Wait-L2Condition -Description 'the vehicle took the demand and set off to its pickup' `
    -Journal $journal -Criterion 'first-journey' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql (
            "SELECT r.JourneyId, r.Stage, r.PickupUpperId, r.ChargingPolicyVersion, o.OrderId, o.Status FROM JourneyRuntimes r " +
            "JOIN OrderIntents o ON o.UpperId = r.PickupUpperId WHERE r.DemandId = '$demandId'")
        if ($rows.Count -eq 0) { return $null }
        return $rows[0]
    } `
    -Until { param($v) $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' -and [string]$v.Status -eq 'CONFIRMED' }
$journeyId = [string]$first.JourneyId
$pickupUpperId = [string]$first.PickupUpperId
$journal.Note("Journey $journeyId, pickup order $pickupUpperId, charging policy version $($first.ChargingPolicyVersion).")

$null = $riot.Command('Put', "orders/$pickupUpperId", @{ orderState = 3; executeVehicleKey = $vehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $vehicleKey; procState = 'PROCESSING_ORDER'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = [string]$first.OrderId; currentPosition = $Context.GateStationRiotId
})
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal

$journal.Note("The battery drops to $lowBattery, below the default test policy's mandatory charge line of 30; a person cancels $pickupUpperId in RIoT.")
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $vehicleKey; procState = 'PROCESSING_ORDER'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = [string]$first.OrderId; currentPosition = $Context.GateStationRiotId; battery = $lowBattery
})
$null = $riot.Command('Put', "orders/$pickupUpperId", @{ orderState = 2 })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    processingOrder = $false; clearOrderTaskId = $true; currentPosition = $Context.GateStationRiotId; battery = $lowBattery
})

# --- 二、到点不重建 -------------------------------------------------------------------------------------------------

# 记录停下（一次保存）先于旅程码（之后一次保存）：等旅程码，再读记录。
$named = Wait-L2ConditionOrLast -Description 'the rebuild stopped for the mandatory charge line' `
    -Journal $journal -Criterion 'rebuild-stopped-for-charge' -TimeoutSeconds 90 `
    -Probe {
        Read-L2SingleRow -Connection $connection -Sql (
            "SELECT Stage, IFNULL(BlockReasonCode, '') AS BlockReasonCode FROM JourneyRuntimes WHERE JourneyId = '$journeyId'")
    } `
    -Until { param($v) $null -ne $v -and ([string]$v.BlockReasonCode -eq 'OWN_ORDER_REBUILD_MANDATORY_CHARGE' -or [string]$v.Stage -eq 'Completed') }
$record = Read-L2SingleRow -Connection $connection -Sql (
    "SELECT State, IFNULL(StoppedReason, '') AS StoppedReason, IFNULL(WaitingReason, '') AS WaitingReason, NewUpperId " +
    "FROM OwnOrderRebuilds WHERE EndedUpperId = '$pickupUpperId'")
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$intents = Get-Count "SELECT COUNT(*) AS N FROM OrderIntents WHERE DemandId = '$demandId'"
$riotRebuilt = @($riot.Snapshot().body.orders | Where-Object { $null -ne $_ -and $null -ne $record -and [string]$_.upperId -eq [string]$record.NewUpperId }).Count
$stoppedOk = $null -ne $record -and [string]$record.State -eq 'STOPPED' -and [string]$record.StoppedReason -eq 'MANDATORY_CHARGE_REQUIRED' -and
    [string]$record.WaitingReason -eq "BATTERY_$($lowBattery)_BELOW_30" -and $intents -eq 1 -and $riotRebuilt -eq 0
$assertions.Add(
    'L2-OCB-01',
    '到点不重建：重建记录停在 STOPPED / MANDATORY_CHARGE_REQUIRED（读数与线写在等待原因里）；这条需求仍只有一张移动意图，RIoT 上没有重建出来的单',
    $stoppedOk,
    "STOPPED / MANDATORY_CHARGE_REQUIRED / BATTERY_$($lowBattery)_BELOW_30 / 1 张意图 / RIoT 0 张",
    $(if ($null -eq $record) { "(没有重建记录) / 旅程 $(${named}?.Stage) $(${named}?.BlockReasonCode)" } else {
        "$($record.State) / $($record.StoppedReason) / $($record.WaitingReason) / $intents 张意图 / RIoT $riotRebuilt 张" }))
if (-not $stoppedOk) {
    Add-L2RealNotReached $assertions @($all | Where-Object { $_ -ne 'L2-OCB-01' }) '重建没有因强制充电而停下，后面几条的前提不成立'
    return
}

# --- 三、需求放回 ---------------------------------------------------------------------------------------------------

# 释放一次提交：旅程收尾、归属移除、积压退回同在一个事务里。等旅程 Completed 再读其余几样。
$closed = Wait-L2ConditionOrLast -Description 'the release service released the demand and closed the journey' `
    -Journal $journal -Criterion 'released' -TimeoutSeconds 60 `
    -Probe {
        Read-L2SingleRow -Connection $connection -Sql (
            "SELECT Stage, IFNULL(BlockReasonCode, '') AS BlockReasonCode FROM JourneyRuntimes WHERE JourneyId = '$journeyId'")
    } `
    -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'Completed' }
$membership = Read-L2SingleRow -Connection $connection -Sql (
    "SELECT IFNULL(RemovalReason, '') AS RemovalReason FROM JourneyDemands WHERE DemandId = '$demandId' AND JourneyId = '$journeyId'")
$status = [string](Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'")[0].Status
$suppressions = Get-Count "SELECT COUNT(*) AS N FROM TransportDemandSuppressions WHERE DemandId = '$demandId'"
$audit = Get-Count 'SELECT COUNT(*) AS N FROM RiotOrderCommandAudit'
$releasedOk = $null -ne $closed -and [string]$closed.Stage -eq 'Completed' -and [string]$closed.BlockReasonCode -eq 'RELEASED_FOR_REDISPATCH' -and
    $null -ne $membership -and [string]$membership.RemovalReason -eq 'RELEASED_FOR_REDISPATCH' -and $status -eq 'Accepted' -and
    $suppressions -eq 0 -and $audit -eq 0
$assertions.Add(
    'L2-OCB-02',
    '需求放回：旅程 Completed / RELEASED_FOR_REDISPATCH，归属以 RELEASED_FOR_REDISPATCH 移除，需求仍 Accepted、不抑制；没有任何订单命令',
    $releasedOk,
    'Completed/RELEASED_FOR_REDISPATCH, RELEASED_FOR_REDISPATCH/Accepted, 抑制 0, 命令 0',
    "$(${closed}?.Stage)/$(${closed}?.BlockReasonCode), $(${membership}?.RemovalReason)/$status, 抑制 $suppressions, 命令 $audit")
if (-not $releasedOk) {
    Add-L2RealNotReached $assertions @('L2-OCB-03') '需求没有放回、旅程没有收尾，车仍挂着用途，进不了充电队'
    return
}

# --- 四、进充电队 ---------------------------------------------------------------------------------------------------

$cycle = Wait-L2ConditionOrLast -Description "the vehicle's charging cycle was allocated and its order confirmed" -Journal $journal `
    -Criterion 'charge-order-confirmed' -TimeoutSeconds 120 -Probe { Get-Cycle } `
    -Until { param($v) $null -ne $v -and [string]$v.WireState -eq 'EN_ROUTE' -and [string]$v.UpperId -ne '' -and [string]$v.OrderConfirmedAt -ne '' }
$claim = Get-Claim
$pickups = Get-Count "SELECT COUNT(*) AS N FROM OrderIntents WHERE VehicleKey = '$vehicleKey' AND Purpose = 'TO_PICKUP'"
$assertions.Add(
    'L2-OCB-03',
    '车进了充电队：分到 211，周期 EN_ROUTE、充电单被 RIoT 确认，车持有这一趟的 CHARGING 用途；这辆车没有再被派往任何取货站',
    ($null -ne $cycle -and [string]$cycle.WireState -eq 'EN_ROUTE' -and [int]$cycle.StationId -eq $charger -and
        [string]$cycle.OrderConfirmedAt -ne '' -and $claim -eq "CHARGING $($cycle.JourneyId)" -and $pickups -eq 1),
    "EN_ROUTE @$charger confirmed / CHARGING <cycle journey> / 取货单 1 张",
    $(if ($null -eq $cycle) { "(no cycle) / $claim / 取货单 $pickups 张" } else {
        "$($cycle.WireState) @$($cycle.StationId) confirmed '$($cycle.OrderConfirmedAt)' / $claim / 取货单 $pickups 张" }))

$journal.Note('自建取货单被取消、重建到点时低电且车上没货：不重建，需求放回，车被派去充电。')
