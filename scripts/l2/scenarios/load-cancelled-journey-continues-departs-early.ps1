#Requires -Version 7

<#
机台取货停靠取消后本站一篮没装、旅程继续时，车不等站点离站期限就走（control-server#742，CP-0015 方案 A）。

现场：agv03 10-17 多站旅程在 145、164 站取消装货之后，本站一条没装、旅程还要继续，车各白等了约 5 分钟才走。修之前服务端把
已终结的归属仍算在本站，离站等待只能等到期限；修之后本站归属全被终结、本站没有 Loaded 时，离站等待的期限就是它的起点，
判定与送到车上的期限读同一个值。

形状：需求甲在 12 号站（N1-3），需求乙在 11 号站（C15-13，车去第一站的路上追加进来）。乙进来之后关掉持货等单（见 setup）。
甲装上车，12 号站照旧等满离站期限（本站有 Loaded，#668 的纠错窗口护栏）；车到 11 号站，合成车载端把乙的录入请求挂着不答，
操作员扫码前取消乙，车随即离站去关卡卸甲。

判据：
1. 前置：乙进了同一趟旅程；新导入的参数版本里本区途中追加上限为空（持货等单不适用）。
2. 12 号站对照：甲已装上，离站核验的发出时刻不早于这一站车上清单的离站期限。
3. 取消走完：授权、ALL_EMPTY 结果被确认，乙 TERMINATED、甲仍 LOADED。
4. 11 号站离站核验在取消之后发出，发出时刻早于这一站的离站期限（第二个事实另等，README 第 14 条）。
5. 车上最新一版 11 号站清单不带一个晚于离站核验的期限：车走时屏上不剩倒计时。
6. 车到关卡卸完甲，旅程收尾。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Change.psm1') -Force
. (Join-Path $PSScriptRoot 'CargoHoldingCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$onboard = $Context.Onboard
$zone = 'MAP-25-WIRE_TO_GATE'

$a = New-L2CargoDemand 'A' 'N1-3' 1 $Context.RunId
$b = New-L2CargoDemand 'B' 'C15-13' 1 $Context.RunId

function ConvertTo-Instant($value) {
    if ($null -eq $value -or [string]$value -eq '') { return $null }
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]$value }
    return [DateTimeOffset]::Parse([string]$value, [Globalization.CultureInfo]::InvariantCulture)
}

function Format-Instant($value) {
    if ($null -eq $value) { return '(none)' }
    return $value.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
}

function Get-DemandStatuses([string]$JourneyId) {
    $map = @{}
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT DemandId, Status FROM JourneyDemands WHERE JourneyId = '$JourneyId'"
    foreach ($row in $rows) { $map[[string]$row.DemandId] = [string]$row.Status }
    return $map
}

# 一个站上发给车的每一版清单：号、条数、期限。按号排。
function Get-StationWorklists([string]$StationId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'CurrentStopWorklistSnapshot'")
    $list = foreach ($row in $rows) {
        $payload = ([string]$row.PayloadJson | ConvertFrom-Json -DateKind String).payload
        if ([string]$payload.stationId -ne $StationId) { continue }
        [pscustomobject]@{
            Revision = [long]$payload.worklistRevision
            Items    = @($payload.items).Count
            Deadline = ConvertTo-Instant $payload.stationDepartureDeadlineAt
        }
    }
    return , @($list | Sort-Object Revision)
}

function Format-Worklists([object[]]$Worklists) {
    return (@($Worklists) | ForEach-Object { "$($_.Revision)x$($_.Items)@$(Format-Instant $_.Deadline)" }) -join ' '
}

# 一个停靠的离站核验：服务端落进发件箱的那一刻（服务端自己的钟），没发就是 $null。
function Get-DepartureCheckQueuedAt([string]$StopId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT o.CreatedAt FROM JourneyStops s JOIN ProtocolOutbox o ON o.MessageId = s.DepartureSafetyCheckMessageId " +
        "WHERE s.StopId = '$StopId'")
    if ($rows.Count -eq 0) { return $null }
    return ConvertTo-Instant $rows[0].CreatedAt
}

function Get-PeerCancellation([string]$cancellationId) {
    $body = (Invoke-RestMethod -Uri "$($onboard.BaseUrl)/$($onboard.Prefix)/load-cancellations" -TimeoutSec 10).body
    return @($body.cancellations) | Where-Object { [string]$_.cancellationId -eq $cancellationId } | Select-Object -First 1
}

Initialize-L2CargoRig $Context

# --- 1. 甲受理，乙追加进同一趟旅程；然后关掉持货等单 -----------------------------------------------------

Publish-L2CargoDemand $Context $a
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey-a' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId
Publish-L2CargoDemand $Context $b
$joined = Wait-L2ConditionOrLast -Description 'demand B joined the journey' -Journal $journal -Criterion 'b-joined' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $b.Id } -Until { param($v) $null -ne $v }

$csv = Join-Path $Context.SnapshotRoot 'dispatch-zone-parameters-unconfigured.csv'
[IO.File]::WriteAllText($csv, "dispatch_zone,en_route_addition_max_path_cost_increase_mm,starvation_threshold_seconds`n$zone,,`n",
    [Text.UTF8Encoding]::new($false))
$imported = & $Context.InvokeFieldOps -Arguments @('import-dispatch-zone-parameters', '--input', $csv)
$current = Invoke-L2Query -Connection $connection -Sql (
    "SELECT p.EnRouteAdditionMaxPathCostIncrease AS Allowance FROM DispatchZoneParameters p " +
    "WHERE p.DispatchZone = '$zone' AND p.Version = (SELECT MAX(Version) FROM DispatchZoneParameterVersions)")
$assertions.Add(
    'L2-CDE-01', '前置：乙进了甲那一趟旅程；随后导入的参数版本里本区途中追加上限为空，持货等单不适用',
    ($null -ne $joined -and [string]$joined.JourneyId -eq $journeyId -and [string]$imported.outcome -eq 'OK' -and
        $current.Count -eq 1 -and ($null -eq $current[0].Allowance -or [string]$current[0].Allowance -eq '')),
    "journey $journeyId, OK, (unconfigured)",
    "journey $(if ($joined) { $joined.JourneyId } else { '(B not joined)' }), $($imported.outcome), " +
    "$(if ($current.Count -eq 0) { '(no row)' } else { "'$($current[0].Allowance)'" })")

# --- 2. 12 号站：甲装上，照旧等满离站期限（对照） -------------------------------------------------------------

$aStop = Move-L2CargoVehicleToCurrentStop $Context $journeyId $Context.PickupStationRiotId
$null = Wait-L2Condition -Description 'demand A was loaded' -Journal $journal -Criterion 'a-loaded' -TimeoutSeconds 120 `
    -Probe { (Get-DemandStatuses $journeyId)[$a.Id] } -Until { param($v) $v -eq 'LOADED' }

# 第二个取货站上没人扫码：录入请求挂着，乙由取消结束。
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Manual' })
$aDeparted = Wait-L2ConditionOrLast -Description 'the departure check of station 12 went out' -Journal $journal `
    -Criterion 'a-departure-check' -TimeoutSeconds 180 `
    -Probe { Get-DepartureCheckQueuedAt ([string]$aStop.StopId) } -Until { param($v) $null -ne $v }
$aStationId = [string](Invoke-L2Query -Connection $connection -Sql "SELECT StationId FROM JourneyStops WHERE StopId = '$($aStop.StopId)'")[0].StationId
$aWorklists = Get-StationWorklists $aStationId
$aDeadline = @($aWorklists | Where-Object { $null -ne $_.Deadline }) | Select-Object -Last 1 | ForEach-Object { $_.Deadline }
$assertions.Add(
    'L2-CDE-02', '12 号站对照：甲已装上，离站核验不早于这一站车上清单的离站期限（本站有 Loaded 时照旧等，#668）',
    ($null -ne $aDeparted -and $null -ne $aDeadline -and $aDeparted -ge $aDeadline),
    "departure check at or after $(Format-Instant $aDeadline)",
    "departure check $(Format-Instant $aDeparted); worklists $(Format-Worklists $aWorklists)")

# --- 3. 11 号站：乙的录入请求挂着，操作员扫码前取消 --------------------------------------------------------

$bStop = Move-L2CargoVehicleToCurrentStop $Context $journeyId 11
$bStopRow = Invoke-L2Query -Connection $connection -Sql (
    "SELECT StationId, OperationSessionId FROM JourneyStops WHERE StopId = '$($bStop.StopId)'")
$bStationId = [string]$bStopRow[0].StationId
$bKeyPrefix = "sublot:$([string]$bStopRow[0].OperationSessionId):"
$null = Wait-L2Condition -Description 'the entry request at station 11 is held on the peer' -Journal $journal `
    -Criterion 'entry-held' -TimeoutSeconds 60 `
    -Probe {
        @(@($onboard.Snapshot().body.pending) | Where-Object {
                [string]$_.messageType -eq 'SublotEntryRequested' -and ([string]$_.key).StartsWith($bKeyPrefix, [StringComparison]::Ordinal)
            }) | Select-Object -First 1
    } `
    -Until { param($v) $null -ne $v }
# 到站那一版清单带着这一站的离站期限：车到站时屏上那个倒计时，也是修之前车真正离站的时刻。
# 先落变量再筛：Get-StationWorklists 以 `return , @(...)` 返回，直接接管道时 $_ 是整个数组，数出来永远是 1。
$bArrivalWorklists = Get-StationWorklists $bStationId
$bArrivalWorklist = @($bArrivalWorklists | Where-Object { $_.Items -gt 0 -and $null -ne $_.Deadline }) | Select-Object -First 1
$bDeadline = if ($null -eq $bArrivalWorklist) { $null } else { $bArrivalWorklist.Deadline }
$journal.Observe('station-11-deadline', (Format-Instant $bDeadline), @{ deadline = (Format-Instant $bDeadline) })

$cancellationId = [guid]::NewGuid().ToString('D')
$journal.Note("Operator cancels demand B before any sublot entry ($cancellationId).")
$authorized = Wait-L2Change -Description 'the peer received the authorization for the cancellation' `
    -Journal $journal -Criterion 'b-authorization' -TimeoutSeconds 60 `
    -Baseline { [int](Invoke-L2Query -Connection $connection -Sql "SELECT COUNT(*) AS Total FROM RecoveryWorkflows WHERE DemandId = '$($b.Id)'")[0].Total } `
    -Action {
        $null = $onboard.Command('Put', "load-cancellations/$cancellationId", @{
            demandId               = $b.Id
            slotOperationAttemptId = $null
            reason                 = '现场确认本站没有要装的货。'
        })
    } `
    -Probe { Get-PeerCancellation $cancellationId } `
    -Until { param($before, $now) $now -and $null -ne $now.decision }
$acknowledged = Wait-L2ConditionOrLast -Description "the peer's ALL_EMPTY result was acknowledged" -Journal $journal `
    -Criterion 'b-result-acknowledged' -TimeoutSeconds 60 `
    -Probe { Get-PeerCancellation $cancellationId } `
    -Until { param($v) $v -and $null -ne $v.resultAcknowledgedAt }
$ended = Wait-L2ConditionOrLast -Description 'the cancellation ended demand B while the journey goes on' -Journal $journal `
    -Criterion 'b-ended' -TimeoutSeconds 60 `
    -Probe { Get-DemandStatuses $journeyId } -Until { param($v) $v[$b.Id] -eq 'TERMINATED' }
$assertions.Add(
    'L2-CDE-03', '取消走完：授权 AUTHORIZED、ALL_EMPTY 结果被确认；乙 TERMINATED，甲仍 LOADED',
    ([string]$authorized.Value.decision -eq 'AUTHORIZED' -and $null -ne $acknowledged -and
        [string]$acknowledged.resultOverallOutcome -eq 'ALL_EMPTY' -and $null -ne $acknowledged.resultAcknowledgedAt -and
        $ended[$b.Id] -eq 'TERMINATED' -and $ended[$a.Id] -eq 'LOADED'),
    'AUTHORIZED / ALL_EMPTY acknowledged / B TERMINATED, A LOADED',
    "$($authorized.Value.decision) / $(if ($acknowledged) { "$($acknowledged.resultOverallOutcome) at $($acknowledged.resultAcknowledgedAt)" } else { '(no result)' }) / " +
    "B $($ended[$b.Id]), A $($ended[$a.Id])")

# --- 4. 离站核验在期限之前发出：第二个事实，另等 -------------------------------------------------------------

# 乙的终结与离站核验不在同一次提交里：终结是结果受理那一次，核验是引擎下一轮的另一次写入（README 第 14 条）。所以另等它；
# 超时不抛，读到的最后一个值进判据表。等的上限放到期限之后 30 秒：修之前车按期限走，核验照样会发，判据看它的时刻而不是有没有。
$waitFor = if ($null -eq $bDeadline) { 60 } else { [int][Math]::Max(30, ($bDeadline - [DateTimeOffset]::UtcNow).TotalSeconds + 30) }
$bDeparted = Wait-L2ConditionOrLast -Description 'the departure check of station 11 went out' -Journal $journal `
    -Criterion 'b-departure-check' -TimeoutSeconds $waitFor `
    -Probe { Get-DepartureCheckQueuedAt ([string]$bStop.StopId) } -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-CDE-04', '11 号站本站空了、一篮没装：离站核验早于这一站的离站期限发出，车不再白等到期限（#742）',
    ($null -ne $bDeparted -and $null -ne $bDeadline -and $bDeparted -lt $bDeadline),
    "departure check before $(Format-Instant $bDeadline)",
    "departure check $(Format-Instant $bDeparted)")

$bWorklists = Get-StationWorklists $bStationId
$bLatest = @($bWorklists) | Select-Object -Last 1
$assertions.Add(
    'L2-CDE-05', '车上最新那一版 11 号站清单不带晚于离站核验的期限：车走时屏上不剩倒计时（判定与下发同一个期限）',
    ($null -ne $bLatest -and $bLatest.Items -eq 0 -and $null -ne $bDeparted -and
        ($null -eq $bLatest.Deadline -or $bLatest.Deadline -le $bDeparted)),
    "latest empty, deadline none or <= $(Format-Instant $bDeparted)",
    (Format-Worklists $bWorklists))

# --- 5. 车到关卡卸甲，旅程收尾 --------------------------------------------------------------------------

$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $Context.GateStationRiotId
$completed = Wait-L2ConditionOrLast -Description 'the journey completed after the gate unload' -Journal $journal `
    -Criterion 'completed' -TimeoutSeconds 120 `
    -Probe { Get-L2CargoJourney $connection $a.Id } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq 'Completed' }
$assertions.Add(
    'L2-CDE-06', '车开往下一站（关卡）照常卸完甲，旅程收尾',
    ($null -ne $completed -and [string]$completed.Stage -eq 'Completed'),
    'Completed', (Format-L2CargoJourney $completed))

$journal.Note('取消让 11 号站空了、一篮没装：车在离站期限之前就去了关卡；12 号站有已装的货，照旧等满期限。')
