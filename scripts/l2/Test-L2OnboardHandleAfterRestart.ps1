#Requires -Version 7

<#
.SYNOPSIS
    Self-check over the real-onboard scenarios: after anything that restarts the onboard HMI, a scenario must read
    $Context.Onboard again before it drives or reads the onboard through a driver taken earlier.

.DESCRIPTION
    Pure parsing, no rig, a second. Context.RestartOnboard kills the onboard process and puts a new UI Automation
    driver into Context.Onboard; a driver taken before that still points at the dead window. Every lookup through it
    finds nothing, so ButtonAvailable answers False for as long as anyone asks -- which reads exactly like "the onboard
    never offered the entry".

    That is how real-onboard-restart-with-open-recovery-session went red on every onboard commit (control-server#222):
    control-server#128 made Invoke-G3UnknownLoad restart the onboard, added `$onboard = $Context.Onboard` after the
    call in the three G3 scenarios, and missed this one, whose $onboard was taken at the top of the script. The
    scenario then waited 90 seconds for 「申请恢复」 on a window that no longer existed.

    What counts, in source order:
      restart    -- a call to Invoke-G3UnknownLoad (it restarts the onboard, G3RecoveryCommon.ps1);
                    `& $Context.RestartOnboard` whose result is not assigned to a handle variable; or
                    `& $Context.StopComponent 'onboard-hmi'`, which kills the process just as a power cut does;
      bind       -- `$<name> = <anything naming $Context.Onboard or $Context.RestartOnboard>`, which makes <name> a
                    handle variable and gives it a fresh driver;
      use        -- any other reference to a handle variable.
    A use between a restart and that variable's next bind is a finding. Each handle variable carries its own
    staleness: re-reading one after a restart says nothing about the others. $onboard is a handle variable from the
    start, bound or not, so a scenario that only ever uses that name is judged exactly as it was before.

    Source order rather than call order: a function the scenario defines is checked where it is written, which is
    where every scenario here also calls it from.

    Two misses this check had until control-server#230, each with a counterexample under
    counterexamples/onboard-handle-after-restart/ that the self-test replays:
      - the driver lived in a variable not called $onboard (only that one name was tracked);
      - the onboard was restarted with `& $Context.StopComponent 'onboard-hmi'` (only Invoke-G3UnknownLoad and
        $Context.RestartOnboard counted as a restart).
    The argument of StopComponent is matched exactly: load-result-requires-recovery and slot-group-temporarily-full
    stop 'fake-onboard', which is a synthetic peer, not the onboard HMI, and must not count.

    Exits 1 on any finding, and prints every finding and every file it checked either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2OnboardHandleAfterRestart.ps1
#>
[CmdletBinding()]
param(
    # Defaults to this repository's scenarios; a directory is accepted so the check can be pointed at a copy.
    [string]$ScenarioDirectory = (Join-Path $PSScriptRoot 'scenarios'),

    # Replayed by the self-test. Each file here must be reported; a rule that stopped working would otherwise
    # leave this check green and say nothing, because the scenarios are supposed to be clean.
    [string]$CounterexampleDirectory = (Join-Path $PSScriptRoot 'counterexamples/onboard-handle-after-restart'),

    [switch]$SkipSelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-NamesContextOnboard([System.Management.Automation.Language.Ast]$Ast) {
    return $null -ne $Ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.MemberExpressionAst] -and
                $node.Expression -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $node.Expression.VariablePath.UserPath -eq 'Context' -and
                [string]$node.Member.Value -in @('Onboard', 'RestartOnboard')
        }, $true)
}

# `& $Context.<Member>` -- the only form the scenarios use for these two, and the only one this recognises.
function Test-IsContextInvocation(
    [System.Management.Automation.Language.Ast]$Node,
    [string]$Member) {
    if ($Node -isnot [System.Management.Automation.Language.CommandAst]) { return $false }
    $first = $Node.CommandElements[0]
    return $Node.InvocationOperator -eq [System.Management.Automation.Language.TokenKind]::Ampersand -and
        $first -is [System.Management.Automation.Language.MemberExpressionAst] -and
        $first.Expression -is [System.Management.Automation.Language.VariableExpressionAst] -and
        $first.Expression.VariablePath.UserPath -eq 'Context' -and
        [string]$first.Member.Value -eq $Member
}

function Test-IsRestart([System.Management.Automation.Language.Ast]$Node) {
    if ($Node -isnot [System.Management.Automation.Language.CommandAst]) { return $false }
    if ($Node.GetCommandName() -eq 'Invoke-G3UnknownLoad') { return $true }
    if (Test-IsContextInvocation $Node 'RestartOnboard') { return $true }
    # Killing the onboard process is a restart for this check's purpose: the driver taken before it is dead from here
    # on, whatever brings the process back.
    if (Test-IsContextInvocation $Node 'StopComponent') {
        $argument = @($Node.CommandElements | Select-Object -Skip 1 |
                Where-Object { $_ -is [System.Management.Automation.Language.StringConstantExpressionAst] })
        return $argument.Count -ge 1 -and [string]$argument[0].Value -eq 'onboard-hmi'
    }
    return $false
}

function Get-AssignedVariableName([System.Management.Automation.Language.Ast]$Node) {
    if ($Node -isnot [System.Management.Automation.Language.AssignmentStatementAst]) { return $null }
    if ($Node.Left -isnot [System.Management.Automation.Language.VariableExpressionAst]) { return $null }
    return $Node.Left.VariablePath.UserPath
}

function Get-OnboardHandleFindings([string]$Path) {
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw "Could not parse ${Path}: $($errors[0].Message)" }

    $events = [System.Collections.Generic.List[object]]::new()
    foreach ($node in $ast.FindAll({ param($n) $true }, $true)) {
        $assigned = Get-AssignedVariableName $node
        if ($null -ne $assigned) {
            if (Test-NamesContextOnboard $node.Right) {
                # Ordered at the end of the statement: `$onboard = & $Context.RestartOnboard` restarts and re-binds in
                # one, and the restart inside it must not outlive it.
                $events.Add([pscustomobject]@{
                        Offset = $node.Extent.EndOffset
                        Kind   = 'bind'
                        Name   = $assigned
                        Line   = $node.Extent.StartLineNumber
                    })
            }
            continue
        }
        if (Test-IsRestart $node) {
            $events.Add([pscustomobject]@{
                    Offset = $node.Extent.StartOffset
                    Kind   = 'restart'
                    Name   = $null
                    Line   = $node.Extent.StartLineNumber
                })
            continue
        }
        if ($node -is [System.Management.Automation.Language.VariableExpressionAst] -and
            -not ($node.Parent -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $node.Parent.Left -eq $node)) {
            $events.Add([pscustomobject]@{
                    Offset = $node.Extent.StartOffset
                    Kind   = 'use'
                    Name   = $node.VariablePath.UserPath
                    Line   = $node.Extent.StartLineNumber
                })
        }
    }

    # $onboard is a handle variable whether or not this file binds it, so a scenario that uses only that name is
    # judged exactly as it was before this check learned about other names.
    $handles = [System.Collections.Generic.HashSet[string]]::new(
        [string[]]@('onboard'), [System.StringComparer]::Ordinal)
    $stale = @{}
    $findings = foreach ($event in ($events | Sort-Object Offset)) {
        switch ($event.Kind) {
            'restart' {
                foreach ($name in @($handles)) { $stale[$name] = $event.Line }
            }
            'bind' {
                $null = $handles.Add($event.Name)
                $stale.Remove($event.Name)
            }
            'use' {
                if ($handles.Contains($event.Name) -and $stale.ContainsKey($event.Name)) {
                    [pscustomobject]@{
                        File        = Split-Path -Leaf $Path
                        Variable    = $event.Name
                        Line        = $event.Line
                        RestartLine = $stale[$event.Name]
                    }
                }
            }
        }
    }
    return , @($findings)
}

function Invoke-OnboardHandleScan([string]$Directory, [string]$Label) {
    $files = @(Get-ChildItem -LiteralPath $Directory -Filter '*.ps1' |
            Where-Object { $_.Name -notlike '*Common.ps1' } | Sort-Object Name)
    if ($files.Count -eq 0) { throw "No $Label under $Directory" }

    $scanned = foreach ($file in $files) {
        $findings = Get-OnboardHandleFindings $file.FullName
        $verdict = $findings.Count -eq 0 ? 'ok' : "$($findings.Count) stale use(s)"
        Write-Host ("{0,-60} {1}" -f $file.Name, $verdict)
        [pscustomobject]@{ Name = $file.Name; Findings = $findings }
    }
    return , @($scanned)
}

Write-Host "Scenarios under $ScenarioDirectory"
$scanned = Invoke-OnboardHandleScan $ScenarioDirectory 'scenarios'
$all = @($scanned | ForEach-Object { $_.Findings } | Where-Object { $null -ne $_ })
foreach ($finding in $all) {
    Write-Host (
        'STALE ONBOARD HANDLE: {0}:{1} uses ${2} after the restart at line {3} without re-reading $Context.Onboard' -f
        $finding.File, $finding.Line, $finding.Variable, $finding.RestartLine)
}

$failed = $all.Count -gt 0
if (-not $failed) { Write-Host "No stale onboard handle in $($scanned.Count) scenarios." }

if (-not $SkipSelfTest) {
    Write-Host ''
    Write-Host "Self-test: counterexamples under $CounterexampleDirectory (each must be reported)"
    $counterexamples = Invoke-OnboardHandleScan $CounterexampleDirectory 'counterexamples'
    foreach ($counterexample in $counterexamples) {
        if ($counterexample.Findings.Count -eq 0) {
            Write-Host (
                "SELF-TEST FAILED: $($counterexample.Name) was not reported; " +
                'the rule it stands for has stopped working.')
            $failed = $true
        }
    }
    if (-not $failed) {
        Write-Host "Self-test passed: all $($counterexamples.Count) counterexamples were reported."
    }
}

if ($failed) { exit 1 }
