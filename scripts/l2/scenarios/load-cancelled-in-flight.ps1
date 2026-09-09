#Requires -Version 7

<#
装货命令已经下发、门已经开了，操作员这时候取消：这一单终结，这个停靠不终结。

L2 此前只覆盖了取消的另一半——`load-cancelled-before-sublot`，条码还没扫、一个仓位都没被命令，
授权本身就是整个握手，对端不回结果。**门开着的那一半一次都没在两端真进程之间跑过**：它要对端
回一份 `LoadCancellationResult` 证明仓位真的空了，服务端才敢把这一单判死。

它测的不是取消本身，是取消之后停靠还站得住：旅程回到 AwaitingSublot 而不是 Blocked，不留
BlockReasonCode，那条永远等不到 LoadResult 的 LoadBatch 命令被结算掉，第二条需求照常装完。
最后一条是 8005-agv-program#28 顺手挖出来的第二个洞，也是 L2 比单元测试更能证的那一半——命令
悬着不会当场出错，它会被重放进后来的每一个会话并撕掉它。
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

# 两条需求，同一个区号与设备，因此解析到同一个取货站点——这是本场景的前提：取消掉一条之后，
# 停靠上还剩一条，`TryContinueLoadingAtStopAsync` 才有得选。
$firstGuid = [guid]::NewGuid()
$secondGuid = [guid]::NewGuid()
$firstSublot = "L2-SUBLOT-A-$($Context.RunId)"
$secondSublot = "L2-SUBLOT-B-$($Context.RunId)"

# SQLite 的可空列读回来是 [System.DBNull] 而不是 $null，两者都要当成空。
function Test-L2Null($value) {
    return ($null -eq $value) -or ($value -is [System.DBNull])
}

function Get-Journey {
    $rows = Invoke-L2Query -Connection $connection -Sql 'SELECT * FROM JourneyRuntimes'
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stage {
    $journey = Get-Journey
    if ($null -eq $journey) { return $null }
    return [string]$journey.Stage
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Publish-Demand([string]$wireId, [string]$sublot) {
    $null = $mes.Command('Put', "demands/$wireId", @{
        sublot      = $sublot
        area        = 'N1-3'
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

function Get-CommandedLoad {
    # 已下发、还没结算的那一条装货操作。Prepared 就是「命令出去了，结果还没回来」。
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT SlotOperationAttemptId, DemandId, Status, TargetSlotsJson FROM StationOperations
              WHERE OperationType = 'Load' AND Status = 'Prepared'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# --- 1. 两条需求同站，车开过去停稳 -----------------------------------------------------------------

# 装货结果留给场景自己控。默认 Auto 会在命令到达的同一瞬间回一份 COMPLETED，那样根本没有
# 「在途」这段时间可言——Manual 把请求记下来但不回，这就是门开着、人还没放料的那一段。
$journal.Note('Synthetic peer holds load results open: the door is open and nothing has been placed yet.')
$null = $onboard.Command('Put', 'policy', @{ loadResult = 'Manual' })

$journal.Note("Publishing two demands ($firstSublot, $secondSublot) at one pickup station.")
Publish-Demand -wireId $firstGuid.ToString('N') -sublot $firstSublot
Publish-Demand -wireId $secondGuid.ToString('N') -sublot $secondSublot

$null = Wait-L2Condition -Description 'a journey was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$null = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-UpperId -purpose 'TO_PICKUP'; if ($row) { [string]$row.Status } else { $null } } `
    -Until { param($v) $v -eq 'CONFIRMED' }
$pickupIntent = Get-UpperId -purpose 'TO_PICKUP'

$journal.Note('Vehicle departs for the pickup station.')
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
$journal.Note('Vehicle arrives at the pickup station and comes to rest.')
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

# --- 2. 第一条需求扫码、下发装货命令，然后停在那里 -------------------------------------------------

$stage = Wait-L2Condition -Description 'the first demand was commanded and the stop is waiting on its result' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingLoadResult' }
$assertions.Add(
    'L2-CI-01', '第一条需求的装货命令已下发，停靠在等结果',
    ($stage -eq 'AwaitingLoadResult'),
    'AwaitingLoadResult', $stage)

$commanded = Wait-L2Condition -Description 'the commanded load operation is on the server' `
    -Journal $journal -Criterion 'commanded-load' -TimeoutSeconds 60 `
    -Probe { Get-CommandedLoad } -Until { param($v) $null -ne $v }
$cancelledDemandId = [string]$commanded.DemandId
$attemptId = [string]$commanded.SlotOperationAttemptId
$journal.Note("Cancelling demand $cancelledDemandId, slot operation attempt $attemptId.")

# 这条命令的 messageId 要在取消之前记下来：取消结算它之后，它就不在未结算的那一批里了，
# 而本场景最要紧的一条判据正是「它被结算了」。
$membership = Invoke-L2Query -Connection $connection `
    -Sql "SELECT LoadCommandMessageId FROM JourneyDemands WHERE DemandId = '$cancelledDemandId'"
$loadCommandMessageId = [string]$membership[0].LoadCommandMessageId
$pendingBefore = Invoke-L2Query -Connection $connection `
    -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$loadCommandMessageId'"
$assertions.Add(
    'L2-CI-02', '取消之前，那条装货命令确实还挂着没结算',
    ($pendingBefore.Count -eq 1 -and (Test-L2Null $pendingBefore[0].AcknowledgedAt)),
    '未结算',
    $(if ($pendingBefore.Count -eq 1) { "AcknowledgedAt=$($pendingBefore[0].AcknowledgedAt)" } else { '(no outbox row)' }))

# --- 3. 在途取消：授权 + 空仓证明 ------------------------------------------------------------------

# 带上 attemptId 是这一幕与 before-sublot 那一幕的全部区别。服务端授权之后不会当场终结这一单，
# 它要等对端回一份 overallOutcome = ALL_EMPTY 且每个仓位 COMPLETED/EMPTY/LOCKED/RESET 的
# LoadCancellationResult——合成对端收到 AUTHORIZED 就自动回，那正是本票补上的能力。
$journal.Note('Operator cancels the load that is already in flight.')
$cancellation = $onboard.Command('Post', 'cancel-load', @{
    demandId               = $cancelledDemandId
    slotOperationAttemptId = $attemptId
    reason                 = '现场确认这一单不装了，货还没放进去。'
})
$journal.Note("Cancellation $($cancellation.body.cancellationId) raised.")

$workflow = Wait-L2Condition -Description 'the cancellation workflow reconciled on the emptiness proof' `
    -Journal $journal -Criterion 'cancellation-workflow' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT State, Outcome, SlotOperationAttemptId FROM RecoveryWorkflows
                  WHERE WorkflowType = 'LOAD_CANCELLATION'"
        if ($rows.Count -eq 0) { return $null }
        return $rows[0]
    } `
    -Until { param($v) [string]$v.State -eq 'Reconciled' }
$assertions.Add(
    'L2-CI-03', '取消工作流凭空仓证明结清，且钉的是在途那个 attempt',
    ([string]$workflow.State -eq 'Reconciled' -and [string]$workflow.SlotOperationAttemptId -eq $attemptId),
    "Reconciled / $attemptId",
    "$($workflow.State) / $($workflow.SlotOperationAttemptId)")

$operationRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
$assertions.Add(
    'L2-CI-04', '在途的那次仓位操作终态是 Cancelled',
    ($operationRows.Count -eq 1 -and [string]$operationRows[0].Status -eq 'Cancelled'),
    'Cancelled',
    $(if ($operationRows.Count -eq 1) { [string]$operationRows[0].Status } else { '(no operation row)' }))

$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$cancelledDemandId'"
$assertions.Add(
    'L2-CI-05', '被取消的需求终态是 Cancelled',
    ($demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'Cancelled'),
    'Cancelled',
    $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' }))

$suppression = Invoke-L2Query -Connection $connection `
    -Sql "SELECT ReasonCode, DemandId FROM TransportDemandSuppressions"
$assertions.Add(
    'L2-CI-06', '按业务键写下了永久取消抑制',
    ($suppression.Count -eq 1 -and
        [string]$suppression[0].ReasonCode -eq 'CANCELLED_BY_OPERATOR' -and
        [string]$suppression[0].DemandId -eq $cancelledDemandId),
    '1 条 / CANCELLED_BY_OPERATOR',
    $(if ($suppression.Count -eq 1) {
        "$($suppression.Count) 条 / $($suppression[0].ReasonCode)"
    } else { "$($suppression.Count) 条" }))

# 本场景最贵的一条。它永远等不到 LoadResult，只有关闭的批次会结算它；悬着就会被重放进后来的
# 每一个会话，对端按「同一个业务 id 内容变了」拒收并撕掉会话。单元测试只能证服务端这一步写了
# 那一行，证不了会话还在。
$settled = Wait-L2Condition -Description 'the dangling LoadBatch command was settled by the cancellation' `
    -Journal $journal -Criterion 'load-command-settled' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$loadCommandMessageId'"
        if ($rows.Count -eq 0) { return $null }
        if (Test-L2Null $rows[0].AcknowledgedAt) { return 'pending' }
        return [string]$rows[0].AcknowledgedAt
    } `
    -Until { param($v) $v -ne 'pending' }
$assertions.Add(
    'L2-CI-07', '那条永远等不到结果的装货命令被取消结算掉了',
    ($settled -ne 'pending'),
    '已结算', $settled)

# --- 4. 停靠没塌：旅程回到等下一个条码 -------------------------------------------------------------

$stage = Wait-L2Condition -Description 'the stop went back to waiting for its next sublot' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingSublot' }
$assertions.Add(
    'L2-CI-08', '取消终结的是这一单不是这个停靠：旅程回到 AwaitingSublot',
    ($stage -eq 'AwaitingSublot'),
    'AwaitingSublot', $stage)

# CANCELLED_BY_OPERATOR 留在一条又跑起来的旅程上会被读成「这趟被取消了」。取消掉的是哪一单，
# 记在需求、它的恢复工作流和那条业务键抑制上，不记在这里。
$journey = Get-Journey
$assertions.Add(
    'L2-CI-09', '跑起来的旅程不带阻塞理由',
    ([string]::IsNullOrWhiteSpace([string]$journey.BlockReasonCode)),
    '(空)', [string]$journey.BlockReasonCode)

# --- 5. 第二条需求照常装完，车去关卡 ---------------------------------------------------------------

$journal.Note('Synthetic peer answers load results again; the operator loads the remaining demand.')
$null = $onboard.Command('Put', 'policy', @{ loadResult = 'Auto' })

$committed = Wait-L2Condition -Description 'the remaining demand loaded normally' `
    -Journal $journal -Criterion 'committed-loads' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT COUNT(*) AS N FROM StationOperations WHERE OperationType = 'Load' AND Status = 'Committed'"
        return [int]$rows[0].N
    } `
    -Until { param($v) $v -ge 1 }
$assertions.Add(
    'L2-CI-10', '取消之后剩下那条需求照常完成装载闭环',
    ($committed -eq 1),
    1, $committed)

# 又是一处要等不能直接读的：仓位操作转 Committed 与需求在旅程里转 Loaded 不是同一次写入，
# 后者由引擎的下一轮收尾。首跑（证据 001）就红在这里——期望 Loaded 实际 Planned，而随后
# L2-CI-12 等到了 AwaitingGateArrival，说明它只是还没轮到。
$remainingState = Wait-L2Condition -Description 'the remaining demand is marked loaded on the journey' `
    -Journal $journal -Criterion 'remaining-demand-state' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT State FROM JourneyDemands WHERE DemandId <> '$cancelledDemandId'"
        if ($rows.Count -ne 1) { return "$($rows.Count) rows" }
        return [string]$rows[0].State
    } `
    -Until { param($v) $v -eq 'Loaded' }
$assertions.Add(
    'L2-CI-11', '剩下那条需求在旅程里的状态是 Loaded',
    ($remainingState -eq 'Loaded'),
    'Loaded', $remainingState)

$stage = Wait-L2Condition -Description 'the journey left the pickup stop for the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$assertions.Add(
    'L2-CI-12', '装完之后这趟车照常开去关卡',
    ($stage -eq 'AwaitingGateArrival'),
    'AwaitingGateArrival', $stage)

# 取消不派车。全程仍然只有取货、关卡两条 RIoT 单——重复建单在厂区里就是两次真实派车。
#
# 要等，不能直接读：转阶段与订单落到假 RIoT 不是同一时刻，直接读在本机稳过、在 CI 上会读到 1 条
# （load-cancelled-before-sublot 的 L2-CB-12 踩过这个）。用 -ge 2 等待、再按 -eq 2 断言。
$riotOrderCount = Wait-L2Condition -Description 'both orders reached the fake RIoT' `
    -Journal $journal -Criterion 'riot-order-count' -TimeoutSeconds 30 `
    -Probe { @($riot.Snapshot().body.orders).Count } -Until { param($v) $v -ge 2 }
$journal.Note("Fake RIoT holds $riotOrderCount order(s) at the end of the scenario.")
$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-CI-13', '全程只建了两条 RIoT 单，取消本身不派车',
    ($riotOrders.Count -eq 2),
    2, $riotOrders.Count)

$journal.Note('Scenario finished.')
