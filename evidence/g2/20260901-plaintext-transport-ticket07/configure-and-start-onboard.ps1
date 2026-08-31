#Requires -Version 7
# Ticket 07 stage 1: point the plaintext onboard build (238b46e) at the ControlServer
# running on the OTHER machine (192.168.200.1) and start it inside the guest.
# environment=Production on purpose: that is the only mode where WireToGateSettings.Validate
# and VehicleSafetySettings.Validate run IsForbiddenProductionHost, so a loopback shortcut
# would be rejected outright.
$ErrorActionPreference = 'Stop'

$cred = Import-Clixml "$env:LOCALAPPDATA\MesIngestWatch\gpt_win11.credential.xml"
$s = New-PSSession -VMName 'plaintext_onboard_07' -Credential $cred

$credential = (Get-Content -LiteralPath 'F:\w2g-ticket07\secrets\onboard-credential.txt' -Raw).Trim()

$result = Invoke-Command -Session $s -ArgumentList $credential -ScriptBlock {
    param([string]$Credential)
    $ErrorActionPreference = 'Stop'

    $dir = 'C:\w2g07\onboard-plaintext-238b46e'
    $config = Join-Path $dir 'appsettings.json'
    $runtime = 'C:\w2g07\runtime'
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null

    # Windows PowerShell 5.1 inside the guest rejects the two `//` comment lines that the
    # shipped ioModule block carries, so strip whole comment lines first. The pattern is
    # anchored to the start of a line so it cannot touch `http://` inside a value.
    $rawConfig = Get-Content -LiteralPath $config -Raw
    $settings = ($rawConfig -replace '(?m)^\s*//.*$', '') | ConvertFrom-Json

    $settings.environment = 'Production'
    $settings.agvId = 'AGV-8005-T07'
    $settings.onboardInstanceId = 'OBU-8005-T07'

    $settings.wireToGate.enabled = $true
    $settings.wireToGate.host = '192.168.200.1'
    $settings.wireToGate.port = 58005
    $settings.wireToGate.onboardBuildCommit = '238b46eb2c9ae90584e4288a782176f66b7de942'
    $settings.wireToGate.journalPath = Join-Path $runtime 'onboard-journal.db'

    $settings.vehicleSafety.enabled = $true
    $settings.vehicleSafety.endpoint = 'http://192.168.200.1:58007/api/onboard/v1/vehicle-safety'
    $settings.vehicleSafety.expectedVehicleKey = 'BROKERX-0c20ff0600d644869a6a80c186065d85'

    $settings.logging.directory = Join-Path $runtime 'onboard-logs'
    $settings.logging.writeToConsole = $true

    # Windows PowerShell 5.1 has no utf8NoBOM encoding and its UTF8 writes a BOM, which the
    # onboard build's own JsonDocument.Parse would choke on. Write it through .NET instead.
    [IO.File]::WriteAllText(
        $config,
        ($settings | ConvertTo-Json -Depth 30),
        [Text.UTF8Encoding]::new($false))

    # Read back the keys that decide this run, and prove the removed transport keys are absent.
    $written = Get-Content -LiteralPath $config -Raw
    $back = ($written -replace '(?m)^\s*//.*$', '') | ConvertFrom-Json
    '---- onboard config read-back ----'
    [PSCustomObject]@{
        environment      = $back.environment
        wtgHost          = $back.wireToGate.host
        wtgPort          = $back.wireToGate.port
        wtgEnabled       = $back.wireToGate.enabled
        safetyEndpoint   = $back.vehicleSafety.endpoint
        safetyEnabled    = $back.vehicleSafety.enabled
        expectedKey      = $back.vehicleSafety.expectedVehicleKey
        hasUseTls        = ($back.wireToGate.PSObject.Properties.Name -contains 'useTls')
        hasServerCertSha = ($back.wireToGate.PSObject.Properties.Name -contains 'serverCertificateSha256')
        rawMentionsTls   = ($written -match 'useTls|serverCertificateSha256|https')
    } | Format-List | Out-String

    # Start it. A PowerShell Direct process is not the interactive desktop, so the window is
    # never visible; the run is judged from the onboard log file and the server side, not pixels.
    $env:CONTROL_SERVER_ONBOARD_CREDENTIAL = $Credential
    $env:CONTROL_SERVER_OPERATOR_ID = 'S0020310'

    Get-Process SQCD.Agv.Wpf -ErrorAction SilentlyContinue | Stop-Process -Force
    $logDir = Join-Path $runtime 'onboard-logs'
    if (Test-Path $logDir) { Remove-Item -Recurse -Force $logDir }

    $p = Start-Process -FilePath (Join-Path $dir 'SQCD.Agv.Wpf.exe') -WorkingDirectory $dir `
        -RedirectStandardOutput (Join-Path $runtime 'onboard.out.log') `
        -RedirectStandardError (Join-Path $runtime 'onboard.err.log') `
        -PassThru
    "started onboard pid=$($p.Id)"
    Start-Sleep -Seconds 20
    "still running after 20s: $(-not $p.HasExited)"
    if ($p.HasExited) { "exit code: $($p.ExitCode)" }

    '---- stderr ----'
    Get-Content -LiteralPath (Join-Path $runtime 'onboard.err.log') -Tail 25 -ErrorAction SilentlyContinue
    '---- stdout ----'
    Get-Content -LiteralPath (Join-Path $runtime 'onboard.out.log') -Tail 40 -ErrorAction SilentlyContinue
    '---- log files ----'
    Get-ChildItem $logDir -ErrorAction SilentlyContinue | Select-Object Name, Length | Format-Table -AutoSize | Out-String
}
$result
Remove-PSSession $s
