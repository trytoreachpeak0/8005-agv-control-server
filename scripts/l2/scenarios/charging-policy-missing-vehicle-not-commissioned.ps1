#Requires -Version 7

<#
没有已批准策略版本的车不投运（批次9-02，control-server#400；REQ-0282，规格 8.6 逐车硬阻断的负向证据）。

编排器的默认前置经 FieldOps 导入、批准（L2_PRESET）、激活一版测试策略，本场景用 setup 键把适用范围收窄到第二台车 A
（BROKERX-L2-0002）。主车 B（BROKERX-L2-0001）没有策略。B 是轮次里第一个被问的车，两车都空闲、停在同一个站：
没有本票的判据，需求就派给 B。

  1. 发一条需求 → 派给 A（旅程、取货单都是 A 的）。
  2. 第二个事实另等（scripts/l2/README.md 第 14 条）：B 被新原因码挡下——服务端日志里有「B takes no new work:
     CHARGING_POLICY_NOT_APPROVED」那一行——而且在整个窗口里 B 一张单都没有（库里 OrderIntents、合成 RIoT 的订单表）。
     这件事与受理不是同一次提交，所以不在读到 A 的旅程那一刻顺手断言。
  3. 服务端没有整机拒绝：它一直在跑、A 照常出车。

红证据（缺陷版本）：去掉派车链里的 ChargingPolicyCommissioningCriterion，需求派给 B，L2-CPM-01 与 L2-CPM-02 变红。
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

$coveredAgvId = 'AGV-L2-002'
$coveredVehicleKey = 'BROKERX-L2-0002'
$uncoveredVehicleKey = $Context.VehicleKey
$demandGuid = [guid]::NewGuid()
$demandId = $demandGuid.ToString('D')
$serverLog = Join-Path $Context.LogRoot 'control-server.out.log'
$blockedLine = "Vehicle $uncoveredVehicleKey takes no new work: CHARGING_POLICY_NOT_APPROVED"

function Read-SharedText([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { return [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
}

function Get-OrdersOf([string]$vehicleKey) {
    # Counted in SQL: a query with no rows comes back as $null, and @($null).Count is 1 (the first local run read "1 intent"
    # for a vehicle that had none -- the snapshot holds only A's order).
    $intents = [int](Invoke-L2Query -Connection $connection -Sql "SELECT COUNT(*) AS N FROM OrderIntents WHERE VehicleKey = '$vehicleKey'")[0].N
    $riotOrders = [int](@($riot.Snapshot().body.orders | Where-Object { $null -ne $_ -and [string]$_.appointVehicleKey -eq $vehicleKey }) |
        Measure-Object).Count
    return [pscustomobject]@{ Intents = $intents; RiotOrders = $riotOrders }
}

# --- 0. 前置：策略只覆盖 A ------------------------------------------------------------------------------

$policyRows = @(Invoke-L2Query -Connection $connection -Sql (
    'SELECT v.Version, s.VehicleKey, a.Source FROM ChargingPolicyVersions v ' +
    'JOIN ChargingPolicyActivations x ON x.Version = v.Version ' +
    'JOIN ChargingPolicyApprovals a ON a.Version = v.Version ' +
    'LEFT JOIN ChargingPolicyVehicleScopes s ON s.Version = v.Version'))
$assertions.Add(
    'L2-CPM-00',
    '前置：唯一一版已激活的策略经 FieldOps 导入、以 L2_PRESET 批准，适用范围只有 A',
    ($policyRows.Count -eq 1 -and [string]$policyRows[0].VehicleKey -eq $coveredVehicleKey -and [string]$policyRows[0].Source -eq 'L2_PRESET'),
    "1 行 / $coveredVehicleKey / L2_PRESET",
    (($policyRows | ForEach-Object { "$($_.Version)/$($_.VehicleKey)/$($_.Source)" }) -join ', '))

# 两车都空闲、停在关卡：谁先被问由名册次序定，B 在前。
foreach ($key in @($uncoveredVehicleKey, $coveredVehicleKey)) {
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $key; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $Context.GateStationRiotId
    })
}

# --- 1. 发一条需求：派给 A --------------------------------------------------------------------------------

$journal.Note("Publishing demand $($demandGuid.ToString('N')) (area N1-3).")
$null = $mes.Command('Put', "demands/$($demandGuid.ToString('N'))", @{
    sublot = "L2-CPM-$($Context.RunId)"; area = 'N1-3'
    eqp = 'EQP-L2-01'; package = 'L2-PACKAGE'; maxBoxCount = 4
})

$journey = Wait-L2Condition -Description 'a vehicle took the demand and set off to its pickup' `
    -Journal $journal -Criterion 'journey-accepted' -TimeoutSeconds 120 `
    -Probe {
        $rows = @(Invoke-L2Query -Connection $connection -Sql (
            "SELECT JourneyId, AgvId, VehicleKey, Stage, PickupUpperId FROM JourneyRuntimes WHERE DemandId = '$demandId'"))
        if ($rows.Count -eq 0) { return $null }
        return $rows[0]
    } `
    -Until { param($v) $null -ne $v }

$assertions.Add(
    'L2-CPM-01',
    '需求派给策略范围内的 A，不是轮次里排在前面的 B',
    ([string]$journey.VehicleKey -eq $coveredVehicleKey -and [string]$journey.AgvId -eq $coveredAgvId),
    "$coveredVehicleKey / $coveredAgvId",
    "$($journey.VehicleKey) / $($journey.AgvId)")

# --- 2. 第二个事实另等：B 被新原因码挡下，整个窗口里 B 没有单 --------------------------------------------

$blocked = Wait-L2ConditionOrLast -Description "the server logged that $uncoveredVehicleKey takes no new work (CHARGING_POLICY_NOT_APPROVED)" `
    -Journal $journal -Criterion 'uncovered-vehicle-refused' -TimeoutSeconds 60 `
    -Probe { (Read-SharedText $serverLog).Contains($blockedLine, [StringComparison]::Ordinal) } `
    -Until { param($v) $v -eq $true }
$assertions.Add(
    'L2-CPM-02',
    'B 被派车链以 CHARGING_POLICY_NOT_APPROVED 挡下（服务端日志）',
    ($blocked -eq $true),
    $blockedLine,
    $(if ($blocked) { 'found' } else { 'not found' }))

# A 的取货单确认之后再看 B：整个窗口（从发需求到 A 出车）里 B 一张单都没有。
$pickup = Wait-L2ConditionOrLast -Description "A's pickup order is confirmed" `
    -Journal $journal -Criterion 'covered-vehicle-pickup-confirmed' -TimeoutSeconds 60 `
    -Probe {
        $rows = @(Invoke-L2Query -Connection $connection -Sql (
            "SELECT Status, VehicleKey FROM OrderIntents WHERE UpperId = '$([string]$journey.PickupUpperId)'"))
        if ($rows.Count -eq 0) { return $null }
        return $rows[0]
    } `
    -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
$ordersOfB = Get-OrdersOf $uncoveredVehicleKey
$assertions.Add(
    'L2-CPM-03',
    'A 的取货单已确认，而 B 在整个窗口里没有任何建单（库里的订单意图、合成 RIoT 的订单）',
    ($pickup -and [string]$pickup.Status -eq 'CONFIRMED' -and [string]$pickup.VehicleKey -eq $coveredVehicleKey -and
        $ordersOfB.Intents -eq 0 -and $ordersOfB.RiotOrders -eq 0),
    "CONFIRMED / $coveredVehicleKey / B: 0 intents, 0 RIoT orders",
    "$(if ($pickup) { "$($pickup.Status) / $($pickup.VehicleKey)" } else { 'no pickup order' }) / B: $($ordersOfB.Intents) intents, $($ordersOfB.RiotOrders) RIoT orders")

# --- 3. 没有整机拒绝：服务端一直在跑 -----------------------------------------------------------------------

$health = try { (Invoke-WebRequest -Uri "http://127.0.0.1:$($Context.HealthPort)/health/live" -TimeoutSec 5).StatusCode } catch { $_.Exception.Message }
$assertions.Add(
    'L2-CPM-04',
    '逐车判定、不整机拒绝启动：服务端进程在跑、存活检查 200',
    ($health -eq 200),
    '200',
    [string]$health)
