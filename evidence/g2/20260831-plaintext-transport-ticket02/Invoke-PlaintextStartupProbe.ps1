#requires -Version 7.0
<#
Starts the Release ControlServer.Host on a NON-loopback binding with no certificate at all and
reads back the three behaviours ticket 02 claims: the listener starts, /health/live answers over
plain HTTP, and the vehicle-safety projection is reachable over plain HTTP but still 401 without a
credential. Isolated ports and an isolated SQLite file; the installed production service is not
touched.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory = $PSScriptRoot,
    [int]$OnboardPort = 58105,
    [int]$HealthPort = 58107
)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$exe = Join-Path $repository 'src\ControlServer.Host\bin\Release\net8.0\win-x64\ControlServer.Host.exe'
if (-not (Test-Path $exe)) { throw "Build the Release host first: $exe" }

$workspace = Join-Path ([System.IO.Path]::GetTempPath()) ("cs-plaintext-probe-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $workspace | Out-Null
$log = Join-Path $OutputDirectory 'plaintext-startup-probe.log'
$report = Join-Path $OutputDirectory 'plaintext-startup-probe.json'
$stdout = Join-Path $workspace 'host.out.log'
$stderr = Join-Path $workspace 'host.err.log'

$env:Health__url = "http://0.0.0.0:$HealthPort"
$env:OnboardTransport__listenAddress = '0.0.0.0'
$env:OnboardTransport__port = "$OnboardPort"
$env:OnboardSafetyProjection__enabled = 'true'
$env:CONTROL_SERVER_ONBOARD_CREDENTIAL = 'probe-only-credential'
$env:ConnectionStrings__ControlServer = "Data Source=$workspace\controlserver.db"

$results = [ordered]@{
    startedAt = (Get-Date).ToString('o')
    exe = $exe
    onboardEndpoint = "0.0.0.0:$OnboardPort"
    healthUrl = $env:Health__url
}

$process = Start-Process -FilePath $exe -WorkingDirectory $workspace -PassThru `
    -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 500
        try {
            $live = Invoke-WebRequest -Uri "http://127.0.0.1:$HealthPort/health/live" -SkipHttpErrorCheck
            if ($live.StatusCode -eq 200) { $ready = $true; break }
        } catch { }
        if ($process.HasExited) { break }
    }
    $results.processExited = $process.HasExited
    $results.healthLiveStatus = if ($ready) { $live.StatusCode } else { $null }
    $results.healthLiveBody = if ($ready) { $live.Content } else { $null }

    if ($ready) {
        $onboard = Test-NetConnection -ComputerName '127.0.0.1' -Port $OnboardPort -WarningAction SilentlyContinue
        $results.onboardTcpConnected = $onboard.TcpTestSucceeded
        $results.onboardListeners = @(Get-NetTCPConnection -State Listen -OwningProcess $process.Id |
            Where-Object { $_.LocalPort -in @($OnboardPort, $HealthPort) } |
            ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" })

        $anonymous = Invoke-WebRequest -Uri "http://127.0.0.1:$HealthPort/api/onboard/v1/vehicle-safety" -SkipHttpErrorCheck
        $results.projectionAnonymousStatus = $anonymous.StatusCode
        $results.projectionAnonymousWwwAuthenticate = $anonymous.Headers['WWW-Authenticate'] -join ','

        $wrong = Invoke-WebRequest -Uri "http://127.0.0.1:$HealthPort/api/onboard/v1/vehicle-safety" `
            -Headers @{ Authorization = 'Bearer wrong-credential' } -SkipHttpErrorCheck
        $results.projectionWrongCredentialStatus = $wrong.StatusCode
    }
}
finally {
    if (-not $process.HasExited) { $process.Kill() }
    $process.WaitForExit(10000) | Out-Null
    $results.hostStdout = Get-Content -Raw -Path $stdout -ErrorAction SilentlyContinue
    $results.hostStderr = Get-Content -Raw -Path $stderr -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $workspace -ErrorAction SilentlyContinue
}

$results.hostStdout | Set-Content -Path $log
$results | ConvertTo-Json -Depth 5 | Set-Content -Path $report
$results | ConvertTo-Json -Depth 5
