using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// control-server#556: the moment a forced mechanical recovery's result has been taken in and the vehicle has not
/// reconnected. The vehicle raises its forced generation only by binding the ForcedMechanicalRecoveryCommand, and the result
/// carries the generation it bound, so a current result is the vehicle reporting that generation. Until #556 only the
/// handshake's RecoveryStateReport counted, and the vehicle stayed on FORCED_RECOVERY_GENERATION_MISMATCH -- every action
/// refused FORCED_RECOVERY_GENERATION_STALE, a hardware record taken but lifting nothing -- until something made it reconnect.
/// </summary>
public sealed partial class RecoveryStateMachineG2Tests
{
    private const string ForcedResultProofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_FORCED_BEFORE_RECONNECT";
    private const string ForcedResultProof = "forced-before-reconnect-proof-not-a-production-secret";

    /// <summary>
    /// The result arrives and nothing else does. What the vehicle waits for next is its hardware recovery record, and that is
    /// the reason it is held under; the record, taken over the same connection, makes it Ready and says so on the wire.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedResultWithoutAReconnectHoldsTheVehicleForItsHardwareRecordAndTheRecordMakesItReady()
    {
        Environment.SetEnvironmentVariable(ForcedResultProofVariable, ForcedResultProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), ForcedResultProofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(ForcedResultProof), state, token);
            Assert.Equal("RecoveryActionAccepted", MessageType(
                await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token)));

            Assert.Equal("DurableAck", MessageType(
                await processor.ProcessAsync(MechanicallyIsolatedResult(generation: 1), state, token)));

            SessionRecoveryRow held = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
            Assert.Equal(
                (1L, 1L, SessionReadiness.RecoveryRequired, WireToGateStore.ForcedRecoveryHardwareRecoveryRequired),
                (held.ForcedRecoveryGeneration, held.ReportedForcedRecoveryGeneration, held.Readiness, held.ReasonCode));

            string recorded = await processor.ProcessAsync(
                HardwareRecoveryRecord("e1000000-0000-4000-8000-000000005561", slots: RecoverySlots), state, token);

            Assert.Equal("RECORDED", FirstPayload(recorded).GetProperty("outcome").GetString());
            string[] lines = recorded.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Equal("SessionReadiness", MessageType(lines[1]));
            Assert.Equal("READY", FirstPayload(lines[1]).GetProperty("readiness").GetString());
            SessionRecoveryRow ready = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
            Assert.Equal((SessionReadiness.Ready, "READY"), (ready.Readiness, ready.ReasonCode));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ForcedResultProofVariable, null);
        }
    }

    /// <summary>
    /// A FAILED forced result does not stand for the vehicle's report (control-server#556 review). The onboard sends one when it
    /// could not bind the command at all (8005-agv-onboard-hmi <c>AnswerUnbindableCommandAsync</c>): it copies the command's
    /// generation without binding it, so the vehicle still holds the generation below and nothing was carried out on it.
    /// Without a reconnect the vehicle stays on FORCED_RECOVERY_GENERATION_MISMATCH, and the fence still refuses the next
    /// session's action FORCED_RECOVERY_GENERATION_STALE.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AFailedForcedResultDoesNotStandForTheVehiclesReport()
    {
        Environment.SetEnvironmentVariable(ForcedResultProofVariable, ForcedResultProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), ForcedResultProofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(ForcedResultProof), state, token);
            await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);

            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(
                MechanicallyIsolatedResult(generation: 1, outcome: "FAILED"), state, token)));

            SessionRecoveryRow held = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
            Assert.Equal(
                (1L, 0L, "FORCED_RECOVERY_GENERATION_MISMATCH"),
                (held.ForcedRecoveryGeneration, held.ReportedForcedRecoveryGeneration, held.ReasonCode));

            string nextSessionId = StableGuid("41000000-0000-4000-8000-000000000169", "exception-recovery-session");
            Assert.Equal("ExceptionRecoverySessionOpened",
                MessageType(await processor.ProcessAsync(NextSessionRequest(ForcedResultProof), state, token)));
            JsonNode compensate = JsonNode.Parse(RecoveryAction(
                "COMPENSATE_LOAD_ALL_EMPTY", messageId: "e0000000-0000-4000-8000-000000005562",
                actionId: "51000000-0000-4000-8000-000000005562"))!;
            compensate["payload"]!["exceptionRecoverySessionId"] = nextSessionId;
            string refused = await processor.ProcessAsync(compensate.ToJsonString(), state, token);
            Assert.Equal(("RecoveryActionRejected", ServerReasonCodes.ForcedRecoveryGenerationStale),
                (MessageType(refused), FirstPayload(refused).GetProperty("problem").GetProperty("reasonCode").GetString()));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ForcedResultProofVariable, null);
        }
    }

    /// <summary>
    /// The result resent with the same messageId is answered as before and moves nothing a second time; the vehicle then
    /// reconnecting and reporting the generation the result gave changes neither the generation nor the reason.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedResultResentAndThenTheReconnectsReportAgreeWithWhatTheResultAlreadyGave()
    {
        Environment.SetEnvironmentVariable(ForcedResultProofVariable, ForcedResultProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), ForcedResultProofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(ForcedResultProof), state, token);
            await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);
            string first = await processor.ProcessAsync(MechanicallyIsolatedResult(generation: 1), state, token);

            string again = await processor.ProcessAsync(MechanicallyIsolatedResult(generation: 1), state, token);

            Assert.Equal(first.Split('\n')[0], again.Split('\n')[0]);
            Assert.Single(await context.RecoveryResultEvidence.AsNoTracking().ToArrayAsync(token));
            Assert.Equal(1, (await context.VehicleRecoveryGenerations.AsNoTracking().SingleAsync(token)).ForcedRecoveryGeneration);
            SessionRecoveryRow held = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
            Assert.Equal((1L, WireToGateStore.ForcedRecoveryHardwareRecoveryRequired),
                (held.ReportedForcedRecoveryGeneration, held.ReasonCode));

            await store.ApplyRecoveryReportAsync(
                AgvId, 3, "f0000000-0000-4000-8000-000000005563", forcedRecoveryGeneration: 1,
                null, "NONE", [], [], [], token);
            SessionReadinessDecision decision = await store.DecideReadinessAsync(AgvId, 3, token);

            Assert.Equal(WireToGateStore.ForcedRecoveryHardwareRecoveryRequired, decision.ReasonCode);
            SessionRecoveryRow reported = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
            Assert.Equal((1L, 1L), (reported.ForcedRecoveryGeneration, reported.ReportedForcedRecoveryGeneration));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ForcedResultProofVariable, null);
        }
    }

    /// <summary>
    /// Not past the fence (control-server#556, against fixing too much). An administrator closed a forced recovery the vehicle
    /// never bound, the vehicle came back reporting the generation below, and a new forced recovery advanced the generation
    /// again. A result of the closed one arriving now is of an older generation: it is history, and the vehicle has still not
    /// reported the server's generation -- the reason stays FORCED_RECOVERY_GENERATION_MISMATCH, so the fence still refuses.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedResultOfAnOlderGenerationDoesNotStandForTheVehiclesReport()
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            (OnboardMessageProcessor processor, OnboardConnectionState state, string? closedResult) =
                await StuckOtherActionAsync(context, peer, Forced);
            WireToGateStore store = new(context);
            Assert.True((await CloseAsync(context, peer, CloseRequest(sessionId: null))).Closed);
            await store.ApplyRecoveryReportAsync(
                AgvId, 3, "f0000000-0000-4000-8000-000000005564", forcedRecoveryGeneration: 0, null, "NONE", [], [], [], token);
            context.ChangeTracker.Clear();
            const string nextRequestId = "41000000-0000-4000-8000-000000005564";
            string nextSessionId = StableGuid(nextRequestId, "exception-recovery-session");
            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(await processor.ProcessAsync(
                PressedAgain("e0000000-0000-4000-8000-000000005564", nextRequestId), state, token)));
            Assert.Equal("RecoveryActionAccepted", MessageType(await processor.ProcessAsync(
                InSessionAction(Forced, "e0000000-0000-4000-8000-000000005565", "51000000-0000-4000-8000-000000005565",
                    nextSessionId), state, token)));
            await processor.FlushDeferredOutboundAsync(state, token);
            context.ChangeTracker.Clear();

            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(closedResult!, state, token)));
            await processor.FlushDeferredOutboundAsync(state, token);
            context.ChangeTracker.Clear();

            Assert.True((await context.RecoveryResultEvidence.AsNoTracking().SingleAsync(token)).HistoricalOnly);
            SessionRecoveryRow session = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
            Assert.Equal((2L, 0L), (session.ForcedRecoveryGeneration, session.ReportedForcedRecoveryGeneration));
            Assert.Equal("FORCED_RECOVERY_GENERATION_MISMATCH", (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);

            // And the fence still refuses: the second forced recovery's outcome is still awaited, so nothing lifts it.
            string refused = await processor.ProcessAsync(
                InSessionAction(Forced, "e0000000-0000-4000-8000-000000005566", "51000000-0000-4000-8000-000000005566",
                    nextSessionId), state, token);
            await processor.FlushDeferredOutboundAsync(state, token);
            context.ChangeTracker.Clear();
            Assert.Equal(("RecoveryActionRejected", ServerReasonCodes.ForcedRecoveryGenerationStale),
                (MessageType(refused), FirstPayload(refused).GetProperty("problem").GetProperty("reasonCode").GetString()));
            Assert.Equal(2, await GenerationOfAsync(context));
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    /// <summary>
    /// Only ever raised. A vehicle that reported a generation ahead of the server's -- its journal from another server, say --
    /// keeps its own word: a current result does not pull the report down to agree, so the two still disagree.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedResultNeverLowersAGenerationTheVehicleReported()
    {
        Environment.SetEnvironmentVariable(ForcedResultProofVariable, ForcedResultProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), ForcedResultProofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(ForcedResultProof), state, token);
            await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);
            await store.ApplyRecoveryReportAsync(
                AgvId, 3, "f0000000-0000-4000-8000-000000005567", forcedRecoveryGeneration: 5,
                null, "NONE", [], [], [], token);
            context.ChangeTracker.Clear();

            Assert.Equal("DurableAck", MessageType(
                await processor.ProcessAsync(MechanicallyIsolatedResult(generation: 1), state, token)));

            SessionRecoveryRow session = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
            Assert.Equal((1L, 5L, "FORCED_RECOVERY_GENERATION_MISMATCH"),
                (session.ForcedRecoveryGeneration, session.ReportedForcedRecoveryGeneration, session.ReasonCode));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ForcedResultProofVariable, null);
        }
    }

    /// <summary>
    /// Only for the connection the result came on. A result reaching the coordinator under an earlier session generation is
    /// recorded and settles its business as before, but says nothing about what the vehicle holds on the current connection,
    /// whose handshake is what tells. Reached here through the coordinator directly: the processor refuses a line of another
    /// session generation before it gets this far, so this is the coordinator's own contract.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedResultOfAnEarlierConnectionDoesNotStandForTheCurrentOnesReport()
    {
        Environment.SetEnvironmentVariable(ForcedResultProofVariable, ForcedResultProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), ForcedResultProofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(ForcedResultProof), state, token);
            await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);
            context.ChangeTracker.Clear();
            OnboardRecoveryCoordinator coordinator = TestOnboardProcessorFactory.CreateRecoveryCoordinator(
                context, store, new FixedTimeProvider(Now), Configuration(ForcedResultProofVariable));
            JsonNode earlier = JsonNode.Parse(MechanicallyIsolatedResult(generation: 1))!;
            earlier["sessionGeneration"] = 2;
            using JsonDocument line = JsonDocument.Parse(earlier.ToJsonString());

            Assert.Equal("DurableAck", MessageType(
                await coordinator.ProcessResultAsync(line.RootElement, new string('c', 64), token)));

            Assert.Equal(RecoveryWorkflowState.Reconciled,
                (await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).State);
            SessionRecoveryRow session = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
            Assert.Equal((1L, 0L), (session.ForcedRecoveryGeneration, session.ReportedForcedRecoveryGeneration));
            Assert.Equal("FORCED_RECOVERY_GENERATION_MISMATCH", (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ForcedResultProofVariable, null);
        }
    }

    /// <summary>
    /// Nor for a forced recovery an administrator closed. Its result can still arrive at the current generation -- nothing
    /// forced it again -- but the closing settled the workflow, and a result that settles nothing stands for nothing either:
    /// the vehicle's own report, at its next handshake, is what tells the server it holds the generation.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedResultOfAWorkflowAnAdministratorClosedDoesNotStandForTheVehiclesReport()
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            (OnboardMessageProcessor processor, OnboardConnectionState state, string? closedResult) =
                await StuckOtherActionAsync(context, peer, Forced);
            WireToGateStore store = new(context);
            Assert.True((await CloseAsync(context, peer, CloseRequest(sessionId: null))).Closed);
            context.ChangeTracker.Clear();

            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(closedResult!, state, token)));
            await processor.FlushDeferredOutboundAsync(state, token);
            context.ChangeTracker.Clear();

            SessionRecoveryRow session = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
            Assert.Equal((1L, 0L), (session.ForcedRecoveryGeneration, session.ReportedForcedRecoveryGeneration));
            Assert.Equal("FORCED_RECOVERY_GENERATION_MISMATCH", (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }
    /// <summary>
    /// Only a forced result (control-server#556 review, R3). A load cancellation's workflow is issued at the vehicle's current
    /// forced generation too (UpsertSimpleWorkflowAsync), so its result carries that generation; it says nothing about the
    /// vehicle having bound a forced command, and leaves what the vehicle reported where it was.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ALoadCancellationResultAtTheCurrentGenerationDoesNotStandForTheVehiclesReport()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string cancellationId = "b1000000-0000-4000-8000-000000005568";
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCancellableLoadAsync(context);
        WireToGateStore store = new(context);
        await store.AdvanceForcedRecoveryGenerationAsync(AgvId, 1, Now, token);
        await context.SaveChangesAsync(token);
        context.ChangeTracker.Clear();
        OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), CancellationProofVariable);
        OnboardConnectionState state = CurrentState();
        Assert.Equal("AUTHORIZED", FirstPayload(
            await processor.ProcessAsync(CancellationRequest(cancellationId), state, token)).GetProperty("decision").GetString());
        Assert.Equal(1, (await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).ForcedRecoveryGeneration);

        Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(
            CancellationResult(cancellationId, "b1000000-0000-4000-8000-000000005569", "EMPTY"), state, token)));

        Assert.Equal(RecoveryWorkflowState.Reconciled, (await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).State);
        SessionRecoveryRow session = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
        Assert.Equal((1L, 0L), (session.ForcedRecoveryGeneration, session.ReportedForcedRecoveryGeneration));
        Assert.Equal("FORCED_RECOVERY_GENERATION_MISMATCH", (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);
    }

    /// <summary>
    /// Only the generation the server issued (control-server#556 review, R4). A forced result claiming a generation above its
    /// workflow's and the server's is not history -- nothing newer exists -- but no command of that generation was ever sent,
    /// so it stands for nothing: what the vehicle reported stays where it was.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedResultClaimingAGenerationTheServerNeverIssuedDoesNotStandForTheVehiclesReport()
    {
        Environment.SetEnvironmentVariable(ForcedResultProofVariable, ForcedResultProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), ForcedResultProofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(ForcedResultProof), state, token);
            await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);

            Assert.Equal("DurableAck", MessageType(
                await processor.ProcessAsync(MechanicallyIsolatedResult(generation: 2), state, token)));

            Assert.False((await context.RecoveryResultEvidence.AsNoTracking().SingleAsync(token)).HistoricalOnly);
            SessionRecoveryRow session = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
            Assert.Equal((1L, 0L), (session.ForcedRecoveryGeneration, session.ReportedForcedRecoveryGeneration));
            Assert.Equal("FORCED_RECOVERY_GENERATION_MISMATCH", (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ForcedResultProofVariable, null);
        }
    }
}
