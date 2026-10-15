#Requires -Version 7

<#
待送点一篮没装，客户在车载屏上点「出发」（control-server#713，program#184 R2；CP-0012，协议 4.0.0 候选）。

待送点规则打开。一条 STAGING_TO_WIRE 需求，合成对端不答录入请求（没人扫码），车开到派工待送站 305 停住。合成对端按它屏上那张清单发
CurrentStopDepartureRequested：

  1. 服务端答 CurrentStopDepartureResult / ACCEPTED。
  2. 旅程原地收尾（Completed，收尾原因 STAGING_OPERATOR_DEPARTED），收尾清单的 stopEndedReason 是 OPERATOR_DEPARTED；需求放回积压
     （RELEASED_FOR_REDISPATCH，仍 Accepted，不抑制）。
  3. 车不动：出发之后没有离站核验；放回的需求下一轮多半又派给这辆停在待送点的车，那张新单只开往待送站本身。

**红证据**（缺陷版本）：一篮没装也走正常离站，L2-SDN-03 红（有离站核验、旅程不收尾）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$onboard = $Context.Onboard
$connection = $Context.Connection

$stagingStationRiotId = 305

$demandGuid = [guid]::NewGuid()
$demandWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')

function Get-Runtime {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, Stage, BlockReasonCode, PickupUpperId, PickupStationRiotId FROM JourneyRuntimes WHERE DemandId = '$demandId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Count([string]$sql) {
    return [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].Total
}

function Get-DepartureResult([string]$DepartureRequestId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'CurrentStopDepartureResult'"
    foreach ($row in $rows) {
        $payload = ([string]$row.PayloadJson | ConvertFrom-Json -Depth 20).payload
        if ([string]$payload.departureRequestId -eq $DepartureRequestId) { return $payload }
    }
    return $null
}

$journal.Note('Synthetic peer stops answering sublot entry requests: nobody comes to scan.')
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Silent' })
$journal.Note("Publishing STAGING_TO_WIRE demand $demandWire.")
$null = $Context.MesIngest.Command('Put', "demands/$demandWire", @{
    sublot      = "L2-SDN-$($Context.RunId)"
    area        = 'N1-3'
    eqp         = 'EQP-L2-SDN-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
    workType    = 'STAGING_TO_WIRE'
})

$runtime = Wait-L2Condition -Description 'the STAGING_TO_WIRE demand was dispatched to the staging station' `
    -Journal $journal -Criterion 'dispatched' -TimeoutSeconds 90 `
    -Probe { Get-Runtime } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
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
    -Probe { Get-Runtime } -Until { param($v) [string]$v.Stage -eq 'AwaitingSublot' }
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal

$ordersBefore = Get-Count 'SELECT COUNT(*) AS Total FROM OrderIntents'
$departureRequestId = [guid]::NewGuid().ToString('D')
$journal.Note("The operator presses 出发 with nothing loaded ($departureRequestId).")
$sent = $onboard.Command('Put', "current-stop-departures/$departureRequestId", @{ operatorId = 'L2-SDN-OPERATOR' })
$journal.Observe('departure-request', "station $($sent.body.stationId) revision $($sent.body.worklistRevision)", @{ answer = $sent.body })

$result = Wait-L2ConditionOrLast -Description 'the departure was answered' -Journal $journal -Criterion 'answered' -TimeoutSeconds 60 `
    -Probe { Get-DepartureResult $departureRequestId } -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-SDN-01', '出发请求得到 CurrentStopDepartureResult / ACCEPTED',
    ($null -ne $result -and [string]$result.outcome -eq 'ACCEPTED'), 'ACCEPTED',
    $(if ($null -eq $result) { '(no result)' } else { [string]$result.outcome }))

$closed = Wait-L2ConditionOrLast -Description 'the journey closed in place' -Journal $journal -Criterion 'closed' -TimeoutSeconds 60 `
    -Probe { Get-Runtime } -Until { param($v) [string]$v.Stage -eq 'Completed' }
$membership = (Invoke-L2Query -Connection $connection -Sql (
    "SELECT RemovalReason FROM JourneyDemands WHERE DemandId = '$demandId'"))[0]
$status = [string](Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'")[0].Status
$suppressions = Get-Count "SELECT COUNT(*) AS Total FROM TransportDemandSuppressions WHERE DemandId = '$demandId'"
# Invoke-L2Query 把整组行原样交回（return , $rows）：先赋值再逐行取，套 @() 再接管道会把整组当一行（CargoHoldingCommon.ps1 的那条注释）。
$worklistRows = Invoke-L2Query -Connection $connection -Sql (
    "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'CurrentStopWorklistSnapshot'")
$reasons = @(foreach ($row in $worklistRows) { [string](([string]$row.PayloadJson | ConvertFrom-Json -Depth 20).payload.stopEndedReason) })
$assertions.Add(
    'L2-SDN-02', '旅程原地收尾（STAGING_OPERATOR_DEPARTED），清单说 OPERATOR_DEPARTED；需求放回、仍 Accepted、不抑制',
    ([string]$closed.Stage -eq 'Completed' -and [string]$closed.BlockReasonCode -eq 'STAGING_OPERATOR_DEPARTED' -and
        $reasons -contains 'OPERATOR_DEPARTED' -and [string]$membership.RemovalReason -eq 'RELEASED_FOR_REDISPATCH' -and
        $status -eq 'Accepted' -and $suppressions -eq 0),
    'Completed/STAGING_OPERATOR_DEPARTED, OPERATOR_DEPARTED, RELEASED_FOR_REDISPATCH/Accepted, 抑制 0',
    "$($closed.Stage)/$($closed.BlockReasonCode), [$($reasons -join ',')], $($membership.RemovalReason)/$status, 抑制 $suppressions")

$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
# 放回的需求还在 MES 目录里，下一轮派车会把它重新受理——这辆车就停在待送点上，多半又派给它（新旅程、新清单）。那不是「车动了」：
# 判据只看这两样，出发之后没有一张离站核验，新建的订单（若有）都开往待送站本身。
$checks = Get-Count "SELECT COUNT(*) AS Total FROM ProtocolOutbox WHERE MessageType = 'PreDepartureSafetyCheck'"
$orderRows = Invoke-L2Query -Connection $connection -Sql 'SELECT TargetStationId FROM OrderIntents ORDER BY CreatedAt, UpperId'
$allTargets = @(foreach ($row in $orderRows) { [string]$row.TargetStationId })
$newTargets = @($allTargets | Select-Object -Skip $ordersBefore)
$assertions.Add(
    'L2-SDN-03', '车不动：出发之后没有离站核验，新建的订单（若有）只开往待送站本身',
    ($checks -eq 0 -and @($newTargets | Where-Object { $_ -ne "$stagingStationRiotId" -and $_ -ne '派工待送取货' }).Count -eq 0),
    '离站核验 0 张，新订单只开往待送站', "离站核验 $checks 张，新订单开往 [$($newTargets -join ', ')]")

$journal.Note('待送点一篮没装就点出发：旅程原地收尾、需求放回，车留在待送点。')
