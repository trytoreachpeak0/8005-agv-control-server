#Requires -Version 7

<#
.SYNOPSIS
    G3 for the slices that only a live journey can reach: FP-IS-01 today, FP-IS-02/03/07 as their
    scenarios land.

.DESCRIPTION
    The other three G3 runners keep the journey runtime off and point RIoT and MesIngest at a dead
    port, deliberately: they must not be able to accept a demand or place an order. Every accepted
    business path needs an accepted demand, so FP-IS-01, 02, 03 and most of 07 had no G3 surface in
    any of them, and the user ruled on 2026-09-09 to record that gap. On 2026-09-13 the user reversed
    it for those four slices and ruled what a surface for them may be: the journey run for real,
    against loopback fakes of RIoT and MesIngest, counted as a formal slice pass at its own assurance
    level, JOURNEY_SIMULATED_COUNTERPARTS (scripts/g3-slice-evidence.ps1).

    What runs is the L2 real-onboard rig, not a new harness: the bound ControlServer with its journey
    runtime on, the shipped onboard WPF driven through UI Automation, the real slots simulator over
    Modbus, ControlServer.FakeRiot and ControlServer.FakeMesIngest. That rig already walks demand,
    pickup, load and unload. What this runner adds is what makes a run of it a gate:

      - the four repositories are exact clones at the shared commit binding, the same binding the
        other three G3 runners read out of run-staged-g3.ps1's param block, so every G3 result names
        one identity;
      - the rig itself -- Invoke-L2Scenario.ps1 and the scenario scripts -- runs from the ControlServer
        clone at the bound commit, so the harness is bound too and not whatever is in this worktree;
      - every scenario's recorded identity is checked against the binding, and a scenario that
        reports another build, another protocol or the synthetic rig fails the run;
      - the scenario judgments are attributed to named G3 assertions, and those to slices, through
        the claim table, which throws if the two drift apart.

    It needs an interactive desktop and takes the machine-wide desktop lock (inside the rig), like
    run-staged-g3.ps1. Two WPF windows appear per scenario.

.PARAMETER SelfCheckControlServerCommit
    For developing a scenario before the shared binding has moved: clone this ControlServer commit
    instead of the bound one. The run records controlServerCommitSource = SELF_CHECK_OVERRIDE and must
    not be committed as gate evidence -- ticket 23's self-check runs go to a temporary directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$StageRoot,
    [Parameter(Mandatory)]
    [string]$EvidenceRoot,
    # Selects what this run CERTIFIES, not what it runs. See run-staged-g3.ps1's parameter of the same
    # name and scripts/g3-slice-evidence.ps1.
    [ValidatePattern('^FP-IS-(0[0-9]|1[0-5])$')][string]$Slice,
    [string]$ControlServerRepository = (Split-Path -Parent $PSScriptRoot),
    [string]$OnboardRepository = 'https://github.com/trytoreachpeak0/8005-agv-onboard-hmi.git',
    [string]$SimulatorRepository = 'https://github.com/trytoreachpeak0/slots-simulator.git',
    [string]$ProtocolRepository = 'https://github.com/trytoreachpeak0/8005-agv-protocol.git',
    # The same ref run-staged-g3.ps1 asserts the onboard commit is the tip of.
    [string]$OnboardRemoteRef = 'origin/w2g/b3-on-v2',
    [string]$SharedRunnerSource = (Join-Path $PSScriptRoot 'run-staged-g3.ps1'),
    [string]$CommitBindingFunctionSource = (Join-Path $PSScriptRoot 'run-staged-g3-restart.ps1'),
    [ValidatePattern('^[0-9a-f]{40}$')][string]$SelfCheckControlServerCommit
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# R-4: no MSBuild node outlives the command that started it (see run-staged-g3.ps1).
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'

$runStartedAt = [DateTimeOffset]::UtcNow
$runId = $runStartedAt.ToString('yyyyMMddTHHmmssfffZ')

# The scenarios this runner drives, in order, and the G3 assertion each of their judgments is. The
# names are the G3 vocabulary and what the claim table attributes; the L2 ids are what the scenario
# scripts write. A judgment the scenario did not report reads as a failure, never as a pass.
$scenarioAssertions = [ordered]@{
    'g3-journey-demand-to-pickup' = [ordered]@{
        'G3-01-01' = 'exactlyOneAcceptedDemandSnapshot'
        'G3-01-02' = 'exactlyOneToPickupIntent'
        'G3-01-03' = 'exactlyOneRiotOrder'
        'G3-01-04' = 'planPublishedAndAcknowledgedBeforePickupArrival'
        'G3-01-05' = 'noSlotOperationBeforePickupArrival'
        'G3-01-06' = 'trustedPickupArrivalAdopted'
        'G3-01-07' = 'demandAcceptanceSnapshotSequenceMatchesVector'
        'G3-01-08' = 'onboardAppliedTheCommittedProjection'
        'G3-01-09' = 'finalStateOneDemandOneOrderAtPickupNoSlotOperation'
        'G3-01-10' = 'onboardNeverDiscoversSelectsOrBindsDemand'
    }
    'g3-pickup-load-and-correction' = [ordered]@{
        'G3-02-01' = 'sublotBoundToOperationSession'
        'G3-02-02' = 'slotSetAuthorizedOnce'
        'G3-02-03' = 'loadOnlyAuthorizedSlotsEachUnlockedOnce'
        'G3-02-04' = 'loadClosedOverRealModbus'
        'G3-02-05' = 'pickupSublotLoadSequenceMatchesVector'
        'G3-02-06' = 'loadOutcomeCommittedOnce'
        'G3-02-07' = 'onboardOffersLoadCorrectionAfterCompletedLoad'
        'G3-02-08' = 'loadCorrectionSequenceMatchesVector'
        'G3-02-09' = 'correctionAuthorizedAgainstCommittedSet'
        'G3-02-10' = 'neverCorrectWithoutAuthorization'
        'G3-02-11' = 'correctedSlotOutcomeReported'
        'G3-02-12' = 'correctionCreatesNoDuplicateCommit'
        'G3-02-13' = 'correctionOnlyBeforeDepartureAndHoldsTheVehicle'
    }
    'g3-load-cancellation' = [ordered]@{
        'G3-02-21' = 'onboardOffersLoadCancellationDuringLoad'
        'G3-02-22' = 'cancellationAuthorizedExplicitly'
        'G3-02-23' = 'loadCancellationSequenceMatchesVector'
        'G3-02-24' = 'cancellationProvesEmptyWithoutUnlocking'
        'G3-02-25' = 'allSlotsProvenEmpty'
        'G3-02-26' = 'cancellationReconciledToEmptyFinalState'
        'G3-02-27' = 'finalStateSurvivesLateLoadResult'
    }
    'g3-predeparture-check-expires' = [ordered]@{
        'G3-03-01' = 'predepartureExpirySequenceMatchesVector'
        'G3-03-02' = 'neverDepartOnExpiredCheck'
        'G3-03-03' = 'checkExpiresOnSafetyStateChange'
        'G3-03-04' = 'safetyStateChangeReportedPromptly'
        'G3-03-05' = 'checkAskedAgainAfterExpiry'
        'G3-03-06' = 'expiredCheckRefusalKeepsTheSession'
        'G3-03-07' = 'expiryLeavesNoDuplicateCommitOrUnprovenState'
    }
    'g3-operation-result-unknown-reconcile' = [ordered]@{
        'G3-03-08' = 'unknownResultReconcileSequenceMatchesVector'
        'G3-03-09' = 'unknownReportedAsUnknownAndReplayedFromJournal'
        'G3-03-10' = 'unknownNeverTreatedAsSuccess'
        'G3-03-11' = 'reconciledFromReportedJournalBeforeReadiness'
        'G3-03-12' = 'replayTouchesNoSlot'
    }
    'g3-exception-resume' = [ordered]@{
        'G3-07-01' = 'recoveryUnknownResultSequenceMatchesVector'
        'G3-07-02' = 'recoveryUnknownReportedAsUnknownAndReplayedFromJournal'
        'G3-07-03' = 'recoveryUnknownNeverTreatedAsSuccess'
        'G3-07-04' = 'recoveryUnknownReconciledBeforeReadiness'
        'G3-07-05' = 'recoveryUnknownReplayTouchesNoSlot'
        'G3-07-06' = 'exceptionResumeSequenceMatchesVector'
        'G3-07-07' = 'recoverySessionOpenedForVerifiedAdministrator'
        'G3-07-08' = 'resumeOnlyTheAuthorizedScope'
        'G3-07-09' = 'resumedOutcomeReportedAndSupersedesUnknown'
        'G3-07-10' = 'resumeReconciledWithOneCommit'
        'G3-07-11' = 'resumeFinalPhysicalStateProven'
    }
    'g3-exception-compensate' = [ordered]@{
        'G3-07-21' = 'exceptionCompensateSequenceMatchesVector'
        'G3-07-22' = 'compensationAuthorizedAgainstRecoverySession'
        'G3-07-23' = 'compensationExecutedOnceWithoutUnlocking'
        'G3-07-24' = 'compensatedSlotStateReported'
        'G3-07-25' = 'compensationReconciledWithoutDuplicateCommit'
    }
    'g3-fault-cargo-handoff' = [ordered]@{
        'G3-07-31' = 'faultCargoHandoffSequenceMatchesVector'
        'G3-07-32' = 'faultCargoHandoffRecorded'
        'G3-07-33' = 'handoffOnlyOnAuthorizedCommand'
        'G3-07-34' = 'handoffOutcomeReported'
        'G3-07-35' = 'handoffTerminatesWithoutDuplicateCommit'
    }
    'g3-forced-mechanical-recovery' = [ordered]@{
        'G3-07-41' = 'forcedMechanicalRecoverySequenceMatchesVector'
        'G3-07-42' = 'forcedRecoveryFencedByGeneration'
        'G3-07-43' = 'forcedRecoveryOutcomeReportedWithoutProof'
        'G3-07-44' = 'forcedRecoveryLeavesVehicleToReconcile'
        'G3-07-45' = 'forcedRecoveryPerformsNoElectronicAction'
    }
    'g3-manual-charging-return' = [ordered]@{
        'G3-07-51' = 'manualChargingReturnSequenceMatchesVector'
        'G3-07-52' = 'manualChargingReturnRequiresVerifiedAdministrator'
        'G3-07-53' = 'eligibilityReevaluatedAfterReturn'
        'G3-07-54' = 'manualChargingReturnHasNoSideEffects'
    }
}

function Get-ScriptFunction {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "The function source does not exist: $Path"
    }
    $parseErrors = $null
    $tokens = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($null -ne $parseErrors -and $parseErrors.Count -gt 0) {
        throw "The function source does not parse: $Path"
    }
    $definitions = @($ast.FindAll(
        { param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                       $node.Name -eq $Name },
        $true))
    if ($definitions.Count -ne 1) {
        throw "Expected exactly one function '$Name' in $Path, found $($definitions.Count)."
    }
    return $definitions[0].Extent.Text
}

# Taken from the restart runner rather than copied, like the demand-bearing runner does.
Invoke-Expression (Get-ScriptFunction -Path $CommitBindingFunctionSource -Name 'Get-SharedCommitBinding')

$commitBinding = Get-SharedCommitBinding -Path $SharedRunnerSource
$ControlServerCommit = $commitBinding['ControlServerCommit']
$OnboardCommit = $commitBinding['OnboardCommit']
$SimulatorCommit = $commitBinding['SimulatorCommit']
$ProtocolCommit = $commitBinding['ProtocolCommit']
$controlServerCommitSource = 'SHARED_BINDING'
if (-not [string]::IsNullOrEmpty($SelfCheckControlServerCommit)) {
    $ControlServerCommit = $SelfCheckControlServerCommit
    $controlServerCommitSource = 'SELF_CHECK_OVERRIDE'
}
$sharedRunnerSha256 = (Get-FileHash -LiteralPath $SharedRunnerSource -Algorithm SHA256).Hash.ToLowerInvariant()

$runnerCommit = (& git -C $ControlServerRepository rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "Unable to read the runner commit from $ControlServerRepository" }
$runnerWorktreeClean = @(& git -C $ControlServerRepository status --porcelain).Count -eq 0

$G3RunKind = 'JOURNEY_G3_REAL_ONBOARD_SIMULATED_COUNTERPARTS'
. (Join-Path $PSScriptRoot 'g3-slice-evidence.ps1')
if (-not [string]::IsNullOrEmpty($Slice)) { Assert-G3SliceIsClaimedBy -RunKind $G3RunKind -Slice $Slice }

if (Test-Path -LiteralPath $StageRoot) { throw "StageRoot must not already exist: $StageRoot" }
if (Test-Path -LiteralPath $EvidenceRoot) { throw "EvidenceRoot must not already exist: $EvidenceRoot" }
New-Item -ItemType Directory -Path $StageRoot, $EvidenceRoot | Out-Null

$sourcesRoot = Join-Path $StageRoot 'sources'
$peerCacheRoot = Join-Path $StageRoot 'peers'
$logsRoot = Join-Path $EvidenceRoot 'logs'
$scenariosRoot = Join-Path $EvidenceRoot 'scenarios'
New-Item -ItemType Directory -Path $sourcesRoot, $peerCacheRoot, $logsRoot, $scenariosRoot | Out-Null

$controlSource = Join-Path $sourcesRoot 'control-server'
$onboardSource = Join-Path $sourcesRoot 'onboard-hmi'
$simulatorSource = Join-Path $sourcesRoot 'slots-simulator'
$protocolSource = Join-Path $sourcesRoot 'protocol'

$commands = [System.Collections.Generic.List[object]]::new()

function Invoke-LoggedCommand {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$LogPath,
        # A scenario exits 1 on FAIL, and a failed scenario is a result to attribute, not a runner error.
        [switch]$AllowNonZeroExit
    )

    $startedAt = [DateTimeOffset]::UtcNow
    Push-Location $WorkingDirectory
    try {
        $output = & $FilePath @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    $output | Out-File -LiteralPath $LogPath -Encoding utf8NoBOM
    $commands.Add([ordered]@{
        name = $Name
        workingDirectory = $WorkingDirectory
        file = $FilePath
        arguments = $Arguments
        startedAtUtc = $startedAt
        exitCode = $exitCode
        log = [IO.Path]::GetRelativePath($EvidenceRoot, $LogPath).Replace('\', '/')
    })
    if ($exitCode -ne 0 -and -not $AllowNonZeroExit) { throw "$Name exited with code $exitCode. See $LogPath" }
    return $exitCode
}

# core.longpaths on every git command: the onboard repository commits G2 evidence deep enough to pass
# MAX_PATH below a clone directory (see scripts/l2/L2.psm1 Get-L2PeerPublish, 2026-09-13).
function New-ExactClone {
    param(
        [string]$Name,
        [string]$Repository,
        [string]$Destination,
        [string]$Commit,
        [string]$RemoteRef = ''
    )

    $null = Invoke-LoggedCommand -Name "clone-$Name" -WorkingDirectory $sourcesRoot -FilePath 'git' `
        -Arguments @('-c', 'core.autocrlf=false', '-c', 'core.longpaths=true', 'clone', '--no-hardlinks', '--no-checkout', $Repository, $Destination) `
        -LogPath (Join-Path $logsRoot "clone-$Name.log")
    & git -C $Destination config core.autocrlf false
    if ($LASTEXITCODE -ne 0) { throw "Unable to set core.autocrlf=false for $Name" }
    & git -C $Destination config core.longpaths true
    if ($LASTEXITCODE -ne 0) { throw "Unable to set core.longpaths=true for $Name" }
    $null = Invoke-LoggedCommand -Name "fetch-$Name" -WorkingDirectory $Destination -FilePath 'git' `
        -Arguments @('fetch', 'origin', '--prune', '--tags') `
        -LogPath (Join-Path $logsRoot "fetch-$Name.log")
    if (-not [string]::IsNullOrWhiteSpace($RemoteRef)) {
        $remoteTip = (& git -C $Destination rev-parse $RemoteRef).Trim()
        if ($LASTEXITCODE -ne 0 -or $remoteTip -ne $Commit) {
            throw "$Name remote ref mismatch: $RemoteRef=$remoteTip, expected $Commit"
        }
    }
    $null = Invoke-LoggedCommand -Name "checkout-$Name" -WorkingDirectory $Destination -FilePath 'git' `
        -Arguments @('checkout', '--detach', $Commit) `
        -LogPath (Join-Path $logsRoot "checkout-$Name.log")
    $actual = (& git -C $Destination rev-parse HEAD).Trim()
    $status = @(& git -C $Destination status --porcelain)
    if ($actual -ne $Commit -or $status.Count -ne 0) {
        throw "$Name exact checkout is not clean at $Commit"
    }
}

function Get-Sha256Text {
    param([Parameter(Mandatory)][string]$Text)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}

$runError = $null
$clonesVerified = $false
$protocolTagExists = $false
$expectedProtocol = $null
$scenarioResults = [System.Collections.Generic.List[object]]::new()

try {
    New-ExactClone -Name 'control-server' -Repository $ControlServerRepository -Destination $controlSource `
        -Commit $ControlServerCommit
    New-ExactClone -Name 'onboard-hmi' -Repository $OnboardRepository -Destination $onboardSource `
        -Commit $OnboardCommit -RemoteRef $OnboardRemoteRef
    New-ExactClone -Name 'slots-simulator' -Repository $SimulatorRepository -Destination $simulatorSource `
        -Commit $SimulatorCommit -RemoteRef 'origin/main'
    New-ExactClone -Name 'protocol' -Repository $ProtocolRepository -Destination $protocolSource `
        -Commit $ProtocolCommit
    $clonesVerified = $true

    # The identity the bound server enforces, read from the bound server's own settings mirror --
    # the clone, not this worktree, since the two may be different commits.
    $expectedProtocol = (Get-Content -Raw -LiteralPath (
        Join-Path $controlSource 'src\ControlServer.Host\appsettings.json') | ConvertFrom-Json).ProtocolCandidate
    if ($null -eq $expectedProtocol) { throw 'The bound ControlServer carries no ProtocolCandidate identity.' }
    if ($expectedProtocol.repositoryCommit -ne $ProtocolCommit) {
        throw "The bound ControlServer names protocol $($expectedProtocol.repositoryCommit), not the bound $ProtocolCommit."
    }

    # Tag absent, tag verified and git failing are three states; see run-staged-g3.ps1 for why the tag
    # namespace is asked explicitly.
    $listedTags = @(& git -C $protocolSource tag --list $expectedProtocol.tag)
    if ($LASTEXITCODE -ne 0) { throw "Unable to enumerate protocol tags in $protocolSource" }
    $protocolTagExists = $listedTags.Count -gt 0
    if ($protocolTagExists) {
        $tagCommit = (& git -C $protocolSource rev-list -n 1 "refs/tags/$($expectedProtocol.tag)^{commit}").Trim()
        if ($LASTEXITCODE -ne 0) { throw "Unable to resolve refs/tags/$($expectedProtocol.tag)" }
        if ($tagCommit -ne $ProtocolCommit) {
            throw "$($expectedProtocol.tag) exists but resolves to $tagCommit, not the bound commit $ProtocolCommit"
        }
    }
    if ($expectedProtocol.approvalStatus -eq 'APPROVED_RELEASE' -and -not $protocolTagExists) {
        throw "The identity claims APPROVED_RELEASE but $($expectedProtocol.tag) does not exist in $protocolSource"
    }

    $rig = Join-Path $controlSource 'scripts\l2\Invoke-L2Scenario.ps1'
    foreach ($scenario in $scenarioAssertions.Keys) {
        $scenarioEvidence = Join-Path $scenariosRoot $scenario
        $exitCode = Invoke-LoggedCommand -Name "scenario-$scenario" -WorkingDirectory $controlSource -FilePath 'pwsh' `
            -Arguments @('-NoProfile', '-File', $rig,
                '-Scenario', $scenario,
                '-EvidenceRoot', $scenarioEvidence,
                '-Repository', $controlSource,
                '-OnboardRepository', $onboardSource,
                '-SimulatorRepository', $simulatorSource,
                '-PeerCacheRoot', $peerCacheRoot,
                '-BatchId', 'batch-2') `
            -LogPath (Join-Path $logsRoot "scenario-$scenario.log") -AllowNonZeroExit
        $assertionsPath = Join-Path $scenarioEvidence 'assertions.json'
        $document = if (Test-Path -LiteralPath $assertionsPath) {
            Get-Content -Raw -LiteralPath $assertionsPath | ConvertFrom-Json
        } else { $null }
        $timelinePath = Join-Path $scenarioEvidence 'timeline.jsonl'
        $notes = if (Test-Path -LiteralPath $timelinePath) {
            @(Get-Content -LiteralPath $timelinePath | Where-Object { $_ -match '"note"' } |
                ForEach-Object { [string]($_ | ConvertFrom-Json).note })
        } else { @() }
        $scenarioResults.Add([pscustomobject]@{
            Name = $scenario
            ExitCode = $exitCode
            Evidence = [IO.Path]::GetRelativePath($EvidenceRoot, $scenarioEvidence).Replace('\', '/')
            Document = $document
            Notes = $notes
        })
    }
}
catch {
    $runError = $_
}

function Test-ScenarioIdentity {
    param([Parameter(Mandatory)][scriptblock]$Predicate)
    if ($null -ne $runError -or $scenarioResults.Count -ne $scenarioAssertions.Count) { return $false }
    foreach ($result in $scenarioResults) {
        if ($null -eq $result.Document -or -not (& $Predicate $result)) { return $false }
    }
    return $true
}

$assertions = [ordered]@{}
foreach ($scenario in $scenarioAssertions.Keys) {
    $result = @($scenarioResults | Where-Object { $_.Name -eq $scenario }) | Select-Object -First 1
    foreach ($l2Id in $scenarioAssertions[$scenario].Keys) {
        $judgment = if ($null -ne $result -and $null -ne $result.Document) {
            @($result.Document.assertions | Where-Object { [string]$_.id -eq $l2Id }) | Select-Object -First 1
        } else { $null }
        $assertions[$scenarioAssertions[$scenario][$l2Id]] = ($null -ne $judgment -and [string]$judgment.outcome -eq 'PASS')
    }
}

# Run-wide: what makes every scenario judgment above evidence about the bound identity at all.
$assertions['exactClonesAtTheSharedCommitBinding'] = $clonesVerified
$assertions['protocolReleaseTagResolvesToTheBoundCommit'] = $clonesVerified -and $protocolTagExists
$assertions['everyScenarioRanTheBoundControlServer'] = Test-ScenarioIdentity {
    param($r) [string]$r.Document.identity.controlServerCommit -eq $ControlServerCommit
}
$assertions['everyScenarioReportedTheBoundProtocolRelease'] = Test-ScenarioIdentity {
    param($r)
    $identity = $r.Document.identity.protocolReleaseIdentity
    $null -ne $identity -and $null -ne $expectedProtocol -and
        [string]$identity.tag -eq [string]$expectedProtocol.tag -and
        [string]$identity.commit -eq $ProtocolCommit -and
        [string]$identity.manifestSha256 -eq [string]$expectedProtocol.manifestSha256 -and
        [string]$identity.approvalStatus -eq [string]$expectedProtocol.approvalStatus
}
$assertions['everyScenarioRanTheRealOnboardRig'] = Test-ScenarioIdentity {
    param($r) [string]$r.Document.identity.rig -eq 'RealOnboard'
}
# Get-L2PeerPublish journals the commit it built or reused each peer at; the clones are detached at the
# binding, so anything else there means the rig published from somewhere other than the clones.
$assertions['everyScenarioPublishedThePeersAtTheBinding'] = Test-ScenarioIdentity {
    param($r)
    @($r.Notes | Where-Object { $_ -match "onboard-hmi (publish for|at) $OnboardCommit" }).Count -ge 1 -and
        @($r.Notes | Where-Object { $_ -match "slots-simulator (publish for|at) $SimulatorCommit" }).Count -ge 1
}
# A scenario that threw before reaching its judgments records a failureReason. Its missing judgments
# already read as failures above; this names the cause so the run does not look like a product FAIL.
$assertions['noScenarioAbortedBeforeItsJudgments'] = Test-ScenarioIdentity {
    param($r) [string]::IsNullOrEmpty([string]$r.Document.failureReason)
}

# The L2 rig generates its own loopback credentials, and its documented non-secrets are written into
# evidence on purpose. What must never reach evidence is a real secret the runner's environment happens
# to carry, so those are the values scanned for. Names are recorded; values never are.
$secretVariableNames = @('CONTROL_SERVER_RIOT_CALL_API_KEY', 'RIOT_CALL_API_KEY', 'CONTROL_SERVER_ONBOARD_CREDENTIAL',
    'CONTROL_SERVER_GOVERNANCE_CREDENTIAL', 'CONTROL_SERVER_RECOVERY_PROOF')
$secretValues = @($secretVariableNames | ForEach-Object { [Environment]::GetEnvironmentVariable($_) } |
    Where-Object { -not [string]::IsNullOrEmpty($_) -and $_.Length -ge 8 })
$secretLeakFiles = [System.Collections.Generic.List[string]]::new()
foreach ($file in @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File)) {
    try {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($value in $secretValues) {
            if ($null -ne $text -and $text.Contains($value, [StringComparison]::Ordinal)) {
                $secretLeakFiles.Add([IO.Path]::GetRelativePath($EvidenceRoot, $file.FullName).Replace('\', '/'))
                break
            }
        }
    }
    catch {
        # Binary evidence (snapshots of images, sqlite) is covered by the artifact hash list below.
    }
}
$assertions['secretScan'] = $secretLeakFiles.Count -eq 0

$assertionReport = [ordered]@{}
foreach ($name in $assertions.Keys) {
    $assertionReport[$name] = if ($assertions[$name]) { 'PASS' } else { 'FAIL_OR_INCONCLUSIVE' }
}
$failedAssertions = @($assertions.Keys | Where-Object { -not $assertions[$_] })

$status = if ($null -ne $runError) {
    'INCONCLUSIVE_RUNNER_ERROR'
} elseif ($failedAssertions.Count -eq 0) {
    'JOURNEY_G3_PASS'
} else {
    'JOURNEY_G3_SLICE_FAIL'
}

$firstIdentity = @($scenarioResults | Where-Object { $null -ne $_.Document } |
    ForEach-Object { $_.Document.identity.protocolReleaseIdentity }) | Select-Object -First 1
$commitsRecord = [ordered]@{
    controlServer = $ControlServerCommit
    controlServerCommitSource = $controlServerCommitSource
    onboardHmi = $OnboardCommit
    slotsSimulator = $SimulatorCommit
    protocol = $ProtocolCommit
    runner = $runnerCommit
    runnerWorktreeCleanAtStart = $runnerWorktreeClean
}

$gateResultPaths = @()
if ($null -ne $firstIdentity) {
    $gateResultPaths = Write-G3GateResults -RunKind $G3RunKind -EvidenceRoot $EvidenceRoot `
        -AssertionReport $assertionReport -Slice $Slice -RunnerErrored:($null -ne $runError) -Context @{
            runId = $runId
            startedAt = $runStartedAt.ToString('O')
            commits = $commitsRecord
            # Read back from the servers the scenarios talked to, like the demand-bearing runner does.
            protocolReleaseVersion = $firstIdentity.releaseVersion
            protocolTag = $firstIdentity.tag
            protocolProfileId = $firstIdentity.profileId
            protocolVersion = $firstIdentity.protocolVersion
            protocolApprovalStatus = $firstIdentity.approvalStatus
            protocolRepositoryCommit = $ProtocolCommit
            protocolManifestSha256 = $firstIdentity.manifestSha256
            protocolSchemaBundleSha256 = $firstIdentity.schemaBundleSha256
            protocolVectorsSha256 = $firstIdentity.vectorsSha256
            sliceIndexPath = (Join-Path $controlSource 'vendor\8005-agv-protocol\integration-slices\index.json')
            sliceIndexSource = 'vendor/8005-agv-protocol/integration-slices/index.json'
        }
} else {
    # No scenario reached the server, so no run can say what protocol identity it certified. Writing a
    # gate result with blank identity fields is what Write-G3GateResult refuses, and so does this.
    if ($null -eq $runError) { $runError = [System.Management.Automation.ErrorRecord]::new(
        [InvalidOperationException]::new('No scenario reported a protocol release identity.'),
        'NoScenarioIdentity', 'InvalidResult', $null) }
    $status = 'INCONCLUSIVE_RUNNER_ERROR'
}

$configuration = [ordered]@{
    loopbackOnly = $true
    unattended = $true
    interactiveDesktopRequired = $true
    rig = 'L2 real-onboard rig, run from the ControlServer clone at the bound commit'
    onboard = 'shipped SQCD.Agv.Wpf, driven through UI Automation'
    slotsSimulator = 'real SQCD_8005AGV_Simulator over Modbus'
    riot = 'tools/ControlServer.FakeRiot (loopback)'
    mesIngest = 'tools/ControlServer.FakeMesIngest (loopback)'
    journeyRuntimeEnabled = $true
    realExternalCredentialsUsed = $false
    realRiotOrderCreated = $false
    realVehicleMoved = $false
    commitBinding = [ordered]@{
        source = [IO.Path]::GetRelativePath($ControlServerRepository, $SharedRunnerSource).Replace('\', '/')
        sourceSha256 = $sharedRunnerSha256
        readFrom = 'param-block-defaults'
        controlServerCommitSource = $controlServerCommitSource
    }
    scenarios = @($scenarioAssertions.Keys)
    secretVariablesScanned = $secretVariableNames
}
$configurationJson = $configuration | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText(
    (Join-Path $EvidenceRoot 'configuration.json'), $configurationJson, [Text.UTF8Encoding]::new($false))

$artifactFiles = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File |
    Where-Object { $_.Name -ne 'run-result.json' } |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($EvidenceRoot, $_.FullName).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            length = $_.Length
        }
    })

$result = [ordered]@{
    schemaVersion = '1.0.0'
    runKind = $G3RunKind
    runId = $runId
    startedAtUtc = $runStartedAt
    completedAtUtc = [DateTimeOffset]::UtcNow
    status = $status
    classification = (New-G3Classification -RunKind $G3RunKind -RunStatus $status `
        -AssertionReport $assertionReport -RunnerErrored:($null -ne $runError))
    gateResults = @($gateResultPaths | ForEach-Object {
        [IO.Path]::GetRelativePath($EvidenceRoot, $_).Replace('\', '/') })
    commits = $commitsRecord
    configurationSha256 = Get-Sha256Text $configurationJson
    configuration = $configuration
    commands = @($commands)
    scenarios = @($scenarioResults | ForEach-Object {
        [ordered]@{
            name = $_.Name
            exitCode = $_.ExitCode
            evidence = $_.Evidence
            outcome = if ($null -ne $_.Document) { $_.Document.outcome } else { $null }
            failureReason = if ($null -ne $_.Document) { $_.Document.failureReason } else { $null }
            identity = if ($null -ne $_.Document) { $_.Document.identity } else { $null }
        }
    })
    assertions = $assertionReport
    failedAssertions = $failedAssertions
    error = if ($null -ne $runError) {
        [ordered]@{ type = $runError.Exception.GetType().FullName; message = $runError.Exception.Message }
    } else { $null }
    secretLeakFiles = @($secretLeakFiles)
    evidenceFiles = $artifactFiles
}

$resultJson = $result | ConvertTo-Json -Depth 40
[IO.File]::WriteAllText(
    (Join-Path $EvidenceRoot 'run-result.json'), $resultJson, [Text.UTF8Encoding]::new($false))
$resultJson

if ($status -ne 'JOURNEY_G3_PASS' -or $secretLeakFiles.Count -ne 0) {
    throw "Journey G3 did not pass: $status. Evidence: $EvidenceRoot"
}
