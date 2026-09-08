using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ControlServer.Tests;

public sealed class ApplicationOrchestrationTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task IntakeUsesFinalMesReadBeforeAtomicCommit()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        AcceptedDemandSnapshot discovered = Snapshot(catalogRevision: 20);
        RecordingMesCatalog catalog = new(discovered with { CatalogRevision = 21 });
        DemandIntakeService service = new(catalog, store);

        DemandIntakeOutcome outcome = await service.AcceptAsync(
            discovered,
            new OrderIntent("LEG-001", "D-001", "UPPER-001", "TO_PICKUP", "ST-PICKUP", discovered.AcceptedAt),
            TestContext.Current.CancellationToken);

        Assert.Equal(DemandIntakeOutcome.Accepted, outcome);
        Assert.Equal(1, catalog.ReadCount);
        AcceptedDemandRow accepted = await context.AcceptedDemands.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(21, accepted.CatalogRevision);
        Assert.Equal("SERIES-001", accepted.SeriesId);
        Assert.Equal("TRACE-001", accepted.ValuePollTraceId);
        Assert.Contains("AREA-01", accepted.LiveMesFieldsJson, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task IntakeCoordinatorPersistsFinalDecisionFactsBeforeRiotMutation()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        AcceptedDemandSnapshot discovered = Snapshot(catalogRevision: 20);
        RecordingMesCatalog catalog = new(discovered with
        {
            CatalogRevision = 21,
            AcceptedAt = discovered.AcceptedAt.AddMinutes(1)
        });
        CommitObservingRiotGateway gateway = new(async () =>
        {
            AcceptedDemandRow accepted = await context.AcceptedDemands
                .SingleAsync(TestContext.Current.CancellationToken);
            return accepted.CatalogRevision == 21 && accepted.ValueProjectionCommitId == "COMMIT-001";
        });
        JourneyIntakeCoordinator coordinator = new(
            new DemandIntakeService(catalog, store),
            new MovementDispatchService(store, gateway));
        OrderIntent intent = new(
            "LEG-001", "D-001", "UPPER-001", "TO_PICKUP", "ST-PICKUP", discovered.AcceptedAt,
            "AGV-8005-01", 25, 12, 1, 1);

        JourneyIntakeResult result = await coordinator.AcceptAndDispatchToPickupAsync(
            discovered,
            intent,
            TestContext.Current.CancellationToken);

        Assert.Equal(DemandIntakeOutcome.Accepted, result.IntakeOutcome);
        Assert.Equal(MovementDispatchOutcome.Confirmed, result.MovementDispatch?.Outcome);
        Assert.True(gateway.AcceptanceWasCommittedBeforeCreate);
        Assert.Equal("CONFIRMED", (await context.OrderIntents.SingleAsync(
            TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task IntakeCoordinatorDoesNotDispatchWhenAnyDecisionFactChanged()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        AcceptedDemandSnapshot discovered = Snapshot(catalogRevision: 20);
        RecordingMesCatalog catalog = new(discovered with
        {
            CatalogRevision = 21,
            ValuePollTraceId = "TRACE-CHANGED"
        });
        CountingRiotGateway gateway = new();
        JourneyIntakeCoordinator coordinator = new(
            new DemandIntakeService(catalog, store),
            new MovementDispatchService(store, gateway));

        JourneyIntakeResult result = await coordinator.AcceptAndDispatchToPickupAsync(
            discovered,
            new OrderIntent("LEG-001", "D-001", "UPPER-001", "TO_PICKUP", "ST-PICKUP", discovered.AcceptedAt),
            TestContext.Current.CancellationToken);

        Assert.Equal(DemandIntakeOutcome.CandidateChanged, result.IntakeOutcome);
        Assert.Null(result.MovementDispatch);
        Assert.Equal(0, gateway.CallCount);
        Assert.Empty(await context.AcceptedDemands.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await context.OrderIntents.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task UnknownRiotResultKeepsOriginalUpperIdAndDoesNotBlindlyCreate()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        DateTimeOffset now = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
        await store.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot("D-001", "SUBLOT-001|WIRE_TO_GATE", 7, "history-1", 21, now),
            new OrderIntent("LEG-001", "D-001", "UPPER-001", "TO_PICKUP", "ST-PICKUP", now),
            TestContext.Current.CancellationToken);
        RecordingRiotGateway gateway = new(new RiotOrderObservation("UPPER-001", RiotOrderObservationKind.Unknown, null));
        MovementDispatchService service = new(store, gateway);

        MovementDispatchResult result = await service.ReconcileOrCreateAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, result.Outcome);
        Assert.Equal("UPPER-001", result.UpperId);
        Assert.Equal(0, gateway.CreateCount);
        Assert.Equal("RESULT_UNKNOWN", (await context.OrderIntents.SingleAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task CreateIsConfirmedOnlyByIndependentUpperIdRead()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        DateTimeOffset now = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
        await store.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot("D-001", "SUBLOT-001|WIRE_TO_GATE", 7, "history-1", 21, now),
            new OrderIntent("LEG-001", "D-001", "UPPER-001", "TO_PICKUP", "ST-12", now,
                "AGV-8005-01", 29, 12, 4, 7),
            TestContext.Current.CancellationToken);
        SequenceRiotGateway gateway = new();
        MovementDispatchService service = new(store, gateway);

        MovementDispatchResult result = await service.ReconcileOrCreateAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.Confirmed, result.Outcome);
        Assert.Equal("ORDER-001", result.OrderId);
        Assert.Equal(1, gateway.CreateCount);
        Assert.Equal(2, gateway.ReconcileCount);
        Assert.NotNull(gateway.CreatedIntent);
        Assert.Equal("AGV-8005-01", gateway.CreatedIntent.VehicleKey);
        Assert.Equal(29, gateway.CreatedIntent.MapId);
        Assert.Equal(12, gateway.CreatedIntent.DestinationStationId);
        Assert.Equal(4, gateway.CreatedIntent.AgvLifecycleGeneration);
        Assert.Equal(7, gateway.CreatedIntent.DispatchGeneration);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    [Trait("IntegrationSlice", "W2G-IS-06")]
    public async Task UnknownCreateOutcomeSurvivesRestartAndConfirmedAbsenceDoesNotCreateAgain()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext firstContext = await CreateContextAsync(connection);
        WireToGateStore firstStore = new(firstContext);
        DateTimeOffset now = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
        OrderIntent intent = new(
            "LEG-001", "D-001", "UPPER-001", "TO_PICKUP", "ST-12", now,
            "AGV-8005-01", 29, 12, 4, 7);
        await firstStore.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot("D-001", "SUBLOT-001|WIRE_TO_GATE", 7, "history-1", 21, now),
            intent,
            TestContext.Current.CancellationToken);
        UnknownCreateGateway gateway = new(intent);
        MovementDispatchResult first = await new MovementDispatchService(firstStore, gateway)
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, first.Outcome);
        Assert.Equal(1, gateway.CreateCount);
        Assert.Equal(1, (await firstContext.OrderIntents.SingleAsync(
            TestContext.Current.CancellationToken)).CreateAttemptCount);
        Assert.Equal(4, await firstContext.RiotDispatchAuditEvents.CountAsync(
            TestContext.Current.CancellationToken));
        await firstContext.DisposeAsync();
        await using ControlServerDbContext restartedContext = await CreateContextAsync(connection);
        MovementDispatchResult afterRestart = await new MovementDispatchService(
                new WireToGateStore(restartedContext), gateway)
            .ReconcileOrCreateAsync(intent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, afterRestart.Outcome);
        Assert.Equal(intent.UpperId, afterRestart.UpperId);
        Assert.Equal(1, gateway.CreateCount);
        OrderIntentRow restarted = await restartedContext.OrderIntents.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal("RESULT_UNKNOWN", restarted.Status);
        Assert.Equal(1, restarted.CreateAttemptCount);
        Assert.Equal("CreateResponseUnknown", restarted.LastCreateOutcome);
        RiotDispatchAuditEventRow[] restartAudit = await restartedContext.RiotDispatchAuditEvents
            .OrderBy(item => item.Sequence)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], restartAudit.Select(item => item.Sequence));
        Assert.Equal("POST_CREATE_RECONCILIATION", restartAudit[^1].Phase);
        Assert.Equal("UNKNOWN", restartAudit[^1].Outcome);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task TerminalObservationIsPersistedAndFrozenIdentityMismatchNeverConfirms()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        DateTimeOffset now = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
        OrderIntent terminalIntent = new(
            "LEG-TERMINAL", "D-TERMINAL", "UPPER-TERMINAL", "TO_PICKUP", "ST-12", now,
            "AGV-8005-01", 29, 12, 4, 7);
        await store.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot("D-TERMINAL", "SUBLOT-T|WIRE_TO_GATE", 1, "history-1", 1, now),
            terminalIntent,
            TestContext.Current.CancellationToken);
        StaticRiotGateway exactTerminal = new(new RiotOrderObservation(
            terminalIntent.UpperId, RiotOrderObservationKind.Terminal, "ORDER-T", 2,
            terminalIntent.VehicleKey, terminalIntent.MapId, terminalIntent.DestinationStationId));

        MovementDispatchResult terminal = await new MovementDispatchService(store, exactTerminal)
            .ReconcileOrCreateAsync(terminalIntent.UpperId, TestContext.Current.CancellationToken);
        MovementDispatchResult terminalReplay = await new MovementDispatchService(store, new ThrowingRiotGateway())
            .ReconcileOrCreateAsync(terminalIntent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.TerminalReconciliationRequired, terminal.Outcome);
        Assert.Equal(terminal, terminalReplay);
        Assert.Equal("TERMINAL_RECONCILIATION_REQUIRED", (await context.OrderIntents.SingleAsync(
            TestContext.Current.CancellationToken)).Status);

        await using SqliteConnection mismatchConnection = new("Data Source=:memory:");
        await mismatchConnection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext mismatchContext = await CreateContextAsync(mismatchConnection);
        WireToGateStore mismatchStore = new(mismatchContext);
        OrderIntent mismatchIntent = terminalIntent with
        {
            MovementLegId = "LEG-MISMATCH",
            DemandId = "D-MISMATCH",
            UpperId = "UPPER-MISMATCH"
        };
        await mismatchStore.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot("D-MISMATCH", "SUBLOT-M|WIRE_TO_GATE", 1, "history-1", 1, now),
            mismatchIntent,
            TestContext.Current.CancellationToken);
        StaticRiotGateway wrongVehicle = new(new RiotOrderObservation(
            mismatchIntent.UpperId, RiotOrderObservationKind.Active, "ORDER-M", 3,
            "DIFFERENT-VEHICLE", mismatchIntent.MapId, mismatchIntent.DestinationStationId));

        MovementDispatchResult mismatch = await new MovementDispatchService(mismatchStore, wrongVehicle)
            .ReconcileOrCreateAsync(mismatchIntent.UpperId, TestContext.Current.CancellationToken);

        Assert.Equal(MovementDispatchOutcome.ResultUnknown, mismatch.Outcome);
        Assert.Null((await mismatchContext.OrderIntents.SingleAsync(
            TestContext.Current.CancellationToken)).OrderId);
    }

    private static async Task<ControlServerDbContext> CreateContextAsync(SqliteConnection connection)
    {
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        ControlServerDbContext context = new(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return context;
    }

    private static AcceptedDemandSnapshot Snapshot(string demandId = "D-001", long catalogRevision = 21)
    {
        DateTimeOffset createdAt = new(2026, 8, 25, 8, 0, 0, TimeSpan.Zero);
        return new AcceptedDemandSnapshot(
            demandId,
            "SUBLOT-001|WIRE_TO_GATE",
            7,
            "11111111-1111-4111-8111-111111111111",
            catalogRevision,
            new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero),
            "SERIES-001",
            "WIRE_TO_GATE",
            "SUBLOT-001",
            1,
            createdAt,
            createdAt.AddMinutes(1),
            "TRACE-001",
            "COMMIT-001",
            new LiveMesFieldSet("AREA-01", "EQP-01", "STEP-01", createdAt, "PKG-01"));
    }

    private sealed class RecordingMesCatalog(params AcceptedDemandSnapshot[] current) : IMesIngestCatalog
    {
        public int ReadCount { get; private set; }

        public Task<DemandCatalogSnapshot> ReadCatalogAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ReadCount++;
            AcceptedDemandSnapshot first = current.First();
            return Task.FromResult(new DemandCatalogSnapshot(
                first.HistoryEpoch,
                first.CatalogRevision,
                current));
        }

        public Task<AcceptedDemandSnapshot?> ReadCurrentAsync(string demandId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ReadCount++;
            return Task.FromResult(current.SingleOrDefault(candidate => candidate.DemandId == demandId));
        }
    }

    private sealed class CommitObservingRiotGateway(Func<Task<bool>> acceptanceCheck) : IRiotMovementGateway
    {
        private int reconcileCount;

        public bool AcceptanceWasCommittedBeforeCreate { get; private set; }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(
            string upperId,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            reconcileCount++;
            return Task.FromResult(reconcileCount == 1
                ? new RiotOrderObservation(upperId, RiotOrderObservationKind.NotFound, null)
                : new RiotOrderObservation(
                    upperId, RiotOrderObservationKind.Active, "ORDER-001", 3,
                    "AGV-8005-01", 25, 12));
        }

        public async Task<RiotOrderObservation> CreateAsync(
            OrderIntent intent,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            AcceptanceWasCommittedBeforeCreate = await acceptanceCheck();
            return new RiotOrderObservation(intent.UpperId, RiotOrderObservationKind.Active, "UNTRUSTED");
        }
    }

    private sealed class CountingRiotGateway : IRiotMovementGateway
    {
        public int CallCount { get; private set; }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(
            string upperId,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            CallCount++;
            return Task.FromResult(new RiotOrderObservation(upperId, RiotOrderObservationKind.Unknown, null));
        }

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            CallCount++;
            return Task.FromResult(new RiotOrderObservation(intent.UpperId, RiotOrderObservationKind.Unknown, null));
        }
    }

    private sealed class RecordingRiotGateway(RiotOrderObservation observation) : IRiotMovementGateway
    {
        public int CreateCount { get; private set; }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(string upperId, CancellationToken cancellationToken)
        {
            _ = upperId;
            _ = cancellationToken;
            return Task.FromResult(observation);
        }

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            CreateCount++;
            return Task.FromResult(new RiotOrderObservation(intent.UpperId, RiotOrderObservationKind.Active, "ORDER-001"));
        }
    }

    private sealed class SequenceRiotGateway : IRiotMovementGateway
    {
        public int CreateCount { get; private set; }
        public int ReconcileCount { get; private set; }
        public OrderIntent? CreatedIntent { get; private set; }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(
            string upperId,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ReconcileCount++;
            return Task.FromResult(ReconcileCount == 1
                ? new RiotOrderObservation(upperId, RiotOrderObservationKind.NotFound, null)
                : new RiotOrderObservation(
                    upperId, RiotOrderObservationKind.Active, "ORDER-001", 3,
                    "AGV-8005-01", 29, 12));
        }

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            CreateCount++;
            CreatedIntent = intent;
            return Task.FromResult(new RiotOrderObservation(
                intent.UpperId, RiotOrderObservationKind.Active, "UNTRUSTED-MUTATION-ORDER"));
        }
    }

    private sealed class UnknownCreateGateway(OrderIntent intent) : IRiotMovementGateway
    {
        private int reconcileCount;
        public int CreateCount { get; private set; }

        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(
            string upperId,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            reconcileCount++;
            Assert.Equal(intent.UpperId, upperId);
            return Task.FromResult(new RiotOrderObservation(upperId, RiotOrderObservationKind.NotFound, null));
        }

        public Task<RiotOrderObservation> CreateAsync(OrderIntent created, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            CreateCount++;
            Assert.Equal(intent, created);
            return Task.FromResult(new RiotOrderObservation(created.UpperId, RiotOrderObservationKind.Unknown, null));
        }
    }

    private sealed class StaticRiotGateway(RiotOrderObservation observation) : IRiotMovementGateway
    {
        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(
            string upperId,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Assert.Equal(observation.UpperId, upperId);
            return Task.FromResult(observation);
        }

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Create must not be called for this branch.");
    }

    private sealed class ThrowingRiotGateway : IRiotMovementGateway
    {
        public Task<RiotOrderObservation> ReconcileByUpperIdAsync(
            string upperId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Persisted terminal state must not query RIoT again.");

        public Task<RiotOrderObservation> CreateAsync(OrderIntent intent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Persisted terminal state must not create again.");
    }
}
