#Requires -Version 7

<#
Finds `{ ... }.GetNewClosure()` blocks that read a variable the closure will not actually capture.

WHY THIS IS NOT A LINT PREFERENCE. `GetNewClosure()` takes a by-value snapshot of the variables that
unqualified lookup can see AT THE MOMENT IT IS CALLED. A read of anything else comes back $null, and
nothing says so: the closure runs, the variable is empty, and whatever the closure was deciding
decides wrongly. In an L2 probe that means a criterion that can never be satisfied -- it times out
sixty seconds later and reads as "the server never did it" (control-server#203 spent a real-rig run
on exactly that, evidence/l2/cs203-rig-01-nested-closure-red).

THE RULE IS NOT "DON'T NEST". Twenty shapes were measured in control-server#266
(evidence/l2/cs266-closure-capture/); nesting is neither sufficient nor necessary:

    legal   function top level, capturing that function's locals or parameters
    legal   nested, capturing a variable the closure's own body declares, or its own parameter
    legal   nested, having first copied the outer variable into a local  <- the sanctioned fix
    legal   inside ForEach-Object (that block runs in the caller's scope)
    legal   a .ps1 file's top-level variables, including ones a dot-sourced file put there
    legal   a script's parameters, captured at its top level

    broken  a variable belonging to an ENCLOSING FUNCTION, captured from a deeper scope -- whether
            that deeper scope is a closure, a plain scriptblock, or a nested function
    broken  the caller's variable, captured inside a dot-sourced function
    broken  a .psm1 file's top-level variables, captured by a closure in that same module
    broken  a $script:/$global:-QUALIFIED read, unconditionally, nesting and file type irrelevant

The last one is the one to keep in mind while reading the code below: it is checked before anything
else, because a qualified read is not part of the snapshot at all -- it is deferred to run time, and
by then the block's script scope is no longer the original. Measured with controls: the same read
without .GetNewClosure() works, and so does a direct read.

FALSE POSITIVES ARE THE EXPENSIVE FAILURE, so the analysis is deliberately incomplete. A wrong flag
blocks a ticket whose author cannot tell whether they deserve to be blocked -- the real mechanism
takes twenty measurements to state. A miss leaves us where we are today. Everything this module
cannot resolve is therefore treated as visible and NOT flagged; `Get-L2ClosureCaptureUnknowns` lists
those constructs by name so that the next person does not read the check as complete.
#>

Set-StrictMode -Version Latest

# Variables a closure may read that nobody has to assign.
$script:AutomaticNames = @(
    '_', 'PSItem', 'args', 'input', 'this', 'true', 'false', 'null', 'matches', 'foreach', 'switch',
    'PSScriptRoot', 'PSCommandPath', 'MyInvocation', 'PSBoundParameters', 'PWD', 'Host', 'Error',
    'LASTEXITCODE', 'PID', 'HOME', 'PSVersionTable', 'ErrorActionPreference', 'ProgressPreference',
    'VerbosePreference', 'InformationPreference', 'DebugPreference', 'WarningPreference',
    'ConfirmPreference', 'WhatIfPreference', 'env', 'StackTrace', 'IsWindows', 'IsLinux', 'IsMacOS'
)

<#
.SYNOPSIS
The constructs this analysis cannot resolve, and therefore never flags.

.DESCRIPTION
Exported so that a caller can print it next to a clean result. "No findings" from a check with an
unlisted blind spot reads as "there is nothing here"; with the list next to it, it reads as what it
is. The last entry is the one that matters most and the one a list like this usually omits.
#>
function Get-L2ClosureCaptureUnknowns {
    [CmdletBinding()]
    param()

    # NOT `, @(...)`: wrapping makes the caller's @() see one element that happens to be an array, so
    # a count of 7 reads as 1. The same wrap on the findings list produced one phantom finding per
    # clean file -- 123 of them -- each with every property null.
    return @(
        'Set-Variable / New-Variable -Name <expression>  (the name is not a literal)'
        'Remove-Variable, Clear-Variable                 (a name can stop being visible)'
        'Invoke-Expression                               (anything at all)'
        '$using: / Invoke-Command -ArgumentList          (remoting brings its own capture rules)'
        '[ref] parameters and PSReference indirection    (the visible name is not the one read)'
        'dot-sourcing a path built at run time           (cannot know what it defines)'
        'any shape not among the twenty measured in evidence/l2/cs266-closure-capture/'
    )
}

# Names a scope makes visible to unqualified lookup: its parameters, its assignments, its foreach
# variables, and -- because a read inside `-Until { param($v) ... }` belongs to THAT block -- the
# parameters of every scriptblock nested within it.
function Get-VisibleNames {
    param([System.Management.Automation.Language.Ast]$Scope)

    $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    if (-not $Scope) { return , $names }

    $owningFunction = $Scope.Parent -as [System.Management.Automation.Language.FunctionDefinitionAst]
    if ($owningFunction -and $owningFunction.Parameters) {
        foreach ($p in $owningFunction.Parameters) { $null = $names.Add($p.Name.VariablePath.UserPath) }
    }
    foreach ($pb in $Scope.FindAll({ param($n) $n -is [System.Management.Automation.Language.ParamBlockAst] }, $true)) {
        foreach ($p in $pb.Parameters) { $null = $names.Add($p.Name.VariablePath.UserPath) }
    }
    foreach ($a in $Scope.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)) {
        foreach ($v in $a.Left.FindAll({ param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] }, $true)) {
            $null = $names.Add($v.VariablePath.UserPath)
        }
    }
    foreach ($fe in $Scope.FindAll({ param($n) $n -is [System.Management.Automation.Language.ForEachStatementAst] }, $true)) {
        $null = $names.Add($fe.Variable.VariablePath.UserPath)
    }
    # A literal -Name on Set-Variable/New-Variable. A computed one is an unknown; see the list above.
    foreach ($c in $Scope.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
        if ($c.GetCommandName() -notin 'Set-Variable', 'New-Variable') { continue }
        foreach ($el in $c.CommandElements) {
            if ($el -is [System.Management.Automation.Language.StringConstantExpressionAst]) { $null = $names.Add($el.Value) }
        }
    }
    return , $names
}

# The scope a GetNewClosure call runs in: the nearest enclosing function body or scriptblock.
function Get-EnclosingScope {
    param([System.Management.Automation.Language.Ast]$Node)

    $p = $Node.Parent
    while ($p) {
        if ($p -is [System.Management.Automation.Language.FunctionDefinitionAst]) { return $p.Body }
        if ($p -is [System.Management.Automation.Language.ScriptBlockAst]) { return $p }
        $p = $p.Parent
    }
    return $null
}

# A file's own top-level assignments -- only those outside every function and scriptblock.
#
# .ps1 ONLY. A .psm1's top-level variables are NOT capturable by a closure inside that module, which
# is the one result here that no amount of reading predicts: writing them as $script: looks more
# deliberate, so the intuition is that they would be MORE visible. Measured the other way
# (evidence/l2/cs266-closure-capture/result-module-scope.txt, T1 empty against three controls).
function Get-FileTopLevelNames {
    param([System.Management.Automation.Language.ScriptBlockAst]$FileAst, [string]$Path)

    $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    if ([System.IO.Path]::GetExtension($Path) -ne '.ps1') { return , $names }

    foreach ($a in $FileAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)) {
        $up = $a.Parent
        $atTop = $true
        while ($up -and $up -ne $FileAst) {
            if ($up -is [System.Management.Automation.Language.FunctionDefinitionAst] -or
                $up -is [System.Management.Automation.Language.ScriptBlockExpressionAst]) { $atTop = $false; break }
            $up = $up.Parent
        }
        if (-not $atTop) { continue }
        foreach ($v in $a.Left.FindAll({ param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] }, $true)) {
            $null = $names.Add($v.VariablePath.UserPath)
        }
    }
    # A script's own param() block is visible at its top level (that is how every L2 scenario reads
    # $Context: Invoke-L2Scenario.ps1 runs it as `& $scenarioPath -Context $context`).
    if ($FileAst.ParamBlock) {
        foreach ($p in $FileAst.ParamBlock.Parameters) { $null = $names.Add($p.Name.VariablePath.UserPath) }
    }
    return , $names
}

<#
.SYNOPSIS
Every GetNewClosure block in a file that reads a variable it will not capture.

.DESCRIPTION
Returns one object per finding: File, Line, Names, Reason. An empty result means nothing was found
among the shapes this can resolve -- read it together with Get-L2ClosureCaptureUnknowns.
#>
function Get-L2ClosureCaptureFindings {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors -and $errors.Count -gt 0) {
        throw "$Path does not parse: $($errors[0].Message) (line $($errors[0].Extent.StartLineNumber))"
    }

    $auto = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($n in $script:AutomaticNames) { $null = $auto.Add($n) }
    $fileNames = Get-FileTopLevelNames -FileAst $ast -Path $Path
    $findings = [System.Collections.Generic.List[object]]::new()

    foreach ($call in $ast.FindAll({ param($n)
        $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
        $n.Member -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
        $n.Member.Value -eq 'GetNewClosure' -and
        $n.Expression -is [System.Management.Automation.Language.ScriptBlockExpressionAst] }, $true)) {

        $body = $call.Expression.ScriptBlock
        $own = Get-VisibleNames -Scope $body
        $enclosing = Get-VisibleNames -Scope (Get-EnclosingScope -Node $call)

        $qualified = [System.Collections.Generic.List[string]]::new()
        $unreachable = [System.Collections.Generic.List[string]]::new()

        foreach ($v in $body.FindAll({ param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] }, $true)) {
            $name = $v.VariablePath.UserPath
            # Qualified first. Checking visibility first would skip it whenever the same function also
            # assigns the variable -- which is the common case, and would have hidden the one fixture
            # that covers this shape.
            if ($v.VariablePath.IsScript -or $v.VariablePath.IsGlobal) { $qualified.Add($v.VariablePath.ToString()); continue }
            if ($auto.Contains($name) -or $own.Contains($name) -or $enclosing.Contains($name) -or $fileNames.Contains($name)) { continue }
            $unreachable.Add($name)
        }

        if ($qualified.Count -gt 0) {
            $findings.Add([pscustomobject]@{
                File   = $Path
                Line   = $call.Extent.StartLineNumber
                Names  = (@($qualified | Sort-Object -Unique) -join ', ')
                Reason = 'a $script:/$global:-qualified read inside a closure is always empty; copy it into a local first'
            })
        }
        if ($unreachable.Count -gt 0) {
            $findings.Add([pscustomobject]@{
                File   = $Path
                Line   = $call.Extent.StartLineNumber
                Names  = (@($unreachable | Sort-Object -Unique) -join ', ')
                Reason = 'not visible where the closure is taken, so it is captured as $null; copy it into a local in this scope first'
            })
        }
    }
    # Unwrapped, see Get-L2ClosureCaptureUnknowns: a wrapped EMPTY array is not empty to a caller.
    return $findings.ToArray()
}

Export-ModuleMember -Function Get-L2ClosureCaptureFindings, Get-L2ClosureCaptureUnknowns
