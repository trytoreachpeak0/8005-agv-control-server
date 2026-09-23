#Requires -Version 7

<#
自动重建停住之后，人经 #299 的入口人工重建一次（control-server#345，出口甲）。

本服务端自己的在途单在 RIoT 里被取消，服务端延迟之后为同一辆车、同一条需求重建（control-server#318 来源一）；重建出来的单
在 REQ-0361 的窗口内又被取消——护栏三停住，旅程码 OWN_ORDER_REBUILD_STOPPED，需求不改派、车不接新单，「由人员处理」。修之前
服务端没有给人的出口，只能找工程师改库。本场景走出口甲：
  1. 两次取消之后旅程停住，重建记录 STOPPED（REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW），只建过两张单；
  2. 没确认已查明原因的请求被拒（409，理由列全），什么都不改；
  3. 署名、确认的请求被受理（200，RebuildRequested），停住的记录原行重开、署上这个人；
  4. 引擎下一轮给同一辆车、同一条需求建第三张单，去同一个取货停靠，意图已确认，旅程码清掉；
  5. 同一请求再来一次：200 AlreadyDone，不再建单；全程没有发出任何订单命令或急停。

延迟在 setup 里调成 8 秒（产品默认 30 秒），只为省机时。人工重建立即到期、不等延迟。

第二个事实（第三张单在 RIoT 上、意图已确认）按 scripts/l2/README.md 第 14 条用 Wait-L2ConditionOrLast 等，不读一次就断言。
负判据要有界：转过几轮之后再判。

红证据（修复之前的 fp/v2-impl 012c31b2，evidence/cs345/red/l2-in-transit-rebuild-stopped-person-rebuilds-012c31b2/）：L2-SR-01 PASS
（护栏三照样停住），L2-SR-02～05 FAIL——入口以 422 拒绝（只认 CLEAR_FAULT、RESUME_HELD_ORDER），停住之后没有第三张单。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$mes = $Context.MesIngest
$riot = $Context.Riot
$agvId = [string]$Context.AgvId
$recoveryUri = "http://127.0.0.1:$($Context.HealthPort)/api/safety/v1/vehicle-fault-recoveries"
if (-not $Context.FaultRecoveryCredential) {
    throw 'The setup file must turn VehicleFaultRecovery on; without it this scenario proves nothing.'
}

$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')

function Invoke-Scalar([string]$sql) {
    # 先赋值再用：`Invoke-L2Query` 的 `return , $rows` 包装穿得过一层 return（README 第 14 条之外的那条坑）。
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Count([string]$sql) {
    $row = Invoke-Scalar $sql
    return [int]$row.N
}

function Invoke-Exit([hashtable]$overrides, [string]$label) {
    $body = @{
        agvId         = $agvId
        operatorId    = 'L2-OPERATOR-345'
        action        = 'REBUILD_STOPPED_ORDER'
        faultRemedied = $true
        note          = "L2 $($Context.RunId)"
    }
    foreach ($key in $overrides.Keys) { $body[$key] = $overrides[$key] }
    $response = Invoke-WebRequest -Method Post -Uri $recoveryUri -SkipHttpErrorCheck -TimeoutSec 60 `
        -Headers @{ Authorization = "Bearer $($Context.FaultRecoveryCredential)" } `
        -ContentType 'application/json' -Body ($body | ConvertTo-Json)
    # A refusal is application/problem+json, which Invoke-WebRequest does not treat as text: Content arrives as bytes.
    $text = if ($response.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($response.Content)
    } else {
        [string]$response.Content
    }
    $journal.Note("Stopped rebuild exit ($label) -> $([int]$response.StatusCode): $text")
    return [pscustomobject]@{
        Status = [int]$response.StatusCode
        Body   = if ($text) { $text | ConvertFrom-Json } else { $null }
    }
}

function Get-Reasons([object]$response) {
    # `return , @(...)` so that one reason stays a one-element array.
    if ($null -eq $response.Body -or -not ($response.Body.PSObject.Properties.Name -contains 'reasons')) { return , @() }
    return , @($response.Body.reasons | ForEach-Object { [string]$_ })
}

function Get-Field([object]$response, [string]$name) {
    # A refusal's problem body has no `outcome`; under StrictMode reading it throws instead of failing the criterion.
    if ($null -eq $response.Body -or -not ($response.Body.PSObject.Properties.Name -contains $name)) { return '' }
    return [string]$response.Body.$name
}

function Get-State {
    $journey = Invoke-Scalar "SELECT JourneyId, Stage, BlockReasonCode FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    return [pscustomobject]@{
        Stage    = [string]$journey.Stage
        Code     = [string]$journey.BlockReasonCode
        Intents  = Get-Count "SELECT COUNT(*) AS N FROM OrderIntents WHERE DemandId = '$demandId'"
        Journeys = Get-Count "SELECT COUNT(*) AS N FROM JourneyRuntimes WHERE DemandId = '$demandId'"
        Records  = Get-Count "SELECT COUNT(*) AS N FROM OwnOrderRebuilds WHERE DemandId = '$demandId'"
        Removed  = Get-Count "SELECT COUNT(*) AS N FROM JourneyDemands WHERE DemandId = '$demandId' AND RemovedAt IS NOT NULL"
    }
}

function Set-Running([string]$upperId, [string]$orderId) {
    $null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'PROCESSING_ORDER'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $orderId; currentPosition = 0
    })
}

function Set-CancelledAndStill([string]$upperId) {
    $null = $riot.Command('Put', "orders/$upperId", @{ orderState = 2 })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        processingOrder = $false; clearOrderTaskId = $true; currentPosition = 0
    })
}

# --- 1. 受理、派往取货站；单被取消两次，第二次在窗口内——护栏三停住 ---------------------------------------

$journal.Note("Publishing demand $($demandGuid.ToString('N')) (area N1-3).")
$null = $mes.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = "L2-SR-$($Context.RunId)"; area = 'N1-3'
    eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})

$first = Wait-L2Condition -Description 'the vehicle took the demand and set off to its pickup' `
    -Journal $journal -Criterion 'first-journey' -TimeoutSeconds 120 `
    -Probe {
        Invoke-Scalar ("SELECT r.JourneyId, r.PickupUpperId, r.VehicleKey, o.OrderId, o.Status, o.DestinationStationId " +
            "FROM JourneyRuntimes r JOIN OrderIntents o ON o.UpperId = r.PickupUpperId WHERE r.DemandId = '$demandId'")
    } `
    -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
$pickupStop = Invoke-Scalar ("SELECT StopId, Sequence, StationId FROM JourneyStops " +
    "WHERE JourneyId = '$([string]$first.JourneyId)' AND UpperId = '$([string]$first.PickupUpperId)'")
Set-Running ([string]$first.PickupUpperId) ([string]$first.OrderId)
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal

$journal.Note("A person cancels $([string]$first.PickupUpperId) in RIoT.")
Set-CancelledAndStill ([string]$first.PickupUpperId)
$rebuilt = Wait-L2Condition -Description 'the order was rebuilt once, automatically' `
    -Journal $journal -Criterion 'first-rebuild' -TimeoutSeconds 90 `
    -Probe {
        Invoke-Scalar ("SELECT s.UpperId, o.OrderId, o.Status FROM JourneyStops s JOIN OrderIntents o ON o.UpperId = s.UpperId " +
            "WHERE s.StopId = '$([string]$pickupStop.StopId)'")
    } `
    -Until { param($v) $v -and [string]$v.UpperId -ne [string]$first.PickupUpperId -and [string]$v.Status -eq 'CONFIRMED' }
Set-Running ([string]$rebuilt.UpperId) ([string]$rebuilt.OrderId)
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal

$journal.Note("The rebuilt order $([string]$rebuilt.UpperId) is cancelled again, within the window.")
Set-CancelledAndStill ([string]$rebuilt.UpperId)
$stopped = Wait-L2Condition -Description 'the third guard stopped the rebuilding' `
    -Journal $journal -Criterion 'third-guard' -TimeoutSeconds 60 `
    -Probe { Get-State } `
    -Until { param($v) $v.Code -eq 'OWN_ORDER_REBUILD_STOPPED' }
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$record = Invoke-Scalar "SELECT State, StoppedReason, OperatorId FROM OwnOrderRebuilds WHERE EndedUpperId = '$([string]$rebuilt.UpperId)'"
$held = Get-State
$assertions.Add(
    'L2-SR-01',
    '重建出来的单在窗口内又被取消：护栏三停住，旅程仍开往取货站、码 OWN_ORDER_REBUILD_STOPPED；只建过两张单；记录 STOPPED；需求未移除',
    ($held.Stage -eq 'AwaitingPickupArrival' -and $held.Code -eq 'OWN_ORDER_REBUILD_STOPPED' -and $held.Intents -eq 2 -and
        $held.Removed -eq 0 -and $null -ne $record -and [string]$record.State -eq 'STOPPED' -and
        [string]$record.StoppedReason -eq 'REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW'),
    'AwaitingPickupArrival / OWN_ORDER_REBUILD_STOPPED / 2 张 / 未移除 / STOPPED REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW',
    "$($held.Stage) / $($held.Code) / $($held.Intents) 张 / 移除 $($held.Removed) / $(if ($record) { "$($record.State) $($record.StoppedReason)" } else { '(没有记录)' })")

# --- 2. 没确认已查明原因：拒绝，什么都不改 ---------------------------------------------------------------

$unconfirmed = Invoke-Exit @{ faultRemedied = $false } 'unconfirmed'
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal
$afterRefusal = Get-State
$assertions.Add(
    'L2-SR-02',
    '没确认已查明原因的请求被拒（409，FAULT_RECOVERY_REMEDY_NOT_CONFIRMED），旅程与单数不变',
    ($unconfirmed.Status -eq 409 -and (Get-Reasons $unconfirmed) -contains 'FAULT_RECOVERY_REMEDY_NOT_CONFIRMED' -and
        $afterRefusal.Code -eq 'OWN_ORDER_REBUILD_STOPPED' -and $afterRefusal.Intents -eq 2),
    '409 FAULT_RECOVERY_REMEDY_NOT_CONFIRMED / OWN_ORDER_REBUILD_STOPPED / 2 张',
    "$($unconfirmed.Status) $((Get-Reasons $unconfirmed) -join ',') / $($afterRefusal.Code) / $($afterRefusal.Intents) 张")

# --- 3. 署名、确认的请求：受理 -----------------------------------------------------------------------------

$accepted = Invoke-Exit @{} 'rebuild'
$reopened = Invoke-Scalar "SELECT State, StoppedReason, OperatorId FROM OwnOrderRebuilds WHERE EndedUpperId = '$([string]$rebuilt.UpperId)'"
$assertions.Add(
    'L2-SR-03',
    '署名、确认的人工重建被受理（200 RebuildRequested / REBUILD_SCHEDULED），停住的记录原行重开、署上这个人、保留停住原因',
    ($accepted.Status -eq 200 -and (Get-Field $accepted 'outcome') -eq 'RebuildRequested' -and
        (Get-Field $accepted 'disposition') -eq 'REBUILD_SCHEDULED' -and $null -ne $reopened -and
        [string]$reopened.OperatorId -eq 'L2-OPERATOR-345' -and [string]$reopened.StoppedReason -eq 'REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW' -and
        [string]$reopened.State -in @('PENDING', 'ORDERING', 'REBUILT')),
    '200 RebuildRequested REBUILD_SCHEDULED / L2-OPERATOR-345 / REBUILT_ORDER_ENDED_AGAIN_WITHIN_WINDOW / PENDING|ORDERING|REBUILT',
    "$($accepted.Status) $(Get-Field $accepted 'outcome') $(Get-Field $accepted 'disposition') / $(if ($reopened) { "$($reopened.OperatorId) / $($reopened.StoppedReason) / $($reopened.State)" } else { '(没有记录)' })")

# --- 4. 引擎建第三张单：同车同需求、同一个取货停靠 ------------------------------------------------------------

$third = Wait-L2ConditionOrLast -Description 'a third order was made for the same vehicle and demand, and confirmed' `
    -Journal $journal -Criterion 'person-rebuild' -TimeoutSeconds 60 `
    -Probe {
        Invoke-Scalar ("SELECT s.Sequence, s.StationId, s.UpperId, o.DemandId, o.VehicleKey, o.DestinationStationId, o.Status, " +
            "r.Stage, r.BlockReasonCode FROM JourneyStops s JOIN OrderIntents o ON o.UpperId = s.UpperId " +
            "JOIN JourneyRuntimes r ON r.JourneyId = s.JourneyId WHERE s.StopId = '$([string]$pickupStop.StopId)'")
    } `
    -Until { param($v) $v -and [string]$v.UpperId -ne [string]$rebuilt.UpperId -and [string]$v.Status -eq 'CONFIRMED' }
$riotOrder = @($riot.Snapshot().body.orders | Where-Object { $third -and [string]$_.upperId -eq [string]$third.UpperId })
$assertions.Add(
    'L2-SR-04',
    '人工重建之后：取货停靠（同序位、同站）改指向第三张服务端自己的单，意图已确认，需求与车不变、目标站不变，旅程码清掉；RIoT 上有这张单、指派给同一辆车',
    ($null -ne $third -and [string]$third.UpperId -ne [string]$rebuilt.UpperId -and [string]$third.UpperId -ne [string]$first.PickupUpperId -and
        [string]$third.UpperId -like 'W2G-*' -and [string]$third.Status -eq 'CONFIRMED' -and [string]$third.DemandId -eq $demandId -and
        [string]$third.VehicleKey -eq [string]$first.VehicleKey -and [int]$third.DestinationStationId -eq [int]$first.DestinationStationId -and
        [int]$third.Sequence -eq [int]$pickupStop.Sequence -and [string]$third.StationId -eq [string]$pickupStop.StationId -and
        [string]$third.Stage -eq 'AwaitingPickupArrival' -and [string]::IsNullOrEmpty([string]$third.BlockReasonCode) -and
        $riotOrder.Count -eq 1 -and [string]$riotOrder[0].appointVehicleKey -eq [string]$first.VehicleKey),
    "第三张 W2G-… / CONFIRMED / $demandId / $($first.VehicleKey) / 站 $($first.DestinationStationId) / 序位 $($pickupStop.Sequence) / AwaitingPickupArrival (无码) / RIoT 1 张",
    $(if ($null -eq $third) { '(停靠读不到)' } else {
        "$($third.UpperId) / $($third.Status) / $($third.DemandId) / $($third.VehicleKey) / 站 $($third.DestinationStationId) / 序位 $($third.Sequence) / $($third.Stage) ($($third.BlockReasonCode)) / RIoT $($riotOrder.Count) 张" }))

# --- 5. 同一请求再来一次：AlreadyDone，不再建单；全程没有订单命令 ----------------------------------------------

$again = Invoke-Exit @{} 'rebuild again'
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$final = Get-State
$audit = Get-Count 'SELECT COUNT(*) AS N FROM RiotOrderCommandAudit'
$calls = @($riot.Snapshot().body.commandInvocations).Count
$assertions.Add(
    'L2-SR-05',
    '同一请求再来一次：200 AlreadyDone，不再建单（恰好三张意图、一趟旅程、需求未移除）；全程没有发出任何订单命令或急停',
    ($again.Status -eq 200 -and (Get-Field $again 'outcome') -eq 'AlreadyDone' -and $final.Intents -eq 3 -and $final.Journeys -eq 1 -and
        $final.Removed -eq 0 -and $audit -eq 0 -and $calls -eq 0),
    '200 AlreadyDone / 3 张 / 1 趟 / 未移除 / 0 条命令 / 0 次调用',
    "$($again.Status) $(Get-Field $again 'outcome') / $($final.Intents) 张 / $($final.Journeys) 趟 / 移除 $($final.Removed) / $audit 条命令 / $calls 次调用")

$journal.Note('护栏三停住之后，人经 #299 的入口人工重建一次：未确认的被拒，确认的被受理，同车同需求建出第三张单，重复请求不再建。')
