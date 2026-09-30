#Requires -Version 7

<#
.SYNOPSIS
    Finds the call shapes that break a function which returns its array whole (control-server#428).

.DESCRIPTION
    Invoke-L2Query ends in `return , $rows`, and so do over a hundred other functions under scripts/. The comma makes the
    function emit ONE object, the array itself, so that an assignment receives the array whatever its length: zero
    rows stay an empty array instead of $null, one row stays a one-element array instead of the bare row.

    That contract holds for exactly one kind of caller: one that takes the single emitted object as it is
    (`$rows = Helper ...`, `(Helper ...)`, an argument). Every caller that ENUMERATES the function's output gets the
    array as one item:

      wrapped   @(Helper ...)                 a one-element array whose element is the whole result. .Count is 1 for
                                              zero rows, one row and forty rows alike, so `.Count -eq 1` cannot go
                                              red; an empty result throws on the first property read under strict
                                              mode; one or more rows read fine through member enumeration, which
                                              is what hides it.
      piped     Helper ... | Where-Object ...   $_ is the whole result, once.
      foreach   foreach ($r in Helper ...)    one iteration, $r is the whole result.

    control-server#428 found 39 wrapped Invoke-L2Query calls in 18 files after the same mistake had been fixed six
    times, one site at a time. This module is the scan that replaces remembering.

    What counts as a whole-array function is derived from the scripts, not listed by hand -- a hand-kept list of
    names is the thing that was always one helper short:

      direct        a statement in output position that is `, <expr>` (unary comma), with or without `return`,
                    or a pipeline ending in `Write-Output -NoEnumerate`;
      pass-through  a statement in output position that is a bare call of a whole-array function
                    (`function Get-Journeys { return Invoke-L2Query ... }`): the wrapping survives one `return`.
                    Resolved to a fixpoint.

    "Output position" is a statement of the function's own body, at any depth of if/loop/switch/try, that is not
    inside an assignment, a sub-expression, a nested function or a script block.

    A name is resolved the way the scripts are loaded: a function defined in the calling file wins; otherwise the
    definitions in the shared files apply -- every *.psm1, and every *.ps1 that some scanned script dot-sources by a
    literal file name (`. (Join-Path $PSScriptRoot 'G3RecoveryCommon.ps1')`). A name defined more than once counts as
    whole-array if ANY of those definitions is.

    WHAT THIS CANNOT SEE. It reads syntax, so:
      - a call whose name is not a literal: `& $reader ...`, `& $Context.Query ...`, Invoke-Expression, an alias
        made with Set-Alias or New-Alias;
      - a whole-array function defined outside the scanned directory, in a class method, or as a script block held
        in a variable; and one that emits through $PSCmdlet.WriteObject($x, $false);
      - a function whose only whole-array statement sits inside an assignment-captured block
        (`$x = if (...) { , $rows }` is correctly not output, but `$x = foreach (...) { Helper }` followed by
        `return , $x` is classified by the second statement alone);
      - enumeration by anything other than the three shapes above: `$(foreach ($d in $ids) { Helper $d })`,
        `Helper ... | Out-Null` IS reported (piped) even though it is harmless -- write `$null = Helper ...`;
      - a scenario-local function shadowing a shared one of the same name with a different return shape in a file
        that dot-sources a third file defining it again.
    None of those shapes occurs under scripts/ at the time of writing; Test-L2WholeArrayReturn.ps1 pins, for each
    shape this module does claim, both the verdict and the measured runtime behaviour behind it.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Ast = 'System.Management.Automation.Language'

# Statement constructs an emitted value passes straight through on its way out of a function or into @().
$script:TransparentStatements = @(
    'StatementBlockAst', 'NamedBlockAst', 'IfStatementAst', 'ForEachStatementAst', 'ForStatementAst',
    'WhileStatementAst', 'DoWhileStatementAst', 'DoUntilStatementAst', 'SwitchStatementAst', 'TryStatementAst',
    'CatchClauseAst', 'ReturnStatementAst')

function ConvertTo-L2BareCommandName([string]$Name) {
    # `function script:Invoke-L2Query` and a call to `script:Invoke-L2Query` name the same function.
    if ([string]::IsNullOrEmpty($Name)) { return $null }
    return ($Name -replace '^(?i)(global|script|local|private):', '')
}

# Where a pipeline's output ends up: 'array' (the nearest consumer is @(...)), a FunctionDefinitionAst (it is that
# function's output), or $null (assigned, an argument, a condition, script-level, inside a script block ...).
function Get-L2OutputSink([System.Management.Automation.Language.PipelineBaseAst]$Pipeline) {
    $node = $Pipeline.Parent
    if ($null -eq $node -or $node.GetType().Name -notin @('StatementBlockAst', 'NamedBlockAst', 'ReturnStatementAst')) {
        return $null
    }
    while ($null -ne $node -and $node.GetType().Name -in $script:TransparentStatements) {
        $child = $node
        $node = $node.Parent
        # A loop or branch is transparent for its BODY only; its condition is consumed by the statement itself.
        if ($null -ne $node -and $node.GetType().Name -in $script:TransparentStatements -and
            $child.GetType().Name -notin @('StatementBlockAst', 'NamedBlockAst', 'CatchClauseAst') -and
            $node.GetType().Name -notin @('StatementBlockAst', 'NamedBlockAst')) {
            return $null
        }
    }
    if ($null -eq $node) { return $null }
    if ($node -is [System.Management.Automation.Language.ArrayExpressionAst]) { return 'array' }
    if ($node -is [System.Management.Automation.Language.ScriptBlockAst] -and
        $node.Parent -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Parent.Parent -isnot [System.Management.Automation.Language.FunctionMemberAst]) {
        return $node.Parent
    }
    return $null
}

function Test-L2EmitsWhole([System.Management.Automation.Language.PipelineAst]$Pipeline) {
    $elements = $Pipeline.PipelineElements
    $last = $elements[$elements.Count - 1]
    # `, $rows` -- a unary comma parses as an array literal with a single element.
    if ($elements.Count -eq 1 -and $last -is [System.Management.Automation.Language.CommandExpressionAst] -and
        $last.Expression -is [System.Management.Automation.Language.ArrayLiteralAst] -and
        $last.Expression.Elements.Count -eq 1) {
        return $true
    }
    if ($last -is [System.Management.Automation.Language.CommandAst] -and $last.GetCommandName() -in @('Write-Output', 'write', 'echo')) {
        return [bool]@($last.CommandElements | Where-Object {
                $_ -is [System.Management.Automation.Language.CommandParameterAst] -and
                'NoEnumerate'.StartsWith($_.ParameterName, [System.StringComparison]::OrdinalIgnoreCase)
            }).Count
    }
    return $false
}

function Read-L2ScriptAst([string]$Path) {
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw "Could not parse ${Path}: $($errors[0].Message)" }
    return $ast
}

# Leaf names of the scripts this one dot-sources: `. (Join-Path $PSScriptRoot 'X.ps1')`, `. "$PSScriptRoot/X.ps1"`.
function Get-L2DotSourcedLeaves([System.Management.Automation.Language.Ast]$Ast) {
    $leaves = foreach ($command in $Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
        if ($command.InvocationOperator -ne [System.Management.Automation.Language.TokenKind]::Dot) { continue }
        foreach ($text in $command.FindAll({ param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
                    $n -is [System.Management.Automation.Language.ExpandableStringExpressionAst] }, $true)) {
            if ([string]$text.Value -match '([^\\/]+\.ps1)$') { $Matches[1] }
        }
    }
    return , @($leaves)
}

<#
.SYNOPSIS
    Parses the scripts and classifies every function in them. Returns one object the other two functions read.
#>
function Get-L2WholeArrayModel {
    param([Parameter(Mandatory)][string[]]$Path)

    $files = foreach ($file in $Path) {
        $ast = Read-L2ScriptAst $file
        $functions = foreach ($definition in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
            if ($definition.Parent -is [System.Management.Automation.Language.FunctionMemberAst]) { continue }
            [pscustomobject]@{
                Name        = ConvertTo-L2BareCommandName $definition.Name
                File        = $file
                Line        = $definition.Extent.StartLineNumber
                Definition  = $definition
                Whole       = $false
                Via         = $null
                PassThrough = [System.Collections.Generic.List[string]]::new()
            }
        }
        $functions = @($functions)
        $byDefinition = @{}
        foreach ($function in $functions) { $byDefinition[$function.Definition] = $function }

        foreach ($pipeline in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.PipelineAst] }, $true)) {
            $sink = Get-L2OutputSink $pipeline
            if ($sink -isnot [System.Management.Automation.Language.FunctionDefinitionAst]) { continue }
            $owner = $byDefinition[$sink]
            if (Test-L2EmitsWhole $pipeline) {
                $owner.Whole = $true
                $owner.Via ??= "line $($pipeline.Extent.StartLineNumber)"
            } elseif ($pipeline.PipelineElements.Count -eq 1 -and
                $pipeline.PipelineElements[0] -is [System.Management.Automation.Language.CommandAst]) {
                $called = ConvertTo-L2BareCommandName $pipeline.PipelineElements[0].GetCommandName()
                if ($called) { $owner.PassThrough.Add($called) }
            }
        }
        [pscustomobject]@{
            Path = $file; Shared = ($file -like '*.psm1'); Ast = $ast; Functions = $functions
            DotSources = (Get-L2DotSourcedLeaves $ast)
        }
    }
    $files = @($files)
    $dotSourced = @($files | ForEach-Object { $_.DotSources } | Sort-Object -Unique)
    foreach ($file in $files) {
        if ((Split-Path -Leaf $file.Path) -in $dotSourced) { $file.Shared = $true }
    }

    $model = [pscustomobject]@{ Files = $files }

    # Pass-through to a fixpoint: a function that returns a whole-array function's output bare is one itself.
    do {
        $changed = $false
        foreach ($file in $files) {
            foreach ($function in $file.Functions) {
                if ($function.Whole) { continue }
                foreach ($called in $function.PassThrough) {
                    if (Test-L2WholeArrayName -Model $model -File $file -Name $called) {
                        $function.Whole = $true
                        $function.Via = "returns $called"
                        $changed = $true
                        break
                    }
                }
            }
        }
    } while ($changed)

    return $model
}

function Test-L2WholeArrayName {
    param([Parameter(Mandatory)][object]$Model, [Parameter(Mandatory)][object]$File, [string]$Name)

    if ([string]::IsNullOrEmpty($Name)) { return $false }
    # PowerShell resolves command names case-insensitively; so must this, or `invoke-l2query` walks past.
    $local = @($File.Functions | Where-Object { $_.Name -ieq $Name })
    if ($local.Count -gt 0) { return [bool]@($local | Where-Object Whole).Count }
    foreach ($shared in $Model.Files) {
        if (-not $shared.Shared -or $shared.Path -eq $File.Path) { continue }
        if (@($shared.Functions | Where-Object { $_.Name -ieq $Name -and $_.Whole }).Count -gt 0) { return $true }
    }
    return $false
}

<#
.SYNOPSIS
    Every function the model classified as returning its array whole: Name, File, Line, Via.
#>
function Get-L2WholeArrayFunctions {
    param([Parameter(Mandatory)][object]$Model)

    $found = foreach ($file in $Model.Files) {
        foreach ($function in $file.Functions) {
            if ($function.Whole) {
                [pscustomobject]@{ Name = $function.Name; File = $file.Path; Line = $function.Line; Via = $function.Via; Shared = $file.Shared }
            }
        }
    }
    return , @($found)
}

<#
.SYNOPSIS
    Every call that enumerates a whole-array function's output: File, Line, Helper, Shape (wrapped|piped|foreach), Text.
#>
function Get-L2WholeArrayMisuse {
    param([Parameter(Mandatory)][object]$Model)

    $findings = foreach ($file in $Model.Files) {
        foreach ($command in $file.Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
            $pipeline = $command.Parent
            if ($pipeline -isnot [System.Management.Automation.Language.PipelineAst]) { continue }
            if ($pipeline.PipelineElements[0] -ne $command) { continue }
            $name = ConvertTo-L2BareCommandName $command.GetCommandName()
            if (-not (Test-L2WholeArrayName -Model $Model -File $file -Name $name)) { continue }

            $shape = if ($pipeline.PipelineElements.Count -gt 1) {
                'piped'
            } elseif ($pipeline.Parent -is [System.Management.Automation.Language.ForEachStatementAst] -and
                $pipeline.Parent.Condition -eq $pipeline) {
                'foreach'
            } elseif ((Get-L2OutputSink $pipeline) -is [string]) {
                'wrapped'
            } else { $null }
            if ($null -eq $shape) { continue }

            [pscustomobject]@{
                File   = $file.Path
                Line   = $command.Extent.StartLineNumber
                Helper = $name
                Shape  = $shape
                Text   = ($pipeline.Extent.Text -split "`r?`n")[0].Trim()
            }
        }
    }
    return , @($findings)
}

Export-ModuleMember -Function Get-L2WholeArrayModel, Get-L2WholeArrayFunctions, Get-L2WholeArrayMisuse
