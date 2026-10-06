using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Recovery;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// control-server#484: the governed exit of control-server#483, extended to the other three actions whose outcome can be
/// awaited for good -- a fault cargo handoff, a load compensation (authorized, or still awaiting its authorization), and a
/// forced mechanical recovery -- and the one way out of the forced generation fence that closing a forced recovery leaves.
/// </summary>
public sealed partial class RecoveryStateMachineG2Tests
{
    private const string Handoff = "fault cargo handoff";
    private const string CompensationAuthorized = "compensation authorized";
    private const string CompensationAwaitingAuthorization = "compensation awaiting its authorization";
    private const string Forced = "forced mechanical recovery";

    public static TheoryData<string> StuckOtherActions => new()
    {
        Handoff,
        CompensationAuthorized,
        CompensationAwaitingAuthorization,
        Forced,
    };

    /// <summary>
    /// Each of the three actions, its outcome awaited and the vehicle gone, is closed the way #483 closes a resume: the
    /// workflow is judged RecoveryRequired under ADMINISTRATOR_CLOSED, its command (when one went out) is settled so it is not
    /// replayed, the session is CLOSED at a new revision with its CLOSED snapshot queued, and the business is left exactly where
    /// it was for the next session -- which can now be opened. A compensation still awaiting its authorization has its
    /// session in ACTION_SELECTED and no command yet; it is closed all the same, and the authorization asked afterwards is
    /// refused (RECOVERY_SESSION_NOT_OPEN) and sends nothing. Until #484 every one of them was ACTION_NOT_CLOSABLE or
    /// SESSION_NOT_EXECUTING, and only a database edit freed the vehicle.
    /// </summary>
    [Theory]
    [MemberData(nameof(StuckOtherActions))]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AnActionWhoseOutcomeWillNotComeIsClosedByAnAdministratorWhenTheVehicleIsGone(string shape)
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            (OnboardMessageProcessor processor, OnboardConnectionState state, _) = await StuckOtherActionAsync(context, peer, shape);
            string sessionId = StableGuid(RequestId, "exception-recovery-session");
            ExceptionRecoverySessionRow stuck = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            RecoveryWorkflowRow waiting = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
            Assert.Equal(shape == CompensationAwaitingAuthorization ? "ACTION_SELECTED" : "EXECUTING", stuck.State);
            BusinessPicture before = await BusinessPictureAsync(context);
            long generation = (await context.VehicleRecoveryGenerations.AsNoTracking().SingleAsync(token)).ForcedRecoveryGeneration;

            RecoverySessionAdministratorCloseResult closed = await CloseAsync(context, peer, CloseRequest(sessionId: null));

            Assert.True(closed.Closed, string.Join(',', closed.Codes));
            Assert.Equal(sessionId, closed.ExceptionRecoverySessionId);
            ExceptionRecoverySessionRow session = await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token);
            Assert.Equal(("CLOSED", stuck.Revision + 1), (session.State, session.Revision));
            RecoveryWorkflowRow judged = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
            Assert.Equal((RecoveryWorkflowState.RecoveryRequired, RecoveryWorkflowOutcomes.AdministratorClosed, (string?)null),
                (judged.State, judged.Outcome, judged.ResultMessageId));
            if (waiting.CommandMessageId is null)
            {
                Assert.Equal(CompensationAwaitingAuthorization, shape);
            }
            else
            {
                Assert.NotNull((await context.ProtocolOutbox.AsNoTracking()
                    .SingleAsync(row => row.MessageId == waiting.CommandMessageId, token)).AcknowledgedAt);
            }
            JsonElement snapshot = await LatestSessionSnapshotAsync(context, sessionId);
            Assert.Equal("CLOSED", snapshot.GetProperty("state").GetString());
            Assert.Empty(snapshot.GetProperty("allowedActions").EnumerateArray());
            Assert.Equal(
                before with { Sessions = $"{sessionId}:CLOSED:{session.Revision}:{stuck.SelectedAction}", Snapshots = before.Snapshots + 1 },
                await BusinessPictureAsync(context));
            // The fence is not touched by a closing: a forced recovery's generation stays where its submission put it.
            Assert.Equal(generation,
                (await context.VehicleRecoveryGenerations.AsNoTracking().SingleAsync(token)).ForcedRecoveryGeneration);
            Assert.Equal(GovernanceActionOutcome.Succeeded, Assert.Single(await CloseAuditsAsync(context)).Outcome);

            if (shape == CompensationAwaitingAuthorization)
            {
                string late = await processor.ProcessAsync(CompensationAuthorizationRequest(), state, token);
                await processor.FlushDeferredOutboundAsync(state, token);
                Assert.Equal(("LoadCompensationRejected", ServerReasonCodes.RecoverySessionNotOpen),
                    (MessageType(late), FirstPayload(late).GetProperty("problem").GetProperty("reasonCode").GetString()));
                Assert.Null((await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token)).CommandMessageId);
                Assert.Equal(0, await context.ProtocolOutbox.CountAsync(row => row.MessageType == "LoadCompensationCommand", token));
            }

            string opened = await processor.ProcessAsync(PressedAgain("e0000000-0000-4000-8000-000000004841",
                "41000000-0000-4000-8000-000000004841"), state, token);
            await processor.FlushDeferredOutboundAsync(state, token);
            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(opened));
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    public static TheoryData<string> LateOtherResults => new() { Handoff, CompensationAuthorized, Forced };

    /// <summary>
    /// Unlike a resume's replacement result (#483), a recovery result arriving after an administrator closed its session is
    /// acknowledged and kept as evidence, and settles nothing (control-server#175's branch for a closed session): the demand,
    /// journey, operation and lease stay as the closing left them. The result's own outcome replaces ADMINISTRATOR_CLOSED on
    /// the workflow; the audit record still says who closed it and why.
    /// </summary>
    [Theory]
    [MemberData(nameof(LateOtherResults))]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ARecoveryResultArrivingAfterAnAdministratorClosedItsSessionIsKeptAndSettlesNothing(string shape)
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            (OnboardMessageProcessor processor, OnboardConnectionState state, string? late) =
                await StuckOtherActionAsync(context, peer, shape);
            Assert.True((await CloseAsync(context, peer, CloseRequest(sessionId: null))).Closed);
            BusinessPicture closed = await BusinessPictureAsync(context);

            string answer = await processor.ProcessAsync(late!, state, token);
            await processor.FlushDeferredOutboundAsync(state, token);

            Assert.Equal("DurableAck", MessageType(answer));
            context.ChangeTracker.Clear();
            Assert.Equal(closed, await BusinessPictureAsync(context));
            RecoveryWorkflowRow workflow = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
            Assert.Equal(RecoveryWorkflowState.RecoveryRequired, workflow.State);
            Assert.NotEqual(RecoveryWorkflowOutcomes.AdministratorClosed, workflow.Outcome);
            Assert.Equal(MessageIdOf(late!), workflow.ResultMessageId);
            Assert.Single(await context.RecoveryResultEvidence.AsNoTracking().ToArrayAsync(token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    public static TheoryData<string, string, bool> OtherActionFactsOnAConnectedVehicle => new()
    {
        { Handoff, "unsettled attempt", false },
        { Handoff, "pending attempt", false },
        { Handoff, "pending result", false },
        { Handoff, "active unlock", false },
        { Handoff, "nothing", true },
        { CompensationAuthorized, "unsettled attempt", false },
        { CompensationAuthorized, "active unlock", false },
        { CompensationAwaitingAuthorization, "unsettled attempt", false },
        { CompensationAwaitingAuthorization, "active unlock", false },
        { Forced, "pending result", false },
        { Forced, "active unlock", false },
        // A forced recovery settles no attempt of its own; the operation's attempt left unsettled by the failed load says
        // nothing about whether its result is on the way.
        { Forced, "unsettled attempt", true },
        { Forced, "nothing", true },
    };

    /// <summary>
    /// A connected vehicle whose latest report says the outcome may still be on its way is not closed: for a handoff or a
    /// compensation, the report names the session's attempt or any pending result; for a forced recovery, any pending result;
    /// for all three, slots whose unlock output is active -- the vehicle is at the doors. A connected vehicle whose report says
    /// none of that -- the onboard replaced or its journal cleared -- is closed.
    /// </summary>
    [Theory]
    [MemberData(nameof(OtherActionFactsOnAConnectedVehicle))]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AConnectedVehicleWhoseReportSaysTheOutcomeMayBeOnItsWayHoldsTheClosingBack(
        string shape, string reported, bool closes)
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            await StuckOtherActionAsync(context, peer, shape);
            await SetReportedFactsAsync(
                context,
                attempts: reported == "pending attempt" ? [AttemptId] : [],
                unsettled: reported == "unsettled attempt" ? AttemptId : null,
                results: reported == "pending result" ? ["e0000000-0000-4000-8000-000000004849"] : [],
                activeUnlockSlots: reported == "active unlock" ? RecoverySlots : []);
            string workflows = await WorkflowAccountAsync(context);

            RecoverySessionAdministratorCloseResult result = await CloseAsync(
                context, peer, CloseRequest(sessionId: null), new FixedPresence(SeededSessionGeneration, handshaking: false));

            if (closes)
            {
                Assert.True(result.Closed, string.Join(',', result.Codes));
                Assert.Equal("CLOSED", (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).State);
            }
            else
            {
                Assert.Equal([RecoverySessionAdministratorCloseCodes.ResultInFlightOnVehicle], result.Codes);
                Assert.NotEqual("CLOSED", (await context.ExceptionRecoverySessions.AsNoTracking().SingleAsync(token)).State);
                Assert.Equal(workflows, await WorkflowAccountAsync(context));
            }
            using JsonDocument detail = JsonDocument.Parse(Assert.Single(await CloseAuditsAsync(context)).DetailJson);
            int[] unlockOnFile = reported == "active unlock" ? RecoverySlots : [];
            Assert.Equal(unlockOnFile,
                detail.RootElement.GetProperty("read").GetProperty("vehicle").GetProperty("activeUnlockSlots")
                    .EnumerateArray().Select(item => item.GetInt32()).ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    /// <summary>
    /// Situation A of #484: a forced recovery was closed by an administrator while the vehicle was gone, and the vehicle comes
    /// back without ever having bound its command -- it reports the generation below the one the submission advanced to. The
    /// onboard raises its generation only by binding a ForcedMechanicalRecoveryCommand, and the closing settled that command,
    /// so nothing can bring the two together but another forced recovery. Before #484 every action was refused
    /// FORCED_RECOVERY_GENERATION_STALE and the hardware record was refused for want of a result: a hold only a database edit
    /// freed. Now a new forced recovery is accepted over the closed generation alone; it advances the fence, makes the closed
    /// workflow history, and goes the ordinary way -- result, the vehicle's report of the new generation, hardware record --
    /// until the vehicle is Ready.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task AForcedRecoveryClosedByAnAdministratorLeavesAnExitWhenTheVehicleReturnsBehindItsGeneration()
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            (OnboardMessageProcessor processor, OnboardConnectionState state, _) = await StuckOtherActionAsync(context, peer, Forced);
            WireToGateStore store = new(context);
            Assert.True((await CloseAsync(context, peer, CloseRequest(sessionId: null))).Closed);

            await store.ApplyRecoveryReportAsync(
                AgvId, 3, "f0000000-0000-4000-8000-000000004842", forcedRecoveryGeneration: 0, null, "NONE", [], [], [], token);
            Assert.Equal("FORCED_RECOVERY_GENERATION_MISMATCH", (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);
            context.ChangeTracker.Clear();

            const string nextRequestId = "41000000-0000-4000-8000-000000004842";
            const string nextActionId = "51000000-0000-4000-8000-000000004842";
            string nextSessionId = StableGuid(nextRequestId, "exception-recovery-session");
            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(await processor.ProcessAsync(
                PressedAgain("e0000000-0000-4000-8000-000000004842", nextRequestId), state, token)));
            string accepted = await processor.ProcessAsync(
                InSessionAction(Forced, "e0000000-0000-4000-8000-000000004843", nextActionId, nextSessionId), state, token);
            Assert.Equal("RecoveryActionAccepted", MessageType(accepted));
            context.ChangeTracker.Clear();
            Assert.Equal(2, (await context.VehicleRecoveryGenerations.AsNoTracking().SingleAsync(token)).ForcedRecoveryGeneration);
            Assert.Equal(RecoveryWorkflowState.HistoricalOnly,
                (await context.RecoveryWorkflows.AsNoTracking().SingleAsync(row => row.WorkflowId == ActionId, token)).State);
            RecoveryWorkflowRow next = await context.RecoveryWorkflows.AsNoTracking()
                .SingleAsync(row => row.WorkflowId == nextActionId, token);
            Assert.Equal((2L, RecoveryWorkflowState.AwaitingResult), (next.ForcedRecoveryGeneration, next.State));
            using (JsonDocument command = JsonDocument.Parse((await context.ProtocolOutbox.AsNoTracking()
                       .SingleAsync(row => row.MessageId == next.CommandMessageId, token)).PayloadJson))
            {
                Assert.Equal(2, command.RootElement.GetProperty("payload").GetProperty("forcedRecoveryGeneration").GetInt64());
            }

            JsonNode result = JsonNode.Parse(MechanicallyIsolatedResult(generation: 2, messageId: "80000000-0000-4000-8000-000000004842"))!;
            result["payload"]!["exceptionRecoverySessionId"] = nextSessionId;
            result["payload"]!["recoveryActionId"] = nextActionId;
            Assert.Equal("DurableAck", MessageType(await processor.ProcessAsync(result.ToJsonString(), state, token)));
            context.ChangeTracker.Clear();
            await store.ApplyRecoveryReportAsync(
                AgvId, 3, "f0000000-0000-4000-8000-000000004843", forcedRecoveryGeneration: 2, null, "NONE", [], [], [], token);
            Assert.Equal(ForcedHoldReason, (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);

            JsonNode record = JsonNode.Parse(HardwareRecoveryRecord("e1000000-0000-4000-8000-000000004842", slots: RecoverySlots))!;
            record["payload"]!["exceptionRecoverySessionId"] = nextSessionId;
            record["payload"]!["recoveryActionId"] = nextActionId;
            Assert.Equal("RECORDED", FirstPayload(await processor.ProcessAsync(record.ToJsonString(), state, token))
                .GetProperty("outcome").GetString());
            context.ChangeTracker.Clear();
            Assert.Equal(SessionReadiness.Ready, (await store.DecideReadinessAsync(AgvId, 3, token)).Readiness);
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    private const string ForcedHoldReason = WireToGateStore.ForcedRecoveryHardwareRecoveryRequired;

    public static TheoryData<string> ForcedFenceCases => new()
    {
        "vehicle reports the closed generation",
        "a higher generation ended normally",
        "no forced workflow above the reported generation",
        "another action over the closed generation",
    };

    /// <summary>
    /// The fence is lifted for one thing only: a new FORCED_MECHANICAL_RECOVERY, while the vehicle reports a generation below
    /// the server's and every forced workflow above what it reports was closed by an administrator -- whose command, settled
    /// by the closing, the vehicle provably never bound. Anything else stays as it was. A vehicle that reports the closed
    /// generation (it bound the command; situation B) is admitted as before, with no lifting needed. A forced recovery above
    /// the reported generation that ended normally -- the vehicle forgot a generation it had really reached, the onboard
    /// replaced or its journal cleared (control-server#493) -- is still FORCED_RECOVERY_GENERATION_STALE; so is a generation
    /// with no forced workflow behind it, and so is every other action over the closed generation.
    /// </summary>
    [Theory]
    [MemberData(nameof(ForcedFenceCases))]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-FORCED-MECHANICAL-RECOVERY")]
    public async Task TheForcedGenerationFenceIsLiftedOnlyForANewForcedRecoveryOverAdministratorClosedGenerations(string fence)
    {
        Environment.SetEnvironmentVariable(StuckProofVariable, StuckProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer peer = new(context);
            WireToGateStore store = new(context);
            OnboardMessageProcessor processor;
            OnboardConnectionState state;
            long reportedGeneration = 0;
            if (fence == "no forced workflow above the reported generation")
            {
                await SeedBlockedJourneyAsync(context, productionShapedSession: true);
                processor = Processor(context, peer, StuckProofVariable);
                state = CurrentState(deferOutbound: true);
                await store.AdvanceForcedRecoveryGenerationAsync(AgvId, 1, Now, token);
            }
            else
            {
                (processor, state, _) = await StuckOtherActionAsync(context, peer, Forced);
                Assert.True((await CloseAsync(context, peer, CloseRequest(sessionId: null))).Closed);
                if (fence == "vehicle reports the closed generation")
                {
                    reportedGeneration = 1;
                }
                else if (fence == "a higher generation ended normally")
                {
                    RecoveryWorkflowRow ended = await context.RecoveryWorkflows.SingleAsync(token);
                    ended.State = RecoveryWorkflowState.Reconciled;
                    ended.Outcome = "MECHANICALLY_ISOLATED";
                    ended.ResultMessageId = "80000000-0000-4000-8000-000000004844";
                    await context.SaveChangesAsync(token);
                }
            }
            await store.ApplyRecoveryReportAsync(
                AgvId, 3, "f0000000-0000-4000-8000-000000004844", reportedGeneration, null, "NONE", [], [], [], token);
            context.ChangeTracker.Clear();
            const string nextRequestId = "41000000-0000-4000-8000-000000004844";
            string nextSessionId = StableGuid(nextRequestId, "exception-recovery-session");
            string opened = await processor.ProcessAsync(
                PressedAgain("e0000000-0000-4000-8000-000000004844", nextRequestId), state, token);
            await processor.FlushDeferredOutboundAsync(state, token);
            Assert.Equal("ExceptionRecoverySessionOpened", MessageType(opened));
            string workflows = await WorkflowAccountAsync(context);

            string answer = await processor.ProcessAsync(
                InSessionAction(fence == "another action over the closed generation" ? Handoff : Forced,
                    "e0000000-0000-4000-8000-000000004845", "51000000-0000-4000-8000-000000004845", nextSessionId),
                state,
                token);
            await processor.FlushDeferredOutboundAsync(state, token);
            context.ChangeTracker.Clear();

            long generation = (await context.VehicleRecoveryGenerations.AsNoTracking().SingleAsync(token)).ForcedRecoveryGeneration;
            if (fence == "vehicle reports the closed generation")
            {
                Assert.Equal("RecoveryActionAccepted", MessageType(answer));
                Assert.Equal(2, generation);
            }
            else
            {
                Assert.Equal(("RecoveryActionRejected", ServerReasonCodes.ForcedRecoveryGenerationStale),
                    (MessageType(answer), FirstPayload(answer).GetProperty("problem").GetProperty("reasonCode").GetString()));
                Assert.Equal(1, generation);
                Assert.Equal(workflows, await WorkflowAccountAsync(context));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(StuckProofVariable, null);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The seeded load needs recovery and a session opened on it took <paramref name="shape"/>; its outcome is awaited. A
    /// vehicle with nothing pending (the production-shaped session), connected through the returned processor and state.
    /// Returns, unsent, a result of that action, when it has one.
    /// </summary>
    private static async Task<(OnboardMessageProcessor Processor, OnboardConnectionState State, string? Result)>
        StuckOtherActionAsync(ControlServerDbContext context, RecordingPeer peer, string shape)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await SeedBlockedJourneyAsync(context, productionShapedSession: true);
        OnboardMessageProcessor processor = Processor(context, peer, StuckProofVariable);
        OnboardConnectionState state = CurrentState(deferOutbound: true);
        string? result;
        switch (shape)
        {
            case Handoff:
                result = await ReachEndingResultAsync("FaultCargoRecoveryResult", processor, state, StuckProof);
                break;
            case CompensationAuthorized:
                result = await ReachEndingResultAsync("LoadCompensationResult", processor, state, StuckProof);
                break;
            case CompensationAwaitingAuthorization:
                await processor.ProcessAsync(RecoverySessionRequest(StuckProof), state, token);
                Assert.Equal("RecoveryActionAccepted", MessageType(
                    await processor.ProcessAsync(RecoveryAction("COMPENSATE_LOAD_ALL_EMPTY"), state, token)));
                result = null;
                break;
            case Forced:
                await processor.ProcessAsync(RecoverySessionRequest(StuckProof), state, token);
                Assert.Equal("RecoveryActionAccepted", MessageType(
                    await processor.ProcessAsync(RecoveryAction("FORCED_MECHANICAL_RECOVERY"), state, token)));
                result = MechanicallyIsolatedResult(generation: 1);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
        await processor.FlushDeferredOutboundAsync(state, token);
        context.ChangeTracker.Clear();
        RecoveryWorkflowRow workflow = await context.RecoveryWorkflows.AsNoTracking().SingleAsync(token);
        Assert.Equal(
            shape == CompensationAwaitingAuthorization
                ? RecoveryWorkflowState.AwaitingAuthorization
                : RecoveryWorkflowState.AwaitingResult,
            workflow.State);
        return (processor, state, result);
    }

    /// <summary>The seeded compensation's authorization request, as the vehicle asks it.</summary>
    private static string CompensationAuthorizationRequest() => Envelope(
        "90000000-0000-4000-8000-000000004841",
        "LoadCompensationRequested",
        new
        {
            recoveryActionId = ActionId,
            exceptionRecoverySessionId = StableGuid(RequestId, "exception-recovery-session"),
            demandId = DemandId,
            slotOperationAttemptId = AttemptId,
            @operator = Operator()
        });

    /// <summary>A new press of the recovery entry after the closing: a new request and a new message.</summary>
    private static string PressedAgain(string messageId, string requestId)
    {
        JsonNode press = JsonNode.Parse(NextSessionRequest(StuckProof))!;
        press["messageId"] = messageId;
        press["payload"]!["requestId"] = requestId;
        return press.ToJsonString();
    }

    /// <summary>An action submitted in the session <paramref name="sessionId"/>.</summary>
    private static string InSessionAction(string shape, string messageId, string actionId, string sessionId)
    {
        JsonNode action = JsonNode.Parse(RecoveryAction(
            shape == Forced ? "FORCED_MECHANICAL_RECOVERY" : "FAULT_CARGO_HANDOFF", messageId: messageId, actionId: actionId))!;
        action["payload"]!["exceptionRecoverySessionId"] = sessionId;
        return action.ToJsonString();
    }

    /// <summary>What the vehicle's latest RecoveryStateReport left on file, set by hand, active unlock slots included.</summary>
    private static async Task SetReportedFactsAsync(
        ControlServerDbContext context, string[] attempts, string? unsettled, string[] results, int[] activeUnlockSlots)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        SessionRecoveryRow row = await context.SessionRecoveries.SingleAsync(token);
        row.PendingAttemptIdsJson = JsonSerializer.Serialize(attempts);
        row.UnsettledSlotOperationAttemptId = unsettled;
        row.PendingResultIdsJson = JsonSerializer.Serialize(results);
        row.ActiveUnlockSlotsJson = JsonSerializer.Serialize(activeUnlockSlots);
        await context.SaveChangesAsync(token);
        context.ChangeTracker.Clear();
    }
}
