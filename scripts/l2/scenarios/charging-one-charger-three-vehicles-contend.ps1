#Requires -Version 7

<#
一个充电桩、三台都需要充电的车：电量最低的那台取得充电用途与桩的预占，另两台留在队里；之后队里一台的电量降到比已预占那台更低，
也抢不走（批次9-06，control-server#404；REQ-0172、REQ-0173。规格 8.3、8.5「1 桩 3 车争用、充满让桩」的「争用」一半，
「充满让桩」一半在批次9-07 的场景里）。

**装置**：三台车停在关卡（站 210，节点 5）。站 211「充电点1」在节点 6，场景把它登记成假 RIoT 的充电桩，并经 FieldOps 的
import-charger-roster 导入名册（与现场开窗用的是同一个动词）。

**怎么让三台车在同一轮一起进队**：先把电量设成 50／45／40——都高于默认测试策略的强制充电线 30，谁也不需要充电；再经 FieldOps 导入、批准、
**激活**一版强制充电线 60 的策略。激活是一次提交，三台车从下一轮起同时落到线下。电量最低的是名册里排最后的那台（0003，40%）：
服务端按名册次序为每台车读策略，激活即使正好落在两次读之间，读到新策略的车里也一定有它；而它排最后，所以「按名册次序分」的实现分不到它。

**第一个事实**：恰好一台车持有 CHARGING 用途占有，同一趟预占着 211，它是电量最低的那台；另两台没有任何用途占有、站点独占或人工充电等待，
各自报「211 已被预占」。「恰好一台」要持续成立：等到第一条承诺之后另等十几秒（十几轮派车），读最后一次。

**建单与进桩**：那一张单在假 RIoT 上恰好一张，单号就是充电周期派生的那个。把它推到完成（5），车在 211 上报 CHARGING——此后整段
持续断言「211 上 docked 的车不超过 1 辆」（假 RIoT 不判占桩互斥，服务端若把两台车派到同一个桩，两台都会报 CHARGING；调度 09-29）。

**第二个事实**：把队里的 0001 压到 10%，比已预占那台更低。等它**在电量变化之后的某一轮**报出排队原因（服务端日志里那一行带着电量，
所以认得出是变化之后的结论），用这次等待自己的返回值断言已预占那台的用途占有、预占、周期与单号逐字未变——不读一次就断言。

**红证据（缺陷版本）**：把「电量优先只在还没有预占的车之间比」改成对全体重排——电量更低的车评估到一个被电量更高的车预占着的桩时，
把那份预占放掉。0001 压到 10% 之后下一轮，0003 的预占就没了，L2-COC-04 变红。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SingleRow.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Chargers.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$riot = $Context.Riot

$charger = 211
$chargerName = '充电点1'
$first = [pscustomobject]@{ AgvId = $Context.AgvId; VehicleKey = $Context.VehicleKey; Battery = 50 }
$second = [pscustomobject]@{ AgvId = 'AGV-L2-002'; VehicleKey = 'BROKERX-L2-0002'; Battery = 45 }
$lowest = [pscustomobject]@{ AgvId = 'AGV-L2-003'; VehicleKey = 'BROKERX-L2-0003'; Battery = 40 }
$vehicles = @($first, $second, $lowest)
$fleetKeys = [string[]]@($vehicles | ForEach-Object { $_.VehicleKey })
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

# The charging commitments as the database holds them, one line: who claims CHARGING, who holds which charger, the cycles
# that have not ended, the charge intents, and what RIoT was asked to do. Invoke-L2Query returns its rows as an array
# already; it is assigned as it comes, not wrapped again.
function Get-Commitments {
    $claims = Invoke-L2Query -Connection $connection -Sql (
        "SELECT VehicleKey, JourneyId FROM VehiclePurposeClaims WHERE Purpose = 'CHARGING' ORDER BY VehicleKey")
    $stations = Invoke-L2Query -Connection $connection -Sql (
        "SELECT StationId, State, VehicleKey, JourneyId FROM StationExclusivities WHERE StationKind = 'CHARGER' ORDER BY StationId")
    $cycles = Invoke-L2Query -Connection $connection -Sql (
        "SELECT VehicleKey, CycleId, StationId, IFNULL(UpperId, '') AS UpperId FROM ChargingCycles WHERE Phase <> 'ENDED' ORDER BY VehicleKey")
    $intents = Invoke-L2Query -Connection $connection -Sql (
        "SELECT VehicleKey, UpperId, OrderShape, DestinationStationId FROM OrderIntents WHERE Purpose = 'TO_CHARGER' ORDER BY UpperId")
    $snapshot = $riot.Snapshot().body
    $riotOrders = @($snapshot.orders | Where-Object { $null -ne $_ } | ForEach-Object { [string]$_.upperId } | Sort-Object)
    $commands = [int](@($snapshot.commandInvocations | Where-Object { $null -ne $_ }) | Measure-Object).Count
    return "claims[" + (($claims | ForEach-Object { "$($_.VehicleKey)=$($_.JourneyId)" }) -join ',') + "] " +
        "chargers[" + (($stations | ForEach-Object { "$($_.StationId) $($_.State) $($_.VehicleKey) $($_.JourneyId)" }) -join ',') + "] " +
        "cycles[" + (($cycles | ForEach-Object { "$($_.VehicleKey) $($_.CycleId) $($_.StationId) $($_.UpperId)" }) -join ',') + "] " +
        "intents[" + (($intents | ForEach-Object { "$($_.VehicleKey) $($_.UpperId) $($_.OrderShape) $($_.DestinationStationId)" }) -join ',') + "] " +
        "riot[" + ($riotOrders -join ',') + "] commands[$commands]"
}

# How many vehicles the fake RIoT has docked on the charger right now, and the most it has ever been in this run.
$script:mostDocked = 0
function Get-Docked {
    $docked = @($riot.Snapshot().body.charging.vehicles |
        Where-Object { $null -ne $_ -and [bool]$_.docked -and [int]$_.chargerStationId -eq $charger } |
        ForEach-Object { [string]$_.vehicleKey })
    if ($docked.Count -gt $script:mostDocked) { $script:mostDocked = $docked.Count }
    return $docked
}

function Get-QueuedLine([string]$agvId, [int]$battery) {
    "Charging not allocated for vehicle ${agvId}: CHARGING_NO_CHARGER_AVAILABLE. battery=$battery; $charger=CHARGER_RESERVED_OR_OCCUPIED"
}

# --- 0. 前置：三台车在关卡、电量都在默认强制充电线之上；桩登记到假 RIoT 与名册 --------------------------------

foreach ($vehicle in $vehicles) { Set-Battery $vehicle.VehicleKey $vehicle.Battery }

$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{ mapId = $Context.MapId; stationId = $charger; chargeIntervalSeconds = 60; chargePercentPerInterval = 1 })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet $fleetKeys -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "L2 charging-one-charger-three-vehicles-contend: station $charger"
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

# 此刻谁也不需要充电：几轮之后仍然没有任何充电承诺或人工充电等待。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$before = Get-Commitments
$holdsBefore = Get-Count 'SELECT COUNT(*) AS N FROM ManualChargingHolds'
$assertions.Add(
    'L2-COC-00',
    '前置：名册经 FieldOps 导入一个桩（211）；三台车电量都在默认强制充电线之上时，没有任何充电承诺、建单或人工充电等待',
    ([string]$roster.outcome -eq 'OK' -and [int]$roster.entryCount -eq 1 -and
        $before -eq 'claims[] chargers[] cycles[] intents[] riot[] commands[0]' -and $holdsBefore -eq 0),
    'OK / 1 / claims[] chargers[] cycles[] intents[] riot[] commands[0] / 0 holds',
    "$($roster.outcome) / $($roster.entryCount) / $before / $holdsBefore holds")

# --- 1. 激活强制充电线 60 的策略：三台车同时落到线下 ----------------------------------------------------------

$policy = [ordered]@{
    minimumPostTaskBatteryMarginPercent  = 20
    mandatoryChargeEntryThresholdPercent = 60
    chargingCompletionThresholdPercent   = 80
    estimatedTaskConsumptionPercent      = 0
    progressStabilizationSeconds         = 180
    progressObservationWindowSeconds     = 600
    progressMinimumIncreasePercent       = 3
    vehicleScope                         = @()
    changeNote                           = 'L2 charging-one-charger-three-vehicles-contend: entry 60, so 50 / 45 / 40 % all need charging at once'
}
$policyFile = Join-Path $Context.SnapshotRoot 'entry-60-charging-policy.json'
[IO.File]::WriteAllText($policyFile, ($policy | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
$fleetText = $fleetKeys -join ';'
$imported = & $Context.InvokeFieldOps -Arguments @('import-charging-policy', '--input', $policyFile, '--fleet', $fleetText)
$version = [string]$imported.version
$approved = & $Context.InvokeFieldOps -Arguments @('approve-charging-policy', '--version', $version, '--approved-by', 'L2 scenario',
    '--role', 'L2_PRESET', '--basis', 'scripts/l2/scenarios/charging-one-charger-three-vehicles-contend.ps1', '--source', 'L2_PRESET')
$activated = & $Context.InvokeFieldOps -Arguments @('activate-charging-policy', '--version', $version, '--activated-by', 'L2 scenario',
    '--fleet', $fleetText, '--allow-non-field-approval')
$journal.Observe('scenario-policy', $version, @{ import = $imported; approve = $approved; activate = $activated })
if ([string]$imported.outcome -ne 'OK' -or [string]$approved.outcome -ne 'OK' -or [string]$activated.outcome -ne 'OK') {
    throw "The scenario's charging policy was not activated: $($imported.outcome)/$($approved.outcome)/$($activated.outcome)."
}

# --- 2. 第一个事实：恰好一台取得，是电量最低的那台 -----------------------------------------------------------

$null = Wait-L2Condition -Description 'one vehicle committed to charging' `
    -Journal $journal -Criterion 'first-charging-commitment' -TimeoutSeconds 120 `
    -Probe { Get-Count "SELECT COUNT(*) AS N FROM VehiclePurposeClaims WHERE Purpose = 'CHARGING'" } `
    -Until { param($v) $v -ge 1 }

# 它的单在下一轮过出发前安全门之后建：等 RIoT 确认（周期记下单号）。
$upperId = Wait-L2Condition -Description 'the charge order was confirmed by RIoT' `
    -Journal $journal -Criterion 'charge-order-confirmed' -TimeoutSeconds 120 `
    -Probe {
        # One cycle on its way is the premise. Two come back as "(2 rows, expected 1)", which then stands in $held and
        # cannot equal what Get-Commitments reads (L2SingleRow.psm1).
        $row = Read-L2SingleRow -Connection $connection -Sql (
            "SELECT UpperId FROM ChargingCycles WHERE WireState = 'EN_ROUTE' AND UpperId IS NOT NULL")
        if ($null -eq $row) { return $null }
        return [string]$row.UpperId
    } `
    -Until { param($v) $null -ne $v }

# 另等：十几轮派车里有没有出现第二条承诺。正常情形等满超时，拿最后一次读数。
$claimCount = Wait-L2ConditionOrLast -Description 'a second charging commitment appears (it must not)' `
    -Journal $journal -Criterion 'charging-claims-after-window' -TimeoutSeconds 15 `
    -Probe { $null = Get-Docked; Get-Count "SELECT COUNT(*) AS N FROM VehiclePurposeClaims WHERE Purpose = 'CHARGING'" } `
    -Until { param($v) $v -ge 2 }

# Exactly one charging journey and one cycle in the whole database: more (a second commitment) or none reads
# "(N rows, expected 1)" in every column, and the comparisons below go red on it.
$journeyRow = Read-L2SingleRow -Required -Connection $connection -Sql (
    "SELECT JourneyId, VehicleKey, ChargingPolicyVersion FROM JourneyRuntimes WHERE JourneyId LIKE 'charging:%'")
$journeyId = [string]$journeyRow.JourneyId
$cycleRow = Read-L2SingleRow -Required -Connection $connection -Sql (
    "SELECT CycleId, VehicleKey, StationId, ChargerRosterVersion, ChargingPolicyVersion FROM ChargingCycles")
$cycleId = [string]$cycleRow.CycleId
$held = "claims[$($lowest.VehicleKey)=$journeyId] chargers[$charger RESERVED $($lowest.VehicleKey) $journeyId] " +
    "cycles[$($lowest.VehicleKey) $cycleId $charger $upperId] intents[$($lowest.VehicleKey) $upperId CHARGE $charger] " +
    "riot[$upperId] commands[0]"
$commitments = Get-Commitments
$journal.Observe('charging-commitment', $commitments, @{ journey = $journeyRow; cycle = $cycleRow })
$assertions.Add(
    'L2-COC-01',
    '三台车同时需要充电：恰好一台取得 CHARGING 用途占有与 211 的预占（另等十几轮之后仍是一台），它是电量最低的那台；' +
        '它的充电单（move + act，形态 CHARGE）在 RIoT 上恰好一张，单号是周期派生的那个',
    ($claimCount -eq 1 -and $commitments -eq $held -and [string]$cycleRow.StationId -eq '211' -and
        [string]$cycleRow.VehicleKey -eq $lowest.VehicleKey -and [string]$journeyRow.VehicleKey -eq $lowest.VehicleKey -and
        [string]$cycleRow.ChargingPolicyVersion -eq $version -and [string]$journeyRow.ChargingPolicyVersion -eq $version -and
        [string]$cycleRow.ChargerRosterVersion -eq [string]$roster.version),
    "1 / $held / policy v$version / roster v$($roster.version)",
    "$claimCount / $commitments / policy v$($cycleRow.ChargingPolicyVersion) / roster v$($cycleRow.ChargerRosterVersion)")

# 另两台在队里：各自报「211 已被预占」，什么也没留下。
$logText = Wait-L2ConditionOrLast -Description 'both queued vehicles reported why they were not allocated' `
    -Journal $journal -Criterion 'queued-vehicles-reported' -TimeoutSeconds 30 `
    -Probe {
        $text = Read-SharedText $serverLog
        (@($first, $second) | ForEach-Object { if ($text.Contains((Get-QueuedLine $_.AgvId $_.Battery))) { 'reported' } else { 'silent' } }) -join '/'
    } `
    -Until { param($v) $v -eq 'reported/reported' }
$queuedKeys = "'$($first.VehicleKey)','$($second.VehicleKey)'"
$queuedFootprint = "$(Get-Count "SELECT COUNT(*) AS N FROM VehiclePurposeClaims WHERE VehicleKey IN ($queuedKeys)") claims, " +
    "$(Get-Count "SELECT COUNT(*) AS N FROM StationExclusivities WHERE VehicleKey IN ($queuedKeys)") stations, " +
    "$(Get-Count "SELECT COUNT(*) AS N FROM ManualChargingHolds WHERE VehicleKey IN ($queuedKeys)") holds, " +
    "$(Get-Count "SELECT COUNT(*) AS N FROM OrderIntents WHERE VehicleKey IN ($queuedKeys)") intents"
$assertions.Add(
    'L2-COC-02',
    '另两台留在队里：各自报出「211 已被预占」，没有用途占有、站点独占、人工充电等待或订单意图',
    ($logText -eq 'reported/reported' -and $queuedFootprint -eq '0 claims, 0 stations, 0 holds, 0 intents'),
    'reported/reported / 0 claims, 0 stations, 0 holds, 0 intents',
    "$logText / $queuedFootprint")

# --- 3. 那张单走完：车在 211 上报 CHARGING -------------------------------------------------------------------

$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })
$docked = Wait-L2ConditionOrLast -Description 'the vehicle holding the reservation is docked on the charger' `
    -Journal $journal -Criterion 'docked-on-the-charger' -TimeoutSeconds 30 `
    -Probe { (Get-Docked) -join ',' } `
    -Until { param($v) $v -ne '' }
$assertions.Add(
    'L2-COC-03',
    '充电单完成后，在 211 上 docked 的正是预占它的那台车',
    ($docked -eq $lowest.VehicleKey),
    $lowest.VehicleKey,
    $docked)

# --- 4. 第二个事实：队里一台的电量降到比已预占那台更低，抢不走 ------------------------------------------------

$lowered = 10
Set-Battery $first.VehicleKey $lowered
$loweredLine = Get-QueuedLine $first.AgvId $lowered
# 等它在电量变化之后的某一轮报出排队原因；同一次探测里读已预占那台的承诺，断言用这次等待的返回值。
$afterLowering = Wait-L2ConditionOrLast -Description "$($first.AgvId) reported its place in the queue after its battery fell to $lowered %" `
    -Journal $journal -Criterion 'queued-after-battery-fell' -TimeoutSeconds 60 `
    -Probe {
        $null = Get-Docked
        $reported = if ((Read-SharedText $serverLog).Contains($loweredLine)) { 'reported' } else { 'silent' }
        "$reported | $(Get-Commitments)"
    } `
    -Until { param($v) $v.StartsWith('reported') }
$assertions.Add(
    'L2-COC-04',
    "队里的 $($first.AgvId) 电量降到 $lowered%（比已预占那台的 $($lowest.Battery)% 更低）之后报出的仍是「211 已被预占」；" +
        '那一刻已预占那台的用途占有、预占、周期与单号逐字未变，没有第二张单，也没有任何取消命令',
    ($afterLowering -eq "reported | $held"),
    "reported | $held",
    $afterLowering)

# 另等：此后十几轮里承诺有没有变、211 上有没有出现第二台车。
$later = Wait-L2ConditionOrLast -Description 'the reservation changed hands or a second vehicle docked (neither may happen)' `
    -Journal $journal -Criterion 'reservation-after-window' -TimeoutSeconds 15 `
    -Probe { "$((Get-Docked) -join ',') | $(Get-Commitments)" } `
    -Until { param($v) $v -ne "$($lowest.VehicleKey) | $held" }
$assertions.Add(
    'L2-COC-05',
    '此后十几轮里预占没有易手；整段里 211 上 docked 的车从没超过 1 辆',
    ($later -eq "$($lowest.VehicleKey) | $held" -and $script:mostDocked -eq 1),
    "$($lowest.VehicleKey) | $held / at most 1 docked",
    "$later / at most $script:mostDocked docked")

$journal.Note('一桩三车：电量最低的那台取得预占并进桩；另两台排队，其中一台电量降到更低也抢不走。')
