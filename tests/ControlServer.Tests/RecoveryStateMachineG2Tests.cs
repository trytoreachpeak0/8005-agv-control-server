using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
                // The vehicle resumes the exact command it journaled and refuses anything else, so the
                // resume authorization names that command by its own content hash. Until 2026-09-14 it
                // carried a hash of the recovery action instead, which no vehicle could match: the
                // real onboard answered every resume with RECOVERY_STATE_MISMATCH (G3 resume-004).
                using (JsonDocument commandDocument = JsonDocument.Parse(firstCommand))
                {
                    Assert.Equal(
                        (await firstContext.StationOperations.SingleAsync(TestContext.Current.CancellationToken)).ContentHash,
                        commandDocument.RootElement.GetProperty("payload").GetProperty("commandContentSha256").GetString());
                }
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
                // Read back rather than asserted on `recoverySnapshot`: OnboardMessageProcessor clears
                // this context's tracking at the top of every message (8005-agv-control-server#28/#40),
                // so the instance loaded before the call is no longer the one the ack wrote.
                Assert.NotNull((await firstContext.ProtocolOutbox.SingleAsync(
                    row => row.MessageId == recoverySnapshot.MessageId,
                    TestContext.Current.CancellationToken)).AcknowledgedAt);
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
            // The same persisted command, but not sent again: the replacement OperationResult above
            // answered it, and a resume whose authorization is spent could only be refused
            // (8005-agv-control-server#78). Before that settlement this replay re-sent it.
            ProtocolOutboxRow resumeCommand = await restartedContext.ProtocolOutbox.SingleAsync(
                row => row.MessageType == "SlotOperationResumeCommand",
                TestContext.Current.CancellationToken);
            Assert.Equal(firstCommand, resumeCommand.PayloadJson + "\n");
            Assert.NotNull(resumeCommand.AcknowledgedAt);
            Assert.DoesNotContain(restartedPeer.Lines, line => MessageType(line) == "SlotOperationResumeCommand");
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

    /// <summary>
    /// A resume that commits settles the attempt the vehicle reported as unsettled at its handshake, so
    /// the session becomes ready again and the vehicle is told in the same response.
    /// </summary>
    /// <remarks>
    /// Before 2026-09-14 the reported attempt stayed on the session after the resume had committed it:
    /// the session read PENDING_FACT_RECONCILIATION_REQUIRED until the vehicle reconnected, and the
    /// journey sat in AwaitingLoadResult on ONBOARD_SESSION_NOT_READY over a committed load (G3 FP-IS-07
    /// resume-007).
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ASuccessfulResumeSettlesTheReportedAttemptAndTellsTheVehicleItIsReady()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_RESUME_READY";
        const string proof = "resume-ready-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            // The failed load left the doors closed and locked; nothing about it makes departure unsafe.
            (await context.SessionRecoveries.SingleAsync(token)).DepartureSafe = true;
            await context.SaveChangesAsync(token);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            await AuthorizeResumeAfterFailedResultAsync(processor, context, state, proof);

            string response = await processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000000021",
                    "OperationResult",
                    OperationResultPayload(journalCheckpoint: "RESUME_RESULT_RECORDED")),
                state,
                token);
            await processor.FlushDeferredOutboundAsync(state, token);

            string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Equal("DurableAck", MessageType(lines[0]));
            Assert.Equal("SessionReadiness", MessageType(lines[1]));
            using (JsonDocument readiness = JsonDocument.Parse(lines[1]))
            {
                Assert.Equal("READY", readiness.RootElement.GetProperty("payload").GetProperty("readiness").GetString());
            }
            SessionRecoveryRow session = await context.SessionRecoveries.SingleAsync(token);
            Assert.Equal("[]", session.PendingAttemptIdsJson);
            Assert.Null(session.UnsettledSlotOperationAttemptId);
            Assert.Equal(SessionReadiness.Ready, session.Readiness);
            Assert.Equal(SessionReadiness.Ready, state.Readiness);
            Assert.Equal(StationOperationStatus.Committed, (await context.StationOperations.SingleAsync(token)).Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A compensation that proves every slot empty settles the load, so the session is decided again and
    /// the vehicle is told it is ready in the response to its result.
    /// </summary>
    /// <remarks>
    /// Before 2026-09-14 no recovery result decided readiness at all: after a reconciled compensation
    /// the session still read OPERATION_RECOVERY_REQUIRED (G3 FP-IS-07 compensate-001,
    /// db-SessionRecoveries.json), and the vehicle stayed out of work until some unrelated safety
    /// change or reconnect decided it again.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationThatProvesEverySlotEmptyTellsTheVehicleItIsReadyAgain()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_COMPENSATE_READY";
        const string proof = "compensate-ready-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            (await context.SessionRecoveries.SingleAsync(token)).DepartureSafe = true;
            await context.SaveChangesAsync(token);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
            await processor.ProcessAsync(RecoveryAction("COMPENSATE_LOAD_ALL_EMPTY"), state, token);
            await processor.ProcessAsync(
                Envelope(
                    "90000000-0000-4000-8000-000000000011",
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
                token);
            Assert.Single(peer.Lines, line => MessageType(line) == "LoadCompensationCommand");

            string response = await processor.ProcessAsync(
                Envelope(
                    "a0000000-0000-4000-8000-000000000011",
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
                        observedAt = Now.AddSeconds(3)
                    }),
                state,
                token);

            string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Equal("DurableAck", MessageType(lines[0]));
            Assert.Equal("SessionReadiness", MessageType(lines[1]));
            using (JsonDocument readiness = JsonDocument.Parse(lines[1]))
            {
                Assert.Equal("READY", readiness.RootElement.GetProperty("payload").GetProperty("readiness").GetString());
            }
            Assert.Equal(RecoveryWorkflowState.Reconciled, (await context.RecoveryWorkflows.SingleAsync(token)).State);
            Assert.Equal(StationOperationStatus.Cancelled, (await context.StationOperations.SingleAsync(token)).Status);
            SessionRecoveryRow session = await context.SessionRecoveries.SingleAsync(token);
            Assert.Equal("[]", session.PendingAttemptIdsJson);
            Assert.Null(session.UnsettledSlotOperationAttemptId);
            Assert.Equal(SessionReadiness.Ready, session.Readiness);
            Assert.Equal(SessionReadiness.Ready, state.Readiness);
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
    /// 8005-agv-control-server#30 for the five recovery results, carried to v2 as 8005-agv-program#61
    /// group (1). The vehicle never got the DurableAck for a recovery result, so it reconnects and
    /// resends that result under the new session generation -- ADR-cross-0030 has a resend keep its
    /// messageId. Until this was fixed the inbox refused the line as a content conflict and the server
    /// dropped the connection; the vehicle reconnected, resent, and was dropped again, so a
    /// compensation could never be closed out. The answer is the first acceptance signed for this
    /// session, and nothing about the workflow moves a second time.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationResultResentInTheNextSessionIsAcknowledgedFromItsFirstAcceptance()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_COMPENSATE_REPLAY";
        const string proof = "compensation-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState();
            string original = await ReachCompensationResultAsync(processor, state, proof);
            string firstAck = await processor.ProcessAsync(original, state, token);
            Assert.Equal("DurableAck", MessageType(firstAck));

            // The vehicle reconnects: the session row moves to the next generation the way the handshake
            // moves it, and the result is resent with that generation and nothing else changed.
            await AdvanceSessionGenerationAsync(context, state, 4);
            JsonNode reboundNode = JsonNode.Parse(original)!;
            reboundNode["sessionGeneration"] = 4;
            string rebound = reboundNode.ToJsonString();

            string replay = await processor.ProcessAsync(rebound, state, token);

            using JsonDocument acknowledgement = JsonDocument.Parse(
                replay.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
            Assert.Equal("DurableAck", acknowledgement.RootElement.GetProperty("messageType").GetString());
            Assert.Equal(4, acknowledgement.RootElement.GetProperty("sessionGeneration").GetInt64());
            JsonElement ack = acknowledgement.RootElement.GetProperty("payload");
            Assert.Equal("LoadCompensationResult", ack.GetProperty("acceptedMessageType").GetString());
            Assert.Equal(WireContentHash(rebound), ack.GetProperty("acceptedContentSha256").GetString());
            Assert.Single(await context.RecoveryResultEvidence.ToArrayAsync(token));
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired,
                (await context.RecoveryWorkflows.SingleAsync(token)).State);
            Assert.Single(await context.ProtocolOutbox
                .Where(row => row.MessageType == "LoadCompensationCommand")
                .ToArrayAsync(token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The inbox is the one place a resend's equivalence is decided. ProcessResultAsync decided it a
    /// second time, on the whole line's hash -- and a resend changes that hash by definition, because
    /// rebinding sessionGeneration is exactly what it does. Two verdicts on one question is one too
    /// many: whichever path got there second turned a resend the inbox had already accepted back into a
    /// dropped connection. What belongs here is the identity of the record, not the bytes that carried
    /// it: the same messageId must still name the same workflow and the same kind of record.
    /// </summary>
    /// <remarks>
    /// This one deliberately calls the coordinator directly instead of sending a line, and that is the
    /// only way to reach what it covers. On the real inbound path the inbox answers the resend first
    /// (ACompensationResultResentInTheNextSessionIsAcknowledgedFromItsFirstAcceptance is that path, and
    /// it goes through OnboardMessageProcessor), so the branch under test here is unreachable from
    /// production -- it is the defensive residue the method keeps for any caller that skips the inbox.
    /// Reaching past a seam is worth it exactly once, for a branch that would otherwise be asserted by
    /// nothing and would quietly regress to judging bytes again.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AResentRecoveryResultIsNotJudgedASecondTimeOnItsWireHash()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_COMPENSATE_SECOND_VERDICT";
        const string proof = "compensation-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState();
            string original = await ReachCompensationResultAsync(processor, state, proof);
            await processor.ProcessAsync(original, state, token);

            await AdvanceSessionGenerationAsync(context, state, 4);
            JsonNode reboundNode = JsonNode.Parse(original)!;
            reboundNode["sessionGeneration"] = 4;
            string rebound = reboundNode.ToJsonString();
            OnboardRecoveryCoordinator coordinator = TestOnboardProcessorFactory.CreateRecoveryCoordinator(
                context, new WireToGateStore(context), new FixedTimeProvider(Now), Configuration(proofVariable), peer);
            using JsonDocument reboundDocument = JsonDocument.Parse(rebound);

            string ack = await coordinator.ProcessResultAsync(
                reboundDocument.RootElement, WireContentHash(rebound), token);

            Assert.Equal("DurableAck", MessageType(ack));
            Assert.Single(await context.RecoveryResultEvidence.ToArrayAsync(token));

            // The same messageId turning up as a different kind of record is still a conflict, and that
            // is what is left here to refuse.
            JsonNode otherKind = JsonNode.Parse(rebound)!;
            otherKind["messageType"] = "ForcedMechanicalRecoveryResult";
            string otherKindLine = otherKind.ToJsonString();
            using JsonDocument otherDocument = JsonDocument.Parse(otherKindLine);
            await Assert.ThrowsAsync<ProtocolContentConflictException>(() => coordinator.ProcessResultAsync(
                otherDocument.RootElement, WireContentHash(otherKindLine), token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// REQ-0237：普通放错只在离站前纠正，离站后将错就错。服务端此前只看装载是否 Committed 就授权修正，
    /// 车已经被派去关卡照样发 LoadCorrectionCommand——G3 FP-IS-02 实跑里车载端因此在车辆被判为未停稳时收到
    /// 开门命令，只能以 VEHICLE_NOT_READY 拒绝，修正工作流就一直挂在 AwaitingResult。授权面现在以旅程是否
    /// 仍在取货点等待离站为准。
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    [InlineData(JourneyRuntimeStage.AwaitingStationDeparture, true)]
    [InlineData(JourneyRuntimeStage.AwaitingDepartureSafety, false)]
    [InlineData(JourneyRuntimeStage.AwaitingGateArrival, false)]
    public async Task LoadCorrectionIsAuthorizedOnlyWhileTheVehicleStillWaitsAtThePickup(
        JourneyRuntimeStage stage,
        bool authorized)
    {
        const string correctionId = "b2000000-0000-4000-8000-000000000001";
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCorrectableLoadAsync(context, stage);
        OnboardMessageProcessor processor = Processor(
            context, new RecordingPeer(context), CancellationProofVariable);

        string response = await processor.ProcessAsync(
            CorrectionRequest(correctionId), CurrentState(), TestContext.Current.CancellationToken);

        RecoveryWorkflowRow[] workflows = await context.RecoveryWorkflows
            .ToArrayAsync(TestContext.Current.CancellationToken);
        int commands = await context.ProtocolOutbox.CountAsync(
            row => row.MessageType == "LoadCorrectionCommand", TestContext.Current.CancellationToken);
        if (authorized)
        {
            RecoveryWorkflowRow workflow = Assert.Single(workflows);
            Assert.Equal("LOAD_CORRECTION", workflow.WorkflowType);
            Assert.Equal(1, commands);
        }
        else
        {
            Assert.Equal("LoadCorrectionRejected", MessageType(response));
            Assert.Empty(workflows);
            Assert.Equal(0, commands);
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

    /// <summary>
    /// Protocol 2.0.0 item 7, the half with a value: a session opened on a demand whose load had
    /// been commanded names that load's attempt in <c>ExceptionRecoverySessionOpened</c>, in its
    /// snapshot, and in <c>RecoveryActionAccepted</c> -- the same one each time, and the one
    /// compensation is later authorized against (8005-agv-program#95).
    /// </summary>
    /// <remarks>
    /// Without it the vehicle could not build <c>LoadCompensationRequested</c>: the attempt is
    /// required there, and the vehicle's own record of it is cleared by the time recovery is needed.
    /// That is MVP control-server#5 on the v2 line.
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("ExceptionRecoverySessionOpened")]
    [InlineData("ExceptionRecoverySessionSnapshot")]
    [InlineData("RecoveryActionAccepted")]
    public async Task EachRecoveryMessageNamesTheAttemptOfTheLoadTheSessionIsAbout(string messageType)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_ATTEMPT_NAMED";
        const string proof = "attempt-named-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState();

            string opened = await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
            string accepted = await processor.ProcessAsync(
                RecoveryAction("COMPENSATE_LOAD_ALL_EMPTY"), state, token);

            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(opened));
            Assert.Equal("RecoveryActionAccepted", MessageType(accepted));
            string?[] named = await NamedAttemptsAsync(context, messageType, opened, accepted);
            Assert.NotEmpty(named);
            Assert.All(named, attempt => Assert.Equal(AttemptId, attempt));
            // The one compensation is authorized against.
            Assert.Equal(AttemptId, (await context.RecoveryWorkflows.SingleAsync(token)).SlotOperationAttemptId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// Protocol 2.0.0 item 7, the null half: a session that is about no demand has no slot operation
    /// to point at, and all three messages say null rather than borrowing an attempt from the demand
    /// the vehicle happens to hold.
    /// </summary>
    /// <remarks>
    /// On v2 today this is the only way a recovery session precedes a load: opening one on a demand
    /// requires that demand's slot operation (<c>RECOVERY_SCOPE_MISMATCH</c> otherwise), so a session
    /// on a demand whose load never started is refused before any of these messages exists. The rule
    /// the server applies is the same either way -- no operation commanded at the session's opening,
    /// no attempt named.
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("ExceptionRecoverySessionOpened")]
    [InlineData("ExceptionRecoverySessionSnapshot")]
    [InlineData("RecoveryActionAccepted")]
    public async Task EachRecoveryMessageOfASessionAboutNoLoadNamesNoAttempt(string messageType)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_ATTEMPT_NULL";
        const string proof = "attempt-null-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState();

            string opened = await processor.ProcessAsync(
                RecoverySessionRequest(proof, demandId: null), state, token);
            string accepted = await processor.ProcessAsync(
                RecoveryAction("FORCED_MECHANICAL_RECOVERY", demandId: null), state, token);

            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(opened));
            Assert.Equal("RecoveryActionAccepted", MessageType(accepted));
            string?[] named = await NamedAttemptsAsync(context, messageType, opened, accepted);
            Assert.NotEmpty(named);
            Assert.All(named, attempt => Assert.Null(attempt));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The attempt a session names is fixed at its first value. A slot operation for the same demand
    /// created after the session opened changes neither a replayed <c>ExceptionRecoverySessionOpened</c>
    /// nor the next snapshot. The session itself is then stale, and an action on it is refused whole with
    /// <c>RECOVERY_SCOPE_MISMATCH</c> -- including the two actions whose preconditions look at no operation
    /// state -- rather than accepted against a load the demand has moved past.
    /// </summary>
    /// <remarks>
    /// Since 8005-agv-control-server#78 the attempt the action is recorded against and the one the
    /// messages name come from one lookup, so they cannot differ; the refusal here is the separate
    /// staleness check. The accepting case, with no later operation and one attempt throughout, is
    /// <see cref="EachRecoveryMessageNamesTheAttemptOfTheLoadTheSessionIsAbout"/>.
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("FORCED_MECHANICAL_RECOVERY")]
    [InlineData("FAULT_CARGO_HANDOFF")]
    public async Task ASessionKeepsItsFirstAttemptAndRefusesAnActionOnceTheDemandHasMovedPastIt(string action)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_ATTEMPT_FIXED";
        const string proof = "attempt-fixed-proof-not-a-production-secret";
        const string laterAttemptId = "20000000-0000-4000-8000-000000000099";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState();
            string opened = await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
            Assert.Equal(AttemptId, PayloadAttempt(opened));

            context.StationOperations.Add(new StationOperationRow
            {
                SlotOperationAttemptId = laterAttemptId,
                DemandId = DemandId,
                SublotId = "SUBLOT-001",
                TargetSlotsJson = "[1,2]",
                OperationType = SlotOperationType.Unload,
                ForcedRecoveryGeneration = 0,
                ContentHash = new string('b', 64),
                Status = StationOperationStatus.Prepared,
                CreatedAt = Now.AddMinutes(1)
            });
            await context.SaveChangesAsync(token);

            string replayed = await processor.ProcessAsync(
                RecoverySessionRequest(proof, messageId: "e0000000-0000-4000-8000-000000000031"), state, token);
            string refused = await processor.ProcessAsync(RecoveryAction(action), state, token);

            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(replayed));
            Assert.Equal(AttemptId, PayloadAttempt(replayed));
            Assert.Equal("RecoveryActionRejected", MessageType(refused));
            using (JsonDocument document = JsonDocument.Parse(refused))
            {
                Assert.Equal(ServerReasonCodes.RecoveryScopeMismatch, document.RootElement.GetProperty("payload")
                    .GetProperty("problem").GetProperty("reasonCode").GetString());
            }
            Assert.Empty(await context.RecoveryWorkflows.ToArrayAsync(token));
            Assert.Null((await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).SelectedAction);
            Assert.All(await SessionSnapshotAttemptsAsync(context), attempt => Assert.Equal(AttemptId, attempt));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A compensation result answers the command that asked for it, whatever it says. The command has no
    /// ack of its own (<c>LoadCompensationCommandAck</c> is on the profile denylist), and the workflow takes
    /// no second result, so a command left pending could only be replayed into a later session to draw a
    /// duplicate.
    /// </summary>
    /// <remarks>
    /// 8005-agv-control-server#78, from 8005-agv-program#61 (MVP <c>219b033f</c>). Before it a failed
    /// compensation's command went out again in every session that followed, because the workflow sits in
    /// RecoveryRequired and the outbox row was never settled; a reconciled one was no longer replayed by the
    /// coordinator but its row stayed pending for the life of the database.
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACompensationResultSettlesItsCommandSoTheNextSessionDoesNotReplayIt(bool allEmpty)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_COMPENSATE_SETTLES";
        const string proof = "compensate-settles-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState();
            string failed = await ReachCompensationResultAsync(processor, state, proof);
            string result = allEmpty ? AllEmptyCompensationResult(failed) : failed;
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(result, state, token)));
            Assert.Equal(
                allEmpty ? RecoveryWorkflowState.Reconciled : RecoveryWorkflowState.RecoveryRequired,
                (await context.RecoveryWorkflows.SingleAsync(token)).State);

            await AdvanceSessionGenerationAsync(context, state, 4);
            peer.Lines.Clear();
            await processor.ProcessAsync(RecoveryStateReport(4, unsettledAttemptId: null), state, token);

            Assert.DoesNotContain(peer.Lines, line => MessageType(line) == "LoadCompensationCommand");
            Assert.NotNull((await context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(row => row.MessageType == "LoadCompensationCommand", token)).AcknowledgedAt);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The replacement OperationResult is a resume's answer, so it settles the
    /// <c>SlotOperationResumeCommand</c> the same way a recovery result settles its command -- whether
    /// the resume committed the load or failed it again.
    /// </summary>
    /// <remarks>
    /// A failed replacement leaves the workflow in RecoveryRequired, which the coordinator's replay
    /// scan still takes in; the resume went out again on the next reconnect and could only be refused,
    /// because its authorization was spent on the first replacement (8005-agv-control-server#78).
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-05")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AResumeResultSettlesTheResumeCommandSoTheNextSessionDoesNotReplayIt(bool completed)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_RESUME_SETTLES";
        const string proof = "resume-settles-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState();
            await AuthorizeResumeAfterFailedResultAsync(processor, context, state, proof);
            await processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000000041",
                    "OperationResult",
                    OperationResultPayload(completed: completed, journalCheckpoint: "RESUME_RESULT_RECORDED")),
                state,
                token);
            Assert.Equal(
                completed ? RecoveryWorkflowState.Reconciled : RecoveryWorkflowState.RecoveryRequired,
                (await context.RecoveryWorkflows.SingleAsync(token)).State);

            await AdvanceSessionGenerationAsync(context, state, 4);
            peer.Lines.Clear();
            await processor.ProcessAsync(RecoveryStateReport(4, unsettledAttemptId: null), state, token);

            Assert.DoesNotContain(peer.Lines, line => MessageType(line) == "SlotOperationResumeCommand");
            Assert.NotNull((await context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(row => row.MessageType == "SlotOperationResumeCommand", token)).AcknowledgedAt);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A load correction asked for on the same connection that just delivered a resume's result is judged
    /// on the journey as it is stored, after the runtime has moved it on, not as that connection's
    /// DbContext last saw it.
    /// </summary>
    /// <remarks>
    /// 8005-agv-program#61 (MVP <c>74019789</c>) on the v2 line. The replacement OperationResult reads and
    /// tracks the journey to put it back to AwaitingLoadResult; the runtime then advances it to
    /// AwaitingStationDeparture from its own context. Refusing the correction on the tracked stage would
    /// refuse it inside the one window REQ-0237 allows. Green without a coordinator change since
    /// 8005-agv-control-server#77 clears the connection's tracking at the top of every inbound message;
    /// verified red with that clear removed.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task ALoadCorrectionAfterAResumeOnTheSameConnectionIsJudgedOnTheStoredStage()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_RESUME_THEN_CORRECT";
        const string proof = "resume-then-correct-proof-not-a-production-secret";
        const string correctionId = "b2000000-0000-4000-8000-000000000041";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState();
            await AuthorizeResumeAfterFailedResultAsync(processor, context, state, proof);
            await processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000000042",
                    "OperationResult",
                    OperationResultPayload(journalCheckpoint: "RESUME_RESULT_RECORDED")),
                state,
                token);
            Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult,
                (await context.JourneyRuntimes.SingleAsync(token)).Stage);

            // The runtime worker, on a context of its own: the committed load moves the journey on to wait
            // at the pickup.
            await using (ControlServerDbContext runtimeContext = await CreateContextAsync(connection))
            {
                JourneyRuntimeRow runtime = await runtimeContext.JourneyRuntimes.SingleAsync(token);
                runtime.Stage = JourneyRuntimeStage.AwaitingStationDeparture;
                runtime.UpdatedAt = Now.AddSeconds(5);
                await runtimeContext.SaveChangesAsync(token);
            }

            string response = await processor.ProcessAsync(CorrectionRequest(correctionId), state, token);

            Assert.Equal(string.Empty, response);
            Assert.Equal("LOAD_CORRECTION", (await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == correctionId, token)).WorkflowType);
            Assert.Equal(1, await context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "LoadCorrectionCommand", token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A handshake report may name as unsettled an attempt this server settled long ago, with no result
    /// left to arrive for it. The session is decided on what the server holds: a committed or cancelled
    /// operation is settled and the session is ready; one with no conclusion yet still holds it.
    /// </summary>
    /// <remarks>
    /// 8005-agv-program#61 residual of MVP <c>369919f5</c>. Reported attempts were settled only when a
    /// result arrived in the session, and the report itself was taken as sent, so this session stayed on
    /// PENDING_FACT_RECONCILIATION_REQUIRED until some later result or reconnect -- and in this case none
    /// comes (8005-agv-control-server#78).
    /// </remarks>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData(StationOperationStatus.Committed, "READY")]
    [InlineData(StationOperationStatus.Cancelled, "READY")]
    [InlineData(StationOperationStatus.Prepared, "RECOVERY_REQUIRED")]
    public async Task AReportNamingAnAttemptTheServerAlreadySettledNeedsNoFurtherResult(
        StationOperationStatus status,
        string expectedReadiness)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedBlockedJourneyAsync(context, productionShapedSession: true);
        (await context.StationOperations.SingleAsync(token)).Status = status;
        JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(token);
        runtime.Stage = JourneyRuntimeStage.AwaitingStationDeparture;
        runtime.BlockReasonCode = null;
        await context.SaveChangesAsync(token);
        OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), CancellationProofVariable);
        OnboardConnectionState state = CurrentState();
        await AdvanceSessionGenerationAsync(context, state, 4);

        string response = await processor.ProcessAsync(
            RecoveryStateReport(4, unsettledAttemptId: AttemptId), state, token);

        string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using (JsonDocument readiness = JsonDocument.Parse(lines[1]))
        {
            Assert.Equal(expectedReadiness,
                readiness.RootElement.GetProperty("payload").GetProperty("readiness").GetString());
        }
        SessionRecoveryRow session = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
        if (expectedReadiness == "READY")
        {
            Assert.Equal("[]", session.PendingAttemptIdsJson);
            Assert.Null(session.UnsettledSlotOperationAttemptId);
        }
        else
        {
            Assert.Contains(AttemptId, session.PendingAttemptIdsJson, StringComparison.Ordinal);
            Assert.Equal(AttemptId, session.UnsettledSlotOperationAttemptId);
        }
    }

    /// <summary>
    /// Protocol 2.0.0 item 3: <c>LoadCancellationResult.slotResults</c> may be empty, which is what a
    /// cancellation before anything was loaded reports. Inbound parsing must take it, whatever was
    /// authorized: here the cancellation named an attempt with two slots, so the empty result is received,
    /// recorded and acknowledged rather than thrown on. What an empty result settles when the
    /// authorization named no slot is JourneyRuntimeWorkerTests' CV-LOAD-CANCELLATION-BEFORE-LOAD test
    /// (8005-agv-control-server#83).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ALoadCancellationResultWithNoSlotResultsIsReceivedAndAcknowledged()
    {
        const string cancellationId = "b2000000-0000-4000-8000-000000000001";
        const string resultMessageId = "b2000000-0000-4000-8000-000000000002";
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCancellableLoadAsync(context);
        OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), CancellationProofVariable);
        OnboardConnectionState state = CurrentState();
        await processor.ProcessAsync(CancellationRequest(cancellationId), state, token);

        string ack = await processor.ProcessAsync(
            Envelope(
                resultMessageId,
                "LoadCancellationResult",
                new
                {
                    cancellationId,
                    demandId = DemandId,
                    slotOperationAttemptId = AttemptId,
                    overallOutcome = "ALL_EMPTY",
                    slotResults = Array.Empty<object>(),
                    observedAt = Now.AddSeconds(4)
                }),
            state,
            token);

        Assert.Equal("DurableAck", MessageType(ack));
        Assert.Single(await context.RecoveryResultEvidence.ToArrayAsync(token));
    }

    /// <summary>
    /// What the given recovery message said: the response line for the two answers, every queued
    /// revision for the snapshot (the session queues one when it opens and one when an action is taken).
    /// </summary>
    private static async Task<string?[]> NamedAttemptsAsync(
        ControlServerDbContext context,
        string messageType,
        string opened,
        string accepted)
    {
        if (messageType == "ExceptionRecoverySessionOpened") return [PayloadAttempt(opened)];
        if (messageType == "RecoveryActionAccepted") return [PayloadAttempt(accepted)];
        Assert.Equal("ExceptionRecoverySessionSnapshot", messageType);
        string?[] snapshots = await SessionSnapshotAttemptsAsync(context);
        Assert.Equal(2, snapshots.Length);
        return snapshots;
    }

    private static string? PayloadAttempt(string wire)
    {
        string first = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
        using JsonDocument document = JsonDocument.Parse(first);
        JsonElement attempt = document.RootElement.GetProperty("payload").GetProperty("slotOperationAttemptId");
        return attempt.ValueKind == JsonValueKind.Null ? null : attempt.GetString();
    }

    private static async Task<string?[]> SessionSnapshotAttemptsAsync(ControlServerDbContext context) =>
    [
        .. (await context.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == "ExceptionRecoverySessionSnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .OrderBy(row => row.CreatedAt)
            .Select(row => PayloadAttempt(row.PayloadJson))
    ];

    private static OnboardMessageProcessor Processor(
        ControlServerDbContext context,
        IOnboardPeer peer,
        string proofVariable)
    {
        WireToGateStore store = new(context);
        return TestOnboardProcessorFactory.Create(
            context, store, new FixedTimeProvider(Now), Configuration(proofVariable), peer);
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

    /// <summary>
    /// Drives a compensation as far as the line the vehicle is about to send: session opened, action
    /// selected, command issued. Returns the LoadCompensationResult envelope, unsent.
    /// </summary>
    private static async Task<string> ReachCompensationResultAsync(
        OnboardMessageProcessor processor,
        OnboardConnectionState state,
        string proof)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
        await processor.ProcessAsync(RecoveryAction("COMPENSATE_LOAD_ALL_EMPTY"), state, token);
        await processor.ProcessAsync(
            Envelope(
                "90000000-0000-4000-8000-000000000021",
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
            token);
        return Envelope(
            "a0000000-0000-4000-8000-000000000021",
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
                    }
                },
                observedAt = Now.AddSeconds(3)
            });
    }

    /// <summary>
    /// The unsent line from <see cref="ReachCompensationResultAsync"/>, turned into a compensation that
    /// proves both authorized slots empty.
    /// </summary>
    private static string AllEmptyCompensationResult(string failedResult)
    {
        JsonNode node = JsonNode.Parse(failedResult)!;
        node["payload"]!["overallOutcome"] = "ALL_EMPTY";
        node["payload"]!["slotResults"] = JsonSerializer.SerializeToNode(RecoverySlots.Select(slot => new
        {
            slotNo = slot,
            outcome = "COMPLETED",
            finalPhysicalState = "EMPTY",
            lockState = "LOCKED",
            unlockOutputState = "RESET",
            reasonCodes = Array.Empty<string>()
        }).ToArray(), SerializerOptions);
        return node.ToJsonString();
    }

    /// <summary>
    /// The handshake report of a vehicle that has reconnected into <paramref name="generation"/>, with no
    /// result of its own left to send.
    /// </summary>
    private static string RecoveryStateReport(long generation, string? unsettledAttemptId)
    {
        JsonNode node = JsonNode.Parse(Envelope(
            "f2000000-0000-4000-8000-" + generation.ToString("D12", System.Globalization.CultureInfo.InvariantCulture),
            "RecoveryStateReport",
            new
            {
                reportId = "f3000000-0000-4000-8000-" +
                           generation.ToString("D12", System.Globalization.CultureInfo.InvariantCulture),
                unsettledSlotOperationAttemptId = unsettledAttemptId,
                provenRecoveryCheckpoint = unsettledAttemptId is null ? "NONE" : "SAFE_FINISH_REACHED",
                activeUnlockSlots = Array.Empty<int>(),
                forcedRecoveryGeneration = 0,
                pendingResults = Array.Empty<object>()
            }))!;
        node["sessionGeneration"] = generation;
        return node.ToJsonString();
    }

    /// <summary>
    /// What the handshake does to the session row when the vehicle comes back: the generation moves on,
    /// and the connection is told which one it is now.
    /// </summary>
    private static async Task AdvanceSessionGenerationAsync(
        ControlServerDbContext context,
        OnboardConnectionState state,
        long generation)
    {
        SessionRecoveryRow session = await context.SessionRecoveries.SingleAsync(
            TestContext.Current.CancellationToken);
        session.SessionGeneration = generation;
        session.UpdatedAt = Now;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        state.SessionGeneration = generation;
    }

    private static IConfiguration Configuration(string proofVariable) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Recovery:AuthenticationProofEnvironmentVariable"] = proofVariable
        })
        .Build();

    private static string WireContentHash(string line) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(line))).ToLowerInvariant();

    private static string RecoverySessionRequest(
        string proof,
        string? demandId = DemandId,
        string messageId = "e0000000-0000-4000-8000-000000000001") => Envelope(
        messageId,
        "ExceptionRecoverySessionRequested",
        new
        {
            requestId = RequestId,
            administrator = Operator(),
            administratorRole = "MAINTENANCE_ADMINISTRATOR",
            eventId = EventId,
            demandId,
            slots = RecoverySlots,
            reason = "Recover the blocked load operation.",
            authenticationProof = proof
        });

    private static string RecoveryAction(
        string action,
        string reason = "Use current persisted facts.",
        string messageId = "e0000000-0000-4000-8000-000000000002",
        string? actionId = null,
        string? demandId = DemandId) => Envelope(
        messageId,
        "RecoveryActionSubmitted",
        new
        {
            recoveryActionId = actionId ?? ActionId,
            exceptionRecoverySessionId = StableGuid(RequestId, "exception-recovery-session"),
            action,
            eventId = EventId,
            demandId,
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

    /// <summary>
    /// 修正面的起点：装载已经提交、需求仍在执行，旅程停在 <paramref name="stage"/>。
    /// </summary>
    private static async Task SeedCorrectableLoadAsync(ControlServerDbContext context, JourneyRuntimeStage stage)
    {
        await SeedBlockedJourneyAsync(context, productionShapedSession: true);
        (await context.AcceptedDemands.SingleAsync(TestContext.Current.CancellationToken)).Status =
            DemandExecutionStatus.Accepted;
        StationOperationRow load = await context.StationOperations.SingleAsync(TestContext.Current.CancellationToken);
        load.Status = StationOperationStatus.Committed;
        load.CommittedAt = Now.AddMinutes(-1);
        JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(TestContext.Current.CancellationToken);
        runtime.Stage = stage;
        runtime.BlockReasonCode = null;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static string CorrectionRequest(string correctionId) => Envelope(
        "b2000000-0000-4000-8000-000000000010",
        "LoadCorrectionRequested",
        new
        {
            correctionId,
            demandId = DemandId,
            slotOperationAttemptId = AttemptId,
            slots = RecoverySlots,
            @operator = Operator(),
            reason = "The basket in slot 1 belongs to another sublot."
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
