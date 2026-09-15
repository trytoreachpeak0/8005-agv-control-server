#Requires -Version 7

<#
站点等待超时把旅程结束之后，车上仍挂着那一站、仍给「取消装货」；按两下，第二下被服务端掐连接。

现场 2026-09-15，agv01，旅程 f25b8553 停靠 2（N15-8_N16-8，需求 d4cbfb37，子批 Q26092970-2），服务端 670bdd45、
车载端 959dd615：

1. 07:11:45Z 等待窗口（五分钟）到期，服务端把需求判 CANCELLED_BY_STATION_TIMEOUT，旅程 Completed。
2. HMI 仍显示这一站：倒计时「已到期，等待本站结束」，行程仍是「交货 关卡 待走」。
3. 07:15:36Z 按「取消装货」，服务端回 REJECTED / ACTION_NOT_ALLOWED_IN_STATE。
4. 07:16:51Z 再按一次，服务端抛 ProtocolContentConflictException「MessageId was replayed with different normalized
   content.」、掐连接；07:16:55Z 车重连。

**两件事，根因不同，这一条都钉。**

- 车上那一站不清（L2-SST-03～05）。`TryTimeOutSublotWaitAsync` 走 `CancelDemandBeforeLoadAsync`：把录入请求那条出站
  报文记成已结算、旅程写成 Completed，**不给车发任何东西**。车载端的 `_currentEntryRequest` 只在 `SublotRejected`
  或会话掉出 Ready 时清空，清单与行程投影只在断线时清空，于是 `CanSubmitSublot` 与扫码前取消的按钮一直在。
- 第二下掐连接（L2-SST-06）。扫码前取消的 messageId 是 `StableUuid(需求|before-load|load-cancellation)`，每次按下
  却带新的 `operatorContext.verifiedAt` 与 `sentAt`，而服务端 `ProtocolInbox` 把 messageId 绑在首次整行字节上。这是
  8005-agv-onboard-hmi#39，修复 `297dd81` 已经合进 `OnboardHmi_MVP`（PR #46）；线上包 959dd615 从 `6b8a0b0` 拉出，
  不含它。

判据按「修好之后该是什么样」写，不预设修在哪一端：车不再要子批、不再给取消、当前站清单不再挂着这单，按几下都不
掉线，已结束的需求不会被迟到的取消再授权一次。两下之间不等七十五秒：服务端对这两条请求的判定只与 messageId 和
内容有关，与间隔无关。

断言只从服务端 SQLite、车载端自动化面 `/snapshot` 与会话计数读。自动化面只用来按按钮。
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

Import-Module (Join-Path $Context.Repository 'scripts/field/FieldOperator.psm1') -Force

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SST-$($Context.RunId)"
$action = 'LOAD_CANCELLATION'

$field = New-FieldOperator -Connection $connection -AgvId $Context.AgvId `
    -SimulatorPort $Context.SimulatorHttpPort -AutomationPort $Context.OnboardAutomationPort `
    -Log { param($message) $journal.Note("field-operator: $message") }.GetNewClosure()

# Every connection the vehicle opens starts with one SessionHello, so this counts connections.
function Get-SessionHelloCount {
    $rows = (Invoke-L2Query -Connection $connection `
        -Sql "SELECT COUNT(*) AS N FROM ProtocolInbox WHERE MessageType = 'SessionHello'")
    return [int]$rows[0].N
}

# What the HMI shows, as far as the automation face sees it: the predicates the entry box, the 取消装货
# button and the stop panel are bound to.
function Get-VehicleView {
    $state = (Get-FieldOnboardSnapshot -Field $field).state
    $journey = Get-FieldProperty $state 'wireToGateJourney'
    $worklist = Get-FieldProperty $journey 'currentStopWorklist'
    $plan = Get-FieldProperty $journey 'upcomingStopPlan'
    $items = @(Get-FieldProperty $worklist 'items' | Where-Object { $null -ne $_ })
    $legs = @(Get-FieldProperty $plan 'legs' | Where-Object { $null -ne $_ })
    $matching = @($items | Where-Object { [string](Get-FieldProperty $_ 'demandId') -eq $demandId })
    return [pscustomobject]@{
        CanSubmitSublot   = [bool](Get-FieldProperty $state 'canSubmitSublot')
        OffersCancel      = @(Get-FieldProperty $state 'availableRecoveryActions') -contains $action
        StationId         = [string](Get-FieldProperty $worklist 'stationId')
        DeadlineAt        = [string](Get-FieldProperty $worklist 'stationDepartureDeadlineAt')
        WorklistHasDemand = $matching.Count -gt 0
        PlanLegs          = $legs.Count
    }
}

function Format-VehicleView([object]$View) {
    return ("canSubmitSublot=$($View.CanSubmitSublot) offersCancel=$($View.OffersCancel) " +
        "station=$($View.StationId) deadline=$($View.DeadlineAt) worklistHasDemand=$($View.WorklistHasDemand) " +
        "planLegs=$($View.PlanLegs)")
}

function Get-OutboxCounts {
    $counts = @{}
    foreach ($row in (Invoke-L2Query -Connection $connection `
            -Sql 'SELECT MessageType, COUNT(*) AS N FROM ProtocolOutbox GROUP BY MessageType')) {
        $counts[[string]$row.MessageType] = [int]$row.N
    }
    return $counts
}

# Negative criteria only mean something once the runtime has had its chances. Map station reads are the one
# observable for "the runtime came round again"; if an idle runtime turns out not to read the map, fall back to
# plain time rather than failing the run on the probe.
function Wait-RuntimeRounds([int]$Count, [string]$Why) {
    try {
        $null = Wait-L2Iterations -Riot $riot -Count $Count -TimeoutSeconds 90 -Journal $journal
    } catch {
        $journal.Note("Runtime iterations not observable ($Why): $($_.Exception.Message) Sleeping $($Count * 2) s instead.")
        Start-Sleep -Seconds ($Count * 2)
    }
}

function Invoke-CancelPress([string]$Label) {
    $response = Invoke-FieldFace -Field $field -Face Onboard -Method POST -Path '/recovery/requests' -Envelope -Body @{
        action = $action
        reason = "L2：站点等待超时结束旅程之后按取消装货（$Label）"
    }
    # 202 is "still running after 15 s": replaying the very same body reads the final answer.
    while ([int]$response.status -eq 202) {
        Start-Sleep -Seconds 3
        $response = Invoke-FieldFace -Field $field -Face Onboard -Method POST -Path '/recovery/requests' `
            -Body ([hashtable]($response.sent | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable))
    }
    $body = Get-FieldProperty $response 'body'
    $answer = "HTTP $($response.status) reasonCode=$(Get-FieldProperty $body 'reasonCode') error=$(Get-FieldProperty $response 'error')"
    $journal.Note("$Label press of ${action}: $answer")
    return $answer
}

# --- 1. 需求出现，车开到取货点 ------------------------------------------------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$intent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'"
        if ($rows.Count -eq 0) { $null } else { $rows[0] }
    } `
    -Until { param($v) [string]$v.Status -eq 'CONFIRMED' }

$journeyId = [string](Get-FieldJourney -Field $field).JourneyId

$journal.Note('Vehicle drives to the pickup station.')
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'
    speed = 0.8; processingOrder = $true; orderTaskId = $intent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.PickupStationRiotId; processingOrder = $false; clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })

# --- 2. 车要子批、给取消按钮；没有人来 ------------------------------------------------------------------------

$request = Wait-FieldSublotRequest -Field $field -JourneyId $journeyId -Sequence 1 -ArrivalTimeoutSeconds 120
$before = Wait-L2Condition -Description 'the vehicle offers sublot entry and 取消装货 at the stop' -Journal $journal `
    -Criterion 'entry-offered' -TimeoutSeconds 60 `
    -Probe { Get-VehicleView } `
    -Until { param($v) $v.CanSubmitSublot -and $v.OffersCancel -and $v.WorklistHasDemand }
$assertions.Add(
    'L2-SST-01', '车到站后要子批、给「取消装货」，当前站清单挂着这单（前提）',
    ($before.CanSubmitSublot -and $before.OffersCancel -and $before.WorklistHasDemand),
    'canSubmitSublot / offersCancel / worklistHasDemand 均为 True', (Format-VehicleView $before))

# --- 3. 等待窗口到期：服务端自己结束需求与旅程 --------------------------------------------------------------

$timeoutSeconds = $request.DeadlineAt `
    ? [int][Math]::Max(60, ([datetimeoffset]$request.DeadlineAt - [DateTimeOffset]::UtcNow).TotalSeconds + 90) `
    : 180
$journal.Note("Nobody scans. Station deadline $($request.DeadlineAt ?? '(not published)'); waiting up to $timeoutSeconds s.")
$settled = Wait-L2Condition -Description 'the station wait expired and ended the demand and the journey' -Journal $journal `
    -Criterion 'station-timeout-settled' -TimeoutSeconds $timeoutSeconds `
    -Probe { Get-FieldDemandSettlement -Field $field -JourneyId $journeyId -DemandId $demandId } `
    -Until { param($v) $v.Status -eq 'Cancelled' -and $v.Suppression -and $v.Position -like '*/Completed' }
$outboxAtCompletion = Get-OutboxCounts
$assertions.Add(
    'L2-SST-02', '站点等待到期，服务端以 CANCELLED_BY_STATION_TIMEOUT 结束需求、旅程 Completed、没发过仓位命令（前提）',
    ($settled.Status -eq 'Cancelled' -and $settled.Suppression -eq 'CANCELLED_BY_STATION_TIMEOUT' -and
        $settled.Operations -eq 0 -and $settled.Position -like '*/Completed'),
    'Cancelled / CANCELLED_BY_STATION_TIMEOUT / 0 / */Completed',
    "$($settled.Status) / $($settled.Suppression) / $($settled.Operations) / $($settled.Position)")

# --- 4. 旅程结束之后，车上那一站该清掉 ----------------------------------------------------------------------

# Ten rounds at the production interval. A snapshot or refusal the server queued on completion would have reached
# the vehicle long before that; whatever still stands afterwards is not in flight.
Wait-RuntimeRounds -Count 10 -Why 'after Completed'
$after = Get-VehicleView
$outboxAfterWait = Get-OutboxCounts
$delta = @(foreach ($type in @($outboxAfterWait.Keys | Sort-Object)) {
    $grown = $outboxAfterWait[$type] - [int]($outboxAtCompletion[$type] ?? 0)
    if ($grown -ne 0) { "$type+$grown" }
}) -join ', '
$journal.Note("Vehicle ten rounds after Completed: $(Format-VehicleView $after)")
$journal.Note("Server queued for the vehicle after Completed: $($delta ? $delta : '(nothing)')")

$assertions.Add(
    'L2-SST-03', '旅程被站点超时结束之后，车不再要子批',
    (-not $after.CanSubmitSublot), 'canSubmitSublot=False', (Format-VehicleView $after))
$assertions.Add(
    'L2-SST-04', '旅程被站点超时结束之后，车不再给「取消装货」',
    (-not $after.OffersCancel), 'offersCancel=False', (Format-VehicleView $after))
$assertions.Add(
    'L2-SST-05', '旅程被站点超时结束之后，车上的当前站清单不再挂着这单',
    (-not $after.WorklistHasDemand), 'worklistHasDemand=False', (Format-VehicleView $after))

# --- 5. 照现场按两下「取消装货」：按几下都不能掉线 ----------------------------------------------------------

$hellosBefore = Get-SessionHelloCount
$firstAnswer = Invoke-CancelPress 'first'
$secondAnswer = Invoke-CancelPress 'second'

# The server drops the connection on the conflicting line itself and the vehicle reconnects within its backoff.
# Eight rounds is well past both, and waiting that long is what gives "no reconnect" its meaning.
Wait-RuntimeRounds -Count 8 -Why 'after the presses'
$hellosAfter = Get-SessionHelloCount
$afterPresses = Get-VehicleView
$journal.Note("Vehicle after both presses: $(Format-VehicleView $afterPresses)")

# foreach, not a pipeline: piping Invoke-L2Query rows joins their string columns (README item 22).
$received = @()
foreach ($row in (Invoke-L2Query -Connection $connection `
        -Sql "SELECT MessageId, RequestJson, FirstResponseJson FROM ProtocolInbox WHERE MessageType = 'LoadCancellationStartRequested' ORDER BY ReceivedAt")) {
    $requestPayload = ([string]$row.RequestJson | ConvertFrom-Json -Depth 32).payload
    $decision = '(no response)'
    if (-not [string]::IsNullOrWhiteSpace([string]$row.FirstResponseJson)) {
        $responsePayload = (([string]$row.FirstResponseJson -split "`n")[0] | ConvertFrom-Json -Depth 32).payload
        $decision = "$(Get-FieldProperty $responsePayload 'decision') $(Get-FieldProperty (Get-FieldProperty $responsePayload 'problem') 'reasonCode')"
    }
    $received += ("messageId=$($row.MessageId) cancellationId=$(Get-FieldProperty $requestPayload 'cancellationId') " +
        "verifiedAt=$(Get-FieldProperty (Get-FieldProperty $requestPayload 'operatorContext') 'verifiedAt') -> $decision")
}
$journal.Note("LoadCancellationStartRequested rows on the server: $($received.Count)")
foreach ($line in $received) { $journal.Note("  $line") }

$assertions.Add(
    'L2-SST-06', '旅程结束之后按两下「取消装货」，服务端不掐连接、车不重连',
    ($hellosAfter -eq $hellosBefore),
    "SessionHello 次数不变（$hellosBefore）",
    "按前 $hellosBefore / 按后 $hellosAfter；第一下 $firstAnswer；第二下 $secondAnswer")

$workflowRows = (Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM RecoveryWorkflows')
$demandRows = (Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'")
$workflowCount = [int]$workflowRows[0].N
$demandStatus = ($demandRows.Count -eq 1) ? [string]$demandRows[0].Status : '(no demand row)'
$assertions.Add(
    'L2-SST-07', '迟到的取消不会被再授权一次：不落取消工作流，需求仍是超时判的 Cancelled',
    ($workflowCount -eq 0 -and $demandStatus -eq 'Cancelled'),
    '0 条工作流 / Cancelled', "$workflowCount 条工作流 / $demandStatus")
