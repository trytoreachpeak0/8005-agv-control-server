#Requires -Version 7

<#
The batch-7 exit's check on .github/workflows/l2.yml (control-server#220), written before the change it
checks so that it has been seen red. Same shape as the batch-6 one
(evidence/l2/20260919-batch-6-exit-runs-check/Test-Batch6ConsecutiveRuns.ps1).

Specification 19.7 says the exit ticket turns its batch's scenarios into `Runs = 3` and adjusts the job
timeout. This evaluates the workflow's own `$scenarios` literal -- the same text the runner executes, not a
copy of it -- and asserts:

1. The batch-7 lines are exactly the nineteen synthetic scenarios the batch-7 tickets merged (every
   synthetic scenario file added between the batch-6 exit e74c0058 and the batch-7 exit base 517e1c7a).
   A new or renamed batch-7 scenario must be looked at, not silently tripled.
2. Every synthetic scenario file under scripts/l2/scenarios/ is listed somewhere in the literal. Point 1
   only sees what carries `BatchId = 'batch-7'`; a batch-7 scenario that was never registered, or was
   registered under another batch, would otherwise pass it by not being there.
3. Each batch-7 line has `Runs = 3`, so `mode=consecutive` runs it three times in a row, not only
   `consecutive-all`.
4. None has `DefaultRuns`: the push/pull_request round stays one run each, which is what keeps a pull
   request's L2 job inside its 120-minute timeout.
5. The job timeout still gives a dispatched run at least 180 minutes: the exit's consecutive-all round runs
   every scenario three times and a timeout must never be what ends it (a cancel wedges the runner).

Exit 0 when all hold, 1 otherwise. Prints the raw values, not only a verdict. Run from anywhere:
    pwsh -File evidence/l2/20260922-batch-7-exit-runs-check/Test-Batch7ConsecutiveRuns.ps1 [-Workflow <path>] [-ScenarioDirectory <path>]
#>
param(
    [string]$Workflow = (Join-Path $PSScriptRoot '../../../.github/workflows/l2.yml'),
    [string]$ScenarioDirectory = (Join-Path $PSScriptRoot '../../../scripts/l2/scenarios')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSStyle.OutputRendering = 'PlainText'

# Ordinal on purpose: scenario names are file names and case matters to nobody but a comparison that
# would otherwise let 'Cargo-holding-timeout' stand in for 'cargo-holding-timeout'.
$ordinal = [StringComparer]::Ordinal

$expected = [string[]]@(
    'cargo-holding-dashboard-projection'           # control-server#217
    'cargo-holding-disabled-when-append-forbidden' # criterion 4, control-server#212
    'cargo-holding-side-full'                      # criterion 1, control-server#212
    'cargo-holding-timeout'                        # criterion 2 (and 6, second half), control-server#212
    'dispatch-zone-parameters-import-rejects'      # control-server#216
    'en-route-append-delay-gate'                   # FP-IS-08, control-server#211
    'en-route-append-not-configured'               # FP-IS-08, control-server#211
    'idle-and-inflight-vehicles-compete'           # FP-IS-08, control-server#211
    'multi-stop-append-same-zone'                  # FP-IS-08, control-server#211
    'onboard-silent-liveness-loss'                 # control-server#234
    'plan-reorder-and-drop-cancelled'              # control-server#215
    'reassign-when-vehicle-ineligible'             # control-server#215
    'starvation-threshold-escalation'              # control-server#214
    'task-type-priority-band'                      # control-server#214
    'transport-demand-key-suppressed'              # criterion 7, control-server#210
    'vehicle-full-ignores-oversized-and-gated'     # criterion 5, control-server#212
    'vehicle-full-still-appends-before-departure'  # criterion 6, control-server#212
    'waiting-station-yield'                        # criterion 3 (and 6, second half), control-server#213
    'waiting-station-yield-waits-for-door'         # criterion 3, control-server#213
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
$batch7 = @($scenarios | Where-Object { $_.ContainsKey('BatchId') -and $_['BatchId'] -ceq 'batch-7' })
$names = [string[]]@($batch7 | ForEach-Object { [string]$_['Name'] })
[Array]::Sort($names, $ordinal)

$missing = @($expected | Where-Object { $names -cnotcontains $_ })
$extra = @($names | Where-Object { $expected -cnotcontains $_ })
if ($missing) { $failures.Add("batch-7 scenarios missing from l2.yml: $($missing -join ', ')") }
if ($extra) { $failures.Add("batch-7 scenarios not in the exit report's list: $($extra -join ', ')") }

# Synthetic scenario files: not the shared *Common.ps1 helpers, not the G3 journey scenarios (run by
# run-journey-g3.ps1), not the real-onboard rig (the real-rig job's own list).
$files = @(Get-ChildItem -LiteralPath $ScenarioDirectory -Filter '*.ps1' -File |
        ForEach-Object { $_.BaseName } |
        Where-Object { $_ -cnotmatch 'Common$' -and $_ -cnotmatch '^g3-' -and $_ -cnotmatch '^real-onboard-' })
$unlisted = @($files | Where-Object { -not $listed.Contains($_) })
if ($unlisted) { $failures.Add("synthetic scenario files not in l2.yml's scenario list: $($unlisted -join ', ')") }

foreach ($scenario in $batch7) {
    $runs = $scenario['Runs']
    $default = $scenario.ContainsKey('DefaultRuns') ? $scenario['DefaultRuns'] : $null
    $verdict = ($runs -eq 3 -and $null -eq $default) ? 'ok' : 'FAIL'
    Write-Host ("{0,-46} Runs={1} DefaultRuns={2} {3}" -f $scenario['Name'], $runs, ($default ?? '-'), $verdict)
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

Write-Host "expected batch-7: $($expected.Count); batch-7 lines: $($batch7.Count); all scenarios: $($scenarios.Count); synthetic scenario files: $($files.Count); unlisted files: $($unlisted.Count)"
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    exit 1
}
Write-Host 'PASS'
exit 0
