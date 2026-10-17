#Requires -Version 7

<#
仓位在扫码之后才分，分的是同侧当时编号最小的空仓（control-server#754，REQ-0352、REQ-0208，派车问题 3）。

现场 10-17 的样子：前侧 1～4 号都空着，操作员扫码后车开了 3 号。原因是那条需求受理时 1、2 号还被同车另一条需求占着，
受理当时就把它的仓号定成了 [3,4]；后来那一条被结掉、1、2 空了，定下的 [3,4] 也不改。这条场景把那个形状原样搭出来：

  1. 需求甲（2 个花篮，N1-3 → FRONT）受理，车开到取货站。受理只占数量，不定仓号。
  2. 车停在站上、甲还没扫码时，同站追加需求乙（2 个花篮，同一 AREA）。当前停靠不并，乙另开一个停靠排在后面（批次7-06 的口径）。
     乙同样只占数量。旧做法下此刻甲是 [1,2]、乙是 [3,4]。
  3. 甲一直不扫码，站点期限把它结掉（甲是锚需求、旅程上还有乙，放不回，终结）。甲从没装货，1、2 号空回来。
  4. 车进入乙的停靠（同一个站），操作员扫乙：乙分到的是此刻前侧最小的 1、2 号，装货命令开的也是它们。
     旧做法开的是 3、4——这就是红证据的样子。

写入边界（README 第 14 条）：分配仓位与装货命令、StationOperations、归属 LOADING 是同一个写事务（control-server#754 起），
所以等到乙的装货操作出现再读归属行的仓号是安全的。期限结掉甲那一次保存里有甲的归属 TERMINATED 与本站清单结束。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'CargoHoldingCommon.ps1')
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2SlotGroups.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2ConditionOrLast.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$connection = $Context.Connection
$onboard = $Context.Onboard
$baskets = 2

$first = New-L2CargoDemand 'A' 'N1-3' $baskets $Context.RunId
$second = New-L2CargoDemand 'B' 'N1-3' $baskets $Context.RunId

# Invoke-L2Query 已经把 DBNull 换成了 $null，所以这里只判 $null。
function Test-L2Null($value) {
    return $null -eq $value
}

function Get-Membership([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT JourneyId, PickupStopId, Status, TargetSlotsJson, ExpectedBasketCount, RemovedAt, RemovalReason " +
        "FROM JourneyDemands WHERE DemandId = '$DemandId' ORDER BY AddedAt DESC LIMIT 1")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-Stop([string]$StopId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT StopId, Sequence, StationRiotId, Status FROM JourneyStops WHERE StopId = '$StopId'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-LoadOperation([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection -Sql (
        "SELECT SlotOperationAttemptId, TargetSlotsJson, Status FROM StationOperations " +
        "WHERE DemandId = '$DemandId' AND OperationType = 'Load'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Get-LoadCommandSlots([string]$DemandId) {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT PayloadJson FROM ProtocolOutbox WHERE MessageType = 'SlotOperationCommand'"
    $slots = @($rows | ForEach-Object { ([string]$_.PayloadJson | ConvertFrom-Json).payload } |
        Where-Object { [string]$_.demandId -eq $DemandId -and [string]$_.operationType -ceq 'LOAD' } |
        ForEach-Object { (@($_.slots | ForEach-Object { [int]$_ }) -join ',') } | Sort-Object -Unique)
    return , $slots
}

# --- 0. 车停在关卡、路网引擎拉起来；录入先设成 Manual，甲到站时对端不替操作员扫码 ------------------------------

Initialize-L2CargoRig $Context
$journal.Note('The synthetic peer holds entry requests open: demand A is never scanned.')
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Manual' })

# --- 1. 甲受理（只占数量），车开到取货站 ------------------------------------------------------------------------

Publish-L2CargoDemand $Context $first
$journey = Wait-L2Condition -Description 'demand A was accepted' -Journal $journal -Criterion 'journey' -TimeoutSeconds 90 `
    -Probe { Get-L2CargoJourney $connection $first.Id } -Until { param($v) $null -ne $v }
$journeyId = [string]$journey.JourneyId
$null = Assert-L2SlotsUnassignedAtAcceptance -Assertions $assertions -Id 'L2-SAS-01' -Connection $connection `
    -DemandId $first.Id -BasketCount $baskets

$pickup = Get-L2CargoCurrentStop $connection $journeyId
$station = [int]$pickup.StationRiotId
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $station
$null = Wait-L2Condition -Description 'the vehicle waits for a sublot at the first pickup stop' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 120 `
    -Probe { [string](Get-L2CargoJourney $connection $first.Id).Stage } -Until { param($v) $v -eq 'AwaitingSublot' }
# 扫码之前这辆车的可用仓（前侧四个都空）：乙扫码之后分到的应当是其中最小的两个。
$available = Get-L2AvailableSlots -Connection $connection -AgvId $Context.AgvId
$journal.Note("Available slots before any scan: [$($available -join ',')].")

# --- 2. 车停在站上时同站追加乙：另开一个停靠，只占数量 ---------------------------------------------------------

Publish-L2CargoDemand $Context $second
$secondMember = Wait-L2ConditionOrLast -Description 'demand B was appended to the same journey' `
    -Journal $journal -Criterion 'append' -TimeoutSeconds 45 `
    -Probe { Get-Membership $second.Id } `
    -Until { param($v) $null -ne $v -and [string]$v.JourneyId -eq $journeyId -and (Test-L2Null $v.RemovedAt) }
$secondStop = if ($null -eq $secondMember) { $null } else { Get-Stop ([string]$secondMember.PickupStopId) }
$assertions.Add(
    'L2-SAS-02', '乙在车停站时同站追加进同一趟旅程：另开一个停靠排在甲的停靠后面、同一个站，只预留 2 个花篮的数量、不定仓号',
    ($null -ne $secondMember -and [string]$secondMember.JourneyId -eq $journeyId -and $null -ne $secondStop -and
        [string]$secondStop.StopId -ne [string]$pickup.StopId -and [int]$secondStop.Sequence -gt [int]$pickup.Sequence -and
        [int]$secondStop.StationRiotId -eq $station -and [int]$secondMember.ExpectedBasketCount -eq $baskets -and
        [string]$secondMember.TargetSlotsJson -ceq '[]'),
    "journey $journeyId / new stop after sequence $($pickup.Sequence) at station $station / baskets $baskets / []",
    $(if ($null -eq $secondMember) { '(not appended)' } else {
        "journey $($secondMember.JourneyId) / stop $($secondStop.StopId) seq $($secondStop.Sequence) at $($secondStop.StationRiotId) / " +
        "baskets $($secondMember.ExpectedBasketCount) / $($secondMember.TargetSlotsJson)" }))
if ($null -eq $secondMember) { throw 'L2-SAS-02 did not hold: demand B was not appended; every later criterion needs it.' }

# --- 3. 甲一直不扫码，站点期限把它结掉：从没装货，不留仓位 ---------------------------------------------------

$ended = Wait-L2ConditionOrLast -Description 'the station deadline ended demand A''s stop' `
    -Journal $journal -Criterion 'first-ended' -TimeoutSeconds 120 `
    -Probe { Get-Membership $first.Id } `
    -Until { param($v) [string]$v.Status -eq 'TERMINATED' -or -not (Test-L2Null $v.RemovedAt) }
$firstOperation = Get-LoadOperation $first.Id
$assertions.Add(
    'L2-SAS-03', '甲在站点期限内没人扫码，被结掉（终结或放回），从没下装货命令，归属行上也没有仓号',
    ($null -ne $ended -and ([string]$ended.Status -eq 'TERMINATED' -or -not (Test-L2Null $ended.RemovedAt)) -and
        $null -eq $firstOperation -and [string]$ended.TargetSlotsJson -ceq '[]'),
    'TERMINATED or removed / no load / []',
    "$($ended.Status) removed=$($ended.RemovedAt) $($ended.RemovalReason) / " +
    "$(if ($null -eq $firstOperation) { 'no load' } else { "load $($firstOperation.TargetSlotsJson)" }) / $($ended.TargetSlotsJson)")

# --- 4. 车进入乙的停靠，扫乙：分到前侧此刻最小的两个仓 ---------------------------------------------------------

$journal.Note("Operator scans demand B's sublot $($second.Sublot) at its own stop.")
$null = $onboard.Command('Put', 'sublot-scan', @{ sublot = $second.Sublot; rescan = $true })
$null = $onboard.Command('Put', 'policy', @{ sublot = 'Auto' })
$null = Move-L2CargoVehicleToCurrentStop $Context $journeyId $station ([string]$secondStop.StopId)
$operation = Wait-L2ConditionOrLast -Description 'demand B''s load was commanded after its scan' `
    -Journal $journal -Criterion 'second-load' -TimeoutSeconds 120 `
    -Probe { Get-LoadOperation $second.Id } -Until { param($v) $null -ne $v }

$assigned = Assert-L2SlotGroupTargets -Assertions $assertions -Id 'L2-SAS-04' -Connection $connection `
    -DemandId $second.Id -SlotPosition 'FRONT' -AvailableSlots $available `
    -Description '乙扫码后分到的仓全部属于前侧、升序，且恰好是扫码之前前侧编号最小的 2 个空仓（甲结掉后空回来的那两个也算）'
$sent = Get-LoadCommandSlots $second.Id
$memberSlots = (@($assigned.Actual) -join ',')
$operationSlots = if ($null -eq $operation) { '' } else { (@([string]$operation.TargetSlotsJson | ConvertFrom-Json) -join ',') }
$assertions.Add(
    'L2-SAS-05', '乙的装货命令开的就是分到的仓，与装货记录一致：现场那种「1～4 都空却开 3 号」不再出现',
    ($null -ne $operation -and $sent.Count -eq 1 -and $sent[0] -eq $memberSlots -and $operationSlots -eq $memberSlots -and
        $memberSlots -ne ''),
    "LOAD [$memberSlots] / StationOperations [$memberSlots]",
    "LOAD $(if ($sent.Count -eq 0) { '(none)' } else { ($sent | ForEach-Object { "[$_]" }) -join ' ' }) / StationOperations [$operationSlots]")

$journal.Note('Scenario finished.')
