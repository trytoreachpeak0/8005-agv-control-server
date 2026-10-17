#Requires -Version 7

<#
只做待送的车空闲时开回派工待送点（control-server#752）：车不在待送点、没有需求，空闲返回的目标是待送固定起点 305（现场是 217），
不是等待点；那个站是单车位公共站，按 REQ-0204 以 FIXED_TASK_STATION 预占，到站后转占用。

**装置**：两台合成车，车队配置里都只接 STAGING_TO_WIRE；两个等待点都在路网上、都空着（setup 文件写了为什么）。开局两台车停在
关卡，没有需求。

**一、开往待送点（本票的判据）**：两台车各承诺一趟空闲返回。先承诺的那台（记为 A）的单开往 305，305 被 A 以 FIXED_TASK_STATION
预占；另一台（B）看得见 305 已被占，这一次回落到等待点，等待点以 WAITING_POINT 预占。

**二、到站收敛**：两趟开完。A 停在 305，305 转为 A 的占用，A 的 IDLE_RETURN 用途释放、旅程收尾。

**三、留在待送点**：A 在 305 停十几轮，不再取得 IDLE_RETURN 用途占有、不建新的空闲返回单（control-server#712 的规则接着管）；
空闲返回看板上 A 的结论码是 IDLE_RETURN_HELD_AT_STAGING_ORIGIN。

**红证据**（缺陷版本）：去掉评估器里开回待送点的分支，A 的单开往最近的等待点，L2-SRO-01 变红。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$connection = $Context.Connection

$staging = 305
$waitingPoints = @(214, 215)
$keys = @([string]$Context.VehicleKey, 'BROKERX-L2-0002')
$agvIdByKey = @{ [string]$Context.VehicleKey = [string]$Context.AgvId; 'BROKERX-L2-0002' = 'AGV-L2-002' }

function Get-Intent([string]$Where) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT UpperId, OrderId, Status, VehicleKey, DestinationStationId FROM OrderIntents WHERE $Where")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Count([string]$Sql) {
    return [int](Invoke-L2Query -Connection $connection -Sql $Sql)[0].Total
}

function Get-Held([int]$Station) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT VehicleKey, JourneyId, State, StationKind FROM StationExclusivities " +
        "WHERE MapId = $($Context.MapId) AND StationId = $Station")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-IdleReturnClaim([string]$VehicleKey) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId FROM VehiclePurposeClaims WHERE VehicleKey = '$VehicleKey' AND Purpose = 'IDLE_RETURN'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Position([string]$VehicleKey) {
    $vehicle = @($riot.Snapshot().body.vehicles) | Where-Object { $null -ne $_ -and [string]$_.deviceKey -eq $VehicleKey } | Select-Object -First 1
    return $vehicle ? [int]$vehicle.currentPosition : $null
}

# RIoT 把单置为执行中并绑车，车动起来，然后停在目的站上，单置为完成（同 staging-only-vehicle-stays-at-staging-origin）。
function Move-Vehicle([string]$VehicleKey, [object]$Intent, [int]$Station) {
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 3; executeVehicleKey = $VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $Intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Station; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 5 })
}

# --- 0. 路网拉起来 -------------------------------------------------------------------------------------------------

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

# --- 一、开往待送点 ------------------------------------------------------------------------------------------------

# 两台车的空闲返回单都确认了。预占在承诺那一次保存里写下，早于单的物化与确认，所以等到单确认之后读预占不会读到旧的。
$intents = @{}
foreach ($key in $keys) {
    $intents[$key] = Wait-L2Condition -Description "the idle return intent of $key was confirmed" -Journal $journal `
        -Criterion "idle-return-confirmed-$key" -TimeoutSeconds 120 `
        -Probe { Get-Intent "VehicleKey = '$key' AND Purpose = 'TO_WAITING_POINT'" } `
        -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
}
$keyA = @($keys | Where-Object { [int]$intents[$_].DestinationStationId -eq $staging }) | Select-Object -First 1
$keyB = @($keys | Where-Object { $_ -ne $keyA }) | Select-Object -First 1
$heldStaging = Get-Held $staging
$destinationB = [int]$intents[$keyB].DestinationStationId
$heldB = Get-Held $destinationB
$journal.Note("A = $keyA heading for $staging; B = $keyB heading for $destinationB.")
$assertions.Add('L2-SRO-01', '只做待送的车开局空闲：一台（A）的空闲返回单开往待送点 305，305 被 A 以 FIXED_TASK_STATION 预占；另一台（B）这一次回落到等待点，以 WAITING_POINT 预占',
    ($null -ne $keyA -and $null -ne $heldStaging -and [string]$heldStaging.VehicleKey -eq $keyA -and
        [string]$heldStaging.StationKind -eq 'FIXED_TASK_STATION' -and [string]$heldStaging.State -eq 'RESERVED' -and
        $waitingPoints -contains $destinationB -and $null -ne $heldB -and [string]$heldB.VehicleKey -eq $keyB -and
        [string]$heldB.StationKind -eq 'WAITING_POINT'),
    "A -> $staging FIXED_TASK_STATION RESERVED / B -> 214|215 WAITING_POINT",
    "A=$keyA -> $staging $(${heldStaging}?.VehicleKey) $(${heldStaging}?.StationKind) $(${heldStaging}?.State) / B=$keyB -> $destinationB $(${heldB}?.VehicleKey) $(${heldB}?.StationKind)")
if ($null -eq $keyA) {
    # 缺陷版本两台都去了等待点：没有 A，后面两段的前提不成立。
    foreach ($id in @('L2-SRO-02', 'L2-SRO-03')) {
        $assertions.Add($id, '未到达：没有一台车的空闲返回开往待送点', $false, '(reached)', '(not reached) no idle return to the staging origin')
    }
    return
}

# --- 二、到站收敛 --------------------------------------------------------------------------------------------------

foreach ($key in $keys) { Move-Vehicle $key $intents[$key] ([int]$intents[$key].DestinationStationId) }
$journeyA = [string](Get-Held $staging)?.JourneyId
# 收敛是一次保存：预占转占用、用途释放、旅程收尾。等旅程收尾，读到的占用与用途是同一次提交之后的。
$convergedA = Wait-L2Condition -Description "A's idle return to the staging origin converged" -Journal $journal `
    -Criterion 'staging-idle-return-converged' -TimeoutSeconds 60 `
    -Probe { [string](Invoke-L2Query -Connection $connection -Sql "SELECT Stage FROM JourneyRuntimes WHERE JourneyId = '$journeyA'")[0].Stage } `
    -Until { param($v) $v -eq 'Completed' }
$occupied = Get-Held $staging
$claimA = Get-IdleReturnClaim $keyA
$assertions.Add('L2-SRO-02', 'A 到站：旅程 Completed，305 转为 A 的 FIXED_TASK_STATION 占用，A 的 IDLE_RETURN 用途已释放，A 在 305',
    ($convergedA -eq 'Completed' -and $null -ne $occupied -and [string]$occupied.VehicleKey -eq $keyA -and
        [string]$occupied.StationKind -eq 'FIXED_TASK_STATION' -and [string]$occupied.State -eq 'OCCUPIED' -and
        $null -eq $claimA -and (Get-Position $keyA) -eq $staging),
    "Completed / $staging OCCUPIED by A / no claim / at $staging",
    "$convergedA / $(${occupied}?.State) by $(${occupied}?.VehicleKey) $(${occupied}?.StationKind) / claim $(${claimA}?.JourneyId) / at $(Get-Position $keyA)")

# --- 三、留在待送点 ------------------------------------------------------------------------------------------------

$intentsBefore = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE VehicleKey = '$keyA' AND Purpose = 'TO_WAITING_POINT'"
$null = Wait-L2Iterations -Riot $riot -Count 6 -Journal $journal
$claimed = Wait-L2ConditionOrLast -Description 'A committed to another idle return while standing at the staging origin' -Journal $journal `
    -Criterion 'no-idle-return-at-staging' -TimeoutSeconds 20 -Probe { Get-IdleReturnClaim $keyA } -Until { param($v) $null -ne $v }
$intentsAfter = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE VehicleKey = '$keyA' AND Purpose = 'TO_WAITING_POINT'"
$idleReturnsEndpoint = "http://127.0.0.1:$($Context.HealthPort)/api/dashboard/idle-returns"
$agvIdA = $agvIdByKey[$keyA]
$verdictA = Wait-L2Condition -Description "the idle-return card shows A's verdict as held at the staging origin" -Journal $journal `
    -Criterion 'dashboard-verdict-held-at-staging' -TimeoutSeconds 30 `
    -Probe {
        $fact = (Invoke-WebRequest -Uri $idleReturnsEndpoint -NoProxy -TimeoutSec 10).Content | ConvertFrom-Json -DateKind String
        $row = @($fact.vehicles | Where-Object { [string]$_.agvId -eq $agvIdA }) | Select-Object -First 1
        $row ? [string]$row.verdict.reasonCode : $null
    } `
    -Until { param($v) $v -eq 'IDLE_RETURN_HELD_AT_STAGING_ORIGIN' }
$assertions.Add('L2-SRO-03', 'A 在 305 停了十几轮：没有新的 IDLE_RETURN 用途、没有新的空闲返回单，车仍在 305；看板结论码 IDLE_RETURN_HELD_AT_STAGING_ORIGIN',
    ($null -eq $claimed -and $intentsAfter -eq $intentsBefore -and (Get-Position $keyA) -eq $staging -and
        $verdictA -eq 'IDLE_RETURN_HELD_AT_STAGING_ORIGIN'),
    "no claim / $intentsBefore intents / at $staging / IDLE_RETURN_HELD_AT_STAGING_ORIGIN",
    "claim $(${claimed}?.JourneyId) / $intentsAfter intents / at $(Get-Position $keyA) / $verdictA")

$journal.Note('Scenario finished.')
