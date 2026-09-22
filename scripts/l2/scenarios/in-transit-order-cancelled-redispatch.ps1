#Requires -Version 7

<#
开往取货站的单被人在 RIoT 里取消，需求释放改派（control-server#316，#299 拆出的 T1）。

车本身没有任何问题——在线、在本图、准入都在——只是有人在 RIoT 里把这张在途单取消了（挂起后放弃，是这条路最常见的来由）。
修之前服务端对此一无所知：释放的触发只有车辆级事实，「订单被取消」不在其中；引擎读到在途单 CANCELLED 既不算到站也不算失败，
旅程永远停在开往取货站，阻断码为空，需求也永远不回调度。服务端要：
  1. 释放这条还没取货的需求（REQ-0328 的形状：归属标 RELEASED_FOR_REDISPATCH、旅程关闭），**不再发一条取消**——订单已经终结了；
  2. 需求回到调度，重新受理成一趟新旅程：新代次、新 upperId，旧 upperId 不复用。

单车：人取消的时候车本身是合格的，所以改派回同一辆车是正确结果，不是巧合；场景不拿「换了哪辆车」当判据。

红证据（修复之前的 fp/v2-impl）：L2-OC-01 等不到释放，超时。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$mes = $Context.MesIngest
$riot = $Context.Riot

$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')

function Invoke-Scalar([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# --- 1. 受理、派往取货站、车在路上 --------------------------------------------------------------------

$journal.Note("Publishing demand $($demandGuid.ToString('N')) (area N1-3).")
$null = $mes.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = "L2-OC-$($Context.RunId)"; area = 'N1-3'
    eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})

$first = Wait-L2Condition -Description 'the vehicle took the demand and set off to its pickup' `
    -Journal $journal -Criterion 'first-journey' -TimeoutSeconds 120 `
    -Probe {
        Invoke-Scalar ("SELECT r.JourneyId, r.AgvId, r.Stage, r.PickupUpperId, r.DispatchGeneration, o.OrderId, o.Status " +
            "FROM JourneyRuntimes r JOIN OrderIntents o ON o.UpperId = r.PickupUpperId WHERE r.DemandId = '$demandId'")
    } `
    -Until { param($v) $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' -and [string]$v.Status -eq 'CONFIRMED' }

$null = $riot.Command('Put', "orders/$([string]$first.PickupUpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'PROCESSING_ORDER'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = [string]$first.OrderId; currentPosition = 0
})
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal

# --- 2. 人在 RIoT 里取消这张单，车停在路上 ------------------------------------------------------------

$journal.Note("A person cancels $([string]$first.PickupUpperId) in RIoT.")
$null = $riot.Command('Put', "orders/$([string]$first.PickupUpperId)", @{ orderState = 2 })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    processingOrder = $false; clearOrderTaskId = $true; currentPosition = 0
})

$released = Wait-L2Condition -Description 'the demand was released from the journey whose order was cancelled' `
    -Journal $journal -Criterion 'released' -TimeoutSeconds 60 `
    -Probe {
        Invoke-Scalar ("SELECT d.RemovedAt, d.RemovalReason, r.Stage, r.BlockReasonCode " +
            "FROM JourneyDemands d JOIN JourneyRuntimes r ON r.JourneyId = d.JourneyId " +
            "WHERE d.JourneyId = '$([string]$first.JourneyId)' AND d.DemandId = '$demandId'")
    } `
    -Until { param($v) $v -and $null -ne $v.RemovedAt -and [string]$v.RemovedAt -ne '' }

$assertions.Add(
    'L2-OC-01',
    '释放：归属标 RELEASED_FOR_REDISPATCH，那趟旅程关闭（Completed / RELEASED_FOR_REDISPATCH）',
    ([string]$released.RemovalReason -eq 'RELEASED_FOR_REDISPATCH' -and [string]$released.Stage -eq 'Completed' -and
        [string]$released.BlockReasonCode -eq 'RELEASED_FOR_REDISPATCH'),
    'RELEASED_FOR_REDISPATCH / Completed / RELEASED_FOR_REDISPATCH',
    "$($released.RemovalReason) / $($released.Stage) / $($released.BlockReasonCode)")

$cancelAudit = [int](Invoke-Scalar "SELECT COUNT(*) AS N FROM RiotOrderCommandAudit WHERE CommandType = 'CANCEL'").N
$cancelCalls = @(@($riot.Snapshot().body.commandInvocations) |
    Where-Object { [string]$_.commandType -eq 'CMD_ORDER_CANCEL' }).Count
$assertions.Add(
    'L2-OC-02',
    '订单已经终结，服务端没有再发取消：审计表与假 RIoT 上都是零条',
    ($cancelAudit -eq 0 -and $cancelCalls -eq 0),
    '0 / 0',
    "$cancelAudit / $cancelCalls")

# --- 3. 需求回到调度，重新受理 --------------------------------------------------------------------------

$second = Wait-L2Condition -Description 'the released demand was taken again as a new journey' `
    -Journal $journal -Criterion 'second-journey' -TimeoutSeconds 120 `
    -Probe {
        Invoke-Scalar ("SELECT r.JourneyId, r.AgvId, r.Stage, r.PickupUpperId, r.DispatchGeneration " +
            "FROM JourneyRuntimes r WHERE r.DemandId = '$demandId' AND r.JourneyId <> '$([string]$first.JourneyId)'")
    } `
    -Until { param($v) $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }

$assertions.Add(
    'L2-OC-03',
    '需求重新受理成新旅程：新代次、新 upperId（旧 upperId 不复用）',
    ([string]$second.PickupUpperId -ne [string]$first.PickupUpperId -and
        [long]$second.DispatchGeneration -gt [long]$first.DispatchGeneration),
    "upperId ≠ $([string]$first.PickupUpperId) / 代次 > $($first.DispatchGeneration)",
    "$($second.PickupUpperId) / 代次 $($second.DispatchGeneration)")

$journal.Note('人在 RIoT 里取消在途单之后，未取货的需求释放改派，服务端没有对已终结的单再发取消。')
