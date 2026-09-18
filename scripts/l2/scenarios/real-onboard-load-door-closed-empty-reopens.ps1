#Requires -Version 7

<#
装货时操作员把空仓门关上：车自己重开，期限前后一样，不判失败；放弃装货只有操作员按取消一个出口。

- 对应决策：ADR-cross-0058 决策 1 与决策 5 的 2026-09-18 改写（按 program#55 2026-09-13 的定论，program PR #110）：
  扫码、开门之后关门默认是意外关上，**期限前后一样**，读到相反态就重开并提示，不设次数上限，也不以本站期限为上限；
  v2 车载端在装货侧不产出期限后的确定失败（`FAILED`／`OPERATOR_TIMEOUT`）；放弃这次装货只有持工号的操作员按取消，
  在途装货的取消入口与 `recoveryResumeEnabled` 解绑（onboard-hmi#78）。车载端闭环本身是 onboard-hmi#72。
  **本条不断言「空关判 `CANCELLED_BY_STATION_TIMEOUT`」**——那是 2026-09-12 回写的 MVP 行为，已作废；服务端对
  `FAILED` 的防御性结算由合成场景 `load-determinate-failure-and-door-open-timeout` 覆盖。
- 期限值：站点期限 `StationDepartureWaitTimeout` = 40 秒（setup.psd1，出厂 5 分钟），从到站起算。第一次空关在
  期限之前，之后等期限过去、车载端倒计时也进入 Expired，再空关两次。车载端出厂配置：`recoveryResumeEnabled=false`，
  `workflow.operationTimeoutMs` 不改（README 第 11 条）。
- 判据来源：服务端 SQLite（`ProtocolInbox` 里车载端发来的 `OperationProgress`／`OperationResult`／
  `LoadCancellationStartRequested`／`LoadCancellationResult`，`StationOperations`，`JourneyRuntimes`，`AcceptedDemands`，
  `VehicleDispatchLeases`，`OrderIntents`，`RecoveryWorkflows`）与模拟器 `/snapshot` 的仓位物理状态。
  车载端界面只用来驱动：录入子批号、按「取消装货」与它的确认框；另读一次倒计时控件的 UIA 状态
  （AutomationId `StationDepartureCountdown` 的 `ItemStatus`，是档位枚举不是文案），作为「车载端自己也认定期限已过」
  的前提——期限后的提示与倒计时文案 onboard-hmi#78 改过，判据一个字都不比。
  取消清空只判结果（需求 `Cancelled`、`CANCELLED_BY_OPERATOR`、目标仓 `EMPTY` 且锁闭、车辆释放），不判开锁方式与
  顺序：有货仓仍是逐个开锁，ADR-cross-0046 要的批量由 onboard-hmi#104 跟进。
  MVP 线参照 `origin/ControlServer_MVP:scripts/l2/scenarios/real-onboard-load-door-closed-empty.ps1`，判据按
  program#55 改写，没有整份拷贝。

模拟器没有开锁脉冲计数器，开锁输出是 500 ms 的脉冲、轮询数不准。它能如实说的是门：自动化接口只能关门不能开门，
门只在开锁输出触发时弹开。所以「关上之后门又回到 OPEN」就是脉冲到了 IO，车载端发来的 `UNLOCKING` 条数说明
脉冲是车发的（见 `L2RealStation.psm1` 的 `Invoke-L2CloseOverOppositeState`）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Change.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealStation.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$onboard = $Context.Onboard
$simulator = $Context.Simulator
$connection = $Context.Connection

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig (Onboard = ''Real'').'
}

# 与 setup.psd1 的 StationDepartureWaitTimeout 相同。
$window = [TimeSpan]::FromSeconds(40)
$postDeadlineRounds = 2
# 取消按钮与它的确认框按标题找，因为驱动就是这么认按钮的（New-L2OnboardDriver）。这只用于驱动，没有一条判据读它。
$cancelButton = '取消装货'

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-DC-$($Context.RunId)"

function Get-Count([string]$sql) { return [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].Total }

function Get-Scalar([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Value
}

function Get-DemandStatus { return Get-Scalar "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'" }

# 车载端对这笔装货报过的所有 FAILED：整体结果或任一仓位结果。另数一遍全部 OperationResult 里的 OPERATOR_TIMEOUT。
function Get-FailedFootprint([string]$attemptId) {
    $results = Get-L2OperationResults -Connection $connection -AttemptId $attemptId
    $failed = @($results | Where-Object {
            [string]$_.Payload.overallOutcome -eq 'FAILED' -or
            ($_.Payload.PSObject.Properties['slotResults'] -and
                @($_.Payload.slotResults | Where-Object { [string]$_.outcome -eq 'FAILED' }).Count -gt 0)
        }).Count
    $operatorTimeout = Get-Count "SELECT COUNT(*) AS Total FROM ProtocolInbox WHERE MessageType = 'OperationResult' AND RequestJson LIKE '%OPERATOR_TIMEOUT%'"
    $failedOperations = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId' AND Status IN ('Failed', 'RecoveryRequired')"
    return "FAILED 结果 $failed / OPERATOR_TIMEOUT $operatorTimeout / Failed 或 RecoveryRequired 操作 $failedOperations"
}
$noFailure = 'FAILED 结果 0 / OPERATOR_TIMEOUT 0 / Failed 或 RecoveryRequired 操作 0'

function Get-Countdown {
    $element = $onboard.Element('AutomationId', 'StationDepartureCountdown')
    if (-not $element) { return '(absent)' }
    return [string]$element.Current.ItemStatus
}

# --- 1. 到站、扫码，车载端为装货开门 ---------------------------------------------------------------

Invoke-L2PickupAndScan -Context $Context -DemandIdWire $demandIdWire -DemandId $demandId -Sublot $sublot
$load = Wait-L2WaitingOperator -Context $Context -DemandId $demandId -OperationType 'Load'
$attemptId = $load.AttemptId
$slotNo = $load.SlotNo

$runtime = Wait-L2Condition -Description 'the journey records that it waits for the load result, with its station deadline' `
    -Journal $journal -Criterion 'awaiting-load-result' -TimeoutSeconds 30 `
    -Probe { Get-L2Runtime -Connection $connection -DemandId $demandId } `
    -Until { param($v) [string]$v.Stage -eq 'AwaitingLoadResult' -and $null -ne (ConvertTo-L2Instant $v.StationDepartureWaitStartedAt) }
$deadline = (ConvertTo-L2Instant $runtime.StationDepartureWaitStartedAt).Add($window)
$journal.Note("Station deadline is $($deadline.ToString('o')) (arrival + $window).")

$physical = Get-L2SlotPhysical -Simulator $simulator -SlotNo $slotNo
$assertions.Add(
    'L2-DC-01', "车载端说在等操作员时，$slotNo 号仓门确实开着、空着、开锁输出已复位",
    ($physical -eq 'OPEN/EMPTY/0/0'), 'OPEN/EMPTY/0/0', $physical)

# --- 2. 期限之前：空关，车自己重开 -----------------------------------------------------------------

$first = Invoke-L2CloseOverOppositeState -Context $Context -AttemptId $attemptId -SlotNo $slotNo -Criterion 'reopen-before-deadline'
$assertions.Add(
    'L2-DC-02', '期限之前空关：车载端自己重新开锁（UNLOCKING +1）、门又弹开（模拟器 OPEN、开锁输出复位）、再次等操作员',
    ($first.After.Unlocking -eq $first.Before.Unlocking + 1 -and $first.After.Waiting -gt $first.Before.Waiting -and
        $first.After.Physical -eq 'OPEN/EMPTY/0/0' -and $null -ne $first.ReopenedAt -and $first.ReopenedAt -lt $deadline),
    "UNLOCKING $($first.Before.Unlocking)→$($first.Before.Unlocking + 1) / WAITING_OPERATOR 增加 / OPEN/EMPTY/0/0 / 重开早于期限",
    "UNLOCKING $($first.Before.Unlocking)→$($first.After.Unlocking) / WAITING_OPERATOR $($first.Before.Waiting)→$($first.After.Waiting) / $($first.After.Physical) / 重开于期限前 $(if ($first.ReopenedAt) { [math]::Round(($deadline - $first.ReopenedAt).TotalSeconds, 1) }) s")

# --- 3. 期限过去：服务端与车载端都认定了 -----------------------------------------------------------

$null = Wait-L2Condition -Description 'the stop has run out its station deadline' `
    -Journal $journal -Criterion 'past-deadline' -TimeoutSeconds ([int]$window.TotalSeconds + 60) `
    -Probe { [DateTimeOffset]::UtcNow } -Until { param($v) $v -ge $deadline.AddSeconds(1) }
$countdown = Wait-L2Condition -Description 'the onboard countdown turned Expired' `
    -Journal $journal -Criterion 'onboard-countdown-expired' -TimeoutSeconds 30 `
    -Probe { Get-Countdown } -Until { param($v) $v -eq 'Expired' }
$assertions.Add(
    'L2-DC-03', '期限已过：车载端倒计时控件（AutomationId StationDepartureCountdown）的状态是 Expired——车载端自己也认定过期了',
    ($countdown -eq 'Expired'), 'Expired', $countdown)

# --- 4. 期限之后：再空关两次，每次仍然重开 ---------------------------------------------------------

for ($round = 1; $round -le $postDeadlineRounds; $round++) {
    $reopen = Invoke-L2CloseOverOppositeState -Context $Context -AttemptId $attemptId -SlotNo $slotNo -Criterion "reopen-after-deadline-$round"
    $assertions.Add(
        "L2-DC-$('{0:d2}' -f (3 + $round))",
        "期限之后第 $round 次空关：车载端照样自己重开（UNLOCKING +1、门又弹开、再次等操作员），重开发生在期限之后",
        ($reopen.After.Unlocking -eq $reopen.Before.Unlocking + 1 -and $reopen.After.Waiting -gt $reopen.Before.Waiting -and
            $reopen.After.Physical -eq 'OPEN/EMPTY/0/0' -and $null -ne $reopen.ReopenedAt -and $reopen.ReopenedAt -ge $deadline),
        "UNLOCKING $($reopen.Before.Unlocking)→$($reopen.Before.Unlocking + 1) / WAITING_OPERATOR 增加 / OPEN/EMPTY/0/0 / 重开晚于期限",
        "UNLOCKING $($reopen.Before.Unlocking)→$($reopen.After.Unlocking) / WAITING_OPERATOR $($reopen.Before.Waiting)→$($reopen.After.Waiting) / $($reopen.After.Physical) / 重开于期限后 $(if ($reopen.ReopenedAt) { [math]::Round(($reopen.ReopenedAt - $deadline).TotalSeconds, 1) }) s")
}

# 否定判据要有界：让运行时又转几轮，再说什么都没被结算。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$held = Get-L2Runtime -Connection $connection -DemandId $demandId
$operation = Get-L2StationOperation -Connection $connection -DemandId $demandId -OperationType 'Load'
$results = Get-L2OperationResults -Connection $connection -AttemptId $attemptId
$footprint = Get-FailedFootprint $attemptId
$demandStatus = Get-DemandStatus
$assertions.Add(
    'L2-DC-06', '期限后空关两次之后：需求未被取消（Accepted），旅程仍 AwaitingLoadResult，装货仍在途（Prepared），车载端一份结果都没报，没有任何 FAILED／OPERATOR_TIMEOUT',
    ($demandStatus -eq 'Accepted' -and [string]$held.Stage -eq 'AwaitingLoadResult' -and [string]$operation.Status -eq 'Prepared' -and
        $results.Count -eq 0 -and $footprint -eq $noFailure),
    "Accepted / AwaitingLoadResult / Prepared / 0 result / $noFailure",
    "$demandStatus / $($held.Stage) / $($operation.Status) / $($results.Count) result / $footprint")

# --- 5. 操作员按取消 -------------------------------------------------------------------------------

# 门此刻开着（最后一次重开之后），操作员决定不装了。取消执行器接手开着的门、不再打脉冲（onboard-hmi#78），
# 所以先按取消、等服务端授权，再把空门带上；反过来先关门，车会把它当成又一次空关重开。
$offered = Wait-L2Condition -Description "the onboard offers $cancelButton with the factory recovery switch off" `
    -Journal $journal -Criterion 'onboard-cancel-offered' -TimeoutSeconds 60 `
    -Probe { [bool]$onboard.ButtonAvailable($cancelButton) } -Until { param($v) $v }
$journal.Note('The operator presses cancel and confirms it.')
$onboard.InvokeButton($cancelButton)
$null = $onboard.Confirm($cancelButton)

$request = Wait-L2Condition -Description 'the server answered LoadCancellationStartRequested for this load' `
    -Journal $journal -Criterion 'cancellation-requested' -TimeoutSeconds 60 `
    -Probe { @((Get-L2Inbound -Connection $connection -MessageType 'LoadCancellationStartRequested') |
            Where-Object { [string]$_.Payload.demandId -eq $demandId -and $_.Response })[0] } `
    -Until { param($v) $null -ne $v }
$authorization = $request.ResponsePayload
$assertions.Add(
    'L2-DC-07', '出厂配置下按取消：车载端发出针对这笔装货的取消请求，服务端授权（AUTHORIZED），范围就是这个仓',
    ([string]$request.Payload.slotOperationAttemptId -eq $attemptId -and $request.Response -eq 'LoadCancellationAuthorization' -and
        $null -ne $authorization -and [string]$authorization.decision -eq 'AUTHORIZED' -and
        (@($authorization.slots | ForEach-Object { [int]$_ }) -join ',') -eq "$slotNo"),
    "$attemptId / LoadCancellationAuthorization AUTHORIZED [$slotNo]",
    "$($request.Payload.slotOperationAttemptId) / $($request.Response) $(if ($authorization) { "$($authorization.decision) [$(@($authorization.slots) -join ',')]" })")

$journal.Note("The operator shuts the empty slot $slotNo.")
$null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})

# --- 6. 取消收敛：只判结果 -------------------------------------------------------------------------

$settled = Wait-L2Condition -Description 'the cancellation settled: demand cancelled by the operator, journey ended' `
    -Journal $journal -Criterion 'cancellation-settled' -TimeoutSeconds 120 `
    -Probe {
        $r = Get-L2Runtime -Connection $connection -DemandId $demandId
        [pscustomobject]@{ Demand = Get-DemandStatus; Stage = [string]$r.Stage; Reason = [string]$r.BlockReasonCode }
    } `
    -Until { param($v) $v.Demand -eq 'Cancelled' -and $v.Stage -eq 'Completed' }
$assertions.Add(
    'L2-DC-08', '取消收敛：需求 Cancelled，旅程以 CANCELLED_BY_OPERATOR 结束',
    ($settled.Demand -eq 'Cancelled' -and $settled.Stage -eq 'Completed' -and $settled.Reason -eq 'CANCELLED_BY_OPERATOR'),
    'Cancelled / Completed / CANCELLED_BY_OPERATOR', "$($settled.Demand) / $($settled.Stage) / $($settled.Reason)")

# 让运行时在收尾之后再转几轮：原装货若还有迟到的结果，也要在这之后判终态。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$finalPhysical = Get-L2SlotPhysical -Simulator $simulator -SlotNo $slotNo
$assertions.Add(
    'L2-DC-09', "目标仓 $slotNo 空着、门关、锁上、开锁输出复位",
    ($finalPhysical -eq 'CLOSED/EMPTY/1/0'), 'CLOSED/EMPTY/1/0', $finalPhysical)

$lease = Get-Scalar "SELECT ReleasedAt AS Value FROM VehicleDispatchLeases WHERE DemandId = '$demandId'"
$occupancy = Get-Scalar "SELECT VehicleOccupancyReleasedAt AS Value FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'"
$toGate = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_GATE'"
$assertions.Add(
    'L2-DC-10', '车辆释放：调度租约与车辆占用都释放，没有去关卡的单',
    (-not [string]::IsNullOrEmpty($lease) -and -not [string]::IsNullOrEmpty($occupancy) -and $toGate -eq 0),
    '租约已释放 / 占用已释放 / TO_GATE 0', "ReleasedAt='$lease' / VehicleOccupancyReleasedAt='$occupancy' / TO_GATE $toGate")

$footprint = Get-FailedFootprint $attemptId
$operation = Get-L2StationOperation -Connection $connection -DemandId $demandId -OperationType 'Load'
$completions = Get-Count "SELECT COUNT(*) AS Total FROM TransportDemandCompletions WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-DC-11', '全程没有 FAILED：装货没有被结算成 Failed 或 RecoveryRequired，车载端没报过 FAILED／OPERATOR_TIMEOUT，需求没有完成记录',
    ($footprint -eq $noFailure -and [string]$operation.Status -notin @('Failed', 'RecoveryRequired', 'Committed') -and $completions -eq 0),
    "$noFailure / 装货非 Failed、RecoveryRequired、Committed / 完成记录 0",
    "$footprint / 装货 $($operation.Status) / 完成记录 $completions")

$journal.Note('Scenario finished.')
