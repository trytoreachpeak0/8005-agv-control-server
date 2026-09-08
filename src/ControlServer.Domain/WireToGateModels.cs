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
    AwaitingDepartureSafety,
    AwaitingGateArrival,
    AwaitingUnloadResult,
    Completed,
    Blocked
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
    DateTimeOffset CreatedAt);

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

public sealed class ProtocolIdentityMismatchException(string message) : InvalidOperationException(message);
public sealed class StaleSessionGenerationException(string message) : InvalidOperationException(message);
public sealed class ProtocolContentConflictException(string message) : InvalidOperationException(message);
public sealed class UnsafePhysicalEvidenceException(string message) : InvalidOperationException(message);
public sealed class UnsafeMovementAuthorizationException(string message) : InvalidOperationException(message);
public sealed class ActiveUnlockSetExpansionException(string message) : InvalidOperationException(message);
public sealed class BusinessIdentityConflictException(string message) : InvalidOperationException(message);
