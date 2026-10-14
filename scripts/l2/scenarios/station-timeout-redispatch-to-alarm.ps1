#Requires -Version 7

<#
站点超时放回待派，放回到上限转人工搬运告警（control-server#703）。

服务端用默认上限（StationTimeoutRedispatchLimit = 2），合成对端始终不答录入请求，同一条需求走满三趟：

  第 1、2 趟：车到取货站，离站期限到期，需求放回待派——旅程以 RELEASED_AT_STATION_DEADLINE 收尾，需求仍 Accepted、
             不写业务键抑制、不挂告警，下一轮以新的一代（新的 RIoT 取货单）重新派车。
  第 3 趟：   已放回 2 次，这一次终结：CANCELLED_BY_STATION_TIMEOUT、需求 Cancelled、按业务键抑制，并在结构性派车阻断里挂
             STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED。之后目录里它还在，也不再派车，告警不被派车轮清掉；
             它离开 MES 目录之后告警才清。

在这之前第 1 趟就终结并永久抑制，没有任何告警，MES 里那条需求一直挂着、货留在机台上没人知道。

control-server#709 起这条只断言机台取货，并在待送点规则打开的情况下跑：规则只对 STAGING_TO_WIRE 在待送点的取货生效，
机台取货的期限、放回与告警一条都不能变。待送点没有期限那一半由 staging-pickup-no-station-timeout 证明。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Change.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$alarmCode = 'STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED'

function Get-Count([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    return [int]$rows[0].Total
}

# 这条需求的第 $Visit 趟（从 1 数）：每派一次一条旅程行，按派车代次排序取第几条。代次的起点是部署的基准代次，不写死。
function Get-Journey([int]$Visit) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, Stage, BlockReasonCode, DispatchGeneration, PickupUpperId FROM JourneyRuntimes " +
        "WHERE DemandId = '$demandId' ORDER BY DispatchGeneration")
    if ($rows.Count -lt $Visit) { return $null }
    return $rows[$Visit - 1]
}

function Get-DemandState {
    $demand = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
    $memberships = Invoke-L2Query -Connection $connection -Sql (
        "SELECT DispatchGeneration, RemovedAt, RemovalReason FROM JourneyDemands WHERE DemandId = '$demandId' ORDER BY DispatchGeneration")
    $suppressions = Get-Count "SELECT COUNT(*) AS Total FROM TransportDemandSuppressions WHERE DemandId = '$demandId'"
    $alarms = Invoke-L2Query -Connection $connection -Sql (
        "SELECT ReasonCode, ClearedAt FROM StructuralDispatchBlocks WHERE DemandId = '$demandId'")
    return [pscustomobject]@{
        Status       = $(if ($demand.Count -eq 1) { [string]$demand[0].Status } else { '(no demand row)' })
        Released     = @($memberships | Where-Object { [string]$_.RemovalReason -eq 'RELEASED_AT_STATION_DEADLINE' }).Count
        Memberships  = $memberships.Count
        Suppressions = $suppressions
        Alarms       = $alarms
    }
}

function Format-DemandState($state) {
    $alarmText = (@($state.Alarms) | ForEach-Object { "$($_.ReasonCode)$(if ($null -ne $_.ClearedAt) { '/cleared' })" }) -join ','
    return "$($state.Status) / 放回 $($state.Released) 次 / 归属 $($state.Memberships) 行 / 抑制 $($state.Suppressions) / 告警 [$alarmText]"
}

# 车跑第 $Visit 趟的取货单：接单、行驶、停在取货点。后两趟车本来就停在那里，但那是一张新的运单，到站判定要重跑一遍。
function Invoke-PickupLeg([int]$Visit) {
    $upperId = [string](Get-Journey $Visit).PickupUpperId
    $intent = Wait-L2Condition -Description "the TO_PICKUP intent of visit $Visit ($upperId) was confirmed" `
        -Journal $journal -Criterion "pickup-intent-$Visit" -TimeoutSeconds 90 `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection -Sql (
                "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE UpperId = '$upperId'")
            if ($rows.Count -eq 0) { $null } else { $rows[0] }
        } `
        -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
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
        currentPosition  = $Context.PickupStationRiotId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })
}

# 第 $Visit 趟：派出、到站、没人扫码、期限到，返回收尾的那一行。
function Invoke-UnscannedVisit([int]$Visit) {
    $null = Wait-L2Condition -Description "visit $Visit of the demand was dispatched" `
        -Journal $journal -Criterion "dispatched-$Visit" -TimeoutSeconds 120 `
        -Probe { Get-Journey $Visit } -Until { param($v) $null -ne $v }
    Invoke-PickupLeg $Visit
    $null = Wait-L2Condition -Description "visit $Visit is waiting for a sublot" `
        -Journal $journal -Criterion "waiting-$Visit" -TimeoutSeconds 120 `
        -Probe { Get-Journey $Visit } -Until { param($v) [string]$v.Stage -in 'AwaitingSublot', 'Completed' }
    $journal.Note("Visit $Visit is at the pickup; nobody scans. Waiting for the station deadline.")
    return Wait-L2Condition -Description "the station deadline ended visit $Visit" `
        -Journal $journal -Criterion "ended-$Visit" -TimeoutSeconds 120 `
        -Probe { Get-Journey $Visit } -Until { param($v) [string]$v.Stage -eq 'Completed' }
}

$journal.Note('Synthetic peer stops answering sublot entry requests: nobody comes to scan.')
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Silent' })
$journal.Note("Publishing demand $demandWire.")
$null = $mes.Command('Put', "demands/$demandWire", @{
    sublot      = "L2-STR-$($Context.RunId)"
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

# --- 第 1、2 趟：放回待派 ---------------------------------------------------------------------------

foreach ($visit in 1, 2) {
    $ended = Invoke-UnscannedVisit $visit
    $state = Get-DemandState
    $journal.Observe("after-visit-$visit", (Format-DemandState $state), @{ journey = $ended; state = $state })
    $assertions.Add(
        "L2-STR-0$visit", "第 $visit 趟期限到：旅程以 RELEASED_AT_STATION_DEADLINE 收尾，需求仍 Accepted、放回 $visit 次、不抑制、不告警",
        ([string]$ended.BlockReasonCode -eq 'RELEASED_AT_STATION_DEADLINE' -and $state.Status -eq 'Accepted' -and
            $state.Released -eq $visit -and $state.Suppressions -eq 0 -and @($state.Alarms).Count -eq 0),
        "RELEASED_AT_STATION_DEADLINE / Accepted / 放回 $visit 次 / 抑制 0 / 告警 []",
        "$($ended.BlockReasonCode) / $(Format-DemandState $state)")
}

# --- 第 3 趟：到上限，终结并告警 ---------------------------------------------------------------------

$third = Invoke-UnscannedVisit 3
$state = Get-DemandState
$journal.Observe('after-visit-3', (Format-DemandState $state), @{ journey = $third; state = $state })
$openAlarms = @($state.Alarms | Where-Object { [string]$_.ReasonCode -eq $alarmCode -and $null -eq $_.ClearedAt })
$assertions.Add(
    'L2-STR-03', '第 3 趟期限到（已放回 2 次）：CANCELLED_BY_STATION_TIMEOUT、需求 Cancelled、抑制 1 条、挂人工搬运告警',
    ([string]$third.BlockReasonCode -eq 'CANCELLED_BY_STATION_TIMEOUT' -and $state.Status -eq 'Cancelled' -and
        $state.Released -eq 2 -and $state.Suppressions -eq 1 -and $openAlarms.Count -eq 1),
    "CANCELLED_BY_STATION_TIMEOUT / Cancelled / 放回 2 次 / 抑制 1 / 告警 [$alarmCode]",
    "$($third.BlockReasonCode) / $(Format-DemandState $state)")

# 目录里它还在：运行时与派车轮再转几轮，不再派车，告警也不被派车轮清掉。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$journeys = Get-Count "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE DemandId = '$demandId'"
$pickupOrders = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'"
$catalogStillLists = @($mes.Snapshot().body.demands | Where-Object { [string]$_.demandId -eq $demandWire }).Count
$state = Get-DemandState
$openAlarms = @($state.Alarms | Where-Object { [string]$_.ReasonCode -eq $alarmCode -and $null -eq $_.ClearedAt })
$assertions.Add(
    'L2-STR-04', '目录仍列出这一单：不再派车（3 条旅程、3 张取货单），告警仍挂着',
    ($catalogStillLists -ge 1 -and $journeys -eq 3 -and $pickupOrders -eq 3 -and $openAlarms.Count -eq 1),
    '目录列出 / 3 / 3 / 告警未清', "目录列出 $catalogStillLists / $journeys / $pickupOrders / $(Format-DemandState $state)")

# --- MES 关单：告警清 -------------------------------------------------------------------------------

$journal.Note("Removing $demandWire from the catalog: MES closed it.")
$null = $mes.Command('Delete', "demands/$demandWire", @{})
$cleared = Wait-L2ConditionOrLast -Description 'the manual-carry alarm cleared once MES closed the demand' `
    -Journal $journal -Criterion 'alarm-cleared' -TimeoutSeconds 60 `
    -Probe { Get-DemandState } `
    -Until { param($v) @($v.Alarms | Where-Object { [string]$_.ReasonCode -eq $alarmCode -and $null -ne $_.ClearedAt }).Count -eq 1 }
$assertions.Add(
    'L2-STR-05', '需求离开 MES 目录之后，人工搬运告警被清',
    (@($cleared.Alarms | Where-Object { [string]$_.ReasonCode -eq $alarmCode -and $null -ne $_.ClearedAt }).Count -eq 1),
    "告警 [$alarmCode/cleared]", (Format-DemandState $cleared))

$journal.Note('Scenario finished.')
