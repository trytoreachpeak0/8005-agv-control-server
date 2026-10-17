#Requires -Version 7
# READ-ONLY: print idle-return / waiting / charging / dispatch-round related keys of the installed V2 config.
$cfg = Get-Content -Raw 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json' | ConvertFrom-Json -AsHashtable
function Walk($o, $path) {
  if ($o -is [System.Collections.IDictionary]) { foreach ($k in $o.Keys) { Walk $o[$k] "$path.$k" } }
  elseif ($o -is [System.Collections.IList] -and $o.Count -gt 0 -and $o[0] -is [System.Collections.IDictionary]) { for ($i=0; $i -lt $o.Count; $i++) { Walk $o[$i] "$path[$i]" } }
  elseif ($path -match '(?i)idle|waiting|charg|battery|round|interval|period|delay|fleet|vehicles|allowedWorkTypes|zone') { "$path = $($o -join ',')" }
}
Walk $cfg ''
