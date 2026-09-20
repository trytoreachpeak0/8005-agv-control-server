#Requires -Version 7
<#
.SYNOPSIS
The grid that settles what .GetNewClosure() carries: WHERE the closure is taken x WHAT name it reads.

.DESCRIPTION
This supersedes the three earlier Measure-*.ps1 in this directory as the authority on the rule. They
each varied one thing and each produced a correct row; read together they produced an over-general
rule three times over (see README). The grid varies both axes at once, so a row and a column can be
told apart.

Two things make the output trustworthy rather than merely present:

  - Every location has a CONTROL that must come out non-empty (its own local, its own parameter, or a
    direct read at the same spot). An all-EMPTY column would otherwise be indistinguishable from a
    broken fixture.
  - Every closure is executed in a FOREIGN scope -- a function inside the module -- so what is
    observed is what the closure carries, not an ambient lookup at the read site. Running a closure
    where its variables happen to be visible measures nothing.

The script writes result-capture-grid.txt beside itself. It writes the file from PowerShell with
-Encoding utf8NoBOM rather than being redirected by a shell, because redirecting this output through
bash mangles the Chinese.
#>
[CmdletBinding()]
param([string]$OutputPath = (Join-Path $PSScriptRoot 'result-capture-grid.txt'))

$ErrorActionPreference = 'Stop'
$lines = [System.Collections.Generic.List[string]]::new()
function Emit([string]$Text) { $lines.Add($Text); Write-Host $Text }

$dir = Join-Path ([IO.Path]::GetTempPath()) "capture-grid-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $dir -Force

# ---- the module: the foreign execution scope, and location C (inside a module function) plus
# ---- location D (the module's own top level)
@'
$modTopUnqualified = 'MOD-TOP-UNQ'
$script:modTopScript = 'MOD-TOP-SCRIPT'

function Show($v) { if ([string]::IsNullOrEmpty([string]$v)) { 'EMPTY' } else { [string]$v } }
function Invoke-Elsewhere { param([scriptblock]$Probe) return & $Probe }

# D: closures taken at the MODULE's own top level
$script:probeD_unq = { $modTopUnqualified }.GetNewClosure()
$script:probeD_scr = { $script:modTopScript }.GetNewClosure()

function Get-GridC {
    $ownLocal = 'OWN'
    return [ordered]@{
        'C1 自己的局部（对照组，必须非空）' = Show (Invoke-Elsewhere { $ownLocal }.GetNewClosure())
        'C2 模块顶层名（无限定）'           = Show (Invoke-Elsewhere { $modTopUnqualified }.GetNewClosure())
        'C3 $script: 限定读'               = Show (Invoke-Elsewhere { $script:modTopScript }.GetNewClosure())
        'C4 $global: 限定读'               = Show (Invoke-Elsewhere { $global:gv }.GetNewClosure())
        'C5 外层流水线的 $_'               = Show ((@(1..2 | ForEach-Object { Invoke-Elsewhere { $_ }.GetNewClosure() })) -join ',')
    }
}
function Get-GridD {
    return [ordered]@{
        'D2 模块顶层名（无限定）' = Show (Invoke-Elsewhere $script:probeD_unq)
        'D3 $script: 限定读'     = Show (Invoke-Elsewhere $script:probeD_scr)
    }
}
Export-ModuleMember -Function Invoke-Elsewhere, Show, Get-GridC, Get-GridD
'@ | Set-Content -LiteralPath (Join-Path $dir 'grid-mod.psm1') -Encoding utf8NoBOM

# ---- the script under test: location A (its own top level) and B (inside one of its functions)
@'
param($Context)
Import-Module (Join-Path $PSScriptRoot 'grid-mod.psm1') -Force
$topUnqualified = 'TOP-UNQ'
$script:topScript = 'TOP-SCRIPT'
if (-not (Test-Path variable:global:gv)) { $global:gv = 'GLOBAL' }

function Get-GridB {
    param($Passed)
    $ownLocal = 'OWN'
    $out = [ordered]@{}
    $out['B1 自己的局部（对照组，必须非空）'] = Show (Invoke-Elsewhere { $ownLocal }.GetNewClosure())
    $out['B1p 本函数参数（对照组）']          = Show (Invoke-Elsewhere { $Passed }.GetNewClosure())
    $out['B2 文件顶层名（无限定）']           = Show (Invoke-Elsewhere { $topUnqualified }.GetNewClosure())
    $out['B2c 同一处直接读（对照组）']        = Show $topUnqualified
    $out['B3 $script: 限定读']               = Show (Invoke-Elsewhere { $script:topScript }.GetNewClosure())
    $out['B4 $global: 限定读']               = Show (Invoke-Elsewhere { $global:gv }.GetNewClosure())
    $out['B5 脚本参数 $Context']             = Show (Invoke-Elsewhere { $Context }.GetNewClosure())
    $out['B6 外层流水线的 $_']               = Show ((@(1..2 | ForEach-Object { Invoke-Elsewhere { $_ }.GetNewClosure() })) -join ',')
    $out['B7 闭包体内部的 ForEach-Object 读 $_'] = Show (Invoke-Elsewhere { @(1..2 | ForEach-Object { "v$_" }) -join ',' }.GetNewClosure())
    foreach ($item in 'i1') {
        $out['B8 foreach 语句的循环变量'] = Show (Invoke-Elsewhere { $item }.GetNewClosure())
    }
    $late = { $assignedLater }.GetNewClosure()
    $assignedLater = 'LATE'
    $out['B9 取闭包之后才赋值'] = Show (Invoke-Elsewhere $late)
    return $out
}

$rowsA = [ordered]@{}
$rowsA['A2 文件顶层名（无限定）'] = Show (Invoke-Elsewhere { $topUnqualified }.GetNewClosure())
$rowsA['A3 $script: 限定读']     = Show (Invoke-Elsewhere { $script:topScript }.GetNewClosure())
$rowsA['A4 $global: 限定读']     = Show (Invoke-Elsewhere { $global:gv }.GetNewClosure())
$rowsA['A5 脚本参数 $Context']   = Show (Invoke-Elsewhere { $Context }.GetNewClosure())
$rowsA['A6 外层流水线的 $_']     = Show ((@(1..2 | ForEach-Object { Invoke-Elsewhere { $_ }.GetNewClosure() })) -join ',')

foreach ($k in $rowsA.Keys) { "{0,-44} : {1}" -f $k, $rowsA[$k] }
$rowsB = Get-GridB -Passed 'PARAM'
foreach ($k in $rowsB.Keys) { "{0,-44} : {1}" -f $k, $rowsB[$k] }
$rowsC = Get-GridC
foreach ($k in $rowsC.Keys) { "{0,-44} : {1}" -f $k, $rowsC[$k] }
$rowsD = Get-GridD
foreach ($k in $rowsD.Keys) { "{0,-44} : {1}" -f $k, $rowsD[$k] }
'@ | Set-Content -LiteralPath (Join-Path $dir 'grid-sub.ps1') -Encoding utf8NoBOM

@'
& (Join-Path $PSScriptRoot 'grid-sub.ps1') -Context 'CTX'
'@ | Set-Content -LiteralPath (Join-Path $dir 'grid-parent.ps1') -Encoding utf8NoBOM

$pwshExe = (Get-Process -Id $PID).Path

Emit "位置 A = 脚本自己的顶层   B = 脚本的函数体内   C = 模块的函数体内   D = 模块自己的顶层"
Emit "EMPTY = 闭包读出来是空。带「对照组」的行必须非空，否则这一格的夹具本身就是坏的。"
Emit ''
Emit '######## 调法一：pwsh -File grid-sub.ps1（脚本顶层即全局作用域）'
foreach ($l in & $pwshExe -NoProfile -File (Join-Path $dir 'grid-sub.ps1') -Context 'CTX') { Emit $l }
Emit ''
Emit '######## 调法二：父脚本用调用运算符 & 跑它（Invoke-L2Scenario 调场景的方式）'
foreach ($l in & $pwshExe -NoProfile -File (Join-Path $dir 'grid-parent.ps1')) { Emit $l }
Emit ''
Emit '两种调法只在 B2 与 B5 上不同：pwsh -File 时脚本顶层名落进全局作用域，函数体内的闭包'
Emit '于是也读得到；用 & 调时脚本有自己的作用域，读不到。守卫按 & 这一种判，因为场景是这么跑的。'

Set-Content -LiteralPath $OutputPath -Value $lines -Encoding utf8NoBOM
Write-Host ''
Write-Host "已写入 $OutputPath"
Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
