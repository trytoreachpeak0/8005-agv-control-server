using System.Data.Common;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Dispatch.Criteria;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// The area assignment table as the execution whitelist and the source of each route's dispatch zone, the
/// startup check on its zones, and the freeze written in the acceptance transaction (control-server#72).
/// </summary>
/// <remarks>
/// The engine-level halves — a silent refusal leaving no structural block and no warning, a version imported
/// between evaluation and acceptance, a Map catalog revision leaving the table alone — are in
/// <see cref="JourneyRuntimeWorkerTests"/>, whose fixture runs whole rounds; zones judged per candidate across
/// vehicles are in <see cref="MultiVehicleExecutionTests"/>.
/// </remarks>
public sealed class AreaAssignmentDispatchTests
{
    private const string DemandId = "10000000-0000-4000-8000-000000000001";
    private const string Zone = "MAP-25-WIRE_TO_GATE";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 6, 0, 0, TimeSpan.Zero);

    // ---- the whitelist ----------------------------------------------------------------------------

    /// <summary>
    /// An AREA the table does not name is out of scope, whatever it starts with; and the refusal is registered
    /// as silent, which is what keeps it out of structural blocks and alarms.
    /// </summary>
    [Theory]
    [InlineData("N1-7")]
    [InlineData("T3-7")]
    [InlineData("D11-10")]
    public async Task AnAreaTheTableDoesNotNameIsOutOfScopeWhateverItsPrefixAndTheRefusalIsSilent(string area)
    {
        DispatchCandidateEvaluation evaluation = Evaluation(area, Table(4, new AreaAssignment("N1-3", Zone, "FRONT")));

        string reason = await RunWhitelistAsync(evaluation);

        Assert.Equal("OUT_OF_SCOPE_AREA", reason);
        Assert.Equal(DispatchReasonCodes.OutOfScopeArea, reason);
        Assert.True(DispatchReasonCodes.IsSilent(reason));
    }

    /// <summary>
    /// An AREA starting with T — a die-attach machine — passes the whitelist once the table names it. Under the
    /// old leading-N rule no configuration could have let it through.
    /// </summary>
    [Fact]
    public async Task AMappedAreaStartingWithTPassesTheWhitelist()
    {
        DispatchCandidateEvaluation evaluation = Evaluation(
            "T3-7",
            Table(4, new AreaAssignment("N1-3", Zone, "FRONT"), new AreaAssignment("T3-7", Zone, "REAR")));

        Assert.Equal(DispatchAdmissionChain.Eligible, await RunWhitelistAsync(evaluation));
    }

    /// <summary>With no table ever imported nothing is in scope, N-prefixed AREAs included.</summary>
    [Fact]
    public async Task WithNoTableEverImportedNothingIsInScope()
    {
        Assert.Equal("OUT_OF_SCOPE_AREA", await RunWhitelistAsync(Evaluation("N1-3", table: null)));
    }

    /// <summary>
    /// Only the whitelist's reason is silent: the codes that mean something is wrong are not in the set.
    /// </summary>
    [Fact]
    public void OnlyTheOutOfScopeAreaReasonIsSilent()
    {
        Assert.Equal(["OUT_OF_SCOPE_AREA"], DispatchReasonCodes.Silent.Order(StringComparer.Ordinal));
        Assert.False(DispatchReasonCodes.IsSilent(DispatchZoneVehicleCriterion.ZoneNotConfiguredReason));
        Assert.False(DispatchReasonCodes.IsSilent(DispatchReasonCodes.AreaSlotGroupNotAssigned));
    }

    // ---- the route's dispatch zone ----------------------------------------------------------------

    /// <summary>
    /// The route's zone is the one the table assigns the AREA, not <c>JourneyRuntime:dispatchZone</c>.
    /// </summary>
    [Fact]
    public async Task TheRoutesDispatchZoneIsTheOneTheTableAssignsTheAreaNotTheConfiguredOne()
    {
        DispatchCandidateEvaluation evaluation = Evaluation(
            "N1-3",
            Table(4, new AreaAssignment("N1-3", "MAP-25-OTHER-ZONE", "FRONT")));
        await new AreaAssignmentLookupCriterion().EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        string reason = await Resolution(allowed: [Zone, "MAP-25-OTHER-ZONE"])
            .EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        Assert.Equal(DispatchAdmissionChain.Eligible, reason);
        Assert.Equal("MAP-25-OTHER-ZONE", evaluation.Route!.DispatchZone);
    }

    /// <summary>
    /// A zone the table assigns but the server does not allow still blocks at runtime, for a version imported
    /// after startup that the startup check never saw.
    /// </summary>
    [Fact]
    public async Task AnAssignedZoneTheServerDoesNotAllowStillBlocksAtRuntime()
    {
        DispatchCandidateEvaluation evaluation = Evaluation(
            "N1-3",
            Table(4, new AreaAssignment("N1-3", "MAP-25-OTHER-ZONE", "FRONT")));
        await new AreaAssignmentLookupCriterion().EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        string reason = await Resolution(allowed: [Zone]).EvaluateAsync(evaluation, TestContext.Current.CancellationToken);

        Assert.Equal("DISPATCH_ZONE_VEHICLE_ADMISSION_MISSING", reason);
    }

    // ---- the startup check ------------------------------------------------------------------------

    /// <summary>
    /// A current version that routes AREAs into zones the server does not allow stops startup, and the message
    /// names every offending entry.
    /// </summary>
    [Fact]
    public async Task StartupIsRefusedWhenTheCurrentVersionRoutesAreasIntoDisallowedZonesAndEveryEntryIsNamed()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        await fixture.AreaAssignments.WriteVersionAsync(
            [
                new AreaAssignment("N1-3", Zone, "FRONT"),
                new AreaAssignment("T3-7", "MAP-26-DIE-ATTACH", "REAR"),
                new AreaAssignment("N2-5", "MAP-25-TYPO", "FRONT"),
            ],
            Now,
            TestContext.Current.CancellationToken);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AreaAssignmentDispatchZoneStartupCheck.EnsureAsync(
                fixture.AreaAssignments, Options(allowed: [Zone]), TestContext.Current.CancellationToken));

        Assert.Contains("version 1", refused.Message, StringComparison.Ordinal);
        Assert.Contains("N2-5 -> MAP-25-TYPO", refused.Message, StringComparison.Ordinal);
        Assert.Contains("T3-7 -> MAP-26-DIE-ATTACH", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("N1-3", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only the current version counts: an older version with a disallowed zone does not stop startup.
    /// </summary>
    [Fact]
    public async Task StartupProceedsWhenEveryZoneInTheCurrentVersionIsAllowed()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        await fixture.AreaAssignments.WriteVersionAsync(
            [new AreaAssignment("N1-3", "MAP-25-TYPO", "FRONT")], Now, TestContext.Current.CancellationToken);
        await fixture.AreaAssignments.WriteVersionAsync(
            [new AreaAssignment("N1-3", Zone, "FRONT")], Now.AddMinutes(1), TestContext.Current.CancellationToken);

        await AreaAssignmentDispatchZoneStartupCheck.EnsureAsync(
            fixture.AreaAssignments, Options(allowed: [Zone]), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A server with no table has nothing to check, and a server whose journey runtime is off dispatches nothing,
    /// so neither is refused.
    /// </summary>
    [Fact]
    public async Task StartupIsNotRefusedWithNoTableOrWithTheJourneyRuntimeOff()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        await AreaAssignmentDispatchZoneStartupCheck.EnsureAsync(
            fixture.AreaAssignments, Options(allowed: [Zone]), TestContext.Current.CancellationToken);

        await fixture.AreaAssignments.WriteVersionAsync(
            [new AreaAssignment("N1-3", "MAP-25-TYPO", "FRONT")], Now, TestContext.Current.CancellationToken);
        JourneyRuntimeOptions off = Options(allowed: [Zone]);
        off.Enabled = false;
        await AreaAssignmentDispatchZoneStartupCheck.EnsureAsync(
            fixture.AreaAssignments, off, TestContext.Current.CancellationToken);
    }

    // ---- the freeze in the acceptance transaction ---------------------------------------------------

    /// <summary>
    /// Accepting a plan that carries a version freezes that version and its snapshot for the demand, at the
    /// moment of acceptance.
    /// </summary>
    [Fact]
    public async Task AcceptingAPlanFreezesTheVersionItWasEvaluatedAgainst()
    {
        await using AcceptanceDatabase database = await AcceptanceDatabase.CreateAsync();
        AreaAssignmentTableVersion table = await database.ImportAsync("FRONT");
        await using ControlServerDbContext context = database.Open();

        await new WireToGateStore(context).AcceptWithOrderIntentAsync(
            Demand("N1-1"), PickupIntent(), Plan(table.Version, "FRONT"), TestContext.Current.CancellationToken);

        await using ControlServerDbContext reader = database.Open();
        Assert.Equal(
            new DemandAreaAssignmentFreeze(DemandId, table.Version, table.SnapshotId, Now),
            await new DemandAreaAssignmentFreezeStore(reader).ReadAsync(DemandId, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A version imported after the round evaluated the demand refuses the acceptance whole: no accepted demand,
    /// no lease, no intent, no journey, no freeze. Intake reports it as a changed candidate, so the demand is
    /// judged again next round against the new version.
    /// </summary>
    [Fact]
    public async Task AVersionImportedBetweenEvaluationAndAcceptanceRefusesTheWholeAcceptance()
    {
        await using AcceptanceDatabase database = await AcceptanceDatabase.CreateAsync();
        AreaAssignmentTableVersion evaluated = await database.ImportAsync("FRONT");
        await database.ImportAsync("REAR");
        await using ControlServerDbContext context = database.Open();
        WireToGateStore store = new(context);

        await Assert.ThrowsAsync<AreaAssignmentVersionChangedException>(() => store.AcceptWithOrderIntentAsync(
            Demand("N1-1"), PickupIntent(), Plan(evaluated.Version, "FRONT"), TestContext.Current.CancellationToken));
        DemandIntakeOutcome outcome = await new DemandIntakeService(new OneDemandCatalog(Demand("N1-1")), store)
            .AcceptJourneyAsync(Demand("N1-1"), PickupIntent(), Plan(evaluated.Version, "FRONT"), TestContext.Current.CancellationToken);

        Assert.Equal(DemandIntakeOutcome.CandidateChanged, outcome);
        await using ControlServerDbContext reader = database.Open();
        await AssertNothingAcceptedAsync(reader);
    }

    /// <summary>
    /// The process dies after the acceptance rows and the freeze are written but before they commit. Nothing is
    /// left behind, so the replay after restart is a first acceptance — not a replay whose plan names a version
    /// no freeze vouches for, which would be refused as a different acceptance — and a replay after that is the
    /// same acceptance.
    /// </summary>
    /// <remarks>
    /// This is what writing the freeze in the acceptance transaction buys. Written after it, the same crash
    /// would leave an accepted demand with no freeze, and every replay would be refused from then on.
    /// </remarks>
    [Fact]
    public async Task ACrashBeforeTheAcceptanceCommitsLeavesNothingSoTheReplayIsAcceptedAndThenIdempotent()
    {
        await using AcceptanceDatabase database = await AcceptanceDatabase.CreateAsync();
        AreaAssignmentTableVersion table = await database.ImportAsync("FRONT");
        JourneyExecutionPlan plan = Plan(table.Version, "FRONT");
        CrashOnCommit crash = new();
        await using (ControlServerDbContext dying = database.Open(crash))
        {
            await Assert.ThrowsAsync<ProcessCrashed>(() => new WireToGateStore(dying).AcceptWithOrderIntentAsync(
                Demand("N1-1"), PickupIntent(), plan, TestContext.Current.CancellationToken));
        }
        Assert.True(crash.WroteTheFreezeBeforeCrashing, "The crash did not come after the freeze was written.");
        await using (ControlServerDbContext afterCrash = database.Open())
        {
            await AssertNothingAcceptedAsync(afterCrash);
        }

        await using (ControlServerDbContext restarted = database.Open())
        {
            await new WireToGateStore(restarted).AcceptWithOrderIntentAsync(
                Demand("N1-1"), PickupIntent(), plan, TestContext.Current.CancellationToken);
        }
        await using (ControlServerDbContext replaying = database.Open())
        {
            await new WireToGateStore(replaying).AcceptWithOrderIntentAsync(
                Demand("N1-1"), PickupIntent(), plan, TestContext.Current.CancellationToken);
        }

        await using ControlServerDbContext reader = database.Open();
        Assert.Single(await reader.JourneyRuntimes.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            table.Version,
            (await new DemandAreaAssignmentFreezeStore(reader).ReadAsync(DemandId, TestContext.Current.CancellationToken))?.Version);
    }

    private static async Task AssertNothingAcceptedAsync(ControlServerDbContext reader)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Assert.Empty(await reader.AcceptedDemands.ToArrayAsync(cancellationToken));
        Assert.Empty(await reader.VehicleDispatchLeases.ToArrayAsync(cancellationToken));
        Assert.Empty(await reader.OrderIntents.ToArrayAsync(cancellationToken));
        Assert.Empty(await reader.JourneyRuntimes.ToArrayAsync(cancellationToken));
        Assert.Empty(await reader.Set<ConfigurationConsumerBindingRow>().ToArrayAsync(cancellationToken));
    }

    private static async Task<string> RunWhitelistAsync(DispatchCandidateEvaluation evaluation)
    {
        string lookup = await new AreaAssignmentLookupCriterion().EvaluateAsync(evaluation, TestContext.Current.CancellationToken);
        Assert.Equal(DispatchAdmissionChain.Eligible, lookup);
        return await new AreaScopeCriterion().EvaluateAsync(evaluation, TestContext.Current.CancellationToken);
    }

    private static StationResolutionCriterion Resolution(string[] allowed) =>
        new(new MapStationResolver(), Microsoft.Extensions.Options.Options.Create(Options(allowed)));

    private static JourneyRuntimeOptions Options(string[] allowed) => new()
    {
        Enabled = true,
        DispatchZone = Zone,
        AllowedDispatchZones = allowed,
    };

    private static AreaAssignmentTableVersion Table(long version, params AreaAssignment[] assignments) => new(
        version,
        new string('a', 64),
        $"snapshot-{version}",
        Now,
        assignments.ToDictionary(assignment => assignment.Area, StringComparer.Ordinal));

    private static AcceptedDemandSnapshot Demand(string area) => new(
        DemandId,
        "SUBLOT-001|WIRE_TO_GATE",
        7,
        "11111111-1111-4111-8111-111111111111",
        21,
        Now,
        "SERIES-1",
        "WIRE_TO_GATE",
        "SUBLOT-001",
        1,
        Now.AddMinutes(-10),
        Now.AddMinutes(-9),
        "TRACE-1",
        "COMMIT-1",
        new LiveMesFieldSet(area, "EQP-01", "STEP-01", Now, "PDFN5×6-8L(12R)"));

    private static OrderIntent PickupIntent() => new(
        "pickup-leg",
        DemandId,
        $"W2G-{DemandId}-PICKUP-1",
        "TO_PICKUP",
        "N1-1",
        Now,
        "BROKERX-0001",
        25,
        12,
        1,
        1);

    private static JourneyExecutionPlan Plan(long areaAssignmentVersion, string slotPosition) => new(
        "AGV-1",
        "BROKERX-0001",
        1,
        25,
        "MAP-25",
        Zone,
        "MAPCAT-1",
        "N1-1",
        12,
        "关卡",
        210,
        2,
        [1, 2],
        "operation-session",
        "pickup-leg",
        $"W2G-{DemandId}-PICKUP-1",
        "gate-leg",
        $"W2G-{DemandId}-GATE-1",
        1,
        Now,
        AreaAssignmentVersion: areaAssignmentVersion,
        RequiredSlotPosition: slotPosition);

    private static DispatchCandidateEvaluation Evaluation(string area, AreaAssignmentTableVersion? table)
    {
        AcceptedDemandSnapshot candidate = Demand(area);
        return new DispatchCandidateEvaluation(
            candidate,
            new DispatchRoundFacts(
                new DemandCatalogSnapshot(candidate.HistoryEpoch, 21, [candidate]),
                new RiotMapStationCatalogSnapshot(
                    25, Now, new string('c', 64), [new RiotMapStation(12, "N1-3_T3-7"), new RiotMapStation(210, "关卡")]),
                new SingleStationView(new RiotMapStation(210, "关卡")),
                new HashSet<string>(StringComparer.Ordinal),
                Now,
                new VehicleDispatchPolicy([], new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal), "TEST-POLICY"),
                table),
            new DispatchVehicleFacts(
                "BROKERX-0001",
                "AGV-1",
                null,
                new RiotVehicleObservation("BROKERX-0001", true, true, "IDLE", "MAP-25", 4, 90, "NO_CHARGE", 0, Now),
                Now));
    }

    /// <summary>A MesIngest catalog holding one unchanged demand, so intake reaches the store.</summary>
    private sealed class OneDemandCatalog(AcceptedDemandSnapshot demand) : IMesIngestCatalog
    {
        public Task<DemandCatalogSnapshot> ReadCatalogAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DemandCatalogSnapshot(demand.HistoryEpoch, demand.CatalogRevision, [demand]));

        public Task<AcceptedDemandSnapshot?> ReadCurrentAsync(string demandId, CancellationToken cancellationToken) =>
            Task.FromResult<AcceptedDemandSnapshot?>(demand.DemandId == demandId ? demand : null);
    }

    private sealed class ProcessCrashed : Exception;

    /// <summary>
    /// Dies at the commit of the acceptance transaction, after checking that the freeze was already written into
    /// it — which is what makes this the crash between acceptance and freeze, not one before either.
    /// </summary>
    private sealed class CrashOnCommit : DbTransactionInterceptor
    {
        public bool WroteTheFreezeBeforeCrashing { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            await using DbCommand count = transaction.Connection!.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM ConfigurationConsumerBindings";
            WroteTheFreezeBeforeCrashing = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 1;
            throw new ProcessCrashed();
        }
    }

    /// <summary>One migrated database file, opened by as many contexts as a test needs.</summary>
    private sealed class AcceptanceDatabase : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly string _connectionString;

        private AcceptanceDatabase(string directory)
        {
            _directory = directory;
            _connectionString = ControlServerSqlite.ForDatabaseFile(Path.Combine(directory, "controlserver.db"));
        }

        public static async Task<AcceptanceDatabase> CreateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "w2g-area-accept-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            AcceptanceDatabase database = new(directory);
            await using ControlServerDbContext context = database.Open();
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            return database;
        }

        public ControlServerDbContext Open(params IInterceptor[] interceptors) => new(
            new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(_connectionString)
                .AddInterceptors(interceptors)
                .Options);

        public async Task<AreaAssignmentTableVersion> ImportAsync(string slotPosition)
        {
            await using ControlServerDbContext context = Open();
            GovernanceStore governance = new(
                context,
                new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
                AuditRetentionPolicy.Default);
            return await new AreaAssignmentStore(context, new GovernedConfigurationPublisher(governance, governance))
                .WriteVersionAsync([new AreaAssignment("N1-1", Zone, slotPosition)], Now, TestContext.Current.CancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // Left in the temp directory; not worth a red test.
            }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Every log entry written, by level, for tests that pin what is not logged.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
