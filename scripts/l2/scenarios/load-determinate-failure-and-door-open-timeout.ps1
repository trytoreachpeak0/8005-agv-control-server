#Requires -Version 7

<#
装货中期限（批次 5，control-server#81；ADR-cross-0058 决策 4、5；program#55）。

两台合成车各接一单，两段都在装货命令已下发、本站期限已过之后：

  A. 确定失败（防御路径）：一台车的对端在期限之后报 FAILED——首仓 OPERATOR_TIMEOUT、其余 NOT_STARTED，全部
     EMPTY／LOCKED／RESET。服务端判 Failed、不进恢复，自己把需求以 CANCELLED_BY_STATION_TIMEOUT 终结，结算悬空的
     装货命令，释放租约与车辆占用；最后再发一单，由这台被释放的车接走。
     **这是防御路径：v2 车载端不产出 FAILED／OPERATOR_TIMEOUT**（期限后仓门已闭而货没动，它一直重开，
     program#55）。服务端保留结算，是因为协议允许别的车载端版本这样报；这一段只能由合成对端走到。
  B. 仓门未闭：另一台车期限过了、没有结果、会话安全证据报 LOCK_NOT_CLOSED。服务端挂 STATION_TIMEOUT_DOOR_NOT_CLOSED，
     stage 留在 AwaitingLoadResult、不转 Blocked、不结束本站，挂着期间开始时间不动；门关上告警撤销；再报 COMPLETED
     按真实结果提交。门开着时会话仍是 Ready，靠的是「本车自己的装货在途」这一条豁免——另一台车当时没有在途装货，
     按车过滤之后这条豁免只能来自本车。

门先关、结果后到，是真车载端的顺序，也是唯一走得通的顺序：结果一提交，本车就没有在途装货了，还开着的门不再有豁免，
会话随之离开 Ready。

两台车而不是一台连着跑两单：合成对端按固定键缓存录入应答，同一台车扫第二单会把第一单的录入原样重发
（control-server#121 在修），见 setup.psd1。

所有「对端收到了什么」「库里变成了什么」的判据都先等它出现再断言（Wait-L2Condition／Wait-L2Change）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Change.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection

# 与 setup.psd1 的 StationDepartureWaitTimeout 相同。
$window = [TimeSpan]::FromSeconds(20)
# Wait-L2Condition 把返回 $null 的探针当成「还没看到」，所以等一个码被清掉要探这个标记。
$cleared = '(cleared)'

$demands = @('A', 'B', 'C') | ForEach-Object {
    $guid = [guid]::NewGuid()
    [pscustomobject]@{ Name = $_; Wire = $guid.ToString('N'); Id = $guid.ToString('D') }
}

# Invoke-L2Query 已经把 DBNull 换成了 $null。
function Test-L2Null($value) {
    return $null -eq $value -or [string]::IsNullOrEmpty([string]$value)
}

# ControlServerDbContext 把 DateTimeOffset 存成文本；判据要拿它和期限比较。
function ConvertTo-Instant($value) {
    if (Test-L2Null $value) { return $null }
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Runtime([string]$demandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Operation([string]$demandId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT SlotOperationAttemptId, Status FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$demandId, [string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status, VehicleOccupancyReleasedAt FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Session([string]$agvId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Readiness, ReasonCode, SafetyReasonCodesJson FROM SessionRecoveries WHERE AgvId = '$agvId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 列名用 Total，与既有场景一致。
function Get-Count([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    return [int]$rows[0].Total
}

function Get-Peer([string]$agvId) {
    return @($Context.OnboardPeers | Where-Object { $_.AgvId -eq $agvId })[0].Double
}

function Get-PendingOperationKey([object]$peer) {
    $pending = @($peer.Snapshot().body.pending | Where-Object { $_.messageType -eq 'SlotOperationCommand' })
    if ($pending.Count -eq 0) { return $null }
    return [string]$pending[0].key
}

function Publish-Demand([object]$demand) {
    $journal.Note("Publishing demand $($demand.Name) ($($demand.Wire)).")
    $null = $mes.Command('Put', "demands/$($demand.Wire)", @{
        sublot      = "L2-LD-$($Context.RunId)-$($demand.Name)"
        area        = 'N1-3'
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
    })
}

# 一台车跑一条取货单：接单、行驶、停在取货点。与 normal-load 同形，只是按 vehicleKey 定向。
function Invoke-PickupLeg([object]$runtime, [string]$criterion) {
    $demandId = [string]$runtime.DemandId
    $intent = Wait-L2Condition -Description "the TO_PICKUP intent of $demandId was confirmed" `
        -Journal $journal -Criterion $criterion -TimeoutSeconds 60 `
        -Probe { Get-Intent $demandId 'TO_PICKUP' } -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
    $vehicleKey = [string]$runtime.VehicleKey
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{
        orderState        = 3
        executeVehicleKey = $vehicleKey
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey      = $vehicleKey
        procState       = 'RUNNING'
        movementState   = 'MT_RUNNING'
        speed           = 0.8
        processingOrder = $true
        orderTaskId     = $intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey       = $vehicleKey
        procState        = 'IDLE'
        movementState    = 'MT_FINISHED'
        speed            = 0
        currentPosition  = [int]$runtime.PickupStationRiotId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })
}

# 扫码自动应答，装货命令挂在这台车的对端，等到库里记下 AwaitingLoadResult 再返回（服务端先发命令、迭代末尾才写阶段）。
function Wait-LoadOutstanding([object]$runtime, [object]$peer, [string]$criterion) {
    $demandId = [string]$runtime.DemandId
    $key = Wait-L2Condition -Description "the load command of $demandId reached its peer and is held there" `
        -Journal $journal -Criterion "$criterion-pending" -TimeoutSeconds 120 `
        -Probe { Get-PendingOperationKey $peer } -Until { param($v) $null -ne $v }
    $waiting = Wait-L2Condition -Description "the journey of $demandId recorded that it waits for the load result" `
        -Journal $journal -Criterion "$criterion-stage" -TimeoutSeconds 30 `
        -Probe { Get-Runtime $demandId } `
        -Until { param($v) [string]$v.Stage -eq 'AwaitingLoadResult' -and -not (Test-L2Null $v.StationDepartureWaitStartedAt) }
    return [pscustomobject]@{
        Key      = $key
        Runtime  = $waiting
        Deadline = (ConvertTo-Instant $waiting.StationDepartureWaitStartedAt).Add($window)
    }
}

# --- 0. 两单进目录，两台车各接一单 ----------------------------------------------------------------

foreach ($peer in $Context.OnboardPeers) {
    $null = $peer.Double.Command('Put', 'policy', @{ loadResult = 'Manual' })
}
Publish-Demand $demands[0]
Publish-Demand $demands[1]
$null = Wait-L2Condition -Description 'both vehicles took a journey' `
    -Journal $journal -Criterion 'journeys-dispatched' -TimeoutSeconds 120 `
    -Probe { $a = Get-Runtime $demands[0].Id; $b = Get-Runtime $demands[1].Id; if ($a -and $b) { @($a, $b) } else { $null } } `
    -Until { param($v) [string]$v[0].AgvId -ne [string]$v[1].AgvId }
$failing = Get-Runtime $demands[0].Id
$doorOpen = Get-Runtime $demands[1].Id
$failingPeer = Get-Peer ([string]$failing.AgvId)
$doorOpenPeer = Get-Peer ([string]$doorOpen.AgvId)
$journal.Note("Demand A rides $($failing.AgvId), demand B rides $($doorOpen.AgvId).")

# --- A1. 第一台车：装货命令下发，结果不来 ----------------------------------------------------------

Invoke-PickupLeg $failing 'a-to-pickup-intent'
$a = Wait-LoadOutstanding $failing $failingPeer 'a-load'

# 否定判据要先证明运行时确实又转了几轮。
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal
$readAt = [DateTimeOffset]::UtcNow
$beforeDeadline = Get-Runtime $demands[0].Id
$operation = Get-Operation $demands[0].Id
$assertions.Add(
    'L2-LD-01', '期限未到、结果未到：旅程原地等装货结果，仓位操作仍在途',
    ($readAt -lt $a.Deadline -and [string]$beforeDeadline.Stage -eq 'AwaitingLoadResult' -and
        [string]$operation.Status -eq 'Prepared'),
    '读于期限前 / AwaitingLoadResult / Prepared',
    "读于期限前 $([math]::Round(($a.Deadline - $readAt).TotalSeconds, 1)) s / $($beforeDeadline.Stage) / $($operation.Status)")

# --- A2. 期限过后，对端报确定失败 ------------------------------------------------------------------

$null = Wait-L2Condition -Description 'the first stop has run out its station deadline' `
    -Journal $journal -Criterion 'a-past-deadline' -TimeoutSeconds 60 `
    -Probe { [DateTimeOffset]::UtcNow } -Until { param($v) $v -ge $a.Deadline.AddSeconds(1) }

$settled = Wait-L2Change -Description 'the determinate load failure ended demand A' `
    -Journal $journal -Criterion 'a-settled' -TimeoutSeconds 60 `
    -Baseline { [string](Get-Runtime $demands[0].Id).Stage } `
    -Action {
        $journal.Note("Peer reports the load FAILED under OPERATOR_TIMEOUT with every slot determinate ($($a.Key)).")
        $null = $failingPeer.Command('Put', "determinate-load-failures/$($a.Key)", @{ reasonCode = 'OPERATOR_TIMEOUT' })
    } `
    -Probe { Get-Runtime $demands[0].Id } `
    -Until { param($before, $now) $before -eq 'AwaitingLoadResult' -and [string]$now.Stage -eq 'Completed' }
$ended = $settled.Value

$attemptId = [string]$a.Runtime.LoadSlotOperationAttemptId
$resultRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT ReceivedAt FROM ProtocolInbox WHERE MessageType = 'OperationResult' AND RequestJson LIKE '%$attemptId%'"
$receivedAt = if ($resultRows.Count -eq 1) { ConvertTo-Instant $resultRows[0].ReceivedAt } else { $null }
$assertions.Add(
    'L2-LD-02', '确定失败是在本站期限之后收到的（这一格的前提）',
    ($null -ne $receivedAt -and $receivedAt -ge $a.Deadline),
    ">= $($a.Deadline.ToString('o'))", $(if ($receivedAt) { $receivedAt.ToString('o') } else { "(results: $($resultRows.Count))" }))

$operation = Get-Operation $demands[0].Id
$demandRows = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$($demands[0].Id)'"
$assertions.Add(
    'L2-LD-03', '仓位操作判 Failed（不是 RecoveryRequired），需求 Cancelled、理由 CANCELLED_BY_STATION_TIMEOUT，旅程 Completed',
    ([string]$operation.Status -eq 'Failed' -and $demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'Cancelled' -and
        [string]$ended.BlockReasonCode -eq 'CANCELLED_BY_STATION_TIMEOUT'),
    'Failed / Cancelled / Completed / CANCELLED_BY_STATION_TIMEOUT',
    "$($operation.Status) / $(if ($demandRows.Count -eq 1) { $demandRows[0].Status } else { '(no demand)' }) / $($ended.Stage) / $($ended.BlockReasonCode)")

# 下面这些与旅程转 Completed 是同一次提交（写事务里暂存、一次保存），所以等到阶段之后读是安全的。
$leaseRows = Invoke-L2Query -Connection $connection -Sql "SELECT ReleasedAt FROM VehicleDispatchLeases WHERE DemandId = '$($demands[0].Id)'"
$pickupIntent = Get-Intent $demands[0].Id 'TO_PICKUP'
$loadCommand = Invoke-L2Query -Connection $connection `
    -Sql "SELECT AcknowledgedAt FROM ProtocolOutbox WHERE MessageId = '$($a.Runtime.LoadCommandMessageId)'"
$assertions.Add(
    'L2-LD-04', '租约与车辆占用释放，悬空的装货命令被结算（不会再重放进后面的会话）',
    ($leaseRows.Count -eq 1 -and -not (Test-L2Null $leaseRows[0].ReleasedAt) -and
        -not (Test-L2Null $pickupIntent.VehicleOccupancyReleasedAt) -and
        $loadCommand.Count -eq 1 -and -not (Test-L2Null $loadCommand[0].AcknowledgedAt)),
    '租约已释放 / 占用已释放 / 装货命令已结算',
    "ReleasedAt=$(if ($leaseRows.Count -eq 1) { $leaseRows[0].ReleasedAt } else { '(no lease)' }) / VehicleOccupancyReleasedAt=$($pickupIntent.VehicleOccupancyReleasedAt) / AcknowledgedAt=$(if ($loadCommand.Count -eq 1) { $loadCommand[0].AcknowledgedAt } else { '(no command)' })")

$workflows = Get-Count "SELECT COUNT(*) AS Total FROM RecoveryWorkflows WHERE DemandId = '$($demands[0].Id)'"
$recoverySessions = Get-Count "SELECT COUNT(*) AS Total FROM ExceptionRecoverySessions WHERE AgvId = '$($failing.AgvId)'"
$session = Get-Session ([string]$failing.AgvId)
$assertions.Add(
    'L2-LD-05', '没有恢复：没有恢复工作流、没有异常恢复会话，这台车的会话仍是 Ready',
    ($workflows -eq 0 -and $recoverySessions -eq 0 -and [string]$session.Readiness -eq 'Ready'),
    '0 / 0 / Ready', "$workflows / $recoverySessions / $($session.Readiness) ($($session.ReasonCode))")

# --- B1. 第二台车：装货命令下发，操作员开着门走开 --------------------------------------------------

Invoke-PickupLeg $doorOpen 'b-to-pickup-intent'
$b = Wait-LoadOutstanding $doorOpen $doorOpenPeer 'b-load'

$opened = Wait-L2Change -Description 'the server recorded the open door from the second peer' `
    -Journal $journal -Criterion 'b-door-open' -TimeoutSeconds 30 `
    -Baseline { [string](Get-Session ([string]$doorOpen.AgvId)).SafetyReasonCodesJson } `
    -Action {
        $journal.Note('Second peer reports a slot door not closed while its load is still underway.')
        $null = $doorOpenPeer.Command('Put', 'safety', @{
            departureSafe        = $false
            allTargetSlotsLocked = $false
            unknownPresent       = $false
            reasonCodes          = @('LOCK_NOT_CLOSED')
        })
    } `
    -Probe { Get-Session ([string]$doorOpen.AgvId) } `
    -Until { param($before, $now) $now -and ([string]$now.SafetyReasonCodesJson).Contains('LOCK_NOT_CLOSED') }
$assertions.Add(
    'L2-LD-06', '门开着时这台车的会话仍是 Ready：开着的门由本车自己在途的装货解释',
    ([string]$opened.Value.Readiness -eq 'Ready'),
    'Ready', "$($opened.Value.Readiness) ($($opened.Value.ReasonCode))")

$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal
$readAt = [DateTimeOffset]::UtcNow
$beforeDeadline = Get-Runtime $demands[1].Id
$assertions.Add(
    'L2-LD-07', '期限未到：门开着也不挂告警',
    ($readAt -lt $b.Deadline -and [string]$beforeDeadline.Stage -eq 'AwaitingLoadResult' -and
        (Test-L2Null $beforeDeadline.BlockReasonCode)),
    '读于期限前 / AwaitingLoadResult / 无阻断码',
    "读于期限前 $([math]::Round(($b.Deadline - $readAt).TotalSeconds, 1)) s / $($beforeDeadline.Stage) / '$($beforeDeadline.BlockReasonCode)'")

# --- B2. 期限过了、门还开着：挂告警，不结束本站 ----------------------------------------------------

$alarmed = Wait-L2Condition -Description 'the stop past its deadline with a door open raised STATION_TIMEOUT_DOOR_NOT_CLOSED' `
    -Journal $journal -Criterion 'b-alarm' -TimeoutSeconds 90 `
    -Probe { Get-Runtime $demands[1].Id } `
    -Until { param($v) [string]$v.BlockReasonCode -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED' }
$alarmSince = ConvertTo-Instant $alarmed.BlockReasonSince
$operation = Get-Operation $demands[1].Id
$demandRows = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$($demands[1].Id)'"
$leaseRows = Invoke-L2Query -Connection $connection -Sql "SELECT ReleasedAt FROM VehicleDispatchLeases WHERE DemandId = '$($demands[1].Id)'"
$assertions.Add(
    'L2-LD-08', '告警挂上：stage 仍是 AwaitingLoadResult（不是 Blocked）、开始时间不早于期限、需求与租约都没动',
    ([string]$alarmed.Stage -eq 'AwaitingLoadResult' -and $null -ne $alarmSince -and $alarmSince -ge $b.Deadline -and
        [string]$operation.Status -eq 'Prepared' -and $demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'Accepted' -and
        $leaseRows.Count -eq 1 -and (Test-L2Null $leaseRows[0].ReleasedAt)),
    "AwaitingLoadResult / since >= $($b.Deadline.ToString('o')) / Prepared / Accepted / 租约未释放",
    "$($alarmed.Stage) / since $($alarmed.BlockReasonSince) / $($operation.Status) / $(if ($demandRows.Count -eq 1) { $demandRows[0].Status } else { '(no demand)' }) / ReleasedAt='$(if ($leaseRows.Count -eq 1) { $leaseRows[0].ReleasedAt })'")

# 否定判据要有界：让运行时确实又跑几轮，再说它还在等、开始时间没动。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$held = Get-Runtime $demands[1].Id
$assertions.Add(
    'L2-LD-09', '告警挂着期间运行时又跑了几轮：仍在 AwaitingLoadResult、同一个码、开始时间不变',
    ([string]$held.Stage -eq 'AwaitingLoadResult' -and [string]$held.BlockReasonCode -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED' -and
        (ConvertTo-Instant $held.BlockReasonSince) -eq $alarmSince),
    "AwaitingLoadResult / STATION_TIMEOUT_DOOR_NOT_CLOSED since $($alarmed.BlockReasonSince)",
    "$($held.Stage) / $($held.BlockReasonCode) since $($held.BlockReasonSince)")

# --- B3. 门关上：告警撤销 --------------------------------------------------------------------------

$withdrawn = Wait-L2Change -Description 'the alarm was withdrawn once the door was shut' `
    -Journal $journal -Criterion 'b-alarm-withdrawn' -TimeoutSeconds 60 `
    -Baseline { [string](Get-Runtime $demands[1].Id).BlockReasonCode } `
    -Action {
        $journal.Note('The operator shuts the door; the second peer reports every slot locked again.')
        $null = $doorOpenPeer.Command('Put', 'safety', @{
            departureSafe        = $true
            allTargetSlotsLocked = $true
            unknownPresent       = $false
            reasonCodes          = @()
        })
    } `
    -Probe { $r = Get-Runtime $demands[1].Id; if ($r -and (Test-L2Null $r.BlockReasonCode)) { $cleared } else { [string]$r.BlockReasonCode } } `
    -Until { param($before, $now) $before -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED' -and $now -eq $cleared }
$afterClose = Get-Runtime $demands[1].Id
$assertions.Add(
    'L2-LD-10', '门关上后告警撤销、开始时间一并清掉，本站仍在等结果（关门本身不结束本站）',
    ($withdrawn.Value -eq $cleared -and (Test-L2Null $afterClose.BlockReasonSince) -and [string]$afterClose.Stage -eq 'AwaitingLoadResult'),
    '(cleared) / since null / AwaitingLoadResult',
    "$($withdrawn.Value) / since '$($afterClose.BlockReasonSince)' / $($afterClose.Stage)")

# --- B4. 结果到：按真实结果提交 --------------------------------------------------------------------

$committed = Wait-L2Change -Description 'the completed load result settled the stop as reported' `
    -Journal $journal -Criterion 'b-committed' -TimeoutSeconds 60 `
    -Baseline { [string](Get-Operation $demands[1].Id).Status } `
    -Action {
        $journal.Note("Second peer reports the load COMPLETED ($($b.Key)).")
        $null = $doorOpenPeer.Command('Put', "answer/$($b.Key)", @{ completed = $true })
    } `
    -Probe { [pscustomobject]@{ Operation = Get-Operation $demands[1].Id; Runtime = Get-Runtime $demands[1].Id } } `
    -Until { param($before, $now) $before -eq 'Prepared' -and [string]$now.Operation.Status -eq 'Committed' -and
        [string]$now.Runtime.Stage -ne 'AwaitingLoadResult' }
$assertions.Add(
    'L2-LD-11', '报 COMPLETED 后仓位操作 Committed、旅程离开 AwaitingLoadResult，告警码不在',
    ([string]$committed.Value.Operation.Status -eq 'Committed' -and
        [string]$committed.Value.Runtime.Stage -in @('AwaitingStationDeparture', 'AwaitingDepartureSafety', 'AwaitingGateArrival') -and
        [string]$committed.Value.Runtime.BlockReasonCode -ne 'STATION_TIMEOUT_DOOR_NOT_CLOSED'),
    'Committed / AwaitingStationDeparture 或其后 / 非 STATION_TIMEOUT_DOOR_NOT_CLOSED',
    "$($committed.Value.Operation.Status) / $($committed.Value.Runtime.Stage) / '$($committed.Value.Runtime.BlockReasonCode)'")

# --- C. 被确定失败释放的那台车接下一单 ---------------------------------------------------------------

# 第二台车此刻还在途，所以这一单只能由第一台车接：它被结算释放了，这就是证据。
$next = Wait-L2Change -Description 'the vehicle released by the determinate failure took the next demand' `
    -Journal $journal -Criterion 'c-accepted' -TimeoutSeconds 90 `
    -Baseline { Get-Count "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE DemandId = '$($demands[2].Id)'" } `
    -Action { Publish-Demand $demands[2] } `
    -Probe { Get-Runtime $demands[2].Id } `
    -Until { param($before, $now) $before -eq 0 -and $now -and [string]$now.Stage -eq 'AwaitingPickupArrival' }
$assertions.Add(
    'L2-LD-12', '确定失败结算之后，那台车接了下一单',
    ($next.Baseline -eq 0 -and [string]$next.Value.Stage -eq 'AwaitingPickupArrival' -and [string]$next.Value.AgvId -eq [string]$failing.AgvId),
    "0 → AwaitingPickupArrival on $($failing.AgvId)", "$($next.Baseline) → $($next.Value.Stage) on $($next.Value.AgvId)")

$workflows = Get-Count "SELECT COUNT(*) AS Total FROM RecoveryWorkflows"
$recoverySessions = Get-Count "SELECT COUNT(*) AS Total FROM ExceptionRecoverySessions"
$assertions.Add(
    'L2-LD-13', '全程没有进过恢复：没有恢复工作流、没有异常恢复会话',
    ($workflows -eq 0 -and $recoverySessions -eq 0),
    '0 / 0', "$workflows / $recoverySessions")

$journal.Note('Scenario finished.')
