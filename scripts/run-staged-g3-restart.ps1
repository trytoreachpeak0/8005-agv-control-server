[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$StageRoot,
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,
    [string]$ControlServerRepository = (Split-Path -Parent $PSScriptRoot),
    [string]$OnboardRepository = 'https://github.com/trytoreachpeak0/8005-agv-onboard-hmi.git',
    [string]$SimulatorRepository = 'https://github.com/trytoreachpeak0/slots-simulator.git'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# This runner speaks plaintext loopback only. It never installs a temporary trust root, so it needs
# no interactive security-warning acknowledgement and can run unattended. Since ticket 03 stripped the
# certificate mechanism, run-staged-g3.ps1 is unattended on the same terms; this is no longer the one
# runner that is.
$controlPort = 58105
$healthPort = 58107
$modbusPort = 1502
$simulatorHttpPort = 58006
$agvId = 'AGV-8005-STAGED-G3-RESTART-01'
$onboardInstanceId = '2f2a2f01-3a58-4a2f-9b41-8b7f3f7b2c19'
$stableWindowSamples = 12
$runStartedAt = [DateTimeOffset]::UtcNow
$runId = $runStartedAt.ToString('yyyyMMddTHHmmssfffZ')

function Get-SharedCommitBinding {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The shared commit binding source does not exist: $Path"
    }
    $parseErrors = $null
    $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) {
        throw "The shared commit binding source does not parse: $Path"
    }
    if ($null -eq $ast.ParamBlock) {
        throw "The shared commit binding source has no param block: $Path"
    }

    $binding = [ordered]@{}
    foreach ($name in @('ControlServerCommit', 'OnboardCommit', 'SimulatorCommit', 'ProtocolCommit')) {
        $candidates = @($ast.ParamBlock.Parameters |
            Where-Object { $_.Name.VariablePath.UserPath -eq $name })
        if ($candidates.Count -ne 1) {
            throw "Expected exactly one `$$name parameter in $Path, found $($candidates.Count)."
        }
        $default = $candidates[0].DefaultValue
        if ($default -isnot [System.Management.Automation.Language.StringConstantExpressionAst]) {
            throw "`$$name in $Path must default to a literal string."
        }
        # -cnotmatch, not -notmatch: PowerShell's default regex comparison is case-insensitive, so the
        # plain form would accept an uppercase SHA-1 and quietly break the byte-for-byte identity match
        # this binding exists to guarantee.
        if ($default.Value -cnotmatch '^[0-9a-f]{40}$') {
            throw "`$$name in $Path must default to a lowercase full SHA-1: $($default.Value)"
        }
        $binding[$name] = $default.Value
    }
    return $binding
}

# The staged G3 runner owns the peer identity this repository validates against. This runner does not
# restate those commits: it reads them back out of that script's param block, so the two can never
# drift apart the way the previous standalone restart runner did. The path is deliberately NOT a
# parameter -- an override port is a drift port: pointing it at a copy carrying stale commits would
# make a whole run claim the wrong binding without failing.
$CommitBindingSource = Join-Path $PSScriptRoot 'run-staged-g3.ps1'
$commitBinding = Get-SharedCommitBinding -Path $CommitBindingSource
$ControlServerCommit = $commitBinding['ControlServerCommit']
$OnboardCommit = $commitBinding['OnboardCommit']
$SimulatorCommit = $commitBinding['SimulatorCommit']
$ProtocolCommit = $commitBinding['ProtocolCommit']
$commitBindingSourceSha256 =
    (Get-FileHash -LiteralPath $CommitBindingSource -Algorithm SHA256).Hash.ToLowerInvariant()

# This runner executes from the working tree rather than from an exact clone, so its own identity has
# to be read back. Read it before anything is written, and commit the runner before the run that will
# be archived: a run started from a dirty tree cannot report a trustworthy runner identity.
$runnerCommit = (& git -C $ControlServerRepository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "Unable to read the runner commit from $ControlServerRepository" }
$runnerWorktreeClean = @(& git -C $ControlServerRepository status --porcelain).Count -eq 0

if (Test-Path -LiteralPath $StageRoot) {
    throw "StageRoot must not already exist: $StageRoot"
}
if (Test-Path -LiteralPath $EvidenceRoot) {
    throw "EvidenceRoot must not already exist: $EvidenceRoot"
}
New-Item -ItemType Directory -Path $StageRoot, $EvidenceRoot | Out-Null

$sourcesRoot = Join-Path $StageRoot 'sources'
$publishRoot = Join-Path $StageRoot 'publish'
$runtimeRoot = Join-Path $StageRoot 'runtime'
$logsRoot = Join-Path $EvidenceRoot 'logs'
New-Item -ItemType Directory -Path $sourcesRoot, $publishRoot, $runtimeRoot, $logsRoot | Out-Null

$controlSource = Join-Path $sourcesRoot 'control-server'
$onboardSource = Join-Path $sourcesRoot 'onboard-hmi'
$simulatorSource = Join-Path $sourcesRoot 'slots-simulator'
$controlPublish = Join-Path $publishRoot 'control-server'
$onboardPublish = Join-Path $publishRoot 'onboard-hmi'
$simulatorPublish = Join-Path $publishRoot 'slots-simulator'
$controlDatabasePath = Join-Path $runtimeRoot 'controlserver.db'
$onboardJournalPath = Join-Path $runtimeRoot 'onboard-journal.db'

$commands = [System.Collections.Generic.List[object]]::new()

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$LogPath
    )

    $startedAt = [DateTimeOffset]::UtcNow
    Push-Location $WorkingDirectory
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    $output | Out-File -LiteralPath $LogPath -Encoding utf8NoBOM
    $commands.Add([ordered]@{
        name = $Name
        workingDirectory = $WorkingDirectory
        file = $FilePath
        arguments = $Arguments
        startedAtUtc = $startedAt
        exitCode = $exitCode
        log = [IO.Path]::GetRelativePath($EvidenceRoot, $LogPath).Replace('\', '/')
    })
    if ($exitCode -ne 0) {
        throw "$Name exited with code $exitCode. See $LogPath"
    }
    return @($output)
}

function New-ExactClone {
    param(
        [string]$Name,
        [string]$Repository,
        [string]$Destination,
        [string]$Commit,
        [string]$RemoteRef = ''
    )

    Invoke-LoggedCommand -Name "clone-$Name" -WorkingDirectory $sourcesRoot -FilePath 'git' `
        -Arguments @('-c', 'core.autocrlf=false', 'clone', '--no-hardlinks', '--no-checkout', $Repository, $Destination) `
        -LogPath (Join-Path $logsRoot "clone-$Name.log") | Out-Null
    & git -C $Destination config core.autocrlf false
    if ($LASTEXITCODE -ne 0) { throw "Unable to set core.autocrlf=false for $Name" }
    Invoke-LoggedCommand -Name "fetch-$Name" -WorkingDirectory $Destination -FilePath 'git' `
        -Arguments @('fetch', 'origin', '--prune', '--tags') `
        -LogPath (Join-Path $logsRoot "fetch-$Name.log") | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($RemoteRef)) {
        $remoteTip = (& git -C $Destination rev-parse $RemoteRef).Trim()
        if ($LASTEXITCODE -ne 0 -or $remoteTip -ne $Commit) {
            throw "$Name remote ref mismatch: $RemoteRef=$remoteTip, expected $Commit"
        }
    }
    Invoke-LoggedCommand -Name "checkout-$Name" -WorkingDirectory $Destination -FilePath 'git' `
        -Arguments @('checkout', '--detach', $Commit) `
        -LogPath (Join-Path $logsRoot "checkout-$Name.log") | Out-Null
    $actual = (& git -C $Destination rev-parse HEAD).Trim()
    $status = @(& git -C $Destination status --porcelain)
    if ($actual -ne $Commit -or $status.Count -ne 0) {
        throw "$Name exact checkout is not clean at $Commit"
    }
    return $actual
}

function Get-Sha256Text {
    param([Parameter(Mandatory)][string]$Text)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

function Wait-TcpPort {
    param([int]$Port, [int]$TimeoutSeconds = 45)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $client = [Net.Sockets.TcpClient]::new()
        try {
            if ($client.ConnectAsync('127.0.0.1', $Port).Wait(300) -and $client.Connected) {
                return
            }
        }
        catch {
            # The listener is not up yet; keep polling until the deadline.
        }
        finally {
            $client.Dispose()
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Timed out waiting for 127.0.0.1:$Port"
}

function Wait-HttpJson {
    param([string]$Uri, [int]$TimeoutSeconds = 45)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            return Invoke-RestMethod -Uri $Uri -TimeoutSec 2
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Timed out waiting for $Uri"
}

function Stop-ProcessSafely {
    param([Diagnostics.Process]$Process)
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force
            $Process.WaitForExit(5000) | Out-Null
        }
    }
    catch {
        # Continue cleanup for the other isolated peers.
    }
}

# Both peers are launched more than once, so the launch shape is a function rather than a repeated
# block: an ordinal that differs only in the log file name is what keeps the phases comparable.
function Start-ControlServer {
    param([Parameter(Mandatory)][int]$Ordinal)

    $process = Start-Process -FilePath 'dotnet' `
        -ArgumentList @(Join-Path $controlPublish 'ControlServer.Host.dll') `
        -WorkingDirectory $controlPublish `
        -RedirectStandardOutput (Join-Path $logsRoot "control-$Ordinal.out.log") `
        -RedirectStandardError (Join-Path $logsRoot "control-$Ordinal.err.log") `
        -Environment $controlEnvironment -WindowStyle Hidden -PassThru
    Wait-TcpPort -Port $controlPort
    Wait-TcpPort -Port $healthPort
    return $process
}

function Start-OnboardHmi {
    param([Parameter(Mandatory)][int]$Ordinal)

    return Start-Process -FilePath 'dotnet' `
        -ArgumentList @(Join-Path $onboardPublish 'SQCD.Agv.Wpf.dll') `
        -WorkingDirectory $onboardPublish `
        -RedirectStandardOutput (Join-Path $logsRoot "onboard-$Ordinal.out.log") `
        -RedirectStandardError (Join-Path $logsRoot "onboard-$Ordinal.err.log") `
        -Environment @{ 'CONTROL_SERVER_ONBOARD_CREDENTIAL' = $credential } `
        -WindowStyle Hidden -PassThru
}

function Read-SessionRow {
    try {
        $rows = Invoke-RestMethod -Uri "http://127.0.0.1:$healthPort/api/runtime/sessions" -TimeoutSec 2
    }
    catch {
        # The endpoint is unavailable while a ControlServer process is being replaced.
        return $null
    }
    return @($rows | Where-Object { $_.agvId -eq $agvId }) | Select-Object -First 1
}

function Wait-SessionGeneration {
    param([long]$GreaterThan = 0, [int]$TimeoutSeconds = 90)

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $row = Read-SessionRow
        if ($null -ne $row -and [long]$row.sessionGeneration -gt $GreaterThan) {
            return $row
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "Timed out waiting for a session generation greater than $GreaterThan"
}

# A single after-the-fact read cannot tell a stable session from one that silently reconnected and
# landed on the same number, so the window is sampled once a second and every sample is kept.
function Measure-StableWindow {
    param([Parameter(Mandatory)][int]$Samples)

    $observed = @()
    for ($index = 0; $index -lt $Samples; $index++) {
        Start-Sleep -Seconds 1
        $row = Read-SessionRow
        $observed += [ordered]@{
            sampledAtUtc = [DateTimeOffset]::UtcNow
            sessionGeneration = if ($null -eq $row) { $null } else { [long]$row.sessionGeneration }
            readiness = if ($null -eq $row) { $null } else { $row.readiness }
            reasonCode = if ($null -eq $row) { $null } else { $row.reasonCode }
        }
    }
    return $observed
}

function Get-FileFingerprint {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = [IO.Path]::GetRelativePath($StageRoot, $item.FullName).Replace('\', '/')
        creationTimeUtc = $item.CreationTimeUtc.ToString('O')
        length = $item.Length
    }
}

function Invoke-SqliteRows {
    param(
        [Parameter(Mandatory)][string]$DatabasePath,
        [Parameter(Mandatory)][string]$Sql,
        [Parameter(Mandatory)][string[]]$Columns
    )

    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$DatabasePath;Mode=ReadOnly")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $Sql
        $reader = $command.ExecuteReader()
        try {
            $rows = @()
            while ($reader.Read()) {
                $row = [ordered]@{}
                for ($index = 0; $index -lt $Columns.Length; $index++) {
                    $row[$Columns[$index]] = if ($reader.IsDBNull($index)) { $null } else { $reader.GetValue($index) }
                }
                $rows += $row
            }
            # Comma operator: a bare single-row result is unwrapped on return, and an ordered
            # dictionary indexed with [0] then yields its first *value* instead of the row.
            return ,$rows
        }
        finally {
            $reader.Dispose()
            $command.Dispose()
        }
    }
    finally {
        $connection.Dispose()
    }
}

function Invoke-SqliteScalarLong {
    param(
        [Parameter(Mandatory)][string]$DatabasePath,
        [Parameter(Mandatory)][string]$Sql
    )

    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$DatabasePath;Mode=ReadOnly")
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        try {
            $command.CommandText = $Sql
            return [long]$command.ExecuteScalar()
        }
        finally {
            $command.Dispose()
        }
    }
    finally {
        $connection.Dispose()
    }
}

# Read only while the owning process is stopped: both databases run in WAL mode, and a read-only
# handle must not be the one that has to recover an unclean write-ahead log.
function Read-ControlDatabase {
    if (-not (Test-Path -LiteralPath $controlDatabasePath -PathType Leaf)) { return $null }

    $sideEffectTables = @(
        'OrderIntents', 'AcceptedDemands', 'StationOperations', 'VehicleDispatchLeases',
        'RiotDispatchAuditEvents', 'UnloadBatches', 'TransportDemandCompletions',
        'RecoveryWorkflows', 'ExceptionRecoverySessions', 'HardwareRecoveryRecords')
    $sideEffectCounts = [ordered]@{}
    foreach ($table in $sideEffectTables) {
        $sideEffectCounts[$table] = Invoke-SqliteScalarLong -DatabasePath $controlDatabasePath `
            -Sql "SELECT COUNT(*) FROM $table"
    }

    return [ordered]@{
        file = Get-FileFingerprint -Path $controlDatabasePath
        inboxRows = Invoke-SqliteRows -DatabasePath $controlDatabasePath `
            -Sql 'SELECT MessageId, MessageType, ContentHash FROM ProtocolInbox ORDER BY ReceivedAt' `
            -Columns @('messageId', 'messageType', 'contentHash')
        sessionHelloRows = Invoke-SqliteRows -DatabasePath $controlDatabasePath `
            -Sql "SELECT MessageId, RequestJson, FirstResponseJson FROM ProtocolInbox WHERE MessageType = 'SessionHello' ORDER BY ReceivedAt" `
            -Columns @('messageId', 'requestJson', 'firstResponseJson')
        sessionRecoveryRows = Invoke-SqliteRows -DatabasePath $controlDatabasePath `
            -Sql 'SELECT AgvId, SessionGeneration, Readiness, ReasonCode, ForcedRecoveryGeneration, ProtocolCommit, ManifestSha256 FROM SessionRecoveries ORDER BY AgvId' `
            -Columns @('agvId', 'sessionGeneration', 'readiness', 'reasonCode', 'forcedRecoveryGeneration', 'protocolCommit', 'manifestSha256')
        connectionRecoveryRows = Invoke-SqliteRows -DatabasePath $controlDatabasePath `
            -Sql 'SELECT AgvId, SessionGeneration, Status, ResumeAuthorized FROM ConnectionRecoveries ORDER BY AgvId' `
            -Columns @('agvId', 'sessionGeneration', 'status', 'resumeAuthorized')
        sideEffectCounts = $sideEffectCounts
    }
}

function Read-OnboardJournal {
    if (-not (Test-Path -LiteralPath $onboardJournalPath -PathType Leaf)) { return $null }

    # Invoke-SqliteRows emits the row array as a single object, so it is indexed rather than wrapped
    # in @(): wrapping a command that already returns an array nests it one level deeper.
    $metadataRows = Invoke-SqliteRows -DatabasePath $onboardJournalPath `
        -Sql 'SELECT JournalEpoch FROM WireToGateJournalMetadata WHERE Id = 1' `
        -Columns @('journalEpoch')

    return [ordered]@{
        file = Get-FileFingerprint -Path $onboardJournalPath
        journalEpoch = if ($metadataRows.Count -eq 1) { $metadataRows[0]['journalEpoch'] } else { $null }
        outboxRows = Invoke-SqliteRows -DatabasePath $onboardJournalPath `
            -Sql 'SELECT DeduplicationKey, MessageType, MessageId, ContentSha256, Acknowledged FROM WireToGateDurableOutbox ORDER BY CreatedAt' `
            -Columns @('deduplicationKey', 'messageType', 'messageId', 'contentSha256', 'acknowledged')
    }
}

# Subset comparison on the durable identity columns: every row observed before a restart must still be
# present, byte for byte, after it. A recreated store fails this; a store that merely grew passes.
function Test-RowsPreserved {
    param(
        [object[]]$Before,
        [object[]]$After,
        [Parameter(Mandatory)][string[]]$IdentityColumns
    )

    if ($null -eq $Before -or $null -eq $After) { return $false }
    if (@($Before).Count -eq 0) { return $false }
    $afterKeys = [System.Collections.Generic.HashSet[string]]::new()
    foreach ($row in @($After)) {
        [void]$afterKeys.Add((($IdentityColumns | ForEach-Object { [string]$row[$_] }) -join "`u{001f}"))
    }
    foreach ($row in @($Before)) {
        $key = ($IdentityColumns | ForEach-Object { [string]$row[$_] }) -join "`u{001f}"
        if (-not $afterKeys.Contains($key)) { return $false }
    }
    return $true
}

function Get-JsonPayloadValue {
    param([string]$Json, [Parameter(Mandatory)][string]$Property)
    if ([string]::IsNullOrWhiteSpace($Json)) { return $null }
    $document = $Json | ConvertFrom-Json
    if ($null -eq $document.payload) { return $null }
    return $document.payload.$Property
}

$control = $null
$onboard = $null
$simulator = $null
$runError = $null
$version = $null
$simulatorHealth = $null
$phase1 = $null
$phase2 = $null
$phase3 = $null
$journalBeforeOnboardRestart = $null
$journalAfterRun = $null
$controlDatabaseBeforeServerRestart = $null
$controlDatabaseAfterRun = $null
$healthReadyStatus = $null
$peerExitObservations = @()
$credential = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant()

try {
    New-ExactClone -Name 'control-server' -Repository $ControlServerRepository -Destination $controlSource `
        -Commit $ControlServerCommit | Out-Null
    New-ExactClone -Name 'onboard-hmi' -Repository $OnboardRepository -Destination $onboardSource `
        -Commit $OnboardCommit -RemoteRef 'origin/OnboardHmi_MVP' | Out-Null
    New-ExactClone -Name 'slots-simulator' -Repository $SimulatorRepository -Destination $simulatorSource `
        -Commit $SimulatorCommit -RemoteRef 'origin/main' | Out-Null

    Invoke-LoggedCommand -Name 'publish-control-server' -WorkingDirectory $controlSource -FilePath 'dotnet' `
        -Arguments @('publish', '.\src\ControlServer.Host\ControlServer.Host.csproj', '-c', 'Release', '-o', $controlPublish) `
        -LogPath (Join-Path $logsRoot 'publish-control-server.log') | Out-Null
    Invoke-LoggedCommand -Name 'publish-onboard-hmi' -WorkingDirectory $onboardSource -FilePath 'dotnet' `
        -Arguments @('publish', '.\src\SQCD.Agv.Wpf\SQCD.Agv.Wpf.csproj', '-c', 'Release', '-o', $onboardPublish) `
        -LogPath (Join-Path $logsRoot 'publish-onboard-hmi.log') | Out-Null
    Invoke-LoggedCommand -Name 'publish-slots-simulator' -WorkingDirectory $simulatorSource -FilePath 'dotnet' `
        -Arguments @('publish', '.\src\SQCD_8005AGV_Simulator\SQCD_8005AGV_Simulator.csproj', '-c', 'Release', '-o', $simulatorPublish) `
        -LogPath (Join-Path $logsRoot 'publish-slots-simulator.log') | Out-Null

    Add-Type -Path (Join-Path $controlPublish 'Microsoft.Data.Sqlite.dll')

    $onboardConfig = Join-Path $onboardPublish 'appsettings.json'
    $settings = Get-Content -LiteralPath $onboardConfig -Raw | ConvertFrom-Json
    $settings.environment = 'Development'
    $settings.agvId = $agvId
    $settings.onboardInstanceId = 'OBU-8005-STAGED-G3-RESTART-01'
    $settings.wireToGate.enabled = $true
    $settings.wireToGate.host = '127.0.0.1'
    $settings.wireToGate.port = $controlPort
    $settings.wireToGate.onboardInstanceId = $onboardInstanceId
    $settings.wireToGate.onboardBuildCommit = $OnboardCommit
    $settings.wireToGate.credentialEnvironmentVariable = 'CONTROL_SERVER_ONBOARD_CREDENTIAL'
    # A TLS-era onboard build still carries this key and a plaintext one will not, so touch it only
    # where it exists: assigning to a missing property on the PSCustomObject that ConvertFrom-Json
    # returns throws SetValueInvocationException rather than adding it. The sibling key
    # serverCertificateSha256 needs no such guard here: this runner rewrites the onboard project's
    # own appsettings.json, which carries no fingerprint on either side of the cutover.
    if ($settings.wireToGate.PSObject.Properties.Name -contains 'useTls') {
        $settings.wireToGate.useTls = $false
    }
    $settings.wireToGate.journalPath = $onboardJournalPath
    $settings.logging.directory = Join-Path $runtimeRoot 'onboard-logs'
    $settings.logging.writeToConsole = $true
    $settings | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $onboardConfig -Encoding utf8NoBOM
    $onboardConfigSha256 = (Get-FileHash -LiteralPath $onboardConfig -Algorithm SHA256).Hash.ToLowerInvariant()

    $controlEnvironment = @{
        'CONTROL_SERVER_ONBOARD_CREDENTIAL' = $credential
        'ConnectionStrings__ControlServer' = "Data Source=$controlDatabasePath"
        'Health__url' = "http://127.0.0.1:$healthPort"
        'OnboardTransport__listenAddress' = '127.0.0.1'
        'OnboardTransport__port' = [string]$controlPort
        'OnboardTransport__credentialEnvironmentVariable' = 'CONTROL_SERVER_ONBOARD_CREDENTIAL'
        'JourneyRuntime__enabled' = 'false'
        'MesIngest__baseUrl' = 'http://127.0.0.1:1'
        'RIoT__baseUrl' = 'http://127.0.0.1:1'
        'ControlServerBuild__commit' = $ControlServerCommit
    }

    $simulator = Start-Process -FilePath 'dotnet' `
        -ArgumentList @(Join-Path $simulatorPublish 'SQCD_8005AGV_Simulator.dll') `
        -WorkingDirectory $simulatorPublish `
        -RedirectStandardOutput (Join-Path $logsRoot 'simulator.out.log') `
        -RedirectStandardError (Join-Path $logsRoot 'simulator.err.log') `
        -WindowStyle Hidden -PassThru
    Wait-TcpPort -Port $modbusPort
    $simulatorHealth = Wait-HttpJson -Uri "http://127.0.0.1:$simulatorHttpPort/api/v1/health"

    # Phase 1: a fresh ControlServer database and a fresh onboard journal.
    $control = Start-ControlServer -Ordinal 1
    $version = Wait-HttpJson -Uri "http://127.0.0.1:$healthPort/version"
    $onboard = Start-OnboardHmi -Ordinal 1
    $phase1Session = Wait-SessionGeneration -GreaterThan 0
    $phase1Window = Measure-StableWindow -Samples $stableWindowSamples
    $peerExitObservations += [ordered]@{
        phase = 'fresh-start'
        onboardProcessId = $onboard.Id
        controlProcessId = $control.Id
        onboardExited = $onboard.HasExited
        controlExited = $control.HasExited
    }
    $phase1 = [ordered]@{
        phase = 'fresh-start'
        sessionGeneration = [long]$phase1Session.sessionGeneration
        stableWindow = $phase1Window
    }

    # Phase 2: the onboard process is killed and restarted against the same journal file.
    $retiredOnboard = $onboard
    Stop-ProcessSafely -Process $onboard
    Start-Sleep -Seconds 2
    $journalBeforeOnboardRestart = Read-OnboardJournal
    $onboard = Start-OnboardHmi -Ordinal 2
    $phase2Session = Wait-SessionGeneration -GreaterThan $phase1.sessionGeneration
    $phase2Window = Measure-StableWindow -Samples $stableWindowSamples
    $peerExitObservations += [ordered]@{
        phase = 'onboard-process-restart-same-journal'
        onboardProcessId = $onboard.Id
        controlProcessId = $control.Id
        onboardExited = $onboard.HasExited
        controlExited = $control.HasExited
        retiredOnboardProcessId = $retiredOnboard.Id
        retiredOnboardExited = $retiredOnboard.HasExited
    }
    $phase2 = [ordered]@{
        phase = 'onboard-process-restart-same-journal'
        sessionGeneration = [long]$phase2Session.sessionGeneration
        stableWindow = $phase2Window
    }

    # Phase 3: the ControlServer process is killed and restarted against the same SQLite file while
    # the onboard peer stays up and has to reconnect on its own.
    $retiredControl = $control
    Stop-ProcessSafely -Process $control
    Start-Sleep -Seconds 4
    $controlDatabaseBeforeServerRestart = Read-ControlDatabase
    $control = Start-ControlServer -Ordinal 2
    $phase3Session = Wait-SessionGeneration -GreaterThan $phase2.sessionGeneration
    $phase3Window = Measure-StableWindow -Samples $stableWindowSamples
    $peerExitObservations += [ordered]@{
        phase = 'controlserver-process-restart-same-database'
        onboardProcessId = $onboard.Id
        controlProcessId = $control.Id
        onboardExited = $onboard.HasExited
        controlExited = $control.HasExited
        retiredControlProcessId = $retiredControl.Id
        retiredControlExited = $retiredControl.HasExited
    }
    $phase3 = [ordered]@{
        phase = 'controlserver-process-restart-same-database'
        sessionGeneration = [long]$phase3Session.sessionGeneration
        stableWindow = $phase3Window
    }

    try {
        Invoke-RestMethod -Uri "http://127.0.0.1:$healthPort/health/ready" -TimeoutSec 3 | Out-Null
        $healthReadyStatus = 200
    }
    catch {
        $healthReadyStatus = [int]$_.Exception.Response.StatusCode
    }
}
catch {
    $runError = $_
}
finally {
    foreach ($process in @($onboard, $control, $simulator)) {
        Stop-ProcessSafely -Process $process
    }
}

Start-Sleep -Seconds 2
$journalAfterRun = Read-OnboardJournal
$controlDatabaseAfterRun = Read-ControlDatabase

$onboardLogFiles = @(Get-ChildItem -LiteralPath (Join-Path $runtimeRoot 'onboard-logs') -File -ErrorAction SilentlyContinue)
foreach ($file in $onboardLogFiles) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $logsRoot $file.Name) -Force
}

$phases = @($phase1, $phase2, $phase3) | Where-Object { $null -ne $_ }

$sessionHelloRows = @()
if ($null -ne $controlDatabaseAfterRun) {
    $sessionHelloRows = @($controlDatabaseAfterRun.sessionHelloRows)
}
$serverInstanceIds = @($sessionHelloRows | ForEach-Object {
    Get-JsonPayloadValue -Json $_['firstResponseJson'] -Property 'serverInstanceId'
})
$serverBuildCommits = @($sessionHelloRows | ForEach-Object {
    Get-JsonPayloadValue -Json $_['firstResponseJson'] -Property 'serverBuildCommit'
})
$onboardBuildCommits = @($sessionHelloRows | ForEach-Object {
    Get-JsonPayloadValue -Json $_['requestJson'] -Property 'onboardBuildCommit'
})
$onboardInstanceIds = @($sessionHelloRows | ForEach-Object {
    Get-JsonPayloadValue -Json $_['requestJson'] -Property 'onboardInstanceId'
})

$commitBindingPass = $ControlServerCommit -cmatch '^[0-9a-f]{40}$' -and
    $OnboardCommit -cmatch '^[0-9a-f]{40}$' -and
    $SimulatorCommit -cmatch '^[0-9a-f]{40}$' -and
    $ProtocolCommit -cmatch '^[0-9a-f]{40}$' -and
    (@($commands | Where-Object { $_.name -eq 'checkout-control-server' }).Count -eq 1) -and
    (@($commands | Where-Object { $_.name -eq 'checkout-onboard-hmi' }).Count -eq 1) -and
    (@($commands | Where-Object { $_.name -eq 'checkout-slots-simulator' }).Count -eq 1)

$controlServerBuildPass = $sessionHelloRows.Count -eq 3 -and
    @($serverBuildCommits | Where-Object { $_ -ne $ControlServerCommit }).Count -eq 0
$onboardBuildPass = $sessionHelloRows.Count -eq 3 -and
    @($onboardBuildCommits | Where-Object { $_ -ne $OnboardCommit }).Count -eq 0 -and
    @($onboardInstanceIds | Where-Object { $_ -ne $onboardInstanceId }).Count -eq 0
$protocolBindingPass = $null -ne $version -and
    $version.protocolCommit -eq $ProtocolCommit -and
    $null -ne $controlDatabaseAfterRun -and
    @($controlDatabaseAfterRun.sessionRecoveryRows).Count -eq 1 -and
    $controlDatabaseAfterRun.sessionRecoveryRows[0]['protocolCommit'] -eq $ProtocolCommit

$freshStartPass = $null -ne $phase1 -and $phase1.sessionGeneration -eq 1
$onboardRestartPass = $null -ne $phase1 -and $null -ne $phase2 -and
    $phase2.sessionGeneration -eq $phase1.sessionGeneration + 1
$controlRestartPass = $null -ne $phase2 -and $null -ne $phase3 -and
    $phase3.sessionGeneration -eq $phase2.sessionGeneration + 1

$stableWindowPass = @($phases).Count -eq 3
foreach ($phase in $phases) {
    $samples = @($phase.stableWindow)
    if ($samples.Count -ne $stableWindowSamples) { $stableWindowPass = $false; break }
    if (@($samples | Where-Object { $_['sessionGeneration'] -ne $phase.sessionGeneration }).Count -ne 0) {
        $stableWindowPass = $false
        break
    }
}

$noUnexpectedExitPass = @($peerExitObservations).Count -eq 3 -and
    @($peerExitObservations | Where-Object { $_['onboardExited'] -or $_['controlExited'] }).Count -eq 0

# Each phase must have restarted exactly the peer it names and left the other one alone, or the
# durability results below are about a restart that never happened. OnboardMessageProcessor is a
# scoped service resolved once per accepted connection, so its serverInstanceId is per connection and
# cannot carry this claim: the OS process identity is what distinguishes a replaced host from a
# reconnected client, and the retired handles prove the old process really died first.
$onboardRestartIsolationPass = @($peerExitObservations).Count -eq 3 -and
    $peerExitObservations[0]['onboardProcessId'] -ne $peerExitObservations[1]['onboardProcessId'] -and
    $peerExitObservations[1]['onboardProcessId'] -eq $peerExitObservations[2]['onboardProcessId'] -and
    $peerExitObservations[1]['retiredOnboardProcessId'] -eq $peerExitObservations[0]['onboardProcessId'] -and
    $peerExitObservations[1]['retiredOnboardExited']
$controlRestartIsolationPass = @($peerExitObservations).Count -eq 3 -and
    $peerExitObservations[0]['controlProcessId'] -eq $peerExitObservations[1]['controlProcessId'] -and
    $peerExitObservations[1]['controlProcessId'] -ne $peerExitObservations[2]['controlProcessId'] -and
    $peerExitObservations[2]['retiredControlProcessId'] -eq $peerExitObservations[1]['controlProcessId'] -and
    $peerExitObservations[2]['retiredControlExited']
# Recorded as its own invariant rather than as restart evidence: a processor that cached one identity
# across connections would collapse these three into fewer.
$connectionScopedIdentityPass = $serverInstanceIds.Count -eq 3 -and
    @($serverInstanceIds | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -eq 0 -and
    @($serverInstanceIds | Sort-Object -Unique).Count -eq 3

$controlDatabaseReusedPass = $null -ne $controlDatabaseBeforeServerRestart -and
    $null -ne $controlDatabaseAfterRun -and
    $controlDatabaseBeforeServerRestart.file.creationTimeUtc -eq $controlDatabaseAfterRun.file.creationTimeUtc -and
    $controlDatabaseAfterRun.file.length -ge $controlDatabaseBeforeServerRestart.file.length

$journalEpochPass = $null -ne $journalBeforeOnboardRestart -and $null -ne $journalAfterRun -and
    -not [string]::IsNullOrWhiteSpace($journalBeforeOnboardRestart.journalEpoch) -and
    $journalBeforeOnboardRestart.journalEpoch -eq $journalAfterRun.journalEpoch -and
    $journalBeforeOnboardRestart.file.creationTimeUtc -eq $journalAfterRun.file.creationTimeUtc

$inboxDurabilityPass = $null -ne $controlDatabaseBeforeServerRestart -and $null -ne $controlDatabaseAfterRun -and
    (Test-RowsPreserved -Before $controlDatabaseBeforeServerRestart.inboxRows `
        -After $controlDatabaseAfterRun.inboxRows `
        -IdentityColumns @('messageId', 'messageType', 'contentHash')) -and
    @($controlDatabaseAfterRun.inboxRows).Count -gt @($controlDatabaseBeforeServerRestart.inboxRows).Count

$onboardOutboxDurabilityPass = $null -ne $journalBeforeOnboardRestart -and $null -ne $journalAfterRun -and
    (Test-RowsPreserved -Before $journalBeforeOnboardRestart.outboxRows `
        -After $journalAfterRun.outboxRows `
        -IdentityColumns @('deduplicationKey', 'messageType', 'messageId', 'contentSha256')) -and
    @($journalAfterRun.outboxRows).Count -gt @($journalBeforeOnboardRestart.outboxRows).Count

# The two peers keep independent stores, so the recovery report identities they each persisted have to
# be the same set. A message re-minted on reconnect, or one lost on either side, breaks this.
$journalReportIds = @()
$inboxReportIds = @()
if ($null -ne $journalAfterRun) {
    $journalReportIds = @($journalAfterRun.outboxRows |
        Where-Object { $_['messageType'] -eq 'RecoveryStateReport' } |
        ForEach-Object { [string]$_['messageId'] } | Sort-Object)
}
if ($null -ne $controlDatabaseAfterRun) {
    $inboxReportIds = @($controlDatabaseAfterRun.inboxRows |
        Where-Object { $_['messageType'] -eq 'RecoveryStateReport' } |
        ForEach-Object { [string]$_['messageId'] } | Sort-Object)
}
$crossPeerIdentityPass = $journalReportIds.Count -eq 3 -and $inboxReportIds.Count -eq 3 -and
    @(Compare-Object -ReferenceObject $journalReportIds -DifferenceObject $inboxReportIds).Count -eq 0 -and
    @($journalAfterRun.outboxRows | Where-Object {
        $_['messageType'] -eq 'RecoveryStateReport' -and [long]$_['acknowledged'] -ne 1 }).Count -eq 0

$sessionRowSingletonPass = $null -ne $controlDatabaseAfterRun -and
    @($controlDatabaseAfterRun.sessionRecoveryRows).Count -eq 1 -and
    $controlDatabaseAfterRun.sessionRecoveryRows[0]['agvId'] -eq $agvId -and
    [long]$controlDatabaseAfterRun.sessionRecoveryRows[0]['sessionGeneration'] -eq 3 -and
    @($controlDatabaseAfterRun.connectionRecoveryRows).Count -le 1

$noMovementPass = $null -ne $controlDatabaseAfterRun -and
    @($controlDatabaseAfterRun.sideEffectCounts.Keys | Where-Object {
        $controlDatabaseAfterRun.sideEffectCounts[$_] -ne 0 }).Count -eq 0 -and
    $healthReadyStatus -eq 503 -and
    $controlDatabaseAfterRun.sessionRecoveryRows[0]['readiness'] -eq 'RecoveryRequired'

$configuration = [ordered]@{
    loopbackOnly = $true
    tls = $false
    temporaryTrustRootInstalled = $false
    unattended = $true
    commitBinding = [ordered]@{
        source = [IO.Path]::GetRelativePath($ControlServerRepository, $CommitBindingSource).Replace('\', '/')
        sourceSha256 = $commitBindingSourceSha256
        readFrom = 'param-block-defaults'
        controlServer = $ControlServerCommit
        onboardHmi = $OnboardCommit
        slotsSimulator = $SimulatorCommit
        protocol = $ProtocolCommit
    }
    ports = [ordered]@{
        controlPlaintext = $controlPort
        controlHealth = $healthPort
        simulatorModbus = $modbusPort
        simulatorHttp = $simulatorHttpPort
    }
    agvId = $agvId
    onboardConfigSha256 = if ($null -ne $onboardConfigSha256) { $onboardConfigSha256 } else { $null }
    stableWindowSamplesPerPhase = $stableWindowSamples
    journeyRuntimeEnabled = $false
    realExternalCredentialsUsed = $false
    realRiotOrderCreated = $false
    movementCommandSent = $false
    vehicleSafetyEligibilityFabricated = $false
    vectorsNotReachableWithoutAnAcceptedDemand = @(
        'demandReusedAcrossRestart',
        'vehicleDispatchLeaseReusedAcrossRestart')
}
$configurationJson = $configuration | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText(
    (Join-Path $EvidenceRoot 'configuration.json'),
    $configurationJson,
    [Text.UTF8Encoding]::new($false))

$secretLeakFiles = [System.Collections.Generic.List[string]]::new()
foreach ($file in @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File)) {
    try {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        if ($text.Contains($credential, [StringComparison]::Ordinal)) {
            $secretLeakFiles.Add([IO.Path]::GetRelativePath($EvidenceRoot, $file.FullName).Replace('\', '/'))
        }
    }
    catch {
        # Evidence is text-only in this runner; unreadable files are covered by the artifact hash list.
    }
}

$assertions = [ordered]@{
    commitBindingSharedWithMainRunner = $commitBindingPass
    runningControlServerReportsBoundBuildCommit = $controlServerBuildPass
    runningOnboardReportsBoundBuildCommit = $onboardBuildPass
    protocolIdentityBoundToRelease = $protocolBindingPass
    freshDatabaseStartsAtGenerationOne = $freshStartPass
    onboardRestartAdvancesGenerationByExactlyOne = $onboardRestartPass
    controlServerRestartAdvancesGenerationByExactlyOne = $controlRestartPass
    sessionGenerationStableWithinEveryPhase = $stableWindowPass
    noPeerExitedUnexpectedly = $noUnexpectedExitPass
    onboardHostProcessReplacedOnlyInPhaseTwo = $onboardRestartIsolationPass
    controlServerHostProcessReplacedOnlyInPhaseThree = $controlRestartIsolationPass
    serverSessionIdentityIsScopedPerConnection = $connectionScopedIdentityPass
    controlDatabaseFileReusedAcrossServerRestart = $controlDatabaseReusedPass
    onboardJournalEpochStableAcrossOnboardRestart = $journalEpochPass
    controlInboxRowsSurviveServerRestart = $inboxDurabilityPass
    onboardOutboxRowsSurviveOnboardRestart = $onboardOutboxDurabilityPass
    recoveryReportIdentityAgreesAcrossPeers = $crossPeerIdentityPass
    sessionRecoveryRowStaysASingletonPerAgv = $sessionRowSingletonPass
    noMovementOrExternalSideEffects = $noMovementPass
    secretScan = $secretLeakFiles.Count -eq 0
}
$assertionReport = [ordered]@{}
foreach ($name in $assertions.Keys) {
    $assertionReport[$name] = if ($assertions[$name]) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
}
$failedAssertions = @($assertions.Keys | Where-Object { -not $assertions[$_] })

$status = if ($null -ne $runError) {
    'INCONCLUSIVE_RUNNER_ERROR'
} elseif ($failedAssertions.Count -eq 0) {
    'STAGED_G3_PROCESS_RESTART_PASS'
} else {
    'STAGED_SLICE_FAIL'
}

$artifactFiles = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File |
    Where-Object { $_.Name -ne 'run-result.json' } |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($EvidenceRoot, $_.FullName).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            length = $_.Length
        }
    })

$result = [ordered]@{
    schemaVersion = '1.0.0'
    runKind = 'STAGED_G3_REAL_PEERS_PROCESS_RESTART_NO_MOVEMENT'
    runId = $runId
    startedAtUtc = $runStartedAt
    completedAtUtc = [DateTimeOffset]::UtcNow
    status = $status
    classification = [ordered]@{
        stagedSlice = $status
        formalSlicePass = $false
        officialSlices = @(
            [ordered]@{ integrationSliceId = 'W2G-IS-00'; status = 'INCONCLUSIVE' },
            [ordered]@{ integrationSliceId = 'W2G-IS-06'; status = 'INCONCLUSIVE' }
        )
        fullG3 = 'INCONCLUSIVE'
        releaseCandidate = 'INCONCLUSIVE'
    }
    commits = [ordered]@{
        controlServer = $ControlServerCommit
        onboardHmi = $OnboardCommit
        slotsSimulator = $SimulatorCommit
        protocol = $ProtocolCommit
        runner = $runnerCommit
        runnerWorktreeCleanAtStart = $runnerWorktreeClean
    }
    configurationSha256 = Get-Sha256Text $configurationJson
    configuration = $configuration
    commands = @($commands)
    assertions = $assertionReport
    failedAssertions = $failedAssertions
    phases = @($phases)
    peerExitObservations = @($peerExitObservations)
    sessionIdentity = [ordered]@{
        serverInstanceIds = $serverInstanceIds
        serverBuildCommits = $serverBuildCommits
        onboardBuildCommits = $onboardBuildCommits
        onboardInstanceIds = $onboardInstanceIds
        recoveryReportMessageIdsInOnboardJournal = $journalReportIds
        recoveryReportMessageIdsInControlInbox = $inboxReportIds
    }
    onboardJournalBeforeRestart = $journalBeforeOnboardRestart
    onboardJournalAfterRun = $journalAfterRun
    controlDatabaseBeforeServerRestart = $controlDatabaseBeforeServerRestart
    controlDatabaseAfterRun = $controlDatabaseAfterRun
    healthReadyHttpStatus = $healthReadyStatus
    controlServerVersion = $version
    simulatorHealth = $simulatorHealth
    error = if ($null -ne $runError) {
        [ordered]@{ type = $runError.Exception.GetType().FullName; message = $runError.Exception.Message }
    } else { $null }
    secretLeakFiles = @($secretLeakFiles)
    evidenceFiles = $artifactFiles
}

$resultJson = $result | ConvertTo-Json -Depth 40
[IO.File]::WriteAllText(
    (Join-Path $EvidenceRoot 'run-result.json'),
    $resultJson,
    [Text.UTF8Encoding]::new($false))
$resultJson

if ($status -ne 'STAGED_G3_PROCESS_RESTART_PASS' -or $secretLeakFiles.Count -ne 0) {
    throw "Staged G3 process restart did not pass: $status. Evidence: $EvidenceRoot"
}
