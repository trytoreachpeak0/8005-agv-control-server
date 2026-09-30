#Requires -Version 7

<#
空闲返回在等待点上的一行两状态（批次8-19，control-server#390；REQ-0293～0295）：车空闲、没有需求 → 承诺、预占、建单、
到点转占用、用途释放；再给一条需求把车派走 → 离点证据满足之后独占才释放，在那之前一直是占用。单车。

**装置**：车停在关卡 210 起步，没有需求。等待点 214 在路网节点 6 上，从关卡一条边就到。

**顺序**：编排器进场景前一刻激活充电策略（空闲返回判定的最后一道前提），车一上来就承诺空闲返回，所以场景先看空闲返回、
再发需求。第一次跑时先发需求，空闲返回在需求被受理之前就承诺了，需求等车、场景等搬运，超时。

**第一个事实（到点转占用）**：空闲返回承诺、物化、过出发前安全门、建单（开往 214 的单段移动，意图 Purpose 为
TO_WAITING_POINT）；214 是这一趟的在途预占。RIoT 报车到了 214、单成功：214 转为这一趟的在点占用，IDLE_RETURN 用途占有释放
（释放原因 IDLE_RETURN_CONVERGED_AT_WAITING_POINT），空闲返回旅程收尾。

**第二个事实（离点才释放）**：来一条需求，派给这辆车（它已回到可选择）。单已下达、车还在 214：214 仍是占用——用
Wait-L2ConditionOrLast 另等十秒看它会不会被放掉，不读一次就断言（scripts/l2/README.md 第 14 条）。然后 RIoT 报车到了机台 12：
再另等，214 的独占以离点证据（DEPARTED_STATION）释放。

**第三个事实（持公共站点独占的车出发即离点）**：这一趟卸在关卡，卸完没有需求，车再次承诺空闲返回；它开离关卡去 214 之后，
关卡的公共站点独占凭离点证据释放。

**红证据**（缺陷版本）：离点清扫把「这辆车已有新订单」当作离点证据（下达离点订单即释放），L2-WPR-04 变红。
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
$machine = 12
$waitingPoint = 214
$vehicleKey = $Context.VehicleKey

function Publish-Demand([string]$Suffix) {
    $guid = [guid]::NewGuid()
    $journal.Note("Publishing demand $Suffix $($guid.ToString('N')).")
    $null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot = "L2-WPR-$Suffix-$($Context.RunId)"; area = 'N1-3'; eqp = 'EQP-L2-WPR-01'
        package = 'L2-PACKAGE'; maxBoxCount = 4
    })
    return $guid.ToString('D')
}

function Get-Runtime([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, Stage, BlockReasonCode FROM JourneyRuntimes WHERE DemandId = '$DemandId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-IdleReturn {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, Stage, BlockReasonCode, PickupUpperId FROM JourneyRuntimes " +
        "WHERE JourneyId LIKE 'idle-return:%' ORDER BY JourneyId DESC")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$Where) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT UpperId, OrderId, Status, DemandId, Purpose FROM OrderIntents WHERE $Where"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Held([int]$Station) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT VehicleKey, JourneyId, State, StationKind FROM StationExclusivities " +
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

function Get-Claim {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT Purpose, JourneyId FROM VehiclePurposeClaims WHERE VehicleKey = '$vehicleKey'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Held-Text([object]$Held) {
    if ($null -eq $Held) { return '(none)' }
    return "$($Held.State) $($Held.StationKind) $($Held.VehicleKey) $($Held.JourneyId)"
}

# RIoT 把单置为执行中并绑车，车动起来，然后停在目的站上，单置为完成（同 fixed-station-single-occupancy）。
function Move-Vehicle([object]$Intent, [int]$Station) {
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 3; executeVehicleKey = $vehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $vehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $Intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Station; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 5 })
}

function Wait-Confirmed([string]$Where, [string]$Label) {
    return Wait-L2Condition -Description "the $Label intent was confirmed" -Journal $journal -Criterion "intent-$Label" `
        -TimeoutSeconds 120 -Probe { Get-Intent $Where } -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
}

function Wait-Stage([string]$DemandId, [string]$Stage, [string]$Label) {
    return Wait-L2Condition -Description "$Label reached $Stage" -Journal $journal -Criterion "stage-$Label" `
        -TimeoutSeconds 180 -Probe { [string](Get-Runtime $DemandId).Stage } -Until { param($v) $v -eq $Stage }
}

function Time([object]$Value) { [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture) }

# --- 0. 路网拉起来 ----------------------------------------------------------------------------------------

$null = Wait-L2Condition -Description 'the route graph engine finished a refresh cycle' `
    -Journal $journal -Criterion 'route-graph-ready' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql ("SELECT DesignEdgeCount, RuntimeRefreshedAt, StaleReason FROM RouteGraphSnapshots " +
                "WHERE MapId = $($Context.MapId)")
        if ($rows.Count -eq 0) { return $false }
        return [int]$rows[0].DesignEdgeCount -gt 0 -and
            $null -ne $rows[0].RuntimeRefreshedAt -and [string]$rows[0].RuntimeRefreshedAt -ne '' -and
            ($null -eq $rows[0].StaleReason -or [string]$rows[0].StaleReason -eq '')
    } `
    -Until { param($v) $v }

# --- 1. 承诺、预占、建单：车停在关卡上、没有需求 ------------------------------------------------------------------------
#
# 编排器进场景前一刻激活充电策略，那是空闲返回判定的最后一道前提，所以车一上来就会承诺空闲返回——第一次跑这条场景时
# 先发了需求，空闲返回在需求发布后 0.2 秒、需求被受理之前就承诺了。这是产品该有的行为，场景就从它开始：先看空闲返回，再发需求。

$idleIntent = Wait-Confirmed "Purpose = 'TO_WAITING_POINT'" 'idle-return'
$idle = Get-IdleReturn
$reserved = Get-Held $waitingPoint
$claim = Get-Claim
$journal.Observe('idle-return-sent', [string]$idle.JourneyId, @{ intent = $idleIntent; reserved = $reserved; claim = $claim })
$assertions.Add(
    'L2-WPR-01',
    '车空闲、没有需求：空闲返回物化成一趟没有需求的旅程，建了开往 214 的单段移动（意图没有需求）；214 是这一趟的在途预占，IDLE_RETURN 用途占有是同一趟',
    ($null -ne $idle -and [string]$idleIntent.UpperId -eq [string]$idle.PickupUpperId -and
        ($null -eq $idleIntent.DemandId -or [string]$idleIntent.DemandId -eq '') -and
        $null -ne $reserved -and [string]$reserved.State -eq 'RESERVED' -and [string]$reserved.StationKind -eq 'WAITING_POINT' -and
        [string]$reserved.JourneyId -eq [string]$idle.JourneyId -and
        $null -ne $claim -and [string]$claim.Purpose -eq 'IDLE_RETURN' -and [string]$claim.JourneyId -eq [string]$idle.JourneyId),
    "214 RESERVED WAITING_POINT $($idle.JourneyId), claim IDLE_RETURN",
    "$(Held-Text $reserved); claim $(${claim}?.Purpose) $(${claim}?.JourneyId); intent demand '$($idleIntent.DemandId)'")

# --- 2. 到点：预占转占用、用途释放 --------------------------------------------------------------------------------

Move-Vehicle $idleIntent $waitingPoint
$closed = Wait-L2Condition -Description 'the idle return converged at the waiting point' -Journal $journal `
    -Criterion 'idle-return-converged' -TimeoutSeconds 60 -Probe { Get-IdleReturn } `
    -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'Completed' }
$occupied = Get-Held $waitingPoint
$claimAfter = Get-Claim
$claimRecord = Invoke-L2Query -Connection $connection -Sql (
    "SELECT ReleaseReason FROM VehiclePurposeClaimRecords WHERE JourneyId = '$($idle.JourneyId)'")
$assertions.Add(
    'L2-WPR-02',
    '到点证据全满足：214 转为这一趟的在点占用，IDLE_RETURN 用途占有释放（原因 IDLE_RETURN_CONVERGED_AT_WAITING_POINT），空闲返回旅程无码收尾',
    ([string]::IsNullOrEmpty([string]$closed.BlockReasonCode) -and
        $null -ne $occupied -and [string]$occupied.State -eq 'OCCUPIED' -and [string]$occupied.JourneyId -eq [string]$idle.JourneyId -and
        $null -eq $claimAfter -and $claimRecord.Count -eq 1 -and [string]$claimRecord[0].ReleaseReason -eq 'IDLE_RETURN_CONVERGED_AT_WAITING_POINT'),
    "214 OCCUPIED $($idle.JourneyId), no claim, released IDLE_RETURN_CONVERGED_AT_WAITING_POINT",
    "$(Held-Text $occupied); claim $(${claimAfter}?.Purpose); record $(($claimRecord | ForEach-Object { $_.ReleaseReason }) -join ',')")

# --- 3. 派走：单已下达、车还在 214，占用不放 ------------------------------------------------------------------------

$demand = Publish-Demand 'FIRST'
$pickup = Wait-Confirmed "DemandId = '$demand' AND Purpose = 'TO_PICKUP'" 'pickup'
$assertions.Add(
    'L2-WPR-03',
    '需求派给了停在 214 的这辆车（收敛之后它回到可选择），开往机台的单已确认',
    ($null -ne (Get-Runtime $demand) -and $null -ne $pickup),
    'accepted and confirmed', "$([string](Get-Runtime $demand).Stage) $([string]$pickup.Status)")

# 另等：单下达之后、车还没离开 214 的这段时间里，214 会不会被放掉（它不能）。
$whileStill = Wait-L2ConditionOrLast -Description 'the waiting point was released while the vehicle still stood on it (it must not)' `
    -Journal $journal -Criterion 'occupied-after-departing-order' -TimeoutSeconds 10 `
    -Probe { Get-Held $waitingPoint } `
    -Until { param($v) $null -eq $v -or [string]$v.JourneyId -ne [string]$idle.JourneyId }
$assertions.Add(
    'L2-WPR-04',
    '离点订单已下达、车还在 214：214 仍是那一趟的在点占用（下达离点订单时不释放，REQ-0293）',
    ($null -ne $whileStill -and [string]$whileStill.State -eq 'OCCUPIED' -and [string]$whileStill.JourneyId -eq [string]$idle.JourneyId),
    "214 OCCUPIED $($idle.JourneyId)",
    (Held-Text $whileStill))

# --- 4. 离点：车到了机台 12，214 凭离点证据释放 ---------------------------------------------------------------------

Move-Vehicle $pickup $machine
$afterLeaving = Wait-L2ConditionOrLast -Description 'the waiting point was released on departure evidence' `
    -Journal $journal -Criterion 'released-on-departure' -TimeoutSeconds 30 `
    -Probe { Get-Held $waitingPoint } -Until { param($v) $null -eq $v }
$waitingRecord = Get-Record $waitingPoint ([string]$idle.JourneyId)
$assertions.Add(
    'L2-WPR-05',
    '车到了机台（RIoT 报当前站是另一个站）：214 的独占以离点证据释放，预占、占用、释放三个时刻依次记在同一段经过上',
    ($null -eq $afterLeaving -and $null -ne $waitingRecord -and [string]$waitingRecord.ReleaseReason -eq 'DEPARTED_STATION' -and
        (Time $waitingRecord.ReservedAt) -le (Time $waitingRecord.OccupiedAt) -and
        (Time $waitingRecord.OccupiedAt) -le (Time $waitingRecord.ReleasedAt)),
    'released DEPARTED_STATION, reserved <= occupied <= released',
    "held: $(Held-Text $afterLeaving); record $(${waitingRecord}?.ReservedAt) / $(${waitingRecord}?.OccupiedAt) / $(${waitingRecord}?.ReleasedAt) ($(${waitingRecord}?.ReleaseReason))")

# --- 5. 卸完再回：持关卡独占的车承诺空闲返回，出发即离点 ------------------------------------------------------------------

$null = Wait-Stage $demand 'AwaitingGateArrival' 'demand'
Move-Vehicle (Wait-Confirmed "DemandId = '$demand' AND Purpose = 'TO_GATE'" 'gate') $gate
$null = Wait-Stage $demand 'Completed' 'demand'
$transport = Get-Runtime $demand
$secondIntent = Wait-Confirmed "Purpose = 'TO_WAITING_POINT' AND UpperId <> '$($idleIntent.UpperId)'" 'second-idle-return'
Move-Vehicle $secondIntent $waitingPoint
$gateAfter = Wait-L2ConditionOrLast -Description 'the gate was released on departure evidence' -Journal $journal `
    -Criterion 'gate-released-on-departure' -TimeoutSeconds 30 -Probe { Get-Held $gate } -Until { param($v) $null -eq $v }
$gateRecord = Get-Record $gate ([string]$transport.JourneyId)
$second = Get-IdleReturn
$assertions.Add(
    'L2-WPR-06',
    '卸完没有需求：停在关卡上（持公共站点独占）的车再次承诺空闲返回，开离关卡去 214 后关卡独占凭离点证据释放；这是另一趟空闲返回',
    ($null -eq $gateAfter -and $null -ne $gateRecord -and [string]$gateRecord.ReleaseReason -eq 'DEPARTED_STATION' -and
        $null -ne $second -and [string]$second.JourneyId -ne [string]$idle.JourneyId),
    'gate released DEPARTED_STATION; a second idle return',
    "gate held: $(Held-Text $gateAfter); record $(${gateRecord}?.ReleaseReason); second $(${second}?.JourneyId) $(${second}?.Stage)")

$journal.Note('Scenario finished.')
