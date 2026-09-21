using System.Text.RegularExpressions;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// Where the engine meets the dispatch chain: the reachability criterion (a gate) and the cost
/// ranker (an ordering), which REQ-0207 deliberately treats differently.
/// </summary>
public sealed class RouteGraphDispatchTests
{
    /// <summary>
    /// The fleet policy the round carries. The reachability criterion under test does not read it,
    /// so an empty one states plainly that these tests configure no fleet.
    /// </summary>
    private static readonly VehicleDispatchPolicy EmptyPolicy =
        new([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY");

    private static readonly DateTimeOffset Origin = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    private const int MapId = 25;

    // Node 1 → 2 → 3, one way only. Station 11 on node 1, station 12 on node 3.
    private static readonly RouteGraphEdgeFact[] Edges =
    [
        new(1, 1, 2, 10000, 0, 0, 0, 10000, 1, false),
        new(2, 2, 3, 10000, 0, 10000, 0, 20000, 1, false),
    ];

    private static readonly RouteGraphStationFact[] Stations =
    [
        new(11, "C15-13", 1, 0, 0, 1, 0),
        new(12, "N1-3_N1-7", 2, 0, 20000, 3, 0),
    ];

    // ---- the criterion is a gate ------------------------------------------------------------

    [Fact]
    public async Task AReachablePickupPassesAndCarriesItsCostForTheRanker()
    {
        await using Fixture fixture = await Fixture.CreateAsync(seeded: true);
        DispatchCandidateEvaluation evaluation = Evaluation(vehicleAtStation: 11, pickupStation: 12);

        string reason = await new RouteGraphReachabilityCriterion(fixture.Access)
            .EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        Assert.Equal(DispatchAdmissionChain.Eligible, reason);
        // Reachability is the gate; the cost rides along for the ordering that comes after.
        Assert.Equal(20000, evaluation.GraphTraversalCostMm);
    }

    [Fact]
    public async Task AnUnreachablePickupTakesTheVehicleOutOfTheRound()
    {
        // REQ-0207: 无法确认车辆到下一站可达时，该车退出本轮候选。The graph runs 1→2→3 only, so
        // going back the other way is not a longer path — it is no path.
        await using Fixture fixture = await Fixture.CreateAsync(seeded: true);
        DispatchCandidateEvaluation evaluation = Evaluation(vehicleAtStation: 12, pickupStation: 11);

        string reason = await new RouteGraphReachabilityCriterion(fixture.Access)
            .EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        Assert.Equal("ROUTE_GRAPH_PICKUP_UNREACHABLE", reason);
        Assert.Null(evaluation.GraphTraversalCostMm);
    }

    [Fact]
    public async Task AnUnknownVehiclePositionFailsClosed()
    {
        // Not knowing where the vehicle is, is not the same as it being anywhere.
        await using Fixture fixture = await Fixture.CreateAsync(seeded: true);
        DispatchCandidateEvaluation evaluation = Evaluation(vehicleAtStation: null, pickupStation: 12);

        string reason = await new RouteGraphReachabilityCriterion(fixture.Access)
            .EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        Assert.Equal("ROUTE_GRAPH_VEHICLE_POSITION_UNKNOWN", reason);
    }

    [Fact]
    public async Task AStaleSnapshotBlocksWithTheEnginesOwnReason()
    {
        // The specification asks for exactly this: the block must be traceable to the engine
        // rather than showing up as a generic "no candidate".
        await using Fixture fixture = await Fixture.CreateAsync(seeded: false);
        DispatchCandidateEvaluation evaluation = Evaluation(vehicleAtStation: 11, pickupStation: 12);

        string reason = await new RouteGraphReachabilityCriterion(fixture.Access)
            .EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        Assert.Equal(RouteGraphStaleReasons.NeverRefreshed, reason);
    }

    [Fact]
    public async Task TheEngineBeingOffLeavesDispatchExactlyAsItWas()
    {
        await using Fixture fixture = await Fixture.CreateAsync(seeded: false, enabled: false);
        DispatchCandidateEvaluation evaluation = Evaluation(vehicleAtStation: 11, pickupStation: 12);

        string reason = await new RouteGraphReachabilityCriterion(fixture.Access)
            .EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        // Off is a deployment that has not adopted the engine, not a routing verdict.
        Assert.Equal(DispatchAdmissionChain.Eligible, reason);
        Assert.Null(evaluation.GraphTraversalCostMm);
    }

    // ---- 成本是排序，不是门；批次7-06 起它在车辆侧 ------------------------------------------------

    // 批次7-06（control-server#211）把轮次翻成任务优先之后，成本比的是「哪辆车接这条任务更便宜」，
    // 所以这四条从任务侧搬到了车辆侧。REQ-0207 的两句话一字未变：可达性是门、成本是序，算不出成本的车不被淘汰。
    // 比较的量也从「这辆车到取货站多远」换成了边际成本（REQ-0206），单趟候选下两者是同一个数。

    [Fact]
    public void TheCheapestReachableVehicleWins()
    {
        // 离得近的那辆车接走它：成本层在做它该做的事。没有路网引擎时，会由确定性的兜底层按车号定。
        Assert.Equal(
            "agv-near",
            DispatchVehicleOrdering.SelectNext([Offer("agv-far", 90000), Offer("agv-near", 10000)]).Vehicle.AgvId);
    }

    [Fact]
    public void AMissingCostDropsTheCostLayerRatherThanTheVehicle()
    {
        // REQ-0207's second half: 保留相关车辆、对该比较组跳过路径成本层。两辆都算不出，于是落到确定性的兜底层。
        Assert.Equal(
            "agv-a",
            DispatchVehicleOrdering.SelectNext([Offer("agv-b", null), Offer("agv-a", null)]).Vehicle.AgvId);
    }

    [Fact]
    public void APricedVehicleIsPreferredOverAnUnpricedOne()
    {
        // 走到排序这一步的车都已经过了可达性那道门，所以算不出成本不等于到不了——只是没量出来，
        // 而量出来的比没量出来的优先。
        Assert.Equal(
            "agv-priced",
            DispatchVehicleOrdering.SelectNext([Offer("agv-unpriced", null), Offer("agv-priced", 90000)]).Vehicle.AgvId);
    }

    [Fact]
    public void EqualCostsFallThroughToTheDeterministicTieBreak()
    {
        // 成本相同、别的也分不出：按车号定，所以这个决定能从证据里复现，而不取决于枚举顺序。
        Assert.Equal(
            "agv-a",
            DispatchVehicleOrdering.SelectNext([Offer("agv-b", 10000), Offer("agv-a", 10000)]).Vehicle.AgvId);
    }

    /// <summary>一辆车对某条任务的出价，只填排序层读的那几样。</summary>
    private static EligibleVehicleOffer Offer(string agvId, long? marginalCostMm) => new(
        new FleetVehicle(agvId, $"VK-{agvId}", 1),
        new DispatchVehicleFacts(
            $"VK-{agvId}",
            agvId,
            new OnboardDispatchFacts(1, [1], true, true, true, true, false),
            new RiotVehicleObservation(
                $"VK-{agvId}", true, true, "IDLE", "MAP-25-WIRE_TO_GATE", 12, 90, "DISCHARGING", 0, Origin),
            Origin),
        Candidate("DEMAND-1", Origin, marginalCostMm),
        marginalCostMm,
        Placement: null,
        DispatchZoneParameterVersion: null);

    // ---- the naming discipline -----------------------------------------------------------------

    [Fact]
    public void TheSelfBuiltGraphNeverCallsItsOutputARouteCost()
    {
        // REQ-0207 and CP-0001's REQ-0298 revision both turn on the two facts staying separately
        // named. A grep is the only thing that keeps that true as the code grows: nothing stops
        // someone naming a variable riotRouteCost inside the engine.
        string[] engineFiles =
        [
            .. Directory.GetFiles(SourcePath("src/ControlServer.Domain"), "RouteGraph*.cs"),
            .. Directory.GetFiles(SourcePath("src/ControlServer.Host/Runtime/RouteGraph"), "*.cs"),
            // 批次7-06（control-server#211）把成本从任务侧搬到车辆侧：原来那两个任务侧的层
            // （PricedBeforeUnpricedLayer、GraphTraversalCostLayer）删掉了，读遍历代价的代码现在在这两处。
            SourcePath("src/ControlServer.Host/Runtime/Dispatch/DispatchVehicleOrdering.cs"),
            SourcePath("src/ControlServer.Host/Runtime/Dispatch/EnRouteAppendPlanner.cs"),
            SourcePath("src/ControlServer.Host/Runtime/Dispatch/Criteria/RouteGraphReachabilityCriterion.cs"),
            SourcePath("src/ControlServer.Host/Runtime/Dispatch/Criteria/EnRouteAppendCriterion.cs"),
        ];

        List<string> offenders = [];
        foreach (string file in engineFiles)
        {
            foreach (string line in File.ReadAllLines(file))
            {
                // Prose may name RouteCost — it has to, to say what this is not. Code may not.
                string code = Regex.Replace(line, @"^\s*(///|//).*$", string.Empty);
                if (code.Contains("RouteCost", StringComparison.Ordinal) &&
                    !code.Contains("DynamicRouteCost", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoStraightLineFallbackExistsInTheEngine()
    {
        // REQ-0207: 禁止用直线距离或其它弱替代证明可达. The one place Euclidean distance is
        // legitimate is placing a station on the edge RIoT already told us it sits on — which is
        // not a reachability claim — so that file is the single allowed exception.
        string[] files =
        [
            .. Directory.GetFiles(SourcePath("src/ControlServer.Host/Runtime/RouteGraph"), "*.cs"),
            SourcePath("src/ControlServer.Domain/RouteGraph.cs"),
        ];

        List<string> offenders = [];
        foreach (string file in files)
        {
            string body = File.ReadAllText(file);
            if (body.Contains("Math.Sqrt", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.Empty(offenders);
    }

    private static string SourcePath(string relative)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
            {
                return Path.Combine(directory.FullName, relative);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the ControlServer repository root.");
    }

    private static DispatchCandidateEvaluation Evaluation(int? vehicleAtStation, int pickupStation)
    {
        AcceptedDemandSnapshot candidate = Snapshot("DEMAND-1", Origin);
        DispatchRoundFacts round = new(
            new DemandCatalogSnapshot("EPOCH", 0, [candidate]),
            new RiotMapStationCatalogSnapshot(MapId, Origin, "sha", []),
            new SingleStationView(new RiotMapStation(pickupStation, "N1-3_N1-7")),
            new HashSet<string>(),
            Origin,
            EmptyPolicy);

        DispatchVehicleFacts vehicle = new(
            "BROKERX-TEST-0001",
            "AGV-TEST-0001",
            new OnboardDispatchFacts(1, [1, 2], true, true, true, true, false),
            new RiotVehicleObservation(
                "BROKERX-TEST-0001", true, true, "IDLE", "MAP", vehicleAtStation, 80, "NO_CHARGE",
                0, Origin, 0, null),
            Origin);

        return new DispatchCandidateEvaluation(candidate, round, vehicle)
        {
            Route = new ResolvedJourneyRoute("ZONE", "EVIDENCE", "N1-3_N1-7", pickupStation, "关卡", 210, FixedTaskStationResolution.Resolved("WIRE_TO_GATE", FixedStationEnd.Destination, new RiotMapStation(210, "关卡"))),
        };
    }

    private static EligibleDispatchCandidate Candidate(string demandId, DateTimeOffset firstSeen, long? cost) =>
        new(Snapshot(demandId, firstSeen), new ResolvedJourneyRoute("ZONE", "EVIDENCE", "N1-3_N1-7", 12, "关卡", 210, FixedTaskStationResolution.Resolved("WIRE_TO_GATE", FixedStationEnd.Destination, new RiotMapStation(210, "关卡"))),
            1, [1], firstSeen, cost);

    private static AcceptedDemandSnapshot Snapshot(string demandId, DateTimeOffset createdAt) => new(
        demandId,
        TransportDemandKey: demandId,
        DemandRevision: 0,
        HistoryEpoch: "EPOCH",
        CatalogRevision: 0,
        AcceptedAt: createdAt,
        SeriesId: "SERIES",
        WorkType: "WIRE_TO_GATE",
        Sublot: "SUBLOT",
        CreatedAt: createdAt);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ControlServerDbContext _context;

        private Fixture(SqliteConnection connection, ControlServerDbContext context, RouteGraphAccess access)
        {
            _connection = connection;
            _context = context;
            Access = access;
        }

        public RouteGraphAccess Access { get; }

        public static async Task<Fixture> CreateAsync(bool seeded, bool enabled = true)
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> dbOptions =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(dbOptions);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

            RouteGraphSnapshotStore store = new(context);
            if (seeded)
            {
                await store.ReplaceDesignStateAsync(
                    MapId, Edges, Stations, null, Origin, TestContext.Current.CancellationToken);
                await store.ReplaceRuntimeStateAsync(
                    MapId, [], [], Origin, TestContext.Current.CancellationToken);
                await store.ReplaceEdgeGroupsAsync(
                    MapId, [], "", Origin, TestContext.Current.CancellationToken);
                await store.ClearStaleAsync(MapId, Origin, TestContext.Current.CancellationToken);
            }

            IOptions<RouteGraphOptions> options = Options.Create(new RouteGraphOptions
            {
                Enabled = enabled,
                MapId = MapId,
                DesignStateTtl = TimeSpan.FromMinutes(10),
                RuntimeRefreshPeriod = TimeSpan.FromSeconds(10),
                RuntimeStateMaxAge = TimeSpan.FromSeconds(45),
            });

            return new Fixture(connection, context, new RouteGraphAccess(store, options, new FixedClock(Origin)));
        }

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
