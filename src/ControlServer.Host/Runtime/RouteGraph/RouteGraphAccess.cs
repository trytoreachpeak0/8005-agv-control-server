using ControlServer.Application;
using ControlServer.Domain;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.RouteGraph;

/// <summary>
/// What a dispatch round gets to ask the engine.
/// </summary>
/// <remarks>
/// Either a usable graph or a reason it is not usable — never a graph with a caveat. The caller
/// cannot accidentally route over a stale snapshot because a stale snapshot does not come with a
/// graph attached.
/// </remarks>
public sealed record RouteGraphAvailability(Domain.RouteGraph? Graph, string? StaleReason)
{
    public bool IsUsable => Graph is not null && StaleReason is null;

    public static RouteGraphAvailability Stale(string reason) => new(null, reason);
}

/// <summary>
/// Reads the persisted snapshot, judges it against the clock, and builds the in-memory graph.
/// </summary>
/// <remarks>
/// <para>
/// Fail-closed at every step: no snapshot, an expired one, a fingerprint change or dynamic cost
/// appearing all produce a <see cref="RouteGraphAvailability"/> with no graph and a reason. There
/// is no path through this class that answers a routing question from data it has judged unusable.
/// </para>
/// <para>
/// The graph is rebuilt per read rather than cached. map25 is 403 edges and 206 stations, so the
/// build is trivial next to the round's own I/O, and a cache would need an invalidation rule that
/// could disagree with the staleness rule — two answers to "is this current" is one too many.
/// </para>
/// </remarks>
public sealed class RouteGraphAccess(
    IRouteGraphSnapshotStore store,
    IOptions<RouteGraphOptions> options,
    TimeProvider timeProvider)
{
    private readonly RouteGraphOptions _options = options.Value;

    public bool Enabled => _options.Enabled;

    public int MapId => _options.MapId;

    public async Task<RouteGraphAvailability> ReadAsync(CancellationToken cancellationToken)
    {
        RouteGraphState? state = await store
            .ReadStateAsync(_options.MapId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return RouteGraphAvailability.Stale(RouteGraphStaleReasons.NeverRefreshed);
        }

        // The persisted flag and the clock are both consulted. The flag carries what only the
        // refresher can know (a fingerprint changed); the clock carries what only now can know
        // (the snapshot aged out since it was written).
        string? reason = state.Header.StaleReason
            ?? RouteGraphRefresher.StaleReason(state.Header, timeProvider.GetUtcNow(), _options);
        if (reason is not null)
        {
            return RouteGraphAvailability.Stale(reason);
        }

        Domain.RouteGraph graph = Domain.RouteGraph.Build(
            state.Edges
                .Select(edge => new RouteGraphEdge(
                    edge.EdgeId,
                    edge.StartNode,
                    edge.EndNode,
                    // Costs are compared and summed as integers: a shortest path decided on
                    // floating-point sums is not reproducible from the evidence afterwards, and
                    // sub-millimetre precision has no meaning for a vehicle.
                    (long)Math.Round(edge.CostMm)))
                .ToList(),
            state.Stations
                .Select(station => new RouteGraphStation(station.StationId, station.ResolvedNode))
                .ToList(),
            state.RemovedEdgeIds,
            state.RemovedStationIds);

        return new RouteGraphAvailability(graph, null);
    }
}
