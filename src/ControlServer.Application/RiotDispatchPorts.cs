using ControlServer.Domain;

namespace ControlServer.Application;

public interface IRiotMovementGateway
{
    Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken);

    Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken);
}

public sealed record ExperimentalRiotCreateAuthorization(
    string AuthorizationId,
    int AuthorizationVersion,
    string UpperId,
    string DemandId,
    string MovementLegId,
    long AgvLifecycleGeneration,
    long DispatchGeneration,
    DateTimeOffset ExpiresAt);

public interface IExperimentalRiotCreateAuthorizationSource
{
    Task<ExperimentalRiotCreateAuthorization?> GetAuthorizationAsync(
        string upperId,
        CancellationToken cancellationToken);
}

public interface IRiotVehicleFacts : IRiotMovementGateway
{
    Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken);
}

public interface IRiotMapStationCatalog
{
    Task<RiotMapStationCatalogSnapshot> ReadMapStationsAsync(
        int mapId,
        CancellationToken cancellationToken);
}

public interface IRiotVehicleSafetyFacts
{
    Task<RiotVehicleSafetyObservation> ReadVehicleSafetyAsync(
        string vehicleKey,
        CancellationToken cancellationToken);
}
