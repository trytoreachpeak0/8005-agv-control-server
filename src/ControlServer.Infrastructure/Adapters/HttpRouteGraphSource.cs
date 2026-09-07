using ControlServer.Application;
using ControlServer.Domain;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace ControlServer.Infrastructure.Adapters;

/// <summary>
/// Reads the route graph from RIoT through the five named Facades CP-0001 approved.
/// </summary>
/// <remarks>
/// <para>
/// Every call goes through a named Facade — <c>.Raw</c> appears nowhere in <c>src/</c> and this
/// adapter does not introduce the first use. The five endpoints are exactly the ones REQ-0146
/// lists after CP-0001's revision; <c>removedEdgeDetail</c> is not among them and is not called.
/// </para>
/// <para>
/// Station placement happens here rather than in the engine because it needs the edge table that
/// was just read: a station resolved against a different edge table than the one it will be routed
/// over can land on a node the graph does not contain.
/// </para>
/// </remarks>
public sealed class HttpRouteGraphSource(RiotSession riotSession) : IRouteGraphSource
{
    public async Task<IReadOnlyList<RouteGraphEdgeFact>> ReadEdgesAsync(
        int mapId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapId);

        IReadOnlyList<MapEdge> edges = await riotSession.Maps
            .ListEdgesAsync(mapId, cancellationToken).ConfigureAwait(false);

        return edges
            .Select(edge => new RouteGraphEdgeFact(
                edge.Id,
                edge.StartNode,
                edge.EndNode,
                edge.CostMm,
                edge.StartX,
                edge.StartY,
                edge.EndX,
                edge.EndY,
                edge.Direction,
                edge.IsBackEdge))
            .ToList();
    }

    public async Task<IReadOnlyList<RouteGraphStationFact>> ReadStationsAsync(
        int mapId,
        IReadOnlyList<RouteGraphEdgeFact> edges,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapId);
        ArgumentNullException.ThrowIfNull(edges);

        IReadOnlyList<MapStationDetail> stations = await riotSession.Maps
            .ListStationDetailsAsync(mapId, cancellationToken).ConfigureAwait(false);

        Dictionary<int, RouteGraphEdgeFact> edgesById = edges.ToDictionary(edge => edge.EdgeId);
        List<RouteGraphStationFact> resolved = [];
        foreach (MapStationDetail station in stations)
        {
            if (!edgesById.TryGetValue(station.EdgeId, out RouteGraphEdgeFact? edge))
            {
                // A station naming an edge the table does not contain cannot be placed. Dropping
                // it keeps it off the graph, which is the fail-closed answer: a station the engine
                // cannot place is a station it must not claim to reach.
                continue;
            }

            RouteGraphPlacement placement = RouteGraphStationPlacement.Resolve(
                station.PosX,
                station.PosY,
                edge.StartNode,
                edge.StartX,
                edge.StartY,
                edge.EndNode,
                edge.EndX,
                edge.EndY);

            resolved.Add(new RouteGraphStationFact(
                station.StationId,
                station.Name,
                station.EdgeId,
                station.PosX,
                station.PosY,
                placement.Node,
                placement.ResidualMm));
        }

        return resolved;
    }

    public async Task<IReadOnlyList<int>> ReadRemovedEdgeIdsAsync(int mapId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapId);

        IReadOnlyList<RemovedEdge> removed = await riotSession.Maps
            .ListRemovedEdgesAsync(mapId, cancellationToken).ConfigureAwait(false);
        return removed.Select(edge => edge.EdgeId).ToList();
    }

    public async Task<IReadOnlyList<int>> ReadRemovedStationIdsAsync(int mapId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapId);

        IReadOnlyList<RemovedStation> removed = await riotSession.Maps
            .ListRemovedStationsAsync(mapId, cancellationToken).ConfigureAwait(false);
        return removed.Select(station => station.StationId).ToList();
    }

    public async Task<IReadOnlyList<RouteGraphEdgeGroupFact>> ReadEdgeGroupsAsync(
        int mapId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapId);

        IReadOnlyList<MapEdgeGroup> groups = await riotSession.Maps
            .ListEdgeGroupsAsync(cancellationToken).ConfigureAwait(false);

        // The endpoint covers every Map, so this filter is not optional. An empty result for this
        // Map is a legitimate answer — map25 has no edge groups at all.
        return groups
            .Where(group => group.MapId == mapId)
            .Select(group => new RouteGraphEdgeGroupFact(group.GroupName, group.EdgeId, group.Type))
            .ToList();
    }

    public async Task<bool> ReadDynamicRouteCostPresentAsync(CancellationToken cancellationToken)
    {
        // Presence only. The engine does not consume dynamic cost; it has to notice it appearing,
        // because a graph built while it was empty stops being a complete account of routing.
        DynamicRouteCostPresence presence = await riotSession.Tasks
            .ReadDynamicRouteCostPresenceAsync(cancellationToken).ConfigureAwait(false);
        return presence.Present;
    }
}
