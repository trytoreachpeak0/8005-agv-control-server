using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Recovery;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// control-server#852 (hmi#399 option C): a repair release's <c>HOLD_RELEASE</c> check the vehicle refuses as
/// <c>PREDEPARTURE_CHECK_EXPIRED</c> -- its accepted safety version moved on, as a roam's handshake always moves it since
/// onboard-hmi#394 -- is not spent but asked again: the release goes back to waiting for readings, the next mid-session
/// snapshot is judged as the first one was, and a check under a new messageId and a new check id asks about that snapshot's
/// version. The grant (control-server#797) must still cover the release, and it is asked again at most
/// <see cref="OnboardRecoveryCoordinator.ReleaseCheckReaskLimit"/> times.
/// </summary>
public sealed partial class RecoveryStateMachineG2Tests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task AnExpiredReleaseCheckIsAskedAgainOnFreshReadingsUnderNewIdsAndItsSafeAnswerLiftsTheHold()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_REASK";
        const string proof = "repair-release-reask-proof-not-a-production-secret";
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
            string first = await ReachReleaseCheckAsync(processor, peer, state, proof, "e8520000-0000-4000-8000-0000000001");

            string[] refused = await ExchangeAsync(processor, peer, state, CheckRefused("e8520000-0000-4000-8000-000000000110", first));

            // Not spent: back to waiting for readings, the refused check settled so it is never replayed, readings asked for.
            RecoveryWorkflowRow waiting = await ReleaseAsync(context);
            Assert.Equal(
                (RecoveryWorkflowState.AwaitingResult, (string?)null, (string?)null),
                (waiting.State, waiting.Outcome, waiting.CommandMessageId));
            Assert.NotNull((await context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(row => row.MessageId == MessageIdOf(first), token)).AcknowledgedAt);
            string request = Assert.Single(refused, line => MessageType(line) == "SafetyStateSnapshotRequested");
            Assert.Equal("PRE_MOVEMENT_RECONCILIATION", PayloadOf(request).GetProperty("reason").GetString());
            Assert.Null((await context.SlotDoorHolds.AsNoTracking().SingleAsync(token)).ReleasedAt);

            // The refused check's SAFE answer, arriving late, is no answer to anything now: the old basis lifts nothing.
            await ExchangeAsync(processor, peer, state, HoldReleaseCheckResult("e8520000-0000-4000-8000-000000000111", first));
            Assert.Null((await context.SlotDoorHolds.AsNoTracking().SingleAsync(token)).ReleasedAt);
            Assert.Null((await ReleaseAsync(context)).CommandMessageId);

            string[] readings = await ExchangeAsync(processor, peer, state, SlotReadings("e8520000-0000-4000-8000-000000000112", 9));
            string again = Assert.Single(readings, line => MessageType(line) == "PreDepartureSafetyCheck");
            Assert.NotEqual(MessageIdOf(first), MessageIdOf(again));
            Assert.NotEqual(CheckIdOf(first), CheckIdOf(again));
            Assert.Equal("HOLD_RELEASE", PayloadOf(again).GetProperty("checkPurpose").GetString());
            Assert.Equal(9, PayloadOf(again).GetProperty("expectedSafetyStateVersion").GetInt64());
            Assert.Equal(WireToGateStore.SlotDoorRepairReleaseRequired, (await store.DecideReadinessAsync(AgvId, 3, token)).ReasonCode);

            // Nor once the new check is out: an answer to the refused one, by either correlation, is not an answer to it.
            await ExchangeAsync(processor, peer, state, HoldReleaseCheckResult("e8520000-0000-4000-8000-000000000114", first));
            await ExchangeAsync(processor, peer, state,
                HoldReleaseCheckResult("e8520000-0000-4000-8000-000000000115", first, byCommandMessageId: true));
            Assert.Null((await context.SlotDoorHolds.AsNoTracking().SingleAsync(token)).ReleasedAt);
            Assert.Equal(MessageIdOf(again), (await ReleaseAsync(context)).CommandMessageId);

            string[] answered = await ExchangeAsync(
                processor, peer, state, HoldReleaseCheckResult("e8520000-0000-4000-8000-000000000113", again));

            Assert.Equal("READY", PayloadOf(Assert.Single(answered, line => MessageType(line) == "VehicleBusinessStateSnapshot"))
                .GetProperty("readiness").GetString());
            RecoveryWorkflowRow lifted = await ReleaseAsync(context);
            Assert.Equal((RecoveryWorkflowState.Reconciled, "SAFE"), (lifted.State, lifted.Outcome));
            Assert.Equal(ReleaseActionId, (await context.SlotDoorHolds.AsNoTracking().SingleAsync(token)).ReleasedByActionId);
            Assert.Equal(SessionReadiness.Ready, (await store.DecideReadinessAsync(AgvId, 3, token)).Readiness);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// Asked again, the release judges its readings afresh: a held slot no longer proven LOCKED, RESET and EMPTY in the next
    /// snapshot spends it as the first readings would have, and no check goes out on the old basis.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task ReadingsThatChangedBeforeTheReleaseCheckIsAskedAgainSpendTheRelease()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_REASK_CHANGED";
        const string proof = "repair-release-reask-changed-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                await HoldTheVehicleAsync(context, proofVariable, proof);
            string first = await ReachReleaseCheckAsync(processor, peer, state, proof, "e8520000-0000-4000-8000-0000000002");
            await ExchangeAsync(processor, peer, state, CheckRefused("e8520000-0000-4000-8000-000000000210", first));

            string[] readings = await ExchangeAsync(
                processor, peer, state, SlotReadings("e8520000-0000-4000-8000-000000000211", 9, unprovenSlot: 2));

            Assert.DoesNotContain(readings, line => MessageType(line) == "PreDepartureSafetyCheck");
            RecoveryWorkflowRow spent = await ReleaseAsync(context);
            Assert.Equal(
                (RecoveryWorkflowState.RecoveryRequired, OnboardRecoveryCoordinator.ReleaseReadingsUnprovenOutcome),
                (spent.State, spent.Outcome));
            Assert.Null((await context.SlotDoorHolds.AsNoTracking().SingleAsync(token)).ReleasedAt);
            Assert.Equal(1, await context.ProtocolOutbox.AsNoTracking()
                .CountAsync(row => row.MessageType == "PreDepartureSafetyCheck", token));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A vehicle that keeps refusing is asked again <see cref="OnboardRecoveryCoordinator.ReleaseCheckReaskLimit"/> times, each
    /// check under its own messageId and check id; the refusal after the last one spends the release under
    /// <see cref="OnboardRecoveryCoordinator.ReleaseCheckReaskLimitOutcome"/> and asks for no more readings.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task AReleaseCheckIsAskedAgainAtMostTheLimitAndThenSpent()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_REASK_LIMIT";
        const string proof = "repair-release-reask-limit-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                await HoldTheVehicleAsync(context, proofVariable, proof);
            string check = await ReachReleaseCheckAsync(processor, peer, state, proof, "e8520000-0000-4000-8000-0000000003");
            List<string> asked = [check];

            for (int reask = 1; reask <= OnboardRecoveryCoordinator.ReleaseCheckReaskLimit; reask++)
            {
                await ExchangeAsync(processor, peer, state, CheckRefused($"e8520000-0000-4000-8000-0000000003{reask}0", check));
                Assert.Equal(RecoveryWorkflowState.AwaitingResult, (await ReleaseAsync(context)).State);
                check = Assert.Single(
                    await ExchangeAsync(processor, peer, state, SlotReadings($"e8520000-0000-4000-8000-0000000003{reask}1", 8 + reask)),
                    line => MessageType(line) == "PreDepartureSafetyCheck");
                asked.Add(check);
            }
            string[] last = await ExchangeAsync(processor, peer, state, CheckRefused("e8520000-0000-4000-8000-000000000399", check));

            Assert.Equal(OnboardRecoveryCoordinator.ReleaseCheckReaskLimit + 1, asked.Select(MessageIdOf).Distinct().Count());
            Assert.Equal(OnboardRecoveryCoordinator.ReleaseCheckReaskLimit + 1, asked.Select(CheckIdOf).Distinct().Count());
            RecoveryWorkflowRow spent = await ReleaseAsync(context);
            Assert.Equal(
                (RecoveryWorkflowState.RecoveryRequired, OnboardRecoveryCoordinator.ReleaseCheckReaskLimitOutcome),
                (spent.State, spent.Outcome));
            Assert.DoesNotContain(last, line => MessageType(line) == "SafetyStateSnapshotRequested");
            Assert.All(await context.ProtocolOutbox.AsNoTracking()
                    .Where(row => row.MessageType == "PreDepartureSafetyCheck").ToArrayAsync(token),
                row => Assert.NotNull(row.AcknowledgedAt));
            Assert.DoesNotContain(
                await ExchangeAsync(processor, peer, state, SlotReadings("e8520000-0000-4000-8000-0000000003a1", 20)),
                line => MessageType(line) == "PreDepartureSafetyCheck");
            Assert.Null((await context.SlotDoorHolds.AsNoTracking().SingleAsync(token)).ReleasedAt);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// With <c>RecoveryGrant:required</c> on, asking again is a new step of the release, so the grant must still cover it
    /// (control-server#797, ADR-cross-0066): ended before the refusal, before the next readings, or before the new check's
    /// SAFE answer, the release is spent under <see cref="OnboardRecoveryCoordinator.GrantEndedOutcome"/> and the hold stands.
    /// A grant still in effect lets the same steps lift it.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    [InlineData("in-effect")]
    [InlineData("expired-before-refusal")]
    [InlineData("revoked-before-readings")]
    [InlineData("expired-before-answer")]
    public async Task AReleaseCheckIsAskedAgainOnlyUnderAGrantStillInEffect(string when)
    {
        Environment.SetEnvironmentVariable(GrantProofVariable, GrantProof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, _, OnboardConnectionState state) = await HoldTheVehicleAsync(context, GrantProofVariable, GrantProof);
            MovableTimeProvider clock = new(Now);
            RecoveryGrantRegistry registry = new(clock);
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), new FixedTimeProvider(Now), GrantConfiguration(required: true), peer,
                recoveryGrants: registry, roster: GrantRoster());
            registry.Grant(GrantFor(AgvId, GrantVehicleKey, journeyId: null, clock.Current,
                [RecoveryGrantActions.HardwareRepairRelease], TimeSpan.FromMinutes(30)));
            string first = await ReachReleaseCheckAsync(processor, peer, state, GrantProof, "e8520000-0000-4000-8000-0000000004");

            if (when == "expired-before-refusal") clock.Current = clock.Current.AddMinutes(31);
            string[] refused = await ExchangeAsync(processor, peer, state, CheckRefused("e8520000-0000-4000-8000-000000000410", first));
            if (when == "revoked-before-readings") registry.Revoke(AgvId);
            string[] readings = await ExchangeAsync(processor, peer, state, SlotReadings("e8520000-0000-4000-8000-000000000411", 9));
            string? again = readings.SingleOrDefault(line => MessageType(line) == "PreDepartureSafetyCheck");
            if (when == "expired-before-answer") clock.Current = clock.Current.AddMinutes(31);
            if (again is not null)
                await ExchangeAsync(processor, peer, state, HoldReleaseCheckResult("e8520000-0000-4000-8000-000000000412", again));

            RecoveryWorkflowRow release = await ReleaseAsync(context);
            SlotDoorHoldRow hold = await context.SlotDoorHolds.AsNoTracking().SingleAsync(token);
            if (when == "in-effect")
            {
                Assert.Equal((RecoveryWorkflowState.Reconciled, "SAFE"), (release.State, release.Outcome));
                Assert.NotNull(hold.ReleasedAt);
                return;
            }
            Assert.Equal(
                (RecoveryWorkflowState.RecoveryRequired, OnboardRecoveryCoordinator.GrantEndedOutcome),
                (release.State, release.Outcome));
            Assert.Null(hold.ReleasedAt);
            Assert.Equal(when == "expired-before-answer", again is not null);
            if (when == "expired-before-refusal")
                Assert.DoesNotContain(refused, line => MessageType(line) == "SafetyStateSnapshotRequested");
        }
        finally
        {
            Environment.SetEnvironmentVariable(GrantProofVariable, null);
        }
    }

    /// <summary>
    /// Session, release action, record and readings at version 8: returns the release's first HOLD_RELEASE check. Message ids
    /// are <paramref name="prefix"/> followed by two digits.
    /// </summary>
    private static async Task<string> ReachReleaseCheckAsync(
        OnboardMessageProcessor processor,
        RecordingPeer peer,
        OnboardConnectionState state,
        string proof,
        string prefix)
    {
        string sessionId = StableGuid(ReleaseRequestId, "exception-recovery-session");
        Assert.Equal("ExceptionRecoverySessionOpened", MessageType((await ExchangeAsync(processor, peer, state,
            ReleaseSessionRequest(proof, RecoverySlots, $"{prefix}01")))[0]));
        Assert.Equal("RecoveryActionAccepted", MessageType((await ExchangeAsync(processor, peer, state,
            ReleaseAction(sessionId, RecoverySlots, messageId: $"{prefix}02")))[0]));
        await ExchangeAsync(processor, peer, state, ReleaseRecord($"{prefix}03", sessionId, RecoverySlots));
        return Assert.Single(
            await ExchangeAsync(processor, peer, state, SlotReadings($"{prefix}04", 8)),
            line => MessageType(line) == "PreDepartureSafetyCheck");
    }

    /// <summary>The vehicle's <c>PREDEPARTURE_CHECK_EXPIRED</c> refusal of <paramref name="check"/>, with no result.</summary>
    private static string CheckRefused(string messageId, string check, string reasonCode = "PREDEPARTURE_CHECK_EXPIRED") => Envelope(
        messageId,
        "ProtocolProblem",
        new
        {
            rejectedMessageId = MessageIdOf(check),
            rejectedMessageType = "PreDepartureSafetyCheck",
            problem = new { reasonCode, fieldPath = (string?)null, displayMessage = (string?)null }
        });

    private static Task<RecoveryWorkflowRow> ReleaseAsync(ControlServerDbContext context) =>
        context.RecoveryWorkflows.AsNoTracking()
            .SingleAsync(row => row.WorkflowId == ReleaseActionId, TestContext.Current.CancellationToken);

    private static string CheckIdOf(string check) =>
        PayloadOf(check).GetProperty("preDepartureSafetyCheckId").GetString()!;
}
