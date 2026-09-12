#Requires -Version 7

<#
补偿清空对账之后，服务端发给车的 `ExceptionRecoverySessionSnapshot` 与 `LoadCompensationCommand` 一直没有被确认
（`ProtocolOutbox.AcknowledgedAt` 为空）。车下一次重连时，`ReplayPendingForSessionAsync` 会不会把它们重放进新会话，
车是收下、忽略还是拒收并撕会话（8005-agv-control-server#31）。

前半段与 `real-onboard-field-operator-compensate` 相同：驱动脚本经车载端自动化面制造真的 `UNKNOWN`、按「补偿清空」、
等对账与会话回到 `Ready`。然后经协议故障代理的 `POST /control/v1/disconnect` 把链路断一次——**不丢 ack**，只断开，
车自己重连。之后发生的一切都是两端出厂代码对「新会话」的处理，不是一条被丢掉的 ack 引出的补发（那是 #30、#33）。

判据只读三处：服务端库、代理快照（每条连接上服务端发过哪些 messageId）、模拟器快照。
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

function Test-L2Null($value) {
    return ($null -eq $value -or $value -is [System.DBNull])
}

function Get-Traffic { return $proxy.Snapshot().body.traffic }

function Get-SessionRow {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT SessionGeneration, Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Format-Session($row) {
    if ($null -eq $row) { return '(no session)' }
    return "gen $($row.SessionGeneration) / $($row.Readiness) / $($row.ReasonCode)"
}

# 补偿会话留下的出站报文：恢复会话快照与补偿命令，按创建先后。
function Get-RecoveryOutbox {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, MessageType, AcknowledgedAt, FencedAt FROM ProtocolOutbox " +
        "WHERE MessageType IN ('ExceptionRecoverySessionSnapshot', 'LoadCompensationCommand') ORDER BY CreatedAt")
    foreach ($row in $rows) {
        [pscustomobject]@{
            MessageId    = ([string]$row.MessageId).ToLowerInvariant()
            MessageType  = [string]$row.MessageType
            Acknowledged = -not (Test-L2Null $row.AcknowledgedAt)
            Fenced       = -not (Test-L2Null $row.FencedAt)
        }
    }
}

function Format-Outbox([object[]]$rows) {
    return (@($rows | ForEach-Object {
        "$($_.MessageType)[$($_.MessageId.Substring(0, 8))] ack=$($_.Acknowledged) fenced=$($_.Fenced)"
    }) -join '; ')
}

function Publish-Demand([string]$sublot) {
    $guid = [guid]::NewGuid()
    $journal.Note("Publishing demand $($guid.ToString('N')) (sublot $sublot).")
    $null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot      = $sublot
        area        = 'N1-3'
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
    return $guid.ToString('D')
}

# 替 RIoT 把车开到取货点。现场由 RIoT 与真车完成，不归驱动脚本。
function Move-VehicleToPickup([string]$demandId) {
    $intent = Wait-L2Condition -Description "the TO_PICKUP intent for $demandId was confirmed" `
        -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 120 `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection `
                -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'"
            if ($rows.Count -eq 0) { $null } else { $rows[0] }
        } `
        -Until { param($v) [string]$v.Status -eq 'CONFIRMED' }

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
}

# --- 0. 车载端确实走代理 ---------------------------------------------------------------------------------

# 少了这一条，「断开没断成」与「车根本没经过代理」在后面的判据上长得一样。
$hellos = Wait-L2Condition -Description 'the onboard session was established through the protocol fault proxy' `
    -Journal $journal -Criterion 'proxy-session' -TimeoutSeconds 30 `
    -Probe { @((Get-Traffic).lines | Where-Object { $_.direction -eq 'onboard->server' -and $_.messageType -eq 'SessionHello' }).Count } `
    -Until { param($v) $v -ge 1 }
$assertions.Add(
    'L2-CR-00', '车载端的会话经协议故障代理建立（否则断开注入不到这条链路上）',
    ($hellos -ge 1), '>= 1 SessionHello through the proxy', $hellos)

# --- 1. 驱动脚本：真的 UNKNOWN、补偿清空、对账 ----------------------------------------------------------

$field = New-FieldOperator -Connection $connection -AgvId $Context.AgvId `
    -SimulatorPort $Context.SimulatorHttpPort -AutomationPort $Context.OnboardAutomationPort `
    -Log { param($message) $journal.Note("field-operator: $message") }.GetNewClosure()

$demandId = Publish-Demand "L2-CR-$($Context.RunId)"
Move-VehicleToPickup $demandId
$journeyId = [string](Get-FieldJourney -Field $field).JourneyId

$unknown = Invoke-FieldActUnknownLoad -Field $field -JourneyId $journeyId -Sequence 1
$compensation = Invoke-FieldActCompensate -Field $field -DemandId $demandId -AttemptId $unknown.AttemptId `
    -Reason 'L2：补偿之后断线重连'
$stage = Wait-L2Condition -Description 'the journey completed on the compensation' -Journal $journal `
    -Criterion 'journey-completed' -TimeoutSeconds 120 `
    -Probe { [string](Get-FieldJourney -Field $field -JourneyId $journeyId).Stage } -Until { param($v) $v -eq 'Completed' }
$assertions.Add(
    'L2-CR-01', '补偿清空走到对账：UNKNOWN 之后 Reconciled / ALL_EMPTY，需求 Cancelled，旅程 Completed',
    ($unknown.Status -eq 'RecoveryRequired' -and $compensation.WorkflowState -eq 'Reconciled' -and
        $compensation.Outcome -eq 'ALL_EMPTY' -and $compensation.DemandStatus -eq 'Cancelled' -and $stage -eq 'Completed'),
    'RecoveryRequired -> Reconciled / ALL_EMPTY / Cancelled / Completed',
    "$($unknown.Status) -> $($compensation.WorkflowState) / $($compensation.Outcome) / $($compensation.DemandStatus) / $stage")

$sessionBefore = $null
try {
    $sessionBefore = Wait-L2Condition -Description 'the session returned to Ready after the compensation' -Journal $journal `
        -Criterion 'session-ready-after-compensation' -TimeoutSeconds 60 `
        -Probe { Get-SessionRow } -Until { param($v) [string]$v.Readiness -eq 'Ready' -and [string]$v.ReasonCode -eq 'READY' }
} catch {
    $journal.Note("Not reached: $($_.Exception.Message)")
}
$assertions.Add(
    'L2-CR-02', '补偿对账之后会话回到 Ready（断开之前的前提）',
    ($null -ne $sessionBefore), 'Ready / READY', (Format-Session ($sessionBefore ?? (Get-SessionRow))))
if ($null -eq $sessionBefore) {
    $journal.Note('Scenario stopped: the session did not return to Ready after the compensation.')
    return
}

# 补偿会话留下的出站报文到这里都该结清了：恢复会话快照由车回 SnapshotAppliedAck(EXCEPTION_RECOVERY_SESSION) 确认，
# 被取代的旧 revision 由服务端 fence；补偿命令由 LoadCompensationResult 结算——协议不许有 LoadCompensationCommandAck。
# 既没确认也没 fence 的行，会被重放进之后的每一个会话（现场 #44 就是这个形状，那次靠 12 号脚本手工结掉）。
try {
    $null = Wait-L2Condition -Description 'the recovery session messages were settled after the compensation' `
        -Journal $journal -Criterion 'recovery-outbox-settled' -TimeoutSeconds 15 `
        -Probe { @(Get-RecoveryOutbox | Where-Object { -not $_.Acknowledged -and -not $_.Fenced }).Count } `
        -Until { param($v) $v -eq 0 }
} catch {
    $journal.Note("Not settled: $($_.Exception.Message)")
}
$recoveryBefore = @(Get-RecoveryOutbox)
$live = @($recoveryBefore | Where-Object { -not $_.Acknowledged -and -not $_.Fenced })
$assertions.Add(
    'L2-CR-03', '补偿对账之后，恢复会话快照与补偿命令都已结清：快照被车确认或被新 revision 取代，命令被补偿结果结算',
    ($recoveryBefore.Count -ge 2 -and $live.Count -eq 0),
    '>= 2 recovery rows, 0 neither acknowledged nor fenced',
    "$($recoveryBefore.Count) recovery rows, $($live.Count) live$(if ($live.Count -gt 0) { " ($(Format-Outbox $live))" })")
$journal.Note("Recovery outbox before the disconnect: $(Format-Outbox $recoveryBefore).")

# --- 2. 断开一次，不丢 ack；车自己重连 --------------------------------------------------------------

$connectionsBefore = @((Get-Traffic).connections)
$lastBefore = [int]$connectionsBefore[-1].connection
$journal.Note("Disconnecting the relay ($($connectionsBefore.Count) connection(s) so far, last #$lastBefore).")
$closed = @($proxy.Command('Post', 'disconnect', @{}).body.connections)
$journal.Note("The relay closed connection(s) $($closed -join ', ').")

$null = Wait-L2Condition -Description 'the onboard reconnected and said SessionHello on a new connection' `
    -Journal $journal -Criterion 'reconnected' -TimeoutSeconds 60 `
    -Probe {
        @((Get-Traffic).lines | Where-Object {
            $_.direction -eq 'onboard->server' -and $_.messageType -eq 'SessionHello' -and [int]$_.connection -gt $lastBefore
        }).Count
    } `
    -Until { param($v) $v -ge 1 }

$sessionAfter = $null
try {
    $sessionAfter = Wait-L2Condition -Description 'the session was Ready again in a newer generation' -Journal $journal `
        -Criterion 'session-ready-after-reconnect' -TimeoutSeconds 60 `
        -Probe { Get-SessionRow } `
        -Until { param($v)
            [long]$v.SessionGeneration -gt [long]$sessionBefore.SessionGeneration -and
                [string]$v.Readiness -eq 'Ready' -and [string]$v.ReasonCode -eq 'READY' }
} catch {
    $journal.Note("Not reached: $($_.Exception.Message)")
}
$assertions.Add(
    'L2-CR-04', '重连之后会话在新世代回到 Ready',
    ($null -ne $sessionAfter), "gen > $($sessionBefore.SessionGeneration) / Ready / READY",
    (Format-Session ($sessionAfter ?? (Get-SessionRow))))

# 给重放与车的应答留出时间：引擎再转几轮，服务端该发的都发了，车要撕会话也撕了。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

# --- 3. 重放了什么 ----------------------------------------------------------------------------------------

$sentAfter = @((Get-Traffic).lines | Where-Object { $_.direction -eq 'server->onboard' -and [int]$_.connection -gt $lastBefore })
$replayed = @($recoveryBefore | Where-Object { $sentAfter.messageId -contains $_.MessageId })
$replayShape = @($replayed | ForEach-Object {
    $id = $_.MessageId
    $on = @($sentAfter | Where-Object { $_.messageId -eq $id } | ForEach-Object { "#$($_.connection)" }) -join ','
    "$($_.MessageType)[$($id.Substring(0, 8))] on $on"
}) -join '; '
$journal.Note("Server->onboard after the disconnect: $(@($sentAfter | ForEach-Object { "#$($_.connection) $($_.messageType)" }) -join ', ').")
$assertions.Add(
    'L2-CR-05', '补偿会话留下的恢复会话快照与补偿命令，一条都没有被重放进新会话',
    ($replayed.Count -eq 0), "0 of $($recoveryBefore.Count) replayed",
    "$($replayed.Count) of $($recoveryBefore.Count) replayed$(if ($replayed.Count -gt 0) { " ($replayShape)" })")
$journal.Note("Recovery outbox after the reconnect: $(Format-Outbox @(Get-RecoveryOutbox)).")

# --- 4. 车还回来：下一条需求被受理，驱动脚本在下一站扫得进码 ---------------------------------------------

$nextDemandId = $null
$nextStop = $null
$nextStopError = $null
if ($null -ne $sessionAfter) {
    $nextSublot = "L2-CR-$($Context.RunId)-NEXT"
    $nextDemandId = Publish-Demand $nextSublot
    try {
        Move-VehicleToPickup $nextDemandId
        $nextJourneyId = [string](Get-L2Journey -Connection $connection -DemandId $nextDemandId)[0].JourneyId
        $nextStop = Start-FieldStopLoad -Field $field -JourneyId $nextJourneyId -Sequence 1 -ArrivalTimeoutSeconds 120
    } catch {
        $nextStopError = $_.Exception.Message
        $journal.Note("Next stop did not start: $nextStopError")
    }
} else {
    $nextStopError = 'the session never came back to Ready after the reconnect'
}
$assertions.Add(
    'L2-CR-06', '重连之后车还接得了单：下一条需求被受理并派车，驱动脚本经自动化面扫码后车开锁等操作员',
    ($null -ne $nextStop -and $nextStop.DemandId -eq $nextDemandId),
    "$nextDemandId / 车在等操作员",
    ($null -ne $nextStop) ? "$($nextStop.DemandId) / 车在等操作员" : "未走到：$nextStopError")

# 在收尾判连接，不在重连刚完成的那一刻（README 第 21 条、real-onboard-durable-ack-lost 的 -002）。
$connections = @((Get-Traffic).connections)
$shape = @($connections | ForEach-Object {
    "#$($_.connection): $(if ($null -eq $_.closedAt) { 'open' } else { $_.closedBy })"
}) -join '; '
$open = @($connections | Where-Object { $null -eq $_.closedAt })
$assertions.Add(
    'L2-CR-07', '断开一次只换来一次重连：之后只有一条连接，而且到收尾还开着（没有哪一端撕会话）',
    ($connections.Count -eq $lastBefore + 1 -and $open.Count -eq 1),
    "$($lastBefore + 1) connections, 1 open",
    "$($connections.Count) connections, $($open.Count) open ($shape)")

$journal.Note('Scenario finished.')
