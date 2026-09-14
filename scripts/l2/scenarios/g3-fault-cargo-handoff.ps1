#Requires -Version 7

<#
G3 `FP-IS-07`：装载失败后由维护人员在车上发起故障货物交接。协议向量 `CV-FAULT-CARGO-HANDOFF`：
`RecoveryActionSubmitted` → `RecoveryActionAccepted` → `FaultCargoRecoveryCommand` → `FaultCargoRecoveryResult`
（向量从动作开始；打开恢复会话那一步在它之前，也在线上）。

装载以 UNKNOWN 结束的办法见 `G3RecoveryCommon.ps1`。交接号 `handoffId` 两端各自由恢复动作号派生、互不告知，
车载端拿它当作用域判据；这里核对命令、结果与服务端工作流三处是同一个。
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

$load = Invoke-G3UnknownLoad $Context 'G3-07F'
$demandId = $load.DemandId
$attemptId = $load.AttemptId
$ids = @('G3-07-31', 'G3-07-32', 'G3-07-33', 'G3-07-34', 'G3-07-35')

$offered = Wait-G3ButtonOffered $onboard $journal '故障交接' 'onboard-fault-cargo-entry' 90
if (-not $offered) {
    Add-G3NotReached $assertions $ids '车载端没有给出「故障交接」入口'
    return
}
$requestedAt = [DateTimeOffset]::UtcNow
$journal.Note('Maintenance presses 故障交接 and confirms.')
$null = Invoke-G3ConfirmedButton $onboard $journal '故障交接' '故障货物交接'

$result = Wait-L2Condition -Description 'the server received FaultCargoRecoveryResult, or the onboard reported a refusal' `
    -Journal $journal -Criterion 'fault-cargo-result' -TimeoutSeconds 120 `
    -Probe {
        $received = @((Get-G3Inbound $connection 'FaultCargoRecoveryResult') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
        if ($received.Count -ge 1) { $received[0] }
        elseif (@($onboard.WindowTitles()) -contains '故障交接失败') { 'REFUSED' }
        else { $null }
    } -Until { param($v) $null -ne $v }
if ($result -is [string]) {
    Add-G3NotReached $assertions $ids '故障交接未被接受（车载端弹出「故障交接失败」）'
    return
}
$actionId = [string]$result.Payload.recoveryActionId
$null = Wait-L2Condition -Description 'the handoff workflow settled' `
    -Journal $journal -Criterion 'fault-cargo-workflow' -TimeoutSeconds 30 `
    -Probe { Get-G3Scalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'" } `
    -Until { param($v) $v -in @('Reconciled', 'RecoveryRequired') }
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

$actions = @((Get-G3Inbound $connection 'RecoveryActionSubmitted') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
$commands = @((Get-G3Outbound $connection 'FaultCargoRecoveryCommand') | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$results = @((Get-G3Inbound $connection 'FaultCargoRecoveryResult') | Where-Object { [string]$_.Payload.recoveryActionId -eq $actionId })
$orderOk = $actions.Count -eq 1 -and $commands.Count -eq 1 -and $results.Count -eq 1 -and
    $actions[0].Response -eq 'RecoveryActionAccepted' -and [string]$actions[0].ResponsePayload.acceptedAction -eq 'FAULT_CARGO_HANDOFF' -and
    $actions[0].At -le $commands[0].At -and $commands[0].At -lt $results[0].At -and $results[0].Response -eq 'DurableAck'
$assertions.Add(
    'G3-07-31',
    '消息顺序与向量一致，各一次：RecoveryActionSubmitted(FAULT_CARGO_HANDOFF) → RecoveryActionAccepted → FaultCargoRecoveryCommand → FaultCargoRecoveryResult（CV-FAULT-CARGO-HANDOFF orderedExpectedMessages）',
    $orderOk,
    '各 1，按向量顺序',
    "Action×$($actions.Count)$(if ($actions.Count -ge 1) { " $($actions[0].Response) $($actions[0].ResponsePayload.acceptedAction)" }) / Command×$($commands.Count) / Result×$($results.Count) / 有序=$orderOk")

$workflow = (Invoke-L2Query -Connection $connection -Sql (
    "SELECT State, HandoffId, Outcome, CommandMessageId FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'"))
$handoffId = if ($workflow.Count -eq 1) { [string]$workflow[0].HandoffId } else { '' }
$assertions.Add(
    'G3-07-32',
    '服务端记下这次交接：工作流带交接号与结果 HANDED_OFF，状态 Reconciled（RECORD_FAULT_CARGO_HANDOFF）',
    ($workflow.Count -eq 1 -and (Test-G3Present $handoffId) -and [string]$workflow[0].Outcome -eq 'HANDED_OFF' -and [string]$workflow[0].State -eq 'Reconciled'),
    '交接号已记 / HANDED_OFF / Reconciled',
    $(if ($workflow.Count -eq 1) { "交接号 $handoffId / $($workflow[0].Outcome) / $($workflow[0].State)" } else { '(no workflow)' }))

$unlocksAfterRequest = @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' -and $_.At -gt $requestedAt })
$assertions.Add(
    'G3-07-33',
    '只凭授权的命令交接：命令就是工作流绑定的那一条，命令、结果、工作流三处交接号相同；按下交接之后没有额外开锁（HANDOFF_ONLY_ON_AUTHORIZED_COMMAND / forbidden duplicate-slot-unlock）',
    ($commands.Count -eq 1 -and $workflow.Count -eq 1 -and [string]$workflow[0].CommandMessageId -eq $commands[0].MessageId -and
        [string]$commands[0].Payload.handoffId -eq $handoffId -and [string]$result.Payload.handoffId -eq $handoffId -and $unlocksAfterRequest.Count -eq 0),
    "命令 = 绑定 / 交接号 $handoffId ×3 / 开锁 0",
    $(if ($commands.Count -ge 1) { "命令 $($commands[0].MessageId) / 命令交接号 $($commands[0].Payload.handoffId) / 结果交接号 $($result.Payload.handoffId) / 工作流 $handoffId / 开锁 $($unlocksAfterRequest.Count)" } else { '(no command)' }))

$slotResultText = (@($result.Payload.slotResults) | Sort-Object { [int]$_.slotNo } | ForEach-Object {
    "$($_.slotNo)=$($_.outcome)/$($_.finalPhysicalState)/$($_.lockState)/$($_.unlockOutputState)" }) -join ' '
$expectedSlotResults = ($load.TargetSlots | Sort-Object | ForEach-Object { "$_=COMPLETED/EMPTY/LOCKED/RESET" }) -join ' '
$assertions.Add(
    'G3-07-34',
    '车载端报交接结果：HANDED_OFF，每个授权仓空、锁上、输出复位（REPORT_HANDOFF_OUTCOME）',
    ([string]$result.Payload.overallOutcome -eq 'HANDED_OFF' -and $slotResultText -eq $expectedSlotResults),
    "HANDED_OFF $expectedSlotResults",
    "$($result.Payload.overallOutcome) $slotResultText")

$demandStatus = Get-G3Scalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
$loadStatus = Get-G3Scalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'"
$journey = "$(Get-G3Scalar $connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")/$(Get-G3Scalar $connection "SELECT BlockReasonCode AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")"
$toGate = Get-G3Count $connection "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'"
$physical = ($load.TargetSlots | Sort-Object | ForEach-Object { "$_=$(Get-G3SlotState $simulator $_)" }) -join ' '
$expectedPhysical = ($load.TargetSlots | Sort-Object | ForEach-Object { "$_=CLOSED/EMPTY/1/0" }) -join ' '
$orders = @($riot.Snapshot().body.orders).Count
$assertions.Add(
    'G3-07-35',
    '交接收敛且无重复提交：需求与装载 Cancelled，旅程以 TERMINATED_BY_FAULT_CARGO_HANDOFF 收尾、没有去关卡，RIoT 上只有取货那一张单，仓位物理上空、锁上（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE）',
    ($demandStatus -eq 'Cancelled' -and $loadStatus -eq 'Cancelled' -and $journey -eq 'Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF' -and
        $toGate -eq 0 -and $orders -eq 1 -and $physical -eq $expectedPhysical),
    "Cancelled / Cancelled / Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / TO_GATE 0 / RIoT 单 1 / $expectedPhysical",
    "$demandStatus / $loadStatus / $journey / TO_GATE $toGate / RIoT 单 $orders / $physical")

$journal.Note('FP-IS-07: a failed load was handed off as fault cargo on an authorized command, one handoff id end to end.')
