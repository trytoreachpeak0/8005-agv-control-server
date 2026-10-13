#Requires -Version 7

<#
车辆故障人工清除的门锁那一种（CP-0011，control-server#539 审查 S-4 把 vehicle-fault-operator-clearance 拆成 FAILED 与门锁两条）：
空车开往取货站，车载端报门没锁，服务端按住、按不住再急停，锁住。值班工程师在 RIoT 上结束那张单，门锁修好读回锁闭——服务端不自动解除。
人经空车解除入口解除，这辆车是因为门锁停下的，所以要确认门锁已检修；之后经故障清除入口清除门锁故障（处置 NONE），
服务端按「单在 RIoT 被取消」同车同需求重建，车接着走。

FAILED 那一种还是 vehicle-fault-operator-clearance：订单 FAILED 不急停，清除与重建那一半不变。

**假 RIoT 不替服务端锁、解急停**，命令只记录，后果由场景照真实 RIoT 的样子摆出来。
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
$releaseUri = "http://127.0.0.1:$($Context.HealthPort)/api/safety/v1/emergency-stop-releases"
$recoveryUri = "http://127.0.0.1:$($Context.HealthPort)/api/safety/v1/vehicle-fault-recoveries"
if (-not $Context.FaultRecoveryCredential -or -not $Context.EmergencyReleaseCredential) {
    throw 'The setup file must turn VehicleFaultRecovery and EmergencyStopRelease on; without them this scenario proves nothing.'
}

function Get-Attempts([string]$commandType) {
    # 先赋值再用，不要把这个函数直接送进管道：`Invoke-L2Query` 的 `return , $rows` 包装穿得过一层 return。
    return Invoke-L2Query -Connection $connection -Sql @"
SELECT CommandType, AttemptNumber, Outcome, FaultGeneration, ReceiptJson
FROM RiotOrderCommandAudit WHERE CommandType = '$commandType' ORDER BY AttemptNumber
"@
}

function Get-RiotInvocations([string]$commandType) {
    # `return , @(...)`：只有一个元素时不被管道拆开，严格模式下对它取 `.Count` 才不会抛错。
    return , @(@($riot.Snapshot().body.commandInvocations) | Where-Object { [string]$_.commandType -eq $commandType })
}

function Set-Vehicle([hashtable]$fields) {
    $body = @{ vehicleKey = $vehicleKey } + $fields
    $null = $riot.Command('Put', 'vehicle', $body)
}

function Set-Doors([bool]$locked) {
    # Typed on purpose: an `if` expression unrolls a one-element array to its string (in-transit-door-not-locked's first run).
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

function Invoke-Json([string]$uri, [string]$credential, [hashtable]$body, [string]$label) {
    $response = Invoke-WebRequest -Method Post -Uri $uri -SkipHttpErrorCheck -TimeoutSec 60 `
        -Headers @{ Authorization = "Bearer $credential" } `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json)
    # A refusal is application/problem+json, which Invoke-WebRequest does not treat as text: Content arrives as bytes.
    $text = if ($response.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($response.Content)
    } else {
        [string]$response.Content
    }
    $journal.Note("$label -> $([int]$response.StatusCode): $text")
    return [pscustomobject]@{
        Status = [int]$response.StatusCode
        Body   = if ($text) { $text | ConvertFrom-Json } else { $null }
    }
}

function Get-Reasons([object]$response) {
    if ($null -eq $response.Body -or -not $response.Body.PSObject.Properties['reasons']) { return , @() }
    return , @($response.Body.reasons | ForEach-Object { [string]$_ })
}

function Invoke-Release([hashtable]$overrides) {
    $body = @{
        agvId = $agvId; operatorId = 'L2-OPERATOR-07'; causeCleared = $true; vehicleEmpty = $true; allDoorsClosed = $true
        doorLockRepaired = $true; note = "L2 $($Context.RunId)"
    }
    foreach ($key in $overrides.Keys) { $body[$key] = $overrides[$key] }
    return Invoke-Json $releaseUri $Context.EmergencyReleaseCredential $body "Emergency release ($(($overrides.Keys | Sort-Object) -join ','))"
}

# --- 1. 空车开往取货站，门报没锁：按住、急停、锁住 --------------------------------------------------------------

$demandId = [guid]::NewGuid().ToString('N')
$journal.Note("Publishing demand $demandId.")
$null = $mes.Command('Put', "demands/$demandId", @{
    sublot      = "L2-VFCD-$($Context.RunId)"
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$journey = Wait-L2Condition -Description 'the vehicle took a journey' `
    -Journal $journal -Criterion 'journey-dispatched' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql "SELECT JourneyId, DemandId FROM JourneyRuntimes WHERE AgvId = '$agvId'"
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

$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $vehicleKey })
Set-Vehicle @{ movementState = 'MT_RUNNING'; speed = 0.6; processingOrder = $true; currentPosition = 0 }
$journal.Note("The onboard reports a door not locked while $agvId drives empty on $upperId.")
Set-Doors -locked $false
$null = Wait-L2Condition -Description 'the server issued the emergency stop' `
    -Journal $journal -Criterion 'trigger-issued' -TimeoutSeconds 90 `
    -Probe { (Get-Attempts 'triggerEmergency').Count } -Until { param($v) $v -ge 1 }
Set-Vehicle @{ emergencyState = 'CAN_RECOVER'; movementState = 'MT_PAUSED'; speed = 0 }
$null = Wait-L2Condition -Description 'the trigger was settled as confirmed' `
    -Journal $journal -Criterion 'trigger-confirmed' -TimeoutSeconds 30 `
    -Probe { [string](Get-Attempts 'triggerEmergency')[0].Outcome } `
    -Until { param($v) $v -eq 'Confirmed' }

# --- 2. 值班工程师在 RIoT 上结束那张单；门锁修好读回锁闭：不自动解除 ---------------------------------------------

$journal.Note('The duty engineer ends the order in RIoT; the door lock is repaired and the onboard reports the vehicle safe.')
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 2 })
# Repaired and standing still, the onboard reports what a safe vehicle reports: departure safe, stopped, every slot locked.
# Left at the in-transit summary (departure not safe), the session stays RecoveryRequired / DEPARTURE_SAFETY_NOT_READY and the
# rebuild below waits on it for good -- the first local run (c44ab881) went red on exactly that, with ONBOARD_SESSION_NOT_READY.
[string[]]$noReasons = @()
$null = Set-L2OnboardSafety -Onboard $onboard -Connection $connection -AgvId $agvId -Journal $journal -Safety @{
    departureSafe         = $true
    vehicleStopped        = $true
    allTargetSlotsLocked  = $true
    allUnlockOutputsReset = $true
    unknownPresent        = $false
    reasonCodes           = $noReasons
    affectedSlots         = @(1, 2, 3, 4, 5, 6, 7, 8)
}
$null = Wait-L2Iterations -Riot $riot -Count 5 -TimeoutSeconds 60 -Journal $journal
$assertions.Add(
    'L2-VFCD-01', '门锁读回锁闭、车上没有未结束的单，服务端也不自动解除（CP-0011）',
    ((Get-RiotInvocations 'cancelEmergency').Count -eq 0), 0, (Get-RiotInvocations 'cancelEmergency').Count)

# --- 3. 空车解除入口：门锁原因要确认门锁已检修 --------------------------------------------------------------------

$unrepaired = Invoke-Release @{ doorLockRepaired = $false }
$assertions.Add(
    'L2-VFCD-02', '空车解除入口：这辆车是因为门锁停下的，没确认门锁已检修回 409（EMERGENCY_DOOR_LOCK_REPAIR_NOT_CONFIRMED），不发 cancelEmergency',
    ($unrepaired.Status -eq 409 -and (Get-Reasons $unrepaired) -contains 'EMERGENCY_DOOR_LOCK_REPAIR_NOT_CONFIRMED' -and
        (Get-RiotInvocations 'cancelEmergency').Count -eq 0),
    '409 / EMERGENCY_DOOR_LOCK_REPAIR_NOT_CONFIRMED / 0',
    "$($unrepaired.Status) / $((Get-Reasons $unrepaired) -join ',') / $((Get-RiotInvocations 'cancelEmergency').Count)")

$released = Invoke-Release @{}
$journal.Note('RIoT clears the latch; the vehicle stands idle at its station.')
Set-Vehicle @{
    emergencyState = 'OK'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.GateStationRiotId; processingOrder = $false; clearOrderTaskId = $true
}
$settled = Wait-L2ConditionOrLast -Description 'the release was settled as confirmed' `
    -Journal $journal -Criterion 'release-confirmed' -TimeoutSeconds 30 `
    -Probe { $rows = Get-Attempts 'cancelEmergency'; if ($rows.Count -gt 0) { [string]$rows[0].Outcome } else { $null } } `
    -Until { param($v) $v -eq 'Confirmed' }
$receipt = $(if ((Get-Attempts 'cancelEmergency').Count -gt 0) { [string](Get-Attempts 'cancelEmergency')[0].ReceiptJson } else { '' })
$assertions.Add(
    'L2-VFCD-03', '确认门锁已检修后解除：202 或 200，解除结算为 Confirmed，回执记下 doorLockRepaired=true',
    ($released.Status -in 200, 202 -and $settled -eq 'Confirmed' -and $receipt -like '*"doorLockRepaired":true*'),
    '200|202 / Confirmed / doorLockRepaired true',
    "$($released.Status) / $settled / $receipt")

# --- 4. 清除门锁故障，同车同需求重建 ------------------------------------------------------------------------------

$cleared = Invoke-Json $recoveryUri $Context.FaultRecoveryCredential @{
    agvId = $agvId; operatorId = 'L2-OPERATOR-07'; action = 'CLEAR_FAULT'; faultRemedied = $true; note = "L2 $($Context.RunId)"
} 'Fault recovery (clear)'
$fault = Invoke-L2Query -Connection $connection -Sql "SELECT Level FROM VehicleFaultStates WHERE AgvId = '$agvId'"
$assertions.Add(
    'L2-VFCD-04', '门锁故障对 RIoT 里结束的单放行清除：200 Cleared、处置 NONE（重建交给服务端看到取消时登记的那一条），故障 Level None',
    ($cleared.Status -eq 200 -and [string]$cleared.Body.outcome -eq 'Cleared' -and [string]$cleared.Body.disposition -eq 'NONE' -and
        $fault.Count -eq 1 -and [string]$fault[0].Level -eq 'None'),
    '200 / Cleared / NONE / None',
    "$($cleared.Status) / $(if ($cleared.Body) { "$([string]$cleared.Body.outcome) / $([string]$cleared.Body.disposition)" } else { '- / -' }) / $(if ($fault.Count) { [string]$fault[0].Level } else { '-' })")

$rebuilt = Wait-L2ConditionOrLast -Description 'the order was rebuilt for the same vehicle and demand, and confirmed' `
    -Journal $journal -Criterion 'order-rebuilt' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql (
            "SELECT s.UpperId, o.DemandId, o.VehicleKey, o.Status FROM JourneyStops s JOIN OrderIntents o ON o.UpperId = s.UpperId " +
            "WHERE s.JourneyId = '$([string]$journey.JourneyId)' AND s.StopRole = 'PICKUP'")
        if ($rows.Count -ge 1) { $rows[0] } else { $null }
    } `
    -Until { param($v) $v -and [string]$v.UpperId -ne $upperId -and [string]$v.Status -eq 'CONFIRMED' }
$assertions.Add(
    'L2-VFCD-05', '清除之后同车同需求重建开往取货站的单，意图已确认；全程只有 1 次急停',
    ($null -ne $rebuilt -and [string]$rebuilt.UpperId -ne $upperId -and [string]$rebuilt.Status -eq 'CONFIRMED' -and
        [string]$rebuilt.DemandId -eq [string]$journey.DemandId -and [string]$rebuilt.VehicleKey -eq $vehicleKey -and
        (Get-RiotInvocations 'triggerEmergency').Count -eq 1),
    "新单 / CONFIRMED / 需求 $([string]$journey.DemandId) / $vehicleKey / 1",
    $(if ($null -eq $rebuilt) { '(取货停靠读不到)' } else {
        "$($rebuilt.UpperId) / $($rebuilt.Status) / 需求 $($rebuilt.DemandId) / $($rebuilt.VehicleKey) / $((Get-RiotInvocations 'triggerEmergency').Count)" }))

$journal.Note('Scenario finished.')
