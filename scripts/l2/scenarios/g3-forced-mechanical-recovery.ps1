#Requires -Version 7

<#
G3 `FP-IS-07`：装载失败后由维护人员在车上发起强制机械恢复。协议向量 `CV-FORCED-MECHANICAL-RECOVERY`：
`RecoveryActionSubmitted` → `RecoveryActionAccepted` → `ForcedMechanicalRecoveryCommand` → `ForcedMechanicalRecoveryResult`，
终态就绪 `RECOVERY_REQUIRED_OR_UNIQUELY_RECONCILED`。

强制机械恢复是人手撬门：结果不证明仓位电子上为空、也不证明车辆可以恢复作业（两个 proof 字段 schema 钉死为 false）。
服务端每接受一次就把这台车的强制恢复代数加一，命令与结果都带这个代数，此前签发的一切都被这道栅栏挡在外面。
装载以 UNKNOWN 结束的办法见 `G3RecoveryCommon.ps1`。车载端「强制机械恢复」按钮是 2026-09-14 补的入口（车载端仓
`docs/W2G_FP_IS_07_OPERATOR_ENTRIES.md`）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'G3RecoveryCommon.ps1')

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

$result = Wait-L2Condition -Description 'the server received ForcedMechanicalRecoveryResult, or the onboard reported a refusal' `
    -Journal $journal -Criterion 'forced-recovery-result' -TimeoutSeconds 120 `
    -Probe {
        $received = Get-G3Inbound $connection 'ForcedMechanicalRecoveryResult'
        if ($received.Count -ge 1) { $received[0] }
        elseif (@($onboard.WindowTitles()) -contains '强制机械恢复失败') { 'REFUSED' }
        else { $null }
    } -Until { param($v) $null -ne $v }
if ($result -is [string]) {
    Add-G3NotReached $assertions $ids '强制机械恢复未被接受（车载端弹出「强制机械恢复失败」）'
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

$assertions.Add(
    'G3-07-43',
    '车载端报强制恢复结果：MECHANICALLY_ISOLATED，带命令的代数，电子空载与车辆就绪两项证明都没有声称，只报仓位集合（REPORT_FORCED_RECOVERY_OUTCOME / REFUSE_STALE_FORCED_RECOVERY_GENERATION 的正向一半：车载端采纳的是当前代数）',
    ([string]$result.Payload.outcome -eq 'MECHANICALLY_ISOLATED' -and [long]$result.Payload.forcedRecoveryGeneration -eq $commandGeneration -and
        -not [bool]$result.Payload.electronicEmptyProven -and -not [bool]$result.Payload.vehicleReadyProven -and
        (Format-G3Slots $result.Payload.slots) -eq (Format-G3Slots $load.TargetSlots) -and -not $result.Payload.PSObject.Properties['slotResults']),
    "MECHANICALLY_ISOLATED / 代数 $commandGeneration / proof false,false / 仓 $(Format-G3Slots $load.TargetSlots) / 无 slotResults",
    "$($result.Payload.outcome) / 代数 $($result.Payload.forcedRecoveryGeneration) / proof $($result.Payload.electronicEmptyProven),$($result.Payload.vehicleReadyProven) / 仓 $(Format-G3Slots $result.Payload.slots) / slotResults=$([bool]$result.Payload.PSObject.Properties['slotResults'])")

$workflowState = Get-G3Scalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'"
$journey = "$(Get-G3Scalar $connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")/$(Get-G3Scalar $connection "SELECT BlockReasonCode AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")"
$session = Get-G3Session $connection
$recoverySession = Get-G3Scalar $connection "SELECT State AS Value FROM ExceptionRecoverySessions WHERE ExceptionRecoverySessionId = '$([string]$result.Payload.exceptionRecoverySessionId)'"
$demandStatus = Get-G3Scalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
$toGate = Get-G3Count $connection "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'"
# control-server#137 (REQ-0242): the forced result closes the cargo's business as a named handoff -- demand
# terminated, journey completed, recovery session closed -- and only that. The vehicle stays out of Ready
# until a HardwareRecoveryRecord for this workflow; before #137 this asserted the dead end the ticket removed
# (workflow RecoveryRequired, journey Blocked/FORCED_MECHANICAL_RECOVERY_REQUIRES_FRESH_RECONCILIATION).
$assertions.Add(
    'G3-07-44',
    '强制恢复只结算货物业务：工作流 Reconciled，需求 Cancelled，旅程 Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF，恢复会话 CLOSED，没有去关卡；车辆会话仍 RecoveryRequired，等硬件恢复记录（REQ-0242 / forbidden ready-before-reconciliation、unknown-as-success）',
    ($workflowState -eq 'Reconciled' -and $journey -eq 'Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF' -and $recoverySession -eq 'CLOSED' -and
        [string]$session.Readiness -eq 'RecoveryRequired' -and $demandStatus -eq 'Cancelled' -and $toGate -eq 0),
    'Reconciled / Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / 会话 CLOSED / RecoveryRequired / Cancelled / TO_GATE 0',
    "$workflowState / $journey / 会话 $recoverySession / $($session.Readiness) ($($session.ReasonCode)) / $demandStatus / TO_GATE $toGate")

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

$journal.Note('FP-IS-07: a forced mechanical recovery was accepted under a new generation, reported without claiming any proof, and left the vehicle to be reconciled.')
