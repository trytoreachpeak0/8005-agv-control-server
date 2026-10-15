#Requires -Version 7

<#
真装置：待送点装了一部分，操作员在车载屏上按「出发」（program#184 第一波集成准备；control-server#713、onboard-hmi#349，CP-0012，
协议 4.0.0 候选）。真车载端 WPF + 真 slots-simulator，按钮与确认框走 UI Automation。

与合成场景 staging-depart-after-partial-load 同一个形状：需求甲（N1-3 → 前侧，1 个花篮）在派工待送站 305 装上；需求丙（C15-13 → 后侧）
在车停在 305 时追加进这趟旅程（同站新开一个停靠），车原地走过去，但没人来扫丙。然后操作员按「出发」。合成场景里请求由合成对端直接发，
这里由真车载端在操作员确认之后发——两端真组件之间的这一次往返，是本场景要证的东西（StagingDepartureRigCommon.ps1 开头）。

判据（`L2-RSP-*`）：
- 01 前提：甲已装上（Committed），丙进了这趟旅程、车停在丙的停靠上等录入；车载端屏上给出可按的「出发」。
- 02 往返：服务端收到真车载端发来的 CurrentStopDepartureRequested（操作员 L2-OPERATOR，站 派工待送取货），答 CurrentStopDepartureResult /
  ACCEPTED，车载端确认了答复（记进日志簿后才确认）。屏上那一读只记录、不作判据（StagingDepartureRigCommon.ps1）。
- 03 放回：丙离开这趟旅程（RELEASED_FOR_REDISPATCH），需求仍 Accepted、不抑制、不计站点超时放回；甲仍是 LOADED。
- 04 离站：装货阶段以 OPERATOR_DEPARTED 关闭，车载端屏上 LoadingClosedReason 读到 OPERATOR_DEPARTED；服务端发出开往卸货站
  （不是待送站）的离站核验。
- 05 送达：车开到 12 号站，甲卸货 Committed、需求 Succeeded，旅程 Completed。丙没有回到这趟旅程上（装货阶段关闭之后不再追加）。

每个第二事实都先等（README 第 14 条）：读屏上关闭原因前等离站核验发出；读放回前等归属行的 RemovedAt。

**红证据：L2 层面目前一次都没有**（PR #724 独立审查 M3）。产品侧变异取红由审查第二阶段补，补上之后写进这里。
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

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: it presses 出发 on the shipped HMI through UI Automation.'
}

$all = @('L2-RSP-01', 'L2-RSP-02', 'L2-RSP-03', 'L2-RSP-04', 'L2-RSP-05')
# 甲（N1-3）卸在 12 号站 `N1-3_N1-7`（setup 的 Stations）。
$dropOffRiotId = 12
$a = New-L2StagingDemand 'A' 'N1-3' 'L2-RSP' $Context.RunId
$c = New-L2StagingDemand 'C' 'C15-13' 'L2-RSP' $Context.RunId

# --- 1. 甲在待送点装上 ------------------------------------------------------------------------------------------------

Initialize-L2CargoRig $Context
Assert-L2RigBaselineSlots $Context 2
Publish-L2StagingDemand $Context $a 'EQP-L2-RSP-01'
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $script:StagingStationRiotId
$loadA = Invoke-L2RigLoad $Context $journeyId $a

# --- 2. 丙追加进来，车停在丙的停靠上，没人扫 ------------------------------------------------------------------------

# 追加只在车停在站上时发生（用户 09-22 决定）；甲装完之后车停在 305，正是时候。丙另开一个同站停靠（当前停靠不并，REQ-0196）。
Publish-L2StagingDemand $Context $c 'EQP-L2-RSP-01'
$joined = Wait-L2RealOrLast -Description 'demand C joined the journey' -Journal $journal -Criterion 'c-joined' -TimeoutSeconds 120 `
    -Probe { Get-L2StagingMembership $connection $c.Id } `
    -Until { param($v) $null -ne $v -and (-not (Test-L2RealPresent $v.RemovedAt)) -and [string]$v.JourneyId -eq $journeyId }
$cJoined = $null -ne $joined -and [string]$joined.JourneyId -eq $journeyId -and -not (Test-L2RealPresent $joined.RemovedAt)
$waitingC = $null
$offered = $false
if ($cJoined) {
    $null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $script:StagingStationRiotId ([string]$joined.PickupStopId)
    $waitingC = Wait-L2RealOrLast -Description "the vehicle waits for C's sublot at its stop and the HMI accepts entry" -Journal $journal `
        -Criterion 'c-waiting' -TimeoutSeconds 120 `
        -Probe { "$([string](Get-L2StagingJourney $connection $journeyId).Stage)/$($Context.Onboard.CanSubmit())" } `
        -Until { param($v) $v -eq 'AwaitingSublot/True' }
    $offered = Wait-L2RealOrLast -Description 'the HMI offers 出发' -Journal $journal -Criterion 'departure-offered-before-press' `
        -TimeoutSeconds 60 -Probe { Test-L2DepartureButtonPressable $Context.Onboard } -Until { param($v) $v }
}
$assertions.Add(
    'L2-RSP-01', '前提：甲已装上，丙进了这趟旅程、车停在丙的停靠上等录入，车载端屏上给出可按的「出发」',
    ($loadA.Status -eq 'Committed' -and $cJoined -and $waitingC -eq 'AwaitingSublot/True' -and $offered),
    'A Committed / C on the journey / AwaitingSublot/True / 出发 pressable',
    "A $($loadA.Status) / C $(if ($cJoined) { 'on the journey' } else { "not joined ($(${joined}?.JourneyId))" }) / $waitingC / 出发 $(if ($offered) { 'pressable' } else { "not offered (notice '$(Get-L2LoadingPhaseLine $Context.Onboard 'CurrentStopDepartureNotice')')" })")
if (-not ($cJoined -and $offered)) {
    Add-L2RealNotReached $assertions @($all | Where-Object { $_ -ne 'L2-RSP-01' }) '丙没有进这趟旅程或屏上没有出发入口，后面几条的前提不成立'
    return
}

# --- 3. 操作员按「出发」-------------------------------------------------------------------------------------------

$pressedAt = Invoke-L2DeparturePress $Context
$roundTrip = Wait-L2DepartureRoundTrip $Context $pressedAt 'ACCEPTED'
$assertions.Add(
    'L2-RSP-02', '往返：真车载端发出 CurrentStopDepartureRequested（L2-OPERATOR，站 派工待送取货），服务端答 ACCEPTED，车载端确认了答复（记进日志簿后才确认）；屏上那一读只记录、不判',
    (Test-L2DepartureRoundTrip $roundTrip 'ACCEPTED'),
    'request by L2-OPERATOR @派工待送取货 / ACCEPTED acked', (Format-L2DepartureRoundTrip $roundTrip))

# --- 4. 丙放回 ----------------------------------------------------------------------------------------------------

$released = Wait-L2RealOrLast -Description 'demand C left the journey' -Journal $journal -Criterion 'c-released' -TimeoutSeconds 60 `
    -Probe { Get-L2StagingMembership $connection $c.Id } -Until { param($v) $null -ne $v -and (Test-L2RealPresent $v.RemovedAt) }
$statusC = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($c.Id)'"
$suppressions = Get-L2RealCount $connection "SELECT COUNT(*) AS Total FROM TransportDemandSuppressions WHERE DemandId = '$($c.Id)'"
$deadlineReleases = Get-L2RealCount $connection "SELECT COUNT(*) AS Total FROM JourneyDemands WHERE RemovalReason = 'RELEASED_AT_STATION_DEADLINE'"
$memberA = Get-L2StagingMembership $connection $a.Id
$assertions.Add(
    'L2-RSP-03', '丙放回积压：原因 RELEASED_FOR_REDISPATCH、需求仍 Accepted、不抑制、不计站点超时放回；甲仍 LOADED',
    ($null -ne $released -and (Test-L2RealPresent $released.RemovedAt) -and [string]$released.RemovalReason -eq 'RELEASED_FOR_REDISPATCH' -and
        $statusC -eq 'Accepted' -and $suppressions -eq 0 -and $deadlineReleases -eq 0 -and [string]$memberA.Status -eq 'LOADED'),
    'RELEASED_FOR_REDISPATCH / Accepted / 抑制 0 / 超时放回 0 / A LOADED',
    "$(if ($null -ne $released -and (Test-L2RealPresent $released.RemovedAt)) { $released.RemovalReason } else { '(still on)' }) / $statusC / 抑制 $suppressions / 超时放回 $deadlineReleases / A $($memberA.Status)")

# --- 5. 带着甲离站 --------------------------------------------------------------------------------------------------

$leaving = Wait-L2RealOrLast -Description 'a departure check towards the drop-off was sent' -Journal $journal -Criterion 'leaving' `
    -TimeoutSeconds 60 -Probe { Get-L2DepartureCheckTargets $connection } `
    -Until { param($v) @($v | Where-Object { $_ -ne $script:StagingStationName }).Count -ge 1 }
$phase = Get-L2StagingJourney $connection $journeyId
$screenReason = Wait-L2RealOrLast -Description 'the HMI shows the loading phase closed by the operator' -Journal $journal `
    -Criterion 'loading-closed-on-screen' -TimeoutSeconds 30 -Probe { Get-L2LoadingPhaseLine $Context.Onboard 'LoadingClosedReason' } `
    -Until { param($v) $v -eq 'OPERATOR_DEPARTED' }
$assertions.Add(
    'L2-RSP-04', '装货阶段以 OPERATOR_DEPARTED 关闭、屏上 LoadingClosedReason 读到它，服务端发出开往卸货站的离站核验',
    ([string]$phase.LoadingPhaseState -eq 'CLOSED' -and [string]$phase.LoadingClosedReason -eq 'OPERATOR_DEPARTED' -and
        $screenReason -eq 'OPERATOR_DEPARTED' -and @($leaving | Where-Object { $_ -ne $script:StagingStationName }).Count -ge 1),
    'CLOSED/OPERATOR_DEPARTED, screen OPERATOR_DEPARTED, check to the drop-off',
    "$($phase.LoadingPhaseState)/$($phase.LoadingClosedReason), screen '$screenReason', checks to [$(@($leaving) -join ', ')]")

# --- 6. 送达 -------------------------------------------------------------------------------------------------------

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $dropOffRiotId
$unload = Invoke-L2RigUnloadNext $Context $journeyId @()
$completed = Wait-L2RealOrLast -Description 'the journey completed at the drop-off' -Journal $journal -Criterion 'journey-completed' `
    -TimeoutSeconds 120 -Probe { [string](Get-L2StagingJourney $connection $journeyId).Stage } -Until { param($v) $v -eq 'Completed' }
$statusA = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($a.Id)'"
$cBackOnJourney = Get-L2RealCount $connection (
    "SELECT COUNT(*) AS Total FROM JourneyDemands WHERE DemandId = '$($c.Id)' AND JourneyId = '$journeyId' AND RemovedAt IS NULL")
$assertions.Add(
    'L2-RSP-05', '车到 12 号站卸下甲（Committed）、甲 Succeeded、旅程 Completed；丙没有回到这趟旅程上',
    ($unload.DemandId -eq $a.Id -and $unload.Status -eq 'Committed' -and $statusA -eq 'Succeeded' -and $completed -eq 'Completed' -and
        $cBackOnJourney -eq 0),
    "unload $($a.Id) Committed / Succeeded / Completed / C off the journey",
    "unload $($unload.DemandId) $($unload.Status) / $statusA / $completed / C on the journey $cBackOnJourney")

$journal.Note('真装置：待送点装了甲、丙没装，按出发之后丙放回待派，车带着甲离站送达。')
