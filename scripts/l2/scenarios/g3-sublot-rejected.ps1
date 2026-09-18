#Requires -Version 7

<#
G3 `FP-IS-02`：录入后拒收（批次 5，control-server#87）。协议向量 `CV-SUBLOT-REJECTED-AFTER-ENTRY`，`protocol-v2.0.0`
新增；同一切片的扫码前取消在 `g3-load-cancellation-before-load`。

两端的半边：服务端 control-server#82（录入之后按 BR-013 重算，与派车时冻结的花篮数不符就不开仓，用 `SublotRejected`
把真实原因发回车前），车载端 onboard-hmi#77（`SublotRejected` 不再当恢复消息，在提示区显示被拒的子批与原因，
`AutomationId=SublotRejectionReason`、`ItemStatus` 是原始原因码；修订号与操作会话都一致时保留录入请求）。合成对端的
同一条路径在 `sublot-rejected-after-entry`，那里改的是 MES 箱数；这里按票面改**容量对照**。

**怎么让重算不符。**`L2-PACKAGE` 的容量对照由装置导入为 4 箱/篮，需求报 4 箱 → 派车冻结 1 个花篮。车到站后导入
容量对照第 2 版，`L2-PACKAGE` 改成 2 箱/篮：重算得 2，与冻结的 1 不符 → `EXPECTED_BASKET_COUNT_MISMATCH`。导入只能
改值、不能删规则，所以向量的 `stableErrorCode`（`PACKAGE_CAPACITY_UNRESOLVED`，规则整条没有）在「派车之后改对照」
这条路上走不到——派车本身就要求对照存在。两个码走的是同一个拒收出口（`RefuseAsync`），原因码的区别由服务端 L1 覆盖。
导入第 3 版改回 4 箱/篮之后，操作员重扫，正常装货。

**「显示了原因」按 UIA 判，不比文案全文。**找 `SublotRejectionReason` 这个元素：在树里、`Name` 非空且含被拒的子批号、
`ItemStatus` 等于服务端发的原因码。文案由车载端的映射表决定，改文案不该让 G3 变红。

**断言读服务端的库、模拟器快照和车载端界面上那一个元素。**其余 UI 只驱动。
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
$sublot = "G3-02R-$($Context.RunId)"
$maxBoxCount = 4
$frozenBasketCount = 1
$package = 'L2-PACKAGE'

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

# 服务端发出的一类线上消息，按写入发件箱的先后，带整个信封（拒收的 correlationId 在信封上）。
function Get-Outbound([string]$messageType) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, PayloadJson, CreatedAt, AcknowledgedAt FROM ProtocolOutbox " +
        "WHERE MessageType = '$messageType' ORDER BY CreatedAt, MessageId")
    $messages = foreach ($row in $rows) {
        $envelope = [string]$row.PayloadJson | ConvertFrom-Json
        [pscustomobject]@{
            MessageId     = [string]$row.MessageId
            At            = ConvertTo-Instant $row.CreatedAt
            CorrelationId = [string]$envelope.correlationId
            Payload       = $envelope.payload
            Acknowledged  = Test-Present $row.AcknowledgedAt
        }
    }
    return , @($messages)
}

# 车载端发来的一类线上消息，按服务端收下的先后。
function Get-Inbound([string]$messageType) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, RequestJson, FirstResponseJson, ReceivedAt FROM ProtocolInbox " +
        "WHERE MessageType = '$messageType' ORDER BY ReceivedAt, MessageId")
    $messages = foreach ($row in $rows) {
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

function Get-Progress([string]$attemptId) {
    $mine = foreach ($message in (Get-Inbound 'OperationProgress')) {
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

function Get-AllSlotStates {
    return (@($simulator.Snapshot().slots | Sort-Object { [int]$_.slotNo } | ForEach-Object {
                "$($_.slotNo)=$($_.doorState)/$($_.cargoState)/$($_.lockFeedbackRaw)/$($_.unlockOutputRaw)"
            }) -join ' ')
}

# 车载端提示区里那一行拒收原因。Collapsed 的元素不进 UIA 树，所以「不在」就是「没显示」。
function Get-RejectionDisplay {
    $element = $onboard.Element('AutomationId', 'SublotRejectionReason')
    if (-not $element) { return $null }
    try {
        if ($element.Current.IsOffscreen) { return $null }
        return [pscustomobject]@{ Name = [string]$element.Current.Name; ItemStatus = [string]$element.Current.ItemStatus }
    } catch [System.Windows.Automation.ElementNotAvailableException] {
        return $null
    }
}

# 等不到就返回 $null 而不是抛：这两处等不到本身是一条具名判据的失败，要落进 assertions 而不是 failureReason。
function Wait-OrNull {
    param([string]$Description, [string]$Criterion, [int]$TimeoutSeconds, [scriptblock]$Probe, [scriptblock]$Until)
    try {
        return Wait-L2Condition -Description $Description -Journal $journal -Criterion $Criterion `
            -TimeoutSeconds $TimeoutSeconds -Probe $Probe -Until $Until
    } catch {
        if ($_.Exception.Message -notlike 'Timed out after*') { throw }
        $journal.Note($_.Exception.Message)
        return $null
    }
}

function Add-NotReached([string[]]$ids, [string]$why) {
    foreach ($id in $ids) {
        $assertions.Add($id, "未到达：$why", $false, '(reached)', "(not reached) $why")
    }
}

function Submit-Sublot([string]$criterion) {
    $null = Wait-L2Condition -Description "the onboard HMI accepts sublot entry ($criterion)" `
        -Journal $journal -Criterion "$criterion-can-submit" -TimeoutSeconds 60 `
        -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
    $journal.Note("Typing sublot $sublot into ScanTextBox through UI Automation ($criterion).")
    $onboard.SetSublot($sublot)
    $null = Wait-L2Condition -Description "the manual submit button became enabled ($criterion)" `
        -Journal $journal -Criterion "$criterion-submit-ready" -TimeoutSeconds 30 `
        -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
    $onboard.Submit()
    $journal.Note("Manual submit invoked ($criterion).")
}

# --- 1. 需求受理（4 箱 ÷ 4 箱/篮 = 1 个花篮），车到取货点，停在等录入 --------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot, $maxBoxCount boxes, $package at 4 per basket).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = $package
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
$dispatched = @(Invoke-L2Query -Connection $connection `
        -Sql "SELECT ExpectedBasketCount, TargetSlotsJson FROM JourneyRuntimes WHERE DemandId = '$demandId'")[0]
if ([int]$dispatched.ExpectedBasketCount -ne $frozenBasketCount) {
    throw "The dispatch froze $($dispatched.ExpectedBasketCount) baskets, not $frozenBasketCount; the capacity change below would not be a mismatch."
}

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

$waiting = Wait-L2Condition -Description 'the stop is waiting for a sublot' `
    -Journal $journal -Criterion 'awaiting-sublot' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT Stage, SublotRequestMessageId, WorklistRevision FROM JourneyRuntimes WHERE DemandId = '$demandId'"
        if ($rows.Count -eq 1 -and [string]$rows[0].Stage -eq 'AwaitingSublot') { $rows[0] } else { $null }
    } `
    -Until { param($v) $null -ne $v }
$slotsBefore = Get-AllSlotStates

# --- 2. 派车之后容量对照变了：录入 → 重算不符 → SublotRejected ------------------------------------

& $Context.ImportPackageCapacity -Rows @(@{ Pattern = $package; Capacity = 2 }) -Version 2
Submit-Sublot 'first-entry'

$laterIds = @('G3-02-41', 'G3-02-42', 'G3-02-43', 'G3-02-44', 'G3-02-45', 'G3-02-46', 'G3-02-47')
$rejection = Wait-L2Condition -Description 'the server sent SublotRejected, or it issued a load command instead' `
    -Journal $journal -Criterion 'entry-refused' -TimeoutSeconds 60 `
    -Probe {
        $sent = @(Get-Outbound 'SublotRejected')
        if ($sent.Count -ge 1) { $sent[0] }
        elseif ((Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId'") -gt 0) { 'LOADED_INSTEAD' }
        else { $null }
    } `
    -Until { param($v) $null -ne $v }
if ($rejection -is [string]) {
    Add-NotReached $laterIds '服务端没有拒收，而是直接发了装货命令'
    return
}

# 拒收被车载端确认、提示出现在界面上。确认与显示是车载端同一次处理里的两件事，一起等。
$display = Wait-OrNull -Description 'the onboard acknowledged the rejection and shows its reason' `
    -Criterion 'rejection-displayed' -TimeoutSeconds 30 `
    -Probe {
        $acknowledged = @(Get-Outbound 'SublotRejected' | Where-Object { $_.MessageId -eq $rejection.MessageId -and $_.Acknowledged }).Count -eq 1
        $shown = Get-RejectionDisplay
        if ($acknowledged -and $null -ne $shown -and (Test-Present $shown.ItemStatus)) { $shown } else { $null }
    } `
    -Until { param($v) $null -ne $v }
# 一轮之后再看旅程：拒收不能把它推离等录入。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal

$entries = @(Get-Outbound 'SublotEntryRequested' | Where-Object { @($_.Payload.expectedSublots) -contains $sublot })
$submissions = @(Get-Inbound 'SublotSubmitted' | Where-Object { [string]$_.Payload.sublot -eq $sublot })
$rejectionAcknowledged = @(Get-Outbound 'SublotRejected' | Where-Object { $_.MessageId -eq $rejection.MessageId -and $_.Acknowledged }).Count -eq 1
$sequenceOk = $entries.Count -ge 1 -and $submissions.Count -eq 1 -and
    $entries[0].At -lt $submissions[0].At -and $submissions[0].At -le $rejection.At -and
    $rejection.CorrelationId -eq $submissions[0].MessageId -and $entries[0].Acknowledged -and $rejectionAcknowledged
$assertions.Add(
    'G3-02-41',
    '消息顺序与向量一致：SublotEntryRequested → SublotSubmitted → SublotRejected；拒收的 correlationId 就是 UIA 录入的那条提交的 messageId，服务端发的两条都被车载端确认（CV-SUBLOT-REJECTED-AFTER-ENTRY orderedExpectedMessages）',
    $sequenceOk,
    'EntryRequested(ack) < Submitted×1 <= Rejected(ack)，correlationId = 提交 messageId',
    ("EntryRequested×$($entries.Count)$(if ($entries.Count -ge 1) { "(ack=$($entries[0].Acknowledged))" }) / " +
     "Submitted×$($submissions.Count) / Rejected ack=$rejectionAcknowledged / " +
     "correlationId=$($rejection.CorrelationId) 提交=$(if ($submissions.Count -ge 1) { $submissions[0].MessageId } else { '(none)' })"))

$reasonCode = [string]$rejection.Payload.problem.reasonCode
$assertions.Add(
    'G3-02-42',
    '录入之后重新校验：容量对照改成 2 箱/篮后重算得 2 个花篮，与派车冻结的 1 不符，拒收原因 EXPECTED_BASKET_COUNT_MISMATCH，指名这条需求、被拒的子批、本站的操作会话与修订号（REVALIDATE_SUBLOT_AFTER_ENTRY）',
    ($reasonCode -eq 'EXPECTED_BASKET_COUNT_MISMATCH' -and [string]$rejection.Payload.demandId -eq $demandId -and
        [string]$rejection.Payload.rejectedSublot -eq $sublot -and $submissions.Count -ge 1 -and
        [string]$rejection.Payload.operationSessionId -eq [string]$submissions[0].Payload.operationSessionId -and
        [long]$rejection.Payload.currentWorklistRevision -eq [long]$waiting.WorklistRevision),
    "EXPECTED_BASKET_COUNT_MISMATCH / $demandId / $sublot / 会话一致 / revision $($waiting.WorklistRevision)",
    ("$reasonCode / $([string]$rejection.Payload.demandId) / $([string]$rejection.Payload.rejectedSublot) / " +
     "会话一致=$($submissions.Count -ge 1 -and [string]$rejection.Payload.operationSessionId -eq [string]$submissions[0].Payload.operationSessionId) / " +
     "revision $($rejection.Payload.currentWorklistRevision)"))

$refusedRuntime = @(Invoke-L2Query -Connection $connection `
        -Sql "SELECT Stage, TargetSlotsJson, ExpectedBasketCount, BlockReasonCode FROM JourneyRuntimes WHERE DemandId = '$demandId'")[0]
$operationsAfterRefusal = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId'"
$commandsAfterRefusal = Get-Count "SELECT COUNT(*) AS Total FROM ProtocolOutbox WHERE MessageType = 'SlotOperationCommand'"
$slotsAfterRefusal = Get-AllSlotStates
$assertions.Add(
    'G3-02-43',
    '拒收的录入不开仓：0 笔仓位操作、0 条 SlotOperationCommand，模拟器上每个仓与到站时逐仓相同；旅程仍在等录入，预留的仓位与冻结的花篮数一点没动，没有阻断原因（NEVER_UNLOCK_ON_REJECTED_ENTRY；forbidden: duplicate-slot-unlock、expanded-active-unlock-set）',
    ($operationsAfterRefusal -eq 0 -and $commandsAfterRefusal -eq 0 -and $slotsAfterRefusal -eq $slotsBefore -and
        [string]$refusedRuntime.Stage -eq 'AwaitingSublot' -and
        [string]$refusedRuntime.TargetSlotsJson -eq [string]$dispatched.TargetSlotsJson -and
        [int]$refusedRuntime.ExpectedBasketCount -eq $frozenBasketCount -and -not (Test-Present $refusedRuntime.BlockReasonCode)),
    "操作 0 / 命令 0 / $slotsBefore / AwaitingSublot / $($dispatched.TargetSlotsJson) / $frozenBasketCount / 无阻断",
    ("操作 $operationsAfterRefusal / 命令 $commandsAfterRefusal / $slotsAfterRefusal / $($refusedRuntime.Stage) / " +
     "$($refusedRuntime.TargetSlotsJson) / $($refusedRuntime.ExpectedBasketCount) / 阻断=$($refusedRuntime.BlockReasonCode)"))

$displayText = if ($null -ne $display) { "Name=「$($display.Name)」 ItemStatus=$($display.ItemStatus)" } else { '(SublotRejectionReason 不在 UIA 树里)' }
$assertions.Add(
    'G3-02-44',
    '车载端显示服务端给的拒收原因：提示区的 SublotRejectionReason 在 UIA 树里，ItemStatus 是服务端发的原因码，文字非空且点出被拒的子批（DISPLAY_SERVER_REJECTION_REASON；不比文案全文）',
    ($null -ne $display -and $display.ItemStatus -eq $reasonCode -and (Test-Present $display.Name) -and
        $display.Name.Contains($sublot, [StringComparison]::Ordinal)),
    "ItemStatus=$reasonCode，Name 非空且含 $sublot",
    $displayText)

$reopened = [bool]$onboard.CanSubmit()
$pendingEntry = @(Invoke-L2Query -Connection $connection `
        -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$($waiting.SublotRequestMessageId)'")
$assertions.Add(
    'G3-02-45',
    '拒收之后录入保持打开、可以重扫：车载端录入框可用，服务端仍在等这一站的录入（KEEP_ENTRY_OPEN_FOR_RESCAN）',
    ($reopened -and [string]$refusedRuntime.Stage -eq 'AwaitingSublot'),
    '录入框可用 / AwaitingSublot',
    "录入框可用=$reopened / $($refusedRuntime.Stage) / 录入请求 AcknowledgedAt=$(if ($pendingEntry.Count -eq 1) { $pendingEntry[0].AcknowledgedAt } else { '(no row)' })")

# --- 3. 容量对照改回 4 箱/篮，操作员重扫，正常装货 ------------------------------------------------

& $Context.ImportPackageCapacity -Rows @(@{ Pattern = $package; Capacity = 4 }) -Version 3
Submit-Sublot 'rescan'

$attemptId = Wait-OrNull -Description 'the server issued the load command for the rescanned sublot' `
    -Criterion 'load-attempt' -TimeoutSeconds 60 `
    -Probe { Get-Scalar "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'" } `
    -Until { param($v) $v }
if (-not (Test-Present $attemptId)) {
    Add-NotReached @('G3-02-46', 'G3-02-47') '重扫之后服务端没有发装货命令'
    return
}
$targetSlots = @([string](Get-Scalar "SELECT TargetSlotsJson AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'") |
    ConvertFrom-Json | ForEach-Object { [int]$_ })
$journal.Note("Load $attemptId targets slots $($targetSlots -join ', ').")

$doorWhenWaiting = [ordered]@{}
for ($ordinal = 0; $ordinal -lt $targetSlots.Count; $ordinal++) {
    $waitingOperator = Wait-L2Condition -Description "the onboard is waiting for the operator (basket $($ordinal + 1))" `
        -Journal $journal -Criterion "load-waiting-operator-$($ordinal + 1)" -TimeoutSeconds 120 `
        -Probe {
            $rows = @((Get-Progress $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })
            if ($rows.Count -gt $ordinal) { $rows[$ordinal] } else { $null }
        } `
        -Until { param($v) $null -ne $v }
    $slotNo = [int]$waitingOperator.Active[0]
    $doorWhenWaiting["$slotNo"] = Get-SlotState $slotNo
    $journal.Note("Operator puts basket $($ordinal + 1) into slot $slotNo ($($doorWhenWaiting["$slotNo"])) and closes the door.")
    $null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = 'OCCUPIED' })
    $null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})
}

$loadStatus = Wait-L2Condition -Description 'the server judged the load result' `
    -Journal $journal -Criterion 'load-status' -TimeoutSeconds 180 `
    -Probe { Get-Scalar "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'" } `
    -Until { param($v) $v -in @('Committed', 'RecoveryRequired') }
if ($loadStatus -eq 'Committed') {
    $null = Wait-L2Condition -Description 'the journey runtime consumed the load result' `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 60 `
        -Probe { Get-Stage } -Until { param($v) $v -ne 'AwaitingLoadResult' }
}
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

$allSubmissions = @(Get-Inbound 'SublotSubmitted' | Where-Object { [string]$_.Payload.sublot -eq $sublot })
$consumed = Get-Scalar "SELECT ConsumedSublotMessageId AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'"
$loadPhysical = ($targetSlots | ForEach-Object { "$_=$(Get-SlotState $_)" }) -join ' '
$expectedPhysical = ($targetSlots | ForEach-Object { "$_=CLOSED/OCCUPIED/1/0" }) -join ' '
$displayAfterLoad = Get-RejectionDisplay
$assertions.Add(
    'G3-02-46',
    "容量对照恢复后重扫照常装货：重扫是一条新提交，服务端消费的正是它，装货命令按冻结的 $frozenBasketCount 个花篮下发，车载端开的就是授权的仓、装完关着有货锁上，装载 Committed；车载端提示区撤下了拒收原因",
    ($allSubmissions.Count -eq 2 -and $allSubmissions[1].MessageId -ne $allSubmissions[0].MessageId -and
        $consumed -eq $allSubmissions[1].MessageId -and $targetSlots.Count -eq $frozenBasketCount -and
        $doorWhenWaiting.Count -eq $targetSlots.Count -and
        @($doorWhenWaiting.Values | Where-Object { -not $_.StartsWith('OPEN/') }).Count -eq 0 -and
        $loadPhysical -eq $expectedPhysical -and $loadStatus -eq 'Committed' -and $null -eq $displayAfterLoad),
    "提交 2 条、消费第 2 条 / $frozenBasketCount 仓 / 等待时 OPEN / $expectedPhysical / Committed / 拒收提示已撤",
    ("提交 $($allSubmissions.Count) 条、消费 $consumed / $($targetSlots.Count) 仓 / 等待时 $(($doorWhenWaiting.Values) -join ',') / " +
     "$loadPhysical / $loadStatus / 拒收提示=$(if ($null -ne $displayAfterLoad) { $displayAfterLoad.ItemStatus } else { '(已撤)' })"))

$rejections = @(Get-Outbound 'SublotRejected')
$operations = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId'"
$results = @(Invoke-L2Query -Connection $connection -Sql "SELECT OverallOutcome FROM OperationResults WHERE SlotOperationAttemptId = '$attemptId'")
$commands = @(Get-Outbound 'SlotOperationCommand')
$unlocks = @((Get-Progress $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' } | ForEach-Object { $_.Active } | ForEach-Object { [int]$_ })
$orders = @($riot.Snapshot().body.orders)
$finalRuntime = @(Invoke-L2Query -Connection $connection -Sql "SELECT BlockReasonCode FROM JourneyRuntimes WHERE DemandId = '$demandId'")[0]
$recoveryRequired = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE Status = 'RecoveryRequired'"
$assertions.Add(
    'G3-02-47',
    '终态没有重复提交：全程 1 条拒收（回答第一条提交）、1 笔仓位操作、1 条装货命令、1 份 COMPLETED 结果，开锁只有授权的仓各一次，RIoT 上只有那一张取货单，没有阻断原因、没有 RecoveryRequired（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE；forbidden: duplicate-riot-order、duplicate-slot-unlock、duplicate-business-commit）',
    ($rejections.Count -eq 1 -and $rejections[0].CorrelationId -eq $allSubmissions[0].MessageId -and $operations -eq 1 -and
        $commands.Count -eq 1 -and $results.Count -eq 1 -and [string]$results[0].OverallOutcome -eq 'COMPLETED' -and
        (@($unlocks | Sort-Object) -join ',') -eq (@($targetSlots | Sort-Object) -join ',') -and
        $orders.Count -eq 1 -and -not (Test-Present $finalRuntime.BlockReasonCode) -and $recoveryRequired -eq 0),
    "拒收 1 / 操作 1 / 命令 1 / 结果 1 COMPLETED / 开锁 $(@($targetSlots | Sort-Object) -join ',') / RIoT 单 1 / 无阻断 / RecoveryRequired 0",
    ("拒收 $($rejections.Count) / 操作 $operations / 命令 $($commands.Count) / 结果 $($results.Count) " +
     "$(if ($results.Count -ge 1) { [string]$results[0].OverallOutcome }) / 开锁 $(@($unlocks | Sort-Object) -join ',') / " +
     "RIoT 单 $($orders.Count) / 阻断=$($finalRuntime.BlockReasonCode) / RecoveryRequired $recoveryRequired"))

$journal.Note('FP-IS-02: entry rejected after a capacity change, reason shown on the HMI, rescan after restore loaded normally.')
