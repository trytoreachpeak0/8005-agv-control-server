#Requires -Version 7
# cs#597 measurement: old code (rig worktree at 774cecca, pins only) vs new code with the index (ticket worktree), same machine.
# A set is days:changeSeconds:agv01SilentHours:sides, sides being old+new or new.
param(
    [string[]] $Sets = @('3:600:0:old+new', '10:600:0:old+new', '3:7:0:old+new', '3:7:24:old+new', '21:7:0:new'),
    [string] $OutDir = 'F:/8005-scratch/cs597-measure'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$trees = @{
    old = 'F:/8005-wt/rig-cs597-old-8005-agv-control-server'
    new = 'F:/8005-wt/cs597-8005-agv-control-server'
}
foreach ($set in $Sets) {
    $days, $seconds, $silent, $sides = $set -split ':'
    foreach ($side in ($sides -split '\+')) {
        $env:CS597_MEASURE_DAYS = $days
        $env:CS597_MEASURE_CHANGE_SECONDS = $seconds
        $env:CS597_MEASURE_SILENT_HOURS = $silent
        $env:CS597_MEASURE_DIR = 'F:/8005-scratch'
        $env:CS597_MEASURE_OUT = "$OutDir/measure-$side-${days}days-${seconds}s-silent${silent}h.txt"
        $started = Get-Date
        dotnet test "$($trees[$side])/tests/ControlServer.Tests/ControlServer.Tests.csproj" --no-build -nologo `
            --filter 'FullyQualifiedName~SafetyStateLatestFirstReadTests.MeasureSafetyStateReads' -- xUnit.Explicit=only |
            Select-String -Pattern 'Passed!|Failed!|Failed '
        "{0} {1}d/{2}s/silent{3}h took {4:F0}s" -f $side, $days, $seconds, $silent, ((Get-Date) - $started).TotalSeconds
    }
}
