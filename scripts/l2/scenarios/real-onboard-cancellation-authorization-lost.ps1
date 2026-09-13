#Requires -Version 7

<#
装货途中的取消已经被服务端授权，授权应答却在回车上的路上丢了；操作员再按一次，要拿到同一个授权、把仓位清空，
而不是被掐连接。8005-agv-onboard-hmi#39。

**修之前，这一单只能改库。**车载端取消请求的 messageId 就是由需求与 attempt 算出的 `cancellationId`，每次按下却
重建内容（新的 `sentAt` 与 `verifiedAt`）。服务端这边有两道：`ProtocolInbox` 把 messageId 绑到首次整行字节上，
内容不同就 `ProtocolContentConflictException` 掐连接；换了 messageId，`UpsertSimpleWorkflowAsync` 又拿整个 payload
与首次授权比。服务端的取消工作流停在 `AwaitingResult`，车没写恢复向量、不会清空，每按一次重连一次。修在车载端：
messageId 每次发送新生成；取消请求发出之前把操作员与理由写进车载端日志，未得应答时原样沿用。

**丢应答靠 `tools/ControlServer.ProtocolFaultProxy` 的 `drop-message`**：代理不转发服务端写回的那一条
`LoadCancellationAuthorization`，链路不断。车载端等满 `messageTimeoutMs` 判超时——与应答途中丢失、服务端授权之后
车载端本地抛错是同一个形状。服务端那一侧的授权是真的，车那一侧没收到也是真的。

**走的是在途取消那一路**：扫码、开锁、车在等操作员放料时按「取消装货」，要恢复入口开着。扫码前取消那一路不在
这里：服务端授权的同一次处理就把需求判 `Cancelled` 并结束本站，录入请求随之过期、按钮也就没了——结果已经达成，
只是车以为失败。

判据的核心是 `L2-CAL-02`…`-05`：第二次按下回 200，两条请求 messageId 不同而 payload 相同、都拿到 `AUTHORIZED`，
取消工作流凭空仓对账，车载端从第一次按下到收尾没有重连。
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
$proxy = $Context.ProtocolProxy

Import-Module (Join-Path $Context.Repository 'scripts/field/FieldOperator.psm1') -Force

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-CAL-$($Context.RunId)"
$action = 'LOAD_CANCELLATION'

$field = New-FieldOperator -Connection $connection -AgvId $Context.AgvId `
    -SimulatorPort $Context.SimulatorHttpPort -AutomationPort $Context.OnboardAutomationPort `
    -Log { param($message) $journal.Note("field-operator: $message") }.GetNewClosure()

# Every connection the vehicle opens starts with one SessionHello, so this counts connections.
function Get-SessionHelloCount {
    $rows = @(Invoke-L2Query -Connection $connection `
        -Sql "SELECT COUNT(*) AS N FROM ProtocolInbox WHERE MessageType = 'SessionHello'")
    return [int]$rows[0].N
}

function Get-Traffic { return $proxy.Snapshot().body.traffic }

function Get-CancellationWorkflowState([string]$attemptId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT State FROM RecoveryWorkflows WHERE WorkflowType = 'LOAD_CANCELLATION' AND SlotOperationAttemptId = '$attemptId'"
    if ($rows.Count -eq 0) { return '(no workflow)' }
    return [string]$rows[0].State
}

# Presses the button through the automation face; 202 is "still running after 15 s", and replaying the
# very same body reads the final answer.
function Invoke-CancellationPress([string]$reason) {
    $response = Invoke-FieldFace -Field $field -Face Onboard -Method POST -Path '/recovery/requests' -Envelope -Body @{
        action = $action
        reason = $reason
    }
    while ([int]$response.status -eq 202) {
        Start-Sleep -Seconds 3
        $response = Invoke-FieldFace -Field $field -Face Onboard -Method POST -Path '/recovery/requests' `
            -Body ([hashtable]($response.sent | ConvertTo-Json -Depth 8 | ConvertFrom-Json -AsHashtable))
    }
    return $response
}

function Wait-CancellationOffered([string]$criterion) {
    $null = Wait-L2Condition -Description "the vehicle offers $action while the load is unsettled" -Journal $journal `
        -Criterion $criterion -TimeoutSeconds 60 `
        -Probe { @((Get-FieldOnboardSnapshot -Field $field).state.availableRecoveryActions) -join ',' } `
        -Until { param($a) @("$a" -split ',') -contains $action }
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

# --- 2. 扫码开锁，车在等操作员放料 ------------------------------------------------------------------------

$load = Start-FieldStopLoad -Field $field -JourneyId $journeyId -Sequence 1 -ArrivalTimeoutSeconds 120
Wait-CancellationOffered 'cancellation-offered'

# --- 3. 丢掉授权应答，第一次按下：服务端授权，车等到超时 ------------------------------------------------------

$journal.Note('Arming the proxy: drop the first LoadCancellationAuthorization and keep the link up.')
$null = $proxy.Command('Put', 'drop-message', @{ messageType = 'LoadCancellationAuthorization'; count = 1 })
$hellosBefore = Get-SessionHelloCount

$first = Invoke-CancellationPress 'L2：装货途中取消，第一次按下'
$firstCode = [string](Get-FieldProperty $first.body 'reasonCode')
$journal.Note("First press: HTTP $($first.status) reasonCode=$firstCode")

$dropped = Wait-L2Condition -Description 'the proxy dropped the cancellation authorization' -Journal $journal `
    -Criterion 'authorization-dropped' -TimeoutSeconds 30 `
    -Probe { [string]@((Get-Traffic).drops).Count } -Until { param($n) [int]$n -ge 1 }
$workflowAfterFirst = Get-CancellationWorkflowState $load.AttemptId
$assertions.Add(
    'L2-CAL-01', '服务端授权了第一次取消、工作流在等结果，授权应答被代理丢掉，车载端没拿到回答',
    ([int]$first.status -ne 200 -and $workflowAfterFirst -eq 'AwaitingResult' -and [int]$dropped -eq 1),
    '非 200 / AwaitingResult / 丢 1 条', "$($first.status) $firstCode / $workflowAfterFirst / 丢 $dropped 条")

# --- 4. 仓位是空的，门关上；操作员再按一次 --------------------------------------------------------------------

# Nothing was loaded: the cancellation's clear vector finds an already-empty, closed and locked slot
# and proves ALL_EMPTY without an IO call (Invoke-FieldActUnknownLoad has the measurements).
$null = Invoke-FieldCloseSlot -Field $field -SlotNo $load.SlotNo -Cargo EMPTY
Wait-CancellationOffered 'cancellation-offered-again'

$second = Invoke-CancellationPress 'L2：授权应答丢了之后再按一次'
$secondCode = [string](Get-FieldProperty $second.body 'reasonCode')
$journal.Note("Second press: HTTP $($second.status) reasonCode=$secondCode")
$assertions.Add(
    'L2-CAL-02', '授权应答丢了之后再按一次，拿到同一个授权并清空完成：自动化面回 200',
    ([int]$second.status -eq 200), '200', "$($second.status) $secondCode".Trim())

$settled = '(not reached)'
try {
    $settled = Wait-L2Condition -Description 'the cancellation reconciled and ended the demand' -Journal $journal `
        -Criterion 'cancellation-reconciled' -TimeoutSeconds 120 `
        -Probe {
            $demand = Get-FieldDemandSettlement -Field $field -JourneyId $journeyId -DemandId $demandId
            "$(Get-CancellationWorkflowState $load.AttemptId) / $($demand.Status)"
        } `
        -Until { param($v) $v -eq 'Reconciled / Cancelled' }
} catch {
    $settled = "$(Get-CancellationWorkflowState $load.AttemptId) / $((Get-FieldDemandSettlement -Field $field -JourneyId $journeyId -DemandId $demandId).Status)"
    $journal.Note("Cancellation did not reconcile: $($_.Exception.Message)")
}
$assertions.Add(
    'L2-CAL-03', '取消工作流凭空仓证明对账 Reconciled，需求判 Cancelled',
    ($settled -eq 'Reconciled / Cancelled'), 'Reconciled / Cancelled', $settled)

# --- 5. 两次按下是两条报文，内容相同；服务端一次都没掐连接 ---------------------------------------------------

$requests = Invoke-L2Query -Connection $connection `
    -Sql "SELECT MessageId, RequestJson, FirstResponseJson FROM ProtocolInbox WHERE MessageType = 'LoadCancellationStartRequested' ORDER BY ReceivedAtUtcTicks"
# foreach, not a pipeline: piping Invoke-L2Query rows joins their string columns (README item 22).
$messageIds = @()
$payloads = @()
$decisions = @()
foreach ($row in $requests) {
    $messageIds += [string]$row.MessageId
    # -DateKind String: verifiedAt is compared as the characters the vehicle sent, not as a parsed date.
    $payloads += (([string]$row.RequestJson | ConvertFrom-Json -DateKind String).payload | ConvertTo-Json -Depth 10 -Compress)
    $answer = ([string]$row.FirstResponseJson -split "`n")[0] | ConvertFrom-Json -DateKind String
    $decisions += [string]$answer.payload.decision
}
$distinctIds = @($messageIds | Sort-Object -Unique).Count
$samePayload = ($payloads.Count -eq 2 -and $payloads[0] -eq $payloads[1])
$assertions.Add(
    'L2-CAL-04', '两次按下是两条 messageId 不同的取消请求，payload 相同（沿用首发的操作员与理由），两次都拿到 AUTHORIZED',
    ($messageIds.Count -eq 2 -and $distinctIds -eq 2 -and $samePayload -and ($decisions -join ',') -eq 'AUTHORIZED,AUTHORIZED'),
    '2 条 / 2 个 id / payload 相同 / AUTHORIZED,AUTHORIZED',
    "$($messageIds.Count) 条 / $distinctIds 个 id / payload $($samePayload ? '相同' : '不同') / $($decisions -join ',')")
if (-not $samePayload -and $payloads.Count -gt 0) {
    $journal.Note("Request payloads: $($payloads -join ' || ')")
}

# Judged at the end, so a dropped connection has had seconds to come back as a fresh SessionHello. When the
# cancellation did not reconcile, give the vehicle its reconnect before judging (the reason
# real-onboard-recovery-retry-after-refusal gives for the same wait).
if ($settled -ne 'Reconciled / Cancelled') {
    try {
        $null = Wait-L2Condition -Description 'the vehicle reconnected after the second press' -Journal $journal `
            -Criterion 'vehicle-reconnected' -TimeoutSeconds 20 `
            -Probe { Get-SessionHelloCount } -Until { param($n) [int]$n -gt $hellosBefore }
    } catch {
        $journal.Note("No reconnect within 20 s: $($_.Exception.Message)")
    }
}
$hellosAfter = Get-SessionHelloCount
$assertions.Add(
    'L2-CAL-05', '从第一次按下到收尾，车载端没有重连：丢一次应答不换来一次掐连接',
    ($hellosAfter -eq $hellosBefore), "SessionHello $hellosBefore 条不变", "SessionHello $hellosBefore → $hellosAfter 条")

$slot = Get-FieldSimulatorSlot -Field $field -SlotNo $load.SlotNo
$reading = "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
$assertions.Add(
    'L2-CAL-06', '现场收在安全状态：门关、仓空、已锁、开锁输出复位',
    ($reading -eq 'CLOSED/EMPTY/1/0'), 'CLOSED/EMPTY/1/0', $reading)

$journal.Note("Journey after the cancellation: $((Get-FieldDemandSettlement -Field $field -JourneyId $journeyId -DemandId $demandId).Position)")
$journal.Note('Scenario finished.')
