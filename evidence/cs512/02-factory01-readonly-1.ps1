$ErrorActionPreference = 'Continue'
$t = '8005 AGV ControlServer V2 FakeMesIngest'
'=== now / boot / Schedule service start'
Get-Date -Format o
(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToString('o')
$svc = Get-CimInstance Win32_Service -Filter "Name='Schedule'"
"Schedule pid=$($svc.ProcessId) state=$($svc.State)"
(Get-Process -Id $svc.ProcessId).StartTime.ToString('o')
'=== task xml'
Export-ScheduledTask -TaskName $t
'=== task info'
Get-ScheduledTaskInfo -TaskName $t | Format-List LastRunTime, LastTaskResult, NextRunTime, NumberOfMissedRuns
'=== TaskScheduler/Operational log state'
Get-WinEvent -ListLog 'Microsoft-Windows-TaskScheduler/Operational' | Format-List IsEnabled, RecordCount, LogMode
'=== TaskScheduler/Operational events for the task'
Get-WinEvent -LogName 'Microsoft-Windows-TaskScheduler/Operational' -FilterXPath "*[EventData[Data[@Name='TaskName']='\$t']]" -ErrorAction SilentlyContinue |
    Sort-Object TimeCreated | Format-List TimeCreated, Id, Message
'=== Application 1026/1000, 2026-10-07 19:10-19:30, pwsh only'
$all = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1026; StartTime = [datetime]'2026-10-07 19:10'; EndTime = [datetime]'2026-10-07 19:30' } -ErrorAction SilentlyContinue)
$all | Where-Object { $_.Message -match 'pwsh' } | Format-List TimeCreated, Id, ProviderName, Message
"other-process entries in window: $(@($all | Where-Object { $_.Message -notmatch 'pwsh' }).Count)"
'=== pwsh on the machine'
[Environment]::GetEnvironmentVariable('Path', 'Machine') -split ';' | Where-Object { $_ -match 'PowerShell' }
where.exe pwsh
Get-ChildItem 'C:\Program Files\PowerShell\*\pwsh.exe' | ForEach-Object { "$($_.FullName) $($_.VersionInfo.ProductVersion) $($_.LastWriteTime.ToString('o'))" }
