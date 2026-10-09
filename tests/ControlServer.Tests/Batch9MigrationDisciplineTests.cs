using System.Globalization;
using System.Reflection;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// 批次 9 的迁移只有一次，由建表票 control-server#399 独占；批次9-02～9-12 零迁移（规格 18.2）。
/// </summary>
/// <remarks>
/// <para>
/// 本迁移按<b>完整后缀</b>认出，不用 <c>Contains("Batch9")</c>：后面的迁移名也可能带这个字样。它紧跟集成分支上当时的最后一个迁移
/// （control-server#387 的 <c>Batch8RetireOldVehicleOccupancy</c>）；它之后允许有哪些迁移，按名字点在 <see cref="MigrationsAfterBatch9"/>，
/// 本票合入时为空。批次 3、6、7、8 那四份纪律测试的名单也点了本迁移的名。
/// </para>
/// <para>
/// 引擎零行为变化在库这一层的样子：既有表的列、列序与每一行都不变，只在四张表的末尾多出列——两张站点独占表各一列可空的名册版本、
/// <c>OrderIntents</c> 的订单形态（缺省即回填 <c>SINGLE_MOVE</c>）、<c>JourneyRuntimes</c> 两列可空列——且新列的值是缺省或空。
/// 两张站点独占表因为改 CHECK 要整表重建，本迁移手写了保留列序的重建，列清单也是手写的：漏拷一列，那一列在迁移后就成了空值。
/// 所以种子把被动到的四张表的每个可空列都填成逐行不同的非空值——只填受理路径会写的列时，漏拷一个今天恰好为空的列看不出来
/// （control-server#394 审查必修 2）。
/// </para>
/// <para>
/// 这是本迁移唯一的守卫：<c>ZeroChangePins/</c> 走 <c>EnsureCreated</c>，不经过迁移。
/// </para>
/// </remarks>
public sealed class Batch9MigrationDisciplineTests
{
    internal const string PreviousMigration = "20260929070322_Batch8RetireOldVehicleOccupancy";
    internal const string Batch9MigrationSuffix = "_Batch9ChargingPersistence";
    internal const string Batch9Migration = "20260929114754" + Batch9MigrationSuffix;

    /// <summary>批次 9 迁移之后允许存在的迁移，按名字点出来。</summary>
    private static readonly string[] MigrationsAfterBatch9 =
    [
        // control-server#383：批次 8 人工判故障（REQ-0359）——新建 SlotFaultDeclarations 一张表（建空），带「同一尝试至多一条未结判定」的过滤唯一索引；既有表与行不变。在 batch-p3/v3 上建，合回集成分支前按先合入的迁移重建。自己的断言在 SlotFaultDeclarationTests。
        "20260930012829_Batch8SlotFaultDeclarations",
        // control-server#385：批次 8 恢复面（REQ-0242 CP-0008、REQ-0364 CP-0009）——ExceptionRecoverySessions 加可空列 ClosedReason，RecoveryWorkflows 加可空列 HandoffSublot、HandoffReceiverName、HandedOverAt，新建 SlotDoorHolds 一张表（建空，AgvId 普通索引）；纯 ADD COLUMN 与 CREATE TABLE，既有表与行不变。在 batch-p3/v3 上建，合回集成分支前按先合入的迁移重建。自己的断言在 RecoveryStateMachineG2Tests（RecoverySurface 分部）。
        "20260930041750_Batch8RecoverySurface",
        // control-server#505：**只改数据，不动 schema**——升级那一刻停在 Blocked、阻塞码以 _NOT_RECONCILED 结尾的旅程，码加后缀 _BEFORE_UPGRADE，成为不可放行的那一族（那时的码不保证有 RecoveryRequired 的需求作标记，而升级前第 5 条本来就一律不放，现场行为不变）；Down 去掉两个新后缀。调度 Coordinator 9 于 2026-10-08 给了迁移通道。自己的断言在 RecoveryEndingReleasesBlockedJourneyTests.Migration.cs。
        "20261008052643_UnreleasableNotReconciledBlocksBeforeUpgrade",
    ];

    private static readonly string[] NewTables =
    [
        "ChargerRosterEntries", "ChargerRosterVehicleScopes", "ChargerRosterVersions", "ChargingCycles",
        "ChargingPolicyActivations", "ChargingPolicyApprovals", "ChargingPolicyVehicleScopes", "ChargingPolicyVersions",
        "ChargingStationAllocationHolds", "ChargingStationRecoveries", "ManualChargingHoldRecords", "ManualChargingHolds",
        "ManualStationClearanceConfirmations", "StationClearances", "UnableToChargeFieldConfirmations",
        "VehicleChargingEligibilityHolds", "VehicleChargingEligibilityRecoveries",
    ];

    /// <summary>The two tables this migration rebuilds by hand.</summary>
    private static readonly string[] RebuiltTables = ["StationExclusivities", "StationExclusivityRecords"];

    /// <summary>The columns this migration appends, table by table, and the value every existing row reads in them.</summary>
    private static readonly Dictionary<string, (string Column, string Value)[]> AppendedColumns = new(StringComparer.Ordinal)
    {
        ["StationExclusivities"] = [("ChargerRosterVersion INTEGER notnull=0 default=- pk=0", "NULL")],
        ["StationExclusivityRecords"] = [("ChargerRosterVersion INTEGER notnull=0 default=- pk=0", "NULL")],
        ["OrderIntents"] = [("OrderShape TEXT notnull=1 default='SINGLE_MOVE' pk=0", "'SINGLE_MOVE'")],
        ["JourneyRuntimes"] =
        [
            ("ChargingPolicyVersion INTEGER notnull=0 default=- pk=0", "NULL"),
            ("PublishedBatteryState TEXT notnull=0 default=- pk=0", "NULL"),
        ],
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Batch9AddsExactlyOneMigrationStraightAfterTheBatch8RetirementAndOnlyNamedOnesFollowIt()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        string[] migrations = [.. fixture.Context.Database.GetMigrations()];

        string batch9 = Assert.Single(migrations, name => name.EndsWith(Batch9MigrationSuffix, StringComparison.Ordinal));
        Assert.Equal(Batch9Migration, batch9);
        int index = Array.IndexOf(migrations, batch9);
        Assert.Equal(PreviousMigration, migrations[index - 1]);
        Assert.Equal(MigrationsAfterBatch9, migrations[(index + 1)..]);
    }

    [Fact]
    public async Task TheModelSnapshotMatchesTheModel()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync();

        Assert.False(fixture.Context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task MigratingAnEmptyDatabaseCreatesEveryBatch9TableWithItsKeysConstraintsAndIndexes()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        await fixture.Context.GetService<IMigrator>().MigrateAsync(Batch9Migration, Token);

        Assert.Equal(
            [
                "ChargerRosterEntries pk=Version,MapId,StationId",
                "ChargerRosterVehicleScopes pk=Version,MapId,StationId,VehicleKey",
                "ChargerRosterVersions pk=Version",
                "ChargingCycles pk=CycleId",
                "ChargingPolicyActivations pk=ActivationId",
                "ChargingPolicyApprovals pk=ApprovalId",
                "ChargingPolicyVehicleScopes pk=Version,VehicleKey",
                "ChargingPolicyVersions pk=Version",
                "ChargingStationAllocationHolds pk=HoldId",
                "ChargingStationRecoveries pk=RecoveryId",
                "ManualChargingHoldRecords pk=HoldId",
                "ManualChargingHolds pk=VehicleKey",
                "ManualStationClearanceConfirmations pk=ConfirmationRequestId",
                "StationClearances pk=ClearanceId",
                "UnableToChargeFieldConfirmations pk=ConfirmationRequestId",
                "VehicleChargingEligibilityHolds pk=HoldId",
                "VehicleChargingEligibilityRecoveries pk=RecoveryId",
            ],
            await Task.WhenAll(NewTables.Select(async table =>
                $"{table} pk={string.Join(",", await PrimaryKeyAsync(fixture.Connection, table))}")));

        Assert.Equal(
            [
                "IX_ChargingCycles_JourneyId on JourneyId",
                "IX_ChargingCycles_MapId_StationId on MapId,StationId",
                // One unfinished cycle per vehicle, decided by the index (not by a read first).
                "IX_ChargingCycles_VehicleKey_Open unique on VehicleKey where Phase <> 'ENDED'",
                "IX_ChargingPolicyActivations_Sequence unique on Sequence",
                "IX_ChargingPolicyActivations_Version on Version",
                "IX_ChargingPolicyApprovals_Version on Version",
                "IX_ChargingStationAllocationHolds_IdempotencyKey unique on IdempotencyKey",
                "IX_ChargingStationAllocationHolds_MapId_StationId on MapId,StationId",
                "IX_ChargingStationRecoveries_HoldId unique on HoldId",
                // The record is evidence, not an arbiter: not unique (control-server#394 review, required 1).
                "IX_ManualChargingHoldRecords_VehicleKey on VehicleKey",
                "IX_ManualChargingHolds_HoldId unique on HoldId",
                "IX_ManualStationClearanceConfirmations_RequestMessageId unique on RequestMessageId",
                "IX_StationClearances_CycleId unique on CycleId",
                "IX_StationClearances_MapId_StationId on MapId,StationId",
                "IX_StationExclusivities_JourneyId on JourneyId",
                "IX_StationExclusivities_RecordId unique on RecordId",
                "IX_StationExclusivities_VehicleKey on VehicleKey",
                "IX_StationExclusivityRecords_JourneyId on JourneyId",
                "IX_StationExclusivityRecords_MapId_StationId on MapId,StationId",
                "IX_StationExclusivityRecords_VehicleKey on VehicleKey",
                "IX_UnableToChargeFieldConfirmations_RequestMessageId unique on RequestMessageId",
                "IX_VehicleChargingEligibilityHolds_IdempotencyKey unique on IdempotencyKey",
                "IX_VehicleChargingEligibilityHolds_VehicleKey on VehicleKey",
                "IX_VehicleChargingEligibilityRecoveries_HoldId unique on HoldId",
            ],
            await IndexesAsync(fixture.Connection, [.. NewTables, .. RebuiltTables]));

        // Every CHECK in the schema, not only this migration's tables: since batch 8's own assertion stops at its migration,
        // this is the one place the whole list is pinned (#416 review, 4). Each policy field's own range, and nothing
        // relating the three thresholds (REQ-0281): that relation has one definition, the validation batch 9-02 writes.
        // OrderIntents.OrderShape has no CHECK either.
        Assert.Equal(
            [
                """ChargingCycles: CONSTRAINT "CK_ChargingCycles_Phase" CHECK ("Phase" IN ('ACTIVE', 'CLEARING', 'ENDED'))""",
                """ChargingCycles: CONSTRAINT "CK_ChargingCycles_WireState" CHECK ("WireState" IN ('NOT_CHARGING', 'ALLOCATED', 'EN_ROUTE', 'CHARGING', 'COMPLETE', 'UNABLE_TO_CHARGE', 'UNKNOWN'))""",
                """ChargingPolicyApprovals: CONSTRAINT "CK_ChargingPolicyApprovals_Source" CHECK ("Source" IN ('FIELD', 'TEST_FIXTURE', 'L2_PRESET'))""",
                """ChargingPolicyVersions: CONSTRAINT "CK_ChargingPolicyVersions_ChargingCompletionThresholdPercent" CHECK ("ChargingCompletionThresholdPercent" BETWEEN 0 AND 100)""",
                """ChargingPolicyVersions: CONSTRAINT "CK_ChargingPolicyVersions_EstimatedTaskConsumptionPercent" CHECK ("EstimatedTaskConsumptionPercent" BETWEEN 0 AND 100)""",
                """ChargingPolicyVersions: CONSTRAINT "CK_ChargingPolicyVersions_MandatoryChargeEntryThresholdPercent" CHECK ("MandatoryChargeEntryThresholdPercent" BETWEEN 0 AND 100)""",
                """ChargingPolicyVersions: CONSTRAINT "CK_ChargingPolicyVersions_MinimumPostTaskBatteryMarginPercent" CHECK ("MinimumPostTaskBatteryMarginPercent" BETWEEN 0 AND 100)""",
                """ChargingPolicyVersions: CONSTRAINT "CK_ChargingPolicyVersions_ProgressMinimumIncreasePercent" CHECK ("ProgressMinimumIncreasePercent" BETWEEN 1 AND 100)""",
                """ChargingPolicyVersions: CONSTRAINT "CK_ChargingPolicyVersions_ProgressObservationWindowSeconds" CHECK ("ProgressObservationWindowSeconds" > 0)""",
                """ChargingPolicyVersions: CONSTRAINT "CK_ChargingPolicyVersions_ProgressStabilizationSeconds" CHECK ("ProgressStabilizationSeconds" > 0)""",
                """ChargingStationAllocationHolds: CONSTRAINT "CK_ChargingStationAllocationHolds_RootCause" CHECK ("RootCause" IN ('UNKNOWN'))""",
                """ChargingStationAllocationHolds: CONSTRAINT "CK_ChargingStationAllocationHolds_Trigger" CHECK ("Trigger" IN ('UNABLE_TO_CHARGE_CONFIRMED', 'INTERRUPTION_CONFIRMED', 'NO_PROGRESS_CONFIRMED', 'MAINTENANCE'))""",
                """StationClearances: CONSTRAINT "CK_StationClearances_Proof" CHECK ("Proof" IN ('ARRIVED_AT_WAITING_POINT', 'MANUAL_CONFIRMATION'))""",
                """StationExclusivities: CONSTRAINT "CK_StationExclusivities_State" CHECK ("State" IN ('RESERVED', 'OCCUPIED'))""",
                """StationExclusivities: CONSTRAINT "CK_StationExclusivities_StationKind" CHECK ("StationKind" IN ('WAITING_POINT', 'FIXED_TASK_STATION', 'CHARGER'))""",
                """StationExclusivityRecords: CONSTRAINT "CK_StationExclusivityRecords_StationKind" CHECK ("StationKind" IN ('WAITING_POINT', 'FIXED_TASK_STATION', 'CHARGER'))""",
                """VehicleChargingEligibilityHolds: CONSTRAINT "CK_VehicleChargingEligibilityHolds_Reason" CHECK ("Reason" IN ('INTERRUPTION_CONFIRMED', 'NO_PROGRESS_CONFIRMED'))""",
                """VehiclePurposeClaimRecords: CONSTRAINT "CK_VehiclePurposeClaimRecords_Purpose" CHECK ("Purpose" IN ('TRANSPORT', 'CHARGING', 'CLEARING_MAINTENANCE', 'IDLE_RETURN'))""",
                """VehiclePurposeClaims: CONSTRAINT "CK_VehiclePurposeClaims_Purpose" CHECK ("Purpose" IN ('TRANSPORT', 'CHARGING', 'CLEARING_MAINTENANCE', 'IDLE_RETURN'))""",
            ],
            await CheckConstraintsAsync(fixture.Connection));

        // The appended columns are their tables' last ones.
        Dictionary<string, string[]> columns = await ColumnsOfEveryTableAsync(fixture.Connection);
        foreach ((string table, (string Column, string Value)[] appended) in AppendedColumns)
        {
            Assert.Equal(appended.Select(column => column.Column), columns[table][^appended.Length..]);
        }
    }

    [Fact]
    public async Task WithJourneysInFlightEveryExistingRowAndColumnStaysExactlyAsItWasAndTheNewTablesStartEmpty()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Token);
        await SeedAsync(fixture);
        Dictionary<string, string[]> columnsBefore = await ColumnsOfEveryTableAsync(fixture.Connection);
        Dictionary<string, string[]> rowsBefore = await DumpEveryTableAsync(fixture.Connection);
        Assert.Equal(3, rowsBefore["JourneyRuntimes"].Length);
        Assert.Equal(3, rowsBefore["OrderIntents"].Length);
        Assert.Equal(2, rowsBefore["VehiclePurposeClaims"].Length);
        Assert.Equal(2, rowsBefore["StationExclusivities"].Length);
        Assert.Equal(3, rowsBefore["StationExclusivityRecords"].Length);

        // Journeys in flight while the migration runs: it runs all the same.
        await migrator.MigrateAsync(Batch9Migration, Token);

        Dictionary<string, string[]> columnsAfter = await ColumnsOfEveryTableAsync(fixture.Connection);
        Assert.Equal(NewTables, columnsAfter.Keys.Except(columnsBefore.Keys).Order(StringComparer.Ordinal));
        Assert.Empty(columnsBefore.Keys.Except(columnsAfter.Keys));
        foreach ((string table, string[] columns) in columnsBefore)
        {
            (string Column, string Value)[] appended = AppendedColumns.GetValueOrDefault(table, []);
            // Every column where it was, the appended ones after them.
            Assert.Equal([.. columns, .. appended.Select(column => column.Column)], columnsAfter[table]);
            // Every row value for value; in the appended columns, the default or null.
            string suffix = string.Concat(appended.Select(column => $"|{column.Column.Split(' ')[0]}={column.Value}"));
            Assert.Equal(
                rowsBefore[table].Select(row => row + suffix).Order(StringComparer.Ordinal),
                await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
        }
        Assert.Equal(3L, await ScalarAsync(fixture.Connection, "SELECT COUNT(*) FROM OrderIntents WHERE OrderShape = 'SINGLE_MOVE'"));

        foreach (string table in NewTables)
        {
            Assert.Empty(await Batch7JourneyFixture.DumpAsync(fixture.Connection, table));
        }
        Assert.Equal(0L, await ScalarAsync(fixture.Connection, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
        Assert.Equal("ok", await ScalarTextAsync(fixture.Connection, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task TheTwoRebuiltTablesKeepEveryIndexAndTriggerAndDifferOnlyInTheKindCheckAndTheNewLastColumn()
    {
        // A rebuild drops the table with everything hanging off it and puts back only what it names. An object built by
        // hand-written SQL somewhere in the history and forgotten here would vanish silently; this is where it shows.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Token);
        await SeedAsync(fixture);
        Dictionary<string, string[]> before = await ObjectsOfAsync(fixture.Connection, RebuiltTables);

        await migrator.MigrateAsync(Batch9Migration, Token);
        Dictionary<string, string[]> after = await ObjectsOfAsync(fixture.Connection, RebuiltTables);

        foreach (string table in RebuiltTables)
        {
            // Everything but the table itself -- indexes, triggers -- word for word. No trigger hangs off either table
            // (control-server#199's audit triggers are on the two audit tables), so for triggers this compares two empty
            // sets; it is kept so that one added later is carried or the comparison goes red.
            Assert.Equal(
                before[table].Where(entry => !entry.StartsWith("table ", StringComparison.Ordinal)),
                after[table].Where(entry => !entry.StartsWith("table ", StringComparison.Ordinal)));
            string lastColumn = table == "StationExclusivities" ? "\"RecordId\" TEXT NOT NULL," : "\"ReleaseReason\" TEXT NULL,";
            string expected = before[table].Single(entry => entry.StartsWith("table ", StringComparison.Ordinal))
                .Replace(
                    lastColumn,
                    lastColumn + Environment.NewLine + "    \"ChargerRosterVersion\" INTEGER NULL,",
                    StringComparison.Ordinal)
                .Replace(
                    "IN ('WAITING_POINT', 'FIXED_TASK_STATION')",
                    "IN ('WAITING_POINT', 'FIXED_TASK_STATION', 'CHARGER')",
                    StringComparison.Ordinal);
            Assert.NotEqual(before[table].Single(entry => entry.StartsWith("table ", StringComparison.Ordinal)), expected);
            Assert.Equal(expected, after[table].Single(entry => entry.StartsWith("table ", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task EveryHandWrittenCopyListNamesEveryColumnTheTableHadInItsOrder()
    {
        // A column left out of a copy list comes back null, and a seed that holds null there reads the same; so the lists
        // themselves are compared with the table as the previous migration left it (incremental review of #394, A).
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        await fixture.Context.GetService<IMigrator>().MigrateAsync(PreviousMigration, Token);
        Type migration = typeof(ControlServerDbContext).Assembly.GetTypes()
            .Single(type => type.Name == Batch9MigrationSuffix.TrimStart('_') && typeof(Migration).IsAssignableFrom(type));
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

    [Fact]
    public async Task DownRestoresTheSchemaAndRowsItFoundAndUpAgainRebuildsTheSameThing()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration, Token);
        await SeedAsync(fixture);
        string[] schemaBefore = await SchemaAsync(fixture.Connection);
        Dictionary<string, string[]> rowsBefore = await DumpEveryTableAsync(fixture.Connection);

        await migrator.MigrateAsync(Batch9Migration, Token);
        string[] schemaAfter = await SchemaAsync(fixture.Connection);
        Dictionary<string, string[]> rowsAfter = await DumpEveryTableAsync(fixture.Connection);
        Assert.NotEqual(schemaBefore, schemaAfter);

        await migrator.MigrateAsync(PreviousMigration, Token);
        Assert.Equal(schemaBefore, await SchemaAsync(fixture.Connection));
        Assert.Equal(rowsBefore, await DumpEveryTableAsync(fixture.Connection));

        await migrator.MigrateAsync(Batch9Migration, Token);
        Assert.Equal(schemaAfter, await SchemaAsync(fixture.Connection));
        Assert.Equal(rowsAfter, await DumpEveryTableAsync(fixture.Connection));
    }

    [Fact]
    public async Task DownWithAChargerStillReservedFailsWholeRatherThanDropIt()
    {
        // The old CHECK refuses the CHARGER row in the copy, and the whole Down goes back: the reservation stays.
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateAsync(migrate: false);
        IMigrator migrator = fixture.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(Batch9Migration, Token);
        await ExecuteAsync(
            fixture.Connection,
            """
            INSERT INTO StationExclusivities (MapId, StationId, StationKind, State, VehicleKey, JourneyId, StateSince, RecordId, ChargerRosterVersion)
            VALUES (26, 211, 'CHARGER', 'RESERVED', 'agv02', 'charge:agv02', '2026-09-19 09:00:00+00:00', 'R-1', 2)
            """);
        string[] schemaBefore = await SchemaAsync(fixture.Connection);

        SqliteException failure = await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync(PreviousMigration, Token));
        Assert.Equal(275, failure.SqliteExtendedErrorCode); // SQLITE_CONSTRAINT_CHECK
        Assert.Contains("CK_StationExclusivities_StationKind", failure.Message, StringComparison.Ordinal);

        Assert.Equal(schemaBefore, await SchemaAsync(fixture.Connection));
        Assert.Single(await Batch7JourneyFixture.DumpAsync(fixture.Connection, "StationExclusivities"));
        Assert.Equal(Batch9Migration, (await fixture.Context.Database.GetAppliedMigrationsAsync(Token)).Last());
    }

    /// <summary>
    /// Two journeys in flight and one ended, through the real acceptance; station exclusivities of both earlier kinds; and
    /// every nullable column of the four tables this migration touches filled with a value different on every row, so a
    /// column the hand-written copy leaves out shows as a changed value.
    /// </summary>
    private static async Task SeedAsync(Batch7JourneyFixture fixture)
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

        // Written directly: the rows are only there to be carried through the rebuild.
        await ExecuteAsync(
            fixture.Connection,
            """
            INSERT INTO StationExclusivities (MapId, StationId, StationKind, State, VehicleKey, JourneyId, StateSince, WaitingPointVersion, RecordId)
            VALUES (26, 214, 'WAITING_POINT', 'RESERVED', 'VK-01', 'journey:D-1', '2026-09-19 09:00:00+00:00', 3, 'SX-1'),
                   (26, 101, 'FIXED_TASK_STATION', 'OCCUPIED', 'VK-02', 'journey:D-2', '2026-09-19 09:01:00+00:00', NULL, 'SX-2');
            INSERT INTO StationExclusivityRecords (RecordId, MapId, StationId, StationKind, VehicleKey, JourneyId, WaitingPointVersion, ReservedAt)
            VALUES ('SX-1', 26, 214, 'WAITING_POINT', 'VK-01', 'journey:D-1', 3, '2026-09-19 09:00:00+00:00'),
                   ('SX-2', 26, 101, 'FIXED_TASK_STATION', 'VK-02', 'journey:D-2', NULL, NULL),
                   ('SX-0', 26, 215, 'WAITING_POINT', 'VK-03', 'journey:D-3', 2, '2026-09-19 08:00:00+00:00');
            """);

        foreach (string table in (string[])[.. RebuiltTables, "OrderIntents", "JourneyRuntimes"])
        {
            foreach ((string column, string type) in await NullableColumnsAsync(fixture.Connection, table))
            {
                string value = type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase)
                    ? "rowid * 1000 + " + (column.Length * 7 % 997).ToString(CultureInfo.InvariantCulture)
                    : $"'fill:{column}:' || rowid";
                await ExecuteAsync(fixture.Connection, $"UPDATE \"{table}\" SET \"{column}\" = {value} WHERE \"{column}\" IS NULL");
            }
            foreach ((string column, _) in await NullableColumnsAsync(fixture.Connection, table))
            {
                Assert.Equal(0L, await ScalarAsync(fixture.Connection, $"SELECT COUNT(*) FROM \"{table}\" WHERE \"{column}\" IS NULL"));
            }
            Assert.NotEqual(0L, await ScalarAsync(fixture.Connection, $"SELECT COUNT(*) FROM \"{table}\""));
        }
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

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Token);
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
        command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type = 'table'";
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
