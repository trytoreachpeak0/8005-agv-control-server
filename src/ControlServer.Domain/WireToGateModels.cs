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
    long ForcedRecoveryGeneration,
    string ContentHash,
    DateTimeOffset CreatedAt);

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

public sealed record VehicleBusinessProjection(
    long Revision,
    string Readiness,
    bool ManualChargingHold,
    string BatteryState,
    IReadOnlyList<VehicleBusinessBlockingFact> BlockingFacts,
    DateTimeOffset ObservedAt);

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
    string OperationSessionId,
    string SlotOperationAttemptId,
    SlotOperationType OperationType,
    IReadOnlyList<int> Slots,
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
