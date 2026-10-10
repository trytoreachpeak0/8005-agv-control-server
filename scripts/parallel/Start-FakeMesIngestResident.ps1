#Requires -Version 7

<#
.SYNOPSIS
    Re-seeds the running FakeMesIngest double of the v2 parallel instance from its seed file.

.DESCRIPTION
    control-server#262 made this script the scheduled task's action: start the double, seed it,
    live as long as it does. control-server#512 took that away. On factory01 a SYSTEM task whose
    action was pwsh -File this script never got as far as a PowerShell host (LastTaskResult -1, no
    PowerShellCore/Operational 40961), also with pwsh by absolute path, while a SYSTEM task running
    pwsh -ExecutionPolicy Bypass -File on a small script elsewhere on the same machine ran normally.
    What separates the two was not isolated. The task now runs the double's executable itself
    (Get-ParallelFakeMesIngestTaskAction in ParallelHost.psm1), the form the dashboard task has.

    What is left here is the seeding, for an operator: the installer seeds once, right after the
    task comes up (Invoke-ParallelFakeMesIngestSeed), and in this task form nothing re-seeds after the
    double restarts -- a reboot, the task's restart policy -- so the catalog is then the double's own
    empty one. Run this after editing the seed file or after the double restarted:

        ssh factory01 "pwsh -NoProfile -File D:\zhengyushao\control-server-v2-ops\Start-FakeMesIngestResident.ps1"

    It keeps its old name because the control host's deployment script copies it by that name
    (19-deploy-control-server-parallel.ps1 in 8005-workspace). It runs from the operations directory,
    beside ParallelHost.psm1 and the installed instance definition, and takes the port, seed path and
    log path from that definition unless given.

    Resetting is idempotent: whatever the catalog held, it then holds the seed file and nothing else.

.PARAMETER Port
    Loopback port of the double. Default: fakeMesIngest.port of the installed definition.

.PARAMETER SeedPath
    The seed file. Default: fakeMesIngest.seedPath of the installed definition.

.PARAMETER LogPath
    Where the seeding lines go. Default: the layout's FakeLogPath.

.PARAMETER ReadyTimeoutSeconds
    How long to wait for the double's health before giving up.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)][int] $Port,
    [string] $SeedPath,
    [string] $LogPath,
    [ValidateRange(1, 600)][int] $ReadyTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

Import-Module (Join-Path $PSScriptRoot 'ParallelHost.psm1') -Force

if (-not $Port -or -not $SeedPath -or -not $LogPath) {
    Import-Module (Join-Path $PSScriptRoot 'ParallelInstance.psm1') -Force
    $definitionPath = Join-Path $PSScriptRoot 'installed-instance.json'
    if (-not (Test-Path -LiteralPath $definitionPath -PathType Leaf)) {
        throw "No installed instance definition at $definitionPath; pass -Port, -SeedPath and -LogPath."
    }
    $definition = Get-Content -LiteralPath $definitionPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable -Depth 12
    $layout = Get-ParallelInstanceLayout -Definition $definition
    if (-not $Port) { $Port = [int] $definition['fakeMesIngest']['port'] }
    if (-not $SeedPath) { $SeedPath = $layout.SeedPath }
    if (-not $LogPath) { $LogPath = $layout.FakeLogPath }
}

$readBack = Invoke-ParallelFakeMesIngestSeed -Port $Port -SeedPath $SeedPath -LogPath $LogPath -ReadyTimeoutSeconds $ReadyTimeoutSeconds
Write-Host $readBack
