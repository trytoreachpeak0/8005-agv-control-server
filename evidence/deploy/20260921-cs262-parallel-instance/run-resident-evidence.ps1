#Requires -Version 7
# Evidence run for control-server#262: the FakeMesIngest residency scheme, exercised on the
# control host. Start -> seed -> crash -> restart -> re-seed, with the catalog read back from
# the double at each step.
param(
    [Parameter(Mandatory = $true)][string] $Repo,
    [Parameter(Mandatory = $true)][string] $Scratch,
    [int] $Port = 58188
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$runner = Join-Path $Repo 'scripts\parallel\Start-FakeMesIngestResident.ps1'
$exe = Join-Path $Scratch 'fake-mes\ControlServer.FakeMesIngest.exe'
$seed = Join-Path $Scratch 'seed-test.json'
$log = Join-Path $Scratch 'evidence-resident.log'
$base = "http://127.0.0.1:$Port"

function Start-Resident {
    Start-Process pwsh -ArgumentList @(
        '-NoProfile', '-File', $runner, '-ExecutablePath', $exe,
        '-Port', "$Port", '-SeedPath', $seed, '-LogPath', $log
    ) -PassThru -WindowStyle Hidden
}
function Wait-Live {
    $deadline = [datetime]::UtcNow.AddSeconds(60)
    while ([datetime]::UtcNow -lt $deadline) {
        try { return (Invoke-WebRequest -Uri "$base/control/v1/health" -NoProxy -TimeoutSec 3 -UseBasicParsing) } catch { Start-Sleep -Milliseconds 400 }
    }
    throw "never came live on $base"
}
function Get-CatalogSummary {
    $snap = (Invoke-WebRequest -Uri "$base/control/v1/snapshot" -NoProxy -TimeoutSec 5 -UseBasicParsing).Content | ConvertFrom-Json
    $ids = @($snap.body.demands | ForEach-Object { $_.demandId.Substring(0, 8) })
    return "runId=$($snap.runId) revision=$($snap.revision) catalogRevision=$($snap.body.catalogRevision) demands=$($ids.Count) [$($ids -join ', ')]"
}
function Get-ResidentProcess {
    return Get-CimInstance Win32_Process -Filter "Name='pwsh.exe'" |
        Where-Object { $_.CommandLine -like '*Start-FakeMesIngestResident*' }
}

Get-Process -Name 'ControlServer.FakeMesIngest' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2
Remove-Item "$log*" -Force -ErrorAction SilentlyContinue

Write-Host "=== step 1: first start ==="
$null = Start-Resident
$health = Wait-Live
Start-Sleep -Seconds 2
Write-Host "  health   : $($health.Content)"
Write-Host "  catalog  : $(Get-CatalogSummary)"
$double1 = (Get-Process -Name 'ControlServer.FakeMesIngest').Id
$wrapper1 = (Get-ResidentProcess).ProcessId
Write-Host "  pids     : double=$double1 wrapper=$wrapper1"

Write-Host ''
Write-Host "=== step 2: the double crashes (SIGKILL equivalent) ==="
Stop-Process -Id $double1 -Force
Start-Sleep -Seconds 6
$wrapperGone = $null -eq (Get-ResidentProcess)
$portFree = $null -eq (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
Write-Host "  wrapper exited with the double : $wrapperGone"
Write-Host "  port $Port released            : $portFree"
Write-Host "  last log line                  : $((Get-Content $log -Tail 1))"

Write-Host ''
Write-Host "=== step 3: the scheduled task's restart policy would run it again ==="
$null = Start-Resident
$null = Wait-Live
Start-Sleep -Seconds 2
Write-Host "  catalog  : $(Get-CatalogSummary)"
$double2 = (Get-Process -Name 'ControlServer.FakeMesIngest').Id
Write-Host "  pids     : double=$double2 (was $double1)"

Write-Host ''
Write-Host "=== step 4: the double refuses a non-loopback listener ==="
# The property the isolation table depends on: this thing answers questions the plant asks for
# real, and must not be reachable from the plant network.
$refuse = Start-Process $exe -ArgumentList '--FakeMesIngest:listenAddress=0.0.0.0', "--FakeMesIngest:port=58189" `
    -NoNewWindow -PassThru -RedirectStandardError "$Scratch\refuse.err" -RedirectStandardOutput "$Scratch\refuse.out"
$refuse.WaitForExit(15000) | Out-Null
Write-Host "  exit code: $($refuse.ExitCode)  (2 = refused to start)"
Write-Host "  stderr   : $((Get-Content "$Scratch\refuse.err" -Raw).Trim())"

Write-Host ''
Write-Host "=== teardown ==="
Get-Process -Name 'ControlServer.FakeMesIngest' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
Write-Host "  double running : $($null -ne (Get-Process -Name 'ControlServer.FakeMesIngest' -ErrorAction SilentlyContinue))"
Write-Host "  wrapper running: $($null -ne (Get-ResidentProcess))"
Write-Host ''
Write-Host '=== full resident log ==='
Get-Content $log
