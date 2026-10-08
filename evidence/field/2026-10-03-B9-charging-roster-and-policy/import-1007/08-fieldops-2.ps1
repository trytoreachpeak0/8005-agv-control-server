$c = Get-Content -LiteralPath 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json' -Raw | ConvertFrom-Json -AsHashtable
$db = $c['ConnectionStrings']['ControlServer'] -replace '^Data Source=', ''
$key = $c['JourneyRuntime']['vehicleKey']
$d = 'D:\zhengyushao\control-server-v2-ops\fieldops-d2018450'
$f = Join-Path $d 'ControlServer.FieldOps.exe'
"database=$db fleet=$key at $(Get-Date -Format o)"
"### import-charging-policy formal"; & $f import-charging-policy --database $db --input (Join-Path $d "charging-policy-map26.json") --fleet $key; "exit=$LASTEXITCODE"
"### import-charger-roster empty --dry-run"; & $f import-charger-roster --database $db --input (Join-Path $d "charger-roster-empty.json") --catalog (Join-Path $d "catalog-26.json") --map 26 --fleet $key --dry-run; "exit=$LASTEXITCODE"
"### import-charger-roster empty"; & $f import-charger-roster --database $db --input (Join-Path $d "charger-roster-empty.json") --catalog (Join-Path $d "catalog-26.json") --map 26 --fleet $key; "exit=$LASTEXITCODE"
"### import-charging-policy drill --dry-run"; & $f import-charging-policy --database $db --input (Join-Path $d "charging-policy-map26-drill-20261008.json") --fleet $key --dry-run; "exit=$LASTEXITCODE"
"### import-charging-policy drill"; & $f import-charging-policy --database $db --input (Join-Path $d "charging-policy-map26-drill-20261008.json") --fleet $key; "exit=$LASTEXITCODE"
"### charger-roster"; & $f charger-roster --database $db; "exit=$LASTEXITCODE"
"### charging-policy"; & $f charging-policy --database $db --fleet $key; "exit=$LASTEXITCODE"
