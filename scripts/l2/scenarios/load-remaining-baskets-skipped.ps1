#Requires -Version 7

<#
多篮装货减篮（REQ-0367、修订后的 REQ-0211 与 REQ-0352，CP-0021，control-server#830）：三篮装进两篮后「剩下的不装」，出发，到关卡只卸两篮。

一条三篮的需求（maxBoxCount 12，L2-PACKAGE 每篮 4 箱），装货命令压在合成车载端上。合成车载端先报一份当下的逐仓快照（前两个目标仓有货、
第三个空着，都锁闭复位），再发 RemainingBasketsSkipRequested；服务端授权（AUTHORIZED，loadedSlots 就是那两个仓），合成车载端按
COMPLETED_REMAINING_SKIPPED 答那条装货命令。之后这条需求按两篮走：装货提交为两篮、第三个仓释放、出发、关卡的卸货命令只开两个仓、
卸完需求 Succeeded、业务键记完成——不按取消终结、不抑制、不进人工搬运告警。

所有「库里变成了什么」的判据都先等它出现再断言（Wait-L2Condition／Wait-L2Change，README 第 14 条）。结算那一次提交里同时写了装货操作的
状态与仓位、归属行的仓位，所以等到操作 Committed 之后，在同一个读快照里读这三样。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Change.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ReadSnapshot.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection
$peer = $Context.Onboard

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-RB-$($Context.RunId)"

function Get-Runtime {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM JourneyRuntimes WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Intent([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Operation([string]$type) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT SlotOperationAttemptId, Status, TargetSlotsJson FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = '$type'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Count([string]$sql) {
    return [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].Total
}

function Get-PendingOperationKey {
    $pending = @($peer.Snapshot().body.pending | Where-Object { $_.messageType -eq 'SlotOperationCommand' })
    if ($pending.Count -eq 0) { return $null }
    return [string]$pending[0].key
}

function Format-Slots([object]$json) {
    if ($null -eq $json) { return '(none)' }
    return '[' + ((@($json | ConvertFrom-Json) | ForEach-Object { [int]$_ }) -join ',') + ']'
}

function Move-Vehicle([object]$intent, [int]$stationRiotId) {
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey      = $Context.VehicleKey
        procState       = 'RUNNING'
        movementState   = 'MT_RUNNING'
        speed           = 0.8
        processingOrder = $true
        orderTaskId     = $intent.OrderId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey       = $Context.VehicleKey
        procState        = 'IDLE'
        movementState    = 'MT_FINISHED'
        speed            = 0
        currentPosition  = $stationRiotId
        processingOrder  = $false
        clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($intent.UpperId)", @{ orderState = 5 })
}

# --- 1. 三篮的需求受理，车开到取货点，装货命令压在合成车载端上 --------------------------------------

$null = $peer.Command('Put', 'policy', @{ loadResult = 'Manual' })
$journal.Note("Publishing demand $demandIdWire (sublot $sublot, 12 boxes = 3 baskets).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 12
})
$null = Wait-L2Condition -Description 'the demand was accepted and dispatched to the pickup station' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 90 `
    -Probe { $runtime = Get-Runtime; if ($runtime) { [string]$runtime.Stage } else { $null } } `
    -Until { param($v) $v -eq 'AwaitingPickupArrival' }
$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 60 `
    -Probe { Get-Intent 'TO_PICKUP' } -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
Move-Vehicle $pickupIntent ([int]$Context.PickupStationRiotId)

$key = Wait-L2Condition -Description 'the load command reached the peer and is held there' `
    -Journal $journal -Criterion 'load-pending' -TimeoutSeconds 120 `
    -Probe { Get-PendingOperationKey } -Until { param($v) $null -ne $v }
$load = Wait-L2Condition -Description 'the journey recorded that it waits for the load result' `
    -Journal $journal -Criterion 'load-stage' -TimeoutSeconds 30 `
    -Probe { Invoke-L2ReadSnapshot -Connection $connection -Read {
            $runtime = Get-Runtime
            if ($null -eq $runtime -or [string]$runtime.Stage -ne 'AwaitingLoadResult') { return $null }
            Get-Operation 'Load'
        } } `
    -Until { param($v) $null -ne $v }
$targets = @($load.TargetSlotsJson | ConvertFrom-Json | ForEach-Object { [int]$_ })
$assertions.Add(
    'L2-RB-01', '三篮的需求分到三个目标仓，装货命令压在车上（Prepared）',
    ($targets.Count -eq 3 -and [string]$load.Status -eq 'Prepared'),
    '3 个目标仓 / Prepared', "$(Format-Slots $load.TargetSlotsJson) / $($load.Status)")

# --- 2. 装进两篮，申请剩下的不装 -------------------------------------------------------------------

$loaded = @($targets[0], $targets[1])
$remaining = $targets[2]
$settled = Wait-L2Change -Description 'the server authorized the skip and settled the load as two baskets' `
    -Journal $journal -Criterion 'skip-settled' -TimeoutSeconds 60 `
    -Baseline { [string](Get-Operation 'Load').Status } `
    -Action {
        $journal.Note("Peer reports slots $($loaded -join ',') loaded and asks to skip slot $remaining ($key).")
        $null = $peer.Command('Put', "remaining-baskets-skips/$key", @{ loadedSlots = $loaded })
    } `
    -Probe { Invoke-L2ReadSnapshot -Connection $connection -Read {
            $operation = Get-Operation 'Load'
            if ($null -eq $operation -or [string]$operation.Status -ne 'Committed') { return $null }
            $membership = (Invoke-L2Query -Connection $connection `
                -Sql "SELECT TargetSlotsJson, LoadedSlotsJson FROM JourneyDemands WHERE DemandId = '$demandId' AND RemovedAt IS NULL")[0]
            $answer = @(Invoke-L2Query -Connection $connection `
                -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'RemainingBasketsSkipResult'")
            [pscustomobject]@{ Operation = $operation; Membership = $membership; Answers = $answer }
        } } `
    -Until { param($before, $now) $before -eq 'Prepared' -and $null -ne $now }
$after = $settled.Value

$answers = @($after.Answers | ForEach-Object { ($_.PayloadJson | ConvertFrom-Json).payload })
$skip = @((Invoke-RestMethod -Uri "$($peer.BaseUrl)/$($peer.Prefix)/remaining-baskets-skips" -TimeoutSec 10).body.skips)
$assertions.Add(
    'L2-RB-02', '服务端授权减篮：答复 AUTHORIZED，loadedSlots 就是装进的两个仓，没有 problem（AUTHORIZE_SKIP_ONLY_WITH_AT_LEAST_ONE_LOADED_AND_ONE_REMAINING_SLOT）',
    ($answers.Count -eq 1 -and [string]$answers[0].outcome -eq 'AUTHORIZED' -and
        ((@($answers[0].loadedSlots) -join ',') -eq ($loaded -join ',')) -and $null -eq $answers[0].problem -and
        [string]$answers[0].demandId -eq $demandId),
    "AUTHORIZED [$($loaded -join ',')]",
    $(if ($answers.Count -gt 0) { "$($answers[0].outcome) [$(@($answers[0].loadedSlots) -join ',')] x$($answers.Count)" } else { '(no answer)' }))
$assertions.Add(
    'L2-RB-03', '车按 COMPLETED_REMAINING_SKIPPED 报完，装货按两篮提交：操作 Committed、操作与归属行都只剩那两个仓（COMMIT_AUTHORIZED_LOADED_SLOTS_AS_ONE_LOAD_BATCH、RELEASE_REMAINING_SLOTS_AND_RESERVATION_ON_SETTLEMENT）',
    ([string]$after.Operation.Status -eq 'Committed' -and
        (Format-Slots $after.Operation.TargetSlotsJson) -eq "[$($loaded -join ',')]" -and
        (Format-Slots $after.Membership.TargetSlotsJson) -eq "[$($loaded -join ',')]" -and
        $skip.Count -eq 1 -and [bool]$skip[0].resultReported),
    "Committed / [$($loaded -join ',')] / [$($loaded -join ',')] / 车已报结果",
    "$($after.Operation.Status) / $(Format-Slots $after.Operation.TargetSlotsJson) / $(Format-Slots $after.Membership.TargetSlotsJson) / $(if ($skip.Count -eq 1) { "resultReported=$($skip[0].resultReported)" } else { "$($skip.Count) skips" })")

# --- 3. 出发，到关卡只卸两篮 -------------------------------------------------------------------------

$gateIntent = Wait-L2Condition -Description 'the departure check passed and the TO_GATE intent was confirmed' `
    -Journal $journal -Criterion 'to-gate-intent' -TimeoutSeconds 120 `
    -Probe { Get-Intent 'TO_GATE' } -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
Move-Vehicle $gateIntent ([int]$Context.GateStationRiotId)

$completed = Wait-L2Condition -Description 'the journey completed at the gate' `
    -Journal $journal -Criterion 'journey-completed' -TimeoutSeconds 120 `
    -Probe { Invoke-L2ReadSnapshot -Connection $connection -Read {
            $runtime = Get-Runtime
            if ($null -eq $runtime -or [string]$runtime.Stage -ne 'Completed') { return $null }
            [pscustomobject]@{
                Unload      = Get-Operation 'Unload'
                Command     = @(Invoke-L2Query -Connection $connection `
                    -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'SlotOperationCommand'") |
                    ForEach-Object { ($_.PayloadJson | ConvertFrom-Json).payload } |
                    Where-Object { [string]$_.operationType -eq 'UNLOAD' -and [string]$_.demandId -eq $demandId }
                Demand      = [string](Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'")[0].Status
                Completions = Get-Count "SELECT COUNT(*) AS Total FROM TransportDemandCompletions WHERE DemandId = '$demandId'"
                Suppressed  = Get-Count "SELECT COUNT(*) AS Total FROM TransportDemandSuppressions WHERE DemandId = '$demandId'"
                Alarms      = Get-Count "SELECT COUNT(*) AS Total FROM StructuralDispatchBlocks WHERE DemandId = '$demandId'"
            }
        } } `
    -Until { param($v) $null -ne $v }

$unloadCommand = @($completed.Command)
$assertions.Add(
    'L2-RB-04', '卸货命令只开那两个仓，expectedBasketCount 2（UNLOAD_ONLY_THE_AUTHORIZED_LOADED_SLOTS）',
    ($unloadCommand.Count -eq 1 -and (@($unloadCommand[0].slots) -join ',') -eq ($loaded -join ',') -and
        [int]$unloadCommand[0].expectedBasketCount -eq 2 -and
        (Format-Slots $completed.Unload.TargetSlotsJson) -eq "[$($loaded -join ',')]"),
    "UNLOAD [$($loaded -join ',')] x2",
    $(if ($unloadCommand.Count -eq 1) { "UNLOAD [$(@($unloadCommand[0].slots) -join ',')] x$($unloadCommand[0].expectedBasketCount)" } else { "$($unloadCommand.Count) unload commands" }))
$assertions.Add(
    'L2-RB-05', '卸完按正常送达完成：需求 Succeeded、业务键记完成，没有抑制、没有人工搬运告警（COMPLETE_DEMAND_AS_NORMAL_DELIVERY_WITHOUT_SUPPRESSION_OR_ALARM）',
    ($completed.Demand -eq 'Succeeded' -and $completed.Completions -eq 1 -and $completed.Suppressed -eq 0 -and $completed.Alarms -eq 0),
    'Succeeded / 1 completion / 0 suppression / 0 alarm',
    "$($completed.Demand) / $($completed.Completions) completion / $($completed.Suppressed) suppression / $($completed.Alarms) alarm")

$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add('L2-RB-06', '全程只建了两条 RIoT 单（取货一条、关卡一条）', ($riotOrders.Count -eq 2), 2, $riotOrders.Count)

$journal.Note('Scenario finished.')
