#Requires -Version 7

<#
批次7-09（control-server#214）两条任务优先级场景共用的发需求、读旅程、开车走完一趟。由场景点号引入，不是场景本身，没有
setup 文件，编排器也不会单独运行它（与 CargoHoldingCommon.ps1 同一种安排）。

一台车，站点与任务类型绑定照批次 6 的 staging-to-wire-slot-group：关卡 210、机台站 12（N1-3_N1-7）、派工待送取货 305。
WIRE_TO_GATE 从机台站 12 取、送关卡 210；STAGING_TO_WIRE 从 305 取、送机台站 12。「车忙着」就是它正在跑一趟
WIRE_TO_GATE：旅程在途时服务端仍然每轮判积压里的需求（在途车走追加那条资格链，本区没配途中追加所以一律拒绝），
车跑完这一趟才空出来，空出来之后的那一轮按任务侧次序先问排在最前的那条。

**需求的建单时刻由场景写死**（假 MesIngest 的 createdAt）：等待年龄从它算，不写的话假 MesIngest 给「此刻减十分钟」，
而那恰好越过 60 秒的阈值——两条场景要的正是分得清「超时了」与「没超时」。

**写入边界**（scripts/l2/README.md 第 14 条那张表，这一票的那一格）：
- 受理（AcceptedDemands、JourneyRuntimes、JourneyBacklog.AcceptedAt）是一次提交；
- 升级告警（JourneyBacklog.StarvationEscalatedAt 与 StarvationEscalationParameterVersion）是轮末汇总的另一次提交，在那一轮
  所有受理之后；日志行在那次提交之后才写。所以「告警标记出现了」之后再读日志行要另等，不能直读；反过来也一样。

**探针不取闭包**，理由见 CargoHoldingCommon.ps1 开头。
#>

Set-StrictMode -Version Latest

$script:TaskPriorityMachineStation = 12
$script:TaskPriorityGateStation = 210
$script:TaskPriorityStagingStation = 305

# 一条需求：Wire 是 MesIngest 的不带连字符写法，Id 是服务端存的规范写法。WIRE_TO_GATE 用 N1-3、STAGING_TO_WIRE 用 N1-7，
# 同一个 AREA 的需求共用一台 EQP（一个 AREA 在目录里挂两台 EQP 是 AREA_EQP_NOT_UNIQUE，跑完的需求仍留在目录里）。
function New-L2PriorityDemand([string]$Label, [string]$TaskType, [DateTimeOffset]$CreatedAt, [string]$RunId) {
    $guid = [guid]::NewGuid()
    $area = if ($TaskType -ceq 'STAGING_TO_WIRE') { 'N1-7' } else { 'N1-3' }
    return @{
        Label     = $Label
        Wire      = $guid.ToString('N')
        Id        = $guid.ToString('D')
        TaskType  = $TaskType
        Sublot    = "L2-TPB-$Label-$RunId"
        Area      = $area
        Eqp       = "EQP-L2-TPB-$area"
        CreatedAt = $CreatedAt
    }
}

function Publish-L2PriorityDemand([object]$Context, [hashtable]$Demand) {
    $Context.Journal.Note(
        "Publishing $($Demand.TaskType) demand $($Demand.Label) $($Demand.Wire) (area $($Demand.Area), created locally at " +
        "$($Demand.CreatedAt.ToString('o'))).")
    $null = $Context.MesIngest.Command('Put', "demands/$($Demand.Wire)", @{
        sublot      = $Demand.Sublot
        area        = $Demand.Area
        eqp         = $Demand.Eqp
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
        workType    = $Demand.TaskType
        createdAt   = $Demand.CreatedAt.ToString('o')
    })
}

# 一条需求的积压行，没有时为 $null。
function Get-L2PriorityBacklog([object]$Connection, [string]$DemandId) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT ReasonCode, FirstSeenAt, LastSeenAt, AcceptedAt, StarvationEscalatedAt, StarvationEscalationParameterVersion " +
        "FROM JourneyBacklog WHERE DemandId = '$DemandId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-L2PriorityRuntime([object]$Connection, [string]$DemandId) {
    $rows = Invoke-L2Query -Connection $Connection -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$DemandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 给定几条需求里已经有旅程的那几条的标签，按标签排序、逗号连接；一条都没有时为空串。
function Get-L2PriorityAccepted([object]$Connection, [hashtable[]]$Demands) {
    $labels = @($Demands | Where-Object { $null -ne (Get-L2PriorityRuntime $Connection $_.Id) } | ForEach-Object { $_.Label })
    return (@($labels | Sort-Object) -join ',')
}

function Wait-L2PriorityStage([object]$Context, [string]$DemandId, [string]$Stage, [string]$Description, [int]$TimeoutSeconds = 120) {
    return Wait-L2Condition -Description $Description -Journal $Context.Journal -Criterion 'journey-stage' `
        -TimeoutSeconds $TimeoutSeconds `
        -Probe { $r = Get-L2PriorityRuntime $Context.Connection $DemandId; if ($r) { [string]$r.Stage } else { $null } } `
        -Until { param($v) $v -eq $Stage }
}

function Get-L2PriorityIntent([object]$Connection, [string]$DemandId, [string]$Purpose) {
    $rows = Invoke-L2Query -Connection $Connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$DemandId' AND Purpose = '$Purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 车从当前位置开到 $StationRiotId 并停稳，照 staging-to-wire-slot-group 的写法。
function Move-L2PriorityVehicle([object]$Context, [object]$Intent, [int]$StationRiotId) {
    $riot = $Context.Riot
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $Intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $StationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 5 })
}

# 把一条已受理的需求走完：到取货站、装货、到卸货站、卸货、旅程 Completed。车随后空出来。
function Complete-L2PriorityJourney([object]$Context, [hashtable]$Demand) {
    $connection = $Context.Connection
    $isStaging = $Demand.TaskType -ceq 'STAGING_TO_WIRE'
    $pickupStation = if ($isStaging) { $script:TaskPriorityStagingStation } else { $script:TaskPriorityMachineStation }
    $dropStation = if ($isStaging) { $script:TaskPriorityMachineStation } else { $script:TaskPriorityGateStation }
    $demandId = $Demand.Id

    $pickup = Wait-L2Condition -Description "demand $($Demand.Label)'s TO_PICKUP intent was confirmed" `
        -Journal $Context.Journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
        -Probe { Get-L2PriorityIntent $connection $demandId 'TO_PICKUP' } `
        -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
    Move-L2PriorityVehicle $Context $pickup $pickupStation
    $null = Wait-L2PriorityStage $Context $demandId 'AwaitingGateArrival' "demand $($Demand.Label) loaded and its journey reached the second leg"
    $drop = Wait-L2Condition -Description "demand $($Demand.Label)'s TO_GATE intent was confirmed" `
        -Journal $Context.Journal -Criterion 'to-gate-intent' -TimeoutSeconds 60 `
        -Probe { Get-L2PriorityIntent $connection $demandId 'TO_GATE' } `
        -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
    Move-L2PriorityVehicle $Context $drop $dropStation
    $null = Wait-L2PriorityStage $Context $demandId 'Completed' "demand $($Demand.Label)'s journey completed"
}

<#
服务端日志里 Warning 及以上、同时提到全部 needles 的记录（照 structural-block-oversized-demand 的写法）。Serilog 控制台格式
一条记录以 `[时:分:秒 级别]` 开头、可能跨多行，按记录切分再判；日志文件此刻仍被服务端写着，用共享读打开。
#>
function Get-L2PriorityWarningRecords([object]$Context, [string[]]$Needles) {
    $serverLog = Join-Path (Split-Path -Parent $Context.SnapshotRoot) 'logs/control-server.out.log'
    if (-not (Test-Path -LiteralPath $serverLog)) { return , @() }
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
        if (@($Needles | Where-Object { -not $record.Contains($_, [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0) {
            $matched.Add($record.Trim())
        }
    }
    return , $matched.ToArray()
}
