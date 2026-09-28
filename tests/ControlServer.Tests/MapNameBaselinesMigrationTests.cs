using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// control-server#186：地图名基线表 <c>MapNameBaselines</c> 由一个只建一张表的迁移落地。钉三件事：它紧跟上一个迁移；
/// 只多出这一张表、列是约定的那几列，别的表的结构一字不变；降回去表就没了、别的表照旧，再升上来又回来。
/// </summary>
public sealed class MapNameBaselinesMigrationTests
{
    private const string Migration = "20260928153736_MapNameBaselines";

    private const string PreviousMigration = "20260928060831_OwnOrderRebuildCargoEvidenceNotBefore";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheTableComesInOneMigrationStraightAfterTheCargoEvidenceNotBeforeOne()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        string[] migrations = [.. fixture.Context.Database.GetMigrations()];

        Assert.Single(migrations, name => name.EndsWith("_MapNameBaselines", StringComparison.Ordinal));
        Assert.Equal(PreviousMigration, migrations[Array.IndexOf(migrations, Migration) - 1]);
    }

    [Fact]
    public async Task UpAddsOnlyTheBaselineTableWithItsColumnsAndDownTakesOnlyItAway()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Token);
        string[] before = await SchemaAsync(fixture.Connection);

        await migrator.MigrateAsync(Migration, Token);
        string[] after = await SchemaAsync(fixture.Connection);

        Assert.Equal(["MapNameBaselines"], after.Except(before).Select(entry => entry.Split('|')[0]));
        Assert.Empty(before.Except(after));
        Assert.Equal(
            ["AcceptedAt TEXT null", "AcceptedBy TEXT null", "EstablishedAt TEXT notnull", "MapId INTEGER notnull pk",
                "Name TEXT notnull", "PendingName TEXT null", "PendingSince TEXT null"],
            await ColumnsAsync(fixture.Connection, "MapNameBaselines"));

        await migrator.MigrateAsync(PreviousMigration, Token);
        Assert.Equal(before, await SchemaAsync(fixture.Connection));
        await migrator.MigrateAsync(Migration, Token);
        Assert.Equal(after, await SchemaAsync(fixture.Connection));
    }

    /// <summary>Every table with its full definition, as SQLite stores it, one entry each, ordered.</summary>
    private static async Task<string[]> SchemaAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'";
        List<string> tables = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            tables.Add(reader.GetString(0) + "|" + reader.GetString(1));
        }
        return [.. tables.Order(StringComparer.Ordinal)];
    }

    private static async Task<string[]> ColumnsAsync(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT name, type, \"notnull\", pk FROM pragma_table_info('{table}')";
        List<string> columns = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            columns.Add(reader.GetString(0) + " " + reader.GetString(1) + (reader.GetInt64(2) == 1 ? " notnull" : " null")
                + (reader.GetInt64(3) > 0 ? " pk" : string.Empty));
        }
        return [.. columns.Order(StringComparer.Ordinal)];
    }
}
