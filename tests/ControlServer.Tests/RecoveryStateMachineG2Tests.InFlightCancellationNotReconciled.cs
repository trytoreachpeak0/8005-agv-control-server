using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// control-server#697 (review H1 of onboard-hmi#346): an in-flight load cancellation whose result does not reconcile
/// (FAILED or UNKNOWN), while the load it cancelled has no OperationResult. The onboard aborts a cancelled load without
/// writing one (<c>WireToGateSlotOperationExecutor</c>), so this is the ordinary shape, not a race.
/// </summary>
/// <remarks>
/// Before #697 the demand went RecoveryRequired and the journey Blocked under <c>LoadCancellationResult_NOT_RECONCILED</c>,
/// but the load stayed <see cref="StationOperationStatus.Prepared"/>: the compensation was refused
/// <c>ACTION_NOT_ALLOWED_IN_STATE</c>, and the session stayed READY mid-session, so the onboard's recovery entries, all
/// gated on RECOVERY_REQUIRED, stayed grey until a reconnect. The load is now judged RecoveryRequired with the result, as a
/// non-COMPLETED OperationResult judges it (<c>WireToGateStore.ApplyOperationResultAsync</c>).
/// </remarks>
public sealed partial class RecoveryStateMachineG2Tests
{
    private const string UnreconciledCancellationId = "b6970000-0000-4000-8000-000000000001";
    private const string UnreconciledCancellationResultId = "b6970000-0000-4000-8000-000000000002";

    /// <summary>
    /// The result alone, with no reconnect: the load becomes RecoveryRequired and the session RECOVERY_REQUIRED on the
    /// result's own answer, announced as a readiness change. Both conclusions the vehicle may send.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("FAILED")]
    [InlineData("UNKNOWN")]
    public async Task AnUnreconciledInFlightCancellationPutsTheLoadAndTheSessionIntoRecoveryWithoutAReconnect(
        string overallOutcome)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        (OnboardMessageProcessor processor, OnboardConnectionState state) =
            await AuthorizeInFlightCancellationAsync(context, CancellationProofVariable);

        string answer = await processor.ProcessAsync(
            UnreconciledCancellationResult(overallOutcome), state, token);

        string[] lines = answer.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["DurableAck", "SessionReadiness"], lines.Select(MessageType).ToArray());
        Assert.Equal("RECOVERY_REQUIRED", FirstPayload(lines[1]).GetProperty("readiness").GetString());
        await AssertHeldForRecoveryAsync(context);
        Assert.Empty(await context.OperationResults.ToArrayAsync(token));
    }

    /// <summary>
    /// Step 1 of the ticket, each way out an administrator has in that shape: the session opens, the action is offered and
    /// accepted, and its proven-empty (or handed-off) result ends the demand, cancels the load and closes the journey.
    /// Before #697 the compensation was refused <c>ACTION_NOT_ALLOWED_IN_STATE</c>; the handoff and the forced recovery went
    /// through.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("COMPENSATE_LOAD_ALL_EMPTY", "CANCELLED_BY_LOAD_COMPENSATION")]
    [InlineData("FAULT_CARGO_HANDOFF", "TERMINATED_BY_FAULT_CARGO_HANDOFF")]
    [InlineData("FORCED_MECHANICAL_RECOVERY", "TERMINATED_BY_FAULT_CARGO_HANDOFF")]
    public async Task EachWayOutOfAnUnreconciledInFlightCancellationEndsTheDemandAndTheJourney(
        string action,
        string endingCode)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_CS697_WAYS_OUT";
        const string proof = "cs697-ways-out-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (OnboardMessageProcessor processor, OnboardConnectionState state) =
                await AuthorizeInFlightCancellationAsync(context, proofVariable);
            await processor.ProcessAsync(UnreconciledCancellationResult("FAILED"), state, token);

            Assert.Equal("ExceptionRecoverySessionOpened",
                MessageType(await processor.ProcessAsync(RecoverySessionRequest(proof), state, token)));
            string sessionId = StableGuid(RequestId, "exception-recovery-session");
            string?[] offered = [.. (await LatestSessionSnapshotAsync(context, sessionId))
                .GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString())];
            string accepted = await processor.ProcessAsync(RecoveryAction(action), state, token);
            // The whole answer on failure: a refusal names its reason code (before #697, ACTION_NOT_ALLOWED_IN_STATE).
            Assert.True(MessageType(accepted) == "RecoveryActionAccepted", accepted);
            Assert.Contains(action, offered);

            string result = action switch
            {
                "COMPENSATE_LOAD_ALL_EMPTY" => await AuthorizedAllEmptyCompensationAsync(processor, state),
                "FAULT_CARGO_HANDOFF" => HandedOffResult(),
                _ => MechanicallyIsolatedResult(generation: 1)
            };
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(result, state, token)));

            Assert.Equal(StationOperationStatus.Cancelled, (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status);
            Assert.Equal(DemandExecutionStatus.Cancelled, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
            JourneyRuntimeRow runtime = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
            Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
            Assert.Equal(endingCode, runtime.BlockReasonCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The snapshot offers exactly what a submission accepts (C11's addition to #697): a resume is offered only where its
    /// preconditions hold. In this shape the vehicle reports no unsettled attempt and no proven checkpoint -- it aborted the
    /// load -- so the resume is not offered, and submitting it anyway is refused as before.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AResumeIsOfferedOnlyWhereItsSubmissionWouldBeAccepted()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_CS697_RESUME_OFFER";
        const string proof = "cs697-resume-offer-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (OnboardMessageProcessor processor, OnboardConnectionState state) =
                await AuthorizeInFlightCancellationAsync(context, proofVariable);
            await processor.ProcessAsync(UnreconciledCancellationResult("FAILED"), state, token);
            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);

            string[] offered = (await LatestSessionSnapshotAsync(context, StableGuid(RequestId, "exception-recovery-session")))
                .GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()!).ToArray();
            string resume = await processor.ProcessAsync(RecoveryAction("RESUME_AFTER_REPAIR"), state, token);
            Assert.Equal("RecoveryActionRejected", MessageType(resume));
            Assert.Equal(["COMPENSATE_LOAD_ALL_EMPTY", "FAULT_CARGO_HANDOFF", "FORCED_MECHANICAL_RECOVERY"], offered);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The same result again: resent on the same connection it is answered from the first acceptance, and replayed into a
    /// new session generation after a reconnect it changes nothing -- the load stays RecoveryRequired, one piece of
    /// evidence, the session RECOVERY_REQUIRED once the handshake ends.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnUnreconciledInFlightCancellationResentOrReplayedAfterAReconnectHoldsTheSameWay()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        (OnboardMessageProcessor processor, OnboardConnectionState state) =
            await AuthorizeInFlightCancellationAsync(context, CancellationProofVariable);
        string result = UnreconciledCancellationResult("UNKNOWN");
        string first = await processor.ProcessAsync(result, state, token);

        string resent = await processor.ProcessAsync(result, state, token);
        Assert.Equal(first.Split('\n')[0], resent.Split('\n')[0]);
        await AssertHeldForRecoveryAsync(context);

        RecordingPeer peer = new(context);
        OnboardConnectionState reconnected = CurrentState(deferOutbound: true);
        List<string> handshake = [.. await ReconnectAsync(processor, peer, reconnected)];
        handshake.AddRange(await ExchangeAsync(
            processor, peer, reconnected, InSession(result, reconnected.SessionGeneration!.Value)));
        Assert.Equal("DurableAck", MessageType(handshake[^1]));
        await FinishHandshakeAsync(processor, peer, reconnected, handshake);

        await AssertHeldForRecoveryAsync(context);
        Assert.Single(await context.RecoveryResultEvidence.AsNoTracking().ToArrayAsync(token));
        Assert.Equal(SessionReadiness.RecoveryRequired, reconnected.Readiness);
    }

    /// <summary>
    /// Regression: the load's own UNKNOWN OperationResult arrived first (four runs in five of the review's probe). The load
    /// is RecoveryRequired by that result, which stays its live result; the cancellation's result moves the block code and
    /// changes nothing else, and the compensation goes through as it did before #697.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task WhenTheLoadReportedItsOwnUnknownResultFirstTheCancellationResultChangesNothingElse()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_CS697_RESULT_FIRST";
        const string proof = "cs697-result-first-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (OnboardMessageProcessor processor, OnboardConnectionState state) =
                await AuthorizeInFlightCancellationAsync(context, proofVariable);
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(
                Envelope(
                    "b6970000-0000-4000-8000-000000000010",
                    "OperationResult",
                    OperationResultPayload(completed: false, journalCheckpoint: "RESULT_UNKNOWN_RECORDED")),
                state,
                token)));
            OperationResultRow loadResult = await context.OperationResults.AsNoTracking().SingleAsync(token);

            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(
                UnreconciledCancellationResult("FAILED"), state, token)));

            await AssertHeldForRecoveryAsync(context);
            OperationResultRow stillLive = await context.OperationResults.AsNoTracking().SingleAsync(token);
            Assert.Equal(loadResult.ResultId, stillLive.ResultId);
            Assert.Null(stillLive.SupersededByResultId);

            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);
            Assert.Equal("RecoveryActionAccepted", MessageType(await processor.ProcessAsync(
                RecoveryAction("COMPENSATE_LOAD_ALL_EMPTY"), state, token)));
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(
                await AuthorizedAllEmptyCompensationAsync(processor, state), state, token)));
            Assert.Equal(JourneyRuntimeStage.Completed, (await context.JourneyRuntimes.AsNoTracking().SingleAsync(token)).Stage);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The guard on the load's own verdict: a load the vehicle reported COMPLETED before the cancellation's result arrived is
    /// what the vehicle said about it, and an unreconciled cancellation does not rewrite it -- only a load still Prepared is
    /// judged by the cancellation's result.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnUnreconciledInFlightCancellationLeavesALoadAlreadyCommittedAsTheVehicleReportedIt()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        (OnboardMessageProcessor processor, OnboardConnectionState state) =
            await AuthorizeInFlightCancellationAsync(context, CancellationProofVariable);
        Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(
            Envelope("b6970000-0000-4000-8000-000000000011", "OperationResult", OperationResultPayload()),
            state,
            token)));
        Assert.Equal(StationOperationStatus.Committed, (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status);

        Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(
            UnreconciledCancellationResult("FAILED"), state, token)));

        Assert.Equal(StationOperationStatus.Committed, (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status);
        Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
        Assert.Equal(JourneyRuntimeStage.Blocked, (await context.JourneyRuntimes.AsNoTracking().SingleAsync(token)).Stage);
    }

    /// <summary>
    /// A load commanded and awaiting its result on a ready session, as production leaves it (<see cref="SeedLoadAwaitingResultAsync"/>),
    /// and the operator's in-flight cancellation of it authorized.
    /// </summary>
    private static async Task<(OnboardMessageProcessor Processor, OnboardConnectionState State)>
        AuthorizeInFlightCancellationAsync(ControlServerDbContext context, string proofVariable)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await SeedLoadAwaitingResultAsync(context, Now.AddMinutes(-1));
        OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), proofVariable);
        OnboardConnectionState state = CurrentState();
        state.Readiness = SessionReadiness.Ready;
        string authorization = await processor.ProcessAsync(
            Altered(CancellationRequest(UnreconciledCancellationId), "b6970000-0000-4000-8000-000000000003"), state, token);
        Assert.Equal("AUTHORIZED", FirstPayload(authorization).GetProperty("decision").GetString());
        Assert.Equal(SessionReadiness.Ready, (await context.SessionRecoveries.AsNoTracking().SingleAsync(token)).Readiness);
        return (processor, state);
    }

    /// <summary>
    /// The cancellation's result as the onboard sends it when the clearing could not prove the slots: every slot's state
    /// unknown, concluded <paramref name="overallOutcome"/>.
    /// </summary>
    private static string UnreconciledCancellationResult(string overallOutcome) => Envelope(
        UnreconciledCancellationResultId,
        "LoadCancellationResult",
        new
        {
            cancellationId = UnreconciledCancellationId,
            demandId = DemandId,
            slotOperationAttemptId = AttemptId,
            overallOutcome,
            slotResults = RecoverySlots.Select(slot => new
            {
                slotNo = slot,
                outcome = overallOutcome,
                finalPhysicalState = "UNKNOWN",
                lockState = "UNKNOWN",
                unlockOutputState = "UNKNOWN",
                reasonCodes = UnknownReasonCodes
            }).ToArray(),
            observedAt = Now.AddSeconds(4)
        });

    private static async Task AssertHeldForRecoveryAsync(ControlServerDbContext context)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.Equal(StationOperationStatus.RecoveryRequired,
            (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status);
        Assert.Equal(DemandExecutionStatus.RecoveryRequired,
            (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
        JourneyRuntimeRow runtime = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
        Assert.Equal(JourneyRuntimeStage.Blocked, runtime.Stage);
        Assert.Equal("LoadCancellationResult_NOT_RECONCILED", runtime.BlockReasonCode);
        SessionRecoveryRow session = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
        Assert.Equal(SessionReadiness.RecoveryRequired, session.Readiness);
        Assert.Equal("OPERATION_RECOVERY_REQUIRED", session.ReasonCode);
        Assert.Null((await context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking().SingleAsync(token)).ReleasedAt);
    }

    /// <summary>The compensation selected on the open session, authorized, and its result proving both slots empty.</summary>
    private static async Task<string> AuthorizedAllEmptyCompensationAsync(
        OnboardMessageProcessor processor,
        OnboardConnectionState state)
    {
        await processor.ProcessAsync(
            Envelope(
                "90000000-0000-4000-8000-000000000697",
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
        JsonNode node = JsonNode.Parse(Envelope(
            "a0000000-0000-4000-8000-000000000697",
            "LoadCompensationResult",
            new
            {
                recoveryActionId = ActionId,
                demandId = DemandId,
                slotOperationAttemptId = AttemptId,
                overallOutcome = "ALL_EMPTY",
                slotResults = Array.Empty<object>(),
                observedAt = Now.AddSeconds(5)
            }))!;
        return AllEmptyCompensationResult(node.ToJsonString());
    }

    private static string HandedOffResult() => Envelope(
        "b6970000-0000-4000-8000-000000000020",
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
}
