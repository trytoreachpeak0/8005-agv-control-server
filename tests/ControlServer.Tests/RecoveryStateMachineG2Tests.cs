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

    private static async Task SeedBlockedJourneyAsync(ControlServerDbContext context)
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
            DepartureSafe = false,
            RecoveryReportId = "b0000000-0000-4000-8000-000000000001",
            ForcedRecoveryGeneration = 0,
            ReportedForcedRecoveryGeneration = 0,
            UnsettledSlotOperationAttemptId = AttemptId,
            ProvenRecoveryCheckpoint = "PREPARED",
            ActiveUnlockSlotsJson = "[1,2]",
            PendingAttemptIdsJson = JsonSerializer.Serialize(new[] { AttemptId }),
            PendingResultIdsJson = "[]",
            Readiness = SessionReadiness.RecoveryRequired,
            ReasonCode = "PENDING_FACT_RECONCILIATION_REQUIRED",
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

    private static string RecoveryAction(string action, string reason = "Use current persisted facts.") => Envelope(
        "e0000000-0000-4000-8000-000000000002",
        "RecoveryActionSubmitted",
        new
        {
            recoveryActionId = ActionId,
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

    private static object OperationResultPayload()
    {
        object[] slotResults = RecoverySlots.Select(slot => (object)new
        {
            slotNo = slot,
            outcome = "COMPLETED",
            finalPhysicalState = "OCCUPIED",
            lockState = "LOCKED",
            unlockOutputState = "RESET",
            reasonCodes = Array.Empty<string>()
        }).ToArray();
        var withoutHash = new
        {
            demandId = DemandId,
            slotOperationAttemptId = AttemptId,
            operationType = "LOAD",
            overallOutcome = "COMPLETED",
            slotResults,
            observedAt = Now.AddSeconds(1),
            journalCheckpoint = "RESULT_RECORDED"
        };
        JsonElement normalized = JsonSerializer.SerializeToElement(withoutHash, SerializerOptions);
        byte[] businessContent = JsonSerializer.SerializeToUtf8Bytes(new
        {
            demandId = normalized.GetProperty("demandId"),
            slotOperationAttemptId = normalized.GetProperty("slotOperationAttemptId"),
            operationType = normalized.GetProperty("operationType"),
            overallOutcome = normalized.GetProperty("overallOutcome"),
            slotResults = normalized.GetProperty("slotResults"),
            observedAt = normalized.GetProperty("observedAt"),
            journalCheckpoint = normalized.GetProperty("journalCheckpoint")
        }, SerializerOptions);
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
