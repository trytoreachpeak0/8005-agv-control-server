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
    private const string AttemptId = "20000000-0000-4000-8000-000000000001";
    private const string EventId = "30000000-0000-4000-8000-000000000001";
    private const string RequestId = "40000000-0000-4000-8000-000000000001";
    private const string ActionId = "50000000-0000-4000-8000-000000000001";
    private const string OperatorId = "maintenance-001";
    // 取消面不开异常恢复会话，走不到 RecoveryProofAccepted；这个名字只是为了让 Processor
    // 拿到一个确定不会命中的变量名，而不是让某个别处设过的环境变量意外生效。
    private const string CancellationProofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_CANCELLATION";

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
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
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
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
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
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
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
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
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-CONNECTION-LOSS-SAFE-FINISH")]
    [Trait("ProtocolVector", "CV-OPERATION-RESULT-UNKNOWN-RECONCILE")]
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
    [Trait("IntegrationSlice", "FP-IS-00")]
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
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
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
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
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
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
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
            Assert.Single(await context.ProtocolOutbox.Where(
                    row => row.MessageType == "LoadCompensationCommand")
                .ToArrayAsync(TestContext.Current.CancellationToken));

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

    /// <summary>
    /// CV-LOAD-CANCELLATION-ALL-EMPTY 对服务端要两件事，此前只证了一件。
    /// FailedCompensationResultIsDurableReplayableAndNeverReleasesDemandOrVehicle 走的是 REJECTED
    /// 分支——补偿还没收敛时取消必须被拒——那证的是 AUTHORIZE_CANCELLATION_EXPLICITLY。
    /// 另一件 RECONCILE_EMPTY_FINAL_STATE 是：取消被批准之后，只有每个仓位都证到 EMPTY 才收敛
    /// 需求、释放租约、收尾旅程。两段共用同一次授权，差别只在一个仓位的 finalPhysicalState，
    /// 所以第二段同时是第一段的 vacuity proof：EMPTY 换成 OCCUPIED，收敛就不发生。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task AuthorizedLoadCancellationReconcilesOnlyWhenEverySlotIsProvenEmpty()
    {
        const string cancellationId = "b1000000-0000-4000-8000-000000000001";
        const string resultMessageId = "b1000000-0000-4000-8000-000000000002";

        await using (SqliteConnection provenEmpty = new("Data Source=:memory:"))
        {
            await provenEmpty.OpenAsync(TestContext.Current.CancellationToken);
            await using ControlServerDbContext context = await CreateContextAsync(provenEmpty);
            await SeedCancellableLoadAsync(context);
            OnboardMessageProcessor processor = Processor(
                context, new RecordingPeer(context), CancellationProofVariable);
            OnboardConnectionState state = CurrentState();

            string authorization = await processor.ProcessAsync(
                CancellationRequest(cancellationId), state, TestContext.Current.CancellationToken);
            using (JsonDocument document = JsonDocument.Parse(authorization))
            {
                Assert.Equal("LoadCancellationAuthorization",
                    document.RootElement.GetProperty("messageType").GetString());
                Assert.Equal("AUTHORIZED", document.RootElement.GetProperty("payload")
                    .GetProperty("decision").GetString());
            }
            Assert.Equal(RecoveryWorkflowState.AwaitingResult, (await context.RecoveryWorkflows.SingleAsync(
                TestContext.Current.CancellationToken)).State);

            string ack = await processor.ProcessAsync(
                CancellationResult(cancellationId, resultMessageId, "EMPTY"),
                state,
                TestContext.Current.CancellationToken);

            Assert.Equal("DurableAck", MessageType(ack));
            Assert.Equal(RecoveryWorkflowState.Reconciled, (await context.RecoveryWorkflows.SingleAsync(
                TestContext.Current.CancellationToken)).State);
            Assert.Equal(DemandExecutionStatus.Cancelled, (await context.AcceptedDemands.SingleAsync(
                TestContext.Current.CancellationToken)).Status);
            Assert.NotNull((await context.VehicleDispatchLeases.SingleAsync(
                TestContext.Current.CancellationToken)).ReleasedAt);
            Assert.Equal(StationOperationStatus.Cancelled, (await context.StationOperations.SingleAsync(
                TestContext.Current.CancellationToken)).Status);
            JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
            Assert.Equal("CANCELLED_BY_OPERATOR", runtime.BlockReasonCode);
        }

        await using (SqliteConnection oneSlotStillOccupied = new("Data Source=:memory:"))
        {
            await oneSlotStillOccupied.OpenAsync(TestContext.Current.CancellationToken);
            await using ControlServerDbContext context = await CreateContextAsync(oneSlotStillOccupied);
            await SeedCancellableLoadAsync(context);
            OnboardMessageProcessor processor = Processor(
                context, new RecordingPeer(context), CancellationProofVariable);
            OnboardConnectionState state = CurrentState();

            // 这一段也断言授权确实是 AUTHORIZED，而不是靠上一段。否则授权面一旦回退成
            // REJECTED，下面那些「没有发生」的断言就是在一个根本没建起来的工作流上做的。
            string authorization = await processor.ProcessAsync(
                CancellationRequest(cancellationId), state, TestContext.Current.CancellationToken);
            using (JsonDocument document = JsonDocument.Parse(authorization))
            {
                Assert.Equal("AUTHORIZED", document.RootElement.GetProperty("payload")
                    .GetProperty("decision").GetString());
            }
            string ack = await processor.ProcessAsync(
                CancellationResult(cancellationId, resultMessageId, "OCCUPIED"),
                state,
                TestContext.Current.CancellationToken);

            // 报文照样落库并被确认——不收敛不等于不记录，否则车载端会一直重发。
            Assert.Equal("DurableAck", MessageType(ack));
            Assert.Single(await context.RecoveryResultEvidence.ToArrayAsync(
                TestContext.Current.CancellationToken));
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, (await context.RecoveryWorkflows.SingleAsync(
                TestContext.Current.CancellationToken)).State);
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await context.AcceptedDemands.SingleAsync(
                TestContext.Current.CancellationToken)).Status);
            Assert.Null((await context.VehicleDispatchLeases.SingleAsync(
                TestContext.Current.CancellationToken)).ReleasedAt);
            Assert.Equal(StationOperationStatus.Prepared, (await context.StationOperations.SingleAsync(
                TestContext.Current.CancellationToken)).Status);
            JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(
                TestContext.Current.CancellationToken);
            Assert.Equal(JourneyRuntimeStage.Blocked, runtime.Stage);
            Assert.Equal("LoadCancellationResult_NOT_RECONCILED", runtime.BlockReasonCode);
            Assert.Empty(await context.TransportDemandCompletions.ToArrayAsync(
                TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
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
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
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
            DemandId = DemandId,
            VehicleKey = "VEHICLE-001",
            AcquiredAt = Now.AddMinutes(-8)
        });
        context.OrderIntents.AddRange(
            Intent("pickup-leg", "UPPER-PICKUP", "TO_PICKUP", 11),
            Intent("gate-leg", "UPPER-GATE", "TO_GATE", 22));
        context.JourneyRuntimes.Add(Runtime());
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

    private static JourneyRuntimeRow Runtime() => new()
    {
        DemandId = DemandId,
        Stage = JourneyRuntimeStage.Blocked,
        AgvId = AgvId,
        VehicleKey = "VEHICLE-001",
        AgvLifecycleGeneration = 1,
        MapId = 29,
        MapIdentity = "MAP-29",
        DispatchZone = "ZONE-01",
        RouteEvidenceId = "ROUTE-01",
        PickupStationId = "PICKUP",
        PickupStationRiotId = 11,
        GateStationId = "GATE",
        GateStationRiotId = 22,
        ExpectedBasketCount = 2,
        TargetSlotsJson = "[1,2]",
        OperationSessionId = "c0000000-0000-4000-8000-000000000001",
        PickupMovementLegId = "pickup-leg",
        PickupUpperId = "UPPER-PICKUP",
        GateMovementLegId = "gate-leg",
        GateUpperId = "UPPER-GATE",
        DispatchGeneration = 1,
        VehicleBusinessRevision = 1,
        WorklistRevision = 1,
        PlanRevision = 1,
        VehicleBusinessMessageId = "d0000000-0000-4000-8000-000000000001",
        WorklistMessageId = "d0000000-0000-4000-8000-000000000002",
        PlanMessageId = "d0000000-0000-4000-8000-000000000003",
        SublotRequestMessageId = "d0000000-0000-4000-8000-000000000004",
        LoadCommandMessageId = "d0000000-0000-4000-8000-000000000005",
        LoadSlotOperationAttemptId = AttemptId,
        PreDepartureSafetyCheckMessageId = "d0000000-0000-4000-8000-000000000006",
        PreDepartureSafetyCheckId = "d0000000-0000-4000-8000-000000000007",
        GateVehicleBusinessMessageId = "d0000000-0000-4000-8000-000000000008",
        GateWorklistMessageId = "d0000000-0000-4000-8000-000000000009",
        GatePlanMessageId = "d0000000-0000-4000-8000-000000000010",
        UnloadCommandMessageId = "d0000000-0000-4000-8000-000000000011",
        UnloadSlotOperationAttemptId = "d0000000-0000-4000-8000-000000000012",
        BlockReasonCode = "LOAD_RESULT_REQUIRES_RECOVERY",
        CreatedAt = Now.AddMinutes(-8),
        UpdatedAt = Now
    };

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

    /// <summary>
    /// 取消面的起点不是恢复态。SeedBlockedJourneyAsync 造的是「装载失败、等恢复」，
    /// AuthorizeLoadCancellationAsync 在那个状态下必然 REJECTED——那正是既有那条测试证的一半。
    /// 要证另一半就得先站到它批准得下去的状态：需求仍被接受、仓位操作还没进恢复。
    /// </summary>
    private static async Task SeedCancellableLoadAsync(ControlServerDbContext context)
    {
        await SeedBlockedJourneyAsync(context);
        (await context.AcceptedDemands.SingleAsync(TestContext.Current.CancellationToken)).Status =
            DemandExecutionStatus.Accepted;
        (await context.StationOperations.SingleAsync(TestContext.Current.CancellationToken)).Status =
            StationOperationStatus.Prepared;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string CancellationRequest(string cancellationId) => Envelope(
        "b1000000-0000-4000-8000-000000000010",
        "LoadCancellationStartRequested",
        new
        {
            cancellationId,
            demandId = DemandId,
            slotOperationAttemptId = AttemptId,
            @operator = Operator(),
            reason = "Operator cancelled the load before any basket entered."
        });

    private static string CancellationResult(
        string cancellationId,
        string messageId,
        string secondSlotFinalPhysicalState) => Envelope(
        messageId,
        "LoadCancellationResult",
        new
        {
            cancellationId,
            demandId = DemandId,
            slotOperationAttemptId = AttemptId,
            overallOutcome = "ALL_EMPTY",
            slotResults = new[]
            {
                CancellationSlotResult(1, "EMPTY"),
                CancellationSlotResult(2, secondSlotFinalPhysicalState)
            },
            observedAt = Now.AddSeconds(4)
        });

    // overallOutcome 说 ALL_EMPTY 而某个仓位仍报 OCCUPIED，正是 unknown-as-success 那类
    // forbiddenSideEffect 要挡住的形状：判据看的是逐仓位的 finalPhysicalState，不是那句总结。
    private static object CancellationSlotResult(int slotNo, string finalPhysicalState) => new
    {
        slotNo,
        outcome = "COMPLETED",
        finalPhysicalState,
        lockState = "LOCKED",
        unlockOutputState = "RESET",
        reasonCodes = Array.Empty<string>()
    };

    private static object OperationResultPayload(
        bool completed = true,
        int[]? slots = null,
        string journalCheckpoint = "RESULT_RECORDED")
    {
        object[] slotResults = (slots ?? RecoverySlots).Select(slot => (object)new
        {
            slotNo = slot,
            outcome = completed ? "COMPLETED" : "FAILED",
            finalPhysicalState = completed ? "OCCUPIED" : "EMPTY",
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
