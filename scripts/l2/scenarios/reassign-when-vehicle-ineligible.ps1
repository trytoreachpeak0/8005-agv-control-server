#Requires -Version 7

<#
仅当前车不合格时释放改派（批次7-10，control-server#215；REQ-0328，集合 B、仅限未取货）。

主车 A 接下一条需求、正开往取货站；A 离开本图（RIoT 上 currentMap 换掉）——这是一条稳定的车辆级事实，需求本身
仍然合格。服务端要：
  1. 经订单命令面取消 A 的取货单，并对账确认（合成 RIoT 收到取消只记账不改状态，所以场景在看到那条取消之后
     再把订单置成 CANCELLED——那正是「对账确认」要读到的东西）；
  2. 确认之后释放：归属标 RELEASED_FOR_REDISPATCH、A 的旅程关闭、积压行清掉受理标记，FirstSeenAt 不动；
  3. 需求回到调度，被另一台车 B 受理：新旅程、新代次、新 upperId，旧 upperId 不再出现在任何新订单上；
  4. B 的新旅程派往取货站的那一版计划确实发出了（不是被「已经发过」吞掉——锚需求再受理时，按需求 id 算的消息 id
     会与第一趟撞上，control-server#215 在引擎里把它改成按派生键算）。

判据与第二个事实：取消之后到释放之间隔着一次对账，释放之后到再受理之间隔着一轮派车，每一步都用
Wait-L2Condition 等「下一个事实」出现，不读一次就断言（scripts/l2/README.md 第 14 条）。

红证据（缺陷版本）：改派沿用原 upperId——那样 B 的受理撞上 OrderIntents 的唯一索引，「upperId 不同」与
「B 受理」两条变红。
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

$secondAgvId = 'AGV-L2-002'
$secondVehicleKey = 'BROKERX-L2-0002'
$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')

function Invoke-Scalar([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# --- 1. 第二台车先停用：第一条需求只能落到主车上 ----------------------------------------------------

$null = $riot.Command('Put', 'vehicle', @{ vehicleKey = $secondVehicleKey; enable = $false; currentPosition = 11 })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'IDLE'
    movementState   = 'MT_FINISHED'
    speed           = 0
    currentPosition = $Context.GateStationRiotId
})

$journal.Note("Publishing demand $($demandGuid.ToString('N')) (area N1-3).")
$null = $mes.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = "L2-RVI-$($Context.RunId)"; area = 'N1-3'
    eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})

$first = Wait-L2Condition -Description 'the primary vehicle took the demand and set off to its pickup' `
    -Journal $journal -Criterion 'first-journey' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql (
            "SELECT r.JourneyId, r.AgvId, r.Stage, r.PickupUpperId, r.DispatchGeneration, o.OrderId, o.Status " +
            "FROM JourneyRuntimes r JOIN OrderIntents o ON o.UpperId = r.PickupUpperId WHERE r.DemandId = '$demandId'")
        if ($rows.Count -eq 0) { return $null }
        return $rows[0]
    } `
    -Until { param($v) $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' -and [string]$v.Status -eq 'CONFIRMED' }

$assertions.Add(
    'L2-RVI-01',
    '主车接下需求，正开往取货站（取货单已确认）',
    ([string]$first.AgvId -eq $Context.AgvId),
    $Context.AgvId,
    [string]$first.AgvId)

$firstSeen = [string](Invoke-Scalar "SELECT FirstSeenAt FROM JourneyBacklog WHERE DemandId = '$demandId'").FirstSeenAt
$journal.Observe('first-seen-before-release', $firstSeen, @{ firstSeenAt = $firstSeen })

# --- 2. 主车离开本图：服务端取消它的取货单 ----------------------------------------------------------

$journal.Note('The primary vehicle leaves the map (RIOT_VEHICLE_MAP_MISMATCH).')
$null = $riot.Command('Put', 'vehicle', @{ vehicleKey = $Context.VehicleKey; currentMap = 'MAP-L2-ELSEWHERE' })

$cancelSeen = Wait-L2Condition -Description 'the fake RIoT received a cancel for the primary pickup order' `
    -Journal $journal -Criterion 'riot-cancel-received' -TimeoutSeconds 60 `
    -Probe {
        @(@($riot.Snapshot().body.commandInvocations) |
            Where-Object { [string]$_.commandType -eq 'CMD_ORDER_CANCEL' -and [string]$_.target -eq [string]$first.OrderId }).Count
    } `
    -Until { param($v) $v -ge 1 }

$assertions.Add(
    'L2-RVI-02',
    '线上收到一条 CMD_ORDER_CANCEL，打在主车那张取货单上',
    ($cancelSeen -ge 1),
    "CMD_ORDER_CANCEL @ $([string]$first.OrderId)",
    "$cancelSeen 条")

# 合成 RIoT 收到取消只记账：把订单置成 CANCELLED，服务端下一轮对账才读得到「确实取消了」。
$null = $riot.Command('Put', "orders/$([string]$first.PickupUpperId)", @{ orderState = 2 })

# --- 3. 对账确认之后释放，退回积压 ------------------------------------------------------------------

$released = Wait-L2Condition -Description 'the demand was released from the primary vehicle' `
    -Journal $journal -Criterion 'released' -TimeoutSeconds 60 `
    -Probe {
        Invoke-Scalar ("SELECT d.RemovedAt, d.RemovalReason, r.Stage, r.BlockReasonCode " +
            "FROM JourneyDemands d JOIN JourneyRuntimes r ON r.JourneyId = d.JourneyId " +
            "WHERE d.JourneyId = '$([string]$first.JourneyId)' AND d.DemandId = '$demandId'")
    } `
    -Until { param($v) $v -and $null -ne $v.RemovedAt -and [string]$v.RemovedAt -ne '' }

$assertions.Add(
    'L2-RVI-03',
    '释放：归属标 RELEASED_FOR_REDISPATCH，主车的旅程关闭（Completed / RELEASED_FOR_REDISPATCH）',
    ([string]$released.RemovalReason -eq 'RELEASED_FOR_REDISPATCH' -and [string]$released.Stage -eq 'Completed' -and
        [string]$released.BlockReasonCode -eq 'RELEASED_FOR_REDISPATCH'),
    'RELEASED_FOR_REDISPATCH / Completed / RELEASED_FOR_REDISPATCH',
    "$($released.RemovalReason) / $($released.Stage) / $($released.BlockReasonCode)")

$cancelAudit = Invoke-Scalar ("SELECT COUNT(*) AS N, MAX(Outcome) AS Outcome FROM RiotOrderCommandAudit " +
    "WHERE CommandType = 'CANCEL' AND TargetUpperId = '$([string]$first.PickupUpperId)'")
$assertions.Add(
    'L2-RVI-04',
    '取消只发了一次，审计行对账结果是 Confirmed（结果未知不释放）',
    ([int]$cancelAudit.N -eq 1 -and [string]$cancelAudit.Outcome -eq 'Confirmed'),
    '1 / Confirmed',
    "$($cancelAudit.N) / $($cancelAudit.Outcome)")

# --- 4. 第二台车启用，需求被它受理 --------------------------------------------------------------------

$journal.Note('Enabling the second vehicle.')
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $secondVehicleKey; enable = $true; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
})

$second = Wait-L2Condition -Description 'the second vehicle took the released demand' `
    -Journal $journal -Criterion 'second-journey' -TimeoutSeconds 120 `
    -Probe {
        Invoke-Scalar ("SELECT r.JourneyId, r.AgvId, r.Stage, r.PickupUpperId, r.PickupMovementLegId, r.DispatchGeneration " +
            "FROM JourneyRuntimes r WHERE r.DemandId = '$demandId' AND r.JourneyId <> '$([string]$first.JourneyId)'")
    } `
    -Until { param($v) $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }

$assertions.Add(
    'L2-RVI-05',
    '需求回到调度后由第二台车受理：新旅程、新代次、新 upperId（旧 upperId 不复用）',
    ([string]$second.AgvId -eq $secondAgvId -and
        [string]$second.PickupUpperId -ne [string]$first.PickupUpperId -and
        [long]$second.DispatchGeneration -gt [long]$first.DispatchGeneration),
    "$secondAgvId / upperId ≠ $([string]$first.PickupUpperId) / 代次 > $($first.DispatchGeneration)",
    "$($second.AgvId) / $($second.PickupUpperId) / 代次 $($second.DispatchGeneration)")

$oldUpperIdOrders = [int](Invoke-Scalar ("SELECT COUNT(*) AS N FROM OrderIntents WHERE UpperId = '$([string]$first.PickupUpperId)'")).N
$assertions.Add(
    'L2-RVI-06',
    '旧 upperId 只属于第一趟那一张订单，没有出现在任何新订单上',
    ($oldUpperIdOrders -eq 1),
    1,
    $oldUpperIdOrders)

$ageAfter = Invoke-Scalar "SELECT FirstSeenAt, AcceptedAt FROM JourneyBacklog WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-RVI-07',
    '等待年龄没有重置：积压行的 FirstSeenAt 与释放之前相同，再受理后又带上受理时刻',
    ([string]$ageAfter.FirstSeenAt -eq $firstSeen -and $null -ne $ageAfter.AcceptedAt -and [string]$ageAfter.AcceptedAt -ne ''),
    "$firstSeen / 有受理时刻",
    "$($ageAfter.FirstSeenAt) / $(if ($null -eq $ageAfter.AcceptedAt -or [string]$ageAfter.AcceptedAt -eq '') { '无受理时刻' } else { '有受理时刻' })")

# --- 5. 新旅程派往取货站的计划确实发出了 --------------------------------------------------------------

# 判据是计划里带着新旅程取货腿的 movementLegId：第一趟那一版里是旧的腿，找到新腿才说明新旅程自己的那一版发了。
$planSent = Wait-L2Condition -Description "the second journey's dispatch plan was published" `
    -Journal $journal -Criterion 'second-plan' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'UpcomingStopPlanSnapshot'"
        @($rows | Where-Object {
                @((ConvertFrom-Json ([string]$_.PayloadJson)).payload.legs |
                    Where-Object { [string]$_.movementLegId -eq [string]$second.PickupMovementLegId }).Count -gt 0
            }).Count
    } `
    -Until { param($v) $v -ge 1 }

$assertions.Add(
    'L2-RVI-08',
    '第二趟派往取货站的那一版计划发出了，带着新取货腿（没有被「已发过」吞掉）',
    ($planSent -ge 1),
    '≥ 1',
    $planSent)

$journal.Note('需求从不合格的车上释放、退回积压，由另一台车以新代次与新 upperId 受理，等待年龄保留。')
