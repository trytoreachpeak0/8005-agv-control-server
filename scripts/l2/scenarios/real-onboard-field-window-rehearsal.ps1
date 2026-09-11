#Requires -Version 7

<#
现场窗口一（无人）在真车载端与真 slots-simulator 上的整窗彩排。8005-agv-program#43。

**驱动、checkpoint、现场记录、finalize 四样全是上车的那一份。**

- 操作员的每一个动作都由 `scripts/field/FieldOperator.psm1` 的上车函数做：扫码、空关、放料关门、门开着
  走开、回来空关、关卡取空。以 `Local` 传输跑在两个 loopback 端口上，与现场只差传输。
- 证据采集器 `Invoke-SlotConvergenceFieldWindow.ps1` 在驱动脚本说「就是这一刻」的时候打 checkpoint：
  先在场景的只读连接上 `VACUUM INTO` 出一份一致快照，再以 `-DatabaseSnapshot` 喂给它——现场是它自己经
  scp 拷，彩排是这里出，判据代码一行不分支。**不拷三件套**：`-002` 红在 `Copy-Item` 读不了被 SQLite
  字节区间锁住的 `-shm`（`-001` 碰巧没撞上），而活库上分三次拷出来的文件本来也不是同一时刻。
- 现场记录由 `New-FieldWindowRecord` 按驱动脚本实际做了什么写出，finalize 在这份记录上判。它的 SC1-*
  判据逐条抄进本场景的判据表（`L2-FW-SC1-*`），所以这一次运行的绿就是采集器在这趟旅程上的绿。

编排与 #19 最后一版、#45 的剧本一致：四需求满仓旅程，**停靠 1 演 A，停靠 2 演 C 接 B**，停靠 3、4
正常装，去关卡卸货收尾。采集器在停靠 4 装完之后就 finalize，关卡卸货另判一条 `L2-FW-40`——卸货不在
SC1-* 里，而多需求关卡清单当前被车载端拒收（8005-agv-program#48），排在后面才不会把 A、C、B 的判决一起
挡掉。与现场只差三处，都写进判据表而不是藏起来：

1. 站点期限一分钟（现场五分钟），停靠 2 才等得到。
2. 场景 C 期限之后只等 **3 分钟**（现场 20 分钟），采集器按 `-MinimumHoldMinutes 3` 判，SUMMARY 会写明
   「只能算彩排」。3 分钟够长：`operationTimeoutMs` 120 秒，提示节拍在期限后约一分钟就会再响一次，
   SC1-C-06 要的正是它。
3. 车由本场景替 RIoT 开到站。现场是真车自己走，驱动脚本在那里只等 stage。

**补偿清空不在这趟旅程里演。**多需求旅程里补偿掉一条需求之后，旅程停在 `Blocked` 再也不动
（`JourneyRuntimeEngine` 对 `Blocked` 只 `return`，而补偿只在 `journeyComplete` 时才改 stage），车上
另外三站的货就回不到关卡——本票查实并另开票。补偿这一幕由
`real-onboard-field-operator-compensate` 单独证。
#>
[CmdletBinding()]
param([Parameter(Mandatory)][object]$Context)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$journal = $Context.Journal
$assertions = $Context.Assertions
$riot = $Context.Riot
$mes = $Context.MesIngest
$connection = $Context.Connection

Import-Module (Join-Path $Context.Repository 'scripts/field/FieldOperator.psm1') -Force

$holdMinutes = 3
$collector = Join-Path $Context.Repository 'scripts/field/Invoke-SlotConvergenceFieldWindow.ps1'
$windowRoot = Join-Path (Split-Path -Parent $Context.SnapshotRoot) 'field-window'
$checkpointStage = Join-Path $Context.StageRoot 'field-checkpoints'
$null = New-Item -ItemType Directory -Path $checkpointStage -Force

$areas = @('N1-3', 'N2-6', 'N3-4', 'N4-2')
foreach ($area in $areas) {
    $wire = [guid]::NewGuid().ToString('N')
    $sublot = "L2-FWR-$area-$($Context.RunId)"
    $journal.Note("Publishing demand $sublot in area $area.")
    $null = $mes.Command('Put', "demands/$wire", @{
        sublot = $sublot; area = $area; eqp = "EQP-L2-$area"; package = 'L2-PACKAGE'; maxBoxCount = 4
    })
}

$field = New-FieldOperator -Connection $connection -AgvId $Context.AgvId `
    -SimulatorPort $Context.SimulatorHttpPort -AutomationPort $Context.OnboardAutomationPort `
    -Log { param($message) $journal.Note("field-operator: $message") }

# --- helpers ----------------------------------------------------------------------------------------

<#
现场 checkpoint 的彩排版：出一份一致快照，交给采集器。每次一个新文件，不复用——finalize 读的是最后一份。
#>
function Save-DatabaseSnapshot([string]$label) {
    $script:checkpointCount++
    $path = Join-Path $checkpointStage ('{0:d2}-{1}.db' -f $script:checkpointCount, $label)
    $command = $connection.CreateCommand()
    $command.CommandText = "VACUUM INTO '$($path.Replace("'", "''"))'"
    $null = $command.ExecuteNonQuery()
    $command.Dispose()
    return $path
}

function Invoke-WindowCheckpoint([string]$label) {
    $script:lastDatabaseCopy = Save-DatabaseSnapshot $label
    $output = & pwsh -NoProfile -File $collector -EvidenceRoot $windowRoot -Checkpoint $label `
        -DatabaseSnapshot $script:lastDatabaseCopy -HostDirectory $Context.HostDirectory 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Collector checkpoint '$label' failed (exit $LASTEXITCODE): $($output | Out-String)" }
    $journal.Note("Collector checkpoint $label taken.")
}
$script:checkpointCount = 0
$script:lastDatabaseCopy = $null

function Get-ConfirmedUpperId([int]$sequence) {
    # 只认 CONFIRMED：建单确认之前假 RIoT 不认这个 UpperId，早改单会回 409（README 第 5 条）。
    $rows = Invoke-L2Query -Connection $connection -Sql (
        'SELECT o.UpperId, s.StationRiotId FROM JourneyStops s JOIN OrderIntents o ON o.MovementLegId = s.MovementLegId ' +
        "WHERE s.JourneyId = '$script:journeyId' AND s.Sequence = $sequence AND o.Status = 'CONFIRMED'")
    if ($rows.Count -eq 0) { return $null }
    return $rows[0]
}

function Invoke-DriveToStop([int]$sequence) {
    $script:driveSequence = $sequence
    $order = Wait-L2Condition -Description "stop $sequence has a confirmed movement order" `
        -Journal $journal -Criterion "stop-$sequence-order" -TimeoutSeconds 180 `
        -Probe { Get-ConfirmedUpperId $script:driveSequence } -Until { param($v) $v }
    $journal.Note("Vehicle drives to stop $sequence (RIoT station $($order.StationRiotId)).")
    $null = $riot.Command('Put', "orders/$($order.UpperId)", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'
        speed = 0.8; processingOrder = $true; orderTaskId = $order.UpperId
    })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = [int]$order.StationRiotId; processingOrder = $false; clearOrderTaskId = $true
    })
    $null = $riot.Command('Put', "orders/$($order.UpperId)", @{ orderState = 5 })
}

# --- 1. 派车、开到停靠 1，窗口开始 ------------------------------------------------------------------------

$journey = Wait-L2Condition -Description 'a journey was dispatched to the first pickup' -Journal $journal `
    -Criterion 'journey-dispatched' -TimeoutSeconds 120 `
    -Probe { Get-FieldJourney -Field $field } -Until { param($v) [string]$v.Stage -eq 'AwaitingPickupArrival' }
$script:journeyId = [string]$journey.JourneyId
Invoke-WindowCheckpoint '00-ready'

Invoke-DriveToStop 1
$stopCount = Wait-L2Condition -Description 'all four demands joined the journey' -Journal $journal `
    -Criterion 'journey-stops' -TimeoutSeconds 120 `
    -Probe { (Invoke-L2Query -Connection $connection -Sql "SELECT COUNT(*) AS N FROM JourneyStops WHERE JourneyId = '$script:journeyId'")[0].N } `
    -Until { param($v) [int]$v -ge 5 }
$assertions.Add('L2-FW-01', '四条需求凑成一趟旅程：四个取货停靠加一个关卡', ([int]$stopCount -eq 5), 5, [int]$stopCount)

# --- 2. 停靠 1：场景 A -----------------------------------------------------------------------------------

$actA = Invoke-FieldActReopen -Field $field -JourneyId $script:journeyId -Sequence 1 -Rounds 2 -MinimumSecondsLeft 20
$assertions.Add(
    'L2-FW-10', '停靠 1 场景 A：驱动脚本空关两轮都换来车自己重开，之后放料提交，恢复入口一次没出现',
    ($actA.RoundsCompleted -eq 2 -and $actA.Status -eq 'Committed' -and -not $actA.RecoveryEntryVisible -and $actA.Unlocking -ge 3),
    '2 轮 / Committed / 入口未出现 / UNLOCKING >= 3',
    "$($actA.RoundsCompleted) 轮 / $($actA.Status) / 入口$($actA.RecoveryEntryVisible ? '出现过' : '未出现')（看到过的按钮：$(@($actA.ActionsSeen) -join ',')） / UNLOCKING $($actA.Unlocking)")

# --- 3. 停靠 2：场景 C 接 B，checkpoint 由驱动脚本在那一刻打 ------------------------------------------------

Invoke-DriveToStop 2
$actCB = Invoke-FieldActDoorLeftOpen -Field $field -JourneyId $script:journeyId -Sequence 2 -HoldMinutes $holdMinutes `
    -StillWaitingCheckpoint 'c-plus-hold' -OnCheckpoint { param($label) Invoke-WindowCheckpoint $label }
$assertions.Add(
    'L2-FW-20', "停靠 2 场景 C 接 B：门开着过期挂告警并撑满 $holdMinutes 分钟，回来空关后结算成 Failed，旅程自己离站",
    ($actCB.Status -eq 'Failed' -and $actCB.PositionAfter -ne '2/AwaitingLoadResult'),
    'Failed / 离开 2/AwaitingLoadResult', "$($actCB.Status) / $($actCB.PositionAfter)（空关 $($actCB.EmptyCloses) 次，重开 $($actCB.GraceReopens) 次）")

# --- 4. 停靠 3、4：正常装 ----------------------------------------------------------------------------------

foreach ($sequence in 3, 4) {
    Invoke-DriveToStop $sequence
    $load = Invoke-FieldActLoad -Field $field -JourneyId $script:journeyId -Sequence $sequence
    $assertions.Add("L2-FW-3$sequence", "停靠 ${sequence}：驱动脚本照常装载提交", ($load.Status -eq 'Committed'), 'Committed', $load.Status)
}

# --- 5. 现场记录与 finalize：采集器判的是 A、C、B，停靠 4 装完就判 --------------------------------------------
#
# 放在关卡之前，不是图省事。-004 第一次把真装置上的多需求旅程开到关卡，车载端以 PROTOCOL_SCHEMA_INVALID
# 拒收三项的关卡作业清单、会话锁死在重连循环里（8005-agv-program#48）。SC1-* 一条都不看关卡，finalize
# 若排在卸货之后，采集器对 A、C、B 的判决就被一个与它们无关的缺陷挡住、一次都出不来。

$record = New-FieldWindowRecord -AgvId $Context.AgvId -IoModule "127.0.0.1:$($Context.SimulatorModbusPort)" `
    -RecoveryWindowOpen $true -DriverRunId $Context.RunId -Site 'L2 真装置彩排（本机）' -ActA $actA -ActCB $actCB
$recordPath = Join-Path $checkpointStage 'field-record.json'
[IO.File]::WriteAllText($recordPath, ($record | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

$finalCopy = Save-DatabaseSnapshot 'finalize'
$finalOutput = & pwsh -NoProfile -File $collector -EvidenceRoot $windowRoot -Finalize -RecordPath $recordPath `
    -DatabaseSnapshot $finalCopy `
    -HostDirectory $Context.HostDirectory -MinimumHoldMinutes $holdMinutes 2>&1
$finalExit = $LASTEXITCODE
$journal.Note("Collector finalize exit $finalExit`: $(($finalOutput | Out-String).Trim())")

$judged = Get-Content -LiteralPath (Join-Path $windowRoot 'assertions.json') -Raw | ConvertFrom-Json
foreach ($item in @($judged.assertions)) {
    $assertions.Add("L2-FW-$($item.id)", "采集器：$($item.description)", ($item.outcome -eq 'PASS'),
        (($item.expected | ConvertTo-Json -Compress -Depth 4) ?? ''), (($item.actual | ConvertTo-Json -Compress -Depth 4) ?? ''))
}
$assertions.Add(
    'L2-FW-50', '采集器在驱动脚本写出的记录上 finalize，整窗 PASS',
    ($finalExit -eq 0 -and [string]$judged.outcome -eq 'PASS'), 'exit 0 / PASS', "exit $finalExit / $($judged.outcome)")

# --- 6. 关卡卸货，旅程收尾 ------------------------------------------------------------------------------------

# 关卡停靠的序号不是 5：服务端按自己的规则编号（-003 实测是 9），按 Role 找。
$gateSequence = [int](Invoke-L2Query -Connection $connection `
    -Sql "SELECT Sequence FROM JourneyStops WHERE JourneyId = '$script:journeyId' AND Role = 'GATE'")[0].Sequence
Invoke-DriveToStop $gateSequence
# 判据而不是抛异常：卸货走不通时要的是一条点名原因的红判据，外加仍然写得出来的上面那些绿判据。
$unloadStatuses = @()
$unloadError = $null
try {
    $unload = Invoke-FieldActUnload -Field $field -JourneyId $script:journeyId -TimeoutSeconds 300
    $unloadStatuses = @($unload.Operations.Values)
} catch {
    $unloadError = $_.Exception.Message
    $journal.Note("Gate unload did not complete: $unloadError")
}
$gateStage = [string](Get-FieldJourney -Field $field -JourneyId $script:journeyId).Stage
$session = @(Invoke-L2Query -Connection $connection -Sql "SELECT Readiness, ReasonCode FROM SessionRecoveries WHERE AgvId = '$($Context.AgvId)'")
$assertions.Add(
    'L2-FW-40', '关卡：驱动脚本把三条装上车的需求逐条取空，每条卸货都提交，旅程 Completed（多需求关卡清单被车载端拒收时红，见 8005-agv-program#48）',
    ($null -eq $unloadError -and $gateStage -eq 'Completed' -and
        $unloadStatuses.Count -eq 3 -and @($unloadStatuses | Where-Object { $_ -ne 'Committed' }).Count -eq 0),
    'Completed / 3 条卸货 Committed',
    "$gateStage / $($unloadStatuses -join ',') / 会话 $(($session.Count -gt 0) ? "$($session[0].Readiness)/$($session[0].ReasonCode)" : '-')$($unloadError ? " / $unloadError" : '')")

$journal.Note('Scenario finished.')
