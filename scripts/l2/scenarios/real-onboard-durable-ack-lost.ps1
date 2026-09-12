#Requires -Version 7

<#
真车载端的装载结果已经被服务端收下，`DurableAck` 却在回车上的路上丢了：车重连之后补发同一条
`OperationResult`，服务端要认出这是同一条消息并确认它，而不是掐连接（8005-agv-control-server#30）。

**车载端补发的形状是固定的**：持久出站报文先写 journal 再发，收到 ack 才标已确认；下一次连接在
`SessionAccepted` 之后、任何快照之前逐条补发未确认的行，`messageId` 与 `sentAt` 不变，只把
`sessionGeneration` 换成新会话的（`WireToGateSessionClient.ReplayDurableOutgoingAsync` →
`RebindSessionGeneration`）。这正是 ADR-cross-0030 要求的「重连补发同一消息时沿用原编号」。

**丢 ack 靠 `tools/ControlServer.ProtocolFaultProxy`**：车载端连代理，代理逐行转发给服务端；场景布下
「丢一次 `acceptedMessageType = OperationResult` 的 `DurableAck`」，代理在服务端写出那条 ack 时不转发、
两头都断。服务端那一侧的提交是真的，车那一侧没收到 ack 也是真的——从两端看，这就是提交之后链路掉了。

业务链路与 `real-onboard-normal-load` 相同，只在装载结果那一处注入。判据只从三处读：服务端库、代理的
`/snapshot`（每条连接上走过哪些报文，只有信封身份）、模拟器的 `/snapshot`。
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
$simulator = $Context.Simulator
$connection = $Context.Connection
$proxy = $Context.ProtocolProxy

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

function Get-Stage {
    $rows = Get-L2Journey -Connection $connection -DemandId $demandId
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Slot([int]$slotNo) {
    return $simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $slotNo }
}

function Get-AttemptId([string]$operationType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT SlotOperationAttemptId FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = '$operationType'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].SlotOperationAttemptId
}

function Get-OperationStatus([string]$operationType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Status FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = '$operationType'"
    if ($rows.Count -ne 1) { return $null }
    return [string]$rows[0].Status
}

function Get-Progress([string]$attemptId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT RequestJson FROM ProtocolInbox WHERE MessageType = 'OperationProgress' ORDER BY ReceivedAt"
    $matched = foreach ($row in $rows) {
        $payload = ([string]$row.RequestJson | ConvertFrom-Json).payload
        if ($payload.slotOperationAttemptId -eq $attemptId) { $payload }
    }
    return @($matched)
}

# 代理记下的流量：每条连接、每一行的方向与信封身份。
function Get-Traffic { return $proxy.Snapshot().body.traffic }

function Get-ProxyLines([string]$direction, [string]$messageType) {
    return @((Get-Traffic).lines | Where-Object { $_.direction -eq $direction -and $_.messageType -eq $messageType })
}

<#
丢 ack 之后的连接各自是怎么结束的。判在场景收尾（或补发判红之后），不在补发刚被确认的那一刻：`-002` 在那一刻
取样，读到「2 条连接、0 条开着」，而车 2 秒之后就开了第 3 条——条件在读的那一刻碰巧成立，与 README 第 21 条
同一类错误。

两条判据分属两个缺陷，所以分开判：
- `L2-DA-04` 是 #30：服务端掐连接。丢 ack 之后只要有一条连接不是车自己关的、也不是到收尾还开着，就是红。
- `L2-DA-07` 是补发之后车等不到 `SessionReadiness`、超时自己断开再完整握手一次：多出来的那次重连。
#>
function Add-ConnectionAssertions([int]$dropConnection) {
    $connections = @((Get-Traffic).connections)
    $shape = @($connections | ForEach-Object {
        "#$($_.connection): $(if ($null -eq $_.closedAt) { 'open' } else { $_.closedBy })"
    }) -join '; '
    $cutByPeer = @($connections | Where-Object {
        [int]$_.connection -gt $dropConnection -and $null -ne $_.closedAt -and $_.closedBy -ne 'onboard closed'
    })
    $assertions.Add(
        'L2-DA-04', '补发之后服务端不再掐连接：丢 ack 之后没有一条连接是被服务端关掉的',
        ($cutByPeer.Count -eq 0), '0 connections after the drop ended by anyone but the onboard',
        "$($cutByPeer.Count) ($shape)")

    $open = @($connections | Where-Object { $null -eq $_.closedAt })
    $assertions.Add(
        'L2-DA-07', '丢一次 ack 只换来一次重连：之后只有一条连接，而且到收尾还开着',
        ($connections.Count -eq $dropConnection + 1 -and $open.Count -eq 1),
        "$($dropConnection + 1) connections, 1 open",
        "$($connections.Count) connections, $($open.Count) open ($shape)")
}

# 与 real-onboard-normal-load 同一个驱动：等车载端自己发出 WAITING_OPERATOR 才动货和门，理由见那边。
function Invoke-SlotOperation([string]$operationType, [string]$cargoState) {
    $attemptId = Wait-L2Condition -Description "the server issued the $operationType command" `
        -Journal $journal -Criterion "$operationType-attempt" -TimeoutSeconds 180 `
        -Probe { Get-AttemptId $operationType } -Until { param($v) $v }

    $unlocking = Wait-L2Condition -Description "the onboard started unlocking for the $operationType" `
        -Journal $journal -Criterion "$operationType-unlocking" -TimeoutSeconds 120 `
        -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'UNLOCKING' })[0] } `
        -Until { param($v) $v }
    $slots = @($unlocking.activeUnlockSlots)
    if ($slots.Count -ne 1) {
        throw ("This scenario drives one slot per operation; the $operationType command targets " +
            "$($slots.Count) ($($slots -join ', ')).")
    }
    $slotNo = [int]$slots[0]

    $null = Wait-L2Condition -Description "the onboard is waiting for the operator on slot $slotNo" `
        -Journal $journal -Criterion "$operationType-waiting-operator" -TimeoutSeconds 120 `
        -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'WAITING_OPERATOR' }).Count } `
        -Until { param($v) $v -ge 1 }

    $journal.Note("Onboard is waiting on slot $slotNo; setting cargo to $cargoState.")
    $null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = $cargoState })
    $null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})

    return [pscustomobject]@{ AttemptId = $attemptId; SlotNo = $slotNo }
}

function Move-VehicleTo([object]$intent, [int]$stationRiotId, [string]$label) {
    $journal.Note("Vehicle departs for $label.")
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
    $journal.Note("Vehicle arrives at $label and comes to rest.")
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

# --- 0. 车载端确实走代理，然后布下丢 ack 的计划 -------------------------------------------------------

# 少了这一条，「没丢成」与「车根本没经过代理」在后面的判据上长得一样。
$hellos = Wait-L2Condition -Description 'the onboard session was established through the protocol fault proxy' `
    -Journal $journal -Criterion 'proxy-session' -TimeoutSeconds 30 `
    -Probe { @(Get-ProxyLines 'onboard->server' 'SessionHello').Count } -Until { param($v) $v -ge 1 }
$assertions.Add(
    'L2-DA-00', '车载端的会话经协议故障代理建立（否则丢 ack 注入不到这条链路上）',
    ($hellos -ge 1), '>= 1 SessionHello through the proxy', $hellos)

$journal.Note('Arming the proxy: drop the first DurableAck for an OperationResult and close that connection.')
$null = $proxy.Command('Put', 'drop-durable-ack', @{ acceptedMessageType = 'OperationResult'; count = 1 })

# --- 1. 需求受理、车到取货点、UIA 扫码 ---------------------------------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot) to the fake MesIngest catalog.")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$null = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-UpperId -purpose 'TO_PICKUP'; if ($row) { [string]$row.Status } else { $null } } `
    -Until { param($v) $v -eq 'CONFIRMED' }
Move-VehicleTo -intent (Get-UpperId -purpose 'TO_PICKUP') -stationRiotId $Context.PickupStationRiotId -label 'the pickup station'

$null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
$onboard.SetSublot($sublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()
$journal.Note('Manual submit invoked.')

# --- 2. 装载；它的结果被服务端收下，ack 在代理处丢掉 ---------------------------------------------------

$load = Invoke-SlotOperation -operationType 'Load' -cargoState 'OCCUPIED'

$drop = Wait-L2Condition -Description 'the proxy dropped the DurableAck for the load result' `
    -Journal $journal -Criterion 'ack-dropped' -TimeoutSeconds 120 `
    -Probe { $drops = @((Get-Traffic).drops); if ($drops.Count -gt 0) { $drops[0] } else { $null } } `
    -Until { param($v) $v }
$resultId = [string]$drop.acceptedMessageId
$dropConnection = [int]$drop.connection
$journal.Note("Dropped the DurableAck for OperationResult $resultId on connection $dropConnection.")

# 服务端在写出 ack 之前已经提交（CaptureFirstResponseAsync 先提交事务再返回应答），这里读到的是那次提交。
$loadStatus = Wait-L2Condition -Description 'the load result is committed on the server' `
    -Journal $journal -Criterion 'load-committed' -TimeoutSeconds 30 `
    -Probe { Get-OperationStatus 'Load' } -Until { param($v) $v -eq 'Committed' }
$acceptedRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT MessageType FROM ProtocolInbox WHERE MessageId = '$resultId'"
$assertions.Add(
    'L2-DA-01', '丢掉的是一份服务端已经收下的装载结果：ProtocolInbox 有这一行，装载已 Committed',
    ($acceptedRows.Count -eq 1 -and [string]$acceptedRows[0].MessageType -eq 'OperationResult' -and
        $loadStatus -eq 'Committed' -and $load.AttemptId),
    'OperationResult / Committed',
    "$(if ($acceptedRows.Count -eq 1) { [string]$acceptedRows[0].MessageType } else { "$($acceptedRows.Count) rows" }) / $loadStatus")

# --- 3. 车重连并补发；补发要被确认 -----------------------------------------------------------------------

$replayAcked = $true
try {
    $null = Wait-L2Condition `
        -Description "the replayed OperationResult $resultId was acknowledged on a later connection" `
        -Journal $journal -Criterion 'replay-acknowledged' -TimeoutSeconds 60 `
        -Probe {
            @(Get-ProxyLines 'server->onboard' 'DurableAck' | Where-Object {
                $_.correlationId -eq $resultId -and [int]$_.connection -gt $dropConnection -and -not $_.dropped
            }).Count
        } `
        -Until { param($v) $v -ge 1 }
} catch {
    $replayAcked = $false
    $journal.Note("The replay was never acknowledged: $($_.Exception.Message)")
}

$sends = @(Get-ProxyLines 'onboard->server' 'OperationResult' | Where-Object { $_.messageId -eq $resultId })
$firstSend = @($sends | Where-Object { [int]$_.connection -eq $dropConnection })
$replays = @($sends | Where-Object { [int]$_.connection -gt $dropConnection })
$replayShape = if ($firstSend.Count -eq 1 -and $replays.Count -ge 1) {
    "first on connection $dropConnection at generation $($firstSend[0].sessionGeneration); " +
    "$($replays.Count) replay(s), first on connection $($replays[0].connection) at generation $($replays[0].sessionGeneration)"
} else {
    "$($firstSend.Count) first send(s), $($replays.Count) replay(s)"
}
$assertions.Add(
    'L2-DA-02', '车重连后在新会话里补发同一 messageId 的 OperationResult，sessionGeneration 换成新的',
    ($firstSend.Count -eq 1 -and $replays.Count -ge 1 -and
        [long]$replays[0].sessionGeneration -gt [long]$firstSend[0].sessionGeneration),
    'same messageId replayed at a newer generation', $replayShape)

$assertions.Add(
    'L2-DA-03', '补发的 OperationResult 被服务端以 DurableAck 确认（不是掐连接）',
    $replayAcked, "DurableAck for $resultId on a connection after $dropConnection",
    $(if ($replayAcked) { 'acknowledged' } else { 'no acknowledgement within 60 s' }))

# 服务端日志只做诊断，不当判据：判据读的是库和代理。
$logRoot = Join-Path (Split-Path -Parent $Context.SnapshotRoot) 'logs'
$conflicts = @(Get-ChildItem -LiteralPath $logRoot -Filter 'control-server*.log' |
    Select-String -SimpleMatch 'MessageId was replayed with different normalized content').Count
$journal.Note("ControlServer logged $conflicts 'MessageId was replayed with different normalized content' conflict(s).")

if (-not $replayAcked) {
    Add-ConnectionAssertions -dropConnection $dropConnection
    $assertions.Add(
        'L2-DA-05', '补发被确认之后旅程照常走完', $false, 'Completed',
        "(not reached: the replay was never acknowledged; stage $(Get-Stage))")
    $journal.Note('Scenario stopped after the replay verdict.')
    return
}

# --- 4. 旅程照常走完，结果只记了一次 ---------------------------------------------------------------------

$null = Wait-L2Condition -Description 'the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
Move-VehicleTo -intent (Get-UpperId -purpose 'TO_GATE') -stationRiotId $Context.GateStationRiotId -label 'the gate'

$unload = Invoke-SlotOperation -operationType 'Unload' -cargoState 'EMPTY'
$null = Wait-L2Condition -Description 'the unloaded slot is closed, locked and empty' `
    -Journal $journal -Criterion 'unload-slot-physical' -TimeoutSeconds 60 `
    -Probe {
        $slot = Get-Slot $unload.SlotNo
        "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
    } `
    -Until { param($v) $v -eq 'CLOSED/EMPTY/1/0' }

$stage = Wait-L2Condition -Description 'the journey completed at the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'Completed' }
$assertions.Add('L2-DA-05', '补发被确认之后旅程照常走完', ($stage -eq 'Completed'), 'Completed', $stage)

$loadResults = Invoke-L2Query -Connection $connection `
    -Sql "SELECT COUNT(*) AS N FROM OperationResults WHERE SlotOperationAttemptId = '$($load.AttemptId)'"
$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
$unloadStatus = Get-OperationStatus 'Unload'
$demandStatus = if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' }
$assertions.Add(
    'L2-DA-06', '装载结果只记了一次，卸载 Committed，需求 Succeeded',
    ([long]$loadResults[0].N -eq 1 -and $unloadStatus -eq 'Committed' -and $demandStatus -eq 'Succeeded'),
    '1 / Committed / Succeeded',
    "$([long]$loadResults[0].N) / $unloadStatus / $demandStatus")

Add-ConnectionAssertions -dropConnection $dropConnection

$journal.Note('Scenario finished.')
