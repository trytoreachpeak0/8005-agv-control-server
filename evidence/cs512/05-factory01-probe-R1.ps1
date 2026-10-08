$ErrorActionPreference = 'Stop'
$task = '8005 AGV ControlServer V2 FakeMesIngest'
$fakeRoot = 'C:\Program Files\8005 AGV\ControlServer.V2.FakeMesIngest'
$logDir = 'D:\zhengyushao\control-server-v2-ops\logs'
$abs = 'C:\Program Files\PowerShell\7\pwsh.exe'
function Get-MvpPoint {
    $s = Get-CimInstance Win32_Service -Filter "Name='8005 AGV ControlServer'"
    $l = @(Get-NetTCPConnection -LocalPort 58005, 58007 -State Listen -ErrorAction SilentlyContinue | Sort-Object LocalPort | ForEach-Object { "$($_.LocalPort)=$($_.OwningProcess)" })
    "MVP service pid=$($s.ProcessId) state=$($s.State); listeners: $($l -join ' ')"
}
function Get-Mine {
    @(Get-CimInstance Win32_Process | Where-Object {
            ($_.ExecutablePath -and $_.ExecutablePath.StartsWith("$fakeRoot\", [StringComparison]::OrdinalIgnoreCase)) -or
            ($_.Name -eq 'pwsh.exe' -and $_.CommandLine -and $_.CommandLine.Contains('ControlServer.V2.FakeMesIngest') -and $_.CommandLine -notmatch 'EncodedCommand') })
}
function Invoke-Round([string] $Label) {
    $since = Get-Date
    Start-ScheduledTask -TaskName $task
    $health = $null
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        try { $r = Invoke-WebRequest -Uri 'http://127.0.0.1:58188/control/v1/health' -NoProxy -TimeoutSec 5 -UseBasicParsing; if ($r.StatusCode -eq 200) { $health = $r.Content; break } } catch { Start-Sleep -Milliseconds 500 }
    }
    Start-Sleep -Seconds 3
    $info = Get-ScheduledTaskInfo -TaskName $task
    "$Label started={0:o} health={1}" -f $since, ($health ?? 'NONE')
    "$Label task state=$((Get-ScheduledTask -TaskName $task).State) lastTaskResult=0x{0:X8}" -f ([uint32] $info.LastTaskResult)
    "$Label log dir exists: $(Test-Path -LiteralPath $logDir)"
    Get-ChildItem -LiteralPath $logDir -File -ErrorAction SilentlyContinue | ForEach-Object { "$Label --- $($_.Name)"; Get-Content -LiteralPath $_.FullName -Tail 15 }
    Get-WinEvent -FilterHashtable @{ LogName = 'PowerShellCore/Operational'; Id = 40961, 53504; StartTime = $since } -ErrorAction SilentlyContinue |
        Where-Object { $_.UserId -and $_.UserId.Value -eq 'S-1-5-18' } | Sort-Object TimeCreated |
        ForEach-Object { "$Label {0:HH:mm:ss.fff} {1} {2}" -f $_.TimeCreated, $_.Id, (($_.Message -split "`n")[0].Trim()) }
    "$Label processes: $((Get-Mine | ForEach-Object { "$($_.ProcessId)=$($_.Name)" }) -join ' ')"
    Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    foreach ($p in Get-Mine) { "$Label stopping pid $($p.ProcessId) $($p.Name)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    return [bool] $health
}
'=== before'
Get-MvpPoint
$v2 = Get-Content -LiteralPath 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json' -Raw | ConvertFrom-Json -AsHashtable
"V2 JourneyRuntime.enabled=$($v2['JourneyRuntime']['enabled'])"
$t = Get-ScheduledTask -TaskName $task
$original = $t.Actions[0]
"task as found: state=$($t.State) Execute=$($original.Execute)"
"log dir exists before: $(Test-Path -LiteralPath $logDir); 58188 listeners before: $(@(Get-NetTCPConnection -LocalPort 58188 -State Listen -ErrorAction SilentlyContinue).Count); my processes before: $(@(Get-Mine).Count)"
if ($t.State -eq 'Running' -or @(Get-Mine).Count) { throw 'the task or its processes are already running' }
try {
    '=== R1: the task exactly as the 10-07 install left it'
    $r1 = Invoke-Round 'R1'
    if (-not $r1) {
        '=== R2: Execute by absolute path, nothing else changed'
        Set-ScheduledTask -TaskName $task -Action (New-ScheduledTaskAction -Execute $abs -Argument $original.Arguments -WorkingDirectory $original.WorkingDirectory) | Out-Null
        "R2 Execute now: $((Get-ScheduledTask -TaskName $task).Actions[0].Execute)"
        $null = Invoke-Round 'R2'
    }
} finally {
    '=== cleanup'
    Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
    if ((Get-ScheduledTask -TaskName $task).Actions[0].Execute -ne $original.Execute) {
        Set-ScheduledTask -TaskName $task -Action (New-ScheduledTaskAction -Execute $original.Execute -Argument $original.Arguments -WorkingDirectory $original.WorkingDirectory) | Out-Null
    }
    foreach ($p in Get-Mine) { "stopping pid $($p.ProcessId) $($p.Name)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    # The restart policy (3 x 1 min) must not bring it back after the stop: look again past one interval.
    Start-Sleep -Seconds 70
    $back = @(Get-Mine)
    foreach ($p in $back) { "restarted, stopping pid $($p.ProcessId) $($p.Name)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    $final = Get-ScheduledTask -TaskName $task
    "task after: state=$($final.State) Execute=$($final.Actions[0].Execute) Arguments-unchanged=$($final.Actions[0].Arguments -ceq $original.Arguments)"
    "58188 listeners after: $(@(Get-NetTCPConnection -LocalPort 58188 -State Listen -ErrorAction SilentlyContinue).Count); my processes after: $(@(Get-Mine).Count)"
    '=== after'
    Get-MvpPoint
}
