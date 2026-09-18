#Requires -Version 7

<#
关卡卸货时操作员把门带上了，货没取走：车每轮自己重开，直到取空；取空后提交，需求完成。

- 对应决策：ADR-cross-0058 决策 1 的卸货侧（目标态闭环，读到相反态就重开，不设次数上限），车载端实现
  onboard-hmi#72。加上 ADR-cross-0015 的不对称：卸货是 UnloadCompletionRequired，没有取消分支，决策 5（及其
  2026-09-18 改写）明写「卸货不适用本条」——所以本条在两轮重开之后断言的是「什么都没有被结算」，唯一的终结是货真的被取走。
  program#55 只改了装货侧的期限行为，卸货侧不受影响。
- 期限值：站点期限 `StationDepartureWaitTimeout` = 30 秒（setup.psd1，与装置默认相同），只作用于取货站；关卡卸货
  没有期限。车载端 `workflow.operationTimeoutMs` 不改（出厂 120 秒，README 第 11 条），本条每一轮都在它之内走完。
- 判据来源：服务端 SQLite（`ProtocolInbox` 里车载端发来的 `OperationProgress`／`OperationResult`，`StationOperations`，
  `JourneyRuntimes`，`AcceptedDemands`，`SessionRecoveries`，`ExceptionRecoverySessions`，`RecoveryWorkflows`）与
  模拟器 `/snapshot` 的仓位物理状态。车载端界面只用来录入子批号，不读任何文字。
  MVP 线参照 `origin/ControlServer_MVP:scripts/l2/scenarios/real-onboard-unload-not-emptied.ps1`。

重开的证据是「门关上后又回到 OPEN」加车载端的 `UNLOCKING` 条数，理由见 `L2RealStation.psm1` 的
`Invoke-L2CloseOverOppositeState`：模拟器没有开锁脉冲计数器，而它的门只能被开锁输出弹开。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2Change.psm1') -Force
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'L2RealStation.psm1') -Force

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$simulator = $Context.Simulator
$connection = $Context.Connection

if ([string]::IsNullOrEmpty([string]$Context.OnboardJournalPath)) {
    throw 'This scenario needs the real onboard rig (Onboard = ''Real'').'
}

$reopenRounds = 2

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-UE-$($Context.RunId)"

function Get-Count([string]$sql) { return [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].Total }

function Get-DemandStatus {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return '(no demand row)' }
    return [string]$rows[0].Status
}

function Get-Stage { $r = Get-L2Runtime -Connection $connection -DemandId $demandId; if ($r) { [string]$r.Stage } else { $null } }

function Get-RecoveryFootprint {
    $session = @(Invoke-L2Query -Connection $connection -Sql "SELECT Readiness FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'")
    return "会话 $(if ($session.Count -ge 1) { $session[0].Readiness } else { '(none)' }) / " +
        "恢复会话 $(Get-Count "SELECT COUNT(*) AS Total FROM ExceptionRecoverySessions WHERE AgvId = '$($Context.AgvId)'") / " +
        "恢复工作流 $(Get-Count "SELECT COUNT(*) AS Total FROM RecoveryWorkflows WHERE DemandId = '$demandId'")"
}
$noRecovery = '会话 Ready / 恢复会话 0 / 恢复工作流 0'

# --- 1. 正常装一篮货 -------------------------------------------------------------------------------

Invoke-L2PickupAndScan -Context $Context -DemandIdWire $demandIdWire -DemandId $demandId -Sublot $sublot
$load = Wait-L2WaitingOperator -Context $Context -DemandId $demandId -OperationType 'Load'
$journal.Note("Operator puts the cargo in slot $($load.SlotNo) and shuts the door.")
$null = $simulator.Command('Put', "slots/$($load.SlotNo)/cargo", @{ state = 'OCCUPIED' })
$null = $simulator.Command('Post', "slots/$($load.SlotNo)/close-door", @{})

$null = Wait-L2Condition -Description 'the load committed and the journey reached the gate leg' `
    -Journal $journal -Criterion 'journey-stage' -TimeoutSeconds 180 `
    -Probe { Get-Stage } -Until { param($v) $v -eq 'AwaitingGateArrival' }
$loadPhysical = Get-L2SlotPhysical -Simulator $simulator -SlotNo $load.SlotNo
$loadStatus = [string](Get-L2StationOperation -Connection $connection -DemandId $demandId -OperationType 'Load').Status
$assertions.Add(
    'L2-UE-01', '前置：装货正常提交，仓位关门、锁上、有货，旅程进到去关卡那一段',
    ($loadStatus -eq 'Committed' -and $loadPhysical -eq 'CLOSED/OCCUPIED/1/0'),
    'Committed / CLOSED/OCCUPIED/1/0', "$loadStatus / $loadPhysical")

# --- 2. 车到关卡，车载端为卸货开门 -----------------------------------------------------------------

$gateIntent = Wait-L2ConfirmedIntent -Context $Context -DemandId $demandId -Purpose 'TO_GATE'
$journal.Note('Vehicle drives to the gate and comes to rest.')
Invoke-L2DriveTo -Context $Context -Intent $gateIntent -StationRiotId $Context.GateStationRiotId

$unload = Wait-L2WaitingOperator -Context $Context -DemandId $demandId -OperationType 'Unload'
$attemptId = $unload.AttemptId
$slotNo = $unload.SlotNo
$physical = Get-L2SlotPhysical -Simulator $simulator -SlotNo $slotNo
$assertions.Add(
    'L2-UE-02', "卸的是装货用的那个仓，车载端说在等操作员时门确实开着、货还在、开锁输出已复位",
    ($slotNo -eq $load.SlotNo -and $physical -eq 'OPEN/OCCUPIED/0/0'),
    "slot $($load.SlotNo) / OPEN/OCCUPIED/0/0", "slot $slotNo / $physical")

# --- 3. 带货关门两轮：每轮车自己重开 ---------------------------------------------------------------

for ($round = 1; $round -le $reopenRounds; $round++) {
    $reopen = Invoke-L2CloseOverOppositeState -Context $Context -AttemptId $attemptId -SlotNo $slotNo -Criterion "unload-reopen-$round"
    $assertions.Add(
        "L2-UE-$('{0:d2}' -f (2 + $round))",
        "第 $round 轮带货关门：车载端自己重新开锁（UNLOCKING +1）、门又弹开（模拟器 OPEN、货还在、开锁输出复位）、再次等操作员",
        ($reopen.After.Unlocking -eq $reopen.Before.Unlocking + 1 -and $reopen.After.Waiting -gt $reopen.Before.Waiting -and
            $reopen.After.Physical -eq 'OPEN/OCCUPIED/0/0'),
        "UNLOCKING $($reopen.Before.Unlocking)→$($reopen.Before.Unlocking + 1) / WAITING_OPERATOR 增加 / OPEN/OCCUPIED/0/0",
        "UNLOCKING $($reopen.Before.Unlocking)→$($reopen.After.Unlocking) / WAITING_OPERATOR $($reopen.Before.Waiting)→$($reopen.After.Waiting) / $($reopen.After.Physical)")
}

# 卸货没有取消分支，也没有确定失败：两轮之后不许出现任何一种结算。否定判据先让运行时再转几轮。
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$operation = Get-L2StationOperation -Connection $connection -DemandId $demandId -OperationType 'Unload'
$results = Get-L2OperationResults -Connection $connection -AttemptId $attemptId
$stage = Get-Stage
$demandStatus = Get-DemandStatus
$footprint = Get-RecoveryFootprint
$assertions.Add(
    'L2-UE-05', "重开 $reopenRounds 轮之后什么都没结算：卸货仍在途（Prepared）、车载端一份结果都没报、旅程等在 AwaitingUnloadResult、需求 Accepted、没有恢复",
    ([string]$operation.Status -eq 'Prepared' -and $results.Count -eq 0 -and $stage -eq 'AwaitingUnloadResult' -and
        $demandStatus -eq 'Accepted' -and $footprint -eq $noRecovery),
    "Prepared / 0 result / AwaitingUnloadResult / Accepted / $noRecovery",
    "$($operation.Status) / $($results.Count) result / $stage / $demandStatus / $footprint")

# --- 4. 取空关门：提交，需求完成 -------------------------------------------------------------------

$journal.Note("The operator finally takes the cargo out of slot $slotNo and shuts the door.")
$null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = 'EMPTY' })
$null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})

$completed = Wait-L2Condition -Description 'the unload committed and the journey completed' `
    -Journal $journal -Criterion 'journey-completed' -TimeoutSeconds 180 `
    -Probe { [pscustomobject]@{ Stage = Get-Stage; Demand = Get-DemandStatus } } `
    -Until { param($v) $v.Stage -eq 'Completed' -and $v.Demand -eq 'Succeeded' }
$operation = Get-L2StationOperation -Connection $connection -DemandId $demandId -OperationType 'Unload'
$results = Get-L2OperationResults -Connection $connection -AttemptId $attemptId
$outcomes = ($results | ForEach-Object { [string]$_.Payload.overallOutcome }) -join ','
$finalPhysical = Get-L2SlotPhysical -Simulator $simulator -SlotNo $slotNo
$assertions.Add(
    'L2-UE-06', '取空后车载端报 COMPLETED（整个卸货 attempt 唯一一份结果），卸货 Committed，仓位空、门关、锁上',
    ($outcomes -eq 'COMPLETED' -and [string]$operation.Status -eq 'Committed' -and
        [string]$operation.SlotOperationAttemptId -eq $attemptId -and $finalPhysical -eq 'CLOSED/EMPTY/1/0'),
    "COMPLETED / Committed $attemptId / CLOSED/EMPTY/1/0",
    "$outcomes / $($operation.Status) $($operation.SlotOperationAttemptId) / $finalPhysical")
$assertions.Add(
    'L2-UE-07', '需求完成（Succeeded），旅程 Completed',
    ($completed.Demand -eq 'Succeeded' -and $completed.Stage -eq 'Completed'),
    'Succeeded / Completed', "$($completed.Demand) / $($completed.Stage)")

$footprint = Get-RecoveryFootprint
$assertions.Add('L2-UE-08', '全程没有进过恢复', ($footprint -eq $noRecovery), $noRecovery, $footprint)

$journal.Note('Scenario finished.')
