#Requires -Version 7

<#
.SYNOPSIS
    Reads how long each synthetic L2 scenario took in past CI rounds and works out one run's estimate per
    scenario, for the lane estimate table (control-server#554).

.DESCRIPTION
    l2.yml deals its scenarios out to lanes longest first by scripts/l2/l2-lane-estimates.json; a scenario not
    in it counts 30 seconds. Hand-kept, that table went stale within three weeks: the 2026-09-17 medians ran
    1.3 to 2.3 times short by 2026-10-10 and most scenarios added since were never entered, so one lane ran
    400 seconds longer than another. This script measures instead of remembering.

    Every scenario run prints one group line in the scenarios job's log,
    `::group::<label> -- lane <n>, slot <n>, PASS|FAIL (<code>), <seconds>s`, and the round ends with a summary
    line `### L2 (<mode>, <n> lanes, <wall>s)`. A round counts only when it ran in default mode, every
    scenario, to the end: a consecutive or an `only` round loads the lanes differently, and a superseded one
    stopped part way. Of a counted round only PASS runs give a time; a failing run may have stopped early or
    waited out a timeout. Each scenario's estimate is the median of its passing runs over all counted rounds,
    rounded half away from zero.

    Read-only: it reads logs through `gh run view --log` (one at a time, three tries each) or from files, and
    writes nothing. To update the table, redirect -AsJson's output over the data file and review the diff.

    Self-check: scripts/l2/Test-L2LaneEstimates.ps1.

.PARAMETER RunId
    l2.yml run ids to read. Rounds that do not count are reported with the reason and left out.

.PARAMETER LogPath
    Saved `gh run view --log` output instead, one file per round; the file name without extension is the run id.

.PARAMETER EstimatesPath
    The table to compare against, for the two lists: scenarios run but not in it, and in it but not run.

.PARAMETER AsJson
    Print the data file's whole new content instead of the result object.

.PARAMETER Report
    Also print the rounds, their lanes and the two lists as Markdown, for a pull request.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/l2/Get-L2LaneEstimates.ps1 -RunId 38103886702, 38102686787 -Report

.EXAMPLE
    pwsh -NoProfile -File ./scripts/l2/Get-L2LaneEstimates.ps1 -RunId 38103886702, 38102686787 -AsJson |
        Set-Content ./scripts/l2/l2-lane-estimates.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = 'Run')][long[]]$RunId,
    [Parameter(Mandatory, ParameterSetName = 'Log')][string[]]$LogPath,
    [string]$EstimatesPath = (Join-Path $PSScriptRoot 'l2-lane-estimates.json'),
    [string]$Repository = 'trytoreachpeak0/8005-agv-control-server',
    [switch]$AsJson,
    [switch]$Report
)

$ErrorActionPreference = 'Stop'

# The lines l2.yml's scenarios job prints, as `gh run view --log` shows them (job, step and timestamp first).
# The real-onboard job's group lines carry no lane and are not matched.
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
    # A lane runs its scenarios one after another, so the sum of its run times is about how long it took.
    $lanes = @($runs | Group-Object Lane | Sort-Object { [int]$_.Name } | ForEach-Object {
        [pscustomobject]@{ Lane = [int]$_.Name; Seconds = ($_.Group | Measure-Object Seconds -Sum).Sum; Runs = $_.Count }
    })
    [pscustomobject]@{ RunId = $Id; Mode = $mode; WallSeconds = $wall; Skipped = $skip; Lanes = $lanes; Runs = @($runs) }
}

function Get-Median([int[]]$Values) {
    $sorted = @($Values | Sort-Object)
    $middle = [Math]::Floor($sorted.Count / 2)
    $median = if ($sorted.Count % 2) { $sorted[$middle] } else { ($sorted[$middle - 1] + $sorted[$middle]) / 2 }
    [int][Math]::Round($median, [MidpointRounding]::AwayFromZero)
}

function Get-RunLog([long]$Id) {
    # One at a time: the proxy in front of github.com drops connections under parallel downloads.
    for ($try = 1; ; $try++) {
        $lines = & gh run view $Id -R $Repository --log 2>$null
        if ($LASTEXITCODE -eq 0) { return $lines }
        if ($try -ge 3) { throw "gh run view $Id --log failed three times (exit $LASTEXITCODE)" }
        Start-Sleep -Seconds (15 * $try)
    }
}

$rounds = @(if ($PSCmdlet.ParameterSetName -eq 'Run') {
    foreach ($id in $RunId) { Read-L2Round -Id "$id" -Lines (Get-RunLog $id) }
} else {
    foreach ($path in $LogPath) {
        Read-L2Round -Id ([IO.Path]::GetFileNameWithoutExtension($path)) -Lines (Get-Content -LiteralPath $path)
    }
})
$counted = @($rounds | Where-Object { -not $_.Skipped })
$passing = @($counted | ForEach-Object Runs | Where-Object Pass)

$estimates = [ordered]@{}
$passing | Group-Object Scenario |
    ForEach-Object { [pscustomobject]@{ Name = $_.Name; Seconds = Get-Median @($_.Group.Seconds) } } |
    Sort-Object -Property @{ Expression = 'Seconds'; Descending = $true }, Name |
    ForEach-Object { $estimates[$_.Name] = $_.Seconds }

# The table this would replace, to say what it lacks and what it lists for nothing.
$table = (Get-Content -Raw -LiteralPath $EstimatesPath | ConvertFrom-Json -AsHashtable).estimates
$ran = @($counted | ForEach-Object Runs | ForEach-Object Scenario | Sort-Object -Unique)
$ranNotRegistered = @($ran | Where-Object { -not $table.Contains($_) })
$registeredNotRan = @($table.Keys | Where-Object { $_ -notin $ran } | Sort-Object)

if ($Report) {
    Write-Host '| Run | Counted | Wall | Lanes (sum of run seconds) |'
    Write-Host '| --- | --- | --- | --- |'
    foreach ($round in $rounds) {
        $laneText = (@($round.Lanes | ForEach-Object { "$($_.Lane): $($_.Seconds)s" }) -join ', ')
        $countedText = if ($round.Skipped) { "no, $($round.Skipped)" } else { 'yes' }
        Write-Host "| $($round.RunId) | $countedText | $($round.WallSeconds)s | $laneText |"
    }
    Write-Host ''
    Write-Host "Ran but not in the table ($($ranNotRegistered.Count)): $($ranNotRegistered -join ', ')"
    Write-Host "In the table but not run ($($registeredNotRan.Count)): $($registeredNotRan -join ', ')"
}

if ($AsJson) {
    $ids = @($counted | ForEach-Object RunId) -join ', '
    [ordered]@{
        source = "Medians of the passing runs in l2.yml rounds $ids (default mode, every scenario, not superseded), " +
            "read $([DateTime]::Now.ToString('yyyy-MM-dd')) by scripts/l2/Get-L2LaneEstimates.ps1."
        estimates = $estimates
    } | ConvertTo-Json -Depth 3
    return
}

[pscustomobject]@{
    Rounds = $rounds; Estimates = $estimates
    RanNotRegistered = $ranNotRegistered; RegisteredNotRan = $registeredNotRan
}
