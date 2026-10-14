#Requires -Version 7

<#
只做待送的车留在派工待送点（control-server#712）：旅程在待送点结束、车上没有需求，车留在原地，不空闲返回；下一条待送需求
它原地受理，开往待送点的取货单照常建、到站、录入请求照常发到车上。

**装置**：两台合成车，车队配置里都只接 STAGING_TO_WIRE；两个等待点都在路网上（setup 文件写了为什么）。开局两台车停在关卡、
没有需求，各自被空闲返回派到一个等待点——场景把这两趟开完，两台车各停在一个等待点上。

**一、旅程在待送点结束**：一条待送需求被其中一台车（记为 V）接走，V 开到待送点 305；没人扫码，操作员扫码前取消，旅程
Completed / CANCELLED_BY_OPERATOR，V 停在 305，305 的单占仍是 V 的（离点证据之前不放）。

**二、车留在原地（本票的判据）**：V 离开等待点之后，它原来那个点被离点证据放掉，空着、开得过去。V 在 305 上停十几轮：
它没有取得 IDLE_RETURN 用途占有，没有新的开往等待点的单。「没有」是一个要持续成立的状态，用 Wait-L2ConditionOrLast 另等
一段，最后断言仍然没有；空闲返回看板上 V 的结论码就是本票的 IDLE_RETURN_HELD_AT_STAGING_ORIGIN（L2-SIS-06）。

**三、新需求原地受理**：再来一条待送需求，派给已在待送点的 V（已在固定起点优先）；开往 305 的取货单在 V 停在 305 时建出，
RIoT 报单完成后到站，录入请求发到 V 的车上——清单可扫。**假 RIoT 照场景的指令把单置为完成，所以这一段只证明本服务端这一侧
的流程；真 RIoT 对「目的站就是当前站」的单怎么处理，不在这里证明（RIoT 行为实验室样本数为 0，上线前实测）。**

**红证据**（缺陷版本）：去掉本票的规则，第二段 V 在 305 收尾后下一轮就承诺空闲返回、建开往等待点的单，L2-SIS-03 变红。
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
$connection = $Context.Connection

$staging = 305
$waitingPoints = @(214, 215)
# 每台车的合成对端，按 AgvId 认（OnboardPeers 的每一项是 Name／Port／AgvId／Double）。
function Get-Peer([string]$AgvId) { return ($Context.OnboardPeers | Where-Object { $_.AgvId -eq $AgvId }).Double }
$peerByKey = @{
    [string]$Context.VehicleKey = Get-Peer $Context.AgvId
    'BROKERX-L2-0002'           = Get-Peer 'AGV-L2-002'
}

function Publish-Demand([string]$Suffix) {
    $guid = [guid]::NewGuid()
    $journal.Note("Publishing STAGING_TO_WIRE demand $Suffix $($guid.ToString('N')).")
    $null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot = "L2-SIS-$Suffix-$($Context.RunId)"; area = 'N1-3'; eqp = 'EQP-L2-SIS-01'
        package = 'L2-PACKAGE'; maxBoxCount = 4; workType = 'STAGING_TO_WIRE'
    })
    return $guid.ToString('D')
}

function Get-Runtime([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, VehicleKey, Stage, BlockReasonCode, SublotRequestMessageId FROM JourneyRuntimes WHERE DemandId = '$DemandId'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$Where) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT UpperId, OrderId, Status, VehicleKey, DestinationStationId, CreatedAt FROM OrderIntents WHERE $Where")
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

# RIoT 把单置为执行中并绑车，车动起来，然后停在目的站上，单置为完成（同 fixed-station-single-occupancy）。
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

function Wait-Confirmed([string]$Where, [string]$Label) {
    return Wait-L2Condition -Description "the $Label intent was confirmed" -Journal $journal -Criterion "intent-$Label" `
        -TimeoutSeconds 120 -Probe { Get-Intent $Where } -Until { param($v) $null -ne $v -and [string]$v.Status -eq 'CONFIRMED' }
}

function Wait-Stage([string]$DemandId, [string]$Stage, [string]$Label) {
    return Wait-L2Condition -Description "$Label reached $Stage" -Journal $journal -Criterion "stage-$Label-$Stage" `
        -TimeoutSeconds 180 -Probe { Get-Runtime $DemandId } -Until { param($v) $null -ne $v -and [string]$v.Stage -eq $Stage }
}

function Get-PeerInbound([object]$Peer, [string]$MessageType) {
    return @(@($Peer.Snapshot().body.wire) | Where-Object {
            [string]$_.direction -eq 'in' -and [string]$_.messageType -eq $MessageType })
}

# --- 0. 路网拉起来；开局两趟空闲返回开完 ---------------------------------------------------------------------------

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

$journal.Note('Both synthetic peers stop answering sublot entry requests: nobody scans at the staging station.')
foreach ($peer in $peerByKey.Values) { $null = $peer.Command('Put', 'policy', @{ sublot = 'Silent' }) }

# 两台车停在关卡、没有需求：各自承诺一趟空闲返回（第一台取最近的 214，第二台看得见它、取 215）。开完它们，两台车各停在一个点上。
foreach ($key in $peerByKey.Keys) {
    $intent = Wait-Confirmed "VehicleKey = '$key' AND Purpose = 'TO_WAITING_POINT'" "idle-return-$key"
    Move-Vehicle $key $intent ([int]$intent.DestinationStationId)
}
$null = Wait-L2Condition -Description 'both opening idle returns converged at their waiting points' -Journal $journal `
    -Criterion 'opening-idle-returns-converged' -TimeoutSeconds 60 `
    -Probe { Get-Count "SELECT COUNT(*) AS Total FROM JourneyRuntimes WHERE JourneyId LIKE 'idle-return:%' AND Stage = 'Completed'" } `
    -Until { param($v) $v -ge 2 }

# --- 一、旅程在待送点结束：V 开到 305，没人扫码，扫码前取消 ---------------------------------------------------------

$first = Publish-Demand 'FIRST'
$firstRuntime = Wait-Stage $first 'AwaitingPickupArrival' 'first demand'
$keyV = [string]$firstRuntime.VehicleKey
$peerV = $peerByKey[$keyV]
$pointV = ($waitingPoints | Where-Object { [string](Get-Held $_)?.VehicleKey -eq $keyV } | Select-Object -First 1)
$journal.Note("V = $keyV, standing at waiting point $pointV.")

Move-Vehicle $keyV (Wait-Confirmed "DemandId = '$first' AND Purpose = 'TO_PICKUP'" 'first-to-pickup') $staging
$firstWaiting = Wait-Stage $first 'AwaitingSublot' 'first demand'
$null = Wait-L2Condition -Description "V's peer received the first sublot entry request" -Journal $journal `
    -Criterion 'first-entry-request-received' -TimeoutSeconds 30 `
    -Probe { @(Get-PeerInbound $peerV 'SublotEntryRequested' | Where-Object { [string]$_.messageId -eq [string]$firstWaiting.SublotRequestMessageId }).Count } `
    -Until { param($v) $v -ge 1 }

$cancellationId = [guid]::NewGuid().ToString('D')
$journal.Note("Operator cancels the first demand before any sublot entry ($cancellationId).")
$null = $peerV.Command('Put', "load-cancellations/$cancellationId", @{
    demandId = $first; slotOperationAttemptId = $null; reason = '现场确认本站没有要装的货。'
})
$ended = Wait-Stage $first 'Completed' 'first demand'
$stagingHeld = Get-Held $staging
$assertions.Add('L2-SIS-01', 'V 在待送点扫码前取消：旅程 Completed / CANCELLED_BY_OPERATOR，V 停在 305，305 的单占仍是 V 的',
    ([string]$ended.BlockReasonCode -eq 'CANCELLED_BY_OPERATOR' -and (Get-Position $keyV) -eq $staging -and
        $null -ne $stagingHeld -and [string]$stagingHeld.VehicleKey -eq $keyV -and [string]$stagingHeld.StationKind -eq 'FIXED_TASK_STATION'),
    "CANCELLED_BY_OPERATOR / at $staging / $staging held by $keyV",
    "$($ended.BlockReasonCode) / at $(Get-Position $keyV) / $staging held by $(${stagingHeld}?.VehicleKey) $(${stagingHeld}?.StationKind)")

# --- 二、车留在原地 -------------------------------------------------------------------------------------------------

# 前提：V 原来那个等待点被离点证据放掉、空着——V 有点可去。没有这一条，下面的「没去」什么也证明不了。
$freed = Wait-L2Condition -Description "V's former waiting point $pointV was released on departure evidence" -Journal $journal `
    -Criterion 'former-point-released' -TimeoutSeconds 60 -Probe { $null -eq (Get-Held $pointV) } -Until { param($v) $v }
$assertions.Add('L2-SIS-02', 'V 离开等待点后那个点被放掉、空着：V 停在 305 时有一个开得过去的等待点',
    ($null -ne $pointV -and $freed), "$pointV free", "$pointV free=$freed")

$idleIntentsBefore = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE VehicleKey = '$keyV' AND Purpose = 'TO_WAITING_POINT'"
$null = Wait-L2Iterations -Riot $riot -Count 6 -Journal $journal
$claimed = Wait-L2ConditionOrLast -Description 'V committed to an idle return while standing at the staging station' -Journal $journal `
    -Criterion 'no-idle-return-at-staging' -TimeoutSeconds 20 -Probe { Get-IdleReturnClaim $keyV } -Until { param($v) $null -ne $v }
$idleIntentsAfter = Get-Count "SELECT COUNT(*) AS Total FROM OrderIntents WHERE VehicleKey = '$keyV' AND Purpose = 'TO_WAITING_POINT'"
$stillHeld = Get-Held $staging
$assertions.Add('L2-SIS-03', 'V 在 305 停了十几轮：没有 IDLE_RETURN 用途占有、没有新的开往等待点的单，车仍在 305，305 仍是 V 的',
    ($null -eq $claimed -and $idleIntentsAfter -eq $idleIntentsBefore -and (Get-Position $keyV) -eq $staging -and
        [string]$stillHeld.VehicleKey -eq $keyV),
    "no claim / $idleIntentsBefore intents / at $staging / held by $keyV",
    "claim $(${claimed}?.JourneyId) / $idleIntentsAfter intents / at $(Get-Position $keyV) / held by $(${stillHeld}?.VehicleKey)")

# 看板上 V 这一轮的空闲返回结论就是本票的码（审查 L2）：上面三条只断言「没发生」，这一条直接读规则答了什么。
$agvIdV = $keyV -eq [string]$Context.VehicleKey ? [string]$Context.AgvId : 'AGV-L2-002'
$idleReturnsEndpoint = "http://127.0.0.1:$($Context.HealthPort)/api/dashboard/idle-returns"
$verdictV = Wait-L2Condition -Description "the idle-return card shows V's verdict as held at the staging origin" -Journal $journal `
    -Criterion 'dashboard-verdict-held-at-staging' -TimeoutSeconds 30 `
    -Probe {
        $fact = (Invoke-WebRequest -Uri $idleReturnsEndpoint -NoProxy -TimeoutSec 10).Content | ConvertFrom-Json -DateKind String
        $row = @($fact.vehicles | Where-Object { [string]$_.agvId -eq $agvIdV }) | Select-Object -First 1
        $row ? [string]$row.verdict.reasonCode : $null
    } `
    -Until { param($v) $v -eq 'IDLE_RETURN_HELD_AT_STAGING_ORIGIN' }
$assertions.Add('L2-SIS-06', '空闲返回看板上 V 这一轮的结论码是 IDLE_RETURN_HELD_AT_STAGING_ORIGIN',
    ($verdictV -eq 'IDLE_RETURN_HELD_AT_STAGING_ORIGIN'), 'IDLE_RETURN_HELD_AT_STAGING_ORIGIN', "$verdictV")

# --- 三、新需求原地受理 ---------------------------------------------------------------------------------------------

$second = Publish-Demand 'SECOND'
$secondRuntime = Wait-Stage $second 'AwaitingPickupArrival' 'second demand'
$pickup = Wait-Confirmed "DemandId = '$second' AND Purpose = 'TO_PICKUP'" 'second-to-pickup'
$assertions.Add('L2-SIS-04', '第二条待送需求派给已在待送点的 V；开往 305 的取货单在 V 停在 305 时建出',
    ([string]$secondRuntime.VehicleKey -eq $keyV -and [string]$pickup.VehicleKey -eq $keyV -and
        [int]$pickup.DestinationStationId -eq $staging -and (Get-Position $keyV) -eq $staging),
    "$keyV / order to $staging / at $staging",
    "$($secondRuntime.VehicleKey) / order to $($pickup.DestinationStationId) by $($pickup.VehicleKey) / at $(Get-Position $keyV)")

Move-Vehicle $keyV $pickup $staging
$secondWaiting = Wait-Stage $second 'AwaitingSublot' 'second demand'
$received = Wait-L2Condition -Description "V's peer received the second sublot entry request" -Journal $journal `
    -Criterion 'second-entry-request-received' -TimeoutSeconds 30 `
    -Probe { @(Get-PeerInbound $peerV 'SublotEntryRequested' | Where-Object { [string]$_.messageId -eq [string]$secondWaiting.SublotRequestMessageId }).Count } `
    -Until { param($v) $v -ge 1 }
$assertions.Add('L2-SIS-05', '取货单完成后 V 在 305 到站，录入请求发到 V 的车上（清单可扫）',
    ([string]$secondWaiting.Stage -eq 'AwaitingSublot' -and $received -ge 1),
    'AwaitingSublot / >= 1', "$($secondWaiting.Stage) / $received")

$journal.Note('Scenario finished.')
