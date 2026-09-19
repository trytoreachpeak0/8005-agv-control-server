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
            StationId = journey.GateStationId,
            StationRiotId = journey.GateStationRiotId,
            DispatchZone = journey.DispatchZone,
            OperationSessionId = journey.OperationSessionId,
            MovementLegId = journey.GateMovementLegId,
            UpperId = journey.GateUpperId,
            VehicleBusinessMessageId = journey.GateVehicleBusinessMessageId,
            WorklistMessageId = journey.GateWorklistMessageId,
            PlanMessageId = journey.GatePlanMessageId,
            Status = JourneyStopStatuses.Pending,
            CreatedAt = journey.CreatedAt
        },
    ];

    internal static JourneyDemandRow Demand(JourneyRuntimeRow journey) => new()
    {
        JourneyId = journey.JourneyId,
        DemandId = journey.DemandId,
        PickupStopId = JourneyIdentity.PickupStopId(journey.JourneyId),
        UnloadStopId = JourneyIdentity.UnloadStopId(journey.JourneyId),
        ExpectedBasketCount = journey.ExpectedBasketCount,
        TargetSlotsJson = journey.TargetSlotsJson,
        LoadSlotOperationAttemptId = journey.LoadSlotOperationAttemptId,
        LoadCommandMessageId = journey.LoadCommandMessageId,
        UnloadSlotOperationAttemptId = journey.UnloadSlotOperationAttemptId,
        UnloadCommandMessageId = journey.UnloadCommandMessageId,
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
    internal static bool SameDemand(JourneyDemandRow stored, JourneyDemandRow expected) =>
        stored.JourneyId == expected.JourneyId &&
        stored.DemandId == expected.DemandId &&
        stored.PickupStopId == expected.PickupStopId &&
        stored.UnloadStopId == expected.UnloadStopId &&
        stored.ExpectedBasketCount == expected.ExpectedBasketCount &&
        stored.TargetSlotsJson == expected.TargetSlotsJson &&
        stored.LoadSlotOperationAttemptId == expected.LoadSlotOperationAttemptId &&
        stored.LoadCommandMessageId == expected.LoadCommandMessageId &&
        stored.UnloadSlotOperationAttemptId == expected.UnloadSlotOperationAttemptId &&
        stored.UnloadCommandMessageId == expected.UnloadCommandMessageId &&
        stored.DispatchZone == expected.DispatchZone &&
        stored.DispatchGeneration == expected.DispatchGeneration &&
        stored.AddedAt == expected.AddedAt;
}
