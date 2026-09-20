using System.Text.Json.Nodes;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Adapters;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// Split out of the one partial JourneyRuntimeWorkerTests class so xunit runs it in parallel with the
/// rest (control-server#130); the shared fixture is <see cref="JourneyRuntimeWorkerTestKit"/>.
/// </summary>
public sealed class JourneyRuntimeWorkerSlotGroupAndRestartTests
{
    private static readonly string[] UnknownReasonCodes = ["IO_FACT_UNKNOWN"];

    // ---- slot group selection (control-server#73) ---------------------------------------------

    /// <summary>
    /// A demand whose AREA is assigned the rear group goes into the lowest free rear slots, and the journey
    /// carries them: loading and unloading both read that one <c>TargetSlotsJson</c>, whatever the station.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task ADemandAssignedTheRearGroupIsAcceptedIntoTheLowestFreeRearSlots()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.ImportAreaAssignmentsAsync(
            [.. RuntimeFixture.DefaultAssignedAreas.Select(area =>
                new AreaAssignment(area, fixture.Options.DispatchZone, area == "N1-1" ? "REAR" : "FRONT"))]);
        fixture.Catalog.Set(fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 12);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(3, runtime.ExpectedBasketCount);
        Assert.Equal("[5,6,7]", runtime.TargetSlotsJson);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    /// <summary>
    /// With neither an active slot configuration nor a published IO binding the vehicle's groups are unknown,
    /// and nothing is dispatched to it -- not onto an assumed eight-slot model.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AVehicleWithNoSlotModelOnRecordIsNotDispatched()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync(bindSlotModel: false);
        fixture.Catalog.Set(fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            DispatchReasonCodes.VehicleSlotModelUnresolved,
            (await fixture.Context.JourneyBacklog.SingleAsync(TestContext.Current.CancellationToken)).ReasonCode);
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
    }

    /// <summary>
    /// A demand needing more baskets than its group has physical slots on any vehicle is refused, raised once as a
    /// structural dispatch block that the next round only refreshes, and cleared once MES drops the demand
    /// (control-server#74). The summary runs after the vehicle loop and changes nothing about what was dispatched.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task ADemandNoVehicleHasTheSlotsForIsBlockedStructurallyNotDispatchedAndClearedWhenItLeavesTheCatalog()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        // Five baskets of four boxes each, for a FRONT group of four physical slots.
        fixture.BoxCounts.Set("SUBLOT-001", 20);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        DateTimeOffset firstRound = fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup,
            (await fixture.Context.JourneyBacklog.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).ReasonCode);
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
        StructuralDispatchBlockRow block = await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup, block.ReasonCode);
        Assert.Equal(firstRound, block.FirstRaisedAt);
        Assert.Equal(firstRound.AddSeconds(1), block.LastSeenAt);
        Assert.Null(block.ClearedAt);
        Assert.Single(fixture.StructuralBlockLog.Entries, entry => entry.Level == LogLevel.Warning);

        fixture.Catalog.Set();
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            firstRound.AddSeconds(2),
            (await fixture.Context.Set<StructuralDispatchBlockRow>().AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken)).ClearedAt);
    }

    /// <summary>
    /// The whole vehicle has room, the demand's group does not: it waits rather than borrowing the other side,
    /// and is taken, into its own group, once slots there free up.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AShortGroupWaitsWhileTheOtherGroupIsEmptyAndIsTakenOnceItsOwnSlotsFreeUp()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.ImportAreaAssignmentsAsync(
            [.. RuntimeFixture.DefaultAssignedAreas.Select(area =>
                new AreaAssignment(area, fixture.Options.DispatchZone, area == "N1-1" ? "REAR" : "FRONT"))]);
        await SetSlotPhysicalStateAsync(fixture, "OCCUPIED", 5, 6, 7, 8);
        fixture.Catalog.Set(fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 8);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable,
            (await fixture.Context.JourneyBacklog.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).ReasonCode);
        Assert.Empty(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);

        await SetSlotPhysicalStateAsync(fixture, "EMPTY", 5, 6);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("[5,6]", (await fixture.RuntimeAsync()).TargetSlotsJson);
    }

    /// <summary>
    /// More free slots in its group does not put a candidate first (REQ-0208): the older demand, squeezed into
    /// the last two free front slots, is taken ahead of a newer one whose rear group is empty.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task MoreRoomInItsGroupDoesNotPutACandidateAheadOfAnOlderOne()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        await fixture.ImportAreaAssignmentsAsync(
            [.. RuntimeFixture.DefaultAssignedAreas.Select(area =>
                new AreaAssignment(area, fixture.Options.DispatchZone, area == "N1-2" ? "REAR" : "FRONT"))]);
        await SetSlotPhysicalStateAsync(fixture, "OCCUPIED", 1, 2);
        fixture.Catalog.Set(
            fixture.Demand("10000000-0000-4000-8000-000000000002", "SUBLOT-002", Now.AddMinutes(-5), area: "N1-2", eqp: "EQP-02"),
            fixture.Demand("10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 8);
        fixture.BoxCounts.Set("SUBLOT-002", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal("10000000-0000-4000-8000-000000000001", runtime.DemandId);
        Assert.Equal("[3,4]", runtime.TargetSlotsJson);
    }

    /// <summary>Sets <c>physicalState</c> on the given slots in the session's stored handshake snapshots.</summary>
    private static async Task SetSlotPhysicalStateAsync(RuntimeFixture fixture, string physicalState, params int[] slotNumbers)
    {
        ProtocolInboxRow[] rows = await fixture.Context.ProtocolInbox
            .Where(row => row.MessageType == "CapabilitySnapshot" || row.MessageType == "SafetyStateSnapshot")
            .ToArrayAsync(TestContext.Current.CancellationToken);
        foreach (ProtocolInboxRow row in rows)
        {
            JsonObject root = JsonNode.Parse(row.RequestJson)!.AsObject();
            foreach (JsonNode? slot in root["payload"]!["slotStates"]!.AsArray())
            {
                if (slotNumbers.Contains(slot!["slotNo"]!.GetValue<int>()))
                {
                    slot["physicalState"] = physicalState;
                }
            }
            row.RequestJson = root.ToJsonString(SerializerOptions);
        }
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task RestartKeepsOriginalVehicleLeaseAndStablePickupIdentity()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow after = await fixture.RuntimeAsync();

        Assert.Equal(before.DemandId, after.DemandId);
        Assert.Equal(before.PickupMovementLegId, after.PickupMovementLegId);
        Assert.Equal(before.PickupUpperId, after.PickupUpperId);
        Assert.Equal(1, await fixture.Context.VehicleDispatchLeases.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.Context.OrderIntents.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
    }

    /// <summary>
    /// CV-DEMAND-ACCEPT-TO-PICKUP in the order its input.ndjson gives it: once the TO_PICKUP order
    /// exists the vehicle is told its plan -- the pickup leg ACTIVE, the drop-off leg PLANNED --
    /// before it arrives; at the pickup it is told the worklist first and the arrived plan after
    /// it, one revision up.
    /// </summary>
    /// <remarks>
    /// Until 2026-09-13 nothing went out before the arrival and a single plan went out after the
    /// worklist. No gate caught it: this suite compared the outbox as a sorted set of types, and the
    /// onboard end's G2 is written against a fake server that sends the vector's order, so each end
    /// passed against its own picture of the other. The order is read from the wire lines the peer
    /// received, because the fixture clock does not move and every outbox row shares a CreatedAt.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task ThePlanGoesOutBeforeArrivalAndAgainAfterTheWorklistAtThePickup()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, runtime.Stage);
        Assert.Equal("CONFIRMED", await fixture.IntentStatusAsync("TO_PICKUP"));

        (string Type, string MessageId, JsonElement Payload)[] beforeArrival = JourneySnapshotsInFirstSentOrder(fixture);
        (string Type, string MessageId, JsonElement Payload) dispatchPlan = Assert.Single(beforeArrival);
        Assert.Equal("UpcomingStopPlanSnapshot", dispatchPlan.Type);
        Assert.Equal(runtime.PlanRevision, dispatchPlan.Payload.GetProperty("planRevision").GetInt64());
        Assert.Equal(
            [("TO_PICKUP", "ACTIVE"), ("TO_DROPOFF", "PLANNED")],
            dispatchPlan.Payload.GetProperty("legs").EnumerateArray()
                .Select(leg => (leg.GetProperty("legType").GetString()!, leg.GetProperty("state").GetString()!))
                .ToArray());
        Assert.Single(
            await fixture.Context.ProtocolOutbox.AsNoTracking()
                .Where(row => row.MessageType == "UpcomingStopPlanSnapshot")
                .ToArrayAsync(TestContext.Current.CancellationToken));

        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);

        (string Type, string MessageId, JsonElement Payload)[] sequence = JourneySnapshotsInFirstSentOrder(fixture);
        Assert.Equal(
            ["UpcomingStopPlanSnapshot", "CurrentStopWorklistSnapshot", "UpcomingStopPlanSnapshot"],
            sequence.Select(item => item.Type).ToArray());
        Assert.Equal(dispatchPlan.MessageId, sequence[0].MessageId);
        Assert.Equal(runtime.PlanRevision + 1, sequence[2].Payload.GetProperty("planRevision").GetInt64());
        Assert.Equal(
            [("TO_PICKUP", "ARRIVED"), ("TO_DROPOFF", "PLANNED")],
            sequence[2].Payload.GetProperty("legs").EnumerateArray()
                .Select(leg => (leg.GetProperty("legType").GetString()!, leg.GetProperty("state").GetString()!))
                .ToArray());
    }

    /// <summary>
    /// The plan sent before the arrival is superseded by the one sent at the pickup, and a superseded
    /// snapshot is never worth delivering: replayed after the higher revision, it is a regression the
    /// vehicle refuses by tearing the session down. A peer that lost every acknowledgement from the
    /// first plan to the pickup is the one that would reach it, so the pickup retires it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task ThePlanSentBeforeArrivalIsRetiredWhenThePickupPlanSupersedesIt()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        AdoptingPeer peer = new(fixture.Context, fixture.Clock);
        fixture.Peer.OnMessageSent = line =>
        {
            peer.Receive(line);
            return Task.CompletedTask;
        };

        async Task IterateLosingAcksAsync()
        {
            peer.LoseBufferedAcks();
            await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        }

        await IterateLosingAcksAsync();
        await IterateLosingAcksAsync();
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        await IterateLosingAcksAsync();
        await IterateLosingAcksAsync();
        await IterateLosingAcksAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
        Assert.DoesNotContain(peer.Regressions, item => item.MessageType == "UpcomingStopPlanSnapshot");
        ProtocolOutboxRow[] plans = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .Where(row => row.MessageType == "UpcomingStopPlanSnapshot")
            .ToArrayAsync(TestContext.Current.CancellationToken);
        ProtocolOutboxRow retired = Assert.Single(plans, row => PlanRevisionOf(row) == runtime.PlanRevision);
        Assert.NotNull(retired.FencedAt);
        Assert.Null(Assert.Single(plans, row => PlanRevisionOf(row) == runtime.PlanRevision + 1).FencedAt);
    }

    private static long PlanRevisionOf(ProtocolOutboxRow row)
    {
        using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
        return document.RootElement.GetProperty("payload").GetProperty("planRevision").GetInt64();
    }

    /// <summary>
    /// The plan and worklist snapshots the peer was sent, each message once, in the order it was
    /// first sent. Replays repeat a line; only the first send says where it stands in the sequence.
    /// </summary>
    private static (string Type, string MessageId, JsonElement Payload)[] JourneySnapshotsInFirstSentOrder(
        RuntimeFixture fixture) =>
        fixture.Peer.Lines
            .Select(line => JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(line)).RootElement)
            .Where(root => root.GetProperty("messageType").GetString() is
                "UpcomingStopPlanSnapshot" or "CurrentStopWorklistSnapshot")
            .Select(root => (
                Type: root.GetProperty("messageType").GetString()!,
                MessageId: root.GetProperty("messageId").GetString()!,
                Payload: root.GetProperty("payload").Clone()))
            .DistinctBy(item => item.MessageId)
            .ToArray();

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-03")]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task RestartReconcilesUnknownPickupCreateWithoutSecondOrderOrIdentityChange()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        fixture.Riot.LoseNextCreateResponse = true;

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow before = await fixture.RuntimeAsync();
        Assert.Equal("RESULT_UNKNOWN", (await fixture.Context.OrderIntents.SingleAsync(
            TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));

        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow after = await fixture.RuntimeAsync();
        OrderIntentRow reconciled = await fixture.Context.OrderIntents.SingleAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(before.DemandId, after.DemandId);
        Assert.Equal(before.PickupUpperId, after.PickupUpperId);
        Assert.Equal("CONFIRMED", reconciled.Status);
        Assert.Equal("ORDER-TO_PICKUP", reconciled.OrderId);
        Assert.Equal(1, fixture.Riot.CreateCount("TO_PICKUP"));
        Assert.Single(await fixture.Context.VehicleDispatchLeases.ToArrayAsync(
            TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    public async Task AcceptedDemandWithoutPersistedRuntimeFailsClosedInsteadOfBeingAdopted()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        AcceptedDemandSnapshot demand = fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10));
        await new WireToGateStore(fixture.Context).AcceptWithOrderIntentAsync(
            demand,
            new OrderIntent(
                "LEGACY-PICKUP-LEG",
                demand.DemandId,
                "W2G-LEGACY-PICKUP-1",
                "TO_PICKUP",
                "N1-1",
                Now,
                fixture.Options.VehicleKey,
                fixture.Options.MapId,
                12,
                fixture.Options.AgvLifecycleGeneration,
                fixture.Options.DispatchGeneration),
            TestContext.Current.CancellationToken);

        BusinessIdentityConflictException error = await Assert.ThrowsAsync<BusinessIdentityConflictException>(
            () => fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken));

        Assert.Contains("has no production journey runtime", error.Message, StringComparison.Ordinal);
        Assert.Empty(await fixture.Context.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Riot.TotalCreateCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    public async Task RestartReenvelopesPendingServerMessagesForTheCurrentSessionWithoutChangingSemantics()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow initialRuntime = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", initialRuntime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = initialRuntime.PickupStationRiotId };
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        Dictionary<string, string> originalPayloads = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .ToDictionaryAsync(
                row => row.MessageId,
                row => JsonDocument.Parse(row.PayloadJson).RootElement.GetProperty("payload").GetRawText(),
                TestContext.Current.CancellationToken);
        int originalCount = originalPayloads.Count;

        await fixture.AdvanceSessionAsync(2);
        await fixture.RecreateEngineAsync();
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        ProtocolOutboxRow[] replayed = await fixture.Context.ProtocolOutbox.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(originalCount, replayed.Length);
        foreach (ProtocolOutboxRow row in replayed)
        {
            using JsonDocument document = JsonDocument.Parse(row.PayloadJson);
            // A retired row is not pending, so it is neither replayed nor re-enveloped: it keeps the
            // generation it was sent in. The one retired here is the plan sent before the pickup
            // arrival, which the pickup plan superseded (and this fixture's peer never acknowledges).
            long expectedGeneration = row.FencedAt is null ? 2 : 1;
            Assert.Equal(expectedGeneration, document.RootElement.GetProperty("sessionGeneration").GetInt64());
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(originalPayloads[row.MessageId]),
                JsonNode.Parse(document.RootElement.GetProperty("payload").GetRawText())));
        }

        ProtocolOutboxRow retired = Assert.Single(replayed, row => row.FencedAt is not null);
        Assert.Equal("UpcomingStopPlanSnapshot", retired.MessageType);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-03")]
    public async Task UnknownPreDepartureSafetyCannotCreateTheGateOrder()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.AdvanceToDepartureSafetyAsync();
        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "PreDepartureSafetyCheckResult",
            new
            {
                preDepartureSafetyCheckId = runtime.PreDepartureSafetyCheckId,
                outcome = "SAFE",
                observedAt = Now,
                safetyStateVersion = 7,
                validUntil = Now.AddMinutes(1),
                safety = new
                {
                    departureSafe = true,
                    vehicleStopped = true,
                    allTargetSlotsLocked = true,
                    allUnlockOutputsReset = true,
                    unknownPresent = true,
                    reasonCodes = UnknownReasonCodes
                }
            }, runtime.PreDepartureSafetyCheckMessageId);

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingDepartureSafety, runtime.Stage);
        Assert.Equal("PRE_DEPARTURE_SAFETY_NOT_VALID", runtime.BlockReasonCode);
        Assert.DoesNotContain(
            await fixture.Context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken),
            row => row.Purpose == "TO_GATE");
        Assert.Equal(0, fixture.Riot.CreateCount("TO_GATE"));
    }

    /// <summary>
    /// Protocol 2.0.0 item 1: the pickup worklist tells the vehicle when its stop ends, and that is
    /// the deadline the runtime itself ends the stop by -- <see cref="JourneyRuntimeEngine.StationDepartureDeadline"/>,
    /// the single source 8005-agv-control-server#79 made for exactly this.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ThePickupWorklistCarriesTheStationDepartureDeadlineTheRuntimeEndsTheStopBy()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromMinutes(5);
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 7);
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);
        JourneyRuntimeRow runtime = await fixture.RuntimeAsync();
        fixture.Riot.SetSuccessfulArrival("TO_PICKUP", runtime.PickupStationRiotId);
        fixture.Riot.Vehicle = fixture.Riot.Vehicle with { CurrentStationId = runtime.PickupStationRiotId };
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        // 这三十秒里车一直在说话，每两秒一次（ADR-cross-0027）。夹具只推时钟不补心跳，是把「时间过去了」
        // 写成了「车不见了」——control-server#234 起，静默过了存活窗的车会被判失联，这一轮就不再推进。
        // 补这一条之前，这个判据还贴着 MaximumEvidenceAge 的三十秒墙：正好不超出，多一毫秒就假红。
        await fixture.HearFromPeerAsync();
        DateTimeOffset arrivedAt = fixture.Clock.GetUtcNow();

        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        JsonElement deadline = (await fixture.OutboxPayloadAsync(runtime.WorklistMessageId))
            .GetProperty("stationDepartureDeadlineAt");
        Assert.Equal(JsonValueKind.String, deadline.ValueKind);
        Assert.Equal(
            JourneyRuntimeEngine.StationDepartureDeadline(runtime, fixture.Options.StationDepartureWaitTimeout),
            deadline.GetDateTimeOffset());
        Assert.Equal(arrivedAt + TimeSpan.FromMinutes(5), deadline.GetDateTimeOffset());
    }

    /// <summary>
    /// The drop-off stop has no departure wait, so its worklist carries no deadline: a countdown there
    /// would be one the runtime never acts on.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-04")]
    public async Task TheDropoffWorklistCarriesNoStationDepartureDeadline()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        JourneyRuntimeRow runtime = await fixture.RunToGateUnloadAsync();

        JsonElement payload = await fixture.OutboxPayloadAsync(runtime.GateWorklistMessageId);
        Assert.Equal("DROPOFF", payload.GetProperty("items")[0].GetProperty("stopRole").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("stationDepartureDeadlineAt").ValueKind);
    }

    /// <summary>
    /// With the station departure wait switched off (<c>StationDepartureWaitTimeout</c> zero) there is
    /// no deadline to tell, even at the pickup.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task WithTheStationDepartureWaitSwitchedOffThePickupWorklistCarriesNoDeadline()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Options.StationDepartureWaitTimeout = TimeSpan.Zero;
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        JourneyRuntimeRow runtime = await fixture.AdvanceToSublotWaitAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, runtime.Stage);
        JsonElement payload = await fixture.OutboxPayloadAsync(runtime.WorklistMessageId);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("stationDepartureDeadlineAt").ValueKind);
    }

    /// <summary>
    /// Protocol 2.0.0 item 2: the entry request gives the vehicle the dispatch scope's sublots and
    /// names no demand. One demand per journey makes that scope this demand's sublot.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task TheSublotEntryRequestNamesTheDispatchScopeAndNoDemand()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        JourneyRuntimeRow runtime = await fixture.AdvanceToSublotWaitAsync();

        JsonElement payload = await fixture.OutboxPayloadAsync(runtime.SublotRequestMessageId);
        Assert.Equal(
            ["SUBLOT-001"],
            payload.GetProperty("expectedSublots").EnumerateArray().Select(item => item.GetString()));
        Assert.False(payload.TryGetProperty("demandId", out _));
        Assert.False(payload.TryGetProperty("expectedSublot", out _));
        Assert.Equal(runtime.OperationSessionId, payload.GetProperty("operationSessionId").GetString());
        Assert.Equal(runtime.WorklistRevision, payload.GetProperty("worklistRevision").GetInt64());
    }

    /// <summary>
    /// Protocol 2.0.0 item 2, the other direction: <c>SublotSubmitted</c> no longer carries a demand,
    /// and a submission without one is still matched and commands the load as before.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("ProtocolVector", "CV-PICKUP-SUBLOT-LOAD")]
    public async Task ASubmissionThatNamesNoDemandIsMatchedAndCommandsTheLoad()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        JourneyRuntimeRow runtime = await fixture.AdvanceToLoadResultAsync();

        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);
        ProtocolInboxRow submission = await fixture.Context.ProtocolInbox.AsNoTracking().SingleAsync(
            row => row.MessageId == runtime.ConsumedSublotMessageId, TestContext.Current.CancellationToken);
        using (JsonDocument submitted = JsonDocument.Parse(submission.RequestJson))
        {
            Assert.False(submitted.RootElement.GetProperty("payload").TryGetProperty("demandId", out _));
        }
        JsonElement command = await fixture.OutboxPayloadAsync(runtime.LoadCommandMessageId);
        Assert.Equal(runtime.DemandId, command.GetProperty("demandId").GetString());
        Assert.Equal(runtime.LoadSlotOperationAttemptId, command.GetProperty("slotOperationAttemptId").GetString());
    }

    /// <summary>
    /// <c>SublotSubmitted</c>'s <c>businessDedupKeys</c> is empty in the 2.0.0 candidate, so nothing in
    /// the protocol stops a second submission of the same entry -- a rescan under a new messageId,
    /// even by another entry method -- and "one entry, one side effect" is this server's to keep. The
    /// submission a load was commanded for is consumed; a later one commands nothing, opens no second
    /// attempt and does not move the journey.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ARescanUnderANewMessageIdCommandsNoSecondLoad()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);
        JourneyRuntimeRow runtime = await fixture.AdvanceToLoadResultAsync();
        string consumed = runtime.ConsumedSublotMessageId!;

        await fixture.AddInboxAsync(
            Guid.NewGuid().ToString("D"),
            "SublotSubmitted",
            new
            {
                operationSessionId = runtime.OperationSessionId,
                stationId = runtime.PickupStationId,
                worklistRevision = runtime.WorklistRevision,
                sublot = "SUBLOT-001",
                entryMethod = "KEYBOARD",
                @operator = new
                {
                    operatorId = "OP-001",
                    verificationMethod = "BADGE",
                    verifiedAt = fixture.Clock.GetUtcNow()
                }
            });
        await fixture.Engine.ExecuteOnceAsync(TestContext.Current.CancellationToken);

        runtime = await fixture.RuntimeAsync();
        Assert.Equal(JourneyRuntimeStage.AwaitingLoadResult, runtime.Stage);
        Assert.Equal(consumed, runtime.ConsumedSublotMessageId);
        Assert.Single(await fixture.OutboxTypesAsync(), type => type == "SlotOperationCommand");
        Assert.Single(await fixture.Context.StationOperations.AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The contract the rescan test above relies on, read from the vendored manifest rather than
    /// assumed: no business deduplication for a submission or a rejection, and the entry request
    /// deduplicated by <c>(operationSessionId, worklistRevision)</c> -- which is why this server keeps
    /// <c>expectedSublots</c> fixed within one worklist revision.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public void TheCandidateLeavesSublotSubmissionsAndRejectionsWithoutBusinessDeduplication()
    {
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllBytes(ProtocolIdentityArchitectureTests.ManifestPath()));
        JsonElement messages = manifest.RootElement.GetProperty("messages");

        Assert.Empty(messages.GetProperty("SublotSubmitted").GetProperty("businessDedupKeys").EnumerateArray());
        Assert.Empty(messages.GetProperty("SublotRejected").GetProperty("businessDedupKeys").EnumerateArray());
        Assert.Equal(
            ["operationSessionId", "worklistRevision"],
            messages.GetProperty("SublotEntryRequested").GetProperty("businessDedupKeys")
                .EnumerateArray().Select(key => key.GetString()));
    }

    /// <summary>
    /// Protocol 2.0.0 items 5 and 6 on the two snapshots a journey publishes: <c>LOADING</c> at the
    /// pickup, <c>CLOSED</c> / <c>PLANNED_LOADING_COMPLETE</c> at the drop-off once the load batch has
    /// closed, never null while the journey exists; and <c>NOT_CHARGING</c> in both, because v2 has
    /// no automatic charging to be in a cycle of (8005-agv-program#94, commit <c>db5a1d14</c>).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    public async Task EachVehicleBusinessStateOfAJourneyCarriesItsLoadingPhaseAndNoChargingCycle()
    {
        await using RuntimeFixture fixture = await RuntimeFixture.CreateAsync();
        fixture.Catalog.Set(fixture.Demand(
            "10000000-0000-4000-8000-000000000001", "SUBLOT-001", Now.AddMinutes(-10)));
        fixture.BoxCounts.Set("SUBLOT-001", 4);

        JourneyRuntimeRow runtime = await fixture.RunToGateUnloadAsync();

        JsonElement pickup = await fixture.OutboxPayloadAsync(runtime.VehicleBusinessMessageId);
        JsonElement dropoff = await fixture.OutboxPayloadAsync(runtime.GateVehicleBusinessMessageId);
        foreach (JsonElement snapshot in new[] { pickup, dropoff })
        {
            Assert.Equal("TRANSPORT", snapshot.GetProperty("activePurpose").GetString());
            Assert.Equal("NOT_CHARGING", snapshot.GetProperty("chargingCycleState").GetString());
            Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("loadingPhase")
                .GetProperty("cargoHoldingDeadlineAt").ValueKind);
        }
        Assert.Equal("LOADING", pickup.GetProperty("loadingPhase").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, pickup.GetProperty("loadingPhase").GetProperty("closedReason").ValueKind);
        Assert.Equal("CLOSED", dropoff.GetProperty("loadingPhase").GetProperty("state").GetString());
        Assert.Equal(
            "PLANNED_LOADING_COMPLETE",
            dropoff.GetProperty("loadingPhase").GetProperty("closedReason").GetString());
    }

    public static TheoryData<JourneyRuntimeStage, bool, string> LoadingPhaseOfEveryStage()
    {
        TheoryData<JourneyRuntimeStage, bool, string> data = [];
        foreach (JourneyRuntimeStage stage in Enum.GetValues<JourneyRuntimeStage>())
        {
            foreach (bool loadBatchClosed in new[] { false, true })
            {
                bool closed = stage switch
                {
                    JourneyRuntimeStage.AwaitingPickupArrival or
                    JourneyRuntimeStage.AwaitingSublot or
                    JourneyRuntimeStage.AwaitingLoadResult => false,
                    JourneyRuntimeStage.Blocked or JourneyRuntimeStage.Completed => loadBatchClosed,
                    _ => true
                };
                data.Add(stage, loadBatchClosed, closed ? "CLOSED/PLANNED_LOADING_COMPLETE" : "LOADING/");
            }
        }
        return data;
    }

    /// <summary>
    /// The one mapping from journey stage to loading phase, over every stage there is: none maps to
    /// null, the stages before the load batch closes are <c>LOADING</c>, the ones after are
    /// <c>CLOSED</c>, and the two a journey reaches from either side follow the load.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-02")]
    [MemberData(nameof(LoadingPhaseOfEveryStage))]
    public void EveryJourneyStageMapsToALoadingPhase(JourneyRuntimeStage stage, bool loadBatchClosed, string expected)
    {
        LoadingPhaseProjection phase = JourneyRuntimeEngine.LoadingPhase(stage, loadBatchClosed);

        Assert.NotNull(phase);
        Assert.Null(phase.CargoHoldingDeadlineAt);
        Assert.Equal(expected, $"{phase.State}/{phase.ClosedReason}");
    }
}
