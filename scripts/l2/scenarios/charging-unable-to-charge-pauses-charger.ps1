#Requires -Version 7

<#
一台车到桩充不上（批次9-08，control-server#406；REQ-0174、REQ-0177、REQ-0179、REQ-0180、REQ-0284）：低电 → 分配 211、建单 → 单在桩上执行开始充电，
假 RIoT 返回 407802、单停在 HANG（9），全程不报 CHARGING → 服务端以一条不可变的暂停事件暂停 211、车进清桩中、车留在原地 → 有人把车挪开、
经车载端确认清桩 → 旧单在 RIoT 里结束的那一轮，211 的独占释放、暂停仍在 → 车回到统一电量队列，唯一的桩暂停着，排队并告警「无合格桩」。

**装置**：单车停在关卡 210（节点 5）。场景把站 211 登记成假 RIoT 的充电桩、经 FieldOps 导入名册。编排器的默认测试策略：强制充电线 30。
服务端的 R-11／R-13 名单里只有 L2-R11（装置文件的 FieldOperatorRoles）。

**第一个事实（形成确认）**：那张充电单被注入 CannotCharge，推到完成——假 RIoT 把它挂在 9、开始充电的动作带 407802；车停在 211 上。服务端两次相隔不远的
新鲜观测都看到同一组事实，写下一条 UNABLE_TO_CHARGE_CONFIRMED 的暂停（根因 UNKNOWN），周期 UNABLE_TO_CHARGE／CLEARING，用途 CLEARING_MAINTENANCE，
旅程 CHARGING_UNABLE_TO_CHARGE。

**第二个事实（车保持原位、原桩不重试）另等**：确认之后另等十秒，用等待的返回值断言没有任何新的订单意图、RIoT 上仍只有那一张单、桩仍是这一趟的、
用途仍是 CLEARING_MAINTENANCE——不读一次就断言（scripts/l2/README.md 第 14 条）。

**人工清桩**：车被挪回 210；合成车载端以 L2-R11 发 ManualStationClearanceConfirmationRequested。旧单还 HANG：CONFIRMED、stationReleased=false、桩不放。
清桩还没完成（REQ-0178）。取消开关默认关：服务端不发 CMD_ORDER_CANCEL（另等八秒，RIoT 一侧与命令审计都是 0），桩仍不放。
然后旧单在 RIoT 里被取消（真车上是人去取消）：清桩在这一轮完成，这一轮释放 211（CHARGER_RELEASED_ON_MANUAL_CLEARANCE）、周期以
CHARGING_UNABLE_TO_CHARGE_CLEARED 结束、旅程收尾，暂停没有恢复行。

**清桩之后**：车仍低电，另等十秒：不进人工充电等待、没有第二个充电周期，服务端日志里有「211=CHARGER_ALLOCATION_HELD」的排队告警。

**红证据（缺陷版本）**：分配不看桩的暂停（原桩重试不为 0），清桩之后车又被派回 211，L2-UTC-05 变红。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SingleRow.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ReadSnapshot.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Chargers.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$riot = $Context.Riot
$onboard = $Context.Onboard

$charger = 211
$chargerName = '充电点1'
$vehicleKey = $Context.VehicleKey
$serverLog = Join-Path $Context.LogRoot 'control-server.out.log'

function Read-SharedText([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { return [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
}

# Counted in SQL, so there is always exactly one row to read. Invoke-L2Query hands the whole result set back as one array
# (README, "whole-array return"); counting rows on the PowerShell side would have to step around that.
function Get-Count([string]$sql) { [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N }

function Get-Cycle {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, WireState, Phase, IFNULL(UpperId, '') AS UpperId, IFNULL(EndReason, '') AS EndReason " +
        "FROM ChargingCycles")
}

# Who holds the charger, as one line: "<state> <vehicle> <journey>", or "(none)".
function Get-ChargerHeld {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT State, VehicleKey, JourneyId FROM StationExclusivities WHERE MapId = $($Context.MapId) AND StationId = $charger")
    if ($null -eq $row) { return '(none)' }
    return "$($row.State) $($row.VehicleKey) $($row.JourneyId)"
}

function Get-Claim {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT Purpose, JourneyId FROM VehiclePurposeClaims WHERE VehicleKey = '$vehicleKey'")
    if ($null -eq $row) { return '(none)' }
    return "$($row.Purpose) $($row.JourneyId)"
}

# The holds on the charger, as one line: "<trigger> <root cause> <recovered?>" per hold, or "(none)".
function Get-Holds {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT h.""Trigger"" AS Trig, h.RootCause, (SELECT COUNT(*) FROM ChargingStationRecoveries r WHERE r.HoldId = h.HoldId) AS Recovered " +
        "FROM ChargingStationAllocationHolds h WHERE h.MapId = $($Context.MapId) AND h.StationId = $charger")
    if ($rows.Count -eq 0) { return '(none)' }
    return (@($rows | ForEach-Object { "$($_.Trig) $($_.RootCause) recovered=$($_.Recovered)" }) -join '; ')
}

function Get-RiotOrders {
    @($riot.Snapshot().body.orders | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_.upperId })
}

function Set-Vehicle([int]$position) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $position; battery = 25
    })
}

# --- 0. 前置：桩登记到假 RIoT 与名册，路网起来 ------------------------------------------------------------------

$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{
        mapId = $Context.MapId; stationId = $charger; chargeIntervalSeconds = 1; chargePercentPerInterval = 5
    })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet @($vehicleKey) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 charging-unable-to-charge-pauses-charger: station $charger"
$journal.Observe('charger-roster', [string]$roster.version, @{ import = $roster })

$null = Wait-L2Condition -Description 'the route graph engine finished a refresh cycle' `
    -Journal $journal -Criterion 'route-graph-ready' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql ("SELECT DesignEdgeCount, RuntimeRefreshedAt, StaleReason FROM RouteGraphSnapshots " +
                "WHERE MapId = $($Context.MapId)")
        if ($rows.Count -eq 0) { return $false }
        return [int]$rows[0].DesignEdgeCount -gt 0 -and
            $null -ne $rows[0].RuntimeRefreshedAt -and [string]$rows[0].RuntimeRefreshedAt -ne '' -and
            ($null -eq $rows[0].StaleReason -or [string]$rows[0].StaleReason -eq '')
    } `
    -Until { param($v) $v }

# --- 1. 低电：分配、建单 -----------------------------------------------------------------------------------------

Set-Vehicle $Context.GateStationRiotId
$upperId = Wait-L2Condition -Description 'the charge order was confirmed by RIoT' `
    -Journal $journal -Criterion 'charge-order-confirmed' -TimeoutSeconds 120 `
    -Probe {
        $cycle = Get-Cycle
        if ($null -eq $cycle -or [string]$cycle.WireState -ne 'EN_ROUTE' -or [string]$cycle.UpperId -eq '') { return $null }
        return [string]$cycle.UpperId
    } `
    -Until { param($v) $null -ne $v }
$journeyId = [string](Get-Cycle).JourneyId

# --- 2. 到桩、开始充电失败：407802 + HANG，全程不报 CHARGING --------------------------------------------------------

$null = $riot.Command('Put', 'charging/faults', @{ upperId = $upperId; startOutcome = 'CannotCharge' })
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $vehicleKey })
$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })
Set-Vehicle $charger
$hung = @($riot.Snapshot().body.orders | Where-Object { $null -ne $_ -and [string]$_.upperId -eq $upperId })[0]
$journal.Observe('order-after-start-charging', "$($hung.orderState)", @{ order = $hung })

# One read snapshot (control-server#510): the pause, the cycle, the purpose and the journey's code are one SaveChanges, but read
# as four statements the cycle came back from before that commit and the rest from after it (CI run 37595371819).
# Deliberately waits only for the pause, and compares the whole line: inside one read snapshot, the first read that shows the pause
# shows everything committed with it. That pins the premise this criterion rests on -- the pause, the cycle, the purpose and the code are one SaveChanges (ConfirmUnableToChargeAsync) -- so splitting
# that commit in the product turns this red, where waiting for the whole line would wait out the split and stay green
# (control-server#510, review S1).
$expectedConfirmed = "UNABLE_TO_CHARGE_CONFIRMED UNKNOWN recovered=0 | UNABLE_TO_CHARGE CLEARING | CLEARING_MAINTENANCE $journeyId | CHARGING_UNABLE_TO_CHARGE"
$confirmed = Wait-L2ConditionOrLast -Description 'the server confirmed the vehicle cannot charge and paused the charger' `
    -Journal $journal -Criterion 'unable-to-charge-confirmed' -TimeoutSeconds 60 `
    -Probe {
        Invoke-L2ReadSnapshot -Connection $connection -Read {
            $cycle = Get-Cycle
            $journey = Read-L2SingleRow -Connection $connection -Sql (
                "SELECT IFNULL(BlockReasonCode, '') AS Code FROM JourneyRuntimes WHERE JourneyId = '$journeyId'")
            "$(Get-Holds) | $(${cycle}?.WireState) $(${cycle}?.Phase) | $(Get-Claim) | $(${journey}?.Code)"
        }
    } `
    -Until { param($v) $v.StartsWith('UNABLE_TO_CHARGE_CONFIRMED') }
$assertions.Add(
    'L2-UTC-01',
    '单在 211 上执行开始充电、RIoT 返回 407802 且停在 HANG（9）、全程不报 CHARGING：一条 UNABLE_TO_CHARGE_CONFIRMED 的暂停（根因 UNKNOWN），周期 UNABLE_TO_CHARGE／CLEARING，用途 CLEARING_MAINTENANCE，旅程 CHARGING_UNABLE_TO_CHARGE',
    ($confirmed -eq $expectedConfirmed -and [int]$hung.orderState -eq 9),
    "$expectedConfirmed / order 9",
    "$confirmed / order $($hung.orderState)")

# --- 3. 第二个事实：车保持原位、原桩不重试（另等） ---------------------------------------------------------------

$steady = "1 intents | $upperId | RESERVED $vehicleKey $journeyId | CLEARING_MAINTENANCE $journeyId | 1 holds | 0 order commands"
$stays = Wait-L2ConditionOrLast -Description 'a new order or a release appeared while clearing (none may)' `
    -Journal $journal -Criterion 'stays-while-clearing' -TimeoutSeconds 10 `
    -Probe {
        "$(Get-Count "SELECT COUNT(*) AS N FROM OrderIntents WHERE VehicleKey = '$vehicleKey'") intents | " +
            "$((Get-RiotOrders) -join ',') | $(Get-ChargerHeld) | $(Get-Claim) | " +
            "$(Get-Count "SELECT COUNT(*) AS N FROM ChargingStationAllocationHolds") holds | " +
            "$(Get-Count "SELECT COUNT(*) AS N FROM RiotOrderCommandAudit") order commands"
    } `
    -Until { param($v) $v -ne $steady }
$assertions.Add(
    'L2-UTC-02',
    '确认之后另等十秒：没有任何新的订单意图、RIoT 上仍只有那一张充电单、211 仍是这一趟的、用途仍是 CLEARING_MAINTENANCE、只有一条暂停、命令审计里一条订单命令都没有（车保持原位，原桩重试为 0，取消开关默认关）',
    ($stays -eq $steady),
    $steady,
    $stays)

# --- 4. 人工清桩：旧单还 HANG 时确认，不放 --------------------------------------------------------------------------

Set-Vehicle $Context.GateStationRiotId
$firstId = [guid]::NewGuid().ToString('D')
$sent = $onboard.Command('Put', "station-clearances/$firstId", @{ stationId = $chargerName; operatorId = 'L2-R11' })
$journal.Observe('station-clearance-sent', $firstId, @{ answer = $sent.body })
$whileHung = Wait-L2Condition -Description 'the server decided the clearance confirmation' `
    -Journal $journal -Criterion 'clearance-decided-while-hung' -TimeoutSeconds 30 `
    -Probe {
        $row = Read-L2SingleRow -Connection $connection -Sql (
            "SELECT Outcome, StationReleased FROM ManualStationClearanceConfirmations WHERE ConfirmationRequestId = '$firstId'")
        $clearance = Read-L2SingleRow -Connection $connection -Sql (
            "SELECT IFNULL(ConfirmedBy, '') AS ConfirmedBy, IFNULL(OldOrderDisposition, '') AS Disposition, " +
            "CASE WHEN CompletedAt IS NULL THEN 'open' ELSE 'completed' END AS Completion FROM StationClearances")
        if ($null -eq $row) { return $null }
        "$($row.Outcome) released=$($row.StationReleased) | $($clearance.ConfirmedBy) $($clearance.Disposition) $($clearance.Completion) | $(Get-ChargerHeld)"
    } `
    -Until { param($v) $null -ne $v }
$expectedWhileHung = "CONFIRMED released=0 | L2-R11 HANG open | RESERVED $vehicleKey $journeyId"
$assertions.Add(
    'L2-UTC-03',
    '车被挪开后，L2-R11 经车载端确认清桩；旧单还停在 HANG：CONFIRMED、stationReleased=false，清桩记录写上确认人与此刻的旧单处置 HANG、清桩未完成（REQ-0178），211 不放',
    ($whileHung -eq $expectedWhileHung),
    $expectedWhileHung,
    $whileHung)

# --- 4b. 取消开关默认关：确认之后旧单仍 HANG，服务端不发 CMD_ORDER_CANCEL，桩仍不放（另等） ------------------------------

# Off by default until a cancel of a HANG charge order has been measured on a real vehicle (review of control-server#406):
# the old order is a person's to end in RIoT. Read on both sides: the fake RIoT's calls and the server's own command audit.
function Get-CancelCalls {
    $calls = @(@($riot.Snapshot().body.commandInvocations) |
        Where-Object { $null -ne $_ -and [string]$_.commandType -eq 'CMD_ORDER_CANCEL' })
    "$($calls.Count) cancels | $(Get-Count "SELECT COUNT(*) AS N FROM RiotOrderCommandAudit") audited | $(Get-ChargerHeld)"
}
$noCancel = "0 cancels | 0 audited | RESERVED $vehicleKey $journeyId"
$cancelSteady = Wait-L2ConditionOrLast -Description 'a cancel or a release appeared before the old order ended (none may)' `
    -Journal $journal -Criterion 'old-order-not-cancelled-by-default' -TimeoutSeconds 8 `
    -Probe { Get-CancelCalls } `
    -Until { param($v) $v -ne $noCancel }
$assertions.Add(
    'L2-UTC-03b',
    '取消开关默认关：确认之后旧单仍 HANG，另等八秒，服务端没有发 CMD_ORDER_CANCEL（RIoT 一侧与命令审计都是 0），211 仍是这一趟的（清桩未完成）',
    ($cancelSteady -eq $noCancel),
    $noCancel,
    $cancelSteady)

# --- 5. 旧单在 RIoT 里结束的那一轮：放 211，暂停仍在 ----------------------------------------------------------------

$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 2 })
# One read snapshot (control-server#510): the cycle is read first and the release after it, so a release committed between the
# two came back next to the cycle from before it. The wait stays what it was: the release and the journey's closing are two
# saves by design (CloseClearedChargingAsync), so it waits for the later one, and the snapshot makes everything else it reads
# at least as new.
$expectedReleased = '(none) | CHARGER_RELEASED_ON_MANUAL_CLEARANCE | ENDED CHARGING_UNABLE_TO_CHARGE_CLEARED | ' +
    'Completed CHARGING_UNABLE_TO_CHARGE_CLEARED | (none) | UNABLE_TO_CHARGE_CONFIRMED UNKNOWN recovered=0 | completed CANCELLED'
$released = Wait-L2ConditionOrLast -Description 'the charger was released once the old order ended' `
    -Journal $journal -Criterion 'released-after-old-order-ended' -TimeoutSeconds 60 `
    -Probe {
        Invoke-L2ReadSnapshot -Connection $connection -Read {
            $cycle = Get-Cycle
            $record = Read-L2SingleRow -Connection $connection -Sql (
                "SELECT IFNULL(ReleaseReason, '') AS Reason FROM StationExclusivityRecords " +
                "WHERE MapId = $($Context.MapId) AND StationId = $charger AND JourneyId = '$journeyId'")
            $journey = Read-L2SingleRow -Connection $connection -Sql (
                "SELECT Stage, IFNULL(BlockReasonCode, '') AS Code FROM JourneyRuntimes WHERE JourneyId = '$journeyId'")
            $clearance = Read-L2SingleRow -Connection $connection -Sql (
                "SELECT IFNULL(OldOrderDisposition, '') AS Disposition, " +
                "CASE WHEN CompletedAt IS NULL THEN 'open' ELSE 'completed' END AS Completion FROM StationClearances")
            "$(Get-ChargerHeld) | $(${record}?.Reason) | $(${cycle}?.Phase) $(${cycle}?.EndReason) | " +
                "$(${journey}?.Stage) $(${journey}?.Code) | $(Get-Claim) | $(Get-Holds) | " +
                "$(${clearance}?.Completion) $(${clearance}?.Disposition)"
        }
    } `
    -Until { param($v) $v.StartsWith('(none)') -and $v.Contains('Completed') }
$assertions.Add(
    'L2-UTC-04',
    '旧单在 RIoT 里结束的那一轮：清桩在这一刻完成（旧单处置 CANCELLED）、211 的独占释放（CHARGER_RELEASED_ON_MANUAL_CLEARANCE）、周期以 CHARGING_UNABLE_TO_CHARGE_CLEARED 结束、旅程收尾、用途放开；暂停仍在（没有恢复）',
    ($released -eq $expectedReleased),
    $expectedReleased,
    $released)

# --- 6. 清桩之后：车回到队里、唯一的桩暂停着 → 排队告警，不进人工充电等待（另等） --------------------------------

$warning = "$charger=CHARGER_ALLOCATION_HELD"
$afterwards = Wait-L2ConditionOrLast -Description 'the vehicle was given a charger or put on manual hold after the clearance (neither may)' `
    -Journal $journal -Criterion 'queued-with-the-only-charger-paused' -TimeoutSeconds 10 `
    -Probe {
        "$(Get-Count "SELECT COUNT(*) AS N FROM ChargingCycles") cycles | " +
            "$(Get-Count "SELECT COUNT(*) AS N FROM ManualChargingHolds") manual holds | $(Get-ChargerHeld)"
    } `
    -Until { param($v) $v -ne '1 cycles | 0 manual holds | (none)' }
$logged = ([regex]::Matches((Read-SharedText $serverLog), [regex]::Escape($warning))).Count
$assertions.Add(
    'L2-UTC-05',
    '清桩之后车仍低电：另等十秒，没有第二个充电周期、211 没被谁取得、不进人工充电等待；服务端以 211=CHARGER_ALLOCATION_HELD 排队告警（无合格桩）',
    ($afterwards -eq '1 cycles | 0 manual holds | (none)' -and $logged -ge 1),
    "1 cycles | 0 manual holds | (none) / warning '$warning' logged",
    "$afterwards / warning logged $logged times")

$journal.Note('充不上：暂停这个桩、车留在原地，人工清桩确认、旧单结束之后才放桩，暂停一直在；车回到队里排队告警。')
