#Requires -Version 7

<#
车载端静默失联：连接没断、报文停了（批次 7，control-server#234，ADR-cross-0027）。

这条场景钉的是此前没人看得见的一格。车载端进程卡死而 TCP 还在时，服务端没有任何判定——收到 Heartbeat
只回 HeartbeatAck，WireToGateStore.RecordConnectionLossAsync 一个产品调用方都没有，连接断开那条路径
（OnboardTcpServer.HandleClientAsync 的 finally）也只把连接从 OnboardPeer 上摘掉、不写库。于是会话行
一直是 Ready，到站永远不可信，AwaitingPickupArrival 每轮走完 ObserveOrderFailureAsync 与
NameCheckpointWaitAsync 就返回，阻断码为空——而看板阻断端点只列码非空的行。车静默卡住，看板上没有卡片。

场景用假车载端新加的 PUT /control/v1/silence 制造这一格：它保持 socket 不动，把自己所有出站报文丢掉
（wire 日志记成 dropped）。那不是 PUT /connection，后者是拔网线。

四段：

  A. 静默之后服务端自己关掉这条连接（ADR-cross-0027 的六秒），假车载端因此读到 EOF。
  B. 另等第二个事实：在途旅程挂上 ONBOARD_SESSION_LOST，看板阻断端点列出它，直接进最高档。
  C. 只亮卡、只升级告警（REQ-0287）：阶段不动、需求还是 Accepted、租约没释放、订单命令面一次都没调过。
     本批不发 OrderHold（用户 2026-09-20 定，ADR-cross-0026 与 REQ-0287 的冲突留到批次 9）。
  D. 车重新说话、重连，旅程接着走完到站与装货。

  D 段里有一个值得知道的细节，不是缺陷：重连开新代次，会话先回到 RecoveryRequired，引擎那条既有分支
  会把 ONBOARD_SESSION_LOST 覆盖成 ONBOARD_SESSION_NOT_READY；到站换段时 SetStage 才把码清掉。所以这
  一段断言的是「不再是失联码」与「到站之后码清空」，而不是「重连当场清掉失联码」。

所有「对端收到了什么」的判据都先等它出现再断言（Wait-L2Condition／Wait-L2Change），不读一次就下结论。
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
$endpoint = "http://127.0.0.1:$($Context.HealthPort)/api/dashboard/blocked-journeys"
# Wait-L2Condition 把 $null 探针读成「还没观测到」，所以「不在端点上」用这个标记表示。
$notListed = '(not listed)'
$lostReason = 'ONBOARD_SESSION_LOST'

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

function Get-Runtime {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Stage, BlockReasonCode, BlockReasonSince, UpdatedAt FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stage {
    $runtime = Get-Runtime
    if ($null -eq $runtime) { return $null }
    return [string]$runtime.Stage
}

function Get-DemandStatus {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Status
}

function Get-OrderCommandCount {
    $rows = Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM RiotOrderCommandAudit'
    return [int]$rows[0].N
}

function Get-ReleasedLeaseCount {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql 'SELECT COUNT(*) AS N FROM VehicleDispatchLeases WHERE ReleasedAt IS NOT NULL'
    return [int]$rows[0].N
}

# 端点里这条需求的那一行；不在就是 $null。-DateKind String：开始时间原样比较，不经本地时区换算。
function Get-BlockedEntry {
    $response = Invoke-WebRequest -Uri $endpoint -NoProxy -TimeoutSec 10
    $fact = $response.Content | ConvertFrom-Json -DateKind String
    return @($fact.journeys | Where-Object { $_.demandId -eq $demandId }) | Select-Object -First 1
}

# 库里的时间列是 SQLite 文本（「2026-09-20 06:42:20.7861062+00:00」），端点给的是 ISO-8601（带 T）。
# 同一个时刻，两种写法，所以比较之前都解析成 DateTimeOffset，不拿字符串直接比。
function ConvertTo-Instant([object]$Value) {
    if ($null -eq $Value -or $Value -is [DBNull] -or [string]::IsNullOrWhiteSpace([string]$Value)) { return $null }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture)
}

function Format-Entry([object]$Entry) {
    if ($null -eq $Entry) { return $notListed }
    return "$($Entry.blockReasonCode) since $($Entry.blockReasonSince) ($($Entry.blockedSeconds) s, $($Entry.escalationLevel), $($Entry.stage))"
}

function Get-PeerReadiness {
    return [string]$onboard.Snapshot().body.readiness
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# --- 0. 派车到取货站的路上 --------------------------------------------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-UpperId -purpose 'TO_PICKUP'; if ($row -and $row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }

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

# 行驶中没有任何阻断：这是下面两段的基线，也是「端点此刻不列这条旅程」的出处。
$beforeSilence = Get-BlockedEntry
$orderCommandsBefore = Get-OrderCommandCount
$assertions.Add(
    'L2-SL-01', '车在路上、会话正常时端点不列这条旅程',
    ($null -eq $beforeSilence), $notListed, (Format-Entry $beforeSilence))

# --- A. 静默：连接没断，服务端自己把它关掉 -------------------------------------------------------------

$closed = Wait-L2Change -Description 'the server closes the connection after the peer goes silent' `
    -Journal $journal -Criterion 'server-closed-silent-connection' -TimeoutSeconds 60 `
    -Baseline { Get-PeerReadiness } `
    -Action {
        $journal.Note('Onboard process hangs: it stops sending anything and leaves the socket open.')
        $null = $onboard.Command('Put', 'silence', @{ silent = $true })
    } `
    -Probe { Get-PeerReadiness } `
    -Until { param($before, $now) $before -eq 'READY' -and $now -eq 'DISCONNECTED' }
$assertions.Add(
    'L2-SL-02', '车载端停发但不断线，服务端按 ADR-cross-0027 自己关掉这条连接（对端读到 EOF）',
    ($closed.Baseline -eq 'READY' -and $closed.Value -eq 'DISCONNECTED'),
    'READY -> DISCONNECTED', "$($closed.Baseline) -> $($closed.Value)")

# 车确实一条都没发出去：wire 日志把丢掉的那些记成 dropped。
$dropped = @($onboard.Snapshot().body.wire | Where-Object { $_.direction -eq 'dropped' })
$assertions.Add(
    'L2-SL-03', '静默期间车载端本该发出的报文被丢掉，不是「它本来就没话说」',
    ($dropped.Count -gt 0), '至少一条 dropped', "$($dropped.Count) 条 dropped")

# --- B. 另等第二个事实：在途旅程挂上阻断码，看板列得出来 ------------------------------------------------

# 关连接与挂阻断码是两次不同的写入：前者在会话层，后者是运行时下一轮的事。所以这里另等，不在上面那一步之后直读。
$listed = Wait-L2Condition -Description 'the blocked-journey endpoint lists the journey behind the silent session' `
    -Journal $journal -Criterion 'endpoint-session-lost' -TimeoutSeconds 60 `
    -Probe { Get-BlockedEntry } `
    -Until { param($v) $null -ne $v -and $v.blockReasonCode -eq $lostReason }
$runtime = Get-Runtime
$listedSince = ConvertTo-Instant $listed.blockReasonSince
$assertions.Add(
    'L2-SL-04', '失联期间在途旅程挂上 ONBOARD_SESSION_LOST，端点列出这条旅程、带开始时间，开始时间就是库里记下的那一个',
    ($listed.blockReasonCode -eq $lostReason -and
        $null -ne $listedSince -and
        $listedSince -eq (ConvertTo-Instant $runtime.BlockReasonSince) -and
        [long]$listed.blockedSeconds -ge 0),
    "$lostReason since $($runtime.BlockReasonSince)", (Format-Entry $listed))

$assertions.Add(
    'L2-SL-05', '失联直接进最高档：会话行上的安全判定是车最后一次在线时的，说不了现在（REQ-0269）',
    ($listed.escalationLevel -eq 'MaintenanceAdministrator'),
    'MaintenanceAdministrator', [string]$listed.escalationLevel)

# --- C. 只亮卡、只升级告警：什么都不动（REQ-0287） ------------------------------------------------------

# 否定判据要有界：让运行时确实又跑几轮，再说它什么都没动。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$held = Get-BlockedEntry
$heldRuntime = Get-Runtime
$assertions.Add(
    'L2-SL-06', '挂着期间又跑了几轮，阻断码与开始时间不变，阶段仍是 AwaitingPickupArrival',
    ($null -ne $held -and $held.blockReasonCode -eq $lostReason -and
        (ConvertTo-Instant $held.blockReasonSince) -eq $listedSince -and
        [string]$heldRuntime.Stage -eq 'AwaitingPickupArrival'),
    "$lostReason since $($listed.blockReasonSince)（AwaitingPickupArrival）",
    "$(Format-Entry $held)（$($heldRuntime.Stage)）")

$assertions.Add(
    'L2-SL-07', '失联不结束需求、不释放租约',
    ((Get-DemandStatus) -eq 'Accepted' -and (Get-ReleasedLeaseCount) -eq 0),
    'Accepted / 0 条已释放的租约',
    "$(Get-DemandStatus) / $(Get-ReleasedLeaseCount) 条已释放的租约")

$orderCommandsAfter = Get-OrderCommandCount
$assertions.Add(
    'L2-SL-08', '失联期间订单命令面一次都没被调过：本批不发 OrderHold（用户 2026-09-20 定）',
    ($orderCommandsAfter -eq $orderCommandsBefore),
    "命令审计表仍是 $orderCommandsBefore 行", "$orderCommandsAfter 行")

# --- D. 车重新说话、重连，旅程接着走 --------------------------------------------------------------------

$backOnAir = Wait-L2Change -Description 'the journey leaves the silent-session block once the peer is back' `
    -Journal $journal -Criterion 'peer-back-on-air' -TimeoutSeconds 90 `
    -Baseline { (Get-BlockedEntry)?.blockReasonCode ?? $notListed } `
    -Action {
        $journal.Note('The onboard process comes back and reconnects; the five-step handshake runs again.')
        $null = $onboard.Command('Put', 'silence', @{ silent = $false })
        $null = $onboard.Command('Put', 'connection', @{ connected = $true })
    } `
    -Probe { (Get-BlockedEntry)?.blockReasonCode ?? $notListed } `
    -Until { param($before, $now) $before -eq $lostReason -and $now -ne $lostReason }
$assertions.Add(
    'L2-SL-09', '车重连之后这条旅程不再挂失联码（重连开新代次，先走既有的会话未就绪那条路）',
    ($backOnAir.Baseline -eq $lostReason -and $backOnAir.Value -ne $lostReason),
    "$lostReason -> 别的", "$($backOnAir.Baseline) -> $($backOnAir.Value)")

$null = Wait-L2Condition -Description 'the session is Ready again on the new generation' `
    -Journal $journal -Criterion 'peer-ready-again' -TimeoutSeconds 90 `
    -Probe { Get-PeerReadiness } -Until { param($v) $v -eq 'READY' }

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

$arrived = Wait-L2Condition -Description 'the journey arrives and waits for a sublot' `
    -Journal $journal -Criterion 'journey-arrived' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingSublot' }
$afterArrival = Get-BlockedEntry
$arrivedRuntime = Get-Runtime
$assertions.Add(
    'L2-SL-10', '到站换段之后阻断码清空，端点不再列出这条旅程，旅程照常往下走',
    ($arrived -eq 'AwaitingSublot' -and $null -eq $afterArrival -and
        [string]::IsNullOrEmpty([string]$arrivedRuntime.BlockReasonCode) -and
        [string]::IsNullOrWhiteSpace([string]$arrivedRuntime.BlockReasonSince)),
    "AwaitingSublot / $notListed / code null / since null",
    "$arrived / $(Format-Entry $afterArrival) / code '$($arrivedRuntime.BlockReasonCode)' / since '$($arrivedRuntime.BlockReasonSince)'")

$assertions.Add(
    'L2-SL-11', '全程订单命令面一次都没被调过',
    ((Get-OrderCommandCount) -eq $orderCommandsBefore),
    "命令审计表仍是 $orderCommandsBefore 行", "$(Get-OrderCommandCount) 行")

$journal.Note('Scenario finished at AwaitingSublot with the vehicle back on air.')
