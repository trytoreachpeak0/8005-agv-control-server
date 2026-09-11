#Requires -Version 7

<#
四需求四停靠的一趟旅程里，在停靠 2 制造一份真的 `UNKNOWN` 并补偿清空，然后问出口。
8005-agv-program#47。

**这条场景问的是补偿之后旅程还走不走。**补偿本身早有两条场景证过：`real-onboard-recovery-compensate-load`
走 UIA，`real-onboard-field-operator-compensate` 走车载端 HTTP 自动化面——**两条都是单需求旅程**。单需求
旅程里补偿掉那一条就是整趟结束，服务端直接判 `Completed`，所以它们一直绿。多需求旅程里补偿掉一条之后，
车上还有停靠 1 装上的货、前面还有停靠 3、4 要装，服务端原来只写一个理由码、stage 留在 `Blocked`，而
`JourneyRuntimeEngine` 对 `Blocked` 只 `return`——那趟旅程再也不动，车上的货回不到关卡。现场窗口一
（#45）要在四需求满仓旅程里补偿之后接着跑，按那时的代码走不下去，补偿这一幕因此没排进窗口脚本（#43）。

- **停靠 1：正常装载。**补偿发生时车上要有货，不然问不出「车上的货回不回得到关卡」。
- **停靠 2：真的 UNKNOWN + 补偿清空。**锁反馈钉死在「已锁」、维护人员发现仓里有一箱货、关门修传感器，
  然后按「补偿清空」、把车打开的那一仓取空。两幕都是上车的 `Invoke-FieldActUnknownLoad` 与
  `Invoke-FieldActCompensate`。
- **出口**：补偿对账之后旅程自己离开停靠 2，一个按钮都不再按；会话回到 `Ready`（#46）。
- **停靠 3、4：正常装载。**证明补偿没有把这趟旅程弄坏。
- **关卡：三条装上车的需求逐条卸完，旅程 `Completed`。**

操作员的每个动作都由 `scripts/field/FieldOperator.psm1` 做，以 `Local` 传输跑在两个 loopback 端口上；
场景自己只做驱动脚本在现场不做的两件事：发布需求、替 RIoT 把车开到站。

**关卡卸货这一段在车载端修好 #48 之前是红的**：车载端把三项的关卡作业清单判 `PROTOCOL_SCHEMA_INVALID`，
与本票无关，`real-onboard-field-window-rehearsal` 的 `L2-FW-40` 红的是同一处。所以卸货排在最后、判据而
不是异常、只等 5 分钟，前面那些判据照样写得出来；`L2-MDC-60` 的实际值里记着服务端日志里那条拒收。

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
$connection = $Context.Connection

Import-Module (Join-Path $Context.Repository 'scripts/field/FieldOperator.psm1') -Force

$areas = @('N1-3', 'N2-6', 'N3-4', 'N4-2')
foreach ($area in $areas) {
    $wire = [guid]::NewGuid().ToString('N')
    $sublot = "L2-MDC-$area-$($Context.RunId)"
    $journal.Note("Publishing demand $sublot in area $area.")
    $null = $mes.Command('Put', "demands/$wire", @{
        sublot = $sublot; area = $area; eqp = "EQP-L2-$area"; package = 'L2-PACKAGE'; maxBoxCount = 4
    })
}

$field = New-FieldOperator -Connection $connection -AgvId $Context.AgvId `
    -SimulatorPort $Context.SimulatorHttpPort -AutomationPort $Context.OnboardAutomationPort `
    -Log { param($message) $journal.Note("field-operator: $message") }

# --- helpers ----------------------------------------------------------------------------------------
#
# `Invoke-L2Query` 以 `return , $rows` 交出整批行：遍历用 foreach，不接管道（README 第 22 条）。
# 探针与 Until 里只引用 `$script:` 变量：`Wait-L2Condition` 在模块里执行它们。

function Test-L2Null($value) {
    return ($null -eq $value -or $value -is [System.DBNull])
}

function Get-ConfirmedUpperId([int]$sequence) {
    # 只认 CONFIRMED：建单确认之前假 RIoT 不认这个 UpperId，早改单会回 409（README 第 5 条）。
    $rows = Invoke-L2Query -Connection $connection -Sql (
        'SELECT o.UpperId, s.StationRiotId FROM JourneyStops s JOIN OrderIntents o ON o.MovementLegId = s.MovementLegId ' +
        "WHERE s.JourneyId = '$script:journeyId' AND s.Sequence = $sequence AND o.Status = 'CONFIRMED'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Invoke-DriveToStop([int]$sequence) {
    $script:driveSequence = $sequence
    $order = Wait-L2Condition -Description "stop $sequence has a confirmed movement order" `
        -Journal $journal -Criterion "stop-$sequence-order" -TimeoutSeconds 180 `
        -Probe { Get-ConfirmedUpperId $script:driveSequence } -Until { param($v) $v }
    $journal.Note("Vehicle drives to stop $sequence (RIoT station $($order.StationRiotId)).")
    $null = $riot.Command('Put', "orders/$($order.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'
        speed = 0.8; processingOrder = $true; orderTaskId = $order.UpperId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = [int]$order.StationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($order.UpperId)", @{ orderState = 5 })
}

function Get-Position {
    $journey = Get-FieldJourney -Field $field -JourneyId $script:journeyId
    return "$($journey.CurrentStopSequence)/$($journey.Stage)"
}

function Get-SessionReading {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'"
    if ($rows.Count -eq 0) { return '(no session)' }
    return "$($rows[0].Readiness) / $($rows[0].ReasonCode)"
}

function Get-DemandStates {
    # 「停靠序号=旅程内状态/执行状态」，按停靠排好，一行字符串就能看出谁在车上、谁判死了、谁还没装。
    $rows = Invoke-L2Query -Connection $connection -Sql (
        'SELECT d.StopSequence, d.State, a.Status FROM JourneyDemands d JOIN AcceptedDemands a ON a.DemandId = d.DemandId ' +
        "WHERE d.JourneyId = '$script:journeyId' ORDER BY d.StopSequence")
    $parts = foreach ($row in $rows) { "$($row.StopSequence)=$($row.State)/$($row.Status)" }
    return ($parts -join ' ')
}

# --- 1. 派车、开到停靠 1、四条需求凑成一趟 --------------------------------------------------------------

$journey = Wait-L2Condition -Description 'a journey was dispatched to the first pickup' -Journal $journal `
    -Criterion 'journey-dispatched' -TimeoutSeconds 120 `
    -Probe { Get-FieldJourney -Field $field } -Until { param($v) [string]$v.Stage -eq 'AwaitingPickupArrival' }
$script:journeyId = [string]$journey.JourneyId

Invoke-DriveToStop 1
$stopCount = Wait-L2Condition -Description 'all four demands joined the journey' -Journal $journal `
    -Criterion 'journey-stops' -TimeoutSeconds 120 `
    -Probe { (Invoke-L2Query -Connection $connection -Sql "SELECT COUNT(*) AS N FROM JourneyStops WHERE JourneyId = '$script:journeyId'")[0].N } `
    -Until { param($v) [int]$v -ge 5 }
$assertions.Add('L2-MDC-01', '四条需求凑成一趟旅程：四个取货停靠加一个关卡', ([int]$stopCount -eq 5), 5, [int]$stopCount)

# --- 2. 停靠 1：正常装载 ---------------------------------------------------------------------------------

$stop1 = Invoke-FieldActLoad -Field $field -JourneyId $script:journeyId -Sequence 1
$assertions.Add('L2-MDC-10', '停靠 1：驱动脚本照常装载提交', ($stop1.Status -eq 'Committed'), 'Committed', $stop1.Status)

# --- 3. 停靠 2：真的 UNKNOWN，补偿清空 -------------------------------------------------------------------

Invoke-DriveToStop 2
$unknown = Invoke-FieldActUnknownLoad -Field $field -JourneyId $script:journeyId -Sequence 2
$assertions.Add(
    'L2-MDC-20', '停靠 2：驱动脚本钉死锁反馈之后车报回真的 UNKNOWN，仓位操作 RecoveryRequired、旅程 Blocked',
    ($unknown.Status -eq 'RecoveryRequired' -and $unknown.BlockCode -eq 'LOAD_RESULT_REQUIRES_RECOVERY' -and
        $unknown.SlotAfter -eq 'CLOSED/OCCUPIED/1/0'),
    'RecoveryRequired / LOAD_RESULT_REQUIRES_RECOVERY / CLOSED/OCCUPIED/1/0',
    "$($unknown.Status) / $($unknown.BlockCode) / $($unknown.SlotAfter)")

# 这一格要问得出东西，前提是补偿那一刻车上确实有别的需求还没了结：停靠 1 已装上车，停靠 3、4 还没装。
$statesBefore = Get-DemandStates
$assertions.Add(
    'L2-MDC-21', '补偿之前：停靠 1 的货在车上（Loaded），停靠 3、4 还没装（Planned）——旅程不止这一条需求',
    ($statesBefore -match '(^| )1=Loaded/' -and $statesBefore -match '(^| )3=Planned/' -and $statesBefore -match '(^| )4=Planned/'),
    '1=Loaded 2=Planned 3=Planned 4=Planned', $statesBefore)

$membership = Invoke-L2Query -Connection $connection `
    -Sql "SELECT LoadCommandMessageId FROM JourneyDemands WHERE DemandId = '$($unknown.DemandId)'"
$loadCommandMessageId = [string]$membership[0].LoadCommandMessageId

$compensation = Invoke-FieldActCompensate -Field $field -DemandId $unknown.DemandId -AttemptId $unknown.AttemptId `
    -Reason 'L2 多需求旅程：驱动脚本补偿清空停靠 2'
$assertions.Add(
    'L2-MDC-22', '停靠 2：补偿清空对账 Reconciled、结论 ALL_EMPTY，车开的就是那一仓，需求判 Cancelled',
    ($compensation.WorkflowState -eq 'Reconciled' -and $compensation.Outcome -eq 'ALL_EMPTY' -and
        $compensation.DemandStatus -eq 'Cancelled' -and
        @($compensation.ServedSlots).Count -eq 1 -and [int]@($compensation.ServedSlots)[0] -eq $unknown.SlotNo),
    "Reconciled / ALL_EMPTY / Cancelled / [$($unknown.SlotNo)]",
    "$($compensation.WorkflowState) / $($compensation.Outcome) / $($compensation.DemandStatus) / [$(@($compensation.ServedSlots) -join ',')]")

# --- 4. 出口：补偿之后旅程自己离开停靠 2 ------------------------------------------------------------------
#
# 修复之前停在这里：服务端补偿对账只在 journeyComplete 时把 stage 改成 Completed，否则只写
# CANCELLED_BY_LOAD_COMPENSATION，stage 留在 Blocked，而运行时对 Blocked 什么都不做（#47）。
# 判据而不是异常：走不出去时要一条点名位置的红判据，外加仍然写得出来的会话判据。

$left = $null
try {
    $left = Wait-L2Condition -Description 'the journey left stop 2 on its own after the compensation' -Journal $journal `
        -Criterion 'stop-2-exit' -TimeoutSeconds 120 `
        -Probe { Get-Position } -Until { param($v) $v -notlike '2/*' }
} catch {
    $left = Get-Position
    $journal.Note("Stop 2 exit not reached: $($_.Exception.Message)")
}
$journeyAfter = Get-FieldJourney -Field $field -JourneyId $script:journeyId
$assertions.Add(
    'L2-MDC-30', '停靠 2：补偿对账之后旅程自己离开这一站、去停靠 3，不需要再按任何按钮',
    ($left -like '3/*'), '3/*（离开停靠 2）', "$left / $($journeyAfter.BlockReasonCode)")

$suppression = Invoke-L2Query -Connection $connection `
    -Sql "SELECT ReasonCode FROM TransportDemandSuppressions WHERE DemandId = '$($unknown.DemandId)'"
$suppressionReason = if ($suppression.Count -eq 0) { '(no suppression)' } else { [string]$suppression[0].ReasonCode }
$settled = Invoke-L2Query -Connection $connection `
    -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$loadCommandMessageId'"
$commandSettled = ($settled.Count -eq 1 -and -not (Test-L2Null $settled[0].AcknowledgedAt))
$assertions.Add(
    'L2-MDC-31', '停靠 2 这条需求本身的结局不变：CANCELLED_BY_LOAD_COMPENSATION 永久抑制，悬空的 LoadBatch 命令已结算',
    ($suppressionReason -eq 'CANCELLED_BY_LOAD_COMPENSATION' -and $commandSettled),
    'CANCELLED_BY_LOAD_COMPENSATION / 已结算',
    "$suppressionReason / $($commandSettled ? '已结算' : '未结算')")

# 会话回到 Ready 这件事不能等 SessionRecoveries 那一行去读（-002 就红在这里）：修好之后旅程在对账后几百毫秒内就
# 过完出发安全检查、车开动，车载端随即报 VEHICLE_NOT_READY，会话按设计转 DEPARTURE_SAFETY_NOT_READY 直到到站——
# 每一段行驶都这样，场景的探针开始读时 Ready 早已过去。读的是持久的那一份：服务端收下 LoadCompensationResult
# 时连同 DurableAck 一起发给车、并缓存在收件箱首个应答里的 SessionReadiness（#46）。旅程能过出发安全检查，本身
# 也要求会话当时是 Ready。
$compensationRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT FirstResponseJson FROM ProtocolInbox WHERE MessageType = 'LoadCompensationResult'"
$readinessTold = '(no LoadCompensationResult)'
foreach ($row in $compensationRows) {
    $readinessTold = '(no SessionReadiness in the first response)'
    foreach ($line in ([string]$row.FirstResponseJson -split "`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $message = $line | ConvertFrom-Json
        if ($message.messageType -eq 'SessionReadiness') { $readinessTold = [string]$message.payload.readiness }
    }
}
$assertions.Add(
    'L2-MDC-32', '补偿对账那一刻服务端会话回到 Ready 并告诉了车：LoadCompensationResult 的首个应答里带 SessionReadiness READY（#46）',
    ($compensationRows.Count -eq 1 -and $readinessTold -eq 'READY'),
    'READY', "$readinessTold / 现在 $(Get-SessionReading)")

if ($left -notlike '3/*') {
    $journal.Note("Scenario stopped: the journey never left stop 2 ($left); demands $(Get-DemandStates).")
    return
}

# --- 5. 停靠 3、4：正常装载 -----------------------------------------------------------------------------

foreach ($sequence in 3, 4) {
    Invoke-DriveToStop $sequence
    $load = Invoke-FieldActLoad -Field $field -JourneyId $script:journeyId -Sequence $sequence
    $assertions.Add("L2-MDC-4$sequence", "停靠 ${sequence}：补偿之后照常装载提交", ($load.Status -eq 'Committed'), 'Committed', $load.Status)
}

$toGate = Wait-L2Condition -Description 'the journey left the last pickup for the gate' -Journal $journal `
    -Criterion 'journey-to-gate' -TimeoutSeconds 180 `
    -Probe { [string](Get-FieldJourney -Field $field -JourneyId $script:journeyId).Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$journeyAfter = Get-FieldJourney -Field $field -JourneyId $script:journeyId
$assertions.Add(
    'L2-MDC-50', '三站装完、一站补偿之后旅程进入去关卡那一段，理由 NO_FURTHER_CARGO',
    ($toGate -eq 'AwaitingGateArrival' -and [string]$journeyAfter.LoadingClosedReason -eq 'NO_FURTHER_CARGO'),
    'AwaitingGateArrival / NO_FURTHER_CARGO', "$toGate / $($journeyAfter.LoadingClosedReason)")

# --- 6. 关卡卸货，旅程收尾 ---------------------------------------------------------------------------------

$gateSequence = [int](Invoke-L2Query -Connection $connection `
    -Sql "SELECT Sequence FROM JourneyStops WHERE JourneyId = '$script:journeyId' AND Role = 'GATE'")[0].Sequence
Invoke-DriveToStop $gateSequence
$unloadStatuses = @()
$unloadError = $null
try {
    # 5 分钟不是现场值：卸货走不通时（#48）别再像 rehearsal-004 那样空等 30 分钟、把日志撑到十倍大。
    $unload = Invoke-FieldActUnload -Field $field -JourneyId $script:journeyId -TimeoutSeconds 300
    $unloadStatuses = @($unload.Operations.Values)
} catch {
    $unloadError = $_.Exception.Message
    $journal.Note("Gate unload did not complete: $unloadError")
}
$gateStage = [string](Get-FieldJourney -Field $field -JourneyId $script:journeyId).Stage

# 卸货走不通时要说清是不是 #48 那一处：服务端日志里「车载端拒收关卡作业清单」的那一行，按报文 id 对上
# 本旅程关卡停靠的 CurrentStopWorklistSnapshot。
$rejection = ''
if ($unloadError) {
    $serverLog = Join-Path (Split-Path -Parent $Context.SnapshotRoot) 'logs/control-server.out.log'
    $gateWorklists = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, PayloadJson FROM ProtocolOutbox WHERE MessageType = 'CurrentStopWorklistSnapshot'")
    $gateStation = [string](Invoke-L2Query -Connection $connection `
        -Sql "SELECT StationId FROM JourneyStops WHERE JourneyId = '$script:journeyId' AND Role = 'GATE'")[0].StationId
    # 解析之后再比：PayloadJson 里的中文站名是 \uXXXX 转义的，按原文 -like 匹配永远对不上（-002 的实际值因此
    # 写成了 not-gate，而那条被拒的正是关卡清单）。
    $gateItems = @{}
    foreach ($row in $gateWorklists) {
        $payload = ([string]$row.PayloadJson | ConvertFrom-Json).payload
        if ([string]$payload.stationId -eq $gateStation) { $gateItems[[string]$row.MessageId] = @($payload.items).Count }
    }
    $lines = @()
    if (Test-Path -LiteralPath $serverLog) {
        $lines = @(Get-Content -LiteralPath $serverLog | Where-Object { $_ -like '*Onboard rejected CurrentStopWorklistSnapshot*' })
    }
    $codes = foreach ($line in $lines) {
        if ($line -match 'CurrentStopWorklistSnapshot ([0-9a-f-]{36}): (\S+)') {
            $gateItems.ContainsKey($Matches[1]) ? "$($Matches[2])@关卡清单($($gateItems[$Matches[1]]) 项)" : "$($Matches[2])@非关卡清单"
        }
    }
    $rejection = " / 车载端拒收作业清单：$(@($codes | Sort-Object -Unique) -join ',') ×$(@($codes).Count)"
}
$session = Get-SessionReading
$assertions.Add(
    'L2-MDC-60', '关卡：三条装上车的需求逐条取空，每条卸货都提交，旅程 Completed（车载端拒收多项关卡清单时红，见 8005-agv-program#48）',
    ($null -eq $unloadError -and $gateStage -eq 'Completed' -and
        $unloadStatuses.Count -eq 3 -and @($unloadStatuses | Where-Object { $_ -ne 'Committed' }).Count -eq 0),
    'Completed / 3 条卸货 Committed',
    "$gateStage / $($unloadStatuses -join ',') / 会话 $session$($unloadError ? " / $unloadError" : '')$rejection")

$statesAfter = Get-DemandStates
$assertions.Add(
    'L2-MDC-61', '收尾：停靠 1、3、4 的需求卸下成功，停靠 2 的需求是补偿判死的那一条',
    ($statesAfter -eq '1=Unloaded/Succeeded 2=Cancelled/Cancelled 3=Unloaded/Succeeded 4=Unloaded/Succeeded'),
    '1=Unloaded/Succeeded 2=Cancelled/Cancelled 3=Unloaded/Succeeded 4=Unloaded/Succeeded', $statesAfter)

# 对账之后再没有任何一次仓位操作要恢复、再没开过第二个恢复会话：补偿之后的一切都是正常路径走出来的。
$recoveryOps = (Invoke-L2Query -Connection $connection `
    -Sql "SELECT COUNT(*) AS N FROM StationOperations WHERE Status = 'RecoveryRequired'")[0].N
$recoverySessions = (Invoke-L2Query -Connection $connection `
    -Sql "SELECT COUNT(*) AS N FROM ExceptionRecoverySessions WHERE AgvId = '$($Context.AgvId)'")[0].N
$assertions.Add(
    'L2-MDC-62', '补偿对账之后没有任何仓位操作再进 RecoveryRequired，也没有第二个恢复会话',
    ([int]$recoveryOps -eq 0 -and [int]$recoverySessions -eq 1),
    'RecoveryRequired 0 / 恢复会话 1', "RecoveryRequired $recoveryOps / 恢复会话 $recoverySessions")

$journal.Note('Scenario finished.')
