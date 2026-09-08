#Requires -Version 7

<#
到站发现没货：操作员在录入条码之前取消，旅程就地终结，车立刻接下一单。

这一幕在 MVP 里一直是断的。协议、车载端按钮和服务端的终结逻辑都齐了，但只覆盖「装货命令已
下发」那一半——车刚到站、条码还没扫的时候，恰恰没有出口，旅程会一直占着取货口。

它测的不是取消本身，是取消之后的闭环：需求置 Cancelled、租约释放、那条还没人回答的
SublotEntryRequested 被结算，以及最要紧的一条——车没有停在原地，它去接了下一单。
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

# SQLite 的可空列读回来是 [System.DBNull] 而不是 $null，两者都要当成空。写成一行内联的
# `-and ... -or ...` 会被优先级拆错，那正是本场景第一次跑出来的那条假红（证据 001）。
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

function Move-VehicleToStation([object]$intent, [int]$stationId) {
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{
        orderState        = 3
        executeVehicleKey = $Context.VehicleKey
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey      = $Context.VehicleKey
        procState       = 'RUNNING'
        movementState   = 'MT_RUNNING'
        speed           = 0.8
        processingOrder = $true
        orderTaskId     = $intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey       = $Context.VehicleKey
        procState        = 'IDLE'
        movementState    = 'MT_FINISHED'
        speed            = 0
        currentPosition  = $stationId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })
}

# --- 1. 第一单：受理、到取货站、等条码 -------------------------------------------------------------

# 合成对端默认自动应答条码录入请求。这一幕的前提正是没人扫码——站点上没有要装的货——所以先
# 让它沉默。Silent 会记下请求但什么都不回，这就是操作员站在车前看着空货架的样子。
$journal.Note('Synthetic peer stops answering sublot entry requests: this stop has nothing to load.')
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Silent' })

$journal.Note("Publishing demand $firstWire to the fake MesIngest catalog.")
$null = $mes.Command('Put', "demands/$firstWire", @{
    sublot      = "L2-SUBLOT-$($Context.RunId)-A"
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Condition -Description 'the first demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage $firstId } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { Get-Intent $firstId 'TO_PICKUP' } -Until { param($v) $v -and $v.Status -eq 'CONFIRMED' }

$journal.Note('Vehicle drives to the pickup station and comes to rest.')
Move-VehicleToStation $pickupIntent $Context.PickupStationRiotId

$stage = Wait-L2Condition -Description 'the arrival was trusted and the stop is waiting for a sublot' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage $firstId } -Until { param($v) $v -eq 'AwaitingSublot' }
$assertions.Add(
    'L2-CB-01', '车到取货站后停在等条码录入这一步',
    ($stage -eq 'AwaitingSublot'),
    'AwaitingSublot', $stage)

$runtime = Get-Runtime $firstId
$requestRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT MessageId, AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$($runtime.SublotRequestMessageId)'"
$assertions.Add(
    'L2-CB-02', '条码录入请求已发出且尚未被结算',
    ($requestRows.Count -eq 1 -and (Test-L2Null $requestRows[0].AcknowledgedAt)),
    '未结算', $(if ($requestRows.Count -eq 1) { "AcknowledgedAt=$($requestRows[0].AcknowledgedAt)" } else { '(no outbox row)' }))

# --- 2. 操作员取消：本站没有要装的货 ---------------------------------------------------------------

# slotOperationAttemptId 留空是这一幕的全部关键。没有下发过装货命令，就没有仓位要清空，也就
# 没有 LoadCancellationResult 会回来——那条消息的 slotResults 是 minItems 1，本来也装不下这一
# 幕。授权本身就是整个握手。
$journal.Note('Operator cancels the load before entering any sublot.')
$cancellation = $onboard.Command('Post', 'cancel-load', @{
    demandId               = $firstId
    slotOperationAttemptId = $null
    reason                 = '现场确认本站没有要装的货。'
})
$journal.Note("Cancellation $($cancellation.body.cancellationId) raised.")

$stage = Wait-L2Condition -Description 'the journey was terminated by the cancellation' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 60 `
    -Probe { Get-Stage $firstId } -Until { param($v) $v -eq 'Completed' }

$runtime = Get-Runtime $firstId
$assertions.Add(
    'L2-CB-03', '旅程以 CANCELLED_BY_OPERATOR_BEFORE_LOAD 终结',
    ($stage -eq 'Completed' -and [string]$runtime.BlockReasonCode -eq 'CANCELLED_BY_OPERATOR_BEFORE_LOAD'),
    'Completed / CANCELLED_BY_OPERATOR_BEFORE_LOAD',
    "$stage / $($runtime.BlockReasonCode)")

$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$firstId'"
$assertions.Add(
    'L2-CB-04', '需求终态为 Cancelled',
    ($demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'Cancelled'),
    'Cancelled', $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' }))

$leaseRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT ReleasedAt FROM VehicleDispatchLeases WHERE DemandId = '$firstId'"
$assertions.Add(
    'L2-CB-05', '车辆调度租约已释放',
    ($leaseRows.Count -eq 1 -and -not (Test-L2Null $leaseRows[0].ReleasedAt)),
    '已释放', $(if ($leaseRows.Count -eq 1) { "ReleasedAt=$($leaseRows[0].ReleasedAt)" } else { '(no lease row)' }))

# 不结算它，下一个会话会把这条命令重放出去，对端按「同一个业务 id 内容变了」拒收并撕掉会话。
$requestRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$($runtime.SublotRequestMessageId)'"
$assertions.Add(
    'L2-CB-06', '那条没人回答的条码录入请求被结算了',
    ($requestRows.Count -eq 1 -and -not (Test-L2Null $requestRows[0].AcknowledgedAt)),
    '已结算', $(if ($requestRows.Count -eq 1) { "AcknowledgedAt=$($requestRows[0].AcknowledgedAt)" } else { '(no outbox row)' }))

$operationRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT COUNT(*) AS N FROM StationOperations WHERE DemandId = '$firstId'"
$assertions.Add(
    'L2-CB-07', '全程没有下发过任何仓位操作',
    ([int]$operationRows[0].N -eq 0),
    0, [int]$operationRows[0].N)

# --- 3. 闭环的另一半：车去接下一单 -----------------------------------------------------------------

$journal.Note("Publishing a second demand $secondWire; the vehicle must take it rather than stand at the pickup stop.")
$null = $mes.Command('Put', "demands/$secondWire", @{
    sublot      = "L2-SUBLOT-$($Context.RunId)-B"
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$stage = Wait-L2Condition -Description 'the vehicle took the next demand' `
    -Journal $journal -Criterion 'second-journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage $secondId } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$assertions.Add(
    'L2-CB-08', '取消之后车立刻受理下一单',
    ($stage -eq 'AwaitingPickupArrival'),
    'AwaitingPickupArrival', $stage)

$backlog = Invoke-L2Query -Connection $connection `
    -Sql "SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$secondId'"
$assertions.Add(
    'L2-CB-09', '第二单的候选判定是 ACCEPTED',
    ($backlog.Count -eq 1 -and [string]$backlog[0].ReasonCode -eq 'ACCEPTED'),
    'ACCEPTED', $(if ($backlog.Count -eq 1) { [string]$backlog[0].ReasonCode } else { '(no backlog row)' }))

# 被取消的那一单不能再被当成候选捡回来——它已经和它那一趟绑死了。
$firstBacklog = Invoke-L2Query -Connection $connection `
    -Sql "SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$firstId'"
$assertions.Add(
    'L2-CB-10', '被取消的需求不会被重新受理',
    ($firstBacklog.Count -eq 1 -and [string]$firstBacklog[0].ReasonCode -eq 'DEMAND_ALREADY_ACCEPTED'),
    'DEMAND_ALREADY_ACCEPTED',
    $(if ($firstBacklog.Count -eq 1) { [string]$firstBacklog[0].ReasonCode } else { '(no backlog row)' }))

$runtimeCount = Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM JourneyRuntimes'
$assertions.Add(
    'L2-CB-11', '两单各自一趟，没有多出来的旅程',
    ([int]$runtimeCount[0].N -eq 2),
    2, [int]$runtimeCount[0].N)

# 第一单一条 TO_PICKUP，第二单一条 TO_PICKUP。取消不派车，也不去关卡。
$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-CB-12', '全程只建了两条 RIoT 单，取消本身不派车',
    ($riotOrders.Count -eq 2),
    2, $riotOrders.Count)

$journal.Note('Scenario finished.')
