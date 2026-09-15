namespace ControlServer.Host.Runtime.Dispatch;

/// <summary>
/// Dispatch reason codes that more than one batch 4 ticket names, registered in one place.
/// </summary>
/// <remarks>
/// <para>
/// The codes that already exist stay on the criterion that returns them. These are here because the
/// ticket that returns one is not the only one that reads it: the slot group criterion (#73) returns
/// them, the structural classification (#74) sorts them, and the dashboard (#70) explains them to an
/// operator. Three spellings of one reason would make each of those silently miss the others.
/// </para>
/// <para>
/// Registered by control-server#69 before anything returns them. Nothing in the chain produces one of
/// these yet; a code that appears here with no caller is expected until #73 merges.
/// </para>
/// </remarks>
public static class DispatchReasonCodes
{
    /// <summary>
    /// The demand's slot group has fewer usable empty slots than it needs right now. Ordinary backlog: a
    /// slot freeing up or being re-enabled clears it.
    /// </summary>
    public const string SlotGroupCapacityTemporarilyUnavailable = "SLOT_GROUP_CAPACITY_TEMPORARILY_UNAVAILABLE";

    /// <summary>
    /// The demand needs more baskets than this vehicle's slot group has physical slots, whatever their
    /// state. One demand never spans groups (REQ-0352), so this vehicle can never carry it.
    /// </summary>
    public const string ExpectedBasketCountExceedsSlotGroup = "EXPECTED_BASKET_COUNT_EXCEEDS_SLOT_GROUP";

    /// <summary>
    /// Neither the vehicle's active slot configuration nor its latest published IO binding names a slot
    /// model, so which slot is in which group is unknown and the vehicle is not guessed at.
    /// </summary>
    public const string VehicleSlotModelUnresolved = "VEHICLE_SLOT_MODEL_UNRESOLVED";

    /// <summary>
    /// The area assignment table the round read gives this demand's AREA no slot group.
    /// </summary>
    public const string AreaSlotGroupNotAssigned = "AREA_SLOT_GROUP_NOT_ASSIGNED";
}
