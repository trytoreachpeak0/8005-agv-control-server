#Requires -Version 7
# control-server#798 mutation self-test: each mutation edits exactly one file, runs the one test expected to go red, restores.
param(
    [string] $Worktree = 'C:/work/8005-wt/cs-798-8005-agv-control-server',
    [string] $Out = 'C:/work/8005-scratch/cs798/mutations',
    [string[]] $Only = @())
$ErrorActionPreference = 'Stop'
$Only = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
Set-Location $Worktree
New-Item -ItemType Directory -Force $Out | Out-Null
$r = 'src/ControlServer.Host/Runtime'
$evaluator = "$r/IdleReturn/IdleReturnEvaluator.cs"
$standing = "$r/IdleReturn/FleetStanding.cs"
$commit = 'IdleReturnCommitmentTests'
$clearance = 'ChargingClearanceToWaitingPointTests'
$mutations = @(
    # The shared judgement, its two callers and the clearance path's fleet read.
    @{ Id = 'M1'; File = $evaluator; Class = $clearance; Expect = 'AnIdleVehicleStandingOnTheOnlyPointWithoutARowKeepsTheClearingVehicleOffItUntilItLeaves'
       Old = "        if (standing.Excludes(graph, vehicleKey, point.StationId) is { } stoodOn)"
       New = "        if (standing.Excludes(graph, vehicleKey, point.StationId) is { } stoodOn && origin < 0)" }
    @{ Id = 'M2'; File = "$r/JourneyRuntimeEngine.ClearanceMove.cs"; Class = $clearance; Expect = 'AVehicleOutsideTheIdleFleetStandingOnAPointKeepsTheClearingVehicleOffIt'
       Old = "                        standing,`n"; New = "                        new FleetStanding([], []),`n" }
    @{ Id = 'M3'; File = $evaluator; Class = $commit; Expect = 'AStagingOriginAnotherVehicleStandsOnIsNotTakenAndTheVehicleFallsBackToAWaitingPoint'
       Old = "        if (standing.Excludes(reads.Graph.Graph!, vehicle.VehicleKey, stationId) is { } stoodOn)"
       New = "        if (standing.Excludes(reads.Graph.Graph!, vehicle.VehicleKey, stationId) is { } stoodOn && stationId < 0)" }
    # The fleet, not the vehicles handed in.
    @{ Id = 'M4'; File = $standing; Class = $commit; Expect = 'AVehicleNotHandedToTheEvaluationStandingOnTheOnlyPointKeepsOthersOffIt'
       Old = "await ReadWithinBudgetAsync(vehicle, budget.Token, cancellationToken).ConfigureAwait(false);"; New = "await Task.FromResult<RiotVehicleObservation?>(null).ConfigureAwait(false);" }
    @{ Id = 'M5'; File = $evaluator; Class = 'MultiVehicleExecutionTests'; Expect = 'AVehicleOutsideTheIdleFleetStandingOnTheWaitingPointKeepsTheIdleReturnOffIt'
       Old = ".ReadAsync([.. candidates.Select(item => item.Facts.Vehicle), .. fleetReadThisRound], cancellationToken)"
       New = ".ReadAsync([.. candidates.Select(item => item.Facts.Vehicle), .. fleetReadThisRound], cancellationToken).ContinueWith(task => new FleetStanding([.. task.Result.Vehicles.Where(vehicle => candidates.Any(item => item.Vehicle.VehicleKey == vehicle.VehicleKey))], []), cancellationToken)" }
    @{ Id = 'M6'; File = $standing; Class = $commit; Expect = 'AVehicleNotHandedToTheEvaluationOnAStationAtThePointsNodeKeepsOthersOffThePoint'
       Old = "from == stationId || graph.Traverse(from, stationId) is { Reachable: true, TraversalCostMm: 0 };"; New = "from == stationId;" }
    @{ Id = 'M7'; File = $standing; Class = $commit; Expect = 'AVehicleStandingOnNoPointOfThisMapKeepsNoOneOff'
       Old = "!string.Equals(observed.CurrentMap, _runtime.MapIdentity, StringComparison.Ordinal))"; New = "observed.CurrentMap.Length < 0)" }
    # Unreadable vehicles: the last seen station, the warning, and where the last seen station comes from.
    @{ Id = 'M8'; File = $standing; Class = $commit; Expect = 'AVehicleThatCannotBeReadCountsWhereItWasLastSeen'
       Old = "if (memory.LastSeen(key) is int last)"; New = "if (memory.LastSeen(key) is int last && last < 0)" }
    @{ Id = 'M9'; File = "$r/Dispatch/DispatchRoundRunner.cs"; Class = $clearance; Expect = 'AVehicleThatCannotBeReadCountsWhereItWasLastSeen'
       Old = "        idleReturn.RememberWhereVehiclesStand(participants.Select(p => p.Facts.Vehicle));`n"; New = '' }
    @{ Id = 'M10'; File = $standing; Class = $commit; Expect = 'AVehicleNeverLocatedIsWarnedAboutOnceAndBlocksNoPoint'
       Old = "unlocated.Add(vehicle.AgvId);`n            if (memory.FirstUnlocated(key))"; New = "unlocated.Add(vehicle.AgvId);`n            if (memory.FirstUnlocated(key) && key.Length < 0)" }
    @{ Id = 'M11'; File = $standing; Class = $commit; Expect = 'AVehicleNeverLocatedIsWarnedAboutOnceAndBlocksNoPoint'
       Old = "            return _warned.Add(vehicleKey);"; New = "            _warned.Add(vehicleKey);`n            return true;" }
    @{ Id = 'M12'; File = $standing; Class = $commit; Expect = 'AVehicleNeverLocatedIsWarnedAboutOnceAndBlocksNoPoint'
       Old = "            _warned.Remove(vehicleKey);`n"; New = '' }
    @{ Id = 'M13'; File = $standing; Class = $commit; Expect = 'AVehicleNeverLocatedIsWarnedAboutOnceAndBlocksNoPoint'
       Old = "<= Charging.ChargerOccupancyReader.NoStation => (true, null, null),"; New = "< Charging.ChargerOccupancyReader.NoStation => (true, null, null)," }
    # A vehicle that hangs is cut off by its own budget, not left to stall the evaluation. Retired in the review rework: the
    # fleet read budget (M18) now cuts off the same hang sooner, so dropping the per-vehicle budget alone changes no outcome.
    @{ Id = 'M14'; Retired = $true; File = $standing; Class = 'IdleReturnExecutionTests'; Expect = 'AVehicleCutOffByItsRoundBudgetReadsAsNotEvaluatedRatherThanItsPreviousVerdict'
       Old = "CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);"; New = "CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);" }
    # Review rework (Coordinator13 2026-10-22): the reviewer's survivors P2 and P3, the fleet read budget (M2) and visibility (M3).
    @{ Id = 'M16'; File = $standing; Class = $clearance; Expect = 'AVehicleThatCannotBeReadCountsWhereItWasLastSeen'
       Old = 'null => (false, null, "no current station"),'; New = 'null => (true, null, null),' }
    @{ Id = 'M17'; File = $standing; Class = $clearance; Expect = 'TheClearingVehicleDoesNotKeepItselfOffAPointAtItsChargersNode'
       Old = "Vehicles.FirstOrDefault(other => !string.Equals(other.VehicleKey, vehicleKey, StringComparison.Ordinal) &&"
       New = "Vehicles.FirstOrDefault(other => vehicleKey.Length >= 0 &&" }
    @{ Id = 'M18'; File = $standing; Class = $commit; Expect = 'AVehicleWhoseReadHangsIsCutOffByTheFleetBudgetAndCountsWhereItWasLastSeen'
       Old = "CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token, fleetBudget);"
       New = "CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token);" }
    @{ Id = 'M19'; File = "$r/Dispatch/DispatchRoundRunner.cs"; Class = 'MultiVehicleExecutionTests'; Expect = 'AVehicleOutsideTheIdleFleetStandingOnTheWaitingPointKeepsTheIdleReturnOffIt'
       Old = "[.. participants.Where(p => p.UnderWay).Select(p => p.Facts.Vehicle)],"
       New = "[.. participants.Where(p => p.UnderWay).Select(p => p.Facts.Vehicle with { CurrentStationId = 300 })]," }
    @{ Id = 'M20'; File = $standing; Class = $commit; Expect = 'AVehicleThatCannotBeReadCountsWhereItWasLastSeen'
       Old = "                    LogCountedAtLastSeen(logger, vehicle.AgvId, key, why ?? `"unlocated`", last, null);`n"; New = '' }
    @{ Id = 'M21'; File = $evaluator; Class = $commit; Expect = 'AVehicleNeverLocatedIsWarnedAboutOnceAndBlocksNoPoint'
       Old = '(standing.UnlocatedNote is { } unlocated ? "; " + unlocated : ""));'; New = '"");' }
    # The dashboard description.
    @{ Id = 'M15'; File = 'src/ControlServer.Host/Dashboard/IdleReturnCodeDescriptions.cs'; Class = 'VehiclePurposeDashboardTests'; Expect = 'EveryIdleReturnVerdictCodeHasAChineseDescription'
       Old = "[IdleReturnReasons.PointStoodOn] ="; New = "[`"X_`" + IdleReturnReasons.PointStoodOn] =" }
)
$summary = @()
foreach ($m in $mutations) {
    if ($Only.Count -gt 0 -and $m.Id -notin $Only) { continue }
    if ($m.Contains('Retired') -and $m.Retired) { $summary += "$($m.Id) RETIRED (see the comment above it)"; continue }
    $full = (Resolve-Path $m.File).Path
    $text = [IO.File]::ReadAllText($full)
    $count = ([regex]::Matches($text, [regex]::Escape($m.Old))).Count
    if ($count -ne 1) { $summary += "$($m.Id) SKIPPED: $count matches in $($m.File)"; continue }
    [IO.File]::WriteAllText($full, $text.Replace($m.Old, $m.New))
    $changed = @(git diff --name-only)
    if ($changed.Count -ne 1) { git checkout -- $m.File; throw "$($m.Id): expected one changed file, got $($changed -join ', ')" }
    $log = Join-Path $Out "$($m.Id).txt"
    git diff | Out-File -FilePath $log -Encoding utf8
    dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj --filter "FullyQualifiedName~$($m.Class).$($m.Expect)" *>> $log
    $exit = $LASTEXITCODE
    git checkout -- $m.File
    if (@(git diff --name-only).Count -ne 0) { throw "$($m.Id): restore left changes" }
    $red = [bool](Select-String -Path $log -Pattern "Failed ControlServer\.Tests\.$($m.Class)\.$($m.Expect)" -Quiet)
    $summary += "$($m.Id) exit=$exit expected-red=$($m.Class).$($m.Expect) $(if ($red) { 'RED (killed)' } else { 'NOT RED (survived)' })"
}
$summary | Tee-Object -FilePath (Join-Path $Out 'SUMMARY.txt')
