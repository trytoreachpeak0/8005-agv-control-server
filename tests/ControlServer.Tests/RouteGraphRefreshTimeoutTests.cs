using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using ControlServer.Host.Runtime.RouteGraph;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static ControlServer.Tests.Batch7StopDrivenAdvanceDriver;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;

namespace ControlServer.Tests;

/// <summary>
/// control-server#656：路网刷新遇到 RIoT 读超时（<c>HttpClient.Timeout</c> 抛的 <see cref="TaskCanceledException"/>，调用方没要求取消），
/// 按刷新失败处理——标 stale、派车被挡——而这一轮全车队的引擎照常跑；真正的停机取消照旧让 worker 退出。
/// </summary>
/// <remarks>
/// <para>
/// <b>修之前</b>：<c>RouteGraphRefresher</c> 的 catch 只认「不是 <see cref="OperationCanceledException"/>」，超时与停机一起漏出去，路网不标 stale；
/// worker 里刷新与 <c>engine.ExecuteOnceAsync</c> 同在一个 try，超时落进通用 catch（2002），整轮引擎跳过——每台车的推进、故障模型的
/// 升级与急停、急停确认与 REQ-0248 重触发、仓门监看、派车，全都停在这一轮。地图接口一直超时，就一直停着。
/// </para>
/// <para>
/// 两层各钉一半。刷新器这一层（前三条）钉住「超时标 stale、派车被挡；停机原样抛出、不标 stale」，以及 worker 拆开之后那条安全前提：
/// 刷新在打标记之前就出了事，旧快照也会因为年龄被 <see cref="RouteGraphAccess"/> 判为 stale。worker 这一层（后三条）跑真的
/// <see cref="JourneyRuntimeWorker"/>，用一张 FAILED、车还在动的取货单作这一轮必须照常发生的监看：同一轮记故障、发 Hold、升级急停。
/// </para>
/// </remarks>
public sealed class RouteGraphRefreshTimeoutTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 7, 8, 0, 0, TimeSpan.Zero);
    private const int MapId = 25;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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

    private static RouteGraphOptions GraphOptions() => new()
    {
        Enabled = true,
        MapId = MapId,
        DesignStateTtl = TimeSpan.FromMinutes(10),
        RuntimeRefreshPeriod = TimeSpan.FromSeconds(10),
        RuntimeStateMaxAge = TimeSpan.FromSeconds(45),
    };

    // ---- the refresher ------------------------------------------------------------------------------------------

    /// <summary>
    /// 一次正常刷新之后，下一次读封路表超时（调用方没取消）：<c>RefreshOnceAsync</c> 不抛，快照标为 <c>ROUTE_GRAPH_REFRESH_FAILED</c>，
    /// 可达性判据以这个原因挡住派车——不是拿五秒前的路网接着派。
    /// </summary>
    [Fact]
    public async Task ARefreshThatTimesOutIsAFailedRefreshAndDispatchIsBlocked()
    {
        await using GraphFixture fixture = await GraphFixture.CreateAsync();
        await fixture.Refresher.RefreshOnceAsync(Token);
        Assert.Equal(DispatchAdmissionChain.Eligible, await fixture.ReachabilityAsync());

        fixture.Source.Mode = SourceMode.TimeOut;
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await fixture.Refresher.RefreshOnceAsync(Token);

        Assert.Equal(RouteGraphStaleReasons.RefreshFailed, (await fixture.Store.ReadHeaderAsync(MapId, Token))?.StaleReason);
        Assert.Equal(RouteGraphStaleReasons.RefreshFailed, await fixture.ReachabilityAsync());
        Assert.Contains(fixture.Log.Entries, entry => entry.EventId.Id == 1);
    }

    /// <summary>
    /// 停机：读封路表时调用方的取消令牌已经取消，源抛出的 <see cref="TaskCanceledException"/> 原样离开刷新器，
    /// 不记「刷新失败」，也不标 stale——停机不是刷新失败。
    /// </summary>
    [Fact]
    public async Task AShutdownCancellationLeavesTheRefresherUnmarked()
    {
        await using GraphFixture fixture = await GraphFixture.CreateAsync();
        await fixture.Refresher.RefreshOnceAsync(Token);

        using CancellationTokenSource shutdown = new();
        fixture.Source.Mode = SourceMode.CancelCaller;
        fixture.Source.OnRuntimeRead = shutdown.Cancel;
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Refresher.RefreshOnceAsync(shutdown.Token));

        Assert.Null((await fixture.Store.ReadHeaderAsync(MapId, Token))?.StaleReason);
        Assert.DoesNotContain(fixture.Log.Entries, entry => entry.EventId.Id == 1);
    }

    /// <summary>
    /// worker 在刷新失败后照跑引擎，靠的是这条前提（<c>RouteGraphAccess.ReadAsync</c> 用时钟重判）：刷新在打标记之前就出了事、库里的标记
    /// 仍是空的，快照也会在 <c>RuntimeStateMaxAge</c> 之后被判为 stale，派车照样被挡。没有它，一次逃出刷新器的失败会让旧路网一直可用。
    /// </summary>
    [Fact]
    public async Task AnUnmarkedSnapshotStillAgesOutOfDispatch()
    {
        await using GraphFixture fixture = await GraphFixture.CreateAsync();
        await fixture.Refresher.RefreshOnceAsync(Token);

        // No refresh at all from here on, as if every later one escaped before it could mark anything.
        fixture.Clock.Advance(TimeSpan.FromSeconds(46));

        Assert.Null((await fixture.Store.ReadHeaderAsync(MapId, Token))?.StaleReason);
        Assert.Equal(RouteGraphStaleReasons.RuntimeStateExpired, await fixture.ReachabilityAsync());
    }

    // ---- the worker ---------------------------------------------------------------------------------------------

    /// <summary>
    /// 地图接口超时的那一轮，真 worker 照常跑引擎：FAILED、车还在动的取货单同一轮记下故障、发 Hold、升级急停；路网标为
    /// <c>ROUTE_GRAPH_REFRESH_FAILED</c>；没有 2002（整轮失败），也没有 2156（刷新器自己接住了）。
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task ARoundWhoseRefreshTimesOutStillRunsTheEngine()
    {
        await using WorkerFixture fixture = await WorkerFixture.CreateAsync();
        fixture.Source.Mode = SourceMode.TimeOut;

        await fixture.RunOneRoundAsync();

        await AssertStoppedThisRoundAsync(fixture.Runtime);
        Assert.Equal(RouteGraphStaleReasons.RefreshFailed, await fixture.StaleReasonAsync());
        Assert.DoesNotContain(fixture.WorkerLog.Entries, entry => entry.EventId.Id is 2002 or 2156);
    }

    /// <summary>
    /// 刷新在刷新器自己的处理之外出事（这里是读快照头时数据库不可用）：worker 记 2156，引擎照常跑，同一轮照样急停；不记 2002。
    /// </summary>
    /// <remarks>
    /// 这条守的是 worker 把刷新拆出引擎的 try：刷新器的 catch 管不到的失败，也不再连累整轮引擎。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task ARefreshFailingOutsideTheRefresherStillRunsTheEngine()
    {
        await using WorkerFixture fixture = await WorkerFixture.CreateAsync(
            headerReadFailure: () => new InvalidOperationException("The database is unavailable."));

        await fixture.RunOneRoundAsync();

        await AssertStoppedThisRoundAsync(fixture.Runtime);
        Assert.Contains(fixture.WorkerLog.Entries, entry => entry.EventId.Id == 2156);
        Assert.DoesNotContain(fixture.WorkerLog.Entries, entry => entry.EventId.Id == 2002);
    }

    /// <summary>
    /// 刷新器的 try 之外抛出一个调用方没要求的取消（读快照头时超时）：worker 不把它当停机，记 2156、照常跑引擎、同一轮照样急停。
    /// </summary>
    /// <remarks>
    /// 守的是 worker 放行停机的那道 catch 只认已取消的 <c>stoppingToken</c>（审查 L1）。它若接住任何
    /// <see cref="OperationCanceledException"/> 就 return，一次这样的取消会让 worker 退出，整个车队运行时停掉。
    /// </remarks>
    [Fact]
    [Trait("Requirement", "REQ-0246")]
    public async Task ACancellationNobodyAskedForOutsideTheRefresherDoesNotStopTheWorker()
    {
        await using WorkerFixture fixture = await WorkerFixture.CreateAsync(
            headerReadFailure: () => new TaskCanceledException("The database read timed out."));

        await fixture.RunOneRoundAsync();

        await AssertStoppedThisRoundAsync(fixture.Runtime);
        Assert.Single(fixture.WorkerLog.Entries, entry => entry.EventId.Id == 2156);
        Assert.DoesNotContain(fixture.WorkerLog.Entries, entry => entry.EventId.Id == 2002);
    }

    /// <summary>
    /// 刷新进行中停机：worker 正常退出，这一轮不跑引擎（没有急停），不记 2156，刷新器不记「刷新失败」，路网不标 stale。
    /// </summary>
    [Fact]
    public async Task AShutdownDuringTheRefreshStopsTheWorker()
    {
        await using WorkerFixture fixture = await WorkerFixture.CreateAsync();
        fixture.Source.Mode = SourceMode.BlockUntilCancelled;

        await fixture.StopDuringRefreshAsync();

        Assert.True(fixture.Worker.ExecuteTask!.IsCompletedSuccessfully, $"worker ended {fixture.Worker.ExecuteTask.Status}");
        Assert.Equal(0, await TriggerCountAsync(fixture.Runtime));
        Assert.DoesNotContain(fixture.WorkerLog.Entries, entry => entry.EventId.Id is 2002 or 2156);
        Assert.DoesNotContain(fixture.RefresherLog.Entries, entry => entry.EventId.Id == 1);
        Assert.Null(await fixture.StaleReasonAsync());
    }

    /// <summary>
    /// 这一轮跑过故障模型：一条第 1 代、证据是订单 FAILED、已升级的故障；审计里急停触发了一次。
    /// </summary>
    private static async Task AssertStoppedThisRoundAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        VehicleFaultStateRow fault = Assert.Single(await reading.VehicleFaultStates.AsNoTracking().ToArrayAsync(Token));
        Assert.Equal(
            (1L, VehicleFaultEvidence.OrderFailed, true),
            (fault.FaultGeneration, fault.EvidenceCode, fault.EscalatedAt is not null));
        Assert.Equal(1, await TriggerCountAsync(fixture));
    }

    private static async Task<int> TriggerCountAsync(RuntimeFixture fixture)
    {
        await using ControlServerDbContext reading = new(fixture.DbOptionsForTests);
        return await reading.RiotOrderCommandAudit.AsNoTracking()
            .CountAsync(row => row.CommandType == RiotCommandTypeNames.TriggerEmergency, Token);
    }

    // ---- fixtures -----------------------------------------------------------------------------------------------

    private enum SourceMode
    {
        Answer,
        TimeOut,
        CancelCaller,
        BlockUntilCancelled,
    }

    /// <summary>
    /// The route-graph source, answering the two-edge map until a test changes <see cref="Mode"/>. Only the runtime-removals read
    /// fails: it is the read every refresh makes, design state due or not.
    /// </summary>
    private sealed class SwitchableSource : IRouteGraphSource
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SourceMode Mode { get; set; } = SourceMode.Answer;

        public Action? OnRuntimeRead { get; set; }

        /// <summary>Completes the first time a failing mode is reached, so a test knows the worker is inside the refresh.</summary>
        public Task Entered => _entered.Task;

        public Task<IReadOnlyList<RouteGraphEdgeFact>> ReadEdgesAsync(int mapId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RouteGraphEdgeFact>>(Edges);

        public Task<IReadOnlyList<RouteGraphStationFact>> ReadStationsAsync(
            int mapId, IReadOnlyList<RouteGraphEdgeFact> edges, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RouteGraphStationFact>>(Stations);

        public async Task<IReadOnlyList<int>> ReadRemovedEdgeIdsAsync(int mapId, CancellationToken ct)
        {
            if (Mode != SourceMode.Answer)
            {
                _entered.TrySetResult();
            }

            switch (Mode)
            {
                case SourceMode.TimeOut:
                    // What HttpClient throws when its own Timeout elapses: the caller's token is untouched.
                    throw new TaskCanceledException(
                        "The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.",
                        new TimeoutException("The operation was canceled."));
                case SourceMode.CancelCaller:
                    OnRuntimeRead?.Invoke();
                    ct.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("the caller's token was expected to be cancelled by now");
                case SourceMode.BlockUntilCancelled:
                    // A RIoT read standing still until the host shuts down.
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    throw new InvalidOperationException("unreachable");
                default:
                    return [];
            }
        }

        public Task<IReadOnlyList<int>> ReadRemovedStationIdsAsync(int mapId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<int>>([]);

        public Task<IReadOnlyList<RouteGraphEdgeGroupFact>> ReadEdgeGroupsAsync(int mapId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RouteGraphEdgeGroupFact>>([]);

        public Task<bool> ReadDynamicRouteCostPresentAsync(CancellationToken ct) => Task.FromResult(false);
    }

    /// <summary>The real store, except that reading the header throws what the test hands it.</summary>
    private sealed class HeaderFailingStore(IRouteGraphSnapshotStore inner, Action onRead, Func<Exception> failure)
        : IRouteGraphSnapshotStore
    {
        public Task<RouteGraphSnapshotHeader?> ReadHeaderAsync(int mapId, CancellationToken cancellationToken)
        {
            onRead();
            throw failure();
        }

        public Task<RouteGraphState?> ReadStateAsync(int mapId, CancellationToken cancellationToken) =>
            inner.ReadStateAsync(mapId, cancellationToken);

        public Task<long> ReplaceDesignStateAsync(
            int mapId,
            IReadOnlyList<RouteGraphEdgeFact> edges,
            IReadOnlyList<RouteGraphStationFact> stations,
            string? sourceGmtUpdate,
            DateTimeOffset refreshedAt,
            CancellationToken cancellationToken) =>
            inner.ReplaceDesignStateAsync(mapId, edges, stations, sourceGmtUpdate, refreshedAt, cancellationToken);

        public Task ReplaceRuntimeStateAsync(
            int mapId,
            IReadOnlyList<int> removedEdgeIds,
            IReadOnlyList<int> removedStationIds,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken) =>
            inner.ReplaceRuntimeStateAsync(mapId, removedEdgeIds, removedStationIds, observedAt, cancellationToken);

        public Task ReplaceEdgeGroupsAsync(
            int mapId,
            IReadOnlyList<RouteGraphEdgeGroupFact> groups,
            string fingerprint,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken) =>
            inner.ReplaceEdgeGroupsAsync(mapId, groups, fingerprint, observedAt, cancellationToken);

        public Task RecordDynamicRouteCostObservationAsync(
            int mapId, bool present, DateTimeOffset observedAt, CancellationToken cancellationToken) =>
            inner.RecordDynamicRouteCostObservationAsync(mapId, present, observedAt, cancellationToken);

        public Task MarkStaleAsync(int mapId, string reason, DateTimeOffset at, CancellationToken cancellationToken) =>
            inner.MarkStaleAsync(mapId, reason, at, cancellationToken);

        public Task ClearStaleAsync(int mapId, DateTimeOffset at, CancellationToken cancellationToken) =>
            inner.ClearStaleAsync(mapId, at, cancellationToken);
    }

    /// <summary>A logger that keeps the event id, which is what these tests tell 2002 from 2156 by.</summary>
    private sealed class EventLog<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, EventId EventId, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, EventId EventId, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, eventId, formatter(state, exception)));
            }
        }
    }

    private sealed class AdvanceableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>The refresher, the store and the reachability criterion over one in-memory database and one clock.</summary>
    private sealed class GraphFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ControlServerDbContext _context;
        private readonly RouteGraphReachabilityCriterion _criterion;

        private GraphFixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            SwitchableSource source,
            AdvanceableClock clock,
            RouteGraphSnapshotStore store,
            RouteGraphRefresher refresher,
            RouteGraphReachabilityCriterion criterion,
            EventLog<RouteGraphRefresher> log)
        {
            _connection = connection;
            _context = context;
            Source = source;
            Clock = clock;
            Store = store;
            Refresher = refresher;
            _criterion = criterion;
            Log = log;
        }

        public SwitchableSource Source { get; }

        public AdvanceableClock Clock { get; }

        public RouteGraphSnapshotStore Store { get; }

        public RouteGraphRefresher Refresher { get; }

        public EventLog<RouteGraphRefresher> Log { get; }

        public static async Task<GraphFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(Token);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await MigratedDatabaseTemplate.ApplyAsync(context.Database, Token);

            SwitchableSource source = new();
            AdvanceableClock clock = new(Origin);
            IOptions<RouteGraphOptions> options = Options.Create(GraphOptions());
            RouteGraphSnapshotStore store = new(context);
            EventLog<RouteGraphRefresher> log = new();
            return new GraphFixture(
                connection,
                context,
                source,
                clock,
                store,
                new RouteGraphRefresher(source, store, options, clock, log),
                new RouteGraphReachabilityCriterion(new RouteGraphAccess(store, options, clock)),
                log);
        }

        /// <summary>The criterion's verdict for a vehicle at station 11 taking a demand picked up at station 12.</summary>
        public Task<string> ReachabilityAsync() => _criterion.EvaluateAsync(Evaluation(11, 12), Token);

        public async ValueTask DisposeAsync()
        {
            await _context.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private static DispatchCandidateEvaluation Evaluation(int vehicleAtStation, int pickupStation)
        {
            AcceptedDemandSnapshot candidate = new(
                "DEMAND-1",
                TransportDemandKey: "DEMAND-1",
                DemandRevision: 0,
                HistoryEpoch: "EPOCH",
                CatalogRevision: 0,
                AcceptedAt: Origin,
                SeriesId: "SERIES",
                WorkType: "WIRE_TO_GATE",
                Sublot: "SUBLOT",
                CreatedAt: Origin);
            DispatchRoundFacts round = new(
                new DemandCatalogSnapshot("EPOCH", 0, [candidate]),
                new RiotMapStationCatalogSnapshot(MapId, Origin, "sha", []),
                new SingleStationView(new RiotMapStation(pickupStation, "N1-3_N1-7")),
                new HashSet<string>(),
                Origin,
                new VehicleDispatchPolicy([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY"));
            DispatchVehicleFacts vehicle = new(
                "BROKERX-TEST-0001",
                "AGV-TEST-0001",
                new OnboardDispatchFacts(1, [1, 2], true, true, true, true, false),
                new RiotVehicleObservation(
                    "BROKERX-TEST-0001", true, true, "IDLE", "MAP", vehicleAtStation, 80, "NO_CHARGE", 0, Origin, 0, null),
                Origin);
            return new DispatchCandidateEvaluation(candidate, round, vehicle)
            {
                Route = new ResolvedJourneyRoute(
                    "ZONE", "EVIDENCE", "N1-3_N1-7", pickupStation, "关卡", 210,
                    FixedTaskStationResolution.Resolved("WIRE_TO_GATE", FixedStationEnd.Destination, new RiotMapStation(210, "关卡"))),
            };
        }
    }

    /// <summary>
    /// The real <see cref="JourneyRuntimeWorker"/> over a container holding just what one round resolves: the route-graph options and
    /// refresher (over <see cref="SwitchableSource"/>), and the <see cref="RuntimeFixture"/>'s engine, with a journey whose pickup order
    /// has FAILED while the vehicle is moving. The release service is not registered; it runs in its own try after the engine, which
    /// is not what these tests are about.
    /// </summary>
    private sealed class WorkerFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly JourneyMutationGate _gate;

        private WorkerFixture(
            RuntimeFixture runtime,
            ServiceProvider services,
            JourneyMutationGate gate,
            SwitchableSource source,
            JourneyRuntimeWorker worker,
            EventLog<JourneyRuntimeWorker> workerLog,
            EventLog<RouteGraphRefresher> refresherLog,
            Task refreshEntered)
        {
            Runtime = runtime;
            _services = services;
            _gate = gate;
            Source = source;
            Worker = worker;
            WorkerLog = workerLog;
            RefresherLog = refresherLog;
            RefreshEntered = refreshEntered;
        }

        public RuntimeFixture Runtime { get; }

        public SwitchableSource Source { get; }

        public JourneyRuntimeWorker Worker { get; }

        public EventLog<JourneyRuntimeWorker> WorkerLog { get; }

        public EventLog<RouteGraphRefresher> RefresherLog { get; }

        private Task RefreshEntered { get; }

        public static async Task<WorkerFixture> CreateAsync(Func<Exception>? headerReadFailure = null)
        {
            bool headerReadFails = headerReadFailure is not null;
            RuntimeFixture runtime = await RuntimeFixture.CreateAsync();
            runtime.Catalog.Set(runtime.Demand(FirstDemandId, FirstSublot, Now.AddMinutes(-10)));
            runtime.BoxCounts.Set(FirstSublot, 7);
            await TickAndRunAsync(runtime);
            JourneyRuntimeRow dispatched = await runtime.RuntimeAsync();
            Assert.Equal(JourneyRuntimeStage.AwaitingPickupArrival, dispatched.Stage);
            runtime.Context.ChangeTracker.Clear();

            // The round under test: the pickup order FAILED with the vehicle still moving, the vehicle heard from this second.
            runtime.Riot.MovementState = "MT_RUNNING";
            runtime.Riot.FailOrder(dispatched.PickupUpperId);
            runtime.Clock.Advance(TimeSpan.FromSeconds(1));
            await runtime.HearFromPeerAsync();
            runtime.Context.ChangeTracker.Clear();

            // One round, then the timer waits an hour: the test stops the worker long before a second.
            runtime.Options.PollInterval = TimeSpan.FromHours(1);

            SwitchableSource source = new();
            TaskCompletionSource headerRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            EventLog<RouteGraphRefresher> refresherLog = new();
            ServiceCollection services = new();
            services.AddSingleton(Options.Create(GraphOptions()));
            services.AddScoped(_ => new ControlServerDbContext(runtime.DbOptionsForTests));
            services.AddScoped<IRouteGraphSnapshotStore>(provider =>
            {
                RouteGraphSnapshotStore store = new(provider.GetRequiredService<ControlServerDbContext>());
                return headerReadFails ? new HeaderFailingStore(store, () => headerRead.TrySetResult(), headerReadFailure!) : store;
            });
            services.AddScoped(provider => new RouteGraphRefresher(
                source,
                provider.GetRequiredService<IRouteGraphSnapshotStore>(),
                provider.GetRequiredService<IOptions<RouteGraphOptions>>(),
                runtime.Clock,
                refresherLog));
            services.AddScoped(_ => runtime.Engine);
            ServiceProvider provider = services.BuildServiceProvider();

            // The snapshot exists and is fresh before the round, as on a server that has been running.
            if (!headerReadFails)
            {
                await using AsyncServiceScope seed = provider.CreateAsyncScope();
                await seed.ServiceProvider.GetRequiredService<RouteGraphRefresher>().RefreshOnceAsync(Token);
                Assert.Null((await seed.ServiceProvider.GetRequiredService<IRouteGraphSnapshotStore>()
                    .ReadHeaderAsync(MapId, Token))?.StaleReason);
            }

            JourneyMutationGate gate = new();
            EventLog<JourneyRuntimeWorker> workerLog = new();
            JourneyRuntimeWorker worker = new(
                provider.GetRequiredService<IServiceScopeFactory>(),
                gate,
                Options.Create(runtime.Options),
                TimeProvider.System,
                workerLog);
            return new WorkerFixture(
                runtime, provider, gate, source, worker, workerLog, refresherLog,
                headerReadFails ? headerRead.Task : source.Entered);
        }

        /// <summary>
        /// Starts the worker, waits until it is inside the refresh (so it holds the gate), then takes the gate itself -- which it gets
        /// only once the worker's round is over -- and stops the worker while it waits for the next tick.
        /// </summary>
        public async Task RunOneRoundAsync()
        {
            await Worker.StartAsync(Token);
            await RefreshEntered.WaitAsync(TimeSpan.FromSeconds(30), Token);
            using (await _gate.EnterAsync(Token).WaitAsync(TimeSpan.FromSeconds(30), Token))
            {
            }

            await Worker.StopAsync(Token);
            Runtime.Context.ChangeTracker.Clear();
        }

        /// <summary>Starts the worker and stops the host while the worker's refresh is waiting on RIoT.</summary>
        public async Task StopDuringRefreshAsync()
        {
            await Worker.StartAsync(Token);
            await RefreshEntered.WaitAsync(TimeSpan.FromSeconds(30), Token);
            await Worker.StopAsync(Token);
            await Worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), Token);
            Runtime.Context.ChangeTracker.Clear();
        }

        public async Task<string?> StaleReasonAsync()
        {
            await using ControlServerDbContext reading = new(Runtime.DbOptionsForTests);
            return (await new RouteGraphSnapshotStore(reading).ReadHeaderAsync(MapId, Token))?.StaleReason;
        }

        public async ValueTask DisposeAsync()
        {
            Worker.Dispose();
            _gate.Dispose();
            await _services.DisposeAsync();
            await Runtime.DisposeAsync();
        }
    }
}
