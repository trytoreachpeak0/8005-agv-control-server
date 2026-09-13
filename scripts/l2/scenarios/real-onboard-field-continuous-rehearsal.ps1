#Requires -Version 7

<#
连续驱动（`scripts/field/Invoke-ContinuousFieldDrive.ps1`）在真车载端与真 slots-simulator 上的彩排。

现场窗口的驱动脚本演的是「一扇窗」：固定两趟、在指定站点上演指定幕。连续驱动演的是「一个一直在跑的厂」：
闸门开着，引擎自己一趟接一趟地派，驱动只管把每一趟照常装完、卸完，再等下一趟。这条场景证明两件事：

1. **一趟接一趟。**第一趟两个取货站；它完成后电量掉到触发线下，车自己去 211 充电，驱动在充电期间空转；
   释放之后引擎受理第二趟，驱动接着开完。整段是**一次** `Invoke-FieldContinuousDrive -MaxJourneys 2` 调用。
2. **驱动自己死过一次也接得上。**第三趟在停靠 1 由场景亲手扫码、让车等在第一个仓（等于驱动扫完码就死了），
   再起一次连续驱动：它必须从 `AwaitingLoadResult` 接手那次装货，把旅程开完。

动作全是 `FieldOperator.psm1` 里上车的那一份，以 `Local` 传输跑在两个 loopback 端口上。与现场只差三处：

1. 车由本场景替 RIoT 开到站、开到桩（`-OnPoll` 钩子里做，现场这个钩子只写心跳）。
2. 电量由本场景替 RIoT 写；现场是真车真电。
3. 需求由本场景发布到假 MesIngest；现场是 MES 里真实的 WIRE_TO_GATE 需求。
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

function Publish-Demands([string[]]$areas, [string]$tag) {
    foreach ($area in $areas) {
        $wire = [guid]::NewGuid().ToString('N')
        $sublot = "L2-CFD-$tag-$area-$($Context.RunId)"
        $journal.Note("Publishing demand $sublot in area $area.")
        $null = $mes.Command('Put', "demands/$wire", @{
            sublot = $sublot; area = $area; eqp = "EQP-L2-$area"; package = 'L2-PACKAGE'; maxBoxCount = 4
        })
    }
}

$field = New-FieldOperator -Connection $connection -AgvId $Context.AgvId `
    -SimulatorPort $Context.SimulatorHttpPort -AutomationPort $Context.OnboardAutomationPort `
    -Log { param($message) $journal.Note("field-operator: $message") }

# --- the rig: what RIoT and the battery do in the field ---------------------------------------------------

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

$script:moved = @{}
$script:batteryRaisedFor = @{}

<#
Called by the drive between observations. With a journey: when it waits to arrive at its current stop and that
stop's movement order is CONFIRMED (the fake RIoT does not know an UpperId before, README item 5), the vehicle
drives there. Idle: a charging run's confirmed order drives the vehicle to the charger, and once the run is
Charging the battery reaches the resume level.
#>
function Invoke-RigTick($journey) {
    if ($journey) {
        if ([string]$journey.Stage -notin 'AwaitingPickupArrival', 'AwaitingGateArrival') { return }
        $rows = Invoke-L2Query -Connection $connection -Sql (
            'SELECT o.UpperId, s.StationRiotId FROM JourneyStops s JOIN OrderIntents o ON o.MovementLegId = s.MovementLegId ' +
            "WHERE s.JourneyId = '$($journey.JourneyId)' AND s.Sequence = $([int]$journey.CurrentStopSequence) AND o.Status = 'CONFIRMED'")
        foreach ($row in @($rows)) {
            if ($script:moved.ContainsKey([string]$row.UpperId)) { continue }
            $script:moved[[string]$row.UpperId] = $true
            $journal.Note("Vehicle drives to stop $($journey.CurrentStopSequence) of journey $($journey.JourneyId) (RIoT station $($row.StationRiotId)).")
            Move-Vehicle ([string]$row.UpperId) ([int]$row.StationRiotId)
        }
        return
    }
    $runs = Invoke-L2Query -Connection $connection -Sql (
        'SELECT r.ChargingRunId, r.Stage, r.UpperId, o.Status FROM AutoChargingRuns r ' +
        "LEFT JOIN OrderIntents o ON o.UpperId = r.UpperId WHERE r.Stage <> 'Completed'")
    foreach ($run in @($runs)) {
        $upperId = [string]$run.UpperId
        if ([string]$run.Stage -eq 'AwaitingChargerArrival' -and [string]$run.Status -eq 'CONFIRMED' -and -not $script:moved.ContainsKey($upperId)) {
            $script:moved[$upperId] = $true
            $journal.Note("Vehicle drives to the charger for charging run $($run.ChargingRunId).")
            # No batteryState: the fake RIoT reports CHARGING only when an order carrying act(78, 1, 0) completes.
            Move-Vehicle $upperId $chargerStationId @{ battery = 22 }
        }
        if ([string]$run.Stage -eq 'Charging' -and -not $script:batteryRaisedFor.ContainsKey([string]$run.ChargingRunId)) {
            $script:batteryRaisedFor[[string]$run.ChargingRunId] = $true
            $journal.Note("Battery reaches the resume level for charging run $($run.ChargingRunId).")
            $null = $riot.Command('Put', 'vehicle', @{ vehicleKey = $Context.VehicleKey; battery = 80 })
        }
    }
}

$script:records = [System.Collections.Generic.List[object]]::new()
$script:journey1CompletedAt = $null

function Invoke-AfterJourney($record) {
    $script:records.Add($record)
    $journal.Note("Drive reported journey $($record.JourneyId): $($record.Outcome) after $($record.Attempts) attempt(s).")
    if ($script:records.Count -eq 1 -and $record.Outcome -eq 'Completed') {
        $script:journey1CompletedAt = [DateTimeOffset]::UtcNow
        # Battery first, demand second: the engine must see the low battery before it could accept the demand.
        $journal.Note('Battery drops below the trigger level; the next demand arrives.')
        $null = $riot.Command('Put', 'vehicle', @{ vehicleKey = $Context.VehicleKey; battery = 15; batteryState = 'NO_CHARGE' })
        Publish-Demands @('N3-4') 'J2'
    }
}

function Get-Loads($record) { return @($record.Acts | Where-Object { $_.Act -in 'N', 'N-RESUMED' }) }
function Get-UnloadStatuses($record) { return @($record.Acts | Where-Object { $_.Act -eq 'UNLOAD' } | ForEach-Object { @($_.Operations.Values) }) }

# --- 0. 开跑：会话 Ready、没有未完成旅程 ---------------------------------------------------------------------

$sessionBefore = Wait-L2Condition -Description 'the onboard session is Ready' -Journal $journal -Criterion 'session-ready' -TimeoutSeconds 120 `
    -Probe { Get-FieldSession -Field $field } -Until { param($v) $v -and [string]$v.Readiness -eq 'Ready' }
$idle = Get-FieldJourney -Field $field
$assertions.Add('L2-CFD-01', '开跑时会话 Ready、没有未完成旅程', ($null -eq $idle), '(无未完成旅程)',
    ($idle ? "$($idle.JourneyId) $($idle.Stage)" : '(无未完成旅程)'))

# --- 1. 一次连续驱动开两趟，中间自己去充电 ------------------------------------------------------------------------

Publish-Demands @('N1-3', 'N2-6') 'J1'
$script:phaseDeadline = [DateTimeOffset]::UtcNow.AddMinutes(25)
$phaseA = Invoke-FieldContinuousDrive -Field $field -MaxJourneys 2 -IdlePollSeconds 1 -DrivePollSeconds 1 -ResumeDelaySeconds 2 `
    -ShouldStop { [DateTimeOffset]::UtcNow -gt $script:phaseDeadline } `
    -OnPoll { param($journey) Invoke-RigTick $journey } `
    -OnJourney { param($record) Invoke-AfterJourney $record }

$recordsA = @($script:records)
$assertions.Add('L2-CFD-02', '一次 Invoke-FieldContinuousDrive 调用开完两趟，两趟都 Completed、都不需要续跑',
    ($phaseA -eq 2 -and $recordsA.Count -eq 2 -and @($recordsA | Where-Object { $_.Outcome -ne 'Completed' -or $_.Attempts -ne 1 }).Count -eq 0),
    '2 趟 / Completed / 1 次', "$phaseA 趟 / $(@($recordsA | ForEach-Object { "$($_.Outcome)×$($_.Attempts)" }) -join ',')")

$loadsA = @($recordsA | ForEach-Object { Get-Loads $_ })
$assertions.Add('L2-CFD-03', '两趟共三次装货全部 Committed（第一趟两站、第二趟一站）',
    ($loadsA.Count -eq 3 -and @($loadsA | Where-Object { $_.Status -ne 'Committed' }).Count -eq 0),
    '3 条 Committed', (@($loadsA | ForEach-Object { "$($_.Sequence):$($_.Status)" }) -join ','))

$unloadsA = @($recordsA | ForEach-Object { Get-UnloadStatuses $_ })
$assertions.Add('L2-CFD-04', '两趟共三次卸货全部 Committed',
    ($unloadsA.Count -eq 3 -and @($unloadsA | Where-Object { $_ -ne 'Committed' }).Count -eq 0),
    '3 条 Committed', ($unloadsA -join ','))

$chargingRuns = @(Invoke-L2Query -Connection $connection -Sql 'SELECT ChargingRunId, Stage, CreatedAt FROM AutoChargingRuns' | ForEach-Object { $_ })
$journey2Row = ($recordsA.Count -ge 2) ? @(Invoke-L2Query -Connection $connection -Sql "SELECT CreatedAt FROM JourneyRuntimes WHERE JourneyId = '$($recordsA[1].JourneyId)'" | ForEach-Object { $_ }) : @()
$completedRun = @($chargingRuns | Where-Object { [string]$_.Stage -eq 'Completed' })
$chargedBetween = $completedRun.Count -eq 1 -and $journey2Row.Count -eq 1 -and $script:journey1CompletedAt -and
    ([datetimeoffset]$completedRun[0].CreatedAt -gt $script:journey1CompletedAt.AddSeconds(-30)) -and
    ([datetimeoffset]$journey2Row[0].CreatedAt -gt [datetimeoffset]$completedRun[0].CreatedAt)
$assertions.Add('L2-CFD-05', '两趟之间车自己去充电并被释放，驱动空转等过了它，第二趟在充电行程之后才建',
    $chargedBetween, '1 次充电 Completed，第二趟晚于它',
    "$($completedRun.Count) 次 Completed / 第二趟 $($journey2Row.Count ? $journey2Row[0].CreatedAt : '(无)')")

# --- 2. 驱动死在装货半路，再起一次接得上 ---------------------------------------------------------------------------

Publish-Demands @('N4-2', 'N1-3') 'J3'
$journey3 = Wait-L2Condition -Description 'the third journey was accepted' -Journal $journal -Criterion 'journey-3-accepted' -TimeoutSeconds 120 `
    -Probe { Get-FieldJourney -Field $field } -Until { param($v) $v -and [string]$v.Stage -eq 'AwaitingPickupArrival' }
$script:journey3Id = [string]$journey3.JourneyId
$null = Wait-L2Condition -Description 'the vehicle reached stop 1 of the third journey' -Journal $journal -Criterion 'journey-3-stop-1' -TimeoutSeconds 180 `
    -Probe { $row = Get-FieldJourney -Field $field -JourneyId $script:journey3Id; Invoke-RigTick $row; $row } `
    -Until { param($v) [string]$v.Stage -eq 'AwaitingSublot' }
$half = Start-FieldStopLoad -Field $field -JourneyId $script:journey3Id -Sequence 1
$journal.Note("Scene: the drive died after scanning $($half.Sublot); the vehicle waits on slot $($half.SlotNo).")
$positionBeforeResume = Get-FieldPosition -Field $field -JourneyId $script:journey3Id

$script:phaseDeadline = [DateTimeOffset]::UtcNow.AddMinutes(15)
$phaseB = Invoke-FieldContinuousDrive -Field $field -MaxJourneys 1 -IdlePollSeconds 1 -DrivePollSeconds 1 -ResumeDelaySeconds 2 `
    -ShouldStop { [DateTimeOffset]::UtcNow -gt $script:phaseDeadline } `
    -OnPoll { param($journey) Invoke-RigTick $journey } `
    -OnJourney { param($record) Invoke-AfterJourney $record }

$record3 = ($script:records.Count -ge 3) ? $script:records[2] : $null
$assertions.Add('L2-CFD-10', '再起的驱动从装货半路接手第三趟（停靠 1 已扫码、车等在仓位上）并开完',
    ($phaseB -eq 1 -and $record3 -and $record3.JourneyId -eq $script:journey3Id -and $record3.Outcome -eq 'Completed' -and $record3.PickedUpAt -eq $positionBeforeResume),
    "1 趟 / 接手于 $positionBeforeResume / Completed",
    "$phaseB 趟 / 接手于 $($record3 ? $record3.PickedUpAt : '(无)') / $($record3 ? $record3.Outcome : '(无)')")

$loads3 = $record3 ? @(Get-Loads $record3) : @()
$resumed = @($loads3 | Where-Object { $_.Act -eq 'N-RESUMED' })
$assertions.Add('L2-CFD-11', '第三趟：停靠 1 那次接手的装货与停靠 2 的照常装货都 Committed',
    ($loads3.Count -eq 2 -and $resumed.Count -eq 1 -and [int]$resumed[0].Sequence -eq 1 -and @($loads3 | Where-Object { $_.Status -ne 'Committed' }).Count -eq 0),
    '1:N-RESUMED:Committed,2:N:Committed', (@($loads3 | ForEach-Object { "$($_.Sequence):$($_.Act):$($_.Status)" }) -join ','))

$unloads3 = $record3 ? @(Get-UnloadStatuses $record3) : @()
$assertions.Add('L2-CFD-12', '第三趟关卡：两条卸货 Committed',
    ($unloads3.Count -eq 2 -and @($unloads3 | Where-Object { $_ -ne 'Committed' }).Count -eq 0), '2 条 Committed', ($unloads3 -join ','))

# --- 3. 收尾 -------------------------------------------------------------------------------------------------

$blocked = @(Invoke-L2Query -Connection $connection -Sql "SELECT JourneyId FROM JourneyRuntimes WHERE Stage = 'Blocked'" | ForEach-Object { $_ })
$sessionAfter = Get-FieldSession -Field $field
$assertions.Add('L2-CFD-20', '全程没有旅程 Blocked，会话一直是开跑时那一代（没有重连）、仍 Ready',
    ($blocked.Count -eq 0 -and $sessionAfter -and [long]$sessionAfter.SessionGeneration -eq [long]$sessionBefore.SessionGeneration -and [string]$sessionAfter.Readiness -eq 'Ready'),
    "0 / generation $($sessionBefore.SessionGeneration) / Ready",
    "$($blocked.Count) / generation $($sessionAfter ? $sessionAfter.SessionGeneration : '(无)') / $($sessionAfter ? $sessionAfter.Readiness : '(无)')")

$journal.Note('Scenario finished.')
