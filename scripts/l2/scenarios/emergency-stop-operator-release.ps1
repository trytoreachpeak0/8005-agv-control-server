#Requires -Version 7

<#
人工确认解除急停：车在两站之间被急停锁住，服务端人员确认后由服务端解除，解除之后不再重触发。

载体是需求基线 v1.3.0 的 REQ-0247（急停锁住即视为停稳）、REQ-0248（服务端自己解除的不算意外恢复）与新增的
REQ-0356（人工确认解除），变更提案 CP-0003，实现见 control-server#63。

**为什么要有这条场景。**2026-09-15 agv02 现场三次急停表明：订单还在执行时锁住的车一直报 MT_RUNNING 加车速 0，
停在两站之间的车站点号为 0，服务端的组合停稳证明永远不成立，自动解除走不通；现场在 RIoT 里手动解锁，服务端又会当成
意外恢复立即重触发。车回不来。本场景按现场的顺序把这条路走一遍：

1. 车在两站之间行驶，在途单被报 FAILED，服务端发急停；锁住后车仍报 MT_RUNNING 加车速 0。
2. 锁住即停稳：故障事实记为已停稳，服务端不再请求停车。
3. 确认不全、车上还有未结束订单：服务端拒绝，一次 cancelEmergency 都不发。
4. 确认齐全：服务端自己发一次 cancelEmergency。假 RIoT 不替服务端解锁，所以回读仍是 CAN_RECOVER，入口回 202——
   这正是现场的样子（真 RIoT 解锁用了 3.6 秒）。
5. 场景把急停状态摆成 OK（RIoT 解锁了），车停在两站之间：那次解除被结算为已确认，服务端不重触发，也不因为
   位置读不到再急停（2026-09-15 用户裁定）；车辆故障阻断还在。
6. 车又动起来：服务端按新的一次急停再发 triggerEmergency。

**假 RIoT 不替服务端锁、解急停**，它一贯如此：命令只记录，后果由场景照真实 RIoT 的样子摆出来。
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
$releaseUri = "http://127.0.0.1:$($Context.HealthPort)/api/safety/v1/emergency-stop-releases"
if (-not $Context.EmergencyReleaseCredential) {
    throw 'The setup file must turn EmergencyStopRelease on; without it this scenario proves nothing.'
}

function Get-EmergencyAttempts([string]$commandType) {
    # 先赋值再用，不要把这个函数直接送进管道：`Invoke-L2Query` 的 `return , $rows` 包装穿得过一层 return。
    return Invoke-L2Query -Connection $connection -Sql @"
SELECT CommandType, AgvId, TargetUpperId, AttemptNumber, Outcome, FaultGeneration, ReceiptJson
FROM RiotOrderCommandAudit WHERE CommandType = '$commandType' ORDER BY AttemptNumber
"@
}

function Get-RiotInvocations([string]$commandType) {
    # `return , @(...)`：只有一个元素时不被管道拆开，严格模式下对它取 `.Count` 才不会抛错。
    return , @(@($riot.Snapshot().body.commandInvocations) | Where-Object { [string]$_.commandType -eq $commandType })
}

function Get-Fault {
    return Invoke-L2Query -Connection $connection `
        -Sql "SELECT Level, StopProven, EscalatedAt, ClearedAt FROM VehicleFaultStates WHERE AgvId = '$agvId'"
}

function Set-Vehicle([hashtable]$fields) {
    $body = @{ vehicleKey = $vehicleKey } + $fields
    $null = $riot.Command('Put', 'vehicle', $body)
}

function Invoke-Release([hashtable]$overrides) {
    $body = @{
        agvId          = $agvId
        operatorId     = 'L2-OPERATOR-07'
        causeCleared   = $true
        vehicleEmpty   = $true
        allDoorsClosed = $true
        note           = "L2 $($Context.RunId)"
    }
    foreach ($key in $overrides.Keys) { $body[$key] = $overrides[$key] }
    $response = Invoke-WebRequest -Method Post -Uri $releaseUri -SkipHttpErrorCheck -TimeoutSec 30 `
        -Headers @{ Authorization = "Bearer $($Context.EmergencyReleaseCredential)" } `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json)
    # A refusal is application/problem+json, which Invoke-WebRequest does not treat as text: Content
    # arrives as bytes. The first run of this scenario (-001) went red right here, on a 409 that was
    # correct in every field.
    $text = if ($response.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($response.Content)
    } else {
        [string]$response.Content
    }
    $journal.Note("Release request ($(($overrides.Keys | Sort-Object) -join ',')) -> $([int]$response.StatusCode): $text")
    return [pscustomobject]@{
        Status = [int]$response.StatusCode
        Body   = if ($text) { $text | ConvertFrom-Json } else { $null }
    }
}

# --- 1. 一台车上路，在两站之间被急停锁住 ------------------------------------------------------------------

$demandId = [guid]::NewGuid().ToString('N')
$journal.Note("Publishing demand $demandId.")
$null = $mes.Command('Put', "demands/$demandId", @{
    sublot      = "L2-ESR-$($Context.RunId)"
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

$journal.Note("$agvId drives between stations; RIoT reports $($intent.UpperId) FAILED.")
Set-Vehicle @{ movementState = 'MT_RUNNING'; speed = 0.6; currentPosition = 0 }
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 4 })

$null = Wait-L2Condition -Description 'the server escalated to an emergency stop' `
    -Journal $journal -Criterion 'trigger-issued' -TimeoutSeconds 90 `
    -Probe { (Get-EmergencyAttempts 'triggerEmergency').Count } -Until { param($v) $v -ge 1 }

# 现场 agv02 在订单仍执行时被锁住后的读数：MT_RUNNING、车速 0、站点 0。
$journal.Note('The latch engages; the vehicle keeps reporting MT_RUNNING at speed 0 between stations.')
Set-Vehicle @{ emergencyState = 'CAN_RECOVER'; movementState = 'MT_RUNNING'; speed = 0; currentPosition = 0 }
$null = Wait-L2Condition -Description 'the trigger was settled as confirmed' `
    -Journal $journal -Criterion 'trigger-confirmed' -TimeoutSeconds 30 `
    -Probe { [string](Get-EmergencyAttempts 'triggerEmergency')[0].Outcome } `
    -Until { param($v) $v -eq 'Confirmed' }

# --- 2. 锁住即停稳 -----------------------------------------------------------------------------------

$null = Wait-L2Iterations -Riot $riot -Count 4 -TimeoutSeconds 60 -Journal $journal
$fault = Get-Fault
$assertions.Add(
    'L2-ESR-01',
    '锁住即停稳（REQ-0247）：车报 MT_RUNNING、车速 0、站点 0，故障事实仍记为已停稳，服务端没有再请求停车',
    ($fault.Count -eq 1 -and [int]$fault[0].StopProven -eq 1 -and
        (Get-EmergencyAttempts 'triggerEmergency').Count -eq 1 -and (Get-RiotInvocations 'triggerEmergency').Count -eq 1),
    'StopProven 1 / 1 行 / 1 次',
    $(if ($fault.Count -eq 0) { '(无故障行)' }
      else { "StopProven $([int]$fault[0].StopProven) / $((Get-EmergencyAttempts 'triggerEmergency').Count) 行 / $((Get-RiotInvocations 'triggerEmergency').Count) 次" }))

# --- 3. 确认不全、订单未结束：拒绝，一次解除都不发 ----------------------------------------------------------

$incomplete = Invoke-Release @{ vehicleEmpty = $false }
$assertions.Add(
    'L2-ESR-02',
    '没确认「车上无货」：入口回 409，原因里点名 EMERGENCY_VEHICLE_EMPTY_NOT_CONFIRMED，没有发 cancelEmergency',
    ($incomplete.Status -eq 409 -and @($incomplete.Body.reasons) -contains 'EMERGENCY_VEHICLE_EMPTY_NOT_CONFIRMED' -and
        (Get-RiotInvocations 'cancelEmergency').Count -eq 0),
    '409 / EMERGENCY_VEHICLE_EMPTY_NOT_CONFIRMED / 0 次',
    "$($incomplete.Status) / $(@($incomplete.Body.reasons) -join ',') / $((Get-RiotInvocations 'cancelEmergency').Count) 次")

# 在途单暂时摆回「暂停」（未结束），入口必须看 RIoT 里这台车的订单，而不是只看确认框。
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 7 })
$unfinished = Invoke-Release @{}
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 4 })
$assertions.Add(
    'L2-ESR-03',
    '这台车在 RIoT 里还有未结束的订单：入口回 409，原因 EMERGENCY_VEHICLE_ORDER_NOT_FINISHED，没有发 cancelEmergency（REQ-0356）',
    ($unfinished.Status -eq 409 -and @($unfinished.Body.reasons) -contains 'EMERGENCY_VEHICLE_ORDER_NOT_FINISHED' -and
        (Get-RiotInvocations 'cancelEmergency').Count -eq 0),
    '409 / EMERGENCY_VEHICLE_ORDER_NOT_FINISHED / 0 次',
    "$($unfinished.Status) / $(@($unfinished.Body.reasons) -join ',') / $((Get-RiotInvocations 'cancelEmergency').Count) 次")

# --- 4. 确认齐全：服务端自己发一次解除，回读仍锁着，回 202 ------------------------------------------------

$released = Invoke-Release @{}
$releaseCalls = Get-RiotInvocations 'cancelEmergency'
$releaseRows = Get-EmergencyAttempts 'cancelEmergency'
$assertions.Add(
    'L2-ESR-04',
    '确认齐全：服务端对这台车发出一次 cancelEmergency；RIoT 还没解锁，入口回 202、动作 RecoveryUnconfirmed，不谎报成功',
    ($released.Status -eq 202 -and [string]$released.Body.action -eq 'RecoveryUnconfirmed' -and
        $releaseCalls.Count -eq 1 -and [string]$releaseCalls[0].target -eq $vehicleKey -and $releaseRows.Count -eq 1),
    "202 / RecoveryUnconfirmed / 1 次 @ $vehicleKey / 1 行",
    "$($released.Status) / $([string]$released.Body.action) / $($releaseCalls.Count) 次 / $($releaseRows.Count) 行")

$receipt = [string]$releaseRows[0].ReceiptJson
$assertions.Add(
    'L2-ESR-05',
    '解除记下了确认人与三项确认：来源 ServerOperator、工号 L2-OPERATOR-07、causeCleared/vehicleEmpty/allDoorsClosed 均为 true',
    ($receipt -like '*"source":"ServerOperator"*' -and $receipt -like '*"requesterIdentity":"L2-OPERATOR-07"*' -and
        $receipt -like '*"causeCleared":true*' -and $receipt -like '*"vehicleEmpty":true*' -and $receipt -like '*"allDoorsClosed":true*'),
    'ServerOperator / L2-OPERATOR-07 / 三项 true',
    $receipt)

# --- 5. RIoT 解锁，车停在两站之间：结算为已解除，不重触发，也不因位置读不到再急停 ----------------------------------

$journal.Note('RIoT clears the latch a few seconds later; the vehicle stands between stations, its order FAILED.')
Set-Vehicle @{ emergencyState = 'OK'; movementState = 'MT_FINISHED'; speed = 0; currentPosition = 0 }
$settled = Wait-L2Condition -Description 'the release was settled as confirmed' `
    -Journal $journal -Criterion 'release-confirmed' -TimeoutSeconds 30 `
    -Probe { [string](Get-EmergencyAttempts 'cancelEmergency')[0].Outcome } `
    -Until { param($v) $v -eq 'Confirmed' }
$assertions.Add(
    'L2-ESR-06',
    'RIoT 读到 OK 之后，那一次解除被改记为 Confirmed，急停就此结束',
    ($settled -eq 'Confirmed'),
    'Confirmed',
    $settled)

$null = Wait-L2Iterations -Riot $riot -Count 6 -TimeoutSeconds 60 -Journal $journal
$triggers = Get-EmergencyAttempts 'triggerEmergency'
$assertions.Add(
    'L2-ESR-07',
    '解除之后又评估了 6 轮：车停在两站之间、位置读不到，服务端既没有按意外恢复重触发，也没有再急停（仍只 1 次 triggerEmergency、1 次 cancelEmergency）',
    ($triggers.Count -eq 1 -and (Get-RiotInvocations 'triggerEmergency').Count -eq 1 -and
        (Get-RiotInvocations 'cancelEmergency').Count -eq 1),
    '1 行 / 1 次 / 1 次',
    "$($triggers.Count) 行 / $((Get-RiotInvocations 'triggerEmergency').Count) 次 / $((Get-RiotInvocations 'cancelEmergency').Count) 次")

$fault = Get-Fault
$assertions.Add(
    'L2-ESR-08',
    '解除只结束急停：车辆故障事实仍是 SuspectedBlocked、未清除，这台车照样不派新单',
    ($fault.Count -eq 1 -and [string]$fault[0].Level -eq 'SuspectedBlocked' -and
        ($null -eq $fault[0].ClearedAt -or [string]$fault[0].ClearedAt -eq '')),
    'SuspectedBlocked / 未清除',
    $(if ($fault.Count -eq 0) { '(无故障行)' } else { "$([string]$fault[0].Level) / ClearedAt=$([string]$fault[0].ClearedAt)" }))

$again = Invoke-Release @{}
$assertions.Add(
    'L2-ESR-09',
    '急停已经解开后再点一次确认：入口回 409（EMERGENCY_NOT_CAN_RECOVER），不再发任何调用',
    ($again.Status -eq 409 -and @($again.Body.reasons) -contains 'EMERGENCY_NOT_CAN_RECOVER' -and
        (Get-RiotInvocations 'cancelEmergency').Count -eq 1),
    '409 / EMERGENCY_NOT_CAN_RECOVER / 仍 1 次',
    "$($again.Status) / $(@($again.Body.reasons) -join ',') / $((Get-RiotInvocations 'cancelEmergency').Count) 次")

# --- 6. 车又动起来：按新的一次急停处理 ------------------------------------------------------------------

$journal.Note('The vehicle starts moving again.')
Set-Vehicle @{ movementState = 'MT_RUNNING'; speed = 0.5; currentPosition = 0 }
$null = Wait-L2Condition -Description 'a new emergency stop was issued for the moving vehicle' `
    -Journal $journal -Criterion 'new-trigger-issued' -TimeoutSeconds 30 `
    -Probe { (Get-EmergencyAttempts 'triggerEmergency').Count } -Until { param($v) $v -ge 2 }

$second = (Get-EmergencyAttempts 'triggerEmergency')[1]
$assertions.Add(
    'L2-ESR-10',
    '解除之后读到车在动：服务端发出新的一次 triggerEmergency，原因不是 EMERGENCY_LATCH_RELEASED_EXTERNALLY（不是意外恢复，是新的急停）',
    ([int]$second.AttemptNumber -eq 2 -and [string]$second.ReceiptJson -notlike '*EMERGENCY_LATCH_RELEASED_EXTERNALLY*' -and
        (Get-RiotInvocations 'triggerEmergency').Count -eq 2),
    'AttemptNumber 2 / 非意外恢复 / 2 次',
    "AttemptNumber $([int]$second.AttemptNumber) / $([string]$second.ReceiptJson) / $((Get-RiotInvocations 'triggerEmergency').Count) 次")

$journal.Note('人工确认解除：锁住即停稳；确认不全或订单未结束就拒绝；确认齐全服务端自己解除、稍后读到 OK 结算；解除后不重触发；车再动按新急停处理。')
