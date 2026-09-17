#Requires -Version 7

<#
扫码前取消（批次 5，control-server#83；ADR-cross-0046 第一种情形、ADR-cross-0055）。

参照 MVP 的同名场景，按 v2 一趟一单的表结构改判据。与 MVP 最大的不同在形状：MVP 是「授权即终结」，
v2 按 ADR-cross-0046 原形做成四步——请求（不带 attempt）→ 授权（slots 为空）→ 车报 ALL_EMPTY 空结果 →
DurableAck，服务端收到结果才终结需求。两段：

  A. 到站没人扫码，操作员取消：授权、slots 为空 → 合成对端报 ALL_EMPTY 空结果并被确认 → 需求 Cancelled、
     CANCELLED_BY_OPERATOR、租约与车辆占用释放、没人回答的录入请求被结算、没有任何仓位命令。
  B. 到站前断联重连，到站后扫码前取消（control-server#40 那一格）：新连接在车开往取货站途中建立，
     到站后同一条新连接上取消，结果照样结算，第 1 轮录入请求照样被结算；再重连一次，它不会被重放给车。

站点离站期限拉到十分钟（见 setup.psd1），远超本场景时长：终结必须来自取消，而不是期限到了——两条出口
走同一段收尾代码，默认值下分不清是谁关的。

合成对端的取消入口见 tools/ControlServer.FakeOnboard/README.md「装货取消」。取消工作流打开之后录入不触发装货、
期限不结束本站，由 L1 AnOpenCancellationHoldsTheStopAgainstALaterEntryAndTheStationDeadline 钉住。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Change.psm1') -Force

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

# Invoke-L2Query 已经把 DBNull 换成了 $null，所以这里只判 $null。
function Test-L2Null($value) {
    return $null -eq $value
}

function ConvertTo-Instant($value) {
    if (Test-L2Null $value) { return $null }
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Runtime([string]$demandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$demandId, [string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status, VehicleOccupancyReleasedAt FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Count([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    return [int]$rows[0].Total
}

function Get-Workflow([string]$cancellationId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT WorkflowType, State, SlotOperationAttemptId, SlotsJson, ResultMessageId FROM RecoveryWorkflows WHERE WorkflowId = '$cancellationId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Publish-Demand([string]$wireId, [string]$suffix) {
    $null = $mes.Command('Put', "demands/$wireId", @{
        sublot      = "L2-CB-$($Context.RunId)-$suffix"
        area        = 'N1-3'
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

function Wait-PickupIntent([string]$demandId, [string]$criterion) {
    return Wait-L2Condition -Description "the TO_PICKUP intent of $demandId was confirmed" `
        -Journal $journal -Criterion $criterion -TimeoutSeconds 60 `
        -Probe { Get-Intent $demandId 'TO_PICKUP' } -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
}

# 车跑完一条已确认的取货单：接单、行驶、停在取货点。
function Invoke-PickupLeg([object]$intent) {
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
        currentPosition  = $Context.PickupStationRiotId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })
}

# 合成对端记下的那一次取消：授权的决定与仓位、报出的结果、结果是否被服务端确认。
function Get-PeerCancellation([string]$cancellationId) {
    $body = (Invoke-RestMethod -Uri "$($onboard.BaseUrl)/$($onboard.Prefix)/load-cancellations" -TimeoutSec 10).body
    return @($body.cancellations) | Where-Object { [string]$_.cancellationId -eq $cancellationId } | Select-Object -First 1
}

# 车从某一刻起收到过的某类报文（合成对端的线上记录，in = 服务端发给车的）。
function Get-PeerInbound([string]$messageType, [DateTimeOffset]$since) {
    return @(@($onboard.Snapshot().body.wire) | Where-Object {
            [string]$_.direction -eq 'in' -and [string]$_.messageType -eq $messageType -and
            (ConvertTo-Instant $_.at) -ge $since
        })
}

# 取消一次并等它走完四步：车收到授权 → 车报的结果被确认 → 旅程转 Completed。返回车上那条取消记录。
function Invoke-CancellationBeforeSublot([string]$demandId, [string]$label) {
    $cancellationId = [guid]::NewGuid().ToString('D')
    $journal.Note("Operator cancels demand $demandId before any sublot entry ($cancellationId).")
    $authorized = Wait-L2Change -Description "the peer received the authorization for the $label cancellation" `
        -Journal $journal -Criterion "$label-authorization" -TimeoutSeconds 60 `
        -Baseline { Get-Count "SELECT COUNT(*) AS Total FROM RecoveryWorkflows WHERE DemandId = '$demandId'" } `
        -Action {
            $null = $onboard.Command('Put', "load-cancellations/$cancellationId", @{
                demandId               = $demandId
                slotOperationAttemptId = $null
                reason                 = '现场确认本站没有要装的货。'
            })
        } `
        -Probe { Get-PeerCancellation $cancellationId } `
        -Until { param($before, $now) $now -and -not (Test-L2Null $now.decision) }
    $acknowledged = Wait-L2Condition -Description "the peer's ALL_EMPTY result for the $label cancellation was acknowledged" `
        -Journal $journal -Criterion "$label-result-acknowledged" -TimeoutSeconds 60 `
        -Probe { Get-PeerCancellation $cancellationId } `
        -Until { param($v) $v -and -not (Test-L2Null $v.resultAcknowledgedAt) }
    $ended = Wait-L2Condition -Description "the $label stop was ended by the cancellation" `
        -Journal $journal -Criterion "$label-ended" -TimeoutSeconds 60 `
        -Probe { Get-Runtime $demandId } -Until { param($v) [string]$v.Stage -eq 'Completed' }
    return [pscustomobject]@{
        CancellationId = $cancellationId
        WorkflowsBefore = $authorized.Baseline
        Authorized     = $authorized.Value
        Acknowledged   = $acknowledged
        Ended          = $ended
    }
}

# 一次取消收尾之后，库里该有的样子。四样与旅程转 Completed 是同一次提交（PickupStopTermination 暂存、
# 结果处理一次保存），所以等到阶段之后读是安全的。
function Add-StopEndedAssertions([string]$prefix, [string]$demandId, [object]$cancellation, [object]$waiting) {
    $peer = $cancellation.Acknowledged
    $assertions.Add(
        "$prefix-a", '授权 AUTHORIZED、slots 为空；车随后报 ALL_EMPTY 空结果并被服务端确认',
        ($cancellation.WorkflowsBefore -eq 0 -and [string]$cancellation.Authorized.decision -eq 'AUTHORIZED' -and
            @($cancellation.Authorized.authorizedSlots).Count -eq 0 -and
            [string]$peer.resultOverallOutcome -eq 'ALL_EMPTY' -and -not (Test-L2Null $peer.resultAcknowledgedAt)),
        '0 个既有工作流 / AUTHORIZED / 0 仓 / ALL_EMPTY 已确认',
        "$($cancellation.WorkflowsBefore) 个既有工作流 / $($cancellation.Authorized.decision) / $(@($cancellation.Authorized.authorizedSlots).Count) 仓 / $($peer.resultOverallOutcome) 确认于 $($peer.resultAcknowledgedAt)")

    $workflow = Get-Workflow $cancellation.CancellationId
    $assertions.Add(
        "$prefix-b", '服务端的取消工作流：无 attempt、仓集合为空，收下的正是车报的那条结果，已收敛',
        ($workflow -and [string]$workflow.WorkflowType -eq 'LOAD_CANCELLATION' -and (Test-L2Null $workflow.SlotOperationAttemptId) -and
            [string]$workflow.SlotsJson -eq '[]' -and [string]$workflow.ResultMessageId -eq [string]$peer.resultMessageId -and
            [string]$workflow.State -eq 'Reconciled'),
        "LOAD_CANCELLATION / 无 attempt / [] / $($peer.resultMessageId) / Reconciled",
        $(if ($workflow) { "$($workflow.WorkflowType) / attempt=$($workflow.SlotOperationAttemptId) / $($workflow.SlotsJson) / $($workflow.ResultMessageId) / $($workflow.State)" } else { '(no workflow row)' }))

    $ended = $cancellation.Ended
    $demandRows = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
    $assertions.Add(
        "$prefix-c", '旅程 Completed / CANCELLED_BY_OPERATOR，需求 Cancelled',
        ([string]$ended.BlockReasonCode -eq 'CANCELLED_BY_OPERATOR' -and $demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'Cancelled'),
        'Completed / CANCELLED_BY_OPERATOR / Cancelled',
        "$($ended.Stage) / $($ended.BlockReasonCode) / $(if ($demandRows.Count -eq 1) { $demandRows[0].Status } else { '(no demand row)' })")

    $leaseRows = Invoke-L2Query -Connection $connection -Sql "SELECT ReleasedAt FROM VehicleDispatchLeases WHERE DemandId = '$demandId'"
    $pickupIntent = Get-Intent $demandId 'TO_PICKUP'
    $assertions.Add(
        "$prefix-d", '调度租约与车辆占用都释放了',
        ($leaseRows.Count -eq 1 -and -not (Test-L2Null $leaseRows[0].ReleasedAt) -and
            -not (Test-L2Null $pickupIntent.VehicleOccupancyReleasedAt)),
        '租约已释放 / 占用已释放',
        "ReleasedAt=$(if ($leaseRows.Count -eq 1) { $leaseRows[0].ReleasedAt } else { '(no lease row)' }) / VehicleOccupancyReleasedAt=$($pickupIntent.VehicleOccupancyReleasedAt)")

    $entryRequest = Invoke-L2Query -Connection $connection `
        -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$($waiting.SublotRequestMessageId)'"
    $assertions.Add(
        "$prefix-e", '那条没人回答的录入请求被结算了（不会再重放进后面的会话）',
        ($entryRequest.Count -eq 1 -and -not (Test-L2Null $entryRequest[0].AcknowledgedAt)),
        '已结算', $(if ($entryRequest.Count -eq 1) { "AcknowledgedAt=$($entryRequest[0].AcknowledgedAt)" } else { '(no outbox row)' }))

    $operations = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId'"
    $commands = Get-Count "SELECT COUNT(*) AS Total FROM ProtocolOutbox WHERE MessageId = '$($waiting.LoadCommandMessageId)'"
    $assertions.Add(
        "$prefix-f", '全程没有仓位操作，也没有装货命令',
        ($operations -eq 0 -and $commands -eq 0),
        '0 / 0', "$operations / $commands")
}

# --- A1. 第一单开到取货站，没人扫码 --------------------------------------------------------------

$journal.Note('Synthetic peer stops answering sublot entry requests: this stop has nothing to load.')
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Silent' })

$journal.Note("Publishing demand $firstWire.")
Publish-Demand $firstWire 'A'
$null = Wait-L2Condition -Description 'the first demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'first-stage' -TimeoutSeconds 90 `
    -Probe { $r = Get-Runtime $firstId; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }
Invoke-PickupLeg (Wait-PickupIntent $firstId 'first-to-pickup-intent')

$firstWaiting = Wait-L2Condition -Description 'the first stop is waiting for a sublot' `
    -Journal $journal -Criterion 'first-waiting' -TimeoutSeconds 120 `
    -Probe { Get-Runtime $firstId } -Until { param($v) $v -and [string]$v.Stage -eq 'AwaitingSublot' }
# 录入请求在到站那一轮、推阶段之前就已落库；车收到它要等线上送达，所以等它出现在车的线上记录里再断言。
$firstRequestSeen = Wait-L2Condition -Description 'the peer received the first sublot entry request' `
    -Journal $journal -Criterion 'first-entry-request-received' -TimeoutSeconds 30 `
    -Probe { @(Get-PeerInbound 'SublotEntryRequested' ([DateTimeOffset]::MinValue) | Where-Object { [string]$_.messageId -eq [string]$firstWaiting.SublotRequestMessageId }).Count } `
    -Until { param($v) $v -ge 1 }
$entryRequest = Invoke-L2Query -Connection $connection `
    -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$($firstWaiting.SublotRequestMessageId)'"
$assertions.Add(
    'L2-CB-01', '车到取货站停在等录入这一步，录入请求已送到车上、还没人回答',
    ([string]$firstWaiting.Stage -eq 'AwaitingSublot' -and $firstRequestSeen -ge 1 -and
        $entryRequest.Count -eq 1 -and (Test-L2Null $entryRequest[0].AcknowledgedAt)),
    'AwaitingSublot / 车收到 >= 1 次 / 未结算',
    "$($firstWaiting.Stage) / 车收到 $firstRequestSeen 次 / AcknowledgedAt=$(if ($entryRequest.Count -eq 1) { $entryRequest[0].AcknowledgedAt } else { '(no row)' })")

# --- A2. 操作员取消：四步走完，服务端收到结果才终结 ------------------------------------------------

$firstCancellation = Invoke-CancellationBeforeSublot $firstId 'first'
Add-StopEndedAssertions 'L2-CB-02' $firstId $firstCancellation $firstWaiting

# --- B1. 车接下一单 ---------------------------------------------------------------------------------

$next = Wait-L2Change -Description 'the released vehicle took the next demand' `
    -Journal $journal -Criterion 'second-accepted' -TimeoutSeconds 90 `
    -Baseline { Get-Count "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE DemandId = '$secondId'" } `
    -Action { Publish-Demand $secondWire 'B' } `
    -Probe { Get-Runtime $secondId } `
    -Until { param($before, $now) $before -eq 0 -and $now -and [string]$now.Stage -eq 'AwaitingPickupArrival' }
$assertions.Add(
    'L2-CB-03', '取消收尾之后，车接了下一单',
    ($next.Baseline -eq 0 -and [string]$next.Value.Stage -eq 'AwaitingPickupArrival'),
    '0 → AwaitingPickupArrival', "$($next.Baseline) → $($next.Value.Stage)")
$secondIntent = Wait-PickupIntent $secondId 'second-to-pickup-intent'

# --- B2. 车还没到站：断联、重连 ---------------------------------------------------------------------

$journal.Note('Vehicle drops its link on the way to the pickup station, then reconnects before arriving.')
$generationBefore = [long]$onboard.Snapshot().body.sessionGeneration
$null = $onboard.Command('Put', 'connection', @{ connected = $false })
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal
$reconnectedAt = [DateTimeOffset]::UtcNow
$reconnect = $onboard.Command('Put', 'connection', @{ connected = $true })
$beforeArrival = Get-Runtime $secondId
$assertions.Add(
    'L2-CB-04', '到站前重连走完完整握手、会话代前进，旅程仍在去取货站的路上',
    ([string]$reconnect.body.readiness -eq 'READY' -and [long]$reconnect.body.sessionGeneration -gt $generationBefore -and
        [string]$beforeArrival.Stage -eq 'AwaitingPickupArrival'),
    "READY / > $generationBefore / AwaitingPickupArrival",
    "$($reconnect.body.readiness) / $($reconnect.body.sessionGeneration) / $($beforeArrival.Stage)")

# --- B3. 到站，在新连接上扫码前取消 -----------------------------------------------------------------

Invoke-PickupLeg $secondIntent
$secondWaiting = Wait-L2Condition -Description 'the second stop is waiting for a sublot' `
    -Journal $journal -Criterion 'second-waiting' -TimeoutSeconds 120 `
    -Probe { Get-Runtime $secondId } -Until { param($v) $v -and [string]$v.Stage -eq 'AwaitingSublot' }
$secondRequestSeen = Wait-L2Condition -Description 'the peer received the second sublot entry request on the new connection' `
    -Journal $journal -Criterion 'second-entry-request-received' -TimeoutSeconds 30 `
    -Probe { @(Get-PeerInbound 'SublotEntryRequested' $reconnectedAt | Where-Object { [string]$_.messageId -eq [string]$secondWaiting.SublotRequestMessageId }).Count } `
    -Until { param($v) $v -ge 1 }
$assertions.Add(
    'L2-CB-05', '录入请求是在重连之后、新连接上送到车的',
    ($secondRequestSeen -ge 1), '>= 1 次', "$secondRequestSeen 次")

$secondCancellation = Invoke-CancellationBeforeSublot $secondId 'second'
Add-StopEndedAssertions 'L2-CB-06' $secondId $secondCancellation $secondWaiting

# --- B4. 再重连一次：已结算的录入请求不会被重放给车 --------------------------------------------------

$journal.Note('Vehicle reconnects once more; nothing settled may be replayed into the new session.')
$null = $onboard.Command('Put', 'connection', @{ connected = $false })
$replayWindowFrom = [DateTimeOffset]::UtcNow
$again = $onboard.Command('Put', 'connection', @{ connected = $true })
# 否定判据要先证明运行时确实又转了几轮：重放发生在运行时轮到这台车的那一轮。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$replayed = @(Get-PeerInbound 'SublotEntryRequested' $replayWindowFrom)
$unsettled = Get-Count "SELECT COUNT(*) AS Total FROM ProtocolOutbox WHERE MessageType = 'SublotEntryRequested' AND AcknowledgedAt IS NULL AND FencedAt IS NULL"
$assertions.Add(
    'L2-CB-07', '重连之后车没有再收到录入请求，库里也没有未结算的录入请求',
    ([string]$again.body.readiness -eq 'READY' -and $replayed.Count -eq 0 -and $unsettled -eq 0),
    'READY / 0 / 0', "$($again.body.readiness) / $($replayed.Count) / $unsettled")

# 两单都只到过取货口：全程两条 RIoT 取货单，没有关卡单，车也从没收到过仓位命令。
$riotOrders = @($riot.Snapshot().body.orders)
$gateIntents = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE Purpose = 'TO_GATE'"
$slotCommands = @(Get-PeerInbound 'SlotOperationCommand' ([DateTimeOffset]::MinValue)).Count
$assertions.Add(
    'L2-CB-08', '全程两条 RIoT 单、没有关卡单，车没收到过仓位命令',
    ($riotOrders.Count -eq 2 -and $gateIntents -eq 0 -and $slotCommands -eq 0),
    '2 / 0 / 0', "$($riotOrders.Count) / $gateIntents / $slotCommands")

$journal.Note('Scenario finished.')
