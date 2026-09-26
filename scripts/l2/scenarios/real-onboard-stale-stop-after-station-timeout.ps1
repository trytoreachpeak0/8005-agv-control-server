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

**三趟。**
1. 第一趟不动任何报文：期限到、服务端收尾，看车上是否在十轮之内撤干净（L2-SST-03～05）。两组对照都该红在这里：旧服务端不发
   收尾快照，旧车载端收到了也不撤录入请求。这三条之前先确认这段时间里没断过线、车载端主窗口还在（L2-SST-13）——断线会让车载端
   清空投影，窗口没了 UI Automation 什么都读不到，两者都会把「没撤」读成「撤了」。
2. 第二趟与第三趟用协议故障代理丢掉那张空清单，造出「服务端已收尾、车上还挂着」的窗口——空清单一到车，录入入口就撤了，迟到的
   取消与扫码只可能出现在这个窗口里（PR #361 审查）。第二趟迟到地取消（L2-SST-06、07、11），第三趟迟到地扫码（L2-SST-08、10、14）。
   取消之后窗口就合上了，但不是因为车载端看了 STALE：服务端答复取消之后 3～4 ms，被丢的那张空清单原样重发了一次（同一个
   messageId，run4 与对照② 的代理记录里都是这样），车收到它就撤掉入口（真装置 run2：第二下取消按不到）。所以取消与扫码各开一趟。
   扫码那一趟被丢的空清单直到场景结束都没重发（run4 代理记录），第三趟的撤录入来自车载端对 STALE 拒收的处理。
   代理只能丢、不能扣住再放，所以窗口不靠「放行」收尾：动作做完、下一趟之前若有残留就断一次线清掉，最后也断一次，给下一个场景
   留一台干净的车。断线不当判据用：v2 车载端会话一结束就清空旅程投影（WireToGateSessionClient ResetJourneyProjection），断线本身
   就能清掉残留，拿它证「撤干净」证不到任何一端的修复——所以「撤干净」只在第一趟判。

**哪一条证哪一端**（两组对照实测，evidence/cs325）：车载端修复（hmi#199「空清单到车就撤录入请求」）只由第一趟 L2-SST-03 判到。
L2-SST-10 守的是服务端这一侧——STALE 拒收带的是收尾那一版的号：旧车载端对号比自己手上高的拒收本来就撤录入（HandleSublotRejected
只在作业会话与清单号都相同时保留），所以它对车载端修复没有判别力，旧服务端上（不答）才红。「带的是收尾那一版的号」由 L2-SST-14
直接判：拒收载荷的 currentWorklistRevision 等于被丢那张空清单的 worklistRevision。修好的三端上 L2-SST-10 与 L2-SST-08 同源
（都来自那一条 STALE 拒收），它不是另一端的独立证据。

「某样东西不在」的判据都有正向锚点：第一趟 L2-SST-01、第二趟 L2-SST-09、第三趟 L2-SST-12 先读到了录入框可用、「取消装货」在、清单挂着这单，
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
    $sublots = @(if ($null -ne $rows) { $rows | ForEach-Object { [string]$_.Sublot } })
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
                Revision     = if ($envelope.payload.PSObject.Properties['currentWorklistRevision']) {
                    [long]$envelope.payload.currentWorklistRevision } else { $null }
                Acknowledged = Test-L2RealPresent $row.AcknowledgedAt
            }
        }
    }
    return , @($found)
}

# 这条出站报文经代理送到了车上：代理记下了服务端到车那一行，而且没丢。
function Test-Delivered([string]$MessageId) {
    return @(@((Get-L2RealTraffic $proxy).lines) | Where-Object {
            $_.direction -eq 'server->onboard' -and -not $_.dropped -and
            [string]::Equals([string]$_.messageId, $MessageId, [StringComparison]::OrdinalIgnoreCase) }).Count -ge 1
}

# 代理从开跑以来接过的连接（按接入顺序编号）。车载端每重连一次多一条；最后一条的 closedAt 为空就是说它此刻连着。
function Get-ProxyConnections { return , @(@((Get-L2RealTraffic $proxy).connections)) }

function Format-ProxyConnections([object[]]$Connections) {
    return (@($Connections | ForEach-Object {
                "#$($_.connection) $(if ($null -eq $_.closedAt) { 'open' } else { "closed($($_.closedBy))" })" }) -join '; ')
}

# 车载端进程还在、主窗口还读得到。窗口一没，UI Automation 读什么都是「不在」，「不再要子批」就会假绿。
function Test-OnboardAlive {
    $process = Get-Process -Id $onboard.ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $process -or $process.HasExited -or $null -eq $onboard.Window) { return $false }
    try {
        $null = $onboard.Window.Current.Name
        return $true
    } catch [System.Windows.Automation.ElementNotAvailableException] {
        return $false
    }
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
    # 答复可能没有载荷（ResponsePayload 为 $null），接受时载荷里也没有 problem；StrictMode 下读不存在的属性会抛，先看在不在。
    $response = $answered.ResponsePayload
    $decision = if ($null -ne $response -and $response.PSObject.Properties['decision']) { [string]$response.decision } else { '(no decision)' }
    $reason = if ($null -ne $response -and $response.PSObject.Properties['problem'] -and $null -ne $response.problem) {
        [string]$response.problem.reasonCode } else { '' }
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
# 下面三条「不在」要在同一个会话、同一个窗口里读才算数：从这里起记下握手次数与代理连接数。
$firstHellos = Get-HelloCount
$firstConnections = Get-ProxyConnections

$journal.Note('Nobody scans. Waiting for the station deadline to end the stop.')
$firstSettled = Wait-StationTimeout $first.DemandId 'first'
$assertions.Add(
    'L2-SST-02', '第一趟：站点期限到，服务端以 CANCELLED_BY_STATION_TIMEOUT 结束需求、旅程 Completed、没发过仓位命令（前提）',
    (Test-TimedOut $firstSettled), 'Cancelled / CANCELLED_BY_STATION_TIMEOUT / Completed / 0 条仓位操作', (Format-Settlement $firstSettled))

# 十轮，生产间隔。服务端收尾时暂存的快照早该到车了；十轮之后还在的，就不是在路上。
$null = Wait-L2Iterations -Riot $riot -Count 10 -TimeoutSeconds 90 -Journal $journal
$afterFirst = Get-VehicleView $first.Sublot
$journal.Note("First stop, ten rounds after Completed: $(Format-VehicleView $afterFirst)")
$firstHellosAfter = Get-HelloCount
$firstConnectionsAfter = Get-ProxyConnections
$firstAlive = Test-OnboardAlive
$assertions.Add(
    'L2-SST-13', '第一趟（前提）：从到站到读下面三条，车没重连（SessionHello 与代理连接数不变、当前连接未关）、车载端主窗口还在——否则「不在」可能是断线清空或窗口没了',
    ($firstHellosAfter -eq $firstHellos -and $firstConnectionsAfter.Count -eq $firstConnections.Count -and
        $firstConnectionsAfter.Count -ge 1 -and $null -eq $firstConnectionsAfter[-1].closedAt -and $firstAlive),
    "SessionHello $firstHellos → 不变；代理连接 $($firstConnections.Count) 条 → 不变、最后一条 open；主窗口在",
    "SessionHello $firstHellos → $firstHellosAfter；代理连接 [$(Format-ProxyConnections $firstConnections)] → " +
    "[$(Format-ProxyConnections $firstConnectionsAfter)]；主窗口 $(if ($firstAlive) { '在' } else { '不在' })")
$assertions.Add(
    'L2-SST-03', '第一趟：旅程被站点期限结束之后，车不再要子批',
    (-not $afterFirst.CanSubmit), 'canSubmit=False', (Format-VehicleView $afterFirst))
$assertions.Add(
    'L2-SST-04', '第一趟：旅程被站点期限结束之后，车不再给「取消装货」',
    (-not $afterFirst.OffersCancel), 'offersCancel=False', (Format-VehicleView $afterFirst))
$assertions.Add(
    'L2-SST-05', '第一趟：旅程被站点期限结束之后，车上的清单不再挂着这单',
    (-not $afterFirst.ListsSublot), 'listsSublot=False', (Format-VehicleView $afterFirst))

# 下一趟要一台干净的车：上一趟若有残留（对照组里就是），断一次线让车载端清空投影。修好的三端上第一趟之后这一下不发生。
function Clear-Residue([string]$Sublot, [string]$Label) {
    $view = Get-VehicleView $Sublot
    if (-not ($view.CanSubmit -or $view.OffersCancel -or $view.ListsSublot)) { return }
    $journal.Note("$Label is still on the vehicle ($(Format-VehicleView $view)); dropping the connection once so the next stop starts clean.")
    $null = $proxy.Command('Post', 'disconnect', @{})
    $null = Wait-L2Condition -Description "the onboard reconnected and $Label is gone" `
        -Journal $journal -Criterion "$Label-cleared" -TimeoutSeconds 90 `
        -Probe { Get-VehicleView $Sublot } `
        -Until { param($v) -not $v.CanSubmit -and -not $v.OffersCancel -and -not $v.ListsSublot }
}

<#
一个「服务端已收尾、车上还挂着」的窗口：车到站要子批，布下「丢下一张清单」——这一站那一版已经到车（车上列着这单），丢的只能是
之后那一张，收尾的空清单——然后等站点期限收尾。窗口的前提连同「丢掉的确实是这一趟收尾的空清单」一起判：丢掉的那一行按
messageId 回发件箱对上，丢了就必须是布下规则之后才写进发件箱的一张空清单。它的 worklistRevision 留给 L2-SST-14 用。
#>
function Open-StaleWindow([string]$Prefix, [string]$Label) {
    $stop = Start-UnattendedStop $Prefix
    # 丢掉的那一行按流量记录里的行读：丢弃记录（drops）只有计划名的类型、没有 messageId（TrafficLog.DropRecord）。
    $droppedBefore = @(@((Get-L2RealTraffic $proxy).lines) | Where-Object { $_.dropped } | ForEach-Object { ([string]$_.messageId).ToLowerInvariant() })
    $outboxBefore = @((Get-L2RealOutbound $connection 'CurrentStopWorklistSnapshot') | ForEach-Object { $_.MessageId })
    # 代理把命令的 commandId 当作计划号（ControlPlane drop-message：PlanId = command.CommandId），丢弃记录按它记。
    $planId = [guid]::NewGuid().ToString('N')
    $null = $proxy.Command('Put', 'drop-message', @{ messageType = 'CurrentStopWorklistSnapshot'; count = 1; commandId = $planId })
    $journal.Note("$Label armed plan ${planId}: drop the next CurrentStopWorklistSnapshot to the vehicle (the closing empty worklist).")
    $settled = Wait-StationTimeout $stop.DemandId $Label
    $null = Wait-L2Iterations -Riot $riot -Count 5 -TimeoutSeconds 60 -Journal $journal

    $traffic = Get-L2RealTraffic $proxy
    $droppedNow = @(@($traffic.lines) | Where-Object {
            $_.dropped -and [string]$_.messageType -eq 'CurrentStopWorklistSnapshot' -and
            $droppedBefore -notcontains ([string]$_.messageId).ToLowerInvariant() })
    $droppedIds = @($droppedNow | ForEach-Object { ([string]$_.messageId).ToLowerInvariant() } | Select-Object -Unique)
    $worklists = Get-L2RealOutbound $connection 'CurrentStopWorklistSnapshot'
    # StrictMode 下越界下标会抛，先数再取。
    $matching = @(if ($droppedIds.Count -eq 1) { $worklists | Where-Object { $_.MessageId -eq $droppedIds[0] } })
    $dropped = if ($matching.Count -eq 1) { $matching[0] } else { $null }
    $droppedIsClosing = ($null -ne $dropped -and $outboxBefore -notcontains $dropped.MessageId -and @($dropped.Payload.items).Count -eq 0)
    $droppedRevision = if ($null -ne $dropped) { [long]$dropped.Payload.worklistRevision } else { $null }
    $journal.Note("$Label dropped in this window: $($droppedIds -join ', ') " +
        "(revision $droppedRevision, empty and new since arming: $droppedIsClosing)")

    # 规则没被用掉（这一趟一张都没丢）就会带进下一趟，丢掉下一趟到站的那一版（对照③ 实测丢了 80b02802）。记下来，清掉。
    $consumed = @(@($traffic.drops) | Where-Object { [string]$_.planId -eq $planId }).Count -ge 1
    if (-not $consumed) {
        $journal.Note("$Label drop plan $planId was not consumed; resetting the proxy plan so it does not carry into the next stop.")
        $null = $proxy.Command('Post', 'reset', @{})
    }

    $view = Get-VehicleView $stop.Sublot
    $journal.Note("$Label after Completed, empty worklist dropped: $(Format-VehicleView $view)")
    # 丢了就必须丢对：是这一趟收尾的空清单，不是到站那一版。一张没丢也算窗口——旧服务端根本不发收尾空清单（对照③），车上照样
    # 挂着；那时迟到动作的判据照常求值，红在它们自己身上，而不是被前提挡成「没走到」。L2-SST-14 另要求真的丢了一张。
    $dropOk = ($droppedIds.Count -eq 0 -or $droppedIsClosing)
    return [pscustomobject]@{
        DemandId = $stop.DemandId; Sublot = $stop.Sublot; Settled = $settled; View = $view
        DroppedId = ($droppedIds -join ','); DroppedRevision = $droppedRevision; DroppedIsClosing = $droppedIsClosing; PlanConsumed = $consumed
        Open     = ((Test-TimedOut $settled) -and $dropOk -and $view.CanSubmit -and $view.OffersCancel -and $view.ListsSublot)
    }
}

function Format-Window([object]$W) {
    $dropText = if ($W.DroppedId) { "丢掉 [$($W.DroppedId)] 号 $($W.DroppedRevision) 收尾空清单=$($W.DroppedIsClosing)" } else { '一张没丢（规则已清）' }
    return "$(Format-Settlement $W.Settled)；$dropText；$(Format-VehicleView $W.View)"
}

$windowExpected = 'Cancelled / CANCELLED_BY_STATION_TIMEOUT / Completed / 0；丢了的话恰好一张、是布下规则之后的空清单；canSubmit / offersCancel / listsSublot 均为 True'

# --- 2. 第二趟：窗口里迟到地按「取消装货」 ---------------------------------------------------------------------------

Clear-Residue $first.Sublot 'first'
$second = Open-StaleWindow 'L2-SST-B' 'second'
$assertions.Add(
    'L2-SST-09', '第二趟（前提）：服务端已以站点期限收尾，车上仍要子批、仍给「取消装货」、清单仍挂着这单——迟到的取消只能发生在这个窗口里',
    $second.Open, $windowExpected, (Format-Window $second))
if ($second.Open) {
    # 车给几次就按几次，最多两下（现场那次按了两下）。第一下答复之后被丢的空清单会重发（见文件头），按钮随之撤掉，所以通常只按得到一下。
    $hellosBefore = Get-HelloCount
    $connectionsBefore = Get-ProxyConnections
    $presses = @()
    foreach ($label in 'first', 'second') {
        if ($label -ne 'first' -and -not (Wait-L2RealButtonOffered $onboard $journal $button "cancel-offered-$label" 10)) {
            $journal.Note("No $button to press for the $label press.")
            break
        }
        $presses += Invoke-CancelPress $second.DemandId $label
    }
    $null = Wait-L2Iterations -Riot $riot -Count 8 -TimeoutSeconds 60 -Journal $journal
    $hellosAfter = Get-HelloCount
    $connectionsAfter = Get-ProxyConnections
    $afterPresses = Get-VehicleView $second.Sublot
    $journal.Note("Second stop after the presses: $(Format-VehicleView $afterPresses)")
    $pressText = ($presses | ForEach-Object { "$($_.Decision)/$($_.ReasonCode)" }) -join '；'
    $assertions.Add(
        'L2-SST-06', '第二趟：收尾之后按「取消装货」（车给几次按几次，最多两下），服务端不掐连接、车不重连',
        ($presses.Count -ge 1 -and $hellosAfter -eq $hellosBefore -and $connectionsAfter.Count -eq $connectionsBefore.Count -and
            $connectionsAfter.Count -ge 1 -and $null -eq $connectionsAfter[-1].closedAt),
        ">= 1 下；SessionHello 次数不变（$hellosBefore）；代理连接数不变（$($connectionsBefore.Count)）、当前连接未关",
        "按了 $($presses.Count) 下（$pressText）；SessionHello 按前 $hellosBefore / 按后 $hellosAfter；代理连接 " +
        "[$(Format-ProxyConnections $connectionsBefore)] → [$(Format-ProxyConnections $connectionsAfter)]")

    $workflows = Get-L2RealCount $connection "SELECT COUNT(*) AS Total FROM RecoveryWorkflows WHERE DemandId = '$($second.DemandId)'"
    $statusAfterPresses = [string](Get-L2RealScalar $connection "SELECT Status AS Value FROM AcceptedDemands WHERE DemandId = '$($second.DemandId)'")
    $stale = @($presses | Where-Object {
            $_.Decision -eq 'REJECTED' -and $_.ReasonCode -eq 'WORKLIST_REVISION_STALE' -and $_.DemandId -eq $second.DemandId })
    $assertions.Add(
        'L2-SST-07', '第二趟：迟到的取消每一下都以 WORKLIST_REVISION_STALE 拒绝，不落取消工作流，需求仍是期限判的 Cancelled',
        ($presses.Count -ge 1 -and $stale.Count -eq $presses.Count -and $workflows -eq 0 -and $statusAfterPresses -eq 'Cancelled'),
        '每一下 REJECTED/WORKLIST_REVISION_STALE / 0 条工作流 / Cancelled',
        "$pressText；$workflows 条工作流 / $statusAfterPresses")
    $assertions.Add(
        'L2-SST-11', '第二趟：迟到的取消被拒之后，车不再给「取消装货」（撤按钮的是答复之后重发到车的那张收尾空清单，不是车载端看了 STALE）',
        (-not $afterPresses.OffersCancel), 'offersCancel=False', (Format-VehicleView $afterPresses))
} else {
    Add-L2RealNotReached $assertions @('L2-SST-06', 'L2-SST-07', 'L2-SST-11') '窗口没造出来：车上没有可按的「取消装货」'
}

# --- 3. 第三趟：窗口里迟到地扫码 -------------------------------------------------------------------------------------

Clear-Residue $second.Sublot 'second'
$third = Open-StaleWindow 'L2-SST-C' 'third'
$assertions.Add(
    'L2-SST-12', '第三趟（前提）：服务端已以站点期限收尾，车上仍要子批、仍给「取消装货」、清单仍挂着这单——迟到的扫码只能发生在这个窗口里',
    $third.Open, $windowExpected, (Format-Window $third))
if (-not $third.Open) {
    Add-L2RealNotReached $assertions @('L2-SST-08', 'L2-SST-10', 'L2-SST-14') '窗口没造出来：车上没有可用的录入框'
} else {
    $submissionsBefore = @((Get-L2RealInbound $connection 'SublotSubmitted') | ForEach-Object { $_.MessageId })
    $journal.Note("Late scan of $($third.Sublot) through UI Automation.")
    $onboard.SetSublot($third.Sublot)
    $null = Wait-L2Condition -Description 'the manual submit button became enabled' `
        -Journal $journal -Criterion 'late-submit-ready' -TimeoutSeconds 30 `
        -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
    $onboard.Submit()
    $submission = Wait-L2RealOrLast -Description 'the server received the late sublot entry' `
        -Journal $journal -Criterion 'late-submission' -TimeoutSeconds 30 `
        -Probe { @((Get-L2RealInbound $connection 'SublotSubmitted') | Where-Object { $submissionsBefore -notcontains $_.MessageId })[0] } `
        -Until { param($v) $null -ne $v }
    $submissionId = if ($null -ne $submission) { [string]$submission.MessageId } else { '' }
    # 送达看代理的流量记录，不看发件箱的 AcknowledgedAt：真车载端对 SublotRejected 不回 DurableAck（真装置 run3 实测，车到服务端
    # 只有 SnapshotAppliedAck 一种确认），那一列在真车上恒为空；车收没收到，由代理记下的那一行加上提示区的显示来证。
    $answer = Wait-L2RealOrLast -Description 'the late entry was answered and the answer crossed the proxy to the vehicle' `
        -Journal $journal -Criterion 'late-answer' -TimeoutSeconds 60 `
        -Probe { if ($submissionId) { Get-RejectionsOf $submissionId } else { , @() } } `
        -Until { param($v) @($v).Count -ge 1 -and (Test-Delivered @($v)[0].MessageId) }
    $shown = Wait-L2RealOrLast -Description 'the HMI shows the rejection reason' `
        -Journal $journal -Criterion 'late-answer-shown' -TimeoutSeconds 30 `
        -Probe { Get-RejectionDisplay } -Until { param($v) $v -eq 'WORKLIST_REVISION_STALE' }
    $afterScan = Wait-L2RealOrLast -Description 'the vehicle withdrew sublot entry after the STALE answer' `
        -Journal $journal -Criterion 'withdrawn-after-stale' -TimeoutSeconds 30 `
        -Probe { Get-VehicleView $third.Sublot } -Until { param($v) -not $v.CanSubmit -and -not $v.OffersCancel }

    # 「恰好一条」在撤录入的等待之后重数：等答复时只等到了第一条，第二条若有，此刻也该在发件箱里了。
    # 不写成 @(if ...)：Get-RejectionsOf 以 , @() 整个交出数组，外面再包一层会变成「一个元素是数组」。
    $answers = @()
    if ($submissionId) { $answers = Get-RejectionsOf $submissionId }
    $answerText = if ($answers.Count -eq 0) { '(no SublotRejected)' } else {
        ($answers | ForEach-Object { "$($_.ReasonCode) rev=$($_.Revision) delivered=$(Test-Delivered $_.MessageId)" }) -join '; ' }
    $journal.Note("Late entry answers after the withdrawal wait: $answerText")
    $assertions.Add(
        'L2-SST-08', '第三趟：收尾之后的迟到扫码恰好得到一条 SublotRejected / WORKLIST_REVISION_STALE，经代理送到车上、提示区显示这个原因',
        ($answers.Count -eq 1 -and $answers[0].ReasonCode -eq 'WORKLIST_REVISION_STALE' -and (Test-Delivered $answers[0].MessageId) -and
            $shown -eq 'WORKLIST_REVISION_STALE'),
        '1 × WORKLIST_REVISION_STALE delivered=True；提示区 WORKLIST_REVISION_STALE',
        "录入 $(if ($submissionId) { $submissionId } else { '(not received)' })：$answerText；提示区 $(if ($shown) { $shown } else { '(none)' })")
    $assertions.Add(
        'L2-SST-10', '第三趟：迟到的扫码被 STALE 拒绝之后，车不再要子批、不再给「取消装货」（守服务端一侧；修好的三端上与 L2-SST-08 同源）',
        (-not $afterScan.CanSubmit -and -not $afterScan.OffersCancel), 'canSubmit=False / offersCancel=False',
        (Format-VehicleView $afterScan))
    # 服务端 STALE 带的是收尾那一版的号：拒收载荷的 currentWorklistRevision 等于被丢那张空清单的 worklistRevision（窗口里读到的）。
    # 被丢的那张若是别的号，旧车载端对号更低的拒收会保留录入——L2-SST-10 看不出来的那一半由这一条判。
    $revisionsMatch = ($answers.Count -ge 1 -and $null -ne $third.DroppedRevision -and
        @($answers | Where-Object { $_.Revision -ne $third.DroppedRevision }).Count -eq 0)
    $assertions.Add(
        'L2-SST-14', '第三趟：STALE 拒收带的是收尾那一版的号——每一条拒收的 currentWorklistRevision 都等于被丢那张收尾空清单的 worklistRevision',
        $revisionsMatch, "每条 currentWorklistRevision = $($third.DroppedRevision)（被丢的 $($third.DroppedId)）",
        "拒收 $answerText；被丢的 $($third.DroppedId) 号 $($third.DroppedRevision)")
}

# 给下一个场景一台干净的车；不当判据用（见文件头）。
$journal.Note('Dropping the connection once so the vehicle leaves this scenario without the last stop.')
$null = $proxy.Command('Post', 'disconnect', @{})
