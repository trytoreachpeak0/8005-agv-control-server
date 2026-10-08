$c = Get-Content -LiteralPath 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json' -Raw | ConvertFrom-Json -AsHashtable
$db = $c['ConnectionStrings']['ControlServer'] -replace '^Data Source=', ''
$key = $c['JourneyRuntime']['vehicleKey']
$d = 'D:\zhengyushao\control-server-v2-ops\fieldops-d2018450'
$f = Join-Path $d 'ControlServer.FieldOps.exe'
"database=$db fleet=$key at $(Get-Date -Format o)"
"### activate-charging-policy v1"; & $f activate-charging-policy --database $db --version 1 --activated-by "Claude（AI 代理，Zhengyu Shao 10-07 授权，cs#411）" --fleet $key; "exit=$LASTEXITCODE"
"### charging-policy"; & $f charging-policy --database $db --fleet $key; "exit=$LASTEXITCODE"
"### charger-roster"; & $f charger-roster --database $db; "exit=$LASTEXITCODE"
"### services"; Get-Service -Name "8005 AGV ControlServer","8005 AGV ControlServer V2" | ForEach-Object { $_.Name + " " + $_.Status }; Get-NetTCPConnection -State Listen -LocalPort 58005,58007,58105,58107 | ForEach-Object { "{0}:{1} pid={2}" -f $_.LocalAddress,$_.LocalPort,$_.OwningProcess }
