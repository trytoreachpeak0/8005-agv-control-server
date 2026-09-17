using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// 批次 5 只有一个迁移，由建表票 control-server#80 独占；批次 5 其余票零迁移（规格 18.2）。
/// </summary>
/// <remarks>
/// 与 <see cref="Batch4MigrationDisciplineTests"/> 同一个理由，也同一个做法：批次 3 那份测试的 <c>MigrationsAfterBatch3</c> 名单点了本迁移的名，
/// 这里只放批次 5 自己的断言。本迁移在 #66 的批次 4 迁移合入 <c>fp/v2-impl</c> 之后生成，所以紧跟在它后面。
/// </remarks>
public sealed class Batch5MigrationDisciplineTests
{
    private const string MigrationBeforeBatch5 = "20260915135043_Batch4AreaAssignmentAndStructuralDispatchBlock";
    private const string Batch5Migration = "20260917015519_Batch5JourneyBlockReasonSince";

    [Fact]
    public async Task Batch5AddsExactlyOneMigrationAndItComesStraightAfterTheBatch4Migration()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        string[] migrations = [.. fixture.Context.Database.GetMigrations()];

        string batch5 = Assert.Single(migrations, name => name.Contains("Batch5", StringComparison.Ordinal));
        Assert.Equal(Batch5Migration, batch5);
        Assert.Equal(MigrationBeforeBatch5, migrations[Array.IndexOf(migrations, batch5) - 1]);
    }

    [Fact]
    public async Task TheMigrationAddsTheBlockStartColumnAndBackFillsNothing()
    {
        // Only AddColumn: a start nobody recorded stays unknown. Back-filling it from UpdatedAt would put a time on
        // the dashboard that is only the last write, which is exactly the misreading the column exists to end.
        // How such a block is shown is BlockedJourneyDashboardTests.ABlockAlreadyHeldWhenTheColumnWasAddedSaysItsStartIsUnknown.
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        await fixture.MigrateToAsync(MigrationBeforeBatch5);
        Assert.DoesNotContain("BlockReasonSince", await ColumnsAsync(fixture.Connection, "JourneyRuntimes"));

        await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Contains("BlockReasonSince", await ColumnsAsync(fixture.Connection, "JourneyRuntimes"));
    }

    [Fact]
    public async Task TheModelSnapshotMatchesTheModel()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();

        Assert.False(fixture.Context.Database.HasPendingModelChanges());
    }

    private static async Task<HashSet<string>> ColumnsAsync(SqliteConnection connection, string table)
    {
        HashSet<string> columns = new(StringComparer.Ordinal);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}')";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private sealed class MigrationFixture : IAsyncDisposable
    {
        private MigrationFixture(SqliteConnection connection, ControlServerDbContext context)
        {
            Connection = connection;
            Context = context;
        }

        public SqliteConnection Connection { get; }
        public ControlServerDbContext Context { get; }

        public static async Task<MigrationFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            return new MigrationFixture(connection, new ControlServerDbContext(options));
        }

        public Task MigrateToAsync(string migration) =>
            Context.GetService<IMigrator>().MigrateAsync(migration, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
