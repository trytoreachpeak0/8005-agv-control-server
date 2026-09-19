using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// control-server#228：「准入被撤」的等待起点从阻断码上挪到独立的持久列 <c>JourneyRuntimes.AreaEndAdmissionRevokedSince</c>，
/// 由一个只加一列的迁移落地。这里钉它对已在等的旅程的回填：在直接迁到批次 7 的库上用原始 SQL 种数据，再迁到最新。
/// </summary>
public sealed class AreaEndAdmissionRevokedSinceMigrationTests
{
    internal const string MigrationSuffix = "_AreaEndAdmissionRevokedSince";

    private const string Batch7Migration = "20260919154546_Batch7MultiDemandJourneyPersistence";

    [Fact]
    public async Task TheColumnComesInOneMigrationStraightAfterBatch7()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        string[] migrations = [.. fixture.Context.Database.GetMigrations()];

        string migration = Assert.Single(migrations, name => name.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        Assert.Equal(Batch7Migration, migrations[Array.IndexOf(migrations, migration) - 1]);
    }

    /// <summary>
    /// A stop held at its AREA machine when the column arrives -- still waiting, or already escalated -- takes its wait's
    /// start from the block it carries, whose start is the first hold's (control-server#198 kept it through the escalation).
    /// Every other journey starts with nothing: one that is not held has no wait, and one whose hold was overwritten by
    /// another code (the defect this ticket fixes) no longer says when its wait began, so its next hold starts the count.
    /// Nothing else in the table changes.
    /// </summary>
    [Fact]
    public async Task AJourneyAlreadyHeldAtItsMachineKeepsTheStartOfItsWaitAndNoOtherJourneyGetsOne()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Context.GetService<IMigrator>().MigrateAsync(Batch7Migration, cancellationToken);
        await SeedBatch7JourneysAsync(fixture);
        string[] columnsAtBatch7 = await ColumnsAsync(fixture.Connection, "JourneyRuntimes");
        string[] before = await DumpAsync(fixture.Connection, columnsAtBatch7);

        await fixture.Context.Database.MigrateAsync(cancellationToken);

        Assert.Equal(before, await DumpAsync(fixture.Connection, columnsAtBatch7));
        Assert.Equal(
            [
                "D-BLOCKED-OTHER -",
                "D-COMPLETED -",
                "D-ESCALATED '2026-09-19 09:04:00+00:00'",
                "D-HELD '2026-09-19 09:05:00+00:00'",
                "D-ORDER-FAILED -",
                "D-TO-GATE -",
            ],
            await SinceByDemandAsync(fixture.Connection));
    }

    [Fact]
    public async Task MigratingDownDropsTheColumnAndMigratingUpAgainFillsItTheSameWay()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Context.GetService<IMigrator>().MigrateAsync(Batch7Migration, cancellationToken);
        await SeedBatch7JourneysAsync(fixture);
        await fixture.Context.Database.MigrateAsync(cancellationToken);
        string[] after = await SinceByDemandAsync(fixture.Connection);

        await fixture.Context.GetService<IMigrator>().MigrateAsync(Batch7Migration, cancellationToken);
        Assert.DoesNotContain("AreaEndAdmissionRevokedSince", await ColumnsAsync(fixture.Connection, "JourneyRuntimes"));
        await fixture.Context.Database.MigrateAsync(cancellationToken);

        Assert.Equal(after, await SinceByDemandAsync(fixture.Connection));
    }

    [Fact]
    public async Task TheColumnIsNullableAndTheModelSnapshotMatchesTheModel()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText =
            "SELECT \"notnull\" FROM pragma_table_info('JourneyRuntimes') WHERE name = 'AreaEndAdmissionRevokedSince'";

        Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.False(fixture.Context.Database.HasPendingModelChanges());
    }

    /// <summary>
    /// Six journeys accepted through the real acceptance path on a scratch database, copied over the columns batch 7 has,
    /// then put by raw SQL where production can hold them.
    /// </summary>
    private static async Task SeedBatch7JourneysAsync(Batch7JourneyFixture fixture)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DateTimeOffset now = Batch7JourneyFixture.Now;
        string[] demands = ["D-HELD", "D-ESCALATED", "D-ORDER-FAILED", "D-TO-GATE", "D-BLOCKED-OTHER", "D-COMPLETED"];

        await using Batch7JourneyFixture scratch = await Batch7JourneyFixture.CreateAsync();
        for (int index = 0; index < demands.Length; index++)
        {
            await Batch7JourneyFixture.AcceptAsync(
                scratch.Context, demands[index], $"agv-0{index + 1}", $"VK-0{index + 1}", now.AddSeconds(index));
        }
        await CopyRowsAsync(scratch.Connection, fixture.Connection, "JourneyRuntimes",
            await ColumnsAsync(fixture.Connection, "JourneyRuntimes"));

        await using SqliteCommand raw = fixture.Connection.CreateCommand();
        raw.CommandText =
            """
            -- Held at its AREA machine for the station's admission, still waiting (control-server#198).
            UPDATE JourneyRuntimes SET Stage = 'AwaitingGateArrival', BlockReasonCode = 'TASK_TYPE_NOT_ALLOWED_AT_STATION',
                BlockReasonSince = '2026-09-19 09:05:00+00:00' WHERE DemandId = 'D-HELD';
            -- Held, then escalated past the threshold: the escalation kept the first hold's start.
            UPDATE JourneyRuntimes SET Stage = 'Blocked', BlockReasonCode = 'TASK_TYPE_NOT_ALLOWED_AT_STATION_TIMEOUT',
                BlockReasonSince = '2026-09-19 09:04:00+00:00' WHERE DemandId = 'D-ESCALATED';
            -- Held, then overwritten by a failed order: when the wait began is no longer on the row.
            UPDATE JourneyRuntimes SET Stage = 'AwaitingGateArrival', BlockReasonCode = 'VEHICLE_ORDER_FAILED',
                BlockReasonSince = '2026-09-19 09:08:00+00:00' WHERE DemandId = 'D-ORDER-FAILED';
            -- On its way to the machine, nothing held.
            UPDATE JourneyRuntimes SET Stage = 'AwaitingGateArrival', BlockReasonCode = NULL, BlockReasonSince = NULL
                WHERE DemandId = 'D-TO-GATE';
            -- Blocked for a different recovery.
            UPDATE JourneyRuntimes SET Stage = 'Blocked', BlockReasonCode = 'UNLOAD_RESULT_REQUIRES_RECOVERY',
                BlockReasonSince = '2026-09-19 09:06:00+00:00' WHERE DemandId = 'D-BLOCKED-OTHER';
            UPDATE JourneyRuntimes SET Stage = 'Completed' WHERE DemandId = 'D-COMPLETED';
            """;
        await raw.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string[]> SinceByDemandAsync(SqliteConnection connection)
    {
        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText =
            "SELECT DemandId || ' ' || CASE WHEN AreaEndAdmissionRevokedSince IS NULL THEN '-' ELSE quote(AreaEndAdmissionRevokedSince) END FROM JourneyRuntimes";
        List<string> rows = [];
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }
        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

    private static async Task CopyRowsAsync(SqliteConnection from, SqliteConnection to, string table, string[] columns)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string list = string.Join(", ", columns.Select(column => $"\"{column}\""));
        await using SqliteCommand select = from.CreateCommand();
        select.CommandText = $"SELECT {list} FROM \"{table}\"";
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await using SqliteCommand insert = to.CreateCommand();
            insert.CommandText =
                $"INSERT INTO \"{table}\" ({list}) VALUES ({string.Join(", ", columns.Select((_, index) => $"$p{index}"))})";
            for (int index = 0; index < columns.Length; index++)
            {
                insert.Parameters.AddWithValue($"$p{index}", reader.GetValue(index));
            }
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<string[]> ColumnsAsync(SqliteConnection connection, string table)
    {
        List<string> columns = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}')";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add(reader.GetString(1));
        }
        columns.Sort(StringComparer.Ordinal);
        return [.. columns];
    }

    private static async Task<string[]> DumpAsync(SqliteConnection connection, string[] columns)
    {
        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText =
            $"SELECT {string.Join(" || '|' || ", columns.Select(name => $"'{name}=' || quote(\"{name}\")"))} FROM JourneyRuntimes";
        List<string> rows = [];
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }
        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }
}
