#Requires -Version 7

<#
仓位配置激活的「下发 → 断线 → 重连 → 补报」：FP-IS-14，批次 3 出口，#15 的 L2 验收标准。

REQ-0264 要消息 7／8 走 RELIABLE 而不是 REQUEST/RESPONSE，理由就是这条场景造出来的那个窗口：车已经把配置
换好了，结果还没送出去线就断了。用 RESPONSE，这一次激活的结论随连接一起丢掉，服务端除了猜没有别的可做；
用 RELIABLE，服务端在断线期间什么都不信，重连之后按 SLOT_CONFIGURATION 这个恢复角色重发同一条命令，车认出
这个 activationId 已经有结论，原样补报。

合成车载端能证的是服务端这一半：不猜、补发的是同一行、只收敛一次、生效配置只写一次。两端算出同一个指纹
不在这里证——合成车载端没有 IO，它采纳激活的目标指纹；那一条是 G3 对真车载端的断言
（evidence/g3/20260910-fp-is-14-15-staged-6dc4bc8）。

最后顺带取一次 REQ-0271 的导出：这次激活留下的业务审计，经受控运维工具导出成 CSV，能读回来。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$onboard = $Context.Onboard
$riot = $Context.Riot
$agvId = $Context.AgvId

function Get-Activation([string]$activationId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT * FROM SlotConfigurationActivations WHERE ActivationId = '$activationId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Count([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    return [int]$rows[0].N
}

# --- 1. 治理前置：现场 W1 窗口里人做的两件事 -----------------------------------------------------------
# 激活入口拒收一个没有发布、IO 绑定不齐的目标，而一个刚起来的库什么都没有。这两条命令走的是与现场同一个
# 可执行文件，写的是服务端正在用的同一个 SQLite 文件。

$seed = & $Context.InvokeFieldOps -Arguments @('seed-approved-facts')
$slotModelVersionId = [string]$seed.slotModelVersionId
$bind = & $Context.InvokeFieldOps -Arguments @('bind-io', '--agv', $agvId)
$assertions.Add(
    'L2-SCA-01', '已批准的八仓事实入库，这台车八个仓位的 IO 都绑上了',
    ($seed.outcome -eq 'OK' -and -not [string]::IsNullOrWhiteSpace($slotModelVersionId) -and [int]$bind.boundSlots -eq 8),
    'OK / 8', "$($seed.outcome) / $($bind.boundSlots)")

# --- 2. 下发，车收到之后先挂着 --------------------------------------------------------------------------

$null = $onboard.Command('Put', 'policy', @{ slotConfigurationActivation = 'Manual' })
$generationBefore = [long]$onboard.Snapshot().body.sessionGeneration

$issuedAt = [DateTimeOffset]::UtcNow
$issueStatus = 0
$issue = Invoke-RestMethod -Method Post `
    -Uri "http://127.0.0.1:$($Context.HealthPort)/api/governance/v1/slot-configuration-activations" `
    -Headers @{ Authorization = "Bearer $($Context.GovernanceCredential)" } `
    -ContentType 'application/json' `
    -Body (@{
        agvId              = $agvId
        slotModelVersionId = $slotModelVersionId
        administrator      = @{
            operatorId         = 'op-l2'
            verificationMethod = 'BADGE'
            verifiedAt         = $issuedAt.ToString('O')
        }
    } | ConvertTo-Json -Depth 5) `
    -StatusCodeVariable issueStatus -TimeoutSec 20
$activationId = [string]$issue.activationId
$journal.Note("Activation $activationId issued: $($issue.state), command $($issue.commandMessageId).")

# 202 不是 200：命令上线之后车还没报结果，200 会让调用方以为配置已经换好了。
$assertions.Add(
    'L2-SCA-02', '下发回 202，激活处在待补报态，恢复角色是 SLOT_CONFIGURATION',
    ($issueStatus -eq 202 -and $issue.state -eq 'PENDING_RESULT' -and $issue.recoveryRole -eq 'SLOT_CONFIGURATION'),
    '202 / PENDING_RESULT / SLOT_CONFIGURATION',
    "$issueStatus / $($issue.state) / $($issue.recoveryRole)")

$key = "activation:$activationId"
$null = Wait-L2Condition -Description 'the activation command reached the vehicle and is held open' `
    -Journal $journal -Criterion 'activation-command-held' -TimeoutSeconds 30 `
    -Probe { @($onboard.Snapshot().body.pending | Where-Object { $_.key -eq $key }).Count } `
    -Until { param($v) $v -eq 1 }
$heldIds = @($onboard.Snapshot().body.activationCommandMessageIds)
$assertions.Add(
    'L2-SCA-03', '车收到的就是服务端落库的那条命令',
    ($heldIds.Count -eq 1 -and $heldIds[0] -eq $issue.commandMessageId),
    [string]$issue.commandMessageId, ($heldIds -join ','))

# --- 3. 车换好了配置，结果还没送出去线就断了 ------------------------------------------------------------

$null = $onboard.Command('Put', "answer/$key", @{ completed = $true; deliver = $false })
$null = $onboard.Command('Put', 'connection', @{ connected = $false })
$journal.Note('Vehicle concluded the activation locally and dropped the link before reporting it.')

# 否定判据要有「服务端又有机会了」的计数，而不是 sleep：等引擎再转三轮，再看服务端有没有自己下结论。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$whileDown = Get-Activation $activationId
$activeWhileDown = Get-Count "SELECT COUNT(*) AS N FROM ActiveSlotConfigurations WHERE AgvId = '$agvId'"
$assertions.Add(
    'L2-SCA-04', '断线期间服务端不猜：激活仍待补报，生效配置一行都没写',
    ($null -ne $whileDown -and [string]$whileDown.State -eq 'PENDING_RESULT' -and $activeWhileDown -eq 0),
    'PENDING_RESULT / 0',
    "$(if ($whileDown) { $whileDown.State } else { '(缺行)' }) / $activeWhileDown")

$peerWhileDown = $onboard.Snapshot().body
$assertions.Add(
    'L2-SCA-05', '车上已经有结论，但一份结果都没送出去',
    (@($peerWhileDown.activationOutcomes).Count -eq 1 -and [int]$peerWhileDown.activationResultsSent -eq 0),
    '1 outcome / 0 sent',
    "$(@($peerWhileDown.activationOutcomes).Count) outcome / $($peerWhileDown.activationResultsSent) sent")

# --- 4. 重连：服务端按 SLOT_CONFIGURATION 重发同一条命令，车认出已有结论，补报 ---------------------------

$reconnect = $onboard.Command('Put', 'connection', @{ connected = $true })
$generationAfter = [long]$reconnect.body.sessionGeneration
$assertions.Add(
    'L2-SCA-06', '重连走完完整握手，会话代前进',
    ($reconnect.body.readiness -eq 'READY' -and $generationAfter -gt $generationBefore),
    "READY / > $generationBefore", "$($reconnect.body.readiness) / $generationAfter")

$converged = Wait-L2Condition -Description 'the replayed result converged the activation' `
    -Journal $journal -Criterion 'activation-activated' -TimeoutSeconds 60 `
    -Probe { $row = Get-Activation $activationId; if ($row) { [string]$row.State } else { $null } } `
    -Until { param($v) $v -eq 'ACTIVATED' }
$assertions.Add('L2-SCA-07', '补报到达之后激活收敛为 ACTIVATED', ($converged -eq 'ACTIVATED'), 'ACTIVATED', $converged)

# 先等假对端自己记下「这次结果发出去了」，再判它只发过一次（control-server#204）。
# 服务端库里变成 ACTIVATED 与假对端把 activationResultsSent 加一，是两件先后发生的事：假对端在
# `await SendLineAsync(...)` 返回之后才加计数（OnboardPeerSession.SendActivationResultAsync），而服务端
# 那边收到、处理、落库可以先跑完。直接读会读到 0，判据假红——等的是前一个事实，断言的是后一个。
$sent = Wait-L2ConditionOrLast -Description 'the synthetic peer recorded the activation result it sent' `
    -Journal $journal -Criterion 'activation-results-sent' -TimeoutSeconds 30 `
    -Probe { [int]($onboard.Snapshot().body.activationResultsSent) } `
    -Until { param($v) $v -ge 1 }
# 再多转几轮：判据说的是「只报了一次」，所以光等到 1 不够，还要给第二次出现的机会。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$journal.Note("Peer reported activationResultsSent = $sent on first sight; re-reading after three more runtime rounds.")

$peer = $onboard.Snapshot().body
$commandIds = @($peer.activationCommandMessageIds)
# 补发的是落库那一行，不是重新决定一次：messageId 从头到尾只有一个。
$assertions.Add(
    'L2-SCA-08', '重连后补发的是同一条命令（同一个 messageId），不是新的一次下发',
    ($commandIds.Count -ge 2 -and @($commandIds | Sort-Object -Unique).Count -eq 1 -and $commandIds[0] -eq $issue.commandMessageId),
    ">=2 receipts of $($issue.commandMessageId)", ($commandIds -join ','))
$assertions.Add(
    'L2-SCA-09', '车只报了一次结果（等到它记下发过一次、再多转三轮之后仍然是一次）',
    ([int]$peer.activationResultsSent -eq 1), 1, [int]$peer.activationResultsSent)

$activationRows = Get-Count "SELECT COUNT(*) AS N FROM SlotConfigurationActivations WHERE AgvId = '$agvId'"
$assertions.Add('L2-SCA-10', '一次下发只有一行激活，补报没有产生第二次激活', ($activationRows -eq 1), 1, $activationRows)

# 不要写成 @(Invoke-L2Query ...)：它以 `return , $rows` 返回，外面再包一层 @() 得到的是「一个元素、那个元素
# 是整张结果集」，`.Count` 恒为 1，「写了一行」这半句就没在判。
$active = (Invoke-L2Query -Connection $connection `
    -Sql "SELECT ActivationId, Fingerprint, ConfigurationVersion FROM ActiveSlotConfigurations WHERE AgvId = '$agvId'")
$assertions.Add(
    'L2-SCA-11', '生效配置写了一行，就是这次激活、就是它的目标指纹',
    ($active.Count -eq 1 -and [string]$active[0].ActivationId -eq $activationId -and [string]$active[0].Fingerprint -eq [string]$issue.fingerprint),
    "1 / $activationId / $($issue.fingerprint)",
    $(if ($active.Count -eq 0) { '(无行)' } else { "$($active.Count) / $($active[0].ActivationId) / $($active[0].Fingerprint)" }))

$session = (Invoke-L2Query -Connection $connection `
    -Sql "SELECT Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$agvId'")
$assertions.Add(
    'L2-SCA-12', '车重连后报的正是生效那一版，会话就绪，没有被判成指纹不符',
    ($session.Count -eq 1 -and [string]$session[0].Readiness -eq 'Ready' -and [string]$session[0].ReasonCode -eq 'READY'),
    'Ready / READY',
    $(if ($session.Count -eq 0) { '(无行)' } else { "$($session[0].Readiness) / $($session[0].ReasonCode)" }))

# --- 5. REQ-0271：这次激活留下的业务审计可导出 --------------------------------------------------------

$exportPath = Join-Path $Context.SnapshotRoot 'audit-business-export.csv'
$export = & $Context.InvokeFieldOps -Arguments @(
    'export-audit', '--stream', 'business', '--format', 'csv', '--output', $exportPath)
$bytes = [IO.File]::ReadAllBytes($exportPath)
$csv = [Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3)
$hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
$assertions.Add(
    'L2-SCA-13', '业务审计经 FieldOps export-audit 导出为带 BOM 的 CSV，含这次激活的下发与结果两条',
    ($export.outcome -eq 'OK' -and $hasBom -and
        $csv.Contains('SLOT_CONFIGURATION_ACTIVATION_ISSUED', [StringComparison]::Ordinal) -and
        $csv.Contains('SLOT_CONFIGURATION_ACTIVATION_RESULT_RECORDED', [StringComparison]::Ordinal)),
    'OK / BOM / ISSUED + RESULT_RECORDED',
    "$($export.outcome) / BOM=$hasBom / count=$($export.count)")

$journal.Note('下发 → 断线 → 重连 → 补报走通；服务端断线期间不猜，补发同一行，只收敛一次。')
