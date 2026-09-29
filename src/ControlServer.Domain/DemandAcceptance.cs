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
    long DispatchGeneration = 0,
    string OrderShape = OrderShapes.SingleMove);

/// <summary>
/// The shape of the RIoT order an <see cref="OrderIntent"/> stands for (batch 9, control-server#399). Stored in
/// <c>OrderIntents.OrderShape</c> with no CHECK constraint: adding one on SQLite rebuilds that table, so the values are
/// validated in code by the order creation of control-server#401.
/// </summary>
public static class OrderShapes
{
    /// <summary>One move to the destination station: every order before batch 9, and the column's default and back-fill.</summary>
    public const string SingleMove = "SINGLE_MOVE";

    /// <summary>
    /// A charging order: <c>move</c> to the charger and <c>act(78, 1, 0)</c>. The moves an entry and exit point expand into
    /// are still this shape.
    /// </summary>
    public const string Charge = "CHARGE";
}
