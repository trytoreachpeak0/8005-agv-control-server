$c = Get-Content -LiteralPath 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json' -Raw | ConvertFrom-Json -AsHashtable
$db = $c['ConnectionStrings']['ControlServer'] -replace '^Data Source=', ''
$key = $c['JourneyRuntime']['vehicleKey']
$d = 'D:\zhengyushao\control-server-v2-ops\fieldops-d2018450'
$f = Join-Path $d 'ControlServer.FieldOps.exe'
"database=$db fleet=$key at $(Get-Date -Format o)"
"### charging-policy (baseline)"; & $f charging-policy --database $db --fleet $key; "exit=$LASTEXITCODE"
"### import-charging-policy formal --dry-run"; & $f import-charging-policy --database $db --input (Join-Path $d "charging-policy-map26.json") --fleet $key --dry-run; "exit=$LASTEXITCODE"
