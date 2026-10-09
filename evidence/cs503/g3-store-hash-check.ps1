#Requires -Version 7
$ErrorActionPreference = 'Stop'
$repo = 'C:/Users/szy/Desktop/8005-workspace-v2/worktrees/b503-8005-agv-control-server'
foreach ($f in 'scripts/run-demand-bearing-g3-vectors.ps1','scripts/Update-ControlServerLocal.ps1','scripts/Install-ControlServerLocal.ps1','scripts/Test-DataRootLockWait.ps1') {
    $t=$null;$e=$null; $null=[Management.Automation.Language.Parser]::ParseFile("$repo/$f",[ref]$t,[ref]$e); "ParseFile $f errors=$($e.Count)"
}
$t=$null;$e=$null; $ast=[Management.Automation.Language.Parser]::ParseFile("$repo/scripts/run-demand-bearing-g3-vectors.ps1",[ref]$t,[ref]$e)
$fn = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-StoreFilesSha256' }, $true)
. ([ScriptBlock]::Create($fn[0].Extent.Text))
$d = Join-Path $PSScriptRoot 'g3hash'; Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $d | Out-Null
$db = Join-Path $d 'controlserver.db'
[IO.File]::WriteAllText($db, 'main')
$a = Get-StoreFilesSha256 -DatabasePath $db
[IO.File]::WriteAllText("$db-wal", 'wal A')
$b = Get-StoreFilesSha256 -DatabasePath $db
[IO.File]::WriteAllText("$db-wal", 'wal B')
$c = Get-StoreFilesSha256 -DatabasePath $db
"main only     : $a"
"main + wal A  : $b"
"main + wal B  : $c"
"all distinct  : $(($a -ne $b) -and ($b -ne $c) -and ($a -ne $c))"
"stable        : $((Get-StoreFilesSha256 -DatabasePath $db) -eq $c)"
"length 64 hex : $($c -match '^[0-9a-f]{64}$')"
