#Requires -Version 7

<#
.SYNOPSIS
    Self-test for the FakeMesIngest scheduled task: registers it as SYSTEM with the installer's own
    functions, and asserts that it actually starts the double and that the installer's seeding step
    reaches it -- not that the task registered.

.DESCRIPTION
    control-server#512. The first real install on factory01 registered the task, started it, and
    nothing ran: LastTaskResult -1, no log directory, no pwsh host start. Every check before it
    stopped at "the task is registered", which was true.

    This script is the check that would have gone red. It needs an elevated session (a SYSTEM task
    cannot be registered without one) and it creates a scheduled task and a directory, so unlike
    Test-ParallelInstance.ps1 it touches the machine it runs on. Run it on a lab machine -- vm01 --
    never on factory01. It is not in CI (README.md).

    Two cases, both through Get-ParallelFakeMesIngestTaskAction, Register-ParallelFakeMesIngestTask,
    Wait-ParallelFakeMesIngestTask and Invoke-ParallelFakeMesIngestSeed in ParallelHost.psm1, the
    functions the installer calls:

      * started: the task runs the real double. It must answer /control/v1/health within 120 s (the
        installer's own deadline); the process must be the double's executable, not a pwsh, and run
        as SYSTEM; the task must be Running; and the seeding step must reset and read the catalog back;
      * registered but not started (the negative control): the same task pointed at an executable
        that does not exist. The task registers; the wait must throw, and its report must say the
        double is not running and give the task result. If this case does not go red, the first
        proves nothing.

    Paths contain spaces on purpose. Everything is removed in a finally block -- task, processes
    started from the test root (by PID), the root itself -- and what is left is printed.

    What it does not show: that the task form starts on factory01. The form that failed there
    started on vm01 too (control-server#512); this proves the chain, a lab machine cannot prove the
    machine.

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
$logPath = Join-Path $opsRoot 'logs\fake-mes-ingest.log'
$executable = Join-Path $installRoot 'ControlServer.FakeMesIngest.exe'

function Remove-TestTask {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    # Stop-ScheduledTask ends the task's process; anything left was started from this root.
    $prefix = $Root.TrimEnd('\') + '\'
    foreach ($process in @(Get-CimInstance Win32_Process | Where-Object {
                $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })) {
        Write-Host "  stopping pid $($process.ProcessId) ($($process.Name))"
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

try {
    Expand-Archive -LiteralPath $FakeMesIngestZip -DestinationPath $installRoot
    New-Item -ItemType Directory -Path $opsRoot | Out-Null
    [IO.File]::WriteAllText($seedPath, '{ "demands": [] }', [Text.UTF8Encoding]::new($false))

    # ------------------------------------------------------------------ started ---
    Write-Host 'Case 1: the installer''s task starts the double, and the installer''s seeding reaches it'
    $action = Get-ParallelFakeMesIngestTaskAction -ExecutablePath $executable -Port $Port -WorkingDirectory $installRoot
    Write-Result ($action.Execute -ceq $executable -and $action.Argument -notmatch 'pwsh|-File') `
        'the action is the double itself, no pwsh' "Execute=$($action.Execute) Argument=$($action.Argument)"
    $since = Register-ParallelFakeMesIngestTask -TaskName $TaskName -Action $action -LogPath $logPath -Description 'cs512 self-test, loopback only'
    $started = [datetime]::UtcNow
    Write-Result ($since -is [datetime]) 'registration returns one timestamp and nothing else' "returned: $(@($since).Count) item(s)"
    Write-Result (Test-Path -LiteralPath (Split-Path -Parent $logPath) -PathType Container) 'the log directory exists before anything runs' $logPath
    $health = $null
    $failure = $null
    try { $health = Wait-ParallelFakeMesIngestTask -TaskName $TaskName -Port $Port -ExecutablePath $executable -Since $since -TimeoutSeconds 120 }
    catch { $failure = $_.Exception.Message }
    $elapsed = ([datetime]::UtcNow - $started).TotalSeconds
    Write-Result ($null -ne $health -and $health -match '"status":"live"') `
        "health answered within 120 s ($([Math]::Round($elapsed, 1)) s)" "$failure"

    $processes = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $executable, [StringComparison]::OrdinalIgnoreCase) })
    $owner = ($processes.Count -eq 1) ? (Invoke-CimMethod -InputObject $processes[0] -MethodName GetOwner) : $null
    Write-Result ($processes.Count -eq 1 -and $owner -and $owner.User -eq 'SYSTEM') `
        'exactly one process of the double runs, as SYSTEM' "processes=$($processes.Count) owner=$($owner ? "$($owner.Domain)\$($owner.User)" : '-')"
    $state = [string] (Get-ScheduledTask -TaskName $TaskName).State
    Write-Result ($state -eq 'Running') 'the task is Running' "state=$state"

    $seeded = $null
    $failure = $null
    try { $seeded = Invoke-ParallelFakeMesIngestSeed -Port $Port -SeedPath $seedPath -LogPath $logPath }
    catch { $failure = $_.Exception.Message }
    $log = (Test-Path -LiteralPath $logPath) ? (Get-Content -LiteralPath $logPath -Raw) : ''
    Write-Result ($null -eq $failure -and "$seeded" -match 'Catalog now holds 0 demand' -and $log -match 'Catalog reset; this round is' -and $log -match 'Seeded\. The double keeps running') `
        'the installer''s seeding resets the catalog, reads it back, and leaves the double running' "seeded=$seeded failure=$failure log: $log"
    Write-Result (@(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $executable, [StringComparison]::OrdinalIgnoreCase) }).Count -eq 1) `
        'the double is still the one process after seeding' 'it is gone, or a second one appeared'
    Write-Host ($log.TrimEnd() -replace '(?m)^', '        ')
    Remove-TestTask

    # --------------------------------------------- registered but not started ---
    Write-Host 'Case 2 (negative control): a task that registers but whose double never starts'
    $missing = Join-Path $installRoot 'Absent.FakeMesIngest.exe'
    $action = Get-ParallelFakeMesIngestTaskAction -ExecutablePath $missing -Port $Port -WorkingDirectory $installRoot
    $since = Register-ParallelFakeMesIngestTask -TaskName $TaskName -Action $action -LogPath $logPath -Description 'cs512 self-test, negative control'
    Write-Result ($null -ne (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) 'the task registered' 'not registered'
    $failure = $null
    try { $null = Wait-ParallelFakeMesIngestTask -TaskName $TaskName -Port $Port -ExecutablePath $missing -Since $since -TimeoutSeconds 20 }
    catch { $failure = $_.Exception.Message }
    Write-Result ($null -ne $failure) 'the wait goes red when the double never starts' 'the wait returned'
    Write-Result ($null -ne $failure -and $failure -match [regex]::Escape("process ${missing}: not running") -and $failure -match 'lastTaskResult=0x') `
        'the failure says the double is not running and gives the task result' "$failure"
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
