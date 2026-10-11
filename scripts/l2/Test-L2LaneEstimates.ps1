#Requires -Version 7

<#
.SYNOPSIS
    Self-check for Get-L2LaneEstimates.ps1: the lane estimates it reads out of CI logs (control-server#554).

.DESCRIPTION
    Offline, no gh, a second. Runs the script with -LogPath on lane-estimates-fixture/, four rounds' lines cut
    out of real `gh run view --log` output, and compares what it reports with numbers worked out by hand below.

    The fixture is recorded, not written: each file holds the lines of that run id's log that the script reads
    (the mode line, some scenario group lines, the summary line), unchanged. One line is added by hand, because
    no round in range failed a scenario: structural-block-oversized-demand's FAIL in 38102686787.log, in the
    format l2.yml prints. The four rounds:

    - 38103886702, 38102686787 -- default mode, ran to the end. These are the two the estimates come from.
    - 38101363125 -- default mode, superseded: a later push stopped it part way. Skipped.
    - 38060390711 -- consecutive-all, only the batch 10 scenarios. Skipped.

    Exits 1 when any case fails, and prints every case either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2LaneEstimates.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'Get-L2LaneEstimates.ps1'
$fixture = Join-Path $PSScriptRoot 'lane-estimates-fixture'
$logs = @('38103886702', '38102686787', '38101363125', '38060390711' | ForEach-Object { Join-Path $fixture "$_.log" })

$failures = @()
function Assert-Case {
    param([Parameter(Mandatory)][string]$Name,
          [Parameter(Mandatory)][bool]$Condition,
          [Parameter(Mandatory)][string]$Detail)
    Write-Host "$(if ($Condition) { 'PASS' } else { 'FAIL' })  $Name -- $Detail"
    if (-not $Condition) { $script:failures += $Name }
}

$result = $null
try {
    $result = & $script -LogPath $logs -EstimatesPath (Join-Path $fixture 'estimates.json')
} catch {
    Write-Host "Get-L2LaneEstimates.ps1 threw: $_"
}

# Worked by hand from the PASS lines of the two rounds that count. A median of an even count is the mean of
# the middle two, rounded half away from zero.
#   vehicle-full-still-appends-before-departure  223, 223                    -> 223
#   station-deadline-sublot-timeout              85, 86                      -> 85.5  -> 86
#   session-established-while-moving             32 33 32 | 32 32 32         -> 32, 32 -> 32 (six samples)
#   same-direction-die-to-oven-journey           101, 100                    -> 100.5 -> 101
#   create-gate                                  33, 26                      -> 29.5  -> 30
#   structural-block-oversized-demand            FAIL only                   -> no estimate
# Had the consecutive-all round counted, oven would be 99 99 100 101 102 -> 100; had the superseded one,
# session-established-while-moving would have nine samples.
$expected = [ordered]@{
    'vehicle-full-still-appends-before-departure' = 223
    'same-direction-die-to-oven-journey' = 101
    'station-deadline-sublot-timeout' = 86
    'session-established-while-moving' = 32
    'create-gate' = 30
}
$actual = if ($result) { $result.Estimates } else { [ordered]@{} }
$actualText = (@($actual.Keys | ForEach-Object { "$_=$($actual[$_])" }) -join ', ')
$expectedText = (@($expected.Keys | ForEach-Object { "$_=$($expected[$_])" }) -join ', ')
Assert-Case -Name 'medians-of-passing-runs-match-the-hand-worked-ones' `
    -Condition ($actualText -eq $expectedText) `
    -Detail "expected $expectedText; got $actualText"


# Which rounds counted and why the others did not, with each counted round's lanes: the sum of the run times
# printed for that lane (lane 1: 223 + 85 + 32 + 33 + 32 = 405 and lane 2: 101 + 33 = 134 in 38103886702;
# 223 + 86 + 32 + 32 + 32 = 405 and 100 + 26 + 41 = 167 in 38102686787, the FAIL counting toward its lane)
# and the round's wall clock from the summary line. That is the before/after comparison the ticket asks for.
$expectedRounds = '38103886702 counted 1702s lanes 405/134; 38102686787 counted 1731s lanes 405/167; ' +
    '38101363125 skipped (superseded); 38060390711 skipped (mode consecutive-all)'
$actualRounds = if ($result) {
    (@($result.Rounds | ForEach-Object {
        if ($_.Skipped) { "$($_.RunId) skipped ($($_.Skipped))" }
        else { "$($_.RunId) counted $($_.WallSeconds)s lanes $((@($_.Lanes | ForEach-Object Seconds)) -join '/')" }
    }) -join '; ')
}
Assert-Case -Name 'rounds-say-whether-they-counted-with-lane-and-wall-seconds' `
    -Condition ($actualRounds -eq $expectedRounds) `
    -Detail "expected $expectedRounds; got $actualRounds"


# Against the fixture's table (vehicle-full-still-appends-before-departure, create-gate, retired-scenario):
# ran in a counted round but not in the table -- the scenario whose only run failed included, since it ran --
# and in the table but run in no counted round. Scenarios only the skipped rounds ran do not count as run.
$expectedRan = 'same-direction-die-to-oven-journey, session-established-while-moving, station-deadline-sublot-timeout, structural-block-oversized-demand'
$actualRan = if ($result) { @($result.RanNotRegistered) -join ', ' }
Assert-Case -Name 'ran-but-not-in-the-table' -Condition ($actualRan -eq $expectedRan) `
    -Detail "expected $expectedRan; got $actualRan"
$actualListed = if ($result) { @($result.RegisteredNotRan) -join ', ' }
Assert-Case -Name 'in-the-table-but-not-run' -Condition ($actualListed -eq 'retired-scenario') `
    -Detail "expected retired-scenario; got $actualListed"

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("L2_LANE_ESTIMATES_SELFTEST_FAILED: $($failures -join ', ')")
    exit 1
}
Write-Host 'L2_LANE_ESTIMATES_SELFTEST_PASSED'
exit 0
