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

// ======== Batch 2, track B: the four capability lanes' storage ports (ticket 06) ===========
//
// Defined here in one pass so tickets 09／10／11／12／13 need no Ports.cs change of their own.
// Everything below is storage access. None of it decides anything: no staleness verdict, no
// admission rule, no escalation. Those live in each lane's own ticket.

// ---- B2 multi-vehicle execution (ticket 09) ----------------------------------------------

/// <summary>One vehicle's dispatch policy: which task types it may take, and its round budget.</summary>
public sealed record VehicleDispatchProfile(
    string AgvId,
    IReadOnlySet<string> AllowedTaskTypes,
    int RoundTimeoutMilliseconds);

/// <summary>
/// The whole fleet's dispatch policy as one snapshot, read once per dispatch round so that a
/// round is decided against a single consistent configuration.
/// </summary>
/// <remarks>
/// Fail-closed by construction: a vehicle absent from <paramref name="Vehicles"/>, a task type
/// absent from its allowed set, or a zone absent from <paramref name="ZoneVehicles"/> all mean
/// "not allowed". There is no permissive default to fall back to.
/// </remarks>
public sealed record VehicleDispatchPolicy(
    IReadOnlyList<VehicleDispatchProfile> Vehicles,
    IReadOnlyDictionary<string, IReadOnlySet<string>> ZoneVehicles,
    string ConfigurationVersion);

public interface IVehicleDispatchPolicyStore
{
    Task<VehicleDispatchPolicy> ReadPolicyAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the whole policy. Configuration-driven, so partial updates are not a thing.</summary>
    Task ReplacePolicyAsync(
        VehicleDispatchPolicy policy,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Claims a vehicle for one in-flight order, returning false when it is already occupied.
    /// This is the uniqueness that moved down from the lease table onto OrderIntents: the unique
    /// index is what decides, not a read-then-write.
    /// </summary>
    Task<bool> TryClaimVehicleOccupancyAsync(
        string upperId,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken);

    Task ReleaseVehicleOccupancyAsync(
        string upperId,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken);
}

// ---- RouteGraphSnapshot engine (ticket 12) ------------------------------------------------

/// <summary>One directed edge of the design-state graph.</summary>
public sealed record RouteGraphEdgeFact(
    int EdgeId,
    int StartNode,
    int EndNode,
    double CostMm,
    int StartX,
    int StartY,
    int EndX,
    int EndY,
    int Direction,
    bool IsBackEdge);

/// <summary>
/// One station of the design state, already resolved onto a node. Resolution is the engine's
/// job — the store persists the result and the residual it was decided by.
/// </summary>
public sealed record RouteGraphStationFact(
    int StationId,
    string Name,
    int EdgeId,
    double PosX,
    double PosY,
    int ResolvedNode,
    double ResolutionResidualMm);

/// <summary>One edge's membership in a named edge group.</summary>
public sealed record RouteGraphEdgeGroupFact(string GroupName, int EdgeId, string GroupType);

/// <summary>
/// The snapshot header: both cycles' clocks and everything a staleness decision reads.
/// </summary>
/// <remarks>
/// <paramref name="EdgeGroupFingerprint"/> is an empty string when the Map has no edge groups at
/// all — map25's actual state. A staleness rule must distinguish that from "never fetched", which
/// is what <paramref name="EdgeGroupRefreshedAt"/> being null means.
/// </remarks>
public sealed record RouteGraphSnapshotHeader(
    int MapId,
    long DesignRevision,
    DateTimeOffset? DesignRefreshedAt,
    string? DesignSourceGmtUpdate,
    int DesignEdgeCount,
    int DesignStationCount,
    DateTimeOffset? RuntimeRefreshedAt,
    int RuntimeRemovedEdgeCount,
    int RuntimeRemovedStationCount,
    string EdgeGroupFingerprint,
    DateTimeOffset? EdgeGroupRefreshedAt,
    bool DynamicRouteCostPresent,
    DateTimeOffset? DynamicRouteCostObservedAt,
    string? StaleReason,
    DateTimeOffset? StaleSince);

/// <summary>The design state plus the runtime removals, as one consistent read.</summary>
public sealed record RouteGraphState(
    RouteGraphSnapshotHeader Header,
    IReadOnlyList<RouteGraphEdgeFact> Edges,
    IReadOnlyList<RouteGraphStationFact> Stations,
    IReadOnlySet<int> RemovedEdgeIds,
    IReadOnlySet<int> RemovedStationIds);

public interface IRouteGraphSnapshotStore
{
    Task<RouteGraphSnapshotHeader?> ReadHeaderAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>Reads design state and runtime removals together, so the caller never mixes cycles.</summary>
    Task<RouteGraphState?> ReadStateAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the design state wholesale and returns the new revision. Wholesale because the
    /// edge table is a snapshot, not a log: a partially applied graph is a graph with holes, and
    /// a hole is a wrong shortest path rather than a visible failure.
    /// </summary>
    Task<long> ReplaceDesignStateAsync(
        int mapId,
        IReadOnlyList<RouteGraphEdgeFact> edges,
        IReadOnlyList<RouteGraphStationFact> stations,
        string? sourceGmtUpdate,
        DateTimeOffset refreshedAt,
        CancellationToken cancellationToken);

    /// <summary>Replaces the runtime removals — the 10-second cycle.</summary>
    Task ReplaceRuntimeStateAsync(
        int mapId,
        IReadOnlyList<int> removedEdgeIds,
        IReadOnlyList<int> removedStationIds,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the Map's edge groups and stores the fingerprint computed over them. An empty
    /// list is a legitimate result and yields an empty fingerprint.
    /// </summary>
    Task ReplaceEdgeGroupsAsync(
        int mapId,
        IReadOnlyList<RouteGraphEdgeGroupFact> groups,
        string fingerprint,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);

    Task RecordDynamicRouteCostObservationAsync(
        int mapId,
        bool present,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);

    /// <summary>Marks the snapshot stale. Stale means no dispatch this round, not degraded ranking.</summary>
    Task MarkStaleAsync(
        int mapId,
        string reason,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    Task ClearStaleAsync(int mapId, DateTimeOffset at, CancellationToken cancellationToken);
}

// ---- FP-C11 fault isolation and command reconciliation (tickets 10 and 11) -----------------

/// <summary>The two-level fault fact held for one vehicle.</summary>
public sealed record VehicleFaultFact(
    string AgvId,
    VehicleFaultLevel Level,
    long FaultGeneration,
    string? EvidenceCode,
    bool EvidenceOnAutoConfirmWhitelist,
    DateTimeOffset? EnteredAt,
    DateTimeOffset? LastEvaluatedAt,
    bool StopProven,
    DateTimeOffset? StopProvenAt,
    DateTimeOffset? EscalatedAt,
    DateTimeOffset? ClearedAt,
    string? ClearedReason);

/// <summary>A faulted vehicle's cargo binding (REQ-0238).</summary>
public sealed record FaultedCargoBinding(
    string CargoBindingId,
    string AgvId,
    long FaultGeneration,
    string DemandId,
    string? MovementLegId,
    string TransportDemandKey,
    bool LoadingWitnessed,
    DateTimeOffset BoundAt,
    DateTimeOffset? ReleasedAt,
    string? ReleasedReason);

public interface IVehicleFaultStore
{
    Task<VehicleFaultFact?> ReadAsync(string agvId, CancellationToken cancellationToken);

    /// <summary>
    /// Records entry into, or escalation within, the two-level model, returning the resulting
    /// fact. Entering from <see cref="VehicleFaultLevel.None"/> starts a new fault generation;
    /// escalating within a live fault keeps it, so the cargo binding and audit trail stay attached
    /// to the same episode.
    /// </summary>
    Task<VehicleFaultFact> RecordLevelAsync(
        string agvId,
        VehicleFaultLevel level,
        string evidenceCode,
        bool evidenceOnAutoConfirmWhitelist,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records whether the vehicle is currently proven stopped. The caller owns the combined-
    /// evidence rule (REQ-0247); this only persists the verdict and its timestamp, which is what
    /// makes freshness checkable afterwards.
    /// </summary>
    Task RecordStopProofAsync(
        string agvId,
        long faultGeneration,
        bool proven,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>Records the immediate escalation REQ-0246 requires when a stop cannot be proven.</summary>
    Task RecordEscalationAsync(
        string agvId,
        long faultGeneration,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    Task ClearAsync(
        string agvId,
        long faultGeneration,
        string reason,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    Task<FaultedCargoBinding> BindCargoAsync(
        string agvId,
        long faultGeneration,
        string demandId,
        string? movementLegId,
        string transportDemandKey,
        bool loadingWitnessed,
        DateTimeOffset boundAt,
        CancellationToken cancellationToken);

    Task<FaultedCargoBinding?> ReadLiveCargoAsync(string agvId, CancellationToken cancellationToken);

    Task ReleaseCargoAsync(
        string cargoBindingId,
        string reason,
        DateTimeOffset releasedAt,
        CancellationToken cancellationToken);
}

/// <summary>One attempt at one RIoT order command.</summary>
public sealed record RiotOrderCommandAttempt(
    string CommandAuditId,
    string CommandType,
    string AgvId,
    string TargetUpperId,
    string? TargetOrderId,
    int AttemptNumber,
    string RequestSemanticSha256,
    DateTimeOffset IssuedAt,
    RiotOrderCommandOutcome Outcome,
    DateTimeOffset? ReconciledAt,
    string? ReceiptJson,
    long? FaultGeneration);

public interface IRiotOrderCommandAuditStore
{
    /// <summary>
    /// Records the intent to issue a command and hands back the attempt, with its number already
    /// allocated. Called before the call goes out: an attempt with no recorded outcome is exactly
    /// the state that says "we may have issued this and do not know what happened".
    /// </summary>
    Task<RiotOrderCommandAttempt> ArmAttemptAsync(
        string commandType,
        string agvId,
        string targetUpperId,
        string? targetOrderId,
        string requestSemanticSha256,
        long? faultGeneration,
        DateTimeOffset issuedAt,
        CancellationToken cancellationToken);

    Task RecordOutcomeAsync(
        string commandAuditId,
        RiotOrderCommandOutcome outcome,
        string? receiptJson,
        DateTimeOffset reconciledAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every attempt at one command against one order, oldest first. This is what makes "it was
    /// called, and exactly once" decidable rather than a matter of trust.
    /// </summary>
    Task<IReadOnlyList<RiotOrderCommandAttempt>> ReadAttemptsAsync(
        string commandType,
        string targetUpperId,
        CancellationToken cancellationToken);
}

// ---- FP-C13 catalog availability and the pre-create gate (ticket 13) -----------------------

/// <summary>One Map's catalog availability state (REQ-0308's catalog level).</summary>
public sealed record MapStationCatalogAvailability(
    int MapId,
    MapStationCatalogState State,
    DateTimeOffset? LastCompleteConfirmationAt,
    DateTimeOffset? LastAttemptAt,
    string? LastFailureReason,
    long CatalogRevision,
    int? ApprovedSyncPeriodSeconds,
    int? ApprovedMaxUnconfirmedSeconds,
    DateTimeOffset UpdatedAt);

/// <summary>A station endpoint frozen at demand-creation time (REQ-0305).</summary>
public sealed record FrozenStationFact(
    FrozenStationRole Role,
    int MapId,
    int StationId,
    string StationName);

/// <summary>One pre-create gate evaluation and the evidence it rested on.</summary>
public sealed record CreateGateEvaluation(
    string GateAuditId,
    string DemandId,
    string TransportDemandKey,
    string AgvId,
    int MapId,
    int TargetStationId,
    CreateGateVerdict Verdict,
    long? RiotRouteCostMm,
    long? GraphTraversalCostMm,
    bool GraphReachable,
    string? ConflictDetail,
    DateTimeOffset EvaluatedAt);

public interface ICatalogAvailabilityStore
{
    Task<MapStationCatalogAvailability?> ReadStateAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a complete confirmation — the only thing freshness is measured from (REQ-0302).
    /// The two approved parameters are stored alongside so the state can be audited against the
    /// values it was actually judged by.
    /// </summary>
    Task RecordCompleteConfirmationAsync(
        int mapId,
        long catalogRevision,
        int approvedSyncPeriodSeconds,
        int approvedMaxUnconfirmedSeconds,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records a catalog-level failure. It does not touch the last complete confirmation, because
    /// a failed attempt is not a confirmation and must not extend freshness.
    /// </summary>
    Task RecordFailureAsync(
        int mapId,
        MapStationCatalogState state,
        string reason,
        DateTimeOffset at,
        CancellationToken cancellationToken);

    /// <summary>
    /// Freezes a demand's resolved endpoints. Idempotent per demand: freezing again with the same
    /// endpoints is a no-op, and REQ-0305 forbids a later catalog refresh from rewriting them.
    /// </summary>
    Task FreezeDemandStationsAsync(
        string demandId,
        string transportDemandKey,
        IReadOnlyList<FrozenStationFact> stations,
        long catalogRevision,
        DateTimeOffset frozenAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FrozenStationFact>> ReadFrozenStationsAsync(
        string demandId,
        CancellationToken cancellationToken);

    Task RecordGateVerdictAsync(CreateGateEvaluation evaluation, CancellationToken cancellationToken);
}
