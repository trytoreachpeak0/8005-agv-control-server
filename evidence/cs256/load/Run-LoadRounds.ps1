#Requires -Version 7
# cs#256 load run: each round starts the OnboardSilentLivenessLossTests class in eight processes at once,
# four built from the fixed fixture (new1..4) and four from the fixture before the fix (old1..4).
param(
    [int] $Rounds = 15,
    [string] $Root = 'F:/8005-scratch/cs256-load',
    [string] $Out = 'F:/8005-scratch/cs256-load/logs'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Out | Out-Null
$lanes = 'new1', 'new2', 'new3', 'new4', 'old1', 'old2', 'old3', 'old4'
$results = [System.Collections.Generic.List[object]]::new()
for ($round = 1; $round -le $Rounds; $round++) {
    $started = Get-Date
    $runs = $lanes | ForEach-Object -Parallel {
        $lane = $_
        $log = Join-Path $using:Out ("r{0:D2}-{1}.log" -f $using:round, $lane)
        $dll = Join-Path $using:Root "$lane/win-x64/ControlServer.Tests.dll"
        & dotnet test $dll --filter 'FullyQualifiedName~OnboardSilentLivenessLossTests' *> $log
        [pscustomobject]@{ Round = $using:round; Lane = $lane; Exit = $LASTEXITCODE
            Summary = (Select-String -Path $log -Pattern '^(Passed!|Failed!)' | Select-Object -Last 1)?.Line }
    } -ThrottleLimit 8
    $runs | ForEach-Object { $results.Add($_) }
    "round $round done in $([int]((Get-Date) - $started).TotalSeconds)s: " +
        (($runs | Sort-Object Lane | ForEach-Object { "$($_.Lane)=$($_.Exit)" }) -join ' ')
}
''
'summary by version:'
$results | Group-Object { $_.Lane.Substring(0, 3) } | ForEach-Object {
    $failed = @($_.Group | Where-Object Exit -ne 0)
    "$($_.Name): $($_.Count) class runs, $($failed.Count) non-zero exit"
    $failed | ForEach-Object { "  round $($_.Round) $($_.Lane): $($_.Summary)" }
}
