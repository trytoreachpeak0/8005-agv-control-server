namespace ControlServer.FakeRiot;

/// <summary>
/// The safe initial state every round starts from, bound from configuration. Defaults reproduce
/// the 2026-09-03 field vehicle and map so a scenario that says nothing still faces the shape the
/// control server was configured against: map 25, gate station 210, one bound vehicle at rest.
/// </summary>
public sealed class FakeRiotSeed
{
    public string VehicleKey { get; set; } = "BROKERX-0c20ff0600d644869a6a80c186065d85";
    public string MapIdentity { get; set; } = "老厂前线new";
    public int MapId { get; set; } = 25;
    public int StartStationId { get; set; } = 210;
    public int BatteryPercent { get; set; } = 80;
    public string BatteryState { get; set; } = "NO_CHARGE";

    /// <summary>
    /// Station id to name, as <c>/api/imap/v1/mapInfo/stations/{mapId}</c> reports it. The names
    /// are load bearing: MapStationResolver picks the pickup station by parsing the area out of
    /// them, and requires the gate station's name to match GateStationId exactly.
    /// </summary>
    public Dictionary<string, string> Stations { get; set; } = new(StringComparer.Ordinal)
    {
        ["210"] = "关卡",
        ["12"] = "N1-3_N1-7",
        ["11"] = "C15-13"
    };

    /// <summary>
    /// Vehicle keys beyond <see cref="VehicleKey"/>. Empty by default, so a scenario that says
    /// nothing faces exactly the single-vehicle fleet every existing scenario was written against.
    /// A multi-vehicle scenario lists the extra keys here and gets them at rest on the same Map.
    /// </summary>
    public List<string> AdditionalVehicleKeys { get; set; } = [];

    /// <summary>
    /// Which node each station sits on, keyed by station id. Together with <see cref="Nodes"/> this
    /// is the whole route-graph seed: the station's coordinates are its node's, which is what makes
    /// the placement rule resolve exactly.
    /// </summary>
    public Dictionary<string, int> StationNodes { get; set; } = new(StringComparer.Ordinal)
    {
        ["11"] = 1,
        ["12"] = 3,
        ["210"] = 5,
        // 批次7-10（control-server#215，看板例外第 13 条）：派工待送站与第三个机台站的节点。默认站表里没有这两个站，
        // 所以不替换站表的场景一个字都不变；替换站表、把它们列进来的场景才用得上（见 PlaceStations）。
        ["305"] = 2,
        ["13"] = 4
    };

    /// <summary>Node id to (x, y) in mm.</summary>
    public Dictionary<string, int[]> Nodes { get; set; } = new(StringComparer.Ordinal)
    {
        ["1"] = [0, 0],
        ["2"] = [0, 10000],
        ["3"] = [0, 20000],
        ["4"] = [10000, 20000],
        ["5"] = [20000, 20000],
        ["6"] = [20000, 0]
    };

    /// <summary>
    /// Directed edges as "edgeId:startNode:endNode". Cost is the Euclidean distance between the two
    /// nodes, which is what Round 43 measured Edge.cost to be on the real Map.
    /// </summary>
    /// <remarks>
    /// Deliberately not symmetric: only two of the six forward edges have a reverse twin, so a
    /// consumer that treats the graph as undirected reaches stations it should not be able to.
    /// map25 is the same shape -- 403 edges, only 148 with a reverse.
    /// </remarks>
    public List<string> Edges { get; set; } =
    [
        "1:1:2", "2:2:3", "3:3:4", "4:4:5", "5:5:6", "6:6:1", "7:2:1", "8:3:2"
    ];

    /// <summary>
    /// Edge ids currently removed from the Map. Empty by default -- that is map25's real state, and
    /// the state a staleness rule has to read as "nothing removed" rather than "not fetched".
    /// </summary>
    public List<int> RemovedEdgeIds { get; set; } = [];

    public List<int> RemovedStationIds { get; set; } = [];

    /// <summary>
    /// What <c>getRouteCostsBy</c> answers per station, keyed <c>"{mapId}:{stationId}"</c>.
    /// Empty by default, so every station answers reachable and a scenario that says nothing sees
    /// a fleet that can get where it is sent. A negative value is RIoT's "unreachable".
    /// </summary>
    public Dictionary<string, long> RouteCosts { get; set; } = new(StringComparer.Ordinal);

    public FakeRiotState BuildInitialState()
    {
        FakeVehicle vehicle = new()
        {
            DeviceKey = VehicleKey,
            CurrentMap = MapIdentity,
            CurrentPosition = StartStationId,
            Battery = BatteryPercent,
            BatteryState = BatteryState
        };
        Dictionary<int, int[]> nodes = Nodes.ToDictionary(
            pair => int.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture),
            pair => pair.Value);

        FakeEdge[] edges = Edges
            .Select(spec => spec.Split(':'))
            .Select(parts => BuildEdge(
                int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                nodes))
            .OrderBy(edge => edge.Id)
            .ToArray();

        FakeStation[] stations = Stations
            .Select(pair => BuildStation(
                int.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture),
                pair.Value,
                nodes,
                edges))
            .OrderBy(station => station.Id)
            .ToArray();

        Dictionary<string, FakeVehicle> vehicles = new(StringComparer.Ordinal)
        {
            [VehicleKey] = vehicle
        };
        foreach (string extra in AdditionalVehicleKeys.Where(key => !string.IsNullOrWhiteSpace(key)))
        {
            vehicles[extra] = vehicle with { DeviceKey = extra };
        }

        return new FakeRiotState
        {
            Vehicles = vehicles,
            OrdersByUpperId = new Dictionary<string, FakeOrder>(StringComparer.Ordinal),
            StationsByMapId = new Dictionary<int, IReadOnlyList<FakeStation>>
            {
                [MapId] = stations
            },
            EdgesByMapId = new Dictionary<int, IReadOnlyList<FakeEdge>>
            {
                [MapId] = edges
            },
            RemovedEdgeIdsByMapId = new Dictionary<int, IReadOnlyList<int>>
            {
                [MapId] = RemovedEdgeIds
            },
            RemovedStationIdsByMapId = new Dictionary<int, IReadOnlyList<int>>
            {
                [MapId] = RemovedStationIds
            },
            RouteCostsByStation = new Dictionary<string, long>(RouteCosts, StringComparer.Ordinal),
            NextOrderSequence = 1
        };
    }

    /// <summary>
    /// 场景替换站表时建出的站：<see cref="StationNodes"/> 里有节点的放到节点上，与初始建表同一条规则；没有的不在路网上
    /// （批次7-10，control-server#215）。
    /// </summary>
    /// <remarks>
    /// 在这之前替换站表一律建出不带坐标的站，连原有的站也一起掉出路网——「换了站表又开路网」的场景里任何路径代价都算不出。
    /// 边取这张图此刻的边（<paramref name="edges"/>），节点坐标取 seed：替换站表不改路网本身。
    /// </remarks>
    internal FakeStation[] PlaceStations(IEnumerable<KeyValuePair<int, string>> stations, IReadOnlyList<FakeEdge> edges)
    {
        Dictionary<int, int[]> nodes = Nodes.ToDictionary(
            pair => int.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture),
            pair => pair.Value);
        return [.. stations
            .Select(pair => BuildStation(pair.Key, pair.Value, nodes, edges))
            .OrderBy(station => station.Id)];
    }

    private static FakeEdge BuildEdge(int id, int startNode, int endNode, Dictionary<int, int[]> nodes)
    {
        int[] start = nodes[startNode];
        int[] end = nodes[endNode];
        double cost = Math.Sqrt(
            Math.Pow(end[0] - start[0], 2) + Math.Pow(end[1] - start[1], 2));
        return new FakeEdge(id, startNode, endNode, Math.Round(cost, 1))
        {
            StartX = start[0],
            StartY = start[1],
            EndX = end[0],
            EndY = end[1],
        };
    }

    /// <summary>
    /// Places a station on its node and points it at an edge touching that node.
    /// </summary>
    /// <remarks>
    /// The station's coordinates are its node's exactly, so the placement rule -- nearer of the
    /// edge's two endpoints -- resolves with zero residual. Round 43 measured a maximum residual of
    /// 4 mm on the real Map, so exactness here is a simplification, not a different rule.
    /// </remarks>
    private FakeStation BuildStation(
        int stationId,
        string name,
        Dictionary<int, int[]> nodes,
        IReadOnlyList<FakeEdge> edges)
    {
        if (!StationNodes.TryGetValue(
                stationId.ToString(System.Globalization.CultureInfo.InvariantCulture), out int node) ||
            !nodes.TryGetValue(node, out int[]? position))
        {
            // A station with no node is still a catalog station; it simply is not on the graph.
            return new FakeStation(stationId, name);
        }

        FakeEdge? edge = edges.FirstOrDefault(candidate => candidate.EndNode == node)
            ?? edges.FirstOrDefault(candidate => candidate.StartNode == node);
        return new FakeStation(stationId, name)
        {
            EdgeId = edge?.Id ?? 0,
            PosX = position[0],
            PosY = position[1],
        };
    }
}
