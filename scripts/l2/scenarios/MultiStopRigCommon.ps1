#Requires -Version 7

<#
批次7-15（control-server#218）两条多需求真装置场景共用的驱动：g3-multi-stop-plan 与 real-onboard-mixed-side-one-stop。由各场景
点号引入，不是场景本身，没有 setup 文件，编排器不会单独运行它（与 CargoHoldingCommon.ps1、G3RecoveryCommon.ps1 同一种安排）。
它自己也点号引入 CargoHoldingCommon.ps1，复用批次7-07 的发需求、按停靠开车、读装货阶段，那个文件一字不改。

为什么不复用 L2TaskTypeJourney.psm1 的 Invoke-L2TaskTypeJourney：它按「一条需求、一个取货站、一个卸货站」写死（第二站的意图
多于一条就抛），而这里一趟旅程挂两到三条需求、同一站装两条、同一站卸三条。

**多需求时服务端怎样逐条做**（实读 fp/v2-impl@8ee99549，行号会漂，按名字查）：
- 取货停靠：到站发一条录入请求，列出本站全部未装的子批；操作员扫哪一条就装哪一条（JourneyRuntimeEngine.RevalidateEnteredSublotAsync
  在未装的需求里按子批匹配）。那一条的装货结果提交后，服务端再发一版清单和一条新的录入请求、阶段回到 AwaitingSublot，才等下一条
  扫码。所以同一站两条装货命令不会同时在路上，跨需求的先后就是扫码的先后。
- 卸货停靠：一次只给一条需求下卸货命令（JourneyStopCursor.NextToUnloadAtCurrentStop：本站第一条 LOADED 的需求，按归属加入的先后
  AddedAt、再按 DemandId），那一条提交且需求 Succeeded 之后才下一条。卸货不录入。
- 阶段（JourneyRuntimes.Stage）属于整趟旅程；追加进来的需求没有自己的旅程行，按 JourneyDemands 找旅程。

**探针不取闭包**，理由与 CargoHoldingCommon.ps1 一样：不取闭包的脚本块记得定义它的作用域，Wait-L2Condition 调它时这些函数还在
栈上，参数读得到；在函数体里 .GetNewClosure() 反而只收那一个作用域，参数读成空（scripts/l2/L2ClosureCapture.psm1）。
#>

Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'CargoHoldingCommon.ps1')

# 这趟旅程的阶段：按旅程号读，不按需求号——追加进来的需求没有旅程行。
function Get-L2JourneyStage([object]$Connection, [string]$JourneyId) {
    $rows = Invoke-L2Query -Connection $Connection -Sql "SELECT Stage FROM JourneyRuntimes WHERE JourneyId = '$JourneyId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

# 一条需求某一类仓位操作（Load／Unload）的那一行；没有返回 $null。
function Get-L2DemandOperation([object]$Connection, [string]$DemandId, [string]$OperationType) {
    $rows = Invoke-L2Query -Connection $Connection -Sql (
        "SELECT SlotOperationAttemptId, DemandId, OperationType, Status, TargetSlotsJson, CreatedAt, CommittedAt " +
        "FROM StationOperations WHERE DemandId = '$DemandId' AND OperationType = '$OperationType'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-L2FirstWaitingOperator([object]$Connection, [string]$AttemptId) {
    $waiting = @((Get-L2RealProgress $Connection $AttemptId) | Where-Object { $_.Phase -eq 'WAITING_OPERATOR' })
    if ($waiting.Count -eq 0) { return $null }
    return $waiting[0]
}

<#
在当前停靠上装一条需求：等服务端要子批、车载端能录入，经 UIA 录入这条需求的子批并提交；等这条需求的装货命令、车载端在那一仓
等操作员（WAITING_OPERATOR 只在锁反馈稳定、开锁输出复位之后才发，所以门真开着），把货放进去、关门；等这笔装货提交。

一次一仓：这两条场景的每条需求都是一个花篮。命令要开的不止一仓就抛，而不是让后面的判据超时。

返回 AttemptId、TargetSlots、OpenedSlot、CommandAt（服务端建这笔操作的时刻）、UnlockedSeenAt（场景看到 WAITING_OPERATOR 的
时刻）、CommittedAt（场景看到提交的时刻）。
#>
function Invoke-L2RigLoad([object]$Context, [string]$JourneyId, [hashtable]$Demand, [int]$TimeoutSeconds = 180,
    [Nullable[DateTimeOffset]]$WorklistAfter = $null) {
    $journal = $Context.Journal
    $connection = $Context.Connection
    $onboard = $Context.Onboard
    $simulator = $Context.Simulator
    $demandId = $Demand.Id

    # 同一站的第二条：前一条装完之后服务端才发新一版清单与录入请求。旧的那版清单同样列着这条需求、也早已被确认，
    # 阶段此刻也是 AwaitingSublot，所以「阶段对了、能录入」不足以说明车载端已经换上新的那一版——在旧版上录入，
    # 车载端或服务端会按修订号拒掉它。等一版在 $WorklistAfter 之后建出、列着这条需求、已被确认的清单。
    if ($null -ne $WorklistAfter) {
        # PowerShell unwraps a Nullable on assignment, so there is no .Value to read.
        $after = [DateTimeOffset]$WorklistAfter
        $null = Wait-L2Condition -Description "the onboard acknowledged the worklist issued after the previous load (for $($Demand.Label))" `
            -Journal $journal -Criterion "worklist-after-previous-load-$($Demand.Label)" -TimeoutSeconds 60 `
            -Probe {
                $fresh = @((Get-L2JourneyWireSnapshots $connection @($demandId)) | Where-Object {
                        $_.Type -eq 'CurrentStopWorklistSnapshot' -and $_.At -gt $after -and $_.Acknowledged })
                if ($fresh.Count -eq 0) { $null } else { $fresh[0].Revision }
            } -Until { param($v) $null -ne $v }
    }

    $null = Wait-L2Condition -Description "journey $JourneyId asks for a sublot and the HMI accepts entry (for $($Demand.Label))" `
        -Journal $journal -Criterion "entry-open-$($Demand.Label)" -TimeoutSeconds $TimeoutSeconds `
        -Probe { "$(Get-L2JourneyStage $connection $JourneyId)/$($onboard.CanSubmit())" } `
        -Until { param($v) $v -eq 'AwaitingSublot/True' }
    $journal.Note("Typing sublot $($Demand.Sublot) (demand $($Demand.Label)) into ScanTextBox through UI Automation.")
    $onboard.SetSublot($Demand.Sublot)
    $null = Wait-L2Condition -Description 'the manual submit button became enabled' `
        -Journal $journal -Criterion "submit-ready-$($Demand.Label)" -TimeoutSeconds 30 `
        -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
    $onboard.Submit()
    $journal.Note("Manual submit invoked for $($Demand.Label).")

    $operation = Wait-L2Condition -Description "the server issued the load command for $($Demand.Label)" `
        -Journal $journal -Criterion "load-attempt-$($Demand.Label)" -TimeoutSeconds $TimeoutSeconds `
        -Probe { Get-L2DemandOperation $connection $demandId 'Load' } -Until { param($v) $null -ne $v }
    $attemptId = [string]$operation.SlotOperationAttemptId
    $targetSlots = @([string]$operation.TargetSlotsJson | ConvertFrom-Json | ForEach-Object { [int]$_ })
    $waiting = Wait-L2Condition -Description "the onboard waits for the operator on $($Demand.Label)'s load slot" `
        -Journal $journal -Criterion "load-waiting-operator-$($Demand.Label)" -TimeoutSeconds 120 `
        -Probe { Get-L2FirstWaitingOperator $connection $attemptId } -Until { param($v) $null -ne $v }
    if ($waiting.Active.Count -ne 1) {
        throw "The onboard reported WAITING_OPERATOR on $($waiting.Active.Count) slots at once for $($Demand.Label)'s load; these scenarios drive one slot."
    }
    $unlockedSeenAt = [DateTimeOffset]::UtcNow
    $slot = [int]$waiting.Active[0]
    $journal.Note("Load $attemptId ($($Demand.Label)) targets slots $(Format-L2RealSlots $targetSlots); slot $slot is open, placing cargo and closing the door.")
    $null = $simulator.Command('Put', "slots/$slot/cargo", @{ state = 'OCCUPIED' })
    $null = $simulator.Command('Post', "slots/$slot/close-door", @{})
    $committed = Wait-L2Condition -Description "$($Demand.Label)'s load committed" `
        -Journal $journal -Criterion "load-committed-$($Demand.Label)" -TimeoutSeconds 120 `
        -Probe { [string](Get-L2DemandOperation $connection $demandId 'Load').Status } -Until { param($v) $v -eq 'Committed' }
    # 提交时刻取服务端记的那一个，不取场景看到它的时刻：下一版清单可能在场景下一次轮询之前就已建出，拿「看到」的时刻去比
    # 会把它排除在外、等到超时。同一台机器同一只钟。
    $row = Get-L2DemandOperation $connection $demandId 'Load'
    return [pscustomobject]@{
        Label          = $Demand.Label
        AttemptId      = $attemptId
        TargetSlots    = $targetSlots
        OpenedSlot     = $slot
        CommandAt      = ConvertTo-L2RealInstant $operation.CreatedAt
        UnlockedSeenAt = $unlockedSeenAt
        CommittedAt    = ConvertTo-L2RealInstant $row.CommittedAt
        Status         = $committed
    }
}

<#
在卸货停靠上做服务端下发的下一笔卸货：等一笔还没做过的卸货命令出现（本旅程的需求、不在 $Done 里），车载端在那一仓等操作员，
把货取出、关门，等它提交。哪一条需求先卸由服务端定，场景只照做并记下先后。返回同 Invoke-L2RigLoad，另带 DemandId。
#>
function Invoke-L2RigUnloadNext([object]$Context, [string]$JourneyId, [string[]]$Done, [int]$TimeoutSeconds = 180) {
    $journal = $Context.Journal
    $connection = $Context.Connection
    $simulator = $Context.Simulator
    $excluded = if (@($Done).Count -eq 0) { "''" } else { (@($Done) | ForEach-Object { "'$_'" }) -join ', ' }
    $sql = "SELECT s.SlotOperationAttemptId, s.DemandId, s.TargetSlotsJson, s.CreatedAt FROM StationOperations s " +
        "JOIN JourneyDemands d ON d.DemandId = s.DemandId WHERE d.JourneyId = '$JourneyId' AND s.OperationType = 'Unload' " +
        "AND s.SlotOperationAttemptId NOT IN ($excluded) ORDER BY s.CreatedAt LIMIT 1"
    $operation = Wait-L2Condition -Description "the server issued the next unload command (after $(@($Done).Count))" `
        -Journal $journal -Criterion "unload-attempt-$(@($Done).Count + 1)" -TimeoutSeconds $TimeoutSeconds `
        -Probe { $rows = Invoke-L2Query -Connection $connection -Sql $sql; if ($rows.Count -eq 0) { $null } else { $rows[0] } } `
        -Until { param($v) $null -ne $v }
    $attemptId = [string]$operation.SlotOperationAttemptId
    $demandId = [string]$operation.DemandId
    $targetSlots = @([string]$operation.TargetSlotsJson | ConvertFrom-Json | ForEach-Object { [int]$_ })
    $waiting = Wait-L2Condition -Description "the onboard waits for the operator on the unload slot of $demandId" `
        -Journal $journal -Criterion "unload-waiting-operator-$(@($Done).Count + 1)" -TimeoutSeconds 120 `
        -Probe { Get-L2FirstWaitingOperator $connection $attemptId } -Until { param($v) $null -ne $v }
    if ($waiting.Active.Count -ne 1) {
        throw "The onboard reported WAITING_OPERATOR on $($waiting.Active.Count) slots at once for an unload; these scenarios drive one slot."
    }
    $unlockedSeenAt = [DateTimeOffset]::UtcNow
    $slot = [int]$waiting.Active[0]
    $journal.Note("Unload $attemptId (demand $demandId) targets slots $(Format-L2RealSlots $targetSlots); slot $slot is open, taking the cargo out and closing the door.")
    $null = $simulator.Command('Put', "slots/$slot/cargo", @{ state = 'EMPTY' })
    $null = $simulator.Command('Post', "slots/$slot/close-door", @{})
    $committed = Wait-L2Condition -Description "the unload of $demandId committed" `
        -Journal $journal -Criterion "unload-committed-$(@($Done).Count + 1)" -TimeoutSeconds 120 `
        -Probe { [string](Get-L2DemandOperation $connection $demandId 'Unload').Status } -Until { param($v) $v -eq 'Committed' }
    return [pscustomobject]@{
        DemandId       = $demandId
        AttemptId      = $attemptId
        TargetSlots    = $targetSlots
        OpenedSlot     = $slot
        CommandAt      = ConvertTo-L2RealInstant $operation.CreatedAt
        UnlockedSeenAt = $unlockedSeenAt
        SeenCommittedAt = [DateTimeOffset]::UtcNow
        Status         = $committed
    }
}

# 这趟旅程的全部归属行，按加入先后。
function Get-L2JourneyMembers([object]$Connection, [string]$JourneyId) {
    return Invoke-L2Query -Connection $Connection -Sql (
        "SELECT DemandId, Status, TargetSlotsJson, PickupStopId, UnloadStopId, AddedAt FROM JourneyDemands " +
        "WHERE JourneyId = '$JourneyId' AND RemovedAt IS NULL ORDER BY AddedAt, DemandId")
}

# 这趟旅程的停靠，按序位。
function Get-L2JourneyStops([object]$Connection, [string]$JourneyId) {
    return Invoke-L2Query -Connection $Connection -Sql (
        "SELECT StopId, Sequence, StopRole, StationId, StationRiotId, Status FROM JourneyStops " +
        "WHERE JourneyId = '$JourneyId' ORDER BY Sequence")
}
