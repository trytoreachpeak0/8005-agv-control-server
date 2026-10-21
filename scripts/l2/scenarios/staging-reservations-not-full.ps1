#Requires -Version 7

<#
待送点只被预留占满、车上一篮没装，不判满；预留随后取消，车不走、继续等单，并装上后来的需求（control-server#800；CP-0018，需求基线 v1.17.0）。

10-20、10-21 agv02 在 217 的形状：派车按「已装或已预留」往这趟追加需求，装货阶段原来也按同一本账判满，于是车上一篮没装就判 VEHICLE_FULL、
锁住整趟，后来的清单一装完车就自己走。CP-0018 改成判满只按已装货物、每轮重判。

待送点规则打开，合成对端先不答录入请求（没人扫码）。

  1. 需求甲（AREA N1-3 → FRONT，4 篮）受理，车开到派工待送站 305 停住；同站再来需求乙（C15-13 → REAR，4 篮），追加进这趟（同站新开一个停靠）。
     两侧都被预留占满（各 4 篮），车上 0 篮：不判满（L2-SRN-01），再转五轮仍不是 VEHICLE_FULL，也没有开往卸货站的离站核验。
  2. 操作员在车载屏上扫码前取消甲（车停在甲的停靠上）：甲终结，FRONT 的预留放开（L2-SRN-02）。
  3. 来一条需求丙（N1-3 → FRONT，1 篮）：FRONT 有空，追加进这趟（L2-SRN-03）。
  4. 对端开始答录入，车原地走到乙的停靠（丙另开了停靠就再走过去）：乙装进 REAR、丙装进 FRONT。之后车上 5 篮、FRONT 还空 3 格，没有等着的需求：装货阶段是 CARGO_HOLDING_WAIT，
     再转五轮也没有开往卸货站的离站核验（L2-SRN-04）——车继续等单，等客户出发或持货期限。

**红证据**（缺陷版本，测试提交 38f552a98 的服务端）：判满按「已装或已预留」并整趟锁住，第 1 步就是 VEHICLE_FULL（L2-SRN-01 红），
锁住之后乙、丙装完车就自己开往卸货站（L2-SRN-04 红）。

**探针不取闭包**，理由见 CargoHoldingCommon.ps1 开头。写入边界：装货阶段三列与它引出的那张车辆业务状态快照同一次保存
（CargoHoldingCommon.ps1 开头）；扫码前取消的终结与停靠结束同一次提交（load-cancelled-before-sublot）。
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
        eqp         = 'EQP-L2-SRN-01'
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

function Get-DemandStatus([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$DemandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Status
}

function Test-L2Null($value) {
    return $null -eq $value
}

function Get-PeerCancellation([string]$CancellationId) {
    $body = (Invoke-RestMethod -Uri "$($onboard.BaseUrl)/$($onboard.Prefix)/load-cancellations" -TimeoutSec 10).body
    return @($body.cancellations) | Where-Object { [string]$_.cancellationId -eq $CancellationId } | Select-Object -First 1
}

$a = New-L2CargoDemand 'A' 'N1-3' 4 $Context.RunId
$b = New-L2CargoDemand 'B' 'C15-13' 4 $Context.RunId
$c = New-L2CargoDemand 'C' 'N1-3' 1 $Context.RunId

Initialize-L2CargoRig $Context
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Silent' })

# --- 1. 甲、乙：两侧都被预留占满，车上 0 篮 ----------------------------------------------------------------------------

Publish-StagingDemand $a
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $stagingStationRiotId
$null = Wait-L2Condition -Description 'the vehicle stands at A''s stop waiting for a sublot' -Journal $journal -Criterion 'a-awaiting-sublot' `
    -TimeoutSeconds 120 -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingSublot' }

Publish-StagingDemand $b
$joinedB = Wait-L2ConditionOrLast -Description 'demand B joined the journey' -Journal $journal -Criterion 'b-joined' -TimeoutSeconds 120 `
    -Probe { Get-Membership $b.Id } -Until { param($v) $null -ne $v }
$null = Wait-L2Iterations -Riot $Context.Riot -Count 5 -Journal $journal
$reserved = Get-L2CargoJourney $connection $a.Id
$targetsReserved = @(Get-DepartureTargets)
# Invoke-L2Query 返回整张表（return , $rows）：先落变量再数，包在 @() 里数出来恒为 1（scripts/l2/README.md 讲 .Count 的那一节）。
$loadedReserved = Invoke-L2Query -Connection $connection -Sql (
    "SELECT DemandId FROM JourneyDemands WHERE JourneyId = '$journeyId' AND Status = 'LOADED' AND RemovedAt IS NULL")
$assertions.Add(
    'L2-SRN-01', '甲、乙把两侧预留占满（各 4 篮）、车上 0 篮：五轮之后装货阶段不是 VEHICLE_FULL，没有开往卸货站的离站核验',
    ($null -ne $joinedB -and [string]$joinedB.JourneyId -eq $journeyId -and $loadedReserved.Count -eq 0 -and
        $null -ne $reserved -and [string]$reserved.LoadingPhaseState -ne 'VEHICLE_FULL' -and
        @($targetsReserved | Where-Object { $_ -ne $stagingStationName }).Count -eq 0),
    'B joined, 0 loaded, not VEHICLE_FULL, no check to the gate',
    "$(if ($joinedB) { 'B joined' } else { 'B not joined' }), $($loadedReserved.Count) loaded, $(Format-L2CargoJourney $reserved), checks to [$($targetsReserved -join ', ')]")

# --- 2. 扫码前取消甲：FRONT 的预留放开 -----------------------------------------------------------------------------

$cancellationId = [guid]::NewGuid().ToString('D')
$journal.Note("Operator cancels demand A $($a.Id) before any sublot entry ($cancellationId).")
$null = $onboard.Command('Put', "load-cancellations/$cancellationId", @{
    demandId               = $a.Id
    slotOperationAttemptId = $null
    reason                 = '现场确认这一单的货不来了。'
})
$acknowledged = Wait-L2ConditionOrLast -Description 'the peer''s ALL_EMPTY result for A''s cancellation was acknowledged' `
    -Journal $journal -Criterion 'a-cancel-acknowledged' -TimeoutSeconds 90 `
    -Probe { Get-PeerCancellation $cancellationId } `
    -Until { param($v) $null -ne $v -and -not (Test-L2Null $v.resultAcknowledgedAt) }
$statusA = Wait-L2ConditionOrLast -Description 'demand A was cancelled' -Journal $journal -Criterion 'a-cancelled' -TimeoutSeconds 60 `
    -Probe { Get-DemandStatus $a.Id } -Until { param($v) $v -eq 'Cancelled' }
$assertions.Add(
    'L2-SRN-02', '操作员扫码前取消甲：授权、车报的结果被确认，甲 Cancelled（FRONT 的预留放开）',
    ($null -ne $acknowledged -and [string]$acknowledged.decision -eq 'AUTHORIZED' -and $statusA -eq 'Cancelled'),
    'AUTHORIZED, acknowledged, Cancelled',
    "$(if ($acknowledged) { "$($acknowledged.decision), ack $($acknowledged.resultAcknowledgedAt)" } else { '(no cancellation)' }), $statusA")

# --- 3. 丙：FRONT 有空，追加进来 -------------------------------------------------------------------------------------

Publish-StagingDemand $c
$joinedC = Wait-L2ConditionOrLast -Description 'demand C joined the journey' -Journal $journal -Criterion 'c-joined' -TimeoutSeconds 120 `
    -Probe { Get-Membership $c.Id } -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-SRN-03', '甲取消后 FRONT 有空，丙追加进了这一趟',
    ($null -ne $joinedC -and [string]$joinedC.JourneyId -eq $journeyId), $journeyId,
    $(if ($null -eq $joinedC) { '(未加入)' } else { [string]$joinedC.JourneyId }))

# --- 4. 开始扫码：乙、丙装上，之后车不走 ------------------------------------------------------------------------------

$null = $onboard.Command('Put', 'policy', @{ sublot = 'Auto' })
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $stagingStationRiotId ([string]$joinedB.PickupStopId)
$loadedB = Wait-L2ConditionOrLast -Description 'demand B was loaded' -Journal $journal -Criterion 'b-loaded' -TimeoutSeconds 120 `
    -Probe { Get-Membership $b.Id } -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'LOADED' }
if ($null -ne $joinedC -and [string]$joinedC.PickupStopId -ne [string]$joinedB.PickupStopId) {
    $null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $stagingStationRiotId ([string]$joinedC.PickupStopId)
}
$loadedC = Wait-L2ConditionOrLast -Description 'demand C was loaded' -Journal $journal -Criterion 'c-loaded' -TimeoutSeconds 120 `
    -Probe { Get-Membership $c.Id } -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'LOADED' }
$holding = Wait-L2LoadingPhase -Context $Context -DemandId $b.Id -States @('CARGO_HOLDING_WAIT', 'VEHICLE_FULL', 'CLOSED') `
    -Criterion 'phase-after-c' -TimeoutSeconds 60
$null = Wait-L2Iterations -Riot $Context.Riot -Count 5 -Journal $journal
$after = Get-L2CargoJourney $connection $b.Id
$targetsAfter = @(Get-DepartureTargets)
$assertions.Add(
    'L2-SRN-04', '乙、丙装上（车上 5 篮、FRONT 还空 3 格），没有等着的需求：五轮之后仍是 CARGO_HOLDING_WAIT，没有开往卸货站的离站核验',
    ($null -ne $loadedB -and [string]$loadedB.Status -eq 'LOADED' -and $null -ne $loadedC -and [string]$loadedC.Status -eq 'LOADED' -and
        $null -ne $holding -and [string]$holding.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and
        $null -ne $after -and [string]$after.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and
        @($targetsAfter | Where-Object { $_ -ne $stagingStationName }).Count -eq 0),
    "B, C LOADED, CARGO_HOLDING_WAIT, checks only to $stagingStationName",
    "B $(if ($loadedB) { $loadedB.Status } else { '-' }), C $(if ($loadedC) { $loadedC.Status } else { '-' }), $(Format-L2CargoJourney $after), checks to [$($targetsAfter -join ', ')]")

$journal.Note('待送点只被预留占满时不判满；取消一条之后追加进来的照常装上，车继续等单。')
