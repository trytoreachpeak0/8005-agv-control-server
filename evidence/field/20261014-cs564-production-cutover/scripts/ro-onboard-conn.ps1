$f = Get-ChildItem 'C:/Users/HT/AppData/Local/SQCD/8005AGV/logs' -File | Sort-Object LastWriteTime | Select-Object -Last 1
"log $($f.Name) $($f.LastWriteTime)"
Get-Content $f.FullName -Encoding utf8 | Where-Object { $_ -match '会话状态发布|上层会话不可用|连接已关闭' } | Select-Object -Last 4
