#Requires -Version 7
# READ-ONLY probe on a vehicle PC: processes, uptime, TCP to the V2 server. Changes nothing.
"now $(Get-Date -Format o) boot $((Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('o'))"
Get-Process | Where-Object { $_.ProcessName -match 'SQCD|Slot|Simulator|Agv' } | ForEach-Object { "proc $($_.ProcessName) pid=$($_.Id) session=$($_.SessionId) start=$($_.StartTime.ToString('o'))" }
Get-NetTCPConnection -RemotePort 58105,58107 -ErrorAction SilentlyContinue | ForEach-Object { "tcp $($_.LocalAddress):$($_.LocalPort) -> $($_.RemoteAddress):$($_.RemotePort) $($_.State)" }
"test 58105: $((Test-NetConnection 172.19.205.222 -Port 58105 -WarningAction SilentlyContinue).TcpTestSucceeded)"
