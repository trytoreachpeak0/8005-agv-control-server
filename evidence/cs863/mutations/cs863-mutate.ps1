#Requires -Version 7
# cs#863 mutation self-check: each mutation edits exactly one file, runs the named tests, restores that file.
param([string]$Repo, [string]$OutDir, [string[]]$Only)
$ErrorActionPreference = 'Stop'
Set-Location $Repo
New-Item -ItemType Directory -Force $OutDir | Out-Null

$charging = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.Charging.cs'
$mutations = @(
    @{ Name = 'M1-unread-binding-counts-as-unbound'; File = $charging
       Old = 'OrderState: RiotOrderState.Queueing, ExecuteVehicleBound: false })'
       New = 'OrderState: RiotOrderState.Queueing, ExecuteVehicleBound: not true })'
       Filter = 'FullyQualifiedName~ChargingApproachSupervisionTests.AQueueingOrderWhoseBindingWasNotRead' },
    @{ Name = 'M2-code-never-cleared-on-binding'; File = $charging
       Old = "            if (string.Equals(runtime.BlockReasonCode, ChargingExecutionReasons.OrderNotDispatched, StringComparison.Ordinal))`n            {`n                dispatchRound.Charging.Board.Unsay(alarmKey);"
       New = "            if (runtime.JourneyId.Length < 0)`n            {`n                dispatchRound.Charging.Board.Unsay(alarmKey);"
       Filter = 'FullyQualifiedName~ChargingApproachSupervisionTests.OnceRiotBindsAVehicle' },
    @{ Name = 'M3-window-kept-in-process-memory'; File = $charging
       Old = 'DateTimeOffset confirmedAt = cycle.OrderConfirmedAt ?? now;'
       New = 'DateTimeOffset confirmedAt = UndispatchedSeen.GetValue(this, _ => new()).GetOrAdd(runtime.JourneyId, now); _ = cycle;'
       Extra = @{ Old = '    private async Task<bool> NameUndispatchedChargeOrderAsync('
                  New = "    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JourneyRuntimeEngine, System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>> UndispatchedSeen = new();`n`n    private async Task<bool> NameUndispatchedChargeOrderAsync(" }
       Filter = 'FullyQualifiedName~ChargingApproachSupervisionTests.ARestartInsideTheWindow' },
    @{ Name = 'M4-only-a-failed-read-counts-as-lost'; File = $charging
       Old = "        if (vehicle is not { Connected: true })`n        {`n            await NameChargingLossAsync(runtime, stop, ChargingExecutionReasons.VehicleObservationLost, now, cancellationToken)`n                .ConfigureAwait(false);`n            return true;"
       New = "        if (vehicle is null)`n        {`n            await NameChargingLossAsync(runtime, stop, ChargingExecutionReasons.VehicleObservationLost, now, cancellationToken)`n                .ConfigureAwait(false);`n            return true;"
       Filter = 'FullyQualifiedName~ChargingApproachSupervisionTests.AVehicleLostOnTheWay' },
    @{ Name = 'M5-lost-overdue-without-window'; File = 'src/ControlServer.Host/Dashboard/ChargingDashboardFacts.cs'
       Old = 'ChargingExecutionReasons.VehicleObservationLost when now - since > window =>'
       New = 'ChargingExecutionReasons.VehicleObservationLost =>'
       Filter = 'FullyQualifiedName~ChargingDashboardTests.AChargerReservedByAJourney' },
    @{ Name = 'M6-not-dispatched-not-a-held-code'; File = 'src/ControlServer.Host/Runtime/Charging/ManualStationClearance.cs'
       Old = "        ChargingExecutionReasons.OrderNotDispatched,`n        // control-server#407"
       New = "        // control-server#407"
       Filter = 'FullyQualifiedName~ChargingApproachSupervisionTests.OnceAPersonCancelsTheUndispatchedOrder' }
)

foreach ($m in $mutations) {
    if ($Only -and $m.Name -notin ($Only -join "," -split ",")) { continue }
    $raw = [IO.File]::ReadAllText($m.File)
    $crlf = $raw.Contains("`r`n")
    $text = $raw.Replace("`r`n", "`n")
    $pairs = @(@{ Old = $m.Old; New = $m.New })
    if ($m.Extra) { $pairs += $m.Extra }
    foreach ($p in $pairs) {
        $count = ([regex]::Matches($text, [regex]::Escape($p.Old))).Count
        if ($count -ne 1) { throw "$($m.Name): expected one match, found $count" }
        $text = $text.Replace($p.Old, $p.New)
    }
    if ($crlf) { $text = $text.Replace("`n", "`r`n") }
    [IO.File]::WriteAllText($m.File, $text)
    $changed = @(git diff --name-only)
    if ($changed.Count -ne 1 -or $changed[0] -ne $m.File) { git checkout -- $m.File; throw "$($m.Name): changed $($changed -join ',')" }
    $log = Join-Path $OutDir "$($m.Name).txt"
    "mutation $($m.Name) in $($m.File)" | Set-Content $log
    git diff | Add-Content $log
    dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj --filter $m.Filter 2>&1 | Add-Content $log
    git checkout -- $m.File
    if (@(git diff --name-only).Count -ne 0) { throw "$($m.Name): restore left changes" }
    $summary = (Select-String -Path $log -Pattern 'Passed!|Failed!|error CS' | Select-Object -Last 1).Line
    "$($m.Name): $summary"
}
