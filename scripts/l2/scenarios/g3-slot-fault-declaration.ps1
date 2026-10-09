#Requires -Version 7

<#
G3 `FP-IS-07`：人工判故障（REQ-0359，control-server#383 服务端、onboard-hmi#215 车载端）的两条向量，一条需求两站：

1. 装货站，`CV-SLOT-FAULT-DECLARATION-NOT-APPLICABLE`：越过门槛后管理员判定，协议故障代理吞掉判定命令一次（链路不断），
   车载端没收到；操作员随后放货关门，装货照常完成（`SETTLE_OPERATION_RESULT_NORMALLY_WHILE_DECLARATION_PENDING`）；
   代理断开一次，车重连，服务端随恢复报告补发那条仍未结的判定，车载端这时核对到尝试已结，回 `NOT_APPLICABLE`
   （`ACTION_NOT_ALLOWED_IN_STATE`）。服务端撤销判定、不改任何业务状态，车照常去卸货站。
   这样造 NOT_APPLICABLE 不靠赛跑：命令丢失是代理按计划做的，结算顺序由场景一步步推，每次跑都是同一个先后。
2. 卸货站，`CV-SLOT-FAULT-DECLARATION-APPLIED`：越过门槛后判定，车载端回 `APPLIED`，停止本次操作的一切开锁，另报
   `OperationResult`（被判仓 UNKNOWN 带 `SLOT_FAULT_DECLARED`，其余 NOT_STARTED）；服务端仓位操作 RecoveryRequired、旅程
   Blocked、需求不结算、会话 RECOVERY_REQUIRED。之后空关一次门，车不再重开。

向量内容按 `protocol-v3.0.0` 候选（`8005-agv-protocol` `3f091cb2`）`vectors/CV-SLOT-FAULT-DECLARATION-*/expected.json` 对照。
线上顺序读协议故障代理的流量日志；服务端库读 `SlotFaultDeclarations`、`StationOperations`、`JourneyRuntimes`、`AcceptedDemands`、
`SessionRecoveries` 与 `ProtocolInbox`；模拟器读门。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealStation.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ExpectedActionOverdue.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$simulator = $Context.Simulator
$connection = $Context.Connection
$proxy = $Context.ProtocolProxy
$agvId = [string]$Context.AgvId

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath) -or $null -eq $proxy) {
    throw 'This scenario needs the real onboard rig with the protocol fault proxy (see its setup.psd1).'
}
if (-not $Context.SlotFaultDeclarationCredential) {
    throw 'The setup file must turn SlotFaultDeclaration on; without it this scenario proves nothing.'
}

$setup = Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot 'g3-slot-fault-declaration.setup.psd1')
$threshold = Resolve-L2ExpectedActionOverdueThreshold -Setup $setup -Where 'g3-slot-fault-declaration.setup.psd1' -RealOnboard $true
if ($null -eq $threshold) {
    throw 'g3-slot-fault-declaration.setup.psd1 must name ExpectedActionOverdueThreshold: both declarations wait for it.'
}
$code = 'SLOT_EXPECTED_ACTION_OVERDUE'
$declarationUri = "http://127.0.0.1:$($Context.HealthPort)/api/safety/v1/slot-fault-declarations"
$reopenWindow = [TimeSpan]::FromSeconds(10)
$notApplicableIds = @('G3-07-61', 'G3-07-62', 'G3-07-63', 'G3-07-64', 'G3-07-65')
$appliedIds = @('G3-07-66', 'G3-07-67', 'G3-07-68', 'G3-07-69', 'G3-07-70')

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "G3-07SFD-$($Context.RunId)"

function Get-Declaration([string]$declarationId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM SlotFaultDeclarations WHERE DeclarationId = '$declarationId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-DemandStatus {
    return Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
}

function Invoke-Declaration([int]$slot, [string]$category, [string]$note) {
    $body = @{
        requestId         = [guid]::NewGuid().ToString('D')
        agvId             = $agvId
        slotNo            = $slot
        faultCategory     = $category
        note              = $note
        operatorId        = 'G3-MAINTENANCE-383'
        administratorRole = 'MAINTENANCE_ADMINISTRATOR'
    }
    $response = Invoke-WebRequest -Method Post -Uri $declarationUri -NoProxy -SkipHttpErrorCheck -TimeoutSec 30 `
        -Headers @{ Authorization = "Bearer $($Context.SlotFaultDeclarationCredential)" } `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json)
    $text = if ($response.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($response.Content)
    } else {
        [string]$response.Content
    }
    $journal.Note("Declaration of slot $slot ($category) -> $([int]$response.StatusCode): $text")
    return [pscustomobject]@{
        Status = [int]$response.StatusCode
        Body   = if ($text) { $text | ConvertFrom-Json } else { $null }
    }
}

function Wait-Overdue([int]$slot, [string]$criterion) {
    return Wait-L2RealOrLast -Description "the onboard reported $code for slot $slot" `
        -Journal $journal -Criterion $criterion -TimeoutSeconds ([int]$threshold.TotalSeconds + 30) `
        -Probe {
            @((Get-L2RealInbound $connection 'OnboardAlarmSnapshot') | Where-Object {
                    @(@($_.Payload.alarms) | Where-Object { $null -ne $_ -and [string]$_.code -eq $code -and [string]$_.subjectId -eq [string]$slot }).Count -gt 0
                }) | Select-Object -Last 1
        } `
        -Until { param($v) $null -ne $v }
}

# Every line of the relay's traffic after index $from, as "direction messageType", for the ordered-message criteria.
function Get-TrafficAfter([int]$from) {
    $lines = @((Get-L2RealTraffic $proxy).lines)
    return , @($lines | Select-Object -Skip $from)
}

# The declaration's own lines in order: its command (by messageId), its result (the onboard's SlotFaultDeclarationResult
# naming the declaration is found through the inbox, which has the payload the relay log does not), the DurableAck for
# that result, and -- when asked -- the OperationResult and its DurableAck.
function Get-DeclarationSequence([int]$from, [string]$commandMessageId, [string]$resultMessageId, [string]$operationResultMessageId) {
    $sequence = [System.Collections.Generic.List[string]]::new()
    foreach ($line in (Get-TrafficAfter $from)) {
        if ($line.PSObject.Properties['dropped'] -and $line.dropped -eq $true) { continue }
        $id = ([string]$line.messageId).ToLowerInvariant()
        $correlation = ([string]($line.PSObject.Properties['correlationId'] ? $line.correlationId : '')).ToLowerInvariant()
        if ($line.direction -eq 'server->onboard' -and $id -eq $commandMessageId) { $sequence.Add('SlotFaultDeclarationCommand') }
        elseif ($line.direction -eq 'onboard->server' -and $id -eq $resultMessageId) { $sequence.Add('SlotFaultDeclarationResult') }
        elseif ($line.direction -eq 'server->onboard' -and $line.messageType -eq 'DurableAck' -and
                ($correlation -eq $resultMessageId -or ($operationResultMessageId -and $correlation -eq $operationResultMessageId))) { $sequence.Add('DurableAck') }
        elseif ($operationResultMessageId -and $line.direction -eq 'onboard->server' -and $id -eq $operationResultMessageId) { $sequence.Add('OperationResult') }
    }
    return , @($sequence)
}

# --- 1. 装货站：到站、扫码、开锁后没人放货 ---------------------------------------------------------------------------

Invoke-L2PickupAndScan -Context $Context -DemandIdWire $demandIdWire -DemandId $demandId -Sublot $sublot
$load = Wait-L2WaitingOperator -Context $Context -DemandId $demandId -OperationType 'Load'
$loadAttempt = $load.AttemptId
$loadSlot = $load.SlotNo

# 门槛之前判一次：服务端只在超时仓上受理（DECLARE_ONLY_ON_OVERDUE_SLOT_AWAITING_OPERATOR）。门槛 20 秒，这一次在开锁后几秒内发出。
$early = Invoke-Declaration $loadSlot 'LIGHT_CURTAIN' '门槛之前就判'
$earlyReasons = @($early.Body.reasons ?? @())

$overdue = Wait-Overdue $loadSlot 'load-overdue-reported'
if ($null -eq $overdue) {
    Add-L2RealNotReached $assertions ($notApplicableIds + $appliedIds) "越过门槛 $([int]$threshold.TotalSeconds + 30) 秒内车载端没有报 $code"
    return
}

$assertions.Add(
    'G3-07-62', '服务端只在超时仓上受理判定：门槛之前判回 409 SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE、什么都不写；越过门槛后同一仓受理',
    ($early.Status -eq 409 -and $earlyReasons -contains 'SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE'),
    '409 / SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE', "$($early.Status) / $($earlyReasons -join ',')")

# --- 2. 判定命令在路上丢了；装货照常完成 --------------------------------------------------------------------------

$null = $proxy.Command('Put', 'drop-message', @{ messageType = 'SlotFaultDeclarationCommand'; count = 1 })
$lost = Invoke-Declaration $loadSlot 'LIGHT_CURTAIN' '光幕一直报有物，仓内实际为空'
$lostId = [string]($lost.Body.declarationId ?? '')
$lostRow = Get-Declaration $lostId
$lostCommandId = if ($lostRow) { ([string]$lostRow.CommandMessageId).ToLowerInvariant() } else { '' }
$dropped = Wait-L2RealOrLast -Description 'the relay dropped the declaration command' `
    -Journal $journal -Criterion 'declaration-command-dropped' -TimeoutSeconds 15 `
    -Probe { @(@((Get-L2RealTraffic $proxy).lines) | Where-Object {
                ([string]$_.messageId).ToLowerInvariant() -eq $lostCommandId -and $_.PSObject.Properties['dropped'] -and $_.dropped -eq $true }).Count } `
    -Until { param($v) $v -ge 1 }
if ($lost.Status -ne 202 -or $dropped -lt 1) {
    Add-L2RealNotReached $assertions @('G3-07-61', 'G3-07-63', 'G3-07-64', 'G3-07-65') "装货站的判定没有受理（$($lost.Status)）或代理没有吞下判定命令（$dropped）"
    Add-L2RealNotReached $assertions $appliedIds '装货站的判定没走到约定的形状，卸货站不跑'
    return
}

$journal.Note("The operator puts the cargo in slot $loadSlot and shuts the door; the vehicle never saw the declaration.")
$null = $simulator.Command('Put', "slots/$loadSlot/cargo", @{ state = 'OCCUPIED' })
$null = $simulator.Command('Post', "slots/$loadSlot/close-door", @{})
$committed = Wait-L2RealOrLast -Description 'the load committed while the declaration was pending' `
    -Journal $journal -Criterion 'load-committed' -TimeoutSeconds 60 `
    -Probe { [string](Get-L2StationOperation -Connection $connection -DemandId $demandId -OperationType 'Load').Status } `
    -Until { param($v) $v -eq 'Committed' }
$pendingWhileSettling = [string](Get-Declaration $lostId).State
$assertions.Add(
    'G3-07-63', '判定未结时装货照常结算：装货结果 COMPLETED、仓位操作 Committed，此刻判定仍 PENDING（SETTLE_OPERATION_RESULT_NORMALLY_WHILE_DECLARATION_PENDING）',
    ($committed -eq 'Committed' -and $pendingWhileSettling -eq 'PENDING' -and
        [string]@((Get-L2OperationResults -Connection $connection -AttemptId $loadAttempt))[0].Payload.overallOutcome -eq 'COMPLETED'),
    'Committed / PENDING / COMPLETED', "$committed / $pendingWhileSettling / $([string]@((Get-L2OperationResults -Connection $connection -AttemptId $loadAttempt))[0].Payload.overallOutcome)")

# --- 3. 断开一次：服务端补发，车载端回 NOT_APPLICABLE ------------------------------------------------------------------

$before = @((Get-L2RealTraffic $proxy).lines).Count
$sessionBefore = Get-L2RealSession $connection $agvId
$null = $proxy.Command('Post', 'disconnect', @{})
$journal.Note('Relay disconnected; the vehicle reconnects and the server replays the pending declaration.')
$refusal = Wait-L2RealOrLast -Description 'the onboard answered the replayed declaration' `
    -Journal $journal -Criterion 'declaration-not-applicable' -TimeoutSeconds 90 `
    -Probe { @((Get-L2RealInbound $connection 'SlotFaultDeclarationResult') | Where-Object { [string]$_.Payload.declarationId -eq $lostId }) | Select-Object -First 1 } `
    -Until { param($v) $null -ne $v }
$refusalId = if ($refusal) { ([string]$refusal.MessageId).ToLowerInvariant() } else { '' }
$sequence = Wait-L2RealOrLast -Description 'the replayed command, the refusal and its DurableAck crossed the relay' `
    -Journal $journal -Criterion 'not-applicable-sequence' -TimeoutSeconds 20 `
    -Probe { Get-DeclarationSequence $before $lostCommandId $refusalId $null } `
    -Until { param($v) (@($v) -join ',') -eq 'SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck' }
$assertions.Add(
    'G3-07-61', '线上顺序与向量一致：补发的 SlotFaultDeclarationCommand（同一 messageId、新一代会话）→ SlotFaultDeclarationResult → DurableAck',
    ((@($sequence) -join ',') -eq 'SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck' -and
        [long]$refusal.Generation -gt [long]$sessionBefore.SessionGeneration),
    'SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck / 新一代',
    "$(@($sequence) -join ',') / gen $(if ($refusal) { $refusal.Generation } else { '(none)' }) vs $($sessionBefore.SessionGeneration)")
$assertions.Add(
    'G3-07-64', '车载端对已结的尝试回 NOT_APPLICABLE，problem.reasonCode = ACTION_NOT_ALLOWED_IN_STATE（REJECT_DECLARATION_ON_SETTLED_UNKNOWN_OR_SUPERSEDED_ATTEMPT）',
    ($null -ne $refusal -and [string]$refusal.Payload.outcome -eq 'NOT_APPLICABLE' -and
        [string]$refusal.Payload.slotOperationAttemptId -eq $loadAttempt -and
        [string]$refusal.Payload.problem.reasonCode -eq 'ACTION_NOT_ALLOWED_IN_STATE'),
    "NOT_APPLICABLE / $loadAttempt / ACTION_NOT_ALLOWED_IN_STATE",
    $(if ($refusal) { $refusal.PayloadJson } else { '(no result)' }))

# 否定判据要有界：让运行时再转几轮，再说它什么都没改。
$null = Wait-L2Iterations -Riot $Context.Riot -Count 4 -Journal $journal
$withdrawn = Get-Declaration $lostId
$runtimeAfter = Get-L2Runtime -Connection $connection -DemandId $demandId
$loadAfter = [string](Get-L2StationOperation -Connection $connection -DemandId $demandId -OperationType 'Load').Status
$assertions.Add(
    'G3-07-65', '服务端撤销判定、不改任何业务状态：判定 NOT_APPLICABLE 并记下原因，装货仍 Committed、需求仍 Accepted、旅程没有阻断',
    ($null -ne $withdrawn -and [string]$withdrawn.State -eq 'NOT_APPLICABLE' -and [string]$withdrawn.ResultProblemJson -like '*ACTION_NOT_ALLOWED_IN_STATE*' -and
        $loadAfter -eq 'Committed' -and [string](Get-DemandStatus) -eq 'Accepted' -and [string]$runtimeAfter.Stage -ne 'Blocked'),
    'NOT_APPLICABLE / Committed / Accepted / not Blocked',
    "$(if ($withdrawn) { $withdrawn.State } else { '(no row)' }) / $loadAfter / $(Get-DemandStatus) / $($runtimeAfter.Stage) $($runtimeAfter.BlockReasonCode)")

# --- 4. 卸货站：判定生效 ------------------------------------------------------------------------------------------

$gateIntent = Wait-L2RealIntent -Context $Context -DemandId $demandId -Purpose 'TO_GATE' -TimeoutSeconds 180
Move-L2RealVehicleTo -Context $Context -Intent $gateIntent -StationRiotId $Context.GateStationRiotId -Label 'the gate'
$unload = Wait-L2WaitingOperator -Context $Context -DemandId $demandId -OperationType 'Unload'
$unloadAttempt = $unload.AttemptId
$unloadSlot = $unload.SlotNo
if ($null -eq (Wait-Overdue $unloadSlot 'unload-overdue-reported')) {
    Add-L2RealNotReached $assertions $appliedIds "卸货越过门槛 $([int]$threshold.TotalSeconds + 30) 秒内车载端没有报 $code"
    return
}

$unlockingsBefore = Get-L2PhaseCount -Connection $connection -AttemptId $unloadAttempt -Phase 'UNLOCKING'
$before = @((Get-L2RealTraffic $proxy).lines).Count
$applied = Invoke-Declaration $unloadSlot 'LOCK' '锁舌卡死，门推不开'
$appliedId = [string]($applied.Body.declarationId ?? '')
$appliedCommandId = if (Get-Declaration $appliedId) { ([string](Get-Declaration $appliedId).CommandMessageId).ToLowerInvariant() } else { '' }

$answer = Wait-L2RealOrLast -Description 'the onboard applied the declaration' `
    -Journal $journal -Criterion 'declaration-applied-result' -TimeoutSeconds 30 `
    -Probe { @((Get-L2RealInbound $connection 'SlotFaultDeclarationResult') | Where-Object { [string]$_.Payload.declarationId -eq $appliedId }) | Select-Object -First 1 } `
    -Until { param($v) $null -ne $v }
$result = Wait-L2RealOrLast -Description 'the onboard reported the unload' `
    -Journal $journal -Criterion 'unload-result' -TimeoutSeconds 30 `
    -Probe { (Get-L2OperationResults -Connection $connection -AttemptId $unloadAttempt) | Select-Object -First 1 } `
    -Until { param($v) $null -ne $v }
$answerId = if ($answer) { ([string]$answer.MessageId).ToLowerInvariant() } else { '' }
$resultId = if ($result) { ([string]$result.MessageId).ToLowerInvariant() } else { '' }
$appliedSequence = Wait-L2RealOrLast -Description 'the command, the result, the operation result and both acks crossed the relay' `
    -Journal $journal -Criterion 'applied-sequence' -TimeoutSeconds 20 `
    -Probe { Get-DeclarationSequence $before $appliedCommandId $answerId $resultId } `
    -Until { param($v) (@($v) -join ',') -eq 'SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck,OperationResult,DurableAck' }
$assertions.Add(
    'G3-07-66', '线上顺序与向量一致：SlotFaultDeclarationCommand → SlotFaultDeclarationResult(APPLIED) → DurableAck → OperationResult → DurableAck（SEND_DECLARATION_RESULT_BEFORE_OPERATION_RESULT）',
    ($applied.Status -eq 202 -and (@($appliedSequence) -join ',') -eq 'SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck,OperationResult,DurableAck' -and
        $null -ne $answer -and [string]$answer.Payload.outcome -eq 'APPLIED'),
    '202 / SlotFaultDeclarationCommand,SlotFaultDeclarationResult,DurableAck,OperationResult,DurableAck / APPLIED',
    "$($applied.Status) / $(@($appliedSequence) -join ',') / $(if ($answer) { $answer.Payload.outcome } else { '(no result)' })")

$declaredSlot = if ($result) { @(@($result.Payload.slotResults) | Where-Object { [int]$_.slotNo -eq $unloadSlot })[0] } else { $null }
$otherSlots = if ($result) { @(@($result.Payload.slotResults) | Where-Object { [int]$_.slotNo -ne $unloadSlot }) } else { @() }
$assertions.Add(
    'G3-07-67', "被判仓 UNKNOWN 带 SLOT_FAULT_DECLARED、其余 NOT_STARTED、整体 UNKNOWN（REPORT_DECLARED_SLOT_UNKNOWN_LATER_SLOTS_NOT_STARTED）",
    ($null -ne $declaredSlot -and [string]$result.Payload.overallOutcome -eq 'UNKNOWN' -and [string]$declaredSlot.outcome -eq 'UNKNOWN' -and
        @($declaredSlot.reasonCodes) -contains 'SLOT_FAULT_DECLARED' -and @($otherSlots | Where-Object { [string]$_.outcome -ne 'NOT_STARTED' }).Count -eq 0),
    "UNKNOWN / slot $unloadSlot UNKNOWN [SLOT_FAULT_DECLARED] / 其余 NOT_STARTED",
    $(if ($result) { $result.PayloadJson } else { '(no OperationResult)' }))

$blocked = Wait-L2RealOrLast -Description 'the journey blocked on the declared unload' `
    -Journal $journal -Criterion 'journey-blocked' -TimeoutSeconds 60 `
    -Probe { Get-L2Runtime -Connection $connection -DemandId $demandId } `
    -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'Blocked' }
$session = Wait-L2ConditionOrLast -Description 'the session needs recovery' `
    -Journal $journal -Criterion 'session-recovery-required' -TimeoutSeconds 30 `
    -Probe { Get-L2RealSession $connection $agvId } -Until { param($v) $null -ne $v -and [string]$v.Readiness -eq 'RecoveryRequired' }
$unloadStatus = [string](Get-L2StationOperation -Connection $connection -DemandId $demandId -OperationType 'Unload').Status
$assertions.Add(
    'G3-07-68', '终态与向量一致：卸货仓位操作 RecoveryRequired、旅程 Blocked（UNLOAD_RESULT_REQUIRES_RECOVERY）、需求未结算、会话 RECOVERY_REQUIRED（BLOCK_JOURNEY_ON_DECLARED_UNKNOWN）',
    ($unloadStatus -eq 'RecoveryRequired' -and $null -ne $blocked -and [string]$blocked.Stage -eq 'Blocked' -and
        [string]$blocked.BlockReasonCode -eq 'UNLOAD_RESULT_REQUIRES_RECOVERY' -and [string](Get-DemandStatus) -ne 'Succeeded' -and
        $null -ne $session -and [string]$session.Readiness -eq 'RecoveryRequired'),
    'RecoveryRequired / Blocked UNLOAD_RESULT_REQUIRES_RECOVERY / not Succeeded / RecoveryRequired',
    "$unloadStatus / $(if ($blocked) { "$($blocked.Stage) $($blocked.BlockReasonCode)" } else { '(no runtime)' }) / $(Get-DemandStatus) / $(Format-L2RealSession $session)")

$row = Wait-L2ConditionOrLast -Description 'the declaration is recorded as applied' `
    -Journal $journal -Criterion 'declaration-applied' -TimeoutSeconds 30 `
    -Probe { Get-Declaration $appliedId } -Until { param($v) $null -ne $v -and [string]$v.State -eq 'APPLIED' }
$readings = if ($row -and (Test-L2RealPresent $row.ReadingsJson)) { [string]$row.ReadingsJson | ConvertFrom-Json } else { $null }
$assertions.Add(
    'G3-07-69', '审计判定与车载端结果（AUDIT_DECLARATION_AND_VEHICLE_RESULT）：判定人、角色、车、需求、尝试、仓、类别、说明、带观测时刻的读数、APPLIED 与收到时刻',
    ($null -ne $row -and [string]$row.State -eq 'APPLIED' -and [string]$row.AdministratorId -eq 'G3-MAINTENANCE-383' -and
        [string]$row.AdministratorRole -eq 'MAINTENANCE_ADMINISTRATOR' -and [string]$row.AgvId -eq $agvId -and
        [string]$row.DemandId -eq $demandId -and [string]$row.SlotOperationAttemptId -eq $unloadAttempt -and
        [int]$row.SlotNo -eq $unloadSlot -and [string]$row.FaultCategory -eq 'LOCK' -and [string]$row.Note -eq '锁舌卡死，门推不开' -and
        $null -ne $readings -and $null -ne $readings.observedAt -and (Test-L2RealPresent $row.ResultReceivedAt)),
    'all present', $(if ($row) { $row | ConvertTo-Json -Compress -Depth 4 } else { '(no row)' }))

$journal.Note("The operator shuts slot $unloadSlot's door; a vehicle still executing the unload would reopen it.")
$null = $simulator.Command('Post', "slots/$unloadSlot/close-door", @{})
$shutAt = [DateTimeOffset]::UtcNow
$null = Wait-L2Condition -Description "$reopenWindow after the door was shut" -Journal $journal -Criterion 'reopen-window' `
    -TimeoutSeconds ([int]$reopenWindow.TotalSeconds + 10) -Probe { [DateTimeOffset]::UtcNow } -Until { param($v) $v -ge $shutAt.Add($reopenWindow) }
$physical = Get-L2SlotPhysical -Simulator $simulator -SlotNo $unloadSlot
$unlockingsAfter = Get-L2PhaseCount -Connection $connection -AttemptId $unloadAttempt -Phase 'UNLOCKING'
$assertions.Add(
    'G3-07-70', "判定生效后零开锁（NEVER_UNLOCK_AFTER_DECLARATION_APPLIED）：空关后 $([int]$reopenWindow.TotalSeconds) 秒门仍关着，卸货的 UNLOCKING 进度条数不变",
    ($physical -like 'CLOSED/*' -and $unlockingsAfter -eq $unlockingsBefore),
    "CLOSED/* / UNLOCKING $unlockingsBefore", "$physical / UNLOCKING $unlockingsAfter")

$journal.Note('Scenario finished at Blocked; the recovery exit is the existing exception recovery session.')
