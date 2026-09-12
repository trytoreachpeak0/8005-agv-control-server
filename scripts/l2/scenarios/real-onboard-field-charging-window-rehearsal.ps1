#Requires -Version 7

<#
现场窗口二充电短窗口（`Invoke-FullLoopFieldDrive.ps1 -ChargingOnly`）在真车载端与真 slots-simulator 上的彩排。
8005-agv-program#20。

续跑窗口里 S52、T、X、NE、R 都已成立，只剩 CH 被 #53 挡住。#53 修好之后，本票只欠一个只演充电的短窗口：
没有未完成旅程、电量低于触发线 → 车自己去 211 充电 → 到恢复线释放 → 接着受理下一趟、照常装卸 →
那一趟 Completed 之后重启服务端。T、X、NE 不重演，免得为已经成立的结论再永久抑制两条真实需求。

**本场景要证明的是这份短窗口记录能被采集器按「本窗只欠 CH、N、R」判完**：现场记录带 `scenesOwed`，
finalize 不再把没演的 T、X、NE 判成红，而欠下的 CH、N、R 一条也不能少。动作、checkpoint、现场记录、
finalize 四样都是上车的那一份。

与现场只差三处，都写进判据表而不是藏起来：

1. 充电线就是出厂的 30 / 80，电量由本场景替 RIoT 写；现场是临时抬线、真车真电。
2. 车由本场景替 RIoT 开到站、开到桩。
3. 服务端「重启」是杀进程再起，现场是 `Stop-Service` 之后由 `Set-JourneyRuntime.ps1 -Off` 重启服务。
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

$chargerStationId = 211
$collector = Join-Path $Context.Repository 'scripts/field/Invoke-SlotConvergenceFieldWindow.ps1'
$windowRoot = Join-Path (Split-Path -Parent $Context.SnapshotRoot) 'field-window'
$checkpointStage = Join-Path $Context.StageRoot 'field-checkpoints'
$null = New-Item -ItemType Directory -Path $checkpointStage -Force

function Publish-Demands([string[]]$areas, [string]$tag) {
    foreach ($area in $areas) {
        $wire = [guid]::NewGuid().ToString('N')
        $sublot = "L2-FWC-$tag-$area-$($Context.RunId)"
        $journal.Note("Publishing demand $sublot in area $area.")
        $null = $mes.Command('Put', "demands/$wire", @{
            sublot = $sublot; area = $area; eqp = "EQP-L2-$area"; package = 'L2-PACKAGE'; maxBoxCount = 4
        })
    }
}

$field = New-FieldOperator -Connection $connection -AgvId $Context.AgvId `
    -SimulatorPort $Context.SimulatorHttpPort -AutomationPort $Context.OnboardAutomationPort `
    -Log { param($message) $journal.Note("field-operator: $message") }

# --- helpers (the same as real-onboard-field-window2-rehearsal) ---------------------------------------

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
    # Deliberately writes to the pipeline, like the other rehearsals: the acts must discard callback output.
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

# --- 0. 开窗：会话 Ready、没有未完成旅程，与驱动脚本 -ChargingOnly 的预检同一个前提 --------------------------

$null = Wait-L2Condition -Description 'the onboard session is Ready' -Journal $journal -Criterion 'session-ready' -TimeoutSeconds 120 `
    -Probe { Get-FieldSession -Field $field } -Until { param($v) $v -and [string]$v.Readiness -eq 'Ready' }
$idle = Get-FieldJourney -Field $field
$assertions.Add('L2-FWC-01', '开窗时没有未完成旅程，充电幕不是插在两趟之间而是开窗第一幕', ($null -eq $idle), '(无未完成旅程)',
    ($idle ? "$($idle.JourneyId) $($idle.Stage)" : '(无未完成旅程)'))
$windowStartedAt = [DateTimeOffset]::UtcNow
Invoke-WindowCheckpoint '00-ready' | Out-Null

# --- 1. CH：电量低于触发线，去 211 充电，到恢复线释放 ---------------------------------------------------------

$journal.Note('Battery drops below the trigger level.')
$null = $riot.Command('Put', 'vehicle', @{ vehicleKey = $Context.VehicleKey; battery = 15; batteryState = 'NO_CHARGE' })
$charging = Wait-FieldChargingStage -Field $field -Stage AwaitingChargerArrival -After $windowStartedAt -TimeoutSeconds 180 `
    -OnCheckpoint $onCheckpoint -Checkpoint 'charge-dispatched'

$chargerOrder = Wait-L2Condition -Description 'the TO_CHARGER order was confirmed' -Journal $journal `
    -Criterion 'to-charger-intent' -TimeoutSeconds 120 `
    -Probe {
        $rows = Invoke-L2Query -Connection $connection -Sql "SELECT UpperId, Status FROM OrderIntents WHERE UpperId = '$($charging.UpperId)'"
        ($rows.Count -gt 0) ? $rows[0] : $null
    } -Until { param($v) $v -and [string]$v.Status -eq 'CONFIRMED' }
$journal.Note('Vehicle drives to the charger; the order''s start-charging action engages it.')
# No batteryState here: the fake RIoT reports CHARGING only when an order carrying act(78, 1, 0) completes.
Move-Vehicle ([string]$chargerOrder.UpperId) $chargerStationId @{ battery = 22 }
$chargingVehicle = @($riot.Snapshot().body.vehicles | Where-Object { $_.deviceKey -eq $Context.VehicleKey })
$assertions.Add('L2-FWC-02', '充电单完成后假 RIoT 自己报 CHARGING（单里带开始充电动作）',
    ($chargingVehicle.Count -eq 1 -and [string]$chargingVehicle[0].batteryState -eq 'CHARGING'),
    'CHARGING', ($chargingVehicle.Count -eq 1 ? [string]$chargingVehicle[0].batteryState : '(no vehicle)'))
$charging = Wait-FieldChargingStage -Field $field -Stage Charging -Tracker $charging -TimeoutSeconds 180 `
    -OnCheckpoint $onCheckpoint -Checkpoint 'charge-charging'

# The next journey's demands arrive while the vehicle charges; none may be taken before the resume level.
Publish-Demands @('N1-3', 'N2-6') 'J'
$null = Wait-L2Iterations -Riot $riot -Count 4 -Journal $journal
$early = Get-FieldJourney -Field $field
$assertions.Add('L2-FWC-03', '充电未到恢复线时不受理需求', ($null -eq $early), '(无未完成旅程)', ($early ? "$($early.JourneyId) $($early.Stage)" : '(无未完成旅程)'))

$null = $riot.Command('Put', 'vehicle', @{ vehicleKey = $Context.VehicleKey; battery = 80 })
$charging = Wait-FieldChargingStage -Field $field -Stage Completed -Tracker $charging -TimeoutSeconds 180 `
    -OnCheckpoint $onCheckpoint -Checkpoint 'charge-released'

# --- 2. 释放之后的那一趟：照常装、照常卸 -----------------------------------------------------------------------

$journey = Wait-L2Condition -Description 'a journey was accepted once the vehicle was released' -Journal $journal `
    -Criterion 'journey-dispatched' -TimeoutSeconds 120 `
    -Probe { Get-FieldJourney -Field $field } -Until { param($v) $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$journeyId = [string]$journey.JourneyId

Invoke-DriveToStop $journeyId 1
$null = Wait-L2Condition -Description 'both demands joined the journey' -Journal $journal `
    -Criterion 'journey-stops' -TimeoutSeconds 120 `
    -Probe { (Invoke-L2Query -Connection $connection -Sql "SELECT COUNT(*) AS N FROM JourneyStops WHERE JourneyId = '$journeyId'")[0].N } `
    -Until { param($v) [int]$v -ge 3 }
foreach ($sequence in 1, 2) {
    if ($sequence -gt 1) { Invoke-DriveToStop $journeyId $sequence }
    $load = Invoke-FieldActLoad -Field $field -JourneyId $journeyId -Sequence $sequence
    $assertions.Add("L2-FWC-1$sequence", "停靠 ${sequence}：照常装载提交", ($load.Status -eq 'Committed'), 'Committed', $load.Status)
}
Invoke-DriveToStop $journeyId (Get-GateSequence $journeyId)
$unload = Invoke-FieldActUnload -Field $field -JourneyId $journeyId -TimeoutSeconds 600
$assertions.Add('L2-FWC-13', '关卡：两条卸货提交、旅程 Completed',
    (@($unload.Operations.Values).Count -eq 2 -and @($unload.Operations.Values | Where-Object { $_ -ne 'Committed' }).Count -eq 0),
    '2 条 Committed', (@($unload.Operations.Values) -join ','))

# --- 3. 车停着时重启服务端，现场记录与 finalize -------------------------------------------------------------------

$before = Get-FieldSession -Field $field
$startedAt = & $Context.RestartServer
$restart = Wait-FieldSessionAfterRestart -Field $field -GenerationBefore ([long]$before.SessionGeneration) -TimeoutSeconds 180 `
    -OnCheckpoint $onCheckpoint -ReadyCheckpoint 'r1-ready'
$restart | Add-Member -NotePropertyName ServerStartedAt -NotePropertyValue $startedAt.ToString('o')
$restart | Add-Member -NotePropertyName AfterJourneyId -NotePropertyValue $journeyId

$record = New-FullLoopWindowRecord -AgvId $Context.AgvId -IoModule "127.0.0.1:$($Context.SimulatorModbusPort)" `
    -DriverRunId $Context.RunId -Site 'L2 真装置彩排（本机）' `
    -Journeys @(@{ journeyId = $journeyId; role = '充电之后' }) `
    -Charging $charging -Restarts @($restart) -ScenesOwed CH, N, R
$recordPath = Join-Path $checkpointStage 'field-record.json'
[IO.File]::WriteAllText($recordPath, ($record | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))

$finalCopy = Save-DatabaseSnapshot 'finalize'
$finalOutput = & pwsh -NoProfile -File $collector -EvidenceRoot $windowRoot -Finalize -RecordPath $recordPath -WindowId FW-FL2 `
    -DatabaseSnapshot $finalCopy -HostDirectory $Context.HostDirectory 2>&1
$finalExit = $LASTEXITCODE
$journal.Note("Collector finalize exit $finalExit`: $(($finalOutput | Out-String).Trim())")

$judged = Get-Content -LiteralPath (Join-Path $windowRoot 'assertions.json') -Raw | ConvertFrom-Json
foreach ($item in @($judged.assertions)) {
    $assertions.Add("L2-FWC-$($item.id)", "采集器：$($item.description)", ($item.outcome -eq 'PASS'),
        (($item.expected | ConvertTo-Json -Compress -Depth 4) ?? ''), (($item.actual | ConvertTo-Json -Compress -Depth 4) ?? ''))
}
$ids = @($judged.assertions | ForEach-Object { [string]$_.id })
$notOwed = @($ids | Where-Object { $_ -like 'FL2-T-*' -or $_ -like 'FL2-X-*' -or $_ -like 'FL2-NE-*' })
$assertions.Add('L2-FWC-40', '本窗不欠的 T、X、NE 在采集器判据表里一行都没有——既不算演到，也不算没演到',
    ($notOwed.Count -eq 0), '(无)', ($notOwed.Count ? ($notOwed -join ',') : '(无)'))
$owedCharging = @('FL2-CH-01', 'FL2-CH-02', 'FL2-CH-03', 'FL2-CH-04', 'FL2-CH-05') | Where-Object { $ids -notcontains $_ }
$assertions.Add('L2-FWC-41', '本窗欠的 CH 判据一条不少：FL2-CH-01..05 都在判据表里（CH-06 只在读到生产配置时判）',
    (@($owedCharging).Count -eq 0), '(无缺失)', (@($owedCharging).Count ? (@($owedCharging) -join ',') : '(无缺失)'))
$assertions.Add('L2-FWC-50', '采集器在充电短窗口的记录上 finalize，整窗 PASS',
    ($finalExit -eq 0 -and [string]$judged.outcome -eq 'PASS'), 'exit 0 / PASS', "exit $finalExit / $($judged.outcome)")

$journal.Note('Scenario finished.')
