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

$ErrorActionPreference = 'Stop'

# The lines l2.yml's scenarios job prints, as `gh run view --log` shows them (job, step and timestamp first).
$modePattern = 'Z L2 mode: (?<mode>[a-z-]+)(?<only>; only [^;]*)?; (?<lanes>\d+) lanes\s*$'
$runPattern = '##\[group\](?<name>[a-z0-9-]+)(?: \((?<n>\d+)/(?<of>\d+)\))? -- lane (?<lane>\d+), slot \d+, (?<result>PASS|FAIL[^,]*), (?<seconds>\d+)s\s*$'
$summaryPattern = 'Z ### L2 \((?<mode>[a-z-]+), (?<lanes>\d+) lanes, (?<wall>\d+)s\)(?<superseded> -- superseded)?'

function Read-L2Round([string]$Id, [string[]]$Lines) {
    $mode = $null; $only = $false; $wall = $null; $superseded = $false
    $runs = [Collections.Generic.List[object]]::new()
    foreach ($line in $Lines) {
        if ($null -eq $mode -and $line -match $modePattern) {
            $mode = $Matches.mode; $only = [bool]$Matches.only
        } elseif ($line -match $runPattern) {
            $runs.Add([pscustomobject]@{
                Scenario = $Matches.name; Lane = [int]$Matches.lane
                Pass = $Matches.result -eq 'PASS'; Seconds = [int]$Matches.seconds
            })
        } elseif ($line -match $summaryPattern) {
            $wall = [int]$Matches.wall; $superseded = [bool]$Matches.superseded
        }
    }
    $skip = if ($null -eq $mode) { 'no synthetic L2 scenarios job' }
        elseif ($mode -ne 'default') { "mode $mode" }
        elseif ($only) { 'only some scenarios' }
        elseif ($superseded) { 'superseded' }
        elseif ($null -eq $wall) { 'no summary line: the job did not finish' }
    [pscustomobject]@{ RunId = $Id; Mode = $mode; WallSeconds = $wall; Skipped = $skip; Runs = @($runs) }
}

function Get-Median([int[]]$Values) {
    $sorted = @($Values | Sort-Object)
    $middle = [Math]::Floor($sorted.Count / 2)
    $median = if ($sorted.Count % 2) { $sorted[$middle] } else { ($sorted[$middle - 1] + $sorted[$middle]) / 2 }
    [int][Math]::Round($median, [MidpointRounding]::AwayFromZero)
}

$rounds = @(foreach ($path in $LogPath) {
    Read-L2Round -Id ([IO.Path]::GetFileNameWithoutExtension($path)) -Lines (Get-Content -LiteralPath $path)
})
$counted = @($rounds | Where-Object { -not $_.Skipped })
$passing = @($counted | ForEach-Object Runs | Where-Object Pass)

$estimates = [ordered]@{}
$passing | Group-Object Scenario |
    ForEach-Object { [pscustomobject]@{ Name = $_.Name; Seconds = Get-Median @($_.Group.Seconds) } } |
    Sort-Object -Property @{ Expression = 'Seconds'; Descending = $true }, Name |
    ForEach-Object { $estimates[$_.Name] = $_.Seconds }

[pscustomobject]@{ Rounds = $rounds; Estimates = $estimates }
