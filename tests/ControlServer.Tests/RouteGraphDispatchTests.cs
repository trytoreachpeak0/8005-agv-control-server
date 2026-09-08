using System.Text.RegularExpressions;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.Dispatch;
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

    // ---- the ranker is an ordering ------------------------------------------------------------

    [Fact]
    public void TheCheapestReachableCandidateWins()
    {
        RouteGraphCostRanker ranker = new();

        EligibleDispatchCandidate near = Candidate("DEMAND-NEAR", Origin, cost: 10000);
        EligibleDispatchCandidate far = Candidate("DEMAND-FAR", Origin.AddMinutes(-10), cost: 90000);

        // The older demand loses to the nearer one: this is the cost layer doing its job. Without
        // the engine the first-seen order would have picked DEMAND-FAR.
        Assert.Equal("DEMAND-NEAR", ranker.SelectNext([far, near]).Snapshot.DemandId);
    }

    [Fact]
    public void AMissingCostDropsTheCostLayerRatherThanTheCandidate()
    {
        // REQ-0207's second half: 保留相关车辆、对该比较组跳过路径成本层。Nothing is priced here,
        // so the round falls through to the deterministic first-seen order.
        RouteGraphCostRanker ranker = new();

        EligibleDispatchCandidate older = Candidate("DEMAND-OLD", Origin.AddMinutes(-10), cost: null);
        EligibleDispatchCandidate newer = Candidate("DEMAND-NEW", Origin, cost: null);

        Assert.Equal("DEMAND-OLD", ranker.SelectNext([newer, older]).Snapshot.DemandId);
    }

    [Fact]
    public void APricedCandidateIsPreferredOverAnUnpricedOne()
    {
        RouteGraphCostRanker ranker = new();

        EligibleDispatchCandidate priced = Candidate("DEMAND-PRICED", Origin, cost: 90000);
        EligibleDispatchCandidate unpriced = Candidate("DEMAND-UNPRICED", Origin.AddMinutes(-10), cost: null);

        // Every candidate here already passed the reachability gate, so an unpriced one is not
        // unreachable — it is unmeasured, and a measured comparison beats an unmeasured one.
        Assert.Equal("DEMAND-PRICED", ranker.SelectNext([unpriced, priced]).Snapshot.DemandId);
    }

    [Fact]
    public void EqualCostsFallThroughToTheDeterministicTieBreak()
    {
        RouteGraphCostRanker ranker = new();

        EligibleDispatchCandidate first = Candidate("DEMAND-B", Origin, cost: 10000);
        EligibleDispatchCandidate second = Candidate("DEMAND-A", Origin, cost: 10000);

        // Same cost, same timestamps: the demand id breaks it, so the decision is reproducible
        // from the evidence rather than depending on enumeration order.
        Assert.Equal("DEMAND-A", ranker.SelectNext([first, second]).Snapshot.DemandId);
    }

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
            SourcePath("src/ControlServer.Host/Runtime/Dispatch/RouteGraphCostRanker.cs"),
            SourcePath("src/ControlServer.Host/Runtime/Dispatch/Criteria/RouteGraphReachabilityCriterion.cs"),
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
            new RiotMapStation(pickupStation, "N1-3_N1-7"),
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
            Route = new ResolvedJourneyRoute("ZONE", "EVIDENCE", "N1-3_N1-7", pickupStation),
        };
    }

    private static EligibleDispatchCandidate Candidate(string demandId, DateTimeOffset firstSeen, long? cost) =>
        new(Snapshot(demandId, firstSeen), new ResolvedJourneyRoute("ZONE", "EVIDENCE", "N1-3_N1-7", 12),
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
