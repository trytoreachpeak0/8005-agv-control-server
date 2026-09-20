#Requires -Version 7

<#
期待动作超时（REQ-0358）在真装置上走完整条链：车载端超时告警上报 → 服务端在同一连接上要一份中途快照 → 车载端回快照 →
看板端点与看板页出一行卡片 → HMI 说「已上报」；上报不改变执行器行为；闭环后撤下。

- 来源：control-server#167；批次 5 出口报告（`docs/batch-5-v2-exit-report.md`）第六节「剩余风险」第一条。需求基线 v1.4.0
  `REQ-0358`；program 仓 `requirements/change-proposals/CP-0005.md` 第 4.1 节（告警走 `OnboardAlarmSnapshot`，码
  `SLOT_EXPECTED_ACTION_OVERDUE`，`subjectType=SLOT`，`raisedAt` 为越过门槛的时刻，`displayMessage` 为期待的动作）；
  ADR-cross-0062（上报只让人看到，不改变行为）。两端实现：onboard-hmi#109（车载端）、control-server#142（服务端看板），
  各自只在替身上绿过（`StationDeadlineExpiredG2Tests`、`ExpectedActionOverdueTests`）；本场景是两者第一次在一次运行里互通。
- 门槛：setup 的 `ExpectedActionOverdueThreshold` = 20 秒，编排器同时写进车载端 stage 副本（`workflow.expectedActionOverdueMs`）
  与服务端环境（`ExpectedActionOverdue__threshold`）。它是投运标定的现场参数（REQ-0358 说明 2、3），只决定何时上报、
  不改任何执行器时序，所以不是 README 第 11 条禁止的那种调短；`operationTimeoutMs` 不动。
- 站点期限 60 秒，从到站起算。时间线（以第一次开锁为 0）：约 8 秒空关、车自己重开；门槛前读一次；20 秒越过门槛；
  随后读告警、线上请求、中途快照、端点、看板页、HMI；门槛后再空关、重开，看端点仍一行、raisedAt 不变；约 54 秒期限过去，看合成一行；
  放货关门闭环，看撤下。全程约 70 秒，在 120 秒的 `operationTimeoutMs` 之内。
- 判据来源：服务端 SQLite（`ProtocolInbox` 里车载端发来的 `OnboardAlarmSnapshot`／`SafetyStateSnapshot`／`OperationProgress`／
  `OperationResult`，`SessionRecoveries`，`JourneyRuntimes`，`StationOperations`，三张恢复表）；协议故障代理的流量日志
  （`SafetyStateSnapshotRequested` 只在线上）；看板只读端点 `GET /api/dashboard/expected-action-overdue` 与看板进程渲染的页面；
  模拟器 `/snapshot`；HMI 的 `ExpectedActionOverdue` 控件。
- **`L2-EAO-09` 读 HMI 文字，这是本仓 L2 少数读 UI 文字的判据**：REQ-0358 的交付物本身就是这句提示（用户 2026-09-18 定的
  措辞），读的是它的内容而不是拿它推断业务事实；只比「告警的 displayMessage」与「已上报」两个片段，不逐字比整句。
- 守护判据：`L2-EAO-10`（上报不改变行为）修正前也绿、`L2-EAO-12`（撤下）已有 G2 覆盖，二者不取红。
- 不在本场景：在途装货时断链重连。control-server#167 调试时试过（`evidence/l2/20260919-cs167-debug-001`），重连后服务端判
  `RecoveryRequired`／`PENDING_FACT_RECONCILIATION_REQUIRED` 等这次装货的结果，车载端却因会话不是 Ready 不发进度与结果
  （`WIRE_TO_GATE_NOT_READY`），两端互相等、装货永远收不了尾——那是另一个缺陷，另案处理，放进本场景只会让后面的判据全部够不着。
  会话断开时的 HMI 文案（G2 `ExpectedActionOverdueViewModelTests` 覆盖）；卸货侧、锁反馈卡死两种变体；判故障
  （REQ-0359，protocol-v3.0.0）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealStation.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ExpectedActionOverdue.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$simulator = $Context.Simulator
$connection = $Context.Connection
$proxy = $Context.ProtocolProxy
$agvId = $Context.AgvId

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath) -or $null -eq $proxy -or -not $Context.DashboardUrl) {
    throw 'This scenario needs the real onboard rig with the protocol fault proxy and the dashboard (see its setup.psd1).'
}

# 门槛与站点期限从 setup.psd1 读，不在这里再写一遍（control-server#204）。这两个值编排器也从同一个文件读：
# 门槛经 Resolve-L2ExpectedActionOverdueThreshold（Invoke-L2Scenario.ps1:195）同时写进车载端 stage 副本与服务端环境，
# 期限经 JourneyRuntime__stationDepartureWaitTimeout（同文件 :606）。各写一遍的时候，改了 setup 而忘了改这里，
# 场景会拿旧值去算「门槛越过的时刻」，判据照样绿——绿的是一个不再成立的算式。
$setup = Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot 'real-onboard-expected-action-overdue.setup.psd1')
$threshold = Resolve-L2ExpectedActionOverdueThreshold -Setup $setup `
    -Where 'real-onboard-expected-action-overdue.setup.psd1' -RealOnboard $true
# 两个键都必须明写。编排器对缺席的期限用它自己的默认值 00:00:30，把那个默认值抄到这里就又是两处真相了；
# 而这条场景的时间线（门槛在期限之前、放货收尾在 operationTimeoutMs 之内）本来就要求 setup 把两个值都定死。
if ($null -eq $threshold) {
    throw 'real-onboard-expected-action-overdue.setup.psd1 must name ExpectedActionOverdueThreshold: this scenario times everything from it.'
}
if (-not $setup.ContainsKey('StationDepartureWaitTimeout')) {
    throw 'real-onboard-expected-action-overdue.setup.psd1 must name StationDepartureWaitTimeout: this scenario needs the threshold to fall inside it.'
}
$window = [TimeSpan]::Parse([string]$setup.StationDepartureWaitTimeout, [Globalization.CultureInfo]::InvariantCulture)
$journal.Note("Read from setup.psd1: ExpectedActionOverdueThreshold $($threshold.ToString('c')), " +
    "StationDepartureWaitTimeout $($window.ToString('c')). Both ends were configured from the same two values.")
# 第一次开锁之后多久空关一次。要比门槛早得多，重开与第一次开锁的间隔才能把「重开不清零」与「重开清零」分开。
$firstCloseAfter = [TimeSpan]::FromSeconds(8)
# raisedAt 与「第一次开锁 + 门槛」的容差。第一次开锁取服务端收到第一条 UNLOCKING 的时刻，车载端取它发布开锁投影的时刻，
# 两端同机同钟，差的只是一次本机转发；容差须明显小于重开与第一次开锁的间隔（场景会检查）。
$tolerance = [TimeSpan]::FromSeconds(2)
$code = 'SLOT_EXPECTED_ACTION_OVERDUE'
$endpoint = "http://127.0.0.1:$($Context.HealthPort)/api/dashboard/expected-action-overdue"

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-EAO-$($Context.RunId)"

function Get-Count([string]$sql) { return Get-L2RealCount $connection $sql }

# 「没有进恢复」的三处：异常恢复会话、恢复工作流、落到 RecoveryRequired 的仓位操作。
function Get-RecoveryFootprint {
    return "恢复会话 $(Get-Count "SELECT COUNT(*) AS Total FROM ExceptionRecoverySessions WHERE AgvId = '$agvId'") / " +
        "恢复工作流 $(Get-Count "SELECT COUNT(*) AS Total FROM RecoveryWorkflows WHERE DemandId = '$demandId'") / " +
        "RecoveryRequired 操作 $(Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId' AND Status = 'RecoveryRequired'")"
}
$noRecovery = '恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0'

# 车载端发来的每份告警快照里本仓的超时告警，按服务端收到的顺序：At、MessageId、Generation、Alarm（为空表示这份快照里没有）。
function Get-OverdueReports([int]$slot) {
    return , @((Get-L2RealInbound $connection 'OnboardAlarmSnapshot') | ForEach-Object {
            $message = $_
            $mine = @(@($message.Payload.alarms) | Where-Object {
                    $null -ne $_ -and [string]$_.code -eq $code -and [string]$_.subjectId -eq [string]$slot })
            [pscustomobject]@{
                At        = $message.At
                MessageId = $message.MessageId
                Generation = $message.Generation
                Alarm     = if ($mine.Count -gt 0) { $mine[0] } else { $null }
                Count     = $mine.Count
            }
        })
}

# 最新一份告警快照里有没有这个码（任一仓）。
function Get-LatestOverdueCount {
    # Get-L2RealInbound already returns one array; wrapping it in @() again would make -Last 1 pick the whole list.
    $latest = (Get-L2RealInbound $connection 'OnboardAlarmSnapshot') | Select-Object -Last 1
    if ($null -eq $latest) { return 0 }
    return @(@($latest.Payload.alarms) | Where-Object { $null -ne $_ -and [string]$_.code -eq $code }).Count
}

# 看板只读端点。日期保持字符串，免得 ConvertFrom-Json 把偏移吃掉。答不上来（例如缺陷版本上没有这个端点）时为 $null，
# 让判据如实记成失败，而不是让整个场景停在第一次读它的地方。
function Get-Endpoint {
    $response = Invoke-WebRequest -Uri $endpoint -NoProxy -TimeoutSec 10 -SkipHttpErrorCheck
    if ([int]$response.StatusCode -ne 200) {
        $journal.Observe('endpoint-status', [int]$response.StatusCode, $null)
        return $null
    }
    return ($response.Content | ConvertFrom-Json -DateKind String)
}

# 端点的 slots；端点答不上来时为空。不用 @($e.slots)：$e 为 $null 时那是一个元素的数组。
function Get-Slots([object]$e) {
    if ($null -eq $e -or $null -eq $e.slots) { return , @() }
    return , @($e.slots)
}

function Format-EndpointRow([object]$row) {
    if ($null -eq $row) { return '(no row)' }
    $readings = if ($null -eq $row.readings) { 'readings null' } else {
        "readings v$($row.readings.safetyStateVersion) $($row.readings.lockState)/$($row.readings.physicalState)/$($row.readings.unlockOutputState) changed=$($row.readings.changedSinceObserved)"
    }
    return "$($row.agvId) slot $($row.slotNo) $($row.stationId) $($row.operationType) '$($row.expectedAction)' raisedAt $($row.raisedAt) waited $($row.waitedSeconds)s stationTimeout=$($row.stationTimeoutDoorNotClosed) $readings"
}

# 看板页上「期待动作超时」那张卡片，解码之后的纯文本；页面上没有这张卡片时为 $null。
function Get-DashboardCard {
    $html = (Invoke-WebRequest -Uri $Context.DashboardUrl -NoProxy -TimeoutSec 10).Content
    $match = [regex]::Match($html, '<section class="card" id="expected-action-overdue">(?s:.*?)</section>')
    if (-not $match.Success) { return $null }
    return ([Net.WebUtility]::HtmlDecode(($match.Value -replace '<[^>]+>', ' ')) -replace '\s+', ' ').Trim()
}

# HMI 上的期待动作超时提示：不在 UIA 树里（Collapsed）时为 $null，否则为它的文字。
function Get-HmiOverdue {
    $element = $Context.Onboard.Element('AutomationId', 'ExpectedActionOverdue')
    if (-not $element) { return $null }
    return [string]$element.Current.Name
}

# 模拟器一仓的门／货／锁反馈／开锁输出，折成快照读数的写法：锁 LOCKED／UNLOCKED、光幕 EMPTY／OCCUPIED、开锁输出 RESET／ACTIVE。
function Get-SimulatorReading([int]$slot) {
    $parts = (Get-L2SlotPhysical -Simulator $simulator -SlotNo $slot) -split '/'
    return "$(if ($parts[2] -eq '1') { 'LOCKED' } else { 'UNLOCKED' })/$($parts[1])/$(if ($parts[3] -eq '1') { 'ACTIVE' } else { 'RESET' })"
}

function Get-TrafficLines { return , @(@((Get-L2RealTraffic $proxy).lines)) }

# --- 1. 到站、扫码，车为装货开门；没人放货 -----------------------------------------------------------------------

Invoke-L2PickupAndScan -Context $Context -DemandIdWire $demandIdWire -DemandId $demandId -Sublot $sublot
$load = Wait-L2WaitingOperator -Context $Context -DemandId $demandId -OperationType 'Load'
$attemptId = $load.AttemptId
$slotNo = $load.SlotNo
$firstUnlock = @((Get-L2OperationProgress -Connection $connection -AttemptId $attemptId) | Where-Object { $_.Phase -eq 'UNLOCKING' })[0].At
$crossing = $firstUnlock.Add($threshold)
$journal.Note("Slot $slotNo first unlocked at $($firstUnlock.ToString('o')); the threshold is crossed at $($crossing.ToString('o')).")

$runtime = Wait-L2Condition -Description 'the journey records that it waits for the load result, with its station deadline' `
    -Journal $journal -Criterion 'awaiting-load-result' -TimeoutSeconds 30 `
    -Probe { Get-L2Runtime -Connection $connection -DemandId $demandId } `
    -Until { param($v) [string]$v.Stage -eq 'AwaitingLoadResult' -and $null -ne (ConvertTo-L2Instant $v.StationDepartureWaitStartedAt) }
$waitStartedAt = [string]$runtime.StationDepartureWaitStartedAt
$deadline = (ConvertTo-L2Instant $runtime.StationDepartureWaitStartedAt).Add($window)
$pickupStationId = [string]$runtime.PickupStationId
$journal.Note("Station deadline is $($deadline.ToString('o')) (arrival + $window); pickup station $pickupStationId.")
if ($crossing -ge $deadline) {
    throw "The threshold ($($crossing.ToString('o'))) would be crossed after the station deadline ($($deadline.ToString('o'))); the setup leaves no room."
}

$session0 = Get-L2RealSession $connection $agvId
$generation0 = [long]$session0.SessionGeneration
$handshakeVersion = (@((Get-L2RealInbound $connection 'SafetyStateSnapshot') | Where-Object { [long]$_.Generation -eq $generation0 } |
        ForEach-Object { [long]$_.Payload.safetyStateVersion }) | Measure-Object -Maximum).Maximum
$journal.Note("Session generation $generation0; its handshake SafetyStateSnapshot is version $handshakeVersion.")

# --- 2. 门槛之前空关一次：车自己重开 -----------------------------------------------------------------------------

$null = Wait-L2Condition -Description "$firstCloseAfter after the first unlock" -Journal $journal -Criterion 'first-close-due' `
    -TimeoutSeconds 30 -Probe { [DateTimeOffset]::UtcNow } -Until { param($v) $v -ge $firstUnlock.Add($firstCloseAfter) }
$reopen = Invoke-L2CloseOverOppositeState -Context $Context -AttemptId $attemptId -SlotNo $slotNo -Criterion 'reopen-before-threshold'
$unlockings = Get-L2PhaseCount -Connection $connection -AttemptId $attemptId -Phase 'UNLOCKING'
$assertions.Add(
    'L2-EAO-01', "空关后车自己重开：这次装货的 UNLOCKING 进度 ≥ 2 条，$slotNo 号仓门又弹开、空着、开锁输出复位，重开早于门槛",
    ($unlockings -ge 2 -and $reopen.After.Physical -eq 'OPEN/EMPTY/0/0' -and $null -ne $reopen.ReopenedAt -and $reopen.ReopenedAt -lt $crossing),
    'UNLOCKING ≥ 2 / OPEN/EMPTY/0/0 / 重开早于门槛',
    "UNLOCKING $unlockings / $($reopen.After.Physical) / 重开于第一次开锁后 $(if ($reopen.ReopenedAt) { [math]::Round(($reopen.ReopenedAt - $firstUnlock).TotalSeconds, 1) }) s")
$reopenGap = if ($reopen.ReopenedAt) { $reopen.ReopenedAt - $firstUnlock } else { [TimeSpan]::Zero }
if ($reopenGap -lt $tolerance + $tolerance + $tolerance) {
    throw "The reopen came $($reopenGap.TotalSeconds) s after the first unlock; that is too close to tell 'counted from the first unlock' from 'counted from the reopen' within $tolerance."
}

# --- 3. 门槛之前读一次：什么都还没有 -------------------------------------------------------------------------------

$preOverdue = Get-LatestOverdueCount
$preEndpoint = Get-Endpoint
$preHmi = Get-HmiOverdue
$preCard = Get-DashboardCard
$preRequests = @((Get-TrafficLines) | Where-Object { $_.direction -eq 'server->onboard' -and $_.messageType -eq 'SafetyStateSnapshotRequested' }).Count
$preReadAt = [DateTimeOffset]::UtcNow
$journal.Note("Before the threshold: card '$preCard'.")
$assertions.Add(
    'L2-EAO-02', '门槛之前没有超时告警：最新一份告警快照里没有这个码，端点 slots 为空，HMI 上 ExpectedActionOverdue 不在 UIA 树里，服务端没要过中途快照；读完仍在门槛之前',
    ($preOverdue -eq 0 -and (Get-Slots $preEndpoint).Count -eq 0 -and $null -eq $preHmi -and $preRequests -eq 0 -and $preReadAt -lt $crossing),
    '告警 0 / 端点 0 行 / HMI 无 / 请求 0 / 读于门槛前',
    "告警 $preOverdue / 端点 $((Get-Slots $preEndpoint).Count) 行 / HMI $(if ($null -eq $preHmi) { '无' } else { "'$preHmi'" }) / 请求 $preRequests / 读于门槛前 $([math]::Round(($crossing - $preReadAt).TotalSeconds, 1)) s")

# --- 4. 越过门槛：告警、线上请求、中途快照 -------------------------------------------------------------------------

$first = Wait-L2RealOrLast -Description "the onboard reported $code for slot $slotNo" `
    -Journal $journal -Criterion 'overdue-reported' -TimeoutSeconds ([int]$threshold.TotalSeconds + 30) `
    -Probe { @((Get-OverdueReports $slotNo) | Where-Object { $null -ne $_.Alarm }) | Select-Object -First 1 } `
    -Until { param($v) $null -ne $v }
if ($null -eq $first) {
    # 缺陷版本（车载端没有这条告警）走到这里：后面每一条都建立在这份告警上，如实记成未到达。
    Add-L2RealNotReached $assertions @('L2-EAO-03', 'L2-EAO-04', 'L2-EAO-05', 'L2-EAO-06', 'L2-EAO-07', 'L2-EAO-08', 'L2-EAO-09',
        'L2-EAO-10', 'L2-EAO-13', 'L2-EAO-14', 'L2-EAO-11', 'L2-EAO-12') "越过门槛 $([int]$threshold.TotalSeconds + 30) 秒内车载端没有报 $code"
    $journal.Note('Scenario stopped: no overdue alarm to follow.')
    return
}
$alarm = $first.Alarm
$raisedAt = ConvertTo-L2RealInstant $alarm.raisedAt
$fromFirst = $raisedAt - $crossing
$fromReopen = $raisedAt - $reopen.ReopenedAt.Add($threshold)
$expectedActions = @("关好${slotNo}号仓门", "放入货物并关好${slotNo}号仓门", "取出货物并关好${slotNo}号仓门")
$assertions.Add(
    'L2-EAO-03', "越过门槛后告警出现且字段正确：code、subjectType=SLOT、subjectId=$slotNo、displayMessage 是三种期待动作之一；raisedAt ≈ 第一次开锁 + 门槛（±$($tolerance.TotalSeconds) s），不 ≈ 重开 + 门槛",
    ([string]$alarm.subjectType -eq 'SLOT' -and [string]$alarm.subjectId -eq [string]$slotNo -and
        [string]$alarm.displayMessage -in $expectedActions -and
        [math]::Abs($fromFirst.TotalSeconds) -le $tolerance.TotalSeconds -and [math]::Abs($fromReopen.TotalSeconds) -gt $tolerance.TotalSeconds),
    "$code / SLOT / $slotNo / 三种期待动作之一 / 距第一次开锁 + 门槛 ≤ $($tolerance.TotalSeconds) s / 距重开 + 门槛 > $($tolerance.TotalSeconds) s",
    "$($alarm.code) / $($alarm.subjectType) / $($alarm.subjectId) / '$($alarm.displayMessage)' / 距第一次开锁 + 门槛 $([math]::Round($fromFirst.TotalSeconds, 2)) s / 距重开 + 门槛 $([math]::Round($fromReopen.TotalSeconds, 2)) s")

# 线上：告警那一行之后，同一连接上服务端要快照、车回快照。代理记下的 messageId 与库里的大小写可能不同，按不分大小写比。
$wire = Wait-L2RealOrLast -Description 'the request and the snapshot after the alarm crossed the relay' `
    -Journal $journal -Criterion 'snapshot-request-on-the-wire' -TimeoutSeconds 20 `
    -Probe {
        $lines = Get-TrafficLines
        $at = -1
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i].direction -eq 'onboard->server' -and [string]$lines[$i].messageId -eq $first.MessageId) { $at = $i; break }
        }
        $request = -1; $answer = -1
        if ($at -ge 0) {
            for ($i = $at + 1; $i -lt $lines.Count; $i++) {
                if ($request -lt 0 -and $lines[$i].direction -eq 'server->onboard' -and $lines[$i].messageType -eq 'SafetyStateSnapshotRequested') { $request = $i; continue }
                if ($request -ge 0 -and $lines[$i].direction -eq 'onboard->server' -and $lines[$i].messageType -eq 'SafetyStateSnapshot') { $answer = $i; break }
            }
        }
        [pscustomobject]@{
            Alarm   = if ($at -ge 0) { [int]$lines[$at].connection } else { $null }
            Request = if ($request -ge 0) { [int]$lines[$request].connection } else { $null }
            Answer  = if ($answer -ge 0) { [int]$lines[$answer].connection } else { $null }
            Connections = @((Get-L2RealTraffic $proxy).connections).Count
        }
    } `
    -Until { param($v) $null -ne $v.Answer }
$assertions.Add(
    'L2-EAO-04', '线上：这份告警快照之后，同一连接上出现 server->onboard:SafetyStateSnapshotRequested，随后 onboard->server:SafetyStateSnapshot；到此只有一条连接',
    ($null -ne $wire.Alarm -and $wire.Request -eq $wire.Alarm -and $wire.Answer -eq $wire.Alarm -and $wire.Connections -eq 1),
    '告警、请求、快照同一连接 / 1 条连接',
    "告警 #$($wire.Alarm) 请求 #$($wire.Request) 快照 #$($wire.Answer) / $($wire.Connections) 条连接")

$mid = Wait-L2RealOrLast -Description 'the server took the mid-session SafetyStateSnapshot' `
    -Journal $journal -Criterion 'mid-session-snapshot' -TimeoutSeconds 20 `
    -Probe { @((Get-L2RealInbound $connection 'SafetyStateSnapshot') | Where-Object {
                [long]$_.Generation -eq $generation0 -and [long]$_.Payload.safetyStateVersion -gt $handshakeVersion }) | Select-Object -First 1 } `
    -Until { param($v) $null -ne $v }
$midVersion = if ($mid) { [long]$mid.Payload.safetyStateVersion } else { $null }
$sessionMid = Get-L2RealSession $connection $agvId
$footprint = Get-RecoveryFootprint
$assertions.Add(
    'L2-EAO-05', "中途快照被采纳、不把会话打回握手：同一代次 $generation0 有第二份 SafetyStateSnapshot，版本大于握手那份（$handshakeVersion），回应 SnapshotAppliedAck + SessionReadiness；会话仍 Ready、不是 HANDSHAKE_INCOMPLETE；没有恢复痕迹",
    ($null -ne $mid -and (@($mid.ResponseTypes) -join ',') -eq 'SnapshotAppliedAck,SessionReadiness' -and
        [string]$sessionMid.Readiness -eq 'Ready' -and [string]$sessionMid.ReasonCode -ne 'HANDSHAKE_INCOMPLETE' -and
        [long]$sessionMid.SessionGeneration -eq $generation0 -and $footprint -eq $noRecovery),
    "v > $handshakeVersion / SnapshotAppliedAck,SessionReadiness / gen $generation0 Ready / $noRecovery",
    "$(if ($mid) { "v$midVersion / $(@($mid.ResponseTypes) -join ',')" } else { '(no mid-session snapshot)' }) / $(Format-L2RealSession $sessionMid) / $footprint")

# --- 5. 端点、看板页、HMI ---------------------------------------------------------------------------------------

# 端点的读数取本代次最新一份快照；等它换成中途那份再读，免得和采纳赛跑。没等到就按读到的判。
$row = Wait-L2RealOrLast -Description 'the endpoint lists the slot with the mid-session readings' `
    -Journal $journal -Criterion 'endpoint-row' -TimeoutSeconds 15 `
    -Probe { $e = Get-Endpoint; $r = Get-Slots $e; [pscustomobject]@{ Endpoint = $e; Row = $(if ($r.Count -gt 0) { $r[0] } else { $null }); Count = $r.Count } } `
    -Until { param($v) $v.Count -eq 1 -and $null -ne $v.Row.readings -and $null -ne $midVersion -and [long]$v.Row.readings.safetyStateVersion -eq $midVersion }
$simulatorReading = Get-SimulatorReading $slotNo
$overdueRow = $row.Row
$journal.Note("Endpoint row: $(Format-EndpointRow $overdueRow); simulator $simulatorReading.")
$assertions.Add(
    'L2-EAO-06', '端点一行、字段对得上：thresholdSeconds = 20；slots 恰好一行，车、仓、取货站、LOAD、期待动作与 raisedAt 同告警，waitedSeconds ≥ 门槛，站点期限未过',
    ($null -ne $row.Endpoint -and [long]$row.Endpoint.thresholdSeconds -eq [long]$threshold.TotalSeconds -and $row.Count -eq 1 -and
        [string]$overdueRow.agvId -eq $agvId -and [int]$overdueRow.slotNo -eq $slotNo -and [string]$overdueRow.stationId -eq $pickupStationId -and
        [string]$overdueRow.operationType -eq 'LOAD' -and [string]$overdueRow.expectedAction -eq [string]$alarm.displayMessage -and
        (ConvertTo-L2RealInstant $overdueRow.raisedAt) -eq $raisedAt -and [long]$overdueRow.waitedSeconds -ge [long]$threshold.TotalSeconds -and
        $overdueRow.stationTimeoutDoorNotClosed -eq $false),
    "threshold 20 / 1 行 / $agvId slot $slotNo $pickupStationId LOAD '$($alarm.displayMessage)' raisedAt $($alarm.raisedAt) waited ≥ 20 stationTimeout=False",
    "$(if ($null -eq $row.Endpoint) { '端点答不上来' } else { "threshold $($row.Endpoint.thresholdSeconds)" }) / $($row.Count) 行 / $(Format-EndpointRow $overdueRow)")
$readings = if ($null -ne $overdueRow) { $overdueRow.readings } else { $null }
$assertions.Add(
    'L2-EAO-07', "读数来自中途快照且与模拟器一致：readings 非空，版本 = 中途那份（v$midVersion，不是握手的 v$handshakeVersion），锁／光幕／开锁输出 = 模拟器此刻（$simulatorReading），changedSinceObserved = false",
    ($null -ne $readings -and $null -ne $midVersion -and [long]$readings.safetyStateVersion -eq $midVersion -and
        "$($readings.lockState)/$($readings.physicalState)/$($readings.unlockOutputState)" -eq $simulatorReading -and
        $simulatorReading -eq 'UNLOCKED/EMPTY/RESET' -and $readings.changedSinceObserved -eq $false),
    "v$midVersion UNLOCKED/EMPTY/RESET changed=False（模拟器 UNLOCKED/EMPTY/RESET）",
    $(if ($null -eq $readings) { 'readings null' } else { "v$($readings.safetyStateVersion) $($readings.lockState)/$($readings.physicalState)/$($readings.unlockOutputState) changed=$($readings.changedSinceObserved)（模拟器 $simulatorReading）" }))

$card = Wait-L2RealOrLast -Description 'the dashboard page shows the slot on its expected-action-overdue card' `
    -Journal $journal -Criterion 'dashboard-card' -TimeoutSeconds 15 `
    -Probe { Get-DashboardCard } `
    -Until { param($v) $null -ne $v -and $v.Contains([string]$alarm.displayMessage) }
$journal.Note("Card after the threshold: '$card'.")
$assertions.Add(
    'L2-EAO-08', '看板页出卡片：「期待动作超时」卡片门槛前写「无期待动作超时的仓位」，越过门槛后有这台车、这个仓、装货与期待动作的一行',
    ($null -ne $preCard -and $preCard.Contains('无期待动作超时的仓位') -and $null -ne $card -and
        $card.Contains("$agvId $pickupStationId 装货 $slotNo $($alarm.displayMessage)") -and -not $card.Contains('无期待动作超时的仓位')),
    "门槛前「无期待动作超时的仓位」/ 门槛后「$agvId $pickupStationId 装货 $slotNo $($alarm.displayMessage)」",
    "门槛前 '$preCard' / 门槛后 '$card'")

$hmi = Wait-L2RealOrLast -Description 'the HMI shows the expected-action-overdue notice' `
    -Journal $journal -Criterion 'hmi-overdue' -TimeoutSeconds 15 `
    -Probe { Get-HmiOverdue } -Until { param($v) $null -ne $v -and $v.Contains('已上报') }
$assertions.Add(
    'L2-EAO-09', 'HMI 提示「已上报」：ExpectedActionOverdue 在 UIA 树里，文字含告警的 displayMessage 与「已上报」，不是断开文案',
    ($null -ne $hmi -and $hmi.Contains([string]$alarm.displayMessage) -and $hmi.Contains('已上报') -and -not $hmi.Contains('与服务端断开')),
    "含 '$($alarm.displayMessage)' 与 '已上报'", $(if ($null -eq $hmi) { '(不在 UIA 树里)' } else { "'$hmi'" }))

# --- 6. 上报不改变行为（守护判据） ------------------------------------------------------------------------------

$progress = Get-L2OperationProgress -Connection $connection -AttemptId $attemptId
$lastPhase = if ($progress.Count -gt 0) { $progress[-1].Phase } else { '(none)' }
$results = Get-L2OperationResults -Connection $connection -AttemptId $attemptId
$held = Get-L2Runtime -Connection $connection -DemandId $demandId
$footprint = Get-RecoveryFootprint
$assertions.Add(
    'L2-EAO-10', '上报不改变行为（守护判据）：告警在时这次装货没有结果，最新进度仍是开锁或等操作员，旅程仍 AwaitingLoadResult，期限起点不变，没有恢复痕迹',
    ($results.Count -eq 0 -and $lastPhase -in @('UNLOCKING', 'WAITING_OPERATOR') -and [string]$held.Stage -eq 'AwaitingLoadResult' -and
        [string]$held.StationDepartureWaitStartedAt -eq $waitStartedAt -and $footprint -eq $noRecovery),
    "0 result / UNLOCKING 或 WAITING_OPERATOR / AwaitingLoadResult / 起点 $waitStartedAt / $noRecovery",
    "$($results.Count) result / $lastPhase / $($held.Stage) / 起点 $($held.StationDepartureWaitStartedAt) / $footprint")

# --- 7. 同一超时再报一次：不出第二行，不覆盖第一行 ----------------------------------------------------------------

# 门槛后再空关一次、车重开：这一仓的状态变化让车载端再发告警快照。判的是端点仍恰好一行、raisedAt 不变，
# 以及各份快照里这一仓的超时始终是同一个 alarmId 与 raisedAt（`L2-EAO-13`），加上服务端为这次变化又要了一次
# 快照（`L2-EAO-14`）。两条分开：前者是不变性（不出第二行、不覆盖），后者是活性（服务端确实重新看了一眼），
# 混成一条会让 FAIL 指不到是哪一端出了事。
# 位置基线，不是计数基线。用「请求总数变多了」挡不住一次重握手：握手里车载端必发一份
# SafetyStateSnapshot，版本号单调递增因而必然越过 $midVersion，而端点只认 messageType 是
# SafetyStateSnapshot 就前推 latestVersion、不区分握手与应答——于是三条合取项可以在**服务端一次都没
# 为这次重开要过快照**的情况下全部满足。这不是推演：本票的红证据
# evidence/l2/cs204-eao11-red-no-redecide 里流量两项就都满足了（请求 7 / 快照 8），那 7/8 是会话被打回
# HANDSHAKE_INCOMPLETE 之后反复重握手刷出来的。所以照 L2-EAO-04 的写法按行序定位，并把两行绑回
# 告警那条连接（重握手会开新连接，连接号与总数都会变）。
$linesBeforeAgain = (Get-TrafficLines).Count
$again = Invoke-L2CloseOverOppositeState -Context $Context -AttemptId $attemptId -SlotNo $slotNo -Criterion 'reopen-after-threshold'

# 主判据读代理流量：重开那一行之后，服务端发了一条 SafetyStateSnapshotRequested，车随后回了一份
# SafetyStateSnapshot，两条都在告警那条连接上、而且全程仍只有这一条连接。
# 服务端为什么会再要一次，见 OnboardMessageProcessor.AppendSafetySnapshotRequest 的注释与
# AffectsAnOverdueSlotAsync：一条影响到已超时仓的 SafetyStateChanged 就让下一次应答捎上一条请求。
$reAsked = Wait-L2RealOrLast -Description 'the server asked for another snapshot after the reopen and the onboard answered, on the same connection' `
    -Journal $journal -Criterion 'snapshot-requested-again' -TimeoutSeconds 20 `
    -Probe {
        $lines = Get-TrafficLines
        $request = -1; $answer = -1
        for ($i = $linesBeforeAgain; $i -lt $lines.Count; $i++) {
            if ($request -lt 0 -and $lines[$i].direction -eq 'server->onboard' -and $lines[$i].messageType -eq 'SafetyStateSnapshotRequested') { $request = $i; continue }
            if ($request -ge 0 -and $lines[$i].direction -eq 'onboard->server' -and $lines[$i].messageType -eq 'SafetyStateSnapshot') { $answer = $i; break }
        }
        [pscustomobject]@{
            Request     = if ($request -ge 0) { [int]$lines[$request].connection } else { $null }
            Answer      = if ($answer -ge 0) { [int]$lines[$answer].connection } else { $null }
            Connections = @((Get-L2RealTraffic $proxy).connections).Count
        }
    } `
    -Until { param($v) $null -ne $v.Answer }

# 端点读数的版本号前进，是同一件事走完到看板的那一端。它**不能单独当判据**，而且不只是因为
# 「车载端今天不主动推快照」这一条：握手那份快照同样会让它前进，所以它连一次重握手都分辨不出来。
# 「服务端又要了一次」由上面那条按行序定位、并绑住连接的流量判据承担，这里只作端到端佐证。
$afterVersion = Wait-L2RealOrLast -Description 'the endpoint readings moved past the mid-session snapshot' `
    -Journal $journal -Criterion 'endpoint-readings-version-again' -TimeoutSeconds 20 `
    -Probe {
        $rows = Get-Slots (Get-Endpoint)
        if ($rows.Count -gt 0 -and $null -ne $rows[0].readings) { [long]$rows[0].readings.safetyStateVersion } else { $null }
    } `
    -Until { param($v) $null -ne $v -and $null -ne $midVersion -and $v -gt $midVersion }
$assertions.Add(
    'L2-EAO-14', "门槛后重开，服务端又要了一次快照：重开之后的流量里出现 server->onboard:SafetyStateSnapshotRequested 与随后的 onboard->server:SafetyStateSnapshot，两条都在告警那条连接（#$($wire.Alarm)）上、全程仍只有一条连接；端点读数的版本也从中途那份（v$midVersion）前进",
    ($null -ne $reAsked.Request -and $null -ne $reAsked.Answer -and
        $reAsked.Request -eq $wire.Alarm -and $reAsked.Answer -eq $wire.Alarm -and $reAsked.Connections -eq 1 -and
        $null -ne $midVersion -and $null -ne $afterVersion -and $afterVersion -gt $midVersion),
    "重开后请求与快照各一条、都在 #$($wire.Alarm) / 1 条连接 / 端点读数 v > $midVersion",
    "请求 $(if ($null -eq $reAsked.Request) { '(无)' } else { "#$($reAsked.Request)" }) 快照 $(if ($null -eq $reAsked.Answer) { '(无)' } else { "#$($reAsked.Answer)" }) / $($reAsked.Connections) 条连接 / 端点读数 $(if ($null -eq $afterVersion) { '(无读数)' } else { "v$afterVersion" })")

$null = Wait-L2Iterations -Riot $Context.Riot -Count 3 -Journal $journal
$reports = @((Get-OverdueReports $slotNo) | Where-Object { $null -ne $_.Alarm })
$alarmIds = @($reports | ForEach-Object { [string]$_.Alarm.alarmId } | Sort-Object -Unique)
$raisedAts = @($reports | ForEach-Object { (ConvertTo-L2RealInstant $_.Alarm.raisedAt).ToString('o') } | Sort-Object -Unique)
$afterAgainSlots = Get-Slots (Get-Endpoint)
$againRow = if ($afterAgainSlots.Count -gt 0) { $afterAgainSlots[0] } else { $null }
$assertions.Add(
    'L2-EAO-13', '门槛后再空关、重开，端点仍恰好一行、raisedAt 不变；各份告警快照里这一仓的超时都是同一个 alarmId、同一个 raisedAt',
    ($again.After.Physical -eq 'OPEN/EMPTY/0/0' -and $reports.Count -ge 1 -and $alarmIds.Count -eq 1 -and $raisedAts.Count -eq 1 -and
        $afterAgainSlots.Count -eq 1 -and (ConvertTo-L2RealInstant $againRow.raisedAt) -eq $raisedAt),
    "重开 OPEN/EMPTY/0/0 / 1 个 alarmId / 1 个 raisedAt / 端点 1 行 raisedAt $($alarm.raisedAt)",
    "重开 $($again.After.Physical) / $($reports.Count) 份快照带它，$($alarmIds.Count) 个 alarmId、$($raisedAts.Count) 个 raisedAt / 端点 $($afterAgainSlots.Count) 行 $(Format-EndpointRow $againRow)")

# --- 8. 站点期限过去、门仍开着：同一行合成 ------------------------------------------------------------------------

# 超时带最后读值，不抛错（control-server#204）。这里原本是 Wait-L2Condition：期限一直没被判成超时时它抛出，
# 场景在这一行中止，`L2-EAO-11` 与 `L2-EAO-12` 两行**从判据表里消失**——读证据的人分不出「判过且通过」和
# 「根本没判」，而整轮的失败原因只剩一句 "Timed out after 90s waiting for: ..."，指不到是哪条判据。
# 更要紧的是第 9 节（放货关门闭环、撤下）整段不跑，所以 `L2-EAO-12` 这条守护判据在任何超时场合都必然取不到。
# `evidence/l2/20260919-cs167-red-05-no-redecide/` 就是这个样子：表里只有 11 行。
$blocked = Wait-L2RealOrLast -Description 'the stop passed its deadline with the door open' `
    -Journal $journal -Criterion 'station-timeout-door-not-closed' -TimeoutSeconds ([int]$window.TotalSeconds + 30) `
    -Probe { Get-L2Runtime -Connection $connection -DemandId $demandId } `
    -Until { param($v) [string]$v.BlockReasonCode -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED' }
$blockReason = if ($null -eq $blocked) { '(no runtime row)' } else { [string]$blocked.BlockReasonCode }
$merged = Wait-L2RealOrLast -Description 'the endpoint merges the station timeout into the same row' `
    -Journal $journal -Criterion 'endpoint-station-timeout' -TimeoutSeconds 15 `
    -Probe { Get-Slots (Get-Endpoint) } -Until { param($v) $v.Count -eq 1 -and $v[0].stationTimeoutDoorNotClosed -eq $true }
$mergedRow = if ($merged.Count -gt 0) { $merged[0] } else { $null }
$assertions.Add(
    'L2-EAO-11', '站点期限过后合成一行：期限过去、门仍开着（STATION_TIMEOUT_DOOR_NOT_CLOSED），端点 slots 仍恰好一行，stationTimeoutDoorNotClosed = true，raisedAt 不变',
    ($blockReason -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED' -and $merged.Count -eq 1 -and
        $mergedRow.stationTimeoutDoorNotClosed -eq $true -and (ConvertTo-L2RealInstant $mergedRow.raisedAt) -eq $raisedAt -and
        (Get-L2SlotPhysical -Simulator $simulator -SlotNo $slotNo) -like 'OPEN/*'),
    "STATION_TIMEOUT_DOOR_NOT_CLOSED / 1 行 stationTimeout=True raisedAt $($alarm.raisedAt) / 门开",
    "$blockReason / $($merged.Count) 行 $(Format-EndpointRow $mergedRow) / $(Get-L2SlotPhysical -Simulator $simulator -SlotNo $slotNo)")

# --- 9. 放货关门：闭环、撤下 ------------------------------------------------------------------------------------

$journal.Note("The operator finally puts the cargo in slot $slotNo and shuts the door.")
$null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = 'OCCUPIED' })
$null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})
$committed = Wait-L2RealOrLast -Description 'the load committed once the door was shut over the cargo' `
    -Journal $journal -Criterion 'load-committed' -TimeoutSeconds 60 `
    -Probe { [string](Get-L2StationOperation -Connection $connection -DemandId $demandId -OperationType 'Load').Status } `
    -Until { param($v) $v -eq 'Committed' }
$withdrawn = Wait-L2RealOrLast -Description 'the overdue alarm was withdrawn everywhere' `
    -Journal $journal -Criterion 'overdue-withdrawn' -TimeoutSeconds 30 `
    -Probe { [pscustomobject]@{ Alarm = Get-LatestOverdueCount; Rows = (Get-Slots (Get-Endpoint)).Count; Hmi = Get-HmiOverdue } } `
    -Until { param($v) $v.Alarm -eq 0 -and $v.Rows -eq 0 -and $null -eq $v.Hmi }
$outcomes = ((Get-L2OperationResults -Connection $connection -AttemptId $attemptId) | ForEach-Object { [string]$_.Payload.overallOutcome }) -join ','
$assertions.Add(
    'L2-EAO-12', '撤下（守护判据）：放货关门后装货 COMPLETED、Committed；随后最新一份告警快照里不再有这个码，端点 slots 为空，HMI 控件不在 UIA 树里',
    ($committed -eq 'Committed' -and $outcomes -eq 'COMPLETED' -and $withdrawn.Alarm -eq 0 -and $withdrawn.Rows -eq 0 -and $null -eq $withdrawn.Hmi),
    'Committed / COMPLETED / 告警 0 / 端点 0 行 / HMI 无',
    "$committed / $outcomes / 告警 $($withdrawn.Alarm) / 端点 $($withdrawn.Rows) 行 / HMI $(if ($null -eq $withdrawn.Hmi) { '无' } else { "'$($withdrawn.Hmi)'" })")

$journal.Note('Scenario finished.')
