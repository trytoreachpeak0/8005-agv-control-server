#Requires -Version 7
# Ticket 07, mismatch experiment B support: bring up the TLS-era ControlServer (3d8b00c7) on a
# NON-loopback address so the plaintext onboard build can be pointed at it.
#
# The certificate is generated straight to a PFX file through the .NET APIs and is never
# imported into any certificate store. Ticket 03's acceptance rests on CurrentUser\Root being
# untouched across install and uninstall, and this run must not disturb that evidence.
$ErrorActionPreference = 'Stop'

$stage = 'F:\w2g-ticket07'
$publish = Join-Path $stage 'publish\control-server-tls-3d8b00c7'
$runtime = Join-Path $stage 'runtime-tls'
$logs = Join-Path $stage 'logs'
$secrets = Join-Path $stage 'secrets'
foreach ($d in @($runtime, $logs, $secrets)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

$listen = '192.168.200.1'
$controlPort = 58105
$healthPort = 58107

$pfxPath = Join-Path $secrets 'ticket07-tls-era.pfx'
$pfxPasswordFile = Join-Path $secrets 'ticket07-tls-era.password'
if (-not (Test-Path -LiteralPath $pfxPath)) {
    $bytes = [byte[]]::new(24)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [IO.File]::WriteAllText($pfxPasswordFile, [Convert]::ToHexString($bytes).ToLowerInvariant())

    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
        "CN=$listen",
        $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $sanBuilder = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
    $sanBuilder.AddIpAddress([Net.IPAddress]::Parse($listen))
    $request.CertificateExtensions.Add($sanBuilder.Build())
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $false))
    $oids = [Security.Cryptography.OidCollection]::new()
    $oids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1')) | Out-Null
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $false))

    $certificate = $request.CreateSelfSigned(
        [DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(30))
    $password = (Get-Content -LiteralPath $pfxPasswordFile -Raw).Trim()
    [IO.File]::WriteAllBytes(
        $pfxPath,
        $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))
    "generated a throwaway PFX (never imported into any store)"
}
$password = (Get-Content -LiteralPath $pfxPasswordFile -Raw).Trim()
$credential = (Get-Content -LiteralPath (Join-Path $secrets 'onboard-credential.txt') -Raw).Trim()

# Prove nothing landed in the trust store: this count must match what ticket 03 recorded.
"CurrentUser\Root certificate count: $((Get-ChildItem Cert:\CurrentUser\Root).Count)"

$environment = @{
    'CONTROL_SERVER_ONBOARD_CREDENTIAL' = $credential
    'CONTROL_SERVER_ONBOARD_CERTIFICATE_PASSWORD' = $password
    'ConnectionStrings__ControlServer'  = 'Data Source=' + (Join-Path $runtime 'controlserver-tls.db')
    'Health__url'                       = "https://${listen}:$healthPort"
    'OnboardTransport__listenAddress'   = $listen
    'OnboardTransport__port'            = [string]$controlPort
    'OnboardTransport__serverCertificatePath' = $pfxPath
    'OnboardTransport__allowInsecureLoopback' = 'false'
    'JourneyRuntime__enabled'           = 'false'
    'MesIngest__baseUrl'                = 'http://127.0.0.1:1'
    'RIoT__baseUrl'                     = 'http://127.0.0.1:58888'
    'CONTROL_SERVER_RIOT_CALL_API_KEY'  = 'ticket07-fake-riot-key'
    'ControlServerBuild__commit'        = '3d8b00c7558ae700358f1f995a5ac75d12a3250c'
}

$proc = Start-Process -FilePath 'dotnet' `
    -ArgumentList @(Join-Path $publish 'ControlServer.Host.dll') `
    -WorkingDirectory $publish `
    -RedirectStandardOutput (Join-Path $logs 'control-tls.out.log') `
    -RedirectStandardError (Join-Path $logs 'control-tls.err.log') `
    -Environment $environment -WindowStyle Hidden -PassThru
$proc.Id | Set-Content -LiteralPath (Join-Path $runtime 'control-server-tls.pid')
"started TLS-era ControlServer pid=$($proc.Id)"

$deadline = (Get-Date).AddSeconds(60)
do {
    Start-Sleep -Milliseconds 500
    $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $proc.Id -ErrorAction SilentlyContinue)
} while ($listeners.Count -eq 0 -and (Get-Date) -lt $deadline)
'---- listeners ----'
$listeners | Select-Object LocalAddress, LocalPort | Format-Table -AutoSize | Out-String
'---- stdout tail ----'
Get-Content -LiteralPath (Join-Path $logs 'control-tls.out.log') -Tail 12 -ErrorAction SilentlyContinue
'---- stderr tail ----'
Get-Content -LiteralPath (Join-Path $logs 'control-tls.err.log') -Tail 12 -ErrorAction SilentlyContinue
