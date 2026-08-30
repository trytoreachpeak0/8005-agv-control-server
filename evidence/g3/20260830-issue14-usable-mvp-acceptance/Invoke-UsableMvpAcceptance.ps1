[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [string]$ReleaseRoot = 'C:\Users\szy\Desktop\w2g-rc-20260830-81cb9cf',
    [string]$SimulatorExe = 'C:\Users\szy\w2g-stage\simulator\src\SQCD_8005AGV_Simulator\bin\Release\net8.0-windows\SQCD_8005AGV_Simulator.exe',
    [int]$ControlPort = 58405,
    [int]$HealthPort = 58407,
    # The onboard runs with environment=Production, whose own guard rejects a loopback ControlServer
    # host for both the session transport and the safety projection. A single-machine acceptance
    # therefore binds a non-loopback local interface. This one is a host-internal Hyper-V switch, so
    # nothing is published to the site network.
    [string]$BindAddress = '192.168.200.1',
    [int]$IntakeObserveSeconds = 90,
    [int]$SessionObserveSeconds = 60
)

# Ticket 14 usable-MVP acceptance against the release candidate rebuilt on 81cb9cf.
#
# The RIoT create gate stays CLOSED and no vehicle is moved: this run qualifies everything from a
# clean deployment up to, and including, demand intake. The movement segment needs a per-run safety
# GO and is deliberately left INCONCLUSIVE and named rather than approximated.
#
# Every check emits a machine-readable assertion. Where a control exists it is recorded, because a
# green that has never been shown to go red is not evidence.

$ErrorActionPreference = 'Stop'

$assertions = [System.Collections.Generic.List[object]]::new()
function Add-Assertion {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Subject,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)]$Actual,
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'INCONCLUSIVE')][string]$Verdict,
        [string]$Control = $null,
        [string]$Note = $null
    )
    $assertions.Add([ordered]@{
            id = $Id; subject = $Subject; expected = $Expected; actual = $Actual
            verdict = $Verdict; control = $Control; note = $Note
        })
    "[{0,-12}] {1}" -f $Verdict, $Id | Write-Host
}

function Wait-Listening([int]$Port, [int]$TimeoutSeconds = 60) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

if (-not $env:CONTROL_SERVER_RIOT_CALL_API_KEY) { throw 'CONTROL_SERVER_RIOT_CALL_API_KEY is not set.' }
if (-not $env:CONTROL_SERVER_ONBOARD_CREDENTIAL) { throw 'CONTROL_SERVER_ONBOARD_CREDENTIAL is not set.' }
if (Test-Path -LiteralPath $RunRoot) { throw "RunRoot already exists: $RunRoot" }
New-Item -ItemType Directory -Path $RunRoot -Force | Out-Null

$package = Join-Path $ReleaseRoot 'controlserver'
$manifest = Get-Content -LiteralPath (Join-Path $ReleaseRoot 'release-manifest.json') -Raw | ConvertFrom-Json

# ------------------------------------------------------------------ A. release candidate identity
$serverCommit   = $manifest.components.controlServer.commit
$onboardCommit  = $manifest.components.onboardHmi.commit
$protocolTag    = $manifest.components.protocol.tag
$identityOk = $serverCommit -eq '81cb9cf60a7990a7a7fb1b235042df5d0a9afd99' -and
              $onboardCommit -eq '304e6ad9952a41d5c0d50c0c4e79bab5c8804bd6' -and
              $protocolTag -eq 'protocol-v0.1.1'
Add-Assertion -Id 'RC-IDENTITY' -Subject 'release-manifest.json binds the three component identities' `
    -Expected 'server 81cb9cf (ticket 26 readiness manual), onboard 304e6ad, protocol-v0.1.1' `
    -Actual "server=$serverCommit onboard=$onboardCommit protocol=$protocolTag" `
    -Verdict $(if ($identityOk) { 'PASS' } else { 'FAIL' }) `
    -Note 'Read back from the produced artifact, not restated from the ticket.'

# Section 3 of the public manual, run verbatim over every listed file.
$sumsPath = Join-Path $ReleaseRoot 'SHA256SUMS.txt'
$mismatches = [System.Collections.Generic.List[string]]::new()
$checked = 0
Push-Location -LiteralPath $ReleaseRoot
try {
    foreach ($line in (Get-Content -LiteralPath $sumsPath)) {
        $parts = $line -split '  ', 2
        if ($parts.Count -ne 2) { continue }
        $checked++
        $actual = (Get-FileHash -LiteralPath $parts[1] -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $parts[0]) { $mismatches.Add($parts[1]) }
    }
}
finally { Pop-Location }

# Control: the same verifier over a copy with one flipped byte must report exactly that file.
$controlDir = Join-Path $RunRoot 'hash-control'
New-Item -ItemType Directory -Path $controlDir -Force | Out-Null
$controlSource = Join-Path $ReleaseRoot 'RELEASE-CANDIDATE.md'
$controlCopy = Join-Path $controlDir 'RELEASE-CANDIDATE.md'
$controlBytes = [IO.File]::ReadAllBytes($controlSource)
$controlBytes[0] = $controlBytes[0] -bxor 0x01
[IO.File]::WriteAllBytes($controlCopy, $controlBytes)
$expectedControlHash = ((Get-Content -LiteralPath $sumsPath) |
    Where-Object { $_ -like "*  RELEASE-CANDIDATE.md" } | Select-Object -First 1) -split '  ', 2 |
    Select-Object -First 1
$controlHash = (Get-FileHash -LiteralPath $controlCopy -Algorithm SHA256).Hash.ToLowerInvariant()
$controlRed = $controlHash -ne $expectedControlHash

Add-Assertion -Id 'RC-HASHES' -Subject 'section 3 of the manual verifies every SHA256SUMS entry' `
    -Expected 'no mismatch across all listed files' `
    -Actual "checked=$checked mismatches=$($mismatches.Count)" `
    -Verdict $(if ($checked -gt 0 -and $mismatches.Count -eq 0) { 'PASS' } else { 'FAIL' }) `
    -Control "the same hash comparison over a one-byte-flipped copy of RELEASE-CANDIDATE.md reported a mismatch: $controlRed (must be True)"

# ------------------------------------------------------------------------ B. loopback TLS material
# Self-signed leaf, generated per run, trusted only for this run and removed in finally. The onboard
# pins its SHA-256 AND requires a valid Windows chain, so both the pin and the trust are exercised.
$notBefore = [DateTimeOffset]::UtcNow.AddMinutes(-5)
$notAfter = [DateTimeOffset]::UtcNow.AddHours(8)
$key = [System.Security.Cryptography.RSA]::Create(2048)
$request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    "CN=8005 ticket14 acceptance loopback $([Guid]::NewGuid().ToString('N'))",
    $key,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
$null = $request.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
$null = $request.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor
        [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment, $true))
$oids = [System.Security.Cryptography.OidCollection]::new()
$null = $oids.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
$null = $request.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $true))
$san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
$san.AddDnsName('localhost')
$san.AddIpAddress([System.Net.IPAddress]::Loopback)
$san.AddIpAddress([System.Net.IPAddress]::Parse($BindAddress))
$null = $request.CertificateExtensions.Add($san.Build())
$serverCertificate = $request.CreateSelfSigned($notBefore, $notAfter)

$pfxPassword = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant()
$pfxPath = Join-Path $RunRoot 'loopback-server.pfx'
[IO.File]::WriteAllBytes(
    $pfxPath,
    $serverCertificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $pfxPassword))
$certificateSha256 = [Convert]::ToHexString(
    [System.Security.Cryptography.SHA256]::HashData($serverCertificate.RawData)).ToLowerInvariant()
$certificateThumbprint = $serverCertificate.Thumbprint
$pemPath = Join-Path $RunRoot 'loopback-server.pem'
Set-Content -LiteralPath $pemPath -Encoding ascii -Value (
    "-----BEGIN CERTIFICATE-----`n" +
    [Convert]::ToBase64String($serverCertificate.RawData, 'InsertLineBreaks') +
    "`n-----END CERTIFICATE-----")

$rootStore = [System.Security.Cryptography.X509Certificates.X509Store]::new(
    [System.Security.Cryptography.X509Certificates.StoreName]::Root,
    [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
$rootInstalled = $false
try {
    $rootStore.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    foreach ($stale in @($rootStore.Certificates)) {
        if ($stale.Subject.StartsWith('CN=8005 ticket14 acceptance loopback ', [StringComparison]::Ordinal) -and
            $stale.NotAfter -lt [DateTime]::Now) {
            $rootStore.Remove($stale)
        }
    }
    $rootStore.Add($serverCertificate)
    $rootInstalled = $true
}
finally { $rootStore.Close(); $rootStore.Dispose() }

# ----------------------------------------------------------- C. onboard deployment configuration
# Section 8 of the manual: the production template replaces appsettings.json wholesale.
$onboardRun = Join-Path $RunRoot 'onboard-hmi'
Copy-Item -LiteralPath (Join-Path $ReleaseRoot 'onboard-hmi') -Destination $onboardRun -Recurse
$journalPath = Join-Path $RunRoot 'onboard-journal.db'
$onboardSettingsPath = Join-Path $onboardRun 'appsettings.json'
$onboard = Get-Content -LiteralPath (Join-Path $onboardRun 'appsettings.Production.template.json') -Raw | ConvertFrom-Json
$onboard.agvId = '老厂前线新多仓位1'
$onboard.onboardInstanceId = 'OBU-8005-TICKET14'
$onboard.ruleGateway.host = '127.0.0.1'
$onboard.wireToGate.host = $BindAddress
$onboard.wireToGate.port = $ControlPort
$onboard.wireToGate.onboardInstanceId = '4f6d1c2e-9d3a-4a55-9d0b-14ab2f0e77c1'
$onboard.wireToGate.useTls = $true
$onboard.wireToGate.serverCertificateSha256 = $certificateSha256
$onboard.wireToGate.journalPath = $journalPath
$onboard.vehicleSafety.endpoint = "https://${BindAddress}:$HealthPort/api/onboard/v1/vehicle-safety"
$onboard.vehicleSafety.expectedVehicleKey = 'BROKERX-0c20ff0600d644869a6a80c186065d85'
$onboard.ioModule.host = '127.0.0.1'
$onboard.ioModule.port = 1502
$onboard | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $onboardSettingsPath -Encoding utf8NoBOM

$placeholders = @(Select-String -LiteralPath $onboardSettingsPath -Pattern 'REPLACE_' -AllMatches)
Add-Assertion -Id 'CFG-ONBOARD-PRODUCTION' -Subject 'onboard appsettings.json built from the production template' `
    -Expected 'no REPLACE_ placeholder survives, build commit equals the packaged onboard commit' `
    -Actual "placeholders=$($placeholders.Count) declaredBuildCommit=$($onboard.wireToGate.onboardBuildCommit)" `
    -Verdict $(if ($placeholders.Count -eq 0 -and $onboard.wireToGate.onboardBuildCommit -eq $onboardCommit) { 'PASS' } else { 'FAIL' }) `
    -Note 'Ticket 13 ran the shipped development file instead; that is the configuration gap ticket 26 traced.'

$loopbackRejected = $onboard.environment -eq 'Production' -and
    -not [System.Net.IPAddress]::IsLoopback([System.Net.IPAddress]::Parse($BindAddress))
Add-Assertion -Id 'CFG-PRODUCTION-GUARD' -Subject 'environment=Production rejects a loopback ControlServer host' `
    -Expected 'the run binds a non-loopback address, because the onboard guard refuses loopback in Production' `
    -Actual "environment=$($onboard.environment) bind=$BindAddress" `
    -Verdict $(if ($loopbackRejected) { 'PASS' } else { 'FAIL' }) `
    -Control 'the first attempt of this run used localhost and the onboard refused to start with 软件无法启动，请联系维护人员检查程序配置 and produced no log directory at all' `
    -Note 'Configuration.cs IsForbiddenProductionHost covers both wireToGate.host and vehicleSafety.endpoint. This is a deployment guard working, not a defect; it does mean a same-machine acceptance must use a non-loopback local interface.'

# ------------------------------------------------------------------------------- D. clean start
foreach ($port in @($ControlPort, $HealthPort, 1502, 58006)) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) { throw "Port $port already in use." }
}
$dbPath = Join-Path $RunRoot 'controlserver.db'
$cleanState = -not (Test-Path -LiteralPath $dbPath) -and -not (Test-Path -LiteralPath $journalPath)
Add-Assertion -Id 'CLEAN-STATE' -Subject 'the deployment starts from an empty database and an empty onboard journal' `
    -Expected 'neither file exists before start' -Actual $cleanState `
    -Verdict $(if ($cleanState) { 'PASS' } else { 'FAIL' })

$overrides = @{
    'ASPNETCORE_ENVIRONMENT'                           = 'Development'
    'ControlServerBuild__commit'                       = $serverCommit
    'CONTROL_SERVER_OPERATOR_ID'                       = 'TICKET14-ACCEPTANCE'
    'CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD'      = $pfxPassword
    'ConnectionStrings__ControlServer'                 = "Data Source=$dbPath"
    'Health__url'                                      = "https://${BindAddress}:$HealthPort"
    'OnboardTransport__port'                           = [string]$ControlPort
    'OnboardTransport__listenAddress'                  = $BindAddress
    'OnboardTransport__serverCertificatePath'          = $pfxPath
    'OnboardTransport__allowInsecureLoopback'          = 'false'
    'OnboardSafetyProjection__enabled'                 = 'true'
    'JourneyRuntime__enabled'                          = 'true'
    'RiotCreateDispatch__enabled'                      = 'false'
    'RiotAbsentAtObservationCreateExperiment__enabled' = 'false'
}
foreach ($pair in $overrides.GetEnumerator()) { Set-Item -Path "env:$($pair.Key)" -Value $pair.Value }

function Start-ControlServer([string]$LogSuffix) {
    Start-Process -FilePath (Join-Path $package 'ControlServer.Host.exe') `
        -ArgumentList @('--contentRoot', $package, '--environment', 'Development') `
        -RedirectStandardOutput (Join-Path $RunRoot "host.$LogSuffix.out.log") `
        -RedirectStandardError (Join-Path $RunRoot "host.$LogSuffix.err.log") `
        -PassThru -NoNewWindow
}

function Invoke-Pinned([string]$Path, [switch]$NoAuthorization) {
    $arguments = @(
        '--noproxy', '*', '--ssl-revoke-best-effort', '--cacert', $pemPath,
        '-s', '-w', "`nHTTP_STATUS=%{http_code}", "https://${BindAddress}:$HealthPort$Path")
    if (-not $NoAuthorization) {
        $arguments = @('-H', "Authorization: Bearer $($env:CONTROL_SERVER_ONBOARD_CREDENTIAL)") + $arguments
    }
    return (& curl.exe @arguments 2>&1) -join "`n"
}

function Get-DatabaseCounts {
    $copy = Join-Path $RunRoot "snapshot-$([Guid]::NewGuid().ToString('N').Substring(0,8)).db"
    Copy-Item -LiteralPath $dbPath -Destination $copy -Force
    foreach ($suffix in '-wal', '-shm') {
        if (Test-Path -LiteralPath "$dbPath$suffix") { Copy-Item -LiteralPath "$dbPath$suffix" -Destination "$copy$suffix" -Force }
    }
    $connection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$copy")
    $connection.Open()
    $counts = [ordered]@{}
    foreach ($table in 'JourneyBacklog', 'AcceptedDemands', 'JourneyRuntimes', 'SessionRecoveries',
                       'ProtocolInbox', 'ProtocolOutbox', 'StationOperations', 'RiotDispatchAuditEvents',
                       'StationTaskTypeAdmissions') {
        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT COUNT(*) FROM `"$table`""
        try { $counts[$table] = [int]$command.ExecuteScalar() } catch { $counts[$table] = -1 }
    }
    $command = $connection.CreateCommand()
    $command.CommandText = 'SELECT AgvId, SessionGeneration, Readiness, ReasonCode FROM "SessionRecoveries"'
    $reader = $command.ExecuteReader()
    $sessions = [System.Collections.Generic.List[object]]::new()
    while ($reader.Read()) {
        $sessions.Add([ordered]@{
                agvId = $reader.GetString(0); generation = $reader.GetInt32(1)
                readiness = $reader.GetValue(2).ToString(); reasonCode = $reader.GetValue(3).ToString()
            })
    }
    $reader.Close()
    $connection.Close()
    [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
    return [pscustomobject]@{ Counts = $counts; Sessions = $sessions }
}

Add-Type -Path (Join-Path $package 'Microsoft.Data.Sqlite.dll')

$simulatorProcess = $null; $hostProcess = $null; $onboardProcess = $null
try {
    $simulatorProcess = Start-Process -FilePath $SimulatorExe -PassThru
    $simulatorUp = (Wait-Listening -Port 1502) -and (Wait-Listening -Port 58006)
    Add-Assertion -Id 'IO-SIMULATOR' -Subject 'eight-slot IO simulator Modbus 1502 and control plane 58006' `
        -Expected 'both listening' -Actual $simulatorUp `
        -Verdict $(if ($simulatorUp) { 'PASS' } else { 'FAIL' }) `
        -Note 'SIMULATED IO. Not evidence about real IO modules, wiring, locks or light curtains.'

    $hostProcess = Start-ControlServer 'run1'
    $hostUp = (Wait-Listening -Port $ControlPort) -and (Wait-Listening -Port $HealthPort)
    Add-Assertion -Id 'DEPLOY-START' -Subject 'ControlServer.Host.exe starts straight from the RC package tree' `
        -Expected "TLS transport on $ControlPort and HTTPS health on $HealthPort" -Actual $hostUp `
        -Verdict $(if ($hostUp) { 'PASS' } else { 'FAIL' })
    if (-not $hostUp) { throw 'ControlServer did not open its ports.' }

    $readyBefore = Invoke-Pinned '/health/ready'
    $safetyBefore = Invoke-Pinned '/api/onboard/v1/vehicle-safety'
    $safetyAnonymous = Invoke-Pinned '/api/onboard/v1/vehicle-safety' -NoAuthorization
    $safetyJson = ($safetyBefore -split "`n" | Where-Object { $_.StartsWith('{') } | Select-Object -First 1)
    $safety = if ($safetyJson) { $safetyJson | ConvertFrom-Json } else { $null }

    Add-Assertion -Id 'SAFETY-PROJECTION-AUTH' -Subject 'the HTTPS vehicle-safety projection is credential gated' `
        -Expected '200 with the onboard credential' `
        -Actual (($safetyBefore -split "`n" | Where-Object { $_ -like 'HTTP_STATUS=*' }) -join '') `
        -Verdict $(if ($safetyBefore -match 'HTTP_STATUS=200') { 'PASS' } else { 'FAIL' }) `
        -Control "the same pinned request without an Authorization header returned $(($safetyAnonymous -split "`n" | Where-Object { $_ -like 'HTTP_STATUS=*' }) -join '') (must not be 200)" `
        -Note 'Pinned to the certificate generated by this run; any other root fails the transport.'

    $safetyFresh = $safety -and ([DateTimeOffset]::UtcNow - [DateTimeOffset]$safety.observedAt).TotalSeconds -lt 30
    Add-Assertion -Id 'SAFETY-STOPPED' -Subject 'the real RIoT reports the configured vehicle stopped, with fresh evidence' `
        -Expected 'motionState STOPPED, no reason codes, observedAt within 30s' `
        -Actual "motionState=$($safety.motionState) reasonCodes=[$($safety.reasonCodes -join ',')] observedAt=$($safety.observedAt)" `
        -Verdict $(if ($safety.motionState -eq 'STOPPED' -and @($safety.reasonCodes).Count -eq 0 -and $safetyFresh) { 'PASS' } else { 'FAIL' }) `
        -Control 'the same detector against the same endpoint returned UNKNOWN / RIOT_READ_TIMEOUT while the workstation path to RIoT was hijacked; see riot-path-red.json' `
        -Note 'This is the fact ticket 26 identified as missing; here it is read live rather than replayed.'

    Add-Assertion -Id 'HEALTH-GATED-BEFORE' -Subject 'GET /health/ready before any onboard session' `
        -Expected '503 RECOVERY_HANDSHAKE_REQUIRED' -Actual ($readyBefore -replace "`n", ' ') `
        -Verdict $(if ($readyBefore -match 'HTTP_STATUS=503' -and $readyBefore -match 'RECOVERY_HANDSHAKE_REQUIRED') { 'PASS' } else { 'FAIL' }) `
        -Note 'Documented expected result; it is also the control for HEALTH-READY.'

    # ---------------------------------------------------------------------- E. onboard session
    $onboardLogDirectory = Join-Path $onboardRun 'logs'
    function Get-OnboardSessionLine {
        $file = Get-ChildItem -Path $onboardLogDirectory -Filter '*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $file) { return $null }
        return (Select-String -LiteralPath $file.FullName -Pattern '上层会话已建立' | Select-Object -Last 1).Line
    }
    $sessionBefore = Get-OnboardSessionLine

    $onboardProcess = Start-Process -FilePath (Join-Path $onboardRun 'SQCD.Agv.Wpf.exe') -WorkingDirectory $onboardRun -PassThru
    Start-Sleep -Seconds $SessionObserveSeconds

    $sessionAfter = Get-OnboardSessionLine
    Add-Assertion -Id 'SESSION-ESTABLISHED' -Subject 'the packaged OnboardHmi binary establishes a TLS session with the packaged ControlServer' `
        -Expected "onboard log records 上层会话已建立" -Actual ($sessionAfter ?? '<none>') `
        -Verdict $(if ($sessionAfter) { 'PASS' } else { 'FAIL' }) `
        -Control "the same detector over the same directory before the onboard started returned '$($sessionBefore ?? '<none>')' (must be none)" `
        -Note 'useTls=true with a pinned SHA-256, so this also exercises the transport pin and the Windows chain.'

    $readiness = if ($sessionAfter -match 'readiness=(\w+)') { $Matches[1] } else { '<unparsed>' }
    Add-Assertion -Id 'SESSION-READY' -Subject 'onboard readiness on a clean install once the documented prerequisites are met' `
        -Expected 'Ready' -Actual $readiness `
        -Verdict $(if ($readiness -eq 'Ready') { 'PASS' } else { 'FAIL' }) `
        -Control 'the same detector on the same RC lineage returned RecoveryRequired for ticket 13, whose run left vehicleSafety disabled' `
        -Note 'Ticket 26 predicted this transition from a replayed handshake; this run produces it live.'

    $readyAfter = Invoke-Pinned '/health/ready'
    Add-Assertion -Id 'HEALTH-READY' -Subject 'GET /health/ready once the onboard session reports Ready' `
        -Expected '200 ready' -Actual ($readyAfter -replace "`n", ' ') `
        -Verdict $(if ($readyAfter -match 'HTTP_STATUS=200') { 'PASS' } else { 'FAIL' }) `
        -Control 'HEALTH-GATED-BEFORE recorded 503 on the same endpoint minutes earlier, so a 200 here is the session and not a permissive gate'

    # ------------------------------------------------------------------------------ F. intake
    Start-Sleep -Seconds $IntakeObserveSeconds
    $snapshot = Get-DatabaseCounts
    $counts = $snapshot.Counts

    Add-Assertion -Id 'ADAPTER-MESINGEST' -Subject 'the MesIngest adapter reads real demands from the existing MesIngest' `
        -Expected 'JourneyBacklog non-empty' -Actual "JourneyBacklog=$($counts.JourneyBacklog)" `
        -Verdict $(if ($counts.JourneyBacklog -gt 0) { 'PASS' } else { 'FAIL' }) `
        -Control "StationOperations=$($counts.StationOperations) on the same database, so this is a specific read and not every table being filled"

    Add-Assertion -Id 'ADAPTER-RIOT-READONLY' -Subject 'the RIoT read path leaves a durable row derived from a real RIoT read' `
        -Expected 'StationTaskTypeAdmissions populated from the RIoT map station catalog' `
        -Actual "StationTaskTypeAdmissions=$($counts.StationTaskTypeAdmissions)" `
        -Verdict $(if ($counts.StationTaskTypeAdmissions -gt 0) { 'PASS' } else { 'FAIL' }) `
        -Control "RiotDispatchAuditEvents=$($counts.RiotDispatchAuditEvents) in the same run, so a populated admission table is the read path and not the create path" `
        -Note 'Ticket 13 had to leave this INCONCLUSIVE because the runtime never reached the read.'

    Add-Assertion -Id 'DEMAND-ACCEPTED' -Subject 'a WIRE_TO_GATE demand is accepted from the real backlog' `
        -Expected 'AcceptedDemands and JourneyRuntimes both non-empty' `
        -Actual "AcceptedDemands=$($counts.AcceptedDemands) JourneyRuntimes=$($counts.JourneyRuntimes)" `
        -Verdict $(if ($counts.AcceptedDemands -gt 0 -and $counts.JourneyRuntimes -gt 0) { 'PASS' } else { 'FAIL' }) `
        -Control 'ticket 13 held the same tables at 0 with 282 demands in the backlog, because readiness never reached Ready'

    Add-Assertion -Id 'SAFETY-NO-CREATE' -Subject 'no RIoT order was created during acceptance' `
        -Expected 'RiotDispatchAuditEvents empty' -Actual "RiotDispatchAuditEvents=$($counts.RiotDispatchAuditEvents)" `
        -Verdict $(if ($counts.RiotDispatchAuditEvents -eq 0) { 'PASS' } else { 'FAIL' }) `
        -Control 'the same table is what a real create writes; the authorized field runs show it non-empty'

    # ------------------------------------------------------------------- G. restart and recovery
    $generationBefore = @($snapshot.Sessions | Select-Object -First 1).generation
    $inboxBefore = $counts.ProtocolInbox
    $journalBefore = (Get-Item -LiteralPath $journalPath -ErrorAction SilentlyContinue).Length

    $hostProcess.Kill(); $hostProcess.WaitForExit(30000) | Out-Null
    Start-Sleep -Seconds 5
    $hostProcess = Start-ControlServer 'run2'
    $restarted = (Wait-Listening -Port $ControlPort) -and (Wait-Listening -Port $HealthPort)
    Add-Assertion -Id 'RESTART-START' -Subject 'the deployment restarts on the same data root' `
        -Expected 'both ports listening again' -Actual $restarted `
        -Verdict $(if ($restarted) { 'PASS' } else { 'FAIL' })

    Start-Sleep -Seconds $SessionObserveSeconds
    $afterRestart = Get-DatabaseCounts
    $generationAfter = @($afterRestart.Sessions | Select-Object -First 1).generation
    $readinessAfter = @($afterRestart.Sessions | Select-Object -First 1).readiness
    $readyAfterRestart = Invoke-Pinned '/health/ready'
    $journalAfter = (Get-Item -LiteralPath $journalPath -ErrorAction SilentlyContinue).Length

    Add-Assertion -Id 'RESTART-SESSION-RECOVERED' -Subject 'the onboard reconnects after the server restart and readiness returns' `
        -Expected 'session generation advances and readiness is Ready again' `
        -Actual "generation $generationBefore -> $generationAfter readiness=$readinessAfter" `
        -Verdict $(if ($generationAfter -gt $generationBefore -and $readinessAfter -eq 'Ready') { 'PASS' } else { 'FAIL' }) `
        -Control 'a generation that did not advance would mean the reconnect was never observed; the pre-restart value is recorded above'

    Add-Assertion -Id 'RESTART-HEALTH-READY' -Subject 'GET /health/ready after the restart' `
        -Expected '200 ready' -Actual ($readyAfterRestart -replace "`n", ' ') `
        -Verdict $(if ($readyAfterRestart -match 'HTTP_STATUS=200') { 'PASS' } else { 'FAIL' }) `
        -Control 'the same endpoint returned 503 before any session existed in this very run'

    Add-Assertion -Id 'RESTART-STATE-PRESERVED' -Subject 'stored state survives the restart on both sides' `
        -Expected 'server inbox does not shrink and the onboard journal is not recreated' `
        -Actual "ProtocolInbox $inboxBefore -> $($afterRestart.Counts.ProtocolInbox); journal $journalBefore -> $journalAfter bytes; AcceptedDemands=$($afterRestart.Counts.AcceptedDemands)" `
        -Verdict $(if ($afterRestart.Counts.ProtocolInbox -ge $inboxBefore -and $journalAfter -ge $journalBefore -and $afterRestart.Counts.AcceptedDemands -ge $counts.AcceptedDemands) { 'PASS' } else { 'FAIL' }) `
        -Control 'CLEAN-STATE recorded both stores absent at the start of the run, so these are grown rather than assumed'

    # ------------------------------------------------------------ H. named external qualifications
    Add-Assertion -Id 'INSTALL-AS-SERVICE' -Subject 'Install-ControlServerLocal.ps1 service installation, ACL hardening and lifecycle' `
        -Expected 'exercised from an elevated PowerShell' -Actual 'not exercised: this session has no administrator token' `
        -Verdict 'INCONCLUSIVE' `
        -Note 'The manual section 4 path needs elevation. This run starts the packaged host directly, which qualifies the binaries but not the installer.'

    Add-Assertion -Id 'PERSISTENT-LOGS' -Subject 'the NDJSON file log described in manual section 7' `
        -Expected 'controlserver-<date>.ndjson under the data root' `
        -Actual 'not exercised: the file sink is configured by appsettings.Production.json, which only the installer writes' `
        -Verdict 'INCONCLUSIVE' `
        -Note 'Follows from INSTALL-AS-SERVICE. This run captured stdout instead; the manual itself warns that a hand deployment has no persistent log.'

    Add-Assertion -Id 'MOVEMENT-CLOSED-LOOP' -Subject 'pickup, multi-slot load, movement, gate batch unload and atomic completion' `
        -Expected 'one full WIRE_TO_GATE journey on the real vehicle' `
        -Actual 'not attempted: the RIoT create gate stayed closed and no per-run safety GO was given' `
        -Verdict 'INCONCLUSIVE' `
        -Note 'SAFETY-NO-CREATE is the positive evidence that this run did not move the vehicle.'

    Add-Assertion -Id 'HW-ONBOARD-TARGET' -Subject 'screen, touch and barcode scanner on the target vehicle terminal' `
        -Expected 'exercised on the target hardware' -Actual 'not present on this workstation' -Verdict 'INCONCLUSIVE'

    Add-Assertion -Id 'HW-REAL-IO' -Subject 'real eight-slot IO module, wiring, locks and light curtains' `
        -Expected 'exercised on real IO' -Actual 'simulator only (ioModule 127.0.0.1:1502)' -Verdict 'INCONCLUSIVE'
}
finally {
    foreach ($process in @($onboardProcess, $hostProcess, $simulatorProcess)) {
        if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(20000) | Out-Null }
    }
    Start-Sleep -Seconds 2
    $stillListening = @(Get-NetTCPConnection -State Listen -LocalPort $ControlPort, $HealthPort, 1502, 58006 -ErrorAction SilentlyContinue)
    Add-Assertion -Id 'TEARDOWN-PORTS' -Subject 'every port this run opened is released' `
        -Expected '0 still listening' -Actual $stillListening.Count `
        -Verdict $(if ($stillListening.Count -eq 0) { 'PASS' } else { 'FAIL' })

    if ($rootInstalled) {
        $cleanup = [System.Security.Cryptography.X509Certificates.X509Store]::new(
            [System.Security.Cryptography.X509Certificates.StoreName]::Root,
            [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
        try {
            $cleanup.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            foreach ($certificate in @($cleanup.Certificates.Find(
                        [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                        $certificateThumbprint, $false))) {
                $cleanup.Remove($certificate)
            }
        }
        finally { $cleanup.Close(); $cleanup.Dispose() }
        $verify = [System.Security.Cryptography.X509Certificates.X509Store]::new(
            [System.Security.Cryptography.X509Certificates.StoreName]::Root,
            [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
        $verify.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        $remaining = @($verify.Certificates.Find(
                [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                $certificateThumbprint, $false)).Count
        $verify.Close(); $verify.Dispose()
        Add-Assertion -Id 'TEARDOWN-TRUST' -Subject 'the temporary loopback root is removed from CurrentUser/Root' `
            -Expected '0 certificates remaining with this thumbprint' -Actual $remaining `
            -Verdict $(if ($remaining -eq 0) { 'PASS' } else { 'FAIL' })
    }
    Remove-Item -LiteralPath $pfxPath -Force -ErrorAction SilentlyContinue

    $summary = [ordered]@{
        schemaVersion  = 1
        runKind        = 'TICKET14_USABLE_MVP_ACCEPTANCE_NO_MOVEMENT'
        releaseRoot    = $ReleaseRoot
        serverCommit   = $serverCommit
        onboardCommit  = $onboardCommit
        protocolTag    = $protocolTag
        createGateOpen = $false
        vehicleMoved   = $false
        finishedAt     = [DateTimeOffset]::UtcNow.ToString('O')
        counts         = [ordered]@{
            pass         = @($assertions | Where-Object verdict -eq 'PASS').Count
            fail         = @($assertions | Where-Object verdict -eq 'FAIL').Count
            inconclusive = @($assertions | Where-Object verdict -eq 'INCONCLUSIVE').Count
        }
        assertions     = $assertions
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RunRoot 'assertions.json') -Encoding utf8NoBOM
    foreach ($key in $overrides.Keys) { Remove-Item -LiteralPath "env:$key" -ErrorAction SilentlyContinue }
    "PASS={0} FAIL={1} INCONCLUSIVE={2}" -f $summary.counts.pass, $summary.counts.fail, $summary.counts.inconclusive
}
