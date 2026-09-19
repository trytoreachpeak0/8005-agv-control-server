using ControlServer.Application;

namespace ControlServer.Infrastructure.Persistence;

// Stubs so the batch 7 tests compile; control-server#206's implementation commit fills them in.

public sealed class JourneyMembershipStore(ControlServerDbContext context) : IJourneyMembershipStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public Task<string?> FindJourneyIdByDemandAsync(string demandId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<JourneyStop>> ListStopsAsync(string journeyId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<JourneyDemand>> ListDemandsAsync(
        string journeyId, bool includeRemoved, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task AddStopAsync(JourneyStop journeyStop, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task AddDemandAsync(JourneyDemand demand, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task SetStopStatusAsync(string stopId, string status, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task SetStopSequenceAsync(string stopId, int sequence, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task RemoveDemandAsync(
        string journeyId, string demandId, string removalReason, DateTimeOffset removedAt,
        CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class VehiclePurposeClaimStore(ControlServerDbContext context) : IVehiclePurposeClaimStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public Task<VehiclePurposeClaim?> ReadAsync(string vehicleKey, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<bool> TryClaimAsync(VehiclePurposeClaim claim, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task ReleaseAsync(string vehicleKey, string journeyId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class TransportDemandSuppressionStore(ControlServerDbContext context) : ITransportDemandSuppressionStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public Task<TransportDemandSuppression> SuppressIfAbsentAsync(
        TransportDemandSuppression suppression, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<TransportDemandSuppression?> ReadAsync(string transportDemandKey, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class DispatchZoneParameterStore(
    ControlServerDbContext context,
    GovernedConfigurationPublisher publisher) : IDispatchZoneParameterStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly GovernedConfigurationPublisher _publisher =
        publisher ?? throw new ArgumentNullException(nameof(publisher));

    public Task<DispatchZoneParameterTableVersion> WriteVersionAsync(
        IReadOnlyList<DispatchZoneParameters> zones, DateTimeOffset loadedAt, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<DispatchZoneParameterTableVersion?> ReadCurrentAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<DispatchZoneParameterTableVersion?> ReadVersionAsync(long version, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}

public sealed class VehicleSnapshotRevisionStore(ControlServerDbContext context) : IVehicleSnapshotRevisionStore
{
    private readonly ControlServerDbContext _context = context ?? throw new ArgumentNullException(nameof(context));

    public Task<VehicleSnapshotRevisions?> ReadAsync(string agvId, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task AdvanceAsync(VehicleSnapshotRevisions revisions, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
