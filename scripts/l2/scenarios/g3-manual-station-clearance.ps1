#Requires -Version 7

<#
G3 `FP-IS-13`：充不上之后的人工清桩（批次9-08，control-server#406；车载端入口 onboard-hmi#221）。协议向量 `CV-MANUAL-STATION-CLEARANCE`：
`ManualStationClearanceConfirmationRequested` → `ManualStationClearanceConfirmationResult`；服务端 `RELEASE_STATION_ONLY_ON_CONFIRMED_CLEARANCE`。

**只写场景，不登记进 G3 runner、不改认领表**（票面第 11 条：本批 G3 runner 归批次9-07，之后转给出口票批次9-14，由它登记）。断言名用
`docs/g3-slice-claim-review.md` 给本票预留的 G3-13-11～19。

两端的半边：服务端 control-server#406（自动形成「已确认充不上」、暂停桩、清桩中车保持原位、人工清桩确认的判定），车载端 onboard-hmi#221
（清桩中计划里取原充电桩站点号、「确认清桩」入口、结果一行 `StationClearanceStatus`）。合成对端上同一条链路是
`charging-unable-to-charge-pauses-charger`（只看服务端的库）；这里换成真车载端 WPF 与真模拟器。

**形状**：一辆车停在关卡 210，电量压到 25 → 分配到 211、建单 → 那张单注入 CannotCharge、推到完成：假 RIoT 挂在 9、开始充电的动作带 407802，
车停在 211 上 → 服务端暂停 211、车进清桩中 → 车被挪回 210、旧单在 RIoT 里取消 → 操作员（车载端配置的 L2-OPERATOR，服务端名单里是 R-11）
在车上按「确认清桩」→ 服务端 CONFIRMED、stationReleased=true，211 释放、暂停仍在。

判据：
- G3-13-11（服务端，消息顺序之前的那一半）：一条 UNABLE_TO_CHARGE_CONFIRMED 的暂停；清桩中的计划（恰好一条 CHARGER 腿）与业务状态
  （UNABLE_TO_CHARGE、CLEARING_MAINTENANCE）都被真车载端确认。
- G3-13-12（车载端）：界面 `ChargingStatus` 报 UNABLE_TO_CHARGE，「确认清桩」入口出现（说明一行 `StationClearanceNotice` 不在）。
- G3-13-13（两端，`orderedExpectedMessages`）：车载端发 Requested（操作员 L2-OPERATOR、publicStationFunction 为空、STATION_EMPTY、站点是计划里
  那条 CHARGER 腿的站点），服务端回 Result：CONFIRMED、problem 为空、stationReleased=true；界面结果一行 `StationClearanceStatus` 报
  CONFIRMED_STATION_RELEASED。
- G3-13-14（服务端，`RELEASE_STATION_ONLY_ON_CONFIRMED_CLEARANCE`）：211 的独占以 CHARGER_RELEASED_ON_MANUAL_CLEARANCE 释放，暂停没有恢复行，
  旅程以 CHARGING_UNABLE_TO_CHARGE_CLEARED 收尾、收尾快照被确认；RIoT 上恰好一张充电单，没有任何订单命令。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SingleRow.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Chargers.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2MultiStopJourney.psm1') -Force
. (Join-Path $PSScriptRoot 'MultiStopRigCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$onboard = $Context.Onboard
$riot = $Context.Riot

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: the clearance is confirmed from the shipped HMI.'
}

$gateRiotId = $Context.GateStationRiotId
$charger = 211
$chargerName = '充电点1'
$vehicleKey = $Context.VehicleKey
$all = @('G3-13-11', 'G3-13-12', 'G3-13-13', 'G3-13-14')

function Get-Cycle {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, WireState, Phase, IFNULL(UpperId, '') AS UpperId, IFNULL(EndReason, '') AS EndReason FROM ChargingCycles")
}

function Get-ChargerHeld {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT State, JourneyId FROM StationExclusivities WHERE MapId = $($Context.MapId) AND StationId = $charger")
    if ($null -eq $row) { return '(none)' }
    return "$($row.State) $($row.JourneyId)"
}

function Get-Holds {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT h.""Trigger"" AS Trig, (SELECT COUNT(*) FROM ChargingStationRecoveries r WHERE r.HoldId = h.HoldId) AS Recovered " +
        "FROM ChargingStationAllocationHolds h WHERE h.StationId = $charger")
    if ($rows.Count -eq 0) { return '(none)' }
    return (@($rows | ForEach-Object { "$($_.Trig) recovered=$($_.Recovered)" }) -join '; ')
}

function Get-Field([object]$Snapshot, [string]$Name) {
    $property = $Snapshot.Payload.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

# --- 1. 车停在关卡上；桩登记；电量压到强制充电线以下 ------------------------------------------------------------------

Initialize-L2CargoRig $Context
$null = $riot.Command('Put', 'chargers', @{
    chargers = @(@{ mapId = $Context.MapId; stationId = $charger; chargeIntervalSeconds = 1; chargePercentPerInterval = 1 })
    dischargeIntervalSeconds = 0; dischargePercentPerInterval = 0
})
$roster = Invoke-L2ChargerRosterImport -Chargers @(@{ StationId = $charger; StationName = $chargerName }) `
    -MapId $Context.MapId -Fleet @($vehicleKey) -Riot $riot -InvokeFieldOps $Context.InvokeFieldOps `
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "G3 g3-manual-station-clearance: station $charger"
$journal.Observe('charger-roster', [string]$roster.version, @{ import = $roster })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $gateRiotId; battery = 25
})

$chargeIntent = Wait-L2ConditionOrLast -Description 'the charge order to the charger was confirmed' -Journal $journal `
    -Criterion 'charge-intent' -TimeoutSeconds 120 `
    -Probe {
        Read-L2SingleRow -Connection $connection -Sql (
            "SELECT UpperId, IFNULL(OrderId, '') AS OrderId, Status FROM OrderIntents WHERE Purpose = 'TO_CHARGER'")
    } `
    -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
if ($null -eq $chargeIntent -or [string]$chargeIntent.Status -ne 'CONFIRMED') {
    Add-L2RealNotReached $assertions $all '电量压到强制充电线以下，却没有一张开往充电桩的单被确认'
    return
}
$journeyId = [string](Get-Cycle).JourneyId

# --- 2. 到桩、开始充电失败：407802 + HANG ------------------------------------------------------------------------------

$null = $riot.Command('Put', 'charging/faults', @{ upperId = [string]$chargeIntent.UpperId; startOutcome = 'CannotCharge' })
$null = Move-L2RealVehicleTo $Context $chargeIntent $charger 'the charger'

$clearing = Wait-L2ConditionOrLast -Description 'the charger was paused and the clearing snapshots acknowledged' `
    -Journal $journal -Criterion 'clearing-snapshots-acknowledged' -TimeoutSeconds 90 `
    -Probe {
        $plan = @((Get-L2RealOutbound $connection 'UpcomingStopPlanSnapshot') | Where-Object {
                $legs = @($_.Payload.legs)
                $legs.Count -eq 1 -and [string]$legs[0].stopPurposeCategory -eq 'CHARGER' }) | Select-Object -Last 1
        $state = @((Get-L2RealOutbound $connection 'VehicleBusinessStateSnapshot') | Where-Object {
                (Get-Field $_ 'activePurpose') -eq 'CLEARING_MAINTENANCE' -and (Get-Field $_ 'chargingCycleState') -eq 'UNABLE_TO_CHARGE' }) |
            Select-Object -First 1
        [pscustomobject]@{ Holds = Get-Holds; Plan = $plan; State = $state }
    } `
    -Until { param($v) $v.Holds -eq 'UNABLE_TO_CHARGE_CONFIRMED recovered=0' -and $null -ne $v.State -and $v.State.Acknowledged -and $null -ne $v.Plan -and $v.Plan.Acknowledged }
$assertions.Add(
    'G3-13-11',
    '到桩开始充电失败（407802、HANG）：一条 UNABLE_TO_CHARGE_CONFIRMED 的暂停；清桩中的计划（恰好一条 CHARGER 腿）与业务状态（UNABLE_TO_CHARGE、CLEARING_MAINTENANCE）都被真车载端确认',
    ($clearing.Holds -eq 'UNABLE_TO_CHARGE_CONFIRMED recovered=0' -and $null -ne $clearing.State -and $clearing.State.Acknowledged -and
        $null -ne $clearing.Plan -and $clearing.Plan.Acknowledged -and [string]@($clearing.Plan.Payload.legs)[0].stationId -eq $chargerName),
    "UNABLE_TO_CHARGE_CONFIRMED recovered=0 / plan [CHARGER@$chargerName] acked / state acked",
    "$($clearing.Holds) / plan $(${clearing}?.Plan?.Acknowledged) / state $(${clearing}?.State?.Acknowledged)")

$shown = Wait-L2ConditionOrLast -Description 'the HMI shows UNABLE_TO_CHARGE and offers the clearance' -Journal $journal `
    -Criterion 'hmi-offers-clearance' -TimeoutSeconds 60 `
    -Probe { "$(Get-L2LoadingPhaseLine $onboard 'ChargingStatus') | offered=$([bool]$onboard.ButtonAvailable('确认清桩'))" } `
    -Until { param($v) $v -eq 'UNABLE_TO_CHARGE | offered=True' }
$assertions.Add(
    'G3-13-12',
    '车载端：界面 ChargingStatus 报 UNABLE_TO_CHARGE，「确认清桩」入口出现（从计划里那条 CHARGER 腿取到了原充电桩）',
    ($shown -eq 'UNABLE_TO_CHARGE | offered=True'),
    'UNABLE_TO_CHARGE | offered=True',
    $shown)

# --- 3. 车被挪开、旧单在 RIoT 里取消；操作员在车上确认清桩 ----------------------------------------------------------------

$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0; currentPosition = $gateRiotId
})
$null = $riot.Command('Put', "orders/$($chargeIntent.UpperId)", @{ orderState = 2 })
$null = Invoke-L2RealConfirmedButton $onboard $journal '确认清桩' '确认清桩'
$exchange = Wait-L2ConditionOrLast -Description 'the server answered the clearance confirmation' -Journal $journal `
    -Criterion 'clearance-answered' -TimeoutSeconds 60 `
    -Probe {
        $request = @((Get-L2RealInbound $connection 'ManualStationClearanceConfirmationRequested') | Where-Object { $null -ne $_ }) | Select-Object -Last 1
        # Get-L2RealInbound's shape: the first answer's type is .Response and its payload .ResponsePayload (ResponseLine is
        # G3RecoveryCommon's shape and is absent here, which StrictMode turns into a throw).
        if ($null -eq $request -or $request.Response -ne 'ManualStationClearanceConfirmationResult') { return $null }
        [pscustomobject]@{ Request = $request; Result = $request.ResponsePayload }
    } `
    -Until { param($v) $null -ne $v }
$status = Wait-L2ConditionOrLast -Description 'the HMI shows the confirmation released the charger' -Journal $journal `
    -Criterion 'hmi-clearance-status' -TimeoutSeconds 30 `
    -Probe { [string](Get-L2LoadingPhaseLine $onboard 'StationClearanceStatus') } `
    -Until { param($v) $v -eq 'CONFIRMED_STATION_RELEASED' }
$requestPayload = ${exchange}?.Request?.Payload
$assertions.Add(
    'G3-13-13',
    '车载端发 ManualStationClearanceConfirmationRequested（操作员 L2-OPERATOR、publicStationFunction 为空、STATION_EMPTY、站点是那条 CHARGER 腿的站点），服务端回 Result：CONFIRMED、problem 为空、stationReleased=true；界面结果一行报 CONFIRMED_STATION_RELEASED',
    ($null -ne $exchange -and [string]$requestPayload.operator.operatorId -eq 'L2-OPERATOR' -and $null -eq $requestPayload.publicStationFunction -and
        [string]$requestPayload.clearedCondition -eq 'STATION_EMPTY' -and [string]$requestPayload.stationId -eq $chargerName -and
        [string]$exchange.Result.outcome -eq 'CONFIRMED' -and $null -eq $exchange.Result.problem -and [bool]$exchange.Result.stationReleased -and
        $status -eq 'CONFIRMED_STATION_RELEASED'),
    "L2-OPERATOR / null / STATION_EMPTY / $chargerName -> CONFIRMED / null / true / CONFIRMED_STATION_RELEASED",
    "$(${requestPayload}?.operator?.operatorId) / $(${requestPayload}?.publicStationFunction) / $(${requestPayload}?.clearedCondition) / $(${requestPayload}?.stationId) -> " +
        "$(${exchange}?.Result?.outcome) / $(${exchange}?.Result?.problem) / $(${exchange}?.Result?.stationReleased) / $status")

# --- 4. 服务端：只在确认之后放 211，暂停仍在，旅程收尾 --------------------------------------------------------------------

$after = Wait-L2ConditionOrLast -Description 'the charger was released and the journey closed' -Journal $journal `
    -Criterion 'released-and-closed' -TimeoutSeconds 60 `
    -Probe {
        $record = Read-L2SingleRow -Connection $connection -Sql (
            "SELECT IFNULL(ReleaseReason, '') AS Reason FROM StationExclusivityRecords WHERE StationId = $charger AND JourneyId = '$journeyId'")
        $journey = Read-L2SingleRow -Connection $connection -Sql (
            "SELECT Stage, IFNULL(BlockReasonCode, '') AS Code FROM JourneyRuntimes WHERE JourneyId = '$journeyId'")
        "$(Get-ChargerHeld) | $(${record}?.Reason) | $(Get-Holds) | $(${journey}?.Stage) $(${journey}?.Code) | " +
            "$(@($riot.Snapshot().body.orders | Where-Object { $null -ne $_ }).Count) orders"
    } `
    -Until { param($v) $v.Contains('Completed') }
$expected = '(none) | CHARGER_RELEASED_ON_MANUAL_CLEARANCE | UNABLE_TO_CHARGE_CONFIRMED recovered=0 | Completed CHARGING_UNABLE_TO_CHARGE_CLEARED | 1 orders'
$assertions.Add(
    'G3-13-14',
    '211 的独占以 CHARGER_RELEASED_ON_MANUAL_CLEARANCE 释放，暂停没有恢复行，旅程以 CHARGING_UNABLE_TO_CHARGE_CLEARED 收尾；RIoT 上恰好一张充电单（RELEASE_STATION_ONLY_ON_CONFIRMED_CLEARANCE）',
    ($after -eq $expected),
    $expected,
    $after)
