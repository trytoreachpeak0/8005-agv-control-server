namespace ControlServer.Domain;

public sealed record SessionIdentity(
    string AgvId,
    long SessionGeneration,
    string ProtocolCommit,
    string ManifestSha256,
    string ProfileId,
    int ProtocolVersion);

public enum SessionReadiness
{
    RecoveryRequired,
    Ready
}

public sealed record SessionReadinessDecision(SessionReadiness Readiness, string ReasonCode);

public sealed record StationOperationPlan(
    string SlotOperationAttemptId,
    string DemandId,
    string SublotId,
    IReadOnlyList<int> TargetSlots,
    SlotOperationType OperationType,
    long ForcedRecoveryGeneration,
    string ContentHash,
    DateTimeOffset CreatedAt,
    string? AdmissionStationId = null,
    string? AdmissionTaskType = null);

public sealed record StationTaskTypeAdmission(string StationId, string TaskType);

public sealed record AdmissionPolicyDefinition(
    long Version,
    string DeploymentId,
    IReadOnlyList<StationTaskTypeAdmission> Relations,
    DateTimeOffset ImportedAt);

public sealed record StationOperationResult(
    string ResultId,
    string SlotOperationAttemptId,
    string DemandId,
    SlotOperationType OperationType,
    string OverallOutcome,
    IReadOnlyList<SlotPhysicalEvidence> SlotEvidence,
    bool AllSlotsCompleted,
    DateTimeOffset ObservedAt,
    string ResultContentSha256,
    string WireContentSha256);

public enum StationOperationStatus
{
    Prepared,
    Committed,
    Cancelled,
    RecoveryRequired
}

public enum SlotBusinessState
{
    Empty,
    Occupied,
    Unknown
}

public sealed record SlotPhysicalEvidence(
    int SlotNumber,
    SlotBusinessState State,
    bool DoorLocked,
    bool UnlockOutputReset);

public sealed record SafetyCheckObservation(
    string CheckId,
    long SafetyStateVersion,
    bool DepartureSafe,
    DateTimeOffset ObservedAt,
    DateTimeOffset ValidUntil);

public sealed record VehicleBusinessBlockingFact(
    string ReasonCode,
    string SubjectType,
    string? SubjectId);

/// <summary>
/// Carries no observation timestamp: the snapshot keeps one deterministic messageId per journey
/// stage, so its wire payload has to be reproducible for that revision. The publisher stamps
/// observedAt from the envelope's frozen sentAt instead.
/// </summary>
public sealed record VehicleBusinessProjection(
    long Revision,
    string Readiness,
    string? ActivePurpose,
    bool ManualChargingHold,
    string BatteryState,
    IReadOnlyList<VehicleBusinessBlockingFact> BlockingFacts);

public sealed record CurrentStopWorklistItem(
    string DemandId,
    string TransportDemandKey,
    string Sublot,
    string WorkType,
    string StopRole,
    int ExpectedBasketCount);

public sealed record CurrentStopWorklistProjection(
    string StationId,
    long Revision,
    string? OperationSessionId,
    IReadOnlyList<CurrentStopWorklistItem> Items);

/// <summary>
/// One leg of the plan the vehicle is shown.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="LegType"/> is <c>TO_PICKUP</c> or <c>TO_DROPOFF</c>, never <c>TO_GATE</c>.</b>
/// The gate is one destination among the six task types v2 froze, and the protocol renamed the leg
/// after what it is rather than after this profile's only instance of it -- the same rename that
/// took <c>CV-GATE-UNLOAD-ALL-EMPTY</c> to <c>CV-DESTINATION-UNLOAD-ALL-EMPTY</c>. The server's own
/// internal movement purpose is still <c>TO_GATE</c>; that string is a RIoT intent and an arrival
/// purpose, not a wire value, and the two must not be conflated.
/// </para>
/// <para>
/// <see cref="DemandId"/> moved here from the payload's top level in v2: with <c>legs.maxItems</c>
/// at 9 a plan can span several demands, so one demand id for the whole snapshot has no meaning.
/// <see cref="PublicStationFunction"/> is nullable and null today -- binding a station to a public
/// function is <c>FP-C9b</c>, batch 4, and naming one from the leg's own station id would be
/// inventing that capability rather than reporting it.
/// </para>
/// </remarks>
public sealed record UpcomingMovementLeg(
    string MovementLegId,
    string? LegType,
    string StopPurposeCategory,
    string? DemandId,
    string? PublicStationFunction,
    int Sequence,
    string StationId,
    string MapId,
    string State);

public sealed record UpcomingStopPlanProjection(
    long Revision,
    IReadOnlyList<UpcomingMovementLeg> Legs);

public sealed record SublotEntryRequest(
    string DemandId,
    string OperationSessionId,
    string StationId,
    long WorklistRevision,
    string ExpectedSublot);

public enum SlotOperationType
{
    Load,
    Unload
}

public sealed record SlotOperationCommand(
    string? CorrelationId,
    string DemandId,
    string SublotId,
    string OperationSessionId,
    string SlotOperationAttemptId,
    SlotOperationType OperationType,
    IReadOnlyList<int> Slots,
    long ForcedRecoveryGeneration,
    string CommandContentSha256);

public sealed record PreDepartureSafetyCheckCommand(
    string PreDepartureSafetyCheckId,
    string DemandId,
    string MovementLegId,
    long ExpectedSafetyStateVersion,
    string TargetStationId);

public enum DemandExecutionStatus
{
    Accepted,
    Succeeded,
    Cancelled,
    RecoveryRequired
}

public enum JourneyRuntimeStage
{
    AwaitingPickupArrival,
    AwaitingSublot,
    AwaitingLoadResult,
    /// <summary>
    /// The load is committed and the vehicle is still at the pickup: the one window in which an
    /// ordinary mis-placement may be corrected (REQ-0237, ADR-cross-0054/0055). Departure safety is
    /// not asked for until the station departure wait has run out with no correction open.
    /// </summary>
    AwaitingStationDeparture,
    AwaitingDepartureSafety,
    AwaitingGateArrival,
    AwaitingUnloadResult,
    Completed,
    Blocked
}

public sealed record JourneyExecutionPlan(
    string AgvId,
    string VehicleKey,
    long AgvLifecycleGeneration,
    int MapId,
    string MapIdentity,
    string DispatchZone,
    string RouteEvidenceId,
    string PickupStationId,
    int PickupStationRiotId,
    string GateStationId,
    int GateStationRiotId,
    int ExpectedBasketCount,
    IReadOnlyList<int> TargetSlots,
    string OperationSessionId,
    string PickupMovementLegId,
    string PickupUpperId,
    string GateMovementLegId,
    string GateUpperId,
    long DispatchGeneration,
    DateTimeOffset CreatedAt,
    long? AreaAssignmentVersion = null,
    string? RequiredSlotPosition = null);

public enum ConnectionRecoveryStatus
{
    Connected,
    SafelyFinishing,
    AwaitingHandshake,
    RecoveryRequired
}

public enum OperationResultDisposition
{
    Accepted,
    RecoveryRequired,
    HistoricalOnly,
    Replay
}

/// <summary>
/// A ManualChargingReturnToServiceRequested payload, decoded. The protocol declares
/// <c>observedBatteryPercent</c> as <c>number | null</c>, so an absent reading is a value the
/// administrator supplied rather than a violation.
/// </summary>
public sealed record ManualChargingReturnToServiceRequest(
    string RequestId,
    string AgvId,
    long SessionGeneration,
    string RequestMessageId,
    string RequestContentHash,
    string AdministratorId,
    string AdministratorRole,
    string Reason,
    double? ObservedBatteryPercent);

/// <summary>
/// What the server decided about one such request, durable so the same <c>requestId</c> arriving
/// again returns the same conclusion instead of deciding twice.
/// </summary>
/// <remarks>
/// <c>ProblemReasonCode</c> is null exactly when <c>Outcome</c> is
/// <c>RETURNED_TO_ELIGIBILITY_EVALUATION</c>; the schema types the field as
/// <c>Problem | null</c> and the rejected outcome is the only one carrying one.
/// </remarks>
public sealed record ManualChargingReturnToServiceDecision(
    string Outcome,
    string? ProblemReasonCode,
    string? ProblemFieldPath,
    string? ProblemDisplayMessage,
    long VehicleBusinessStateRevision)
{
    public const string ReturnedToEligibilityEvaluation = "RETURNED_TO_ELIGIBILITY_EVALUATION";
    public const string Rejected = "REJECTED";
}

public sealed class ProtocolIdentityMismatchException(string message) : InvalidOperationException(message);
public sealed class StaleSessionGenerationException(string message) : InvalidOperationException(message);
public sealed class ProtocolContentConflictException(string message) : InvalidOperationException(message);
public sealed class UnsafePhysicalEvidenceException(string message) : InvalidOperationException(message);
public sealed class UnsafeMovementAuthorizationException(string message) : InvalidOperationException(message);
public sealed class ActiveUnlockSetExpansionException(string message) : InvalidOperationException(message);
public sealed class BusinessIdentityConflictException(string message) : InvalidOperationException(message);
