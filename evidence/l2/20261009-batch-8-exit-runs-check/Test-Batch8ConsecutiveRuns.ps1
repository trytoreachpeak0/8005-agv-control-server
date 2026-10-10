#Requires -Version 7

<#
The batch-8 exit's check on .github/workflows/l2.yml (control-server#393), written before the change it
checks so that it has been seen red. Same shape as the batch-7 and batch-9 ones
(evidence/l2/20260922-batch-7-exit-runs-check/, evidence/l2/20261002-batch-9-exit-runs-check/).

Specification 19.7 says the exit ticket turns its batch's scenarios into `Runs = 3` and adjusts the job
timeout. This evaluates the workflow's own `$scenarios` literals -- the same text the runner executes, not a
copy of it -- and asserts:

1. The batch-8 lines of the synthetic list are exactly the seven synthetic scenarios the batch-8 tickets
   merged, including the two registered as batch 8 after the batch-7 exit (control-server#273, #186).
   A new or renamed batch-8 scenario must be looked at, not silently tripled.
2. Every synthetic scenario file under scripts/l2/scenarios/ is listed somewhere in the synthetic literal,
   except demand-bearing-store-at-unload: that one is the demand-bearing G3 runner's store generator
   (control-server#453), not an L2 criterion, and is deliberately absent from l2.yml.
3. Each batch-8 line has `Runs = 3` and no `DefaultRuns`, so `mode=consecutive` runs it three times in a
   row while a pull request's default round stays one run each.
4. The batch-8 real-rig scenario (real-onboard-slot-fault-declaration, control-server#383 with
   onboard-hmi#215) has `Runs = 3` in the real-rig literal.
5. The job timeout still gives a dispatched synthetic run at least 180 minutes, and some job (the real-rig
   one) keeps at least 360: a timeout must never be what ends a run (a cancel wedges the runner).

Exit 0 when all hold, 1 otherwise. Prints the raw values, not only a verdict. Run from anywhere:
    pwsh -File evidence/l2/20261009-batch-8-exit-runs-check/Test-Batch8ConsecutiveRuns.ps1 [-Workflow <path>] [-ScenarioDirectory <path>]
#>
param(
    [string]$Workflow = (Join-Path $PSScriptRoot '../../../.github/workflows/l2.yml'),
    [string]$ScenarioDirectory = (Join-Path $PSScriptRoot '../../../scripts/l2/scenarios')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSStyle.OutputRendering = 'PlainText'

# Ordinal on purpose: scenario names are file names, and a case-insensitive match would let a renamed
# line stand in for the one this list names.
$ordinal = [StringComparer]::Ordinal

$expected = [string[]]@(
    'fixed-station-single-occupancy'                     # REQ-0204 revised, control-server#391
    'idle-return-two-vehicles-contend-one-waiting-point' # two vehicles, one waiting point, control-server#389
    'map-rename-holds-all-task-types'                    # REQ-0341, control-server#186 (after the batch-7 exit)
    'slot-fault-declaration'                             # REQ-0359, control-server#383
    'waiting-journey-battery-watch'                      # REQ-0169, control-server#273 (after the batch-7 exit)
    'waiting-point-exclusive-reserve-occupy-release'     # 8.3 batch-8 criterion 1, control-server#390
    'waiting-points-fewer-than-vehicles-refuses-start'   # 8.3 batch-8 criterion 2 (negative), control-server#388
)
[Array]::Sort($expected, $ordinal)
$expectedRealRig = 'real-onboard-slot-fault-declaration'
$notAnL2Criterion = 'demand-bearing-store-at-unload'

$text = Get-Content -LiteralPath $Workflow -Raw
# The first `$scenarios = @(` literal is the synthetic scenarios job, the second the real-rig job's.
$literals = [regex]::Matches($text, '(?ms)^(?<indent>[ ]*)\$scenarios = @\(\r?\n(?<body>.*?)^\k<indent>\)')
if ($literals.Count -ne 2) { throw "Expected two `$scenarios = @( ... ) literals in $Workflow (synthetic, real-rig), found $($literals.Count)" }
$scenarios = @(& ([scriptblock]::Create("@(`n$($literals[0].Groups['body'].Value)`n)")))
$realRig = @(& ([scriptblock]::Create("@(`n$($literals[1].Groups['body'].Value)`n)")))

$failures = [Collections.Generic.List[string]]::new()
$listed = [Collections.Generic.HashSet[string]]::new([string[]]@($scenarios | ForEach-Object { [string]$_['Name'] }), $ordinal)
$batch8 = @($scenarios | Where-Object { $_.ContainsKey('BatchId') -and $_['BatchId'] -ceq 'batch-8' })
$names = [string[]]@($batch8 | ForEach-Object { [string]$_['Name'] })
[Array]::Sort($names, $ordinal)

$missing = @($expected | Where-Object { $names -cnotcontains $_ })
$extra = @($names | Where-Object { $expected -cnotcontains $_ })
if ($missing) { $failures.Add("batch-8 scenarios missing from l2.yml: $($missing -join ', ')") }
if ($extra) { $failures.Add("batch-8 scenarios not in the exit report's list: $($extra -join ', ')") }

# Synthetic scenario files: not the shared *Common.ps1 helpers, not the G3 journey scenarios (run by
# run-journey-g3.ps1), not the real-onboard rig (the real-rig job's own list).
$files = @(Get-ChildItem -LiteralPath $ScenarioDirectory -Filter '*.ps1' -File |
        ForEach-Object { $_.BaseName } |
        Where-Object { $_ -cnotmatch 'Common$' -and $_ -cnotmatch '^g3-' -and $_ -cnotmatch '^real-onboard-' })
if ($files -cnotcontains $notAnL2Criterion) { $failures.Add("$notAnL2Criterion.ps1 is gone; this check's exemption is stale") }
if ($listed.Contains($notAnL2Criterion)) { $failures.Add("$notAnL2Criterion is listed in l2.yml; it is a G3 store generator, not an L2 criterion") }
$unlisted = @($files | Where-Object { $_ -cne $notAnL2Criterion -and -not $listed.Contains($_) })
if ($unlisted) { $failures.Add("synthetic scenario files not in l2.yml's scenario list: $($unlisted -join ', ')") }

foreach ($scenario in $batch8) {
    $runs = $scenario['Runs']
    $default = $scenario.ContainsKey('DefaultRuns') ? $scenario['DefaultRuns'] : $null
    $verdict = ($runs -eq 3 -and $null -eq $default) ? 'ok' : 'FAIL'
    Write-Host ("{0,-52} Runs={1} DefaultRuns={2} {3}" -f $scenario['Name'], $runs, ($default ?? '-'), $verdict)
    if ($runs -ne 3) { $failures.Add("$($scenario['Name']): Runs = $runs, expected 3") }
    if ($null -ne $default) { $failures.Add("$($scenario['Name']): DefaultRuns = $default, expected none") }
}

$rig = @($realRig | Where-Object { [string]$_['Name'] -ceq $expectedRealRig })
if ($rig.Count -ne 1) {
    $failures.Add("real-rig list has $($rig.Count) lines named $expectedRealRig, expected 1")
} else {
    $rigRuns = $rig[0]['Runs']
    Write-Host ("{0,-52} Runs={1} (real-rig) {2}" -f $expectedRealRig, $rigRuns, (($rigRuns -eq 3) ? 'ok' : 'FAIL'))
    if ($rigRuns -ne 3) { $failures.Add("real-rig $($expectedRealRig): Runs = $rigRuns, expected 3") }
}

$timeout = [regex]::Match($text, "timeout-minutes:\s*\$\{\{\s*github\.event_name == 'workflow_dispatch' && (?<dispatch>\d+) \|\| (?<other>\d+)\s*\}\}")
if (-not $timeout.Success) {
    $failures.Add('timeout-minutes is no longer the dispatch/other expression this check reads')
} else {
    $dispatch = [int]$timeout.Groups['dispatch'].Value
    Write-Host "timeout-minutes (scenarios): workflow_dispatch $dispatch, otherwise $($timeout.Groups['other'].Value)"
    if ($dispatch -lt 180) { $failures.Add("workflow_dispatch timeout $dispatch minutes, expected at least 180") }
}
$plain = @([regex]::Matches($text, '(?m)^\s*timeout-minutes:\s*(?<n>\d+)\s*$') | ForEach-Object { [int]$_.Groups['n'].Value })
Write-Host "plain timeout-minutes values: $($plain -join ', ')"
if (-not ($plain | Where-Object { $_ -ge 360 })) { $failures.Add('no job keeps a timeout of at least 360 minutes (the real-rig job)') }

Write-Host "expected batch-8: $($expected.Count); batch-8 lines: $($batch8.Count); all synthetic scenarios: $($scenarios.Count); real-rig scenarios: $($realRig.Count); synthetic scenario files: $($files.Count); unlisted files: $($unlisted.Count)"
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    exit 1
}
Write-Host 'PASS'
exit 0
