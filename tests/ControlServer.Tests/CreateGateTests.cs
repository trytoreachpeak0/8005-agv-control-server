using System.Text.RegularExpressions;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.CreateGate;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// FP-C13: catalog availability (REQ-0302／REQ-0303／REQ-0308) and the pre-create RouteCost gate
/// (REQ-0147／REQ-0293／REQ-0305／REQ-0345).
/// </summary>
/// <remarks>
/// The storage side is ticket 06's and is tested in <c>Batch2CapabilityStoresTests</c>. What is
/// tested here is the layer that decides: what counts as fresh, what the two approved parameters
/// mean when they are absent, and what the gate does when its two evidence sources disagree.
/// </remarks>
public sealed class CreateGateTests
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 8, 8, 0, 0, TimeSpan.Zero);
    private const int MapId = 25;
    private const string VehicleKey = "BROKERX-TEST-0001";
    private const string AgvId = "AGV-TEST-0001";

    /// <summary>
    /// The fleet policy the round carries. These tests are about the create gate, and no criterion
    /// they exercise reads the policy, so an empty one keeps the fixture honest about what it does
    /// not configure.
    /// </summary>
    private static readonly VehicleDispatchPolicy EmptyPolicy =
        new([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY");

    // ---- REQ-0302: the two approved parameters ------------------------------------------------

    [Fact]
    public async Task WithoutApprovedValuesEverythingMapAndStationDependentIsBlocked()
    {
        // Specification 8.6's hard block, in its negative form: take the parameters away and the
        // gate has to refuse. Showing that it works once configured proves nothing about this.
        await using Fixture fixture = await Fixture.CreateAsync(approved: false);
        await fixture.ConfirmCatalogAsync(Origin);

        CatalogAvailability availability = await fixture.Catalog
            .ReadAsync(MapId, TestContext.Current.CancellationToken);

        Assert.False(availability.IsUsable);
        Assert.Equal(CatalogAvailabilityReasons.ParametersNotApproved, availability.BlockReason);
    }

    [Fact]
    public async Task WithoutApprovedValuesTheAdmissionChainBlocksBeforeStationResolution()
    {
        // The same block where it actually bites. REQ-0303 forbids resolving execution stations
        // for a new demand while the catalog is unusable, so the criterion has to run ahead of
        // resolution rather than discard its result afterwards.
        await using Fixture fixture = await Fixture.CreateAsync(approved: false);
        CatalogAvailabilityCriterion criterion = new(fixture.Catalog);
        DispatchCandidateEvaluation evaluation = Evaluation();

        string reason = await criterion.EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        Assert.Equal(CatalogAvailabilityReasons.ParametersNotApproved, reason);
        Assert.True(criterion.Order < new StationResolutionCriterion(
            new Host.Runtime.MapStationResolver(),
            Options.Create(new Host.Runtime.JourneyRuntimeOptions())).Order);
    }

    [Fact]
    public async Task ApprovedValuesAndAConfirmationMakeTheCatalogUsable()
    {
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);
        await fixture.ConfirmCatalogAsync(Origin);

        CatalogAvailability availability = await fixture.Catalog
            .ReadAsync(MapId, TestContext.Current.CancellationToken);

        Assert.True(availability.IsUsable);
        Assert.Null(availability.DegradedReason);
        Assert.NotEqual(0, availability.CatalogRevision);
    }

    [Theory]
    [InlineData(299, true)]   // inside the five-minute window
    [InlineData(300, true)]   // exactly at it -- the boundary is inclusive
    [InlineData(301, false)]  // past it
    public async Task FreshnessIsJudgedAgainstTheApprovedMaximum(int elapsedSeconds, bool expectUsable)
    {
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);
        await fixture.ConfirmCatalogAsync(Origin);
        fixture.Clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));

        CatalogAvailability availability = await fixture.Catalog
            .ReadAsync(MapId, TestContext.Current.CancellationToken);

        Assert.Equal(expectUsable, availability.IsUsable);
        if (!expectUsable)
        {
            Assert.Equal(CatalogAvailabilityReasons.FreshnessExceeded, availability.BlockReason);
        }
    }

    [Fact]
    public async Task FreshnessRunsFromTheLastCompleteConfirmationNotTheLastAttempt()
    {
        // REQ-0302 in one sentence. A stream of failed attempts must not hold the window open --
        // the vehicle would be routed for hours on a catalog nobody could re-read.
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);
        await fixture.ConfirmCatalogAsync(Origin);

        fixture.Clock.Advance(TimeSpan.FromSeconds(280));
        await fixture.Catalog.RecordFailureAsync(
            MapId, MapStationCatalogState.RefreshFailed, "HTTP_TIMEOUT",
            TestContext.Current.CancellationToken);

        // Still inside the window measured from the confirmation, and REQ-0302 says so explicitly:
        // the last known good snapshot may keep serving, marked as such.
        CatalogAvailability degraded = await fixture.Catalog
            .ReadAsync(MapId, TestContext.Current.CancellationToken);
        Assert.True(degraded.IsUsable);
        Assert.Equal("HTTP_TIMEOUT", degraded.DegradedReason);

        // The attempt did not move the window: 21 more seconds is 301 since the confirmation.
        fixture.Clock.Advance(TimeSpan.FromSeconds(21));
        CatalogAvailability expired = await fixture.Catalog
            .ReadAsync(MapId, TestContext.Current.CancellationToken);
        Assert.False(expired.IsUsable);
        Assert.Equal(CatalogAvailabilityReasons.FreshnessExceeded, expired.BlockReason);
    }

    [Fact]
    public async Task AnIncompatibleBuildBlocksHoweverFreshTheSnapshotIs()
    {
        // REQ-0303 puts a wrong environment behind the same business gate as no snapshot at all.
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);
        await fixture.ConfirmCatalogAsync(Origin);
        await fixture.Catalog.RecordFailureAsync(
            MapId, MapStationCatalogState.BuildIncompatible, "CONTRACT_MISMATCH",
            TestContext.Current.CancellationToken);

        CatalogAvailability availability = await fixture.Catalog
            .ReadAsync(MapId, TestContext.Current.CancellationToken);

        Assert.False(availability.IsUsable);
        Assert.Equal(CatalogAvailabilityReasons.BuildIncompatible, availability.BlockReason);
    }

    [Fact]
    public async Task ACatalogNobodyHasConfirmedIsNotAnEmptyCatalogThatIsFine()
    {
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);

        CatalogAvailability availability = await fixture.Catalog
            .ReadAsync(MapId, TestContext.Current.CancellationToken);

        Assert.False(availability.IsUsable);
        Assert.Equal(CatalogAvailabilityReasons.NeverConfirmed, availability.BlockReason);
    }

    // ---- REQ-0302: the startup validation of the pair -----------------------------------------

    [Fact]
    public void AnUnconfiguredPairStartsTheServerRatherThanCrashingIt()
    {
        // REQ-0303 requires the system to start, show its state, diagnose and retry even with no
        // usable catalog. The block belongs in the admission chain, where it can name itself per
        // demand, not in a startup stack trace.
        Assert.True(new MapStationCatalogOptionsValidator()
            .Validate(null, new MapStationCatalogOptions()).Succeeded);
    }

    [Fact]
    public void AMaximumThatDoesNotExceedThePeriodRefusesToStart()
    {
        ValidateOptionsResult result = new MapStationCatalogOptionsValidator().Validate(null, new()
        {
            ApprovedSyncPeriod = TimeSpan.FromSeconds(30),
            ApprovedMaxUnconfirmed = TimeSpan.FromSeconds(30),
        });

        Assert.True(result.Failed);
        Assert.Contains("must exceed", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void HalfAnApprovalIsNotAnApprovalAndRefusesToStart()
    {
        // The two values are approved separately, so exactly one of them being present is a
        // half-finished approval -- a different thing from one that was never sought.
        Assert.True(new MapStationCatalogOptionsValidator().Validate(null, new()
        {
            ApprovedSyncPeriod = TimeSpan.FromSeconds(30),
        }).Failed);
    }

    // ---- the gate: RIoT's RouteCost -----------------------------------------------------------

    [Fact]
    public async Task AReachableStationPassesAndTheRiotCostIsRecordedUnderItsOwnName()
    {
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);
        fixture.RouteCosts.Set(12, 21500);

        CreateGateOutcome outcome = await fixture.Gate.EvaluateAsync(
            Request(graphCostMm: 20000), TestContext.Current.CancellationToken);

        Assert.True(outcome.IsAllowed);
        Assert.Equal(21500, outcome.RiotRouteCostMm);

        CreateGateAuditRow audit = await fixture.SingleAuditAsync();
        Assert.Equal(21500, audit.RiotRouteCostMm);
        Assert.Equal(20000, audit.GraphTraversalCostMm);
        Assert.True(audit.GraphReachable);
    }

    [Fact]
    public async Task AnUnreachableStationIsRefusedACreate()
    {
        // RIoT answers -1 for unreachable, and that is an answer rather than a failure.
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);
        fixture.RouteCosts.Set(12, -1);

        CreateGateOutcome outcome = await fixture.Gate.EvaluateAsync(
            Request(graphCostMm: null), TestContext.Current.CancellationToken);

        Assert.Equal(CreateGateVerdict.BlockedUnreachable, outcome.Verdict);
        Assert.Equal(CreateGateReasons.StationUnreachable, outcome.BlockReason);
        Assert.Equal(-1, (await fixture.SingleAuditAsync()).RiotRouteCostMm);
    }

    [Fact]
    public async Task NotGettingAnAnswerIsNotRecordedAsUnreachable()
    {
        // Collapsing the two would put a claim about the Map into the audit that nobody made.
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);
        fixture.RouteCosts.FailFor(12);

        CreateGateOutcome outcome = await fixture.Gate.EvaluateAsync(
            Request(graphCostMm: 20000), TestContext.Current.CancellationToken);

        Assert.Equal(CreateGateVerdict.BlockedRouteCostUnavailable, outcome.Verdict);
        Assert.Equal(CreateGateReasons.RouteCostUnavailable, outcome.BlockReason);
        Assert.Null((await fixture.SingleAuditAsync()).RiotRouteCostMm);
    }

    // ---- the gate: two sources that disagree --------------------------------------------------

    [Fact]
    public async Task WhenTheTwoSourcesDisagreeTheCreateIsBlockedAndNeitherSourceWins()
    {
        // The route graph reached the station; RIoT says it cannot be reached. Picking either one
        // buries the fact that something is wrong with the other.
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);
        fixture.RouteCosts.Set(12, -1);

        CreateGateOutcome outcome = await fixture.Gate.EvaluateAsync(
            Request(graphCostMm: 20000), TestContext.Current.CancellationToken);

        Assert.Equal(CreateGateVerdict.BlockedEvidenceConflict, outcome.Verdict);
        Assert.Equal(CreateGateReasons.EvidenceConflict, outcome.BlockReason);

        // Both numbers survive into the audit, separately named, with the disagreement spelled out.
        CreateGateAuditRow audit = await fixture.SingleAuditAsync();
        Assert.Equal(-1, audit.RiotRouteCostMm);
        Assert.Equal(20000, audit.GraphTraversalCostMm);
        Assert.NotNull(audit.ConflictDetail);
    }

    [Fact]
    public async Task TwoCostsOfVeryDifferentSizeAreNotADisagreement()
    {
        // Only reachability is compared. That MapEdge.cost and getRouteCostsBy's costs are the
        // same quantity in the same unit is an inference -- Round 43 never sampled both over one
        // origin/destination pair -- so a magnitude comparison would rest a production block on
        // something nobody measured. The two numbers are recorded side by side instead, which is
        // what makes the inference checkable later from real traffic.
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);
        fixture.RouteCosts.Set(12, 21500);

        CreateGateOutcome outcome = await fixture.Gate.EvaluateAsync(
            Request(graphCostMm: 3), TestContext.Current.CancellationToken);

        Assert.True(outcome.IsAllowed);
        CreateGateAuditRow audit = await fixture.SingleAuditAsync();
        Assert.Equal(21500, audit.RiotRouteCostMm);
        Assert.Equal(3, audit.GraphTraversalCostMm);
    }

    [Fact]
    public async Task WithTheEngineSilentAnUnreachableStationIsSimplyUnreachable()
    {
        // One source cannot disagree with itself. A candidate arrives with no graph cost when the
        // engine is off -- never because the engine found it unreachable, since the engine's own
        // criterion evicts the vehicle before this gate runs.
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);
        fixture.RouteCosts.Set(12, -1);

        CreateGateOutcome outcome = await fixture.Gate.EvaluateAsync(
            Request(graphCostMm: null), TestContext.Current.CancellationToken);

        Assert.Equal(CreateGateVerdict.BlockedUnreachable, outcome.Verdict);
        Assert.False((await fixture.SingleAuditAsync()).GraphReachable);
    }

    // ---- REQ-0305 / REQ-0308: the two levels are not the same thing ---------------------------

    [Fact]
    public async Task AFrozenStationMissingFromAFreshCatalogIsATaskLevelBlock()
    {
        // REQ-0308's StructuralDispatchBlock: the snapshot is fine, this task's station is not.
        // Not a catalog-level state, and never remapped onto a station with a similar name.
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);
        fixture.RouteCosts.Set(12, 21500);

        CreateGateOutcome outcome = await fixture.Gate.EvaluateAsync(
            Request(graphCostMm: 20000) with { TargetStationInCurrentCatalog = false },
            TestContext.Current.CancellationToken);

        Assert.Equal(CreateGateVerdict.BlockedFrozenStationAbsent, outcome.Verdict);
        Assert.Equal(CreateGateReasons.FrozenStationAbsent, outcome.BlockReason);
        // RIoT was never asked: there is no point pricing a route to a station the catalog lost.
        Assert.Empty(fixture.RouteCosts.Calls);
    }

    [Fact]
    public async Task ATaskLevelBlockLeavesTheCatalogStateAlone()
    {
        // The two levels have independent production and clearing conditions. A demand that cannot
        // be created does not make the catalog unavailable to every other demand.
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);
        await fixture.ConfirmCatalogAsync(Origin);
        fixture.RouteCosts.Set(12, -1);

        await fixture.Gate.EvaluateAsync(Request(graphCostMm: null), TestContext.Current.CancellationToken);

        MapStationCatalogAvailability state = (await new CatalogAvailabilityStore(fixture.Context)
            .ReadStateAsync(MapId, TestContext.Current.CancellationToken))!;
        Assert.Equal(MapStationCatalogState.Fresh, state.State);
        Assert.True((await fixture.Catalog.ReadAsync(MapId, TestContext.Current.CancellationToken)).IsUsable);
    }

    [Fact]
    public async Task ACatalogLevelFailureProducesNoGateAuditAtAll()
    {
        // The other direction: a catalog in doubt is not a verdict about any one demand's
        // endpoints, so it must not leave rows that read like one.
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);
        await fixture.Catalog.RecordFailureAsync(
            MapId, MapStationCatalogState.RefreshFailed, "HTTP_TIMEOUT",
            TestContext.Current.CancellationToken);

        CatalogAvailabilityCriterion criterion = new(fixture.Catalog);
        string reason = await criterion.EvaluateAsync(Evaluation(), TestContext.Current.CancellationToken);

        Assert.Equal(CatalogAvailabilityReasons.NeverConfirmed, reason);
        Assert.Empty(await fixture.Context.CreateGateAudit.ToListAsync(TestContext.Current.CancellationToken));
    }

    // ---- REQ-0308: one state per reason, not one alarm per round ------------------------------

    [Fact]
    public async Task RepeatingTheSameVerdictDoesNotAppendAnotherAuditRow()
    {
        // The runtime ticks once a second. Appending an identical verdict every round would bury
        // the one transition worth reading under a shift's worth of rows saying the same thing.
        await using Fixture fixture = await Fixture.CreateAsync(approved: true);
        fixture.RouteCosts.Set(12, -1);

        for (int round = 0; round < 5; round++)
        {
            await fixture.Gate.EvaluateAsync(Request(graphCostMm: null), TestContext.Current.CancellationToken);
        }

        Assert.Single(await fixture.Context.CreateGateAudit.ToListAsync(TestContext.Current.CancellationToken));

        // A change does get its own row: the transition is exactly what the table is for.
        fixture.RouteCosts.Set(12, 21500);
        await fixture.Gate.EvaluateAsync(Request(graphCostMm: null), TestContext.Current.CancellationToken);
        Assert.Equal(2, await fixture.Context.CreateGateAudit.CountAsync(TestContext.Current.CancellationToken));
    }

    // ---- the retired endpoints stay retired ---------------------------------------------------

    [Fact]
    public void TheNearStationQueriesAreStillAZeroHitInProductCode()
    {
        // The specification's withdrawal of queryNearEnd/queryNearestStart is unchanged by this
        // ticket, and the gate is exactly where someone would be tempted to reach for them: they
        // sound like they answer "which station can this vehicle get to". They do not -- they
        // return a nearest candidate, not a reachability proof.
        List<string> offenders = [];
        foreach (string file in Directory.GetFiles(SourcePath("src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string line in File.ReadAllLines(file))
            {
                string code = Regex.Replace(line, @"^\s*(///|//).*$", string.Empty);
                if (code.Contains("QueryNearestEnd", StringComparison.Ordinal) ||
                    code.Contains("QueryNearestStart", StringComparison.Ordinal) ||
                    code.Contains("queryNearEnd", StringComparison.Ordinal) ||
                    code.Contains("queryNearestStart", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    // ---- fixture -------------------------------------------------------------------------------

    private static CreateGateRequest Request(long? graphCostMm) => new(
        "10000000-0000-4000-8000-000000000001",
        "SUBLOT-001|WIRE_TO_GATE",
        "AGV-01",
        VehicleKey,
        MapId,
        TargetStationId: 12,
        graphCostMm);

    private static DispatchCandidateEvaluation Evaluation()
    {
        AcceptedDemandSnapshot candidate = new(
            "10000000-0000-4000-8000-000000000001",
            "SUBLOT-001|WIRE_TO_GATE",
            DemandRevision: 0,
            HistoryEpoch: "EPOCH",
            CatalogRevision: 0,
            AcceptedAt: Origin,
            SeriesId: "SERIES",
            WorkType: "WIRE_TO_GATE",
            Sublot: "SUBLOT-001",
            CreatedAt: Origin);
        DispatchRoundFacts round = new(
            new DemandCatalogSnapshot("EPOCH", 0, [candidate]),
            new RiotMapStationCatalogSnapshot(MapId, Origin, new string('a', 64), []),
            new ConfiguredGateStationView(new RiotMapStation(210, "关卡")),
            new HashSet<string>(),
            Origin,
            EmptyPolicy);
        DispatchVehicleFacts vehicle = new(
            VehicleKey,
            AgvId,
            new OnboardDispatchFacts(1, [1, 2], true, true, true, true, false),
            new RiotVehicleObservation(VehicleKey, true, true, "IDLE", "MAP", 11, 80, "NO_CHARGE", 0, Origin, 0, null),
            Origin);
        return new DispatchCandidateEvaluation(candidate, round, vehicle);
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

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class StubRouteCostProbe : IRiotRouteCostProbe
    {
        private readonly Dictionary<int, long?> _byStation = [];

        public List<(int MapId, int StationId, string VehicleKey)> Calls { get; } = [];

        public void Set(int stationId, long costMm) => _byStation[stationId] = costMm;

        /// <summary>The call itself fails — no answer, which is not the same as "unreachable".</summary>
        public void FailFor(int stationId) => _byStation[stationId] = null;

        public Task<RiotRouteCost?> ReadRouteCostAsync(
            int mapId,
            int stationId,
            string vehicleKey,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Calls.Add((mapId, stationId, vehicleKey));
            long? configured = _byStation.TryGetValue(stationId, out long? value) ? value : 0;
            return Task.FromResult(configured is null ? null : new RiotRouteCost(configured.Value));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Fixture(
            SqliteConnection connection,
            ControlServerDbContext context,
            CatalogAvailabilityAccess catalog,
            PreCreateGate gate,
            StubRouteCostProbe routeCosts,
            MovableClock clock)
        {
            _connection = connection;
            Context = context;
            Catalog = catalog;
            Gate = gate;
            RouteCosts = routeCosts;
            Clock = clock;
        }

        public ControlServerDbContext Context { get; }
        public CatalogAvailabilityAccess Catalog { get; }
        public PreCreateGate Gate { get; }
        public StubRouteCostProbe RouteCosts { get; }
        public MovableClock Clock { get; }

        public static async Task<Fixture> CreateAsync(bool approved)
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> dbOptions =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            ControlServerDbContext context = new(dbOptions);
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

            MovableClock clock = new(Origin);
            CatalogAvailabilityStore store = new(context);
            MapStationCatalogOptions options = approved
                ? new MapStationCatalogOptions
                {
                    ApprovedSyncPeriod = TimeSpan.FromSeconds(30),
                    ApprovedMaxUnconfirmed = TimeSpan.FromMinutes(5),
                }
                : new MapStationCatalogOptions();

            CatalogAvailabilityAccess catalog = new(
                store,
                Options.Create(options),
                new CatalogAlarmLedger(),
                clock,
                NullLogger<CatalogAvailabilityAccess>.Instance);
            StubRouteCostProbe routeCosts = new();
            PreCreateGate gate = new(routeCosts, store, clock, NullLogger<PreCreateGate>.Instance);

            return new Fixture(connection, context, catalog, gate, routeCosts, clock);
        }

        public Task ConfirmCatalogAsync(DateTimeOffset at) => Catalog.RecordConfirmationAsync(
            new RiotMapStationCatalogSnapshot(MapId, at, new string('a', 64), []),
            TestContext.Current.CancellationToken);

        public async Task<CreateGateAuditRow> SingleAuditAsync() =>
            (await Context.CreateGateAudit.ToListAsync(TestContext.Current.CancellationToken)).Single();

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
