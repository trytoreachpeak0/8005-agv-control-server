#Requires -Version 7
# Robustness run: Burners busy-loop processes saturate the CPU while Lanes parallel processes each run the filter
# Rounds times against the already-built Release output. Records every round's verdict per lane.
param(
    [Parameter(Mandatory)][string]$Label,
    [Parameter(Mandatory)][string]$Filter,
    [int]$Burners = 12,
    [int]$Lanes = 3,
    [int]$Rounds = 10
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$env:DOTNET_CLI_UI_LANGUAGE = "en"
$repo = (git rev-parse --show-toplevel).Trim()
Set-Location $repo
$out = Join-Path $repo "evidence/cs440/runs/$Label"
New-Item -ItemType Directory -Force $out | Out-Null
"head=$(git rev-parse HEAD) filter=$Filter burners=$Burners lanes=$Lanes rounds=$Rounds" | Set-Content "$out/meta.txt"
git status --short -- src tests | Add-Content "$out/meta.txt"
$burn = 1..$Burners | ForEach-Object {
    Start-Process pwsh -ArgumentList '-NoProfile', '-Command', '$x = 0; while ($true) { $x++ }' -WindowStyle Hidden -PassThru
}
try {
    1..$Lanes | ForEach-Object -Parallel {
        $lane = $_
        foreach ($round in 1..$using:Rounds) {
            $log = Join-Path $using:out "lane$lane-round$round.log"
            dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-build --filter $using:Filter `
                --logger "console;verbosity=normal" --results-directory (Join-Path $using:out "trx-lane$lane") *> $log
            $exit = $LASTEXITCODE
            $lines = (Select-String -Path $log -Pattern '^\s+(Passed|Failed) ControlServer' | ForEach-Object { $_.Line.Trim() })
            "lane $lane round $round exit=$exit :: $($lines -join ' || ')" | Add-Content (Join-Path $using:out "results.txt")
        }
    } -ThrottleLimit $Lanes
}
finally {
    $burn | Stop-Process -Force -ErrorAction SilentlyContinue
}
$all = Get-Content "$out/results.txt"
$passed = ($all | Select-String -Pattern '\bPassed ControlServer' -AllMatches | ForEach-Object { $_.Matches.Count } | Measure-Object -Sum).Sum
$failed = ($all | Select-String -Pattern '\bFailed ControlServer' -AllMatches | ForEach-Object { $_.Matches.Count } | Measure-Object -Sum).Sum
"TOTAL test results: passed=$passed failed=$failed (rounds=$($all.Count))" | Tee-Object -Append "$out/results.txt"
