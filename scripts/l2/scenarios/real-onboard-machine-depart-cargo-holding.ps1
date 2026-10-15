#Requires -Version 7

<#
真装置：机台取货装完一篮、后面还挂着一个追加的取货停靠，操作员在车载屏上按「出发」（control-server#725、onboard-hmi#352，CP-0014 第 1 项，
协议 4.0.0 候选 3fb214e5 的 CV-CURRENT-STOP-DEPARTURE-MACHINE-CARGO-HOLDING）。真车载端 WPF + 真 slots-simulator，按钮与确认框走 UI Automation，
驱动与待送点那三条共用（StagingDepartureRigCommon.ps1）。

需求甲（N1-3 → 前侧，1 篮）在 12 号站装上；需求丙（C15-13 → 后侧）在车停在 12 号站时追加进这趟旅程（另开一个取货停靠，在 11 号站）。
站点离站与持货期限都是十分钟，车本该在 12 号站再等将近十分钟、然后去 11 号站装丙。操作员按「出发」：

判据（`L2-RMD-*`）：
- 01 前提：甲已装上（Committed），丙进了这趟旅程、取货停靠在后面；装货阶段没关、离站核验没发（车在等）；车载端屏上给出可按的「出发」。
- 02 往返：服务端收到真车载端发来的 CurrentStopDepartureRequested（操作员 L2-OPERATOR，站 N1-3_N1-7），答 ACCEPTED，车载端确认了答复。
- 03 放回（等同持货此刻到期）：丙离开这趟旅程（RELEASED_FOR_REDISPATCH），需求仍 Accepted、不抑制、不计站点超时放回，它的取货停靠被删；甲仍 LOADED。
- 04 立即离站：装货阶段以 OPERATOR_DEPARTED 关闭、屏上 LoadingClosedReason 读到它；服务端在按下之后一分钟内发出开往关卡（不是 11 号站）
  的离站核验——十分钟的站点期限没有等完。
- 05 送达：车开到关卡，甲卸货 Committed、需求 Succeeded，旅程 Completed；丙没有回到这趟旅程上。

每个第二事实都先等（README 第 14 条）：读屏上关闭原因前等离站核验发出；读放回前等归属行的 RemovedAt。
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

# 已发出的离站核验张数。Get-L2DepartureCheckTargets 以 `return , @(...)` 交回一个数组对象，直接 @(调用).Count 数到的是那一个对象（永远是 1）；
# 先落进变量再数才是张数（CI run 38760088856 的 L2-RMD-01 就红在这里：按出发之前实际 0 张，读成了 1 张）。
function Get-L2DepartureCheckCount([object]$Connection) {
    $targets = Get-L2DepartureCheckTargets $Connection
    return @($targets).Count
}

$all = @('L2-RMD-01', 'L2-RMD-02', 'L2-RMD-03', 'L2-RMD-04', 'L2-RMD-05')
# setup 的 Stations：甲在 12 号站 `N1-3_N1-7` 取货，丙在 11 号站 `C15-13`，卸货都在关卡。
$machineRiotId = 12
$machineStationName = 'N1-3_N1-7'
$a = New-L2CargoDemand 'A' 'N1-3' 1 $Context.RunId
$c = New-L2CargoDemand 'C' 'C15-13' 1 $Context.RunId

# --- 1. 甲在机台装上 --------------------------------------------------------------------------------------------------

Initialize-L2CargoRig $Context
Assert-L2RigBaselineSlots $Context 2
Publish-L2CargoDemand $Context $a
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $machineRiotId
$loadA = Invoke-L2RigLoad $Context $journeyId $a

# --- 2. 丙追加进来（后面另开一个取货停靠），车在 12 号站等 ---------------------------------------------------------

Publish-L2CargoDemand $Context $c
$joined = Wait-L2RealOrLast -Description 'demand C joined the journey' -Journal $journal -Criterion 'c-joined' -TimeoutSeconds 120 `
    -Probe { Get-L2StagingMembership $connection $c.Id } `
    -Until { param($v) $null -ne $v -and (-not (Test-L2RealPresent $v.RemovedAt)) -and [string]$v.JourneyId -eq $journeyId }
$cJoined = $null -ne $joined -and [string]$joined.JourneyId -eq $journeyId -and -not (Test-L2RealPresent $joined.RemovedAt)
$cStopId = if ($cJoined) { [string]$joined.PickupStopId } else { '' }
$waiting = Get-L2StagingJourney $connection $journeyId
$checksBefore = @(Get-L2DepartureCheckCount $connection)[0]
$offered = $false
if ($cJoined) {
    $offered = Wait-L2RealOrLast -Description 'the HMI offers 出发 at the machine pickup' -Journal $journal `
        -Criterion 'departure-offered-before-press' -TimeoutSeconds 60 -Probe { Test-L2DepartureButtonPressable $Context.Onboard } `
        -Until { param($v) $v }
    $waiting = Get-L2StagingJourney $connection $journeyId
    $checksBefore = @(Get-L2DepartureCheckCount $connection)[0]
}
$assertions.Add(
    'L2-RMD-01', '前提：甲已装上，丙进了这趟旅程、取货停靠在后面；装货阶段没关、离站核验没发；车载端屏上给出可按的「出发」',
    ($loadA.Status -eq 'Committed' -and $cJoined -and [string]$waiting.Stage -eq 'AwaitingStationDeparture' -and
        [string]$waiting.LoadingPhaseState -ne 'CLOSED' -and $checksBefore -eq 0 -and $offered),
    'A Committed / C on the journey / AwaitingStationDeparture, phase open / no departure check / 出发 pressable',
    "A $($loadA.Status) / C $(if ($cJoined) { 'on the journey' } else { 'not joined' }) / $($waiting.Stage), phase '$($waiting.LoadingPhaseState)' / checks $checksBefore / 出发 $(if ($offered) { 'pressable' } else { "not offered (notice '$(Get-L2LoadingPhaseLine $Context.Onboard 'CurrentStopDepartureNotice')')" })")
if (-not ($cJoined -and $offered)) {
    Add-L2RealNotReached $assertions @($all | Where-Object { $_ -ne 'L2-RMD-01' }) '丙没有进这趟旅程或屏上没有出发入口，后面几条的前提不成立'
    return
}

# --- 3. 操作员按「出发」-------------------------------------------------------------------------------------------

$pressedAt = Invoke-L2DeparturePress $Context
$roundTrip = Wait-L2DepartureRoundTrip $Context $pressedAt 'ACCEPTED'
$assertions.Add(
    'L2-RMD-02', "往返：真车载端发出 CurrentStopDepartureRequested（L2-OPERATOR，站 $machineStationName），服务端答 ACCEPTED，车载端确认了答复",
    (Test-L2DepartureRoundTrip $roundTrip 'ACCEPTED' $machineStationName),
    "request by L2-OPERATOR @$machineStationName / ACCEPTED acked", (Format-L2DepartureRoundTrip $roundTrip))

# --- 4. 丙按持货到期放回 --------------------------------------------------------------------------------------------

$released = Wait-L2RealOrLast -Description 'demand C left the journey' -Journal $journal -Criterion 'c-released' -TimeoutSeconds 60 `
    -Probe { Get-L2StagingMembership $connection $c.Id } -Until { param($v) $null -ne $v -and (Test-L2RealPresent $v.RemovedAt) }
$statusC = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($c.Id)'"
$suppressions = Get-L2RealCount $connection "SELECT COUNT(*) AS Total FROM TransportDemandSuppressions WHERE DemandId = '$($c.Id)'"
$deadlineReleases = Get-L2RealCount $connection "SELECT COUNT(*) AS Total FROM JourneyDemands WHERE RemovalReason = 'RELEASED_AT_STATION_DEADLINE'"
$cStopStatus = Get-L2RealScalar $connection "SELECT Status AS Value FROM JourneyStops WHERE StopId = '$cStopId'"
$memberA = Get-L2StagingMembership $connection $a.Id
$assertions.Add(
    'L2-RMD-03', '丙按持货到期放回积压：RELEASED_FOR_REDISPATCH、需求仍 Accepted、不抑制、不计站点超时放回，它的取货停靠被删；甲仍 LOADED',
    ($null -ne $released -and (Test-L2RealPresent $released.RemovedAt) -and [string]$released.RemovalReason -eq 'RELEASED_FOR_REDISPATCH' -and
        $statusC -eq 'Accepted' -and $suppressions -eq 0 -and $deadlineReleases -eq 0 -and $cStopStatus -eq 'REMOVED' -and
        [string]$memberA.Status -eq 'LOADED'),
    'RELEASED_FOR_REDISPATCH / Accepted / 抑制 0 / 超时放回 0 / C stop REMOVED / A LOADED',
    "$(if ($null -ne $released -and (Test-L2RealPresent $released.RemovedAt)) { $released.RemovalReason } else { '(still on)' }) / $statusC / 抑制 $suppressions / 超时放回 $deadlineReleases / C stop $cStopStatus / A $($memberA.Status)")

# --- 5. 立即离站，不等站点期限 --------------------------------------------------------------------------------------

$leaving = Wait-L2RealOrLast -Description 'a departure check towards the gate was sent' -Journal $journal -Criterion 'leaving' `
    -TimeoutSeconds 60 -Probe { Get-L2DepartureCheckTargets $connection } -Until { param($v) @($v).Count -ge 1 }
$leftWithin = [int]([DateTimeOffset]::UtcNow - $pressedAt).TotalSeconds
# 上限 60 秒：站点离站期限是十分钟，等完才走的实现过不了这一条；按下到核验发出实测在几秒以内，留足 vm01 慢时的余量。
$leftLimitSeconds = 60
$phase = Get-L2StagingJourney $connection $journeyId
$screenReason = Wait-L2RealOrLast -Description 'the HMI shows the loading phase closed by the operator' -Journal $journal `
    -Criterion 'loading-closed-on-screen' -TimeoutSeconds 30 -Probe { Get-L2LoadingPhaseLine $Context.Onboard 'LoadingClosedReason' } `
    -Until { param($v) $v -eq 'OPERATOR_DEPARTED' }
$targets = @($leaving)
$assertions.Add(
    'L2-RMD-04', '装货阶段以 OPERATOR_DEPARTED 关闭、屏上读到它；按下之后一分钟内发出开往关卡（不是 C15-13）的离站核验，没有等完十分钟的站点期限',
    ([string]$phase.LoadingPhaseState -eq 'CLOSED' -and [string]$phase.LoadingClosedReason -eq 'OPERATOR_DEPARTED' -and
        $screenReason -eq 'OPERATOR_DEPARTED' -and $targets.Count -ge 1 -and $targets[0] -eq '关卡' -and $leftWithin -le $leftLimitSeconds),
    "CLOSED/OPERATOR_DEPARTED, screen OPERATOR_DEPARTED, first check to 关卡 within ${leftLimitSeconds}s",
    "$($phase.LoadingPhaseState)/$($phase.LoadingClosedReason), screen '$screenReason', checks to [$($targets -join ', ')] ${leftWithin}s after the press")

# --- 6. 送达 -------------------------------------------------------------------------------------------------------

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $Context.GateStationRiotId
$unload = Invoke-L2RigUnloadNext $Context $journeyId @()
$completed = Wait-L2RealOrLast -Description 'the journey completed at the gate' -Journal $journal -Criterion 'journey-completed' `
    -TimeoutSeconds 120 -Probe { [string](Get-L2StagingJourney $connection $journeyId).Stage } -Until { param($v) $v -eq 'Completed' }
$statusA = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($a.Id)'"
$cBackOnJourney = Get-L2RealCount $connection (
    "SELECT COUNT(*) AS Total FROM JourneyDemands WHERE DemandId = '$($c.Id)' AND JourneyId = '$journeyId' AND RemovedAt IS NULL")
$assertions.Add(
    'L2-RMD-05', '车到关卡卸下甲（Committed）、甲 Succeeded、旅程 Completed；丙没有回到这趟旅程上',
    ($unload.DemandId -eq $a.Id -and $unload.Status -eq 'Committed' -and $statusA -eq 'Succeeded' -and $completed -eq 'Completed' -and
        $cBackOnJourney -eq 0),
    "unload $($a.Id) Committed / Succeeded / Completed / C off the journey",
    "unload $($unload.DemandId) $($unload.Status) / $statusA / $completed / C on the journey $cBackOnJourney")

$journal.Note('真装置：机台取货装了甲、后面追加的丙还没装，按出发之后丙按持货到期放回，车不等站点期限、带着甲去关卡送达。')
