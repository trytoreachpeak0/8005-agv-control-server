#Requires -Version 7

<#
.SYNOPSIS
    Runs one L2 scenario end to end and writes its evidence.

.DESCRIPTION
    Brings up a real ControlServer against the loopback test doubles (fake RIoT, fake MesIngest,
    synthetic Onboard peer), drives the scenario, asserts against the server's own database, writes
    evidence, and tears the environment down.

    This is L2 with a synthetic peer rather than the real onboard WPF: the UI Automation driver
    does not exist yet (landing step 5). What it does prove is the server's cross-end timing under
    a peer that follows the protocol, which is what makes a scenario about the server.

    A PASS here says nothing about real hardware. See RELEASE-CANDIDATE.md section 11.

.EXAMPLE
    .\Invoke-L2Scenario.ps1 -Scenario normal-load -EvidenceRoot ..\..\evidence\l2\20260903-normal-load
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Scenario,

    # Must not exist. Evidence is never overwritten -- a red run that a green re-run erased is the
    # one thing the evidence discipline in CLAUDE.md forbids outright.
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,

    # This script lives in scripts/l2, so the repository root is two levels up, not one.
    [string]$Repository = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),

    # Clear of the field run (58105/58107), the staged runners (58205/58207) and the demand-bearing
    # runner (58305/58307), so an accidental overlap fails to bind rather than quietly talking to
    # the wrong server.
    [int]$ControlPort = 58405,
    [int]$HealthPort = 58407,
    [int]$FakeRiotPort = 58408,
    [int]$FakeMesIngestPort = 58409,
    [int]$FakeOnboardPort = 58410
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

Import-Module (Join-Path $PSScriptRoot 'L2.psm1') -Force

$scenarioPath = Join-Path $PSScriptRoot "scenarios/$Scenario.ps1"
if (-not (Test-Path -LiteralPath $scenarioPath -PathType Leaf)) {
    throw "No such scenario: $Scenario (looked for $scenarioPath)"
}
if (Test-Path -LiteralPath $EvidenceRoot) {
    throw "EvidenceRoot must not exist: $EvidenceRoot"
}

$runStartedAt = [DateTimeOffset]::UtcNow
$runId = $runStartedAt.ToString('yyyyMMddTHHmmssfffZ')
$null = New-Item -ItemType Directory -Path $EvidenceRoot -Force
$logRoot = Join-Path $EvidenceRoot 'logs'
$snapshotRoot = Join-Path $EvidenceRoot 'snapshots'
$null = New-Item -ItemType Directory -Path $logRoot -Force
$null = New-Item -ItemType Directory -Path $snapshotRoot -Force

$stageRoot = Join-Path ([IO.Path]::GetTempPath()) "l2-$runId"
$null = New-Item -ItemType Directory -Path $stageRoot -Force
$databasePath = Join-Path $stageRoot 'controlserver.db'

$journal = New-L2Journal -Path (Join-Path $EvidenceRoot 'timeline.jsonl')
$assertions = New-L2Assertions
$handles = @()
$connection = $null
$outcome = 'FAIL'
$failureReason = $null

try {
    $journal.Note("L2 run $runId starting for scenario '$Scenario'.")

    # Build once, run the built output. `dotnet run` would rebuild under the scenario and put a
    # compiler on the critical path of a timing test.
    $journal.Note('Building ControlServer and the test doubles.')
    $buildLog = Join-Path $logRoot 'build.log'
    & dotnet build (Join-Path $Repository 'ControlServer.sln') -c Release --nologo *>&1 |
        Tee-Object -FilePath $buildLog | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build failed; see $buildLog" }

    $configuration = 'Release'
    $framework = 'net8.0/win-x64'
    $hostDirectory = Join-Path $Repository "src/ControlServer.Host/bin/$configuration/$framework"
    $riotDirectory = Join-Path $Repository "tools/ControlServer.FakeRiot/bin/$configuration/$framework"
    $mesDirectory = Join-Path $Repository "tools/ControlServer.FakeMesIngest/bin/$configuration/$framework"
    $onboardDirectory = Join-Path $Repository "tools/ControlServer.FakeOnboard/bin/$configuration/$framework"

    $credential = [guid]::NewGuid().ToString('N')
    $agvId = 'AGV-L2-001'
    $vehicleKey = 'BROKERX-L2-0001'
    $mapIdentity = 'MAP-L2'
    $mapId = 25
    $gateStationId = '关卡'
    $gateStationRiotId = 210
    $pickupStationRiotId = 12

    # 1. The doubles first. Both are pure loopback services with no dependency on the server, and
    #    starting them first means the server never meets a dead port during its first poll.
    $handles += Start-L2Process -Name 'fake-riot' `
        -FilePath (Join-Path $riotDirectory 'ControlServer.FakeRiot.exe') `
        -ArgumentList @(
            "--FakeRiot:port=$FakeRiotPort",
            "--FakeRiot:instanceId=l2-riot",
            "--FakeRiot:Seed:vehicleKey=$vehicleKey",
            "--FakeRiot:Seed:mapIdentity=$mapIdentity",
            "--FakeRiot:Seed:mapId=$mapId",
            "--FakeRiot:Seed:startStationId=$gateStationRiotId") `
        -WorkingDirectory $riotDirectory -LogRoot $logRoot |
        ForEach-Object { $_ | Add-Member -NotePropertyName Order -NotePropertyValue 1 -PassThru }

    $handles += Start-L2Process -Name 'fake-mes-ingest' `
        -FilePath (Join-Path $mesDirectory 'ControlServer.FakeMesIngest.exe') `
        -ArgumentList @(
            "--FakeMesIngest:port=$FakeMesIngestPort",
            "--FakeMesIngest:instanceId=l2-mes") `
        -WorkingDirectory $mesDirectory -LogRoot $logRoot |
        ForEach-Object { $_ | Add-Member -NotePropertyName Order -NotePropertyValue 2 -PassThru }

    $riot = New-L2Double -Name 'fake-riot' -BaseUrl "http://127.0.0.1:$FakeRiotPort"
    $mes = New-L2Double -Name 'fake-mes-ingest' -BaseUrl "http://127.0.0.1:$FakeMesIngestPort"

    $null = Wait-L2Condition -Description 'fake RIoT is live' -Journal $journal -Criterion 'fake-riot-live' `
        -Probe { $riot.Health().body.status } -Until { param($v) $v -eq 'live' }
    $null = Wait-L2Condition -Description 'fake MesIngest is live' -Journal $journal -Criterion 'fake-mes-live' `
        -Probe { $mes.Health().body.status } -Until { param($v) $v -eq 'live' }

    # 2. Package capacity is a server-side rule table, not something MesIngest supplies. Without it
    #    every candidate is refused PACKAGE_CAPACITY_NOT_UNIQUE, so it is seeded through the
    #    server's own import command rather than by writing rows behind its back.
    $capacityCsv = Join-Path $stageRoot 'package-capacity.csv'
    @(
        'pattern,match_type,max_boxes_per_basket,source,status,note'
        'L2-PACKAGE,exact,4,l2-scenario,active,L2 scenario fixture'
    ) | Set-Content -LiteralPath $capacityCsv -Encoding utf8NoBOM

    $serverEnvironment = @{
        'CONTROL_SERVER_ONBOARD_CREDENTIAL'               = $credential
        'ConnectionStrings__ControlServer'                = "Data Source=$databasePath"
        'Health__url'                                     = "http://127.0.0.1:$HealthPort"
        'OnboardTransport__listenAddress'                 = '127.0.0.1'
        'OnboardTransport__port'                          = [string]$ControlPort
        'MesIngest__baseUrl'                              = "http://127.0.0.1:$FakeMesIngestPort"
        'RIoT__baseUrl'                                   = "http://127.0.0.1:$FakeRiotPort"
        'RiotCreateDispatch__enabled'                     = 'true'
        'JourneyRuntime__enabled'                         = 'true'
        'JourneyRuntime__pollInterval'                    = '00:00:01'
        'JourneyRuntime__agvId'                           = $agvId
        'JourneyRuntime__vehicleKey'                      = $vehicleKey
        'JourneyRuntime__mapId'                           = [string]$mapId
        'JourneyRuntime__mapIdentity'                     = $mapIdentity
        'JourneyRuntime__gateStationId'                   = $gateStationId
        'JourneyRuntime__gateStationRiotId'               = [string]$gateStationRiotId
        'JourneyRuntime__admissionPolicyDeploymentId'     = "L2-$runId"
    }

    $importEnvironment = @{}
    foreach ($key in $serverEnvironment.Keys) { $importEnvironment[$key] = $serverEnvironment[$key] }
    $importEnvironment['JourneyRuntime__enabled'] = 'false'
    $import = Start-L2Process -Name 'package-capacity-import' `
        -FilePath (Join-Path $hostDirectory 'ControlServer.Host.exe') `
        -ArgumentList @('--import-package-capacity', '--input', $capacityCsv, '--version', '1') `
        -WorkingDirectory $hostDirectory -Environment $importEnvironment -LogRoot $logRoot
    $null = $import.Process.WaitForExit(120000)
    if ($import.Process.ExitCode -ne 0) {
        throw "Package capacity import failed with exit code $($import.Process.ExitCode)."
    }
    $journal.Note('Package capacity rules imported.')

    # 3. ControlServer, against the doubles.
    $handles += Start-L2Process -Name 'control-server' `
        -FilePath (Join-Path $hostDirectory 'ControlServer.Host.exe') `
        -WorkingDirectory $hostDirectory -Environment $serverEnvironment -LogRoot $logRoot |
        ForEach-Object { $_ | Add-Member -NotePropertyName Order -NotePropertyValue 3 -PassThru }

    # /health/live, not /health/ready: readiness means a peer has completed the recovery handshake,
    # and the peer cannot connect until the server is listening. Waiting on readiness here would
    # deadlock the startup order against itself.
    $null = Wait-L2Condition -Description 'ControlServer is listening' -Journal $journal -Criterion 'control-server-live' `
        -TimeoutSeconds 120 `
        -Probe { (Invoke-RestMethod -Uri "http://127.0.0.1:$HealthPort/health/live" -TimeoutSec 5).status } `
        -Until { param($v) $v -eq 'live' }

    # 4. The synthetic peer last: it connects out to the server, so the server has to be listening.
    $handles += Start-L2Process -Name 'fake-onboard' `
        -FilePath (Join-Path $onboardDirectory 'ControlServer.FakeOnboard.exe') `
        -ArgumentList @(
            "--FakeOnboard:port=$FakeOnboardPort",
            "--FakeOnboard:instanceId=l2-onboard",
            "--FakeOnboard:Peer:port=$ControlPort",
            "--FakeOnboard:Peer:agvId=$agvId") `
        -WorkingDirectory $onboardDirectory `
        -Environment @{ 'CONTROL_SERVER_ONBOARD_CREDENTIAL' = $credential } `
        -LogRoot $logRoot |
        ForEach-Object { $_ | Add-Member -NotePropertyName Order -NotePropertyValue 4 -PassThru }

    $onboard = New-L2Double -Name 'fake-onboard' -BaseUrl "http://127.0.0.1:$FakeOnboardPort"
    $null = Wait-L2Condition -Description 'the synthetic peer reached READY' -Journal $journal -Criterion 'onboard-readiness' `
        -TimeoutSeconds 60 `
        -Probe { $onboard.Snapshot().body.readiness } -Until { param($v) $v -eq 'READY' }

    # Now readiness is meaningful: the peer finished the handshake and the server granted it.
    $null = Wait-L2Condition -Description 'ControlServer reports the vehicle ready' -Journal $journal `
        -Criterion 'control-server-ready' -TimeoutSeconds 60 `
        -Probe { (Invoke-RestMethod -Uri "http://127.0.0.1:$HealthPort/health/ready" -TimeoutSec 5).status } `
        -Until { param($v) $v -eq 'ready' }

    $connection = Open-L2Database -HostDirectory $hostDirectory -DatabasePath $databasePath

    $context = [pscustomobject]@{
        Journal             = $journal
        Assertions          = $assertions
        Riot                = $riot
        MesIngest           = $mes
        Onboard             = $onboard
        Connection          = $connection
        RunId               = $runId
        AgvId               = $agvId
        VehicleKey          = $vehicleKey
        MapId               = $mapId
        MapIdentity         = $mapIdentity
        GateStationRiotId   = $gateStationRiotId
        PickupStationRiotId = $pickupStationRiotId
        HealthPort          = $HealthPort
        SnapshotRoot        = $snapshotRoot
    }

    $journal.Note("Environment is up; entering scenario '$Scenario'.")
    & $scenarioPath -Context $context
    $outcome = if ($assertions.AllPassed()) { 'PASS' } else { 'FAIL' }
} catch {
    $outcome = 'FAIL'
    $failureReason = $_.Exception.Message
    $journal.Note("Run failed: $failureReason")
    Write-Warning $failureReason
} finally {
    # Snapshots before teardown, so a failed run keeps the state that explains it. Each is written
    # independently: a double that already died must not stop the others being captured.
    foreach ($double in @(
        @{ Name = 'fake-riot'; Port = $FakeRiotPort },
        @{ Name = 'fake-mes-ingest'; Port = $FakeMesIngestPort },
        @{ Name = 'fake-onboard'; Port = $FakeOnboardPort })) {
        try {
            $body = Invoke-RestMethod -Uri "http://127.0.0.1:$($double.Port)/control/v1/snapshot" -TimeoutSec 5
            [IO.File]::WriteAllText(
                (Join-Path $snapshotRoot "$($double.Name).json"),
                ($body | ConvertTo-Json -Depth 12),
                [Text.UTF8Encoding]::new($false))
        } catch {
            Write-Warning "Could not snapshot $($double.Name): $_"
        }
    }
    if ($connection) {
        foreach ($table in @('JourneyRuntimes', 'AcceptedDemands', 'JourneyBacklog', 'OrderIntents',
                             'StationOperations', 'SessionRecoveries')) {
            try {
                $rows = Invoke-L2Query -Connection $connection -Sql "SELECT * FROM $table"
                [IO.File]::WriteAllText(
                    (Join-Path $snapshotRoot "db-$table.json"),
                    ($rows | ConvertTo-Json -Depth 8),
                    [Text.UTF8Encoding]::new($false))
            } catch {
                Write-Warning "Could not snapshot table ${table}: $_"
            }
        }
        try { $connection.Close(); $connection.Dispose() } catch { }
    }

    Stop-L2Process -Handles $handles

    Write-L2Evidence -EvidenceRoot $EvidenceRoot -Scenario $Scenario -RunId $runId `
        -Assertions $assertions -Outcome $outcome -FailureReason $failureReason `
        -Identity @{
            controlServerCommit = (& git -C $Repository rev-parse HEAD 2>$null)
            agvId               = $agvId
            vehicleKey          = $vehicleKey
            stageRoot           = $stageRoot
        }

    # The stage root is left behind on failure: its controlserver.db is usually the only place the
    # cause is written down.
    if ($outcome -eq 'PASS') {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Warning "Stage root kept for diagnosis: $stageRoot"
    }
}

Write-Host "L2 $Scenario -> $outcome (evidence: $EvidenceRoot)"
if ($outcome -ne 'PASS') { exit 1 }
