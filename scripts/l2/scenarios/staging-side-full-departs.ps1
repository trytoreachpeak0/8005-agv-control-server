#Requires -Version 7

<#
待送点一侧满、等着的需求都要这一侧，即走（control-server#710，program#184 R1；CP-0012、ADR-cross-0065）。

待送点规则打开。一条 STAGING_TO_WIRE 需求甲（AREA N1-3 → FRONT，4 个花篮）在派工待送站 305 装上，正好装满 FRONT 组的四个仓；
REAR 组四个仓全空。

  1. 甲装完、没有等着的需求：车不走（S 为空，等客户出发）。装货阶段是 CARGO_HOLDING_WAIT，满的一侧只记 FRONT；再转五轮仍然不发
     离站核验——「没有别的需求」不能让车自己走。
  2. 再来一条待送需求乙（同在 N1-3 → FRONT，1 个花篮）：派车轮这辆车判它 SLOT_GROUP_OCCUPIED_BY_OWN_CARGO（FRONT 只因本车的货而装不下）。
     这一轮之后车就该走：装货阶段 VEHICLE_FULL，发出开往甲卸货站的离站核验，不等离站期限与持货期限（都是十分钟），乙没有进这趟旅程。

「一侧满即走」只在「等着的都要这一侧」时成立，另一半（另一侧还能装时不走）是 staging-other-side-fits-holds。机台取货在同样的事实下不变，
由 cargo-holding-side-full（规则关）证明；规则开着时的机台回归在单测（StagingDepartureFullOrNoFitTests 的三种停靠）。

**红证据**（缺陷版本）：把待送点的判定改回「两侧都满」，第 2 步车停在 CARGO_HOLDING_WAIT，L2-SSF-04、05 变红。

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
        eqp         = 'EQP-L2-SSF-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4 * $Demand.Baskets
        workType    = 'STAGING_TO_WIRE'
    })
}

# 发给车的离站核验，按入队先后：每一张开往哪一站、什么时候排的。
function Get-DepartureChecks {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT PayloadJson, CreatedAt FROM ProtocolOutbox WHERE MessageType = 'PreDepartureSafetyCheck' ORDER BY CreatedAt, MessageId"
    return @(foreach ($row in $rows) {
            [pscustomobject]@{
                Target    = [string]([string]$row.PayloadJson | ConvertFrom-Json -Depth 20).payload.targetStationId
                CreatedAt = [DateTimeOffset]::Parse([string]$row.CreatedAt, [Globalization.CultureInfo]::InvariantCulture)
            }
        })
}

$a = New-L2CargoDemand 'A' 'N1-3' 4 $Context.RunId
$b = New-L2CargoDemand 'B' 'N1-3' 1 $Context.RunId

Initialize-L2CargoRig $Context

# --- 1. 甲：装满 FRONT，没有等着的需求，车不走 ----------------------------------------------------------------------

Publish-StagingDemand $a
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $stagingStationRiotId

$afterA = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('CARGO_HOLDING_WAIT', 'VEHICLE_FULL', 'CLOSED') `
    -Criterion 'phase-after-a'
$assertions.Add(
    'L2-SSF-01', '甲装完、FRONT 已无空仓、REAR 全空，没有等着的需求：CARGO_HOLDING_WAIT，满的一侧只有 FRONT',
    ([string]$afterA.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and [string]$afterA.FullSlotPositionsJson -ceq '["FRONT"]'),
    'CARGO_HOLDING_WAIT, ["FRONT"]', "$(Format-L2CargoJourney $afterA), $($afterA.FullSlotPositionsJson)")

$null = Wait-L2Iterations -Riot $Context.Riot -Count 5 -Journal $journal
$stillWaiting = Get-L2CargoJourney $connection $a.Id
$checksWhileAlone = @(Get-DepartureChecks)
$assertions.Add(
    'L2-SSF-02', '又转了五轮，仍没有等着的需求：车不自己走（不发离站核验），装货阶段仍是 CARGO_HOLDING_WAIT',
    ([string]$stillWaiting.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and $checksWhileAlone.Count -eq 0),
    'CARGO_HOLDING_WAIT, 0 checks', "$(Format-L2CargoJourney $stillWaiting), $($checksWhileAlone.Count) checks")

# --- 2. 乙：只因本车货占着 FRONT 而装不下 → 即走 ------------------------------------------------------------------

$publishedAt = [DateTimeOffset]::UtcNow
Publish-StagingDemand $b
$backlogB = Wait-L2ConditionOrLast -Description "demand B's backlog reason is SLOT_GROUP_OCCUPIED_BY_OWN_CARGO" -Journal $journal `
    -Criterion 'b-own-cargo' -TimeoutSeconds 90 `
    -Probe { Get-L2CargoBacklog $connection $b.Id } -Until { param($v) $null -ne $v -and [string]$v.ReasonCode -eq 'SLOT_GROUP_OCCUPIED_BY_OWN_CARGO' }
$assertions.Add(
    'L2-SSF-03', '派车轮判乙 SLOT_GROUP_OCCUPIED_BY_OWN_CARGO（FRONT 只因本车的货装不下）',
    ($null -ne $backlogB -and [string]$backlogB.ReasonCode -eq 'SLOT_GROUP_OCCUPIED_BY_OWN_CARGO'),
    'SLOT_GROUP_OCCUPIED_BY_OWN_CARGO', $(if ($backlogB) { [string]$backlogB.ReasonCode } else { '(no backlog row)' }))

$full = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('VEHICLE_FULL', 'CLOSED') -Criterion 'phase-full' -TimeoutSeconds 90
$assertions.Add(
    'L2-SSF-04', '等着的需求全要 FRONT 且装不下：装货阶段 VEHICLE_FULL（REAR 还空着）',
    ([string]$full.LoadingPhaseState -in @('VEHICLE_FULL', 'CLOSED') -and
        ([string]$full.LoadingPhaseState -ne 'CLOSED' -or [string]$full.LoadingClosedReason -eq 'VEHICLE_FULL')),
    'VEHICLE_FULL', "$(Format-L2CargoJourney $full), $($full.FullSlotPositionsJson)")

$check = Wait-L2ConditionOrLast -Description 'a departure check for the drop-off was queued' -Journal $journal `
    -Criterion 'departure-check' -TimeoutSeconds 60 `
    -Probe { @(Get-DepartureChecks | Where-Object { $_.Target -ne $stagingStationName }) | Select-Object -First 1 } `
    -Until { param($v) $null -ne $v }
$waited = if ($null -eq $check) { $null } else { ($check.CreatedAt - $publishedAt).TotalSeconds }
$assertions.Add(
    'L2-SSF-05', '判满之后立即发出开往卸货站的离站核验：乙发布后一分钟之内，远早于十分钟的离站期限与持货期限',
    ($null -ne $check -and $waited -lt 60),
    'check < 60 s after B', $(if ($null -eq $check) { '(no departure check)' } else { "check to $($check.Target) $([math]::Round($waited, 1)) s after B" }))

$bJourney = Get-L2CargoJourney $connection $b.Id
$assertions.Add(
    'L2-SSF-06', '乙没有进这趟旅程',
    ($null -eq $bJourney), 'no journey', $(if ($bJourney) { "journey $($bJourney.JourneyId)" } else { 'no journey' }))

$journal.Note('待送点 FRONT 满、等着的乙也要 FRONT：车判 VEHICLE_FULL 即走；没有等着的需求时车不自己走。')
