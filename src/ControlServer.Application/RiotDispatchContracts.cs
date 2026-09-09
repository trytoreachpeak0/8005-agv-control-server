namespace ControlServer.Application;

public enum RiotDispatchAuditPhase
{
    PreCreateReconciliation,
    CreateDispatch,
    CreateRequest,
    CreateResponse,
    PostCreateReconciliation
}

public enum RiotDispatchAuditOutcome
{
    Unknown,
    NotFound,
    Armed,
    Started,
    Accepted,
    Confirmed,
    Terminal,
    LegacyAuditUnavailable
}

public sealed record RiotOrderCallReceipt(
    string Operation,
    string Classification,
    DateTimeOffset ObservedAt,
    int? HttpStatusCode = null,
    string? BusinessCode = null,
    bool? ResultPresent = null,
    string? FailureCategory = null);

public sealed record DispatchAuditWrite(
    RiotDispatchAuditPhase Phase,
    RiotDispatchAuditOutcome Outcome,
    DateTimeOffset OccurredAt,
    string? AttemptId = null,
    string? RequestSemanticSha256 = null,
    string? ReturnedOrderId = null,
    RiotOrderCallReceipt? Receipt = null,
    string? ExperimentalAuthorizationId = null,
    string? EligibilityBasis = null);

public sealed record CreateDispatchAttempt(
    string AttemptId,
    int AttemptNumber,
    string RequestSemanticSha256,
    DateTimeOffset ArmedAt,
    string? ExperimentalAuthorizationId = null,
    string? EligibilityBasis = null);

public enum RiotOrderObservationKind
{
    NotFound,
    Active,
    Terminal,

    /// <summary>
    /// RIoT refused a create because the frozen upperId is already held (BC-ORDER-004).
    /// Definitive: no second order was created, and the existing order is not returned,
    /// so the caller must reconcile by upperId to learn its state.
    /// </summary>
    AlreadyExists,
    Unknown
}

public sealed record RiotOrderObservation(
    string UpperId,
    RiotOrderObservationKind Kind,
    string? OrderId,
    int? OrderState = null,
    string? VehicleKey = null,
    int? MapId = null,
    int? DestinationStationId = null,
    RiotOrderCallReceipt? Receipt = null);

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

public enum RiotVehicleMotionState
{
    Stopped,
    Moving,
    Unknown
}

public sealed record RiotVehicleSafetyObservation(
    string VehicleKey,
    RiotVehicleMotionState MotionState,
    DateTimeOffset ObservedAt,
    string Source,
    IReadOnlyList<string> ReasonCodes);

public sealed record RiotMapStation(int StationId, string StationName);

public sealed record RiotMapStationCatalogSnapshot(
    int MapId,
    DateTimeOffset ObservedAt,
    string ContentSha256,
    IReadOnlyList<RiotMapStation> Stations);
