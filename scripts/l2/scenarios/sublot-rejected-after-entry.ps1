#Requires -Version 7

<#
录入后按派车范围找需求并按 BR-013 重算，不符时回 SublotRejected（批次 5，control-server#82）。

票面第 7 项。四段：

  A. 派车之后把假 MES 的箱数从 4 改成 8（容量对照 L2-PACKAGE 是 4 箱/篮），操作员录入本范围的子批号：
     服务端重算得 2，与受理时冻结的 1 不符 → 收到 SublotRejected，reasonCode 是
     EXPECTED_BASKET_COUNT_MISMATCH，correlationId 等于那条提交的 messageId，没有装货命令、没有仓位操作、
     TargetSlotsJson 一点没动。
  B. 录入一个派车范围外的子批号 → SUBLOT_NOT_IN_DISPATCH_SCOPE、demandId 为空。
  C. 箱数改回 4，操作员重扫本范围的子批号（新的 messageId）→ 正常发装货命令。
  D. 这一段里本站一直没人成功录入：旅程的阻断原因始终为空、阶段始终是 AwaitingSublot、被拒的那条提交
     不会在下一轮再冒出一条拒收。

**「别车的历史提交不刷误导码」这一格在这个装置上做不到**：只有一台假车，另一台车的提交发不出来。L1 的
SubmissionsThatDoNotAnswerTheEntryRequestOpenNowAreSkippedWithoutABlockReason 覆盖了它（别车、别的操作
会话、别的 worklistRevision 三条），这里用「被拒过的提交留在库里、之后每一轮都还会被读到，而旅程的阻断
原因仍为空」作为这个装置上能观察到的等价形式。

站点离站期限拉到十分钟（见 setup.psd1），远超本场景时长：本站必须靠录入成功走出去，而不是期限到了——
两条出口不是同一段代码，但默认值下先把「什么时候结束」混进来就分不清了。

判据读的是服务端自己的库：录入在 ProtocolInbox、拒收在 ProtocolOutbox（PayloadJson 里带着 correlationId
与原因码）。合成对端的 wire 只记 direction/messageType/messageId，没有载荷，所以「车真的收到了」由 wire
里那条 in 的 SublotRejected 证明、内容由服务端那条出站记录给出，两者用 messageId 对齐。

对端入口与限制见 tools/ControlServer.FakeOnboard/README.md「操作员扫码」。
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
$onboard = $Context.Onboard
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SRJ-$($Context.RunId)"
$outsideSublot = "L2-SRJ-OUTSIDE-$($Context.RunId)"

# Invoke-L2Query 已经把 DBNull 换成了 $null，所以这里只判 $null。
function Test-L2Null($value) {
    return $null -eq $value
}

function ConvertTo-Instant($value) {
    if (Test-L2Null $value) { return $null }
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

function Get-Runtime {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stage {
    $runtime = Get-Runtime
    if ($null -eq $runtime) { return $null }
    # ControlServerDbContext 把这些枚举都按 HasConversion<string> 存成员名，按序数读会抛。
    return [string]$runtime.Stage
}

function Get-Intent([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Count([string]$sql) {
    $rows = Invoke-L2Query -Connection $connection -Sql $sql
    return [int]$rows[0].Total
}

# 服务端收到过的录入提交，按收到时间排序。载荷在 RequestJson 里，wire 不带载荷。
function Get-Submissions {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT MessageId, ReceivedAt, RequestJson FROM ProtocolInbox WHERE MessageType = 'SublotSubmitted' ORDER BY ReceivedAt"
    return @($rows | ForEach-Object {
        $request = ConvertFrom-Json -AsHashtable $_.RequestJson
        [pscustomobject]@{
            MessageId = [string]$_.MessageId
            ReceivedAt = ConvertTo-Instant $_.ReceivedAt
            Sublot = [string]$request.payload.sublot
        }
    })
}

# 服务端发出去过的拒收，按出的时间排序，载荷解出来。
function Get-Rejections {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT MessageId, PayloadJson FROM ProtocolOutbox WHERE MessageType = 'SublotRejected'"
    return @($rows | ForEach-Object {
        $envelope = ConvertFrom-Json -AsHashtable $_.PayloadJson
        [pscustomobject]@{
            MessageId = [string]$_.MessageId
            CorrelationId = [string]$envelope.correlationId
            AgvId = [string]$envelope.agvId
            DemandId = $envelope.payload.demandId
            Problem = [string]$envelope.payload.problem.reasonCode
            WorklistRevision = $envelope.payload.currentWorklistRevision
            RejectedSublot = [string]$envelope.payload.rejectedSublot
        }
    })
}

# 车从某一刻起收到过的某类报文（合成对端的线上记录，in = 服务端发给车的）。
function Get-PeerInbound([string]$messageType, [DateTimeOffset]$since) {
    return @(@($onboard.Snapshot().body.wire) | Where-Object {
            [string]$_.direction -eq 'in' -and [string]$_.messageType -eq $messageType -and
            (ConvertTo-Instant $_.at) -ge $since
        })
}

# 车发出去过的录入提交的 messageId，去重。服务端每轮重放未结的录入请求，缓存里的答案会原样再发一遍，
# 所以同一份提交在 wire 里会出现很多次。
function Get-PeerSubmissionIds([DateTimeOffset]$since) {
    return @(@($onboard.Snapshot().body.wire) |
            Where-Object {
                [string]$_.direction -eq 'out' -and [string]$_.messageType -eq 'SublotSubmitted' -and
                (ConvertTo-Instant $_.at) -ge $since
            } |
            ForEach-Object { [string]$_.messageId } | Select-Object -Unique)
}

# 挂起中的录入请求的 key。Manual 策略下它一直挂着，key 从 /snapshot 的 pending 里读。
function Get-PendingEntryKey {
    $pending = @($onboard.Snapshot().body.pending |
        Where-Object { $_.messageType -eq 'SublotEntryRequested' })
    if ($pending.Count -eq 0) { return $null }
    return [string]$pending[0].key
}

function Publish-Demand([int]$maxBoxCount) {
    $null = $mes.Command('Put', "demands/$demandIdWire", @{
        sublot      = $sublot
        area        = 'N1-3'
        eqp         = 'EQP-L2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = $maxBoxCount
    })
}

# 车跑完一条已确认的取货单：接单、行驶、停在取货点。
function Invoke-PickupLeg([object]$intent) {
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{
        orderState        = 3
        executeVehicleKey = $Context.VehicleKey
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey      = $Context.VehicleKey
        procState       = 'RUNNING'
        movementState   = 'MT_RUNNING'
        speed           = 0.8
        processingOrder = $true
        orderTaskId     = $intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey       = $Context.VehicleKey
        procState        = 'IDLE'
        movementState    = 'MT_FINISHED'
        speed            = 0
        currentPosition  = $Context.PickupStationRiotId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })
}

# --- 1. 发需求，开到取货站，停在等录入这一步 ------------------------------------------------------

# 录入这一路先设成 Manual：本站的第一条提交必须由场景决定什么时候发，否则对端会在到站那一轮立刻用
# 请求给的子批号应答，后面的箱数改动就赶不上了。
$journal.Note('The synthetic peer holds entry requests open until the scenario answers them.')
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Manual' })

$journal.Note("Publishing demand $demandIdWire (sublot $sublot, 4 boxes, capacity 4 per basket).")
Publish-Demand 4
$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-Intent 'TO_PICKUP'; if ($row -and [string]$row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }
$dispatched = Get-Runtime
$assertions.Add(
    'L2-SRJ-01', '受理时冻结的花篮数量是 1（4 箱 ÷ 每篮 4 箱），仓位也已按它预留',
    ([int]$dispatched.ExpectedBasketCount -eq 1 -and -not [string]::IsNullOrEmpty([string]$dispatched.TargetSlotsJson)),
    'ExpectedBasketCount=1 / TargetSlotsJson 非空',
    "ExpectedBasketCount=$($dispatched.ExpectedBasketCount) / TargetSlotsJson=$($dispatched.TargetSlotsJson)")

Invoke-PickupLeg $pickupIntent
$waiting = Wait-L2Condition -Description 'the stop is waiting for a sublot' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Runtime } -Until { param($v) $v -and [string]$v.Stage -eq 'AwaitingSublot' }
$pendingKey = Wait-L2Condition -Description 'the peer is holding the entry request open' `
    -Journal $journal -Criterion 'entry-request-pending' -TimeoutSeconds 30 `
    -Probe { Get-PendingEntryKey } -Until { param($v) -not [string]::IsNullOrEmpty($v) }
$entryRequestSeen = Wait-L2Condition -Description 'the peer received the entry request' `
    -Journal $journal -Criterion 'entry-request-received' -TimeoutSeconds 30 `
    -Probe { @(Get-PeerInbound 'SublotEntryRequested' ([DateTimeOffset]::MinValue) |
            Where-Object { [string]$_.messageId -eq [string]$waiting.SublotRequestMessageId }).Count } `
    -Until { param($v) $v -ge 1 }
$request = Invoke-L2Query -Connection $connection `
    -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageId = '$($waiting.SublotRequestMessageId)'"
$expectedSublots = @((ConvertFrom-Json -AsHashtable $request[0].PayloadJson).payload.expectedSublots)
$assertions.Add(
    'L2-SRJ-02', '到站停在等录入，录入请求给的是本次派车范围的子批号、没有需求号',
    ($entryRequestSeen -ge 1 -and $expectedSublots.Count -eq 1 -and $expectedSublots[0] -eq $sublot),
    "车收到 >= 1 次 / [$sublot]",
    "车收到 $entryRequestSeen 次 / [$($expectedSublots -join ', ')]")

# --- 2. 派车之后箱数变了：录入本范围的子批号，按 BR-013 重算不符，拒收 ------------------------------

# 容量对照 L2-PACKAGE 是 4 箱/篮，箱数 4 → 1 个篮；改成 8 → 2 个篮，与受理时冻结的 1 不符。这一步就是
# BR-013 要在录入之后重算的理由：派车之后 MES 的箱数会变。
$journal.Note('The MES box count for the sublot rises from 4 to 8 after dispatch.')
$answeredAt = [DateTimeOffset]::UtcNow
$refused = Wait-L2Change -Description 'the peer received a SublotRejected for the entry' `
    -Journal $journal -Criterion 'entry-refused' -TimeoutSeconds 60 `
    -Baseline { @(Get-Rejections).Count } `
    -Action {
        Publish-Demand 8
        $null = $onboard.Command('Put', "answer/$pendingKey", @{ completed = $true })
    } `
    -Probe { @(Get-Rejections) } `
    -Until { param($before, $now) $before -eq 0 -and $now.Count -eq 1 }
$rejection = @($refused.Value)[0]
$firstSubmission = @(Get-Submissions)[0]
$refusalSeen = @(Get-PeerInbound 'SublotRejected' $answeredAt |
    Where-Object { [string]$_.messageId -eq $rejection.MessageId })
$assertions.Add(
    'L2-SRJ-03', '服务端拒收并发出 SublotRejected：原因是 EXPECTED_BASKET_COUNT_MISMATCH，correlationId 是被拒那条提交的 messageId，rejectedSublot 是录入值',
    ($rejection.Problem -eq 'EXPECTED_BASKET_COUNT_MISMATCH' -and $rejection.CorrelationId -eq $firstSubmission.MessageId -and
        $rejection.RejectedSublot -eq $sublot -and $rejection.DemandId -eq $demandId -and $refusalSeen.Count -ge 1),
    "EXPECTED_BASKET_COUNT_MISMATCH / $($firstSubmission.MessageId) / $sublot / 车收到",
    "$($rejection.Problem) / $($rejection.CorrelationId) / $($rejection.RejectedSublot) / demandId=$($rejection.DemandId) / 车收到 $($refusalSeen.Count) 次")
$assertions.Add(
    'L2-SRJ-04', '被拒的那条提交带的是本站地址、带 currentWorklistRevision',
    ($firstSubmission.Sublot -eq $sublot -and $rejection.WorklistRevision -eq [long]$waiting.WorklistRevision -and
        $rejection.AgvId -eq $Context.AgvId),
    "sublot=$sublot / revision=$($waiting.WorklistRevision) / $($Context.AgvId)",
    "sublot=$($firstSubmission.Sublot) / revision=$($rejection.WorklistRevision) / $($rejection.AgvId)")

$refusedRuntime = Get-Runtime
$slotCommands = @(Get-PeerInbound 'SlotOperationCommand' ([DateTimeOffset]::MinValue)).Count
$loadOperations = Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-SRJ-05', '拒收不发装货命令、不建仓位操作、不动已预留的仓位，旅程留在等录入',
    ([string]$refusedRuntime.Stage -eq 'AwaitingSublot' -and $slotCommands -eq 0 -and $loadOperations -eq 0 -and
        [string]$refusedRuntime.TargetSlotsJson -eq [string]$dispatched.TargetSlotsJson -and
        [string]$refusedRuntime.ExpectedBasketCount -eq [string]$dispatched.ExpectedBasketCount),
    "AwaitingSublot / 0 / 0 / $($dispatched.TargetSlotsJson) / $($dispatched.ExpectedBasketCount)",
    "$($refusedRuntime.Stage) / $slotCommands / $loadOperations / $($refusedRuntime.TargetSlotsJson) / $($refusedRuntime.ExpectedBasketCount)")
$assertions.Add(
    'L2-SRJ-06', '拒收不写阻断原因（被拒的提交不是旅程的阻断，它不上看板）',
    (Test-L2Null $refusedRuntime.BlockReasonCode),
    '无阻断原因', "BlockReasonCode=$(if (Test-L2Null $refusedRuntime.BlockReasonCode) { '(null)' } else { $refusedRuntime.BlockReasonCode })")

# --- 3. 范围外的 SUBLOT：拒收且不指名需求 ---------------------------------------------------------

# 范围外的子批号不在请求给的 expectedSublots 里——列在里面就不叫范围外——所以要显式让对端扫一个。
# rescan 丢掉录入请求的缓存答案，于是下一次重放会重新构造一条提交（新的 messageId）。
$journal.Note("Operator scans $outsideSublot, which belongs to no demand this vehicle was sent for.")
$outsideScannedAt = [DateTimeOffset]::UtcNow
$outside = Wait-L2Change -Description 'the peer received a second SublotRejected for the out-of-scope sublot' `
    -Journal $journal -Criterion 'outside-scope-refused' -TimeoutSeconds 60 `
    -Baseline { @(Get-Rejections).Count } `
    -Action {
        $null = $onboard.Command('Put', 'sublot-scan', @{ sublot = $outsideSublot; rescan = $true })
        $null = $onboard.Command('Put', 'policy', @{ sublot = 'Auto' })
    } `
    -Probe { @(Get-Rejections) } `
    -Until { param($before, $now) $now.Count -eq $before + 1 }
$outsideRejection = @($outside.Value)[-1]
$outsideSubmission = @(Get-Submissions | Where-Object { $_.Sublot -eq $outsideSublot })
$assertions.Add(
    'L2-SRJ-07', '范围外的子批号：SUBLOT_NOT_IN_DISPATCH_SCOPE、demandId 为空、rejectedSublot 是录入值',
    ($outsideRejection.Problem -eq 'SUBLOT_NOT_IN_DISPATCH_SCOPE' -and (Test-L2Null $outsideRejection.DemandId) -and
        $outsideRejection.RejectedSublot -eq $outsideSublot -and $outsideSubmission.Count -eq 1 -and
        $outsideRejection.CorrelationId -eq $outsideSubmission[0].MessageId),
    "SUBLOT_NOT_IN_DISPATCH_SCOPE / demandId=null / $outsideSublot / 提交与拒收对得上",
    "$($outsideRejection.Problem) / demandId=$($outsideRejection.DemandId) / $($outsideRejection.RejectedSublot) / correlationId=$($outsideRejection.CorrelationId)")
$assertions.Add(
    'L2-SRJ-08', '范围外的拒收也是新的一条提交（新的 messageId），不是把上一条重发一遍',
    ($outsideSubmission.Count -eq 1 -and $outsideSubmission[0].MessageId -ne $firstSubmission.MessageId),
    "1 条新提交 / 与第一条不同",
    "$($outsideSubmission.Count) 条 / $(if ($outsideSubmission.Count -eq 1) { $outsideSubmission[0].MessageId } else { '(none)' })")
$assertions.Add(
    'L2-SRJ-09', '两次拒收之后仍然没有装货命令、没有仓位操作，旅程仍在等录入',
    ((Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId'") -eq 0 -and
        [string](Get-Runtime).Stage -eq 'AwaitingSublot'),
    '0 / AwaitingSublot',
    "$(Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId'") / $((Get-Runtime).Stage)")

# --- 4. 箱数恢复，操作员重扫本范围的子批号：正常装货 ------------------------------------------------

$journal.Note('The box count is back to 4 and the operator scans the demand''s own sublot again.')
$loadSentAt = [DateTimeOffset]::UtcNow
$loaded = Wait-L2Change -Description 'the peer received a SlotOperationCommand for the rescanned sublot' `
    -Journal $journal -Criterion 'load-commanded' -TimeoutSeconds 60 `
    -Baseline { @(Get-PeerInbound 'SlotOperationCommand' ([DateTimeOffset]::MinValue)).Count } `
    -Action {
        Publish-Demand 4
        $null = $onboard.Command('Put', 'sublot-scan', @{ sublot = $sublot; rescan = $true })
    } `
    -Probe { @(Get-PeerInbound 'SlotOperationCommand' ([DateTimeOffset]::MinValue)) } `
    -Until { param($before, $now) $before -eq 0 -and $now.Count -eq 1 }
$loadCommand = @($loaded.Value)[0]
$acceptedSubmission = @(Get-Submissions | Where-Object { $_.Sublot -eq $sublot } | Select-Object -Last 1)
$commandRow = Invoke-L2Query -Connection $connection `
    -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageId = '$($loadCommand.messageId)'"
$commandPayload = (ConvertFrom-Json -AsHashtable $commandRow[0].PayloadJson).payload
# 装货命令的载荷里没有 sublot——报文的 schema 只让它带 demandId、attempt 与仓位集合。这条尝试对应的子批号
# 记在 StationOperations.SublotId 上，所以从那里读。
$attempts = @(Invoke-L2Query -Connection $connection `
    -Sql "SELECT SublotId, TargetSlotsJson FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'")
$assertions.Add(
    'L2-SRJ-10', '重扫之后发的是装货命令：给的是这条需求与重扫那条子批号，仓位数是受理时冻结的值',
    ($commandPayload.demandId -eq $demandId -and $attempts.Count -eq 1 -and [string]$attempts[0].SublotId -eq $sublot -and
        [int]$commandPayload.expectedBasketCount -eq [int]$dispatched.ExpectedBasketCount),
    "$demandId / $sublot / $($dispatched.ExpectedBasketCount)",
    "$($commandPayload.demandId) / $(if ($attempts.Count -eq 1) { $attempts[0].SublotId } else { '(no attempt)' }) / $($commandPayload.expectedBasketCount)")

$consumed = Wait-L2Condition -Description 'the journey left the entry wait on the rescanned submission' `
    -Journal $journal -Criterion 'entry-accepted' -TimeoutSeconds 60 `
    -Probe { Get-Runtime } -Until { param($v) $v -and [string]$v.Stage -ne 'AwaitingSublot' }
$assertions.Add(
    'L2-SRJ-11', '被拒过的子批号重扫之后照常受理：旅程离开等录入，消费的是重扫那条新提交',
    ([string]$consumed.ConsumedSublotMessageId -eq $acceptedSubmission.MessageId -and
        $acceptedSubmission.MessageId -ne $firstSubmission.MessageId),
    "ConsumedSublotMessageId=$($acceptedSubmission.MessageId)（新提交）",
    "ConsumedSublotMessageId=$($consumed.ConsumedSublotMessageId) / 重扫提交=$($acceptedSubmission.MessageId)")

$loadCommitted = @(Wait-L2Condition -Description 'the synthetic peer answered the load command and it committed' `
        -Journal $journal -Criterion 'load-committed' -TimeoutSeconds 60 `
        -Probe { Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'" } `
        -Until { param($v) @($v).Count -eq 1 -and [string]@($v)[0].Status -eq 'Committed' })
$assertions.Add(
    'L2-SRJ-12', '装载操作提交，且全程只有一条装货尝试',
    ($loadCommitted.Count -eq 1 -and (Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId'") -eq 1),
    'Committed / 1 条', "$(if ($loadCommitted.Count -eq 1) { $loadCommitted[0].Status } else { '(none)' }) / $(Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId'")")

# --- 5. 两条拒收始终只有两条，且都不是误导码 -------------------------------------------------------

$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$final = Get-Runtime
$allRejections = @(Get-Rejections)
$allSubmissions = @(Get-Submissions)
$refusedIds = @($allRejections | ForEach-Object { $_.CorrelationId })
# 三次录入、两条拒收：前两条各被拒一次（箱数不符、范围外），第三条是重扫之后受理的那条、没有拒收。
$assertions.Add(
    'L2-SRJ-13', '同一条提交只拒一次：三次录入对应两条拒收，被拒的正是前两条，受理的那条没有拒收',
    ($allRejections.Count -eq 2 -and $allSubmissions.Count -eq 3 -and
        $refusedIds.Count -eq @($refusedIds | Select-Object -Unique).Count -and
        $refusedIds -contains $allSubmissions[0].MessageId -and $refusedIds -contains $allSubmissions[1].MessageId -and
        $refusedIds -notcontains $allSubmissions[2].MessageId),
    '2 条拒收 / 3 条提交 / 被拒的是前两条',
    "$($allRejections.Count) 条拒收 / $($allSubmissions.Count) 条提交 / 被拒 $($refusedIds -join ', ') / 受理 $($allSubmissions[-1].MessageId)")
$assertions.Add(
    'L2-SRJ-14', '全程没有 SUBLOT_SUBMISSION_MISMATCH：被拒过的提交留在库里，之后每一轮都还会被读到，旅程的阻断原因始终为空',
    (Test-L2Null $final.BlockReasonCode),
    '无阻断原因', "BlockReasonCode=$(if (Test-L2Null $final.BlockReasonCode) { '(null)' } else { $final.BlockReasonCode })")

# 只建了一条 RIoT 单：取货那条。装载提交之后服务端停在 AwaitingStationDeparture 等离站期限——本场景把
# 它拉到十分钟（见 setup.psd1），为的是本站只能靠录入成功走出去，代价就是这一段跑不到去关卡那一步。
$riotOrders = @($riot.Snapshot().body.orders)
$gateIntents = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE Purpose = 'TO_GATE'"
$assertions.Add(
    'L2-SRJ-15', '全程只建了一条 RIoT 单（取货），没有重复派车，也还没有关卡单',
    ($riotOrders.Count -eq 1 -and $gateIntents -eq 0),
    1, "$($riotOrders.Count) / $gateIntents")

$journal.Note('Scenario finished.')
