#Requires -Version 7

<#
CP-0011 判定表第 4 行（control-server#539 审查 H-1 的变体）：车在路上、车载端报门没锁时，有人按着车上的急停按钮
（RIoT 报 `CAN_NOT_RECOVER`）。服务端在这个闩锁上只记自己的一类、不调用 RIoT。门锁修好读回锁闭、按钮松开（`CAN_RECOVER`）、
有人用二类入口解开（`OK`）：本服务端这一类还在，立即重下一次。之后人经有货解除入口解除——这次急停记着门锁原因，所以没确认门锁已检修
会被拒；确认了就解除，闩锁读到 OK 之后不再重下。

**为什么要有这条场景。**修前这条路卡死：按钮按着时故障证据码变成 `RIOT_EMERGENCY_CAN_NOT_RECOVER`，门锁读回锁闭后门锁那一路认不出
这是门锁故障、不再调故障模型；急停监看只挂在那一路上，二类入口解开后没有重下，开着的急停又挡住续行和清除，三类入口要
`CAN_RECOVER` 而闩锁已是 `OK`。审查探针 P1 实测过。

**假 RIoT 不替服务端改状态**：按钮、闩锁锁上与放开、单停住，都由场景照真实 RIoT（riot-behavior-lab Round 27-29、BC-ORDER-006）
的样子在控制面上摆出来。
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
$loadedReleaseUri = "http://127.0.0.1:$($Context.HealthPort)/api/safety/v1/emergency-stop-releases/loaded"
if (-not $Context.EmergencyLoadedReleaseCredential) {
    throw 'The setup file must turn EmergencyStopRelease on; without it nobody can release the stop and this scenario proves nothing.'
}

function Get-Attempts([string]$commandType) {
    # 调用方先赋值再用，不要把这个函数直接送进管道：`Invoke-L2Query` 的 `return , $rows` 包装穿得过一层 return。
    return Invoke-L2Query -Connection $connection -Sql @"
SELECT CommandType, AgvId, TargetUpperId, AttemptNumber, Outcome, FaultGeneration, ReceiptJson
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
    # Typed on purpose: an `if` expression unrolls a one-element array to its string, the control plane then gets
    # "reasonCodes":"..." and refuses the binding with an empty 400 (in-transit-door-not-locked's first local run).
    [string[]]$reasons = if ($locked) { @('ACTION_NOT_ALLOWED_IN_STATE') } else { @('LOCK_NOT_CLOSED', 'ACTION_NOT_ALLOWED_IN_STATE') }
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

function Invoke-LoadedRelease([hashtable]$overrides) {
    $body = @{
        agvId            = $agvId
        operatorId       = 'L2-ADMIN-13'
        reason           = "L2 $($Context.RunId): door lock repaired on site"
        causeCleared     = $true
        allDoorsClosed   = $true
        doorLockRepaired = $true
    }
    foreach ($key in $overrides.Keys) { $body[$key] = $overrides[$key] }
    $response = Invoke-WebRequest -Method Post -Uri $loadedReleaseUri -SkipHttpErrorCheck -TimeoutSec 30 `
        -Headers @{ Authorization = "Bearer $($Context.EmergencyLoadedReleaseCredential)" } `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json)
    # A refusal is application/problem+json, which Invoke-WebRequest does not treat as text: Content arrives as bytes.
    $text = if ($response.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($response.Content)
    } else {
        [string]$response.Content
    }
    $journal.Note("Loaded release request ($(($overrides.Keys | Sort-Object) -join ',')) -> $([int]$response.StatusCode): $text")
    return [pscustomobject]@{
        Status = [int]$response.StatusCode
        Body   = if ($text) { $text | ConvertFrom-Json } else { $null }
    }
}

# --- 1. 一台车上路 ----------------------------------------------------------------------------------

$demandId = [guid]::NewGuid().ToString('N')
$journal.Note("Publishing demand $demandId.")
$null = $mes.Command('Put', "demands/$demandId", @{
    sublot      = "L2-DB-$($Context.RunId)"
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

# --- 2. 按钮按着时门报没锁：只记不发 ---------------------------------------------------------------------

$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $vehicleKey })
Set-Vehicle @{ movementState = 'MT_RUNNING'; speed = 0.6; processingOrder = $true; currentPosition = 0 }
$journal.Note('Somebody holds the emergency-stop button (CAN_NOT_RECOVER); the onboard reports a door not locked.')
Set-Vehicle @{ emergencyState = 'CAN_NOT_RECOVER' }
Set-Doors -locked $false
$recorded = Wait-L2ConditionOrLast -Description "the server recorded its class on the held button's latch" `
    -Journal $journal -Criterion 'class-recorded' -TimeoutSeconds 90 `
    -Probe { (Get-Attempts 'emergencyLatchRecorded').Count } -Until { param($v) $v -ge 1 }
$null = Wait-L2Iterations -Riot $riot -Count 3 -TimeoutSeconds 60 -Journal $journal
$faults = Invoke-L2Query -Connection $connection -Sql "SELECT Level, EvidenceCode FROM VehicleFaultStates WHERE AgvId = '$agvId'"
$assertions.Add(
    'L2-DB-01', '按钮按着时只记不发：一行 emergencyLatchRecorded、0 次 triggerEmergency；故障证据码被按钮盖成 RIOT_EMERGENCY_CAN_NOT_RECOVER',
    ([int]$recorded -eq 1 -and (Get-Attempts 'triggerEmergency').Count -eq 0 -and (Get-RiotInvocations 'triggerEmergency').Count -eq 0 -and
        $faults.Count -eq 1 -and [string]$faults[0].EvidenceCode -eq 'RIOT_EMERGENCY_CAN_NOT_RECOVER'),
    '1 / 0 / 0 / RIOT_EMERGENCY_CAN_NOT_RECOVER',
    "$([int]$recorded) / $((Get-Attempts 'triggerEmergency').Count) / $((Get-RiotInvocations 'triggerEmergency').Count) / $(if ($faults.Count) { [string]$faults[0].EvidenceCode } else { '-' })")

# --- 3. 单停住、门锁修好读回锁闭、按钮松开：仍不发 -------------------------------------------------------------

$journal.Note('RIoT parks the order; the door lock is repaired and reads locked; the button is released (CAN_RECOVER).')
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 7 })
Set-Vehicle @{ movementState = 'MT_PAUSED'; speed = 0 }
Set-Doors -locked $true
$null = Wait-L2Iterations -Riot $riot -Count 3 -TimeoutSeconds 60 -Journal $journal
Set-Vehicle @{ emergencyState = 'CAN_RECOVER' }
$null = Wait-L2Iterations -Riot $riot -Count 3 -TimeoutSeconds 60 -Journal $journal
$assertions.Add(
    'L2-DB-02', '按钮松开、门锁读回锁闭：服务端既不触发也不解除',
    ((Get-RiotInvocations 'triggerEmergency').Count -eq 0 -and (Get-RiotInvocations 'cancelEmergency').Count -eq 0),
    '0 / 0', "$((Get-RiotInvocations 'triggerEmergency').Count) / $((Get-RiotInvocations 'cancelEmergency').Count)")

# --- 4. 有人用二类入口解开：本服务端这一类还在，重下一次 -----------------------------------------------------------

$journal.Note('Somebody releases the latch at a class-two entry (OK) while this server''s class stands.')
Set-Vehicle @{ emergencyState = 'OK' }
$null = Wait-L2Condition -Description 'the server re-triggered its stop' `
    -Journal $journal -Criterion 'retrigger-issued' -TimeoutSeconds 30 `
    -Probe { (Get-Attempts 'triggerEmergency').Count } -Until { param($v) $v -ge 1 }
$retrigger = Wait-L2ConditionOrLast -Description 'the re-trigger was settled' `
    -Journal $journal -Criterion 'retrigger-settled' -TimeoutSeconds 30 `
    -Probe { $rows = Get-Attempts 'triggerEmergency'; if ($rows.Count -gt 0) { $rows[0] } else { $null } } `
    -Until { param($row) -not [string]::IsNullOrEmpty([string]$row.ReceiptJson) }
$journal.Note('The latch engages again.')
Set-Vehicle @{ emergencyState = 'CAN_RECOVER' }
$null = Wait-L2Iterations -Riot $riot -Count 3 -TimeoutSeconds 60 -Journal $journal
$assertions.Add(
    'L2-DB-03', '二类入口解开后重下恰好一次，原因是 EMERGENCY_LATCH_RELEASED_EXTERNALLY（REQ-0248 按 CP-0011 修订）',
    ((Get-RiotInvocations 'triggerEmergency').Count -eq 1 -and [string]$retrigger.ReceiptJson -like '*EMERGENCY_LATCH_RELEASED_EXTERNALLY*'),
    '1 / EMERGENCY_LATCH_RELEASED_EXTERNALLY',
    "$((Get-RiotInvocations 'triggerEmergency').Count) / $([string]$retrigger.ReceiptJson)")

# --- 5. 人经有货解除入口解除：门锁原因记在这次急停上，没确认检修就拒 ---------------------------------------------------

$unrepaired = Invoke-LoadedRelease @{ doorLockRepaired = $false }
$unrepairedReasons = if ($unrepaired.Body -and $unrepaired.Body.PSObject.Properties['reasons']) { @($unrepaired.Body.reasons) } else { @() }
$assertions.Add(
    'L2-DB-04', '故障证据码已被按钮盖掉，入口仍认得这是门锁原因：没确认门锁已检修回 409（EMERGENCY_DOOR_LOCK_REPAIR_NOT_CONFIRMED）',
    ($unrepaired.Status -eq 409 -and $unrepairedReasons -contains 'EMERGENCY_DOOR_LOCK_REPAIR_NOT_CONFIRMED' -and
        (Get-RiotInvocations 'cancelEmergency').Count -eq 0),
    '409 / EMERGENCY_DOOR_LOCK_REPAIR_NOT_CONFIRMED / 0',
    "$($unrepaired.Status) / $($unrepairedReasons -join ',') / $((Get-RiotInvocations 'cancelEmergency').Count)")

$released = Invoke-LoadedRelease @{}
$null = Wait-L2Condition -Description 'the server released its stop' `
    -Journal $journal -Criterion 'release-issued' -TimeoutSeconds 30 `
    -Probe { (Get-RiotInvocations 'cancelEmergency').Count } -Until { param($v) $v -ge 1 }
Set-Vehicle @{ emergencyState = 'OK' }
$null = Wait-L2Iterations -Riot $riot -Count 5 -TimeoutSeconds 60 -Journal $journal
$assertions.Add(
    'L2-DB-05', '确认门锁已检修后解除一次（200 或 202）；闩锁读到 OK 之后不再重下（仍 1 次 triggerEmergency、1 次 cancelEmergency）',
    ($released.Status -in 200, 202 -and (Get-RiotInvocations 'triggerEmergency').Count -eq 1 -and
        (Get-RiotInvocations 'cancelEmergency').Count -eq 1),
    '200|202 / 1 / 1',
    "$($released.Status) / $((Get-RiotInvocations 'triggerEmergency').Count) / $((Get-RiotInvocations 'cancelEmergency').Count)")

$journal.Note('Scenario finished; the held order waits for a person.')
