#Requires -Version 7

<#
装不下的原因不是「本车货物占着」时，这一侧不算满（批次7-07，control-server#212；ADR-cross-0059）。

一侧满的第二种判法是「上一轮有候选只因本车货物占着这一侧而装不下」。「只因」两个字是这条场景要守的：一条候选装不进这辆车
可以有很多原因，只有 SLOT_GROUP_OCCUPIED_BY_OWN_CARGO 说的是「车上的货挡着」——别的原因下车再空也装不进去，拿它们判满，
车就会因为一条永远装不进来的单提前关门，而本来能追加进来的单全被挡在外面。

需求甲 4 个花篮装满 FRONT，REAR 空着，车进入 CARGO_HOLDING_WAIT。此后三条候选都装不进这辆车，原因各不相同：
1. 需求乙：REAR 5 个花篮——比整个 REAR 组（4 个仓）还大，判 EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP（结构性的，车空着也装不下）；
2. 需求丙：FRONT 5 个花篮——同上，落在已满的那一侧，也不能把「已满」变成「满得有理由」之外的任何东西；
3. 看板暂停 WIRE_TO_GATE 之后的需求丁：REAR 1 个花篮，本来放得下，被 TASK_TYPE_HELD 挡住——暂停判据排在仓位判据之前，
   它根本走不到「本车货物占侧」那一步。

每一步之后都多转几轮再判：状态仍是 CARGO_HOLDING_WAIT，满的一侧只有 FRONT。

**第 3 步与前两步守的不是同一样东西**（红证据 red-2 量出来的）：读口改成「任何装不下都算占侧」时，前两步变红，第 3 步仍绿。
原因是暂停判据（顺序 20）排在区域归属之前，被暂停挡下的候选还没有「侧」，读口按侧归类时它根本不在任何一侧——所以第 3 步靠的
是判据顺序，不是读口的原因码过滤。它守的是「有人把暂停挪到区域归属之后」；原因码过滤本身由前两步与 L1 的
Batch7LoadingPhaseDispatchTests.OnlyOwnCargoVerdictsMarkASideAndEachSideOnce 守。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2TaskTypeHolds.psm1') -Force
. (Join-Path $PSScriptRoot 'CargoHoldingCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection

$a = New-L2CargoDemand 'A' 'N1-3' 4 $Context.RunId
$b = New-L2CargoDemand 'B' 'C15-13' 5 $Context.RunId
$c = New-L2CargoDemand 'C' 'N1-3' 5 $Context.RunId
$d = New-L2CargoDemand 'D' 'C15-13' 1 $Context.RunId

# 候选被判过、理由是 $Reason；判过之后再转五轮，读装货阶段。
function Test-StillWaiting([hashtable]$Demand, [string]$Reason, [string]$Id, [string]$Why) {
    $backlog = Wait-L2ConditionOrLast -Description "demand $($Demand.Label) was judged $Reason" -Journal $journal `
        -Criterion "reason-$($Demand.Label)" -TimeoutSeconds 60 `
        -Probe { Get-L2CargoBacklog $connection $Demand.Id } -Until { param($v) $null -ne $v -and [string]$v.ReasonCode -eq $Reason }
    $null = Wait-L2Iterations -Riot $Context.Riot -Count 5 -Journal $journal
    $row = Get-L2CargoJourney $connection $a.Id
    $joined = Get-L2CargoJourney $connection $Demand.Id
    $assertions.Add(
        $Id, $Why,
        ($null -ne $backlog -and [string]$backlog.ReasonCode -eq $Reason -and $null -eq $joined -and
            [string]$row.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and [string]$row.FullSlotPositionsJson -ceq '["FRONT"]'),
        "$Reason / not joined / CARGO_HOLDING_WAIT, full [""FRONT""]",
        "$(if ($backlog) { $backlog.ReasonCode } else { '(no backlog row)' }) / $(if ($joined) { 'joined' } else { 'not joined' }) / " +
        "$(Format-L2CargoJourney $row), full $($row.FullSlotPositionsJson)")
}

Initialize-L2CargoRig $Context

# --- 1. 需求甲装满 FRONT，车持货等单 ----------------------------------------------------------------------

Publish-L2CargoDemand $Context $a
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$null = Move-L2CargoVehicleToCurrentStop $Context ([string]$journey.JourneyId) $Context.PickupStationRiotId
$waiting = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('CARGO_HOLDING_WAIT', 'VEHICLE_FULL', 'CLOSED') `
    -Criterion 'phase-wait'
$assertions.Add(
    'L2-VFI-01', '需求甲装满 FRONT、REAR 空着：车进入 CARGO_HOLDING_WAIT，满的一侧只有 FRONT',
    ([string]$waiting.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and [string]$waiting.FullSlotPositionsJson -ceq '["FRONT"]'),
    'CARGO_HOLDING_WAIT, ["FRONT"]', "$(Format-L2CargoJourney $waiting), $($waiting.FullSlotPositionsJson)")

# --- 2. 两条超大的单：结构性装不下，不算占侧 ------------------------------------------------------------

Publish-L2CargoDemand $Context $b
Test-StillWaiting $b 'EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP' 'L2-VFI-02' `
    '需求乙（REAR 5 花篮，比整组还大）判 EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP、没有进来；又转五轮，REAR 仍不算满，车仍在持货等单'

Publish-L2CargoDemand $Context $c
Test-StillWaiting $c 'EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP' 'L2-VFI-03' `
    '需求丙（FRONT 5 花篮）同样判结构性装不下；状态与满的一侧都不变'

# --- 3. 看板暂停之后的一条小单：被暂停挡住，不算占侧 -----------------------------------------------------

$hold = Submit-L2DashboardHold -Context $Context -TaskType 'WIRE_TO_GATE' -Reason 'L2 场景：暂停之下的候选不算占侧'
$assertions.Add(
    'L2-VFI-04', '经看板暂停 WIRE_TO_GATE：提交后 303 回到看板主页',
    ($hold.StatusCode -eq 303), 303, $hold.StatusCode)

Publish-L2CargoDemand $Context $d
Test-StillWaiting $d 'TASK_TYPE_HELD' 'L2-VFI-05' `
    '暂停之后的需求丁（REAR 1 花篮，本来放得下）判 TASK_TYPE_HELD、没有进来；REAR 仍不算满，车仍在持货等单'

$journal.Note('三条装不进这辆车的候选，原因都不是「本车货物占着」：车一直在持货等单，满的一侧始终只有 FRONT。')
