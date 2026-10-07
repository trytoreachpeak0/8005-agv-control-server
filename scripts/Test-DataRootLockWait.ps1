#Requires -Version 7
<#
.SYNOPSIS
Self-check for control-server#503: the installer and the upgrader never copy or delete a data root
while a process still holds a database in it, and never empty a data root they could not back up.
.DESCRIPTION
Runs offline and unelevated against a scratch directory under %TEMP%; it touches no service, no machine
variable, no registry key and no real data root. Four parts:

1. Both scripts parse with zero errors, both carry the same Wait-DataRootReleased, and in each the wait
   comes before every copy and delete of the data root and the install directory.
2. Wait-DataRootReleased itself: no lock file, a held one (and what the refusal tells the operator), one
   let go within the wait, a read-only one.
3. Update-ControlServerLocal.ps1 end to end (U1-U5).
4. Install-ControlServerLocal.ps1 end to end (I1-I4).

Parts 3 and 4 run a scratch copy of the real script in a child pwsh. The copy differs from the source in
exactly two ways, both counted: its one top-level Assert-Administrator call is removed, and (Install only)
[Environment]:: is redirected to a stub class so that the machine-scope credentials it reads exist. The
service control manager, New-Service, sc.exe, Set-Acl and the HTTP checks are replaced by functions, which
win over cmdlets and executables. The registry key Install opens for its new service does not exist, so
Install fails there and runs its own rollback -- which is the path under test. A "holder" is a child pwsh
that opens <db>.instance-lock with FileShare.None, the way ControlServer.Host does since control-server#473;
holders are stopped by PID, after checking the PID still names the process this script started.

Exit code 0 when every check passes, 1 otherwise. About three minutes: four cases wait out the scripts'
real 30-second limit.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$updatePath = Join-Path $repoRoot 'scripts/Update-ControlServerLocal.ps1'
$installPath = Join-Path $repoRoot 'scripts/Install-ControlServerLocal.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("cs503-selftest-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $scratch | Out-Null
$failures = [Collections.Generic.List[string]]::new()
$holders = [Collections.Generic.List[object]]::new()

function Write-Result([bool]$Ok, [string]$Name, [string]$Detail = '') {
    $mark = if ($Ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("[{0}] {1}{2}" -f $mark, $Name, ($Detail ? " -- $Detail" : ''))
    if (-not $Ok) { $failures.Add($Name) }
}

function Get-HolderCommand([string]$LockFile, [int]$Seconds) {
    $escaped = $LockFile.Replace("'", "''")
    return "`$f = [IO.File]::Open('$escaped', 'OpenOrCreate', 'ReadWrite', 'None'); Start-Sleep -Seconds $Seconds; `$f.Dispose()"
}

function Wait-LockHeld([string]$LockFile) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try { [IO.File]::Open($LockFile, 'Open', 'Read', 'None').Dispose() }
        catch [IO.IOException] { return }
        catch { }
        Start-Sleep -Milliseconds 100
    }
    throw "The holder never took $LockFile."
}

function Add-Holder([int]$Id, [long]$StartTicks) {
    $holders.Add([pscustomobject]@{ Id = $Id; StartTicks = $StartTicks })
}

function Start-Holder([string]$LockFile, [int]$Seconds) {
    $process = Start-Process -FilePath 'pwsh' -PassThru -WindowStyle Hidden `
        -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', (Get-HolderCommand $LockFile $Seconds))
    Add-Holder $process.Id $process.StartTime.ToUniversalTime().Ticks
    Wait-LockHeld $LockFile
}

# By PID, and only while that PID is still the pwsh this script started: a holder that has exited may have
# had its PID handed to an unrelated process.
function Stop-Holders {
    foreach ($holder in $holders) {
        $process = Get-Process -Id $holder.Id -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        if ($process.ProcessName -ne 'pwsh' -or $process.StartTime.ToUniversalTime().Ticks -ne $holder.StartTicks) { continue }
        Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue
        $null = $process.WaitForExit(10000)
    }
    $holders.Clear()
}

# A missing file reads as an empty string, so a check against it fails instead of stopping the run.
function Read-Text([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) { return [IO.File]::ReadAllText($Path) }
    return ''
}

function Get-TreeFingerprint([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root)) { return '<absent>' }
    $prefix = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\') + '\'
    $lines = Get-ChildItem -LiteralPath $Root -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
        '{0} {1}' -f $_.FullName.Substring($prefix.Length), (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    return ($lines -join "`n")
}

function New-DataRoot([string]$Root, [bool]$WithLockFile) {
    $data = New-Item -ItemType Directory -Path (Join-Path $Root 'data') -Force
    New-Item -ItemType Directory -Path (Join-Path $Root 'logs') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $data 'controlserver.db'), 'main file')
    [IO.File]::WriteAllText((Join-Path $data 'controlserver.db-wal'), 'committed rows not yet checkpointed')
    [IO.File]::WriteAllText((Join-Path $data 'controlserver.db-shm'), 'shared memory index')
    [IO.File]::WriteAllText((Join-Path $Root 'logs/controlserver-20261007.ndjson'), '{}')
    if ($WithLockFile) { [IO.File]::WriteAllText((Join-Path $data 'controlserver.db.instance-lock'), 'lock') }
    return (Join-Path $data 'controlserver.db.instance-lock')
}

function Test-Order([string]$Name, [string]$Source, [string]$Wait, [string[]]$Acts) {
    $waitAt = $Source.IndexOf($Wait, [StringComparison]::Ordinal)
    $detail = "wait@$waitAt"
    $ok = $waitAt -ge 0
    foreach ($act in $Acts) {
        $actAt = $Source.IndexOf($act, $waitAt -ge 0 ? $waitAt : 0, [StringComparison]::Ordinal)
        $detail += " '$($act.Substring(0, [Math]::Min(40, $act.Length)))'@$actAt"
        $ok = $ok -and $actAt -gt $waitAt
    }
    Write-Result $ok $Name $detail
}

try {
    # --- 1. Parse, one function in two places, and order ------------------------------------------
    $asts = @{}
    foreach ($path in @($updatePath, $installPath)) {
        $tokens = $null; $errors = $null
        $asts[$path] = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
        Write-Result ($errors.Count -eq 0) "ParseFile $(Split-Path -Leaf $path): 0 errors" "errors=$($errors.Count)"
    }
    $functionText = @{}
    foreach ($path in @($updatePath, $installPath)) {
        $found = @($asts[$path].FindAll({ param($n)
                    $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Wait-DataRootReleased' }, $true))
        $functionText[$path] = if ($found.Count -eq 1) { $found[0].Extent.Text } else { $null }
        Write-Result ($found.Count -eq 1) "$(Split-Path -Leaf $path) defines Wait-DataRootReleased once" "found=$($found.Count)"
    }
    Write-Result ($null -ne $functionText[$updatePath] -and $functionText[$updatePath] -ceq $functionText[$installPath]) `
        'the two copies of Wait-DataRootReleased are identical'

    $updateSource = [IO.File]::ReadAllText($updatePath)
    $installSource = [IO.File]::ReadAllText($installPath)
    Test-Order 'Update: wait before the data-root backup' $updateSource "-NotDone '备份数据目录" `
        @('foreach ($item in Get-ChildItem -LiteralPath $dataRoot -Force)')
    Test-Order 'Update: rollback waits before deleting the install directory and the data root' $updateSource "-NotDone '从备份恢复" `
        @('Remove-Item -LiteralPath $installPath -Recurse -Force', 'Remove-Item -LiteralPath $dataRoot -Recurse -Force')
    Test-Order 'Install: rollback waits before deleting the install directory and the data root' $installSource "-NotDone '删除安装目录" `
        @('Remove-Item -LiteralPath $installPath -Recurse -Force', 'Get-ChildItem -LiteralPath $dataRoot -Force | Remove-Item -Recurse -Force')

    # --- 2. The function on its own ---------------------------------------------------------------
    . ([ScriptBlock]::Create($functionText[$updatePath]))

    $root = Join-Path $scratch 'unit-none'; $null = New-DataRoot $root $false
    $started = [Diagnostics.Stopwatch]::StartNew()
    $locks = @(Wait-DataRootReleased -Root $root -NotDone 'x' -NextSteps 'y' -Seconds 5)
    Write-Result ($locks.Count -eq 0 -and $started.Elapsed.TotalSeconds -lt 2) 'no lock file: returns at once' "locks=$($locks.Count) elapsed=$([int]$started.Elapsed.TotalMilliseconds)ms"

    $root = Join-Path $scratch 'unit-held'; $lock = New-DataRoot $root $true
    Start-Holder $lock 60
    $started = [Diagnostics.Stopwatch]::StartNew()
    # A string, never $null: a function that returns instead of refusing must fail these checks, not stop the run.
    $message = ''
    try { $null = Wait-DataRootReleased -Root $root -InstallPath 'C:\nowhere\cs503' -AfterServiceStop -NotDone '备份' -NextSteps 'NEXT-STEPS-MARKER' -Seconds 3 }
    catch { $message = $_.Exception.Message }
    Write-Result ($message -like 'DATA_ROOT_IN_USE:*' -and $message.Contains($lock) -and $started.Elapsed.TotalSeconds -ge 3) `
        'held lock: refuses after the wait with DATA_ROOT_IN_USE and the lock file' "elapsed=$([int]$started.Elapsed.TotalSeconds)s"
    Write-Result ($message.Contains("Where-Object Path -like 'C:\nowhere\cs503\*'") -and $message.Contains('不要按名字结束进程') -and
        $message.Contains('接下来：NEXT-STEPS-MARKER') -and $message.Contains('服务管理器报「已停止」')) `
        'held lock: the refusal finds the Host by install path, warns against stopping by name, and ends with the next steps'
    Stop-Holders

    $root = Join-Path $scratch 'unit-released'; $lock = New-DataRoot $root $true
    Start-Holder $lock 3
    $started = [Diagnostics.Stopwatch]::StartNew()
    $locks = @(Wait-DataRootReleased -Root $root -NotDone 'x' -NextSteps 'y' -Seconds 20)
    Write-Result ($locks.Count -eq 1 -and $started.Elapsed.TotalSeconds -lt 15) 'a holder that lets go within the wait: returns' "elapsed=$([int]$started.Elapsed.TotalSeconds)s"
    Stop-Holders

    $root = Join-Path $scratch 'unit-readonly'; $lock = New-DataRoot $root $true
    Set-ItemProperty -LiteralPath $lock -Name IsReadOnly -Value $true
    $outcome = 'returned'
    try { $null = Wait-DataRootReleased -Root $root -NotDone 'x' -NextSteps 'y' -Seconds 3 } catch { $outcome = $_.Exception.Message }
    Write-Result ($outcome -eq 'returned') 'a read-only lock file nobody holds is not taken for a held one' $outcome
    Set-ItemProperty -LiteralPath $lock -Name IsReadOnly -Value $false

    # --- Scratch copies and the runner -------------------------------------------------------------
    function New-ScriptCopy([string]$Source, [string]$Destination, [bool]$RedirectEnvironment) {
        $lines = [IO.File]::ReadAllLines($Source)
        $calls = @(0..($lines.Count - 1) | Where-Object { $lines[$_] -ceq 'Assert-Administrator' })
        Write-Result ($calls.Count -eq 1) "$(Split-Path -Leaf $Source): exactly one top-level Assert-Administrator call to remove" "found=$($calls.Count)"
        $lines[$calls[0]] = '# Assert-Administrator removed by Test-DataRootLockWait.ps1'
        $text = $lines -join "`r`n"
        if ($RedirectEnvironment) {
            $count = ([regex]::Matches($text, [regex]::Escape('[Environment]::'))).Count
            Write-Result ($count -ge 4) "$(Split-Path -Leaf $Source): [Environment]:: redirected to the stub" "occurrences=$count"
            $text = $text.Replace('[Environment]::', '[Cs503SelftestEnvironment]::')
        }
        [IO.File]::WriteAllText($Destination, $text, [Text.UTF8Encoding]::new($false))
    }
    $updateCopy = Join-Path $scratch 'Update-ControlServerLocal.selftest.ps1'
    $installCopy = Join-Path $scratch 'Install-ControlServerLocal.selftest.ps1'
    New-ScriptCopy $updatePath $updateCopy $false
    New-ScriptCopy $installPath $installCopy $true

    $runner = Join-Path $scratch 'runner.ps1'
    [IO.File]::WriteAllText($runner, @'
#Requires -Version 7
param([string]$Kind, [string]$Script, [string]$Case, [string]$HolderLock, [int]$HolderSeconds, [switch]$HealthFails, [switch]$FailBackupCopy)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @"
public static class Cs503SelftestEnvironment {
    public static string GetEnvironmentVariable(string name, System.EnvironmentVariableTarget target) {
        if (name == "CONTROL_SERVER_RIOT_CALL_API_KEY" && target == System.EnvironmentVariableTarget.User) return "stub-riot";
        if (name == "CONTROL_SERVER_ONBOARD_CREDENTIAL" && target == System.EnvironmentVariableTarget.Machine) return "stub-onboard";
        return null;
    }
    public static void SetEnvironmentVariable(string name, string value, System.EnvironmentVariableTarget target) { }
}
"@
$global:ServiceStatus = if ($Kind -eq 'Update') { 'Running' } else { $null }
function Start-StubHolder {
    if (-not $HolderLock -or $global:HolderStarted) { return }
    $global:HolderStarted = $true
    $escaped = $HolderLock.Replace("'", "''")
    $p = Start-Process pwsh -PassThru -WindowStyle Hidden -ArgumentList @('-NoProfile', '-NonInteractive', '-Command',
        "`$f = [IO.File]::Open('$escaped', 'OpenOrCreate', 'ReadWrite', 'None'); Start-Sleep -Seconds $HolderSeconds; `$f.Dispose()")
    Set-Content -LiteralPath "$Case.holder" -Value ('{0}|{1}' -f $p.Id, $p.StartTime.ToUniversalTime().Ticks)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try { [IO.File]::Open($HolderLock, 'Open', 'Read', 'None').Dispose() } catch [IO.IOException] { break } catch { }
        Start-Sleep -Milliseconds 100
    }
}
function Get-Service {
    param([string]$Name, $ErrorAction)
    if ($null -eq $global:ServiceStatus) { return $null }
    [pscustomobject]@{ Name = $Name; Status = $global:ServiceStatus }
}
function Stop-Service { param([string]$Name, [switch]$Force, $ErrorAction) if ($global:ServiceStatus) { $global:ServiceStatus = 'Stopped' } }
# Stands in for a replacement binary that takes the lock and may outlive its Stop-Service.
function Start-Service { param([string]$Name) $global:ServiceStatus = 'Running'; Start-StubHolder }
function Restart-Service { param([string]$Name, [switch]$Force) $global:ServiceStatus = 'Running' }
# Install registers, then fails at the registry key; a service that took the lock before that is the holder.
function New-Service { param($Name, $BinaryPathName, $DisplayName, $Description, $StartupType) $global:ServiceStatus = 'Stopped'; Start-StubHolder }
function sc.exe { }
function Set-Acl { param([string]$LiteralPath, $AclObject) }
function Copy-Item {
    [CmdletBinding()]
    param([string]$LiteralPath, [string]$Destination, [switch]$Recurse, [switch]$Force)
    if ($FailBackupCopy -and ($Destination -replace '/', '\') -like '*\backups\*\data-root*') { throw 'stub: the data-root backup copy failed' }
    Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
}
function Invoke-WebRequest {
    param([string]$Uri, [switch]$NoProxy, [int]$TimeoutSec, [switch]$UseBasicParsing, [switch]$SkipHttpErrorCheck)
    if ($HealthFails) { throw 'stub: health check refused' }
    [pscustomobject]@{ StatusCode = 200; Content = '{"status":"live","protocolTag":"selftest","protocolCommit":"selftest"}' }
}
$common = @{ PackagePath = "$Case/package"; ResultPath = "$Case/result.json"; DiagnosticPath = "$Case/diagnostic.log"
    ServiceName = 'cs503-selftest'; InstallRoot = "$Case/install"; DataRoot = "$Case/dataroot"; BackupRoot = "$Case/backups" }
try {
    if ($Kind -eq 'Update') { & $Script @common -CertificatePasswordVariable 'CS503_SELFTEST_NO_SUCH_VARIABLE' }
    else { & $Script @common -SkipMachineEnvironmentInjection }
    Set-Content -LiteralPath "$Case/outcome.txt" -Value 'COMPLETED'
}
catch {
    $inner = if ($_.Exception -is [AggregateException]) { ($_.Exception.InnerExceptions | ForEach-Object Message) -join ' || ' } else { '' }
    Set-Content -LiteralPath "$Case/outcome.txt" -Value ("THREW: " + $_.Exception.Message + ' || ' + $inner)
}
'@, [Text.UTF8Encoding]::new($false))

    function New-Case([string]$Name, [bool]$WithLockFile, [bool]$Installed) {
        $case = New-Item -ItemType Directory -Path (Join-Path $scratch $Name)
        $package = New-Item -ItemType Directory -Path (Join-Path $case 'package')
        [IO.File]::WriteAllText((Join-Path $package 'ControlServer.Host.exe'), 'new binary')
        $hash = (Get-FileHash -LiteralPath (Join-Path $package 'ControlServer.Host.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifest = [ordered]@{ schemaVersion = 1; product = '8005 AGV ControlServer'; sourceCommit = 'selftest'
            files = @([ordered]@{ path = 'ControlServer.Host.exe'; sha256 = $hash }) }
        [IO.File]::WriteAllText((Join-Path $package 'deployment-manifest.json'), ($manifest | ConvertTo-Json -Depth 4))
        if ($Installed) {
            $install = New-Item -ItemType Directory -Path (Join-Path $case 'install')
            [IO.File]::WriteAllText((Join-Path $install 'ControlServer.Host.exe'), 'old binary')
            [IO.File]::WriteAllText((Join-Path $install 'appsettings.Production.json'),
                '{ "Health": { "url": "http://127.0.0.1:1" }, "JourneyRuntime": { "enabled": false } }')
        }
        $lock = New-DataRoot (Join-Path $case 'dataroot') $WithLockFile
        return @{ Root = $case.FullName; Lock = $lock; Data = (Join-Path $case.FullName 'dataroot'); Install = (Join-Path $case.FullName 'install') }
    }

    function Invoke-Case([string]$Kind, $Case, [string]$HolderLock = '', [int]$HolderSeconds = 0, [switch]$HealthFails, [switch]$FailBackupCopy) {
        $script = if ($Kind -eq 'Update') { $updateCopy } else { $installCopy }
        $arguments = @('-NoProfile', '-NonInteractive', '-File', $runner, '-Kind', $Kind, '-Script', $script, '-Case', $Case.Root)
        if ($HolderLock) { $arguments += @('-HolderLock', $HolderLock, '-HolderSeconds', $HolderSeconds) }
        if ($HealthFails) { $arguments += '-HealthFails' }
        if ($FailBackupCopy) { $arguments += '-FailBackupCopy' }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        & pwsh @arguments
        $holderFile = "$($Case.Root).holder"
        if (Test-Path -LiteralPath $holderFile) {
            $parts = (Get-Content -Raw -LiteralPath $holderFile).Trim() -split '\|'
            Add-Holder ([int]$parts[0]) ([long]$parts[1])
        }
        $diagnosticPath = Join-Path $Case.Root 'diagnostic.log'
        return [ordered]@{
            Outcome = (Read-Text (Join-Path $Case.Root 'outcome.txt')).Trim()
            Diagnostic = Read-Text $diagnosticPath
            Seconds = [int]$watch.Elapsed.TotalSeconds
        }
    }

    function Test-Pass($Case) {
        $result = Join-Path $Case.Root 'result.json'
        return (Test-Path $result) -and ((Get-Content -Raw $result | ConvertFrom-Json).result -eq 'PASS')
    }

    function Get-BackupFingerprint($Case) {
        $backup = Get-ChildItem -LiteralPath (Join-Path $Case.Root 'backups') -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
        return $backup ? (Get-TreeFingerprint (Join-Path $backup.FullName 'data-root')) : '<no backup>'
    }

    # --- 3. Update ----------------------------------------------------------------------------------
    # U1: the old process still holds the lock after Stop-Service reports Stopped.
    $case = New-Case 'u1-held' $true $true
    $dataBefore = Get-TreeFingerprint $case.Data
    Start-Holder $case.Lock 120
    $result = Invoke-Case 'Update' $case
    Stop-Holders
    $backups = @(Get-ChildItem -LiteralPath (Join-Path $case.Root 'backups') -Recurse -Force -File -ErrorAction SilentlyContinue)
    Write-Result ($result.Outcome -like 'THREW: DATA_ROOT_IN_USE:*') 'U1 held lock: the upgrade stops with DATA_ROOT_IN_USE' $result.Outcome
    Write-Result ($result.Outcome.Contains("Where-Object Path -like '$($case.Install)\*'") -and $result.Outcome.Contains('不要按名字结束进程')) `
        'U1 held lock: the refusal finds the Host by this install path and warns against stopping by name'
    Write-Result ($result.Outcome.Contains("Start-Service 'cs503-selftest'") -and $result.Outcome.Contains('安装目录与数据目录都没动')) `
        'U1 held lock: the refusal says nothing was changed and how to get the service back'
    Write-Result ((Get-TreeFingerprint $case.Data) -eq $dataBefore) 'U1 held lock: the data root is untouched'
    Write-Result ($backups.Count -eq 0 -and $result.Diagnostic -notmatch 'backup-complete' -and $result.Diagnostic -notmatch 'replacement-installed') `
        'U1 held lock: nothing was backed up and the binary was not replaced' "backupFiles=$($backups.Count)"
    Write-Result ((Read-Text (Join-Path $case.Install 'ControlServer.Host.exe')) -eq 'old binary') 'U1 held lock: the old binary is still installed'
    Write-Result ($result.Seconds -ge 30) 'U1 held lock: it waited the full 30 seconds first' "seconds=$($result.Seconds)"

    # U2: the old process lets go a few seconds after Stop-Service.
    $case = New-Case 'u2-released' $true $true
    Start-Holder $case.Lock 4
    $result = Invoke-Case 'Update' $case
    Stop-Holders
    $backupData = Get-ChildItem -LiteralPath (Join-Path $case.Root 'backups') -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
    $backedUp = $backupData ? @(Get-ChildItem -LiteralPath (Join-Path $backupData.FullName 'data-root/data') -Force -File | ForEach-Object Name | Sort-Object) : @()
    Write-Result ($result.Outcome -eq 'COMPLETED' -and (Test-Pass $case)) 'U2 lock let go: the upgrade completes with PASS' $result.Outcome
    Write-Result ($result.Diagnostic -match 'data-root-released lockFiles=1') 'U2 lock let go: the diagnostic records the one lock file it waited on'
    Write-Result (($backedUp -join ',') -eq 'controlserver.db,controlserver.db-shm,controlserver.db-wal,controlserver.db.instance-lock') `
        'U2 lock let go: the backup holds the database with its -wal and -shm' ($backedUp -join ',')

    # U3: an instance installed before control-server#473 has no lock file.
    $case = New-Case 'u3-no-lock-file' $false $true
    $result = Invoke-Case 'Update' $case
    Write-Result ($result.Outcome -eq 'COMPLETED' -and (Test-Pass $case) -and $result.Diagnostic -match 'data-root-released lockFiles=0') `
        'U3 no lock file: the upgrade completes as before' "$($result.Outcome) seconds=$($result.Seconds)"

    # U4: the replacement takes the lock, its health check fails, and it outlives Stop-Service: the rollback must
    # touch neither the install directory nor the data root, and must say how to restore by hand.
    $case = New-Case 'u4-rollback-held' $true $true
    $dataBefore = Get-TreeFingerprint $case.Data
    $result = Invoke-Case 'Update' $case -HolderLock $case.Lock -HolderSeconds 120 -HealthFails
    Stop-Holders
    Write-Result ($result.Outcome -like 'THREW: ControlServer upgrade and rollback both failed.*' -and $result.Outcome -match 'DATA_ROOT_IN_USE:') `
        'U4 rollback under a live holder: refused with DATA_ROOT_IN_USE' $result.Outcome.Substring(0, [Math]::Min(120, $result.Outcome.Length))
    Write-Result ((Get-TreeFingerprint $case.Data) -eq $dataBefore) 'U4 rollback under a live holder: the data root was not deleted'
    Write-Result ((Read-Text (Join-Path $case.Install 'ControlServer.Host.exe')) -eq 'new binary' -and
        (Test-Path (Join-Path $case.Install 'appsettings.Production.json'))) `
        'U4 rollback under a live holder: the install directory is intact (the replacement, whole)'
    Write-Result ((Get-BackupFingerprint $case) -eq $dataBefore) 'U4 rollback under a live holder: the backup is intact for a manual restore'
    $backupDirectory = "$((Get-ChildItem -LiteralPath (Join-Path $case.Root 'backups') -Directory -ErrorAction SilentlyContinue | Select-Object -First 1).FullName)"
    if ($backupDirectory -eq '') { $backupDirectory = '<no backup directory>' }
    Write-Result ($result.Outcome.Contains('共四步') -and $result.Outcome.Contains("$backupDirectory\install") -and
        $result.Outcome.Contains("$backupDirectory\data-root") -and $result.Outcome.Contains('不用做') -and
        $result.Outcome.Contains("Start-Service 'cs503-selftest'") -and $result.Outcome.Contains('不要用重新执行本脚本代替恢复')) `
        'U4 rollback under a live holder: the refusal gives the four restore steps with this run''s backup paths'

    # U5: the same, but the replacement exits a few seconds after Stop-Service: the rollback goes ahead.
    $case = New-Case 'u5-rollback-released' $true $true
    $dataBefore = Get-TreeFingerprint $case.Data
    $result = Invoke-Case 'Update' $case -HolderLock $case.Lock -HolderSeconds 4 -HealthFails
    Stop-Holders
    Write-Result ($result.Outcome -like 'THREW: HTTP GET of *' -and $result.Diagnostic -match 'rollback-complete') `
        'U5 rollback after the holder exits: the rollback completes and the original error is rethrown' $result.Outcome
    Write-Result ((Get-TreeFingerprint $case.Data) -eq $dataBefore) 'U5 rollback after the holder exits: the data root is restored from the backup'
    Write-Result ((Read-Text (Join-Path $case.Install 'ControlServer.Host.exe')) -eq 'old binary') 'U5 rollback after the holder exits: the old binary is back'

    # --- 4. Install ---------------------------------------------------------------------------------
    # I1: a Host started by hand holds the existing data root: the preflight refuses before doing anything.
    $case = New-Case 'i1-preflight-held' $true $false
    $dataBefore = Get-TreeFingerprint $case.Data
    Start-Holder $case.Lock 120
    $result = Invoke-Case 'Install' $case
    Stop-Holders
    Write-Result ($result.Outcome -like 'THREW: DATA_ROOT_IN_USE:*' -and $result.Seconds -ge 30) 'I1 preflight, held lock: refused with DATA_ROOT_IN_USE after the wait' "seconds=$($result.Seconds)"
    Write-Result (-not $result.Outcome.Contains('服务管理器') -and -not $result.Outcome.Contains('停服务后') -and
        $result.Outcome.Contains('不要按名字结束进程') -and $result.Outcome.Contains('本脚本还什么都没有做')) `
        'I1 preflight, held lock: the refusal does not speak of a stopped service and says nothing was done'
    Write-Result ((Get-TreeFingerprint $case.Data) -eq $dataBefore -and -not (Test-Path $case.Install) -and
        -not (Test-Path (Join-Path $case.Root 'backups')) -and $result.Diagnostic -notmatch 'preflight-complete') `
        'I1 preflight, held lock: no install directory, no backup, data root untouched'

    # I2: the existing data root's backup fails part-way: the rollback must not empty the only copy.
    $case = New-Case 'i2-backup-incomplete' $true $false
    $dataBefore = Get-TreeFingerprint $case.Data
    $result = Invoke-Case 'Install' $case -FailBackupCopy
    Write-Result ($result.Outcome -match 'DATA_ROOT_BACKUP_INCOMPLETE') 'I2 backup incomplete: the rollback reports DATA_ROOT_BACKUP_INCOMPLETE' $result.Outcome.Substring(0, [Math]::Min(160, $result.Outcome.Length))
    Write-Result ((Get-TreeFingerprint $case.Data) -eq $dataBefore) 'I2 backup incomplete: the existing data root is neither emptied nor deleted'

    # I3: a failure after a complete backup, nobody holding the lock: the data root comes back from the backup.
    $case = New-Case 'i3-rollback-released' $true $false
    $dataBefore = Get-TreeFingerprint $case.Data
    $result = Invoke-Case 'Install' $case
    Write-Result ($result.Outcome -like 'THREW: The new service registry key cannot be opened.*') 'I3 rollback: the original error is rethrown' $result.Outcome
    Write-Result ((Get-TreeFingerprint $case.Data) -eq $dataBefore -and -not (Test-Path $case.Install)) `
        'I3 rollback: the data root is restored from the backup and the install directory is removed'

    # I4: the service took the lock before the failure and outlives Stop-Service: the rollback deletes nothing.
    $case = New-Case 'i4-rollback-held' $true $false
    $dataBefore = Get-TreeFingerprint $case.Data
    $result = Invoke-Case 'Install' $case -HolderLock $case.Lock -HolderSeconds 120
    Stop-Holders
    Write-Result ($result.Outcome -like 'THREW: Deployment failed and rollback reported: *' -and $result.Outcome -match 'DATA_ROOT_IN_USE:') `
        'I4 rollback under a live holder: refused with DATA_ROOT_IN_USE'
    Write-Result ((Get-TreeFingerprint $case.Data) -eq $dataBefore) 'I4 rollback under a live holder: the data root was not touched'
    Write-Result ((Read-Text (Join-Path $case.Install 'ControlServer.Host.exe')) -eq 'new binary') `
        'I4 rollback under a live holder: the install directory was not deleted'
    Write-Result ($result.Outcome.Contains("删除 $($case.Install)") -and $result.Outcome.Contains('data-root') -and
        $result.Outcome.Contains('不要按名字结束进程') -and -not $result.Outcome.Contains('不要动')) `
        'I4 rollback under a live holder: the refusal says how to finish the rollback by hand'
}
catch {
    # A check that throws is a failure of this run, reported as one; the exit code below then says so.
    Write-Result $false "the self-test itself stopped: $($_.Exception.Message)" "line $($_.InvocationInfo.ScriptLineNumber)"
}
finally {
    Stop-Holders
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Host "FAILED: $($failures.Count) check(s): $($failures -join '; ')"
    exit 1
}
Write-Host 'All checks passed.'
exit 0
