[Console]::OutputEncoding = [Text.Encoding]::UTF8
$s = Get-CimInstance Win32_Service -Filter "Name='8005 AGV ControlServer V2'"; "svc state=$($s.State) path=$($s.PathName)"
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-20)} -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -match '\.NET Runtime|Application Error|ControlServer|Service Control' -or $_.Message -match 'ControlServer' } | Select-Object -First 12 | ForEach-Object { "--- $($_.TimeCreated) $($_.ProviderName) $($_.Id)"; ($_.Message -split "`n" | Select-Object -First 25) -join "`n" }
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Service Control Manager'; StartTime=(Get-Date).AddMinutes(-20)} -ErrorAction SilentlyContinue | Select-Object -First 8 | ForEach-Object { "SCM $($_.TimeCreated) $($_.Id) $($_.Message)" }
$f = (Get-ChildItem 'C:\ProgramData\8005\ControlServer.V2\logs' -Filter 'controlserver-*.ndjson' | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
"log $f"
Get-Content $f -Tail 400 -Encoding utf8 | Where-Object { $_ -match '"@t":"2026-10-17T05:3[0-9]' -and $_ -match 'Error|Fatal|Migrat|migrat|Exception' } | Select-Object -Last 15 | ForEach-Object { $_.Substring(0,[Math]::Min(700,$_.Length)) }
