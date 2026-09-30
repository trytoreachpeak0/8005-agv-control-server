#Requires -Version 7

<#
充电桩名册在运行中被置空、又重新启用（批次9-06，control-server#404；REQ-0171、REQ-0173，规格 8.6；用户 2026-09-29 定的并行期
隔离方式：v2 只在授权窗口里自动充电，窗口外名册置空）。

  1. 开窗：名册登记 211。A 的电量压到线下 → A 取得 CHARGING 用途占有与 211 的预占，充电单建成（周期 EN_ROUTE）。
  2. 关窗：同一个 FieldOps 动词导入空名册。A 已经在充电周期里，按它记下的名册版本原样继续——预占、周期、单号都不变，没有取消命令。
  3. B 的电量压到线下：名册是空的，B 进入服务端持有的人工充电等待（原因 ROSTER_EMPTY），服务端告警一次，B 的车载端收到
     manualChargingHold=true 的业务状态并确认。B 没有用途占有、没有预占、没有建单、不动。
  4. 再开窗：名册登记 211 与 213（213 空着）。B 仍在等待中，**不被分配**——名册重新启用不解除等待。这是一个要持续成立的状态，
     另等十几轮再读。
  5. B 的车载端发「充电后返回服务」（合成对端的控制面入口 PUT /manual-charging-returns/{requestId}）→ 等待解除（经过记录写上这个请求 id），
     B 收到 manualChargingHold=false，随后进队、取得 213 的预占与 CHARGING 用途占有。
  6. 整段里 A 的承诺逐字未变（第二个事实，用 Wait-L2ConditionOrLast 另等，不读一次就断言）。

红证据（缺陷版本）：去掉「名册里没有这辆车可用的桩时置人工充电等待」那一段（改成只答「没有可用的桩」）。B 静默停着：没有等待行、
没有告警、没有 manualChargingHold=true 的快照，L2-CRE-03 变红；名册一重新启用它就被直接分配，L2-CRE-04 也红。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Chargers.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$riot = $Context.Riot

$a = [pscustomobject]@{ AgvId = $Context.AgvId; VehicleKey = $Context.VehicleKey }
$b = [pscustomobject]@{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002' }
$bPeer = ($Context.OnboardPeers | Where-Object { $_.AgvId -eq $b.AgvId }).Double
$fleetKeys = [string[]]@($a.VehicleKey, $b.VehicleKey)
$firstCharger = @{ StationId = 211; StationName = '充电点1' }
$secondCharger = @{ StationId = 213; StationName = '充电点2' }
$lowBattery = 20
$serverLog = Join-Path $Context.LogRoot 'control-server.out.log'

function Read-SharedText([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { return [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
}

# Counted in SQL: a query with no rows comes back as $null, and @($null).Count is 1.
function Get-Count([string]$sql) { [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N }

function Set-Battery([string]$vehicleKey, [int]$battery) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Context.GateStationRiotId; battery = $battery
    })
}

function Import-Roster([object[]]$chargers, [string]$label, [string]$note) {
    $result = Invoke-L2ChargerRosterImport -Chargers $chargers -MapId $Context.MapId -Fleet $fleetKeys -Riot $riot `
        -InvokeFieldOps $Context.InvokeFieldOps -SnapshotRoot $Context.SnapshotRoot -Label $label -ChangeNote $note
    $journal.Observe("charger-roster:$label", [string]$result.version, @{ import = $result })
    return $result
}

# One vehicle's charging commitment as the database holds it, one line. Invoke-L2Query returns its rows as an array
# already; it is assigned as it comes, not wrapped again.
function Get-CommitmentOf([string]$vehicleKey) {
    $claims = Invoke-L2Query -Connection $connection -Sql (
        "SELECT Purpose, JourneyId FROM VehiclePurposeClaims WHERE VehicleKey = '$vehicleKey'")
    $stations = Invoke-L2Query -Connection $connection -Sql (
        "SELECT StationId, State, StationKind, JourneyId, ChargerRosterVersion FROM StationExclusivities WHERE VehicleKey = '$vehicleKey' ORDER BY StationId")
    $cycles = Invoke-L2Query -Connection $connection -Sql (
        "SELECT CycleId, StationId, ChargerRosterVersion, Phase, IFNULL(UpperId, '') AS UpperId FROM ChargingCycles WHERE VehicleKey = '$vehicleKey' ORDER BY CycleId")
    $riotOrders = @($riot.Snapshot().body.orders |
        Where-Object { $null -ne $_ -and [string]$_.appointVehicleKey -eq $vehicleKey } |
        ForEach-Object { [string]$_.upperId } | Sort-Object)
    return "claim[" + (($claims | ForEach-Object { "$($_.Purpose) $($_.JourneyId)" }) -join ',') + "] " +
        "station[" + (($stations | ForEach-Object { "$($_.StationId) $($_.State) $($_.StationKind) $($_.JourneyId) v$($_.ChargerRosterVersion)" }) -join ',') + "] " +
        "cycle[" + (($cycles | ForEach-Object { "$($_.CycleId) $($_.StationId) v$($_.ChargerRosterVersion) $($_.Phase) $($_.UpperId)" }) -join ',') + "] " +
        "riot[" + ($riotOrders -join ',') + "]"
}

function Get-CancelCommands {
    [int](@($riot.Snapshot().body.commandInvocations | Where-Object { $null -ne $_ }) | Measure-Object).Count
}

# The manualChargingHold flags of the business states the server has put in B's outbox, oldest first, with whether the
# vehicle acknowledged each: "true:acked false:acked".
function Get-HoldSnapshotsOf([string]$agvId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT json_extract(PayloadJson, '`$.payload.manualChargingHold') AS Hold, " +
        "json_extract(PayloadJson, '`$.payload.vehicleBusinessStateRevision') AS Revision, " +
        "json_extract(PayloadJson, '`$.payload.activePurpose') AS Purpose, " +
        "CASE WHEN AcknowledgedAt IS NULL THEN 'pending' ELSE 'acked' END AS Ack " +
        "FROM ProtocolOutbox WHERE MessageType = 'VehicleBusinessStateSnapshot' " +
        "AND json_extract(PayloadJson, '`$.agvId') = '$agvId' ORDER BY Revision")
    return ($rows | ForEach-Object { "$(if ([int]$_.Hold -eq 1) { 'true' } else { 'false' }):$(if ([string]$_.Purpose -eq '') { 'none' } else { $_.Purpose }):$($_.Ack)" }) -join ' '
}

# --- 0. 前置：两台车在关卡、电量充足；两个桩登记到假 RIoT；开窗（名册只有 211）-----------------------------------

Set-Battery $a.VehicleKey 80
Set-Battery $b.VehicleKey 80
$null = $riot.Command('Put', 'chargers', @{
    chargers = @(
        @{ mapId = $Context.MapId; stationId = $firstCharger.StationId; chargeIntervalSeconds = 60; chargePercentPerInterval = 1 },
        @{ mapId = $Context.MapId; stationId = $secondCharger.StationId; chargeIntervalSeconds = 60; chargePercentPerInterval = 1 })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$opened = Import-Roster @($firstCharger) 'open' 'L2 charging-registry-emptied-degrades-and-resumes: window opened, station 211'

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

# --- 1. A 需要充电：取得 211，充电单建成 -------------------------------------------------------------------

Set-Battery $a.VehicleKey $lowBattery
$aUpperId = Wait-L2Condition -Description 'A committed to charging and its order was confirmed by RIoT' `
    -Journal $journal -Criterion 'a-charge-order-confirmed' -TimeoutSeconds 180 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql (
            "SELECT UpperId FROM ChargingCycles WHERE VehicleKey = '$($a.VehicleKey)' AND WireState = 'EN_ROUTE' AND UpperId IS NOT NULL")
        if ($rows.Count -eq 0) { return $null }
        return [string]$rows[0].UpperId
    } `
    -Until { param($v) $null -ne $v }
$aJourney = [string](Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId FROM JourneyRuntimes WHERE VehicleKey = '$($a.VehicleKey)' AND JourneyId LIKE 'charging:%'"))[0].JourneyId
$aCycle = [string](Invoke-L2Query -Connection $connection -Sql (
        "SELECT CycleId FROM ChargingCycles WHERE VehicleKey = '$($a.VehicleKey)'"))[0].CycleId
$aHeld = "claim[CHARGING $aJourney] station[211 RESERVED CHARGER $aJourney v$($opened.version)] " +
    "cycle[$aCycle 211 v$($opened.version) ACTIVE $aUpperId] riot[$aUpperId]"
$aNow = Get-CommitmentOf $a.VehicleKey
$assertions.Add(
    'L2-CRE-01',
    '开窗（名册登记 211）后，电量低于强制充电线的 A 取得 CHARGING 用途占有与 211 的预占，充电单在 RIoT 上恰好一张；周期与预占记下的是这一版名册',
    ([string]$opened.outcome -eq 'OK' -and [int]$opened.entryCount -eq 1 -and $aNow -eq $aHeld),
    "OK / 1 / $aHeld",
    "$($opened.outcome) / $($opened.entryCount) / $aNow")

# --- 2. 关窗：导入空名册。A 的周期原样继续 -----------------------------------------------------------------

$closed = Import-Roster @() 'closed' 'L2 charging-registry-emptied-degrades-and-resumes: window closed, empty roster'
$assertions.Add(
    'L2-CRE-02',
    '关窗是同一个动词导入空名册：成一个新的空版本，并指出还有充电在进行、窗口还不能关',
    ([string]$closed.outcome -eq 'OK' -and [int]$closed.entryCount -eq 0 -and [bool]$closed.emptyRoster -and
        [long]$closed.version -gt [long]$opened.version -and $closed.windowCanClose -eq $false),
    "OK / 0 entries / emptyRoster / version > $($opened.version) / windowCanClose false",
    "$($closed.outcome) / $($closed.entryCount) entries / emptyRoster=$($closed.emptyRoster) / v$($closed.version) / windowCanClose $($closed.windowCanClose)")

# --- 3. B 需要充电而名册是空的：进入人工充电等待、告警、快照 -------------------------------------------------

Set-Battery $b.VehicleKey $lowBattery
$warning = "Vehicle $($b.AgvId) ($($b.VehicleKey)) is on manual charging hold: ROSTER_EMPTY"
$degraded = Wait-L2ConditionOrLast -Description 'B was put on manual charging hold, warned about and told' `
    -Journal $journal -Criterion 'b-held-warned-and-told' -TimeoutSeconds 90 `
    -Probe {
        $hold = Invoke-L2Query -Connection $connection -Sql (
            "SELECT Reason, CASE WHEN WarnedAt IS NULL THEN 'not warned' ELSE 'warned' END AS Warned FROM ManualChargingHolds " +
            "WHERE VehicleKey = '$($b.VehicleKey)'")
        $holdText = if ($hold.Count -eq 1) { "$($hold[0].Reason) $($hold[0].Warned)" } else { "$($hold.Count) holds" }
        $logged = ([regex]::Matches((Read-SharedText $serverLog), [regex]::Escape($warning))).Count
        "$holdText | $logged warnings | $(Get-HoldSnapshotsOf $b.AgvId)"
    } `
    -Until { param($v) $v -eq 'ROSTER_EMPTY warned | 1 warnings | true:none:acked' }
$bNothing = 'claim[] station[] cycle[] riot[]'
$bNow = Get-CommitmentOf $b.VehicleKey
$assertions.Add(
    'L2-CRE-03',
    '名册为空时需要充电的 B 进入服务端持有的人工充电等待（ROSTER_EMPTY），告警恰好一次，车载端收到并确认 manualChargingHold=true；' +
        'B 没有用途占有、预占、周期或建单',
    ($degraded -eq 'ROSTER_EMPTY warned | 1 warnings | true:none:acked' -and $bNow -eq $bNothing),
    "ROSTER_EMPTY warned | 1 warnings | true:none:acked / $bNothing",
    "$degraded / $bNow")

# --- 4. 再开窗：名册登记 211 与 213。B 在返回服务之前不被分配 -----------------------------------------------

$reopened = Import-Roster @($firstCharger, $secondCharger) 'reopened' 'L2 charging-registry-emptied-degrades-and-resumes: window reopened, stations 211 and 213'
# 「一直不被分配」：等满窗口（十几轮派车），读最后一次。213 空着、B 需要充电，挡住它的只有那份等待。
$stillHeld = "ROSTER_EMPTY | $bNothing | 1 warnings | true:none:acked"
$beforeReturn = Wait-L2ConditionOrLast -Description 'B was allocated a charger, or lost its hold, before returning to service (neither may happen)' `
    -Journal $journal -Criterion 'b-before-return-to-service' -TimeoutSeconds 15 `
    -Probe {
        $hold = Invoke-L2Query -Connection $connection -Sql "SELECT Reason FROM ManualChargingHolds WHERE VehicleKey = '$($b.VehicleKey)'"
        $holdText = if ($hold.Count -eq 1) { [string]$hold[0].Reason } else { "$($hold.Count) holds" }
        $logged = ([regex]::Matches((Read-SharedText $serverLog), [regex]::Escape($warning))).Count
        "$holdText | $(Get-CommitmentOf $b.VehicleKey) | $logged warnings | $(Get-HoldSnapshotsOf $b.AgvId)"
    } `
    -Until { param($v) $v -ne $stillHeld }
$assertions.Add(
    'L2-CRE-04',
    '名册重新启用（211、213，213 空着）之后，仍在等待中的 B 十几轮里不被分配、等待不被解除，告警与快照也不重复',
    ([string]$reopened.outcome -eq 'OK' -and [int]$reopened.entryCount -eq 2 -and $beforeReturn -eq $stillHeld),
    "OK / 2 / $stillHeld",
    "$($reopened.outcome) / $($reopened.entryCount) / $beforeReturn")

# --- 5. B 的车载端发「充电后返回服务」：等待解除，B 进队并取得 213 -------------------------------------------

$requestId = [guid]::NewGuid().ToString('D')
$sent = $bPeer.Command('Put', "manual-charging-returns/$requestId", @{
    administratorRole = 'MAINTENANCE_ADMINISTRATOR'; reason = 'L2: charged by hand'; observedBatteryPercent = $lowBattery
})
$journal.Observe('manual-charging-return', $requestId, @{ answer = $sent.body })
$decision = Wait-L2ConditionOrLast -Description 'the server decided the return to service and lifted the hold in the same save' `
    -Journal $journal -Criterion 'b-returned-to-service' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql (
            "SELECT Outcome FROM ManualChargingReturnToServiceRequests WHERE RequestId = '$requestId'")
        $record = Invoke-L2Query -Connection $connection -Sql (
            "SELECT IFNULL(ReleaseRequestId, '') AS ReleaseRequestId FROM ManualChargingHoldRecords WHERE VehicleKey = '$($b.VehicleKey)'")
        $holds = Get-Count "SELECT COUNT(*) AS N FROM ManualChargingHolds WHERE VehicleKey = '$($b.VehicleKey)'"
        "$(if ($rows.Count -eq 1) { $rows[0].Outcome } else { 'undecided' }) | $holds holds | " +
            "released by $(if ($record.Count -eq 1) { $record[0].ReleaseRequestId } else { "$($record.Count) records" })"
    } `
    -Until { param($v) $v -eq "RETURNED_TO_ELIGIBILITY_EVALUATION | 0 holds | released by $requestId" }

$bUpperId = Wait-L2ConditionOrLast -Description 'B, back in service and still below its line, committed to the free charger and its order was confirmed' `
    -Journal $journal -Criterion 'b-charge-order-confirmed' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql (
            "SELECT UpperId FROM ChargingCycles WHERE VehicleKey = '$($b.VehicleKey)' AND WireState = 'EN_ROUTE' AND UpperId IS NOT NULL")
        if ($rows.Count -eq 0) { return $null }
        return [string]$rows[0].UpperId
    } `
    -Until { param($v) $null -ne $v }
$bJourneyRows = Invoke-L2Query -Connection $connection -Sql (
    "SELECT JourneyId FROM JourneyRuntimes WHERE VehicleKey = '$($b.VehicleKey)' AND JourneyId LIKE 'charging:%'")
$bJourney = if ($bJourneyRows.Count -eq 1) { [string]$bJourneyRows[0].JourneyId } else { "($($bJourneyRows.Count) journeys)" }
$bCycleRows = Invoke-L2Query -Connection $connection -Sql "SELECT CycleId FROM ChargingCycles WHERE VehicleKey = '$($b.VehicleKey)'"
$bCycle = if ($bCycleRows.Count -eq 1) { [string]$bCycleRows[0].CycleId } else { "($($bCycleRows.Count) cycles)" }
$bHeld = "claim[CHARGING $bJourney] station[213 RESERVED CHARGER $bJourney v$($reopened.version)] " +
    "cycle[$bCycle 213 v$($reopened.version) ACTIVE $bUpperId] riot[$bUpperId]"
$bAfter = Get-CommitmentOf $b.VehicleKey
# 先撤下等待（false、没有用途），之后才是充电旅程的那几张（CHARGING）：按修订号排，前两张必须是 true、false。
$bSnapshots = Wait-L2ConditionOrLast -Description "B's vehicle acknowledged the snapshot taking the hold off" `
    -Journal $journal -Criterion 'b-told-hold-is-off' -TimeoutSeconds 30 `
    -Probe { (Get-HoldSnapshotsOf $b.AgvId) } `
    -Until { param($v) $v.StartsWith('true:none:acked false:none:acked') }
$assertions.Add(
    'L2-CRE-05',
    '「充电后返回服务」受理时同一次保存解除等待（经过记录写上这个请求 id）；B 收到并确认 manualChargingHold=false，随后取得空着的 213 与 CHARGING 用途占有',
    ($decision -eq "RETURNED_TO_ELIGIBILITY_EVALUATION | 0 holds | released by $requestId" -and $bAfter -eq $bHeld -and
        $bSnapshots.StartsWith('true:none:acked false:none:acked') -and -not $bSnapshots.Contains('true:CHARGING')),
    "RETURNED_TO_ELIGIBILITY_EVALUATION | 0 holds | released by $requestId / $bHeld / true:none:acked false:none:acked ...",
    "$decision / $bAfter / $bSnapshots")

# --- 6. 第二个事实：A 的承诺整段未变 -----------------------------------------------------------------------

$aLater = Wait-L2ConditionOrLast -Description "A's commitment changed, or an order command went out (neither may happen)" `
    -Journal $journal -Criterion 'a-commitment-after-window' -TimeoutSeconds 12 `
    -Probe { "$(Get-CommitmentOf $a.VehicleKey) | $(Get-CancelCommands) commands" } `
    -Until { param($v) $v -ne "$aHeld | 0 commands" }
$assertions.Add(
    'L2-CRE-06',
    'A 的周期在名册置空、重新启用的整段里原样继续：同一趟、同一个桩、同一个单号，仍按它记下的那一版名册，没有任何取消命令',
    ($aLater -eq "$aHeld | 0 commands"),
    "$aHeld | 0 commands",
    $aLater)

$journal.Note('名册置空：在途的周期原样继续，需要充电的车进人工充电等待并告警；重新启用后，等待中的车要先经「充电后返回服务」才回到自动充电。')
