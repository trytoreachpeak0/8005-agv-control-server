[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [string]$ReleaseRoot = 'C:\Users\szy\Desktop\w2g-rc-20260830-81cb9cf',
    [string]$BindAddress = '192.168.200.1',
    [int]$ControlPort = 58415,
    [int]$HealthPort = 58417,
    [int]$SessionObserveSeconds = 45
)

# Falsifiability for the ticket 14 acceptance run. Each mutation changes exactly one field of the
# configuration that produced SESSION-ESTABLISHED / SESSION-READY, and the run asserts that the
# corresponding green turns red. Nothing here is expected to reach Ready.

$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $RunRoot) { throw "RunRoot already exists: $RunRoot" }
New-Item -ItemType Directory -Path $RunRoot -Force | Out-Null

$package = Join-Path $ReleaseRoot 'controlserver'
$assertions = [System.Collections.Generic.List[object]]::new()

function Wait-Listening([int]$Port, [int]$TimeoutSeconds = 60) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function New-LoopbackCertificate([string]$Directory) {
    $key = [System.Security.Cryptography.RSA]::Create(2048)
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
        "CN=8005 ticket14 redside loopback $([Guid]::NewGuid().ToString('N'))",
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
        [DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddHours(4))
    $password = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant()
    $pfx = Join-Path $Directory 'loopback-server.pfx'
    [IO.File]::WriteAllBytes(
        $pfx, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12, $password))
    return [pscustomobject]@{
        Certificate = $certificate
        PfxPath     = $pfx
        Password    = $password
        Sha256      = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($certificate.RawData)).ToLowerInvariant()
        Thumbprint  = $certificate.Thumbprint
    }
}

function Invoke-Mutation {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Mutation,
        [Parameter(Mandatory)][string]$Expected,
        [Parameter(Mandatory)][scriptblock]$Apply
    )

    $caseRoot = Join-Path $RunRoot $Id
    New-Item -ItemType Directory -Path $caseRoot -Force | Out-Null
    $tls = New-LoopbackCertificate $caseRoot

    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new(
        [System.Security.Cryptography.X509Certificates.StoreName]::Root,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    $store.Add($tls.Certificate)
    $store.Close(); $store.Dispose()

    $onboardRun = Join-Path $caseRoot 'onboard-hmi'
    Copy-Item -LiteralPath (Join-Path $ReleaseRoot 'onboard-hmi') -Destination $onboardRun -Recurse
    $onboard = Get-Content -LiteralPath (Join-Path $onboardRun 'appsettings.Production.template.json') -Raw | ConvertFrom-Json
    $onboard.agvId = '老厂前线新多仓位1'
    $onboard.onboardInstanceId = 'OBU-8005-TICKET14-RED'
    $onboard.ruleGateway.host = '127.0.0.1'
    $onboard.wireToGate.host = $BindAddress
    $onboard.wireToGate.port = $ControlPort
    $onboard.wireToGate.onboardInstanceId = '4f6d1c2e-9d3a-4a55-9d0b-14ab2f0e77c2'
    $onboard.wireToGate.useTls = $true
    $onboard.wireToGate.serverCertificateSha256 = $tls.Sha256
    $onboard.wireToGate.journalPath = Join-Path $caseRoot 'onboard-journal.db'
    $onboard.vehicleSafety.endpoint = "https://${BindAddress}:$HealthPort/api/onboard/v1/vehicle-safety"
    $onboard.vehicleSafety.expectedVehicleKey = 'BROKERX-0c20ff0600d644869a6a80c186065d85'
    $onboard.ioModule.host = '127.0.0.1'
    $onboard.ioModule.port = 1502
    & $Apply $onboard
    $onboard | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $onboardRun 'appsettings.json') -Encoding utf8NoBOM

    $overrides = @{
        'ASPNETCORE_ENVIRONMENT'                           = 'Development'
        'CONTROL_SERVER_OPERATOR_ID'                       = 'TICKET14-REDSIDE'
        'CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD'      = $tls.Password
        'ConnectionStrings__ControlServer'                 = "Data Source=$(Join-Path $caseRoot 'controlserver.db')"
        'Health__url'                                      = "https://${BindAddress}:$HealthPort"
        'OnboardTransport__port'                           = [string]$ControlPort
        'OnboardTransport__listenAddress'                  = $BindAddress
        'OnboardTransport__serverCertificatePath'          = $tls.PfxPath
        'OnboardTransport__allowInsecureLoopback'          = 'false'
        'OnboardSafetyProjection__enabled'                 = 'true'
        'JourneyRuntime__enabled'                          = 'false'
        'RiotCreateDispatch__enabled'                      = 'false'
        'RiotAbsentAtObservationCreateExperiment__enabled' = 'false'
    }
    foreach ($pair in $overrides.GetEnumerator()) { Set-Item -Path "env:$($pair.Key)" -Value $pair.Value }

    $hostProcess = $null; $onboardProcess = $null
    try {
        $hostProcess = Start-Process -FilePath (Join-Path $package 'ControlServer.Host.exe') `
            -ArgumentList @('--contentRoot', $package, '--environment', 'Development') `
            -RedirectStandardOutput (Join-Path $caseRoot 'host.out.log') `
            -RedirectStandardError (Join-Path $caseRoot 'host.err.log') -PassThru -NoNewWindow
        if (-not ((Wait-Listening -Port $ControlPort) -and (Wait-Listening -Port $HealthPort))) {
            throw 'ControlServer did not open its ports for the red-side case.'
        }
        $onboardProcess = Start-Process -FilePath (Join-Path $onboardRun 'SQCD.Agv.Wpf.exe') `
            -WorkingDirectory $onboardRun -PassThru
        Start-Sleep -Seconds $SessionObserveSeconds

        $logFile = Get-ChildItem -Path (Join-Path $onboardRun 'logs') -Filter '*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $sessionLine = if ($logFile) {
            (Select-String -LiteralPath $logFile.FullName -Pattern '上层会话已建立' | Select-Object -Last 1).Line
        } else { $null }
        $readiness = if ($sessionLine -match 'readiness=(\w+)') { $Matches[1] } else { '<no session line>' }
        return [ordered]@{
            id = $Id; mutation = $Mutation; expected = $Expected
            sessionLine = ($sessionLine ?? '<none>'); readiness = $readiness
        }
    }
    finally {
        foreach ($process in @($onboardProcess, $hostProcess)) {
            if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit(20000) | Out-Null }
        }
        $cleanup = [System.Security.Cryptography.X509Certificates.X509Store]::new(
            [System.Security.Cryptography.X509Certificates.StoreName]::Root,
            [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
        $cleanup.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        foreach ($certificate in @($cleanup.Certificates.Find(
                    [System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
                    $tls.Thumbprint, $false))) {
            $cleanup.Remove($certificate)
        }
        $cleanup.Close(); $cleanup.Dispose()
        Remove-Item -LiteralPath $tls.PfxPath -Force -ErrorAction SilentlyContinue
        foreach ($key in $overrides.Keys) { Remove-Item -LiteralPath "env:$key" -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds 3
    }
}

$simulator = Start-Process -FilePath 'C:\Users\szy\w2g-stage\simulator\src\SQCD_8005AGV_Simulator\bin\Release\net8.0-windows\SQCD_8005AGV_Simulator.exe' -PassThru
try {
    $null = Wait-Listening -Port 1502

    # M1 targets SESSION-ESTABLISHED: the pinned server certificate hash is the only field changed.
    $m1 = Invoke-Mutation -Id 'M1-TLS-PIN' `
        -Mutation 'wireToGate.serverCertificateSha256 last hex digit flipped' `
        -Expected 'no session is established at all' `
        -Apply {
            param($settings)
            $hash = $settings.wireToGate.serverCertificateSha256
            $last = if ($hash[-1] -eq '0') { '1' } else { '0' }
            $settings.wireToGate.serverCertificateSha256 = $hash.Substring(0, 63) + $last
        }
    $m1.verdict = if ($m1.sessionLine -eq '<none>') { 'RED-CONFIRMED' } else { 'NOT-RED' }
    $assertions.Add($m1)
    "[{0}] {1}: {2}" -f $m1.verdict, $m1.id, $m1.readiness | Write-Host

    # M2 targets SESSION-READY: the session must still be established, but the safety evidence no
    # longer matches the vehicle the onboard expects, so readiness must not reach Ready.
    $m2 = Invoke-Mutation -Id 'M2-VEHICLE-KEY' `
        -Mutation 'vehicleSafety.expectedVehicleKey changed to another well-formed key' `
        -Expected 'session established, readiness stays RecoveryRequired' `
        -Apply {
            param($settings)
            $settings.vehicleSafety.expectedVehicleKey = 'BROKERX-ffffffffffffffffffffffffffffffff'
        }
    $m2.verdict = if ($m2.sessionLine -ne '<none>' -and $m2.readiness -ne 'Ready') { 'RED-CONFIRMED' } else { 'NOT-RED' }
    $assertions.Add($m2)
    "[{0}] {1}: {2}" -f $m2.verdict, $m2.id, $m2.readiness | Write-Host
}
finally {
    if ($simulator -and -not $simulator.HasExited) { $simulator.Kill(); $simulator.WaitForExit(20000) | Out-Null }
    [ordered]@{
        schemaVersion = 1
        runKind       = 'TICKET14_ACCEPTANCE_RED_SIDE'
        releaseRoot   = $ReleaseRoot
        finishedAt    = [DateTimeOffset]::UtcNow.ToString('O')
        mutations     = $assertions
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RunRoot 'mutations.json') -Encoding utf8NoBOM
}
