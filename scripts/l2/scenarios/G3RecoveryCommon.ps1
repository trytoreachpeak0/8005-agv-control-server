#Requires -Version 7

<#
G3 `FP-IS-07` 场景共用的读库、读线上消息、界面等待与「装载以 UNKNOWN 结束」前半段。由各场景点号引入，不是场景本身，
没有 setup 文件，编排器也不会单独运行它。

断言依旧只读服务端的库、模拟器与假 RIoT 快照；这里的界面函数只用来驱动与等待入口出现。
#>

Set-StrictMode -Version Latest

function ConvertTo-G3Instant([object]$value) {
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

function Test-G3Present([object]$value) { return ($null -ne $value -and [string]$value -ne '') }

function Get-G3Scalar([object]$Connection, [string]$Sql) {
    $rows = Invoke-L2Query -Connection $Connection -Sql $Sql
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Value
}

function Get-G3Count([object]$Connection, [string]$Sql) {
    $rows = Invoke-L2Query -Connection $Connection -Sql $Sql
    return [int]$rows[0].Total
}

# 车载端发来的一类消息，按服务端收下的先后。Response / ResponsePayload 是服务端第一份应答的第一行（多行应答的
# 后续是 SessionReadiness）。收件箱按 messageId 存一行：同号补发被判为等价重放后，这一行换成补发的那份。
function Get-G3Inbound([object]$Connection, [string]$MessageType) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT MessageId, RequestJson, ContentHash, FirstResponseJson, ReceivedAt FROM ProtocolInbox " +
        "WHERE MessageType = '$MessageType' ORDER BY ReceivedAt, MessageId")
    $messages = foreach ($row in $rows) {
        $request = [string]$row.RequestJson | ConvertFrom-Json
        $response = if (Test-G3Present $row.FirstResponseJson) {
            @(([string]$row.FirstResponseJson) -split "`n" | Where-Object { $_ })[0] | ConvertFrom-Json
        } else { $null }
        [pscustomobject]@{
            MessageId       = [string]$row.MessageId
            At              = ConvertTo-G3Instant $row.ReceivedAt
            Generation      = [long]$request.sessionGeneration
            Payload         = $request.payload
            PayloadJson     = $request.payload | ConvertTo-Json -Depth 20 -Compress
            ContentHash     = [string]$row.ContentHash
            Response        = if ($null -ne $response) { [string]$response.messageType } else { '' }
            ResponsePayload = if ($null -ne $response -and $response.PSObject.Properties['payload']) { $response.payload } else { $null }
            ResponseLine    = $response
        }
    }
    return , @($messages)
}

# 服务端发给车载端的一类消息，按入发件箱的先后。
function Get-G3Outbound([object]$Connection, [string]$MessageType) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT MessageId, PayloadJson, CreatedAt, AcknowledgedAt, FencedAt FROM ProtocolOutbox " +
        "WHERE MessageType = '$MessageType' ORDER BY CreatedAt, MessageId")
    $messages = foreach ($row in $rows) {
        [pscustomobject]@{
            MessageId    = [string]$row.MessageId
            At           = ConvertTo-G3Instant $row.CreatedAt
            Payload      = ([string]$row.PayloadJson | ConvertFrom-Json).payload
            Acknowledged = Test-G3Present $row.AcknowledgedAt
            Fenced       = Test-G3Present $row.FencedAt
        }
    }
    return , @($messages)
}

function Get-G3Progress([object]$Connection, [string]$AttemptId) {
    $mine = foreach ($message in (Get-G3Inbound $Connection 'OperationProgress')) {
        if ([string]$message.Payload.slotOperationAttemptId -ne $AttemptId) { continue }
        [pscustomobject]@{
            At     = $message.At
            Phase  = [string]$message.Payload.phase
            Active = @($message.Payload.activeUnlockSlots | ForEach-Object { [int]$_ })
        }
    }
    return , @($mine)
}

function Get-G3Session([object]$Connection) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        'SELECT SessionGeneration, Readiness, ReasonCode, PendingResultIdsJson, ForcedRecoveryGeneration, ' +
        'ReportedForcedRecoveryGeneration FROM SessionRecoveries')
    return $rows[0]
}

function Get-G3SlotState([object]$Simulator, [int]$SlotNo) {
    $slot = @($Simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $SlotNo })[0]
    return "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
}

function Format-G3Slots([object[]]$Slots) { return (@($Slots | ForEach-Object { [int]$_ } | Sort-Object) -join ',') }

# 入口的显隐与可用都绑在各自的 CanRequest* 上，没出现与不可用读起来一样。
function Wait-G3ButtonOffered([object]$Onboard, [object]$Journal, [string]$Name, [string]$Criterion, [int]$TimeoutSeconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $available = [bool]$Onboard.ButtonAvailable($Name)
        $Journal.Observe($Criterion, $available, $null)
        if ($available -or [DateTimeOffset]::UtcNow -ge $deadline) { return $available }
        Start-Sleep -Milliseconds 500
    }
}

if (-not ('G3L2.DialogNative' -as [type])) {
    Add-Type -Namespace 'G3L2' -Name 'DialogNative' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
public static extern bool PostMessage(System.IntPtr hWnd, uint msg, System.IntPtr wParam, System.IntPtr lParam);
'@
}

<#
答一个模态对话框：等标题为 $Title 的窗口出现，按 AutomationId 为 $ButtonAutomationId 的按钮，并确认窗口真的关了。

UIA Invoke 在刚重启的车载端上会失败：窗口没拿到前台时，MessageBox 的按钮先报 "Operation is not valid due to the
current state of the object"，再报 "Hot key is already registered"，车载端什么都没收到（resume-001 / resume-005，
2026-09-14）。所以 Invoke 失败就改向按钮的 Win32 窗口投递 BM_CLICK：投递是异步的，不要求前台。
没有这个对话框返回 $false；按了但窗口一直不关就抛出。
#>
function Invoke-G3DialogButton([object]$Onboard, [object]$Journal, [string]$Title, [string]$ButtonAutomationId, [int]$AppearSeconds) {
    $isButton = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $ButtonAutomationId)
    $appearDeadline = [DateTimeOffset]::UtcNow.AddSeconds($AppearSeconds)
    $answerDeadline = $null
    $pressed = $false
    while ($true) {
        # Select-Object rather than [0]: under StrictMode an index into the empty list a closed dialog
        # leaves is an error, and the dialog closing is exactly what a successful press looks like.
        $dialog = $Onboard.Windows() | Where-Object { $_.Current.Name -eq $Title } | Select-Object -First 1
        if ($null -eq $dialog) {
            if ($pressed) { return $true }
            if ([DateTimeOffset]::UtcNow -ge $appearDeadline) { return $false }
            Start-Sleep -Milliseconds 250
            continue
        }
        if ($null -eq $answerDeadline) { $answerDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15) }
        if ([DateTimeOffset]::UtcNow -ge $answerDeadline) {
            throw "Dialog '$Title' is still open 15s after its button $ButtonAutomationId was pressed."
        }
        $button = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $isButton)
        if ($null -eq $button) { throw "Dialog '$Title' has no button with AutomationId '$ButtonAutomationId'." }
        try {
            $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        } catch {
            $handle = [IntPtr]$button.Current.NativeWindowHandle
            $Journal.Note("Invoke on '$Title' button $ButtonAutomationId failed ($($_.Exception.Message)); posting BM_CLICK to hwnd $handle.")
            if ($handle -eq [IntPtr]::Zero -or -not [G3L2.DialogNative]::PostMessage($handle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)) {
                throw "Could not press '$Title' button ${ButtonAutomationId}: no window handle to post BM_CLICK to."
            }
        }
        $pressed = $true
        Start-Sleep -Milliseconds 500
    }
}

<#
按一个恢复入口并答它的确认框。按钮刚随会话状态出现时，UIA 读得到它可用，但窗口可能还在重排，点击会落空、确认框
不出来（manual-002，2026-09-14：主窗口出现 0.8 秒后按下，30 秒没有确认框，车载端也没发出任何请求）。所以先要它连续
一秒保持可用再按；按下后 10 秒内没有确认框就重按，最多三次。没有确认框就没有请求，重按不会造成重复请求。
#>
function Invoke-G3ConfirmedButton([object]$Onboard, [object]$Journal, [string]$Name, [string]$DialogTitle, [int]$Attempts = 3) {
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $stableSince = $null
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            if ([bool]$Onboard.ButtonAvailable($Name)) {
                if ($null -eq $stableSince) { $stableSince = [DateTimeOffset]::UtcNow }
                elseif (([DateTimeOffset]::UtcNow - $stableSince).TotalSeconds -ge 1) { break }
            } else { $stableSince = $null }
            Start-Sleep -Milliseconds 200
        }
        $Journal.Note("Pressing $Name (attempt $attempt).")
        $Onboard.InvokeButton($Name)
        if (Invoke-G3DialogButton $Onboard $Journal $DialogTitle '6' 10) { return $true }
        $Journal.Note("No '$DialogTitle' dialog after pressing $Name (attempt $attempt).")
    }
    throw "Pressing '$Name' never raised the '$DialogTitle' confirmation in $Attempts attempts."
}

# 只有「确定」一个按钮的提示框。Win32 给这种框的唯一按钮的控件号不一定是 IDOK，所以两个都试。
function Confirm-G3Notice([object]$Onboard, [string]$Title, [object]$Journal = $null) {
    $log = if ($null -ne $Journal) { $Journal } else { [pscustomobject]@{} | Add-Member -MemberType ScriptMethod -Name Note -Value { param($m) } -PassThru }
    foreach ($id in @('2', '1')) {
        try { return Invoke-G3DialogButton $Onboard $log $Title $id 5 }
        catch { if ($_.Exception.Message -notlike "*has no button with AutomationId*") { throw } }
    }
    throw "Notice '$Title' has neither button 2 nor button 1."
}

function Add-G3NotReached([object]$Assertions, [string[]]$Ids, [string]$Why) {
    foreach ($id in $Ids) {
        $Assertions.Add($id, "未到达：$Why", $false, '(reached)', "(not reached) $Why")
    }
}

<#
需求受理 → 车到取货点 → UIA 录入 → 装载开到「在等操作员」→ 车载端断电 → 断电期间空仓门被关上 → 车载端重启，
中断结算报 UNKNOWN → 服务端判 RecoveryRequired、旅程停在 Blocked。

**为什么不再是「关上空门、等车载端超时」。**2026-09-18 之前这里关上空门后等车载端操作超时（120 秒）交一份结果。
onboard-hmi#72（批次5-20）之后 v2 车载端读到「门关了、货没放」会自己重新开锁，按规格第 19.4 节决策 3 与 program#55
不再产出 `FAILED`／`OPERATOR_TIMEOUT`，那份结果永远等不到（control-server#87 自检，缺陷记录
`docs/defects/20260918-journey-g3-scenarios-assume-empty-close-fails-the-load.md`，由 control-server#128 改写）。

**v2 上真实可达的办法**是 ADR-cross-0058 决策 2 的反面，与 control-server#88 的 `real-onboard-compensate-then-reconnect`
同一个：车在等操作员时进程没了，它不在的时候门被空着关上。重启后车载端的中断结算（`SettleInterruptedAsync`，
onboard-hmi#70）只读实时 IO、不打任何脉冲：门关了、锁上了、输出复位了，唯独仓里没有货，不是装货的最终态，于是报
`UNKNOWN`，检查点 `SAFE_FINISH_REACHED`。仓空着、关着、锁着，所以补偿、交接不开门就能证空，恢复原操作也有已证实
的物理断点。**车载端不会在门被关上的那一刻重开**——它那时不在。

返回这笔装载的身份、第一份结果（重启之后的新会话里交的那一份）与仓位。调用之后车载端已经重启过一次，
`Context.Onboard` 是新进程的驱动，调用方要重新取。
#>
function Invoke-G3UnknownLoad([object]$Context, [string]$SublotPrefix) {
    $journal = $Context.Journal
    $connection = $Context.Connection
    $riot = $Context.Riot
    $onboard = $Context.Onboard
    $simulator = $Context.Simulator

    $demandGuid = [guid]::NewGuid()
    $demandId = $demandGuid.ToString('D')
    $sublot = "$SublotPrefix-$($Context.RunId)"

    $journal.Note("Publishing demand $($demandGuid.ToString('N')) (sublot $sublot).")
    $null = $Context.MesIngest.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
        sublot = $sublot; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
    })
    $null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { Get-G3Scalar $connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'" } `
        -Until { param($v) $v -eq 'AwaitingPickupArrival' }
    $intent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
        -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection `
                -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'"
            if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
        } -Until { param($v) $null -ne $v }

    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Context.PickupStationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })

    $null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
        -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
        -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
    $onboard.SetSublot($sublot)
    $null = Wait-L2Condition -Description 'the manual submit button became enabled' `
        -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
        -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
    $onboard.Submit()

    $attemptId = Wait-L2Condition -Description 'the server issued the load command' `
        -Journal $journal -Criterion 'load-attempt' -TimeoutSeconds 180 `
        -Probe { Get-G3Scalar $connection "SELECT SlotOperationAttemptId AS Value FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'" } `
        -Until { param($v) $v }
    $targetSlots = @([string](Get-G3Scalar $connection "SELECT TargetSlotsJson AS Value FROM StationOperations WHERE SlotOperationAttemptId = '$attemptId'") |
        ConvertFrom-Json | ForEach-Object { [int]$_ })
    $waiting = Wait-L2Condition -Description 'the onboard is waiting for the operator' `
        -Journal $journal -Criterion 'load-waiting-operator' -TimeoutSeconds 120 `
        -Probe { @((Get-G3Progress $connection $attemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })[0] } `
        -Until { param($v) $null -ne $v }
    $slot = [int]$waiting.Active[0]

    & $Context.StopComponent 'onboard-hmi'
    $journal.Note("While the onboard is down the operator closes slot $slot without the basket.")
    $null = $simulator.Command('Post', "slots/$slot/close-door", @{})
    $null = Wait-L2Condition -Description "slot $slot reads closed, empty, locked and reset" `
        -Journal $journal -Criterion 'slot-closed-empty' -TimeoutSeconds 30 `
        -Probe { Get-G3SlotState $simulator $slot } -Until { param($v) $v -eq 'CLOSED/EMPTY/1/0' }
    $null = & $Context.RestartOnboard

    $first = Wait-L2Condition -Description 'the restarted onboard settled the interrupted load and the server acknowledged it' `
        -Journal $journal -Criterion 'unknown-result' -TimeoutSeconds 120 `
        -Probe { @((Get-G3Inbound $connection 'OperationResult') | Where-Object { [string]$_.Payload.slotOperationAttemptId -eq $attemptId })[0] } `
        -Until { param($v) $null -ne $v -and $null -ne $v.ResponseLine }
    $null = Wait-L2Condition -Description 'the journey blocked on the unknown load result' `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 60 `
        -Probe { Get-G3Scalar $connection "SELECT Stage AS Value FROM JourneyRuntimes WHERE DemandId = '$demandId'" } `
        -Until { param($v) $v -eq 'Blocked' }
    $journal.Note("Load $attemptId on slot $slot ended $($first.Payload.overallOutcome); journey Blocked.")

    return [pscustomobject]@{
        DemandId    = $demandId
        AttemptId   = $attemptId
        Slot        = $slot
        TargetSlots = $targetSlots
        First       = $first
    }
}

<#
结算之后车辆真的放出来了（control-server#128，按调度会话 2026-09-18 的要求补；缺口本身是 control-server#131）：
这条需求的 TO_PICKUP 单 `VehicleOccupancyReleasedAt` 有值，而且同一台车在 60 秒内接了下一单——下一单的旅程到
`AwaitingPickupArrival`，不是建了旅程却 `Blocked / VEHICLE_OCCUPANCY_CONFLICT`。写法同 `real-onboard-load-door-closed-empty-reopens`
的 `L2-DC-10`、`L2-DC-12`，两层合成一条判据。

发下一单会在假 RIoT 上多出一张单，所以调用方把它放在所有数 RIoT 单的判据之后。不用 Wait-L2Condition：派不出去
时它会抛超时，而这里要把「派不出去」连同原因记成一条判据。
#>
function Add-G3VehicleReleasedForNextDemand([object]$Context, [string]$Id, [string]$Description, [string]$DemandId, [string]$SublotPrefix) {
    $connection = $Context.Connection
    $journal = $Context.Journal
    $occupancy = Get-G3Scalar $connection "SELECT VehicleOccupancyReleasedAt AS Value FROM OrderIntents WHERE DemandId = '$DemandId' AND Purpose = 'TO_PICKUP'"

    $nextGuid = [guid]::NewGuid()
    $nextDemandId = $nextGuid.ToString('D')
    $journal.Note("Publishing the next demand $($nextGuid.ToString('N')) to see whether the vehicle takes it.")
    $null = $Context.MesIngest.Command('Put', "demands/$($nextGuid.ToString('N'))", @{
        sublot = "$SublotPrefix-$($Context.RunId)-NEXT"; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
    })
    # 「车放出来了」不能停在「下一单到了 AwaitingPickupArrival」。那个阶段是**受理那一次提交**写下的，而占车
    # 实读出来的顺序（control-server#203 的独立审查指出，作者逐行核过；**此前这段注释写反了**）：
    #
    #   1. `DispatchRoundRunner.cs:499` → `WireToGateOrchestration.AcceptAndDispatchToPickupAsync`：
    #      先 `AcceptJourneyAsync` 写 JourneyRuntimes（阶段 `AwaitingPickupArrival`），**紧接着**
    #      `ReconcileOrCreateAsync` 建 RIoT 单并把 TO_PICKUP 意图置 `CONFIRMED`；
    #   2. `DispatchRoundRunner.cs:548` `TryClaimVehicleOccupancyAsync` 在**这之后**，失败才
    #      `Block(...)`（`:712` 把 Stage 设为 `Blocked` 并写 `VEHICLE_OCCUPANCY_CONFLICT`）；
    #   3. `DispatchRoundRunner.cs:560` 另一条分支：建单没到 `Confirmed` 时只 `SetBlockReason(...)`，
    #      **不改 Stage**。
    #
    # 所以 `CONFIRMED` 对占车冲突**判别力为零**——冲突发生时它早就是 CONFIRMED 了。这条判据真正抓住的是
    # 第 3 条分支：一个「释放了车、却没能给下一单建成／确认 RIoT 单」的服务端，落库是
    # `AwaitingPickupArrival` + `PICKUP_DISPATCH_NOT_CONFIRMED`——**旧写法只看阶段与 AgvId，会绿**，
    # 而 `-not (Test-G3Present $next.BlockReasonCode)` 这一项会红。
    #
    # `CONFIRMED` 仍然留着，但要知道它管的是别的事：它挡的是「停在第一次落库上就收工」，即在建单结果落库
    # 之前判据已经通过。它不是用来区分占车冲突的。
    #
    # Blocked 也算「等到了」，理由与条目 7 那处相同：让红落在判据表里、带着原因码，而不是熬满 60 秒只留下
    # 一句「没派出」，把「被占着」和「还没轮到」混成同一种读数。
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    $next = $null
    $intentStatus = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Stage, BlockReasonCode, AgvId FROM JourneyRuntimes WHERE DemandId = '$nextDemandId'"
        $next = if ($rows.Count -ge 1) { $rows[0] } else { $null }
        $intentStatus = Get-G3Scalar $connection "SELECT Status AS Value FROM OrderIntents WHERE DemandId = '$nextDemandId' AND Purpose = 'TO_PICKUP'"
        $journal.Observe(
            "$Id-next-demand",
            $(if ($next) { "$($next.Stage)/$($next.BlockReasonCode)/TO_PICKUP=$intentStatus" } else { $null }),
            $null)
        if ($next -and ([string]$next.Stage -eq 'Blocked' -or
                ([string]$next.Stage -eq 'AwaitingPickupArrival' -and [string]$intentStatus -eq 'CONFIRMED'))) {
            break
        }
        Start-Sleep -Milliseconds 500
    }
    # 列名实读自 `JourneyBacklogRow`：这张表没有 Status 列，「受理了没有」写在 AcceptedAt 上。
    # 原来 L2-DC-12 那份用的是 `SELECT *` 再按名字过滤属性——那不是随手写的，是因为它不假设列名；
    # 我把它「改进」成显式列名时照搬了一个不存在的 Status，三次真装置运行白跑在
    # `SQLite Error 1: 'no such column: Status'` 上。
    $backlog = @(Invoke-L2Query -Connection $connection -Sql "SELECT ReasonCode, AcceptedAt FROM JourneyBacklog WHERE DemandId = '$nextDemandId'")
    $backlogText = if ($backlog.Count -ge 1) {
        "积压 $($backlog[0].ReasonCode)，受理时间 $(if (Test-G3Present $backlog[0].AcceptedAt) { $backlog[0].AcceptedAt } else { '(无)' })"
    } else { '无积压行' }
    $nextText = if ($next) {
        "下一单 $($next.Stage)/$($next.BlockReasonCode) TO_PICKUP=$intentStatus on $($next.AgvId)"
    } else { "60 s 内下一单没有建旅程（$backlogText）" }
    $Context.Assertions.Add(
        $Id, $Description,
        ((Test-G3Present $occupancy) -and $null -ne $next -and [string]$next.Stage -eq 'AwaitingPickupArrival' -and
            [string]$next.AgvId -eq [string]$Context.AgvId -and [string]$intentStatus -eq 'CONFIRMED' -and
            -not (Test-G3Present $next.BlockReasonCode)),
        "TO_PICKUP 占用已释放 / 下一单 AwaitingPickupArrival on $($Context.AgvId)，TO_PICKUP 意图 CONFIRMED，没有停摆原因码",
        "VehicleOccupancyReleasedAt='$occupancy' / $nextText")
}
