#Requires -Version 7

<#
四需求四停靠的一趟旅程里，把 ADR-cross-0058 的三种操作员不作为摆进去，跑在真车载端与真
slots-simulator 上。这是现场窗口一（8005-agv-program#19）在模拟器上的彩排：2026-09-10 那次现场
组出来的正是四需求满仓旅程，三个场景一个都没跑到。

三条单停靠场景各自证过一格（`real-onboard-load-door-closed-empty`、
`real-onboard-station-timeout-door-open`、`real-onboard-unload-not-emptied`）。本条证的是**它们在
多停靠旅程里的后果**——单停靠旅程里一站结束就是整趟结束，一站判了确定失败之后旅程还能不能往下
走，那种形状根本问不出来。

- **停靠 1：关门不放料。**两轮自动重开（决策 1），最后放料，照常提交。
- **停靠 2：开门不放料、门不关超时、门已闭超时。**门一直开着，一分钟期限到期挂
  `STATION_TIMEOUT_DOOR_NOT_CLOSED` 而不结束本站（决策 4）；等满一个 `OperationTimeout` 只再提示、
  不重复脉冲（决策 3）；之后两次关门不放料，第二次结算成确定失败而不是恢复（决策 5）。
  **然后问出口**：HMI 上有没有「取消装货」，点了之后旅程能不能离开停靠 2。
- **停靠 3、4：正常装载。**证明前面两站没有把这趟旅程弄坏。

需求、条码、站点都按停靠序号从服务端库里读，不假设发布顺序就是停靠顺序；仓号从车载端自己上报
的 `UNLOCKING` 里读，不假设服务端选哪个仓。

断言只从服务端 SQLite 与模拟器 `/snapshot` 读。UI 只用来驱动，以及读「取消装货在不在」——那是
能不能操作的前提，不是业务事实。
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

$cancelButton = '取消装货'

$areas = @('N1-3', 'N2-6', 'N3-4', 'N4-2')
$published = foreach ($area in $areas) {
    $guid = [guid]::NewGuid()
    [pscustomobject]@{
        Area         = $area
        DemandIdWire = $guid.ToString('N')
        Sublot       = "L2-MDI-$area-$($Context.RunId)"
    }
}

# --- helpers ------------------------------------------------------------------------------------
#
# 两条从本目录踩出来的规矩，这里每个函数都守着：
# - 先赋值再返回。`Invoke-L2Query` 以 `return , $rows` 结尾，直接转手返回会让 `@(...).Count` 永远是 1。
# - 探针与 Until 里只引用 `$script:` 变量。`Wait-L2Condition` 在模块里执行它们，看不见函数的局部作用域。

function Test-L2Null($value) {
    return ($null -eq $value -or $value -is [System.DBNull])
}

function Get-Journey {
    $rows = Invoke-L2Query -Connection $connection -Sql 'SELECT * FROM JourneyRuntimes'
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Position {
    # 「停靠序号/stage」一个字符串，等「到了第 n 站的某一格」只要一次比较。
    $journey = Get-Journey
    if ($null -eq $journey) { return '(no journey)' }
    return "$($journey.CurrentStopSequence)/$($journey.Stage)"
}

function Get-Stops {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql 'SELECT Sequence, Role, State, StationId, StationRiotId, MovementLegId FROM JourneyStops ORDER BY Sequence'
    return $rows
}

function Get-StopDemand([int]$sequence) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        'SELECT d.DemandId, a.Sublot, s.StationId, s.StationRiotId FROM JourneyDemands d ' +
        'JOIN AcceptedDemands a ON a.DemandId = d.DemandId ' +
        'JOIN JourneyStops s ON s.JourneyId = d.JourneyId AND s.Sequence = d.StopSequence ' +
        "WHERE d.StopSequence = $sequence")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-UpperIdForStop([int]$sequence) {
    # 只认 CONFIRMED。移动单一落库就有 UpperId，但假 RIoT 那边要等建单确认才认得它——-001 在 UpperId
    # 出现 40 ms 后就去改单，假 RIoT 回 409，场景连第一站都没到（README 第 5 条那个坑的多停靠版）。
    $rows = Invoke-L2Query -Connection $connection -Sql (
        'SELECT o.UpperId FROM JourneyStops s ' +
        'JOIN OrderIntents o ON o.MovementLegId = s.MovementLegId ' +
        "WHERE s.Sequence = $sequence AND o.Status = 'CONFIRMED'")
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].UpperId
}

function Get-LoadOperation([string]$demandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        'SELECT SlotOperationAttemptId, Status FROM StationOperations ' +
        "WHERE DemandId = '$demandId' AND OperationType = 'Load'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-DemandStatus([string]$demandId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return '(no demand row)' }
    return [string]$rows[0].Status
}

function Get-Session {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 「没有告警」返回哨兵而不是 $null：Probe 返回 $null 时 Until 根本不评估，等一个值消失会等到超时。
function Get-BlockReason {
    $journey = Get-Journey
    if ($null -eq $journey) { return '(no runtime row)' }
    if (Test-L2Null $journey.BlockReasonCode) { return '(none)' }
    return [string]$journey.BlockReasonCode
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

function Invoke-DriveToStop([int]$sequence) {
    $script:driveSequence = $sequence
    $upperId = Wait-L2Condition -Description "stop $sequence has a movement order" `
        -Journal $journal -Criterion "stop-$sequence-order" -TimeoutSeconds 120 `
        -Probe { Get-UpperIdForStop $script:driveSequence } `
        -Until { param($v) -not [string]::IsNullOrWhiteSpace($v) }
    $riotId = [int]((@(Get-Stops) | Where-Object { [int]$_.Sequence -eq $sequence })[0].StationRiotId)

    $journal.Note("Vehicle drives to stop $sequence (RIoT station $riotId).")
    $null = $riot.Command('Put', "orders/$upperId", @{
        orderState        = 3
        executeVehicleKey = $Context.VehicleKey
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey      = $Context.VehicleKey
        procState       = 'RUNNING'
        movementState   = 'MT_RUNNING'
        speed           = 0.8
        processingOrder = $true
        orderTaskId     = $upperId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey       = $Context.VehicleKey
        procState        = 'IDLE'
        movementState    = 'MT_FINISHED'
        speed            = 0
        currentPosition  = $riotId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })
}

<#
在第 n 站扫码，等车载端为这次装载开锁并且自己说在等操作员。返回需求、attempt 与仓号。

**先等服务端进入这一站的 AwaitingSublot 再扫。**车载端提交扫码后不清空 `_currentEntryRequest`，只有
会话离开 Ready 或收到 SublotRejected 才清——所以车一到下一站，界面上上一站的录入请求还挂着，
`CanSubmit()` 已经是真。照它就扫，扫的是上一站的请求。
#>
function Start-StopLoad([int]$sequence) {
    $script:loadSequence = $sequence
    $null = Wait-L2Condition -Description "stop $sequence is waiting for a sublot" `
        -Journal $journal -Criterion "stop-$sequence-awaiting-sublot" -TimeoutSeconds 120 `
        -Probe { Get-Position } -Until { param($v) $v -eq "$script:loadSequence/AwaitingSublot" }

    $demand = Get-StopDemand $sequence
    if ($null -eq $demand) { throw "Stop $sequence has no demand in JourneyDemands." }
    $script:loadDemandId = [string]$demand.DemandId

    $null = Wait-L2Condition -Description "the onboard HMI accepts sublot entry at stop $sequence" `
        -Journal $journal -Criterion "stop-$sequence-can-submit" -TimeoutSeconds 60 `
        -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
    $journal.Note("Stop ${sequence}: typing $($demand.Sublot) into ScanTextBox through UI Automation.")
    $onboard.SetSublot([string]$demand.Sublot)
    $null = Wait-L2Condition -Description "the manual submit button is enabled at stop $sequence" `
        -Journal $journal -Criterion "stop-$sequence-submit-ready" -TimeoutSeconds 30 `
        -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
    $onboard.Submit()

    $attemptId = Wait-L2Condition -Description "the server issued the Load command at stop $sequence" `
        -Journal $journal -Criterion "stop-$sequence-load-attempt" -TimeoutSeconds 120 `
        -Probe { $row = Get-LoadOperation $script:loadDemandId; if ($row) { [string]$row.SlotOperationAttemptId } else { $null } } `
        -Until { param($v) $v }
    $script:loadAttemptId = $attemptId

    $unlocking = Wait-L2Condition -Description "the onboard started unlocking at stop $sequence" `
        -Journal $journal -Criterion "stop-$sequence-unlocking" -TimeoutSeconds 120 `
        -Probe { @(Get-Progress $script:loadAttemptId | Where-Object { $_.phase -eq 'UNLOCKING' })[0] } `
        -Until { param($v) $v }
    $slots = @($unlocking.activeUnlockSlots)
    if ($slots.Count -ne 1) {
        throw "Stop $sequence drives one slot; the load command targets $($slots.Count) ($($slots -join ', '))."
    }

    # 绝不能一看到 UNLOCKING 就动手：锁反馈稳定 feedbackStableMs 之后车载端才发 WAITING_OPERATOR。
    $null = Wait-L2Condition -Description "the onboard is waiting for the operator at stop $sequence" `
        -Journal $journal -Criterion "stop-$sequence-waiting-operator" -TimeoutSeconds 120 `
        -Probe { Get-PhaseCount $script:loadAttemptId 'WAITING_OPERATOR' } -Until { param($v) $v -ge 1 }

    return [pscustomobject]@{
        Sequence  = $sequence
        DemandId  = [string]$demand.DemandId
        Sublot    = [string]$demand.Sublot
        AttemptId = [string]$attemptId
        SlotNo    = [int]$slots[0]
    }
}

<#
关门不放料一次，等车载端重开锁并**自己**再发一条 WAITING_OPERATOR。那条消息在锁反馈稳定且开锁
输出确认复位之后才发得出来，所以它同时是「这一轮走完了」和「现在关门才算数」。只看门弹开就动手，
开锁输出还是 1 的时候门就被关上，车载端观测不到稳定的已开锁，整轮卡死（README 第 13 条）。
#>
function Invoke-CloseEmptyRound($load, [int]$round, [string]$criterionPrefix) {
    $script:roundLoad = $load
    $script:roundUnlockingBefore = Get-PhaseCount $load.AttemptId 'UNLOCKING'
    $script:roundWaitingBefore = Get-PhaseCount $load.AttemptId 'WAITING_OPERATOR'
    $journal.Note("Stop $($load.Sequence) round ${round}: closing slot $($load.SlotNo) with no cargo in it.")
    $null = $simulator.Command('Post', "slots/$($load.SlotNo)/close-door", @{})

    $closed = Wait-L2Condition -Description "slot $($load.SlotNo) is closed and locked with no cargo ($criterionPrefix round $round)" `
        -Journal $journal -Criterion "$criterionPrefix-$round-closed-empty" -TimeoutSeconds 60 `
        -Probe { Get-SlotPhysical $script:roundLoad.SlotNo } -Until { param($v) $v -eq 'CLOSED/EMPTY/1/0' }
    return [pscustomobject]@{ Closed = $closed; UnlockingBefore = $script:roundUnlockingBefore; WaitingBefore = $script:roundWaitingBefore }
}

function Wait-Reopened($load, $before, [int]$round, [string]$criterionPrefix) {
    $script:roundLoad = $load
    $script:roundUnlockingBefore = $before.UnlockingBefore
    $script:roundWaitingBefore = $before.WaitingBefore
    $unlocking = Wait-L2Condition -Description "the onboard re-pulsed the unlock ($criterionPrefix round $round)" `
        -Journal $journal -Criterion "$criterionPrefix-$round-unlocking" -TimeoutSeconds 60 `
        -Probe { Get-PhaseCount $script:roundLoad.AttemptId 'UNLOCKING' } -Until { param($v) $v -gt $script:roundUnlockingBefore }
    $waiting = Wait-L2Condition -Description "the onboard is waiting for the operator again ($criterionPrefix round $round)" `
        -Journal $journal -Criterion "$criterionPrefix-$round-waiting-operator" -TimeoutSeconds 60 `
        -Probe { Get-PhaseCount $script:roundLoad.AttemptId 'WAITING_OPERATOR' } -Until { param($v) $v -gt $script:roundWaitingBefore }
    return [pscustomobject]@{ Unlocking = $unlocking; Waiting = $waiting; Door = [string](Get-Slot $load.SlotNo).doorState }
}

function Complete-StopLoad($load) {
    $script:completeLoad = $load
    $journal.Note("Stop $($load.Sequence): operator puts the cargo in slot $($load.SlotNo) and closes the door.")
    $null = $simulator.Command('Put', "slots/$($load.SlotNo)/cargo", @{ state = 'OCCUPIED' })
    $null = $simulator.Command('Post', "slots/$($load.SlotNo)/close-door", @{})
    $physical = Wait-L2Condition -Description "slot $($load.SlotNo) is closed, locked and occupied" `
        -Journal $journal -Criterion "stop-$($load.Sequence)-loaded" -TimeoutSeconds 60 `
        -Probe { Get-SlotPhysical $script:completeLoad.SlotNo } -Until { param($v) $v -eq 'CLOSED/OCCUPIED/1/0' }
    $status = Wait-L2Condition -Description "the load at stop $($load.Sequence) committed" `
        -Journal $journal -Criterion "stop-$($load.Sequence)-committed" -TimeoutSeconds 120 `
        -Probe { $row = Get-LoadOperation $script:completeLoad.DemandId; if ($row) { [string]$row.Status } else { $null } } `
        -Until { param($v) $v -eq 'Committed' }
    return [pscustomobject]@{ Physical = $physical; Status = $status }
}

# --- 1. 四条需求，派车，开到停靠 1 ------------------------------------------------------------------

foreach ($demand in $published) {
    $journal.Note("Publishing demand $($demand.Sublot) in area $($demand.Area).")
    $null = $mes.Command('Put', "demands/$($demand.DemandIdWire)", @{
        sublot      = $demand.Sublot
        area        = $demand.Area
        eqp         = "EQP-L2-$($demand.Area)"
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

$null = Wait-L2Condition -Description 'a journey was created and dispatched to the first pickup' `
    -Journal $journal -Criterion 'journey-dispatched' -TimeoutSeconds 120 `
    -Probe { Get-Position } -Until { param($v) $v -eq '1/AwaitingPickupArrival' }

# 派车时只承诺一条，其余三条在车到第一站之后才被吸收。
Invoke-DriveToStop 1

$stopCount = Wait-L2Condition -Description 'all four demands joined one journey after the first arrival' `
    -Journal $journal -Criterion 'journey-stops' -TimeoutSeconds 120 `
    -Probe { @(Get-Stops).Count } -Until { param($v) $v -ge 5 }
$pickups = @(Get-Stops | Where-Object { $_.Role -eq 'PICKUP' })
$assertions.Add(
    'L2-MDI-01', '四条需求凑成一趟旅程，四个取货停靠落在四个不同站点上',
    ($pickups.Count -eq 4 -and @($pickups | ForEach-Object { [int]$_.StationRiotId } | Sort-Object -Unique).Count -eq 4),
    '4 PICKUP / 4 stations',
    "$($pickups.Count) PICKUP / $(@($pickups | ForEach-Object { [int]$_.StationRiotId } | Sort-Object -Unique).Count) stations")

# --- 2. 停靠 1：关门不放料，两轮自动重开，最后放料 -----------------------------------------------------

$stop1 = Start-StopLoad 1
$assertions.Add(
    'L2-MDI-10', '停靠 1：车载端说在等操作员时，它开的那个仓门确实开着、货位是空的',
    ((Get-SlotPhysical $stop1.SlotNo) -eq 'OPEN/EMPTY/0/0'),
    'OPEN/EMPTY/0/0', (Get-SlotPhysical $stop1.SlotNo))

for ($round = 1; $round -le 2; $round++) {
    $before = Invoke-CloseEmptyRound $stop1 $round 'stop-1'
    $after = Wait-Reopened $stop1 $before $round 'stop-1'
    $assertions.Add(
        "L2-MDI-1$round",
        "停靠 1 第 $round 轮：关门不放料是明确的相反态，车载端自动重开并再次等操作员，没有判失败",
        ($before.Closed -eq 'CLOSED/EMPTY/1/0' -and $after.Unlocking -gt $before.UnlockingBefore -and
            $after.Door -eq 'OPEN' -and $after.Waiting -gt $before.WaitingBefore),
        'CLOSED/EMPTY/1/0 -> UNLOCKING+1 -> OPEN / WAITING_OPERATOR+1',
        "$($before.Closed) -> UNLOCKING $($before.UnlockingBefore)->$($after.Unlocking) -> $($after.Door) / WAITING_OPERATOR $($before.WaitingBefore)->$($after.Waiting)")
}

$stop1Done = Complete-StopLoad $stop1
$assertions.Add(
    'L2-MDI-13', '停靠 1：重开两轮之后照常放料提交，没有进恢复',
    ($stop1Done.Physical -eq 'CLOSED/OCCUPIED/1/0' -and $stop1Done.Status -eq 'Committed'),
    'CLOSED/OCCUPIED/1/0 / Committed', "$($stop1Done.Physical) / $($stop1Done.Status)")

# --- 3. 停靠 2：门开着过期、提示节拍、两次关门判确定失败 ------------------------------------------------

Invoke-DriveToStop 2
$stop2 = Start-StopLoad 2
$script:stop2 = $stop2
$assertions.Add(
    'L2-MDI-20', '停靠 2：车载端为这次装载开了门，操作员走开——这就是现场原样，不需要注入',
    ((Get-SlotPhysical $stop2.SlotNo) -eq 'OPEN/EMPTY/0/0'),
    'OPEN/EMPTY/0/0', (Get-SlotPhysical $stop2.SlotNo))
$waitingAtWalkAway = Get-PhaseCount $stop2.AttemptId 'WAITING_OPERATOR'
$unlockingAtWalkAway = Get-PhaseCount $stop2.AttemptId 'UNLOCKING'
$script:waitingAtWalkAway = $waitingAtWalkAway

$journal.Note('Stop 2: leaving the door open for the one-minute station deadline to expire.')
$alarm = Wait-L2Condition -Description 'the expired stop raised the door-not-closed alarm' `
    -Journal $journal -Criterion 'stop-2-alarm' -TimeoutSeconds 180 `
    -Probe { Get-BlockReason } -Until { param($v) $v -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED' }
$operation = Get-LoadOperation $stop2.DemandId
$position = Get-Position
$assertions.Add(
    'L2-MDI-21', '停靠 2：期限到期挂告警而不结束本站——旅程仍在 AwaitingLoadResult，装载仍在进行（决策 4）',
    ($alarm -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED' -and $position -eq '2/AwaitingLoadResult' -and
        [string]$operation.Status -eq 'Prepared'),
    'STATION_TIMEOUT_DOOR_NOT_CLOSED / 2/AwaitingLoadResult / Prepared',
    "$alarm / $position / $([string]$operation.Status)")

$journal.Note('Stop 2: still leaving the door open, now for a full operationTimeoutMs prompt cadence.')
$waitingAfterIdle = Wait-L2Condition -Description 'the prompt cadence fired again on the open door' `
    -Journal $journal -Criterion 'stop-2-idle-prompt' -TimeoutSeconds 240 `
    -Probe { Get-PhaseCount $script:stop2.AttemptId 'WAITING_OPERATOR' } -Until { param($v) $v -gt $script:waitingAtWalkAway }
$unlockingAfterIdle = Get-PhaseCount $stop2.AttemptId 'UNLOCKING'
$assertions.Add(
    'L2-MDI-22', '停靠 2：门一直开着，提示节拍到期只再提示一次、不重复脉冲（决策 3）',
    ($waitingAfterIdle -gt $waitingAtWalkAway -and $unlockingAfterIdle -eq $unlockingAtWalkAway),
    "WAITING_OPERATOR > $waitingAtWalkAway / UNLOCKING = $unlockingAtWalkAway",
    "WAITING_OPERATOR $waitingAfterIdle / UNLOCKING $unlockingAfterIdle")

# 期限过了并不等于立刻判死：第一次相反态换来一轮重开，第二次才结算。
$before = Invoke-CloseEmptyRound $stop2 1 'stop-2-grace'
$after = Wait-Reopened $stop2 $before 1 'stop-2-grace'
$assertions.Add(
    'L2-MDI-23', '停靠 2：期限过后第一次关门不放料换来的是再开一次门，不是判死',
    ($after.Unlocking -gt $before.UnlockingBefore -and $after.Door -eq 'OPEN'),
    'UNLOCKING+1 / OPEN', "UNLOCKING $($before.UnlockingBefore)->$($after.Unlocking) / $($after.Door)")

$null = Invoke-CloseEmptyRound $stop2 2 'stop-2-settle'
$failed = Wait-L2Condition -Description 'the stop-2 load settled as a determinate failure' `
    -Journal $journal -Criterion 'stop-2-determinate-failure' -TimeoutSeconds 300 `
    -Probe { $row = Get-LoadOperation $script:stop2.DemandId; if ($row) { [string]$row.Status } else { $null } } `
    -Until { param($v) $v -in @('Failed', 'RecoveryRequired') }
$session = Get-Session
$assertions.Add(
    'L2-MDI-24', '停靠 2：第二次相反态结算成确定失败而不是 RecoveryRequired，会话仍 Ready（决策 5）',
    ($failed -eq 'Failed' -and $null -ne $session -and [string]$session.Readiness -eq 'Ready'),
    'Failed / Ready', "$failed / $(if ($session) { $session.Readiness } else { '(no session)' })")

$withdrawn = Wait-L2Condition -Description 'the door alarm was withdrawn once the load settled' `
    -Journal $journal -Criterion 'stop-2-alarm-withdrawn' -TimeoutSeconds 120 `
    -Probe { Get-BlockReason } -Until { param($v) $v -ne 'STATION_TIMEOUT_DOOR_NOT_CLOSED' }
$assertions.Add(
    'L2-MDI-25', '停靠 2：确定失败落地之后告警撤销',
    ($withdrawn -ne 'STATION_TIMEOUT_DOOR_NOT_CLOSED'), '(not STATION_TIMEOUT_DOOR_NOT_CLOSED)', $withdrawn)

# --- 4. 停靠 2 的出口 ---------------------------------------------------------------------------------

# 服务端在 AwaitingLoadResult 读到 Failed 只 return，注释说「等 LoadTaskCancellation 结算这条需求」。
# 站点期限只在 AwaitingSublot 检查，持货超时也不在这个分支里——所以现场这一格唯一的出口，是操作员
# 在 HMI 上取消。下面三条判据问的就是这条出口在不在、走不走得通。
$cancelVisible = Wait-L2Condition -Description 'the HMI offers load cancellation after the determinate failure' `
    -Journal $journal -Criterion 'stop-2-cancel-visible' -TimeoutSeconds 30 `
    -Probe { $onboard.ButtonEnabled($cancelButton) } -Until { param($v) $v }
$assertions.Add(
    'L2-MDI-30', '停靠 2：确定失败之后 HMI 上出现「取消装货」',
    [bool]$cancelVisible, $true, $cancelVisible)

$journal.Note('Stop 2: operator presses 取消装货 and confirms.')
$onboard.InvokeButton($cancelButton)
$null = $onboard.Confirm($cancelButton)

$left = Wait-L2Condition -Description 'the journey left stop 2 after the cancellation' `
    -Journal $journal -Criterion 'stop-2-exit' -TimeoutSeconds 120 `
    -Probe { Get-Position } -Until { param($v) $v -notlike '2/*' }
$stop2Status = Get-DemandStatus $stop2.DemandId
$assertions.Add(
    'L2-MDI-31', '停靠 2：取消之后旅程离开这一站，这条需求以取消终结',
    ($left -notlike '2/*' -and $stop2Status -eq 'Cancelled'),
    '(not stop 2) / Cancelled', "$left / $stop2Status")

# --- 5. 停靠 3、4：正常装载 ----------------------------------------------------------------------------

foreach ($sequence in 3, 4) {
    Invoke-DriveToStop $sequence
    $load = Start-StopLoad $sequence
    $done = Complete-StopLoad $load
    $assertions.Add(
        "L2-MDI-4$sequence", "停靠 ${sequence}：前两站之后照常装载提交",
        ($done.Physical -eq 'CLOSED/OCCUPIED/1/0' -and $done.Status -eq 'Committed'),
        'CLOSED/OCCUPIED/1/0 / Committed', "$($done.Physical) / $($done.Status)")
}

# --- 6. 收尾 ------------------------------------------------------------------------------------------

$final = Wait-L2Condition -Description 'the journey left the last pickup for the gate' `
    -Journal $journal -Criterion 'journey-to-gate' -TimeoutSeconds 180 `
    -Probe { $j = Get-Journey; if ($j) { [string]$j.Stage } else { $null } } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$journey = Get-Journey
$assertions.Add(
    'L2-MDI-50', '三站装完、一站取消之后旅程进入去关卡那一段，并记下装货收尾的理由',
    ($final -eq 'AwaitingGateArrival' -and -not (Test-L2Null $journey.LoadingClosedReason)),
    'AwaitingGateArrival / a reason', "$final / $($journey.LoadingClosedReason)")

$recoveryRows = Invoke-L2Query -Connection $connection -Sql (
    "SELECT COUNT(*) AS N FROM StationOperations WHERE Status = 'RecoveryRequired'")
$assertions.Add(
    'L2-MDI-51', '全程没有任何一次装载被判 RecoveryRequired',
    ([int]$recoveryRows[0].N -eq 0), 0, [int]$recoveryRows[0].N)

$journal.Note('Scenario finished.')
