#Requires -Version 7

<#
一趟跑完整条链路的多幕场景：送完一单 → 电量掉到线下自己去充电 → 充满回来接下一单 → 再送完一单。

这条场景存在的理由和别的不一样。别的每条只在 normal-load 上改一处，好让红的时候原因唯一；这
条刻意把几幕串起来，因为它要证的恰恰是幕与幕之间的交接——第一单结束后车真的空下来了，充电这
件事不会把车永远扣在桩上，充完之后受理链路完好如初。这些都是单幕场景按定义看不见的。

自动充电在这之前一整块都不存在。车电量低就只是不接单，然后停在原地等人推去充——而 MVP 里没有
任何东西会把它推过去。桩是 id25 地图上的 211 号站点。

一个不显然的点值得先说：车充完仍然站在桩上，RIoT 会一直报 CHARGING。所以「在充电就不接单」这
条老规矩必须让位给「充到恢复线就可以接单」，否则车一旦上桩就再也下不来。这里两侧都钉住了。
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

$chargerStationId = 211
$chargerStationName = '充电准备点1'

$firstGuid = [guid]::NewGuid()
$firstWire = $firstGuid.ToString('N')
$firstId = $firstGuid.ToString('D')
$secondGuid = [guid]::NewGuid()
$secondWire = $secondGuid.ToString('N')
$secondId = $secondGuid.ToString('D')

function Test-L2Null($value) {
    return ($null -eq $value) -or ($value -is [System.DBNull])
}

function Get-Stage([string]$demandId) {
    $rows = Get-L2Journey -Connection $connection -DemandId $demandId
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

function Get-ChargingRun {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql 'SELECT * FROM AutoChargingRuns ORDER BY ChargingRunId'
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$demandId, [string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Backlog([string]$demandId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].ReasonCode
}

function Move-VehicleToStation([object]$intent, [int]$stationId) {
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{
        orderState        = 3
        executeVehicleKey = $Context.VehicleKey
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey      = $Context.VehicleKey
        procState       = 'RUNNING'
        movementState   = 'MT_RUNNING'
        speed           = 0.8
        processingOrder = $true
        orderTaskId     = $intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey       = $Context.VehicleKey
        procState        = 'IDLE'
        movementState    = 'MT_FINISHED'
        speed            = 0
        currentPosition  = $stationId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })
}

function Complete-Demand([string]$demandId, [string]$wireId, [string]$sublot) {
    $journal.Note("Publishing demand $wireId ($sublot).")
    $null = $mes.Command('Put', "demands/$wireId", @{
        sublot      = $sublot
        area        = 'N1-3'
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
    $null = Wait-L2Condition -Description "demand $sublot was accepted and dispatched to the pickup station" `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
        -Probe { Get-Stage $demandId } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
    $pickup = Wait-L2Condition -Description "the TO_PICKUP intent for $sublot was confirmed" `
        -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
        -Probe { Get-Intent $demandId 'TO_PICKUP' } -Until { param($v) $v -and $v.Status -eq 'CONFIRMED' }
    Move-VehicleToStation $pickup $Context.PickupStationRiotId
    $null = Wait-L2Condition -Description "$sublot loaded and left for the gate" `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { Get-Stage $demandId } -Until { param($v) $v -eq 'AwaitingGateArrival' }
    $gate = Get-Intent $demandId 'TO_GATE'
    Move-VehicleToStation $gate $Context.GateStationRiotId
    return Wait-L2Condition -Description "$sublot completed at the gate" `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { Get-Stage $demandId } -Until { param($v) $v -eq 'Completed' }
}

# --- 0. 地图上先有一个充电桩 -----------------------------------------------------------------------

# RequireFixedStation 要求编号与名字精确成对，所以这里补的是地图内容，不是配置。名字与
# 站点号必须和 setup 里给服务端的两项对上。
#
# 读出现有站点再追加，而不是直接写一张新表：准入策略把 admissionPolicyVersion 绑在按区号解析
# 出的取货站点集合上，少掉任何一个，同一个版本号就绑到了不同内容，ApplyAdmissionPolicyAsync
# 每一轮都会抛 BusinessIdentityConflictException——运行时整个停摆，而日志里只说准入策略，不
# 说地图。第一次跑这条场景就是这么挂的（证据 001）。充电桩与关卡的名字都不是区号格式，所以
# 只增不减时那个集合原样不动。
$journal.Note("Adding charger station $chargerStationId ($chargerStationName) to map $($Context.MapId).")
$currentMap = @($riot.Snapshot().body.maps | Where-Object { $_.mapId -eq $Context.MapId })
if ($currentMap.Count -ne 1) { throw "Fake RIoT does not serve map $($Context.MapId)." }
$stations = @{}
foreach ($station in $currentMap[0].stations) { $stations["$($station.id)"] = $station.name }
$stations["$chargerStationId"] = $chargerStationName
$null = $riot.Command('Put', "maps/$($Context.MapId)/stations", @{ stations = $stations })

$mapStations = @($riot.Snapshot().body.maps | Where-Object { $_.mapId -eq $Context.MapId })
$assertions.Add(
    'L2-AC-01', '地图上有一个 211 号充电桩',
    ($mapStations.Count -eq 1 -and
        @($mapStations[0].stations | Where-Object { $_.id -eq $chargerStationId }).Count -eq 1),
    "station $chargerStationId",
    $(if ($mapStations.Count -eq 1) { ($mapStations[0].stations | ForEach-Object { $_.id }) -join ',' } else { '(no map)' }))

# --- 1. 第一幕：电量充足，正常送完一单 -------------------------------------------------------------

$stage = Complete-Demand $firstId $firstWire "L2-SUBLOT-$($Context.RunId)-A"
$assertions.Add(
    'L2-AC-02', '第一单在电量充足时正常走完',
    ($stage -eq 'Completed'),
    'Completed', $stage)

$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$firstId'"
$assertions.Add(
    'L2-AC-03', '第一单终态为 Succeeded',
    ($demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'Succeeded'),
    'Succeeded', $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' }))

$runsBefore = Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM AutoChargingRuns'
$assertions.Add(
    'L2-AC-04', '电量充足时不会没事跑去充电',
    ([int]$runsBefore[0].N -eq 0),
    0, [int]$runsBefore[0].N)

# --- 2. 第二幕：电量掉到触发线下，车自己去充电 -----------------------------------------------------

$journal.Note('Battery drops below the trigger level; the vehicle must take itself to the charger.')
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey   = $Context.VehicleKey
    battery      = 15
    batteryState = 'NO_CHARGE'
})

$run = Wait-L2Condition -Description 'a charging run was started and dispatched' `
    -Journal $journal -Criterion 'charging-run' -TimeoutSeconds 90 `
    -Probe { Get-ChargingRun } -Until { param($v) $null -ne $v }

$assertions.Add(
    'L2-AC-05', '低电触发一趟去充电桩的行程',
    ([string]$run.Stage -eq 'AwaitingChargerArrival' -and
        [int]$run.ChargerStationRiotId -eq $chargerStationId -and
        [int]$run.TriggeredAtBatteryPercent -eq 15),
    "AwaitingChargerArrival / $chargerStationId / 15",
    "$($run.Stage) / $($run.ChargerStationRiotId) / $($run.TriggeredAtBatteryPercent)")

# 建单与对账不是同一个瞬间：行程一写下来就有 intent 行，状态要等 create 回来对上才翻
# CONFIRMED。直接读会撞上 PENDING_RECONCILIATION——那是取样太早，不是缺陷。
$chargerIntent = Wait-L2Condition -Description 'the TO_CHARGER intent was confirmed' `
    -Journal $journal -Criterion 'to-charger-intent' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT UpperId, OrderId, Status, DestinationStationId FROM OrderIntents WHERE Purpose = 'TO_CHARGER'"
        if ($rows.Count -eq 0) { return $null }
        return $rows[0]
    } -Until { param($v) $v -and $v.Status -eq 'CONFIRMED' }
$assertions.Add(
    'L2-AC-06', '充电行程建出并确认了一条 TO_CHARGER 单',
    ($null -ne $chargerIntent -and [string]$chargerIntent.Status -eq 'CONFIRMED' -and
        [int]$chargerIntent.DestinationStationId -eq $chargerStationId),
    "CONFIRMED / $chargerStationId",
    $(if ($chargerIntent) { "$($chargerIntent.Status) / $($chargerIntent.DestinationStationId)" } else { '(no intent)' }))

# 充电这件事没有需求，也不该凭空长出一趟旅程来。
$runtimeCount = Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM JourneyRuntimes'
$assertions.Add(
    'L2-AC-07', '充电不产生旅程，也不占用需求',
    ([int]$runtimeCount[0].N -eq 1),
    1, [int]$runtimeCount[0].N)

# 在路上再转几轮，不能冒出第二条单——重复派单在厂区里就是两次真实调度。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$chargerIntentRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT COUNT(*) AS N FROM OrderIntents WHERE Purpose = 'TO_CHARGER'"
$assertions.Add(
    'L2-AC-08', '去充电桩的路上不会重复派单',
    ([int]$chargerIntentRows[0].N -eq 1),
    1, [int]$chargerIntentRows[0].N)

# --- 3. 第三幕：到桩、接电、充到恢复线 -------------------------------------------------------------

$chargerIntent = Invoke-L2Query -Connection $connection `
    -Sql "SELECT UpperId, OrderId FROM OrderIntents WHERE Purpose = 'TO_CHARGER'"
$journal.Note('Vehicle arrives at the charger and starts drawing current.')
$null = $riot.Command('Put', "orders/$($chargerIntent[0].UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $chargerIntent[0].OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $chargerStationId
    processingOrder  = $false
    clearOrderTaskId = $true
    battery          = 22
    batteryState     = 'CHARGING'
})
$null = $riot.Command('Put', "orders/$($chargerIntent[0].UpperId)", @{ orderState = 5 })

$run = Wait-L2Condition -Description 'the charger arrival was trusted' `
    -Journal $journal -Criterion 'charging-run' -TimeoutSeconds 90 `
    -Probe { Get-ChargingRun } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'Charging' }
$assertions.Add(
    'L2-AC-09', '到桩并确认在充电',
    ([string]$run.Stage -eq 'Charging' -and (Test-L2Null $run.BlockReasonCode)),
    'Charging / 无阻塞原因',
    "$($run.Stage) / $($run.BlockReasonCode)")

# 高于接单门槛（30%）但没到恢复线（80%）：仍然不许接单。这一条是这套阈值的真正含义所在——
# 中途被拉走的车会带着半箱电跑一整趟。
$journal.Note('Battery passes the demand floor but not the resume level; the vehicle must stay put.')
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey   = $Context.VehicleKey
    battery      = 55
    batteryState = 'CHARGING'
})
$null = $mes.Command('Put', "demands/$secondWire", @{
    sublot      = "L2-SUBLOT-$($Context.RunId)-B"
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

# 充电这一轮由充电行程占用，候选评估整个不跑——所以这一单连一条 backlog 记录都不会有。这是
# 有意的：车在恢复线以下，每个候选都会被电量政策拒掉，而做出那个判断要读 MesIngest、箱数和
# 包装规格，全是远程调用。代价是运维在 JourneyBacklog 里看不到解释，解释在 AutoChargingRuns
# 里——车正在充电，本身就是原因。钉住它，免得将来有人把这个空当成缺陷。
$reason = Get-Backlog $secondId
$assertions.Add(
    'L2-AC-10', '充电占用的轮次不评估候选，解释在充电行程而不是 backlog 里',
    ($null -eq $reason),
    '(无 backlog 记录)', $(if ($null -eq $reason) { '(无 backlog 记录)' } else { $reason }))

$assertions.Add(
    'L2-AC-11', '充电未达恢复线时不受理新需求',
    ($null -eq (Get-Stage $secondId)),
    '(无旅程)', $(Get-Stage $secondId))

$run = Get-ChargingRun
$assertions.Add(
    'L2-AC-12', '充电行程未到恢复线时不结束',
    ([string]$run.Stage -eq 'Charging'),
    'Charging', [string]$run.Stage)

# --- 4. 第四幕：充到恢复线，车重新可用，接着把第二单送完 -------------------------------------------

$journal.Note('Battery reaches the resume level; the vehicle stays plugged in but becomes available.')
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey   = $Context.VehicleKey
    battery      = 80
    batteryState = 'CHARGING'
})

$run = Wait-L2Condition -Description 'the charging run released the vehicle' `
    -Journal $journal -Criterion 'charging-run' -TimeoutSeconds 90 `
    -Probe { Get-ChargingRun } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'Completed' }
$assertions.Add(
    'L2-AC-13', '充到恢复线后充电行程结束并记下释放电量',
    ([string]$run.Stage -eq 'Completed' -and [int]$run.ReleasedAtBatteryPercent -eq 80),
    'Completed / 80',
    "$($run.Stage) / $($run.ReleasedAtBatteryPercent)")

# 车还插在桩上，RIoT 仍然报 CHARGING。老规矩是「在充电就不接单」，那会把车永远扣在这里。
$vehicle = @($riot.Snapshot().body.vehicles | Where-Object { $_.deviceKey -eq $Context.VehicleKey })
$assertions.Add(
    'L2-AC-14', '车仍然停在桩上并且仍报 CHARGING',
    ($vehicle.Count -eq 1 -and [string]$vehicle[0].batteryState -eq 'CHARGING' -and
        [int]$vehicle[0].currentPosition -eq $chargerStationId),
    "CHARGING / $chargerStationId",
    $(if ($vehicle.Count -eq 1) { "$($vehicle[0].batteryState) / $($vehicle[0].currentPosition)" } else { '(no vehicle)' }))

$stage = Wait-L2Condition -Description 'the waiting demand was accepted once the vehicle was released' `
    -Journal $journal -Criterion 'second-journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage $secondId } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$assertions.Add(
    'L2-AC-15', '达到恢复线后等着的那一单立刻被受理',
    ($stage -eq 'AwaitingPickupArrival'),
    'AwaitingPickupArrival', $stage)

$secondPickup = Wait-L2Condition -Description 'the second TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'second-to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { Get-Intent $secondId 'TO_PICKUP' } -Until { param($v) $v -and $v.Status -eq 'CONFIRMED' }
Move-VehicleToStation $secondPickup $Context.PickupStationRiotId
$null = Wait-L2Condition -Description 'the second journey loaded and left for the gate' `
    -Journal $journal -Criterion 'second-journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage $secondId } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$gateIntent = Get-Intent $secondId 'TO_GATE'
Move-VehicleToStation $gateIntent $Context.GateStationRiotId
$stage = Wait-L2Condition -Description 'the second journey completed at the gate' `
    -Journal $journal -Criterion 'second-journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage $secondId } -Until { param($v) $v -eq 'Completed' }

$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$secondId'"
$assertions.Add(
    'L2-AC-16', '充电之后整条受理链路完好，第二单走到 Succeeded',
    ($stage -eq 'Completed' -and $demandRows.Count -eq 1 -and
        [string]$demandRows[0].Status -eq 'Succeeded'),
    'Completed / Succeeded',
    "$stage / $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' })")

$runCount = Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM AutoChargingRuns'
$assertions.Add(
    'L2-AC-17', '全程只去了一趟充电桩',
    ([int]$runCount[0].N -eq 1),
    1, [int]$runCount[0].N)

# 两单各两条，加去充电桩那一条。
$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-AC-18', '全程五条 RIoT 单：两单各两段，加一条去充电桩',
    ($riotOrders.Count -eq 5),
    5, $riotOrders.Count)

$journal.Note('Scenario finished.')
