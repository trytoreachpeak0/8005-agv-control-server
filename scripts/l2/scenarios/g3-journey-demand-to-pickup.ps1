#Requires -Version 7

<#
G3 `FP-IS-01` 的场景：接单到取货点。协议向量 `CV-DEMAND-ACCEPT-TO-PICKUP`。

由 `scripts/run-journey-g3.ps1` 在四个仓的精确克隆上驱动，也能像其它 L2 场景一样单独跑来调试。
装置是真服务端 + **真车载端 WPF** + 真 slots-simulator + 假 RIoT + 假 MesIngest：旅程真跑，RIoT 与
MesIngest 是仿真。这就是 2026-09-13 用户裁定计入正式切片通过的那一级（`JOURNEY_SIMULATED_COUNTERPARTS`）。

**判据对着向量写，不对着实现写。**`input.ndjson` 给出的顺序是：RIoT 建出 `TO_PICKUP` 单之后、车到
取货点之前发 `UpcomingStopPlanSnapshot` 并得到确认；到站后发 `CurrentStopWorklistSnapshot`，再发下一个
revision 的计划，各自得到确认。服务端在 `5293c43f` 之前到站前什么都不发——那是写这条场景时对着向量查出来
的，缺陷单 `docs/defects/20260913-no-plan-snapshot-before-pickup-arrival.md`。

**断言只读两处：服务端自己的库，和车载端自己的日志库。**确认（`SnapshotAppliedAck`）在服务端落成发件箱行的
`AcknowledgedAt`，服务端收下它之前已经核对过类型、线上内容哈希和 revision；车载端采纳了什么记在它的
`WireToGateAppliedJourneySnapshots` 表里，服务端看不到。两边对上，才说明「车载端显示的是服务端提交的那份
行程」（`DISPLAY_COMMITTED_DEMAND_JOURNEY`），而不是它自己猜的。

**场景停在取货点。**录入 sublot、装载是 `FP-IS-02` 的事；这里只把车停在「等录入」，那正是 `FP-IS-01` 的
终态：`ONE_ACCEPTED_DEMAND_ONE_TO_PICKUP_ORDER_AT_PICKUP` / `NO_SLOT_OPERATION_STARTED`。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: its assertions read the onboard journal.'
}

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "G3-01-$($Context.RunId)"

function Get-Stage {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

function Get-PlanRevision {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT PlanRevision FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    return [long]$rows[0].PlanRevision
}

# 这一趟的行程快照，按服务端写入发件箱的先后。计划与清单分别按 legs[].demandId 与 items[].demandId 归属到
# 这条需求，免得哪天装置里多出一趟旅程时把别人的快照算进来。
function Get-JourneySnapshots {
    $rows = Invoke-L2Query -Connection $connection -Sql @'
SELECT MessageId, MessageType, PayloadJson, CreatedAt, AcknowledgedAt, FencedAt
FROM ProtocolOutbox
WHERE MessageType IN ('UpcomingStopPlanSnapshot', 'CurrentStopWorklistSnapshot')
ORDER BY CreatedAt, MessageId
'@
    $mine = foreach ($row in $rows) {
        $payload = ([string]$row.PayloadJson | ConvertFrom-Json).payload
        $owners = if ([string]$row.MessageType -eq 'UpcomingStopPlanSnapshot') {
            @($payload.legs | ForEach-Object { [string]$_.demandId })
        } else {
            @($payload.items | ForEach-Object { [string]$_.demandId })
        }
        if ($owners -contains $demandId) {
            [pscustomobject]@{
                MessageId    = [string]$row.MessageId
                Type         = [string]$row.MessageType
                Revision     = if ([string]$row.MessageType -eq 'UpcomingStopPlanSnapshot') { [long]$payload.planRevision } else { [long]$payload.worklistRevision }
                LegStates    = if ([string]$row.MessageType -eq 'UpcomingStopPlanSnapshot') { (@($payload.legs | Sort-Object sequence | ForEach-Object { "$($_.legType):$($_.state)" }) -join ',') } else { '' }
                Acknowledged = ($null -ne $row.AcknowledgedAt -and [string]$row.AcknowledgedAt -ne '')
                Fenced       = ($null -ne $row.FencedAt -and [string]$row.FencedAt -ne '')
            }
        }
    }
    return , @($mine)
}

function Get-Count([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    return [int]$rows[0].Total
}

function Get-RiotOrders {
    # `return , @(...)`：零张或一张单时也原样返回数组，严格模式下才能取 .Count。
    return , @($riot.Snapshot().body.orders)
}

# --- 1. 需求出现在 MesIngest 目录里，服务端受理并派车去取货点 -------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$intent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'"
        if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
    } `
    -Until { param($v) $null -ne $v }

# 车载端确认到站前那份计划，是向量第 4 步。等的是服务端发件箱那一行被确认，不是等界面。
$null = Wait-L2Condition -Description 'the onboard acknowledged the plan sent before the arrival' `
    -Journal $journal -Criterion 'dispatch-plan-acknowledged' -TimeoutSeconds 60 `
    -Probe { $s = Get-JourneySnapshots; @($s | Where-Object { $_.Type -eq 'UpcomingStopPlanSnapshot' -and $_.Acknowledged }).Count } `
    -Until { param($v) $v -ge 1 }

$planRevision = Get-PlanRevision
$beforeArrival = Get-JourneySnapshots

$assertions.Add(
    'G3-01-01',
    '需求只受理一次：AcceptedDemands 里这条需求恰好一行（EXACTLY_ONE_ACCEPTED_DEMAND_SNAPSHOT）',
    ((Get-Count "SELECT COUNT(*) AS Total FROM AcceptedDemands WHERE DemandId = '$demandId'") -eq 1),
    1,
    (Get-Count "SELECT COUNT(*) AS Total FROM AcceptedDemands WHERE DemandId = '$demandId'"))

$assertions.Add(
    'G3-01-02',
    '取货意图只建一条且已确认（EXACTLY_ONE_TO_PICKUP_INTENT）',
    ((Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'") -eq 1 -and
        [string]$intent.Status -eq 'CONFIRMED'),
    '1 / CONFIRMED',
    "$(Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'") / $([string]$intent.Status)")

$ordersBefore = Get-RiotOrders
$assertions.Add(
    'G3-01-03',
    'RIoT 侧只有一张单，就是这条取货意图的那张（EXACTLY_ONE_RIOT_ORDER）',
    ($ordersBefore.Count -eq 1 -and [string]$ordersBefore[0].upperId -eq [string]$intent.UpperId),
    "1 / $([string]$intent.UpperId)",
    "$($ordersBefore.Count) / $(if ($ordersBefore.Count -ge 1) { [string]$ordersBefore[0].upperId } else { '(none)' })")

$plansBefore = @($beforeArrival | Where-Object { $_.Type -eq 'UpcomingStopPlanSnapshot' })
$worklistsBefore = @($beforeArrival | Where-Object { $_.Type -eq 'CurrentStopWorklistSnapshot' })
$assertions.Add(
    'G3-01-04',
    '到站前恰好发出一份计划：revision 为旅程的计划起点，取货段 ACTIVE、送货段 PLANNED，已被真车载端确认；工作清单一份都还没发（向量第 3～4 步）',
    ($plansBefore.Count -eq 1 -and $worklistsBefore.Count -eq 0 -and
        $plansBefore[0].Revision -eq $planRevision -and
        $plansBefore[0].LegStates -eq 'TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED' -and
        $plansBefore[0].Acknowledged),
    "计划 1 份 rev $planRevision TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED 已确认 / 清单 0 份",
    "计划 $($plansBefore.Count) 份 $(if ($plansBefore.Count -ge 1) { "rev $($plansBefore[0].Revision) $($plansBefore[0].LegStates) 确认=$($plansBefore[0].Acknowledged)" }) / 清单 $($worklistsBefore.Count) 份")

$operationsBefore = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId'"
$sublotRequestsBefore = Get-Count "SELECT COUNT(*) AS Total FROM ProtocolOutbox WHERE MessageType IN ('SublotEntryRequested', 'SlotOperationCommand')"
$assertions.Add(
    'G3-01-05',
    '到站前没有任何仓位操作，也没有发录入请求或仓位命令（forbidden: slot-operation-before-pickup-arrival）',
    ($operationsBefore -eq 0 -and $sublotRequestsBefore -eq 0),
    '0 / 0',
    "$operationsBefore / $sublotRequestsBefore")

# --- 2. 车开到取货点 -------------------------------------------------------------------------------

$journal.Note('Vehicle departs for the pickup station.')
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $intent.OrderId
})
$journal.Note('Vehicle arrives at the pickup station and comes to rest.')
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $Context.PickupStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })

$stage = Wait-L2Condition -Description 'the server adopted the trusted pickup arrival' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingSublot' }
$assertions.Add(
    'G3-01-06',
    '服务端采纳了可信到站：旅程进入 AwaitingSublot（TRUSTED_PICKUP_ARRIVAL）',
    ($stage -eq 'AwaitingSublot'),
    'AwaitingSublot',
    $stage)

$null = Wait-L2Condition -Description 'the onboard acknowledged the worklist and the arrived plan' `
    -Journal $journal -Criterion 'pickup-snapshots-acknowledged' -TimeoutSeconds 60 `
    -Probe { $s = Get-JourneySnapshots; @($s | Where-Object { $_.Acknowledged }).Count } `
    -Until { param($v) $v -ge 3 }

$sequence = Get-JourneySnapshots
$described = ($sequence | ForEach-Object {
    $kind = if ($_.Type -eq 'UpcomingStopPlanSnapshot') { "plan r$($_.Revision) $($_.LegStates)" } else { "worklist r$($_.Revision)" }
    "$kind ack=$($_.Acknowledged) fenced=$($_.Fenced)"
}) -join ' | '
$expectedOrderOk = $sequence.Count -eq 3 -and
    $sequence[0].Type -eq 'UpcomingStopPlanSnapshot' -and $sequence[0].Revision -eq $planRevision -and
    $sequence[0].LegStates -eq 'TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED' -and
    $sequence[1].Type -eq 'CurrentStopWorklistSnapshot' -and
    $sequence[2].Type -eq 'UpcomingStopPlanSnapshot' -and $sequence[2].Revision -eq ($planRevision + 1) -and
    $sequence[2].LegStates -eq 'TO_PICKUP:ARRIVED,TO_DROPOFF:PLANNED' -and
    @($sequence | Where-Object { -not $_.Acknowledged -or $_.Fenced }).Count -eq 0
$assertions.Add(
    'G3-01-07',
    '消息顺序与向量一致：到站前计划、到站后工作清单、下一 revision 的到站计划，三份都被真车载端确认、没有一份被作废（向量第 3～9 步）',
    $expectedOrderOk,
    "plan r$planRevision TO_PICKUP:ACTIVE,TO_DROPOFF:PLANNED | worklist | plan r$($planRevision + 1) TO_PICKUP:ARRIVED,TO_DROPOFF:PLANNED，全部 ack=True fenced=False",
    $described)

# --- 3. 车载端采纳的，就是服务端提交的那一份 ---------------------------------------------------------

$journalConnection = [Microsoft.Data.Sqlite.SqliteConnection]::new(
    "Data Source=$($Context.OnboardJournalPath);Mode=ReadOnly")
$journalConnection.Open()
try {
    $applied = Invoke-L2Query -Connection $journalConnection `
        -Sql 'SELECT MessageType, MessageId, Revision FROM WireToGateAppliedJourneySnapshots ORDER BY MessageType'
} finally {
    $journalConnection.Dispose()
}
$appliedPlan = @($applied | Where-Object { [string]$_.MessageType -eq 'UpcomingStopPlanSnapshot' })
$appliedWorklist = @($applied | Where-Object { [string]$_.MessageType -eq 'CurrentStopWorklistSnapshot' })
$committedPlan = if ($sequence.Count -ge 3) { $sequence[2] } else { $null }
$committedWorklist = if ($sequence.Count -ge 2) { $sequence[1] } else { $null }
$assertions.Add(
    'G3-01-08',
    '车载端日志库里采纳的计划与工作清单，消息号与 revision 就是服务端提交的那两份（DISPLAY_COMMITTED_DEMAND_JOURNEY / DISPLAY_CURRENT_STOP）',
    ($null -ne $committedPlan -and $null -ne $committedWorklist -and
        $appliedPlan.Count -eq 1 -and [string]$appliedPlan[0].MessageId -eq $committedPlan.MessageId -and
        [long]$appliedPlan[0].Revision -eq $committedPlan.Revision -and
        $appliedWorklist.Count -eq 1 -and [string]$appliedWorklist[0].MessageId -eq $committedWorklist.MessageId -and
        [long]$appliedWorklist[0].Revision -eq $committedWorklist.Revision),
    "plan $(if ($committedPlan) { "$($committedPlan.MessageId) r$($committedPlan.Revision)" }) / worklist $(if ($committedWorklist) { "$($committedWorklist.MessageId) r$($committedWorklist.Revision)" })",
    "plan $(if ($appliedPlan.Count -ge 1) { "$([string]$appliedPlan[0].MessageId) r$([long]$appliedPlan[0].Revision)" } else { '(none)' }) / worklist $(if ($appliedWorklist.Count -ge 1) { "$([string]$appliedWorklist[0].MessageId) r$([long]$appliedWorklist[0].Revision)" } else { '(none)' })")

# --- 4. 终态：一条需求、一张取货单，车在取货点，还没开始任何仓位操作 -----------------------------------------

$ordersAfter = Get-RiotOrders
$intentsAfter = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId'"
$demandsAfter = Get-Count "SELECT COUNT(*) AS Total FROM AcceptedDemands"
$operationsAfter = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId'"
$assertions.Add(
    'G3-01-09',
    '终态 ONE_ACCEPTED_DEMAND_ONE_TO_PICKUP_ORDER_AT_PICKUP / NO_SLOT_OPERATION_STARTED：全程一条需求、一条意图、一张 RIoT 单，没有仓位操作（forbidden: duplicate-demand-acceptance、duplicate-riot-order）',
    ($ordersAfter.Count -eq 1 -and $intentsAfter -eq 1 -and $demandsAfter -eq 1 -and $operationsAfter -eq 0),
    '1 单 / 1 意图 / 1 需求 / 0 操作',
    "$($ordersAfter.Count) 单 / $intentsAfter 意图 / $demandsAfter 需求 / $operationsAfter 操作")

# 车载端发来的每一类消息里，没有一类涉及需求的发现、选择或绑定。协议给车载端的需求权限只有「读服务端提交的
# 投影」（authorityModel.onboardMode = READ_ONLY_COMMITTED_PROJECTION）。
$inboundTypes = @(Invoke-L2Query -Connection $connection -Sql 'SELECT DISTINCT MessageType FROM ProtocolInbox ORDER BY MessageType' |
    ForEach-Object { [string]$_.MessageType })
$demandTypes = @($inboundTypes | Where-Object { $_ -match 'Demand' })
$assertions.Add(
    'G3-01-10',
    '车载端没有发过任何涉及需求发现、选择或绑定的消息（NEVER_DISCOVER_SELECT_OR_BIND_DEMAND）',
    ($demandTypes.Count -eq 0),
    '(none)',
    $(if ($demandTypes.Count -eq 0) { "(none)；车载端发来的类型：$($inboundTypes -join ', ')" } else { $demandTypes -join ', ' }))

$journal.Note('FP-IS-01: demand accepted once, planned before the arrival, worklist and arrived plan after it, applied by the onboard as committed.')
