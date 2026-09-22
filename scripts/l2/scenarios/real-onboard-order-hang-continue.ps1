#Requires -Version 7

<#
真车载端在场时，在途单挂起（9 HANG）后被人在 RIoT 里 continue（control-server#316）。

与合成场景 `in-transit-order-hang-continue` 同一条业务链路，差别只有车载端是出厂的那个 WPF。这一差别正是它存在的理由：
真车载端在车有本服务端的在途单时读 RIoT 安全接口，报 VEHICLE_NOT_READY，会话整段在途都停在
DEPARTURE_SAFETY_NOT_READY——9 属于未终结状态，挂起期间也一样。第一版的 ORDER_HANG 只写在会话就绪才走得到的在途分支里，
在真车载端上一次都没写出来；合成车载端永远报安全，合成 L2 按构造看不见这一点（调度独立审查高项）。

判据：
  1. 挂起期间旅程写 ORDER_HANG，且这件事发生在会话未就绪的时候（L2-ROH-02 把「会话确实没就绪」当成前提断言：
     它不成立，这条场景就没证明它要证明的东西）；
  2. 挂起期间没有一条订单命令、急停，也没有故障事实；
  3. continue 之后码离开 ORDER_HANG，车到站后车载端允许录入 sublot——旅程照常往下走。

断言只从服务端 SQLite 与假 RIoT 读；UI 只用来读「现在允不允许录入」。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')

function Invoke-Scalar([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Runtime {
    return Invoke-Scalar "SELECT Stage, BlockReasonCode, BlockReasonSince FROM JourneyRuntimes WHERE DemandId = '$demandId'"
}

function Get-CommandCounts {
    $audit = [int](Invoke-Scalar 'SELECT COUNT(*) AS N FROM RiotOrderCommandAudit').N
    $faults = [int](Invoke-Scalar 'SELECT COUNT(*) AS N FROM VehicleFaultStates').N
    $calls = @($riot.Snapshot().body.commandInvocations).Count
    return [pscustomobject]@{ Audit = $audit; Faults = $faults; Calls = $calls }
}

# --- 1. 受理、派往取货站 ------------------------------------------------------------------------------

$journal.Note("Publishing demand $($demandGuid.ToString('N')) (area N1-3).")
$null = $mes.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = "L2-ROH-$($Context.RunId)"; area = 'N1-3'
    eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})

$intent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 180 `
    -Probe {
        $row = Invoke-Scalar "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'"
        if ($row -and [string]$row.Status -eq 'CONFIRMED') { $row } else { $null }
    } `
    -Until { param($v) $null -ne $v }

$journal.Note('Vehicle departs for the pickup station.')
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = $intent.OrderId; currentPosition = 0
})
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal

# --- 2. 两站之间挂起 ------------------------------------------------------------------------------------

$journal.Note("RIoT reports $($intent.UpperId) HANG between stations.")
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 9 })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'INNER_FORCE_IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = 0
})

$hang = Wait-L2Condition -Description 'the journey names the hanging order' `
    -Journal $journal -Criterion 'order-hang-named' -TimeoutSeconds 90 `
    -Probe { Get-Runtime } `
    -Until { param($v) $v -and [string]$v.BlockReasonCode -eq 'ORDER_HANG' }
$session = Invoke-Scalar "SELECT Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'"
$journal.Observe('session-during-hang', "$($session.Readiness)/$($session.ReasonCode)",
    @{ readiness = [string]$session.Readiness; reasonCode = [string]$session.ReasonCode })

$assertions.Add(
    'L2-ROH-01',
    '真车载端在场时，订单挂起后旅程写 ORDER_HANG',
    ([string]$hang.BlockReasonCode -eq 'ORDER_HANG'),
    'ORDER_HANG',
    [string]$hang.BlockReasonCode)
$assertions.Add(
    'L2-ROH-02',
    '前提：挂起期间会话未就绪（真车载端看到本服务端自己的在途单），所以这条走的是会话闸门那条路',
    ([string]$session.Readiness -ne 'Ready'),
    '非 Ready',
    "$($session.Readiness) / $($session.ReasonCode)")

$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$during = Get-CommandCounts
$held = Get-Runtime
$assertions.Add(
    'L2-ROH-03',
    '挂起期间没有订单命令、急停或故障事实；码与开始时刻不变',
    ($during.Audit -eq 0 -and $during.Calls -eq 0 -and $during.Faults -eq 0 -and
        [string]$held.BlockReasonCode -eq 'ORDER_HANG' -and [string]$held.BlockReasonSince -eq [string]$hang.BlockReasonSince),
    "0 / 0 / 0 / ORDER_HANG / $($hang.BlockReasonSince)",
    "$($during.Audit) / $($during.Calls) / $($during.Faults) / $($held.BlockReasonCode) / $($held.BlockReasonSince)")

# --- 3. continue，到站 ----------------------------------------------------------------------------------

$journal.Note('A person continues the order in RIoT.')
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3 })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = $intent.OrderId; currentPosition = 0
})

$resumed = Wait-L2Condition -Description 'the hang reason is gone once the order runs again' `
    -Journal $journal -Criterion 'order-hang-cleared' -TimeoutSeconds 60 `
    -Probe { Get-Runtime } `
    -Until { param($v) $v -and [string]$v.BlockReasonCode -ne 'ORDER_HANG' }
$assertions.Add(
    'L2-ROH-04',
    'continue 之后码离开 ORDER_HANG（会话仍未就绪时回到 ONBOARD_SESSION_NOT_READY），旅程仍在开往取货站',
    ([string]$resumed.Stage -eq 'AwaitingPickupArrival' -and [string]$resumed.BlockReasonCode -ne 'ORDER_HANG'),
    'AwaitingPickupArrival / 非 ORDER_HANG',
    "$($resumed.Stage) / $($resumed.BlockReasonCode)")

$journal.Note('Vehicle arrives at the pickup station and comes to rest.')
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.PickupStationRiotId; processingOrder = $false; clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })

$null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
$stage = [string](Get-Runtime).Stage
$after = Get-CommandCounts
$assertions.Add(
    'L2-ROH-05',
    '到站后旅程照常推进，车载端允许录入 sublot；全程没有订单命令、急停或故障事实',
    ($stage -eq 'AwaitingSublot' -and $after.Audit -eq 0 -and $after.Calls -eq 0 -and $after.Faults -eq 0),
    'AwaitingSublot / 0 / 0 / 0',
    "$stage / $($after.Audit) / $($after.Calls) / $($after.Faults)")

$journal.Note('真车载端在场、会话因自己的在途单未就绪时，挂起照样被看见；continue 之后旅程照常到站。')
