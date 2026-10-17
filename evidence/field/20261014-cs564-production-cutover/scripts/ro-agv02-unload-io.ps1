# READ-ONLY on agv01 (user-approved 2026-10-15): slot IO lines and unload-operation lines since 16:20, no writes.
$d = Get-ChildItem C:/Users -Directory -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'AppData/Local/SQCD/8005AGV/logs' } | Where-Object { Test-Path $_ } | Select-Object -First 1
$f = Get-ChildItem $d -File | Sort-Object LastWriteTime | Select-Object -Last 1
"log $($f.FullName) $($f.LastWriteTime)"
Get-Content $f.FullName -Encoding utf8 | Where-Object { $_ -match '^2026-10-15T(19:[45][0-9]|20:[0-9][0-9])' -and $_ -match '仓IO变化|开锁|仓位|卸货|SlotOperation|核空|未执行|Warning|Error' -and $_ -notmatch '请求录入Sublot|录入请求所属的站已结束|忽略并发重复' } | Select-Object -Last 60
"--- dup-ignore count since 16:20: " + (Get-Content $f.FullName -Encoding utf8 | Where-Object { $_ -match '^2026-10-15T16:(2|3|4)' -and $_ -match '忽略并发重复SlotOperationCommand' }).Count
