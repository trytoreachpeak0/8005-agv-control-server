using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// control-server#725 的迁移 <c>20261015144738_JourneyStopOperatorDepartedAt</c>：<c>JourneyStops</c> 末尾加一列可空的 <c>OperatorDepartedAt</c>，
/// 别的什么都不动。迁移通道由调度 Coordinator 11 于 2026-10-15 给出，约束是「只加这一列、不改既有数据、要有 Down，升级再回退之后 schema 一致」。
/// </summary>
/// <remarks>
/// <para>
/// 比的是什么，如实写（#732 独立审查 L1）：库从空库只升到上一个迁移 <c>20261013041134_ProtocolInboxTypeNewestFirstIndex</c>，记下那时
/// <c>JourneyStops</c> 的列定义、建表语句、索引与触发器，造一行；升到本迁移，断言只多了末尾一列可空的 <c>OperatorDepartedAt</c>、索引不变、那一行
/// 原值不变而新列为空；再 Down 回上一个迁移，断言列定义、建表语句、索引与那一行都与升级之前逐字相同；再升一次，列定义与第一次升级后相同。
/// 只看 <c>JourneyStops</c> 一张表——迁移只碰它；别的表不变由五份迁移纪律测试的名单与它们的整库比对兜着。
/// </para>
/// <para>
/// 走真迁移（<see cref="Batch7JourneyFixture.CreateAsync"/> 传 <c>migrate: false</c> 再按名迁移）：<c>RuntimeFixture</c> 用 <c>EnsureCreated</c> 建库，
/// 没有迁移历史，回退不了。那一行按表的列定义逐列造（NOT NULL 的列给一个按类型的占位值），外键检查在造行时关掉——这里断的是迁移对行做了什么，
/// 不是行在业务上成不成立。
/// </para>
/// </remarks>
public sealed class JourneyStopOperatorDepartedAtMigrationTests
{
    private const string Previous = "20261013041134_ProtocolInboxTypeNewestFirstIndex";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task UpAddsOnlyANullableLastColumnKeepsEveryRowAndDownRestoresTheTableExactly()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(Previous, Token);
        Assert.DoesNotContain(
            "20261015144738_JourneyStopOperatorDepartedAt", await fixture.Context.Database.GetAppliedMigrationsAsync(Token));
        string[] columnsBefore = await ColumnsAsync(fixture);
        string tableBefore = await TableSqlAsync(fixture);
        string[] indexesBefore = await IndexesAsync(fixture);
        Assert.DoesNotContain(columnsBefore, column => column.StartsWith("OperatorDepartedAt|", StringComparison.Ordinal));
        await SeedRowAsync(fixture, columnsBefore);
        string rowBefore = await RowAsync(fixture, columnsBefore);

        await fixture.Context.Database.MigrateAsync(Token);
        await fixture.RenewContextAsync();
        string[] columnsUp = await ColumnsAsync(fixture);
        Assert.Equal([.. columnsBefore, "OperatorDepartedAt|TEXT|0|"], columnsUp);
        Assert.Equal(indexesBefore, await IndexesAsync(fixture));
        Assert.Equal(rowBefore, await RowAsync(fixture, columnsBefore));
        Assert.Equal(["NULL"], await QueryAsync(fixture, """SELECT IFNULL("OperatorDepartedAt", 'NULL') FROM "JourneyStops" """));

        await fixture.Context.GetService<IMigrator>().MigrateAsync(Previous, Token);
        Assert.Equal(columnsBefore, await ColumnsAsync(fixture));
        Assert.Equal(tableBefore, await TableSqlAsync(fixture));
        Assert.Equal(indexesBefore, await IndexesAsync(fixture));
        Assert.Equal(rowBefore, await RowAsync(fixture, columnsBefore));

        await fixture.Context.Database.MigrateAsync(Token);
        Assert.Equal(columnsUp, await ColumnsAsync(fixture));
    }

    /// <summary>每一列：名字、类型、NOT NULL、缺省值，按列序。</summary>
    private static Task<string[]> ColumnsAsync(Batch7JourneyFixture fixture) =>
        QueryAsync(fixture, """
            SELECT "name" || '|' || "type" || '|' || "notnull" || '|' || IFNULL("dflt_value", '')
            FROM pragma_table_info('JourneyStops') ORDER BY "cid"
            """);

    private static async Task<string> TableSqlAsync(Batch7JourneyFixture fixture) =>
        (await QueryAsync(fixture, """SELECT "sql" FROM sqlite_master WHERE "type" = 'table' AND "name" = 'JourneyStops'""")).Single();

    private static Task<string[]> IndexesAsync(Batch7JourneyFixture fixture) =>
        QueryAsync(fixture, """
            SELECT "name" || '|' || IFNULL("sql", '') FROM sqlite_master
            WHERE "tbl_name" = 'JourneyStops' AND "type" IN ('index', 'trigger') ORDER BY "name"
            """);

    private static async Task SeedRowAsync(Batch7JourneyFixture fixture, string[] columns)
    {
        string[] names = [.. columns.Select(column => column.Split('|')[0])];
        string[] values = [.. columns.Select((column, index) =>
        {
            string type = column.Split('|')[1];
            return type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase) ? (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : $"'v{index}'";
        })];
        await ExecuteAsync(fixture, "PRAGMA foreign_keys = OFF");
        await ExecuteAsync(fixture,
            $"INSERT INTO \"JourneyStops\" ({string.Join(", ", names.Select(name => $"\"{name}\""))}) VALUES ({string.Join(", ", values)})");
        await ExecuteAsync(fixture, "PRAGMA foreign_keys = ON");
    }

    private static async Task<string> RowAsync(Batch7JourneyFixture fixture, string[] columns) =>
        (await QueryAsync(fixture,
            $"SELECT {string.Join(" || '|' || ", columns.Select(column => $"IFNULL(CAST(\"{column.Split('|')[0]}\" AS TEXT), 'NULL')"))} FROM \"JourneyStops\""))
        .Single();

    private static async Task ExecuteAsync(Batch7JourneyFixture fixture, string sql)
    {
        await using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<string[]> QueryAsync(Batch7JourneyFixture fixture, string sql)
    {
        await using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText = sql;
        List<string> values = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            values.Add(reader.GetString(0));
        }
        return [.. values];
    }
}
