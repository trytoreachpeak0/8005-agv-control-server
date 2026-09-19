using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Composition;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Tests;

/// <summary>
/// 批次 7 建表票（control-server#206）给后续票的五个持久化端口：批次7-02～7-12 经它们读写，不再各自碰表。
/// </summary>
public sealed class Batch7PersistencePortTests
{
    private static readonly DateTimeOffset Now = Batch7JourneyFixture.Now;

    [Fact]
    public async Task EveryBatch7PortIsRegisteredWithTheMultiDemandJourneyModuleSoNoLaterTicketEditsIt()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        ServiceCollection services = new();
        services.AddDbContext<ControlServerDbContext>(options => options.UseSqlite(connection));
        services.AddGovernance(new ConfigurationBuilder().Build());
        services.AddMultiDemandJourneys();
        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        Assert.IsType<JourneyMembershipStore>(scope.ServiceProvider.GetRequiredService<IJourneyMembershipStore>());
        Assert.IsType<VehiclePurposeClaimStore>(scope.ServiceProvider.GetRequiredService<IVehiclePurposeClaimStore>());
        Assert.IsType<TransportDemandSuppressionStore>(
            scope.ServiceProvider.GetRequiredService<ITransportDemandSuppressionStore>());
        Assert.IsType<DispatchZoneParameterStore>(scope.ServiceProvider.GetRequiredService<IDispatchZoneParameterStore>());
        Assert.IsType<VehicleSnapshotRevisionStore>(
            scope.ServiceProvider.GetRequiredService<IVehicleSnapshotRevisionStore>());
    }

    [Fact]
    public async Task APurposeClaimIsHeldByWhoeverInsertsFirstAndOnlyItsOwnJourneyReleasesIt()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        VehiclePurposeClaimStore first = new(fixture.Context);
        VehiclePurposeClaimStore second = new(fixture.NewContext());

        Assert.True(await first.TryClaimAsync(Claim("journey:D-1"), cancellationToken));
        Assert.True(await first.TryClaimAsync(Claim("journey:D-1"), cancellationToken));
        Assert.False(await second.TryClaimAsync(Claim("journey:D-2"), cancellationToken));
        await second.ReleaseAsync("VK-01", "journey:D-2", cancellationToken);
        Assert.Equal(Claim("journey:D-1"), await second.ReadAsync("VK-01", cancellationToken));

        await first.ReleaseAsync("VK-01", "journey:D-1", cancellationToken);
        Assert.Null(await second.ReadAsync("VK-01", cancellationToken));
        Assert.True(await second.TryClaimAsync(Claim("journey:D-2"), cancellationToken));
        Assert.Equal(Claim("journey:D-2"), await first.ReadAsync("VK-01", cancellationToken));
    }

    [Fact]
    public async Task TheFirstSuppressionOfABusinessKeyStandsAndALaterOneNeitherReplacesItNorThrows()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        TransportDemandSuppressionStore store = new(fixture.Context);
        TransportDemandSuppression first = new("SUBLOT-1|WIRE_TO_GATE", "D-1", "TERMINATED_BY_FAULT_CARGO_HANDOFF", Now);
        TransportDemandSuppression later = new("SUBLOT-1|WIRE_TO_GATE", "D-2", "CANCELLED_BY_OPERATOR", Now.AddMinutes(1));

        Assert.Null(await store.ReadAsync(first.TransportDemandKey, cancellationToken));
        Assert.Equal(first, await store.SuppressIfAbsentAsync(first, cancellationToken));
        Assert.Equal(first, await new TransportDemandSuppressionStore(fixture.NewContext())
            .SuppressIfAbsentAsync(later, cancellationToken));
        Assert.Equal(first, await store.SuppressIfAbsentAsync(later, cancellationToken));

        Assert.Equal(first, await new TransportDemandSuppressionStore(fixture.NewContext())
            .ReadAsync(first.TransportDemandKey, cancellationToken));
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "TransportDemandSuppressions"));
    }

    [Fact]
    public async Task TheRevisionCounterMovesForwardOnEveryStreamAndRefusesToStepBackOnAny()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        VehicleSnapshotRevisionStore store = new(fixture.Context);

        Assert.Null(await store.ReadAsync("agv-01", cancellationToken));
        await store.AdvanceAsync(new VehicleSnapshotRevisions("agv-01", 1, 1, 1), cancellationToken);
        await store.AdvanceAsync(new VehicleSnapshotRevisions("agv-01", 3, 3, 4), cancellationToken);
        await store.AdvanceAsync(new VehicleSnapshotRevisions("agv-01", 3, 3, 4), cancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AdvanceAsync(new VehicleSnapshotRevisions("agv-01", 5, 2, 7), cancellationToken));

        Assert.Equal(
            new VehicleSnapshotRevisions("agv-01", 3, 3, 4),
            await new VehicleSnapshotRevisionStore(fixture.NewContext()).ReadAsync("agv-01", cancellationToken));
        Assert.Null(await store.ReadAsync("agv-02", cancellationToken));
    }

    [Fact]
    public async Task ZoneParametersAreWrittenAsGovernedVersionsThatKeepZeroApartFromUnconfiguredAndCannotBeRewritten()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DispatchZoneParameterStore store = ZoneParameterStore(fixture.Context);
        Assert.Null(await store.ReadCurrentAsync(cancellationToken));

        DispatchZoneParameterTableVersion first = await store.WriteVersionAsync(
            [
                new DispatchZoneParameters("ZONE-C", 20000, 300),
                new DispatchZoneParameters("ZONE-A", 0, null),
                new DispatchZoneParameters("ZONE-B", null, 600),
            ],
            Now,
            cancellationToken);
        DispatchZoneParameterTableVersion second = await store.WriteVersionAsync(
            [new DispatchZoneParameters("ZONE-A", null, null)], Now.AddMinutes(1), cancellationToken);

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.Equal(DispatchZoneParameterSources.GovernedImport, first.Source);
        Assert.NotNull(first.SnapshotId);
        DispatchZoneParameterStore reader = ZoneParameterStore(fixture.NewContext());
        DispatchZoneParameterTableVersion? current = await reader.ReadCurrentAsync(cancellationToken);
        Assert.Equal(2, current?.Version);
        DispatchZoneParameterTableVersion? readBack = await reader.ReadVersionAsync(1, cancellationToken);
        Assert.NotNull(readBack);
        Assert.Equal(first.ContentSha256, readBack.ContentSha256);
        Assert.Equal(
            ["ZONE-A 0 -", "ZONE-B - 600", "ZONE-C 20000 300"],
            readBack.Zones.Values.OrderBy(zone => zone.DispatchZone, StringComparer.Ordinal)
                .Select(zone => $"{zone.DispatchZone} {zone.EnRouteAdditionMaxPathCostIncrease?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} {zone.StarvationThresholdSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}"));
        Assert.Equal(
            [GovernedObjectKind.DispatchZoneParameters, GovernedObjectKind.DispatchZoneParameters],
            await fixture.NewContext().Set<GovernedConfigurationSnapshotRow>().AsNoTracking()
                .Select(row => row.ObjectKind).ToArrayAsync(cancellationToken));

        await using ControlServerDbContext writer = fixture.NewContext();
        DispatchZoneParameterRow published = await writer.Set<DispatchZoneParameterRow>()
            .SingleAsync(row => row.Version == 1 && row.DispatchZone == "ZONE-A", cancellationToken);
        published.EnRouteAdditionMaxPathCostIncrease = 5;
        await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() => writer.SaveChangesAsync(cancellationToken));
        writer.ChangeTracker.Clear();
        writer.Set<DispatchZoneParameterVersionRow>().Remove(
            await writer.Set<DispatchZoneParameterVersionRow>().SingleAsync(row => row.Version == 1, cancellationToken));
        await Assert.ThrowsAsync<PublishedVersionImmutabilityException>(() => writer.SaveChangesAsync(cancellationToken));
    }

    [Theory]
    [InlineData("ZONE-A", "ZONE-A", 1L, 1L)]
    [InlineData("ZONE-A", "ZONE-B", -1L, 1L)]
    [InlineData("ZONE-A", "ZONE-B", 1L, -1L)]
    [InlineData("ZONE-A", " ", 1L, 1L)]
    public async Task AZoneParameterTableThatNamesAZoneTwiceOrCarriesANegativeValueIsRefusedWhole(
        string firstZone, string secondZone, long increase, long threshold)
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        DispatchZoneParameterStore store = ZoneParameterStore(fixture.Context);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.WriteVersionAsync(
            [new DispatchZoneParameters(firstZone, 1, 1), new DispatchZoneParameters(secondZone, increase, threshold)],
            Now,
            TestContext.Current.CancellationToken));
        Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "DispatchZoneParameterVersions"));
    }

    private static DispatchZoneParameterStore ZoneParameterStore(ControlServerDbContext context)
    {
        GovernanceStore governance = new(
            context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);
        return new DispatchZoneParameterStore(context, new GovernedConfigurationPublisher(governance, governance));
    }

    [Fact]
    public async Task StopsAndDemandMembershipsAreReadAndWrittenThroughTheirPortAndADemandBelongsToOneJourneyAtATime()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-731", "agv-01", "VK-01", Now);
        JourneyMembershipStore store = new(fixture.NewContext());
        Assert.Equal("journey:D-731", await store.FindJourneyIdByDemandAsync("D-731", cancellationToken));
        Assert.Null(await store.FindJourneyIdByDemandAsync("D-732", cancellationToken));

        // A second demand joins en route: its own pickup stop goes in before the unload stop.
        await store.AddStopAsync(
            new JourneyStop(
                "stop:D-732|PICKUP", "journey:D-731", 3, JourneyStopRoles.Pickup, "ST-PICKUP-2", 103, "MAP-25-WIRE_TO_GATE",
                "session-2", "leg-2", "W2G-D-732-PICKUP-1", "vb-2", "wl-2", "plan-2", "sublot-2", "safety-msg-2", "safety-2",
                JourneyStopStatuses.Pending, Now.AddMinutes(1)),
            cancellationToken);
        await store.SetStopSequenceAsync("journey:D-731|UNLOAD", 4, cancellationToken);
        await store.SetStopSequenceAsync("stop:D-732|PICKUP", 2, cancellationToken);
        await store.SetStopStatusAsync("journey:D-731|PICKUP", JourneyStopStatuses.Completed, cancellationToken);
        await store.AddDemandAsync(Demand("journey:D-731", "D-732"), cancellationToken);

        JourneyMembershipStore reader = new(fixture.NewContext());
        Assert.Equal(
            ["1 journey:D-731|PICKUP COMPLETED", "2 stop:D-732|PICKUP PENDING", "4 journey:D-731|UNLOAD PENDING"],
            (await reader.ListStopsAsync("journey:D-731", cancellationToken))
                .Select(stop => $"{stop.Sequence} {stop.StopId} {stop.Status}"));
        Assert.Equal(
            ["D-731 [1,2]", "D-732 [3]"],
            (await reader.ListDemandsAsync("journey:D-731", includeRemoved: false, cancellationToken))
                .OrderBy(demand => demand.DemandId, StringComparer.Ordinal)
                .Select(demand => $"{demand.DemandId} [{string.Join(",", demand.TargetSlots)}]"));
        Assert.Equal("journey:D-731", await reader.FindJourneyIdByDemandAsync("D-732", cancellationToken));

        // While it belongs here it cannot belong anywhere else; once removed it can, and the removal is kept.
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            new JourneyMembershipStore(fixture.NewContext()).AddDemandAsync(Demand("journey:D-799", "D-732"), cancellationToken));
        await store.RemoveDemandAsync("journey:D-731", "D-732", "RELEASED_FOR_REDISPATCH", Now.AddMinutes(2), cancellationToken);
        await store.RemoveDemandAsync("journey:D-731", "D-732", "SOMETHING_LATER", Now.AddMinutes(3), cancellationToken);
        Assert.Null(await reader.FindJourneyIdByDemandAsync("D-732", cancellationToken));
        await new JourneyMembershipStore(fixture.NewContext()).AddDemandAsync(Demand("journey:D-799", "D-732"), cancellationToken);
        Assert.Equal("journey:D-799", await reader.FindJourneyIdByDemandAsync("D-732", cancellationToken));
        JourneyDemand removed = Assert.Single(
            await reader.ListDemandsAsync("journey:D-731", includeRemoved: true, cancellationToken),
            demand => demand.DemandId == "D-732");
        Assert.Equal((Now.AddMinutes(2), "RELEASED_FOR_REDISPATCH"), (removed.RemovedAt, removed.RemovalReason));
        Assert.Single(await reader.ListDemandsAsync("journey:D-731", includeRemoved: false, cancellationToken));
    }

    private static JourneyDemand Demand(string journeyId, string demandId) =>
        new(journeyId, demandId, $"stop:{demandId}|PICKUP", $"{journeyId}|UNLOAD", 1, [3], null,
            $"load-attempt-{demandId}", $"load-command-{demandId}", $"unload-attempt-{demandId}", $"unload-command-{demandId}",
            "MAP-25-WIRE_TO_GATE", 1, JourneyDemandStatuses.PendingLoad, Now.AddMinutes(1), null, null, 7);

    private static VehiclePurposeClaim Claim(string journeyId) =>
        new("VK-01", VehiclePurposes.Transport, journeyId, Now);
}
