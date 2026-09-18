#Requires -Version 7

<#
车辆运动中建立会话，随后停稳。

方案第 4 节标 ★ 的三条之一。服务端侧的缺陷已在 L1 修掉并钉住
（docs/defects/20260903-onboard-safety-facts-frozen-at-session-start.md，
AdmissionSafetyFactsComeFromTheLatestChangeNotTheSessionSnapshot 与
ArrivalIsNotTrustedWhileTheLatestSafetyStateSaysTheVehicleIsMoving），所以这一条要证的不是同一
件事，而是**真实两端的时序**：合成车载端确实按协议把每一次变化发成 SafetyStateChanged，服务端
确实按收到的那一条推进，而不是按会话建立那一刻的快照。

两个方向都走一遍：

  会话带着 vehicleStopped=false 建立 → 需求判 ONBOARD_DEPARTURE_UNSAFE，不受理
  车停稳（SafetyStateChanged）        → 受理并派车
  车出发（SafetyStateChanged）        → RIoT 说到站了，但车载端说还在动 → 不采信
  车停稳（SafetyStateChanged）        → 采信到站，进 AwaitingSublot

第三步是危险的那个方向：服务端若仍读会话建立时的快照，就会采信一次并不成立的到站。

会话建立时的初值来自 session-established-while-moving.setup.psd1，不是运行时改的——
PUT /control/v1/safety 只能报告一个已经存在的会话的变化。
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

function Get-Stage {
    # HasConversion<string>: the column holds the member name, and reading it as an ordinal throws.
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Stage FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].Stage
}

function Get-BacklogReason {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return [string]$rows[0].ReasonCode
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-OutboundSafetyChanges {
    return @($onboard.Snapshot().body.wire |
        Where-Object { $_.direction -eq 'out' -and $_.messageType -eq 'SafetyStateChanged' }).Count
}

# 这条场景讲的是安全事实，不是装载。让 sublot 挂起，旅程就停在 AwaitingSublot，终态是确定的。
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Manual' })

# --- 0. 前提：会话确实是在「车还在动」的状态下建立的 -----------------------------------------------

# 断言而不是假定。种子配错了会让后面每一条判据都在证明另一回事。
$seeded = $onboard.Snapshot().body
$assertions.Add(
    'L2-MV-01', '会话建立时车载端报告车辆仍在运动',
    ($seeded.safety.vehicleStopped -eq $false -and $seeded.safetyStateVersion -eq 1),
    'vehicleStopped=False @ safetyStateVersion=1',
    "vehicleStopped=$($seeded.safety.vehicleStopped) @ safetyStateVersion=$($seeded.safetyStateVersion)")

# 会话本身仍然是 Ready：departureSafe 为真，车在动不妨碍会话就绪，这正是缺陷当初难被发现的原因。
$sessionRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'"
$assertions.Add(
    'L2-MV-02', '车在动不影响会话就绪（就绪只看 departureSafe）',
    ($sessionRows.Count -eq 1 -and [string]$sessionRows[0].Readiness -eq 'Ready'),
    'Ready', $(if ($sessionRows.Count -eq 1) { [string]$sessionRows[0].Readiness } else { '(no session row)' }))

# --- 1. 车还在动的时候，需求不受理 ------------------------------------------------------------------

$journal.Note("Publishing demand $demandIdWire (sublot $sublot) while the vehicle is still moving.")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$reason = Wait-L2Condition -Description 'the moving vehicle was refused the demand' `
    -Journal $journal -Criterion 'backlog-reason' -TimeoutSeconds 90 `
    -Probe { Get-BacklogReason } -Until { param($v) $v -eq 'ONBOARD_DEPARTURE_UNSAFE' }
$assertions.Add(
    'L2-MV-03', '车在动时需求判 ONBOARD_DEPARTURE_UNSAFE',
    ($reason -eq 'ONBOARD_DEPARTURE_UNSAFE'), 'ONBOARD_DEPARTURE_UNSAFE', $reason)

# 判定与记录的「实际值」必须来自同一次读取，否则证据可能记着一个不是它据以判定的状态。
$stage = Get-Stage
$refusedIntent = Get-UpperId -purpose 'TO_PICKUP'
$assertions.Add(
    'L2-MV-04', '被拒的需求没有 journey，也没有建单',
    ($null -eq $stage -and $null -eq $refusedIntent),
    '(no runtime, no intent)',
    "runtime=$($stage ?? '(none)') intent=$(if ($refusedIntent) { 'present' } else { '(none)' })")

# --- 2. 车停稳，车载端发 SafetyStateChanged，服务端据此受理 ------------------------------------------

$journal.Note('Vehicle comes to rest; the peer reports the change.')
$null = Set-L2OnboardSafety -Onboard $onboard -Connection $connection -AgvId $Context.AgvId `
    -Safety @{ vehicleStopped = $true } -Journal $journal

$stage = Wait-L2Condition -Description 'the demand was accepted once the vehicle reported itself stopped' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$assertions.Add(
    'L2-MV-05', '车停稳后同一条需求被受理并派车',
    ($stage -eq 'AwaitingPickupArrival'), 'AwaitingPickupArrival', $stage)
$acceptedReason = Get-BacklogReason
$assertions.Add(
    'L2-MV-06', '同一条 backlog 记录翻成 ACCEPTED',
    ($acceptedReason -eq 'ACCEPTED'), 'ACCEPTED', $acceptedReason)

# 服务端的推进必须真的是被一条 SafetyStateChanged 推动的。少了这一条，上面两句只说明「等久了就好了」。
$changes = Get-OutboundSafetyChanges
$assertions.Add(
    'L2-MV-07', '推进是被车载端发出的 SafetyStateChanged 推动的',
    ($changes -ge 1), '>= 1', $changes)

$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-UpperId -purpose 'TO_PICKUP'; if ($row -and $row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }

# --- 3. 危险的反方向：RIoT 说到站了，车载端说还在动 --------------------------------------------------

$journal.Note('Vehicle departs for the pickup station; the peer reports it moving again.')
# 等服务端把「在动」落库再往下走，不能只等车载端发出去（control-server#141）。原来这里 PUT 完就摆到站，
# CI 四路并跑时服务端处理这条报告用了 183 ms，运行时在这段空当里读到的还是上一条「停稳」，采信了到站。
# 服务端没法对一份还没收到的报告保守，所以错在场景：L2-MV-08 要证的是「已知车在动时不采信」。
$null = Set-L2OnboardSafety -Onboard $onboard -Connection $connection -AgvId $Context.AgvId `
    -Safety @{ vehicleStopped = $false } -Journal $journal
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

# RIoT 摆出一个完整的到站：订单终态、车在目标站、速度零。唯一还说「在动」的是车载端自己。
$journal.Note('RIoT reports a complete arrival while the peer still says the vehicle is moving.')
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

# 否定判据要有界：等运行时确实又跑了几轮，再说「它没有采信」。不用 sleep。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$stage = Get-Stage
$assertions.Add(
    'L2-MV-08', '车载端仍报运动时，服务端不采信 RIoT 的到站',
    ($stage -eq 'AwaitingPickupArrival'), 'AwaitingPickupArrival', $stage)

# --- 4. 车真的停稳了，到站才被采信 ------------------------------------------------------------------

$journal.Note('Vehicle comes to rest at the pickup station; the peer reports the change.')
$null = Set-L2OnboardSafety -Onboard $onboard -Connection $connection -AgvId $Context.AgvId `
    -Safety @{ vehicleStopped = $true } -Journal $journal

$stage = Wait-L2Condition -Description 'the arrival was trusted once the peer reported the vehicle stopped' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingSublot' }
$assertions.Add(
    'L2-MV-09', '车载端报停稳后到站被采信，进入 AwaitingSublot',
    ($stage -eq 'AwaitingSublot'), 'AwaitingSublot', $stage)

# 全程一条 TO_PICKUP 单。被拒的那几轮不许留下派车痕迹。
$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-MV-10', '全程只建了一条 RIoT 单（被拒期间没有派过车）',
    ($riotOrders.Count -eq 1), 1, $riotOrders.Count)

$journal.Note('Scenario finished.')
