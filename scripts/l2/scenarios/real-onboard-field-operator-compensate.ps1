#Requires -Version 7

<#
现场驱动脚本（`scripts/field/FieldOperator.psm1`）的「制造真的 UNKNOWN + 补偿清空」两幕，在真车载端与
真 slots-simulator 上跑一遍。8005-agv-program#43。

**这条场景证的是驱动脚本，不是产品。**产品这一段早由 `real-onboard-recovery-compensate-load` 证过：
那条走 UI Automation 点「补偿清空」，本条走车载端 HTTP 自动化面的 `POST /api/v1/recovery/requests`
——现场车上只有后者。两条判据链大体同形，差别全在「谁在按」：

- 扫码、按按钮、维护人员的三步、取空关门，**全部**是 `FieldOperator.psm1` 里上车用的那几个函数，
  以 `Local` 传输跑在本机两个 loopback 端口上。场景自己只做两件驱动脚本在现场不做的事：发布需求、
  替 RIoT 把车开到站。
- 按钮是经自动化面按的，所以恢复会话的理由必须带车载端主机加的前缀 `【车载端自动化接口】`
  （8005-agv-program#41）——这是事后分辨「脚本按的」与「人按的」唯一的依据，本条钉它。
- 驱动脚本判「补偿完了」读的是服务端 `RecoveryWorkflows`，被拒读的是服务端 `ProtocolOutbox`，
  不是车载端快照：受理之后才到的 `LoadCompensationRejected` 在车上只进操作员事件（#41 留的问题，
  本票定为不改车载端）。

同一组函数是 [救出卡在停靠 2 的旅程 54d2cf63](https://github.com/trytoreachpeak0/8005-agv-program/issues/44)
第 3 步要用的。那一趟的仓位早就关着没放货，车辆一次 IO 都不碰就会报 `ALL_EMPTY`（README 第 16 条），
驱动脚本不假设车一定会开门——本条走的是「会开门」那一支，另一支只在补偿动作里少一次服务。

补偿对账之后会话回不到 `Ready`（#46），这里只记不判。
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

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-FOC-$($Context.RunId)"

function Test-L2Null($value) {
    return ($null -eq $value -or $value -is [System.DBNull])
}

$field = New-FieldOperator -Connection $connection -AgvId $Context.AgvId `
    -SimulatorPort $Context.SimulatorHttpPort -AutomationPort $Context.OnboardAutomationPort `
    -Log { param($message) $journal.Note("field-operator: $message") }.GetNewClosure()

# --- 1. 需求出现，车开到取货点（现场由 RIoT 与真车完成，不归驱动脚本） -----------------------------------

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

$journey = Get-FieldJourney -Field $field
$journeyId = [string]$journey.JourneyId

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

# --- 2. 驱动脚本：扫码、等操作员、钉死锁反馈、维护人员三步 ------------------------------------------------

$unknown = Invoke-FieldActUnknownLoad -Field $field -JourneyId $journeyId -Sequence 1
$assertions.Add(
    'L2-FOC-01', '驱动脚本经自动化面扫码并钉死锁反馈之后，车报回真的 UNKNOWN：仓位操作 RecoveryRequired、旅程 Blocked',
    ($unknown.DemandId -eq $demandId -and $unknown.Status -eq 'RecoveryRequired' -and
        $unknown.BlockCode -eq 'LOAD_RESULT_REQUIRES_RECOVERY'),
    "$demandId / RecoveryRequired / LOAD_RESULT_REQUIRES_RECOVERY",
    "$($unknown.DemandId) / $($unknown.Status) / $($unknown.BlockCode)")
$assertions.Add(
    'L2-FOC-02', '维护人员三步之后的现场是补偿的入场券：门关、有货、锁反馈有效、开锁输出复位',
    ($unknown.SlotAfter -eq 'CLOSED/OCCUPIED/1/0'), 'CLOSED/OCCUPIED/1/0', $unknown.SlotAfter)

$membership = Invoke-L2Query -Connection $connection `
    -Sql "SELECT LoadCommandMessageId FROM JourneyDemands WHERE DemandId = '$demandId'"
$loadCommandMessageId = [string]$membership[0].LoadCommandMessageId
$pendingBefore = Invoke-L2Query -Connection $connection `
    -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$loadCommandMessageId'"
$loadCommandWasPending = ($pendingBefore.Count -eq 1 -and (Test-L2Null $pendingBefore[0].AcknowledgedAt))

# --- 3. 驱动脚本：按「补偿清空」，替维护人员取空关门，等对账 -------------------------------------------------

$compensation = Invoke-FieldActCompensate -Field $field -DemandId $demandId -AttemptId $unknown.AttemptId `
    -Reason 'L2 彩排：驱动脚本补偿清空'
$assertions.Add(
    'L2-FOC-03', '驱动脚本经自动化面发起 COMPENSATE_LOAD_ALL_EMPTY，服务端对账 Reconciled、结论 ALL_EMPTY，需求判 Cancelled',
    ($compensation.WorkflowState -eq 'Reconciled' -and $compensation.Outcome -eq 'ALL_EMPTY' -and
        $compensation.DemandStatus -eq 'Cancelled'),
    'Reconciled / ALL_EMPTY / Cancelled',
    "$($compensation.WorkflowState) / $($compensation.Outcome) / $($compensation.DemandStatus)")
$assertions.Add(
    'L2-FOC-04', '补偿时车真的开了门，驱动脚本替维护人员把那一仓取空关上——且只服务了那一仓',
    (@($compensation.ServedSlots).Count -eq 1 -and [int]@($compensation.ServedSlots)[0] -eq $unknown.SlotNo),
    "[$($unknown.SlotNo)]", "[$(@($compensation.ServedSlots) -join ',')]")

$session = @(Invoke-L2Query -Connection $connection `
    -Sql "SELECT Reason, SelectedAction, AdministratorRole FROM ExceptionRecoverySessions WHERE DemandId = '$demandId'")
$reason = ($session.Count -gt 0) ? [string]$session[0].Reason : '(no recovery session)'
$assertions.Add(
    'L2-FOC-05', '恢复会话的理由带车载端主机加的前缀【车载端自动化接口】——事后分得清这一下是脚本按的',
    ($session.Count -eq 1 -and $reason -like '*【车载端自动化接口】*' -and
        [string]$session[0].SelectedAction -eq 'COMPENSATE_LOAD_ALL_EMPTY'),
    '1 个会话 / 理由含【车载端自动化接口】 / COMPENSATE_LOAD_ALL_EMPTY',
    "$($session.Count) 个会话 / $reason / $(($session.Count -gt 0) ? $session[0].SelectedAction : '-')")

$stage = Wait-L2Condition -Description 'the journey completed on the compensation' -Journal $journal `
    -Criterion 'journey-completed' -TimeoutSeconds 120 `
    -Probe { [string](Get-FieldJourney -Field $field -JourneyId $journeyId).Stage } -Until { param($v) $v -eq 'Completed' }
$runtime = Get-FieldJourney -Field $field -JourneyId $journeyId
$suppression = @(Invoke-L2Query -Connection $connection `
    -Sql "SELECT ReasonCode FROM TransportDemandSuppressions WHERE DemandId = '$demandId'")
$settled = Invoke-L2Query -Connection $connection `
    -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$loadCommandMessageId'"
$commandSettled = ($settled.Count -eq 1 -and -not (Test-L2Null $settled[0].AcknowledgedAt))
$assertions.Add(
    'L2-FOC-06', '单需求旅程以 CANCELLED_BY_LOAD_COMPENSATION 收尾并永久抑制，悬空的 LoadBatch 命令被结算',
    ($stage -eq 'Completed' -and [string]$runtime.BlockReasonCode -eq 'CANCELLED_BY_LOAD_COMPENSATION' -and
        $suppression.Count -eq 1 -and [string]$suppression[0].ReasonCode -eq 'CANCELLED_BY_LOAD_COMPENSATION' -and
        $loadCommandWasPending -and $commandSettled),
    'Completed / CANCELLED_BY_LOAD_COMPENSATION / 抑制 1 条 / 补偿前挂着、之后已结算',
    "$stage / $($runtime.BlockReasonCode) / 抑制 $($suppression.Count) 条 / " +
        "$($loadCommandWasPending ? '补偿前挂着' : '补偿前不是挂着的')、$($commandSettled ? '已结算' : '未结算')")

$slot = Get-FieldSimulatorSlot -Field $field -SlotNo $unknown.SlotNo
$reading = "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
$assertions.Add(
    'L2-FOC-07', '现场收在安全状态：门关、仓空、已锁、开锁输出复位',
    ($reading -eq 'CLOSED/EMPTY/1/0'), 'CLOSED/EMPTY/1/0', $reading)

# 只记不判：补偿对账之后会话回不到 Ready，归 8005-agv-program#46。
$readiness = @(Invoke-L2Query -Connection $connection `
    -Sql "SELECT Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'")
if ($readiness.Count -gt 0) {
    $journal.Note("Session after compensation (recorded, not judged; #46): $($readiness[0].Readiness) / $($readiness[0].ReasonCode)")
}

$journal.Note('Scenario finished.')
