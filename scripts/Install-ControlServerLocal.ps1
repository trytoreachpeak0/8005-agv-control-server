#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [Parameter(Mandatory = $true)]
    [string]$ResultPath,
    [string]$DiagnosticPath,
    [string]$ServiceName = '8005 AGV ControlServer',
    [string]$InstallRoot = 'C:\Program Files\8005 AGV\ControlServer',
    [string]$DataRoot = 'C:\ProgramData\8005\ControlServer',
    [string]$BackupRoot = 'C:\ProgramData\8005\ControlServer-backups',
    [ValidateRange(1, 65535)]
    [int]$OnboardPort = 58005,
    [ValidateRange(1, 65535)]
    [int]$HealthPort = 58007,
    [string]$ListenAddress = '127.0.0.1',
    [string]$HealthBindAddress = '127.0.0.1',
    [switch]$CopyUserRiotSecretToMachine,
    [switch]$SkipMachineEnvironmentInjection,
    # control-server#80: the release candidate's dashboard directory. Optional, so an install that names
    # only the server package behaves exactly as before.
    [string]$DashboardPackagePath,
    [string]$DashboardInstallRoot = 'C:\Program Files\8005 AGV\ControlServer.Dashboard',
    [string]$DashboardTaskName = '8005 AGV ControlServer Dashboard',
    [ValidateRange(1, 65535)]
    [int]$DashboardPort = 58009
)

$ErrorActionPreference = 'Stop'

function Resolve-FullPath([string]$Path) {
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

$serviceName = $ServiceName
$installPath = Resolve-FullPath $InstallRoot
$dataRoot = Resolve-FullPath $DataRoot
$logDirectory = Join-Path $dataRoot 'logs'
$backupRoot = Resolve-FullPath $BackupRoot
$resolvedPackage = Resolve-FullPath $PackagePath
$resolvedResult = Resolve-FullPath $ResultPath
$resolvedDiagnostic = if ([string]::IsNullOrWhiteSpace($DiagnosticPath)) { $null } else { Resolve-FullPath $DiagnosticPath }
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$backupPath = Join-Path $backupRoot $runId
$healthOrigin = "http://${HealthBindAddress}:$HealthPort"
# Kestrel accepts a wildcard bind address, but nothing can connect to one. The lifecycle checks below
# therefore dial loopback whenever the service was told to listen on every interface.
$healthCheckHost = if ($HealthBindAddress -in @('0.0.0.0', '*', '+', '::', '[::]')) {
    '127.0.0.1'
} else {
    $HealthBindAddress
}
$healthCheckOrigin = "http://${healthCheckHost}:$HealthPort"
$serviceCreated = $false
$installCreated = $false
$dataRootExisted = Test-Path -LiteralPath $dataRoot
$dataBackupCreated = $false
# An existing data root with nothing in it has nothing to back up; the rollback may empty it again. One with
# content and no completed backup ($dataBackupCreated still false) must never be emptied (control-server#503).
$dataRootWasEmpty = $false
$oldRiotMachine = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', 'Machine')
$machineEnvironmentInjected = $false
$installDashboard = -not [string]::IsNullOrWhiteSpace($DashboardPackagePath)
$resolvedDashboardPackage = if ($installDashboard) { Resolve-FullPath $DashboardPackagePath } else { $null }
$dashboardInstallPath = Resolve-FullPath $DashboardInstallRoot
# The dashboard has no authentication, so it stays on loopback whatever the server binds (program#55): a
# maintainer reaches it with ssh -L 58009:127.0.0.1:58009. Opening it to the LAN is a separate decision.
$dashboardOrigin = "http://127.0.0.1:$DashboardPort"
$dashboardProduct = '8005 AGV ControlServer Dashboard'
$dashboardInstallCreated = $false
$dashboardTaskCreated = $false
$dashboardPageServed = $false

function Assert-Administrator {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Install-ControlServerLocal.ps1 must run from an elevated PowerShell process.'
    }
}

function Write-Diagnostic([string]$Message) {
    if ([string]::IsNullOrWhiteSpace($resolvedDiagnostic)) { return }
    $directory = Split-Path -Parent $resolvedDiagnostic
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $line = '{0} {1}{2}' -f [DateTimeOffset]::UtcNow.ToString('O'), $Message, [Environment]::NewLine
    [IO.File]::AppendAllText($resolvedDiagnostic, $line, [Text.UTF8Encoding]::new($false))
}

function Set-RestrictedDirectoryAcl([string]$Path) {
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sidValue in @('S-1-5-18', 'S-1-5-32-544')) {
        $sid = [Security.Principal.SecurityIdentifier]::new($sidValue)
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $security
}

function Set-RestrictedRegistryAcl([Microsoft.Win32.RegistryKey]$Key) {
    $security = [Security.AccessControl.RegistrySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sidValue in @('S-1-5-18', 'S-1-5-32-544')) {
        $sid = [Security.Principal.SecurityIdentifier]::new($sidValue)
        $rule = [Security.AccessControl.RegistryAccessRule]::new(
            $sid,
            [Security.AccessControl.RegistryRights]::FullControl,
            [Security.AccessControl.InheritanceFlags]::ContainerInherit,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow)
        [void]$security.AddAccessRule($rule)
    }
    $Key.SetAccessControl($security)
}

function Test-PackageManifest([string]$Path, [string]$ExpectedProduct = '8005 AGV ControlServer') {
    $manifestPath = Join-Path $Path 'deployment-manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'Package deployment-manifest.json is missing.'
    }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or $manifest.product -ne $ExpectedProduct) {
        throw 'Package manifest identity is invalid.'
    }
    $packagePrefix = $Path.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($file in $manifest.files) {
        $candidate = [IO.Path]::GetFullPath((Join-Path $Path $file.path))
        if (-not $candidate.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Package manifest contains an escaping path: $($file.path)"
        }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "Package file is missing: $($file.path)"
        }
        $actual = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $file.sha256) {
            throw "Package file hash mismatch: $($file.path)"
        }
    }
    return $manifest
}

function Wait-ServiceState([string]$ExpectedStatus, [int]$Seconds = 30) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    do {
        $service = Get-Service -Name $serviceName -ErrorAction Stop
        if ($service.Status.ToString() -eq $ExpectedStatus) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Service did not reach $ExpectedStatus within $Seconds seconds."
}

<#
.SYNOPSIS
Waits until no process holds a ControlServer database under the data root, or refuses.
.DESCRIPTION
control-server#503. Stop-Service returns once the service control manager reports Stopped, which says
nothing about whether the process has exited and closed the database. The database runs in WAL mode
(EF Core's SqliteDatabaseCreator turns it on when it creates the file), so a copy or a delete of the
data root under a process that is still exiting copies or deletes a database that is still being
written, and part of it may still be only in -wal.

The proof is the lock control-server#473 added: the Host opens <database>.instance-lock with
FileShare.None for as long as it lives, and the operating system closes that handle when the process
is gone, however it went. Opening every such file under the data root exclusively therefore proves
that no Host still owns any database there. A data root with no lock file (an instance installed
before control-server#473, upgraded for the first time) has nothing to wait for. Opened for reading
only, so a read-only lock file is not mistaken for a held one.

The refusal names this instance's Host processes by path, never by name: from 2026-10-08 the MVP and the
v2 instance run side by side on factory01 as two ControlServer.Host processes, so an operator who follows
a "stop ControlServer.Host" instruction stops production. -NotDone says what the refusal left undone,
-NextSteps what the operator does instead; both are the caller's, because only the caller knows its state.

Keep this function identical in Update-ControlServerLocal.ps1 and Install-ControlServerLocal.ps1;
scripts/Test-DataRootLockWait.ps1 compares the two.
#>
function Wait-DataRootReleased {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$NotDone,
        [Parameter(Mandatory)][string]$NextSteps,
        [string]$InstallPath,
        [switch]$AfterServiceStop,
        [int]$Seconds = 30
    )
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return @() }
    $lockFiles = @(Get-ChildItem -LiteralPath $Root -Recurse -Force -File -Filter '*.instance-lock' |
        ForEach-Object { $_.FullName })
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    foreach ($lockFile in $lockFiles) {
        while ($true) {
            try {
                [IO.File]::Open($lockFile, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None).Dispose()
                break
            }
            catch [IO.FileNotFoundException] { break }
            catch [IO.DirectoryNotFoundException] { break }
            catch [IO.IOException] {
                if ([DateTimeOffset]::UtcNow -lt $deadline) {
                    Start-Sleep -Milliseconds 250
                    continue
                }
                $text = [Text.StringBuilder]::new()
                [void]$text.Append("DATA_ROOT_IN_USE: 等了 $Seconds 秒，仍有进程占着数据目录 $Root 里的库（锁文件 $lockFile）。")
                if ($AfterServiceStop) {
                    [void]$text.Append('服务管理器报「已停止」只说明服务报了停，不说明进程已经退出、库已经关上。')
                }
                [void]$text.Append("库是 WAL 模式，这时拷贝或删除数据目录，会拷到或删掉一个还在写的库，所以没有$NotDone，数据目录原样未动。")
                if ([string]::IsNullOrWhiteSpace($InstallPath)) {
                    [void]$text.Append('按路径查是哪个进程：Get-Process ControlServer.Host | Select-Object Id, Path，只认路径属于这个实例的那一个。')
                }
                else {
                    $prefix = $InstallPath.TrimEnd('\', '/') + '\'
                    $ours = @(Get-Process -Name 'ControlServer.Host' -ErrorAction SilentlyContinue |
                        Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } |
                        ForEach-Object { "PID $($_.Id)" })
                    [void]$text.Append("本实例的 Host 按安装目录查：Get-Process ControlServer.Host | Where-Object Path -like '$prefix*'。")
                    [void]$text.Append($(if ($ours.Count -gt 0) { "现在查到：$($ours -join '、')。" }
                        else { '现在没有查到，占着锁的可能是别的进程，按锁文件所在的库去找。' }))
                }
                [void]$text.Append('不要按名字结束进程（Stop-Process -Name、taskkill /IM）：同一台机器上 MVP 与 v2 的 Host 同名，按名字会连生产实例一起停掉。')
                [void]$text.Append('不要删除锁文件：删它解不了锁。')
                [void]$text.Append("接下来：$NextSteps")
                throw $text.ToString()
            }
        }
    }
    return $lockFiles
}

# curl.exe is not present on every Windows this script installs on. It ships with
# Windows 10 1803 and Server 2019; the factory server is Server 2016 and has none,
# where the previous implementation failed with "The term
# 'C:\Windows\System32\curl.exe' is not recognized". PowerShell 7's own client
# covers the same ground: -NoProxy for --noproxy, -TimeoutSec for --max-time, and
# a non-2xx status throwing by default the way --fail does. -SkipHttpErrorCheck
# restores the one call that deliberately did not pass --fail, because it has to
# read the body of a not-ready response.
function Invoke-HealthJson([string]$Uri, [switch]$AllowErrorStatus) {
    try {
        $response = Invoke-WebRequest -Uri $Uri -NoProxy -TimeoutSec 10 -UseBasicParsing `
            -SkipHttpErrorCheck:$AllowErrorStatus
    }
    catch {
        throw "HTTP request to $Uri failed: $($_.Exception.Message)"
    }
    return $response.Content | ConvertFrom-Json
}

function Invoke-LiveCheck {
    $response = Invoke-HealthJson "$healthCheckOrigin/health/live"
    if ($response.status -ne 'live') { throw 'HTTP live check returned an unexpected response.' }
}

function Get-ReadyCheck {
    $response = Invoke-HealthJson "$healthCheckOrigin/health/ready" -AllowErrorStatus
    if ($response.status -eq 'not-ready' -and $response.reason -eq 'DATABASE_UNAVAILABLE') {
        throw 'Readiness reports the database is unavailable after migration.'
    }
    if ($response.status -ne 'ready' -and $response.status -ne 'not-ready') {
        throw 'HTTP ready check returned an unexpected response.'
    }
    return $response
}

function Get-VersionCheck {
    return Invoke-HealthJson "$healthCheckOrigin/version"
}

# The page is the check, not the port: it has to come back rendered with the blocked-journey card on it,
# which also proves the dashboard reached the server's read-only endpoints through the origin it was given.
function Wait-DashboardPage([int]$Seconds = 60) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    $lastError = 'no response'
    do {
        try {
            $response = Invoke-WebRequest -Uri "$dashboardOrigin/" -NoProxy -TimeoutSec 5 -UseBasicParsing
            if ($response.StatusCode -eq 200 -and $response.Content.Contains('id="blocked-journeys"')) { return $true }
            $lastError = "status $($response.StatusCode) without the blocked-journeys card"
        }
        catch {
            $lastError = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "The dashboard at $dashboardOrigin did not serve its page within $Seconds seconds: $lastError"
}

function Stop-DashboardProcess {
    $prefix = $dashboardInstallPath.TrimEnd('\') + '\'
    Get-Process -Name 'ControlServer.Dashboard' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

Assert-Administrator
if (-not $CopyUserRiotSecretToMachine -and -not $SkipMachineEnvironmentInjection) {
    throw 'Explicit -CopyUserRiotSecretToMachine authorization is required unless -SkipMachineEnvironmentInjection is used.'
}
if (-not (Test-Path -LiteralPath $resolvedPackage -PathType Container)) {
    throw "Package path does not exist: $resolvedPackage"
}
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "Service already exists; this first-install script will not replace it: $serviceName"
}
if (Test-Path -LiteralPath $installPath) {
    throw "Install path already exists; this first-install script will not replace it: $installPath"
}

$manifest = Test-PackageManifest $resolvedPackage
$dashboardManifest = $null
if ($installDashboard) {
    if (-not (Test-Path -LiteralPath $resolvedDashboardPackage -PathType Container)) {
        throw "Dashboard package path does not exist: $resolvedDashboardPackage"
    }
    if (Get-ScheduledTask -TaskName $DashboardTaskName -ErrorAction SilentlyContinue) {
        throw "Scheduled task already exists; this first-install script will not replace it: $DashboardTaskName"
    }
    if (Test-Path -LiteralPath $dashboardInstallPath) {
        throw "Dashboard install path already exists; this first-install script will not replace it: $dashboardInstallPath"
    }
    $dashboardManifest = Test-PackageManifest $resolvedDashboardPackage $dashboardProduct
    if ($dashboardManifest.sourceCommit -ne $manifest.sourceCommit) {
        throw "The dashboard package was built from $($dashboardManifest.sourceCommit), the server package from $($manifest.sourceCommit)."
    }
}
$riotUser = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', 'User')
if ([string]::IsNullOrWhiteSpace($riotUser)) {
    throw 'User-scope CONTROL_SERVER_RIOT_CALL_API_KEY is missing.'
}
if (-not $SkipMachineEnvironmentInjection -and
    -not [string]::IsNullOrWhiteSpace($oldRiotMachine) -and $oldRiotMachine -cne $riotUser) {
    throw 'Machine-scope RIoT credential already exists with different content; refusing overwrite.'
}
$onboardCredential = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CREDENTIAL', 'Machine')
if ([string]::IsNullOrWhiteSpace($onboardCredential)) {
    throw 'Machine-scope CONTROL_SERVER_ONBOARD_CREDENTIAL is missing.'
}
# No service exists yet (refused above), but a Host started by hand on an existing data root would still be
# writing the database the backup below copies (control-server#503). Before the try, not inside it: a refusal
# here has touched nothing, so there is nothing for the error path to undo.
if ($dataRootExisted) {
    $null = Wait-DataRootReleased -Root $dataRoot -NotDone '备份既有数据目录、也没有安装' `
        -NextSteps '查清并停下占着这个数据目录的那个进程（按路径认，不按名字）后，重新执行本脚本。本脚本还什么都没有做。'
}
Write-Diagnostic 'preflight-complete'

try {
    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
    if ($dataRootExisted) {
        $existingData = Get-ChildItem -LiteralPath $dataRoot -Force
        if ($existingData) {
            $dataBackup = Join-Path $backupPath 'data-root'
            New-Item -ItemType Directory -Path $dataBackup -Force | Out-Null
            foreach ($item in $existingData) { Copy-Item -LiteralPath $item.FullName -Destination $dataBackup -Recurse -Force }
            $dataBackupCreated = $true
        }
        else {
            $dataRootWasEmpty = $true
        }
    }
    Write-Diagnostic 'backup-complete'

    New-Item -ItemType Directory -Path $installPath -Force | Out-Null
    $installCreated = $true
    foreach ($item in Get-ChildItem -LiteralPath $resolvedPackage -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $installPath -Recurse -Force
    }
    Write-Diagnostic 'package-copy-complete'

    # The data root used to come into being as a side effect of creating the certificate directory
    # under it. With the certificates gone it has to be created outright, or the ACL below has
    # nothing to tighten on a machine that has never run this service.
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null

    if (-not $SkipMachineEnvironmentInjection) {
        [Environment]::SetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', $riotUser, 'Machine')
        $machineEnvironmentInjected = $true
    }
    Write-Diagnostic ("machine-secret-injection-complete injected={0}" -f $machineEnvironmentInjected)

    $databasePath = Join-Path (Join-Path $dataRoot 'data') 'controlserver.db'
    $configuration = [ordered]@{
        Health = [ordered]@{ url = $healthOrigin }
        ConnectionStrings = [ordered]@{ ControlServer = "Data Source=$databasePath" }
        OnboardTransport = [ordered]@{
            enabled = $true
            listenAddress = $ListenAddress
            port = $OnboardPort
            credentialEnvironmentVariable = 'CONTROL_SERVER_ONBOARD_CREDENTIAL'
        }
        OnboardSafetyProjection = [ordered]@{
            enabled = $true
            credentialEnvironmentVariable = 'CONTROL_SERVER_ONBOARD_CREDENTIAL'
        }
        JourneyRuntime = [ordered]@{ enabled = $false }
        Serilog = [ordered]@{
            WriteTo = @(
                [ordered]@{
                    Name = 'File'
                    Args = [ordered]@{
                        path = (Join-Path $logDirectory 'controlserver-.ndjson')
                        formatter = 'Serilog.Formatting.Compact.CompactJsonFormatter, Serilog.Formatting.Compact'
                        rollingInterval = 'Day'
                        retainedFileCountLimit = 14
                        shared = $true
                    }
                }
            )
        }
    }
    $configurationPath = Join-Path $installPath 'appsettings.Production.json'
    [IO.File]::WriteAllText(
        $configurationPath,
        ($configuration | ConvertTo-Json -Depth 6),
        [Text.UTF8Encoding]::new($false))
    Write-Diagnostic 'configuration-complete'

    Set-RestrictedDirectoryAcl $installPath
    Set-RestrictedDirectoryAcl $dataRoot

    $executable = Join-Path $installPath 'ControlServer.Host.exe'
    $binaryPath = '"{0}" --contentRoot "{1}" --environment Production' -f $executable, $installPath
    New-Service -Name $serviceName -BinaryPathName $binaryPath `
        -DisplayName $serviceName -Description '8005 AGV WIRE_TO_GATE ControlServer' `
        -StartupType Automatic | Out-Null
    $serviceCreated = $true
    $serviceRegistrySubKey = "SYSTEM\CurrentControlSet\Services\$serviceName"
    [string[]]$serviceEnvironment = @(
        'DOTNET_ENVIRONMENT=Production',
        "CONTROL_SERVER_RIOT_CALL_API_KEY=$riotUser",
        "CONTROL_SERVER_ONBOARD_CREDENTIAL=$onboardCredential"
    )
    $serviceRegistryKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($serviceRegistrySubKey, $true)
    if ($null -eq $serviceRegistryKey) { throw 'The new service registry key cannot be opened.' }
    try {
        $serviceRegistryKey.SetValue('Environment', $serviceEnvironment, [Microsoft.Win32.RegistryValueKind]::MultiString)
        Set-RestrictedRegistryAcl $serviceRegistryKey
        $serviceEnvironmentVerified = @($serviceRegistryKey.GetValue('Environment')).Count -eq $serviceEnvironment.Count
    }
    finally {
        $serviceRegistryKey.Close()
    }
    Write-Diagnostic 'service-installation-complete'

    Start-Service -Name $serviceName
    Wait-ServiceState 'Running'
    Invoke-LiveCheck
    $firstReadiness = Get-ReadyCheck
    Stop-Service -Name $serviceName
    Wait-ServiceState 'Stopped'
    Start-Service -Name $serviceName
    Wait-ServiceState 'Running'
    Invoke-LiveCheck
    Restart-Service -Name $serviceName -Force
    Wait-ServiceState 'Running'
    Invoke-LiveCheck
    Write-Diagnostic 'service-lifecycle-checks-complete'

    if ($installDashboard) {
        New-Item -ItemType Directory -Path $dashboardInstallPath -Force | Out-Null
        $dashboardInstallCreated = $true
        foreach ($item in Get-ChildItem -LiteralPath $resolvedDashboardPackage -Force) {
            Copy-Item -LiteralPath $item.FullName -Destination $dashboardInstallPath -Recurse -Force
        }
        Set-RestrictedDirectoryAcl $dashboardInstallPath

        # A scheduled task at startup rather than a service: the dashboard's Program.cs does not host itself
        # as a Windows service, the service control manager kills an executable that never reports in, and
        # that file is the dashboard main file its self-registration convention keeps unchanged. The task
        # runs as LocalSystem like the server, restarts on failure, and has no time limit.
        $dashboardExecutable = Join-Path $dashboardInstallPath 'ControlServer.Dashboard.exe'
        $dashboardArguments = '--Dashboard:url={0} --Dashboard:controlServerBaseUrl={1}' -f $dashboardOrigin, $healthCheckOrigin
        $dashboardAction = New-ScheduledTaskAction -Execute $dashboardExecutable -Argument $dashboardArguments `
            -WorkingDirectory $dashboardInstallPath
        $dashboardTrigger = New-ScheduledTaskTrigger -AtStartup
        $dashboardPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
        $dashboardSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
            -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable `
            -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $DashboardTaskName -Action $dashboardAction -Trigger $dashboardTrigger `
            -Principal $dashboardPrincipal -Settings $dashboardSettings `
            -Description '8005 AGV ControlServer read-only dashboard, loopback only' | Out-Null
        $dashboardTaskCreated = $true
        Start-ScheduledTask -TaskName $DashboardTaskName
        $dashboardPageServed = Wait-DashboardPage
        Write-Diagnostic 'dashboard-installation-complete'
    }

    $databaseCreated = Test-Path -LiteralPath $databasePath -PathType Leaf
    if (-not $databaseCreated) { throw "The service did not create the SQLite database at $databasePath." }
    $logFiles = @()
    if (Test-Path -LiteralPath $logDirectory -PathType Container) {
        $logFiles = @(Get-ChildItem -LiteralPath $logDirectory -File | Sort-Object Name |
            ForEach-Object { $_.Name })
    }
    if ($logFiles.Count -eq 0) { throw "The service did not write a log file under $logDirectory." }

    $version = Get-VersionCheck
    $resultDirectory = Split-Path -Parent $resolvedResult
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    $result = [ordered]@{
        schemaVersion = 2
        result = 'PASS'
        runId = $runId
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
        sourceCommit = $manifest.sourceCommit
        packageManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $resolvedPackage 'deployment-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        configurationSha256 = (Get-FileHash -LiteralPath $configurationPath -Algorithm SHA256).Hash.ToLowerInvariant()
        serviceName = $serviceName
        serviceAccount = 'LocalSystem'
        serviceStatus = (Get-Service -Name $serviceName).Status.ToString()
        serviceStartType = (Get-CimInstance Win32_Service -Filter "Name='$serviceName'").StartMode
        installRoot = $installPath
        dataRoot = $dataRoot
        databasePath = $databasePath
        databaseCreatedByMigration = $databaseCreated
        logDirectory = $logDirectory
        logFiles = @($logFiles)
        backupRoot = $backupRoot
        httpEndpoint = $healthOrigin
        httpCheckOrigin = $healthCheckOrigin
        onboardTransportEndpoint = "tcp://${ListenAddress}:$OnboardPort"
        transport = 'plaintext'
        firstStartReadiness = [ordered]@{
            status = $firstReadiness.status
            reason = $firstReadiness.reason
        }
        externalSecrets = [ordered]@{
            machineEnvironmentInjected = $machineEnvironmentInjected
            riotMachineScopePresent = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', 'Machine'))
            onboardMachineScopePresent = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CREDENTIAL', 'Machine'))
            serviceSpecificEnvironmentPresent = $serviceEnvironmentVerified
            valuesDisclosed = $false
        }
        checks = @('package-hashes', 'sqlite-migrations-at-start', 'http-live-after-start', 'http-ready-after-start', 'stop-start', 'restart', 'http-version', 'log-file-written') +
            @(if ($installDashboard) { 'dashboard-package-hashes'; 'dashboard-page-served' })
        dashboard = [ordered]@{
            installed = $installDashboard
            taskName = if ($installDashboard) { $DashboardTaskName } else { $null }
            installRoot = if ($installDashboard) { $dashboardInstallPath } else { $null }
            url = if ($installDashboard) { $dashboardOrigin } else { $null }
            controlServerBaseUrl = if ($installDashboard) { $healthCheckOrigin } else { $null }
            packageManifestSha256 = if ($installDashboard) {
                (Get-FileHash -LiteralPath (Join-Path $resolvedDashboardPackage 'deployment-manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
            } else { $null }
            pageServed = $dashboardPageServed
        }
        journeyRuntimeEnabled = $false
        riotMutationPerformed = $false
        orderCreated = $false
        vehicleMoved = $false
        protocolTag = $version.protocolTag
        protocolCommit = $version.protocolCommit
        backupPath = $backupPath
    }
    [IO.File]::WriteAllText($resolvedResult, ($result | ConvertTo-Json -Depth 7), [Text.UTF8Encoding]::new($false))
    Write-Diagnostic 'result-written'
    Write-Output "ControlServer local deployment PASS. Result: $resolvedResult"
}
catch {
    $originalError = $_
    $rollbackErrors = [Collections.Generic.List[string]]::new()
    Write-Diagnostic ("failure: {0}" -f $originalError.Exception.Message)
    try {
        if ($dashboardTaskCreated) {
            Stop-ScheduledTask -TaskName $DashboardTaskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $DashboardTaskName -Confirm:$false
        }
        if ($dashboardInstallCreated) { Stop-DashboardProcess }
    }
    catch { $rollbackErrors.Add("dashboard-task: $($_.Exception.Message)") }
    try {
        if ($dashboardInstallCreated -and (Test-Path -LiteralPath $dashboardInstallPath)) {
            Remove-Item -LiteralPath $dashboardInstallPath -Recurse -Force
        }
    }
    catch { $rollbackErrors.Add("dashboard-install-path: $($_.Exception.Message)") }
    try {
        if ($serviceCreated) {
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            & sc.exe delete $serviceName | Out-Null
        }
    }
    catch { $rollbackErrors.Add("service: $($_.Exception.Message)") }
    # The service stopped above may still be exiting: never delete an install directory or a data root a live
    # Host is using (control-server#503). Before the install directory, as in Update-ControlServerLocal.ps1. A
    # refusal leaves both as they are, is reported with the rest, and says how to finish by hand.
    $dataRootReleased = $true
    try {
        $dataRootStep = if ($dataBackupCreated) {
            "清空 $dataRoot，再把 $backupPath\data-root 里的内容全部拷回（.db、-wal、-shm 一起）"
        } elseif (-not $dataRootExisted) {
            "删除 $dataRoot：它是这次安装新建的，没有要恢复的内容"
        } elseif ($dataRootWasEmpty) {
            "清空 $dataRoot：它在安装前是空的"
        } else {
            "不要动 $dataRoot：它在安装前就有内容，但备份没有完成，那里面就是原来的数据"
        }
        $null = Wait-DataRootReleased -Root $dataRoot -InstallPath $installPath -AfterServiceStop `
            -NotDone '删除安装目录、回滚数据目录' `
            -NextSteps ("手工完成回滚。1. 按上面的命令确认本实例的 Host 已经退出（没有输出）。" +
                "2. 删除 $installPath。3. $dataRootStep。" +
                $(if ($serviceCreated) { '本次安装注册的服务已在回滚里删除，不用起服务。' } else { '本次安装还没有注册服务。' }))
    }
    catch {
        $dataRootReleased = $false
        $rollbackErrors.Add("data-root: $($_.Exception.Message)")
    }
    try {
        if ($dataRootReleased -and $installCreated -and (Test-Path -LiteralPath $installPath)) {
            Remove-Item -LiteralPath $installPath -Recurse -Force
        }
    }
    catch { $rollbackErrors.Add("install-path: $($_.Exception.Message)") }
    try {
        if ($machineEnvironmentInjected) {
            [Environment]::SetEnvironmentVariable('CONTROL_SERVER_RIOT_CALL_API_KEY', $oldRiotMachine, 'Machine')
        }
    }
    catch { $rollbackErrors.Add("machine-environment: $($_.Exception.Message)") }
    try {
        if ($dataRootReleased -and $dataRootExisted -and -not $dataBackupCreated -and -not $dataRootWasEmpty -and
            (Test-Path -LiteralPath $dataRoot)) {
            # Content was there before this install and its backup never completed: emptying it now would delete
            # the only copy (control-server#503; a v2 uninstall keeps its data root, so a later first install
            # starts here). Leave it, say so, and let a person compare it with whatever the backup did copy.
            $rollbackErrors.Add(("data-root: DATA_ROOT_BACKUP_INCOMPLETE: 数据目录 $dataRoot 在安装前就有内容，但备份没有完成" +
                "（$backupPath），所以回滚没有清空或删除它，原有内容原样保留。本次安装失败前可能已往里写过文件，" +
                "请对照 $backupPath 里已拷出的部分人工核对后再重新安装。"))
        }
        elseif ($dataRootReleased -and (Test-Path -LiteralPath $dataRoot)) {
            Get-ChildItem -LiteralPath $dataRoot -Force | Remove-Item -Recurse -Force
            if ($dataBackupCreated) {
                foreach ($item in Get-ChildItem -LiteralPath (Join-Path $backupPath 'data-root') -Force) {
                    Copy-Item -LiteralPath $item.FullName -Destination $dataRoot -Recurse -Force
                }
            }
            elseif (-not $dataRootExisted) {
                Remove-Item -LiteralPath $dataRoot -Force
            }
        }
    }
    catch { $rollbackErrors.Add("data-root: $($_.Exception.Message)") }
    Write-Diagnostic ("rollback-complete errors={0}" -f $rollbackErrors.Count)
    if ($rollbackErrors.Count -gt 0) {
        throw "Deployment failed and rollback reported: $($rollbackErrors -join '; ')"
    }
    throw $originalError
}
