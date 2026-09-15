using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

/// <summary>
/// 需求在分配目标仓位时冻结所用的分区归属表版本，之后的版本变化不改变已冻结的需求（REQ-0350）。
/// </summary>
public sealed class DemandAreaAssignmentFreezeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreezingTheSameVersionAgainReturnsTheFirstFreezeUnchanged()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        (AreaAssignmentTableVersion first, _) = await WriteTwoVersionsAsync(fixture);

        DemandAreaAssignmentFreeze frozen = await fixture.Freezes.FreezeAsync(
            "D-1", 1, Now, TestContext.Current.CancellationToken);
        DemandAreaAssignmentFreeze again = await fixture.Freezes.FreezeAsync(
            "D-1", 1, Now.AddMinutes(3), TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        DemandAreaAssignmentFreeze expected = new("D-1", 1, first.SnapshotId, Now);
        Assert.Equal(expected, frozen);
        Assert.Equal(expected, again);
        Assert.Equal(expected, await fixture.Freezes.ReadAsync("D-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FreezingADifferentVersionForAnAlreadyFrozenDemandIsRefusedAndTheFirstFreezeStands()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        (AreaAssignmentTableVersion first, _) = await WriteTwoVersionsAsync(fixture);
        await fixture.Freezes.FreezeAsync("D-1", 1, Now, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DemandAreaAssignmentFreezeConflictException>(() =>
            fixture.Freezes.FreezeAsync("D-1", 2, Now.AddMinutes(1), TestContext.Current.CancellationToken));
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(
            new DemandAreaAssignmentFreeze("D-1", 1, first.SnapshotId, Now),
            await fixture.Freezes.ReadAsync("D-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AVersionThatWasNeverWrittenCannotBeFrozenAndAnUnfrozenDemandReadsAsNothing()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        await WriteTwoVersionsAsync(fixture);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Freezes.FreezeAsync("D-1", 3, Now, TestContext.Current.CancellationToken));

        Assert.Null(await fixture.Freezes.ReadAsync("D-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheFreezeIsOneRowPerDemandInTheBatch3ConsumerBindingTable()
    {
        // The storage decision of control-server#66: no column on JourneyRuntimes (batch 7 rekeys that
        // table), the consumer-binding table batch 3 built for exactly this instead.
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        (_, AreaAssignmentTableVersion second) = await WriteTwoVersionsAsync(fixture);
        await fixture.Freezes.FreezeAsync("D-1", 2, Now, TestContext.Current.CancellationToken);
        await fixture.Freezes.FreezeAsync("D-1", 2, Now.AddMinutes(1), TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        ConfigurationConsumerBindingRow binding = await fixture.Context.Set<ConfigurationConsumerBindingRow>()
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DispatchZoneAreaAssignmentGovernance.DemandConsumerKind, binding.ConsumerKind);
        Assert.Equal("D-1", binding.ConsumerId);
        Assert.Equal(GovernedObjectKind.DispatchZoneAreaAssignment, binding.ObjectKind);
        Assert.Equal(DispatchZoneAreaAssignmentGovernance.ObjectId, binding.ObjectId);
        Assert.Equal(2, binding.FrozenVersion);
        Assert.Equal(second.SnapshotId, binding.SnapshotId);
        Assert.Equal(Now, binding.FrozenAt);
    }

    [Fact]
    public async Task TheInFlightListLeavesOutOnlyDemandsWhoseJourneyHasCompleted()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        await WriteTwoVersionsAsync(fixture);
        fixture.Context.Set<JourneyRuntimeRow>().AddRange(
            Runtime("D-COMPLETED", JourneyRuntimeStage.Completed),
            Runtime("D-TRAVELLING", JourneyRuntimeStage.AwaitingGateArrival),
            Runtime("D-BLOCKED", JourneyRuntimeStage.Blocked));
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await fixture.Freezes.FreezeAsync("D-COMPLETED", 1, Now, TestContext.Current.CancellationToken);
        await fixture.Freezes.FreezeAsync("D-TRAVELLING", 1, Now, TestContext.Current.CancellationToken);
        await fixture.Freezes.FreezeAsync("D-BLOCKED", 2, Now, TestContext.Current.CancellationToken);
        // Frozen before its journey row exists: still in flight, because leaving one out of an import
        // preview is worse than listing one too many.
        await fixture.Freezes.FreezeAsync("D-NO-JOURNEY-YET", 2, Now, TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        IReadOnlyList<DemandAreaAssignmentFreeze> inFlight =
            await fixture.Freezes.ListInFlightAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [("D-BLOCKED", 2L), ("D-NO-JOURNEY-YET", 2L), ("D-TRAVELLING", 1L)],
            inFlight.Select(freeze => (freeze.DemandId, freeze.Version)));
    }

    private static async Task<(AreaAssignmentTableVersion First, AreaAssignmentTableVersion Second)>
        WriteTwoVersionsAsync(AreaAssignmentPersistenceFixture fixture)
    {
        AreaAssignmentTableVersion first = await fixture.AreaAssignments.WriteVersionAsync(
            [new("N01", "MAP-25-WIRE_TO_GATE", "FRONT")], Now.AddDays(-2), TestContext.Current.CancellationToken);
        AreaAssignmentTableVersion second = await fixture.AreaAssignments.WriteVersionAsync(
            [new("N01", "MAP-25-WIRE_TO_GATE", "REAR")], Now.AddDays(-1), TestContext.Current.CancellationToken);
        return (first, second);
    }

    private static JourneyRuntimeRow Runtime(string demandId, JourneyRuntimeStage stage) => new()
    {
        DemandId = demandId,
        Stage = stage,
        AgvId = "AGV-01",
        VehicleKey = "VEHICLE-001",
        AgvLifecycleGeneration = 1,
        MapId = 25,
        MapIdentity = "MAP-25",
        DispatchZone = "MAP-25-WIRE_TO_GATE",
        RouteEvidenceId = "ROUTE-01",
        PickupStationId = "PICKUP",
        PickupStationRiotId = 11,
        GateStationId = "GATE",
        GateStationRiotId = 22,
        ExpectedBasketCount = 2,
        TargetSlotsJson = "[1,2]",
        OperationSessionId = $"session-{demandId}",
        PickupMovementLegId = $"pickup-leg-{demandId}",
        PickupUpperId = $"UPPER-PICKUP-{demandId}",
        GateMovementLegId = $"gate-leg-{demandId}",
        GateUpperId = $"UPPER-GATE-{demandId}",
        DispatchGeneration = 1,
        VehicleBusinessRevision = 1,
        WorklistRevision = 1,
        PlanRevision = 1,
        VehicleBusinessMessageId = $"vb-{demandId}",
        WorklistMessageId = $"wl-{demandId}",
        PlanMessageId = $"plan-{demandId}",
        SublotRequestMessageId = $"sublot-{demandId}",
        LoadCommandMessageId = $"load-{demandId}",
        LoadSlotOperationAttemptId = $"load-attempt-{demandId}",
        PreDepartureSafetyCheckMessageId = $"safety-msg-{demandId}",
        PreDepartureSafetyCheckId = $"safety-{demandId}",
        GateVehicleBusinessMessageId = $"gate-vb-{demandId}",
        GateWorklistMessageId = $"gate-wl-{demandId}",
        GatePlanMessageId = $"gate-plan-{demandId}",
        UnloadCommandMessageId = $"unload-{demandId}",
        UnloadSlotOperationAttemptId = $"unload-attempt-{demandId}",
        CreatedAt = Now.AddMinutes(-8),
        UpdatedAt = Now
    };
}
