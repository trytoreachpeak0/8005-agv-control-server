using ControlServer.Application;

namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// A single-demand journey as batch 7's tables hold it: a pickup stop, an unload stop and one demand, each carrying the ids
/// the journey row already has.
/// </summary>
/// <remarks>
/// The mapping is the one the batch 7 migration back-fills with in SQL (<c>Batch7MultiDemandJourneyPersistence.BackFill</c>),
/// column for column, so a journey accepted after the migration and one back-filled by it cannot be told apart. A change
/// here without the same change there breaks that, and
/// <c>Batch7JourneyAcceptanceTests.ANewlyAcceptedJourneyCannotBeToldApartFromTheSameJourneyBackFilledByTheMigration</c> says so.
/// </remarks>
internal static class SingleDemandJourneyShape
{
    internal static JourneyStopRow[] Stops(JourneyRuntimeRow journey) =>
    [
        new JourneyStopRow
        {
            StopId = JourneyIdentity.PickupStopId(journey.JourneyId),
            JourneyId = journey.JourneyId,
            Sequence = 1,
            StopRole = JourneyStopRoles.Pickup,
            StationId = journey.PickupStationId,
            StationRiotId = journey.PickupStationRiotId,
            DispatchZone = journey.DispatchZone,
            OperationSessionId = journey.OperationSessionId,
            MovementLegId = journey.PickupMovementLegId,
            UpperId = journey.PickupUpperId,
            VehicleBusinessMessageId = journey.VehicleBusinessMessageId,
            WorklistMessageId = journey.WorklistMessageId,
            PlanMessageId = journey.PlanMessageId,
            SublotRequestMessageId = journey.SublotRequestMessageId,
            DepartureSafetyCheckMessageId = journey.PreDepartureSafetyCheckMessageId,
            DepartureSafetyCheckId = journey.PreDepartureSafetyCheckId,
            Status = JourneyStopStatuses.Pending,
            CreatedAt = journey.CreatedAt
        },
        new JourneyStopRow
        {
            StopId = JourneyIdentity.UnloadStopId(journey.JourneyId),
            JourneyId = journey.JourneyId,
            Sequence = 2,
            StopRole = JourneyStopRoles.Unload,
            StationId = journey.TransportColumn(journey.GateStationId),
            StationRiotId = journey.GateStationRiotId,
            DispatchZone = journey.DispatchZone,
            OperationSessionId = journey.OperationSessionId,
            MovementLegId = journey.TransportColumn(journey.GateMovementLegId),
            UpperId = journey.TransportColumn(journey.GateUpperId),
            VehicleBusinessMessageId = journey.TransportColumn(journey.GateVehicleBusinessMessageId),
            WorklistMessageId = journey.TransportColumn(journey.GateWorklistMessageId),
            PlanMessageId = journey.TransportColumn(journey.GatePlanMessageId),
            Status = JourneyStopStatuses.Pending,
            CreatedAt = journey.CreatedAt
        },
    ];

    internal static JourneyDemandRow Demand(JourneyRuntimeRow journey) => new()
    {
        JourneyId = journey.JourneyId,
        DemandId = journey.TransportColumn(journey.DemandId),
        PickupStopId = JourneyIdentity.PickupStopId(journey.JourneyId),
        UnloadStopId = JourneyIdentity.UnloadStopId(journey.JourneyId),
        ExpectedBasketCount = journey.ExpectedBasketCount,
        TargetSlotsJson = journey.TransportColumn(journey.TargetSlotsJson),
        LoadSlotOperationAttemptId = journey.TransportColumn(journey.LoadSlotOperationAttemptId),
        LoadCommandMessageId = journey.TransportColumn(journey.LoadCommandMessageId),
        UnloadSlotOperationAttemptId = journey.TransportColumn(journey.UnloadSlotOperationAttemptId),
        UnloadCommandMessageId = journey.TransportColumn(journey.UnloadCommandMessageId),
        DispatchZone = journey.DispatchZone,
        DispatchGeneration = journey.DispatchGeneration,
        Status = JourneyDemandStatuses.PendingLoad,
        AddedAt = journey.CreatedAt
    };

    /// <summary>
    /// Whether a stored stop is the same stop: every identity the acceptance fixed. Status and sequence are left out -- the
    /// runtime moves them on later (control-server#208, #211), and a replayed acceptance is not a conflict for that.
    /// </summary>
    internal static bool SameStop(JourneyStopRow stored, JourneyStopRow expected) =>
        stored.StopId == expected.StopId &&
        stored.JourneyId == expected.JourneyId &&
        stored.StopRole == expected.StopRole &&
        stored.StationId == expected.StationId &&
        stored.StationRiotId == expected.StationRiotId &&
        stored.DispatchZone == expected.DispatchZone &&
        stored.OperationSessionId == expected.OperationSessionId &&
        stored.MovementLegId == expected.MovementLegId &&
        stored.UpperId == expected.UpperId &&
        stored.VehicleBusinessMessageId == expected.VehicleBusinessMessageId &&
        stored.WorklistMessageId == expected.WorklistMessageId &&
        stored.PlanMessageId == expected.PlanMessageId &&
        stored.SublotRequestMessageId == expected.SublotRequestMessageId &&
        stored.DepartureSafetyCheckMessageId == expected.DepartureSafetyCheckMessageId &&
        stored.DepartureSafetyCheckId == expected.DepartureSafetyCheckId &&
        stored.CreatedAt == expected.CreatedAt;

    /// <summary>
    /// Whether a stored membership is the same membership, on what the acceptance fixed. Status, loaded slots and removal
    /// are left out for the reason <see cref="SameStop"/> leaves status out.
    /// </summary>
    /// <remarks>
    /// <b>Target slots are left out too (REQ-0352, control-server#754).</b> Acceptance fixes only the basket count; the
    /// slots are assigned after the sublot entry and written onto this membership, while the journey row the expected
    /// membership is built from keeps the empty list acceptance wrote. Comparing them would read a replayed acceptance of
    /// a demand already scanned as a different acceptance. Two acceptances of one demand cannot differ in slots any more:
    /// both carry none, and <see cref="ExpectedBasketCount"/> still has to match.
    /// </remarks>
    internal static bool SameDemand(JourneyDemandRow stored, JourneyDemandRow expected) =>
        stored.JourneyId == expected.JourneyId &&
        stored.DemandId == expected.DemandId &&
        stored.PickupStopId == expected.PickupStopId &&
        stored.UnloadStopId == expected.UnloadStopId &&
        stored.ExpectedBasketCount == expected.ExpectedBasketCount &&
        stored.LoadSlotOperationAttemptId == expected.LoadSlotOperationAttemptId &&
        stored.LoadCommandMessageId == expected.LoadCommandMessageId &&
        stored.UnloadSlotOperationAttemptId == expected.UnloadSlotOperationAttemptId &&
        stored.UnloadCommandMessageId == expected.UnloadCommandMessageId &&
        stored.DispatchZone == expected.DispatchZone &&
        stored.DispatchGeneration == expected.DispatchGeneration &&
        stored.AddedAt == expected.AddedAt;
}
