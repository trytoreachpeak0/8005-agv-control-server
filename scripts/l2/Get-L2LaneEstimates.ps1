#Requires -Version 7

<#
.SYNOPSIS
    Reads how long each synthetic L2 scenario took in past CI rounds and works out one run's estimate per
    scenario, for the lane estimate table (control-server#554).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = 'Run')][long[]]$RunId,
    [Parameter(Mandatory, ParameterSetName = 'Log')][string[]]$LogPath,
    [string]$EstimatesPath = (Join-Path $PSScriptRoot 'l2-lane-estimates.json'),
    [switch]$AsJson
)

throw 'Not implemented'
