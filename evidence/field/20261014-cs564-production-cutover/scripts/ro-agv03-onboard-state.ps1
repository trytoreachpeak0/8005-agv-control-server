Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match 'SQCD|Onboard|Agv' } | ForEach-Object { "proc $($_.ProcessName) pid=$($_.Id) start=$($_.StartTime)" }
$f = Get-ChildItem 'C:/Users/HT/AppData/Local/SQCD/8005AGV/logs' -File | Sort-Object LastWriteTime | Select-Object -Last 1
"log $($f.Name) $($f.LastWriteTime)"
Get-Content $f.FullName -Encoding utf8 | Where-Object { $_ -match '^2026-10-15T16:1[5-9]' -and $_ -notmatch 'Heartbeat' } | Where-Object { $_ -match 'Warning|Error|启动|退出|vehicle-safety|安全投影|Safety|取消|cancel' } | Select-Object -Last 30
