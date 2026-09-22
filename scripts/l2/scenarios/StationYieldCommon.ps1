#Requires -Version 7

<#
批次7-08（control-server#213）两条让站场景共用的布置：两台合成车、一台在站上持货等单、另一台被承诺以这个站为下一停靠。
由场景点号引入，不是场景本身，没有 setup 文件（与 CargoHoldingCommon.ps1 同一种安排，并且点号引入了它）。

**「另一辆车」是确定性造出来的，不靠时序。**两台车都停在关卡上、都没接过单，需求甲按车辆侧排序的兜底层（车号）落在
AGV-L2-001 上——这一步读出来再用，不假设：读到的持单车不是它，场景当场停下并说明为什么。需求乙占 4 个花篮、区号在前侧：
持单车前侧已被甲占去 1 格，只因本车货物装不下（它的判满理由），所以乙只能给另一台车。乙被受理的那一刻，另一台车的下一停靠
就是持单车所在的站——这就是让站的触发，而且它发生在受理事务里，不取决于两台车谁先跑哪一轮。

**写入边界**（scripts/l2/README.md 第 14 条那张表，这一票的那一格）：触发的两列（持单车 JourneyRuntimes.YieldTriggeredAt、
YieldTriggeredByVehicleKey）与另一台车的受理是同一次提交；持单车的 CLOSED/WAITING_STATION_YIELD 与那张快照是持单车自己
下一轮的另一次写入。所以「乙受理了」之后要另等装货阶段变，不能直读。

**探针不取闭包**，理由见 CargoHoldingCommon.ps1 开头。
#>

Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'CargoHoldingCommon.ps1')

# setup 文件 Fleet 里的那一台。主车一对（AGV-L2-001/BROKERX-L2-0001）由编排器给，在 $Context 上。
$script:YieldSecondAgvId = 'AGV-L2-002'
$script:YieldSecondVehicleKey = 'BROKERX-L2-0002'

function Get-L2YieldVehicleKey([object]$Context, [string]$AgvId) {
    if ($AgvId -eq $Context.AgvId) { return $Context.VehicleKey }
    if ($AgvId -eq $script:YieldSecondAgvId) { return $script:YieldSecondVehicleKey }
    throw "No vehicle key known for $AgvId."
}

# 两台车都停在关卡上，路网引擎拉起来（CargoHoldingCommon 的前置只摆主车）。
function Initialize-L2YieldRig([object]$Context) {
    $null = $Context.Riot.Command('Put', 'vehicle', @{
        vehicleKey = $script:YieldSecondVehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Context.GateStationRiotId
    })
    Initialize-L2CargoRig $Context
}

# 按 VehicleKey 开车到这趟旅程的当前停靠。与 Move-L2CargoVehicleToCurrentStop 同一步骤，只是车不一定是主车。
function Move-L2YieldVehicleToCurrentStop([object]$Context, [string]$JourneyId, [string]$VehicleKey, [int]$StationRiotId) {
    $connection = $Context.Connection
    $stop = Wait-L2Condition -Description "journey $JourneyId's current stop is at station $StationRiotId" `
        -Journal $Context.Journal -Criterion 'current-stop' -TimeoutSeconds 180 `
        -Probe { Get-L2CargoCurrentStop $connection $JourneyId } `
        -Until { param($v) $null -ne $v -and [int]$v.StationRiotId -eq $StationRiotId }
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
    $Context.Journal.Note("Vehicle $VehicleKey drives to stop $($stop.Sequence) ($($stop.StopRole) at station $($stop.StationRiotId)).")
    $null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = [int]$stop.StationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })
    return $stop
}

# 这条需求所在旅程的车与触发两列。
function Get-L2YieldJourney([object]$Connection, [string]$DemandId) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT r.JourneyId, r.AgvId, r.VehicleKey, r.Stage, r.LoadingPhaseState, r.LoadingClosedReason, " +
        "r.YieldTriggeredAt, r.YieldTriggeredByVehicleKey, r.ConsumedSafetyResultMessageId, r.BlockReasonCode " +
        "FROM JourneyRuntimes r JOIN JourneyDemands d ON d.JourneyId = r.JourneyId " +
        "WHERE d.DemandId = '$DemandId' AND d.RemovedAt IS NULL")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 某一台车的装货阶段快照。车辆业务状态快照的载荷里没有车号，按信封上的 agvId 分。
function Get-L2YieldSnapshotsOf([object]$Connection, [string]$AgvId) {
    $closureIds = Get-L2JourneyClosureBusinessStateIds $Connection
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT MessageId, PayloadJson, CreatedAt FROM ProtocolOutbox WHERE MessageType = 'VehicleBusinessStateSnapshot'")
    $snapshots = foreach ($row in $rows) {
        # 旅程收尾那一张按 messageId 跳过，别的不带 loadingPhase 照旧抛，理由同 Get-L2LoadingPhaseSnapshots。
        if ($closureIds.Contains([string]$row.MessageId)) { continue }
        $envelope = [string]$row.PayloadJson | ConvertFrom-Json -DateKind String
        if ([string]$envelope.agvId -ne $AgvId) { continue }
        $payload = $envelope.payload
        if ($null -eq $payload.loadingPhase) {
            throw ("VehicleBusinessStateSnapshot $($row.MessageId) (revision $($payload.vehicleBusinessStateRevision)) for $AgvId " +
                'has no loadingPhase and is not a journey closure snapshot.')
        }
        [pscustomobject]@{
            Revision  = [long]$payload.vehicleBusinessStateRevision
            State     = [string]$payload.loadingPhase.state
            Reason    = if ($null -eq $payload.loadingPhase.closedReason) { $null } else { [string]$payload.loadingPhase.closedReason }
            MessageId = [string]$row.MessageId
            CreatedAt = [DateTimeOffset]::Parse([string]$row.CreatedAt, [Globalization.CultureInfo]::InvariantCulture)
        }
    }
    return , @($snapshots | Sort-Object Revision)
}

# 一趟旅程的关卡腿订单意图（非取货停靠的那一段），没有为空。
function Get-L2YieldGateIntent([object]$Connection, [string]$JourneyId) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT i.UpperId, i.CreatedAt, i.Status FROM OrderIntents i JOIN JourneyStops s ON s.UpperId = i.UpperId " +
        "WHERE s.JourneyId = '$JourneyId' AND s.StopRole <> 'PICKUP'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function ConvertTo-L2YieldInstant([object]$Value) {
    if ($null -eq $Value -or [string]$Value -eq '') { return $null }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture)
}

<#
布置到「持单车在站上持货等单」为止：需求甲受理、车开到取货站、装完、进入 CARGO_HOLDING_WAIT。返回甲、持单车那一趟、
持单车与另一台车的 AgvId。第一条判据（持单车是谁、进入了等单）在这里落表，前缀由场景给。
#>
function Start-L2YieldHolder([object]$Context, [string]$Prefix) {
    $connection = $Context.Connection
    $a = New-L2CargoDemand 'A' 'N1-3' 1 $Context.RunId
    Publish-L2CargoDemand $Context $a
    $journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $Context.Journal -Criterion 'journey-a' -TimeoutSeconds 120 `
        -Probe { Get-L2YieldJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
    $holderAgvId = [string]$journey.AgvId
    if ($holderAgvId -ne $Context.AgvId) {
        # 两台车停在同一个站、都没接过单，排序的最后一层（车号）必然给主车。不是它，说明前面某一层起了作用，
        # 这条场景的「另一台车」就不是确定性造出来的了——停下，而不是换一台继续。
        throw "Demand A went to $holderAgvId, not $($Context.AgvId): the scenario's premise (vehicle-id tie-break between two idle vehicles at the gate) does not hold."
    }
    $null = Move-L2YieldVehicleToCurrentStop $Context ([string]$journey.JourneyId) $Context.VehicleKey $Context.PickupStationRiotId
    $waiting = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('CARGO_HOLDING_WAIT', 'VEHICLE_FULL', 'CLOSED') `
        -Criterion 'phase-wait'
    $Context.Assertions.Add(
        "$Prefix-01", "持单车 $holderAgvId 装完需求甲（1 花篮）、两侧都没满，在 $($Context.PickupStationRiotId) 号站上 CARGO_HOLDING_WAIT",
        ([string]$waiting.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and [string]$waiting.Stage -eq 'AwaitingStationDeparture'),
        'AwaitingStationDeparture CARGO_HOLDING_WAIT', (Format-L2CargoJourney $waiting))
    return [pscustomobject]@{
        DemandA     = $a
        JourneyId   = [string]$journey.JourneyId
        HolderAgvId = $holderAgvId
        ComerAgvId  = $script:YieldSecondAgvId
    }
}

<#
需求乙（4 花篮、前侧、同一个站）发出去，等另一台车受理。返回乙、另一台车那一趟的受理时刻（AcceptedDemands.AcceptedAt）。
#>
function Send-L2YieldComer([object]$Context, [object]$Holder, [string]$Prefix) {
    $connection = $Context.Connection
    $b = New-L2CargoDemand 'B' 'N1-3' 4 $Context.RunId
    Publish-L2CargoDemand $Context $b
    $comer = Wait-L2Condition -Description 'demand B was accepted' -Journal $Context.Journal -Criterion 'journey-b' -TimeoutSeconds 120 `
        -Probe { Get-L2YieldJourney $connection $b.Id } -Until { param($v) $null -ne $v }
    $acceptedRows = Invoke-L2Query -Connection $connection -Sql "SELECT AcceptedAt FROM AcceptedDemands WHERE DemandId = '$($b.Id)'"
    $acceptedAt = ConvertTo-L2YieldInstant $acceptedRows[0].AcceptedAt
    $nextStop = Get-L2CargoCurrentStop $connection ([string]$comer.JourneyId)
    $Context.Assertions.Add(
        "$Prefix-02", "需求乙由另一台车 $($Holder.ComerAgvId) 受理，它的下一停靠就是持单车所在的 $($Context.PickupStationRiotId) 号站",
        ([string]$comer.AgvId -eq $Holder.ComerAgvId -and [string]$comer.Stage -eq 'AwaitingPickupArrival' -and
            $null -ne $nextStop -and [int]$nextStop.StationRiotId -eq $Context.PickupStationRiotId),
        "$($Holder.ComerAgvId) AwaitingPickupArrival → $($Context.PickupStationRiotId)",
        "$($comer.AgvId) $($comer.Stage) → $(if ($nextStop) { $nextStop.StationRiotId } else { '(none)' })")
    return [pscustomobject]@{ DemandB = $b; AcceptedAt = $acceptedAt; JourneyId = [string]$comer.JourneyId }
}

<#
让站确实触发过（正事实，两条场景都要）：持单车 CLOSED/WAITING_STATION_YIELD，触发两列有值、记的是另一台车、时刻不早于受理，
而且车上收到了那张快照。返回持单车那一行。
#>
function Confirm-L2YieldTriggered([object]$Context, [object]$Holder, [object]$Comer, [string]$Prefix) {
    $connection = $Context.Connection
    $closed = Wait-L2LoadingPhase -Context $Context -DemandId $Holder.DemandA.Id -States @('CLOSED') -Criterion 'phase-yield' -TimeoutSeconds 60
    $row = Get-L2YieldJourney $connection $Holder.DemandA.Id
    $triggeredAt = ConvertTo-L2YieldInstant $row.YieldTriggeredAt
    $comerKey = Get-L2YieldVehicleKey $Context $Holder.ComerAgvId
    $Context.Assertions.Add(
        "$Prefix-03", '另一台车受理之后，持单车装货阶段 CLOSED/WAITING_STATION_YIELD；触发列记的是那台车，时刻不早于受理',
        ([string]$closed.LoadingPhaseState -eq 'CLOSED' -and [string]$closed.LoadingClosedReason -eq 'WAITING_STATION_YIELD' -and
            [string]$row.YieldTriggeredByVehicleKey -eq $comerKey -and $null -ne $triggeredAt -and $null -ne $Comer.AcceptedAt -and
            $triggeredAt -ge $Comer.AcceptedAt),
        "CLOSED/WAITING_STATION_YIELD by $comerKey at or after $($Comer.AcceptedAt.ToString('o'))",
        "$(Format-L2CargoJourney $closed) by '$($row.YieldTriggeredByVehicleKey)' at $(if ($triggeredAt) { $triggeredAt.ToString('o') } else { '(null)' })")
    $snapshot = Wait-L2ConditionOrLast -Description 'the holder was sent CLOSED/WAITING_STATION_YIELD' -Journal $Context.Journal `
        -Criterion 'yield-snapshot' -TimeoutSeconds 30 `
        -Probe { @((Get-L2YieldSnapshotsOf $connection $Holder.HolderAgvId) | Where-Object { $_.Reason -eq 'WAITING_STATION_YIELD' }) } `
        -Until { param($v) @($v).Count -ge 1 }
    $Context.Assertions.Add(
        "$Prefix-04", '持单车收到了 CLOSED/WAITING_STATION_YIELD 那张车辆业务状态快照',
        (@($snapshot).Count -ge 1), '>= 1', @($snapshot).Count)
    return $row
}
