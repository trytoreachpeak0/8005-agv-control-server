using System.Text.Json;
using System.Text.Json.Nodes;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// Each slot's lock is read from the session's latest SafetyStateSnapshot, and Onboard sends one only in the handshake and
/// when asked (control-server#865). A session established while a slot door stood open kept that slot "not locked" for the
/// whole generation once the door was locked again -- SafetyStateChanged moves the revision on but carries no slot states,
/// real onboard naming slots 1..8 in every one -- so the vehicle got no charger and no idle return until it reconnected
/// (agv02, generation 50, 2026-10-23). The server now asks for a fresh snapshot when a safety change follows a snapshot
/// that showed a slot unsecured.
/// </summary>
/// <remarks>
/// The departure gate itself is unchanged: it still trusts nothing but a snapshot's <c>LOCKED</c>/<c>RESET</c>. Driven
/// through the real processor and the real gate on one database.
/// </remarks>
public sealed class StaleSlotLockSnapshotTests
{
    private static readonly int[] AllSlots = [1, 2, 3, 4, 5, 6, 7, 8];

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task ASessionEstablishedWithADoorOpenSeesTheDoorLockedOnceItIsLocked()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await OpenSlotInHandshakeSnapshotAsync(fixture, slotNo: 1);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        OnboardConnectionState state = Connected(fixture);
        Assert.Equal(["SLOT_1_NOT_LOCKED"], await GapsAsync(fixture, token));

        // The door is locked: the vehicle reports a change, which names every slot and holds no slot state.
        string[] change = Lines(await processor.ProcessAsync(Change(fixture, revision: 8), state, token));

        Assert.Equal("DurableAck", TypeOf(change[0]));
        Assert.Contains("SafetyStateSnapshotRequested", change.Select(TypeOf));
        Assert.Equal(["SLOT_1_NOT_LOCKED"], await GapsAsync(fixture, token));

        // The snapshot asked for shows the door locked, and the gate opens on it.
        await processor.ProcessAsync(Snapshot(fixture, revision: 9, unlockedSlot: null), state, token);
        Assert.Empty(await GapsAsync(fixture, token));
    }

    /// <summary>
    /// A door that really is open is asked about once per change and then left alone: the snapshot answers it open at the
    /// current revision, and heartbeats after that ask nothing, however long the door stays open.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task ADoorThatReallyIsOpenIsAskedAboutOnceAndTheDepartureGateStaysShut()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await OpenSlotInHandshakeSnapshotAsync(fixture, slotNo: 1);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = Processor(fixture, connection);
        OnboardConnectionState state = Connected(fixture);

        Assert.Equal(1, Count(await processor.ProcessAsync(Change(fixture, revision: 8), state, token)));
        await processor.ProcessAsync(Snapshot(fixture, revision: 9, unlockedSlot: 1), state, token);
        Assert.Equal(["SLOT_1_NOT_LOCKED"], await GapsAsync(fixture, token));

        for (int beat = 0; beat < 5; beat++)
        {
            fixture.Clock.Advance(OnboardMessageProcessor.SafetySnapshotReaskInterval);
            Assert.Equal(0, Count(await processor.ProcessAsync(Heartbeat(fixture), state, token)));
        }
        Assert.Equal(["SLOT_1_NOT_LOCKED"], await GapsAsync(fixture, token));

        // The next change may be the door closing, and is asked about in its turn.
        Assert.Equal(1, Count(await processor.ProcessAsync(Change(fixture, revision: 10), state, token)));
    }

    /// <summary>All slots locked in the snapshot on file: a change asks for nothing (no added traffic in the everyday case).</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task WithEverySlotLockedInTheSnapshotAChangeAsksForNothing()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = Processor(fixture, connection);
        OnboardConnectionState state = Connected(fixture);

        Assert.Equal(0, Count(await processor.ProcessAsync(Change(fixture, revision: 8), state, token)));
        fixture.Clock.Advance(OnboardMessageProcessor.SafetySnapshotReaskInterval);
        Assert.Equal(0, Count(await processor.ProcessAsync(Heartbeat(fixture), state, token)));
        Assert.Empty(await GapsAsync(fixture, token));
    }

    /// <summary>
    /// A snapshot that never comes is asked for again, but no sooner than
    /// <see cref="OnboardMessageProcessor.SafetySnapshotReaskInterval"/> after the last request: a burst of changes (a door
    /// closing comes as about four within a second) and a heartbeat every two seconds do not become a request each.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task ASnapshotThatNeverComesIsAskedForAgainButNotOnEveryMessage()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await OpenSlotInHandshakeSnapshotAsync(fixture, slotNo: 1);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = Processor(fixture, connection);
        OnboardConnectionState state = Connected(fixture);

        int burst = 0;
        burst += Count(await processor.ProcessAsync(Change(fixture, revision: 8), state, token));
        burst += Count(await processor.ProcessAsync(Change(fixture, revision: 9), state, token));
        burst += Count(await processor.ProcessAsync(Change(fixture, revision: 10), state, token));
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        burst += Count(await processor.ProcessAsync(Heartbeat(fixture), state, token));
        Assert.Equal(1, burst);

        fixture.Clock.Advance(OnboardMessageProcessor.SafetySnapshotReaskInterval);
        Assert.Equal(1, Count(await processor.ProcessAsync(Heartbeat(fixture), state, token)));
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0, Count(await processor.ProcessAsync(Heartbeat(fixture), state, token)));

        // A minute of heartbeats every two seconds: one request per interval at most.
        int minute = 0;
        for (int beat = 0; beat < 30; beat++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(2));
            minute += Count(await processor.ProcessAsync(Heartbeat(fixture), state, token));
        }
        Assert.InRange(minute, 1, (int)(TimeSpan.FromSeconds(60) / OnboardMessageProcessor.SafetySnapshotReaskInterval));

        // Answered at last: the asking stops.
        await processor.ProcessAsync(Snapshot(fixture, revision: 11, unlockedSlot: null), state, token);
        fixture.Clock.Advance(OnboardMessageProcessor.SafetySnapshotReaskInterval);
        Assert.Equal(0, Count(await processor.ProcessAsync(Heartbeat(fixture), state, token)));
        Assert.Empty(await GapsAsync(fixture, token));
    }

    /// <summary>
    /// After a reconnect the new generation is judged on its own snapshots: a new connection whose handshake snapshot shows a
    /// door open asks once the door is locked, and the previous generation's snapshot, all locked, counts for nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task AfterAReconnectTheNewGenerationIsAskedAboutItsOwnOpenDoor()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = Processor(fixture, connection);

        // Generation 1 is established with slot 1 open, asks after a change, and the connection drops before any answer.
        await OpenSlotInHandshakeSnapshotAsync(fixture, slotNo: 1);
        OnboardConnectionState first = Connected(fixture);
        Assert.Equal(1, Count(await processor.ProcessAsync(Change(fixture, revision: 8), first, token)));

        // Generation 2's handshake snapshot shows the door still open. Nothing of the old connection's asking carries over:
        // no request before a change of this generation.
        await fixture.AdvanceSessionAsync(2);
        await OpenSlotInHandshakeSnapshotAsync(fixture, slotNo: 1, generation: 2);
        OnboardConnectionState second = Connected(fixture, generation: 2);
        Assert.Equal(["SLOT_1_NOT_LOCKED"], await GapsAsync(fixture, token));
        fixture.Clock.Advance(OnboardMessageProcessor.SafetySnapshotReaskInterval);
        Assert.Equal(0, Count(await processor.ProcessAsync(Heartbeat(fixture, generation: 2), second, token)));

        // The door is locked: asked, answered, and the gate opens.
        Assert.Equal(1, Count(await processor.ProcessAsync(Change(fixture, revision: 8, generation: 2), second, token)));
        await processor.ProcessAsync(Snapshot(fixture, revision: 9, unlockedSlot: null, generation: 2), second, token);
        Assert.Empty(await GapsAsync(fixture, token));
    }

    /// <summary>
    /// The previous generation's snapshot counts for nothing: generation 1 ended with slot 1 open, generation 2's handshake
    /// snapshot shows every slot locked, and a change of generation 2 asks for nothing.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task AnOpenDoorInThePreviousGenerationAsksNothingOfTheNewOne()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await OpenSlotInHandshakeSnapshotAsync(fixture, slotNo: 1);
        await fixture.AdvanceSessionAsync(2);
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = Processor(fixture, connection);
        OnboardConnectionState second = Connected(fixture, generation: 2);

        Assert.Equal(0, Count(await processor.ProcessAsync(Change(fixture, revision: 8, generation: 2), second, token)));
        Assert.Empty(await GapsAsync(fixture, token));
    }

    /// <summary>
    /// The asking does not wait for the session to be ready. Real onboard reads RecoveryRequired all the while it carries this
    /// server's own order, and agv02's generation 50 began that way: the slot was locked at 14:39:15 while the session was
    /// still RECOVERY_REQUIRED / SESSION_RECOVERY_REQUIRED. The request goes out on such a session, and the gate opens on the
    /// answer (here the session is decided ready again by the store itself: nothing in this fixture keeps it not ready).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task ASessionNotReadyWhenTheDoorIsLockedIsStillAsked()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await OpenSlotInHandshakeSnapshotAsync(fixture, slotNo: 1);
        await SetReadinessAsync(fixture, SessionReadiness.RecoveryRequired, "SESSION_RECOVERY_REQUIRED");
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = Processor(fixture, connection);
        OnboardConnectionState state = Connected(fixture);
        state.Readiness = SessionReadiness.RecoveryRequired;
        Assert.Equal(["ONBOARD_SESSION_NOT_READY"], await GapsAsync(fixture, token));

        Assert.Equal(1, Count(await processor.ProcessAsync(Change(fixture, revision: 8), state, token)));
        await processor.ProcessAsync(Snapshot(fixture, revision: 9, unlockedSlot: null), state, token);
        Assert.Empty(await GapsAsync(fixture, token));
    }

    private static OnboardMessageProcessor Processor(RuntimeFixture fixture, ControlServerDbContext connection) =>
        TestOnboardProcessorFactory.Create(
            connection, new WireToGateStore(connection), fixture.Clock, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static async Task SetReadinessAsync(RuntimeFixture fixture, SessionReadiness readiness, string reasonCode)
    {
        SessionRecoveryRow session = await fixture.Context.SessionRecoveries.SingleAsync(TestContext.Current.CancellationToken);
        session.Readiness = readiness;
        session.ReasonCode = reasonCode;
        session.RecoveryReportId ??= Guid.NewGuid().ToString("D");
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
    }

    private static int Count(string response) =>
        Lines(response).Select(TypeOf).Count(type => type == "SafetyStateSnapshotRequested");

    private static async Task OpenSlotInHandshakeSnapshotAsync(RuntimeFixture fixture, int slotNo, long generation = 1)
    {
        ProtocolInboxRow[] rows = await fixture.Context.ProtocolInbox
            .Where(item => item.MessageType == "SafetyStateSnapshot")
            .ToArrayAsync(TestContext.Current.CancellationToken);
        ProtocolInboxRow row = rows.Single(
            item => (long)JsonNode.Parse(item.RequestJson)!["sessionGeneration"]! == generation);
        JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
        JsonNode slot = root["payload"]!["slotStates"]!.AsArray().Single(item => (int)item!["slotNo"]! == slotNo)!;
        slot["lockState"] = "UNLOCKED";
        row.RequestJson = root.ToJsonString();
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
    }

    private static async Task<IReadOnlyList<string>> GapsAsync(RuntimeFixture fixture, CancellationToken token)
    {
        fixture.Context.ChangeTracker.Clear();
        OnboardDispatchFactsReader reader = new(fixture.Context, Options.Create(fixture.Options), fixture.Clock);
        return await NonBusinessDepartureGate.SessionGapsAsync(reader, fixture.Options.AgvId, token);
    }

    private static OnboardConnectionState Connected(RuntimeFixture fixture, long generation = 1) => new()
    {
        AgvId = fixture.Options.AgvId,
        SessionGeneration = generation,
        CapabilityRevision = 1,
        SafetyRevision = 7,
        Readiness = SessionReadiness.Ready,
        HandshakeCompleted = true
    };

    private static string Change(RuntimeFixture fixture, long revision, long generation = 1) =>
        BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), "SafetyStateChanged", generation, new
        {
            safetyStateVersion = revision,
            observedAt = fixture.Clock.GetUtcNow(),
            safety = Safety(),
            affectedSlots = AllSlots
        });

    private static string Snapshot(RuntimeFixture fixture, long revision, int? unlockedSlot, long generation = 1) =>
        BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), "SafetyStateSnapshot", generation, new
        {
            safetyStateVersion = revision,
            observedAt = fixture.Clock.GetUtcNow(),
            safety = Safety(),
            slotStates = AllSlots.Select(slot => new
            {
                slotNo = slot,
                operability = "OPERABLE",
                administrativeAvailability = "ENABLED",
                physicalState = "EMPTY",
                lockState = slot == unlockedSlot ? "UNLOCKED" : "LOCKED",
                unlockOutputState = "RESET",
                reasonCodes = Array.Empty<string>()
            }).ToArray()
        });

    private static string Heartbeat(RuntimeFixture fixture, long generation = 1) =>
        BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), "Heartbeat", generation, new { });

    // The abstract summary stays safe throughout: the vehicle's own flags were never what held agv02 back.
    private static object Safety() => new
    {
        departureSafe = true,
        vehicleStopped = true,
        allTargetSlotsLocked = true,
        allUnlockOutputsReset = true,
        unknownPresent = false,
        reasonCodes = Array.Empty<string>()
    };

    private static string[] Lines(string response) => response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string TypeOf(string line)
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("messageType").GetString()!;
    }
}
