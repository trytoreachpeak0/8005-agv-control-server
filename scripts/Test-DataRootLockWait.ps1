#Requires -Version 7
<#
.SYNOPSIS
Self-check for control-server#503: the installer and the upgrader never copy or delete a data root
while a process still holds a database in it.
.DESCRIPTION
Runs offline, unelevated, against a scratch directory under %TEMP%; it touches no service, no machine
variable and no real data root. Three parts:

1. Both scripts parse with zero errors, and both carry the same Wait-DataRootReleased.
2. Wait-DataRootReleased itself: no lock file, a held one, one let go within the wait, a read-only one.
3. Update-ControlServerLocal.ps1 run end to end in a child pwsh against a scratch install, with the
   service control manager, Set-Acl and the HTTP checks replaced by stubs (functions win over cmdlets),
   and its one Assert-Administrator call removed from a scratch copy. A "holder" is a child pwsh that
   opens <db>.instance-lock with FileShare.None, the way ControlServer.Host does since control-server#473.

Install-ControlServerLocal.ps1 cannot be driven the same way unelevated (its preflight reads a
machine-scope credential and its install path registers a service), so part 3 covers it only by the
shared function and by checking that the wait comes before every data-root copy and delete.

Exit code 0 when every check passes, 1 otherwise. Takes about two minutes: two cases wait out the
upgrader's real 30-second limit.
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
$holders = [Collections.Generic.List[Diagnostics.Process]]::new()

function Write-Result([bool]$Ok, [string]$Name, [string]$Detail = '') {
    $mark = if ($Ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("[{0}] {1}{2}" -f $mark, $Name, ($Detail ? " -- $Detail" : ''))
    if (-not $Ok) { $failures.Add($Name) }
}

function Start-Holder([string]$LockFile, [int]$Seconds) {
    $escaped = $LockFile.Replace("'", "''")
    $command = "`$f = [IO.File]::Open('$escaped', 'OpenOrCreate', 'ReadWrite', 'None'); Start-Sleep -Seconds $Seconds; `$f.Dispose()"
    $process = Start-Process -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-NonInteractive', '-Command', $command) `
        -PassThru -WindowStyle Hidden
    $holders.Add($process)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try { [IO.File]::Open($LockFile, 'Open', 'Read', 'None').Dispose() }
        catch [IO.IOException] { return $process }
        catch { }
        Start-Sleep -Milliseconds 100
    }
    throw "The holder never took $LockFile."
}

function Stop-Holders {
    foreach ($holder in $holders) {
        if (-not $holder.HasExited) { Stop-Process -Id $holder.Id -Force -ErrorAction SilentlyContinue }
        $null = $holder.WaitForExit(10000)
    }
    $holders.Clear()
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

try {
    # --- 1. Parse, and one function in two places -------------------------------------------------
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

    # The wait comes before every data-root copy and delete, in both scripts.
    $updateSource = [IO.File]::ReadAllText($updatePath)
    $installSource = [IO.File]::ReadAllText($installPath)
    $orderChecks = @(
        @{ Name = 'Update: wait before the data-root backup'; Source = $updateSource
           Wait = "Wait-DataRootReleased `$dataRoot '备份数据目录"; Act = 'foreach ($item in Get-ChildItem -LiteralPath $dataRoot -Force)' },
        @{ Name = 'Update: wait before the rollback deletes the data root'; Source = $updateSource
           Wait = 'Wait-DataRootReleased $dataRoot "从备份'; Act = 'Remove-Item -LiteralPath $dataRoot -Recurse -Force' },
        @{ Name = 'Install: wait before the existing data root is backed up'; Source = $installSource
           Wait = "Wait-DataRootReleased `$dataRoot '备份既有数据目录"; Act = '$existingData = Get-ChildItem -LiteralPath $dataRoot -Force' },
        @{ Name = 'Install: wait before the rollback deletes the data root'; Source = $installSource
           Wait = 'Wait-DataRootReleased $dataRoot "回滚数据目录'; Act = 'Get-ChildItem -LiteralPath $dataRoot -Force | Remove-Item -Recurse -Force' }
    )
    foreach ($check in $orderChecks) {
        $waitAt = $check.Source.IndexOf($check.Wait, [StringComparison]::Ordinal)
        $actAt = $check.Source.IndexOf($check.Act, [StringComparison]::Ordinal)
        $actCount = ([regex]::Matches($check.Source, [regex]::Escape($check.Act))).Count
        Write-Result ($waitAt -ge 0 -and $actAt -gt $waitAt -and $actCount -eq 1) $check.Name "wait@$waitAt act@$actAt actCount=$actCount"
    }

    # --- 2. The function on its own ---------------------------------------------------------------
    . ([ScriptBlock]::Create($functionText[$updatePath]))

    $root = Join-Path $scratch 'unit-none'; $null = New-DataRoot $root $false
    $started = [Diagnostics.Stopwatch]::StartNew()
    $locks = @(Wait-DataRootReleased $root 'x' 5)
    Write-Result ($locks.Count -eq 0 -and $started.Elapsed.TotalSeconds -lt 2) 'no lock file: returns at once' "locks=$($locks.Count) elapsed=$([int]$started.Elapsed.TotalMilliseconds)ms"

    $root = Join-Path $scratch 'unit-held'; $lock = New-DataRoot $root $true
    $null = Start-Holder $lock 60
    $started = [Diagnostics.Stopwatch]::StartNew()
    $message = $null
    try { $null = Wait-DataRootReleased $root '备份' 3 } catch { $message = $_.Exception.Message }
    Write-Result ($message -like 'DATA_ROOT_IN_USE:*' -and $message.Contains($lock) -and $started.Elapsed.TotalSeconds -ge 3) `
        'held lock: refuses after the wait with DATA_ROOT_IN_USE and the lock file' "elapsed=$([int]$started.Elapsed.TotalSeconds)s message=$message"
    Stop-Holders

    $root = Join-Path $scratch 'unit-released'; $lock = New-DataRoot $root $true
    $null = Start-Holder $lock 3
    $started = [Diagnostics.Stopwatch]::StartNew()
    $locks = @(Wait-DataRootReleased $root 'x' 20)
    Write-Result ($locks.Count -eq 1 -and $started.Elapsed.TotalSeconds -lt 15) 'a holder that lets go within the wait: returns' "elapsed=$([int]$started.Elapsed.TotalSeconds)s"
    Stop-Holders

    $root = Join-Path $scratch 'unit-readonly'; $lock = New-DataRoot $root $true
    Set-ItemProperty -LiteralPath $lock -Name IsReadOnly -Value $true
    $outcome = 'returned'
    try { $null = Wait-DataRootReleased $root 'x' 3 } catch { $outcome = $_.Exception.Message }
    Write-Result ($outcome -eq 'returned') 'a read-only lock file nobody holds is not taken for a held one' $outcome
    Set-ItemProperty -LiteralPath $lock -Name IsReadOnly -Value $false

    # --- 3. The upgrader end to end ---------------------------------------------------------------
    $scriptCopy = Join-Path $scratch 'Update-ControlServerLocal.selftest.ps1'
    $lines = [IO.File]::ReadAllLines($updatePath)
    $callLines = @(0..($lines.Count - 1) | Where-Object { $lines[$_] -ceq 'Assert-Administrator' })
    Write-Result ($callLines.Count -eq 1) 'Update: exactly one top-level Assert-Administrator call to remove' "found=$($callLines.Count)"
    $lines[$callLines[0]] = '# Assert-Administrator removed by Test-DataRootLockWait.ps1'
    [IO.File]::WriteAllLines($scriptCopy, $lines, [Text.UTF8Encoding]::new($false))

    $runner = Join-Path $scratch 'runner.ps1'
    [IO.File]::WriteAllText($runner, @'
#Requires -Version 7
param([string]$Script, [string]$Case, [string]$HolderLock, [int]$HolderSeconds, [switch]$HealthFails)
$ErrorActionPreference = 'Stop'
$global:ServiceStatus = 'Running'
function Get-Service { param([string]$Name, $ErrorAction) [pscustomobject]@{ Name = $Name; Status = $global:ServiceStatus } }
function Stop-Service { param([string]$Name, [switch]$Force, $ErrorAction) $global:ServiceStatus = 'Stopped' }
function Start-Service {
    param([string]$Name)
    $global:ServiceStatus = 'Running'
    # Stands in for a replacement binary that takes the lock and may outlive its Stop-Service.
    if ($HolderLock -and -not $global:HolderStarted) {
        $global:HolderStarted = $true
        $escaped = $HolderLock.Replace("'", "''")
        $p = Start-Process pwsh -ArgumentList @('-NoProfile', '-NonInteractive', '-Command',
            "`$f = [IO.File]::Open('$escaped', 'OpenOrCreate', 'ReadWrite', 'None'); Start-Sleep -Seconds $HolderSeconds; `$f.Dispose()") -PassThru -WindowStyle Hidden
        Set-Content -LiteralPath "$Case.holder-pid" -Value $p.Id
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            try { [IO.File]::Open($HolderLock, 'Open', 'Read', 'None').Dispose() } catch [IO.IOException] { break } catch { }
            Start-Sleep -Milliseconds 100
        }
    }
}
function Restart-Service { param([string]$Name, [switch]$Force) $global:ServiceStatus = 'Running' }
function Set-Acl { param([string]$LiteralPath, $AclObject) }
function Invoke-WebRequest {
    param([string]$Uri, [switch]$NoProxy, [int]$TimeoutSec, [switch]$UseBasicParsing)
    if ($HealthFails) { throw 'stub: health check refused' }
    [pscustomobject]@{ Content = '{"status":"live","protocolTag":"selftest","protocolCommit":"selftest"}' }
}
try {
    & $Script -PackagePath "$Case/package" -ResultPath "$Case/result.json" -DiagnosticPath "$Case/diagnostic.log" `
        -ServiceName 'cs503-selftest' -InstallRoot "$Case/install" -DataRoot "$Case/dataroot" -BackupRoot "$Case/backups" `
        -CertificatePasswordVariable 'CS503_SELFTEST_NO_SUCH_VARIABLE'
    Set-Content -LiteralPath "$Case/outcome.txt" -Value 'COMPLETED'
}
catch {
    $inner = if ($_.Exception -is [AggregateException]) { ($_.Exception.InnerExceptions | ForEach-Object Message) -join ' || ' } else { '' }
    Set-Content -LiteralPath "$Case/outcome.txt" -Value ("THREW: " + $_.Exception.Message + ' || ' + $inner)
}
'@, [Text.UTF8Encoding]::new($false))

    function New-UpgradeCase([string]$Name, [bool]$WithLockFile) {
        $case = New-Item -ItemType Directory -Path (Join-Path $scratch $Name)
        $package = New-Item -ItemType Directory -Path (Join-Path $case 'package')
        [IO.File]::WriteAllText((Join-Path $package 'ControlServer.Host.exe'), 'new binary')
        $hash = (Get-FileHash -LiteralPath (Join-Path $package 'ControlServer.Host.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifest = [ordered]@{ schemaVersion = 1; product = '8005 AGV ControlServer'; sourceCommit = 'selftest'
            files = @([ordered]@{ path = 'ControlServer.Host.exe'; sha256 = $hash }) }
        [IO.File]::WriteAllText((Join-Path $package 'deployment-manifest.json'), ($manifest | ConvertTo-Json -Depth 4))
        $install = New-Item -ItemType Directory -Path (Join-Path $case 'install')
        [IO.File]::WriteAllText((Join-Path $install 'ControlServer.Host.exe'), 'old binary')
        [IO.File]::WriteAllText((Join-Path $install 'appsettings.Production.json'),
            '{ "Health": { "url": "http://127.0.0.1:1" }, "JourneyRuntime": { "enabled": false } }')
        $lock = New-DataRoot (Join-Path $case 'dataroot') $WithLockFile
        return @{ Root = $case.FullName; Lock = $lock }
    }

    function Invoke-Upgrade($Case, [string]$HolderLock = '', [int]$HolderSeconds = 0, [switch]$HealthFails) {
        $arguments = @('-NoProfile', '-NonInteractive', '-File', $runner, '-Script', $scriptCopy, '-Case', $Case.Root)
        if ($HolderLock) { $arguments += @('-HolderLock', $HolderLock, '-HolderSeconds', $HolderSeconds) }
        if ($HealthFails) { $arguments += '-HealthFails' }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        & pwsh @arguments
        $pidFile = "$($Case.Root).holder-pid"
        if (Test-Path -LiteralPath $pidFile) {
            $holderPid = [int](Get-Content -LiteralPath $pidFile)
            $process = Get-Process -Id $holderPid -ErrorAction SilentlyContinue
            if ($process) { $holders.Add($process) }
        }
        return [ordered]@{
            Outcome = (Get-Content -Raw -LiteralPath (Join-Path $Case.Root 'outcome.txt')).Trim()
            Diagnostic = if (Test-Path (Join-Path $Case.Root 'diagnostic.log')) { Get-Content -Raw (Join-Path $Case.Root 'diagnostic.log') } else { '' }
            Seconds = [int]$watch.Elapsed.TotalSeconds
        }
    }

    # U1: the old process still holds the lock after Stop-Service reports Stopped.
    $case = New-UpgradeCase 'u1-held' $true
    $dataBefore = Get-TreeFingerprint (Join-Path $case.Root 'dataroot')
    $null = Start-Holder $case.Lock 120
    $result = Invoke-Upgrade $case
    Stop-Holders
    $dataAfter = Get-TreeFingerprint (Join-Path $case.Root 'dataroot')
    $backups = @(Get-ChildItem -LiteralPath (Join-Path $case.Root 'backups') -Recurse -Force -File -ErrorAction SilentlyContinue)
    Write-Result ($result.Outcome -like 'THREW: DATA_ROOT_IN_USE:*') 'U1 held lock: the upgrade stops with DATA_ROOT_IN_USE' $result.Outcome
    Write-Result ($dataAfter -eq $dataBefore) 'U1 held lock: the data root is untouched'
    Write-Result ($backups.Count -eq 0 -and $result.Diagnostic -notmatch 'backup-complete' -and $result.Diagnostic -notmatch 'replacement-installed') `
        'U1 held lock: nothing was backed up and the binary was not replaced' "backupFiles=$($backups.Count)"
    Write-Result ((Get-Content -Raw (Join-Path $case.Root 'install/ControlServer.Host.exe')) -eq 'old binary') 'U1 held lock: the old binary is still installed'
    Write-Result ($result.Seconds -ge 30) 'U1 held lock: it waited the full 30 seconds first' "seconds=$($result.Seconds)"

    # U2: the old process lets go a few seconds after Stop-Service.
    $case = New-UpgradeCase 'u2-released' $true
    $null = Start-Holder $case.Lock 4
    $result = Invoke-Upgrade $case
    Stop-Holders
    $backupData = Get-ChildItem -LiteralPath (Join-Path $case.Root 'backups') -Directory | Select-Object -First 1
    $backedUp = if ($backupData) { @(Get-ChildItem -LiteralPath (Join-Path $backupData.FullName 'data-root/data') -Force -File | ForEach-Object Name | Sort-Object) } else { @() }
    $pass = (Test-Path (Join-Path $case.Root 'result.json')) -and ((Get-Content -Raw (Join-Path $case.Root 'result.json') | ConvertFrom-Json).result -eq 'PASS')
    Write-Result ($result.Outcome -eq 'COMPLETED' -and $pass) 'U2 lock let go: the upgrade completes with PASS' $result.Outcome
    Write-Result ($result.Diagnostic -match 'data-root-released lockFiles=1') 'U2 lock let go: the diagnostic records the one lock file it waited on'
    Write-Result (($backedUp -join ',') -eq 'controlserver.db,controlserver.db-shm,controlserver.db-wal,controlserver.db.instance-lock') `
        'U2 lock let go: the backup holds the database with its -wal and -shm' ($backedUp -join ',')

    # U3: an instance installed before control-server#473 has no lock file.
    $case = New-UpgradeCase 'u3-no-lock-file' $false
    $result = Invoke-Upgrade $case
    $pass = (Test-Path (Join-Path $case.Root 'result.json')) -and ((Get-Content -Raw (Join-Path $case.Root 'result.json') | ConvertFrom-Json).result -eq 'PASS')
    Write-Result ($result.Outcome -eq 'COMPLETED' -and $pass -and $result.Diagnostic -match 'data-root-released lockFiles=0') `
        'U3 no lock file: the upgrade completes as before' "$($result.Outcome) seconds=$($result.Seconds)"

    # U4: the replacement takes the lock, its health check fails, and it outlives Stop-Service: the rollback must
    # not delete the data root under it.
    $case = New-UpgradeCase 'u4-rollback-held' $true
    $dataBefore = Get-TreeFingerprint (Join-Path $case.Root 'dataroot')
    $result = Invoke-Upgrade $case -HolderLock $case.Lock -HolderSeconds 120 -HealthFails
    Stop-Holders
    $dataAfter = Get-TreeFingerprint (Join-Path $case.Root 'dataroot')
    $backupData = Get-ChildItem -LiteralPath (Join-Path $case.Root 'backups') -Directory | Select-Object -First 1
    $backupFingerprint = Get-TreeFingerprint (Join-Path $backupData.FullName 'data-root')
    Write-Result ($result.Outcome -like 'THREW: ControlServer upgrade and rollback both failed.*' -and $result.Outcome -match 'DATA_ROOT_IN_USE:') `
        'U4 rollback under a live holder: refused with DATA_ROOT_IN_USE' $result.Outcome
    Write-Result ($dataAfter -eq $dataBefore) 'U4 rollback under a live holder: the data root was not deleted'
    Write-Result ($backupFingerprint -eq $dataBefore) 'U4 rollback under a live holder: the backup is intact for a manual restore'
    Write-Result ($result.Diagnostic -match 'rollback-failure' -and $result.Diagnostic -notmatch 'rollback-data-root-released') `
        'U4 rollback under a live holder: the diagnostic records the refused rollback'

    # U5: the same, but the replacement exits a few seconds after Stop-Service: the rollback goes ahead.
    $case = New-UpgradeCase 'u5-rollback-released' $true
    $dataBefore = Get-TreeFingerprint (Join-Path $case.Root 'dataroot')
    $result = Invoke-Upgrade $case -HolderLock $case.Lock -HolderSeconds 4 -HealthFails
    Stop-Holders
    $dataAfter = Get-TreeFingerprint (Join-Path $case.Root 'dataroot')
    Write-Result ($result.Outcome -like 'THREW: HTTP GET of *' -and $result.Diagnostic -match 'rollback-complete') `
        'U5 rollback after the holder exits: the rollback completes and the original error is rethrown' $result.Outcome
    Write-Result ($dataAfter -eq $dataBefore) 'U5 rollback after the holder exits: the data root is restored from the backup'
    Write-Result ((Get-Content -Raw (Join-Path $case.Root 'install/ControlServer.Host.exe')) -eq 'old binary') 'U5 rollback after the holder exits: the old binary is back'
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
