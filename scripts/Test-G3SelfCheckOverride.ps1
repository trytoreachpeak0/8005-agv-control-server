#Requires -Version 7

<#
.SYNOPSIS
    Offline self-check of the -SelfCheckControlServerCommit / -SelfCheckOnboardCommit parameters of
    run-staged-g3.ps1 and run-staged-g3-restart.ps1 (control-server#582).

.DESCRIPTION
    A second or two: no build, no peer, no window, nothing written outside this process.

    The nightly G3 (.github/workflows/g3.yml) runs all four runners at the integration branch's tip without
    moving the commit binding, which is deliberate and moves only in an exit ticket's first step. Journey and
    demand-bearing already had the override; this checks the two staged runners got the same shape:

      1. Each runner declares both parameters, each validated as a lowercase full SHA-1.
      2. Each runner's own statements -- its top-level $commitSources assignment and every top-level `if` on a
         -SelfCheck* parameter, in their order -- are run from its AST:
           - with no override: the four commits are the binding read off run-staged-g3.ps1's param defaults, all
             four sources SHARED_BINDING, and an all-PASS slice is still a formal pass;
           - with each override: that commit is the override, its source SELF_CHECK_OVERRIDE and the other three
             SHARED_BINDING, and an all-PASS slice is withheld as SELF_CHECK_OVERRIDE;
           - with the override equal to the bound commit: still SELF_CHECK_OVERRIDE, as in run-journey-g3.ps1.
             A run that asked for an override is not gate evidence, whatever commit it named.
      3. The override branches run before the first top-level statement that touches $StageRoot, so nothing has
         been staged from the bound commits when they apply.
      4. The restart runner still reads its binding from run-staged-g3.ps1 by AST, through a path that is not a
         parameter: an override port for the binding itself would be a drift port.

    Exits 1 when any check comes out the other way, and prints every check either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\Test-G3SelfCheckOverride.ps1
#>
[CmdletBinding()]
param(
    [string]$ScriptRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

$failures = [System.Collections.Generic.List[string]]::new()
function Check([string]$name, [bool]$ok, [string]$detail) {
    Write-Host ("{0} {1}{2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $(if ($ok) { '' } else { " -- $detail" }))
    if (-not $ok) { $failures.Add($name) }
}

function Get-RunnerAst([string]$Path) {
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) { throw "The runner does not parse: $Path" }
    return $ast
}

. (Join-Path $ScriptRoot 'g3-slice-evidence.ps1')

$restartPath = Join-Path $ScriptRoot 'run-staged-g3-restart.ps1'
$stagedPath = Join-Path $ScriptRoot 'run-staged-g3.ps1'
$restartAst = Get-RunnerAst $restartPath
$bindingReader = @($restartAst.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-SharedCommitBinding'
        }, $true))
if ($bindingReader.Count -ne 1) { throw "Expected one Get-SharedCommitBinding in $restartPath, found $($bindingReader.Count)." }
. ([scriptblock]::Create($bindingReader[0].Extent.Text))
$binding = Get-SharedCommitBinding -Path $stagedPath

$sourceNames = [ordered]@{
    ControlServerCommit = 'controlServerCommitSource'; OnboardCommit = 'onboardCommitSource'
    SimulatorCommit = 'simulatorCommitSource'; ProtocolCommit = 'protocolCommitSource'
}
$overrides = [ordered]@{ SelfCheckControlServerCommit = 'ControlServerCommit'; SelfCheckOnboardCommit = 'OnboardCommit' }

$runners = [ordered]@{
    'run-staged-g3.ps1' = 'STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT'
    'run-staged-g3-restart.ps1' = 'STAGED_G3_REAL_PEERS_PROCESS_RESTART_NO_MOVEMENT'
}

foreach ($file in $runners.Keys) {
    $runKind = $runners[$file]
    $ast = Get-RunnerAst (Join-Path $ScriptRoot $file)

    # 1. The parameters.
    foreach ($name in $overrides.Keys) {
        $declared = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq $name })
        $pattern = @($declared | ForEach-Object { $_.Attributes } | Where-Object {
                $_ -is [System.Management.Automation.Language.AttributeAst] -and $_.TypeName.Name -eq 'ValidatePattern' } |
            ForEach-Object { $_.PositionalArguments[0].Value })
        Check "${file}: declares -$name, validated as a lowercase full SHA-1" `
            ($declared.Count -eq 1 -and $pattern.Count -eq 1 -and $pattern[0] -ceq '^[0-9a-f]{40}$') "$($declared.Count) declared, pattern '$($pattern -join ',')'"
    }

    # 2. The runner's own statements, in their order.
    $topLevel = @($ast.EndBlock.Statements)
    $statements = @($topLevel | Where-Object {
            ($_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
             $_.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
             $_.Left.VariablePath.UserPath -eq 'commitSources') -or
            ($_ -is [System.Management.Automation.Language.IfStatementAst] -and
             $_.Clauses[0].Item1.Extent.Text -match '\$SelfCheck\w*Commit\b')
        })
    $branches = @($statements | Where-Object { $_ -is [System.Management.Automation.Language.IfStatementAst] })
    Check "${file}: one top-level -SelfCheck* branch per override parameter" ($branches.Count -eq $overrides.Count) "$($branches.Count) found"
    $assignments = @($statements | Where-Object { $_ -is [System.Management.Automation.Language.AssignmentStatementAst] })
    Check "${file}: assigns `$commitSources once, at top level" ($assignments.Count -eq 1) "$($assignments.Count) found"
    if ($branches.Count -eq 0 -or $assignments.Count -ne 1) { continue }
    $firstBranch = [array]::IndexOf($topLevel, $branches[0])
    Check "${file}: the -SelfCheck* branches come after `$commitSources, so the override is what the record keeps" `
        ($firstBranch -gt [array]::IndexOf($topLevel, $assignments[0])) 'a branch precedes the $commitSources assignment'

    # 3. Before anything is staged.
    $firstStage = @($topLevel | Where-Object { $_.Extent.Text -match '\$StageRoot\b' } | Select-Object -First 1)
    $lastBranch = [array]::IndexOf($topLevel, $branches[-1])
    Check "${file}: the -SelfCheck* branches run before the first statement that touches `$StageRoot" `
        ($firstStage.Count -eq 1 -and $lastBranch -lt [array]::IndexOf($topLevel, $firstStage[0])) "last branch at $lastBranch"

    # $PSScriptRoot is empty inside a created scriptblock; the runner's directory is this one's.
    $block = [scriptblock]::Create((@($statements | ForEach-Object { $_.Extent.Text }) -join "`n").Replace('$PSScriptRoot', "'$ScriptRoot'"))
    $cases = @(@{ Name = 'no override'; Override = $null; Value = $null }) +
        @($overrides.Keys | ForEach-Object { @{ Name = "-$_"; Override = $_; Value = 'e' * 40 } }) +
        @($overrides.Keys | ForEach-Object { @{ Name = "-$_ naming the bound commit"; Override = $_; Value = $binding[$overrides[$_]] } })
    foreach ($case in $cases) {
        foreach ($name in $overrides.Keys) { Set-Variable -Name $name -Value $null }
        if ($null -ne $case.Override) { Set-Variable -Name $case.Override -Value $case.Value }
        $ControlServerCommit = $binding['ControlServerCommit']; $OnboardCommit = $binding['OnboardCommit']
        $SimulatorCommit = $binding['SimulatorCommit']; $ProtocolCommit = $binding['ProtocolCommit']
        $commitBinding = [ordered]@{} + $binding
        $runnerProvenance = [ordered]@{ bindingAtHead = ([ordered]@{} + $binding); runnerSource = 'COMMITTED_RUNNER' }
        $commitSources = $null
        $thrown = $null
        try { . $block } catch { $thrown = $_.Exception.Message }

        $changed = if ($null -ne $case.Override) { $overrides[$case.Override] } else { $null }
        $used = [ordered]@{ ControlServerCommit = $ControlServerCommit; OnboardCommit = $OnboardCommit; SimulatorCommit = $SimulatorCommit; ProtocolCommit = $ProtocolCommit }
        $wrong = @($sourceNames.Keys | ForEach-Object {
                $expectedCommit = if ($_ -eq $changed) { $case.Value } else { $binding[$_] }
                $expectedSource = if ($_ -eq $changed) { 'SELF_CHECK_OVERRIDE' } else { 'SHARED_BINDING' }
                if ($used[$_] -cne $expectedCommit) { "$_=$($used[$_]) (expected $expectedCommit)" }
                if ($null -eq $commitSources -or "$($commitSources[$sourceNames[$_]])" -cne $expectedSource) {
                    "$($sourceNames[$_])=$(${commitSources}?[$sourceNames[$_]]) (expected $expectedSource)" }
            })
        Check "${file}, $($case.Name): the commits it uses and their sources" ($null -eq $thrown -and $wrong.Count -eq 0) "$thrown $($wrong -join '; ')"
        if ($null -ne $commitSources) {
            $graded = Get-G3FormalSlicePass -RunKind $runKind -SliceStatus 'PASS' -Commits ([ordered]@{ runnerSource = 'COMMITTED_RUNNER' } + $commitSources)
            $ok = if ($null -eq $changed) { $graded.formalSlicePass -eq $true } else {
                $graded.formalSlicePass -eq $false -and $graded.formalSliceWithheldReason -eq 'SELF_CHECK_OVERRIDE' }
            Check "${file}, $($case.Name): an all-PASS slice is $(if ($null -eq $changed) { 'a formal pass' } else { 'withheld as SELF_CHECK_OVERRIDE' })" `
                $ok "$($graded | ConvertTo-Json -Compress)"
        }
    }
}

# 4. The restart runner's binding still comes from run-staged-g3.ps1, through a path that is not a parameter.
$bindingSourceParameters = @($restartAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -like '*Binding*' -or
        $_.Name.VariablePath.UserPath -like '*SharedRunner*' })
Check 'run-staged-g3-restart.ps1: no parameter can redirect where the binding is read from' ($bindingSourceParameters.Count -eq 0) `
    "$(($bindingSourceParameters | ForEach-Object { $_.Name.VariablePath.UserPath }) -join ', ')"
$bindingSourceAssignments = @($restartAst.EndBlock.Statements | Where-Object {
        $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $_.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
        $_.Left.VariablePath.UserPath -eq 'CommitBindingSource' })
Check "run-staged-g3-restart.ps1: `$CommitBindingSource is assigned once, to run-staged-g3.ps1 beside it" `
    ($bindingSourceAssignments.Count -eq 1 -and $bindingSourceAssignments[0].Right.Extent.Text -ceq "Join-Path `$PSScriptRoot 'run-staged-g3.ps1'") `
    "$(($bindingSourceAssignments | ForEach-Object { $_.Extent.Text }) -join ' | ')"

if ($failures.Count -gt 0) {
    Write-Host "$($failures.Count) check(s) failed."
    exit 1
}
Write-Host 'All checks passed.'
