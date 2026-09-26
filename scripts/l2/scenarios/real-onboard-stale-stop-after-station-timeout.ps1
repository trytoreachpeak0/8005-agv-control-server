#Requires -Version 7

<#
站点期限结束一站之后，真车载端上不残留那一站（program#86 v2，control-server#325）。

现场线 2026-09-15 在 agv01 上看到：站点等待到期、服务端把需求判 CANCELLED_BY_STATION_TIMEOUT 并收尾旅程之后，HMI 仍挂着
那一站——仍要子批、仍给「取消装货」，按两下第二下掉线。现场线的场景 L2-SST-01～07 钉住的是这件事；本场景是它在 v2 上的版本。

v2 的修法两端都有，缺一不可：
- 服务端 control-server#323：旅程收尾时给车发三张收尾快照（空清单、无腿的行程、车辆业务状态）；control-server#324：收尾之后
  才到的扫码答 SublotRejected / WORKLIST_REVISION_STALE，迟到的取消以同一个码拒绝。
- 车载端 onboard-hmi#199：收到结束本站的清单（空或更高号）就撤掉录入请求与「取消装货」入口；收到 STALE 拒收同样撤掉。

合成车载端只看得到线上报文，看不到 _currentEntryRequest 与按钮；车上残不残留只有真 WPF 加 UI Automation 看得见。

**两趟，各证一半。**
1. 第一趟不动任何报文：期限到、服务端收尾，看车上是否在十轮之内撤干净（L2-SST-03～05）。两组对照都该红在这里：旧服务端不发
   收尾快照，旧车载端收到了也不撤录入请求。
2. 第二趟用协议故障代理丢掉那张空清单，造出「服务端已收尾、车上还挂着」的窗口——空清单一到车，录入入口就撤了，迟到的取消与
   扫码只可能出现在这个窗口里（PR #361 审查）。在窗口里按两下「取消装货」、扫一次码，看服务端怎么答（L2-SST-06～08、10）。
   代理只能丢、不能扣住再放，所以这一趟不靠「放行」收尾：窗口里的动作做完就结束，最后断一次线只为给下一个场景留一台干净的车。
   断线不当判据用：v2 车载端会话一结束就清空旅程投影（WireToGateSessionClient ResetJourneyProjection），断线本身就能清掉残留，
   拿它证「撤干净」证不到任何一端的修复——所以「撤干净」只在第一趟判。

「某样东西不在」的判据都有正向锚点：第一趟 L2-SST-01 与第二趟 L2-SST-09 先读到了录入框可用、「取消装货」在、清单挂着这单，
同一个读法后来读到「不在」才有意义（control-server#260 的教训）。

断言读服务端 SQLite、代理的流量记录与车载端 UI Automation。「取消装货」被拒时车载端会另弹「取消装货失败」模态框
（MainWindow.xaml.cs，#361 剩余风险），它挡住后续操作，所以每按一下都先关掉它；框在不在不当判据。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2MultiStopJourney.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$onboard = $Context.Onboard
$riot = $Context.Riot
$connection = $Context.Connection
$proxy = $Context.ProtocolProxy

if ($null -eq $proxy) { throw 'This scenario needs ProtocolFaultProxy = $true in its setup file.' }
$button = '取消装货'
$failure = '取消装货失败'

# --- 读法 -------------------------------------------------------------------------------------------------------------

# 车上此刻给的：录入框可用、「取消装货」在、清单里挂着哪些子批。Get-L2WorklistRows 返回 $null 表示清单不在树里——也就是没挂任何一条。
function Get-VehicleView([string]$Sublot) {
    $rows = Get-L2WorklistRows $onboard
    $sublots = if ($null -eq $rows) { @() } else { @($rows | ForEach-Object { [string]$_.Sublot }) }
    return [pscustomobject]@{
        CanSubmit     = [bool]$onboard.CanSubmit()
        OffersCancel  = [bool]$onboard.ButtonAvailable($button)
        ListsSublot   = $sublots -contains $Sublot
        WorklistShown = ($null -ne $rows)
        Rows          = ($sublots -join ',')
    }
}

function Format-VehicleView([object]$View) {
    return ("canSubmit=$($View.CanSubmit) offersCancel=$($View.OffersCancel) listsSublot=$($View.ListsSublot) " +
        "worklist=[$($View.Rows)]")
}

# 提示区里那一行拒收原因（g3-sublot-rejected 的读法）。Collapsed 的元素不进 UIA 树，「不在」就是「没显示」。
function Get-RejectionDisplay {
    $element = $onboard.Element('AutomationId', 'SublotRejectionReason')
    if (-not $element) { return $null }
    try {
        if ($element.Current.IsOffscreen) { return $null }
        return [string]$element.Current.ItemStatus
    } catch [System.Windows.Automation.ElementNotAvailableException] {
        return $null
    }
}

function Get-Settlement([string]$DemandId) {
    $demand = Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$DemandId'"
    $key = Get-L2RealScalar $connection "SELECT TransportDemandKey AS Value FROM AcceptedDemands WHERE DemandId = '$DemandId'"
    $suppression = if ($key) {
        Get-L2RealScalar $connection "SELECT ReasonCode AS Value FROM TransportDemandSuppressions WHERE TransportDemandKey = '$key'"
    } else { $null }
    $stage = Get-L2RealStage $connection $DemandId
    $operations = Get-L2RealCount $connection "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$DemandId'"
    return [pscustomobject]@{
        Status = [string]$demand; Suppression = [string]$suppression; Stage = [string]$stage; Operations = [int]$operations
    }
}

function Format-Settlement([object]$S) { return "$($S.Status) / $($S.Suppression) / $($S.Stage) / $($S.Operations) 条仓位操作" }

function Get-HelloCount {
    return Get-L2RealCount $connection "SELECT COUNT(*) AS Total FROM ProtocolInbox WHERE MessageType = 'SessionHello'"
}

# 服务端给这条录入的全部拒收：correlationId 是录入的 messageId。PayloadJson 存的是整个信封。
function Get-RejectionsOf([string]$SubmissionId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, PayloadJson, AcknowledgedAt FROM ProtocolOutbox WHERE MessageType = 'SublotRejected' ORDER BY CreatedAt")
    $found = foreach ($row in $rows) {
        $envelope = [string]$row.PayloadJson | ConvertFrom-Json -DateKind String
        if ([string]$envelope.correlationId -eq $SubmissionId) {
            [pscustomobject]@{
                MessageId    = [string]$row.MessageId
                ReasonCode   = [string]$envelope.payload.problem.reasonCode
                Acknowledged = Test-L2RealPresent $row.AcknowledgedAt
            }
        }
    }
    return , @($found)
}

# 车到站、要子批，但没人扫。返回需求 id、子批与到站那一刻车上的样子。
function Start-UnattendedStop([string]$Prefix) {
    $demandGuid = [guid]::NewGuid()
    $demandId = $demandGuid.ToString('D')
    $sublot = "$Prefix-$($Context.RunId)"
    $journal.Note("Publishing demand $($demandGuid.ToString('N')) (sublot $sublot).")
    $null = $Context.MesIngest.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
        sublot = $sublot; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
    })
    $null = Wait-L2Condition -Description "demand $Prefix was dispatched to the pickup station" `
        -Journal $journal -Criterion "$Prefix-stage" -TimeoutSeconds 120 `
        -Probe { Get-L2RealStage $connection $demandId } `
        -Until { param($v) $v -eq 'AwaitingPickupArrival' }
    $intent = Wait-L2RealIntent $Context $demandId 'TO_PICKUP'
    Move-L2RealVehicleTo $Context $intent $Context.PickupStationRiotId 'the pickup station'
    $view = Wait-L2RealOrLast -Description "the vehicle offers sublot entry and $button for $Prefix" `
        -Journal $journal -Criterion "$Prefix-offered" -TimeoutSeconds 120 `
        -Probe { Get-VehicleView $sublot } `
        -Until { param($v) $v.CanSubmit -and $v.OffersCancel -and $v.ListsSublot }
    $journal.Note("$Prefix at the stop: $(Format-VehicleView $view)")
    return [pscustomobject]@{ DemandId = $demandId; Sublot = $sublot; View = $view }
}

function Wait-StationTimeout([string]$DemandId, [string]$Prefix) {
    return Wait-L2RealOrLast -Description "the station deadline ended $Prefix and closed the journey" `
        -Journal $journal -Criterion "$Prefix-settled" -TimeoutSeconds 180 `
        -Probe { Get-Settlement $DemandId } `
        -Until { param($v) $v.Status -eq 'Cancelled' -and $v.Stage -eq 'Completed' }
}

function Test-TimedOut([object]$S) {
    return ($S.Status -eq 'Cancelled' -and $S.Suppression -eq 'CANCELLED_BY_STATION_TIMEOUT' -and
        $S.Stage -eq 'Completed' -and $S.Operations -eq 0)
}

# 按一下「取消装货」：确认框点「是」，被拒时关掉随后的「取消装货失败」框。返回按下之后服务端新收到的那一条请求的答复。
function Invoke-CancelPress([string]$DemandId, [string]$Label) {
    $before = (Get-L2RealInbound $connection 'LoadCancellationStartRequested').Count
    $null = Invoke-L2RealConfirmedButton $onboard $journal $button $button
    $answered = Wait-L2RealOrLast -Description "the server answered the $Label press" `
        -Journal $journal -Criterion "cancel-$Label" -TimeoutSeconds 30 `
        -Probe {
            $all = Get-L2RealInbound $connection 'LoadCancellationStartRequested'
            if ($all.Count -gt $before) { $all[$all.Count - 1] } else { $null }
        } `
        -Until { param($v) $null -ne $v }
    $closed = Confirm-L2RealNotice $onboard $journal $failure 10
    if ($null -eq $answered) {
        $journal.Note("$Label press: no request reached the server (failure notice shown: $closed).")
        return [pscustomobject]@{ Decision = '(no request)'; ReasonCode = ''; DemandId = '' }
    }
    $decision = [string]$answered.ResponsePayload.decision
    $reason = if ($answered.ResponsePayload.problem) { [string]$answered.ResponsePayload.problem.reasonCode } else { '' }
    $journal.Note("$Label press: $decision $reason (demand $($answered.Payload.demandId); failure notice shown: $closed).")
    return [pscustomobject]@{ Decision = $decision; ReasonCode = $reason; DemandId = [string]$answered.Payload.demandId }
}

# --- 0. 车载端确实走代理 ---------------------------------------------------------------------------------------------

$hellos = Wait-L2Condition -Description 'the onboard session was established through the protocol fault proxy' `
    -Journal $journal -Criterion 'proxy-session' -TimeoutSeconds 30 `
    -Probe { @(@((Get-L2RealTraffic $proxy).lines) | Where-Object { $_.direction -eq 'onboard->server' -and $_.messageType -eq 'SessionHello' }).Count } `
    -Until { param($v) $v -ge 1 }
$assertions.Add(
    'L2-SST-00', '车载端的会话经协议故障代理建立（否则第二趟丢不掉那张空清单）',
    ($hellos -ge 1), '>= 1 SessionHello through the proxy', $hellos)

# --- 1. 第一趟：不动任何报文，期限到、服务端收尾，车上要撤干净 -------------------------------------------------------

$first = Start-UnattendedStop 'L2-SST-A'
$assertions.Add(
    'L2-SST-01', '第一趟：车到站后要子批、给「取消装货」、清单挂着这单（前提，也是下面三条「不在」的正向锚点）',
    ($first.View.CanSubmit -and $first.View.OffersCancel -and $first.View.ListsSublot),
    'canSubmit / offersCancel / listsSublot 均为 True', (Format-VehicleView $first.View))

$journal.Note('Nobody scans. Waiting for the station deadline to end the stop.')
$firstSettled = Wait-StationTimeout $first.DemandId 'first'
$assertions.Add(
    'L2-SST-02', '第一趟：站点期限到，服务端以 CANCELLED_BY_STATION_TIMEOUT 结束需求、旅程 Completed、没发过仓位命令（前提）',
    (Test-TimedOut $firstSettled), 'Cancelled / CANCELLED_BY_STATION_TIMEOUT / Completed / 0 条仓位操作', (Format-Settlement $firstSettled))

# 十轮，生产间隔。服务端收尾时暂存的快照早该到车了；十轮之后还在的，就不是在路上。
$null = Wait-L2Iterations -Riot $riot -Count 10 -TimeoutSeconds 90 -Journal $journal
$afterFirst = Get-VehicleView $first.Sublot
$journal.Note("First stop, ten rounds after Completed: $(Format-VehicleView $afterFirst)")
$assertions.Add(
    'L2-SST-03', '第一趟：旅程被站点期限结束之后，车不再要子批',
    (-not $afterFirst.CanSubmit), 'canSubmit=False', (Format-VehicleView $afterFirst))
$assertions.Add(
    'L2-SST-04', '第一趟：旅程被站点期限结束之后，车不再给「取消装货」',
    (-not $afterFirst.OffersCancel), 'offersCancel=False', (Format-VehicleView $afterFirst))
$assertions.Add(
    'L2-SST-05', '第一趟：旅程被站点期限结束之后，车上的清单不再挂着这单',
    (-not $afterFirst.ListsSublot), 'listsSublot=False', (Format-VehicleView $afterFirst))

# 第二趟要一台干净的车：第一趟若有残留（对照组里就是），断一次线让车载端清空投影。修好的三端上这一下什么都不改变。
if ($afterFirst.CanSubmit -or $afterFirst.OffersCancel -or $afterFirst.ListsSublot) {
    $journal.Note('The first stop is still on the vehicle; dropping the connection once so the second stop starts clean.')
    $null = $proxy.Command('Post', 'disconnect', @{})
    $null = Wait-L2Condition -Description 'the onboard reconnected and the first stop is gone' `
        -Journal $journal -Criterion 'first-cleared' -TimeoutSeconds 90 `
        -Probe { Get-VehicleView $first.Sublot } `
        -Until { param($v) -not $v.CanSubmit -and -not $v.OffersCancel -and -not $v.ListsSublot }
}

# --- 2. 第二趟：丢掉那张空清单，在「服务端已收尾、车上还挂着」的窗口里迟到地取消与扫码 --------------------------------

$second = Start-UnattendedStop 'L2-SST-B'
# 这一站那一版清单已经到车（车上列着这单），此刻布下的「丢下一张清单」丢的只能是之后那一张——收尾的空清单。
$null = $proxy.Command('Put', 'drop-message', @{ messageType = 'CurrentStopWorklistSnapshot'; count = 1 })
$journal.Note('Armed: drop the next CurrentStopWorklistSnapshot to the vehicle (the closing empty worklist).')

$secondSettled = Wait-StationTimeout $second.DemandId 'second'
$null = Wait-L2Iterations -Riot $riot -Count 5 -TimeoutSeconds 60 -Journal $journal
$drops = @(@((Get-L2RealTraffic $proxy).drops) | Where-Object { [string]$_.messageType -eq 'CurrentStopWorklistSnapshot' })
$emptyWorklists = @((Get-L2RealOutbound $connection 'CurrentStopWorklistSnapshot') | Where-Object { @($_.Payload.items).Count -eq 0 })
$journal.Note("Dropped worklists: $(@($drops | ForEach-Object { [string]$_.messageId }) -join ', '); " +
    "empty worklists in the outbox: $(@($emptyWorklists | ForEach-Object { $_.MessageId }) -join ', ')")
$inWindow = Get-VehicleView $second.Sublot
$journal.Note("Second stop after Completed, empty worklist dropped: $(Format-VehicleView $inWindow)")
$assertions.Add(
    'L2-SST-09', '第二趟（前提）：服务端已以站点期限收尾，车上仍要子批、仍给「取消装货」、清单仍挂着这单——迟到的动作只能发生在这个窗口里',
    ((Test-TimedOut $secondSettled) -and $inWindow.CanSubmit -and $inWindow.OffersCancel -and $inWindow.ListsSublot),
    'Cancelled / CANCELLED_BY_STATION_TIMEOUT / Completed / 0；canSubmit / offersCancel / listsSublot 均为 True',
    "$(Format-Settlement $secondSettled)；$(Format-VehicleView $inWindow)")
if (-not ($inWindow.CanSubmit -and $inWindow.OffersCancel)) {
    Add-L2RealNotReached $assertions @('L2-SST-06', 'L2-SST-07', 'L2-SST-08', 'L2-SST-10') '窗口没造出来：车上没有可按的录入框或「取消装货」'
    return
}

# 两下「取消装货」。服务端的判定只与需求状态有关，与两下之间隔多久无关。
$hellosBefore = Get-HelloCount
$firstPress = Invoke-CancelPress $second.DemandId 'first'
$secondPress = Invoke-CancelPress $second.DemandId 'second'
$null = Wait-L2Iterations -Riot $riot -Count 8 -TimeoutSeconds 60 -Journal $journal
$hellosAfter = Get-HelloCount
$assertions.Add(
    'L2-SST-06', '第二趟：收尾之后按两下「取消装货」，服务端不掐连接、车不重连',
    ($hellosAfter -eq $hellosBefore), "SessionHello 次数不变（$hellosBefore）",
    "按前 $hellosBefore / 按后 $hellosAfter")

$workflows = Get-L2RealCount $connection "SELECT COUNT(*) AS Total FROM RecoveryWorkflows WHERE DemandId = '$($second.DemandId)'"
$statusAfterPresses = [string](Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($second.DemandId)'")
$pressesStale = @($firstPress, $secondPress | Where-Object {
        $_.Decision -eq 'REJECTED' -and $_.ReasonCode -eq 'WORKLIST_REVISION_STALE' -and $_.DemandId -eq $second.DemandId })
$assertions.Add(
    'L2-SST-07', '第二趟：迟到的取消两下都以 WORKLIST_REVISION_STALE 拒绝，不落取消工作流，需求仍是期限判的 Cancelled',
    ($pressesStale.Count -eq 2 -and $workflows -eq 0 -and $statusAfterPresses -eq 'Cancelled'),
    '2 × REJECTED/WORKLIST_REVISION_STALE / 0 条工作流 / Cancelled',
    "第一下 $($firstPress.Decision)/$($firstPress.ReasonCode)；第二下 $($secondPress.Decision)/$($secondPress.ReasonCode)；" +
    "$workflows 条工作流 / $statusAfterPresses")

# 迟到的扫码。取消被拒不撤录入请求（车载端只忘掉那次取消），所以录入框还在；前提照样读一次。
$beforeScan = Get-VehicleView $second.Sublot
if (-not $beforeScan.CanSubmit) {
    $journal.Note("Entry no longer offered before the late scan: $(Format-VehicleView $beforeScan)")
    Add-L2RealNotReached $assertions @('L2-SST-08', 'L2-SST-10') '两下取消之后车上已不能录入，迟到的扫码做不了'
    return
}
$submissionsBefore = @((Get-L2RealInbound $connection 'SublotSubmitted') | ForEach-Object { $_.MessageId })
$journal.Note("Late scan of $($second.Sublot) through UI Automation.")
$onboard.SetSublot($second.Sublot)
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'late-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()
$submission = Wait-L2RealOrLast -Description 'the server received the late sublot entry' `
    -Journal $journal -Criterion 'late-submission' -TimeoutSeconds 30 `
    -Probe { @((Get-L2RealInbound $connection 'SublotSubmitted') | Where-Object { $submissionsBefore -notcontains $_.MessageId })[0] } `
    -Until { param($v) $null -ne $v }
$submissionId = if ($null -ne $submission) { [string]$submission.MessageId } else { '' }
$answer = Wait-L2RealOrLast -Description 'the late entry was answered and the vehicle acknowledged the answer' `
    -Journal $journal -Criterion 'late-answer' -TimeoutSeconds 60 `
    -Probe { if ($submissionId) { Get-RejectionsOf $submissionId } else { , @() } } `
    -Until { param($v) @($v).Count -ge 1 -and @($v)[0].Acknowledged }
$shown = Wait-L2RealOrLast -Description 'the HMI shows the rejection reason' `
    -Journal $journal -Criterion 'late-answer-shown' -TimeoutSeconds 30 `
    -Probe { Get-RejectionDisplay } -Until { param($v) $v -eq 'WORKLIST_REVISION_STALE' }
# 等到的是一个数组时 return 会把它展开：一条时拿到的是那一条本身，零条时是 $null——@($null) 数出来是 1，所以滤掉空值再数。
$answers = @($answer | Where-Object { $null -ne $_ })
$answerText = if ($answers.Count -eq 0) { '(no SublotRejected)' } else {
    ($answers | ForEach-Object { "$($_.ReasonCode) ack=$($_.Acknowledged)" }) -join '; ' }
$assertions.Add(
    'L2-SST-08', '第二趟：收尾之后的迟到扫码恰好得到一条 SublotRejected / WORKLIST_REVISION_STALE，车已确认、提示区显示这个原因',
    ($answers.Count -eq 1 -and $answers[0].ReasonCode -eq 'WORKLIST_REVISION_STALE' -and $answers[0].Acknowledged -and
        $shown -eq 'WORKLIST_REVISION_STALE'),
    '1 × WORKLIST_REVISION_STALE ack=True；提示区 WORKLIST_REVISION_STALE',
    "录入 $(if ($submissionId) { $submissionId } else { '(not received)' })：$answerText；提示区 $(if ($shown) { $shown } else { '(none)' })")

$afterScan = Wait-L2RealOrLast -Description 'the vehicle withdrew sublot entry after the STALE answer' `
    -Journal $journal -Criterion 'withdrawn-after-stale' -TimeoutSeconds 30 `
    -Probe { Get-VehicleView $second.Sublot } -Until { param($v) -not $v.CanSubmit -and -not $v.OffersCancel }
$assertions.Add(
    'L2-SST-10', '第二趟：迟到的扫码被 STALE 拒绝之后，车不再要子批、不再给「取消装货」',
    (-not $afterScan.CanSubmit -and -not $afterScan.OffersCancel), 'canSubmit=False / offersCancel=False',
    (Format-VehicleView $afterScan))

# 给下一个场景一台干净的车；不当判据用（见文件头）。
$journal.Note('Dropping the connection once so the vehicle leaves this scenario without the second stop.')
$null = $proxy.Command('Post', 'disconnect', @{})
