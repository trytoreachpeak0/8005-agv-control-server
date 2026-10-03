namespace ControlServer.Domain;

/// <summary>One directed edge of the route graph, as the engine holds it.</summary>
/// <param name="EdgeId">RIoT's edge id, unique within the Map.</param>
/// <param name="StartNode">The edge runs StartNode → EndNode and only that way.</param>
/// <param name="CostMm">Traversal cost in mm. Never called RouteCost — see <see cref="RouteGraph"/>.</param>
public sealed record RouteGraphEdge(int EdgeId, int StartNode, int EndNode, long CostMm);

/// <summary>A station placed on a node of the graph.</summary>
public sealed record RouteGraphStation(int StationId, int Node);

/// <summary>
/// What the graph can say about getting from one station to another.
/// </summary>
/// <remarks>
/// <para>
/// Two facts, deliberately separate, because REQ-0207 treats them differently: an unconfirmed
/// <em>reachability</em> takes the vehicle out of the round, while a missing or stale
/// <em>cost</em> only takes that comparison layer out and leaves the vehicle in. Collapsing them
/// into one nullable number would lose that distinction at the first call site.
/// </para>
/// <para>
/// <see cref="TraversalCostMm"/> is <b>not</b> a RouteCost and must never be presented as one.
/// RIoT's RouteCost is a different fact from a different source, answering a different question;
/// CP-0001's revision of REQ-0298 keeps the two separately named on purpose.
/// </para>
/// </remarks>
public sealed record RouteGraphTraversal(bool Reachable, long TraversalCostMm)
{
    /// <summary>The answer for a pair the graph cannot connect.</summary>
    public static RouteGraphTraversal Unreachable { get; } = new(false, -1);
}

/// <summary>
/// A Map's route graph: a directed station-to-station graph built from RIoT's edge table, with
/// the runtime removals already applied.
/// </summary>
/// <remarks>
/// <para>
/// <b>Directed.</b> Round 43 measured map25 at 403 edges over 307 nodes with only 148 of those
/// edges having a reverse twin, so a shortest path computed as if the graph were undirected is
/// simply wrong — it claims reachability that does not exist.
/// </para>
/// <para>
/// <b>Nothing here is a RouteCost.</b> The graph answers "is B reachable from A, and at what
/// traversal cost" from the design-state edge table. RIoT's <c>getRouteCostsBy</c> answers "can
/// this vehicle get to that station right now" and is the only approved evidence for the
/// pre-create gate. The two are separate facts with separate names, and REQ-0207 forbids either
/// standing in for the other.
/// </para>
/// <para>
/// <b>No weak substitute exists in this type.</b> There is no straight-line distance, no
/// Euclidean fallback, no "approximately reachable". A pair the graph cannot connect returns
/// <see cref="RouteGraphTraversal.Unreachable"/>, and the caller fails closed.
/// </para>
/// </remarks>
public sealed class RouteGraph
{
    private readonly Dictionary<int, List<RouteGraphEdge>> _outgoingByNode;
    private readonly Dictionary<int, int> _nodeByStation;
    private readonly Dictionary<int, List<int>> _stationsByNode;

    private RouteGraph(
        Dictionary<int, List<RouteGraphEdge>> outgoingByNode,
        Dictionary<int, int> nodeByStation,
        Dictionary<int, List<int>> stationsByNode,
        int edgeCount)
    {
        _outgoingByNode = outgoingByNode;
        _nodeByStation = nodeByStation;
        _stationsByNode = stationsByNode;
        EdgeCount = edgeCount;
    }

    public int EdgeCount { get; }

    public int NodeCount => _outgoingByNode.Count;

    public int StationCount => _nodeByStation.Count;

    /// <summary>
    /// Builds the graph, dropping removed edges and removed stations.
    /// </summary>
    /// <remarks>
    /// A removed edge is not in the graph at all, and a removed station cannot be an endpoint —
    /// both are runtime facts from RIoT, and treating either as still present is how a vehicle
    /// gets sent down a path that no longer exists.
    /// </remarks>
    public static RouteGraph Build(
        IReadOnlyList<RouteGraphEdge> edges,
        IReadOnlyList<RouteGraphStation> stations,
        IReadOnlySet<int> removedEdgeIds,
        IReadOnlySet<int> removedStationIds)
    {
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(stations);
        ArgumentNullException.ThrowIfNull(removedEdgeIds);
        ArgumentNullException.ThrowIfNull(removedStationIds);

        Dictionary<int, List<RouteGraphEdge>> outgoing = [];
        int edgeCount = 0;
        foreach (RouteGraphEdge edge in edges)
        {
            if (removedEdgeIds.Contains(edge.EdgeId))
            {
                continue;
            }

            if (!outgoing.TryGetValue(edge.StartNode, out List<RouteGraphEdge>? fromStart))
            {
                fromStart = [];
                outgoing[edge.StartNode] = fromStart;
            }

            fromStart.Add(edge);
            edgeCount++;

            // Every node the graph knows about gets an entry, so a node with no outgoing edge is
            // still a node one can arrive at rather than a lookup miss.
            if (!outgoing.ContainsKey(edge.EndNode))
            {
                outgoing[edge.EndNode] = [];
            }
        }

        Dictionary<int, int> nodeByStation = [];
        Dictionary<int, List<int>> stationsByNode = [];
        foreach (RouteGraphStation station in stations)
        {
            if (removedStationIds.Contains(station.StationId))
            {
                continue;
            }

            nodeByStation[station.StationId] = station.Node;
            // One node can carry several stations, and every one of them is a routing endpoint:
            // TraversalCostsFrom answers by reading this reverse lookup, so a node that kept only
            // one station would make the others silently unreachable (control-server#431). Shared
            // nodes are an ordinary product of RouteGraphStationPlacement, which snaps each station
            // to the nearer end of its edge. Round 43 found none on map25 (206 stations onto 206
            // distinct nodes); map26, the map the v2 line runs on, was never measured for them.
            if (!stationsByNode.TryGetValue(station.Node, out List<int>? onNode))
            {
                onNode = [];
                stationsByNode[station.Node] = onNode;
            }

            onNode.Add(station.StationId);
        }

        return new RouteGraph(outgoing, nodeByStation, stationsByNode, edgeCount);
    }

    /// <summary>Whether the graph knows where this station sits.</summary>
    public bool KnowsStation(int stationId) => _nodeByStation.ContainsKey(stationId);

    /// <summary>
    /// Traversal from one station to another.
    /// </summary>
    /// <remarks>
    /// A station the graph does not know is <see cref="RouteGraphTraversal.Unreachable"/>, not an
    /// exception: "we cannot establish that this is reachable" and "there is no path" lead to the
    /// same fail-closed decision, and distinguishing them at this level would invite a caller to
    /// treat one of them as recoverable.
    /// </remarks>
    public RouteGraphTraversal Traverse(int fromStationId, int toStationId)
    {
        IReadOnlyDictionary<int, long> costs = TraversalCostsFrom(fromStationId);
        return costs.TryGetValue(toStationId, out long cost)
            ? new RouteGraphTraversal(true, cost)
            : RouteGraphTraversal.Unreachable;
    }

    /// <summary>
    /// Single-source shortest paths from one station to every station it can reach, keyed by
    /// station id.
    /// </summary>
    /// <remarks>
    /// One Dijkstra per origin rather than one per pair: a dispatch round asks about one vehicle
    /// against many candidates, so the origin is fixed and the destinations are many. An
    /// unreachable station is absent from the result rather than present with a sentinel — a
    /// sentinel is exactly the kind of value REQ-0207 forbids being compared as if it were a cost.
    /// </remarks>
    public IReadOnlyDictionary<int, long> TraversalCostsFrom(int fromStationId)
    {
        Dictionary<int, long> byStation = [];
        if (!_nodeByStation.TryGetValue(fromStationId, out int origin) ||
            !_outgoingByNode.ContainsKey(origin))
        {
            return byStation;
        }

        Dictionary<int, long> best = new() { [origin] = 0 };
        PriorityQueue<int, long> queue = new();
        queue.Enqueue(origin, 0);

        while (queue.TryDequeue(out int node, out long cost))
        {
            // A node can be queued more than once; the first time it comes out carries its best
            // cost, so later copies are stale and skipped.
            if (cost > best.GetValueOrDefault(node, long.MaxValue))
            {
                continue;
            }

            // Every station on the node is reached at the node's cost, including any that share
            // the origin's node: those come out at 0. That says only that both stations snapped
            // onto the same endpoint; with a large placement residual they need not stand at the
            // same physical spot.
            foreach (int stationId in _stationsByNode.GetValueOrDefault(node) ?? [])
            {
                byStation.TryAdd(stationId, cost);
            }

            foreach (RouteGraphEdge edge in _outgoingByNode.GetValueOrDefault(node) ?? [])
            {
                long next = cost + edge.CostMm;
                if (next >= best.GetValueOrDefault(edge.EndNode, long.MaxValue))
                {
                    continue;
                }

                best[edge.EndNode] = next;
                queue.Enqueue(edge.EndNode, next);
            }
        }

        return byStation;
    }
}
