#Requires -Version 7

<#
G3 `FP-IS-03` 的第一条场景：出发前安全检查在答复之后因安全状态变化而过期。协议向量
`CV-PREDEPARTURE-SAFETY-EXPIRES`：`PreDepartureSafetyCheck` → `PreDepartureSafetyCheckResult` → `SafetyStateChanged` →
`ProtocolProblem`，稳定错误码 `PREDEPARTURE_CHECK_EXPIRED`。

装置与 `FP-IS-01`/`02` 相同：真服务端 + 真车载端 WPF + 真 slots-simulator + 假 RIoT + 假 MesIngest。

**怎么让检查在答复之后过期。**服务端判一份安全答复是在发出检查的同一轮里，答复成立就立刻建去关卡的单；检查、答复、
发车之间没有空隙可插。所以场景先把发车扣住：离站等待期间把去关卡站的路线改成不可达（假 RIoT `route-costs`），检查发出、
答复 SAFE 之后，建单门禁拒绝去关卡的单，车带着一份有效答复停在 `AwaitingDepartureSafety`。这时：

1. 把一个不相干的仓（8 号）的锁反馈固定成未锁：车载端报 `SafetyStateChanged`（不安全），会话转 `RecoveryRequired`；
2. 趁车不安全把路线改回可达；
3. 放开锁反馈：车载端再报 `SafetyStateChanged`（安全），会话恢复。

会话恢复后的第一轮，服务端照常补发那张尚未结清的检查——它问的是两次变化之前的安全版本。车载端版本已前进，
不作答，回 `ProtocolProblem(PREDEPARTURE_CHECK_EXPIRED)`。服务端判出检查过期，作废它，以新身份按当前版本重问，
车载端答 SAFE，这次门禁放行，车出发。两端这一半都是 2026-09-13 按用户裁定补上的：服务端
`docs/defects/20260913-expired-predeparture-check-never-asked-again.md`，车载端 `docs/W2G_PREDEPARTURE_CHECK_EXPIRED.md`。

**断言只读服务端的库、模拟器与假 RIoT 快照。**
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SessionContinuity.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$simulator = $Context.Simulator
$connection = $Context.Connection

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: the safety changes come from its Modbus reading.'
}

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "G3-03E-$($Context.RunId)"
$bystanderSlot = 8
$gateRouteKey = "$($Context.MapId):$($Context.GateStationRiotId)"

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
function Get-BlockReason { return Get-Scalar "SELECT BlockReasonCode AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'" }

function Test-Present([object]$value) { return ($null -ne $value -and [string]$value -ne '') }

function ConvertTo-Instant([object]$value) {
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

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

function Get-Inbound([string]$messageType) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, RequestJson, ReceivedAt FROM ProtocolInbox " +
        "WHERE MessageType = '$messageType' ORDER BY ReceivedAt, MessageId")
    $messages = foreach ($row in $rows) {
        [pscustomobject]@{
            MessageId = [string]$row.MessageId
            At        = ConvertTo-Instant $row.ReceivedAt
            Payload   = ([string]$row.RequestJson | ConvertFrom-Json).payload
        }
    }
    return , @($messages)
}

function Get-Progress([string]$attemptId) {
    $all = Get-Inbound 'OperationProgress'
    $mine = foreach ($message in $all) {
        if ([string]$message.Payload.slotOperationAttemptId -ne $attemptId) { continue }
        [pscustomobject]@{ At = $message.At; Phase = [string]$message.Payload.phase; Active = @($message.Payload.activeUnlockSlots | ForEach-Object { [int]$_ }) }
    }
    return , @($mine)
}

function Get-Session {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        'SELECT SessionGeneration, SafetyRevision, DepartureSafe, Readiness, ReasonCode, SafetyReasonCodesJson FROM SessionRecoveries')
    return $rows[0]
}

# --- 1. 需求受理、到取货点、录入、单仓装载 --------------------------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot = $sublot; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
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
    } -Until { param($v) $null -ne $v }

$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = $intent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.PickupStationRiotId; processingOrder = $false; clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })

$null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
$onboard.SetSublot($sublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()

$attemptId = Wait-L2Condition -Description 'the server issued the load command' `
    -Journal $journal -Criterion 'load-attempt' -TimeoutSeconds 180 `
    -Probe { Get-Scalar "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'" } `
    -Until { param($v) $v }
$waiting = Wait-L2Condition -Description 'the onboard is waiting for the operator' `
    -Journal $journal -Criterion 'load-waiting-operator' -TimeoutSeconds 120 `
    -Probe { @((Get-Progress $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })[0] } `
    -Until { param($v) $null -ne $v }
$loadSlot = [int]$waiting.Active[0]
$null = $simulator.Command('Put', "slots/$loadSlot/cargo", @{ state = 'OCCUPIED' })
$null = $simulator.Command('Post', "slots/$loadSlot/close-door", @{})

# --- 2. 离站等待期间把去关卡的路线改成不可达 ---------------------------------------------------------

$null = Wait-L2Condition -Description 'the load committed and the vehicle waits out the station departure' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingStationDeparture' }
$journal.Note("Gate route $gateRouteKey made unreachable before the departure check is asked.")
$null = $riot.Command('Put', 'route-costs', @{ costs = @{ $gateRouteKey = -1 } })

$held = Wait-L2Condition -Description 'a SAFE answer is in hand but the create gate refuses the gate leg' `
    -Journal $journal -Criterion 'departure-held' -TimeoutSeconds 90 `
    -Probe {
        $results = @((Get-Inbound 'PreDepartureSafetyCheckResult') | Where-Object { [string]$_.Payload.outcome -eq 'SAFE' })
        if ((Get-Stage) -eq 'AwaitingDepartureSafety' -and $results.Count -ge 1 -and
            (Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'") -eq 0) {
            $results[0]
        } else { $null }
    } -Until { param($v) $null -ne $v }
$sessionBefore = Get-Session
$journal.Note("Departure held with a SAFE answer (reason $(Get-BlockReason)); session generation $($sessionBefore.SessionGeneration), safety revision $($sessionBefore.SafetyRevision).")

# --- 3. 安全状态变了两次，中间把路线改回可达 ------------------------------------------------------------

$unsafeAt = [DateTimeOffset]::UtcNow
$journal.Note("Slot $bystanderSlot lock feedback forced open: the vehicle is no longer departure-safe.")
$null = $simulator.Command('Put', "slots/$bystanderSlot/lock-feedback-override", @{ mode = 'FIXED_0' })
$unsafeChange = Wait-L2Condition -Description 'the onboard reported the unsafe safety state' `
    -Journal $journal -Criterion 'unsafe-safety-change' -TimeoutSeconds 30 `
    -Probe {
        @((Get-Inbound 'SafetyStateChanged') | Where-Object { $_.At -gt $held.At -and -not [bool]$_.Payload.safety.departureSafe })[0]
    } -Until { param($v) $null -ne $v }

$journal.Note("Gate route $gateRouteKey made reachable again while the vehicle is unsafe.")
$null = $riot.Command('Put', 'route-costs', @{ costs = @{} })

$safeAt = [DateTimeOffset]::UtcNow
$journal.Note("Slot $bystanderSlot lock feedback released: the vehicle is departure-safe again.")
$null = $simulator.Command('Put', "slots/$bystanderSlot/lock-feedback-override", @{ mode = 'AUTO' })
$safeChange = Wait-L2Condition -Description 'the onboard reported the safe safety state again' `
    -Journal $journal -Criterion 'safe-safety-change' -TimeoutSeconds 30 `
    -Probe {
        @((Get-Inbound 'SafetyStateChanged') | Where-Object { $_.At -gt $unsafeChange.At -and [bool]$_.Payload.safety.departureSafe })[0]
    } -Until { param($v) $null -ne $v }

# --- 4. 旧检查被拒、以新身份重问、出发 -----------------------------------------------------------------

$departed = Wait-L2Condition -Description 'the vehicle departed on a check asked again after the expiry' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -in @('AwaitingGateArrival', 'Blocked') }

$checks = Get-Outbound 'PreDepartureSafetyCheck'
$results = Get-Inbound 'PreDepartureSafetyCheckResult'
$problems = @((Get-Inbound 'ProtocolProblem') | Where-Object { [string]$_.Payload.problem.reasonCode -eq 'PREDEPARTURE_CHECK_EXPIRED' })
$expiredCheck = if ($problems.Count -ge 1) { @($checks | Where-Object { $_.MessageId -eq [string]$problems[0].Payload.rejectedMessageId })[0] } else { $null }
$expiredResult = if ($null -ne $expiredCheck) {
    @($results | Where-Object { [string]$_.Payload.preDepartureSafetyCheckId -eq [string]$expiredCheck.Payload.preDepartureSafetyCheckId })[0]
} else { $null }
$runtimeRow = (Invoke-L2Query -Connection $connection -Sql (
    "SELECT PreDepartureSafetyCheckId, PreDepartureSafetyCheckMessageId, ConsumedSafetyResultMessageId FROM JourneyRuntimes WHERE DemandId = '$demandId'"))[0]
$reissuedCheck = @($checks | Where-Object { $_.MessageId -eq [string]$runtimeRow.PreDepartureSafetyCheckMessageId })[0]
$reissuedResult = @($results | Where-Object { [string]$_.Payload.preDepartureSafetyCheckId -eq [string]$runtimeRow.PreDepartureSafetyCheckId -and [string]$_.Payload.outcome -eq 'SAFE' })[0]
$gateIntents = (Invoke-L2Query -Connection $connection -Sql "SELECT CreatedAt FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'")
$gateCreatedAt = if ($gateIntents.Count -ge 1) { ConvertTo-Instant $gateIntents[0].CreatedAt } else { $null }

$orderOk = $null -ne $expiredCheck -and $null -ne $expiredResult -and $problems.Count -ge 1 -and
    $expiredCheck.At -lt $expiredResult.At -and $expiredResult.At -lt $safeChange.At -and $safeChange.At -lt $problems[0].At -and
    [long]$safeChange.Payload.safetyStateVersion -gt [long]$expiredCheck.Payload.expectedSafetyStateVersion
$assertions.Add(
    'G3-03-01',
    '消息顺序与向量一致：检查 → 它的答复 → 安全状态变化（版本越过检查问的版本）→ 车载端回 ProtocolProblem(PREDEPARTURE_CHECK_EXPIRED) 拒收那张检查（CV-PREDEPARTURE-SAFETY-EXPIRES orderedExpectedMessages / stableErrorCode）',
    $orderOk,
    'Check(v) < Result < SafetyStateChanged(>v) < ProtocolProblem(PREDEPARTURE_CHECK_EXPIRED, rejected=Check)',
    "Check=$(if ($expiredCheck) { "v$($expiredCheck.Payload.expectedSafetyStateVersion)" } else { '(none)' }) / Result=$(if ($expiredResult) { $expiredResult.Payload.outcome } else { '(none)' }) / SafetyStateChanged=v$($safeChange.Payload.safetyStateVersion) / ProtocolProblem×$($problems.Count) / 有序=$orderOk")

$assertions.Add(
    'G3-03-02',
    '不凭过期的检查出发：去关卡的单晚于重问那张检查的 SAFE 答复，旅程消费的答复就是重问那张的，不是过期那张（NEVER_DEPART_ON_EXPIRED_CHECK）',
    ($departed -eq 'AwaitingGateArrival' -and $null -ne $reissuedResult -and $null -ne $gateCreatedAt -and
        $gateCreatedAt -gt $reissuedResult.At -and [string]$runtimeRow.ConsumedSafetyResultMessageId -eq $reissuedResult.MessageId -and
        ($null -eq $expiredResult -or $expiredResult.MessageId -ne [string]$runtimeRow.ConsumedSafetyResultMessageId)),
    'AwaitingGateArrival / 建单晚于重问的 SAFE / 消费的是重问的答复',
    "$departed / 建单晚于重问=$(if ($gateCreatedAt -and $reissuedResult) { $gateCreatedAt -gt $reissuedResult.At } else { '(n/a)' }) / consumed=$([string]$runtimeRow.ConsumedSafetyResultMessageId)")

$assertions.Add(
    'G3-03-03',
    '安全状态变化使检查作废：过期那张的发件箱行已作废，重问那张是新身份，问的是变化之后的安全版本（EXPIRE_CHECK_ON_SAFETY_STATE_CHANGE）',
    ($null -ne $expiredCheck -and $expiredCheck.Fenced -and $null -ne $reissuedCheck -and
        [string]$reissuedCheck.Payload.preDepartureSafetyCheckId -ne [string]$expiredCheck.Payload.preDepartureSafetyCheckId -and
        [long]$reissuedCheck.Payload.expectedSafetyStateVersion -ge [long]$safeChange.Payload.safetyStateVersion),
    "过期那张作废 / 新身份 / 问 v≥$($safeChange.Payload.safetyStateVersion)",
    "作废=$(if ($expiredCheck) { $expiredCheck.Fenced } else { '(none)' }) / 新身份=$(if ($expiredCheck -and $reissuedCheck) { [string]$reissuedCheck.Payload.preDepartureSafetyCheckId -ne [string]$expiredCheck.Payload.preDepartureSafetyCheckId } else { '(n/a)' }) / 问 v$(if ($reissuedCheck) { $reissuedCheck.Payload.expectedSafetyStateVersion } else { '(none)' })")

$unsafeDelay = ($unsafeChange.At - $unsafeAt).TotalSeconds
$safeDelay = ($safeChange.At - $safeAt).TotalSeconds
$assertions.Add(
    'G3-03-04',
    '车载端及时报告安全状态变化：锁反馈被改后 5 秒内报不安全，放开后 5 秒内报安全（REPORT_SAFETY_STATE_CHANGE_PROMPTLY）',
    ($unsafeDelay -le 5 -and $safeDelay -le 5),
    '≤5s / ≤5s',
    "$([math]::Round($unsafeDelay, 2))s / $([math]::Round($safeDelay, 2))s")

$gateOrders = @($riot.Snapshot().body.orders | Where-Object { [string]$_.upperId -like '*GATE*' })
$assertions.Add(
    'G3-03-05',
    '过期之后重问并凭新答复出发：重问那张得到车载端的 SAFE，去关卡的意图恰好一条、RIoT 上的关卡单恰好一张（REREQUEST_CHECK_AFTER_EXPIRY）',
    ($null -ne $reissuedResult -and $gateIntents.Count -eq 1 -and $gateOrders.Count -eq 1),
    'SAFE / 意图 1 / 关卡单 1',
    "$(if ($reissuedResult) { 'SAFE' } else { '(no SAFE for the reissued check)' }) / 意图 $($gateIntents.Count) / 关卡单 $($gateOrders.Count)")

# G3-03-06 is read after the vehicle departed, and departing is what takes this session out of Ready by design:
# once the gate order exists the vehicle-safety projection says UNKNOWN (RIOT_NONFINAL_ORDER_PRESENT) or MOVING,
# the onboard reports VEHICLE_NOT_READY / ACTION_NOT_ALLOWED_IN_STATE, and the server holds the session at
# RecoveryRequired / DEPARTURE_SAFETY_NOT_READY until the vehicle stands still (docs/RELEASE-CANDIDATE.md
# section 8). The onboard polls that projection about once a second, so a bare "is it Ready" here raced it and
# went red in control-server#128's journey self-check (control-server#138). Test-L2SessionKeptThroughDeparture
# still asks what the rejection could have broken -- the generation, and a usable session after the rejection --
# and accepts a non-Ready reading only when the departure explains all of it. Session first, then the safety
# changes and RIoT, so the revision the session holds is among the changes read.
$sessionAfter = Get-Session
$safetyChanges = @((Get-Inbound 'SafetyStateChanged') | ForEach-Object {
    [pscustomobject]@{ At = $_.At; Version = [long]$_.Payload.safetyStateVersion; DepartureSafe = [bool]$_.Payload.safety.departureSafe }
})
$gateOrderAfter = @($riot.Snapshot().body.orders | Where-Object { [string]$_.upperId -like '*GATE*' })[0]
$continuity = Test-L2SessionKeptThroughDeparture -GenerationBefore ([long]$sessionBefore.SessionGeneration) -Session $sessionAfter `
    -ProblemAt $(if ($problems.Count -ge 1) { $problems[0].At } else { $null }) -GateIntentCreatedAt $gateCreatedAt `
    -SafetyChanges $safetyChanges -GateOrderState $(if ($null -ne $gateOrderAfter) { [int]$gateOrderAfter.orderState } else { $null })
$continuityPath = switch ($continuity.Path) {
    'Ready' { 'Ready' }
    'DepartureExplainedDemotion' { "出发解释的降级（$([string]$sessionAfter.ReasonCode) $([string]$sessionAfter.SafetyReasonCodesJson)，安全版本 $($sessionAfter.SafetyRevision)，关卡单状态 $($gateOrderAfter.orderState)）" }
    default { "FAIL：$($continuity.Reason)（$([string]$sessionAfter.Readiness) / $([string]$sessionAfter.ReasonCode) $([string]$sessionAfter.SafetyReasonCodesJson)）" }
}
$assertions.Add(
    'G3-03-06',
    '拒收过期检查不断会话：会话代次在整个过程中不变；拒收之后车凭安全状态出发；结束时 Ready，或者未就绪完全由这次出发解释（DEPARTURE_SAFETY_NOT_READY、只含车辆运动原因、那版安全状态晚于建单、关卡单未结束，control-server#138）',
    $continuity.Passed,
    "代次 $($sessionBefore.SessionGeneration) / Ready 或出发解释的降级",
    "代次 $($sessionAfter.SessionGeneration) / $continuityPath")

$loads = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM StationOperations WHERE DemandId = '$demandId'"
$assertions.Add(
    'G3-03-07',
    '终态没有重复提交也没有未证实的物理状态：一笔装载 Committed，8 号仓锁反馈已恢复，装载仓关着、有货、锁上（finalState NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE）',
    ($loads.Count -eq 1 -and [string]$loads[0].Status -eq 'Committed' -and
        "$(@($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $loadSlot })[0].doorState)" -eq 'CLOSED' -and
        [int](@($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $bystanderSlot })[0].lockFeedbackRaw) -eq 1),
    '1 笔 Committed / 8 号锁反馈 1 / 装载仓 CLOSED',
    "$($loads.Count) 笔 $(if ($loads.Count -ge 1) { [string]$loads[0].Status }) / 8 号锁反馈 $(@($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $bystanderSlot })[0].lockFeedbackRaw) / 装载仓 $(@($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $loadSlot })[0].doorState)")

$journal.Note('FP-IS-03: a pre-departure check expired after its answer, was refused by the vehicle, asked again and departed on.')
