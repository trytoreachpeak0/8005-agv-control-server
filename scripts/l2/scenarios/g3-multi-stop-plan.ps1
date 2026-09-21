#Requires -Version 7

<#
G3 `FP-IS-08`：多停靠计划（批次 7，control-server#218）。协议向量 `CV-MULTI-STOP-PLAN-NINE-LEGS`（`protocol-v2.0.0`）。

两端的半边：服务端 control-server#211（多停靠计划与途中追加），车载端 onboard-hmi#134（放开清单与腿数上限、计划腿列表）。
合成对端上同一条链路停在「计划已重发」（`multi-stop-append-same-zone`），这里换成真车载端 WPF 与真模拟器，并且**走完**：
两条需求各装一次、各卸一次，旅程 `Completed`。

**形状**：一辆车、两条需求、两个取货站、一个关卡。车先停在关卡上；需求甲（`N1-3`，12 号站 `N1-3_N1-7`）被这辆空闲车接走，
计划两条腿；车到 12 号站装完甲、停在站上持货等单时发需求乙（`C15-13`，11 号站），它追加进同一趟：取货新开一个停靠排在 12 号站
之后，卸货并进关卡那个停靠，计划变成三条腿、整体重发。然后车到 11 号站装乙，到关卡卸两条。追加不能在车开往 12 号站的路上发：
真车载端那时报「是否停车未知」，会话不就绪，服务端不对它追加（下文第 2 节）。

**站点刻意这样挑**：追加后的计划是 `1:N1-3_N1-7`、`2:C15-13`、`3:关卡`，而按站名排是 `C15-13`、`N1-3_N1-7`、`关卡`——
车载端要是按站点在本地重排，行序就变成 2,1,3，`NEVER_REORDER_LEGS_LOCALLY` 的判据才有东西可判。两个取货站按站名顺序
排的话，重排与不重排看起来一样，判据白绿。

判据对着向量的产品断言写：
- 服务端 `CATEGORISE_EVERY_STOP_PURPOSE`、序位从 1 连续（G3-08-01，每一版计划）；`PLAN_UP_TO_NINE_LEGS` 的 G3 面：追加后的
  计划修订号前进、腿数不少于 3、不多于 9（G3-08-02）；`ORDER_LEGS_BY_SEQUENCE`：线上 `legs` 数组的先后就是 `sequence`
  的先后，读发件箱原文、不排序（G3-08-03）；消息序列与向量一致、每份快照都被确认（G3-08-04）。
- 车载端 `DISPLAY_FULL_JOURNEY_PLAN`、`NEVER_REORDER_LEGS_LOCALLY`：经 UIA 读计划腿列表 `JourneyPlanLegs`，追加前、追加后
  各一次，行数与行序都等于服务端那一版的 `sequence`（G3-08-05、G3-08-06）。**读之前先等车载端确认那一版计划**
  （`scripts/l2/README.md` 第 14 条），再等视图跟上——确认是业务层的事，界面在它之后渲染。
  行序读的是每行第一个 TextBlock（序位），不是行上的 `ItemStatus`：那个值挂在模板里的 Grid 上，Grid 不进 UIA 树，
  读不到（`L2MultiStopJourney.psm1` 头注释，docs/defects/20260922-journey-plan-legs-item-status-not-in-uia-tree.md）。
- 终态（G3-08-07）：两条需求各一次装、一次卸，全部 `Committed`，需求 `Succeeded`，旅程 `Completed`，只有一行旅程。

换序与删除（control-server#215）不在本场景；九条腿由两端 G2 证（docs/g3-slice-claim-review.md）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2MultiStopJourney.psm1') -Force
. (Join-Path $PSScriptRoot 'MultiStopRigCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$onboard = $Context.Onboard
$simulator = $Context.Simulator

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: its onboard assertions read the shipped HMI through UI Automation.'
}

# 与默认站表同一份事实（Invoke-L2Scenario.ps1 的 FakeRiot 种子）。
$firstStationRiotId = $Context.PickupStationRiotId
$secondStationRiotId = 11
$gateRiotId = $Context.GateStationRiotId

$a = New-L2CargoDemand 'A' 'N1-3' 1 $Context.RunId
$b = New-L2CargoDemand 'B' 'C15-13' 1 $Context.RunId
$a.Sublot = "G3-08-A-$($Context.RunId)"
$b.Sublot = "G3-08-B-$($Context.RunId)"
$demandIds = @($a.Id, $b.Id)

function Get-Plans {
    return , @((Get-L2JourneyWireSnapshots $connection $demandIds) | Where-Object { $_.Type -eq 'UpcomingStopPlanSnapshot' })
}

# 车载端计划列表的行序（序位）是否等于 $plan 的序位顺序，且行数相同。
function Test-RowsShowPlan([object]$rows, [object]$plan) {
    if ($null -eq $rows -or $null -eq $plan) { return $false }
    $shown = @(@($rows) | ForEach-Object { $_.Sequence })
    $expected = @(@($plan.Legs) | ForEach-Object { [int]$_.sequence })
    return ($shown -join ',') -eq ($expected -join ',')
}

# --- 1. 甲被空闲车接走，计划两条腿 --------------------------------------------------------------------------------

Initialize-L2CargoRig $Context
Publish-L2CargoDemand $Context $a
$journey = Wait-L2Condition -Description 'demand A was accepted and the vehicle set off' -Journal $journal -Criterion 'journey-a' `
    -TimeoutSeconds 120 -Probe { Get-L2CargoJourney $connection $a.Id } `
    -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
$journal.Note("Journey $journeyId is under way to station $firstStationRiotId.")

# 第二事实：派车那一版计划被车载端确认。确认与计划入发件箱不是同一次写入。
$dispatchPlan = Wait-L2ConditionOrLast -Description 'the onboard acknowledged the dispatch plan' -Journal $journal `
    -Criterion 'dispatch-plan-acknowledged' -TimeoutSeconds 60 `
    -Probe { @((Get-Plans) | Where-Object { $_.Acknowledged }) | Select-Object -Last 1 } `
    -Until { param($v) $null -ne $v }
# 第三事实：界面跟上了那一版。超时带最后读值交给判据。
$rowsBefore = Wait-L2ConditionOrLast -Description 'the HMI shows the dispatch plan' -Journal $journal `
    -Criterion 'plan-rows-before-append' -TimeoutSeconds 30 `
    -Probe { Get-L2PlanLegRows $onboard } -Until { param($v) Test-RowsShowPlan $v $dispatchPlan }
$journal.Note("Plan rows before the append: $(Format-L2PlanLegRows $rowsBefore); server $(if ($dispatchPlan) { Format-L2WireSnapshot $dispatchPlan } else { '(no acknowledged plan)' })")
$assertions.Add(
    'G3-08-05',
    '追加之前：车载端确认了派车那一版计划之后，计划腿列表的行数与行序等于服务端那一版的 sequence（DISPLAY_FULL_JOURNEY_PLAN）',
    ($null -ne $dispatchPlan -and @($dispatchPlan.Legs).Count -eq 2 -and (Test-RowsShowPlan $rowsBefore $dispatchPlan)),
    "两条腿，行序 $(if ($dispatchPlan) { (@($dispatchPlan.Legs) | ForEach-Object { $_.sequence }) -join ',' } else { '(无计划)' })",
    "服务端 $(if ($dispatchPlan) { Format-L2WireSnapshot $dispatchPlan } else { '(无已确认计划)' }) / 界面 $(Format-L2PlanLegRows $rowsBefore)")

# --- 2. 车到 12 号站装甲，停在站上时乙追加进同一趟，计划变三条腿 ------------------------------------------------------

# 追加只能在车停在站上时发生：真车载端在车有未结束的 RIoT 单时报「是否停车未知」（RIOT_NONFINAL_ORDER_PRESENT），
# 会话 RecoveryRequired，在途判据以 ONBOARD_FACTS_NOT_READY 拒绝追加（real-onboard-mixed-side-one-stop 第一次跑实测）。
# 甲装完之后车在 12 号站持货等单（setup 里 90 秒），乙在这段时间里追加：12 号站是当前下一站，乙的取货新开一个停靠排在它后面。
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $firstStationRiotId
$loadA = Invoke-L2RigLoad $Context $journeyId $a
Publish-L2CargoDemand $Context $b
$appendedPlan = Wait-L2ConditionOrLast -Description 'the onboard acknowledged a plan of three or more legs' -Journal $journal `
    -Criterion 'appended-plan-acknowledged' -TimeoutSeconds 120 `
    -Probe { @((Get-Plans) | Where-Object { $_.Acknowledged -and @($_.Legs).Count -ge 3 }) | Select-Object -First 1 } `
    -Until { param($v) $null -ne $v }
if ($null -eq $appendedPlan) {
    # 没有三条腿的计划被确认：后面按停靠开车会停在 11 号站等不到停靠。先把已经读得到的判据落表再停，
    # 而不是让驱动的超时顶替「计划没被追加／没被确认」这个结论。
    $plans = Get-Plans
    $journal.Note("Plans so far: $((@($plans) | ForEach-Object { "$(Format-L2WireSnapshot $_) ack=$($_.Acknowledged)" }) -join ' | ')")
    Add-L2RealNotReached $assertions @('G3-08-01', 'G3-08-02', 'G3-08-03', 'G3-08-04', 'G3-08-06', 'G3-08-07') `
        '没有一版三条腿以上的计划被车载端确认（追加没发生、没重发，或车载端没应用）'
    return
}
$rowsAfter = Wait-L2ConditionOrLast -Description 'the HMI shows the appended plan' -Journal $journal `
    -Criterion 'plan-rows-after-append' -TimeoutSeconds 30 `
    -Probe { Get-L2PlanLegRows $onboard } -Until { param($v) Test-RowsShowPlan $v $appendedPlan }
$journal.Note("Plan rows after the append: $(Format-L2PlanLegRows $rowsAfter); server $(Format-L2WireSnapshot $appendedPlan)")
$assertions.Add(
    'G3-08-06',
    '追加之后：车载端确认了三条腿那一版计划之后，计划腿列表的行数与行序等于服务端那一版的 sequence；站点刻意挑成按站名排会是 2,1,3，本地重排就红（DISPLAY_FULL_JOURNEY_PLAN、NEVER_REORDER_LEGS_LOCALLY）',
    (Test-RowsShowPlan $rowsAfter $appendedPlan),
    "行序 $((@($appendedPlan.Legs) | ForEach-Object { $_.sequence }) -join ',')",
    "服务端 $(Format-L2WireSnapshot $appendedPlan) / 界面 $(Format-L2PlanLegRows $rowsAfter)")

# --- 3. 走完：11 号站装乙、关卡卸两条 ------------------------------------------------------------------------------

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $secondStationRiotId
$loadB = Invoke-L2RigLoad $Context $journeyId $b
# 11 号站是最后一个装货停靠：车在这里持货等单到期限才走（setup 里 90 秒），所以这一步的等待给足。
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $gateRiotId
$unloads = [System.Collections.Generic.List[object]]::new()
foreach ($i in 1..2) {
    $unloads.Add((Invoke-L2RigUnloadNext $Context $journeyId @($unloads | ForEach-Object { $_.AttemptId })))
}
$stage = Wait-L2ConditionOrLast -Description 'the journey completed' -Journal $journal -Criterion 'journey-completed' `
    -TimeoutSeconds 120 -Probe { Get-L2JourneyStage $connection $journeyId } -Until { param($v) $v -eq 'Completed' }

# 第二事实：最后一份清单的确认与「旅程 Completed」不是同一次提交（control-server#203 条目 3）。
$snapshots = Wait-L2ConditionOrLast -Description 'every plan and worklist snapshot of this journey was acknowledged' `
    -Journal $journal -Criterion 'journey-snapshots-acknowledged' -TimeoutSeconds 30 `
    -Probe { Get-L2JourneyWireSnapshots $connection $demandIds } `
    -Until { param($v) @($v).Count -ge 2 -and @(@($v) | Where-Object { $_.Fenced -or -not $_.Acknowledged }).Count -eq 0 }
$snapshots = @($snapshots)
$described = (@($snapshots | ForEach-Object { "$(Format-L2WireSnapshot $_) ack=$($_.Acknowledged) fenced=$($_.Fenced)" }) -join ' | ')
$journal.Observe('journey-wire-snapshots', $described, $null)
$plans = @($snapshots | Where-Object { $_.Type -eq 'UpcomingStopPlanSnapshot' })

# --- 4. 服务端的计划 ---------------------------------------------------------------------------------------------

$badPlans = @($plans | Where-Object {
        $seq = @($_.Legs | ForEach-Object { [int]$_.sequence })
        $expected = @(if ($seq.Count -gt 0) { 1..$seq.Count })
        ($seq -join ',') -ne ($expected -join ',') -or
            @($_.Legs | Where-Object { -not (Test-L2RealPresent $_.stopPurposeCategory) }).Count -gt 0
    })
$assertions.Add(
    'G3-08-01',
    '这趟旅程的每一版计划：腿的 sequence 从 1 起连续、无重复，每条腿都有 stopPurposeCategory（CATEGORISE_EVERY_STOP_PURPOSE）',
    ($plans.Count -ge 2 -and $badPlans.Count -eq 0),
    '至少两版计划，全部 1..n 连续且每腿有用途类别',
    "$($plans.Count) 版计划，不合格 $($badPlans.Count) 版$(if ($badPlans) { '：' + ((@($badPlans) | ForEach-Object { Format-L2WireSnapshot $_ }) -join ' | ') })")

$beforeAppend = @($plans | Where-Object { $_.At -lt $appendedPlan.At })
$maxBefore = if ($beforeAppend.Count -gt 0) { (@($beforeAppend | ForEach-Object { $_.Revision }) | Measure-Object -Maximum).Maximum } else { $null }
$members = Get-L2JourneyMembers $connection $journeyId
$runtimeRowsRead = Invoke-L2Query -Connection $connection -Sql 'SELECT COUNT(*) AS N FROM JourneyRuntimes'
$runtimeRows = [int]$runtimeRowsRead[0].N
$legCount = @($appendedPlan.Legs).Count
$assertions.Add(
    'G3-08-02',
    '追加之后的计划修订号大于此前每一版，腿数不少于 3、不多于 9；乙进的是同一趟旅程（归属两条、旅程行一行）（PLAN_UP_TO_NINE_LEGS 的 G3 面，九条由两端 G2 证）',
    ($null -ne $maxBefore -and $appendedPlan.Revision -gt $maxBefore -and $legCount -ge 3 -and $legCount -le 9 -and
        $members.Count -eq 2 -and $runtimeRows -eq 1),
    "修订号 > 此前最大 / 3..9 条腿 / 归属 2 / 旅程行 1",
    "r$($appendedPlan.Revision) 对此前最大 r$maxBefore / $legCount 条腿 / 归属 $($members.Count) / 旅程行 $runtimeRows")

$unsorted = @($plans | Where-Object {
        $wire = @($_.WireSequences | ForEach-Object { [int]$_ })
        ($wire -join ',') -ne (@($wire | Sort-Object) -join ',')
    })
$assertions.Add(
    'G3-08-03',
    '线上计划按 sequence 下发：每一版 UpcomingStopPlanSnapshot 的 legs 数组，原文的先后就是 sequence 从小到大（读发件箱原文，不排序）（ORDER_LEGS_BY_SEQUENCE）',
    ($plans.Count -ge 2 -and $unsorted.Count -eq 0),
    '每一版原文升序',
    "$($plans.Count) 版，乱序 $($unsorted.Count) 版$(if ($unsorted) { '：' + ((@($unsorted) | ForEach-Object { Format-L2WireSnapshot $_ }) -join ' | ') })")

$firstWorklist = @($snapshots | Where-Object { $_.Type -eq 'CurrentStopWorklistSnapshot' }) | Select-Object -First 1
$unacked = @($snapshots | Where-Object { $_.Fenced -or -not $_.Acknowledged })
$assertions.Add(
    'G3-08-04',
    '消息顺序与向量一致：这趟旅程第一份快照是计划，清单在它之后；每一份计划与清单都被真车载端确认（SnapshotAppliedAck），没有一份被作废',
    ($snapshots.Count -ge 2 -and $snapshots[0].Type -eq 'UpcomingStopPlanSnapshot' -and $null -ne $firstWorklist -and
        $firstWorklist.At -ge $snapshots[0].At -and $unacked.Count -eq 0),
    '计划在前 / 全部确认、无作废',
    $described)

# --- 5. 终态 ---------------------------------------------------------------------------------------------------

$perDemand = foreach ($demand in @($a, $b)) {
    $ops = Invoke-L2Query -Connection $connection -Sql (
        "SELECT OperationType, Status FROM StationOperations WHERE DemandId = '$($demand.Id)'")
    $status = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($demand.Id)'"
    $loadsCommitted = @($ops | Where-Object { [string]$_.OperationType -eq 'Load' -and [string]$_.Status -eq 'Committed' }).Count
    $unloadsCommitted = @($ops | Where-Object { [string]$_.OperationType -eq 'Unload' -and [string]$_.Status -eq 'Committed' }).Count
    "$($demand.Label):$status/ops $($ops.Count)/load $loadsCommitted/unload $unloadsCommitted"
}
$expectedPerDemand = @('A:Succeeded/ops 2/load 1/unload 1', 'B:Succeeded/ops 2/load 1/unload 1')
$slotReadings = @(@($loadA.OpenedSlot, $loadB.OpenedSlot) | ForEach-Object { "$_=$(Get-L2RealSlotReading $simulator $_)" })
$slotsClean = @($slotReadings | Where-Object { $_ -notmatch '=CLOSED/EMPTY/1/0$' }).Count -eq 0
$unloadedDemands = @($unloads | ForEach-Object { $_.DemandId } | Sort-Object)
$assertions.Add(
    'G3-08-07',
    '旅程走完：两条需求各一次装、一次卸、都 Committed，需求 Succeeded，旅程 Completed；关卡上两笔卸货各属一条需求；装过的两个仓最后关门、空、锁上、开锁输出复位（NO_DUPLICATE_COMMIT）',
    ($stage -eq 'Completed' -and (@($perDemand) -join ' ') -eq ($expectedPerDemand -join ' ') -and
        ($unloadedDemands -join ',') -eq ((@($a.Id, $b.Id) | Sort-Object) -join ',') -and $slotsClean),
    "Completed / $($expectedPerDemand -join ' ') / 两笔卸货两条需求 / 仓 CLOSED/EMPTY/1/0",
    "$stage / $(@($perDemand) -join ' ') / 卸货需求 $($unloads.Count) 笔 / $($slotReadings -join ' ')")

$journal.Note("FP-IS-08: plan grew from $(@($dispatchPlan.Legs).Count) to $legCount legs on an en-route append, the HMI showed both in sequence order, and the journey ran to completion.")
