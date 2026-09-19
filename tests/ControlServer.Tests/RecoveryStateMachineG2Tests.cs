using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ControlServer.Tests;

public sealed class RecoveryStateMachineG2Tests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly int[] RecoverySlots = [1, 2];
    private static readonly string[] UnknownReasonCodes = ["PHYSICAL_STATE_UNKNOWN"];
    // The fixed values onboard-hmi#107 sends; the server only files them.
    private static readonly string[] HardwareChecks = ["LIVE_SLOT_SIGNALS_VALID"];
    private static readonly string[] HardwareActions = ["ADMINISTRATOR_CONFIRMED_HARDWARE_REPAIRED"];
    private static readonly string[] HardwareObservations = ["Lock 1 replaced; both doors shut and read locked."];
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
    // A fault cargo result that concludes HANDED_OFF while one slot still reports OCCUPIED (control-server#169).
    private const string HandedOffOverAnOccupiedSlot = "HANDED_OFF_OVER_AN_OCCUPIED_SLOT";

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
                OperationResultPayload(completed: false, journalCheckpoint: "RESULT_UNKNOWN_RECORDED")),
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
                OperationResultPayload(completed: false, journalCheckpoint: "RESULT_UNKNOWN_RECORDED")),
            state,
            TestContext.Current.CancellationToken);
        Assert.Equal(response, replay);
    }

    /// <summary>
    /// ADR-cross-0058 decision 5, on the wire: a load that ran out its station deadline and came back FAILED
    /// with every slot empty, locked and reset -- the failed slot under <c>OPERATOR_TIMEOUT</c>, the other
    /// never started -- is a complete account. The operation is Failed, the demand is left for the runtime to
    /// end, and the vehicle is not told its session needs recovery: the result is acknowledged on one line.
    /// Until control-server#81 this same result was judged RecoveryRequired, which with
    /// <c>recoveryResumeEnabled=false</c> left the vehicle with no way out. Defensive: the v2 onboard does not
    /// produce it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ADeterminateLoadFailureAfterTheStationDeadlineAsksNoRecovery()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedLoadAwaitingResultAsync(context, waitStartedAt: Now.AddMinutes(-6));
        OnboardMessageProcessor processor = DeadlineAwareProcessor(context, new RecordingLogger<OnboardMessageProcessor>());
        OnboardConnectionState state = CurrentState();
        state.Readiness = SessionReadiness.Ready;

        string response = await processor.ProcessAsync(
            Envelope("e0000000-0000-4000-8000-000000000031", "OperationResult", DeterminateLoadFailurePayload()),
            state,
            token);

        Assert.Equal("DurableAck", MessageType(Assert.Single(response.Split('\n', StringSplitOptions.RemoveEmptyEntries))));
        Assert.Equal(SessionReadiness.Ready, state.Readiness);
        StationOperationRow operation = await context.StationOperations.AsNoTracking().SingleAsync(token);
        Assert.Equal(StationOperationStatus.Failed, operation.Status);
        Assert.NotNull(operation.EvidenceJson);
        Assert.Equal(DemandExecutionStatus.Accepted, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
        Assert.Empty(await context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(token));
        Assert.Equal(
            SessionReadiness.Ready,
            (await context.SessionRecoveries.AsNoTracking().SingleAsync(token)).Readiness);
    }

    /// <summary>
    /// The same complete-looking FAILED, where the server cannot explain it: it arrived before the station
    /// deadline, which a determinate failure only ever follows, or its failed slot names a reason the
    /// protocol gives no terminal state (only <c>OPERATOR_TIMEOUT</c> may sit on an OperationResult). Neither
    /// is accepted silently: the operation and demand go to recovery, the vehicle is told, and the server logs
    /// why under a reason code.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData(-1, "OPERATOR_TIMEOUT", "LOAD_FAILED_BEFORE_STATION_DEADLINE")]
    [InlineData(-6, "ACTION_NOT_ALLOWED_IN_STATE", "LOAD_FAILURE_REASON_WITHOUT_TERMINAL_STATE")]
    public async Task ADeterminateLookingFailureTheServerCannotExplainGoesToRecoveryUnderItsReasonCode(
        int waitStartedMinutesAgo,
        string failedSlotReasonCode,
        string expectedReasonCode)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedLoadAwaitingResultAsync(context, waitStartedAt: Now.AddMinutes(waitStartedMinutesAgo));
        RecordingLogger<OnboardMessageProcessor> log = new();
        OnboardMessageProcessor processor = DeadlineAwareProcessor(context, log);
        OnboardConnectionState state = CurrentState();
        state.Readiness = SessionReadiness.Ready;

        string response = await processor.ProcessAsync(
            Envelope(
                "e0000000-0000-4000-8000-000000000032",
                "OperationResult",
                DeterminateLoadFailurePayload(failedSlotReasonCode)),
            state,
            token);

        string[] lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["DurableAck", "SessionReadiness"], lines.Select(MessageType));
        Assert.Equal(SessionReadiness.RecoveryRequired, state.Readiness);
        Assert.Equal(
            StationOperationStatus.RecoveryRequired,
            (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status);
        Assert.Equal(
            DemandExecutionStatus.RecoveryRequired,
            (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
        (LogLevel level, string message) = Assert.Single(log.Entries, entry => entry.Message.Contains(expectedReasonCode));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains(AttemptId, message);
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
            DemandExecutionStatus demandBefore = (await context.AcceptedDemands.AsNoTracking().SingleAsync(
                TestContext.Current.CancellationToken)).Status;
            StationOperationStatus operationBefore = (await context.StationOperations.AsNoTracking().SingleAsync(
                TestContext.Current.CancellationToken)).Status;

            string lateAck = await processor.ProcessAsync(
                Envelope(
                    "70000000-0000-4000-8000-000000000001",
                    "OperationResult",
                    OperationResultPayload()),
                state,
                TestContext.Current.CancellationToken);
            // The fenced generation's late result is evidence only: it moves neither the demand nor the
            // operation, even though it reports a completed load.
            Assert.Equal(demandBefore, (await context.AcceptedDemands.AsNoTracking().SingleAsync(
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(operationBefore, (await context.StationOperations.AsNoTracking().SingleAsync(
                TestContext.Current.CancellationToken)).Status);
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
            // The late OperationResult of the fenced generation changed nothing; the current-generation
            // forced result settles the cargo's business as a handoff (control-server#137) -- never as a
            // completed transport. Until #137 it left the demand and the workflow in RecoveryRequired.
            Assert.Equal(RecoveryWorkflowState.Reconciled, workflow.State);
            Assert.Equal(DemandExecutionStatus.Cancelled, (await context.AcceptedDemands.SingleAsync(
                TestContext.Current.CancellationToken)).Status);
            Assert.Equal(StationOperationStatus.Cancelled, (await context.StationOperations.SingleAsync(
                TestContext.Current.CancellationToken)).Status);
            Assert.Empty(await context.TransportDemandCompletions.ToArrayAsync(
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// REQ-0242, business half (control-server#137). A forced mechanical recovery on a demand-bearing
    /// session is the named handoff of ForcedCargoHandoffRecord: the product is the demand's own bound
    /// cargo, and the verified <c>operator</c> the result carries is the named person. Protocol 2.0.0 has no
    /// field for an unknown identity, so the "pending inventory" branch is unreachable here. The demand ends
    /// the way a fault cargo handoff ends it (CONTEXT.md, FaultCargoRecoveryRecord), the vehicle is released
    /// from the journey, and the session closes -- so the same vehicle can open another one. Until #137 the
    /// session stayed EXECUTING forever and every later session request was refused.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedRecoveryWithANamedHandoffEndsTheDemandAndClosesTheSessionSoTheVehicleCanOpenAnother()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_FORCED_SETTLEMENT";
        const string proof = "forced-settlement-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            OrderIntentRow pickup = await context.OrderIntents.SingleAsync(row => row.UpperId == "UPPER-PICKUP", token);
            pickup.VehicleOccupancyClaimedAt = Now.AddMinutes(-8);
            await context.SaveChangesAsync(token);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
            await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);

            string ack = await processor.ProcessAsync(MechanicallyIsolatedResult(generation: 1), state, token);

            Assert.Equal("DurableAck", MessageType(ack));
            Assert.Equal(RecoveryWorkflowState.Reconciled, (await context.RecoveryWorkflows.SingleAsync(token)).State);
            Assert.Equal(DemandExecutionStatus.Cancelled, (await context.AcceptedDemands.SingleAsync(token)).Status);
            Assert.NotNull((await context.VehicleDispatchLeases.SingleAsync(token)).ReleasedAt);
            Assert.Equal(StationOperationStatus.Cancelled, (await context.StationOperations.SingleAsync(token)).Status);
            JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(token);
            Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
            Assert.Equal("TERMINATED_BY_FAULT_CARGO_HANDOFF", runtime.BlockReasonCode);
            Assert.NotNull((await context.OrderIntents.AsNoTracking()
                .SingleAsync(row => row.UpperId == "UPPER-PICKUP", token)).VehicleOccupancyReleasedAt);
            Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.SingleAsync(token)).State);
            Assert.Equal("CLOSED", await LatestSessionSnapshotStateAsync(context));

            JsonNode next = JsonNode.Parse(RecoverySessionRequest(
                proof, demandId: null, messageId: "e0000000-0000-4000-8000-000000000031"))!;
            next["payload"]!["requestId"] = "41000000-0000-4000-8000-000000000001";
            next["payload"]!["eventId"] = "31000000-0000-4000-8000-000000000001";
            string opened = await processor.ProcessAsync(next.ToJsonString(), state, token);

            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(opened));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// REQ-0241/0242, device half (control-server#137). Ending the cargo's business proves nothing about
    /// the slots: once the forced result settled the operation, every other readiness input -- the vehicle
    /// reporting the new forced generation, nothing pending, departure safe -- says Ready, and without this
    /// hold the vehicle would be handed work on slots nobody has proved. It stays RecoveryRequired until a
    /// HardwareRecoveryRecord for the forced workflow arrives (ADR-cross-0036), and that record is taken
    /// against the session the forced result closed. A record for another scope is refused and lifts
    /// nothing. The record lifts only this hold; it resumes nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AfterAForcedRecoveryTheVehicleStaysUnreadyUntilAHardwareRecoveryRecordForItArrives()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_FORCED_HARDWARE";
        const string proof = "forced-hardware-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
            await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);
            await processor.ProcessAsync(MechanicallyIsolatedResult(generation: 1), state, token);
            // The vehicle comes back having adopted the new generation, with nothing of its own left open.
            await store.ApplyRecoveryReportAsync(
                AgvId, 3, "f0000000-0000-4000-8000-000000000137", forcedRecoveryGeneration: 1,
                null, "NONE", [], [], [], token);

            SessionReadinessDecision held = await store.DecideReadinessAsync(AgvId, 3, token);

            Assert.Equal(SessionReadiness.RecoveryRequired, held.Readiness);
            Assert.Equal("FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED", held.ReasonCode);
            Assert.Equal("SESSION_RECOVERY_REQUIRED", ProtocolErrorCodes.ToSessionReadinessReasonCode(held.ReasonCode));

            string refused = await processor.ProcessAsync(
                HardwareRecoveryRecord("e1000000-0000-4000-8000-000000000001", slots: [1]), state, token);
            Assert.Equal("REJECTED", FirstPayload(refused).GetProperty("outcome").GetString());
            Assert.Equal("FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED",
                (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);

            state.Readiness = SessionReadiness.RecoveryRequired;
            string recorded = await processor.ProcessAsync(
                HardwareRecoveryRecord("e1000000-0000-4000-8000-000000000002", slots: RecoverySlots), state, token);

            Assert.Equal("HardwareRecoveryRecordResult", MessageType(recorded));
            Assert.Equal("RECORDED", FirstPayload(recorded).GetProperty("outcome").GetString());
            Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.SingleAsync(token)).State);
            string[] lines = recorded.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);
            Assert.Equal("SessionReadiness", MessageType(lines[1]));
            Assert.Equal(SessionReadiness.Ready, (await store.DecideReadinessAsync(AgvId, 3, token)).Readiness);
            Assert.Equal(JourneyRuntimeStage.Completed, (await context.JourneyRuntimes.SingleAsync(token)).Stage);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A hardware record attests to what was found after the forced recovery, so it can only follow the
    /// forced result. Taken while the forced command is still pending, it would attest to nothing -- the doors
    /// had not yet been forced -- and yet, once the result arrived, it lifted the hardware hold at the vehicle's
    /// next report, with no hardware confirmation after the forcing at all. It is refused and lifts nothing;
    /// the same record taken after the result is accepted and lifts the hold.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AHardwareRecoveryRecordTakenBeforeTheForcedResultIsRefusedAndLiftsNothing()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_FORCED_EARLY_RECORD";
        const string proof = "forced-early-record-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
            await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);

            string early = await processor.ProcessAsync(
                HardwareRecoveryRecord("e1000000-0000-4000-8000-000000000011", slots: RecoverySlots), state, token);

            Assert.Equal("REJECTED", FirstPayload(early).GetProperty("outcome").GetString());
            Assert.Empty(await context.HardwareRecoveryRecords.ToArrayAsync(token));

            await processor.ProcessAsync(MechanicallyIsolatedResult(generation: 1), state, token);
            await store.ApplyRecoveryReportAsync(
                AgvId, 3, "f0000000-0000-4000-8000-000000000138", forcedRecoveryGeneration: 1,
                null, "NONE", [], [], [], token);
            Assert.Equal("FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED",
                (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);

            string recorded = await processor.ProcessAsync(
                HardwareRecoveryRecord("e1000000-0000-4000-8000-000000000012", slots: RecoverySlots), state, token);

            Assert.Equal("RECORDED", FirstPayload(recorded).GetProperty("outcome").GetString());
            Assert.Equal(SessionReadiness.Ready, (await store.DecideReadinessAsync(AgvId, 3, token)).Readiness);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A forced recovery that the vehicle reports as FAILED settles nothing, but it did force the doors all the
    /// same: its result is on file, so a hardware record against it is accepted. Requiring the result before
    /// the record must not turn into requiring a successful result.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AHardwareRecoveryRecordIsAcceptedAgainstAForcedRecoveryThatFailed()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_FORCED_FAILED_RECORD";
        const string proof = "forced-failed-record-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
            await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);
            await processor.ProcessAsync(MechanicallyIsolatedResult(generation: 1, outcome: "FAILED"), state, token);
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, (await context.RecoveryWorkflows.SingleAsync(token)).State);

            string recorded = await processor.ProcessAsync(
                HardwareRecoveryRecord("e1000000-0000-4000-8000-000000000021", slots: RecoverySlots), state, token);

            Assert.Equal("RECORDED", FirstPayload(recorded).GetProperty("outcome").GetString());
            Assert.Single(await context.HardwareRecoveryRecords.ToArrayAsync(token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A hardware record is about one vehicle's hardware. One sent over another vehicle's connection names a
    /// workflow that is not that vehicle's, and is refused.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AHardwareRecoveryRecordForAnotherVehiclesForcedRecoveryIsRefused()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_FORCED_OTHER_AGV";
        const string proof = "forced-other-agv-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
            await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);
            await processor.ProcessAsync(MechanicallyIsolatedResult(generation: 1), state, token);
            // The forced workflow belongs to a different vehicle than the one now submitting the record.
            (await context.RecoveryWorkflows.SingleAsync(token)).AgvId = "AGV-8005-99";
            await context.SaveChangesAsync(token);

            string refused = await processor.ProcessAsync(
                HardwareRecoveryRecord("e1000000-0000-4000-8000-000000000031", slots: RecoverySlots), state, token);

            Assert.Equal("REJECTED", FirstPayload(refused).GetProperty("outcome").GetString());
            Assert.Empty(await context.HardwareRecoveryRecords.ToArrayAsync(token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A forced workflow that a later forced generation made history of is covered by that later one, and its
    /// own missing hardware record no longer holds the vehicle.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedRecoveryMadeHistoricalNoLongerHoldsTheVehicleForItsHardwareRecord()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_FORCED_HISTORICAL";
        const string proof = "forced-historical-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
            await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);
            await processor.ProcessAsync(MechanicallyIsolatedResult(generation: 1), state, token);
            await store.ApplyRecoveryReportAsync(
                AgvId, 3, "f0000000-0000-4000-8000-000000000139", forcedRecoveryGeneration: 1,
                null, "NONE", [], [], [], token);
            Assert.Equal("FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED",
                (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);
            // The state a superseding forced generation leaves an earlier workflow in.
            (await context.RecoveryWorkflows.SingleAsync(token)).State = RecoveryWorkflowState.HistoricalOnly;
            await context.SaveChangesAsync(token);

            Assert.Equal(SessionReadiness.Ready, (await store.DecideReadinessAsync(AgvId, 3, token)).Readiness);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#169, decided by the user on 2026-09-19. A recovery action whose result does not
    /// reconcile -- FAILED, UNKNOWN, or a success conclusion the per-slot results do not bear out -- leaves the
    /// business exactly where it was: the demand in RecoveryRequired, the journey Blocked under
    /// <c>&lt;messageType&gt;_NOT_RECONCILED</c>, the lease and the vehicle held, nothing ended. What it must not
    /// leave is the session in EXECUTING. The result has arrived, so there is nothing left to execute, and until
    /// #169 that state refused every way out: a second action in the session, a new session on the vehicle, and
    /// a snapshot that told the vehicle to wait for a result it had already sent. The session closes under
    /// <c>RECOVERY_ACTION_RESULT_NOT_RECONCILED</c> -- which is not "handled" -- and the administrator opens a new
    /// one on the same demand, on the same attempt.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("LoadCompensationResult", "FAILED")]
    [InlineData("LoadCompensationResult", "UNKNOWN")]
    [InlineData("FaultCargoRecoveryResult", "FAILED")]
    [InlineData("FaultCargoRecoveryResult", "UNKNOWN")]
    [InlineData("FaultCargoRecoveryResult", HandedOffOverAnOccupiedSlot)]
    [InlineData("ForcedMechanicalRecoveryResult", "FAILED")]
    [InlineData("ForcedMechanicalRecoveryResult", "UNKNOWN")]
    public async Task AnUnreconciledRecoveryResultClosesTheSessionSoTheVehicleCanOpenAnotherWhileTheDemandStaysBlocked(
        string messageType,
        string outcome)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_UNRECONCILED_CLOSES";
        const string proof = "unreconciled-closes-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            OrderIntentRow pickup = await context.OrderIntents.SingleAsync(row => row.UpperId == "UPPER-PICKUP", token);
            pickup.VehicleOccupancyClaimedAt = Now.AddMinutes(-8);
            await context.SaveChangesAsync(token);
            RecordingLogger<OnboardRecoveryCoordinator> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), new FixedTimeProvider(Now), Configuration(proofVariable),
                new RecordingPeer(context), recoveryLogger: log);
            OnboardConnectionState state = CurrentState();
            string result = await ReachUnreconciledResultAsync(messageType, outcome, processor, state, proof);

            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(result, state, token)));

            ExceptionRecoverySessionRow closed = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            Assert.Equal("CLOSED", closed.State);
            JsonElement snapshot = await LatestSessionSnapshotAsync(context, closed.ExceptionRecoverySessionId);
            Assert.Equal("CLOSED", snapshot.GetProperty("state").GetString());
            Assert.Equal(closed.Revision, snapshot.GetProperty("recoverySessionRevision").GetInt64());
            Assert.Empty(snapshot.GetProperty("allowedActions").EnumerateArray());
            Assert.Empty(snapshot.GetProperty("blockingFacts").EnumerateArray());
            Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains("RECOVERY_ACTION_RESULT_NOT_RECONCILED", StringComparison.Ordinal) &&
                entry.Message.Contains(closed.ExceptionRecoverySessionId, StringComparison.Ordinal));

            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, (await context.RecoveryWorkflows.SingleAsync(token)).State);
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await context.AcceptedDemands.SingleAsync(token)).Status);
            JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(token);
            Assert.Equal(JourneyRuntimeStage.Blocked, runtime.Stage);
            Assert.Equal(messageType + "_NOT_RECONCILED", runtime.BlockReasonCode);
            Assert.Null((await context.VehicleDispatchLeases.SingleAsync(token)).ReleasedAt);
            Assert.Null((await context.OrderIntents.AsNoTracking()
                .SingleAsync(row => row.UpperId == "UPPER-PICKUP", token)).VehicleOccupancyReleasedAt);
            Assert.Equal(StationOperationStatus.RecoveryRequired, (await context.StationOperations.SingleAsync(token)).Status);
            Assert.Empty(await context.TransportDemandCompletions.ToArrayAsync(token));

            string opened = await processor.ProcessAsync(NextSessionRequest(proof), state, token);

            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(opened));
            Assert.Equal(AttemptId, PayloadAttempt(opened));
            Assert.Equal(2, await context.ExceptionRecoverySessions.CountAsync(token));
            Assert.Equal(JourneyRuntimeStage.Blocked, (await context.JourneyRuntimes.SingleAsync(token)).Stage);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#169. The vehicle resends an unreconciled result it got no acknowledgement for. The resend
    /// is answered with the first DurableAck and moves nothing: the session that result closed keeps its
    /// revision and gets no second closing snapshot.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AResentUnreconciledResultIsAcknowledgedFromTheFirstAndDoesNotCloseTheSessionAgain()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_UNRECONCILED_RESENT";
        const string proof = "unreconciled-resent-proof-not-a-production-secret";
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
            string result = await ReachCompensationResultAsync(processor, state, proof);
            string firstAck = await processor.ProcessAsync(result, state, token);
            long closedRevision = (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).Revision;
            int snapshots = await context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "ExceptionRecoverySessionSnapshot", token);

            string resentAck = await processor.ProcessAsync(result, state, token);

            Assert.Equal("DurableAck", MessageType(firstAck));
            Assert.Equal(firstAck, resentAck);
            ExceptionRecoverySessionRow session = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            Assert.Equal("CLOSED", session.State);
            Assert.Equal(closedRevision, session.Revision);
            Assert.Equal(snapshots, await context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "ExceptionRecoverySessionSnapshot", token));
            Assert.Single(await context.RecoveryResultEvidence.ToArrayAsync(token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#169. Protocol 2.0.0 lets the same action be submitted again in a session that is still
    /// executing, each under its own recoveryActionId. The first of them to report closes the session; a later
    /// one reporting into the CLOSED session is recorded against its own workflow, and does not close the
    /// session a second time -- no new revision, no second closing snapshot, no second closing log -- so the
    /// session keeps the closing the first result gave it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task ASecondActionsResultArrivingAfterTheSessionClosedDoesNotCloseItAgain()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_SECOND_RESULT";
        const string proof = "second-result-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            RecordingLogger<OnboardRecoveryCoordinator> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), new FixedTimeProvider(Now), Configuration(proofVariable),
                new RecordingPeer(context), recoveryLogger: log);
            OnboardConnectionState state = CurrentState();
            string first = await ReachUnreconciledResultAsync("FaultCargoRecoveryResult", "FAILED", processor, state, proof);
            const string secondActionId = "51000000-0000-4000-8000-000000000170";
            Assert.Equal("RecoveryActionAccepted", MessageType(await processor.ProcessAsync(
                RecoveryAction("FAULT_CARGO_HANDOFF", messageId: "e0000000-0000-4000-8000-000000001700",
                    actionId: secondActionId), state, token)));
            JsonNode second = JsonNode.Parse(first)!;
            second["messageId"] = "b3200000-0000-4000-8000-000000000170";
            second["payload"]!["recoveryActionId"] = secondActionId;
            second["payload"]!["handoffId"] = StableGuid(secondActionId, "fault-cargo-handoff");
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(first, state, token)));
            long closedRevision = (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).Revision;
            int snapshots = await context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "ExceptionRecoverySessionSnapshot", token);

            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(second.ToJsonString(), state, token)));

            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, (await context.RecoveryWorkflows
                .SingleAsync(row => row.WorkflowId == secondActionId, token)).State);
            ExceptionRecoverySessionRow session = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            Assert.Equal("CLOSED", session.State);
            Assert.Equal(closedRevision, session.Revision);
            Assert.Equal(snapshots, await context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "ExceptionRecoverySessionSnapshot", token));
            Assert.Single(log.Entries, entry =>
                entry.Message.Contains("RECOVERY_ACTION_RESULT_NOT_RECONCILED", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#169, review of PR #173. A result that does not reconcile, arriving for a session that has
    /// already closed, is a record of that attempt and nothing more. The demand and the journey may since have
    /// been settled by the next session -- here a compensation proved every slot empty, ended the demand and
    /// released the vehicle -- and a late FAILED must not put that journey back into Blocked under its own code.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task ALateUnreconciledResultForAClosedSessionDoesNotBlockAJourneyTheNextSessionSettled()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_LATE_UNRECONCILED";
        const string proof = "late-unreconciled-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            OrderIntentRow pickup = await context.OrderIntents.SingleAsync(row => row.UpperId == "UPPER-PICKUP", token);
            pickup.VehicleOccupancyClaimedAt = Now.AddMinutes(-8);
            await context.SaveChangesAsync(token);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState();
            // Session A: the same handoff submitted twice; the first reports FAILED and closes A.
            string first = await ReachUnreconciledResultAsync("FaultCargoRecoveryResult", "FAILED", processor, state, proof);
            const string secondActionId = "51000000-0000-4000-8000-000000000173";
            await processor.ProcessAsync(
                RecoveryAction("FAULT_CARGO_HANDOFF", messageId: "e0000000-0000-4000-8000-000000001730",
                    actionId: secondActionId), state, token);
            JsonNode second = JsonNode.Parse(first)!;
            second["messageId"] = "b3200000-0000-4000-8000-000000000173";
            second["payload"]!["recoveryActionId"] = secondActionId;
            second["payload"]!["handoffId"] = StableGuid(secondActionId, "fault-cargo-handoff");
            await processor.ProcessAsync(first, state, token);

            // Session B: a compensation proves every slot empty and ends the demand.
            const string nextRequestId = "41000000-0000-4000-8000-000000000169";
            const string compensationId = "51000000-0000-4000-8000-000000000174";
            string nextSessionId = StableGuid(nextRequestId, "exception-recovery-session");
            await processor.ProcessAsync(NextSessionRequest(proof), state, token);
            JsonNode compensate = JsonNode.Parse(RecoveryAction(
                "COMPENSATE_LOAD_ALL_EMPTY", messageId: "e0000000-0000-4000-8000-000000001731",
                actionId: compensationId))!;
            compensate["payload"]!["exceptionRecoverySessionId"] = nextSessionId;
            await processor.ProcessAsync(compensate.ToJsonString(), state, token);
            await processor.ProcessAsync(
                Envelope(
                    "90000000-0000-4000-8000-000000000174",
                    "LoadCompensationRequested",
                    new
                    {
                        recoveryActionId = compensationId,
                        exceptionRecoverySessionId = nextSessionId,
                        demandId = DemandId,
                        slotOperationAttemptId = AttemptId,
                        @operator = Operator()
                    }),
                state,
                token);
            await processor.ProcessAsync(AllEmptyCompensationResult(Envelope(
                "a0000000-0000-4000-8000-000000000174",
                "LoadCompensationResult",
                new
                {
                    recoveryActionId = compensationId,
                    demandId = DemandId,
                    slotOperationAttemptId = AttemptId,
                    overallOutcome = "FAILED",
                    slotResults = Array.Empty<object>(),
                    observedAt = Now.AddSeconds(6)
                })), state, token);
            Assert.Equal(JourneyRuntimeStage.Completed, (await context.JourneyRuntimes.SingleAsync(token)).Stage);

            // Session A's second handoff reports, late, and FAILED.
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(second.ToJsonString(), state, token)));

            RecoveryWorkflowRow late = await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == secondActionId, token);
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, late.State);
            Assert.Equal("FAILED", late.Outcome);
            JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(token);
            Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
            Assert.Equal("CANCELLED_BY_LOAD_COMPENSATION", runtime.BlockReasonCode);
            Assert.Equal(DemandExecutionStatus.Cancelled, (await context.AcceptedDemands.SingleAsync(token)).Status);
            Assert.NotNull((await context.VehicleDispatchLeases.SingleAsync(token)).ReleasedAt);
            Dictionary<string, (string Reason, string? Outcome)> reasons = await ClosingReasonsAsync(context);
            Assert.Equal(("RECOVERY_ACTION_RESULT_NOT_RECONCILED", "FAILED"),
                reasons[StableGuid(RequestId, "exception-recovery-session")]);
            Assert.Equal(("RECONCILED", "ALL_EMPTY"), reasons[nextSessionId]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#169. Why a session closed is not on the wire and has no column: it is read from the store,
    /// as a CLOSED session whose own workflow is RecoveryRequired with the vehicle's outcome on it. That reading
    /// only holds while nothing after the closing rewrites the workflow -- neither the vehicle resending the
    /// result, nor the administrator opening the next session on the same demand and taking it through its own
    /// unreconciled action. Each session keeps its own account.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    [InlineData("FAILED")]
    [InlineData("UNKNOWN")]
    public async Task WhyASessionClosedStaysReadableAfterTheResultIsResentAndTheNextSessionRuns(string outcome)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_CLOSE_REASON_KEPT";
        const string proof = "close-reason-kept-proof-not-a-production-secret";
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
            string first = await ReachUnreconciledResultAsync("LoadCompensationResult", outcome, processor, state, proof);
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(first, state, token)));
            string firstSessionId = StableGuid(RequestId, "exception-recovery-session");
            long firstClosedRevision = (await context.ExceptionRecoverySessions.AsNoTracking()
                .SingleAsync(row => row.ExceptionRecoverySessionId == firstSessionId, token)).Revision;

            // The vehicle resends the result; then the administrator opens the next session and hands the
            // cargo off, and that fails too.
            await processor.ProcessAsync(first, state, token);
            const string nextRequestId = "41000000-0000-4000-8000-000000000169";
            const string handoffActionId = "51000000-0000-4000-8000-000000000171";
            string nextSessionId = StableGuid(nextRequestId, "exception-recovery-session");
            Assert.Equal("ExceptionRecoverySessionOpened",
                MessageType(await processor.ProcessAsync(NextSessionRequest(proof), state, token)));
            JsonNode handoff = JsonNode.Parse(RecoveryAction(
                "FAULT_CARGO_HANDOFF", messageId: "e0000000-0000-4000-8000-000000001710",
                actionId: handoffActionId))!;
            handoff["payload"]!["exceptionRecoverySessionId"] = nextSessionId;
            Assert.Equal("RecoveryActionAccepted",
                MessageType(await processor.ProcessAsync(handoff.ToJsonString(), state, token)));
            string handoffResult = Envelope(
                "b3200000-0000-4000-8000-000000000171",
                "FaultCargoRecoveryResult",
                new
                {
                    exceptionRecoverySessionId = nextSessionId,
                    recoveryActionId = handoffActionId,
                    demandId = DemandId,
                    handoffId = StableGuid(handoffActionId, "fault-cargo-handoff"),
                    overallOutcome = "FAILED",
                    slotResults = RecoverySlots.Select(slot => new
                    {
                        slotNo = slot,
                        outcome = "FAILED",
                        finalPhysicalState = "UNKNOWN",
                        lockState = "LOCKED",
                        unlockOutputState = "RESET",
                        reasonCodes = UnknownReasonCodes
                    }).ToArray(),
                    @operator = Operator(),
                    observedAt = Now.AddSeconds(7)
                });
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(handoffResult, state, token)));

            RecoveryWorkflowRow compensation = await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == ActionId, token);
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, compensation.State);
            Assert.Equal(outcome, compensation.Outcome);
            Assert.Equal("a0000000-0000-4000-8000-000000000021", compensation.ResultMessageId);
            Assert.Equal(firstClosedRevision, (await context.ExceptionRecoverySessions.AsNoTracking()
                .SingleAsync(row => row.ExceptionRecoverySessionId == firstSessionId, token)).Revision);
            Dictionary<string, (string Reason, string? Outcome)> reasons = await ClosingReasonsAsync(context);
            Assert.Equal(2, reasons.Count);
            Assert.Equal(("RECOVERY_ACTION_RESULT_NOT_RECONCILED", outcome), reasons[firstSessionId]);
            Assert.Equal(("RECOVERY_ACTION_RESULT_NOT_RECONCILED", "FAILED"), reasons[nextSessionId]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#169, device half. Closing the session over a FAILED forced recovery lifts nothing about
    /// the hardware: the doors were forced either way. While the load still needs recovery that is the reason
    /// the vehicle reports; once a new session has settled the load -- here by a compensation that proves every
    /// slot empty -- the vehicle is still held under FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED, and the
    /// administrator files the hardware record against the forced workflow of the session that is now CLOSED.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AFailedForcedRecoveryStillHoldsTheVehicleForItsHardwareRecordAfterItsSessionClosed()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_FORCED_FAILED_CLOSED";
        const string proof = "forced-failed-closed-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
            await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);
            await processor.ProcessAsync(MechanicallyIsolatedResult(generation: 1, outcome: "FAILED"), state, token);
            await store.ApplyRecoveryReportAsync(
                AgvId, 3, "f0000000-0000-4000-8000-000000000169", forcedRecoveryGeneration: 1,
                null, "NONE", [], [], [], token);

            string firstSessionId = StableGuid(RequestId, "exception-recovery-session");
            Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.SingleAsync(token)).State);
            Assert.Equal("OPERATION_RECOVERY_REQUIRED", (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);

            // The administrator opens a new session and compensates: every slot proven empty ends the load.
            const string nextRequestId = "41000000-0000-4000-8000-000000000169";
            const string compensationId = "51000000-0000-4000-8000-000000000169";
            string nextSessionId = StableGuid(nextRequestId, "exception-recovery-session");
            Assert.Equal("ExceptionRecoverySessionOpened",
                MessageType(await processor.ProcessAsync(NextSessionRequest(proof), state, token)));
            JsonNode compensate = JsonNode.Parse(RecoveryAction(
                "COMPENSATE_LOAD_ALL_EMPTY", messageId: "e0000000-0000-4000-8000-000000001691",
                actionId: compensationId))!;
            compensate["payload"]!["exceptionRecoverySessionId"] = nextSessionId;
            Assert.Equal("RecoveryActionAccepted",
                MessageType(await processor.ProcessAsync(compensate.ToJsonString(), state, token)));
            await processor.ProcessAsync(
                Envelope(
                    "90000000-0000-4000-8000-000000000169",
                    "LoadCompensationRequested",
                    new
                    {
                        recoveryActionId = compensationId,
                        exceptionRecoverySessionId = nextSessionId,
                        demandId = DemandId,
                        slotOperationAttemptId = AttemptId,
                        @operator = Operator()
                    }),
                state,
                token);
            JsonNode compensated = JsonNode.Parse(AllEmptyCompensationResult(Envelope(
                "a0000000-0000-4000-8000-000000000169",
                "LoadCompensationResult",
                new
                {
                    recoveryActionId = compensationId,
                    demandId = DemandId,
                    slotOperationAttemptId = AttemptId,
                    overallOutcome = "FAILED",
                    slotResults = Array.Empty<object>(),
                    observedAt = Now.AddSeconds(6)
                })))!;
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(compensated.ToJsonString(), state, token)));
            Assert.Equal(JourneyRuntimeStage.Completed, (await context.JourneyRuntimes.SingleAsync(token)).Stage);

            SessionReadinessDecision held = await store.DecideReadinessAsync(AgvId, 3, token);
            Assert.Equal(SessionReadiness.RecoveryRequired, held.Readiness);
            Assert.Equal("FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED", held.ReasonCode);

            string recorded = await processor.ProcessAsync(
                HardwareRecoveryRecord("e1000000-0000-4000-8000-000000000169", slots: RecoverySlots), state, token);

            Assert.Equal("HardwareRecoveryRecordResult", MessageType(recorded));
            Assert.Equal("RECORDED", FirstPayload(recorded).GetProperty("outcome").GetString());
            HardwareRecoveryRecordRow record = await context.HardwareRecoveryRecords.SingleAsync(token);
            Assert.Equal(firstSessionId, record.ExceptionRecoverySessionId);
            Assert.Equal(ActionId, record.RecoveryActionId);
            Assert.Equal(SessionReadiness.Ready, (await store.DecideReadinessAsync(AgvId, 3, token)).Readiness);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#169 for a resume. Its result is the replacement OperationResult, judged on the operation
    /// rather than as a recovery result, and one the vehicle cannot vouch for leaves the operation needing
    /// recovery again. The session goes where every other unreconciled result sends it -- CLOSED, the
    /// administrator free to open another -- while the operation, the demand and the journey stay held.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task AResumeWhoseReplacementResultNeedsRecoveryAgainClosesTheSessionSoAnotherCanOpen()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_RESUME_UNRECONCILED";
        const string proof = "resume-unreconciled-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            OrderIntentRow pickup = await context.OrderIntents.SingleAsync(row => row.UpperId == "UPPER-PICKUP", token);
            pickup.VehicleOccupancyClaimedAt = Now.AddMinutes(-8);
            await context.SaveChangesAsync(token);
            RecordingLogger<OnboardRecoveryCoordinator> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), new FixedTimeProvider(Now), Configuration(proofVariable),
                new RecordingPeer(context), recoveryLogger: log);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            await AuthorizeResumeAfterFailedResultAsync(processor, context, state, proof);

            string replacementAck = await processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000001690",
                    "OperationResult",
                    OperationResultPayload(completed: false, journalCheckpoint: "RESUME_RESULT_UNKNOWN_RECORDED")),
                state,
                token);
            await processor.FlushDeferredOutboundAsync(state, token);
            Assert.Equal("DurableAck", MessageType(replacementAck));
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, (await context.RecoveryWorkflows.SingleAsync(token)).State);

            string opened = await processor.ProcessAsync(NextSessionRequest(proof), state, token);
            await processor.FlushDeferredOutboundAsync(state, token);

            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(opened));
            Assert.Equal(AttemptId, PayloadAttempt(opened));
            ExceptionRecoverySessionRow closed = await context.ExceptionRecoverySessions.AsNoTracking()
                .SingleAsync(row => row.RequestId == RequestId, token);
            Assert.Equal("CLOSED", closed.State);
            JsonElement snapshot = await LatestSessionSnapshotAsync(context, closed.ExceptionRecoverySessionId);
            Assert.Equal("CLOSED", snapshot.GetProperty("state").GetString());
            Assert.Empty(snapshot.GetProperty("allowedActions").EnumerateArray());
            Assert.Empty(snapshot.GetProperty("blockingFacts").EnumerateArray());
            Assert.Contains(log.Entries, entry => entry.Level == LogLevel.Warning &&
                entry.Message.Contains("RECOVERY_ACTION_RESULT_NOT_RECONCILED", StringComparison.Ordinal) &&
                entry.Message.Contains(closed.ExceptionRecoverySessionId, StringComparison.Ordinal));

            Assert.Equal(StationOperationStatus.RecoveryRequired, (await context.StationOperations.SingleAsync(token)).Status);
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await context.AcceptedDemands.SingleAsync(token)).Status);
            JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(token);
            Assert.Equal(JourneyRuntimeStage.Blocked, runtime.Stage);
            Assert.Equal("LOAD_RESULT_REQUIRES_RECOVERY", runtime.BlockReasonCode);
            Assert.Null((await context.VehicleDispatchLeases.SingleAsync(token)).ReleasedAt);
            Assert.Null((await context.OrderIntents.AsNoTracking()
                .SingleAsync(row => row.UpperId == "UPPER-PICKUP", token)).VehicleOccupancyReleasedAt);
            Assert.Empty(await context.TransportDemandCompletions.ToArrayAsync(token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#169, review of PR #173. Once a resume that did not reconcile has closed its session, the
    /// operation still needs recovery, so the next session offers RESUME_AFTER_REPAIR again and a second resume
    /// names the same attempt. The replacement result that follows belongs to the resume still waiting for it
    /// only: the first one was judged, its session closed on it, and it is not judged a second time -- which is
    /// also what keeps the first session's reason readable.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ASecondResumeAfterOneThatDidNotReconcileIsJudgedOnItsOwnAndClosesItsOwnSession()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_SECOND_RESUME";
        const string proof = "second-resume-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            await AuthorizeResumeAfterFailedResultAsync(processor, context, state, proof);
            await processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000001720",
                    "OperationResult",
                    OperationResultPayload(completed: false, journalCheckpoint: "RESUME_RESULT_UNKNOWN_RECORDED")),
                state,
                token);
            await processor.FlushDeferredOutboundAsync(state, token);
            RecoveryWorkflowRow firstResume = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, firstResume.State);

            const string nextRequestId = "41000000-0000-4000-8000-000000000169";
            const string secondResumeId = "51000000-0000-4000-8000-000000000172";
            string nextSessionId = StableGuid(nextRequestId, "exception-recovery-session");
            Assert.Equal("ExceptionRecoverySessionOpened",
                MessageType(await processor.ProcessAsync(NextSessionRequest(proof), state, token)));
            await processor.FlushDeferredOutboundAsync(state, token);
            JsonNode resume = JsonNode.Parse(RecoveryAction(
                "RESUME_AFTER_REPAIR", messageId: "e0000000-0000-4000-8000-000000001721", actionId: secondResumeId))!;
            resume["payload"]!["exceptionRecoverySessionId"] = nextSessionId;
            Assert.Equal("RecoveryActionAccepted",
                MessageType(await processor.ProcessAsync(resume.ToJsonString(), state, token)));
            await processor.FlushDeferredOutboundAsync(state, token);

            string ack = await processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000001722",
                    "OperationResult",
                    OperationResultPayload(journalCheckpoint: "SECOND_RESUME_RESULT_RECORDED")),
                state,
                token);
            await processor.FlushDeferredOutboundAsync(state, token);

            Assert.Equal("DurableAck", MessageType(ack));
            Assert.Equal(RecoveryWorkflowState.Reconciled, (await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == secondResumeId, token)).State);
            Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.AsNoTracking()
                .SingleAsync(row => row.ExceptionRecoverySessionId == nextSessionId, token)).State);
            RecoveryWorkflowRow firstAfter = await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == ActionId, token);
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, firstAfter.State);
            Assert.Equal(firstResume.Outcome, firstAfter.Outcome);
            Assert.Equal(firstResume.UpdatedAt, firstAfter.UpdatedAt);
            Assert.Equal(StationOperationStatus.Committed, (await context.StationOperations.SingleAsync(token)).Status);
            Dictionary<string, (string Reason, string? Outcome)> reasons = await ClosingReasonsAsync(context);
            Assert.Equal("RECOVERY_ACTION_RESULT_NOT_RECONCILED",
                reasons[StableGuid(RequestId, "exception-recovery-session")].Reason);
            Assert.Equal("RECONCILED", reasons[nextSessionId].Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#180. An attempt admits one replacement result per resume authorization, so it can have only
    /// one resume waiting for that result. A second RESUME_AFTER_REPAIR on the same attempt while the first still
    /// waits -- here the administrator pressing it twice in one session -- is refused before any workflow exists,
    /// and the replacement result that follows settles the first resume as if the second had never been asked.
    /// Before the fix both were accepted, and the replacement result then matched two waiting resumes and threw.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    public async Task ASecondResumeWhileTheFirstStillAwaitsItsResultIsRefusedAndTheFirstStillSettles()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_RESUME_TWICE";
        const string proof = "resume-twice-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            await AuthorizeResumeAfterFailedResultAsync(processor, context, state, proof);
            string sessionId = StableGuid(RequestId, "exception-recovery-session");
            ExceptionRecoverySessionRow before = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            BusinessPicture pictureBefore = await BusinessPictureAsync(context);
            int outboxBefore = await context.ProtocolOutbox.CountAsync(token);

            const string secondResumeId = "51000000-0000-4000-8000-000000000180";
            string refused = await processor.ProcessAsync(
                RecoveryAction(
                    "RESUME_AFTER_REPAIR", messageId: "e0000000-0000-4000-8000-000000001800", actionId: secondResumeId),
                state,
                token);
            await processor.FlushDeferredOutboundAsync(state, token);

            Assert.Equal("RecoveryActionRejected", MessageType(refused));
            JsonElement problem = FirstPayload(refused);
            Assert.Equal(secondResumeId, problem.GetProperty("recoveryActionId").GetString());
            Assert.Equal(before.Revision, problem.GetProperty("recoverySessionRevision").GetInt64());
            Assert.Equal(ServerReasonCodes.ActionNotAllowedInState,
                problem.GetProperty("problem").GetProperty("reasonCode").GetString());
            RecoveryWorkflowRow first = Assert.Single(await context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(token));
            Assert.Equal(ActionId, first.WorkflowId);
            Assert.Equal(RecoveryWorkflowState.AwaitingResult, first.State);
            ExceptionRecoverySessionRow after = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            Assert.Equal(("EXECUTING", before.Revision, before.UpdatedAt), (after.State, after.Revision, after.UpdatedAt));
            Assert.Equal(pictureBefore, await BusinessPictureAsync(context));
            Assert.Equal(outboxBefore, await context.ProtocolOutbox.CountAsync(token));

            string ack = await processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000001801",
                    "OperationResult",
                    OperationResultPayload(journalCheckpoint: "RESUME_RESULT_RECORDED")),
                state,
                token);
            await processor.FlushDeferredOutboundAsync(state, token);

            Assert.Equal("DurableAck", MessageType(ack));
            Assert.Equal(RecoveryWorkflowState.Reconciled,
                (await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).State);
            Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.AsNoTracking()
                .SingleAsync(row => row.ExceptionRecoverySessionId == sessionId, token)).State);
            Assert.Equal(StationOperationStatus.Committed, (await context.StationOperations.SingleAsync(token)).Status);
            Assert.Equal("RECONCILED", (await ClosingReasonsAsync(context))[sessionId].Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#175. Session A took the same action twice; the first result did not reconcile and closed A,
    /// and the administrator is now working the demand in session B. The second result of A then arrives. Whether
    /// it concludes success or not, it is the record of A's own attempt and nothing more: it is acknowledged and
    /// kept as evidence, and the demand, the journey, the lease, the vehicle, the operation and session B are
    /// exactly as B has them. One warning names the late result for a person to reconcile by hand.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    [InlineData("COMPENSATE_LOAD_ALL_EMPTY", "ALL_EMPTY")]
    [InlineData("FAULT_CARGO_HANDOFF", "HANDED_OFF")]
    [InlineData("FAULT_CARGO_HANDOFF", "FAILED")]
    public async Task ALateResultOfAClosedSessionIsOnlyRecordedWhileTheNextSessionIsExecuting(
        string action,
        string lateOutcome)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_LATE_WHILE_NEXT_EXECUTES";
        const string proof = "late-while-next-executes-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            await ClaimPickupOccupancyAsync(context);
            MovableTimeProvider clock = new(Now);
            EventRecordingLogger<OnboardRecoveryCoordinator> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), clock, Configuration(proofVariable),
                new RecordingPeer(context), recoveryLogger: log);
            OnboardConnectionState state = CurrentState();
            (string first, string late) = await ReachTwoSubmissionsOfOneActionAsync(
                action, lateOutcome, processor, state, proof);
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(first, state, token)));
            string firstSessionId = StableGuid(RequestId, "exception-recovery-session");
            clock.Current = Now.AddMinutes(1);
            string nextSessionId = await OpenNextSessionAndHandOffAsync(processor, state, proof);
            BusinessPicture before = await BusinessPictureAsync(context);
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, before.Demand);
            Assert.Equal(JourneyRuntimeStage.Blocked, before.Stage);
            Assert.Contains($"{nextSessionId}:EXECUTING:", before.Sessions, StringComparison.Ordinal);
            int logsBefore = log.Entries.Count;
            clock.Current = Now.AddMinutes(2);

            string ack = await processor.ProcessAsync(late, state, token);

            Assert.Equal("DurableAck", MessageType(ack));
            Assert.Equal(before, await BusinessPictureAsync(context));
            Assert.Null((await context.VehicleDispatchLeases.AsNoTracking().SingleAsync(token)).ReleasedAt);
            await AssertLateResultRecordedAsync(context, firstSessionId, late, lateOutcome, Now.AddMinutes(2));
            Assert.Equal(logsBefore + 1, log.Entries.Count);
            AssertSingleLateResultLog(log, firstSessionId, SecondActionId, lateOutcome);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#175, decided by the user on 2026-09-19. A late success that arrives after A closed and before
    /// anyone opened B is still only recorded: the demand stays RecoveryRequired and the journey blocked, and the
    /// administrator opens B as usual -- a handoff or compensation there proves the slots empty again, one step
    /// more, accepted.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ALateSuccessOfAClosedSessionBeforeTheNextOneOpensLeavesTheDemandToIt()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_LATE_BEFORE_NEXT";
        const string proof = "late-before-next-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            await ClaimPickupOccupancyAsync(context);
            MovableTimeProvider clock = new(Now);
            EventRecordingLogger<OnboardRecoveryCoordinator> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), clock, Configuration(proofVariable),
                new RecordingPeer(context), recoveryLogger: log);
            OnboardConnectionState state = CurrentState();
            (string first, string late) = await ReachTwoSubmissionsOfOneActionAsync(
                "COMPENSATE_LOAD_ALL_EMPTY", "ALL_EMPTY", processor, state, proof);
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(first, state, token)));
            string firstSessionId = StableGuid(RequestId, "exception-recovery-session");
            BusinessPicture before = await BusinessPictureAsync(context);
            clock.Current = Now.AddMinutes(1);

            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(late, state, token)));

            Assert.Equal(before, await BusinessPictureAsync(context));
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, before.Demand);
            Assert.Equal(JourneyRuntimeStage.Blocked, before.Stage);
            await AssertLateResultRecordedAsync(context, firstSessionId, late, "ALL_EMPTY", Now.AddMinutes(1));
            AssertSingleLateResultLog(log, firstSessionId, SecondActionId, "ALL_EMPTY");
            Assert.Equal("ExceptionRecoverySessionOpened",
                MessageType(await processor.ProcessAsync(NextSessionRequest(proof), state, token)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#175. Session B has already settled the demand -- a compensation proved every slot empty,
    /// the demand is Cancelled, the journey Completed under CANCELLED_BY_LOAD_COMPENSATION, the vehicle released
    /// -- when A's second handoff reports HANDED_OFF, late. B's ending stands as B wrote it: reason, times and
    /// releases included. Before #175 the late handoff ended the journey a second time under its own reason and
    /// clock.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task ALateSuccessOfAClosedSessionDoesNotRewriteTheEndingTheNextSessionGaveTheDemand()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_LATE_AFTER_NEXT_SETTLED";
        const string proof = "late-after-next-settled-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            await ClaimPickupOccupancyAsync(context);
            MovableTimeProvider clock = new(Now);
            EventRecordingLogger<OnboardRecoveryCoordinator> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), clock, Configuration(proofVariable),
                new RecordingPeer(context), recoveryLogger: log);
            OnboardConnectionState state = CurrentState();
            (string first, string late) = await ReachTwoSubmissionsOfOneActionAsync(
                "FAULT_CARGO_HANDOFF", "HANDED_OFF", processor, state, proof);
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(first, state, token)));
            string firstSessionId = StableGuid(RequestId, "exception-recovery-session");
            clock.Current = Now.AddMinutes(1);
            await CompensateAllEmptyInNextSessionAsync(processor, state, proof);
            BusinessPicture settled = await BusinessPictureAsync(context);
            Assert.Equal(DemandExecutionStatus.Cancelled, settled.Demand);
            Assert.Equal(JourneyRuntimeStage.Completed, settled.Stage);
            Assert.Equal("CANCELLED_BY_LOAD_COMPENSATION", settled.BlockReasonCode);
            Assert.Equal(Now.AddMinutes(1), settled.LeaseReleasedAt);
            Assert.Equal(Now.AddMinutes(1), settled.OccupancyReleasedAt);
            clock.Current = Now.AddMinutes(2);

            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(late, state, token)));

            Assert.Equal(settled, await BusinessPictureAsync(context));
            await AssertLateResultRecordedAsync(context, firstSessionId, late, "HANDED_OFF", Now.AddMinutes(2));
            AssertSingleLateResultLog(log, firstSessionId, SecondActionId, "HANDED_OFF");
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#175. Session B resumed the load after repair, the replacement result committed it, and the
    /// journey went on to deliver: the demand Succeeded. A's second handoff then reports HANDED_OFF, late. Before
    /// #175 that result tried to end a delivered demand at its pickup stop, PickupStopTermination refused with
    /// BusinessIdentityConflictException, the whole message rolled back, and the vehicle never got an
    /// acknowledgement for it however often it resent. It is acknowledged and recorded now, and the delivery
    /// stands.
    /// </summary>
    /// <remarks>
    /// The journey's run from the committed load to the delivered unload is the runtime's, not the recovery
    /// coordinator's; it is written here as the runtime leaves it when the unload commits (demand Succeeded,
    /// journey Completed, lease released, the demand's completion recorded) rather than driven through it.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    [Trait("ProtocolVector", "CV-FAULT-CARGO-HANDOFF")]
    public async Task ALateSuccessOfAClosedSessionIsAcknowledgedAfterTheDemandWasDelivered()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_LATE_AFTER_DELIVERY";
        const string proof = "late-after-delivery-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            await ClaimPickupOccupancyAsync(context);
            MovableTimeProvider clock = new(Now);
            EventRecordingLogger<OnboardRecoveryCoordinator> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), clock, Configuration(proofVariable),
                new RecordingPeer(context), recoveryLogger: log);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            (string first, string late) = await ReachTwoSubmissionsOfOneActionAsync(
                "FAULT_CARGO_HANDOFF", "HANDED_OFF", processor, state, proof);
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(first, state, token)));
            await processor.FlushDeferredOutboundAsync(state, token);
            string firstSessionId = StableGuid(RequestId, "exception-recovery-session");

            // Session B resumes the load after repair, and the replacement result commits it.
            clock.Current = Now.AddMinutes(1);
            string nextSessionId = StableGuid("41000000-0000-4000-8000-000000000169", "exception-recovery-session");
            Assert.Equal("ExceptionRecoverySessionOpened",
                MessageType(await processor.ProcessAsync(NextSessionRequest(proof), state, token)));
            await processor.FlushDeferredOutboundAsync(state, token);
            JsonNode resume = JsonNode.Parse(RecoveryAction(
                "RESUME_AFTER_REPAIR", messageId: "e0000000-0000-4000-8000-000000001753",
                actionId: NextSessionActionId))!;
            resume["payload"]!["exceptionRecoverySessionId"] = nextSessionId;
            Assert.Equal("RecoveryActionAccepted",
                MessageType(await processor.ProcessAsync(resume.ToJsonString(), state, token)));
            await processor.FlushDeferredOutboundAsync(state, token);
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000001754",
                    "OperationResult",
                    OperationResultPayload(journalCheckpoint: "RESUME_AFTER_REPAIR_RESULT_RECORDED")),
                state,
                token)));
            await processor.FlushDeferredOutboundAsync(state, token);
            Assert.Equal(StationOperationStatus.Committed, (await context.StationOperations.SingleAsync(token)).Status);

            // The journey then delivers.
            clock.Current = Now.AddMinutes(5);
            AcceptedDemandRow demand = await context.AcceptedDemands.SingleAsync(token);
            demand.Status = DemandExecutionStatus.Succeeded;
            JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(token);
            runtime.Stage = JourneyRuntimeStage.Completed;
            runtime.SetBlockReason(null, clock.Current);
            runtime.UpdatedAt = clock.Current;
            (await context.VehicleDispatchLeases.SingleAsync(token)).ReleasedAt = clock.Current;
            (await context.OrderIntents.SingleAsync(row => row.UpperId == "UPPER-PICKUP", token))
                .VehicleOccupancyReleasedAt = clock.Current;
            context.TransportDemandCompletions.Add(new TransportDemandCompletionRow
            {
                TransportDemandKey = demand.TransportDemandKey,
                DemandId = demand.DemandId,
                DemandRevision = demand.DemandRevision,
                Evidence = new string('b', 64),
                CompletedAt = clock.Current
            });
            await context.SaveChangesAsync(token);
            BusinessPicture delivered = await BusinessPictureAsync(context);
            clock.Current = Now.AddMinutes(6);

            string ack = await processor.ProcessAsync(late, state, token);
            await processor.FlushDeferredOutboundAsync(state, token);

            Assert.Equal("DurableAck", MessageType(ack));
            Assert.Equal(delivered, await BusinessPictureAsync(context));
            Assert.Equal(DemandExecutionStatus.Succeeded, delivered.Demand);
            Assert.Equal(StationOperationStatus.Committed, delivered.Operation);
            await AssertLateResultRecordedAsync(context, firstSessionId, late, "HANDED_OFF", Now.AddMinutes(6));
            AssertSingleLateResultLog(log, firstSessionId, SecondActionId, "HANDED_OFF");
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#175. The vehicle resends a late result it has no acknowledgement for: on the same connection,
    /// after the server restarted, and rebound into the next session generation after a reconnect. Each resend is
    /// answered from the first judgement -- the same DurableAck for the same line -- and adds no evidence, no
    /// workflow change and no second warning; the business stays where session B has it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task AResentLateResultOfAClosedSessionIsAnsweredFromTheFirstAcrossRestartAndReconnect()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_LATE_RESENT";
        const string proof = "late-resent-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            await ClaimPickupOccupancyAsync(context);
            MovableTimeProvider clock = new(Now);
            EventRecordingLogger<OnboardRecoveryCoordinator> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), clock, Configuration(proofVariable),
                new RecordingPeer(context), recoveryLogger: log);
            OnboardConnectionState state = CurrentState();
            (string first, string late) = await ReachTwoSubmissionsOfOneActionAsync(
                "COMPENSATE_LOAD_ALL_EMPTY", "ALL_EMPTY", processor, state, proof);
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(first, state, token)));
            string firstSessionId = StableGuid(RequestId, "exception-recovery-session");
            clock.Current = Now.AddMinutes(1);
            await OpenNextSessionAndHandOffAsync(processor, state, proof);
            BusinessPicture before = await BusinessPictureAsync(context);
            clock.Current = Now.AddMinutes(2);
            string firstAck = await processor.ProcessAsync(late, state, token);
            Assert.Equal("DurableAck", MessageType(firstAck));
            clock.Current = Now.AddMinutes(3);

            Assert.Equal(firstAck, await processor.ProcessAsync(late, state, token));

            // The server restarts: a new context and processor over the same store.
            await using ControlServerDbContext restarted = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            OnboardMessageProcessor afterRestart = TestOnboardProcessorFactory.Create(
                restarted, new WireToGateStore(restarted), clock, Configuration(proofVariable),
                new RecordingPeer(restarted), recoveryLogger: log);
            Assert.Equal(firstAck, await afterRestart.ProcessAsync(late, state, token));

            // The vehicle reconnects into generation 4 and resends the same result rebound to it.
            await AdvanceSessionGenerationAsync(restarted, state, 4);
            JsonNode rebound = JsonNode.Parse(late)!;
            rebound["sessionGeneration"] = 4;
            Assert.Equal("DurableAck", MessageType(await afterRestart.ProcessAsync(rebound.ToJsonString(), state, token)));

            Assert.Equal(before, await BusinessPictureAsync(restarted));
            await AssertLateResultRecordedAsync(restarted, firstSessionId, late, "ALL_EMPTY", Now.AddMinutes(2));
            Assert.Equal(2, await restarted.RecoveryResultEvidence.CountAsync(token));
            AssertSingleLateResultLog(log, firstSessionId, SecondActionId, "ALL_EMPTY");
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#175, the forced recovery's way in, and why it was already closed. A session cannot change
    /// action, so a late forced result needs A to have forced twice. Each forced action advances the vehicle's
    /// forced generation, and only the result of the newest generation is current -- so the one that closes A is
    /// the second, and the first, arriving afterwards, is HistoricalOnly: kept as evidence, the business and
    /// session B untouched, and no late-result warning, since it never reached the judgement at all. Session B
    /// hands off here; had B forced too, its generation would make any of A's results historical the same way.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task ALateForcedRecoveryOfAClosedSessionIsHistoricalAndSettlesNothing()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_LATE_FORCED";
        const string proof = "late-forced-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            await ClaimPickupOccupancyAsync(context);
            WireToGateStore store = new(context);
            EventRecordingLogger<OnboardRecoveryCoordinator> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, store, new FixedTimeProvider(Now), Configuration(proofVariable),
                new RecordingPeer(context), recoveryLogger: log);
            OnboardConnectionState state = CurrentState();
            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
            Assert.Equal("RecoveryActionAccepted", MessageType(await processor.ProcessAsync(
                RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token)));
            await store.ApplyRecoveryReportAsync(
                AgvId, 3, "f0000000-0000-4000-8000-000000000175", forcedRecoveryGeneration: 1,
                AttemptId, "PREPARED", [], [AttemptId], [], token);
            Assert.Equal("RecoveryActionAccepted", MessageType(await processor.ProcessAsync(
                RecoveryAction("FORCED_MECHANICAL_RECOVERY", messageId: "e0000000-0000-4000-8000-000000001755",
                    actionId: SecondActionId), state, token)));
            await store.ApplyRecoveryReportAsync(
                AgvId, 3, "f0000000-0000-4000-8000-000000000176", forcedRecoveryGeneration: 2,
                AttemptId, "PREPARED", [], [AttemptId], [], token);
            JsonNode closing = JsonNode.Parse(MechanicallyIsolatedResult(
                generation: 2, messageId: "80000000-0000-4000-8000-000000000175", outcome: "FAILED"))!;
            closing["payload"]!["recoveryActionId"] = SecondActionId;
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(closing.ToJsonString(), state, token)));
            Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).State);
            await OpenNextSessionAndHandOffAsync(processor, state, proof);
            BusinessPicture before = await BusinessPictureAsync(context);

            string ack = await processor.ProcessAsync(MechanicallyIsolatedResult(generation: 1), state, token);

            Assert.Equal("DurableAck", MessageType(ack));
            Assert.Equal(before, await BusinessPictureAsync(context));
            Assert.Equal(RecoveryWorkflowState.HistoricalOnly, (await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == ActionId, token)).State);
            Assert.True((await context.RecoveryResultEvidence.AsNoTracking()
                .SingleAsync(row => row.MessageId == "80000000-0000-4000-8000-000000000001", token)).HistoricalOnly);
            Assert.DoesNotContain(log.Entries, entry => entry.EventId.Id == 2122);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#175, the resume's way in. A resume's result is the attempt's replacement OperationResult, and
    /// a resume authorizes exactly one (WireToGateStore.RequireResumeAuthorizationAsync): once session A's resume
    /// has been judged and A closed on it, a further replacement for the same attempt -- whether session B is
    /// executing or has already settled the demand -- finds no resume awaiting it and is refused whole as a replay
    /// conflict, before anything is written. The demand, the journey, the lease, the operation and session B stay
    /// as B has them. No late-result settlement is reachable along this path, so the guard is this test.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-RESUME")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFurtherReplacementResultAfterAResumeClosedItsSessionSettlesNothing(bool nextSessionSettled)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_LATE_RESUME";
        const string proof = "late-resume-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            await ClaimPickupOccupancyAsync(context);
            MovableTimeProvider clock = new(Now);
            EventRecordingLogger<OnboardRecoveryCoordinator> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), clock, Configuration(proofVariable),
                new RecordingPeer(context), recoveryLogger: log);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            await AuthorizeResumeAfterFailedResultAsync(processor, context, state, proof);
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(
                Envelope(
                    "e0000000-0000-4000-8000-000000001756",
                    "OperationResult",
                    OperationResultPayload(completed: false, journalCheckpoint: "RESUME_RESULT_UNKNOWN_RECORDED")),
                state,
                token)));
            await processor.FlushDeferredOutboundAsync(state, token);
            Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).State);
            clock.Current = Now.AddMinutes(1);
            if (nextSessionSettled)
                await CompensateAllEmptyInNextSessionAsync(processor, state, proof);
            else
                await OpenNextSessionAndHandOffAsync(processor, state, proof);
            await processor.FlushDeferredOutboundAsync(state, token);
            BusinessPicture before = await BusinessPictureAsync(context);
            int operationResults = await context.OperationResults.CountAsync(token);
            clock.Current = Now.AddMinutes(2);

            string late = Envelope(
                "e0000000-0000-4000-8000-000000001757",
                "OperationResult",
                OperationResultPayload(journalCheckpoint: "LATE_RESUME_RESULT_RECORDED"));
            await Assert.ThrowsAsync<ProtocolContentConflictException>(
                () => processor.ProcessAsync(late, state, token));

            context.ChangeTracker.Clear();
            Assert.Equal(before, await BusinessPictureAsync(context));
            Assert.Equal(operationResults, await context.OperationResults.CountAsync(token));
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, (await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == ActionId, token)).State);
            Assert.DoesNotContain(log.Entries, entry => entry.EventId.Id == 2122);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#175, review of PR #177. The other way a session closes: its first result reconciled, and the
    /// second submission of the same action reports afterwards, also ALL_EMPTY. It is recorded against its own
    /// workflow and settles nothing a second time, and the store still reads A as closed on a normal
    /// reconciliation -- the late one is RecoveryRequired and judged later, so it never reads as the closer.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ALateResultAfterASessionClosedOnAReconciledResultLeavesThatClosingReadable()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_LATE_AFTER_RECONCILED";
        const string proof = "late-after-reconciled-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            await ClaimPickupOccupancyAsync(context);
            MovableTimeProvider clock = new(Now);
            EventRecordingLogger<OnboardRecoveryCoordinator> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), clock, Configuration(proofVariable),
                new RecordingPeer(context), recoveryLogger: log);
            OnboardConnectionState state = CurrentState();
            (string first, string late) = await ReachTwoSubmissionsOfOneActionAsync(
                "COMPENSATE_LOAD_ALL_EMPTY", "ALL_EMPTY", processor, state, proof);
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(
                AllEmptyCompensationResult(first), state, token)));
            string sessionId = StableGuid(RequestId, "exception-recovery-session");
            BusinessPicture settled = await BusinessPictureAsync(context);
            Assert.Equal(DemandExecutionStatus.Cancelled, settled.Demand);
            clock.Current = Now.AddMinutes(1);

            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(late, state, token)));

            Assert.Equal(settled, await BusinessPictureAsync(context));
            RecoveryWorkflowRow second = await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == SecondActionId, token);
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, second.State);
            Assert.Equal("ALL_EMPTY", second.Outcome);
            Assert.Equal(("RECONCILED", "ALL_EMPTY"), (await ClosingReasonsAsync(context))[sessionId]);
            Assert.Equal([SecondActionId], await ResultsArrivedAfterClosingAsync(context));
            AssertSingleLateResultLog(log, sessionId, SecondActionId, "ALL_EMPTY");
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// control-server#175, review of PR #177. A compensation is authorized in a second message, and that message can
    /// arrive after its session closed: A's second compensation was submitted, the first reported FAILED and closed
    /// A, and the administrator is working the demand in B when the second one's authorization comes in. It is
    /// refused: A stays CLOSED at its revision, no compensation command goes out, no snapshot is queued. Before this,
    /// the authorization set A back to EXECUTING and sent the command, so the compensation's ALL_EMPTY then found an
    /// open session and settled the demand under B's feet.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-EXCEPTION-COMPENSATE")]
    public async Task ACompensationAuthorizedAfterItsSessionClosedIsRefusedAndDoesNotReopenIt()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_AUTHORIZED_AFTER_CLOSE";
        const string proof = "authorized-after-close-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context);
            await ClaimPickupOccupancyAsync(context);
            MovableTimeProvider clock = new(Now);
            EventRecordingLogger<OnboardRecoveryCoordinator> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), clock, Configuration(proofVariable),
                new RecordingPeer(context), recoveryLogger: log);
            OnboardConnectionState state = CurrentState();
            string sessionId = StableGuid(RequestId, "exception-recovery-session");
            string first = await ReachCompensationResultAsync(processor, state, proof);
            Assert.Equal("RecoveryActionAccepted", MessageType(await processor.ProcessAsync(
                RecoveryAction("COMPENSATE_LOAD_ALL_EMPTY", messageId: "e0000000-0000-4000-8000-000000001758",
                    actionId: SecondActionId), state, token)));
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(first, state, token)));
            clock.Current = Now.AddMinutes(1);
            await OpenNextSessionAndHandOffAsync(processor, state, proof);
            BusinessPicture before = await BusinessPictureAsync(context);
            int compensationCommands = await context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "LoadCompensationCommand", token);
            clock.Current = Now.AddMinutes(2);

            string authorized = await processor.ProcessAsync(
                Envelope(
                    "90000000-0000-4000-8000-000000000177",
                    "LoadCompensationRequested",
                    new
                    {
                        recoveryActionId = SecondActionId,
                        exceptionRecoverySessionId = sessionId,
                        demandId = DemandId,
                        slotOperationAttemptId = AttemptId,
                        @operator = Operator()
                    }),
                state,
                token);

            Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.AsNoTracking()
                .SingleAsync(row => row.ExceptionRecoverySessionId == sessionId, token)).State);
            Assert.Equal("LoadCompensationRejected", MessageType(authorized));
            Assert.Equal("RECOVERY_SESSION_NOT_OPEN",
                FirstPayload(authorized).GetProperty("problem").GetProperty("reasonCode").GetString());
            Assert.Equal(before, await BusinessPictureAsync(context));
            Assert.Equal(compensationCommands, await context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "LoadCompensationCommand", token));
            RecoveryWorkflowRow second = await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == SecondActionId, token);
            Assert.Equal(RecoveryWorkflowState.AwaitingAuthorization, second.State);
            Assert.Null(second.CommandMessageId);

            // Should the vehicle report that compensation all the same, it is a late result of a closed session.
            JsonNode late = JsonNode.Parse(AllEmptyCompensationResult(first))!;
            late["messageId"] = "a0000000-0000-4000-8000-000000000177";
            late["payload"]!["recoveryActionId"] = SecondActionId;
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(late.ToJsonString(), state, token)));
            Assert.Equal(before, await BusinessPictureAsync(context));
            AssertSingleLateResultLog(log, sessionId, SecondActionId, "ALL_EMPTY");
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
    [InlineData(StationOperationStatus.Failed, "READY")]
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
        runtime.SetBlockReason(null, Now);
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
    /// A cancellation that names an attempt is judged against the vehicle that sent it too
    /// (control-server#116 review, item 3): another vehicle naming this demand and its load is refused and
    /// records nothing, while the vehicle running the journey is authorized.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ALoadCancellationNamingAnAttemptIsRefusedFromAnotherVehicle()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCancellableLoadAsync(context);
        OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), CancellationProofVariable);
        OnboardConnectionState other = CurrentState();
        other.AgvId = "AGV-SOMEONE-ELSE";
        JsonNode foreign = JsonNode.Parse(CancellationRequest("b3000000-0000-4000-8000-000000000001"))!;
        foreign["agvId"] = other.AgvId;

        string refused = await processor.ProcessAsync(foreign.ToJsonString(), other, token);
        string authorized = await processor.ProcessAsync(
            CancellationRequest("b3000000-0000-4000-8000-000000000002").Replace(
                "b1000000-0000-4000-8000-000000000010", "b3000000-0000-4000-8000-000000000010", StringComparison.Ordinal),
            CurrentState(),
            token);

        using (JsonDocument document = JsonDocument.Parse(refused))
        {
            Assert.Equal("REJECTED", document.RootElement.GetProperty("payload").GetProperty("decision").GetString());
        }
        using (JsonDocument document = JsonDocument.Parse(authorized))
        {
            Assert.Equal("AUTHORIZED", document.RootElement.GetProperty("payload").GetProperty("decision").GetString());
        }
        RecoveryWorkflowRow workflow = await context.RecoveryWorkflows.SingleAsync(token);
        Assert.Equal("b3000000-0000-4000-8000-000000000002", workflow.WorkflowId);
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
    /// control-server#131: each of the three results that end a demand whose load was commanded -- an
    /// in-flight cancellation, a compensation, a fault cargo handoff -- frees the vehicle in the same change
    /// that cancels the demand, releases the lease and cancels the slot operation. Until #131 they wrote all
    /// of that by hand except the vehicle occupancy, so the pickup order kept holding the vehicle and the
    /// occupancy index refused its next order (VEHICLE_OCCUPANCY_CONFLICT). The second claim below is that
    /// index's decision, the same call the runtime makes before it dispatches.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("LoadCancellationResult", "CANCELLED_BY_OPERATOR")]
    [InlineData("LoadCompensationResult", "CANCELLED_BY_LOAD_COMPENSATION")]
    [InlineData("FaultCargoRecoveryResult", "TERMINATED_BY_FAULT_CARGO_HANDOFF")]
    public async Task EachResultThatEndsACommandedLoadFreesTheVehicleForItsNextOrder(
        string messageType,
        string reasonCode)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_RELEASES_VEHICLE";
        const string proof = "releases-vehicle-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            if (messageType == "LoadCancellationResult")
                await SeedCancellableLoadAsync(context);
            else
                await SeedBlockedJourneyAsync(context);
            OrderIntentRow pickup = await context.OrderIntents.SingleAsync(row => row.UpperId == "UPPER-PICKUP", token);
            pickup.VehicleOccupancyClaimedAt = Now.AddMinutes(-8);
            await context.SaveChangesAsync(token);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState();

            string result = await ReachEndingResultAsync(messageType, processor, state, proof);
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(result, state, token)));

            Assert.Equal(RecoveryWorkflowState.Reconciled, (await context.RecoveryWorkflows
                .SingleAsync(row => row.ResultMessageId != null, token)).State);
            Assert.Equal(DemandExecutionStatus.Cancelled, (await context.AcceptedDemands.SingleAsync(token)).Status);
            Assert.NotNull((await context.VehicleDispatchLeases.SingleAsync(token)).ReleasedAt);
            Assert.Equal(StationOperationStatus.Cancelled, (await context.StationOperations.SingleAsync(token)).Status);
            JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(token);
            Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
            Assert.Equal(reasonCode, runtime.BlockReasonCode);
            Assert.NotNull((await context.OrderIntents.AsNoTracking()
                .SingleAsync(row => row.UpperId == "UPPER-PICKUP", token)).VehicleOccupancyReleasedAt);

            OrderIntentRow next = Intent("next-pickup-leg", "UPPER-NEXT-PICKUP", "TO_PICKUP", 11);
            context.OrderIntents.Add(next);
            await context.SaveChangesAsync(token);
            Assert.True(await new VehicleDispatchPolicyStore(context).TryClaimVehicleOccupancyAsync(
                next.UpperId, Now.AddMinutes(1), token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// Drives the seeded journey to the line the vehicle sends when the given recovery has proved every
    /// authorized slot empty, and returns that line unsent.
    /// </summary>
    private static async Task<string> ReachEndingResultAsync(
        string messageType,
        OnboardMessageProcessor processor,
        OnboardConnectionState state,
        string proof)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        switch (messageType)
        {
            case "LoadCancellationResult":
                const string cancellationId = "b3100000-0000-4000-8000-000000000001";
                await processor.ProcessAsync(CancellationRequest(cancellationId), state, token);
                return CancellationResult(cancellationId, "b3100000-0000-4000-8000-000000000002", "EMPTY");
            case "LoadCompensationResult":
                return AllEmptyCompensationResult(await ReachCompensationResultAsync(processor, state, proof));
            case "FaultCargoRecoveryResult":
                await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
                await processor.ProcessAsync(RecoveryAction("FAULT_CARGO_HANDOFF"), state, token);
                return Envelope(
                    "b3100000-0000-4000-8000-000000000003",
                    "FaultCargoRecoveryResult",
                    new
                    {
                        exceptionRecoverySessionId = StableGuid(RequestId, "exception-recovery-session"),
                        recoveryActionId = ActionId,
                        demandId = DemandId,
                        handoffId = StableGuid(ActionId, "fault-cargo-handoff"),
                        overallOutcome = "HANDED_OFF",
                        slotResults = RecoverySlots.Select(slot => new
                        {
                            slotNo = slot,
                            outcome = "COMPLETED",
                            finalPhysicalState = "EMPTY",
                            lockState = "LOCKED",
                            unlockOutputState = "RESET",
                            reasonCodes = Array.Empty<string>()
                        }).ToArray(),
                        @operator = Operator(),
                        observedAt = Now.AddSeconds(5)
                    });
            default:
                throw new ArgumentOutOfRangeException(nameof(messageType), messageType, null);
        }
    }

    /// <summary>
    /// Drives the seeded journey through a session and the action that produces <paramref name="messageType"/>,
    /// and returns, unsent, a result of that type that does not reconcile: <paramref name="outcome"/> as its
    /// conclusion, or <see cref="HandedOffOverAnOccupiedSlot"/> for a handoff whose second slot is not empty.
    /// </summary>
    private static async Task<string> ReachUnreconciledResultAsync(
        string messageType,
        string outcome,
        OnboardMessageProcessor processor,
        OnboardConnectionState state,
        string proof)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        switch (messageType)
        {
            case "LoadCompensationResult":
                JsonNode compensation = JsonNode.Parse(await ReachCompensationResultAsync(processor, state, proof))!;
                compensation["payload"]!["overallOutcome"] = outcome;
                return compensation.ToJsonString();
            case "FaultCargoRecoveryResult":
                await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
                await processor.ProcessAsync(RecoveryAction("FAULT_CARGO_HANDOFF"), state, token);
                bool occupied = outcome == HandedOffOverAnOccupiedSlot;
                return Envelope(
                    "b3200000-0000-4000-8000-000000000001",
                    "FaultCargoRecoveryResult",
                    new
                    {
                        exceptionRecoverySessionId = StableGuid(RequestId, "exception-recovery-session"),
                        recoveryActionId = ActionId,
                        demandId = DemandId,
                        handoffId = StableGuid(ActionId, "fault-cargo-handoff"),
                        overallOutcome = occupied ? "HANDED_OFF" : outcome,
                        slotResults = RecoverySlots.Select(slot => new
                        {
                            slotNo = slot,
                            outcome = occupied ? "COMPLETED" : outcome,
                            finalPhysicalState = occupied && slot == 2 ? "OCCUPIED" : occupied ? "EMPTY" : "UNKNOWN",
                            lockState = "LOCKED",
                            unlockOutputState = "RESET",
                            reasonCodes = occupied ? Array.Empty<string>() : UnknownReasonCodes
                        }).ToArray(),
                        @operator = Operator(),
                        observedAt = Now.AddSeconds(5)
                    });
            case "ForcedMechanicalRecoveryResult":
                await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
                await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);
                return MechanicallyIsolatedResult(generation: 1, outcome: outcome);
            default:
                throw new ArgumentOutOfRangeException(nameof(messageType), messageType, null);
        }
    }

    /// <summary>
    /// Why each CLOSED session closed, read from the store the way control-server#169 leaves it -- there is no
    /// column for it. The closing result is the first one judged for the session: of its workflows that a result
    /// has settled (Reconciled or RecoveryRequired; HistoricalOnly never touches a session), the one judged
    /// earliest by the server's clock (<c>UpdatedAt</c>, written when the result was judged and never after).
    /// RecoveryRequired reads as RECOVERY_ACTION_RESULT_NOT_RECONCILED. The outcome is the recovery result's
    /// own; a resume's is null, and its account is the OperationResults row for the attempt.
    /// </summary>
    private static async Task<Dictionary<string, (string Reason, string? Outcome)>> ClosingReasonsAsync(
        ControlServerDbContext context)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        ExceptionRecoverySessionRow[] closed = await context.ExceptionRecoverySessions.AsNoTracking()
            .Where(row => row.State == "CLOSED").ToArrayAsync(token);
        RecoveryWorkflowRow[] workflows = await context.RecoveryWorkflows.AsNoTracking()
            .Where(row => row.State == RecoveryWorkflowState.Reconciled ||
                          row.State == RecoveryWorkflowState.RecoveryRequired)
            .ToArrayAsync(token);
        return closed.ToDictionary(
            session => session.ExceptionRecoverySessionId,
            session =>
            {
                RecoveryWorkflowRow closing = workflows
                    .Where(row => row.ExceptionRecoverySessionId == session.ExceptionRecoverySessionId)
                    .OrderBy(row => row.UpdatedAt)
                    .First();
                return (closing.State == RecoveryWorkflowState.Reconciled
                    ? "RECONCILED"
                    : "RECOVERY_ACTION_RESULT_NOT_RECONCILED", closing.Outcome);
            });
    }

    /// <summary>
    /// The results that reached a session only after it had closed (control-server#175), read from the store by
    /// the same rule as <see cref="ClosingReasonsAsync"/>: a session closes on the first result judged for it, so
    /// every other settled workflow of a CLOSED session was judged after the closing. There is no column for it.
    /// </summary>
    private static async Task<string[]> ResultsArrivedAfterClosingAsync(ControlServerDbContext context)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string[] closed = await context.ExceptionRecoverySessions.AsNoTracking()
            .Where(row => row.State == "CLOSED").Select(row => row.ExceptionRecoverySessionId).ToArrayAsync(token);
        RecoveryWorkflowRow[] settled = await context.RecoveryWorkflows.AsNoTracking()
            .Where(row => row.State == RecoveryWorkflowState.Reconciled ||
                          row.State == RecoveryWorkflowState.RecoveryRequired)
            .ToArrayAsync(token);
        return
        [
            .. settled
                .Where(row => row.ExceptionRecoverySessionId is not null && closed.Contains(row.ExceptionRecoverySessionId))
                .GroupBy(row => row.ExceptionRecoverySessionId)
                .SelectMany(group => group.OrderBy(row => row.UpdatedAt).Skip(1))
                .Select(row => row.WorkflowId)
                .Order(StringComparer.Ordinal)
        ];
    }

    private const string SecondActionId = "51000000-0000-4000-8000-000000000175";

    /// <summary>
    /// Session A on the seeded demand with <paramref name="action"/> taken twice while it executes (the protocol
    /// allows the same action again under a new recoveryActionId). Returns, unsent, the first action's result,
    /// which does not reconcile and so closes A, and the second action's, concluding <paramref name="lateOutcome"/>
    /// -- with every slot proven empty when that is a success.
    /// </summary>
    private static async Task<(string First, string Second)> ReachTwoSubmissionsOfOneActionAsync(
        string action,
        string lateOutcome,
        OnboardMessageProcessor processor,
        OnboardConnectionState state,
        string proof)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string sessionId = StableGuid(RequestId, "exception-recovery-session");
        string first = action switch
        {
            "COMPENSATE_LOAD_ALL_EMPTY" => await ReachCompensationResultAsync(processor, state, proof),
            "FAULT_CARGO_HANDOFF" => await ReachUnreconciledResultAsync(
                "FaultCargoRecoveryResult", "FAILED", processor, state, proof),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
        Assert.Equal("RecoveryActionAccepted", MessageType(await processor.ProcessAsync(
            RecoveryAction(action, messageId: "e0000000-0000-4000-8000-000000001750", actionId: SecondActionId),
            state, token)));
        JsonNode second = JsonNode.Parse(first)!;
        second["messageId"] = "b3200000-0000-4000-8000-000000000175";
        second["payload"]!["recoveryActionId"] = SecondActionId;
        second["payload"]!["overallOutcome"] = lateOutcome;
        if (action == "COMPENSATE_LOAD_ALL_EMPTY")
        {
            await processor.ProcessAsync(
                Envelope(
                    "90000000-0000-4000-8000-000000000175",
                    "LoadCompensationRequested",
                    new
                    {
                        recoveryActionId = SecondActionId,
                        exceptionRecoverySessionId = sessionId,
                        demandId = DemandId,
                        slotOperationAttemptId = AttemptId,
                        @operator = Operator()
                    }),
                state,
                token);
        }
        else
        {
            second["payload"]!["handoffId"] = StableGuid(SecondActionId, "fault-cargo-handoff");
        }
        if (lateOutcome is "ALL_EMPTY" or "HANDED_OFF")
        {
            second["payload"]!["slotResults"] = JsonSerializer.SerializeToNode(RecoverySlots.Select(slot => new
            {
                slotNo = slot,
                outcome = "COMPLETED",
                finalPhysicalState = "EMPTY",
                lockState = "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = Array.Empty<string>()
            }).ToArray(), SerializerOptions);
        }
        return (first, second.ToJsonString());
    }

    /// <summary>
    /// Opens session B on the seeded demand after A closed, and takes it into EXECUTING with a fault cargo handoff
    /// whose result has not come back. Returns B's id.
    /// </summary>
    private static async Task<string> OpenNextSessionAndHandOffAsync(
        OnboardMessageProcessor processor,
        OnboardConnectionState state,
        string proof)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string nextSessionId = StableGuid("41000000-0000-4000-8000-000000000169", "exception-recovery-session");
        Assert.Equal("ExceptionRecoverySessionOpened",
            MessageType(await processor.ProcessAsync(NextSessionRequest(proof), state, token)));
        JsonNode handoff = JsonNode.Parse(RecoveryAction(
            "FAULT_CARGO_HANDOFF", messageId: "e0000000-0000-4000-8000-000000001751",
            actionId: NextSessionActionId))!;
        handoff["payload"]!["exceptionRecoverySessionId"] = nextSessionId;
        Assert.Equal("RecoveryActionAccepted",
            MessageType(await processor.ProcessAsync(handoff.ToJsonString(), state, token)));
        return nextSessionId;
    }

    private const string NextSessionActionId = "51000000-0000-4000-8000-000000000176";

    /// <summary>
    /// Opens session B after A closed and settles the demand in it: a compensation that proves every slot empty
    /// cancels the demand, completes the journey and releases the vehicle.
    /// </summary>
    private static async Task CompensateAllEmptyInNextSessionAsync(
        OnboardMessageProcessor processor,
        OnboardConnectionState state,
        string proof)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string nextSessionId = StableGuid("41000000-0000-4000-8000-000000000169", "exception-recovery-session");
        Assert.Equal("ExceptionRecoverySessionOpened",
            MessageType(await processor.ProcessAsync(NextSessionRequest(proof), state, token)));
        JsonNode compensate = JsonNode.Parse(RecoveryAction(
            "COMPENSATE_LOAD_ALL_EMPTY", messageId: "e0000000-0000-4000-8000-000000001752",
            actionId: NextSessionActionId))!;
        compensate["payload"]!["exceptionRecoverySessionId"] = nextSessionId;
        Assert.Equal("RecoveryActionAccepted",
            MessageType(await processor.ProcessAsync(compensate.ToJsonString(), state, token)));
        await processor.ProcessAsync(
            Envelope(
                "90000000-0000-4000-8000-000000000176",
                "LoadCompensationRequested",
                new
                {
                    recoveryActionId = NextSessionActionId,
                    exceptionRecoverySessionId = nextSessionId,
                    demandId = DemandId,
                    slotOperationAttemptId = AttemptId,
                    @operator = Operator()
                }),
            state,
            token);
        Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(AllEmptyCompensationResult(Envelope(
            "a0000000-0000-4000-8000-000000000176",
            "LoadCompensationResult",
            new
            {
                recoveryActionId = NextSessionActionId,
                demandId = DemandId,
                slotOperationAttemptId = AttemptId,
                overallOutcome = "FAILED",
                slotResults = Array.Empty<object>(),
                observedAt = Now.AddSeconds(6)
            })), state, token)));
    }

    private static async Task ClaimPickupOccupancyAsync(ControlServerDbContext context)
    {
        OrderIntentRow pickup = await context.OrderIntents.SingleAsync(
            row => row.UpperId == "UPPER-PICKUP", TestContext.Current.CancellationToken);
        pickup.VehicleOccupancyClaimedAt = Now.AddMinutes(-8);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Everything a recovery result may settle, and every session, as the store has it: two pictures taken either
    /// side of a result that must only be recorded compare equal.
    /// </summary>
    private sealed record BusinessPicture(
        DemandExecutionStatus Demand,
        JourneyRuntimeStage Stage,
        string? BlockReasonCode,
        DateTimeOffset? BlockReasonSince,
        DateTimeOffset? RuntimeUpdatedAt,
        DateTimeOffset? LeaseReleasedAt,
        DateTimeOffset? OccupancyReleasedAt,
        StationOperationStatus Operation,
        string Sessions,
        int Snapshots,
        int Completions);

    private static async Task<BusinessPicture> BusinessPictureAsync(ControlServerDbContext context)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        JourneyRuntimeRow runtime = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
        ExceptionRecoverySessionRow[] sessions = await context.ExceptionRecoverySessions.AsNoTracking()
            .ToArrayAsync(token);
        return new BusinessPicture(
            (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status,
            runtime.Stage,
            runtime.BlockReasonCode,
            runtime.BlockReasonSince,
            runtime.UpdatedAt,
            (await context.VehicleDispatchLeases.AsNoTracking().SingleAsync(token)).ReleasedAt,
            (await context.OrderIntents.AsNoTracking()
                .SingleAsync(row => row.UpperId == "UPPER-PICKUP", token)).VehicleOccupancyReleasedAt,
            (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status,
            string.Join(';', sessions.OrderBy(row => row.ExceptionRecoverySessionId, StringComparer.Ordinal)
                .Select(row => $"{row.ExceptionRecoverySessionId}:{row.State}:{row.Revision}:{row.SelectedAction}")),
            await context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "ExceptionRecoverySessionSnapshot", token),
            await context.TransportDemandCompletions.CountAsync(token));
    }

    /// <summary>
    /// What a late result of the first session leaves behind (control-server#175): its evidence and its workflow,
    /// judged at <paramref name="judgedAt"/>, RecoveryRequired -- never HistoricalOnly, which would let a forced
    /// recovery's hardware hold go -- and readable from the store as having arrived after the closing, while the
    /// closing itself still reads as the first result's.
    /// </summary>
    private static async Task AssertLateResultRecordedAsync(
        ControlServerDbContext context,
        string firstSessionId,
        string lateResult,
        string lateOutcome,
        DateTimeOffset judgedAt)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string messageId = JsonNode.Parse(lateResult)!["messageId"]!.GetValue<string>();
        RecoveryWorkflowRow late = await context.RecoveryWorkflows.AsNoTracking()
            .SingleAsync(row => row.WorkflowId == SecondActionId, token);
        Assert.Equal(messageId, late.ResultMessageId);
        Assert.Equal(lateOutcome, late.Outcome);
        Assert.Equal(RecoveryWorkflowState.RecoveryRequired, late.State);
        Assert.Equal(judgedAt, late.UpdatedAt);
        RecoveryResultEvidenceRow evidence = await context.RecoveryResultEvidence.AsNoTracking()
            .SingleAsync(row => row.MessageId == messageId, token);
        Assert.False(evidence.HistoricalOnly);
        Assert.Equal(lateOutcome, evidence.Outcome);
        Assert.Equal(judgedAt, evidence.ReceivedAt);
        Assert.Equal(("RECOVERY_ACTION_RESULT_NOT_RECONCILED", "FAILED"),
            (await ClosingReasonsAsync(context))[firstSessionId]);
        Assert.Equal([SecondActionId], await ResultsArrivedAfterClosingAsync(context));
    }

    /// <summary>
    /// The one warning a late result of a closed session writes (control-server#175): EventId 2122, naming the
    /// session, the workflow and the vehicle's conclusion, whichever way that conclusion went.
    /// </summary>
    private static void AssertSingleLateResultLog(
        EventRecordingLogger<OnboardRecoveryCoordinator> log,
        string sessionId,
        string workflowId,
        string outcome)
    {
        (LogLevel Level, EventId EventId, string Message) entry = Assert.Single(
            log.Entries, entry => entry.EventId.Id == 2122);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(sessionId, entry.Message, StringComparison.Ordinal);
        Assert.Contains(workflowId, entry.Message, StringComparison.Ordinal);
        Assert.Contains(outcome, entry.Message, StringComparison.Ordinal);
        Assert.Contains(DemandId, entry.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A second session request on the seeded demand and slots, as the administrator makes it after the first
    /// session closed: a new press, so a new request and a new message.
    /// </summary>
    private static string NextSessionRequest(string proof)
    {
        JsonNode next = JsonNode.Parse(RecoverySessionRequest(
            proof, messageId: "e0000000-0000-4000-8000-000000000169"))!;
        next["payload"]!["requestId"] = "41000000-0000-4000-8000-000000000169";
        return next.ToJsonString();
    }

    /// <summary>
    /// The payload of the newest ExceptionRecoverySessionSnapshot queued for the given session.
    /// </summary>
    private static async Task<JsonElement> LatestSessionSnapshotAsync(
        ControlServerDbContext context,
        string exceptionRecoverySessionId) =>
        (await context.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == "ExceptionRecoverySessionSnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .Select(row => FirstPayload(row.PayloadJson))
            .Where(payload => payload.GetProperty("exceptionRecoverySessionId").GetString() == exceptionRecoverySessionId)
            .MaxBy(payload => payload.GetProperty("recoverySessionRevision").GetInt64());

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
                OperationResultPayload(completed: false, journalCheckpoint: "RESULT_UNKNOWN_RECORDED")),
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

    /// <summary>
    /// The seeded journey before its load has any result: the operation Prepared, the demand Accepted, the
    /// journey in AwaitingLoadResult with its station departure wait started at <paramref name="waitStartedAt"/>
    /// (a five-minute wait under <see cref="DeadlineAwareProcessor"/>), and a Ready session.
    /// </summary>
    private static async Task SeedLoadAwaitingResultAsync(ControlServerDbContext context, DateTimeOffset waitStartedAt)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await SeedBlockedJourneyAsync(context, productionShapedSession: true);
        (await context.StationOperations.SingleAsync(token)).Status = StationOperationStatus.Prepared;
        (await context.AcceptedDemands.SingleAsync(token)).Status = DemandExecutionStatus.Accepted;
        JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(token);
        runtime.Stage = JourneyRuntimeStage.AwaitingLoadResult;
        runtime.SetBlockReason(null, Now);
        runtime.StationDepartureWaitStartedAt = waitStartedAt;
        await context.SaveChangesAsync(token);
    }

    private static OnboardMessageProcessor DeadlineAwareProcessor(
        ControlServerDbContext context,
        RecordingLogger<OnboardMessageProcessor> log) =>
        TestOnboardProcessorFactory.Create(
            context,
            new WireToGateStore(context),
            new FixedTimeProvider(Now),
            Configuration(CancellationProofVariable),
            new RecordingPeer(context),
            new JourneyRuntimeOptions { StationDepartureWaitTimeout = TimeSpan.FromMinutes(5) },
            log);

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

    private static JourneyRuntimeRow Runtime()
    {
        JourneyRuntimeRow runtime = new()
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
            CreatedAt = Now.AddMinutes(-8),
            UpdatedAt = Now
        };
        runtime.SetBlockReason("LOAD_RESULT_REQUIRES_RECOVERY", Now);
        return runtime;
    }

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

    /// <summary>
    /// The state of the newest ExceptionRecoverySessionSnapshot this server queued.
    /// </summary>
    private static async Task<string?> LatestSessionSnapshotStateAsync(ControlServerDbContext context)
    {
        // By revision, not CreatedAt: the fixed clock stamps every snapshot with the same instant.
        (long Revision, string? State)[] snapshots = (await context.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == "ExceptionRecoverySessionSnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken))
            .Select(row =>
            {
                using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
                JsonElement payload = document.RootElement.GetProperty("payload");
                return (payload.GetProperty("recoverySessionRevision").GetInt64(),
                    payload.GetProperty("state").GetString());
            })
            .ToArray();
        return snapshots.MaxBy(snapshot => snapshot.Revision).State;
    }

    /// <summary>
    /// The vehicle's report of a forced mechanical recovery of the seeded session and action: isolated by
    /// hand (or, with <paramref name="outcome"/>, not), and -- as the schema pins them -- neither electronic
    /// emptiness nor vehicle readiness claimed.
    /// </summary>
    private static string MechanicallyIsolatedResult(
        long generation,
        string messageId = "80000000-0000-4000-8000-000000000001",
        string outcome = "MECHANICALLY_ISOLATED") =>
        Envelope(
            messageId,
            "ForcedMechanicalRecoveryResult",
            new
            {
                exceptionRecoverySessionId = StableGuid(RequestId, "exception-recovery-session"),
                recoveryActionId = ActionId,
                forcedRecoveryGeneration = generation,
                outcome,
                slots = RecoverySlots,
                @operator = Operator(),
                observedAt = Now.AddSeconds(2),
                electronicEmptyProven = false,
                vehicleReadyProven = false
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
        runtime.SetBlockReason(null, Now);
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

    /// <remarks>
    /// <paramref name="completed"/> false is a result whose slots the vehicle cannot vouch for: occupancy
    /// UNKNOWN. Until control-server#81 it was FAILED over EMPTY, LOCKED and RESET, which is now the shape of
    /// a determinate failure (ADR-cross-0058 decision 5) and stays out of recovery once the station deadline
    /// has passed -- so the tests about a result that needs recovery carry one that needs it whatever the
    /// clock says. The determinate shape is <see cref="DeterminateLoadFailurePayload"/>.
    /// </remarks>
    private static object OperationResultPayload(
        bool completed = true,
        int[]? slots = null,
        string journalCheckpoint = "RESULT_RECORDED")
    {
        object[] slotResults = (slots ?? RecoverySlots).Select(slot => (object)new
        {
            slotNo = slot,
            outcome = completed ? "COMPLETED" : "UNKNOWN",
            finalPhysicalState = completed ? "OCCUPIED" : "UNKNOWN",
            lockState = "LOCKED",
            unlockOutputState = "RESET",
            reasonCodes = completed ? Array.Empty<string>() : UnknownReasonCodes
        }).ToArray();
        return OperationResultPayload(completed ? "COMPLETED" : "UNKNOWN", slotResults, journalCheckpoint);
    }

    /// <summary>
    /// ADR-cross-0058 decision 5's result: slot 1 failed under <c>OPERATOR_TIMEOUT</c>, slot 2 was never
    /// started, and both are empty, locked and reset.
    /// </summary>
    private static object DeterminateLoadFailurePayload(string failedSlotReasonCode = "OPERATOR_TIMEOUT") =>
        OperationResultPayload(
            "FAILED",
            [
                new
                {
                    slotNo = 1,
                    outcome = "FAILED",
                    finalPhysicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = new[] { failedSlotReasonCode }
                },
                new
                {
                    slotNo = 2,
                    outcome = "NOT_STARTED",
                    finalPhysicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = Array.Empty<string>()
                }
            ],
            "OPERATOR_TIMEOUT_RECORDED");

    private static object OperationResultPayload(
        string overallOutcome,
        object[] slotResults,
        string journalCheckpoint)
    {
        var withoutHash = new
        {
            demandId = DemandId,
            slotOperationAttemptId = AttemptId,
            operationType = "LOAD",
            overallOutcome,
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

    private static JsonElement FirstPayload(string wire)
    {
        string first = wire.Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
        using JsonDocument document = JsonDocument.Parse(first);
        return document.RootElement.GetProperty("payload").Clone();
    }

    /// <summary>
    /// The administrator's record of the hardware after a forced recovery of the seeded session, in the
    /// shape onboard-hmi#107 sends it.
    /// </summary>
    private static string HardwareRecoveryRecord(string recordId, int[] slots) => Envelope(
        "e2" + recordId[2..],
        "HardwareRecoveryRecordSubmitted",
        new
        {
            recordId,
            exceptionRecoverySessionId = StableGuid(RequestId, "exception-recovery-session"),
            recoveryActionId = ActionId,
            @operator = Operator(),
            administratorRole = "MAINTENANCE_ADMINISTRATOR",
            slots,
            checksPerformed = HardwareChecks,
            actionsPerformed = HardwareActions,
            observations = HardwareObservations,
            observedAt = Now.AddMinutes(30)
        });

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

    /// <summary>
    /// A clock the test moves by hand, for the facts that are read by the server's time of judgement: which of a
    /// session's results closed it, and which arrived after.
    /// </summary>
    private sealed class MovableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Current;
    }

    /// <summary>
    /// Records each entry with its EventId, which the shared RecordingLogger drops: the late-result warning is
    /// asserted to be one event whichever way the result concluded.
    /// </summary>
    private sealed class EventRecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, EventId EventId, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, eventId, formatter(state, exception)));
            }
        }
    }
}
