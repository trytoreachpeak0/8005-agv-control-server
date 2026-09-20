#Requires -Version 7
# WHICH nested .GetNewClosure() shapes capture an empty scope, and which are fine?
#
# control-server#203 measured exactly one shape -- a probe built inside a -WhileWaiting callback that
# was itself a closure -- and the conclusion written down was the general "calling GetNewClosure inside
# a closure captures an empty scope". That may be an over-generalisation, and control-server#266 cannot
# be written until it is known which shapes are legitimate: a guard whose false positives block
# unrelated tickets is worse than no guard.
#
# Each case prints what the inner closure actually SAW. 'EMPTY' means the capture failed.
$ErrorActionPreference = 'Stop'

$results = [System.Collections.Generic.List[object]]::new()
function Note([string]$Shape, [string]$Saw, [string]$Expected) {
    $results.Add([pscustomobject]@{ Shape = $Shape; Saw = $Saw; Ok = ($Saw -ne 'EMPTY') ; Expected = $Expected })
}
function Show($v) { if ([string]::IsNullOrEmpty([string]$v)) { 'EMPTY' } else { [string]$v } }

# A stand-in for Wait-L2Condition: takes a scriptblock from elsewhere and runs it.
function Invoke-Elsewhere { param([scriptblock]$Probe) return & $Probe }

# ---- 1. baseline: GetNewClosure at a function's top level (the shape that works today)
function Case1 {
    $demandId = 'D-1'
    $probe = { $demandId }.GetNewClosure()
    Note '1 函数体顶层取闭包' (Show (Invoke-Elsewhere -Probe $probe)) 'D-1'
}
Case1

# ---- 2. the cs#203 shape: GetNewClosure inside a scriptblock that is ITSELF a closure,
#         capturing a variable from the ENCLOSING FUNCTION
function Case2 {
    $demandId = 'D-2'
    $outer = {
        $inner = { $demandId }.GetNewClosure()
        Invoke-Elsewhere -Probe $inner
    }.GetNewClosure()
    Note '2 闭包内取闭包，捕获外层函数的变量' (Show (& $outer)) 'D-2'
}
Case2

# ---- 3. the same, but capturing a variable DEFINED INSIDE the outer closure's own body
function Case3 {
    $outer = {
        $local = 'L-3'
        $inner = { $local }.GetNewClosure()
        Invoke-Elsewhere -Probe $inner
    }.GetNewClosure()
    Note '3 闭包内取闭包，捕获闭包自己体内定义的变量' (Show (& $outer)) 'L-3'
}
Case3

# ---- 4. nested inside a PLAIN scriptblock (not a closure), capturing the enclosing function's var
function Case4 {
    $demandId = 'D-4'
    $outer = {
        $inner = { $demandId }.GetNewClosure()
        Invoke-Elsewhere -Probe $inner
    }
    Note '4 普通 scriptblock 内取闭包，捕获外层函数的变量' (Show (& $outer)) 'D-4'
}
Case4

# ---- 5. nested inside a ForEach-Object block
function Case5 {
    $demandId = 'D-5'
    $seen = @(1 | ForEach-Object {
        $inner = { $demandId }.GetNewClosure()
        Invoke-Elsewhere -Probe $inner
    })
    Note '5 ForEach-Object 块内取闭包' (Show $seen[0]) 'D-5'
}
Case5

# ---- 6. nested inside a function DEFINED INSIDE a function
function Case6 {
    $demandId = 'D-6'
    function Inner6 {
        $probe = { $demandId }.GetNewClosure()
        return Invoke-Elsewhere -Probe $probe
    }
    Note '6 函数内定义的函数里取闭包' (Show (Inner6)) 'D-6'
}
Case6

# ---- 7. the outer closure takes a param; inner captures that param
function Case7 {
    $outer = {
        param($Given)
        $inner = { $Given }.GetNewClosure()
        Invoke-Elsewhere -Probe $inner
    }.GetNewClosure()
    Note '7 闭包内取闭包，捕获闭包自己的参数' (Show (& $outer 'P-7')) 'P-7'
}
Case7

# ---- 8. two levels of GetNewClosure, both capturing their own body's variable
function Case8 {
    $outer = {
        $mid = 'M-8'
        $inner = {
            $deepest = { $mid }.GetNewClosure()
            Invoke-Elsewhere -Probe $deepest
        }.GetNewClosure()
        & $inner
    }.GetNewClosure()
    Note '8 三层，中间层的变量被最内层捕获' (Show (& $outer)) 'M-8'
}
Case8

# ---- 9. no GetNewClosure at all on the inner one (what cs#203 changed to)
function Case9 {
    $demandId = 'D-9'
    $outer = {
        $inner = { $demandId }
        Invoke-Elsewhere -Probe $inner
    }.GetNewClosure()
    Note '9 内层不取闭包（cs#203 的改法）' (Show (& $outer)) 'D-9'
}
Case9

Write-Host ''
Write-Host ('{0,-52} {1,-10} {2,-10} {3}' -f '形状', '看到', '应看到', '判定')
foreach ($r in $results) {
    $verdict = if ($r.Saw -eq $r.Expected) { 'ok' } elseif ($r.Saw -eq 'EMPTY') { '*** 空作用域' } else { '?? 值不对' }
    Write-Host ('{0,-52} {1,-10} {2,-10} {3}' -f $r.Shape, $r.Saw, $r.Expected, $verdict)
}
$broken = @($results | Where-Object { $_.Saw -ne $_.Expected })
Write-Host ''
Write-Host "捕获失败的形状：$($broken.Count) / $($results.Count)"
