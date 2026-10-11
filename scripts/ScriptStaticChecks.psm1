#Requires -Version 7
<#
Offline static checks over the repository's PowerShell (control-server#583). Scripts/Test-ScriptStaticChecks.ps1 runs
them against its fixtures and against scripts/; this module only scans and reports.

These are regression guards for the commonest shapes, not proofs. Each check's header lists what it cannot see.
#>

Set-StrictMode -Version Latest

# Every check reads the same files; each is parsed once per session, keyed by path and last write.
$script:ParseCache = @{}
function Get-ParsedScript([string]$File) {
    $full = [IO.Path]::GetFullPath($File)
    $key = "$full|$((Get-Item -LiteralPath $full).LastWriteTimeUtc.Ticks)"
    if (-not $script:ParseCache.ContainsKey($key)) {
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($full, [ref]$null, [ref]$errors)
        $script:ParseCache[$key] = [pscustomobject]@{ Ast = $ast; Errors = @($errors) }
    }
    return $script:ParseCache[$key]
}

<#
Parses every file with Parser::ParseFile and returns { Files; Findings = { File, Line, Column, Message } }.

ParseFile, not ParseInput with the errors thrown away: an AST check built on ParseInput keeps passing on a file that does
not parse at all (control-server#472, whose script failed on "$InstanceDefinitionPath:" and passed a dozen AST checks).
#>
function Test-ScriptParse {
    param([Parameter(Mandatory)][string[]]$Path)
    $findings = [System.Collections.Generic.List[object]]::new()
    $files = 0
    foreach ($file in $Path) {
        $files++
        foreach ($e in @((Get-ParsedScript $file).Errors)) {
            $findings.Add([pscustomobject]@{
                File    = $file
                Line    = $e.Extent.StartLineNumber
                Column  = $e.Extent.StartColumnNumber
                Message = "$($e.ErrorId): $($e.Message)"
            })
        }
    }
    return [pscustomobject]@{ Files = $files; Findings = $findings.ToArray() }
}

# --- 2. properties read off a helper's literal object ----------------------------------------------------------------

<#
The helpers whose return shape the property audit knows, written out rather than derived (control-server#583): each must
build exactly one [pscustomobject]@{...} literal with constant keys and return only that literal, a `, @($x)` / `, $x`
wrap of what a loop emitted, or $null. Get-HelperShapeCatalog re-reads every one and refuses the list when one no longer
does, so a helper rewritten to return something else stops this check instead of shrinking it.

FilterOf: the function returns another listed helper's output through Where-Object only, so it has that helper's shape.
Relay: the function hands back what its script-block parameter of that name produced (the Wait-* family), so a variable
assigned from it has the shape of whatever the script block's last statement resolves to.

Picked by use in scripts/l2 on fp/v2-impl@da6e40e2: the shared inbound/outbound readers every real-onboard and G3
scenario goes through, including the two bc22cfd25 turned on. Scenario-local helpers (the several different Get-Inbound)
are not listed: their shapes differ file to file.
#>
$script:HelperShapes = @(
    @{ Name = 'Get-L2Inbound';                File = 'l2/L2RealStation.psm1' }
    @{ Name = 'Get-L2OperationResults';       File = 'l2/L2RealStation.psm1'; FilterOf = 'Get-L2Inbound' }
    @{ Name = 'Get-L2OperationProgress';      File = 'l2/L2RealStation.psm1' }
    @{ Name = 'Get-L2RealInbound';            File = 'l2/L2RealOnboard.psm1' }
    @{ Name = 'Get-L2RealOutbound';           File = 'l2/L2RealOnboard.psm1' }
    @{ Name = 'Get-G3Inbound';                File = 'l2/scenarios/G3RecoveryCommon.ps1' }
    @{ Name = 'Get-G3Progress';               File = 'l2/scenarios/G3RecoveryCommon.ps1' }
    @{ Name = 'Invoke-G3UnknownLoad';         File = 'l2/scenarios/G3RecoveryCommon.ps1' }
    @{ Name = 'Get-L2DemandJourneySnapshots'; File = 'l2/L2TaskTypeJourney.psm1' }
    @{ Name = 'Wait-L2Change';                File = 'l2/L2Change.psm1' }
    @{ Name = 'Start-L2Process';              File = 'l2/L2.psm1' }
    @{ Name = 'Wait-L2Condition';             File = 'l2/L2.psm1';                Relay = 'Probe' }
    @{ Name = 'Wait-L2ConditionOrLast';       File = 'l2/L2ConditionOrLast.psm1'; Relay = 'Probe' }
    @{ Name = 'Wait-L2RealOrLast';            File = 'l2/L2RealOnboard.psm1';     Relay = 'Probe' }
)

# Read on any object under StrictMode without a property of that name: what an array or a pscustomobject always has.
$script:IntrinsicProperties = @('Count', 'Length', 'PSObject', 'PSTypeNames')

function Get-StaticFunction([System.Management.Automation.Language.Ast]$Ast, [string]$Name) {
    return @($Ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $Name }, $true))
}

function Get-LiteralObject([System.Management.Automation.Language.Ast]$Body) {
    return @($Body.FindAll({ param($a)
        $a -is [System.Management.Automation.Language.ConvertExpressionAst] -and $a.Type.TypeName.FullName -eq 'pscustomobject' -and
            $a.Child -is [System.Management.Automation.Language.HashtableAst] }, $true))
}

# Strips `, x`, `@(x)`, `(x)`, `$(x)` and a one-element pipeline down to what is inside; stops at more than one statement.
function Get-Unwrapped([System.Management.Automation.Language.Ast]$Ast) {
    while ($true) {
        if ($Ast -is [System.Management.Automation.Language.PipelineAst] -and $Ast.PipelineElements.Count -eq 1 -and
            $Ast.PipelineElements[0] -is [System.Management.Automation.Language.CommandExpressionAst]) {
            $Ast = $Ast.PipelineElements[0].Expression
        } elseif ($Ast -is [System.Management.Automation.Language.CommandExpressionAst]) {
            # An assignment's right-hand side that is a single expression arrives as this, not as a pipeline.
            $Ast = $Ast.Expression
        } elseif ($Ast -is [System.Management.Automation.Language.UnaryExpressionAst] -and $Ast.TokenKind -eq 'Comma') {
            $Ast = $Ast.Child
        } elseif ($Ast -is [System.Management.Automation.Language.ArrayLiteralAst] -and $Ast.Elements.Count -eq 1) {
            # The parser reads a unary `, x` as a one-element array literal.
            $Ast = $Ast.Elements[0]
        } elseif ($Ast -is [System.Management.Automation.Language.ParenExpressionAst]) {
            $Ast = $Ast.Pipeline
        } elseif ($Ast -is [System.Management.Automation.Language.ArrayExpressionAst] -or $Ast -is [System.Management.Automation.Language.SubExpressionAst]) {
            $statements = $Ast.SubExpression.Statements
            if ($statements.Count -ne 1) { return $Ast }
            $Ast = $statements[0]
        } else {
            return $Ast
        }
    }
}

function Get-HelperShapeCatalog {
    param(
        [Parameter(Mandatory)][string]$ScriptRoot,
        # Replaces the written-out list; the self-check uses it to show a wrong entry is refused.
        [object[]]$Entries = $script:HelperShapes
    )
    $parsed = @{}
    $byName = @{}
    $catalog = foreach ($entry in $Entries) {
        $file = Join-Path $ScriptRoot $entry.File
        if (-not $parsed.ContainsKey($file)) {
            if (-not (Test-Path -LiteralPath $file)) { throw "Helper catalog: $($entry.Name) is listed in $($entry.File), which does not exist." }
            $parsed[$file] = (Get-ParsedScript $file).Ast
        }
        $definitions = @(Get-StaticFunction $parsed[$file] $entry.Name)
        if ($definitions.Count -ne 1) { throw "Helper catalog: $($entry.Name) is defined $($definitions.Count) times in $($entry.File), not once." }
        $function = $definitions[0]

        if ($entry['Relay']) {
            $parameter = @($function.FindAll({ param($a) $a -is [System.Management.Automation.Language.ParameterAst] -and
                $a.Name.VariablePath.UserPath -eq $entry.Relay -and $a.StaticType -eq [scriptblock] }, $true))
            if ($parameter.Count -ne 1) { throw "Helper catalog: $($entry.Name) has no [scriptblock]`$$($entry.Relay) to relay." }
            [pscustomobject]@{ Name = $entry.Name; Properties = $null; Relay = $entry.Relay }
            continue
        }

        $returns = @($function.Body.FindAll({ param($a) $a -is [System.Management.Automation.Language.ReturnStatementAst] }, $true))
        if ($entry['FilterOf']) {
            if (-not $byName.ContainsKey($entry.FilterOf)) { throw "Helper catalog: $($entry.Name) filters $($entry.FilterOf), which must be listed before it." }
            if ($returns.Count -eq 0) { throw "Helper catalog: $($entry.Name) has no return to read." }
            foreach ($return in $returns) {
                $inner = Get-Unwrapped $return.Pipeline
                $elements = if ($inner -is [System.Management.Automation.Language.PipelineAst]) { @($inner.PipelineElements) } else { @() }
                $source = if ($elements.Count -gt 0) { $elements[0] } else { $null }
                if ($source -is [System.Management.Automation.Language.CommandExpressionAst]) { $source = Get-Unwrapped $source.Expression }
                if ($source -is [System.Management.Automation.Language.PipelineAst] -and $source.PipelineElements.Count -eq 1) { $source = $source.PipelineElements[0] }
                $sourceOk = $source -is [System.Management.Automation.Language.CommandAst] -and $source.GetCommandName() -eq $entry.FilterOf
                $filtersOk = @($elements | Select-Object -Skip 1 | Where-Object {
                    -not ($_ -is [System.Management.Automation.Language.CommandAst] -and $_.GetCommandName() -in 'Where-Object', 'where', '?') }).Count -eq 0
                if (-not ($sourceOk -and $filtersOk)) {
                    throw "Helper catalog: $($entry.Name) no longer returns $($entry.FilterOf) through Where-Object only (line $($return.Extent.StartLineNumber))."
                }
            }
            $byName[$entry.Name] = $byName[$entry.FilterOf]
            [pscustomobject]@{ Name = $entry.Name; Properties = $byName[$entry.FilterOf]; Relay = $null }
            continue
        }

        $literals = @(Get-LiteralObject $function.Body)
        if ($literals.Count -ne 1) { throw "Helper catalog: $($entry.Name) builds $($literals.Count) [pscustomobject] literals, not exactly one." }
        $keys = foreach ($pair in $literals[0].Child.KeyValuePairs) {
            if ($pair.Item1 -isnot [System.Management.Automation.Language.StringConstantExpressionAst]) {
                throw "Helper catalog: $($entry.Name)'s literal has a computed key ($($pair.Item1.Extent.Text))."
            }
            $pair.Item1.Value
        }
        foreach ($return in $returns) {
            $value = if ($null -eq $return.Pipeline) { $null } else { Get-Unwrapped $return.Pipeline }
            $ok = $null -eq $value -or $value -eq $literals[0] -or $value -is [System.Management.Automation.Language.VariableExpressionAst]
            if (-not $ok) { throw "Helper catalog: $($entry.Name) returns something other than its literal object at line $($return.Extent.StartLineNumber)." }
        }
        $properties = @($keys | Sort-Object)
        $byName[$entry.Name] = $properties
        [pscustomobject]@{ Name = $entry.Name; Properties = $properties; Relay = $null }
    }
    return @($catalog)
}

<#
Resolves the right-hand side of an assignment, or a foreach's collection, to the listed helper whose shape it has, or $null.
Recognised: a call to a listed helper; that wrapped in (), @(), $() or indexed; that piped through Where-Object, Sort-Object
or Select-Object -First/-Last/-Skip/-Unique; a relay (Wait-L2RealOrLast and kin) whose -Probe resolves to one.
#>
function Resolve-HelperShape([System.Management.Automation.Language.Ast]$Ast, [hashtable]$Shapes, [hashtable]$Relays, [hashtable]$Local) {
    $Ast = Get-Unwrapped $Ast
    if ($Ast -is [System.Management.Automation.Language.IndexExpressionAst]) {
        return Resolve-HelperShape $Ast.Target $Shapes $Relays $Local
    }
    if ($Ast -is [System.Management.Automation.Language.PipelineAst]) {
        $elements = @($Ast.PipelineElements)
        for ($i = 1; $i -lt $elements.Count; $i++) {
            $next = $elements[$i]
            if ($next -isnot [System.Management.Automation.Language.CommandAst]) { return $null }
            $name = $next.GetCommandName()
            if ($name -in 'Where-Object', 'where', '?', 'Sort-Object', 'sort') { continue }
            if ($name -in 'Select-Object', 'select') {
                $shapeKept = $true
                foreach ($element in $next.CommandElements) {
                    if ($element -is [System.Management.Automation.Language.CommandParameterAst] -and $element.ParameterName -notin 'First', 'Last', 'Skip', 'Unique') { $shapeKept = $false }
                }
                if ($shapeKept) { continue }
            }
            return $null
        }
        $Ast = $elements[0]
        if ($Ast -is [System.Management.Automation.Language.CommandExpressionAst]) { return Resolve-HelperShape $Ast.Expression $Shapes $Relays $Local }
    }
    if ($Ast -is [System.Management.Automation.Language.CommandAst]) {
        $name = $Ast.GetCommandName()
        if (-not $name) { return $null }
        $name = ($name -split '\\')[-1]
        if ($Local.ContainsKey($name)) { $Local[$name]++; return $null }
        if ($Shapes.ContainsKey($name)) { return $name }
        if ($Relays.ContainsKey($name)) {
            $elements = @($Ast.CommandElements)
            for ($i = 1; $i -lt $elements.Count; $i++) {
                $element = $elements[$i]
                if ($element -is [System.Management.Automation.Language.CommandParameterAst] -and $element.ParameterName -eq $Relays[$name]) {
                    $argument = $element.Argument ?? ($i + 1 -lt $elements.Count ? $elements[$i + 1] : $null)
                    if ($argument -isnot [System.Management.Automation.Language.ScriptBlockExpressionAst]) { return $null }
                    $statements = @($argument.ScriptBlock.EndBlock.Statements)
                    if ($statements.Count -eq 0) { return $null }
                    return Resolve-HelperShape $statements[-1] $Shapes $Relays $Local
                }
            }
        }
    }
    return $null
}

# $script:x and $x name the same variable for this purpose; the scope prefix is dropped.
function Get-VariableName([System.Management.Automation.Language.VariableExpressionAst]$Variable) {
    return $Variable.VariablePath.UserPath -replace '^(script|global|local|private|using):', ''
}

function Get-ScopeKey([System.Management.Automation.Language.Ast]$Ast) {
    for ($p = $Ast.Parent; $null -ne $p; $p = $p.Parent) {
        if ($p -is [System.Management.Automation.Language.FunctionDefinitionAst]) { return "$($p.Extent.StartOffset)" }
    }
    return 'file'
}

<#
For each variable whose every assignment in its scope (the enclosing function, or the file) resolves to the same listed
helper, checks each $var.Name read against that helper's literal keys. A variable assigned from anything else as well is
skipped and counted (Unresolved). A file that defines its own function of a listed name is judged by neither shape: its
calls of that name are counted (Shadowed) and skipped.

Blind spots, deliberately not chased (control-server#583; a scan guard is never complete): a variable set by [ref],
-OutVariable, Set-Variable or a loop other than foreach; a read in a function of a variable it did not assign (dynamic
scope); a read through anything but $var.Name, such as $var.$name, $var['Name'] or $_ in a pipeline; nested properties
($x.Payload.foo is checked as Payload only); helpers not in the written-out list, scenario-local ones included.
#>
function Find-HelperPropertyMisuse {
    param([Parameter(Mandatory)][string[]]$Path, [Parameter(Mandatory)][object[]]$Catalog)
    $shapes = @{}; $relays = @{}; $homes = @{}
    foreach ($entry in $Catalog) {
        if ($entry.Relay) { $relays[$entry.Name] = $entry.Relay } else { $shapes[$entry.Name] = $entry.Properties }
    }
    foreach ($entry in $script:HelperShapes) { $homes[$entry.Name] = $entry.File }

    $findings = [System.Collections.Generic.List[object]]::new()
    $variables = 0; $reads = 0; $unresolved = 0; $shadowed = 0
    foreach ($file in $Path) {
        $ast = (Get-ParsedScript $file).Ast
        $normalised = $file.Replace('\', '/')
        # A file's own definition of a listed name wins, except in the file the list points at.
        $local = @{}
        foreach ($definition in $ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
            if ($shapes.ContainsKey($definition.Name) -and
                -not ($homes.ContainsKey($definition.Name) -and $normalised.EndsWith("/$($homes[$definition.Name])", [StringComparison]::OrdinalIgnoreCase))) {
                $local[$definition.Name] = 0
            }
        }

        # Only a right-hand side whose text names a listed, relayed or shadowing function is worth resolving: a cheap
        # pre-filter, since resolving every one of the repository's assignments is most of this check's time.
        $names = @($shapes.PSBase.Keys) + @($relays.PSBase.Keys) + @($local.PSBase.Keys) | ForEach-Object { [regex]::Escape($_) }
        $mentions = [regex]::new("(?<![\w-])($($names -join '|'))(?![\w-])", 'IgnoreCase')

        # scope -> variable -> what each assignment resolved to ($null for unrecognised)
        $assigned = @{}
        $add = {
            param([string]$Scope, [string]$Variable, $Helper)
            if (-not $assigned.ContainsKey($Scope)) { $assigned[$Scope] = @{} }
            if (-not $assigned[$Scope].ContainsKey($Variable)) { $assigned[$Scope][$Variable] = [System.Collections.Generic.List[object]]::new() }
            # '' for unrecognised, not $null: a $null in the list would be dropped by the pipeline below.
            $assigned[$Scope][$Variable].Add([string]$Helper)
        }
        # Every assignment as (node, variable, helper), the helper resolved only when the text mentions one; then scopes and
        # records only for the variables some assignment resolved for, which is a few hundred of the tens of thousands.
        $entries = [System.Collections.Generic.List[object]]::new()
        $resolvedNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($node in $ast.FindAll({ param($a)
            $a -is [System.Management.Automation.Language.AssignmentStatementAst] -or $a -is [System.Management.Automation.Language.ForEachStatementAst] -or
                $a -is [System.Management.Automation.Language.ParameterAst] }, $true)) {
            if ($node -is [System.Management.Automation.Language.ParameterAst]) {
                $entries.Add(@($node, $node.Name, ''))
            } elseif ($node -is [System.Management.Automation.Language.ForEachStatementAst]) {
                $helper = if ($mentions.IsMatch($node.Condition.Extent.Text)) { [string](Resolve-HelperShape $node.Condition $shapes $relays $local) } else { '' }
                $entries.Add(@($node, $node.Variable, $helper))
            } elseif ($node.Left -is [System.Management.Automation.Language.VariableExpressionAst]) {
                $helper = if ($node.Operator -eq 'Equals' -and $mentions.IsMatch($node.Right.Extent.Text)) { [string](Resolve-HelperShape $node.Right $shapes $relays $local) } else { '' }
                $entries.Add(@($node, $node.Left, $helper))
            } else {
                foreach ($target in $node.Left.FindAll({ param($a) $a -is [System.Management.Automation.Language.VariableExpressionAst] }, $true)) {
                    $entries.Add(@($node, $target, ''))
                }
            }
            if ($entries[-1][2] -ne '') { $null = $resolvedNames.Add((Get-VariableName $entries[-1][1])) }
        }
        foreach ($entry in $entries) {
            $variable = $entry[1].VariablePath.UserPath -replace '^(script|global|local|private|using):', ''
            if (-not $resolvedNames.Contains($variable)) { continue }
            & $add (Get-ScopeKey $entry[0]) $variable $entry[2]
        }
        $known = @{}
        $candidates = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($scope in $assigned.PSBase.Keys) {
            $known[$scope] = @{}
            foreach ($variable in $assigned[$scope].PSBase.Keys) {
                $helpers = @([System.Collections.Generic.HashSet[string]]::new([string[]]$assigned[$scope][$variable]))
                if ($helpers.Count -eq 1 -and $helpers[0] -ne '') {
                    $known[$scope][$variable] = $helpers[0]
                    $null = $candidates.Add($variable)
                    $variables++
                } elseif ($helpers.Count -gt 1) {
                    $unresolved++
                }
            }
        }

        foreach ($read in $ast.FindAll({ param($a)
            $a -is [System.Management.Automation.Language.MemberExpressionAst] -and $a -isnot [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                -not $a.Static -and $a.Expression -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $a.Member -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true)) {
            $variable = $read.Expression.VariablePath.UserPath -replace '^(script|global|local|private|using):', ''
            if (-not $candidates.Contains($variable)) { continue }
            $scope = Get-ScopeKey $read
            if (-not $known.ContainsKey($scope) -or -not $known[$scope].ContainsKey($variable)) { continue }
            $helper = $known[$scope][$variable]
            $reads++
            $property = $read.Member.Value
            if ($property -in $shapes[$helper] -or $property -in $script:IntrinsicProperties) { continue }
            $findings.Add([pscustomobject]@{
                File    = $file
                Line    = $read.Extent.StartLineNumber
                Message = "`$$variable.$property`: $helper returns no property $property (it has $($shapes[$helper] -join ', ')); under StrictMode the read throws"
            })
        }
        foreach ($count in $local.PSBase.Values) { $shadowed += $count }
    }
    return [pscustomobject]@{
        Files = $Path.Count; Variables = $variables; Reads = $reads; Unresolved = $unresolved; Shadowed = $shadowed
        Findings = $findings.ToArray()
    }
}

# --- 3. a [string] helper's result compared with $null ----------------------------------------------------------------

<#
True for a NULL-folding reader: it takes any SQL (a parameter named Sql), every return is a [string] cast or $null with at
least one cast, and no cast's operand was first tested by an `if` that returns $null -- so a NULL column comes back as ''.
#>
function Test-NullFoldingReader([System.Management.Automation.Language.FunctionDefinitionAst]$Function) {
    $takesSql = @($Function.FindAll({ param($a) $a -is [System.Management.Automation.Language.ParameterAst] -and
        (Get-VariableName $a.Name) -eq 'Sql' -and (Get-ScopeKey $a) -eq "$($Function.Extent.StartOffset)" }, $true)).Count -gt 0
    if (-not $takesSql) { return $false }
    $returns = @($Function.Body.FindAll({ param($a) $a -is [System.Management.Automation.Language.ReturnStatementAst] }, $true))
    $casts = [System.Collections.Generic.List[string]]::new()
    foreach ($return in $returns) {
        $value = if ($null -eq $return.Pipeline) { $null } else { Get-Unwrapped $return.Pipeline }
        if ($null -eq $value -or ($value -is [System.Management.Automation.Language.VariableExpressionAst] -and $value.VariablePath.UserPath -eq 'null')) { continue }
        if ($value -is [System.Management.Automation.Language.ConvertExpressionAst] -and $value.Type.TypeName.FullName -eq 'string') {
            $casts.Add($value.Child.Extent.Text)
            continue
        }
        return $false
    }
    if ($casts.Count -eq 0) { return $false }
    # A guard: an if whose condition names the cast operand and whose branch returns $null (Get-L2RealScalar's shape).
    foreach ($if in $Function.Body.FindAll({ param($a) $a -is [System.Management.Automation.Language.IfStatementAst] }, $true)) {
        foreach ($clause in $if.Clauses) {
            $returnsNull = @($clause.Item2.FindAll({ param($a) $a -is [System.Management.Automation.Language.ReturnStatementAst] -and
                $null -ne $a.Pipeline -and $a.Pipeline.Extent.Text -eq '$null' }, $true)).Count -gt 0
            if (-not $returnsNull) { continue }
            foreach ($operand in $casts) {
                if ($clause.Item1.Extent.Text.Contains($operand, [StringComparison]::OrdinalIgnoreCase)) { return $false }
            }
        }
    }
    return $true
}

<#
The NULL-folding readers (Test-NullFoldingReader) defined in the given files. A name defined more than once is kept only if
every definition qualifies. Get-G3Scalar (G3RecoveryCommon.ps1) is the first: `return [string]$rows[0].Value` turns a NULL
column into '', so `$null -eq` on its result is true for "no row" and false for "row with NULL" -- bc22cfd25's G3-07-44 read
the second as the first.

Not judged, on purpose: a reader of one fixed column (Get-L2JourneyStage and kin) returns $null for "no row", which is what
its callers test for, and whether that column can be NULL is the schema's business; a reader that passes another reader's
result straight back (`function Get-Stage { return Get-Scalar ... }`) is not followed.
#>
function Get-StringHelperCatalog {
    param([Parameter(Mandatory)][string[]]$Path)
    $verdicts = @{}
    foreach ($file in $Path) {
        $ast = (Get-ParsedScript $file).Ast
        foreach ($function in $ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
            $ok = Test-NullFoldingReader $function
            $verdicts[$function.Name] = $verdicts.ContainsKey($function.Name) ? ($verdicts[$function.Name] -and $ok) : $ok
        }
    }
    return @($verdicts.PSBase.Keys | Where-Object { $verdicts[$_] } | Sort-Object)
}

<#
Reports `$null -eq/-ne X` and `X -eq/-ne $null` where X is a call of a [string] helper, or a variable whose every assignment
in its scope is one. A file's own definition of a helper name is judged on its own (string or not).

Blind spots: a result passed through anything first (a parameter, a property, a pipeline, [string] again), -is [DBNull]
and other forms of the same test, `if (-not $x)` (which is right for '' anyway), and helpers that return a string without
a [string] cast.
#>
function Find-StringHelperNullComparison {
    param(
        [Parameter(Mandatory)][string[]]$Path, [string[]]$Helpers, [object[]]$Exemptions = @(),
        # Where an exemption's Premise.Snapshot is read from; this module sits in scripts/.
        [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
    )
    $findings = [System.Collections.Generic.List[object]]::new()
    $comparisons = 0; $variables = 0; $exempted = 0
    $used = @{}
    $premises = @{}
    foreach ($e in $Exemptions) { $premises[$e] = Test-StringNullExemptionPremise -RepositoryRoot $RepositoryRoot -Exemption $e }
    foreach ($file in $Path) {
        $ast = (Get-ParsedScript $file).Ast
        $effective = @{}
        foreach ($name in $Helpers) { $effective[$name] = $true }
        foreach ($function in $ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
            $effective[$function.Name] = Test-NullFoldingReader $function
        }
        $isCall = {
            param($Node)
            $node = Get-Unwrapped $Node
            if ($node -is [System.Management.Automation.Language.PipelineAst] -and $node.PipelineElements.Count -eq 1) { $node = $node.PipelineElements[0] }
            if ($node -isnot [System.Management.Automation.Language.CommandAst]) { return '' }
            $name = $node.GetCommandName()
            if (-not $name) { return '' }
            $name = ($name -split '\\')[-1]
            return ($effective.ContainsKey($name) -and $effective[$name]) ? $name : ''
        }

        $assigned = @{}
        foreach ($node in $ast.FindAll({ param($a)
            $a -is [System.Management.Automation.Language.AssignmentStatementAst] -or $a -is [System.Management.Automation.Language.ForEachStatementAst] -or
                $a -is [System.Management.Automation.Language.ParameterAst] }, $true)) {
            $scope = Get-ScopeKey $node
            # Each entry is a variable name and what it was assigned from ('' for anything but a [string] helper call).
            $pairs = [System.Collections.Generic.List[object]]::new()
            if ($node -is [System.Management.Automation.Language.ParameterAst]) {
                $pairs.Add(@((Get-VariableName $node.Name), ''))
            } elseif ($node -is [System.Management.Automation.Language.ForEachStatementAst]) {
                $pairs.Add(@((Get-VariableName $node.Variable), ''))
            } elseif ($node.Left -is [System.Management.Automation.Language.VariableExpressionAst]) {
                $pairs.Add(@((Get-VariableName $node.Left), ($node.Operator -eq 'Equals' ? (& $isCall $node.Right) : '')))
            } else {
                foreach ($target in $node.Left.FindAll({ param($a) $a -is [System.Management.Automation.Language.VariableExpressionAst] }, $true)) {
                    $pairs.Add(@((Get-VariableName $target), ''))
                }
            }
            foreach ($pair in $pairs) {
                $key = "$scope|$($pair[0])"
                if (-not $assigned.ContainsKey($key)) { $assigned[$key] = [System.Collections.Generic.List[string]]::new() }
                $assigned[$key].Add($pair[1])
            }
        }
        $known = @{}
        foreach ($key in $assigned.PSBase.Keys) {
            $sources = @([System.Collections.Generic.HashSet[string]]::new([string[]]$assigned[$key]))
            if ($sources.Count -eq 1 -and $sources[0] -ne '') { $known[$key] = $sources[0]; $variables++ }
        }

        foreach ($compare in $ast.FindAll({ param($a)
            $a -is [System.Management.Automation.Language.BinaryExpressionAst] -and $a.Operator -in 'Ieq', 'Ine', 'Ceq', 'Cne' }, $true)) {
            $isNull = { param($n) $n -is [System.Management.Automation.Language.VariableExpressionAst] -and $n.VariablePath.UserPath -eq 'null' }
            $other = if (& $isNull $compare.Left) { $compare.Right } elseif (& $isNull $compare.Right) { $compare.Left } else { $null }
            if ($null -eq $other) { continue }
            $comparisons++
            $helper = & $isCall $other
            $what = "($($other.Extent.Text))"
            if ($helper -eq '') {
                $inner = Get-Unwrapped $other
                if ($inner -is [System.Management.Automation.Language.VariableExpressionAst]) {
                    $key = "$(Get-ScopeKey $compare)|$(Get-VariableName $inner)"
                    if ($known.ContainsKey($key)) { $helper = $known[$key]; $what = $inner.Extent.Text }
                }
            }
            if ($helper -eq '') { continue }
            $function = ''
            for ($p = $compare.Parent; $null -ne $p; $p = $p.Parent) {
                if ($p -is [System.Management.Automation.Language.FunctionDefinitionAst]) { $function = $p.Name; break }
            }
            $normalised = $file.Replace('\', '/')
            $exemption = @($Exemptions | Where-Object {
                $normalised.EndsWith("/$($_.File)", [StringComparison]::OrdinalIgnoreCase) -and $_.Function -eq $function -and "`$$($_.Variable)" -eq $what })
            $void = ''
            if ($exemption.Count -gt 0) {
                $used[$exemption[0]] = $true
                if ($premises[$exemption[0]].Holds) { $exempted++; continue }
                # Matched, but what it rests on no longer holds: report, and say why the exemption did not apply.
                $void = " (exemption void -- $($premises[$exemption[0]].Detail))"
            }
            $findings.Add([pscustomobject]@{
                File    = $file
                Line    = $compare.Extent.StartLineNumber
                Message = "$what is compared with `$null, but $helper returns [string]: a NULL column comes back as '' and the test never sees it$void"
            })
        }
    }
    return [pscustomobject]@{
        Files = $Path.Count; Variables = $variables; Comparisons = $comparisons; Exempted = $exempted
        Findings = $findings.ToArray()
        StaleExemptions = @($Exemptions | Where-Object { -not $used.ContainsKey($_) } | ForEach-Object { [pscustomobject]$_ })
    }
}

<#
Comparisons the string-null check knowingly lets through, each by file (relative to scripts/), enclosing function and
variable, with the reason. Each must still match a comparison, or the self-check reports it stale. Scenario scripts are not
changed by control-server#583; whether an entry here is rewritten instead is the coordinator's call.
#>
function Get-StringNullExemption {
    return @(
        @{ File = 'l2/scenarios/g3-forced-mechanical-recovery.ps1'; Function = 'Get-FleetGeneration'; Variable = 'value'
           Reason = 'VehicleRecoveryGenerations.ForcedRecoveryGeneration is a non-null long column, and [long]'''' is 0 anyway, ' +
                    'the same as the no-row branch: the comparison cannot change the result (found on the first run, control-server#583).'
           # The first half of the reason, re-read from the EF model snapshot on every run: if the column ever becomes
           # nullable, Test-StringNullExemptionPremise fails and the comparison is reported again.
           Premise = @{
               Snapshot = 'src/ControlServer.Infrastructure/Persistence/Migrations/ControlServerDbContextModelSnapshot.cs'
               Entity   = 'ControlServer.Infrastructure.Persistence.VehicleRecoveryGenerationRow'
               Property = 'ForcedRecoveryGeneration'
               ClrType  = 'long'
           } }
    )
}

<#
Whether an exemption's schema premise still holds: in the model snapshot, inside the modelBuilder.Entity("<Entity>") block,
the property is declared b.Property<ClrType>("<Property>") with exactly that type (long, not long?). Returns
{ Holds; Detail }. An exemption without a Premise holds trivially.
#>
function Test-StringNullExemptionPremise {
    param([Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][object]$Exemption)
    $premise = $Exemption['Premise']
    if (-not $premise) { return [pscustomobject]@{ Holds = $true; Detail = 'no premise' } }
    $file = Join-Path $RepositoryRoot $premise.Snapshot
    if (-not (Test-Path -LiteralPath $file)) { return [pscustomobject]@{ Holds = $false; Detail = "premise: $($premise.Snapshot) does not exist" } }
    $text = Get-Content -Raw -LiteralPath $file
    $start = $text.IndexOf("modelBuilder.Entity(`"$($premise.Entity)`"", [StringComparison]::Ordinal)
    if ($start -lt 0) { return [pscustomobject]@{ Holds = $false; Detail = "premise: no entity $($premise.Entity) in $($premise.Snapshot)" } }
    $next = $text.IndexOf('modelBuilder.Entity(', $start + 1, [StringComparison]::Ordinal)
    $block = $next -lt 0 ? $text.Substring($start) : $text.Substring($start, $next - $start)
    $declared = [regex]::Match($block, "b\.Property<([^>]+)>\(`"$([regex]::Escape($premise.Property))`"\)")
    if (-not $declared.Success) {
        return [pscustomobject]@{ Holds = $false; Detail = "premise: $($premise.Entity) has no property $($premise.Property)" }
    }
    $type = $declared.Groups[1].Value
    return [pscustomobject]@{
        Holds  = $type -eq $premise.ClrType
        Detail = "premise: $($premise.Entity).$($premise.Property) is $type in the snapshot, the exemption needs $($premise.ClrType)"
    }
}

# --- 4. literal SQL compiled against a migrated temporary database ---------------------------------------------------

# The functions every scenario's SQL reaches the server database through, with the 0-based position of their Sql
# parameter. Get-SqlSinkCatalog adds every function that hands its own $Sql on to one of these.
$script:SqlRoots = @{ 'Invoke-L2Query' = 1; 'Invoke-L2SnapshotStatement' = 1 }

function Get-ParameterName([System.Management.Automation.Language.FunctionDefinitionAst]$Function) {
    $parameters = if ($Function.Parameters) { $Function.Parameters } elseif ($Function.Body.ParamBlock) { $Function.Body.ParamBlock.Parameters } else { @() }
    return @($parameters | ForEach-Object { Get-VariableName $_.Name })
}

# The AST passed as a command's Sql parameter (or another, by -Name), by name or by position; $null when there is none.
function Get-SqlArgument([System.Management.Automation.Language.CommandAst]$Command, [int]$Position, [string]$Name = 'Sql') {
    $elements = @($Command.CommandElements)
    $positional = 0
    for ($i = 1; $i -lt $elements.Count; $i++) {
        $element = $elements[$i]
        if ($element -is [System.Management.Automation.Language.CommandParameterAst]) {
            $value = $element.Argument
            if ($null -eq $value -and $i + 1 -lt $elements.Count -and $elements[$i + 1] -isnot [System.Management.Automation.Language.CommandParameterAst]) {
                $i++
                $value = $elements[$i]
            }
            if ($element.ParameterName -eq $Name) { return $value }
            continue
        }
        if ($positional -eq $Position) { return $element }
        $positional++
    }
    return $null
}

function Add-DerivedSink([System.Management.Automation.Language.Ast]$Ast, [hashtable]$Sinks) {
    $changed = $false
    foreach ($function in $Ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
        $names = Get-ParameterName $function
        $index = [array]::IndexOf([string[]]@($names | ForEach-Object { $_.ToLowerInvariant() }), 'sql')
        if ($index -lt 0 -or ($Sinks.ContainsKey($function.Name) -and $Sinks[$function.Name] -eq $index)) { continue }
        foreach ($call in $function.Body.FindAll({ param($a) $a -is [System.Management.Automation.Language.CommandAst] }, $true)) {
            $callee = $call.GetCommandName()
            if (-not $callee -or -not $Sinks.ContainsKey($callee) -or $callee -eq $function.Name) { continue }
            $argument = Get-SqlArgument $call $Sinks[$callee]
            if ($argument -is [System.Management.Automation.Language.VariableExpressionAst] -and (Get-VariableName $argument) -eq 'Sql') {
                $Sinks[$function.Name] = $index
                $changed = $true
                break
            }
        }
    }
    return $changed
}

<#
Invoke-L2Query and Invoke-L2SnapshotStatement, plus every function in the given files that passes its own parameter named
Sql on to one of them (Get-G3Scalar, Get-G3Count, the scenario-local Get-Count and Get-Scalar), to a fixed point. Name to
position of the Sql parameter.
#>
function Get-SqlSinkCatalog {
    param([Parameter(Mandatory)][string[]]$Path)
    $sinks = @{} + $script:SqlRoots
    $asts = @($Path | ForEach-Object { (Get-ParsedScript $_).Ast })
    do {
        $changed = $false
        foreach ($ast in $asts) { if (Add-DerivedSink $ast $sinks) { $changed = $true } }
    } while ($changed)
    return $sinks
}

# A string the parser can fix without running anything: a constant, a double-quoted string with nothing to expand, or a
# + of such. $null otherwise.
function Get-LiteralString([System.Management.Automation.Language.Ast]$Ast) {
    $Ast = Get-Unwrapped $Ast
    if ($Ast -is [System.Management.Automation.Language.StringConstantExpressionAst]) { return $Ast.Value }
    if ($Ast -is [System.Management.Automation.Language.ExpandableStringExpressionAst]) {
        return $Ast.NestedExpressions.Count -eq 0 ? $Ast.Value : $null
    }
    if ($Ast -is [System.Management.Automation.Language.BinaryExpressionAst] -and $Ast.Operator -eq 'Plus') {
        $left = Get-LiteralString $Ast.Left
        $right = Get-LiteralString $Ast.Right
        return ($null -ne $left -and $null -ne $right) ? $left + $right : $null
    }
    return $null
}

<#
Every literal SQL argument of a call to a sink, with its file and line. Sinks are the given catalog plus the file's own
wrappers. Anything else -- $ interpolation, a variable, a here-string built elsewhere -- is counted in NonLiteral and not
checked: most scenario SQL carries a demand id, so this is the larger part, and the count is the measure of the blind spot.
#>
function Get-ScenarioLiteralSql {
    param([Parameter(Mandatory)][string[]]$Path, [Parameter(Mandatory)][hashtable]$Sinks)
    $literal = [System.Collections.Generic.List[object]]::new()
    $nonLiteral = 0; $otherDatabase = 0
    foreach ($file in $Path) {
        $ast = (Get-ParsedScript $file).Ast
        $local = @{} + $Sinks
        while (Add-DerivedSink $ast $local) { }
        # Connections the script opens itself (SqliteConnection::new -- the onboard journal in the real-onboard scenarios):
        # the server's connection always arrives from the orchestrator ($Context.Connection) or Open-L2Database.
        $ownConnections = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($assignment in $ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $a.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and $a.Right.Extent.Text -match 'SqliteConnection\]::new\(' }, $true)) {
            $null = $ownConnections.Add((Get-VariableName $assignment.Left))
        }
        foreach ($call in $ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.CommandAst] }, $true)) {
            $name = $call.GetCommandName()
            if (-not $name) { continue }
            $name = ($name -split '\\')[-1]
            if (-not $local.ContainsKey($name)) { continue }
            $argument = Get-SqlArgument $call $local[$name]
            if ($null -eq $argument) { continue }
            # A wrapper's own forwarding of its $Sql parameter is not a statement; a script-level $sql is.
            if ($argument -is [System.Management.Automation.Language.VariableExpressionAst] -and (Get-VariableName $argument) -eq 'Sql') {
                $enclosing = $call.Parent
                while ($null -ne $enclosing -and $enclosing -isnot [System.Management.Automation.Language.FunctionDefinitionAst]) { $enclosing = $enclosing.Parent }
                if ($null -ne $enclosing -and @(Get-ParameterName $enclosing) -contains 'Sql') { continue }
            }
            # The connection comes first wherever a sink takes one (-Connection, or position 0 ahead of the SQL).
            $connection = if ($local[$name] -gt 0) { Get-SqlArgument $call 0 'Connection' } else { Get-SqlArgument $call 99 'Connection' }
            if ($connection -is [System.Management.Automation.Language.VariableExpressionAst] -and $ownConnections.Contains((Get-VariableName $connection))) {
                $otherDatabase++
                continue
            }
            $sql = Get-LiteralString $argument
            if ($null -eq $sql) { $nonLiteral++; continue }
            $literal.Add([pscustomobject]@{ File = $file; Line = $call.Extent.StartLineNumber; Sql = $sql })
        }
    }
    return [pscustomobject]@{ Literal = $literal.ToArray(); NonLiteral = $nonLiteral; OtherDatabase = $otherDatabase }
}

# Where a ControlServer database lives for real: %ProgramData% (the MVP production default), the two install roots.
function Assert-ClearOfProduction([string]$Full, [string]$What) {
    $forbidden = @($env:ProgramData, 'C:\ProgramData', 'C:\Program Files\8005 AGV', 'D:\zhengyushao') | Where-Object { $_ }
    foreach ($root in $forbidden) {
        $rootFull = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
        if (($Full.TrimEnd('\') + '\').StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "$What '$Full' is under '$root'." }
    }
    if ($Full -match '\\ControlServer\\data(\\|$)') { throw "$What '$Full' looks like a ControlServer data root." }
}

<#
Throws unless the work root is given and clear of %ProgramData% and any ControlServer install. Test-ScenarioSqlExplain.ps1
takes it from -WorkRoot or $RUNNER_TEMP and runs nothing without one.
#>
function Assert-WorkRoot {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$WorkRoot)
    if (-not $WorkRoot) { throw 'No work root given for the temporary database.' }
    Assert-ClearOfProduction ([IO.Path]::GetFullPath($WorkRoot)) 'Work root'
}

<#
Throws unless the database path is under the work root and clear of %ProgramData% and any ControlServer install or data
root. --migrate-only with no connection string migrates the MVP production database, so this runs before the host is ever
started.
#>
function Assert-TempDatabasePath {
    param([Parameter(Mandatory)][string]$DatabasePath, [Parameter(Mandatory)][AllowEmptyString()][string]$WorkRoot)
    Assert-WorkRoot $WorkRoot
    $full = [IO.Path]::GetFullPath($DatabasePath)
    $root = [IO.Path]::GetFullPath($WorkRoot).TrimEnd('\') + '\'
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Temporary database path '$full' is not under the work root '$root'."
    }
    Assert-ClearOfProduction $full 'Temporary database path'
}

<#
The verdict on the production database file from its stamps before and after the migration, as text: 'True' (unchanged),
'False' (changed, created or removed), or 'UNVERIFIED (access denied)' when either stamp could not be read. Never 'True'
for something that was not looked at.
#>
function Compare-ProductionStamp {
    param([Parameter(Mandatory)][string]$Before, [Parameter(Mandatory)][string]$After)
    if ($Before -eq 'inaccessible' -or $After -eq 'inaccessible') { return 'UNVERIFIED (access denied)' }
    return $Before -ceq $After ? 'True' : 'False'
}

# Length and last write of a file, 'absent', or 'inaccessible' when this account may not even look (the control host's
# %ProgramData%\8005 is like that). Compare-ProductionStamp turns 'inaccessible' into UNVERIFIED, not into a pass.
function Get-FileStamp([string]$File) {
    try {
        if (-not (Test-Path -LiteralPath $File -ErrorAction Stop)) { return 'absent' }
        $item = Get-Item -LiteralPath $File -ErrorAction Stop
        return "$($item.Length) $($item.LastWriteTimeUtc.Ticks)"
    } catch [System.UnauthorizedAccessException] {
        return 'inaccessible'
    } catch {
        if ($_.Exception -is [System.UnauthorizedAccessException] -or $_.CategoryInfo.Category -eq 'PermissionDenied') { return 'inaccessible' }
        throw
    }
}

function Import-HostSqlite([string]$HostDirectory) {
    foreach ($assembly in @('SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'SQLitePCLRaw.batteries_v2.dll', 'Microsoft.Data.Sqlite.dll')) {
        $file = Join-Path $HostDirectory $assembly
        if (Test-Path -LiteralPath $file) { Add-Type -LiteralPath $file -ErrorAction SilentlyContinue }
    }
    # As in Open-L2Database (scripts/l2/L2.psm1): Batteries_V2 reports a missing type on some builds and works regardless.
    try { [SQLitePCL.Batteries_V2]::Init() } catch { }
}

<#
Runs the built ControlServer.Host once with --migrate-only against DatabasePath, passed as ConnectionStrings__ControlServer
in the child's environment (an argument with a space in it was split once, control-server#503). Returns the number of
tables, whether the default production database is exactly as it was, and how long it took.
#>
function New-MigratedTempDatabase {
    param(
        [Parameter(Mandatory)][string]$HostDirectory, [Parameter(Mandatory)][string]$DatabasePath,
        [Parameter(Mandatory)][AllowEmptyString()][string]$WorkRoot, [int]$TimeoutSeconds = 180
    )
    # Before anything else, and before any process exists.
    Assert-TempDatabasePath -DatabasePath $DatabasePath -WorkRoot $WorkRoot
    $DatabasePath = [IO.Path]::GetFullPath($DatabasePath)
    $production = Join-Path ($env:ProgramData ?? 'C:\ProgramData') '8005\ControlServer\data\controlserver.db'
    $before = Get-FileStamp $production
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $DatabasePath) -Force

    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $HostDirectory 'ControlServer.Host.exe'))
    $start.ArgumentList.Add('--migrate-only')
    $start.WorkingDirectory = $HostDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['ConnectionStrings__ControlServer'] = "Data Source=$DatabasePath"
    $start.Environment['DOTNET_ENVIRONMENT'] = 'Production'
    # The connection string the host will read, checked as it will be handed over: given, and naming exactly the path
    # asserted above. Without it the host would fall back to the production default.
    $given = $start.Environment['ConnectionStrings__ControlServer']
    if ($given -notmatch '^Data Source=(.+)$' -or $Matches[1] -ne $DatabasePath) {
        throw "Refusing to start --migrate-only: ConnectionStrings__ControlServer is '$given', not the temporary database."
    }
    Assert-TempDatabasePath -DatabasePath $Matches[1] -WorkRoot $WorkRoot
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true)
        throw "ControlServer.Host --migrate-only did not exit within $TimeoutSeconds s."
    }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "ControlServer.Host --migrate-only exited $($process.ExitCode).`n$($stdout.Result)`n$($stderr.Result)"
    }
    if (-not (Test-Path -LiteralPath $DatabasePath)) { throw "ControlServer.Host --migrate-only exited 0 but did not create $DatabasePath." }

    Import-HostSqlite $HostDirectory
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$DatabasePath;Mode=ReadOnly;Pooling=False")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'"
        $tables = [int]$command.ExecuteScalar()
        $command.Dispose()
    } finally {
        $connection.Dispose()
    }
    return [pscustomobject]@{
        Tables = $tables; ProductionUntouched = (Compare-ProductionStamp -Before $before -After (Get-FileStamp $production)); Elapsed = $watch.Elapsed
        DataSource = $DatabasePath
    }
}

function Open-ExplainConnection {
    param([Parameter(Mandatory)][string]$HostDirectory, [Parameter(Mandatory)][string]$DatabasePath, [Parameter(Mandatory)][string]$WorkRoot)
    Assert-TempDatabasePath -DatabasePath $DatabasePath -WorkRoot $WorkRoot
    Import-HostSqlite $HostDirectory
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$DatabasePath;Mode=ReadOnly;Pooling=False")
    $connection.Open()
    return $connection
}

<#
Compiles each statement with EXPLAIN (which prepares the statement and runs nothing) and returns the ones SQLite refused:
an unknown table or column, a syntax error. Not seen: a double-quoted identifier SQLite falls back to reading as a string,
anything only the data could reveal, and type mismatches SQLite allows.
#>
function Test-SqlExplain {
    param([Parameter(Mandatory)][object]$Connection, [object[]]$Statement)
    foreach ($s in @($Statement)) {
        $command = $Connection.CreateCommand()
        try {
            $command.CommandText = "EXPLAIN $($s.Sql)"
            $reader = $command.ExecuteReader()
            try { while ($reader.Read()) { } } finally { $reader.Dispose() }
        } catch {
            $message = $_.Exception.InnerException?.Message ?? $_.Exception.Message
            [pscustomobject]@{ File = $s.File; Line = $s.Line; Sql = $s.Sql; Message = $message }
        } finally {
            $command.Dispose()
        }
    }
}

Export-ModuleMember -Function Compare-ProductionStamp, Test-StringNullExemptionPremise, Assert-WorkRoot, Get-SqlSinkCatalog, Get-ScenarioLiteralSql, Assert-TempDatabasePath, New-MigratedTempDatabase,
    Open-ExplainConnection, Test-SqlExplain, Get-StringNullExemption, Test-ScriptParse, Get-HelperShapeCatalog, Find-HelperPropertyMisuse, Get-StringHelperCatalog, Find-StringHelperNullComparison
