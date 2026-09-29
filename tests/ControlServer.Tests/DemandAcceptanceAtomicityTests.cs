using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

public sealed class DemandAcceptanceAtomicityTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("ProtocolVector", "CV-DEMAND-ACCEPT-TO-PICKUP")]
    public async Task AcceptedDemandAndToPickupIntentAreCommittedAtomically()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext dbContext = new(options);
        await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        DemandAcceptanceStore store = new(dbContext);
        DateTimeOffset now = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

        await store.AcceptWithOrderIntentAsync(
            new AcceptedDemandSnapshot("D-001", "SUBLOT-001|WIRE_TO_GATE", 7, "history-1", 21, now),
            new OrderIntent(
                "LEG-001", "D-001", "W2G-D-001-PICKUP-1", "TO_PICKUP", "ST-PICKUP", now,
                "AGV-8005-01", 29, 12, 1, 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, await dbContext.AcceptedDemands.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await dbContext.Set<VehiclePurposeClaimRecordRow>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await dbContext.OrderIntents.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-04")]
    public async Task VehicleLeaseRejectsSecondDemandUntilSuccessfulUnloadReleasesIt()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext dbContext = new(options);
        await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        WireToGateStore store = new(dbContext);
        DateTimeOffset now = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

        await store.AcceptWithOrderIntentAsync(
            Snapshot("D-001", "SUBLOT-001|WIRE_TO_GATE", now),
            PickupIntent("D-001", "LEG-001", "W2G-D-001-PICKUP-1", now),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<BusinessIdentityConflictException>(() => store.AcceptWithOrderIntentAsync(
            Snapshot("D-002", "SUBLOT-002|WIRE_TO_GATE", now.AddMinutes(1)),
            PickupIntent("D-002", "LEG-002", "W2G-D-002-PICKUP-1", now.AddMinutes(1)),
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await dbContext.AcceptedDemands.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await dbContext.Set<VehiclePurposeClaimRecordRow>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await dbContext.OrderIntents.CountAsync(TestContext.Current.CancellationToken));

        await store.CompleteDemandAfterUnloadAsync(
            "UNLOAD-001",
            "D-001",
            "SUBLOT-001|WIRE_TO_GATE",
            7,
            [new SlotPhysicalEvidence(1, SlotBusinessState.Empty, true, true)],
            "all-empty-locked-output-reset",
            now.AddMinutes(2),
            TestContext.Current.CancellationToken);

        await store.AcceptWithOrderIntentAsync(
            Snapshot("D-002", "SUBLOT-002|WIRE_TO_GATE", now.AddMinutes(3)),
            PickupIntent("D-002", "LEG-002", "W2G-D-002-PICKUP-1", now.AddMinutes(3)),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, await dbContext.AcceptedDemands.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            1,
            await dbContext.Set<VehiclePurposeClaimRecordRow>().CountAsync(
                record => record.ReleasedAt == null,
                TestContext.Current.CancellationToken));
        Assert.Equal(2, await dbContext.OrderIntents.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task VehicleLeaseMigrationBackfillsAnUnresolvedAcceptedDemand()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using ControlServerDbContext dbContext = new(options);
        IMigrator migrator = dbContext.GetService<IMigrator>();
        await migrator.MigrateAsync(
            "20260826080712_ExactAcceptedDemandSnapshot",
            TestContext.Current.CancellationToken);
        // Seed through the historical schema. Using the current EF model here would try to
        // write columns that intentionally do not exist until later migrations.
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO AcceptedDemands
                (DemandId, SeriesId, TransportDemandKey, WorkType, Sublot, Generation,
                 DemandRevision, HistoryEpoch, CatalogRevision, CreatedAt, ValueObservedAt,
                 ValuePollTraceId, ValueProjectionCommitId, LiveMesFieldsJson, AcceptedAt, Status)
            VALUES
                ('D-001', 'SERIES-001', 'SUBLOT-001|WIRE_TO_GATE', 'WIRE_TO_GATE', 'SUBLOT-001', 1,
                 7, 'history-1', 21, '2026-08-26 09:00:00+00:00', '2026-08-26 09:59:00+00:00',
                 'TRACE-001', 'COMMIT-001', '{{}}', '2026-08-26 10:00:00+00:00', 'Accepted');

            INSERT INTO OrderIntents
                (MovementLegId, DemandId, UpperId, Purpose, TargetStationId, VehicleKey,
                 MapId, DestinationStationId, AgvLifecycleGeneration, DispatchGeneration,
                 CreatedAt, Status, OrderId)
            VALUES
                ('LEG-001', 'D-001', 'W2G-D-001-PICKUP-1', 'TO_PICKUP', 'ST-PICKUP', 'AGV-8005-01',
                 29, 12, 1, 1, '2026-08-26 10:00:00+00:00', 'PENDING_RECONCILIATION', NULL);
            """,
            TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        // The lease that old migration backfilled became a purpose claim in batch 7 and, when batch 8-16 (control-server#387)
        // dropped the lease table, an open claim record: the vehicle is still held for that demand's journey.
        VehiclePurposeClaimRecordRow record = await dbContext.Set<VehiclePurposeClaimRecordRow>()
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("journey:D-001", record.JourneyId);
        Assert.Equal("AGV-8005-01", record.VehicleKey);
        Assert.Null(record.ReleasedAt);
        Assert.Equal(
            ("AGV-8005-01", "journey:D-001"),
            await dbContext.Set<VehiclePurposeClaimRow>().AsNoTracking()
                .Select(claim => ValueTuple.Create(claim.VehicleKey, claim.JourneyId))
                .SingleAsync(TestContext.Current.CancellationToken));
    }

    private static AcceptedDemandSnapshot Snapshot(
        string demandId,
        string transportDemandKey,
        DateTimeOffset acceptedAt) =>
        new(demandId, transportDemandKey, 7, "history-1", 21, acceptedAt);

    private static OrderIntent PickupIntent(
        string demandId,
        string movementLegId,
        string upperId,
        DateTimeOffset createdAt) =>
        new(
            movementLegId,
            demandId,
            upperId,
            "TO_PICKUP",
            "ST-PICKUP",
            createdAt,
            "AGV-8005-01",
            29,
            12,
            1,
            1);
}
