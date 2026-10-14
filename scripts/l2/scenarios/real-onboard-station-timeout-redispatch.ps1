#Requires -Version 7

<#
站点超时放回待派、同一辆车重新派回同一站，真车载端上走得通（control-server#703 审查 M3）。

生产用默认上限（StationTimeoutRedispatchLimit = 2）：车到站没人扫码，期限一到服务端把需求放回待派，旅程以
RELEASED_AT_STATION_DEADLINE 收尾，车载端先收到说 DEMAND_RELEASED 的收尾快照；紧接着同一个 demandId 多半被重新派回这辆车、这一站。
这一串真车载端从没跑过。合成对端只看得到线上报文，车上是不是撤干净了旧的那一站、新的一趟能不能照常扫码装货，只有真 WPF 加
UI Automation 看得见。

一条需求，两趟：
  1. 第一趟：车到站、车上要子批（正向锚点）；没人扫，期限到，服务端放回并收尾。车就停在这一站，下一轮多半立刻被重新派车，
     所以「旧的那一站撤干净」不单独判，判的是下面新一趟的录入能用、全程没断线。
  2. 第二趟：同一个 demandId 以新的一代被重新派车，车再到这一站；车上又要这个子批；操作员扫码，服务端受理并下装货命令。
全程车载端没有重连（SessionHello 次数不变）、主窗口还在：收尾快照与新一趟的清单号若撞号，车会以 SNAPSHOT_REVISION_CONTENT_CONFLICT
断会话，重连次数会多出来。
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

# --- 1. 第一趟：到站、没人扫、期限到放回 ------------------------------------------------------------------------------

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
    'L2-RSTR-01', '第一趟：车到站后要子批、清单挂着这单（正向锚点）',
    ($offered.CanSubmit -and $offered.ListsSublot), 'canSubmit / listsSublot 均为 True', (Format-VehicleView $offered))
$hellos = Get-HelloCount

$journal.Note('Nobody scans. Waiting for the station deadline to release the demand.')
$released = Wait-L2RealOrLast -Description 'the station deadline released the demand and closed visit 1' `
    -Journal $journal -Criterion 'visit-1-released' -TimeoutSeconds 180 `
    -Probe { Get-Journey 1 } -Until { param($v) [string]$v.Stage -eq 'Completed' }
$status = [string](Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'")
$assertions.Add(
    'L2-RSTR-02', '第一趟：期限到，旅程以 RELEASED_AT_STATION_DEADLINE 收尾，需求仍 Accepted（放回待派，不是终结）',
    ([string]$released.Stage -eq 'Completed' -and [string]$released.BlockReasonCode -eq 'RELEASED_AT_STATION_DEADLINE' -and $status -eq 'Accepted'),
    'Completed / RELEASED_AT_STATION_DEADLINE / Accepted', "$($released.Stage) / $($released.BlockReasonCode) / $status")

# --- 2. 第二趟：重新派回、再到站、扫码装货 ----------------------------------------------------------------------------

$second = Wait-L2RealOrLast -Description 'the released demand was dispatched again' `
    -Journal $journal -Criterion 'visit-2-dispatched' -TimeoutSeconds 120 `
    -Probe { Get-Journey 2 } -Until { param($v) $null -ne $v }
if ($null -eq $second) {
    Add-L2RealNotReached $assertions @('L2-RSTR-03', 'L2-RSTR-04', 'L2-RSTR-05') 'the released demand was never dispatched again'
    $journal.Note('Scenario finished early.')
    return
}
Move-ToPickup 2
$offeredAgain = Wait-L2RealOrLast -Description 'the vehicle asks for the sublot again on visit 2' `
    -Journal $journal -Criterion 'visit-2-offered' -TimeoutSeconds 180 `
    -Probe { Get-VehicleView } -Until { param($v) $v.CanSubmit -and $v.ListsSublot }
$assertions.Add(
    'L2-RSTR-03', '第二趟：同一个需求重新派回、车再到站，车上又要这个子批',
    ($offeredAgain.CanSubmit -and $offeredAgain.ListsSublot), 'canSubmit / listsSublot 均为 True', (Format-VehicleView $offeredAgain))

$journal.Note("Typing sublot $sublot into ScanTextBox through UI Automation.")
$onboard.SetSublot($sublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'visit-2-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()
$loadAttempt = Wait-L2RealOrLast -Description 'the server issued the load command for visit 2' `
    -Journal $journal -Criterion 'visit-2-load' -TimeoutSeconds 120 `
    -Probe { Get-L2RealScalar $connection "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'" } `
    -Until { param($v) $v }
$assertions.Add(
    'L2-RSTR-04', '第二趟：操作员扫码，服务端受理并下装货命令',
    (Test-L2RealPresent $loadAttempt), '有一条装货仓位操作', $(if (Test-L2RealPresent $loadAttempt) { [string]$loadAttempt } else { '(none)' }))

$hellosAfter = Get-HelloCount
$alive = Test-OnboardAlive
$assertions.Add(
    'L2-RSTR-05', '全程车载端没有重连（SessionHello 次数不变，收尾快照与新一趟的清单号没撞）、主窗口还在',
    ($hellosAfter -eq $hellos -and $alive),
    "SessionHello $hellos → 不变；主窗口在", "SessionHello $hellos → $hellosAfter；主窗口 $(if ($alive) { '在' } else { '不在' })")

$journal.Note('Scenario finished.')
