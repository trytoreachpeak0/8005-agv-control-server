#Requires -Version 7

<#
混挂站点一次停靠、前后两侧各一条需求（批次7-15，control-server#218；规格第 8.3 节批次 7 行的真装置 L2，卸货按第 20 节补记；
REQ-0189、REQ-0196、REQ-0354、REQ-0357，ADR-cross-0061）。真车载端 WPF + 真 slots-simulator，不进任何 G3 runner、不认领切片。

**为什么是三条需求而不是两条。**同一次停靠上要有两条需求，第二条只能并进一个既有停靠，而服务端规定「同站就并入，唯独当前
下一站不并」（EnRouteAppendPlanner：并进当前下一站等于改它，REQ-0196 禁止）。初始派车那一条的取货停靠从一开始就是当前下一站，
永远并不进去；车在最后装货站持货等单时同站来的需求也另开停靠。所以要一条需求先把车引向别处：
- 需求甲（`C15-13`，11 号站，前侧）被停在关卡上的空闲车接走，11 号站是当前下一站；
- 车停在 11 号站、甲装完之后先追加丙（`N2-5`，后侧）、再追加乙（`N1-3`，前侧），两者的取货都在 12 号站 `N1-3_N2-5`：丙新开
  12 号站的停靠，乙并进丙那一个——12 号站此刻不是当前下一站，可以并。三条的卸货都并进关卡那一个停靠。追加只在停站时发生（用户 09-22 决定，
  control-server#286、program#133）。

**服务端怎样逐条做**（MultiStopRigCommon.ps1 头注释）：12 号站到站发一条录入请求、列出乙丙两个子批，操作员扫哪一条就装哪一条，
装完一条才发下一版清单与录入请求。所以跨需求的装货先后就是扫码先后——场景先扫乙（前侧）。卸货一次只下一条。
**后侧的丙先追加。**cs#303（fp/v2-impl@c4b04bb2）起卸货按侧下发、前侧先于后侧。场景写于 cs#303 之前时让前侧的乙先追加，
「先前后后」靠加入顺序也成立，卸货那一半的绿证不了服务端按侧排。出口票 control-server#220 把追加顺序倒过来：按加入先后卸货会先开
丙（后侧）那一扇，所以 L2-MSO-10 现在只有按侧排序才绿（docs/defects/20260922-cross-demand-side-order-follows-arrival.md）。
装货仍先扫乙：跨需求的装货先后由操作员扫码顺序决定，服务端不改。

判据（`L2-MSO-*`）：
- 本站清单曾同时有两条清单项：服务端发出并被确认的一版 12 号站清单是乙丙两条（01、02），车载端 UIA 的清单列表读到这两行（03）。
- 装货各开各组、先前后后、第二条命令在第一条闭环之后才开锁：服务端库里丙的装货命令建在乙的装货提交之后；模拟器每 100 ms 采样里，
  丙那一仓第一次未锁闭晚于乙那一仓最后一次未锁闭（04）；乙的目标仓在前侧组、丙的在后侧组（05）。**04 的装货这一半由驱动顺序保证**：
  场景等乙的装货 Committed、新一版清单被确认之后才扫丙，而服务端扫码之后才建命令，所以它必然成立，证不了车载端执行器是串行的；
  有判别力的是卸货那一半（10、11）。
- 装完时模拟器每个仓的物理状态与三条需求的 `TargetSlotsJson` 一致（06）。
- 持货等单确实发生过：12 号站装完后装货阶段进入 `CARGO_HOLDING_WAIT`、发给车的快照里有它，车载端 UIA 读到持货倒计时 `ACTIVE`（07）；
  收尾原因属于 {`CARGO_HOLDING_TIMEOUT`, `VEHICLE_FULL`}，明确不是让站 `WAITING_STATION_YIELD`（08）——车队只有一辆车，让站不该发生，
  发生了就是别的东西在承诺这个站。
- 卸货：关卡清单上乙标前侧、丙标后侧（09，onboard-hmi#134 的 `WorklistItemSide`）；一次一扇、前侧的都卸在后侧之前，丙的卸货命令
  建在乙的卸货提交之后、采样里丙那一仓在乙那一仓锁回之后才开（10）。
- 全程一次一扇：从车到 11 号站起到旅程走完，采样里任一时刻至多一仓未锁闭，并且真的采到了六次开门涉及的三个仓（11，照
  `real-onboard-cancellation-authorization-lost` 的 `L2-CAL-10`）。
- 终态：三条需求各一次装、一次卸、都 `Committed`、`Succeeded`，旅程 `Completed`，八个仓都关门、空、锁上、复位，会话回 `Ready`（12）。

每个第二事实都先等（README 第 14 条）：读 UIA 清单前等那一版清单被确认；读模拟器前等那几笔装卸提交；读持货状态等装货阶段列变。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SlotGroups.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2MultiStopJourney.psm1') -Force
. (Join-Path $PSScriptRoot 'MultiStopRigCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$onboard = $Context.Onboard
$simulator = $Context.Simulator

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: it reads the shipped HMI through UI Automation and the real simulator.'
}

# 与 setup.psd1 的 Stations、AreaAssignments 同一份事实。
$firstStationRiotId = 11
$mixedStationRiotId = $Context.PickupStationRiotId
$mixedStationName = 'N1-3_N2-5'
$gateRiotId = $Context.GateStationRiotId
$gateName = '关卡'

$a = New-L2CargoDemand 'A' 'C15-13' 1 $Context.RunId
$b = New-L2CargoDemand 'B' 'N1-3' 1 $Context.RunId
$c = New-L2CargoDemand 'C' 'N2-5' 1 $Context.RunId
$a.Sublot = "L2-MSO-A-$($Context.RunId)"
$b.Sublot = "L2-MSO-B-$($Context.RunId)"
$c.Sublot = "L2-MSO-C-$($Context.RunId)"
$demandIds = @($a.Id, $b.Id, $c.Id)
$sideOf = @{ $a.Id = 'FRONT'; $b.Id = 'FRONT'; $c.Id = 'REAR' }
$allIds = @('L2-MSO-01', 'L2-MSO-02', 'L2-MSO-03', 'L2-MSO-04', 'L2-MSO-05', 'L2-MSO-06', 'L2-MSO-07', 'L2-MSO-08',
    'L2-MSO-09', 'L2-MSO-10', 'L2-MSO-11', 'L2-MSO-12')

function Get-AcknowledgedWorklists([string]$stationName) {
    return , @((Get-L2JourneyWireSnapshots $connection $demandIds) | Where-Object {
            $_.Type -eq 'CurrentStopWorklistSnapshot' -and $_.Acknowledged -and $_.StationId -eq $stationName })
}

function Get-SublotSet([object]$items) { return ((@($items) | ForEach-Object { [string]$_.sublot } | Sort-Object) -join ',') }

# 采样里某一仓在 [$from, $to) 之间第一次与最后一次未锁闭的下标（在 $transitions 里的位置），没见过为 -1。
function Get-SlotSpan([object[]]$transitions, [int]$slot, [DateTimeOffset]$from, [DateTimeOffset]$to) {
    $first = -1; $last = -1
    for ($i = 0; $i -lt $transitions.Count; $i++) {
        $at = [DateTimeOffset]::Parse($transitions[$i].At, [Globalization.CultureInfo]::InvariantCulture)
        if ($at -lt $from -or $at -ge $to) { continue }
        if (@($transitions[$i].Slots) -contains $slot) {
            if ($first -lt 0) { $first = $i }
            $last = $i
        }
    }
    return [pscustomobject]@{ First = $first; Last = $last }
}

$positions = Get-L2VehicleSlotPositions -Connection $connection -AgvId $Context.AgvId
if ($null -eq $positions) { throw "The slot grouping of $($Context.AgvId) is unresolved; every side criterion reads it." }
function Get-Group([int]$slot) { if ($positions.Positions.ContainsKey($slot)) { return $positions.Positions[$slot] } else { return '(unknown)' } }

# --- 1. 甲被空闲车接走，车到 11 号站装甲 -----------------------------------------------------------------------------

Initialize-L2CargoRig $Context
Assert-L2RigBaselineSlots $Context 3
Publish-L2CargoDemand $Context $a
$journey = Wait-L2Condition -Description 'demand A was accepted and the vehicle set off' -Journal $journal -Criterion 'journey-a' `
    -TimeoutSeconds 120 -Probe { Get-L2CargoJourney $connection $a.Id } `
    -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId

$sampler = Start-L2DoorSampler $simulator 25
$samplerStopped = $false
try {
    $null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $firstStationRiotId
    $loadA = Invoke-L2RigLoad $Context $journeyId $a

    # --- 2. 车停在 11 号站时追加乙丙，并成 12 号站的同一个停靠 ---------------------------------------------------------

    # 追加只能在车停在站上时发生，不能在车开往 11 号站的路上：真车载端在车有未结束的 RIoT 单时报「是否停车未知」
    # （OnboardAlarmSnapshot ONBOARD_DEPARTURE_SAFETY_SIGNAL_UNAVAILABLE / RIOT_NONFINAL_ORDER_PRESENT，SafetyStateChanged
    # departureSafe=false、unknownPresent=true），会话因此 RecoveryRequired，在途判据读不到车载端事实，以
    # ONBOARD_FACTS_NOT_READY 拒绝追加。这是预期行为：按用户 09-22 决定，途中追加只在停站时，不包括行驶中（control-server#286、
    # program#133）。本场景第一遍（证据 mso-001）按行驶中追加写，乙丙积压两分钟未进旅程；合成车载端恒报安全，看不到这一点。
    # 所以等甲装完、车停在 11 号站的修正窗口里再发：11 号站此刻是当前下一站（不能并），12 号站是新开的、可以并。
    # 后侧的丙先追加、前侧的乙后追加（control-server#220）：卸货若按加入先后，关卡上会先开丙那一扇，L2-MSO-10 靠这一点才有判别力。
    Publish-L2CargoDemand $Context $c
    $null = Wait-L2ConditionOrLast -Description 'demand C joined the journey' -Journal $journal -Criterion 'members-2' -TimeoutSeconds 45 `
        -Probe { (Get-L2JourneyMembers $connection $journeyId).Count } -Until { param($v) $v -ge 2 }
    Publish-L2CargoDemand $Context $b
    $members = Wait-L2ConditionOrLast -Description 'demand B joined the journey' -Journal $journal -Criterion 'members-3' -TimeoutSeconds 45 `
        -Probe { Get-L2JourneyMembers $connection $journeyId } -Until { param($v) @($v).Count -ge 3 }
    $members = @($members)
    $stops = Get-L2JourneyStops $connection $journeyId
    $stopShape = (@($stops) | ForEach-Object { "$($_.Sequence):$($_.StopRole)@$($_.StationRiotId)" }) -join ' '
    $memberB = @($members | Where-Object { [string]$_.DemandId -eq $b.Id }) | Select-Object -First 1
    $memberC = @($members | Where-Object { [string]$_.DemandId -eq $c.Id }) | Select-Object -First 1
    $sharedStop = if ($null -ne $memberB -and $null -ne $memberC -and [string]$memberB.PickupStopId -eq [string]$memberC.PickupStopId) {
        @($stops | Where-Object { [string]$_.StopId -eq [string]$memberB.PickupStopId }) | Select-Object -First 1
    } else { $null }
    $expectedShape = "1:PICKUP@$firstStationRiotId 2:PICKUP@$mixedStationRiotId 3:UNLOAD@$gateRiotId"
    $assertions.Add(
        'L2-MSO-01',
        '前置：乙丙都进了甲那一趟旅程，取货并成 12 号站的同一个停靠（归属三条、停靠 11→12→关卡）',
        ($members.Count -eq 3 -and $null -ne $sharedStop -and [int]$sharedStop.StationRiotId -eq $mixedStationRiotId -and
            $stopShape -eq $expectedShape),
        "归属 3 / 乙丙同一个停靠 @$mixedStationRiotId / $expectedShape",
        "归属 $($members.Count) / 乙丙$(if ($null -ne $sharedStop) { "同一个停靠 @$($sharedStop.StationRiotId)" } else { '不在同一个停靠' }) / $stopShape")
    if ($null -eq $sharedStop) {
        $backlog = Invoke-L2Query -Connection $connection -Sql 'SELECT DemandId, ReasonCode FROM JourneyBacklog'
        $journal.Note("Backlog: $((@($backlog) | ForEach-Object { "$($_.DemandId)=$($_.ReasonCode)" }) -join '; ')")
        Add-L2RealNotReached $assertions @($allIds | Where-Object { $_ -ne 'L2-MSO-01' }) '乙丙没有落在同一个停靠上，这条场景的前提不成立'
        return
    }

    # --- 3. 12 号站：清单两条，先装乙（前侧）、再装丙（后侧） -------------------------------------------------------

    $null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $mixedStationRiotId
    $mixedArrivedAt = [DateTimeOffset]::UtcNow
    $twoItemWorklist = Wait-L2ConditionOrLast -Description 'the onboard acknowledged a two-item worklist at the mixed station' `
        -Journal $journal -Criterion 'mixed-worklist-acknowledged' -TimeoutSeconds 60 `
        -Probe { @((Get-AcknowledgedWorklists $mixedStationName) | Where-Object { @($_.Items).Count -eq 2 }) | Select-Object -First 1 } `
        -Until { param($v) $null -ne $v }
    $expectedPair = (@($b.Sublot, $c.Sublot) | Sort-Object) -join ','
    $assertions.Add(
        'L2-MSO-02',
        '服务端：12 号站被车载端确认的清单里有一版同时列着乙丙两条清单项（正事实，从发件箱读）',
        ($null -ne $twoItemWorklist -and (Get-SublotSet $twoItemWorklist.Items) -eq $expectedPair),
        $expectedPair,
        $(if ($null -ne $twoItemWorklist) { "$(Format-L2WireSnapshot $twoItemWorklist)" } else {
                "(没有两条的已确认清单) $((@(Get-AcknowledgedWorklists $mixedStationName) | ForEach-Object { Format-L2WireSnapshot $_ }) -join ' | ')" }))

    $rowsAtMixed = Wait-L2ConditionOrLast -Description 'the HMI lists both worklist items at the mixed station' -Journal $journal `
        -Criterion 'mixed-worklist-rows' -TimeoutSeconds 30 -Probe { Get-L2WorklistRows $onboard } `
        -Until { param($v) $null -ne $v -and ((@($v) | ForEach-Object { $_.Sublot } | Sort-Object) -join ',') -eq $expectedPair }
    $assertions.Add(
        'L2-MSO-03',
        '车载端：清单确认之后，UIA 的本站清单列表（WorklistItems）同时有乙丙两行',
        ($null -ne $rowsAtMixed -and @($rowsAtMixed).Count -eq 2 -and
            ((@($rowsAtMixed) | ForEach-Object { $_.Sublot } | Sort-Object) -join ',') -eq $expectedPair),
        "两行 $expectedPair", (Format-L2WorklistRows $rowsAtMixed))

    $loadB = Invoke-L2RigLoad $Context $journeyId $b
    $loadC = Invoke-L2RigLoad $Context $journeyId $c -WorklistAfter $loadB.CommittedAt
    $mixedLoadsDoneAt = [DateTimeOffset]::UtcNow

    # --- 4. 持货等单 ------------------------------------------------------------------------------------------

    $waiting = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('CARGO_HOLDING_WAIT', 'VEHICLE_FULL', 'CLOSED') `
        -Criterion 'phase-after-mixed-loads'
    $countdown = Wait-L2ConditionOrLast -Description 'the HMI shows the cargo holding countdown' -Journal $journal `
        -Criterion 'cargo-holding-countdown' -TimeoutSeconds 30 -Probe { Get-L2LoadingPhaseLine $onboard 'CargoHoldingCountdown' } `
        -Until { param($v) $v -eq 'ACTIVE' }
    # 物理状态在装完、离站之前读：三笔装货都已提交（Invoke-L2RigLoad 等到 Committed 才返回）。
    $physicalAfterLoads = $simulator.Snapshot().slots
    $closed = Wait-L2LoadingPhase -Context $Context -DemandId $a.Id -States @('CLOSED') -Criterion 'phase-closed' -TimeoutSeconds 240
    $phaseSnapshots = Get-L2LoadingPhaseSnapshots $connection
    $journal.Observe('loading-phase-snapshots', (Format-L2LoadingPhaseSnapshots $phaseSnapshots), $null)

    # --- 5. 关卡：卸三条 ----------------------------------------------------------------------------------------

    $null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $gateRiotId
    $gateArrivedAt = [DateTimeOffset]::UtcNow
    $gateWorklist = Wait-L2ConditionOrLast -Description 'the onboard acknowledged the three-item worklist at the gate' `
        -Journal $journal -Criterion 'gate-worklist-acknowledged' -TimeoutSeconds 60 `
        -Probe { @((Get-AcknowledgedWorklists $gateName) | Where-Object { @($_.Items).Count -eq 3 }) | Select-Object -First 1 } `
        -Until { param($v) $null -ne $v }
    $rowsAtGate = Wait-L2ConditionOrLast -Description 'the HMI marks B front and C rear at the gate' -Journal $journal `
        -Criterion 'gate-worklist-sides' -TimeoutSeconds 30 -Probe { Get-L2WorklistRows $onboard } `
        -Until {
            param($v)
            $null -ne $v -and @(@($v) | Where-Object { $_.Sublot -eq $b.Sublot -and $_.Side -eq 'FRONT' }).Count -eq 1 -and
                @(@($v) | Where-Object { $_.Sublot -eq $c.Sublot -and $_.Side -eq 'REAR' }).Count -eq 1
        }
    $unloads = [System.Collections.Generic.List[object]]::new()
    foreach ($i in 1..3) {
        $unloads.Add((Invoke-L2RigUnloadNext $Context $journeyId @($unloads | ForEach-Object { $_.AttemptId })))
    }
    $stage = Wait-L2ConditionOrLast -Description 'the journey completed' -Journal $journal -Criterion 'journey-completed' `
        -TimeoutSeconds 120 -Probe { Get-L2JourneyStage $connection $journeyId } -Until { param($v) $v -eq 'Completed' }
    $session = Wait-L2ConditionOrLast -Description 'the session is Ready' -Journal $journal -Criterion 'session-ready' `
        -TimeoutSeconds 30 -Probe { Get-L2RealSession $connection $Context.AgvId } `
        -Until { param($v) $null -ne $v -and [string]$v.Readiness -eq 'Ready' }
    $completedAt = [DateTimeOffset]::UtcNow

    $transitions = @()
    $samplerErrors = @()
    $changes = Stop-L2DoorSampler $sampler
    $samplerStopped = $true
    $samplerErrors = @($changes | Where-Object { $_.PSObject.Properties['Error'] })
    $transitions = @($changes | Where-Object { $_.PSObject.Properties['Slots'] })
    foreach ($change in $transitions) { $journal.Note("Not locked at $($change.At): [$(@($change.Slots) -join ',')]") }

    # --- 6. 判据：装货 -----------------------------------------------------------------------------------------

    $bOp = Get-L2DemandOperation $connection $b.Id 'Load'
    $cOp = Get-L2DemandOperation $connection $c.Id 'Load'
    $bCommitted = if ($null -ne $bOp -and (Test-L2RealPresent $bOp.CommittedAt)) { ConvertTo-L2RealInstant $bOp.CommittedAt } else { $null }
    $cCreated = if ($null -ne $cOp) { ConvertTo-L2RealInstant $cOp.CreatedAt } else { $null }
    $bSpan = Get-SlotSpan $transitions $loadB.OpenedSlot $mixedArrivedAt $mixedLoadsDoneAt
    $cSpan = Get-SlotSpan $transitions $loadC.OpenedSlot $mixedArrivedAt $mixedLoadsDoneAt
    $assertions.Add(
        'L2-MSO-04',
        '装货先前后后、第二条命令在第一条闭环之后才开锁：丙（后侧）的装货命令建在乙（前侧）的装货提交之后；模拟器采样里丙那一仓第一次未锁闭晚于乙那一仓最后一次未锁闭（REQ-0357）。这一条由驱动顺序保证（等乙提交、新清单确认后才扫丙），证不了车载端执行器串行，有判别力的是卸货的 10、11',
        ($null -ne $bCommitted -and $null -ne $cCreated -and $cCreated -ge $bCommitted -and
            $bSpan.Last -ge 0 -and $cSpan.First -ge 0 -and $cSpan.First -gt $bSpan.Last),
        '丙命令 ≥ 乙提交 / 采样 乙仓最后 < 丙仓最先',
        "乙提交 $(if ($bCommitted) { $bCommitted.ToString('o') } else { '-' }) 丙命令 $(if ($cCreated) { $cCreated.ToString('o') } else { '-' }) / 采样 乙仓 $($loadB.OpenedSlot) [$($bSpan.First)..$($bSpan.Last)] 丙仓 $($loadC.OpenedSlot) [$($cSpan.First)..$($cSpan.Last)]")

    $groups = "甲 $(Format-L2RealSlots $loadA.TargetSlots)→$(Get-Group $loadA.OpenedSlot) / 乙 $(Format-L2RealSlots $loadB.TargetSlots)→$(Get-Group $loadB.OpenedSlot) / 丙 $(Format-L2RealSlots $loadC.TargetSlots)→$(Get-Group $loadC.OpenedSlot)"
    $assertions.Add(
        'L2-MSO-05',
        '装货各开各组：乙的目标仓与开的仓在前侧组、丙的在后侧组、甲的在前侧组，开的仓就是目标仓（一条需求一仓）',
        (@($loadA.TargetSlots).Count -eq 1 -and @($loadB.TargetSlots).Count -eq 1 -and @($loadC.TargetSlots).Count -eq 1 -and
            $loadA.OpenedSlot -eq $loadA.TargetSlots[0] -and $loadB.OpenedSlot -eq $loadB.TargetSlots[0] -and $loadC.OpenedSlot -eq $loadC.TargetSlots[0] -and
            (Get-Group $loadA.OpenedSlot) -eq 'FRONT' -and (Get-Group $loadB.OpenedSlot) -eq 'FRONT' -and (Get-Group $loadC.OpenedSlot) -eq 'REAR'),
        '甲 FRONT / 乙 FRONT / 丙 REAR，开的仓 = 目标仓', $groups)

    # 三条需求的目标仓（服务端 StationOperations.TargetSlotsJson）应当正好是装着货、关门锁好的那几仓，其余全空。
    $targetUnion = @(@($loadA.TargetSlots) + @($loadB.TargetSlots) + @($loadC.TargetSlots) | Sort-Object -Unique)
    $physicalWrong = @($physicalAfterLoads | Where-Object {
            $want = if ([int]$_.slotNo -in $targetUnion) { 'OCCUPIED' } else { 'EMPTY' }
            [string]$_.cargoState -ne $want -or [string]$_.doorState -ne 'CLOSED' -or [int]$_.lockFeedbackRaw -ne 1 -or [int]$_.unlockOutputRaw -ne 0
        } | ForEach-Object { "$($_.slotNo)=$($_.doorState)/$($_.cargoState)/$($_.lockFeedbackRaw)/$($_.unlockOutputRaw)" })
    # 「一致」有两半：物理状态等于服务端记下的目标仓，而目标仓又在这条需求的区域指派的那一组。只判前一半，服务端把丙的货
    # 派进前侧组时物理与目标照样一致（都错在同一处），这条就白绿了。
    $occupiedByGroup = @(@($physicalAfterLoads) | Where-Object { [string]$_.cargoState -eq 'OCCUPIED' } |
        ForEach-Object { Get-Group ([int]$_.slotNo) } | Sort-Object)
    $wrongGroup = [System.Collections.Generic.List[string]]::new()
    foreach ($pair in @(@{ Demand = $a; Load = $loadA }, @{ Demand = $b; Load = $loadB }, @{ Demand = $c; Load = $loadC })) {
        $want = $sideOf[$pair.Demand.Id]
        $groupsOfTargets = @(@($pair.Load.TargetSlots) | ForEach-Object { Get-Group $_ })
        if (@($groupsOfTargets | Where-Object { $_ -ne $want }).Count -gt 0) {
            $wrongGroup.Add("$($pair.Demand.Label) 目标 $(Format-L2RealSlots $pair.Load.TargetSlots) 在 $($groupsOfTargets -join ',')，应在 $want")
        }
    }
    $assertions.Add(
        'L2-MSO-06',
        '装完时模拟器每个仓的物理状态与三条需求的 TargetSlotsJson 一致：目标仓有货、其余全空，全部关门、锁上、开锁输出复位；每条需求的目标仓都在它区域指派的那一组（有货的是前侧两仓、后侧一仓）',
        ($targetUnion.Count -eq 3 -and $physicalWrong.Count -eq 0 -and $wrongGroup.Count -eq 0 -and
            ($occupiedByGroup -join ',') -eq 'FRONT,FRONT,REAR'),
        "有货 [$($targetUnion -join ',')]，其余空，全部 CLOSED/1/0 / 有货的组 FRONT,FRONT,REAR",
        "目标 [$($targetUnion -join ',')] / 不一致 $($physicalWrong.Count) 仓$(if ($physicalWrong) { '：' + ($physicalWrong -join ' ') }) / 有货的组 $($occupiedByGroup -join ',')$(if ($wrongGroup) { ' / ' + ($wrongGroup -join '; ') })")

    # --- 7. 判据：持货 -----------------------------------------------------------------------------------------

    $waitSnapshots = @(@($phaseSnapshots) | Where-Object { $_.State -eq 'CARGO_HOLDING_WAIT' })
    $assertions.Add(
        'L2-MSO-07',
        '持货等单确实发生过：12 号站装完后装货阶段是 CARGO_HOLDING_WAIT、发给车的快照里有它，车载端 UIA 读到持货倒计时 ACTIVE',
        ($null -ne $waiting -and [string]$waiting.LoadingPhaseState -eq 'CARGO_HOLDING_WAIT' -and $waitSnapshots.Count -ge 1 -and $countdown -eq 'ACTIVE'),
        'CARGO_HOLDING_WAIT / 快照有 WAIT / 倒计时 ACTIVE',
        "$(Format-L2CargoJourney $waiting) / WAIT 快照 $($waitSnapshots.Count) 张 / 倒计时 $(if ($null -eq $countdown) { '(不在树上)' } else { $countdown })")

    $closedReasons = @(@($phaseSnapshots) | Where-Object { $_.State -eq 'CLOSED' } | ForEach-Object { $_.Reason } | Sort-Object -Unique)
    $assertions.Add(
        'L2-MSO-08',
        '持货收尾原因属于 {CARGO_HOLDING_TIMEOUT, VEHICLE_FULL}，不是让站 WAITING_STATION_YIELD（车队只有这一辆车）；发给车的关闭快照是同一个原因',
        ($null -ne $closed -and [string]$closed.LoadingPhaseState -eq 'CLOSED' -and
            [string]$closed.LoadingClosedReason -in @('CARGO_HOLDING_TIMEOUT', 'VEHICLE_FULL') -and
            $closedReasons.Count -eq 1 -and $closedReasons[0] -eq [string]$closed.LoadingClosedReason),
        'CLOSED/CARGO_HOLDING_TIMEOUT 或 CLOSED/VEHICLE_FULL，快照一致',
        "$(Format-L2CargoJourney $closed) / 快照原因 $($closedReasons -join ',')")

    # --- 8. 判据：卸货 -----------------------------------------------------------------------------------------

    $assertions.Add(
        'L2-MSO-09',
        '车载端：关卡清单确认之后，UIA 清单列表上乙那一行的侧标 FRONT、丙那一行 REAR（onboard-hmi#134 WorklistItemSide）',
        ($null -ne $gateWorklist -and $null -ne $rowsAtGate -and
            @(@($rowsAtGate) | Where-Object { $_.Sublot -eq $b.Sublot -and $_.Side -eq 'FRONT' }).Count -eq 1 -and
            @(@($rowsAtGate) | Where-Object { $_.Sublot -eq $c.Sublot -and $_.Side -eq 'REAR' }).Count -eq 1),
        "$($b.Sublot)/FRONT, $($c.Sublot)/REAR",
        "$(if ($gateWorklist) { Format-L2WireSnapshot $gateWorklist } else { '(没有三条的已确认清单)' }) / 界面 $(Format-L2WorklistRows $rowsAtGate)")

    $unloadOrder = @($unloads | ForEach-Object { $sideOf[[string]$_.DemandId] })
    $lastFront = [Array]::LastIndexOf([object[]]$unloadOrder, 'FRONT')
    $firstRear = [Array]::IndexOf([object[]]$unloadOrder, 'REAR')
    $bUnload = @($unloads | Where-Object { $_.DemandId -eq $b.Id }) | Select-Object -First 1
    $cUnload = @($unloads | Where-Object { $_.DemandId -eq $c.Id }) | Select-Object -First 1
    $bUnloadOp = Get-L2DemandOperation $connection $b.Id 'Unload'
    $cUnloadOp = Get-L2DemandOperation $connection $c.Id 'Unload'
    $bUnloadCommitted = if ($null -ne $bUnloadOp -and (Test-L2RealPresent $bUnloadOp.CommittedAt)) { ConvertTo-L2RealInstant $bUnloadOp.CommittedAt } else { $null }
    $cUnloadCreated = if ($null -ne $cUnloadOp) { ConvertTo-L2RealInstant $cUnloadOp.CreatedAt } else { $null }
    $bGate = if ($null -ne $bUnload) { Get-SlotSpan $transitions $bUnload.OpenedSlot $gateArrivedAt $completedAt.AddSeconds(1) } else { [pscustomobject]@{ First = -1; Last = -1 } }
    $cGate = if ($null -ne $cUnload) { Get-SlotSpan $transitions $cUnload.OpenedSlot $gateArrivedAt $completedAt.AddSeconds(1) } else { [pscustomobject]@{ First = -1; Last = -1 } }
    $assertions.Add(
        'L2-MSO-10',
        '卸货一次一扇、先前后后（规格第 20 节，不是第 8.3 节「两组同开」）：前侧两条都卸在后侧那一条之前；丙的卸货命令建在乙的卸货提交之后；采样里丙那一仓在乙那一仓最后一次未锁闭之后才开',
        ($unloads.Count -eq 3 -and $firstRear -gt $lastFront -and $null -ne $bUnloadCommitted -and $null -ne $cUnloadCreated -and
            $cUnloadCreated -ge $bUnloadCommitted -and $bGate.Last -ge 0 -and $cGate.First -gt $bGate.Last),
        'FRONT,FRONT,REAR / 丙命令 ≥ 乙提交 / 采样 乙仓最后 < 丙仓最先',
        "$($unloadOrder -join ',') / 乙提交 $(if ($bUnloadCommitted) { $bUnloadCommitted.ToString('o') } else { '-' }) 丙命令 $(if ($cUnloadCreated) { $cUnloadCreated.ToString('o') } else { '-' }) / 采样 乙 [$($bGate.First)..$($bGate.Last)] 丙 [$($cGate.First)..$($cGate.Last)]")

    # --- 9. 判据：全程一次一扇、终态 ------------------------------------------------------------------------------

    $widest = @($transitions | Where-Object { @($_.Slots).Count -gt 1 })
    $maxOpen = if ($transitions.Count -gt 0) { (@($transitions | ForEach-Object { @($_.Slots).Count }) | Measure-Object -Maximum).Maximum } else { 0 }
    $seenSlots = @($transitions | ForEach-Object { @($_.Slots) } | Sort-Object -Unique)
    $missed = @(@($loadA.OpenedSlot, $loadB.OpenedSlot, $loadC.OpenedSlot) | Where-Object { $_ -notin $seenSlots })
    $sequence = @($transitions | ForEach-Object { "[$(@($_.Slots) -join ',')]" }) -join ' → '
    $assertions.Add(
        'L2-MSO-11',
        '全程一次一扇（REQ-0357）：从车到 11 号站起到旅程走完，模拟器每 100 ms 的采样里任一时刻至多一仓未锁闭；采样确实见过三条需求开过的三个仓',
        ($widest.Count -eq 0 -and $samplerErrors.Count -eq 0 -and $missed.Count -eq 0),
        '至多 1 仓 / 采样错误 0 / 三个仓都见过',
        "至多 $maxOpen 仓$(if ($widest) { "（$(@($widest | ForEach-Object { "$($_.At) [$(@($_.Slots) -join ',')]" }) -join '; ')）" }) / 采样错误 $($samplerErrors.Count) / 没见过 [$($missed -join ',')] / $sequence")

    $perDemand = foreach ($demand in @($a, $b, $c)) {
        $ops = Invoke-L2Query -Connection $connection -Sql "SELECT OperationType, Status FROM StationOperations WHERE DemandId = '$($demand.Id)'"
        $status = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($demand.Id)'"
        $loads = @($ops | Where-Object { [string]$_.OperationType -eq 'Load' -and [string]$_.Status -eq 'Committed' }).Count
        $unloadsCommitted = @($ops | Where-Object { [string]$_.OperationType -eq 'Unload' -and [string]$_.Status -eq 'Committed' }).Count
        "$($demand.Label):$status/ops $($ops.Count)/load $loads/unload $unloadsCommitted"
    }
    $expectedPerDemand = @('A', 'B', 'C') | ForEach-Object { "${_}:Succeeded/ops 2/load 1/unload 1" }
    $finalSlots = @($simulator.Snapshot().slots | Where-Object {
            [string]$_.doorState -ne 'CLOSED' -or [string]$_.cargoState -ne 'EMPTY' -or [int]$_.lockFeedbackRaw -ne 1 -or [int]$_.unlockOutputRaw -ne 0
        } | ForEach-Object { "$($_.slotNo)=$($_.doorState)/$($_.cargoState)/$($_.lockFeedbackRaw)/$($_.unlockOutputRaw)" })
    $assertions.Add(
        'L2-MSO-12',
        '终态：三条需求各一次装、一次卸、都 Committed、Succeeded，旅程 Completed，八个仓都关门、空、锁上、复位，会话回 Ready',
        ($stage -eq 'Completed' -and (@($perDemand) -join ' ') -eq ($expectedPerDemand -join ' ') -and $finalSlots.Count -eq 0 -and
            $null -ne $session -and [string]$session.Readiness -eq 'Ready'),
        "Completed / $($expectedPerDemand -join ' ') / 八仓 CLOSED/EMPTY/1/0 / Ready",
        "$stage / $(@($perDemand) -join ' ') / 不干净 $($finalSlots.Count) 仓$(if ($finalSlots) { '：' + ($finalSlots -join ' ') }) / $(Format-L2RealSession $session)")

    $journal.Note('Mixed-side station: two demands at one stop, loaded and unloaded one door at a time, front before rear.')
} finally {
    if (-not $samplerStopped) { $null = Stop-L2DoorSampler $sampler }
}
