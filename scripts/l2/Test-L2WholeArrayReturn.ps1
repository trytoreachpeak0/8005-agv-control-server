#Requires -Version 7
<#
.SYNOPSIS
    Guard and self-check: no script under scripts/ enumerates the output of a function that returns its array whole.

.DESCRIPTION
    Pure parsing plus a few in-process fixture runs, no rig, a few seconds.

    Invoke-L2Query ends in `return , $rows`. Written as `@(Invoke-L2Query ...)`, the caller holds a one-element array
    whose element is the whole result: `.Count` is 1 for zero rows and for forty, so a `.Count -eq 1` criterion can
    never go red, and an empty result throws on the first property read. control-server#428 found 35 such calls in
    14 files (39 by grep, four of which were comments warning against it) after the same mistake had been
    fixed six times one site at a time; control-server#390 lost a G3 round to one of them. L2WholeArrayReturn.psm1 is
    the scan; its header says what it derives, how it resolves names, and what it cannot see.

    Three parts, and the order matters:

      1. Fixtures, two-sided. Every shape the scan claims to judge is written out as a small script and judged twice:
         by the scan, and by RUNNING it against a whole-array reader that returns 0, 1 and 3 rows and recording how
         many rows the caller saw. A broken shape must be flagged AND must be measured wrong; a legal shape must be
         left alone AND must be measured right. (One broken shape, the reshaped stub, must be measured RIGHT: a
         mistake that measures right under the stub is exactly what it is reported for.) The shapes the scan
         cannot see are fixtures too: measured wrong and required NOT to be flagged, so the list of blind spots is
         a measurement that fails when it stops being true. The measurement is what keeps the rule honest -- a verdict with no
         measurement behind it is an opinion about PowerShell, and this whole ticket exists because such opinions
         were wrong seven times.
      2. Classifier cases: which function bodies count as returning whole, including the ones that only look like it.
      3. The scan of scripts/ itself. It also refuses to pass if it did not recognise Invoke-L2Query as a whole-array
         function: that is the premise the rule stands on, and a scan that lost it would report zero findings for ever.

    Exits 1 on any finding or any self-check failure, and prints every finding either way.

.EXAMPLE
    pwsh -NoProfile -File ./scripts/l2/Test-L2WholeArrayReturn.ps1

.EXAMPLE
    pwsh -NoProfile -File ./scripts/l2/Test-L2WholeArrayReturn.ps1 -ListHelpers
    Also prints every function classified as returning its array whole.
#>
[CmdletBinding()]
param(
    # Defaults to this repository's scripts/; a directory is accepted so the scan can be pointed at a copy.
    [string]$ScriptRoot = (Split-Path -Parent $PSScriptRoot),

    [switch]$ListHelpers,

    [switch]$SkipSelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2WholeArrayReturn.psm1') -Force

$failures = [System.Collections.Generic.List[string]]::new()

function Format-Relative([string]$Path) {
    return [System.IO.Path]::GetRelativePath($ScriptRoot, $Path).Replace('\', '/')
}

if (-not $SkipSelfTest) {
    $scratch = Join-Path ([System.IO.Path]::GetTempPath()) "l2-whole-array-$([guid]::NewGuid().ToString('N'))"
    $null = New-Item -ItemType Directory -Path $scratch -Force

    # The reader every fixture calls, in a module of its own -- so the fixtures resolve it across files, which is how
    # every scenario resolves Invoke-L2Query.
    $readerModule = Join-Path $scratch 'Reader.psm1'
    Set-Content -LiteralPath $readerModule -Encoding utf8NoBOM -Value @'
function Read-Shared {
    param([int]$N, [string]$Tag)
    $rows = @()
    for ($i = 0; $i -lt $N; $i++) { $rows += [pscustomobject]@{ Id = $i } }
    return , $rows
}
Export-ModuleMember -Function Read-Shared
'@

    # Each body must leave the number of rows its caller saw in $seen. $N is the number of rows the reader returns.
    $fixtures = @(
        # ------------------------------------------------------------ broken: must be flagged, must measure wrong
        @{ Name = 'wrapped.ps1'; Broken = 'wrapped'
           Case = '@( ) straight around the helper -- the 35 sites of control-server#428, verbatim'
           Body = '$rows = @(Read-Shared -N $N); $seen = $rows.Count' }
        @{ Name = 'wrapped-multiline-lowercase.ps1'; Broken = 'wrapped'
           Case = 'the same across lines, with backtick continuations, extra spaces and all lower case'
           Body = @'
$rows = @(
        read-shared   `
            -N $N `
            -Tag 'x'
)
$seen = $rows.Count
'@ }
        @{ Name = 'wrapped-call-operator.ps1'; Broken = 'wrapped'
           Case = 'through the call operator and a quoted name: @(& ''Read-Shared'' ...)'
           Body = '$rows = @(& ''Read-Shared'' -N $N); $seen = $rows.Count' }
        @{ Name = 'wrapped-module-qualified-scope.ps1'; Broken = 'wrapped'
           Case = 'a whole-array function this file defines with a scope prefix, then wraps'
           Body = @'
function script:Read-Local { $r = Read-Shared -N $N; return ,$r }
$rows = @(script:Read-Local); $seen = $rows.Count
'@ }
        @{ Name = 'pass-through-return.ps1'; Broken = 'wrapped'
           Case = 'the wrapping survives one return: function Get-Mine { return Read-Shared }, then @(Get-Mine)'
           Body = @'
function Get-Mine { return Read-Shared -N $N }
$rows = @(Get-Mine); $seen = $rows.Count
'@ }
        @{ Name = 'pass-through-two-levels.ps1'; Broken = 'wrapped'
           Case = 'through two levels, neither with the return keyword, the second inside an if'
           Body = @'
function Get-Inner { Read-Shared -N $N }
function Get-Outer { if ($true) { Get-Inner } }
$rows = @(Get-Outer); $seen = $rows.Count
'@ }
        @{ Name = 'bare-comma.ps1'; Broken = 'wrapped'
           Case = 'a function handing back with a bare unary comma, no return keyword'
           Body = @'
function Get-Mine { $r = Read-Shared -N $N; , $r }
$rows = @(Get-Mine); $seen = $rows.Count
'@ }
        @{ Name = 'no-enumerate.ps1'; Broken = 'wrapped'
           Case = 'a function handing back with Write-Output -NoEnumerate'
           Body = @'
function Get-Mine { $r = Read-Shared -N $N; Write-Output -NoEnumerate $r }
$rows = @(Get-Mine); $seen = $rows.Count
'@ }
        @{ Name = 'wrapped-inside-return.ps1'; Broken = 'wrapped'
           Case = 'the helper wraps inside its own return , @(...) -- L2TaskTypeJourney.psm1, verbatim'
           Body = @'
function Get-Mine { return , @(Read-Shared -N $N) }
$rows = Get-Mine; $seen = $rows.Count
'@ }
        @{ Name = 'wrapped-loop.ps1'; Broken = 'wrapped'
           Case = 'a loop inside @( ) whose body hands back one whole result per round'
           Body = '$rows = @(foreach ($i in 1) { Read-Shared -N $N }); $seen = $rows.Count' }
        @{ Name = 'wrapped-module-qualified.ps1'; Broken = 'wrapped'
           Case = 'the helper called by its module-qualified name: @(Reader\Read-Shared ...)'
           Body = '$rows = @(Reader\Read-Shared -N $N); $seen = $rows.Count' }
        # The relay: a function that hands on what its script block parameter emits (`return & $Probe`), which is
        # what Wait-L2RealOrLast and Wait-L2ConditionOrLast do once they time out.
        @{ Name = 'wrapped-relay.ps1'; Broken = 'wrapped'
           Case = '@( ) around a relay whose -Probe block ends in the helper -- g3-reversed-direction-journey.ps1:165, which the first version of the scan walked past'
           Body = @'
function Invoke-Relay { param([string]$Description, [scriptblock]$Probe) try { throw 'timed out' } catch { return & $Probe } }
$rows = @(Invoke-Relay -Description 'x' -Probe { Read-Shared -N $N }); $seen = $rows.Count
'@ }
        @{ Name = 'wrapped-relay-prefix.ps1'; Broken = 'wrapped'
           Case = 'the same with the parameter abbreviated, in lower case, and the block ending in a local pass-through function'
           Body = @'
function Invoke-Relay { param([scriptblock]$Probe) return & $Probe }
function Get-Mine { return Read-Shared -N $N }
$rows = @(Invoke-Relay -pro { Get-Mine }); $seen = $rows.Count
'@ }
        @{ Name = 'piped.ps1'; Broken = 'piped'
           Case = 'piped straight on: $_ in Where-Object is the whole result'
           Body = '$rows = @(Read-Shared -N $N | Where-Object { $true }); $seen = $rows.Count' }
        @{ Name = 'foreach.ps1'; Broken = 'foreach'
           Case = 'straight after the in of a foreach: one round'
           Body = '$seen = 0; foreach ($row in Read-Shared -N $N) { $seen++ }' }

        # The one broken shape that measures RIGHT, which is the whole trouble with it: a stand-in with the unrolled
        # shape makes the wrapping mistake in the code under test come out correct, so the test passes on broken code.
        @{ Name = 'reshaped-stub.ps1'; Broken = 'reshaped'; MeasuresRight = $true
           Case = 'a stub replaces the module''s whole-array reader with an unrolling one: the wrapping mistake behind it measures right'
           Body = @'
function Read-Shared { param([int]$N) $r = @(); for ($i = 0; $i -lt $N; $i++) { $r += [pscustomobject]@{ Id = $i } }; return $r }
function Get-UnderTest { return , @(Read-Shared -N $N) }
$rows = Get-UnderTest; $seen = $rows.Count
'@ }

        # ------------------------------------------------------------ known misses: measured wrong, NOT flagged
        # What the scan cannot see, pinned so that the list in L2WholeArrayReturn.psm1 and in the README is a
        # measurement and not a guess. If the scan learns one of these, its fixture fails here: move it up to the
        # broken ones and take it off both lists.
        @{ Name = 'miss-name-in-variable.ps1'; KnownMiss = $true
           Case = 'KNOWN MISS: the helper is called through a variable, @(& $reader ...)'
           Body = '$reader = ''Read-Shared''; $rows = @(& $reader -N $N); $seen = $rows.Count' }
        @{ Name = 'miss-alias.ps1'; KnownMiss = $true
           Case = 'KNOWN MISS: the helper is called through an alias'
           Body = 'Set-Alias -Name rs -Value Read-Shared; $rows = @(rs -N $N); $seen = $rows.Count' }
        @{ Name = 'miss-script-block-variable.ps1'; KnownMiss = $true
           Case = 'KNOWN MISS: a script block held in a variable hands the helper''s output on, and is wrapped'
           Body = '$reader = { Read-Shared -N $N }; $rows = @(& $reader); $seen = $rows.Count' }
        @{ Name = 'miss-subexpression-loop.ps1'; KnownMiss = $true
           Case = 'KNOWN MISS: $( ) collecting a loop that calls the helper each round'
           Body = '$rows = $(foreach ($i in 1, 2) { Read-Shared -N $N }); $seen = $rows.Count' }
        @{ Name = 'miss-get-command.ps1'; KnownMiss = $true
           Case = 'KNOWN MISS: the helper reached through Get-Command, @(& (Get-Command Helper) ...)'
           Body = '$rows = @(& (Get-Command Read-Shared) -N $N); $seen = $rows.Count' }
        @{ Name = 'miss-inline-script-block.ps1'; KnownMiss = $true
           Case = 'KNOWN MISS: an inline script block, @(& { Helper })'
           Body = '$rows = @(& { Read-Shared -N $N }); $seen = $rows.Count' }
        @{ Name = 'miss-foreach-object-block.ps1'; KnownMiss = $true
           Case = 'KNOWN MISS: the helper inside a ForEach-Object block, @(1 | ForEach-Object { Helper })'
           Body = '$rows = @(1 | ForEach-Object { Read-Shared -N $N }); $seen = $rows.Count' }
        @{ Name = 'miss-invoke-command.ps1'; KnownMiss = $true
           Case = 'KNOWN MISS: @(Invoke-Command { Helper })'
           Body = '$rows = @(Invoke-Command { Read-Shared -N $N }); $seen = $rows.Count' }
        @{ Name = 'miss-relay-positional.ps1'; KnownMiss = $true
           Case = 'KNOWN MISS: a relay given its block positionally rather than by parameter name'
           Body = @'
function Invoke-Relay { param([scriptblock]$Probe) return & $Probe }
$rows = @(Invoke-Relay { Read-Shared -N $N }); $seen = $rows.Count
'@ }
        @{ Name = 'miss-switch.ps1'; KnownMiss = $true
           Case = 'KNOWN MISS: switch (Helper ...) { } runs its clause once, on the whole result'
           Body = '$seen = 0; switch (Read-Shared -N $N) { default { $seen++ } }' }
        @{ Name = 'miss-return-parenthesised-comma.ps1'; KnownMiss = $true
           Case = 'KNOWN MISS: a whole-array return written return (, $x) is not classified, so wrapping it is not reported'
           Body = @'
function Get-Mine { $r = Read-Shared -N $N; return (, $r) }
$rows = @(Get-Mine); $seen = $rows.Count
'@ }
        @{ Name = 'miss-return-array-of-comma.ps1'; KnownMiss = $true
           Case = 'KNOWN MISS: the same written return @(, $x)'
           Body = @'
function Get-Mine { $r = Read-Shared -N $N; return @(, $r) }
$rows = @(Get-Mine); $seen = $rows.Count
'@ }
        @{ Name = 'miss-return-ternary-comma.ps1'; KnownMiss = $true
           Case = 'KNOWN MISS: the same with the unary comma inside a ternary'
           Body = @'
function Get-Mine { $r = Read-Shared -N $N; return ($true ? (, $r) : $null) }
$rows = @(Get-Mine); $seen = $rows.Count
'@ }
        # Like reshaped-stub.ps1 above, it measures RIGHT, and that is the harm: the wrap behind the stub is still
        # reported as wrapped, but nothing says the stub is why the test over it would pass.
        @{ Name = 'miss-function-drive-stub.ps1'; KnownMiss = $true; MissShape = 'reshaped'; MeasuresRight = $true
           Case = 'KNOWN MISS: a stand-in written ${function:Helper} = { ... } that unrolls is not reported as reshaped'
           Body = @'
${function:Read-Shared} = { param([int]$N) $r = @(); for ($i = 0; $i -lt $N; $i++) { $r += [pscustomobject]@{ Id = $i } }; return $r }
function Get-UnderTest { return , @(Read-Shared -N $N) }
$rows = Get-UnderTest; $seen = $rows.Count
'@ }

        # ------------------------------------------------------------ legal: must be left alone, must measure right
        @{ Name = 'assigned.ps1'
           Case = 'assigned directly -- the right way'
           Body = '$rows = Read-Shared -N $N; $seen = $rows.Count' }
        @{ Name = 'parenthesised.ps1'
           Case = 'parenthesised, then .Count'
           Body = '$seen = (Read-Shared -N $N).Count' }
        @{ Name = 'parenthesised-then-piped.ps1'
           Case = 'parenthesised, then piped, inside @( ): legal, used by L2RealStation.psm1, one pair of parentheses away from piped'
           Body = '$rows = @((Read-Shared -N $N) | Where-Object { $true }); $seen = $rows.Count' }
        @{ Name = 'relay-assigned-then-wrapped.ps1'
           Case = 'a relay''s result assigned first and wrapped afterwards: the one form right on both of its paths (g3-multi-stop-plan.ps1)'
           Body = @'
function Invoke-Relay { param([scriptblock]$Probe) return & $Probe }
$rows = Invoke-Relay -Probe { Read-Shared -N $N }
$rows = @($rows); $seen = $rows.Count
'@ }
        @{ Name = 'relay-of-unrolled-block.ps1'
           Case = 'a relay whose block hands back unrolled rows: wrapping it is correct'
           Body = @'
function Invoke-Relay { param([scriptblock]$Probe) return & $Probe }
$rows = @(Invoke-Relay -Probe { $r = Read-Shared -N $N; $r }); $seen = $rows.Count
'@ }
        @{ Name = 'assigned-then-wrapped.ps1'
           Case = 'assigned, then the variable wrapped in @( )'
           Body = '$r = Read-Shared -N $N; $rows = @($r); $seen = $rows.Count' }
        @{ Name = 'assigned-then-foreach.ps1'
           Case = 'assigned, then foreach over the variable'
           Body = '$r = Read-Shared -N $N; $seen = 0; foreach ($row in $r) { $seen++ }' }
        @{ Name = 'stub-same-shape.ps1'
           Case = 'a stub that replaces the module''s reader and keeps its shape (return , $r): legal, and direct assignment measures right'
           Body = @'
function Read-Shared { param([int]$N) $r = @(); for ($i = 0; $i -lt $N; $i++) { $r += [pscustomobject]@{ Id = $i } }; return , $r }
function Get-UnderTest { return Read-Shared -N $N }
$rows = Get-UnderTest; $seen = $rows.Count
'@ }
        @{ Name = 'unrolling-function-wrapped.ps1'
           Case = 'a function that hands back unrolled (return $r): wrapping it in @( ) is correct'
           Body = @'
function Get-Mine { $r = Read-Shared -N $N; return $r }
$rows = @(Get-Mine); $seen = $rows.Count
'@ }
        @{ Name = 'comma-inside-assignment.ps1'
           Case = 'a unary comma inside an if on the right of an assignment: not the function''s output'
           Body = @'
function Get-Mine { $r = if ($true) { , (Read-Shared -N $N) }; return $r }
$rows = @(Get-Mine); $seen = $rows.Count
'@ }
        @{ Name = 'script-block.ps1'
           Case = 'the helper called inside a script block whose result is assigned'
           Body = '$block = { Read-Shared -N $N }; $rows = & $block; $seen = $rows.Count' }
        @{ Name = 'mentioned-not-called.ps1'
           Case = '@(Read-Shared ...) in a comment and in a string: the four lines a text search counted'
           Body = @'
# Do not write @(Read-Shared -N $N): wrapped again it is one element, and that element is the whole result.
$text = '@(Read-Shared -N 1)'
$rows = Read-Shared -N $N; $seen = $rows.Count
'@ }
    )

    foreach ($fixture in $fixtures) {
        $fixture.Path = Join-Path $scratch $fixture.Name
        Set-Content -LiteralPath $fixture.Path -Encoding utf8NoBOM -Value (@(
                'param([int]$N)'
                'Set-StrictMode -Version Latest'
                "Import-Module '$readerModule' -Force"
                $fixture.Body
                '$seen'
            ) -join "`n")
    }

    $fixtureModel = Get-L2WholeArrayModel -Path (@($readerModule) + @($fixtures | ForEach-Object { $_.Path }))
    $fixtureFindings = Get-L2WholeArrayMisuse -Model $fixtureModel

    Write-Host 'Self-test: fixtures (verdict of the scan, and rows the caller saw for a reader returning 0 / 1 / 3 rows)'
    foreach ($fixture in $fixtures) {
        $found = @($fixtureFindings | Where-Object { $_.File -eq $fixture.Path })
        $seen = foreach ($n in 0, 1, 3) {
            try { [string](& $fixture.Path -N $n) } catch { 'throws' }
        }
        $measuredRight = ($seen -join ',') -eq '0,1,3'
        $wantShape = $fixture.ContainsKey('Broken') ? $fixture.Broken : $null
        $verdict = $found.Count -eq 0 ? 'clean' : (($found | ForEach-Object Shape | Sort-Object -Unique) -join '+')

        $wantRight = $fixture.ContainsKey('MeasuresRight') -and $fixture.MeasuresRight
        $knownMiss = $fixture.ContainsKey('KnownMiss') -and $fixture.KnownMiss
        $ok = if ($knownMiss) {
            # A miss of one shape only (MissShape) may still be reported as another; any other miss is reported as nothing.
            $missed = $fixture.ContainsKey('MissShape') ? @($found | Where-Object Shape -eq $fixture.MissShape) : $found
            $missed.Count -eq 0 -and $measuredRight -eq $wantRight
        } elseif ($wantShape) {
            $found.Count -ge 1 -and @($found | Where-Object Shape -ne $wantShape).Count -eq 0 -and $measuredRight -eq $wantRight
        } else {
            $found.Count -eq 0 -and $measuredRight
        }
        Write-Host ("  {0} {1,-38} scan={2,-8} saw={3,-16} {4}" -f ($ok ? 'ok  ' : 'FAIL'), $fixture.Name, $verdict, ($seen -join ','), $fixture.Case)
        if (-not $ok) {
            $failures.Add((
                    "fixture $($fixture.Name): expected " +
                    ($knownMiss ? "a known miss -- not reported, and the caller to see $($wantRight ? '0,1,3' : 'something other than 0,1,3')" :
                        $wantShape ? "the scan to report '$wantShape' only and the caller to see $($wantRight ? '0,1,3' : 'something other than 0,1,3')" :
                        'no finding and the caller to see 0,1,3') +
                    "; got scan=$verdict saw=$($seen -join ',')"))
        }
    }

    # Which bodies count as returning whole. The three "not whole" rows each resemble a whole-array return closely
    # enough that a rule written from intuition would take them for one.
    $classifierSource = Join-Path $scratch 'classifier.ps1'
    Set-Content -LiteralPath $classifierSource -Encoding utf8NoBOM -Value @'
function Whole-Return { return , $rows }
function Whole-ReturnTight { return ,$rows }
function Whole-ReturnWrapped { return , @($rows | Sort-Object Id) }
function Whole-Bare { , $rows }
function Whole-EarlyExit { if (-not $rows) { return , @() }; return $rows }
function Whole-NoEnumerate { $rows | Sort-Object Id | Write-Output -NoEnumerate }
function Whole-InLoop { foreach ($x in 1, 2) { , $rows } }
function Whole-PassThrough { return Whole-Return }
function Whole-PassThroughTwice { Whole-PassThrough }
function Unrolled-Return { return $rows }
function Unrolled-Pair { return $a, $b }
function Unrolled-Assigned { $x = , $rows; return $x }
function Unrolled-PipedOn { Whole-Return | Sort-Object Id }
function Unrolled-InScriptBlock { $rows | ForEach-Object { , $_ } }
class Holder { [object[]] Whole() { return , $this.rows } }
'@
    $classifierModel = Get-L2WholeArrayModel -Path @($classifierSource)
    $whole = @((Get-L2WholeArrayFunctions -Model $classifierModel) | ForEach-Object Name | Sort-Object)
    $wantWhole = @($classifierModel.Files[0].Functions | ForEach-Object Name | Where-Object { $_ -like 'Whole-*' } | Sort-Object)
    Write-Host ''
    Write-Host "Self-test: classifier ($($classifierModel.Files[0].Functions.Count) functions, $($wantWhole.Count) of them whole-array)"
    if ($wantWhole.Count -ne 9) { $failures.Add("classifier: expected to parse 9 Whole-* functions, parsed $($wantWhole.Count)") }
    if (($whole -join ' ') -cne ($wantWhole -join ' ')) {
        $failures.Add("classifier: whole-array functions were [$($whole -join ' ')], expected [$($wantWhole -join ' ')]")
        Write-Host "  FAIL classified [$($whole -join ' ')]"
    } else {
        Write-Host "  ok   classified exactly the Whole-* functions; the Unrolled-* ones and the class method were left out"
    }
    # `Unrolled-PipedOn` pipes a whole-array function, which is itself a finding: the classifier fixture doubles as
    # the check that a finding inside a function body is reported.
    $classifierFindings = Get-L2WholeArrayMisuse -Model $classifierModel
    if ($classifierFindings.Count -ne 1 -or $classifierFindings[0].Shape -ne 'piped') {
        $failures.Add("classifier: expected exactly one 'piped' finding (Unrolled-PipedOn), got $($classifierFindings.Count)")
    }

    Remove-Item -LiteralPath $scratch -Recurse -Force
    Write-Host ''
}

# ------------------------------------------------------------------------------------------ the scan itself

$files = @(Get-ChildItem -Recurse -LiteralPath $ScriptRoot -Include *.ps1, *.psm1 -File | Sort-Object FullName | ForEach-Object FullName)
if ($files.Count -eq 0) { throw "No scripts under $ScriptRoot" }
$model = Get-L2WholeArrayModel -Path $files
$helpers = Get-L2WholeArrayFunctions -Model $model
$findings = Get-L2WholeArrayMisuse -Model $model

Write-Host "Scan: $($files.Count) scripts under $ScriptRoot, $($helpers.Count) functions return their array whole ($(@($helpers | Where-Object Shared).Count) of them in shared files)."
if ($ListHelpers) {
    foreach ($helper in ($helpers | Sort-Object { -not $_.Shared }, File, Line)) {
        Write-Host ("  {0,-6} {1,-40} {2}:{3}  ({4})" -f ($helper.Shared ? 'shared' : 'local'), $helper.Name, (Format-Relative $helper.File), $helper.Line, $helper.Via)
    }
}

# The premise. Only checked where L2.psm1 is part of the scan, so the scan can still be pointed at a partial copy.
$l2Module = $files | Where-Object { (Split-Path -Leaf $_) -eq 'L2.psm1' } | Select-Object -First 1
if ($l2Module -and @($helpers | Where-Object { $_.Name -eq 'Invoke-L2Query' -and $_.File -eq $l2Module }).Count -ne 1) {
    $failures.Add(
        'premise: Invoke-L2Query in L2.psm1 was not recognised as returning its array whole. Either its return shape ' +
        'changed (then every caller that assigns it directly now gets $null for an empty result -- see ' +
        'control-server#428 before going further) or this scan stopped working.')
}

foreach ($finding in $findings) {
    $title = $finding.Shape -eq 'reshaped' ? 'WHOLE ARRAY FUNCTION RESHAPED' : "WHOLE ARRAY ENUMERATED ($($finding.Shape))"
    Write-Host ("{0}: {1}:{2}  {3}" -f $title, (Format-Relative $finding.File), $finding.Line, $finding.Text)
}
if ($findings.Count -gt 0) {
    Write-Host ''
    Write-Host "$($findings.Count) finding(s). A function that returns its array whole must be assigned first: `$rows = Helper ...; then use `$rows."
    Write-Host 'wrapped: .Count is 1 whatever the result, and an empty result throws on the first property read.'
    Write-Host 'reshaped: a stand-in must end in `return , $rows` like the function it replaces. See scripts/l2/README.md.'
}

foreach ($failure in $failures) { Write-Host "SELF-CHECK FAILED: $failure" }

if ($findings.Count -gt 0 -or $failures.Count -gt 0) { exit 1 }
Write-Host 'No call enumerates a whole-array function, and no stand-in changes the shape of one.'
