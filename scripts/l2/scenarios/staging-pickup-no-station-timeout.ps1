#Requires -Version 7

<#
待送点无人扫码不超时（control-server#709，program#184 R3）。

待送点规则打开、离站期限十秒。放一条 STAGING_TO_WIRE 需求，合成对端始终不答录入请求，车开到派工待送站 305 停住：

  - 到站发给车的清单，stationDepartureDeadlineAt 为空（待送点没有倒计时）；
  - 等满离站期限的三倍以上，本站不结束：旅程仍在 AwaitingSublot，需求仍 Accepted、没有放回（没有 RELEASED_AT_STATION_DEADLINE
    的归属行）、不抑制、不挂人工搬运告警；车上的装货阶段快照里 cargoHoldingDeadlineAt 也为空。

机台取货在同样的事实下照旧超时、放回、到上限告警：那一半由 station-timeout-redispatch-to-alarm 在同样打开待送点规则的情况下证明。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$connection = $Context.Connection

$stagingStationRiotId = 305
$stationDeadlineSeconds = 10

$demandGuid = [guid]::NewGuid()
$demandWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')

function Get-Count([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    return [int]$rows[0].Total
}

function Get-Runtime {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, Stage, BlockReasonCode, PickupUpperId, PickupStationRiotId FROM JourneyRuntimes WHERE DemandId = '$demandId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-DemandState {
    $demand = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
    return [pscustomobject]@{
        Status       = $(if ($demand.Count -eq 1) { [string]$demand[0].Status } else { '(no demand row)' })
        Journeys     = Get-Count "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE DemandId = '$demandId'"
        Released     = Get-Count ("SELECT COUNT(*) AS Total FROM JourneyDemands WHERE DemandId = '$demandId' " +
                                  "AND RemovalReason = 'RELEASED_AT_STATION_DEADLINE'")
        Suppressions = Get-Count "SELECT COUNT(*) AS Total FROM TransportDemandSuppressions WHERE DemandId = '$demandId'"
        Alarms       = Get-Count "SELECT COUNT(*) AS Total FROM StructuralDispatchBlocks WHERE DemandId = '$demandId'"
    }
}

function Format-DemandState($state) {
    return "$($state.Status) / 旅程 $($state.Journeys) 条 / 放回 $($state.Released) 次 / 抑制 $($state.Suppressions) / 告警 $($state.Alarms)"
}

# 本服务端排给车的某一种快照的载荷，按入队先后。
function Get-Payloads([string]$MessageType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = '$MessageType' ORDER BY CreatedAt, MessageId"
    return @($rows | ForEach-Object { ([string]$_.PayloadJson | ConvertFrom-Json -Depth 20).payload })
}

$journal.Note('Synthetic peer stops answering sublot entry requests: nobody comes to scan.')
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Silent' })
$journal.Note("Publishing STAGING_TO_WIRE demand $demandWire.")
$null = $mes.Command('Put', "demands/$demandWire", @{
    sublot      = "L2-SNT-$($Context.RunId)"
    area        = 'N1-3'
    eqp         = 'EQP-L2-SNT-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
    workType    = 'STAGING_TO_WIRE'
})

$runtime = Wait-L2Condition -Description 'the STAGING_TO_WIRE demand was dispatched to the staging station' `
    -Journal $journal -Criterion 'dispatched' -TimeoutSeconds 90 `
    -Probe { Get-Runtime } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$assertions.Add(
    'L2-SNT-01', '需求派向派工待送站 305',
    ([int]$runtime.PickupStationRiotId -eq $stagingStationRiotId),
    "pickup $stagingStationRiotId", "pickup $($runtime.PickupStationRiotId)")

$upperId = [string]$runtime.PickupUpperId
$intent = Wait-L2Condition -Description "the TO_PICKUP intent ($upperId) was confirmed" `
    -Journal $journal -Criterion 'pickup-intent' -TimeoutSeconds 90 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE UpperId = '$upperId'"
        if ($rows.Count -eq 0) { $null } else { $rows[0] }
    } `
    -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
$journal.Note('The vehicle drives to the staging station and comes to rest.')
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
    processingOrder = $true; orderTaskId = $intent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $stagingStationRiotId; processingOrder = $false; clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })

$null = Wait-L2Condition -Description 'the vehicle waits for a sublot at the staging station' `
    -Journal $journal -Criterion 'waiting' -TimeoutSeconds 120 `
    -Probe { Get-Runtime } -Until { param($v) [string]$v.Stage -in 'AwaitingSublot', 'Completed' }
$arrivedAt = [DateTimeOffset]::UtcNow

$worklists = @(Get-Payloads 'CurrentStopWorklistSnapshot' | Where-Object { [string]$_.stationId -eq '派工待送取货' })
$deadlines = @($worklists | Where-Object { $null -ne $_.stationDepartureDeadlineAt } | ForEach-Object { [string]$_.stationDepartureDeadlineAt })
$assertions.Add(
    'L2-SNT-02', '待送点的清单不带离站期限（stationDepartureDeadlineAt 为空）',
    ($worklists.Count -ge 1 -and $deadlines.Count -eq 0),
    '至少 1 版清单，期限全为空', "清单 $($worklists.Count) 版，带期限的 [$($deadlines -join ', ')]")

# 等满离站期限的三倍以上。期限若还在待送点上生效，第一个期限到时本站就被结束、需求放回、旅程收尾。
$journal.Note("Nobody scans. Waiting more than three station deadlines ($stationDeadlineSeconds s each).")
while (([DateTimeOffset]::UtcNow - $arrivedAt).TotalSeconds -lt 3 * $stationDeadlineSeconds + 5) {
    $null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal
    $now = Get-Runtime
    if ([string]$now.Stage -ne 'AwaitingSublot') { break }
}

$after = Get-Runtime
$state = Get-DemandState
$journal.Observe('after-three-deadlines', "$($after.Stage) / $(Format-DemandState $state)", @{ journey = $after; state = $state })
$assertions.Add(
    'L2-SNT-03', '三个离站期限过去：本站不结束、需求不放回不终结、不抑制、不告警',
    ([string]$after.Stage -eq 'AwaitingSublot' -and $state.Status -eq 'Accepted' -and $state.Journeys -eq 1 -and
        $state.Released -eq 0 -and $state.Suppressions -eq 0 -and $state.Alarms -eq 0),
    'AwaitingSublot / Accepted / 旅程 1 条 / 放回 0 次 / 抑制 0 / 告警 0',
    "$($after.Stage) / $(Format-DemandState $state)")

$holdingDeadlines = @(Get-Payloads 'VehicleBusinessStateSnapshot' |
    Where-Object { $_.PSObject.Properties.Name -contains 'loadingPhase' -and $null -ne $_.loadingPhase } |
    Where-Object { $null -ne $_.loadingPhase.cargoHoldingDeadlineAt } |
    ForEach-Object { [string]$_.loadingPhase.cargoHoldingDeadlineAt })
$assertions.Add(
    'L2-SNT-04', '车辆业务状态快照里没有持货期限（cargoHoldingDeadlineAt 为空）',
    ($holdingDeadlines.Count -eq 0), '带持货期限的 0 张', "带持货期限的 [$($holdingDeadlines -join ', ')]")

$journal.Note('Scenario finished.')
