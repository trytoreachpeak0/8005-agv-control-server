namespace ControlServer.Domain;

public sealed record LiveMesFieldSet(
    string? Area,
    string? Eqp,
    string? Step,
    DateTimeOffset? MesSourceDate,
    string? Package);

public sealed record AcceptedDemandSnapshot(
    string DemandId,
    string TransportDemandKey,
    long DemandRevision,
    string HistoryEpoch,
    long CatalogRevision,
    DateTimeOffset AcceptedAt,
    string SeriesId = "",
    string WorkType = "",
    string Sublot = "",
    int Generation = 0,
    DateTimeOffset CreatedAt = default,
    DateTimeOffset ValueObservedAt = default,
    string ValuePollTraceId = "",
    string ValueProjectionCommitId = "",
    LiveMesFieldSet? LiveMesFields = null);

public sealed record DemandCatalogSnapshot(
    string HistoryEpoch,
    long CatalogRevision,
    IReadOnlyList<AcceptedDemandSnapshot> Items);

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
