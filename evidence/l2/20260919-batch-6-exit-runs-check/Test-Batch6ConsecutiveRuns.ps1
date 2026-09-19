#Requires -Version 7

<#
The batch-6 exit's check on .github/workflows/l2.yml (control-server#165), written before the change it
checks so that it has been seen red.

Specification 19.7 says the exit ticket turns its batch's scenarios into `Runs = 3` and adjusts the job
timeout. This evaluates the workflow's own `$scenarios` literal -- the same text the runner executes, not a
copy of it -- and asserts:

1. The batch-6 lines are exactly the eight scenarios the exit report maps onto the six mechanism criteria
   of specification 8.3 (a new or renamed batch-6 scenario must be looked at, not silently tripled).
2. Each has `Runs = 3`, so `mode=consecutive` runs it three times in a row, not only `consecutive-all`.
3. None has `DefaultRuns`: the push/pull_request round stays one run each, which is what keeps a pull
   request's L2 job inside its 120-minute timeout.
4. The job timeout still gives a dispatched run at least 180 minutes: the exit's consecutive-all round runs
   every scenario three times and a timeout must never be what ends it (a cancel wedges the runner).

Exit 0 when all hold, 1 otherwise. Run from anywhere:
    pwsh -File evidence/l2/20260919-batch-6-exit-runs-check/Test-Batch6ConsecutiveRuns.ps1 [-Workflow <path>]
#>
param(
    [string]$Workflow = (Join-Path $PSScriptRoot '../../../.github/workflows/l2.yml')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSStyle.OutputRendering = 'PlainText'

$expected = @(
    'area-eqp-unique-across-task-types'              # criterion 3, control-server#160
    'binding-hold-dashboard-not-cascading'           # criterion 5, control-server#162
    'catalog-change-binding-hold'                    # criterion 5, control-server#162
    'staging-to-wire-reversed-journey'               # criterion 6, control-server#163
    'staging-to-wire-slot-group'                     # criterion 6, control-server#163
    'task-type-binding-admits-bound-station'         # criterion 2, control-server#160
    'task-type-binding-missing-not-cascading'        # criterion 1, control-server#160
    'task-type-binding-station-reused-refuses-start' # criterion 4, control-server#159
) | Sort-Object

$text = Get-Content -LiteralPath $Workflow -Raw
$match = [regex]::Match($text, '(?ms)^(?<indent>[ ]*)\$scenarios = @\(\r?\n(?<body>.*?)^\k<indent>\)')
if (-not $match.Success) { throw "No `$scenarios = @( ... ) literal found in $Workflow" }
$scenarios = & ([scriptblock]::Create("@(`n$($match.Groups['body'].Value)`n)"))

$failures = [Collections.Generic.List[string]]::new()
$batch6 = @($scenarios | Where-Object { $_['BatchId'] -eq 'batch-6' })
$names = @($batch6 | ForEach-Object Name | Sort-Object)

$missing = @($expected | Where-Object { $_ -notin $names })
$extra = @($names | Where-Object { $_ -notin $expected })
if ($missing) { $failures.Add("batch-6 scenarios missing from l2.yml: $($missing -join ', ')") }
if ($extra) { $failures.Add("batch-6 scenarios not in the exit report's criteria table: $($extra -join ', ')") }

foreach ($scenario in $batch6) {
    $runs = $scenario.Runs
    $default = $scenario.ContainsKey('DefaultRuns') ? $scenario.DefaultRuns : $null
    $verdict = ($runs -eq 3 -and $null -eq $default) ? 'ok' : 'FAIL'
    Write-Host ("{0,-48} Runs={1} DefaultRuns={2} {3}" -f $scenario.Name, $runs, ($default ?? '-'), $verdict)
    if ($runs -ne 3) { $failures.Add("$($scenario.Name): Runs = $runs, expected 3") }
    if ($null -ne $default) { $failures.Add("$($scenario.Name): DefaultRuns = $default, expected none") }
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

Write-Host "batch-6 scenarios: $($batch6.Count); all scenarios: $($scenarios.Count)"
if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "FAIL: $_" }
    exit 1
}
Write-Host 'PASS'
exit 0
