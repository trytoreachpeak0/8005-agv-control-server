using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

public sealed class WireToGateStoreTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("ProtocolVector", "CV-SESSION-RECONNECT-DURING-RECOVERY")]
    [Trait("ProtocolVector", "CV-SESSION-RECOVERY-HAPPY")]
    public async Task FiveStepRecoveryRequiresCurrentGenerationAndUniqueConsistentFacts()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        SessionIdentity identity = new(
            "AGV-001", 1, ProtocolCandidateIdentity.RepositoryCommit,
            ProtocolCandidateIdentity.ManifestSha256, ProtocolCandidateIdentity.ProfileId,
            ProtocolCandidateIdentity.ProtocolVersion);

        await fixture.Store.BeginSessionRecoveryAsync(identity, fixture.CancellationToken);
        await fixture.Store.ApplyCapabilitySnapshotAsync("AGV-001", 1, 4, "cap-hash", fixture.CancellationToken);
        await fixture.Store.ApplySafetySnapshotAsync("AGV-001", 1, 9, true, "safe-hash", fixture.CancellationToken);
        await fixture.Store.ApplyRecoveryReportAsync(
            "AGV-001", 1, "REPORT-001", 0, null, null, [], [], [], fixture.CancellationToken);
        SessionReadinessDecision decision = await fixture.Store.DecideReadinessAsync(
            "AGV-001", 1, fixture.CancellationToken);

        Assert.Equal(SessionReadiness.Ready, decision.Readiness);
        await Assert.ThrowsAsync<StaleSessionGenerationException>(() =>
            fixture.Store.ApplyCapabilitySnapshotAsync("AGV-001", 0, 5, "late", fixture.CancellationToken));

        await fixture.Store.BeginSessionRecoveryAsync(identity with { SessionGeneration = 2 }, fixture.CancellationToken);
        SessionReadinessDecision reconnectDecision = await fixture.Store.GetReadinessAsync(
            "AGV-001", fixture.CancellationToken);
        Assert.Equal(SessionReadiness.RecoveryRequired, reconnectDecision.Readiness);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task SessionStaysUsableWhileOurOwnSlotOperationHoldsASlotUnlocked()
    {
        // Carrying out a SlotOperationCommand means unlocking a slot, and an unlocked slot is
        // correctly reported as unsafe to depart. Failing the session on that made the command
        // destroy its own precondition: the peer refused to continue the operation this server
        // had just asked for, and the journey could never leave the load stage.
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await ReachReadyAsync(fixture);
        await fixture.Store.PrepareSlotOperationAsync(
            new StationOperationPlan(
                "ATTEMPT-401", "D-401", "SUBLOT-401", [1], SlotOperationType.Load, 0, "plan-hash", fixture.Now),
            "MSG-CMD-401",
            "command-json",
            fixture.CancellationToken);

        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 10, false, "unlocked-hash", fixture.CancellationToken,
            ["LOCK_NOT_CLOSED", "UNLOCK_OUTPUT_NOT_RESET"], unknownPresent: false);
        SessionReadinessDecision duringOperation = await fixture.Store.DecideReadinessAsync(
            "AGV-001", 1, fixture.CancellationToken);

        Assert.Equal(SessionReadiness.Ready, duringOperation.Readiness);
        Assert.Equal("READY", duringOperation.ReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task UnsafetyOurOwnOperationDoesNotExplainStillFailsTheSessionClosed()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await ReachReadyAsync(fixture);
        await fixture.Store.PrepareSlotOperationAsync(
            new StationOperationPlan(
                "ATTEMPT-402", "D-402", "SUBLOT-402", [1], SlotOperationType.Load, 0, "plan-hash", fixture.Now),
            "MSG-CMD-402",
            "command-json",
            fixture.CancellationToken);

        // One reason outside the operation-induced set is enough to fail closed.
        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 11, false, "moving-hash", fixture.CancellationToken,
            ["LOCK_NOT_CLOSED", "VEHICLE_STATE_UNKNOWN"], unknownPresent: false);
        Assert.Equal(
            SessionReadiness.RecoveryRequired,
            (await fixture.Store.DecideReadinessAsync("AGV-001", 1, fixture.CancellationToken)).Readiness);

        // Incomplete evidence is never treated as explained.
        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 12, false, "unknown-hash", fixture.CancellationToken,
            ["LOCK_NOT_CLOSED"], unknownPresent: true);
        Assert.Equal(
            SessionReadiness.RecoveryRequired,
            (await fixture.Store.DecideReadinessAsync("AGV-001", 1, fixture.CancellationToken)).Readiness);

        // Neither is a peer that reports no reason at all, nor a legacy row with no reasons stored.
        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 13, false, "silent-hash", fixture.CancellationToken, [], unknownPresent: false);
        Assert.Equal(
            SessionReadiness.RecoveryRequired,
            (await fixture.Store.DecideReadinessAsync("AGV-001", 1, fixture.CancellationToken)).Readiness);
        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 14, false, "legacy-hash", fixture.CancellationToken);
        SessionReadinessDecision legacy = await fixture.Store.DecideReadinessAsync(
            "AGV-001", 1, fixture.CancellationToken);
        Assert.Equal(SessionReadiness.RecoveryRequired, legacy.Readiness);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", legacy.ReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task AnUnlockedSlotWithNoOperationOfOursInFlightFailsTheSessionClosed()
    {
        // An idle vehicle standing with a locker open is exactly what this gate is for. Nothing
        // this server commanded explains it, so it must still fail closed.
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await ReachReadyAsync(fixture);

        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 15, false, "idle-open-hash", fixture.CancellationToken,
            ["LOCK_NOT_CLOSED"], unknownPresent: false);
        SessionReadinessDecision decision = await fixture.Store.DecideReadinessAsync(
            "AGV-001", 1, fixture.CancellationToken);

        Assert.Equal(SessionReadiness.RecoveryRequired, decision.Readiness);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", decision.ReasonCode);
    }

    private static async Task ReachReadyAsync(StoreFixture fixture)
    {
        SessionIdentity identity = new(
            "AGV-001", 1, ProtocolCandidateIdentity.RepositoryCommit,
            ProtocolCandidateIdentity.ManifestSha256, ProtocolCandidateIdentity.ProfileId,
            ProtocolCandidateIdentity.ProtocolVersion);
        await fixture.Store.BeginSessionRecoveryAsync(identity, fixture.CancellationToken);
        await fixture.Store.ApplyCapabilitySnapshotAsync("AGV-001", 1, 4, "cap-hash", fixture.CancellationToken);
        await fixture.Store.ApplySafetySnapshotAsync(
            "AGV-001", 1, 9, true, "safe-hash", fixture.CancellationToken, [], unknownPresent: false);
        await fixture.Store.ApplyRecoveryReportAsync(
            "AGV-001", 1, "REPORT-001", 0, null, null, [], [], [], fixture.CancellationToken);
        Assert.Equal(
            SessionReadiness.Ready,
            (await fixture.Store.DecideReadinessAsync("AGV-001", 1, fixture.CancellationToken)).Readiness);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task SlotPlanAndReliableCommandAreAtomicAndBatchNeedsCompleteSafeEvidence()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        StationOperationPlan plan = new(
            "ATTEMPT-001", "D-001", "SUBLOT-001", [1, 2], SlotOperationType.Load, 0, "plan-hash", fixture.Now);

        await fixture.Store.PrepareSlotOperationAsync(plan, "MSG-CMD-001", "command-json", fixture.CancellationToken);

        Assert.Equal(1, await fixture.Context.StationOperations.CountAsync(fixture.CancellationToken));
        Assert.Equal(1, await fixture.Context.ProtocolOutbox.CountAsync(fixture.CancellationToken));
        await Assert.ThrowsAsync<UnsafePhysicalEvidenceException>(() => fixture.Store.CommitSlotBatchAsync(
            "ATTEMPT-001",
            [new SlotPhysicalEvidence(1, SlotBusinessState.Occupied, true, true)],
            fixture.Now.AddSeconds(1), fixture.CancellationToken));

        await fixture.Store.CommitSlotBatchAsync(
            "ATTEMPT-001",
            [
                new SlotPhysicalEvidence(1, SlotBusinessState.Occupied, true, true),
                new SlotPhysicalEvidence(2, SlotBusinessState.Occupied, true, true)
            ],
            fixture.Now.AddSeconds(2), fixture.CancellationToken);

        StationOperationRow row = await fixture.Context.StationOperations.SingleAsync(fixture.CancellationToken);
        Assert.Equal(StationOperationStatus.Committed, row.Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
    public async Task UnsafeCompletedOperationResultIsDurableButMovesDemandToRecovery()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await fixture.Store.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot(
                "D-001",
                "SUBLOT-001|WIRE_TO_GATE",
                7,
                "history-1",
                21,
                fixture.Now),
            new OrderIntent(
                "LEG-001",
                "D-001",
                "W2G-D-001-PICKUP-1",
                "TO_PICKUP",
                "ST-PICKUP",
                fixture.Now),
            fixture.CancellationToken);
        await fixture.Store.PrepareSlotOperationAsync(
            new StationOperationPlan(
                "ATTEMPT-001",
                "D-001",
                "SUBLOT-001",
                [1],
                SlotOperationType.Load,
                0,
                "plan-hash",
                fixture.Now),
            "MSG-CMD-001",
            "command-json",
            fixture.CancellationToken);

        OperationResultDisposition disposition = await fixture.Store.ApplyOperationResultAsync(
            new StationOperationResult(
                "RESULT-001",
                "ATTEMPT-001",
                "D-001",
                SlotOperationType.Load,
                "COMPLETED",
                [new SlotPhysicalEvidence(1, SlotBusinessState.Occupied, false, true)],
                true,
                fixture.Now.AddSeconds(1),
                "result-hash",
                "wire-hash"),
            "AGV-001",
            0,
            fixture.CancellationToken);

        Assert.Equal(OperationResultDisposition.RecoveryRequired, disposition);
        Assert.Equal(
            StationOperationStatus.RecoveryRequired,
            (await fixture.Context.StationOperations.SingleAsync(fixture.CancellationToken)).Status);
        Assert.Equal(
            DemandExecutionStatus.RecoveryRequired,
            (await fixture.Context.AcceptedDemands.SingleAsync(fixture.CancellationToken)).Status);
        Assert.False((await fixture.Context.OperationResults.SingleAsync(fixture.CancellationToken)).HistoricalOnly);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("ProtocolVector", "CV-PREDEPARTURE-SAFETY-EXPIRES")]
    public async Task GateMovementRequiresFreshSafeCheckAndStableIntentIdentity()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        SafetyCheckObservation fresh = new("CHECK-001", 12, true, fixture.Now, fixture.Now.AddSeconds(10));
        OrderIntent gateIntent = new("LEG-GATE-001", "D-001", "W2G-D-001-GATE-1", "TO_GATE", "ST-GATE", fixture.Now);

        await fixture.Store.AuthorizeMovementAsync(gateIntent, fresh, fixture.Now.AddSeconds(5), fixture.CancellationToken);
        Assert.Equal(1, await fixture.Context.OrderIntents.CountAsync(fixture.CancellationToken));

        SafetyCheckObservation expired = fresh with { CheckId = "CHECK-002", ValidUntil = fixture.Now.AddSeconds(3) };
        await Assert.ThrowsAsync<UnsafeMovementAuthorizationException>(() => fixture.Store.AuthorizeMovementAsync(
            gateIntent with { MovementLegId = "LEG-GATE-002", UpperId = "W2G-D-001-GATE-2" },
            expired, fixture.Now.AddSeconds(4), fixture.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-04")]
    [Trait("ProtocolVector", "CV-DESTINATION-UNLOAD-ALL-EMPTY")]
    public async Task UnloadCompletionCommitsAllFourFactsExactlyOnce()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await fixture.Store.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot("D-001", "SUBLOT-001|WIRE_TO_GATE", 7, "history-1", 21, fixture.Now),
            new OrderIntent("LEG-001", "D-001", "W2G-D-001-PICKUP-1", "TO_PICKUP", "ST-PICKUP", fixture.Now),
            fixture.CancellationToken);

        SlotPhysicalEvidence[] allEmpty =
        [
            new(1, SlotBusinessState.Empty, true, true),
            new(2, SlotBusinessState.Empty, true, true)
        ];
        await fixture.Store.CompleteDemandAfterUnloadAsync(
            "UNLOAD-001", "D-001", "SUBLOT-001|WIRE_TO_GATE", 7, allEmpty,
            "all-empty-locked-output-reset", fixture.Now.AddMinutes(1), fixture.CancellationToken);
        await fixture.Store.CompleteDemandAfterUnloadAsync(
            "UNLOAD-001", "D-001", "SUBLOT-001|WIRE_TO_GATE", 7, allEmpty,
            "all-empty-locked-output-reset", fixture.Now.AddMinutes(1), fixture.CancellationToken);

        Assert.Equal(1, await fixture.Context.UnloadBatches.CountAsync(fixture.CancellationToken));
        Assert.Equal(1, await fixture.Context.StopClosures.CountAsync(fixture.CancellationToken));
        Assert.Equal(1, await fixture.Context.TransportDemandCompletions.CountAsync(fixture.CancellationToken));
        Assert.Equal(DemandExecutionStatus.Succeeded,
            (await fixture.Context.AcceptedDemands.SingleAsync(fixture.CancellationToken)).Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("ProtocolVector", "CV-CONNECTION-LOSS-SAFE-FINISH")]
    public async Task ConnectionLossCanFinishButNeverExpandActiveUnlockSet()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await fixture.Store.RecordConnectionLossAsync("AGV-001", 4, [2], fixture.Now, fixture.CancellationToken);

        await Assert.ThrowsAsync<ActiveUnlockSetExpansionException>(() => fixture.Store.RecordSafeFinishAsync(
            "AGV-001", 4, [2, 3], fixture.Now.AddSeconds(1), fixture.CancellationToken));
        await fixture.Store.RecordSafeFinishAsync(
            "AGV-001", 4, [2], fixture.Now.AddSeconds(2), fixture.CancellationToken);

        ConnectionRecoveryRow row = await fixture.Context.ConnectionRecoveries.SingleAsync(fixture.CancellationToken);
        Assert.Equal(ConnectionRecoveryStatus.AwaitingHandshake, row.Status);
        Assert.False(row.ResumeAuthorized);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-SAME-CONTENT")]
    [Trait("ProtocolVector", "CV-REQUEST-FIRST-RESULT-REPLAY")]
    public async Task InboxReplaysFirstResponseAndRejectsDifferentContent()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        int sideEffects = 0;
        string first = await fixture.Store.CaptureFirstResponseAsync(
            "MSG-001", "same-hash", () => Task.FromResult($"result-{++sideEffects}"),
            fixture.Now, fixture.CancellationToken);
        string replay = await fixture.Store.CaptureFirstResponseAsync(
            "MSG-001", "same-hash", () => Task.FromResult($"result-{++sideEffects}"),
            fixture.Now.AddSeconds(1), fixture.CancellationToken);

        Assert.Equal("result-1", first);
        Assert.Equal(first, replay);
        Assert.Equal(1, sideEffects);
        await Assert.ThrowsAsync<ProtocolContentConflictException>(() => fixture.Store.CaptureFirstResponseAsync(
            "MSG-001", "different-hash", () => Task.FromResult("must-not-run"),
            fixture.Now.AddSeconds(2), fixture.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ForcedRecoveryGenerationFencesLateResultsAndPersistsDecision()
    {
        await using StoreFixture fixture = await StoreFixture.CreateAsync();
        await fixture.Store.AdvanceForcedRecoveryGenerationAsync("AGV-001", 2, fixture.Now, fixture.CancellationToken);

        OperationResultDisposition late = await fixture.Store.RecordOperationResultAsync(
            "RESULT-LATE", "ATTEMPT-001", "AGV-001", 1, "late-hash", fixture.Now.AddSeconds(1), fixture.CancellationToken);
        OperationResultDisposition current = await fixture.Store.RecordOperationResultAsync(
            "RESULT-CURRENT", "ATTEMPT-001", "AGV-001", 2, "current-hash", fixture.Now.AddSeconds(2), fixture.CancellationToken);
        string first = await fixture.Store.RecordRecoveryDecisionAsync(
            "RECOVERY-SESSION-001", "ACTION-001", 2, "decision-hash", "RESUME",
            fixture.Now.AddSeconds(3), fixture.CancellationToken);
        string replay = await fixture.Store.RecordRecoveryDecisionAsync(
            "RECOVERY-SESSION-001", "ACTION-001", 2, "decision-hash", "COMPENSATE",
            fixture.Now.AddSeconds(4), fixture.CancellationToken);

        Assert.Equal(OperationResultDisposition.HistoricalOnly, late);
        Assert.Equal(OperationResultDisposition.Accepted, current);
        Assert.Equal("RESUME", first);
        Assert.Equal(first, replay);
        Assert.Equal(1, await fixture.Context.RecoveryDecisions.CountAsync(fixture.CancellationToken));
    }

    private sealed class StoreFixture : IAsyncDisposable
    {
        private StoreFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            CancellationToken cancellationToken)
        {
            Connection = connection;
            Context = context;
            Store = new WireToGateStore(context);
            CancellationToken = cancellationToken;
        }

        public SqliteConnection Connection { get; }
        public ControlServerDbContext Context { get; }
        public WireToGateStore Store { get; }
        public DateTimeOffset Now { get; } = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
        public CancellationToken CancellationToken { get; }

        public static async Task<StoreFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .Options;
            ControlServerDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new StoreFixture(connection, context, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
