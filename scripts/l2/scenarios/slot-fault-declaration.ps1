#Requires -Version 7

<#
人工判故障（REQ-0359，control-server#383）：管理员在服务端判定车正在等的那一仓有故障。

一条需求走两站，两站各判一次，结局相反：

1. 装货站，「判定到达前操作已闭环」：操作员恰好在管理员按下按钮的同一刻关好了门，车载端拒绝判定
   （NOT_APPLICABLE）。服务端撤销判定，**不改任何业务状态**——仓位操作仍是 Prepared、旅程仍在等装货结果、
   需求仍是 Accepted——随后装货照常完成，车照常去卸货站。合成车载端用控制面把这一刻造出来
   （slot-fault-declaration-policy = NOT_APPLICABLE），此时它手里的装货指令还没有应答，与真车那一瞬间的样子一致。
2. 卸货站，判定生效：车载端回 APPLIED，并照常另发 OperationResult——被判的仓 UNKNOWN、带 SLOT_FAULT_DECLARED，
   后面的仓 NOT_STARTED。服务端走既有的 UNKNOWN 结算：仓位操作转 RecoveryRequired，旅程转 Blocked
   （UNLOAD_RESULT_REQUIRES_RECOVERY），之后按既有异常处置会话收尾（本场景不跑那一段，合成对端不发起恢复握手，
   理由见 load-result-requires-recovery.ps1 文件头）。

**NOT_APPLICABLE 排在前面，是有意的。**它要证的是「不改任何东西」，而车只有在没被阻断时才能继续往下走；
反过来排，第一次判定生效就把旅程停在那里，第二次判定就只能证一个已经停摆的旅程「没有更停摆」。

**红证据**：让结果处理忽略 NOT_APPLICABLE、照样把仓位操作判成需要恢复，第一站的 L2-SFD-03 变红
（旅程被阻断在装货站）。

读取纪律（scripts/l2/README.md 第 14 条）：旅程 Blocked 与判定记录的 APPLIED 落在不同的写入里（前者是引擎下一轮，
后者是处理器收下判定结果那一次），所以等到 Blocked 之后，判定记录用 Wait-L2ConditionOrLast 另等，不读一次就断言。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$connection = $Context.Connection
$agvId = [string]$Context.AgvId
$declarationUri = "http://127.0.0.1:$($Context.HealthPort)/api/safety/v1/slot-fault-declarations"
if (-not $Context.SlotFaultDeclarationCredential) {
    throw 'The setup file must turn SlotFaultDeclaration on; without it this scenario proves nothing.'
}

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SFD-$($Context.RunId)"

function Get-Runtime {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Stage, BlockReasonCode FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stage {
    $runtime = Get-Runtime
    if ($null -eq $runtime) { return $null }
    return [string]$runtime.Stage
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Operation([string]$operationType) {
    $rows = Invoke-L2Query -Connection $connection -Sql @"
SELECT SlotOperationAttemptId, Status, TargetSlotsJson FROM StationOperations
WHERE DemandId = '$demandId' AND OperationType = '$operationType'
"@
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-DemandStatus {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Status
}

function Get-Declaration([string]$declarationId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM SlotFaultDeclarations WHERE DeclarationId = '$declarationId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-PendingOperationKey([string]$attemptId) {
    $pending = @($onboard.Snapshot().body.pending |
        Where-Object { $_.messageType -eq 'SlotOperationCommand' -and [string]$_.key -eq "operation:$attemptId" })
    if ($pending.Count -eq 0) { return $null }
    return [string]$pending[0].key
}

function Get-OverdueProjected([int]$slot) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT AlarmsJson FROM OnboardAlarmSnapshots WHERE AgvId = '$agvId'"
    if ($rows.Count -eq 0) { return $null }
    $alarms = @([string]$rows[0].AlarmsJson | ConvertFrom-Json)
    return @($alarms | Where-Object {
        [string]$_.alarmCode -eq 'SLOT_EXPECTED_ACTION_OVERDUE' -and "$($_.physicalSlotNumber)" -eq "$slot"
    }).Count -gt 0
}

function Set-Overdue([int]$slot, [string]$expectedAction) {
    $null = $onboard.Command('Put', 'alarms', @{
        alarms = @(@{
            code           = 'SLOT_EXPECTED_ACTION_OVERDUE'
            severity       = 'WARNING'
            subjectType    = 'SLOT'
            subjectId      = "$slot"
            displayMessage = $expectedAction
        })
    })
    $journal.Note("Vehicle reports slot $slot overdue: $expectedAction")
    $null = Wait-L2Condition -Description "the server projected slot $slot's expected action as overdue" `
        -Journal $journal -Criterion 'overdue-projected' -TimeoutSeconds 30 `
        -Probe { Get-OverdueProjected $slot } -Until { param($v) $v -eq $true }
}

function Invoke-Declaration([int]$slot, [string]$category, [string]$note) {
    $body = @{
        requestId         = [guid]::NewGuid().ToString('D')
        agvId             = $agvId
        slotNo            = $slot
        faultCategory     = $category
        note              = $note
        operatorId        = 'L2-MAINTENANCE-383'
        administratorRole = 'MAINTENANCE_ADMINISTRATOR'
    }
    $response = Invoke-WebRequest -Method Post -Uri $declarationUri -SkipHttpErrorCheck -TimeoutSec 30 `
        -Headers @{ Authorization = "Bearer $($Context.SlotFaultDeclarationCredential)" } `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json)
    # A refusal is application/problem+json, which Invoke-WebRequest hands back as bytes.
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

function Move-Vehicle([object]$intent, [int]$stationRiotId, [string]$label) {
    $journal.Note("Vehicle departs for the $label.")
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{
        orderState        = 3
        executeVehicleKey = $Context.VehicleKey
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey      = $Context.VehicleKey
        procState       = 'RUNNING'
        movementState   = 'MT_RUNNING'
        speed           = 0.8
        processingOrder = $true
        orderTaskId     = $intent.OrderId
    })
    $journal.Note("Vehicle arrives at the $label and comes to rest.")
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey       = $Context.VehicleKey
        procState        = 'IDLE'
        movementState    = 'MT_FINISHED'
        speed            = 0
        currentPosition  = $stationRiotId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })
}

# --- 0. 两种结果都挂起，由场景决定 ------------------------------------------------------------------------

$null = $onboard.Command('Put', 'policy', @{ loadResult = 'Manual'; unloadResult = 'Manual' })

$journal.Note("Publishing demand $demandIdWire (sublot $sublot).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-UpperId -purpose 'TO_PICKUP'; if ($row -and $row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }
Move-Vehicle $pickupIntent $Context.PickupStationRiotId 'pickup station'

# --- 1. 装货站：判定到达前操作已闭环 -> NOT_APPLICABLE，业务状态不变 ----------------------------------------

# 发件箱、Prepared 的仓位操作与 AwaitingLoadResult 同一次提交（README 第 14 条「下发装货指令那一轮」），
# 所以等到阶段之后再读仓位操作是安全的。
$null = Wait-L2Condition -Description 'the load command was issued and the journey waits for its result' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingLoadResult' }
$load = Get-Operation 'Load'
$loadSlot = [int](@($load.TargetSlotsJson | ConvertFrom-Json)[0])
$loadKey = Wait-L2Condition -Description 'the load command reached the peer and is held open' `
    -Journal $journal -Criterion 'pending-load' -TimeoutSeconds 60 `
    -Probe { Get-PendingOperationKey ([string]$load.SlotOperationAttemptId) } -Until { param($v) $null -ne $v }

Set-Overdue $loadSlot "放入货物并关好${loadSlot}号仓门"
# The operator shuts the door the moment the administrator declares: the peer will refuse it.
$null = $onboard.Command('Put', 'slot-fault-declaration-policy', @{ answer = 'NOT_APPLICABLE' })

$refused = Invoke-Declaration $loadSlot 'LIGHT_CURTAIN' '光幕一直报有物，仓内实际为空'
$assertions.Add(
    'L2-SFD-01', '装货站的判定被服务端受理（202，PENDING）',
    ($refused.Status -eq 202 -and [string]$refused.Body.state -eq 'PENDING'),
    '202 / PENDING', "$($refused.Status) / $($refused.Body.state ?? '(none)')")
$refusedId = [string]$refused.Body.declarationId

$refusedRow = Wait-L2Condition -Description 'the vehicle refused the declaration' `
    -Journal $journal -Criterion 'declaration-not-applicable' -TimeoutSeconds 30 `
    -Probe { Get-Declaration $refusedId } -Until { param($v) $null -ne $v -and [string]$v.State -eq 'NOT_APPLICABLE' }
$assertions.Add(
    'L2-SFD-02', '车载端拒绝判定，服务端记下 NOT_APPLICABLE 与原因',
    ([string]$refusedRow.State -eq 'NOT_APPLICABLE' -and [string]$refusedRow.ResultProblemJson -like '*ACTION_NOT_ALLOWED_IN_STATE*'),
    'NOT_APPLICABLE / ACTION_NOT_ALLOWED_IN_STATE', "$([string]$refusedRow.State) / $([string]$refusedRow.ResultProblemJson)")

# 否定判据要有界：让运行时确实又跑几轮，再说它什么都没改。不用 sleep。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$picture = "$(Get-Stage) / $([string](Get-Operation 'Load').Status) / $(Get-DemandStatus)"
$assertions.Add(
    'L2-SFD-03', '被拒的判定不改任何业务状态：旅程仍等装货结果、仓位操作仍 Prepared、需求仍 Accepted',
    ($picture -eq 'AwaitingLoadResult / Prepared / Accepted'),
    'AwaitingLoadResult / Prepared / Accepted', $picture)

$journal.Note('The door is shut; the peer completes the load.')
$null = $onboard.Command('Put', 'alarms', @{ alarms = @() })
$null = $onboard.Command('Put', 'slot-fault-declaration-policy', @{ answer = 'AUTO' })
$null = $onboard.Command('Put', "answer/$loadKey", @{ completed = $true })

$null = Wait-L2Condition -Description 'the load completed and the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$assertions.Add(
    'L2-SFD-04', '装货照常提交（Committed），车继续去卸货站',
    ([string](Get-Operation 'Load').Status -eq 'Committed'), 'Committed', [string](Get-Operation 'Load').Status)

# --- 2. 卸货站：判定生效 -> APPLIED，仓位操作 RecoveryRequired，旅程 Blocked -------------------------------

$gateIntent = Get-UpperId -purpose 'TO_GATE'
Move-Vehicle $gateIntent $Context.GateStationRiotId 'gate'

$null = Wait-L2Condition -Description 'the unload command was issued and the journey waits for its result' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingUnloadResult' }
$unload = Wait-L2Condition -Description 'the unload operation was prepared' `
    -Journal $journal -Criterion 'unload-prepared' -TimeoutSeconds 30 `
    -Probe { Get-Operation 'Unload' } -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'Prepared' }
$unloadSlot = [int](@($unload.TargetSlotsJson | ConvertFrom-Json)[0])
$null = Wait-L2Condition -Description 'the unload command reached the peer and is held open' `
    -Journal $journal -Criterion 'pending-unload' -TimeoutSeconds 60 `
    -Probe { Get-PendingOperationKey ([string]$unload.SlotOperationAttemptId) } -Until { param($v) $null -ne $v }

Set-Overdue $unloadSlot "取出货物并关好${unloadSlot}号仓门"
$applied = Invoke-Declaration $unloadSlot 'LOCK' '锁舌卡死，门推不开'
$assertions.Add(
    'L2-SFD-05', '卸货站的判定被受理并已下发到车（202，PENDING，sentToVehicle）',
    ($applied.Status -eq 202 -and [string]$applied.Body.state -eq 'PENDING' -and $applied.Body.sentToVehicle -eq $true),
    '202 / PENDING / True', "$($applied.Status) / $($applied.Body.state ?? '(none)') / $($applied.Body.sentToVehicle ?? '(none)')")
$appliedId = [string]$applied.Body.declarationId

$runtime = Wait-L2Condition -Description 'the journey blocked on the declared unload' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Runtime } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'Blocked' }
$assertions.Add(
    'L2-SFD-06', '旅程转 Blocked，原因 UNLOAD_RESULT_REQUIRES_RECOVERY',
    ([string]$runtime.Stage -eq 'Blocked' -and [string]$runtime.BlockReasonCode -eq 'UNLOAD_RESULT_REQUIRES_RECOVERY'),
    'Blocked / UNLOAD_RESULT_REQUIRES_RECOVERY', "$([string]$runtime.Stage) / $([string]$runtime.BlockReasonCode)")

# The second fact is another write (the processor taking the declaration result), so it gets its own wait.
$appliedRow = Wait-L2ConditionOrLast -Description 'the declaration is recorded as applied' `
    -Journal $journal -Criterion 'declaration-applied' -TimeoutSeconds 30 `
    -Probe { Get-Declaration $appliedId } -Until { param($v) $null -ne $v -and [string]$v.State -eq 'APPLIED' }
$assertions.Add(
    'L2-SFD-07', '判定记录为 APPLIED',
    ($null -ne $appliedRow -and [string]$appliedRow.State -eq 'APPLIED'),
    'APPLIED', $(if ($appliedRow) { [string]$appliedRow.State } else { '(no row)' }))

# ApplyOperationResultAsync writes RecoveryRequired in the save that takes the OperationResult, before any round of the
# runtime that then blocks the journey: read after Blocked, it is already there.
$unloadStatus = [string](Get-Operation 'Unload').Status
$assertions.Add(
    'L2-SFD-08', '卸货仓位操作转 RecoveryRequired（走既有 UNKNOWN 结算）',
    ($unloadStatus -eq 'RecoveryRequired'), 'RecoveryRequired', $unloadStatus)

$readings = if ($appliedRow -and $appliedRow.ReadingsJson) { [string]$appliedRow.ReadingsJson | ConvertFrom-Json } else { $null }
$audit = $appliedRow -and
    [string]$appliedRow.AdministratorId -eq 'L2-MAINTENANCE-383' -and
    [string]$appliedRow.AdministratorRole -eq 'MAINTENANCE_ADMINISTRATOR' -and
    [string]$appliedRow.DemandId -eq $demandId -and
    [string]$appliedRow.SlotOperationAttemptId -eq [string]$unload.SlotOperationAttemptId -and
    [int]$appliedRow.SlotNo -eq $unloadSlot -and
    [string]$appliedRow.FaultCategory -eq 'LOCK' -and
    [string]$appliedRow.Note -eq '锁舌卡死，门推不开' -and
    $null -ne $appliedRow.DeclaredAt -and
    $null -ne $readings -and $null -ne $readings.observedAt -and $null -ne $readings.lockState -and
    [string]$appliedRow.ResultOutcome -eq 'APPLIED' -and $null -ne $appliedRow.ResultReceivedAt
$assertions.Add(
    'L2-SFD-09', '审计含 REQ-0359 列的全部项（判定人、角色、时间、车辆、Demand、尝试、仓位、类别、说明、带观测时刻的读数、车载端结果）',
    [bool]$audit, 'all present', $(if ($appliedRow) { ($appliedRow | ConvertTo-Json -Compress -Depth 4) } else { '(no row)' }))

# 服务端能判的前置条件，在运行着的服务端上也判：已判 UNKNOWN 的操作不能再判。
$again = Invoke-Declaration $unloadSlot 'LOCK' '再判一次'
$reasons = @($again.Body.reasons ?? @())
$assertions.Add(
    'L2-SFD-10', '操作已判 UNKNOWN 后再判回 409 SLOT_FAULT_OPERATION_ALREADY_UNKNOWN',
    ($again.Status -eq 409 -and $reasons -contains 'SLOT_FAULT_OPERATION_ALREADY_UNKNOWN'),
    '409 / SLOT_FAULT_OPERATION_ALREADY_UNKNOWN', "$($again.Status) / $($reasons -join ',')")

$journal.Note('Scenario finished at Blocked; the recovery exit is the existing exception recovery session.')
