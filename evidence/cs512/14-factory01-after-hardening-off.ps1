$ErrorActionPreference = 'Stop'
$task = '8005 AGV ControlServer V2 FakeMesIngest'
$fakeRoot = 'C:\Program Files\8005 AGV\ControlServer.V2.FakeMesIngest'
$logDir = 'D:\zhengyushao\control-server-v2-ops\logs'
$mvp = { $s = Get-CimInstance Win32_Service -Filter "Name='8005 AGV ControlServer'"; $l = @(Get-NetTCPConnection -LocalPort 58005, 58007 -State Listen -ErrorAction SilentlyContinue | Sort-Object LocalPort | ForEach-Object { "$($_.LocalPort)=$($_.OwningProcess)" }); "MVP service pid=$($s.ProcessId) state=$($s.State); listeners: $($l -join ' ')" }
$mine = { @(Get-CimInstance Win32_Process | Where-Object { ($_.ExecutablePath -and $_.ExecutablePath.StartsWith("$fakeRoot\", [StringComparison]::OrdinalIgnoreCase)) -or ($_.Name -eq 'pwsh.exe' -and $_.CommandLine -and $_.CommandLine.Contains('ControlServer.V2.FakeMesIngest') -and $_.CommandLine -notmatch 'EncodedCommand') }) }
Write-Host '=== before'
Write-Host (& $mvp)
$v2 = Get-Content -LiteralPath 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json' -Raw | ConvertFrom-Json -AsHashtable
Write-Host "V2 JourneyRuntime.enabled=$($v2['JourneyRuntime']['enabled'])"
$t = Get-ScheduledTask -TaskName $task
$original = $t.Actions[0]
Write-Host "task as found: state=$($t.State) Execute=$($original.Execute)"
Write-Host "log dir exists before: $(Test-Path -LiteralPath $logDir); 58188 listeners before: $(@(Get-NetTCPConnection -LocalPort 58188 -State Listen -ErrorAction SilentlyContinue).Count); my processes before: $(@(& $mine).Count)"
if ($original.Execute -ne 'pwsh.exe') { throw "Execute is '$($original.Execute)', not the 10-07 'pwsh.exe'; stopping" }
if ($t.State -eq 'Running' -or @(& $mine).Count) { throw 'the task or its processes are already running' }
if (@(Get-NetTCPConnection -LocalPort 58188 -State Listen -ErrorAction SilentlyContinue).Count) { throw '58188 is already listened on' }
try {
    Write-Host '=== T: the 10-07 task exactly as it is, after Huorong system hardening was turned off'
    $since = Get-Date
    $null = Start-ScheduledTask -TaskName $task
    $health = 'NONE'
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        try { $r = Invoke-WebRequest -Uri 'http://127.0.0.1:58188/control/v1/health' -NoProxy -TimeoutSec 5 -UseBasicParsing; if ($r.StatusCode -eq 200) { $health = $r.Content; break } } catch { Start-Sleep -Milliseconds 500 }
    }
    Start-Sleep -Seconds 5
    $info = Get-ScheduledTaskInfo -TaskName $task
    Write-Host ("T started={0:o} health={1}" -f $since, $health)
    Write-Host ("T task state={0} lastTaskResult=0x{1:X8}" -f (Get-ScheduledTask -TaskName $task).State, ([uint32] $info.LastTaskResult))
    foreach ($e in @(Get-WinEvent -FilterHashtable @{ LogName = 'PowerShellCore/Operational'; Id = 40961, 53504; StartTime = $since } -ErrorAction SilentlyContinue | Where-Object { $_.UserId -and $_.UserId.Value -eq 'S-1-5-18' } | Sort-Object TimeCreated)) {
        Write-Host ("T {0:HH:mm:ss.fff} {1} {2}" -f $e.TimeCreated, $e.Id, (($e.Message -split "`n")[0].Trim()))
    }
    Write-Host "T log dir exists: $(Test-Path -LiteralPath $logDir)"
    foreach ($f in @(Get-ChildItem -LiteralPath $logDir -File -ErrorAction SilentlyContinue)) { Write-Host "T --- $($f.Name)"; Get-Content -LiteralPath $f.FullName -Tail 15 | ForEach-Object { Write-Host "  $_" } }
    Write-Host "T processes: $((& $mine | ForEach-Object { "$($_.ProcessId)=$($_.Name)" }) -join ' ')"
} finally {
    Write-Host '=== cleanup'
    Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    foreach ($p in @(& $mine)) { Write-Host "stopping pid $($p.ProcessId) $($p.Name)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 70
    foreach ($p in @(& $mine)) { Write-Host "restarted, stopping pid $($p.ProcessId) $($p.Name)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    $final = Get-ScheduledTask -TaskName $task
    Write-Host "task after: state=$($final.State) Execute=$($final.Actions[0].Execute) Arguments-unchanged=$($final.Actions[0].Arguments -ceq $original.Arguments)"
    Write-Host "58188 listeners after: $(@(Get-NetTCPConnection -LocalPort 58188 -State Listen -ErrorAction SilentlyContinue).Count); my processes after: $(@(& $mine).Count)"
    Write-Host '=== after'
    Write-Host (& $mvp)
}
