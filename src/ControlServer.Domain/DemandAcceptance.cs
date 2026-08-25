namespace ControlServer.Domain;

public sealed record AcceptedDemandSnapshot(
    string DemandId,
    string TransportDemandKey,
    long DemandRevision,
    string HistoryEpoch,
    long CatalogRevision,
    DateTimeOffset AcceptedAt);

public sealed record OrderIntent(
    string MovementLegId,
    string DemandId,
    string UpperId,
    string Purpose,
    string TargetStationId,
    DateTimeOffset CreatedAt,
    string VehicleKey = "",
    int MapId = 0,
    int DestinationStationId = 0,
    long AgvLifecycleGeneration = 0,
    long DispatchGeneration = 0);
