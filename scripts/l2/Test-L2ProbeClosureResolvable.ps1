#Requires -Version 7
<#
.SYNOPSIS
No scriptblock that has been through .GetNewClosure() may call an unexported function of its own module.

.DESCRIPTION
A closure resolves command names against the global table, not against the module it was written in.
So a `-Probe { Get-SomethingPrivate ... }.GetNewClosure()` inside a .psm1 cannot see a private sibling,
and it does not say so: the call throws, Wait-L2Condition's poll swallows every probe exception
(`L2.psm1:24`, deliberately -- a poll has to tolerate a transient read), and the whole thing surfaces
a timeout later as "timed out waiting for <whatever the probe was for>". On the rig that reads as a
product fault, and finding out costs a real-rig round. control-server#203 wrote exactly that bug and
caught it here instead.

Two halves, and the first is the reason the second is worth anything:

1. The rule is re-measured, not assumed. Two generated modules, identical but for one name on the
   Export-ModuleMember line, are actually imported and driven through Wait-L2Condition. The private
   one must fail and the exported one must work. If PowerShell ever changes this, that is a finding
   about the rule rather than a silent green.
2. Every scripts/l2 module is scanned for the pattern, and a deliberately bad fixture is scanned
   alongside them -- because a scanner that reports nothing and a scanner that has stopped working
   look the same from outside.

Scripts are not scanned, and that is measured too: a function defined in a .ps1 stays resolvable from
a closure, even when the closure is run by another module's function. The trap needs a module scope.

WHAT THIS DOES NOT COVER: it knows exactly one shape, a closure calling an unexported function of its
own module -- a COMMAND-NAME resolution failure. The other way a GetNewClosure block comes back
useless is a VARIABLE capture failure, where the block runs but every captured variable reads as
$null, and that is Test-L2ClosureCapture.ps1's job (control-server#266). Neither subsumes the other,
and from outside both look the same: a criterion that can never be satisfied, timing out sixty
seconds later as though the server never acted.

This paragraph used to describe that second shape as ".GetNewClosure() called inside a scriptblock
that is already a closure". That was wrong, and it is recorded here rather than deleted because the
word "nested" is what a reader arrives with. The grid in evidence/l2/cs266-closure-capture/ says
nesting is neither sufficient nor necessary: nested blocks that read their own scope variables are
fine, and unnested ones -- a plain scriptblock, a function inside a function -- are not. The one axis
that fits every cell is WHERE the closure is taken: at the file top level a read is carried, anywhere
deeper it is not. A guard written from the word "nested" would have flagged eleven working call sites
in this repository; that number is measured too, by deleting one rule from the real guard and
rerunning it.

Two sentences that used to be here were themselves wrong and are worth knowing as wrong, because both
were written from a single measurement: "$script: reads are unconditionally empty" (they are carried
from the file top level) and "a .psm1 top-level variable is not capturable" (it is, at the module own
top level). Same mistake both times -- one position measured, the conclusion written as though
position did not matter.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$l2Root = $PSScriptRoot
$results = [System.Collections.Generic.List[object]]::new()

function Add-Case([string]$Name, [bool]$Ok, [string]$Actual) {
    $results.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Actual = $Actual })
}

# ---------------------------------------------------------------- part 1: the rule still holds

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "l2-probe-closure-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch -Force

function New-ProbeModule([string]$Path, [bool]$ExportHelper) {
    $exports = if ($ExportHelper) { 'Invoke-ProbeDriver, Get-ProbeHelper' } else { 'Invoke-ProbeDriver' }
    @"
function Get-ProbeHelper { return 'helper-reached' }
function Invoke-ProbeDriver {
    param([int]`$TimeoutSeconds = 2)
    return Wait-L2Condition -Description 'the helper answered' ``
        -TimeoutSeconds `$TimeoutSeconds -PollMilliseconds 50 ``
        -Probe { Get-ProbeHelper }.GetNewClosure() -Until { param(`$v) `$null -ne `$v }
}
Export-ModuleMember -Function $exports
"@ | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

Import-Module (Join-Path $l2Root 'L2.psm1') -Force

foreach ($exported in $false, $true) {
    $name = if ($exported) { 'ProbeExported' } else { 'ProbePrivate' }
    $path = Join-Path $scratch "$name.psm1"
    New-ProbeModule -Path $path -ExportHelper $exported
    Import-Module $path -Force
    $outcome = try { & "Invoke-ProbeDriver" } catch { "threw: $($_.Exception.Message.Split([char]10)[0])" }
    Remove-Module $name -Force -ErrorAction SilentlyContinue

    if ($exported) {
        Add-Case 'an exported helper IS reachable from a probe closure' ($outcome -ceq 'helper-reached') $outcome
    } else {
        # The point is not merely that it fails, but that it fails AS A TIMEOUT -- swallowed, with the
        # helper's name nowhere in the message. That is what makes the static scan below necessary
        # rather than optional: this failure does not identify itself.
        $isSwallowedTimeout = $outcome -like 'threw: Timed out after*' -and $outcome -notlike '*Get-ProbeHelper*'
        Add-Case 'an unexported helper is NOT reachable, and fails as a nameless timeout' $isSwallowedTimeout $outcome
    }
}

# ---------------------------------------------------------------- part 2: the scan

function Get-ExportedFunctionName([System.Management.Automation.Language.Ast]$Ast) {
    $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $found = $false
    $Ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object {
        if ($_.GetCommandName() -ne 'Export-ModuleMember') { return }
        $found = $true
        $_.CommandElements | Select-Object -Skip 1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.Language.CommandParameterAst]) { return }
            $_.FindAll({ param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] }, $true) |
                ForEach-Object { $null = $names.Add($_.Value) }
        }
    }
    # No Export-ModuleMember at all means everything is exported, so nothing can be out of reach.
    return [pscustomobject]@{ Declared = $found; Names = $names }
}

function Find-UnreachableCall([string]$Path) {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if ($errors) { throw "$([System.IO.Path]::GetFileName($Path)) does not parse: $($errors[0].Message)" }

    $exports = Get-ExportedFunctionName $ast
    if (-not $exports.Declared) { return @() }

    $defined = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) |
        ForEach-Object { $null = $defined.Add($_.Name) }

    $private = @($defined | Where-Object { -not $exports.Names.Contains($_) })
    if ($private.Count -eq 0) { return @() }

    $hits = [System.Collections.Generic.List[object]]::new()
    $ast.FindAll({ param($n)
        $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and
        $n.Member -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
        $n.Member.Value -eq 'GetNewClosure' -and
        $n.Expression -is [System.Management.Automation.Language.ScriptBlockExpressionAst]
    }, $true) | ForEach-Object {
        $sb = $_.Expression.ScriptBlock
        $sb.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true) | ForEach-Object {
            $called = $_.GetCommandName()
            if ($called -and ($private -contains $called)) {
                $hits.Add([pscustomobject]@{
                    File = [System.IO.Path]::GetFileName($Path)
                    Line = $_.Extent.StartLineNumber
                    Calls = $called })
            }
        }
    }
    # A plain list, NOT `, @($hits)`: a wrapped array survives one pipeline hop as a single object, so
    # a caller doing `$modules | ForEach-Object { Find-UnreachableCall ... }` collects one empty array
    # per clean module and counts them as findings. Callers iterate this with foreach instead.
    return $hits
}

# The fixture first: if the scanner has stopped working, this is where it says so.
$fixture = Join-Path $scratch 'BadFixture.psm1'
@'
function Get-PrivateThing { 'x' }
function Invoke-Bad {
    $probe = { Get-PrivateThing }.GetNewClosure()
    & $probe
}
Export-ModuleMember -Function Invoke-Bad
'@ | Set-Content -LiteralPath $fixture -Encoding utf8NoBOM
$fixtureHits = Find-UnreachableCall $fixture
Add-Case 'the scanner reports a module that does have the pattern' `
    ($fixtureHits.Count -eq 1 -and $fixtureHits[0].Calls -ceq 'Get-PrivateThing') `
    "$($fixtureHits.Count) hit(s): $(@($fixtureHits | ForEach-Object { "$($_.File):$($_.Line) -> $($_.Calls)" }) -join '; ')"

$modules = @(Get-ChildItem -LiteralPath $l2Root -Filter *.psm1 | Sort-Object Name)
$violations = [System.Collections.Generic.List[object]]::new()
foreach ($m in $modules) {
    foreach ($hit in (Find-UnreachableCall $m.FullName)) { $violations.Add($hit) }
}
Add-Case 'no scripts/l2 module calls an unexported sibling from a probe closure' `
    ($violations.Count -eq 0) `
    "$($modules.Count) module(s) scanned, $($violations.Count) violation(s)$(if ($violations.Count) { ': ' + (@($violations | ForEach-Object { "$($_.File):$($_.Line) -> $($_.Calls)" }) -join '; ') })"

Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- report

$bad = 0
foreach ($r in $results) {
    if (-not $r.Ok) { $bad++ }
    Write-Host ("{0}  {1} -> {2}" -f $(if ($r.Ok) { 'ok  ' } else { 'BAD ' }), $r.Name, $r.Actual)
}
if ($bad -gt 0) {
    Write-Host "L2ProbeClosureResolvable self-check: $bad of $($results.Count) cases came out the wrong way."
    exit 1
}
Write-Host "L2ProbeClosureResolvable self-check: all $($results.Count) cases as expected."
