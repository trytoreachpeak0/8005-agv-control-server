#Requires -Version 7

<#
真车载端的正常装载：与 `normal-load` 同一条业务链路，但车载端是出厂的那个 WPF，IO 是真 Modbus。

这是落地顺序第 5 步的产物。和 `normal-load` 的差别只有两处，而这两处正是它存在的理由：

- **条码由 UI Automation 写进 `ScanTextBox` 再点「手动提交」**，不是合成对端按策略自动应答；
- **放货取货走 slots-simulator 的物理面**，开锁由车载端自己写 Modbus DO 触发，测试脚本碰不到锁。

因此这里能证明合成对端证明不了的东西：车载端的操作员通路、本地 journal、真实 IO 闭环，以及
`ReadOnboardFactsAsync` 面对的是一个会自己判新鲜度的对端。

断言仍然只从服务端 SQLite 与模拟器 `/snapshot` 读。UI 只用来驱动——从控件读到的唯一一件事是
「现在允不允许录入」，那是能不能打字的前提，不是业务事实。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$simulator = $Context.Simulator
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

function Get-Stage {
    $rows = Get-L2Journey -Connection $connection -DemandId $demandId
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Slot([int]$slotNo) {
    return $simulator.Snapshot().slots | Where-Object { [int]$_.slotNo -eq $slotNo }
}

function Get-AttemptId([string]$operationType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT SlotOperationAttemptId FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = '$operationType'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].SlotOperationAttemptId
}

# 车载端每走一个相位就发一条 OperationProgress，服务端原样存进 ProtocolInbox。这是唯一一处能
# 看到「车载端现在到哪一步了」的地方，而且是服务端自己收到的，不是从界面上读的。
function Get-Progress([string]$attemptId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT RequestJson FROM ProtocolInbox WHERE MessageType = 'OperationProgress' ORDER BY ReceivedAt"
    $matched = foreach ($row in $rows) {
        $payload = ([string]$row.RequestJson | ConvertFrom-Json).payload
        if ($payload.slotOperationAttemptId -eq $attemptId) { $payload }
    }
    return @($matched)
}

<#
一次装卸的完整驱动：等车载端把门开到「在等操作员」为止，摆一次货，再关门。

**这里绝不能一看到门开就动手。**第一版就是那么写的，结果开锁到关门只隔了 124 ms，而车载端要求
锁反馈稳定 `feedbackStableMs`(300ms) 才认，于是它从来没看见过一个稳定的「已开锁」状态，报回
一份不完美的 `OperationResult`，服务端如实判 `LOAD_RESULT_REQUIRES_RECOVERY` 并停摆。那不是缺陷，
是脚本比人快——真操作员放一篮货要几秒钟。

可等的判据是车载端自己发的 `WAITING_OPERATOR`：它在锁反馈稳定、开锁输出复位之后才发，收到它就
说明车载端确实观测到了门开着的那一段。
#>
function Invoke-SlotOperation([string]$operationType, [string]$cargoState) {
    $attemptId = Wait-L2Condition -Description "the server issued the $operationType command" `
        -Journal $journal -Criterion "$operationType-attempt" -TimeoutSeconds 180 `
        -Probe { Get-AttemptId $operationType } -Until { param($v) $v }

    $unlocking = Wait-L2Condition -Description "the onboard started unlocking for the $operationType" `
        -Journal $journal -Criterion "$operationType-unlocking" -TimeoutSeconds 120 `
        -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'UNLOCKING' })[0] } `
        -Until { param($v) $v }
    # 执行器是逐仓循环的：多仓时会走 UNLOCKING[1] → WAITING_OPERATOR → UNLOCKING[2] → …
    # 这个函数只驱动一仓，所以多仓要当场报错，而不是把第一仓装完然后挂在后面某条判据上。
    $slots = @($unlocking.activeUnlockSlots)
    if ($slots.Count -ne 1) {
        throw ("This scenario drives one slot per operation; the $operationType command targets " +
            "$($slots.Count) ($($slots -join ', ')).")
    }
    $slotNo = [int]$slots[0]

    $null = Wait-L2Condition -Description "the onboard is waiting for the operator on slot $slotNo" `
        -Journal $journal -Criterion "$operationType-waiting-operator" -TimeoutSeconds 120 `
        -Probe { @(Get-Progress $attemptId | Where-Object { $_.phase -eq 'WAITING_OPERATOR' }).Count } `
        -Until { param($v) $v -ge 1 }

    $doorState = [string](Get-Slot $slotNo).doorState
    $journal.Note("Onboard is waiting on slot $slotNo (door $doorState); setting cargo to $cargoState.")
    $null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = $cargoState })
    $null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})

    return [pscustomobject]@{ AttemptId = $attemptId; SlotNo = $slotNo; DoorStateWhenWaiting = $doorState }
}

# --- 1. 需求出现在 MesIngest 目录里，服务端受理并派车去取货点 -------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot) to the fake MesIngest catalog.")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$intentStatus = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-UpperId -purpose 'TO_PICKUP'; if ($row) { [string]$row.Status } else { $null } } `
    -Until { param($v) $v -eq 'CONFIRMED' }
$pickupIntent = Get-UpperId -purpose 'TO_PICKUP'
$assertions.Add(
    'L2-RO-01', '真车载端在场时需求被受理并建出 TO_PICKUP 单',
    ($intentStatus -eq 'CONFIRMED'),
    'CONFIRMED', $intentStatus)

# --- 2. 车开到取货点 -------------------------------------------------------------------------------

$journal.Note('Vehicle departs for the pickup station.')
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $pickupIntent.OrderId
})

$journal.Note('Vehicle arrives at the pickup station and comes to rest.')
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $Context.PickupStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 5 })

# --- 3. 服务端请求录入 sublot，UIA 代替操作员扫码 ---------------------------------------------------

# 这是第 5 步的核心：等的是 ScanTextBox 变为可用，而它绑的是 WireToGateBusinessService.CanSubmitSublot
# （App.xaml.cs:146），不是规则网关连上——WIRE_TO_GATE 模式下规则网关是 DisabledRuleGateway，
# 那个 18080 从头到尾都没有人连。
$null = Wait-L2Condition -Description 'the onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }

$journal.Note("Typing sublot $sublot into ScanTextBox through UI Automation.")
$onboard.SetSublot($sublot)

# 「手动提交」按钮的 CanExecute 是 `CanSubmit && ScanText 非空`（MainViewModel），文本一变
# ScanText 的 setter 就 RaiseCanExecuteChanged，所以这是一条真判据而不是等 UI 反应过来。
$null = Wait-L2Condition -Description 'the manual submit button became enabled' `
    -Journal $journal -Criterion 'onboard-submit-ready' -TimeoutSeconds 30 `
    -Probe { $onboard.SubmitReady() } -Until { param($v) $v }
$onboard.Submit()
$journal.Note('Manual submit invoked.')

# 提交这件事从服务端自己收到的那条消息上确认，不从界面上确认。
$inboxJson = Wait-L2Condition -Description 'the server received SublotSubmitted from the real onboard' `
    -Journal $journal -Criterion 'sublot-submitted' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT RequestJson FROM ProtocolInbox WHERE MessageType = 'SublotSubmitted'"
        if ($rows.Count -eq 0) { return $null }
        return [string]$rows[0].RequestJson
    } `
    -Until { param($v) $v -like "*$sublot*" }
$submitted = ($inboxJson | ConvertFrom-Json).payload
$assertions.Add(
    'L2-RO-02', 'UIA 录入的 sublot 经真车载端以 KEYBOARD 方式提交到服务端',
    ($submitted.sublot -eq $sublot -and $submitted.entryMethod -eq 'KEYBOARD' -and
        $submitted.operator.operatorId -eq 'L2-OPERATOR'),
    "$sublot / KEYBOARD / L2-OPERATOR",
    "$($submitted.sublot) / $($submitted.entryMethod) / $($submitted.operator.operatorId)")

# --- 4. 装载：车载端开锁，测试放货关门，车载端自己判完成 ---------------------------------------------

# 模拟器不提供开锁接口，这一步只能等车载端真的写了 Modbus DO——这条边界是模拟器刻意设的，
# 它保证被验证的是 HMI→IO 那条通路，不是脚本自己摆出来的状态。
$load = Invoke-SlotOperation -operationType 'Load' -cargoState 'OCCUPIED'
$loadSlot = $load.SlotNo
$assertions.Add(
    'L2-RO-03', '车载端上报在等操作员时，它说要开的那个仓门确实是开的',
    ($load.DoorStateWhenWaiting -eq 'OPEN'),
    "slot $loadSlot OPEN", "slot $loadSlot $($load.DoorStateWhenWaiting)")

# 车载端的完成条件是「锁上了，且货物事实与期望一致」（WireToGateSlotOperationExecutor），两条都
# 只能从 DI 读到，所以这里等的是模拟器的物理状态，而不是车载端说它好了。
$loadPhysical = Wait-L2Condition -Description 'the loaded slot is closed, locked and occupied' `
    -Journal $journal -Criterion 'load-slot-physical' -TimeoutSeconds 60 `
    -Probe {
        $slot = Get-Slot $loadSlot
        "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
    } `
    -Until { param($v) $v -eq 'CLOSED/OCCUPIED/1/0' }
$assertions.Add(
    'L2-RO-04', '装载走的是真 Modbus 闭环：车载端开锁、放货、关门、锁反馈回到 1、开锁输出复位',
    ($loadPhysical -eq 'CLOSED/OCCUPIED/1/0'),
    'CLOSED/OCCUPIED/1/0', $loadPhysical)

$stage = Wait-L2Condition -Description 'the load committed and the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }

$loadRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status, SublotId FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'"
$assertions.Add(
    'L2-RO-05', '装载操作提交（Committed），且记的是 UIA 录进去的那个 sublot',
    ($loadRows.Count -eq 1 -and [string]$loadRows[0].Status -eq 'Committed' -and
        [string]$loadRows[0].SublotId -eq $sublot),
    "Committed / $sublot",
    $(if ($loadRows.Count -eq 1) { "$([string]$loadRows[0].Status) / $([string]$loadRows[0].SublotId)" }
      else { '(no load row)' }))

$gateIntent = Get-UpperId -purpose 'TO_GATE'
$assertions.Add(
    'L2-RO-06', '出发前安全检查通过后才建 TO_GATE 单',
    ($null -ne $gateIntent -and $gateIntent.Status -eq 'CONFIRMED'),
    'CONFIRMED', $(if ($gateIntent) { $gateIntent.Status } else { '(no intent)' }))

# --- 5. 车开到关卡并卸载 ---------------------------------------------------------------------------

$journal.Note('Vehicle departs for the gate.')
$null = $riot.Command('Put', "orders/$($gateIntent.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $gateIntent.OrderId
})
$journal.Note('Vehicle arrives at the gate and comes to rest.')
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey       = $Context.VehicleKey
    procState        = 'IDLE'
    movementState    = 'MT_FINISHED'
    speed            = 0
    currentPosition  = $Context.GateStationRiotId
    processingOrder  = $false
    clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($gateIntent.UpperId)", @{ orderState = 5 })

$unload = Invoke-SlotOperation -operationType 'Unload' -cargoState 'EMPTY'
$unloadSlot = $unload.SlotNo
$assertions.Add(
    'L2-RO-07', '关卡卸的是装货时用的那个仓位',
    ($unloadSlot -eq $loadSlot),
    $loadSlot, $unloadSlot)

$unloadPhysical = Wait-L2Condition -Description 'the unloaded slot is closed, locked and empty' `
    -Journal $journal -Criterion 'unload-slot-physical' -TimeoutSeconds 60 `
    -Probe {
        $slot = Get-Slot $unloadSlot
        "$($slot.doorState)/$($slot.cargoState)/$($slot.lockFeedbackRaw)/$($slot.unlockOutputRaw)"
    } `
    -Until { param($v) $v -eq 'CLOSED/EMPTY/1/0' }
$assertions.Add(
    'L2-RO-08', '卸载后仓位回到空、门关、锁上',
    ($unloadPhysical -eq 'CLOSED/EMPTY/1/0'),
    'CLOSED/EMPTY/1/0', $unloadPhysical)

# --- 6. 终态 ---------------------------------------------------------------------------------------

$stage = Wait-L2Condition -Description 'the journey completed at the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'Completed' }
$assertions.Add('L2-RO-09', 'journey 走到 Completed', ($stage -eq 'Completed'), 'Completed', $stage)

$unloadRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Unload'"
$assertions.Add(
    'L2-RO-10', '卸载操作提交（Committed）',
    ($unloadRows.Count -eq 1 -and [string]$unloadRows[0].Status -eq 'Committed'),
    'Committed', $(if ($unloadRows.Count -eq 1) { [string]$unloadRows[0].Status } else { '(no unload row)' }))

$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-RO-11', '需求终态为 Succeeded',
    ($demandRows.Count -eq 1 -and [string]$demandRows[0].Status -eq 'Succeeded'),
    'Succeeded', $(if ($demandRows.Count -eq 1) { [string]$demandRows[0].Status } else { '(no demand row)' }))

$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-RO-12', '全程只建了两条 RIoT 单（取货一条、关卡一条）',
    ($riotOrders.Count -eq 2),
    2, $riotOrders.Count)

$journal.Note('Scenario finished.')
