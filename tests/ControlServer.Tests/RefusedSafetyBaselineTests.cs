using System.Data.Common;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.JourneyRuntimeWorkerLoadCancellationBeforeSublotTests;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// A refused safety message leaves the session's safety baseline untrusted until a fresh SafetyStateSnapshot arrives
/// (control-server#478, review S2).
/// </summary>
/// <remarks>
/// <para>
/// Before #478 a refused SafetyStateChanged or SafetyStateSnapshot ended the connection, and the reconnect's handshake
/// brought a new safety baseline. With the connection kept, the server would go on judging departure on whichever content
/// arrived first while the vehicle holds another -- admission line 1. So the departure verdict is cleared, readiness falls to
/// RecoveryRequired / DEPARTURE_SAFETY_NOT_READY (the state an unsafe vehicle sits in every day: dispatch held, journeys
/// named ONBOARD_SESSION_NOT_READY, nothing escalated), and a snapshot is asked for. A SafetyStateChanged does not restore
/// it -- it names what changed, not every slot -- a SafetyStateSnapshot does.
/// </para>
/// <para>
/// Driven through the real processor and the real runtime on one database, at a pickup stop waiting for its entry, so that
/// "the journey is back to normal" is the engine's own verdict and not an assertion about a row.
/// </para>
/// </remarks>
public sealed class RefusedSafetyBaselineTests
{
    private static readonly int[] FirstSlot = [1];
    private static readonly string[] LockNotClosed = ["LOCK_NOT_CLOSED"];

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-SAME-REVISION-CONFLICT")]
    public async Task ARefusedSafetyChangeHoldsDispatchUntilAFreshSnapshotAndThenTheJourneyCarriesOn()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        OnboardConnectionState state = Connected(fixture);
        OnboardDispatchFactsReader dispatchFacts = new(fixture.Context, Options.Create(fixture.Options), fixture.Clock);
        Assert.NotNull(await dispatchFacts.CurrentReadySessionAsync(fixture.Options.AgvId, token));
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();

        // Revision 7 is the session's baseline; this is other content under it.
        string conflicting = SafetyLine(fixture, "SafetyStateChanged", revision: 7, departureSafe: true);
        string[] refusal = Lines(await processor.ProcessAsync(conflicting, state, token));

        ProtocolProblemAssert.RefusedLine(refusal[0], "SNAPSHOT_REVISION_CONTENT_CONFLICT", conflicting);
        Assert.Equal(["ProtocolProblem", "SessionReadiness", "SafetyStateSnapshotRequested"], refusal.Select(TypeOf).ToArray());
        Assert.Equal("RECOVERY_REQUIRED", Payload(refusal[1]).GetProperty("readiness").GetString());
        SessionRecoveryRow untrusted = await SessionAsync(fixture);
        Assert.Equal(SessionReadiness.RecoveryRequired, untrusted.Readiness);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", untrusted.ReasonCode);
        Assert.Null(untrusted.DepartureSafe);
        Assert.Equal(7, untrusted.SafetyRevision);
        Assert.Null(await dispatchFacts.CurrentReadySessionAsync(fixture.Options.AgvId, token));

        await fixture.Engine.ExecuteOnceAsync(token);
        JourneyRuntimeRow held = await fixture.RuntimeAsync();
        Assert.Equal(waiting.Stage, held.Stage);
        Assert.Equal("ONBOARD_SESSION_NOT_READY", held.BlockReasonCode);

        // A change, however safe, is not a baseline.
        string change = SafetyLine(fixture, "SafetyStateChanged", revision: 8, departureSafe: true);
        Assert.Equal("DurableAck", TypeOf(Lines(await processor.ProcessAsync(change, state, token))[0]));
        SessionRecoveryRow stillUntrusted = await SessionAsync(fixture);
        Assert.Equal(SessionReadiness.RecoveryRequired, stillUntrusted.Readiness);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", stillUntrusted.ReasonCode);
        Assert.Null(stillUntrusted.DepartureSafe);
        Assert.Equal(8, stillUntrusted.SafetyRevision);
        Assert.Null(await dispatchFacts.CurrentReadySessionAsync(fixture.Options.AgvId, token));

        // The operator scans meanwhile: the entry is on file, but the stop does not load while departure is untrusted.
        await processor.ProcessAsync(SublotEntry(fixture, waiting, "SUBLOT-001"), state, token);
        await fixture.Engine.ExecuteOnceAsync(token);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);

        // The snapshot the request asked for: the next revision, a whole baseline.
        string snapshot = SafetyLine(fixture, "SafetyStateSnapshot", revision: 9, departureSafe: true);
        string[] restored = Lines(await processor.ProcessAsync(snapshot, state, token));
        Assert.Equal(["SnapshotAppliedAck", "SessionReadiness"], restored.Select(TypeOf).ToArray());
        Assert.Equal("READY", Payload(restored[1]).GetProperty("readiness").GetString());
        SessionRecoveryRow trusted = await SessionAsync(fixture);
        Assert.Equal(SessionReadiness.Ready, trusted.Readiness);
        Assert.True(trusted.DepartureSafe);
        Assert.False(state.SafetyBaselineUntrusted);
        Assert.NotNull(await dispatchFacts.CurrentReadySessionAsync(fixture.Options.AgvId, token));

        // The journey carries on from where it was held: the entry taken while untrusted now loads. (ONBOARD_SESSION_NOT_READY
        // is not cleared on its own while the stage stays the same -- SetStage clears it on a stage change, as for any
        // unsafe-then-safe episode -- so "carries on" is the stage moving, not the code disappearing.)
        await fixture.Engine.ExecuteOnceAsync(token);
        JourneyRuntimeRow carryingOn = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, carryingOn.Stage);
        Assert.Null(carryingOn.BlockReasonCode);
    }

    /// <summary>
    /// The same holds for a refused SafetyStateSnapshot (CV-SNAPSHOT-SAME-REVISION-CONFLICT's own message): untrusted,
    /// readiness down, a snapshot asked for.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-SNAPSHOT-SAME-REVISION-CONFLICT")]
    public async Task ARefusedSafetySnapshotLeavesTheBaselineUntrustedAndAsksForAnother()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        OnboardConnectionState state = Connected(fixture);

        string conflicting = SafetyLine(fixture, "SafetyStateSnapshot", revision: 7, departureSafe: true);
        string[] refusal = Lines(await processor.ProcessAsync(conflicting, state, token));

        ProtocolProblemAssert.RefusedLine(refusal[0], "SNAPSHOT_REVISION_CONTENT_CONFLICT", conflicting);
        Assert.Equal(["ProtocolProblem", "SessionReadiness", "SafetyStateSnapshotRequested"], refusal.Select(TypeOf).ToArray());
        Assert.True(state.SafetyBaselineUntrusted);
        SessionRecoveryRow untrusted = await SessionAsync(fixture);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", untrusted.ReasonCode);
        Assert.Null(untrusted.DepartureSafe);

        // A refused snapshot leaves the baseline as untrusted as a refused change does: a change after it, however safe,
        // does not bring the session back to Ready.
        await processor.ProcessAsync(SafetyLine(fixture, "SafetyStateChanged", revision: 8, departureSafe: true), state, token);
        SessionRecoveryRow afterChange = await SessionAsync(fixture);
        Assert.Equal(SessionReadiness.RecoveryRequired, afterChange.Readiness);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", afterChange.ReasonCode);
        Assert.Null(afterChange.DepartureSafe);
    }

    /// <summary>
    /// The departure verdict is cleared together with its reasons. A vehicle with this server's own slot operation in
    /// progress reads Ready while its only unsafety is the one that operation causes (LOCK_NOT_CLOSED, nothing unknown --
    /// WireToGateStore.IsUnsafetyExplainedByOwnCommandAsync). Left behind on an untrusted baseline, those reasons would
    /// keep that exemption alive and the vehicle Ready; cleared, it reads DEPARTURE_SAFETY_NOT_READY like any other.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task ARefusedSafetyMessageAlsoEndsTheOwnOperationExemption()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        WireToGateStore store = new(connection);
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, store, fixture.Clock, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        OnboardConnectionState state = Connected(fixture);
        await processor.ProcessAsync(SublotEntry(fixture, await fixture.RuntimeAsync(), "SUBLOT-001"), state, token);
        await fixture.Engine.ExecuteOnceAsync(token);
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, (await fixture.RuntimeAsync()).Stage);
        Assert.Equal(StationOperationStatus.Prepared,
            (await fixture.Context.StationOperations.AsNoTracking().SingleAsync(token)).Status);

        // The baseline says unsafe for the reason the vehicle's own load causes, and nothing is unknown.
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(token);
        session.DepartureSafe = false;
        session.SafetyReasonCodesJson = JsonSerializer.Serialize(LockNotClosed);
        session.SafetyUnknownPresent = false;
        await fixture.Context.SaveChangesAsync(token);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(SessionReadiness.Ready, (await store.DecideReadinessAsync(fixture.Options.AgvId, 1, token)).Readiness);

        string conflicting = SafetyLine(fixture, "SafetyStateChanged", revision: 7, departureSafe: true);
        ProtocolProblemAssert.RefusedLine(
            Lines(await processor.ProcessAsync(conflicting, state, token))[0], "SNAPSHOT_REVISION_CONTENT_CONFLICT", conflicting);

        SessionRecoveryRow untrusted = await SessionAsync(fixture);
        Assert.Equal(SessionReadiness.RecoveryRequired, untrusted.Readiness);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", untrusted.ReasonCode);
        Assert.Null(untrusted.SafetyReasonCodesJson);
        Assert.Null(untrusted.SafetyUnknownPresent);
    }

    /// <summary>
    /// The request sent with the refusal is usually not answered: the onboard leaves a snapshot request unanswered while it
    /// holds an unacknowledged SafetyStateChanged, and the refused one is such a change. So while the baseline is untrusted
    /// the server asks again on the vehicle's later messages -- no sooner than
    /// <see cref="OnboardMessageProcessor.SafetySnapshotReaskInterval"/> after the last request, so heartbeats do not
    /// become a request each.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task WhileTheBaselineIsUntrustedTheServerAsksAgainButNotOnEveryMessage()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        OnboardConnectionState state = Connected(fixture);
        await processor.ProcessAsync(SafetyLine(fixture, "SafetyStateChanged", revision: 7, departureSafe: true), state, token);

        string[] tooSoon = Lines(await processor.ProcessAsync(
            SafetyLine(fixture, "SafetyStateChanged", revision: 8, departureSafe: true), state, token));
        Assert.DoesNotContain("SafetyStateSnapshotRequested", tooSoon.Select(TypeOf));

        fixture.Clock.Advance(OnboardMessageProcessor.SafetySnapshotReaskInterval);
        string[] askedAgain = Lines(await processor.ProcessAsync(
            SafetyLine(fixture, "SafetyStateChanged", revision: 9, departureSafe: true), state, token));
        Assert.Contains("SafetyStateSnapshotRequested", askedAgain.Select(TypeOf));

        // Trusted again: no more asking, however long it has been.
        await processor.ProcessAsync(SafetyLine(fixture, "SafetyStateSnapshot", revision: 10, departureSafe: true), state, token);
        fixture.Clock.Advance(OnboardMessageProcessor.SafetySnapshotReaskInterval);
        string[] trusted = Lines(await processor.ProcessAsync(
            SafetyLine(fixture, "SafetyStateChanged", revision: 11, departureSafe: true), state, token));
        Assert.DoesNotContain("SafetyStateSnapshotRequested", trusted.Select(TypeOf));
    }

    /// <summary>
    /// The save that clears the departure verdict also lowers readiness (control-server#478 incremental review). Had the
    /// second save -- the one that decides readiness again -- been the first to lower it, a failure there would end the
    /// connection with the row still Ready, and dispatch reads nothing but Readiness.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task WhenTheReadinessDecisionFailsAfterTheVerdictIsClearedTheVehicleIsAlreadyOffDispatch()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        FailOnSessionUpdate failSecond = new(failOnUpdateNumber: 2);
        await using ControlServerDbContext connection = new(
            new DbContextOptionsBuilder<ControlServerDbContext>(fixture.DbOptionsForTests).AddInterceptors(failSecond).Options);
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        OnboardDispatchFactsReader dispatchFacts = new(fixture.Context, Options.Create(fixture.Options), fixture.Clock);

        failSecond.Armed = true;
        // The processor throws, which OnboardTcpServer ends the connection on.
        await Assert.ThrowsAnyAsync<Exception>(() => processor.ProcessAsync(
            SafetyLine(fixture, "SafetyStateChanged", revision: 7, departureSafe: true), Connected(fixture), token));

        Assert.True(failSecond.Fired, "第二次保存没有被注入失败，这条用例没有造出它要的形状。");
        SessionRecoveryRow row = await SessionAsync(fixture);
        Assert.Null(row.DepartureSafe);
        Assert.Equal(SessionReadiness.RecoveryRequired, row.Readiness);
        Assert.Equal("DEPARTURE_SAFETY_NOT_READY", row.ReasonCode);
        Assert.Null(await dispatchFacts.CurrentReadySessionAsync(fixture.Options.AgvId, token));
    }

    /// <summary>
    /// If the save that clears the verdict fails, nothing of it is kept and the connection ends: exactly what a refusal
    /// did before control-server#478, so the reconnect's handshake brings the new safety baseline.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task WhenClearingTheVerdictFailsTheRowIsUnchangedAndTheConnectionEnds()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await ReachSublotWaitAsync();
        SessionRecoveryRow before = await SessionAsync(fixture);
        FailOnSessionUpdate failFirst = new(failOnUpdateNumber: 1);
        await using ControlServerDbContext connection = new(
            new DbContextOptionsBuilder<ControlServerDbContext>(fixture.DbOptionsForTests).AddInterceptors(failFirst).Options);
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        OnboardConnectionState state = Connected(fixture);

        failFirst.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => processor.ProcessAsync(
            SafetyLine(fixture, "SafetyStateChanged", revision: 7, departureSafe: true), state, token));

        Assert.True(failFirst.Fired, "清空那次保存没有被注入失败，这条用例没有造出它要的形状。");
        SessionRecoveryRow after = await SessionAsync(fixture);
        Assert.Equal(before.Readiness, after.Readiness);
        Assert.Equal(before.ReasonCode, after.ReasonCode);
        Assert.Equal(before.DepartureSafe, after.DepartureSafe);
        Assert.Equal(before.SafetyRevision, after.SafetyRevision);
        Assert.Equal(before.SafetyHash, after.SafetyHash);
        Assert.False(state.SafetyBaselineUntrusted);
    }

    /// <summary>Fails the Nth UPDATE of the SessionRecoveries table once armed, the way a database error would.</summary>
    private sealed class FailOnSessionUpdate(int failOnUpdateNumber) : DbCommandInterceptor
    {
        private int _updates;

        public bool Armed { get; set; }

        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Check(DbCommand command)
        {
            if (!Armed || Fired || !command.CommandText.Contains("UPDATE \"SessionRecoveries\"", StringComparison.Ordinal))
            {
                return;
            }
            if (++_updates == failOnUpdateNumber)
            {
                Fired = true;
                throw new InvalidOperationException("Injected failure on a SessionRecoveries update (control-server#478 test).");
            }
        }
    }

    private static OnboardConnectionState Connected(RuntimeFixture fixture) => new()
    {
        AgvId = fixture.Options.AgvId,
        SessionGeneration = 1,
        CapabilityRevision = 1,
        SafetyRevision = 7,
        Readiness = SessionReadiness.Ready,
        HandshakeCompleted = true
    };

    private static string SafetyLine(RuntimeFixture fixture, string messageType, long revision, bool departureSafe) =>
        BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), messageType, 1, messageType == "SafetyStateSnapshot"
            ? new
            {
                safetyStateVersion = revision,
                observedAt = Now,
                safety = Safety(departureSafe),
                slotStates = Enumerable.Range(1, 8).Select(slot => new
                {
                    slotNo = slot,
                    operability = "OPERABLE",
                    administrativeAvailability = "ENABLED",
                    physicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                    reasonCodes = Array.Empty<string>()
                }).ToArray()
            }
            : new
            {
                safetyStateVersion = revision,
                observedAt = Now,
                safety = Safety(departureSafe),
                affectedSlots = FirstSlot
            });

    private static object Safety(bool departureSafe) => new
    {
        departureSafe,
        vehicleStopped = true,
        allTargetSlotsLocked = true,
        allUnlockOutputsReset = true,
        unknownPresent = false,
        reasonCodes = Array.Empty<string>()
    };

    private static async Task<SessionRecoveryRow> SessionAsync(RuntimeFixture fixture) =>
        await fixture.Context.SessionRecoveries.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

    private static string[] Lines(string response) => response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string TypeOf(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("messageType").GetString()!;
    }

    private static JsonElement Payload(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("payload").Clone();
    }
}
