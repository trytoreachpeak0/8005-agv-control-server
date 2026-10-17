#Requires -Version 7
# Stop the V2 service and copy the whole V2 data root to a timestamped backup (rollback DB for the v4 upgrade). MVP untouched.
$ErrorActionPreference = 'Stop'
$svc = '8005 AGV ControlServer V2'
Stop-Service -Name $svc
(Get-Service $svc).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
"stopped: $((Get-Service $svc).Status)  MVP: $((Get-Service '8005 AGV ControlServer').Status)"
$src = 'C:\ProgramData\8005\ControlServer.V2'
$dst = "D:\zhengyushao\control-server-v2-ops\db-backup-pre-v4-$(Get-Date -Format yyyyMMdd-HHmmss)"
Copy-Item -LiteralPath $src -Destination $dst -Recurse
"backup: $dst"
Get-ChildItem "$dst\data" | ForEach-Object { "$($_.Name) $($_.Length)" }
"sha256 db: $((Get-FileHash "$dst\data\controlserver.db").Hash)  src: $((Get-FileHash "$src\data\controlserver.db").Hash)"
