#Requires -Version 7

<#
旅程阻断带开始时间上看板（批次 5，control-server#80；program#55 的升级规程）。

在这之前阻断码只写在 JourneyRuntimes.BlockReasonCode 一个字段里：没有开始时间（UpdatedAt 会被后续写入冲掉）、
没有端点，现场与脚本只能直查 SQLite。这条场景从外面读服务端新加的只读端点 /api/dashboard/blocked-journeys，
用 v2 已有的四条阻断路径证「挂上即记、后续写入不冲掉、清掉即消失」：

  A. 检查点等待（VEHICLE_WAITING_AT_CHECKPOINT）：挂上之后端点列出这条旅程与开始时间；车重新动起来、码被
     清掉之后，端点里这条旅程消失，库里开始时间一并清掉。
  B. 会话未就绪（ONBOARD_SESSION_NOT_READY，安全证据有未知项）：端点带开始时间与会话的原因码、安全原因码、
     SafetyUnknownPresent，直接按最高档；引擎每一轮都重写这一行（UpdatedAt 往前走），开始时间不动，已挂时长
     照实增长；看板那一页按最高档渲染出这一行。到站换段时它被清掉，端点不再列出。
  D. 装货中期限过了、门还开着（STATION_TIMEOUT_DOOR_NOT_CLOSED，control-server#81 补）：端点列出 AwaitingLoadResult
     （不是 Blocked）、取货站与开始时间，按操作员档；再跑几轮开始时间不变；门关上端点不再列出。
  C. 装货结果需要恢复（LOAD_RESULT_REQUIRES_RECOVERY）：停摆后端点列出 Blocked、取货站与开始时间，再跑几轮不变。

D 段在 C 段应答之前跑：告警只在装货结果还没到时挂。它等的是装置默认的三十秒站点期限，从到站起算。合成对端不发起恢复
握手，所以 C 段停在「正确地停摆」，清除由 A、B、D 三段证。

所有「对端收到了什么」的判据都先等它出现再断言（Wait-L2Condition／Wait-L2Change），不读一次就下结论。
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
$endpoint = "http://127.0.0.1:$($Context.HealthPort)/api/dashboard/blocked-journeys"
# Wait-L2Condition treats a $null probe as "nothing observed yet", so waiting for a journey to leave the endpoint
# probes for this marker instead.
$notListed = '(not listed)'

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

function Get-Runtime {
    # HasConversion<string>：Stage 存的是枚举成员名。时间列按 SQLite 里的文本读回，由 ConvertTo-Instant 解析。
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Stage, BlockReasonCode, BlockReasonSince, UpdatedAt, PickupStationId FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stage {
    $runtime = Get-Runtime
    if ($null -eq $runtime) { return $null }
    return [string]$runtime.Stage
}

function ConvertTo-Instant([object]$Value) {
    if ($null -eq $Value -or $Value -is [DBNull] -or [string]::IsNullOrWhiteSpace([string]$Value)) { return $null }
    return [DateTimeOffset]::Parse([string]$Value, [Globalization.CultureInfo]::InvariantCulture)
}

# 端点里这条需求的那一行；不在就是 $null。-DateKind String：开始时间原样比较，不经本地时区换算。
function Get-BlockedEntry {
    $response = Invoke-WebRequest -Uri $endpoint -NoProxy -TimeoutSec 10
    $fact = $response.Content | ConvertFrom-Json -DateKind String
    return @($fact.journeys | Where-Object { $_.demandId -eq $demandId }) | Select-Object -First 1
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-PendingOperationKey {
    $pending = @($onboard.Snapshot().body.pending |
        Where-Object { $_.messageType -eq 'SlotOperationCommand' })
    if ($pending.Count -eq 0) { return $null }
    return [string]$pending[0].key
}

function Format-Entry([object]$Entry) {
    if ($null -eq $Entry) { return '(not listed)' }
    return "$($Entry.blockReasonCode) since $($Entry.blockReasonSince) ($($Entry.blockedSeconds) s, $($Entry.escalationLevel), $($Entry.stage) @ $($Entry.stationId))"
}

# 看板那一页上这台车的旅程阻断行：档位的 class 在前，后面是解码后的纯文本。只在旅程阻断卡片里找。
function Get-DashboardRow {
    $html = (Invoke-WebRequest -Uri $Context.DashboardUrl -NoProxy -TimeoutSec 10).Content
    $start = $html.IndexOf('id="blocked-journeys"', [StringComparison]::Ordinal)
    if ($start -lt 0) { return $null }
    $card = $html.Substring($start)
    $end = $card.IndexOf('</section>', [StringComparison]::Ordinal)
    if ($end -ge 0) { $card = $card.Substring(0, $end) }
    $pattern = '<tr class="(escalation-[a-z-]+)"[^>]*><td>' + [regex]::Escape($Context.AgvId) + '</td>.*?</tr>'
    $match = [regex]::Match($card, $pattern)
    if (-not $match.Success) { return $null }
    return $match.Groups[1].Value + ' ' + [Net.WebUtility]::HtmlDecode(($match.Value -replace '<[^>]+>', ' ')).Trim()
}

# --- 0. 派车到取货站的路上 --------------------------------------------------------------------------

# 装载结果挂起，由 C 段决定它是什么。其余应答保持 Auto。
$null = $onboard.Command('Put', 'policy', @{ loadResult = 'Manual' })

$journal.Note("Publishing demand $demandIdWire (sublot $sublot).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-UpperId -purpose 'TO_PICKUP'; if ($row -and $row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }
$pickupStationId = [string](Get-Runtime).PickupStationId

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

# --- A. 检查点等待：挂上即列出、带开始时间；清掉即消失 --------------------------------------------------

$checkpoint = Wait-L2Change -Description 'the endpoint lists the journey waiting at a traffic checkpoint' `
    -Journal $journal -Criterion 'endpoint-checkpoint-wait' -TimeoutSeconds 60 `
    -Baseline { Get-BlockedEntry } `
    -Action {
        $journal.Note('RIoT reports the vehicle holding at a traffic checkpoint.')
        $null = $riot.Command('Put', 'vehicle', @{
            vehicleKey      = $Context.VehicleKey
            procState       = 'RUNNING'
            movementState   = 'MT_WAIT_FOR_CHECKPOINT'
            speed           = 0
            processingOrder = $true
            orderTaskId     = $pickupIntent.OrderId
        })
    } `
    -Probe { Get-BlockedEntry } `
    -Until { param($before, $now) $null -ne $now -and $now.blockReasonCode -eq 'VEHICLE_WAITING_AT_CHECKPOINT' }
$waiting = $checkpoint.Value
$assertions.Add(
    'L2-BJ-01', '行驶中没有阻断时端点不列这条旅程',
    ($null -eq $checkpoint.Baseline), '(not listed)', (Format-Entry $checkpoint.Baseline))

$runtime = Get-Runtime
$checkpointSince = ConvertTo-Instant $waiting.blockReasonSince
$assertions.Add(
    'L2-BJ-02', '检查点等待挂上后端点列出车、站、阻断码与开始时间，开始时间就是库里记下的那一个',
    ($waiting.agvId -eq $Context.AgvId -and $waiting.stationId -eq $pickupStationId -and
        $waiting.stage -eq 'AwaitingPickupArrival' -and $null -ne $checkpointSince -and
        $checkpointSince -eq (ConvertTo-Instant $runtime.BlockReasonSince) -and
        [long]$waiting.blockedSeconds -ge 0 -and $waiting.escalationLevel -eq 'Operator'),
    "$($Context.AgvId) / $pickupStationId / VEHICLE_WAITING_AT_CHECKPOINT since $($runtime.BlockReasonSince) (Operator)",
    "$($waiting.agvId) / $($waiting.stationId) / $(Format-Entry $waiting)")

$cleared = Wait-L2Change -Description 'the journey disappears from the endpoint once the vehicle moves again' `
    -Journal $journal -Criterion 'endpoint-checkpoint-cleared' -TimeoutSeconds 60 `
    -Baseline { Get-BlockedEntry } `
    -Action {
        $journal.Note('The checkpoint releases the vehicle; it is moving again.')
        $null = $riot.Command('Put', 'vehicle', @{
            vehicleKey      = $Context.VehicleKey
            procState       = 'RUNNING'
            movementState   = 'MT_RUNNING'
            speed           = 0.8
            processingOrder = $true
            orderTaskId     = $pickupIntent.OrderId
        })
    } `
    -Probe { (Get-BlockedEntry) ?? $notListed } `
    -Until { param($before, $now) $null -ne $before -and $now -eq $notListed }
$runtime = Get-Runtime
$assertions.Add(
    'L2-BJ-03', '码被清掉后端点不再列出这条旅程，库里阻断码与开始时间一并清空',
    ($cleared.Value -eq $notListed -and $null -eq (ConvertTo-Instant $runtime.BlockReasonSince) -and
        [string]::IsNullOrEmpty([string]$runtime.BlockReasonCode)),
    '(not listed) / code null / since null',
    "$($cleared.Value) / code '$($runtime.BlockReasonCode)' / since '$($runtime.BlockReasonSince)'")

# --- B. 会话未就绪：每一轮都重写同一个码，开始时间不动；会话的三个安全字段随行 ---------------------------------

# 选这个码来证「后续写入不冲掉开始时间」：旅程进了 Blocked 引擎就不再写这一行，而在途阶段会话不就绪时，引擎每一轮
# 都把 ONBOARD_SESSION_NOT_READY 连同 UpdatedAt 重写一遍——正是原来会把阻断时刻冲掉的那种写入。
$sessionBlock = Wait-L2Change -Description 'the endpoint lists the journey behind a session that is no longer ready' `
    -Journal $journal -Criterion 'endpoint-session-not-ready' -TimeoutSeconds 60 `
    -Baseline { Get-BlockedEntry } `
    -Action {
        $journal.Note('The peer reports departure safety it cannot fully establish: one fact unknown.')
        $null = $onboard.Command('Put', 'safety', @{
            departureSafe  = $false
            unknownPresent = $true
            reasonCodes    = @('IO_FACT_UNKNOWN')
        })
    } `
    -Probe { Get-BlockedEntry } `
    -Until { param($before, $now) $null -ne $now -and $now.blockReasonCode -eq 'ONBOARD_SESSION_NOT_READY' }
$notReady = $sessionBlock.Value
$notReadySince = ConvertTo-Instant $notReady.blockReasonSince
$session = $notReady.session
$assertions.Add(
    'L2-BJ-04', '会话未就绪挂上后端点带开始时间与会话的原因码、安全原因码、SafetyUnknownPresent，安全证据不全直接最高档',
    ($null -ne $notReadySince -and $notReady.stage -eq 'AwaitingPickupArrival' -and $null -ne $session -and
        $session.present -eq $true -and -not [string]::IsNullOrEmpty([string]$session.reasonCode) -and
        ([string]$session.safetyReasonCodesJson).Contains('IO_FACT_UNKNOWN') -and $session.safetyUnknownPresent -eq $true -and
        $notReady.escalationLevel -eq 'MaintenanceAdministrator'),
    'ONBOARD_SESSION_NOT_READY with since, session reason, ["IO_FACT_UNKNOWN"], unknown=True, MaintenanceAdministrator',
    "$(Format-Entry $notReady); session $($session | ConvertTo-Json -Compress)")

# 等的是「这一行在挂上之后又被写过、隔了两秒以上」，不是固定睡几秒。
$rewritten = Wait-L2Condition -Description 'the runtime row was written again well after the block began' `
    -Journal $journal -Criterion 'runtime-rewritten-after-block' -TimeoutSeconds 60 `
    -Probe { Get-Runtime } `
    -Until { param($v) $null -ne $v -and (ConvertTo-Instant $v.UpdatedAt) -gt $notReadySince.AddSeconds(2) }
$later = Wait-L2Condition -Description 'the endpoint reports the block as held for longer than before' `
    -Journal $journal -Criterion 'endpoint-after-rewrite' -TimeoutSeconds 30 `
    -Probe { Get-BlockedEntry } `
    -Until { param($v) $null -ne $v -and [long]$v.blockedSeconds -ge [long]$notReady.blockedSeconds + 2 }
$assertions.Add(
    'L2-BJ-05', '这一行被后续写入（同码、UpdatedAt 前移）之后，端点与库里的开始时间都不变，已挂时长照实增长',
    ($later.blockReasonCode -eq 'ONBOARD_SESSION_NOT_READY' -and
        (ConvertTo-Instant $later.blockReasonSince) -eq $notReadySince -and
        (ConvertTo-Instant $rewritten.BlockReasonSince) -eq $notReadySince -and
        (ConvertTo-Instant $rewritten.UpdatedAt) -gt $notReadySince.AddSeconds(2) -and
        [long]$later.blockedSeconds -gt [long]$notReady.blockedSeconds),
    "since $($notReady.blockReasonSince), UpdatedAt > since + 2 s, blockedSeconds > $($notReady.blockedSeconds)",
    "since $($later.blockReasonSince) (db $($rewritten.BlockReasonSince)), UpdatedAt $($rewritten.UpdatedAt), $($later.blockedSeconds) s")

# 看板那一页：卡片是自注册进来的，这里只确认它确实把这一行按最高档渲染出来了。
$row = Wait-L2Condition -Description 'the dashboard renders the blocked journey row' `
    -Journal $journal -Criterion 'dashboard-blocked-row' -TimeoutSeconds 30 `
    -Probe { Get-DashboardRow } `
    -Until { param($v) $null -ne $v -and $v.Contains('ONBOARD_SESSION_NOT_READY') }
$assertions.Add(
    'L2-BJ-06', '看板的旅程阻断卡片渲染出这条阻断：车、取货站、阻断码、安全原因码，按维护管理员那一档上色',
    ($row.StartsWith('escalation-maintenance-administrator ') -and $row.Contains($pickupStationId) -and
        $row.Contains('ONBOARD_SESSION_NOT_READY') -and $row.Contains('IO_FACT_UNKNOWN')),
    "escalation-maintenance-administrator $($Context.AgvId) $pickupStationId ONBOARD_SESSION_NOT_READY ... IO_FACT_UNKNOWN", $row)

$journal.Note('The peer establishes departure safety again.')
$null = $onboard.Command('Put', 'safety', @{
    departureSafe  = $true
    unknownPresent = $false
    reasonCodes    = @()
})

# --- C. 停摆的码：LOAD_RESULT_REQUIRES_RECOVERY 挂上即带开始时间，停摆期间不变 ---------------------------------

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

$operationKey = Wait-L2Condition -Description 'the load command reached the peer and is waiting for a result' `
    -Journal $journal -Criterion 'pending-operation' -TimeoutSeconds 120 `
    -Probe { Get-PendingOperationKey } -Until { param($v) $null -ne $v }
# 服务端先发指令、迭代末尾才把 stage 写成 AwaitingLoadResult，所以等到库里是这一段再应答（见 load-result-requires-recovery）。
$null = Wait-L2Condition -Description 'the journey recorded that it is waiting for the load result' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 30 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingLoadResult' }
$runtime = Get-Runtime
$afterArrival = Get-BlockedEntry
$assertions.Add(
    'L2-BJ-07', '到站换段之后会话未就绪那条阻断已清掉，端点不再列出这条旅程',
    ([string]::IsNullOrEmpty([string]$runtime.BlockReasonCode) -and $null -eq $afterArrival),
    '(not listed) / code null', "$(Format-Entry $afterArrival) / code '$($runtime.BlockReasonCode)'")

# --- D. 装货中期限过了、门还开着：STATION_TIMEOUT_DOOR_NOT_CLOSED 带开始时间，门关上即消失 --------------------------

$doorAlarm = Wait-L2Change -Description 'the endpoint lists the load stop past its deadline with a door open' `
    -Journal $journal -Criterion 'endpoint-door-not-closed' -TimeoutSeconds 90 `
    -Baseline { Get-BlockedEntry } `
    -Action {
        $journal.Note('Peer reports a slot door not closed while the load is still underway; the stop runs out its deadline.')
        $null = $onboard.Command('Put', 'safety', @{
            departureSafe        = $false
            allTargetSlotsLocked = $false
            unknownPresent       = $false
            reasonCodes          = @('LOCK_NOT_CLOSED')
        })
    } `
    -Probe { Get-BlockedEntry } `
    -Until { param($before, $now) $null -ne $now -and $now.blockReasonCode -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED' }
$door = $doorAlarm.Value
$doorSince = ConvertTo-Instant $door.blockReasonSince
$runtime = Get-Runtime
$assertions.Add(
    'L2-BJ-10', '仓门未闭告警挂上后端点列出 AwaitingLoadResult（不是 Blocked）、取货站与开始时间，开始时间就是库里记下的那一个，按操作员档',
    ($null -eq $doorAlarm.Baseline -and $door.stage -eq 'AwaitingLoadResult' -and $door.stationId -eq $pickupStationId -and
        $null -ne $doorSince -and $doorSince -eq (ConvertTo-Instant $runtime.BlockReasonSince) -and
        $door.escalationLevel -eq 'Operator'),
    "(not listed) → AwaitingLoadResult @ $pickupStationId, STATION_TIMEOUT_DOOR_NOT_CLOSED since $($runtime.BlockReasonSince) (Operator)",
    "$(Format-Entry $doorAlarm.Baseline) → $(Format-Entry $door)")

$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$doorHeld = Get-BlockedEntry
$assertions.Add(
    'L2-BJ-11', '告警挂着期间运行时又跑了几轮，码与开始时间不变，stage 仍是 AwaitingLoadResult',
    ($null -ne $doorHeld -and $doorHeld.blockReasonCode -eq 'STATION_TIMEOUT_DOOR_NOT_CLOSED' -and
        (ConvertTo-Instant $doorHeld.blockReasonSince) -eq $doorSince -and $doorHeld.stage -eq 'AwaitingLoadResult'),
    "STATION_TIMEOUT_DOOR_NOT_CLOSED since $($door.blockReasonSince) (AwaitingLoadResult)", (Format-Entry $doorHeld))

$doorShut = Wait-L2Change -Description 'the journey disappears from the endpoint once the door is shut' `
    -Journal $journal -Criterion 'endpoint-door-closed' -TimeoutSeconds 60 `
    -Baseline { Get-BlockedEntry } `
    -Action {
        $journal.Note('The operator shuts the door; the peer reports every slot locked again.')
        $null = $onboard.Command('Put', 'safety', @{
            departureSafe        = $true
            allTargetSlotsLocked = $true
            unknownPresent       = $false
            reasonCodes          = @()
        })
    } `
    -Probe { (Get-BlockedEntry) ?? $notListed } `
    -Until { param($before, $now) $null -ne $before -and $now -eq $notListed }
$runtime = Get-Runtime
$assertions.Add(
    'L2-BJ-12', '门关上后端点不再列出这条旅程，库里阻断码与开始时间一并清空，本站仍在等装货结果',
    ($doorShut.Value -eq $notListed -and $null -eq (ConvertTo-Instant $runtime.BlockReasonSince) -and
        [string]::IsNullOrEmpty([string]$runtime.BlockReasonCode) -and [string]$runtime.Stage -eq 'AwaitingLoadResult'),
    '(not listed) / code null / since null / AwaitingLoadResult',
    "$($doorShut.Value) / code '$($runtime.BlockReasonCode)' / since '$($runtime.BlockReasonSince)' / $($runtime.Stage)")

$blocked = Wait-L2Change -Description 'the endpoint lists the journey blocked on the incomplete load result' `
    -Journal $journal -Criterion 'endpoint-load-result-block' -TimeoutSeconds 120 `
    -Baseline { Get-BlockedEntry } `
    -Action {
        $journal.Note("Operator timeout ran out on the peer; it reports an incomplete OperationResult ($operationKey).")
        $null = $onboard.Command('Put', "answer/$operationKey", @{ completed = $false })
    } `
    -Probe { Get-BlockedEntry } `
    -Until { param($before, $now) $null -ne $now -and $now.blockReasonCode -eq 'LOAD_RESULT_REQUIRES_RECOVERY' }
$loadBlock = $blocked.Value
$loadSince = ConvertTo-Instant $loadBlock.blockReasonSince
$runtime = Get-Runtime
$assertions.Add(
    'L2-BJ-08', '装货结果需要恢复挂上后端点列出 Blocked、取货站与开始时间，开始时间就是库里记下的那一个',
    ($loadBlock.stage -eq 'Blocked' -and $loadBlock.stationId -eq $pickupStationId -and $null -ne $loadSince -and
        $loadSince -eq (ConvertTo-Instant $runtime.BlockReasonSince) -and $loadSince -gt $notReadySince),
    "Blocked @ $pickupStationId, LOAD_RESULT_REQUIRES_RECOVERY since $($runtime.BlockReasonSince)",
    (Format-Entry $loadBlock))

# 否定判据要有界：让运行时确实又跑几轮，再说开始时间没动。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$stillBlocked = Get-BlockedEntry
$assertions.Add(
    'L2-BJ-09', '停摆期间运行时又跑了几轮，阻断码与开始时间不变',
    ($null -ne $stillBlocked -and $stillBlocked.blockReasonCode -eq 'LOAD_RESULT_REQUIRES_RECOVERY' -and
        (ConvertTo-Instant $stillBlocked.blockReasonSince) -eq $loadSince),
    "LOAD_RESULT_REQUIRES_RECOVERY since $($loadBlock.blockReasonSince)", (Format-Entry $stillBlocked))

$journal.Note('Scenario finished at Blocked.')
