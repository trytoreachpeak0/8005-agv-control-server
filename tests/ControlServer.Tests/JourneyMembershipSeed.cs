using ControlServer.Application;
using ControlServer.Infrastructure.Persistence;

namespace ControlServer.Tests;

/// <summary>
/// The demand membership (<c>JourneyDemands</c>) for fixtures that write a journey row by hand.
/// </summary>
/// <remarks>
/// Acceptance writes one beside every journey row (control-server#206), and since control-server#207 every
/// "which journey carries this demand" read goes through it. A hand-written journey without it is a database the
/// server can no longer produce, so fixtures that seed journeys directly add this row too. The anchor's mapping is the
/// one <c>SingleDemandJourneyShape.Demand</c> writes on acceptance.
/// </remarks>
internal static class JourneyMembershipSeed
{
    /// <summary>The anchor demand's membership, exactly as acceptance writes it.</summary>
    internal static JourneyDemandRow For(JourneyRuntimeRow journey) => Member(journey, journey.DemandId);

    /// <summary>
    /// A further demand in the same journey. Batch 7's tables allow it; nothing in the runtime creates one yet
    /// (control-server#208 and later), so tests that need a two-demand journey write it here. Its per-demand ids are
    /// its own; the anchor keeps the journey row's.
    /// </summary>
    internal static JourneyDemandRow Member(JourneyRuntimeRow journey, string demandId)
    {
        string Own(string id) => demandId == journey.DemandId ? id : $"{id}:{demandId}";
        return new JourneyDemandRow
        {
            JourneyId = journey.JourneyId,
            DemandId = demandId,
            PickupStopId = JourneyIdentity.PickupStopId(journey.JourneyId),
            UnloadStopId = JourneyIdentity.UnloadStopId(journey.JourneyId),
            ExpectedBasketCount = journey.ExpectedBasketCount,
            TargetSlotsJson = journey.TargetSlotsJson,
            LoadSlotOperationAttemptId = Own(journey.LoadSlotOperationAttemptId),
            LoadCommandMessageId = Own(journey.LoadCommandMessageId),
            UnloadSlotOperationAttemptId = Own(journey.UnloadSlotOperationAttemptId),
            UnloadCommandMessageId = Own(journey.UnloadCommandMessageId),
            DispatchZone = journey.DispatchZone,
            DispatchGeneration = journey.DispatchGeneration,
            Status = JourneyDemandStatuses.PendingLoad,
            AddedAt = journey.CreatedAt
        };
    }
}
