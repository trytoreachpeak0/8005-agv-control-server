#Requires -Version 7

<#
装载指令根本不被应答：旅程停在 AwaitingLoadResult，而且**不进 Blocked**。

这条与 `load-result-requires-recovery` 是**两件不同的事**，方案第 4 节装载表把它们列成两行正是因为
容易混为一谈：

| | 车载端做了什么 | 服务端看到 | 结果 |
| --- | --- | --- | --- |
| `load-result-requires-recovery` | 跑掉自己的操作员超时，上报一份不完美的 `OperationResult` | 一份 `completed=false` 的结果 | 操作判 `RecoveryRequired`，旅程进 `Blocked / LOAD_RESULT_REQUIRES_RECOVERY` |
| **本场景** | 什么都不发 | 什么都没有 | 操作停在 `Prepared`，旅程停在 `AwaitingLoadResult`，**不进 `Blocked`** |

差别落在 `JourneyRuntimeEngine` 的 `AwaitingLoadResult` 分支上（`JourneyRuntimeEngine.cs:430`）：它
只在操作转 `RecoveryRequired` 时 `Block()`、转 `Committed` 时推进，其余一律 `return`。所以「没有结果」
不是一种失败状态，是一种**什么都没发生**的状态——旅程无限期地等下去。

**这是对的，不是缺陷。**服务端不能凭「对端一直没回话」就断定仓门和货物是什么状态；ADR-cross-0006
要求仓位物理事实必须经证实。但它的诊断价值与 `Blocked` 完全不同：`Blocked` 会带一个
`BlockReasonCode` 说明在等哪一种恢复，而这里现场看到的只是「卡着」。这条场景把这个区别钉住，免得
将来有人"修"掉其中一个的时候顺手把另一个也改了。

用合成对端的 `Silent` 策略（`AnswerMode.Silent`：记录请求但永不应答）。**这是装载那一整批故障场景
里唯一不需要真装置的一条**——其余八条（锁不上、假装锁上、光幕、放错仓位、IO 断链/变慢/无响应、
放货后又取走）的注入端点都在模拟器上，而模拟器只在 `Onboard = 'Real'` 时启动。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

function Get-Runtime {
    # HasConversion<string>：这些列存的是枚举成员名，按序数读会抛异常。
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Stage, BlockReasonCode FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stage {
    $runtime = Get-Runtime
    if ($null -eq $runtime) { return $null }
    return [string]$runtime.Stage
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-PendingOperationKey {
    $pending = @($onboard.Snapshot().body.pending |
        Where-Object { $_.messageType -eq 'SlotOperationCommand' })
    if ($pending.Count -eq 0) { return $null }
    return [string]$pending[0].key
}

# 服务端每轮都会重放未结命令，所以这个计数是「它还在坚持」的可观测量。`pending` 是按 key 的字典，
# 重放只会覆盖同一项，数不出重放次数——只有 wire 能。
#
# `direction` 的取值是 `in` / `out`（`WireEvent`，从对端自己的视角说：`in` 是它收到的），不是
# `inbound` / `outbound`。第一次就写成了后者，于是计数恒为 0——判据红了，但红的是过滤器不是产品。
function Get-LoadCommandWire {
    return @($onboard.Snapshot().body.wire |
        Where-Object { $_.direction -eq 'in' -and $_.messageType -eq 'SlotOperationCommand' })
}

function Get-LoadCommandSends {
    return (Get-LoadCommandWire).Count
}

# --- 1. 走到装载指令下发为止，和 normal-load 一样 ---------------------------------------------------

# 唯一注入的一处：装载结果永不应答。其余三类应答保持 Auto。
$null = $onboard.Command('Put', 'policy', @{ loadResult = 'Silent' })

$journal.Note("Publishing demand $demandIdWire (sublot $sublot).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-UpperId -purpose 'TO_PICKUP'; if ($row -and $row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }

$journal.Note('Vehicle departs for the pickup station.')
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $pickupIntent.OrderId
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
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 5 })

# --- 2. 装载指令到了车载端，然后什么都没有发生 -----------------------------------------------------

$operationKey = Wait-L2Condition -Description 'the load command reached the peer and is waiting for a result' `
    -Journal $journal -Criterion 'pending-operation' -TimeoutSeconds 120 `
    -Probe { Get-PendingOperationKey } -Until { param($v) $null -ne $v }
$journal.Note("Load command $operationKey reached the peer, which will never answer it.")

$stage = Get-Stage
$assertions.Add(
    'L2-LN-01', '装载指令已下发，旅程在等结果',
    ($stage -eq 'AwaitingLoadResult'), 'AwaitingLoadResult', $stage)

$sendsBefore = Get-LoadCommandSends

# 否定判据要有界：让运行时确实又跑几轮，再说它没有把旅程推走。不用 sleep。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal

# --- 3. 核心：停在 AwaitingLoadResult，而不是 Blocked ----------------------------------------------

$runtime = Get-Runtime
$assertions.Add(
    'L2-LN-02', '旅程仍停在 AwaitingLoadResult（没有进 Blocked）',
    ([string]$runtime.Stage -eq 'AwaitingLoadResult'), 'AwaitingLoadResult', [string]$runtime.Stage)

# 这一条是与 load-result-requires-recovery 的分界：那条会有 LOAD_RESULT_REQUIRES_RECOVERY，这条不该有
# 任何阻塞原因，因为它根本没有进入阻塞。
$blockReason = [string]$runtime.BlockReasonCode
$assertions.Add(
    'L2-LN-03', '没有阻塞原因码（Blocked 与「没有结果」是两种状态）',
    ([string]::IsNullOrEmpty($blockReason)), '(none)', $(if ([string]::IsNullOrEmpty($blockReason)) { '(none)' } else { $blockReason }))

$loadRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'"
$loadStatus = $(if ($loadRows.Count -eq 1) { [string]$loadRows[0].Status } else { '(no load row)' })
$assertions.Add(
    'L2-LN-04', '装载操作停在 Prepared（不是 RecoveryRequired，也不是 Committed）',
    ($loadRows.Count -eq 1 -and $loadStatus -eq 'Prepared'), 'Prepared', $loadStatus)

# 没有结果就没有已证实的仓位事实，所以需求不该被判成任何终态，也不该被判成需要恢复。
$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
$demandStatus = $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' })
$assertions.Add(
    'L2-LN-05', '需求仍是 Accepted（既没成功，也没转恢复）',
    ($demandRows.Count -eq 1 -and $demandStatus -eq 'Accepted'), 'Accepted', $demandStatus)

# --- 4. 服务端在坚持重放，不是悄悄把命令丢了 -------------------------------------------------------

# ADR-cross-0006 与 ADR-cross-0014：未结命令每轮重放，直到对端应答。计数必须真的长了——否则
# 「旅程停着」也可以是服务端忘了这条命令造成的，那是完全不同的一种坏。
$sendsAfter = Get-LoadCommandSends
$assertions.Add(
    'L2-LN-06', '未结的装载指令在被持续重放（不是发一次就忘）',
    ($sendsAfter -gt $sendsBefore), "> $sendsBefore", $sendsAfter)

$pendingKey = Get-PendingOperationKey
$assertions.Add(
    'L2-LN-07', '重放用的是同一条命令身份（对端只有一条待答请求）',
    ($pendingKey -eq $operationKey), $operationKey, $pendingKey)

# ADR-cross-0006 / ADR-cross-0014 要的不只是「重放」，是**重放同一条消息**。每次换一个 messageId 也
# 会让上面那个计数增长，却是另一种坏：对端无法把它们认成同一次操作，会重复执行物理动作。
$distinctIds = @(Get-LoadCommandWire | ForEach-Object { [string]$_.messageId } | Sort-Object -Unique)
$assertions.Add(
    'L2-LN-08', '每次重放都是同一个 messageId（不是每轮新造一条命令）',
    ($distinctIds.Count -eq 1), 1, $distinctIds.Count)

# --- 5. 没有结果就不许往下走 -----------------------------------------------------------------------

$gateIntent = Get-UpperId -purpose 'TO_GATE'
$assertions.Add(
    'L2-LN-09', '没有装载结果就不建 TO_GATE 单',
    ($null -eq $gateIntent), '(none)', $(if ($null -eq $gateIntent) { '(none)' } else { [string]$gateIntent.Status }))

$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-LN-10', '没有为关卡段派过车（RIoT 单仍只有一条）',
    ($riotOrders.Count -eq 1), 1, $riotOrders.Count)

# 出发前安全检查属于 AwaitingDepartureSafety 阶段，旅程根本没走到那里。
$safetyChecks = @($onboard.Snapshot().body.wire |
    Where-Object { $_.messageType -eq 'PreDepartureSafetyCheck' }).Count
$assertions.Add(
    'L2-LN-11', '没有下发出发前安全检查',
    ($safetyChecks -eq 0), 0, $safetyChecks)

$journal.Note('Scenario finished: the journey waits at AwaitingLoadResult, with no block reason and no exit.')
