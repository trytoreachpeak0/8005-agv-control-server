#Requires -Version 7

<#
旅程等人期间的电量监看（control-server#273）：满载停在闸口等人取货，电量掉到救命线以下。

用户 2026-09-22 定的方案 A 是「只报不动」：服务端每轮读 RIoT 电量并记下，同一阶段等满门槛就打日志，低于接单线升 Error，
低于救命线写明需要人工挪车充电，看板「等人中的旅程」显示已等多久与电量——从不下发行驶或充电命令（REQ-0169）。单元测试在一个
进程里用可控时钟证了判法；这条场景从外面证四件只有真进程才看得见的事：

  1. 电量是经真 HTTP 网关从 RIoT 的车卡片读来的，不是测试替身塞进去的值：场景只改假 RIoT 的车卡片。
  2. 那一行日志真的以 Error 级、带救命那句话写进了服务端的控制台日志。
  3. 看板进程经真端点拿到了这一行，页面上写着「需要人工挪车充电」。
  4. 这期间 RIoT 上一张单都没多建，旅程阶段原样；人把货取了，旅程照常完成，这一行从端点消失。

门槛由 setup 文件缩到五秒，否则场景要等十分钟。闸口的卸货应答挂起（unloadResult = Manual），等判据都出现之后再放行。
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
$endpoint = "http://127.0.0.1:$($Context.HealthPort)/api/dashboard/waiting-journeys"
$serverLog = Join-Path (Split-Path -Parent $Context.SnapshotRoot) 'logs/control-server.out.log'
# Wait-L2Condition treats a $null probe as "nothing observed yet", so waiting for a journey to leave the endpoint
# probes for this marker instead.
$notListed = '(not listed)'

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

function Get-Runtime {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT JourneyId, Stage, BlockReasonCode, WaitingSince, WaitingBatteryPercent FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stage {
    $runtime = Get-Runtime
    if ($null -eq $runtime) { return $null }
    return [string]$runtime.Stage
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 端点里这趟旅程的那一行；不在就是 $null。-DateKind String：时间原样读，不经本地时区换算。
function Get-WaitingEntry([string]$JourneyId) {
    $response = Invoke-WebRequest -Uri $endpoint -NoProxy -TimeoutSec 10
    $fact = $response.Content | ConvertFrom-Json -DateKind String
    return @($fact.journeys | Where-Object { $_.journeyId -eq $JourneyId }) | Select-Object -First 1
}

<#
服务端日志里这趟旅程、同时提到 $Needle 的第一条监看记录；没有就是 $null。Serilog 控制台格式一条记录以 `[时:分:秒 级别]`
开头，可能跨多行，所以按记录切分再判（与 structural-block-oversized-demand 同一个切法）。日志文件此刻仍被服务端写着，用共享读打开。
返回一条字符串而不是数组：Wait-L2Condition 把探针的返回值原样交给判据，单个字符串没有包不包一层的问题。
#>
function Get-WatchRecord([string]$JourneyId, [string]$Needle) {
    if (-not (Test-Path -LiteralPath $serverLog)) { return $null }
    $stream = [IO.File]::Open($serverLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $text = [IO.StreamReader]::new($stream).ReadToEnd()
    }
    finally {
        $stream.Dispose()
    }
    foreach ($record in [regex]::Split($text, '(?m)^(?=\[\d{2}:\d{2}:\d{2} [A-Z]{3}\] )')) {
        if ($record.Contains("Journey $JourneyId ", [StringComparison]::Ordinal) -and
            $record.Contains('has waited for a person', [StringComparison]::Ordinal) -and
            $record.Contains($Needle, [StringComparison]::Ordinal)) {
            return $record.Trim()
        }
    }
    return $null
}

# 看板那一页上「等人中的旅程」卡片里这台车那一行，解码成纯文本。
function Get-DashboardRow {
    $html = (Invoke-WebRequest -Uri $Context.DashboardUrl -NoProxy -TimeoutSec 10).Content
    $start = $html.IndexOf('id="waiting-journeys"', [StringComparison]::Ordinal)
    if ($start -lt 0) { return $null }
    $card = $html.Substring($start)
    $end = $card.IndexOf('</section>', [StringComparison]::Ordinal)
    if ($end -ge 0) { $card = $card.Substring(0, $end) }
    $match = [regex]::Match($card, '<tr[^>]*><td>' + [regex]::Escape($Context.AgvId) + '</td>.*?</tr>')
    if (-not $match.Success) { return $null }
    return [Net.WebUtility]::HtmlDecode(($match.Value -replace '<[^>]+>', ' ')).Trim()
}

# --- 0. 走到闸口，卸货应答挂起 ----------------------------------------------------------------------

$null = $onboard.Command('Put', 'policy', @{ unloadResult = 'Manual' })

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

$journal.Note('Vehicle drives to the pickup station and comes to rest.')
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
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

$null = Wait-L2Condition -Description 'the load committed and the journey set off for the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$gateIntent = Wait-L2Condition -Description 'the TO_GATE intent was confirmed' `
    -Journal $journal -Criterion 'to-gate-intent' -TimeoutSeconds 60 `
    -Probe { $row = Get-UpperId -purpose 'TO_GATE'; if ($row -and $row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }

$journal.Note('Vehicle drives to the gate and comes to rest.')
$null = $riot.Command('Put', "orders/$($gateIntent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
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

$null = Wait-L2Condition -Description 'the unload command went out and the vehicle stands at the gate waiting for a person' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingUnloadResult' }
$journeyId = [string](Get-Runtime).JourneyId
$ordersAtTheGate = @($riot.Snapshot().body.orders).Count

# --- 1. 电量掉到救命线以下：日志、库、端点、看板 ----------------------------------------------------

$journal.Note('RIoT reports the waiting vehicle at 12% battery.')
$null = $riot.Command('Put', 'vehicle', @{ vehicleKey = $Context.VehicleKey; battery = 12 })

$line = Wait-L2Condition -Description 'the server logged the wait with the battery under the rescue line' `
    -Journal $journal -Criterion 'watch-log-record' -TimeoutSeconds 60 `
    -Probe { Get-WatchRecord -JourneyId $journeyId -Needle 'battery 12%' } `
    -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-WJ-01', '等满门槛后服务端日志里有这趟旅程的监看记录：ERR 级，阶段 AwaitingUnloadResult，电量 12%，写明低于救命线要人工挪车',
    ($line -match '^\[\d{2}:\d{2}:\d{2} ERR\] ' -and
        $line.Contains('AwaitingUnloadResult', [StringComparison]::Ordinal) -and
        $line.Contains('below the rescue line of 15%', [StringComparison]::Ordinal)),
    '[hh:mm:ss ERR] ... AwaitingUnloadResult ... battery 12% ... below the rescue line of 15% ...', $line)

$entry = Wait-L2Condition -Description 'the dashboard endpoint lists the journey with the recorded battery' `
    -Journal $journal -Criterion 'endpoint-waiting-journey' -TimeoutSeconds 30 `
    -Probe { Get-WaitingEntry -JourneyId $journeyId } `
    -Until { param($v) $null -ne $v -and $null -ne $v.batteryPercent -and [int]$v.batteryPercent -eq 12 }
$runtime = Get-Runtime
$assertions.Add(
    'L2-WJ-02', '端点列出这趟旅程：车、阶段、电量 12%、等级 BelowRescueLine、已过门槛，电量与库里监看记下的一致',
    ($entry.agvId -eq $Context.AgvId -and $entry.stage -eq 'AwaitingUnloadResult' -and
        $entry.batteryLevel -eq 'BelowRescueLine' -and [bool]$entry.pastWarningThreshold -and
        [long]$entry.waitedSeconds -ge 5 -and [int]$runtime.WaitingBatteryPercent -eq 12),
    "$($Context.AgvId) / AwaitingUnloadResult / 12 / BelowRescueLine / past threshold",
    "$($entry.agvId) / $($entry.stage) / $($entry.batteryPercent) / $($entry.batteryLevel) / past=$($entry.pastWarningThreshold) / waited $($entry.waitedSeconds) s / db $($runtime.WaitingBatteryPercent)")

$row = Wait-L2Condition -Description 'the dashboard page shows the waiting journey card row' `
    -Journal $journal -Criterion 'dashboard-waiting-row' -TimeoutSeconds 30 `
    -Probe { Get-DashboardRow } -Until { param($v) $null -ne $v -and $v.Contains('12%', [StringComparison]::Ordinal) }
$assertions.Add(
    'L2-WJ-03', '看板「等人中的旅程」这一行写着闸口等卸货、12%、需要人工挪车充电',
    ($row.Contains('闸口等卸货', [StringComparison]::Ordinal) -and $row.Contains('需要人工挪车充电', [StringComparison]::Ordinal)),
    '闸口等卸货 ... 12% ... 需要人工挪车充电', $row)

$ordersNow = @($riot.Snapshot().body.orders).Count
$stage = Get-Stage
$assertions.Add(
    'L2-WJ-04', '报了但车没动：RIoT 上一张单都没多建，旅程仍停在 AwaitingUnloadResult',
    ($ordersNow -eq $ordersAtTheGate -and $stage -eq 'AwaitingUnloadResult'),
    "$ordersAtTheGate orders / AwaitingUnloadResult", "$ordersNow orders / $stage")

# --- 2. 人把货取了：照常完成，这一行消失 ------------------------------------------------------------

$operationKey = Wait-L2Condition -Description 'the unload command is pending on the synthetic peer' `
    -Journal $journal -Criterion 'pending-unload' -TimeoutSeconds 30 `
    -Probe {
        $pending = @($onboard.Snapshot().body.pending | Where-Object { $_.messageType -eq 'SlotOperationCommand' })
        if ($pending.Count -gt 0) { [string]$pending[0].key } else { $null }
    } `
    -Until { param($v) -not [string]::IsNullOrEmpty($v) }
$journal.Note('The operator empties the slots; the unload result goes back.')
$null = $onboard.Command('Put', "answer/$operationKey", @{ completed = $true })

$stage = Wait-L2Condition -Description 'the journey completed after the unload' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 60 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'Completed' }
$gone = Wait-L2Condition -Description 'the journey leaves the waiting journey endpoint' `
    -Journal $journal -Criterion 'endpoint-waiting-journey-gone' -TimeoutSeconds 30 `
    -Probe { (Get-WaitingEntry -JourneyId $journeyId) ?? $notListed } -Until { param($v) $v -eq $notListed }
$assertions.Add(
    'L2-WJ-05', '卸货应答回来后旅程照常完成，端点不再列出它',
    ($stage -eq 'Completed' -and $gone -eq $notListed), 'Completed / (not listed)', "$stage / $gone")

$journal.Note('Scenario finished.')
