using ControlServer.Domain;

namespace ControlServer.Application;

public interface IMesIngestCatalog
{
    Task<DemandCatalogSnapshot> ReadCatalogAsync(CancellationToken cancellationToken);

    Task<AcceptedDemandSnapshot?> ReadCurrentAsync(string demandId, CancellationToken cancellationToken);
}

public interface IRiotMovementGateway
{
    Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken);

    Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken);
}

public interface IRiotVehicleFacts : IRiotMovementGateway
{
    Task<RiotVehicleObservation> ReadVehicleAsync(string vehicleKey, CancellationToken cancellationToken);
}

public interface ISublotBoxCountReader
{
    Task<int?> ReadMaxBoxCountAsync(string sublot, CancellationToken cancellationToken);
}

public interface IOnboardPeer
{
    Task SendAsync(ReadOnlyMemory<byte> ndjsonLine, CancellationToken cancellationToken);
}

public interface IDemandAcceptanceStore
{
    Task AcceptWithOrderIntentAsync(
        AcceptedDemandSnapshot snapshot,
        OrderIntent orderIntent,
        CancellationToken cancellationToken);
}

public interface IJourneyAcceptanceStore : IDemandAcceptanceStore
{
    Task AcceptWithOrderIntentAsync(
        AcceptedDemandSnapshot snapshot,
        OrderIntent orderIntent,
        JourneyExecutionPlan journey,
        CancellationToken cancellationToken);
}

public interface IMovementIntentStore
{
    Task<StoredMovementIntent?> GetByUpperIdAsync(string upperId, CancellationToken cancellationToken);

    Task MarkCreateAttemptedAsync(string upperId, CancellationToken cancellationToken);

    Task MarkResultUnknownAsync(string upperId, CancellationToken cancellationToken);

    Task MarkTerminalReconciliationRequiredAsync(
        string upperId,
        string orderId,
        CancellationToken cancellationToken);

    Task ConfirmAsync(string upperId, string orderId, CancellationToken cancellationToken);
}

public enum RiotOrderObservationKind
{
    NotFound,
    Active,
    Terminal,
    Unknown
}

public sealed record RiotOrderObservation(
    string UpperId,
    RiotOrderObservationKind Kind,
    string? OrderId,
    int? OrderState = null,
    string? VehicleKey = null,
    int? MapId = null,
    int? DestinationStationId = null);

public sealed record RiotVehicleObservation(
    string VehicleKey,
    bool Connected,
    bool Enabled,
    string ProcState,
    string CurrentMap,
    int? CurrentStationId,
    int? BatteryPercent,
    string? BatteryState,
    double? Speed,
    DateTimeOffset ObservedAt,
    int? LockStatus = null,
    string? OrderTaskId = null);

public sealed record StoredMovementIntent(OrderIntent Intent, string Status, string? OrderId);
