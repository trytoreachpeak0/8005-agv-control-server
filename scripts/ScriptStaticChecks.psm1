#Requires -Version 7
<#
Offline static checks over the repository's PowerShell (control-server#583). Scripts/Test-ScriptStaticChecks.ps1 runs
them against its fixtures and against scripts/; this module only scans and reports.

These are regression guards for the commonest shapes, not proofs. Each check's header lists what it cannot see.
#>

Set-StrictMode -Version Latest

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
        $errors = $null
        $null = [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$null, [ref]$errors)
        foreach ($e in @($errors)) {
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
            $parsed[$file] = [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$null, [ref]$null)
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
        foreach ($next in ($elements | Select-Object -Skip 1)) {
            if ($next -isnot [System.Management.Automation.Language.CommandAst]) { return $null }
            $name = $next.GetCommandName()
            if ($name -in 'Where-Object', 'where', '?', 'Sort-Object', 'sort') { continue }
            if ($name -in 'Select-Object', 'select') {
                $parameters = @($next.CommandElements | Where-Object { $_ -is [System.Management.Automation.Language.CommandParameterAst] } | ForEach-Object ParameterName)
                if (@($parameters | Where-Object { $_ -notin 'First', 'Last', 'Skip', 'Unique' }).Count -eq 0) { continue }
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
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$null, [ref]$null)
        $normalised = $file.Replace('\', '/')
        # A file's own definition of a listed name wins, except in the file the list points at.
        $local = @{}
        foreach ($definition in $ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
            if ($shapes.ContainsKey($definition.Name) -and
                -not ($homes.ContainsKey($definition.Name) -and $normalised.EndsWith("/$($homes[$definition.Name])", [StringComparison]::OrdinalIgnoreCase))) {
                $local[$definition.Name] = 0
            }
        }

        # scope -> variable -> what each assignment resolved to ($null for unrecognised)
        $assigned = @{}
        $add = {
            param([string]$Scope, [string]$Variable, $Helper)
            if (-not $assigned.ContainsKey($Scope)) { $assigned[$Scope] = @{} }
            if (-not $assigned[$Scope].ContainsKey($Variable)) { $assigned[$Scope][$Variable] = [System.Collections.Generic.List[object]]::new() }
            # '' for unrecognised, not $null: a $null in the list would be dropped by the pipeline below.
            $assigned[$Scope][$Variable].Add([string]$Helper)
        }
        foreach ($node in $ast.FindAll({ param($a)
            $a -is [System.Management.Automation.Language.AssignmentStatementAst] -or $a -is [System.Management.Automation.Language.ForEachStatementAst] -or
                $a -is [System.Management.Automation.Language.ParameterAst] }, $true)) {
            $scope = Get-ScopeKey $node
            if ($node -is [System.Management.Automation.Language.ParameterAst]) {
                & $add $scope (Get-VariableName $node.Name) $null
            } elseif ($node -is [System.Management.Automation.Language.ForEachStatementAst]) {
                & $add $scope (Get-VariableName $node.Variable) (Resolve-HelperShape $node.Condition $shapes $relays $local)
            } elseif ($node.Left -is [System.Management.Automation.Language.VariableExpressionAst]) {
                $helper = if ($node.Operator -eq 'Equals') { Resolve-HelperShape $node.Right $shapes $relays $local } else { $null }
                & $add $scope (Get-VariableName $node.Left) $helper
            } else {
                foreach ($target in $node.Left.FindAll({ param($a) $a -is [System.Management.Automation.Language.VariableExpressionAst] }, $true)) {
                    & $add $scope (Get-VariableName $target) $null
                }
            }
        }
        $known = @{}
        foreach ($scope in $assigned.PSBase.Keys) {
            $known[$scope] = @{}
            foreach ($variable in $assigned[$scope].PSBase.Keys) {
                $helpers = @($assigned[$scope][$variable] | Select-Object -Unique)
                if ($helpers.Count -eq 1 -and $helpers[0] -ne '') {
                    $known[$scope][$variable] = $helpers[0]
                    $variables++
                } elseif (@($helpers | Where-Object { $_ -ne '' }).Count -gt 0) {
                    $unresolved++
                }
            }
        }

        foreach ($read in $ast.FindAll({ param($a)
            $a -is [System.Management.Automation.Language.MemberExpressionAst] -and $a -isnot [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
                -not $a.Static -and $a.Expression -is [System.Management.Automation.Language.VariableExpressionAst] -and
                $a.Member -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true)) {
            $scope = Get-ScopeKey $read
            $variable = (Get-VariableName $read.Expression)
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
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$null, [ref]$null)
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
    param([Parameter(Mandatory)][string[]]$Path, [string[]]$Helpers, [object[]]$Exemptions = @())
    $findings = [System.Collections.Generic.List[object]]::new()
    $comparisons = 0; $variables = 0; $exempted = 0
    $used = @{}
    foreach ($file in $Path) {
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($file, [ref]$null, [ref]$null)
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
            $sources = @($assigned[$key] | Select-Object -Unique)
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
            if ($exemption.Count -gt 0) { $used[$exemption[0]] = $true; $exempted++; continue }
            $findings.Add([pscustomobject]@{
                File    = $file
                Line    = $compare.Extent.StartLineNumber
                Message = "$what is compared with `$null, but $helper returns [string]: a NULL column comes back as '' and the test never sees it"
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
                    'the same as the no-row branch: the comparison cannot change the result (found on the first run, control-server#583).' }
    )
}

# --- 4. literal SQL compiled against a migrated temporary database ---------------------------------------------------

function Get-SqlSinkCatalog { param([Parameter(Mandatory)][string[]]$Path) throw 'Get-SqlSinkCatalog: not implemented' }
function Get-ScenarioLiteralSql { param([Parameter(Mandatory)][string[]]$Path, [Parameter(Mandatory)][hashtable]$Sinks) throw 'Get-ScenarioLiteralSql: not implemented' }
function Assert-TempDatabasePath { param([Parameter(Mandatory)][string]$DatabasePath) throw 'Assert-TempDatabasePath: not implemented' }
function New-MigratedTempDatabase { param([Parameter(Mandatory)][string]$HostDirectory, [Parameter(Mandatory)][string]$DatabasePath) throw 'New-MigratedTempDatabase: not implemented' }
function Open-ExplainConnection { param([Parameter(Mandatory)][string]$HostDirectory, [Parameter(Mandatory)][string]$DatabasePath) throw 'Open-ExplainConnection: not implemented' }
function Test-SqlExplain { param([Parameter(Mandatory)][object]$Connection, [object[]]$Statement) throw 'Test-SqlExplain: not implemented' }

Export-ModuleMember -Function Get-SqlSinkCatalog, Get-ScenarioLiteralSql, Assert-TempDatabasePath, New-MigratedTempDatabase,
    Open-ExplainConnection, Test-SqlExplain, Get-StringNullExemption, Test-ScriptParse, Get-HelperShapeCatalog, Find-HelperPropertyMisuse, Get-StringHelperCatalog, Find-StringHelperNullComparison
