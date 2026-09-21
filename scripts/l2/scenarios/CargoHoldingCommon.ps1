#Requires -Version 7

<#
批次7-07（control-server#212）五条持货场景共用的发需求、读装货阶段、开车到当前停靠。由各场景点号引入，不是场景本身，
没有 setup 文件，编排器也不会单独运行它（与 G3RecoveryCommon.ps1 同一种安排）。

断言只读服务端的库、假 RIoT 与假 MesIngest 的快照；这里的函数只读与驱动。

**写入边界**（scripts/l2/README.md 第 14 条要的那张表，这一票的那一格）：装货阶段三列（JourneyRuntimes.LoadingPhaseState、
LoadingClosedReason、CargoHoldingStartedAt）与它引出的那一张车辆业务状态快照是**同一次保存**
（JourneyRuntimeEngine.ReconcileLoadingPhaseAsync：先改旅程行，再由发布把发件箱行与旅程行一起存）。所以等到列变了再读快照是
安全的；反过来也一样。起算点另是装货落定那一次保存（与 JourneyDemands.Status = LOADED 同一次），早于状态变成 WAIT／FULL。

**探针不取闭包。**在函数体里对探针调 .GetNewClosure()，快照只收那一个作用域里的变量，参数与外层变量都读成空，
探针拿空需求号去查，永远等到超时（scripts/l2/L2ClosureCapture.psm1 的网格，control-server#266）。不取闭包的脚本块记得
定义它的作用域，Wait-L2Condition 调它时这些函数还在栈上，参数读得到。
#>

Set-StrictMode -Version Latest

# 一条需求：Wire 是 MesIngest 的不带连字符写法，Id 是服务端存的规范写法。L2-PACKAGE 每篮 4 箱（编排器导入的容量表），
# 所以 Boxes = 4 × 花篮数。
function New-L2CargoDemand([string]$Label, [string]$Area, [int]$Baskets, [string]$RunId) {
    $guid = [guid]::NewGuid()
    return @{
        Label   = $Label
        Wire    = $guid.ToString('N')
        Id      = $guid.ToString('D')
        Sublot  = "L2-CH-$Label-$RunId"
        Area    = $Area
        Baskets = $Baskets
    }
}

function Publish-L2CargoDemand([object]$Context, [hashtable]$Demand) {
    $Context.Journal.Note("Publishing demand $($Demand.Label) $($Demand.Wire) (area $($Demand.Area), $($Demand.Baskets) baskets).")
    $null = $Context.MesIngest.Command('Put', "demands/$($Demand.Wire)", @{
        sublot      = $Demand.Sublot
        area        = $Demand.Area
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4 * $Demand.Baskets
    })
}

# 追加进来的需求没有自己的旅程行，所以按归属找旅程。
function Get-L2CargoJourney([object]$Connection, [string]$DemandId) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT r.JourneyId, r.Stage, r.LoadingPhaseState, r.LoadingClosedReason, r.CargoHoldingStartedAt, " +
        "r.FullSlotPositionsJson, r.BlockReasonCode FROM JourneyRuntimes r " +
        "JOIN JourneyDemands d ON d.JourneyId = r.JourneyId WHERE d.DemandId = '$DemandId' AND d.RemovedAt IS NULL")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Format-L2CargoJourney([object]$Row) {
    if ($null -eq $Row) { return '(no journey)' }
    $state = if ($null -eq $Row.LoadingPhaseState -or [string]$Row.LoadingPhaseState -eq '') { 'LOADING(null)' } else { [string]$Row.LoadingPhaseState }
    $reason = if ($null -eq $Row.LoadingClosedReason -or [string]$Row.LoadingClosedReason -eq '') { '' } else { "/$($Row.LoadingClosedReason)" }
    return "$($Row.Stage) $state$reason"
}

# 等这条需求所在旅程的装货阶段落到 $States 之一。列为空与 LOADING 同义。超时不抛：把最后读到的交回去，由判据落表。
function Wait-L2LoadingPhase {
    param(
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][string]$DemandId,
        [Parameter(Mandatory)][string[]]$States,
        [Parameter(Mandatory)][string]$Criterion,
        [int]$TimeoutSeconds = 120
    )
    $connection = $Context.Connection
    return Wait-L2ConditionOrLast -Description "the loading phase of $DemandId's journey is one of $($States -join ', ')" `
        -Journal $Context.Journal -Criterion $Criterion -TimeoutSeconds $TimeoutSeconds `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection -Sql (
                "SELECT r.JourneyId, r.Stage, r.LoadingPhaseState, r.LoadingClosedReason, r.CargoHoldingStartedAt, " +
                "r.FullSlotPositionsJson, r.BlockReasonCode FROM JourneyRuntimes r " +
                "JOIN JourneyDemands d ON d.JourneyId = r.JourneyId WHERE d.DemandId = '$DemandId' AND d.RemovedAt IS NULL")
            if ($rows.Count -eq 0) { return $null }
            return $rows[0]
        } `
        -Until {
            param($v)
            if ($null -eq $v) { return $false }
            $state = if ($null -eq $v.LoadingPhaseState -or [string]$v.LoadingPhaseState -eq '') { 'LOADING' } else { [string]$v.LoadingPhaseState }
            return $States -contains $state
        }
}

# 发件箱里全部车辆业务状态快照的 loadingPhase，按修订号。
function Get-L2LoadingPhaseSnapshots([object]$Connection) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT MessageId, PayloadJson, CreatedAt FROM ProtocolOutbox WHERE MessageType = 'VehicleBusinessStateSnapshot'")
    $snapshots = foreach ($row in $rows) {
        # -DateKind String：否则 ConvertFrom-Json 把时刻转成 DateTime，偏移量随之丢掉，比较期限时差出时区那几个小时。
        $payload = ([string]$row.PayloadJson | ConvertFrom-Json -DateKind String).payload
        [pscustomobject]@{
            Revision  = [long]$payload.vehicleBusinessStateRevision
            State     = [string]$payload.loadingPhase.state
            Deadline  = if ($null -eq $payload.loadingPhase.cargoHoldingDeadlineAt) { $null } else {
                [DateTimeOffset]::Parse([string]$payload.loadingPhase.cargoHoldingDeadlineAt, [Globalization.CultureInfo]::InvariantCulture) }
            Reason    = if ($null -eq $payload.loadingPhase.closedReason) { $null } else { [string]$payload.loadingPhase.closedReason }
            MessageId = [string]$row.MessageId
        }
    }
    return , @($snapshots | Sort-Object Revision)
}

function Format-L2LoadingPhaseSnapshots([object[]]$Snapshots) {
    if (@($Snapshots).Count -eq 0) { return '(none)' }
    return (@($Snapshots) | ForEach-Object {
            $tail = if ($_.Reason) { "/$($_.Reason)" } else { '' }
            "$($_.Revision):$($_.State)$tail"
        }) -join ' '
}

# 这趟旅程此刻的当前停靠：序位最小的、没完成也没被移除的那一个——与 JourneyStopCursor.Current 同一个定义。
function Get-L2CargoCurrentStop([object]$Connection, [string]$JourneyId) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT StopId, Sequence, StopRole, StationRiotId, UpperId, Status FROM JourneyStops " +
        "WHERE JourneyId = '$JourneyId' AND Status NOT IN ('COMPLETED', 'REMOVED') ORDER BY Sequence LIMIT 1")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 车开到这趟旅程的当前停靠：等那一段腿的订单确认，然后在假 RIoT 上走一遍「在动 → 停稳在站上 → 单成功」。
#
# $StationRiotId 是车要开去的站。必须给：车装完之后还停在上一个停靠上等（站点等待、持货），那个停靠在它离站之前一直是
# 「当前停靠」，不等到当前停靠换到目标站就开，开的就是车脚下那一站（第一次跑这条场景就是这样，把第一站又走了一遍）。
#
# $StopId 可选：同一个站上先后两个停靠时（车停在最后装货站等单时同站追加的需求，批次7-06 的口径：当前停靠不并，另开一个），
# 只按站号等会在前一个停靠上就放行，要按停靠本身等。
function Move-L2CargoVehicleToCurrentStop([object]$Context, [string]$JourneyId, [int]$StationRiotId, [string]$StopId = '') {
    $connection = $Context.Connection
    $stop = Wait-L2Condition -Description "journey $JourneyId's current stop is at station $StationRiotId $StopId" `
        -Journal $Context.Journal -Criterion 'current-stop' -TimeoutSeconds 180 `
        -Probe { Get-L2CargoCurrentStop $connection $JourneyId } `
        -Until {
            param($v)
            $null -ne $v -and [int]$v.StationRiotId -eq $StationRiotId -and ($StopId -eq '' -or [string]$v.StopId -eq $StopId)
        }
    $upperId = [string]$stop.UpperId
    $intent = Wait-L2Condition -Description "the order for stop $($stop.StopId) ($upperId) was confirmed" `
        -Journal $Context.Journal -Criterion 'stop-intent' -TimeoutSeconds 120 `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection `
                -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE UpperId = '$upperId'"
            if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
        } `
        -Until { param($v) $null -ne $v }
    $riot = $Context.Riot
    $Context.Journal.Note("Vehicle drives to stop $($stop.Sequence) ($($stop.StopRole) at station $($stop.StationRiotId)).")
    $null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = [int]$stop.StationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })
    return $stop
}

# 车先停在关卡上、路网引擎把图拉起来：追加要算路径代价，两样缺一样服务端都 fail closed（与 multi-stop-append-same-zone 同一个前置）。
function Initialize-L2CargoRig([object]$Context) {
    $connection = $Context.Connection
    $null = $Context.Riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Context.GateStationRiotId
    })
    $mapId = $Context.MapId
    $null = Wait-L2Condition -Description 'the route graph engine finished a refresh cycle' `
        -Journal $Context.Journal -Criterion 'route-graph-ready' -TimeoutSeconds 120 `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection `
                -Sql ("SELECT DesignEdgeCount, RuntimeRefreshedAt, StaleReason FROM RouteGraphSnapshots WHERE MapId = $mapId")
            if ($rows.Count -eq 0) { return $false }
            return [int]$rows[0].DesignEdgeCount -gt 0 -and
                $null -ne $rows[0].RuntimeRefreshedAt -and [string]$rows[0].RuntimeRefreshedAt -ne '' -and
                ($null -eq $rows[0].StaleReason -or [string]$rows[0].StaleReason -eq '')
        } `
        -Until { param($v) $v }
}

# 一条需求的积压行：理由码与受理时刻。
function Get-L2CargoBacklog([object]$Connection, [string]$DemandId) {
    $rows = Invoke-L2Query -Connection $Connection -Sql "SELECT ReasonCode, AcceptedAt FROM JourneyBacklog WHERE DemandId = '$DemandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}
