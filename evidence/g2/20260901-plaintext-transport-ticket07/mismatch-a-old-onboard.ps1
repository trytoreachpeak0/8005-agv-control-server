#Requires -Version 7
# Ticket 07, mismatch experiment A: the TLS-era onboard build (31263b1, useTls=true) pointed at
# the plaintext ControlServer. Ticket 01 decided the two sides do not negotiate, so the only
# requirement is that the failure is IDENTIFIABLE. This run captures the real error text so
# ticket 04's manual can carry an error cross-reference table instead of leaving the field to
# misread a version mismatch as "the network is down".
$ErrorActionPreference = 'Stop'

$cred = Import-Clixml "$env:LOCALAPPDATA\MesIngestWatch\gpt_win11.credential.xml"
$s = New-PSSession -VMName 'plaintext_onboard_07' -Credential $cred -ConfigurationName 'PowerShell.7'
$credential = (Get-Content -LiteralPath 'F:\w2g-ticket07\secrets\onboard-credential.txt' -Raw).Trim()

# Stop the plaintext peer first: one onboard process at a time, so the log under inspection
# can only have been written by the build being tested.
Invoke-Command -Session $s -ScriptBlock {
    Get-Process SQCD.Agv.Wpf -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 3
    "plaintext onboard stopped: $((Get-Process SQCD.Agv.Wpf -ErrorAction SilentlyContinue) -eq $null)"
}

Copy-Item -Path 'F:\w2g-ticket07\publish\onboard-tls-31263b1' -Destination 'C:\w2g07\' -ToSession $s -Recurse -Force
'copied the TLS-era onboard build into the guest'

Invoke-Command -Session $s -ArgumentList $credential -ScriptBlock {
    param([string]$Credential)
    $ErrorActionPreference = 'Stop'

    $dir = 'C:\w2g07\onboard-tls-31263b1'
    $config = Join-Path $dir 'appsettings.json'
    $runtime = 'C:\w2g07\runtime-tls'
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null

    $raw = Get-Content -LiteralPath $config -Raw
    $settings = ($raw -replace '(?m)^\s*//.*$', '') | ConvertFrom-Json

    '---- the TLS-era build still carries these transport keys ----'
    [PSCustomObject]@{
        hasUseTls        = ($settings.wireToGate.PSObject.Properties.Name -contains 'useTls')
        hasServerCertSha = ($settings.wireToGate.PSObject.Properties.Name -contains 'serverCertificateSha256')
        safetyEndpoint   = $settings.vehicleSafety.endpoint
    } | Format-List | Out-String

    $settings.environment = 'Production'
    $settings.agvId = 'AGV-8005-T07'
    $settings.onboardInstanceId = 'OBU-8005-T07'
    $settings.wireToGate.enabled = $true
    $settings.wireToGate.host = '192.168.200.1'
    $settings.wireToGate.port = 58005
    $settings.wireToGate.onboardBuildCommit = '31263b1ffd372db1f27af5e1143ebad7e7679715'
    $settings.wireToGate.journalPath = Join-Path $runtime 'onboard-journal.db'
    # This build's Validate(production:true) forces a non-empty, non-all-zero pin, so it cannot
    # even be started without one. That is exactly the hard check ticket 06 called out.
    $settings.wireToGate.useTls = $true
    # The shipped file has no serverCertificateSha256 key at all, so a plain assignment throws
    # SetValueInvocationException on a PSCustomObject; Add-Member is the only way to introduce it.
    if ($settings.wireToGate.PSObject.Properties.Name -contains 'serverCertificateSha256') {
        $settings.wireToGate.serverCertificateSha256 = ('ab' * 32)
    } else {
        $settings.wireToGate | Add-Member -NotePropertyName 'serverCertificateSha256' -NotePropertyValue ('ab' * 32)
    }
    $settings.vehicleSafety.enabled = $true
    $settings.vehicleSafety.endpoint = 'https://192.168.200.1:58007/api/onboard/v1/vehicle-safety'
    $settings.vehicleSafety.expectedVehicleKey = 'BROKERX-0c20ff0600d644869a6a80c186065d85'
    $settings.logging.directory = Join-Path $runtime 'onboard-logs'
    $settings.logging.writeToConsole = $true

    [IO.File]::WriteAllText($config, ($settings | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))

    $env:CONTROL_SERVER_ONBOARD_CREDENTIAL = $Credential
    $env:CONTROL_SERVER_OPERATOR_ID = 'S0020310'

    $p = Start-Process -FilePath (Join-Path $dir 'SQCD.Agv.Wpf.exe') -WorkingDirectory $dir `
        -RedirectStandardOutput (Join-Path $runtime 'onboard.out.log') `
        -RedirectStandardError (Join-Path $runtime 'onboard.err.log') -PassThru
    "started TLS-era onboard pid=$($p.Id)"
    Start-Sleep -Seconds 35
    "still running: $(-not $p.HasExited)"
    if ($p.HasExited) { "exit code: $($p.ExitCode)" }

    '---- MISMATCH A: real error text, old onboard (useTls=true) -> plaintext server ----'
    Get-ChildItem (Join-Path $runtime 'onboard-logs') -ErrorAction SilentlyContinue |
        ForEach-Object { Get-Content $_.FullName } | Where-Object { $_ -notmatch 'Modbus' }
    '---- stderr ----'
    Get-Content -LiteralPath (Join-Path $runtime 'onboard.err.log') -Tail 20 -ErrorAction SilentlyContinue
}
Remove-PSSession $s
