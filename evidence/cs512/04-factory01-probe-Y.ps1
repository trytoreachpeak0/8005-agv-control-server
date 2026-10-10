$ErrorActionPreference = 'Stop'
$dir = 'D:\zhengyushao\control-server-v2-ops\cs512-probe'
$pwsh = 'C:\Program Files\PowerShell\7\pwsh.exe'
function Get-MvpPoint {
    $s = Get-CimInstance Win32_Service -Filter "Name='8005 AGV ControlServer'"
    $l = @(Get-NetTCPConnection -LocalPort 58005, 58007 -State Listen -ErrorAction SilentlyContinue | Sort-Object LocalPort | ForEach-Object { "$($_.LocalPort)=$($_.OwningProcess)" })
    "MVP service pid=$($s.ProcessId) state=$($s.State); listeners: $($l -join ' ')"
}
'=== before'
Get-MvpPoint
Get-ExecutionPolicy -List | Format-Table Scope, ExecutionPolicy
if (@(Get-ScheduledTask -TaskName 'cs512-probe*' -ErrorAction SilentlyContinue).Count) { throw 'cs512-probe tasks already exist' }
if (Test-Path -LiteralPath $dir) { throw "$dir already exists" }
$since = Get-Date
try {
    New-Item -ItemType Directory -Path $dir | Out-Null
    [IO.File]::WriteAllText("$dir\probe.ps1", "[IO.File]::WriteAllText('$dir\probe-ran.txt', [DateTimeOffset]::Now.ToString('o'))`r`nexit 9`r`n", [Text.UTF8Encoding]::new($false))
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    # The installer's settings, minus the restart policy: each task runs exactly once.
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
    $tasks = [ordered]@{
        'cs512-probe-T1-exit7'          = '-NoProfile -Command "exit 7"'
        'cs512-probe-T2-bypass-file-exit9' = "-NoProfile -ExecutionPolicy Bypass -File `"$dir\probe.ps1`""
    }
    foreach ($name in $tasks.Keys) {
        $action = New-ScheduledTaskAction -Execute $pwsh -Argument $tasks[$name] -WorkingDirectory $dir
        $registered = Get-Date
        Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -Description 'cs512 probe, one run' | Out-Null
        Start-ScheduledTask -TaskName $name
        $deadline = (Get-Date).AddSeconds(30)
        do { Start-Sleep -Milliseconds 500; $state = (Get-ScheduledTask -TaskName $name).State } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
        Start-Sleep -Seconds 1
        $info = Get-ScheduledTaskInfo -TaskName $name
        '{0}: registered={1:o} state={2} lastRunTime={3:o} lastTaskResult={4} (0x{4:X8})' -f $name, $registered, $state, $info.LastRunTime, ([uint32] $info.LastTaskResult)
    }
    "probe file: $(Test-Path -LiteralPath "$dir\probe-ran.txt") $((Test-Path -LiteralPath "$dir\probe-ran.txt") ? (Get-Content -LiteralPath "$dir\probe-ran.txt") : '')"
    '=== PowerShellCore/Operational 40961/53504 since the first registration'
    Get-WinEvent -FilterHashtable @{ LogName = 'PowerShellCore/Operational'; Id = 40961, 53504; StartTime = $since } -ErrorAction SilentlyContinue |
        Sort-Object TimeCreated | ForEach-Object { '{0:HH:mm:ss.fff} {1} user={2} {3}' -f $_.TimeCreated, $_.Id, $_.UserId, (($_.Message -split "`n")[0].Trim()) }
} finally {
    '=== cleanup'
    foreach ($name in 'cs512-probe-T1-exit7', 'cs512-probe-T2-bypass-file-exit9') {
        Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    }
    foreach ($p in @(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'pwsh.exe' -and $_.CommandLine -and ($_.CommandLine.Contains('cs512-probe') -or $_.CommandLine.Contains('"exit 7"')) -and $_.CommandLine -notmatch 'EncodedCommand' })) {
        "stopping pid $($p.ProcessId)"; Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    "tasks left: $(@(Get-ScheduledTask -TaskName 'cs512-probe*' -ErrorAction SilentlyContinue).Count); dir left: $(Test-Path -LiteralPath $dir)"
    '=== after'
    Get-MvpPoint
}
