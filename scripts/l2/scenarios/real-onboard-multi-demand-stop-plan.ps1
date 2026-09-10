#Requires -Version 7

<#
四条需求、四个不同取货停靠、一个关卡，跑在真车载端与真 slots-simulator 上。

这条场景存在的直接原因是 2026-09-10 的现场窗口：引擎组了一趟四需求旅程，服务端按契约发出
一条五条腿的 `UpcomingStopPlanSnapshot`（4×TO_PICKUP + 1×TO_GATE），车辆侧的手写校验写死只收
两条，判它 `PROTOCOL_SCHEMA_INVALID` 并断开。服务端每次会话恢复重发同一个 messageId 而报文里的
`sentAt` 变了，于是撞上自己的幂等保护、主动关连接，车辆两秒后重连再拒——**循环不自愈，八分半后
车载端进程直接消失，装载一次都没开始**。见 `evidence/field/20260910-FW-SC1-operator-inaction/`。

为什么既有场景一条都挡不住它：

- `multi-demand-one-stop` 是**两条需求同一个取货停靠**，行程带只有 2 条腿，恰好落在旧上限里。
- 三条 `real-onboard-*` 全是单需求单停靠，同样 2 条腿。
- 车载端那条单元回归（`MultiDemandUpcomingStopPlanWithMoreThanTwoLegsIsAccepted`）证明它收得下
  五条腿，但证明不了服务端真的会发五条腿、也证明不了后面四个停靠还能依次走完。

所以判据分两段：**行程带被收下**，以及**四个停靠依次完成真 IO 装载闭环**。第二段是第一段的意义
所在——现场那次锁死的表现就是「行程带被拒之后什么都没发生」。

三个前几版踩出来的事实，都写进了下面的写法里：

- **四条需求是车到第一站之后才凑齐的。**派车时只承诺一条，行程带在那一刻只有两条腿（-004 在开车
  之前等五个停靠，停在 2 一直等到超时）。
- **每条需求分几个仓、分哪个仓，由服务端的包装容量规则决定。**-006 按「每站两箱、占 1/2 号仓」
  写死，服务端实际每条只分一仓（`TargetSlotsJson` 依次是 `[1]`/`[2]`/`[3]`/`[4]`），2 号仓永远不开。
  仓号现在从车载端自己上报的 `UNLOCKING` 里读。
- **移动单要等 `CONFIRMED` 才能开车**，扫码要等服务端进入**这一站**的 `AwaitingSublot` 才能扫——
  车载端提交扫码后不清空录入请求，车一到下一站界面上还挂着上一站的请求。

站点在 setup.psd1 的 ExtraStations 里。种子地图只有一个可当取货点的站（`12 = N1-3_N1-7`；
`11 = C15-13` 不以 N 开头，`JourneyRuntimeEngine` 判它 `OUT_OF_SCOPE_AREA`）。
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

$areas = @('N1-3', 'N2-6', 'N3-4', 'N4-2')
$published = foreach ($area in $areas) {
    [pscustomobject]@{
        Area         = $area
        # 目录里的键是 demandId 的无连字符形式。拿 sublot 当键发布过一次，目录照收，引擎一条都不
        # 受理，而且不写任何原因。
        DemandIdWire = [guid]::NewGuid().ToString('N')
        Sublot       = "L2-MDS-$area-$($Context.RunId)"
    }
}

# --- helpers ------------------------------------------------------------------------------------
#
# 先赋值再返回（`Invoke-L2Query` 以 `return , $rows` 结尾）；探针与 Until 里只引用 `$script:` 变量
# （`Wait-L2Condition` 在模块里执行它们）。

function Test-L2Null($value) {
    return ($null -eq $value -or $value -is [System.DBNull])
}

function Get-Journey {
    $rows = Invoke-L2Query -Connection $connection -Sql 'SELECT * FROM JourneyRuntimes'
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Position {
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
        'SELECT d.DemandId, a.Sublot FROM JourneyDemands d ' +
        'JOIN AcceptedDemands a ON a.DemandId = d.DemandId ' +
        "WHERE d.StopSequence = $sequence")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-UpperIdForStop([int]$sequence) {
    # 只认 CONFIRMED：UpperId 一落库就有，假 RIoT 要等建单确认才认得它，早改单回 409。
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

function Get-CommittedLoadCount {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT COUNT(*) AS N FROM StationOperations WHERE OperationType = 'Load' AND Status = 'Committed'")
    return [int]$rows[0].N
}

function Get-SlotPhysical([int]$slotNo) {
    $slot = $simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $slotNo }
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

# 行程带是服务端存进出站表的原文，不是从界面上读来的投影。判据要看的就是「服务端到底发了几条腿」。
function Get-LatestStopPlanLegs {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'UpcomingStopPlanSnapshot' " +
        'ORDER BY CreatedAt DESC LIMIT 1')
    if ($rows.Count -eq 0) { return $null }
    return ([string]$rows[0].PayloadJson | ConvertFrom-Json).payload.legs
}

# 车辆侧拒收会回一条 ProtocolProblem。它在服务端的收件箱里，是「这条报文被拒了」唯一的一手证据。
function Get-StopPlanRejections {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT RequestJson FROM ProtocolInbox WHERE MessageType = 'ProtocolProblem'"
    $matched = foreach ($row in $rows) {
        $payload = ([string]$row.RequestJson | ConvertFrom-Json).payload
        if ($payload.rejectedMessageType -eq 'UpcomingStopPlanSnapshot') { $payload }
    }
    return @($matched)
}

function Invoke-DriveToStop([int]$sequence) {
    $script:driveSequence = $sequence
    $upperId = Wait-L2Condition -Description "stop $sequence has a confirmed movement order" `
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

# --- 1. 四条需求，派车，开到停靠 1 ------------------------------------------------------------------

foreach ($demand in $published) {
    $journal.Note("Publishing demand $($demand.Sublot) in area $($demand.Area).")
    $null = $mes.Command('Put', "demands/$($demand.DemandIdWire)", @{
        sublot      = $demand.Sublot
        area        = $demand.Area
        eqp         = "EQP-L2-$($demand.Area)"
        package     = 'L2-PACKAGE'
        maxBoxCount = 2
    })
}

$null = Wait-L2Condition -Description 'a journey was created and dispatched to the first pickup' `
    -Journal $journal -Criterion 'journey-dispatched' -TimeoutSeconds 120 `
    -Probe { Get-Position } -Until { param($v) $v -eq '1/AwaitingPickupArrival' }

Invoke-DriveToStop 1

$null = Wait-L2Condition -Description 'all four demands joined one journey after the first arrival' `
    -Journal $journal -Criterion 'journey-stops' -TimeoutSeconds 120 `
    -Probe { @(Get-Stops).Count } -Until { param($v) $v -ge 5 }

$allStops = @(Get-Stops)
$pickupStops = @($allStops | Where-Object { $_.Role -eq 'PICKUP' })
$gateStops = @($allStops | Where-Object { $_.Role -eq 'GATE' })
$assertions.Add(
    'L2-MDS-01', '四条需求凑成一趟旅程，四个取货停靠加一个关卡',
    ($pickupStops.Count -eq 4 -and $gateStops.Count -eq 1),
    '4 PICKUP + 1 GATE', "$($pickupStops.Count) PICKUP + $($gateStops.Count) GATE")

$journeyCount = Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM JourneyRuntimes'
$assertions.Add(
    'L2-MDS-02', '是一趟旅程，不是四趟',
    ([int]$journeyCount[0].N -eq 1), 1, [int]$journeyCount[0].N)

# --- 2. 行程带：五条腿，车辆侧收下 -----------------------------------------------------------------

$legs = Wait-L2Condition -Description 'the server published a five-leg stop plan' `
    -Journal $journal -Criterion 'stop-plan-legs' -TimeoutSeconds 60 `
    -Probe { $l = Get-LatestStopPlanLegs; if ($null -eq $l) { 0 } else { @($l).Count } } `
    -Until { param($v) $v -ge 5 }
$assertions.Add(
    'L2-MDS-03', '服务端按契约发出五条腿的行程带（四个取货加一个关卡）',
    ($legs -eq 5), 5, $legs)

# 这一条是现场那次锁死的判据本身：车辆侧收下了，没有回 ProtocolProblem。
# 套一层 @()：没有拒收时函数返回的空数组会被管道展开成 $null，StrictMode 下 $null.Count 直接抛
# `The property 'Count' cannot be found on this object`——-005 就红在这里，恰恰是在它该绿的时候。
$rejections = @(Get-StopPlanRejections)
$assertions.Add(
    'L2-MDS-04', '车辆侧收下了这条行程带——没有 PROTOCOL_SCHEMA_INVALID 拒收',
    ($rejections.Count -eq 0),
    '0 次拒收',
    "$($rejections.Count) 次拒收$(if ($rejections.Count) { "（$($rejections[0].problem.reasonCode)）" })")

# --- 3. 四个停靠依次装载 --------------------------------------------------------------------------

for ($sequence = 1; $sequence -le 4; $sequence++) {
    # 停靠 1 上面已经开过去了——四条需求正是在那一刻才凑齐的。
    if ($sequence -gt 1) { Invoke-DriveToStop $sequence }

    $script:stopSequence = $sequence
    $null = Wait-L2Condition -Description "stop $sequence is waiting for a sublot" `
        -Journal $journal -Criterion "stop-$sequence-awaiting-sublot" -TimeoutSeconds 120 `
        -Probe { Get-Position } -Until { param($v) $v -eq "$script:stopSequence/AwaitingSublot" }

    $demand = Get-StopDemand $sequence
    if ($null -eq $demand) { throw "Stop $sequence has no demand in JourneyDemands." }
    $script:stopDemandId = [string]$demand.DemandId
    $journal.Note("=== Stop ${sequence}: $($demand.Sublot) ===")

    $null = Wait-L2Condition -Description "the onboard HMI accepts sublot entry at stop $sequence" `
        -Journal $journal -Criterion "stop-$sequence-can-submit" -TimeoutSeconds 60 `
        -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
    $onboard.SetSublot([string]$demand.Sublot)
    $null = Wait-L2Condition -Description "the manual submit button is enabled at stop $sequence" `
        -Journal $journal -Criterion "stop-$sequence-submit-ready" -TimeoutSeconds 30 `
        -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
    $onboard.Submit()

    $script:stopAttempt = Wait-L2Condition -Description "the server issued the Load command at stop $sequence" `
        -Journal $journal -Criterion "stop-$sequence-load-attempt" -TimeoutSeconds 120 `
        -Probe { $row = Get-LoadOperation $script:stopDemandId; if ($row) { [string]$row.SlotOperationAttemptId } else { $null } } `
        -Until { param($v) $v }
    $unlocking = Wait-L2Condition -Description "the onboard started unlocking at stop $sequence" `
        -Journal $journal -Criterion "stop-$sequence-unlocking" -TimeoutSeconds 120 `
        -Probe { @(Get-Progress $script:stopAttempt | Where-Object { $_.phase -eq 'UNLOCKING' })[0] } `
        -Until { param($v) $v }

    foreach ($slotNo in @($unlocking.activeUnlockSlots | ForEach-Object { [int]$_ })) {
        $script:slotNo = $slotNo
        # 等门开到位、开锁输出复位再动手——脚本比人快，开锁输出还没复位就关门，车载端观测不到
        # 一个稳定的「已开锁」状态，会卡在 WaitForLockerAsync 里。README 第 7 条那个坑。
        $null = Wait-L2Condition -Description "slot $slotNo is open and waiting for cargo" `
            -Journal $journal -Criterion "stop-$sequence-slot-$slotNo-open" -TimeoutSeconds 120 `
            -Probe { Get-SlotPhysical $script:slotNo } -Until { param($v) $v -eq 'OPEN/EMPTY/0/0' }

        $journal.Note("Operator puts cargo in slot $slotNo and closes the door.")
        $null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = 'OCCUPIED' })
        $null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})

        $physical = Wait-L2Condition -Description "slot $slotNo is closed, locked and occupied" `
            -Journal $journal -Criterion "stop-$sequence-slot-$slotNo-loaded" -TimeoutSeconds 90 `
            -Probe { Get-SlotPhysical $script:slotNo } -Until { param($v) $v -eq 'CLOSED/OCCUPIED/1/0' }
        $assertions.Add(
            "L2-MDS-1$sequence-$slotNo", "停靠 $sequence 的 $slotNo 号仓走通真 Modbus 闭环",
            ($physical -eq 'CLOSED/OCCUPIED/1/0'),
            'CLOSED/OCCUPIED/1/0', $physical)
    }

    $committed = Wait-L2Condition -Description "the load at stop $sequence committed" `
        -Journal $journal -Criterion "stop-$sequence-committed" -TimeoutSeconds 120 `
        -Probe { Get-CommittedLoadCount } -Until { param($v) $v -ge $script:stopSequence }
    $assertions.Add(
        "L2-MDS-2$sequence", "停靠 $sequence 的装载提交，累计 $sequence 次",
        ($committed -ge $sequence), ">= $sequence", $committed)
}

# --- 4. 装完之后去关卡 ----------------------------------------------------------------------------

$stage = Wait-L2Condition -Description 'the journey left the last pickup for the gate' `
    -Journal $journal -Criterion 'journey-to-gate' -TimeoutSeconds 180 `
    -Probe { $j = Get-Journey; if ($j) { [string]$j.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingGateArrival' }
$assertions.Add(
    'L2-MDS-30', '四个停靠都装完之后旅程进入去关卡那一段',
    ($stage -eq 'AwaitingGateArrival'), 'AwaitingGateArrival', $stage)

# 四条需求各分一仓，八仓装不满，所以收尾理由是「没有更多货」而不是「车已装满」。只要求它说得出理由。
$journey = Get-Journey
$assertions.Add(
    'L2-MDS-31', '装货阶段收尾时记下了理由',
    (-not (Test-L2Null $journey.LoadingClosedReason)),
    'a reason', [string]$journey.LoadingClosedReason)

$recoveryRows = Invoke-L2Query -Connection $connection -Sql (
    "SELECT COUNT(*) AS N FROM StationOperations WHERE Status = 'RecoveryRequired'")
$assertions.Add(
    'L2-MDS-32', '全程没有任何一次装载被判 RecoveryRequired',
    ([int]$recoveryRows[0].N -eq 0), 0, [int]$recoveryRows[0].N)

$demandRows = Invoke-L2Query -Connection $connection -Sql 'SELECT Status FROM AcceptedDemands'
$notAccepted = @($demandRows | Where-Object { [string]$_.Status -ne 'Accepted' })
$assertions.Add(
    'L2-MDS-33', '四条需求全程保持 Accepted',
    ($demandRows.Count -eq 4 -and $notAccepted.Count -eq 0),
    '4 条 Accepted',
    "$($demandRows.Count) 条，其中 $($notAccepted.Count) 条不是 Accepted")

$journal.Note('Scenario finished.')
