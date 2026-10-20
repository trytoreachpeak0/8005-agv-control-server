#Requires -Version 7
$p = 'C:/work/8005-wt/cs823-8005-agv-control-server/scripts/l2/scenarios/charging-no-progress-isolates.ps1'
$t = [IO.File]::ReadAllText($p)
$old = "        `$cycle = Get-Cycle`r`n        `$journey = "
$new = "        `$cycle = Get-Cycle`r`n        Start-Sleep -Seconds 6  # cs823 delay injection on the fixed script`r`n        `$journey = "
if (([regex]::Matches($t, [regex]::Escape($old))).Count -ne 1) { throw 'anchor not unique' }
[IO.File]::WriteAllText($p, $t.Replace($old, $new))
