#Requires -Version 7
param([string]$Out = 'C:/work/8005-scratch/cs789/m4c')
$repo = 'C:/work/8005-wt/cs789-8005-agv-control-server'
Set-Location $repo
[IO.Directory]::SetCurrentDirectory($repo)
New-Item -ItemType Directory -Force $Out | Out-Null
$sup = 'src/ControlServer.Host/Runtime/Commands/EmergencyStopSupervisor.cs'
$coord = 'src/ControlServer.Host/Runtime/Faults/VehicleFaultCoordinator.cs'
$cases = @(
    @{ Name = 'supervisor-paused-check'; File = $sup;
       Old = 'else if (orders.StateOf(allowance.HeldOrderId) != RiotOrderState.Paused)'; New = 'else if (orders.StateOf(allowance.HeldOrderId) == -999)' },
    @{ Name = 'hold-confirmed-check'; File = $coord;
       Old = "            holdOutcome != RiotOrderCommandOutcome.Confirmed ||`n"; New = '' },
    @{ Name = 'level-check'; File = $coord;
       Old = "            fault.Level != VehicleFaultLevel.SuspectedBlocked ||`n"; New = '' },
    @{ Name = 'trigger-generation-check'; File = $sup;
       Old = '        triggerFaultGeneration == allowance.FaultGeneration;'; New = '        true;' },
    @{ Name = 'coordinator-paused-check'; File = $coord;
       Old = "            order.OrderState != RiotOrderState.Paused ||`n"; New = '' },
    @{ Name = 'coordinator-orderid-check'; File = $coord;
       Old = '            !string.Equals(order.OrderId, target.OrderId, StringComparison.Ordinal))'; New = '            false)' }
)
$summary = Join-Path $Out 'summary.txt'
Set-Content $summary ''
foreach ($case in $cases) {
    if (@(git status --porcelain).Count -ne 0) { "ABORT dirty before $($case.Name)" | Add-Content $summary; exit 2 }
    $path = Join-Path $repo $case.File
    $text = [IO.File]::ReadAllText($path)
    $hits = ([regex]::Matches($text, [regex]::Escape($case.Old))).Count
    if ($hits -ne 1) { "CASE $($case.Name) SKIPPED hits=$hits" | Add-Content $summary; continue }
    [IO.File]::WriteAllText($path, $text.Replace($case.Old, $case.New))
    $changed = @(git diff --name-only) -join ','
    try {
        $log = Join-Path $Out "$($case.Name).txt"
        dotnet test tests/ControlServer.Tests -c Release --filter 'FullyQualifiedName~InTransitDoorReadingRestoredReleaseTests' 2>&1 | Set-Content $log
        $exit = $LASTEXITCODE
        $line = (Select-String -Path $log -Pattern '(Passed|Failed)!\s+-' | Select-Object -Last 1).Line
        $failed = (Select-String -Path $log -Pattern '^\s+Failed ControlServer' | ForEach-Object { $_.Line.Trim() }) -join ' || '
        "CASE $($case.Name) changed=$changed exit=$exit $line FAILED: $failed" | Add-Content $summary
    } finally {
        git checkout -- $case.File
    }
}
"DONE dirty=$(@(git status --porcelain).Count)" | Add-Content $summary
