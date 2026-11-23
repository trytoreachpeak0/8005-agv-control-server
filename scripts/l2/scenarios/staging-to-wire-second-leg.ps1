#Requires -Version 7

<#
同一子批先后两程 STAGING_TO_WIRE（control-server#911）。

mes-ingest#15 给送焊线加了工序「焊线2」：同一个子批先送「焊线」机台，机台做完再送「焊线2」机台，两程业务键都是
<sublot>|STAGING_TO_WIRE。需求身份是「业务键 + 程」，程取 MES STEP，只对 STAGING_TO_WIRE 分程（用户 2026-11-23 定）。

  1. D1（STEP 焊线）受理、取货站 305 装货、机台站 12 卸货，走完——完成事实经车载端的卸货结果写下。
  2. MesIngest 撤下 D1，以新 DemandId D2 再发同一程（STEP 仍是焊线，generation 2，MES 重复下发的样子）。断言 D2 从未受理、
     积压原因 TRANSPORT_DEMAND_KEY_ALREADY_ACCEPTED——同一程照旧去重。
  3. MesIngest 撤下 D2，以新 DemandId D3 发焊线2 那程（generation 3）。断言 D3 受理、走完；受理行的程是焊线2；
     完成事实两条，同键、程分别是焊线与焊线2。

缺陷版本上（本票之前的 fp/v2-impl）红在第 3 步：D3 被第 12 道以 TRANSPORT_DEMAND_KEY_ALREADY_ACCEPTED 挡住，永远不受理。
第 3 步用 Wait-L2ConditionOrLast 等 D3，等不到时照实记红并在那里收尾，而不是让整趟运行在一个超时上抛掉。
取消后的焊线2（现场 Q26113563-4 那种）由 TransportDemandLegTests 判，这里不走：合成车载端自动答录入，站点期限等不到。
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

$stagingStationRiotId = 305
$machineStationRiotId = 12
$sublot = "L2-S2W2-$($Context.RunId)"
$key = "$sublot|STAGING_TO_WIRE"

function New-Demand([string]$Label) {
    $guid = [guid]::NewGuid()
    return [pscustomobject]@{ Wire = $guid.ToString('N'); Id = $guid.ToString('D'); Label = $Label }
}

function Publish-Demand($Demand, [string]$Step, [int]$Generation) {
    $journal.Note("Publishing $($Demand.Label) $($Demand.Wire): sublot $sublot, STEP $Step, generation $Generation.")
    $null = $mes.Command('Put', "demands/$($Demand.Wire)", @{
        sublot      = $sublot
        area        = 'N1-3'
        eqp         = 'EQP-L2-S2W2-01'
        package     = 'L2-PACKAGE'
        maxBoxCount = 4
        workType    = 'STAGING_TO_WIRE'
        step        = $Step
        generation  = $Generation
    })
}

function Remove-Demand($Demand) {
    $journal.Note("MesIngest withdraws $($Demand.Label) $($Demand.Wire).")
    $null = $mes.Command('Delete', "demands/$($Demand.Wire)", @{})
}

function Get-Runtime([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$DemandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Backlog([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT ReasonCode, AcceptedAt FROM JourneyBacklog WHERE DemandId = '$DemandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Accepted([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT DemandId, TransportDemandKey, TransportLeg, Status FROM AcceptedDemands WHERE DemandId = '$DemandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$DemandId, [string]$Purpose) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$DemandId' AND Purpose = '$Purpose'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 不要写成 @(Invoke-L2Query ...)：它以 `return , $rows` 交回整张表，再包一层 @() 就成了「一个元素是整张表」。
function Get-Completions {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT TransportDemandKey, TransportLeg, DemandId FROM TransportDemandCompletions " +
        "WHERE TransportDemandKey = '$key' ORDER BY TransportLeg")
    return , $rows
}

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

# 一程从受理走到 Completed：取货站 305 装、机台站 12 卸。
function Complete-Leg([string]$DemandId, [string]$Label) {
    $pickupIntent = Wait-L2Condition -Description "the $Label TO_PICKUP intent was confirmed" `
        -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
        -Probe { Get-Intent $DemandId 'TO_PICKUP' } -Until { param($v) [string]$v.Status -eq 'CONFIRMED' }
    $journal.Note("$Label vehicle drives to the staging station $stagingStationRiotId.")
    Move-Vehicle $pickupIntent $stagingStationRiotId
    $null = Wait-L2Condition -Description "the $Label load committed and the journey reached its second leg" `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { [string](Get-Runtime $DemandId).Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
    $dropIntent = Wait-L2Condition -Description "the $Label drop-off intent was confirmed" `
        -Journal $journal -Criterion 'to-gate-intent' -TimeoutSeconds 60 `
        -Probe { Get-Intent $DemandId 'TO_GATE' } -Until { param($v) [string]$v.Status -eq 'CONFIRMED' }
    $journal.Note("$Label vehicle drives to the machine station $machineStationRiotId.")
    Move-Vehicle $dropIntent $machineStationRiotId
    return Wait-L2Condition -Description "the $Label journey completed" `
        -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
        -Probe { [string](Get-Runtime $DemandId).Stage } -Until { param($v) $v -eq 'Completed' }
}

$d1 = New-Demand 'D1'
$d2 = New-Demand 'D2'
$d3 = New-Demand 'D3'

# --- 1. 焊线那程走完 -------------------------------------------------------------------------------------

Publish-Demand $d1 '焊线' 1
$null = Wait-L2Condition -Description 'the first leg (焊线) was accepted and dispatched' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { $r = Get-Runtime $d1.Id; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$stage = Complete-Leg $d1.Id 'first leg'
$first = Get-Accepted $d1.Id
$assertions.Add(
    'L2-S2W2-01', '焊线那程受理并走完，受理行的程是焊线',
    ($stage -eq 'Completed' -and [string]$first.TransportLeg -ceq '焊线' -and [string]$first.Status -eq 'Succeeded'),
    'Completed, leg 焊线, Succeeded',
    "$stage, leg '$($first.TransportLeg)', $($first.Status)")

# --- 2. 同一程以新 DemandId 再发：照旧去重 ----------------------------------------------------------------

Remove-Demand $d1
Publish-Demand $d2 '焊线' 2
$d2Backlog = Wait-L2ConditionOrLast -Description 'the re-issued first leg was refused as already accepted' `
    -Journal $journal -Criterion 'backlog-reason' -TimeoutSeconds 60 `
    -Probe { Get-Backlog $d2.Id } -Until { param($v) $null -ne $v -and [string]$v.ReasonCode -eq 'TRANSPORT_DEMAND_KEY_ALREADY_ACCEPTED' }
$assertions.Add(
    'L2-S2W2-02', '同一程（STEP 焊线）以新 DemandId 再发：不受理，积压原因 TRANSPORT_DEMAND_KEY_ALREADY_ACCEPTED',
    ($null -ne $d2Backlog -and [string]$d2Backlog.ReasonCode -eq 'TRANSPORT_DEMAND_KEY_ALREADY_ACCEPTED' -and
        $null -eq (Get-Accepted $d2.Id) -and $null -eq (Get-Runtime $d2.Id)),
    'TRANSPORT_DEMAND_KEY_ALREADY_ACCEPTED, never accepted',
    "backlog $(if ($d2Backlog) { $d2Backlog.ReasonCode } else { '(none)' }), accepted $($null -ne (Get-Accepted $d2.Id))")

# --- 3. 焊线2 那程：受理、走完、两条完成事实 -------------------------------------------------------------

Remove-Demand $d2
Publish-Demand $d3 '焊线2' 3
$d3Stage = Wait-L2ConditionOrLast -Description 'the second leg (焊线2) was accepted and dispatched' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { $r = Get-Runtime $d3.Id; if ($r) { [string]$r.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }
if ($d3Stage -ne 'AwaitingPickupArrival') {
    $backlog = Get-Backlog $d3.Id
    $assertions.Add(
        'L2-S2W2-03', '焊线2 那程受理并走完，受理行的程是焊线2',
        $false, 'accepted and Completed, leg 焊线2',
        "never accepted; backlog $(if ($backlog) { $backlog.ReasonCode } else { '(none)' })")
    $journal.Note('Scenario finished early: the second leg was never accepted.')
    return
}
$stage = Complete-Leg $d3.Id 'second leg'
$second = Get-Accepted $d3.Id
$assertions.Add(
    'L2-S2W2-03', '焊线2 那程受理并走完，受理行的程是焊线2',
    ($stage -eq 'Completed' -and [string]$second.TransportLeg -ceq '焊线2' -and [string]$second.Status -eq 'Succeeded' -and
        [string]$second.TransportDemandKey -ceq $key),
    "Completed, key $key, leg 焊线2, Succeeded",
    "$stage, key $($second.TransportDemandKey), leg '$($second.TransportLeg)', $($second.Status)")

$completions = Get-Completions
$observed = ($completions | ForEach-Object { "$($_.TransportLeg)=$($_.DemandId)" }) -join ' '
$assertions.Add(
    'L2-S2W2-04', '同一业务键下两条完成事实，程分别是焊线与焊线2，各是自己那条需求的',
    ($completions.Count -eq 2 -and $observed -ceq "焊线=$($d1.Id) 焊线2=$($d3.Id)"),
    "焊线=$($d1.Id) 焊线2=$($d3.Id)",
    "$($completions.Count) row(s): $observed")

$journal.Note('Scenario finished.')
