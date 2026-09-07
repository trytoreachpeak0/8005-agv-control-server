using System.Text.Json.Serialization;

namespace ControlServer.FakeRiot;

/// <summary>
/// One vehicle as the two RIoT vehicle endpoints report it. The card
/// (<c>getVehicleInfoByDeviceKey</c>) and the execution facts (<c>getVehicleInfo</c>) are two
/// views of the same physical vehicle, so they live in one record: a scenario that moves the
/// vehicle must not be able to leave one view saying IDLE while the other says RUNNING.
/// </summary>
public sealed record FakeVehicle
{
    public required string DeviceKey { get; init; }
    public bool Enable { get; init; } = true;

    /// <summary>RIoT's connectivity code on the card. 1 is online; anything else is not.</summary>
    public int Status { get; init; } = 1;
    public string ProcState { get; init; } = "IDLE";
    public string CurrentMap { get; init; } = "老厂前线new";
    public int? CurrentPosition { get; init; }
    public int Battery { get; init; } = 80;
    public string BatteryState { get; init; } = "NO_CHARGE";
    public double Speed { get; init; }
    public int LockStatus { get; init; }
    public string? OrderTaskId { get; init; }

    /// <summary>MT_FINISHED (at rest), MT_RUNNING (moving) or MT_NA (unknown to RIoT).</summary>
    public string MovementState { get; init; } = "MT_FINISHED";
    public string ControlState { get; init; } = "CONTROL_STATE_OK";
    public string EmergencyState { get; init; } = "OK";
    public string BreakSwitchState { get; init; } = "MOVABLE";
    public string LocationState { get; init; } = "LOCATION_STATE_RUNNING";
    public bool ProcessingOrder { get; init; }
    public string IntegrationLevel { get; init; } = "ON_LINE";
}

/// <summary>
/// One order record. <c>ExecuteVehicleKey</c> is deliberately nullable and starts as RIoT's "--"
/// placeholder for a QUEUEING order: BC-ORDER-012 says RIoT reports that until it binds a vehicle,
/// and reading it as a real key has already broken the control server once.
/// </summary>
public sealed record FakeOrder
{
    public required long Id { get; init; }
    public required string OrderId { get; init; }
    public required string UpperId { get; init; }

    /// <summary>1 QUEUEING, 3 EXECUTING, 5 COMPLETE, 7/9 the other non-final states.</summary>
    public int OrderState { get; init; } = 1;
    public string? AppointVehicleKey { get; init; }
    public string? ExecuteVehicleKey { get; init; } = "--";
    public int? EndStationNo { get; init; }
    public required IReadOnlyList<FakeMission> Missions { get; init; }
}

public sealed record FakeMission(string Type, int MapId, int Destination);

/// <summary>
/// One station as <c>mapInfo/stations/{mapId}</c> reports it.
/// </summary>
/// <remarks>
/// Id and Name are what the catalog Facade reads; the rest is what the route-graph Facade reads,
/// and it is the same row on the wire. Round 43 measured the placement rule: a station sits on
/// <c>edge_id</c>, at whichever of that edge's two endpoints it is nearer, and
/// <c>station_offset</c> is zero on all 206 map25 stations and cannot be used for it.
/// </remarks>
public sealed record FakeStation(int Id, string Name)
{
    public int EdgeId { get; init; }
    public double PosX { get; init; }
    public double PosY { get; init; }
    public double PosYaw { get; init; }
    public int StationOffset { get; init; }
    public int Type { get; init; } = 1;
}

/// <summary>One directed edge as <c>mapInfo/edges/{mapId}</c> reports it.</summary>
public sealed record FakeEdge(int Id, int StartNode, int EndNode, double Cost)
{
    public int StartX { get; init; }
    public int StartY { get; init; }
    public int EndX { get; init; }
    public int EndY { get; init; }
    public double StartFacing { get; init; }
    public double EndFacing { get; init; }
    public int Direction { get; init; } = 1;
    public bool IsBackEdge { get; init; }
    public int Type { get; init; } = 1;
}

/// <summary>
/// One edge's membership in a named edge group, as <c>mapEdgeGroup/all</c> reports it.
/// </summary>
/// <remarks>
/// A group name spans Maps on the real RIoT — Round 43 saw 老厂电梯 in both map 14 and map 19 —
/// so MapId is part of the identity, not decoration.
/// </remarks>
public sealed record FakeEdgeGroup(string GroupName, int Id, int MapId, string MapName, int EdgeId, string Type)
{
    public string GmtCreate { get; init; } = "2024-06-26 10:57:58";
    public string GmtUpdate { get; init; } = "2024-06-26 10:57:58";
    public int IsDelete { get; init; }
}

/// <summary>
/// One order-command or emergency-service call, recorded and nothing else.
/// </summary>
/// <remarks>
/// The fake records the call and does not simulate its business consequence: an OrderHold does not
/// move the order to HELD here. A scenario asserts that the call was made, with these arguments,
/// this many times — which is what the reconciliation under test has to get right — and never that
/// the fake reacted the way the real RCS would.
/// </remarks>
public sealed record FakeCommandInvocation(
    string CommandType,
    string Target,
    string? ArgumentsJson,
    DateTimeOffset At);

/// <summary>How the RIoT data plane misbehaves. The control plane is never affected by it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FakeRiotFaultMode
{
    Normal,
    NoResponse,
    ServerError,
    Delay
}

/// <summary>Everything one test round can observe or drive.</summary>
public sealed record FakeRiotState
{
    public required IReadOnlyDictionary<string, FakeVehicle> Vehicles { get; init; }
    public required IReadOnlyDictionary<string, FakeOrder> OrdersByUpperId { get; init; }
    public required IReadOnlyDictionary<int, IReadOnlyList<FakeStation>> StationsByMapId { get; init; }

    /// <summary>Design-state edges per Map. Empty means the Map has no edge table to serve.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<FakeEdge>> EdgesByMapId { get; init; } =
        new Dictionary<int, IReadOnlyList<FakeEdge>>();

    /// <summary>Runtime removals per Map. Empty is the normal case — map25 had none in Round 43.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> RemovedEdgeIdsByMapId { get; init; } =
        new Dictionary<int, IReadOnlyList<int>>();

    public IReadOnlyDictionary<int, IReadOnlyList<int>> RemovedStationIdsByMapId { get; init; } =
        new Dictionary<int, IReadOnlyList<int>>();

    /// <summary>
    /// Edge groups across every Map, as the endpoint reports them. Empty by default, which is what
    /// map25 actually looks like: every group on the production RIoT belongs to another Map.
    /// </summary>
    public IReadOnlyList<FakeEdgeGroup> EdgeGroups { get; init; } = [];

    /// <summary>Every order-command and emergency-service call this round, oldest first.</summary>
    public IReadOnlyList<FakeCommandInvocation> CommandInvocations { get; init; } = [];

    /// <summary>
    /// Dynamic route cost, keyed however RIoT keys it. Empty by default, which is what every
    /// observation of the real endpoint has returned — Round 15 on the test RCS and Round 43 on
    /// the production one both answered <c>"result":{}</c>.
    /// </summary>
    /// <remarks>
    /// The route-graph engine reads this for presence only: a graph built while it was empty
    /// stops being a complete account of routing the moment it is not, and that makes the
    /// snapshot stale. A scenario that puts something here is exercising that trigger.
    /// </remarks>
    public IReadOnlyDictionary<string, double> DynamicRouteCosts { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>
    /// What <c>getRouteCostsBy</c> answers, keyed <c>"{mapId}:{stationId}"</c>. A station with no
    /// entry is answered with <see cref="DefaultRouteCostMm"/>; a negative value is RIoT's way of
    /// saying unreachable.
    /// </summary>
    /// <remarks>
    /// <b>The fake does not recompute a path cost from its own edge table, on purpose.</b> The
    /// pre-create gate compares reachability between its two evidence sources and deliberately does
    /// not compare the two magnitudes -- that they are the same quantity in the same unit is an
    /// inference nobody has measured -- so a derived number here would look more meaningful than it
    /// is. What a scenario needs to be able to drive is the sign, and a table drives it exactly.
    /// It is also the only way to make the two sources disagree: a computed cost would agree with
    /// the route graph by construction, and the disagreement path would be untestable at L2.
    /// </remarks>
    public IReadOnlyDictionary<string, long> RouteCostsByStation { get; init; } =
        new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>The answer for a station the round said nothing about. Reachable.</summary>
    public long DefaultRouteCostMm { get; init; } = 20000;
    public FakeRiotFaultMode FaultMode { get; init; } = FakeRiotFaultMode.Normal;
    public int DelayMs { get; init; }

    /// <summary>Serial number for the next created order, so orderIds are stable within a round.</summary>
    public required long NextOrderSequence { get; init; }
}
