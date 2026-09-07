namespace ControlServer.Application;

/// <summary>
/// RIoT's answer to "can this vehicle reach that station right now, and at what cost".
/// </summary>
/// <remarks>
/// <para>
/// <c>CostsMm</c> is RIoT's own number under RIoT's own name. <b>This is the only fact in the
/// server that may be called a RouteCost</b> — the self-built graph's station-to-station number is
/// <c>GraphTraversalCostMm</c>, and REQ-0207 turns on the two never being confused for each other.
/// </para>
/// <para>
/// A negative cost means unreachable and is a domain answer, not a failure: RIoT answered, and the
/// answer is no. A failure to get any answer at all is <c>null</c> from the port, which is a
/// different thing and blocks under its own verdict.
/// </para>
/// </remarks>
public sealed record RiotRouteCost(long CostsMm)
{
    public bool IsReachable => CostsMm >= 0;
}

/// <summary>
/// The pre-create gate's RIoT evidence source: <c>POST /api/task/v1/route/getRouteCostsBy</c>.
/// </summary>
/// <remarks>
/// <para>
/// In its own file rather than in <c>Ports.cs</c>, following the engine's port (ticket 12): that
/// file was held by ticket 06 so the capability lanes could land their tables together, and this
/// port belongs to one lane only. Its storage side does live there
/// (<see cref="ICatalogAvailabilityStore"/>).
/// </para>
/// <para>
/// The origin of the query is the vehicle's current position, which RIoT supplies itself — that is
/// exactly why this endpoint answers the pre-create question ("can <i>this vehicle</i> reach that
/// station") and cannot answer the selection question ("what does station A to station B cost"),
/// which the route graph answers instead. The two are not interchangeable and neither is a
/// fallback for the other.
/// </para>
/// </remarks>
public interface IRiotRouteCostProbe
{
    /// <summary>
    /// Reads the vehicle's RouteCost to one station, or null when RIoT could not be asked.
    /// </summary>
    /// <returns>
    /// The cost RIoT reported — possibly negative, meaning unreachable — or <c>null</c> when the
    /// call failed or returned nothing for this vehicle. Never throws for either case: the gate
    /// has to tell "unreachable" from "unknown", and an exception collapses them.
    /// </returns>
    Task<RiotRouteCost?> ReadRouteCostAsync(
        int mapId,
        int stationId,
        string vehicleKey,
        CancellationToken cancellationToken);
}
