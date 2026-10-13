using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
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
            string ended = await processor.ProcessAsync(result, state, token);
            Assert.Equal("DurableAck", MessageType(ended));

            Assert.Equal(StationOperationStatus.Cancelled, (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status);
            Assert.Equal(DemandExecutionStatus.Cancelled, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
            JourneyRuntimeRow runtime = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
            Assert.Equal(JourneyRuntimeStage.Completed, runtime.Stage);
            Assert.Equal(endingCode, runtime.BlockReasonCode);
            // The ended demand no longer holds the session. A forced recovery still holds it, for its hardware record
            // (REQ-0242) -- that hold, not the demand's, is what the session names.
            if (action == "FORCED_MECHANICAL_RECOVERY")
            {
                Assert.Equal("FORCED_RECOVERY_HARDWARE_RECOVERY_REQUIRED",
                    (await context.SessionRecoveries.AsNoTracking().SingleAsync(token)).ReasonCode);
            }
            else
            {
                await AssertReadyAgainAsync(context, ended);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The snapshot offers exactly what a submission accepts (C11's addition to #697): a resume is offered only where its
    /// preconditions hold. In this shape the vehicle reports no unsettled attempt and no proven checkpoint -- it aborted the
    /// load -- so the resume is not offered, and submitting it anyway is refused as before. The other half (review M1): a vehicle
    /// whose report names the attempt as unsettled at a proven checkpoint is offered the resume, and its submission is taken.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AResumeIsOfferedOnlyWhereItsSubmissionWouldBeAccepted(bool provenCheckpoint)
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
            if (provenCheckpoint)
            {
                // What the handshake's RecoveryStateReport writes for a vehicle holding the attempt at a proven checkpoint.
                SessionRecoveryRow report = await context.SessionRecoveries.SingleAsync(token);
                report.UnsettledSlotOperationAttemptId = AttemptId;
                report.ProvenRecoveryCheckpoint = "SAFE_FINISH_REACHED";
                await context.SaveChangesAsync(token);
            }
            await processor.ProcessAsync(RecoverySessionRequest(proof), state, token);

            string[] offered = (await LatestSessionSnapshotAsync(context, StableGuid(RequestId, "exception-recovery-session")))
                .GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()!).ToArray();
            string resume = await processor.ProcessAsync(RecoveryAction("RESUME_AFTER_REPAIR"), state, token);
            if (provenCheckpoint)
            {
                Assert.True(MessageType(resume) == "RecoveryActionAccepted", resume);
                Assert.Equal(
                    ["RESUME_AFTER_REPAIR", "COMPENSATE_LOAD_ALL_EMPTY", "FAULT_CARGO_HANDOFF", "FORCED_MECHANICAL_RECOVERY"],
                    offered);
            }
            else
            {
                Assert.Equal("RecoveryActionRejected", MessageType(resume));
                Assert.Equal(["COMPENSATE_LOAD_ALL_EMPTY", "FAULT_CARGO_HANDOFF", "FORCED_MECHANICAL_RECOVERY"], offered);
            }
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
    /// The shapes the guard on the load's own verdict leaves alone (review H1 of #697, C11's choice A): the load's own result
    /// is on file as well, in either order. The load keeps that verdict -- COMPLETED is Committed, a determinate failure
    /// Failed -- and the demand carries the mark. Mid-session, with no reconnect, the vehicle is RECOVERY_REQUIRED on the
    /// demand's mark (and no later answer pushes it back to READY); the session offers exactly the two ways out that do not
    /// need a load in recovery, the compensation is refused; and the handoff ends the demand and the journey, releases the
    /// vehicle's purpose and brings it back to READY on the handoff's own answer.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("completed-first")]
    [InlineData("cancellation-first-completed-late")]
    [InlineData("cancellation-first-determinate-failure-late")]
    public async Task AnUnreconciledCancellationOfALoadWithItsOwnResultHoldsTheVehicleAndLeavesItTwoWaysOut(string order)
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_CS697_ARRIVAL_ORDER";
        const string proof = "cs697-arrival-order-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            bool determinate = order == "cancellation-first-determinate-failure-late";
            // A determinate failure is settled only once the station deadline has passed (ADR-cross-0058 decision 5).
            (OnboardMessageProcessor processor, OnboardConnectionState state) = await AuthorizeInFlightCancellationAsync(
                context, proofVariable, determinate ? Now.AddMinutes(-10) : Now.AddMinutes(-1));
            string loadResult = Envelope(
                "b6970000-0000-4000-8000-000000000011",
                "OperationResult",
                determinate ? DeterminateLoadFailurePayload() : OperationResultPayload());
            List<string> answers = [];
            if (order == "completed-first")
            {
                answers.Add(await processor.ProcessAsync(loadResult, state, token));
                answers.Add(await processor.ProcessAsync(UnreconciledCancellationResult("FAILED"), state, token));
            }
            else
            {
                answers.Add(await processor.ProcessAsync(UnreconciledCancellationResult("UNKNOWN"), state, token));
                answers.Add(await processor.ProcessAsync(loadResult, state, token));
            }

            Assert.All(answers, answer => Assert.Equal("DurableAck", MessageType(answer)));
            string[] readinessLines = [.. answers
                .SelectMany(answer => answer.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                .Where(line => MessageType(line) == "SessionReadiness")];
            Assert.Equal(["RECOVERY_REQUIRED"],
                readinessLines.Select(line => FirstPayload(line).GetProperty("readiness").GetString()!).ToArray());
            SessionRecoveryRow session = await context.SessionRecoveries.AsNoTracking().SingleAsync(token);
            Assert.Equal((SessionReadiness.RecoveryRequired, "OPERATION_RECOVERY_REQUIRED"), (session.Readiness, session.ReasonCode));
            Assert.Equal(determinate ? StationOperationStatus.Failed : StationOperationStatus.Committed,
                (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status);
            Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
            JourneyRuntimeRow blocked = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
            Assert.Equal((JourneyRuntimeStage.Blocked, "LoadCancellationResult_NOT_RECONCILED"), (blocked.Stage, blocked.BlockReasonCode));

            Assert.Equal("ExceptionRecoverySessionOpened",
                MessageType(await processor.ProcessAsync(RecoverySessionRequest(proof), state, token)));
            Assert.Equal(["FAULT_CARGO_HANDOFF", "FORCED_MECHANICAL_RECOVERY"],
                (await LatestSessionSnapshotAsync(context, StableGuid(RequestId, "exception-recovery-session")))
                .GetProperty("allowedActions").EnumerateArray().Select(item => item.GetString()!).ToArray());
            string compensation = await processor.ProcessAsync(
                RecoveryAction(
                    "COMPENSATE_LOAD_ALL_EMPTY",
                    messageId: "e6970000-0000-4000-8000-000000000001",
                    actionId: "56970000-0000-4000-8000-000000000001"),
                state,
                token);
            Assert.Equal("RecoveryActionRejected", MessageType(compensation));
            Assert.Equal(ServerReasonCodes.ActionNotAllowedInState,
                FirstPayload(compensation).GetProperty("problem").GetProperty("reasonCode").GetString());
            string accepted = await processor.ProcessAsync(RecoveryAction("FAULT_CARGO_HANDOFF"), state, token);
            Assert.True(MessageType(accepted) == "RecoveryActionAccepted", accepted);
            string ended = await processor.ProcessAsync(HandedOffResult(), state, token);

            Assert.Equal("DurableAck", MessageType(ended));
            Assert.Equal(DemandExecutionStatus.Cancelled, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
            JourneyRuntimeRow runtime = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
            Assert.Equal((JourneyRuntimeStage.Completed, "TERMINATED_BY_FAULT_CARGO_HANDOFF"), (runtime.Stage, runtime.BlockReasonCode));
            Assert.NotNull((await context.Set<VehiclePurposeClaimRecordRow>().AsNoTracking().SingleAsync(token)).ReleasedAt);
            await AssertReadyAgainAsync(context, ended);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }
    /// <summary>
    /// The same kind of shape on another workflow (C11's choice A for #697): a correction of a committed load whose result
    /// does not reconcile leaves the demand RecoveryRequired and the load Committed. The session goes RECOVERY_REQUIRED on
    /// the result's answer, without a reconnect; before #697 it stayed READY and the onboard's entries stayed grey.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-LOAD-CORRECTION")]
    public async Task AnUnreconciledCorrectionOfACommittedLoadPutsTheSessionIntoRecoveryWithoutAReconnect()
    {
        const string correctionId = "b6970000-0000-4000-8000-000000000030";
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedCorrectableLoadAsync(context, JourneyRuntimeStage.AwaitingStationDeparture);
        OnboardMessageProcessor processor = Processor(context, new RecordingPeer(context), CancellationProofVariable);
        OnboardConnectionState state = CurrentState();
        state.Readiness = SessionReadiness.Ready;
        await processor.ProcessAsync(CorrectionRequest(correctionId), state, token);
        Assert.Equal("LOAD_CORRECTION", (await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).WorkflowType);

        string answer = await processor.ProcessAsync(
            Envelope(
                "b6970000-0000-4000-8000-000000000031",
                "LoadCorrectionResult",
                new
                {
                    correctionId,
                    demandId = DemandId,
                    slotOperationAttemptId = AttemptId,
                    overallOutcome = "FAILED",
                    slotResults = RecoverySlots.Select(slot => new
                    {
                        slotNo = slot,
                        outcome = "FAILED",
                        finalPhysicalState = "UNKNOWN",
                        lockState = "UNKNOWN",
                        unlockOutputState = "UNKNOWN",
                        reasonCodes = UnknownReasonCodes
                    }).ToArray(),
                    observedAt = Now.AddSeconds(4)
                }),
            state,
            token);

        string[] lines = answer.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["DurableAck", "SessionReadiness"], lines.Select(MessageType).ToArray());
        Assert.Equal("RECOVERY_REQUIRED", FirstPayload(lines[1]).GetProperty("readiness").GetString());
        Assert.Equal(StationOperationStatus.Committed, (await context.StationOperations.AsNoTracking().SingleAsync(token)).Status);
        Assert.Equal(DemandExecutionStatus.RecoveryRequired, (await context.AcceptedDemands.AsNoTracking().SingleAsync(token)).Status);
        JourneyRuntimeRow runtime = await context.JourneyRuntimes.AsNoTracking().SingleAsync(token);
        Assert.Equal(JourneyRuntimeStage.Blocked, runtime.Stage);
        Assert.Equal("LoadCorrectionResult_NOT_RECONCILED", runtime.BlockReasonCode);
        Assert.Equal("OPERATION_RECOVERY_REQUIRED", (await context.SessionRecoveries.AsNoTracking().SingleAsync(token)).ReasonCode);
    }

    /// <summary>
    /// The demand half of <see cref="WireToGateStore.OperationNeedsRecoveryAsync"/> (control-server#697) holds a vehicle only
    /// for a RecoveryRequired demand of its own Blocked journey. Seeded with the load Committed, so the operation half says no
    /// and each case is the demand half's alone: the held one, then one condition broken at a time.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [InlineData("held", true)]
    [InlineData("demand-not-in-recovery", false)]
    [InlineData("journey-not-blocked", false)]
    [InlineData("another-vehicle", false)]
    [InlineData("membership-removed", false)]
    public async Task ADemandAwaitingRecoveryHoldsOnlyItsOwnVehicleOnABlockedJourney(string variant, bool held)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(token);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        await SeedBlockedJourneyAsync(context, productionShapedSession: true);
        StationOperationRow load = await context.StationOperations.SingleAsync(token);
        load.Status = StationOperationStatus.Committed;
        load.CommittedAt = Now.AddMinutes(-1);
        JourneyRuntimeRow runtime = await context.JourneyRuntimes.SingleAsync(token);
        runtime.SetBlockReason("LoadCorrectionResult_NOT_RECONCILED", Now);
        switch (variant)
        {
            case "held":
                break;
            case "demand-not-in-recovery":
                (await context.AcceptedDemands.SingleAsync(token)).Status = DemandExecutionStatus.Accepted;
                break;
            case "journey-not-blocked":
                runtime.Stage = JourneyRuntimeStage.AwaitingStationDeparture;
                runtime.SetBlockReason(null, Now);
                break;
            case "another-vehicle":
                runtime.AgvId = "AGV-8005-02";
                break;
            case "membership-removed":
                (await context.Set<JourneyDemandRow>().SingleAsync(token)).RemovedAt = Now;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, null);
        }
        await context.SaveChangesAsync(token);
        WireToGateStore store = new(context);

        Assert.Equal(held, await store.OperationNeedsRecoveryAsync(AgvId, token));
        SessionReadinessDecision decision = await store.DecideReadinessAsync(AgvId, 3, token);
        Assert.Equal(held ? SessionReadiness.RecoveryRequired : SessionReadiness.Ready, decision.Readiness);
        Assert.Equal(held ? "OPERATION_RECOVERY_REQUIRED" : "READY", decision.ReasonCode);
    }

    /// <summary>The session READY again, and the given answer carrying that change to the vehicle.</summary>
    private static async Task AssertReadyAgainAsync(ControlServerDbContext context, string answer)
    {
        SessionRecoveryRow session = await context.SessionRecoveries.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SessionReadiness.Ready, session.Readiness);
        Assert.Equal("READY", session.ReasonCode);
        string readiness = Assert.Single(
            answer.Split('\n', StringSplitOptions.RemoveEmptyEntries), line => MessageType(line) == "SessionReadiness");
        Assert.Equal("READY", FirstPayload(readiness).GetProperty("readiness").GetString());
    }

    /// <summary>
    /// A load commanded and awaiting its result on a ready session, as production leaves it (<see cref="SeedLoadAwaitingResultAsync"/>),
    /// and the operator's in-flight cancellation of it authorized.
    /// </summary>
    private static async Task<(OnboardMessageProcessor Processor, OnboardConnectionState State)>
        AuthorizeInFlightCancellationAsync(
            ControlServerDbContext context,
            string proofVariable,
            DateTimeOffset? waitStartedAt = null)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await SeedLoadAwaitingResultAsync(context, waitStartedAt ?? Now.AddMinutes(-1));
        // A five-minute station wait, as DeadlineAwareProcessor has it, so a determinate failure can be judged against it.
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            context,
            new WireToGateStore(context),
            new FixedTimeProvider(Now),
            Configuration(proofVariable),
            new RecordingPeer(context),
            new JourneyRuntimeOptions { StationDepartureWaitTimeout = TimeSpan.FromMinutes(5) });
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
