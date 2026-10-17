$f='C:/Users/HT/AppData/Local/SQCD/8005AGV/logs/agv-20261015.log'
$lines = Get-Content $f -Encoding utf8
$hits = $lines | Where-Object { $_ -match '51eac174|92ef588c|e80362ae|卸货|UNLOAD|Unload|仓门|门' -and $_ -match '^2026-10-15T1(5:[3-5]|6:)' -and $_ -notmatch '忽略并发重复|请求录入Sublot|录入请求所属的站已结束' }
$hits | Select-Object -First 80
"--- count dup-ignore: " + ($lines | Where-Object { $_ -match '忽略并发重复SlotOperationCommand：attempt=51eac174' }).Count
"--- first dup-ignore: " + (($lines | Where-Object { $_ -match '忽略并发重复SlotOperationCommand：attempt=51eac174' } | Select-Object -First 1))
