#Requires -Version 7

<#
在途单挂起（9 HANG）后被人在 RIoT 里 continue（control-server#316，#299 拆出的 T1；用户 2026-09-22 定 H-a）。

车正开往取货站，RIoT 把这张单报成 9 HANG——实验室 BC-ORDER-015 的单机取消、解抱闸、执行中关机再开机都会走到这里，
而且 RIoT 此后不再驱动它，只有人 continue 或取消才能往下走。服务端要：
  1. 在旅程上写 ORDER_HANG，让看板阻断卡片列出这一行（修之前阻断码为空，旅程静默停在开往取货站）；
  2. 除此之外什么都不做：不发 OrderHold、不发急停、不记车辆故障事实——三样都按「零」判，而且是在运行时转过几轮之后判；
  3. 人 continue 之后（订单回到 3）清掉这个码，车到站后旅程照常往下走。

为什么「什么都不做」本身是判据：把 9 原样交给故障协调器，车停在两站之间会被升级急停，闩锁下 RIoT 拒绝 continue，
人工解除又因 HANG 算未完成订单而被拒——#299 阶段一方案第三节的死锁。本条的负判据就是在守那条路没被接上。

红证据（修复之前的 fp/v2-impl）：L2-OH-01 等不到 ORDER_HANG，超时。
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

$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')

function Invoke-Scalar([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Runtime {
    return Invoke-Scalar "SELECT JourneyId, Stage, BlockReasonCode, BlockReasonSince, PickupUpperId FROM JourneyRuntimes WHERE DemandId = '$demandId'"
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
    sublot = "L2-OH-$($Context.RunId)"; area = 'N1-3'
    eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})

$intent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 120 `
    -Probe {
        $row = Invoke-Scalar "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'"
        if ($row -and [string]$row.Status -eq 'CONFIRMED') { $row } else { $null }
    } `
    -Until { param($v) $null -ne $v }

$journal.Note('Vehicle departs for the pickup station.')
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'PROCESSING_ORDER'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = $intent.OrderId; currentPosition = 0
})
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal

$before = Get-CommandCounts
$journal.Observe('commands-before-hang', "$($before.Audit)/$($before.Faults)/$($before.Calls)",
    @{ audit = $before.Audit; faults = $before.Faults; calls = $before.Calls })

# --- 2. 两站之间挂起：RIoT 停止执行这张单 -------------------------------------------------------------

# 车态照实验室 Round34 的样子：INNER_FORCE_IDLE、不动、不在任何站上。「不在站上」是要紧的那一格——故障协调器
# 对它一律升级急停，所以它最能看出服务端有没有把 9 接进故障模型。
$journal.Note("RIoT reports $($intent.UpperId) HANG between stations.")
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 9 })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'INNER_FORCE_IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = 0
})

$hang = Wait-L2Condition -Description 'the journey names the hanging order' `
    -Journal $journal -Criterion 'order-hang-named' -TimeoutSeconds 60 `
    -Probe { Get-Runtime } `
    -Until { param($v) $v -and [string]$v.BlockReasonCode -eq 'ORDER_HANG' }
$assertions.Add(
    'L2-OH-01',
    '订单挂起后旅程写 ORDER_HANG（看板阻断卡片据此列出这一行）',
    ([string]$hang.BlockReasonCode -eq 'ORDER_HANG'),
    'ORDER_HANG',
    [string]$hang.BlockReasonCode)

# 负判据要有界：转过几轮再数，只读一次会在服务端还没来得及做错事时误绿。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$during = Get-CommandCounts
$held = Get-Runtime
$assertions.Add(
    'L2-OH-02',
    '挂起期间没有发出任何订单命令或急停：审计表零行、假 RIoT 零调用',
    ($during.Audit -eq 0 -and $during.Calls -eq 0),
    '0 / 0',
    "$($during.Audit) / $($during.Calls)")
$assertions.Add(
    'L2-OH-03',
    '挂起不记车辆故障事实（H-a：不进两级故障模型）',
    ($during.Faults -eq 0),
    0,
    $during.Faults)
$assertions.Add(
    'L2-OH-04',
    '旅程仍停在开往取货站，码与开始时刻不变（没有被重写、没有被清掉）',
    ([string]$held.Stage -eq 'AwaitingPickupArrival' -and [string]$held.BlockReasonCode -eq 'ORDER_HANG' -and
        [string]$held.BlockReasonSince -eq [string]$hang.BlockReasonSince),
    "AwaitingPickupArrival / ORDER_HANG / $($hang.BlockReasonSince)",
    "$($held.Stage) / $($held.BlockReasonCode) / $($held.BlockReasonSince)")

# --- 3. 人在 RIoT 里 continue：订单回到执行 ---------------------------------------------------------

$journal.Note('A person continues the order in RIoT.')
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3 })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'PROCESSING_ORDER'; movementState = 'MT_RUNNING'; speed = 0.8
    currentPosition = 0
})

$resumed = Wait-L2Condition -Description 'the reason is cleared once the order runs again' `
    -Journal $journal -Criterion 'order-hang-cleared' -TimeoutSeconds 60 `
    -Probe { Get-Runtime } `
    -Until { param($v) $v -and ($null -eq $v.BlockReasonCode -or [string]$v.BlockReasonCode -eq '') }
$assertions.Add(
    'L2-OH-05',
    'continue 之后码清掉，旅程仍在开往取货站',
    ([string]$resumed.Stage -eq 'AwaitingPickupArrival'),
    'AwaitingPickupArrival / (无码)',
    "$($resumed.Stage) / $($resumed.BlockReasonCode)")

# --- 4. 车到站，旅程照常往下走 ------------------------------------------------------------------------

$journal.Note('Vehicle arrives at the pickup station and comes to rest.')
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.PickupStationRiotId; processingOrder = $false; clearOrderTaskId = $true
})

$stage = Wait-L2Condition -Description 'the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { [string](Get-Runtime).Stage } `
    -Until { param($v) $v -eq 'AwaitingGateArrival' }
$assertions.Add(
    'L2-OH-06',
    '到站、装载、出发前检查照常走完，旅程进入去关卡那一段',
    ($stage -eq 'AwaitingGateArrival'),
    'AwaitingGateArrival',
    $stage)

$after = Get-CommandCounts
$assertions.Add(
    'L2-OH-07',
    '整个过程没有一条订单命令、急停或故障事实',
    ($after.Audit -eq 0 -and $after.Calls -eq 0 -and $after.Faults -eq 0),
    '0 / 0 / 0',
    "$($after.Audit) / $($after.Calls) / $($after.Faults)")

$journal.Note('挂起被看见、没有被当成故障，continue 之后旅程照常到站。')
