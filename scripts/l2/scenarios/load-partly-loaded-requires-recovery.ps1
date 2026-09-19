#Requires -Version 7

<#
三仓装货装上两仓、第三仓到期限都没放：服务端不再按「没人交货」作废需求，旅程停下等恢复。

8005-agv-control-server#170。2026-09-19 09:23 agv01 的需求 `182cc979` 装 `[3,4,5]`，3、4 号仓装上了，
5 号仓放进又取出、空仓关门，车在本站期限过后报 `overallOutcome = FAILED`，每个仓位的读数都明确：
3、4 `OCCUPIED`，5 `EMPTY`，门全锁、开锁输出全复位。那时的服务端只看「读数明确」，把它当作
ADR-cross-0058 决策 5 的确定失败：需求 `Cancelled`，旅程带着别的货去了关卡。**3、4 号仓里的两篮
从此没有任何记录指向它们**，下一趟旅程被分到了 3 号仓。

决策 5 的前提是「没人把货交过来」。有仓位读到有货，这个前提就不成立。现在这份结果判
`RecoveryRequired`：旅程 Blocked / `LOAD_RESULT_REQUIRES_RECOVERY`，需求留着，等补偿清空把货取出。

**用合成对端，是因为这一条要钉的全在服务端的结算规则上**：一份怎样的结果进来、服务端怎么判。
结果的形状由对端的 `loadedSlotCount` 给出，和现场那一份逐字段一致。车载端怎么在真 IO 上产出这份
结果、补偿之后下一单能不能照常装，归 `real-onboard-partial-load-and-occupied-slot`。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$onboard = $Context.Onboard
$connection = $Context.Connection

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-SUBLOT-$($Context.RunId)"

function Get-Runtime {
    $rows = Get-L2Journey -Connection $connection -DemandId $demandId
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stage {
    $runtime = Get-Runtime
    if ($null -eq $runtime) { return $null }
    return [string]$runtime.Stage
}

function Get-UpperId([string]$purpose) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT UpperId, OrderId, Status FROM OrderIntents WHERE DemandId = '$demandId' AND Purpose = '$purpose'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-PendingOperationKey {
    $pending = @($onboard.Snapshot().body.pending |
        Where-Object { $_.messageType -eq 'SlotOperationCommand' })
    if ($pending.Count -eq 0) { return $null }
    return [string]$pending[0].key
}

# --- 1. 一条要装三篮的需求，走到装载指令下发 ----------------------------------------------------------

$null = $onboard.Command('Put', 'policy', @{ loadResult = 'Manual' })

# L2-PACKAGE 一篮装 4 盒，12 盒就是三篮，与现场那一单同样是三仓。
$journal.Note("Publishing demand $demandIdWire (sublot $sublot, three baskets).")
$null = $mes.Command('Put', "demands/$demandIdWire", @{
    sublot      = $sublot
    area        = 'N1-3'
    eqp         = 'EQP-L2-01'
    package     = 'L2-PACKAGE'
    maxBoxCount = 12
})

$pickupIntent = Wait-L2Condition -Description 'the TO_PICKUP intent was confirmed' `
    -Journal $journal -Criterion 'to-pickup-intent' -TimeoutSeconds 90 `
    -Probe { $row = Get-UpperId -purpose 'TO_PICKUP'; if ($row -and $row.Status -eq 'CONFIRMED') { $row } else { $null } } `
    -Until { param($v) $null -ne $v }

$journal.Note('Vehicle drives to the pickup station and comes to rest.')
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'
    speed = 0.8; processingOrder = $true; orderTaskId = $pickupIntent.OrderId
})
$null = $riot.Command('Put', 'vehicle', @{
    vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
    currentPosition = $Context.PickupStationRiotId; processingOrder = $false; clearOrderTaskId = $true
})
$null = $riot.Command('Put', "orders/$($pickupIntent.UpperId)", @{ orderState = 5 })

$operationKey = Wait-L2Condition -Description 'the load command reached the peer and is waiting for a result' `
    -Journal $journal -Criterion 'pending-operation' -TimeoutSeconds 120 `
    -Probe { Get-PendingOperationKey } -Until { param($v) $null -ne $v }

$null = Wait-L2Condition -Description 'the journey moved on to waiting for the load result' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 60 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingLoadResult' }

$loadRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT TargetSlotsJson FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'"
$targets = @(([string]$loadRows[0].TargetSlotsJson | ConvertFrom-Json))
$assertions.Add(
    'L2-PL-01', '装载指令针对三个仓位，与现场那一单同形',
    ($targets.Count -eq 3), 3, $targets.Count)

# --- 2. 前两仓装上，第三仓到期限都没放 ---------------------------------------------------------------

$journal.Note("The first two slots took their baskets, the third never did; the peer reports that ($operationKey).")
$null = $onboard.Command('Put', "answer/$operationKey", @{
    completed       = $false
    determinate     = $true
    loadedSlotCount = 2
})

$resultRows = Wait-L2Condition -Description 'the server recorded the partial load result' `
    -Journal $journal -Criterion 'operation-result' -TimeoutSeconds 60 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql (
            "SELECT r.EvidenceJson FROM OperationResults r JOIN StationOperations o " +
            "ON o.SlotOperationAttemptId = r.SlotOperationAttemptId WHERE o.DemandId = '$demandId'")
        if ($rows.Count -eq 0) { $null } else { [string]$rows[0].EvidenceJson }
    } `
    -Until { param($v) $null -ne $v }
$journal.Observe('operation-result-evidence', $resultRows, $null)

# --- 3. 服务端判 RecoveryRequired，旅程停下，需求不作废 ------------------------------------------------

$stage = Wait-L2Condition -Description 'the journey blocked on the partial load' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { Get-Stage } -Until { param($v) $v -in @('Blocked', 'Completed', 'AwaitingDepartureSafety') }
$runtime = Get-Runtime
$assertions.Add(
    'L2-PL-02', '旅程停下等恢复，而不是作废需求继续走',
    ($stage -eq 'Blocked' -and [string]$runtime.BlockReasonCode -eq 'LOAD_RESULT_REQUIRES_RECOVERY'),
    'Blocked / LOAD_RESULT_REQUIRES_RECOVERY', "$stage / $([string]$runtime.BlockReasonCode)")

$operationRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM StationOperations WHERE DemandId = '$demandId' AND OperationType = 'Load'"
$assertions.Add(
    'L2-PL-03', '装载操作判 RecoveryRequired，不是确定失败 Failed',
    ([string]$operationRows[0].Status -eq 'RecoveryRequired'), 'RecoveryRequired', [string]$operationRows[0].Status)

$demandRows = Invoke-L2Query -Connection $connection `
    -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
$suppressions = Invoke-L2Query -Connection $connection `
    -Sql "SELECT ReasonCode FROM TransportDemandSuppressions WHERE DemandId = '$demandId'"
$assertions.Add(
    'L2-PL-04', '需求留着（RecoveryRequired），没有被判 Cancelled，也没有被永久抑制——车上那两篮还有记录指向它',
    ([string]$demandRows[0].Status -eq 'RecoveryRequired' -and $suppressions.Count -eq 0),
    'RecoveryRequired / 抑制 0 条', "$([string]$demandRows[0].Status) / 抑制 $($suppressions.Count) 条")

# 否定判据要有界：让运行时再跑几轮，确认它没有自己把这一站结掉。
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$runtime = Get-Runtime
$riotOrders = @($riot.Snapshot().body.orders)
$assertions.Add(
    'L2-PL-05', '又跑了几轮之后旅程仍然停着，没有派车去关卡',
    ([string]$runtime.Stage -eq 'Blocked' -and $riotOrders.Count -eq 1),
    'Blocked / RIoT 单 1 条', "$([string]$runtime.Stage) / RIoT 单 $($riotOrders.Count) 条")

$journal.Note('Scenario finished.')
