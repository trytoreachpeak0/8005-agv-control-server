#Requires -Version 7

<#
G3 `FP-IS-07`：装载失败后由维护人员在车上发起强制机械恢复。协议向量 `CV-FORCED-MECHANICAL-RECOVERY`：
`RecoveryActionSubmitted` → `RecoveryActionAccepted` → `ForcedMechanicalRecoveryCommand` → `ForcedMechanicalRecoveryResult`，
终态就绪 `RECOVERY_REQUIRED_OR_UNIQUELY_RECONCILED`。

强制机械恢复是人手撬门：结果不证明仓位电子上为空、也不证明车辆可以恢复作业（两个 proof 字段 schema 钉死为 false）。
服务端每接受一次就把这台车的强制恢复代数加一，命令与结果都带这个代数，此前签发的一切都被这道栅栏挡在外面。
装载以 UNKNOWN 结束的办法见 `G3RecoveryCommon.ps1`：车载端在等人时断电、空仓门被关上、重启后中断结算报 UNKNOWN
（control-server#128 起，此前是「空关后等车载端超时」，v2 上不可达）。车载端「强制机械恢复」按钮是 2026-09-14 补的入口（车载端仓
`docs/W2G_FP_IS_07_OPERATOR_ENTRIES.md`）。onboard-hmi#107 起车载端收到命令后不发开锁、也不上报，等现场人员按「已隔离并完成机械取出」
并在「确认强制机械取出」答是之后才报结果；场景自 control-server#156 起按这一步。场景不按「提交硬件恢复记录」，所以 `G3-07-44` 读到的是
记录到达之前的就绪：车载端没有重连，原因码必须是 `FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED`，不是
`FORCED_RECOVERY_GENERATION_MISMATCH`（control-server#556）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'G3RecoveryCommon.ps1')
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$onboard = $Context.Onboard
$simulator = $Context.Simulator
$connection = $Context.Connection

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: the recovery entry is the onboard HMI.'
}

function Get-FleetGeneration {
    $value = Get-G3Scalar $connection "SELECT ForcedRecoveryGeneration AS Value FROM VehicleRecoveryGenerations WHERE AgvId = '$($Context.AgvId)'"
    if ($null -eq $value) { return 0 }
    return [long]$value
}

$load = Invoke-G3UnknownLoad $Context 'G3-07M'
$onboard = $Context.Onboard
$demandId = $load.DemandId
$attemptId = $load.AttemptId
$ids = @('G3-07-41', 'G3-07-42', 'G3-07-43', 'G3-07-44', 'G3-07-45')
$generationBefore = Get-FleetGeneration

$offered = Wait-G3ButtonOffered $onboard $journal '强制机械恢复' 'onboard-forced-recovery-entry' 90
if (-not $offered) {
    Add-G3NotReached $assertions $ids '车载端没有给出「强制机械恢复」入口'
    return
}
$requestedAt = [DateTimeOffset]::UtcNow
$journal.Note('Maintenance presses 强制机械恢复 and confirms.')
$null = Invoke-G3ConfirmedButton $onboard $journal '强制机械恢复' '强制机械恢复'

# The second step (onboard-hmi#107): once the ForcedMechanicalRecoveryCommand has arrived, the onboard sends no
# unlock and reports nothing until the person at the vehicle says the slots were isolated and the cargo taken out
# by hand -- 「已隔离并完成机械取出」, then Yes on 「确认强制机械取出」. Until control-server#156 this scenario never
# pressed it, so the result never came and the run aborted with the workflow AwaitingResult.
$failureTitles = Get-G3ForcedRecoveryFailureTitle
# Protocol 3.0.0 (CP-0008, onboard-hmi#216): the confirm stays disabled until the hand-off is filled in -- the sublot of the
# demand as the current stop's worklist names it (another sublot raises a warning and needs a second press) and the
# person it was handed to. The server settles the demand on that record only (control-server#385).
# So the order is form, fill, then the enabled button (control-server#541). This used to wait for the enabled button first and
# fill the form after, which no run could get past: the button enables only once the form is filled.
$formShown = Wait-G3ElementPresent $onboard $journal 'ForcedHandoffSublot' 'onboard-forced-handoff-form' 60
if (-not $formShown) {
    $shownFailure = @(@($onboard.WindowTitles()) | Where-Object { $_ -in $failureTitles })
    $why = if ($shownFailure.Count -gt 0) { "强制机械恢复未被接受（车载端弹出「$($shownFailure[0])」）" }
           else { '车载端没有给出强制机械取出的交接框（ForcedHandoffSublot）' }
    Add-G3NotReached $assertions $ids $why
    return
}
$handoffSublot = [string](Get-G3Scalar $connection "SELECT Sublot AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'")
$handoffReceiver = 'G3 交接人 王五'
$journal.Note("Maintenance fills the hand-off: sublot $handoffSublot, receiver $handoffReceiver.")
$onboard.SetTextBox('ForcedHandoffSublot', $handoffSublot)
$onboard.SetTextBox('ForcedHandoffReceiverName', $handoffReceiver)
$confirmOffered = Wait-G3ButtonOffered $onboard $journal '已隔离并完成机械取出' 'onboard-forced-recovery-confirm-entry' 30
if (-not $confirmOffered) {
    Add-G3NotReached $assertions $ids '填好交接框后「已隔离并完成机械取出」仍不可用'
    return
}
$journal.Note('Maintenance has taken the cargo out by hand; presses 已隔离并完成机械取出 and confirms.')
$null = Invoke-G3ConfirmedButton $onboard $journal '已隔离并完成机械取出' '确认强制机械取出'

$result = Wait-L2Condition -Description 'the server received ForcedMechanicalRecoveryResult, or the onboard reported a refusal' `
    -Journal $journal -Criterion 'forced-recovery-result' -TimeoutSeconds 120 `
    -Probe {
        $received = Get-G3Inbound $connection 'ForcedMechanicalRecoveryResult'
        $titles = @($onboard.WindowTitles())
        if ($received.Count -ge 1) { $received[0] }
        elseif (@($titles | Where-Object { $_ -in $failureTitles }).Count -gt 0) { 'REFUSED' }
        else { $null }
    } -Until { param($v) $null -ne $v }
if ($result -is [string]) {
    Add-G3NotReached $assertions $ids "强制机械恢复未被接受（车载端弹出「$((@(@($onboard.WindowTitles()) | Where-Object { $_ -in $failureTitles }) + '(已关闭)')[0])」）"
    return
}
$actionId = [string]$result.Payload.recoveryActionId
$null = Wait-L2Condition -Description 'the forced recovery workflow recorded its outcome' `
    -Journal $journal -Criterion 'forced-recovery-workflow' -TimeoutSeconds 30 `
    -Probe { Get-G3Scalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'" } `
    -Until { param($v) $v -in @('Reconciled', 'RecoveryRequired') }
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

$actions = @((Get-G3Inbound $connection 'RecoveryActionSubmitted') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
$commands = @((Get-G3Outbound $connection 'ForcedMechanicalRecoveryCommand') | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$results = @((Get-G3Inbound $connection 'ForcedMechanicalRecoveryResult') | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$orderOk = $actions.Count -eq 1 -and $commands.Count -eq 1 -and $results.Count -eq 1 -and
    $actions[0].Response -eq 'RecoveryActionAccepted' -and [string]$actions[0].ResponsePayload.acceptedAction -eq 'FORCED_MECHANICAL_RECOVERY' -and
    $actions[0].At -le $commands[0].At -and $commands[0].At -lt $results[0].At -and $results[0].Response -eq 'DurableAck'
$assertions.Add(
    'G3-07-41',
    '消息顺序与向量一致，各一次：RecoveryActionSubmitted(FORCED_MECHANICAL_RECOVERY) → RecoveryActionAccepted → ForcedMechanicalRecoveryCommand → ForcedMechanicalRecoveryResult（CV-FORCED-MECHANICAL-RECOVERY orderedExpectedMessages）',
    $orderOk,
    '各 1，按向量顺序',
    "Action×$($actions.Count)$(if ($actions.Count -ge 1) { " $($actions[0].Response) $($actions[0].ResponsePayload.acceptedAction)" }) / Command×$($commands.Count) / Result×$($results.Count) / 有序=$orderOk")

$generationAfter = Get-FleetGeneration
$workflowGeneration = Get-G3Scalar $connection "SELECT ForcedRecoveryGeneration AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'"
$commandGeneration = if ($commands.Count -ge 1) { [long]$commands[0].Payload.forcedRecoveryGeneration } else { -1 }
$assertions.Add(
    'G3-07-42',
    '强制恢复按代数设栅栏：接受这次动作使本车强制恢复代数恰好加一，工作流、命令都签在新代数下（FENCE_FORCED_RECOVERY_BY_GENERATION）',
    ($generationAfter -eq $generationBefore + 1 -and [long]$workflowGeneration -eq $generationAfter -and $commandGeneration -eq $generationAfter),
    "代数 $generationBefore → $($generationBefore + 1) / 工作流 $($generationBefore + 1) / 命令 $($generationBefore + 1)",
    "代数 $generationBefore → $generationAfter / 工作流 $workflowGeneration / 命令 $commandGeneration")

# Protocol 3.0.0 (CP-0008, control-server#385): the result copies the command's demand and, on a session with a demand, carries
# the named hand-off -- the sublot identified at the vehicle, who took it, when. The server settles the demand on that record
# only; without it the result is kept and settles nothing, so G3-07-44 would read the demand still blocked.
$demandSublot = [string](Get-G3Scalar $connection "SELECT Sublot AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'")
$handoff = if ($result.Payload.PSObject.Properties['cargoHandoff']) { $result.Payload.cargoHandoff } else { $null }
$handoffOk = $null -ne $handoff -and [string]$result.Payload.demandId -eq $demandId -and
    [string]$handoff.sublot -eq $demandSublot -and [string]$handoff.sublot -eq $handoffSublot -and
    [string]$handoff.receiverName -eq $handoffReceiver -and -not [string]::IsNullOrWhiteSpace([string]$handoff.handedOverAt)
$assertions.Add(
    'G3-07-43',
    '车载端报强制恢复结果：MECHANICALLY_ISOLATED，带命令的代数，电子空载与车辆就绪两项证明都没有声称，只报仓位集合，抄回命令的需求并带具名交接记录（批号与交接人即界面上所填、批号即该需求的批号、带交接时刻）（REPORT_FORCED_RECOVERY_OUTCOME / REPORT_CARGO_HANDOFF_RECORD_IN_RESULT / COPY_COMMAND_DEMAND_INTO_RESULT / REFUSE_STALE_FORCED_RECOVERY_GENERATION 的正向一半：车载端采纳的是当前代数）',
    ([string]$result.Payload.outcome -eq 'MECHANICALLY_ISOLATED' -and [long]$result.Payload.forcedRecoveryGeneration -eq $commandGeneration -and
        -not [bool]$result.Payload.electronicEmptyProven -and -not [bool]$result.Payload.vehicleReadyProven -and
        (Format-G3Slots $result.Payload.slots) -eq (Format-G3Slots $load.TargetSlots) -and -not $result.Payload.PSObject.Properties['slotResults'] -and
        $handoffOk),
    "MECHANICALLY_ISOLATED / 代数 $commandGeneration / proof false,false / 仓 $(Format-G3Slots $load.TargetSlots) / 无 slotResults / 需求 $demandId / 交接 $handoffSublot→$handoffReceiver",
    "$($result.Payload.outcome) / 代数 $($result.Payload.forcedRecoveryGeneration) / proof $($result.Payload.electronicEmptyProven),$($result.Payload.vehicleReadyProven) / 仓 $(Format-G3Slots $result.Payload.slots) / slotResults=$([bool]$result.Payload.PSObject.Properties['slotResults']) / 需求 $($result.Payload.demandId) / 交接 $(if ($null -eq $handoff) { '无' } else { "$($handoff.sublot)→$($handoff.receiverName) @ $($handoff.handedOverAt)" })")

$workflowState = Get-G3Scalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'"
$handoffRecorded = [string](Get-G3Scalar $connection "SELECT HandoffReceiverName AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'")
$journey = "$(Get-G3Scalar $connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")/$(Get-G3Scalar $connection "SELECT BlockReasonCode AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")"
# control-server#556: what the vehicle waits for once the result is in and before any reconnect is its hardware recovery
# record, so the session is held under FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED. Readiness alone could not tell that from
# FORCED_RECOVERY_GENERATION_MISMATCH -- the vehicle stuck until something made it reconnect -- and this read PASS over it.
# The reason is written by the readiness decision that follows the result's save, so wait for it rather than read once.
$session = Wait-L2ConditionOrLast -Description 'the session is held for the forced recovery''s hardware record' `
    -Journal $journal -Criterion 'forced-recovery-hardware-hold' -TimeoutSeconds 30 `
    -Probe { Get-G3Session $connection } `
    -Until { param($v) $null -ne $v -and [string]$v.ReasonCode -eq 'FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED' }
$recoverySession = Get-G3Scalar $connection "SELECT State AS Value FROM ExceptionRecoverySessions WHERE ExceptionRecoverySessionId = '$([string]$result.Payload.exceptionRecoverySessionId)'"
$closedReason = Get-G3Scalar $connection "SELECT ClosedReason AS Value FROM ExceptionRecoverySessions WHERE ExceptionRecoverySessionId = '$([string]$result.Payload.exceptionRecoverySessionId)'"
# control-server#555: Get-G3Scalar returns [string]$rows[0].Value, so a NULL column reads back as "" and can never be
# $null or DBNull -- the old test of $closedReason was false whatever the server wrote. Ask SQLite whether the column is
# NULL instead: that keeps NULL apart from an empty string, which a NULL-or-empty test would let through.
$closedReasonIsNull = (Get-G3Scalar $connection "SELECT ClosedReason IS NULL AS Value FROM ExceptionRecoverySessions WHERE ExceptionRecoverySessionId = '$([string]$result.Payload.exceptionRecoverySessionId)'") -eq '1'
$demandStatus = Get-G3Scalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
$toGate = Get-G3Count $connection "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'"
# control-server#137 (REQ-0242): the forced result closes the cargo's business as a named handoff -- demand
# terminated, journey completed, recovery session closed -- and only that. The vehicle stays out of Ready
# until a HardwareRecoveryRecord for this workflow; before #137 this asserted the dead end the ticket removed
# (workflow RecoveryRequired, journey Blocked/FORCED_MECHANICAL_RECOVERY_REQUIRES_FRESH_RECONCILIATION).
# Reading Readiness here does not race the record: the onboard never submits it by itself -- an administrator
# presses for it, and the entry appears only after the result's DurableAck (onboard-hmi#107) -- and this
# scenario presses nothing after 「已隔离并完成机械取出」: that press is what sends the result, the record entry
# (「提交硬件恢复记录」) is a separate button it never touches.
$assertions.Add(
    'G3-07-44',
    '强制恢复只结算货物业务：工作流 Reconciled 并记下结果里的交接人，需求 Cancelled，旅程 Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF，恢复会话按交接 CLOSED（closedReason 为空），没有去关卡；车辆会话仍 RecoveryRequired，原因 FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED（等硬件恢复记录，不经重连；车载端报过的代数由结果更新为当前代数，cs#556）（REQ-0242 / SETTLE_DEMAND_ONLY_ON_NAMED_HANDOFF / forbidden ready-before-reconciliation、unknown-as-success）',
    ($workflowState -eq 'Reconciled' -and $journey -eq 'Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF' -and $recoverySession -eq 'CLOSED' -and
        [string]$session.Readiness -eq 'RecoveryRequired' -and
        [string]$session.ReasonCode -eq 'FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED' -and
        [long]$session.ReportedForcedRecoveryGeneration -eq $generationAfter -and $demandStatus -eq 'Cancelled' -and $toGate -eq 0 -and
        $null -ne $handoff -and $handoffRecorded -eq $handoffReceiver -and
        $closedReasonIsNull),
    "Reconciled（交接人 G3 交接人 王五）/ Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / 会话 CLOSED（原因 NULL）/ RecoveryRequired (FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED，报过的代数 $generationAfter) / Cancelled / TO_GATE 0",
    "$workflowState（交接人 $handoffRecorded）/ $journey / 会话 $recoverySession（原因 $(if ($closedReasonIsNull) { 'NULL' } else { "'$closedReason'" })）/ $($session.Readiness) ($($session.ReasonCode)，报过的代数 $($session.ReportedForcedRecoveryGeneration)) / $demandStatus / TO_GATE $toGate")

$unlocksAfterRequest = @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' -and $_.At -gt $requestedAt })
$physical = ($load.TargetSlots | Sort-Object | ForEach-Object { "$_=$(Get-G3SlotState $simulator $_)" }) -join ' '
$expectedPhysical = ($load.TargetSlots | Sort-Object | ForEach-Object { "$_=CLOSED/EMPTY/1/0" }) -join ' '
$orders = @($riot.Snapshot().body.orders).Count
$assertions.Add(
    'G3-07-45',
    '车辆没有替人撬门做电子动作：按下之后没有开锁，仓位物理状态不变，RIoT 上只有取货那一张单（forbidden duplicate-slot-unlock、duplicate-riot-order / NO_UNPROVEN_STATE）',
    ($unlocksAfterRequest.Count -eq 0 -and $physical -eq $expectedPhysical -and $orders -eq 1),
    "开锁 0 / $expectedPhysical / RIoT 单 1",
    "开锁 $($unlocksAfterRequest.Count) / $physical / RIoT 单 $orders")

$journal.Note('FP-IS-07: a forced mechanical recovery was accepted under a new generation, reported without claiming any proof, settled the cargo as a named handoff and closed the session, and left the vehicle RecoveryRequired until an administrator submits a HardwareRecoveryRecord.')
