using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// The RouteGraphSnapshot engine: the graph itself, the two refresh cycles, the staleness rules,
/// and the reachability verdict the dispatch chain reads.
/// </summary>
/// <remarks>
/// The graph fixture is the shape Round 43 measured on map25, in miniature: directed, with only
/// some edges having a reverse twin. That property is the one a wrong implementation loses first,
/// and losing it means claiming reachability that does not exist.
/// </remarks>
public sealed class RouteGraphEngineTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    private const int MapId = 25;

    // 1 → 2 → 3 → 4 → 5, with 5 → 6 → 1 closing a loop, plus reverses for 1→2 and 2→3 only.
    // Node 1 holds station 11, node 3 station 12, node 5 station 210.
    private static readonly RouteGraphEdgeFact[] Edges =
    [
        Edge(1, 1, 2, 10000, 0, 0, 0, 10000),
        Edge(2, 2, 3, 10000, 0, 10000, 0, 20000),
        Edge(3, 3, 4, 10000, 0, 20000, 10000, 20000),
        Edge(4, 4, 5, 10000, 10000, 20000, 20000, 20000),
        Edge(5, 5, 6, 20000, 20000, 20000, 20000, 0),
        Edge(6, 6, 1, 20000, 20000, 0, 0, 0),
        Edge(7, 2, 1, 10000, 0, 10000, 0, 0),
        Edge(8, 3, 2, 10000, 0, 20000, 0, 10000),
    ];

    private static readonly RouteGraphStationFact[] Stations =
    [
        new(11, "C15-13", 1, 0, 0, 1, 0),
        new(12, "N1-3_N1-7", 2, 0, 20000, 3, 0),
        new(210, "关卡", 4, 20000, 20000, 5, 0),
    ];

    // ---- the graph ----------------------------------------------------------------------

    [Fact]
    public void TheGraphIsDirectedAndOnlyClaimsPathsThatExist()
    {
        RouteGraph graph = BuildGraph();

        // 11 (node 1) → 210 (node 5) runs 1→2→3→4→5.
        RouteGraphTraversal forward = graph.Traverse(11, 210);
        Assert.True(forward.Reachable);
        Assert.Equal(40000, forward.TraversalCostMm);

        // 210 (node 5) → 11 (node 1) has to go the long way round, 5→6→1, because 4→5 has no
        // reverse. An undirected reading would answer 40000 here and be wrong.
        RouteGraphTraversal backward = graph.Traverse(210, 11);
        Assert.True(backward.Reachable);
        Assert.Equal(40000, backward.TraversalCostMm);

        // 12 (node 3) → 11 (node 1) uses the two reverse edges that do exist.
        Assert.Equal(20000, graph.Traverse(12, 11).TraversalCostMm);
    }

    [Fact]
    public void ARemovedEdgeIsNotInTheGraphAtAll()
    {
        // Take out 3→4 and node 5 becomes unreachable from node 1: the loop only runs the other
        // way. This is the case a stale runtime state would get wrong.
        RouteGraph graph = RouteGraph.Build(
            Edges.Select(ToDomainEdge).ToList(),
            Stations.Select(ToDomainStation).ToList(),
            new HashSet<int> { 3 },
            new HashSet<int>());

        Assert.False(graph.Traverse(11, 210).Reachable);
        // And what is still connected still answers.
        Assert.True(graph.Traverse(11, 12).Reachable);
    }

    [Fact]
    public void ARemovedStationCannotBeAnEndpoint()
    {
        RouteGraph graph = RouteGraph.Build(
            Edges.Select(ToDomainEdge).ToList(),
            Stations.Select(ToDomainStation).ToList(),
            new HashSet<int>(),
            new HashSet<int> { 210 });

        Assert.False(graph.KnowsStation(210));
        Assert.False(graph.Traverse(11, 210).Reachable);
    }

    [Fact]
    public void AnUnknownStationIsUnreachableRatherThanAnException()
    {
        // "We cannot establish that this is reachable" and "there is no path" lead to the same
        // fail-closed decision; distinguishing them here would invite treating one as recoverable.
        RouteGraph graph = BuildGraph();

        Assert.False(graph.Traverse(11, 9999).Reachable);
        Assert.False(graph.Traverse(9999, 11).Reachable);
        Assert.Equal(-1, graph.Traverse(11, 9999).TraversalCostMm);
    }

    [Fact]
    public void SingleSourceCostsOmitUnreachableStationsInsteadOfSentinellingThem()
    {
        RouteGraph graph = RouteGraph.Build(
            Edges.Select(ToDomainEdge).ToList(),
            [.. Stations.Select(ToDomainStation), new RouteGraphStation(170, 99)],
            new HashSet<int>(),
            new HashSet<int>());

        IReadOnlyDictionary<int, long> costs = graph.TraversalCostsFrom(11);

        // Station 170 sits on a node no edge touches. A sentinel value for it is exactly what
        // REQ-0207 forbids being compared as if it were a cost, so it is simply absent.
        Assert.DoesNotContain(170, costs.Keys);
        Assert.Equal([11, 12, 210], costs.Keys.Order());
    }

    // ---- station placement ---------------------------------------------------------------

    [Fact]
    public void AStationResolvesOntoWhicheverEndOfItsEdgeIsNearer()
    {
        // Round 43: every map25 station sits at one end of its edge, never in between, and
        // station_offset is zero on all of them so it cannot be used for this.
        RouteGraphPlacement nearEnd = RouteGraphStationPlacement.Resolve(
            stationX: 0, stationY: 19_996, startNode: 2, startX: 0, startY: 10000,
            endNode: 3, endX: 0, endY: 20000);
        Assert.Equal(3, nearEnd.Node);
        Assert.Equal(4, nearEnd.ResidualMm);

        RouteGraphPlacement nearStart = RouteGraphStationPlacement.Resolve(
            stationX: 0, stationY: 10000, startNode: 2, startX: 0, startY: 10000,
            endNode: 3, endX: 0, endY: 20000);
        Assert.Equal(2, nearStart.Node);
        Assert.Equal(0, nearStart.ResidualMm);
    }

    // ---- the two refresh cycles ------------------------------------------------------------

    [Fact]
    public async Task TheDesignCycleRefreshesOnItsTtlWhileTheRuntimeCycleRefreshesEveryTick()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        await fixture.Refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Source.EdgeReads);
        Assert.Equal(1, fixture.Source.RemovedEdgeReads);

        // A tick inside the TTL: runtime again, design not.
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        await fixture.Refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Source.EdgeReads);
        Assert.Equal(2, fixture.Source.RemovedEdgeReads);

        // Past the TTL backstop: the design state is re-read even though nothing said it changed.
        fixture.Clock.Advance(TimeSpan.FromMinutes(11));
        await fixture.Refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Source.EdgeReads);
        Assert.Equal(3, fixture.Source.RemovedEdgeReads);
    }

    [Fact]
    public async Task AFreshlyRefreshedSnapshotIsUsableAndCarriesTheGraph()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);

        RouteGraphAvailability availability = await fixture.Access.ReadAsync(
            TestContext.Current.CancellationToken);

        Assert.True(availability.IsUsable);
        Assert.Equal(8, availability.Graph!.EdgeCount);
        Assert.Equal(3, availability.Graph.StationCount);
        Assert.Equal(40000, availability.Graph.Traverse(11, 210).TraversalCostMm);
    }

    // ---- the staleness rules ---------------------------------------------------------------

    [Fact]
    public async Task ASnapshotNobodyHasFetchedIsStaleRatherThanAnEmptyGraph()
    {
        await using Fixture fixture = await Fixture.CreateAsync();

        RouteGraphAvailability availability = await fixture.Access.ReadAsync(
            TestContext.Current.CancellationToken);

        Assert.False(availability.IsUsable);
        Assert.Equal(RouteGraphStaleReasons.NeverRefreshed, availability.StaleReason);
        Assert.Null(availability.Graph);
    }

    [Fact]
    public async Task TheRuntimeStateGoingStaleBlocksEvenThoughTheDesignStateIsFine()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);

        // Past the runtime budget but well inside the design TTL: the removals are what aged out.
        fixture.Clock.Advance(TimeSpan.FromSeconds(60));

        RouteGraphAvailability availability = await fixture.Access.ReadAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(RouteGraphStaleReasons.RuntimeStateExpired, availability.StaleReason);
    }

    [Fact]
    public async Task AChangedEdgeGroupFingerprintMakesTheSnapshotStale()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.True((await fixture.Access.ReadAsync(TestContext.Current.CancellationToken)).IsUsable);

        // map25 has no edge groups at all, so the first fingerprint is empty — and the engine must
        // not read that as a change. One appearing is a change.
        fixture.Source.EdgeGroups = [new RouteGraphEdgeGroupFact("老厂电梯", 8, "SINGLE_VEHICLE_ONLY")];
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);

        RouteGraphAvailability availability = await fixture.Access.ReadAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(RouteGraphStaleReasons.EdgeGroupFingerprintChanged, availability.StaleReason);
    }

    [Fact]
    public async Task AnEmptyEdgeGroupListOnTheFirstFetchIsNotAChange()
    {
        // The regression this guards: treating "no groups" as a fingerprint change would make the
        // engine stale on its very first tick, on the Map the project actually runs on.
        await using Fixture fixture = await Fixture.CreateAsync();

        await fixture.Refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);

        Assert.True((await fixture.Access.ReadAsync(TestContext.Current.CancellationToken)).IsUsable);
    }

    [Fact]
    public async Task DynamicRouteCostAppearingMakesTheSnapshotStale()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.True((await fixture.Access.ReadAsync(TestContext.Current.CancellationToken)).IsUsable);

        // The graph is built assuming no dynamic cost is in play. The moment there is some, it is
        // no longer a complete account of routing.
        fixture.Source.DynamicRouteCostPresent = true;
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);

        RouteGraphAvailability availability = await fixture.Access.ReadAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(RouteGraphStaleReasons.DynamicRouteCostAppeared, availability.StaleReason);
    }

    [Fact]
    public async Task ARefreshThatThrowsMarksTheSnapshotStaleInsteadOfLeavingTheOldOneStanding()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await fixture.Refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);
        Assert.True((await fixture.Access.ReadAsync(TestContext.Current.CancellationToken)).IsUsable);

        fixture.Source.FailNextRuntimeRead = true;
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Refresher.RefreshOnceAsync(TestContext.Current.CancellationToken);

        // A graph that was right five seconds ago is not evidence about now.
        RouteGraphAvailability availability = await fixture.Access.ReadAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(RouteGraphStaleReasons.RefreshFailed, availability.StaleReason);
    }

    [Fact]
    public void TheStalenessVerdictIsPureAndCoversEveryTrigger()
    {
        RouteGraphOptions options = new()
        {
            Enabled = true,
            MapId = MapId,
            DesignStateTtl = TimeSpan.FromMinutes(10),
            RuntimeRefreshPeriod = TimeSpan.FromSeconds(10),
            RuntimeStateMaxAge = TimeSpan.FromSeconds(45),
        };

        Assert.Equal(
            RouteGraphStaleReasons.NeverRefreshed,
            RouteGraphRefresher.StaleReason(null, Origin, options));

        RouteGraphSnapshotHeader fresh = Header(Origin, Origin, staleReason: null);
        Assert.Null(RouteGraphRefresher.StaleReason(fresh, Origin.AddSeconds(5), options));

        Assert.Equal(
            RouteGraphStaleReasons.DesignStateExpired,
            RouteGraphRefresher.StaleReason(fresh, Origin.AddMinutes(11), options));

        Assert.Equal(
            RouteGraphStaleReasons.RuntimeStateExpired,
            RouteGraphRefresher.StaleReason(
                Header(Origin, Origin.AddMinutes(-2), staleReason: null), Origin, options));

        // These two do not age out; they are the world having changed under the graph, and only a
        // refresh that re-establishes agreement clears them.
        Assert.Equal(
            RouteGraphStaleReasons.EdgeGroupFingerprintChanged,
            RouteGraphRefresher.StaleReason(
                Header(Origin, Origin, RouteGraphStaleReasons.EdgeGroupFingerprintChanged),
                Origin.AddSeconds(1),
                options));
    }

    // ---- the dispatch verdict ---------------------------------------------------------------

    [Fact]
    public void TheOptionsValidatorRefusesABudgetThatIsNotLargerThanItsPeriod()
    {
        RouteGraphOptionsValidator validator = new();

        // A maximum age at or below the period means the state is stale the instant it is written.
        Assert.False(validator.Validate(null, new RouteGraphOptions
        {
            Enabled = true,
            MapId = MapId,
            RuntimeRefreshPeriod = TimeSpan.FromSeconds(10),
            RuntimeStateMaxAge = TimeSpan.FromSeconds(10),
        }).Succeeded);

        Assert.True(validator.Validate(null, new RouteGraphOptions
        {
            Enabled = true,
            MapId = MapId,
            RuntimeRefreshPeriod = TimeSpan.FromSeconds(10),
            RuntimeStateMaxAge = TimeSpan.FromSeconds(45),
        }).Succeeded);

        // Off is off: nothing to validate, and no accidental block on a deployment not using it.
        Assert.True(validator.Validate(null, new RouteGraphOptions { Enabled = false }).Succeeded);
    }

    private static RouteGraphSnapshotHeader Header(
        DateTimeOffset designRefreshedAt,
        DateTimeOffset runtimeRefreshedAt,
        string? staleReason) => new(
            MapId,
            DesignRevision: 1,
            designRefreshedAt,
            DesignSourceGmtUpdate: null,
            DesignEdgeCount: 8,
            DesignStationCount: 3,
            runtimeRefreshedAt,
            RuntimeRemovedEdgeCount: 0,
            RuntimeRemovedStationCount: 0,
            EdgeGroupFingerprint: "",
            EdgeGroupRefreshedAt: designRefreshedAt,
            DynamicRouteCostPresent: false,
            DynamicRouteCostObservedAt: designRefreshedAt,
            staleReason,
            StaleSince: staleReason is null ? null : designRefreshedAt);

    private static RouteGraphEdgeFact Edge(
        int id, int startNode, int endNode, double cost, int sx, int sy, int ex, int ey) =>
        new(id, startNode, endNode, cost, sx, sy, ex, ey, 1, false);

    private static RouteGraphEdge ToDomainEdge(RouteGraphEdgeFact fact) =>
        new(fact.EdgeId, fact.StartNode, fact.EndNode, (long)fact.CostMm);

    private static RouteGraphStation ToDomainStation(RouteGraphStationFact fact) =>
        new(fact.StationId, fact.ResolvedNode);

    private static RouteGraph BuildGraph() => RouteGraph.Build(
        Edges.Select(ToDomainEdge).ToList(),
        Stations.Select(ToDomainStation).ToList(),
        new HashSet<int>(),
        new HashSet<int>());

    /// <summary>
    /// A clock the test moves by hand. The repository has several fixed-time providers already;
    /// this one advances, because the two refresh cycles are told apart by elapsed time.
    /// </summary>
    private sealed class AdvanceableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _current = now;

        public void Advance(TimeSpan by) => _current += by;

        public override DateTimeOffset GetUtcNow() => _current;
    }

    /// <summary>A source that counts its reads, so the two cycles can be told apart.</summary>
    private sealed class CountingSource : IRouteGraphSource
    {
        public int EdgeReads { get; private set; }

        public int RemovedEdgeReads { get; private set; }

        public IReadOnlyList<RouteGraphEdgeGroupFact> EdgeGroups { get; set; } = [];

        public bool DynamicRouteCostPresent { get; set; }

        public bool FailNextRuntimeRead { get; set; }

        public Task<IReadOnlyList<RouteGraphEdgeFact>> ReadEdgesAsync(int mapId, CancellationToken ct)
        {
            EdgeReads++;
            return Task.FromResult<IReadOnlyList<RouteGraphEdgeFact>>(Edges);
        }

        public Task<IReadOnlyList<RouteGraphStationFact>> ReadStationsAsync(
            int mapId, IReadOnlyList<RouteGraphEdgeFact> edges, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RouteGraphStationFact>>(Stations);

        public Task<IReadOnlyList<int>> ReadRemovedEdgeIdsAsync(int mapId, CancellationToken ct)
        {
            RemovedEdgeReads++;
            if (FailNextRuntimeRead)
            {
                FailNextRuntimeRead = false;
                throw new HttpRequestException("RIoT is unreachable.");
            }

            return Task.FromResult<IReadOnlyList<int>>([]);
        }

        public Task<IReadOnlyList<int>> ReadRemovedStationIdsAsync(int mapId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<int>>([]);

        public Task<IReadOnlyList<RouteGraphEdgeGroupFact>> ReadEdgeGroupsAsync(int mapId, CancellationToken ct) =>
            Task.FromResult(EdgeGroups);

        public Task<bool> ReadDynamicRouteCostPresentAsync(CancellationToken ct) =>
            Task.FromResult(DynamicRouteCostPresent);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ControlServerDbContext _context;

        private Fixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            CountingSource source,
            AdvanceableTimeProvider clock,
            RouteGraphRefresher refresher,
            RouteGraphAccess access)
        {
            _connection = connection;
            _context = context;
            Source = source;
            Clock = clock;
            Refresher = refresher;
            Access = access;
        }

        public CountingSource Source { get; }

        public AdvanceableTimeProvider Clock { get; }

        public RouteGraphRefresher Refresher { get; }

        public RouteGraphAccess Access { get; }

        public static async Task<Fixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> dbOptions =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(dbOptions);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

            CountingSource source = new();
            AdvanceableTimeProvider clock = new(Origin);
            IOptions<RouteGraphOptions> options = Options.Create(new RouteGraphOptions
            {
                Enabled = true,
                MapId = MapId,
                DesignStateTtl = TimeSpan.FromMinutes(10),
                RuntimeRefreshPeriod = TimeSpan.FromSeconds(10),
                RuntimeStateMaxAge = TimeSpan.FromSeconds(45),
            });
            RouteGraphSnapshotStore store = new(context);

            return new Fixture(
                connection,
                context,
                source,
                clock,
                new RouteGraphRefresher(
                    source, store, options, clock, NullLogger<RouteGraphRefresher>.Instance),
                new RouteGraphAccess(store, options, clock));
        }

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
