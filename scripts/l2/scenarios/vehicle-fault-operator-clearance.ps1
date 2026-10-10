#Requires -Version 7

<#
车辆故障的人工清除：在途单 FAILED 的车被判为疑似故障、被急停锁住；人工解除急停、确认故障已排除之后，服务端自己核对判据、
清除故障；需求留在本车，延迟之后为同一辆车、同一条需求重建运单，车接着走。

载体是 control-server#299（#299 阶段一方案 T2，用户 2026-09-22 定 F-a 与 HTTP 入口）；清除之后的处置由 control-server#318 改写：
原来是「没取货的需求释放改派、车重新接单」，用户 2026-09-23 推翻（issuecomment-5787511271：「不改派啊，留在本车上」），
改为同车重建。L2-VFC-04～06 的判据随之翻转，依据就是这一条。

**为什么要有这条场景。**修之前，订单 FAILED 引起的故障没有任何办法清掉：唯一的清除路径 `ResumeAsync` 要求订单被 Hold 成
PAUSED，FAILED 永远不是。车一直不接单、需求一直不改派，唯一的出口是改库。这条场景按现场的顺序走一遍新出口：

1. 车在两站之间行驶，在途单被报 FAILED，服务端判疑似故障并急停；锁住。
2. 锁着的车清不了故障：入口回 409，理由点名 FAULT_RECOVERY_EMERGENCY_LATCHED。清除不解除急停。
3. 按 REQ-0356 人工解除急停，RIoT 解锁。
4. 没确认「故障已排除」：409；RIoT 里这台车还有未结束的订单：409，理由把两项都列出来。什么都不改。
5. 条件齐全：200，故障清除，操作员记进 ClearedReason；处置 REBUILD_SCHEDULED：需求不释放，旅程不关闭、停在原阶段，
   码 VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD。
6. 延迟之后（setup 调成 8 秒）：取货停靠改指向一张服务端自己的新单，同车、同需求、同一站，RIoT 上有这张单；仍是同一趟旅程，
   之后几轮不再记新的故障、不再急停。
7. 同一请求再来一次：200 AlreadyCleared，旅程表一行不变。
8. 已经清过的车，再来一个没署名的请求：409，理由只有 FAULT_RECOVERY_OPERATOR_UNIDENTIFIED，不答「已经清过」。

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
$connection = $Context.Connection
$agvId = [string]$Context.AgvId
$vehicleKey = [string]$Context.VehicleKey
$releaseUri = "http://127.0.0.1:$($Context.HealthPort)/api/safety/v1/emergency-stop-releases"
$recoveryUri = "http://127.0.0.1:$($Context.HealthPort)/api/safety/v1/vehicle-fault-recoveries"
if (-not $Context.FaultRecoveryCredential -or -not $Context.EmergencyReleaseCredential) {
    throw 'The setup file must turn VehicleFaultRecovery and EmergencyStopRelease on; without them this scenario proves nothing.'
}

function Get-EmergencyAttempts([string]$commandType) {
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

function Get-Fault {
    return Invoke-L2Query -Connection $connection `
        -Sql "SELECT Level, FaultGeneration, ClearedAt, ClearedReason FROM VehicleFaultStates WHERE AgvId = '$agvId'"
}

function Get-Journeys {
    # Same wrapping as Get-EmergencyAttempts: assign the result before filtering it, never pipe the call itself.
    return Invoke-L2Query -Connection $connection `
        -Sql "SELECT JourneyId, DemandId, Stage, BlockReasonCode FROM JourneyRuntimes WHERE AgvId = '$agvId' ORDER BY rowid"
}

function Set-Vehicle([hashtable]$fields) {
    $body = @{ vehicleKey = $vehicleKey } + $fields
    $null = $riot.Command('Put', 'vehicle', $body)
}

function Invoke-Json([string]$uri, [string]$credential, [hashtable]$body, [string]$label) {
    $response = Invoke-WebRequest -Method Post -Uri $uri -SkipHttpErrorCheck -TimeoutSec 60 `
        -Headers @{ Authorization = "Bearer $credential" } `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json)
    # A refusal is application/problem+json, which Invoke-WebRequest does not treat as text: Content arrives as bytes
    # (emergency-stop-operator-release -001 went red on exactly that).
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

function Invoke-Recovery([hashtable]$overrides, [string]$label) {
    $body = @{
        agvId         = $agvId
        operatorId    = 'L2-OPERATOR-07'
        action        = 'CLEAR_FAULT'
        faultRemedied = $true
        note          = "L2 $($Context.RunId)"
    }
    foreach ($key in $overrides.Keys) { $body[$key] = $overrides[$key] }
    return Invoke-Json $recoveryUri $Context.FaultRecoveryCredential $body "Fault recovery ($label)"
}

function Get-Reasons([object]$response) {
    # `reasons` sits on the 409 problem body and on the 200 body alike. `return , @(...)` so that one reason stays a
    # one-element array: unwrapped to a string, `[-1]` below would read its last character.
    if ($null -eq $response.Body) { return , @() }
    return , @($response.Body.reasons | ForEach-Object { [string]$_ })
}

# --- 1. 一台车上路，在两站之间订单 FAILED，被急停锁住 ------------------------------------------------------

$demandId = [guid]::NewGuid().ToString('N')
$journal.Note("Publishing demand $demandId.")
$null = $mes.Command('Put', "demands/$demandId", @{
    sublot      = "L2-VFC-$($Context.RunId)"
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$journey = Wait-L2Condition -Description 'the vehicle took a journey' `
    -Journal $journal -Criterion 'journey-dispatched' -TimeoutSeconds 120 `
    -Probe {
        $rows = Get-Journeys
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

$journal.Note("$agvId drives between stations; RIoT reports $($intent.UpperId) FAILED.")
Set-Vehicle @{ movementState = 'MT_RUNNING'; speed = 0.6; currentPosition = 0 }
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 4 })

$null = Wait-L2Condition -Description 'the server escalated to an emergency stop' `
    -Journal $journal -Criterion 'trigger-issued' -TimeoutSeconds 90 `
    -Probe { (Get-EmergencyAttempts 'triggerEmergency').Count } -Until { param($v) $v -ge 1 }

$journal.Note('The latch engages; the vehicle keeps reporting MT_RUNNING at speed 0 between stations.')
Set-Vehicle @{ emergencyState = 'CAN_RECOVER'; movementState = 'MT_RUNNING'; speed = 0; currentPosition = 0 }
$null = Wait-L2Condition -Description 'the trigger was settled as confirmed' `
    -Journal $journal -Criterion 'trigger-confirmed' -TimeoutSeconds 30 `
    -Probe { [string](Get-EmergencyAttempts 'triggerEmergency')[0].Outcome } `
    -Until { param($v) $v -eq 'Confirmed' }

# --- 2. 锁着的车清不了故障 --------------------------------------------------------------------------------

$latched = Invoke-Recovery @{} 'latched'
$fault = Get-Fault
$assertions.Add(
    'L2-VFC-01',
    '急停锁着：清除入口回 409，理由点名 FAULT_RECOVERY_EMERGENCY_LATCHED；故障仍是 SuspectedBlocked，没有发 cancelEmergency（清除不解除急停）',
    ($latched.Status -eq 409 -and (Get-Reasons $latched) -contains 'FAULT_RECOVERY_EMERGENCY_LATCHED' -and
        $fault.Count -eq 1 -and [string]$fault[0].Level -eq 'SuspectedBlocked' -and
        (Get-RiotInvocations 'cancelEmergency').Count -eq 0),
    '409 / FAULT_RECOVERY_EMERGENCY_LATCHED / SuspectedBlocked / 0 次',
    "$($latched.Status) / $((Get-Reasons $latched) -join ',') / $(if ($fault.Count) { [string]$fault[0].Level } else { '(无故障行)' }) / $((Get-RiotInvocations 'cancelEmergency').Count) 次")

# --- 3. 按 REQ-0356 人工解除急停，RIoT 解锁 ---------------------------------------------------------------

$released = Invoke-Json $releaseUri $Context.EmergencyReleaseCredential @{
    agvId = $agvId; operatorId = 'L2-OPERATOR-07'; causeCleared = $true; vehicleEmpty = $true; allDoorsClosed = $true
    note = "L2 $($Context.RunId)"
} 'Emergency release'
if ($released.Status -ne 202 -and $released.Status -ne 200) {
    throw "The REQ-0356 release this scenario builds on was refused ($($released.Status)); see the journal."
}

# RIoT clears the latch a few seconds later. The vehicle is put back where the fleet leaves it idle -- at its station,
# not running an order -- so that once the fault is cleared the dispatch round can take it again.
$journal.Note('RIoT clears the latch; the vehicle stands idle at its station, its order FAILED.')
Set-Vehicle @{
    emergencyState = 'OK'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.GateStationRiotId; processingOrder = $false; clearOrderTaskId = $true
}
$null = Wait-L2Condition -Description 'the release was settled as confirmed' `
    -Journal $journal -Criterion 'release-confirmed' -TimeoutSeconds 30 `
    -Probe { [string](Get-EmergencyAttempts 'cancelEmergency')[0].Outcome } `
    -Until { param($v) $v -eq 'Confirmed' }
$null = Wait-L2Iterations -Riot $riot -Count 3 -TimeoutSeconds 60 -Journal $journal
$fault = Get-Fault
$journeysBefore = Get-Journeys
$assertions.Add(
    'L2-VFC-02',
    '急停解除之后故障仍在（解除只结束急停）：SuspectedBlocked、未清除；车一直挂在原旅程上，没有新旅程',
    ($fault.Count -eq 1 -and [string]$fault[0].Level -eq 'SuspectedBlocked' -and $journeysBefore.Count -eq 1),
    'SuspectedBlocked / 1 趟旅程',
    "$(if ($fault.Count) { [string]$fault[0].Level } else { '(无故障行)' }) / $($journeysBefore.Count) 趟旅程")

# --- 4. 条件不全：拒绝，理由全列，什么都不改 ----------------------------------------------------------------

# 在途单暂时摆回「暂停」：RIoT 里这台车还有未结束的订单，旅程等的那张单也没结束。入口必须自己读 RIoT，而不是只看确认框。
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 7 })
$incomplete = Invoke-Recovery @{ faultRemedied = $false } 'not remedied, order unfinished'
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 4 })
$reasons = Get-Reasons $incomplete
$fault = Get-Fault
$assertions.Add(
    'L2-VFC-03',
    '没确认故障已排除、RIoT 里这台车还有未结束的订单：入口回 409，三条理由（没确认、车上有未完成订单、当前单没结束）一起列出；故障不动',
    ($incomplete.Status -eq 409 -and $reasons -contains 'FAULT_RECOVERY_REMEDY_NOT_CONFIRMED' -and
        $reasons -contains 'FAULT_RECOVERY_VEHICLE_ORDER_NOT_FINISHED' -and $reasons -contains 'FAULT_RECOVERY_CURRENT_ORDER_NOT_ENDED' -and
        [string]$fault[0].Level -eq 'SuspectedBlocked'),
    '409 / REMEDY_NOT_CONFIRMED + VEHICLE_ORDER_NOT_FINISHED + CURRENT_ORDER_NOT_ENDED / SuspectedBlocked',
    "$($incomplete.Status) / $($reasons -join ',') / $([string]$fault[0].Level)")

# --- 5. 条件齐全：清除 ------------------------------------------------------------------------------------

$cleared = Invoke-Recovery @{} 'complete'
$fault = Get-Fault
$journeysAfterClear = Get-Journeys
$old = @($journeysAfterClear | Where-Object { [string]$_.JourneyId -eq [string]$journey.JourneyId })
$membership = Invoke-L2Query -Connection $connection `
    -Sql "SELECT RemovedAt FROM JourneyDemands WHERE JourneyId = '$([string]$journey.JourneyId)'"
$assertions.Add(
    'L2-VFC-04',
    '条件齐全：入口回 200，结果 Cleared、处置 REBUILD_SCHEDULED；故障 Level None，ClearedReason 记着操作员 L2-OPERATOR-07；旧旅程不关闭、停在开往取货站，码 VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD，需求不释放（翻转自 #299 的「释放改派、旧旅程关闭」，依据 issuecomment-5787511271）',
    ($cleared.Status -eq 200 -and [string]$cleared.Body.outcome -eq 'Cleared' -and
        [string]$cleared.Body.disposition -eq 'REBUILD_SCHEDULED' -and
        [string]$fault[0].Level -eq 'None' -and [string]$fault[0].ClearedReason -like '*L2-OPERATOR-07*' -and
        $journeysAfterClear.Count -eq 1 -and $old.Count -eq 1 -and [string]$old[0].Stage -eq 'AwaitingPickupArrival' -and
        [string]$old[0].BlockReasonCode -eq 'VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD' -and
        $membership.Count -eq 1 -and ($null -eq $membership[0].RemovedAt -or [string]$membership[0].RemovedAt -eq '')),
    '200 / Cleared / REBUILD_SCHEDULED / None / L2-OPERATOR-07 / 1 趟 AwaitingPickupArrival VEHICLE_FAULT_CLEARED_NOTHING_ON_BOARD / 需求未移除',
    "$($cleared.Status) / $([string]$cleared.Body.outcome) / $([string]$cleared.Body.disposition) / $([string]$fault[0].Level) / $([string]$fault[0].ClearedReason) / $($journeysAfterClear.Count) 趟 $(if ($old.Count) { "$([string]$old[0].Stage) $([string]$old[0].BlockReasonCode)" } else { '(没有旧旅程)' }) / $(if ($membership.Count -eq 1 -and ($null -eq $membership[0].RemovedAt -or [string]$membership[0].RemovedAt -eq '')) { '需求未移除' } else { '需求已移除' })")

# --- 6. 延迟之后：同车同需求重建，车接着走 ----------------------------------------------------------------------

# 停靠改指向新单与新意图在一次保存里，RIoT 上的单与意图的确认在之后：等后一个，读不到就把最后一次读到的样子交给判据
# （README 第 14 条）。
$rebuilt = Wait-L2ConditionOrLast -Description 'the order was rebuilt for the same vehicle and demand, and confirmed' `
    -Journal $journal -Criterion 'order-rebuilt' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql (
            "SELECT s.UpperId, o.DemandId, o.VehicleKey, o.Status, o.DestinationStationId, r.JourneyId, r.Stage FROM JourneyStops s " +
            "JOIN OrderIntents o ON o.UpperId = s.UpperId JOIN JourneyRuntimes r ON r.JourneyId = s.JourneyId " +
            "WHERE s.JourneyId = '$([string]$journey.JourneyId)' AND s.StopRole = 'PICKUP'")
        if ($rows.Count -ge 1) { $rows[0] } else { $null }
    } `
    -Until { param($v) $v -and [string]$v.UpperId -ne [string]$intent.UpperId -and [string]$v.Status -eq 'CONFIRMED' }
$riotOrder = @($riot.Snapshot().body.orders | Where-Object { $rebuilt -and [string]$_.upperId -eq [string]$rebuilt.UpperId })
$assertions.Add(
    'L2-VFC-05',
    '延迟之后重建：同一趟旅程的取货停靠改指向一张服务端自己的新单（W2G-…），意图已确认，同一条需求、同一辆车；RIoT 上有这张单、指派给这辆车；没有新旅程',
    ($null -ne $rebuilt -and [string]$rebuilt.UpperId -ne [string]$intent.UpperId -and [string]$rebuilt.UpperId -like 'W2G-*' -and
        [string]$rebuilt.Status -eq 'CONFIRMED' -and [string]$rebuilt.DemandId -eq [string]$journey.DemandId -and
        [string]$rebuilt.VehicleKey -eq $vehicleKey -and [string]$rebuilt.JourneyId -eq [string]$journey.JourneyId -and
        $riotOrder.Count -eq 1 -and [string]$riotOrder[0].appointVehicleKey -eq $vehicleKey -and (Get-Journeys).Count -eq 1),
    "新单号 W2G-… / CONFIRMED / 需求 $([string]$journey.DemandId) / $vehicleKey / RIoT 1 张 / 1 趟旅程",
    $(if ($null -eq $rebuilt) { '(取货停靠读不到)' } else {
        "$($rebuilt.UpperId) / $($rebuilt.Status) / 需求 $($rebuilt.DemandId) / $($rebuilt.VehicleKey) / RIoT $($riotOrder.Count) 张 / $((Get-Journeys).Count) 趟旅程" }))

$null = Wait-L2Iterations -Riot $riot -Count 4 -TimeoutSeconds 60 -Journal $journal
$fault = Get-Fault
$assertions.Add(
    'L2-VFC-06',
    '清除之后又评估了 4 轮：故障没有再记（Level None、代次仍是 1），没有新的急停（仍只 1 次 triggerEmergency）',
    ([string]$fault[0].Level -eq 'None' -and [long]$fault[0].FaultGeneration -eq 1 -and
        (Get-RiotInvocations 'triggerEmergency').Count -eq 1),
    'None / 代次 1 / 1 次',
    "$([string]$fault[0].Level) / 代次 $([long]$fault[0].FaultGeneration) / $((Get-RiotInvocations 'triggerEmergency').Count) 次")

# --- 7. 同一请求再来一次 --------------------------------------------------------------------------------------

function Get-JourneysJson {
    $rows = Get-Journeys
    return ConvertTo-Json -InputObject @($rows) -Compress
}

$journeysBeforeRepeat = Get-JourneysJson
$again = Invoke-Recovery @{} 'repeat'
$assertions.Add(
    'L2-VFC-07',
    '同一个清除请求再来一次：200、AlreadyCleared，旅程表一行不变（不碰清除之后重建出来接着走的这趟旅程）',
    ($again.Status -eq 200 -and [string]$again.Body.outcome -eq 'AlreadyCleared' -and
        (Get-JourneysJson) -eq $journeysBeforeRepeat),
    '200 / AlreadyCleared / 旅程不变',
    "$($again.Status) / $([string]$again.Body.outcome) / $(if ((Get-JourneysJson) -eq $journeysBeforeRepeat) { '旅程不变' } else { '旅程变了' })")

# --- 8. 「已经清过」不是免检通道：没署名的请求照样按缺的那一项拒绝（独立审查 L3） ------------------------------

$unsigned = Invoke-Recovery @{ operatorId = '' } 'unsigned'
$unsignedReasons = Get-Reasons $unsigned
$assertions.Add(
    'L2-VFC-08',
    '已经清过的车，再来一个没署名的请求：409，理由只有 FAULT_RECOVERY_OPERATOR_UNIDENTIFIED，不答「已经清过」，旅程表一行不变',
    ($unsigned.Status -eq 409 -and $unsignedReasons.Count -eq 1 -and $unsignedReasons[0] -eq 'FAULT_RECOVERY_OPERATOR_UNIDENTIFIED' -and
        (Get-JourneysJson) -eq $journeysBeforeRepeat),
    '409 / FAULT_RECOVERY_OPERATOR_UNIDENTIFIED / 旅程不变',
    "$($unsigned.Status) / $($unsignedReasons -join ',') / $(if ((Get-JourneysJson) -eq $journeysBeforeRepeat) { '旅程不变' } else { '旅程变了' })")

$journal.Note('故障人工清除：锁着清不了；解除急停后条件不全就拒并全列理由；齐全则清除、需求留在本车、延迟后同车重建、不再记故障；重复请求不做事；没署名的重复请求照样被拒。')
