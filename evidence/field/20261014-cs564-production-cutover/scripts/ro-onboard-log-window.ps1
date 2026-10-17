$f='C:/Users/HT/AppData/Local/SQCD/8005AGV/logs/agv-20261015.log'
Get-Content $f -Encoding utf8 | Where-Object { $_ -match '^2026-10-15T(15:5[89]|16:)' -and $_ -notmatch 'Heartbeat|请求录入Sublot' } | Select-Object -Last 120
