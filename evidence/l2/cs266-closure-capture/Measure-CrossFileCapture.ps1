#Requires -Version 7
# Cross-file visibility for .GetNewClosure(), shaped after what scripts/l2 actually does:
#   - a scenario is invoked as `& $scenarioPath -Context $context` (a script with a parameter),
#   - scenarios dot-source G3RecoveryCommon.ps1 (10 of them), which defines FUNCTIONS,
#   - modules are imported with Import-Module.
# The guard for control-server#266 needs to know which of these names a closure can capture, or it
# will be blind exactly where the two control-server#203 defects lived.
$ErrorActionPreference = 'Stop'

$dir = Join-Path ([IO.Path]::GetTempPath()) "xfile-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $dir -Force

# --- a dot-sourced file that sets a TOP-LEVEL variable and defines a function
@'
$fromDotSourceTop = 'DS-TOP'
function Get-FromDotSourcedFunction {
    $localInDotSourced = 'DS-FN'
    $probe = { $localInDotSourced }.GetNewClosure()
    return & $probe
}
function Get-CapturingCallersVar {
    # reads a variable that belongs to the CALLER's scope, captured here
    $probe = { $callerOwned }.GetNewClosure()
    return & $probe
}
'@ | Set-Content -LiteralPath (Join-Path $dir 'common.ps1') -Encoding utf8NoBOM

# --- a module with a module-scope variable and an exported function
@'
$moduleScoped = 'MOD'
function Get-ModuleScopedViaClosure {
    $probe = { $moduleScoped }.GetNewClosure()
    return & $probe
}
Export-ModuleMember -Function Get-ModuleScopedViaClosure
'@ | Set-Content -LiteralPath (Join-Path $dir 'mod.psm1') -Encoding utf8NoBOM

# --- the "scenario": invoked as a script with a parameter, like Invoke-L2Scenario does
@'
param($Context)
. (Join-Path $PSScriptRoot 'common.ps1')
function Show($v) { if ([string]::IsNullOrEmpty([string]$v)) { 'EMPTY' } else { [string]$v } }
function Invoke-Elsewhere { param([scriptblock]$Probe) return & $Probe }

# 1. the script's own PARAMETER, captured at scenario top level
$p1 = { $Context }.GetNewClosure()
"  1 脚本参数 \$Context，顶层捕获            : $(Show (Invoke-Elsewhere -Probe $p1))"

# 2. a TOP-LEVEL variable from the dot-sourced file, captured here
$p2 = { $fromDotSourceTop }.GetNewClosure()
"  2 点源文件的顶层变量，在点源方捕获        : $(Show (Invoke-Elsewhere -Probe $p2))"

# 3. a closure taken INSIDE a dot-sourced function, capturing that function's own local
"  3 点源函数内取闭包，捕获它自己的局部      : $(Show (Get-FromDotSourcedFunction))"

# 4. a closure taken inside a dot-sourced function, capturing the CALLER's variable
$callerOwned = 'CALLER'
"  4 点源函数内取闭包，捕获调用方的变量      : $(Show (Get-CapturingCallersVar))"

# 5. module-scope variable captured by a closure inside that module
Import-Module (Join-Path $PSScriptRoot 'mod.psm1') -Force
"  5 模块作用域变量，模块内函数取闭包        : $(Show (Get-ModuleScopedViaClosure))"

# 6. a module-scope variable read from OUTSIDE the module (not exported as a variable)
$p6 = { $moduleScoped }.GetNewClosure()
"  6 模块作用域变量，模块外捕获              : $(Show (Invoke-Elsewhere -Probe $p6))"
'@ | Set-Content -LiteralPath (Join-Path $dir 'scenario.ps1') -Encoding utf8NoBOM

Write-Host '形状                                        结果'
& (Join-Path $dir 'scenario.ps1') -Context 'CTX'

Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
