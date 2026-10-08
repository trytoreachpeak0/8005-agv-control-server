$ErrorActionPreference = 'Stop'
$probeDir = 'D:\zhengyushao\control-server-v2-ops\cs512-probe'
$module = "$probeDir\ParallelHost.psm1"
$expectedModuleHash = 'DA2313F5745CE407457A58C285FD3D419948D1AC2C7761ED9C5ABC3BF5BCB710'   # 702a25d7e:scripts/parallel/ParallelHost.psm1
$realTask = '8005 AGV ControlServer V2 FakeMesIngest'
$fakeRoot = 'C:\Program Files\8005 AGV\ControlServer.V2.FakeMesIngest'
$fakeExe = "$fakeRoot\ControlServer.FakeMesIngest.exe"
$abs = 'C:\Program Files\PowerShell\7\pwsh.exe'
$r3 = 'cs512-probe-R3'; $r4 = 'cs512-probe-R4'
$r3Port = 58188; $r4Port = 47188
$mvp = { $s = Get-CimInstance Win32_Service -Filter "Name='8005 AGV ControlServer'"; $l = @(Get-NetTCPConnection -LocalPort 58005, 58007 -State Listen -ErrorAction SilentlyContinue | Sort-Object LocalPort | ForEach-Object { "$($_.LocalPort)=$($_.OwningProcess)" }); "MVP service pid=$($s.ProcessId) state=$($s.State); listeners: $($l -join ' ')" }
$mine = { @(Get-CimInstance Win32_Process | Where-Object { ($_.ExecutablePath -and $_.ExecutablePath.StartsWith("$fakeRoot\", [StringComparison]::OrdinalIgnoreCase)) -or ($_.Name -eq 'pwsh.exe' -and $_.CommandLine -and $_.CommandLine.Contains('ControlServer.V2.FakeMesIngest') -and $_.CommandLine -notmatch 'EncodedCommand') }) }
$listeners = { param($p) @(Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue).Count }

Write-Host '=== before'
Write-Host (& $mvp)
$v2 = Get-Content -LiteralPath 'C:\Program Files\8005 AGV\ControlServer.V2\appsettings.Production.json' -Raw | ConvertFrom-Json -AsHashtable
Write-Host "V2 JourneyRuntime.enabled=$($v2['JourneyRuntime']['enabled'])"
$real = Get-ScheduledTask -TaskName $realTask
$realAction = $real.Actions[0]
Write-Host "real task: state=$($real.State) Execute=$($realAction.Execute)"
Write-Host "listeners before: $r3Port=$(& $listeners $r3Port) $r4Port=$(& $listeners $r4Port); my processes before: $(@(& $mine).Count)"
if (@(Get-ScheduledTask -TaskName 'cs512-probe*' -ErrorAction SilentlyContinue).Count) { throw 'cs512-probe tasks already exist' }
if ($real.State -eq 'Running' -or @(& $mine).Count) { throw 'the real task or its processes are running' }
if ((& $listeners $r3Port) -or (& $listeners $r4Port)) { throw 'a probe port is in use' }
$hash = (Get-FileHash -LiteralPath $module -Algorithm SHA256).Hash
Write-Host "module hash: $hash (expected $expectedModuleHash)"
if ($hash -ne $expectedModuleHash) { throw 'the staged ParallelHost.psm1 is not the reviewed one' }
Import-Module $module -Force

try {
    # ------------------------------------------------------------------ R3 ---
    Write-Host '=== R3: the real task''s pwsh action (absolute pwsh), Y''s settings: no trigger, no restart policy'
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $ySettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
    $null = Register-ScheduledTask -TaskName $r3 -Principal $principal -Settings $ySettings -Description 'cs512 probe R3, one run' `
        -Action (New-ScheduledTaskAction -Execute $abs -Argument $realAction.Arguments -WorkingDirectory $realAction.WorkingDirectory)
    $since3 = Get-Date
    $null = Start-ScheduledTask -TaskName $r3
    $health3 = 'NONE'
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        try { $r = Invoke-WebRequest -Uri "http://127.0.0.1:$r3Port/control/v1/health" -NoProxy -TimeoutSec 5 -UseBasicParsing; if ($r.StatusCode -eq 200) { $health3 = $r.Content; break } } catch { Start-Sleep -Milliseconds 500 }
    }
    Start-Sleep -Seconds 3
    $info3 = Get-ScheduledTaskInfo -TaskName $r3
    Write-Host ("R3 started={0:o} health={1}" -f $since3, $health3)
    Write-Host ("R3 task state={0} lastTaskResult=0x{1:X8}" -f (Get-ScheduledTask -TaskName $r3).State, ([uint32] $info3.LastTaskResult))
    foreach ($e in @(Get-WinEvent -FilterHashtable @{ LogName = 'PowerShellCore/Operational'; Id = 40961, 53504; StartTime = $since3 } -ErrorAction SilentlyContinue | Where-Object { $_.UserId -and $_.UserId.Value -eq 'S-1-5-18' } | Sort-Object TimeCreated)) {
        Write-Host ("R3 {0:HH:mm:ss.fff} {1} {2}" -f $e.TimeCreated, $e.Id, (($e.Message -split "`n")[0].Trim()))
    }
    Write-Host "R3 processes: $((& $mine | ForEach-Object { "$($_.ProcessId)=$($_.Name)" }) -join ' ')"
    Stop-ScheduledTask -TaskName $r3 -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    foreach ($p in @(& $mine)) { Write-Host "R3 stopping pid $($p.ProcessId) $($p.Name)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Unregister-ScheduledTask -TaskName $r3 -Confirm:$false
    Start-Sleep -Seconds 2

    # ------------------------------------------------------------------ R4 ---
    Write-Host '=== R4: the final form from the shared functions (702a25d7e), the installer''s settings, then the installer''s seeding'
    $seedPath = "$probeDir\fake-mes-ingest-seed.json"
    [IO.File]::WriteAllText($seedPath, '{ "demands": [] }', [Text.UTF8Encoding]::new($false))
    $logPath = "$probeDir\logs\fake-mes-ingest.log"
    $action = Get-ParallelFakeMesIngestTaskAction -ExecutablePath $fakeExe -Port $r4Port -WorkingDirectory $fakeRoot
    Write-Host "R4 action: Execute=$($action.Execute) Argument=$($action.Argument) WorkingDirectory=$($action.WorkingDirectory)"
    $since4 = Register-ParallelFakeMesIngestTask -TaskName $r4 -Action $action -LogPath $logPath -Description 'cs512 probe R4, one run'
    $t4 = Get-ScheduledTask -TaskName $r4
    Write-Host "R4 registered: triggers=$(@($t4.Triggers | ForEach-Object { $_.CimClass.CimClassName }) -join ',') restartCount=$($t4.Settings.RestartCount) restartInterval=$($t4.Settings.RestartInterval) user=$($t4.Principal.UserId)"
    $health4 = 'NONE'
    try { $health4 = Wait-ParallelFakeMesIngestTask -TaskName $r4 -Port $r4Port -ExecutablePath $fakeExe -Since $since4 -TimeoutSeconds 120 }
    catch { Write-Host "R4 wait failed: $($_.Exception.Message)" }
    Write-Host "R4 health=$health4"
    $procs = @(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $fakeExe, [StringComparison]::OrdinalIgnoreCase) })
    foreach ($p in $procs) { $o = Invoke-CimMethod -InputObject $p -MethodName GetOwner; Write-Host "R4 process pid=$($p.ProcessId) owner=$($o.Domain)\$($o.User)" }
    Write-Host "R4 task state=$((Get-ScheduledTask -TaskName $r4).State)"
    if ($health4 -ne 'NONE') {
        $seeded = 'NONE'
        try { $seeded = Invoke-ParallelFakeMesIngestSeed -Port $r4Port -SeedPath $seedPath -LogPath $logPath }
        catch { Write-Host "R4 seeding failed: $($_.Exception.Message)" }
        Write-Host "R4 seeded=$seeded"
        Write-Host "R4 double still running after seeding: $(@(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and [string]::Equals($_.ExecutablePath, $fakeExe, [StringComparison]::OrdinalIgnoreCase) }).Count)"
    }
} finally {
    Write-Host '=== cleanup'
    foreach ($name in $r3, $r4) {
        Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
    foreach ($p in @(& $mine)) { Write-Host "stopping pid $($p.ProcessId) $($p.Name)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 70
    foreach ($p in @(& $mine)) { Write-Host "restarted, stopping pid $($p.ProcessId) $($p.Name)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $probeDir -Recurse -Force -ErrorAction SilentlyContinue
    $realAfter = Get-ScheduledTask -TaskName $realTask
    Write-Host "probe tasks left: $(@(Get-ScheduledTask -TaskName 'cs512-probe*' -ErrorAction SilentlyContinue).Count); probe dir left: $(Test-Path -LiteralPath $probeDir)"
    Write-Host "real task after: state=$($realAfter.State) Execute=$($realAfter.Actions[0].Execute) Arguments-unchanged=$($realAfter.Actions[0].Arguments -ceq $realAction.Arguments)"
    Write-Host "listeners after: $r3Port=$(& $listeners $r3Port) $r4Port=$(& $listeners $r4Port); my processes after: $(@(& $mine).Count)"
    Write-Host '=== after'
    Write-Host (& $mvp)
}
