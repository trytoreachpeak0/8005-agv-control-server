#Requires -Version 7
<#
.SYNOPSIS
Two-sided check of the closure-capture guard: every broken shape flagged, every legal shape left alone.

.DESCRIPTION
One-sided is not enough here and the reason is on record. While this guard was being built there was
a version that flagged 0 of the repository's 23 working sites AND 0 of 3 deliberately broken
fixtures -- and the 0-of-23 on its own looked like success. The cause was a "file-level names" rule
written as "any name anywhere in the file", which made every read resolvable and the check vacuous.
So both sides run here, and a shape that disappears from either list is a finding about the guard.

The fixtures are the shapes measured in evidence/l2/cs266-closure-capture/. Each legal one exists
because it is nested, or qualified-looking, or otherwise resembles a broken shape closely enough that
a guard written from intuition would flag it -- those are the false positives worth paying for a test.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'L2ClosureCapture.psm1') -Force

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) "l2-closure-capture-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch -Force

$results = [System.Collections.Generic.List[object]]::new()
function Add-Case([string]$Name, [bool]$Ok, [string]$Actual) {
    $results.Add([pscustomobject]@{ Name = $Name; Ok = $Ok; Actual = $Actual })
}

function New-Fixture([string]$Name, [string]$Body) {
    $path = Join-Path $scratch $Name
    Set-Content -LiteralPath $path -Value $Body -Encoding utf8NoBOM
    return $path
}

# ---------------------------------------------------------------- broken shapes: must be flagged

$broken = @(
    @{ Name = 'nested.ps1'
       Case = 'capturing the enclosing function''s variable from a deeper scope (burned rig-01)'
       Want = 'DemandId'
       Body = @'
function Drive {
    param($DemandId)
    Invoke-Thing -WhileWaiting {
        $probe = { Get-Thing $DemandId }.GetNewClosure()
        Wait-For -Probe $probe
    }.GetNewClosure()
}
'@ }
    @{ Name = 'plain-block.ps1'
       Case = 'the same, from a plain scriptblock rather than a closure -- no nesting involved'
       Want = 'DemandId'
       Body = @'
function Drive {
    param($DemandId)
    $outer = {
        $probe = { $DemandId }.GetNewClosure()
        Wait-For -Probe $probe
    }
    & $outer
}
'@ }
    @{ Name = 'inner-function.ps1'
       Case = 'the same, from a function defined inside a function'
       Want = 'DemandId'
       Body = @'
function Drive {
    param($DemandId)
    function Inner {
        $probe = { $DemandId }.GetNewClosure()
        Wait-For -Probe $probe
    }
    Inner
}
'@ }
    @{ Name = 'qualified.ps1'
       Case = 'a $script:-qualified read, in a function that also assigns it'
       Want = 'script:thing'
       Body = @'
function Drive {
    $script:thing = 'x'
    $probe = { $script:thing }.GetNewClosure()
    Wait-For -Probe $probe
}
'@ }
    @{ Name = 'module-top.psm1'
       Case = 'a .psm1 top-level variable captured inside that module'
       Want = 'moduleTop'
       Body = @'
$moduleTop = 'MOD'
function Drive {
    $probe = { $moduleTop }.GetNewClosure()
    Wait-For -Probe $probe
}
'@ }
)

foreach ($f in $broken) {
    $path = New-Fixture $f.Name $f.Body
    $found = @(Get-L2ClosureCaptureFindings -Path $path)
    $names = (@($found | ForEach-Object { $_.Names }) -join '; ')
    Add-Case "报: $($f.Case)" (($found.Count -ge 1) -and ($names -like "*$($f.Want)*")) "$($found.Count) 条: $names"
}

# ---------------------------------------------------------------- legal shapes: must NOT be flagged

$legal = @(
    @{ Name = 'top-level.ps1'
       Case = '函数体顶层捕获本函数的参数'
       Body = @'
function Drive {
    param($DemandId)
    Wait-For -Probe { Get-Thing $DemandId }.GetNewClosure() -Until { param($v) $v -ge 1 }
}
'@ }
    @{ Name = 'own-local.ps1'
       Case = '嵌套，但捕获闭包自己体内定义的变量'
       Body = @'
function Drive {
    param($DemandId)
    $outer = {
        $local = 'ok'
        $probe = { $local }.GetNewClosure()
        Wait-For -Probe $probe
    }.GetNewClosure()
    & $outer
}
'@ }
    @{ Name = 'own-param.ps1'
       Case = '嵌套，但捕获闭包自己的参数'
       Body = @'
function Drive {
    $outer = {
        param($Given)
        $probe = { $Given }.GetNewClosure()
        Wait-For -Probe $probe
    }.GetNewClosure()
    & $outer 'p'
}
'@ }
    @{ Name = 'copy-first.ps1'
       Case = '嵌套，但先复制到本地再捕获（推荐解法）'
       Body = @'
function Drive {
    param($DemandId)
    $outer = {
        $copy = $DemandId
        $probe = { $copy }.GetNewClosure()
        Wait-For -Probe $probe
    }.GetNewClosure()
    & $outer
}
'@ }
    @{ Name = 'script-top.ps1'
       Case = '.ps1 文件顶层变量与脚本参数'
       Body = @'
param($Context)
$fromTop = 'T'
Wait-For -Probe { "$Context/$fromTop" }.GetNewClosure()
'@ }
    @{ Name = 'foreach-var.ps1'
       Case = 'foreach 变量与自动变量'
       Body = @'
function Drive {
    foreach ($item in 1..3) {
        Wait-For -Probe { $item }.GetNewClosure()
    }
    1..3 | ForEach-Object { Wait-For -Probe { $_ }.GetNewClosure() }
}
'@ }
    @{ Name = 'set-variable.ps1'
       Case = 'Set-Variable 建的名字（不确定项，按构造不报）'
       Body = @'
function Drive {
    Set-Variable -Name 'built' -Value 1
    Wait-For -Probe { $built }.GetNewClosure()
}
'@ }
)

foreach ($f in $legal) {
    $path = New-Fixture $f.Name $f.Body
    $found = @(Get-L2ClosureCaptureFindings -Path $path)
    $names = (@($found | ForEach-Object { "$($_.Names)" }) -join '; ')
    Add-Case "不报: $($f.Case)" ($found.Count -eq 0) "$($found.Count) 条$(if ($names) { ": $names" })"
}

# ---------------------------------------------------------------- the repository as it stands

$scriptRoot = Split-Path -Parent $PSScriptRoot
$repoFindings = [System.Collections.Generic.List[object]]::new()
$scanned = 0
foreach ($file in Get-ChildItem -Recurse -LiteralPath $scriptRoot -Include *.ps1, *.psm1 -File) {
    if ($file.FullName -like "$scratch*") { continue }
    $scanned++
    foreach ($x in @(Get-L2ClosureCaptureFindings -Path $file.FullName)) { $repoFindings.Add($x) }
}
Add-Case "scripts/ 下 $scanned 个文件没有发现" ($repoFindings.Count -eq 0) `
    "$($repoFindings.Count) 条$(if ($repoFindings.Count) { ': ' + ((@($repoFindings | ForEach-Object { "$(Split-Path -Leaf $_.File):$($_.Line) -> $($_.Names)" })) -join '; ') })"

# The unknowns list must stay non-empty and must keep its last line: a clean result is only readable
# next to what the check cannot see, and the entry that covers unmeasured shapes is the one a list
# like this loses first.
$unknowns = @(Get-L2ClosureCaptureUnknowns)
Add-Case '不确定项清单仍在，且仍覆盖「未测量过的形状」' `
    (($unknowns.Count -ge 5) -and (@($unknowns | Where-Object { $_ -like '*cs266-closure-capture*' }).Count -ge 1)) `
    "$($unknowns.Count) 条"

Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue

$bad = 0
foreach ($r in $results) {
    if (-not $r.Ok) { $bad++ }
    Write-Host ("{0}  {1} -> {2}" -f $(if ($r.Ok) { 'ok  ' } else { 'BAD ' }), $r.Name, $r.Actual)
}
if ($bad -gt 0) {
    Write-Host "L2ClosureCapture self-check: $bad of $($results.Count) cases came out the wrong way."
    exit 1
}
Write-Host "L2ClosureCapture self-check: all $($results.Count) cases as expected."
