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

/// <summary>Outcomes a recovery workflow is judged on by the server itself rather than by a result. Not wire codes.</summary>
public static class RecoveryWorkflowOutcomes
{
    /// <summary>
    /// An administrator closed the workflow's session because its result would never come (control-server#483). A result
    /// arriving afterwards is refused whole, and the refusal names this.
    /// </summary>
    public const string AdministratorClosed = "ADMINISTRATOR_CLOSED";
}

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
    IReadOnlyList<VehicleBusinessBlockingFact> BlockingFacts);

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
