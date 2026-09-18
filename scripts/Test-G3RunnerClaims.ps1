#Requires -Version 7

<#
.SYNOPSIS
    Offline check that each of the four G3 runners and its claim in g3-slice-evidence.ps1 agree, in both
    directions, without running anything.

.DESCRIPTION
    Assert-G3ClaimCoversReport already guards this at the end of every G3 run -- which is up to half an
    hour of rebuilt peers and WPF windows away from the edit that broke it. This reads the same two
    halves from source instead and hands them to the same guard:

      - every assertion name a runner can report, read from its source:
          run-staged-g3.ps1                    the keys of its $assertionReport literal;
          run-staged-g3-restart.ps1            the keys of its $assertions literal;
          run-demand-bearing-g3-vectors.ps1    the keys of its $assertions literal;
          run-journey-g3.ps1                   the values of its $scenarioAssertions table (one per
                                               scenario judgment) plus every $assertions['<name>'] it
                                               assigns;
      - the runner's claim, from Get-G3RunnerClaim.

    For the journey runner it also checks what the guard cannot see: every L2 id in the table is written
    by its scenario script, and no L2 id or G3 name is mapped twice.

    Nothing is started, cloned or written. Exit 0 when every runner agrees with its claim; a thrown error
    naming the drift otherwise.
#>
[CmdletBinding()]
param(
    [string]$ScriptRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

function Get-RunnerAst([string]$Path) {
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) { throw "The runner does not parse: $Path" }
    return $ast
}

# The keys of the one top-level hashtable literal assigned to $<Variable>.
function Get-LiteralKeys([System.Management.Automation.Language.Ast]$Ast, [string]$Variable, [string]$Path) {
    $assignments = @($Ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $node.Left.VariablePath.UserPath -eq $Variable -and
                @($node.Right.FindAll({ param($n) $n -is [System.Management.Automation.Language.HashtableAst] }, $true)).Count -ge 1 -and
                $node.Right.Extent.Text -match '^\[ordered\]@\{\s*\S'
            }, $true))
    if ($assignments.Count -ne 1) { throw "Expected one non-empty `$$Variable literal in $Path, found $($assignments.Count)." }
    $table = @($assignments[0].Right.FindAll({ param($n) $n -is [System.Management.Automation.Language.HashtableAst] }, $true))[0]
    return @($table.KeyValuePairs | ForEach-Object { $_.Item1.Extent.Text.Trim("'", '"') })
}

function New-Report([string[]]$Names, [System.Collections.Generic.List[string]]$Problems, [string]$Runner) {
    $report = [ordered]@{}
    foreach ($name in $Names) {
        if ($report.Contains($name)) { $Problems.Add("$Runner reports '$name' twice.") }
        $report[$name] = 'PASS'
    }
    return $report
}

. (Join-Path $ScriptRoot 'g3-slice-evidence.ps1')

$problems = [System.Collections.Generic.List[string]]::new()
$reports = [ordered]@{}

# --- the three staged runners: the assertion table is a literal -----------------------------------
foreach ($entry in @(
        @{ File = 'run-staged-g3.ps1'; Variable = 'assertionReport'; RunKind = 'STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT' },
        @{ File = 'run-staged-g3-restart.ps1'; Variable = 'assertions'; RunKind = 'STAGED_G3_REAL_PEERS_PROCESS_RESTART_NO_MOVEMENT' },
        @{ File = 'run-demand-bearing-g3-vectors.ps1'; Variable = 'assertions'; RunKind = 'DEMAND_BEARING_G3_RESULT_AND_RIOT_UNKNOWN_VECTORS_NO_MOVEMENT' })) {
    $path = Join-Path $ScriptRoot $entry.File
    $names = Get-LiteralKeys (Get-RunnerAst $path) $entry.Variable $path
    $reports[$entry.RunKind] = New-Report $names $problems $entry.File
}

# --- the journey runner: scenario table plus run-wide assignments -------------------------------
$journeyPath = Join-Path $ScriptRoot 'run-journey-g3.ps1'
$scenarioRoot = Join-Path $ScriptRoot 'l2/scenarios'
$journeyAst = Get-RunnerAst $journeyPath
$tableAssignments = @($journeyAst.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $node.Left.VariablePath.UserPath -eq 'scenarioAssertions'
        }, $true))
if ($tableAssignments.Count -ne 1) { throw "Expected one `$scenarioAssertions assignment in $journeyPath, found $($tableAssignments.Count)." }
# The table is a pure literal, so its right-hand side can be evaluated on its own.
$scenarioAssertions = Invoke-Expression $tableAssignments[0].Right.Extent.Text
$journeyNames = [System.Collections.Generic.List[string]]::new()
$l2Ids = [System.Collections.Generic.List[string]]::new()
foreach ($scenario in $scenarioAssertions.Keys) {
    $scenarioPath = Join-Path $scenarioRoot "$scenario.ps1"
    if (-not (Test-Path -LiteralPath $scenarioPath)) {
        $problems.Add("run-journey-g3.ps1 drives '$scenario', which has no scenario script at $scenarioPath.")
        continue
    }
    $scenarioText = Get-Content -Raw -LiteralPath $scenarioPath
    foreach ($l2Id in $scenarioAssertions[$scenario].Keys) {
        $l2Ids.Add($l2Id)
        if ($scenarioText -notmatch "'$([regex]::Escape($l2Id))'") {
            $problems.Add("run-journey-g3.ps1 maps $l2Id of '$scenario', but that scenario script never writes it.")
        }
        $journeyNames.Add($scenarioAssertions[$scenario][$l2Id])
    }
}
foreach ($duplicate in @($l2Ids | Group-Object | Where-Object Count -GT 1 | ForEach-Object Name)) {
    $problems.Add("run-journey-g3.ps1 maps the L2 id $duplicate more than once.")
}
$journeyAst.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [System.Management.Automation.Language.IndexExpressionAst] -and
        $node.Left.Target -is [System.Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.Target.VariablePath.UserPath -eq 'assertions' -and
        $node.Left.Index -is [System.Management.Automation.Language.StringConstantExpressionAst]
    }, $true) | ForEach-Object { $journeyNames.Add($_.Left.Index.Value) }
$reports['JOURNEY_G3_REAL_ONBOARD_SIMULATED_COUNTERPARTS'] = New-Report $journeyNames $problems 'run-journey-g3.ps1'

if ($problems.Count -ne 0) { throw ($problems -join [Environment]::NewLine) }

foreach ($runKind in $reports.Keys) {
    Assert-G3ClaimCoversReport -RunKind $runKind -AssertionReport $reports[$runKind]
    $claim = Get-G3RunnerClaim -RunKind $runKind
    $counts = @($claim.slices.Keys | ForEach-Object { "$_ $(@($claim.slices[$_]).Count)" }) -join ', '
    "{0}: {1} reported, run-wide {2}; {3}" -f $runKind, $reports[$runKind].Count, @($claim.runWide).Count, $counts
}
"G3_CLAIMS_OK: every runner's reported names are attributed exactly once, and every attributed name is reported."
