#Requires -Version 7

<#
Finds `{ ... }.GetNewClosure()` blocks that read a variable the closure will not actually carry.

WHY THIS IS NOT A LINT PREFERENCE. A read that is not carried comes back $null, and nothing says so:
the closure runs, the variable is empty, and whatever it was deciding decides wrongly. In an L2 probe
that means a criterion that can never be satisfied -- it times out sixty seconds later and reads as
"the server never did it" (control-server#203 spent a real-rig run on exactly that,
evidence/l2/cs203-rig-01-nested-closure-red).

THE RULE IS ONE AXIS: WHERE THE CLOSURE IS TAKEN.

    GetNewClosure() snapshots by value the variables of the ONE scope it is called in. Names that
    ordinary lookup would find in a PARENT scope are not in the snapshot -- and ordinary lookup at
    the same spot does find them, which is why this is invisible in review.

Measured in evidence/l2/cs266-closure-capture/ (result-capture-grid.txt is the grid; every cell has
a control that must come out non-empty):

    where the closure is taken  | file top-level name | $script: | $global: | outer function local
    ----------------------------+---------------------+----------+----------+---------------------
    at the FILE top level       | carried             | carried  | resolves | n/a
    (.ps1 and .psm1 alike)      |                     |          |          |
    inside a function body      | EMPTY               | EMPTY    | resolves | EMPTY
    inside a nested scriptblock | EMPTY               | EMPTY    | resolves | EMPTY

Three earlier write-ups of this same rule, all from this repository, were overturned by that grid.
They are listed rather than deleted because each is a word or a sentence a reader arrives with:

  - "do not nest" is wrong. Nesting is neither sufficient nor necessary: a nested block that reads
    its OWN scope variables is fine, and an unnested function body reading the file top level is not.
  - "$script: is unconditionally empty" is wrong. It is carried when the closure is taken at the file
    top level; only deeper does it come back empty.
  - "a .psm1 top-level variable is not capturable" is wrong. It is, at the module own top level. What
    is not capturable is reading it from inside one of that module functions -- the same row of the
    table as everything else.

$global: IS NOT FLAGGED, and that is a measurement rather than an omission: a $global: read is not
part of the snapshot at all. It resolves at run time against the global scope and finds the value, in
all six cells of the grid. Note what that does NOT mean: a live lookup is not a snapshot, so a later
write IS visible to the closure. That is a different hazard, and this guard says nothing about it.

$_ / $PSItem is visible only when the read sits inside a scriptblock nested WITHIN the closure body
(a Where-Object or ForEach-Object block the closure itself contains). The enclosing pipeline one is
not carried -- measured EMPTY -- so a closure taken directly inside ForEach-Object and reading $_ is
a finding.

FALSE POSITIVES ARE THE EXPENSIVE FAILURE, so the analysis is deliberately incomplete. A wrong flag
blocks a ticket whose author cannot tell whether they deserve to be blocked. A miss leaves us where
we are today. Everything this module cannot resolve is therefore treated as visible and NOT flagged;
Get-L2ClosureCaptureUnknowns lists those constructs by name so that a clean result is readable next
to what the check cannot see.
#>

Set-StrictMode -Version Latest

# Variables a closure may read that nobody has to assign.
#
# $_ and $PSItem are not here, and leaving them out is documentation rather than mechanism: the branch
# that handles them runs BEFORE this list is consulted, so putting them back would change nothing.
# Measured, because the obvious mutation (adding them back) reddens nothing and that is easy to
# misread as "the list is what decides". The list is not; the branch is.
$script:AutomaticNames = @(
    'args', 'input', 'this', 'true', 'false', 'null', 'matches', 'foreach', 'switch',
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
unlisted blind spot reads as "there is nothing here"; with the list beside it, it reads as what it
is. The last entry is the one that matters most and the one a list like this usually loses first.
#>
function Get-L2ClosureCaptureUnknowns {
    [CmdletBinding()]
    param()

    # NOT a comma-wrapped return: wrapping makes the caller @() see one element that happens to be an
    # array, so a count of ten reads as 1. The same wrap on the findings list produced one phantom
    # finding per clean file -- 123 of them -- each with every property null.
    return @(
        'Set-Variable / New-Variable -Name <expression>  (the name is not a literal)'
        'Remove-Variable, Clear-Variable                 (a name can stop being visible)'
        'Invoke-Expression                               (anything at all)'
        '$using: / Invoke-Command -ArgumentList          (remoting brings its own capture rules)'
        '[ref] parameters and PSReference indirection    (the visible name is not the one read)'
        'dot-sourcing a path built at run time           (cannot know what it defines)'
        'WHERE in a scope a name is assigned: an assignment anywhere in the scope counts as visible, including one made AFTER the closure is taken (measured EMPTY) and one inside a nested function'
        '$foreach / $switch / $matches and the rest of the automatic list, exempted wholesale rather than measured one by one'
        'a script run as pwsh -File instead of through the call operator: its top-level names land in the global scope, so a function body there CAN read them. This guard judges by the call-operator form, which is how Invoke-L2Scenario runs a scenario, and so it is stricter than pwsh -File needs'
        'any shape not measured in evidence/l2/cs266-closure-capture/'
    )
}

# Names a scope makes visible to unqualified lookup: its parameters, its assignments, its foreach
# variables, and -- because a read inside an -Until { param($v) ... } block belongs to THAT block --
# the parameters of every scriptblock nested within it.
#
# Position is not considered: an assignment anywhere in the scope counts. That is unsound in one
# direction only (it treats an assignment made after the closure is taken as visible, and that shape
# measures EMPTY), and unsoundness in that direction is a miss rather than a false flag. It is in the
# unknowns above. A position rule would be wrong inside a loop, where the textually-later assignment
# does run first.
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

# The scope a node sits in: the nearest enclosing function body or scriptblock. For a node at a file
# top level this is the file own root ScriptBlockAst -- which is what makes the file top-level names
# fall out of Get-VisibleNames for free, and ONLY there.
#
# An earlier version of this module had a separate "file top-level names" rule that it applied at
# every depth. That is exactly the shape the grid measures as EMPTY, and it left the guard blind to
# a scenario that moves its probe into a helper function -- the cheapest edit there is, and the one
# that reproduces control-server#203.
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

<#
.SYNOPSIS
Every GetNewClosure block in a file that reads a variable it will not carry.

.DESCRIPTION
Returns one object per finding: File, Line, Names, Reason. An empty result means nothing was found
among the shapes this can resolve -- read it together with Get-L2ClosureCaptureUnknowns. With nothing
to report it returns $null rather than an empty array, so a caller that wants a count writes @().
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
    $findings = [System.Collections.Generic.List[object]]::new()

    foreach ($call in $ast.FindAll({ param($n)
        $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
        $n.Member -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
        $n.Member.Value -eq 'GetNewClosure' -and
        $n.Expression -is [System.Management.Automation.Language.ScriptBlockExpressionAst] }, $true)) {

        $body = $call.Expression.ScriptBlock
        $scope = Get-EnclosingScope -Node $call
        # The whole rule turns on this one boolean. At the file top level the scope IS the file, so
        # its top-level names and its param() are visible and $script: is carried; anywhere deeper,
        # neither is.
        $atFileTopLevel = [object]::ReferenceEquals($scope, $ast)
        $own = Get-VisibleNames -Scope $body
        $enclosing = Get-VisibleNames -Scope $scope

        $qualified = [System.Collections.Generic.List[string]]::new()
        $unreachable = [System.Collections.Generic.List[string]]::new()

        foreach ($v in $body.FindAll({ param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] }, $true)) {
            $name = $v.VariablePath.UserPath

            # $global: resolves at run time against the global scope and finds the value in every
            # cell of the grid. Flagging it was a false positive by construction -- the expensive
            # kind, because nothing at the site tells its author why they were blocked.
            if ($v.VariablePath.IsGlobal) { continue }

            # $script: is carried only from the file top level. Checked before visibility, because a
            # function that also assigns the same name would otherwise hide the qualified read.
            if ($v.VariablePath.IsScript) {
                if (-not $atFileTopLevel) { $qualified.Add($v.VariablePath.ToString()) }
                continue
            }

            # $_ belongs to the innermost pipeline block. Inside a Where-Object/ForEach-Object block
            # the closure itself contains, it is the closure own; read directly in the closure body
            # it is the ENCLOSING pipeline one, which is not carried.
            if ($name -ceq '_' -or $name -ceq 'PSItem') {
                if (-not [object]::ReferenceEquals((Get-EnclosingScope -Node $v), $body)) { continue }
                $unreachable.Add('$' + $name)
                continue
            }

            if ($auto.Contains($name) -or $own.Contains($name) -or $enclosing.Contains($name)) { continue }
            $unreachable.Add($name)
        }

        if ($qualified.Count -gt 0) {
            $findings.Add([pscustomobject]@{
                File   = $Path
                Line   = $call.Extent.StartLineNumber
                Names  = (@($qualified | Sort-Object -Unique) -join ', ')
                Reason = 'a $script:-qualified read is carried only when the closure is taken at the file top level; this one is deeper, so it reads as empty -- copy it into a local in this scope first'
            })
        }
        if ($unreachable.Count -gt 0) {
            $findings.Add([pscustomobject]@{
                File   = $Path
                Line   = $call.Extent.StartLineNumber
                Names  = (@($unreachable | Sort-Object -Unique) -join ', ')
                Reason = 'not in the scope the closure is taken in, so it is carried as $null; copy it into a local in this scope first'
            })
        }
    }
    # Unwrapped, see Get-L2ClosureCaptureUnknowns: a wrapped EMPTY array is not empty to a caller.
    return $findings.ToArray()
}

Export-ModuleMember -Function Get-L2ClosureCaptureFindings, Get-L2ClosureCaptureUnknowns
