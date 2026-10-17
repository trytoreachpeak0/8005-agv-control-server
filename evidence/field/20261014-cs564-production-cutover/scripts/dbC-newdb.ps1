#Requires -Version 7
# DB C (2026-10-15 collision fix): stop V2, rename database B aside, create an empty root with the same ACL, then START the service (fast path, auto-migrate). Never touches the MVP service.
# the same ACL. Does NOT start the service. Never touches the MVP service.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$root = 'C:\ProgramData\8005\ControlServer.V2'
$aside = 'C:\ProgramData\8005\ControlServer.V2.pre-collision-20261015'
$svc = '8005 AGV ControlServer V2'
if (Test-Path -LiteralPath $aside) { throw "target exists: $aside" }
"### now $(Get-Date -Format o)"
"MVP before: $((Get-Service '8005 AGV ControlServer').Status)/$((Get-Service '8005 AGV ControlServer').StartType)"
Stop-Service -Name $svc
(Get-Service $svc).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
$deadline = (Get-Date).AddSeconds(30)
while (Get-Process -Name ControlServer.Host -ErrorAction SilentlyContinue | Where-Object { $_.Path -like 'C:\Program Files\8005 AGV\ControlServer.V2\*' }) { if ((Get-Date) -gt $deadline) { throw 'V2 host process still running' }; Start-Sleep -Milliseconds 500 }
"V2 service: $((Get-Service $svc).Status)/$((Get-Service $svc).StartType)"
$acl = Get-Acl -LiteralPath $root
Rename-Item -LiteralPath $root -NewName (Split-Path -Leaf $aside)
"renamed to $aside"
Get-ChildItem -LiteralPath $aside -Recurse -File -Include *.db, *.db-wal, *.db-shm | ForEach-Object { "  $($_.Length) $((Get-FileHash $_.FullName).Hash) $($_.FullName)" }
New-Item -ItemType Directory -Path $root | Out-Null
Set-Acl -LiteralPath $root -AclObject $acl
"new root items: $((Get-ChildItem -LiteralPath $root -Force | Measure-Object).Count)"
"ACL same as old: $((Get-Acl -LiteralPath $root).Sddl -eq $acl.Sddl)"
"V2 service after: $((Get-Service $svc).Status)/$((Get-Service $svc).StartType)"
"MVP after: $((Get-Service '8005 AGV ControlServer').Status)/$((Get-Service '8005 AGV ControlServer').StartType)"
Start-Service -Name $svc
(Get-Service $svc).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
Start-Sleep -Seconds 20
"V2 service started: $((Get-Service $svc).Status)/$((Get-Service $svc).StartType)"
Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object { "  new $($_.Length) $($_.FullName)" }
