#Requires -Version 7
# READ-ONLY: lines of today's V2 log matching a pattern, newest last.
param([Parameter(Mandatory)][string] $Pattern, [int] $Last = 10, [int] $Width = 400)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Get-Content ((Get-ChildItem 'C:\ProgramData\8005\ControlServer.V2\logs' -Filter 'controlserver-*.ndjson' | Sort-Object Name | Select-Object -Last 1).FullName) -Encoding utf8 | Where-Object { $_ -match $Pattern } | Select-Object -Last $Last | ForEach-Object { $_.Substring(0,[Math]::Min($Width,$_.Length)) }
