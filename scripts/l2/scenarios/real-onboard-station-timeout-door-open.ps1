#Requires -Version 7

<#
装货时操作员开了门不放料也不关门，站点期限到期：服务端不结束本站，挂告警持续等待；门关上按真实结果结算。

- 对应决策：ADR-cross-0058 决策 4（期限到期而仓门未闭时不结束本站，转告警、闭合后按真实 IO 结算）；服务端实现
  control-server#81（`ReconcileStationTimeoutDoorNotClosed`，告警码 `STATION_TIMEOUT_DOOR_NOT_CLOSED`）。
  program#55 的结论在这里的体现：期限到期不判失败、不进恢复，把「去关门」这件事交给人，本站一直等。
  Verification 里「仓门未闭超时」一条钉在 `AwaitingLoadResult`（program#25）：本站已下发仓位命令，开着的门由本车
  自己在途的装货解释，会话保持 Ready，引擎才走得到就绪门后面的期限判定。
- 期限值：站点期限 `StationDepartureWaitTimeout` = 40 秒（setup.psd1，出厂 5 分钟），从到站起算；期限之后再晾
  30 秒（`$heldPast`）证「过期一段时间后仍如此」。车载端 `workflow.operationTimeoutMs` 不改（出厂 120 秒，
  README 第 11 条：它是安全相关时序），v2 车载端上它只是提示节拍，不影响本条任何判据。
- 判据来源：服务端 SQLite（`JourneyRuntimes` 的 `Stage`／`BlockReasonCode`／`BlockReasonSince`／
  `StationDepartureWaitStartedAt`，`StationOperations`，`AcceptedDemands`，`SessionRecoveries`，
  `ExceptionRecoverySessions`，`RecoveryWorkflows`，`ProtocolInbox` 里车载端发来的 `OperationProgress`／
  `OperationResult`）与模拟器 `/snapshot` 的仓位物理状态。车载端界面只用来录入子批号，不读任何文字。
  MVP 线同名场景（`origin/ControlServer_MVP:scripts/l2/scenarios/real-onboard-station-timeout-door-open.ps1`）
  是参照：那边期限后空关两次判确定失败，program#55 之后 v2 不走那条路，本条只钉决策 4 这一格，收口换成放料关门。

门是怎么开着的：不用注入。操作员扫完子批号，车载端为这次装货打开锁脉冲，门弹开，人走了——模拟器照实反映。
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

# 与 setup.psd1 的 StationDepartureWaitTimeout 相同。
$window = [TimeSpan]::FromSeconds(40)
# 期限之后再晾多久才读「仍然如此」。
$heldPast = [TimeSpan]::FromSeconds(30)
$alarmCode = 'STATION_TIMEOUT_DOOR_NOT_CLOSED'

$demandGuid = [guid]::NewGuid()
$demandIdWire = $demandGuid.ToString('N')
$demandId = $demandGuid.ToString('D')
$sublot = "L2-DT-$($Context.RunId)"

function Get-Count([string]$sql) { return [int](Invoke-L2Query -Connection $connection -Sql $sql)[0].Total }

function Get-DemandStatus {
    $rows = Invoke-L2Query -Connection $connection -Sql "SELECT Status FROM AcceptedDemands WHERE DemandId = '$demandId'"
    if ($rows.Count -eq 0) { return '(no demand row)' }
    return [string]$rows[0].Status
}

function Get-Session {
    $rows = Invoke-L2Query -Connection $connection `
        -Sql "SELECT Readiness, ReasonCode, SafetyReasonCodesJson FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'"
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

# 「没有进恢复」的三处：异常恢复会话、恢复工作流、落到 RecoveryRequired 的仓位操作。
function Get-RecoveryFootprint {
    return "恢复会话 $(Get-Count "SELECT COUNT(*) AS Total FROM ExceptionRecoverySessions WHERE AgvId = '$($Context.AgvId)'") / " +
        "恢复工作流 $(Get-Count "SELECT COUNT(*) AS Total FROM RecoveryWorkflows WHERE DemandId = '$demandId'") / " +
        "RecoveryRequired 操作 $(Get-Count "SELECT COUNT(*) AS Total FROM StationOperations WHERE DemandId = '$demandId' AND Status = 'RecoveryRequired'")"
}
$noRecovery = '恢复会话 0 / 恢复工作流 0 / RecoveryRequired 操作 0'

# --- 1. 到站、扫码，车载端为装货开门 ---------------------------------------------------------------

Invoke-L2PickupAndScan -Context $Context -DemandIdWire $demandIdWire -DemandId $demandId -Sublot $sublot
$load = Wait-L2WaitingOperator -Context $Context -DemandId $demandId -OperationType 'Load'
$slotNo = $load.SlotNo

# 等到库里记下 AwaitingLoadResult 与期限起点再读（到站那一轮先发报文、后存阶段，README 第 14 条）。
$runtime = Wait-L2Condition -Description 'the journey records that it waits for the load result, with its station deadline' `
    -Journal $journal -Criterion 'awaiting-load-result' -TimeoutSeconds 30 `
    -Probe { Get-L2Runtime -Connection $connection -DemandId $demandId } `
    -Until { param($v) [string]$v.Stage -eq 'AwaitingLoadResult' -and $null -ne (ConvertTo-L2Instant $v.StationDepartureWaitStartedAt) }
$deadline = (ConvertTo-L2Instant $runtime.StationDepartureWaitStartedAt).Add($window)
$journal.Note("Station deadline is $($deadline.ToString('o')) (arrival + $window).")

$physical = Get-L2SlotPhysical -Simulator $simulator -SlotNo $slotNo
$assertions.Add(
    'L2-DT-01', "车载端说在等操作员时，$slotNo 号仓门确实弹开了、空着、开锁输出已复位——操作员走开时的现场原样",
    ($physical -eq 'OPEN/EMPTY/0/0'),
    'OPEN/EMPTY/0/0', $physical)

# --- 2. 车辆把这扇门报上来，会话仍然 Ready ---------------------------------------------------------

$reasons = Wait-L2Condition -Description 'the vehicle reported LOCK_NOT_CLOSED to the server' `
    -Journal $journal -Criterion 'safety-lock-not-closed' -TimeoutSeconds 60 `
    -Probe { $s = Get-Session; if ($s) { [string]$s.SafetyReasonCodesJson } else { $null } } `
    -Until { param($v) $v -like '*LOCK_NOT_CLOSED*' }
$session = Get-Session
$assertions.Add(
    'L2-DT-02', '服务端的安全投影里出现车辆自己算出的 LOCK_NOT_CLOSED，而会话仍是 Ready——开着的门由本车在途的装货解释',
    ("$reasons" -like '*LOCK_NOT_CLOSED*' -and [string]$session.Readiness -eq 'Ready'),
    '含 LOCK_NOT_CLOSED / Ready', "$reasons / $($session.Readiness) ($($session.ReasonCode))")

# 否定判据要有界：让运行时确实又转了几轮，再说期限前没有告警。
$null = Wait-L2Iterations -Riot $riot -Count 2 -Journal $journal
$readAt = [DateTimeOffset]::UtcNow
$before = Get-L2Runtime -Connection $connection -DemandId $demandId
$assertions.Add(
    'L2-DT-03', '期限未到：门开着也不挂告警，旅程原地等装货结果',
    ($readAt -lt $deadline -and [string]$before.Stage -eq 'AwaitingLoadResult' -and [string]::IsNullOrEmpty([string]$before.BlockReasonCode)),
    '读于期限前 / AwaitingLoadResult / 无阻断码',
    "读于期限前 $([math]::Round(($deadline - $readAt).TotalSeconds, 1)) s / $($before.Stage) / '$($before.BlockReasonCode)'")

# --- 3. 期限到期：挂告警，不结束本站 ---------------------------------------------------------------

$journal.Note('Leaving the door open through the station deadline.')
$alarmed = Wait-L2Condition -Description "the stop past its deadline with the door open raised $alarmCode" `
    -Journal $journal -Criterion 'station-timeout-alarm' -TimeoutSeconds ([int]$window.TotalSeconds + 60) `
    -Probe { Get-L2Runtime -Connection $connection -DemandId $demandId } `
    -Until { param($v) [string]$v.BlockReasonCode -eq $alarmCode }
$alarmSince = ConvertTo-L2Instant $alarmed.BlockReasonSince
$operation = Get-L2StationOperation -Connection $connection -DemandId $demandId -OperationType 'Load'
$demandStatus = Get-DemandStatus
$assertions.Add(
    'L2-DT-04', "期限到期挂上 $alarmCode：开始时间不早于期限，stage 仍是 AwaitingLoadResult（不是 Blocked），装货仍在途，需求未被终结",
    ([string]$alarmed.Stage -eq 'AwaitingLoadResult' -and $null -ne $alarmSince -and $alarmSince -ge $deadline -and
        [string]$operation.Status -eq 'Prepared' -and $demandStatus -eq 'Accepted'),
    "AwaitingLoadResult / since >= $($deadline.ToString('o')) / Prepared / Accepted",
    "$($alarmed.Stage) / since $($alarmed.BlockReasonSince) / $($operation.Status) / $demandStatus")

$session = Get-Session
$footprint = Get-RecoveryFootprint
$assertions.Add(
    'L2-DT-05', '告警不是恢复：没有异常恢复会话、没有恢复工作流、没有 RecoveryRequired，会话仍是 Ready',
    ($footprint -eq $noRecovery -and [string]$session.Readiness -eq 'Ready'),
    "$noRecovery / Ready", "$footprint / $($session.Readiness) ($($session.ReasonCode))")

# --- 4. 过期一段时间之后仍然如此 ---------------------------------------------------------------------

$journal.Note("Holding the door open for another $heldPast past the deadline.")
$null = Wait-L2Condition -Description "the stop has been past its deadline for $heldPast" `
    -Journal $journal -Criterion 'held-past-deadline' -TimeoutSeconds ([int]$heldPast.TotalSeconds + 60) `
    -Probe { [DateTimeOffset]::UtcNow } -Until { param($v) $v -ge $deadline.Add($heldPast) }
$null = Wait-L2Iterations -Riot $riot -Count 3 -Journal $journal
$held = Get-L2Runtime -Connection $connection -DemandId $demandId
$operation = Get-L2StationOperation -Connection $connection -DemandId $demandId -OperationType 'Load'
$results = Get-L2OperationResults -Connection $connection -AttemptId $load.AttemptId
$footprint = Get-RecoveryFootprint
$session = Get-Session
$heldPhysical = Get-L2SlotPhysical -Simulator $simulator -SlotNo $slotNo
$assertions.Add(
    'L2-DT-06', "过期 $([int]$heldPast.TotalSeconds) 秒后仍然如此：同一个告警码、开始时间不动，stage 仍是 AwaitingLoadResult，装货仍在途、车载端一份结果都没报，需求未终结，没有恢复，会话 Ready，门仍开着",
    ([string]$held.Stage -eq 'AwaitingLoadResult' -and [string]$held.BlockReasonCode -eq $alarmCode -and
        (ConvertTo-L2Instant $held.BlockReasonSince) -eq $alarmSince -and [string]$operation.Status -eq 'Prepared' -and
        $results.Count -eq 0 -and (Get-DemandStatus) -eq 'Accepted' -and $footprint -eq $noRecovery -and
        [string]$session.Readiness -eq 'Ready' -and $heldPhysical -like 'OPEN/*'),
    "AwaitingLoadResult / $alarmCode since $($alarmed.BlockReasonSince) / Prepared / 0 result / Accepted / $noRecovery / Ready / OPEN",
    "$($held.Stage) / $($held.BlockReasonCode) since $($held.BlockReasonSince) / $($operation.Status) / $($results.Count) result / $(Get-DemandStatus) / $footprint / $($session.Readiness) / $heldPhysical")

# --- 5. 放料关门：按真实结果结算，告警清掉 ---------------------------------------------------------

$settled = Wait-L2Change -Description 'the load settled as reported once the door was shut over the cargo' `
    -Journal $journal -Criterion 'load-settled' -TimeoutSeconds 120 `
    -Baseline { [string](Get-L2StationOperation -Connection $connection -DemandId $demandId -OperationType 'Load').Status } `
    -Action {
        $journal.Note("The operator finally puts the cargo in slot $slotNo and shuts the door.")
        $null = $simulator.Command('Put', "slots/$slotNo/cargo", @{ state = 'OCCUPIED' })
        $null = $simulator.Command('Post', "slots/$slotNo/close-door", @{})
    } `
    -Probe {
        $r = Get-L2Runtime -Connection $connection -DemandId $demandId
        [pscustomobject]@{
            Operation = [string](Get-L2StationOperation -Connection $connection -DemandId $demandId -OperationType 'Load').Status
            Stage     = [string]$r.Stage
            Block     = [string]$r.BlockReasonCode
        }
    } `
    -Until { param($before, $now) $before -eq 'Prepared' -and $now.Operation -eq 'Committed' -and
        $now.Stage -ne 'AwaitingLoadResult' -and $now.Block -ne $alarmCode }
$results = Get-L2OperationResults -Connection $connection -AttemptId $load.AttemptId
$outcomes = ($results | ForEach-Object { [string]$_.Payload.overallOutcome }) -join ','
$loadPhysical = Get-L2SlotPhysical -Simulator $simulator -SlotNo $slotNo
$assertions.Add(
    'L2-DT-07', '放料关门后车载端报 COMPLETED（整个 attempt 唯一一份结果），装货 Committed，仓位关门、锁上、有货',
    ($outcomes -eq 'COMPLETED' -and $settled.Value.Operation -eq 'Committed' -and $loadPhysical -eq 'CLOSED/OCCUPIED/1/0'),
    'COMPLETED / Committed / CLOSED/OCCUPIED/1/0', "$outcomes / $($settled.Value.Operation) / $loadPhysical")
$assertions.Add(
    'L2-DT-08', "告警清掉，旅程离开 AwaitingLoadResult 往下走，需求仍在（没有被取消）",
    ($settled.Value.Block -ne $alarmCode -and $settled.Value.Stage -ne 'AwaitingLoadResult' -and (Get-DemandStatus) -eq 'Accepted'),
    "非 $alarmCode / 非 AwaitingLoadResult / Accepted",
    "'$($settled.Value.Block)' / $($settled.Value.Stage) / $(Get-DemandStatus)")

$footprint = Get-RecoveryFootprint
$assertions.Add(
    'L2-DT-09', '全程没有进过恢复', ($footprint -eq $noRecovery), $noRecovery, $footprint)

$journal.Note('Scenario finished.')
