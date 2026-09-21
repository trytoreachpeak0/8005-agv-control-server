#Requires -Version 7
# Red side for control-server#262 review finding 3: the module as committed before the fix
# (HEAD), run against the three injections the whitelist now refuses. Each must be ACCEPTED by
# the old module -- that is what makes the new check necessary rather than decorative.
param([Parameter(Mandatory)][string] $Repo, [Parameter(Mandatory)][string] $Scratch)
$ErrorActionPreference = 'Stop'
$old = Join-Path $Scratch 'ParallelInstance.HEAD.psm1'
$bytes = & git -C $Repo show 'HEAD:scripts/parallel/ParallelInstance.psm1'
if ($LASTEXITCODE -ne 0) { throw 'git show failed' }
[IO.File]::WriteAllText($old, ($bytes -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
"old module: HEAD $(& git -C $Repo rev-parse --short HEAD) -> $old"
Import-Module $old -Force
$def = Read-ParallelInstanceDefinition -Path (Join-Path $Repo 'scripts/parallel/instance-factory01-v2.json')
function Copy-Def($d) { ConvertFrom-Json (ConvertTo-Json $d -Depth 12) -AsHashtable -Depth 12 }

$cases = [ordered]@{
    'fleet roster with agv01'   = { param($d) $d['journeyRuntime']['fleet'] = @(@{ agvId = '老厂前线新多仓位1'; vehicleKey = 'BROKERX-0c20ff0600d644869a6a80c186065d85' }); $d }
    'Fleet (C# casing)'         = { param($d) $d['journeyRuntime']['Fleet'] = @(@{ agvId = '老厂前线新多仓位1'; vehicleKey = 'BROKERX-0c20ff0600d644869a6a80c186065d85' }); $d }
    'AgvId beside agvId, agv01' = { param($d) $d['journeyRuntime']['AgvId'] = '老厂前线新多仓位1'; $d }
}
foreach ($name in $cases.Keys) {
    $m = & $cases[$name] (Copy-Def $def)
    $f = @(Test-ParallelInstanceDefinition -Definition $m)
    "{0,-28} old module failures: {1}  -> {2}" -f $name, $f.Count, ($f.Count -eq 0 ? 'ACCEPTED (the hole)' : ('refused: ' + ($f -join ' | ')))
}
