#Requires -Version 7

<#
低于强制充电线的车不接搬运，原地不动（批次9-05，control-server#403；REQ-0290、REQ-0281）。

编排器的默认前置已经激活一版测试策略（强制充电线 30、余量 30），两条线一样高，挡住 25% 的车时分不出是哪一条挡的（审查 S2）。所以场景
经 FieldOps 再导入、批准（L2_PRESET）、激活一版「强制充电线 30、余量 20、每趟估计 0」——与现场用的是同一组动词，不直写库。电量 25 落在
两条线之间：余量那一条放行（25 − 0 ≥ 20），只剩强制充电线挡它。

  1. 两车都设成 25，发一条需求。断言：积压行上的原因码是 MANDATORY_CHARGE_REQUIRED（派车链的结论，不是日志行）；在一个十几秒的窗口里
     没有任何旅程、建单、用途占有或站点独占。「一直没有」要持续成立，所以用 Wait-L2ConditionOrLast 等满窗口再读最后一次。
  2. 只把 B 抬到 80。断言：需求派给 B，旅程记下的是场景激活的那一版与 SUFFICIENT；A 整段没有足迹。

红证据（缺陷版本）：只删 BatteryEligibility.Judge 里强制充电线那一段（IsMandatoryCharge → MandatoryChargeRequired），余量那一条照常。
25 − 0 ≥ 20，两车在第一段就合法，需求当场派出，L2-MCT-01 变红。
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

$fineAgvId = $Context.AgvId
$fineVehicleKey = $Context.VehicleKey
$lowVehicleKey = 'BROKERX-L2-0002'
$fleetText = "$fineVehicleKey;$lowVehicleKey"
$lowBattery = 25
$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')

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

function Set-Battery([string]$vehicleKey, [int]$battery) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $vehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0; battery = $battery
    })
}

function Get-Journey {
    $rows = @(Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, AgvId, VehicleKey, ChargingPolicyVersion, PublishedBatteryState FROM JourneyRuntimes WHERE DemandId = '$demandId'"))
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# --- 0. 前置：两车先设成 25，再导入并激活「强制充电线 30 > 余量 20」的一版 ---------------------------------------

# 电量先于策略：新策略一生效，两车就已经落在两条线之间。反过来的话，新策略生效后、电量改之前那几轮里两车按 80% 都合法，
# 但那时还没有需求，所以次序只是为了读日志时不绕。
Set-Battery $fineVehicleKey $lowBattery
Set-Battery $lowVehicleKey $lowBattery

$policy = [ordered]@{
    minimumPostTaskBatteryMarginPercent  = 20
    mandatoryChargeEntryThresholdPercent = 30
    chargingCompletionThresholdPercent   = 80
    estimatedTaskConsumptionPercent      = 0
    progressStabilizationSeconds         = 180
    progressObservationWindowSeconds     = 600
    progressMinimumIncreasePercent       = 3
    vehicleScope                         = @()
    changeNote                           = 'L2 mandatory-charge-vehicle-takes-no-transport: entry 30 above margin 20, so only the entry line blocks 25 %'
}
$file = Join-Path $Context.SnapshotRoot 'entry-above-margin-charging-policy.json'
[IO.File]::WriteAllText($file, ($policy | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
$imported = & $Context.InvokeFieldOps -Arguments @('import-charging-policy', '--input', $file, '--fleet', $fleetText)
$version = [string]$imported.version
$approved = & $Context.InvokeFieldOps -Arguments @('approve-charging-policy', '--version', $version, '--approved-by', 'L2 scenario',
    '--role', 'L2_PRESET', '--basis', 'scripts/l2/scenarios/mandatory-charge-vehicle-takes-no-transport.ps1', '--source', 'L2_PRESET')
$activated = & $Context.InvokeFieldOps -Arguments @('activate-charging-policy', '--version', $version, '--activated-by', 'L2 scenario',
    '--fleet', $fleetText, '--allow-non-field-approval')
$journal.Observe('scenario-policy', $version, @{ import = $imported; approve = $approved; activate = $activated })

$active = @(Invoke-L2Query -Connection $connection -Sql (
    'SELECT v.Version, v.MandatoryChargeEntryThresholdPercent AS Entry, v.MinimumPostTaskBatteryMarginPercent AS Margin, ' +
    'v.EstimatedTaskConsumptionPercent AS Estimate FROM ChargingPolicyActivations x ' +
    'JOIN ChargingPolicyVersions v ON v.Version = x.Version ORDER BY x.Sequence DESC LIMIT 1'))
$assertions.Add(
    'L2-MCT-00',
    '前置：最后一次激活的是场景导入的那一版（强制充电线 30、余量 20、每趟估计 0）',
    ([string]$imported.outcome -eq 'OK' -and [string]$approved.outcome -eq 'OK' -and [string]$activated.outcome -eq 'OK' -and
        $active.Count -eq 1 -and [string]$active[0].Version -eq $version -and
        [int]$active[0].Entry -eq 30 -and [int]$active[0].Margin -eq 20 -and [int]$active[0].Estimate -eq 0),
    "OK/OK/OK, v$version 30/20/0",
    "$($imported.outcome)/$($approved.outcome)/$($activated.outcome), " +
        (($active | ForEach-Object { "v$($_.Version) $($_.Entry)/$($_.Margin)/$($_.Estimate)" }) -join ', '))

# --- 1. 两车都在两条线之间：派车链答 MANDATORY_CHARGE_REQUIRED，整个窗口零派出 ------------------------------------

$journal.Note("Publishing demand $($demandGuid.ToString('N')) (area N1-3) with both vehicles at $lowBattery %.")
$null = $mes.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = "L2-MCT-$($Context.RunId)"; area = 'N1-3'
    eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})

$reason = Wait-L2ConditionOrLast -Description 'the dispatch chain wrote its verdict on the demand' `
    -Journal $journal -Criterion 'backlog-reason' -TimeoutSeconds 60 `
    -Probe {
        $rows = @(Invoke-L2Query -Connection $connection -Sql "SELECT ReasonCode FROM JourneyBacklog WHERE DemandId = '$demandId'")
        if ($rows.Count -eq 0) { return $null }
        return [string]$rows[0].ReasonCode
    } `
    -Until { param($v) $v -eq 'MANDATORY_CHARGE_REQUIRED' }

# 「一直没有」：等满窗口（十几轮派车），读最后一次。
$nothing = '0 journeys; 0 intents, 0 purpose claims, 0 station holds, 0 RIoT orders; 0 intents, 0 purpose claims, 0 station holds, 0 RIoT orders'
$window = Wait-L2ConditionOrLast -Description 'a vehicle below its mandatory charge line got the demand (none may)' `
    -Journal $journal -Criterion 'nothing-dispatched-below-the-line' -TimeoutSeconds 15 `
    -Probe {
        $journeys = Get-Count "SELECT COUNT(*) AS N FROM JourneyRuntimes WHERE DemandId = '$demandId'"
        "$journeys journeys; $(Get-FootprintOf $fineVehicleKey); $(Get-FootprintOf $lowVehicleKey)"
    } `
    -Until { param($v) $v -ne $nothing }
$assertions.Add(
    'L2-MCT-01',
    '两车都在强制充电线 30 与余量 20 之间：派车链答 MANDATORY_CHARGE_REQUIRED，整个窗口里没有旅程、建单、用途占有与站点独占',
    ($reason -eq 'MANDATORY_CHARGE_REQUIRED' -and $window -eq $nothing),
    "MANDATORY_CHARGE_REQUIRED / $nothing",
    "$reason / $window")

# --- 2. 只把 B 抬到 80：需求派给 B，A 仍然哪儿也不去 -------------------------------------------------------------

Set-Battery $fineVehicleKey 80
$journey = Wait-L2ConditionOrLast -Description 'the vehicle raised above the line took the demand' `
    -Journal $journal -Criterion 'journey-accepted' -TimeoutSeconds 180 `
    -Probe { Get-Journey } `
    -Until { param($v) $null -ne $v }
$assertions.Add(
    'L2-MCT-02',
    'B 抬到 80 之后需求派给 B；旅程记下场景激活的那一版与 SUFFICIENT',
    ($null -ne $journey -and [string]$journey.VehicleKey -eq $fineVehicleKey -and [string]$journey.AgvId -eq $fineAgvId -and
        [string]$journey.ChargingPolicyVersion -eq $version -and [string]$journey.PublishedBatteryState -eq 'SUFFICIENT'),
    "$fineVehicleKey / $fineAgvId / v$version / SUFFICIENT",
    $(if ($null -ne $journey) { "$($journey.VehicleKey) / $($journey.AgvId) / v$($journey.ChargingPolicyVersion) / $($journey.PublishedBatteryState)" } else { 'not dispatched' }))

$lowNothing = '0 intents, 0 purpose claims, 0 station holds, 0 RIoT orders'
$footprint = Wait-L2ConditionOrLast -Description "$lowVehicleKey got an order, a purpose or a station (it must not)" `
    -Journal $journal -Criterion 'low-vehicle-footprint-after-window' -TimeoutSeconds 15 `
    -Probe { Get-FootprintOf $lowVehicleKey } `
    -Until { param($v) $v -ne $lowNothing }
$journal.Observe('low-vehicle-footprint', $footprint, @{ vehicleKey = $lowVehicleKey; battery = $lowBattery })
$assertions.Add(
    'L2-MCT-03',
    'A 在整段里没有建单、没有用途占有、没有站点独占：原地不动（批次9-06 之前）',
    ($footprint -eq $lowNothing),
    $lowNothing,
    $footprint)

$journal.Note('低于强制充电线（但满足余量）的车：派车链答 MANDATORY_CHARGE_REQUIRED；抬过线的车接走搬运，低的那辆哪儿也不去。')
