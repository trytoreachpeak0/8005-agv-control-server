#Requires -Version 7
$p = 'C:/work/8005-wt/cs823-8005-agv-control-server/scripts/l2/scenarios/charging-no-progress-isolates.ps1'
$t = [IO.File]::ReadAllText($p)
$old = "`n`$journey = Read-L2SingleRow -Required"
$new = "`nStart-Sleep -Seconds 6  # cs823 red injection: widen the gap between the two reads`n`$journey = Read-L2SingleRow -Required"
if (([regex]::Matches($t, [regex]::Escape($old))).Count -ne 1) { throw 'anchor not unique' }
[IO.File]::WriteAllText($p, $t.Replace($old, $new))
