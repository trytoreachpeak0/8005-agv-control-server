$ErrorActionPreference = 'Continue'
$from = [datetime]'2026-10-07 19:11'; $to = [datetime]'2026-10-07 19:20'
'=== pwsh install time'
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -match 'PowerShell 7' } | Format-List DisplayName, DisplayVersion, InstallDate
(Get-Item 'C:\Program Files\PowerShell\7').CreationTime.ToString('o')
'=== security products and policies'
Get-Service | Where-Object { $_.DisplayName -match '360|Huorong|HipsDaemon|Symantec|McAfee|Trend|Kaspersky|Sophos|ESET|CrowdStrike|Defender|Sangfor|Qianxin|Tianqing|Rising|Kingsoft' } | Format-Table Name, DisplayName, Status
Get-MpComputerStatus -ErrorAction SilentlyContinue | Format-List AMServiceEnabled, RealTimeProtectionEnabled, AntivirusEnabled
(Get-AppLockerPolicy -Effective -ErrorAction SilentlyContinue).RuleCollections | ForEach-Object { "$($_.RuleCollectionType) rules=$(@($_).Count) mode=$($_.EnforcementMode)" }
'=== machine environment: names only, plus the PowerShell/.NET ones in full'
$m = [Environment]::GetEnvironmentVariables('Machine')
($m.Keys | Sort-Object) -join ', '
$m.Keys | Where-Object { $_ -match '^(PSModulePath|__PSLockdownPolicy|POWERSHELL_|DOTNET_|COMPlus_)' } | ForEach-Object { "$_=$($m[$_])" }
'=== SYSTEM profile PowerShell state'
Get-ChildItem 'C:\Windows\System32\config\systemprofile\AppData\Local\Microsoft\PowerShell' -Force -ErrorAction SilentlyContinue | Format-Table Name, Length, LastWriteTime
Get-ChildItem 'C:\Windows\System32\config\systemprofile\Documents\PowerShell' -Force -ErrorAction SilentlyContinue | Format-Table Name, Length, LastWriteTime
'=== Security 4688/4689 for pwsh in window (if auditing is on)'
Get-WinEvent -FilterHashtable @{ LogName = 'Security'; Id = 4688, 4689; StartTime = $from; EndTime = $to } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'pwsh' } | Format-List TimeCreated, Id, Message
'=== Defender / AppLocker / CodeIntegrity events in window'
foreach ($log in 'Microsoft-Windows-Windows Defender/Operational', 'Microsoft-Windows-AppLocker/EXE and DLL', 'Microsoft-Windows-CodeIntegrity/Operational') {
    "--- $log"
    Get-WinEvent -FilterHashtable @{ LogName = $log; StartTime = $from; EndTime = $to } -ErrorAction SilentlyContinue | Format-List TimeCreated, Id, Message
}
'=== System log in window, PowerShell- or task-related only'
Get-WinEvent -FilterHashtable @{ LogName = 'System'; StartTime = $from; EndTime = $to } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'pwsh|PowerShell|Task Scheduler|FakeMesIngest' } | Format-List TimeCreated, Id, ProviderName, Message
'=== PowerShell Core operational log in window'
Get-WinEvent -FilterHashtable @{ LogName = 'PowerShellCore/Operational'; StartTime = $from; EndTime = $to } -ErrorAction SilentlyContinue | Format-List TimeCreated, Id, Message
'=== other tasks whose action runs pwsh (name, user, last result only)'
Get-ScheduledTask | Where-Object { @($_.Actions | Where-Object { $_.PSObject.Properties['Execute'] -and $_.Execute -match 'pwsh' }).Count -gt 0 } |
    ForEach-Object { $i = $_ | Get-ScheduledTaskInfo; "{0}{1} user={2} last={3} result={4}" -f $_.TaskPath, $_.TaskName, $_.Principal.UserId, $i.LastRunTime, $i.LastTaskResult }
