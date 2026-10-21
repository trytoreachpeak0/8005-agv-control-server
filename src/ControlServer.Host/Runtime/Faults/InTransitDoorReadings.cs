using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Faults;

/// <summary>What this round's door reading asks of the engine (control-server#789, CP-0017).</summary>
public enum InTransitDoorAction
{
    /// <summary>Nothing to hand on: the doors are proven locked, or the vehicle is not heard from and no fault stands.</summary>
    Nothing,

    /// <summary>A slot reads unknown and its grace has not run out: alarm only, no fault, no command (REQ-0246 as revised).</summary>
    InGrace,

    /// <summary>Hand the vehicle to the fault model this round.</summary>
    HandToFault,
}

/// <summary>One round's answer from <see cref="InTransitDoorReadings.Observe"/>.</summary>
/// <param name="Action">What the engine does this round.</param>
/// <param name="DoorsNotProvenLocked">The doors read this round are not fresh, known and locked.</param>
/// <param name="ReadingRestored">
/// The door fault in effect was raised only for doors that could not be read, the doors have read locked for
/// <see cref="VehicleFaultOptions.DoorReadingRestoredFor"/> without a break, and this process saw the whole of it.
/// </param>
/// <param name="GraceStarted">This round started a grace.</param>
/// <param name="GraceEndedAfter">A grace ended this round on a locked reading, after this long.</param>
/// <param name="HandedBecause">Why the vehicle is handed on this round when that is new: one of the <c>Hand*</c> codes.</param>
public sealed record InTransitDoorReading(
    InTransitDoorAction Action,
    bool DoorsNotProvenLocked,
    bool ReadingRestored = false,
    bool GraceStarted = false,
    TimeSpan? GraceEndedAfter = null,
    string? HandedBecause = null);

/// <summary>
/// The door readings of driving vehicles across rounds: the grace for a slot that cannot be read, and whether a door fault
/// was raised only for doors that could not be read (control-server#789, CP-0017).
/// </summary>
public sealed class InTransitDoorReadings(IOptions<VehicleFaultOptions> options)
{
    public const string HandReadNotLocked = "DOOR_READ_NOT_LOCKED";

    public const string HandGraceExpired = "DOOR_UNKNOWN_GRACE_EXPIRED";

    public const string HandGraceLimitReached = "DOOR_UNKNOWN_GRACE_LIMIT_REACHED";

    private readonly VehicleFaultOptions faultOptions = options.Value;

    /// <summary>The configured grace (<see cref="VehicleFaultOptions.DoorUnknownGrace"/>).</summary>
    public TimeSpan Grace => faultOptions.DoorUnknownGrace;

    /// <summary>The earliest moment a grace in progress runs out, or null when none is in progress.</summary>
    public DateTimeOffset? NextDueAt => null;

    public InTransitDoorReading Observe(string agvId, InTransitDoorVerdict doors, bool doorFaultInEffect, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agvId);
        ArgumentNullException.ThrowIfNull(doors);
        _ = faultOptions;
        bool reported = doors.State is InTransitDoorState.NotLocked or InTransitDoorState.SlotStateUnknown;
        return reported || doorFaultInEffect
            ? new InTransitDoorReading(InTransitDoorAction.HandToFault, doors.NotProvenLocked)
            : new InTransitDoorReading(InTransitDoorAction.Nothing, doors.NotProvenLocked);
    }
}
