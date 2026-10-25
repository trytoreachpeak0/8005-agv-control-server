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

    private static async Task OpenSlotInHandshakeSnapshotAsync(RuntimeFixture fixture, int slotNo)
    {
        ProtocolInboxRow row = await fixture.Context.ProtocolInbox.SingleAsync(
            item => item.MessageType == "SafetyStateSnapshot", TestContext.Current.CancellationToken);
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

    private static OnboardConnectionState Connected(RuntimeFixture fixture) => new()
    {
        AgvId = fixture.Options.AgvId,
        SessionGeneration = 1,
        CapabilityRevision = 1,
        SafetyRevision = 7,
        Readiness = SessionReadiness.Ready,
        HandshakeCompleted = true
    };

    private static string Change(RuntimeFixture fixture, long revision) =>
        BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), "SafetyStateChanged", 1, new
        {
            safetyStateVersion = revision,
            observedAt = fixture.Clock.GetUtcNow(),
            safety = Safety(),
            affectedSlots = AllSlots
        });

    private static string Snapshot(RuntimeFixture fixture, long revision, int? unlockedSlot) =>
        BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), "SafetyStateSnapshot", 1, new
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

    private static string Heartbeat(RuntimeFixture fixture) =>
        BeforeSublotEnvelope(fixture, Guid.NewGuid().ToString("D"), "Heartbeat", 1, new { });

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
