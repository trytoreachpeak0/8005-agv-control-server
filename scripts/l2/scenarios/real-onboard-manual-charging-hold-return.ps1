#Requires -Version 7

<#
真装置：服务端持有的人工充电等待的唯一出口（批次9-07，control-server#405；cs#404 独立审查 S6）。

cs#404 让服务端在名册为空时置人工充电等待（ROSTER_EMPTY）、告警、给车发 manualChargingHold=true，出口只有车上管理员点「充电后返回服务」
（用户 2026-09-29 定）。这条链在合成对端上走过（charging-registry-emptied-degrades-and-resumes），真车载端还没有：
服务端持有等待 → 真车载端显示 → 管理员点「充电后返回服务」→ 服务端解除 → 车载端不再显示、车重新可派。

**为什么单写一条，不并进 real-onboard-charging-cycle**：前置相反——那一条要名册里有桩、车会去充电；这一条要名册为空、车哪儿也不去；
红了也要能指到一端一段。两条各自一分钟左右。

判据：
- L2-RMH-01：电量压到 20：服务端置等待（ROSTER_EMPTY），manualChargingHold=true 的业务状态被真车载端确认，界面充电那一格的文字写
  「需人工充电：服务端保持」。
- L2-RMH-02：电量回到 80（有人在现场充过电），并发一条需求：之后十秒里等待仍在、界面仍写着它、那条需求没有派给它——电量回升本身不解除
  （NEVER_CLEAR_HOLD_LOCALLY 的服务端一半），等待期间不派单（独立审查 S7）。
- L2-RMH-03：管理员点「充电后返回服务」并确认：服务端受理（RETURNED_TO_ELIGIBILITY_EVALUATION），同一次保存删掉等待、经过上写解除时刻与请求号；
  manualChargingHold=false 的业务状态被确认，界面不再写等待。
- L2-RMH-04：解除之后车重新可派：等待期间那条需求派给它，开往取货站的单已确认。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealOnboard.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SingleRow.psm1') -Force
. (Join-Path $PSScriptRoot 'G3RecoveryCommon.ps1')

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$onboard = $Context.Onboard
$riot = $Context.Riot
$mes = $Context.MesIngest
$vehicleKey = $Context.VehicleKey
$holdText = '需人工充电：服务端保持'
$all = @('L2-RMH-01', 'L2-RMH-02', 'L2-RMH-03', 'L2-RMH-04')

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig: the hold is read off the shipped HMI and lifted from it.'
}

# The text the charging cell shows (its Name); its ItemStatus is the raw chargingCycleState and says nothing about the hold.
function Get-ChargingText {
    try {
        $cell = $onboard.Element('AutomationId', 'ChargingStatus')
        if (-not $cell) { return $null }
        return [string]$cell.Current.Name
    } catch [System.Windows.Automation.ElementNotAvailableException] {
        return $null
    }
}

function Get-Hold {
    $row = Read-L2SingleRow -Connection $connection -Sql "SELECT Reason FROM ManualChargingHolds WHERE VehicleKey = '$vehicleKey'"
    if ($null -eq $row) { return '(none)' }
    return [string]$row.Reason
}

# The newest business state the onboard acknowledged: its manualChargingHold, or '(none)'.
function Get-AcknowledgedHold {
    $state = @((Get-L2RealOutbound $connection 'VehicleBusinessStateSnapshot') | Where-Object { $_.Acknowledged }) | Select-Object -Last 1
    if ($null -eq $state) { return '(none)' }
    return [string]$state.Payload.manualChargingHold
}

function Set-Battery([int]$battery) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Context.GateStationRiotId; battery = $battery
    })
}

$null = Wait-L2Condition -Description 'the onboard session is ready' `
    -Journal $journal -Criterion 'session-readiness' -TimeoutSeconds 180 `
    -Probe { [string](Get-G3Session $connection).Readiness } -Until { param($v) $v -eq 'Ready' }

# --- 1. 空名册、电量到线下：服务端置等待，车载端显示 ------------------------------------------------------------

Set-Battery 20
$held = Wait-L2ConditionOrLast -Description 'the server holds the vehicle for manual charging and the HMI shows it' `
    -Journal $journal -Criterion 'hold-shown' -TimeoutSeconds 90 `
    -Probe { "$(Get-Hold) | $(Get-AcknowledgedHold) | $([string](Get-ChargingText) -like "*$holdText*")" } `
    -Until { param($v) $v -eq 'ROSTER_EMPTY | True | True' }
$assertions.Add(
    'L2-RMH-01',
    '空名册、电量 20：服务端置人工充电等待（ROSTER_EMPTY），manualChargingHold=true 的业务状态被真车载端确认，界面充电那一格写「需人工充电：服务端保持」',
    ($held -eq 'ROSTER_EMPTY | True | True'),
    'ROSTER_EMPTY | True | True',
    "$held (text '$(Get-ChargingText)')")
if ($held -ne 'ROSTER_EMPTY | True | True') {
    Add-L2RealNotReached $assertions @('L2-RMH-02', 'L2-RMH-03', 'L2-RMH-04') '服务端没有置等待，或车载端没有显示它'
    return
}

# --- 2. 电量回升不解除 ----------------------------------------------------------------------------------------

Set-Battery 80
# Independent review S7: a demand is waiting while the hold is on, and it is not taken -- the hold, not an empty catalogue, is
# what keeps the vehicle from work. The same demand is the one L2-RMH-04 sees taken once the hold is lifted.
$guid = [guid]::NewGuid()
$journal.Note("Publishing demand $($guid.ToString('N')) while the hold is on.")
$null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
    sublot = "L2-RMH-$($Context.RunId)"; area = 'N1-3'; eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})
function Get-DemandIntents {
    $row = Read-L2SingleRow -Connection $connection -Sql (
        "SELECT COUNT(*) AS N FROM OrderIntents WHERE DemandId = '$($guid.ToString('D'))'")
    return [int]$row.N
}
$stillHeld = Wait-L2ConditionOrLast -Description 'the hold was lifted by the battery coming back, or the demand was taken (neither may be)' `
    -Journal $journal -Criterion 'hold-kept-after-battery' -TimeoutSeconds 10 `
    -Probe { "$(Get-Hold) | $([string](Get-ChargingText) -like "*$holdText*") | intents $(Get-DemandIntents)" } `
    -Until { param($v) $v -ne 'ROSTER_EMPTY | True | intents 0' }
$assertions.Add(
    'L2-RMH-02',
    '电量回到 80、并有一条需求在等之后十秒里，服务端的等待仍在、界面仍写着「需人工充电：服务端保持」，那条需求没有派给它（没有任何订单意图）：电量回升本身不解除，等待期间不派单',
    ($stillHeld -eq 'ROSTER_EMPTY | True | intents 0'),
    'ROSTER_EMPTY | True | intents 0',
    $stillHeld)

# --- 3. 管理员点「充电后返回服务」：解除 -------------------------------------------------------------------------

$offered = Wait-G3ButtonOffered $onboard $journal '充电后返回服务' 'return-entry' 90
if (-not $offered) {
    Add-L2RealNotReached $assertions @('L2-RMH-03', 'L2-RMH-04') '车载端没有给出「充电后返回服务」入口'
    return
}
$journal.Note('Administrator presses 充电后返回服务 and confirms.')
$null = Invoke-G3ConfirmedButton $onboard $journal '充电后返回服务' '充电后返回服务'
$decision = Wait-L2ConditionOrLast -Description 'the server decided the return to service' -Journal $journal `
    -Criterion 'return-decided' -TimeoutSeconds 60 `
    -Probe {
        Read-L2SingleRow -Connection $connection -Sql (
            "SELECT RequestId, Outcome FROM ManualChargingReturnToServiceRequests")
    } `
    -Until { param($v) $null -ne $v }
$lifted = Wait-L2ConditionOrLast -Description 'the hold was lifted on both ends' -Journal $journal -Criterion 'hold-lifted' `
    -TimeoutSeconds 60 `
    -Probe {
        $record = Read-L2SingleRow -Connection $connection -Sql (
            "SELECT IFNULL(ReleaseRequestId, '') AS RequestId, IFNULL(ReleasedAt, '') AS ReleasedAt FROM ManualChargingHoldRecords " +
            "WHERE VehicleKey = '$vehicleKey'")
        "$(Get-Hold) | released $(if ($null -ne $record -and [string]$record.ReleasedAt -ne '') { [string]$record.RequestId } else { '(no)' }) | " +
            "$(Get-AcknowledgedHold) | $([string](Get-ChargingText) -like "*$holdText*")"
    } `
    -Until { param($v) $v.StartsWith('(none) | released ') -and $v.EndsWith('| False | False') }
$expectedLift = "(none) | released $(${decision}?.RequestId) | False | False"
$assertions.Add(
    'L2-RMH-03',
    '管理员点「充电后返回服务」并确认：服务端受理为 RETURNED_TO_ELIGIBILITY_EVALUATION，等待删掉、经过上写着解除它的那个请求号；manualChargingHold=false 的业务状态被确认，界面不再写等待',
    ([string]${decision}?.Outcome -eq 'RETURNED_TO_ELIGIBILITY_EVALUATION' -and $lifted -eq $expectedLift),
    "RETURNED_TO_ELIGIBILITY_EVALUATION / $expectedLift",
    "$(${decision}?.Outcome) / $lifted")

# --- 4. 解除之后车重新可派 ---------------------------------------------------------------------------------------

$pickup = Wait-L2ConditionOrLast -Description 'the vehicle took the demand after the hold was lifted' -Journal $journal `
    -Criterion 'dispatched-after-return' -TimeoutSeconds 90 `
    -Probe {
        Read-L2SingleRow -Connection $connection -Sql (
            "SELECT Status, VehicleKey FROM OrderIntents WHERE DemandId = '$($guid.ToString('D'))' AND Purpose = 'TO_PICKUP'")
    } `
    -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
$assertions.Add(
    'L2-RMH-04',
    '解除之后车重新可派：等待期间没派出去的那条需求派给了这辆车，开往取货站的单已确认',
    ([string]${pickup}?.Status -eq 'CONFIRMED' -and [string]${pickup}?.VehicleKey -eq $vehicleKey),
    "CONFIRMED $vehicleKey",
    "$(${pickup}?.Status) $(${pickup}?.VehicleKey)")

$journal.Note('Real onboard: the server-held manual charging hold was shown, outlived the battery coming back, and was lifted only by 充电后返回服务.')
