using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// 批次 8 的建表迁移由建表票 control-server#386 独占；批次8-17～8-21 零迁移，批次8-16（control-server#387）删旧表是第二次，
/// 已定（规格 18.2）。
/// </summary>
/// <remarks>
/// <para>
/// 本迁移按<b>完整后缀</b>认出，不用 <c>Contains("Batch8")</c>：后面的迁移名也可能带这个字样。
/// 它之后允许有哪些迁移，按名字点在 <see cref="MigrationsAfterBatch8"/>：本票合入时为空，#387 与 v3 那边若有迁移各自加一行。
/// 批次 3、6、7 那三份纪律测试的名单也点了本迁移的名。
/// </para>
/// <para>
/// 引擎零行为变化在库这一层的样子：既有表的列、列序与每一行都不变，唯一动到的既有表 <c>VehiclePurposeClaims</c> 只多了一条
/// CHECK 约束。EF 在 SQLite 上加约束会整表重建并把列重排，所以本迁移手写了保留列序的重建；这里断言它真的保留了。
/// </para>
/// </remarks>
public sealed class Batch8MigrationDisciplineTests
{
    internal const string PreviousMigration = "20260928153736_MapNameBaselines";
    internal const string Batch8MigrationSuffix = "_Batch8VehiclePurposePersistence";

    /// <summary>批次 8 迁移之后允许存在的迁移，按名字点出来。</summary>
    private static readonly string[] MigrationsAfterBatch8 = [];

    private static readonly string[] NewTables =
    [
        "StationExclusivities", "StationExclusivityRecords", "VehiclePurposeClaimRecords", "WaitingPointVehicleScopes",
        "WaitingPointVersions", "WaitingPoints",
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Batch8AddsExactlyOneMigrationStraightAfterMapNameBaselinesAndOnlyNamedOnesFollowIt()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        string[] migrations = [.. fixture.Context.Database.GetMigrations()];

        string batch8 = Assert.Single(migrations, name => name.EndsWith(Batch8MigrationSuffix, StringComparison.Ordinal));
        int index = Array.IndexOf(migrations, batch8);
        Assert.Equal(PreviousMigration, migrations[index - 1]);
        Assert.Equal(MigrationsAfterBatch8, migrations[(index + 1)..]);
    }

    [Fact]
    public async Task TheModelSnapshotMatchesTheModel()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();

        Assert.False(fixture.Context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task MigratingAnEmptyDatabaseCreatesEveryBatch8TableWithItsKeysConstraintsAndIndexes()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();

        Assert.Equal(
            [
                "StationExclusivities pk=MapId,StationId",
                "StationExclusivityRecords pk=RecordId",
                "VehiclePurposeClaimRecords pk=RecordId",
                "WaitingPointVehicleScopes pk=Version,MapId,StationId,VehicleKey",
                "WaitingPointVersions pk=Version",
                "WaitingPoints pk=Version,MapId,StationId",
            ],
            await Task.WhenAll(NewTables.Select(async table =>
                $"{table} pk={string.Join(",", await PrimaryKeyAsync(fixture.Connection, table))}")));

        Assert.Equal(
            [
                "IX_StationExclusivities_JourneyId on JourneyId",
                "IX_StationExclusivities_RecordId unique on RecordId",
                "IX_StationExclusivities_VehicleKey on VehicleKey",
                "IX_StationExclusivityRecords_JourneyId on JourneyId",
                "IX_StationExclusivityRecords_MapId_StationId unique on MapId,StationId where ReleasedAt IS NULL",
                "IX_StationExclusivityRecords_VehicleKey on VehicleKey",
                "IX_VehiclePurposeClaimRecords_JourneyId on JourneyId",
                "IX_VehiclePurposeClaimRecords_VehicleKey unique on VehicleKey where ReleasedAt IS NULL",
                "IX_VehiclePurposeClaims_JourneyId on JourneyId",
            ],
            await IndexesAsync(fixture.Connection, [.. NewTables, "VehiclePurposeClaims"]));

        Assert.Equal(
            [
                """StationExclusivities: CONSTRAINT "CK_StationExclusivities_State" CHECK ("State" IN ('RESERVED', 'OCCUPIED'))""",
                """StationExclusivities: CONSTRAINT "CK_StationExclusivities_StationKind" CHECK ("StationKind" IN ('WAITING_POINT', 'FIXED_TASK_STATION'))""",
                """StationExclusivityRecords: CONSTRAINT "CK_StationExclusivityRecords_StationKind" CHECK ("StationKind" IN ('WAITING_POINT', 'FIXED_TASK_STATION'))""",
                """VehiclePurposeClaimRecords: CONSTRAINT "CK_VehiclePurposeClaimRecords_Purpose" CHECK ("Purpose" IN ('TRANSPORT', 'CHARGING', 'CLEARING_MAINTENANCE', 'IDLE_RETURN'))""",
                """VehiclePurposeClaims: CONSTRAINT "CK_VehiclePurposeClaims_Purpose" CHECK ("Purpose" IN ('TRANSPORT', 'CHARGING', 'CLEARING_MAINTENANCE', 'IDLE_RETURN'))""",
            ],
            await CheckConstraintsAsync(fixture.Connection));
    }

    [Fact]
    public async Task EveryClaimInFlightGetsItsAcquiredRecordAndEveryExistingRowAndColumnStaysExactlyAsItWas()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Token);
        await SeedThroughTheAcceptancePathAsync(fixture);
        Dictionary<string, string[]> columnsBefore = await ColumnsOfEveryTableAsync(fixture.Connection);
        Dictionary<string, string[]> rowsBefore = await DumpEveryTableAsync(fixture.Connection);
        Assert.Equal(3, rowsBefore["JourneyRuntimes"].Length);
        Assert.Equal(3, rowsBefore["OrderIntents"].Length);
        Assert.Single(rowsBefore["RiotDispatchAuditEvents"]);
        Assert.Single(rowsBefore["ExperimentalRiotCreateAuthorizations"]);
        Assert.Equal(2, rowsBefore["VehiclePurposeClaims"].Length);

        // Journeys in flight while the migration runs: it runs all the same.
        await migrator.MigrateAsync(null, Token);

        // Every table that existed keeps its columns, in the same order, and every row, value for value. The only
        // difference is the nullability of the columns choice A relaxes -- and none of them holds a null.
        Dictionary<string, string[]> columnsAfter = await ColumnsOfEveryTableAsync(fixture.Connection);
        Assert.Equal(NewTables, columnsAfter.Keys.Except(columnsBefore.Keys).Order(StringComparer.Ordinal));
        foreach ((string table, string[] columns) in columnsBefore)
        {
            Assert.Equal(columns.Select(column => Relaxed(table, column)), columnsAfter[table]);
            Assert.Equal(rowsBefore[table], await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
        }
        foreach ((string table, string[] relaxed) in RelaxedColumns)
        {
            foreach (string column in relaxed)
            {
                Assert.Equal(0L, await ScalarAsync(fixture.Connection, $"SELECT COUNT(*) FROM \"{table}\" WHERE \"{column}\" IS NULL"));
            }
        }

        // One "acquired" per claim held at the upgrade, the ended journey's none; the other new tables are empty.
        Assert.Equal(
            [
                "RecordId='backfill|journey:D-1'|VehicleKey='VK-01'|Purpose='TRANSPORT'|JourneyId='journey:D-1'|AcquiredAt='2026-09-19 09:00:00+00:00'|ReleasedAt=NULL|ReleaseReason=NULL",
                "RecordId='backfill|journey:D-2'|VehicleKey='VK-02'|Purpose='TRANSPORT'|JourneyId='journey:D-2'|AcquiredAt='2026-09-19 09:01:00+00:00'|ReleasedAt=NULL|ReleaseReason=NULL",
            ],
            await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehiclePurposeClaimRecords"));
        foreach (string table in NewTables.Where(table => table != "VehiclePurposeClaimRecords"))
        {
            Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
        }
    }

    [Fact]
    public async Task TheFiveRebuiltTablesKeepEveryIndexConstraintAndTriggerAndDifferOnlyInWhatTheMigrationMeantToChange()
    {
        // A rebuild drops the table with everything hanging off it and puts back only what it names. An object built by
        // hand-written SQL somewhere in the history and forgotten here would vanish silently; this is where it shows.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Token);
        await SeedThroughTheAcceptancePathAsync(fixture);
        Dictionary<string, string[]> before = await ObjectsOfAsync(fixture.Connection, RebuiltTables);

        await migrator.MigrateAsync(null, Token);
        Dictionary<string, string[]> after = await ObjectsOfAsync(fixture.Connection, RebuiltTables);

        foreach (string table in RebuiltTables)
        {
            // Everything but the table itself -- indexes, partial unique indexes, triggers -- word for word.
            Assert.Equal(
                before[table].Where(entry => !entry.StartsWith("table ", StringComparison.Ordinal)),
                after[table].Where(entry => !entry.StartsWith("table ", StringComparison.Ordinal)));
            // The table's own definition: the relaxed NOT NULLs, and the claims' CHECK, and nothing else.
            string expected = before[table].Single(entry => entry.StartsWith("table ", StringComparison.Ordinal));
            foreach (string column in RelaxedColumns.GetValueOrDefault(table, []))
            {
                expected = expected.Replace($"\"{column}\" TEXT NOT NULL", $"\"{column}\" TEXT NULL", StringComparison.Ordinal);
            }
            if (table == "VehiclePurposeClaims")
            {
                expected = expected.Replace(
                    "\"ClaimedAt\" TEXT NOT NULL" + Environment.NewLine + ")",
                    "\"ClaimedAt\" TEXT NOT NULL," + Environment.NewLine
                    + "    CONSTRAINT \"CK_VehiclePurposeClaims_Purpose\" CHECK (\"Purpose\" IN ('TRANSPORT', 'CHARGING', 'CLEARING_MAINTENANCE', 'IDLE_RETURN'))"
                    + Environment.NewLine + ")",
                    StringComparison.Ordinal);
            }
            Assert.NotEqual(before[table].Single(entry => entry.StartsWith("table ", StringComparison.Ordinal)), expected);
            Assert.Equal(expected, after[table].Single(entry => entry.StartsWith("table ", StringComparison.Ordinal)));
        }
        Assert.Equal(0L, await ScalarAsync(fixture.Connection, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
        Assert.Equal("ok", await ScalarTextAsync(fixture.Connection, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task DownRestoresTheSchemaAndRowsItFoundAndUpAgainRebuildsTheSameThing()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Token);
        await SeedThroughTheAcceptancePathAsync(fixture);
        string[] schemaBefore = await SchemaAsync(fixture.Connection);
        Dictionary<string, string[]> rowsBefore = await DumpEveryTableAsync(fixture.Connection);

        await migrator.MigrateAsync(null, Token);
        string[] schemaAfter = await SchemaAsync(fixture.Connection);
        Dictionary<string, string[]> rowsAfter = await DumpEveryTableAsync(fixture.Connection);
        Assert.NotEqual(schemaBefore, schemaAfter);

        await migrator.MigrateAsync(PreviousMigration, Token);
        Assert.Equal(schemaBefore, await SchemaAsync(fixture.Connection));
        Assert.Equal(rowsBefore, await DumpEveryTableAsync(fixture.Connection));

        await migrator.MigrateAsync(null, Token);
        Assert.Equal(schemaAfter, await SchemaAsync(fixture.Connection));
        Assert.Equal(rowsAfter, await DumpEveryTableAsync(fixture.Connection));
    }

    [Fact]
    public async Task AClaimWithAPurposeOutsideTheFourMakesTheMigrationFailWholeRatherThanDropIt()
    {
        // Nothing writes such a row today. If one were there, the rebuild's copy refuses it and the migration goes back
        // whole: the claim table keeps the row, and no new table is left behind.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Token);
        await using (SqliteCommand insert = fixture.Connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO VehiclePurposeClaims (VehicleKey, Purpose, JourneyId, ClaimedAt) VALUES ('VK-09', 'PARKING', 'j', 'x')";
            await insert.ExecuteNonQueryAsync(Token);
        }
        string[] schemaBefore = await SchemaAsync(fixture.Connection);

        await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync(null, Token));

        Assert.Equal(schemaBefore, await SchemaAsync(fixture.Connection));
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehiclePurposeClaims"));
        Assert.Equal(PreviousMigration, (await fixture.Context.Database.GetAppliedMigrationsAsync(Token)).Last());
    }

    /// <summary>Two journeys in flight on two vehicles of one fleet, and one that has ended, all through the real acceptance.</summary>
    private static async Task SeedThroughTheAcceptancePathAsync(Batch7JourneyFixture fixture)
    {
        DateTimeOffset now = Batch7JourneyFixture.Now;
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-1", "AGV-01", "VK-01", now);
        await fixture.RenewContextAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-2", "AGV-02", "VK-02", now.AddMinutes(1));
        await fixture.RenewContextAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-3", "AGV-03", "VK-03", now.AddMinutes(2));
        await fixture.RenewContextAsync();
        await Batch7JourneyFixture.CompleteByUnloadAsync(fixture.Context, "D-3", now.AddMinutes(10));
        await fixture.RenewContextAsync();

        // The two RIoT tables whose DemandId is relaxed, one row each, so their values are compared too. Written directly:
        // the rows are only there to be carried through the rebuild.
        await using SqliteCommand insert = fixture.Connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO RiotDispatchAuditEvents (AuditEventId, MovementLegId, DemandId, UpperId, DispatchGeneration, Sequence, AttemptId, AttemptNumber, Phase, Outcome, OccurredAt, RequestSemanticSha256, HttpStatusCode, ResultPresent, EligibilityBasis)
            VALUES ('AE-1', 'LEG-1', 'D-1', 'W2G-D-1-PICKUP-1', 1, 1, 'ATTEMPT-1', 1, 'CREATE', 'ACCEPTED', '2026-09-19 09:00:05+00:00', 'abc', 200, 1, 'FRESH');
            INSERT INTO ExperimentalRiotCreateAuthorizations (AuthorizationId, AuthorizationVersion, UpperId, DemandId, MovementLegId, AgvLifecycleGeneration, DispatchGeneration, ExpiresAt, PersistedAt)
            VALUES ('AUTH-1', 1, 'W2G-D-1-PICKUP-1', 'D-1', 'LEG-1', 1, 1, '2026-09-19 10:00:00+00:00', '2026-09-19 09:00:01+00:00');
            """;
        await insert.ExecuteNonQueryAsync(Token);
    }

    /// <summary>The five tables this migration rebuilds.</summary>
    private static readonly string[] RebuiltTables =
        ["ExperimentalRiotCreateAuthorizations", "JourneyRuntimes", "OrderIntents", "RiotDispatchAuditEvents", "VehiclePurposeClaims"];

    /// <summary>Choice A: the columns made nullable, table by table. Nothing else changes nullability.</summary>
    private static readonly Dictionary<string, string[]> RelaxedColumns = new(StringComparer.Ordinal)
    {
        ["JourneyRuntimes"] =
        [
            "DemandId", "GateMovementLegId", "GatePlanMessageId", "GateStationId", "GateUpperId", "GateVehicleBusinessMessageId",
            "GateWorklistMessageId", "LoadCommandMessageId", "LoadSlotOperationAttemptId", "PreDepartureSafetyCheckId",
            "PreDepartureSafetyCheckMessageId", "SublotRequestMessageId", "TargetSlotsJson", "UnloadCommandMessageId",
            "UnloadSlotOperationAttemptId",
        ],
        ["OrderIntents"] = ["DemandId"],
        ["RiotDispatchAuditEvents"] = ["DemandId"],
        ["ExperimentalRiotCreateAuthorizations"] = ["DemandId"],
    };

    private static string Relaxed(string table, string column)
    {
        string name = column.Split(' ')[0];
        return RelaxedColumns.TryGetValue(table, out string[]? relaxed) && relaxed.Contains(name)
            ? column.Replace(" notnull=1 ", " notnull=0 ", StringComparison.Ordinal)
            : column;
    }

    /// <summary>Every schema object of each table -- the table, its indexes and triggers -- with its stored definition.</summary>
    private static async Task<Dictionary<string, string[]>> ObjectsOfAsync(SqliteConnection connection, IEnumerable<string> tables)
    {
        Dictionary<string, string[]> objects = new(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT type || ' ' || name || ': ' || coalesce(sql, '(implicit)') FROM sqlite_master WHERE tbl_name = @table";
            command.Parameters.AddWithValue("@table", table);
            List<string> own = [];
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                own.Add(reader.GetString(0));
            }
            own.Sort(StringComparer.Ordinal);
            objects[table] = [.. own];
        }
        return objects;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task<string> ScalarTextAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task<List<string>> TablesAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' ORDER BY name";
        List<string> tables = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
    }

    private static async Task<Dictionary<string, string[]>> DumpEveryTableAsync(SqliteConnection connection)
    {
        Dictionary<string, string[]> rows = new(StringComparer.Ordinal);
        foreach (string table in await TablesAsync(connection))
        {
            rows[table] = await Batch7JourneyFixture.DumpAsync(connection, table);
        }
        return rows;
    }

    /// <summary>Each table's columns in their stored order, with type, nullability, default and key position.</summary>
    private static async Task<Dictionary<string, string[]>> ColumnsOfEveryTableAsync(SqliteConnection connection)
    {
        Dictionary<string, string[]> columns = new(StringComparer.Ordinal);
        foreach (string table in await TablesAsync(connection))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT name, type, \"notnull\", coalesce(dflt_value, '-'), pk FROM pragma_table_info('{table}') ORDER BY cid";
            List<string> own = [];
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                own.Add($"{reader.GetString(0)} {reader.GetString(1)} notnull={reader.GetInt64(2)} default={reader.GetString(3)} pk={reader.GetInt64(4)}");
            }
            columns[table] = [.. own];
        }
        return columns;
    }

    /// <summary>Every table, index and trigger with its full stored definition, ordered.</summary>
    private static async Task<string[]> SchemaAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT type || ' ' || name || ': ' || coalesce(sql, '') FROM sqlite_master WHERE name <> '__EFMigrationsHistory' AND name NOT LIKE 'sqlite_autoindex___EFMigrationsHistory%'";
        List<string> schema = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            schema.Add(reader.GetString(0));
        }
        schema.Sort(StringComparer.Ordinal);
        return [.. schema];
    }

    private static async Task<string[]> PrimaryKeyAsync(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}') WHERE pk > 0 ORDER BY pk";
        List<string> key = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            key.Add(reader.GetString(0));
        }
        return [.. key];
    }

    private static async Task<string[]> IndexesAsync(SqliteConnection connection, IEnumerable<string> tables)
    {
        List<string> indexes = [];
        foreach (string table in tables)
        {
            await using SqliteCommand list = connection.CreateCommand();
            list.CommandText =
                $"SELECT il.name, il.\"unique\", m.sql FROM pragma_index_list('{table}') il JOIN sqlite_master m ON m.name = il.name WHERE il.origin = 'c'";
            await using SqliteDataReader reader = await list.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                string name = reader.GetString(0);
                string sql = reader.GetString(2);
                int where = sql.IndexOf(" WHERE ", StringComparison.Ordinal);
                List<string> columns = [];
                await using (SqliteCommand info = connection.CreateCommand())
                {
                    info.CommandText = $"SELECT name FROM pragma_index_info('{name}') ORDER BY seqno";
                    await using SqliteDataReader columnReader = await info.ExecuteReaderAsync(Token);
                    while (await columnReader.ReadAsync(Token))
                    {
                        columns.Add(columnReader.GetString(0));
                    }
                }
                indexes.Add($"{name}{(reader.GetInt64(1) == 1 ? " unique" : string.Empty)} on {string.Join(",", columns)}"
                    + (where >= 0 ? " where " + sql[(where + 7)..] : string.Empty));
            }
        }
        indexes.Sort(StringComparer.Ordinal);
        return [.. indexes];
    }

    /// <summary>Every CHECK constraint in the schema, as "table: its clause".</summary>
    private static async Task<string[]> CheckConstraintsAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table' AND sql LIKE '%CHECK (%'";
        List<string> checks = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            foreach (string line in reader.GetString(1).ReplaceLineEndings("\n").Split('\n'))
            {
                string clause = line.Trim().TrimEnd(',');
                if (clause.StartsWith("CONSTRAINT \"CK_", StringComparison.Ordinal))
                {
                    checks.Add($"{reader.GetString(0)}: {clause}");
                }
            }
        }
        checks.Sort(StringComparer.Ordinal);
        return [.. checks];
    }
}
