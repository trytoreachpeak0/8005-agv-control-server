#Requires -Version 7

<#
一趟多单：两条需求在同一个取货站点上车，一起去关卡，逐个卸下。

这是 ADR-cross-0057 的基线形态，也是 `normal-load` 一直没能覆盖的那一半——MVP 把「一趟一单」
写进了 schema，于是「暂时只做一单」变成了「契约规定只能一单」。0.2.0 解开 schema，服务端改成
停靠序列之后，这条场景要证明的是跨端时序仍然成立：两条需求共用一个 operationSessionId、各自
一轮作业清单与录入请求、各自一次装载闭环，全程只建两条 RIoT 单。

单元测试已经覆盖了乱序扫码、装满、持货超时与异站录入。这里不重复那些判定，只跑通链路——L2 的
价值在于它启的是真 ControlServer，走的是真 NDJSON 会话。
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

# 两条需求，同一个区号与设备，因此解析到同一个取货站点。区号必须是站点名里解析得出的那个，
# 不是它的前缀：MapStationResolver 把 "N1-3_N1-7" 拆成 N1-3 与 N1-7，"N1" 谁都匹配不上。
$firstGuid = [guid]::NewGuid()
$secondGuid = [guid]::NewGuid()
$firstId = $firstGuid.ToString('D')
$secondId = $secondGuid.ToString('D')
$firstSublot = "L2-SUBLOT-A-$($Context.RunId)"
$secondSublot = "L2-SUBLOT-B-$($Context.RunId)"

function Get-Journey {
    # 多单旅程读的是三张表本身，不是 Get-L2Journey 那个单单视图——它会把一趟多单压平成看不出
    # 区别的两行。
    $rows = Invoke-L2Query -Connection $connection -Sql 'SELECT * FROM JourneyRuntimes'
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stage {
    $journey = Get-Journey
    if ($null -eq $journey) { return $null }
    return [string]$journey.Stage
}

function Get-JourneyDemands {
    return Invoke-L2Query -Connection $connection `
        -Sql 'SELECT DemandId, StopSequence, ExpectedBasketCount, State, TargetSlotsJson FROM JourneyDemands'
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Publish-Demand([string]$wireId, [string]$sublot) {
    $null = $mes.Command('Put', "demands/$wireId", @{
        sublot      = $sublot
        area        = 'N1-3'
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

# --- 1. 两条需求同时在目录里，服务端受理其中一条并派车 ---------------------------------------------

$journal.Note("Publishing two demands ($firstSublot, $secondSublot) at one pickup station.")
Publish-Demand -wireId $firstGuid.ToString('N') -sublot $firstSublot
Publish-Demand -wireId $secondGuid.ToString('N') -sublot $secondSublot

$stage = Wait-L2Condition -Description 'a journey was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$intentStatus = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-UpperId -purpose 'TO_PICKUP'; if ($row) { [string]$row.Status } else { $null } } `
    -Until { param($v) $v -eq 'CONFIRMED' }
$pickupIntent = Get-UpperId -purpose 'TO_PICKUP'
$assertions.Add(
    'L2-MD-01', '受理后建出 TO_PICKUP 单并确认',
    ($intentStatus -eq 'CONFIRMED'),
    'CONFIRMED', $intentStatus)

# 派车时只承诺了一条需求。第二条要到车停稳、可以安全收货的那一刻才被吸收进来。
$journeyCount = Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM JourneyRuntimes'
$assertions.Add(
    'L2-MD-02', '两条需求共用一趟旅程（而不是各起一趟）',
    ([int]$journeyCount[0].N -eq 1),
    1, [int]$journeyCount[0].N)

# --- 2. 车开到取货点并停稳 -------------------------------------------------------------------------

$journal.Note('Vehicle departs for the pickup station.')
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $pickupIntent.OrderId
})
$journal.Note('Vehicle arrives at the pickup station and comes to rest.')
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $Context.PickupStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 5 })

# --- 3. 两条需求在同一个停靠上车 -------------------------------------------------------------------

# 合成车载端拿到录入请求就自动应答，取 expectedSublots 的第一个；服务端把本停靠的排在最前，所以
# 它每一轮扫到的都是这一站真的能装的那一条。
$loadedCount = Wait-L2Condition -Description 'both demands were loaded at the one stop' `
    -Journal $journal -Criterion 'loaded-demands' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT COUNT(*) AS N FROM StationOperations WHERE OperationType = 'Load' AND Status = 'Committed'"
        return [int]$rows[0].N
    } `
    -Until { param($v) $v -ge 2 }

$assertions.Add(
    'L2-MD-03', '同一个停靠上完成两次装载闭环',
    ($loadedCount -eq 2),
    2, $loadedCount)

$demandRows = Get-JourneyDemands
$assertions.Add(
    'L2-MD-04', '两条需求都挂在这趟旅程的同一个停靠上',
    ($demandRows.Count -eq 2 -and
        (@($demandRows | ForEach-Object { [int]$_.StopSequence } | Sort-Object -Unique)).Count -eq 1),
    '2 demands / 1 stop',
    "$($demandRows.Count) demands / $((@($demandRows | ForEach-Object { [int]$_.StopSequence } | Sort-Object -Unique)).Count) stops")

# 仓位不能重叠：两条需求各自预留，并集就是它们占的全部。
$slots = @($demandRows | ForEach-Object { ([string]$_.TargetSlotsJson) } | ForEach-Object {
    ($_ | ConvertFrom-Json)
})
$distinctSlots = @($slots | Sort-Object -Unique)
$assertions.Add(
    'L2-MD-05', '两条需求预留的仓位互不重叠',
    ($slots.Count -eq $distinctSlots.Count -and $slots.Count -ge 2),
    "$($slots.Count) distinct", "$($distinctSlots.Count) distinct of $($slots.Count)")

$stage = Wait-L2Condition -Description 'the journey left the pickup stop for the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }

$journey = Get-Journey
$assertions.Add(
    'L2-MD-06', '装货阶段结束时记下了理由',
    (-not [string]::IsNullOrWhiteSpace([string]$journey.LoadingClosedReason)),
    'a reason', [string]$journey.LoadingClosedReason)

# 持货钟从第一批安全闭环起算，所以它一定已经起来了。
$assertions.Add(
    'L2-MD-07', '持货钟在第一批装载闭环后开始走',
    (-not [string]::IsNullOrWhiteSpace([string]$journey.HoldingStartedAt)),
    'a timestamp', [string]$journey.HoldingStartedAt)

# --- 4. 车开到关卡，两条需求逐个卸下 ---------------------------------------------------------------

$gateIntent = Get-UpperId -purpose 'TO_GATE'
$journal.Note('Vehicle departs for the gate.')
$null = $riot.Command('Put', "orders/$($gateIntent.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $gateIntent.OrderId
})
$journal.Note('Vehicle arrives at the gate and comes to rest.')
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $Context.GateStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($gateIntent.UpperId)", @{ orderState = 5 })

$stage = Wait-L2Condition -Description 'the journey completed at the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'Completed' }

$assertions.Add('L2-MD-08', 'journey 走到 Completed', ($stage -eq 'Completed'), 'Completed', $stage)

$unloadRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM StationOperations WHERE OperationType = 'Unload'"
$committedUnloads = @($unloadRows | Where-Object { [string]$_.Status -eq 'Committed' })
$assertions.Add(
    'L2-MD-09', '两条需求各自卸载一次，都提交',
    ($unloadRows.Count -eq 2 -and $committedUnloads.Count -eq 2),
    '2 committed', "$($committedUnloads.Count) committed of $($unloadRows.Count)")

$demandStatuses = Invoke-L2Query -Connection $connection `
    -Sql 'SELECT DemandId, Status FROM AcceptedDemands'
$succeeded = @($demandStatuses | Where-Object { [string]$_.Status -eq 'Succeeded' })
$assertions.Add(
    'L2-MD-10', '两条需求终态都是 Succeeded',
    ($demandStatuses.Count -eq 2 -and $succeeded.Count -eq 2),
    '2 Succeeded', "$($succeeded.Count) Succeeded of $($demandStatuses.Count)")

# 租约属于旅程，不属于需求：一趟车只占车一次，卸完最后一条才放。
$leases = Invoke-L2Query -Connection $connection `
    -Sql 'SELECT JourneyId, ReleasedAt FROM VehicleDispatchLeases'
$released = @($leases | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.ReleasedAt) })
$assertions.Add(
    'L2-MD-11', '两条需求只占用一份车辆租约，收尾时释放',
    ($leases.Count -eq 1 -and $released.Count -eq 1),
    '1 released lease', "$($released.Count) released of $($leases.Count)")

# 一趟多单不该多建 RIoT 单：仍然是取货一条、关卡一条。这是这条场景最贵的一个判据——重复建单在
# 厂区里就是两次真实派车。
$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-MD-12', '两条需求全程只建两条 RIoT 单',
    ($riotOrders.Count -eq 2),
    2, $riotOrders.Count)

$journal.Note('Scenario finished.')
