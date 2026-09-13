#Requires -Version 7

<#
.SYNOPSIS
    Runs one L2 scenario end to end and writes its evidence.

.DESCRIPTION
    Brings up a real ControlServer against the loopback test doubles (fake RIoT, fake MesIngest,
    synthetic Onboard peer), drives the scenario, asserts against the server's own database, writes
    evidence, and tears the environment down.

    A scenario whose sibling setup file says `Onboard = 'Real'` gets a different rig: the shipped
    onboard WPF driven through UI Automation, plus the real slots simulator supplying Modbus IO.
    The two go together -- without the simulator the onboard reports every slot UNKNOWN, so
    departureSafe never becomes true and the server never grants readiness.

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
    #
    # **Below 49152, which is where they moved on 2026-09-08 and why.** Windows' default dynamic
    # port range is 49152-65535, and an outbound connection may take any port in it as its source.
    # The old block (58405-58413) sat inside that range, so an unrelated program on the machine
    # could hold one of these ports at any moment -- and it did: a proxy's outbound connection had
    # 58410 bound to 0.0.0.0, and the run died with SocketException 10013 rather than 10048,
    # which reads like a permissions problem rather than a collision. A fixed-port rig inside the
    # ephemeral range is flaky by construction, and this ticket's own exit criterion is three
    # consecutive green runs.
    [int]$ControlPort = 48405,
    [int]$HealthPort = 48407,
    [int]$FakeRiotPort = 48408,
    [int]$FakeMesIngestPort = 48409,
    # Real-onboard rig only. The simulator's own defaults are 58006/1502; moving both keeps an L2
    # run from talking to a simulator someone left open for hand testing.
    [int]$SimulatorHttpPort = 48411,
    [int]$SimulatorModbusPort = 48412,
    # Only started when a scenario asks for clock skew; see scenarios/*.setup.psd1.
    [int]$ClockSkewProxyPort = 48413,
    # Only started when a scenario sets Dashboard = $true; see scenarios/*.setup.psd1.
    [int]$DashboardPort = 48414,
    # One port per synthetic peer, counting up from here, so a fleet of N takes 48420..48420+N-1.
    # Its own block rather than a neighbour of the others: the peers are the only component whose
    # count is not fixed, and the old layout put peer 1 and peer 2 straight onto the simulator's
    # two ports. Those two rigs are mutually exclusive today, which made it a latent collision
    # rather than a live one -- the kind that surfaces the first time someone relaxes that.
    [int]$FakeOnboardPort = 48420,

    # The two peer repositories are read-only for agents, so they are never built in place: each is
    # cloned to the cache below and published from the clone. Siblings of this repository by
    # default, which is how the workspace lays them out.
    [string]$OnboardRepository,
    [string]$SimulatorRepository,
    [string]$PeerCacheRoot = (Join-Path $env:LOCALAPPDATA '8005-l2-peers'),

    # Which batch's exit this run is evidence for. Specification 8.4 asks assertions.json to be
    # able to answer "whose exit evidence is this", and nothing in the repository can derive it:
    # a batch is a plan, not a property of the code. It is a parameter with a default rather than
    # a constant so that CI states it explicitly and a later batch changes one argument.
    [string]$BatchId = 'batch-2'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

Import-Module (Join-Path $PSScriptRoot 'L2.psm1') -Force
# Only the real-onboard rig ever takes the desktop lock, but the import stays unconditional so the
# dependency is visible at the top rather than buried in a branch 150 lines down.
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'DesktopLock.psm1') -Force

$scenarioPath = Join-Path $PSScriptRoot "scenarios/$Scenario.ps1"
if (-not (Test-Path -LiteralPath $scenarioPath -PathType Leaf)) {
    throw "No such scenario: $Scenario (looked for $scenarioPath)"
}

# A scenario that needs the environment to start differently says so in a sibling data file rather
# than in a switch the caller has to remember: forgetting -SomeSeed would leave the scenario green
# while proving something else entirely. Optional -- most scenarios only change things at runtime.
$setupPath = Join-Path $PSScriptRoot "scenarios/$Scenario.setup.psd1"
$setup = if (Test-Path -LiteralPath $setupPath -PathType Leaf) {
    Import-PowerShellDataFile -LiteralPath $setupPath
} else {
    @{}
}
# Which rig this scenario needs is declared in that same data file, for the same reason the seed is:
# a run against the wrong peer is green about the wrong thing.
$realOnboard = ($setup.ContainsKey('Onboard') -and $setup.Onboard -eq 'Real')
if ($setup.ContainsKey('Onboard') -and $setup.Onboard -notin @('Real', 'Synthetic')) {
    throw "Unknown Onboard rig in $Scenario.setup.psd1: $($setup.Onboard) (expected Real or Synthetic)"
}
if ($realOnboard -and $setup.ContainsKey('OnboardSeed')) {
    throw "OnboardSeed only applies to the synthetic peer; the real onboard reads its own IO."
}
# Clock skew is injected on the wire, in front of the server's vehicle-safety projection, so it
# only means anything to a peer that actually evaluates freshness -- which the synthetic one does
# not.
$clockSkewMs = if ($setup.ContainsKey('ClockSkewMs')) { [int]$setup.ClockSkewMs } else { $null }
if ($null -ne $clockSkewMs -and -not $realOnboard) {
    throw "ClockSkewMs needs Onboard = 'Real': the synthetic peer has no freshness check to skew."
}
# RESUME_AFTER_REPAIR is off in the shipped onboard appsettings and the server has no recovery proof
# configured, so both ends have to be turned on together or the handshake fails halfway with an
# authentication rejection that looks like a protocol fault. Declared per scenario rather than for
# every real-onboard run, so real-onboard-normal-load keeps running the configuration it went green
# against.
$recoveryResume = ($setup.ContainsKey('RecoveryResume') -and $setup.RecoveryResume)
if ($recoveryResume -and -not $realOnboard) {
    throw "RecoveryResume needs Onboard = 'Real': the synthetic peer never starts a recovery session."
}
# Not a secret: it authorises nothing outside this loopback rig, and the whole point of the run is
# that it is written down in the evidence.
$recoveryProofVariable = 'CONTROL_SERVER_RECOVERY_PROOF'
$recoveryProof = 'l2-recovery-proof-not-a-production-secret'
# FP-IS-14's activation entry point. Off in the product, because what it sends makes a vehicle swap its
# own slot IO bindings; a scenario that proves the activation path turns it on here, the same switch a
# site turns on deliberately.
$slotConfigurationActivation = ($setup.ContainsKey('SlotConfigurationActivation') -and $setup.SlotConfigurationActivation)
$governanceCredentialVariable = 'CONTROL_SERVER_GOVERNANCE_CREDENTIAL'
# Not a secret either, for the same reason as the recovery proof above.
$governanceCredential = 'l2-governance-credential-not-a-production-secret'
# The dashboard process. It reads only the server's read-only /api/dashboard/ endpoints over HTTP, so
# starting it changes nothing about the server under test.
$dashboard = ($setup.ContainsKey('Dashboard') -and $setup.Dashboard)
if (-not $OnboardRepository) {
    $OnboardRepository = Join-Path (Split-Path -Parent $Repository) '8005-agv-onboard-hmi'
}
if (-not $SimulatorRepository) {
    $SimulatorRepository = Join-Path (Split-Path -Parent $Repository) 'slots-simulator'
}
if (Test-Path -LiteralPath $EvidenceRoot) {
    throw "EvidenceRoot must not exist: $EvidenceRoot"
}

$runStartedAt = [DateTimeOffset]::UtcNow
$runId = $runStartedAt.ToString('yyyyMMddTHHmmssfffZ')
$null = New-Item -ItemType Directory -Path $EvidenceRoot -Force
# Absolute from here on. The onboard resolves its log directory against its own working directory,
# which is the stage root, so a relative -EvidenceRoot would scatter its log somewhere neither the
# evidence nor the operator ever looks.
$EvidenceRoot = (Resolve-Path -LiteralPath $EvidenceRoot).Path
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
# Declared out here because the evidence block in `finally` reports them, and a run can fail before
# the peers are built.
$onboardPublish = $null
$simulatorPublish = $null
# Read off /version once the server is up, never restated here. Specification 8.4 wants L2 evidence
# to be invalidated by a protocol generation the way a G2 gate-result is, and the only identity that
# can do that is the one the build actually enforces on the wire -- restating a triple in this
# script would make the evidence agree with the script rather than with the server.
$protocolReleaseIdentity = $null
# Held only by the real-onboard rig, and released in `finally` after teardown. Declared here so that
# release is unconditional even when the run dies before acquiring it.
$desktopLock = $null

try {
    $journal.Note("L2 run $runId starting for scenario '$Scenario'.")

    # Build once, run the built output. `dotnet run` would rebuild under the scenario and put a
    # compiler on the critical path of a timing test.
    $journal.Note('Building ControlServer and the test doubles.')
    $buildLog = Join-Path $logRoot 'build.log'
    & dotnet build (Join-Path $Repository 'ControlServer.sln') -c Release --nologo *>&1 |
        Tee-Object -FilePath $buildLog | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build failed; see $buildLog" }

    # The two peers come from repositories this workspace may not write to, so they are published
    # out of throwaway clones and cached by commit. First run of a given commit pays for a build;
    # every run after that pays for a directory copy.
    if ($realOnboard) {
        $onboardPublish = Get-L2PeerPublish -Name 'onboard-hmi' -Journal $journal `
            -SourceRepository $OnboardRepository `
            -ProjectPath 'src/SQCD.Agv.Wpf/SQCD.Agv.Wpf.csproj' `
            -CacheRoot $PeerCacheRoot -LogRoot $logRoot
        $simulatorPublish = Get-L2PeerPublish -Name 'slots-simulator' -Journal $journal `
            -SourceRepository $SimulatorRepository `
            -ProjectPath 'src/SQCD_8005AGV_Simulator/SQCD_8005AGV_Simulator.csproj' `
            -CacheRoot $PeerCacheRoot -LogRoot $logRoot
        $journal.Note("Peers: onboard-hmi@$($onboardPublish.Commit), slots-simulator@$($simulatorPublish.Commit).")

        # From here on this run puts two WPF windows on win11-01's single interactive desktop, so it
        # must own that desktop machine-wide -- 8005-mes-ingest's golden renderer and desktop test
        # suite take the same mutex, and GitHub's per-repository `concurrency` cannot see across the
        # two. See scripts/DesktopLock.psm1.
        #
        # Acquired here rather than at the top of the run on purpose: building the server and
        # publishing the peers touches neither the desktop nor a port, so a run that queues for the
        # lock queues holding nothing. Everything after this line does hold something.
        $desktopLock = Enter-DesktopLock -Reason "L2 scenario '$Scenario' (real onboard rig)"
        $journal.Note('Interactive desktop lock acquired.')
    }

    $configuration = 'Release'
    $framework = 'net8.0/win-x64'
    $hostDirectory = Join-Path $Repository "src/ControlServer.Host/bin/$configuration/$framework"
    $riotDirectory = Join-Path $Repository "tools/ControlServer.FakeRiot/bin/$configuration/$framework"
    $mesDirectory = Join-Path $Repository "tools/ControlServer.FakeMesIngest/bin/$configuration/$framework"
    $onboardDirectory = Join-Path $Repository "tools/ControlServer.FakeOnboard/bin/$configuration/$framework"
    $skewProxyDirectory = Join-Path $Repository "tools/ControlServer.ClockSkewProxy/bin/$configuration/$framework"
    $fieldOpsDirectory = Join-Path $Repository "tools/ControlServer.FieldOps/bin/$configuration/$framework"
    $dashboardDirectory = Join-Path $Repository "src/ControlServer.Dashboard/bin/$configuration/$framework"

    $credential = [guid]::NewGuid().ToString('N')
    $agvId = 'AGV-L2-001'
    $vehicleKey = 'BROKERX-L2-0001'
    # The vehicles this run drives, primary first. A scenario that says nothing gets exactly one
    # -- the single-vehicle deployment every existing scenario was written against, where
    # JourneyRuntime:Fleet stays empty and the server derives its one roster entry from the fields
    # below. A scenario that lists Fleet gets those vehicles *in addition to* the primary pair
    # rather than instead of it, because the options validator requires the roster to contain the
    # primary pair: stating it in every setup file would be a line nobody could get right in a
    # second way.
    $fleet = @(
        @{ AgvId = $agvId; VehicleKey = $vehicleKey }
    )
    if ($setup.ContainsKey('Fleet')) {
        foreach ($vehicle in $setup.Fleet) {
            if (-not $vehicle.ContainsKey('AgvId') -or -not $vehicle.ContainsKey('VehicleKey')) {
                throw "Every Fleet entry in $Scenario.setup.psd1 needs both AgvId and VehicleKey."
            }
            $fleet += @{ AgvId = $vehicle.AgvId; VehicleKey = $vehicle.VehicleKey }
        }
    }
    $mapIdentity = 'MAP-L2'
    $mapId = 25
    $gateStationId = '关卡'
    $gateStationRiotId = 210
    $pickupStationRiotId = 12

    # getRouteCostsBy answers reachable for every station unless a scenario says otherwise. A
    # scenario that wants the pre-create gate to refuse -- or to disagree with the route graph --
    # seeds a negative cost here, keyed "{mapId}:{stationId}".
    $riotArguments = @(
        "--FakeRiot:port=$FakeRiotPort",
        "--FakeRiot:instanceId=l2-riot",
        "--FakeRiot:Seed:vehicleKey=$vehicleKey",
        "--FakeRiot:Seed:mapIdentity=$mapIdentity",
        "--FakeRiot:Seed:mapId=$mapId",
        "--FakeRiot:Seed:startStationId=$gateStationRiotId")
    # Every vehicle past the first. They start at rest on the same Map at the same station, which
    # is what a fleet parked at the gate looks like before the first round.
    for ($index = 1; $index -lt $fleet.Count; $index++) {
        $riotArguments += "--FakeRiot:Seed:AdditionalVehicleKeys:$($index - 1)=$($fleet[$index].VehicleKey)"
    }
    if ($setup.ContainsKey('RouteCosts')) {
        foreach ($key in ($setup.RouteCosts.Keys | Sort-Object)) {
            $riotArguments += "--FakeRiot:Seed:RouteCosts:$key=$($setup.RouteCosts[$key])"
        }
    }

    # 1. The doubles first. Both are pure loopback services with no dependency on the server, and
    #    starting them first means the server never meets a dead port during its first poll.
    $riotHandle = Start-L2Process -Name 'fake-riot' `
        -FilePath (Join-Path $riotDirectory 'ControlServer.FakeRiot.exe') `
        -ArgumentList $riotArguments `
        -WorkingDirectory $riotDirectory -LogRoot $logRoot |
        ForEach-Object { $_ | Add-Member -NotePropertyName Order -NotePropertyValue 1 -PassThru }
    $handles += $riotHandle

    $mesHandle = Start-L2Process -Name 'fake-mes-ingest' `
        -FilePath (Join-Path $mesDirectory 'ControlServer.FakeMesIngest.exe') `
        -ArgumentList @(
            "--FakeMesIngest:port=$FakeMesIngestPort",
            "--FakeMesIngest:instanceId=l2-mes") `
        -WorkingDirectory $mesDirectory -LogRoot $logRoot |
        ForEach-Object { $_ | Add-Member -NotePropertyName Order -NotePropertyValue 2 -PassThru }
    $handles += $mesHandle

    $riot = New-L2Double -Name 'fake-riot' -BaseUrl "http://127.0.0.1:$FakeRiotPort"
    $mes = New-L2Double -Name 'fake-mes-ingest' -BaseUrl "http://127.0.0.1:$FakeMesIngestPort"

    $null = Wait-L2Condition -Description 'fake RIoT is live' -Journal $journal -Criterion 'fake-riot-live' `
        -Component $riotHandle `
        -Probe { $riot.Health().body.status } -Until { param($v) $v -eq 'live' }
    $null = Wait-L2Condition -Description 'fake MesIngest is live' -Journal $journal -Criterion 'fake-mes-live' `
        -Component $mesHandle `
        -Probe { $mes.Health().body.status } -Until { param($v) $v -eq 'live' }

    # 1b. The slots simulator, when the scenario asked for the real onboard. It has to be listening
    #     on Modbus before the onboard starts, or the onboard's first snapshot is all UNKNOWN and
    #     the session takes an extra reconnect to recover from a state that never had to happen.
    $simulator = $null
    if ($realOnboard) {
        $simulatorDirectory = New-L2PeerStage -Publish $simulatorPublish -StageRoot $stageRoot `
            -SettingsFileName 'simulator.settings.json' -Configure {
                param($settings)
                $settings.instanceId = 'l2-simulator'
                $settings.agvId = $agvId
                $settings.modbus.listenAddress = '127.0.0.1'
                $settings.modbus.port = $SimulatorModbusPort
                $settings.automation.listenAddress = '127.0.0.1'
                $settings.automation.port = $SimulatorHttpPort
            }
        $simulatorHandle = Start-L2Process -Name 'slots-simulator' -Gui `
            -FilePath (Join-Path $simulatorDirectory 'SQCD_8005AGV_Simulator.exe') `
            -WorkingDirectory $simulatorDirectory -LogRoot $logRoot |
            ForEach-Object { $_ | Add-Member -NotePropertyName Order -NotePropertyValue 3 -PassThru }
        $handles += $simulatorHandle

        $simulator = New-L2Double -Name 'slots-simulator' -BaseUrl "http://127.0.0.1:$SimulatorHttpPort" `
            -Prefix 'api/v1' -RequireExpectedRevision
        $null = Wait-L2Condition -Description 'the slots simulator is serving Modbus' -Journal $journal `
            -Criterion 'simulator-ready' -TimeoutSeconds 120 -Component $simulatorHandle `
            -Probe { $h = $simulator.Health(); "$($h.status)/$($h.modbus.isRunning)" } `
            -Until { param($v) $v -eq 'READY/True' }
    }

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
        # JourneyRuntimeOptions requires this secret unconditionally -- unlike the MesIngest one it
        # has no loopback exemption -- so without these two lines the server refuses to start with
        # "RIoT:callApiKeyEnvironmentVariable must name a populated external environment variable."
        # It used to start anyway on a developer machine, by inheriting the real
        # CONTROL_SERVER_RIOT_CALL_API_KEY from the ambient environment. That was two problems: the
        # rig was not hermetic (CI, running as NetworkService, has no such variable), and every L2
        # run handed the production RIoT key to a server that only ever talks to a double. Point the
        # setting at a dedicated dummy instead, the way Invoke-AuthorizedAbsentObservationShadow.ps1
        # already does. ControlServer.FakeRiot does not check the value.
        'RIoT__callApiKeyEnvironmentVariable'             = 'CONTROL_SERVER_L2_DUMMY_RIOT_CALL_API_KEY'
        'CONTROL_SERVER_L2_DUMMY_RIOT_CALL_API_KEY'       = 'l2-fake-riot-not-a-production-secret'
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

    # The roster, only when there is more than one vehicle. Left absent for a single-vehicle run
    # so that those scenarios keep facing the empty-Fleet path -- the one an upgraded single-vehicle
    # deployment actually runs -- rather than a roster of one that merely behaves the same.
    #
    # The policy slice is stated per vehicle because an unstated one admits nothing: an empty
    # AllowedTaskTypes means the vehicle may take no task at all, and an empty Zones means it
    # serves none. Both are read from the same single-vehicle fields the server would otherwise
    # derive its one entry from, so the fleet runs the configuration the single vehicle ran.
    if ($fleet.Count -gt 1) {
        for ($index = 0; $index -lt $fleet.Count; $index++) {
            $serverEnvironment["JourneyRuntime__Fleet__${index}__AgvId"] = $fleet[$index].AgvId
            $serverEnvironment["JourneyRuntime__Fleet__${index}__VehicleKey"] = $fleet[$index].VehicleKey
            $serverEnvironment["JourneyRuntime__Fleet__${index}__AgvLifecycleGeneration"] = '1'
            $serverEnvironment["JourneyRuntime__Fleet__${index}__AllowedTaskTypes__0"] = 'WIRE_TO_GATE'
            $serverEnvironment["JourneyRuntime__Fleet__${index}__Zones__0"] = 'MAP-25-WIRE_TO_GATE'
        }
        $journal.Note("Fleet of $($fleet.Count): " +
            (($fleet | ForEach-Object { "$($_.AgvId)/$($_.VehicleKey)" }) -join ', '))
    }

    # FP-C13: the two REQ-0302 values, approved. A commissioned server has them, so every scenario
    # faces one. `CatalogApproved = $false` in a setup file takes them away, which is the negative
    # evidence specification 8.6 requires -- and there is no switch that turns the check off, only
    # the absence of an approval.
    if (-not $setup.ContainsKey('CatalogApproved') -or [bool]$setup.CatalogApproved) {
        $serverEnvironment['MapStationCatalog__ApprovedSyncPeriod'] = '00:00:30'
        $serverEnvironment['MapStationCatalog__ApprovedMaxUnconfirmed'] = '00:05:00'
    } else {
        $journal.Note('Map/Station catalog parameters deliberately unapproved for this scenario.')
    }
    if ($recoveryResume) {
        $serverEnvironment['Recovery__AuthenticationProofEnvironmentVariable'] = $recoveryProofVariable
        $serverEnvironment[$recoveryProofVariable] = $recoveryProof
    }
    if ($slotConfigurationActivation) {
        $serverEnvironment['SlotConfigurationActivation__enabled'] = 'true'
        $serverEnvironment['SlotConfigurationActivation__credentialEnvironmentVariable'] = $governanceCredentialVariable
        $serverEnvironment[$governanceCredentialVariable] = $governanceCredential
    }
    if ($realOnboard) {
        # Only the real onboard polls this projection; the synthetic peer decides for itself what
        # the safety summary says. Leaving it off for the synthetic rig keeps those scenarios
        # running exactly the server they were made green against.
        $serverEnvironment['OnboardSafetyProjection__enabled'] = 'true'
    }

    # The route-graph engine, off unless a scenario asks for it. Off is the shape every
    # existing scenario was written against, and the criterion passes when it is off, so a
    # scenario that says nothing sees dispatch exactly as it was before the engine existed.
    if ($setup.ContainsKey('RouteGraph') -and $setup.RouteGraph.ContainsKey('Enabled') -and
        [bool]$setup.RouteGraph.Enabled) {
        $serverEnvironment['RouteGraph__Enabled'] = 'true'
        $serverEnvironment['RouteGraph__MapId'] = [string]$mapId
        foreach ($key in ($setup.RouteGraph.Keys | Sort-Object)) {
            if ($key -in @('Enabled', 'MapId')) { continue }
            $serverEnvironment["RouteGraph__$key"] = [string]$setup.RouteGraph[$key]
        }
        $journal.Note("Route graph engine enabled for map $mapId.")
    }

    # The RIoT command options, only when a scenario asks. REQ-0248 makes the emergency-retry backoff
    # a site parameter, so this sets a value a site could set rather than switching anything off. A
    # scenario that has to tell "the stop was asked for again inside the backoff" from "the backoff
    # ran out and it retried" at this rig's one-second poll needs a backoff longer than the phases
    # it watches; the retry itself is covered by the unit suite.
    if ($setup.ContainsKey('RiotCommands')) {
        foreach ($key in ($setup.RiotCommands.Keys | Sort-Object)) {
            $serverEnvironment["RiotCommands__$key"] = [string]$setup.RiotCommands[$key]
        }
        $journal.Note('RIoT command options for this scenario: ' +
            (($setup.RiotCommands.Keys | Sort-Object | ForEach-Object { "$_=$($setup.RiotCommands[$_])" }) -join ', '))
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
    $serverHandle = Start-L2Process -Name 'control-server' `
        -FilePath (Join-Path $hostDirectory 'ControlServer.Host.exe') `
        -WorkingDirectory $hostDirectory -Environment $serverEnvironment -LogRoot $logRoot |
        ForEach-Object { $_ | Add-Member -NotePropertyName Order -NotePropertyValue 4 -PassThru }
    $handles += $serverHandle

    # /health/live, not /health/ready: readiness means a peer has completed the recovery handshake,
    # and the peer cannot connect until the server is listening. Waiting on readiness here would
    # deadlock the startup order against itself.
    $null = Wait-L2Condition -Description 'ControlServer is listening' -Journal $journal -Criterion 'control-server-live' `
        -TimeoutSeconds 120 -Component $serverHandle `
        -Probe { (Invoke-RestMethod -Uri "http://127.0.0.1:$HealthPort/health/live" -TimeoutSec 5).status } `
        -Until { param($v) $v -eq 'live' }

    $version = Invoke-RestMethod -Uri "http://127.0.0.1:$HealthPort/version" -TimeoutSec 5
    $protocolReleaseIdentity = [ordered]@{
        repository         = '8005-agv-protocol'
        releaseVersion     = $version.protocolReleaseVersion
        tag                = $version.protocolTag
        commit             = $version.protocolCommit
        protocolVersion    = $version.protocolVersion
        profileId          = $version.profileId
        manifestSha256     = $version.manifestSha256
        schemaBundleSha256 = $version.schemaBundleSha256
        vectorsSha256      = $version.vectorsSha256
        approvalStatus     = $version.approvalStatus
    }
    $journal.Note("Protocol release identity: $($version.protocolTag) " +
        "(protocolVersion $($version.protocolVersion), $($version.approvalStatus)).")

    # 4b. The skew proxy, when a scenario asked for one. After the server (it forwards to it) and
    #     before the onboard (which must find it listening on its first poll).
    $skewProxy = $null
    if ($null -ne $clockSkewMs) {
        $skewProxyHandle = Start-L2Process -Name 'clock-skew-proxy' `
            -FilePath (Join-Path $skewProxyDirectory 'ControlServer.ClockSkewProxy.exe') `
            -ArgumentList @(
                "--ClockSkewProxy:port=$ClockSkewProxyPort",
                "--ClockSkewProxy:instanceId=l2-skew-proxy",
                "--ClockSkewProxy:target=http://127.0.0.1:$HealthPort",
                "--ClockSkewProxy:Seed:skewMs=$clockSkewMs") `
            -WorkingDirectory $skewProxyDirectory -LogRoot $logRoot |
            ForEach-Object { $_ | Add-Member -NotePropertyName Order -NotePropertyValue 5 -PassThru }
        $handles += $skewProxyHandle

        $skewProxy = New-L2Double -Name 'clock-skew-proxy' -BaseUrl "http://127.0.0.1:$ClockSkewProxyPort"
        $null = Wait-L2Condition -Description 'the clock skew proxy is live' -Journal $journal `
            -Criterion 'skew-proxy-live' -TimeoutSeconds 60 -Component $skewProxyHandle `
            -Probe { $skewProxy.Health().body.status } -Until { param($v) $v -eq 'live' }
        $journal.Note("Clock skew proxy forwarding vehicle-safety with observedAt +${clockSkewMs}ms.")
    }

    # 5. The onboard last, either way: it connects out to the server, so the server has to be
    #    listening first.
    # Empty on a real-onboard rig, which has no synthetic peers; the scenario Context carries it
    # either way, so it must exist before the branch.
    $onboardDoubles = @()
    $onboard = $null
    if ($realOnboard) {
        # Everything here is a runtime setting the shipped appsettings.json already carries. The
        # onboard reads that one file next to its executable and supports no environment or
        # command-line override, so the staged copy is the only place these can be set -- and the
        # staged copy is why the read-only repository stays untouched.
        $onboardStageDirectory = New-L2PeerStage -Publish $onboardPublish -StageRoot $stageRoot `
            -SettingsFileName 'appsettings.json' -Configure {
                param($settings)
                $settings.agvId = $agvId
                $settings.onboardInstanceId = 'OBU-L2-001'
                $settings.wireToGate.enabled = $true
                $settings.wireToGate.host = '127.0.0.1'
                $settings.wireToGate.port = $ControlPort
                $settings.wireToGate.onboardInstanceId = '9f2c7f10-3a4d-4a2e-9a26-6f0d5a1c8b77'
                # Validate() insists this is a real 40-hex commit, and it is the identity the
                # server records for the peer, so it must be the commit actually published.
                $settings.wireToGate.onboardBuildCommit = $onboardPublish.Commit
                $settings.wireToGate.journalPath = (Join-Path $stageRoot 'onboard-journal.db')
                # Ships false. Configuration.Validate() then also insists the proof variable is
                # populated, which the process environment below does.
                $settings.wireToGate.recoveryResumeEnabled = $recoveryResume
                # WireToGate readiness runs through this projection: App.xaml.cs awaits the first
                # refresh before the handshake snapshot, and vehicleStoppedProvider reads it on
                # every safety summary afterwards.
                $settings.vehicleSafety.enabled = $true
                # Straight to the server unless the scenario asked for skew, in which case the
                # proxy sits in between and this is the only line that says so.
                $settings.vehicleSafety.endpoint = if ($null -ne $clockSkewMs) {
                    "http://127.0.0.1:$ClockSkewProxyPort/api/onboard/v1/vehicle-safety"
                } else {
                    "http://127.0.0.1:$HealthPort/api/onboard/v1/vehicle-safety"
                }
                $settings.vehicleSafety.expectedVehicleKey = $vehicleKey
                $settings.ioModule.host = '127.0.0.1'
                $settings.ioModule.port = $SimulatorModbusPort
                # Into the evidence rather than the stage root: the onboard's own log is the
                # richest account of a failed run, and the stage root is deleted on a pass.
                $settings.logging.directory = (Join-Path $logRoot 'onboard-app')
            }
        $onboardEnvironment = @{
            'CONTROL_SERVER_ONBOARD_CREDENTIAL' = $credential
            'CONTROL_SERVER_OPERATOR_ID'        = 'L2-OPERATOR'
        }
        if ($recoveryResume) { $onboardEnvironment[$recoveryProofVariable] = $recoveryProof }
        $onboardHandle = Start-L2Process -Name 'onboard-hmi' -Gui `
            -FilePath (Join-Path $onboardStageDirectory 'SQCD.Agv.Wpf.exe') `
            -WorkingDirectory $onboardStageDirectory `
            -Environment $onboardEnvironment `
            -LogRoot $logRoot |
            ForEach-Object { $_ | Add-Member -NotePropertyName Order -NotePropertyValue 6 -PassThru }
        $handles += $onboardHandle

        $onboard = New-L2OnboardDriver -ProcessId $onboardHandle.Process.Id
        $journal.Note("Onboard window: $($onboard.Attach(120))")

        # The simulator counts Modbus clients, so "the onboard is talking to IO" is observed from
        # the simulator rather than taken on trust from the onboard's own log.
        $null = Wait-L2Condition -Description 'the onboard connected to the simulator over Modbus' `
            -Journal $journal -Criterion 'onboard-modbus' -TimeoutSeconds 60 -Component $onboardHandle `
            -Probe { [int]$simulator.Health().modbus.clientCount } -Until { param($v) $v -ge 1 }
    } else {
        # One synthetic peer per configured vehicle. A scenario that says nothing gets exactly one,
        # on the same port and instance id every existing scenario was written against, so the
        # evidence layout for those is unchanged. A multi-vehicle scenario lists OnboardPeers and
        # each peer gets its own process -- separate processes are what makes 'sessions do not
        # cross' structural rather than something the fake has to remember to enforce.
        # The outer @() is load bearing: an if-expression's output goes through the pipeline, so
        # a single-element array coming out of either branch would be unrolled to the hashtable
        # itself. Indexing that with [0] then looks up the key 0 and yields null -- which is how
        # every single-peer scenario broke while the three-peer one passed.
        # A scenario that drives a fleet gets one peer per vehicle without listing them twice:
        # a roster entry the server drives and a peer it drives it through are the same vehicle,
        # and two lists of the same names is one way for them to disagree.
        $peerSpecs = @(
            if ($setup.ContainsKey('OnboardPeers')) { $setup.OnboardPeers }
            elseif ($fleet.Count -gt 1) { $fleet | ForEach-Object { @{ AgvId = $_.AgvId } } }
            else { @{ AgvId = $agvId } })
        for ($peerIndex = 0; $peerIndex -lt $peerSpecs.Count; $peerIndex++) {
            $spec = $peerSpecs[$peerIndex]
            $peerAgvId = if ($spec.ContainsKey('AgvId')) { $spec.AgvId } else { $agvId }
            $peerPort = $FakeOnboardPort + $peerIndex
            $peerName = if ($peerIndex -eq 0) { 'fake-onboard' } else { "fake-onboard-$peerIndex" }
            $peerInstance = if ($peerIndex -eq 0) { 'l2-onboard' } else { "l2-onboard-$peerIndex" }

            # OnboardSeed lands on the safety summary the handshake's SafetyStateSnapshot carries,
            # which is the only way to establish a session that already says the vehicle is moving.
            $onboardArguments = @(
                "--FakeOnboard:port=$peerPort",
                "--FakeOnboard:instanceId=$peerInstance",
                "--FakeOnboard:Peer:port=$ControlPort",
                "--FakeOnboard:Peer:agvId=$peerAgvId")
            $peerSeed = if ($spec.ContainsKey('Seed')) { $spec.Seed } elseif ($setup.ContainsKey('OnboardSeed')) { $setup.OnboardSeed } else { $null }
            if ($null -ne $peerSeed) {
                foreach ($key in ($peerSeed.Keys | Sort-Object)) {
                    $onboardArguments += "--FakeOnboard:Seed:$key=$($peerSeed[$key])"
                }
                $journal.Note("Onboard seed for ${peerName}: " +
                    (($peerSeed.GetEnumerator() | Sort-Object Key |
                        ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', '))
            }
            $onboardHandle = Start-L2Process -Name $peerName `
                -FilePath (Join-Path $onboardDirectory 'ControlServer.FakeOnboard.exe') `
                -ArgumentList $onboardArguments `
                -WorkingDirectory $onboardDirectory `
                -Environment @{ 'CONTROL_SERVER_ONBOARD_CREDENTIAL' = $credential } `
                -LogRoot $logRoot |
                ForEach-Object { $_ | Add-Member -NotePropertyName Order -NotePropertyValue 6 -PassThru }
            $handles += $onboardHandle

            $peerDouble = New-L2Double -Name $peerName -BaseUrl "http://127.0.0.1:$peerPort"
            $onboardDoubles += [pscustomobject]@{ Name = $peerName; Port = $peerPort; AgvId = $peerAgvId; Double = $peerDouble }
            # Every peer waits for READY by default. It did not always: before ticket 09 the
            # server's accept loop awaited one connection's handler before accepting the next, so
            # only the first peer ever reached READY and a multi-vehicle scenario had to say
            # WaitForReady = $false on the rest. Both halves of that are gone -- the accept loop
            # is concurrent and OnboardPeer holds one connection per AgvId -- and the switch is
            # kept only for a scenario that deliberately wants an unfinished session.
            $waitForReady = if ($spec.ContainsKey('WaitForReady')) { [bool]$spec.WaitForReady } else { $true }
            if ($waitForReady) {
                $null = Wait-L2Condition -Description "the synthetic peer $peerAgvId reached READY" -Journal $journal -Criterion 'onboard-readiness' `
                    -TimeoutSeconds 60 -Component $onboardHandle `
                    -Probe { $peerDouble.Snapshot().body.readiness } -Until { param($v) $v -eq 'READY' }
            } else {
                $journal.Note("Peer $peerAgvId started; not waiting for READY (WaitForReady = false).")
            }
        }
        # Scenario bodies address the first peer as $onboard, which is the only one a
        # single-vehicle scenario has.
        $onboard = $onboardDoubles[0].Double
    }

    # Now readiness is meaningful: the peer finished the handshake and the server granted it.
    $null = Wait-L2Condition -Description 'ControlServer reports the vehicle ready' -Journal $journal `
        -Criterion 'control-server-ready' -TimeoutSeconds 60 -Component $serverHandle `
        -Probe { (Invoke-RestMethod -Uri "http://127.0.0.1:$HealthPort/health/ready" -TimeoutSec 5).status } `
        -Until { param($v) $v -eq 'ready' }

    # 6. The dashboard, when the scenario asked for it. Last, and after readiness: it fetches from the
    #    server on every render, so starting it any earlier only gives it a server that is not ready
    #    to say anything yet.
    $dashboardUrl = $null
    if ($dashboard) {
        $dashboardUrl = "http://127.0.0.1:$DashboardPort"
        $dashboardHandle = Start-L2Process -Name 'dashboard' `
            -FilePath (Join-Path $dashboardDirectory 'ControlServer.Dashboard.exe') `
            -ArgumentList @(
                "--Dashboard:url=$dashboardUrl",
                "--Dashboard:controlServerBaseUrl=http://127.0.0.1:$HealthPort") `
            -WorkingDirectory $dashboardDirectory -LogRoot $logRoot |
            ForEach-Object { $_ | Add-Member -NotePropertyName Order -NotePropertyValue 7 -PassThru }
        $handles += $dashboardHandle
        $null = Wait-L2Condition -Description 'the dashboard is serving its page' -Journal $journal `
            -Criterion 'dashboard-live' -TimeoutSeconds 60 -Component $dashboardHandle `
            -Probe { (Invoke-WebRequest -Uri $dashboardUrl -TimeoutSec 5).StatusCode } `
            -Until { param($v) $v -eq 200 }
    }

    $connection = Open-L2Database -HostDirectory $hostDirectory -DatabasePath $databasePath

    $context = [pscustomobject]@{
        Journal             = $journal
        Assertions          = $assertions
        Riot                = $riot
        MesIngest           = $mes
        # Two different things under one name, and the setup file says which: an L2Double over the
        # synthetic peer's control plane, or the UI Automation driver over the shipped WPF. A
        # scenario is written for one rig, so there is nothing to branch on at this level.
        Onboard             = $onboard
        # Every synthetic peer, first one first. A single-vehicle scenario never touches this;
        # a multi-vehicle one needs to address the peer that did not get the order as well as
        # the one that did.
        OnboardPeers        = $onboardDoubles
        Simulator           = $simulator
        SkewProxy           = $skewProxy
        Connection          = $connection
        RunId               = $runId
        # The shipped onboard's own SQLite journal, real-onboard rig only. What the vehicle adopted is
        # recorded there and nowhere the server can see, so a scenario that has to show the onboard
        # applied the projection the server committed reads it -- read-only, alongside the process
        # that owns it.
        OnboardJournalPath  = if ($realOnboard) { Join-Path $stageRoot 'onboard-journal.db' } else { $null }
        AgvId               = $agvId
        VehicleKey          = $vehicleKey
        MapId               = $mapId
        MapIdentity         = $mapIdentity
        GateStationRiotId   = $gateStationRiotId
        PickupStationRiotId = $pickupStationRiotId
        HealthPort          = $HealthPort
        SnapshotRoot        = $snapshotRoot
        # Null unless the setup file turned the activation entry point on.
        GovernanceCredential = if ($slotConfigurationActivation) { $governanceCredential } else { $null }
        # Null unless the setup file asked for the dashboard.
        DashboardUrl        = $dashboardUrl
        # ControlServer.FieldOps, the same executable a site's W1 window runs, against the SQLite file the
        # server is using. Returns the one JSON object the tool prints; a non-zero exit is a thrown error
        # carrying its stderr, because a governance act that silently did nothing would leave the rest of
        # the scenario proving something else.
        InvokeFieldOps      = {
            param([Parameter(Mandatory)][string[]]$Arguments)
            $all = @($Arguments[0], '--database', $databasePath) + @($Arguments | Select-Object -Skip 1)
            $journal.Note("FieldOps: $($all -join ' ')")
            $lines = @(& (Join-Path $fieldOpsDirectory 'ControlServer.FieldOps.exe') @all 2>&1)
            $exit = $LASTEXITCODE
            $text = ($lines | ForEach-Object { [string]$_ }) -join "`n"
            if ($exit -ne 0) { throw "ControlServer.FieldOps $($Arguments[0]) exited with $exit`: $text" }
            $json = $lines | Where-Object { $_ -is [string] -and $_.TrimStart().StartsWith('{') } | Select-Object -Last 1
            if (-not $json) { throw "ControlServer.FieldOps $($Arguments[0]) printed no JSON: $text" }
            return ($json | ConvertFrom-Json)
        }
        # Order is the start position, and Stop-L2Process tears down in reverse: fake RIoT 1, fake
        # MesIngest 2, simulator 3, ControlServer 4, clock skew proxy 5, onboard 6 (synthetic or
        # real -- they are mutually exclusive, so they share the position). Two components on the
        # same number would make that order undefined.
        # Powering a component down is part of several scenarios -- the vehicle is normally switched
        # off while a blocked load is being dealt with -- so a scenario can stop one by name. Teardown
        # stops whatever is left, and stopping something twice is not an error.
        StopComponent       = {
            param([Parameter(Mandatory)][string]$Name)
            $matched = @($handles | Where-Object { $_.Name -eq $Name })
            if ($matched.Count -eq 0) { throw "No such component to stop: $Name" }
            $journal.Note("Stopping component '$Name'.")
            Stop-L2Process -Handles $matched
        }
    }

    $journal.Note("Environment is up; entering scenario '$Scenario'.")
    & $scenarioPath -Context $context
    $outcome = if ($assertions.AllPassed()) { 'PASS' } else { 'FAIL' }
} catch {
    $outcome = 'FAIL'
    $failureReason = $_.Exception.Message
    # The stack trace is what turns "a method on a null-valued expression" into a line number.
    # Without it a failure inside the orchestrator costs a bisect to locate.
    $journal.Note("Run failed: $failureReason")
    $journal.Note("Failure at: " + ($_.ScriptStackTrace -replace "
?
", " | "))
    Write-Warning $failureReason
} finally {
    # Snapshots before teardown, so a failed run keeps the state that explains it. Each is written
    # independently: a double that already died must not stop the others being captured.
    $snapshotSources = @(
        @{ Name = 'fake-riot'; Url = "http://127.0.0.1:$FakeRiotPort/control/v1/snapshot" },
        @{ Name = 'fake-mes-ingest'; Url = "http://127.0.0.1:$FakeMesIngestPort/control/v1/snapshot" })
    $snapshotSources += if ($realOnboard) {
        # The simulator's snapshot is the physical record: door, cargo, DO and DI per slot. On a
        # failed load it says whether the goods were ever there.
        @{ Name = 'slots-simulator'; Url = "http://127.0.0.1:$SimulatorHttpPort/api/v1/snapshot" }
    } else {
        # Every peer, not just the first: on a multi-vehicle run the interesting evidence is often
        # what the peer that did *not* get the order was doing.
        $onboardDoubles | ForEach-Object {
            @{ Name = $_.Name; Url = "http://127.0.0.1:$($_.Port)/control/v1/snapshot" }
        }
    }
    if ($null -ne $clockSkewMs) {
        $snapshotSources += @{
            Name = 'clock-skew-proxy'
            Url  = "http://127.0.0.1:$ClockSkewProxyPort/control/v1/snapshot"
        }
    }
    foreach ($double in $snapshotSources) {
        try {
            $body = Invoke-RestMethod -Uri $double.Url -TimeoutSec 5
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
                             'StationOperations', 'SessionRecoveries', 'OperationResults',
                             'ExceptionRecoverySessions', 'RecoveryWorkflows',
                             'RouteGraphSnapshots', 'MapStationCatalogStates',
                             'FrozenDemandStations', 'CreateGateAudit',
                             'SlotConfigurationActivations', 'ActiveSlotConfigurations',
                             'OnboardAlarmSnapshots', 'BusinessAuditRecords')) {
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

    $identity = @{
        controlServerCommit = (& git -C $Repository rev-parse HEAD 2>$null)
        agvId               = $agvId
        vehicleKey          = $vehicleKey
        stageRoot           = $stageRoot
        rig                 = if ($realOnboard) { 'RealOnboard' } else { 'SyntheticOnboard' }
        batchId             = $BatchId
    }
    # Null only when the run died before the server answered /version. Written as null rather than
    # omitted: "this run never got far enough to read it" and "this evidence predates the field"
    # are different, and a reader of a failed run's evidence needs to be able to tell them apart.
    $identity['protocolReleaseIdentity'] = $protocolReleaseIdentity
    if ($fleet.Count -gt 1) {
        $identity['fleet'] = $fleet | ForEach-Object { "$($_.AgvId)/$($_.VehicleKey)" }
    }
    if ($null -ne $clockSkewMs) { $identity['clockSkewMs'] = $clockSkewMs }
    if ($onboardPublish) { $identity['onboardHmiCommit'] = $onboardPublish.Commit }
    if ($simulatorPublish) { $identity['slotsSimulatorCommit'] = $simulatorPublish.Commit }
    Write-L2Evidence -EvidenceRoot $EvidenceRoot -Scenario $Scenario -RunId $runId `
        -Assertions $assertions -Outcome $outcome -FailureReason $failureReason `
        -Rig $identity.rig -Identity $identity

    # The stage root is left behind on failure: its controlserver.db is usually the only place the
    # cause is written down.
    if ($outcome -eq 'PASS') {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Warning "Stage root kept for diagnosis: $stageRoot"
    }

    # Last, after the peers are stopped. Releasing earlier would hand the desktop to another
    # repository while this run's WPF windows were still closing.
    Exit-DesktopLock -Handle $desktopLock
}

Write-Host "L2 $Scenario -> $outcome (evidence: $EvidenceRoot)"
if ($outcome -ne 'PASS') { exit 1 }
