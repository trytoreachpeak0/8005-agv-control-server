#Requires -Version 7
# Which G3 runners hand an $EvidenceRoot-derived path to a CHILD PROCESS?
# That is the mechanism that bit control-server#211, and it is what decides whether absolutising the
# parent's variable is a fix or merely defence in depth. Variable names are not a reliable filter --
# $proxyTranscript is evidence-derived and says nothing about evidence -- so this propagates taint
# through assignments instead of guessing names.
param([string]$Seed = 'EvidenceRoot', [string]$Root = 'C:\Users\szy\Desktop\8005-workspace-v2\worktrees\cs264-8005-agv-control-server')

$ErrorActionPreference = 'Stop'

function Get-TaintedNames {
    param([System.Management.Automation.Language.Ast]$Ast, [string]$Seed)
    $tainted = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $null = $tainted.Add($Seed)
    for ($i = 0; $i -lt 10; $i++) {
        $before = $tainted.Count
        foreach ($a in $Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)) {
            $lhs = $a.Left -as [System.Management.Automation.Language.VariableExpressionAst]
            if (-not $lhs) { continue }
            $rhsVars = @($a.Right.FindAll({ param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] }, $true) |
                ForEach-Object { $_.VariablePath.UserPath })
            if (@($rhsVars | Where-Object { $tainted.Contains($_) }).Count -gt 0) {
                $null = $tainted.Add($lhs.VariablePath.UserPath)
            }
        }
        if ($tainted.Count -eq $before) { break }
    }
    return $tainted
}

$runners = 'run-staged-g3.ps1', 'run-staged-g3-restart.ps1', 'run-demand-bearing-g3-vectors.ps1', 'run-journey-g3.ps1'
foreach ($runner in $runners) {
    $path = Join-Path $Root "scripts\$runner"
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    if ($errors) { Write-Host "  $runner 解析失败"; continue }

    $tainted = Get-TaintedNames -Ast $ast -Seed $Seed
    $hits = [System.Collections.Generic.List[string]]::new()

    foreach ($p in $ast.FindAll({ param($n)
        $n -is [System.Management.Automation.Language.CommandParameterAst] -and
        $n.ParameterName -in @('Arguments', 'ArgumentList') }, $true)) {

        $cmd = $p.Parent
        $idx = $cmd.CommandElements.IndexOf($p)
        $val = if ($p.Argument) { $p.Argument }
               elseif ($idx -ge 0 -and $idx + 1 -lt $cmd.CommandElements.Count) { $cmd.CommandElements[$idx + 1] }
               else { $null }
        if ($null -eq $val) { continue }

        $vars = @($val.FindAll({ param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] }, $true) |
            ForEach-Object { $_.VariablePath.UserPath }) | Sort-Object -Unique
        $bad = @($vars | Where-Object { $tainted.Contains($_) })
        if ($bad.Count -gt 0) {
            $hits.Add("    line $($val.Extent.StartLineNumber): $($bad -join ', ')  [cmd: $($cmd.GetCommandName())]")
        }
    }

    Write-Host "=== $runner  (derived variables: $($tainted.Count))"
    if ($hits.Count -gt 0) { $hits | ForEach-Object { Write-Host $_ } }
    else { Write-Host '    no child-process argument carries an EvidenceRoot-derived path' }
}
