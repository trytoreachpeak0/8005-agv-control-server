using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime;

/// <summary>What an administrator declares, after the entry point has checked its shape.</summary>
public sealed record SlotFaultDeclarationRequest(
    string RequestId,
    string AgvId,
    int SlotNo,
    string FaultCategory,
    string Note,
    string OperatorId,
    string AdministratorRole);

public enum SlotFaultDeclarationOutcome
{
    /// <summary>Persisted with its command in one save; <see cref="SlotFaultDeclarationDecision.SentToVehicle"/> says whether it went out.</summary>
    Accepted,

    /// <summary>The same request again: answered from the declaration it created, nothing written, nothing queued.</summary>
    AlreadyAccepted,

    /// <summary>A precondition REQ-0359 names does not hold; every one that fails is listed.</summary>
    Refused,

    /// <summary>The request id was already used for a different request.</summary>
    RequestIdConflict
}

public sealed record SlotFaultDeclarationDecision(
    SlotFaultDeclarationOutcome Outcome,
    IReadOnlyList<string> Reasons,
    SlotFaultDeclarationRow? Declaration,
    bool SentToVehicle);

/// <summary>Why a declaration was refused. HTTP reasons, not protocol reason codes: nothing here reaches the vehicle.</summary>
public static class SlotFaultDeclarationRefusals
{
    public const string NoSlotOperationInProgress = "SLOT_FAULT_NO_SLOT_OPERATION_IN_PROGRESS";
    public const string SlotNotInOperation = "SLOT_FAULT_SLOT_NOT_IN_OPERATION";
    public const string ExpectedActionNotOverdue = "SLOT_FAULT_EXPECTED_ACTION_NOT_OVERDUE";
    public const string NotTheCurrentSlot = "SLOT_FAULT_NOT_THE_CURRENT_SLOT";
    public const string OperationAlreadyClosed = "SLOT_FAULT_OPERATION_ALREADY_CLOSED";
    public const string OperationAlreadyUnknown = "SLOT_FAULT_OPERATION_ALREADY_UNKNOWN";
    public const string DeclarationPending = "SLOT_FAULT_DECLARATION_PENDING";
    public const string NoSession = "SLOT_FAULT_NO_SESSION";
}

/// <summary>
/// REQ-0359's server half: judge what the server can judge, write the declaration and its
/// <c>SlotFaultDeclarationCommand</c> in one save, and send the command.
/// </summary>
public sealed class SlotFaultDeclarationService(
    ControlServerDbContext dbContext,
    WireToGateStore store,
    OnboardJourneyPublisher publisher,
    TimeProvider timeProvider,
    ILogger<SlotFaultDeclarationService> logger)
{
    public Task<SlotFaultDeclarationDecision> DeclareAsync(
        SlotFaultDeclarationRequest request,
        CancellationToken cancellationToken)
    {
        _ = (dbContext, store, publisher, timeProvider, logger, request, cancellationToken);
        throw new NotImplementedException();
    }
}
