#Requires -Version 7
# Applies one probe patch (or none), builds Release, runs one test filter N times, records each run, reverts the patch.
param(
    [Parameter(Mandatory)][string]$Label,
    [string]$Probe,
    [Parameter(Mandatory)][string]$Filter,
    [int]$Runs = 2
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$env:DOTNET_CLI_UI_LANGUAGE = "en"
$repo = (git rev-parse --show-toplevel).Trim()
Set-Location $repo
$out = Join-Path $repo "evidence/cs440/runs/$Label"
New-Item -ItemType Directory -Force $out | Out-Null
if ($Probe) { git apply $Probe; if ($LASTEXITCODE) { throw "probe did not apply" } }
try {
    "head=$(git rev-parse HEAD) probe=$Probe filter=$Filter" | Set-Content "$out/meta.txt"
    git diff --stat -- src tests | Add-Content "$out/meta.txt"
    dotnet build tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release -v q -nologo *> "$out/build.log"
    if ($LASTEXITCODE) { throw "build failed, see $out/build.log" }
    foreach ($i in 1..$Runs) {
        $log = "$out/run-$i.log"
        dotnet test tests/ControlServer.Tests/ControlServer.Tests.csproj -c Release --no-build --filter $Filter --logger "console;verbosity=normal" --logger "trx;LogFileName=run-$i.trx" --results-directory $out *> $log
        $summary = ((Select-String -Path $log -Pattern '^Test Run (Successful|Failed)|^\s+(Passed|Failed): \d+|^\s+Failed ControlServer' | ForEach-Object { $_.Line.Trim() }) -join '; ')
        "run $i exit=$LASTEXITCODE $summary" | Tee-Object -Append "$out/results.txt"
    }
}
finally {
    if ($Probe) { git apply -R $Probe }
    git status --short -- src tests | Add-Content "$out/meta.txt"
}
