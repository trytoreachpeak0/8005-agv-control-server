#Requires -Version 7
# Step 21 (governed facts on new database B): catalog, seed, bind-io x3, waiting points, charger roster, charging policy.
# Each import is dry-run first; any non-zero exit stops the script. -Fleet is the installed fleet's vehicle keys.
param([Parameter(Mandatory)][string] $Fleet, [switch] $Commit)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$f  = 'D:\zhengyushao\control-server-v2-ops\fieldops-96a88db0\ControlServer.FieldOps.exe'
$db = 'C:\ProgramData\8005\ControlServer.V2\data\controlserver.db'
$in = 'D:\zhengyushao\control-server-v2-ops\prod-20261014\inputs'
$cat = "$in\catalog-26.json"
function Run([string] $title, [string[]] $a) { "=== $title"; & $f @a; $x = $LASTEXITCODE; "exit=$x"; if ($x -ne 0) { throw "$title failed ($x)" } }
"### now $(Get-Date -Format o) fleet=$Fleet commit=$Commit"
"=== catalog"; & 'D:\zhengyushao\control-server-v2-ops\Export-RiotStationCatalog.ps1' -MapId 26 -OutFile $cat
Run 'task-type-stations (read)' @('task-type-stations', '--database', $db, '--map', '26')
Run 'status before' @('status', '--database', $db)
if (-not $Commit) {
  Run 'waiting points dry-run' @('import-waiting-points','--database',$db,'--input',"$in\waiting-points-map26.csv",'--catalog',$cat,'--map','26','--fleet',$Fleet,'--dry-run')
  Run 'charger roster dry-run' @('import-charger-roster','--database',$db,'--input',"$in\charger-roster-map26-station211.production.json",'--catalog',$cat,'--map','26','--fleet',$Fleet,'--dry-run')
  Run 'charging policy dry-run' @('import-charging-policy','--database',$db,'--input',"$in\charging-policy-map26.json",'--fleet',$Fleet,'--dry-run')
  return
}
Run 'seed-approved-facts' @('seed-approved-facts','--database',$db)
foreach ($agv in '老厂前线新多仓位1','老厂前线新多仓位2','老厂前线新多仓位3') { Run "bind-io $agv" @('bind-io','--database',$db,'--agv',$agv) }
Run 'waiting points' @('import-waiting-points','--database',$db,'--input',"$in\waiting-points-map26.csv",'--catalog',$cat,'--map','26','--fleet',$Fleet)
Run 'charger roster' @('import-charger-roster','--database',$db,'--input',"$in\charger-roster-map26-station211.production.json",'--catalog',$cat,'--map','26','--fleet',$Fleet)
Run 'charging policy' @('import-charging-policy','--database',$db,'--input',"$in\charging-policy-map26.json",'--fleet',$Fleet)
Run 'approve charging policy' @('approve-charging-policy','--database',$db,'--version','1','--approved-by','Zhengyu Shao','--role','product owner','--basis','U4: https://github.com/trytoreachpeak0/8005-agv-program/issues/170#issuecomment-6138103462 (charging policy v1 re-approved on production cutover new database C after the 2026-10-15 upper-id collision, control-server#564)','--source','FIELD')
Run 'activate charging policy' @('activate-charging-policy','--database',$db,'--version','1','--activated-by','Zhengyu Shao (U4; executed by production cutover field session)','--fleet',$Fleet)
Run 'read waiting points' @('read-waiting-points','--database',$db,'--map','26','--fleet',$Fleet)
Run 'read charger roster' @('charger-roster','--database',$db)
Run 'read charging policy' @('charging-policy','--database',$db,'--fleet',$Fleet)
Run 'status after' @('status','--database',$db)
