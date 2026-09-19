using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.Extensions.Logging;

namespace ControlServer.Host.Runtime.Dispatch.Criteria;

/// <summary>
/// Derives how many baskets the sublot needs and chooses that many free slots inside the slot group the
/// demand's AREA is assigned.
/// </summary>
/// <remarks>
/// <para>
/// A box-count read that fails over the network blocks this candidate rather than propagating:
/// the failure is logged and the round moves on to the next candidate. That distinction matters —
/// one unreachable sublot must not fail the whole round closed, but it must not be admitted with
/// an assumed count either.
/// </para>
/// <para>
/// The slots are chosen here, not later, because the count and the slots have to come from the
/// same onboard observation. Choosing them against a newer observation could pick slots that the
/// count was never checked against.
/// </para>
/// <para>
/// <b>Slots are chosen inside one group (REQ-0351, REQ-0352; ADR-cross-0059).</b> This replaces invariant I8,
/// "any slot serves any station", which took the lowest free slots across the whole vehicle. A machine only
/// opens one side's doors, so a basket put in the other side's slot cannot come out there. The whole demand
/// goes into the group its AREA is assigned, lowest-numbered free slots first; free slots in any other group
/// do not count, and when the group is short the demand waits rather than borrowing.
/// </para>
/// <para>
/// Which slot is in which group is the server's record of the vehicle's slot model
/// (<see cref="DispatchVehicleFacts.SlotPositions"/>, program#70 decision 4), never a slot-number range: a
/// vehicle bound to a different model is grouped by that model. Which slots in the group are free is the slot
/// ledger's answer (<see cref="IVehicleSlotLedger"/>, control-server#209); for an idle vehicle that is the vehicle's
/// session baseline, as it was when this criterion read it for itself.
/// </para>
/// <para>
/// <b>There is deliberately no branch on the station type.</b> Loading and unloading both read the one
/// <see cref="DispatchCandidateEvaluation.TargetSlots"/> chosen here, so the gate being able to open either
/// group needs no code: which group opens is decided by the demand's AREA alone. Do not add a per-station
/// check of the door side (REQ-0353, program#80).
/// </para>
/// </remarks>
public sealed class SlotCapacityCriterion(
    ISublotBoxCountReader boxCountReader,
    ILogger<SlotCapacityCriterion> logger,
    IVehicleSlotLedger slotLedger) : IDispatchAdmissionCriterion
{
    private static readonly Action<ILogger, string, Exception?> LogBoxCountFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1, nameof(LogBoxCountFailed)),
            "Sublot box count unavailable for demand {DemandId}.");

    /// <summary>Baskets per journey, as the onboard rack is built. Outside this, the plan is wrong.</summary>
    private const int MinimumBasketCount = 1;
    private const int MaximumBasketCount = 8;

    public int Order => 100;

    public async Task<string> EvaluateAsync(
        DispatchCandidateEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);

        _ = evaluation.Vehicle.Onboard
            ?? throw new InvalidOperationException(
                "Onboard facts are established by VehicleDynamicFactsCriterion before this runs.");

        int? maxBoxCount;
        try
        {
            maxBoxCount = await boxCountReader
                .ReadMaxBoxCountAsync(evaluation.Candidate.Sublot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or InvalidDataException or JsonException)
        {
            LogBoxCountFailed(logger, evaluation.Candidate.DemandId, error);
            maxBoxCount = null;
        }

        if (!AuthoritativeBasketCount.BoxCountIsUsable(maxBoxCount))
        {
            return "SUBLOT_BOX_COUNT_UNAVAILABLE";
        }

        int expectedBasketCount = AuthoritativeBasketCount.Compute(maxBoxCount, evaluation.PackageCapacity)
            ?? throw new InvalidOperationException("An eligible PACKAGE must have a frozen capacity.");
        evaluation.ExpectedBasketCount = expectedBasketCount;

        if (expectedBasketCount is < MinimumBasketCount or > MaximumBasketCount)
        {
            return "EXPECTED_BASKET_COUNT_OUT_OF_RANGE";
        }

        // The area assignment whitelist (control-server#72) refuses an AREA with no assignment earlier in the
        // chain; this is the defence behind it, not the place that decision is made.
        string? requiredSlotPosition = evaluation.RequiredSlotPosition;
        if (requiredSlotPosition is null)
        {
            return DispatchReasonCodes.AreaSlotGroupNotAssigned;
        }

        // Unresolved is fail-closed: no default eight-slot model, no inference from slot numbers.
        VehicleSlotPositions? slotPositions = evaluation.Vehicle.SlotPositions;
        if (slotPositions is null)
        {
            return DispatchReasonCodes.VehicleSlotModelUnresolved;
        }

        // Physical slots, whatever their state: a demand larger than the group can never go on this vehicle,
        // which is a different fact from the group being full right now.
        if (expectedBasketCount > slotPositions.PhysicalSlotCountByGroup.GetValueOrDefault(requiredSlotPosition))
        {
            return DispatchReasonCodes.ExpectedBasketCountExceedsSlotGroup;
        }

        IReadOnlyList<int> groupAvailableSlots = await slotLedger
            .ReadAvailableSlotsAsync(evaluation.Vehicle, requiredSlotPosition, cancellationToken)
            .ConfigureAwait(false);
        if (groupAvailableSlots.Count < expectedBasketCount)
        {
            return DispatchReasonCodes.SlotGroupCapacityTemporarilyUnavailable;
        }

        evaluation.TargetSlots = groupAvailableSlots.Take(expectedBasketCount).ToArray();
        return DispatchAdmissionChain.Eligible;
    }
}
