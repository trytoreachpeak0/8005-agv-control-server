#Requires -Version 7

<#
站点期限第一次到点即终结、不放回、不再派车，真车载端上走得通（REQ-0365，CP-0016，control-server#751）。

服务端用出厂上限（StationTimeoutRedispatchLimit = 0，setup 不设）：车到站没人扫码，期限一到服务端以 CANCELLED_BY_STATION_TIMEOUT
终结需求、按业务键抑制、挂人工搬运告警，旅程收尾。车停在这一站，此后派车轮一轮轮跑，这条需求不再派回这辆车。

control-server#703 审查 M3 时这条场景证的是「放回、同一辆车重新派回同一站、新一趟照常扫码」；v5 不再放回，那一串不复存在，
场景名保留（改名要动 l2.yml 与既有证据），判据改成默认配置下的新行为。车上撤不撤干净那一站、迟到的取消与扫码，
由 real-onboard-stale-stop-after-station-timeout 三趟细判；这里判的是出厂配置下的一整段：终结、告警、车上不再要这单、不再派车、全程不断线。

一条需求，一趟：
  1. 车到站、车上要子批（正向锚点）；
  2. 没人扫，期限到：旅程以 CANCELLED_BY_STATION_TIMEOUT 收尾、需求 Cancelled、没有放回待派的归属行、人工搬运告警挂着；
  3. 车上不再要这个子批、清单不再挂着这单；
  4. 再等派车轮转十轮：仍只有这一条旅程、一张取货单，车上没再要这单，告警仍挂着；
  5. 全程车载端没有重连（SessionHello 次数不变）、主窗口还在。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2MultiStopJourney.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$onboard = $Context.Onboard
$riot = $Context.Riot
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')
$sublot = "L2-STRR-$($Context.RunId)"

# 车上此刻给的：录入框可用、清单里挂着这个子批。Get-L2WorklistRows 返回 $null 表示清单不在树里。
function Get-VehicleView {
    $rows = Get-L2WorklistRows $onboard
    $sublots = @(if ($null -ne $rows) { $rows | ForEach-Object { [string]$_.Sublot } })
    return [pscustomobject]@{
        CanSubmit   = [bool]$onboard.CanSubmit()
        ListsSublot = $sublots -contains $sublot
        Rows        = ($sublots -join ',')
    }
}

function Format-VehicleView([object]$View) { return "canSubmit=$($View.CanSubmit) listsSublot=$($View.ListsSublot) worklist=[$($View.Rows)]" }

# 这条需求的第 $Visit 趟（从 1 数），按派车代次排序取第几条旅程行。
function Get-Journey([int]$Visit) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, Stage, BlockReasonCode, PickupUpperId FROM JourneyRuntimes WHERE DemandId = '$demandId' ORDER BY DispatchGeneration")
    if ($rows.Count -lt $Visit) { return $null }
    return $rows[$Visit - 1]
}

function Get-HelloCount {
    return Get-L2RealCount $connection "SELECT COUNT(*) AS Total FROM ProtocolInbox WHERE MessageType = 'SessionHello'"
}

function Test-OnboardAlive {
    $process = Get-Process -Id $onboard.ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process -or $process.HasExited -or $null -eq $onboard.Window) { return $false }
    try {
        $null = $onboard.Window.Current.Name
        return $true
    } catch [System.Windows.Automation.ElementNotAvailableException] {
        return $false
    }
}

# 车开到第 $Visit 趟的取货站：用这一趟自己的取货单。
function Move-ToPickup([int]$Visit) {
    $upperId = [string](Get-Journey $Visit).PickupUpperId
    $intent = Wait-L2Condition -Description "the TO_PICKUP intent of visit $Visit ($upperId) was confirmed" `
        -Journal $journal -Criterion "pickup-intent-$Visit" -TimeoutSeconds 120 `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE UpperId = '$upperId'"
            if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
        } `
        -Until { param($v) $null -ne $v }
    Move-L2RealVehicleTo $Context $intent $Context.PickupStationRiotId "the pickup station (visit $Visit)"
}

# --- 1. 到站、车上要子批 -----------------------------------------------------------------------------------------------

$journal.Note("Publishing demand $($demandGuid.ToString('N')) (sublot $sublot).")
$null = $Context.MesIngest.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = $sublot; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})
$null = Wait-L2Condition -Description 'visit 1 was dispatched' -Journal $journal -Criterion 'visit-1-dispatched' -TimeoutSeconds 120 `
    -Probe { Get-Journey 1 } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
Move-ToPickup 1
$offered = Wait-L2RealOrLast -Description 'the vehicle asks for the sublot on visit 1' `
    -Journal $journal -Criterion 'visit-1-offered' -TimeoutSeconds 120 `
    -Probe { Get-VehicleView } -Until { param($v) $v.CanSubmit -and $v.ListsSublot }
$assertions.Add(
    'L2-RSTR-01', '车到站后要子批、清单挂着这单（正向锚点）',
    ($offered.CanSubmit -and $offered.ListsSublot), 'canSubmit / listsSublot 均为 True', (Format-VehicleView $offered))
$hellos = Get-HelloCount

# --- 2. 期限到：终结、告警，不放回 -------------------------------------------------------------------------------------

$journal.Note('Nobody scans. Waiting for the station deadline to end the demand.')
$ended = Wait-L2RealOrLast -Description 'the station deadline ended the demand and closed visit 1' `
    -Journal $journal -Criterion 'visit-1-ended' -TimeoutSeconds 180 `
    -Probe { Get-Journey 1 } -Until { param($v) [string]$v.Stage -eq 'Completed' }
$status = [string](Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'")
$released = Get-L2RealCount $connection (
    "SELECT COUNT(*) AS Total FROM JourneyDemands WHERE DemandId = '$demandId' AND RemovalReason = 'RELEASED_AT_STATION_DEADLINE'")
$alarms = Get-L2RealCount $connection (
    "SELECT COUNT(*) AS Total FROM StructuralDispatchBlocks WHERE DemandId = '$demandId' " +
    "AND ReasonCode = 'STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED' AND ClearedAt IS NULL")
$assertions.Add(
    'L2-RSTR-02', '期限到（出厂上限 0）：旅程以 CANCELLED_BY_STATION_TIMEOUT 收尾、需求 Cancelled、没有放回、人工搬运告警挂着',
    ([string]$ended.Stage -eq 'Completed' -and [string]$ended.BlockReasonCode -eq 'CANCELLED_BY_STATION_TIMEOUT' -and
        $status -eq 'Cancelled' -and $released -eq 0 -and $alarms -eq 1),
    'Completed / CANCELLED_BY_STATION_TIMEOUT / Cancelled / 放回 0 / 告警 1',
    "$($ended.Stage) / $($ended.BlockReasonCode) / $status / 放回 $released / 告警 $alarms")

# --- 3. 车上不再要这单 ---------------------------------------------------------------------------------------------------

$withdrawn = Wait-L2RealOrLast -Description 'the vehicle no longer asks for the sublot' `
    -Journal $journal -Criterion 'visit-1-withdrawn' -TimeoutSeconds 60 `
    -Probe { Get-VehicleView } -Until { param($v) -not $v.ListsSublot -and -not $v.CanSubmit }
$assertions.Add(
    'L2-RSTR-03', '终结之后，车上不再要这个子批、清单不再挂着这单',
    (-not $withdrawn.CanSubmit -and -not $withdrawn.ListsSublot), 'canSubmit / listsSublot 均为 False', (Format-VehicleView $withdrawn))

# --- 4. 不再派车 ---------------------------------------------------------------------------------------------------------

$null = Wait-L2Iterations -Riot $riot -Count 10 -TimeoutSeconds 90 -Journal $journal
$journeys = Get-L2RealCount $connection "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE DemandId = '$demandId'"
$pickupOrders = Get-L2RealCount $connection (
    "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'")
$view = Get-VehicleView
$alarms = Get-L2RealCount $connection (
    "SELECT COUNT(*) AS Total FROM StructuralDispatchBlocks WHERE DemandId = '$demandId' " +
    "AND ReasonCode = 'STATION_TIMEOUT_REDISPATCH_LIMIT_REACHED' AND ClearedAt IS NULL")
$assertions.Add(
    'L2-RSTR-04', '派车轮再转十轮：仍只有一条旅程、一张取货单，车上没再要这单，告警仍挂着',
    ($journeys -eq 1 -and $pickupOrders -eq 1 -and -not $view.ListsSublot -and $alarms -eq 1),
    '1 / 1 / listsSublot False / 告警 1', "$journeys / $pickupOrders / listsSublot $($view.ListsSublot) / 告警 $alarms")

# --- 5. 全程不断线 -------------------------------------------------------------------------------------------------------

$hellosAfter = Get-HelloCount
$alive = Test-OnboardAlive
$assertions.Add(
    'L2-RSTR-05', '全程车载端没有重连（SessionHello 次数不变）、主窗口还在',
    ($hellosAfter -eq $hellos -and $alive),
    "SessionHello $hellos → 不变；主窗口在", "SessionHello $hellos → $hellosAfter；主窗口 $(if ($alive) { '在' } else { '不在' })")

$journal.Note('Scenario finished.')
