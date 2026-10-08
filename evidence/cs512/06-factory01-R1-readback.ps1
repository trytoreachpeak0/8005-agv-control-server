$ErrorActionPreference = 'Continue'
$task = '8005 AGV ControlServer V2 FakeMesIngest'
$logDir = 'D:\zhengyushao\control-server-v2-ops\logs'
$since = [datetime]::Today.AddHours(17).AddMinutes(30)
"now: $(Get-Date -Format o)"
$info = Get-ScheduledTaskInfo -TaskName $task
"task state=$((Get-ScheduledTask -TaskName $task).State) lastTaskResult=0x{0:X8}" -f ([uint32] $info.LastTaskResult)
"log dir exists: $(Test-Path -LiteralPath $logDir)"
Get-ChildItem -LiteralPath $logDir -File -ErrorAction SilentlyContinue | ForEach-Object { "--- $($_.Name) ($($_.Length) bytes, written $($_.LastWriteTime.ToString('o')))"; Get-Content -LiteralPath $_.FullName -Tail 30 }
'=== SYSTEM pwsh host starts since 17:30'
Get-WinEvent -FilterHashtable @{ LogName = 'PowerShellCore/Operational'; Id = 40961, 53504; StartTime = $since } -ErrorAction SilentlyContinue |
    Where-Object { $_.UserId -and $_.UserId.Value -eq 'S-1-5-18' } | Sort-Object TimeCreated |
    ForEach-Object { '{0:HH:mm:ss.fff} {1} {2}' -f $_.TimeCreated, $_.Id, (($_.Message -split "`n")[0].Trim()) }
