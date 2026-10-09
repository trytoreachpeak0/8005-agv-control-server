using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ControlServer.Tests;

/// <summary>
/// control-server#481: inbound messages the onboard keeps in its outbox and replays in every handshake until it is
/// answered (8005-agv-onboard-hmi WireToGateSessionClient.ReplayDurableOutgoingAsync). An answer ends that replay only
/// when it is a DurableAck, or a ProtocolProblem with one of the four row-content codes the onboard abandons a row on
/// (8005-agv-onboard-hmi#254, IsAbandonableContentConflictCode). Anything else -- an exception that drops the connection,
/// or a code outside those four -- brings the same row back on the next handshake.
/// </summary>
public sealed partial class RecoveryStateMachineG2Tests
{
    private const string UnknownAttemptId = "48100000-0000-4000-8000-000000000001";

    private static readonly string[] OnboardAbandonsTheRowOn =
    [
        "MESSAGE_ID_CONTENT_CONFLICT",
        "BUSINESS_ID_CONTENT_CONFLICT",
        "SNAPSHOT_REVISION_CONTENT_CONFLICT",
        "RECOVERY_SCOPE_MISMATCH"
    ];

    /// <summary>
    /// A result for a slot operation this server never commanded -- the vehicle owes it a server whose database was
    /// replaced, or another server instance -- is acknowledged and kept as historical evidence, and nothing else changes.
    /// Its rebound replay in the next session is acknowledged again without a second row.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task AnOperationResultForAnAttemptThisServerNeverCommandedIsKeptAsHistoryAndEndsItsReplay()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string messageId = "48100000-0000-4000-8000-000000000002";
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCancellableLoadAsync(context);
        RecordingLogger<OnboardMessageProcessor> log = new();
        OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), CancellationProofVariable, log);
        OnboardConnectionState state = CurrentState();
        string result = Envelope(messageId, "OperationResult", CompletedLoadResultPayload(UnknownAttemptId));
        StationOperationRow operationBefore = await context.StationOperations.AsNoTracking().SingleAsync(token);

        string response = await processor.ProcessAsync(result, state, token);

        AssertEndsTheReplay(response, result);
        Assert.Equal("DurableAck", MessageType(response));
        context.ChangeTracker.Clear();
        OperationResultRow kept = await context.OperationResults.AsNoTracking().SingleAsync(token);
        Assert.Equal(
            (messageId, UnknownAttemptId, AgvId, true, "COMPLETED", ResultContentSha256Of(result)),
            (kept.ResultId, kept.SlotOperationAttemptId, kept.AgvId, kept.HistoricalOnly, kept.OverallOutcome,
                kept.ResultContentSha256));
        Assert.Equal(WireContentHash(result), kept.ContentHash);
        StationOperationRow operationAfter = await context.StationOperations.AsNoTracking().SingleAsync(token);
        Assert.Equal(operationBefore.Status, operationAfter.Status);
        Assert.Equal(DemandExecutionStatus.Accepted, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
        string warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning).Message;
        Assert.All(
            new[] { AgvId, UnknownAttemptId, DemandId, messageId, ResultContentSha256Of(result) },
            fact => Assert.Contains(fact, warning, StringComparison.Ordinal));

        // A forced recovery in between moves the vehicle's generation: the replay is still recognised by its result id.
        VehicleRecoveryGenerationRow? vehicleGeneration =
            await context.VehicleRecoveryGenerations.SingleOrDefaultAsync(row => row.AgvId == AgvId, token);
        if (vehicleGeneration is null)
        {
            vehicleGeneration = new VehicleRecoveryGenerationRow { AgvId = AgvId };
            context.VehicleRecoveryGenerations.Add(vehicleGeneration);
        }
        vehicleGeneration.ForcedRecoveryGeneration++;
        vehicleGeneration.UpdatedAt = Now;
        await context.SaveChangesAsync(token);
        await AdvanceSessionGenerationAsync(context, state, 4);
        string replay = InSession(result, 4);
        string replayed = await processor.ProcessAsync(replay, state, token);

        AssertEndsTheReplay(replayed, replay);
        Assert.Equal("DurableAck", MessageType(replayed));
        context.ChangeTracker.Clear();
        Assert.Equal(messageId, (await context.OperationResults.AsNoTracking().SingleAsync(token)).ResultId);
    }

    /// <summary>
    /// A second, different result for the same unknown attempt is acknowledged too, but not kept: the attempt's one live
    /// row is the first one's.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task ASecondResultForTheSameUnknownAttemptIsAcknowledgedAndKeepsTheFirst()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCancellableLoadAsync(context);
        OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), CancellationProofVariable);
        OnboardConnectionState state = CurrentState();
        await processor.ProcessAsync(
            Envelope("48100000-0000-4000-8000-000000000003", "OperationResult", CompletedLoadResultPayload(UnknownAttemptId)),
            state, token);
        string second = Envelope(
            "48100000-0000-4000-8000-000000000004", "OperationResult",
            CompletedLoadResultPayload(UnknownAttemptId, observedAfterSeconds: 9));

        string response = await processor.ProcessAsync(second, state, token);

        AssertEndsTheReplay(response, second);
        Assert.Equal("DurableAck", MessageType(response));
        context.ChangeTracker.Clear();
        Assert.Equal(
            "48100000-0000-4000-8000-000000000003",
            (await context.OperationResults.AsNoTracking().SingleAsync(token)).ResultId);
    }

    /// <summary>
    /// The store keeps the same answer for a caller that hands it a result of an operation it does not have: historical,
    /// not a throw on an inbound line.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task TheStoreKeepsAResultOfAnOperationItDoesNotHaveAsHistory()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCancellableLoadAsync(context);

        OperationResultDisposition disposition = await new WireToGateStore(context).ApplyOperationResultAsync(
            new StationOperationResult(
                "48100000-0000-4000-8000-000000000005",
                UnknownAttemptId,
                DemandId,
                SlotOperationType.Load,
                "COMPLETED",
                [new SlotPhysicalEvidence(1, SlotBusinessState.Occupied, true, true)],
                true,
                Now,
                new string('a', 64),
                new string('b', 64),
                SlotOutcomeReport.FromSlotResults(JsonDocument.Parse("[]").RootElement)),
            AgvId,
            0,
            token);

        Assert.Equal(OperationResultDisposition.HistoricalOnly, disposition);
        context.ChangeTracker.Clear();
        Assert.True((await context.OperationResults.AsNoTracking().SingleAsync(token)).HistoricalOnly);
    }

    /// <summary>
    /// A recovery result naming a workflow this server never opened is acknowledged and kept as historical evidence under
    /// that workflow id, and no workflow is created. A forced mechanical recovery's evidence keeps the generation its result
    /// names; the others take the vehicle's current one.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [InlineData("LoadCancellationResult")]
    [InlineData("ForcedMechanicalRecoveryResult")]
    public async Task ARecoveryResultForAWorkflowThisServerNeverOpenedIsKeptAsHistoryAndEndsItsReplay(string messageType)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        const string messageId = "48100000-0000-4000-8000-000000000012";
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCancellableLoadAsync(context);
        RecordingLogger<OnboardRecoveryCoordinator> log = new();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            context, new WireToGateStore(context), new FixedTimeProvider(Now), Configuration(CancellationProofVariable),
            new RecordingPeer(context), recoveryLogger: log);
        (string result, string workflowId, long generation) = messageType == "LoadCancellationResult"
            ? (CancellationResult("48100000-0000-4000-8000-000000000011", messageId, "EMPTY"),
                "48100000-0000-4000-8000-000000000011", 0L)
            : (MechanicallyIsolatedResult(7, messageId), ActionId, 7L);

        string response = await processor.ProcessAsync(result, CurrentState(), token);

        AssertEndsTheReplay(response, result);
        Assert.Equal("DurableAck", MessageType(response));
        context.ChangeTracker.Clear();
        RecoveryResultEvidenceRow kept = await context.RecoveryResultEvidence.AsNoTracking().SingleAsync(token);
        Assert.Equal(
            (messageId, workflowId, messageType, true, generation, WireContentHash(result)),
            (kept.MessageId, kept.WorkflowId, kept.MessageType, kept.HistoricalOnly, kept.ForcedRecoveryGeneration,
                kept.ContentHash));
        Assert.Empty(await context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(token));
        string warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning).Message;
        Assert.All(
            new[] { AgvId, messageType, workflowId, messageId, PayloadSha256Of(result) },
            fact => Assert.Contains(fact, warning, StringComparison.Ordinal));
    }

    /// <summary>
    /// The coordinator called a second time with the same unknown-workflow result keeps its one row: a caller that skips
    /// the inbox does not write a second evidence row under one messageId.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task AnUnknownWorkflowResultCalledAgainKeepsOneEvidenceRow()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCancellableLoadAsync(context);
        OnboardRecoveryCoordinator coordinator = TestOnboardProcessorFactory.CreateRecoveryCoordinator(
            context, new WireToGateStore(context), new FixedTimeProvider(Now), Configuration(CancellationProofVariable));
        string result = CancellationResult(
            "48100000-0000-4000-8000-000000000013", "48100000-0000-4000-8000-000000000014", "EMPTY");
        using JsonDocument document = JsonDocument.Parse(result);

        await coordinator.ProcessResultAsync(document.RootElement, WireContentHash(result), token);
        string again = await coordinator.ProcessResultAsync(document.RootElement, WireContentHash(result), token);

        Assert.Equal("DurableAck", MessageType(again));
        Assert.Single(await context.RecoveryResultEvidence.AsNoTracking().ToArrayAsync(token));
    }

    /// <summary>
    /// A cancellation result that would end a demand already delivered is taken as a result that does not reconcile:
    /// acknowledged, its workflow RecoveryRequired, the demand left delivered and its operation untouched (control-server#481,
    /// withdrawing #478's ACTION_NOT_ALLOWED_IN_STATE, which the onboard replays). The RecoveryRequired workflow holds
    /// nothing: the session's readiness is what it was before the result, and a resend is answered from the inbox.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-ALL-EMPTY")]
    public async Task ACancellationResultForADemandAlreadyDeliveredIsTakenAsNotReconcilingAndHoldsNothing()
    {
        const string cancellationId = "48100000-0000-4000-8000-000000000021";
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCancellableLoadAsync(context);
        SessionRecoveryRow seeded = await context.SessionRecoveries.SingleAsync(token);
        seeded.DepartureSafe = true;
        seeded.PendingAttemptIdsJson = "[]";
        seeded.UnsettledSlotOperationAttemptId = null;
        await context.SaveChangesAsync(token);
        RecordingLogger<OnboardRecoveryCoordinator> log = new();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            context, new WireToGateStore(context), new FixedTimeProvider(Now), Configuration(CancellationProofVariable),
            new RecordingPeer(context), recoveryLogger: log);
        OnboardConnectionState state = CurrentState();
        Assert.Equal("AUTHORIZED", FirstPayload(await processor.ProcessAsync(
            CancellationRequest(cancellationId), state, token)).GetProperty("decision").GetString());
        (await context.AcceptedDemands.SingleAsync(token)).Status = DemandExecutionStatus.Succeeded;
        await context.SaveChangesAsync(token);
        context.ChangeTracker.Clear();
        SessionReadinessDecision before = await new WireToGateStore(context).DecideReadinessAsync(AgvId, 3, token);
        StationOperationRow operationBefore = await context.StationOperations.AsNoTracking().SingleAsync(token);
        JourneyRuntimeRow journeyBefore = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
        string result = CancellationResult(cancellationId, "48100000-0000-4000-8000-000000000022", "EMPTY");

        string response = await processor.ProcessAsync(result, state, token);

        AssertEndsTheReplay(response, result);
        Assert.Equal("DurableAck", MessageType(response));
        context.ChangeTracker.Clear();
        Assert.Equal(RecoveryWorkflowState.RecoveryRequired,
            (await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).State);
        Assert.Equal(DemandExecutionStatus.Succeeded, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
        Assert.Equal(operationBefore.Status, (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status);
        JourneyRuntimeRow journeyAfter = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
        Assert.Equal((journeyBefore.Stage, journeyBefore.BlockReasonCode), (journeyAfter.Stage, journeyAfter.BlockReasonCode));
        Assert.Single(await context.RecoveryResultEvidence.AsNoTracking().ToArrayAsync(token));
        string warning = Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning).Message;
        Assert.Contains(DemandId, warning, StringComparison.Ordinal);

        SessionReadinessDecision after = await new WireToGateStore(context).DecideReadinessAsync(AgvId, 3, token);
        Assert.Equal((before.Readiness, before.ReasonCode), (after.Readiness, after.ReasonCode));
        Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(result, state, token)));
    }

    /// <summary>
    /// A handoff or compensation result that would end a demand already delivered is taken like the cancellation above:
    /// acknowledged, its workflow RecoveryRequired, the demand left delivered and the journey untouched (control-server#481,
    /// coordinator's ruling: the same refusal looped on the onboard for these as well).
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("FaultCargoRecoveryResult")]
    [InlineData("LoadCompensationResult")]
    public async Task AnEndingRecoveryResultForADemandAlreadyDeliveredIsTakenAsNotReconciling(string messageType)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_481_DELIVERED_ENDING";
        const string proof = "delivered-ending-proof-not-a-production-secret";
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
            string result = await ReachEndingResultAsync(messageType, processor, state, proof);
            (await context.AcceptedDemands.SingleAsync(token)).Status = DemandExecutionStatus.Succeeded;
            await context.SaveChangesAsync(token);
            context.ChangeTracker.Clear();
            JourneyRuntimeRow journeyBefore = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
            StationOperationRow operationBefore = await context.StationOperations.AsNoTracking().SingleAsync(token);

            string response = await processor.ProcessAsync(result, state, token);

            AssertEndsTheReplay(response, result);
            Assert.Equal("DurableAck", MessageType(response));
            context.ChangeTracker.Clear();
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, (await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.ResultMessageId != null, token)).State);
            Assert.Equal(DemandExecutionStatus.Succeeded, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
            Assert.Equal(operationBefore.Status, (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status);
            JourneyRuntimeRow journeyAfter = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
            Assert.Equal((journeyBefore.Stage, journeyBefore.BlockReasonCode), (journeyAfter.Stage, journeyAfter.BlockReasonCode));
            Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).State);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The boundary of the acknowledgement above: it is for an attempt this server has no record of. A second result for an
    /// attempt it knows and has already settled -- a late replacement after its resume was closed, say -- is still refused
    /// with BUSINESS_ID_CONTENT_CONFLICT, never acknowledged: an acknowledgement would have the onboard drop the row in
    /// silence, while the refusal makes it give the row up with an alert (8005-agv-onboard-hmi#254).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task ASecondResultForAnAttemptThisServerSettledIsStillRefused()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCancellableLoadAsync(context);
        OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), CancellationProofVariable);
        OnboardConnectionState state = CurrentState();
        Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(
            Envelope("48100000-0000-4000-8000-000000000041", "OperationResult", CompletedLoadResultPayload(AttemptId)),
            state, token)));
        string late = Envelope(
            "48100000-0000-4000-8000-000000000042", "OperationResult",
            CompletedLoadResultPayload(AttemptId, observedAfterSeconds: 9));

        ProtocolProblemAssert.RefusedLine(
            await processor.ProcessAsync(late, state, token), "BUSINESS_ID_CONTENT_CONFLICT", late);

        context.ChangeTracker.Clear();
        Assert.Equal(
            "48100000-0000-4000-8000-000000000041",
            (await context.OperationResults.AsNoTracking().SingleAsync(token)).ResultId);
    }

    /// <summary>
    /// A correction is not an ending: a successful correction result on a demand already delivered reconciles as it always
    /// did (review of #489, S1). Since control-server#505 a correction is authorized only while its demand is on board, so here it
    /// is authorized first and the demand delivered after, the order Batch7StationYieldTests.AnOpenCorrectionOnADemandAlreadyUnloaded
    /// DoesNotHoldTheDeparture also takes.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task ASuccessfulCorrectionResultOnADemandAlreadyDeliveredStillReconciles()
    {
        const string correctionId = "48100000-0000-4000-8000-000000000051";
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCorrectableLoadAsync(context, JourneyRuntimeStage.AwaitingStationDeparture);
        OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), CancellationProofVariable);
        OnboardConnectionState state = CurrentState();
        await processor.ProcessAsync(CorrectionRequest(correctionId), state, token);
        Assert.Equal("LOAD_CORRECTION", (await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).WorkflowType);
        (await context.AcceptedDemands.SingleAsync(token)).Status = DemandExecutionStatus.Succeeded;
        await context.SaveChangesAsync(token);
        context.ChangeTracker.Clear();
        string result = Envelope(
            "48100000-0000-4000-8000-000000000052",
            "LoadCorrectionResult",
            new
            {
                correctionId,
                demandId = DemandId,
                slotOperationAttemptId = AttemptId,
                overallOutcome = "COMPLETED",
                slotResults = RecoverySlots.Select(slot => new
                {
                    slotNo = slot,
                    outcome = "COMPLETED",
                    finalPhysicalState = "OCCUPIED",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = Array.Empty<string>()
                }).ToArray(),
                observedAt = Now.AddSeconds(4)
            });

        string response = await processor.ProcessAsync(result, state, token);

        Assert.Equal("DurableAck", MessageType(response));
        context.ChangeTracker.Clear();
        Assert.Equal(RecoveryWorkflowState.Reconciled, (await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).State);
        Assert.Equal(DemandExecutionStatus.Succeeded, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
    }

    /// <summary>
    /// A workflow this server knows but opened for another vehicle is a known business id: refused with
    /// BUSINESS_ID_CONTENT_CONFLICT and nothing kept, never acknowledged as unknown (review of #489, S2).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task ARecoveryResultNamingAnotherVehiclesWorkflowIsRefusedAndKeepsNothing()
    {
        const string cancellationId = "48100000-0000-4000-8000-000000000061";
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCancellableLoadAsync(context);
        OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), CancellationProofVariable);
        OnboardConnectionState state = CurrentState();
        Assert.Equal("AUTHORIZED", FirstPayload(await processor.ProcessAsync(
            CancellationRequest(cancellationId), state, token)).GetProperty("decision").GetString());
        (await context.RecoveryWorkflows.SingleAsync(token)).AgvId = "AGV-8005-02";
        await context.SaveChangesAsync(token);
        context.ChangeTracker.Clear();
        string result = CancellationResult(cancellationId, "48100000-0000-4000-8000-000000000062", "EMPTY");

        ProtocolProblemAssert.RefusedLine(
            await processor.ProcessAsync(result, state, token), "BUSINESS_ID_CONTENT_CONFLICT", result);

        context.ChangeTracker.Clear();
        Assert.Empty(await context.RecoveryResultEvidence.AsNoTracking().ToArrayAsync(token));
        RecoveryWorkflowRow workflow = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
        Assert.Equal((RecoveryWorkflowState.AwaitingResult, (string?)null), (workflow.State, workflow.ResultMessageId));
    }

    private static void AssertEndsTheReplay(string response, string requestLine)
    {
        using JsonDocument request = JsonDocument.Parse(requestLine);
        string requestId = request.RootElement.GetProperty("messageId").GetString()!;
        string first = response.Split('\n', StringSplitOptions.RemoveEmptyEntries).First();
        using JsonDocument answer = JsonDocument.Parse(first);
        JsonElement payload = answer.RootElement.GetProperty("payload");
        switch (answer.RootElement.GetProperty("messageType").GetString())
        {
            case "DurableAck":
                Assert.Equal(requestId, payload.GetProperty("acceptedMessageId").GetString());
                break;
            case "ProtocolProblem":
                Assert.Equal(requestId, payload.GetProperty("rejectedMessageId").GetString());
                Assert.Contains(
                    payload.GetProperty("problem").GetProperty("reasonCode").GetString(), OnboardAbandonsTheRowOn);
                break;
            default:
                Assert.Fail($"期望 DurableAck 或车载端会放弃该行的 ProtocolProblem，实际是：{first}");
                break;
        }
    }

    private static string ResultContentSha256Of(string line) =>
        JsonNode.Parse(line)!["payload"]!["resultContentSha256"]!.GetValue<string>();

    private static string PayloadSha256Of(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return Sha256(document.RootElement.GetProperty("payload").GetRawText());
    }

    private static object CompletedLoadResultPayload(string attemptId, int observedAfterSeconds = 1)
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
            slotOperationAttemptId = attemptId,
            operationType = "LOAD",
            overallOutcome = "COMPLETED",
            slotResults,
            observedAt = Now.AddSeconds(observedAfterSeconds),
            journalCheckpoint = "RESULT_RECORDED"
        };
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

    /// <summary>
    /// REQ-0364 with a delivered demand (review of control-server#482, probe P3): a door-unproven empty cancellation result
    /// for a demand already delivered is acknowledged as a result that does not reconcile, and the vehicle is still held for
    /// the door its result could not prove locked -- the hold does not depend on whether the demand could be ended. The
    /// hold has the exit every door hold has: a repair release, its record, fresh readings and a SAFE HOLD_RELEASE check
    /// bring the session back to Ready, with nothing written by hand.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-EMPTY-DOOR-UNPROVEN")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task ADoorUnprovenCancellationResultForADeliveredDemandHoldsTheVehicleUntilARepairRelease()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_481_DELIVERED_DOOR";
        const string proof = "delivered-door-proof-not-a-production-secret";
        const string cancellationId = "48100000-0000-4000-8000-000000000031";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            (await context.AcceptedDemands.SingleAsync(token)).Status = DemandExecutionStatus.Accepted;
            (await context.StationOperations.SingleAsync(token)).Status = StationOperationStatus.Prepared;
            await context.SaveChangesAsync(token);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            WireToGateStore store = new(context);
            Assert.Equal("AUTHORIZED", FirstPayload((await ExchangeAsync(
                processor, peer, state, CancellationRequest(cancellationId)))[0]).GetProperty("decision").GetString());
            (await context.AcceptedDemands.SingleAsync(token)).Status = DemandExecutionStatus.Succeeded;
            await context.SaveChangesAsync(token);
            context.ChangeTracker.Clear();
            string result = DoorUnprovenEmptyResult(
                CancellationResult(cancellationId, "48100000-0000-4000-8000-000000000032", "EMPTY"));

            string[] wire = await ExchangeAsync(processor, peer, state, result);

            AssertEndsTheReplay(wire[0], result);
            Assert.Equal("DurableAck", MessageType(wire[0]));
            context.ChangeTracker.Clear();
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, (await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == cancellationId, token)).State);
            Assert.Equal(DemandExecutionStatus.Succeeded, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
            SlotDoorHoldRow hold = await context.SlotDoorHolds.AsNoTracking().SingleAsync(token);
            Assert.Equal((cancellationId, AgvId, "[1,2]", (DateTimeOffset?)null),
                (hold.HoldId, hold.AgvId, hold.SlotsJson, hold.ReleasedAt));
            SessionReadinessDecision held = await store.DecideReadinessAsync(AgvId, 3, token);
            Assert.Equal(
                (SessionReadiness.RecoveryRequired, WireToGateStore.SlotDoorRepairReleaseRequired),
                (held.Readiness, held.ReasonCode));
            // The hold goes out to the vehicle too: one business state outside the journey naming both held slots (review
            // of #490, S3 -- the hold row alone, without this snapshot, left the vehicle showing no hold).
            string holding = Assert.Single(
                (await context.ProtocolOutbox.AsNoTracking()
                    .Where(row => row.MessageType == "VehicleBusinessStateSnapshot").ToArrayAsync(token))
                .Select(row => row.PayloadJson),
                line => line.Contains("SLOT_DOOR_LOCK_UNPROVEN_AFTER_EMPTY", StringComparison.Ordinal));
            JsonElement holdingSnapshot = PayloadOf(holding);
            Assert.Equal("RECOVERY_REQUIRED", holdingSnapshot.GetProperty("readiness").GetString());
            Assert.Equal(
                ["SLOT_DOOR_LOCK_UNPROVEN_AFTER_EMPTY/SLOT/1", "SLOT_DOOR_LOCK_UNPROVEN_AFTER_EMPTY/SLOT/2"],
                HoldFacts(holdingSnapshot));

            string sessionId = StableGuid(ReleaseRequestId, "exception-recovery-session");
            Assert.Equal("ExceptionRecoverySessionOpened",
                MessageType((await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(proof, RecoverySlots)))[0]));
            Assert.Equal("RecoveryActionAccepted",
                MessageType((await ExchangeAsync(processor, peer, state, ReleaseAction(sessionId, RecoverySlots)))[0]));
            Assert.Equal("RECORDED", PayloadOf((await ExchangeAsync(
                processor, peer, state, ReleaseRecord("48100000-0000-4000-8000-000000000033", sessionId, RecoverySlots)))[0])
                .GetProperty("outcome").GetString());
            string[] readings = await ExchangeAsync(
                processor, peer, state, SlotReadings("48100000-0000-4000-8000-000000000034", 8));
            string check = Assert.Single(readings, line => MessageType(line) == "PreDepartureSafetyCheck");
            await ExchangeAsync(
                processor, peer, state, HoldReleaseCheckResult("48100000-0000-4000-8000-000000000035", check));

            context.ChangeTracker.Clear();
            Assert.Equal(SessionReadiness.Ready, (await store.DecideReadinessAsync(AgvId, 3, token)).Readiness);
            Assert.Equal(ReleaseActionId, (await context.SlotDoorHolds.AsNoTracking().SingleAsync(token)).ReleasedByActionId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }
}
