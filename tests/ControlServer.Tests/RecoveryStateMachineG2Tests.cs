using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

public sealed class RecoveryStateMachineG2Tests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly int[] RecoverySlots = [1, 2];
    private static readonly string[] UnknownReasonCodes = ["PHYSICAL_STATE_UNKNOWN"];
    private static readonly string[] FailedSlotReasonCodes = ["ACTION_NOT_ALLOWED_IN_STATE"];
    private static readonly string[] ExpectedRecoveryCommandReplay =
        ["LoadCorrectionCommand", "FaultCargoRecoveryCommand", "LoadCorrectionCommand"];
    private static readonly string[] ExpectedResumeSends =
        ["ExceptionRecoverySessionSnapshot", "SlotOperationResumeCommand", "ExceptionRecoverySessionSnapshot"];
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
    private const string AgvId = "AGV-8005-01";
    private const string DemandId = "10000000-0000-4000-8000-000000000001";
    private const string JourneyId = "j0000000-0000-4000-8000-000000000001";
    private const string AttemptId = "20000000-0000-4000-8000-000000000001";
    private const string EventId = "30000000-0000-4000-8000-000000000001";
    private const string RequestId = "40000000-0000-4000-8000-000000000001";
    private const string ActionId = "50000000-0000-4000-8000-000000000001";
    private const string OperatorId = "maintenance-001";

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task ResumeAuthorizationPersistsFormalCommandBeforeSendAndReplaysSameIdentityAfterRestart()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_RESUME";
        const string proof = "resume-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            string sessionRequest = RecoverySessionRequest(proof);
            string actionRequest = RecoveryAction("RESUME_AFTER_REPAIR");
            string firstResponse;
            string firstCommand;
            await using (ControlServerDbContext firstContext = await CreateContextAsync(connection))
            {
                await SeedBlockedJourneyAsync(firstContext);
                RecordingPeer firstPeer = new(firstContext);
                OnboardMessageProcessor firstProcessor = Processor(
                    firstContext, firstPeer, proofVariable);
                OnboardConnectionState state = CurrentState(deferOutbound: true);

                string opened = await firstProcessor.ProcessAsync(
                    sessionRequest, state, TestContext.Current.CancellationToken);
                Assert.Empty(firstPeer.Lines);
                await firstProcessor.FlushDeferredOutboundAsync(
                    state, TestContext.Current.CancellationToken);
                firstResponse = await firstProcessor.ProcessAsync(
                    actionRequest, state, TestContext.Current.CancellationToken);
                Assert.Single(firstPeer.Lines);
                await firstProcessor.FlushDeferredOutboundAsync(
                    state, TestContext.Current.CancellationToken);

                Assert.Equal("ExceptionRecoverySessionOpened", MessageType(opened));
                Assert.Equal("RecoveryActionAccepted", MessageType(firstResponse));
                Assert.Equal(ExpectedResumeSends, firstPeer.Lines.Select(MessageType).ToArray());
                firstCommand = firstPeer.Lines.Single(line => MessageType(line) == "SlotOperationResumeCommand");
                Assert.Equal("SlotOperationResumeCommand", MessageType(firstCommand));
                ProtocolOutboxRow outbox = await firstContext.ProtocolOutbox.SingleAsync(
                    row => row.MessageType == "SlotOperationResumeCommand",
                    TestContext.Current.CancellationToken);
                Assert.Equal(outbox.PayloadJson + "\n", firstCommand);
                ProtocolOutboxRow recoverySnapshot = await firstContext.ProtocolOutbox.SingleAsync(
                    row => row.MessageType == "ExceptionRecoverySessionSnapshot" && row.FencedAt == null,
                    TestContext.Current.CancellationToken);
                using (JsonDocument snapshotDocument = JsonDocument.Parse(recoverySnapshot.PayloadJson))
                {
                    Assert.Equal("EXECUTING", snapshotDocument.RootElement.GetProperty("payload")
                        .GetProperty("state").GetString());
                    await firstProcessor.ProcessAsync(
                        Envelope(
                            "e0000000-0000-4000-8000-000000000003",
                            "SnapshotAppliedAck",
                            new
                            {
                                snapshotMessageId = recoverySnapshot.MessageId,
                                snapshotKind = "EXCEPTION_RECOVERY_SESSION",
                                appliedRevision = snapshotDocument.RootElement.GetProperty("payload")
                                    .GetProperty("recoverySessionRevision").GetInt64(),
                                appliedContentSha256 = Sha256(recoverySnapshot.PayloadJson)
                            },
                            recoverySnapshot.MessageId),
                        state,
                        TestContext.Current.CancellationToken);
                }
                Assert.NotNull(recoverySnapshot.AcknowledgedAt);
                string resultAck = await firstProcessor.ProcessAsync(
                    Envelope(
                        "e0000000-0000-4000-8000-000000000004",
                        "OperationResult",
                        OperationResultPayload()),
                    state,
                    TestContext.Current.CancellationToken);
                await firstProcessor.FlushDeferredOutboundAsync(
                    state, TestContext.Current.CancellationToken);
                Assert.Equal("DurableAck", MessageType(resultAck));
                Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await firstContext.JourneyRuntimes.SingleAsync(
                    TestContext.Current.CancellationToken)).Stage);
                Assert.Equal(StationOperationStatus.Committed, (await firstContext.StationOperations.SingleAsync(
                    TestContext.Current.CancellationToken)).Status);
                Assert.Equal(RecoveryWorkflowState.Reconciled, (await firstContext.RecoveryWorkflows.SingleAsync(
                    TestContext.Current.CancellationToken)).State);
                Assert.Equal("CLOSED", (await firstContext.ExceptionRecoverySessions.SingleAsync(
                    TestContext.Current.CancellationToken)).State);
                Assert.Equal(1, await firstContext.ProtocolInbox.CountAsync(
                    row => row.MessageType == "RecoveryActionSubmitted",
                    TestContext.Current.CancellationToken));
                Assert.DoesNotContain(proof, (await firstContext.ProtocolInbox.SingleAsync(
                    row => row.MessageType == "ExceptionRecoverySessionRequested",
                    TestContext.Current.CancellationToken)).RequestJson, StringComparison.Ordinal);
                Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await firstContext.AcceptedDemands.SingleAsync(
                    TestContext.Current.CancellationToken)).Status);
                Assert.Null((await firstContext.VehicleDispatchLeases.SingleAsync(
                    TestContext.Current.CancellationToken)).ReleasedAt);
            }

            await using ControlServerDbContext restartedContext = await CreateContextAsync(connection);
            RecordingPeer restartedPeer = new(restartedContext);
            OnboardMessageProcessor restartedProcessor = Processor(
                restartedContext, restartedPeer, proofVariable);
            OnboardConnectionState restartedState = CurrentState(deferOutbound: true);
            string replayedResponse = await restartedProcessor.ProcessAsync(
                actionRequest, restartedState, TestContext.Current.CancellationToken);
            Assert.Empty(restartedPeer.Lines);
            await restartedProcessor.FlushDeferredOutboundAsync(
                restartedState, TestContext.Current.CancellationToken);

            Assert.Equal(firstResponse, replayedResponse);
            Assert.Equal(firstCommand, restartedPeer.Lines.Single(
                line => MessageType(line) == "SlotOperationResumeCommand"));
            Assert.Single(await restartedContext.RecoveryWorkflows.ToArrayAsync(
                TestContext.Current.CancellationToken));
            Assert.Equal(4, await restartedContext.ProtocolOutbox.CountAsync(
                TestContext.Current.CancellationToken));
            Assert.Equal(2, await restartedContext.OrderIntents.CountAsync(
                TestContext.Current.CancellationToken));
            Assert.Single(await restartedContext.VehicleDispatchLeases.ToArrayAsync(
                TestContext.Current.CancellationToken));
            Assert.Single(await restartedContext.StationOperations.ToArrayAsync(
                TestContext.Current.CancellationToken));

            string conflictingAction = RecoveryAction("RESUME_AFTER_REPAIR", reason: "different-content");
            await Assert.ThrowsAsync<ProtocolContentConflictException>(() => restartedProcessor.ProcessAsync(
                conflictingAction, CurrentState(), TestContext.Current.CancellationToken));
            Assert.Equal(4, await restartedContext.ProtocolOutbox.CountAsync(
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task ResumeAdmitsExactlyOneReplacementResultForTheOperationThatAlreadyFailedItsFirstResult()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPLACEMENT";
        const string proof = "replacement-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            await AuthorizeResumeAfterFailedResultAsync(processor, context, state, proof);

            string replacementAck = await processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000000011",
                    "OperationResult",
                    OperationResultPayload(journalCheckpoint: "RESUME_RESULT_RECORDED")),
                state,
                TestContext.Current.CancellationToken);
            await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);

            Assert.Equal("DurableAck", MessageType(replacementAck));
            Assert.Equal(2, await context.OperationResults.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(StationOperationStatus.Committed, (await context.StationOperations.SingleAsync(
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(RecoveryWorkflowState.Reconciled, (await context.RecoveryWorkflows.SingleAsync(
                TestContext.Current.CancellationToken)).State);
            Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.SingleAsync(
                TestContext.Current.CancellationToken)).State);
            Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await context.JourneyRuntimes.SingleAsync(
                TestContext.Current.CancellationToken)).Stage);

            // Exactly one. The authorization was consumed with the replacement, so a third result
            // has nothing behind it and lands back on the ordinary replay conflict.
            await Assert.ThrowsAsync<ProtocolContentConflictException>(() => processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000000012",
                    "OperationResult",
                    OperationResultPayload(journalCheckpoint: "UNAUTHORIZED_THIRD_RESULT")),
                state,
                TestContext.Current.CancellationToken));
            Assert.Equal(2, await context.OperationResults.CountAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task ResumeRejectsAReplacementResultReportedOutsideTheAuthorizedSlotScope()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_SCOPE";
        const string proof = "scope-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            await AuthorizeResumeAfterFailedResultAsync(processor, context, state, proof);

            // The authorization covers slots 1 and 2. A replacement that settles only slot 1 leaves
            // slot 2 unproven, so it is refused outright rather than degraded to RecoveryRequired:
            // the operation would otherwise carry a result narrower than what was authorized.
            await Assert.ThrowsAsync<BusinessIdentityConflictException>(() => processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000000013",
                    "OperationResult",
                    OperationResultPayload(slots: [1], journalCheckpoint: "RESUME_RESULT_RECORDED")),
                state,
                TestContext.Current.CancellationToken));

            Assert.Single(await context.OperationResults.ToArrayAsync(TestContext.Current.CancellationToken));
            Assert.Equal(StationOperationStatus.RecoveryRequired, (await context.StationOperations.SingleAsync(
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(RecoveryWorkflowState.AwaitingResult, (await context.RecoveryWorkflows.SingleAsync(
                TestContext.Current.CancellationToken)).State);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    public async Task ARefusedResultTellsTheVehicleItsSessionNeedsRecovery()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedBlockedJourneyAsync(context, productionShapedSession: true);
        // The seeded operation is already RecoveryRequired, which is the state a refused result
        // leaves; what this test is about is whether the vehicle is ever told.
        StationOperationRow operation = await context.StationOperations.SingleAsync(
            TestContext.Current.CancellationToken);
        operation.Status = StationOperationStatus.Prepared;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        RecordingPeer peer = new(context);
        OnboardMessageProcessor processor = Processor(context, peer, "CONTROL_SERVER_TEST_UNUSED_PROOF");
        OnboardConnectionState state = CurrentState(deferOutbound: true);
        state.Readiness = SessionReadiness.Ready;

        string response = await processor.ProcessAsync(
            Envelope(
                "e0000000-0000-4000-8000-000000000030",
                "OperationResult",
                OperationResultPayload(completed: false, journalCheckpoint: "OPERATOR_TIMEOUT")),
            state,
            TestContext.Current.CancellationToken);
        await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);

        // Two lines: the durable ack the vehicle is waiting for, then the readiness it did not used
        // to be told about. Readiness was recomputed only on RecoveryStateReport and
        // SafetyStateChanged, and the vehicle sends neither after a load fails — so the session
        // stayed READY and the onboard, which gates its recovery entry on RECOVERY_REQUIRED, never
        // offered the operator any way to start a recovery.
        string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("DurableAck", MessageType(lines[0]));
        Assert.Equal("SessionReadiness", MessageType(lines[1]));
        using (JsonDocument readiness = JsonDocument.Parse(lines[1]))
        {
            JsonElement payload = readiness.RootElement.GetProperty("payload");
            Assert.Equal("RECOVERY_REQUIRED", payload.GetProperty("readiness").GetString());
            Assert.Equal(
                "SESSION_RECOVERY_REQUIRED",
                Assert.Single(payload.GetProperty("reasonCodes").EnumerateArray()).GetString());
        }
        Assert.Equal(SessionReadiness.RecoveryRequired, state.Readiness);

        // Redelivering the same result is idempotent: the inbox returns the stored first response,
        // both lines of it, rather than recomputing anything. That is what keeps the announcement
        // from multiplying while the vehicle retries.
        string replay = await processor.ProcessAsync(
            Envelope(
                "e0000000-0000-4000-8000-000000000030",
                "OperationResult",
                OperationResultPayload(completed: false, journalCheckpoint: "OPERATOR_TIMEOUT")),
            state,
            TestContext.Current.CancellationToken);
        Assert.Equal(response, replay);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    public async Task ASessionIsNotReadyWhileTheServerHoldsAnOperationNeedingRecovery()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        // The production shape: the vehicle reported nothing outstanding and got its result acked,
        // so every input readiness used to look at says "fine". The one thing that is not fine is
        // the server's own verdict on that result.
        await SeedBlockedJourneyAsync(context, productionShapedSession: true);
        WireToGateStore store = new(context);

        SessionReadinessDecision decision = await store.DecideReadinessAsync(
            AgvId, 3, TestContext.Current.CancellationToken);

        Assert.Equal(SessionReadiness.RecoveryRequired, decision.Readiness);
        Assert.Equal("OPERATION_RECOVERY_REQUIRED", decision.ReasonCode);
        // The onboard shows its recovery entry only while the session says RECOVERY_REQUIRED, so
        // this is what lets the operator open a recovery session at all. Before this, a vehicle with
        // a load needing recovery reported READY and the entry never appeared -- for any vector,
        // including the COMPENSATE_LOAD_ALL_EMPTY the server would have authorized.
        Assert.Equal(
            "SESSION_RECOVERY_REQUIRED",
            ProtocolErrorCodes.ToSessionReadinessReasonCode(decision.ReasonCode));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    public async Task ASessionIsReadyOnceItsOperationIsCommitted()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedBlockedJourneyAsync(context, productionShapedSession: true);
        // The negative half: the new condition must not be a one-way door that leaves every vehicle
        // that ever needed recovery stuck out of Ready.
        StationOperationRow operation = await context.StationOperations.SingleAsync(
            TestContext.Current.CancellationToken);
        operation.Status = StationOperationStatus.Committed;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        WireToGateStore store = new(context);

        SessionReadinessDecision decision = await store.DecideReadinessAsync(
            AgvId, 3, TestContext.Current.CancellationToken);

        Assert.Equal(SessionReadiness.Ready, decision.Readiness);
        Assert.Equal("READY", decision.ReasonCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task AReportedPendingFactIsReconciledOnlyByAConclusionTheServerHolds()
    {
        // 8005-agv-program#46. What the vehicle reports as outstanding at the handshake is written once
        // and never revisited, so a session whose attempt had long since been answered stayed at
        // PENDING_FACT_RECONCILIATION_REQUIRED until the vehicle reconnected. The guard it replaces
        // (#40) still holds: while the server has no conclusion, the attempt stays outstanding --
        // after a reconnect the onboard executor may still be running it.
        const string pendingResultMessageId = "e0000000-0000-4000-8000-000000000040";
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedBlockedJourneyAsync(context, productionShapedSession: true);
        StationOperationRow operation = await context.StationOperations.SingleAsync(
            TestContext.Current.CancellationToken);
        operation.Status = StationOperationStatus.Prepared;
        SessionRecoveryRow session = await context.SessionRecoveries.SingleAsync(
            TestContext.Current.CancellationToken);
        session.UnsettledSlotOperationAttemptId = AttemptId;
        session.PendingAttemptIdsJson = JsonSerializer.Serialize(new[] { AttemptId });
        session.PendingResultIdsJson = JsonSerializer.Serialize(new[] { pendingResultMessageId });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        WireToGateStore store = new(context);

        Assert.Equal(
            "PENDING_FACT_RECONCILIATION_REQUIRED",
            (await store.DecideReadinessAsync(AgvId, 3, TestContext.Current.CancellationToken)).ReasonCode);

        // A result from before a forced recovery is kept as evidence only and was never applied, so
        // it is not a conclusion the server acted on.
        OperationResultRow result = new()
        {
            ResultId = "e0000000-0000-4000-8000-000000000041",
            SlotOperationAttemptId = AttemptId,
            AgvId = AgvId,
            ForcedRecoveryGeneration = 0,
            ContentHash = new string('b', 64),
            ResultContentSha256 = new string('c', 64),
            OverallOutcome = "COMPLETED",
            EvidenceJson = "[]",
            ObservedAt = Now,
            HistoricalOnly = true,
            ReceivedAt = Now
        };
        context.OperationResults.Add(result);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(
            "PENDING_FACT_RECONCILIATION_REQUIRED",
            (await store.DecideReadinessAsync(AgvId, 3, TestContext.Current.CancellationToken)).ReasonCode);

        result.HistoricalOnly = false;
        operation.Status = StationOperationStatus.Committed;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        // The attempt is answered; the reported result message still is not.
        Assert.Equal(
            "PENDING_FACT_RECONCILIATION_REQUIRED",
            (await store.DecideReadinessAsync(AgvId, 3, TestContext.Current.CancellationToken)).ReasonCode);

        context.ProtocolInbox.Add(new ProtocolInboxRow
        {
            MessageId = pendingResultMessageId,
            MessageType = "OperationResult",
            RequestJson = "{}",
            ContentHash = new string('d', 64),
            FirstResponseJson = "{}",
            ReceivedAt = Now
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        SessionReadinessDecision decision = await store.DecideReadinessAsync(
            AgvId, 3, TestContext.Current.CancellationToken);

        Assert.Equal(SessionReadiness.Ready, decision.Readiness);
        // What the vehicle reported stays as it reported it.
        Assert.Equal(
            JsonSerializer.Serialize(new[] { AttemptId }),
            (await context.SessionRecoveries.SingleAsync(TestContext.Current.CancellationToken))
                .PendingAttemptIdsJson);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-00")]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    public async Task AResultThatAnswersTheLastPendingAttemptTellsTheVehicleItIsReady()
    {
        // The reconnect-mid-load shape: the handshake reported the attempt as outstanding because the
        // executor was still running it, and it then finished normally. The session used to stay
        // RecoveryRequired until the next reconnect, and nothing told the vehicle otherwise.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedBlockedJourneyAsync(context, productionShapedSession: true);
        StationOperationRow operation = await context.StationOperations.SingleAsync(
            TestContext.Current.CancellationToken);
        operation.Status = StationOperationStatus.Prepared;
        await ReportAttemptOutstandingAsync(context);
        RecordingPeer peer = new(context);
        OnboardMessageProcessor processor = Processor(context, peer, "CONTROL_SERVER_TEST_UNUSED_PROOF");
        OnboardConnectionState state = CurrentState(deferOutbound: true);

        string response = await processor.ProcessAsync(
            Envelope("e0000000-0000-4000-8000-000000000050", "OperationResult", OperationResultPayload()),
            state,
            TestContext.Current.CancellationToken);
        await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);

        string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("DurableAck", MessageType(lines[0]));
        AssertReadinessLine(lines[1], "READY");
        Assert.Equal(SessionReadiness.Ready, state.Readiness);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task AReconciledCompensationTellsTheVehicleItsSessionIsReadyAgain()
    {
        // 8005-agv-program#46, the shape L2 real-onboard-restart-while-waiting-operator-004/-005 ended
        // in: the onboard was killed while a load waited for the operator, reported the attempt at the
        // handshake, settled it as UNKNOWN on restart, and an administrator compensated it. Demand
        // Cancelled, journey Completed -- and the session still RecoveryRequired, so the vehicle could
        // not take the next demand without being restarted again.
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_READY_AFTER_COMPENSATION";
        const string proof = "ready-after-compensation-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            StationOperationRow operation = await context.StationOperations.SingleAsync(
                TestContext.Current.CancellationToken);
            operation.Status = StationOperationStatus.Prepared;
            await ReportAttemptOutstandingAsync(context);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState(deferOutbound: true);

            string unknownAck = await processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000000060",
                    "OperationResult",
                    OperationResultPayload(completed: false)),
                state,
                TestContext.Current.CancellationToken);
            await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);
            Assert.Single(unknownAck.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            // The server holds the vehicle's conclusion now, so what keeps the session out of Ready is
            // its own verdict on that conclusion rather than a fact it is still waiting for.
            Assert.Equal(
                "OPERATION_RECOVERY_REQUIRED",
                (await context.SessionRecoveries.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken))
                    .ReasonCode);

            await processor.ProcessAsync(
                RecoverySessionRequest(proof), state, TestContext.Current.CancellationToken);
            await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);
            string accepted = await processor.ProcessAsync(
                RecoveryAction("COMPENSATE_LOAD_ALL_EMPTY"), state, TestContext.Current.CancellationToken);
            await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);
            Assert.Equal("RecoveryActionAccepted", MessageType(accepted));
            await processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000000061",
                    "LoadCompensationRequested",
                    new
                    {
                        recoveryActionId = ActionId,
                        exceptionRecoverySessionId = StableGuid(RequestId, "exception-recovery-session"),
                        demandId = DemandId,
                        slotOperationAttemptId = AttemptId,
                        @operator = Operator()
                    }),
                state,
                TestContext.Current.CancellationToken);
            await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);
            string compensationResult = Envelope(
                "e0000000-0000-4000-8000-000000000062",
                "LoadCompensationResult",
                new
                {
                    recoveryActionId = ActionId,
                    demandId = DemandId,
                    slotOperationAttemptId = AttemptId,
                    overallOutcome = "ALL_EMPTY",
                    slotResults = RecoverySlots.Select(slot => new
                    {
                        slotNo = slot,
                        outcome = "COMPLETED",
                        finalPhysicalState = "EMPTY",
                        lockState = "LOCKED",
                        unlockOutputState = "RESET",
                        reasonCodes = Array.Empty<string>()
                    }).ToArray(),
                    observedAt = Now.AddSeconds(5)
                });

            string response = await processor.ProcessAsync(
                compensationResult, state, TestContext.Current.CancellationToken);
            await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);

            Assert.Equal(StationOperationStatus.Cancelled, (await context.StationOperations.AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken)).Status);
            string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Equal("DurableAck", MessageType(lines[0]));
            AssertReadinessLine(lines[1], "READY");
            Assert.Equal(SessionReadiness.Ready, state.Readiness);
            SessionRecoveryRow session = await context.SessionRecoveries.AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(SessionReadiness.Ready, session.Readiness);
            Assert.Equal("READY", session.ReasonCode);

            // The result answers the compensation command, and nothing else ever will: the profile has no
            // ack for it (LoadCompensationCommandAck is on the denylist). Left pending, the row outlived the
            // journey in every replay scan (8005-agv-control-server#31).
            Assert.NotNull((await context.ProtocolOutbox.AsNoTracking().SingleAsync(
                row => row.MessageType == "LoadCompensationCommand",
                TestContext.Current.CancellationToken)).AcknowledgedAt);

            // A redelivered result gets the stored first response back, readiness line included, and
            // recomputes nothing.
            string replay = await processor.ProcessAsync(
                compensationResult, state, TestContext.Current.CancellationToken);
            Assert.Equal(response, replay);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task AfterARefusedResultResumeIsRefusedButCompensationIsAuthorized()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_PRODUCTION_SHAPE";
        const string proof = "production-shape-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState(deferOutbound: true);

            string opened = await processor.ProcessAsync(
                RecoverySessionRequest(proof), state, TestContext.Current.CancellationToken);
            await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);
            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(opened));
            // Control-server issue #5: the vehicle reached this state by believing the load
            // succeeded, which cleared its own copy of the attempt identity. Every recovery request
            // it must send from here -- LoadCompensationRequested below all of them -- has
            // slotOperationAttemptId as a required field, so unless the server names the attempt it
            // is recovering, the exchange cannot continue past this message.
            using (JsonDocument openedSession = JsonDocument.Parse(opened))
            {
                Assert.Equal(
                    AttemptId,
                    openedSession.RootElement.GetProperty("payload")
                        .GetProperty("slotOperationAttemptId").GetString());
            }
            ProtocolOutboxRow snapshot = await context.ProtocolOutbox.SingleAsync(
                row => row.MessageType == "ExceptionRecoverySessionSnapshot" && row.FencedAt == null,
                TestContext.Current.CancellationToken);
            using (JsonDocument projected = JsonDocument.Parse(snapshot.PayloadJson))
            {
                Assert.Equal(
                    AttemptId,
                    projected.RootElement.GetProperty("payload")
                        .GetProperty("slotOperationAttemptId").GetString());
            }

            // RESUME_AFTER_REPAIR is refused here, and that is the design rather than a defect: it
            // resumes an operation that stalled at a physical breakpoint the vehicle still holds.
            // Once the vehicle has recorded a result there is no such breakpoint — the onboard
            // clears the attempt and its operation context, and refuses a resume command whose
            // checkpoint is not PREPARED / ACTIVE_UNLOCK_SET / SAFE_FINISH_REACHED. Both ends agree.
            string resumeRefused = await processor.ProcessAsync(
                RecoveryAction("RESUME_AFTER_REPAIR"), state, TestContext.Current.CancellationToken);
            await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);
            Assert.Equal("RecoveryActionRejected", MessageType(resumeRefused));
            using (JsonDocument refusal = JsonDocument.Parse(resumeRefused))
            {
                Assert.Equal(
                    "PROVEN_RECOVERY_CHECKPOINT_REQUIRED",
                    refusal.RootElement.GetProperty("payload").GetProperty("problem")
                        .GetProperty("reasonCode").GetString());
            }

            // The vector that fits this state is COMPENSATE_LOAD_ALL_EMPTY — the load ran and left
            // every slot empty — and the server authorizes it from exactly the facts production
            // leaves behind: no unsettled attempt, no proven checkpoint, session Ready. Its
            // preconditions ask only that the operation be a Load in RecoveryRequired.
            string compensation = await processor.ProcessAsync(
                RecoveryAction(
                    "COMPENSATE_LOAD_ALL_EMPTY",
                    messageId: "e0000000-0000-4000-8000-000000000020",
                    actionId: "50000000-0000-4000-8000-000000000020"),
                state,
                TestContext.Current.CancellationToken);
            await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);
            Assert.Equal("RecoveryActionAccepted", MessageType(compensation));
            using (JsonDocument accepted = JsonDocument.Parse(compensation))
            {
                Assert.Equal(
                    "COMPENSATE_LOAD_ALL_EMPTY",
                    accepted.RootElement.GetProperty("payload").GetProperty("acceptedAction").GetString());
                Assert.Equal(
                    AttemptId,
                    accepted.RootElement.GetProperty("payload")
                        .GetProperty("slotOperationAttemptId").GetString());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task ARecoverySessionIsJudgedOnTheJourneyAsStoredNotAsThisConnectionFirstSawIt()
    {
        // 8005-agv-program#40. One DbContext serves a whole TCP connection, and the handshake loads
        // this vehicle's active journeys into it, tracked. A vehicle that reconnects mid-load is seen
        // at AwaitingLoadResult; the runtime worker then blocks the journey from its own context. The
        // recovery request that follows on the same connection used to be judged on the tracked copy
        // -- still AwaitingLoadResult -- and refused RECOVERY_DEMAND_NOT_BLOCKED against a journey the
        // database held as Blocked. L2 evidence real-onboard-restart-while-waiting-operator-002.
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_STALE_JOURNEY";
        const string proof = "stale-journey-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using ControlServerDbContext connectionContext = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(connectionContext, productionShapedSession: true);
            await using (ControlServerDbContext seed = await CreateContextAsync(connection))
            {
                JourneyRuntimeRow runtime = await seed.JourneyRuntimes.SingleAsync(TestContext.Current.CancellationToken);
                runtime.Stage = JourneyRuntimeStage.AwaitingLoadResult;
                runtime.BlockReasonCode = "ONBOARD_SESSION_NOT_READY";
                await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
            // What the handshake does to the connection's context: every active journey of this
            // vehicle, tracked, as it stands at that moment.
            connectionContext.ChangeTracker.Clear();
            Assert.Equal(
                JourneyRuntimeStage.AwaitingLoadResult,
                (await connectionContext.JourneyRuntimes.SingleAsync(TestContext.Current.CancellationToken)).Stage);
            // The runtime worker, on its own context, blocks the journey on the recorded recovery.
            await using (ControlServerDbContext worker = await CreateContextAsync(connection))
            {
                JourneyRuntimeRow runtime = await worker.JourneyRuntimes.SingleAsync(TestContext.Current.CancellationToken);
                runtime.Stage = JourneyRuntimeStage.Blocked;
                runtime.BlockReasonCode = "LOAD_RESULT_REQUIRES_RECOVERY";
                await worker.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            RecordingPeer peer = new(connectionContext);
            OnboardMessageProcessor processor = Processor(connectionContext, peer, proofVariable);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            string answer = await processor.ProcessAsync(
                RecoverySessionRequest(proof), state, TestContext.Current.CancellationToken);
            await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);

            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(answer));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task ForcedRecoveryAdvancesOnceFencesOldOutboxAndKeepsLateGenerationAsEvidenceOnly()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_FORCED";
        const string proof = "forced-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            context.ProtocolOutbox.Add(new ProtocolOutboxRow
            {
                MessageId = "60000000-0000-4000-8000-000000000001",
                MessageType = "SlotOperationCommand",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    agvId = AgvId,
                    payload = new { slotOperationAttemptId = AttemptId }
                }),
                CreatedAt = Now.AddMinutes(-1)
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(
                RecoverySessionRequest(proof), state, TestContext.Current.CancellationToken);

            string accepted = await processor.ProcessAsync(
                RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, TestContext.Current.CancellationToken);

            Assert.Equal("RecoveryActionAccepted", MessageType(accepted));
            VehicleRecoveryGenerationRow generation = await context.VehicleRecoveryGenerations.SingleAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(1, generation.ForcedRecoveryGeneration);
            ProtocolOutboxRow oldCommand = await context.ProtocolOutbox.SingleAsync(
                row => row.MessageType == "SlotOperationCommand",
                TestContext.Current.CancellationToken);
            Assert.NotNull(oldCommand.FencedAt);
            ProtocolOutboxRow forcedCommand = await context.ProtocolOutbox.SingleAsync(
                row => row.MessageType == "ForcedMechanicalRecoveryCommand",
                TestContext.Current.CancellationToken);
            Assert.Null(forcedCommand.FencedAt);
            using (JsonDocument command = JsonDocument.Parse(forcedCommand.PayloadJson))
                Assert.Equal(1, command.RootElement.GetProperty("payload")
                    .GetProperty("forcedRecoveryGeneration").GetInt64());

            string lateAck = await processor.ProcessAsync(
                Envelope(
                    "70000000-0000-4000-8000-000000000001",
                    "OperationResult",
                    OperationResultPayload()),
                state,
                TestContext.Current.CancellationToken);
            string result = Envelope(
                "80000000-0000-4000-8000-000000000001",
                "ForcedMechanicalRecoveryResult",
                new
                {
                    exceptionRecoverySessionId = StableGuid(RequestId, "exception-recovery-session"),
                    recoveryActionId = ActionId,
                    forcedRecoveryGeneration = 1,
                    outcome = "MECHANICALLY_ISOLATED",
                    slots = RecoverySlots,
                    @operator = Operator(),
                    observedAt = Now.AddSeconds(2),
                    electronicEmptyProven = false,
                    vehicleReadyProven = false
                });
            await processor.ProcessAsync(result, state, TestContext.Current.CancellationToken);

            Assert.Equal("DurableAck", MessageType(lateAck));
            Assert.True((await context.OperationResults.SingleAsync(
                TestContext.Current.CancellationToken)).HistoricalOnly);
            RecoveryWorkflowRow workflow = await context.RecoveryWorkflows.SingleAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(1, workflow.ForcedRecoveryGeneration);
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, workflow.State);
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await context.AcceptedDemands.SingleAsync(
                TestContext.Current.CancellationToken)).Status);
            Assert.Null((await context.VehicleDispatchLeases.SingleAsync(
                TestContext.Current.CancellationToken)).ReleasedAt);
            Assert.Empty(await context.TransportDemandCompletions.ToArrayAsync(
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task FailedCompensationResultIsDurableReplayableAndNeverReleasesDemandOrVehicle()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_COMPENSATE";
        const string proof = "compensation-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(
                RecoverySessionRequest(proof), state, TestContext.Current.CancellationToken);
            await processor.ProcessAsync(
                RecoveryAction("COMPENSATE_LOAD_ALL_EMPTY"), state, TestContext.Current.CancellationToken);
            string compensationRequest = Envelope(
                "90000000-0000-4000-8000-000000000001",
                "LoadCompensationRequested",
                new
                {
                    recoveryActionId = ActionId,
                    exceptionRecoverySessionId = StableGuid(RequestId, "exception-recovery-session"),
                    demandId = DemandId,
                    slotOperationAttemptId = AttemptId,
                    @operator = Operator()
                });
            string requestResponse = await processor.ProcessAsync(
                compensationRequest, state, TestContext.Current.CancellationToken);

            Assert.Equal(string.Empty, requestResponse);
            Assert.Single(peer.Lines, line => MessageType(line) == "LoadCompensationCommand");
            string failedResult = Envelope(
                "a0000000-0000-4000-8000-000000000001",
                "LoadCompensationResult",
                new
                {
                    recoveryActionId = ActionId,
                    demandId = DemandId,
                    slotOperationAttemptId = AttemptId,
                    overallOutcome = "FAILED",
                    slotResults = new[]
                    {
                        new
                        {
                            slotNo = 1,
                            outcome = "FAILED",
                            finalPhysicalState = "UNKNOWN",
                            lockState = "UNKNOWN",
                            unlockOutputState = "UNKNOWN",
                            reasonCodes = UnknownReasonCodes
                        },
                        new
                        {
                            slotNo = 2,
                            outcome = "NOT_STARTED",
                            finalPhysicalState = "OCCUPIED",
                            lockState = "LOCKED",
                            unlockOutputState = "RESET",
                            reasonCodes = Array.Empty<string>()
                        }
                    },
                    observedAt = Now.AddSeconds(3)
                });
            string firstAck = await processor.ProcessAsync(
                failedResult, state, TestContext.Current.CancellationToken);
            string replayAck = await processor.ProcessAsync(
                failedResult, state, TestContext.Current.CancellationToken);

            Assert.Equal(firstAck, replayAck);
            Assert.Equal("DurableAck", MessageType(firstAck));
            Assert.Single(await context.RecoveryResultEvidence.ToArrayAsync(
                TestContext.Current.CancellationToken));
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, (await context.RecoveryWorkflows.SingleAsync(
                TestContext.Current.CancellationToken)).State);
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await context.AcceptedDemands.SingleAsync(
                TestContext.Current.CancellationToken)).Status);
            Assert.Null((await context.VehicleDispatchLeases.SingleAsync(
                TestContext.Current.CancellationToken)).ReleasedAt);
            Assert.Empty(await context.TransportDemandCompletions.ToArrayAsync(
                TestContext.Current.CancellationToken));
            // A failed result answers the command as surely as a successful one. The workflow takes no
            // second result, so replaying the command into a later session could only draw a duplicate.
            Assert.NotNull(Assert.Single(await context.ProtocolOutbox.Where(
                    row => row.MessageType == "LoadCompensationCommand")
                .ToArrayAsync(TestContext.Current.CancellationToken)).AcknowledgedAt);

            string changedResultIdentity = failedResult.Replace(
                "a0000000-0000-4000-8000-000000000001",
                "a0000000-0000-4000-8000-000000000004",
                StringComparison.Ordinal);
            await Assert.ThrowsAsync<ProtocolContentConflictException>(() => processor.ProcessAsync(
                changedResultIdentity, state, TestContext.Current.CancellationToken));

            string cancellationAuthorization = await processor.ProcessAsync(
                Envelope(
                    "a0000000-0000-4000-8000-000000000002",
                    "LoadCancellationStartRequested",
                    new
                    {
                        cancellationId = "a0000000-0000-4000-8000-000000000003",
                        demandId = DemandId,
                        slotOperationAttemptId = AttemptId,
                        @operator = Operator(),
                        reason = "Must not bypass recovery."
                    }),
                state,
                TestContext.Current.CancellationToken);
            using (JsonDocument authorization = JsonDocument.Parse(cancellationAuthorization))
            {
                Assert.Equal("LoadCancellationAuthorization",
                    authorization.RootElement.GetProperty("messageType").GetString());
                Assert.Equal("REJECTED", authorization.RootElement.GetProperty("payload")
                    .GetProperty("decision").GetString());
            }
            Assert.Single(await context.ProtocolOutbox.Where(
                    row => row.MessageType == "LoadCompensationCommand")
                .ToArrayAsync(TestContext.Current.CancellationToken));

            string conflictingResult = failedResult.Replace(
                "PHYSICAL_STATE_UNKNOWN", "DIFFERENT_CONTENT", StringComparison.Ordinal);
            await Assert.ThrowsAsync<ProtocolContentConflictException>(() => processor.ProcessAsync(
                conflictingResult, state, TestContext.Current.CancellationToken));
            Assert.Single(await context.RecoveryResultEvidence.ToArrayAsync(
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    [Trait("IntegrationSlice", "W2G-IS-07")]
    public async Task RecoveryReportCannotRegressOrInventForcedRecoveryGeneration()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedBlockedJourneyAsync(context);
        WireToGateStore store = new(context);
        await store.AdvanceForcedRecoveryGenerationAsync(
            AgvId, 1, Now.AddSeconds(1), TestContext.Current.CancellationToken);

        await store.ApplyRecoveryReportAsync(
            AgvId,
            3,
            "f0000000-0000-4000-8000-000000000001",
            forcedRecoveryGeneration: 0,
            AttemptId,
            "PREPARED",
            RecoverySlots,
            [AttemptId],
            [],
            TestContext.Current.CancellationToken);
        SessionReadinessDecision decision = await store.DecideReadinessAsync(
            AgvId, 3, TestContext.Current.CancellationToken);
        SessionRecoveryRow session = await context.SessionRecoveries.SingleAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(1, session.ForcedRecoveryGeneration);
        Assert.Equal(0, session.ReportedForcedRecoveryGeneration);
        Assert.Equal(SessionReadiness.RecoveryRequired, decision.Readiness);
        Assert.Equal("FORCED_RECOVERY_GENERATION_MISMATCH", decision.ReasonCode);
        Assert.Equal(1, (await context.VehicleRecoveryGenerations.SingleAsync(
            TestContext.Current.CancellationToken)).ForcedRecoveryGeneration);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    [Trait("IntegrationSlice", "W2G-IS-05")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task FormalCorrectionAndFaultCommandsUseDurableOutboxReplayAndExactAck()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        RecordingPeer peer = new(context);
        OnboardJourneyPublisher publisher = new(store, peer, new FixedTimeProvider(Now));
        string correctionMessageId = "f1000000-0000-4000-8000-000000000001";
        string faultMessageId = "f1000000-0000-4000-8000-000000000002";
        string sessionId = "f1000000-0000-4000-8000-000000000003";
        string correctionId = "f1000000-0000-4000-8000-000000000004";
        string handoffId = "f1000000-0000-4000-8000-000000000005";
        string correctionHash = new string('b', 64);
        string faultHash = new string('c', 64);

        await publisher.QueueLoadCorrectionCommandAsync(
            correctionMessageId,
            AgvId,
            3,
            new LoadCorrectionAuthorizationCommand(
                correctionId, DemandId, AttemptId, RecoverySlots, correctionHash),
            TestContext.Current.CancellationToken);
        await publisher.QueueFaultCargoRecoveryCommandAsync(
            faultMessageId,
            AgvId,
            3,
            new FaultCargoRecoveryAuthorizationCommand(
                sessionId, ActionId, DemandId, RecoverySlots, handoffId, faultHash),
            TestContext.Current.CancellationToken);

        Assert.Empty(peer.Lines);
        Assert.Equal(2, await context.ProtocolOutbox.CountAsync(TestContext.Current.CancellationToken));
        await publisher.SendPersistedAsync(correctionMessageId, TestContext.Current.CancellationToken);
        await publisher.SendPersistedAsync(faultMessageId, TestContext.Current.CancellationToken);
        await publisher.SendPersistedAsync(correctionMessageId, TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedRecoveryCommandReplay, peer.Lines.Select(MessageType).ToArray());
        Assert.Equal(peer.Lines[0], peer.Lines[2]);
        ProtocolOutboxRow correction = await context.ProtocolOutbox.SingleAsync(
            row => row.MessageId == correctionMessageId,
            TestContext.Current.CancellationToken);
        await store.AcknowledgeOutboundEnvelopeAsync(
            correctionMessageId,
            "LoadCorrectionCommand",
            Sha256(correction.PayloadJson),
            null,
            Now.AddSeconds(1),
            TestContext.Current.CancellationToken);
        await publisher.SendPersistedAsync(correctionMessageId, TestContext.Current.CancellationToken);
        Assert.Equal(3, peer.Lines.Count);

        using JsonDocument fault = JsonDocument.Parse((await context.ProtocolOutbox.SingleAsync(
            row => row.MessageId == faultMessageId,
            TestContext.Current.CancellationToken)).PayloadJson);
        Assert.Equal(ProtocolCandidateIdentity.ReleaseVersion,
            fault.RootElement.GetProperty("protocolReleaseVersion").GetString());
        Assert.Equal(ProtocolCandidateIdentity.ManifestSha256,
            fault.RootElement.GetProperty("protocolReleaseManifestSha256").GetString());
        Assert.Equal(handoffId, fault.RootElement.GetProperty("payload").GetProperty("handoffId").GetString());
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task ACancellationRaisedBeforeAnySlotOperationEndsTheDemandAsItIsAuthorised()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_BEFORE_LOAD";
        Environment.SetEnvironmentVariable(proofVariable, "before-load-proof-not-a-production-secret");
        try
        {
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedPickupStopAwaitingSublotAsync(context);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState();
            state.Readiness = SessionReadiness.Ready;

            string request = Envelope(
                "e0000000-0000-4000-8000-000000000010",
                "LoadCancellationStartRequested",
                new
                {
                    cancellationId = "a0000000-0000-4000-8000-000000000010",
                    demandId = DemandId,
                    slotOperationAttemptId = (string?)null,
                    @operator = Operator(),
                    reason = "站点没有要装的货。"
                });
            string response = await processor.ProcessAsync(
                request, state, TestContext.Current.CancellationToken);

            using (JsonDocument document = JsonDocument.Parse(response))
            {
                JsonElement payload = document.RootElement.GetProperty("payload");
                Assert.Equal("LoadCancellationAuthorization",
                    document.RootElement.GetProperty("messageType").GetString());
                Assert.Equal("AUTHORIZED", payload.GetProperty("decision").GetString());
                // No slot was ever commanded, so there is nothing for the peer to clear and no
                // LoadCancellationResult to come back -- its slotResults is minItems 1.
                Assert.Empty(payload.GetProperty("slots").EnumerateArray());
                Assert.Equal(JsonValueKind.Null, payload.GetProperty("problem").ValueKind);
            }

            AcceptedDemandRow demand = await context.AcceptedDemands.SingleAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(DemandExecutionStatus.Cancelled, demand.Status);
            VehicleDispatchLeaseRow lease = await context.VehicleDispatchLeases.SingleAsync(
                TestContext.Current.CancellationToken);
            Assert.NotNull(lease.ReleasedAt);
            JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
            Assert.Equal("CANCELLED_BY_OPERATOR", runtime.BlockReasonCode);
            // The entry request is answered by the cancellation; unsettled it would replay into the
            // next session as a business id whose content changed and tear that session down.
            ProtocolOutboxRow entryRequest = await context.ProtocolOutbox.SingleAsync(
                row => row.MessageType == "SublotEntryRequested", TestContext.Current.CancellationToken);
            Assert.NotNull(entryRequest.AcknowledgedAt);
            RecoveryWorkflowRow workflow = await context.RecoveryWorkflows.SingleAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(RecoveryWorkflowState.Reconciled, workflow.State);
            Assert.Equal("CANCELLED_BEFORE_LOAD", workflow.Outcome);
            Assert.Empty(await context.StationOperations.ToArrayAsync(TestContext.Current.CancellationToken));

            // The peer keeps asking until it sees an answer, and the answer cannot change. Deciding
            // afresh from the demand status would now read Cancelled and refuse the very
            // cancellation that produced it.
            DateTimeOffset releasedAt = lease.ReleasedAt!.Value;
            string replay = await processor.ProcessAsync(
                request, state, TestContext.Current.CancellationToken);
            using (JsonDocument document = JsonDocument.Parse(replay))
            {
                Assert.Equal("AUTHORIZED",
                    document.RootElement.GetProperty("payload").GetProperty("decision").GetString());
            }
            Assert.Equal(releasedAt, (await context.VehicleDispatchLeases.SingleAsync(
                TestContext.Current.CancellationToken)).ReleasedAt);
            Assert.Single(await context.RecoveryWorkflows.ToArrayAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task CancellingBeforeLoadRefusesADemandWhoseSlotOperationWasAlreadyCommanded()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedPickupStopAwaitingSublotAsync(context);
        context.StationOperations.Add(new StationOperationRow
        {
            SlotOperationAttemptId = AttemptId,
            DemandId = DemandId,
            SublotId = "SUBLOT-001",
            TargetSlotsJson = "[1,2]",
            OperationType = SlotOperationType.Load,
            ForcedRecoveryGeneration = 0,
            ContentHash = new string('a', 64),
            Status = StationOperationStatus.Committed,
            CreatedAt = Now.AddMinutes(-1)
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        WireToGateStore store = new(context);

        // A commanded operation carries physical state only the peer can settle. Ending the demand
        // here would drop it while a slot door is possibly still open, so the shortcut is refused
        // rather than quietly widened.
        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() => store.CancelDemandBeforeLoadAsync(
            DemandId, "CANCELLED_BY_STATION_TIMEOUT", Now, TestContext.Current.CancellationToken));

        Assert.Equal(
            DemandExecutionStatus.Accepted,
            (await context.AcceptedDemands.SingleAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(
            JourneyRuntimeStage.AwaitingSublot,
            (await context.JourneyRuntimes.SingleAsync(TestContext.Current.CancellationToken)).Stage);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-02")]
    public async Task CancellingBeforeLoadIsIdempotentAndRefusesToUndoACompletedDemand()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedPickupStopAwaitingSublotAsync(context);
        WireToGateStore store = new(context);

        Assert.True(await store.CancelDemandBeforeLoadAsync(
            DemandId, "CANCELLED_BY_STATION_TIMEOUT", Now, TestContext.Current.CancellationToken));
        // Second call: already cancelled, so it reports that it did nothing rather than terminating
        // twice. Both the timeout and an operator request can reach this, and they can race.
        Assert.False(await store.CancelDemandBeforeLoadAsync(
            DemandId, "CANCELLED_BY_OPERATOR", Now.AddMinutes(1),
            TestContext.Current.CancellationToken));

        JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("CANCELLED_BY_STATION_TIMEOUT", runtime.BlockReasonCode);

        AcceptedDemandRow demand = await context.AcceptedDemands.SingleAsync(
            TestContext.Current.CancellationToken);
        demand.Status = DemandExecutionStatus.Succeeded;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() => store.CancelDemandBeforeLoadAsync(
            DemandId, "CANCELLED_BY_STATION_TIMEOUT", Now, TestContext.Current.CancellationToken));
    }

    private static OnboardMessageProcessor Processor(
        ControlServerDbContext context,
        IOnboardPeer peer,
        string proofVariable)
    {
        WireToGateStore store = new(context);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Recovery:AuthenticationProofEnvironmentVariable"] = proofVariable
            })
            .Build();
        return TestOnboardProcessorFactory.Create(
            context, store, new FixedTimeProvider(Now), configuration, peer);
    }

    /// <summary>
    /// The session as the handshake leaves it when the vehicle has an attempt without a result: it
    /// names the attempt, and the server holds the session out of Ready until it is reconciled.
    /// </summary>
    private static async Task ReportAttemptOutstandingAsync(ControlServerDbContext context)
    {
        SessionRecoveryRow session = await context.SessionRecoveries.SingleAsync(
            TestContext.Current.CancellationToken);
        session.UnsettledSlotOperationAttemptId = AttemptId;
        session.ProvenRecoveryCheckpoint = "ACTIVE_UNLOCK_SET";
        session.PendingAttemptIdsJson = JsonSerializer.Serialize(new[] { AttemptId });
        session.Readiness = SessionReadiness.RecoveryRequired;
        session.ReasonCode = "PENDING_FACT_RECONCILIATION_REQUIRED";
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static void AssertReadinessLine(string line, string expectedReadiness)
    {
        Assert.Equal("SessionReadiness", MessageType(line));
        using JsonDocument readiness = JsonDocument.Parse(line);
        JsonElement payload = readiness.RootElement.GetProperty("payload");
        Assert.Equal(expectedReadiness, payload.GetProperty("readiness").GetString());
        if (expectedReadiness == "READY")
        {
            Assert.Empty(payload.GetProperty("reasonCodes").EnumerateArray());
        }
    }

    private static OnboardConnectionState CurrentState(bool deferOutbound = false) => new()
    {
        AgvId = AgvId,
        SessionGeneration = 3,
        CapabilityRevision = 5,
        SafetyRevision = 7,
        Readiness = SessionReadiness.RecoveryRequired,
        DeferOutboundUntilResponseWritten = deferOutbound
    };

    /// <summary>
    /// Drives the state a resume actually starts from: the operation failed its first
    /// OperationResult, and an administrator then authorized RESUME_AFTER_REPAIR. Seeding
    /// StationOperationStatus.RecoveryRequired on its own skips the OperationResults row the
    /// replacement has to displace, which is the whole difficulty.
    /// </summary>
    private static async Task AuthorizeResumeAfterFailedResultAsync(
        OnboardMessageProcessor processor,
        ControlServerDbContext context,
        OnboardConnectionState state,
        string proof)
    {
        string failedAck = await processor.ProcessAsync(
            Envelope(
                "e0000000-0000-4000-8000-000000000010",
                "OperationResult",
                OperationResultPayload(completed: false, journalCheckpoint: "OPERATOR_TIMEOUT")),
            state,
            TestContext.Current.CancellationToken);
        await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);
        Assert.Equal("DurableAck", MessageType(failedAck));
        Assert.Single(await context.OperationResults.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(StationOperationStatus.RecoveryRequired, (await context.StationOperations.SingleAsync(
            TestContext.Current.CancellationToken)).Status);

        await processor.ProcessAsync(
            RecoverySessionRequest(proof), state, TestContext.Current.CancellationToken);
        await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);
        string accepted = await processor.ProcessAsync(
            RecoveryAction("RESUME_AFTER_REPAIR"), state, TestContext.Current.CancellationToken);
        await processor.FlushDeferredOutboundAsync(state, TestContext.Current.CancellationToken);
        Assert.Equal("RecoveryActionAccepted", MessageType(accepted));
        Assert.Equal(RecoveryWorkflowState.AwaitingResult, (await context.RecoveryWorkflows.SingleAsync(
            TestContext.Current.CancellationToken)).State);
    }

    /// <summary>
    /// <paramref name="productionShapedSession"/> seeds the session recovery row the way production
    /// actually leaves it after a refused OperationResult, instead of the way these tests used to
    /// assume it. The difference is the whole point: the onboard reports no unsettled attempt and no
    /// proven checkpoint, because from its side the attempt produced a result and was acked, so the
    /// server computes the session as Ready even while it holds an operation in RecoveryRequired.
    /// Observed in `evidence/l2/20260904-real-onboard-resume-after-repair-f0465d9-001`.
    /// </summary>
    /// <summary>
    /// The vehicle standing at the pickup stop with the entry request outstanding: demand accepted,
    /// lease held, no slot operation commanded, session Ready. This is the state an operator is in
    /// when the stop turns out to have nothing to load, and it is the one state the blocked-journey
    /// seed cannot express -- there, a load has already failed.
    /// </summary>
    private static async Task SeedPickupStopAwaitingSublotAsync(ControlServerDbContext context)
    {
        context.AcceptedDemands.Add(new AcceptedDemandRow
        {
            DemandId = DemandId,
            SeriesId = "SERIES-001",
            TransportDemandKey = "SUBLOT-001|WIRE_TO_GATE",
            WorkType = "WIRE_TO_GATE",
            Sublot = "SUBLOT-001",
            Generation = 1,
            DemandRevision = 1,
            HistoryEpoch = "history-1",
            CatalogRevision = 1,
            CreatedAt = Now.AddMinutes(-10),
            ValueObservedAt = Now.AddMinutes(-9),
            ValuePollTraceId = "TRACE-001",
            ValueProjectionCommitId = "COMMIT-001",
            LiveMesFieldsJson = "{}",
            AcceptedAt = Now.AddMinutes(-8),
            Status = DemandExecutionStatus.Accepted
        });
        context.VehicleDispatchLeases.Add(new VehicleDispatchLeaseRow
        {
            JourneyId = JourneyId,
            VehicleKey = "VEHICLE-001",
            AcquiredAt = Now.AddMinutes(-8)
        });
        context.OrderIntents.AddRange(
            Intent("pickup-leg", "UPPER-PICKUP", "TO_PICKUP", 11),
            Intent("gate-leg", "UPPER-GATE", "TO_GATE", 22));
        AddJourney(context, JourneyRuntimeStage.AwaitingSublot, blockReasonCode: null);
        context.ProtocolOutbox.Add(new ProtocolOutboxRow
        {
            // Derived, not a literal: an entry request is identified by the stop and the round that
            // asked, so the cancellation can only find this row if the id matches that derivation.
            MessageId = WireToGateStore.SublotRequestId(JourneyId, 1, 1),
            MessageType = "SublotEntryRequested",
            PayloadJson = "{}",
            CreatedAt = Now.AddMinutes(-2)
        });
        context.SessionRecoveries.Add(new SessionRecoveryRow
        {
            AgvId = AgvId,
            SessionGeneration = 3,
            ProtocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
            ManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            ProfileId = ProtocolCandidateIdentity.ProfileId,
            ProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            CapabilityRevision = 5,
            SafetyRevision = 7,
            DepartureSafe = true,
            RecoveryReportId = "b0000000-0000-4000-8000-000000000001",
            ForcedRecoveryGeneration = 0,
            ReportedForcedRecoveryGeneration = 0,
            PendingAttemptIdsJson = "[]",
            PendingResultIdsJson = "[]",
            ProvenRecoveryCheckpoint = "NONE",
            ActiveUnlockSlotsJson = "[]",
            Readiness = SessionReadiness.Ready,
            ReasonCode = "READY",
            UpdatedAt = Now
        });
        context.VehicleRecoveryGenerations.Add(new VehicleRecoveryGenerationRow
        {
            AgvId = AgvId,
            ForcedRecoveryGeneration = 0,
            UpdatedAt = Now
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task SeedBlockedJourneyAsync(
        ControlServerDbContext context,
        bool productionShapedSession = false)
    {
        context.AcceptedDemands.Add(new AcceptedDemandRow
        {
            DemandId = DemandId,
            SeriesId = "SERIES-001",
            TransportDemandKey = "SUBLOT-001|WIRE_TO_GATE",
            WorkType = "WIRE_TO_GATE",
            Sublot = "SUBLOT-001",
            Generation = 1,
            DemandRevision = 1,
            HistoryEpoch = "history-1",
            CatalogRevision = 1,
            CreatedAt = Now.AddMinutes(-10),
            ValueObservedAt = Now.AddMinutes(-9),
            ValuePollTraceId = "TRACE-001",
            ValueProjectionCommitId = "COMMIT-001",
            LiveMesFieldsJson = "{}",
            AcceptedAt = Now.AddMinutes(-8),
            Status = DemandExecutionStatus.RecoveryRequired
        });
        context.VehicleDispatchLeases.Add(new VehicleDispatchLeaseRow
        {
            JourneyId = JourneyId,
            VehicleKey = "VEHICLE-001",
            AcquiredAt = Now.AddMinutes(-8)
        });
        context.OrderIntents.AddRange(
            Intent("pickup-leg", "UPPER-PICKUP", "TO_PICKUP", 11),
            Intent("gate-leg", "UPPER-GATE", "TO_GATE", 22));
        AddJourney(context);
        context.StationOperations.Add(new StationOperationRow
        {
            SlotOperationAttemptId = AttemptId,
            DemandId = DemandId,
            SublotId = "SUBLOT-001",
            TargetSlotsJson = "[1,2]",
            OperationType = SlotOperationType.Load,
            ForcedRecoveryGeneration = 0,
            ContentHash = new string('a', 64),
            Status = StationOperationStatus.RecoveryRequired,
            CreatedAt = Now.AddMinutes(-5)
        });
        context.SessionRecoveries.Add(new SessionRecoveryRow
        {
            AgvId = AgvId,
            SessionGeneration = 3,
            ProtocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
            ManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            ProfileId = ProtocolCandidateIdentity.ProfileId,
            ProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            CapabilityRevision = 5,
            SafetyRevision = 7,
            // 生产形状里这是 true：L2 证据 db-SessionRecoveries.json 记的就是 DepartureSafe=1。
            // 装载失败本身不让发车变得不安全——门关上了、锁上了、开锁输出复位了，只是货没进去。
            DepartureSafe = productionShapedSession,
            RecoveryReportId = "b0000000-0000-4000-8000-000000000001",
            ForcedRecoveryGeneration = 0,
            ReportedForcedRecoveryGeneration = 0,
            UnsettledSlotOperationAttemptId = productionShapedSession ? null : AttemptId,
            ProvenRecoveryCheckpoint = productionShapedSession ? "NONE" : "PREPARED",
            ActiveUnlockSlotsJson = productionShapedSession ? "[]" : "[1,2]",
            PendingAttemptIdsJson = productionShapedSession
                ? "[]"
                : JsonSerializer.Serialize(new[] { AttemptId }),
            PendingResultIdsJson = "[]",
            Readiness = productionShapedSession
                ? SessionReadiness.Ready
                : SessionReadiness.RecoveryRequired,
            ReasonCode = productionShapedSession ? "READY" : "PENDING_FACT_RECONCILIATION_REQUIRED",
            UpdatedAt = Now
        });
        context.VehicleRecoveryGenerations.Add(new VehicleRecoveryGenerationRow
        {
            AgvId = AgvId,
            ForcedRecoveryGeneration = 0,
            UpdatedAt = Now
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static OrderIntentRow Intent(string legId, string upperId, string purpose, int station) => new()
    {
        MovementLegId = legId,
        DemandId = DemandId,
        UpperId = upperId,
        Purpose = purpose,
        TargetStationId = station.ToString(System.Globalization.CultureInfo.InvariantCulture),
        VehicleKey = "VEHICLE-001",
        MapId = 29,
        DestinationStationId = station,
        AgvLifecycleGeneration = 1,
        DispatchGeneration = 1,
        CreatedAt = Now.AddMinutes(-8),
        Status = "CONFIRMED",
        OrderId = "ORDER-" + purpose
    };

    /// <summary>
    /// One blocked journey in the degenerate single-demand shape: a pickup stop, a gate stop and one
    /// demand loading at the pickup stop. ADR-cross-0057 split what used to be a single row, so
    /// seeding a journey now means seeding three.
    /// </summary>
    private static void AddJourney(
        ControlServerDbContext context,
        JourneyRuntimeStage stage = JourneyRuntimeStage.Blocked,
        string? blockReasonCode = "LOAD_RESULT_REQUIRES_RECOVERY")
    {
        context.JourneyRuntimes.Add(new JourneyRuntimeRow
        {
            JourneyId = JourneyId,
            Stage = stage,
            AgvId = AgvId,
            VehicleKey = "VEHICLE-001",
            AgvLifecycleGeneration = 1,
            MapId = 29,
            MapIdentity = "MAP-29",
            DispatchZone = "ZONE-01",
            GateStationId = "GATE",
            GateStationRiotId = 22,
            OperationSessionId = "c0000000-0000-4000-8000-000000000001",
            DispatchGeneration = 1,
            CurrentStopSequence = 1,
            VehicleBusinessRevision = 3,
            WorklistRevision = 3,
            PlanRevision = 3,
            BlockReasonCode = blockReasonCode,
            CreatedAt = Now.AddMinutes(-8),
            UpdatedAt = Now
        });
        context.JourneyStops.AddRange(
            new JourneyStopRow
            {
                JourneyId = JourneyId,
                Sequence = 1,
                Role = JourneyStopRole.Pickup,
                StationId = "PICKUP",
                StationRiotId = 11,
                RouteEvidenceId = "ROUTE-01",
                MovementLegId = "pickup-leg",
                UpperId = "UPPER-PICKUP",
                LegType = "TO_PICKUP",
                State = JourneyStopState.Arrived,
                VehicleBusinessRevision = 1,
                WorklistRevision = 1,
                PlanRevision = 1,
                VehicleBusinessMessageId = "d0000000-0000-4000-8000-000000000001",
                PlanMessageId = "d0000000-0000-4000-8000-000000000003",
                PreDepartureSafetyCheckMessageId = "d0000000-0000-4000-8000-000000000006",
                PreDepartureSafetyCheckId = "d0000000-0000-4000-8000-000000000007",
                LoadRound = 1,
                CreatedAt = Now.AddMinutes(-8),
                UpdatedAt = Now
            },
            new JourneyStopRow
            {
                JourneyId = JourneyId,
                Sequence = 9,
                Role = JourneyStopRole.Gate,
                StationId = "GATE",
                StationRiotId = 22,
                RouteEvidenceId = "ROUTE-01",
                MovementLegId = "gate-leg",
                UpperId = "UPPER-GATE",
                LegType = "TO_GATE",
                State = JourneyStopState.Planned,
                VehicleBusinessRevision = 2,
                WorklistRevision = 2,
                PlanRevision = 2,
                VehicleBusinessMessageId = "d0000000-0000-4000-8000-000000000008",
                PlanMessageId = "d0000000-0000-4000-8000-000000000010",
                PreDepartureSafetyCheckMessageId = "d0000000-0000-4000-8000-000000000013",
                PreDepartureSafetyCheckId = "d0000000-0000-4000-8000-000000000014",
                LoadRound = 0,
                CreatedAt = Now.AddMinutes(-8),
                UpdatedAt = Now
            });
        context.JourneyDemands.Add(new JourneyDemandRow
        {
            JourneyId = JourneyId,
            DemandId = DemandId,
            StopSequence = 1,
            ExpectedBasketCount = 2,
            TargetSlotsJson = "[1,2]",
            LoadCommandMessageId = "d0000000-0000-4000-8000-000000000005",
            LoadSlotOperationAttemptId = AttemptId,
            UnloadCommandMessageId = "d0000000-0000-4000-8000-000000000011",
            UnloadSlotOperationAttemptId = "d0000000-0000-4000-8000-000000000012",
            State = JourneyDemandState.Planned,
            CreatedAt = Now.AddMinutes(-8)
        });
    }

    private static string RecoverySessionRequest(string proof) => Envelope(
        "e0000000-0000-4000-8000-000000000001",
        "ExceptionRecoverySessionRequested",
        new
        {
            requestId = RequestId,
            administrator = Operator(),
            administratorRole = "MAINTENANCE_ADMINISTRATOR",
            eventId = EventId,
            demandId = DemandId,
            slots = RecoverySlots,
            reason = "Recover the blocked load operation.",
            authenticationProof = proof
        });

    private static string RecoveryAction(
        string action,
        string reason = "Use current persisted facts.",
        string messageId = "e0000000-0000-4000-8000-000000000002",
        string? actionId = null) => Envelope(
        messageId,
        "RecoveryActionSubmitted",
        new
        {
            recoveryActionId = actionId ?? ActionId,
            exceptionRecoverySessionId = StableGuid(RequestId, "exception-recovery-session"),
            action,
            eventId = EventId,
            demandId = DemandId,
            slots = RecoverySlots,
            @operator = Operator(),
            reason
        });

    private static object Operator() => new
    {
        operatorId = OperatorId,
        verificationMethod = "BADGE",
        verifiedAt = Now
    };

    /// <param name="determinate">
    /// Which kind of failure this is, once ADR-cross-0058 decision 5 split the two. A determinate
    /// failure names the physical state it ended in, so the server settles it as
    /// StationOperationStatus.Failed and nobody is asked to recover anything. The default is the
    /// other half -- a slot whose state the vehicle could not establish -- which is what still
    /// needs a recovery handshake, and what every caller of this helper is testing.
    /// </param>
    private static object OperationResultPayload(
        bool completed = true,
        int[]? slots = null,
        string journalCheckpoint = "RESULT_RECORDED",
        bool determinate = false)
    {
        object[] slotResults = (slots ?? RecoverySlots).Select(slot => (object)new
        {
            slotNo = slot,
            outcome = completed ? "COMPLETED" : "FAILED",
            finalPhysicalState = completed ? "OCCUPIED" : determinate ? "EMPTY" : "UNKNOWN",
            lockState = "LOCKED",
            unlockOutputState = "RESET",
            reasonCodes = completed ? Array.Empty<string>() : FailedSlotReasonCodes
        }).ToArray();
        var withoutHash = new
        {
            demandId = DemandId,
            slotOperationAttemptId = AttemptId,
            operationType = "LOAD",
            overallOutcome = completed ? "COMPLETED" : "FAILED",
            slotResults,
            observedAt = Now.AddSeconds(1),
            journalCheckpoint
        };
        // The peer hashes these CLR values directly, before anything reaches the wire. Round-tripping
        // them through a JsonElement first only reproduced what the server itself used to do, so the
        // two sides agreed here while disagreeing on any timestamp that carries an offset.
        byte[] businessContent = JsonSerializer.SerializeToUtf8Bytes(withoutHash, SerializerOptions);
        return new
        {
            withoutHash.demandId,
            withoutHash.slotOperationAttemptId,
            withoutHash.operationType,
            withoutHash.overallOutcome,
            withoutHash.slotResults,
            withoutHash.observedAt,
            withoutHash.journalCheckpoint,
            resultContentSha256 = Convert.ToHexString(SHA256.HashData(businessContent)).ToLowerInvariant()
        };
    }

    private static string Envelope(
        string messageId,
        string messageType,
        object payload,
        string? correlationId = null) =>
        JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
            protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
            messageType,
            messageId,
            correlationId,
            agvId = AgvId,
            sessionGeneration = 3,
            sentAt = Now,
            payload
        }, SerializerOptions);

    private static string MessageType(string wire)
    {
        string first = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
        using JsonDocument document = JsonDocument.Parse(first);
        return document.RootElement.GetProperty("messageType").GetString()!;
    }

    private static async Task<ControlServerDbContext> CreateContextAsync(SqliteConnection connection)
    {
        ControlServerDbContext context = new(new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return context;
    }

    private static string StableGuid(string identity, string purpose)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{identity}|{purpose}"));
        Span<byte> guidBytes = bytes.AsSpan(0, 16);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes).ToString("D");
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class RecordingPeer(ControlServerDbContext context) : IOnboardPeer
    {
        public List<string> Lines { get; } = [];
        public List<int> OutboxCountsAtSend { get; } = [];

        public async Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken)
        {
            OutboxCountsAtSend.Add(await context.ProtocolOutbox.CountAsync(cancellationToken));
            Lines.Add(Encoding.UTF8.GetString(ndjsonLine.Span));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
