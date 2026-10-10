using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// control-server#357：旅程行的并发令牌 <c>JourneyRuntimes.Version</c> 由一个只加一列的迁移落地。钉三件事：它紧跟上一个迁移、
/// 不动任何既有行；迁移过的库上令牌真的起作用（生产库走迁移，不走 <c>EnsureCreated</c>）；降回去再升上来，行照旧。
/// </summary>
public sealed class JourneyRuntimeVersionMigrationTests
{
    private const string MigrationSuffix = "_JourneyRuntimeVersion";

    private const string PreviousMigration = "20260923152943_JourneyStopWorklistRefills";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheColumnComesInOneMigrationStraightAfterTheWorklistRefills()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        string[] migrations = [.. fixture.Context.Database.GetMigrations()];

        string migration = Assert.Single(migrations, name => name.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        Assert.Equal(PreviousMigration, migrations[Array.IndexOf(migrations, migration) - 1]);
    }

    /// <summary>
    /// 迁移之前就在的旅程：迁移之后每一个旧列逐字不变，<c>Version</c> 全为 0。
    /// </summary>
    [Fact]
    public async Task EveryJourneyAlreadyThereKeepsEveryColumnAndStartsAtVersionZero()
    {
        await using Batch7JourneyFixture fixture = await SeededAtThePreviousMigrationAsync();
        string[] oldColumns = await ColumnsAsync(fixture.Connection);
        Assert.DoesNotContain("Version", oldColumns);
        string[] before = await DumpAsync(fixture.Connection, oldColumns);

        await fixture.Context.Database.MigrateAsync(Token);

        Assert.Equal(before, await DumpAsync(fixture.Connection, oldColumns));
        Assert.Equal(["0", "0", "0"], await DumpAsync(fixture.Connection, ["Version"], quote: false));
    }

    /// <summary>
    /// 迁移过的库上：一次普通保存把版本从 0 抬到 1；拿保存之前读到的那一行再存，被拒、什么都不写。
    /// </summary>
    [Fact]
    public async Task OnAMigratedDatabaseASaveRaisesTheVersionAndASaveFromAnOlderReadIsRefused()
    {
        await using Batch7JourneyFixture fixture = await SeededAtThePreviousMigrationAsync();
        await fixture.Context.Database.MigrateAsync(Token);
        DbContextOptions<ControlServerDbContext> options =
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(fixture.Connection).Options;

        await using ControlServerDbContext stale = new(options);
        JourneyRuntimeRow staleRow = await stale.JourneyRuntimes.OrderBy(row => row.JourneyId).FirstAsync(Token);
        await using (ControlServerDbContext inbound = new(options))
        {
            JourneyRuntimeRow row = await inbound.JourneyRuntimes.SingleAsync(item => item.JourneyId == staleRow.JourneyId, Token);
            row.SetBlockReason("CANCELLED_BY_OPERATOR", Batch7JourneyFixture.Now);
            await inbound.SaveChangesAsync(Token);
            Assert.Equal(1L, row.Version);
        }

        staleRow.SetBlockReason("ONBOARD_SESSION_NOT_READY", Batch7JourneyFixture.Now);
        DbUpdateConcurrencyException conflict =
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(Token));
        Assert.True(JourneyRowConflict.Is(conflict));
        await using ControlServerDbContext reading = new(options);
        Assert.Equal(
            ("CANCELLED_BY_OPERATOR", 1L),
            await reading.JourneyRuntimes.AsNoTracking().Where(row => row.JourneyId == staleRow.JourneyId)
                .Select(row => ValueTuple.Create(row.BlockReasonCode, row.Version)).SingleAsync(Token));
    }

    [Fact]
    public async Task MigratingDownDropsTheColumnAndMigratingUpAgainLeavesEveryOtherColumnTheSame()
    {
        await using Batch7JourneyFixture fixture = await SeededAtThePreviousMigrationAsync();
        // The columns the previous migration left, read before migrating up: later migrations append their own columns
        // too (control-server#399), and migrating down to the previous one takes those away as well.
        string[] oldColumns = await ColumnsAsync(fixture.Connection);
        Assert.DoesNotContain("Version", oldColumns);
        await fixture.Context.Database.MigrateAsync(Token);
        string[] after = await DumpAsync(fixture.Connection, oldColumns);

        await fixture.Context.GetService<IMigrator>().MigrateAsync(PreviousMigration, Token);
        Assert.DoesNotContain("Version", await ColumnsAsync(fixture.Connection));
        Assert.Equal(after, await DumpAsync(fixture.Connection, oldColumns));
        await fixture.Context.Database.MigrateAsync(Token);

        Assert.Equal(after, await DumpAsync(fixture.Connection, oldColumns));
        Assert.Equal(["0", "0", "0"], await DumpAsync(fixture.Connection, ["Version"], quote: false));
    }

    [Fact]
    public async Task TheColumnIsNotNullAndTheModelSnapshotMatchesTheModel()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateMigratedForRealAsync();
        await using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText = "SELECT \"notnull\" FROM pragma_table_info('JourneyRuntimes') WHERE name = 'Version'";

        Assert.Equal(1L, await command.ExecuteScalarAsync(Token));
        Assert.False(fixture.Context.Database.HasPendingModelChanges());
    }

    /// <summary>
    /// 一个迁到上一个迁移为止的库，里面三趟旅程：在一个迁到最新的草稿库上走真实受理路径建出来，再按旧库有的列拷过去。
    /// 受理路径按当前模型写，旧库没有 Version 列，所以不能直接在旧库上受理。
    /// </summary>
    private static async Task<Batch7JourneyFixture> SeededAtThePreviousMigrationAsync()
    {
        Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        await fixture.Context.GetService<IMigrator>().MigrateAsync(PreviousMigration, Token);
        await using Batch7JourneyFixture scratch = await Batch7JourneyFixture.CreateMigratedForRealAsync();
        string[] demands = ["D-1", "D-2", "D-3"];
        for (int index = 0; index < demands.Length; index++)
        {
            await Batch7JourneyFixture.AcceptAsync(
                scratch.Context, demands[index], $"agv-0{index + 1}", $"VK-0{index + 1}", Batch7JourneyFixture.Now.AddSeconds(index));
        }
        string[] columns = await ColumnsAsync(fixture.Connection);
        string list = string.Join(", ", columns.Select(column => $"\"{column}\""));
        await using SqliteCommand select = scratch.Connection.CreateCommand();
        select.CommandText = $"SELECT {list} FROM JourneyRuntimes";
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            await using SqliteCommand insert = fixture.Connection.CreateCommand();
            insert.CommandText =
                $"INSERT INTO JourneyRuntimes ({list}) VALUES ({string.Join(", ", columns.Select((_, index) => $"$p{index}"))})";
            for (int index = 0; index < columns.Length; index++)
            {
                insert.Parameters.AddWithValue($"$p{index}", reader.GetValue(index));
            }
            await insert.ExecuteNonQueryAsync(Token);
        }
        return fixture;
    }

    private static async Task<string[]> ColumnsAsync(SqliteConnection connection)
    {
        List<string> columns = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('JourneyRuntimes')";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            columns.Add(reader.GetString(1));
        }
        columns.Sort(StringComparer.Ordinal);
        return [.. columns];
    }

    private static async Task<string[]> DumpAsync(SqliteConnection connection, string[] columns, bool quote = true)
    {
        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText = quote
            ? $"SELECT {string.Join(" || '|' || ", columns.Select(name => $"'{name}=' || quote(\"{name}\")"))} FROM JourneyRuntimes"
            : $"SELECT CAST(\"{columns.Single()}\" AS TEXT) FROM JourneyRuntimes";
        List<string> rows = [];
        await using SqliteDataReader reader = await select.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add(reader.GetString(0));
        }
        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }
}
