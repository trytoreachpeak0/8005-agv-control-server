#Requires -Version 7

<#
control-server#335（REQ-0246）：车在路上，车载端报门没锁。服务端对这辆车、这一张单发 `OrderHold`；按不住、车还在动，
升级发 `triggerEmergency`，而且只发一次；从不 Cancel。之后 RIoT 把单停成 PAUSED、闩锁锁上，车载端报门锁恢复：
服务端自动 `cancelEmergency`（用户 2026-09-28 选的 A），单仍停着，没有任何 CONTINUE。

**判据断「发给了正确的那一个」**，不只断「发过一次」：Hold 的审计行与假 RIoT 收到的 CMD_ORDER_HELD 都要打在本车本单上，
急停打在本车上。

**假 RIoT 不替服务端改状态**，这是它一贯的设计（`FakeRiotState.FakeCommandInvocation` 的注释）：Hold 不会让单变 PAUSED，
急停不会锁闩锁，解除不会放开。所以「按不住」是这里的默认；「单已 PAUSED」「闩锁锁上」「闩锁放开」都由场景照真实 RIoT
（riot-behavior-lab BC-ORDER-006、BC-VEH-005）的样子在控制面上摆出来，摆的时机对着审计表里已经出现的那一行。

**失联不在这里。**车载端不说话时本票不下命令（用户 2026-09-20 把失联时的 Hold 留到批次 9），这条场景只让车载端明报。

取红证据：把门锁从故障模型的输入里拿掉（`JourneyRuntimeEngine.ObserveDoorsInTransitAsync` 直接返回 false），L2-DL-02 起全红。
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
$doorSymptom = 'VEHICLE_DOOR_NOT_PROVEN_LOCKED'

function Get-Attempts([string]$commandType) {
    # 调用方先赋值再用，不要把这个函数直接送进管道：`Invoke-L2Query` 的 `return , $rows` 包装穿得过一层 return。
    return Invoke-L2Query -Connection $connection -Sql @"
SELECT CommandType, AgvId, TargetUpperId, TargetOrderId, AttemptNumber, IssuedAt, Outcome, FaultGeneration, ReceiptJson
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

function Set-Doors([bool]$locked) {
    $reasons = if ($locked) { @('ACTION_NOT_ALLOWED_IN_STATE') } else { @('LOCK_NOT_CLOSED', 'ACTION_NOT_ALLOWED_IN_STATE') }
    $null = Set-L2OnboardSafety -Onboard $onboard -Connection $connection -AgvId $agvId -Journal $journal -Safety @{
        departureSafe         = $false
        vehicleStopped        = $false
        allTargetSlotsLocked  = $locked
        allUnlockOutputsReset = $true
        unknownPresent        = $false
        reasonCodes           = $reasons
        affectedSlots         = @(1, 2, 3, 4, 5, 6, 7, 8)
    }
}

function Get-Instant($value) {
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

# --- 1. 一台车上路 ----------------------------------------------------------------------------------

$demandId = [guid]::NewGuid().ToString('N')
$journal.Note("Publishing demand $demandId.")
$null = $mes.Command('Put', "demands/$demandId", @{
    sublot      = "L2-DL-$($Context.RunId)"
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

# --- 2. 车在动、门锁着：什么都不发 --------------------------------------------------------------------

# 「该发时才发」的负半条。少了它，一台只要在动就按住的服务端也能让后面全部判据变绿。
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $vehicleKey })
Set-Vehicle @{ movementState = 'MT_RUNNING'; speed = 0.6; processingOrder = $true }
$null = Wait-L2Iterations -Riot $riot -Count 3 -TimeoutSeconds 60 -Journal $journal
$holdsBefore = Get-Attempts 'OrderHold'
$triggersBefore = Get-Attempts 'triggerEmergency'
$assertions.Add(
    'L2-DL-01', '车在正常行驶、门锁着时，不发 Hold、不发急停',
    ($holdsBefore.Count -eq 0 -and $triggersBefore.Count -eq 0),
    '0 / 0', "$($holdsBefore.Count) / $($triggersBefore.Count)")

# --- 3. 车载端报门没锁：按住本单，按不住、车还在动，急停一次 ----------------------------------------------

$journal.Note("The onboard reports a door not locked while $agvId drives on $upperId.")
Set-Doors -locked $false

$null = Wait-L2Condition -Description 'the server issued the emergency stop' `
    -Journal $journal -Criterion 'trigger-issued' -TimeoutSeconds 90 `
    -Probe { (Get-Attempts 'triggerEmergency').Count } -Until { param($v) $v -ge 1 }
# 审计行先武装再调 RIoT、回读后才写 ReceiptJson，三步各自提交：等结算落库再读（control-server#193 普查）。
$trigger = Wait-L2ConditionOrLast -Description 'the trigger attempt was settled' `
    -Journal $journal -Criterion 'trigger-settled' -TimeoutSeconds 30 `
    -Probe { $rows = Get-Attempts 'triggerEmergency'; if ($rows.Count -gt 0) { $rows[0] } else { $null } } `
    -Until { param($row) -not [string]::IsNullOrEmpty([string]$row.ReceiptJson) }
$holds = Get-Attempts 'OrderHold'
$hold = if ($holds.Count -gt 0) { $holds[0] } else { $null }

$assertions.Add(
    'L2-DL-02', 'Hold 恰好一条，打在本车、本单上，挂在这一代故障下',
    ($holds.Count -eq 1 -and [string]$hold.AgvId -eq $agvId -and [string]$hold.TargetUpperId -eq $upperId -and
        $null -ne $hold.FaultGeneration),
    "1 / $agvId / $upperId",
    $(if ($hold) { "$($holds.Count) / $([string]$hold.AgvId) / $([string]$hold.TargetUpperId)" } else { '0 / - / -' }))

$heldCalls = Get-RiotInvocations 'CMD_ORDER_HELD'
$assertions.Add(
    'L2-DL-03', 'RIoT 侧收到的是 CMD_ORDER_HELD，打在这一张 orderId 上',
    ($heldCalls.Count -ge 1 -and @($heldCalls | Where-Object { [string]$_.target -ne $orderId }).Count -eq 0),
    "CMD_ORDER_HELD -> $orderId",
    ((@($heldCalls) | ForEach-Object { "$($_.commandType) -> $($_.target)" }) -join '; '))

$triggers = Get-Attempts 'triggerEmergency'
$assertions.Add(
    'L2-DL-04', '急停恰好一条，打在这台车上，而且在 Hold 之后',
    ($triggers.Count -eq 1 -and [string]$trigger.AgvId -eq $agvId -and [string]$trigger.TargetUpperId -eq $vehicleTarget -and
        $null -ne $hold -and (Get-Instant $hold.IssuedAt) -le (Get-Instant $trigger.IssuedAt)),
    "1 / $agvId / $vehicleTarget / hold first",
    "$($triggers.Count) / $([string]$trigger.AgvId) / $([string]$trigger.TargetUpperId) / hold $(if ($hold) { [string]$hold.IssuedAt } else { '-' }) trigger $([string]$trigger.IssuedAt)")

$stage = Invoke-L2Query -Connection $connection -Sql "SELECT BlockReasonCode FROM JourneyRuntimes WHERE AgvId = '$agvId'"
$faults = Invoke-L2Query -Connection $connection -Sql "SELECT Level, EvidenceCode FROM VehicleFaultStates WHERE AgvId = '$agvId'"
$assertions.Add(
    'L2-DL-05', '故障事实记的是门锁症状，旅程码也是它',
    ($faults.Count -eq 1 -and [string]$faults[0].EvidenceCode -eq $doorSymptom -and [string]$stage[0].BlockReasonCode -eq $doorSymptom),
    "$doorSymptom / $doorSymptom",
    "$(if ($faults.Count -gt 0) { [string]$faults[0].EvidenceCode } else { '-' }) / $([string]$stage[0].BlockReasonCode)")

# 按不住、闩锁还没锁：在退避之内不再发第二次。
$null = Wait-L2Iterations -Riot $riot -Count 4 -TimeoutSeconds 60 -Journal $journal
$triggersLater = Get-Attempts 'triggerEmergency'
$assertions.Add(
    'L2-DL-06', '按不住时急停只发一次：再跑几轮，审计表与 RIoT 侧都仍是一次',
    ($triggersLater.Count -eq 1 -and (Get-RiotInvocations 'triggerEmergency').Count -eq 1),
    '1 / 1', "$($triggersLater.Count) / $((Get-RiotInvocations 'triggerEmergency').Count)")

# --- 4. RIoT 把单停住、闩锁锁上；门还没锁：不解除 --------------------------------------------------------

$journal.Note('RIoT parks the order (PAUSED 7) and latches the emergency stop; the doors are still reported open.')
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 7 })
Set-Vehicle @{ emergencyState = 'CAN_RECOVER'; movementState = 'MT_PAUSED'; speed = 0 }
$null = Wait-L2Iterations -Riot $riot -Count 4 -TimeoutSeconds 60 -Journal $journal
$releasesEarly = Get-Attempts 'cancelEmergency'
$assertions.Add(
    'L2-DL-07', '门还报没锁时，急停不解除',
    ($releasesEarly.Count -eq 0), 0, $releasesEarly.Count)

# --- 5. 车载端报门锁恢复：自动解除，单仍停着，没有 CONTINUE ------------------------------------------------

$journal.Note('The onboard reports the doors locked again.')
Set-Doors -locked $true
$null = Wait-L2Condition -Description 'the server released the emergency stop' `
    -Journal $journal -Criterion 'release-issued' -TimeoutSeconds 60 `
    -Probe { (Get-Attempts 'cancelEmergency').Count } -Until { param($v) $v -ge 1 }
$release = Wait-L2ConditionOrLast -Description 'the release attempt was settled' `
    -Journal $journal -Criterion 'release-settled' -TimeoutSeconds 30 `
    -Probe { $rows = Get-Attempts 'cancelEmergency'; if ($rows.Count -gt 0) { $rows[0] } else { $null } } `
    -Until { param($row) -not [string]::IsNullOrEmpty([string]$row.ReceiptJson) }
$releaseReason = ([string]$release.ReceiptJson | ConvertFrom-Json).Reason
$holdsNow = Get-Attempts 'OrderHold'
$assertions.Add(
    'L2-DL-08', '门锁恢复后自动解除一次，原因记的是门锁原因消除，本单的 Hold 已回查确认',
    ((Get-Attempts 'cancelEmergency').Count -eq 1 -and $releaseReason -eq 'EMERGENCY_DOOR_CAUSE_REMOVED' -and
        $holdsNow.Count -eq 1 -and [string]$holdsNow[0].Outcome -eq 'Confirmed'),
    '1 / EMERGENCY_DOOR_CAUSE_REMOVED / Confirmed',
    "$((Get-Attempts 'cancelEmergency').Count) / $releaseReason / $(if ($holdsNow.Count -gt 0) { [string]$holdsNow[0].Outcome } else { '-' })")

$journal.Note('RIoT clears the latch.')
Set-Vehicle @{ emergencyState = 'OK' }
$null = Wait-L2Iterations -Riot $riot -Count 5 -TimeoutSeconds 60 -Journal $journal

$continues = Get-Attempts 'OrderContinue'
$assertions.Add(
    'L2-DL-09', '解除之后单仍停着：没有任何 CONTINUE，审计表与 RIoT 侧都没有',
    ($continues.Count -eq 0 -and (Get-RiotInvocations 'CMD_ORDER_CONTINUE_FROM_HELD').Count -eq 0),
    '0 / 0', "$($continues.Count) / $((Get-RiotInvocations 'CMD_ORDER_CONTINUE_FROM_HELD').Count)")

$triggersAfter = Get-Attempts 'triggerEmergency'
$faultsAfter = Invoke-L2Query -Connection $connection -Sql "SELECT Level, EvidenceCode FROM VehicleFaultStates WHERE AgvId = '$agvId'"
$assertions.Add(
    'L2-DL-10', '解除之后不再急停，故障仍在效（等人继续原单）',
    ($triggersAfter.Count -eq 1 -and $faultsAfter.Count -eq 1 -and [string]$faultsAfter[0].Level -ne 'None'),
    '1 trigger / fault in effect',
    "$($triggersAfter.Count) trigger / $(if ($faultsAfter.Count -gt 0) { [string]$faultsAfter[0].Level } else { '-' })")

$cancels = Get-Attempts 'CANCEL'
$assertions.Add(
    'L2-DL-11', '全程没有任何 Cancel 代替停车（REQ-0246）',
    ($cancels.Count -eq 0 -and (Get-RiotInvocations 'CMD_ORDER_CANCEL').Count -eq 0),
    '0 / 0', "$($cancels.Count) / $((Get-RiotInvocations 'CMD_ORDER_CANCEL').Count)")

$journal.Note('Scenario finished.')
