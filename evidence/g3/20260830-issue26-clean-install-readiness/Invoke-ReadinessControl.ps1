[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$RunRoot,
    [Parameter(Mandatory)][string]$ResultPath
)

$ErrorActionPreference = 'Stop'
$credential = [Environment]::GetEnvironmentVariable('CONTROL_SERVER_ONBOARD_CREDENTIAL', 'Machine')
if ([string]::IsNullOrWhiteSpace($credential)) { throw 'Machine onboard credential is unavailable.' }
if (Test-Path -LiteralPath $RunRoot) { throw "RunRoot already exists: $RunRoot" }
if (Test-Path -LiteralPath $ResultPath) { throw "ResultPath already exists: $ResultPath" }

$messages = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'captured-handshake.json') | ConvertFrom-Json
function Get-Message([string]$Type) { $messages | Where-Object messageType -eq $Type | Select-Object -First 1 }

function Wait-Port([int]$Port) {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        $client = [Net.Sockets.TcpClient]::new()
        try {
            $task = $client.ConnectAsync('127.0.0.1', $Port)
            if ($task.Wait(500) -and $client.Connected) { return $true }
        }
        catch { }
        finally { $client.Dispose() }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Invoke-Control([string]$Name, [int]$Port, [int]$HealthPort, [bool]$Stopped) {
    $root = Join-Path $RunRoot $Name
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $overrides = @{
        ASPNETCORE_ENVIRONMENT = 'Development'
        ControlServerBuild__commit = 'd243abf'
        CONTROL_SERVER_OPERATOR_ID = 'QUALIFICATION-READONLY'
        ConnectionStrings__ControlServer = "Data Source=$(Join-Path $root 'controlserver.db')"
        Health__url = "http://127.0.0.1:$HealthPort"
        OnboardTransport__port = [string]$Port
        OnboardTransport__allowInsecureLoopback = 'true'
        OnboardSafetyProjection__enabled = 'false'
        JourneyRuntime__enabled = 'false'
        RiotCreateDispatch__enabled = 'false'
        RiotAbsentAtObservationCreateExperiment__enabled = 'false'
    }
    foreach ($pair in $overrides.GetEnumerator()) { Set-Item -Path "env:$($pair.Key)" -Value $pair.Value }

    $process = $null
    $tcp = $null
    try {
        $process = Start-Process -FilePath (Join-Path $PackagePath 'ControlServer.Host.exe') `
            -ArgumentList @('--contentRoot', $PackagePath, '--environment', 'Development') `
            -RedirectStandardOutput (Join-Path $root 'host.out.log') `
            -RedirectStandardError (Join-Path $root 'host.err.log') -PassThru -NoNewWindow
        if (-not (Wait-Port $Port)) { throw "$Name did not open port $Port." }

        $tcp = [Net.Sockets.TcpClient]::new('127.0.0.1', $Port)
        $stream = $tcp.GetStream()
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false))
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        $writer.AutoFlush = $true
        $writer.NewLine = "`n"

        $hello = Get-Message 'SessionHello'
        $hello.payload.credentialProof = $credential
        $writer.WriteLine(($hello | ConvertTo-Json -Depth 20 -Compress))
        $accepted = $reader.ReadLine() | ConvertFrom-Json
        if ($accepted.messageType -ne 'SessionAccepted') { throw "Expected SessionAccepted, got $($accepted.messageType)." }
        $generation = [long]$accepted.sessionGeneration

        $capability = Get-Message 'CapabilitySnapshot'
        $capability.sessionGeneration = $generation
        $writer.WriteLine(($capability | ConvertTo-Json -Depth 20 -Compress))
        $null = $reader.ReadLine()

        $safety = Get-Message 'SafetyStateSnapshot'
        $safety.sessionGeneration = $generation
        if ($Stopped) {
            $safety.payload.safety.departureSafe = $true
            $safety.payload.safety.vehicleStopped = $true
            $safety.payload.safety.reasonCodes = @()
        }
        $writer.WriteLine(($safety | ConvertTo-Json -Depth 20 -Compress))
        $null = $reader.ReadLine()

        $recovery = Get-Message 'RecoveryStateReport'
        $recovery.sessionGeneration = $generation
        $writer.WriteLine(($recovery | ConvertTo-Json -Depth 20 -Compress))
        $ack = $reader.ReadLine() | ConvertFrom-Json
        $readiness = $reader.ReadLine() | ConvertFrom-Json
        [ordered]@{
            name = $Name
            onlySafetyStoppedChanged = $Stopped
            durableAck = $ack.messageType
            readiness = $readiness.payload.readiness
            reasonCodes = @($readiness.payload.reasonCodes)
            databasePath = Join-Path $root 'controlserver.db'
        }
    }
    finally {
        if ($tcp) { $tcp.Dispose() }
        if ($process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    }
}

New-Item -ItemType Directory -Path $RunRoot -Force | Out-Null
$red = Invoke-Control 'red' 58205 58207 $false
$green = Invoke-Control 'green' 58305 58307 $true
$assertions = @(
    [ordered]@{ id = 'RECOVERY-REPORT-PRESENT'; expected = 'DurableAck in both runs'; actual = "$($red.durableAck),$($green.durableAck)"; verdict = if ($red.durableAck -eq 'DurableAck' -and $green.durableAck -eq 'DurableAck') { 'PASS' } else { 'FAIL' } },
    [ordered]@{ id = 'UNSAFE-RED'; expected = 'RECOVERY_REQUIRED / DEPARTURE_SAFETY_NOT_READY'; actual = "$($red.readiness) / $($red.reasonCodes -join ',')"; verdict = if ($red.readiness -eq 'RECOVERY_REQUIRED' -and $red.reasonCodes -contains 'DEPARTURE_SAFETY_NOT_READY') { 'PASS' } else { 'FAIL' } },
    [ordered]@{ id = 'STOPPED-GREEN'; expected = 'READY when only vehicleStopped/departureSafe change'; actual = $green.readiness; verdict = if ($green.readiness -eq 'READY') { 'PASS' } else { 'FAIL' } }
)
$result = [ordered]@{
    schemaVersion = 1
    source = 'ticket 13 captured real handshake with credential redacted'
    productCommit = 'd243abf'
    vehicleMoved = $false
    riotMutationPerformed = $false
    uniqueDifference = 'green changes only payload.safety.vehicleStopped/departureSafe to true and reasonCodes to []'
    runs = @($red, $green)
    assertions = $assertions
}
[IO.File]::WriteAllText($ResultPath, ($result | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
if ($assertions.verdict -contains 'FAIL') { throw "Readiness control failed. Result: $ResultPath" }
Write-Output "Readiness control PASS. Result: $ResultPath"
