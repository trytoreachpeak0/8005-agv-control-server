namespace ControlServer.Infrastructure.Persistence;

/// <summary>
/// The columns batch 8 (control-server#386) makes nullable in the database so that an idle return can be a journey without a
/// demand. Nothing writes a null into them yet: their CLR properties stay non-nullable.
/// </summary>
internal static class IdleReturnNullableColumns
{
    /// <summary>
    /// <c>JourneyRuntimes</c>: the anchor demand, and every column only a transport has -- the gate stop's leg and messages,
    /// the slots, the loading and unloading commands, the sublot request and the pre-departure check. An idle return has
    /// one leg, to a waiting point, which the pickup columns and the stop rows carry.
    /// </summary>
    internal static readonly string[] JourneyRuntimes =
    [
        nameof(JourneyRuntimeRow.DemandId),
        nameof(JourneyRuntimeRow.GateStationId),
        nameof(JourneyRuntimeRow.GateMovementLegId),
        nameof(JourneyRuntimeRow.GateUpperId),
        nameof(JourneyRuntimeRow.GateVehicleBusinessMessageId),
        nameof(JourneyRuntimeRow.GateWorklistMessageId),
        nameof(JourneyRuntimeRow.GatePlanMessageId),
        nameof(JourneyRuntimeRow.TargetSlotsJson),
        nameof(JourneyRuntimeRow.SublotRequestMessageId),
        nameof(JourneyRuntimeRow.LoadCommandMessageId),
        nameof(JourneyRuntimeRow.LoadSlotOperationAttemptId),
        nameof(JourneyRuntimeRow.PreDepartureSafetyCheckMessageId),
        nameof(JourneyRuntimeRow.PreDepartureSafetyCheckId),
        nameof(JourneyRuntimeRow.UnloadCommandMessageId),
        nameof(JourneyRuntimeRow.UnloadSlotOperationAttemptId),
    ];
}
