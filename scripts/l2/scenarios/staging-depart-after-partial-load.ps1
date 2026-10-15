#Requires -Version 7

<#
待送点装了一部分，客户在车载屏上点「出发」（control-server#713，program#184 R2；CP-0012，协议 4.0.0 候选）。

待送点规则打开。STAGING_TO_WIRE 需求甲（AREA N1-3 → FRONT，1 个花篮）在派工待送站 305 装上；需求丙（AREA C15-13 → REAR，1 个花篮）
随后追加进这趟旅程（同站新开一个停靠），车原地走过去，但没人来扫丙。

  1. 合成对端按它屏上那张清单发 CurrentStopDepartureRequested，服务端答 CurrentStopDepartureResult / ACCEPTED。
  2. 丙放回积压：归属移除、原因 RELEASED_FOR_REDISPATCH，需求仍 Accepted、不抑制、不计站点超时放回。
  3. 装货阶段以 OPERATOR_DEPARTED 关闭，车带着甲走正常离站：发一张开往卸货站（不是待送站）的离站核验。

**红证据**（缺陷版本）：把放回改成取消，L2-SDP-02 红（丙被抑制、终结）；不接出发请求，L2-SDP-01 红（等不到答复）。

**探针不取闭包**，理由见 CargoHoldingCommon.ps1 开头。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
. (Join-Path $PSScriptRoot 'CargoHoldingCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$onboard = $Context.Onboard

$stagingStationRiotId = 305
$stagingStationName = '派工待送取货'

function Publish-StagingDemand([hashtable]$Demand) {
    $journal.Note("Publishing STAGING_TO_WIRE demand $($Demand.Label) $($Demand.Wire) (area $($Demand.Area), $($Demand.Baskets) baskets).")
    $null = $Context.MesIngest.Command('Put', "demands/$($Demand.Wire)", @{
        sublot      = $Demand.Sublot
        area        = $Demand.Area
        eqp         = 'EQP-L2-SDP-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4 * $Demand.Baskets
        workType    = 'STAGING_TO_WIRE'
    })
}

function Get-Membership([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, PickupStopId, Status, RemovedAt, RemovalReason FROM JourneyDemands WHERE DemandId = '$DemandId' " +
        "ORDER BY AddedAt DESC LIMIT 1")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
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

function Get-DepartureTargets {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'PreDepartureSafetyCheck' ORDER BY CreatedAt, MessageId"
    return @(foreach ($row in $rows) { [string]([string]$row.PayloadJson | ConvertFrom-Json -Depth 20).payload.targetStationId })
}

$a = New-L2CargoDemand 'A' 'N1-3' 1 $Context.RunId
$c = New-L2CargoDemand 'C' 'C15-13' 1 $Context.RunId

Initialize-L2CargoRig $Context

# --- 甲装上 -------------------------------------------------------------------------------------------------------------

Publish-StagingDemand $a
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $stagingStationRiotId
$null = Wait-L2Condition -Description 'demand A was loaded' -Journal $journal -Criterion 'a-loaded' -TimeoutSeconds 120 `
    -Probe { Get-Membership $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'LOADED' }

# --- 丙追加进来，没人扫 -------------------------------------------------------------------------------------------------

$journal.Note('Synthetic peer stops answering sublot entry requests: nobody scans demand C.')
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Silent' })
Publish-StagingDemand $c
$joined = Wait-L2Condition -Description 'demand C joined the journey' -Journal $journal -Criterion 'c-joined' -TimeoutSeconds 120 `
    -Probe { Get-Membership $c.Id } -Until { param($v) $null -ne $v -and $null -eq $v.RemovedAt -and [string]$v.JourneyId -eq $journeyId }
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $stagingStationRiotId ([string]$joined.PickupStopId)
$null = Wait-L2Condition -Description "the vehicle waits for C's sublot at its stop" -Journal $journal -Criterion 'c-waiting' `
    -TimeoutSeconds 120 -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) [string]$v.Stage -eq 'AwaitingSublot' }
$null = Wait-L2Iterations -Riot $Context.Riot -Count 2 -Journal $journal

# --- 1. 出发 ----------------------------------------------------------------------------------------------------------

$departureRequestId = [guid]::NewGuid().ToString('D')
$journal.Note("The operator presses 出发 ($departureRequestId).")
$sent = $onboard.Command('Put', "current-stop-departures/$departureRequestId", @{ operatorId = 'L2-SDP-OPERATOR' })
$journal.Observe('departure-request', "station $($sent.body.stationId) revision $($sent.body.worklistRevision)", @{ answer = $sent.body })
$result = Wait-L2ConditionOrLast -Description 'the departure was answered' -Journal $journal -Criterion 'answered' -TimeoutSeconds 60 `
    -Probe { Get-DepartureResult $departureRequestId } -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-SDP-01', '出发请求得到 CurrentStopDepartureResult / ACCEPTED',
    ($null -ne $result -and [string]$result.outcome -eq 'ACCEPTED'), 'ACCEPTED',
    $(if ($null -eq $result) { '(no result)' } else { "$($result.outcome) $($result.problem | ConvertTo-Json -Compress)" }))

# --- 2. 丙放回 --------------------------------------------------------------------------------------------------------

$released = Wait-L2ConditionOrLast -Description 'demand C left the journey' -Journal $journal -Criterion 'c-released' -TimeoutSeconds 60 `
    -Probe { Get-Membership $c.Id } -Until { param($v) $null -ne $v -and $null -ne $v.RemovedAt }
$demandC = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$($c.Id)'"
$suppressions = [int](Invoke-L2Query -Connection $connection -Sql (
    "SELECT COUNT(*) AS Total FROM TransportDemandSuppressions WHERE DemandId = '$($c.Id)'"))[0].Total
$deadlineReleases = [int](Invoke-L2Query -Connection $connection -Sql (
    "SELECT COUNT(*) AS Total FROM JourneyDemands WHERE RemovalReason = 'RELEASED_AT_STATION_DEADLINE'"))[0].Total
$assertions.Add(
    'L2-SDP-02', '丙放回积压：原因 RELEASED_FOR_REDISPATCH、需求仍 Accepted、不抑制、不计站点超时放回',
    ($null -ne $released -and [string]$released.RemovalReason -eq 'RELEASED_FOR_REDISPATCH' -and
        $demandC.Count -eq 1 -and [string]$demandC[0].Status -eq 'Accepted' -and $suppressions -eq 0 -and $deadlineReleases -eq 0),
    'RELEASED_FOR_REDISPATCH / Accepted / 抑制 0 / 超时放回 0',
    "$(if ($released) { $released.RemovalReason } else { '(still on)' }) / $(if ($demandC.Count) { $demandC[0].Status }) / 抑制 $suppressions / 超时放回 $deadlineReleases")

# --- 3. 带着甲离站 ------------------------------------------------------------------------------------------------------

$leaving = Wait-L2ConditionOrLast -Description 'a departure check towards the drop-off was sent' -Journal $journal -Criterion 'leaving' `
    -TimeoutSeconds 60 -Probe { @(Get-DepartureTargets) } `
    -Until { param($v) @($v | Where-Object { $_ -ne $stagingStationName }).Count -ge 1 }
$journeyAfter = Get-L2CargoJourney $connection $a.Id
$assertions.Add(
    'L2-SDP-03', '装货阶段以 OPERATOR_DEPARTED 关闭，甲仍在车上，发出开往卸货站的离站核验',
    ($null -ne $journeyAfter -and [string]$journeyAfter.LoadingPhaseState -eq 'CLOSED' -and
        [string]$journeyAfter.LoadingClosedReason -eq 'OPERATOR_DEPARTED' -and
        @($leaving | Where-Object { $_ -ne $stagingStationName }).Count -ge 1 -and
        [string](Get-Membership $a.Id).Status -eq 'LOADED'),
    'CLOSED/OPERATOR_DEPARTED, A LOADED, check to the drop-off',
    "$(Format-L2CargoJourney $journeyAfter), A $((Get-Membership $a.Id).Status), checks to [$(@($leaving) -join ', ')]")

$journal.Note('待送点装了甲、丙没装：点出发之后丙放回待派，车带着甲走正常离站。')
