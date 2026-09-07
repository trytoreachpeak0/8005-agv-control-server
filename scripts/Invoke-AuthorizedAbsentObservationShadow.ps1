[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,
    [Parameter(Mandatory = $true)]
    [string] $OnboardSource,
    [Parameter(Mandatory = $true)]
    [string] $SimulatorSource,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedProductCommit,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string] $ExpectedManifestSha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedOnboardCommit,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string] $ExpectedOnboardArtifactSha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedSimulatorCommit,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string] $ExpectedSimulatorArtifactSha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string] $ExpectedRunnerSha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string] $ExpectedProxySha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string] $ExpectedStateToolSha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string] $ExpectedPythonExeSha256,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-f]{64}$')]
    [string] $ExpectedPythonRuntimeSha256,
    [switch] $PreflightOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This run puts WPF windows on the machine's single interactive desktop, which 8005-mes-ingest's
# golden renderer and desktop suite also claim. The mutex name is the cross-repository contract.
Import-Module (Join-Path $PSScriptRoot 'DesktopLock.psm1') -Force

$productionDatabase = 'C:\ProgramData\8005\ControlServer\data\controlserver.db'
$proxyPort = 58888
$sessionPort = 59005
$healthPort = 59007
$simulatorPorts = @(1502, 58006)
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' +
    [guid]::NewGuid().ToString('N')
$root = Join-Path $env:TEMP "controlserver-authorized-absent-shadow-$stamp"
$runRootPreexisting = Test-Path -LiteralPath $root
$hostRoot = Join-Path $root 'host'
$peerRoot = Join-Path $root 'peers'
$logRoot = Join-Path $root 'logs'
$privateRoot = Join-Path $root 'private'
$toolRoot = Join-Path $root 'tools'
$databasePath = Join-Path $root 'shadow.db'
$privatePlanPath = Join-Path $privateRoot 'permit-private.json'
$sanitizedPlanPath = Join-Path $root 'shadow-result.json'
$finalResultPath = Join-Path $root 'result.json'
$proxyScript = Join-Path $PSScriptRoot 'authorized-experiment-readonly-proxy.py'
$stateScript = Join-Path $PSScriptRoot 'authorized-experiment-state.py'
$runProxyScript = Join-Path $toolRoot 'authorized-experiment-readonly-proxy.py'
$runStateScript = Join-Path $toolRoot 'authorized-experiment-state.py'
$stopMarker = Join-Path $env:TEMP 'controlserver-authorized-g3-orchestrator.stop'

$hostProcess = $null
$proxyProcess = $null
$onboardProcess = $null
$simulatorProcess = $null
$stateProcess = $null
# Released in `finally` after every peer is stopped. Declared here so that release is unconditional
# even when the run dies during preflight, before the lock was ever taken.
$desktopLock = $null
$result = 'FAIL'
$failure = $null
$sessionGeneration = $null
$proxyStatus = $null
$mesIngestContractPreflightPassed = $false
$toolIdentityVerified = $false
$runRootRestricted = $false
$hostEverStarted = $false
$hostIsolationConfigured = $false
$boundaryViolation = $false
$boundaryCheckCount = 0
$safetySampleCount = 0
$productionDatabaseBefore = $null
$productionDatabaseAfter = $null
$installedEffectiveStateBeforeSha256 = $null
$installedEffectiveStateAfterSha256 = $null
$installedStateBefore = $null
$installedStateAfter = $null
$cleanupFailures = [Collections.Generic.List[string]]::new()
$python = $null
$pythonRuntimeRoot = Join-Path $root 'python-runtime'
$expectedVehicleKey = $null
$maximumSafetyEvidenceAge = [TimeSpan]::Zero
$finalStateExtractionPassed = $false
$finalProxyStatusCaptured = $false
$preflightComplete = $false

function Wait-ExactListeningPort(
    [int] $Port,
    [Diagnostics.Process] $Process,
    [string] $ExpectedName,
    [int] $Seconds = 30) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($Seconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited -or $Process.ProcessName -ne $ExpectedName) {
            $script:boundaryViolation = $true
            throw "$ExpectedName exited before owning port $Port."
        }
        $owners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty OwningProcess -Unique)
        if ($owners.Count -eq 1 -and [int]$owners[0] -eq $Process.Id) {
            $script:boundaryCheckCount++
            return
        }
        if ($owners.Count -gt 0) {
            $script:boundaryViolation = $true
            throw "Port $Port is owned by an unexpected process."
        }
        Start-Sleep -Milliseconds 200
    }
    $script:boundaryViolation = $true
    throw "Timed out waiting for $ExpectedName to own port $Port."
}

function Assert-ExactListeningPort(
    [int] $Port,
    [Diagnostics.Process] $Process,
    [string] $ExpectedName) {
    $Process.Refresh()
    $owners = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    if ($Process.HasExited -or $Process.ProcessName -ne $ExpectedName -or
        $owners.Count -ne 1 -or [int]$owners[0] -ne $Process.Id) {
        $script:boundaryViolation = $true
        throw "$ExpectedName no longer exclusively owns port $Port."
    }
    $script:boundaryCheckCount++
}

function Assert-PortFree([int] $Port) {
    if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
        throw "Port $Port is already in use."
    }
}

function Set-RestrictedDirectoryAcl([string] $Path) {
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $sidValues = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
        'S-1-5-18',
        'S-1-5-32-544') | Select-Object -Unique
    foreach ($sidValue in $sidValues) {
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

function Assert-RestrictedDirectoryAcl([string] $Path) {
    $allowed = @(
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
        'S-1-5-18',
        'S-1-5-32-544')
    $acl = Get-Acl -LiteralPath $Path
    $rules = @($acl.GetAccessRules(
        $true,
        $true,
        [Security.Principal.SecurityIdentifier]))
    if (-not $acl.AreAccessRulesProtected -or
        $rules.Count -ne $allowed.Count -or
        @($rules | Where-Object {
            $_.IdentityReference.Value -notin $allowed -or
            $_.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or
            ($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne
                [Security.AccessControl.FileSystemRights]::FullControl
        }).Count -ne 0) {
        throw "Restricted ACL verification failed: $Path"
    }
}

function Ensure-RestrictedRunRoot {
    [IO.Directory]::CreateDirectory($root) | Out-Null
    Set-RestrictedDirectoryAcl $root
    Assert-RestrictedDirectoryAcl $root
    $script:runRootRestricted = $true
}

function Get-RequiredMachineSecret([string] $Name) {
    $value = [Environment]::GetEnvironmentVariable($Name, [EnvironmentVariableTarget]::Machine)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Required Machine-scope secret reference is absent: $Name"
    }
    return $value
}

function Get-FileBundleSha256([string[]] $Paths) {
    $lines = @($Paths | Sort-Object | ForEach-Object {
        $path = [IO.Path]::GetFullPath($_)
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $item = Get-Item -LiteralPath $path
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            "$($item.Name)`0$($item.Length)`0$hash"
        }
        else {
            "$([IO.Path]::GetFileName($path))`0ABSENT"
        }
    })
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($lines -join "`n")
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function New-ChildEnvironment([hashtable] $Values) {
    $environment = @{}
    foreach ($scope in @(
        [EnvironmentVariableTarget]::Process,
        [EnvironmentVariableTarget]::User,
        [EnvironmentVariableTarget]::Machine)) {
        foreach ($name in [Environment]::GetEnvironmentVariables($scope).Keys) {
            $environment[[string]$name] = $null
        }
    }
    foreach ($name in @(
        'SystemRoot', 'WINDIR', 'SystemDrive', 'ComSpec', 'PATH', 'PATHEXT',
        'TEMP', 'TMP', 'USERPROFILE', 'USERNAME', 'USERDOMAIN', 'APPDATA',
        'LOCALAPPDATA', 'HOMEDRIVE', 'HOMEPATH', 'ProgramData', 'ProgramFiles',
        'ProgramFiles(x86)', 'CommonProgramFiles', 'CommonProgramFiles(x86)',
        'PROCESSOR_ARCHITECTURE', 'NUMBER_OF_PROCESSORS', 'OS')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) { $environment[$name] = $value }
    }
    foreach ($entry in $Values.GetEnumerator()) {
        $environment[[string]$entry.Key] = [string]$entry.Value
    }
    return $environment
}

function Assert-SafetyToolHashes {
    if ((Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne
            $ExpectedRunnerSha256 -or
        (Get-FileHash -LiteralPath $proxyScript -Algorithm SHA256).Hash.ToLowerInvariant() -ne
            $ExpectedProxySha256 -or
        (Get-FileHash -LiteralPath $stateScript -Algorithm SHA256).Hash.ToLowerInvariant() -ne
            $ExpectedStateToolSha256) {
        throw 'A safety tool SHA-256 does not match the authorization.'
    }
}

function Assert-RunToolHashes {
    if ((Get-FileHash -LiteralPath $runProxyScript -Algorithm SHA256).Hash.ToLowerInvariant() -ne
            $ExpectedProxySha256 -or
        (Get-FileHash -LiteralPath $runStateScript -Algorithm SHA256).Hash.ToLowerInvariant() -ne
            $ExpectedStateToolSha256) {
        throw 'A restricted runtime tool SHA-256 does not match the authorization.'
    }
}

function Assert-PythonHash {
    if ((Get-FileHash -LiteralPath $python -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        $ExpectedPythonExeSha256) {
        throw 'The Python executable SHA-256 no longer matches the authorization.'
    }
}

function Assert-RepositoryAndToolIdentity {
    if ($PSVersionTable.PSVersion -lt [Version]'7.4') {
        throw 'PowerShell 7.4 or later is required for isolated child environments.'
    }
    $repository = Split-Path -Parent $PSScriptRoot
    $head = (& git -C $repository rev-parse HEAD).Trim()
    $status = @(& git -C $repository status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $head -ne $ExpectedProductCommit -or $status.Count -ne 0) {
        throw 'The safety tools must run from the exact clean authorized repository commit.'
    }
    Assert-SafetyToolHashes
    $pythonLauncher = @(Get-Command python -CommandType Application -ErrorAction Stop |
        Where-Object {
            try {
                [void](Get-FileHash -LiteralPath $_.Source -Algorithm SHA256 -ErrorAction Stop)
                $true
            }
            catch { $false }
        } | Select-Object -First 1 -ExpandProperty Source)[0]
    if ([string]::IsNullOrWhiteSpace($pythonLauncher)) {
        throw 'No hashable Python launcher is available.'
    }
    $pythonPathOut = Join-Path $root 'python-executable.txt'
    $pythonPathError = Join-Path $root 'python-executable.stderr.log'
    $pythonResolver = Start-Process -FilePath $pythonLauncher `
        -ArgumentList @('-I', '-S', '-B', '-c', "__import__('sys').stdout.write(__import__('sys').executable)") `
        -WorkingDirectory $root -RedirectStandardOutput $pythonPathOut `
        -RedirectStandardError $pythonPathError -WindowStyle Hidden -PassThru -Wait `
        -Environment (New-ChildEnvironment @{})
    if ($pythonResolver.ExitCode -ne 0) {
        throw 'Unable to resolve the isolated Python executable.'
    }
    $resolvedPython = [IO.Path]::GetFullPath(
        [IO.File]::ReadAllText($pythonPathOut).Trim())
    [IO.File]::Delete($pythonPathOut)
    [IO.File]::Delete($pythonPathError)
    if ((Get-FileHash -LiteralPath $resolvedPython -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        $ExpectedPythonExeSha256) {
        throw 'The Python executable SHA-256 does not match the authorization.'
    }
    $pythonSourceRoot = Split-Path -Parent $resolvedPython
    if ((Get-DirectoryContentSha256 $pythonSourceRoot) -ne $ExpectedPythonRuntimeSha256) {
        throw 'The Python runtime tree does not match the authorization.'
    }
    Copy-Item -LiteralPath $pythonSourceRoot -Destination $pythonRuntimeRoot -Recurse
    if ((Get-DirectoryContentSha256 $pythonRuntimeRoot) -ne $ExpectedPythonRuntimeSha256) {
        throw 'The restricted Python runtime copy does not match the authorization.'
    }
    $script:python = Join-Path $pythonRuntimeRoot ([IO.Path]::GetFileName($resolvedPython))
    $script:toolIdentityVerified = $true
}

function Get-DirectoryContentSha256([string] $Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (Get-ChildItem -LiteralPath $resolved -Force -Recurse |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
        throw "Artifact directory contains a reparse point: $resolved"
    }
    $lines = @(Get-ChildItem -LiteralPath $resolved -File -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($resolved, $_.FullName).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$relative`0$($_.Length)`0$hash"
        })
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($lines -join "`n")
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Assert-MesIngestContract([string] $BaseUrl) {
    $expected = [ordered]@{
        CONTRACT_DISCOVERY = '2.0'
        CURRENT_INGEST_ATTENTION = '2.1'
        DEMAND_SERIES = '2.1'
        ERROR_SEARCH = '2.2'
        EXTERNALLY_READABLE_DEMAND_CATALOG = '2.1'
        POLL_HEALTH_AND_EVIDENCE = '2.0'
        READABILITY_AUDIT = '2.0'
        SERIES_ERROR_CATALOG = '2.0'
        SUBLOT_BOX_COUNT = '1.0'
        WATCH_OVERVIEW = '2.0'
    }
    $contract = Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/api/v2/contract" -TimeoutSec 5
    $actual = @{}
    foreach ($capability in @($contract.capabilities)) {
        $actual[[string]$capability.id] = [string]$capability.version
    }
    $matches =
        [string]$contract.contractVersion -eq '2026.08.new-mes-ingest.v2.4' -and
        [int]$contract.schemaVersion -eq 29 -and
        $actual.Count -eq $expected.Count -and
        @($expected.Keys | Where-Object {
            -not $actual.ContainsKey($_) -or $actual[$_] -ne $expected[$_]
        }).Count -eq 0
    if (-not $matches) {
        throw 'MesIngest exact v2.4 contract preflight failed before runtime startup.'
    }
}

function Read-SafetyProjection {
    if ([string]::IsNullOrWhiteSpace($script:expectedVehicleKey) -or
        $script:maximumSafetyEvidenceAge -le [TimeSpan]::Zero) {
        throw 'Expected vehicle safety identity and freshness are not configured.'
    }
    $credential = [Environment]::GetEnvironmentVariable(
        'CONTROL_SERVER_ONBOARD_CREDENTIAL',
        [EnvironmentVariableTarget]::Machine)
    if ([string]::IsNullOrWhiteSpace($credential)) {
        throw 'Machine-scope Onboard credential is unavailable.'
    }
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds(4)
    $client.DefaultRequestHeaders.Authorization =
        [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $credential)
    $response = $null
    try {
        $response = $client.GetAsync(
            'http://localhost:58007/api/onboard/v1/vehicle-safety').GetAwaiter().GetResult()
        if ([int]$response.StatusCode -ne 200) {
            throw "Installed safety endpoint returned HTTP $([int]$response.StatusCode)."
        }
        $rawBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $document = [Text.Json.JsonDocument]::Parse($rawBody)
        try {
            $rootElement = $document.RootElement
            $vehicleKey = $rootElement.GetProperty('vehicleKey').GetString()
            $source = $rootElement.GetProperty('source').GetString()
            $motionState = $rootElement.GetProperty('motionState').GetString()
            $observedAtText = $rootElement.GetProperty('observedAt').GetString()
            $reasonCodes = $rootElement.GetProperty('reasonCodes')
            if ($reasonCodes.ValueKind -ne [Text.Json.JsonValueKind]::Array -or
                -not [regex]::IsMatch(
                    $observedAtText,
                    '(?:Z|[+-]\d{2}:\d{2})$',
                    [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
                throw 'Installed safety projection has an invalid reason or timestamp shape.'
            }
            $reasonCount = $reasonCodes.GetArrayLength()
        }
        finally {
            $document.Dispose()
        }
        $observedAt = [DateTimeOffset]::Parse(
            $observedAtText,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None)
        $now = [DateTimeOffset]::UtcNow
        if (-not [string]::Equals(
                $vehicleKey,
                $script:expectedVehicleKey,
                [StringComparison]::Ordinal) -or
            -not [string]::Equals(
                $source,
                'RIOT_BEHAVIOR_LAB_R41',
                [StringComparison]::Ordinal) -or
            $observedAt -gt $now -or
            ($now - $observedAt) -gt $script:maximumSafetyEvidenceAge -or
            -not [string]::Equals(
                $motionState,
                'STOPPED',
                [StringComparison]::Ordinal) -or
            $reasonCount -ne 0) {
            throw 'Installed safety projection has the wrong identity, source, freshness, or stop state.'
        }
        return [pscustomobject]@{
            vehicleKey = $vehicleKey
            motionState = $motionState
            reasonCount = $reasonCount
            source = $source
            observedAt = $observedAt.ToString('O')
        }
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $client.Dispose()
        $handler.Dispose()
        $credential = $null
    }
}

function Assert-SafetyStopped {
    [void](Read-SafetyProjection)
    $script:safetySampleCount++
}

function Invoke-EffectiveStateInspection {
    $inspectTask = '8005 AGV ControlServer - Inspect Effective State'
    $before = (Get-ScheduledTaskInfo -TaskName $inspectTask).LastRunTime
    Start-ScheduledTask -TaskName $inspectTask
    $deadline = [DateTimeOffset]::Now.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $task = Get-ScheduledTask -TaskName $inspectTask
        $taskInfo = Get-ScheduledTaskInfo -TaskName $inspectTask
    } while (($taskInfo.LastRunTime -le $before -or $task.State -ne 'Ready') -and
        [DateTimeOffset]::Now -lt $deadline)
    if ($taskInfo.LastRunTime -le $before -or $taskInfo.LastTaskResult -ne 0 -or
        $task.State -ne 'Ready') {
        throw 'The fixed effective-state inspection did not complete successfully.'
    }
    return [IO.File]::ReadAllText(
        (Join-Path $env:TEMP 'controlserver-operator-effective-state.json')) | ConvertFrom-Json
}

function Assert-InstalledSafeState($Effective) {
    if ($Effective.baseJourneyRuntimeEnabled -isnot [bool] -or
        $Effective.productionJourneyRuntimeEnabled -isnot [bool] -or
        $Effective.serviceAccountIsLocalSystem -isnot [bool] -or
        $Effective.bothRequiredPortsPresent -isnot [bool] -or
        $Effective.requiredPortsOwnedOnlyByService -isnot [bool] -or
        [int]$Effective.schemaVersion -ne 1 -or
        [string]$Effective.result -ne 'PASS' -or
        $Effective.baseJourneyRuntimeEnabled -ne $false -or
        $Effective.productionJourneyRuntimeEnabled -ne $false -or
        [int]$Effective.minimumBatteryPercent -ne 10 -or
        [string]$Effective.serviceState -ne 'Running' -or
        [string]$Effective.serviceStartMode -ne 'Auto' -or
        $Effective.serviceAccountIsLocalSystem -ne $true -or
        $Effective.bothRequiredPortsPresent -ne $true -or
        $Effective.requiredPortsOwnedOnlyByService -ne $true -or
        [string]$Effective.liveStatus -ne 'live' -or
        [string]$Effective.privilegeBroker -ne 'FIXED_SYSTEM_SCHEDULED_TASK') {
        throw 'The installed ControlServer is not in the required disabled safe state.'
    }
}

function Get-InstalledEffectiveStateSha256($Effective) {
    $canonical = [ordered]@{
        schemaVersion = [int]$Effective.schemaVersion
        result = [string]$Effective.result
        baseJourneyRuntimeEnabled = [bool]$Effective.baseJourneyRuntimeEnabled
        productionJourneyRuntimeEnabled = [bool]$Effective.productionJourneyRuntimeEnabled
        minimumBatteryPercent = [int]$Effective.minimumBatteryPercent
        serviceState = [string]$Effective.serviceState
        serviceStartMode = [string]$Effective.serviceStartMode
        serviceAccountIsLocalSystem = [bool]$Effective.serviceAccountIsLocalSystem
        bothRequiredPortsPresent = [bool]$Effective.bothRequiredPortsPresent
        requiredPortsOwnedOnlyByService = [bool]$Effective.requiredPortsOwnedOnlyByService
        liveStatus = [string]$Effective.liveStatus
        privilegeBroker = [string]$Effective.privilegeBroker
    }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(
        ($canonical | ConvertTo-Json -Compress))
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Start-ExactPeers {
    param([string] $ServerHost, [int] $ServerPort, [int] $HealthPort)

    $onboardRoot = Join-Path $peerRoot 'onboard'
    $simulatorRoot = Join-Path $peerRoot 'simulator'
    Copy-Item -LiteralPath $OnboardSource -Destination $onboardRoot -Recurse
    Copy-Item -LiteralPath $SimulatorSource -Destination $simulatorRoot -Recurse
    if ((Get-DirectoryContentSha256 $onboardRoot) -ne $ExpectedOnboardArtifactSha256 -or
        (Get-DirectoryContentSha256 $simulatorRoot) -ne $ExpectedSimulatorArtifactSha256) {
        throw 'A copied peer artifact changed after source verification.'
    }

    $credential = [Environment]::GetEnvironmentVariable(
        'CONTROL_SERVER_ONBOARD_CREDENTIAL',
        [EnvironmentVariableTarget]::Machine)
    if ([string]::IsNullOrWhiteSpace($credential)) {
        throw 'Machine-scope Onboard credential is unavailable.'
    }

    $configPath = Join-Path $onboardRoot 'appsettings.json'
    $config = [IO.File]::ReadAllText($configPath) | ConvertFrom-Json
    $serviceConfig = [IO.File]::ReadAllText((Join-Path $hostRoot 'appsettings.json')) | ConvertFrom-Json
    $config.environment = 'AuthorizedExperiment'
    $config.agvId = [string]$serviceConfig.JourneyRuntime.agvId
    $config.wireToGate.enabled = $true
    $config.wireToGate.host = $ServerHost
    $config.wireToGate.port = $ServerPort
    $config.wireToGate.onboardBuildCommit = $expectedOnboardCommit
    $config.wireToGate.supportsBatchUnlock = $true
    # A TLS-era onboard build still carries these two keys and a plaintext one will not, so touch
    # them only where they exist.
    if ($config.wireToGate.PSObject.Properties.Name -contains 'useTls') {
        $config.wireToGate.useTls = $false
    }
    if ($config.wireToGate.PSObject.Properties.Name -contains 'serverCertificateSha256') {
        $config.wireToGate.PSObject.Properties.Remove('serverCertificateSha256')
    }
    $config.wireToGate.journalPath = Join-Path $peerRoot 'onboard-journal.db'
    $config.vehicleSafety.enabled = $true
    $config.vehicleSafety.endpoint = 'http://localhost:58007/api/onboard/v1/vehicle-safety'
    $config.vehicleSafety.expectedVehicleKey = [string]$serviceConfig.JourneyRuntime.vehicleKey
    $config.logging.directory = Join-Path $logRoot 'onboard'
    [IO.File]::WriteAllText(
        $configPath,
        ($config | ConvertTo-Json -Depth 30),
        [Text.UTF8Encoding]::new($false))

    # The simulator and the onboard client are both WPF -- the onboard one deliberately visible --
    # so from here to teardown this run owns the machine's single interactive desktop.
    # 8005-mes-ingest's golden renderer and desktop suite take the same machine-wide mutex, and
    # GitHub's per-repository `concurrency` cannot see across the two. See scripts/DesktopLock.psm1.
    #
    # Taken here rather than at the top of the run: everything above is hashing, ACL work and
    # preflight assertions, none of which touch the desktop, so a queued run waits holding nothing.
    $desktopLock = Enter-DesktopLock -Reason "authorized absent-observation shadow run $stamp"

    $simulatorArguments = @{
        FilePath = Join-Path $simulatorRoot 'SQCD_8005AGV_Simulator.exe'
        WorkingDirectory = $simulatorRoot
        WindowStyle = 'Hidden'
        PassThru = $true
        Environment = (New-ChildEnvironment @{})
    }
    $script:simulatorProcess = Start-Process @simulatorArguments
    Wait-ExactListeningPort 1502 $script:simulatorProcess 'SQCD_8005AGV_Simulator' 30
    Wait-ExactListeningPort 58006 $script:simulatorProcess 'SQCD_8005AGV_Simulator' 30
    $health = Invoke-RestMethod -Uri 'http://127.0.0.1:58006/api/v1/health' -TimeoutSec 3
    if ([string]$health.status -ne 'READY') { throw 'Simulator is not READY.' }

    $onboardArguments = @{
        FilePath = Join-Path $onboardRoot 'SQCD.Agv.Wpf.exe'
        WorkingDirectory = $onboardRoot
        WindowStyle = 'Normal'
        PassThru = $true
        Environment = (New-ChildEnvironment @{
            CONTROL_SERVER_ONBOARD_CREDENTIAL = $credential
        })
    }
    $script:onboardProcess = Start-Process @onboardArguments
    $credential = $null

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
        Assert-ExactListeningPort $proxyPort $proxyProcess 'python'
        Assert-ExactListeningPort $sessionPort $hostProcess 'ControlServer.Host'
        Assert-ExactListeningPort $healthPort $hostProcess 'ControlServer.Host'
        Assert-ExactListeningPort 1502 $simulatorProcess 'SQCD_8005AGV_Simulator'
        Assert-ExactListeningPort 58006 $simulatorProcess 'SQCD_8005AGV_Simulator'
        if ($script:onboardProcess.HasExited) {
            throw "Onboard exited with code $($script:onboardProcess.ExitCode)."
        }
        $readySession = $null
        try {
            $sessions = @(Invoke-RestMethod -Uri "http://127.0.0.1:$HealthPort/api/runtime/sessions" -TimeoutSec 3)
            $readySession = @($sessions | Where-Object {
                $_.agvId -eq [string]$serviceConfig.JourneyRuntime.agvId -and
                $_.readiness -eq 'Ready' -and
                $_.reasonCode -eq 'READY'
            } | Select-Object -Last 1)[0]
        }
        catch {
        }
        if ($null -ne $readySession) {
            $script:sessionGeneration = [long]$readySession.sessionGeneration
            Assert-SafetyStopped
            return
        }
    }
    throw 'Onboard session did not become Ready on the isolated shadow instance.'
}

function Stop-ExactProcess($Process, [string] $ExpectedName) {
    if ($null -eq $Process) { return }
    $Process.Refresh()
    if ($Process.HasExited) {
        $Process.Dispose()
        return
    }
    if ($Process.ProcessName -ne $ExpectedName) {
        throw "PID $($Process.Id) no longer belongs to $ExpectedName."
    }
    $Process.Kill($true)
    if (-not $Process.WaitForExit(10000)) {
        throw "$ExpectedName PID $($Process.Id) did not exit during cleanup."
    }
    $Process.Dispose()
}

try {
    if ($runRootPreexisting) {
        throw 'The cryptographically unique run root already exists.'
    }
    Ensure-RestrictedRunRoot
    Assert-RepositoryAndToolIdentity
    [IO.Directory]::CreateDirectory($logRoot) | Out-Null
    [IO.Directory]::CreateDirectory($privateRoot) | Out-Null
    [IO.Directory]::CreateDirectory($toolRoot) | Out-Null
    Assert-RestrictedDirectoryAcl $root
    Copy-Item -LiteralPath $proxyScript -Destination $runProxyScript
    Copy-Item -LiteralPath $stateScript -Destination $runStateScript
    Assert-RunToolHashes

    foreach ($port in @($proxyPort, $sessionPort, $healthPort) + $simulatorPorts) {
        Assert-PortFree $port
    }
    if (-not (Test-Path -LiteralPath $stopMarker -PathType Leaf)) {
        throw 'The installed journey stop marker is missing.'
    }
    if (-not (Test-Path -LiteralPath $productionDatabase -PathType Leaf)) {
        throw 'The production ControlServer database is unavailable for overlap checks.'
    }
    foreach ($path in @($PackagePath, $OnboardSource, $SimulatorSource, $proxyScript, $stateScript)) {
        if (-not (Test-Path -LiteralPath $path)) { throw "Required path is missing: $path" }
    }
    if ((Get-DirectoryContentSha256 $OnboardSource) -ne $ExpectedOnboardArtifactSha256) {
        throw 'The Onboard artifact tree does not match the authorized SHA-256.'
    }
    if ((Get-DirectoryContentSha256 $SimulatorSource) -ne $ExpectedSimulatorArtifactSha256) {
        throw 'The simulator artifact tree does not match the authorized SHA-256.'
    }

    $manifestPath = Join-Path $PackagePath 'deployment-manifest.json'
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    $manifestSha = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]$manifest.sourceCommit -ne $ExpectedProductCommit -or
        $manifestSha -ne $ExpectedManifestSha256) {
        throw 'The exact ControlServer package identity does not match the authorization.'
    }
    $packageRoot = [IO.Path]::GetFullPath($PackagePath)
    if (Get-ChildItem -LiteralPath $packageRoot -Force -Recurse |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
        throw 'The ControlServer package contains a reparse point.'
    }
    $packagePrefix = $packageRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $listed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($manifest.files)) {
        $candidate = [IO.Path]::GetFullPath((Join-Path $packageRoot ([string]$file.path)))
        if (-not $candidate.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not $listed.Add([IO.Path]::GetRelativePath($packageRoot, $candidate))) {
            throw 'The ControlServer package manifest has an escaping or duplicate path.'
        }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf) -or
            (Get-Item -LiteralPath $candidate).Length -ne [long]$file.length -or
            (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant() -ne
                [string]$file.sha256) {
            throw 'A ControlServer package file does not match its manifest.'
        }
    }
    $actualFiles = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse |
        Where-Object { $_.FullName -ne [IO.Path]::GetFullPath($manifestPath) })
    $unlistedFiles = @($actualFiles | Where-Object {
        -not $listed.Contains([IO.Path]::GetRelativePath($packageRoot, $_.FullName))
    })
    if ($actualFiles.Count -ne $listed.Count -or $unlistedFiles.Count -ne 0) {
        throw 'The ControlServer package contains files outside its manifest.'
    }
    $packageTreeSha256 = Get-DirectoryContentSha256 $packageRoot
    $packageSettings = [IO.File]::ReadAllText(
        (Join-Path $PackagePath 'appsettings.json')) | ConvertFrom-Json
    $expectedVehicleKey = [string]$packageSettings.JourneyRuntime.vehicleKey
    $maximumSafetyEvidenceAge = [TimeSpan]::Parse(
        [string]$packageSettings.JourneyRuntime.maximumEvidenceAge,
        [Globalization.CultureInfo]::InvariantCulture)
    if ([string]::IsNullOrWhiteSpace($expectedVehicleKey) -or
        $maximumSafetyEvidenceAge -le [TimeSpan]::Zero) {
        throw 'The package does not define a valid vehicle safety identity and freshness limit.'
    }
    Assert-MesIngestContract -BaseUrl ([string]$packageSettings.MesIngest.baseUrl)
    $mesIngestContractPreflightPassed = $true

    $installedStateBefore = Invoke-EffectiveStateInspection
    Assert-InstalledSafeState $installedStateBefore
    $installedEffectiveStateBeforeSha256 = Get-InstalledEffectiveStateSha256 $installedStateBefore
    Assert-SafetyStopped
    $productionDatabaseBefore = Get-FileBundleSha256 @(
        $productionDatabase,
        "$productionDatabase-wal",
        "$productionDatabase-shm")
    $riotApiKey = Get-RequiredMachineSecret 'CONTROL_SERVER_RIOT_CALL_API_KEY'
    $onboardCredential = Get-RequiredMachineSecret 'CONTROL_SERVER_ONBOARD_CREDENTIAL'

    Copy-Item -LiteralPath $PackagePath -Destination $hostRoot -Recurse
    if ((Get-DirectoryContentSha256 $hostRoot) -ne $packageTreeSha256) {
        throw 'The copied ControlServer package changed after source verification.'
    }
    $settingsPath = Join-Path $hostRoot 'appsettings.json'
    $settings = [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
    $settings.Health.url = "http://127.0.0.1:$healthPort"
    $settings.OnboardTransport.port = $sessionPort
    $settings.ConnectionStrings.ControlServer = "Data Source=$databasePath"
    $settings.RIoT.baseUrl = "http://127.0.0.1:$proxyPort"
    $settings.RIoT.callApiKeyEnvironmentVariable = 'CONTROL_SERVER_SHADOW_DUMMY_RIOT_CALL_API_KEY'
    $settings.OnboardSafetyProjection.enabled = $false
    $settings.JourneyRuntime.enabled = $true
    $settings.JourneyRuntime.minimumBatteryPercent = 10
    $settings.RiotAbsentAtObservationCreateExperiment.enabled = $false
    [IO.File]::WriteAllText(
        $settingsPath,
        ($settings | ConvertTo-Json -Depth 100),
        [Text.UTF8Encoding]::new($false))

    Assert-SafetyToolHashes
    Assert-RunToolHashes
    Assert-PythonHash
    $proxySelfTest = Start-Process -FilePath $python `
        -ArgumentList @('-I', '-S', '-B', $runProxyScript, '--self-test') `
        -WorkingDirectory $root `
        -RedirectStandardOutput (Join-Path $logRoot 'proxy-self-test.stdout.log') `
        -RedirectStandardError (Join-Path $logRoot 'proxy-self-test.stderr.log') `
        -WindowStyle Hidden -PassThru -Wait `
        -Environment (New-ChildEnvironment @{})
    if ($proxySelfTest.ExitCode -ne 0) { throw 'The read-only proxy self-test failed.' }

    if ($PreflightOnly) {
        $riotApiKey = $null
        $onboardCredential = $null
        $preflightComplete = $true
        throw [OperationCanceledException]::new('PREFLIGHT_ONLY_COMPLETE')
    }

    $dummyRiotKey = [Convert]::ToHexString(
        [Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $proxyArguments = @{
        FilePath = $python
        ArgumentList = @(
            '-I', '-S', '-B', $runProxyScript,
            '--listen-port', $proxyPort,
            '--upstream', 'http://172.19.206.222:8888',
            '--map-id', [int]$settings.JourneyRuntime.mapId,
            '--vehicle-key', [string]$settings.JourneyRuntime.vehicleKey,
            '--dispatch-generation', [long]$settings.JourneyRuntime.dispatchGeneration)
        WorkingDirectory = $root
        RedirectStandardOutput = Join-Path $logRoot 'proxy.stdout.log'
        RedirectStandardError = Join-Path $logRoot 'proxy.stderr.log'
        WindowStyle = 'Hidden'
        PassThru = $true
        Environment = (New-ChildEnvironment @{
            AUTHORIZED_EXPERIMENT_RIOT_CALL_API_KEY = $riotApiKey
            AUTHORIZED_EXPERIMENT_PROXY_CLIENT_TOKEN = $dummyRiotKey
        })
    }
    Assert-PythonHash
    Assert-RunToolHashes
    $proxyProcess = Start-Process @proxyArguments
    $riotApiKey = $null
    Wait-ExactListeningPort $proxyPort $proxyProcess 'python' 15

    $hostEnvironment = New-ChildEnvironment @{
        DOTNET_ENVIRONMENT = 'AuthorizedExperiment'
        ASPNETCORE_ENVIRONMENT = 'AuthorizedExperiment'
        CONTROL_SERVER_SHADOW_DUMMY_RIOT_CALL_API_KEY = $dummyRiotKey
        CONTROL_SERVER_ONBOARD_CREDENTIAL = $onboardCredential
        ConnectionStrings__ControlServer = "Data Source=$databasePath"
        RIoT__baseUrl = "http://127.0.0.1:$proxyPort"
        RIoT__callApiKeyEnvironmentVariable = 'CONTROL_SERVER_SHADOW_DUMMY_RIOT_CALL_API_KEY'
        Health__url = "http://127.0.0.1:$healthPort"
        OnboardTransport__port = [string]$sessionPort
        OnboardTransport__credentialEnvironmentVariable = 'CONTROL_SERVER_ONBOARD_CREDENTIAL'
        OnboardSafetyProjection__enabled = 'false'
        JourneyRuntime__enabled = 'true'
        JourneyRuntime__minimumBatteryPercent = '10'
        RiotAbsentAtObservationCreateExperiment__enabled = 'false'
        MesIngest__baseUrl = [string]$settings.MesIngest.baseUrl
    }
    if (-not [string]::IsNullOrWhiteSpace(
            [string]$hostEnvironment['CONTROL_SERVER_RIOT_CALL_API_KEY'])) {
        throw 'The Host environment unexpectedly contains the real RIoT credential name.'
    }
    $hostIsolationConfigured = $true
    $hostArguments = @{
        FilePath = Join-Path $hostRoot 'ControlServer.Host.exe'
        WorkingDirectory = $hostRoot
        RedirectStandardOutput = Join-Path $logRoot 'host.stdout.log'
        RedirectStandardError = Join-Path $logRoot 'host.stderr.log'
        WindowStyle = 'Hidden'
        PassThru = $true
        Environment = $hostEnvironment
    }
    $hostProcess = Start-Process @hostArguments
    $hostEverStarted = $true
    $hostEnvironment = $null
    $onboardCredential = $null
    $dummyRiotKey = $null
    Wait-ExactListeningPort $sessionPort $hostProcess 'ControlServer.Host' 30
    Wait-ExactListeningPort $healthPort $hostProcess 'ControlServer.Host' 30
    Assert-ExactListeningPort $proxyPort $proxyProcess 'python'
    if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf)) {
        throw 'The isolated Host did not create the fresh shadow database.'
    }
    if ((Get-FileBundleSha256 @(
            $productionDatabase,
            "$productionDatabase-wal",
            "$productionDatabase-shm")) -ne $productionDatabaseBefore) {
        throw 'The production database changed during isolated Host startup.'
    }
    $live = Invoke-RestMethod -Uri "http://127.0.0.1:$healthPort/health/live" -TimeoutSec 5
    if ([string]$live.status -ne 'live') { throw 'Isolated shadow ControlServer is not live.' }

    Start-ExactPeers -ServerHost 'localhost' -ServerPort $sessionPort -HealthPort $healthPort

    $extractDeadline = [DateTimeOffset]::UtcNow.AddSeconds(150)
    $extracted = $false
    do {
        Start-Sleep -Seconds 1
        Assert-ExactListeningPort $proxyPort $proxyProcess 'python'
        Assert-ExactListeningPort $sessionPort $hostProcess 'ControlServer.Host'
        Assert-ExactListeningPort $healthPort $hostProcess 'ControlServer.Host'
        Assert-ExactListeningPort 1502 $simulatorProcess 'SQCD_8005AGV_Simulator'
        Assert-ExactListeningPort 58006 $simulatorProcess 'SQCD_8005AGV_Simulator'
        Assert-SafetyStopped
        Assert-SafetyToolHashes
        Assert-RunToolHashes
        Assert-PythonHash
        $stateArguments = @(
            '-I', '-S', '-B', $runStateScript,
            'extract',
            '--shadow-db', $databasePath,
            '--production-db', $productionDatabase,
            '--private-out', $privatePlanPath,
            '--sanitized-out', $sanitizedPlanPath)
        $stateProcess = Start-Process -FilePath $python -ArgumentList $stateArguments `
            -WorkingDirectory $root -WindowStyle Hidden -PassThru `
            -Environment (New-ChildEnvironment @{})
        if (-not $stateProcess.WaitForExit(5000)) {
            $stateProcess.Kill($true)
            if (-not $stateProcess.WaitForExit(10000)) {
                throw 'The isolated state helper could not be stopped.'
            }
            $stateProcess.Dispose()
            $stateProcess = $null
            throw 'The isolated state helper did not exit within five seconds.'
        }
        $stateExitCode = $stateProcess.ExitCode
        $stateProcess.Dispose()
        $stateProcess = $null
        if ($stateExitCode -eq 0) { $extracted = $true; break }
        if ($hostProcess.HasExited) {
            throw "Isolated shadow ControlServer exited with code $($hostProcess.ExitCode)."
        }
    } while ([DateTimeOffset]::UtcNow -lt $extractDeadline)
    if (-not $extracted) {
        throw 'Shadow selection did not reach an exact, fresh AbsentAtObservation intent in time.'
    }

    $proxyStatus = Invoke-RestMethod -Uri "http://127.0.0.1:$proxyPort/_proxy/status" -TimeoutSec 3
    if ([int]$proxyStatus.forwardedReadCount -le 0 -or
        [int]$proxyStatus.blockedReadCount -ne 0 -or
        [int]$proxyStatus.forwardedMutationCount -ne 0 -or
        [int]$proxyStatus.blockedMutationCount -ne 0) {
        throw 'The shadow proxy did not prove an allowlisted, mutation-free selection pass.'
    }
    Assert-SafetyStopped
    $result = 'PASS'
}
catch {
    if ($preflightComplete -and
        $_.Exception -is [OperationCanceledException] -and
        [string]::Equals(
            $_.Exception.Message,
            'PREFLIGHT_ONLY_COMPLETE',
            [StringComparison]::Ordinal)) {
        $result = 'PREFLIGHT_PASS'
        $failure = $null
    }
    else {
        $failure = $_.Exception.Message
    }
}
finally {
    foreach ($processEntry in @(
        [pscustomobject]@{ Process = $stateProcess; Name = 'python' },
        [pscustomobject]@{ Process = $onboardProcess; Name = 'SQCD.Agv.Wpf' },
        [pscustomobject]@{ Process = $simulatorProcess; Name = 'SQCD_8005AGV_Simulator' },
        [pscustomobject]@{ Process = $hostProcess; Name = 'ControlServer.Host' })) {
        try { Stop-ExactProcess $processEntry.Process $processEntry.Name }
        catch { $cleanupFailures.Add("$($processEntry.Name) cleanup failed.") }
    }
    if ($null -ne $proxyProcess) {
        try {
            $proxyProcess.Refresh()
            if (-not $proxyProcess.HasExited -and $proxyProcess.ProcessName -eq 'python') {
                $proxyStatus = Invoke-RestMethod -Uri "http://127.0.0.1:$proxyPort/_proxy/status" -TimeoutSec 3
                $finalProxyStatusCaptured = $true
            }
            else {
                throw 'Proxy exited before final status capture.'
            }
        }
        catch { $cleanupFailures.Add('Proxy status capture failed.') }
    }
    if ($null -ne $hostProcess -and $cleanupFailures.Count -eq 0) {
        try {
            Assert-SafetyToolHashes
            Assert-RunToolHashes
            Assert-PythonHash
            $finalStateArguments = @(
                '-I', '-S', '-B', $runStateScript,
                'extract',
                '--shadow-db', $databasePath,
                '--production-db', $productionDatabase,
                '--private-out', $privatePlanPath,
                '--sanitized-out', $sanitizedPlanPath)
            $stateProcess = Start-Process -FilePath $python -ArgumentList $finalStateArguments `
                -WorkingDirectory $root -WindowStyle Hidden -PassThru `
                -Environment (New-ChildEnvironment @{})
            if (-not $stateProcess.WaitForExit(5000)) {
                $stateProcess.Kill($true)
                if (-not $stateProcess.WaitForExit(10000)) {
                    throw 'The final isolated state helper could not be stopped.'
                }
                throw 'The final isolated state helper did not exit within five seconds.'
            }
            if ($stateProcess.ExitCode -ne 0) {
                throw 'The stopped shadow database failed final permit extraction.'
            }
            $finalStateExtractionPassed = $true
        }
        catch { $cleanupFailures.Add('Final stopped-state extraction failed.') }
        finally {
            if ($null -ne $stateProcess) {
                try { Stop-ExactProcess $stateProcess 'python' }
                catch { $cleanupFailures.Add('Final state-helper cleanup failed.') }
                $stateProcess = $null
            }
        }
    }
    try { Stop-ExactProcess $proxyProcess 'python' }
    catch { $cleanupFailures.Add('python cleanup failed.') }
    try {
        if (Test-Path -LiteralPath $pythonRuntimeRoot -PathType Container) {
            $resolvedRunRoot = [IO.Path]::GetFullPath($root).TrimEnd(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            $resolvedPythonRuntime = [IO.Path]::GetFullPath($pythonRuntimeRoot)
            if (-not $resolvedPythonRuntime.StartsWith(
                    $resolvedRunRoot,
                    [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Refusing to remove a Python runtime outside the run root.'
            }
            [IO.Directory]::Delete($resolvedPythonRuntime, $true)
        }
    }
    catch { $cleanupFailures.Add('Restricted Python runtime cleanup failed.') }

    $portsStillListening = @(@($proxyPort, $sessionPort, $healthPort) + $simulatorPorts |
        Where-Object { Get-NetTCPConnection -State Listen -LocalPort $_ -ErrorAction SilentlyContinue })
    try {
        $productionDatabaseAfter = Get-FileBundleSha256 @(
            $productionDatabase,
            "$productionDatabase-wal",
            "$productionDatabase-shm")
    }
    catch { $cleanupFailures.Add('Production database post-check failed.') }
    try {
        $installedStateAfter = Invoke-EffectiveStateInspection
        Assert-InstalledSafeState $installedStateAfter
        $installedEffectiveStateAfterSha256 = Get-InstalledEffectiveStateSha256 $installedStateAfter
    }
    catch { $cleanupFailures.Add('Installed ControlServer post-check failed.') }
    try { Assert-SafetyStopped }
    catch { $cleanupFailures.Add('Final independent safety sample failed.') }

    $productionDatabaseModified = if (
        $null -eq $productionDatabaseBefore -or $null -eq $productionDatabaseAfter) {
        $null
    } else { $productionDatabaseBefore -ne $productionDatabaseAfter }
    $installedJourneyRuntimeChanged = if (
        $null -eq $installedEffectiveStateBeforeSha256 -or
        $null -eq $installedEffectiveStateAfterSha256 -or
        $null -eq $installedStateBefore -or $null -eq $installedStateAfter) {
        $null
    } else {
        $installedEffectiveStateBeforeSha256 -ne $installedEffectiveStateAfterSha256 -or
        [bool]$installedStateBefore.baseJourneyRuntimeEnabled -or
        [bool]$installedStateBefore.productionJourneyRuntimeEnabled -or
        [bool]$installedStateAfter.baseJourneyRuntimeEnabled -or
        [bool]$installedStateAfter.productionJourneyRuntimeEnabled
    }
    $temporaryPortsReleased = $portsStillListening.Count -eq 0
    $noRiotMutationProven = if (-not $hostEverStarted) {
        $true
    } else {
        $toolIdentityVerified -and $hostIsolationConfigured -and -not $boundaryViolation -and
        $boundaryCheckCount -gt 0 -and $temporaryPortsReleased -and
        $finalProxyStatusCaptured -and $null -ne $proxyStatus -and
        [int]$proxyStatus.forwardedMutationCount -eq 0
    }
    $finalProxyPass = $finalProxyStatusCaptured -and
        $null -ne $proxyStatus -and
        [int]$proxyStatus.forwardedReadCount -gt 0 -and
        [int]$proxyStatus.blockedReadCount -eq 0 -and
        [int]$proxyStatus.forwardedMutationCount -eq 0 -and
        [int]$proxyStatus.blockedMutationCount -eq 0
    if ($cleanupFailures.Count -ne 0 -or -not $temporaryPortsReleased -or
        $productionDatabaseModified -ne $false -or
        $installedJourneyRuntimeChanged -ne $false -or
        ($result -eq 'PASS' -and (-not $finalProxyPass -or -not $finalStateExtractionPassed)) -or
        ($result -eq 'PASS' -and -not $noRiotMutationProven)) {
        $result = 'FAIL'
        if ([string]::IsNullOrWhiteSpace($failure)) {
            $failure = 'One or more post-run safety facts were not positively proven.'
        }
    }
    if ($result -ne 'PASS' -and (Test-Path -LiteralPath $privatePlanPath -PathType Leaf)) {
        [IO.File]::Delete($privatePlanPath)
    }

    if ($runRootPreexisting) {
        throw 'Refusing to write evidence into a pre-existing run root.'
    }
    try { Assert-RestrictedDirectoryAcl $root }
    catch { throw 'The run root is not secure enough for final evidence.' }
    $sanitized = if (Test-Path -LiteralPath $sanitizedPlanPath) {
        [IO.File]::ReadAllText($sanitizedPlanPath) | ConvertFrom-Json
    } else { $null }
    $final = [ordered]@{
        schemaVersion = 2
        result = $result
        preflightOnly = [bool]$PreflightOnly
        completedAt = [DateTimeOffset]::UtcNow
        runRoot = $root
        failure = $failure
        productCommit = $ExpectedProductCommit
        packageManifestSha256 = $ExpectedManifestSha256
        runnerSha256 = $ExpectedRunnerSha256
        proxySha256 = $ExpectedProxySha256
        stateToolSha256 = $ExpectedStateToolSha256
        pythonExeSha256 = $ExpectedPythonExeSha256
        pythonRuntimeSha256 = $ExpectedPythonRuntimeSha256
        onboardCommit = $ExpectedOnboardCommit
        onboardArtifactSha256 = $ExpectedOnboardArtifactSha256
        simulatorCommit = $ExpectedSimulatorCommit
        simulatorArtifactSha256 = $ExpectedSimulatorArtifactSha256
        configurationSha256 = if (Test-Path -LiteralPath (Join-Path $hostRoot 'appsettings.json')) {
            (Get-FileHash -LiteralPath (Join-Path $hostRoot 'appsettings.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        } else { $null }
        toolIdentityVerified = $toolIdentityVerified
        runRootRestricted = $runRootRestricted
        hostReceivedRealRiotCredential = if ($hostEverStarted) { $false } else { $null }
        sessionGeneration = $sessionGeneration
        safetySampleCount = $safetySampleCount
        boundaryCheckCount = $boundaryCheckCount
        finalProxyStatusCaptured = $finalProxyStatusCaptured
        finalStateExtractionPassed = $finalStateExtractionPassed
        mesIngestContractPreflightPassed = $mesIngestContractPreflightPassed
        exactAbsentAtObservation = if ($null -eq $sanitized) { $null } else { [bool]$sanitized.exactAbsentAtObservation }
        productionIdentityOverlap = if ($null -eq $sanitized) { $null } else { [bool]$sanitized.productionIdentityOverlap }
        selectionIdentitySha256 = if ($null -eq $sanitized) { $null } else { [string]$sanitized.selectionIdentitySha256 }
        proxyForwardedReadCount = if ($null -eq $proxyStatus) { $null } else { [int]$proxyStatus.forwardedReadCount }
        proxyBlockedReadCount = if ($null -eq $proxyStatus) { $null } else { [int]$proxyStatus.blockedReadCount }
        proxyBlockedMutationCount = if ($null -eq $proxyStatus) { $null } else { [int]$proxyStatus.blockedMutationCount }
        proxyForwardedMutationCount = if ($null -eq $proxyStatus) { $null } else { [int]$proxyStatus.forwardedMutationCount }
        installedJourneyRuntimeChanged = $installedJourneyRuntimeChanged
        installedEffectiveStateBeforeSha256 = $installedEffectiveStateBeforeSha256
        installedEffectiveStateAfterSha256 = $installedEffectiveStateAfterSha256
        productionDatabaseModified = $productionDatabaseModified
        realRiotMutationPerformed = if ($noRiotMutationProven) { $false } else { $null }
        orderCreated = if ($noRiotMutationProven) { $false } else { $null }
        vehicleMoved = $null
        vehicleMovementObserved = if ($safetySampleCount -ge 2) { $false } else { $null }
        vehicleMovementCausedByRun = if ($noRiotMutationProven) { $false } else { $null }
        temporaryPortsReleased = $temporaryPortsReleased
        cleanupFailureCount = $cleanupFailures.Count
        authorizationReusable = if ($hostEverStarted) {
            $false
        } elseif ($result -eq 'PREFLIGHT_PASS') {
            $true
        } else { $null }
        rawIdentityIncluded = $false
        runArtifactsMayContainOperationalIdentity = $true
        credentialsIncluded = $false
    }
    [IO.File]::WriteAllText(
        $finalResultPath,
        ($final | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    [pscustomobject]$final | ConvertTo-Json -Depth 20

    # Last, after every peer has been stopped and every post-check has run. Releasing earlier would
    # hand the desktop to another repository while this run's WPF windows were still closing.
    Exit-DesktopLock -Handle $desktopLock
}

if ($result -notin @('PASS', 'PREFLIGHT_PASS')) { exit 1 }
