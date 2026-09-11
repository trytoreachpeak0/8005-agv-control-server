#Requires -Version 7

<#
现场窗口二（无人）在真车载端与真 slots-simulator 上的整窗彩排。8005-agv-program#20。

**动作、checkpoint、现场记录、finalize 四样全是上车的那一份。**动作来自 `scripts/field/FieldOperator.psm1`，
以 `Local` 传输跑在两个 loopback 端口上；采集器是 `Invoke-SlotConvergenceFieldWindow.ps1 -WindowId FW-FL2`，
checkpoint 喂的是场景只读连接 `VACUUM INTO` 出来的一致快照（与 real-onboard-field-window-rehearsal 同一个做法）。

编排与现场驱动脚本 `Invoke-FullLoopFieldDrive.ps1` 相同：

1. 第一趟旅程，四需求：停靠 1 没人扫码等站点期限（T，决策 7），停靠 2 扫码前取消（X），停靠 3、4 照常装，
   关卡上第一个开的仓关门不取空两轮再取空（NE）。T、X 都排在第一条装载提交之前，持货期限算不到它们头上。
2. 旅程 Completed 之后重启服务端，车停着（R1，缺陷 20260908 的发现条件）。
3. 电量掉到触发线下，车自己去 211 充电、到恢复线释放（CH），接着受理第二趟：两需求照常装、关卡照常卸。
4. 第二趟 Completed 之后再重启一次（R2）。

与现场只差四处，都写进判据表而不是藏起来：

1. 站点期限一分钟（现场五分钟），采集器按 `-SublotWaitMinutes 1` 判，SUMMARY 会写明「只能算彩排」。
2. 充电线就是出厂的 20 / 80，电量由本场景替 RIoT 写；现场是临时抬线、真车真电。
3. 车由本场景替 RIoT 开到站、开到桩。
4. 服务端「重启」是杀进程再起，现场是 `Stop-Service` 之后由 `Set-JourneyRuntime.ps1 -Off` 重启服务。
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

$sublotWaitMinutes = 1
$chargerStationId = 211
$collector = Join-Path $Context.Repository 'scripts/field/Invoke-SlotConvergenceFieldWindow.ps1'
$windowRoot = Join-Path (Split-Path -Parent $Context.SnapshotRoot) 'field-window'
$checkpointStage = Join-Path $Context.StageRoot 'field-checkpoints'
$null = New-Item -ItemType Directory -Path $checkpointStage -Force

function Publish-Demands([string[]]$areas, [string]$tag) {
    foreach ($area in $areas) {
        $wire = [guid]::NewGuid().ToString('N')
        $sublot = "L2-FW2-$tag-$area-$($Context.RunId)"
        $journal.Note("Publishing demand $sublot in area $area.")
        $null = $mes.Command('Put', "demands/$wire", @{
            sublot = $sublot; area = $area; eqp = "EQP-L2-$area"; package = 'L2-PACKAGE'; maxBoxCount = 4
        })
    }
}

$field = New-FieldOperator -Connection $connection -AgvId $Context.AgvId `
    -SimulatorPort $Context.SimulatorHttpPort -AutomationPort $Context.OnboardAutomationPort `
    -Log { param($message) $journal.Note("field-operator: $message") }

# --- helpers ----------------------------------------------------------------------------------------

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
    $copy = Save-DatabaseSnapshot $label
    $output = & pwsh -NoProfile -File $collector -EvidenceRoot $windowRoot -Checkpoint $label -WindowId FW-FL2 `
        -DatabaseSnapshot $copy -HostDirectory $Context.HostDirectory 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Collector checkpoint '$label' failed (exit $LASTEXITCODE): $($output | Out-String)" }
    $journal.Note("Collector checkpoint $label taken.")
    # Deliberately writes to the pipeline, like the SC1 rehearsal: the acts must discard callback output.
    "FW-FL2 checkpoint $label (rehearsal callback output)"
}
$script:checkpointCount = 0
$onCheckpoint = { param($label) Invoke-WindowCheckpoint $label }

function Move-Vehicle([string]$upperId, [int]$stationRiotId, [hashtable]$arrival = @{}) {
    $null = $riot.Command('Put', "orders/$upperId", @{ orderState = 3; executeVehicleKey = $Context.VehicleKey })
    $null = $riot.Command('Put', 'vehicle', @{
        vehicleKey = $Context.VehicleKey; procState = 'RUNNING'; movementState = 'MT_RUNNING'
        speed = 0.8; processingOrder = $true; orderTaskId = $upperId
    })
    $standing = @{
        vehicleKey = $Context.VehicleKey; procState = 'IDLE'; movementState = 'MT_FINISHED'; speed = 0
        currentPosition = $stationRiotId; processingOrder = $false; clearOrderTaskId = $true
    }
    foreach ($key in $arrival.Keys) { $standing[$key] = $arrival[$key] }
    $null = $riot.Command('Put', 'vehicle', $standing)
    $null = $riot.Command('Put', "orders/$upperId", @{ orderState = 5 })
}

function Invoke-DriveToStop([string]$journeyId, [int]$sequence) {
    # CONFIRMED only: before the create is confirmed the fake RIoT does not know the UpperId (README item 5).
    $script:driveJourney = $journeyId
    $script:driveSequence = $sequence
    $order = Wait-L2Condition -Description "stop $sequence has a confirmed movement order" `
        -Journal $journal -Criterion "stop-$sequence-order" -TimeoutSeconds 180 `
        -Probe {
            $rows = Invoke-L2Query -Connection $connection -Sql (
                'SELECT o.UpperId, s.StationRiotId FROM JourneyStops s JOIN OrderIntents o ON o.MovementLegId = s.MovementLegId ' +
                "WHERE s.JourneyId = '$script:driveJourney' AND s.Sequence = $script:driveSequence AND o.Status = 'CONFIRMED'")
            ($rows.Count -gt 0) ? $rows[0] : $null
        } -Until { param($v) $v }
    $journal.Note("Vehicle drives to stop $sequence of journey $journeyId (RIoT station $($order.StationRiotId)).")
    Move-Vehicle ([string]$order.UpperId) ([int]$order.StationRiotId)
}

function Get-GateSequence([string]$journeyId) {
    return [int](Invoke-L2Query -Connection $connection `
        -Sql "SELECT Sequence FROM JourneyStops WHERE JourneyId = '$journeyId' AND Role = 'GATE'")[0].Sequence
}

function Invoke-Restart([string]$afterJourneyId, [string]$checkpoint) {
    $before = Get-FieldSession -Field $field
    $startedAt = & $Context.RestartServer
    $observed = Wait-FieldSessionAfterRestart -Field $field -GenerationBefore ([long]$before.SessionGeneration) -TimeoutSeconds 180 `
        -OnCheckpoint $onCheckpoint -ReadyCheckpoint $checkpoint
    $observed | Add-Member -NotePropertyName ServerStartedAt -NotePropertyValue $startedAt.ToString('o')
    $observed | Add-Member -NotePropertyName AfterJourneyId -NotePropertyValue $afterJourneyId
    return $observed
}

$restarts = [System.Collections.Generic.List[object]]::new()

# --- 1. 第一趟：T、X、照常装、NE ---------------------------------------------------------------------------

Publish-Demands @('N1-3', 'N2-6', 'N3-4', 'N4-2') 'J1'
$journey = Wait-L2Condition -Description 'the first journey was dispatched' -Journal $journal `
    -Criterion 'journey-1-dispatched' -TimeoutSeconds 120 `
    -Probe { Get-FieldJourney -Field $field } -Until { param($v) $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journey1 = [string]$journey.JourneyId
Invoke-WindowCheckpoint '00-ready' | Out-Null

Invoke-DriveToStop $journey1 1
$stopCount = Wait-L2Condition -Description 'all four demands joined the first journey' -Journal $journal `
    -Criterion 'journey-1-stops' -TimeoutSeconds 120 `
    -Probe { (Invoke-L2Query -Connection $connection -Sql "SELECT COUNT(*) AS N FROM JourneyStops WHERE JourneyId = '$journey1'")[0].N } `
    -Until { param($v) [int]$v -ge 5 }
$assertions.Add('L2-FW2-01', '四条需求凑成第一趟旅程：四个取货停靠加一个关卡', ([int]$stopCount -eq 5), 5, [int]$stopCount)

$actT = Invoke-FieldActNoSublot -Field $field -JourneyId $journey1 -Sequence 1 -OnCheckpoint $onCheckpoint
$assertions.Add('L2-FW2-10', '停靠 1 没人扫码：站点期限到期后需求被结算并抑制，旅程自己离站',
    ($actT.Status -eq 'Cancelled' -and $actT.Suppression -eq 'CANCELLED_BY_STATION_TIMEOUT' -and $actT.PositionAfter -ne '1/AwaitingSublot'),
    'Cancelled / CANCELLED_BY_STATION_TIMEOUT / 离站', "$($actT.Status) / $($actT.Suppression) / $($actT.PositionAfter)")

Invoke-DriveToStop $journey1 2
$actX = Invoke-FieldActCancelBeforeSublot -Field $field -JourneyId $journey1 -Sequence 2 -OnCheckpoint $onCheckpoint
$assertions.Add('L2-FW2-11', '停靠 2 扫码前取消：需求 Cancelled 并以 CANCELLED_BY_OPERATOR 抑制，旅程自己离站',
    ($actX.Status -eq 'Cancelled' -and $actX.Suppression -eq 'CANCELLED_BY_OPERATOR' -and $actX.PositionAfter -ne '2/AwaitingSublot'),
    'Cancelled / CANCELLED_BY_OPERATOR / 离站', "$($actX.Status) / $($actX.Suppression) / $($actX.PositionAfter)（面答 $($actX.FaceAnswer)）")

foreach ($sequence in 3, 4) {
    Invoke-DriveToStop $journey1 $sequence
    $load = Invoke-FieldActLoad -Field $field -JourneyId $journey1 -Sequence $sequence
    $assertions.Add("L2-FW2-1$($sequence + 1)", "第一趟停靠 ${sequence}：照常装载提交", ($load.Status -eq 'Committed'), 'Committed', $load.Status)
}

Invoke-DriveToStop $journey1 (Get-GateSequence $journey1)
$unload1 = Invoke-FieldActUnload -Field $field -JourneyId $journey1 -TimeoutSeconds 600 -NotEmptiedRounds 2 -OnCheckpoint $onCheckpoint
$assertions.Add('L2-FW2-16', '第一趟关卡：第一个开的仓关门不取空两轮都换来重开，之后两条卸货提交、旅程 Completed',
    ($unload1.NotEmptied -and $unload1.NotEmptied.Rounds -eq 2 -and @($unload1.Operations.Values).Count -eq 2 -and
        @($unload1.Operations.Values | Where-Object { $_ -ne 'Committed' }).Count -eq 0),
    '2 轮 / 2 条 Committed', "$($unload1.NotEmptied ? $unload1.NotEmptied.Rounds : 0) 轮 / $(@($unload1.Operations.Values) -join ',')")
$journey1CompletedAt = [DateTimeOffset]::UtcNow

# --- 2. R1：车停在关卡，服务端重启 ----------------------------------------------------------------------------

$restarts.Add((Invoke-Restart $journey1 'r1-ready'))

# --- 3. CH：电量掉到线下，去 211 充电，到恢复线释放 -------------------------------------------------------------

$journal.Note('Battery drops below the trigger level.')
$null = $riot.Command('Put', 'vehicle', @{ vehicleKey = $Context.VehicleKey; battery = 15; batteryState = 'NO_CHARGE' })
$charging = Wait-FieldChargingStage -Field $field -Stage AwaitingChargerArrival -After $journey1CompletedAt -TimeoutSeconds 180 `
    -OnCheckpoint $onCheckpoint -Checkpoint 'charge-dispatched'

$chargerOrder = Wait-L2Condition -Description 'the TO_CHARGER order was confirmed' -Journal $journal `
    -Criterion 'to-charger-intent' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql "SELECT UpperId, Status FROM OrderIntents WHERE UpperId = '$($charging.UpperId)'"
        ($rows.Count -gt 0) ? $rows[0] : $null
    } -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
$journal.Note('Vehicle drives to the charger and starts drawing current.')
Move-Vehicle ([string]$chargerOrder.UpperId) $chargerStationId @{ battery = 22; batteryState = 'CHARGING' }
$charging = Wait-FieldChargingStage -Field $field -Stage Charging -Tracker $charging -TimeoutSeconds 180 `
    -OnCheckpoint $onCheckpoint -Checkpoint 'charge-charging'

# The next journey's demands arrive while the vehicle charges; none may be taken before the resume level.
Publish-Demands @('N1-3', 'N2-6') 'J2'
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$early = Get-FieldJourney -Field $field
$assertions.Add('L2-FW2-20', '充电未到恢复线时不受理第二趟的需求', ($null -eq $early), '(无未完成旅程)', ($early ? "$($early.JourneyId) $($early.Stage)" : '(无未完成旅程)'))

$null = $riot.Command('Put', 'vehicle', @{ vehicleKey = $Context.VehicleKey; battery = 80; batteryState = 'CHARGING' })
$charging = Wait-FieldChargingStage -Field $field -Stage Completed -Tracker $charging -TimeoutSeconds 180 `
    -OnCheckpoint $onCheckpoint -Checkpoint 'charge-released'

# --- 4. 第二趟：照常装、照常卸 -------------------------------------------------------------------------------

$journey = Wait-L2Condition -Description 'the second journey was accepted once the vehicle was released' -Journal $journal `
    -Criterion 'journey-2-dispatched' -TimeoutSeconds 120 `
    -Probe { Get-FieldJourney -Field $field } -Until { param($v) $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journey2 = [string]$journey.JourneyId
$assertions.Add('L2-FW2-21', '释放之后接着受理第二趟', ($journey2 -ne $journey1), '新旅程', $journey2)

Invoke-DriveToStop $journey2 1
$null = Wait-L2Condition -Description 'both demands joined the second journey' -Journal $journal `
    -Criterion 'journey-2-stops' -TimeoutSeconds 120 `
    -Probe { (Invoke-L2Query -Connection $connection -Sql "SELECT COUNT(*) AS N FROM JourneyStops WHERE JourneyId = '$journey2'")[0].N } `
    -Until { param($v) [int]$v -ge 3 }
foreach ($sequence in 1, 2) {
    if ($sequence -gt 1) { Invoke-DriveToStop $journey2 $sequence }
    $load = Invoke-FieldActLoad -Field $field -JourneyId $journey2 -Sequence $sequence
    $assertions.Add("L2-FW2-2$($sequence + 1)", "第二趟停靠 ${sequence}：照常装载提交", ($load.Status -eq 'Committed'), 'Committed', $load.Status)
}
Invoke-DriveToStop $journey2 (Get-GateSequence $journey2)
$unload2 = Invoke-FieldActUnload -Field $field -JourneyId $journey2 -TimeoutSeconds 600
$assertions.Add('L2-FW2-24', '第二趟关卡：两条卸货提交、旅程 Completed',
    (@($unload2.Operations.Values).Count -eq 2 -and @($unload2.Operations.Values | Where-Object { $_ -ne 'Committed' }).Count -eq 0),
    '2 条 Committed', (@($unload2.Operations.Values) -join ','))

# --- 5. R2，现场记录与 finalize ---------------------------------------------------------------------------------

$restarts.Add((Invoke-Restart $journey2 'r2-ready'))

$record = New-FullLoopWindowRecord -AgvId $Context.AgvId -IoModule "127.0.0.1:$($Context.SimulatorModbusPort)" `
    -DriverRunId $Context.RunId -Site 'L2 真装置彩排（本机）' `
    -Journeys @(@{ journeyId = $journey1; role = 'T/X/NE' }, @{ journeyId = $journey2; role = '充电之后' }) `
    -ActT $actT -ActX $actX -NotEmptied $unload1.NotEmptied -NotEmptiedCheckpoint $unload1.NotEmptiedCheckpoint `
    -Charging $charging -Restarts $restarts.ToArray()
$recordPath = Join-Path $checkpointStage 'field-record.json'
[IO.File]::WriteAllText($recordPath, ($record | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))

$finalCopy = Save-DatabaseSnapshot 'finalize'
$finalOutput = & pwsh -NoProfile -File $collector -EvidenceRoot $windowRoot -Finalize -RecordPath $recordPath -WindowId FW-FL2 `
    -DatabaseSnapshot $finalCopy -HostDirectory $Context.HostDirectory -SublotWaitMinutes $sublotWaitMinutes 2>&1
$finalExit = $LASTEXITCODE
$journal.Note("Collector finalize exit $finalExit`: $(($finalOutput | Out-String).Trim())")

$judged = Get-Content -LiteralPath (Join-Path $windowRoot 'assertions.json') -Raw | ConvertFrom-Json
foreach ($item in @($judged.assertions)) {
    $assertions.Add("L2-FW2-$($item.id)", "采集器：$($item.description)", ($item.outcome -eq 'PASS'),
        (($item.expected | ConvertTo-Json -Compress -Depth 4) ?? ''), (($item.actual | ConvertTo-Json -Compress -Depth 4) ?? ''))
}
$assertions.Add('L2-FW2-50', '采集器在驱动写出的记录上 finalize，整窗 PASS',
    ($finalExit -eq 0 -and [string]$judged.outcome -eq 'PASS'), 'exit 0 / PASS', "exit $finalExit / $($judged.outcome)")

$journal.Note('Scenario finished.')
