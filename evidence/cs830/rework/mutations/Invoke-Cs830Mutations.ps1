#Requires -Version 7
# control-server#830 mutation self-check. Each mutation replaces one exact text in one committed file, runs the
# skip tests, records the outcome, and restores that one file from HEAD. Run from the ticket's worktree.
param(
    [Parameter(Mandatory)][string]$Worktree,
    [Parameter(Mandatory)][string]$OutDir,
    [string[]]$Only = @()
)
Set-StrictMode -Version Latest
$Only = @($Only | ForEach-Object { $_ -split "," } | Where-Object { $_ })
$ErrorActionPreference = 'Stop'

$mutations = @(
    @{
        Id = 'M1-settle-without-authorization'
        File = 'src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs'
        Old = '        if (authorized is null)
        {
            return null;
        }
        bool earlierResult'
        New = '        authorized ??= [.. result.SlotEvidence.Where(item => item.State == SlotBusinessState.Occupied).Select(item => item.SlotNumber).Order()];
        bool earlierResult'
        Expect = 'ASkippedResultWithoutAMatchingAuthorizationIsNeverSettledAsComplete'
    },
    @{
        Id = 'M2-no-release-of-membership-slots'
        File = 'src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs'
        Old = '            membership.TargetSlotsJson = loadedJson;'
        New = '            _ = loadedJson;'
        Expect = 'ThreeBasketsLoadedTwo|TheSkippedSlotIsFree'
    },
    @{
        Id = 'M3-no-authorization-path'
        File = 'src/ControlServer.Host/Runtime/RemainingBasketsSkips.cs'
        Old = '            if (decision is null)
            {
                awaitingReadings = true;
                continue;
            }'
        New = '            if (decision is null || decision.Outcome == RemainingBasketsSkipRule.Authorized)
            {
                continue;
            }'
        Expect = 'Authorized|ThreeBasketsLoadedTwo'
    },
    @{
        Id = 'M4-lock-checked-before-unknown'
        File = 'src/ControlServer.Domain/RemainingBasketsSkipRule.cs'
        Old = '        if (targets.Any(slot => Reading(slot) is not { IsUnknown: false }))'
        New = '        if (targets.Any(slot => Reading(slot) is { Lock: "UNLOCKED" }))
        {
            return Reject(ServerReasonCodes.LockNotClosed, "payload.loadedSlots", "x");
        }
        if (targets.Any(slot => Reading(slot) is not { IsUnknown: false }))'
        Expect = 'unknown-ahead-of-unlocked'
    },
    @{
        Id = 'M5-stale-readings-used'
        File = 'src/ControlServer.Host/Runtime/SessionSlotReadings.cs'
        Old = '        if (latest is not JsonElement snapshot ||
            changes.Any(change => change.Version > latestVersion && change.Slots.Any(targets.Contains)))'
        New = '        if (latest is not JsonElement snapshot)'
        Expect = 'ARequestOverStaleReadingsWaits|AResultArrivingWhileASkipIsPending|ACancellationWhileASkipIsPendingOrAuthorizedIsRefused|GoesToRecovery'
    },
    @{
        Id = 'M6-cancellation-not-held-by-skip'
        File = 'src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs'
        Old = '        bool authorized = !declared && !skipHeld &&'
        New = '        bool authorized = !declared &&'
        Expect = 'ACancellationWhileASkipIsPendingOrAuthorizedIsRefused'
    },
    @{
        Id = 'M7-no-replay-of-resubmitted-request'
        File = 'src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs'
        Old = '        if (replaySkipRequestId is not null &&
            !staged.Contains'
        New = '        if (replaySkipRequestId is not null && staged.Count < 0 &&
            !staged.Contains'
        Expect = 'TheSameSkipRequestIdIsAnsweredFromTheRecord'
    },
    @{
        Id = 'M8-second-authorization-allowed'
        File = 'src/ControlServer.Host/Runtime/RemainingBasketsSkips.cs'
        Old = '            return "这次装货已经同意过剩下的不装。";'
        New = '            return null;'
        Expect = 'ASecondSkipOfAnAuthorizedAttemptIsRefused'
    },
    @{
        Id = 'M9-skipped-reason-not-checked'
        File = 'src/ControlServer.Domain/RemainingBasketsSkipRule.cs'
        Old = '                : outcome.Outcome == "NOT_STARTED" && evidence.State == SlotBusinessState.Empty && skipReason;'
        New = '                : outcome.Outcome == "NOT_STARTED" && skipReason;'
        Expect = 'remaining-slot-not-empty'
    },
    @{
        Id = 'M10-load-scope-not-emptied'
        File = 'src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs'
        Old = '            // what its result is reconciled against.
            slots = Array.Empty<int>(),'
        New = '            // what its result is reconciled against.
            slots = ParseSlots(operation?.TargetSlotsJson ?? "[]"),'
        Expect = 'schema-conformance'
    },
    @{
        Id = 'U1-spent-authorization-reused'
        File = 'src/ControlServer.Infrastructure/Persistence/WireToGateStore.cs'
        Old = '        return !earlierResult && RemainingBasketsSkipRule.SettlesAuthorization('
        New = '        return RemainingBasketsSkipRule.SettlesAuthorization('
        Expect = 'AResumedReplacementResultNeverUsesAnAuthorizationTheFirstResultSpent'
    },
    @{
        Id = 'U2-remaining-slot-lock-and-output-unchecked'
        File = 'src/ControlServer.Domain/RemainingBasketsSkipRule.cs'
        Old = '            if (!settled || !evidence.DoorLocked || !evidence.UnlockOutputReset)'
        New = '            if (!settled)'
        Expect = 'remaining-slot-unlocked|remaining-slot-output-active'
    },
    @{
        Id = 'U3-earlier-session-readings-used'
        File = 'src/ControlServer.Host/Runtime/SessionSlotReadings.cs'
        Old = '                session.ValueKind != JsonValueKind.Number || session.GetInt64() != generation)'
        New = '                session.ValueKind != JsonValueKind.Number)'
        Expect = 'ReadingsOfAnEarlierSessionNeverDecideASkip'
    },
    @{
        Id = 'R1-empty-scope-all-empty-not-on-readings'
        File = 'src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs'
        Old = '            RequiredString(payload, "overallOutcome") != "ALL_EMPTY" ||
            payload.GetProperty("slotResults").GetArrayLength() != 0)'
        New = '            true)'
        Expect = 'ADuringLoadCancellationReportedAllEmptyReconcilesWithOrWithoutSlotResults'
    },
    @{
        Id = 'R2-empty-scope-slot-states-unchecked'
        File = 'src/ControlServer.Host/Transport/OnboardRecoveryCoordinator.cs'
        Old = '                                   reading.Physical == "EMPTY" && reading.Lock == "LOCKED" && reading.Output == "RESET");'
        New = '                                   reading is not null);'
        Expect = 'occupied|door-unlocked|output-active'
    },
    @{
        Id = 'R3-stale-skip-not-asked-again'
        File = 'src/ControlServer.Host/Transport/OnboardMessageProcessor.cs'
        Old = '        if ((state.SafetyBaselineUntrusted || state.RemainingBasketsSkipAwaitingReadings) &&'
        New = '        if (state.SafetyBaselineUntrusted &&'
        Expect = 'AnUnansweredSnapshotRequestIsAskedAgainAfterTheReaskInterval'
    }
)

New-Item -ItemType Directory -Force $OutDir | Out-Null
Push-Location $Worktree
try {
    if (git status --porcelain) { throw 'The worktree is not clean; commit before mutating.' }
    foreach ($m in $mutations) {
        if ($Only.Count -gt 0 -and $m.Id -notin $Only) { continue }
        $path = Join-Path $Worktree $m.File
        $text = [IO.File]::ReadAllText($path)
        $count = ([regex]::Matches($text, [regex]::Escape($m.Old))).Count
        if ($count -ne 1) { throw "$($m.Id): expected one match in $($m.File), found $count." }
        [IO.File]::WriteAllText($path, $text.Replace($m.Old, $m.New))
        $changed = @(git diff --name-only)
        if ($changed.Count -ne 1 -or $changed[0] -ne $m.File) { throw "$($m.Id): changed $($changed -join ', ')." }
        $log = Join-Path $OutDir "$($m.Id).txt"
        try {
            dotnet test tests/ControlServer.Tests --filter 'FullyQualifiedName~RemainingBasketsSkipTests' *> $log
            $exit = $LASTEXITCODE
        } finally {
            git checkout HEAD -- $m.File
        }
        $summary = (Select-String -Path $log -Pattern 'Failed!|Passed!|error CS' | Select-Object -Last 1)?.Line
        $failed = @(Select-String -Path $log -Pattern '^\s+Failed ControlServer' | ForEach-Object { $_.Line.Trim() })
        $schema = [bool](Select-String -Path $log -Pattern 'Test Assembly Cleanup Failure' -Quiet)
        $hit = @($failed | Where-Object { $_ -match $m.Expect })
        $red = ($exit -ne 0) -and (($hit.Count -gt 0) -or ($m.Expect -eq 'schema-conformance' -and $schema))
        "$($m.Id): exit=$exit red=$red expected~'$($m.Expect)' schemaCleanupFailure=$schema | $summary" |
            Tee-Object -FilePath (Join-Path $OutDir 'SUMMARY.txt') -Append
    }
} finally {
    Pop-Location
}
