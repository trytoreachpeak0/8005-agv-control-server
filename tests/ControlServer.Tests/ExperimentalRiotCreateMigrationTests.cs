using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

public sealed class ExperimentalRiotCreateMigrationTests
{
    [Fact]
    public async Task UpgradeFromDurableCreateAuditPreservesHistoricalUnknownsAndCreatesPermitUniquenessConstraints()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        DbContextOptions<ControlServerDbContext> options =
            new DbContextOptionsBuilder<ControlServerDbContext>()
                .UseSqlite(connection)
                .Options;
        await using ControlServerDbContext context = new(options);
        IMigrator migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(
            "20260828082910_DurableRiotCreateAttemptAudit",
            TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO OrderIntents
                (MovementLegId, DemandId, UpperId, Purpose, TargetStationId, VehicleKey,
                 MapId, DestinationStationId, AgvLifecycleGeneration, DispatchGeneration,
                 CreatedAt, Status, OrderId, DispatchAuditVersion, CreateAttemptCount, CreateAttemptId)
            VALUES
                ('LEG-HISTORICAL', 'D-HISTORICAL', 'UPPER-HISTORICAL', 'TO_PICKUP', 'ST-12',
                 'AGV-8005-01', 25, 12, 4, 7, '2026-08-28 08:00:00+00:00',
                 'RESULT_UNKNOWN', NULL, 1, 1, 'ATTEMPT-HISTORICAL')
            """,
            TestContext.Current.CancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO RiotDispatchAuditEvents
                (AuditEventId, MovementLegId, DemandId, UpperId, DispatchGeneration,
                 Sequence, AttemptId, AttemptNumber, Phase, Outcome, OccurredAt)
            VALUES
                ('AUDIT-HISTORICAL', 'LEG-HISTORICAL', 'D-HISTORICAL', 'UPPER-HISTORICAL', 7,
                 1, 'ATTEMPT-HISTORICAL', 1, 'CREATE_RESPONSE', 'UNKNOWN',
                 '2026-08-28 08:01:00+00:00')
            """,
            TestContext.Current.CancellationToken);

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(
            "20260828103631_ExperimentalAbsentObservationCreateAuthorization",
            await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await context.Set<ExperimentalRiotCreateAuthorizationRow>()
            .AsNoTracking()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        RiotDispatchAuditEventRow historicalAudit = await context.RiotDispatchAuditEvents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("AUDIT-HISTORICAL", historicalAudit.AuditEventId);
        Assert.Null(historicalAudit.ExperimentalAuthorizationId);
        Assert.Null(historicalAudit.EligibilityBasis);
        OrderIntentRow historicalIntent = await context.OrderIntents
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal("RESULT_UNKNOWN", historicalIntent.Status);
        Assert.Equal(1, historicalIntent.CreateAttemptCount);
        Assert.Equal("ATTEMPT-HISTORICAL", historicalIntent.CreateAttemptId);
        Assert.Null(historicalIntent.DispatchAuditSequence);
        Assert.Null(historicalIntent.ExperimentalCreateAuthorizationId);

        Dictionary<string, (bool Unique, bool Partial)> indexes = await ReadIndexesAsync(
            connection,
            "ExperimentalRiotCreateAuthorizations",
            TestContext.Current.CancellationToken);
        Assert.True(indexes.TryGetValue(
            "IX_ExperimentalRiotCreateAuthorizations_UpperId",
            out (bool Unique, bool Partial) upperId));
        Assert.True(upperId.Unique);
        Assert.False(upperId.Partial);
        Assert.True(indexes.TryGetValue(
            "IX_ExperimentalRiotCreateAuthorizations_ConsumedByAttemptId",
            out (bool Unique, bool Partial) consumedAttempt));
        Assert.True(consumedAttempt.Unique);
        Assert.True(consumedAttempt.Partial);
    }

    private static async Task<Dictionary<string, (bool Unique, bool Partial)>> ReadIndexesAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list('{table}')";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        Dictionary<string, (bool Unique, bool Partial)> result = new(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            string name = reader.GetString(1);
            bool unique = reader.GetInt64(2) == 1;
            bool partial = reader.GetInt64(4) == 1;
            result.Add(name, (unique, partial));
        }
        return result;
    }
}
