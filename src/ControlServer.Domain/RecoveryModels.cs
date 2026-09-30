namespace ControlServer.Domain;

public enum RecoveryWorkflowState
{
    AwaitingAuthorization,
    CommandPending,
    AwaitingResult,
    Reconciled,
    RecoveryRequired,
    HistoricalOnly
}

/// <remarks>
/// <see cref="ClosedReason"/> is v3's required nullable <c>closedReason</c> (control-server#382): a registry
/// <c>ErrorCode</c> saying why a closed session closed without its action's result being reconciled, or null. Since
/// control-server#385 it is the session row's own persisted <c>ClosedReason</c>, written once at the closing.
/// </remarks>
public sealed record ExceptionRecoverySessionProjection(
    string ExceptionRecoverySessionId,
    long RecoverySessionRevision,
    string State,
    string AdministratorId,
    string AdministratorRole,
    string EventId,
    string? DemandId,
    string? SlotOperationAttemptId,
    IReadOnlyList<int> Slots,
    string? SelectedAction,
    IReadOnlyList<string> AllowedActions,
    IReadOnlyList<VehicleBusinessBlockingFact> BlockingFacts,
    string? ClosedReason);

public sealed record SlotOperationResumeAuthorization(
    string ExceptionRecoverySessionId,
    string RecoveryActionId,
    string DemandId,
    string SlotOperationAttemptId,
    string ProvenRecoveryCheckpoint,
    IReadOnlyList<int> Slots,
    string CommandContentSha256);

public sealed record LoadCompensationAuthorizationCommand(
    string RecoveryActionId,
    string ExceptionRecoverySessionId,
    string DemandId,
    string SlotOperationAttemptId,
    IReadOnlyList<int> Slots,
    string CommandContentSha256);

public sealed record LoadCorrectionAuthorizationCommand(
    string CorrectionId,
    string DemandId,
    string SlotOperationAttemptId,
    IReadOnlyList<int> Slots,
    string CommandContentSha256);

public sealed record FaultCargoRecoveryAuthorizationCommand(
    string ExceptionRecoverySessionId,
    string RecoveryActionId,
    string DemandId,
    IReadOnlyList<int> Slots,
    string HandoffId,
    string CommandContentSha256);

public sealed record ForcedMechanicalRecoveryAuthorizationCommand(
    string ExceptionRecoverySessionId,
    string RecoveryActionId,
    string? DemandId,
    long ForcedRecoveryGeneration,
    IReadOnlyList<int> Slots,
    string CommandContentSha256);
