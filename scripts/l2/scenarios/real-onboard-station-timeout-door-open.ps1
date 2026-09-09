#Requires -Version 7

<#
站点期限到期，而车上还有一扇仓门没关：不结束本站，转告警并持续等待。

ADR-cross-0058 决策 4——该 ADR 自称「基线未覆盖的那一格」。`StopClosureCommit` 的前提是车辆
随后能够离站，而 ADR-cross-0011 与 ADR-cross-0012 不允许带着未闭合的仓门移动，两者相加，
对着一扇开着的门结束本站产出的是一趟「账面上已完结、事实上动不了」的旅程。所以期限到期只把
该站转入告警（`BlockReasonCode = STATION_TIMEOUT_DOOR_NOT_CLOSED`，**stage 仍是
`AwaitingSublot` 而不是 `Blocked`**），闭合后按当时的真实 IO 读数结算。

与 `sublot-wait-timeout` 的关系：那条是同一个期限的**另一半**——门都关着，到期就按决策 7 终结，
`CANCELLED_BY_STATION_TIMEOUT`。两条合起来才是这个期限的完整判据。那条走合成对端就够，
本条不行：判据是车辆**自己**上报的 `LOCK_NOT_CLOSED`，那份安全投影由 `WireToGateSafetyEvaluator`
按真实 IO 读数算出来，合成对端没有这段逻辑。

**怎么把一扇门弄开而不下任何仓位命令。**模拟器不提供开锁接口——那条边界是它刻意设的，
保证开锁只能由车载端写 Modbus DO 触发。所以这里用 `lock-feedback-override` 把某个仓位的锁反馈
钉成 0：物理上等价于机械锁没有咬合、门虚掩着，而车载端读到的就是「这个仓位没锁上」，
于是安全投影里出现 `LOCK_NOT_CLOSED`。清掉 override 等价于操作员把门推实了。

**本条预期会红，而且红得对。**写它之前先按源码推过一遍，结论是这条分支在真装置上到不了：

- `JourneyRuntimeEngine.AdvanceAsync` 开头就是就绪门（`CurrentReadySessionAsync`），
  会话不 Ready 就直接记 `ONBOARD_SESSION_NOT_READY` 返回，**根本走不到期限那一段**；
- 而会话要在 `LOCK_NOT_CLOSED` 之下仍然 Ready，得靠
  `WireToGateStore.IsUnsafetyExplainedByOwnCommandAsync` 那条豁免，它要求
  **存在一个 `Prepared` 的 station operation**——也就是「正在执行的仓位命令」；
- 可 `AwaitingSublot` 这个阶段恰恰一个命令都没发出去。

L1 那两条测试（`AnExpiredStationDeadlineDoesNotCloseTheStopWhileASlotDoorIsStillOpen` 等）能到，
是因为夹具的 `ReportDoorLeftOpenAsync` **直接写 `SessionRecoveryRow` 的安全字段而没有重算
readiness**。分支本身是对的；到不了的是那个状态。

所以本条的产出不是绿，是一份把「到不了」钉死的证据：L2-DT-04 记下门开着那一刻会话的真实
readiness，L2-DT-05/06/07 记下期限到期时旅程的真实样子。这与
`real-onboard-recovery-entry-missing` 是同一类——授权是齐的，路是断的。

断言只从服务端 SQLite 与模拟器 `/snapshot` 读。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$simulator = $Context.Simulator
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

# SQLite 的可空列读回来是 [System.DBNull] 而不是 $null，写成一行内联的 -and/-or 会被优先级
# 拆错，跑出一条假红。
function Test-L2Null($value) {
    return ($null -eq $value -or $value -is [System.DBNull])
}

<#
Wait-L2Condition 的容错版：超时不抛，返回最后一次观测。

本条场景是**预期会红**的那一类，判据的价值全在「到期那一刻实际是什么样」。用会抛的版本，
第一条不成立的判据就会把脚本掀掉，后面几条根本不会被记下来——证据里剩下一句超时消息，
而真正要看的三行状态一行都没有。
#>
function Wait-L2Tolerant {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Probe,
        [Parameter(Mandatory)][scriptblock]$Until,
        [Parameter(Mandatory)][string]$Criterion,
        [int]$TimeoutSeconds = 60
    )
    try {
        return Wait-L2Condition -Description $Description -Journal $journal -Criterion $Criterion `
            -TimeoutSeconds $TimeoutSeconds -Probe $Probe -Until $Until
    } catch {
        $journal.Note("Tolerated timeout on '$Description': $($_.Exception.Message)")
        return (& $Probe)
    }
}

function Get-Runtime {
    $rows = Get-L2Journey -Connection $connection -DemandId $demandId
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stage {
    $runtime = Get-Runtime
    if ($null -eq $runtime) { return $null }
    return [string]$runtime.Stage
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Session {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT Readiness, ReasonCode, DepartureSafe, SafetyUnknownPresent, SafetyReasonCodesJson " +
        "FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-DemandStatus {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Status
}

function Get-Slot([int]$slotNo) {
    return $simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $slotNo }
}

# 挑一个装载不会用到的仓位。业务分配从 1 号开始，8 号在这趟里不会被命令到，
# 所以门开着这件事只可能来自本场景的注入。
$doorSlot = 8

# --- 1. 需求出现，车去取货点，停在等条码录入 --------------------------------------------------------

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

$stage = Wait-L2Condition -Description 'the arrival was trusted and the stop is waiting for a sublot' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingSublot' }
$assertions.Add(
    'L2-DT-01', '车到取货站后停在等条码录入这一步',
    ($stage -eq 'AwaitingSublot'),
    'AwaitingSublot', $stage)

$runtime = Get-Runtime
$assertions.Add(
    'L2-DT-02', '站点期限的起点已经种下',
    (-not (Test-L2Null $runtime.SublotWaitStartedAt)),
    '有值', "SublotWaitStartedAt=$($runtime.SublotWaitStartedAt)")

# --- 2. 一扇仓门没关好，操作员也没来扫码 ------------------------------------------------------------

# 立刻注入，别等：期限只有一分钟，晚一步这一站就按「门都关着」那条路终结了，
# 场景会绿在完全另一件事上。
$journal.Note("Pinning slot $doorSlot lock feedback to 0 — a door left ajar, nobody scanning.")
$null = $simulator.Command('Put', "slots/$doorSlot/lock-feedback-override", @{ mode = 'FIXED_0' })

$lockRaw = Wait-L2Condition -Description "slot $doorSlot reads unlocked at the Modbus layer" `
    -Journal $journal -Criterion 'door-open-io' -TimeoutSeconds 30 `
    -Probe { [string](Get-Slot $doorSlot).lockFeedbackRaw } -Until { param($v) $v -eq '0' }
$assertions.Add(
    'L2-DT-03', "$doorSlot 号仓的锁反馈读数是 0——这就是一扇没关实的门在 IO 上的样子",
    ($lockRaw -eq '0'),
    '0', $lockRaw)

# 车载端每秒算一次安全投影，读到有仓位没锁上就把 LOCK_NOT_CLOSED 放进 reasonCodes 推给服务端。
$reasons = Wait-L2Tolerant -Description 'the vehicle reported LOCK_NOT_CLOSED to the server' `
    -Criterion 'safety-lock-not-closed' -TimeoutSeconds 60 `
    -Probe { $s = Get-Session; if ($s) { [string]$s.SafetyReasonCodesJson } else { $null } } `
    -Until { param($v) $v -like '*LOCK_NOT_CLOSED*' }
$assertions.Add(
    'L2-DT-04', '服务端的安全投影里出现了 LOCK_NOT_CLOSED——决策 4 的判据来源是这一个字段',
    ("$reasons" -like '*LOCK_NOT_CLOSED*'),
    '含 LOCK_NOT_CLOSED', "$reasons")

# 这一条是本场景真正的分水岭。决策 4 那段代码在 AdvanceAsync 的就绪门**之后**，所以它要跑起来，
# 会话必须在报着 LOCK_NOT_CLOSED 的同时仍然 Ready。而让它 Ready 的那条豁免
# （IsUnsafetyExplainedByOwnCommandAsync）要求存在一个 Prepared 的 station operation，
# AwaitingSublot 阶段一个都没有。
$session = Get-Session
$assertions.Add(
    'L2-DT-05', '门开着时会话仍然 Ready——否则运行时停在就绪门上，期限那一段根本不会被执行',
    ($null -ne $session -and [string]$session.Readiness -eq 'Ready'),
    'Ready',
    $(if ($session) { "$($session.Readiness) / $($session.ReasonCode)" } else { '(no session row)' }))

# --- 3. 期限到期：告警，但不结束本站 ---------------------------------------------------------------

$journal.Note('Waiting for the one-minute station deadline to expire against the open door.')
$blockReason = Wait-L2Tolerant -Description 'the expired stop raised the door-not-closed alarm' `
    -Criterion 'station-timeout-alarm' -TimeoutSeconds 120 `
    -Probe { $r = Get-Runtime; if ($r) { [string]$r.BlockReasonCode } else { $null } } `
    -Until { param($v) $v -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED' }
$assertions.Add(
    'L2-DT-06', '期限到期挂上 STATION_TIMEOUT_DOOR_NOT_CLOSED 告警',
    ("$blockReason" -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED'),
    'STATION_TIMEOUT_DOOR_NOT_CLOSED', "$blockReason")

# 告警不是 Block：这里没有任何东西需要管理员，ADR 要的是一辆看得见地等着的车，
# 而不是一辆停在恢复态里的车。
$stage = Get-Stage
$assertions.Add(
    'L2-DT-07', '停靠没有被关闭，旅程仍停在 AwaitingSublot 而不是 Blocked',
    ($stage -eq 'AwaitingSublot'),
    'AwaitingSublot', $stage)

$demandStatus = Get-DemandStatus
$assertions.Add(
    'L2-DT-08', '需求没有被 CANCELLED_BY_STATION_TIMEOUT 终结——门还开着，本站不许结算',
    ($demandStatus -eq 'Accepted'),
    'Accepted', $demandStatus)

# 等待不会自己退化成结束。Wait-L2Iterations 等的是假 RIoT 的地图站点读取，那是「运行时又转了
# 一轮」唯一的可观测量；少了它，「它没有结束本站」就只能写成 sleep。
$null = Wait-L2Iterations -Riot $riot -Count 6 -Journal $journal
$stage = Get-Stage
$demandStatus = Get-DemandStatus
$assertions.Add(
    'L2-DT-09', '又转了六轮仍然在等，等待没有自己退化成结束',
    ($stage -eq 'AwaitingSublot' -and $demandStatus -eq 'Accepted'),
    'AwaitingSublot / Accepted', "$stage / $demandStatus")

# --- 4. 操作员把门推实了：下一轮立刻按当时的读数结算 ------------------------------------------------

$journal.Note("Operator pushes slot $doorSlot shut; clearing the lock feedback override.")
$null = $simulator.Command('Put', "slots/$doorSlot/lock-feedback-override", @{ mode = 'AUTO' })

$lockRaw = Wait-L2Condition -Description "slot $doorSlot reads locked again" `
    -Journal $journal -Criterion 'door-closed-io' -TimeoutSeconds 30 `
    -Probe { [string](Get-Slot $doorSlot).lockFeedbackRaw } -Until { param($v) $v -eq '1' }
$assertions.Add(
    'L2-DT-10', "$doorSlot 号仓的锁反馈回到 1",
    ($lockRaw -eq '1'),
    '1', $lockRaw)

$stage = Wait-L2Tolerant -Description 'the stop settled once the door was shut' `
    -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'Completed' }
$runtime = Get-Runtime
$assertions.Add(
    'L2-DT-11', '门一闭合，下一轮立刻按当时的真实读数结算本站',
    ($stage -eq 'Completed' -and
        $null -ne $runtime -and [string]$runtime.BlockReasonCode -eq 'CANCELLED_BY_STATION_TIMEOUT'),
    'Completed / CANCELLED_BY_STATION_TIMEOUT',
    "$stage / $(if ($runtime) { [string]$runtime.BlockReasonCode } else { '(no runtime row)' })")

$demandStatus = Get-DemandStatus
$assertions.Add(
    'L2-DT-12', '需求终态为 Cancelled',
    ($demandStatus -eq 'Cancelled'),
    'Cancelled', $demandStatus)

$journal.Note('Scenario finished.')
