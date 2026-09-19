#Requires -Version 7

<#
.SYNOPSIS
    Self-check over the real-onboard scenarios: after anything that restarts the onboard HMI, a scenario must read
    $Context.Onboard again before it drives or reads the onboard through its own $onboard.

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
      restart    -- a call to Invoke-G3UnknownLoad (it restarts the onboard, G3RecoveryCommon.ps1), or
                    `& $Context.RestartOnboard` whose result is not assigned to $onboard;
      re-read    -- `$onboard = <anything naming $Context.Onboard or $Context.RestartOnboard>`;
      use        -- any other reference to $onboard.
    A use between a restart and the next re-read is a finding. Source order rather than call order: a function the
    scenario defines is checked where it is written, which is where every scenario here also calls it from.

    Exits 1 on any finding, and prints every finding and every file it checked either way.

.EXAMPLE
    pwsh -NoProfile -File .\scripts\l2\Test-L2OnboardHandleAfterRestart.ps1
#>
[CmdletBinding()]
param(
    # Defaults to this repository's scenarios; a directory is accepted so the check can be pointed at a copy.
    [string]$ScenarioDirectory = (Join-Path $PSScriptRoot 'scenarios')
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

function Test-IsRestart([System.Management.Automation.Language.Ast]$Node) {
    if ($Node -isnot [System.Management.Automation.Language.CommandAst]) { return $false }
    if ($Node.GetCommandName() -eq 'Invoke-G3UnknownLoad') { return $true }
    $first = $Node.CommandElements[0]
    return $Node.InvocationOperator -eq [System.Management.Automation.Language.TokenKind]::Ampersand -and
        $first -is [System.Management.Automation.Language.MemberExpressionAst] -and
        [string]$first.Member.Value -eq 'RestartOnboard'
}

function Test-IsOnboardAssignment([System.Management.Automation.Language.Ast]$Node) {
    return $Node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $Node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
        $Node.Left.VariablePath.UserPath -eq 'onboard'
}

function Get-OnboardHandleFindings([string]$Path) {
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw "Could not parse ${Path}: $($errors[0].Message)" }

    $events = [System.Collections.Generic.List[object]]::new()
    foreach ($node in $ast.FindAll({ param($n) $true }, $true)) {
        if (Test-IsOnboardAssignment $node) {
            if (Test-NamesContextOnboard $node.Right) {
                # Ordered at the end of the statement: `$onboard = & $Context.RestartOnboard` restarts and re-reads
                # in one, and the restart inside it must not outlive it.
                $events.Add([pscustomobject]@{ Offset = $node.Extent.EndOffset; Kind = 'reread'; Line = $node.Extent.StartLineNumber })
            }
            continue
        }
        if (Test-IsRestart $node) {
            $events.Add([pscustomobject]@{ Offset = $node.Extent.StartOffset; Kind = 'restart'; Line = $node.Extent.StartLineNumber })
            continue
        }
        if ($node -is [System.Management.Automation.Language.VariableExpressionAst] -and
            $node.VariablePath.UserPath -eq 'onboard' -and
            -not ($node.Parent -is [System.Management.Automation.Language.AssignmentStatementAst] -and $node.Parent.Left -eq $node)) {
            $events.Add([pscustomobject]@{ Offset = $node.Extent.StartOffset; Kind = 'use'; Line = $node.Extent.StartLineNumber })
        }
    }

    $stale = $null
    $findings = foreach ($event in ($events | Sort-Object Offset)) {
        switch ($event.Kind) {
            'restart' { $stale = $event.Line }
            'reread' { $stale = $null }
            'use' {
                if ($null -ne $stale) {
                    [pscustomobject]@{ File = Split-Path -Leaf $Path; Line = $event.Line; RestartLine = $stale }
                }
            }
        }
    }
    return , @($findings)
}

$files = @(Get-ChildItem -LiteralPath $ScenarioDirectory -Filter '*.ps1' |
        Where-Object { $_.Name -notlike '*Common.ps1' } | Sort-Object Name)
if ($files.Count -eq 0) { throw "No scenarios under $ScenarioDirectory" }

$all = [System.Collections.Generic.List[object]]::new()
foreach ($file in $files) {
    $findings = Get-OnboardHandleFindings $file.FullName
    foreach ($finding in $findings) { $all.Add($finding) }
    $verdict = $findings.Count -eq 0 ? 'ok' : "$($findings.Count) stale use(s)"
    Write-Host ("{0,-60} {1}" -f $file.Name, $verdict)
}

foreach ($finding in $all) {
    Write-Host "STALE ONBOARD HANDLE: $($finding.File):$($finding.Line) uses `$onboard after the restart at line $($finding.RestartLine) without re-reading `$Context.Onboard"
}
if ($all.Count -gt 0) { exit 1 }
Write-Host "No stale onboard handle in $($files.Count) scenarios."
