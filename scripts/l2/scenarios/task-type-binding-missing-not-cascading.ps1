#Requires -Version 7

<#
缺绑定只停该任务类型、不连带（control-server#160，规格 8.3 批次 6 机制判据 ①，REQ-0335、REQ-0342）。

本图需求集合只含 WIRE_TO_GATE 且已绑定（编排器默认前置装的预置配置：map 25、WIRE_TO_GATE → 210「关卡」）。
假 MesIngest 同时给两条需求：

- STAGING_TO_WIRE（AREA N1-7）：规则表认识它，出厂 allowedWorkTypes 也放行它，但本图没有它的绑定。它不受理，
  JourneyBacklog 的原因是 TASK_TYPE_BINDING_MISSING，/api/dashboard/dispatch-backlog 把它连同中文说明列出来；
  没有受理行、旅程、订单意图，也不形成结构性派车阻断——配置造成的不投运，不是故障。
- WIRE_TO_GATE（AREA N1-3）：同一次运行内被受理，走完取货与关卡两段——另一个任务类型缺绑定不连带它。

两条的 AREA 不同，免得 REQ-0187 的跨任务类型唯一性（control-server#160 第 10 条）先把 WIRE_TO_GATE 挡掉。
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
$backlogEndpoint = "http://127.0.0.1:$($Context.HealthPort)/api/dashboard/dispatch-backlog"

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

# --- 1. 两条需求同时上：未绑定的 STAGING_TO_WIRE 先放，已绑定的 WIRE_TO_GATE 随后 ------------------------

$unbound = New-Demand 'STAGING'
$bound = New-Demand 'GATE'
Publish-Demand $unbound 'STAGING_TO_WIRE' 'N1-7' 'EQP-L2-N17'
Publish-Demand $bound 'WIRE_TO_GATE' 'N1-3' 'EQP-L2-N13'

$null = Wait-L2Condition -Description 'the bound WIRE_TO_GATE demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage $bound.Id } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$null = Wait-L2Condition -Description 'the unbound STAGING_TO_WIRE demand was judged and written to the backlog' `
    -Journal $journal -Criterion 'backlog-row' -TimeoutSeconds 60 `
    -Probe { $row = Get-Backlog $unbound.Id; if ($row) { [string]$row.ReasonCode } else { $null } } `
    -Until { param($v) -not [string]::IsNullOrEmpty($v) }

# --- 2. WIRE_TO_GATE 同一次运行内走完两段 --------------------------------------------------------

$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-Intent $bound.Id 'TO_PICKUP'; if ($row -and [string]$row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }
Move-VehicleTo $pickupIntent $Context.PickupStationRiotId 'the pickup station'

$null = Wait-L2Condition -Description 'the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage $bound.Id } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$gateIntent = Wait-L2Condition -Description 'the TO_GATE intent was confirmed' `
    -Journal $journal -Criterion 'to-gate-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-Intent $bound.Id 'TO_GATE'; if ($row -and [string]$row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }
Move-VehicleTo $gateIntent $Context.GateStationRiotId 'the gate'

$stage = Wait-L2Condition -Description 'the WIRE_TO_GATE journey completed at the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage $bound.Id } -Until { param($v) $v -eq 'Completed' }
$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-TTBM-01', 'WIRE_TO_GATE 在同一次运行内被受理并走完两段：另一个任务类型缺绑定不连带它',
    ($stage -eq 'Completed' -and [int]$gateIntent.DestinationStationId -eq $Context.GateStationRiotId -and $riotOrders.Count -eq 2),
    "Completed / gate leg to $($Context.GateStationRiotId) / 2 RIoT orders",
    "$stage / gate leg to $($gateIntent.DestinationStationId) / $($riotOrders.Count) RIoT orders")

# --- 3. STAGING_TO_WIRE 始终不受理，原因是缺绑定 --------------------------------------------------

# 车空下来之后再让派车循环转几圈：它有机会被派的时候仍然不受理，原因仍是那一个。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$unboundBacklog = Get-Backlog $unbound.Id
$assertions.Add(
    'L2-TTBM-02', 'STAGING_TO_WIRE 未受理：积压原因 TASK_TYPE_BINDING_MISSING，没有受理行、旅程、订单意图',
    ([string]$unboundBacklog.ReasonCode -eq 'TASK_TYPE_BINDING_MISSING' -and
        [string]::IsNullOrEmpty([string]$unboundBacklog.AcceptedAt) -and
        (Get-AcceptanceRowCount $unbound.Id) -eq 0),
    'TASK_TYPE_BINDING_MISSING / not accepted / 0 rows',
    "$($unboundBacklog.ReasonCode) / AcceptedAt=$($unboundBacklog.AcceptedAt) / $(Get-AcceptanceRowCount $unbound.Id) rows")

$backlogFact = Invoke-RestMethod -Uri $backlogEndpoint -NoProxy -TimeoutSec 10
$row = @($backlogFact.backlog | Where-Object { [string]$_.demandId -eq $unbound.Id })
$assertions.Add(
    'L2-TTBM-03', '/api/dashboard/dispatch-backlog 列出这条 STAGING_TO_WIRE 需求，原因码 TASK_TYPE_BINDING_MISSING 带中文说明',
    ($row.Count -eq 1 -and [string]$row[0].reasonCode -eq 'TASK_TYPE_BINDING_MISSING' -and
        [string]$row[0].reasonDescription -match '\p{IsCJKUnifiedIdeographs}'),
    'one row / TASK_TYPE_BINDING_MISSING / Chinese description',
    $(if ($row.Count -ne 1) { "$($row.Count) rows" } else { "$($row[0].reasonCode) / $($row[0].reasonDescription)" }))

$blocks = Get-Count "SELECT COUNT(*) AS N FROM StructuralDispatchBlocks WHERE DemandId = '$($unbound.Id)'"
$assertions.Add(
    'L2-TTBM-04', 'StructuralDispatchBlocks 没有这条 STAGING_TO_WIRE 需求的行：缺绑定是配置造成的不投运，不是结构性告警',
    ($blocks -eq 0), '0', $blocks)

$journal.Note('缺绑定只停 STAGING_TO_WIRE；WIRE_TO_GATE 同一次运行内受理并走完。')
