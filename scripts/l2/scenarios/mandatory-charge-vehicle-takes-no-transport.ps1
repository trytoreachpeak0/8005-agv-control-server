#Requires -Version 7

<#
低于强制充电线的车不接搬运，原地不动（批次9-05，control-server#403；REQ-0290、REQ-0281）。

编排器的默认前置导入、批准、激活一版测试策略（强制充电线 30、余量 30、每趟估计 0）。主车 A（BROKERX-L2-0001）的电量用合成 RIoT 的
控制面设成 25、停在取货站上，第二台 B（BROKERX-L2-0002）是 80、停在关卡：路网开着，A 离需求更近，没有电量这一段时需求是 A 的。空闲返回不打开
（setup 文件写了为什么），那一半由 L1 钉住。

  1. 电量设好之后发一条需求。断言：需求派给 B，不是排在前面的 A；旅程记下判它的策略版本与 SUFFICIENT。
  2. 第二个事实另等（scripts/l2/README.md 第 14 条）：服务端日志里有 A 进入强制充电的那一行（MANDATORY_CHARGE_REQUIRED）；在一个十几秒
     的窗口里，A 没有任何建单（库里 OrderIntents、合成 RIoT 的订单表）、没有用途占有、没有站点独占。「一直没有」要持续成立，所以用
     Wait-L2ConditionOrLast 等满窗口再读最后一次，不在受理那一刻读一次就断言。

红证据（缺陷版本）：把 BatteryEligibility.Judge 里阈值那一段（强制充电线与任务后余量）去掉，需求派给更近的 A，L2-MCT-01 变红。只去掉入口线
那一条不够：默认测试策略的余量也是 30，25 − 0 仍低于余量，被余量那一条挡住（换成 BATTERY_POLICY_NOT_SATISFIED，派车结论不变）。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$mes = $Context.MesIngest
$riot = $Context.Riot

$lowAgvId = $Context.AgvId
$lowVehicleKey = $Context.VehicleKey
$fineAgvId = 'AGV-L2-002'
$fineVehicleKey = 'BROKERX-L2-0002'
$lowBattery = 25
$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')
$serverLog = Join-Path $Context.LogRoot 'control-server.out.log'
$enteredLine = "Vehicle $lowVehicleKey is below its mandatory charge entry threshold: battery $lowBattery% < 30%"

function Read-SharedText([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { return [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
}

# Counted in SQL: a query with no rows comes back as $null, and @($null).Count is 1 (charging-policy-missing-vehicle-not-commissioned).
function Get-Count([string]$sql) { [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].N }

function Get-FootprintOf([string]$vehicleKey) {
    $intents = Get-Count "SELECT COUNT(*) AS N FROM OrderIntents WHERE VehicleKey = '$vehicleKey'"
    $claims = Get-Count "SELECT COUNT(*) AS N FROM VehiclePurposeClaims WHERE VehicleKey = '$vehicleKey'"
    $stations = Get-Count "SELECT COUNT(*) AS N FROM StationExclusivities WHERE VehicleKey = '$vehicleKey'"
    $riotOrders = [int](@($riot.Snapshot().body.orders | Where-Object { $null -ne $_ -and [string]$_.appointVehicleKey -eq $vehicleKey }) |
        Measure-Object).Count
    return "$intents intents, $claims purpose claims, $stations station holds, $riotOrders RIoT orders"
}

# --- 0. 前置：默认测试策略，A 低于线、B 充足，两车都停在关卡 --------------------------------------------------

$policy = @(Invoke-L2Query -Connection $connection -Sql (
    'SELECT v.Version, v.MandatoryChargeEntryThresholdPercent AS Entry, v.MinimumPostTaskBatteryMarginPercent AS Margin, ' +
    'v.EstimatedTaskConsumptionPercent AS Estimate FROM ChargingPolicyVersions v ' +
    'JOIN ChargingPolicyActivations x ON x.Version = v.Version'))
$assertions.Add(
    'L2-MCT-00',
    '前置：唯一一版已激活的策略是默认测试策略（强制充电线 30、余量 30、每趟估计 0）',
    ($policy.Count -eq 1 -and [int]$policy[0].Entry -eq 30 -and [int]$policy[0].Margin -eq 30 -and [int]$policy[0].Estimate -eq 0),
    '1 版 / 30 / 30 / 0',
    (($policy | ForEach-Object { "v$($_.Version) $($_.Entry)/$($_.Margin)/$($_.Estimate)" }) -join ', '))

# A 停在取货站上（离这条需求最近），B 停在关卡：距离层排在电量层之前，所以没有强制充电那一条时需求是 A 的。两车停在同一个站时，
# 电量这一末级裁决本来就让 80% 的 B 排在 25% 的 A 前面，红证据就证不出东西——第一次取红时正是这样（工作区 evidence/cs403/）。
foreach ($vehicle in @(
        @{ Key = $lowVehicleKey; Battery = $lowBattery; Station = $Context.PickupStationRiotId },
        @{ Key = $fineVehicleKey; Battery = 80; Station = $Context.GateStationRiotId })) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $vehicle.Key; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $vehicle.Station; battery = $vehicle.Battery
    })
}

# 路网就绪之后再发需求：就绪之前路网判据挡住一切，两车分不出远近。
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

# --- 1. 电量设好、路网就绪之后发需求：派给 B -------------------------------------------------------------------

$journal.Note("Publishing demand $($demandGuid.ToString('N')) (area N1-3) with A at $lowBattery %.")
$null = $mes.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = "L2-MCT-$($Context.RunId)"; area = 'N1-3'
    eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})

$journey = Wait-L2Condition -Description 'a vehicle took the demand and set off to its pickup' `
    -Journal $journal -Criterion 'journey-accepted' -TimeoutSeconds 180 `
    -Probe {
        $rows = @(Invoke-L2Query -Connection $connection -Sql (
            "SELECT JourneyId, AgvId, VehicleKey, ChargingPolicyVersion, PublishedBatteryState FROM JourneyRuntimes WHERE DemandId = '$demandId'"))
        if ($rows.Count -eq 0) { return $null }
        return $rows[0]
    } `
    -Until { param($v) $null -ne $v }

$assertions.Add(
    'L2-MCT-01',
    '需求派给电量充足的 B，不是离需求更近、低于强制充电线的 A；旅程记下判它的策略版本与 SUFFICIENT',
    ([string]$journey.VehicleKey -eq $fineVehicleKey -and [string]$journey.AgvId -eq $fineAgvId -and
        [string]$journey.ChargingPolicyVersion -eq [string]$policy[0].Version -and [string]$journey.PublishedBatteryState -eq 'SUFFICIENT'),
    "$fineVehicleKey / $fineAgvId / v$($policy[0].Version) / SUFFICIENT",
    "$($journey.VehicleKey) / $($journey.AgvId) / v$($journey.ChargingPolicyVersion) / $($journey.PublishedBatteryState)")

# --- 2. 第二个事实另等：整个窗口里 A 哪儿也不去 ---------------------------------------------------------------

$logged = Wait-L2ConditionOrLast -Description "the server logged that $lowVehicleKey entered mandatory charging" `
    -Journal $journal -Criterion 'low-vehicle-logged' -TimeoutSeconds 60 `
    -Probe { (Read-SharedText $serverLog).Contains($enteredLine, [StringComparison]::Ordinal) } `
    -Until { param($v) $v -eq $true }
$assertions.Add(
    'L2-MCT-02',
    'A 进入强制充电（派车侧 MANDATORY_CHARGE_REQUIRED，服务端日志）',
    ($logged -eq $true),
    $enteredLine,
    $(if ($logged) { 'found' } else { 'not found' }))

# 「一直没有」：等满窗口（十几轮派车），读最后一次。
$nothing = '0 intents, 0 purpose claims, 0 station holds, 0 RIoT orders'
$footprint = Wait-L2ConditionOrLast -Description "$lowVehicleKey got an order, a purpose or a station (it must not)" `
    -Journal $journal -Criterion 'low-vehicle-footprint-after-window' -TimeoutSeconds 15 `
    -Probe { Get-FootprintOf $lowVehicleKey } `
    -Until { param($v) $v -ne $nothing }
$journal.Observe('low-vehicle-footprint', $footprint, @{ vehicleKey = $lowVehicleKey; battery = $lowBattery })
$assertions.Add(
    'L2-MCT-03',
    'A 在整个窗口里没有建单、没有用途占有、没有站点独占：原地不动（批次9-06 之前）',
    ($footprint -eq $nothing),
    $nothing,
    $footprint)

$journal.Note('低于强制充电线的车：搬运派给了另一辆，它哪儿也不去。')
