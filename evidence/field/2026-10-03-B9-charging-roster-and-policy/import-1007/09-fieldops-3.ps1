$c = Get-Content -LiteralPath 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json' -Raw | ConvertFrom-Json -AsHashtable
$db = $c['ConnectionStrings']['ControlServer'] -replace '^Data Source=', ''
$key = $c['JourneyRuntime']['vehicleKey']
$d = 'D:\zhengyushao\control-server-v2-ops\fieldops-d2018450'
$f = Join-Path $d 'ControlServer.FieldOps.exe'
"database=$db fleet=$key at $(Get-Date -Format o)"
"### approve-charging-policy v1"; & $f approve-charging-policy --database $db --version 1 --approved-by "Zhengyu Shao" --role "产品负责人" --basis "用户 2026-09-29 在调度会话中批准（转述），批次 9 方案第七节第 6 条；--role 由用户 10-07 在调度会话中确认（转述）；10-07 导入授权 cs#411 issuecomment-6032690519" --source FIELD; "exit=$LASTEXITCODE"
"### activate-charging-policy v1 --dry-run"; & $f activate-charging-policy --database $db --version 1 --activated-by "Claude（AI 代理，Zhengyu Shao 10-07 授权，cs#411）" --fleet $key --dry-run; "exit=$LASTEXITCODE"
