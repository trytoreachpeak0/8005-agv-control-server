#Requires -Version 7

<#
.SYNOPSIS
    Offline check that run-journey-g3.ps1 and its claim in g3-slice-evidence.ps1 agree, in both
    directions, without running a single scenario.

.DESCRIPTION
    Assert-G3ClaimCoversReport already guards this at the end of every journey G3 run -- which is
    twenty minutes of two WPF windows away from the edit that broke it. This reads the same two
    halves from source instead:

      - every G3 assertion name the runner can report: the values of its $scenarioAssertions table
        (one per scenario judgment) and every run-wide name it assigns as $assertions['<name>'];
      - the claim for JOURNEY_G3_REAL_ONBOARD_SIMULATED_COUNTERPARTS.

    It hands the first to the guard as a report and lets the guard throw. Nothing is started, cloned
    or written. It also checks what the guard cannot see: that every L2 id in the runner's table is
    written by its scenario script, and that no L2 id is mapped twice.

    Exit 0 when the two agree; a thrown error naming the drift otherwise.
#>
[CmdletBinding()]
param(
    [string]$Runner = (Join-Path $PSScriptRoot 'run-journey-g3.ps1'),
    [string]$ScenarioRoot = (Join-Path $PSScriptRoot 'l2/scenarios')
)

$ErrorActionPreference = 'Stop'

$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Runner, [ref]$tokens, [ref]$parseErrors)
if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) { throw "The runner does not parse: $Runner" }

# The table is a pure literal, so its right-hand side can be evaluated on its own.
$tableAssignments = @($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $node.Left.VariablePath.UserPath -eq 'scenarioAssertions'
        }, $true))
if ($tableAssignments.Count -ne 1) { throw "Expected one `$scenarioAssertions assignment in $Runner, found $($tableAssignments.Count)." }
$scenarioAssertions = Invoke-Expression $tableAssignments[0].Right.Extent.Text

# Run-wide names: every $assertions['<literal>'] = ... in the runner.
$runWide = @($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left -is [System.Management.Automation.Language.IndexExpressionAst] -and
            $node.Left.Target -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $node.Left.Target.VariablePath.UserPath -eq 'assertions' -and
            $node.Left.Index -is [System.Management.Automation.Language.StringConstantExpressionAst]
        }, $true) | ForEach-Object { $_.Left.Index.Value })

$report = [ordered]@{}
$problems = [System.Collections.Generic.List[string]]::new()
$l2Ids = [System.Collections.Generic.List[string]]::new()
foreach ($scenario in $scenarioAssertions.Keys) {
    $scenarioPath = Join-Path $ScenarioRoot "$scenario.ps1"
    if (-not (Test-Path -LiteralPath $scenarioPath)) {
        $problems.Add("The runner drives '$scenario', which has no scenario script at $scenarioPath.")
        continue
    }
    $scenarioText = Get-Content -Raw -LiteralPath $scenarioPath
    foreach ($l2Id in $scenarioAssertions[$scenario].Keys) {
        $l2Ids.Add($l2Id)
        # A judgment is written as the first argument of $assertions.Add, quoted.
        if ($scenarioText -notmatch "'$([regex]::Escape($l2Id))'") {
            $problems.Add("The runner maps $l2Id of '$scenario', but that scenario script never writes it.")
        }
        $name = $scenarioAssertions[$scenario][$l2Id]
        if ($report.Contains($name)) { $problems.Add("The runner maps two judgments to the G3 assertion '$name'.") }
        $report[$name] = 'PASS'
    }
}
foreach ($name in $runWide) { $report[$name] = 'PASS' }
foreach ($duplicate in @($l2Ids | Group-Object | Where-Object Count -GT 1 | ForEach-Object Name)) {
    $problems.Add("The L2 id $duplicate is mapped more than once.")
}
if ($problems.Count -ne 0) { throw ($problems -join [Environment]::NewLine) }

. (Join-Path $PSScriptRoot 'g3-slice-evidence.ps1')
$runKind = 'JOURNEY_G3_REAL_ONBOARD_SIMULATED_COUNTERPARTS'
Assert-G3ClaimCoversReport -RunKind $runKind -AssertionReport $report

$claim = Get-G3RunnerClaim -RunKind $runKind
foreach ($slice in $claim.slices.Keys) {
    "{0}  {1,3} assertions" -f $slice, @($claim.slices[$slice]).Count
}
"run-wide {0,3} assertions" -f @($claim.runWide).Count
"JOURNEY_G3_CLAIM_OK: $($report.Count) reported names, every one attributed exactly once, every attributed name reported."
