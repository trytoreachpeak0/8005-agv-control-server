#Requires -Version 7
# READ-ONLY: recent Application/System events about the V2 service and .NET runtime, and the V2 data root listing.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$since = (Get-Date).AddMinutes(-10)
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since } -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -match '\.NET|ControlServer|Application Error' -or $_.Message -match 'ControlServer' } | Select-Object -First 6 | ForEach-Object { "APP $($_.TimeCreated.ToString('HH:mm:ss')) $($_.ProviderName) $($_.Id): $(($_.Message -split "`n" | Select-Object -First 12) -join ' / ')" }
Get-WinEvent -FilterHashtable @{ LogName = 'System'; StartTime = $since; ProviderName = 'Service Control Manager' } -ErrorAction SilentlyContinue | Select-Object -First 4 | ForEach-Object { "SCM $($_.TimeCreated.ToString('HH:mm:ss')) $($_.Id): $($_.Message)" }
"root:"; Get-ChildItem -Force 'C:\ProgramData\8005\ControlServer.V2' | ForEach-Object { "  $($_.Name)" }
"logs dir tail:"; Get-ChildItem 'C:\ProgramData\8005\ControlServer.V2.pre-collision-20261015' | ForEach-Object { "  aside $($_.Name)" }
