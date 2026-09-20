#Requires -Version 7

<#
G3 `FP-IS-11`：`STAGING_TO_WIRE` 反向旅程（批次 6，control-server#164）。协议向量 `CV-REVERSED-DIRECTION-JOURNEY`
（`protocol-v2.0.0`）。

两端的半边：服务端 control-server#163（方向由任务类型规则里「固定端是起点还是终点」决定，推翻 I6，准入挂在 AREA 端那条
腿上），车载端 onboard-hmi#115（本站取货还是卸货只取服务端下发的 `stopRole`／`legType`，不按任务类型推）。合成对端的
同一条路径在 `staging-to-wire-reversed-journey`，这里换成真车载端 WPF 与真模拟器。

**配置**见 `setup.psd1`：`STAGING_TO_WIRE` 绑到 230「派工待送」。放一条送往 AREA `N1-3` 机台（12 号站）的
`STAGING_TO_WIRE` 需求。它与 `WIRE_TO_GATE` 恰好反向：先到派工待送站装货，再到 AREA 机台卸货。

判据对着向量的产品断言写：

- 服务端 `DERIVE_DIRECTION_FROM_TASK_TYPE_RULE`：计划第一腿 `TO_PICKUP` 到派工待送站、第二腿 `TO_DROPOFF` 到 AREA 机台，
  `publicStationFunction` 为空；两站的清单 `stopRole` 分别是 `PICKUP`、`DROPOFF`；录入请求与装货发生在派工待送站，卸货
  发生在 AREA 机台，两笔的 `slots` 都是目标仓，模拟器上开的也是目标仓。
- 服务端 `NEVER_SWAP_ORIGIN_AND_DESTINATION`：旅程记下的起点是派工待送站、终点是 AREA 机台；RIoT 上第一张单开往派工待送站、
  第二张开往 AREA 机台；路线证据只有一份。
- 推翻 I6：准入决策冻结在卸货那次操作上（AREA 机台站、`STAGING_TO_WIRE`），装货那次没有。
- 车载端 `DISPLAY_DIRECTION_AS_PLANNED`：经 UIA 读 `StopDirection`，派工待送站显示取货、AREA 机台显示卸货。按
  `AutomationId` 读，按文字含义判（含「取」／含「卸」），不比文案全文。

**断言读服务端的库、假 RIoT 的快照、模拟器快照，与车载端界面上 `StopDirection`、`TaskType` 两个元素。**
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2TaskTypeJourney.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RouteEvidence.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$simulator = $Context.Simulator
$connection = $Context.Connection

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: its onboard assertions read the shipped HMI through UI Automation.'
}

# 与 setup.psd1 的 Stations 和 TaskTypeStations 同一份事实。
$stagingRiotId = 230
$stagingName = '派工待送'
$areaRiotId = $Context.PickupStationRiotId
$areaName = 'N1-3_N1-7'
# 需求的 AREA 与 EQP：G3-11-07 重算路线证据时也要它们，所以写成变量而不是发需求那里的字面量——重算与需求
# 必须是同一对值，否则重算算的是另一趟的路线。
$area = 'N1-3'
$eqp = 'EQP-L2-01'
$mapId = $Context.MapId

$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')
$sublot = "G3-11-$($Context.RunId)"

function Get-Count([string]$sql) { return Get-L2RealCount $connection $sql }

# RIoT 单的目的站：最后一个任务的 destination。
function Get-OrderDestination([object]$order) {
    $missions = @($order.missions)
    if ($missions.Count -eq 0) { return $null }
    return [int]$missions[-1].destination
}

# --- 1. 需求出现在 MesIngest 目录里，然后整趟走完 --------------------------------------------------------------------

$journal.Note("Publishing STAGING_TO_WIRE demand $($demandGuid.ToString('N')) to AREA N1-3 (sublot $sublot).")
$null = $mes.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    workType    = 'STAGING_TO_WIRE'
    sublot      = $sublot
    area        = $area
    eqp         = $eqp
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

# 路线的两个判据在车出发之前判：计划与旅程的两端在受理时就定了。一个排反了方向的服务端，会让下面按正确方向开车的驱动在
# 第一站就等不到到站采信而超时；判据若放在整趟之后，就只剩「没走完」，说不出错在哪。
$judgeRoute = {
    param($originIntent)

    $early = Get-L2DemandJourneySnapshots $connection $demandId
    $firstPlan = @($early | Where-Object { $_.Type -eq 'UpcomingStopPlanSnapshot' }) | Select-Object -First 1
    $planShape = if ($null -ne $firstPlan) {
        (@($firstPlan.Legs | ForEach-Object { "$($_.sequence):$($_.legType)@$($_.stationId)" }) -join ',')
    } else { '(no plan)' }
    $functions = @(@($firstPlan) | Where-Object { $null -ne $_ } | ForEach-Object { @($_.Legs) } |
        Where-Object { Test-L2RealPresent $_.publicStationFunction })
    $assertions.Add(
        'G3-11-01',
        '计划按任务类型规则排腿：第一腿 TO_PICKUP 到派工待送站、第二腿 TO_DROPOFF 到 AREA 机台，publicStationFunction 为空（DERIVE_DIRECTION_FROM_TASK_TYPE_RULE；车出发前判）',
        ($planShape -eq "1:TO_PICKUP@$stagingName,2:TO_DROPOFF@$areaName" -and $functions.Count -eq 0),
        "1:TO_PICKUP@$stagingName,2:TO_DROPOFF@$areaName / publicStationFunction 非空 0 条",
        "$planShape / publicStationFunction 非空 $($functions.Count) 条")

    # Invoke-L2Query returns its rows as one wrapped array; @() around the call would count that wrapper as one row.
    $runtime = Invoke-L2Query -Connection $connection -Sql (
        "SELECT PickupStationId, PickupStationRiotId, GateStationId, GateStationRiotId, RouteEvidenceId " +
        "FROM JourneyRuntimes WHERE DemandId = '$demandId'")
    $riotSnapshot = $riot.Snapshot().body
    $firstOrder = @(@($riotSnapshot.orders) | Where-Object { [string]$_.upperId -eq [string]$originIntent.UpperId }) |
        Select-Object -First 1
    $endsRecorded = if ($runtime.Count -eq 1) {
        "$($runtime[0].PickupStationId)/$($runtime[0].PickupStationRiotId) → $($runtime[0].GateStationId)/$($runtime[0].GateStationRiotId)"
    } else { "($($runtime.Count) journey rows)" }
    $firstOrderTo = if ($null -ne $firstOrder) { Get-OrderDestination $firstOrder } else { $null }

    # 路线证据独立重算。「非空」对这一条几乎没有判别力：把两端喂反、漏掉一个输入、换掉拼法，服务端存进去的
    # 仍然是一个非空的 MAPCAT-…，而这趟正是「方向不能反」那条产品断言的现场。重算用场景自己知道的两端、AREA
    # 与 EQP，加上假 RIoT 此刻的站点目录。
    #
    # 前提：本场景中途不改站点表（setup.psd1 一次性设好，此后没有 Stations 命令），所以此刻的目录就是服务端
    # 受理这趟时读到的那一份。哪天有场景在途中改站点，这条重算要先取当时的目录。
    #
    # 拼法与服务端一致由两处钉死：JourneyPlanCharacterizationTests.TheRouteEvidenceIdOfAWireToGateCandidateIsPinned
    # 钉指纹到 id 那一半，HttpRiotMovementGatewayTests 钉站点到指纹那一半，scripts/l2/Test-L2RouteEvidence.ps1
    # 用同样两对值断言这边的复刻。改了服务端任一边的拼法，它自己的测试先红。
    $mapEntry = @(@($riotSnapshot.maps) | Where-Object { [int]$_.mapId -eq $mapId }) | Select-Object -First 1
    $plannedEvidence = '(no station catalog)'
    $swappedEvidence = '(no station catalog)'
    if ($null -ne $mapEntry) {
        $catalogSha = Get-L2MapCatalogSha256 -MapId $mapId -Stations @($mapEntry.stations)
        $plannedEvidence = Get-L2RouteEvidenceId -MapId $mapId -CatalogSha256 $catalogSha `
            -OriginStationRiotId $stagingRiotId -OriginStationName $stagingName `
            -DestinationStationRiotId $areaRiotId -DestinationStationName $areaName -Area $area -Eqp $eqp
        # 反着再算一遍。两个值必须不同——相等的话「重算对上了」只说明这个哈希对方向不敏感，那条比对就什么也没判。
        $swappedEvidence = Get-L2RouteEvidenceId -MapId $mapId -CatalogSha256 $catalogSha `
            -OriginStationRiotId $areaRiotId -OriginStationName $areaName `
            -DestinationStationRiotId $stagingRiotId -DestinationStationName $stagingName -Area $area -Eqp $eqp
    }
    $recordedEvidence = if ($runtime.Count -eq 1) { [string]$runtime[0].RouteEvidenceId } else { '(no journey row)' }
    $shortId = { param([string]$Value) if ($Value.Length -gt 21) { $Value.Substring(0, 21) + '…' } else { $Value } }
    $assertions.Add(
        'G3-11-07',
        '起终点没有互换：旅程记下的起点是派工待送站、终点是 AREA 机台，只有一行；路线证据等于按计划方向独立重算出来的那个 id，而把两端互换重算会得到另一个 id；RIoT 上第一张单开往派工待送站（NEVER_SWAP_ORIGIN_AND_DESTINATION；车出发前判，第二张单的目的站在 G3-11-06）',
        ($runtime.Count -eq 1 -and [string]$runtime[0].PickupStationId -eq $stagingName -and [int]$runtime[0].PickupStationRiotId -eq $stagingRiotId -and
            [string]$runtime[0].GateStationId -eq $areaName -and [int]$runtime[0].GateStationRiotId -eq $areaRiotId -and
            $recordedEvidence -ceq $plannedEvidence -and $plannedEvidence -cne $swappedEvidence -and
            $firstOrderTo -eq $stagingRiotId),
        "$stagingName/$stagingRiotId → $areaName/$areaRiotId / 路线证据 = 正向重算 $(& $shortId $plannedEvidence)、≠ 反向重算 $(& $shortId $swappedEvidence) / 第一张单 → $stagingRiotId",
        "$endsRecorded / 路线证据 $(& $shortId $recordedEvidence) / 第一张单 → $(if ($null -ne $firstOrderTo) { $firstOrderTo } else { '(no order)' })")
}

# 车按路线应有的方向被送去两站：先派工待送站、后 AREA 机台。服务端有没有这样排，由判据从它自己的快照和单上读。
$journey = Invoke-L2TaskTypeJourney -Context $Context -DemandId $demandId -Sublot $sublot `
    -OriginRiotId $stagingRiotId -DestinationRiotId $areaRiotId -BeforeFirstArrival $judgeRoute

$snapshots = Get-L2DemandJourneySnapshots $connection $demandId
$described = (@($snapshots | ForEach-Object { "$(Format-L2JourneySnapshot $_) ack=$($_.Acknowledged) fenced=$($_.Fenced)" }) -join ' | ')

# --- 2. 服务端按规则定方向 -----------------------------------------------------------------------------------------

$worklistAt = {
    param([string]$station)
    @($snapshots | Where-Object { $_.Type -eq 'CurrentStopWorklistSnapshot' -and $_.StationId -eq $station -and @($_.Items).Count -ge 1 }) |
        Select-Object -First 1
}
$atStaging = & $worklistAt $stagingName
$atArea = & $worklistAt $areaName
$roleOf = { param($w) if ($null -eq $w) { '(no worklist)' } else { (@($w.Items | ForEach-Object { "$($_.stopRole)/$($_.workType)" }) -join ',') } }
$assertions.Add(
    'G3-11-02',
    '清单的 stopRole 跟着计划走：派工待送站那份是 PICKUP、AREA 机台那份是 DROPOFF，任务类型都是 STAGING_TO_WIRE',
    ((& $roleOf $atStaging) -eq 'PICKUP/STAGING_TO_WIRE' -and (& $roleOf $atArea) -eq 'DROPOFF/STAGING_TO_WIRE'),
    "$stagingName PICKUP/STAGING_TO_WIRE / $areaName DROPOFF/STAGING_TO_WIRE",
    "$stagingName $(& $roleOf $atStaging) / $areaName $(& $roleOf $atArea)")

# 向量四步：计划 → 确认 → 清单 → 确认。第一份是计划，它之后才有清单；这一趟的每一份都被真车载端确认、没有一份作废。
$firstWorklist = @($snapshots | Where-Object { $_.Type -eq 'CurrentStopWorklistSnapshot' }) | Select-Object -First 1
$assertions.Add(
    'G3-11-03',
    '消息顺序与向量一致：先发计划、被确认，之后才发清单、被确认；这一趟的计划与清单全部被真车载端确认，没有一份被作废',
    ($snapshots.Count -ge 2 -and $snapshots[0].Type -eq 'UpcomingStopPlanSnapshot' -and $null -ne $firstWorklist -and
        $firstWorklist.At -ge $snapshots[0].At -and @($snapshots | Where-Object { $_.Fenced -or -not $_.Acknowledged }).Count -eq 0),
    '计划在前 / 全部 ack、无作废',
    $described)

# --- 3. 车载端按计划显示方向 ---------------------------------------------------------------------------------------

$atOrigin = $journey.StopFacts['at-origin']
$atDestination = $journey.StopFacts['at-destination']
$assertions.Add(
    'G3-11-04',
    '车载端在派工待送站显示「取货」：StopDirection 含「取」、不含「卸」，TaskType 已显示（DISPLAY_DIRECTION_AS_PLANNED；不比文案全文）',
    ($null -ne $atOrigin -and (Test-L2RealPresent $atOrigin.Direction) -and $atOrigin.Direction.Contains('取') -and
        -not $atOrigin.Direction.Contains('卸') -and (Test-L2RealPresent $atOrigin.TaskType)),
    'StopDirection 含「取」 / TaskType 非空',
    (Format-L2StopFacts $atOrigin))
$assertions.Add(
    'G3-11-05',
    '车载端在 AREA 机台显示「卸货」：StopDirection 含「卸」、不含「取」，TaskType 已显示（DISPLAY_DIRECTION_AS_PLANNED；不按任务类型推）',
    ($null -ne $atDestination -and (Test-L2RealPresent $atDestination.Direction) -and $atDestination.Direction.Contains('卸') -and
        -not $atDestination.Direction.Contains('取') -and (Test-L2RealPresent $atDestination.TaskType)),
    'StopDirection 含「卸」 / TaskType 非空',
    (Format-L2StopFacts $atDestination))

# --- 4. 装货在派工待送站、卸货在 AREA 机台，开的都是目标仓 ------------------------------------------------------------

$entryRequests = @((Get-L2RealOutbound $connection 'SublotEntryRequested') | Where-Object { $_.At -ge $journey.OriginArrivedAt })
$entryStations = @($entryRequests | ForEach-Object { [string]$_.Payload.stationId } | Sort-Object -Unique)
$commands = @((Get-L2RealOutbound $connection 'SlotOperationCommand') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
$loadCommand = @($commands | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $journey.Load.AttemptId }) | Select-Object -First 1
$unloadCommand = @($commands | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $journey.Unload.AttemptId }) | Select-Object -First 1
$targets = Format-L2RealSlots $journey.Load.TargetSlots
$loadWhere = if ($null -ne $loadCommand) {
    if ($loadCommand.At -ge $journey.OriginArrivedAt -and $loadCommand.At -lt $journey.DestinationDepartedAt) { 'staging' } else { "outside ($($loadCommand.At.ToString('O')))" }
} else { '(no command)' }
$unloadWhere = if ($null -ne $unloadCommand) {
    if ($unloadCommand.At -ge $journey.DestinationArrivedAt) { 'area' } else { "outside ($($unloadCommand.At.ToString('O')))" }
} else { '(no command)' }
$orders = @($riot.Snapshot().body.orders)
$secondOrder = @($orders | Where-Object { [string]$_.upperId -eq [string]$journey.DestinationIntent.UpperId }) | Select-Object -First 1
$secondOrderTo = if ($null -ne $secondOrder) { Get-OrderDestination $secondOrder } else { $null }
$loadSlots = if ($null -ne $loadCommand) { Format-L2RealSlots @($loadCommand.Payload.slots) } else { '' }
$unloadSlots = if ($null -ne $unloadCommand) { Format-L2RealSlots @($unloadCommand.Payload.slots) } else { '' }
$assertions.Add(
    'G3-11-06',
    '装货在派工待送站、卸货在 AREA 机台：录入请求的站点是派工待送站，装货命令在车到派工待送站之后、出发去机台之前发出，第二张 RIoT 单开往 AREA 机台，卸货命令在车到机台之后发出；两条命令的 slots 都是目标仓，模拟器上装与卸开的是同一个目标仓',
    ($entryStations.Count -eq 1 -and $entryStations[0] -eq $stagingName -and $loadWhere -eq 'staging' -and
        $secondOrderTo -eq $areaRiotId -and $unloadWhere -eq 'area' -and
        $loadSlots -eq $targets -and $unloadSlots -eq $targets -and
        $journey.Load.OpenedSlot -in $journey.Load.TargetSlots -and $journey.Unload.OpenedSlot -eq $journey.Load.OpenedSlot),
    "录入@$stagingName / 装 staging / 第二张单 → $areaRiotId / 卸 area / slots $targets / 开仓相同",
    "录入@$($entryStations -join ',') / 装 $loadWhere / 第二张单 → $(if ($null -ne $secondOrderTo) { $secondOrderTo } else { '(no order)' }) / 卸 $unloadWhere / 装 slots $loadSlots 卸 slots $unloadSlots 目标 $targets / 开仓 装 $($journey.Load.OpenedSlot) 卸 $($journey.Unload.OpenedSlot)")

# --- 5. 准入冻结在卸货那次（推翻 I6） ---------------------------------------------------------------------------

$admissions = Invoke-L2Query -Connection $connection -Sql (
    "SELECT SlotOperationAttemptId, StationId, TaskType, Allowed FROM AdmissionDecisionSnapshots " +
    "WHERE SlotOperationAttemptId IN ('$($journey.Load.AttemptId)', '$($journey.Unload.AttemptId)')")
$onUnload = @($admissions | Where-Object { [string]$_.SlotOperationAttemptId -eq $journey.Unload.AttemptId })
$onLoad = @($admissions | Where-Object { [string]$_.SlotOperationAttemptId -eq $journey.Load.AttemptId })
$assertions.Add(
    'G3-11-08',
    '站点任务类型准入冻结在卸货那次操作上：AdmissionDecisionSnapshots 里卸货那次有一行（AREA 机台站、STAGING_TO_WIRE、放行），装货那次没有（推翻 I6，准入跟着 AREA 端那条腿走）',
    ($onUnload.Count -eq 1 -and [string]$onUnload[0].StationId -eq $areaName -and [string]$onUnload[0].TaskType -eq 'STAGING_TO_WIRE' -and
        [int]$onUnload[0].Allowed -eq 1 -and $onLoad.Count -eq 0),
    "卸货 1 行 $areaName/STAGING_TO_WIRE/放行 / 装货 0 行",
    "卸货 $($onUnload.Count) 行$(if ($onUnload.Count) { " $($onUnload[0].StationId)/$($onUnload[0].TaskType)/Allowed=$($onUnload[0].Allowed)" }) / 装货 $($onLoad.Count) 行")

# --- 6. 终态 ---------------------------------------------------------------------------------------------------

$demandStatus = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'"
$commits = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId' AND Status = 'Committed'"
$operationsTotal = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId'"
$slotReading = Get-L2RealSlotReading $simulator $journey.Unload.OpenedSlot
$assertions.Add(
    'G3-11-09',
    '终态 NO_DUPLICATE_COMMIT / NO_UNPROVEN_STATE：需求 Succeeded、旅程 Completed，两张 RIoT 单、两笔仓位操作都 Committed，卸完的仓关门、空、锁上、开锁输出复位',
    ($demandStatus -eq 'Succeeded' -and $journey.Stage -eq 'Completed' -and $orders.Count -eq 2 -and $operationsTotal -eq 2 -and
        $commits -eq 2 -and $slotReading -eq 'CLOSED/EMPTY/1/0'),
    'Succeeded / Completed / 2 单 / 2 操作 2 Committed / CLOSED/EMPTY/1/0',
    "$demandStatus / $($journey.Stage) / $($orders.Count) 单 / $operationsTotal 操作 $commits Committed / 仓 $($journey.Unload.OpenedSlot) $slotReading")

$journal.Note('FP-IS-11: STAGING_TO_WIRE ran staging station first and AREA machine second, on the real onboard.')
