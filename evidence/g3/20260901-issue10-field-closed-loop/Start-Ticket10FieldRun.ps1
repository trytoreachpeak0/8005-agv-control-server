[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [Parameter(Mandatory)][string]$OperatorId,
    [Parameter(Mandatory)][int]$DispatchGeneration,
    # Explicit per-run authorization. Without it the script refuses: this run opens the RIoT create
    # gate, creates real orders and moves a real vehicle. It requires both a per-run authorization
    # and an on-site physical safety GO, every time, and neither is reusable.
    [Parameter(Mandatory)][switch]$ConfirmRealVehicleMovement,
    [string]$ReleaseRoot = 'C:\Users\szy\Desktop\w2g-rc-20260901b-19ce7db',
    [string]$SimulatorExe = 'C:\Users\szy\w2g-stage\simulator\src\SQCD_8005AGV_Simulator\bin\Release\net8.0-windows\SQCD_8005AGV_Simulator.exe',
    [string]$BindAddress = '192.168.200.1',
    [int]$ControlPort = 58705,
    [int]$HealthPort = 58707
)

# Ticket 10 field closed loop on the PLAINTEXT release candidate. Same shape as the ticket 14 run
# (three products started detached, observation window decided on the floor), with the entire TLS
# apparatus removed: no self-signed certificate, no CurrentUser\Root install, no pinned SHA-256,
# no PFX password. Link A is NDJSON over plain TCP and link B is plain HTTP.
#
# Stop-Ticket10FieldRun.ps1 ends the run and reads the outcome out of the isolated store.

$ErrorActionPreference = 'Stop'
if (-not $ConfirmRealVehicleMovement) { throw 'This run moves a real vehicle and requires -ConfirmRealVehicleMovement.' }
if (-not $env:CONTROL_SERVER_RIOT_CALL_API_KEY) { throw 'CONTROL_SERVER_RIOT_CALL_API_KEY is not set.' }
if (-not $env:CONTROL_SERVER_ONBOARD_CREDENTIAL) { throw 'CONTROL_SERVER_ONBOARD_CREDENTIAL is not set.' }
if ($OperatorId -eq 'REHEARSAL-OPERATOR') { throw 'A formal run needs the operator actually standing at the HMI.' }
if ($DispatchGeneration -lt 1) { throw 'dispatchGeneration must be a positive integer.' }
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

# The evidence fields written by this script are our own literals, so they cannot show that the run
# was really plaintext. Take an observation the script never writes: the ordered thumbprint set of
# every certificate store the TLS-era runs touched. Stop-Ticket10FieldRun.ps1 recomputes it.
function Get-StoreDigest {
    param(
        [System.Security.Cryptography.X509Certificates.StoreName]$Name,
        [System.Security.Cryptography.X509Certificates.StoreLocation]$Location
    )
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($Name, $Location)
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
    $thumbprints = @($store.Certificates | ForEach-Object { $_.Thumbprint } | Sort-Object)
    $store.Close(); $store.Dispose()
    $canonical = $thumbprints -join "`n"
    $digest = [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
    return [pscustomobject]@{ Store = "$Location\$Name"; Count = $thumbprints.Count; Digest = $digest }
}

$storeDigests = @(
    (Get-StoreDigest -Name Root -Location CurrentUser),
    (Get-StoreDigest -Name My -Location CurrentUser),
    (Get-StoreDigest -Name Root -Location LocalMachine),
    (Get-StoreDigest -Name My -Location LocalMachine)
)

# The production service must not be disturbed by this run. Bind the identity by PID, not by process
# name: the isolated instance runs the same image name and a name lookup would mix the two.
$productionService = Get-CimInstance -ClassName Win32_Service -Filter "Name='8005 AGV ControlServer'"
$productionPid = [int]$productionService.ProcessId
$productionPorts = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
    Where-Object { $_.OwningProcess -eq $productionPid } |
    ForEach-Object { '{0}:{1}' -f $_.LocalAddress, $_.LocalPort } | Sort-Object)

# ------------------------------------------------------------------------- onboard deployment
$onboardRun = Join-Path $RunRoot 'onboard-hmi'
Copy-Item -LiteralPath (Join-Path $ReleaseRoot 'onboard-hmi') -Destination $onboardRun -Recurse
$onboard = Get-Content -LiteralPath (Join-Path $onboardRun 'appsettings.Production.template.json') -Raw | ConvertFrom-Json
$onboard.agvId = '老厂前线新多仓位1'
$onboard.onboardInstanceId = 'OBU-8005-TICKET10-FIELD'
# environment stays Production: that is the only mode where IsForbiddenProductionHost actually runs,
# so binding a non-loopback address is a checked fact rather than a convention.
$onboard.ruleGateway.host = '127.0.0.1'
$onboard.wireToGate.host = $BindAddress
$onboard.wireToGate.port = $ControlPort
$onboard.wireToGate.onboardInstanceId = '4f6d1c2e-9d3a-4a55-9d0b-14ab2f0e77c3'
$onboard.wireToGate.journalPath = Join-Path $RunRoot 'onboard-journal.db'
$onboard.vehicleSafety.endpoint = "http://${BindAddress}:$HealthPort/api/onboard/v1/vehicle-safety"
$onboard.vehicleSafety.expectedVehicleKey = 'BROKERX-0c20ff0600d644869a6a80c186065d85'
$onboard.ioModule.host = '127.0.0.1'
$onboard.ioModule.port = 1502
$onboardConfigPath = Join-Path $onboardRun 'appsettings.json'
$onboard | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $onboardConfigPath -Encoding utf8NoBOM

# Read the written file back rather than trusting the object we just serialised.
$onboardConfigText = Get-Content -LiteralPath $onboardConfigPath -Raw
$placeholders = @(Select-String -LiteralPath $onboardConfigPath -Pattern 'REPLACE_' -AllMatches |
    ForEach-Object { $_.Line.Trim() })
if ($placeholders.Count -gt 0) {
    throw "The onboard configuration still carries template placeholders: $($placeholders -join ' | ')"
}
$tlsKeyHits = @('useTls', 'serverCertificateSha256', 'serverCertificatePath', 'https://' |
    Where-Object { $onboardConfigText -match [regex]::Escape($_) })
if ($tlsKeyHits.Count -gt 0) {
    throw "The onboard configuration still carries TLS-era settings: $($tlsKeyHits -join ', ')"
}

# ------------------------------------------------------------------------------------- launch
$dbPath = Join-Path $RunRoot 'controlserver.db'
$overrides = @{
    'ASPNETCORE_ENVIRONMENT'                           = 'Development'
    'ControlServerBuild__commit'                       = $manifest.components.controlServer.commit
    'CONTROL_SERVER_OPERATOR_ID'                       = $OperatorId
    'ConnectionStrings__ControlServer'                 = "Data Source=$dbPath"
    'Health__url'                                      = "http://${BindAddress}:$HealthPort"
    'OnboardTransport__port'                           = [string]$ControlPort
    'OnboardTransport__listenAddress'                  = $BindAddress
    'OnboardSafetyProjection__enabled'                 = 'true'
    'JourneyRuntime__enabled'                          = 'true'
    # upperId is W2G-{demandId}-PICKUP-{generation} / -GATE-{generation}. A generation already used
    # at RIoT reconciles against the existing terminal order instead of creating one.
    'JourneyRuntime__dispatchGeneration'               = [string]$DispatchGeneration
    'RiotCreateDispatch__enabled'                      = 'true'
    'RiotAbsentAtObservationCreateExperiment__enabled' = 'false'
}
# The four TLS-era keys are refused at startup by OnboardTransportOptionsValidator. Injecting one as
# an empty environment variable still counts as the key being present, so assert they are absent.
foreach ($removed in @('OnboardTransport__serverCertificatePath',
                       'OnboardTransport__serverCertificatePasswordEnvironmentVariable',
                       'OnboardTransport__allowInsecureLoopback',
                       'OnboardSafetyProjection__requireHttps')) {
    if ($overrides.ContainsKey($removed)) { throw "Override $removed was removed in this version." }
    Remove-Item -Path "env:$removed" -ErrorAction SilentlyContinue
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
    runKind               = 'TICKET10_FIELD_CLOSED_LOOP_ON_PLAINTEXT_RELEASE_CANDIDATE'
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
    onboardConfigPath     = $onboardConfigPath
    storeDigestsAtStart   = $storeDigests
    productionServicePid  = $productionPid
    productionServicePorts = $productionPorts
    simulatorPid          = $simulator.Id
    hostPid               = $hostProcess.Id
    onboardPid            = $onboardProcess.Id
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $RunRoot 'run-start.json') -Encoding utf8NoBOM

"STARTED RunRoot=$RunRoot host=$($hostProcess.Id) onboard=$($onboardProcess.Id) simulator=$($simulator.Id) generation=$DispatchGeneration plaintext=tcp:$ControlPort http:$HealthPort"
