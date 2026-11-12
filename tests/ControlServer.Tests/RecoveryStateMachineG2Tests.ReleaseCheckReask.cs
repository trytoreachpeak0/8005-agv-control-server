using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Recovery;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
            (RecordingPeer peer, _, OnboardConnectionState state) = await HoldTheVehicleAsync(context, proofVariable, proof);
            EventRecordingLogger<OnboardRecoveryCoordinator> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), new FixedTimeProvider(Now), Configuration(proofVariable), peer,
                recoveryLogger: log);
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
            // Each re-ask is logged as information, the spend at the limit once as a warning naming the release (review L2).
            Assert.Equal(OnboardRecoveryCoordinator.ReleaseCheckReaskLimit, log.Entries.Count(entry => entry.EventId.Id == 8520));
            (LogLevel Level, EventId EventId, string Message) limit = Assert.Single(log.Entries, entry => entry.EventId.Id == 8521);
            Assert.Equal(LogLevel.Warning, limit.Level);
            Assert.Contains(ReleaseActionId, limit.Message, StringComparison.Ordinal);
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
    /// The field shape behind hmi#399's probe cell <c>HOLD_RELEASE</c> (<c>ACheckAskedBeforeARoamIsAnsweredAfterItWhenNothingChanged</c>),
    /// on this server: the release's check goes out, the vehicle roams before answering, and the reconnect's handshake
    /// raises its accepted safety version (onboard-hmi#394). The check is replayed into the new session and the onboard
    /// refuses it as <c>PREDEPARTURE_CHECK_EXPIRED</c> (<c>WireToGateSessionClient.SendProtocolProblemAsync</c>, the
    /// check's messageId as <c>rejectedMessageId</c>). The server asks for readings, asks again about the new version, and
    /// the SAFE answer lifts the hold -- in the session after the roam, with no new release.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task AReleaseCheckAskedBeforeARoamAndRefusedAfterItIsAskedAgainAndLiftsTheHold()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_REASK_ROAM";
        const string proof = "repair-release-reask-roam-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                await HoldTheVehicleAsync(context, proofVariable, proof);
            string first = await ReachReleaseCheckAsync(processor, peer, state, proof, "e8520000-0000-4000-8000-0000000005");

            state = CurrentState(deferOutbound: true);
            List<string> handshake = [.. await ReconnectAsync(processor, peer, state)];
            // The handshake's snapshot goes out one version past the one the check asked about, as the real onboard's does
            // (onboard-hmi#394), which is why it refuses the replayed check (review L1).
            await FinishHandshakeAsync(processor, peer, state, handshake, safetyStateVersion: 9);
            long generation = state.SessionGeneration!.Value;
            Assert.Equal(9, state.SafetyRevision);
            Assert.Contains(handshake, line => line.Contains(MessageIdOf(first), StringComparison.Ordinal));

            string[] refused = await ExchangeAsync(processor, peer, state,
                InSession(CheckRefused("e8520000-0000-4000-8000-000000000510", first), generation));
            Assert.Single(refused, line => MessageType(line) == "SafetyStateSnapshotRequested");
            string[] readings = await ExchangeAsync(processor, peer, state,
                InSession(SlotReadings("e8520000-0000-4000-8000-000000000511", 10), generation));
            string again = Assert.Single(readings, line => MessageType(line) == "PreDepartureSafetyCheck");
            Assert.Equal(generation, JsonDocument.Parse(again).RootElement.GetProperty("sessionGeneration").GetInt64());
            Assert.Equal(10, PayloadOf(again).GetProperty("expectedSafetyStateVersion").GetInt64());
            Assert.NotEqual(MessageIdOf(first), MessageIdOf(again));

            string[] answered = await ExchangeAsync(processor, peer, state,
                InSession(HoldReleaseCheckResult("e8520000-0000-4000-8000-000000000512", again), generation));

            Assert.Equal("READY", PayloadOf(Assert.Single(answered, line => MessageType(line) == "VehicleBusinessStateSnapshot"))
                .GetProperty("readiness").GetString());
            Assert.Equal((RecoveryWorkflowState.Reconciled, "SAFE"),
                ((await ReleaseAsync(context)).State, (await ReleaseAsync(context)).Outcome));
            Assert.Equal(ReleaseActionId, (await context.SlotDoorHolds.AsNoTracking().SingleAsync(token)).ReleasedByActionId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The vehicle does not answer the readings request the re-ask sent -- the onboard leaves a snapshot request unanswered
    /// while it holds an unacknowledged <c>SafetyStateChanged</c> (review S1). The connection asks again at
    /// <see cref="OnboardMessageProcessor.SafetySnapshotReaskInterval"/> on the vehicle's next messages, says once in a
    /// warning when the wait passes <see cref="OnboardMessageProcessor.ReleaseReaskReadingsOverdueAfter"/>, and stops asking
    /// once a snapshot came; the release then goes on as usual.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task AReleaseWaitingForReaskReadingsAsksAgainUntilTheVehicleAnswers()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_REASK_UNANSWERED";
        const string proof = "repair-release-reask-unanswered-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, _, OnboardConnectionState state) = await HoldTheVehicleAsync(context, proofVariable, proof);
            MovableTimeProvider clock = new(Now);
            EventRecordingLogger<OnboardMessageProcessor> log = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), clock, Configuration(proofVariable), peer, logger: log);
            string first = await ReachReleaseCheckAsync(processor, peer, state, proof, "e8520000-0000-4000-8000-0000000006");
            int heartbeat = 0;
            async Task<string[]> HeartbeatAfterAsync(TimeSpan wait)
            {
                clock.Current += wait;
                heartbeat++;
                return await ExchangeAsync(processor, peer, state, Envelope(
                    $"e8520000-0000-4000-8000-0000000006{heartbeat:00}", "Heartbeat", new { sequence = heartbeat }));
            }
            static string? RequestReason(string[] wire) =>
                wire.SingleOrDefault(line => MessageType(line) == "SafetyStateSnapshotRequested") is { } request
                    ? PayloadOf(request).GetProperty("reason").GetString()
                    : null;
            int Overdue() => log.Entries.Count(entry => entry.EventId.Id == 1109);

            Assert.Equal("PRE_MOVEMENT_RECONCILIATION",
                RequestReason(await ExchangeAsync(processor, peer, state, CheckRefused("e8520000-0000-4000-8000-000000000690", first))));
            // Unanswered: not asked again within the interval, asked again after it.
            Assert.Null(RequestReason(await HeartbeatAfterAsync(TimeSpan.FromSeconds(1))));
            Assert.Equal("PRE_MOVEMENT_RECONCILIATION", RequestReason(await HeartbeatAfterAsync(TimeSpan.FromSeconds(5))));
            Assert.Equal(0, Overdue());
            // Still unanswered past the limit: one warning naming the release's vehicle, the asking goes on.
            Assert.Equal("PRE_MOVEMENT_RECONCILIATION",
                RequestReason(await HeartbeatAfterAsync(OnboardMessageProcessor.ReleaseReaskReadingsOverdueAfter)));
            (LogLevel Level, EventId EventId, string Message) overdue = Assert.Single(log.Entries, entry => entry.EventId.Id == 1109);
            Assert.Equal(LogLevel.Warning, overdue.Level);
            Assert.Contains(AgvId, overdue.Message, StringComparison.Ordinal);
            Assert.Equal("PRE_MOVEMENT_RECONCILIATION", RequestReason(await HeartbeatAfterAsync(TimeSpan.FromSeconds(6))));
            Assert.Equal(1, Overdue());

            // Answered: the release goes on, and the connection stops asking.
            string again = Assert.Single(
                await ExchangeAsync(processor, peer, state, SlotReadings("e8520000-0000-4000-8000-000000000691", 9)),
                line => MessageType(line) == "PreDepartureSafetyCheck");
            Assert.Null(RequestReason(await HeartbeatAfterAsync(TimeSpan.FromSeconds(10))));
            await ExchangeAsync(processor, peer, state, HoldReleaseCheckResult("e8520000-0000-4000-8000-000000000692", again));
            Assert.Equal((RecoveryWorkflowState.Reconciled, "SAFE"),
                ((await ReleaseAsync(context)).State, (await ReleaseAsync(context)).Outcome));
            Assert.Null(RequestReason(await HeartbeatAfterAsync(OnboardMessageProcessor.ReleaseReaskReadingsOverdueAfter)));
            Assert.Equal(1, Overdue());
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A refusal counts once (review probe P1): the same refusal line again is an inbox duplicate, a second refusal of the
    /// refused check under a new messageId is no release's any more, and a stale refusal of it after the new check is out
    /// leaves the new one bound and unsettled -- the new check's SAFE answer still lifts the hold.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task ARepeatedOrStaleRefusalOfAReleaseCheckIsNotCountedAgain()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_REASK_DUPLICATE";
        const string proof = "repair-release-reask-duplicate-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                await HoldTheVehicleAsync(context, proofVariable, proof);
            string first = await ReachReleaseCheckAsync(processor, peer, state, proof, "e8520000-0000-4000-8000-0000000007");

            string refusal = CheckRefused("e8520000-0000-4000-8000-000000000710", first);
            Assert.Single(await ExchangeAsync(processor, peer, state, refusal),
                line => MessageType(line) == "SafetyStateSnapshotRequested");
            Assert.DoesNotContain(await ExchangeAsync(processor, peer, state, refusal),
                line => MessageType(line) == "SafetyStateSnapshotRequested");
            Assert.DoesNotContain(
                await ExchangeAsync(processor, peer, state, CheckRefused("e8520000-0000-4000-8000-000000000711", first)),
                line => MessageType(line) == "SafetyStateSnapshotRequested");
            RecoveryWorkflowRow waiting = await ReleaseAsync(context);
            Assert.Equal((RecoveryWorkflowState.AwaitingResult, (string?)null), (waiting.State, waiting.CommandMessageId));

            string again = Assert.Single(
                await ExchangeAsync(processor, peer, state, SlotReadings("e8520000-0000-4000-8000-000000000712", 9)),
                line => MessageType(line) == "PreDepartureSafetyCheck");
            await ExchangeAsync(processor, peer, state, CheckRefused("e8520000-0000-4000-8000-000000000713", first));
            RecoveryWorkflowRow bound = await ReleaseAsync(context);
            Assert.Equal((RecoveryWorkflowState.AwaitingResult, MessageIdOf(again)), (bound.State, bound.CommandMessageId));
            Assert.Null((await context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(row => row.MessageId == MessageIdOf(again), token)).AcknowledgedAt);
            Assert.Equal(2, await context.ProtocolOutbox.AsNoTracking()
                .CountAsync(row => row.MessageType == "PreDepartureSafetyCheck", token));

            await ExchangeAsync(processor, peer, state, HoldReleaseCheckResult("e8520000-0000-4000-8000-000000000714", again));
            Assert.Equal((RecoveryWorkflowState.Reconciled, "SAFE"),
                ((await ReleaseAsync(context)).State, (await ReleaseAsync(context)).Outcome));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// The server restarts while the release waits for its re-ask readings, and the vehicle reconnects (review probe P2):
    /// the refused check is not replayed, the new connection asks for readings on its first message past the handshake, and
    /// the check asked on them -- new ids, the new session -- lifts the hold.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task AfterARestartTheReleaseWaitingForReaskReadingsAsksForThemInTheNewSession()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_REASK_RESTART";
        const string proof = "repair-release-reask-restart-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            string first;
            await using (ControlServerDbContext before = await CreateContextAsync(connection))
            {
                (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                    await HoldTheVehicleAsync(before, proofVariable, proof);
                first = await ReachReleaseCheckAsync(processor, peer, state, proof, "e8520000-0000-4000-8000-0000000008");
                await ExchangeAsync(processor, peer, state, CheckRefused("e8520000-0000-4000-8000-000000000810", first));
            }

            await using ControlServerDbContext context = await CreateContextAsync(connection);
            RecordingPeer restartedPeer = new(context);
            OnboardMessageProcessor restarted = Processor(context, restartedPeer, proofVariable);
            OnboardConnectionState reconnected = CurrentState(deferOutbound: true);
            List<string> handshake = [.. await ReconnectAsync(restarted, restartedPeer, reconnected)];
            await FinishHandshakeAsync(restarted, restartedPeer, reconnected, handshake, safetyStateVersion: 9);
            long generation = reconnected.SessionGeneration!.Value;
            Assert.DoesNotContain(handshake, line => line.Contains(MessageIdOf(first), StringComparison.Ordinal));
            Assert.DoesNotContain(handshake, line => MessageType(line) == "PreDepartureSafetyCheck");

            string[] next = await ExchangeAsync(restarted, restartedPeer, reconnected, InSession(Envelope(
                "e8520000-0000-4000-8000-000000000811",
                "OnboardAlarmSnapshot",
                new { alarmSnapshotRevision = 2, observedAt = Now, alarms = Array.Empty<object>() }), generation));
            Assert.Equal("PRE_MOVEMENT_RECONCILIATION",
                PayloadOf(Assert.Single(next, line => MessageType(line) == "SafetyStateSnapshotRequested"))
                    .GetProperty("reason").GetString());

            string again = Assert.Single(
                await ExchangeAsync(restarted, restartedPeer, reconnected,
                    InSession(SlotReadings("e8520000-0000-4000-8000-000000000812", 10), generation)),
                line => MessageType(line) == "PreDepartureSafetyCheck");
            Assert.NotEqual(MessageIdOf(first), MessageIdOf(again));
            Assert.NotEqual(CheckIdOf(first), CheckIdOf(again));
            Assert.Equal(generation, JsonDocument.Parse(again).RootElement.GetProperty("sessionGeneration").GetInt64());
            Assert.Equal(10, PayloadOf(again).GetProperty("expectedSafetyStateVersion").GetInt64());

            await ExchangeAsync(restarted, restartedPeer, reconnected,
                InSession(HoldReleaseCheckResult("e8520000-0000-4000-8000-000000000813", again), generation));
            Assert.Equal((RecoveryWorkflowState.Reconciled, "SAFE"),
                ((await ReleaseAsync(context)).State, (await ReleaseAsync(context)).Outcome));
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
        }
    }

    /// <summary>
    /// A re-asked check answered UNSAFE spends the release as the first check's UNSAFE answer would (review probe P3): the
    /// onboard answers from the present IO, so readings that changed after they were judged end here.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    [Trait("ProtocolVector", "CV-VEHICLE-HOLD-DOOR-REPAIR-RELEASE")]
    public async Task AnUnsafeAnswerToAReaskedReleaseCheckSpendsTheRelease()
    {
        const string proofVariable = "CONTROL_SERVER_TEST_RECOVERY_PROOF_REPAIR_RELEASE_REASK_UNSAFE";
        const string proof = "repair-release-reask-unsafe-proof-not-a-production-secret";
        Environment.SetEnvironmentVariable(proofVariable, proof);
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            await using SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(token);
            await using ControlServerDbContext context = await CreateContextAsync(connection);
            (RecordingPeer peer, OnboardMessageProcessor processor, OnboardConnectionState state) =
                await HoldTheVehicleAsync(context, proofVariable, proof);
            string first = await ReachReleaseCheckAsync(processor, peer, state, proof, "e8520000-0000-4000-8000-0000000009");
            await ExchangeAsync(processor, peer, state, CheckRefused("e8520000-0000-4000-8000-000000000910", first));
            string again = Assert.Single(
                await ExchangeAsync(processor, peer, state, SlotReadings("e8520000-0000-4000-8000-000000000911", 9)),
                line => MessageType(line) == "PreDepartureSafetyCheck");

            await ExchangeAsync(processor, peer, state,
                HoldReleaseCheckResult("e8520000-0000-4000-8000-000000000912", again, outcome: "UNSAFE"));

            RecoveryWorkflowRow spent = await ReleaseAsync(context);
            Assert.Equal((RecoveryWorkflowState.RecoveryRequired, "UNSAFE"), (spent.State, spent.Outcome));
            Assert.Null((await context.SlotDoorHolds.AsNoTracking().SingleAsync(token)).ReleasedAt);
        }
        finally
        {
            Environment.SetEnvironmentVariable(proofVariable, null);
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

    /// <summary>
    /// The vehicle's <c>PREDEPARTURE_CHECK_EXPIRED</c> refusal of <paramref name="check"/>, with no result, as the onboard
    /// writes it (<c>WireToGateSessionClient.CreateProtocolProblem</c>): correlated to the refused message, with the
    /// expected protocol identity (review L1).
    /// </summary>
    private static string CheckRefused(string messageId, string check, string reasonCode = "PREDEPARTURE_CHECK_EXPIRED") => Envelope(
        messageId,
        "ProtocolProblem",
        new
        {
            rejectedMessageId = MessageIdOf(check),
            rejectedMessageType = "PreDepartureSafetyCheck",
            problem = new { reasonCode, fieldPath = (string?)null, displayMessage = (string?)null },
            expectedProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            expectedProfileId = ProtocolCandidateIdentity.ProfileId,
            expectedProtocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256
        },
        MessageIdOf(check));

    private static Task<RecoveryWorkflowRow> ReleaseAsync(ControlServerDbContext context) =>
        context.RecoveryWorkflows.AsNoTracking()
            .SingleAsync(row => row.WorkflowId == ReleaseActionId, TestContext.Current.CancellationToken);

    private static string CheckIdOf(string check) =>
        PayloadOf(check).GetProperty("preDepartureSafetyCheckId").GetString()!;
}
