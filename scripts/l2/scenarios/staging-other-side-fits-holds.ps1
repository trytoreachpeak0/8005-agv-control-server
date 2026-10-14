#Requires -Version 7

<#
待送点一侧满，另一侧还能装的需求来了，车不走、先装它（control-server#710，program#184 R1；CP-0012、ADR-cross-0065）。

待送点规则打开。一条 STAGING_TO_WIRE 需求甲（AREA N1-3 → FRONT，4 个花篮）在派工待送站 305 装上，FRONT 组四个仓满；REAR 组全空。

  1. 甲装完、没有等着的需求：CARGO_HOLDING_WAIT，满的一侧只有 FRONT。
  2. 来一条待送需求丙（AREA C15-13 → REAR，1 个花篮）：REAR 装得下，派车轮判它合格，追加进这趟旅程（同站新开一个停靠）。
     FRONT 满不算整车满：车不开往卸货站，原地走到丙的停靠（离站核验只开往待送站本身）。
  3. 丙装进 REAR 组的仓（5～8），之后没有等着的需求：车仍不走，装货阶段回到 CARGO_HOLDING_WAIT，再转五轮也没有开往卸货站的离站核验。

一侧满即走的另一半是 staging-side-full-departs。「全部装不下才走」与「任一装不下就走」的分别，合成装置里看不出来：一条合格的候选在判出它的
那一轮就被追加，不会以「合格但没装」的样子留到下一轮，那一格由单测（StagingDepartureRuleTests、StagingDepartureFullOrNoFitTests 验收 2）证明。

**红证据**（缺陷版本）：把待送点的判定改成「有一侧满就走」，第 1 步车就判 VEHICLE_FULL 开往卸货站，L2-SOF-01、03 变红。

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

$stagingStationRiotId = 305
$stagingStationName = '派工待送取货'

function Publish-StagingDemand([hashtable]$Demand) {
    $journal.Note("Publishing STAGING_TO_WIRE demand $($Demand.Label) $($Demand.Wire) (area $($Demand.Area), $($Demand.Baskets) baskets).")
    $null = $Context.MesIngest.Command('Put', "demands/$($Demand.Wire)", @{
        sublot      = $Demand.Sublot
        area        = $Demand.Area
        eqp         = 'EQP-L2-SOF-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4 * $Demand.Baskets
        workType    = 'STAGING_TO_WIRE'
    })
}

# 发给车的离站核验开往的站，按入队先后。
function Get-DepartureTargets {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'PreDepartureSafetyCheck' ORDER BY CreatedAt, MessageId"
    return @(foreach ($row in $rows) { [string]([string]$row.PayloadJson | ConvertFrom-Json -Depth 20).payload.targetStationId })
}

function Get-Membership([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, PickupStopId, Status, TargetSlotsJson FROM JourneyDemands WHERE DemandId = '$DemandId' AND RemovedAt IS NULL")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

$a = New-L2CargoDemand 'A' 'N1-3' 4 $Context.RunId
$c = New-L2CargoDemand 'C' 'C15-13' 1 $Context.RunId

Initialize-L2CargoRig $Context

# --- 1. 甲：装满 FRONT ---------------------------------------------------------------------------------------------

Publish-StagingDemand $a
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $stagingStationRiotId

$afterA = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('CARGO_HOLDING_WAIT', 'VEHICLE_FULL', 'CLOSED') `
    -Criterion 'phase-after-a'
$assertions.Add(
    'L2-SOF-01', '甲装完、FRONT 已无空仓、REAR 全空：CARGO_HOLDING_WAIT，满的一侧只有 FRONT，不是 VEHICLE_FULL',
    ([string]$afterA.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and [string]$afterA.FullSlotPositionsJson -ceq '["FRONT"]'),
    'CARGO_HOLDING_WAIT, ["FRONT"]', "$(Format-L2CargoJourney $afterA), $($afterA.FullSlotPositionsJson)")

# --- 2. 丙：REAR 装得下，追加进来，车不开往卸货站 ---------------------------------------------------------------------

Publish-StagingDemand $c
$joined = Wait-L2ConditionOrLast -Description 'demand C joined the journey' -Journal $journal -Criterion 'c-joined' -TimeoutSeconds 120 `
    -Probe { Get-Membership $c.Id } -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-SOF-02', 'REAR 装得下的丙追加进了这趟旅程（同一趟、同站新开一个停靠）',
    ($null -ne $joined -and [string]$joined.JourneyId -eq $journeyId), $journeyId,
    $(if ($null -eq $joined) { '(未加入)' } else { [string]$joined.JourneyId }))

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $stagingStationRiotId ([string]$joined.PickupStopId)
$loadedC = Wait-L2ConditionOrLast -Description 'demand C was loaded' -Journal $journal -Criterion 'c-loaded' -TimeoutSeconds 120 `
    -Probe { Get-Membership $c.Id } -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'LOADED' }
$slotsC = @(if ($null -ne $loadedC) { [string]$loadedC.TargetSlotsJson | ConvertFrom-Json })
$targetsUntilC = @(Get-DepartureTargets)
$assertions.Add(
    'L2-SOF-03', '丙装进 REAR 组（5～8）；到这时为止的离站核验只开往待送站本身（原地走到丙的停靠），没有一张开往卸货站',
    ($null -ne $loadedC -and [string]$loadedC.Status -eq 'LOADED' -and $slotsC.Count -ge 1 -and
        @($slotsC | Where-Object { $_ -lt 5 -or $_ -gt 8 }).Count -eq 0 -and
        @($targetsUntilC | Where-Object { $_ -ne $stagingStationName }).Count -eq 0),
    "LOADED in 5..8, checks only to $stagingStationName",
    "$(if ($loadedC) { $loadedC.Status } else { '(no membership)' }) slots [$($slotsC -join ',')], checks to [$($targetsUntilC -join ', ')]")

# --- 3. 之后没有等着的需求：车仍不走 ---------------------------------------------------------------------------------

$holding = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('CARGO_HOLDING_WAIT', 'VEHICLE_FULL', 'CLOSED') `
    -Criterion 'phase-after-c' -TimeoutSeconds 60
$null = Wait-L2Iterations -Riot $Context.Riot -Count 5 -Journal $journal
$after = Get-L2CargoJourney $connection $a.Id
$targetsAfter = @(Get-DepartureTargets)
$assertions.Add(
    'L2-SOF-04', '丙装完、没有等着的需求：又转五轮，装货阶段仍是 CARGO_HOLDING_WAIT，没有开往卸货站的离站核验',
    ([string]$holding.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and [string]$after.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and
        @($targetsAfter | Where-Object { $_ -ne $stagingStationName }).Count -eq 0),
    "CARGO_HOLDING_WAIT, checks only to $stagingStationName",
    "$(Format-L2CargoJourney $after), checks to [$($targetsAfter -join ', ')]")

$journal.Note('待送点 FRONT 满、REAR 还能装：装得下的丙追加进来并装上，车不开往卸货站；之后没有等着的需求，车继续等。')
