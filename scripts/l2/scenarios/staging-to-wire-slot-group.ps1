#Requires -Version 7

<#
按目的机台装侧（control-server#163，规格 8.3 批次 6 判据⑥，REQ-0352）。

边车把 N1-3 指 REAR、N1-7 指 FRONT，两个 AREA 挂在同一个机台站。各放一条要 3 个花篮的 STAGING_TO_WIRE 需求，先后走完，
每条断言：
  - 目标仓全部落在需求 AREA 指派的那一组、升序，且是该组编号最小的 3 个可用仓；
  - 派工待送站下发的 LOAD 命令与机台站下发的 UNLOAD 命令，slots 都等于目标仓。

分组从 SlotModelSlots 经该车的记录读（L2SlotGroups.psm1），脚本里不写 1～4／5～8。
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

$stagingStationRiotId = 305
$machineStationRiotId = 12

# L2-PACKAGE 每篮 4 盒（编排器导入的包装容量），12 盒就是 3 个花篮。
$basketCount = 3
$maxBoxCount = 12

function Publish-Demand([string]$Area) {
    $guid = [guid]::NewGuid()
    $sublot = "L2-S2WSG-$Area-$($Context.RunId)"
    $journal.Note("Publishing STAGING_TO_WIRE demand $($guid.ToString('N')) (sublot $sublot, area $Area, $maxBoxCount boxes).")
    $null = $mes.Command('Put', "demands/$($guid.ToString('N'))", @{
        sublot      = $sublot
        area        = $Area
        eqp         = "EQP-L2-S2WSG-$Area"
        package     = 'L2-PACKAGE'
        maxBoxCount = $maxBoxCount
        workType    = 'STAGING_TO_WIRE'
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

# 一条需求：受理、按 AREA 的组断言目标仓、走完、断言装卸命令的 slots。
function Test-Demand([string]$Area, [string]$SlotPosition, [string]$IdPrefix) {
    $demandId = Publish-Demand $Area
    $null = Wait-L2Condition -Description "the $Area demand was accepted and dispatched to the staging station" `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
        -Probe { $r = Get-Runtime $demandId; if ($r) { [string]$r.Stage } else { $null } } `
        -Until { param($v) $v -eq 'AwaitingPickupArrival' }
    $runtime = Get-Runtime $demandId
    $targets = @([string]$runtime.TargetSlotsJson | ConvertFrom-Json | ForEach-Object { [int]$_ })
    $assertions.Add(
        "$IdPrefix-01", "$Area 需求在派工待送站取货、到机台站卸货，恰好占 $basketCount 个目标仓",
        ([int]$runtime.PickupStationRiotId -eq $stagingStationRiotId -and [int]$runtime.GateStationRiotId -eq $machineStationRiotId -and
            [int]$runtime.ExpectedBasketCount -eq $basketCount -and $targets.Count -eq $basketCount),
        "pickup $stagingStationRiotId, drop-off $machineStationRiotId, $basketCount target slots",
        "pickup $($runtime.PickupStationRiotId), drop-off $($runtime.GateStationRiotId), ExpectedBasketCount $($runtime.ExpectedBasketCount), [$($targets -join ',')]")
    $null = Assert-L2SlotGroupTargets -Assertions $assertions -Id "$IdPrefix-02" -Connection $connection `
        -DemandId $demandId -SlotPosition $SlotPosition `
        -Description "$Area 指 ${SlotPosition}：目标仓全部属于本车 $SlotPosition 组、升序，且恰好是该组编号最小的 $basketCount 个可用仓"

    $pickupIntent = Wait-L2Condition -Description "the $Area TO_PICKUP intent was confirmed" `
        -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
        -Probe { Get-Intent $demandId 'TO_PICKUP' } -Until { param($v) [string]$v.Status -eq 'CONFIRMED' }
    Move-Vehicle $pickupIntent $stagingStationRiotId
    $null = Wait-L2Condition -Description "the $Area load committed and the journey reached the machine leg" `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { [string](Get-Runtime $demandId).Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
    $machineIntent = Wait-L2Condition -Description "the $Area machine-leg intent was confirmed" `
        -Journal $journal -Criterion 'to-gate-intent' -TimeoutSeconds 60 `
        -Probe { Get-Intent $demandId 'TO_GATE' } -Until { param($v) [string]$v.Status -eq 'CONFIRMED' }
    Move-Vehicle $machineIntent $machineStationRiotId
    $stage = Wait-L2Condition -Description "the $Area journey completed at the machine" `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { [string](Get-Runtime $demandId).Stage } -Until { param($v) $v -eq 'Completed' }
    $assertions.Add("$IdPrefix-03", "$Area 需求的旅程走到 Completed", ($stage -eq 'Completed'), 'Completed', $stage)

    foreach ($check in @(
            @{ Id = "$IdPrefix-04"; Type = 'LOAD'; Text = '派工待送站' }
            @{ Id = "$IdPrefix-05"; Type = 'UNLOAD'; Text = '机台站' })) {
        $commands = Get-CommandSlots $demandId $check.Type
        $sent = @($commands | ForEach-Object { (@($_.slots | ForEach-Object { [int]$_ }) -join ',') } | Sort-Object -Unique)
        $assertions.Add(
            $check.Id, "$Area：$($check.Text)下发给车的 $($check.Type) 命令 slots 与旅程目标仓相同",
            ($commands.Count -ge 1 -and $sent.Count -eq 1 -and $sent[0] -eq ($targets -join ',')),
            "[$($targets -join ',')]",
            "$($commands.Count) command(s), slots: $(if ($sent.Count -eq 0) { '(none)' } else { ($sent | ForEach-Object { "[$_]" }) -join ' ' })")
    }
}

# --- 0. 车型：分组只有 FRONT／REAR，且都来自库 --------------------------------------------------------

$positions = Get-L2VehicleSlotPositions -Connection $connection -AgvId $Context.AgvId
if ($null -eq $positions) { throw "Vehicle $($Context.AgvId) has no resolvable slot model; the preseed did not bind it." }
$journal.Note("Vehicle $($Context.AgvId) slot model $($positions.SlotModelVersionId) via $($positions.Source): " +
    (($positions.Positions.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))

# --- 1. N1-3 指 REAR，2. N1-7 指 FRONT ------------------------------------------------------------------

Test-Demand 'N1-3' 'REAR' 'L2-S2WSG-REAR'
Test-Demand 'N1-7' 'FRONT' 'L2-S2WSG-FRONT'

$journal.Note('Scenario finished.')
