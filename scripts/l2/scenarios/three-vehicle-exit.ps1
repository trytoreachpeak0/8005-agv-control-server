#Requires -Version 7

<#
轨 B 的出口场景：三台车同时被派单、同时装货、同时走完 journey。

`three-synthetic-peers` 证到「三台车同时有 Ready 会话」为止——那是会话这一层。这条从那里往下
接：三条会话上各跑一趟完整的 WIRE_TO_GATE，取货、装载、出发前安全检查、到关卡、卸载、终态。

**为什么必须是同一次运行里的三台，而不是跑三次单车。**多车下最先炸的东西不在单车路径上：
三台车面对同一份 MesIngest 目录、用同一个全序排序候选，天然会选中同一个需求；一轮之内先决
定的那台车必须让后面的车看见它已经拿走了什么，否则第二台车进 intake 就撞唯一性、整轮 fail
closed。跑三次单车永远碰不到这件事。

同理，「一台车的判据不会拿另一台车的事实来判」也只有在三台车的事实同时存在时才有意义。

**这条场景不注入任何故障。**命令面与故障隔离的证据在 `command-surface-order-hold`，引擎陈旧
态在 `route-graph-staleness`，`FP-C13` 的负向证据在 `create-gate-unapproved`。四条合起来是规格
8.3 轨 B 那一行的四项出口。
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

$expectedAgvIds = @('AGV-L2-001', 'AGV-L2-002', 'AGV-L2-003')

# 三个需求，三台车。同一个区号是有意的：现场就是几台车服务同一片区，而「三台车不会选中同一个
# 需求」这件事，只有在候选完全对称时才真的被证到——给每台车一个只有它够得着的区，等于把题目
# 提前解掉。
$demands = @()
foreach ($index in 0..2) {
    $guid = [guid]::NewGuid()
    $demands += [pscustomobject]@{
        Wire   = $guid.ToString('N')
        Id     = $guid.ToString('D')
        Sublot = "L2-3V-$($Context.RunId)-$index"
    }
}

function Get-Journeys {
    # 直接赋值，不要包 @()：Invoke-L2Query 以 `return , $rows` 返回整张结果集，再包一层拿到的
    # 是「一个元素、那个元素是整张结果集」，`$_.AgvId` 于是成员展开成三个值。单行时看不出来。
    return Invoke-L2Query -Connection $connection -Sql @'
SELECT AgvId, VehicleKey, DemandId, Stage, PickupUpperId, GateUpperId,
       PickupStationRiotId, GateStationRiotId, BlockReasonCode
FROM JourneyRuntimes ORDER BY AgvId
'@
}

function Get-JourneyOf([string]$agvId) {
    $rows = Get-Journeys
    return $rows | Where-Object { [string]$_.AgvId -eq $agvId } | Select-Object -First 1
}

function Get-StageOf([string]$agvId) {
    $row = Get-JourneyOf $agvId
    if ($null -eq $row) { return $null }
    return [string]$row.Stage
}

function Get-IntentOf([string]$demandId, [string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 一台车走完一段路：RIoT 把单置为执行中并绑车，车动起来，然后停在目的站上，单置为完成。
# 与 normal-load 逐字同形，只是按 vehicleKey 定向——多车下唯一变的就是这个。
function Move-Vehicle {
    param(
        [Parameter(Mandatory)][string]$VehicleKey,
        [Parameter(Mandatory)][object]$Intent,
        [Parameter(Mandatory)][int]$DestinationStationId
    )

    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{
        orderState        = 3
        executeVehicleKey = $VehicleKey
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey      = $VehicleKey
        procState       = 'RUNNING'
        movementState   = 'MT_RUNNING'
        speed           = 0.8
        processingOrder = $true
        orderTaskId     = $Intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey       = $VehicleKey
        procState        = 'IDLE'
        movementState    = 'MT_FINISHED'
        speed            = 0
        currentPosition  = $DestinationStationId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 5 })
}

# --- 1. 三个需求进目录，三台车各拿一个 -------------------------------------------------------------

foreach ($demand in $demands) {
    $journal.Note("Publishing demand $($demand.Wire) (sublot $($demand.Sublot)).")
    $null = $mes.Command('Put', "demands/$($demand.Wire)", @{
        sublot      = $demand.Sublot
        area        = 'N1-3'
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

$dispatched = Wait-L2Condition -Description 'all three vehicles took a journey' `
    -Journal $journal -Criterion 'journeys-dispatched' -TimeoutSeconds 120 `
    -Probe { (Get-Journeys).Count } -Until { param($v) $v -ge 3 }

$journeys = Get-Journeys
$actualAgvIds = @($journeys | ForEach-Object { [string]$_.AgvId } | Sort-Object)
$assertions.Add(
    'L2-3V-01',
    '三台车各自拿到一趟 journey',
    (($actualAgvIds -join ',') -eq ($expectedAgvIds -join ',')),
    ($expectedAgvIds -join ','),
    ($actualAgvIds -join ','))

# 三个需求互不相同，才说明「一轮之内先决定的车让后面的车看见它拿走了什么」真的生效了。
# 这一条如果红，读到的会是三台车共用一个 DemandId——那正是活集合退化成快照的样子。
$distinctDemands = @($journeys | ForEach-Object { [string]$_.DemandId } | Sort-Object -Unique)
$assertions.Add(
    'L2-3V-02',
    '三趟 journey 落在三个互不相同的需求上',
    ($distinctDemands.Count -eq 3),
    3,
    $distinctDemands.Count)

$distinctVehicleKeys = @($journeys | ForEach-Object { [string]$_.VehicleKey } | Sort-Object -Unique)
$assertions.Add(
    'L2-3V-03',
    '三趟 journey 落在三把互不相同的 vehicleKey 上',
    ($distinctVehicleKeys.Count -eq 3),
    3,
    $distinctVehicleKeys.Count)

# 派单是逐车判的，所以每台车都该有一张自己的 TO_PICKUP 单且已确认。
foreach ($journey in $journeys) {
    $agvId = [string]$journey.AgvId
    $status = Wait-L2Condition -Description "the TO_PICKUP intent for $agvId was confirmed" `
        -Journal $journal -Criterion "to-pickup-$agvId" -TimeoutSeconds 90 `
        -Probe {
            $row = Get-IntentOf ([string]$journey.DemandId) 'TO_PICKUP'
            if ($row) { [string]$row.Status } else { $null }
        } `
        -Until { param($v) $v -eq 'CONFIRMED' }
    $assertions.Add(
        "L2-3V-04-$agvId",
        "$agvId 的 TO_PICKUP 单已建并确认",
        ($status -eq 'CONFIRMED'),
        'CONFIRMED',
        $status)
}

# --- 2. 三台车同时开到取货点 -----------------------------------------------------------------------

# 三台车一起动，而不是一台走完再动下一台：出口要证的是它们能同时在途，而不是能轮流在途。
$journal.Note('All three vehicles depart for their pickup stations.')
foreach ($journey in $journeys) {
    $intent = Get-IntentOf ([string]$journey.DemandId) 'TO_PICKUP'
    Move-Vehicle -VehicleKey ([string]$journey.VehicleKey) -Intent $intent `
        -DestinationStationId ([int]$journey.PickupStationRiotId)
}

foreach ($agvId in $expectedAgvIds) {
    $stage = Wait-L2Condition -Description "$agvId loaded and moved on to the gate leg" `
        -Journal $journal -Criterion "stage-$agvId" -TimeoutSeconds 180 `
        -Probe { Get-StageOf $agvId } -Until { param($v) $v -eq 'AwaitingGateArrival' }
    $assertions.Add(
        "L2-3V-05-$agvId",
        "$agvId 到站取货、装载提交、出发前安全检查通过，进入关卡段",
        ($stage -eq 'AwaitingGateArrival'),
        'AwaitingGateArrival',
        $stage)
}

# 装载是逐车逐需求的，三条各一行 Committed。合并成一条数总数看不出是哪台车没装上。
foreach ($journey in $journeys) {
    $agvId = [string]$journey.AgvId
    $rows = Invoke-L2Query -Connection $connection -Sql @"
SELECT Status FROM StationOperations
WHERE DemandId = '$([string]$journey.DemandId)' AND OperationType = 'Load'
"@
    $assertions.Add(
        "L2-3V-06-$agvId",
        "$agvId 的装载操作提交（Committed）",
        ($rows.Count -eq 1 -and [string]$rows[0].Status -eq 'Committed'),
        'Committed',
        $(if ($rows.Count -eq 1) { [string]$rows[0].Status } else { '(无装载行)' }))
}

# --- 3. 三台车同时开到关卡并卸载 -------------------------------------------------------------------

$journal.Note('All three vehicles depart for the gate.')
foreach ($journey in $journeys) {
    $intent = Get-IntentOf ([string]$journey.DemandId) 'TO_GATE'
    Move-Vehicle -VehicleKey ([string]$journey.VehicleKey) -Intent $intent `
        -DestinationStationId ([int]$journey.GateStationRiotId)
}

foreach ($agvId in $expectedAgvIds) {
    $stage = Wait-L2Condition -Description "$agvId completed at the gate" `
        -Journal $journal -Criterion "stage-$agvId" -TimeoutSeconds 180 `
        -Probe { Get-StageOf $agvId } -Until { param($v) $v -eq 'Completed' }
    $assertions.Add(
        "L2-3V-07-$agvId",
        "$agvId 的 journey 走到 Completed",
        ($stage -eq 'Completed'),
        'Completed',
        $stage)
}

foreach ($journey in $journeys) {
    $agvId = [string]$journey.AgvId
    $rows = Invoke-L2Query -Connection $connection -Sql @"
SELECT Status FROM StationOperations
WHERE DemandId = '$([string]$journey.DemandId)' AND OperationType = 'Unload'
"@
    $assertions.Add(
        "L2-3V-08-$agvId",
        "$agvId 的卸载操作提交（Committed）",
        ($rows.Count -eq 1 -and [string]$rows[0].Status -eq 'Committed'),
        'Committed',
        $(if ($rows.Count -eq 1) { [string]$rows[0].Status } else { '(无卸载行)' }))
}

$succeeded = Invoke-L2Query -Connection $connection `
    -Sql "SELECT DemandId, Status FROM AcceptedDemands ORDER BY DemandId"
$succeededCount = @($succeeded | Where-Object { [string]$_.Status -eq 'Succeeded' }).Count
$assertions.Add(
    'L2-3V-09',
    '三个需求全部终态 Succeeded',
    ($succeeded.Count -eq 3 -and $succeededCount -eq 3),
    '3 / 3',
    "$succeededCount / $($succeeded.Count)")

# --- 4. 三台车六张单，一张不多 ---------------------------------------------------------------------

# 重复建单在厂区里是两次真实派车。三台车放大了这个风险：一台车的单被记在另一台名下，两边看
# 起来都还「有一张单」，只有总数会露馅。
$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-3V-10',
    '全程只建了六张 RIoT 单（三台车各取货一张、关卡一张）',
    ($riotOrders.Count -eq 6),
    6,
    $riotOrders.Count)

$boundKeys = @($riotOrders | ForEach-Object { [string]$_.executeVehicleKey } | Sort-Object -Unique)
$assertions.Add(
    'L2-3V-11',
    '六张单绑在三把 vehicleKey 上，没有一张落到别的车头上',
    ((@($boundKeys | Where-Object { $_ -in $distinctVehicleKeys }).Count) -eq $boundKeys.Count),
    ($distinctVehicleKeys -join ','),
    ($boundKeys -join ','))

# 引擎是派车链路的硬依赖：三台车都拿到单，说明可达性判据对三台车各放行过一次。顺带确认快照
# 自始至终没进过陈旧态——进过的话这一趟根本不会有单，但把它写下来才算证据。
$snapshotRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT StaleReason FROM RouteGraphSnapshots WHERE MapId = $($Context.MapId)"
$assertions.Add(
    'L2-3V-12',
    '路网快照全程不是陈旧态（引擎在三车链路里真的工作）',
    ($snapshotRows.Count -eq 1 -and
        ($null -eq $snapshotRows[0].StaleReason -or [string]$snapshotRows[0].StaleReason -eq '')),
    '(无陈旧原因)',
    $(if ($snapshotRows.Count -ne 1) { '(无快照行)' }
      elseif ($null -eq $snapshotRows[0].StaleReason) { '(null)' }
      else { [string]$snapshotRows[0].StaleReason }))

$journal.Note("三台车在同一次运行里各自完成一趟 WIRE_TO_GATE（本轮共派出 $dispatched 趟）。")
