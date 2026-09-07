namespace ControlServer.Application;

/// <summary>
/// What the engine reads from RIoT: the design state, the runtime removals, the edge groups, and
/// whether dynamic route cost is currently present.
/// </summary>
/// <remarks>
/// <para>
/// In its own file rather than in <c>Ports.cs</c> on purpose. Ticket 06 owns that file for the
/// batch so that the capability lanes can run in parallel, and this port belongs to one lane only
/// — the engine's. Its storage side does live in <c>Ports.cs</c>
/// (<see cref="IRouteGraphSnapshotStore"/>), because several lanes needed the tables landed
/// together. It sits in Application rather than Host because the adapter that implements it lives
/// in Infrastructure, and Infrastructure cannot reference Host.
/// </para>
/// <para>
/// Every method here goes through a named SDK Facade. Product code must not use <c>.Raw</c>
/// (REQ-0309), and the five endpoints behind these calls are exactly the ones CP-0001 approved —
/// no more.
/// </para>
/// </remarks>
public interface IRouteGraphSource
{
    /// <summary>The Map's directed edge table, design state.</summary>
    Task<IReadOnlyList<RouteGraphEdgeFact>> ReadEdgesAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>
    /// The Map's stations with their placement already resolved onto nodes.
    /// </summary>
    /// <remarks>
    /// Placement needs the edge table, so this is read after and against the same edges — a
    /// station resolved against a newer edge table than the one it will be routed over is a
    /// station on a node that may not exist.
    /// </remarks>
    Task<IReadOnlyList<RouteGraphStationFact>> ReadStationsAsync(
        int mapId,
        IReadOnlyList<RouteGraphEdgeFact> edges,
        CancellationToken cancellationToken);

    /// <summary>Edge ids currently removed. Empty is the normal answer.</summary>
    Task<IReadOnlyList<int>> ReadRemovedEdgeIdsAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>Station ids currently removed.</summary>
    Task<IReadOnlyList<int>> ReadRemovedStationIdsAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>
    /// This Map's edge groups. Empty means the Map has none — map25's actual state — which is a
    /// different thing from never having asked.
    /// </summary>
    Task<IReadOnlyList<RouteGraphEdgeGroupFact>> ReadEdgeGroupsAsync(int mapId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether RIoT currently reports any dynamic route cost.
    /// </summary>
    /// <remarks>
    /// Only presence, not the values. The engine does not consume dynamic cost; it only has to
    /// notice it appearing, because a graph built while it was empty stops being a complete
    /// account of routing the moment it is not.
    /// </remarks>
    Task<bool> ReadDynamicRouteCostPresentAsync(CancellationToken cancellationToken);
}
