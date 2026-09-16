using System.Data.Common;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ControlServer.Tests;

/// <summary>
/// 需求在分配目标仓位时冻结所用的分区归属表版本，之后的版本变化不改变已冻结的需求（REQ-0350）。
/// </summary>
public sealed class DemandAreaAssignmentFreezeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FreezingTheSameVersionAgainReturnsTheFirstFreezeUnchanged()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        (AreaAssignmentTableVersion first, _) = await WriteTwoVersionsAsync(fixture);

        DemandAreaAssignmentFreeze frozen = await fixture.Freezes.FreezeAsync(
            "D-1", 1, Now, TestContext.Current.CancellationToken);
        DemandAreaAssignmentFreeze again = await fixture.Freezes.FreezeAsync(
            "D-1", 1, Now.AddMinutes(3), TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        DemandAreaAssignmentFreeze expected = new("D-1", 1, first.SnapshotId, Now);
        Assert.Equal(expected, frozen);
        Assert.Equal(expected, again);
        Assert.Equal(expected, await fixture.Freezes.ReadAsync("D-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FreezingADifferentVersionForAnAlreadyFrozenDemandIsRefusedAndTheFirstFreezeStands()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        (AreaAssignmentTableVersion first, _) = await WriteTwoVersionsAsync(fixture);
        await fixture.Freezes.FreezeAsync("D-1", 1, Now, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DemandAreaAssignmentFreezeConflictException>(() =>
            fixture.Freezes.FreezeAsync("D-1", 2, Now.AddMinutes(1), TestContext.Current.CancellationToken));
        fixture.Context.ChangeTracker.Clear();

        Assert.Equal(
            new DemandAreaAssignmentFreeze("D-1", 1, first.SnapshotId, Now),
            await fixture.Freezes.ReadAsync("D-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AVersionThatWasNeverWrittenCannotBeFrozenAndAnUnfrozenDemandReadsAsNothing()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        await WriteTwoVersionsAsync(fixture);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Freezes.FreezeAsync("D-1", 3, Now, TestContext.Current.CancellationToken));

        Assert.Null(await fixture.Freezes.ReadAsync("D-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheFreezeIsOneRowPerDemandInTheBatch3ConsumerBindingTable()
    {
        // The storage decision of control-server#66: no column on JourneyRuntimes (batch 7 rekeys that
        // table), the consumer-binding table batch 3 built for exactly this instead.
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        (_, AreaAssignmentTableVersion second) = await WriteTwoVersionsAsync(fixture);
        await fixture.Freezes.FreezeAsync("D-1", 2, Now, TestContext.Current.CancellationToken);
        await fixture.Freezes.FreezeAsync("D-1", 2, Now.AddMinutes(1), TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        ConfigurationConsumerBindingRow binding = await fixture.Context.Set<ConfigurationConsumerBindingRow>()
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DispatchZoneAreaAssignmentGovernance.DemandConsumerKind, binding.ConsumerKind);
        Assert.Equal("D-1", binding.ConsumerId);
        Assert.Equal(GovernedObjectKind.DispatchZoneAreaAssignment, binding.ObjectKind);
        Assert.Equal(DispatchZoneAreaAssignmentGovernance.ObjectId, binding.ObjectId);
        Assert.Equal(2, binding.FrozenVersion);
        Assert.Equal(second.SnapshotId, binding.SnapshotId);
        Assert.Equal(Now, binding.FrozenAt);
    }

    [Fact]
    public async Task TheInFlightListLeavesOutOnlyDemandsWhoseJourneyHasCompleted()
    {
        await using AreaAssignmentPersistenceFixture fixture = await AreaAssignmentPersistenceFixture.CreateAsync();
        await WriteTwoVersionsAsync(fixture);
        fixture.Context.Set<JourneyRuntimeRow>().AddRange(
            Runtime("D-COMPLETED", JourneyRuntimeStage.Completed),
            Runtime("D-TRAVELLING", JourneyRuntimeStage.AwaitingGateArrival),
            Runtime("D-BLOCKED", JourneyRuntimeStage.Blocked));
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await fixture.Freezes.FreezeAsync("D-COMPLETED", 1, Now, TestContext.Current.CancellationToken);
        await fixture.Freezes.FreezeAsync("D-TRAVELLING", 1, Now, TestContext.Current.CancellationToken);
        await fixture.Freezes.FreezeAsync("D-BLOCKED", 2, Now, TestContext.Current.CancellationToken);
        // Frozen before its journey row exists: still in flight, because leaving one out of an import
        // preview is worse than listing one too many.
        await fixture.Freezes.FreezeAsync("D-NO-JOURNEY-YET", 2, Now, TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        IReadOnlyList<DemandAreaAssignmentFreeze> inFlight =
            await fixture.Freezes.ListInFlightAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [("D-BLOCKED", 2L), ("D-NO-JOURNEY-YET", 2L), ("D-TRAVELLING", 1L)],
            inFlight.Select(freeze => (freeze.DemandId, freeze.Version)));
    }

    // ---- two writers freezing one demand (control-server#72) ------------------------------------

    /// <summary>
    /// A second writer freezes the same demand at the same version between this writer's read and its insert:
    /// this writer returns that freeze instead of failing on the primary key.
    /// </summary>
    /// <remarks>
    /// The interleaving is placed, not hoped for: the other writer's freeze runs from inside this writer's
    /// read of the version header, which comes after its read of the existing freeze and before its insert.
    /// Two connections to one database file, so the refusal is SQLite's own.
    /// </remarks>
    [Fact]
    public async Task AWriterThatLosesTheRaceToFreezeTheSameVersionReturnsTheWinnersFreezeInsteadOfFailing()
    {
        await using TwoWriterDatabase database = await TwoWriterDatabase.CreateAsync();
        (AreaAssignmentTableVersion first, _) = await database.WriteTwoVersionsAsync();
        await using ControlServerDbContext other = database.Open();
        FreezeBeforeVersionRead interleave = new(() =>
            new DemandAreaAssignmentFreezeStore(other).FreezeAsync("D-1", 1, Now, TestContext.Current.CancellationToken));
        await using ControlServerDbContext mine = database.Open(interleave);

        DemandAreaAssignmentFreeze frozen = await new DemandAreaAssignmentFreezeStore(mine)
            .FreezeAsync("D-1", 1, Now.AddSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(interleave.Fired, "The other writer never ran between the read and the insert.");
        DemandAreaAssignmentFreeze winner = new("D-1", 1, first.SnapshotId, Now);
        Assert.Equal(winner, frozen);
        await using ControlServerDbContext reader = database.Open();
        Assert.Equal(winner, await new DemandAreaAssignmentFreezeStore(reader).ReadAsync("D-1", TestContext.Current.CancellationToken));
        Assert.Equal(1, await reader.Set<ConfigurationConsumerBindingRow>().CountAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The same race, but the other writer froze a different version: this writer gets the freeze conflict,
    /// exactly as if its read had seen that freeze, and not a database error.
    /// </summary>
    [Fact]
    public async Task AWriterThatLosesTheRaceToADifferentVersionGetsTheFreezeConflictNotADatabaseError()
    {
        await using TwoWriterDatabase database = await TwoWriterDatabase.CreateAsync();
        (_, AreaAssignmentTableVersion second) = await database.WriteTwoVersionsAsync();
        await using ControlServerDbContext other = database.Open();
        FreezeBeforeVersionRead interleave = new(() =>
            new DemandAreaAssignmentFreezeStore(other).FreezeAsync("D-1", 2, Now, TestContext.Current.CancellationToken));
        await using ControlServerDbContext mine = database.Open(interleave);

        await Assert.ThrowsAsync<DemandAreaAssignmentFreezeConflictException>(() =>
            new DemandAreaAssignmentFreezeStore(mine).FreezeAsync("D-1", 1, Now.AddSeconds(5), TestContext.Current.CancellationToken));

        Assert.True(interleave.Fired, "The other writer never ran between the read and the insert.");
        await using ControlServerDbContext reader = database.Open();
        Assert.Equal(
            new DemandAreaAssignmentFreeze("D-1", 2, second.SnapshotId, Now),
            await new DemandAreaAssignmentFreezeStore(reader).ReadAsync("D-1", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Eight writers on eight connections freeze one demand at one version at once: every one of them returns
    /// the one freeze that was written, and none throws.
    /// </summary>
    /// <remarks>
    /// Unlike the two tests above this does not place the interleaving, so a run may or may not hit the race.
    /// What it pins is the outcome under real concurrency, whichever way the scheduler lines the writers up.
    /// </remarks>
    [Fact]
    public async Task ManyWritersFreezingOneDemandAtOneVersionConcurrentlyAllReturnTheOneFreeze()
    {
        await using TwoWriterDatabase database = await TwoWriterDatabase.CreateAsync();
        await database.WriteTwoVersionsAsync();
        using Barrier start = new(8);

        DemandAreaAssignmentFreeze[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(async () =>
        {
            await using ControlServerDbContext context = database.Open();
            start.SignalAndWait(TestContext.Current.CancellationToken);
            return await new DemandAreaAssignmentFreezeStore(context)
                .FreezeAsync("D-1", 2, Now.AddSeconds(writer), TestContext.Current.CancellationToken);
        })));

        Assert.Single(results.Distinct());
        await using ControlServerDbContext reader = database.Open();
        Assert.Equal(1, await reader.Set<ConfigurationConsumerBindingRow>().CountAsync(TestContext.Current.CancellationToken));
    }

    private static Task<(AreaAssignmentTableVersion First, AreaAssignmentTableVersion Second)>
        WriteTwoVersionsAsync(AreaAssignmentPersistenceFixture fixture) => WriteTwoVersionsAsync(fixture.AreaAssignments);

    private static async Task<(AreaAssignmentTableVersion First, AreaAssignmentTableVersion Second)>
        WriteTwoVersionsAsync(IAreaAssignmentStore areaAssignments)
    {
        AreaAssignmentTableVersion first = await areaAssignments.WriteVersionAsync(
            [new("N01", "MAP-25-WIRE_TO_GATE", "FRONT")], Now.AddDays(-2), TestContext.Current.CancellationToken);
        AreaAssignmentTableVersion second = await areaAssignments.WriteVersionAsync(
            [new("N01", "MAP-25-WIRE_TO_GATE", "REAR")], Now.AddDays(-1), TestContext.Current.CancellationToken);
        return (first, second);
    }

    private static JourneyRuntimeRow Runtime(string demandId, JourneyRuntimeStage stage) => new()
    {
        DemandId = demandId,
        Stage = stage,
        AgvId = "AGV-01",
        VehicleKey = "VEHICLE-001",
        AgvLifecycleGeneration = 1,
        MapId = 25,
        MapIdentity = "MAP-25",
        DispatchZone = "MAP-25-WIRE_TO_GATE",
        RouteEvidenceId = "ROUTE-01",
        PickupStationId = "PICKUP",
        PickupStationRiotId = 11,
        GateStationId = "GATE",
        GateStationRiotId = 22,
        ExpectedBasketCount = 2,
        TargetSlotsJson = "[1,2]",
        OperationSessionId = $"session-{demandId}",
        PickupMovementLegId = $"pickup-leg-{demandId}",
        PickupUpperId = $"UPPER-PICKUP-{demandId}",
        GateMovementLegId = $"gate-leg-{demandId}",
        GateUpperId = $"UPPER-GATE-{demandId}",
        DispatchGeneration = 1,
        VehicleBusinessRevision = 1,
        WorklistRevision = 1,
        PlanRevision = 1,
        VehicleBusinessMessageId = $"vb-{demandId}",
        WorklistMessageId = $"wl-{demandId}",
        PlanMessageId = $"plan-{demandId}",
        SublotRequestMessageId = $"sublot-{demandId}",
        LoadCommandMessageId = $"load-{demandId}",
        LoadSlotOperationAttemptId = $"load-attempt-{demandId}",
        PreDepartureSafetyCheckMessageId = $"safety-msg-{demandId}",
        PreDepartureSafetyCheckId = $"safety-{demandId}",
        GateVehicleBusinessMessageId = $"gate-vb-{demandId}",
        GateWorklistMessageId = $"gate-wl-{demandId}",
        GatePlanMessageId = $"gate-plan-{demandId}",
        UnloadCommandMessageId = $"unload-{demandId}",
        UnloadSlotOperationAttemptId = $"unload-attempt-{demandId}",
        CreatedAt = Now.AddMinutes(-8),
        UpdatedAt = Now
    };

    /// <summary>
    /// Runs another writer's freeze from inside the first read of the version header, which in
    /// <see cref="DemandAreaAssignmentFreezeStore.FreezeAsync"/> sits between the read of the existing freeze and
    /// the insert.
    /// </summary>
    private sealed class FreezeBeforeVersionRead(Func<Task> otherWriter) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("DispatchZoneAreaAssignmentVersions", StringComparison.Ordinal))
            {
                Fired = true;
                await otherWriter();
            }
            return result;
        }
    }

    /// <summary>
    /// One migrated database file that several connections open, the way two server scopes, or the server and
    /// FieldOps, do.
    /// </summary>
    private sealed class TwoWriterDatabase : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly string _connectionString;

        private TwoWriterDatabase(string directory)
        {
            _directory = directory;
            _connectionString = ControlServerSqlite.ForDatabaseFile(Path.Combine(directory, "controlserver.db"));
        }

        public static async Task<TwoWriterDatabase> CreateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "w2g-area-freeze-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            TwoWriterDatabase database = new(directory);
            await using ControlServerDbContext context = database.Open();
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            return database;
        }

        public ControlServerDbContext Open(params IInterceptor[] interceptors) => new(
            new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(_connectionString)
                .AddInterceptors(interceptors)
                .Options);

        public async Task<(AreaAssignmentTableVersion First, AreaAssignmentTableVersion Second)> WriteTwoVersionsAsync()
        {
            await using ControlServerDbContext context = Open();
            GovernanceStore governance = new(
                context,
                new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
                AuditRetentionPolicy.Default);
            return await DemandAreaAssignmentFreezeTests.WriteTwoVersionsAsync(
                new AreaAssignmentStore(context, new GovernedConfigurationPublisher(governance, governance)));
        }

        public ValueTask DisposeAsync()
        {
            // The pool keeps the file open on behalf of the process; without this the directory cannot go.
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
