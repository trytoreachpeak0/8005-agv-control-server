namespace ControlServer.Application;

/// <summary>
/// What one reading of a vehicle's <c>movementState</c> says about whether it is moving.
/// </summary>
/// <remarks>
/// <para>
/// Three values rather than two, and the third is the point. REQ-0247 forbids claiming a vehicle
/// is stopped while any necessary fact is unknown, so "RIoT did not say" has to be a value of its
/// own — folding it into <see cref="NotMoving"/> is exactly the defect that requirement names.
/// </para>
/// <para>
/// Deliberately not <c>RiotVehicleMotionState</c>, which answers a different question. That one is
/// the dispatch safety verdict: <c>Stopped</c> there means every one of eleven facts passed,
/// emergency latch included, so a vehicle held by a software emergency stop can never be
/// <c>Stopped</c> — which makes it useless for deciding whether a stopped vehicle has stopped.
/// This one reads motion and nothing else.
/// </para>
/// </remarks>
public enum VehicleMotionReading
{
    /// <summary>RIoT reports motion, or a non-zero speed.</summary>
    Moving,

    /// <summary>
    /// RIoT positively reports one of the closed set of non-moving states, with a zero speed.
    /// </summary>
    NotMoving,

    /// <summary>
    /// The state could not be read, was empty, or was not one of the states this server is
    /// prepared to read as either moving or not moving. Never evidence of a stop.
    /// </summary>
    Unknown,
}

/// <summary>
/// One sample of a vehicle's motion and position, taken at one instant.
/// </summary>
/// <remarks>
/// <para>
/// Position is a Map plus a station id because that is the only position RIoT publishes for a
/// vehicle. It is coarse, and while a vehicle is between stations RIoT reports no station at all
/// — Round 10 recorded <c>station: 0, noStation: true</c> through the whole of a move, and a
/// station only once the move finished. So an unknown position is the normal state of a moving
/// vehicle, which is why it counts as a missing fact rather than as an unchanged one.
/// </para>
/// <para>
/// <paramref name="MovementState"/> is carried verbatim alongside the reading so that a refusal
/// can name what RIoT actually said. The reading is this server's interpretation; the string is
/// RIoT's vocabulary.
/// </para>
/// </remarks>
public sealed record VehicleMotionSample(
    string DeviceKey,
    VehicleMotionReading Reading,
    string? MovementState,
    double? Speed,
    string? CurrentMap,
    int? CurrentStationId,
    DateTimeOffset ObservedAt)
{
    /// <summary>
    /// Whether this sample pins the vehicle to a place. False while it is between stations.
    /// </summary>
    public bool HasKnownPosition =>
        !string.IsNullOrWhiteSpace(CurrentMap) && CurrentStationId is > 0;

    /// <summary>Whether two samples were taken at the same place, both of them known.</summary>
    public bool IsSamePlaceAs(VehicleMotionSample other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return HasKnownPosition && other.HasKnownPosition &&
            string.Equals(CurrentMap, other.CurrentMap, StringComparison.Ordinal) &&
            CurrentStationId == other.CurrentStationId;
    }
}

/// <summary>
/// Reads one vehicle's motion and position for REQ-0247's combined stop proof.
/// </summary>
/// <remarks>
/// In its own file rather than in <c>Ports.cs</c>, following the engine's, the create gate's and
/// the command surface's ports: that file is ticket 06's for the batch so the capability lanes can
/// land in parallel.
/// </remarks>
public interface IVehicleMotionFacts
{
    /// <summary>
    /// Takes one sample. A sample is never a proof — REQ-0247 requires several, spaced, agreeing.
    /// </summary>
    Task<VehicleMotionSample> SampleMotionAsync(string deviceKey, CancellationToken cancellationToken);
}
