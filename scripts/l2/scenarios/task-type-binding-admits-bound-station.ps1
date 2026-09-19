#Requires -Version 7

<#
绑定完备时放行，终点按绑定解析（control-server#160，规格 8.3 批次 6 机制判据 ②，REQ-0334、REQ-0344）。

边车把本图 WIRE_TO_GATE 绑到站点 220「关卡2」，关卡 210 仍在地图上。一条 WIRE_TO_GATE 需求：

- 被受理，关卡腿的订单目标站是 220，旅程走完两段；
- 受理事务里冻结的规则版本、绑定集版本等于当前生效版本，冻结的卸货站（FrozenDemandStations 的 Dropoff）是 220。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection
$boundStation = 220

function New-Demand([string]$label) {
    $guid = [guid]::NewGuid()
    # MesIngest 报不带连字符的 demandId，服务端入口处归一化成规范 UUID：替身按前者发，断言按后者查。
    return [pscustomobject]@{
        Label  = $label
        Wire   = $guid.ToString('N')
        Id     = $guid.ToString('D')
        Sublot = "L2-SUBLOT-$label-$($Context.RunId)"
    }
}

function Publish-Demand([object]$demand, [string]$workType, [string]$area, [string]$eqp) {
    $journal.Note("Publishing demand $($demand.Wire) ($workType, AREA $area) to the fake MesIngest catalog.")
    $null = $mes.Command('Put', "demands/$($demand.Wire)", @{
        sublot      = $demand.Sublot
        workType    = $workType
        area        = $area
        eqp         = $eqp
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

function Get-Backlog([string]$demandId) {
    $rows = @(Invoke-L2Query -Connection $connection -Sql "SELECT ReasonCode, AcceptedAt FROM JourneyBacklog WHERE DemandId = '$demandId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Count([string]$sql) {
    return [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N
}

function Get-AcceptanceRowCount([string]$demandId) {
    return Get-Count @"
SELECT (SELECT COUNT(*) FROM AcceptedDemands WHERE DemandId = '$demandId')
     + (SELECT COUNT(*) FROM JourneyRuntimes WHERE DemandId = '$demandId')
     + (SELECT COUNT(*) FROM OrderIntents WHERE DemandId = '$demandId') AS N
"@
}

function Get-Stage([string]$demandId) {
    $rows = @(Invoke-L2Query -Connection $connection -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$demandId'")
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

function Get-Intent([string]$demandId, [string]$purpose) {
    $rows = @(Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status, DestinationStationId FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Move-VehicleTo([object]$intent, [int]$stationRiotId, [string]$where) {
    $journal.Note("Vehicle departs for $where.")
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey      = $Context.VehicleKey
        procState       = 'RUNNING'
        movementState   = 'MT_RUNNING'
        speed           = 0.8
        processingOrder = $true
        orderTaskId     = $intent.OrderId
    })
    $journal.Note("Vehicle arrives at $where and comes to rest.")
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey       = $Context.VehicleKey
        procState        = 'IDLE'
        movementState    = 'MT_FINISHED'
        speed            = 0
        currentPosition  = $stationRiotId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })
}

# --- 1. 一条 WIRE_TO_GATE 需求：受理、取货 ---------------------------------------------------------

$demand = New-Demand 'GATE2'
Publish-Demand $demand 'WIRE_TO_GATE' 'N1-3' 'EQP-L2-N13'

$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage $demand.Id } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-Intent $demand.Id 'TO_PICKUP'; if ($row -and [string]$row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }

$freeze = @(Invoke-L2Query -Connection $connection -Sql @"
SELECT (SELECT FrozenVersion FROM ConfigurationConsumerBindings
         WHERE ConsumerKind = 'TransportDemand' AND ConsumerId = '$($demand.Id)' AND ObjectKind = 'TaskTypeStationRule') AS RuleVersion,
       (SELECT FrozenVersion FROM ConfigurationConsumerBindings
         WHERE ConsumerKind = 'TransportDemand' AND ConsumerId = '$($demand.Id)' AND ObjectKind = 'PublicStationBinding'
           AND ObjectId = 'map-25') AS BindingSetVersion,
       (SELECT RuleVersion FROM TaskTypeStationBindingSetVersions v
         JOIN TaskTypeStationActiveBindingSets a ON a.MapId = v.MapId AND a.ActiveVersion = v.Version
         WHERE a.MapId = 25) AS ActiveRuleVersion,
       (SELECT ActiveVersion FROM TaskTypeStationActiveBindingSets WHERE MapId = 25) AS ActiveBindingSetVersion,
       (SELECT StationId FROM FrozenDemandStations WHERE DemandId = '$($demand.Id)' AND Role = 'Dropoff') AS DropoffStationId
"@)[0]
$assertions.Add(
    'L2-TTAB-01', '受理冻结的规则版本、绑定集版本等于当前生效版本，冻结的卸货站是 220',
    ($null -ne $freeze.RuleVersion -and [long]$freeze.RuleVersion -eq [long]$freeze.ActiveRuleVersion -and
        $null -ne $freeze.BindingSetVersion -and [long]$freeze.BindingSetVersion -eq [long]$freeze.ActiveBindingSetVersion -and
        [int]$freeze.DropoffStationId -eq $boundStation),
    "rule v$($freeze.ActiveRuleVersion) / map-25 bindings v$($freeze.ActiveBindingSetVersion) / dropoff $boundStation",
    "rule v$($freeze.RuleVersion) / map-25 bindings v$($freeze.BindingSetVersion) / dropoff $($freeze.DropoffStationId)")

Move-VehicleTo $pickupIntent $Context.PickupStationRiotId 'the pickup station'

# --- 2. 关卡腿开去绑定的 220，旅程走完 --------------------------------------------------------------

$null = Wait-L2Condition -Description 'the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage $demand.Id } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$gateIntent = Wait-L2Condition -Description 'the TO_GATE intent was confirmed' `
    -Journal $journal -Criterion 'to-gate-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-Intent $demand.Id 'TO_GATE'; if ($row -and [string]$row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-TTAB-02', '关卡腿的订单目标站是绑定的 220「关卡2」，不是地图上仍在的 210',
    ([int]$gateIntent.DestinationStationId -eq $boundStation),
    [string]$boundStation, [string]$gateIntent.DestinationStationId)

Move-VehicleTo $gateIntent $boundStation 'the bound gate station 220'
$stage = Wait-L2Condition -Description 'the journey completed at the bound station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage $demand.Id } -Until { param($v) $v -eq 'Completed' }
$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-TTAB-03', '旅程走完：Completed，全程两条 RIoT 单',
    ($stage -eq 'Completed' -and $riotOrders.Count -eq 2),
    'Completed / 2 RIoT orders', "$stage / $($riotOrders.Count) RIoT orders")

$journal.Note('WIRE_TO_GATE 按绑定开去 220，冻结版本等于受理时的生效版本。')
