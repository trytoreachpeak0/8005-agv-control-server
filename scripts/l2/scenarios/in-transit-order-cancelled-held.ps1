#Requires -Version 7

<#
开往取货站的单被人在 RIoT 里取消：旅程说出原因，服务端什么都不做（control-server#316，#299 拆出的 T1）。

用户 2026-09-22 更正：RIoT 里取消订单不是正常操作，多半是误操作，不该改派，该重建订单。怎么重建（自动还是等人确认、
是否同车、已装货与未装货是否一样）另行决定；在那之前，本票把这种情况定为「看得见、不动」：
  1. 旅程写 ORDER_ENDED_WITHOUT_ARRIVAL，看板阻断卡片列出这一行（修之前阻断码为空，旅程静默停在开往取货站）；
  2. 不释放、不改派、不重建：需求的归属不动，没有第二趟旅程，也没有第二张移动单；
  3. 也不对这张已终结的单再发取消，或任何订单命令、急停。

负判据要有界：转过几轮之后再判，只读一次会在服务端还没来得及做错事时误绿。

红证据（修复之前的 fp/v2-impl）：L2-OC-01 等不到码，超时。
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

$named = Wait-L2Condition -Description 'the journey names the order that ended without an arrival' `
    -Journal $journal -Criterion 'order-ended-named' -TimeoutSeconds 60 `
    -Probe {
        Invoke-Scalar "SELECT Stage, BlockReasonCode, BlockReasonSince FROM JourneyRuntimes WHERE JourneyId = '$([string]$first.JourneyId)'"
    } `
    -Until { param($v) $v -and [string]$v.BlockReasonCode -eq 'ORDER_ENDED_WITHOUT_ARRIVAL' }
$assertions.Add(
    'L2-OC-01',
    '订单被取消后旅程写 ORDER_ENDED_WITHOUT_ARRIVAL，仍停在开往取货站',
    ([string]$named.Stage -eq 'AwaitingPickupArrival' -and [string]$named.BlockReasonCode -eq 'ORDER_ENDED_WITHOUT_ARRIVAL'),
    'AwaitingPickupArrival / ORDER_ENDED_WITHOUT_ARRIVAL',
    "$($named.Stage) / $($named.BlockReasonCode)")

$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

$membership = Invoke-Scalar ("SELECT RemovedAt, RemovalReason FROM JourneyDemands " +
    "WHERE JourneyId = '$([string]$first.JourneyId)' AND DemandId = '$demandId'")
$journeys = [int](Invoke-Scalar "SELECT COUNT(*) AS N FROM JourneyRuntimes WHERE DemandId = '$demandId'").N
$intents = [int](Invoke-Scalar "SELECT COUNT(*) AS N FROM OrderIntents WHERE DemandId = '$demandId'").N
$assertions.Add(
    'L2-OC-02',
    '不释放、不改派、不重建：归属未移除，仍只有一趟旅程、一张移动意图',
    (($null -eq $membership.RemovedAt -or [string]$membership.RemovedAt -eq '') -and $journeys -eq 1 -and $intents -eq 1),
    '未移除 / 1 趟 / 1 张',
    "$(if ($null -eq $membership.RemovedAt -or [string]$membership.RemovedAt -eq '') { '未移除' } else { "已移除 $($membership.RemovalReason)" }) / $journeys 趟 / $intents 张")

$audit = [int](Invoke-Scalar 'SELECT COUNT(*) AS N FROM RiotOrderCommandAudit').N
$calls = @($riot.Snapshot().body.commandInvocations).Count
$held = Invoke-Scalar "SELECT Stage, BlockReasonCode, BlockReasonSince FROM JourneyRuntimes WHERE JourneyId = '$([string]$first.JourneyId)'"
$assertions.Add(
    'L2-OC-03',
    '没有发出任何订单命令或急停（包括对已终结的单再发取消），码与开始时刻不变',
    ($audit -eq 0 -and $calls -eq 0 -and [string]$held.BlockReasonCode -eq 'ORDER_ENDED_WITHOUT_ARRIVAL' -and
        [string]$held.BlockReasonSince -eq [string]$named.BlockReasonSince),
    "0 / 0 / ORDER_ENDED_WITHOUT_ARRIVAL / $($named.BlockReasonSince)",
    "$audit / $calls / $($held.BlockReasonCode) / $($held.BlockReasonSince)")

$journal.Note('人在 RIoT 里取消在途单之后，旅程说出原因，服务端不释放、不改派、不重建、不发命令，等人处置。')
