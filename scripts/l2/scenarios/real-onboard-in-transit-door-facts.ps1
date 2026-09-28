#Requires -Version 7

<#
control-server#335 开工第一步的查证场景：车在路上时，服务端对这辆车有没有新鲜的门锁事实；途中断线重连后多久恢复。

**为什么要真车载端。**真车载端在执行本服务端的在途单时，会从服务端的车辆安全接口读到 motionState=Unknown
（服务端看见本车有未终结的 RIoT 单，cs#138 起按设计如此），于是它报 VEHICLE_NOT_READY、departureSafe=false，
会话落到 RecoveryRequired / DEPARTURE_SAFETY_NOT_READY，到站才回来。合成车载端永远报安全，看不到这一段。

**它不断言任何产品行为，只抄数据。**判据只要求「每一段都观测到了」，数字写进 snapshots/door-facts-*.json：

- 段 P（取货段，空车行驶 12 秒）与段 A（关卡段，有货行驶 15 秒）：每 500 ms 抄一次会话行、服务端收到的最新安全摘要
  （与会话行 SafetyRevision 对得上的那一条）、这一代最后一条入站消息的时刻、旅程阶段与阻塞码。
- 段 D：代理断开一次连接，量「断开 → 新一代会话行出现 → 新一代的首条安全快照到库」。
- 段 L：把有货那个仓的锁反馈强制成 0（没锁），量「覆盖 → 服务端收到 allTargetSlotsLocked=false」；再放回 AUTO，量恢复。
- 段 M：让 Modbus 不回应，量「故障 → 服务端收到 SLOT_STATE_UNKNOWN」；再恢复 NORMAL，量恢复。

时刻一律取服务端库里的 ReceivedAt 减去本脚本下命令前那一刻的 UtcNow，两者在同一台机器上，没有时钟偏差。
段的顺序是 A → D → L → M：锁反馈覆盖可能让车载端锁存别的状态，放在后面免得污染前两段。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$riot = $Context.Riot
$simulator = $Context.Simulator
$proxy = $Context.ProtocolProxy
$onboard = $Context.Onboard
$agvId = $Context.AgvId

if ($null -eq $proxy) { throw 'This scenario needs ProtocolFaultProxy = $true in its setup file.' }

$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')
$sublot = "L2-DOORFACTS-$($Context.RunId)"

function ConvertTo-Instant($value) {
    if (-not (Test-L2RealPresent $value)) { return $null }
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

# Every safety message the server holds for this vehicle, oldest first by receive time, parsed. The rig's inbox is small.
function Get-SafetyMessages {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageType, RequestJson, ReceivedAt FROM ProtocolInbox " +
        "WHERE MessageType IN ('SafetyStateSnapshot', 'SafetyStateChanged')")
    $parsed = foreach ($row in $rows) {
        $envelope = [string]$row.RequestJson | ConvertFrom-Json -DateKind String
        if ([string]$envelope.agvId -ne $agvId) { continue }
        [pscustomobject]@{
            MessageType = [string]$row.MessageType
            Generation  = [long]$envelope.sessionGeneration
            Version     = [long]$envelope.payload.safetyStateVersion
            ObservedAt  = [string]$envelope.payload.observedAt
            ReceivedAt  = ConvertTo-Instant $row.ReceivedAt
            Safety      = $envelope.payload.safety
        }
    }
    return , @($parsed | Sort-Object ReceivedAt)
}

# The newest inbound message of one generation, by the server's receive time.
function Get-LastInboundAt([long]$generation) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT RequestJson, ReceivedAt FROM ProtocolInbox"
    $latest = $null
    foreach ($row in $rows) {
        $envelope = [string]$row.RequestJson | ConvertFrom-Json -DateKind String
        # Every inbound row is scanned, and StrictMode throws on a missing property.
        if ($null -eq $envelope.PSObject.Properties['agvId'] -or [string]$envelope.agvId -ne $agvId) { continue }
        if ($null -eq $envelope.PSObject.Properties['sessionGeneration']) { continue }
        if ([long]$envelope.sessionGeneration -ne $generation) { continue }
        $at = ConvertTo-Instant $row.ReceivedAt
        if ($null -eq $latest -or $at -gt $latest) { $latest = $at }
    }
    return $latest
}

# One sample of everything the server knows about this vehicle's doors right now.
function Get-DoorFactsSample([string]$phase) {
    $now = [DateTimeOffset]::UtcNow
    $sessionRows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT SessionGeneration, Readiness, ReasonCode, SafetyRevision, DepartureSafe, SafetyReasonCodesJson, " +
        "SafetyUnknownPresent FROM SessionRecoveries WHERE AgvId = '$agvId'")
    $journeyRows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT Stage, BlockReasonCode FROM JourneyRuntimes WHERE DemandId = '$demandId'")
    $session = if ($sessionRows.Count -gt 0) { $sessionRows[0] } else { $null }
    $matching = $null
    $lastInbound = $null
    if ($null -ne $session) {
        $generation = [long]$session.SessionGeneration
        if (Test-L2RealPresent $session.SafetyRevision) {
            $revision = [long]$session.SafetyRevision
            $candidates = @((Get-SafetyMessages) | Where-Object { $_.Generation -eq $generation -and $_.Version -eq $revision })
            # StrictMode Latest throws on an out-of-range index, so an empty list is checked rather than indexed.
            if ($candidates.Count -gt 0) { $matching = $candidates[$candidates.Count - 1] }
        }
        $lastInbound = Get-LastInboundAt $generation
    }
    return [pscustomobject]@{
        Phase               = $phase
        At                  = $now.ToString('o')
        Generation          = if ($session) { [long]$session.SessionGeneration } else { $null }
        Readiness           = if ($session) { [string]$session.Readiness } else { $null }
        ReasonCode          = if ($session) { [string]$session.ReasonCode } else { $null }
        SafetyRevision      = if ($session -and (Test-L2RealPresent $session.SafetyRevision)) { [long]$session.SafetyRevision } else { $null }
        RowDepartureSafe    = if ($session) { $session.DepartureSafe } else { $null }
        RowReasonCodes      = if ($session) { [string]$session.SafetyReasonCodesJson } else { $null }
        SummaryMessageType  = if ($matching) { $matching.MessageType } else { $null }
        SummaryReceivedAt   = if ($matching) { $matching.ReceivedAt.ToString('o') } else { $null }
        SummaryAgeMs        = if ($matching) { [int]($now - $matching.ReceivedAt).TotalMilliseconds } else { $null }
        Summary             = if ($matching) { $matching.Safety } else { $null }
        LastInboundAgeMs    = if ($lastInbound) { [int]($now - $lastInbound).TotalMilliseconds } else { $null }
        Stage               = if ($journeyRows.Count -gt 0) { [string]$journeyRows[0].Stage } else { $null }
        BlockReasonCode     = if ($journeyRows.Count -gt 0) { [string]$journeyRows[0].BlockReasonCode } else { $null }
    }
}

function Invoke-Sampling([string]$phase, [int]$seconds, [int]$pollMilliseconds = 500) {
    $samples = [System.Collections.Generic.List[object]]::new()
    $until = [DateTimeOffset]::UtcNow.AddSeconds($seconds)
    while ([DateTimeOffset]::UtcNow -lt $until) {
        $samples.Add((Get-DoorFactsSample $phase))
        Start-Sleep -Milliseconds $pollMilliseconds
    }
    return , $samples.ToArray()
}

function Save-Evidence([string]$name, [object]$value) {
    $path = Join-Path $Context.SnapshotRoot "door-facts-$name.json"
    $value | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    $journal.Note("Wrote $path.")
}

# The first safety message received at or after $since that satisfies $predicate; waits up to $timeoutSeconds.
function Wait-SafetyMessage([string]$description, [string]$criterion, [DateTimeOffset]$since, [scriptblock]$predicate,
    [int]$timeoutSeconds = 60) {
    try {
        return Wait-L2Condition -Description $description -Journal $journal -Criterion $criterion `
            -TimeoutSeconds $timeoutSeconds -PollMilliseconds 100 `
            -Probe { @((Get-SafetyMessages) | Where-Object { $_.ReceivedAt -ge $since -and (& $predicate $_) })[0] } `
            -Until { param($v) $null -ne $v }
    } catch {
        $journal.Note("Not observed: $description ($($_.Exception.Message))")
        return $null
    }
}

function Get-SlotReading([int]$slotNo) {
    $slot = @($simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $slotNo })[0]
    return "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
}

function Start-Drive([object]$intent) {
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $intent.OrderId
    })
}

function Complete-Drive([object]$intent, [int]$stationRiotId) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $stationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })
}

function Get-Stage { return Get-L2RealStage $connection $demandId }

function Get-AttemptId([string]$operationType) {
    return Get-L2RealScalar $connection (
        "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = '$operationType'")
}

function Invoke-SlotOperation([string]$operationType, [string]$cargoState) {
    $attemptId = Wait-L2Condition -Description "the server issued the $operationType command" `
        -Journal $journal -Criterion "$operationType-attempt" -TimeoutSeconds 180 `
        -Probe { Get-AttemptId $operationType } -Until { param($v) $v }
    $waiting = Wait-L2Condition -Description "the onboard is waiting for the operator for the $operationType" `
        -Journal $journal -Criterion "$operationType-waiting-operator" -TimeoutSeconds 120 `
        -Probe { @((Get-L2RealProgress $connection $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })[0] } `
        -Until { param($v) $null -ne $v }
    if ($waiting.Active.Count -ne 1) { throw "This scenario drives one slot per operation; got $($waiting.Active.Count)." }
    $slotNo = [int]$waiting.Active[0]
    $null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = $cargoState })
    $null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})
    return $slotNo
}

# --- 1. 需求受理，取货段（空车）行驶 12 秒并抽样 ----------------------------------------------------------------

$journal.Note("Publishing demand $($demandGuid.ToString('N')) (sublot $sublot).")
$null = $Context.MesIngest.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = $sublot; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})
$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$pickupIntent = Wait-L2RealIntent $Context $demandId 'TO_PICKUP'

$preDrive = Get-DoorFactsSample 'before-pickup-drive'
$journal.Note('Vehicle departs for the pickup station; sampling for 12 s.')
Start-Drive $pickupIntent
$pickupSamples = Invoke-Sampling 'P-pickup-drive' 12
Save-Evidence 'P-pickup-drive' @{ Before = $preDrive; Samples = $pickupSamples }
Complete-Drive $pickupIntent $Context.PickupStationRiotId
$assertions.Add('L2-DF-01', '取货段行驶中抽到了样本', ($pickupSamples.Count -ge 10), '>= 10', $pickupSamples.Count)

# --- 2. 装货 --------------------------------------------------------------------------------------------------------

$null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
$onboard.SetSublot($sublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()
$loadSlot = Invoke-SlotOperation 'Load' 'OCCUPIED'
$null = Wait-L2Condition -Description 'the loaded slot is closed, locked and occupied' `
    -Journal $journal -Criterion 'load-slot-physical' -TimeoutSeconds 60 `
    -Probe { Get-SlotReading $loadSlot } -Until { param($v) $v -eq 'CLOSED/OCCUPIED/1/0' }
$null = Wait-L2Condition -Description 'the load committed and the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$gateIntent = Wait-L2RealIntent $Context $demandId 'TO_GATE'

# --- 3. 段 A：关卡段（有货）行驶 15 秒并抽样 ----------------------------------------------------------------------

$journal.Note("Vehicle departs for the gate with slot $loadSlot loaded; sampling for 15 s.")
Start-Drive $gateIntent
$gateSamples = Invoke-Sampling 'A-gate-drive' 15
Save-Evidence 'A-gate-drive' @{ LoadSlot = $loadSlot; Samples = $gateSamples }
$assertions.Add('L2-DF-02', '关卡段行驶中抽到了样本', ($gateSamples.Count -ge 10), '>= 10', $gateSamples.Count)

# --- 4. 段 D：行驶中断线一次，量恢复 -------------------------------------------------------------------------------

$before = Get-DoorFactsSample 'D-before'
$oldGeneration = $before.Generation
$disconnectAt = [DateTimeOffset]::UtcNow
$closed = @($proxy.Command('Post', 'disconnect', @{}).body.connections)
$journal.Note("Disconnected $($closed.Count) relay connection(s) at $($disconnectAt.ToString('o')); old generation $oldGeneration.")
$reconnectSamples = [System.Collections.Generic.List[object]]::new()
$newGenerationSeenAt = $null
$newSafetyRevisionSeenAt = $null
$until = $disconnectAt.AddSeconds(60)
while ([DateTimeOffset]::UtcNow -lt $until) {
    $sample = Get-DoorFactsSample 'D-reconnect'
    $reconnectSamples.Add($sample)
    if ($null -eq $newGenerationSeenAt -and $null -ne $sample.Generation -and $sample.Generation -gt $oldGeneration) {
        $newGenerationSeenAt = [DateTimeOffset]::Parse($sample.At)
    }
    if ($null -ne $newGenerationSeenAt -and $null -eq $newSafetyRevisionSeenAt -and $null -ne $sample.SafetyRevision -and
        $sample.Generation -gt $oldGeneration) {
        $newSafetyRevisionSeenAt = [DateTimeOffset]::Parse($sample.At)
    }
    if ($null -ne $newSafetyRevisionSeenAt -and ([DateTimeOffset]::UtcNow - $newSafetyRevisionSeenAt).TotalSeconds -ge 5) { break }
    Start-Sleep -Milliseconds 100
}
$newSafety = @((Get-SafetyMessages) | Where-Object { $_.Generation -gt $oldGeneration })
$firstNewSafety = if ($newSafety.Count -gt 0) { $newSafety[0] } else { $null }
$reconnect = [ordered]@{
    OldGeneration                 = $oldGeneration
    DisconnectAt                  = $disconnectAt.ToString('o')
    ClosedConnections             = $closed.Count
    NewGenerationSeenAfterMs      = if ($newGenerationSeenAt) { [int]($newGenerationSeenAt - $disconnectAt).TotalMilliseconds } else { $null }
    NewSafetyRevisionSeenAfterMs  = if ($newSafetyRevisionSeenAt) { [int]($newSafetyRevisionSeenAt - $disconnectAt).TotalMilliseconds } else { $null }
    FirstNewSafetyMessage         = $firstNewSafety
    FirstNewSafetyReceivedAfterMs = if ($firstNewSafety) { [int]($firstNewSafety.ReceivedAt - $disconnectAt).TotalMilliseconds } else { $null }
    Samples                       = $reconnectSamples.ToArray()
}
Save-Evidence 'D-reconnect' $reconnect
$assertions.Add('L2-DF-03', '行驶中断线后，新一代会话的首条安全快照在 60 秒内到库',
    ($null -ne $firstNewSafety), 'a SafetyStateSnapshot of a newer generation',
    $(if ($firstNewSafety) { "$($firstNewSafety.MessageType) gen $($firstNewSafety.Generation) after $($reconnect.FirstNewSafetyReceivedAfterMs) ms" } else { '(none)' }))

# --- 5. 段 L：行驶中把有货的仓锁反馈强制成 0，量到库时间；再放回 AUTO -----------------------------------------------

$lockAt = [DateTimeOffset]::UtcNow
$null = $simulator.Command('Put', "slots/$loadSlot/lock-feedback-override", @{ mode = 'FIXED_0' })
$unlocked = Wait-SafetyMessage "the server received a safety summary with the doors not all locked" 'lock-not-closed' $lockAt `
    { param($m) -not [bool]$m.Safety.allTargetSlotsLocked }
$unlockedSamples = Invoke-Sampling 'L-lock-feedback-0' 5
$relockAt = [DateTimeOffset]::UtcNow
$null = $simulator.Command('Put', "slots/$loadSlot/lock-feedback-override", @{ mode = 'AUTO' })
$relocked = Wait-SafetyMessage "the server received a safety summary with the doors locked again" 'lock-closed-again' $relockAt `
    { param($m) [bool]$m.Safety.allTargetSlotsLocked }
$afterRelock = Invoke-Sampling 'L-lock-feedback-auto' 3
Save-Evidence 'L-lock-feedback' ([ordered]@{
    LoadSlot            = $loadSlot
    OverrideAt          = $lockAt.ToString('o')
    Unlocked            = $unlocked
    UnlockedAfterMs     = if ($unlocked) { [int]($unlocked.ReceivedAt - $lockAt).TotalMilliseconds } else { $null }
    UnlockedSamples     = $unlockedSamples
    RestoreAt           = $relockAt.ToString('o')
    Relocked            = $relocked
    RelockedAfterMs     = if ($relocked) { [int]($relocked.ReceivedAt - $relockAt).TotalMilliseconds } else { $null }
    AfterRestoreSamples = $afterRelock
})
$assertions.Add('L2-DF-04', '行驶中锁反馈变为未锁，服务端收到 allTargetSlotsLocked=false',
    ($null -ne $unlocked), 'observed',
    $(if ($unlocked) { "after $([int]($unlocked.ReceivedAt - $lockAt).TotalMilliseconds) ms, reasons $(@($unlocked.Safety.reasonCodes) -join ',')" } else { '(none)' }))

# --- 6. 段 M：行驶中 Modbus 不回应，量到库时间；再恢复 ------------------------------------------------------------

$faultAt = [DateTimeOffset]::UtcNow
$null = $simulator.Command('Put', 'faults/modbus', @{ mode = 'NO_RESPONSE' })
$unknown = Wait-SafetyMessage "the server received a safety summary naming the slot state unknown" 'slot-state-unknown' $faultAt `
    { param($m) @($m.Safety.reasonCodes) -contains 'SLOT_STATE_UNKNOWN' }
$unknownSamples = Invoke-Sampling 'M-modbus-no-response' 5
$healAt = [DateTimeOffset]::UtcNow
$null = $simulator.Command('Put', 'faults/modbus', @{ mode = 'NORMAL' })
$healed = Wait-SafetyMessage "the server received a safety summary with the slot state known again" 'slot-state-known' $healAt `
    { param($m) @($m.Safety.reasonCodes) -notcontains 'SLOT_STATE_UNKNOWN' }
$afterHeal = Invoke-Sampling 'M-modbus-normal' 3
Save-Evidence 'M-modbus' ([ordered]@{
    FaultAt             = $faultAt.ToString('o')
    Unknown             = $unknown
    UnknownAfterMs      = if ($unknown) { [int]($unknown.ReceivedAt - $faultAt).TotalMilliseconds } else { $null }
    UnknownSamples      = $unknownSamples
    HealAt              = $healAt.ToString('o')
    Healed              = $healed
    HealedAfterMs       = if ($healed) { [int]($healed.ReceivedAt - $healAt).TotalMilliseconds } else { $null }
    AfterHealSamples    = $afterHeal
})
$assertions.Add('L2-DF-05', '行驶中 IO 失联，服务端收到 SLOT_STATE_UNKNOWN',
    ($null -ne $unknown), 'observed',
    $(if ($unknown) { "after $([int]($unknown.ReceivedAt - $faultAt).TotalMilliseconds) ms" } else { '(none)' }))

# --- 7. 全部安全消息原样存档，再把车开到关卡、尽量走完（只记录，不判） ------------------------------------------------

Save-Evidence 'all-safety-messages' (Get-SafetyMessages)

Complete-Drive $gateIntent $Context.GateStationRiotId
try {
    $unloadSlot = Invoke-SlotOperation 'Unload' 'EMPTY'
    $stage = Wait-L2Condition -Description 'the journey completed at the gate' `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { Get-Stage } -Until { param($v) $v -eq 'Completed' }
    $journal.Note("Unloaded slot $unloadSlot; journey $stage.")
} catch {
    $journal.Note("The trip did not finish after the probes (recorded, not judged): $($_.Exception.Message)")
}
Save-Evidence 'final' (Get-DoorFactsSample 'final')
$journal.Note('Scenario finished.')
