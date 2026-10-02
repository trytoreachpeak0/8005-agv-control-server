#Requires -Version 7

<#
G3 `FP-IS-13`：现场确认充不上（批次9-12，control-server#410；车载端入口 onboard-hmi#222）。协议向量 `CV-UNABLE-TO-CHARGE-FIELD-CONFIRMATION`：
`UnableToChargeFieldConfirmationRequested` → `UnableToChargeFieldConfirmationResult` → `VehicleBusinessStateSnapshot` → `SnapshotAppliedAck`；
服务端 `DECIDE_CHARGING_POLICY_CENTRALLY`、`RECORD_FIELD_OBSERVATION`。

**只写场景，不登记进 G3 runner、不改认领表**（票面第 8 条：由出口批次9-14，control-server#412 登记）。断言名用
`docs/g3-slice-claim-review.md` 给本票预留的 G3-13-21～29。

**编排器的一个开关**：setup 里的 `UnableToChargeEntry` 由 `Invoke-L2Scenario.ps1` 写进车载端这一次的配置副本
（`wireToGate.unableToChargeEntryEnabled`，车载端出厂是 false；本票加的，只在场景声明时写）。

两端的半边：服务端 control-server#410（判定、暂停、清桩中、服务端定的 chargingPolicyDecision），车载端 onboard-hmi#222（充电用途、计划当前腿是充电桩
时出现入口，按情形各一个按钮，结果一行 `UnableToChargeStatus`）。合成对端没有发这条消息的入口，所以这条链路只有 G2 与这里。

**形状**：一辆车停在关卡 210，电量压到 25 → 分配到 211、建单 → 那张单注入 HangOnly（挂在 9，开始充电的动作带 400001，不是验证过的 407802）、
推到完成，车停在 211 上 → 系统事实不完整，服务端只写 ORDER_HANG、不暂停桩 → 操作员（L2-OPERATOR，服务端名单里是 R-11）在车上按「接不上充电」
（CONNECTION_FAILED）并确认 → 服务端 CONFIRMED；名册里只有 211、电量 25 低于预置策略的最低余量 30，所以 chargingPolicyDecision 是
MANUAL_CHARGING_HOLD → 211 暂停（记确认人）、车进清桩中保持原位，清桩中的业务状态被车载端确认。

判据：
- G3-13-21（服务端，前提）：车停在 211、单 HANG 而结果码不是 407802：旅程写 ORDER_HANG，没有任何暂停，周期仍 ACTIVE——系统没有自动确认。
- G3-13-22（车载端）：充电用途、计划当前腿是 211 时，「现场确认充不上」入口出现（「接不上充电」按钮可按，说明一行 `UnableToChargeNotice` 不在）。
- G3-13-23（两端，`orderedExpectedMessages` 前两条）：车载端发 Requested（操作员 L2-OPERATOR、chargerStationId 是计划里那条 CHARGER 腿的站点、
  CONNECTION_FAILED），服务端回 Result：CONFIRMED、problem 为空、chargingPolicyDecision=MANUAL_CHARGING_HOLD。只断线路。
- G3-13-24（两端，`orderedExpectedMessages` 后两条）：Result 之后，业务状态 UNABLE_TO_CHARGE、CLEARING_MAINTENANCE 进发件箱并被车载端确认。
- G3-13-25（服务端，`DECIDE_CHARGING_POLICY_CENTRALLY`、`RECORD_FIELD_OBSERVATION`）：一条 UNABLE_TO_CHARGE_CONFIRMED 的暂停，确认人 L2-OPERATOR、角色 R-11、
  现场处置 CONNECTION_FAILED；周期 UNABLE_TO_CHARGE／CLEARING；人工充电等待原因 UNABLE_TO_CHARGE_LOW_BATTERY；判定记下一行。
- G3-13-26（服务端，forbiddenSideEffects `duplicate-riot-order`；车保持原位）：RIoT 上恰好一张充电单，没有任何订单命令，211 仍是这一趟的。
- G3-13-27（车载端）：清桩中的业务状态被车载端确认**之后**，界面结果一行 `UnableToChargeStatus` 在随后 3 秒里每次读都是 CONFIRMED，
  任何一次读到别的值即红（调度 10-02：只读到一次 CONFIRMED，读的时刻离清桩中状态到达太近，证明不了「清桩中之后结果还在」）。
  **已知红，等 onboard-hmi#242**：车载端在业务状态的 activePurpose
  不再是 CHARGING 时清掉上一次结果（`ServerSaysTheChargingIsOver`），而确认之后服务端把用途转成 CLEARING_MAINTENANCE，结果一行只闪一下。
  第一遍（G3-13-23 拆分之前）的红证据：`evidence/l2/20261002-cs410-g3-unable-to-charge-field-confirmation-red-before-g3-13-23-split`。
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
    throw 'This scenario needs the real onboard rig: the unable-to-charge is confirmed from the shipped HMI.'
}

$gateRiotId = $Context.GateStationRiotId
$charger = 211
$chargerName = '充电点1'
$vehicleKey = $Context.VehicleKey
# CONNECTION_FAILED's caption on the HMI (WireToGateUnableToChargeText.ConditionLabel): the button is addressed by it.
$conditionButton = '接不上充电'
$all = @('G3-13-21', 'G3-13-22', 'G3-13-23', 'G3-13-24', 'G3-13-25', 'G3-13-26', 'G3-13-27')

function Get-Cycle {
    Read-L2SingleRow -Connection $connection -Sql (
        "SELECT CycleId, JourneyId, WireState, Phase FROM ChargingCycles")
}

function Get-Holds {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT h.""Trigger"" AS Trig, IFNULL(h.ConfirmedByPersonId, '') AS Person, IFNULL(h.ConfirmedByRole, '') AS Role, " +
        "IFNULL(h.SiteDisposition, '') AS Disposition FROM ChargingStationAllocationHolds h WHERE h.StationId = $charger")
    if ($rows.Count -eq 0) { return '(none)' }
    return (@($rows | ForEach-Object { "$($_.Trig) $($_.Person)/$($_.Role)/$($_.Disposition)" }) -join '; ')
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
    -SnapshotRoot $Context.SnapshotRoot -Label 'open' -ChangeNote "G3 g3-unable-to-charge-field-confirmation: station $charger"
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

# --- 2. 到桩、开始充电失败，但不是 407802：系统事实不完整 ------------------------------------------------------------------

$null = $riot.Command('Put', 'charging/faults', @{
    upperId = [string]$chargeIntent.UpperId; startOutcome = 'HangOnly'; hangResultCode = 400001
})
$null = Move-L2RealVehicleTo $Context $chargeIntent $charger 'the charger'

$hanging = Wait-L2ConditionOrLast -Description 'the hang was named ORDER_HANG and nothing was paused' -Journal $journal `
    -Criterion 'order-hang-not-confirmed' -TimeoutSeconds 90 `
    -Probe {
        $journey = Read-L2SingleRow -Connection $connection -Sql (
            "SELECT IFNULL(BlockReasonCode, '') AS Code FROM JourneyRuntimes WHERE JourneyId = '$journeyId'")
        "$(${journey}?.Code) | $(Get-Holds) | $((Get-Cycle).Phase)"
    } `
    -Until { param($v) $v.StartsWith('ORDER_HANG |') }
# Two more rounds of the engine: a strict-fact confirmation would need two observations, and it must not come.
Start-Sleep -Seconds 5
$settled = "$((Read-L2SingleRow -Connection $connection -Sql (
    "SELECT IFNULL(BlockReasonCode, '') AS Code FROM JourneyRuntimes WHERE JourneyId = '$journeyId'")).Code) | $(Get-Holds) | $((Get-Cycle).Phase)"
$assertions.Add(
    'G3-13-21',
    '车停在 211、单 HANG、开始充电的结果码不是 407802：旅程写 ORDER_HANG，没有任何暂停，周期仍 ACTIVE（系统没有自动确认，REQ-0175）',
    ($hanging -eq 'ORDER_HANG | (none) | ACTIVE' -and $settled -eq 'ORDER_HANG | (none) | ACTIVE'),
    'ORDER_HANG | (none) | ACTIVE',
    "$hanging / $settled")
if ($settled -ne 'ORDER_HANG | (none) | ACTIVE') {
    Add-L2RealNotReached $assertions @('G3-13-22', 'G3-13-23', 'G3-13-24', 'G3-13-25', 'G3-13-26', 'G3-13-27') '前提不成立：系统已自动确认或旅程不在 ORDER_HANG'
    return
}

$offered = Wait-L2ConditionOrLast -Description 'the HMI offers the unable-to-charge confirmation' -Journal $journal `
    -Criterion 'hmi-offers-unable-to-charge' -TimeoutSeconds 60 `
    -Probe { "offered=$([bool]$onboard.ButtonAvailable($conditionButton)) | notice=$([bool]$onboard.Element('AutomationId', 'UnableToChargeNotice'))" } `
    -Until { param($v) $v -eq 'offered=True | notice=False' }
$assertions.Add(
    'G3-13-22',
    '车载端：充电用途、计划当前腿是 211 时「现场确认充不上」入口出现（「接不上充电」可按，没有不可用说明）',
    ($offered -eq 'offered=True | notice=False'),
    'offered=True | notice=False',
    $offered)
if ($offered -ne 'offered=True | notice=False') {
    Add-L2RealNotReached $assertions @('G3-13-23', 'G3-13-24', 'G3-13-25', 'G3-13-26', 'G3-13-27') '车载端没有出现现场确认充不上入口（检查 UnableToChargeEntry 是否已写进车载端配置）'
    return
}

# --- 3. 操作员在车上确认：接不上充电 ------------------------------------------------------------------------------------

$null = Invoke-L2RealConfirmedButton $onboard $journal $conditionButton '现场确认充不上'
$exchange = Wait-L2ConditionOrLast -Description 'the server answered the field confirmation' -Journal $journal `
    -Criterion 'unable-to-charge-answered' -TimeoutSeconds 60 `
    -Probe {
        $request = @((Get-L2RealInbound $connection 'UnableToChargeFieldConfirmationRequested') | Where-Object { $null -ne $_ }) | Select-Object -Last 1
        if ($null -eq $request -or $request.Response -ne 'UnableToChargeFieldConfirmationResult') { return $null }
        [pscustomobject]@{ Request = $request; Result = $request.ResponsePayload }
    } `
    -Until { param($v) $null -ne $v }
$requestPayload = ${exchange}?.Request?.Payload
$assertions.Add(
    'G3-13-23',
    '车载端发 UnableToChargeFieldConfirmationRequested（L2-OPERATOR、计划里那条 CHARGER 腿的站点、CONNECTION_FAILED），服务端回 Result：CONFIRMED、problem 为空、chargingPolicyDecision=MANUAL_CHARGING_HOLD（名册只有 211、电量 25 低于最低余量 30）',
    ($null -ne $exchange -and [string]$requestPayload.operator.operatorId -eq 'L2-OPERATOR' -and
        [string]$requestPayload.chargerStationId -eq $chargerName -and [string]$requestPayload.observedCondition -eq 'CONNECTION_FAILED' -and
        [string]$exchange.Result.outcome -eq 'CONFIRMED' -and $null -eq $exchange.Result.problem -and
        [string]$exchange.Result.chargingPolicyDecision -eq 'MANUAL_CHARGING_HOLD'),
    "L2-OPERATOR / $chargerName / CONNECTION_FAILED -> CONFIRMED / null / MANUAL_CHARGING_HOLD",
    "$(${requestPayload}?.operator?.operatorId) / $(${requestPayload}?.chargerStationId) / $(${requestPayload}?.observedCondition) -> " +
        "$(${exchange}?.Result?.outcome) / $(${exchange}?.Result?.problem) / $(${exchange}?.Result?.chargingPolicyDecision)")
# --- 4. Result 之后：清桩中的业务状态被确认；服务端的暂停、周期与人工充电等待 ----------------------------------------------------

$answeredAt = ${exchange}?.Request?.At
$state = Wait-L2ConditionOrLast -Description 'the clearing business state after the result was acknowledged' -Journal $journal `
    -Criterion 'clearing-state-acknowledged' -TimeoutSeconds 60 `
    -Probe {
        @((Get-L2RealOutbound $connection 'VehicleBusinessStateSnapshot') | Where-Object {
                (Get-Field $_ 'activePurpose') -eq 'CLEARING_MAINTENANCE' -and (Get-Field $_ 'chargingCycleState') -eq 'UNABLE_TO_CHARGE' }) |
            Select-Object -First 1
    } `
    -Until { param($v) $null -ne $v -and $v.Acknowledged }
$assertions.Add(
    'G3-13-24',
    'Result 之后，业务状态 UNABLE_TO_CHARGE、CLEARING_MAINTENANCE 进发件箱并被车载端确认（VehicleBusinessStateSnapshot → SnapshotAppliedAck）',
    ($null -ne $state -and $state.Acknowledged -and $null -ne $answeredAt -and $state.At -gt $answeredAt),
    "after $answeredAt, acknowledged",
    "$(${state}?.At), acknowledged=$(${state}?.Acknowledged)")

# Known red until onboard-hmi#242: the onboard forgets the outcome once activePurpose leaves CHARGING, and a confirmation turns
# it into CLEARING_MAINTENANCE within a second. Read only after that business state is acknowledged -- applied on the vehicle --
# and then held: every read over the next three seconds must say CONFIRMED, so a result that is gone, or going, is red.
if ($null -eq $state -or -not $state.Acknowledged) {
    Add-L2RealNotReached $assertions @('G3-13-27') '清桩中的业务状态没有被车载端确认，结果一行无从在它之后读'
} else {
    $reads = [System.Collections.Generic.List[string]]::new()
    $holdUntil = [DateTimeOffset]::UtcNow.AddSeconds(3)
    while ([DateTimeOffset]::UtcNow -lt $holdUntil) {
        $reads.Add([string](Get-L2LoadingPhaseLine $onboard 'UnableToChargeStatus'))
        Start-Sleep -Milliseconds 200
    }
    $off = @($reads | Where-Object { $_ -ne 'CONFIRMED' })
    $journal.Observe('hmi-unable-to-charge-status-held', "$($reads.Count) reads, $($off.Count) not CONFIRMED", @{ reads = @($reads) })
    $assertions.Add(
        'G3-13-27',
        '清桩中的业务状态被车载端确认之后，界面结果一行 UnableToChargeStatus 在随后 3 秒里每次读都是 CONFIRMED（已知红，等 onboard-hmi#242：用途转为 CLEARING_MAINTENANCE 时车载端清掉了结果）',
        ($reads.Count -gt 0 -and $off.Count -eq 0),
        "$($reads.Count) reads, all CONFIRMED",
        "$($reads.Count) reads, $($off.Count) not CONFIRMED (first: '$(if ($off.Count -gt 0) { $off[0] } else { '-' })')")
}

$serverSide = "$(Get-Holds) | $((Get-Cycle).WireState)/$((Get-Cycle).Phase) | " +
    "$((Read-L2SingleRow -Connection $connection -Sql "SELECT IFNULL(MAX(Reason), '(none)') AS Reason FROM ManualChargingHolds WHERE VehicleKey = '$vehicleKey'").Reason) | " +
    "$((Read-L2SingleRow -Connection $connection -Sql "SELECT COUNT(*) AS N FROM UnableToChargeFieldConfirmations WHERE Outcome = 'CONFIRMED'").N) decided"
$expectedServer = 'UNABLE_TO_CHARGE_CONFIRMED L2-OPERATOR/R-11/CONNECTION_FAILED | UNABLE_TO_CHARGE/CLEARING | UNABLE_TO_CHARGE_LOW_BATTERY | 1 decided'
$assertions.Add(
    'G3-13-25',
    '一条 UNABLE_TO_CHARGE_CONFIRMED 的暂停（确认人 L2-OPERATOR、R-11、现场处置 CONNECTION_FAILED）；周期 UNABLE_TO_CHARGE／CLEARING；人工充电等待 UNABLE_TO_CHARGE_LOW_BATTERY；判定记下一行（DECIDE_CHARGING_POLICY_CENTRALLY、RECORD_FIELD_OBSERVATION）',
    ($serverSide -eq $expectedServer),
    $expectedServer,
    $serverSide)

# --- 5. 车保持原位：没有新单、没有订单命令，211 仍是这一趟的 ----------------------------------------------------------------

Start-Sleep -Seconds 5
$held = Read-L2SingleRow -Connection $connection -Sql (
    "SELECT JourneyId FROM StationExclusivities WHERE MapId = $($Context.MapId) AND StationId = $charger")
$commands = (Read-L2SingleRow -Connection $connection -Sql "SELECT COUNT(*) AS N FROM RiotOrderCommandAudit").N
$orders = @($riot.Snapshot().body.orders | Where-Object { $null -ne $_ }).Count
$stays = "$orders orders | $commands commands | $(${held}?.JourneyId -eq $journeyId)"
$assertions.Add(
    'G3-13-26',
    '车保持原位：RIoT 上恰好一张充电单（duplicate-riot-order），没有任何订单命令，211 仍是这一趟的',
    ($stays -eq '1 orders | 0 commands | True'),
    '1 orders | 0 commands | True',
    $stays)
