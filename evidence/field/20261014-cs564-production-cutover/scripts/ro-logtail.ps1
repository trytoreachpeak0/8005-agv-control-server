#Requires -Version 7
# READ-ONLY: tail of today's V2 log, non-HTTP lines.
param([int] $Last = 15, [int] $Width = 600)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Get-Content ((Get-ChildItem 'C:\ProgramData\8005\ControlServer.V2\logs' -Filter 'controlserver-*.ndjson' | Sort-Object Name | Select-Object -Last 1).FullName) -Tail 400 -Encoding utf8 | Where-Object { $_ -notmatch 'HTTP request|responded \{StatusCode\}' } | Select-Object -Last $Last | ForEach-Object { $_.Substring(0,[Math]::Min($Width,$_.Length)) }
