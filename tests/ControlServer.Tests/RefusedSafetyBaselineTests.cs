using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
        Assert.NotEqual(JourneyRuntimeStage.AwaitingSublot, carryingOn.Stage);
        Assert.NotEqual("ONBOARD_SESSION_NOT_READY", carryingOn.BlockReasonCode);
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
