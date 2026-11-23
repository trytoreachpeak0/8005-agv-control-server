using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// 迁移 <c>TransportDemandLeg</c>（control-server#911）：三张身份表加「程」，回填、改唯一性、降级。
/// </summary>
/// <remarks>
/// <para>
/// <b>回填的判据是「该填的填了、不该动的一个没动」</b>：受理行只有 <c>STAGING_TO_WIRE</c> 取 <c>LiveMesFieldsJson.Step</c>（空白、缺字段、
/// 整个为 <c>null</c> 都是空串），别的任务类型哪怕有 <c>STEP</c> 也是空串；抑制与完成行按自己的 <c>DemandId</c> 取那条受理行的程，找不到受理行的
/// 留空串。三张表去掉新列之后，每一行逐字与迁移前相同。只断言目标行变了，一条把全表都填成 <c>STEP</c> 的回填也能通过。
/// </para>
/// <para>
/// <b>降级</b>：升上去以后再写下同一子批的第二程（新唯一性允许），然后降回上一个迁移。旧唯一性按业务键，所以同键的后一程被改名成
/// <c>{key}#{leg}</c>、先写下的那一程留原键；三张表回到旧的列与索引；再升一次照样升得上去。
/// </para>
/// <para>
/// 种子在上一个迁移的库上用 SQL 直接写：今天的模型认得 <c>TransportLeg</c>，拿它写旧表会去写一列旧表没有的列。
/// </para>
/// </remarks>
public sealed class TransportDemandLegMigrationTests
{
    private const string PreviousMigration = "20261117085542_VehicleFaultOriginSymptomCode";
    private const string ThisMigration = "20261123074049_TransportDemandLeg";

    private static readonly string[] IdentityTables = ["AcceptedDemands", "TransportDemandSuppressions", "TransportDemandCompletions"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheMigrationFollowsThePreviousOneAndTheModelHasNoPendingChanges()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        string[] migrations = [.. fixture.Context.Database.GetMigrations()];
        int own = Array.IndexOf(migrations, ThisMigration);
        Assert.True(own > 0);
        Assert.Equal(PreviousMigration, migrations[own - 1]);
        Assert.False(fixture.Context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task UpgradingFillsTheLegOnlyWhereItSplitsAndLeavesEveryOtherValueAsItWas()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Token);
        await SeedAsOldVersionLeftItAsync(fixture.Connection);
        Dictionary<string, string[]> before = await DumpAsync(fixture.Connection);

        await migrator.MigrateAsync(ThisMigration, Token);

        Assert.Equal(
            ["D-BLANK=", "D-GATE=", "D-NOFIELDS=", "D-WIRE2=焊线2", "D-WIRE=焊线"],
            await LegsAsync(fixture.Connection, "AcceptedDemands"));
        Assert.Equal(["D-ORPHAN=", "D-WIRE2=焊线2"], await LegsAsync(fixture.Connection, "TransportDemandSuppressions"));
        Assert.Equal(["D-GATE=", "D-WIRE=焊线"], await LegsAsync(fixture.Connection, "TransportDemandCompletions"));
        Dictionary<string, string[]> after = await DumpAsync(fixture.Connection);
        foreach (string table in IdentityTables)
        {
            Assert.Equal(before[table], after[table]);
        }
    }

    [Fact]
    public async Task AfterTheUpgradeASecondLegOfTheSameSublotIsWrittenAndRollingBackRenamesItOutOfTheOldKey()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Token);
        string[] oldSchema = await SchemaAsync(fixture.Connection);
        await SeedAsOldVersionLeftItAsync(fixture.Connection);
        await migrator.MigrateAsync(ThisMigration, Token);

        // 新唯一性允许：同一子批 S-WIRE 的焊线2 那程受理、送达；S-WIRE2 的焊线那程另被取消抑制。
        await ExecuteAsync(
            fixture.Connection,
            AcceptedSql("D-WIRE-B", "S-WIRE|STAGING_TO_WIRE", "STAGING_TO_WIRE", "S-WIRE", """{"Step":"焊线2"}""", "Succeeded", "焊线2") +
            CompletionSql("D-WIRE-B", "S-WIRE|STAGING_TO_WIRE", "焊线2") +
            AcceptedSql("D-WIRE2-A", "S-WIRE2|STAGING_TO_WIRE", "STAGING_TO_WIRE", "S-WIRE2", """{"Step":"焊线"}""", "Cancelled", "焊线") +
            SuppressionSql("D-WIRE2-A", "S-WIRE2|STAGING_TO_WIRE", "焊线"));

        await migrator.MigrateAsync(PreviousMigration, Token);

        Assert.Equal(oldSchema, await SchemaAsync(fixture.Connection));
        Assert.Equal(
            ["D-WIRE-B=S-WIRE|STAGING_TO_WIRE#焊线2", "D-WIRE2-A=S-WIRE2|STAGING_TO_WIRE#焊线", "D-WIRE2=S-WIRE2|STAGING_TO_WIRE",
             "D-WIRE=S-WIRE|STAGING_TO_WIRE"],
            await KeysAsync(fixture.Connection, "AcceptedDemands", "S-WIRE"));
        Assert.Equal(
            ["D-WIRE-B=S-WIRE|STAGING_TO_WIRE#焊线2", "D-WIRE=S-WIRE|STAGING_TO_WIRE"],
            await KeysAsync(fixture.Connection, "TransportDemandCompletions", "S-WIRE"));
        Assert.Equal(
            ["D-WIRE2-A=S-WIRE2|STAGING_TO_WIRE#焊线", "D-WIRE2=S-WIRE2|STAGING_TO_WIRE"],
            await KeysAsync(fixture.Connection, "TransportDemandSuppressions", "S-WIRE"));

        await migrator.MigrateAsync(ThisMigration, Token);
        Assert.Contains("D-WIRE-B=焊线2", await LegsAsync(fixture.Connection, "AcceptedDemands"));
    }

    // ---- Seed -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// 旧版本留下的样子：一个子批一行受理。焊线送达（有完成）、焊线2 取消（有抑制）、关卡送达（有完成，<c>STEP</c> 非空但不分程）、
    /// 没有现场字段与 <c>STEP</c> 为空白的两条送焊线，以及一条找不到受理行的抑制。
    /// </summary>
    private static Task SeedAsOldVersionLeftItAsync(SqliteConnection connection) => ExecuteAsync(
        connection,
        AcceptedSql("D-WIRE", "S-WIRE|STAGING_TO_WIRE", "STAGING_TO_WIRE", "S-WIRE", """{"Area":"N17-3","Step":"焊线"}""", "Succeeded") +
        CompletionSql("D-WIRE", "S-WIRE|STAGING_TO_WIRE") +
        AcceptedSql("D-WIRE2", "S-WIRE2|STAGING_TO_WIRE", "STAGING_TO_WIRE", "S-WIRE2", """{"Area":"N3-16","Step":"焊线2"}""", "Cancelled") +
        SuppressionSql("D-WIRE2", "S-WIRE2|STAGING_TO_WIRE") +
        AcceptedSql("D-GATE", "S-GATE|WIRE_TO_GATE", "WIRE_TO_GATE", "S-GATE", """{"Step":"焊线关卡"}""", "Succeeded") +
        CompletionSql("D-GATE", "S-GATE|WIRE_TO_GATE") +
        AcceptedSql("D-NOFIELDS", "S-NOFIELDS|STAGING_TO_WIRE", "STAGING_TO_WIRE", "S-NOFIELDS", "null", "Accepted") +
        AcceptedSql("D-BLANK", "S-BLANK|STAGING_TO_WIRE", "STAGING_TO_WIRE", "S-BLANK", """{"Step":"  "}""", "Accepted") +
        SuppressionSql("D-ORPHAN", "S-ORPHAN|STAGING_TO_WIRE"));

    private static string AcceptedSql(
        string demandId, string key, string workType, string sublot, string fields, string status, string? leg = null) =>
        $"""
        INSERT INTO "AcceptedDemands" ("DemandId", "TransportDemandKey", "DemandRevision", "HistoryEpoch", "CatalogRevision",
            "AcceptedAt", "Status", "CreatedAt", "Generation", "LiveMesFieldsJson", "SeriesId", "Sublot", "ValueObservedAt",
            "ValuePollTraceId", "ValueProjectionCommitId", "WorkType"{(leg is null ? "" : ", \"TransportLeg\"")})
        VALUES ('{demandId}', '{key}', 7, '11111111-1111-4111-8111-111111111111', 21, '2026-11-22 15:28:00+00:00', '{status}',
            '2026-11-22 15:00:00+00:00', 1, '{fields}', 'SERIES-{sublot}', '{sublot}', '2026-11-22 15:01:00+00:00',
            'TRACE-{demandId}', 'COMMIT-{demandId}', '{workType}'{(leg is null ? "" : $", '{leg}'")});

        """;

    private static string CompletionSql(string demandId, string key, string? leg = null) =>
        $"""
        INSERT INTO "TransportDemandCompletions" ("TransportDemandKey", "DemandId", "DemandRevision", "Evidence", "CompletedAt"{(leg is null ? "" : ", \"TransportLeg\"")})
        VALUES ('{key}', '{demandId}', 7, 'evidence-{demandId}', '2026-11-22 16:00:00+00:00'{(leg is null ? "" : $", '{leg}'")});

        """;

    private static string SuppressionSql(string demandId, string key, string? leg = null) =>
        $"""
        INSERT INTO "TransportDemandSuppressions" ("TransportDemandKey", "DemandId", "ReasonCode", "SuppressedAt"{(leg is null ? "" : ", \"TransportLeg\"")})
        VALUES ('{key}', '{demandId}', 'CANCELLED_BY_OPERATOR', '2026-11-22 15:30:00+00:00'{(leg is null ? "" : $", '{leg}'")});

        """;

    // ---- Reads ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// 三张表每一行除 <c>TransportLeg</c> 外的全部列，列按名字排：两张表在迁移里整表重建，物理列序会变，没人按位置读它们。
    /// </summary>
    private static async Task<Dictionary<string, string[]>> DumpAsync(SqliteConnection connection)
    {
        Dictionary<string, string[]> tables = new(StringComparer.Ordinal);
        foreach (string table in IdentityTables)
        {
            string[] columns = [.. (await ReadAsync(
                    connection, $"SELECT name FROM pragma_table_info('{table}') WHERE name <> 'TransportLeg'"))
                .Order(StringComparer.Ordinal)];
            tables[table] = await ReadAsync(
                connection,
                $"SELECT {string.Join(" || '|' || ", columns.Select(name => $"'{name}=' || quote(\"{name}\")"))} FROM \"{table}\"");
        }
        return tables;
    }

    private static Task<string[]> LegsAsync(SqliteConnection connection, string table) =>
        ReadAsync(connection, $"""SELECT "DemandId" || '=' || "TransportLeg" FROM "{table}" """);

    private static Task<string[]> KeysAsync(SqliteConnection connection, string table, string sublotPrefix) =>
        ReadAsync(
            connection,
            $"""SELECT "DemandId" || '=' || "TransportDemandKey" FROM "{table}" WHERE "TransportDemandKey" LIKE '{sublotPrefix}%' """);

    /// <summary>The three identity tables' columns (name, type, nullability, default, key position) and their indexes.</summary>
    private static async Task<string[]> SchemaAsync(SqliteConnection connection)
    {
        List<string> schema = [];
        foreach (string table in IdentityTables)
        {
            schema.AddRange((await ReadAsync(
                    connection,
                    $"SELECT '{table}.' || name || ' ' || type || ' notnull=' || \"notnull\" || ' default=' || coalesce(dflt_value, '-') || ' pk=' || pk FROM pragma_table_info('{table}')"))
                .Select(column => "column " + column));
            schema.AddRange(await ReadAsync(
                connection,
                $"SELECT 'index ' || name || ': ' || coalesce(sql, '(implicit)') FROM sqlite_master WHERE type = 'index' AND tbl_name = '{table}' AND name NOT LIKE 'sqlite_%'"));
        }
        schema.Sort(StringComparer.Ordinal);
        return [.. schema];
    }

    private static async Task<string[]> ReadAsync(SqliteConnection connection, string sql)
    {
        List<string> values = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            values.Add(reader.GetString(0));
        }
        values.Sort(StringComparer.Ordinal);
        return [.. values];
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
    }
}
