#Requires -Version 7

<#
control-server#789（CP-0017，REQ-0246 与 REQ-0167）：车在路上，车载端报「仓位状态未知」（SLOT_STATE_UNKNOWN）约 5 秒不恢复。

1. 宽限 3 秒用完才交故障模型：从读不到那一刻起不早于 3 秒，服务端对本车本单发 Hold，按不住、车还在动，急停一次。
2. RIoT 把单停成 PAUSED、闩锁锁上；门锁仍读不到：不解除。
3. 读数恢复、读回锁闭：连续 10 秒之后服务端自己 `cancelEmergency`，原因 `EMERGENCY_DOOR_READING_RESTORED`，不早于 10 秒。
4. 闩锁放开之后：单仍停着、没有 CONTINUE、故障仍在效、不再急停。
5. 人经故障处置入口「继续原单」（`RESUME_HELD_ORDER`）：CONTINUE 发出，RIoT 让单跑起来，故障清除，车再走、不再急停。

**假 RIoT 不替服务端改状态**（`FakeRiotState.FakeCommandInvocation`）：单 PAUSED、闩锁锁上与放开、续行后单跑起来，都由场景照真实
RIoT（riot-behavior-lab BC-ORDER-006、BC-VEH-005）的样子在控制面上摆出来，摆的时机对着审计表或 RIoT 侧已经出现的那一条。
续行那一步：入口发出 CONTINUE 后要在 5 秒内回读到单在执行（control-server#791），所以请求放在后台线程里发，主线程看到
`CMD_ORDER_CONTINUE_FROM_HELD` 落到假 RIoT 上就把单置为执行（3）。

第二个事实（释放落库后的原因、续行请求的结果）按 scripts/l2/README.md 第 14 条用 Wait-L2ConditionOrLast 等，不读一次就断言。

取红证据：自动解除前要连续读回锁闭的时长设为 5 分钟（setup 里 `VehicleFault.DoorReadingRestoredFor = '00:05:00'`），等于没有自动解除。
L2-DU-01～03 照常通过，L2-DU-04 在等服务端解除那一步超时失败（见 PR 证据目录 red/）。
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
$vehicleKey = [string]$Context.VehicleKey
$vehicleTarget = "vehicle:$vehicleKey"
$recoveryUri = "http://127.0.0.1:$($Context.HealthPort)/api/safety/v1/vehicle-fault-recoveries"
if (-not $Context.FaultRecoveryCredential) {
    throw 'The setup file must turn VehicleFaultRecovery on; without it nobody can continue the held order and this scenario proves nothing.'
}

function Get-Attempts([string]$commandType) {
    # 调用方先赋值再用，不要把这个函数直接送进管道：`Invoke-L2Query` 的 `return , $rows` 包装穿得过一层 return。
    return Invoke-L2Query -Connection $connection -Sql @"
SELECT CommandType, AgvId, TargetUpperId, AttemptNumber, IssuedAt, Outcome, FaultGeneration, ReceiptJson
FROM RiotOrderCommandAudit WHERE CommandType = '$commandType' ORDER BY AttemptNumber
"@
}

function Get-RiotInvocations([string]$commandType) {
    # `return , @(...)`：只有一个元素时 `return @(...)` 会被拆成那个元素本身，严格模式下取 `.Count` 就抛错。
    return , @(@($riot.Snapshot().body.commandInvocations) | Where-Object { [string]$_.commandType -eq $commandType })
}

function Set-Vehicle([hashtable]$fields) {
    $body = @{ vehicleKey = $vehicleKey } + $fields
    $null = $riot.Command('Put', 'vehicle', $body)
}

function Set-Doors([ValidateSet('Locked', 'Unknown')][string]$doors) {
    # IO 读不到时真车载端报的样子（真装置实测，control-server#335）：带 LOCK_NOT_CLOSED、allTargetSlotsLocked=false。
    [string[]]$reasons = if ($doors -eq 'Locked') {
        @('ACTION_NOT_ALLOWED_IN_STATE')
    } else {
        @('SLOT_STATE_UNKNOWN', 'LOCK_NOT_CLOSED', 'UNLOCK_OUTPUT_NOT_RESET', 'ACTION_NOT_ALLOWED_IN_STATE')
    }
    $null = Set-L2OnboardSafety -Onboard $onboard -Connection $connection -AgvId $agvId -Journal $journal -Safety @{
        departureSafe         = $false
        vehicleStopped        = $false
        allTargetSlotsLocked  = ($doors -eq 'Locked')
        allUnlockOutputsReset = ($doors -eq 'Locked')
        unknownPresent        = ($doors -ne 'Locked')
        reasonCodes           = $reasons
        affectedSlots         = @(1, 2, 3, 4, 5, 6, 7, 8)
    }
}

function Get-Instant($value) {
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Fault {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Level, EvidenceCode, FaultGeneration FROM VehicleFaultStates WHERE AgvId = '$agvId'"
    if ($rows.Count -gt 0) { return $rows[0] }
    return $null
}

# --- 1. 一台车上路 ----------------------------------------------------------------------------------

$demandId = [guid]::NewGuid().ToString('N')
$journal.Note("Publishing demand $demandId.")
$null = $mes.Command('Put', "demands/$demandId", @{
    sublot      = "L2-DU-$($Context.RunId)"
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$journey = Wait-L2Condition -Description 'the vehicle took a journey' `
    -Journal $journal -Criterion 'journey-dispatched' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql "SELECT DemandId FROM JourneyRuntimes WHERE AgvId = '$agvId'"
        if ($rows.Count -ge 1) { $rows[0] } else { $null }
    } `
    -Until { param($v) $null -ne $v }

$intent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'pickup-intent' -TimeoutSeconds 90 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$([string]$journey.DemandId)' AND Purpose = 'TO_PICKUP'"
        if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
    } `
    -Until { param($v) $null -ne $v }
$upperId = [string]$intent.UpperId
$orderId = [string]$intent.OrderId

# Between stations, as a driving vehicle is: RIoT reports station 0 there (ADR-cross-0060).
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $vehicleKey })
Set-Vehicle @{ movementState = 'MT_RUNNING'; speed = 0.55; processingOrder = $true; currentPosition = 0 }
Set-Doors 'Locked'
$null = Wait-L2Iterations -Riot $riot -Count 3 -TimeoutSeconds 60 -Journal $journal

# --- 2. 读不到 5 秒：宽限 3 秒用完才按住、急停 ------------------------------------------------------------

$journal.Note("The onboard reports a slot it cannot read while $agvId drives on $upperId, and keeps reporting it.")
$unknownAt = [DateTimeOffset]::UtcNow
Set-Doors 'Unknown'

$null = Wait-L2Condition -Description 'the server issued the emergency stop' `
    -Journal $journal -Criterion 'trigger-issued' -TimeoutSeconds 60 `
    -Probe { (Get-Attempts 'triggerEmergency').Count } -Until { param($v) $v -ge 1 }
# 审计行先武装再调 RIoT、回读后才写 ReceiptJson，三步各自提交：等结算落库再读（control-server#193 普查）。
$trigger = Wait-L2ConditionOrLast -Description 'the trigger attempt was settled' `
    -Journal $journal -Criterion 'trigger-settled' -TimeoutSeconds 30 `
    -Probe { $rows = Get-Attempts 'triggerEmergency'; if ($rows.Count -gt 0) { $rows[0] } else { $null } } `
    -Until { param($row) -not [string]::IsNullOrEmpty([string]$row.ReceiptJson) }
$holds = Get-Attempts 'OrderHold'
$hold = if ($holds.Count -gt 0) { $holds[0] } else { $null }
# 车载端报文由 L2 进程发出、服务端收到大约同时；3 秒从服务端收到起算，所以这里放 0.5 秒的余量给本机时钟与传输。
$stoppedAfter = if ($hold) { ((Get-Instant $hold.IssuedAt) - $unknownAt).TotalSeconds } else { -1 }
$assertions.Add(
    'L2-DU-01', '读不到之后不早于 3 秒、不晚于 6 秒才发 Hold；Hold 恰好一条，打在本车本单上',
    ($holds.Count -eq 1 -and [string]$hold.TargetUpperId -eq $upperId -and $stoppedAfter -ge 2.5 -and $stoppedAfter -le 6),
    "1 / $upperId / 3-6 s", ("{0} / {1} / {2:0.0} s" -f $holds.Count, $(if ($hold) { [string]$hold.TargetUpperId } else { '-' }), $stoppedAfter))

$fault = Get-Fault
$assertions.Add(
    'L2-DU-02', '急停恰好一条，打在这台车上、在 Hold 之后；故障事实是门锁症状',
    ((Get-Attempts 'triggerEmergency').Count -eq 1 -and [string]$trigger.TargetUpperId -eq $vehicleTarget -and
        $null -ne $hold -and (Get-Instant $hold.IssuedAt) -le (Get-Instant $trigger.IssuedAt) -and
        $null -ne $fault -and [string]$fault.EvidenceCode -eq 'VEHICLE_DOOR_NOT_PROVEN_LOCKED'),
    "1 / $vehicleTarget / hold first / VEHICLE_DOOR_NOT_PROVEN_LOCKED",
    "$((Get-Attempts 'triggerEmergency').Count) / $([string]$trigger.TargetUpperId) / $(if ($fault) { [string]$fault.EvidenceCode } else { '-' })")

# --- 3. RIoT 停住单、闩锁锁上；门锁仍读不到：不解除 ---------------------------------------------------------

$journal.Note('RIoT parks the order (PAUSED 7) and latches the emergency stop; the slot still cannot be read.')
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 7 })
Set-Vehicle @{ emergencyState = 'CAN_RECOVER'; movementState = 'MT_PAUSED'; speed = 0 }
$null = Wait-L2Iterations -Riot $riot -Count 4 -TimeoutSeconds 60 -Journal $journal
$releasesEarly = Get-Attempts 'cancelEmergency'
$assertions.Add(
    'L2-DU-03', '门锁仍读不到时，急停不解除', ($releasesEarly.Count -eq 0), 0, $releasesEarly.Count)

# --- 4. 读数恢复：连续 10 秒读回锁闭后服务端自己解除 ---------------------------------------------------------

$journal.Note('The onboard reads every door locked again.')
$lockedAt = [DateTimeOffset]::UtcNow
Set-Doors 'Locked'
$null = Wait-L2Condition -Description 'the server released the emergency stop on its own' `
    -Journal $journal -Criterion 'release-issued' -TimeoutSeconds 45 `
    -Probe { (Get-Attempts 'cancelEmergency').Count } -Until { param($v) $v -ge 1 }
$release = Wait-L2ConditionOrLast -Description 'the release attempt was settled' `
    -Journal $journal -Criterion 'release-settled' -TimeoutSeconds 30 `
    -Probe { $rows = Get-Attempts 'cancelEmergency'; if ($rows.Count -gt 0) { $rows[0] } else { $null } } `
    -Until { param($row) -not [string]::IsNullOrEmpty([string]$row.ReceiptJson) }
$releaseReason = ([string]$release.ReceiptJson | ConvertFrom-Json).Reason
$releasedAfter = ((Get-Instant $release.IssuedAt) - $lockedAt).TotalSeconds
$assertions.Add(
    'L2-DU-04', '读回锁闭之后不早于 10 秒，服务端自己解除一次，原因 EMERGENCY_DOOR_READING_RESTORED，挂在这一代故障下',
    ((Get-Attempts 'cancelEmergency').Count -eq 1 -and $releaseReason -eq 'EMERGENCY_DOOR_READING_RESTORED' -and
        $releasedAfter -ge 9.5 -and [string]$release.FaultGeneration -eq [string]$fault.FaultGeneration),
    '1 / EMERGENCY_DOOR_READING_RESTORED / >= 10 s / same generation',
    ("{0} / {1} / {2:0.0} s / {3} vs {4}" -f (Get-Attempts 'cancelEmergency').Count, $releaseReason, $releasedAfter,
        [string]$release.FaultGeneration, [string]$fault.FaultGeneration))

# --- 5. 闩锁放开：单仍停着、没有 CONTINUE、故障仍在效、不再急停 -------------------------------------------------

$journal.Note('RIoT clears the latch.')
Set-Vehicle @{ emergencyState = 'OK' }
$null = Wait-L2Iterations -Riot $riot -Count 5 -TimeoutSeconds 60 -Journal $journal
$faultHeld = Get-Fault
$assertions.Add(
    'L2-DU-05', '解除之后：没有 CONTINUE（审计与 RIoT 两侧）、仍只 1 次急停、故障仍在效、旅程码仍是门锁症状',
    ((Get-Attempts 'OrderContinue').Count -eq 0 -and (Get-RiotInvocations 'CMD_ORDER_CONTINUE_FROM_HELD').Count -eq 0 -and
        (Get-Attempts 'triggerEmergency').Count -eq 1 -and $null -ne $faultHeld -and [string]$faultHeld.Level -ne 'None'),
    '0 / 0 / 1 / fault in effect',
    "$((Get-Attempts 'OrderContinue').Count) / $((Get-RiotInvocations 'CMD_ORDER_CONTINUE_FROM_HELD').Count) / $((Get-Attempts 'triggerEmergency').Count) / $(if ($faultHeld) { [string]$faultHeld.Level } else { '-' })")

# --- 6. 人按「继续原单」：CONTINUE 发出、单跑起来、故障清除、车再走 -------------------------------------------------

$journal.Note('A person continues the held order through the fault recovery entry.')
$resumeJob = Start-ThreadJob -ArgumentList $recoveryUri, $Context.FaultRecoveryCredential, $agvId, $Context.RunId -ScriptBlock {
    param($uri, $credential, $agv, $runId)
    $response = Invoke-WebRequest -Method Post -Uri $uri -SkipHttpErrorCheck -TimeoutSec 60 `
        -Headers @{ Authorization = "Bearer $credential" } -ContentType 'application/json' -Body (@{
            agvId = $agv; operatorId = 'L2-OPERATOR-789'; action = 'RESUME_HELD_ORDER'; faultRemedied = $true
            note = "L2 ${runId}: door reading restored"
        } | ConvertTo-Json)
    $text = if ($response.Content -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
    [pscustomobject]@{ Status = [int]$response.StatusCode; Text = $text }
}
try {
    $null = Wait-L2Condition -Description 'the continue reached RIoT' `
        -Journal $journal -Criterion 'continue-sent' -TimeoutSeconds 30 `
        -Probe { (Get-RiotInvocations 'CMD_ORDER_CONTINUE_FROM_HELD').Count } -Until { param($v) $v -ge 1 }
    $journal.Note('RIoT runs the order again (3) and the vehicle drives.')
    $null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3 })
    Set-Vehicle @{ movementState = 'MT_RUNNING'; speed = 0.55 }
} finally {
    $resumed = Receive-Job -Job $resumeJob -Wait -AutoRemoveJob
}
$journal.Note("Fault recovery (resume) -> $($resumed.Status): $($resumed.Text)")
$resumedBody = if ($resumed.Text) { $resumed.Text | ConvertFrom-Json } else { $null }
$faultCleared = Wait-L2ConditionOrLast -Description 'the fault was cleared' `
    -Journal $journal -Criterion 'fault-cleared' -TimeoutSeconds 15 `
    -Probe { Get-Fault } -Until { param($row) $null -ne $row -and [string]$row.Level -eq 'None' }
$continueCalls = Get-RiotInvocations 'CMD_ORDER_CONTINUE_FROM_HELD'
$assertions.Add(
    'L2-DU-06', '继续原单：入口 200 Resumed，CONTINUE 恰好一次打在这一张 orderId 上，故障清除',
    ($resumed.Status -eq 200 -and $null -ne $resumedBody -and [string]$resumedBody.outcome -eq 'Resumed' -and
        $continueCalls.Count -eq 1 -and [string]$continueCalls[0].target -eq $orderId -and
        $null -ne $faultCleared -and [string]$faultCleared.Level -eq 'None'),
    "200 / Resumed / 1 -> $orderId / None",
    "$($resumed.Status) / $(if ($resumedBody) { [string]$resumedBody.outcome } else { '-' }) / $($continueCalls.Count) -> $(if ($continueCalls.Count) { [string]$continueCalls[0].target } else { '-' }) / $(if ($faultCleared) { [string]$faultCleared.Level } else { '-' })")

$null = Wait-L2Iterations -Riot $riot -Count 5 -TimeoutSeconds 60 -Journal $journal
$assertions.Add(
    'L2-DU-07', '续行之后车在走、门锁着：不再急停、不再按住，全程没有 Cancel',
    ((Get-Attempts 'triggerEmergency').Count -eq 1 -and (Get-Attempts 'OrderHold').Count -eq 1 -and
        (Get-RiotInvocations 'CMD_ORDER_CANCEL').Count -eq 0),
    '1 / 1 / 0',
    "$((Get-Attempts 'triggerEmergency').Count) / $((Get-Attempts 'OrderHold').Count) / $((Get-RiotInvocations 'CMD_ORDER_CANCEL').Count)")

$journal.Note('Scenario finished.')
