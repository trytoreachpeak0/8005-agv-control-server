#Requires -Version 7

<#
混挂站点：同一个站点挂前后两侧的区域号，先后两趟各开各组（control-server#75，规格 8.3 批次 4 场景⑦；REQ-0353）。

一个机台站点的名字可以带多个区域号（AREA），例如 `N1-3_N2-5`。批次 4 起每个 AREA 在分区归属表里指派开哪一侧仓门，
于是同一个站点上可能一个 AREA 开前侧、一个开后侧。决议（8005-agv-program#80 决议 1～7）定的是：服务端不按站点检查
这种「混挂」是否一致，不按站点暂停或告警；一次停靠为各条需求分别开各自那一组；一个停靠位够不着两侧的站点由现场拆站，
系统不建模也不检查。所以这里要证的是「什么都不发生」：两趟照常派、照常开各自的组，没有阻断、没有原因码、没有告警。

边车把取货点 12 号站换成 `N1-3_N2-5`，归属表 N1-3 → FRONT、N2-5 → REAR。同一台车先后走两趟：
  1. 放需求甲（N1-3），走完一趟：目标仓在 FRONT 组，装卸命令都开这一组，停的是 12 号站。
  2. 再放需求乙（N2-5），走完一趟：目标仓在 REAR 组，装卸命令都开这一组，停的仍是 12 号站。
  3. 全程：StructuralDispatchBlocks 无行；积压里没有与站点一致性相关的原因码；两趟都没有被阻断；服务端日志里没有
     针对该站点的 Warning 及以上记录。

**两趟由同一台车走。**合成车载端（tools/ControlServer.FakeOnboard）起初按固定键缓存批次录入与出发前安全检查的答案，
一个对端进程只能正确应答一趟，所以 #120 里这个场景一度改成两台车各走一趟；PR #121 让假车载端按请求缓存答案后，改回
票面原样：一台车、同一个站点、先前侧后后侧。

**这不是「一次停靠前后两侧各一条需求」。**v2 线在批次 7 之前一趟只带一条需求，同一次停靠同时服务前侧一条、后侧一条
在今天构造不出来。那条真装置 L2 在批次 7（规格第 16 节第 9 条）；批次 4 用合成装置证同站两侧区域号先后两趟各开各组。

分组从 SlotModelSlots 经该车的记录读（L2SlotGroups.psm1），脚本里不写 1～4／5～8。断言对端收到的命令与库里的状态时，
都先用 Wait-L2Condition 等到它出现，再取样断言。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SlotGroups.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection

# 与边车一致。
$stationName = 'N1-3_N2-5'
$stationRiotId = $Context.PickupStationRiotId
# L2-PACKAGE 每篮 4 盒，8 盒就是 2 个花篮。
$basketCount = 2
$maxBoxCount = 8
$serverLog = Join-Path (Split-Path -Parent $Context.SnapshotRoot) 'logs/control-server.out.log'
# 名字里带这些字样的原因码才可能是在判站点或开门侧一致性。今天的原因码目录里没有这样的码——这条断言守的正是不要把它加回来。
$stationConsistencyPattern = 'STATION|SIDE|SLOT_POSITION|MIXED|CONSISTEN'
# 场景里各个时点见过的积压原因码，最后一起判。
$observedReasons = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)

function Publish-Demand([string]$Label, [string]$Area) {
    $guid = [guid]::NewGuid()
    $sublot = "L2-MSS-$Label-$($Context.RunId)"
    $journal.Note("Publishing demand $($guid.ToString('N')) (sublot $sublot, area $Area, $maxBoxCount boxes).")
    $null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot      = $sublot
        area        = $Area
        eqp         = "EQP-L2-MSS-$Label"
        package     = 'L2-PACKAGE'
        maxBoxCount = $maxBoxCount
    })
    return $guid.ToString('D')
}

function Get-Runtime([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$DemandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$DemandId, [string]$Purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$DemandId' AND Purpose = '$Purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-CommandSlots([string]$DemandId, [string]$OperationType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'SlotOperationCommand' ORDER BY CreatedAt, MessageId"
    $commands = @($rows | ForEach-Object { ([string]$_.PayloadJson | ConvertFrom-Json).payload } |
        Where-Object { [string]$_.demandId -eq $DemandId -and [string]$_.operationType -ceq $OperationType })
    return , $commands
}

function Get-Count([string]$Sql) {
    return [int](Invoke-L2Query -Connection $connection -Sql $Sql)[0].N
}

function Save-BacklogReasons {
    $rows = Invoke-L2Query -Connection $connection -Sql 'SELECT ReasonCode FROM JourneyBacklog'
    foreach ($row in $rows) {
        if (-not [string]::IsNullOrEmpty([string]$row.ReasonCode)) { $null = $observedReasons.Add([string]$row.ReasonCode) }
    }
}

function Move-Vehicle([string]$VehicleKey, [object]$Intent, [int]$StationRiotId) {
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 3; executeVehicleKey = $VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $Intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $StationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 5 })
}

function Wait-Stage([string]$DemandId, [string]$Stage, [string]$Description, [int]$TimeoutSeconds = 120) {
    return Wait-L2Condition -Description $Description -Journal $journal -Criterion 'journey-stage' `
        -TimeoutSeconds $TimeoutSeconds `
        -Probe { $r = Get-Runtime $DemandId; if ($r) { [string]$r.Stage } else { $null } } `
        -Until { param($v) $v -eq $Stage }
}

function Wait-Intent([string]$DemandId, [string]$Purpose) {
    return Wait-L2Condition -Description "the $Purpose intent was confirmed" `
        -Journal $journal -Criterion "$($Purpose.ToLowerInvariant())-intent" -TimeoutSeconds 60 `
        -Probe { Get-Intent $DemandId $Purpose } -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
}

<#
一趟：放需求、等受理、断言目标仓在该组，开到 12 号站装货、开到关卡卸货，等到卸货命令发出且旅程 Completed，再断言装卸命令
开的都是目标仓。返回这趟的旅程行。
#>
function Invoke-Trip([string]$Label, [string]$Name, [string]$Area, [string]$Group, [string]$IdPrefix) {
    $demandId = Publish-Demand $Label $Area
    $null = Wait-Stage $demandId 'AwaitingPickupArrival' "demand $Label ($Area) was accepted and dispatched to the pickup station" 90
    Save-BacklogReasons

    $null = Assert-L2SlotGroupTargets -Assertions $assertions -Id "$IdPrefix-1" -Connection $connection `
        -DemandId $demandId -SlotPosition $Group `
        -Description "需求$Name（$Area，$basketCount 花篮）：目标仓全部属于 $Group 组、升序，且恰好是该组编号最小的 $basketCount 个可用仓"
    $runtime = Get-Runtime $demandId
    $targets = @([string]$runtime.TargetSlotsJson | ConvertFrom-Json | ForEach-Object { [int]$_ })
    $vehicleKey = [string]$runtime.VehicleKey
    $journal.Note("Demand $Label went to $($runtime.AgvId) ($vehicleKey), target slots [$($targets -join ',')].")

    $pickupIntent = Wait-Intent $demandId 'TO_PICKUP'
    $journal.Note("Vehicle drives to station $stationRiotId ($stationName) for demand $Label and comes to rest.")
    Move-Vehicle $vehicleKey $pickupIntent $stationRiotId
    $null = Wait-Stage $demandId 'AwaitingGateArrival' "demand $Label loaded and its journey reached the gate leg"
    $gateIntent = Wait-Intent $demandId 'TO_GATE'
    $journal.Note("Vehicle drives to the gate for demand $Label and comes to rest.")
    Move-Vehicle $vehicleKey $gateIntent $Context.GateStationRiotId
    $null = Wait-L2Condition -Description "the UNLOAD command for demand $Label was sent to the vehicle" `
        -Journal $journal -Criterion 'unload-command' -TimeoutSeconds 120 `
        -Probe { (Get-CommandSlots $demandId 'UNLOAD').Count } -Until { param($v) $v -ge 1 }
    $stage = Wait-Stage $demandId 'Completed' "demand $Label completed at the gate"
    Save-BacklogReasons

    $runtime = Get-Runtime $demandId
    $sent = @()
    foreach ($type in 'LOAD', 'UNLOAD') {
        # Assigned before it is piped: the result set would otherwise arrive as one element (README).
        $commands = Get-CommandSlots $demandId $type
        $slots = @($commands | ForEach-Object { (@($_.slots | ForEach-Object { [int]$_ }) -join ',') } | Sort-Object -Unique)
        $sent += "$type " + $(if ($slots.Count -eq 0) { '(none)' } else { ($slots | ForEach-Object { "[$_]" }) -join ' ' })
    }
    $expectedSent = "LOAD [$($targets -join ',')]; UNLOAD [$($targets -join ',')]"
    $assertions.Add(
        "$IdPrefix-2", "需求$Name 走完一趟，没有被阻断：取货停 $stationRiotId 号站，装货与卸货命令开的都是目标仓（$Group 组）",
        ($stage -eq 'Completed' -and [string]::IsNullOrEmpty([string]$runtime.BlockReasonCode) -and
            [int]$runtime.PickupStationRiotId -eq $stationRiotId -and ($sent -join '; ') -ceq $expectedSent),
        "Completed / no block / station $stationRiotId / $expectedSent",
        "$stage / block '$($runtime.BlockReasonCode)' / station $($runtime.PickupStationRiotId) / $($sent -join '; ')")
    return [pscustomobject]@{ DemandId = $demandId; Runtime = $runtime; Targets = $targets }
}

<#
服务端日志里 Warning 及以上、提到任一 needle 的记录。Serilog 控制台格式一条记录以 `[时:分:秒 级别]` 开头，可能跨多行，
所以按记录切分再判。日志文件此刻仍被服务端写着，用共享读打开。
#>
function Get-WarningRecordsMentioning([string[]]$Needles) {
    $stream = [IO.File]::Open($serverLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $text = [IO.StreamReader]::new($stream).ReadToEnd()
    }
    finally {
        $stream.Dispose()
    }
    $matched = [Collections.Generic.List[string]]::new()
    foreach ($record in [regex]::Split($text, '(?m)^(?=\[\d{2}:\d{2}:\d{2} [A-Z]{3}\] )')) {
        if ($record -notmatch '^\[\d{2}:\d{2}:\d{2} (WRN|ERR|FTL)\] ') { continue }
        foreach ($needle in $Needles) {
            if ($record.Contains($needle, [StringComparison]::OrdinalIgnoreCase)) {
                $matched.Add($record.Trim())
                break
            }
        }
    }
    return , $matched.ToArray()
}

# --- 0. 前置：12 号站挂两侧区域号，归属表把它们分到两侧 --------------------------------------------------

$mapStations = @(@($riot.Snapshot().body.maps | Where-Object { [int]$_.mapId -eq $Context.MapId }) | ForEach-Object { $_.stations })
$pickupStation = @($mapStations | Where-Object { [int]$_.id -eq $stationRiotId })
$table = & $Context.InvokeFieldOps -Arguments @('area-assignments')
$tableEntries = @($table.entries | ForEach-Object { "$($_.area)/$($_.slotPosition)" })
$assertions.Add(
    'L2-MSS-01', "前置：取货点 $stationRiotId 号站名为 $stationName，归属表 N1-3 → FRONT、N2-5 → REAR（同站两侧）",
    ($pickupStation.Count -eq 1 -and [string]$pickupStation[0].name -ceq $stationName -and
        [string]$table.outcome -eq 'OK' -and ($tableEntries -join ',') -ceq 'N1-3/FRONT,N2-5/REAR'),
    "$stationRiotId=$stationName / N1-3/FRONT,N2-5/REAR",
    "$(if ($pickupStation.Count -eq 1) { "$stationRiotId=$($pickupStation[0].name)" } else { "$($pickupStation.Count) stations with id $stationRiotId" }) / $($tableEntries -join ',')")
$positions = Get-L2VehicleSlotPositions -Connection $connection -AgvId $Context.AgvId
if ($null -eq $positions) { throw "Vehicle $($Context.AgvId) has no resolvable slot model; the preseed did not bind it." }
$journal.Note("Vehicle $($Context.AgvId) slot model $($positions.SlotModelVersionId) via $($positions.Source): " +
    (($positions.Positions.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))

# --- 1. 第一趟：需求甲（N1-3）开 FRONT 组 ----------------------------------------------------------------

$tripA = Invoke-Trip 'A' '甲' 'N1-3' 'FRONT' 'L2-MSS-02'

# --- 2. 第二趟：需求乙（N2-5）开 REAR 组，同一个站点 ------------------------------------------------------

$tripB = Invoke-Trip 'B' '乙' 'N2-5' 'REAR' 'L2-MSS-03'

$assertions.Add(
    'L2-MSS-04', '同一台车两趟停的是同一个站点，而开的是不相交的两组仓',
    ([string]$tripA.Runtime.AgvId -ceq [string]$tripB.Runtime.AgvId -and
        [string]$tripA.Runtime.PickupStationId -ceq [string]$tripB.Runtime.PickupStationId -and
        [int]$tripA.Runtime.PickupStationRiotId -eq [int]$tripB.Runtime.PickupStationRiotId -and
        @($tripA.Targets | Where-Object { $_ -in $tripB.Targets }).Count -eq 0),
    "same vehicle / same station $stationRiotId / disjoint slots",
    "A $($tripA.Runtime.AgvId) $($tripA.Runtime.PickupStationId) ($($tripA.Runtime.PickupStationRiotId)) [$($tripA.Targets -join ',')], " +
    "B $($tripB.Runtime.AgvId) $($tripB.Runtime.PickupStationId) ($($tripB.Runtime.PickupStationRiotId)) [$($tripB.Targets -join ',')]")

# --- 3. 全程：无结构性阻断、无站点一致性原因、无告警 ----------------------------------------------------------

# 再转几轮，让「之后也没有冒出来」有机会被看见。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
Save-BacklogReasons

$blocks = Get-Count 'SELECT COUNT(*) AS N FROM StructuralDispatchBlocks'
$assertions.Add(
    'L2-MSS-05', 'StructuralDispatchBlocks 无行（含已清除的）：混挂站点不形成结构性派车阻断',
    ($blocks -eq 0), '0 rows', "$blocks rows")

$stationReasons = @($observedReasons | Where-Object { $_ -cmatch $stationConsistencyPattern })
$assertions.Add(
    'L2-MSS-06', "积压里没有与站点一致性相关的原因码：各时点见过的原因码里没有带站点、开门侧、分组、混挂或一致性字样的",
    ($stationReasons.Count -eq 0),
    'none',
    "$(if ($stationReasons.Count -eq 0) { 'none' } else { $stationReasons -join ',' }) (observed: $(if ($observedReasons.Count -eq 0) { '(none)' } else { @($observedReasons) -join ',' }))")

$warnings = Get-WarningRecordsMentioning @($stationName, "station $stationRiotId", "station=$stationRiotId", 'N1-3', 'N2-5')
# 服务端 Information 级别也不写站名，所以这里另记一笔全部 Warning 及以上的条数，读证据的人能看出这条断言面对的是什么样的日志。
$allWarnings = Get-WarningRecordsMentioning @('')
$journal.Note("Server log at assertion time: $($allWarnings.Count) Warning-or-above record(s) in total, $($warnings.Count) naming the station or its areas.")
$assertions.Add(
    'L2-MSS-07', "服务端日志里没有针对该站点（站名 $stationName、站号、两个区域号）的 Warning 及以上记录",
    ($warnings.Count -eq 0 -and (Test-Path -LiteralPath $serverLog)),
    '0 records', $(if ($warnings.Count -eq 0) { '0 records' } else { ($warnings | Select-Object -First 3) -join ' | ' }))

$journal.Note('混挂站点先后两趟各开各组，没有阻断、没有站点一致性原因码、没有针对该站点的告警。')
