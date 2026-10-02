#Requires -Version 7

<#
The batch-9 exit's check on .github/workflows/l2.yml (control-server#412), written before the change it
checks so that it has been seen red. Same shape as the batch-7 one
(evidence/l2/20260922-batch-7-exit-runs-check/Test-Batch7ConsecutiveRuns.ps1).

Specification 19.7 says the exit ticket turns its batch's scenarios into `Runs = 3` and adjusts the job
timeout. This evaluates the workflow's own `$scenarios` literal -- the same text the runner executes, not a
copy of it -- and asserts:

1. The batch-9 lines are exactly the twelve synthetic scenarios the batch-9 tickets merged. The list below
   was derived independently of l2.yml: every synthetic scenario file added by a batch-9 merge commit
   (`git diff --name-only --diff-filter=A <merge>^1 <merge> -- scripts/l2/scenarios` over PRs #414, #416,
   #415, #420, #427, #430, #433, #437, #441, #442, #445, #446). A new or renamed batch-9 scenario must be
   looked at, not silently tripled.
2. Every synthetic scenario file under scripts/l2/scenarios/ is listed somewhere in the literal. Point 1
   only sees what carries `BatchId = 'batch-9'`; a batch-9 scenario that was never registered, or was
   registered under another batch, would otherwise pass it by not being there.
3. Each batch-9 line has `Runs = 3`, so `mode=consecutive` runs it three times in a row, not only
   `consecutive-all`.
4. None has `DefaultRuns`: the push/pull_request round stays one run each, which is what keeps a pull
   request's L2 job inside its 120-minute timeout.
5. The job timeout still gives a dispatched run at least 180 minutes: the exit's consecutive-all round runs
   every scenario three times and a timeout must never be what ends it (a cancel wedges the runner).

Exit 0 when all hold, 1 otherwise. Prints the raw values, not only a verdict. Run from anywhere:
    pwsh -File evidence/l2/20261002-batch-9-exit-runs-check/Test-Batch9ConsecutiveRuns.ps1 [-Workflow <path>] [-ScenarioDirectory <path>]
#>
param(
    [string]$Workflow = (Join-Path $PSScriptRoot '../../../.github/workflows/l2.yml'),
    [string]$ScenarioDirectory = (Join-Path $PSScriptRoot '../../../scripts/l2/scenarios')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSStyle.OutputRendering = 'PlainText'

# Ordinal on purpose: scenario names are file names, and a case-insensitive comparison would let
# 'Charging-full-cycle' stand in for 'charging-full-cycle'.
$ordinal = [StringComparer]::Ordinal

$expected = [string[]]@(
    'charging-clearance-to-waiting-point'              # control-server#409
    'charging-full-cycle'                              # one charger registered, control-server#405
    'charging-full-vehicle-yields-charger'             # 1 charger 3 vehicles, the full one yields, control-server#405
    'charging-general-fault-does-not-pause'            # control-server#406
    'charging-interruption-isolates'                   # control-server#407
    'charging-no-progress-isolates'                    # control-server#407
    'charging-one-charger-three-vehicles-contend'      # 1 charger 3 vehicles contend, control-server#404
    'charging-policy-missing-vehicle-not-commissioned' # no approved policy, not commissioned, control-server#400
    'charging-registry-emptied-degrades-and-resumes'   # empty roster degrades to ManualChargingHold, control-server#404
    'charging-thresholds-relation-refused'             # control-server#403
    'charging-unable-to-charge-pauses-charger'         # and manual clearance, control-server#406
    'mandatory-charge-vehicle-takes-no-transport'      # control-server#403
)
[Array]::Sort($expected, $ordinal)

$text = Get-Content -LiteralPath $Workflow -Raw
# The first `$scenarios = @(` literal is the synthetic scenarios job; the real-rig job's list comes later
# in the file and carries no BatchId.
$match = [regex]::Match($text, '(?ms)^(?<indent>[ ]*)\$scenarios = @\(\r?\n(?<body>.*?)^\k<indent>\)')
if (-not $match.Success) { throw "No `$scenarios = @( ... ) literal found in $Workflow" }
$scenarios = @(& ([scriptblock]::Create("@(`n$($match.Groups['body'].Value)`n)")))

$failures = [Collections.Generic.List[string]]::new()
$listed = [Collections.Generic.HashSet[string]]::new([string[]]@($scenarios | ForEach-Object { [string]$_['Name'] }), $ordinal)
$batch9 = @($scenarios | Where-Object { $_.ContainsKey('BatchId') -and $_['BatchId'] -ceq 'batch-9' })
$names = [string[]]@($batch9 | ForEach-Object { [string]$_['Name'] })
[Array]::Sort($names, $ordinal)

$missing = @($expected | Where-Object { $names -cnotcontains $_ })
$extra = @($names | Where-Object { $expected -cnotcontains $_ })
if ($missing) { $failures.Add("batch-9 scenarios missing from l2.yml: $($missing -join ', ')") }
if ($extra) { $failures.Add("batch-9 scenarios not in the exit report's list: $($extra -join ', ')") }

# Synthetic scenario files: not the shared *Common.ps1 helpers, not the G3 journey scenarios (run by
# run-journey-g3.ps1), not the real-onboard rig (the real-rig job's own list).
$files = @(Get-ChildItem -LiteralPath $ScenarioDirectory -Filter '*.ps1' -File |
        ForEach-Object { $_.BaseName } |
        Where-Object { $_ -cnotmatch 'Common$' -and $_ -cnotmatch '^g3-' -and $_ -cnotmatch '^real-onboard-' })
$unlisted = @($files | Where-Object { -not $listed.Contains($_) })
if ($unlisted) { $failures.Add("synthetic scenario files not in l2.yml's scenario list: $($unlisted -join ', ')") }

foreach ($scenario in $batch9) {
    $runs = $scenario['Runs']
    $default = $scenario.ContainsKey('DefaultRuns') ? $scenario['DefaultRuns'] : $null
    $verdict = ($runs -eq 3 -and $null -eq $default) ? 'ok' : 'FAIL'
    Write-Host ("{0,-50} Runs={1} DefaultRuns={2} {3}" -f $scenario['Name'], $runs, ($default ?? '-'), $verdict)
    if ($runs -ne 3) { $failures.Add("$($scenario['Name']): Runs = $runs, expected 3") }
    if ($null -ne $default) { $failures.Add("$($scenario['Name']): DefaultRuns = $default, expected none") }
}

$timeout = [regex]::Match($text, "timeout-minutes:\s*\$\{\{\s*github\.event_name == 'workflow_dispatch' && (?<dispatch>\d+) \|\| (?<other>\d+)\s*\}\}")
if (-not $timeout.Success) {
    $failures.Add('timeout-minutes is no longer the dispatch/other expression this check reads')
} else {
    $dispatch = [int]$timeout.Groups['dispatch'].Value
    $other = [int]$timeout.Groups['other'].Value
    Write-Host "timeout-minutes: workflow_dispatch $dispatch, otherwise $other"
    if ($dispatch -lt 180) { $failures.Add("workflow_dispatch timeout $dispatch minutes, expected at least 180") }
}

Write-Host "expected batch-9: $($expected.Count); batch-9 lines: $($batch9.Count); all scenarios: $($scenarios.Count); synthetic scenario files: $($files.Count); unlisted files: $($unlisted.Count)"
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    exit 1
}
Write-Host 'PASS'
exit 0
