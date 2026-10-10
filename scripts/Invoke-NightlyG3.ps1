#Requires -Version 7

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$WorkRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ControlServerCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$OnboardCommit,
    [Parameter(Mandatory)][DateTimeOffset]$StartDeadlineUtc,
    [string]$RunnerRoot = $PSScriptRoot,
    [double]$CommitCeilingGiB = 12,
    [double]$CommitWaitMinutes = 30,
    [double]$MinFreeGiB = 4
)
exit 0
