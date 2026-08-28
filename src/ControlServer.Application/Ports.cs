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

public interface ISublotBoxCountReader
{
    Task<int?> ReadMaxBoxCountAsync(string sublot, CancellationToken cancellationToken);
}

public interface IPackageCapacityStore
{
    Task<int?> ResolveAndTrackAsync(
        string package,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
}

public interface IRiotVehicleSafetyFacts
{
    Task<RiotVehicleSafetyObservation> ReadVehicleSafetyAsync(
        string vehicleKey,
        CancellationToken cancellationToken);
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

    Task PersistExperimentalCreateAuthorizationAsync(
        ExperimentalRiotCreateAuthorization authorization,
        DateTimeOffset persistedAt,
        CancellationToken cancellationToken);

    Task RecordReconciliationAsync(
        string upperId,
        DispatchAuditWrite audit,
        bool markResultUnknown,
        CancellationToken cancellationToken);

    Task<CreateDispatchAttempt> ArmCreateDispatchAsync(
        string upperId,
        string requestSemanticSha256,
        DateTimeOffset armedAt,
        CancellationToken cancellationToken);

    Task<CreateDispatchAttempt> ArmExperimentalCreateDispatchAsync(
        string upperId,
        string requestSemanticSha256,
        ExperimentalRiotCreateAuthorization authorization,
        string eligibilityBasis,
        DateTimeOffset armedAt,
        CancellationToken cancellationToken);

    Task RecordCreateStartedAsync(
        string upperId,
        CreateDispatchAttempt attempt,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken);

    Task RecordCreateResponseAsync(
        string upperId,
        CreateDispatchAttempt attempt,
        DispatchAuditWrite audit,
        bool markResultUnknown,
        CancellationToken cancellationToken);

    Task MarkTerminalReconciliationRequiredAsync(
        string upperId,
        string orderId,
        DispatchAuditWrite audit,
        CancellationToken cancellationToken);

    Task ConfirmAsync(
        string upperId,
        string orderId,
        DispatchAuditWrite audit,
        CancellationToken cancellationToken);
}

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

public sealed record StoredMovementIntent(
    OrderIntent Intent,
    string Status,
    string? OrderId,
    int? DispatchAuditVersion,
    string? CreateAttemptId,
    int? CreateAttemptCount,
    string? ExperimentalAuthorizationId = null,
    string? EligibilityBasis = null);
