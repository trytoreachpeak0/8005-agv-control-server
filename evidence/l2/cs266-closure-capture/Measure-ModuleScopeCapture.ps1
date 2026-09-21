#Requires -Version 7
$ErrorActionPreference = 'Stop'
$dir = Join-Path ([IO.Path]::GetTempPath()) "modscope-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $dir -Force
@'
$moduleTop = 'MOD-TOP'
function Show($v) { if ([string]::IsNullOrEmpty([string]$v)) { 'EMPTY' } else { [string]$v } }
function Run($sb) { return & $sb }

function T1 { $p = { $moduleTop }.GetNewClosure(); Show (Run $p) }          # 取闭包
function T2 { $p = { $moduleTop };                 Show (Run $p) }          # 对照：不取闭包
function T3 { Show $moduleTop }                                            # 对照：直接读
function T4 { $copy = $moduleTop; $p = { $copy }.GetNewClosure(); Show (Run $p) }  # 先复制再捕获
function T5 { $p = { $script:moduleTop }.GetNewClosure(); Show (Run $p) }   # $script: 限定 + 闭包
function T6 { $p = { $script:moduleTop };                  Show (Run $p) }  # 对照：$script: 不取闭包
Export-ModuleMember -Function T1,T2,T3,T4,T5,T6
'@ | Set-Content -LiteralPath (Join-Path $dir 'm.psm1') -Encoding utf8NoBOM
Import-Module (Join-Path $dir 'm.psm1') -Force
"  T1 模块顶层变量 + 取闭包          : $(T1)"
"  T2 同上但不取闭包（对照）         : $(T2)"
"  T3 直接读（对照，证明变量存在）   : $(T3)"
"  T4 先复制到本地再取闭包           : $(T4)"
"  T5 `$script: 限定 + 取闭包         : $(T5)"
"  T6 `$script: 限定不取闭包（对照）  : $(T6)"
Remove-Module m -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
