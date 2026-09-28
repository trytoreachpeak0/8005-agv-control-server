#Requires -Version 7

<#
control-server#375 在真车载端下的那一格：取货腿建单前的对账读没读到，之后补建，只建一张。

修之前，建单前那一次读只要回的是非精确的 Unknown（SDK 超时、503），意图就被标成 RESULT_UNKNOWN，
此后 RIoT 明确答「没有这张单」也永不建单，旅程停在取货腿，现场只能改库。修之后，从没发出过的意图在 RIoT
答「没有」时照常建；而补建发生在离站核验（这里是受理时的派车准入）之后若干轮，所以补建前要重看一遍车况，
那一组判据里有车载端报上来的离站摘要——**这正是合成对端证明不了的部分**：合成对端永远报安全，
真车载端的会话与安全摘要是它自己按 IO 判出来的。

注入只打一处：假 RIoT 的 `PUT /faults/absent-order-reads`，让接下来一次「按 upperId 读一张不存在的单」
回 503。它不碰车辆读取、站点目录、订单列表，也不按时间生效，所以打中的是哪一次读由假 RIoT 自己记下
（`absentOrderReadFaults.failedUpperIds`），判据先核它打中的就是取货腿那一张。

判据只从服务端 SQLite 与假 RIoT `/snapshot` 读。
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
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

function Get-PickupIntent {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql ("SELECT MovementLegId, UpperId, OrderId, Status, CreateAttemptCount FROM OrderIntents " +
              "WHERE DemandId = '$demandId' AND Purpose = 'TO_PICKUP'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-AuditChain([string]$movementLegId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql ("SELECT Sequence, Phase, Outcome, ReceiptClassification, FailureCategory, AttemptId FROM RiotDispatchAuditEvents " +
              "WHERE MovementLegId = '$movementLegId' ORDER BY Sequence")
    return @(foreach ($row in $rows) {
        "$([string]$row.Phase)/$([string]$row.Outcome)/$([string]$row.ReceiptClassification)/$([string]$row.FailureCategory)"
    })
}

function Get-Stage {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

# --- 1. 只让下一次「读一张不存在的单」失败，然后发需求 -------------------------------------------------

$armed = $riot.Command('Put', 'faults/absent-order-reads', @{ count = 1 })
$journal.Note("Armed one failing read of an absent order: remaining $($armed.body.absentOrderReadFaults.remaining).")

$journal.Note("Publishing demand $demandIdWire (sublot $sublot) to the fake MesIngest catalog.")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$pickup = Wait-L2Condition -Description 'the demand was accepted and its TO_PICKUP intent written' `
    -Journal $journal -Criterion 'to-pickup-intent-written' -TimeoutSeconds 120 `
    -Probe { Get-PickupIntent } -Until { param($v) $v }

# --- 2. 注入打中的就是这张单建单前的那一次读 --------------------------------------------------------------

$faults = $riot.Snapshot().body.absentOrderReadFaults
$failed = @($faults.failedUpperIds)
$journal.Note("Absent-order read faults now: remaining $($faults.remaining), failed [$($failed -join ', ')].")
$assertions.Add(
    'L2-UR-01', '注入只打中一次，打中的是取货腿那张单的读',
    ($failed.Count -eq 1 -and $failed[0] -eq [string]$pickup.UpperId -and [int]$faults.remaining -eq 0),
    "[$($pickup.UpperId)] / remaining 0", "[$($failed -join ', ')] / remaining $($faults.remaining)")

# --- 3. 之后补建：确认、只建一张 ------------------------------------------------------------------------

$status = Wait-L2Condition -Description 'the TO_PICKUP intent was created after the unread read and confirmed' `
    -Journal $journal -Criterion 'to-pickup-confirmed' -TimeoutSeconds 120 `
    -Probe { $row = Get-PickupIntent; if ($row) { [string]$row.Status } else { $null } } `
    -Until { param($v) $v -eq 'CONFIRMED' }
$pickup = Get-PickupIntent
$chain = Get-AuditChain ([string]$pickup.MovementLegId)
$journal.Note("TO_PICKUP audit chain: $($chain -join ' -> ')")

$assertions.Add(
    'L2-UR-02', '读没读到之后，取货腿照常建出并确认，建单计数恰好是 1',
    ($status -eq 'CONFIRMED' -and [int]$pickup.CreateAttemptCount -eq 1),
    'CONFIRMED / 1', "$status / $($pickup.CreateAttemptCount)")

# 审计链的顺序就是因果：第一次建单前读是 SdkFailure（503 经网关成了非精确 Unknown），第二次是 NotFound，
# 然后才 arm。第二次读只可能来自到站等待阶段的 EnsureMovementConfirmedAsync——受理那一轮只调一次派车——
# 而那条路径对从没发出过的意图先过车况检查，不过就不调派车。所以 NOT_FOUND 出现在链上，本身就说明
# 那一轮真车载端的会话就绪、车况检查放行了。
$firstThree = @($chain | Select-Object -First 3)
$assertions.Add(
    'L2-UR-03', '审计链：建单前读没读到 → 建单前读答没有 → 才 arm 建单；中间没有第二次 arm',
    ($firstThree.Count -eq 3 -and
        $firstThree[0] -like 'PRE_CREATE_RECONCILIATION/UNKNOWN/SdkFailure/*' -and
        $firstThree[1] -like 'PRE_CREATE_RECONCILIATION/NOT_FOUND/*' -and
        $firstThree[2] -like 'CREATE_DISPATCH/ARMED/*' -and
        @($chain | Where-Object { $_ -like 'CREATE_DISPATCH/*' }).Count -eq 1),
    'PRE/UNKNOWN/SdkFailure -> PRE/NOT_FOUND -> CREATE_DISPATCH/ARMED (x1)', ($chain -join ' -> '))

$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-UR-04', 'RIoT 上只有一张单，就是取货腿那一张',
    ($riotOrders.Count -eq 1),
    1, $riotOrders.Count)

# --- 4. 补建的单能照常走完取货腿：车到站，真车载端开放录入 ---------------------------------------------

$journal.Note('Vehicle drives the rebuilt TO_PICKUP order and arrives.')
$null = $riot.Command('Put', "orders/$($pickup.UpperId)", @{
    orderState        = 3
    executeVehicleKey = $Context.VehicleKey
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey      = $Context.VehicleKey
    procState       = 'RUNNING'
    movementState   = 'MT_RUNNING'
    speed           = 0.8
    processingOrder = $true
    orderTaskId     = $pickup.OrderId
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
$null = $riot.Command('Put', "orders/$($pickup.UpperId)", @{ orderState = 5 })

$stage = Wait-L2Condition -Description 'the journey trusted the arrival and waits for a sublot' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingSublot' }
$canSubmit = Wait-L2Condition -Description 'the real onboard HMI accepted sublot entry' `
    -Journal $journal -Criterion 'onboard-can-submit' -TimeoutSeconds 180 `
    -Probe { $onboard.CanSubmit() } -Until { param($v) $v }
$assertions.Add(
    'L2-UR-05', '补建的取货单走完：服务端采信到站，真车载端开放录入',
    ($stage -eq 'AwaitingSublot' -and $canSubmit),
    'AwaitingSublot / can submit', "$stage / $canSubmit")

$journal.Note('Scenario finished.')
