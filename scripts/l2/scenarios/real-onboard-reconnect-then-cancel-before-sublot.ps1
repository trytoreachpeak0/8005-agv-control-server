#Requires -Version 7

<#
车在开往取货站的途中重连一次，到站之后操作员扫码前「取消装货」：那一轮 SublotEntryRequested 要被结算
（8005-agv-control-server#40）。

服务端每条 TCP 连接只有一个 DbContext。重连的握手把这趟旅程的当前 stop 带跟踪读进这条连接的上下文，那时它还是
LoadRound 0；车到站后引擎在自己的上下文里把它改成第 1 轮并发出条码录入请求。同一条连接上随后到来的取消，按连接里
那份旧 stop 判轮次，第 0 轮没有请求可结算，第 1 轮那条于是一直挂着（#28 的普查与 L1 复现）。旅程照样 Completed、需求
照样 Cancelled——症状只在发件箱里。

所有前面的场景里车都在旅程开始之前连上，握手时没有活动旅程可读，所以一次都没撞上。这里用协议故障代理的
POST /control/v1/disconnect 在车到站之前断一次（不丢 ack），让握手落在「旅程已受理、stop 还没到站」那一格。

判据只读服务端库与代理快照。
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

# 旅程与它第 1 个停靠在同一次查询里读：阶段与轮次要一起判。
function Get-JourneyAtFirstStop([string]$demandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT r.JourneyId, r.Stage, r.BlockReasonCode, r.CurrentStopSequence, s.Sequence AS StopSequence, s.LoadRound " +
        "FROM JourneyDemands d JOIN JourneyRuntimes r ON r.JourneyId = d.JourneyId " +
        "LEFT JOIN JourneyStops s ON s.JourneyId = r.JourneyId AND s.Sequence = d.StopSequence " +
        "WHERE d.DemandId = '$demandId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-OutboxRow([string]$messageId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT MessageId, MessageType, AcknowledgedAt, FencedAt FROM ProtocolOutbox WHERE MessageId = '$messageId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Format-OutboxRow($row) {
    if ($null -eq $row) { return '(no outbox row)' }
    $ack = (Test-L2Null $row.AcknowledgedAt) ? 'unacknowledged' : "AcknowledgedAt=$($row.AcknowledgedAt)"
    $fence = (Test-L2Null $row.FencedAt) ? '' : " FencedAt=$($row.FencedAt)"
    return "$($row.MessageType) $ack$fence"
}

# --- 0. 车载端确实走代理 ---------------------------------------------------------------------------------

$hellos = Wait-L2Condition -Description 'the onboard session was established through the protocol fault proxy' `
    -Journal $journal -Criterion 'proxy-session' -TimeoutSeconds 30 `
    -Probe { @((Get-Traffic).lines | Where-Object { $_.direction -eq 'onboard->server' -and $_.messageType -eq 'SessionHello' }).Count } `
    -Until { param($v) $v -ge 1 }
$assertions.Add(
    'L2-RCS-00', '车载端的会话经协议故障代理建立（否则断开注入不到这条链路上）',
    ($hellos -ge 1), '>= 1 SessionHello through the proxy', $hellos)

$sessionBefore = Wait-L2Condition -Description 'the first session is Ready' -Journal $journal `
    -Criterion 'session-ready' -TimeoutSeconds 60 `
    -Probe { Get-SessionRow } -Until { param($v) [string]$v.Readiness -eq 'Ready' -and [string]$v.ReasonCode -eq 'READY' }

# --- 1. 受理一单，车还没到站 ------------------------------------------------------------------------------

$field = New-FieldOperator -Connection $connection -AgvId $Context.AgvId `
    -SimulatorPort $Context.SimulatorHttpPort -AutomationPort $Context.OnboardAutomationPort `
    -Log { param($message) $journal.Note("field-operator: $message") }.GetNewClosure()

$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')
$journal.Note("Publishing demand $($demandGuid.ToString('N')).")
$null = $mes.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot      = "L2-RCS-$($Context.RunId)"
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
$journeyId = [string](Get-JourneyAtFirstStop $demandId).JourneyId

# --- 2. 到站之前断开一次，不丢 ack；车自己重连 ------------------------------------------------------------

$connectionsBefore = @((Get-Traffic).connections)
$lastBefore = [int]$connectionsBefore[-1].connection
$journal.Note("Disconnecting the relay before arrival ($($connectionsBefore.Count) connection(s) so far, last #$lastBefore).")
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

$sessionAfter = Wait-L2Condition -Description 'the session was Ready again in a newer generation' -Journal $journal `
    -Criterion 'session-ready-after-reconnect' -TimeoutSeconds 60 `
    -Probe { Get-SessionRow } `
    -Until { param($v)
        [long]$v.SessionGeneration -gt [long]$sessionBefore.SessionGeneration -and
            [string]$v.Readiness -eq 'Ready' -and [string]$v.ReasonCode -eq 'READY' }
$reconnection = [int]@((Get-Traffic).lines | Where-Object {
    $_.direction -eq 'onboard->server' -and $_.messageType -eq 'SessionHello' -and [int]$_.connection -gt $lastBefore
})[0].connection

# 车还没动过：RIoT 没报到站，引擎推不了 stop。握手读进连接的就是这一刻的 stop。
$atHandshake = Get-JourneyAtFirstStop $demandId
$assertions.Add(
    'L2-RCS-01', '重连握手时旅程已受理、车还没到站：新世代 Ready，stop 仍是第 0 轮',
    ([string]$atHandshake.Stage -eq 'AwaitingPickupArrival' -and [int]$atHandshake.LoadRound -eq 0),
    "$(Format-Session $sessionAfter) / AwaitingPickupArrival / LoadRound 0",
    "$(Format-Session $sessionAfter) / $($atHandshake.Stage) / LoadRound $($atHandshake.LoadRound)")

# --- 3. 到站，引擎发出第 1 轮条码录入请求 -----------------------------------------------------------------

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

# 服务端到 AwaitingSublot、车上亮出这张单的录入请求。
$request = Wait-FieldSublotRequest -Field $field -JourneyId $journeyId -Sequence 1 -ArrivalTimeoutSeconds 120
# 阶段、轮次与请求是引擎同一次提交写下的（README 第 14 条的写入边界），等到阶段之后读是安全的。
# 请求编号由服务端按停靠与轮次派生（WireToGateStore.SublotRequestId），表里没有这一列：-001 就红在当成列去读。
$asked = Get-JourneyAtFirstStop $demandId
$requestId = Get-L2DeterministicId -Value "$journeyId|stop-$($asked.StopSequence)|sublot-request-$($asked.LoadRound)"
$pendingBefore = Get-OutboxRow $requestId
$assertions.Add(
    'L2-RCS-02', '到站之后引擎发出第 1 轮条码录入请求，取消之前它还没被结算',
    ([string]$asked.Stage -eq 'AwaitingSublot' -and [int]$asked.LoadRound -eq 1 -and $null -ne $pendingBefore -and
        [string]$pendingBefore.MessageType -eq 'SublotEntryRequested' -and (Test-L2Null $pendingBefore.AcknowledgedAt)),
    'AwaitingSublot / LoadRound 1 / SublotEntryRequested unacknowledged',
    "$($asked.Stage) / LoadRound $($asked.LoadRound) / $(Format-OutboxRow $pendingBefore)")

# --- 4. 扫码前取消 ------------------------------------------------------------------------------------------

$actX = Invoke-FieldActCancelBeforeSublot -Field $field -JourneyId $journeyId -Sequence 1 `
    -Reason 'L2：重连之后扫码前取消'

# 取消必须落在握手的那条连接上，否则这一幕问不出东西（换了连接就换了上下文）。
$cancelLines = @((Get-Traffic).lines | Where-Object {
    $_.direction -eq 'onboard->server' -and $_.messageType -eq 'LoadCancellationStartRequested'
})
$cancelConnections = @($cancelLines | ForEach-Object { [int]$_.connection } | Sort-Object -Unique)
$assertions.Add(
    'L2-RCS-03', '取消请求在重连握手的同一条连接上发出，需求 Cancelled 并以 CANCELLED_BY_OPERATOR 抑制',
    ($cancelConnections.Count -eq 1 -and $cancelConnections[0] -eq $reconnection -and
        $actX.Status -eq 'Cancelled' -and $actX.Suppression -eq 'CANCELLED_BY_OPERATOR'),
    "#$reconnection / Cancelled / CANCELLED_BY_OPERATOR",
    "#$($cancelConnections -join ',') / $($actX.Status) / $($actX.Suppression)（面答 $($actX.FaceAnswer)）")

# 需求 Cancelled、旅程 Completed、条码录入请求结算是 CancelDemandBeforeLoadAsync 同一次提交（README 第 14 条），
# 驱动脚本等到了前两样，第三样直读。
$after = Get-JourneyAtFirstStop $demandId
$assertions.Add(
    'L2-RCS-04', '单需求旅程以 CANCELLED_BY_OPERATOR 结束',
    ([string]$after.Stage -eq 'Completed' -and [string]$after.BlockReasonCode -eq 'CANCELLED_BY_OPERATOR'),
    'Completed / CANCELLED_BY_OPERATOR', "$($after.Stage) / $($after.BlockReasonCode)")

$settled = Get-OutboxRow $requestId
$assertions.Add(
    'L2-RCS-05', '第 1 轮条码录入请求被这次取消结算（#40：连接里握手读进来的旧 stop 让它漏掉）',
    ($null -ne $settled -and -not (Test-L2Null $settled.AcknowledgedAt)),
    'SublotEntryRequested acknowledged', (Format-OutboxRow $settled))

# 在收尾判连接，不在重连刚完成的那一刻（README 第 21 条）。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$connections = @((Get-Traffic).connections)
$shape = @($connections | ForEach-Object {
    "#$($_.connection): $(if ($null -eq $_.closedAt) { 'open' } else { $_.closedBy })"
}) -join '; '
$open = @($connections | Where-Object { $null -eq $_.closedAt })
$assertions.Add(
    'L2-RCS-06', '断开一次只换来一次重连：之后只有一条连接，而且到收尾还开着（没有哪一端撕会话）',
    ($connections.Count -eq $lastBefore + 1 -and $open.Count -eq 1),
    "$($lastBefore + 1) connections, 1 open",
    "$($connections.Count) connections, $($open.Count) open ($shape)")

$journal.Note('Scenario finished.')
