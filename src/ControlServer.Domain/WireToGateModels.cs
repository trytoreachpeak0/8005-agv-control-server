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
    /// <summary>
    /// The operation did not reach its target state, and the vehicle said so with complete,
    /// unambiguous physical evidence: every commanded slot reported a known occupancy state,
    /// a locked door and a reset unlock output. ADR-cross-0058 decision 5 settles that as a
    /// determinate failure -- nothing is uncertain, so nothing needs an administrator.
    /// <see cref="RecoveryRequired"/> is the opposite case: the server cannot tell what the
    /// physical world looks like. Load ends through LoadTaskCancellation from here
    /// (ADR-cross-0015, ADR-cross-0046); unload has no cancellation branch and never reaches
    /// this state.
    /// </summary>
    Failed,
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

public sealed record UpcomingMovementLeg(
    string MovementLegId,
    string LegType,
    int Sequence,
    string StationId,
    string MapId,
    string State);

public sealed record UpcomingStopPlanProjection(
    long Revision,
    string? DemandId,
    IReadOnlyList<UpcomingMovementLeg> Legs);

/// <summary>
/// What the operator may enter at this stop. <see cref="ExpectedSublots"/> is a set because
/// FR-001 AC-3 and BR-001 scope entry to the whole dispatch range, not to the stop the vehicle
/// happens to be parked at: "允许 T 的目标站点与操作员当前物理站点不完全相同，只要 T 在范围内".
/// A single-demand journey simply puts one entry in it.
/// </summary>
public sealed record SublotEntryRequest(
    string DemandId,
    string OperationSessionId,
    string StationId,
    long WorklistRevision,
    IReadOnlyList<string> ExpectedSublots);

/// <summary>
/// Why an entered sublot was refused, in terms the operator can act on. The reason codes are the
/// protocol's own registered ones; the display message is what appears at the vehicle.
/// </summary>
public sealed record SublotRejection(
    string DemandId,
    string OperationSessionId,
    long WorklistRevision,
    string ReasonCode,
    string? FieldPath,
    string DisplayMessage);

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

/// <summary>
/// What the journey is waiting for. The names still say PICKUP and GATE because that is what the
/// vehicle is driving to, but neither names a fixed leg any more: a journey visits a sequence of
/// stops, and <see cref="JourneyStopRole"/> on the current stop decides which of the two applies.
/// <see cref="AwaitingPickupArrival"/> is therefore reached once per pickup stop, not once per
/// journey.
/// </summary>
public enum JourneyRuntimeStage
{
    AwaitingPickupArrival,
    AwaitingSublot,
    AwaitingLoadResult,
    AwaitingDepartureSafety,
    AwaitingGateArrival,
    AwaitingUnloadResult,
    Completed,
    Blocked
}

/// <summary>
/// What a stop is for. Stored as text on the stop row rather than as an enum conversion because it
/// is also the protocol's own <c>stopRole</c> value and travels onto the wire unchanged.
/// </summary>
public static class JourneyStopRole
{
    public const string Pickup = "PICKUP";
    public const string Gate = "GATE";
}

/// <summary>
/// What a stop's movement leg is doing. Mirrors the protocol's leg state enum, minus the two states
/// the server never assigns.
/// </summary>
public static class JourneyStopState
{
    public const string Planned = "PLANNED";
    public const string Arrived = "ARRIVED";
    public const string Completed = "COMPLETED";
}

/// <summary>
/// How far one demand has got inside its journey. A journey ends when every demand it carries is
/// <see cref="Unloaded"/> or <see cref="Cancelled"/>; a demand that is <see cref="Loaded"/> is
/// physically on the vehicle, which is what starts the holding clock.
/// </summary>
public enum JourneyDemandState
{
    Planned,
    Loaded,
    Unloaded,
    Cancelled
}

/// <summary>
/// The vehicle's own errand between demands: drive to the charger, charge, and stand there until
/// something else needs the vehicle. It is deliberately not a <see cref="JourneyRuntimeStage"/>.
/// A journey carries a demand, a worklist and slot operations, and this carries none of the three
/// -- folding it in would make every stage handler answer "which kind of run is this" first.
/// </summary>
public enum AutoChargingStage
{
    AwaitingChargerArrival,
    Charging,
    Completed
}

/// <summary>
/// One journey: the vehicle and map identity it is bound to, plus the stops it visits in order and
/// the demands it carries. The identity is the journey's own, not a demand's -- ADR-cross-0057 --
/// so a journey can carry several demands and visit several pickup stops before the gate.
/// </summary>
/// <remarks>
/// This describes the journey at the moment it is created. The stop sequence grows afterwards, as
/// further demands are picked up on the way, and the loading phase ends on whichever of "full" or
/// "holding timeout" comes first; neither is expressible here.
/// </remarks>
public sealed record JourneyExecutionPlan(
    string JourneyId,
    string AgvId,
    string VehicleKey,
    long AgvLifecycleGeneration,
    int MapId,
    string MapIdentity,
    string DispatchZone,
    string GateStationId,
    int GateStationRiotId,
    string OperationSessionId,
    long DispatchGeneration,
    IReadOnlyList<JourneyStopPlan> Stops,
    IReadOnlyList<JourneyDemandPlan> Demands,
    DateTimeOffset CreatedAt);

/// <summary>
/// One stop in a journey's sequence. <see cref="Role"/> says what happens there -- loading at a
/// pickup stop, unloading at the gate -- and is what the stage handlers key on now that the two
/// are no longer one hard-coded leg each.
/// </summary>
public sealed record JourneyStopPlan(
    int Sequence,
    string Role,
    string StationId,
    int StationRiotId,
    string RouteEvidenceId,
    string MovementLegId,
    string UpperId,
    string LegType);

/// <summary>
/// One demand carried by a journey, bound to the stop it is loaded at. The slot reservation is per
/// demand because a demand has to fit its whole basket count in one go to be worth loading at all.
/// </summary>
public sealed record JourneyDemandPlan(
    string DemandId,
    int StopSequence,
    int ExpectedBasketCount,
    IReadOnlyList<int> TargetSlots);

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
    /// <summary>
    /// The result was accepted as a complete account of a failure -- see
    /// <see cref="StationOperationStatus.Failed"/>. It is not
    /// <see cref="RecoveryRequired"/>: no recovery workflow opens and no administrator is
    /// asked for anything.
    /// </summary>
    DeterminateFailure,
    RecoveryRequired,
    HistoricalOnly,
    Replay
}

public sealed class ProtocolIdentityMismatchException(string message) : InvalidOperationException(message);
public sealed class StaleSessionGenerationException(string message) : InvalidOperationException(message);
public sealed class ProtocolContentConflictException(string message) : InvalidOperationException(message);
public sealed class UnsafePhysicalEvidenceException(string message) : InvalidOperationException(message);
public sealed class UnsafeMovementAuthorizationException(string message) : InvalidOperationException(message);
public sealed class ActiveUnlockSetExpansionException(string message) : InvalidOperationException(message);
public sealed class BusinessIdentityConflictException(string message) : InvalidOperationException(message);
