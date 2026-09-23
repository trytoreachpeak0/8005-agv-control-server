#Requires -Version 7

<#
站点离站期限（批次 5，control-server#79；ADR-cross-0055、ADR-cross-0058 决策 7）。

参照 MVP 的 sublot-wait-timeout，按 v2 一趟一单的表结构改判据。两段：

  A. 到站没人扫码：期限从到站起算，到期服务端自己结束本站——需求 Cancelled、理由
     CANCELLED_BY_STATION_TIMEOUT、租约与车辆占用释放、没人回答的录入请求被结算、没有任何仓位操作；
     车收到并确认一张空清单，车上那一站被撤掉（control-server#323）；同一个 DemandId 不再被派。
  B. 期限走到一半断联重连：重连（新的会话代）作废本轮期限，会话回到 Ready 之后从那一刻重新计满——
     原期限过了本站还在等，要等到新期限才结束。

v2 之前这一站没有期限也没有出口：没人扫码，服务端每轮重发录入请求，车一直占着取货站，只能改库解救。

合成对端的仓门安全证据始终是「全部关好」，所以这里测不到「门开着不结束本站」那一格，那一格由
L1 TheStationDeadlineDoesNotEndTheStopWhileADoorIsOpenOrUnknown 钉住。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Change.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$connection = $Context.Connection

# 与 setup.psd1 的 StationDepartureWaitTimeout 相同。
$window = [TimeSpan]::FromSeconds(20)

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

# ControlServerDbContext 把 DateTimeOffset 存成文本；判据要拿它和期限做加减，所以统一转成 DateTimeOffset。
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

# 列名用 Total，与既有场景（g3-journey-demand-to-pickup 等）一致。
function Get-Count([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    return [int]$rows[0].Total
}

function Publish-Demand([string]$wireId, [string]$suffix) {
    $null = $mes.Command('Put', "demands/$wireId", @{
        sublot      = "L2-SD-$($Context.RunId)-$suffix"
        area        = 'N1-3'
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

<#
这辆车收到过的清单快照（本装置只有一辆车，所以不按车筛）：列着 $DemandId 的那几版（到站时发的），与 items 为空的那几版
（旅程收尾时发的，control-server#323）。收尾那一版不列任何需求，所以不能按需求认，只能按形状认。
#>
function Get-StopWorklists([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, PayloadJson, AcknowledgedAt FROM ProtocolOutbox WHERE MessageType = 'CurrentStopWorklistSnapshot'")
    $listing = [Collections.Generic.List[object]]::new()
    $empty = [Collections.Generic.List[object]]::new()
    foreach ($row in $rows) {
        $payload = ([string]$row.PayloadJson | ConvertFrom-Json -DateKind String).payload
        $items = @($payload.items)
        $snapshot = [pscustomobject]@{
            MessageId    = [string]$row.MessageId
            Revision     = [long]$payload.worklistRevision
            StationId    = [string]$payload.stationId
            Deadline     = ConvertTo-Instant $payload.stationDepartureDeadlineAt
            Acknowledged = -not (Test-L2Null $row.AcknowledgedAt)
        }
        if ($items.Count -eq 0) { $empty.Add($snapshot) }
        elseif (@($items | Where-Object { [string]$_.demandId -eq $DemandId }).Count -gt 0) { $listing.Add($snapshot) }
    }
    return [pscustomobject]@{ Listing = $listing.ToArray(); Empty = $empty.ToArray() }
}

# 车跑一条取货单：接单、行驶、停在取货点。第二段车本来就停在取货点，但那是一条新的运单，到站判定要重跑一遍。
function Invoke-PickupLeg([string]$demandId, [string]$criterion) {
    $intent = Wait-L2Condition -Description "the TO_PICKUP intent of $demandId was confirmed" `
        -Journal $journal -Criterion $criterion -TimeoutSeconds 60 `
        -Probe { Get-Intent $demandId 'TO_PICKUP' } -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
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

# --- A1. 第一单开到取货站，然后没有人来 ------------------------------------------------------------

$journal.Note('Synthetic peer stops answering sublot entry requests: nobody comes to scan.')
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Silent' })

$journal.Note("Publishing demand $firstWire.")
Publish-Demand $firstWire 'A'
$null = Wait-L2Condition -Description 'the first demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'first-stage' -TimeoutSeconds 90 `
    -Probe { $r = Get-Runtime $firstId; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }
Invoke-PickupLeg $firstId 'first-to-pickup-intent'

$arrived = Wait-L2Condition -Description 'the first stop is waiting for a sublot with its deadline running' `
    -Journal $journal -Criterion 'first-waiting' -TimeoutSeconds 120 `
    -Probe { Get-Runtime $firstId } `
    -Until { param($v) [string]$v.Stage -eq 'AwaitingSublot' -and -not (Test-L2Null $v.StationDepartureWaitStartedAt) }
$firstStart = ConvertTo-Instant $arrived.StationDepartureWaitStartedAt
$assertions.Add(
    'L2-SD-01', '车到取货站就开始计本站期限（不是等装货提交才开始）',
    ([string]$arrived.Stage -eq 'AwaitingSublot' -and $null -ne $firstStart),
    'AwaitingSublot / 起点有值', "$($arrived.Stage) / $firstStart")

# --- A2. 期限没到之前，什么都不许发生 --------------------------------------------------------------

# 否定判据要先证明运行时确实又转了几轮。录入请求在到站那一轮、推阶段之前就已落库，所以等到阶段之后读它是安全的。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$readAt = [DateTimeOffset]::UtcNow
$beforeDeadline = Get-Runtime $firstId
$entryRequest = Invoke-L2Query -Connection $connection `
    -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$($arrived.SublotRequestMessageId)'"
$assertions.Add(
    'L2-SD-02', '期限未到：旅程原地等，录入请求还挂着没人回答',
    ($readAt -lt $firstStart.Add($window) -and [string]$beforeDeadline.Stage -eq 'AwaitingSublot' -and
        $entryRequest.Count -eq 1 -and (Test-L2Null $entryRequest[0].AcknowledgedAt)),
    "读于期限前 / AwaitingSublot / 请求未结算",
    "读于期限前 $([math]::Round(($firstStart.Add($window) - $readAt).TotalSeconds, 1)) s / $($beforeDeadline.Stage) / AcknowledgedAt=$(if ($entryRequest.Count -eq 1) { $entryRequest[0].AcknowledgedAt } else { '(no row)' })")

# --- A3. 期限到：服务端自己结束本站 ----------------------------------------------------------------

$journal.Note('Waiting for the station departure deadline (20 s, set by the sidecar).')
$ended = Wait-L2Condition -Description 'the station deadline ended the first stop' `
    -Journal $journal -Criterion 'first-ended' -TimeoutSeconds 90 `
    -Probe { Get-Runtime $firstId } -Until { param($v) [string]$v.Stage -eq 'Completed' }
$endedAt = ConvertTo-Instant $ended.UpdatedAt
$assertions.Add(
    'L2-SD-03', '到期结束本站：Completed / CANCELLED_BY_STATION_TIMEOUT，且不早于到站起算的期限',
    ([string]$ended.BlockReasonCode -eq 'CANCELLED_BY_STATION_TIMEOUT' -and $endedAt -ge $firstStart.Add($window)),
    "Completed / CANCELLED_BY_STATION_TIMEOUT / >= $($firstStart.Add($window).ToString('o'))",
    "$($ended.Stage) / $($ended.BlockReasonCode) / $($endedAt.ToString('o'))")

# 下面四样与旅程转 Completed 是同一次提交（PickupStopTermination 暂存、引擎一次保存），所以等到阶段之后读是安全的。
$demandRows = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$firstId'"
$assertions.Add(
    'L2-SD-04', '需求终态为 Cancelled',
    ($demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'Cancelled'),
    'Cancelled', $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' }))

$leaseRows = Invoke-L2Query -Connection $connection -Sql "SELECT ReleasedAt FROM VehicleDispatchLeases WHERE DemandId = '$firstId'"
$pickupIntent = Get-Intent $firstId 'TO_PICKUP'
$assertions.Add(
    'L2-SD-05', '调度租约与车辆占用都释放了',
    ($leaseRows.Count -eq 1 -and -not (Test-L2Null $leaseRows[0].ReleasedAt) -and
        -not (Test-L2Null $pickupIntent.VehicleOccupancyReleasedAt)),
    '租约已释放 / 占用已释放',
    "ReleasedAt=$(if ($leaseRows.Count -eq 1) { $leaseRows[0].ReleasedAt } else { '(no lease row)' }) / VehicleOccupancyReleasedAt=$($pickupIntent.VehicleOccupancyReleasedAt)")

$entryRequest = Invoke-L2Query -Connection $connection `
    -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$($arrived.SublotRequestMessageId)'"
$assertions.Add(
    'L2-SD-06', '那条没人回答的录入请求被结算了（不会再重放进后面的会话）',
    ($entryRequest.Count -eq 1 -and -not (Test-L2Null $entryRequest[0].AcknowledgedAt)),
    '已结算', $(if ($entryRequest.Count -eq 1) { "AcknowledgedAt=$($entryRequest[0].AcknowledgedAt)" } else { '(no outbox row)' }))

$operations = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$firstId'"
$workflows = Get-Count "SELECT COUNT(*) AS Total FROM RecoveryWorkflows WHERE DemandId = '$firstId'"
$assertions.Add(
    'L2-SD-07', '超时不下发任何仓位操作，也不开恢复流程',
    ($operations -eq 0 -and $workflows -eq 0),
    '0 / 0', "$operations / $workflows")

# 旅程收尾之后，车上那一站的清单要被撤掉（control-server#323，program#86 v2 的 A 形态）：服务端发一张 items 为空、号比到站
# 那一版大的清单，车确认了它。修之前收尾不碰发件箱，车上一直留着这一站、录入请求与「取消装货」按钮。确认由车另起一次写库，
# 与旅程转 Completed 不是同一次提交，所以等，而不是立刻读；等不到时把最后一次读数交给判据，红点落在判据表里。
$closure = Wait-L2ConditionOrLast -Description 'the vehicle acknowledged an empty worklist above the arrival one' `
    -Journal $journal -Criterion 'first-closure-worklist' -TimeoutSeconds 30 `
    -Probe { Get-StopWorklists $firstId } `
    -Until {
        param($v)
        $top = ($v.Listing | Measure-Object -Property Revision -Maximum).Maximum
        @($v.Listing).Count -ge 1 -and
            @($v.Empty | Where-Object { $_.Acknowledged -and $_.Revision -gt $top -and $_.StationId -eq [string]$arrived.PickupStationId }).Count -ge 1
    }
$arrivalTop = ($closure.Listing | Measure-Object -Property Revision -Maximum).Maximum
$acknowledgedEmpty = @($closure.Empty | Where-Object { $_.Acknowledged -and $_.Revision -gt $arrivalTop -and $_.StationId -eq [string]$arrived.PickupStationId })
$journal.Observe('first-stop-worklists',
    ("到站 " + ((@($closure.Listing) | ForEach-Object { "r$($_.Revision)$(if ($_.Acknowledged) { '/ack' })" }) -join ',') +
        "；空清单 " + ((@($closure.Empty) | ForEach-Object { "r$($_.Revision)@$($_.StationId)$(if ($_.Acknowledged) { '/ack' })" }) -join ',')),
    @{ worklists = $closure })
$assertions.Add(
    'L2-SD-15', '本站结束之后，车收到并确认了一张空清单：同一个站、号比到站那一版大（control-server#323）',
    (@($closure.Listing).Count -ge 1 -and $acknowledgedEmpty.Count -ge 1),
    "空清单 @$([string]$arrived.PickupStationId) / r > $arrivalTop / 已确认",
    $(if (@($closure.Empty).Count -eq 0) { '(一张空清单都没有)' } else {
        (@($closure.Empty) | ForEach-Object { "r$($_.Revision)@$($_.StationId) ack=$($_.Acknowledged)" }) -join ', ' }))

# --- A4. 同一个 DemandId 不再被派 ------------------------------------------------------------------

# 假 MesIngest 的目录里这一单还在，所以「不再被派」是真在判：运行时又转几轮，照样看得见它，照样不接。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$firstRuntimes = Get-Count "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE DemandId = '$firstId'"
$ordersAfterTimeout = @($riot.Snapshot().body.orders).Count
$catalogStillLists = @($mes.Snapshot().body.demands | Where-Object { [string]$_.demandId -eq $firstWire }).Count
$assertions.Add(
    'L2-SD-08', '目录里仍有这一单，但它没有被再派：只有一条旅程、一条 RIoT 单',
    ($catalogStillLists -ge 1 -and $firstRuntimes -eq 1 -and $ordersAfterTimeout -eq 1),
    '目录仍列出 / 1 条旅程 / 1 条 RIoT 单',
    "目录列出 $catalogStillLists / $firstRuntimes 条旅程 / $ordersAfterTimeout 条 RIoT 单")

# --- B1. 车去接下一单 ------------------------------------------------------------------------------

$next = Wait-L2Change -Description 'the released vehicle took the next demand' `
    -Journal $journal -Criterion 'second-accepted' -TimeoutSeconds 90 `
    -Baseline { Get-Count "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE DemandId = '$secondId'" } `
    -Action { Publish-Demand $secondWire 'B' } `
    -Probe { Get-Runtime $secondId } `
    -Until { param($before, $now) $before -eq 0 -and $now -and [string]$now.Stage -eq 'AwaitingPickupArrival' }
$assertions.Add(
    'L2-SD-09', '本站结束之后，车接了下一单',
    ($next.Baseline -eq 0 -and [string]$next.Value.Stage -eq 'AwaitingPickupArrival'),
    '0 → AwaitingPickupArrival', "$($next.Baseline) → $($next.Value.Stage)")

Invoke-PickupLeg $secondId 'second-to-pickup-intent'
$secondArrived = Wait-L2Condition -Description 'the second stop is waiting for a sublot with its deadline running' `
    -Journal $journal -Criterion 'second-waiting' -TimeoutSeconds 120 `
    -Probe { Get-Runtime $secondId } `
    -Until { param($v) [string]$v.Stage -eq 'AwaitingSublot' -and -not (Test-L2Null $v.StationDepartureWaitStartedAt) }
$secondStart = ConvertTo-Instant $secondArrived.StationDepartureWaitStartedAt
$originalDeadline = $secondStart.Add($window)

# --- B2. 期限走到一半，断联、重连 ------------------------------------------------------------------

# 让期限先走掉一截。这里等的是运行时的轮次（出厂 2 秒一轮，本装置 1 秒），不是 sleep。
$null = Wait-L2Iterations -Riot $riot -Count 8 -Journal $journal
$journal.Note('Vehicle drops its link partway through the countdown.')
$generationBefore = [long]$onboard.Snapshot().body.sessionGeneration
$null = $onboard.Command('Put', 'connection', @{ connected = $false })
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal

$reconnectRequestedAt = [DateTimeOffset]::UtcNow
$script:reconnect = $null
$refill = Wait-L2Change -Description 'the reconnect voided the countdown and the runtime refilled it once Ready' `
    -Journal $journal -Criterion 'second-refilled' -TimeoutSeconds 60 `
    -Baseline { ConvertTo-Instant (Get-Runtime $secondId).StationDepartureWaitStartedAt } `
    -Action { $script:reconnect = $onboard.Command('Put', 'connection', @{ connected = $true }) } `
    -Probe { ConvertTo-Instant (Get-Runtime $secondId).StationDepartureWaitStartedAt } `
    -Until { param($before, $now) $null -ne $now -and $now -gt $before }
$generationAfter = [long]$script:reconnect.body.sessionGeneration
$assertions.Add(
    'L2-SD-10', '重连走完完整握手，会话代前进',
    ([string]$script:reconnect.body.readiness -eq 'READY' -and $generationAfter -gt $generationBefore),
    "READY / > $generationBefore", "$($script:reconnect.body.readiness) / $generationAfter")
$refilledStart = $refill.Value
$assertions.Add(
    'L2-SD-11', '期限起点换成了重连之后的时刻（不是断联前那个起点接着走）',
    ($refill.Baseline -eq $secondStart -and $refilledStart -ge $reconnectRequestedAt),
    ">= $($reconnectRequestedAt.ToString('o'))（原起点 $($secondStart.ToString('o'))）",
    "$($refilledStart.ToString('o'))（基线 $(if ($refill.Baseline) { $refill.Baseline.ToString('o') } else { '(null)' })）")

# --- B3. 原期限过了，本站还在等；到新期限才结束 -----------------------------------------------------

$pastOriginal = Wait-L2Condition -Description 'the original deadline has passed' `
    -Journal $journal -Criterion 'second-past-original-deadline' -TimeoutSeconds 60 `
    -Probe { [pscustomobject]@{ At = [DateTimeOffset]::UtcNow; Runtime = Get-Runtime $secondId } } `
    -Until { param($v) $v.At -ge $originalDeadline.AddSeconds(3) }
$assertions.Add(
    'L2-SD-12', '原期限过去 3 秒，本站仍在等录入（断联那一轮的截止已作废）',
    ([string]$pastOriginal.Runtime.Stage -eq 'AwaitingSublot' -and $pastOriginal.At -lt $refilledStart.Add($window)),
    "AwaitingSublot（读于 $($originalDeadline.AddSeconds(3).ToString('o')) 之后、新期限之前）",
    "$($pastOriginal.Runtime.Stage)（读于 $($pastOriginal.At.ToString('o'))）")

# 重填的期限要送到车上（control-server#339）：车载端不作废也不重新计满期限，永远照最新一版清单显示倒计时，只在服务端重填，
# 车上就会在原期限显示「已到期」，而服务端刚重新计满。重填与新的一版清单是同一次保存，车的确认是之后的另一次写入，所以等。
# 判据要的是「号比到站那一版大、期限等于重填后的期限、车确认了」三样都在同一行上；等不到时把最后一次读数交给判据。
# 放在 L2-SD-12 之后：修前这里要等满 30 秒，放在前面会把 L2-SD-12「原期限过去 3 秒、新期限之前」那一读推到新期限之后，
# 让一条与本票无关的判据跟着红。
$refilledDeadline = $refilledStart.Add($window)
$secondWorklists = Wait-L2ConditionOrLast -Description 'the vehicle acknowledged a newer worklist carrying the refilled deadline' `
    -Journal $journal -Criterion 'second-refilled-worklist' -TimeoutSeconds 30 `
    -Probe { Get-StopWorklists $secondId } `
    -Until {
        param($v)
        $arrival = ($v.Listing | Measure-Object -Property Revision -Minimum).Minimum
        @($v.Listing | Where-Object { $_.Acknowledged -and $_.Revision -gt $arrival -and $_.Deadline -eq $refilledDeadline }).Count -ge 1
    }
$secondArrivalRevision = ($secondWorklists.Listing | Measure-Object -Property Revision -Minimum).Minimum
$carriesRefill = @($secondWorklists.Listing | Where-Object {
        $_.Acknowledged -and $_.Revision -gt $secondArrivalRevision -and $_.Deadline -eq $refilledDeadline })
$journal.Observe('second-stop-worklists',
    ((@($secondWorklists.Listing) | ForEach-Object {
                "r$($_.Revision) 期限 $(if ($_.Deadline) { $_.Deadline.ToString('o') } else { '(null)' })$(if ($_.Acknowledged) { '/ack' })"
            }) -join '；'),
    @{ worklists = $secondWorklists })
$assertions.Add(
    'L2-SD-16', '重连之后，车收到并确认了一版号更大的清单，带的正是重填后的期限（control-server#339）',
    ($carriesRefill.Count -ge 1),
    "r > $secondArrivalRevision / 期限 $($refilledDeadline.ToString('o')) / 已确认",
    ((@($secondWorklists.Listing) | ForEach-Object {
                "r$($_.Revision) $(if ($_.Deadline) { $_.Deadline.ToString('o') } else { '(null)' }) ack=$($_.Acknowledged)"
            }) -join ', '))

$secondEnded = Wait-L2Condition -Description 'the refilled deadline ended the second stop' `
    -Journal $journal -Criterion 'second-ended' -TimeoutSeconds 90 `
    -Probe { Get-Runtime $secondId } -Until { param($v) [string]$v.Stage -eq 'Completed' }
$secondEndedAt = ConvertTo-Instant $secondEnded.UpdatedAt
$assertions.Add(
    'L2-SD-13', '按重连后重新计满的期限结束：不早于新起点加整段时长',
    ([string]$secondEnded.BlockReasonCode -eq 'CANCELLED_BY_STATION_TIMEOUT' -and $secondEndedAt -ge $refilledStart.Add($window)),
    "CANCELLED_BY_STATION_TIMEOUT / >= $($refilledStart.Add($window).ToString('o'))",
    "$($secondEnded.BlockReasonCode) / $($secondEndedAt.ToString('o'))")

# 两单都只到过取货口：全程两条 RIoT 取货单，没有一条关卡单。
$riotOrders = @($riot.Snapshot().body.orders)
$gateIntents = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE Purpose = 'TO_GATE'"
$assertions.Add(
    'L2-SD-14', '全程两条 RIoT 单（两单各一条取货），没有关卡单',
    ($riotOrders.Count -eq 2 -and $gateIntents -eq 0),
    '2 / 0', "$($riotOrders.Count) / $gateIntents")

$journal.Note('Scenario finished.')
