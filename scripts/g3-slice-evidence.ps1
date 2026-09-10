#Requires -Version 7

# Dot-sourced by the three G3 runners. Definitions only: nothing here has a side effect, so
# dot-sourcing cannot start, write or clone anything. That is why this is a plain file rather than
# another Get-ScriptFunction extraction -- the AST-parsing route the runners use for each other
# exists because the commit binding lives inside a param block, which cannot be dot-sourced without
# running the script. Functions can.
#
# ---------------------------------------------------------------------------------------------
# What this file holds is a table that, before ticket 23, existed nowhere.
#
# The three runners carry 19, 20 and 20 named assertions between them and not one of them is tagged
# with a slice. Each runner instead wrote its slice conclusion as a literal -- formalSlicePass =
# $false, and an officialSlices array naming two slices as INCONCLUSIVE -- so "which assertion is
# evidence for which slice" lived only in the head of whoever wrote that literal, in b4afdb3.
#
# The attribution below is therefore a first written statement, derived from the assertion names
# and from each slice's vectorIds in the protocol's own frozen index. It is not a transcription of
# something that was already recorded, and it deserves review by whoever owns the slice family.
# What makes it safe to rely on today is Assert-G3ClaimCoversReport: a runner that grows, renames or
# drops an assertion without updating its claim throws instead of quietly reattributing evidence.
#
# ---------------------------------------------------------------------------------------------
# The ruling, 2026-09-09 (ticket 23, the user's decision -- recorded here rather than only in a
# commit message, because this is the constant it governs):
#
#   The question was whether a staged G3 run of a slice counts as that slice passing G3. The
#   2026-09-04 evidence said it does not: a staged run binds commits, rebuilds from an exact clone
#   and drives synthetic peers, and it never touches a candidate release artefact. The user ruled
#   neither "keep that" nor "call it a pass", but grade it -- split the one word into two fields:
#
#     status          did this slice's assertions pass in this run
#     assuranceLevel  what the run actually exercised
#
#   and let formalSlicePass be computed from the pair instead of written as a literal $false.

# The levels that count as a slice pass, per that ruling. CANDIDATE_ARTEFACT is deliberately absent
# from this list AND unoccupied by any runner: it is the level a run would reach by exercising the
# candidate release artefact itself, which nothing does today. It is listed so that the ladder shows
# its own top rung rather than ending at the highest rung that happens to be built.
function Get-G3AssuranceLevelsThatCountAsSlicePass {
    return @('STAGED_REBUILD', 'DEMAND_BEARING_RESTORE')
}

function Get-G3AssuranceLevelLadder {
    return [ordered]@{
        STAGED_REBUILD = 'Commits bound; peers rebuilt from exact clones; synthetic peer harness; no candidate release artefact touched.'
        DEMAND_BEARING_RESTORE = 'As STAGED_REBUILD, plus a restored controlserver.db from an authorised field run.'
        CANDIDATE_ARTEFACT = 'The candidate release artefact itself is what runs. No runner produces this level today.'
    }
}

# The slices no G3 runner covers. Counting the onboard runner alongside the three here, together they
# name FP-IS-00/04/05/06 and, since 2026-09-10, FP-IS-14 and FP-IS-15; the slices below appear in
# none of them.
# The user ruled on 2026-09-09 that a batch records such a gap rather than writing new
# cross-repository scenarios to close it, so these slices emit no gate-result.json at all -- an
# absent artefact, not an INCONCLUSIVE one, on the same reasoning as ticket 14's "a filter that
# selects nothing is refused, not written up as green".
#
# FP-IS-14 was listed here on 2026-09-10 as a gap of a different kind -- not a missing assertion but a
# slice that could not be asserted at all, because nothing outside the process could start an
# activation. The activation entry point landed the same day and the staged runner now claims it, so
# it is no longer in this table. The distinction is worth keeping in mind: the four below lack an
# assertion somebody could write against the peers as they stand.
function Get-G3SlicesWithoutSurfaceThisBatch {
    return [ordered]@{
        'FP-IS-01' = 'CV-DEMAND-ACCEPT-TO-PICKUP has no assertion in any G3 runner.'
        'FP-IS-02' = 'CV-PICKUP-SUBLOT-LOAD, CV-LOAD-CORRECTION and CV-LOAD-CANCELLATION-ALL-EMPTY have no assertion in any G3 runner.'
        'FP-IS-03' = 'CV-PREDEPARTURE-SAFETY-EXPIRES and CV-OPERATION-RESULT-UNKNOWN-RECONCILE have no assertion in any G3 runner.'
        'FP-IS-07' = 'None of its six vectors has an assertion in any G3 runner; the staged runner records several of its accepted paths as not reachable in a staged run.'
    }
}

# Per-runner claim. runWide lists the assertions that are preconditions or whole-run safety checks:
# they are not evidence for one slice rather than another, so every slice the runner claims carries
# them. A slice passes only if its own assertions AND the run-wide ones passed.
function Get-G3RunnerClaim {
    param([Parameter(Mandatory)][string]$RunKind)

    $claims = @{
        'STAGED_G3_REAL_PEERS_DETERMINISTIC_PLAINTEXT' = [ordered]@{
            assuranceLevel = 'STAGED_REBUILD'
            runWide = @(
                'identityRejections',
                'noMovementOrExternalSideEffects',
                'secretScan')
            slices = [ordered]@{
                'FP-IS-00' = @(
                    'recoveryStateReportFirstAckDropReplay',
                    'recoveryStateReportFirstAckDropReplayOverPlaintext',
                    'recoverySessionAuthorisationBoundary',
                    'recoveryActionsRefusedWithoutPersistedOperation',
                    'hardwareRecoveryRecordScopeEnforced',
                    'recoveryCommandSurvivesMidFlightDisconnect',
                    'forcedRecoveryGenerationAdvancesMonotonically',
                    'supersededGenerationResultIsHistoricalEvidenceOnly',
                    'recoveryNeverReportsFalseCompletion')
                'FP-IS-06' = @(
                    'sameConnectionSameMessageIdSameContent',
                    'sameMessageIdDifferentContentStableConflict',
                    'businessMessageSameMessageIdSameContentReplay',
                    'businessMessageSameMessageIdDifferentContentStableConflict',
                    'businessMessageAckDropInSessionReplay',
                    'businessMessageDelayedDeliveryAccepted',
                    'businessMessageReorderedDeliveryAccepted')
                # FP-IS-14 landed on 2026-09-10 together with the activation entry point. Until that
                # entry point existed nothing outside the process could start an activation, so this
                # slice had no surface at all -- it was listed in the gap table below.
                #
                # The last assertion is the one the slice exists for. No protocol message carries slot
                # IO bindings, so message 7 carries a version name and a fingerprint and the vehicle
                # recomputes the digest of what it actually holds. A converged activation therefore
                # means two implementations, in two processes, produced the same digest from their own
                # copies -- which the pinned literal in each repository's unit tests cannot say.
                'FP-IS-14' = @(
                    'slotConfigurationActivationCarriesOneMessageIdOnly',
                    'slotConfigurationActivationReplayedByteForByteAfterAMidFlightDrop',
                    'slotConfigurationActivationPersistedBeforeItWasSent',
                    'slotConfigurationActivationResultReportedByTheVehicle',
                    'bothEndsComputedTheSameSlotConfigurationFingerprint')
                # FP-IS-15 needs no driving: the onboard alarm board publishes a snapshot as part of
                # every FULL handshake. Message 9 landed on 2026-09-10.
                #
                # "Full" is load-bearing and was got wrong once. The first run of this claim asserted
                # one snapshot per CONNECTION and failed: a reconnect that resumes an interrupted
                # recovery replays the unacknowledged message and republishes no snapshots at all.
                # That FAIL is kept at evidence/g3/20260910-fp-is-15-v2-alarm-snapshot. The resume
                # assertion below is what replaced it, and it now pins that behaviour rather than
                # contradicting it.
                #
                # This runner reaches one half of the (generation, sequence) adoption rule: the row
                # carries the generation its snapshot arrived in. The other half -- a restarted
                # vehicle whose sequence returns to 1 is still adopted -- needs the onboard process
                # to restart, so it belongs to run-staged-g3-restart.ps1 and is not claimed here.
                'FP-IS-15' = @(
                    'onboardAlarmSnapshotPublishedOnTheFullHandshake',
                    'onboardAlarmSnapshotAppliedAckOnEverySnapshot',
                    'onboardAlarmSnapshotNotRepublishedOnRecoveryResume',
                    'onboardAlarmProjectionIsASingletonPerVehicle',
                    'onboardAlarmProjectionCarriesTheGenerationItArrivedIn')
            }
        }
        'STAGED_G3_REAL_PEERS_PROCESS_RESTART_NO_MOVEMENT' = [ordered]@{
            assuranceLevel = 'STAGED_REBUILD'
            runWide = @(
                'commitBindingSharedWithMainRunner',
                'runningControlServerReportsBoundBuildCommit',
                'runningOnboardReportsBoundBuildCommit',
                'protocolIdentityBoundToRelease',
                'noPeerExitedUnexpectedly',
                'onboardHostProcessReplacedOnlyInPhaseTwo',
                'controlServerHostProcessReplacedOnlyInPhaseThree',
                'noMovementOrExternalSideEffects',
                'secretScan')
            slices = [ordered]@{
                'FP-IS-00' = @(
                    'freshDatabaseStartsAtGenerationOne',
                    'onboardRestartAdvancesGenerationByExactlyOne',
                    'controlServerRestartAdvancesGenerationByExactlyOne',
                    'sessionGenerationStableWithinEveryPhase',
                    'serverSessionIdentityIsScopedPerConnection',
                    'recoveryReportIdentityAgreesAcrossPeers',
                    'sessionRecoveryRowStaysASingletonPerAgv',
                    'onboardJournalEpochStableAcrossOnboardRestart')
                'FP-IS-06' = @(
                    'controlDatabaseFileReusedAcrossServerRestart',
                    'controlInboxRowsSurviveServerRestart',
                    'onboardOutboxRowsSurviveOnboardRestart')
                # FP-IS-15's other half, which only this runner can reach. The staged runner proves
                # the projection holds the generation its snapshot arrived in; this one restarts the
                # onboard PROCESS, so the vehicle comes back with an alarm board counting from 1
                # again. Reading generation 2 in the projection after that is the proof that adoption
                # is keyed on (generation, sequence) and not on sequence alone -- a sequence-only rule
                # would have ignored the post-restart snapshot, since 1 does not advance past 1.
                'FP-IS-15' = @(
                    'onboardAlarmProjectionAdoptedTheRestartedVehiclesSnapshot',
                    'onboardAlarmProjectionNeverRegressedToAnEarlierGeneration')
            }
        }
        'DEMAND_BEARING_G3_RESULT_AND_RIOT_UNKNOWN_VECTORS_NO_MOVEMENT' = [ordered]@{
            assuranceLevel = 'DEMAND_BEARING_RESTORE'
            runWide = @(
                'protocolAndBuildIdentityBoundToTheSharedBinding',
                'noMovementOrExternalSideEffects',
                'listenersReleased',
                'secretScan')
            slices = [ordered]@{
                'FP-IS-04' = @(
                    'riotPreCreateReconciliationObservesUnknownOnEveryLeg',
                    'riotUnknownIsAnExactAbsentAtObservation',
                    'riotUnknownStillCreatesExactlyOncePerLeg',
                    'riotUnknownResolvesToTheOrderItCreated',
                    'preparedAttemptAcceptsItsFirstResult',
                    'identicalResultReplayReturnsTheStoredAcknowledgement',
                    'sameMessageIdWithDifferentContentIsRefused',
                    'sameAttemptAndGenerationUnderANewMessageIdIsRefused',
                    'alreadyCommittedAttemptRefusesASecondResult',
                    'replayedResultWasNotProcessedTwice',
                    'unloadResultClosedTheDemandAtomically')
                'FP-IS-05' = @(
                    'resultFromASupersededSessionGenerationIsRefused',
                    'controlServerHostProcessWasActuallyReplaced',
                    'acceptedDemandSurvivesTheHostRestart',
                    'vehicleDispatchLeaseSurvivesTheHostRestart',
                    'restartedHostServesTheSameStore')
            }
        }
    }

    if (-not $claims.ContainsKey($RunKind)) {
        throw ("No G3 slice claim is registered for runKind '$RunKind'. Registered: " +
               (($claims.Keys | Sort-Object) -join ', ') + '.')
    }
    return $claims[$RunKind]
}

# Drift guard. The attribution above is a hand-written table over assertion names the runners own,
# so the failure it has to prevent is silent: an assertion renamed, added or dropped in a runner
# while the table keeps answering for the old shape. Both directions are checked, and both throw --
# an unattributed assertion would go uncounted toward every slice, and an attributed name that no
# longer exists would count a slice as passing on evidence that was not produced.
function Assert-G3ClaimCoversReport {
    param(
        [Parameter(Mandatory)][string]$RunKind,
        [Parameter(Mandatory)]$AssertionReport
    )

    $claim = Get-G3RunnerClaim -RunKind $RunKind
    $claimed = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $claim.runWide) { $claimed.Add($name) }
    foreach ($slice in $claim.slices.Keys) {
        foreach ($name in $claim.slices[$slice]) { $claimed.Add($name) }
    }

    $duplicates = @($claimed | Group-Object | Where-Object Count -GT 1 | ForEach-Object Name)
    if ($duplicates.Count -ne 0) {
        throw ("The G3 slice claim for '$RunKind' attributes these assertions more than once: " +
               ($duplicates -join ', ') + '.')
    }

    $reported = @($AssertionReport.Keys)
    $unattributed = @($reported | Where-Object { $_ -notin $claimed })
    if ($unattributed.Count -ne 0) {
        throw ("These assertions are reported by '$RunKind' but attributed to no slice and not " +
               'listed as run-wide, so no slice result can be computed: ' +
               ($unattributed -join ', ') + '. Update Get-G3RunnerClaim in scripts/g3-slice-evidence.ps1.')
    }

    $missing = @($claimed | Where-Object { $_ -notin $reported })
    if ($missing.Count -ne 0) {
        throw ("The G3 slice claim for '$RunKind' names assertions the run did not report: " +
               ($missing -join ', ') + '. Update Get-G3RunnerClaim in scripts/g3-slice-evidence.ps1.')
    }
}

function Assert-G3SliceIsClaimedBy {
    param(
        [Parameter(Mandatory)][string]$RunKind,
        [Parameter(Mandatory)][string]$Slice
    )

    $claim = Get-G3RunnerClaim -RunKind $RunKind
    if ($claim.slices.Contains($Slice)) { return }

    $noSurface = Get-G3SlicesWithoutSurfaceThisBatch
    if ($noSurface.Contains($Slice)) {
        throw ("Slice '$Slice' has no G3 surface in this batch, so no runner can certify it and " +
               "none will write evidence for it. $($noSurface[$Slice]) " +
               'Recorded by the user ruling of 2026-09-09, ticket 23.')
    }
    throw ("Runner '$RunKind' does not claim slice '$Slice'. It claims: " +
           (($claim.slices.Keys) -join ', ') + '.')
}

# A slice's own assertions plus the run-wide ones, in the order the claim lists them.
function Get-G3SliceAssertionReport {
    param(
        [Parameter(Mandatory)][string]$RunKind,
        [Parameter(Mandatory)][string]$Slice,
        [Parameter(Mandatory)]$AssertionReport
    )

    $claim = Get-G3RunnerClaim -RunKind $RunKind
    $report = [ordered]@{}
    foreach ($name in $claim.slices[$Slice]) { $report[$name] = $AssertionReport[$name] }
    foreach ($name in $claim.runWide) { $report[$name] = $AssertionReport[$name] }
    return $report
}

# INCONCLUSIVE only when the runner itself failed: a run that threw before its assertions ran did
# not observe the slice, which is a different statement from observing it and finding it wrong.
function Get-G3SliceStatus {
    param(
        [Parameter(Mandatory)][string]$RunKind,
        [Parameter(Mandatory)][string]$Slice,
        [Parameter(Mandatory)]$AssertionReport,
        [switch]$RunnerErrored
    )

    if ($RunnerErrored) { return 'INCONCLUSIVE' }
    $sliceReport = Get-G3SliceAssertionReport -RunKind $RunKind -Slice $Slice -AssertionReport $AssertionReport
    foreach ($name in $sliceReport.Keys) {
        if ($sliceReport[$name] -ne 'PASS') { return 'FAIL' }
    }
    return 'PASS'
}

# Replaces the literal classification block the three runners used to carry. officialSlices is now
# computed from the claim and the assertion results; formalSlicePass from the ruling above.
function New-G3Classification {
    param(
        [Parameter(Mandatory)][string]$RunKind,
        [Parameter(Mandatory)][string]$RunStatus,
        [Parameter(Mandatory)]$AssertionReport,
        [switch]$RunnerErrored
    )

    $claim = Get-G3RunnerClaim -RunKind $RunKind
    $counting = Get-G3AssuranceLevelsThatCountAsSlicePass
    $official = [System.Collections.Generic.List[object]]::new()
    $allPass = $true
    foreach ($slice in $claim.slices.Keys) {
        $sliceStatus = Get-G3SliceStatus -RunKind $RunKind -Slice $slice `
            -AssertionReport $AssertionReport -RunnerErrored:$RunnerErrored
        $formal = ($sliceStatus -eq 'PASS') -and ($claim.assuranceLevel -in $counting)
        if (-not $formal) { $allPass = $false }
        $official.Add([ordered]@{
            integrationSliceId = $slice
            status = $sliceStatus
            assuranceLevel = $claim.assuranceLevel
            formalSlicePass = $formal
        })
    }

    return [ordered]@{
        runStatus = $RunStatus
        assuranceLevel = $claim.assuranceLevel
        formalSlicePass = $allPass
        officialSlices = @($official)
        slicesWithoutSurfaceThisBatch = @((Get-G3SlicesWithoutSurfaceThisBatch).Keys)
        # Unchanged, and still literal on purpose: one runner covering its own slices says nothing
        # about the gate as a whole or about a release candidate that has not been cut.
        fullG3 = 'INCONCLUSIVE'
        releaseCandidate = 'INCONCLUSIVE'
    }
}

# One file per slice, mirroring the two G2 harnesses: ticket 17 counts files, and both G2 gates
# already write one per slice. schemaVersion 1.3.0 -- 1.2.0 was additive over the 1.1.0 both G2
# harnesses emit, and 1.3.0 is additive again (ticket 24's optional fieldStoreProvenance), so a
# 1.1.0 reader still parses it, but a reader that cannot tell the shapes apart cannot tell a graded
# G3 result from an ungraded G2 one either.
function Write-G3GateResult {
    param(
        [Parameter(Mandatory)][string]$RunKind,
        [Parameter(Mandatory)][string]$EvidenceRoot,
        [Parameter(Mandatory)][string]$Slice,
        [Parameter(Mandatory)]$AssertionReport,
        [Parameter(Mandatory)][hashtable]$Context,
        [switch]$RunnerErrored
    )

    $claim = Get-G3RunnerClaim -RunKind $RunKind
    $sliceIndex = Get-Content -Raw -LiteralPath $Context['sliceIndexPath'] | ConvertFrom-Json
    $entry = $sliceIndex.slices | Where-Object { $_.integrationSliceId -eq $Slice }
    if ($null -eq $entry) { throw "Slice '$Slice' is not in the frozen slice family index." }

    $sliceReport = Get-G3SliceAssertionReport -RunKind $RunKind -Slice $Slice -AssertionReport $AssertionReport
    $status = Get-G3SliceStatus -RunKind $RunKind -Slice $Slice `
        -AssertionReport $AssertionReport -RunnerErrored:$RunnerErrored
    $counting = Get-G3AssuranceLevelsThatCountAsSlicePass

    $result = [ordered]@{
        schemaVersion = '1.3.0'
        gate = 'G3'
        runKind = $RunKind
        runId = $Context['runId']
        integrationSliceId = $Slice
        status = $status
        assuranceLevel = $claim.assuranceLevel
        formalSlicePass = ($status -eq 'PASS') -and ($claim.assuranceLevel -in $counting)
        startedAt = $Context['startedAt']
        finishedAt = ([DateTimeOffset]::UtcNow).ToString('O')
        implementationRepository = '8005-agv-control-server'
        commits = $Context['commits']
        protocolReleaseVersion = $Context['protocolReleaseVersion']
        protocolTag = $Context['protocolTag']
        protocolProfileId = $Context['protocolProfileId']
        protocolVersion = $Context['protocolVersion']
        protocolApprovalStatus = $Context['protocolApprovalStatus']
        protocolRepositoryCommit = $Context['protocolRepositoryCommit']
        protocolManifestSha256 = $Context['protocolManifestSha256']
        protocolSchemaBundleSha256 = $Context['protocolSchemaBundleSha256']
        protocolVectorsSha256 = $Context['protocolVectorsSha256']
        integrationSliceIndexSha256 = (Get-FileHash -LiteralPath $Context['sliceIndexPath'] -Algorithm SHA256).Hash.ToLowerInvariant()
        integrationSliceIndexSource = $Context['sliceIndexSource']
        vectorIds = @($entry.vectorIds)
    }

    # Recorded facts that are deliberately NOT assertions. Ticket 24 split the restored field store's
    # own protocolCommit out of the demand-bearing runner's identity assertion: it reports the history
    # of the field run the store came from, which no v2-identity run can match, so asserting it kept
    # six meaningful identity checks permanently red behind one name. Only the runner that restores a
    # store passes this; the others would carry an empty section, which reads as a measurement that
    # went missing rather than one that does not apply.
    if ($Context.ContainsKey('fieldStoreProvenance')) {
        $provenance = $Context['fieldStoreProvenance']
        # Same rule as the identity fields below: a section that is written at all has to carry a
        # value. Empty here means the store was never read, which is a run problem and must not go
        # out looking like a complete result.
        if ($null -eq $provenance['protocolCommit'] -or "$($provenance['protocolCommit'])".Length -eq 0) {
            throw ("The gate result for $Slice carries a fieldStoreProvenance with no protocolCommit. " +
                   'The restored store was not read, so the run writes nothing rather than a blank field.')
        }
        $result['fieldStoreProvenance'] = $provenance
    }

    # The attribution this whole file exists to write down: which named assertions of this run
    # are the evidence for this slice, and what each of them said.
    $result['assertionIds'] = $sliceReport

    # Every identity field has to have a value. Measured on 2026-09-09 before this check existed:
    # the two runners that read their identity back from the running host asked it for
    # $version.releaseVersion, which the host names protocolReleaseVersion, and a gate result went
    # out with a null release version beside eight correct fields. A gate result is read to decide
    # what a run certified; a null there reads as "not applicable" rather than "we asked wrong".
    foreach ($required in @('protocolReleaseVersion', 'protocolTag', 'protocolProfileId',
                            'protocolVersion', 'protocolApprovalStatus', 'protocolRepositoryCommit',
                            'protocolManifestSha256', 'protocolSchemaBundleSha256',
                            'protocolVectorsSha256')) {
        if ($null -eq $result[$required] -or "$($result[$required])".Length -eq 0) {
            throw ("The gate result for $Slice has no $required. The run cannot say what protocol " +
                   'identity it certified, so it writes nothing rather than a blank field.')
        }
    }

    $directory = Join-Path (Join-Path $EvidenceRoot 'slices') $Slice
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $path = Join-Path $directory 'gate-result.json'
    [IO.File]::WriteAllText($path, ($result | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    return $path
}

# Writes one file per claimed slice, or just the requested one when -Slice was given. The -Slice
# parameter selects what is certified, NOT what is run: a G3 run is one end-to-end scenario, not a
# filterable set of tests, so narrowing it would change what the evidence means.
function Write-G3GateResults {
    param(
        [Parameter(Mandatory)][string]$RunKind,
        [Parameter(Mandatory)][string]$EvidenceRoot,
        [Parameter(Mandatory)]$AssertionReport,
        [Parameter(Mandatory)][hashtable]$Context,
        [string]$Slice,
        [switch]$RunnerErrored
    )

    Assert-G3ClaimCoversReport -RunKind $RunKind -AssertionReport $AssertionReport
    $claim = Get-G3RunnerClaim -RunKind $RunKind
    $targets = if ([string]::IsNullOrEmpty($Slice)) { @($claim.slices.Keys) } else { @($Slice) }

    $written = [System.Collections.Generic.List[string]]::new()
    foreach ($target in $targets) {
        $written.Add((Write-G3GateResult -RunKind $RunKind -EvidenceRoot $EvidenceRoot -Slice $target `
            -AssertionReport $AssertionReport -Context $Context -RunnerErrored:$RunnerErrored))
    }
    return @($written)
}
