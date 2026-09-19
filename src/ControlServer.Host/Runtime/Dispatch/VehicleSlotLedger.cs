namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// Which of a vehicle's physical slots on one side are free for the demand being judged -- the one place dispatch asks.
/// </summary>
/// <remarks>
/// <para>
/// The slot capacity criterion chooses target slots from this answer and from nothing else (control-server#209). Until
/// then it read the vehicle's session baseline for itself, which is right for an idle vehicle and wrong for one under
/// way: a vehicle carrying a journey has slots its plan reserved and slots already loaded, which the baseline still
/// reports empty.
/// </para>
/// <para>
/// Which slot is on which side is the server's own record of the vehicle's slot model
/// (<see cref="DispatchVehicleFacts.SlotPositions"/>), never the vehicle's report; an unresolved model has no free slot
/// on any side.
/// </para>
/// </remarks>
public interface IVehicleSlotLedger
{
    /// <summary>The free physical slots in <paramref name="slotPosition"/>, lowest first, each once.</summary>
    Task<IReadOnlyList<int>> ReadAvailableSlotsAsync(
        DispatchVehicleFacts vehicle,
        string slotPosition,
        CancellationToken cancellationToken);
}

/// <summary>
/// The ledger for an idle vehicle: its session baseline, as Onboard reported it when the session was established.
/// </summary>
/// <remarks>
/// Exactly what <c>SlotCapacityCriterion</c> read before the port existed. The baseline is what the slot reservation
/// ledger is built against -- see <see cref="OnboardDispatchFactsReader.ReadOnboardFactsAsync"/> for why availability
/// is not updated from SafetyStateChanged. control-server#211 builds on this to take away, for a vehicle under way,
/// the slots its journey has reserved or loaded.
/// </remarks>
public sealed class SessionBaselineSlotLedger : IVehicleSlotLedger
{
    public Task<IReadOnlyList<int>> ReadAvailableSlotsAsync(
        DispatchVehicleFacts vehicle,
        string slotPosition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        _ = cancellationToken;
        if (vehicle.Onboard is not OnboardDispatchFacts onboard || vehicle.SlotPositions is not { } positions)
        {
            return Task.FromResult<IReadOnlyList<int>>([]);
        }

        return Task.FromResult<IReadOnlyList<int>>(
        [
            .. onboard.AvailableSlots
                .Where(slot => positions.SlotPositionByPhysicalSlot.TryGetValue(slot, out string? position) &&
                    string.Equals(position, slotPosition, StringComparison.Ordinal))
                .Distinct()
                .Order()
        ]);
    }
}
