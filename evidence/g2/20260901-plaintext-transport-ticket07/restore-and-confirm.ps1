#Requires -Version 7
# Ticket 07 closing pass: tear down the TLS-era peer, put the onboard build back on the
# plaintext server, and take the final green reading with a fresh session generation.
$ErrorActionPreference = 'Stop'

$tlsPid = Get-Content 'F:\w2g-ticket07\runtime-tls\control-server-tls.pid' -ErrorAction SilentlyContinue
if ($tlsPid) { Stop-Process -Id $tlsPid -Force -ErrorAction SilentlyContinue; "stopped the TLS-era server pid=$tlsPid" }

"CurrentUser\Root after the whole TLS-era experiment: $((Get-ChildItem Cert:\CurrentUser\Root).Count)"

$cred = Import-Clixml "$env:LOCALAPPDATA\MesIngestWatch\gpt_win11.credential.xml"
$s = New-PSSession -VMName 'plaintext_onboard_07' -Credential $cred -ConfigurationName 'PowerShell.7'
$credential = (Get-Content -LiteralPath 'F:\w2g-ticket07\secrets\onboard-credential.txt' -Raw).Trim()

Invoke-Command -Session $s -ArgumentList $credential -ScriptBlock {
    param([string]$Credential)
    $ErrorActionPreference = 'Stop'

    Get-Process SQCD.Agv.Wpf -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3

    $dir = 'C:\w2g07\onboard-plaintext-238b46e'
    $config = Join-Path $dir 'appsettings.json'
    $runtime = 'C:\w2g07\runtime-final'
    if (Test-Path $runtime) { Remove-Item -Recurse -Force $runtime }
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null

    $settings = ((Get-Content -LiteralPath $config -Raw) -replace '(?m)^\s*//.*$', '') | ConvertFrom-Json
    $settings.wireToGate.port = 58005
    $settings.wireToGate.journalPath = Join-Path $runtime 'onboard-journal.db'
    $settings.vehicleSafety.endpoint = 'http://192.168.200.1:58007/api/onboard/v1/vehicle-safety'
    $settings.logging.directory = Join-Path $runtime 'onboard-logs'
    [IO.File]::WriteAllText($config, ($settings | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))

    $env:CONTROL_SERVER_ONBOARD_CREDENTIAL = $Credential
    $env:CONTROL_SERVER_OPERATOR_ID = 'S0020310'
    $p = Start-Process -FilePath (Join-Path $dir 'SQCD.Agv.Wpf.exe') -WorkingDirectory $dir `
        -RedirectStandardOutput (Join-Path $runtime 'onboard.out.log') `
        -RedirectStandardError (Join-Path $runtime 'onboard.err.log') -PassThru
    Start-Sleep -Seconds 30

    '---- final green: onboard log ----'
    Get-ChildItem (Join-Path $runtime 'onboard-logs') -ErrorAction SilentlyContinue |
        ForEach-Object { Get-Content $_.FullName } | Where-Object { $_ -notmatch 'Modbus' }

    '---- guest-side sockets to the plaintext server ----'
    Get-NetTCPConnection -RemoteAddress '192.168.200.1' -State Established -ErrorAction SilentlyContinue |
        Select-Object LocalAddress, LocalPort, RemoteAddress, RemotePort | Format-Table -AutoSize | Out-String

    '---- guest reads the projection over plaintext HTTP ----'
    (Invoke-WebRequest -Uri 'http://192.168.200.1:58007/api/onboard/v1/vehicle-safety' `
        -Headers @{ Authorization = "Bearer $Credential" } -TimeoutSec 10 -UseBasicParsing).Content
}
Remove-PSSession $s
