#Requires -Version 7
<#
.SYNOPSIS
Two-sided check of the closure-capture guard: every broken shape flagged, every legal shape left alone.

.DESCRIPTION
One-sided is not enough here and the reason is on record twice.

While this guard was being built there was a version that flagged 0 of the repository working sites
AND 0 of 3 deliberately broken fixtures -- and the 0-of-23 on its own looked like success. The cause
was a "file-level names" rule written as "any name anywhere in the file", which made every read
resolvable and the check vacuous.

Then review found the other side of the same coin: the guard flagged $global: unconditionally, which
is a false positive BY CONSTRUCTION, and no fixture could ever have caught it because every
qualified-read fixture used $script:. A shape with no fixture is a shape the self-check has an
opinion about only by accident.

So both sides run here, and the fixtures are kept in step with the grid in
evidence/l2/cs266-closure-capture/ -- one fixture per measured cell that the guard claims to judge.
Each legal fixture exists because it is nested, or qualified, or otherwise resembles a broken shape
closely enough that a guard written from intuition would flag it.
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
       Case = '从更深作用域捕获外层函数的变量（烧掉 rig-01 的那一个）'
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
       Case = '同上，但外层是普通 scriptblock，完全没有嵌套闭包'
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
       Case = '同上，但更深的作用域是函数里定义的函数'
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
    @{ Name = 'qualified-in-function.ps1'
       Case = '函数体内的 $script: 限定读（该函数自己还赋过这个名字）'
       Want = 'script:thing'
       Body = @'
function Drive {
    $script:thing = 'x'
    $probe = { $script:thing }.GetNewClosure()
    Wait-For -Probe $probe
}
'@ }
    @{ Name = 'module-top-in-function.psm1'
       Case = '模块顶层变量，被该模块【函数体内】的闭包读'
       Want = 'moduleTop'
       Body = @'
$moduleTop = 'MOD'
function Drive {
    $probe = { $moduleTop }.GetNewClosure()
    Wait-For -Probe $probe
}
'@ }
    # 以下三条是本轮独立审查补出来的缺口。前两条是守卫原先按构造看不见的形状，第三条原先
    # 被一个「不报」的夹具钉成了合法。
    @{ Name = 'script-top-in-function.ps1'
       Case = '脚本顶层变量与脚本参数，被该脚本【函数体内】的闭包读（场景把探针挪进辅助函数就是这个）'
       Want = 'Context'
       Body = @'
param($Context)
$topVar = 'TOP'
function Inner {
    $probe = { "$Context/$topVar" }.GetNewClosure()
    Wait-For -Probe $probe
}
Inner
'@ }
    @{ Name = 'callers-variable.ps1'
       Case = '捕获调用方的变量（点源函数里取闭包时的形状）'
       Want = 'callerOwned'
       Body = @'
function Get-CapturingCallersVar {
    $probe = { $callerOwned }.GetNewClosure()
    return & $probe
}
'@ }
    @{ Name = 'pipeline-dollar-underscore.ps1'
       Case = '闭包体直接读外层流水线的 $_（实测为空）'
       Want = '$_'
       Body = @'
function Drive {
    1..3 | ForEach-Object { Wait-For -Probe { $_ }.GetNewClosure() }
}
'@ }
)

foreach ($f in $broken) {
    $path = New-Fixture $f.Name $f.Body
    $found = @(Get-L2ClosureCaptureFindings -Path $path)
    $names = (@($found | ForEach-Object { $_.Names }) -join '; ')
    Add-Case "报: $($f.Case)" (($found.Count -ge 1) -and ($names -like "*$($f.Want)*")) "$($found.Count) 条: $names"
}

# ------------------------------------------- the caveat in the Reason string, both directions
#
# The guard judges by the strict column (`& path`). For a file's OWN top-level names that is an
# assumption about how the file is invoked, and the person who gets flagged reads the Reason field,
# not the README -- so the caveat has to be there, and only there. Asserting only that it appears
# would pass an implementation that appends it to everything, which would train readers to skip it.
$caveatApplies = @(Get-L2ClosureCaptureFindings -Path (Join-Path $scratch 'script-top-in-function.ps1'))
Add-Case '告警带例外说明：被点名的是文件顶层名时' `
    (@($caveatApplies | Where-Object { $_.Reason -like '*CAVEAT*' }).Count -ge 1) `
    "$($caveatApplies.Count) 条，带 CAVEAT 的 $(@($caveatApplies | Where-Object { $_.Reason -like '*CAVEAT*' }).Count) 条"

$caveatDoesNot = @(Get-L2ClosureCaptureFindings -Path (Join-Path $scratch 'nested.ps1'))
Add-Case '告警不带例外说明：被点名的是外层函数的参数时' `
    (($caveatDoesNot.Count -ge 1) -and (@($caveatDoesNot | Where-Object { $_.Reason -like '*CAVEAT*' }).Count -eq 0)) `
    "$($caveatDoesNot.Count) 条，带 CAVEAT 的 $(@($caveatDoesNot | Where-Object { $_.Reason -like '*CAVEAT*' }).Count) 条"

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
       Case = '.ps1 顶层变量与脚本参数，在【顶层】捕获'
       Body = @'
param($Context)
$fromTop = 'T'
Wait-For -Probe { "$Context/$fromTop" }.GetNewClosure()
'@ }
    @{ Name = 'foreach-stmt.ps1'
       Case = 'foreach 语句的循环变量（实测携带得到）'
       Body = @'
function Drive {
    foreach ($item in 1..3) {
        Wait-For -Probe { $item }.GetNewClosure()
    }
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
    # 以下四条同样来自本轮审查与随后的补测。前两条是守卫原先误报的形状（$global: 是按构造
    # 成立的误报，而误报是本票自己定义为昂贵的那一类）；后两条是原先没有任何夹具的合法格。
    @{ Name = 'global-qualified.ps1'
       Case = '$global: 限定读（运行时解析得到值，六格全部实测非空）'
       Body = @'
function Drive {
    $probe = { $global:gv }.GetNewClosure()
    Wait-For -Probe $probe
}
'@ }
    @{ Name = 'script-qualified-at-top.ps1'
       Case = '$script: 限定读，但在【文件顶层】取闭包（实测携带得到）'
       Body = @'
$script:atTop = 'x'
Wait-For -Probe { $script:atTop }.GetNewClosure()
'@ }
    @{ Name = 'module-top-at-top.psm1'
       Case = '.psm1 顶层变量，在【模块顶层】取闭包（实测携带得到）'
       Body = @'
$moduleTop = 'MOD'
$script:probe = { $moduleTop }.GetNewClosure()
function Get-Probe { return $script:probe }
'@ }
    @{ Name = 'nested-foreach-object.ps1'
       Case = '闭包体【内部】的 ForEach-Object 读 $_（那是闭包自己的）'
       Body = @'
function Drive {
    $probe = { @(1..2 | ForEach-Object { "v$_" }) -join ',' }.GetNewClosure()
    Wait-For -Probe $probe
}
'@ }
    # 这一格是变异验证撞出来的，不是审查提的：把 Get-VisibleNames 里取「作用域直属函数的签名
    # 参数」那一段改掉，全仓会多出一条假发现（L2RealOnboard.psm1:286 的 $Purpose），但当时
    # 所有夹具都用 param() 块写参数，一个都不会红。真实仓库代码用的是括号里的签名写法。
    @{ Name = 'signature-params.ps1'
       Case = '写在函数签名括号里的参数（不是 param() 块）'
       Body = @'
function Drive($DemandId, $Purpose) {
    $probe = { "$DemandId/$Purpose" }.GetNewClosure()
    Wait-For -Probe $probe
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

# The unknowns list must keep its entries AND keep the one that covers what was never measured.
#
# The count is asserted so that DELETING an entry reddens; it is a floor rather than an equality so
# that ADDING one does not. The content match is deliberately on the wording and not only on the
# path: review showed that a path-only match stays green when the fallback entry is replaced by an
# unrelated entry that happens to mention the same directory, which is exactly how a list like this
# loses the one line that covers the unknown.
$unknowns = @(Get-L2ClosureCaptureUnknowns)
$hasFallback = @($unknowns | Where-Object { $_ -like '*not measured in*' -and $_ -like '*cs266-closure-capture*' }).Count -eq 1
Add-Case '不确定项清单没有丢条目，且仍有那条「未测量过的形状」兜底' `
    (($unknowns.Count -ge 10) -and $hasFallback) `
    "$($unknowns.Count) 条，兜底条目$(if ($hasFallback) { '在' } else { '缺失或被改写' })"

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
