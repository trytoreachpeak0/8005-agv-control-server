#Requires -Version 7

<#
车载端时钟偏差：方案第 4 节标 ★ 的三条里的第三条，也是拖得最久的那条。

在真车载端接进来之前，这一条**做不了**——缺陷在车载端的 `VehicleSafetySignal.IsFresh`
（[`8005-agv-onboard-hmi#1`](https://github.com/trytoreachpeak0/8005-agv-onboard-hmi/issues/1)），
合成对端里根本没有那段逻辑。真车载端接进来之后仍然差一件事：两端跑在同一台机器上共用一个时钟，
而 `observedAt` 是 ControlServer 用自己的 `timeProvider` 盖的章，偏差不会自己出现。
`tools/ControlServer.ClockSkewProxy` 补的就是这一件。

`#1` 已由 Kun Wang 在 `abb8e73` 修复为**有界容差**（`vehicleSafety.clockSkewToleranceMs`，默认
500 ms、上限 1000 ms），所以这条场景不再是「复现缺陷」，而是**钉住那个界**——两侧都要对：

1. **容差内（100 ms）**：会话照常建立。修复之前，任何正偏差都会让 `IsFresh` 判 false，
   `WaitForFirstRefreshAsync` 发布 UNKNOWN，会话永远进不了 Ready。
2. **容差外（3000 ms）**：仍然 fail-closed。有界容差不能变成「无限容忍」——车载端必须拒绝一份
   它没有理由相信的证据。实测先翻的是会话本身：`RecoveryRequired / DEPARTURE_SAFETY_NOT_READY`，
   偏差生效后 0.3 秒内；需求随之判 `ONBOARD_FACTS_NOT_READY`。
3. **回到容差内**：自动恢复，不用重启车载端。这是 `abb8e73` 明确承诺的行为。

被测的是车载端出厂的真代码；喂给它的输入与真的慢 N 毫秒的时钟喂给它的一模一样。**但代理只偏这
一处比较**，真的慢时钟会同时挪动车载端自己盖的每一个时间戳——证据里不能说得比这更多。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$skewProxy = $Context.SkewProxy
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')

# 车载端默认容差 500 ms；3000 ms 毫无争议地在界外，而且远大于 maximumEvidenceAgeMs 能解释的范围。
$beyondToleranceMs = 3000
$withinToleranceMs = 100

function Get-BacklogReason {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].ReasonCode
}

function Get-Stage {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

# 会话就绪度和它的原因码一起读：偏差生效时先翻的是这一行，需求被拒是它的后果。
function Get-Session {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'"
    if ($rows.Count -eq 0) { return $null }
    return "$([string]$rows[0].Readiness)/$([string]$rows[0].ReasonCode)"
}

# --- 1. 容差内：偏差确实施加了，而且会话照常建立 --------------------------------------------------

# 先证明车载端真的走了代理。少了这一条，后面「没就绪」既可能是车载端 fail-closed，也可能只是代理
# 自己写坏了——两者在断言上长得一模一样。
$forward = $skewProxy.Snapshot().body.forward
$assertions.Add(
    'L2-CS-01', '车载端的安全投影确实经过了偏差代理，且 observedAt 被推后',
    ([long]$forward.forwardedRequests -ge 1 -and
        $null -ne $forward.lastUpstreamObservedAt -and
        ([datetimeoffset]$forward.lastForwardedObservedAt - [datetimeoffset]$forward.lastUpstreamObservedAt).TotalMilliseconds -eq $withinToleranceMs),
    "forwardedRequests >= 1，位移 = $withinToleranceMs ms",
    "forwardedRequests = $($forward.forwardedRequests)，位移 = $(
        if ($forward.lastUpstreamObservedAt) {
            ([datetimeoffset]$forward.lastForwardedObservedAt - [datetimeoffset]$forward.lastUpstreamObservedAt).TotalMilliseconds
        } else { '(没有转发过)' }) ms")

# 环境起得来这件事本身就是判据：Invoke-L2Scenario 在进入场景之前等过 /health/ready，而那要求
# 有对端完成恢复握手并被服务端授予就绪。这里从服务端自己的表里再确认一次。
$session = Get-Session
$assertions.Add(
    'L2-CS-02', "车载端时钟慢 $withinToleranceMs ms（容差内）时会话正常建立",
    ($session -like 'Ready/*'),
    'Ready/…', $session)

# --- 2. 推到容差外：必须仍然 fail-closed ----------------------------------------------------------

$journal.Note("Pushing observedAt skew to ${beyondToleranceMs}ms, beyond the onboard's tolerance.")
$null = $skewProxy.Command('Put', 'skew', @{ skewMs = $beyondToleranceMs })

# 先翻的是会话本身，不是需求。车载端读到一份它不敢相信的证据 → departureSafe=false → 服务端把
# 会话降级为 RecoveryRequired/DEPARTURE_SAFETY_NOT_READY，实测在偏差生效后 0.6 秒内。
$session = Wait-L2Condition -Description 'the session degraded because departure safety can no longer be vouched for' `
    -Journal $journal -Criterion 'session-readiness' -TimeoutSeconds 120 `
    -Probe { Get-Session } -Until { param($v) $v -eq 'RecoveryRequired/DEPARTURE_SAFETY_NOT_READY' }
$assertions.Add(
    'L2-CS-03', "偏差 $beyondToleranceMs ms 超出容差时车载端仍然 fail-closed，会话降级",
    ($session -eq 'RecoveryRequired/DEPARTURE_SAFETY_NOT_READY'),
    'RecoveryRequired/DEPARTURE_SAFETY_NOT_READY', $session)

# **需求必须等降级真的落库之后再发布。**设完偏差就投是个竞态：车载端要下一次 1 秒轮询才发现证据
# 过期，而运行时可能在那个窗口里用「仍然 Ready」的会话把需求受理掉——受理之后 backlog 不再重评，
# 判据就只能等到超时。红证据留在 evidence/l2/20260903-real-onboard-clock-skew-004。
$journal.Note("Publishing demand $demandIdWire now that the session has degraded.")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = "L2-SUBLOT-$($Context.RunId)"
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

# 需求被拒是上面那件事的后果。原因码是 ONBOARD_FACTS_NOT_READY 而不是 ONBOARD_DEPARTURE_UNSAFE：
# 会话已经不就绪，ReadOnboardFactsAsync 压根走不到读 departureSafe 那一步。第一版按后者写，白等
# 了 120 秒——它是「会话还在 Ready、但车载端说不能走」时才会出现的原因码。
$reason = Wait-L2Condition -Description 'the demand was refused because the onboard facts are not readable' `
    -Journal $journal -Criterion 'backlog-reason' -TimeoutSeconds 120 `
    -Probe { Get-BacklogReason } -Until { param($v) $v -eq 'ONBOARD_FACTS_NOT_READY' }
$assertions.Add(
    'L2-CS-04', '需求随之被拒，判 ONBOARD_FACTS_NOT_READY',
    ($reason -eq 'ONBOARD_FACTS_NOT_READY'),
    'ONBOARD_FACTS_NOT_READY', $reason)

# 有界容差不能变成「反正会过去」：再给运行时几轮机会，它仍然不许这台车动。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$stage = Get-Stage
$assertions.Add(
    'L2-CS-05', '运行时又转了几轮，仍然没有为这条需求建 journey',
    ($null -eq $stage),
    '(没有 journey)', $(if ($null -eq $stage) { '(没有 journey)' } else { $stage }))

# --- 3. 回到容差内：自动恢复，不重启车载端 --------------------------------------------------------

$journal.Note("Bringing the skew back to ${withinToleranceMs}ms; the onboard must recover on its own.")
$null = $skewProxy.Command('Put', 'skew', @{ skewMs = $withinToleranceMs })

$reason = Wait-L2Condition -Description 'the same demand was accepted once the evidence came back inside tolerance' `
    -Journal $journal -Criterion 'backlog-reason' -TimeoutSeconds 120 `
    -Probe { Get-BacklogReason } -Until { param($v) $v -eq 'ACCEPTED' }
$assertions.Add(
    'L2-CS-06', '偏差回到容差内后，同一条需求被受理——车载端自行恢复，没有重启',
    ($reason -eq 'ACCEPTED'),
    'ACCEPTED', $reason)

$stage = Wait-L2Condition -Description 'the journey was created and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$assertions.Add(
    'L2-CS-07', '恢复之后旅程真的建起来并派车',
    ($stage -eq 'AwaitingPickupArrival'),
    'AwaitingPickupArrival', $stage)

$session = Get-Session
$assertions.Add(
    'L2-CS-08', '会话从 RecoveryRequired 自己回到 Ready，全程没有重启车载端',
    ($session -like 'Ready/*'),
    'Ready/…', $session)

$journal.Note('Scenario finished.')
