#Requires -Version 7

<#
出厂配置下，装货进行中操作员按「取消装货」：服务端授权了，授权应答却在回车上的路上丢了；操作员再按一次，要沿用首发的
内容、换一个新的 messageId 发出，拿到同一个授权，把取消做完——而不是被掐连接，也不是永远卡在「已授权、车不知道」。

**来源与成对的两端票。**`trytoreachpeak0/8005-agv-program#61` 审计第 4 节 ③：
- onboard-hmi#71（批次5-19，MVP `297dd81`＋`5e29f58`）：取消每次发送新 messageId、`cancellationId` 不变；未得应答的取消
  把首发的操作员与理由写进待答取消记录，重发原样沿用。
- onboard-hmi#78（批次5-29，并入 ③ 的 `1acb018`）：在途装货的取消入口与 `recoveryResumeEnabled` 解绑，出厂就有；授权后
  先中止原执行器，再由取消执行器接手开着的仓门清空。
服务端的两道判等（收件箱按 messageId 绑首次整行、取消工作流按 `cancellationId` 绑首次 payload）不需要改：车载端换了
messageId、payload 不变，两道都放行。MVP 线参照：`ControlServer_MVP` 同名场景，onboard-hmi#39：`-001` 红（第二次按下
同一 messageId、不同内容，被掐连接）、`-003` 绿。

**与 MVP 版的两处差别，都是 v2 车载端的行为变了。**
1. 出厂配置，不开恢复入口（MVP 版要开，那时取消是恢复入口）。
2. 按取消时仓门开着，不先把空门关上：v2 执行器读到「门关了、货没放」会自动重开（onboard-hmi#72），操作员放弃装货的
   唯一出口就是按取消（program#55）。授权到达后原执行器被中止，取消执行器接手开着的那扇门，操作员把空门带上即证空。
   这里只判结果（授权、对账、需求取消、仓位最终空着锁着），不判开锁次数与顺序：对关着且有货的仓取消执行器仍逐个开锁，
   批量开锁由 onboard-hmi#104 跟进。

**丢应答靠 `tools/ControlServer.ProtocolFaultProxy` 的 `drop-message`**：代理不转发服务端写回的那一条
`LoadCancellationAuthorization`，链路不断。车载端等满 `messageTimeoutMs`（出厂 3 秒）判超时——与应答途中丢失、服务端
授权之后车载端本地抛错是同一个形状。服务端那一侧的授权是真的，车那一侧没收到也是真的。

判据只读服务端库、代理快照与模拟器快照，不读车载端界面文字。界面上只看「取消装货」入口在不在、失败提示框在不在——那是
能不能按、要不要先关框的前提，不是业务事实。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$onboard = $Context.Onboard
$simulator = $Context.Simulator
$connection = $Context.Connection
$proxy = $Context.ProtocolProxy

if ($null -eq $proxy) { throw 'This scenario needs ProtocolFaultProxy = $true in its setup file.' }
$button = '取消装货'
$failure = '取消装货失败'
$laterIds = @('L2-CAL-02', 'L2-CAL-03', 'L2-CAL-04', 'L2-CAL-05', 'L2-CAL-06', 'L2-CAL-07', 'L2-CAL-08')

function Get-Requests([string]$demandId) {
    return , @((Get-L2RealInbound $connection 'LoadCancellationStartRequested') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
}

function Get-WorkflowState([string]$cancellationId) {
    return Get-L2RealScalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$cancellationId'"
}

# --- 0. 车载端确实走代理 -------------------------------------------------------------------------------------------

$hellos = Wait-L2Condition -Description 'the onboard session was established through the protocol fault proxy' `
    -Journal $journal -Criterion 'proxy-session' -TimeoutSeconds 30 `
    -Probe { @(@((Get-L2RealTraffic $proxy).lines) | Where-Object { $_.direction -eq 'onboard->server' -and $_.messageType -eq 'SessionHello' }).Count } `
    -Until { param($v) $v -ge 1 }
$assertions.Add(
    'L2-CAL-00', '车载端的会话经协议故障代理建立（否则丢应答注入不到这条链路上）',
    ($hellos -ge 1), '>= 1 SessionHello through the proxy', $hellos)

# --- 1. 车在等操作员放货；出厂配置下「取消装货」在 ----------------------------------------------------------------

$load = Start-L2RealLoad $Context 'L2-CAL'
$demandId = $load.DemandId
$attemptId = $load.AttemptId

$offered = Wait-L2RealButtonOffered $onboard $journal $button 'onboard-cancellation-entry' 60
$assertions.Add(
    'L2-CAL-01', '出厂配置（recoveryResumeEnabled=false）下装货进行中，车载端给出可用的「取消装货」入口（onboard-hmi#78）',
    $offered, $true, $offered)
if (-not $offered) {
    Add-L2RealNotReached $assertions $laterIds '车载端没有给出「取消装货」入口'
    return
}

# --- 2. 丢掉授权应答，第一次按下：服务端授权，车等到超时 --------------------------------------------------------

$journal.Note('Arming the proxy: drop the first LoadCancellationAuthorization and keep the link up.')
$null = $proxy.Command('Put', 'drop-message', @{ messageType = 'LoadCancellationAuthorization'; count = 1 })
$connectionsBefore = @((Get-L2RealTraffic $proxy).connections).Count
$pressedAt = [DateTimeOffset]::UtcNow

$null = Invoke-L2RealConfirmedButton $onboard $journal $button $button
$dropped = Wait-L2RealOrLast -Description 'the proxy dropped the cancellation authorization' -Journal $journal `
    -Criterion 'authorization-dropped' -TimeoutSeconds 30 `
    -Probe { @((Get-L2RealTraffic $proxy).drops).Count } -Until { param($n) [int]$n -ge 1 }
# 车载端等满 messageTimeoutMs 才判失败、弹提示框；关掉它，操作员才能再按。
$noticeShown = Confirm-L2RealNotice $onboard $journal $failure 30
$first = @(Get-Requests $demandId)
$cancellationId = if ($first.Count -ge 1) { [string]$first[0].Payload.cancellationId } else { '' }
$workflowAfterFirst = if ($cancellationId) { Get-WorkflowState $cancellationId } else { '(no request)' }
$firstDecision = if ($first.Count -ge 1 -and $null -ne $first[0].ResponsePayload) { [string]$first[0].ResponsePayload.decision } else { '(none)' }
$assertions.Add(
    'L2-CAL-02', '服务端授权了第一次取消、工作流在等结果，授权应答被代理丢掉，车载端没拿到回答（报了失败）',
    ($first.Count -eq 1 -and $firstDecision -eq 'AUTHORIZED' -and $workflowAfterFirst -eq 'AwaitingResult' -and [int]$dropped -eq 1 -and $noticeShown),
    '1 条请求 / AUTHORIZED / AwaitingResult / 丢 1 条 / 车载端报失败',
    "$($first.Count) 条请求 / $firstDecision / $workflowAfterFirst / 丢 $dropped 条 / 车载端报失败=$noticeShown")
if ($first.Count -ne 1) {
    Add-L2RealNotReached $assertions @('L2-CAL-03', 'L2-CAL-04', 'L2-CAL-05', 'L2-CAL-06', 'L2-CAL-07', 'L2-CAL-08') '第一次按下没有发出取消请求'
    return
}

# --- 3. 操作员再按一次：拿到同一个授权，原执行器中止，取消执行器接手开着的门 ------------------------------------

$null = Invoke-L2RealConfirmedButton $onboard $journal $button $button
$second = Wait-L2RealOrLast -Description 'the second cancellation request was answered' -Journal $journal `
    -Criterion 'second-request' -TimeoutSeconds 30 `
    -Probe { $all = Get-Requests $demandId; if ($all.Count -ge 2) { $all[1] } else { $null } } `
    -Until { param($v) $null -ne $v -and $v.Response -ne '' }
$journal.Note("Operator takes nothing out and closes the open slot $($load.Slot).")
$null = $simulator.Command('Post', "slots/$($load.Slot)/close-door", @{})

$result = Wait-L2RealOrLast -Description 'the server received the LoadCancellationResult, or the onboard gave up' -Journal $journal `
    -Criterion 'cancellation-result' -TimeoutSeconds 90 `
    -Probe {
        $received = @((Get-L2RealInbound $connection 'LoadCancellationResult') | Where-Object { [string]$_.Payload.cancellationId -eq $cancellationId })
        if ($received.Count -ge 1) { $received[0] }
        elseif (@($onboard.WindowTitles()) -contains $failure) { 'REFUSED' }
        else { $null }
    } `
    -Until { param($v) $null -ne $v }
$secondFailed = ($result -is [string])
if ($secondFailed) { $null = Confirm-L2RealNotice $onboard $journal $failure 5 }
$assertions.Add(
    'L2-CAL-03', '授权应答丢了之后再按一次，车载端拿到授权并把取消做完、报回 LoadCancellationResult（没有再报失败）',
    (-not $secondFailed -and $null -ne $result), 'LoadCancellationResult',
    $(if ($secondFailed) { '车载端再次报失败' } elseif ($null -eq $result) { '90 s 内没有结果' } else { "LoadCancellationResult $($result.Payload.overallOutcome) → $($result.Response)" }))

# --- 4. 取消收敛 -----------------------------------------------------------------------------------------------

$settled = Wait-L2RealOrLast -Description 'the cancellation reconciled and ended the demand' -Journal $journal `
    -Criterion 'cancellation-reconciled' -TimeoutSeconds 60 `
    -Probe {
        "$(Get-WorkflowState $cancellationId) / " +
        "$(Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$demandId'") / " +
        "$(Get-L2RealScalar $connection "SELECT Status AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'") / " +
        "$(Get-L2RealStage $connection $demandId)/$(Get-L2RealScalar $connection "SELECT BlockReasonCode AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'")"
    } `
    -Until { param($v) $v -eq 'Reconciled / Cancelled / Cancelled / Completed/CANCELLED_BY_OPERATOR' }
$outcome = if ($result -isnot [string] -and $null -ne $result) { [string]$result.Payload.overallOutcome } else { '(no result)' }
$assertions.Add(
    'L2-CAL-04', '取消收敛：ALL_EMPTY，工作流 Reconciled，需求与装货 Cancelled，旅程以 CANCELLED_BY_OPERATOR 收尾',
    ($outcome -eq 'ALL_EMPTY' -and $settled -eq 'Reconciled / Cancelled / Cancelled / Completed/CANCELLED_BY_OPERATOR'),
    'ALL_EMPTY / Reconciled / Cancelled / Cancelled / Completed/CANCELLED_BY_OPERATOR', "$outcome / $settled")

# --- 5. 两次按下是两条报文，内容相同；服务端一次都没掐连接 --------------------------------------------------------

$requests = @(Get-Requests $demandId)
$messageIds = @($requests | ForEach-Object { $_.MessageId })
$payloads = @($requests | ForEach-Object { $_.PayloadJson })
$decisions = @($requests | ForEach-Object { if ($null -ne $_.ResponsePayload) { [string]$_.ResponsePayload.decision } else { '(none)' } })
$distinctIds = @($messageIds | Sort-Object -Unique).Count
$samePayload = ($payloads.Count -eq 2 -and $payloads[0] -eq $payloads[1])
$assertions.Add(
    'L2-CAL-05', '两次按下是两条 messageId 不同的取消请求，payload 相同（同一 cancellationId，沿用首发的操作员与理由），两次都拿到 AUTHORIZED（onboard-hmi#71）',
    ($messageIds.Count -eq 2 -and $distinctIds -eq 2 -and $samePayload -and ($decisions -join ',') -eq 'AUTHORIZED,AUTHORIZED'),
    '2 条 / 2 个 id / payload 相同 / AUTHORIZED,AUTHORIZED',
    "$($messageIds.Count) 条 / $distinctIds 个 id / payload $(if ($samePayload) { '相同' } else { '不同' }) / $($decisions -join ',')")
if (-not $samePayload -and $payloads.Count -gt 0) { $journal.Note("Request payloads: $($payloads -join ' || ')") }

$traffic = Get-L2RealTraffic $proxy
$connections = @($traffic.connections)
$open = @($connections | Where-Object { $null -eq $_.closedAt })
$assertions.Add(
    'L2-CAL-06', '从第一次按下到收尾，车载端没有重连：丢一次应答不换来一次掐连接',
    ($connections.Count -eq $connectionsBefore -and $open.Count -eq 1),
    "$connectionsBefore connection(s), 1 open", "$($connections.Count) connection(s), $($open.Count) open ($(Format-L2RealConnections $traffic))")

$reading = Get-L2RealSlotReading $simulator $load.Slot
$assertions.Add(
    'L2-CAL-07', '现场收在安全状态：门关、仓空、已锁、开锁输出复位',
    ($reading -eq 'CLOSED/EMPTY/1/0'), 'CLOSED/EMPTY/1/0', $reading)

# --- 6. 取消期间车没有替原命令编结果，会话一次都没被判需要恢复 ----------------------------------------------------

# 取消结算原 attempt（ADR-cross-0046：原 SlotOperationCommand 既不撤回也不改写）。MVP 的 -001..-004 在上面每一条都过了
# 的情况下，车仍替被中止的 attempt 报过结果——先是服务端重发的命令被重新执行成 FAILED，修掉之后又是中断结算在门关上时
# 把它报成 UNKNOWN——每次会话都进 RECOVERY_REQUIRED。上面没有一条判据看得见它。
$null = Wait-L2Iterations -Riot $Context.Riot -Count 4 -Journal $journal
$lateResults = @((Get-L2RealInbound $connection 'OperationResult') | Where-Object {
        [string]$_.Payload.slotOperationAttemptId -eq $attemptId -and $_.At -ge $pressedAt })
$recoveryRequired = @()
foreach ($type in @('LoadCancellationStartRequested', 'LoadCancellationResult', 'OperationResult', 'SafetyStateChanged', 'OperationProgress')) {
    foreach ($message in (Get-L2RealInbound $connection $type)) {
        if ($message.At -lt $pressedAt) { continue }
        foreach ($answer in $message.Answers) {
            if ([string]$answer.messageType -eq 'SessionReadiness' -and [string]$answer.payload.readiness -eq 'RECOVERY_REQUIRED') {
                $recoveryRequired += "$type→$(@($answer.payload.reasonCodes) -join '+')"
            }
        }
    }
}
$lateText = @($lateResults | ForEach-Object { "$($_.Payload.overallOutcome)" }) -join '; '
$assertions.Add(
    'L2-CAL-08', '从第一次按下到收尾，车没有替这次 attempt 报 OperationResult，服务端一次都没回 RECOVERY_REQUIRED',
    ($lateResults.Count -eq 0 -and $recoveryRequired.Count -eq 0),
    'OperationResult 0 条 / RECOVERY_REQUIRED 0 次',
    "OperationResult $($lateResults.Count) 条$(if ($lateText) { "（$lateText）" }) / RECOVERY_REQUIRED $($recoveryRequired.Count) 次$(if ($recoveryRequired) { "（$($recoveryRequired -join '; ')）" })")

$journal.Note('Scenario finished.')
