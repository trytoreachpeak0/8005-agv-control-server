#Requires -Version 7
# READ-ONLY: agv03 onboard log lines 16:08:00-16:20:59 on 2026-10-17, excluding heartbeats.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$f = 'C:/Users/HT/AppData/Local/SQCD/8005AGV/logs/agv-20261017.log'
"file: $f exists=$(Test-Path $f)"
Get-Content $f -Encoding utf8 | Where-Object { $_ -match '^2026-10-17T16:(0[89]|1[0-9]|20)' -and $_ -notmatch 'Heartbeat' }
