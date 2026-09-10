#Requires -Version 7

<#
站点期限到期，而车上还有一扇仓门没关：不结束本站，转告警并持续等待。

ADR-cross-0058 决策 4——该 ADR 自称「基线未覆盖的那一格」。`StopClosureCommit` 的前提是车辆
随后能够离站，而 ADR-cross-0011 与 ADR-cross-0012 不允许带着未闭合的仓门移动，两者相加，
对着一扇开着的门结束本站产出的是一趟「账面上已完结、事实上动不了」的旅程。所以期限到期只把
该站转入告警（`BlockReasonCode = STATION_TIMEOUT_DOOR_NOT_CLOSED`，**stage 不变、不是
`Blocked`**），闭合后按当时的真实 IO 读数结算。

**本条 2026-09-09 重写过，钉的格子换了。**初版把这一格摆在 `AwaitingSublot`——操作员既没扫码、
又有一扇门虚掩着——跑出来是红的（`20260909-real-onboard-station-timeout-door-open-001`）。当时
的诊断是「决策 4 的分支到不了」，那个诊断只对一半：

- 决策 4 有**两格**，ADR 的 Consequences 点名的是 **`AwaitingLoadResult`**（原文：「服务端改动
  落在 …… `JourneyRuntimeEngine` 的 `AwaitingLoadResult`」）。那一格实测可达，告警照挂。
- `AwaitingSublot` 那一格**本来就不该挂这个告警**：该阶段的定义是本站一条仓位命令都没发过，
  此时读到一扇开着的门意味着**没有任何我方命令能解释它**，会话降级成 `RecoveryRequired` /
  `DEPARTURE_SAFETY_NOT_READY` 是正确行为，旅程停在就绪门上记 `ONBOARD_SESSION_NOT_READY`。

分水岭是 `IsUnsafetyExplainedByOwnCommandAsync` 那条豁免要求存在 `Prepared` 的 station
operation，而在途仓位操作**只存在于 `AwaitingLoadResult`**。完整复盘见
`docs/defects/20260909-station-timeout-door-not-closed-branch-unreachable.md`，判定本身由三条
L1 测试钉住（两格各一条，外加一条「豁免不跨车」），本条不重复钉判定——**L2 留给只有真装置能证
的东西**：光幕极性、锁反馈时序、开锁输出复位。

**为什么这条必须走真装置。**判据是车辆**自己**上报的 `LOCK_NOT_CLOSED`，那份安全投影由
`WireToGateSafetyEvaluator` 按真实 IO 读数算出来，合成对端没有这段逻辑——给它设个种子只是让
服务端收到一个字符串。

**门是怎么开着的：不用注入。**操作员扫完 SUBLOT，车载端为这次装载打开锁脉冲，门弹开，人走了。
这就是决策 4 那一格的现场原样，模拟器照实反映，不需要 `lock-feedback-override`。

与 `real-onboard-load-door-closed-empty` 的关系：那条钉的是**门已闭**的重开闭环（决策 1/2/5/6），
期限压根不到期；本条钉的是同一次装载**门一直开着**直到期限到期的那一段（决策 4）。与
`sublot-wait-timeout` 的关系：那条是门全关着、无人扫码，到期按决策 7 终结。三条互不重叠。

断言只从服务端 SQLite 与模拟器 `/snapshot` 读。UI 只用来驱动。
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

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

# SQLite 的可空列读回来是 [System.DBNull] 而不是 $null，写成一行内联的 -and/-or 会被优先级
# 拆错，跑出一条假红。
function Test-L2Null($value) {
    return ($null -eq $value -or $value -is [System.DBNull])
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

# 「没有告警」返回哨兵字符串而不是 $null：Wait-L2Condition 在 Probe 返回 $null 时**根本不评估
# Until**（`L2.psm1` 的 `if ($null -ne $last -and (& $Until $last))`），所以「等一个值消失」用
# $null 表达就是等到超时，而且报错写的是 `Last observed: (nothing)`——看着像没读到，其实是读到了
# 想要的那个空。`20260909-real-onboard-station-timeout-door-open-003` 红在这里。
function Get-BlockReason {
    $runtime = Get-Runtime
    if ($null -eq $runtime) { return '(no runtime row)' }
    if (Test-L2Null $runtime.BlockReasonCode) { return '(none)' }
    return [string]$runtime.BlockReasonCode
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

function Get-LoadOperation {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT SlotOperationAttemptId, Status FROM StationOperations " +
        "WHERE DemandId = '$demandId' AND OperationType = 'Load'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Slot([int]$slotNo) {
    return $simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $slotNo }
}

function Get-SlotPhysical([int]$slotNo) {
    $slot = Get-Slot $slotNo
    return "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
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

function Get-PhaseCount([string]$attemptId, [string]$phase) {
    return @(Get-Progress $attemptId | Where-Object { $_.phase -eq $phase }).Count
}

# --- 1. 需求出现，车去取货点 -----------------------------------------------------------------------

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
    'L2-DT-02', '站点期限的起点已经种下——它是决策 4 那一格的计时起点',
    (-not (Test-L2Null $runtime.SublotWaitStartedAt)),
    '有值', "SublotWaitStartedAt=$($runtime.SublotWaitStartedAt)")

# --- 2. UIA 代替操作员扫码，车载端为这次装载开锁 ----------------------------------------------------

# 期限只有一分钟，而扫码要走一遍真 WPF 的 UI Automation。这一段必须尽快走完，否则期限会在
# AwaitingSublot 就到期，本站按决策 7 终结，场景会绿在完全另一件事上。
$null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }

$journal.Note("Typing sublot $sublot into ScanTextBox through UI Automation.")
$onboard.SetSublot($sublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()
$journal.Note('Manual submit invoked.')

$attemptId = Wait-L2Condition -Description 'the server issued the Load command' `
    -Journal $journal -Criterion 'load-attempt' -TimeoutSeconds 120 `
    -Probe { $row = Get-LoadOperation; if ($row) { [string]$row.SlotOperationAttemptId } else { $null } } `
    -Until { param($v) $v }

$stage = Wait-L2Condition -Description 'the journey is waiting for the load result' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingLoadResult' }
$assertions.Add(
    'L2-DT-03', '扫码之后旅程进到 AwaitingLoadResult——决策 4 的告警只属于这一格',
    ($stage -eq 'AwaitingLoadResult'),
    'AwaitingLoadResult', $stage)

# 绝不能一看到 UNLOCKING 就往下走：车载端要求锁反馈稳定 feedbackStableMs(300ms) 才认，
# WAITING_OPERATOR 是它自己发的、说明它确实观测到了门开着的那一段。
$unlocking = Wait-L2Condition -Description 'the onboard started unlocking for the load' `
    -Journal $journal -Criterion 'load-unlocking' -TimeoutSeconds 120 `
    -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'UNLOCKING' })[0] } `
    -Until { param($v) $v }
$slots = @($unlocking.activeUnlockSlots)
if ($slots.Count -ne 1) {
    throw ("This scenario drives one slot; the load command targets $($slots.Count) " +
        "($($slots -join ', ')).")
}
$slotNo = [int]$slots[0]

$null = Wait-L2Condition -Description "the onboard is waiting for the operator on slot $slotNo" `
    -Journal $journal -Criterion 'load-waiting-operator' -TimeoutSeconds 120 `
    -Probe { Get-PhaseCount $attemptId 'WAITING_OPERATOR' } -Until { param($v) $v -ge 1 }

$physical = Get-SlotPhysical $slotNo
$assertions.Add(
    'L2-DT-04', "$slotNo 号仓门确实弹开了、开锁输出已复位——这就是操作员走开时的现场原样",
    ($physical -eq 'OPEN/EMPTY/0/0'),
    'OPEN/EMPTY/0/0', $physical)

# --- 3. 车辆把这扇门报上来，而会话仍然可用 ----------------------------------------------------------

# 车载端每秒算一次安全投影，读到有仓位没锁上就把 LOCK_NOT_CLOSED 放进 reasonCodes 推给服务端。
$reasons = Wait-L2Condition -Description 'the vehicle reported LOCK_NOT_CLOSED to the server' `
    -Journal $journal -Criterion 'safety-lock-not-closed' -TimeoutSeconds 60 `
    -Probe { $s = Get-Session; if ($s) { [string]$s.SafetyReasonCodesJson } else { $null } } `
    -Until { param($v) $v -like '*LOCK_NOT_CLOSED*' }
$assertions.Add(
    'L2-DT-05', '服务端的安全投影里出现了 LOCK_NOT_CLOSED——决策 4 的判据来源是这一个字段',
    ("$reasons" -like '*LOCK_NOT_CLOSED*'),
    '含 LOCK_NOT_CLOSED', "$reasons")

# 这一条是本场景真正的分水岭，也是本格与 AwaitingSublot 那一格的全部差别：这扇门是我方刚下的
# 仓位命令开的，IsUnsafetyExplainedByOwnCommandAsync 因此给出豁免，会话保持 Ready，
# AdvanceAsync 才走得到就绪门后面的期限那一段。
$session = Get-Session
$assertions.Add(
    'L2-DT-06', '门开着而会话仍然 Ready——这扇门由本服务端自己下的命令解释，不是会话故障',
    ($null -ne $session -and [string]$session.Readiness -eq 'Ready'),
    'Ready',
    $(if ($session) { "$($session.Readiness) / $($session.ReasonCode)" } else { '(no session row)' }))

# --- 4. 期限到期：告警，但不结束本站 ---------------------------------------------------------------

$journal.Note('Waiting for the one-minute station deadline to expire against the open door.')
$blockReason = Wait-L2Condition -Description 'the expired stop raised the door-not-closed alarm' `
    -Journal $journal -Criterion 'station-timeout-alarm' -TimeoutSeconds 180 `
    -Probe { Get-BlockReason } -Until { param($v) $v -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED' }
$assertions.Add(
    'L2-DT-07', '期限到期挂上 STATION_TIMEOUT_DOOR_NOT_CLOSED 告警',
    ("$blockReason" -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED'),
    'STATION_TIMEOUT_DOOR_NOT_CLOSED', "$blockReason")

# 告警不是 Block：这里没有任何东西需要管理员，ADR 要的是一辆看得见地等着的车，
# 而不是一辆停在恢复态里的车。
$stage = Get-Stage
$assertions.Add(
    'L2-DT-08', '停靠没有被关闭，旅程仍停在 AwaitingLoadResult 而不是 Blocked',
    ($stage -eq 'AwaitingLoadResult'),
    'AwaitingLoadResult', $stage)

$demandStatus = Get-DemandStatus
$assertions.Add(
    'L2-DT-09', '需求没有被 CANCELLED_BY_STATION_TIMEOUT 终结——门还开着，本站不许结算',
    ($demandStatus -eq 'Accepted'),
    'Accepted', $demandStatus)

$operation = Get-LoadOperation
$assertions.Add(
    'L2-DT-10', '期限到期没有把在途装载判进人工恢复',
    ($null -ne $operation -and [string]$operation.Status -eq 'Prepared'),
    'Prepared', $(if ($operation) { [string]$operation.Status } else { '(no operation row)' }))

# 等待不会自己退化成结束。Wait-L2Iterations 等的是假 RIoT 的地图站点读取，那是「运行时又转了
# 一轮」唯一的可观测量；少了它，「它没有结束本站」就只能写成 sleep。
$null = Wait-L2Iterations -Riot $riot -Count 6 -Journal $journal
$stage = Get-Stage
$demandStatus = Get-DemandStatus
$assertions.Add(
    'L2-DT-11', '又转了六轮仍然在等，等待没有自己退化成结束',
    ($stage -eq 'AwaitingLoadResult' -and $demandStatus -eq 'Accepted'),
    'AwaitingLoadResult / Accepted', "$stage / $demandStatus")

# --- 5. 操作员把门推实了（仍然没放料）：先换来一轮重开，第二次才结算 -------------------------------

# **期限过了并不等于立刻判死：决策 1 的目标态闭环先赢一轮。**读到相反态（门已闭、货没动）车载端
# 先重新开锁并提示——那是过期之后留给人的最后一次机会——要再读到一次相反态才结算成确定失败。
# 所以这里要关两次门：第一次换来一轮重开，第二次才是决策 5。
#
# 第一版就是只关一次的，`20260909-real-onboard-station-timeout-door-open-002` 红在这里：
# 门 20:57:56.173 关到位，车载端 20:57:56.539 就重新打了脉冲，门又弹开——脚本却在等告警撤销，
# 而门开着告警本来就不该撤销。产品是对的，判据错了。
$reopenPhysical = $null
for ($round = 1; $round -le 2; $round++) {
    $unlockingBefore = Get-PhaseCount $attemptId 'UNLOCKING'
    $waitingBefore = Get-PhaseCount $attemptId 'WAITING_OPERATOR'
    $journal.Note("Round ${round}: operator pushes slot $slotNo shut without putting anything in it.")
    $null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})

    $closed = Wait-L2Condition -Description "slot $slotNo is closed and locked with no cargo (round $round)" `
        -Journal $journal -Criterion "door-closed-empty-$round" -TimeoutSeconds 60 `
        -Probe { Get-SlotPhysical $slotNo } -Until { param($v) $v -eq 'CLOSED/EMPTY/1/0' }
    $assertions.Add(
        "L2-DT-$('{0:d2}' -f (11 + $round))",
        "第 $round 次关门：$slotNo 号仓关到位、锁反馈回到 1、货位仍是空——明确的相反态，不是 UNKNOWN",
        ($closed -eq 'CLOSED/EMPTY/1/0'),
        'CLOSED/EMPTY/1/0', $closed)

    if ($round -eq 1) {
        # 过期之后的第一次相反态换来的是重开，不是判死。等车载端**自己**再发一条
        # WAITING_OPERATOR：它在锁反馈稳定 feedbackStableMs 并且开锁输出确认复位之后才发得出来，
        # 所以它同时是「这一轮走完了」和「现在关门才算数」两件事的判据。
        $unlockingAfter = Wait-L2Condition -Description 'the onboard re-pulsed the unlock one more time' `
            -Journal $journal -Criterion 'grace-reopen' -TimeoutSeconds 120 `
            -Probe { Get-PhaseCount $attemptId 'UNLOCKING' } -Until { param($v) $v -gt $unlockingBefore }
        $null = Wait-L2Condition -Description 'the onboard is waiting for the operator again' `
            -Journal $journal -Criterion 'grace-waiting-operator' -TimeoutSeconds 120 `
            -Probe { Get-PhaseCount $attemptId 'WAITING_OPERATOR' } -Until { param($v) $v -gt $waitingBefore }
        $reopenPhysical = Get-SlotPhysical $slotNo
        $assertions.Add(
            'L2-DT-13', '期限过了之后第一次关门换来的是再开一次门，不是判死——决策 1 先赢一轮',
            ($unlockingAfter -gt $unlockingBefore -and $reopenPhysical -eq 'OPEN/EMPTY/0/0'),
            "UNLOCKING > $unlockingBefore / OPEN/EMPTY/0/0",
            "$unlockingAfter / $reopenPhysical")

        # 门又开了，所以告警也回来了。它名的是一个条件而不是一个事件。
        $stillAlarmed = Get-BlockReason
        $assertions.Add(
            'L2-DT-14', '门重新弹开之后告警还在——它名的是一个条件，不是一个事件',
            ($stillAlarmed -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED'),
            'STATION_TIMEOUT_DOOR_NOT_CLOSED', "$stillAlarmed")
    }
}

# 第二次相反态才走得到决策 5：明确的物理状态、门已闭、开锁输出已复位，报确定失败而不是 UNKNOWN。
$status = Wait-L2Condition -Description 'the onboard settled the load as a determinate failure' `
    -Journal $journal -Criterion 'determinate-failure' -TimeoutSeconds 300 `
    -Probe { $row = Get-LoadOperation; if ($row) { [string]$row.Status } else { $null } } `
    -Until { param($v) $v -eq 'Failed' }
$assertions.Add(
    'L2-DT-15', '第二次相反态按决策 5 结算成确定失败，而不是 RecoveryRequired',
    ($status -eq 'Failed'),
    'Failed', $status)

# 结算那一刻门是关着的，告警随之撤销——否则停靠会顶着一条告警读下去，把人派去找一扇已经关好的门。
$withdrawn = Wait-L2Condition -Description 'the door alarm was withdrawn once the load settled' `
    -Journal $journal -Criterion 'alarm-withdrawn' -TimeoutSeconds 120 `
    -Probe { Get-BlockReason } -Until { param($v) $v -ne 'STATION_TIMEOUT_DOOR_NOT_CLOSED' }
$assertions.Add(
    'L2-DT-16', '确定失败落地之后告警撤销，停靠不再顶着一条已经不成立的告警',
    ("$withdrawn" -ne 'STATION_TIMEOUT_DOOR_NOT_CLOSED'),
    '不是 STATION_TIMEOUT_DOOR_NOT_CLOSED', "$withdrawn")

$session = Get-Session
$assertions.Add(
    'L2-DT-17', '全程会话没有离开 Ready——没有任何一步把「人没放料」当成传感器故障',
    ($null -ne $session -and [string]$session.Readiness -eq 'Ready'),
    'Ready',
    $(if ($session) { "$($session.Readiness) / $($session.ReasonCode)" } else { '(no session row)' }))

# --- 6. 确定失败之后本站要有出口 ---------------------------------------------------------------------

# 8005-agv-program#39：这一格原先没有出口。服务端读到 Failed 只 return 等 LoadTaskCancellation，HMI 不给
# 「取消装货」，站点期限与持货超时都不查这一格，旅程永远停在 AwaitingLoadResult。现在服务端自己以
# CANCELLED_BY_STATION_TIMEOUT 终结这条需求；单需求旅程车上什么都没装，没有东西要送去关卡，就此结束。
$ended = Wait-L2Condition -Description 'the journey ended after the determinate failure' `
    -Journal $journal -Criterion 'journey-ended' -TimeoutSeconds 60 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'Completed' }
$assertions.Add(
    'L2-DT-18', '确定失败之后旅程自己结束，不再停在 AwaitingLoadResult',
    ($ended -eq 'Completed'), 'Completed', $ended)

$demandStatus = Get-DemandStatus
$assertions.Add(
    'L2-DT-19', '需求以取消终结，而不是一直挂着 Accepted 等一个没人会发的取消',
    ($demandStatus -eq 'Cancelled'), 'Cancelled', $demandStatus)

$journal.Note('Scenario finished.')
