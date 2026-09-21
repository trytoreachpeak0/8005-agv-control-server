#Requires -Version 7

<#
让站不打断阻断离站的状态（批次7-08，control-server#213；出口判据 ③ 后半）：主车满了、尚未离开，它那一条装货还在执行、
仓门开着，这时另一台车被承诺以这个站为下一停靠。让站当场触发、快照当场是 WAITING_STATION_YIELD；但离站要等装货落定、
门关上、离站安全核验通过之后才发——复用既有离站路，本票不另写一套「阻断离站」判定。

布置见 setup 文件：主车后侧四个仓报 DISABLED，前侧 4 花篮的需求甲一受理两侧就都没有空仓，VEHICLE_FULL。
为什么门要由本车在执行的装货打开，而不是让一辆空闲等单的车开着门，也写在那里。

判据：
1. 需求甲给主车；到站后装货命令挂在主车的合成车载端上（结果手动答），主车 VEHICLE_FULL、AwaitingLoadResult（L2-WSD-01）。
   主车报一扇门没锁（/safety，LOCK_NOT_CLOSED）；由本车在执行的装货解释，会话仍是 Ready。
2. 需求乙（4 花篮、前侧、同在 12 号站）只能给另一台车，它的下一停靠就是 12 号站（02）。
3. 让站确实触发了，而且门开着、装货没落定时车上就收到了那张快照（03、04）。
4. 门开着、装货未落定，服务端又转了几轮：没有离站核验，关卡腿没有建单（05）。
5. 门关上、装货落定；另等第二个事实：关卡腿建了单。三个时刻从证据里取值比较——放行离站的那一次核验请求、关卡腿的订单
   意图，都晚于服务端收到「门已关」的那一刻（06）。

红证据（票面）：触发即发离站请求、不等收敛——05 变红（门开着、装货未落定时就发了离站核验）。06 在那份注入下仍绿，
而且应该绿：门开着时车载端答不出能用的「可以走」，服务端照旧不建关卡单；门关上时安全版本前移、旧核验过期、重新问，
放行的那一次核验与关卡单仍晚于关门。能让 06 红的是「不看车载端答复就走」，那是离站路本身坏了，不是让站的事。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
. (Join-Path $PSScriptRoot 'StationYieldCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$riot = $Context.Riot

# 主车那一台合成车载端（setup 文件把它列在第一位）。
$holderPeer = @($Context.OnboardPeers | Where-Object { $_.AgvId -eq $Context.AgvId })[0].Double

# 服务端收到的、这台车的安全状态变更，按收到的先后。
function Get-SafetyChanges([string]$AgvId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT MessageId, RequestJson, ReceivedAt FROM ProtocolInbox WHERE MessageType = 'SafetyStateChanged'")
    $changes = foreach ($row in $rows) {
        $envelope = [string]$row.RequestJson | ConvertFrom-Json -DateKind String
        if ([string]$envelope.agvId -ne $AgvId) { continue }
        [pscustomobject]@{
            Version    = [long]$envelope.payload.safetyStateVersion
            Locked     = [bool]$envelope.payload.safety.allTargetSlotsLocked
            ReceivedAt = ConvertTo-L2YieldInstant $row.ReceivedAt
        }
    }
    return , @($changes | Sort-Object ReceivedAt)
}

function Get-PendingLoadKey {
    $pending = @($holderPeer.Snapshot().body.pending | Where-Object { $_.messageType -eq 'SlotOperationCommand' })
    if ($pending.Count -eq 0) { return $null }
    return [string]$pending[0].key
}

function Get-DepartureCheckCount([string]$AgvId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'PreDepartureSafetyCheck'")
    return @($rows | Where-Object { ([string]$_.PayloadJson | ConvertFrom-Json).agvId -eq $AgvId }).Count
}

Initialize-L2YieldRig $Context

# --- 1. 主车满了，装货在执行，门开着 ------------------------------------------------------------------------

$null = $holderPeer.Command('Put', 'policy', @{ loadResult = 'Manual' })
$a = New-L2CargoDemand 'A' 'N1-3' 4 $Context.RunId
Publish-L2CargoDemand $Context $a
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2YieldJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
if ([string]$journey.AgvId -ne $Context.AgvId) {
    throw "Demand A went to $($journey.AgvId), not $($Context.AgvId): the scenario's premise (vehicle-id tie-break between two idle vehicles at the gate) does not hold."
}
$holder = [pscustomobject]@{
    DemandA     = $a
    JourneyId   = [string]$journey.JourneyId
    HolderAgvId = $Context.AgvId
    ComerAgvId  = $script:YieldSecondAgvId
}
$null = Move-L2YieldVehicleToCurrentStop $Context $holder.JourneyId $Context.VehicleKey $Context.PickupStationRiotId
$loadKey = Wait-L2Condition -Description 'the load command of demand A is held at the holder' -Journal $journal `
    -Criterion 'a-load-pending' -TimeoutSeconds 120 -Probe { Get-PendingLoadKey } -Until { param($v) $null -ne $v }
$loading = Wait-L2Condition -Description 'the holder records that it waits for the load result' -Journal $journal `
    -Criterion 'a-load-stage' -TimeoutSeconds 30 -Probe { Get-L2YieldJourney $connection $a.Id } `
    -Until { param($v) [string]$v.Stage -eq 'AwaitingLoadResult' }
$assertions.Add(
    'L2-WSD-01', "主车 $($Context.AgvId) 接下前侧 4 花篮的需求甲，后侧无空仓：VEHICLE_FULL；到站后装货命令在执行",
    ([string]$loading.LoadingPhaseState -eq 'VEHICLE_FULL' -and [string]$loading.Stage -eq 'AwaitingLoadResult'),
    'AwaitingLoadResult VEHICLE_FULL', (Format-L2CargoJourney $loading))

$journal.Note('The holder reports a slot door not locked while its load is under way.')
$null = $holderPeer.Command('Put', 'safety', @{
    departureSafe        = $false
    allTargetSlotsLocked = $false
    unknownPresent       = $false
    reasonCodes          = @('LOCK_NOT_CLOSED')
})
$null = Wait-L2Condition -Description 'the server recorded the open door' -Journal $journal -Criterion 'door-open' -TimeoutSeconds 30 `
    -Probe { @((Get-SafetyChanges $Context.AgvId) | Where-Object { -not $_.Locked }).Count } -Until { param($v) $v -ge 1 }

# --- 2、3. 另一台车被承诺以这个站为下一停靠；让站确实触发了 -------------------------------------------------

$comer = Send-L2YieldComer $Context $holder 'L2-WSD'
$null = Confirm-L2YieldTriggered $Context $holder $comer 'L2-WSD'

# --- 4. 门开着、装货未落定：不离站 ----------------------------------------------------------------------------

$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$held = Get-L2YieldJourney $connection $a.Id
$checksWhileOpen = Get-DepartureCheckCount $Context.AgvId
$gateWhileOpen = Get-L2YieldGateIntent $connection $holder.JourneyId
$assertions.Add(
    'L2-WSD-05', '让站已触发、门开着、装货未落定，服务端又转了 4 轮：没有离站核验，关卡腿没有建单',
    ($checksWhileOpen -eq 0 -and $null -eq $gateWhileOpen -and [string]$held.LoadingClosedReason -eq 'WAITING_STATION_YIELD'),
    '0 departure checks, no gate intent, WAITING_STATION_YIELD',
    "$checksWhileOpen departure checks, $(if ($gateWhileOpen) { "gate intent $($gateWhileOpen.UpperId)" } else { 'no gate intent' }), $(Format-L2CargoJourney $held)")

# --- 5. 门关上、装货落定；另等第二个事实：离站，且晚于关门 ------------------------------------------------------

$journal.Note('The holder reports every slot door locked again, then the load completes.')
$null = $holderPeer.Command('Put', 'safety', @{
    departureSafe        = $true
    allTargetSlotsLocked = $true
    unknownPresent       = $false
    reasonCodes          = @()
})
$null = Wait-L2Condition -Description 'the server recorded the closed door' -Journal $journal -Criterion 'door-closed' -TimeoutSeconds 30 `
    -Probe { @((Get-SafetyChanges $Context.AgvId) | Where-Object { $_.Locked }).Count } -Until { param($v) $v -ge 1 }
$null = $holderPeer.Command('Put', "answer/$loadKey", @{ completed = $true })

# 等「已消费的离站核验答复」落库，而不是等关卡单：关卡腿的订单意图由 AuthorizeMovementAsync 先单独保存，已消费答复与
# 停靠完成、阶段前移是这一轮最后那一次保存（scripts/l2/README.md 第 14 条）。等到后者再读前者是安全的，反过来不是——
# 第一版等关卡单、随即读已消费答复，读在两次保存之间就是「核验 (none)」，同一份脚本一绿一红。
$departed = Wait-L2ConditionOrLast -Description 'the holder left for the gate after the door closed' -Journal $journal `
    -Criterion 'departed' -TimeoutSeconds 90 -Probe { Get-L2YieldJourney $connection $a.Id } `
    -Until { param($v) $null -ne $v -and -not [string]::IsNullOrEmpty([string]$v.ConsumedSafetyResultMessageId) }
$gate = Get-L2YieldGateIntent $connection $holder.JourneyId

$changes = Get-SafetyChanges $Context.AgvId
$firstOpen = @($changes | Where-Object { -not $_.Locked } | Select-Object -First 1)
$closedAt = if ($firstOpen.Count -eq 0) { $null } else {
    @($changes | Where-Object { $_.Locked -and $_.ReceivedAt -gt $firstOpen[0].ReceivedAt } | Select-Object -First 1) |
        ForEach-Object { $_.ReceivedAt } }
# 放行离站的那一次核验：旅程行记下的已消费答复，它答的是哪一个核验，那个核验请求什么时候发出。
$checkSentAt = $null
if ($null -ne $departed -and -not [string]::IsNullOrEmpty([string]$departed.ConsumedSafetyResultMessageId)) {
    $answer = Invoke-L2Query -Connection $connection -Sql (
        "SELECT RequestJson FROM ProtocolInbox WHERE MessageId = '$($departed.ConsumedSafetyResultMessageId)'")
    $checkId = [string](([string]$answer[0].RequestJson | ConvertFrom-Json).payload.preDepartureSafetyCheckId)
    $checks = Invoke-L2Query -Connection $connection -Sql (
        "SELECT PayloadJson, CreatedAt FROM ProtocolOutbox WHERE MessageType = 'PreDepartureSafetyCheck'")
    $checkSentAt = @($checks | Where-Object {
            [string](([string]$_.PayloadJson | ConvertFrom-Json).payload.preDepartureSafetyCheckId) -eq $checkId } |
        ForEach-Object { ConvertTo-L2YieldInstant $_.CreatedAt }) | Select-Object -First 1
}
$orderAt = if ($null -eq $gate) { $null } else { ConvertTo-L2YieldInstant $gate.CreatedAt }
$journal.Observe('door-and-departure-instants',
    "door closed $closedAt; departure check $checkSentAt; gate order intent $orderAt",
    @{ doorClosedAt = "$closedAt"; departureCheckSentAt = "$checkSentAt"; gateOrderIntentAt = "$orderAt" })
$assertions.Add(
    'L2-WSD-06', '门关上之后主车才离站：放行离站的那一次核验请求与关卡腿的订单意图，都晚于服务端收到「门已关」',
    ($null -ne $closedAt -and $null -ne $checkSentAt -and $null -ne $orderAt -and
        $checkSentAt -gt $closedAt -and $orderAt -gt $closedAt),
    'door closed < departure check, door closed < gate order intent',
    "door closed $(if ($closedAt) { $closedAt.ToString('o') } else { '(none)' }); " +
    "departure check $(if ($checkSentAt) { $checkSentAt.ToString('o') } else { '(none)' }); " +
    "gate order intent $(if ($orderAt) { $orderAt.ToString('o') } else { '(none)' })")

$journal.Note('让站在门开着、装货未落定时已经触发并告诉了车；离站等到门关上、装货落定、离站核验通过之后才发。')
