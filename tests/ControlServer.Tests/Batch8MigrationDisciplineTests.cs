using System.Reflection;
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
/// 引擎零行为变化在库这一层的样子：既有表的列、列序与每一行都不变；重建的六张表只差放宽的 NOT NULL（选甲）与
/// <c>VehiclePurposeClaims</c> 的用途 CHECK。EF 在 SQLite 上改这两样会整表重建并把列重排，所以本迁移手写了保留列序的重建，
/// 列清单也是手写的：漏拷一列，那一列在迁移后就成了空值。这里断言它真的一列不漏，所以种子把六张表的每个可空列都填成逐行不同的
/// 非空值——只填受理路径会写的列时，漏拷一个今天恰好为空的列（在途单的 <c>OrderId</c>）看不出来（control-server#394 审查必修 2）。
/// </para>
/// <para>
/// 这是本迁移唯一的守卫：<c>ZeroChangePins/</c> 走 <c>EnsureCreated</c>，不经过迁移。
/// </para>
/// </remarks>
public sealed class Batch8MigrationDisciplineTests
{
    internal const string PreviousMigration = "20260928153736_MapNameBaselines";
    internal const string Batch8MigrationSuffix = "_Batch8VehiclePurposePersistence";

    /// <summary>
    /// This migration by its full name. The assertions below migrate to it rather than to the latest: the one after it
    /// (batch 8-16, control-server#387) drops the order occupancy columns these compare row for row.
    /// </summary>
    internal const string Batch8Migration = "20260929044052" + Batch8MigrationSuffix;

    /// <summary>批次 8 迁移之后允许存在的迁移，按名字点出来。</summary>
    private static readonly string[] MigrationsAfterBatch8 =
    [
        // control-server#387：批次 8 第二次迁移——删前核数据（未结束的旧占用没有对应用途占有即整体拒绝、列出行、什么都不删），从占有行与已释放租约回填占有记录，删租约表与 OrderIntents 的两列订单占用及其过滤唯一索引（原生 DROP COLUMN，其余列序不变）。自己的断言在 Batch8OccupancyRetirementMigrationTests。
        "20260929070322_Batch8RetireOldVehicleOccupancy",
        // control-server#399：批次 9 唯一一次迁移——新建充电桩名册、充电策略版本（含批准与激活）、充电周期、桩与车两类暂停及其恢复、清桩记录、人工充电等待及其经过、两类现场确认请求共 17 张表（建空）；StationExclusivities／StationExclusivityRecords 加 CHARGER 种类与末列可空 ChargerRosterVersion（保留列序的手写重建）；OrderIntents 末列加 OrderShape（缺省即回填 SINGLE_MOVE）、JourneyRuntimes 末列加两列可空列（原生 ADD COLUMN）；既有列序与行不变。自己的断言在 Batch9MigrationDisciplineTests。
        "20260929114754_Batch9ChargingPersistence",
        // control-server#383：批次 8 人工判故障（REQ-0359）——新建 SlotFaultDeclarations 一张表（建空），带「同一尝试至多一条未结判定」的过滤唯一索引；既有表与行不变。在 batch-p3/v3 上建，合回集成分支前按先合入的迁移重建。自己的断言在 SlotFaultDeclarationTests。
        "20260930012829_Batch8SlotFaultDeclarations",
        // control-server#385：批次 8 恢复面（REQ-0242 CP-0008、REQ-0364 CP-0009）——ExceptionRecoverySessions 加可空列 ClosedReason，RecoveryWorkflows 加可空列 HandoffSublot、HandoffReceiverName、HandedOverAt，新建 SlotDoorHolds 一张表（建空，AgvId 普通索引）；纯 ADD COLUMN 与 CREATE TABLE，既有表与行不变。在 batch-p3/v3 上建，合回集成分支前按先合入的迁移重建。自己的断言在 RecoveryStateMachineG2Tests（RecoverySurface 分部）。
        "20260930041750_Batch8RecoverySurface",
        // control-server#505：**只改数据，不动 schema**——升级那一刻停在 Blocked、阻塞码以 _NOT_RECONCILED 结尾的旅程，码加后缀 _BEFORE_UPGRADE，成为不可放行的那一族（那时的码不保证有 RecoveryRequired 的需求作标记，而升级前第 5 条本来就一律不放，现场行为不变）；Down 去掉两个新后缀。调度 Coordinator 9 于 2026-10-08 给了迁移通道。自己的断言在 RecoveryEndingReleasesBlockedJourneyTests.Migration.cs。
        "20261008052643_UnreleasableNotReconciledBlocksBeforeUpgrade",
    ];

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
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateMigratedForRealAsync();

        Assert.False(fixture.Context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task MigratingAnEmptyDatabaseCreatesEveryBatch8TableWithItsKeysConstraintsAndIndexes()
    {
        // Up to this migration, not the latest: batch 9 widens the station kind CHECK (control-server#399), and asserts
        // its own shape in Batch9MigrationDisciplineTests.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        await fixture.Context.GetService<IMigrator>().MigrateAsync(Batch8Migration, Token);

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
                // The two record tables are evidence, not arbiters: no unique index (control-server#394 review, required 1).
                "IX_StationExclusivityRecords_MapId_StationId on MapId,StationId",
                "IX_StationExclusivityRecords_VehicleKey on VehicleKey",
                "IX_VehiclePurposeClaimRecords_JourneyId on JourneyId",
                "IX_VehiclePurposeClaimRecords_VehicleKey on VehicleKey",
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
    public async Task WithJourneysInFlightEveryExistingRowAndColumnStaysExactlyAsItWasAndTheNewTablesStartEmpty()
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
        Assert.Single(rowsBefore["OwnOrderRebuilds"]);
        Assert.Equal(2, rowsBefore["VehiclePurposeClaims"].Length);

        // Journeys in flight while the migration runs: it runs all the same.
        await migrator.MigrateAsync(Batch8Migration, Token);

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

        // Every new table starts empty -- the claim records included: their back-fill belongs to control-server#387, which
        // switches the engine onto the ledger in the same migration (control-server#394 review, required 1).
        foreach (string table in NewTables)
        {
            Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
        }
    }

    [Fact]
    public async Task TheSixRebuiltTablesKeepEveryIndexConstraintAndTriggerAndDifferOnlyInWhatTheMigrationMeantToChange()
    {
        // A rebuild drops the table with everything hanging off it and puts back only what it names. An object built by
        // hand-written SQL somewhere in the history and forgotten here would vanish silently; this is where it shows.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Token);
        await SeedThroughTheAcceptancePathAsync(fixture);
        Dictionary<string, string[]> before = await ObjectsOfAsync(fixture.Connection, RebuiltTables);

        await migrator.MigrateAsync(Batch8Migration, Token);
        Dictionary<string, string[]> after = await ObjectsOfAsync(fixture.Connection, RebuiltTables);

        foreach (string table in RebuiltTables)
        {
            // Everything but the table itself -- indexes, partial unique indexes, triggers -- word for word. No trigger hangs
            // off any of the six today (control-server#199's audit triggers are on the two audit tables), so for triggers this
            // compares two empty sets; it is kept so that one added later is carried or the comparison goes red.
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
    public async Task EveryHandWrittenCopyListNamesEveryColumnTheTableHadInItsOrder()
    {
        // A column with a default -- JourneyRuntimes.Version is NOT NULL DEFAULT 0 -- left out of a copy list is not
        // caught by comparing values: it comes back as its default, and a seed that holds the default everywhere reads the
        // same (incremental review of control-server#394, A). So the lists themselves are compared with the table.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        await fixture.Context.GetService<IMigrator>().MigrateAsync(PreviousMigration, Token);
        Type migration = typeof(ControlServerDbContext).Assembly.GetTypes()
            .Single(type => type.Name == Batch8MigrationSuffix.TrimStart('_') && typeof(Migration).IsAssignableFrom(type));
        (string Table, string[] Columns)[] lists =
        [
            .. migration.GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(field => field.FieldType.Name == "Table")
                .Select(field => field.GetValue(null)!)
                .Select(table => (
                    (string)table.GetType().GetProperty("Name")!.GetValue(table)!,
                    (string[])table.GetType().GetProperty("Columns")!.GetValue(table)!))
        ];

        // Two definitions per rebuilt table -- as it was, and as this migration leaves it -- and no table left out.
        Assert.Equal(RebuiltTables.Length * 2, lists.Length);
        Assert.Equal(RebuiltTables, lists.Select(list => list.Table).Distinct().Order(StringComparer.Ordinal));
        foreach ((string table, string[] columns) in lists)
        {
            Assert.Equal(await ColumnNamesAsync(fixture.Connection, table), columns);
        }
    }

    private static async Task<string[]> ColumnNamesAsync(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid";
        List<string> names = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }
        return [.. names];
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

        await migrator.MigrateAsync(Batch8Migration, Token);
        string[] schemaAfter = await SchemaAsync(fixture.Connection);
        Dictionary<string, string[]> rowsAfter = await DumpEveryTableAsync(fixture.Connection);
        Assert.NotEqual(schemaBefore, schemaAfter);

        await migrator.MigrateAsync(PreviousMigration, Token);
        Assert.Equal(schemaBefore, await SchemaAsync(fixture.Connection));
        Assert.Equal(rowsBefore, await DumpEveryTableAsync(fixture.Connection));

        await migrator.MigrateAsync(Batch8Migration, Token);
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

        SqliteException failure = await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync(Batch8Migration, Token));
        Assert.Equal(275, failure.SqliteExtendedErrorCode); // SQLITE_CONSTRAINT_CHECK
        Assert.Contains("CK_VehiclePurposeClaims_Purpose", failure.Message, StringComparison.Ordinal);

        Assert.Equal(schemaBefore, await SchemaAsync(fixture.Connection));
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "VehiclePurposeClaims"));
        Assert.Equal(PreviousMigration, (await fixture.Context.Database.GetAppliedMigrationsAsync(Token)).Last());
    }

    /// <summary>Two journeys in flight on two vehicles of one fleet, and one that has ended, all through the real acceptance.</summary>
    private static async Task SeedThroughTheAcceptancePathAsync(Batch7JourneyFixture fixture)
    {
        DateTimeOffset now = Batch7JourneyFixture.Now;
        // The acceptance writes a claim record since batch 8-16 (control-server#387); this schema has leases instead.
        await using (await Batch7JourneyFixture.WriteLeasesTheWayTheOldVersionDidAsync(fixture.Connection))
        {
            await AcceptAndCompleteAsync(fixture, now);
        }

        // The RIoT tables and the own-order rebuild record whose DemandId is relaxed, one row each, so their values are
        // compared too. Written directly: the rows are only there to be carried through the rebuild.
        await using (SqliteCommand insert = fixture.Connection.CreateCommand())
        {
            insert.CommandText =
                """
            INSERT INTO RiotDispatchAuditEvents (AuditEventId, MovementLegId, DemandId, UpperId, DispatchGeneration, Sequence, AttemptId, AttemptNumber, Phase, Outcome, OccurredAt, RequestSemanticSha256, HttpStatusCode, ResultPresent, EligibilityBasis)
            VALUES ('AE-1', 'LEG-1', 'D-1', 'W2G-D-1-PICKUP-1', 1, 1, 'ATTEMPT-1', 1, 'CREATE', 'ACCEPTED', '2026-09-19 09:00:05+00:00', 'abc', 200, 1, 'FRESH');
            INSERT INTO ExperimentalRiotCreateAuthorizations (AuthorizationId, AuthorizationVersion, UpperId, DemandId, MovementLegId, AgvLifecycleGeneration, DispatchGeneration, ExpiresAt, PersistedAt)
            VALUES ('AUTH-1', 1, 'W2G-D-1-PICKUP-1', 'D-1', 'LEG-1', 1, 1, '2026-09-19 10:00:00+00:00', '2026-09-19 09:00:01+00:00');
            INSERT INTO OwnOrderRebuilds (RebuildId, JourneyId, DemandId, AgvId, VehicleKey, StopId, Source, EndedUpperId, IncidentAt, RecordedAt, DueAt, NewUpperId, NewMovementLegId, State, CargoEvidenceRequestedWhileReady)
            VALUES ('RB-1', 'journey:D-1', 'D-1', 'AGV-01', 'VK-01', 'journey:D-1|PICKUP', 'RIOT', 'W2G-D-1-PICKUP-1', '2026-09-19 09:02:00+00:00', '2026-09-19 09:02:01+00:00', '2026-09-19 09:03:00+00:00', 'W2G-D-1-REBUILD-1', 'LEG-RB-1', 'WAITING', 0);
            """;
            await insert.ExecuteNonQueryAsync(Token);
        }

        // Every nullable column of the six rebuilt tables gets a value, different on every row, so a column the hand-written
        // copy leaves out shows as a changed value -- not only the columns the acceptance path happens to write.
        foreach (string table in RebuiltTables)
        {
            foreach ((string column, string type) in await NullableColumnsAsync(fixture.Connection, table))
            {
                await using SqliteCommand fill = fixture.Connection.CreateCommand();
                string value = type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase)
                    ? "rowid * 1000 + " + (column.Length * 7 % 997).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : $"'fill:{column}:' || rowid";
                fill.CommandText = $"UPDATE \"{table}\" SET \"{column}\" = {value} WHERE \"{column}\" IS NULL";
                await fill.ExecuteNonQueryAsync(Token);
            }
            foreach ((string column, _) in await NullableColumnsAsync(fixture.Connection, table))
            {
                Assert.Equal(0L, await ScalarAsync(fixture.Connection, $"SELECT COUNT(*) FROM \"{table}\" WHERE \"{column}\" IS NULL"));
            }
            Assert.NotEqual(0L, await ScalarAsync(fixture.Connection, $"SELECT COUNT(*) FROM \"{table}\""));
        }
    }

    private static async Task AcceptAndCompleteAsync(Batch7JourneyFixture fixture, DateTimeOffset now)
    {
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-1", "AGV-01", "VK-01", now);
        await fixture.RenewContextAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-2", "AGV-02", "VK-02", now.AddMinutes(1));
        await fixture.RenewContextAsync();
        await Batch7JourneyFixture.AcceptAsync(fixture.Context, "D-3", "AGV-03", "VK-03", now.AddMinutes(2));
        await fixture.RenewContextAsync();
        await Batch7JourneyFixture.CompleteByUnloadAsync(fixture.Context, "D-3", now.AddMinutes(10));
        await fixture.RenewContextAsync();
    }

    private static async Task<List<(string Column, string Type)>> NullableColumnsAsync(SqliteConnection connection, string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT name, type FROM pragma_table_info('{table}') WHERE \"notnull\" = 0 AND pk = 0";
        List<(string, string)> columns = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            columns.Add((reader.GetString(0), reader.GetString(1)));
        }
        return columns;
    }

    /// <summary>The six tables this migration rebuilds.</summary>
    private static readonly string[] RebuiltTables =
    [
        "ExperimentalRiotCreateAuthorizations", "JourneyRuntimes", "OrderIntents", "OwnOrderRebuilds", "RiotDispatchAuditEvents",
        "VehiclePurposeClaims",
    ];

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
        ["OwnOrderRebuilds"] = ["DemandId"],
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
