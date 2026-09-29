#Requires -Version 7

<#
固定公共站点单车位（批次8-20，control-server#391；REQ-0204 修订）：一个公共站点同时只由一台已到达车占用或一台已承诺车预占，
别的车不承接下一站为该点的新任务。两台合成车，两段。

**一、关卡（WIRE_TO_GATE）：车照常出发，不在机台等。**关卡在卸货端，受理时它不是任何车的下一站；车站到最后一个取货停靠上，
关卡才成为它的下一站。那一刻关卡被别的车占着，它照常出发（不带货停在机台、不连带占住机台——调度 2026-09-29 定），
每轮开头的补预占在关卡放了之后先给它。
  - A 接第一条、装完：关卡被 A 预占，到关卡后转占用，卸完仍占着（车还停在那里）。
  - B 接第二条、装完：关卡被 A 占着，B 照常出发（进入 AwaitingGateArrival），没有它的独占行。
  - A 离开关卡（RIoT 报它到了别的站）：第二个事实另等——关卡的独占交给 B（RESERVED），而且是在 A 那一段释放之后。

**二、派工待送站（STAGING_TO_WIRE）：别的车不接下一站为该点的新任务。**这是本票判据挡住新任务的地方：取货端就是公共站点。
  - H 接第一条：同一次受理预占 305。
  - 第二条同站需求进目录：H 占着 305 的整段时间里（在途预占、到站占用、装完为离站请求移动之后仍占用），它一直没被接走——
    别的车被挡，H 自己没有追加的分区参数也接不了。
  - H 到了机台 12（离点证据）：第二个事实另等——第二条需求被接走，而且受理时刻不早于 H 那一段在 305 上的释放时刻。

**红证据**（缺陷版本）：去掉本票的判据与受理预占，第二段的第二条需求在 H 仍占着 305 时就被另一台车接走，L2-FSO-06 变红。

两段的「第二个事实」都用 Wait-L2ConditionOrLast 另等，不在读到第一个事实时顺手读（scripts/l2/README.md 第 14 条）。
原因码不在这里断：两台车时积压行记的是最后判它的那辆车的理由，谁最后判不确定；原因码由 L1 精确断
（Batch8FixedStationSingleOccupancyTests）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection

$gate = 210
$staging = 305
$machineN13 = 12
$machineC15 = 11
$vehicleKeys = @{ 'AGV-L2-001' = 'BROKERX-L2-0001'; 'AGV-L2-002' = 'BROKERX-L2-0002' }

function Publish-Demand([string]$Suffix, [string]$WorkType, [string]$Area, [string]$Eqp) {
    $guid = [guid]::NewGuid()
    $journal.Note("Publishing $WorkType demand $Suffix $($guid.ToString('N')) (area $Area).")
    $null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot = "L2-FSO-$Suffix-$($Context.RunId)"; area = $Area; eqp = $Eqp
        package = 'L2-PACKAGE'; maxBoxCount = 4; workType = $WorkType
    })
    return $guid.ToString('D')
}

function Get-Runtime([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, AgvId, VehicleKey, Stage, BlockReasonCode FROM JourneyRuntimes WHERE DemandId = '$DemandId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$DemandId, [string]$Purpose) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$DemandId' AND Purpose = '$Purpose'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Held([int]$Station) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT VehicleKey, JourneyId, State, StationKind, RecordId FROM StationExclusivities " +
        "WHERE MapId = $($Context.MapId) AND StationId = $Station")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Record([int]$Station, [string]$JourneyId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT ReservedAt, OccupiedAt, ReleasedAt, ReleaseReason FROM StationExclusivityRecords " +
        "WHERE MapId = $($Context.MapId) AND StationId = $Station AND JourneyId = '$JourneyId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-AcceptedAt([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT AcceptedAt FROM AcceptedDemands WHERE DemandId = '$DemandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].AcceptedAt
}

function Get-Backlog([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT ReasonCode, FirstSeenAt, LastSeenAt FROM JourneyBacklog WHERE DemandId = '$DemandId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Time([object]$Value) { [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture) }

# RIoT 把单置为执行中并绑车，车动起来，然后停在目的站上，单置为完成（与 three-vehicle-exit 同形，按 vehicleKey 定向）。
function Move-Vehicle([string]$VehicleKey, [object]$Intent, [int]$Station) {
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 3; executeVehicleKey = $VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $Intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Station; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 5 })
}

# 只动车的位置，不带订单：一台空闲车离开它停着的站（离点证据的「当前站是另一个站」）。
function Set-VehiclePosition([string]$VehicleKey, [int]$Station) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0; currentPosition = $Station
    })
}

function Wait-Accepted([string]$DemandId, [string]$Label) {
    return Wait-L2Condition -Description "$Label was accepted and dispatched" -Journal $journal `
        -Criterion "accepted-$Label" -TimeoutSeconds 120 `
        -Probe { $r = Get-Runtime $DemandId; if ($r -and [string]$r.Stage -eq 'AwaitingPickupArrival') { $r } else { $null } } `
        -Until { param($v) $null -ne $v }
}

function Wait-Stage([string]$DemandId, [string]$Stage, [string]$Label) {
    return Wait-L2Condition -Description "$Label reached $Stage" -Journal $journal -Criterion "stage-$Label" `
        -TimeoutSeconds 180 -Probe { [string](Get-Runtime $DemandId).Stage } -Until { param($v) $v -eq $Stage }
}

function Wait-Confirmed([string]$DemandId, [string]$Purpose, [string]$Label) {
    return Wait-L2Condition -Description "the $Label $Purpose intent was confirmed" -Journal $journal `
        -Criterion "intent-$Label-$Purpose" -TimeoutSeconds 90 `
        -Probe { Get-Intent $DemandId $Purpose } -Until { param($v) [string]$v.Status -eq 'CONFIRMED' }
}

# === 一、关卡（WIRE_TO_GATE） ============================================================================

$wireA = Publish-Demand 'GATE-A' 'WIRE_TO_GATE' 'N1-3' 'EQP-L2-FSO-01'
$runtimeA = Wait-Accepted $wireA 'gate demand A'
$keyA = [string]$runtimeA.VehicleKey
# B 的需求在 A 接单之后、A 还在途时放：没有追加的分区参数，在途的 A 接不了，只有另一台空闲车接得了——谁是 B 由此确定。
$wireB = Publish-Demand 'GATE-B' 'WIRE_TO_GATE' 'C15-13' 'EQP-L2-FSO-02'
$runtimeB = Wait-Accepted $wireB 'gate demand B'
$keyB = [string]$runtimeB.VehicleKey
$journal.Note("Gate section: A = $keyA, B = $keyB.")
$assertions.Add('L2-FSO-01', '两条 WIRE_TO_GATE 需求落在两台不同的车上，受理时关卡都不是它们的下一站、没有任何独占行',
    ($keyA -ne $keyB -and $null -eq (Get-Held $gate)), "two vehicles, no row at $gate",
    "A=$keyA B=$keyB, row at ${gate}: $((Get-Held $gate)?.VehicleKey)")

Move-Vehicle $keyA (Wait-Confirmed $wireA 'TO_PICKUP' 'A') $machineN13
$null = Wait-Stage $wireA 'AwaitingGateArrival' 'A'
$heldByA = Wait-L2Condition -Description 'the gate was reserved for A once it became its next stop' -Journal $journal `
    -Criterion 'gate-reserved-for-a' -TimeoutSeconds 30 -Probe { Get-Held $gate } `
    -Until { param($v) $null -ne $v -and [string]$v.VehicleKey -eq $keyA }
Move-Vehicle $keyA (Wait-Confirmed $wireA 'TO_GATE' 'A') $gate
$null = Wait-Stage $wireA 'Completed' 'A'
$occupiedByA = Get-Held $gate
$assertions.Add('L2-FSO-02', 'A 装完后关卡成了它的下一站、被补预占给 A；到关卡后转占用，卸完车还停在那里仍是占用',
    ($null -ne $heldByA -and $null -ne $occupiedByA -and [string]$occupiedByA.VehicleKey -eq $keyA -and
        [string]$occupiedByA.State -eq 'OCCUPIED'),
    "$keyA OCCUPIED at $gate", "$(${occupiedByA}?.VehicleKey) $(${occupiedByA}?.State)")

Move-Vehicle $keyB (Wait-Confirmed $wireB 'TO_PICKUP' 'B') $machineC15
$null = Wait-Stage $wireB 'AwaitingGateArrival' 'B'
$whileAHolds = Get-Held $gate
$assertions.Add('L2-FSO-03', 'B 装完时关卡被 A 占着：B 照常出发（AwaitingGateArrival、没有阻断码），关卡仍是 A 的',
    ([string]$whileAHolds.VehicleKey -eq $keyA -and [string](Get-Runtime $wireB).Stage -eq 'AwaitingGateArrival' -and
        [string]::IsNullOrEmpty([string](Get-Runtime $wireB).BlockReasonCode)),
    "B AwaitingGateArrival, gate held by $keyA", "B $([string](Get-Runtime $wireB).Stage) block=$((Get-Runtime $wireB).BlockReasonCode), gate held by $($whileAHolds.VehicleKey)")

$journal.Note("A leaves the gate for station $machineC15 without an order (departure evidence).")
Set-VehiclePosition $keyA $machineC15
# 第二个事实另等：关卡交给 B。
$givenToB = Wait-L2ConditionOrLast -Description 'the gate was given to B after A left it' -Journal $journal `
    -Criterion 'gate-given-to-b' -TimeoutSeconds 30 -Probe { Get-Held $gate } `
    -Until { param($v) $null -ne $v -and [string]$v.VehicleKey -eq $keyB }
$aRecord = Get-Record $gate ([string]$runtimeA.JourneyId)
$bRecord = Get-Record $gate ([string]$runtimeB.JourneyId)
$assertions.Add('L2-FSO-04', 'A 离开关卡、凭离点证据释放之后，下一轮补预占把关卡给 B（RESERVED），B 的预占不早于 A 的释放',
    ($null -ne $givenToB -and [string]$givenToB.VehicleKey -eq $keyB -and [string]$givenToB.State -eq 'RESERVED' -and
        $null -ne $aRecord -and [string]$aRecord.ReleaseReason -eq 'DEPARTED_STATION' -and
        $null -ne $bRecord -and (Time $bRecord.ReservedAt) -ge (Time $aRecord.ReleasedAt)),
    "B RESERVED at $gate at or after A's DEPARTED_STATION release",
    "held: $(${givenToB}?.VehicleKey) $(${givenToB}?.State); A released $(${aRecord}?.ReleasedAt) ($(${aRecord}?.ReleaseReason)); B reserved $(${bRecord}?.ReservedAt)")

Move-Vehicle $keyB (Wait-Confirmed $wireB 'TO_GATE' 'B') $gate
$null = Wait-Stage $wireB 'Completed' 'B'
# 让关卡空出来，第二段不受它影响：B 离开关卡。
Set-VehiclePosition $keyB $machineC15

# === 二、派工待送站（STAGING_TO_WIRE） ===================================================================

$stagingFirst = Publish-Demand 'STAGING-1' 'STAGING_TO_WIRE' 'N1-3' 'EQP-L2-FSO-01'
$runtimeH = Wait-Accepted $stagingFirst 'staging demand 1'
$keyH = [string]$runtimeH.VehicleKey
$reserved = Get-Held $staging
$assertions.Add('L2-FSO-05', '第一条 STAGING_TO_WIRE 受理即预占派工待送站，持有者是接它的那台车那一趟',
    ($null -ne $reserved -and [string]$reserved.VehicleKey -eq $keyH -and [string]$reserved.State -eq 'RESERVED' -and
        [string]$reserved.JourneyId -eq [string]$runtimeH.JourneyId -and [string]$reserved.StationKind -eq 'FIXED_TASK_STATION'),
    "$keyH RESERVED FIXED_TASK_STATION", "$(${reserved}?.VehicleKey) $(${reserved}?.State) $(${reserved}?.StationKind)")

$stagingSecond = Publish-Demand 'STAGING-2' 'STAGING_TO_WIRE' 'N1-7' 'EQP-L2-FSO-03'
# 第一个事实：它进了积压、被判过好几轮，而一直没被接走（H 在途预占着 305）。
$backlog = Wait-L2Condition -Description 'the second staging demand was judged for several rounds' -Journal $journal `
    -Criterion 'second-judged' -TimeoutSeconds 60 -Probe { Get-Backlog $stagingSecond } `
    -Until { param($v) $null -ne $v -and ((Time $v.LastSeenAt) - (Time $v.FirstSeenAt)).TotalSeconds -ge 5 }
$acceptedWhileReserved = Get-AcceptedAt $stagingSecond
$journal.Observe('second-backlog', [string]$backlog.ReasonCode, @{ backlog = $backlog })

Move-Vehicle $keyH (Wait-Confirmed $stagingFirst 'TO_PICKUP' 'H') $staging
$null = Wait-Stage $stagingFirst 'AwaitingGateArrival' 'H'
# H 装完、为离站请求了移动，车还在 305（RIoT 的当前站没变）：仍是 H 的占用，第二条仍没被接走——下达离站订单不释放。
$afterDepartureOrder = Get-Held $staging
$acceptedWhileOccupied = Get-AcceptedAt $stagingSecond
$assertions.Add('L2-FSO-06', 'H 占着派工待送站的整段时间里（在途预占、到站占用、下达离站订单之后），同站的第二条需求一直没被接走',
    ($null -eq $acceptedWhileReserved -and $null -eq $acceptedWhileOccupied -and $null -ne $afterDepartureOrder -and
        [string]$afterDepartureOrder.VehicleKey -eq $keyH -and [string]$afterDepartureOrder.State -eq 'OCCUPIED'),
    "not accepted; $staging held by $keyH OCCUPIED after the departure order",
    "accepted while reserved: $acceptedWhileReserved; while occupied: $acceptedWhileOccupied; held $(${afterDepartureOrder}?.VehicleKey) $(${afterDepartureOrder}?.State)")

Move-Vehicle $keyH (Wait-Confirmed $stagingFirst 'TO_GATE' 'H') $machineN13
# 第二个事实另等：H 到了机台（离点证据），第二条才被接走。
$secondAcceptedAt = Wait-L2ConditionOrLast -Description 'the second staging demand was accepted after H left the staging station' `
    -Journal $journal -Criterion 'second-accepted' -TimeoutSeconds 60 -Probe { Get-AcceptedAt $stagingSecond } `
    -Until { param($v) $null -ne $v }
$hRecord = Get-Record $staging ([string]$runtimeH.JourneyId)
$assertions.Add('L2-FSO-07', 'H 到了机台、凭离点证据释放派工待送站之后，第二条需求才被接走（受理时刻不早于释放时刻）',
    ($null -ne $secondAcceptedAt -and $null -ne $hRecord -and [string]$hRecord.ReleaseReason -eq 'DEPARTED_STATION' -and
        (Time $secondAcceptedAt) -ge (Time $hRecord.ReleasedAt)),
    "accepted at or after H's DEPARTED_STATION release",
    "accepted $secondAcceptedAt; H released $(${hRecord}?.ReleasedAt) ($(${hRecord}?.ReleaseReason))")

$journal.Note('Scenario finished.')
