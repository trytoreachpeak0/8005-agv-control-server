using ControlServer.Application;

namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// How many of a vehicle's physical slots on one side are free for the demand being judged -- the one place dispatch asks.
/// </summary>
/// <remarks>
/// <para>
/// <b>A count, not slot numbers (REQ-0352, REQ-0208; control-server#754).</b> Acceptance reserves only how many baskets a
/// demand needs on its side; which physical slots it gets is decided after its sublot entry, at the lowest-numbered free
/// slots of that side at that moment. So dispatch has nothing to ask about slot numbers, and an answer that listed some
/// would name slots nobody has been given.
/// </para>
/// <para>
/// The slot capacity criterion judges against this answer and nothing else (control-server#209). Until
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
    /// <summary>How many physical slots in <paramref name="slotPosition"/> are free and not reserved.</summary>
    Task<int> ReadAvailableSlotCountAsync(
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
    public Task<int> ReadAvailableSlotCountAsync(
        DispatchVehicleFacts vehicle,
        string slotPosition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        _ = cancellationToken;
        return Task.FromResult(BaselineSlots(vehicle.Onboard, vehicle.SlotPositions, slotPosition).Count);
    }

    /// <summary>The session baseline's free slots in <paramref name="slotPosition"/>, lowest first, each once.</summary>
    internal static IReadOnlyList<int> BaselineSlots(
        OnboardDispatchFacts? onboardFacts,
        VehicleSlotPositions? slotPositions,
        string slotPosition)
    {
        if (onboardFacts is not OnboardDispatchFacts onboard || slotPositions is not { } positions)
        {
            return [];
        }

        return
        [
            .. onboard.AvailableSlots
                .Where(slot => positions.SlotPositionByPhysicalSlot.TryGetValue(slot, out string? position) &&
                    string.Equals(position, slotPosition, StringComparison.Ordinal))
                .Distinct()
                .Order()
        ];
    }
}
