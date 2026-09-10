#Requires -Version 7

<#
四条需求、四个不同取货停靠、一个关卡，跑在合成车载端上。

`multi-demand-one-stop` 证明的是「两条需求在同一个停靠上车」——行程带只有两条腿（一取一关卡）。
这条场景证明的是另一半：**四条需求分布在四个不同的取货停靠上**，行程带因此有五条腿。

那正是 2026-09-10 现场窗口炸掉的形状：车载端的手写校验写死只收两条腿，判合法报文
`PROTOCOL_SCHEMA_INVALID`，服务端每次会话恢复重发同一个 messageId、而 `sentAt` 变了，于是撞上
自己的幂等保护并关连接——循环不自愈，八分半后车载端进程消失。车载端那条已经修了
（8005-agv-onboard-hmi#30），但没有任何一条 L2 场景会让服务端真的发出五条腿，所以这个形状在
自动化里是零覆盖的。

这条是合成对端版：它证明的是**服务端侧**的多停靠链路——四条需求凑成一趟、四个停靠依次装载、
行程带按契约发出五条腿。真装置那一半（真 Modbus 闭环、光幕时序、操作员不作为的三幕）另有场景，
它需要交互式桌面，跑不进这个 headless 的 CI。

站点是场景自己注入的：种子地图只有一个能当取货点的站（`12 = N1-3_N1-7`；`11 = C15-13` 虽然也是
area-named station，但 area 不以 N 开头，`JourneyRuntimeEngine` 判它 `OUT_OF_SCOPE_AREA`）。
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

# 四个取货停靠。area 必须是站点名里解析得出的那一段：MapStationResolver 把 "N1-3_N1-7" 拆成
# N1-3 与 N1-7，写 "N1" 谁都匹配不上。
$stops = @(
    @{ RiotId = 12; Station = 'N1-3_N1-7'; Area = 'N1-3' }
    @{ RiotId = 13; Station = 'N2-6';      Area = 'N2-6' }
    @{ RiotId = 14; Station = 'N3-4';      Area = 'N3-4' }
    @{ RiotId = 15; Station = 'N4-2';      Area = 'N4-2' }
)
foreach ($stop in $stops) {
    $guid = [guid]::NewGuid()
    # 目录里的键是 demandId 的无连字符形式。拿 sublot 当键发布过一次，目录照收，引擎一条都不
    # 受理，而且不写任何原因——两个小时就耗在这上面。
    $stop.DemandIdWire = $guid.ToString('N')
    $stop.DemandId = $guid.ToString('D')
    $stop.Sublot = "L2-MD4-$($stop.Area)-$($Context.RunId)"
}

function Get-Journey {
    # 多单旅程读三张表本身。Get-L2Journey 是「一需求一取货停靠」的视图，会把四个停靠压平。
    $rows = Invoke-L2Query -Connection $connection -Sql 'SELECT * FROM JourneyRuntimes'
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stage {
    $journey = Get-Journey
    if ($null -eq $journey) { return $null }
    return [string]$journey.Stage
}

function Get-Stops {
    <#
    **先赋值再返回。**`Invoke-L2Query` 以 `return , $rows` 结尾，把整组行包成一个对象；直接
    `return Invoke-L2Query ...` 会把那个包原样交出去，于是 `@(Get-Stops).Count` 永远是 1。这个 1
    与「停靠还没建出来」长得一模一样——#38 因此把一个会正常规划四个停靠的服务端误诊成「其余三条
    没被吸收成新停靠」。赋值会拆掉那一层，`return $rows` 再逐行交出。
    #>
    $rows = Invoke-L2Query -Connection $connection `
        -Sql ('SELECT Sequence, Role, State, StationId, StationRiotId, MovementLegId ' +
              'FROM JourneyStops ORDER BY Sequence')
    return $rows
}

function Get-StopUpperId {
    <#
    停靠的移动单在 OrderIntents 里，两张表靠 MovementLegId 对上。

    **过滤下推到 SQL，不要在 PowerShell 里按 Sequence 挑行。**`[int]$_.Sequence` 会抛
    `Cannot convert the "System.Object[]" value ... to type "System.Int32"`，而
    `Wait-L2Condition` 把探针异常一律吞成 `$null`，报出来是 `Last observed: (nothing)`——
    与「条件还没满足」完全同形，查了好几轮才看见。
    #>
    param([Parameter(Mandatory)][int]$Sequence)

    $rows = Invoke-L2Query -Connection $connection -Sql (
        'SELECT o.UpperId FROM JourneyStops s ' +
        'JOIN OrderIntents o ON o.MovementLegId = s.MovementLegId ' +
        # 只认 CONFIRMED：UpperId 一落库就有，假 RIoT 要等建单确认才认得它，早改单回 409。
        "WHERE s.Sequence = $Sequence AND o.Status = 'CONFIRMED'")
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].UpperId
}

function Get-StopRow {
    param([Parameter(Mandatory)][int]$Sequence)

    $rows = Invoke-L2Query -Connection $connection -Sql (
        'SELECT Sequence, Role, State, StationId, StationRiotId, MovementLegId ' +
        "FROM JourneyStops WHERE Sequence = $Sequence")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-CommittedLoadCount {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT COUNT(*) AS N FROM StationOperations WHERE OperationType = 'Load' AND Status = 'Committed'")
    return [int]$rows[0].N
}

function Invoke-DriveToStop {
    <#
    把车开到指定停靠并停稳。停靠的 UpperId 是它自己的移动单，要等引擎建出来才有。
    #>
    param([Parameter(Mandatory)][int]$Sequence)

    # 探针里只能引用脚本级变量。`Wait-L2Condition` 在模块里执行这个 scriptblock，看不见本函数的
    # 局部作用域，而它第 82 行把探针的异常一律吞成 `$null`——于是「变量取不到」和「条件还没满足」
    # 长得一模一样，都报 `Last observed: (nothing)`。既有场景的探针清一色只引用脚本级变量，不是
    # 巧合。
    $script:driveSequence = $Sequence
    $upperId = Wait-L2Condition -Description "stop $Sequence has a movement order" `
        -Journal $journal -Criterion "stop-$Sequence-order" -TimeoutSeconds 90 `
        -Probe { Get-StopUpperId -Sequence $script:driveSequence } `
        -Until { param($v) -not [string]::IsNullOrWhiteSpace($v) }

    $stopRow = Get-StopRow -Sequence $Sequence
    $journal.Note("Vehicle drives to stop $Sequence (station $($stopRow.StationId) / $($stopRow.StationRiotId)).")
    $null = $riot.Command('Put', "orders/$upperId", @{
        orderState        = 3
        executeVehicleKey = $Context.VehicleKey
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey      = $Context.VehicleKey
        procState       = 'RUNNING'
        movementState   = 'MT_RUNNING'
        speed           = 0.8
        processingOrder = $true
        orderTaskId     = $upperId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey       = $Context.VehicleKey
        procState        = 'IDLE'
        movementState    = 'MT_FINISHED'
        speed            = 0
        currentPosition  = [int]$stopRow.StationRiotId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })
}

function Get-LatestStopPlanLegs {
    # 行程带看服务端存进出站表的原文，不是界面上的投影：判据要问的是「服务端到底发了几条腿」。
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'UpcomingStopPlanSnapshot' " +
        'ORDER BY CreatedAt DESC LIMIT 1')
    if ($rows.Count -eq 0) { return $null }
    return ([string]$rows[0].PayloadJson | ConvertFrom-Json).payload.legs
}

# --- 1. 四条需求，四个区号 ------------------------------------------------------------------------

# 站点在 setup.psd1 的 ExtraStations 里，FakeRiot 启动时就带上了——**不能在这里注入**。准入策略把
# 版本号绑定在它第一次看到的地图内容上，服务端起来之后再改，每一轮 tick 都抛
# `Admission policy version is already bound to different content or deployment identity`，
# 而那个抛出在需求循环之前，于是没有 backlog、没有原因码、日志干净，看起来就像引擎在装死。

foreach ($stop in $stops) {
    $journal.Note("Publishing demand $($stop.Sublot) in area $($stop.Area).")
    $null = $mes.Command('Put', "demands/$($stop.DemandIdWire)", @{
        sublot      = $stop.Sublot
        area        = $stop.Area
        eqp         = "EQP-L2-$($stop.Area)"
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

$null = Wait-L2Condition -Description 'a journey was created and dispatched to the first pickup' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

# 派车时只承诺了一条需求，其余三条在 backlog 里是 ELIGIBLE——**合格但还没受理**。它们要等车停稳、
# 可以安全收货的那一刻才被吸收进来（`multi-demand-one-stop` 记的也是这件事）。所以「四条凑成一趟」
# 这个判据只能在第一次到站之后问；在派车那一刻问，看到的永远是 1，而且没有任何东西出错。
Invoke-DriveToStop -Sequence 1

$null = Wait-L2Condition -Description 'all four demands joined one journey after the first arrival' `
    -Journal $journal -Criterion 'journey-stops' -TimeoutSeconds 120 `
    -Probe { @(Get-Stops).Count } -Until { param($v) $v -ge 5 }

$allStops = @(Get-Stops)
$pickupStops = @($allStops | Where-Object { $_.Role -eq 'PICKUP' })
$gateStops = @($allStops | Where-Object { $_.Role -eq 'GATE' })
$assertions.Add(
    'L2-MD4-01', '四条需求凑成一趟旅程：四个取货停靠加一个关卡',
    ($pickupStops.Count -eq 4 -and $gateStops.Count -eq 1),
    '4 PICKUP + 1 GATE', "$($pickupStops.Count) PICKUP + $($gateStops.Count) GATE")

$journeyCount = Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM JourneyRuntimes'
$assertions.Add(
    'L2-MD4-02', '是一趟旅程，不是四趟',
    ([int]$journeyCount[0].N -eq 1), 1, [int]$journeyCount[0].N)

$distinctStations = @($pickupStops | ForEach-Object { [int]$_.StationRiotId } | Sort-Object -Unique)
$assertions.Add(
    'L2-MD4-03', '四个取货停靠落在四个不同站点上（这才是多停靠，不是一站多单）',
    ($distinctStations.Count -eq 4),
    4, $distinctStations.Count)

# --- 3. 行程带：五条腿 ----------------------------------------------------------------------------

$legs = Wait-L2Condition -Description 'the server published a five-leg stop plan' `
    -Journal $journal -Criterion 'stop-plan-legs' -TimeoutSeconds 60 `
    -Probe { $l = Get-LatestStopPlanLegs; if ($null -eq $l) { 0 } else { @($l).Count } } `
    -Until { param($v) $v -ge 5 }
$assertions.Add(
    'L2-MD4-04', '服务端按契约发出五条腿的行程带（四个取货加一个关卡）',
    ($legs -eq 5), 5, $legs)

$legRows = @(Get-LatestStopPlanLegs)
$gateLegs = @($legRows | Where-Object { $_.legType -eq 'TO_GATE' })
$assertions.Add(
    'L2-MD4-05', '行程带里四条 TO_PICKUP 加一条 TO_GATE，序号连续',
    ($gateLegs.Count -eq 1 -and
        @($legRows | Where-Object { $_.legType -eq 'TO_PICKUP' }).Count -eq 4 -and
        (@($legRows | ForEach-Object { [int]$_.sequence } | Sort-Object) -join ',') -eq '1,2,3,4,5'),
    '4 TO_PICKUP + 1 TO_GATE / 1,2,3,4,5',
    "$(@($legRows | Where-Object { $_.legType -eq 'TO_PICKUP' }).Count) TO_PICKUP + $($gateLegs.Count) TO_GATE / $((@($legRows | ForEach-Object { [int]$_.sequence } | Sort-Object) -join ','))")

# --- 4. 四个停靠依次装载 --------------------------------------------------------------------------

# 合成对端拿到录入请求就自动应答，取 expectedSublots 的第一个；服务端把本停靠的排在最前，所以
# 它每一轮扫到的都是这一站真能装的那条。
for ($sequence = 1; $sequence -le 4; $sequence++) {
    $stopRow = Get-StopRow -Sequence $sequence
    $journal.Note("=== Stop $sequence : station $($stopRow.StationId) ($($stopRow.StationRiotId)) ===")

    # 停靠 1 上面已经开过去了——四条需求正是在那一刻才凑齐的。
    if ($sequence -gt 1) { Invoke-DriveToStop -Sequence $sequence }

    $committed = Wait-L2Condition -Description "the load at stop $sequence committed" `
        -Journal $journal -Criterion "stop-$sequence-committed" -TimeoutSeconds 120 `
        -Probe { Get-CommittedLoadCount } -Until { param($v) $v -ge $sequence }
    $assertions.Add(
        "L2-MD4-1$sequence", "停靠 $sequence 装载提交，累计 $sequence 次",
        ($committed -ge $sequence), ">= $sequence", $committed)
}

# --- 5. 装完之后去关卡 ----------------------------------------------------------------------------

$stage = Wait-L2Condition -Description 'the journey left the last pickup for the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$assertions.Add(
    'L2-MD4-20', '四个停靠都装完之后旅程进入去关卡那一段',
    ($stage -eq 'AwaitingGateArrival'), 'AwaitingGateArrival', $stage)

$journey = Get-Journey
$assertions.Add(
    'L2-MD4-21', '装货阶段收尾时记下了理由',
    (-not [string]::IsNullOrWhiteSpace([string]$journey.LoadingClosedReason)),
    'a reason', [string]$journey.LoadingClosedReason)

$recoveryRows = Invoke-L2Query -Connection $connection -Sql (
    "SELECT COUNT(*) AS N FROM StationOperations WHERE Status = 'RecoveryRequired'")
$assertions.Add(
    'L2-MD4-22', '全程没有任何一次装载被判 RecoveryRequired',
    ([int]$recoveryRows[0].N -eq 0), 0, [int]$recoveryRows[0].N)

$journal.Note('Scenario finished.')
