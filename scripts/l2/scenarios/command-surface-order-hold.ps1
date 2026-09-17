#Requires -Version 7

<#
命令面的三条断言：该调用时调用了、参数正确、只调一次。

规格 8.3 轨 B 那一行把这三条并列，并且点名「只调一次」最重要——急停重试与对账很容易变成重复
发令，而重复发令在厂区里是两次真实动作。

**触发点是 RIoT 把本服务端创建的在途单报成终态 FAILED**，`REQ-0232` 五个症状里的一个。选它是
因为它自带故障模型需要的两样东西：一台有在途单的车（`OrderHold` 才有目标），以及一张
`ResumeAsync` 日后问得到的单（故障才有清除路径）。

**「只调一次」按审计表的 attempt 行数判定，不按假 RIoT 的请求数。**两者在正常情况下一致，但
分不清「重试」与「重复发令」的正是后者：审计表的唯一索引是
`(CommandType, TargetUpperId, AttemptNumber)`，一次重试是一行新的 attempt，所以「调了一次」
与「调了三次」是不同的行，而不是一件要靠信任的事。两个数都记进证据，一致本身也是一条判据。

**它在这里是结构性成立的，不是碰巧。**对账回读订单，读到 4 FAILED——终态，且不是 `OrderHold`
要的 7 PAUSED——判为 `Failed`：命令没做到它要做的事，且再也做不到。协调器因此不再重发。
若哪天有人把这条规则改回「非 Confirmed 一律重发」，本条会红，而且是每轮加一行地红。
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

# 出事的那台车，以及不该被牵连的那两台。
$faultedAgvId = 'AGV-L2-002'
$bystanderAgvIds = @('AGV-L2-001', 'AGV-L2-003')

function Get-Journeys {
    return Invoke-L2Query -Connection $connection -Sql @'
SELECT AgvId, VehicleKey, DemandId, Stage, PickupUpperId, PickupStationRiotId, BlockReasonCode
FROM JourneyRuntimes ORDER BY AgvId
'@
}

function Get-JourneyOf([string]$agvId) {
    # 先赋值再过滤，不要写成 `Get-Journeys | Where-Object ...`。`Invoke-L2Query` 用 `return , $rows`
    # 保住整张结果集，而这个包装**穿得过一层 return**：管道里拿到的是一个元素、那个元素是整张
    # 结果集，`$_.AgvId` 于是成员展开成三个值拼成一行，一条也匹配不上。赋值给变量会展开外面那
    # 层，再管道就正常了。这条第一次跑就撞上，症状是「三趟 journey 都在库里，却一趟都找不到」。
    $rows = Get-Journeys
    return $rows | Where-Object { [string]$_.AgvId -eq $agvId } | Select-Object -First 1
}

function Get-HoldAttempts {
    return Invoke-L2Query -Connection $connection -Sql @'
SELECT CommandType, AgvId, TargetUpperId, TargetOrderId, AttemptNumber, Outcome, FaultGeneration
FROM RiotOrderCommandAudit ORDER BY AttemptNumber
'@
}

function Get-IntentOf([string]$demandId, [string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# --- 1. 三台车各自上路 ----------------------------------------------------------------------------

foreach ($index in 0..2) {
    $guid = [guid]::NewGuid()
    $journal.Note("Publishing demand $($guid.ToString('N')).")
    $null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot      = "L2-CS-$($Context.RunId)-$index"
        area        = 'N1-3'
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

$null = Wait-L2Condition -Description 'all three vehicles took a journey' `
    -Journal $journal -Criterion 'journeys-dispatched' -TimeoutSeconds 120 `
    -Probe { (Get-Journeys).Count } -Until { param($v) $v -ge 3 }

$faulted = Get-JourneyOf $faultedAgvId
$faultedIntent = Wait-L2Condition -Description "the TO_PICKUP intent for $faultedAgvId was confirmed" `
    -Journal $journal -Criterion 'faulted-intent' -TimeoutSeconds 90 `
    -Probe {
        $row = Get-IntentOf ([string]$faulted.DemandId) 'TO_PICKUP'
        if ($row -and [string]$row.Status -eq 'CONFIRMED') { $row } else { $null }
    } `
    -Until { param($v) $null -ne $v }

# --- 2. 出事之前，一条命令都没发过 ----------------------------------------------------------------

# 「该调用时调用了」的负半条。少了它，一条从头到尾都在发命令的服务端也能让下面三条全绿。
$before = Get-HoldAttempts
$assertions.Add(
    'L2-CS-01',
    '在途单还正常时，命令面一次都没被调用过',
    ($before.Count -eq 0),
    0,
    $before.Count)

$invocationsBefore = @($riot.Snapshot().body.commandInvocations).Count
$assertions.Add(
    'L2-CS-02',
    '假 RIoT 侧也没收到过任何命令请求',
    ($invocationsBefore -eq 0),
    0,
    $invocationsBefore)

# --- 3. RIoT 把这一张在途单报成 FAILED --------------------------------------------------------------

# 车没动过，仍然停在关卡站上：`REQ-0247` 的停稳判定因此成立，`REQ-0246` 的升级不触发。这不是
# 为了让场景好看——一台停着的车报了单失败，正确处置就是保护订单与货物并等人，而不是发急停。
$journal.Note("RIoT reports $($faultedIntent.UpperId) FAILED on $faultedAgvId.")
$null = $riot.Command('Put', "orders/$($faultedIntent.UpperId)", @{ orderState = 4 })

$attempts = Wait-L2Condition -Description 'the fault model held the failed order' `
    -Journal $journal -Criterion 'hold-attempts' -TimeoutSeconds 90 `
    -Probe { (Get-HoldAttempts).Count } -Until { param($v) $v -ge 1 }

$rows = Get-HoldAttempts
$hold = $rows | Select-Object -First 1

$assertions.Add(
    'L2-CS-03',
    '该调用时调用了：单被报成 FAILED 之后，命令面发出了一条命令',
    ($rows.Count -ge 1),
    '>= 1',
    $rows.Count)

# 参数正确，逐项判而不是拼一个串比对：错的是哪一项，决定人要去看哪台车。
$assertions.Add(
    'L2-CS-04',
    '参数正确：命令是 OrderHold（REQ-0234 明令不得用 Cancel）',
    ([string]$hold.CommandType -eq 'OrderHold'),
    'OrderHold',
    [string]$hold.CommandType)

$assertions.Add(
    'L2-CS-05',
    "参数正确：命令记在 $faultedAgvId 名下",
    ([string]$hold.AgvId -eq $faultedAgvId),
    $faultedAgvId,
    [string]$hold.AgvId)

$assertions.Add(
    'L2-CS-06',
    '参数正确：目标是这一趟自己的 upperId（审计的键）',
    ([string]$hold.TargetUpperId -eq [string]$faultedIntent.UpperId),
    [string]$faultedIntent.UpperId,
    [string]$hold.TargetUpperId)

$assertions.Add(
    'L2-CS-07',
    '参数正确：目标带着 RIoT 自己的 orderId（端点的地址），两个身份都在',
    ([string]$hold.TargetOrderId -eq [string]$faultedIntent.OrderId),
    [string]$faultedIntent.OrderId,
    [string]$hold.TargetOrderId)

# 到 RIoT 那一侧再确认一次：审计表证的是本服务端记了什么，`commandInvocations` 证的是线上真的
# 发生过什么。两者对不上的那种缺陷，只看一边永远看不见。
#
# 要等，不能在审计行出现后立刻读。`RiotOrderCommandService.IssueAsync` 先 `ArmAttemptAsync` 把审计行
# 提交进库，再发 HTTP 请求给 RIoT；上面等的是审计行，所以此刻请求可能还在路上。立刻读就会偶发读到空：
# CI run 35109762084 第 3/3 次就是这样红的——这条判据读到「无 CMD_ORDER_HELD」，同一次运行 8 轮之后的
# L2-CS-11 却读到恰好一条，命令其实发了。判据的实际值取自等待的返回值（README 的读取纪律）。等不到仍按
# 失败记：线上真的没收到，就是这条判据要抓的缺陷。
$holdCallsSeen = $null
try {
    $holdCallsSeen = Wait-L2Condition -Description 'the fake RIoT received the CMD_ORDER_HELD the audit row announced' `
        -Journal $journal -Criterion 'riot-hold-received' -TimeoutSeconds 30 `
        -Probe {
            $calls = @(@($riot.Snapshot().body.commandInvocations) |
                Where-Object { [string]$_.commandType -eq 'CMD_ORDER_HELD' })
            [pscustomobject]@{
                Count  = $calls.Count
                Target = if ($calls.Count -gt 0) { [string]$calls[0].target } else { $null }
            }
        } `
        -Until { param($v) $v.Count -ge 1 }
}
catch {
    $journal.Note("No CMD_ORDER_HELD reached the fake RIoT within 30 s: $($_.Exception.Message)")
}
$assertions.Add(
    'L2-CS-08',
    '参数正确：线上收到的是 CMD_ORDER_HELD，打在这一张 orderId 上',
    ($null -ne $holdCallsSeen -and $holdCallsSeen.Count -eq 1 -and
        $holdCallsSeen.Target -eq [string]$faultedIntent.OrderId),
    "CMD_ORDER_HELD @ $([string]$faultedIntent.OrderId)",
    $(if ($null -eq $holdCallsSeen) { '(30 秒内无 CMD_ORDER_HELD)' }
      else { "$($holdCallsSeen.Count) 条，首条打在 $($holdCallsSeen.Target)" }))

# --- 4. 只调一次 ----------------------------------------------------------------------------------

# 让运行时再来若干轮，而不是睡一段时间：要证的是「又评估了 N 次仍然没有第二条」，而轮数正是
# 那个 N。睡秒数在慢机器上会退化成「只评估了一轮」，那样这条断言什么都没证。
$null = Wait-L2Iterations -Riot $riot -Count 8 -TimeoutSeconds 120 -Journal $journal

$rowsAfter = Get-HoldAttempts
$assertions.Add(
    'L2-CS-09',
    '只调一次：又过了 8 轮评估，审计表仍然只有一行 attempt',
    ($rowsAfter.Count -eq 1),
    1,
    $rowsAfter.Count)

$assertions.Add(
    'L2-CS-10',
    '只调一次：那一行的 AttemptNumber 是 1（重试会是新的一行，不是覆盖）',
    ([int]$rowsAfter[0].AttemptNumber -eq 1),
    1,
    [int]$rowsAfter[0].AttemptNumber)

$holdCallsAfter = @(@($riot.Snapshot().body.commandInvocations) |
    Where-Object { [string]$_.commandType -eq 'CMD_ORDER_HELD' })
$assertions.Add(
    'L2-CS-11',
    '只调一次：RIoT 侧也只收到过一条 CMD_ORDER_HELD，两边计数一致',
    ($holdCallsAfter.Count -eq 1),
    1,
    $holdCallsAfter.Count)

# 停着的车不该被急停。这一条与上面三条一起看才完整：一台在第一轮就被急停的车同样满足「只调
# 一次 OrderHold」，但那是完全不同的一件事。
$emergencyCalls = @(@($riot.Snapshot().body.commandInvocations) |
    Where-Object { [string]$_.commandType -in @('triggerEmergency', 'cancelEmergency') })
$assertions.Add(
    'L2-CS-12',
    '停在站上的车没有被升级成急停（REQ-0246 的第三个条件不成立）',
    ($emergencyCalls.Count -eq 0),
    0,
    $emergencyCalls.Count)

# --- 5. 故障事实与隔离 ----------------------------------------------------------------------------

$faults = Invoke-L2Query -Connection $connection `
    -Sql 'SELECT AgvId, Level, EvidenceCode, EscalatedAt FROM VehicleFaultStates ORDER BY AgvId'
$assertions.Add(
    'L2-CS-13',
    "只有 $faultedAgvId 有故障事实，另外两台一条都没有",
    ($faults.Count -eq 1 -and [string]$faults[0].AgvId -eq $faultedAgvId),
    "1 行 / $faultedAgvId",
    "$($faults.Count) 行 / $(if ($faults.Count -ge 1) { [string]$faults[0].AgvId } else { '(无)' })")

$assertions.Add(
    'L2-CS-14',
    '故障停在第一级 SuspectedBlocked：一个症状不越级成隔离（REQ-0232）',
    ($faults.Count -eq 1 -and [string]$faults[0].Level -eq 'SuspectedBlocked'),
    'SuspectedBlocked',
    $(if ($faults.Count -ge 1) { [string]$faults[0].Level } else { '(无行)' }))

$assertions.Add(
    'L2-CS-15',
    '证据码是 VEHICLE_ORDER_FAILED，能追到具体是哪个症状',
    ($faults.Count -eq 1 -and [string]$faults[0].EvidenceCode -eq 'VEHICLE_ORDER_FAILED'),
    'VEHICLE_ORDER_FAILED',
    $(if ($faults.Count -ge 1) { [string]$faults[0].EvidenceCode } else { '(无行)' }))

$faultedNow = Get-JourneyOf $faultedAgvId
$assertions.Add(
    'L2-CS-16',
    "$faultedAgvId 的 journey 把停住的原因写成 VEHICLE_ORDER_FAILED，而不是留空",
    ([string]$faultedNow.BlockReasonCode -eq 'VEHICLE_ORDER_FAILED'),
    'VEHICLE_ORDER_FAILED',
    [string]$faultedNow.BlockReasonCode)

foreach ($agvId in $bystanderAgvIds) {
    $journey = Get-JourneyOf $agvId
    $assertions.Add(
        "L2-CS-17-$agvId",
        "$agvId 不受牵连：仍在自己的到站阶段，没有阻断原因",
        ($null -ne $journey -and
            [string]$journey.Stage -eq 'AwaitingPickupArrival' -and
            ($null -eq $journey.BlockReasonCode -or [string]$journey.BlockReasonCode -eq '')),
        'AwaitingPickupArrival / (无阻断原因)',
        $(if ($null -eq $journey) { '(无 journey)' }
          else { "$([string]$journey.Stage) / $(if ($null -eq $journey.BlockReasonCode) { '(无)' } else { [string]$journey.BlockReasonCode })" }))
}

# 旁观的两台车还能继续走：故障事实挡的是那一台的新派车，不是整个车队的执行。
$bystander = Get-JourneyOf $bystanderAgvIds[0]
$bystanderIntent = Get-IntentOf ([string]$bystander.DemandId) 'TO_PICKUP'
$null = $riot.Command('Put', "orders/$($bystanderIntent.UpperId)", @{
    orderState        = 3
    executeVehicleKey = [string]$bystander.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = [string]$bystander.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = [int]$bystander.PickupStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($bystanderIntent.UpperId)", @{ orderState = 5 })

$bystanderStage = Wait-L2Condition -Description "$($bystanderAgvIds[0]) kept going past the faulted vehicle" `
    -Journal $journal -Criterion 'bystander-stage' -TimeoutSeconds 180 `
    -Probe {
        $row = Get-JourneyOf $bystanderAgvIds[0]
        if ($null -eq $row) { return $null }
        return [string]$row.Stage
    } `
    -Until { param($v) $v -eq 'AwaitingGateArrival' }

$assertions.Add(
    'L2-CS-18',
    "$($bystanderAgvIds[0]) 在另一台车带着故障事实的同时照常取货装载",
    ($bystanderStage -eq 'AwaitingGateArrival'),
    'AwaitingGateArrival',
    $bystanderStage)

# 那一台车走完取货装载又发了不少轮，命令面仍然只有那一行。
$rowsFinal = Get-HoldAttempts
$assertions.Add(
    'L2-CS-19',
    '全程命令面只有一行 attempt，旁观车的推进没有带出任何额外命令',
    ($rowsFinal.Count -eq 1),
    1,
    $rowsFinal.Count)

$journal.Note('命令面：该调用时调用了、参数正确、只调一次，且只作用于出事的那一台车。')
