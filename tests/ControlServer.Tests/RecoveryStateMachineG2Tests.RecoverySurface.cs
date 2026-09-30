using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// control-server#385, the recovery surface on protocol 3.0.0: the forced recovery's named hand-off (CP-0008), why a session
/// closed (<c>closedReason</c>), and the door-unproven empty settlement with its vehicle hold and repair release (CP-0009,
/// REQ-0364).
/// </summary>
public sealed partial class RecoveryStateMachineG2Tests
{
    private const string ReleaseRequestId = "41000000-0000-4000-8000-000000000385";
    private const string ReleaseEventId = "31000000-0000-4000-8000-000000000385";
    private const string ReleaseActionId = "71000000-0000-4000-8000-000000000385";
    private static readonly string[] DoorUnprovenReasonCodes = ["SLOT_DOOR_LOCK_UNPROVEN_AFTER_EMPTY"];

    // ---------------------------------------------------------------------------------------------------------------
    // Forced mechanical recovery: the hand-off is the result's record (CP-0008)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The hand-off a forced recovery ends its demand on is the one the result records -- the sublot, the named receiver,
    /// when -- not the administrator who verified the action (until control-server#385 the operator stood in for it).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedRecoveryKeepsTheHandoffItsResultRecordsNotTheVerifyingAdministrator()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_FORCED_HANDOFF_RECORD";
        const string proof = "forced-handoff-record-proof-not-a-production-secret";
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
            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
            await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);

            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(
                MechanicallyIsolatedResult(generation: 1), state, token)));

            RecoveryWorkflowRow forced = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
            Assert.Equal(RecoveryWorkflowState.Reconciled, forced.State);
            Assert.Equal(
                ("SUBLOT-001", ForcedRecoveryHandoffRecord.ReceiverName, (DateTimeOffset?)Now.AddSeconds(1)),
                (forced.HandoffSublot, forced.HandoffReceiverName, forced.HandedOverAt));
            Assert.NotEqual(OperatorId, forced.HandoffReceiverName);
            Assert.Equal(DemandExecutionStatus.Cancelled, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
            JourneyRuntimeRow runtime = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
            Assert.Equal(
                (JourneyRuntimeStage.Completed, "TERMINATED_BY_FAULT_CARGO_HANDOFF"),
                (runtime.Stage, runtime.BlockReasonCode));
            ExceptionRecoverySessionRow session = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            Assert.Equal(("CLOSED", (string?)null), (session.State, session.ClosedReason));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// An isolation on a demand whose result lacks the hand-off record, or whose record contradicts the demand, is never
    /// refused -- the result is acknowledged and kept -- and settles nothing: the demand stays blocked and not suppressed,
    /// the session closes as not reconciled so the next one can open, and the vehicle is still held for its hardware
    /// record. A result naming no demand, or another, on a session with one is refused as an identity conflict, as before. The schema forbids every one of these shapes,
    /// but nothing validates an inbound line against it (program#157: that cell needs an exit that changes nothing).
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    [InlineData("no-record")]
    [InlineData("another-sublot")]
    [InlineData("blank-receiver")]
    [InlineData("no-handover-time")]
    public async Task AnIsolationWithoutAStandingHandoffRecordIsKeptButSettlesNothing(string defect)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_FORCED_NO_RECORD";
        const string proof = "forced-no-record-proof-not-a-production-secret";
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
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
            await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token);
            JsonNode result = JsonNode.Parse(MechanicallyIsolatedResult(generation: 1))!;
            JsonNode payload = result["payload"]!;
            switch (defect)
            {
                case "no-record": payload["cargoHandoff"] = null; break;
                case "another-sublot": payload["cargoHandoff"]!["sublot"] = "SUBLOT-999"; break;
                case "blank-receiver": payload["cargoHandoff"]!["receiverName"] = " "; break;
                case "no-handover-time": payload["cargoHandoff"]!.AsObject().Remove("handedOverAt"); break;
            }

            string ack = await processor.ProcessAsync(result.ToJsonString(), state, token);

            Assert.Equal("DurableAck", MessageType(ack));
            Assert.Single(await context.RecoveryResultEvidence.AsNoTracking().ToArrayAsync(token));
            RecoveryWorkflowRow forced = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, forced.State);
            Assert.Null(forced.HandoffReceiverName);
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
            Assert.Equal(JourneyRuntimeStage.Blocked, (await context.JourneyRuntimes.AsNoTracking().SingleAsync(token)).Stage);
            await SuppressionAssertions.AssertNothingSuppressedAsync(context);
            ExceptionRecoverySessionRow session = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            Assert.Equal(("CLOSED", ServerReasonCodes.RecoveryActionResultNotReconciled), (session.State, session.ClosedReason));
            Assert.True(await store.ForcedRecoveryAwaitsHardwareRecordAsync(AgvId, token));
            Assert.Equal("ExceptionRecoverySessionOpened",
                MessageType(await processor.ProcessAsync(NextSessionRequest(proof), state, token)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // closedReason (CV-RECOVERY-SESSION-CLOSED-RESULT-NOT-RECONCILED)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A compensation that reports FAILED closes its session under <c>RECOVERY_ACTION_RESULT_NOT_RECONCILED</c>, on the
    /// wire and in the store; the demand stays blocked and a new session is accepted. The vehicle resending the result
    /// gets the first answer and no second closing, and after a reconnect the unapplied closing snapshot is replayed
    /// carrying the reason word for word.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-RECOVERY-SESSION-CLOSED-RESULT-NOT-RECONCILED")]
    public async Task ASessionClosedOnAnUnreconciledResultSaysWhyAndSaysTheSameAfterResendAndReconnect()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_CLOSED_REASON";
        const string proof = "closed-reason-proof-not-a-production-secret";
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
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            string failed = await ReachCompensationResultAsync(processor, state, proof);
            string sessionId = StableGuid(RequestId, "exception-recovery-session");

            string[] wire = await ExchangeAsync(processor, peer, state, failed);

            Assert.Equal("DurableAck", MessageType(wire[0]));
            string closing = Assert.Single(wire, IsClosedSessionSnapshot);
            Assert.Equal(ServerReasonCodes.RecoveryActionResultNotReconciled, ClosedReasonOf(closing));
            Assert.Equal(ServerReasonCodes.RecoveryActionResultNotReconciled, (await context.ExceptionRecoverySessions
                .AsNoTracking().SingleAsync(token)).ClosedReason);
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
            Assert.Equal(JourneyRuntimeStage.Blocked, (await context.JourneyRuntimes.AsNoTracking().SingleAsync(token)).Stage);
            long closedRevision = (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).Revision;

            // Resent on the same connection: answered from the first acceptance, nothing closes twice.
            string[] resent = await ExchangeAsync(processor, peer, state, failed);
            Assert.Equal(wire[0], resent[0]);
            Assert.Equal(closedRevision, (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).Revision);

            // Reconnect: the closing snapshot, never applied, is replayed into the new generation with the same reason.
            state = CurrentState(deferOutbound: true);
            List<string> handshake = [.. await ReconnectAsync(processor, peer, state)];
            await FinishHandshakeAsync(processor, peer, state, handshake);
            string replayed = Assert.Single(handshake, IsClosedSessionSnapshot);
            Assert.NotEqual(SessionGenerationOf(closing), SessionGenerationOf(replayed));
            Assert.Equal(PayloadOf(closing).GetRawText(), PayloadOf(replayed).GetRawText());
            Assert.Equal(ServerReasonCodes.RecoveryActionResultNotReconciled, ClosedReasonOf(replayed));

            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(await processor.ProcessAsync(
                InSession(NextSessionRequest(proof), state.SessionGeneration!.Value), state, token)));
            Assert.Equal(sessionId, (await context.ExceptionRecoverySessions.AsNoTracking()
                .SingleAsync(row => row.ClosedReason != null, token)).ExceptionRecoverySessionId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// Each of the three ways a session closes, by value: reconciled is null, a result that does not reconcile and a
    /// resume the vehicle refused are both <c>RECOVERY_ACTION_RESULT_NOT_RECONCILED</c> -- the registry defines the code
    /// to cover the refusal, since no result will ever come for it.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-RECOVERY-SESSION-CLOSED-RESULT-NOT-RECONCILED")]
    [InlineData("reconciled", null)]
    [InlineData("not-reconciled", "RECOVERY_ACTION_RESULT_NOT_RECONCILED")]
    [InlineData("command-rejected", "RECOVERY_ACTION_RESULT_NOT_RECONCILED")]
    public async Task EachWayASessionClosesSaysWhyByValue(string closing, string? expected)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_CLOSED_REASON_WAYS";
        const string proof = "closed-reason-ways-proof-not-a-production-secret";
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
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            if (closing == "command-rejected")
            {
                await AuthorizeResumeAfterFailedResultAsync(processor, context, state, proof);
                await processor.ProcessAsync(
                    ResumeCommandRejected(
                        "e3850000-0000-4000-8000-000000000187", StableGuid(ActionId, "recovery-command")),
                    state, token);
            }
            else
            {
                string result = await ReachCompensationResultAsync(processor, state, proof);
                await processor.ProcessAsync(
                    closing == "reconciled" ? AllEmptyCompensationResult(result) : result, state, token);
            }

            ExceptionRecoverySessionRow session = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            Assert.Equal(("CLOSED", expected), (session.State, session.ClosedReason));
            JsonElement snapshot = await LatestSessionSnapshotAsync(context, session.ExceptionRecoverySessionId);
            Assert.Equal("CLOSED", snapshot.GetProperty("state").GetString());
            Assert.Equal(expected, snapshot.GetProperty("closedReason").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A result arriving after its session closed -- the second submission of the same action -- leaves the reason the
    /// closing gave, whichever way either concluded: no new revision, no new snapshot, the stored reason unchanged.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-RECOVERY-SESSION-CLOSED-RESULT-NOT-RECONCILED")]
    [InlineData(true, "FAILED")]
    [InlineData(false, "ALL_EMPTY")]
    public async Task ALateResultNeverRewritesWhyItsSessionClosed(bool firstReconciles, string lateOutcome)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_CLOSED_REASON_LATE";
        const string proof = "closed-reason-late-proof-not-a-production-secret";
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
            (string first, string late) = await ReachTwoSubmissionsOfOneActionAsync(
                "COMPENSATE_LOAD_ALL_EMPTY", lateOutcome, processor, context, state, proof);
            await processor.ProcessAsync(firstReconciles ? AllEmptyCompensationResult(first) : first, state, token);
            ExceptionRecoverySessionRow closed = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            int snapshots = await context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "ExceptionRecoverySessionSnapshot", token);

            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(late, state, token)));

            ExceptionRecoverySessionRow after = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            Assert.Equal(firstReconciles ? null : ServerReasonCodes.RecoveryActionResultNotReconciled, after.ClosedReason);
            Assert.Equal((closed.Revision, closed.ClosedReason), (after.Revision, after.ClosedReason));
            Assert.Equal(snapshots, await context.ProtocolOutbox.CountAsync(
                row => row.MessageType == "ExceptionRecoverySessionSnapshot", token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // ALL_EMPTY_DOOR_UNPROVEN: settle as empty, hold the vehicle (CP-0009, REQ-0364)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A compensation whose light curtains prove both target slots empty while slot 2's lock cannot be proven settles
    /// the demand as the all-empty compensation it is -- ended under <c>CANCELLED_BY_LOAD_COMPENSATION</c>, suppressed,
    /// its journey closed -- and holds the vehicle: readiness is held under the repair-release reason, and a business
    /// snapshot outside the journey names each held slot, sent after the journey's closing snapshot at a higher
    /// revision. No hand-off record is formed.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    public async Task ACompensationProvingTheSlotsEmptyWithADoorUnprovenSettlesTheDemandAndHoldsTheVehicle()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_DOOR_UNPROVEN_COMPENSATE";
        const string proof = "door-unproven-compensate-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            string result = DoorUnprovenEmptyResult(await ReachCompensationResultAsync(processor, state, proof));

            string[] wire = await ExchangeAsync(processor, peer, state, result);

            Assert.Equal("DurableAck", MessageType(wire[0]));
            await AssertSettledAsEmptyAndHeldAsync(context, "CANCELLED_BY_LOAD_COMPENSATION", wire);
            ExceptionRecoverySessionRow session = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            Assert.Equal(("CLOSED", (string?)null), (session.State, session.ClosedReason));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The same for an operator's in-flight cancellation: ended under <c>CANCELLED_BY_OPERATOR</c>, suppressed in the same
    /// save, and the vehicle held.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-LOAD-CANCELLATION-EMPTY-DOOR-UNPROVEN")]
    public async Task ACancellationProvingTheSlotsEmptyWithADoorUnprovenSettlesTheDemandAndHoldsTheVehicle()
    {
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
        RecordingPeer peer = new(context);
        OnboardMessageProcessor processor = Processor(context, peer, UnusedProofVariable);
        OnboardConnectionState state = CurrentState(deferOutbound: true);
        const string cancellationId = "c3850000-0000-4000-8000-000000000001";
        Assert.Equal("AUTHORIZED", FirstPayload(await processor.ProcessAsync(
            CancellationRequest(cancellationId), state, token)).GetProperty("decision").GetString());
        string result = DoorUnprovenEmptyResult(
            CancellationResult(cancellationId, "c3850000-0000-4000-8000-000000000002", "EMPTY"));

        string[] wire = await ExchangeAsync(processor, peer, state, result);

        Assert.Equal("DurableAck", MessageType(wire[0]));
        await AssertSettledAsEmptyAndHeldAsync(context, "CANCELLED_BY_OPERATOR", wire);
    }

    /// <summary>
    /// The door-unproven outcome settles only on exactly the target slots, every one EMPTY. A slot missing, an extra
    /// slot, or one OCCUPIED or UNKNOWN is a result that does not reconcile: the demand stays blocked, nothing is
    /// suppressed, and no hold is written.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    [InlineData("slot-missing")]
    [InlineData("slot-extra")]
    [InlineData("slot-occupied")]
    [InlineData("slot-unknown")]
    public async Task ADoorUnprovenResultNotProvingEveryTargetSlotEmptySettlesNothing(string defect)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_DOOR_UNPROVEN_DEFECT";
        const string proof = "door-unproven-defect-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            JsonNode result = JsonNode.Parse(
                DoorUnprovenEmptyResult(await ReachCompensationResultAsync(processor, state, proof)))!;
            JsonArray slots = result["payload"]!["slotResults"]!.AsArray();
            switch (defect)
            {
                case "slot-missing": slots.RemoveAt(1); break;
                case "slot-extra":
                    JsonNode extra = slots[1]!.DeepClone();
                    extra["slotNo"] = 3;
                    slots.Add(extra);
                    break;
                case "slot-occupied": slots[0]!["finalPhysicalState"] = "OCCUPIED"; break;
                case "slot-unknown": slots[0]!["finalPhysicalState"] = "UNKNOWN"; break;
            }

            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(result.ToJsonString(), state, token)));

            Assert.Equal(RecoveryWorkflowState.RecoveryRequired,
                (await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).State);
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
            await SuppressionAssertions.AssertNothingSuppressedAsync(context);
            Assert.Empty(await context.SlotDoorHolds.AsNoTracking().ToArrayAsync(token));
            Assert.Equal(ServerReasonCodes.RecoveryActionResultNotReconciled,
                (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).ClosedReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// <c>ALL_EMPTY</c> keeps its meaning -- every slot EMPTY, LOCKED and RESET -- so an <c>ALL_EMPTY</c> whose lock is
    /// unknown is still a failure (never taken for the door-unproven settlement, never for proven locks), and a fault
    /// cargo handoff that says <c>ALL_EMPTY_DOOR_UNPROVEN</c> is not a hand-off. Neither settles, neither holds.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-COMPENSATION-EMPTY-DOOR-UNPROVEN")]
    [InlineData("LoadCompensationResult")]
    [InlineData("FaultCargoRecoveryResult")]
    public async Task NeitherAnAllEmptyWithALockUnprovenNorAHandoffTakesTheDoorUnprovenSettlement(string messageType)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_DOOR_UNPROVEN_NOT_OTHERS";
        const string proof = "door-unproven-not-others-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            JsonNode result;
            if (messageType == "LoadCompensationResult")
            {
                result = JsonNode.Parse(DoorUnprovenEmptyResult(await ReachCompensationResultAsync(processor, state, proof)))!;
                result["payload"]!["overallOutcome"] = "ALL_EMPTY";
            }
            else
            {
                result = JsonNode.Parse(DoorUnprovenEmptyResult(
                    await ReachUnreconciledResultAsync("FaultCargoRecoveryResult", "FAILED", processor, state, proof)))!;
            }

            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(result.ToJsonString(), state, token)));

            Assert.Equal(RecoveryWorkflowState.RecoveryRequired,
                (await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).State);
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
            Assert.Empty(await context.SlotDoorHolds.AsNoTracking().ToArrayAsync(token));
            await SuppressionAssertions.AssertNothingSuppressedAsync(context);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // HARDWARE_REPAIR_RELEASE (CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The whole release. The held vehicle's administrator opens a session without a demand over the held slots and is
    /// offered <c>HARDWARE_REPAIR_RELEASE</c>; choosing it sends the vehicle nothing. The hardware record closes the
    /// session reconciled and asks for readings (<c>PRE_MOVEMENT_RECONCILIATION</c>, after the closing snapshot), but the
    /// vehicle stays held on the record alone. Readings received after the record, every held slot LOCKED, RESET and
    /// EMPTY, bring a <c>HOLD_RELEASE</c> check with no demand, leg or station -- still held. Its SAFE answer lifts the
    /// hold: the session is ready, and the business state goes out with no door fact.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task ARepairReleaseLiftsTheHoldOnlyAfterItsRecordFreshReadingsAndASafeHoldReleaseCheck()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE";
        const string proof = "repair-release-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                await HoldTheVehicleAsync(context, proofVariable, proof);
            WireToGateStore store = new(context);
            string sessionId = StableGuid(ReleaseRequestId, "exception-recovery-session");

            string[] opened = await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(proof, RecoverySlots));
            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(opened[0]));
            JsonElement openSnapshot = SessionSnapshotIn(opened, sessionId);
            Assert.Contains("HARDWARE_REPAIR_RELEASE",
                openSnapshot.GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()));

            string[] accepted = await ExchangeAsync(processor, peer, state, ReleaseAction(sessionId, RecoverySlots));
            Assert.Equal("RecoveryActionAccepted", MessageType(accepted[0]));
            Assert.DoesNotContain(accepted, line => MessageType(line).EndsWith("Command", StringComparison.Ordinal));
            Assert.Null((await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == ReleaseActionId, token)).CommandMessageId);

            string[] recorded = await ExchangeAsync(
                processor, peer, state, ReleaseRecord("e3850000-0000-4000-8000-000000000501", sessionId, RecoverySlots));
            Assert.Equal("RECORDED", PayloadOf(recorded[0]).GetProperty("outcome").GetString());
            int closingAt = Array.FindIndex(recorded, IsClosedSessionSnapshot);
            int requestAt = Array.FindIndex(recorded, line => MessageType(line) == "SafetyStateSnapshotRequested");
            Assert.True(closingAt > 0 && requestAt > closingAt, string.Join(" | ", recorded.Select(MessageType)));
            Assert.Null(ClosedReasonOf(recorded[closingAt]));
            Assert.Equal("PRE_MOVEMENT_RECONCILIATION", PayloadOf(recorded[requestAt]).GetProperty("reason").GetString());
            Assert.Equal(WireToGateStore.SlotDoorRepairReleaseRequired, (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);

            string[] readings = await ExchangeAsync(processor, peer, state, SlotReadings("e3850000-0000-4000-8000-000000000502", 8));
            Assert.Equal("SnapshotAppliedAck", MessageType(readings[0]));
            string check = Assert.Single(readings, line => MessageType(line) == "PreDepartureSafetyCheck");
            JsonElement checkPayload = PayloadOf(check);
            Assert.Equal("HOLD_RELEASE", checkPayload.GetProperty("checkPurpose").GetString());
            Assert.Equal(JsonValueKind.Null, checkPayload.GetProperty("demandId").ValueKind);
            Assert.Equal(JsonValueKind.Null, checkPayload.GetProperty("movementLegId").ValueKind);
            Assert.Equal(JsonValueKind.Null, checkPayload.GetProperty("targetStationId").ValueKind);
            Assert.Equal(8, checkPayload.GetProperty("expectedSafetyStateVersion").GetInt64());
            Assert.Equal(WireToGateStore.SlotDoorRepairReleaseRequired, (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);

            string[] answered = await ExchangeAsync(
                processor, peer, state, HoldReleaseCheckResult("e3850000-0000-4000-8000-000000000503", check));

            Assert.Equal("DurableAck", MessageType(answered[0]));
            Assert.Contains(answered, line => MessageType(line) == "SessionReadiness");
            JsonElement released = PayloadOf(Assert.Single(answered, line => MessageType(line) == "VehicleBusinessStateSnapshot"));
            Assert.Equal("READY", released.GetProperty("readiness").GetString());
            Assert.Empty(released.GetProperty("blockingFacts").EnumerateArray());
            Assert.Equal(SessionReadiness.Ready, (await store.DecideReadinessAsync(AgvId, 3, token)).Readiness);
            SlotDoorHoldRow hold = await context.SlotDoorHolds.AsNoTracking().SingleAsync(token);
            Assert.Equal(ReleaseActionId, hold.ReleasedByActionId);
            Assert.Equal(RecoveryWorkflowState.Reconciled, (await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == ReleaseActionId, token)).State);
            Assert.NotNull((await context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(row => row.MessageType == "PreDepartureSafetyCheck", token)).AcknowledgedAt);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The release is offered only while the vehicle is held, on a session without a demand, over exactly the held slots.
    /// Anywhere else it is refused as not allowed and nothing starts -- and a session over the held slots of a vehicle that
    /// is not held does not list it.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    [InlineData("not-held")]
    [InlineData("other-slots")]
    public async Task ARepairReleaseIsRefusedWhereItIsNotOffered(string where)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_REFUSED";
        const string proof = "repair-release-refused-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer;
            OnboardMessageProcessor processor;
            OnboardConnectionState state;
            if (where == "not-held")
            {
                await SeedBlockedJourneyAsync(context, productionShapedSession: true);
                peer = new RecordingPeer(context);
                processor = Processor(context, peer, proofVariable);
                state = CurrentState(deferOutbound: true);
                string reconciled = AllEmptyCompensationResult(await ReachCompensationResultAsync(processor, state, proof));
                Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(reconciled, state, token)));
            }
            else
            {
                (peer, processor, state) = await HoldTheVehicleAsync(context, proofVariable, proof);
            }
            int[] slots = where == "other-slots" ? [1] : RecoverySlots;
            string sessionId = StableGuid(ReleaseRequestId, "exception-recovery-session");

            string[] opened = await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(proof, slots));
            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(opened[0]));
            Assert.DoesNotContain("HARDWARE_REPAIR_RELEASE",
                SessionSnapshotIn(opened, sessionId)
                    .GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()));
            string refused = await processor.ProcessAsync(ReleaseAction(sessionId, slots), state, token);

            Assert.Equal("RecoveryActionRejected", MessageType(refused));
            Assert.Equal(ServerReasonCodes.ActionNotAllowedInState,
                FirstPayload(refused).GetProperty("problem").GetProperty("reasonCode").GetString());
            Assert.False(await context.RecoveryWorkflows.AnyAsync(row => row.WorkflowId == ReleaseActionId, token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A session on a demand is never offered the release, even over the held slots of a held vehicle: choosing it is
    /// refused as not allowed.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task ARepairReleaseOnASessionWithADemandIsRefused()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_DEMAND";
        const string proof = "repair-release-demand-proof-not-a-production-secret";
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
            context.SlotDoorHolds.Add(new SlotDoorHoldRow
            {
                HoldId = "f3850000-0000-4000-8000-000000000001",
                AgvId = AgvId,
                DemandId = "f3850000-0000-4000-8000-000000000002",
                SlotsJson = "[1,2]",
                HeldAt = Now.AddMinutes(-1)
            });
            await context.SaveChangesAsync(token);
            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);

            string refused = await processor.ProcessAsync(RecoveryAction("HARDWARE_REPAIR_RELEASE"), state, token);

            Assert.Equal("RecoveryActionRejected", MessageType(refused));
            Assert.Equal(ServerReasonCodes.ActionNotAllowedInState,
                FirstPayload(refused).GetProperty("problem").GetProperty("reasonCode").GetString());
            Assert.Empty(await context.RecoveryWorkflows.AsNoTracking().ToArrayAsync(token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// Readings the server received before the record prove nothing about the repair: a snapshot taken while the release
    /// waits for its record is not judged. The first readings after the record decide -- here slot 2's lock reads
    /// UNKNOWN, so the record is spent: no check goes out, the vehicle stays held, and a new session is offered the
    /// release again, which a new record and proving readings then carry through.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task OnlyReadingsAfterTheRecordCountAndUnprovenFirstReadingsSpendTheRecord()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_READINGS";
        const string proof = "repair-release-readings-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                await HoldTheVehicleAsync(context, proofVariable, proof);
            WireToGateStore store = new(context);
            string sessionId = StableGuid(ReleaseRequestId, "exception-recovery-session");
            await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(proof, RecoverySlots));
            await ExchangeAsync(processor, peer, state, ReleaseAction(sessionId, RecoverySlots));

            string[] early = await ExchangeAsync(processor, peer, state, SlotReadings("e3850000-0000-4000-8000-000000000601", 8));
            Assert.DoesNotContain(early, line => MessageType(line) == "PreDepartureSafetyCheck");

            await ExchangeAsync(processor, peer, state,
                ReleaseRecord("e3850000-0000-4000-8000-000000000602", sessionId, RecoverySlots));
            string[] unproven = await ExchangeAsync(
                processor, peer, state, SlotReadings("e3850000-0000-4000-8000-000000000603", 9, unprovenSlot: 2));

            Assert.DoesNotContain(unproven, line => MessageType(line) == "PreDepartureSafetyCheck");
            RecoveryWorkflowRow spent = await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == ReleaseActionId, token);
            Assert.Equal((RecoveryWorkflowState.RecoveryRequired, "READINGS_UNPROVEN"), (spent.State, spent.Outcome));
            Assert.Equal(WireToGateStore.SlotDoorRepairReleaseRequired, (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);
            string[] proving = await ExchangeAsync(processor, peer, state, SlotReadings("e3850000-0000-4000-8000-000000000604", 10));
            Assert.DoesNotContain(proving, line => MessageType(line) == "PreDepartureSafetyCheck");

            const string againRequestId = "41000000-0000-4000-8000-000000000386";
            const string againActionId = "71000000-0000-4000-8000-000000000386";
            string againSessionId = StableGuid(againRequestId, "exception-recovery-session");
            string[] reopened = await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(
                proof, RecoverySlots, "e3850000-0000-4000-8000-000000000605", againRequestId));
            Assert.Contains("HARDWARE_REPAIR_RELEASE",
                SessionSnapshotIn(reopened, againSessionId)
                    .GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal("RecoveryActionAccepted", MessageType((await ExchangeAsync(processor, peer, state,
                ReleaseAction(againSessionId, RecoverySlots, againActionId, "e3850000-0000-4000-8000-000000000606")))[0]));
            await ExchangeAsync(processor, peer, state, ReleaseRecord(
                "e3850000-0000-4000-8000-000000000607", againSessionId, RecoverySlots, againActionId));
            string check = Assert.Single(
                await ExchangeAsync(processor, peer, state, SlotReadings("e3850000-0000-4000-8000-000000000608", 11)),
                line => MessageType(line) == "PreDepartureSafetyCheck");
            await ExchangeAsync(processor, peer, state, HoldReleaseCheckResult("e3850000-0000-4000-8000-000000000609", check));

            Assert.Equal(SessionReadiness.Ready, (await store.DecideReadinessAsync(AgvId, 3, token)).Readiness);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A hardware record on any other action does not lift the door hold: here the held vehicle's administrator takes a
    /// forced recovery over the held slots and records the hardware after it, and the vehicle is still held for its
    /// repair release.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task AHardwareRecordOnAnotherActionDoesNotLiftTheDoorHold()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_OTHER_RECORD";
        const string proof = "repair-release-other-record-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                await HoldTheVehicleAsync(context, proofVariable, proof);
            WireToGateStore store = new(context);
            string sessionId = StableGuid(ReleaseRequestId, "exception-recovery-session");
            await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(proof, RecoverySlots));
            Assert.Equal("RecoveryActionAccepted", MessageType((await ExchangeAsync(processor, peer, state,
                ReleaseAction(sessionId, RecoverySlots, action: "FORCED_MECHANICAL_RECOVERY")))[0]));
            string forcedResult = Envelope("e3850000-0000-4000-8000-000000000701", "ForcedMechanicalRecoveryResult", new
            {
                exceptionRecoverySessionId = sessionId,
                recoveryActionId = ReleaseActionId,
                forcedRecoveryGeneration = 1,
                outcome = "MECHANICALLY_ISOLATED",
                slots = RecoverySlots,
                @operator = Operator(),
                observedAt = Now,
                electronicEmptyProven = false,
                vehicleReadyProven = false,
                demandId = (string?)null,
                cargoHandoff = (object?)null
            });
            await ExchangeAsync(processor, peer, state, forcedResult);
            await store.ApplyRecoveryReportAsync(
                AgvId, 3, "f0000000-0000-4000-8000-000000000385", forcedRecoveryGeneration: 1, null, "NONE", [], [], [], token);

            string[] recorded = await ExchangeAsync(
                processor, peer, state, ReleaseRecord("e3850000-0000-4000-8000-000000000702", sessionId, RecoverySlots));

            Assert.Equal("RECORDED", PayloadOf(recorded[0]).GetProperty("outcome").GetString());
            Assert.DoesNotContain(recorded, line => MessageType(line) == "SafetyStateSnapshotRequested");
            Assert.False(await store.ForcedRecoveryAwaitsHardwareRecordAsync(AgvId, token));
            Assert.Equal(WireToGateStore.SlotDoorRepairReleaseRequired, (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);
            Assert.Null((await context.SlotDoorHolds.AsNoTracking().SingleAsync(token)).ReleasedAt);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// Only a SAFE answer to the release's own check, saying HOLD_RELEASE, lifts the hold. An answer saying DEPARTURE
    /// under the same correlation is not a release (and the runtime never takes a HOLD_RELEASE answer for a departure,
    /// control-server#382): it spends the release like an UNSAFE or UNKNOWN HOLD_RELEASE answer does, rather than leave it
    /// waiting for an answer that will not come (review M1; that a new release then lifts the hold is the next test's).
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    [InlineData("DEPARTURE", "SAFE", RecoveryWorkflowState.RecoveryRequired)]
    [InlineData("HOLD_RELEASE", "UNSAFE", RecoveryWorkflowState.RecoveryRequired)]
    [InlineData("HOLD_RELEASE", "UNKNOWN", RecoveryWorkflowState.RecoveryRequired)]
    public async Task OnlyASafeHoldReleaseAnswerToTheReleasesOwnCheckLiftsTheHold(
        string purpose, string outcome, RecoveryWorkflowState expected)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_ANSWER";
        const string proof = "repair-release-answer-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                await HoldTheVehicleAsync(context, proofVariable, proof);
            WireToGateStore store = new(context);
            string sessionId = StableGuid(ReleaseRequestId, "exception-recovery-session");
            await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(proof, RecoverySlots));
            await ExchangeAsync(processor, peer, state, ReleaseAction(sessionId, RecoverySlots));
            await ExchangeAsync(processor, peer, state,
                ReleaseRecord("e3850000-0000-4000-8000-000000000801", sessionId, RecoverySlots));
            string check = Assert.Single(
                await ExchangeAsync(processor, peer, state, SlotReadings("e3850000-0000-4000-8000-000000000802", 8)),
                line => MessageType(line) == "PreDepartureSafetyCheck");

            string[] answered = await ExchangeAsync(processor, peer, state,
                HoldReleaseCheckResult("e3850000-0000-4000-8000-000000000803", check, outcome, purpose));

            Assert.Equal("DurableAck", MessageType(answered[0]));
            Assert.DoesNotContain(answered, line => MessageType(line) == "VehicleBusinessStateSnapshot");
            Assert.Equal(expected, (await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == ReleaseActionId, token)).State);
            Assert.Null((await context.SlotDoorHolds.AsNoTracking().SingleAsync(token)).ReleasedAt);
            Assert.Equal(WireToGateStore.SlotDoorRepairReleaseRequired, (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The vehicle does not answer the release's check with a SAFE HOLD_RELEASE result (control-server#385 review M1): it
    /// refuses it with a <c>ProtocolProblem</c> -- the onboard's answer to a check whose safety version has moved on
    /// (<c>PREDEPARTURE_CHECK_EXPIRED</c>) or whose purpose it will not run (<c>ACTION_NOT_ALLOWED_IN_STATE</c>), with no
    /// result -- or answers it as a DEPARTURE check. Each spends the release: its check is settled and never replayed, the
    /// vehicle stays held, and a new session is offered the release again and lifts the hold with it. No store edit.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    [InlineData("PREDEPARTURE_CHECK_EXPIRED")]
    [InlineData("ACTION_NOT_ALLOWED_IN_STATE")]
    [InlineData("DEPARTURE")]
    public async Task ARefusedOrMisansweredReleaseCheckSpendsTheReleaseAndANewOneLiftsTheHold(string answer)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_REFUSED";
        const string proof = "repair-release-refused-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                await HoldTheVehicleAsync(context, proofVariable, proof);
            WireToGateStore store = new(context);
            string sessionId = StableGuid(ReleaseRequestId, "exception-recovery-session");
            await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(proof, RecoverySlots));
            await ExchangeAsync(processor, peer, state, ReleaseAction(sessionId, RecoverySlots));
            await ExchangeAsync(processor, peer, state,
                ReleaseRecord("e3850000-0000-4000-8000-000000000a01", sessionId, RecoverySlots));
            string check = Assert.Single(
                await ExchangeAsync(processor, peer, state, SlotReadings("e3850000-0000-4000-8000-000000000a02", 8)),
                line => MessageType(line) == "PreDepartureSafetyCheck");
            string checkMessageId;
            using (JsonDocument checkDocument = JsonDocument.Parse(check))
            {
                checkMessageId = checkDocument.RootElement.GetProperty("messageId").GetString()!;
            }

            if (answer == "DEPARTURE")
            {
                await ExchangeAsync(processor, peer, state,
                    HoldReleaseCheckResult("e3850000-0000-4000-8000-000000000a03", check, "SAFE", "DEPARTURE"));
            }
            else
            {
                Assert.Equal(string.Empty, await processor.ProcessAsync(Envelope(
                    "e3850000-0000-4000-8000-000000000a03",
                    "ProtocolProblem",
                    new
                    {
                        rejectedMessageId = checkMessageId,
                        rejectedMessageType = "PreDepartureSafetyCheck",
                        problem = new { reasonCode = answer, fieldPath = (string?)null, displayMessage = (string?)null }
                    }), state, token));
            }

            RecoveryWorkflowRow spent = await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == ReleaseActionId, token);
            Assert.Equal(
                (RecoveryWorkflowState.RecoveryRequired, answer == "DEPARTURE" ? "CHECK_ANSWER_MISMATCHED" : answer),
                (spent.State, spent.Outcome));
            Assert.NotNull((await context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(row => row.MessageId == checkMessageId, token)).AcknowledgedAt);
            Assert.Null((await context.SlotDoorHolds.AsNoTracking().SingleAsync(token)).ReleasedAt);
            Assert.Equal(WireToGateStore.SlotDoorRepairReleaseRequired, (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);

            const string againRequestId = "41000000-0000-4000-8000-000000000387";
            const string againActionId = "71000000-0000-4000-8000-000000000387";
            string againSessionId = StableGuid(againRequestId, "exception-recovery-session");
            string[] reopened = await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(
                proof, RecoverySlots, "e3850000-0000-4000-8000-000000000a04", againRequestId));
            Assert.Contains("HARDWARE_REPAIR_RELEASE",
                SessionSnapshotIn(reopened, againSessionId)
                    .GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal("RecoveryActionAccepted", MessageType((await ExchangeAsync(processor, peer, state,
                ReleaseAction(againSessionId, RecoverySlots, againActionId, "e3850000-0000-4000-8000-000000000a05")))[0]));
            await ExchangeAsync(processor, peer, state, ReleaseRecord(
                "e3850000-0000-4000-8000-000000000a06", againSessionId, RecoverySlots, againActionId));
            string[] readings = await ExchangeAsync(
                processor, peer, state, SlotReadings("e3850000-0000-4000-8000-000000000a07", 9));
            string againCheck = Assert.Single(readings, line => MessageType(line) == "PreDepartureSafetyCheck");
            Assert.DoesNotContain(readings, line => line.Contains(checkMessageId, StringComparison.Ordinal));
            await ExchangeAsync(processor, peer, state,
                HoldReleaseCheckResult("e3850000-0000-4000-8000-000000000a08", againCheck));

            Assert.NotNull((await context.SlotDoorHolds.AsNoTracking().SingleAsync(token)).ReleasedAt);
            Assert.Equal(SessionReadiness.Ready, (await store.DecideReadinessAsync(AgvId, 3, token)).Readiness);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A release takes one record (control-server#385 review S1). A second record on the same release, once the first has
    /// been spent on unproven readings or has lifted the hold, is refused: it neither pulls the release back to waiting for
    /// readings nor adds a second record the release's check would have to choose between.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    [InlineData("spent")]
    [InlineData("lifted")]
    public async Task ASecondRecordOnARepairReleaseIsRefusedAndChangesNothing(string first)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_SECOND_RECORD";
        const string proof = "repair-release-second-record-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                await HoldTheVehicleAsync(context, proofVariable, proof);
            string sessionId = StableGuid(ReleaseRequestId, "exception-recovery-session");
            await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(proof, RecoverySlots));
            await ExchangeAsync(processor, peer, state, ReleaseAction(sessionId, RecoverySlots));
            await ExchangeAsync(processor, peer, state,
                ReleaseRecord("e3850000-0000-4000-8000-000000000b01", sessionId, RecoverySlots));
            if (first == "spent")
            {
                await ExchangeAsync(processor, peer, state,
                    SlotReadings("e3850000-0000-4000-8000-000000000b02", 8, unprovenSlot: 2));
            }
            else
            {
                string check = Assert.Single(
                    await ExchangeAsync(processor, peer, state, SlotReadings("e3850000-0000-4000-8000-000000000b02", 8)),
                    line => MessageType(line) == "PreDepartureSafetyCheck");
                await ExchangeAsync(processor, peer, state,
                    HoldReleaseCheckResult("e3850000-0000-4000-8000-000000000b03", check));
            }
            RecoveryWorkflowRow before = await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == ReleaseActionId, token);
            SlotDoorHoldRow holdBefore = await context.SlotDoorHolds.AsNoTracking().SingleAsync(token);
            Assert.Equal(
                first == "spent" ? RecoveryWorkflowState.RecoveryRequired : RecoveryWorkflowState.Reconciled, before.State);

            string[] second = await ExchangeAsync(processor, peer, state,
                ReleaseRecord("e3850000-0000-4000-8000-000000000b04", sessionId, RecoverySlots));

            Assert.Equal("REJECTED", PayloadOf(second[0]).GetProperty("outcome").GetString());
            RecoveryWorkflowRow after = await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == ReleaseActionId, token);
            Assert.Equal((before.State, before.Outcome, before.CommandMessageId), (after.State, after.Outcome, after.CommandMessageId));
            Assert.Equal(1, await context.HardwareRecoveryRecords.AsNoTracking()
                .CountAsync(row => row.RecoveryActionId == ReleaseActionId, token));
            SlotDoorHoldRow holdAfter = await context.SlotDoorHolds.AsNoTracking().SingleAsync(token);
            Assert.Equal((holdBefore.ReleasedAt, holdBefore.ReleasedByActionId), (holdAfter.ReleasedAt, holdAfter.ReleasedByActionId));
            Assert.DoesNotContain(second, line => MessageType(line) is "SafetyStateSnapshotRequested" or "PreDepartureSafetyCheck");
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The vehicle drops off after the record, before any readings: the release does not stall. Once the new
    /// connection's handshake is over, the first message the vehicle sends is answered together with a request for
    /// readings (<c>PRE_MOVEMENT_RECONCILIATION</c>), once per connection; the handshake's own snapshot is never judged.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task AfterAReconnectTheReleaseAsksForItsReadingsAgain()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_RECONNECT";
        const string proof = "repair-release-reconnect-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                await HoldTheVehicleAsync(context, proofVariable, proof);
            string sessionId = StableGuid(ReleaseRequestId, "exception-recovery-session");
            await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(proof, RecoverySlots));
            await ExchangeAsync(processor, peer, state, ReleaseAction(sessionId, RecoverySlots));
            await ExchangeAsync(processor, peer, state,
                ReleaseRecord("e3850000-0000-4000-8000-000000000901", sessionId, RecoverySlots));

            state = CurrentState(deferOutbound: true);
            List<string> handshake = [.. await ReconnectAsync(processor, peer, state)];
            await FinishHandshakeAsync(processor, peer, state, handshake);
            Assert.DoesNotContain(handshake, line => MessageType(line) is "SafetyStateSnapshotRequested" or "PreDepartureSafetyCheck");
            long generation = state.SessionGeneration!.Value;
            string[] first = await ExchangeAsync(processor, peer, state, InSession(Envelope(
                "e3850000-0000-4000-8000-000000000902", "Heartbeat", new { sequence = 1 }), generation));
            string[] second = await ExchangeAsync(processor, peer, state, InSession(Envelope(
                "e3850000-0000-4000-8000-000000000903", "Heartbeat", new { sequence = 2 }), generation));

            string request = Assert.Single(first, line => MessageType(line) == "SafetyStateSnapshotRequested");
            Assert.Equal("PRE_MOVEMENT_RECONCILIATION", PayloadOf(request).GetProperty("reason").GetString());
            Assert.DoesNotContain(second, line => MessageType(line) == "SafetyStateSnapshotRequested");
            Assert.Null((await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == ReleaseActionId, token)).CommandMessageId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// What one release lifts, and that it lifts it once. The vehicle carries two holds (slots 1 and 2, from two
    /// settlements); the release session over both lifts both, naming itself and the time. A third hold that arrives after
    /// the record -- a settlement the record could not have attested to -- stays. The vehicle resending the SAFE answer under
    /// a new messageId, later by the clock, rewrites nothing: <c>ReleasedAt</c> and <c>ReleasedByActionId</c> stand as first
    /// written, and no second business snapshot goes out.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task ARepairReleaseLiftsTheHoldsOnFileAtItsRecordOnceAndAResendRewritesNothing()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_ONCE";
        const string proof = "repair-release-once-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            context.SlotDoorHolds.AddRange(
                Hold("f3850000-0000-4000-8000-000000000301", "[1]", Now.AddMinutes(-2)),
                Hold("f3850000-0000-4000-8000-000000000302", "[2]", Now.AddMinutes(-1)));
            await context.SaveChangesAsync(token);
            MovableTimeProvider clock = new(Now);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), clock, Configuration(proofVariable), peer);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            string sessionId = StableGuid(ReleaseRequestId, "exception-recovery-session");
            Assert.Contains("HARDWARE_REPAIR_RELEASE", SessionSnapshotIn(
                    await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(proof, RecoverySlots)), sessionId)
                .GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()));
            await ExchangeAsync(processor, peer, state, ReleaseAction(sessionId, RecoverySlots));
            await ExchangeAsync(processor, peer, state,
                ReleaseRecord("e3850000-0000-4000-8000-000000000311", sessionId, RecoverySlots));
            clock.Current = Now.AddMinutes(1);
            context.SlotDoorHolds.Add(Hold("f3850000-0000-4000-8000-000000000303", "[1]", clock.Current));
            await context.SaveChangesAsync(token);
            string check = Assert.Single(
                await ExchangeAsync(processor, peer, state, SlotReadings("e3850000-0000-4000-8000-000000000312", 8)),
                line => MessageType(line) == "PreDepartureSafetyCheck");
            clock.Current = Now.AddMinutes(2);

            string[] answered = await ExchangeAsync(
                processor, peer, state, HoldReleaseCheckResult("e3850000-0000-4000-8000-000000000313", check));

            SlotDoorHoldRow[] holds = await context.SlotDoorHolds.AsNoTracking().OrderBy(row => row.HoldId).ToArrayAsync(token);
            Assert.Equal(
                [
                    ("f3850000-0000-4000-8000-000000000301", (string?)ReleaseActionId, (DateTimeOffset?)Now.AddMinutes(2)),
                    ("f3850000-0000-4000-8000-000000000302", ReleaseActionId, Now.AddMinutes(2)),
                    ("f3850000-0000-4000-8000-000000000303", null, null)
                ],
                holds.Select(row => (row.HoldId, row.ReleasedByActionId, row.ReleasedAt)).ToArray());
            JsonElement released = PayloadOf(Assert.Single(answered, line => MessageType(line) == "VehicleBusinessStateSnapshot"));
            Assert.Equal("RECOVERY_REQUIRED", released.GetProperty("readiness").GetString());
            Assert.Equal("1", string.Join(",", released.GetProperty("blockingFacts").EnumerateArray()
                .Select(fact => fact.GetProperty("subjectId").GetString())));

            clock.Current = Now.AddMinutes(5);
            string[] resent = await ExchangeAsync(
                processor, peer, state, HoldReleaseCheckResult("e3850000-0000-4000-8000-000000000314", check));

            Assert.Equal("DurableAck", MessageType(resent[0]));
            Assert.DoesNotContain(resent, line => MessageType(line) == "VehicleBusinessStateSnapshot");
            Assert.Equal(
                holds.Select(row => (row.HoldId, row.ReleasedByActionId, row.ReleasedAt)).ToArray(),
                (await context.SlotDoorHolds.AsNoTracking().OrderBy(row => row.HoldId).ToArrayAsync(token))
                    .Select(row => (row.HoldId, row.ReleasedByActionId, row.ReleasedAt)).ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A release lifts only the holds its slots cover. The vehicle is held over slot 1 and the release is taken over slot 1;
    /// while it runs, a second settlement holds slot 2 -- on file before the record by the clock, so what keeps it standing
    /// is the slot, not the time. The SAFE answer lifts the slot-1 hold alone: the slot-2 hold stands, the vehicle stays
    /// unready, and the shared new-purpose verdict (transport, idle return, charging) still refuses it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task ARepairReleaseLeavesAHoldOnSlotsItDoesNotCoverAndTheVehicleStaysOutOfWork()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_PARTIAL";
        const string proof = "repair-release-partial-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            await SeedBlockedJourneyAsync(context, productionShapedSession: true);
            // The seeded load is settled, so that nothing but the holds keeps the vehicle out of work.
            (await context.StationOperations.SingleAsync(token)).Status = StationOperationStatus.Cancelled;
            context.SlotDoorHolds.Add(Hold("f3850000-0000-4000-8000-000000000401", "[1]", Now.AddMinutes(-3)));
            await context.SaveChangesAsync(token);
            RecordingPeer peer = new(context);
            OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
            OnboardConnectionState state = CurrentState(deferOutbound: true);
            WireToGateStore store = new(context);
            int[] slotOne = [1];
            string sessionId = StableGuid(ReleaseRequestId, "exception-recovery-session");
            await ExchangeAsync(processor, peer, state, ReleaseSessionRequest(proof, slotOne));
            Assert.Equal("RecoveryActionAccepted",
                MessageType((await ExchangeAsync(processor, peer, state, ReleaseAction(sessionId, slotOne)))[0]));
            context.SlotDoorHolds.Add(Hold("f3850000-0000-4000-8000-000000000402", "[2]", Now.AddMinutes(-2)));
            await context.SaveChangesAsync(token);
            await ExchangeAsync(processor, peer, state,
                ReleaseRecord("e3850000-0000-4000-8000-000000000411", sessionId, slotOne));
            string check = Assert.Single(
                await ExchangeAsync(processor, peer, state, SlotReadings("e3850000-0000-4000-8000-000000000412", 8)),
                line => MessageType(line) == "PreDepartureSafetyCheck");

            await ExchangeAsync(processor, peer, state, HoldReleaseCheckResult("e3850000-0000-4000-8000-000000000413", check));

            Assert.Equal(
                [
                    ("f3850000-0000-4000-8000-000000000401", (string?)ReleaseActionId, true),
                    ("f3850000-0000-4000-8000-000000000402", null, false)
                ],
                (await context.SlotDoorHolds.AsNoTracking().OrderBy(row => row.HoldId).ToArrayAsync(token))
                    .Select(row => (row.HoldId, row.ReleasedByActionId, row.ReleasedAt is not null)).ToArray());
            Assert.Equal(WireToGateStore.SlotDoorRepairReleaseRequired, (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);
            Assert.Equal(
                ControlServer.Host.Runtime.Dispatch.DispatchReasonCodes.VehicleSlotDoorHold,
                await ControlServer.Host.Runtime.Dispatch.Criteria.VehicleNewPurposeReadiness.BlockVerdictAsync(
                    new VehicleFaultStore(context), context, AgvId, token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    private static SlotDoorHoldRow Hold(string holdId, string slotsJson, DateTimeOffset heldAt) => new()
    {
        HoldId = holdId,
        AgvId = AgvId,
        DemandId = DemandId,
        SlotsJson = slotsJson,
        HeldAt = heldAt
    };

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A ready vehicle whose load needed recovery, compensated with its slots proven empty and slot 2's lock unproven:
    /// the demand settled, the vehicle held.
    /// </summary>
    private static async Task<(RecordingPeer Peer, OnboardMessageProcessor Processor, OnboardConnectionState State)>
        HoldTheVehicleAsync(ControlServerDbContext context, string proofVariable, string proof)
    {
        await SeedBlockedJourneyAsync(context, productionShapedSession: true);
        RecordingPeer peer = new(context);
        OnboardMessageProcessor processor = Processor(context, peer, proofVariable);
        OnboardConnectionState state = CurrentState(deferOutbound: true);
        string result = DoorUnprovenEmptyResult(await ReachCompensationResultAsync(processor, state, proof));
        Assert.Equal("DurableAck", MessageType((await ExchangeAsync(processor, peer, state, result))[0]));
        Assert.Single(await context.SlotDoorHolds.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
        return (peer, processor, state);
    }

    private static async Task AssertSettledAsEmptyAndHeldAsync(ControlServerDbContext context, string ending, string[] wire)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        RecoveryWorkflowRow workflow = await context.RecoveryWorkflows.AsNoTracking()
            .SingleAsync(row => row.ResultMessageId != null, token);
        Assert.Equal((RecoveryWorkflowState.Reconciled, "ALL_EMPTY_DOOR_UNPROVEN"), (workflow.State, workflow.Outcome));
        Assert.Null(workflow.HandoffId);
        Assert.Null(workflow.HandoffReceiverName);
        Assert.Equal(DemandExecutionStatus.Cancelled, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
        Assert.Equal(StationOperationStatus.Cancelled, (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status);
        JourneyRuntimeRow runtime = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
        Assert.Equal((JourneyRuntimeStage.Completed, ending), (runtime.Stage, runtime.BlockReasonCode));
        await SuppressionAssertions.AssertTheDemandSuppressedAsync(context, ending);

        SlotDoorHoldRow hold = await context.SlotDoorHolds.AsNoTracking().SingleAsync(token);
        Assert.Equal((workflow.WorkflowId, AgvId, DemandId, "[1,2]", (DateTimeOffset?)null),
            (hold.HoldId, hold.AgvId, hold.DemandId, hold.SlotsJson, hold.ReleasedAt));
        SessionReadinessDecision held = await new WireToGateStore(context).DecideReadinessAsync(AgvId, 3, token);
        Assert.Equal(
            (SessionReadiness.RecoveryRequired, WireToGateStore.SlotDoorRepairReleaseRequired),
            (held.Readiness, held.ReasonCode));
        Assert.Equal("SESSION_RECOVERY_REQUIRED", ProtocolErrorCodes.ToSessionReadinessReasonCode(held.ReasonCode));

        // The hold goes out outside the journey, after the journey's closing snapshot and above its revision.
        string[] business = [.. wire.Where(line => MessageType(line) == "VehicleBusinessStateSnapshot")];
        Assert.Equal(2, business.Length);
        JsonElement closing = PayloadOf(business[0]);
        JsonElement holding = PayloadOf(business[1]);
        Assert.Empty(closing.GetProperty("blockingFacts").EnumerateArray());
        Assert.True(holding.GetProperty("vehicleBusinessStateRevision").GetInt64() >
                    closing.GetProperty("vehicleBusinessStateRevision").GetInt64());
        Assert.Equal("RECOVERY_REQUIRED", holding.GetProperty("readiness").GetString());
        Assert.Equal(JsonValueKind.Null, holding.GetProperty("activePurpose").ValueKind);
        Assert.Equal(
            ["SLOT_DOOR_LOCK_UNPROVEN_AFTER_EMPTY/SLOT/1", "SLOT_DOOR_LOCK_UNPROVEN_AFTER_EMPTY/SLOT/2"],
            holding.GetProperty("blockingFacts").EnumerateArray().Select(fact =>
                $"{fact.GetProperty("reasonCode").GetString()}/{fact.GetProperty("subjectType").GetString()}/" +
                fact.GetProperty("subjectId").GetString()).ToArray());
        ProtocolOutboxRow[] rows = await context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "VehicleBusinessStateSnapshot").ToArrayAsync(token);
        Assert.True(rows.Single(row => row.PayloadJson == business[1]).CreatedAt >
                    rows.Single(row => row.PayloadJson == business[0]).CreatedAt);
    }

    /// <summary>
    /// The unsent line from <see cref="ReachCompensationResultAsync"/> or a cancellation result, turned into
    /// <c>ALL_EMPTY_DOOR_UNPROVEN</c>: both slots empty, slot 1 locked and reset, slot 2's lock unknown.
    /// </summary>
    private static string DoorUnprovenEmptyResult(string line)
    {
        JsonNode node = JsonNode.Parse(line)!;
        node["payload"]!["overallOutcome"] = "ALL_EMPTY_DOOR_UNPROVEN";
        node["payload"]!["slotResults"] = JsonSerializer.SerializeToNode(new object[]
        {
            new
            {
                slotNo = 1, outcome = "COMPLETED", finalPhysicalState = "EMPTY", lockState = "LOCKED",
                unlockOutputState = "RESET", reasonCodes = Array.Empty<string>()
            },
            new
            {
                slotNo = 2, outcome = "FAILED", finalPhysicalState = "EMPTY", lockState = "UNKNOWN",
                unlockOutputState = "RESET", reasonCodes = DoorUnprovenReasonCodes
            }
        }, SerializerOptions);
        return node.ToJsonString();
    }

    private static string ReleaseSessionRequest(
        string proof,
        int[] slots,
        string messageId = "e3850000-0000-4000-8000-000000000001",
        string requestId = ReleaseRequestId) => Envelope(
        messageId,
        "ExceptionRecoverySessionRequested",
        new
        {
            requestId,
            administrator = Operator(),
            administratorRole = "MAINTENANCE_ADMINISTRATOR",
            eventId = ReleaseEventId,
            demandId = (string?)null,
            slots,
            reason = "Repair the doors the compensation could not prove locked.",
            authenticationProof = proof
        });

    private static string ReleaseAction(
        string sessionId,
        int[] slots,
        string actionId = ReleaseActionId,
        string messageId = "e3850000-0000-4000-8000-000000000002",
        string action = "HARDWARE_REPAIR_RELEASE") => Envelope(
        messageId,
        "RecoveryActionSubmitted",
        new
        {
            recoveryActionId = actionId,
            exceptionRecoverySessionId = sessionId,
            action,
            eventId = ReleaseEventId,
            demandId = (string?)null,
            slots,
            @operator = Operator(),
            reason = "Lock replaced."
        });

    private static string ReleaseRecord(string messageId, string sessionId, int[] slots, string actionId = ReleaseActionId) =>
        Envelope(
            messageId,
            "HardwareRecoveryRecordSubmitted",
            new
            {
                recordId = "d" + messageId[1..],
                exceptionRecoverySessionId = sessionId,
                recoveryActionId = actionId,
                @operator = Operator(),
                administratorRole = "MAINTENANCE_ADMINISTRATOR",
                slots,
                checksPerformed = HardwareChecks,
                actionsPerformed = HardwareActions,
                observations = HardwareObservations,
                observedAt = Now.AddMinutes(30)
            });

    /// <summary>
    /// A mid-session <c>SafetyStateSnapshot</c>: all eight slots EMPTY, LOCKED and RESET, except that
    /// <paramref name="unprovenSlot"/>'s lock reads UNKNOWN.
    /// </summary>
    private static string SlotReadings(string messageId, long safetyStateVersion, int unprovenSlot = 0) => Envelope(
        messageId,
        "SafetyStateSnapshot",
        new
        {
            safetyStateVersion,
            observedAt = Now,
            safety = new
            {
                departureSafe = true,
                vehicleStopped = true,
                allTargetSlotsLocked = true,
                allUnlockOutputsReset = true,
                unknownPresent = unprovenSlot != 0,
                reasonCodes = Array.Empty<string>()
            },
            slotStates = Enumerable.Range(1, 8).Select(slot => new
            {
                slotNo = slot,
                operability = "OPERABLE",
                administrativeAvailability = "ENABLED",
                physicalState = "EMPTY",
                lockState = slot == unprovenSlot ? "UNKNOWN" : "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = Array.Empty<string>()
            }).ToArray()
        });

    /// <summary>The vehicle's answer to <paramref name="check"/>, correlated to it as the protocol requires.</summary>
    private static string HoldReleaseCheckResult(
        string messageId,
        string check,
        string outcome = "SAFE",
        string checkPurpose = "HOLD_RELEASE")
    {
        using JsonDocument document = JsonDocument.Parse(check);
        JsonElement root = document.RootElement;
        JsonElement payload = root.GetProperty("payload");
        return Envelope(
            messageId,
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = payload.GetProperty("preDepartureSafetyCheckId").GetString(),
                checkPurpose,
                outcome,
                observedAt = Now,
                safetyStateVersion = payload.GetProperty("expectedSafetyStateVersion").GetInt64(),
                validUntil = Now.AddMinutes(1),
                safety = new
                {
                    departureSafe = outcome == "SAFE",
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = outcome == "UNKNOWN",
                    reasonCodes = Array.Empty<string>()
                }
            },
            root.GetProperty("messageId").GetString());
    }

    private static JsonElement PayloadOf(string wire)
    {
        using JsonDocument document = JsonDocument.Parse(wire);
        return document.RootElement.GetProperty("payload").Clone();
    }

    /// <summary>The one session snapshot in <paramref name="wire"/> for <paramref name="sessionId"/>: the vehicle may be sent
    /// unapplied snapshots of an earlier session in the same exchange.</summary>
    private static JsonElement SessionSnapshotIn(string[] wire, string sessionId) => Assert.Single(
        wire.Where(line => MessageType(line) == "ExceptionRecoverySessionSnapshot").Select(PayloadOf),
        payload => payload.GetProperty("exceptionRecoverySessionId").GetString() == sessionId);

    private static string? ClosedReasonOf(string snapshot) => PayloadOf(snapshot).GetProperty("closedReason").GetString();
}
