#Requires -Version 7

<#
到站没人扫码：等待窗口到期，旅程自己终结，车去接下一单。

这是「站点上没有要装的货」的第二条出口。第一条要操作员按下取消（见
load-cancelled-before-sublot），这一条一个人都不需要——没人来扫码本身就是答案。两条走的是同
一个终结动作，所以这里刻意把超时压到十秒，好让终结只可能来自计时器。

反过来的那一半同样重要，也在这里钉住：窗口没到之前旅程一动不动。一个走开两分钟的操作员回来
时，他那一单必须还在。
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

$firstGuid = [guid]::NewGuid()
$firstWire = $firstGuid.ToString('N')
$firstId = $firstGuid.ToString('D')
$secondGuid = [guid]::NewGuid()
$secondWire = $secondGuid.ToString('N')
$secondId = $secondGuid.ToString('D')

function Test-L2Null($value) {
    return ($null -eq $value) -or ($value -is [System.DBNull])
}

function Get-Stage([string]$demandId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

function Get-Runtime([string]$demandId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$demandId, [string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# --- 1. 第一单开到取货站，然后没有人来 -------------------------------------------------------------

$journal.Note('Synthetic peer stops answering sublot entry requests: nobody comes to scan.')
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Silent' })

$journal.Note("Publishing demand $firstWire to the fake MesIngest catalog.")
$null = $mes.Command('Put', "demands/$firstWire", @{
    sublot      = "L2-SUBLOT-$($Context.RunId)-A"
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage $firstId } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { Get-Intent $firstId 'TO_PICKUP' } -Until { param($v) $v -and $v.Status -eq 'CONFIRMED' }

$journal.Note('Vehicle drives to the pickup station and comes to rest.')
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $pickupIntent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $Context.PickupStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 5 })

$stage = Wait-L2Condition -Description 'the arrival was trusted and the stop is waiting for a sublot' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage $firstId } -Until { param($v) $v -eq 'AwaitingSublot' }
$assertions.Add(
    'L2-TO-01', '车到取货站后停在等条码录入这一步',
    ($stage -eq 'AwaitingSublot'),
    'AwaitingSublot', $stage)

$runtime = Get-Runtime $firstId
$assertions.Add(
    'L2-TO-02', '等待起点被单独记下来，不是复用 UpdatedAt',
    (-not (Test-L2Null $runtime.SublotWaitStartedAt)),
    '有值', "SublotWaitStartedAt=$($runtime.SublotWaitStartedAt)")

# --- 2. 窗口没到之前，什么都不许发生 ---------------------------------------------------------------

# 否定判据要有意义，就得先证明运行时确实又转了几轮而仍然没动它。Wait-L2Iterations 等的是假
# RIoT 的地图站点读取，那是「运行时又有一次机会了」唯一的可观测量。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$stage = Get-Stage $firstId
$assertions.Add(
    'L2-TO-03', '窗口未到期时旅程原地不动',
    ($stage -eq 'AwaitingSublot'),
    'AwaitingSublot', $stage)

$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$firstId'"
$assertions.Add(
    'L2-TO-04', '窗口未到期时需求仍是 Accepted',
    ($demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'Accepted'),
    'Accepted', $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' }))

# --- 3. 窗口到期：旅程自己终结 ---------------------------------------------------------------------

$journal.Note('Waiting for the sublot wait window to expire (10 s, set by the sidecar).')
$stage = Wait-L2Condition -Description 'the sublot wait expired and ended the journey' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage $firstId } -Until { param($v) $v -eq 'Completed' }

$runtime = Get-Runtime $firstId
$assertions.Add(
    'L2-TO-05', '旅程以 CANCELLED_BY_SUBLOT_WAIT_TIMEOUT 终结',
    ($stage -eq 'Completed' -and [string]$runtime.BlockReasonCode -eq 'CANCELLED_BY_SUBLOT_WAIT_TIMEOUT'),
    'Completed / CANCELLED_BY_SUBLOT_WAIT_TIMEOUT',
    "$stage / $($runtime.BlockReasonCode)")

$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$firstId'"
$assertions.Add(
    'L2-TO-06', '需求终态为 Cancelled',
    ($demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'Cancelled'),
    'Cancelled', $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' }))

$leaseRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT ReleasedAt FROM VehicleDispatchLeases WHERE DemandId = '$firstId'"
$assertions.Add(
    'L2-TO-07', '车辆调度租约已释放',
    ($leaseRows.Count -eq 1 -and -not (Test-L2Null $leaseRows[0].ReleasedAt)),
    '已释放', $(if ($leaseRows.Count -eq 1) { "ReleasedAt=$($leaseRows[0].ReleasedAt)" } else { '(no lease row)' }))

$requestRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$($runtime.SublotRequestMessageId)'"
$assertions.Add(
    'L2-TO-08', '那条没人回答的条码录入请求被结算了',
    ($requestRows.Count -eq 1 -and -not (Test-L2Null $requestRows[0].AcknowledgedAt)),
    '已结算', $(if ($requestRows.Count -eq 1) { "AcknowledgedAt=$($requestRows[0].AcknowledgedAt)" } else { '(no outbox row)' }))

$operationRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT COUNT(*) AS N FROM StationOperations WHERE DemandId = '$firstId'"
$assertions.Add(
    'L2-TO-09', '超时不下发也不残留任何仓位操作',
    ([int]$operationRows[0].N -eq 0),
    0, [int]$operationRows[0].N)

# 车载端不参与这条出口：没有仓位要清空，就没有恢复流程要走。
$workflowRows = Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM RecoveryWorkflows'
$assertions.Add(
    'L2-TO-10', '超时不开任何恢复流程',
    ([int]$workflowRows[0].N -eq 0),
    0, [int]$workflowRows[0].N)

# --- 4. 车去接下一单，这次有人扫码，一路跑到关卡 ---------------------------------------------------

$journal.Note('Synthetic peer answers again; the next demand must run all the way through.')
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Auto' })
$null = $mes.Command('Put', "demands/$secondWire", @{
    sublot      = "L2-SUBLOT-$($Context.RunId)-B"
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Condition -Description 'the vehicle took the next demand' `
    -Journal $journal -Criterion 'second-journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage $secondId } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$secondPickup = Wait-L2Condition -Description 'the second TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'second-to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { Get-Intent $secondId 'TO_PICKUP' } -Until { param($v) $v -and $v.Status -eq 'CONFIRMED' }

# 车已经站在取货点了，但这是一条新的运单，得重新跑一遍到站判定。
$journal.Note('Vehicle runs the second pickup leg.')
$null = $riot.Command('Put', "orders/$($secondPickup.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $secondPickup.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $Context.PickupStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($secondPickup.UpperId)", @{ orderState = 5 })

$stage = Wait-L2Condition -Description 'the second journey loaded and left for the gate' `
    -Journal $journal -Criterion 'second-journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage $secondId } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$assertions.Add(
    'L2-TO-11', '下一单照常装载并发往关卡',
    ($stage -eq 'AwaitingGateArrival'),
    'AwaitingGateArrival', $stage)

$gateIntent = Get-Intent $secondId 'TO_GATE'
$journal.Note('Vehicle drives to the gate and comes to rest.')
$null = $riot.Command('Put', "orders/$($gateIntent.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $gateIntent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $Context.GateStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($gateIntent.UpperId)", @{ orderState = 5 })

$stage = Wait-L2Condition -Description 'the second journey completed at the gate' `
    -Journal $journal -Criterion 'second-journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage $secondId } -Until { param($v) $v -eq 'Completed' }

$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$secondId'"
$assertions.Add(
    'L2-TO-12', '一次超时之后，下一单仍能走到 Succeeded',
    ($stage -eq 'Completed' -and $demandRows.Count -eq 1 -and
        [string]$demandRows[0].Status -eq 'Succeeded'),
    'Completed / Succeeded',
    "$stage / $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' })")

# 超时那一单只建了取货一条单（它从没去过关卡），第二单建了取货与关卡两条。
$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-TO-13', '全程三条 RIoT 单：超时那单只到取货口，第二单跑完两段',
    ($riotOrders.Count -eq 3),
    3, $riotOrders.Count)

$journal.Note('Scenario finished.')
