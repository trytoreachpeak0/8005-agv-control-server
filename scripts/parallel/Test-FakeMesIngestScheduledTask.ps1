#Requires -Version 7

<#
.SYNOPSIS
    Self-test for the FakeMesIngest scheduled task: registers it as SYSTEM with the installer's own
    functions, and asserts that it actually starts the double -- not that it registered.

.DESCRIPTION
    control-server#512. The first real install on factory01 registered the task, started it, and
    nothing ran: LastTaskResult -1, no log directory, no pwsh host start. Every check before it
    stopped at "the task is registered", which was true.

    This script is the check that would have gone red. It needs an elevated session (a SYSTEM task
    cannot be registered without one) and it creates a scheduled task and a directory, so unlike
    Test-ParallelInstance.ps1 it touches the machine it runs on. Run it on a lab machine -- vm01 --
    never on factory01. It is not in CI (README.md).

    Two cases, both through Get-ParallelFakeMesIngestTaskAction, Register-ParallelFakeMesIngestTask
    and Wait-ParallelFakeMesIngestTask in ParallelHost.psm1, the functions the installer calls:

      * started: the real double behind the real wrapper. The double must answer
        /control/v1/health within 120 s (the installer's own deadline), the wrapper's log must
        reach "Serving.", and the task must be Running;
      * registered but not started (the negative control): the same task with a runner path
        that does not exist, so pwsh exits before any wrapper line, the shape factory01 showed.
        The task registers; the wait must throw, and its report must say the log is absent. If this
        case does not go red, the first case proves nothing.

    Paths contain spaces on purpose. Everything is removed in a finally block -- task, processes
    started from the test root (by PID), the root itself -- and what is left is printed.

.PARAMETER FakeMesIngestZip
    Publish-FakeMesIngest.ps1's zip.

.PARAMETER Port
    A free loopback port. Not 58188 (factory01's) and not in the L2 blocks.

.EXAMPLE
    pwsh -File scripts/parallel/Test-FakeMesIngestScheduledTask.ps1 -FakeMesIngestZip C:/stage/fake-pub.zip -Port 47188
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $FakeMesIngestZip,
    [ValidateRange(1024, 65535)][int] $Port = 47188,
    [string] $TaskName = 'cs512-repro-FakeMesIngest',
    [string] $Root = 'C:\cs512 repro'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

Import-Module (Join-Path $PSScriptRoot 'ParallelHost.psm1') -Force

$script:Failed = 0
$script:Passed = 0
function Write-Result {
    param([bool] $Ok, [string] $Name, [string] $Detail)
    if ($Ok) { $script:Passed++; Write-Host "  PASS  $Name" -ForegroundColor Green }
    else { $script:Failed++; Write-Host "  FAIL  $Name" -ForegroundColor Red; Write-Host "        $Detail" -ForegroundColor Red }
}

$identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run elevated: registering a SYSTEM task needs it.'
}
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { throw "Task '$TaskName' already exists; refusing to replace it." }
if (Test-Path -LiteralPath $Root) { throw "$Root already exists; use a new root." }
if (Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue) { throw "Port $Port is in use." }

$installRoot = Join-Path $Root 'Fake Root'
$opsRoot = Join-Path $Root 'ops root'
$seedPath = Join-Path $opsRoot 'fake-mes-ingest-seed.json'
$executable = Join-Path $installRoot 'ControlServer.FakeMesIngest.exe'
$runner = Join-Path $installRoot 'Start-FakeMesIngestResident.ps1'
$pwsh = Join-Path $PSHOME 'pwsh.exe'

function Remove-TestTask {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    # Stop-ScheduledTask ends the task's process tree; anything left was started from this root.
    $prefix = $Root.TrimEnd('\') + '\'
    foreach ($process in @(Get-CimInstance Win32_Process | Where-Object {
                ($_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) -or
                ($_.Name -eq 'pwsh.exe' -and $_.CommandLine -and $_.CommandLine.Contains($installRoot)) })) {
        Write-Host "  stopping pid $($process.ProcessId) ($($process.Name))"
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

try {
    Expand-Archive -LiteralPath $FakeMesIngestZip -DestinationPath $installRoot
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Start-FakeMesIngestResident.ps1') -Destination $runner
    New-Item -ItemType Directory -Path $opsRoot | Out-Null
    [IO.File]::WriteAllText($seedPath, '{ "demands": [] }', [Text.UTF8Encoding]::new($false))

    # ------------------------------------------------------------------ started ---
    Write-Host 'Case 1: the installer''s task starts the double'
    $logPath = Join-Path $opsRoot 'logs 1\fake-mes-ingest.log'
    $action = Get-ParallelFakeMesIngestTaskAction -PwshPath $pwsh -RunnerPath $runner -ExecutablePath $executable `
        -Port $Port -SeedPath $seedPath -LogPath $logPath -WorkingDirectory $installRoot
    Write-Result ([IO.Path]::IsPathFullyQualified($action.Execute) -and (Test-Path -LiteralPath $action.Execute -PathType Leaf)) `
        'the action runs pwsh by an absolute path that exists' "Execute=$($action.Execute)"
    $since = Register-ParallelFakeMesIngestTask -TaskName $TaskName -Action $action -LogPath $logPath -Description 'cs512 self-test, loopback only'
    $started = [datetime]::UtcNow
    $health = $null
    $failure = $null
    try { $health = Wait-ParallelFakeMesIngestTask -TaskName $TaskName -Port $Port -LogPath $logPath -Since $since -TimeoutSeconds 120 }
    catch { $failure = $_.Exception.Message }
    $elapsed = ([datetime]::UtcNow - $started).TotalSeconds
    Write-Result ($null -ne $health -and $health -match '"status":"live"') `
        "health answered within 120 s ($([Math]::Round($elapsed, 1)) s)" "$failure"
    # The wrapper seeds after the double answers; give it the few seconds that takes.
    $deadline = [datetime]::UtcNow.AddSeconds(30)
    while ([datetime]::UtcNow -lt $deadline -and -not ((Test-Path -LiteralPath $logPath) -and (Get-Content -LiteralPath $logPath -Raw) -match 'Serving\.')) {
        Start-Sleep -Milliseconds 500
    }
    $log = (Test-Path -LiteralPath $logPath) ? (Get-Content -LiteralPath $logPath -Raw) : ''
    Write-Result ($log -match 'FakeMesIngest resident starting' -and $log -match 'Serving\.') `
        'the wrapper ran under the task and reached Serving.' "log: $log"
    $state = [string] (Get-ScheduledTask -TaskName $TaskName).State
    Write-Result ($state -eq 'Running') 'the task is Running' "state=$state"
    Write-Host ($log.TrimEnd() -replace '(?m)^', '        ')
    Remove-TestTask

    # --------------------------------------------- registered but not started ---
    Write-Host 'Case 2 (negative control): a task that registers but never reaches the wrapper'
    $logPath = Join-Path $opsRoot 'logs 2\fake-mes-ingest.log'
    $missingRunner = Join-Path $installRoot 'Absent-Runner.ps1'
    $action = Get-ParallelFakeMesIngestTaskAction -PwshPath $pwsh -RunnerPath $missingRunner -ExecutablePath $executable `
        -Port $Port -SeedPath $seedPath -LogPath $logPath -WorkingDirectory $installRoot
    $since = Register-ParallelFakeMesIngestTask -TaskName $TaskName -Action $action -LogPath $logPath -Description 'cs512 self-test, negative control'
    Write-Result ($null -ne (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) 'the task registered' 'not registered'
    $failure = $null
    try { $null = Wait-ParallelFakeMesIngestTask -TaskName $TaskName -Port $Port -LogPath $logPath -Since $since -TimeoutSeconds 20 }
    catch { $failure = $_.Exception.Message }
    Write-Result ($null -ne $failure) 'the wait goes red when the double never starts' 'the wait returned'
    Write-Result ($null -ne $failure -and $failure -match [regex]::Escape("${logPath}: absent") -and $failure -match 'lastTaskResult=0x') `
        'the failure names the absent log and the task result' "$failure"
    if ($failure) { Write-Host ($failure -replace '(?m)^', '        ') }
} finally {
    Write-Host 'Cleanup'
    Remove-TestTask
    Start-Sleep -Seconds 2
    Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue
    $leftTask = @(Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue).Count
    $leftRoot = Test-Path -LiteralPath $Root
    $leftPort = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue).Count
    Write-Host "  left behind: task=$leftTask root=$leftRoot listeners on $Port=$leftPort"
}

Write-Host ''
Write-Host "Passed: $script:Passed  Failed: $script:Failed"
if ($script:Failed -gt 0) { exit 1 }
