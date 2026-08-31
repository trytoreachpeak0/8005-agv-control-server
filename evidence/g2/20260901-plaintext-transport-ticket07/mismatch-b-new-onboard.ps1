#Requires -Version 7
# Ticket 07, mismatch experiment B: the plaintext onboard build (238b46e) pointed at the
# TLS-era ControlServer (3d8b00c7) on a non-loopback address. The other direction of the
# cross-reference table ticket 04 has to carry.
$ErrorActionPreference = 'Stop'

$cred = Import-Clixml "$env:LOCALAPPDATA\MesIngestWatch\gpt_win11.credential.xml"
$s = New-PSSession -VMName 'plaintext_onboard_07' -Credential $cred -ConfigurationName 'PowerShell.7'
$credential = (Get-Content -LiteralPath 'F:\w2g-ticket07\secrets\onboard-credential.txt' -Raw).Trim()

Invoke-Command -Session $s -ArgumentList $credential -ScriptBlock {
    param([string]$Credential)
    $ErrorActionPreference = 'Stop'

    Get-Process SQCD.Agv.Wpf -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    "all onboard processes stopped: $((Get-Process SQCD.Agv.Wpf -ErrorAction SilentlyContinue) -eq $null)"

    $dir = 'C:\w2g07\onboard-plaintext-238b46e'
    $config = Join-Path $dir 'appsettings.json'
    $runtime = 'C:\w2g07\runtime-mismatch-b'
    if (Test-Path $runtime) { Remove-Item -Recurse -Force $runtime }
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null

    $settings = ((Get-Content -LiteralPath $config -Raw) -replace '(?m)^\s*//.*$', '') | ConvertFrom-Json
    $settings.wireToGate.port = 58105
    $settings.wireToGate.journalPath = Join-Path $runtime 'onboard-journal.db'
    # The plaintext build's VehicleSafetySettings.Validate only accepts Uri.UriSchemeHttp, so
    # "just switch it to https" is not even expressible here; pointing plain http at the TLS
    # port is the shape the field will actually produce.
    $settings.vehicleSafety.endpoint = 'http://192.168.200.1:58107/api/onboard/v1/vehicle-safety'
    $settings.logging.directory = Join-Path $runtime 'onboard-logs'
    [IO.File]::WriteAllText($config, ($settings | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))

    $env:CONTROL_SERVER_ONBOARD_CREDENTIAL = $Credential
    $env:CONTROL_SERVER_OPERATOR_ID = 'S0020310'

    $p = Start-Process -FilePath (Join-Path $dir 'SQCD.Agv.Wpf.exe') -WorkingDirectory $dir `
        -RedirectStandardOutput (Join-Path $runtime 'onboard.out.log') `
        -RedirectStandardError (Join-Path $runtime 'onboard.err.log') -PassThru
    "started plaintext onboard against the TLS-era server, pid=$($p.Id)"
    Start-Sleep -Seconds 35
    "still running: $(-not $p.HasExited)"
    if ($p.HasExited) { "exit code: $($p.ExitCode)" }

    '---- MISMATCH B: real error text, plaintext onboard -> TLS-era server ----'
    Get-ChildItem (Join-Path $runtime 'onboard-logs') -ErrorAction SilentlyContinue |
        ForEach-Object { Get-Content $_.FullName } | Where-Object { $_ -notmatch 'Modbus' } | Select-Object -First 20

    '---- link B seen directly from the guest: plain HTTP against the TLS port ----'
    try {
        $r = Invoke-WebRequest -Uri 'http://192.168.200.1:58107/api/onboard/v1/vehicle-safety' `
            -Headers @{ Authorization = "Bearer $Credential" } -TimeoutSec 10 -UseBasicParsing
        "UNEXPECTED SUCCESS status=$($r.StatusCode)"
    } catch { "error text: $($_.Exception.Message)" }
}
Remove-PSSession $s
