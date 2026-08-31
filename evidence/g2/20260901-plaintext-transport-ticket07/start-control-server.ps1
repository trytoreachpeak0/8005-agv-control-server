#Requires -Version 7
# Ticket 07 stage 1: start the plaintext ControlServer bound to the host side of the
# Hyper-V internal switch (192.168.200.1), i.e. a NON-loopback address. The production
# ControlServer.Host keeps 127.0.0.1:58005/58007 and is not touched.
param(
    # Upstream RIoT. Unreachable by default, which makes the projection fail closed to UNKNOWN.
    [string]$RiotBaseUrl = 'http://127.0.0.1:1'
)
$ErrorActionPreference = 'Stop'

$stage = 'F:\w2g-ticket07'
$publish = Join-Path $stage 'publish\control-server-65841df'
$runtime = Join-Path $stage 'runtime'
$logs = Join-Path $stage 'logs'
$secrets = Join-Path $stage 'secrets'
foreach ($d in @($runtime, $logs, $secrets)) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

$listen = '192.168.200.1'
$controlPort = 58005
$healthPort = 58007

# The shared credential never enters Git, the publish output, or the evidence files.
$credentialFile = Join-Path $secrets 'onboard-credential.txt'
if (-not (Test-Path -LiteralPath $credentialFile)) {
    $bytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    [IO.File]::WriteAllText($credentialFile, [Convert]::ToHexString($bytes).ToLowerInvariant())
}
$credential = (Get-Content -LiteralPath $credentialFile -Raw).Trim()

$environment = @{
    'CONTROL_SERVER_ONBOARD_CREDENTIAL' = $credential
    'ConnectionStrings__ControlServer'  = 'Data Source=' + (Join-Path $runtime 'controlserver.db')
    'Health__url'                       = "http://${listen}:$healthPort"
    'OnboardTransport__listenAddress'   = $listen
    'OnboardTransport__port'            = [string]$controlPort
    'OnboardTransport__credentialEnvironmentVariable' = 'CONTROL_SERVER_ONBOARD_CREDENTIAL'
    'OnboardSafetyProjection__enabled'  = 'true'
    'JourneyRuntime__enabled'           = 'false'
    'MesIngest__baseUrl'                = 'http://127.0.0.1:1'
    'RIoT__baseUrl'                     = $RiotBaseUrl
    'CONTROL_SERVER_RIOT_CALL_API_KEY'  = 'ticket07-fake-riot-key'
    'ControlServerBuild__commit'        = '65841df04aa6184106cfd407738fc2ebeb5d968c'
}

$proc = Start-Process -FilePath 'dotnet' `
    -ArgumentList @(Join-Path $publish 'ControlServer.Host.dll') `
    -WorkingDirectory $publish `
    -RedirectStandardOutput (Join-Path $logs 'control.out.log') `
    -RedirectStandardError (Join-Path $logs 'control.err.log') `
    -Environment $environment -WindowStyle Hidden -PassThru

$proc.Id | Set-Content -LiteralPath (Join-Path $runtime 'control-server.pid')
"started ControlServer.Host pid=$($proc.Id)"

$deadline = (Get-Date).AddSeconds(45)
$listeners = @()
do {
    Start-Sleep -Milliseconds 500
    $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $proc.Id -ErrorAction SilentlyContinue)
} while ($listeners.Count -eq 0 -and (Get-Date) -lt $deadline)

'---- listeners owned by this process ----'
$listeners | Select-Object LocalAddress, LocalPort | Format-Table -AutoSize | Out-String
'---- stderr tail ----'
Get-Content -LiteralPath (Join-Path $logs 'control.err.log') -Tail 15 -ErrorAction SilentlyContinue
'---- stdout tail ----'
Get-Content -LiteralPath (Join-Path $logs 'control.out.log') -Tail 15 -ErrorAction SilentlyContinue
