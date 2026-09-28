#Requires -Version 7

<#
本服务端的单在 RIoT 被取消、车上有货：同车重建之前先证明货物仍在原仓位（control-server#366，CP-0007 修订的 REQ-0360）。
真车载端 WPF + 真 slots-simulator。快照的索取与回答走真车载端（SafetyStateSnapshotRequested → SafetyStateSnapshot），
合成车载端按构造看不见这一段。

第 1 件：别人的急停锁着期间，货没了。
  1. 装货提交、建出 TO_GATE 单；车在去卸货站的路上被别人急停（RIoT 锁着，单 HANG，服务端没有发过触发）。
  2. 人在 RIoT 里取消这张单：服务端记下取消来源的重建。
  3. 锁着期间货没了：装货那一仓的光幕固定成「没挡住」（模拟器不许门关着改货物，会报 DOOR_NOT_OPEN）。延迟在锁着期间走完，
     重建被车况挡着，记录上记下这一次挡住开始的时刻（VehicleHeldAt）。
  4. 别人解开急停：服务端不建开往卸货站的单，重新向车要快照，只认解开之后收到的那一份；那一份显示放货的仓是空的，重建停住，
     旅程码 OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE。
  5. 人经 control-server#345 的出口转交接，车载端给出「故障交接」入口，维护人员交接；需求终止、旅程收尾、记录 ENDED、会话回到就绪。
     这一趟从头到尾没有故障货物绑定——#345 的出口原本是为故障清除来源做的。
第 2 件：同样取消，但货在。
  6. 下一条需求装货、建出 TO_GATE 单，车在路上，单被取消（没有急停）。
  7. 延迟到点后服务端向车要快照，收到之后才重建开往卸货站的单：证明它的那份快照晚于重建到期。

修之前第 1 件在第 4 步就错：解开急停的那一轮直接建开往卸货站的单（cs#349 第 5 格），第 2 件在第 7 步不等快照就建。

**读取函数的写法。**`Invoke-L2Query` 与 `Get-G3Inbound`／`Get-G3Outbound` 都以 `return , @(...)` 返回：一律先赋值再用，
不写 `@(f)`，也不写 `@(f) | ...`；要走管道时写 `(f) | ...`。夹具里一条需求装一仓；按条件筛的地方都在判据里把筛出的条数一并断言
（筛成空会红，而不是在空集上恒真）。
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
$agvId = [string]$Context.AgvId
$recoveryUri = "http://127.0.0.1:$($Context.HealthPort)/api/safety/v1/vehicle-fault-recoveries"

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: the snapshot and the handoff entry are the onboard HMI.'
}
if (-not $Context.FaultRecoveryCredential) {
    throw 'The setup file must turn VehicleFaultRecovery on; without it this scenario proves nothing.'
}

# 光幕被固定成「没挡住」的仓（第 1 件里货没了的那一仓）；$null 表示没有。见 Invoke-LoadedTrip 里放货那一步。
$script:emptiedSlot = $null

$ids = @('L2-RC-01', 'L2-RC-02', 'L2-RC-03', 'L2-RC-04', 'L2-RC-05', 'L2-RC-06', 'L2-RC-07', 'L2-RC-08', 'L2-RC-09', 'L2-RC-10', 'L2-RC-11')

function Get-Journey([string]$forDemand) {
    # 先赋值再用（见文件头）。
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT JourneyId, Stage, BlockReasonCode, GateUpperId FROM JourneyRuntimes WHERE DemandId = '$forDemand'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$forDemand, [string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$forDemand' AND Purpose = '$purpose' ORDER BY CreatedAt"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-GateCount([string]$forDemand) {
    return Get-G3Count $connection "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$forDemand' AND Purpose = 'TO_GATE'"
}

function Get-Rebuild([string]$endedUpperId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT Source, State, StoppedReason, WaitingReason, RecordedAt, DueAt, VehicleHeldAt, CargoEvidenceNotBefore, " +
        "CargoEvidenceMessageId, NewUpperId FROM OwnOrderRebuilds WHERE EndedUpperId = '$endedUpperId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 证明（或证明不了）货在原仓的那份快照，服务端收下的时刻；没有就是 $null。
function Get-EvidenceReceivedAt([object]$record) {
    if ($null -eq $record -or -not (Test-G3Present $record.CargoEvidenceMessageId)) { return $null }
    $at = Get-G3Scalar $connection "SELECT ReceivedAt AS Value FROM ProtocolInbox WHERE MessageId = '$($record.CargoEvidenceMessageId)'"
    if (-not (Test-G3Present $at)) { return $null }
    return ConvertTo-G3Instant $at
}

function Invoke-Exit([string]$action, [string]$label) {
    $body = @{
        agvId         = $agvId
        operatorId    = 'L2-OPERATOR-366'
        action        = $action
        faultRemedied = $true
        note          = "L2 $($Context.RunId)"
    }
    $response = Invoke-WebRequest -Method Post -Uri $recoveryUri -SkipHttpErrorCheck -TimeoutSec 60 `
        -Headers @{ Authorization = "Bearer $($Context.FaultRecoveryCredential)" } `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json)
    # A refusal is application/problem+json, which Invoke-WebRequest does not treat as text: Content arrives as bytes.
    $text = if ($response.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($response.Content)
    } else {
        [string]$response.Content
    }
    $journal.Note("Fault recovery $action ($label) -> $([int]$response.StatusCode): $text")
    return [pscustomobject]@{
        Status = [int]$response.StatusCode
        Body   = if ($text) { $text | ConvertFrom-Json } else { $null }
    }
}

function Get-Field([object]$response, [string]$name) {
    # A refusal's problem body has no `outcome`; under StrictMode reading it throws instead of failing the criterion.
    if ($null -eq $response.Body -or -not ($response.Body.PSObject.Properties.Name -contains $name)) { return '' }
    return [string]$response.Body.$name
}

function Get-TriggerCount {
    $invocations = @($riot.Snapshot().body.commandInvocations)
    return @($invocations | Where-Object { [string]$_.commandType -eq 'triggerEmergency' }).Count
}

function Set-Vehicle([hashtable]$fields) {
    $fields['vehicleKey'] = $Context.VehicleKey
    $null = $riot.Command('Put', 'vehicle', $fields)
}

# 受理一条需求、车到取货站、UIA 录入、放货关门，直到 TO_GATE 意图 CONFIRMED。返回需求号、装货的仓与 TO_GATE 意图。
# 与 real-onboard-normal-load 的 Invoke-SlotOperation 同一个道理：等车载端自己报 WAITING_OPERATOR 再放货。
function Invoke-LoadedTrip([string]$label) {
    $guid = [guid]::NewGuid()
    $forDemand = $guid.ToString('D')
    $sublot = "L2-RC-$($Context.RunId)-$label"
    $journal.Note("Publishing demand $($guid.ToString('N')) (sublot $sublot).")
    $null = $Context.MesIngest.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot = $sublot; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
    })
    $pickup = Wait-L2Condition -Description "the TO_PICKUP intent of $label was confirmed" `
        -Journal $journal -Criterion "to-pickup-intent-$label" -TimeoutSeconds 120 `
        -Probe { Get-Intent $forDemand 'TO_PICKUP' } -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }

    $journal.Note('Vehicle drives to the pickup station and comes to rest there.')
    $null = $riot.Command('Put', "orders/$($pickup.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    Set-Vehicle @{ procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8; processingOrder = $true; orderTaskId = $pickup.OrderId }
    Set-Vehicle @{
        procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Context.PickupStationRiotId; processingOrder = $false; clearOrderTaskId = $true
    }
    $null = $riot.Command('Put', "orders/$($pickup.UpperId)", @{ orderState = 5 })

    $null = Wait-L2Condition -Description "the onboard HMI accepted sublot entry ($label)" `
        -Journal $journal -Criterion "onboard-can-submit-$label" -TimeoutSeconds 180 `
        -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
    $onboard.SetSublot($sublot)
    $null = Wait-L2Condition -Description "the manual submit button became enabled ($label)" `
        -Journal $journal -Criterion "onboard-submit-ready-$label" -TimeoutSeconds 30 `
        -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
    $onboard.Submit()

    $attemptId = Wait-L2Condition -Description "the server issued the load command ($label)" `
        -Journal $journal -Criterion "load-attempt-$label" -TimeoutSeconds 180 `
        -Probe { Get-G3Scalar $connection "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$forDemand' AND OperationType = 'Load'" } `
        -Until { param($v) $v }
    $waiting = Wait-L2Condition -Description "the onboard is waiting for the operator ($label)" `
        -Journal $journal -Criterion "load-waiting-operator-$label" -TimeoutSeconds 120 `
        -Probe { @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })[0] } `
        -Until { param($v) $null -ne $v }
    if (@($waiting.Active).Count -ne 1) {
        throw "This scenario drives one slot per load; the command targets $(@($waiting.Active).Count) ($(@($waiting.Active) -join ', '))."
    }
    $slot = [int]$waiting.Active[0]
    if ($script:emptiedSlot -eq $slot) {
        # 第 1 件交接之后，这一仓在模拟器里仍是「有货」，只是光幕被固定成「没挡住」——那正是货被搬走之后车载端读到的样子。
        # 门此刻开着，操作员放进新货：光幕交还给模拟器（AUTO）。放早了（门关着时），车载端会读到有货而拒绝装货：
        # CI run 36390242195 第 2 件就是这样红的（装货结果 FAILED，1 号仓 State=1）。
        $journal.Note("Slot $slot light curtain back to AUTO: the operator puts a new basket into the slot emptied in part 1.")
        $null = $simulator.Command('Put', "slots/$slot/light-curtain-override", @{ mode = 'AUTO' })
        $script:emptiedSlot = $null
    }
    $journal.Note("Onboard is waiting on slot $slot; the operator puts the basket in and closes the door.")
    $null = $simulator.Command('Put', "slots/$slot/cargo", @{ state = 'OCCUPIED' })
    $null = $simulator.Command('Post', "slots/$slot/close-door", @{})
    $null = Wait-L2Condition -Description "slot $slot reads closed, occupied, locked and reset ($label)" `
        -Journal $journal -Criterion "load-slot-physical-$label" -TimeoutSeconds 60 `
        -Probe { Get-G3SlotState $simulator $slot } -Until { param($v) $v -eq 'CLOSED/OCCUPIED/1/0' }
    $gate = Wait-L2Condition -Description "the load committed and the TO_GATE intent was confirmed ($label)" `
        -Journal $journal -Criterion "to-gate-intent-$label" -TimeoutSeconds 180 `
        -Probe { Get-Intent $forDemand 'TO_GATE' } -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
    return [pscustomobject]@{ DemandId = $forDemand; Slot = $slot; Gate = $gate }
}

# ================================================================================================================
# 第 1 件：别人的急停锁着期间货没了
# ================================================================================================================

# --- 1. 装货提交、车在去卸货站的路上被别人急停，单 HANG --------------------------------------------------------

$first = Invoke-LoadedTrip 'EMPTIED'
$demandId = $first.DemandId
$gateUpper = [string]$first.Gate.UpperId
$journal.Note("Vehicle sets off for the gate on $gateUpper; someone else presses the emergency stop on the way.")
$null = $riot.Command('Put', "orders/$gateUpper", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
Set-Vehicle @{ procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8; processingOrder = $true; orderTaskId = $first.Gate.OrderId }
Set-Vehicle @{ emergencyState = 'CAN_RECOVER'; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0 }
$null = $riot.Command('Put', "orders/$gateUpper", @{ orderState = 9 })
$hung = Wait-L2ConditionOrLast -Description 'the journey names the HANG under someone else''s stop' `
    -Journal $journal -Criterion 'order-hang' -TimeoutSeconds 60 `
    -Probe { $j = Get-Journey $demandId; if ($j) { [string]$j.BlockReasonCode } else { '' } } `
    -Until { param($v) $v -eq 'ORDER_HANG' }

# --- 2. 人在 RIoT 里取消这张单 -----------------------------------------------------------------------------------

$journal.Note("Someone cancels $gateUpper in RIoT while the vehicle is latched.")
$null = $riot.Command('Put', "orders/$gateUpper", @{ orderState = 2 })
Set-Vehicle @{ processingOrder = $false; clearOrderTaskId = $true }
$recorded = Wait-L2ConditionOrLast -Description 'the server recorded the cancellation to be rebuilt' `
    -Journal $journal -Criterion 'cancel-recorded' -TimeoutSeconds 60 `
    -Probe { Get-Rebuild $gateUpper } `
    -Until { param($v) $null -ne $v -and [string]$v.Source -eq 'ORDER_CANCELLED_IN_RIOT' }
$triggers = Get-TriggerCount
$assertions.Add(
    'L2-RC-01',
    '别人的急停锁着（服务端没有发过触发），单 HANG 之后被人在 RIoT 取消：服务端记下取消来源的同车重建（ORDER_CANCELLED_IN_RIOT / PENDING），只有原来那一张 TO_GATE 单',
    ($hung -eq 'ORDER_HANG' -and $null -ne $recorded -and [string]$recorded.Source -eq 'ORDER_CANCELLED_IN_RIOT' -and
        [string]$recorded.State -eq 'PENDING' -and $triggers -eq 0 -and (Get-GateCount $demandId) -eq 1),
    'ORDER_HANG / ORDER_CANCELLED_IN_RIOT PENDING / 触发 0 / TO_GATE 1',
    "$hung / $(if ($recorded) { "$($recorded.Source) $($recorded.State)" } else { '(没有记录)' }) / 触发 $triggers / TO_GATE $(Get-GateCount $demandId)")
if ($null -eq $recorded -or [string]$recorded.State -ne 'PENDING') {
    Add-G3NotReached $assertions ($ids | Select-Object -Skip 1) '取消没有记成待重建'
    return
}

# --- 3. 锁着期间货没了；延迟在锁着期间走完，重建被车况挡住 ----------------------------------------------------------

# 模拟器不许门关着改货物（DOOR_NOT_OPEN），而车载端读货物用的就是光幕那一路 DI。读出现在（有货）的原始值，把它固定成反面。
$slotBefore = @($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $first.Slot })
if ($slotBefore.Count -ne 1) { throw "The simulator snapshot has $($slotBefore.Count) rows for slot $($first.Slot)." }
$emptyRaw = 1 - [int]$slotBefore[0].lightCurtainRaw
$journal.Note("While the vehicle is latched the basket goes missing: slot $($first.Slot) light curtain forced to $emptyRaw (unobstructed).")
$null = $simulator.Command('Put', "slots/$($first.Slot)/light-curtain-override", @{ mode = "FIXED_$emptyRaw" })
$script:emptiedSlot = $first.Slot
$null = Wait-L2Condition -Description "slot $($first.Slot) light curtain reads $emptyRaw" `
    -Journal $journal -Criterion 'light-curtain-empty' -TimeoutSeconds 30 `
    -Probe { [int]@($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $first.Slot })[0].lightCurtainRaw } `
    -Until { param($v) $v -eq $emptyRaw }

$held = Wait-L2ConditionOrLast -Description 'the rebuild fell due while latched and is held by the vehicle''s condition' `
    -Journal $journal -Criterion 'rebuild-held' -TimeoutSeconds 60 `
    -Probe {
        $record = Get-Rebuild $gateUpper
        $journey = Get-Journey $demandId
        [pscustomobject]@{
            Code    = if ($journey) { [string]$journey.BlockReasonCode } else { '' }
            State   = if ($record) { [string]$record.State } else { '' }
            Waiting = if ($record) { [string]$record.WaitingReason } else { '' }
            Held    = if ($record) { Test-G3Present $record.VehicleHeldAt } else { $false }
        }
    } `
    -Until { param($v) $v.Code -eq 'OWN_ORDER_REBUILD_WAITING_VEHICLE' -and $v.Held }
$assertions.Add(
    'L2-RC-02',
    '延迟在锁着期间走完：重建被车况挡住（OWN_ORDER_REBUILD_WAITING_VEHICLE），记录仍 PENDING、记下这一次挡住开始（VehicleHeldAt），不建新单',
    ($held.Code -eq 'OWN_ORDER_REBUILD_WAITING_VEHICLE' -and $held.State -eq 'PENDING' -and $held.Held -and (Get-GateCount $demandId) -eq 1),
    'OWN_ORDER_REBUILD_WAITING_VEHICLE / PENDING / VehicleHeldAt 有值 / TO_GATE 1',
    "$($held.Code) / $($held.State) ($($held.Waiting)) / VehicleHeldAt $(if ($held.Held) { '有值' } else { '空' }) / TO_GATE $(Get-GateCount $demandId)")

# --- 4. 别人解开急停：不建单，等解开之后的快照；那一份显示仓空，停住 ---------------------------------------------------

$journal.Note('Someone else releases the emergency stop.')
Set-Vehicle @{ emergencyState = 'OK' }
$stopped = Wait-L2ConditionOrLast -Description 'the rebuild stopped on a snapshot received after the release' `
    -Journal $journal -Criterion 'cargo-not-in-place' -TimeoutSeconds 120 `
    -Probe {
        $journey = Get-Journey $demandId
        $record = Get-Rebuild $gateUpper
        [pscustomobject]@{
            Stage     = if ($journey) { [string]$journey.Stage } else { '' }
            Code      = if ($journey) { [string]$journey.BlockReasonCode } else { '' }
            State     = if ($record) { [string]$record.State } else { '' }
            Reason    = if ($record) { [string]$record.StoppedReason } else { '' }
            Held      = if ($record) { Test-G3Present $record.VehicleHeldAt } else { $true }
            NotBefore = if ($record -and (Test-G3Present $record.CargoEvidenceNotBefore)) { ConvertTo-G3Instant $record.CargoEvidenceNotBefore } else { $null }
            DueAt     = if ($record) { ConvertTo-G3Instant $record.DueAt } else { $null }
            Evidence  = Get-EvidenceReceivedAt $record
            Gates     = Get-GateCount $demandId
        }
    } `
    -Until { param($v) $v.State -in @('STOPPED', 'REBUILT', 'ORDERING') -or $v.Gates -ne 1 }
$afterRelease = $null -ne $stopped.NotBefore -and $null -ne $stopped.Evidence -and $stopped.Evidence -gt $stopped.NotBefore -and
    $stopped.Evidence -gt $stopped.DueAt
$assertions.Add(
    'L2-RC-03',
    '解开急停之后不建开往卸货站的单：证据是解开之后收到的快照（晚于 CargoEvidenceNotBefore 与到期），它显示放货的仓是空的，重建停住（STOPPED / CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS），旅程码 OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE、仍在去关卡的阶段，只有原来那一张 TO_GATE 单',
    ($stopped.Code -eq 'OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE' -and $stopped.State -eq 'STOPPED' -and
        $stopped.Reason -eq 'CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS' -and $stopped.Stage -eq 'AwaitingGateArrival' -and
        -not $stopped.Held -and $afterRelease -and $stopped.Gates -eq 1),
    'AwaitingGateArrival / OWN_ORDER_REBUILD_CARGO_NOT_IN_PLACE / STOPPED CARGO_NOT_PROVEN_IN_ORIGINAL_SLOTS / 证据晚于放开与到期 / TO_GATE 1',
    "$($stopped.Stage) / $($stopped.Code) / $($stopped.State) $($stopped.Reason) / 证据 $(if ($null -ne $stopped.Evidence) { $stopped.Evidence.ToString('o') } else { '(无)' }) 放开 $(if ($null -ne $stopped.NotBefore) { $stopped.NotBefore.ToString('o') } else { '(无)' }) 到期 $(if ($null -ne $stopped.DueAt) { $stopped.DueAt.ToString('o') } else { '(无)' }) / TO_GATE $($stopped.Gates)")
if ($stopped.State -ne 'STOPPED') {
    Add-G3NotReached $assertions ($ids | Select-Object -Skip 3) '重建没有因货不在原仓停住'
    return
}

# --- 5. 转交接，车载端给出「故障交接」入口，维护人员交接，走到底 ------------------------------------------------------

$prepared = Invoke-Exit 'PREPARE_CARGO_HANDOFF' 'handoff'
$handed = Wait-L2ConditionOrLast -Description 'the journey is held for the handoff and the session asks for it' `
    -Journal $journal -Criterion 'handoff-prepared' -TimeoutSeconds 60 `
    -Probe {
        $journey = Get-Journey $demandId
        $record = Get-Rebuild $gateUpper
        $session = Get-G3Session $connection
        [pscustomobject]@{
            Journey = if ($journey) { "$($journey.Stage)/$($journey.BlockReasonCode)" } else { '' }
            Record  = if ($record) { [string]$record.State } else { '' }
            Session = if ($session) { "$($session.Readiness)/$($session.ReasonCode)" } else { '' }
        }
    } `
    -Until { param($v) $v.Record -eq 'AWAITING_CARGO_HANDOFF' -and $v.Session -eq 'RecoveryRequired/CARGO_HANDOFF_REQUIRED' }
$assertions.Add(
    'L2-RC-04',
    '取消来源停住之后人工转交接被受理（200 HandoffPrepared）：旅程 Blocked / OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF，记录 AWAITING_CARGO_HANDOFF，会话 RecoveryRequired / CARGO_HANDOFF_REQUIRED',
    ($prepared.Status -eq 200 -and (Get-Field $prepared 'outcome') -eq 'HandoffPrepared' -and
        $handed.Journey -eq 'Blocked/OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF' -and $handed.Record -eq 'AWAITING_CARGO_HANDOFF' -and
        $handed.Session -eq 'RecoveryRequired/CARGO_HANDOFF_REQUIRED'),
    '200 HandoffPrepared / Blocked/OWN_ORDER_REBUILD_AWAITING_CARGO_HANDOFF / AWAITING_CARGO_HANDOFF / RecoveryRequired/CARGO_HANDOFF_REQUIRED',
    "$($prepared.Status) $(Get-Field $prepared 'outcome') / $($handed.Journey) / $($handed.Record) / $($handed.Session)")

$offered = Wait-G3ButtonOffered $onboard $journal '故障交接' 'onboard-fault-cargo-entry' 90
$assertions.Add(
    'L2-RC-05',
    '车载端给出「故障交接」入口：这一趟没有故障，也没有故障货物绑定，入口照样出现',
    [bool]$offered, 'offered', $(if ($offered) { 'offered' } else { '90 秒内没出现' }))
if (-not $offered) {
    Add-G3NotReached $assertions ($ids | Select-Object -Skip 5) '车载端没有给出「故障交接」入口'
    return
}
$requestedAt = [DateTimeOffset]::UtcNow
$journal.Note('Maintenance presses 故障交接 and confirms.')
$null = Invoke-G3ConfirmedButton $onboard $journal '故障交接' '故障货物交接'

$result = Wait-L2Condition -Description 'the server received FaultCargoRecoveryResult, or the onboard reported a refusal' `
    -Journal $journal -Criterion 'fault-cargo-result' -TimeoutSeconds 120 `
    -Probe {
        # 先赋值再筛：Get-G3Inbound 以 `return , @(...)` 返回。
        $inbound = Get-G3Inbound $connection 'FaultCargoRecoveryResult'
        $received = @($inbound | Where-Object { [string]$_.Payload.demandId -eq $demandId })
        if ($received.Count -ge 1) { $received[0] }
        elseif (@($onboard.WindowTitles()) -contains '故障交接失败') { 'REFUSED' }
        else { $null }
    } -Until { param($v) $null -ne $v }
if ($result -is [string]) {
    Add-G3NotReached $assertions ($ids | Select-Object -Skip 5) '故障交接未被接受（车载端弹出「故障交接失败」）'
    return
}
$actionId = [string]$result.Payload.recoveryActionId
$null = Wait-L2Condition -Description 'the handoff workflow settled' `
    -Journal $journal -Criterion 'fault-cargo-workflow' -TimeoutSeconds 30 `
    -Probe { Get-G3Scalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'" } `
    -Until { param($v) $v -in @('Reconciled', 'RecoveryRequired') }
$inboundActions = Get-G3Inbound $connection 'RecoveryActionSubmitted'
$actions = @($inboundActions | Where-Object { [string]$_.Payload.demandId -eq $demandId })
$workflow = Invoke-L2Query -Connection $connection -Sql "SELECT State, Outcome FROM RecoveryWorkflows WHERE WorkflowId = '$actionId'"
$assertions.Add(
    'L2-RC-06',
    '车载端的交接走完 CV-FAULT-CARGO-HANDOFF：RecoveryActionSubmitted(FAULT_CARGO_HANDOFF) 被接受，结果 HANDED_OFF；工作流 Reconciled / HANDED_OFF',
    ($actions.Count -eq 1 -and $actions[0].Response -eq 'RecoveryActionAccepted' -and $actions[0].At -ge $requestedAt.AddSeconds(-1) -and
        [string]$result.Payload.overallOutcome -eq 'HANDED_OFF' -and
        $workflow.Count -eq 1 -and [string]$workflow[0].State -eq 'Reconciled' -and [string]$workflow[0].Outcome -eq 'HANDED_OFF'),
    'Action×1 RecoveryActionAccepted / HANDED_OFF / Reconciled HANDED_OFF',
    "Action×$($actions.Count)$(if ($actions.Count -ge 1) { " $($actions[0].Response)" }) / $($result.Payload.overallOutcome) / $(if ($workflow.Count -eq 1) { "$($workflow[0].State) $($workflow[0].Outcome)" } else { "(工作流 $($workflow.Count) 行)" })")

$settled = Wait-L2ConditionOrLast -Description 'the handoff settled on the server and the session came back ready' `
    -Journal $journal -Criterion 'handoff-settled' -TimeoutSeconds 60 `
    -Probe {
        $journey = Get-Journey $demandId
        $record = Get-Rebuild $gateUpper
        $session = Get-G3Session $connection
        [pscustomobject]@{
            Demand    = [string](Get-G3Scalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'")
            Journey   = if ($journey) { "$($journey.Stage)/$($journey.BlockReasonCode)" } else { '' }
            Record    = if ($record) { [string]$record.State } else { '' }
            Readiness = if ($session) { [string]$session.Readiness } else { '' }
            Bindings  = Get-G3Count $connection "SELECT COUNT(*) AS Total FROM FaultedVehicleCargo WHERE DemandId = '$demandId'"
            Gates     = Get-GateCount $demandId
        }
    } `
    -Until { param($v) $v.Journey -eq 'Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF' -and $v.Readiness -eq 'Ready' -and $v.Record -eq 'ENDED' }
$assertions.Add(
    'L2-RC-07',
    '交接收敛：需求 Cancelled，旅程 Completed / TERMINATED_BY_FAULT_CARGO_HANDOFF，停住的重建记录 ENDED，全程没有故障货物绑定、只有一张 TO_GATE 单，会话回到 Ready',
    ($settled.Demand -eq 'Cancelled' -and $settled.Journey -eq 'Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF' -and
        $settled.Record -eq 'ENDED' -and $settled.Bindings -eq 0 -and $settled.Gates -eq 1 -and $settled.Readiness -eq 'Ready'),
    'Cancelled / Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF / ENDED / 绑定 0 / TO_GATE 1 / Ready',
    "$($settled.Demand) / $($settled.Journey) / $($settled.Record) / 绑定 $($settled.Bindings) / TO_GATE $($settled.Gates) / $($settled.Readiness)")
if ($settled.Readiness -ne 'Ready' -or $settled.Journey -ne 'Completed/TERMINATED_BY_FAULT_CARGO_HANDOFF') {
    Add-G3NotReached $assertions ($ids | Select-Object -Skip 7) '第 1 件的交接没有收敛，第 2 件无从开始'
    return
}

# ================================================================================================================
# 第 2 件：同样取消，货在
# ================================================================================================================

# 第 1 件那一仓的光幕不在这里复原：交接把货搬走了，这一仓在车载端读来就该是空的；下一次装货若落在这一仓，
# 在放货那一刻才复原（Invoke-LoadedTrip）。

# --- 6. 下一条需求装货，车在去卸货站的路上，单被取消 -------------------------------------------------------------------

$second = Invoke-LoadedTrip 'KEPT'
$keptDemand = $second.DemandId
$keptUpper = [string]$second.Gate.UpperId
$assertions.Add(
    'L2-RC-08',
    '交接之后同一台车接了下一单，照常装货、建出 TO_GATE 单',
    ((Get-GateCount $keptDemand) -eq 1),
    'TO_GATE 1', "TO_GATE $(Get-GateCount $keptDemand)")

$journal.Note("Vehicle sets off for the gate on $keptUpper; someone cancels the order in RIoT on the way (no emergency stop).")
$null = $riot.Command('Put', "orders/$keptUpper", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
Set-Vehicle @{ procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8; processingOrder = $true; orderTaskId = $second.Gate.OrderId }
Set-Vehicle @{ procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0; processingOrder = $false; clearOrderTaskId = $true }
$null = $riot.Command('Put', "orders/$keptUpper", @{ orderState = 2 })
$keptRecorded = Wait-L2ConditionOrLast -Description 'the server recorded the second cancellation to be rebuilt' `
    -Journal $journal -Criterion 'cancel-recorded-kept' -TimeoutSeconds 60 `
    -Probe { Get-Rebuild $keptUpper } `
    -Until { param($v) $null -ne $v -and [string]$v.Source -eq 'ORDER_CANCELLED_IN_RIOT' }
$assertions.Add(
    'L2-RC-09',
    '第二张 TO_GATE 单被取消：记下取消来源的同车重建（PENDING），这条需求的第一次出问题，不在第 1 件的窗口里',
    ($null -ne $keptRecorded -and [string]$keptRecorded.State -eq 'PENDING'),
    'ORDER_CANCELLED_IN_RIOT PENDING', $(if ($keptRecorded) { "$($keptRecorded.Source) $($keptRecorded.State)" } else { '(没有记录)' }))

# --- 7. 延迟到点、收到到期之后的快照，货在：重建开往卸货站 --------------------------------------------------------------

$rebuilt = Wait-L2ConditionOrLast -Description 'the rebuild was created on a snapshot showing the cargo in place' `
    -Journal $journal -Criterion 'rebuilt-on-proof' -TimeoutSeconds 120 `
    -Probe {
        $record = Get-Rebuild $keptUpper
        $newIntent = if ($record -and (Test-G3Present $record.NewUpperId)) {
            Get-G3Scalar $connection "SELECT Status AS Value FROM OrderIntents WHERE UpperId = '$($record.NewUpperId)'"
        } else { $null }
        [pscustomobject]@{
            State    = if ($record) { [string]$record.State } else { '' }
            DueAt    = if ($record) { ConvertTo-G3Instant $record.DueAt } else { $null }
            Evidence = Get-EvidenceReceivedAt $record
            Intent   = [string]$newIntent
            Gates    = Get-GateCount $keptDemand
        }
    } `
    -Until { param($v) $v.State -in @('REBUILT', 'STOPPED') }
$assertions.Add(
    'L2-RC-10',
    '货在原仓：延迟到点后服务端要到一份晚于到期的快照，它证明货在、锁闭、输出复位，才重建（REBUILT，新 TO_GATE 意图 CONFIRMED，共两张 TO_GATE 单）',
    ($rebuilt.State -eq 'REBUILT' -and $null -ne $rebuilt.Evidence -and $rebuilt.Evidence -gt $rebuilt.DueAt -and
        $rebuilt.Intent -eq 'CONFIRMED' -and $rebuilt.Gates -eq 2),
    'REBUILT / 证据晚于到期 / CONFIRMED / TO_GATE 2',
    "$($rebuilt.State) / 证据 $(if ($null -ne $rebuilt.Evidence) { $rebuilt.Evidence.ToString('o') } else { '(无)' }) 到期 $(if ($null -ne $rebuilt.DueAt) { $rebuilt.DueAt.ToString('o') } else { '(无)' }) / $($rebuilt.Intent) / TO_GATE $($rebuilt.Gates)")

$keptJourney = Get-Journey $keptDemand
$keptMembership = Get-G3Scalar $connection "SELECT Status AS Value FROM JourneyDemands WHERE DemandId = '$keptDemand'"
# 新单在途时真车载端的会话整段未就绪，旅程码可以是 ONBOARD_SESSION_NOT_READY：那是常态，不是重建没走完。这里只断它不再是
# 重建那一族的码（取消之后的过渡码、在等车况／快照／建单结果、停住）。
$keptCode = if ($keptJourney) { [string]$keptJourney.BlockReasonCode } else { '' }
$assertions.Add(
    'L2-RC-11',
    '重建之后这一趟照常去卸货站：旅程 AwaitingGateArrival，旅程码不再是重建那一族（真车载端在途时的 ONBOARD_SESSION_NOT_READY 是常态），需求仍是已装货',
    ($null -ne $keptJourney -and [string]$keptJourney.Stage -eq 'AwaitingGateArrival' -and
        $keptCode -notlike 'OWN_ORDER_REBUILD_*' -and $keptCode -ne 'ORDER_ENDED_WITHOUT_ARRIVAL' -and $keptMembership -eq 'LOADED'),
    'AwaitingGateArrival / 非重建族原因码 / LOADED',
    "$(if ($keptJourney) { "$($keptJourney.Stage)/$($keptJourney.BlockReasonCode)" } else { '(无旅程)' }) / $keptMembership")

$journal.Note('取消来源、车上有货：锁着期间货没了，解开之后读到仓空停住并交接到底；货在时收到到期之后的快照才重建。')
