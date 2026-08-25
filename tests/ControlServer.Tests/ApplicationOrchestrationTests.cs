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
    public async Task IntakeUsesFinalMesReadBeforeAtomicCommit()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using ControlServerDbContext context = await CreateContextAsync(connection);
        WireToGateStore store = new(context);
        AcceptedDemandSnapshot discovered = new(
            "D-001", "SUBLOT-001|WIRE_TO_GATE", 7, "history-1", 20,
            new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero));
        RecordingMesCatalog catalog = new(discovered with { CatalogRevision = 21 });
        DemandIntakeService service = new(catalog, store);

        DemandIntakeOutcome outcome = await service.AcceptAsync(
            discovered,
            new OrderIntent("LEG-001", "D-001", "UPPER-001", "TO_PICKUP", "ST-PICKUP", discovered.AcceptedAt),
            TestContext.Current.CancellationToken);

        Assert.Equal(DemandIntakeOutcome.Accepted, outcome);
        Assert.Equal(1, catalog.ReadCount);
        Assert.Equal(21, (await context.AcceptedDemands.SingleAsync(TestContext.Current.CancellationToken)).CatalogRevision);
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

    private static async Task<ControlServerDbContext> CreateContextAsync(SqliteConnection connection)
    {
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        ControlServerDbContext context = new(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return context;
    }

    private sealed class RecordingMesCatalog(AcceptedDemandSnapshot current) : IMesIngestCatalog
    {
        public int ReadCount { get; private set; }

        public Task<AcceptedDemandSnapshot?> ReadCurrentAsync(string demandId, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            ReadCount++;
            return Task.FromResult<AcceptedDemandSnapshot?>(current.DemandId == demandId ? current : null);
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
                : new RiotOrderObservation(upperId, RiotOrderObservationKind.Active, "ORDER-001"));
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
}
