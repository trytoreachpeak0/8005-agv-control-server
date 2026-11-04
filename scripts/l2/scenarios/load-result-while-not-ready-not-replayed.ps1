#Requires -Version 7

<#
已出结果的装货命令不再补发（control-server#623）。

形状：装货命令发到车上之后，车报一次不能出发（会话离开 Ready），就在这段时间里车把装货结果报上来、服务端判 Committed；
车随后报回安全、会话回到 Ready。修之前，回到 Ready 的那一轮先把发件箱里还没结算的这条 SlotOperationCommand 补发一次，
然后才在 AwaitingLoadResult 分支里结算它——cs#205 真装置证据里装货结果提交约 147 毫秒后的那条重复命令就是它。生产上的放大形
（agv01 10-22 attempt 3f01992d）是结果回 RECOVERY_REQUIRED、补偿终结之后的永久补发，那一形要真车载端走恢复会话，合成对端
不发起（见 load-result-requires-recovery 文件头），L1 在 RecoveryEndingReleasesBlockedJourneyTests 里经真实入站处理器与
引擎走完；这里证同一个根因（「结果到了，命令还在补发名单里」）在整套进程上的样子。

判据：
1. 前置：装货命令到了车上、旅程在等结果；车报不安全之后服务端会话确实离开 Ready。
2. 前置：会话不就绪期间车报的装货结果被采信（装货 Committed）；车报回安全之后会话回到 Ready（第二个事实另等，README 第 14 条）。
3. 会话回到 Ready 之后再跑几轮，车再也没收到这条装货命令（同一 messageId）。缺陷版本上是 1 次。
4. 发件箱里这条命令已确认：不留到换协议身份时挡启动。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

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

function Get-Stage {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-PendingOperationKey {
    $pending = @($onboard.Snapshot().body.pending | Where-Object { $_.messageType -eq 'SlotOperationCommand' })
    if ($pending.Count -eq 0) { return $null }
    return [string]$pending[0].key
}

function Get-Readiness {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'"
    if ($rows.Count -eq 0) { return $null }
    return "$([string]$rows[0].Readiness)/$([string]$rows[0].ReasonCode)"
}

function Get-LoadStatus {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Status FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'"
    if ($rows.Count -ne 1) { return '(no load row)' }
    return [string]$rows[0].Status
}

# 车收到这条命令的次数：对端自己的线上记录（direction 'in' 是它收到的），按 messageId 数，只数 $since 之后的。
function Get-CommandReceipts([string]$messageId, [DateTimeOffset]$since) {
    return @($onboard.Snapshot().body.wire | Where-Object {
            $_.direction -eq 'in' -and $_.messageType -eq 'SlotOperationCommand' -and [string]$_.messageId -eq $messageId -and
            [DateTimeOffset]$_.at -gt $since
        }).Count
}

# --- 1. 走到装载指令下发为止，和 load-command-never-answered 一样 -----------------------------------

$null = $onboard.Command('Put', 'policy', @{ loadResult = 'Manual' })

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

$operationKey = Wait-L2Condition -Description 'the load command reached the peer and is waiting for a result' `
    -Journal $journal -Criterion 'pending-operation' -TimeoutSeconds 120 `
    -Probe { Get-PendingOperationKey } -Until { param($v) $null -ne $v }
$stage = Wait-L2ConditionOrLast -Description 'the journey recorded that it is waiting for the load result' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 30 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingLoadResult' }
$commandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT MessageId FROM ProtocolOutbox WHERE MessageType = 'SlotOperationCommand'"
$commandId = $(if ($commandRows.Count -eq 1) { [string]$commandRows[0].MessageId } else { '' })
$assertions.Add(
    'L2-RN-01', '前置：装货命令已下发（发件箱恰好一条），旅程在等结果',
    ($stage -eq 'AwaitingLoadResult' -and $commandId -ne ''), 'AwaitingLoadResult, 1 SlotOperationCommand',
    "$stage, $($commandRows.Count) SlotOperationCommand")

# --- 2. 会话离开 Ready，结果在这段时间里到 --------------------------------------------------------

$journal.Note('The peer reports it cannot depart; the session leaves Ready.')
$null = Set-L2OnboardSafety -Onboard $onboard -Connection $connection -AgvId $Context.AgvId -Journal $journal -Safety @{
    departureSafe = $false
    reasonCodes   = @('VEHICLE_NOT_READY')
}
$notReady = Wait-L2ConditionOrLast -Description 'the server took the session out of Ready' `
    -Journal $journal -Criterion 'session-readiness' -TimeoutSeconds 60 `
    -Probe { Get-Readiness } -Until { param($v) $null -ne $v -and -not $v.StartsWith('Ready/') }
$assertions.Add(
    'L2-RN-02', '前置：车报不能出发之后，会话离开 Ready',
    ($null -ne $notReady -and -not $notReady.StartsWith('Ready/')), 'not Ready', $notReady)

$journal.Note("The peer reports the load result ($operationKey) while the session is not Ready.")
$null = $onboard.Command('Put', "answer/$operationKey", @{ completed = $true })
$answeredAt = [DateTimeOffset]::UtcNow
$loadStatus = Wait-L2ConditionOrLast -Description 'the server committed the load from the result' `
    -Journal $journal -Criterion 'load-status' -TimeoutSeconds 60 `
    -Probe { Get-LoadStatus } -Until { param($v) $v -eq 'Committed' }

$journal.Note('The peer reports it is safe again; the session goes back to Ready.')
$null = Set-L2OnboardSafety -Onboard $onboard -Connection $connection -AgvId $Context.AgvId -Journal $journal -Safety @{
    departureSafe = $true
    reasonCodes   = @()
}
$ready = Wait-L2ConditionOrLast -Description 'the session is Ready again' `
    -Journal $journal -Criterion 'session-readiness' -TimeoutSeconds 60 `
    -Probe { Get-Readiness } -Until { param($v) $null -ne $v -and $v.StartsWith('Ready/') }
$assertions.Add(
    'L2-RN-03', '前置：会话不就绪期间的装货结果被采信（Committed），之后会话回到 Ready',
    ($loadStatus -eq 'Committed' -and $null -ne $ready -and $ready.StartsWith('Ready/')), 'Committed, Ready',
    "$loadStatus, $ready")

# --- 3. 回到 Ready 之后，这条命令一次也不再发 ------------------------------------------------------

# 否定判据要有界：让运行时确实又跑几轮。从车报结果那一刻数起：那之后车收到的这条命令都是补发一条已作答的命令。
# 缺陷版本上它落在回到 Ready 的第一轮，读到 Ready 之后才数会漏掉，所以起点放在作答时。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$receipts = Get-CommandReceipts $commandId $answeredAt
$assertions.Add(
    'L2-RN-04', '会话回到 Ready 之后车没有再收到这条装货命令（同一 messageId）',
    ($receipts -eq 0), 0, $receipts)

$acknowledged = Invoke-L2Query -Connection $connection `
    -Sql "SELECT AcknowledgedAt, FencedAt FROM ProtocolOutbox WHERE MessageId = '$commandId'"
$settled = $acknowledged.Count -eq 1 -and -not [string]::IsNullOrEmpty([string]$acknowledged[0].AcknowledgedAt)
$assertions.Add(
    'L2-RN-05', '发件箱里这条装货命令已确认（不会留到换协议身份时挡启动）',
    $settled, 'AcknowledgedAt set',
    $(if ($acknowledged.Count -eq 1) { "AcknowledgedAt=$([string]$acknowledged[0].AcknowledgedAt) FencedAt=$([string]$acknowledged[0].FencedAt)" } else { '(no row)' }))
