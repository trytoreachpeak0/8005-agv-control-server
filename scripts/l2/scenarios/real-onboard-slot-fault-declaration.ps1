#Requires -Version 7

<#
人工判故障（REQ-0359）在真装置上走完整条链：装货开锁后没人放货 → 车载端报期待动作超时 → 管理员经服务端接口判定 →
车载端回 SlotFaultDeclarationResult(APPLIED)、停止本次操作的一切开锁、报 OperationResult（被判仓 UNKNOWN 带
SLOT_FAULT_DECLARED）→ 服务端仓位操作 RecoveryRequired、旅程 Blocked；判定之后再空关一次门，车不再重开。

- 来源：control-server#383（服务端）、onboard-hmi#215（车载端，它的「资源」一节列了本场景要断言的车载端行为）；需求基线
  v1.6.0 `REQ-0359`；program 仓 `requirements/change-proposals/CP-0005.md` 第 4.3 节。
- **前提**：`onboard_ref` 含 onboard-hmi#215。之前的车载端不认识 `SlotFaultDeclarationCommand`，收到会断开会话，服务端
  重连后照持久消息的规则补发，于是反复断开重连；本场景会在 L2-RSFD-02 超时并如实记成未到达。
- 前半段照 `real-onboard-expected-action-overdue` 的写法：门槛 20 秒从 setup 读，同时写进两端；到站、扫码、开锁后不放货。
- 判据来源：服务端 SQLite（`ProtocolInbox` 里车载端发来的 `OnboardAlarmSnapshot`／`SlotFaultDeclarationResult`／
  `OperationResult`／`OperationProgress`，`SlotFaultDeclarations`，`StationOperations`，`JourneyRuntimes`）与模拟器 `/snapshot`。
- **「判定之后零开锁」怎么看**：模拟器的开锁输出是 500 ms 脉冲、没有计数器（`Invoke-L2CloseOverOppositeState` 的注释），
  它能看见的是门——自动化接口只能关门不能开门，门只有开锁输出触发时才会弹开。所以判定之后空关一次：车若还在执行这次装货，
  会像门槛前那样重开（ADR-cross-0058 决策 1）；中止了就不会。再加车载端这次尝试的 UNLOCKING 进度条数不增加。
- 读取纪律（`scripts/l2/README.md` 第 14 条）：旅程 Blocked 是引擎的一轮，判定记录的 APPLIED 是处理器收下判定结果那一次，
  两者不在同一次写入里，所以 Blocked 之后判定记录用 `Wait-L2ConditionOrLast` 另等。
- 红证据由 onboard-hmi#215 取（车载端缺陷版本：收到判定只回 APPLIED、不中止不出结果，L2-RSFD-03 变红）。
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
$agvId = [string]$Context.AgvId

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig (see its setup.psd1).'
}
if (-not $Context.SlotFaultDeclarationCredential) {
    throw 'The setup file must turn SlotFaultDeclaration on; without it this scenario proves nothing.'
}

$setup = Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot 'real-onboard-slot-fault-declaration.setup.psd1')
$threshold = Resolve-L2ExpectedActionOverdueThreshold -Setup $setup `
    -Where 'real-onboard-slot-fault-declaration.setup.psd1' -RealOnboard $true
if ($null -eq $threshold) {
    throw 'real-onboard-slot-fault-declaration.setup.psd1 must name ExpectedActionOverdueThreshold: the declaration waits for it.'
}
$code = 'SLOT_EXPECTED_ACTION_OVERDUE'
$declarationUri = "http://127.0.0.1:$($Context.HealthPort)/api/safety/v1/slot-fault-declarations"
# 空关之后等多久再看门。门槛前那次空关，车几秒内就重开（real-onboard-expected-action-overdue 的 L2-EAO-01）；这里给到 10 秒。
$reopenWindow = [TimeSpan]::FromSeconds(10)

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-RSFD-$($Context.RunId)"

function Get-Declaration([string]$declarationId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM SlotFaultDeclarations WHERE DeclarationId = '$declarationId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Invoke-Declaration([int]$slot) {
    $body = @{
        requestId         = [guid]::NewGuid().ToString('D')
        agvId             = $agvId
        slotNo            = $slot
        faultCategory     = 'LOCK'
        note              = '锁舌卡死，门推不开'
        operatorId        = 'L2-MAINTENANCE-383'
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
    $journal.Note("Declaration of slot $slot -> $([int]$response.StatusCode): $text")
    return [pscustomobject]@{
        Status = [int]$response.StatusCode
        Body   = if ($text) { $text | ConvertFrom-Json } else { $null }
    }
}

$allIds = @('L2-RSFD-01', 'L2-RSFD-02', 'L2-RSFD-03', 'L2-RSFD-04', 'L2-RSFD-05', 'L2-RSFD-06', 'L2-RSFD-07')

# --- 1. 到站、扫码，车为装货开门；没人放货，越过门槛 ----------------------------------------------------------------

Invoke-L2PickupAndScan -Context $Context -DemandIdWire $demandIdWire -DemandId $demandId -Sublot $sublot
$load = Wait-L2WaitingOperator -Context $Context -DemandId $demandId -OperationType 'Load'
$attemptId = $load.AttemptId
$slotNo = $load.SlotNo

$overdue = Wait-L2RealOrLast -Description "the onboard reported $code for slot $slotNo" `
    -Journal $journal -Criterion 'overdue-reported' -TimeoutSeconds ([int]$threshold.TotalSeconds + 30) `
    -Probe {
        @((Get-L2RealInbound $connection 'OnboardAlarmSnapshot') | Where-Object {
                @(@($_.Payload.alarms) | Where-Object { $null -ne $_ -and [string]$_.code -eq $code -and [string]$_.subjectId -eq [string]$slotNo }).Count -gt 0
            }) | Select-Object -First 1
    } `
    -Until { param($v) $null -ne $v }
if ($null -eq $overdue) {
    Add-L2RealNotReached $assertions $allIds "越过门槛 $([int]$threshold.TotalSeconds + 30) 秒内车载端没有报 $code"
    $journal.Note('Scenario stopped: no overdue slot to declare.')
    return
}

# --- 2. 管理员判定 -----------------------------------------------------------------------------------------------

$unlockingsBefore = Get-L2PhaseCount -Connection $connection -AttemptId $attemptId -Phase 'UNLOCKING'
$declared = Invoke-Declaration $slotNo
$assertions.Add(
    'L2-RSFD-01', '判定被受理并已下发到车：202、PENDING、sentToVehicle，demandId 与尝试是这次装货的',
    ($declared.Status -eq 202 -and [string]$declared.Body.state -eq 'PENDING' -and $declared.Body.sentToVehicle -eq $true -and
        [string]$declared.Body.demandId -eq $demandId -and [string]$declared.Body.slotOperationAttemptId -eq $attemptId),
    "202 / PENDING / True / $demandId / $attemptId",
    "$($declared.Status) / $($declared.Body.state ?? '(none)') / $($declared.Body.sentToVehicle ?? '(none)') / $($declared.Body.demandId ?? '(none)') / $($declared.Body.slotOperationAttemptId ?? '(none)')")
$declarationId = [string]($declared.Body.declarationId ?? '')

# --- 3. 车载端回 APPLIED，并另报 OperationResult ---------------------------------------------------------------------

$answer = Wait-L2RealOrLast -Description 'the onboard answered the declaration' `
    -Journal $journal -Criterion 'declaration-result' -TimeoutSeconds 30 `
    -Probe { @((Get-L2RealInbound $connection 'SlotFaultDeclarationResult') | Where-Object { [string]$_.Payload.declarationId -eq $declarationId }) | Select-Object -First 1 } `
    -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-RSFD-02', '线上 SlotFaultDeclarationResult：APPLIED、同一个尝试、problem 为空，服务端 DurableAck',
    ($null -ne $answer -and [string]$answer.Payload.outcome -eq 'APPLIED' -and
        [string]$answer.Payload.slotOperationAttemptId -eq $attemptId -and $null -eq $answer.Payload.problem -and $answer.Response -eq 'DurableAck'),
    "APPLIED / $attemptId / problem null / DurableAck",
    $(if ($null -eq $answer) { '(no SlotFaultDeclarationResult)' } else { "$($answer.Payload.outcome) / $($answer.Payload.slotOperationAttemptId) / problem $($answer.PayloadJson) / $($answer.Response)" }))

$result = Wait-L2RealOrLast -Description 'the onboard reported the operation' `
    -Journal $journal -Criterion 'operation-result' -TimeoutSeconds 30 `
    -Probe { (Get-L2OperationResults -Connection $connection -AttemptId $attemptId) | Select-Object -First 1 } `
    -Until { param($v) $null -ne $v }
$declaredSlot = if ($null -ne $result) { @(@($result.Payload.slotResults) | Where-Object { [int]$_.slotNo -eq $slotNo })[0] } else { $null }
$otherSlots = if ($null -ne $result) { @(@($result.Payload.slotResults) | Where-Object { [int]$_.slotNo -ne $slotNo }) } else { @() }
$assertions.Add(
    'L2-RSFD-03', "OperationResult：overallOutcome UNKNOWN；$slotNo 号仓 UNKNOWN 且 reasonCodes 含 SLOT_FAULT_DECLARED；其余目标仓 NOT_STARTED",
    ($null -ne $declaredSlot -and [string]$result.Payload.overallOutcome -eq 'UNKNOWN' -and [string]$declaredSlot.outcome -eq 'UNKNOWN' -and
        @($declaredSlot.reasonCodes) -contains 'SLOT_FAULT_DECLARED' -and
        @($otherSlots | Where-Object { [string]$_.outcome -ne 'NOT_STARTED' }).Count -eq 0),
    "UNKNOWN / slot $slotNo UNKNOWN [SLOT_FAULT_DECLARED] / 其余 NOT_STARTED",
    $(if ($null -eq $result) { '(no OperationResult)' } else { "$($result.Payload.overallOutcome) / $($result.PayloadJson)" }))

# --- 4. 服务端：RecoveryRequired、Blocked，判定记为 APPLIED ---------------------------------------------------------

$runtime = Wait-L2RealOrLast -Description 'the journey blocked on the declared load' `
    -Journal $journal -Criterion 'journey-blocked' -TimeoutSeconds 60 `
    -Probe { Get-L2Runtime -Connection $connection -DemandId $demandId } `
    -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'Blocked' }
$assertions.Add(
    'L2-RSFD-04', '旅程转 Blocked，原因 LOAD_RESULT_REQUIRES_RECOVERY',
    ($null -ne $runtime -and [string]$runtime.Stage -eq 'Blocked' -and [string]$runtime.BlockReasonCode -eq 'LOAD_RESULT_REQUIRES_RECOVERY'),
    'Blocked / LOAD_RESULT_REQUIRES_RECOVERY', $(if ($runtime) { "$($runtime.Stage) / $($runtime.BlockReasonCode)" } else { '(no runtime)' }))

# The operation was judged in the save that took the OperationResult, before the round that blocked the journey.
$operationStatus = [string](Get-L2StationOperation -Connection $connection -DemandId $demandId -OperationType 'Load').Status
$assertions.Add(
    'L2-RSFD-05', '装货仓位操作 RecoveryRequired（既有 UNKNOWN 结算）',
    ($operationStatus -eq 'RecoveryRequired'), 'RecoveryRequired', $operationStatus)

$row = Wait-L2ConditionOrLast -Description 'the declaration is recorded as applied' `
    -Journal $journal -Criterion 'declaration-applied' -TimeoutSeconds 30 `
    -Probe { Get-Declaration $declarationId } -Until { param($v) $null -ne $v -and [string]$v.State -eq 'APPLIED' }
$readings = if ($row -and (Test-L2RealPresent $row.ReadingsJson)) { [string]$row.ReadingsJson | ConvertFrom-Json } else { $null }
$assertions.Add(
    'L2-RSFD-06', '判定记录 APPLIED，审计带判定时车载端最近上报的读数与观测时刻',
    ($null -ne $row -and [string]$row.State -eq 'APPLIED' -and [int]$row.SlotNo -eq $slotNo -and
        [string]$row.SlotOperationAttemptId -eq $attemptId -and $null -ne $readings -and $null -ne $readings.observedAt),
    "APPLIED / slot $slotNo / $attemptId / readings with observedAt",
    $(if ($row) { "$($row.State) / slot $($row.SlotNo) / $($row.SlotOperationAttemptId) / $($row.ReadingsJson)" } else { '(no row)' }))

# --- 5. 判定之后零开锁：空关一次，车不再重开 --------------------------------------------------------------------------

$journal.Note("The operator shuts slot $slotNo's door over nothing; before the declaration this made the vehicle reopen it.")
$null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})
$shutAt = [DateTimeOffset]::UtcNow
$null = Wait-L2Condition -Description "$reopenWindow after the door was shut" -Journal $journal -Criterion 'reopen-window' `
    -TimeoutSeconds ([int]$reopenWindow.TotalSeconds + 10) -Probe { [DateTimeOffset]::UtcNow } -Until { param($v) $v -ge $shutAt.Add($reopenWindow) }
$physical = Get-L2SlotPhysical -Simulator $simulator -SlotNo $slotNo
$unlockingsAfter = Get-L2PhaseCount -Connection $connection -AttemptId $attemptId -Phase 'UNLOCKING'
$assertions.Add(
    'L2-RSFD-07', "判定之后零开锁：空关后 $([int]$reopenWindow.TotalSeconds) 秒门仍关着，这次装货的 UNLOCKING 进度条数与判定前相同",
    ($physical -like 'CLOSED/*' -and $unlockingsAfter -eq $unlockingsBefore),
    "CLOSED/* / UNLOCKING $unlockingsBefore", "$physical / UNLOCKING $unlockingsAfter")

$journal.Note('Scenario finished at Blocked; the recovery exit is the existing exception recovery session.')
