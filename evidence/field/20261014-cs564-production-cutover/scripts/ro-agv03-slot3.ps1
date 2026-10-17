$f='C:/Users/HT/AppData/Local/SQCD/8005AGV/logs/agv-20261017.log'
if (-not (Test-Path $f)) { $f = (Get-ChildItem 'C:/Users/HT/AppData/Local/SQCD/8005AGV/logs' -File | Sort-Object LastWriteTime | Select-Object -Last 1).FullName }
"log $f"
Get-Content $f -Encoding utf8 | Where-Object { $_ -match '^2026-10-17T1[12]:' -and $_ -match '仓IO变化|开锁|仓位|卸货|核空|有货|未执行|Warning|Error|提示|界面' -and $_ -notmatch '忽略并发重复|请求录入Sublot|录入请求所属的站已结束' } | Select-Object -Last 40
