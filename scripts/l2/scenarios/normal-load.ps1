#Requires -Version 7

<#
正常装载：一条需求从受理走到 journey 完成的全链路。

这是落地顺序第 3 步要跑通的那一条。它不注入任何故障——每一步都按顺序成功——所以它的价值不在
于发现缺陷，而在于证明编排器、三个替身和真 ControlServer 能在无人值守下走完一整趟。有了它，
后面每一个异常场景都只是在它上面改一处。
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

# MesIngest 报的 demandId 是不带连字符的，服务端在入口处归一化成规范 UUID。两种写法都留在这里
# 是有意的：替身按 MesIngest 的写法发，断言按服务端存的写法查。
$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

function Get-Runtime {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stage {
    $runtime = Get-Runtime
    if ($null -eq $runtime) { return $null }
    # ControlServerDbContext converts every one of these enums with HasConversion<string>, so the
    # column holds the member name. Reading it as an ordinal throws, and a throwing probe inside
    # Wait-L2Condition looks exactly like "not there yet" -- which is a 90-second timeout with
    # nothing to show for it.
    return [string]$runtime.Stage
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# --- 1. 需求出现在 MesIngest 目录里，服务端受理并派车去取货点 -------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot) to the fake MesIngest catalog.")
# area 必须是站点名里解析得出的区号，不是它的前缀：MapStationResolver 把 "N1-3_N1-7" 拆成
# N1-3 与 N1-7 两个区号，"N1" 谁都匹配不上，会判 AREA_STATION_NOT_FOUND。
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot       = $sublot
    area         = 'N1-3'
    eqp          = 'EQP-L2-01'
    package      = 'L2-PACKAGE'
    maxBoxCount  = 4
})

$stage = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

# 受理与建单确认不是同一个瞬间：stage 在受理时就翻到 AwaitingPickupArrival，建单与对账紧随其后
# 才把 intent 置为 CONFIRMED。第一次跑这里读到的是 CREATE_ATTEMPTED——是判据取样太早，不是缺陷。
$intentStatus = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-UpperId -purpose 'TO_PICKUP'; if ($row) { [string]$row.Status } else { $null } } `
    -Until { param($v) $v -eq 'CONFIRMED' }
$pickupIntent = Get-UpperId -purpose 'TO_PICKUP'
$assertions.Add(
    'L2-NL-01', '受理后建出 TO_PICKUP 单并确认',
    ($intentStatus -eq 'CONFIRMED'),
    'CONFIRMED', $intentStatus)

$backlog = Invoke-L2Query -Connection $connection `
    -Sql "SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-NL-02', '候选判定结果是 ACCEPTED',
    ($backlog.Count -eq 1 -and $backlog[0].ReasonCode -eq 'ACCEPTED'),
    'ACCEPTED', $(if ($backlog.Count -eq 1) { $backlog[0].ReasonCode } else { '(no backlog row)' }))

# --- 2. 车开到取货点：先运动，再停稳 ---------------------------------------------------------------

# 这一段就是 2026-09-03 那两个缺陷需要的形状：车动起来，然后停下。在有假 RIoT 之前，这件事只能
# 站在真车前面做。
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

# RIoT 把单标成了成功、位置也报到了取货点，而车还在减速——静态证据全齐，只剩运动证据说它没停稳。
# 这一拍是 control-server#204 加的，因为没有它，`L2-NL-03` 判不到它名字里那件事：
# CheckArrivalAsync 的第一道门是 exactOrder（订单必须 Terminal + Success），上一拍的 orderState = 3
# 过不了，于是「服务端会不会看车在不在动」这个问题根本轮不到被问。实测过：把 ProcState、Speed、
# OrderTaskId 与 onboard.VehicleStopped 四个条件一起从 trusted 里删掉，旧写法仍然整条 PASS
# （evidence/l2/cs204-nl03-defect-motion-001）。
$journal.Note('RIoT reports the order finished and the vehicle at the pickup station, while it is still moving.')
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 5 })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    currentPosition = $Context.PickupStationRiotId
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $pickupIntent.OrderId
})

# 先让运行时转过两轮再读（与 MVP 线 cs#26 同形）。直接读，读到的是引擎还没看过这些报告的状态——
# 那时 stage 还停在上一步写下的 AwaitingPickupArrival，判据无论服务端怎么判都绿。
# $Count 保证「开始了这么多轮」，所以要两轮才有一轮是完整跑完的。
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal
$stage = Get-Stage
$assertions.Add(
    'L2-NL-03', '车在路上时不采信到站：单已报成功、位置已报到取货点，只有运动证据说车还没停稳，运行时转过两轮之后仍然不前进',
    ($stage -eq 'AwaitingPickupArrival'),
    'AwaitingPickupArrival', $stage)

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
# 订单在上一拍就报成功了，这里是 RIoT 重复同一个状态。留着是为了让「车停稳」这一拍自己就完整，
# 而不是要靠上一拍才成立；假 RIoT 对没有变化的更新不产生新修订，所以它是幂等的。
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 5 })

# --- 3. 到站 → 报 sublot → 装载 → 出发前安全检查 ---------------------------------------------------

# 合成车载端按策略自动应答，所以这一大段服务端自己就能走完。断言看的是它有没有真的按顺序走。
$stage = Wait-L2Condition -Description 'the arrival was trusted and the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }

$loadRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'"
$assertions.Add(
    'L2-NL-04', '装载操作提交（Committed）',
    ($loadRows.Count -eq 1 -and [string]$loadRows[0].Status -eq 'Committed'),
    'Committed', $(if ($loadRows.Count -eq 1) { [string]$loadRows[0].Status } else { '(no load row)' }))

$gateIntent = Get-UpperId -purpose 'TO_GATE'
$assertions.Add(
    'L2-NL-05', '出发前安全检查通过后才建 TO_GATE 单',
    ($null -ne $gateIntent -and $gateIntent.Status -eq 'CONFIRMED'),
    'CONFIRMED', $(if ($gateIntent) { $gateIntent.Status } else { '(no intent)' }))

# --- 4. 车开到关卡并卸载 ---------------------------------------------------------------------------

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

$assertions.Add('L2-NL-06', 'journey 走到 Completed', ($stage -eq 'Completed'), 'Completed', $stage)

$unloadRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Unload'"
$assertions.Add(
    'L2-NL-07', '卸载操作提交（Committed）',
    ($unloadRows.Count -eq 1 -and [string]$unloadRows[0].Status -eq 'Committed'),
    'Committed', $(if ($unloadRows.Count -eq 1) { [string]$unloadRows[0].Status } else { '(no unload row)' }))

$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-NL-08', '需求终态为 Succeeded',
    ($demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'Succeeded'),
    'Succeeded', $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' }))

# 全程只建了两条 RIoT 单。重复建单是这套系统最贵的一类缺陷——它会在厂区里变成两次真实派车。
$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-NL-09', '全程只建了两条 RIoT 单（取货一条、关卡一条）',
    ($riotOrders.Count -eq 2),
    2, $riotOrders.Count)

$journal.Note('Scenario finished.')
