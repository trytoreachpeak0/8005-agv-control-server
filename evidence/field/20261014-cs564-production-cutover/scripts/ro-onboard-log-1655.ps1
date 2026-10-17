#Requires -Version 7
# READ-ONLY: agv02 onboard log lines 16:55:00-17:09:59 on 2026-10-17, excluding heartbeats.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$f = 'C:/Users/HT/AppData/Local/SQCD/8005AGV/logs/agv-20261017.log'
"file: $f exists=$(Test-Path $f)"
Get-Content $f -Encoding utf8 | Where-Object { $_ -match '^2026-10-17T1(6:5[5-9]|7:0[0-9])' -and $_ -notmatch 'Heartbeat' }
