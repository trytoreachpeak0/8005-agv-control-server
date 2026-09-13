#Requires -Version 7

<#
急停只发一次：车在动、在途单被 RIoT 报成 FAILED、证不出停住，服务端升级发 `triggerEmergency`，而且只发一次。

这是批次 2 票 19（W1 空载急停演练）上现场之前要先在 L2 上证的机制。现场的出口判据是「RIoT 侧确实停车，
且 8005 侧只发一次调用」，数的是 `RiotOrderCommandAudit` 的 attempt 行，不是 RIoT 侧收到的请求数。

**为什么要有这条场景。**2026-09-13 之前，故障协调器每一轮评估都会重新请求停车，而 `RequestStopAsync`
不看退避、也不看这一轮急停是否已经发出：只要回读还没读到闩锁，就再发一次。RIoT 的闩锁在调用之后约一秒
才锁上（Round 19），运行时每一两秒评估一次，所以现场的一次急停会变成两次、三次真实调用。同时负责「确认、
重试、被外部解除就重触发」的 `EvaluateAsync` 没有任何调用方，一次成功的急停在审计表里永远是 Pending。
缺陷单：`docs/defects/20260913-emergency-stop-reissued-every-evaluation-and-never-settled.md`。

**假 RIoT 不替服务端锁闩锁**，这是它一贯的设计：命令只记录，后果由场景照真实 RIoT 的样子摆出来。所以
「闩锁晚一点才锁上」「读不到」「被外部解除」这几种状态，都是场景在控制面上写的，写的时机对着审计表里
已经出现的那一行。

**退避调到 20 秒**（setup 文件）。本装置每秒评估一次，默认 2 秒的退避会让「退避内又请求了一次、正确地
没发」与「退避到期、按 REQ-0248 重试」挤在同一两秒里，场景就分不清服务端做对的是哪一件。退避到期后的
重试由单元测试 `AStopAskedForAgainOnceTheBackoffHasElapsedRetries` 证。
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
$agvId = [string]$Context.AgvId
$vehicleKey = [string]$Context.VehicleKey
$vehicleTarget = "vehicle:$vehicleKey"

function Get-EmergencyAttempts([string]$commandType) {
    # 先赋值再用，不要把这个函数直接送进管道：`Invoke-L2Query` 的 `return , $rows` 包装穿得过一层 return。
    return Invoke-L2Query -Connection $connection -Sql @"
SELECT CommandType, AgvId, TargetUpperId, AttemptNumber, Outcome, FaultGeneration, ReceiptJson
FROM RiotOrderCommandAudit WHERE CommandType = '$commandType' ORDER BY AttemptNumber
"@
}

function Get-HoldAttempts {
    return Invoke-L2Query -Connection $connection `
        -Sql "SELECT AttemptNumber, Outcome FROM RiotOrderCommandAudit WHERE CommandType = 'OrderHold' ORDER BY AttemptNumber"
}

function Get-RiotInvocations([string]$commandType) {
    return @(@($riot.Snapshot().body.commandInvocations) | Where-Object { [string]$_.commandType -eq $commandType })
}

function Set-Vehicle([hashtable]$fields) {
    $body = @{ vehicleKey = $vehicleKey } + $fields
    $null = $riot.Command('Put', 'vehicle', $body)
}

function Assert-StillOneTrigger([string]$id, [string]$phase) {
    $rows = Get-EmergencyAttempts 'triggerEmergency'
    $calls = Get-RiotInvocations 'triggerEmergency'
    $assertions.Add(
        $id,
        "只发一次：$phase，审计表仍只有一行 triggerEmergency，RIoT 侧也只收到一次",
        ($rows.Count -eq 1 -and $calls.Count -eq 1),
        '1 行 / 1 次',
        "$($rows.Count) 行 / $($calls.Count) 次")
}

# --- 1. 一台车上路 ----------------------------------------------------------------------------------

$demandId = [guid]::NewGuid().ToString('N')
$journal.Note("Publishing demand $demandId.")
$null = $mes.Command('Put', "demands/$demandId", @{
    sublot      = "L2-ES-$($Context.RunId)"
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

# --- 2. 车在动，但没有故障：不发急停 ------------------------------------------------------------------

# 「该发时才发」的负半条。少了它，一台只要在动就发急停的服务端也能让后面全部判据变绿。
Set-Vehicle @{ movementState = 'MT_RUNNING'; speed = 0.6 }
$null = Wait-L2Iterations -Riot $riot -Count 3 -TimeoutSeconds 60 -Journal $journal

$before = Get-EmergencyAttempts 'triggerEmergency'
$assertions.Add(
    'L2-ES-01',
    '车在正常行驶、没有任何故障时，一次急停都没发',
    ($before.Count -eq 0 -and (Get-RiotInvocations 'triggerEmergency').Count -eq 0),
    '0 行 / 0 次',
    "$($before.Count) 行 / $((Get-RiotInvocations 'triggerEmergency').Count) 次")

# --- 3. 在途单被报 FAILED，车还在动：升级，发出一次 --------------------------------------------------

$journal.Note("RIoT reports $($intent.UpperId) FAILED while $agvId is still moving.")
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 4 })

$null = Wait-L2Condition -Description 'the server escalated to an emergency stop' `
    -Journal $journal -Criterion 'trigger-issued' -TimeoutSeconds 90 `
    -Probe { (Get-EmergencyAttempts 'triggerEmergency').Count } -Until { param($v) $v -ge 1 }

$first = (Get-EmergencyAttempts 'triggerEmergency')[0]
$assertions.Add(
    'L2-ES-02',
    '该发时发了：单被报 FAILED 且车证不出停住，服务端发出 triggerEmergency，记在这台车名下',
    ([string]$first.AgvId -eq $agvId -and [string]$first.TargetUpperId -eq $vehicleTarget),
    "$agvId / $vehicleTarget",
    "$([string]$first.AgvId) / $([string]$first.TargetUpperId)")

$firstCalls = Get-RiotInvocations 'triggerEmergency'
$assertions.Add(
    'L2-ES-03',
    '线上收到的是 triggerEmergency，打在这台车的 deviceKey 上',
    ($firstCalls.Count -ge 1 -and [string]$firstCalls[0].target -eq $vehicleKey),
    "triggerEmergency @ $vehicleKey",
    $(if ($firstCalls.Count -eq 0) { '(无)' } else { "$($firstCalls.Count) 次，首次打在 $([string]$firstCalls[0].target)" }))

# 回读发生在调用之后立刻，而假 RIoT 的闩锁还是 OK：这一行此刻只能是 Pending，不能被说成已确认。
$assertions.Add(
    'L2-ES-04',
    '回读时闩锁还没锁上，这一行记为 Pending，没有被当成已停住',
    ([string]$first.Outcome -eq 'Pending'),
    'Pending',
    [string]$first.Outcome)

# --- 4. 闩锁还没锁上：车仍在动，服务端每轮都还在请求停车，但不再发 --------------------------------------

$null = Wait-L2Iterations -Riot $riot -Count 4 -TimeoutSeconds 60 -Journal $journal
Assert-StillOneTrigger 'L2-ES-05' '闩锁还没锁上、车仍在动，又评估了 4 轮'

# --- 5. 闩锁读不到：RIoT 的回答里没有 emergencyState，仍不再发 ------------------------------------------

$journal.Note('RIoT stops reporting emergencyState for the vehicle.')
Set-Vehicle @{ emergencyState = '' }
$null = Wait-L2Iterations -Riot $riot -Count 4 -TimeoutSeconds 60 -Journal $journal
Assert-StillOneTrigger 'L2-ES-06' '闩锁读不到（不是 OK，也不是锁上），又评估了 4 轮'

# --- 6. 闩锁锁上：那一次急停被确认，之后仍不再发 ------------------------------------------------------

$journal.Note('The latch engages: RIoT reports CAN_RECOVER.')
Set-Vehicle @{ emergencyState = 'CAN_RECOVER' }
$confirmed = Wait-L2Condition -Description 'the trigger was settled as confirmed' `
    -Journal $journal -Criterion 'trigger-confirmed' -TimeoutSeconds 30 `
    -Probe { [string](Get-EmergencyAttempts 'triggerEmergency')[0].Outcome } `
    -Until { param($v) $v -eq 'Confirmed' }
$assertions.Add(
    'L2-ES-07',
    '闩锁锁上之后，发出它的那一行被改记为 Confirmed，不再永远停在 Pending',
    ($confirmed -eq 'Confirmed'),
    'Confirmed',
    $confirmed)

$null = Wait-L2Iterations -Riot $riot -Count 4 -TimeoutSeconds 60 -Journal $journal
Assert-StillOneTrigger 'L2-ES-08' '闩锁已确认，又评估了 4 轮'

$faults = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Level, EvidenceCode, EscalatedAt FROM VehicleFaultStates WHERE AgvId = '$agvId'"
$assertions.Add(
    'L2-ES-09',
    '故障事实停在 SuspectedBlocked，证据码 VEHICLE_ORDER_FAILED，并记下了升级时刻',
    ($faults.Count -eq 1 -and [string]$faults[0].Level -eq 'SuspectedBlocked' -and
        [string]$faults[0].EvidenceCode -eq 'VEHICLE_ORDER_FAILED' -and $null -ne $faults[0].EscalatedAt -and
        [string]$faults[0].EscalatedAt -ne ''),
    'SuspectedBlocked / VEHICLE_ORDER_FAILED / 有升级时刻',
    $(if ($faults.Count -eq 0) { '(无行)' }
      else { "$([string]$faults[0].Level) / $([string]$faults[0].EvidenceCode) / $([string]$faults[0].EscalatedAt)" }))

$holds = Get-HoldAttempts
$assertions.Add(
    'L2-ES-10',
    'OrderHold 也只发了一次：单已经是 FAILED，重发改变不了什么',
    ($holds.Count -eq 1),
    1,
    $holds.Count)

# --- 7. 车停下来：不再需要升级，也不解除，因为原因还在 ----------------------------------------------------

$journal.Note('The vehicle comes to rest at its current station.')
Set-Vehicle @{ movementState = 'MT_PAUSED'; speed = 0 }
$null = Wait-L2Iterations -Riot $riot -Count 5 -TimeoutSeconds 60 -Journal $journal
Assert-StillOneTrigger 'L2-ES-11' '车停下来不再升级，又评估了 5 轮'

$releases = Get-EmergencyAttempts 'cancelEmergency'
$assertions.Add(
    'L2-ES-12',
    '恢复严：故障原因（单 FAILED）还在，服务端一次 cancelEmergency 都没发',
    ($releases.Count -eq 0 -and (Get-RiotInvocations 'cancelEmergency').Count -eq 0),
    '0 行 / 0 次',
    "$($releases.Count) 行 / $((Get-RiotInvocations 'cancelEmergency').Count) 次")

# --- 8. 闩锁被外部解除、原因还在：立即重触发，并且只重触发一次 --------------------------------------------

# 这一段只有「故障协调器在车不再升级时仍驱动急停的评估」才走得到：车已经停着，没有任何评估会再请求停车。
$journal.Note('Something outside the server releases the latch while the cause still stands.')
Set-Vehicle @{ emergencyState = 'OK' }
$null = Wait-L2Condition -Description 'the externally released latch was re-triggered' `
    -Journal $journal -Criterion 'retrigger-issued' -TimeoutSeconds 30 `
    -Probe { (Get-EmergencyAttempts 'triggerEmergency').Count } -Until { param($v) $v -ge 2 }

$second = (Get-EmergencyAttempts 'triggerEmergency')[1]
$assertions.Add(
    'L2-ES-13',
    '原因消除前闩锁意外恢复 OK：服务端重触发，第二行的原因写的是 EMERGENCY_LATCH_RELEASED_EXTERNALLY（REQ-0248）',
    ([int]$second.AttemptNumber -eq 2 -and [string]$second.ReceiptJson -like '*EMERGENCY_LATCH_RELEASED_EXTERNALLY*'),
    'AttemptNumber 2 / EMERGENCY_LATCH_RELEASED_EXTERNALLY',
    "AttemptNumber $([int]$second.AttemptNumber) / $([string]$second.ReceiptJson)")

$journal.Note('The latch engages again.')
Set-Vehicle @{ emergencyState = 'CAN_RECOVER' }
$null = Wait-L2Condition -Description 'the re-trigger was settled as confirmed' `
    -Journal $journal -Criterion 'retrigger-confirmed' -TimeoutSeconds 30 `
    -Probe { [string](Get-EmergencyAttempts 'triggerEmergency')[1].Outcome } `
    -Until { param($v) $v -eq 'Confirmed' }
$null = Wait-L2Iterations -Riot $riot -Count 4 -TimeoutSeconds 60 -Journal $journal

$finalRows = Get-EmergencyAttempts 'triggerEmergency'
$finalCalls = Get-RiotInvocations 'triggerEmergency'
$assertions.Add(
    'L2-ES-14',
    '全程两次 triggerEmergency：一次升级、一次外部解除后的重触发；两行都已确认，RIoT 侧计数一致，没有 cancelEmergency',
    ($finalRows.Count -eq 2 -and $finalCalls.Count -eq 2 -and
        @($finalRows | Where-Object { [string]$_.Outcome -eq 'Confirmed' }).Count -eq 2 -and
        (Get-RiotInvocations 'cancelEmergency').Count -eq 0),
    '2 行（均 Confirmed） / 2 次 / 0 次 cancelEmergency',
    "$($finalRows.Count) 行（Confirmed $(@($finalRows | Where-Object { [string]$_.Outcome -eq 'Confirmed' }).Count)） / $($finalCalls.Count) 次 / $((Get-RiotInvocations 'cancelEmergency').Count) 次 cancelEmergency")

$journal.Note('急停：该发时发了一次；闩锁晚锁、读不到、锁上都不重发；外部解除只重触发一次；原因还在就不解除。')
