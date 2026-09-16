#Requires -Version 7

<#
G3 `FP-IS-02` 的第一条场景：取货点录入 sublot、两仓装载、装载修正。协议向量 `CV-PICKUP-SUBLOT-LOAD` 与
`CV-LOAD-CORRECTION`；同一切片的 `CV-LOAD-CANCELLATION-ALL-EMPTY` 在 `g3-load-cancellation`。

由 `scripts/run-journey-g3.ps1` 在四个仓的精确克隆上驱动，也能像其它 L2 场景一样单独跑来调试。装置与
`FP-IS-01` 相同：真服务端 + 真车载端 WPF + 真 slots-simulator + 假 RIoT + 假 MesIngest
（`JOURNEY_SIMULATED_COUNTERPARTS`）。

**两仓，不是一仓。**切片范围是 `STATION_PICKUP_AND_MULTI_SLOT_LOAD`，禁止的副作用里有「扩大开锁集合」和
「重复开锁」，一仓装载证不出这两条。`L2-PACKAGE` 的容量是 4（`Invoke-L2Scenario.ps1` 导入的包装规则），
需求报 8 箱，服务端就要给两个仓（`SlotCapacityCriterion`）。

**修正接在正常装载之后。**车载端只在「上一笔装载已完成」时给「修正装货」按钮（`CanRequestLoadCorrection`
看 `LastCompletedLoadOperationContext`），服务端只对 `Committed` 的装载授权修正
（`AuthorizeLoadCorrectionAsync`）。所以先把装载做完、判完 `CV-PICKUP-SUBLOT-LOAD`，车还停在取货点，再由 UIA
代替操作员点「修正装货」。修正的物理步骤照车载端 `WaitForCorrectionAsync`：门开之后先把篮子取出（仓位无货），
再放回、关门（锁上、有货、开锁输出复位）。

**断言只读服务端的库与模拟器快照。**UI 只驱动；从界面上读的只有「按钮在不在」「有没有弹出拒绝框」，那是
操作员能不能动手的前提，不是业务事实。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$simulator = $Context.Simulator
$connection = $Context.Connection

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: the operator actions go through its HMI.'
}

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "G3-02-$($Context.RunId)"
$maxBoxCount = 8
$expectedBasketCount = 2

function Get-Count([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    return [int]$rows[0].Total
}

function Get-Scalar([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Value
}

function Get-Stage { return Get-Scalar "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'" }

function Test-Present([object]$value) { return ($null -ne $value -and [string]$value -ne '') }

function ConvertTo-Instant([object]$value) {
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

# 服务端发出的一类线上消息，按写入发件箱的先后。
function Get-Outbound([string]$messageType) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, PayloadJson, CreatedAt, AcknowledgedAt, FencedAt FROM ProtocolOutbox " +
        "WHERE MessageType = '$messageType' ORDER BY CreatedAt, MessageId")
    $messages = foreach ($row in $rows) {
        [pscustomobject]@{
            MessageId    = [string]$row.MessageId
            At           = ConvertTo-Instant $row.CreatedAt
            Payload      = ([string]$row.PayloadJson | ConvertFrom-Json).payload
            Acknowledged = Test-Present $row.AcknowledgedAt
            Fenced       = Test-Present $row.FencedAt
        }
    }
    return , @($messages)
}

# 车载端发来的一类线上消息，按服务端收下的先后。Response 是服务端第一份应答的消息类型。
function Get-Inbound([string]$messageType) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, RequestJson, FirstResponseJson, ReceivedAt FROM ProtocolInbox " +
        "WHERE MessageType = '$messageType' ORDER BY ReceivedAt, MessageId")
    $messages = foreach ($row in $rows) {
        # 应答可以是多行：OperationResult 与 SafetyStateChanged 的应答是 DurableAck 再跟一行 SessionReadiness。
        # 第一行才是对这条消息本身的应答。
        $response = if (Test-Present $row.FirstResponseJson) {
            ([string]$row.FirstResponseJson -split "`n")[0] | ConvertFrom-Json
        } else { $null }
        [pscustomobject]@{
            MessageId = [string]$row.MessageId
            At        = ConvertTo-Instant $row.ReceivedAt
            Payload   = ([string]$row.RequestJson | ConvertFrom-Json).payload
            Response  = if ($null -ne $response) { [string]$response.messageType } else { '' }
        }
    }
    return , @($messages)
}

# 这个 attempt 的 OperationProgress。装载和修正沿用同一个 slotOperationAttemptId，靠到达时间分开。
function Get-Progress([string]$attemptId) {
    $all = Get-Inbound 'OperationProgress'
    $mine = foreach ($message in $all) {
        if ([string]$message.Payload.slotOperationAttemptId -ne $attemptId) { continue }
        [pscustomobject]@{
            At     = $message.At
            Phase  = [string]$message.Payload.phase
            Active = @($message.Payload.activeUnlockSlots | ForEach-Object { [int]$_ })
        }
    }
    return , @($mine)
}

function Get-SlotState([int]$slotNo) {
    $slot = @($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $slotNo })[0]
    return "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
}

function Format-Slots([object[]]$slots) { return (@($slots | ForEach-Object { [int]$_ } | Sort-Object) -join ',') }

# 等车载端的第 $ordinal 条 WAITING_OPERATOR（从 0 数），只数 $since 之后到达的。它是车载端在锁反馈稳定、
# 开锁输出复位之后才发的，收到它才说明门确实开着，脚本不会比车载端的观测快。
function Wait-WaitingOperator([string]$attemptId, [int]$ordinal, [DateTimeOffset]$since, [string]$criterion) {
    $waiting = Wait-L2Condition -Description "the onboard is waiting for the operator ($criterion)" `
        -Journal $journal -Criterion $criterion -TimeoutSeconds 120 `
        -Probe {
            $rows = @((Get-Progress $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' -and $_.At -gt $since })
            if ($rows.Count -gt $ordinal) { $rows[$ordinal] } else { $null }
        } `
        -Until { param($v) $null -ne $v }
    if ($waiting.Active.Count -ne 1) {
        throw "The onboard reported WAITING_OPERATOR on $($waiting.Active.Count) slots at once; the executor walks one slot at a time."
    }
    return [int]$waiting.Active[0]
}

# 不用 Wait-L2Condition：超时它只在证据里留一句 failureReason，而「车载端有没有给操作员这个入口」值得一条
# 具名判据（同 real-onboard-recovery-entry-missing）。
function Wait-Offered([string]$buttonName, [string]$criterion, [int]$timeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($timeoutSeconds)
    while ($true) {
        $available = [bool]$onboard.ButtonAvailable($buttonName)
        $journal.Observe($criterion, $available, $null)
        if ($available -or [DateTimeOffset]::UtcNow -ge $deadline) { return $available }
        Start-Sleep -Milliseconds 500
    }
}

function Add-NotReached([string[]]$ids, [string]$why) {
    foreach ($id in $ids) {
        $assertions.Add($id, "未到达：$why", $false, '(reached)', "(not reached) $why")
    }
}

# --- 1. 需求出现在 MesIngest 目录里，服务端受理并派车去取货点 -------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot, $maxBoxCount boxes).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = $maxBoxCount
})

$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$intent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'"
        if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
    } `
    -Until { param($v) $null -ne $v }

# --- 2. 车开到取货点 -------------------------------------------------------------------------------

$journal.Note('Vehicle departs for the pickup station.')
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
$journal.Note('Vehicle arrives at the pickup station and comes to rest.')
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $Context.PickupStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })

# --- 3. 服务端请求录入 sublot，UIA 代替操作员扫码 ---------------------------------------------------

$null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
$journal.Note("Typing sublot $sublot into ScanTextBox through UI Automation.")
$onboard.SetSublot($sublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()
$journal.Note('Manual submit invoked.')

$null = Wait-L2Condition -Description 'the server received SublotSubmitted from the real onboard' `
    -Journal $journal -Criterion 'sublot-submitted' -TimeoutSeconds 60 `
    -Probe { @((Get-Inbound 'SublotSubmitted') | Where-Object { [string]$_.Payload.sublot -eq $sublot }).Count } `
    -Until { param($v) $v -ge 1 }

# --- 4. 两仓装载：车载端逐仓开锁，测试放篮子关门，车载端自己判完成 ------------------------------------

$attemptId = Wait-L2Condition -Description 'the server issued the load command' `
    -Journal $journal -Criterion 'load-attempt' -TimeoutSeconds 180 `
    -Probe { Get-Scalar "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'" } `
    -Until { param($v) $v }
$targetSlots = @([string](Get-Scalar "SELECT TargetSlotsJson AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'") |
    ConvertFrom-Json | ForEach-Object { [int]$_ })
$journal.Note("Load $attemptId targets slots $($targetSlots -join ', ').")

$loadStarted = [DateTimeOffset]::MinValue
$doorWhenWaiting = [ordered]@{}
for ($ordinal = 0; $ordinal -lt $targetSlots.Count; $ordinal++) {
    $slotNo = Wait-WaitingOperator $attemptId $ordinal $loadStarted "load-waiting-operator-$($ordinal + 1)"
    $doorWhenWaiting["$slotNo"] = Get-SlotState $slotNo
    $journal.Note("Operator puts basket $($ordinal + 1) into slot $slotNo ($($doorWhenWaiting["$slotNo"])) and closes the door.")
    $null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = 'OCCUPIED' })
    $null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})
}

$loadStatus = Wait-L2Condition -Description 'the server judged the load result' `
    -Journal $journal -Criterion 'load-status' -TimeoutSeconds 180 `
    -Probe { Get-Scalar "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'" } `
    -Until { param($v) $v -in @('Committed', 'RecoveryRequired') }

# 服务端把仓位命令记为已答复（SettleAnsweredCommandAsync），是在旅程运行时消费这份结果、离开
# AwaitingLoadResult 的那一轮里做的，比结果落库晚一个轮询。首跑就是在这之前读的发件箱。
if ($loadStatus -eq 'Committed') {
    $null = Wait-L2Condition -Description 'the journey runtime consumed the load result' `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 60 `
        -Probe { Get-Stage } -Until { param($v) $v -ne 'AwaitingLoadResult' }
}

# 协议 2.0.0 第 2 项：两条消息都不再带 demandId。请求给出派车范围的 expectedSublots，提交只报扫到的 sublot，
# 所以按 sublot 找；一趟一单时范围里只有这条需求的 sublot。
$entries = @((Get-Outbound 'SublotEntryRequested') | Where-Object { @($_.Payload.expectedSublots) -contains $sublot })
$submissions = @((Get-Inbound 'SublotSubmitted') | Where-Object { [string]$_.Payload.sublot -eq $sublot })
$operations = Invoke-L2Query -Connection $connection `
    -Sql "SELECT SlotOperationAttemptId, SublotId, Status FROM StationOperations WHERE DemandId = '$demandId'"
$sessionBound = $entries.Count -ge 1 -and $submissions.Count -ge 1 -and
    [string]$submissions[0].Payload.operationSessionId -eq [string]$entries[-1].Payload.operationSessionId
$assertions.Add(
    'G3-02-01',
    'sublot 绑定到操作会话：UIA 录入的 sublot 经真车载端只提交一次、回的是服务端请求录入的那个 operationSessionId，服务端为这条需求只建一笔装载，记的就是这个 sublot（SUBLOT_BOUND_TO_OPERATION_SESSION / BIND_SUBLOT_TO_OPERATION_SESSION / SUBMIT_SCANNED_SUBLOT）',
    ($submissions.Count -eq 1 -and $sessionBound -and [string]$submissions[0].Payload.sublot -eq $sublot -and
        $operations.Count -eq 1 -and [string]$operations[0].SublotId -eq $sublot),
    "提交 1 次 / 会话一致 / 操作 1 笔 / $sublot",
    "提交 $($submissions.Count) 次 / 会话一致=$sessionBound / 操作 $($operations.Count) 笔 / $(if ($operations.Count -ge 1) { [string]$operations[0].SublotId } else { '(none)' })")

$commands = @((Get-Outbound 'SlotOperationCommand') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
$commandSlots = if ($commands.Count -ge 1) { Format-Slots $commands[0].Payload.slots } else { '(none)' }
$assertions.Add(
    'G3-02-02',
    "仓位集合只授权一次：这条需求恰好一条 SlotOperationCommand，指向这笔装载，仓位就是服务端选定的 $expectedBasketCount 个（SLOT_SET_AUTHORIZED_ONCE / AUTHORIZE_SLOT_SET_ONCE）",
    ($commands.Count -eq 1 -and [string]$commands[0].Payload.slotOperationAttemptId -eq $attemptId -and
        $targetSlots.Count -eq $expectedBasketCount -and $commandSlots -eq (Format-Slots $targetSlots) -and
        [int]$commands[0].Payload.expectedBasketCount -eq $expectedBasketCount),
    "1 条 / $expectedBasketCount 仓 $(Format-Slots $targetSlots)",
    "$($commands.Count) 条 / $($targetSlots.Count) 仓 $commandSlots")

$loadUnlocks = @((Get-Progress $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' })
$unlockedSlots = @($loadUnlocks | ForEach-Object { $_.Active } | ForEach-Object { [int]$_ })
$otherSlots = @($simulator.Snapshot().slots | Where-Object { $targetSlots -notcontains [int]$_.slotNo })
$othersUntouched = @($otherSlots | Where-Object { "$($_.doorState)/$($_.cargoState)/$($_.unlockOutputRaw)" -ne 'CLOSED/EMPTY/0' }).Count -eq 0
$assertions.Add(
    'G3-02-03',
    '只开授权的仓、每仓只开一次：装载期间车载端报的开锁逐条只含一个仓，合起来恰好是授权集合、无重复；其余仓门始终关着、空着（LOAD_ONLY_AUTHORIZED_SLOTS；forbidden: duplicate-slot-unlock、expanded-active-unlock-set）',
    ($unlockedSlots.Count -eq $targetSlots.Count -and (Format-Slots $unlockedSlots) -eq (Format-Slots $targetSlots) -and
        @($loadUnlocks | Where-Object { $_.Active.Count -ne 1 }).Count -eq 0 -and $othersUntouched),
    "开锁 $(Format-Slots $targetSlots) 各一次 / 其余仓未动",
    "开锁 $($unlockedSlots -join ',') / 其余仓未动=$othersUntouched")

$loadPhysical = ($targetSlots | ForEach-Object { "$_=$(Get-SlotState $_)" }) -join ' '
$waitingDoors = ($targetSlots | ForEach-Object { "$_=$(if ($doorWhenWaiting.Contains("$_")) { $doorWhenWaiting["$_"] } else { '(never waited)' })" }) -join ' '
$assertions.Add(
    'G3-02-04',
    '装载走的是真 Modbus 闭环：车载端在等操作员时那个仓门确实开着；装完每个授权仓都关着、有货、锁反馈 1、开锁输出复位',
    (@($doorWhenWaiting.Values | Where-Object { -not $_.StartsWith('OPEN/') }).Count -eq 0 -and
        $doorWhenWaiting.Count -eq $targetSlots.Count -and
        $loadPhysical -eq (($targetSlots | ForEach-Object { "$_=CLOSED/OCCUPIED/1/0" }) -join ' ')),
    "等待时 OPEN / 装完 $(($targetSlots | ForEach-Object { "$_=CLOSED/OCCUPIED/1/0" }) -join ' ')",
    "等待时 $waitingDoors / 装完 $loadPhysical")

$results = @((Get-Inbound 'OperationResult') | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $attemptId })
$orderOk = $entries.Count -eq 1 -and $submissions.Count -eq 1 -and $commands.Count -eq 1 -and $results.Count -eq 1 -and
    $entries[0].At -lt $submissions[0].At -and $submissions[0].At -lt $commands[0].At -and $commands[0].At -lt $results[0].At -and
    $entries[0].Acknowledged -and $commands[0].Acknowledged -and $results[0].Response -eq 'DurableAck'
$assertions.Add(
    'G3-02-05',
    '消息顺序与向量一致：SublotEntryRequested → SublotSubmitted → SlotOperationCommand → OperationResult → DurableAck，各一次，服务端发的两条都被车载端确认（CV-PICKUP-SUBLOT-LOAD orderedExpectedMessages）',
    $orderOk,
    'SublotEntryRequested(ack) < SublotSubmitted < SlotOperationCommand(ack) < OperationResult → DurableAck，各 1',
    ("SublotEntryRequested×$($entries.Count)$(if ($entries.Count -ge 1) { "(ack=$($entries[0].Acknowledged))" }) / " +
     "SublotSubmitted×$($submissions.Count) / " +
     "SlotOperationCommand×$($commands.Count)$(if ($commands.Count -ge 1) { "(ack=$($commands[0].Acknowledged))" }) / " +
     "OperationResult×$($results.Count)$(if ($results.Count -ge 1) { "→$($results[0].Response)" }) / " +
     "有序=$(if ($entries.Count -ge 1 -and $submissions.Count -ge 1 -and $commands.Count -ge 1 -and $results.Count -ge 1) { $entries[0].At -lt $submissions[0].At -and $submissions[0].At -lt $commands[0].At -and $commands[0].At -lt $results[0].At } else { '(incomplete)' })"))

$resultRows = Invoke-L2Query -Connection $connection -Sql "SELECT OverallOutcome FROM OperationResults WHERE SlotOperationAttemptId = '$attemptId'"
$demandStatus = Get-Scalar "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
$stageAfterLoad = Get-Stage
$assertions.Add(
    'G3-02-06',
    '装载结果只提交一次：操作 Committed，结果一份且 COMPLETED，需求仍在执行，旅程没有停摆（RECONCILE_LOAD_OUTCOME；forbidden: duplicate-business-commit、unknown-as-success）',
    ($loadStatus -eq 'Committed' -and $resultRows.Count -eq 1 -and [string]$resultRows[0].OverallOutcome -eq 'COMPLETED' -and
        $demandStatus -eq 'Accepted' -and $stageAfterLoad -ne 'Blocked'),
    'Committed / 1 份 COMPLETED / Accepted / 未停摆',
    "$loadStatus / $($resultRows.Count) 份 $(if ($resultRows.Count -ge 1) { [string]$resultRows[0].OverallOutcome }) / $demandStatus / $stageAfterLoad")

# --- 5. 装载修正：UIA 代替操作员点「修正装货」，取出再放回 ---------------------------------------------

$correctionIds = @('G3-02-08', 'G3-02-09', 'G3-02-10', 'G3-02-11', 'G3-02-12', 'G3-02-13')
# 与 setup.psd1 的 StationDepartureWaitTimeout 相同。
$stationDepartureWait = [TimeSpan]::FromSeconds(20)
if ($loadStatus -ne 'Committed') {
    Add-NotReached (@('G3-02-07') + $correctionIds) "装载没有提交（$loadStatus），服务端不会授权修正"
    return
}

$correctionOffered = Wait-Offered '修正装货' 'onboard-correction-entry' 60
$assertions.Add(
    'G3-02-07',
    '装载完成后，车载端界面上出现可用的「修正装货」入口',
    $correctionOffered, $true, $correctionOffered)
if (-not $correctionOffered) {
    Add-NotReached $correctionIds '车载端没有给出「修正装货」入口'
    $journal.Note('Scenario finished at the missing load correction entry.')
    return
}

$stageAtCorrection = Get-Stage
$journal.Note("Operator presses 修正装货 and confirms (journey stage $stageAtCorrection).")
$onboard.InvokeButton('修正装货')
$null = $onboard.Confirm('修正装货')

$request = Wait-L2Condition -Description 'the server received LoadCorrectionRequested, or the onboard refused it' `
    -Journal $journal -Criterion 'correction-requested' -TimeoutSeconds 60 `
    -Probe {
        $received = @((Get-Inbound 'LoadCorrectionRequested') | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $attemptId })
        if ($received.Count -ge 1) { $received[0] }
        elseif (@($onboard.WindowTitles()) -contains '修正装货失败') { 'REFUSED_BY_ONBOARD' }
        else { $null }
    } `
    -Until { param($v) $null -ne $v }
if ($request -is [string]) {
    Add-NotReached $correctionIds '车载端拒绝发出修正请求（弹出「修正装货失败」）'
    return
}
$correctionId = [string]$request.Payload.correctionId

$command = Wait-L2Condition -Description 'the server authorized the correction, or rejected it' `
    -Journal $journal -Criterion 'correction-command' -TimeoutSeconds 60 `
    -Probe {
        $issued = @((Get-Outbound 'LoadCorrectionCommand') | Where-Object { [string]$_.Payload.correctionId -eq $correctionId })
        # 拒绝是对请求的直接应答，落在收件箱那一行的 FirstResponseJson，不进发件箱。首跑（corr-004）
        # 在发件箱里找它，服务端早已拒绝，场景却等满了 60 秒。
        $answered = @((Get-Inbound 'LoadCorrectionRequested') | Where-Object { [string]$_.Payload.correctionId -eq $correctionId })
        if ($issued.Count -ge 1) { $issued[0] }
        elseif ($answered.Count -ge 1 -and $answered[0].Response -eq 'LoadCorrectionRejected') { 'REJECTED_BY_SERVER' }
        else { $null }
    } `
    -Until { param($v) $null -ne $v }
if ($command -is [string]) {
    Add-NotReached $correctionIds '服务端拒绝了对已提交装载的修正'
    return
}

# 取出与放回之间留一个人手的间隔。车载端要先观测到「未锁、无货」稳定 FeedbackStableWindow（300ms）才去等
# 放回，而这一步它不发任何进度，外面等不到信号；三秒是一个人把篮子拿出来再放回去的时间，不是在掩盖竞态。
for ($ordinal = 0; $ordinal -lt $targetSlots.Count; $ordinal++) {
    $slotNo = Wait-WaitingOperator $attemptId $ordinal $request.At "correction-waiting-operator-$($ordinal + 1)"
    $journal.Note("Operator takes the basket out of slot $slotNo ($(Get-SlotState $slotNo)).")
    $null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = 'EMPTY' })
    Start-Sleep -Seconds 3
    $journal.Note("Operator puts the basket back into slot $slotNo and closes the door.")
    $null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = 'OCCUPIED' })
    $null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})
}

$correctionResult = Wait-L2Condition -Description 'the server received the LoadCorrectionResult' `
    -Journal $journal -Criterion 'correction-result' -TimeoutSeconds 180 `
    -Probe {
        $received = @((Get-Inbound 'LoadCorrectionResult') | Where-Object { [string]$_.Payload.correctionId -eq $correctionId })
        if ($received.Count -ge 1) { $received[0] } else { $null }
    } `
    -Until { param($v) $null -ne $v }

$workflowState = Wait-L2Condition -Description 'the correction workflow settled' `
    -Journal $journal -Criterion 'correction-workflow' -TimeoutSeconds 30 `
    -Probe { Get-Scalar "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$correctionId'" } `
    -Until { param($v) $v -in @('Reconciled', 'RecoveryRequired') }

$requests = @((Get-Inbound 'LoadCorrectionRequested') | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $attemptId })
$correctionCommands = @((Get-Outbound 'LoadCorrectionCommand') | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $attemptId })
$correctionResults = @((Get-Inbound 'LoadCorrectionResult') | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $attemptId })
# 恢复命令的「已答复」不记在发件箱行上，而记在工作流上：服务端重连时只补发工作流尚未收敛的恢复命令
# （OnboardRecoveryCoordinator.ReplayPendingCommandsAsync 按 State 筛），旅程运行时的重放白名单里没有恢复命令。
# 所以这里判的是「命令就是工作流绑定的那一条，工作流由这份结果收敛」，而不是发件箱行的 AcknowledgedAt——
# 调试运行 corr-005 里后者为空，工作流却已 Reconciled、命令不会再被补发。仓位命令不同，它由旅程运行时
# 结清，G3-02-05 照旧要求它被确认。
$workflowRows = (Invoke-L2Query -Connection $connection `
    -Sql "SELECT CommandMessageId, ResultMessageId, State FROM RecoveryWorkflows WHERE WorkflowId = '$correctionId'")
$settledByResult = $workflowRows.Count -eq 1 -and $correctionCommands.Count -eq 1 -and $correctionResults.Count -eq 1 -and
    [string]$workflowRows[0].CommandMessageId -eq $correctionCommands[0].MessageId -and
    [string]$workflowRows[0].ResultMessageId -eq $correctionResults[0].MessageId -and
    [string]$workflowRows[0].State -eq 'Reconciled'
$assertions.Add(
    'G3-02-08',
    '消息顺序与向量一致：LoadCorrectionRequested → LoadCorrectionCommand → LoadCorrectionResult → DurableAck，各一次；命令是修正工作流绑定的那一条，工作流由这份结果收敛（CV-LOAD-CORRECTION orderedExpectedMessages）',
    ($requests.Count -eq 1 -and $correctionCommands.Count -eq 1 -and $correctionResults.Count -eq 1 -and
        $requests[0].At -lt $correctionCommands[0].At -and $correctionCommands[0].At -lt $correctionResults[0].At -and
        $correctionResults[0].Response -eq 'DurableAck' -and $settledByResult),
    'Requested < Command < Result → DurableAck，各 1 / 工作流绑定该命令并由该结果收敛',
    ("Requested×$($requests.Count) / Command×$($correctionCommands.Count) / " +
     "Result×$($correctionResults.Count)$(if ($correctionResults.Count -ge 1) { "→$($correctionResults[0].Response)" }) / " +
     "工作流绑定并收敛=$settledByResult$(if ($workflowRows.Count -eq 1) { "（$([string]$workflowRows[0].State)）" })"))

$assertions.Add(
    'G3-02-09',
    '修正对着已提交的仓位集合授权：命令指向这条需求、这笔装载，仓位就是提交时的集合；修正工作流收敛为 Reconciled（AUTHORIZE_CORRECTION_AGAINST_COMMITTED_SET）',
    ([string]$command.Payload.demandId -eq $demandId -and [string]$command.Payload.slotOperationAttemptId -eq $attemptId -and
        (Format-Slots $command.Payload.slots) -eq (Format-Slots $targetSlots) -and $workflowState -eq 'Reconciled'),
    "$demandId / $attemptId / $(Format-Slots $targetSlots) / Reconciled",
    "$([string]$command.Payload.demandId) / $([string]$command.Payload.slotOperationAttemptId) / $(Format-Slots $command.Payload.slots) / $workflowState")

$correctionProgress = @((Get-Progress $attemptId) | Where-Object { $_.At -gt $request.At })
$correctionUnlocks = @($correctionProgress | Where-Object { $_.Phase -eq 'UNLOCKING' })
$correctionUnlocked = @($correctionUnlocks | ForEach-Object { $_.Active } | ForEach-Object { [int]$_ })
$beforeAuthorization = @($correctionProgress | Where-Object { $_.At -lt $command.At })
$assertions.Add(
    'G3-02-10',
    '没有授权不动仓门：车载端的修正进度全部晚于服务端发出修正命令；修正期间只开授权集合里的仓、每仓一次（NEVER_CORRECT_WITHOUT_AUTHORIZATION；forbidden: duplicate-slot-unlock、expanded-active-unlock-set）',
    ($correctionProgress.Count -ge 1 -and $beforeAuthorization.Count -eq 0 -and
        $correctionUnlocked.Count -eq $targetSlots.Count -and (Format-Slots $correctionUnlocked) -eq (Format-Slots $targetSlots)),
    "命令前 0 条进度 / 开锁 $(Format-Slots $targetSlots) 各一次",
    "命令前 $($beforeAuthorization.Count) 条进度（共 $($correctionProgress.Count) 条）/ 开锁 $($correctionUnlocked -join ',')")

$slotResults = @($correctionResult.Payload.slotResults)
$slotResultText = ($slotResults | Sort-Object { [int]$_.slotNo } | ForEach-Object {
    "$($_.slotNo)=$($_.outcome)/$($_.finalPhysicalState)/$($_.lockState)/$($_.unlockOutputState)"
}) -join ' '
$expectedSlotResults = ($targetSlots | Sort-Object | ForEach-Object { "$_=COMPLETED/OCCUPIED/LOCKED/RESET" }) -join ' '
$assertions.Add(
    'G3-02-11',
    '车载端如实上报修正后的仓位结果：COMPLETED，每个授权仓都是有货、锁上、开锁输出复位（REPORT_CORRECTED_SLOT_OUTCOME）',
    ([string]$correctionResult.Payload.overallOutcome -eq 'COMPLETED' -and $slotResultText -eq $expectedSlotResults),
    "COMPLETED $expectedSlotResults",
    "$([string]$correctionResult.Payload.overallOutcome) $slotResultText")

$finalOperations = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM StationOperations WHERE DemandId = '$demandId'"
$finalResults = Get-Count "SELECT COUNT(*) AS Total FROM OperationResults WHERE SlotOperationAttemptId = '$attemptId'"
$finalCommands = @((Get-Outbound 'SlotOperationCommand') | Where-Object { [string]$_.Payload.demandId -eq $demandId }).Count
$finalDemand = Get-Scalar "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
$finalStage = Get-Stage
$finalPhysical = ($targetSlots | ForEach-Object { "$_=$(Get-SlotState $_)" }) -join ' '
$assertions.Add(
    'G3-02-12',
    '修正不产生第二次业务提交：仍是一笔装载、一份结果、一条仓位命令，需求仍在执行、旅程没有停摆；仓位物理上仍是关着、有货、锁上、输出复位（forbidden: duplicate-business-commit；finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE）',
    ($finalOperations.Count -eq 1 -and [string]$finalOperations[0].Status -eq 'Committed' -and $finalResults -eq 1 -and
        $finalCommands -eq 1 -and $finalDemand -eq 'Accepted' -and $finalStage -ne 'Blocked' -and
        $finalPhysical -eq (($targetSlots | ForEach-Object { "$_=CLOSED/OCCUPIED/1/0" }) -join ' ')),
    "1 笔 Committed / 1 份结果 / 1 条命令 / Accepted / 未停摆 / $(($targetSlots | ForEach-Object { "$_=CLOSED/OCCUPIED/1/0" }) -join ' ')",
    "$($finalOperations.Count) 笔 $(if ($finalOperations.Count -ge 1) { [string]$finalOperations[0].Status }) / $finalResults 份结果 / $finalCommands 条命令 / $finalDemand / $finalStage / $finalPhysical")

# REQ-0237 / ADR-cross-0054：修正只在离站前开始，修正期间车留在取货点，修正收敛后从完整时长重新等满
# 才出发。不用 Wait-L2Condition：车一直不走也要落成这条具名判据，而不是一句 failureReason。
$reconciledAt = ConvertTo-Instant (Get-Scalar "SELECT UpdatedAt AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$correctionId'")
$departDeadline = [DateTimeOffset]::UtcNow.Add($stationDepartureWait).AddSeconds(60)
$departedStage = Get-Stage
while ($departedStage -notin @('AwaitingGateArrival', 'Blocked') -and [DateTimeOffset]::UtcNow -lt $departDeadline) {
    $journal.Observe('journey-stage-after-correction', $departedStage, $null)
    Start-Sleep -Milliseconds 500
    $departedStage = Get-Stage
}
$gateIntents = (Invoke-L2Query -Connection $connection `
    -Sql "SELECT CreatedAt FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'")
$gateCreatedAt = if ($gateIntents.Count -ge 1) { ConvertTo-Instant $gateIntents[0].CreatedAt } else { $null }
$heldBeforeResult = $null -eq $gateCreatedAt -or $gateCreatedAt -gt $correctionResult.At
$waitAfterCorrection = if ($null -ne $gateCreatedAt) { $gateCreatedAt - $reconciledAt } else { $null }
$assertions.Add(
    'G3-02-13',
    "修正只在离站前、并把车留在取货点：点「修正装货」时旅程在 AwaitingStationDeparture，修正结果到达前没有建出去关卡的单，修正收敛后等满离站期限（$([int]$stationDepartureWait.TotalSeconds)s）才建单出发（REQ-0237 / ADR-cross-0054）",
    ($stageAtCorrection -eq 'AwaitingStationDeparture' -and $heldBeforeResult -and $departedStage -eq 'AwaitingGateArrival' -and
        $gateIntents.Count -eq 1 -and $waitAfterCorrection -ge $stationDepartureWait),
    "AwaitingStationDeparture / 结果前 0 单 / AwaitingGateArrival / 收敛后 ≥ $([int]$stationDepartureWait.TotalSeconds)s 建单",
    "$stageAtCorrection / 结果前建单=$(-not $heldBeforeResult) / $departedStage / $(if ($null -ne $waitAfterCorrection) { "收敛后 $([math]::Round($waitAfterCorrection.TotalSeconds, 1))s 建单" } else { '没有建单' })")

$journal.Note('FP-IS-02: sublot bound, two slots loaded once, correction authorized against the committed set and reported.')
