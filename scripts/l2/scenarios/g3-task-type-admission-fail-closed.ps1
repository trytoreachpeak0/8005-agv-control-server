#Requires -Version 7

<#
G3 `FP-IS-10`：按任务类型准入、缺绑定 fail-closed（批次 6，control-server#164）。协议向量
`CV-TASK-TYPE-ADMISSION-FAIL-CLOSED`（`protocol-v2.0.0`）。

两端的半边：服务端 control-server#160（按任务类型准入，缺绑定只停该类、原因只在服务端与看板），车载端
onboard-hmi#115（放开非 `WIRE_TO_GATE` 的入站校验、按清单项显示任务类型，没有清单项不显示、不推断）。合成对端的同一条
路径在 `task-type-binding-missing-not-cascading`，这里换成真车载端 WPF 与真模拟器。

**配置就是编排器默认装的出厂预置配置**（control-server#159）：本图需求集与绑定只有 `WIRE_TO_GATE`。放两条需求，
一条 `STAGING_TO_WIRE`（AREA `C15-13`，11 号站）、一条 `WIRE_TO_GATE`（AREA `N1-3`，12 号站）。两条的 AREA 与 EQP
都不同，免得 `REQ-0187` 的 AREA–EQP 唯一性把 `WIRE_TO_GATE` 那条也挡住，那就证不出「不连带」了。

判据对着向量的产品断言写：

- 服务端 `FAIL_CLOSED_ON_MISSING_BINDING`：`STAGING_TO_WIRE` 不受理、不建单、不派车，发件箱里没有任何提到它的消息，
  不投运原因只落在服务端的 `JourneyBacklog`，不经 `VehicleBusinessStateSnapshot.blockingFacts` 下发（规格第 5.3 节）。
- 服务端 `ADMIT_ONLY_BOUND_TASK_TYPES`：同一轮里的 `WIRE_TO_GATE` 照常受理、在真车载端上走完一趟。
- 车载端 `NEVER_INFER_UNBOUND_TASK_TYPE`：经 UIA 读 `TaskType`——只有计划、没有清单项时不显示任何任务类型；有清单项时
  只出现 `WIRE_TO_GATE` 那一种文案。按 `AutomationId` 读，按文字含义判（含「关卡」、不是「未知」），不比文案全文。
- 车载端 `DISPLAY_ADMISSION_BLOCK_REASON` **不认领**：规格第 5.3 节把原因留在服务端，v2 没有生产者（program#125）。

向量的 `stableErrorCode`（`ACTION_NOT_ALLOWED_IN_STATE`）是车载端对不可做动作的应答码，本场景里车载端从没被要求做
`STAGING_TO_WIRE` 的任何动作，所以没有它可判；判的是「它根本没被下发」。

**断言读服务端的库、假 RIoT 的快照、模拟器快照，与车载端界面上 `StopDirection`、`TaskType` 两个元素。**
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SlotGroups.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2TaskTypeJourney.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$simulator = $Context.Simulator
$connection = $Context.Connection

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: its onboard assertions read the shipped HMI through UI Automation.'
}

$boundGuid = [guid]::NewGuid()
$boundId = $boundGuid.ToString('D')
$boundSublot = "G3-10-$($Context.RunId)"
$unboundGuid = [guid]::NewGuid()
$unboundId = $unboundGuid.ToString('D')

function Get-Count([string]$sql) { return Get-L2RealCount $connection $sql }

# 缺绑定的原因码（control-server#160，DispatchReasonCodes.TaskTypeBindingMissing）。与 OUT_OF_SCOPE_WORK_TYPE（规则表不认识或
# 部署不允许）、TASK_TYPE_NOT_YET_EXECUTABLE（绑定齐全但本构建还不会执行）是三种不同情形，现场处置也不同。
$missingBindingReason = 'TASK_TYPE_BINDING_MISSING'

# --- 1. 已绑定的需求先出现；它受理之后，缺绑定的那条再出现 ----------------------------------------------------------
#
# 先后是刻意的：只有一台车，而且已绑定的这条等它受理完了才放缺绑定的那条，派车的任务侧次序（DispatchCandidateOrdering）
# 怎么排都轮不到比较两者。缺绑定的需求若先被看到，
# 一个 fail-open 的服务端会先把车派给它，已绑定的那条就走不成，这时变红的是「已绑定的照常走完」，而不是本该变红的
# 「缺绑定的不受理」。让已绑定的先受理，缺绑定的那条就在整趟旅程里、以及车空下来之后，一轮一轮地被重新判定。

$journal.Note("Publishing WIRE_TO_GATE demand $($boundGuid.ToString('N')) (AREA N1-3, sublot $boundSublot).")
$null = $mes.Command('Put', "demands/$($boundGuid.ToString('N'))", @{
    workType    = 'WIRE_TO_GATE'
    sublot      = $boundSublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

# 服务端把缺绑定原因记下的时刻，由场景自己观测（control-server#204 之后的 #203 条目 1）。
# **不读 JourneyBacklog.FirstSeenAt**：那一列是「这条需求第一次进 backlog」，不是「这个原因首次记下」——
# DispatchRoundRunner.UpsertBacklog 在行已存在时只改 ReasonCode 与 LastSeenAt、原样保留 FirstSeenAt
# （DispatchReasonCodes 的注释也写着 "keeps its FirstSeenAt"）。这条场景里两者大概率恰好相等，
# 而「恰好相等」正是这条判据不该依赖的东西。
#
# 观测时刻必然晚于真实落库，所以拿它当下界只会更严，不会让 G3-10-04 假绿。
#
# 用哈希表装这个时刻，不用普通变量：下面那个回调带 .GetNewClosure()，而**闭包里的 `$script:` 写的是闭包
# 自己的作用域，外面读不到**（实测过：写 `$script:reasonSeenAt`，回调跑完外面仍是 $null，于是
# 「原因记下之后的快照 >= 1 份」恒不成立、这条判据在正确的世界里也必然红）。哈希表是引用类型，
# 闭包捕获的是同一个对象，改它的成员外面看得见。
$reasonSeen = @{ At = $null }
$publishUnbound = {
    $journal.Note("Publishing STAGING_TO_WIRE demand $($unboundGuid.ToString('N')) (AREA C15-13), which the factory preset does not bind.")
    $null = $mes.Command('Put', "demands/$($unboundGuid.ToString('N'))", @{
        workType    = 'STAGING_TO_WIRE'
        sublot      = "G3-10-UNBOUND-$($Context.RunId)"
        area        = 'C15-13'
        eqp         = 'EQP-L2-03'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
    # 在这里等、而不是等旅程走完再等：VehicleBusinessStateSnapshot 是**按旅程阶段**发的，不是每轮发
    # （OnboardJourneyPublisher 的注释：one deterministic messageId per journey stage）。旅程一结束就不再有新快照，
    # 把时刻记在那之后，「至少一份快照晚于原因」会必然不成立——那是一条在正确的世界里也必然红的判据。
    # 这里是第一份计划刚被确认、车还没动，后面还有到站、录入、装货、二站、卸货、完成一串阶段会发快照。
    $null = Wait-L2RealOrLast -Description 'the server recorded the missing-binding reason for the unbound demand' `
        -Journal $journal -Criterion 'unbound-reason-recorded' -TimeoutSeconds 120 `
        -Probe { $row = Get-L2JourneyBacklogRow -Connection $connection -DemandId $unboundId
                 if ($null -ne $row) { [string]$row.ReasonCode } else { $null } } `
        -Until { param($v) $v -ceq $missingBindingReason }
    $reasonSeen.At = [DateTimeOffset]::UtcNow
    $journal.Note("Missing-binding reason observed at $($reasonSeen.At.ToString('o')); G3-10-04 requires a business-state snapshot after it.")
}.GetNewClosure()

# --- 2. 已绑定的那一类在真车载端上走完一趟 ------------------------------------------------------------------------

$journey = Invoke-L2TaskTypeJourney -Context $Context -DemandId $boundId -Sublot $boundSublot `
    -OriginRiotId $Context.PickupStationRiotId -DestinationRiotId $Context.GateStationRiotId `
    -BeforeFirstArrival { param($intent) & $publishUnbound }

# 走完之后再让运行时多转几轮：缺绑定的需求在此期间一直被重新判定，仍然不受理，才说明是 fail-closed 而不是「还没轮到」。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

# --- 3. 缺绑定的那一类：不受理、不下发，原因只在服务端 ---------------------------------------------------------------

$unboundAccepted = Get-Count "SELECT COUNT(*) AS Total FROM AcceptedDemands WHERE DemandId = '$unboundId'"
$unboundRuntimes = Get-Count "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE DemandId = '$unboundId'"
$unboundIntents = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$unboundId'"
$unboundOperations = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$unboundId'"
$assertions.Add(
    'G3-10-01',
    '缺绑定的 STAGING_TO_WIRE 需求始终不受理：没有受理快照、没有旅程、没有订单意图、没有仓位操作，已绑定的那条走完之后再转四轮仍是如此（FAIL_CLOSED_ON_MISSING_BINDING）',
    ($unboundAccepted -eq 0 -and $unboundRuntimes -eq 0 -and $unboundIntents -eq 0 -and $unboundOperations -eq 0),
    '受理 0 / 旅程 0 / 意图 0 / 操作 0',
    "受理 $unboundAccepted / 旅程 $unboundRuntimes / 意图 $unboundIntents / 操作 $unboundOperations")

# 发件箱里任何一类消息都不许提到它：计划、清单、业务状态快照都算。两种写法的 id 都查。
# Invoke-L2Query returns its rows as one wrapped array: assign it first, then enumerate.
$mentioningRows = Invoke-L2Query -Connection $connection -Sql (
    "SELECT MessageType FROM ProtocolOutbox WHERE PayloadJson LIKE '%$unboundId%' " +
    "OR PayloadJson LIKE '%$($unboundGuid.ToString('N'))%'")
$mentioning = @($mentioningRows | ForEach-Object { [string]$_.MessageType })
$boundIntentRows = Invoke-L2Query -Connection $connection -Sql "SELECT UpperId FROM OrderIntents WHERE DemandId = '$boundId'"
$boundUpperIds = @($boundIntentRows | ForEach-Object { [string]$_.UpperId })
$orders = @($riot.Snapshot().body.orders)
$foreignOrders = @($orders | Where-Object { [string]$_.upperId -notin $boundUpperIds })
$assertions.Add(
    'G3-10-02',
    '缺绑定的需求没有被排进任何计划或清单：服务端发件箱里没有一条消息提到它，RIoT 上没有不属于已绑定需求的单',
    ($mentioning.Count -eq 0 -and $foreignOrders.Count -eq 0),
    '发件箱 0 条 / 外来单 0 张',
    "发件箱 $($mentioning.Count) 条$(if ($mentioning.Count) { "（$($mentioning -join ', ')）" }) / 外来单 $($foreignOrders.Count) 张")

$backlog = Get-L2JourneyBacklogRow -Connection $connection -DemandId $unboundId
$backlogReason = if ($null -ne $backlog) { [string]$backlog.ReasonCode } else { $null }
$backlogAccepted = $null -ne $backlog -and (Test-L2RealPresent $backlog.AcceptedAt)
$assertions.Add(
    'G3-10-03',
    '不投运原因记在服务端：JourneyBacklog 里这条需求的原因是 TASK_TYPE_BINDING_MISSING（缺绑定，不是范围外或尚未可执行），且没有受理时间',
    ($backlogReason -ceq $missingBindingReason -and -not $backlogAccepted),
    "$missingBindingReason / 未受理",
    $(if ($null -eq $backlog) { '(没有 JourneyBacklog 行)' } else { "$backlogReason / 受理时间=$($backlog.AcceptedAt)" }))

# 规格第 5.3 节：原因只在服务端与看板，不经 blockingFacts 下发。每一份业务状态快照都查，缺绑定的原因码、需求号都不许出现。
$businessSnapshots = Get-L2RealOutbound $connection 'VehicleBusinessStateSnapshot'
$leakingFacts = @($businessSnapshots | Where-Object {
        # -InputObject, not the pipeline: piped, an empty array serialises to nothing and $facts would be $null.
        $facts = ConvertTo-Json -InputObject @($_.Payload.blockingFacts) -Depth 10 -Compress
        ((Test-L2RealPresent $backlogReason) -and $facts.Contains($backlogReason, [StringComparison]::Ordinal)) -or
            $facts.Contains($unboundId, [StringComparison]::OrdinalIgnoreCase) -or
            $facts.Contains($unboundGuid.ToString('N'), [StringComparison]::OrdinalIgnoreCase) -or
            $facts -cmatch 'BINDING|STAGING_TO_WIRE'
    })
# 时序前提：至少一份快照晚于原因被记下的时刻。少了它，所有快照都早于那一刻时这条判据照样绿——
# 而那种运行根本没给泄露留下机会，「没泄露」说的是「还没到会泄露的时候」（control-server#164 审查）。
$reasonSeenAt = $reasonSeen.At
$snapshotsAfterReason = @($businessSnapshots | Where-Object { $null -ne $reasonSeenAt -and $_.At -gt $reasonSeenAt })
$backlogFirstSeen = if ($null -ne $backlog) { [string]$backlog.FirstSeenAt } else { '(无 backlog 行)' }
$assertions.Add(
    'G3-10-04',
    '准入原因没有下发给车：全部 VehicleBusinessStateSnapshot 的 blockingFacts 里都没有缺绑定的原因码、需求号或任务类型；且至少有一份快照是在原因被记下之后发的（规格第 5.3 节）',
    ($businessSnapshots.Count -ge 1 -and $leakingFacts.Count -eq 0 -and $snapshotsAfterReason.Count -ge 1),
    '快照 ≥1 份 / 含准入原因 0 份 / 原因记下之后的快照 ≥1 份',
    ("快照 $($businessSnapshots.Count) 份 / 含准入原因 $($leakingFacts.Count) 份 / " +
        "原因记下之后 $($snapshotsAfterReason.Count) 份（原因观测于 $(if ($null -eq $reasonSeenAt) { '(未观测到)' } else { $reasonSeenAt.ToString('o') })" +
        "，最后一份快照 $(if ($businessSnapshots.Count -gt 0) { $businessSnapshots[-1].At.ToString('o') } else { '(无)' })" +
        "，对照 JourneyBacklog.FirstSeenAt=$backlogFirstSeen）"))

# --- 4. 已绑定的那一类照常受理并走完（不连带） --------------------------------------------------------------------

$boundDemandStatus = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$boundId'"
$boundCommits = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$boundId' AND Status = 'Committed'"
$assertions.Add(
    'G3-10-05',
    '同一轮里已绑定的 WIRE_TO_GATE 需求照常受理，并在真车载端上装货、卸货、走完（ADMIT_ONLY_BOUND_TASK_TYPES；缺绑定不连带其它任务类型）',
    ($boundDemandStatus -eq 'Succeeded' -and $journey.Stage -eq 'Completed' -and $boundCommits -eq 2),
    'Succeeded / Completed / 2 笔操作 Committed',
    "$boundDemandStatus / $($journey.Stage) / $boundCommits 笔操作 Committed")

# 向量四步：计划 → 确认 → 业务状态快照 → 确认。对着已绑定那条需求的旅程查：第一份计划被确认，其后有一份业务状态快照被确认。
# 与 g3-reversed-direction-journey 的 G3-11-03 同一件事（control-server#203 条目 3）：旅程走完那一刻，确认
# 可能还在路上——它由车载端发出、服务端另起一次写库，与阶段推进不是同一次提交。原来立刻就读，判据偶发红。
#
# 等的就是判据本身要的那三件事，所以这里没有放松任何一条；`Wait-L2RealOrLast` 超时不抛错，把最后一次读到的
# 两批快照交给判据，红点因此落在判据表里、带着是哪一份没确认，而不是变成一行超时。选它而不是「先转几轮」的
# 理由与那边相同：运行时轮次与确认落库之间没有因果。
#
# 业务状态快照在这里重读一次（第 164 行那份是 G3-10-04 用的，取的是更早的时刻，不动它）。
$acknowledged = Wait-L2RealOrLast -Description 'the bound journey plan and a later business snapshot were acknowledged' `
    -Journal $journal -Criterion 'bound-journey-snapshots-acknowledged' -TimeoutSeconds 30 `
    -Probe {
        [pscustomobject]@{
            Journey  = Get-L2DemandJourneySnapshots $connection $boundId
            Business = Get-L2RealOutbound $connection 'VehicleBusinessStateSnapshot'
        }
    } `
    -Until {
        param($v)
        $plan = @($v.Journey | Where-Object { $_.Type -eq 'UpcomingStopPlanSnapshot' }) | Select-Object -First 1
        $null -ne $plan -and $plan.Acknowledged -and
        @($v.Journey | Where-Object { $_.Fenced -or -not $_.Acknowledged }).Count -eq 0 -and
        @($v.Business | Where-Object { $_.At -ge $plan.At -and $_.Acknowledged }).Count -ge 1
    }
$snapshots = @($acknowledged.Journey)
$firstPlan = @($snapshots | Where-Object { $_.Type -eq 'UpcomingStopPlanSnapshot' }) | Select-Object -First 1
$businessAfterPlan = if ($null -ne $firstPlan) {
    @($acknowledged.Business | Where-Object { $_.At -ge $firstPlan.At -and $_.Acknowledged })
} else { @() }
$assertions.Add(
    'G3-10-06',
    '消息顺序与向量一致：已绑定需求的计划快照被真车载端确认，其后有业务状态快照被确认；这一趟的计划与清单没有一份被作废',
    ($null -ne $firstPlan -and $firstPlan.Acknowledged -and $businessAfterPlan.Count -ge 1 -and
        @($snapshots | Where-Object { $_.Fenced -or -not $_.Acknowledged }).Count -eq 0),
    '计划 ack / 其后业务快照 ack ≥1 / 作废或未确认 0',
    "计划 $(if ($firstPlan) { "ack=$($firstPlan.Acknowledged)" } else { '(无)' }) / 其后业务快照 ack $($businessAfterPlan.Count) / 作废或未确认 $(@($snapshots | Where-Object { $_.Fenced -or -not $_.Acknowledged }).Count)（$(@($snapshots | ForEach-Object { Format-L2JourneySnapshot $_ }) -join ' | ')）")

# --- 5. 车载端：没有清单项不显示任务类型，有清单项只显示被绑定的那一类 ----------------------------------------------

$enRoute = $journey.StopFacts['en-route-to-origin']
$assertions.Add(
    'G3-10-07',
    '只有计划、还没有清单项时，车载端不显示任何任务类型：StopDirection 已按计划腿显示方向，TaskType 为空（NEVER_INFER_UNBOUND_TASK_TYPE）',
    ((Test-L2RealPresent $enRoute.Direction) -and $null -ne $enRoute.TaskType -and $enRoute.TaskType -eq ''),
    'StopDirection 非空 / TaskType (empty)',
    (Format-L2StopFacts $enRoute))

$shown = @($journey.StopFacts.Values | Where-Object { $null -ne $_ -and (Test-L2RealPresent $_.TaskType) } |
    ForEach-Object { [string]$_.TaskType } | Sort-Object -Unique)
$atStops = @($journey.StopFacts['at-origin'], $journey.StopFacts['at-destination'])
$assertions.Add(
    'G3-10-08',
    '车载端全程只显示被绑定的那一种任务类型：取货点与关卡两站的 TaskType 都非空，四次读数里出现过的文案只有一种，是 WIRE_TO_GATE 的（含「关卡」），不是「未知」（NEVER_INFER_UNBOUND_TASK_TYPE；不比文案全文）',
    (@($atStops | Where-Object { $null -eq $_ -or -not (Test-L2RealPresent $_.TaskType) }).Count -eq 0 -and
        $shown.Count -eq 1 -and $shown[0].Contains('关卡') -and $shown[0] -ne '未知'),
    '两站都显示 / 只一种文案，含「关卡」',
    ((@($journey.StopFacts.Keys | ForEach-Object { "${_}: $(Format-L2StopFacts $journey.StopFacts[$_])" }) -join '；')))

# --- 6. 终态 ---------------------------------------------------------------------------------------------------

$acceptedTotal = Get-Count 'SELECT COUNT(*) AS Total FROM AcceptedDemands'
$operationsTotal = Get-Count 'SELECT COUNT(*) AS Total FROM StationOperations'
$unloadedSlot = $journey.Unload.OpenedSlot
$slotReading = Get-L2RealSlotReading $simulator $unloadedSlot
$assertions.Add(
    'G3-10-09',
    '终态 NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE：全程只受理一条需求、两张 RIoT 单、两笔仓位操作，卸完的仓关门、空、锁上、开锁输出复位',
    ($acceptedTotal -eq 1 -and $orders.Count -eq 2 -and $operationsTotal -eq 2 -and $slotReading -eq 'CLOSED/EMPTY/1/0'),
    '1 需求 / 2 单 / 2 操作 / CLOSED/EMPTY/1/0',
    "$acceptedTotal 需求 / $($orders.Count) 单 / $operationsTotal 操作 / 仓 $unloadedSlot $slotReading")

$journal.Note('FP-IS-10: the unbound STAGING_TO_WIRE demand stayed on the server, the bound WIRE_TO_GATE demand ran on the real onboard.')
