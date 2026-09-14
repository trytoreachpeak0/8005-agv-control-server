#Requires -Version 7

<#
G3 `FP-IS-07`：装载失败后由维护人员在车上发起补偿清空。协议向量 `CV-EXCEPTION-COMPENSATE`：
`ExceptionRecoverySessionRequested` → `ExceptionRecoverySessionOpened` → `RecoveryActionSubmitted` → `RecoveryActionAccepted` →
`LoadCompensationRequested` → `LoadCompensationCommand` → `LoadCompensationResult`。

装载以 UNKNOWN 结束的办法见 `G3RecoveryCommon.ps1`。门关着、仓是空的：补偿要证的是「全部授权仓位为空」，
仓本来就空，车载端不必开门就能证。服务端授权补偿只看「装载是 Load 且 RecoveryRequired」，不要物理断点，所以不必重启。
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

$load = Invoke-G3UnknownLoad $Context 'G3-07C'
$demandId = $load.DemandId
$attemptId = $load.AttemptId
$ids = @('G3-07-21', 'G3-07-22', 'G3-07-23', 'G3-07-24', 'G3-07-25')

$offered = Wait-G3ButtonOffered $onboard $journal '补偿清空' 'onboard-compensation-entry' 90
if (-not $offered) {
    Add-G3NotReached $assertions $ids '车载端没有给出「补偿清空」入口'
    return
}
$requestedAt = [DateTimeOffset]::UtcNow
$journal.Note('Maintenance presses 补偿清空 and confirms.')
$null = Invoke-G3ConfirmedButton $onboard $journal '补偿清空' '补偿清空'

$result = Wait-L2Condition -Description 'the server received LoadCompensationResult, or the onboard reported a refusal' `
    -Journal $journal -Criterion 'compensation-result' -TimeoutSeconds 120 `
    -Probe {
        $received = @((Get-G3Inbound $connection 'LoadCompensationResult') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
        if ($received.Count -ge 1) { $received[0] }
        elseif (@($onboard.WindowTitles()) -contains '补偿清空失败') { 'REFUSED' }
        else { $null }
    } -Until { param($v) $null -ne $v }
if ($result -is [string]) {
    Add-G3NotReached $assertions $ids '补偿清空未被接受（车载端弹出「补偿清空失败」）'
    return
}
$actionId = [string]$result.Payload.recoveryActionId
$null = Wait-L2Condition -Description 'the compensation workflow settled' `
    -Journal $journal -Criterion 'compensation-workflow' -TimeoutSeconds 30 `
    -Probe { Get-G3Scalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'" } `
    -Until { param($v) $v -in @('Reconciled', 'RecoveryRequired') }
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

$sessionRequests = @((Get-G3Inbound $connection 'ExceptionRecoverySessionRequested') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
$actions = @((Get-G3Inbound $connection 'RecoveryActionSubmitted') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
$compensationRequests = @((Get-G3Inbound $connection 'LoadCompensationRequested') | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$commands = @((Get-G3Outbound $connection 'LoadCompensationCommand') | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$results = @((Get-G3Inbound $connection 'LoadCompensationResult') | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$orderOk = $sessionRequests.Count -eq 1 -and $actions.Count -eq 1 -and $compensationRequests.Count -eq 1 -and $commands.Count -eq 1 -and $results.Count -eq 1 -and
    $sessionRequests[0].Response -eq 'ExceptionRecoverySessionOpened' -and $actions[0].Response -eq 'RecoveryActionAccepted' -and
    [string]$actions[0].ResponsePayload.acceptedAction -eq 'COMPENSATE_LOAD_ALL_EMPTY' -and
    $sessionRequests[0].At -le $actions[0].At -and $actions[0].At -le $compensationRequests[0].At -and
    $compensationRequests[0].At -le $commands[0].At -and $commands[0].At -lt $results[0].At -and $results[0].Response -eq 'DurableAck'
$assertions.Add(
    'G3-07-21',
    '消息顺序与向量一致，各一次：SessionRequested → Opened → ActionSubmitted(COMPENSATE_LOAD_ALL_EMPTY) → Accepted → LoadCompensationRequested → LoadCompensationCommand → LoadCompensationResult（CV-EXCEPTION-COMPENSATE orderedExpectedMessages）',
    $orderOk,
    '各 1，按向量顺序',
    "SessionRequested×$($sessionRequests.Count) / Action×$($actions.Count)$(if ($actions.Count -ge 1) { " $($actions[0].ResponsePayload.acceptedAction)" }) / CompensationRequested×$($compensationRequests.Count) / Command×$($commands.Count) / Result×$($results.Count) / 有序=$orderOk")

$workflow = (Invoke-L2Query -Connection $connection -Sql (
    "SELECT ExceptionRecoverySessionId, SlotOperationAttemptId, SlotsJson, CommandMessageId FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'"))
$sessionId = if ($sessionRequests.Count -ge 1 -and $sessionRequests[0].ResponsePayload) { [string]$sessionRequests[0].ResponsePayload.exceptionRecoverySessionId } else { '' }
$assertions.Add(
    'G3-07-22',
    '补偿依托恢复会话授权：工作流挂在这次打开的恢复会话上，命令就是工作流绑定的那一条，指向同一会话、同一尝试、同一仓位（AUTHORIZE_COMPENSATION_AGAINST_RECOVERY_SESSION）',
    ($workflow.Count -eq 1 -and [string]$workflow[0].ExceptionRecoverySessionId -eq $sessionId -and (Test-G3Present $sessionId) -and
        [string]$workflow[0].CommandMessageId -eq $commands[0].MessageId -and
        [string]$commands[0].Payload.exceptionRecoverySessionId -eq $sessionId -and [string]$commands[0].Payload.slotOperationAttemptId -eq $attemptId -and
        (Format-G3Slots $commands[0].Payload.slots) -eq (Format-G3Slots $load.TargetSlots)),
    "会话 $sessionId / 命令 = 工作流绑定 / attempt $attemptId / 仓 $(Format-G3Slots $load.TargetSlots)",
    $(if ($workflow.Count -eq 1 -and $commands.Count -ge 1) { "会话 $($workflow[0].ExceptionRecoverySessionId) / 命令 $($commands[0].MessageId) vs 绑定 $($workflow[0].CommandMessageId) / attempt $($commands[0].Payload.slotOperationAttemptId) / 仓 $(Format-G3Slots $commands[0].Payload.slots)" } else { '(workflow or command missing)' }))

$unlocksAfterRequest = @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' -and $_.At -gt $requestedAt })
$assertions.Add(
    'G3-07-23',
    '补偿只执行一次、证空不开门：一条命令、一份结果；按下补偿之后没有任何开锁（EXECUTE_COMPENSATION_ONCE / forbidden duplicate-slot-unlock）',
    ($commands.Count -eq 1 -and $results.Count -eq 1 -and $unlocksAfterRequest.Count -eq 0),
    '命令 1 / 结果 1 / 补偿后开锁 0',
    "命令 $($commands.Count) / 结果 $($results.Count) / 补偿后开锁 $($unlocksAfterRequest.Count)")

$slotResultText = (@($result.Payload.slotResults) | Sort-Object { [int]$_.slotNo } | ForEach-Object {
    "$($_.slotNo)=$($_.outcome)/$($_.finalPhysicalState)/$($_.lockState)/$($_.unlockOutputState)" }) -join ' '
$expectedSlotResults = ($load.TargetSlots | Sort-Object | ForEach-Object { "$_=COMPLETED/EMPTY/LOCKED/RESET" }) -join ' '
$physical = ($load.TargetSlots | Sort-Object | ForEach-Object { "$_=$(Get-G3SlotState $simulator $_)" }) -join ' '
$expectedPhysical = ($load.TargetSlots | Sort-Object | ForEach-Object { "$_=CLOSED/EMPTY/1/0" }) -join ' '
$assertions.Add(
    'G3-07-24',
    '车载端报补偿后的仓位状态：ALL_EMPTY，每仓空、锁上、输出复位，与模拟器一致（REPORT_COMPENSATED_SLOT_STATE）',
    ([string]$result.Payload.overallOutcome -eq 'ALL_EMPTY' -and $slotResultText -eq $expectedSlotResults -and $physical -eq $expectedPhysical),
    "ALL_EMPTY $expectedSlotResults / $expectedPhysical",
    "$($result.Payload.overallOutcome) $slotResultText / $physical")

$workflowState = Get-G3Scalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'"
$demandStatus = Get-G3Scalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
$loadStatus = Get-G3Scalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
$journey = "$(Get-G3Scalar $connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")/$(Get-G3Scalar $connection "SELECT BlockReasonCode AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")"
$released = Test-G3Present (Get-G3Scalar $connection "SELECT ReleasedAt AS Value FROM VehicleDispatchLeases WHERE DemandId = '$demandId'")
$toGate = Get-G3Count $connection "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'"
$sessionState = if (Test-G3Present $sessionId) { Get-G3Scalar $connection "SELECT State AS Value FROM ExceptionRecoverySessions WHERE ExceptionRecoverySessionId = '$sessionId'" } else { '(none)' }
$orders = @($riot.Snapshot().body.orders).Count
$assertions.Add(
    'G3-07-25',
    '补偿收敛且无重复提交：工作流 Reconciled、恢复会话 CLOSED、需求与装载 Cancelled、租约释放、旅程以 CANCELLED_BY_LOAD_COMPENSATION 收尾，没有去关卡，RIoT 上只有取货那一张单（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE）',
    ($workflowState -eq 'Reconciled' -and $sessionState -eq 'CLOSED' -and $demandStatus -eq 'Cancelled' -and $loadStatus -eq 'Cancelled' -and
        $released -and $journey -eq 'Completed/CANCELLED_BY_LOAD_COMPENSATION' -and $toGate -eq 0 -and $orders -eq 1),
    'Reconciled / CLOSED / Cancelled / Cancelled / 释放 / Completed/CANCELLED_BY_LOAD_COMPENSATION / TO_GATE 0 / RIoT 单 1',
    "$workflowState / $sessionState / $demandStatus / $loadStatus / 释放=$released / $journey / TO_GATE $toGate / RIoT 单 $orders")

$journal.Note('FP-IS-07: a failed load was compensated on authorization against a recovery session, proven empty without unlocking.')
