#Requires -Version 7

<#
真装置：待送点一篮没装，操作员在车载屏上按「出发」（program#184 第一波集成准备；control-server#713、onboard-hmi#349，CP-0012，
协议 4.0.0 候选）。真车载端 WPF + 真 slots-simulator，按钮与确认框走 UI Automation。

与合成场景 staging-depart-with-nothing-loaded 同一个形状：一条待送需求，车开到派工待送站 305 停住，没人扫码，操作员按「出发」。
一篮没装时出发的效果是旅程原地收尾、需求放回，车不动（wire-notes「Staging pickup stops」第 4 条）。合成场景里请求由合成对端直接发、
收尾那几张快照由合成对端照单全收；这里两头都是真组件：请求由真车载端在操作员确认之后发，收尾的空清单要真车载端确认。

判据（`L2-RSN-*`）：
- 01 前提：车停在 305 等录入、屏上能录入（CanSubmit），屏上给出可按的「出发」。
- 02 往返：服务端收到真车载端发来的 CurrentStopDepartureRequested（L2-OPERATOR，站 派工待送取货），答 ACCEPTED，车载端确认了答复，
  屏上状态行读到 ACCEPTED。
- 03 原地收尾：旅程 Completed / STAGING_OPERATOR_DEPARTED；一张 stopEndedReason 为 OPERATOR_DEPARTED 的清单发出并被真车载端确认；
  需求放回（RELEASED_FOR_REDISPATCH）、仍 Accepted、不抑制。
- 04 车不动：出发之后没有离站核验；之后新建的订单（放回的需求多半又派给这辆停在待送点上的车）只开往待送站本身；模拟器八个仓都关门、
  空、锁上——没有哪扇门因为出发被打开。

**红证据**：本条是真装置场景，红证据在合成场景 staging-depart-with-nothing-loaded 与两端 L1/G2 上取。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2MultiStopJourney.psm1') -Force
. (Join-Path $PSScriptRoot 'StagingDepartureRigCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$simulator = $Context.Simulator

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: it presses 出发 on the shipped HMI through UI Automation.'
}

$all = @('L2-RSN-01', 'L2-RSN-02', 'L2-RSN-03', 'L2-RSN-04')
$a = New-L2StagingDemand 'A' 'N1-3' 'L2-RSN' $Context.RunId

# --- 1. 车开到待送点，没人扫码 ----------------------------------------------------------------------------------------

# Not Initialize-L2CargoRig: it also waits for the route graph engine, which this setup leaves off (no append here).
$null = $Context.Riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.GateStationRiotId
})
Assert-L2RigBaselineSlots $Context 1
Publish-L2StagingDemand $Context $a 'EQP-L2-RSN-01'
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $script:StagingStationRiotId
$waiting = Wait-L2RealOrLast -Description 'the vehicle waits for a sublot at the staging station and the HMI accepts entry' `
    -Journal $journal -Criterion 'awaiting-sublot' -TimeoutSeconds 120 `
    -Probe { "$([string](Get-L2StagingJourney $connection $journeyId).Stage)/$($Context.Onboard.CanSubmit())" } `
    -Until { param($v) $v -eq 'AwaitingSublot/True' }
$offered = Wait-L2RealOrLast -Description 'the HMI offers 出发' -Journal $journal -Criterion 'departure-offered-before-press' `
    -TimeoutSeconds 60 -Probe { Test-L2DepartureButtonPressable $Context.Onboard } -Until { param($v) $v }
$assertions.Add(
    'L2-RSN-01', '前提：车停在待送点 305 等录入、屏上能录入，屏上给出可按的「出发」',
    ($waiting -eq 'AwaitingSublot/True' -and $offered),
    'AwaitingSublot/True / 出发 pressable',
    "$waiting / 出发 $(if ($offered) { 'pressable' } else { "not offered (notice '$(Get-L2LoadingPhaseLine $Context.Onboard 'CurrentStopDepartureNotice')')" })")
if (-not $offered) {
    Add-L2RealNotReached $assertions @($all | Where-Object { $_ -ne 'L2-RSN-01' }) '屏上没有出发入口，后面几条的前提不成立'
    return
}

# --- 2. 操作员按「出发」-------------------------------------------------------------------------------------------

$intentsBefore = Get-L2RealCount $connection 'SELECT COUNT(*) AS Total FROM OrderIntents'
$pressedAt = Invoke-L2DeparturePress $Context
$roundTrip = Wait-L2DepartureRoundTrip $Context $pressedAt 'ACCEPTED'
$assertions.Add(
    'L2-RSN-02', '往返：真车载端发出 CurrentStopDepartureRequested（L2-OPERATOR，站 派工待送取货），服务端答 ACCEPTED，车载端确认答复、屏上状态 ACCEPTED',
    (Test-L2DepartureRoundTrip $roundTrip 'ACCEPTED'),
    'request by L2-OPERATOR @派工待送取货 / ACCEPTED acked / screen STATUS:ACCEPTED or ENDED:已按出发', (Format-L2DepartureRoundTrip $roundTrip))

# --- 3. 旅程原地收尾、需求放回 ----------------------------------------------------------------------------------------

$closed = Wait-L2RealOrLast -Description 'the journey closed in place' -Journal $journal -Criterion 'closed' -TimeoutSeconds 60 `
    -Probe { Get-L2StagingJourney $connection $journeyId } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'Completed' }
$endWorklist = Wait-L2RealOrLast -Description 'the OPERATOR_DEPARTED worklist was acknowledged by the real onboard' -Journal $journal `
    -Criterion 'end-worklist-acknowledged' -TimeoutSeconds 60 `
    -Probe {
        @((Get-L2RealOutbound $connection 'CurrentStopWorklistSnapshot') | Where-Object {
                [string]$_.Payload.stopEndedReason -eq 'OPERATOR_DEPARTED' }) | Select-Object -First 1
    } `
    -Until { param($v) $null -ne $v -and $v.Acknowledged }
$membership = Invoke-L2Query -Connection $connection -Sql (
    "SELECT IFNULL(RemovalReason, '') AS RemovalReason FROM JourneyDemands WHERE DemandId = '$($a.Id)' AND JourneyId = '$journeyId'")
$statusA = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($a.Id)'"
$suppressions = Get-L2RealCount $connection "SELECT COUNT(*) AS Total FROM TransportDemandSuppressions WHERE DemandId = '$($a.Id)'"
$assertions.Add(
    'L2-RSN-03', '旅程原地收尾（STAGING_OPERATOR_DEPARTED），OPERATOR_DEPARTED 的清单被真车载端确认；需求放回、仍 Accepted、不抑制',
    ($null -ne $closed -and [string]$closed.Stage -eq 'Completed' -and [string]$closed.BlockReasonCode -eq 'STAGING_OPERATOR_DEPARTED' -and
        $null -ne $endWorklist -and $endWorklist.Acknowledged -and $membership.Count -eq 1 -and
        [string]$membership[0].RemovalReason -eq 'RELEASED_FOR_REDISPATCH' -and $statusA -eq 'Accepted' -and $suppressions -eq 0),
    'Completed/STAGING_OPERATOR_DEPARTED, OPERATOR_DEPARTED acked, RELEASED_FOR_REDISPATCH/Accepted, 抑制 0',
    "$(${closed}?.Stage)/$(${closed}?.BlockReasonCode), end worklist $(if ($null -eq $endWorklist) { '(none)' } elseif ($endWorklist.Acknowledged) { 'acked' } else { 'not acked' }), " +
    "$(if ($membership.Count -eq 1) { $membership[0].RemovalReason } else { "($($membership.Count) rows)" })/$statusA, 抑制 $suppressions")

# --- 4. 车不动 -------------------------------------------------------------------------------------------------------

# 放回的需求还在 MES 目录里，下一轮派车多半又派给这辆停在待送点上的车（新旅程、开往待送站本身的新单）。那不是「车动了」：判据只看
# 出发之后有没有离站核验、新订单开往哪里、有没有哪扇门被打开。几轮之后再读（不读一次就断言）。
$null = Wait-L2Iterations -Riot $Context.Riot -Count 3 -Journal $journal
# Whole array already (return , ...): wrapped in @() its Count would be 1 whatever it held (Test-L2WholeArrayReturn).
$checks = Get-L2DepartureCheckTargets $connection
$orderRows = Invoke-L2Query -Connection $connection -Sql 'SELECT TargetStationId FROM OrderIntents ORDER BY CreatedAt, UpperId'
$newTargets = @(foreach ($row in $orderRows) { [string]$row.TargetStationId }) | Select-Object -Skip $intentsBefore
$slots = @($simulator.Snapshot().slots | ForEach-Object { "$($_.doorState)/$($_.cargoState)/$($_.lockFeedbackRaw)/$($_.unlockOutputRaw)" })
$notAtRest = @($slots | Where-Object { $_ -ne 'CLOSED/EMPTY/1/0' })
$assertions.Add(
    'L2-RSN-04', '车不动：出发之后没有离站核验，新建的订单（若有）只开往待送站本身，八个仓都关门、空、锁上、复位',
    ($checks.Count -eq 0 -and @($newTargets | Where-Object { $_ -ne "$($script:StagingStationRiotId)" -and $_ -ne $script:StagingStationName }).Count -eq 0 -and
        $slots.Count -eq 8 -and $notAtRest.Count -eq 0),
    '离站核验 0 张 / 新订单只开往待送站 / 8 仓 CLOSED/EMPTY/1/0',
    "离站核验 $($checks.Count) 张 / 新订单开往 [$(@($newTargets) -join ', ')] / $($slots.Count) 仓，不在原位 [$($notAtRest -join ', ')]")

$journal.Note('真装置：待送点一篮没装就按出发，旅程原地收尾、需求放回，车留在待送点。')
