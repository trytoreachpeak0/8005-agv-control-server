#Requires -Version 7

<#
出厂配置下，装货进行中操作员按「取消装货」：服务端授权了，授权应答却在回车上的路上丢了；操作员再按一次，要沿用首发的
内容、换一个新的 messageId 发出，拿到同一个授权，把取消做完——而不是被掐连接，也不是永远卡在「已授权、车不知道」。

**来源与成对的两端票。**`trytoreachpeak0/8005-agv-program#61` 审计第 4 节 ③：
- onboard-hmi#71（批次5-19，MVP `297dd81`＋`5e29f58`）：取消每次发送新 messageId、`cancellationId` 不变；未得应答的取消
  把首发的操作员与理由写进待答取消记录，重发原样沿用。
- onboard-hmi#78（批次5-29，并入 ③ 的 `1acb018`）：在途装货的取消入口与 `recoveryResumeEnabled` 解绑，出厂就有。
服务端的两道判等（收件箱按 messageId 绑首次整行、取消工作流按 `cancellationId` 绑首次 payload）放行这种重发；协议 6.0.0
起服务端对已经授权、记录在案的同一个 `cancellationId` 照记录答同一个授权，不再拿此刻的读数重判（control-server#837）。
MVP 线参照：`ControlServer_MVP` 同名场景，onboard-hmi#39：`-001` 红（第二次按下同一 messageId、不同内容，被掐连接）、`-003` 绿。

**协议 6.0.0 的形状（control-server#837、onboard-hmi#392 改）。**修订后的 `REQ-0211`（CP-0021）：装货开始之后只有全部目标仓
都空、门都证明锁闭、开锁输出都复位才能取消，授权范围为空，不为取消开任何门。5.x 的形状——第一仓装好一篮、第二仓开着时
按取消，取消执行器接手开着的门、再开已装货的仓把货取出——在 v6 下第一次按就该被拒（`LOAD_CANCELLATION_BASKET_LOADED`），
联跑 39654207191 里本场景红在车载端 `RECOVERY_RESPONSE_SCOPE_MISMATCH` 也是这个根。所以改成：两仓的需求，车开第一仓等人，
操作员不放货、把空门关上锁好；服务端拿到关门之后的快照（它在装货中目标仓的每次变化之后都要一份）之后，再按取消。
原来的 `L2-CAL-10`「一次只开一扇」（onboard-hmi#106 的真装置复验，取消清空循环给已装货的仓开锁）在 v6 下没有那条路了，
换成「取消全程不开任何门」：从第一次按下到收尾，模拟器每 100 ms 的采样里任一时刻都没有未锁闭的仓。

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
$laterIds = @('L2-CAL-02', 'L2-CAL-03', 'L2-CAL-04', 'L2-CAL-05', 'L2-CAL-06', 'L2-CAL-07', 'L2-CAL-08', 'L2-CAL-09', 'L2-CAL-10')

# Assign the result, never wrap the call in @(): it hands back the whole result set as one array, and @() would keep
# that as a single element (cancellation-authorization-lost-001 joined both decisions into one string that way).
function Get-Requests([string]$demandId) {
    return , @((Get-L2RealInbound $connection 'LoadCancellationStartRequested') | Where-Object { [string]$_.Payload.demandId -eq $demandId })
}

function Get-WorkflowState([string]$cancellationId) {
    return Get-L2RealScalar $connection "SELECT State AS Value FROM RecoveryWorkflows WHERE WorkflowId = '$cancellationId'"
}

<#
Samples the simulator snapshot every 100 ms on its own thread and emits a record each time the set of slots that are not
closed-and-locked changes: door open, lock feedback other than locked, or the unlock output still on. The first sample is
always emitted. The scenario's own thread spends seconds inside single waits, so it cannot do the sampling itself. The job
stops itself after ten minutes in case the scenario never reaches Stop-DoorSampler.
#>
function Start-DoorSampler {
    $uri = "$($simulator.BaseUrl)/$($simulator.Prefix)/snapshot"
    return Start-ThreadJob -ArgumentList $uri -ScriptBlock {
        param($uri)
        $deadline = [DateTimeOffset]::UtcNow.AddMinutes(10)
        $last = $null
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            try {
                $snapshot = Invoke-RestMethod -Uri $uri -TimeoutSec 5
                $notLocked = @($snapshot.slots | Where-Object {
                        $_.doorState -ne 'CLOSED' -or [int]$_.lockFeedbackRaw -ne 1 -or [int]$_.unlockOutputRaw -ne 0
                    } | ForEach-Object { [int]$_.slotNo } | Sort-Object)
                $key = $notLocked -join ','
                if ($key -ne $last) {
                    [pscustomobject]@{ At = [DateTimeOffset]::UtcNow.ToString('o'); Slots = $notLocked }
                    $last = $key
                }
            } catch {
                [pscustomobject]@{ At = [DateTimeOffset]::UtcNow.ToString('o'); Error = $_.Exception.Message }
            }
            Start-Sleep -Milliseconds 100
        }
    }
}

function Stop-DoorSampler([object]$job) {
    Stop-Job $job
    $changes = @(Receive-Job $job)
    Remove-Job $job -Force
    return , $changes
}

# --- 0. 车载端确实走代理 -------------------------------------------------------------------------------------------

$hellos = Wait-L2Condition -Description 'the onboard session was established through the protocol fault proxy' `
    -Journal $journal -Criterion 'proxy-session' -TimeoutSeconds 30 `
    -Probe { @(@((Get-L2RealTraffic $proxy).lines) | Where-Object { $_.direction -eq 'onboard->server' -and $_.messageType -eq 'SessionHello' }).Count } `
    -Until { param($v) $v -ge 1 }
$assertions.Add(
    'L2-CAL-00', '车载端的会话经协议故障代理建立（否则丢应答注入不到这条链路上）',
    ($hellos -ge 1), '>= 1 SessionHello through the proxy', $hellos)

# --- 1. 车开第一仓等人，操作员不放货、把空门关好；出厂配置下「取消装货」在 ----------------------------------------------

$load = Start-L2RealLoad $Context 'L2-CAL' 8
$demandId = $load.DemandId
$attemptId = $load.AttemptId
if ($load.TargetSlots.Count -ne 2) {
    throw "An 8-box demand was expected to target two slots; the load targets $($load.TargetSlots -join ', ')."
}
$openedSlot = $load.Slot
$readings = Close-L2RealEmptySlotForCancellation $Context $openedSlot
$journal.Note("Slot $openedSlot reads $($readings.Shut); $($readings.Changes) change(s) since, $($readings.Snapshots) snapshot(s) after the last.")

$offered = Wait-L2RealButtonOffered $onboard $journal $button 'onboard-cancellation-entry' 60
$assertions.Add(
    'L2-CAL-01', "出厂配置（recoveryResumeEnabled=false）下装货进行中、第 $openedSlot 仓空门关好锁上之后，车载端给出可用的「取消装货」入口（onboard-hmi#78、#392），服务端手上有关门之后的快照（control-server#837）",
    ($offered -and $readings.Shut -eq 'CLOSED/EMPTY/1/0' -and $readings.Snapshots -ge 1),
    '入口可用 / CLOSED/EMPTY/1/0 / 关门后快照 ≥1',
    "入口可用=$offered / $($readings.Shut) / 关门后变化 $($readings.Changes)、最后一条之后快照 $($readings.Snapshots)")
if (-not $offered) {
    Add-L2RealNotReached $assertions $laterIds '车载端没有给出「取消装货」入口'
    return
}

$sampler = Start-DoorSampler
$samplerStopped = $false
try {

    # --- 2. 丢掉授权应答，第一次按下：服务端授权，车等到超时 --------------------------------------------------------

    $journal.Note('Arming the proxy: drop the first LoadCancellationAuthorization and keep the link up.')
    $null = $proxy.Command('Put', 'drop-message', @{ messageType = 'LoadCancellationAuthorization'; count = 1 })
    $connectionsBefore = @((Get-L2RealTraffic $proxy).connections).Count
    $pressedAt = [DateTimeOffset]::UtcNow

    $null = Invoke-L2RealConfirmedButton $onboard $journal $button $button
    $dropped = Wait-L2RealOrLast -Description 'the proxy dropped the cancellation authorization' -Journal $journal `
        -Criterion 'authorization-dropped' -TimeoutSeconds 30 `
        -Probe { @((Get-L2RealTraffic $proxy).drops).Count } -Until { param($n) [int]$n -ge 1 }
    # 车载端等满 messageTimeoutMs 才判失败、弹提示框；关掉它，操作员才能再按。提示框是驱动的前提，不是判据：它靠窗口标题
    # 识别，判据不依赖车载端界面文案。
    $noticeShown = Confirm-L2RealNotice $onboard $journal $failure 30
    $first = Get-Requests $demandId
    $cancellationId = if ($first.Count -ge 1) { [string]$first[0].Payload.cancellationId } else { '' }
    $workflowAfterFirst = if ($cancellationId) { Get-WorkflowState $cancellationId } else { '(no request)' }
    $firstAnswer = if ($first.Count -ge 1) { $first[0].ResponsePayload } else { $null }
    $firstDecision = if ($null -ne $firstAnswer) { [string]$firstAnswer.decision } else { '(none)' }
    $firstSlots = if ($null -ne $firstAnswer) { Format-L2RealSlots @($firstAnswer.slots) } else { '' }
    $assertions.Add(
        'L2-CAL-02', '服务端授权了第一次取消（范围为空，协议 6.0.0）、工作流在等结果，授权应答被代理丢掉（车载端提示框只记日志，不作判据）',
        ($first.Count -eq 1 -and $firstDecision -eq 'AUTHORIZED' -and $firstSlots -eq '' -and $workflowAfterFirst -eq 'AwaitingResult' -and [int]$dropped -eq 1),
        '1 条请求 / AUTHORIZED slots= / AwaitingResult / 丢 1 条',
        "$($first.Count) 条请求 / $firstDecision slots=$firstSlots$(if ($null -ne $firstAnswer -and $null -ne $firstAnswer.problem) { " $($firstAnswer.problem.reasonCode)" }) / $workflowAfterFirst / 丢 $dropped 条（提示框出现=$noticeShown）")
    if ($first.Count -ne 1) {
        Add-L2RealNotReached $assertions @('L2-CAL-03', 'L2-CAL-04', 'L2-CAL-05', 'L2-CAL-06', 'L2-CAL-07', 'L2-CAL-08', 'L2-CAL-09', 'L2-CAL-10') '第一次按下没有发出取消请求'
        return
    }

    # --- 3. 操作员再按一次：拿到同一个授权，车不开门、读仓位报结果 ----------------------------------------------------

    $null = Invoke-L2RealConfirmedButton $onboard $journal $button $button
    $second = Wait-L2RealOrLast -Description 'the second cancellation request was answered' -Journal $journal `
        -Criterion 'second-request' -TimeoutSeconds 30 `
        -Probe { $all = Get-Requests $demandId; if ($all.Count -ge 2) { $all[1] } else { $null } } `
        -Until { param($v) $null -ne $v -and $v.Response -ne '' }
    $secondDecision = if ($null -ne $second -and $null -ne $second.ResponsePayload) { [string]$second.ResponsePayload.decision } else { '(none)' }

    # A second refusal is read off the protocol (the server's decision on the second request), not off the HMI.
    $secondFailed = ($secondDecision -ne 'AUTHORIZED')
    $result = if ($secondFailed) { $null } else {
        Wait-L2RealOrLast -Description 'the server received the LoadCancellationResult' -Journal $journal `
            -Criterion 'cancellation-result' -TimeoutSeconds 90 `
            -Probe {
                $received = @((Get-L2RealInbound $connection 'LoadCancellationResult') | Where-Object { [string]$_.Payload.cancellationId -eq $cancellationId })
                if ($received.Count -ge 1) { $received[0] } else { $null }
            } `
            -Until { param($v) $null -ne $v }
    }
    if ($secondFailed) { $null = Confirm-L2RealNotice $onboard $journal $failure 5 }
    $assertions.Add(
        'L2-CAL-03', '授权应答丢了之后再按一次，车载端拿到授权并把取消做完、报回 LoadCancellationResult（没有再报失败）',
        (-not $secondFailed -and $null -ne $result), 'LoadCancellationResult',
        $(if ($secondFailed) { "第二次请求的决定是 $secondDecision" } elseif ($null -eq $result) { '90 s 内没有结果' } else { "LoadCancellationResult $($result.Payload.overallOutcome) → $($result.Response)" }))

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
    $outcome = if ($null -ne $result) { [string]$result.Payload.overallOutcome } else { '(no result)' }
    $assertions.Add(
        'L2-CAL-04', '取消收敛：ALL_EMPTY，工作流 Reconciled，需求与装货 Cancelled，旅程以 CANCELLED_BY_OPERATOR 收尾',
        ($outcome -eq 'ALL_EMPTY' -and $settled -eq 'Reconciled / Cancelled / Cancelled / Completed/CANCELLED_BY_OPERATOR'),
        'ALL_EMPTY / Reconciled / Cancelled / Cancelled / Completed/CANCELLED_BY_OPERATOR', "$outcome / $settled")

    # --- 5. 两次按下是两条报文，内容相同；服务端一次都没掐连接 --------------------------------------------------------

    $requests = Get-Requests $demandId
    $messageIds = @($requests | ForEach-Object { $_.MessageId })
    $payloads = @($requests | ForEach-Object { $_.PayloadJson })
    $decisions = @($requests | ForEach-Object { if ($null -ne $_.ResponsePayload) { [string]$_.ResponsePayload.decision } else { '(none)' } })
    $distinctIds = @($messageIds | Sort-Object -Unique).Count
    $samePayload = ($payloads.Count -eq 2 -and $payloads[0] -eq $payloads[1])
    $assertions.Add(
        'L2-CAL-05', '两次按下是两条 messageId 不同的取消请求，payload 相同（同一 cancellationId，沿用首发的操作员与理由），两次都拿到 AUTHORIZED（onboard-hmi#71；第二次照记录答，control-server#837）',
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

    $reading = (@($load.TargetSlots | Sort-Object) | ForEach-Object { "$_=$(Get-L2RealSlotReading $simulator $_)" }) -join ' '
    $expectedReading = (@($load.TargetSlots | Sort-Object) | ForEach-Object { "$_=CLOSED/EMPTY/1/0" }) -join ' '
    $assertions.Add(
        'L2-CAL-07', '现场收在安全状态：两仓都门关、仓空、已锁、开锁输出复位',
        ($reading -eq $expectedReading), $expectedReading, $reading)

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

    # --- 7. 取消完成也把车还回去 -----------------------------------------------------------------------------------

    # 「取消完成」是这一站的终结（ADR-cross-0046），终结要把车辆占用还回去，否则这台车此后一单也派不出（受理被
    # 用途占有的主键拒掉；control-server#387 之前是旅程 Blocked / VEHICLE_OCCUPANCY_CONFLICT）。读的是被释放的那个事实本身，
    # 不靠再派一单：本场景不驱动第二个需求。control-server#387 起车辆占用只剩用途占有，读它的记录上的释放时刻。
    # 扫码前取消走 PickupStopTermination，同一次提交里释放；在途取消由 OnboardRecoveryCoordinator 手写终结，写不写这一格
    # 就是 control-server#131。写入不一定与工作流 Reconciled 同一次提交，所以等，不直读。
    $releasedAt = Wait-L2RealOrLast -Description 'the vehicle occupancy of the cancelled pickup was released' -Journal $journal `
        -Criterion 'vehicle-occupancy-released' -TimeoutSeconds 30 `
        -Probe { Get-L2RealScalar $connection "SELECT r.ReleasedAt AS Value FROM VehiclePurposeClaimRecords AS r JOIN JourneyDemands AS d ON d.JourneyId = r.JourneyId WHERE d.DemandId = '$demandId' ORDER BY r.AcquiredAt DESC LIMIT 1" } `
        -Until { param($v) $null -ne $v }
    $assertions.Add(
        'L2-CAL-09', '取消完成也把车还回去：这一单所在旅程的用途占有记录已释放（ReleasedAt 有值），同一台车能再派单',
        ($null -ne $releasedAt), '用途占有记录 ReleasedAt 有值',
        $(if ($null -ne $releasedAt) { "ReleasedAt $releasedAt" } else { '用途占有记录 ReleasedAt 为空（30 s 内）' }))

    # --- 8. 取消全程不开门 ------------------------------------------------------------------------------------------

    $changes = Stop-DoorSampler $sampler
    $samplerStopped = $true
    $samplerErrors = @($changes | Where-Object { $_.PSObject.Properties['Error'] })
    $transitions = @($changes | Where-Object { $_.PSObject.Properties['Slots'] })
    foreach ($change in $transitions) { $journal.Note("Not locked at $($change.At): [$(@($change.Slots) -join ',')]") }
    $everOpen = @($transitions | Where-Object { @($_.Slots).Count -gt 0 })
    $sequence = @($transitions | ForEach-Object { "[$(@($_.Slots) -join ',')]" }) -join ' → '
    # Guard against a green that sampled nothing: the sampler always emits its first sample, so a run that sampled at all
    # has at least one record.
    $assertions.Add(
        'L2-CAL-10', "取消全程不开门（协议 6.0.0，NEVER_OPEN_A_SLOT_FOR_A_CANCELLATION）：从第 $openedSlot 仓空门锁好、按取消之前起到取消收尾，模拟器每 100 ms 的采样里任一时刻都没有未锁闭的仓",
        ($transitions.Count -ge 1 -and $everOpen.Count -eq 0 -and $samplerErrors.Count -eq 0),
        '采样 ≥1 / 未锁闭 0 次 / 采样错误 0 次',
        "采样记录 $($transitions.Count) / 未锁闭 $($everOpen.Count) 次$(if ($everOpen) { "（$(@($everOpen | ForEach-Object { "$($_.At) [$(@($_.Slots) -join ',')]" }) -join '; ')）" }) / 采样错误 $($samplerErrors.Count) 次 / $sequence")

    $journal.Note('Scenario finished.')
} finally {
    if (-not $samplerStopped) { $null = Stop-DoorSampler $sampler }
}
