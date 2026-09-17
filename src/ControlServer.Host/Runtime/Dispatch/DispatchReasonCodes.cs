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

    /// <summary>
    /// <b>Silent.</b> The area assignment table the round read does not name this demand's AREA, so this
    /// server does not execute it (REQ-0191). The table is the only execution whitelist; there is no prefix
    /// rule behind it.
    /// </summary>
    /// <remarks>
    /// Not a fault and not an alarm: eutectic and low-temperature eutectic AREAs, among others, are kept out of
    /// execution precisely by leaving them unmapped, and they stay in the plant-wide projection. So the demand
    /// is only written to <c>JourneyBacklog</c>, never raised as a structural dispatch block, and never logged
    /// at Warning or above. Listed in <see cref="Silent"/>, which is what the structural classification
    /// (control-server#74) excludes.
    /// </remarks>
    public const string OutOfScopeArea = "OUT_OF_SCOPE_AREA";

    /// <summary>
    /// <b>Not waiting.</b> The demand was in the backlog unaccepted and the latest catalog the round read no
    /// longer lists it: MES closed or withdrew it before this server took it. Nothing is waiting for a vehicle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written by the dispatch round, not by an admission criterion, and only from a catalog that was read
    /// successfully -- the MesIngest catalog is the complete list of open demands, so absence from it is a
    /// fact, whereas a failed read says nothing. The row is kept rather than deleted, and its
    /// <c>LastSeenAt</c> keeps the last time the demand was actually in the catalog. A demand that comes back
    /// is judged again like any other and keeps its <c>FirstSeenAt</c>.
    /// </para>
    /// <para>
    /// Anything that lists waiting backlog (the dashboard, control-server#70) leaves these rows out; REQ-0210's
    /// "展示等待状态" is about demands still waiting. A structural dispatch block on such a demand is cleared by
    /// its own rule (control-server#74), not by this code.
    /// </para>
    /// </remarks>
    public const string DemandLeftCatalog = "DEMAND_LEFT_CATALOG";

    /// <summary>
    /// The reasons that are a configured outcome rather than a problem: they reach the backlog and nothing
    /// else — no structural dispatch block, no alarm, no log at Warning or above.
    /// </summary>
    public static IReadOnlySet<string> Silent { get; } =
        new HashSet<string>(StringComparer.Ordinal) { OutOfScopeArea };

    /// <summary>Whether <paramref name="reasonCode"/> is one of <see cref="Silent"/>.</summary>
    public static bool IsSilent(string reasonCode) => Silent.Contains(reasonCode);
}
