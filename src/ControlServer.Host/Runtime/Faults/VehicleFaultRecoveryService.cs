using ControlServer.Application;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Host.Runtime.Faults;

/// <summary>What a person asks the server to do about a faulted vehicle (control-server#299).</summary>
public enum VehicleFaultRecoveryAction
{
    /// <summary>The order that raised the fault has ended; clear the fault and let the vehicle work again.</summary>
    ClearFault,

    /// <summary>The order was held (PAUSED 7); continue it on the same vehicle (REQ-0239, first half).</summary>
    ResumeHeldOrder,

    /// <summary>
    /// Rebuild the ended order for the same vehicle and the same demand (control-server#318). Reserved: the criteria are
    /// judged and reported, and the answer is always that the rebuild is not available yet.
    /// </summary>
    ConfirmRebuild,
}

/// <summary>A person's request about one explicitly named vehicle.</summary>
/// <param name="OperatorId">Who asks. Recorded, and required: an unnamed request has nobody to record.</param>
/// <param name="FaultRemedied">The person confirms the cause of the fault has been removed on site.</param>
/// <param name="Note">Free text, recorded verbatim. Optional.</param>
public sealed record VehicleFaultRecoveryRequest(
    EmergencyStopSubject Subject,
    VehicleFaultRecoveryAction Action,
    string? OperatorId,
    bool FaultRemedied,
    string? Note);

/// <summary>How a request ended.</summary>
public enum VehicleFaultRecoveryOutcome
{
    /// <summary>The fault is cleared and the journey disposed of; see the disposition.</summary>
    Cleared,

    /// <summary>The held order was continued and the fault cleared.</summary>
    Resumed,

    /// <summary>The same clearance had already been made; nothing was done again.</summary>
    AlreadyCleared,

    /// <summary>Refused; the reasons name every criterion that is not met.</summary>
    Refused,

    /// <summary>The action is reserved and not implemented yet; the reasons still name every criterion not met.</summary>
    NotAvailable,
}

/// <summary>What was done with the vehicle's journey when its fault was cleared.</summary>
public static class VehicleFaultRecoveryDispositions
{
    /// <summary>No journey was waiting on the order, so there was nothing to dispose of.</summary>
    public const string None = "NONE";

    /// <summary>Nothing was loaded: every demand still to load was released for redispatch and the journey closed.</summary>
    public const string Released = "RELEASED_FOR_REDISPATCH";

    /// <summary>Cargo is on board: its binding stays, and the journey waits for a person (or #318's rebuild).</summary>
    public const string HeldForPerson = "HELD_FOR_PERSON";
}

/// <summary>The answer to one request.</summary>
public sealed record VehicleFaultRecoveryDecision(
    VehicleFaultRecoveryOutcome Outcome,
    IReadOnlyList<string> Reasons,
    string Disposition,
    long? FaultGeneration);

/// <summary>
/// The person's way out of a vehicle fault (control-server#299).
/// </summary>
public sealed class VehicleFaultRecoveryService(
    ControlServerDbContext dbContext,
    IVehicleFaultStore faults,
    IRiotVehicleEmergencyFacts emergencyFacts,
    IRiotVehicleOrderFacts orderFacts,
    IRiotMovementGateway movement,
    EmergencyStopSupervisor emergencyStop,
    VehicleFaultCoordinator coordinator,
    VehicleMotionLedger ledger,
    JourneyMutationGate gate,
    TimeProvider timeProvider,
    ILogger<VehicleFaultRecoveryService> logger)
{
    public Task<VehicleFaultRecoveryDecision> RecoverAsync(
        VehicleFaultRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        _ = (dbContext, faults, emergencyFacts, orderFacts, movement, emergencyStop, coordinator, ledger, gate,
            timeProvider, logger, request, cancellationToken);
        return Task.FromResult(new VehicleFaultRecoveryDecision(
            VehicleFaultRecoveryOutcome.Refused, ["NOT_IMPLEMENTED"], VehicleFaultRecoveryDispositions.None, null));
    }
}
