#Requires -Version 7

<#
按需求区域号的仓位分组选仓（control-server#73，规格 8.3 批次 4 场景①）。

边车把 N1-3 指 REAR。放一条要 3 个花篮的 N1-3 需求，断言：
  - 库内整车模型的分组只有 FRONT／REAR；
  - 旅程的目标仓是这台车 REAR 组内编号最小的 3 个可用仓（按库内模型判，默认模型下即 [5,6,7]）；
  - 下发给车的装货命令与卸货命令的 slots 都与目标仓相同——装卸读同一份，关卡卸货不另开一组。
走完这一趟后导入一版把 N1-3 改指 FRONT 的归属表，放第二条同样的需求，断言取的是 FRONT 组最小的 3 个。

分组从 SlotModelSlots 经该车的记录读（L2SlotGroups.psm1），脚本里不写 1～4／5～8：那是今天这一版已批准模型的性质，
不是车的性质。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SlotGroups.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection

# L2-PACKAGE 每篮 4 盒（编排器导入的包装容量），12 盒就是 3 个花篮。
$basketCount = 3
$maxBoxCount = 12

function Publish-Demand([string]$Suffix) {
    $guid = [guid]::NewGuid()
    $sublot = "L2-SGS-$Suffix-$($Context.RunId)"
    $journal.Note("Publishing demand $($guid.ToString('N')) (sublot $sublot, area N1-3, $maxBoxCount boxes).")
    $null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot      = $sublot
        area        = 'N1-3'
        # One machine for both demands: an AREA naming two EQPs in the catalog is AREA_EQP_NOT_UNIQUE, and the first
        # demand stays listed after its journey completes.
        eqp         = 'EQP-L2-SGS-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = $maxBoxCount
    })
    return $guid.ToString('D')
}

function Get-Runtime([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$DemandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$DemandId, [string]$Purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$DemandId' AND Purpose = '$Purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-CommandSlots([string]$DemandId, [string]$OperationType) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'SlotOperationCommand' ORDER BY CreatedAt, MessageId"
    $commands = @($rows | ForEach-Object { ([string]$_.PayloadJson | ConvertFrom-Json).payload } |
        Where-Object { [string]$_.demandId -eq $DemandId -and [string]$_.operationType -ceq $OperationType })
    return , $commands
}

# 车从当前位置开到 $StationRiotId 并停稳，照 normal-load 的写法。
function Move-Vehicle([object]$Intent, [int]$StationRiotId) {
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'; speed = 0.8
        processingOrder = $true; orderTaskId = $Intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $StationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($Intent.UpperId)", @{ orderState = 5 })
}

# --- 0. 车型：分组只有 FRONT／REAR，且都来自库 --------------------------------------------------------

$positions = Get-L2VehicleSlotPositions -Connection $connection -AgvId $Context.AgvId
$groups = if ($null -eq $positions) { @() } else { @($positions.Positions.Values | Sort-Object -Unique -CaseSensitive) }
# Assigned before it is piped: Invoke-L2Query's result set would otherwise arrive as one element (README).
$modelGroupRows = Invoke-L2Query -Connection $connection -Sql 'SELECT DISTINCT SlotPosition FROM SlotModelSlots'
$modelGroups = @($modelGroupRows | ForEach-Object { [string]$_.SlotPosition } | Sort-Object -CaseSensitive)
$assertions.Add(
    'L2-SGS-01', '库内整车模型的分组只有 FRONT 与 REAR，本车的仓位模型可解析',
    ($null -ne $positions -and ($modelGroups -join ',') -ceq 'FRONT,REAR' -and ($groups -join ',') -ceq 'FRONT,REAR'),
    'SlotModelSlots: FRONT,REAR; vehicle groups: FRONT,REAR',
    "SlotModelSlots: $($modelGroups -join ','); vehicle groups: $(if ($null -eq $positions) { '(unresolved)' } else { $groups -join ',' })")
if ($null -eq $positions) { throw "Vehicle $($Context.AgvId) has no resolvable slot model; the preseed did not bind it." }
$journal.Note("Vehicle $($Context.AgvId) slot model $($positions.SlotModelVersionId) via $($positions.Source): " +
    (($positions.Positions.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))

# --- 1. N1-3 指 REAR：整条需求装进 REAR 组最小的 3 个仓 ----------------------------------------------

$rearDemand = Publish-Demand 'REAR'
$null = Wait-L2Condition -Description 'the REAR demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { $r = Get-Runtime $rearDemand; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }

$rearRuntime = Get-Runtime $rearDemand
$rearTargets = @([string]$rearRuntime.TargetSlotsJson | ConvertFrom-Json | ForEach-Object { [int]$_ })
$assertions.Add(
    'L2-SGS-02', "需求要 $basketCount 个花篮，旅程恰好占 $basketCount 个目标仓",
    ([int]$rearRuntime.ExpectedBasketCount -eq $basketCount -and $rearTargets.Count -eq $basketCount),
    "ExpectedBasketCount $basketCount, $basketCount target slots",
    "ExpectedBasketCount $($rearRuntime.ExpectedBasketCount), $($rearTargets.Count) target slots [$($rearTargets -join ',')]")
# -Description given explicitly: the module's default is not applied when the parameter is left out.
$null = Assert-L2SlotGroupTargets -Assertions $assertions -Id 'L2-SGS-03' -Connection $connection `
    -DemandId $rearDemand -SlotPosition 'REAR' `
    -Description "N1-3 指 REAR：目标仓全部属于本车 REAR 组、升序，且恰好是该组编号最小的 $basketCount 个可用仓"

# --- 2. 走完这一趟：装货、卸货命令的 slots 与目标仓相同 -----------------------------------------------

$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { Get-Intent $rearDemand 'TO_PICKUP' } -Until { param($v) [string]$v.Status -eq 'CONFIRMED' }
$journal.Note('Vehicle drives to the pickup station and comes to rest.')
Move-Vehicle $pickupIntent $Context.PickupStationRiotId

$null = Wait-L2Condition -Description 'the load committed and the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { [string](Get-Runtime $rearDemand).Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }

$gateIntent = Wait-L2Condition -Description 'the TO_GATE intent was confirmed' `
    -Journal $journal -Criterion 'to-gate-intent' -TimeoutSeconds 60 `
    -Probe { Get-Intent $rearDemand 'TO_GATE' } -Until { param($v) [string]$v.Status -eq 'CONFIRMED' }
$journal.Note('Vehicle drives to the gate and comes to rest.')
Move-Vehicle $gateIntent $Context.GateStationRiotId

$stage = Wait-L2Condition -Description 'the REAR journey completed at the gate' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { [string](Get-Runtime $rearDemand).Stage } -Until { param($v) $v -eq 'Completed' }
$assertions.Add('L2-SGS-04', '第一条需求的旅程走到 Completed', ($stage -eq 'Completed'), 'Completed', $stage)

foreach ($check in @(
        @{ Id = 'L2-SGS-05'; Type = 'LOAD'; Text = '取货点' }
        @{ Id = 'L2-SGS-06'; Type = 'UNLOAD'; Text = '关卡' })) {
    $commands = Get-CommandSlots $rearDemand $check.Type
    $sent = @($commands | ForEach-Object { (@($_.slots | ForEach-Object { [int]$_ }) -join ',') } | Sort-Object -Unique)
    $assertions.Add(
        $check.Id, "$($check.Text)下发给车的 $($check.Type) 命令 slots 与旅程目标仓相同",
        ($commands.Count -ge 1 -and $sent.Count -eq 1 -and $sent[0] -eq ($rearTargets -join ',')),
        "[$($rearTargets -join ',')]",
        "$($commands.Count) command(s), slots: $(if ($sent.Count -eq 0) { '(none)' } else { ($sent | ForEach-Object { "[$_]" }) -join ' ' })")
}

# --- 3. 导入新版本把 N1-3 改指 FRONT，第二条需求取 FRONT 组最小的 3 个 ---------------------------------

$versionBefore = [long](Invoke-L2Query -Connection $connection `
        -Sql 'SELECT COALESCE(MAX(Version), 0) AS Version FROM DispatchZoneAreaAssignmentVersions')[0].Version
$csv = Join-Path $Context.SnapshotRoot 'area-assignments-n1-3-front.csv'
[IO.File]::WriteAllText($csv, "area,dispatch_zone,slot_position`nN1-3,$($Context.DispatchZone),FRONT`n", [Text.UTF8Encoding]::new($false))
$import = & $Context.InvokeFieldOps -Arguments @('import-area-assignments', '--input', $csv)
$journal.Observe('area-assignment-import', $import.outcome, @{ output = $import })
$versionAfter = [long](Invoke-L2Query -Connection $connection `
        -Sql 'SELECT COALESCE(MAX(Version), 0) AS Version FROM DispatchZoneAreaAssignmentVersions')[0].Version
$assertions.Add(
    'L2-SGS-07', '导入把 N1-3 改指 FRONT 的新版本归属表',
    ($import.outcome -eq 'OK' -and $versionAfter -eq $versionBefore + 1),
    "OK, version $($versionBefore + 1)", "$($import.outcome), version $versionAfter")

$frontDemand = Publish-Demand 'FRONT'
$null = Wait-L2Condition -Description 'the FRONT demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { $r = Get-Runtime $frontDemand; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$frontRuntime = Get-Runtime $frontDemand
$assertions.Add(
    'L2-SGS-08', "第二条需求同样要 $basketCount 个花篮",
    ([int]$frontRuntime.ExpectedBasketCount -eq $basketCount),
    $basketCount, [int]$frontRuntime.ExpectedBasketCount)
$null = Assert-L2SlotGroupTargets -Assertions $assertions -Id 'L2-SGS-09' -Connection $connection `
    -DemandId $frontDemand -SlotPosition 'FRONT' `
    -Description "N1-3 改指 FRONT 之后：目标仓全部属于本车 FRONT 组、升序，且恰好是该组编号最小的 $basketCount 个可用仓"

$journal.Note('Scenario finished.')
