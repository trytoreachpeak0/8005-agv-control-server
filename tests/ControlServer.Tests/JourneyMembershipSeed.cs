using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>
    /// Makes <paramref name="journey"/> a two-demand journey: a further accepted demand, a copy of the anchor's with its own
    /// id, business key and sublot, in <paramref name="status"/>, and its membership. Saved.
    /// </summary>
    internal static async Task<AcceptedDemandRow> AddFurtherDemandAsync(
        ControlServerDbContext context,
        JourneyRuntimeRow journey,
        string demandId,
        DemandExecutionStatus status = DemandExecutionStatus.Accepted)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        AcceptedDemandRow anchor = await context.AcceptedDemands.AsNoTracking()
            .SingleAsync(row => row.DemandId == journey.DemandId, cancellationToken);
        AcceptedDemandRow further = new()
        {
            DemandId = demandId,
            SeriesId = $"SERIES-{demandId}",
            TransportDemandKey = $"SUBLOT-{demandId}|{anchor.WorkType}",
            WorkType = anchor.WorkType,
            Sublot = $"SUBLOT-{demandId}",
            Generation = anchor.Generation,
            DemandRevision = anchor.DemandRevision,
            HistoryEpoch = anchor.HistoryEpoch,
            CatalogRevision = anchor.CatalogRevision,
            CreatedAt = anchor.CreatedAt,
            ValueObservedAt = anchor.ValueObservedAt,
            ValuePollTraceId = $"TRACE-{demandId}",
            ValueProjectionCommitId = $"COMMIT-{demandId}",
            LiveMesFieldsJson = anchor.LiveMesFieldsJson,
            AcceptedAt = anchor.AcceptedAt,
            Status = status
        };
        context.AcceptedDemands.Add(further);
        context.Set<JourneyDemandRow>().Add(Member(journey, demandId));
        await context.SaveChangesAsync(cancellationToken);
        return further;
    }
}
