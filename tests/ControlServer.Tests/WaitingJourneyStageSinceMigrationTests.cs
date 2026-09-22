using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// control-server#273：<c>JourneyRuntimes</c> 加阶段起点 <c>StageSince</c> 与三列等人电量记录，由一个迁移落地。这里钉它对在途旅程的回填：
/// 在迁到上一张迁移的库上用原始 SQL 种数据，再迁到最新。
/// </summary>
public sealed class WaitingJourneyStageSinceMigrationTests
{
    private const string MigrationSuffix = "_WaitingJourneyStageSince";

    private const string PreviousMigration = "20260920145604_Batch7LoadingMembershipBackfill";

    [Fact]
    public async Task TheColumnsComeInOneMigrationStraightAfterTheLastBatch7One()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        string[] migrations = [.. fixture.Context.Database.GetMigrations()];

        string migration = Assert.Single(migrations, name => name.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        Assert.Equal(PreviousMigration, migrations[Array.IndexOf(migrations, migration) - 1]);
    }

    /// <summary>
    /// 在途旅程取记下来的最好的起点：有原因码起点的取它（SetStage 会清掉原因码，所以它不会早于所属的阶段），没有的取最后一次写行的时刻。
    /// 两者都只会晚于真实起点，所以已在等的旅程读起来比实际短、报得晚，从不提前报。已完成的旅程不回填；电量三列一律为空；别的列一个字节不动。
    /// </summary>
    [Fact]
    public async Task AJourneyUnderWayGetsTheBestStartThatWasRecordedAndACompletedOneGetsNone()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Context.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken);
        await SeedJourneysAsync(fixture);
        string[] columnsBefore = await ColumnsAsync(fixture.Connection);
        string[] before = await DumpAsync(fixture.Connection, columnsBefore);

        await fixture.Context.Database.MigrateAsync(cancellationToken);

        Assert.Equal(before, await DumpAsync(fixture.Connection, columnsBefore));
        Assert.Equal(
            [
                "D-BLOCKED '2026-09-21 18:00:00+00:00' - - -",
                "D-COMPLETED - - - -",
                "D-GATE '2026-09-21 16:40:00+00:00' - - -",
                "D-HANG '2026-09-21 17:05:00+00:00' - - -",
            ],
            await NewColumnsByDemandAsync(fixture.Connection));
    }

    [Fact]
    public async Task MigratingDownDropsTheColumnsAndMigratingUpAgainFillsThemTheSameWay()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await fixture.Context.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken);
        await SeedJourneysAsync(fixture);
        await fixture.Context.Database.MigrateAsync(cancellationToken);
        string[] after = await NewColumnsByDemandAsync(fixture.Connection);

        await fixture.Context.GetService<IMigrator>().MigrateAsync(PreviousMigration, cancellationToken);
        string[] columns = await ColumnsAsync(fixture.Connection);
        Assert.DoesNotContain("StageSince", columns);
        Assert.DoesNotContain("WaitingBatteryPercent", columns);
        Assert.DoesNotContain("WaitingBatteryObservedAt", columns);
        Assert.DoesNotContain("WaitingWarnedAt", columns);
        await fixture.Context.Database.MigrateAsync(cancellationToken);

        Assert.Equal(after, await NewColumnsByDemandAsync(fixture.Connection));
    }

    [Fact]
    public async Task TheColumnsAreNullableAndTheModelSnapshotMatchesTheModel()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();
        await using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*) FROM pragma_table_info('JourneyRuntimes')
            WHERE name IN ('StageSince', 'WaitingBatteryPercent', 'WaitingBatteryObservedAt', 'WaitingWarnedAt')
              AND "notnull" = 0
            """;

        Assert.Equal(4L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.False(fixture.Context.Database.HasPendingModelChanges());
    }

    /// <summary>四趟旅程经真实受理路径在一个草稿库上建出，照上一张迁移的列拷过来，再用原始 SQL 放到生产能到的样子。</summary>
    private static async Task SeedJourneysAsync(Batch7JourneyFixture fixture)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string[] demands = ["D-GATE", "D-BLOCKED", "D-HANG", "D-COMPLETED"];
        await using Batch7JourneyFixture scratch = await Batch7JourneyFixture.CreateAsync();
        for (int index = 0; index < demands.Length; index++)
        {
            await Batch7JourneyFixture.AcceptAsync(
                scratch.Context, demands[index], $"agv-0{index + 1}", $"VK-0{index + 1}",
                Batch7JourneyFixture.Now.AddSeconds(index));
        }
        await CopyRowsAsync(scratch.Connection, fixture.Connection, await ColumnsAsync(fixture.Connection));

        await using SqliteCommand raw = fixture.Connection.CreateCommand();
        raw.CommandText =
            """
            -- 闸口等卸货，没有原因码：只剩最后一次写行的时刻。
            UPDATE JourneyRuntimes SET Stage = 'AwaitingUnloadResult', BlockReasonCode = NULL, BlockReasonSince = NULL,
                UpdatedAt = '2026-09-21 16:40:00+00:00' WHERE DemandId = 'D-GATE';
            -- 阻断：原因码的起点早于最后一次写行，取原因码的。
            UPDATE JourneyRuntimes SET Stage = 'Blocked', BlockReasonCode = 'UNLOAD_RESULT_REQUIRES_RECOVERY',
                BlockReasonSince = '2026-09-21 18:00:00+00:00', UpdatedAt = '2026-09-21 18:30:00+00:00'
                WHERE DemandId = 'D-BLOCKED';
            -- 路上被挂起、引擎命名了原因：同样取原因码的。
            UPDATE JourneyRuntimes SET Stage = 'AwaitingGateArrival', BlockReasonCode = 'VEHICLE_ORDER_FAILED',
                BlockReasonSince = '2026-09-21 17:05:00+00:00', UpdatedAt = '2026-09-21 17:09:00+00:00'
                WHERE DemandId = 'D-HANG';
            UPDATE JourneyRuntimes SET Stage = 'Completed', UpdatedAt = '2026-09-21 12:00:00+00:00'
                WHERE DemandId = 'D-COMPLETED';
            """;
        await raw.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string[]> NewColumnsByDemandAsync(SqliteConnection connection)
    {
        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText =
            """
            SELECT DemandId
                || ' ' || CASE WHEN StageSince IS NULL THEN '-' ELSE quote(StageSince) END
                || ' ' || CASE WHEN WaitingBatteryPercent IS NULL THEN '-' ELSE quote(WaitingBatteryPercent) END
                || ' ' || CASE WHEN WaitingBatteryObservedAt IS NULL THEN '-' ELSE quote(WaitingBatteryObservedAt) END
                || ' ' || CASE WHEN WaitingWarnedAt IS NULL THEN '-' ELSE quote(WaitingWarnedAt) END
            FROM JourneyRuntimes
            """;
        List<string> rows = [];
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }
        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

    private static async Task CopyRowsAsync(SqliteConnection from, SqliteConnection to, string[] columns)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string list = string.Join(", ", columns.Select(column => $"\"{column}\""));
        await using SqliteCommand select = from.CreateCommand();
        select.CommandText = $"SELECT {list} FROM \"JourneyRuntimes\"";
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await using SqliteCommand insert = to.CreateCommand();
            insert.CommandText =
                $"INSERT INTO \"JourneyRuntimes\" ({list}) VALUES ({string.Join(", ", columns.Select((_, index) => $"$p{index}"))})";
            for (int index = 0; index < columns.Length; index++)
            {
                insert.Parameters.AddWithValue($"$p{index}", reader.GetValue(index));
            }
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<string[]> ColumnsAsync(SqliteConnection connection)
    {
        List<string> columns = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('JourneyRuntimes')";
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
