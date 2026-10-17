#Requires -Version 7

<#
站点期限第一次到点即终结、抑制并告警人工搬运，不放回（REQ-0365，CP-0016，control-server#751）。

服务端用出厂上限（StationTimeoutRedispatchLimit = 0，setup 不设），合成对端始终不答录入请求：

  第 1 趟：车到取货站，离站期限到期，需求终结：CANCELLED_BY_STATION_TIMEOUT、需求 Cancelled、按业务键抑制，
          并在结构性派车阻断里挂 STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED；不放回（没有 RELEASED_AT_STATION_DEADLINE 的归属行）。
          看板站点超时抑制清单（/api/dashboard/station-timeout-suppressions）列出它的批号与取货站点。
  之后：   目录里它还在，也不再派车，告警不被派车轮清掉；它离开 MES 目录之后告警清、清单里不再有它。

场景名还叫 redispatch-to-alarm，是 control-server#703 时「放回两次再告警」留下的；改名要动 l2.yml 与既有证据目录，没改。
control-server#703 那时要防的「站点超时静默丢单、无告警」在这里照样被钉住：L2-STR-01 要求告警与终结同时出现。

control-server#709 起这条只断言机台取货，并在待送点规则打开的情况下跑：规则只对 STAGING_TO_WIRE 在待送点的取货生效，
机台取货的期限、终结与告警一条都不能变。待送点没有期限那一半由 staging-pickup-no-station-timeout 证明。
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
$sublot = "L2-STR-$($Context.RunId)"
$suppressionsEndpoint = "http://127.0.0.1:$($Context.HealthPort)/api/dashboard/station-timeout-suppressions"

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

# 看板站点超时抑制清单里这条需求的那一行；不在就是 $null。-DateKind String：时刻原样读回，不经本地时区换算。
function Get-SuppressionEntry {
    $response = Invoke-WebRequest -Uri $suppressionsEndpoint -NoProxy -TimeoutSec 10
    $fact = $response.Content | ConvertFrom-Json -DateKind String
    return @($fact.suppressions | Where-Object { [string]$_.demandId -eq $demandId }) | Select-Object -First 1
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
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

# --- 第 1 趟：期限到，终结并告警，不放回 -------------------------------------------------------------

$first = Invoke-UnscannedVisit 1
$state = Get-DemandState
$journal.Observe('after-visit-1', (Format-DemandState $state), @{ journey = $first; state = $state })
$openAlarms = @($state.Alarms | Where-Object { [string]$_.ReasonCode -eq $alarmCode -and $null -eq $_.ClearedAt })
$assertions.Add(
    'L2-STR-01', '第 1 趟期限到（出厂上限 0）：CANCELLED_BY_STATION_TIMEOUT、需求 Cancelled、不放回、抑制 1 条、挂人工搬运告警',
    ([string]$first.BlockReasonCode -eq 'CANCELLED_BY_STATION_TIMEOUT' -and $state.Status -eq 'Cancelled' -and
        $state.Released -eq 0 -and $state.Suppressions -eq 1 -and $openAlarms.Count -eq 1),
    "CANCELLED_BY_STATION_TIMEOUT / Cancelled / 放回 0 次 / 抑制 1 / 告警 [$alarmCode]",
    "$($first.BlockReasonCode) / $(Format-DemandState $state)")

$pickupStation = [string](Invoke-L2Query -Connection $connection -Sql (
        "SELECT PickupStationId FROM JourneyRuntimes WHERE JourneyId = '$($first.JourneyId)'"))[0].PickupStationId
$entry = Wait-L2ConditionOrLast -Description 'the station timeout suppression list shows the demand' `
    -Journal $journal -Criterion 'suppression-listed' -TimeoutSeconds 30 `
    -Probe { Get-SuppressionEntry } -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-STR-02', '看板站点超时抑制清单列出这条需求：批号、取货站点、终结时刻',
    ($null -ne $entry -and [string]$entry.sublot -eq $sublot -and [string]$entry.stationId -eq $pickupStation -and
        -not [string]::IsNullOrEmpty([string]$entry.endedAt)),
    "$sublot / $pickupStation / 有终结时刻",
    $(if ($null -eq $entry) { '(not listed)' } else { "$($entry.sublot) / $($entry.stationId) / $($entry.endedAt)" }))

# 目录里它还在：运行时与派车轮再转几轮，不再派车，告警也不被派车轮清掉。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$journeys = Get-Count "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE DemandId = '$demandId'"
$pickupOrders = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'"
$catalogStillLists = @($mes.Snapshot().body.demands | Where-Object { [string]$_.demandId -eq $demandWire }).Count
$state = Get-DemandState
$openAlarms = @($state.Alarms | Where-Object { [string]$_.ReasonCode -eq $alarmCode -and $null -eq $_.ClearedAt })
$assertions.Add(
    'L2-STR-04', '目录仍列出这一单：不再派车（1 条旅程、1 张取货单），告警仍挂着',
    ($catalogStillLists -ge 1 -and $journeys -eq 1 -and $pickupOrders -eq 1 -and $openAlarms.Count -eq 1),
    '目录列出 / 1 / 1 / 告警未清', "目录列出 $catalogStillLists / $journeys / $pickupOrders / $(Format-DemandState $state)")

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
$stillListed = Wait-L2ConditionOrLast -Description 'the suppression list dropped the demand once MES closed it' `
    -Journal $journal -Criterion 'suppression-unlisted' -TimeoutSeconds 30 `
    -Probe { $listed = Get-SuppressionEntry; if ($null -eq $listed) { '(not listed)' } else { $listed } } `
    -Until { param($v) $v -is [string] -and $v -eq '(not listed)' }
$assertions.Add(
    'L2-STR-06', '需求离开 MES 目录之后，站点超时抑制清单不再列出它',
    ($stillListed -is [string] -and $stillListed -eq '(not listed)'), '(not listed)', [string]$stillListed)

$journal.Note('Scenario finished.')
