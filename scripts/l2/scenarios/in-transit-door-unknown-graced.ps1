#Requires -Version 7

<#
control-server#789（CP-0017 修订项一，REQ-0246）：车在路上，车载端报「仓位状态未知」（SLOT_STATE_UNKNOWN，IO 读不到锁反馈）约 1 秒后
读回锁闭。服务端从收到那一刻起宽限 3 秒：这 1 秒里不记故障、不发 Hold、不急停、不 Cancel；读回锁闭之后也什么都不发。

修订前（CP-0011 到 CP-0017）同一轮就按住、证不出停稳就急停——2026-10-19 agv01 行驶中被急停、退出服务的就是这一下。

**判据断「什么都没发」，要有界**：读回锁闭之后再转 6 轮（每轮 1 秒，比宽限 3 秒长一倍）才判，审计表与假 RIoT 两侧都看。
「该发时才发」的正半条（读不到满 3 秒就按住、急停）在 in-transit-door-unknown-stopped-released。

取红证据：宽限设为 0（setup 里 `VehicleFault.DoorUnknownGrace = '00:00:00'`，即 CP-0017 之前的行为）。L2-DG-01 照常通过，
L2-DG-02～04 FAIL：读不到那一轮就按住并急停（见 PR 证据目录 red/）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$connection = $Context.Connection
$agvId = [string]$Context.AgvId
$vehicleKey = [string]$Context.VehicleKey

function Get-Attempts([string]$commandType) {
    # 调用方先赋值再用，不要把这个函数直接送进管道：`Invoke-L2Query` 的 `return , $rows` 包装穿得过一层 return。
    return Invoke-L2Query -Connection $connection -Sql @"
SELECT CommandType, AgvId, TargetUpperId, AttemptNumber, IssuedAt, Outcome, FaultGeneration, ReceiptJson
FROM RiotOrderCommandAudit WHERE CommandType = '$commandType' ORDER BY AttemptNumber
"@
}

function Get-RiotInvocations([string]$commandType) {
    # `return , @(...)`：只有一个元素时 `return @(...)` 会被拆成那个元素本身，严格模式下取 `.Count` 就抛错。
    return , @(@($riot.Snapshot().body.commandInvocations) | Where-Object { [string]$_.commandType -eq $commandType })
}

function Set-Vehicle([hashtable]$fields) {
    $body = @{ vehicleKey = $vehicleKey } + $fields
    $null = $riot.Command('Put', 'vehicle', $body)
}

function Set-Doors([ValidateSet('Locked', 'Unknown')][string]$doors) {
    # IO 读不到时真车载端报的样子（真装置实测，control-server#335）：带 LOCK_NOT_CLOSED、allTargetSlotsLocked=false。
    # Typed on purpose: an `if` expression unrolls a one-element array to its string (in-transit-door-not-locked).
    [string[]]$reasons = if ($doors -eq 'Locked') {
        @('ACTION_NOT_ALLOWED_IN_STATE')
    } else {
        @('SLOT_STATE_UNKNOWN', 'LOCK_NOT_CLOSED', 'UNLOCK_OUTPUT_NOT_RESET', 'ACTION_NOT_ALLOWED_IN_STATE')
    }
    $null = Set-L2OnboardSafety -Onboard $onboard -Connection $connection -AgvId $agvId -Journal $journal -Safety @{
        departureSafe         = $false
        vehicleStopped        = $false
        allTargetSlotsLocked  = ($doors -eq 'Locked')
        allUnlockOutputsReset = ($doors -eq 'Locked')
        unknownPresent        = ($doors -ne 'Locked')
        reasonCodes           = $reasons
        affectedSlots         = @(1, 2, 3, 4, 5, 6, 7, 8)
    }
}

function Get-RaisedCounts {
    $holds = Get-Attempts 'OrderHold'
    $triggers = Get-Attempts 'triggerEmergency'
    $faults = Invoke-L2Query -Connection $connection -Sql "SELECT Level FROM VehicleFaultStates WHERE AgvId = '$agvId' AND Level <> 'None'"
    return [pscustomobject]@{
        Holds        = $holds.Count
        Triggers     = $triggers.Count
        Faults       = $faults.Count
        RiotHolds    = (Get-RiotInvocations 'CMD_ORDER_HELD').Count
        RiotTriggers = (Get-RiotInvocations 'triggerEmergency').Count
        RiotCancels  = (Get-RiotInvocations 'CMD_ORDER_CANCEL').Count
    }
}

function Format-Counts($counts) {
    "hold $($counts.Holds)/$($counts.RiotHolds) trigger $($counts.Triggers)/$($counts.RiotTriggers) fault $($counts.Faults) cancel $($counts.RiotCancels)"
}

# --- 1. 一台车上路 ----------------------------------------------------------------------------------

$demandId = [guid]::NewGuid().ToString('N')
$journal.Note("Publishing demand $demandId.")
$null = $mes.Command('Put', "demands/$demandId", @{
    sublot      = "L2-DG-$($Context.RunId)"
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 4
})

$journey = Wait-L2Condition -Description 'the vehicle took a journey' `
    -Journal $journal -Criterion 'journey-dispatched' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql "SELECT DemandId FROM JourneyRuntimes WHERE AgvId = '$agvId'"
        if ($rows.Count -ge 1) { $rows[0] } else { $null }
    } `
    -Until { param($v) $null -ne $v }

$intent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'pickup-intent' -TimeoutSeconds 90 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection `
            -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$([string]$journey.DemandId)' AND Purpose = 'TO_PICKUP'"
        if ($rows.Count -ge 1 -and [string]$rows[0].Status -eq 'CONFIRMED') { $rows[0] } else { $null }
    } `
    -Until { param($v) $null -ne $v }
$upperId = [string]$intent.UpperId

# --- 2. 车在两站之间行驶、门锁着：什么都不发 ----------------------------------------------------------

$null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $vehicleKey })
Set-Vehicle @{ movementState = 'MT_RUNNING'; speed = 0.55; processingOrder = $true; currentPosition = 0 }
Set-Doors 'Locked'
$null = Wait-L2Iterations -Riot $riot -Count 3 -TimeoutSeconds 60 -Journal $journal
$before = Get-RaisedCounts
$assertions.Add(
    'L2-DG-01', '车在正常行驶、门锁着时，不发 Hold、不发急停、没有故障',
    ($before.Holds + $before.Triggers + $before.Faults -eq 0), 'hold 0/0 trigger 0/0 fault 0 cancel 0', (Format-Counts $before))

# --- 3. 读不到约 1 秒，读回锁闭 -----------------------------------------------------------------------

$journal.Note("The onboard reports a slot it cannot read while $agvId drives on $upperId; about a second later, locked again.")
$unknownAt = [DateTimeOffset]::UtcNow
Set-Doors 'Unknown'
$null = Wait-L2Iterations -Riot $riot -Count 1 -TimeoutSeconds 30 -Journal $journal
$during = Get-RaisedCounts
Set-Doors 'Locked'
$lockedAt = [DateTimeOffset]::UtcNow
$gap = ($lockedAt - $unknownAt).TotalSeconds
$journal.Note(("Doors unread for {0:0.0} s." -f $gap))
$assertions.Add(
    'L2-DG-02', '读不到的那 1 秒多里（宽限 3 秒之内）：不发 Hold、不发急停、没有故障',
    ($gap -lt 2.5 -and $during.Holds + $during.Triggers + $during.Faults + $during.RiotHolds + $during.RiotTriggers -eq 0),
    'gap < 2.5 s / hold 0/0 trigger 0/0 fault 0', ("gap {0:0.0} s / {1}" -f $gap, (Format-Counts $during)))

# 读回锁闭之后再转 6 轮：宽限早就过了，若计时没被锁闭清零，这里会按住、急停。
$null = Wait-L2Iterations -Riot $riot -Count 6 -TimeoutSeconds 60 -Journal $journal
$after = Get-RaisedCounts
$assertions.Add(
    'L2-DG-03', '读回锁闭之后再转 6 轮：审计表与 RIoT 两侧都没有 Hold、急停、Cancel，也没有故障事实',
    ($after.Holds + $after.Triggers + $after.Faults + $after.RiotHolds + $after.RiotTriggers + $after.RiotCancels -eq 0),
    'hold 0/0 trigger 0/0 fault 0 cancel 0', (Format-Counts $after))

$stage = Invoke-L2Query -Connection $connection -Sql "SELECT BlockReasonCode FROM JourneyRuntimes WHERE AgvId = '$agvId'"
$code = if ($stage.Count -gt 0) { [string]$stage[0].BlockReasonCode } else { '-' }
$assertions.Add(
    'L2-DG-04', '旅程码不是门锁症状（车照常在路上）',
    ($code -ne 'VEHICLE_DOOR_NOT_PROVEN_LOCKED'), 'not VEHICLE_DOOR_NOT_PROVEN_LOCKED', $code)

$journal.Note('Scenario finished.')
