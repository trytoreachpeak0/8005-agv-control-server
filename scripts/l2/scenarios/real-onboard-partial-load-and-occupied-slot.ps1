#Requires -Version 7

<#
「仓里已经有货」的两种来路，在真车载端与真 slots-simulator 上各走一遍，最后看下一单能不能照常装。

8005-agv-control-server#170 与 8005-agv-onboard-hmi#116 两处一起修，这一条把两端串起来验。起因是
2026-09-19 09:23 agv01 的需求 `182cc979` 装 `[3,4,5]`：3、4 号仓装上了，5 号仓到本站期限都没放，服务端
把它当成「没人交货」的确定失败作废了需求，两篮货被遗忘在锁着的仓里，下一趟旅程又被分到了 3 号仓。

三幕：

1. **命令到达时目标仓已经有货（车载端 #116）。**服务端分给这一单的仓，在扫码之前被放进了东西——
   这里用光幕覆写表达，对车来说与「别的单的货早就在里面」一模一样，车只读光幕的原始信号。
   车必须**不开门、不记完成**，照实报 FAILED，那一仓读 `OCCUPIED` 并挂 `SLOT_OPERATION_CONFLICT`；
   服务端据此判 `RecoveryRequired`、旅程停下。然后补偿清空：车**真的打开那一仓**，维护人员取出，
   对账 `ALL_EMPTY`，需求作废。
   旧车载端在这里走不下去：它把三个物理字段都填 `UNKNOWN`、也不写日志，服务端同样会停下旅程，
   但车上的恢复入口无从针对这一单开起来，那一仓的货没有产品路径能取出来。
2. **三仓装上两仓、第三仓到期限都没放（服务端 #170）。**车在期限过后照实报 FAILED，两仓 `OCCUPIED`、
   一仓 `EMPTY`。服务端不再作废需求，判 `RecoveryRequired`、旅程停下；补偿清空只开那两个有货的仓。
3. **下一单。**分到的仓可能正是刚才那几个——那没关系，要紧的是**它们此刻确实是空的**。扫码时逐仓对
   模拟器核一遍，然后照常装货提交。

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

$field = New-FieldOperator -Connection $connection -AgvId $Context.AgvId `
    -SimulatorPort $Context.SimulatorHttpPort -AutomationPort $Context.OnboardAutomationPort `
    -Log { param($message) $journal.Note("field-operator: $message") }.GetNewClosure()

function Get-SlotReading([int]$slotNo) {
    $slot = Get-FieldSimulatorSlot -Field $field -SlotNo $slotNo
    return "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
}

function Get-SessionState {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'"
    if ($rows.Count -eq 0) { return '(no session)' }
    return "$($rows[0].Readiness) / $($rows[0].ReasonCode)"
}

function Get-OperationResultPayload([string]$attemptId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT RequestJson FROM ProtocolInbox WHERE MessageType = 'OperationResult' " +
        "AND RequestJson LIKE '%$attemptId%' ORDER BY ReceivedAt")
    foreach ($row in $rows) {
        $payload = ([string]$row.RequestJson | ConvertFrom-Json -Depth 32).payload
        if ([string]$payload.slotOperationAttemptId -eq $attemptId) { return $payload }
    }
    return $null
}

function Get-JourneyState([string]$journeyId) {
    $journey = Get-FieldJourney -Field $field -JourneyId $journeyId
    return "$($journey.Stage) / $($journey.BlockReasonCode)"
}

<#
Publishes one demand, drives the vehicle to the pickup station the way RIoT would, and returns the
demand and its journey. Driving is the one thing the field driver does not do; the vehicle does.
#>
function Start-Demand([string]$label, [int]$maxBoxCount) {
    $guid = [guid]::NewGuid()
    $demandId = $guid.ToString('D')
    $sublot = "L2-POS-$($Context.RunId)-$label"
    $journal.Note("Publishing demand $label $($guid.ToString('N')) (sublot $sublot, $maxBoxCount boxes).")
    $null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot      = $sublot
        area        = 'N1-3'
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = $maxBoxCount
    })
    $intent = Wait-L2Condition -Description "the TO_PICKUP intent of demand $label was confirmed" `
        -Journal $journal -Criterion "to-pickup-intent-$label" -TimeoutSeconds 180 `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection `
                -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'"
            if ($rows.Count -eq 0) { $null } else { $rows[0] }
        } `
        -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
    $journeyId = [string](Get-L2Journey -Connection $connection -DemandId $demandId)[0].JourneyId

    $journal.Note("Vehicle drives to the pickup station for demand $label.")
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

    $targets = Invoke-L2Query -Connection $connection `
        -Sql "SELECT TargetSlotsJson FROM JourneyDemands WHERE DemandId = '$demandId'"
    return [pscustomobject]@{
        DemandId  = $demandId
        Sublot    = $sublot
        JourneyId = $journeyId
        Slots     = @([string]$targets[0].TargetSlotsJson | ConvertFrom-Json | ForEach-Object { [int]$_ })
    }
}

<#
The maintenance worker's hands during a compensation, for a slot whose "cargo" is a light-curtain
override: the moment the vehicle opens the door, the override comes off -- the basket is out -- and the
field driver closes the door on an empty slot as it would for real cargo. Runs beside
Invoke-FieldActCompensate, whose own loop only knows how to take real cargo out.
#>
function Start-OverrideRemoval([int]$slotNo) {
    $port = $Context.SimulatorHttpPort
    return Start-ThreadJob -ScriptBlock {
        param($port, $slotNo)
        $base = "http://127.0.0.1:$port/api/v1"
        $deadline = [DateTimeOffset]::UtcNow.AddMinutes(5)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            $snapshot = Invoke-RestMethod -Uri "$base/snapshot" -TimeoutSec 20
            $slot = @($snapshot.slots | Where-Object { [int]$_.slotNo -eq $slotNo })[0]
            if ([string]$slot.doorState -eq 'OPEN') {
                for ($attempt = 1; $attempt -le 4; $attempt++) {
                    $snapshot = Invoke-RestMethod -Uri "$base/snapshot" -TimeoutSec 20
                    $body = @{
                        mode             = 'AUTO'
                        runId            = $snapshot.runId
                        expectedRevision = $snapshot.revision
                        commandId        = [guid]::NewGuid().ToString('D')
                    } | ConvertTo-Json -Compress
                    $response = Invoke-WebRequest -Uri "$base/slots/$slotNo/light-curtain-override" -Method Put `
                        -Body $body -ContentType 'application/json' -SkipHttpErrorCheck -TimeoutSec 30
                    if ([int]$response.StatusCode -eq 200) { return "override removed at $([DateTimeOffset]::UtcNow.ToString('o'))" }
                }
                throw "Could not remove the light-curtain override of slot $slotNo."
            }
            Start-Sleep -Milliseconds 100
        }
        throw "Slot $slotNo never opened within five minutes."
    } -ArgumentList $port, $slotNo
}

# --- 第一幕：命令到达时目标仓已经有货 ---------------------------------------------------------------------

$first = Start-Demand -label 'A' -maxBoxCount 4
$slotA = [int]$first.Slots[0]
$request = Wait-FieldSublotRequest -Field $field -JourneyId $first.JourneyId -Sequence 1 -ArrivalTimeoutSeconds 120

# 车在等扫码，服务端已把 $slotA 分给这一单。现在让这一仓读成有货：取它空仓时的光幕原始值，钉在反面。
# 极性是逐仓配置，所以不写死 0 或 1。
$emptyRaw = [int](Get-FieldSimulatorSlot -Field $field -SlotNo $slotA).lightCurtainRaw
$journal.Note("Slot $slotA reads light curtain $emptyRaw when empty; pinning it to $(1 - $emptyRaw) -- someone else's basket.")
$null = Invoke-FieldSimulatorCommand -Field $field -Method PUT -Path "/slots/$slotA/light-curtain-override" `
    -Body @{ mode = "FIXED_$(1 - $emptyRaw)" }
$null = Wait-L2Condition -Description "slot $slotA to read the pinned light curtain" `
    -Journal $journal -Criterion 'occupied-override' -TimeoutSeconds 30 `
    -Probe { [int](Get-FieldSimulatorSlot -Field $field -SlotNo $slotA).lightCurtainRaw } -Until { param($v) $v -eq 1 - $emptyRaw }
# 车载端按自己的节拍轮询 Modbus；给它几轮，别让扫码抢在它读到之前。
Start-Sleep -Seconds 3

$submit = Invoke-FieldFace -Field $field -Face Onboard -Method POST -Path '/sublots/submit' -Body @{ sublot = $request.Sublot } -Envelope
if ([int]$submit.status -ne 200 -or -not (Get-FieldProperty $submit.body 'accepted')) {
    throw "The vehicle refused sublot $($request.Sublot): HTTP $($submit.status)"
}
$journal.Note("Submitted sublot $($request.Sublot).")

$operationA = Wait-L2Condition -Description 'the load of demand A settled' `
    -Journal $journal -Criterion 'load-a-settled' -TimeoutSeconds 180 `
    -Probe { Get-FieldOperation -Field $field -DemandId $first.DemandId } `
    -Until { param($o) $o -and [string]$o.Status -in @('Committed', 'Failed', 'RecoveryRequired', 'Cancelled') }
$attemptA = [string]$operationA.SlotOperationAttemptId
$payloadA = Get-OperationResultPayload $attemptA
$slotResultA = @($payloadA.slotResults | Where-Object { [int]$_.slotNo -eq $slotA })[0]
$assertions.Add(
    'L2-POS-01', '车没有开门：目标仓已经有货，车照实报 FAILED，那一仓 NOT_STARTED / OCCUPIED / LOCKED / RESET 并挂 SLOT_OPERATION_CONFLICT',
    ([string]$payloadA.overallOutcome -eq 'FAILED' -and [string]$slotResultA.outcome -eq 'NOT_STARTED' -and
        [string]$slotResultA.finalPhysicalState -eq 'OCCUPIED' -and [string]$slotResultA.lockState -eq 'LOCKED' -and
        [string]$slotResultA.unlockOutputState -eq 'RESET' -and @($slotResultA.reasonCodes) -contains 'SLOT_OPERATION_CONFLICT'),
    'FAILED / NOT_STARTED / OCCUPIED / LOCKED / RESET / [SLOT_OPERATION_CONFLICT]',
    "$($payloadA.overallOutcome) / $($slotResultA.outcome) / $($slotResultA.finalPhysicalState) / " +
        "$($slotResultA.lockState) / $($slotResultA.unlockOutputState) / [$(@($slotResultA.reasonCodes) -join ',')]")

$unlocksA = (Get-FieldPhaseCounts -Field $field -AttemptId $attemptA).Unlocking
$assertions.Add(
    'L2-POS-02', '整个装货过程一次开锁都没有，门一直关着',
    ($unlocksA -eq 0 -and (Get-SlotReading $slotA) -like 'CLOSED/*'), '0 次 UNLOCKING / CLOSED', "$unlocksA 次 UNLOCKING / $(Get-SlotReading $slotA)")

$stateA = Wait-L2Condition -Description 'journey A blocked on the refused load' `
    -Journal $journal -Criterion 'journey-a-blocked' -TimeoutSeconds 60 `
    -Probe { Get-JourneyState $first.JourneyId } -Until { param($v) $v -like 'Blocked*' -or $v -like 'Completed*' }
$assertions.Add(
    'L2-POS-03', '服务端判 RecoveryRequired、旅程停下：仓里那箱货要先取出来，不能悄悄作废需求',
    ([string]$operationA.Status -eq 'RecoveryRequired' -and $stateA -eq 'Blocked / LOAD_RESULT_REQUIRES_RECOVERY'),
    'RecoveryRequired / Blocked / LOAD_RESULT_REQUIRES_RECOVERY', "$($operationA.Status) / $stateA")

$removal = Start-OverrideRemoval -slotNo $slotA
try {
    $compensationA = Invoke-FieldActCompensate -Field $field -DemandId $first.DemandId -AttemptId $attemptA `
        -Reason 'L2：目标仓在装货前已有货，补偿清空取出'
} finally {
    $removalNote = try { Receive-Job $removal -Wait -AutoRemoveJob -ErrorAction Stop } catch { "override removal failed: $($_.Exception.Message)" }
    $journal.Note("maintenance: $removalNote")
}
$assertions.Add(
    'L2-POS-04', '被拒的这一单能走补偿清空：车打开了那一仓、维护人员取出，对账 Reconciled / ALL_EMPTY，需求 Cancelled',
    ($compensationA.WorkflowState -eq 'Reconciled' -and $compensationA.Outcome -eq 'ALL_EMPTY' -and
        $compensationA.DemandStatus -eq 'Cancelled' -and "$(@($compensationA.ServedSlots) -join ',')" -eq "$slotA"),
    "Reconciled / ALL_EMPTY / Cancelled / 开过 [$slotA]",
    "$($compensationA.WorkflowState) / $($compensationA.Outcome) / $($compensationA.DemandStatus) / 开过 [$(@($compensationA.ServedSlots) -join ',')]")

$null = Wait-L2Condition -Description 'journey A completed and the session is Ready again' `
    -Journal $journal -Criterion 'act-a-closed' -TimeoutSeconds 120 `
    -Probe { "$(Get-JourneyState $first.JourneyId) | $(Get-SessionState)" } `
    -Until { param($v) $v -like 'Completed*| Ready / READY' }
$assertions.Add(
    'L2-POS-05', "现场收在安全状态：$slotA 号仓门关、仓空、已锁、开锁输出复位",
    ((Get-SlotReading $slotA) -eq 'CLOSED/EMPTY/1/0'), 'CLOSED/EMPTY/1/0', (Get-SlotReading $slotA))

# --- 第二幕：三仓装上两仓，第三仓到期限都没放 ----------------------------------------------------------

$second = Start-Demand -label 'B' -maxBoxCount 12
$loadB = Start-FieldStopLoad -Field $field -JourneyId $second.JourneyId -Sequence 1 -ArrivalTimeoutSeconds 120
$assertions.Add(
    'L2-POS-06', '第二单要装三篮，服务端给了三个仓',
    ($second.Slots.Count -eq 3), 3, $second.Slots.Count)

$served = [System.Collections.Generic.List[int]]::new()
$emptySlot = 0
while ($true) {
    $counts = Wait-L2Condition -Description "the vehicle to wait on a slot not yet served (served: $($served -join ','))" `
        -Journal $journal -Criterion "load-b-waiting-$($served.Count)" -TimeoutSeconds 120 `
        -Probe { Get-FieldPhaseCounts -Field $field -AttemptId $loadB.AttemptId } `
        -Until { param($c) $c.WaitingSlot -gt 0 -and -not $served.Contains([int]$c.WaitingSlot) }
    $slotNo = [int]$counts.WaitingSlot
    if ($served.Count -eq 2) {
        $emptySlot = $slotNo
        break
    }
    $journal.Note("Operator puts a basket into slot $slotNo and closes it.")
    $null = Invoke-FieldCloseSlot -Field $field -SlotNo $slotNo -Cargo OCCUPIED
    $served.Add($slotNo)
}

# 第三仓：操作员一次次空关，车一次次重开（决策 1），直到本站期限过去、宽限的那一轮也用掉。
$loadOnEmpty = [pscustomobject]@{ DemandId = $second.DemandId; AttemptId = $loadB.AttemptId; SlotNo = $emptySlot }
$closes = 0
$statusB = $null
while ($null -eq $statusB) {
    if ($closes -ge 60) { throw "Slot ${emptySlot}: sixty empty closes and the load has not settled." }
    $before = Get-FieldPhaseCounts -Field $field -AttemptId $loadB.AttemptId
    $null = Invoke-FieldCloseSlot -Field $field -SlotNo $emptySlot -NoSettle
    $closes++
    $answer = Wait-FieldCloseAnswer -Field $field -Load $loadOnEmpty -Before $before -TimeoutSeconds 300
    if ($answer.Status -in @('Committed', 'Failed', 'RecoveryRequired', 'Cancelled')) { $statusB = $answer.Status }
}
$journal.Note("Load B settled as $statusB after $closes empty close(s) of slot $emptySlot.")

$payloadB = Get-OperationResultPayload $loadB.AttemptId
$shapeB = @($payloadB.slotResults | Sort-Object { [int]$_.slotNo } | ForEach-Object { "$($_.slotNo):$($_.outcome)/$($_.finalPhysicalState)" }) -join ' '
$expectedShapeB = @($second.Slots | Sort-Object | ForEach-Object {
    ($served.Contains($_)) ? "${_}:COMPLETED/OCCUPIED" : "${_}:FAILED/EMPTY"
}) -join ' '
$assertions.Add(
    'L2-POS-07', '车照实报了部分装上：两仓 COMPLETED / OCCUPIED，空关的那一仓 FAILED / EMPTY，整体 FAILED',
    ([string]$payloadB.overallOutcome -eq 'FAILED' -and $shapeB -eq $expectedShapeB),
    "FAILED | $expectedShapeB", "$($payloadB.overallOutcome) | $shapeB")

$stateB = Wait-L2Condition -Description 'journey B blocked on the partial load' `
    -Journal $journal -Criterion 'journey-b-blocked' -TimeoutSeconds 60 `
    -Probe { Get-JourneyState $second.JourneyId } -Until { param($v) $v -like 'Blocked*' -or $v -like 'Completed*' }
$demandB = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$($second.DemandId)'"
$assertions.Add(
    'L2-POS-08', '部分装上不再是确定失败：装货 RecoveryRequired、旅程停下、需求留着不作废（#170 的那一处）',
    ($statusB -eq 'RecoveryRequired' -and $stateB -eq 'Blocked / LOAD_RESULT_REQUIRES_RECOVERY' -and
        [string]$demandB[0].Status -eq 'RecoveryRequired'),
    'RecoveryRequired / Blocked / LOAD_RESULT_REQUIRES_RECOVERY / RecoveryRequired',
    "$statusB / $stateB / $([string]$demandB[0].Status)")

$loadedReadings = @($served | Sort-Object | ForEach-Object { "${_}=$(Get-SlotReading $_)" }) -join ' '
$assertions.Add(
    'L2-POS-09', '那两篮确实在车上：两仓门关、有货、已锁',
    (@($served | Where-Object { (Get-SlotReading $_) -ne 'CLOSED/OCCUPIED/1/0' }).Count -eq 0),
    'CLOSED/OCCUPIED/1/0 ×2', $loadedReadings)

$compensationB = Invoke-FieldActCompensate -Field $field -DemandId $second.DemandId -AttemptId $loadB.AttemptId `
    -Reason 'L2：部分装上，补偿清空取出已装的两篮'
$servedByCompensation = "$(@($compensationB.ServedSlots) -join ',')"
$assertions.Add(
    'L2-POS-10', '补偿清空只开了有货的那两仓，取出后对账 Reconciled / ALL_EMPTY，需求 Cancelled',
    ($compensationB.WorkflowState -eq 'Reconciled' -and $compensationB.Outcome -eq 'ALL_EMPTY' -and
        $compensationB.DemandStatus -eq 'Cancelled' -and $servedByCompensation -eq "$(@($served | Sort-Object) -join ',')"),
    "Reconciled / ALL_EMPTY / Cancelled / 开过 [$(@($served | Sort-Object) -join ',')]",
    "$($compensationB.WorkflowState) / $($compensationB.Outcome) / $($compensationB.DemandStatus) / 开过 [$servedByCompensation]")

$null = Wait-L2Condition -Description 'journey B completed and the session is Ready again' `
    -Journal $journal -Criterion 'act-b-closed' -TimeoutSeconds 120 `
    -Probe { "$(Get-JourneyState $second.JourneyId) | $(Get-SessionState)" } `
    -Until { param($v) $v -like 'Completed*| Ready / READY' }

# --- 第三幕：下一单 ----------------------------------------------------------------------------------

$third = Start-Demand -label 'C' -maxBoxCount 8
$loadC = Start-FieldStopLoad -Field $field -JourneyId $third.JourneyId -Sequence 1 -ArrivalTimeoutSeconds 120
$targetReadings = @($third.Slots | ForEach-Object {
    $slot = Get-FieldSimulatorSlot -Field $field -SlotNo $_
    "${_}=$($slot.cargoState)"
}) -join ' '
$assertions.Add(
    'L2-POS-11', '下一单分到的每一仓此刻确实是空的——即便它们正是刚才出过事的那几个',
    (@($third.Slots | Where-Object { [string](Get-FieldSimulatorSlot -Field $field -SlotNo $_).cargoState -ne 'EMPTY' }).Count -eq 0),
    '全部 EMPTY', $targetReadings)

$servedC = Invoke-FieldServeOperation -Field $field -DemandId $third.DemandId -AttemptId $loadC.AttemptId -Cargo OCCUPIED
$assertions.Add(
    'L2-POS-12', '下一单照常装货并提交',
    ($servedC.Status -eq 'Committed'), 'Committed', $servedC.Status)

$journal.Note('Scenario finished.')
