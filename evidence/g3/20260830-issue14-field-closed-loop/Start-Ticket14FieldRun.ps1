[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [Parameter(Mandatory)][string]$OperatorId,
    [Parameter(Mandatory)][int]$DispatchGeneration,
    # Explicit per-run authorization. Without it the script refuses: this run opens the RIoT create
    # gate, creates real orders and moves a real vehicle. It requires both a per-run authorization
    # and an on-site physical safety GO, every time, and neither is reusable.
    [Parameter(Mandatory)][switch]$ConfirmRealVehicleMovement,
    [string]$ReleaseRoot = 'C:\Users\szy\Desktop\w2g-rc-20260830-81cb9cf',
    [string]$SimulatorExe = 'C:\Users\szy\w2g-stage\simulator\src\SQCD_8005AGV_Simulator\bin\Release\net8.0-windows\SQCD_8005AGV_Simulator.exe',
    [string]$BindAddress = '192.168.200.1',
    [int]$ControlPort = 58425,
    [int]$HealthPort = 58427
)

# Ticket 14 field closed loop on the release candidate. Starts the three products detached and
# returns, so the observation window is decided by what happens on the floor rather than by a
# launcher timeout. Stop-Ticket14FieldRun.ps1 ends the run and reads the outcome out of the store.
#
# Unlike the ticket 13 and 2026-08-29 field runs, this deployment is the RC itself: the packaged
# ControlServer, the packaged OnboardHmi configured from its own production template, TLS with a
# pinned certificate, and the HTTPS vehicle-safety projection enabled.

$ErrorActionPreference = 'Stop'
if (-not $ConfirmRealVehicleMovement) { throw 'This run moves a real vehicle and requires -ConfirmRealVehicleMovement.' }
if (-not $env:CONTROL_SERVER_RIOT_CALL_API_KEY) { throw 'CONTROL_SERVER_RIOT_CALL_API_KEY is not set.' }
if (-not $env:CONTROL_SERVER_ONBOARD_CREDENTIAL) { throw 'CONTROL_SERVER_ONBOARD_CREDENTIAL is not set.' }
if ($OperatorId -eq 'REHEARSAL-OPERATOR') { throw 'A formal run needs the operator actually standing at the HMI.' }
if ($DispatchGeneration -lt 4) { throw "Generations 1 to 3 are already consumed at RIoT; this run needs a fresh one." }
if (Test-Path -LiteralPath $RunRoot) { throw "RunRoot already exists: $RunRoot" }
New-Item -ItemType Directory -Path $RunRoot -Force | Out-Null

$package = Join-Path $ReleaseRoot 'controlserver'
$manifest = Get-Content -LiteralPath (Join-Path $ReleaseRoot 'release-manifest.json') -Raw | ConvertFrom-Json
foreach ($exe in @((Join-Path $package 'ControlServer.Host.exe'), $SimulatorExe)) {
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing executable: $exe" }
}
foreach ($port in @($ControlPort, $HealthPort, 1502, 58006)) {
    if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue) { throw "Port $port already in use." }
}

function Wait-Listening([int]$Port, [int]$TimeoutSeconds = 60) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# ------------------------------------------------------------------------------- TLS material
$key = [System.Security.Cryptography.RSA]::Create(2048)
$request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
    "CN=8005 ticket14 field loopback $([Guid]::NewGuid().ToString('N'))",
    $key,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
$null = $request.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
$oids = [System.Security.Cryptography.OidCollection]::new()
$null = $oids.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
$null = $request.CertificateExtensions.Add(
    [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $true))
$san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
$san.AddDnsName('localhost')
$san.AddIpAddress([System.Net.IPAddress]::Loopback)
$san.AddIpAddress([System.Net.IPAddress]::Parse($BindAddress))
$null = $request.CertificateExtensions.Add($san.Build())
$certificate = $request.CreateSelfSigned(
    [DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddHours(12))
$pfxPassword = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant()
$pfxPath = Join-Path $RunRoot 'loopback-server.pfx'
[IO.File]::WriteAllBytes(
    $pfxPath, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $pfxPassword))
$certificateSha256 = [Convert]::ToHexString(
    [System.Security.Cryptography.SHA256]::HashData($certificate.RawData)).ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $RunRoot 'loopback-server.pem') -Encoding ascii -Value (
    "-----BEGIN CERTIFICATE-----`n" +
    [Convert]::ToBase64String($certificate.RawData, 'InsertLineBreaks') +
    "`n-----END CERTIFICATE-----")

$store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
    [System.Security.Cryptography.X509Certificates.StoreName]::Root,
    [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
$store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
$store.Add($certificate)
$store.Close(); $store.Dispose()

# ------------------------------------------------------------------------- onboard deployment
$onboardRun = Join-Path $RunRoot 'onboard-hmi'
Copy-Item -LiteralPath (Join-Path $ReleaseRoot 'onboard-hmi') -Destination $onboardRun -Recurse
$onboard = Get-Content -LiteralPath (Join-Path $onboardRun 'appsettings.Production.template.json') -Raw | ConvertFrom-Json
$onboard.agvId = '老厂前线新多仓位1'
$onboard.onboardInstanceId = 'OBU-8005-TICKET14-FIELD'
$onboard.ruleGateway.host = '127.0.0.1'
$onboard.wireToGate.host = $BindAddress
$onboard.wireToGate.port = $ControlPort
$onboard.wireToGate.onboardInstanceId = '4f6d1c2e-9d3a-4a55-9d0b-14ab2f0e77c3'
$onboard.wireToGate.useTls = $true
$onboard.wireToGate.serverCertificateSha256 = $certificateSha256
$onboard.wireToGate.journalPath = Join-Path $RunRoot 'onboard-journal.db'
$onboard.vehicleSafety.endpoint = "https://${BindAddress}:$HealthPort/api/onboard/v1/vehicle-safety"
$onboard.vehicleSafety.expectedVehicleKey = 'BROKERX-0c20ff0600d644869a6a80c186065d85'
$onboard.ioModule.host = '127.0.0.1'
$onboard.ioModule.port = 1502
$onboard | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $onboardRun 'appsettings.json') -Encoding utf8NoBOM
if (@(Select-String -LiteralPath (Join-Path $onboardRun 'appsettings.json') -Pattern 'REPLACE_' -AllMatches).Count -gt 0) {
    throw 'The onboard configuration still carries template placeholders.'
}

# ------------------------------------------------------------------------------------- launch
$dbPath = Join-Path $RunRoot 'controlserver.db'
$overrides = @{
    'ASPNETCORE_ENVIRONMENT'                           = 'Development'
    'ControlServerBuild__commit'                       = $manifest.components.controlServer.commit
    'CONTROL_SERVER_OPERATOR_ID'                       = $OperatorId
    'CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD'      = $pfxPassword
    'ConnectionStrings__ControlServer'                 = "Data Source=$dbPath"
    'Health__url'                                      = "https://${BindAddress}:$HealthPort"
    'OnboardTransport__port'                           = [string]$ControlPort
    'OnboardTransport__listenAddress'                  = $BindAddress
    'OnboardTransport__serverCertificatePath'          = $pfxPath
    'OnboardTransport__allowInsecureLoopback'          = 'false'
    'OnboardSafetyProjection__enabled'                 = 'true'
    'JourneyRuntime__enabled'                          = 'true'
    # upperId is W2G-{demandId}-PICKUP-{generation} / -GATE-{generation}. A generation already used
    # at RIoT reconciles against the existing terminal order instead of creating one.
    'JourneyRuntime__dispatchGeneration'               = [string]$DispatchGeneration
    'RiotCreateDispatch__enabled'                      = 'true'
    'RiotAbsentAtObservationCreateExperiment__enabled' = 'false'
}
foreach ($pair in $overrides.GetEnumerator()) { Set-Item -Path "env:$($pair.Key)" -Value $pair.Value }

$simulator = Start-Process -FilePath $SimulatorExe -PassThru
if (-not ((Wait-Listening -Port 1502) -and (Wait-Listening -Port 58006))) {
    $simulator.Kill(); throw 'Simulator did not open its ports.'
}
$hostProcess = Start-Process -FilePath (Join-Path $package 'ControlServer.Host.exe') `
    -ArgumentList @('--contentRoot', $package, '--environment', 'Development') `
    -RedirectStandardOutput (Join-Path $RunRoot 'host.out.log') `
    -RedirectStandardError (Join-Path $RunRoot 'host.err.log') `
    -PassThru -NoNewWindow
if (-not ((Wait-Listening -Port $ControlPort) -and (Wait-Listening -Port $HealthPort))) {
    $hostProcess.Kill(); $simulator.Kill(); throw 'ControlServer did not open its ports.'
}
$onboardProcess = Start-Process -FilePath (Join-Path $onboardRun 'SQCD.Agv.Wpf.exe') `
    -WorkingDirectory $onboardRun -PassThru

[ordered]@{
    schemaVersion         = 1
    runKind               = 'TICKET14_FIELD_CLOSED_LOOP_ON_RELEASE_CANDIDATE'
    releaseRoot           = $ReleaseRoot
    serverCommit          = $manifest.components.controlServer.commit
    onboardCommit         = $manifest.components.onboardHmi.commit
    protocolTag           = $manifest.components.protocol.tag
    startedAt             = [DateTimeOffset]::UtcNow.ToString('O')
    createGateOpen        = $true
    runtimeEnabled        = $true
    dispatchGeneration    = $DispatchGeneration
    operatorId            = $OperatorId
    bindAddress           = $BindAddress
    controlPort           = $ControlPort
    healthPort            = $HealthPort
    databasePath          = $dbPath
    onboardRun            = $onboardRun
    certificateThumbprint = $certificate.Thumbprint
    simulatorPid          = $simulator.Id
    hostPid               = $hostProcess.Id
    onboardPid            = $onboardProcess.Id
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $RunRoot 'run-start.json') -Encoding utf8NoBOM

"STARTED RunRoot=$RunRoot host=$($hostProcess.Id) onboard=$($onboardProcess.Id) simulator=$($simulator.Id) generation=$DispatchGeneration"
