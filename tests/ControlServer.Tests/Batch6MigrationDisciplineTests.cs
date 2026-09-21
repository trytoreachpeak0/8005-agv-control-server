using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Composition;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ControlServer.Tests;

/// <summary>
/// 批次 6 只有一个迁移，由建表票 control-server#159 独占；批次 6 其余票零迁移（规格 18.2、21.1）。
/// </summary>
/// <remarks>
/// 与 <see cref="Batch4MigrationDisciplineTests"/>、<see cref="Batch5MigrationDisciplineTests"/> 同一个理由、同一个做法：批次 3 那份测试的
/// <c>MigrationsAfterBatch3</c> 名单点了本迁移的名，这里只放批次 6 自己的断言。
/// </remarks>
public sealed class Batch6MigrationDisciplineTests
{
    private const string MigrationBeforeBatch6 = "20260917015519_Batch5JourneyBlockReasonSince";
    private const string Batch6Migration = "20260919021150_Batch6TaskTypeStationBindings";
    private const string Batch6MigrationSuffix = "_Batch6TaskTypeStationBindings";

    private static readonly DateTimeOffset Now = new(2026, 9, 19, 8, 0, 0, TimeSpan.Zero);

    private static readonly string[] Batch6Tables =
    [
        "TaskTypeStationActiveBindingSets",
        "TaskTypeStationBindingSetVersions",
        "TaskTypeStationBindings",
        "TaskTypeStationCatalogChanges",
        "TaskTypeStationHolds",
        "TaskTypeStationRequirements",
        "TaskTypeStationRuleVersions",
        "TaskTypeStationRules",
    ];

    /// <summary>
    /// 批次 6 迁移之后允许存在的迁移，按名字点出来。
    /// </summary>
    /// <remarks>
    /// 原来断言「批次 6 的迁移是最后一个」，批次 7 建表票 control-server#206 的迁移一进来就是假的；照
    /// <see cref="Batch3MigrationDisciplineTests"/> 的写法改成点名，比原来更紧：说清它之后允许有谁。批次 7 自己的断言在
    /// <see cref="Batch7MigrationDisciplineTests"/>，后续迁移（control-server#199、#186）加在那边的名单里，也加在这里。
    /// </remarks>
    private static readonly string[] MigrationsAfterBatch6 =
    [
        "20260919154546_Batch7MultiDemandJourneyPersistence",
        "20260919200353_AreaEndAdmissionRevokedSince",
        // 批次 7 迁移通道第二张：control-server#199，两张审计表的 BEFORE UPDATE／BEFORE DELETE 触发器。
        // 自己的断言在 AuditDatabaseImmutabilityTests。
        "20260920001500_AuditImmutabilityTriggers",
        // 批次 7 迁移通道第三张：control-server#211，**data only, no schema change**——把升级那一刻正在装货的
        // 那条归属回填成 LOADING。为什么非它不可、谓词为什么无歧义，写在 Batch7MigrationDisciplineTests 的同一条上。
        // 自己的断言在 Batch7LoadingMembershipBackfillMigrationTests。
        "20260920145604_Batch7LoadingMembershipBackfill",
    ];

    [Fact]
    public async Task Batch6AddsExactlyOneMigrationAndItComesStraightAfterTheBatch5Migration()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        string[] migrations = [.. fixture.Context.Database.GetMigrations()];

        string batch6 = Assert.Single(migrations, name => name.Contains("Batch6", StringComparison.Ordinal));
        Assert.EndsWith(Batch6MigrationSuffix, batch6, StringComparison.Ordinal);
        Assert.Equal(MigrationBeforeBatch6, migrations[Array.IndexOf(migrations, batch6) - 1]);
        Assert.Equal(MigrationsAfterBatch6, migrations[(Array.IndexOf(migrations, batch6) + 1)..]);
    }

    [Fact]
    public async Task MigratingAnEmptyDatabaseCreatesEveryTableAndBothUniqueIndexesBatch6Needs()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        HashSet<string> tables = await ReadTableNamesAsync(fixture.Connection);
        foreach (string table in Batch6Tables)
        {
            Assert.Contains(table, tables);
        }
        Assert.Contains(
            ["MapId", "Version", "StationRiotId"],
            await ReadUniqueIndexColumnsAsync(fixture.Connection, "TaskTypeStationBindings"));
        Assert.Contains(
            ["MapId", "StationRiotId", "CatalogRevision"],
            await ReadUniqueIndexColumnsAsync(fixture.Connection, "TaskTypeStationCatalogChanges"));
    }

    [Fact]
    public async Task MigratingADatabaseThatAlreadyHoldsDataLeavesEveryExistingTableRowForRowTheSame()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        await fixture.MigrateToAsync(MigrationBeforeBatch6);
        await SeedBatch5DataAsync(fixture);
        HashSet<string> existingTables = await ReadTableNamesAsync(fixture.Connection);
        Dictionary<string, string[]> before = [];
        foreach (string table in existingTables)
        {
            before[table] = await DumpAsync(fixture.Connection, table);
        }
        Assert.NotEmpty(before["GovernedConfigurationSnapshots"]);
        Assert.NotEmpty(before["DispatchZoneAreaAssignments"]);
        Assert.NotEmpty(before["ConfigurationConsumerBindings"]);

        await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        foreach (string table in existingTables.Where(table => table != "__EFMigrationsHistory"))
        {
            Assert.Equal(before[table], await DumpAsync(fixture.Connection, table));
        }
    }

    /// <summary>
    /// control-server#159 审查「可选」第 3 条、由 control-server#199 补上：把一个装着批次 6 数据的库迁回批次 5 再迁上来。
    /// 迁下去，八张表一张不剩，其余每一张表的结构与批次 5 那一刻逐字相同；再迁上来，<c>sqlite_master</c> 里的表、索引与
    /// 第一次迁上来时逐字相同。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 表里的数据回不来，也不该断言它回来：<c>Down()</c> 是 <c>DropTable</c>，八张表连同内容一起没了。这条测试证的是
    /// 「退得干净、上得一致」，不是「退回去不丢数据」。
    /// </para>
    /// <para>
    /// <b>已知回滚限制，这里把它钉成事实而不是修它</b>（PR #171 正文）：<c>GovernedConfigurationSnapshots</c> 里
    /// <c>ObjectKind='TaskTypeStationRule'</c> 的快照行不随 <c>Down()</c> 删——那张表是批次 3 建的，批次 6 只是往里写。
    /// 退回批次 5 的二进制读到这种行会失败。修它要么让 <c>Down()</c> 按种类删快照（跨了表的归属），要么让旧二进制容忍
    /// 不认识的种类（改的是已经发出去的代码），两条都不属于本票。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MigratingDownToBatch5DropsAllEightTablesAndMigratingUpAgainRebuildsThemIdentically()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await fixture.MigrateToAsync(MigrationBeforeBatch6);
        await SeedBatch5DataAsync(fixture);
        string[] schemaAtBatch5 = await ReadSchemaAsync(fixture.Connection);

        await fixture.MigrateToAsync(Batch6Migration);
        await SeedBatch6DataAsync(fixture);
        string[] schemaAtBatch6 = await ReadSchemaAsync(fixture.Connection);
        Assert.NotEmpty(await DumpAsync(fixture.Connection, "TaskTypeStationRules"));
        Assert.NotEmpty(await DumpAsync(fixture.Connection, "TaskTypeStationBindings"));
        Assert.NotEmpty(await DumpAsync(fixture.Connection, "TaskTypeStationHolds"));
        Assert.NotEmpty(await DumpAsync(fixture.Connection, "TaskTypeStationCatalogChanges"));
        string[] ruleSnapshots = await RuleSnapshotIdsAsync(fixture.Connection);
        Assert.NotEmpty(ruleSnapshots);

        await fixture.MigrateToAsync(MigrationBeforeBatch6);

        HashSet<string> afterDown = await ReadTableNamesAsync(fixture.Connection);
        Assert.All(Batch6Tables, table => Assert.DoesNotContain(table, afterDown));
        Assert.Equal(schemaAtBatch5, await ReadSchemaAsync(fixture.Connection));
        // The known limit: the rule snapshots batch 6 wrote into a batch 3 table outlive the tables that produced them.
        Assert.Equal(ruleSnapshots, await RuleSnapshotIdsAsync(fixture.Connection));

        await fixture.MigrateToAsync(Batch6Migration);

        Assert.Equal(schemaAtBatch6, await ReadSchemaAsync(fixture.Connection));
        foreach (string table in Batch6Tables)
        {
            Assert.Empty(await DumpAsync(fixture.Connection, table));
        }
        Assert.Equal(ruleSnapshots, await RuleSnapshotIdsAsync(fixture.Connection));
        // And the tables work again: the same seed runs a second time on the rebuilt schema.
        fixture.Context.ChangeTracker.Clear();
        await SeedBatch6DataAsync(fixture);
        Assert.NotEmpty(await fixture.Context.Set<TaskTypeStationBindingRow>().ToArrayAsync(cancellationToken));
    }

    [Fact]
    public async Task EveryBatch6TableIsReachableWithoutADbSetPropertyOnTheSharedContextFile()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Empty(await fixture.Context.Set<TaskTypeStationRuleVersionRow>().ToArrayAsync(cancellationToken));
        Assert.Empty(await fixture.Context.Set<TaskTypeStationRuleRow>().ToArrayAsync(cancellationToken));
        Assert.Empty(await fixture.Context.Set<TaskTypeStationBindingSetVersionRow>().ToArrayAsync(cancellationToken));
        Assert.Empty(await fixture.Context.Set<TaskTypeStationBindingRow>().ToArrayAsync(cancellationToken));
        Assert.Empty(await fixture.Context.Set<TaskTypeStationRequirementRow>().ToArrayAsync(cancellationToken));
        Assert.Empty(await fixture.Context.Set<TaskTypeStationActiveBindingSetRow>().ToArrayAsync(cancellationToken));
        Assert.Empty(await fixture.Context.Set<TaskTypeStationHoldRow>().ToArrayAsync(cancellationToken));
        Assert.Empty(await fixture.Context.Set<TaskTypeStationCatalogChangeRow>().ToArrayAsync(cancellationToken));
    }

    [Fact]
    public async Task TheModelSnapshotMatchesTheModel()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();

        Assert.False(fixture.Context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task EveryBatch6PortIsRegisteredWithTheTaskTypeStationModuleSoNoLaterTicketEditsIt()
    {
        // control-server#160 to #163 each call these ports. Registered here once, in a module of their own, none of them
        // touches GovernanceModule.cs or this module -- the same reason the tables all land in one migration.
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        ServiceCollection services = new();
        services.AddDbContext<ControlServerDbContext>(options => options.UseSqlite(connection));
        services.AddGovernance(new ConfigurationBuilder().Build());
        services.AddTaskTypeStations();
        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        Assert.IsType<TaskTypeStationRuleStore>(scope.ServiceProvider.GetRequiredService<ITaskTypeStationRuleStore>());
        Assert.IsType<TaskTypeStationBindingStore>(
            scope.ServiceProvider.GetRequiredService<ITaskTypeStationBindingStore>());
        Assert.IsType<TaskTypeStationHoldStore>(scope.ServiceProvider.GetRequiredService<ITaskTypeStationHoldStore>());
        Assert.IsType<TaskTypeStationCatalogChangeStore>(
            scope.ServiceProvider.GetRequiredService<ITaskTypeStationCatalogChangeStore>());
        Assert.IsType<DemandTaskTypeStationFreezeStore>(
            scope.ServiceProvider.GetRequiredService<IDemandTaskTypeStationFreeze>());
    }

    /// <summary>
    /// A database as batch 5 left it: a published slot model with its snapshot and audit, an area assignment version, and
    /// one demand that froze it.
    /// </summary>
    private static async Task SeedBatch5DataAsync(MigrationFixture fixture)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        GovernanceStore governance = new(
            fixture.Context,
            new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
            AuditRetentionPolicy.Default);
        GovernedConfigurationPublisher publisher = new(governance, governance);
        SlotConfigurationAuthorityStore slots = new(fixture.Context, publisher);
        SlotTemplateRow template = await slots.PublishTemplateVersionAsync(
            ApprovedSlotHardwareFacts.TemplateKey,
            new SlotTemplateSpecification(600, 400, 300, ["PDFN5", "TOLL"]),
            Now,
            cancellationToken);
        await slots.PublishModelVersionAsync(
            ApprovedSlotHardwareFacts.ModelKey,
            [
                .. Enumerable.Range(1, ApprovedSlotHardwareFacts.SlotCount).Select(number =>
                    new SlotModelSlotSpecification(
                        number, number <= 4 ? "FRONT" : "REAR", template.TemplateKey, template.Version))
            ],
            Now,
            cancellationToken);
        AreaAssignmentTableVersion table = await new AreaAssignmentStore(fixture.Context, publisher).WriteVersionAsync(
            [new AreaAssignment("N01", "MAP-25-WIRE_TO_GATE", "FRONT")], Now, cancellationToken);
        await new DemandAreaAssignmentFreezeStore(fixture.Context).FreezeAsync(
            "demand-batch5", table.Version, Now, cancellationToken);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>
    /// 批次 6 自己那八张表里的数据，全部经真实写入路径落进去：一版规则（连带它在批次 3 那张快照表里的治理快照）、一张
    /// 图的绑定集与生效指针、一条人工暂停、一条目录变化记录、一条需求冻结。
    /// </summary>
    private static async Task SeedBatch6DataAsync(MigrationFixture fixture)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        GovernanceStore governance = new(
            fixture.Context,
            new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"),
            AuditRetentionPolicy.Default);
        GovernedConfigurationPublisher publisher = new(governance, governance);
        TaskTypeStationRuleStore rules = new(fixture.Context, publisher);
        TaskTypeStationBindingStore bindings = new(fixture.Context, publisher);

        TaskTypeStationVersionWrite<TaskTypeStationRuleVersion> rule = await rules.WriteVersionAsync(
            TaskTypeStationTestData.SixRules, TaskTypeStationTestData.Source, Now, cancellationToken);
        TaskTypeStationVersionWrite<TaskTypeStationBindingSetVersion> set = await bindings.WriteVersionAsync(
            25,
            rule.Version.Version,
            [TransportTaskTypes.WireToGate],
            [TaskTypeStationTestData.GateBinding],
            1,
            TaskTypeStationTestData.Source,
            Now,
            cancellationToken);
        await bindings.SetActiveAsync(25, set.Version.Version, Now, cancellationToken);
        await new TaskTypeStationHoldStore(fixture.Context).RaiseAsync(
            25, TransportTaskTypes.WireToGate, TaskTypeStationHoldSource.Manual, "MANUAL_TIGHTEN", "{}", "operator",
            Now, cancellationToken);
        await new TaskTypeStationCatalogChangeStore(fixture.Context).RecordAsync(
            new TaskTypeStationCatalogChange(
                "catalog-change-batch6", 25, 210, "关卡", "关卡A", "RENAMED", "SITE_REVIEW_REQUIRED", 2, Now,
                [TransportTaskTypes.WireToGate], null),
            cancellationToken);
        await new DemandTaskTypeStationFreezeStore(fixture.Context).FreezeAsync(
            "demand-batch6", rule.Version.Version, 25, set.Version.Version, Now, cancellationToken);
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>批次 6 写进批次 3 那张快照表的规则快照，按 id 排序。</summary>
    private static async Task<string[]> RuleSnapshotIdsAsync(SqliteConnection connection)
    {
        List<string> ids = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT SnapshotId FROM GovernedConfigurationSnapshots WHERE ObjectKind = 'TaskTypeStationRule' ORDER BY SnapshotId";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            ids.Add(reader.GetString(0));
        }
        return [.. ids];
    }

    /// <summary>每张表的每一列（名、类型、可空、默认值、主键位），以及每个索引与触发器的定义原文。</summary>
    private static async Task<string[]> ReadSchemaAsync(SqliteConnection connection)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        List<string> schema = [];
        foreach (string table in (await ReadTableNamesAsync(connection)).Order(StringComparer.Ordinal))
        {
            await using SqliteCommand columns = connection.CreateCommand();
            columns.CommandText =
                $"SELECT name || ' ' || type || ' notnull=' || \"notnull\" || ' default=' || coalesce(dflt_value, '-') "
                + $"|| ' pk=' || pk FROM pragma_table_info('{table}')";
            await using SqliteDataReader reader = await columns.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                schema.Add($"column {table}.{reader.GetString(0)}");
            }
        }
        await using SqliteCommand definitions = connection.CreateCommand();
        definitions.CommandText =
            "SELECT type || ' ' || name || ': ' || coalesce(sql, '') FROM sqlite_master "
            + "WHERE type IN ('index', 'trigger') AND name NOT LIKE 'sqlite_%'";
        await using SqliteDataReader reader2 = await definitions.ExecuteReaderAsync(cancellationToken);
        while (await reader2.ReadAsync(cancellationToken))
        {
            schema.Add(reader2.GetString(0));
        }
        schema.Sort(StringComparer.Ordinal);
        return [.. schema];
    }

    private static async Task<List<string[]>> ReadUniqueIndexColumnsAsync(SqliteConnection connection, string table)
    {
        List<string> uniqueIndexes = [];
        await using (SqliteCommand list = connection.CreateCommand())
        {
            list.CommandText = $"PRAGMA index_list('{table}')";
            await using SqliteDataReader reader = await list.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                if (reader.GetInt64(reader.GetOrdinal("unique")) == 1)
                {
                    uniqueIndexes.Add(reader.GetString(reader.GetOrdinal("name")));
                }
            }
        }

        List<string[]> columns = [];
        foreach (string index in uniqueIndexes)
        {
            List<string> names = [];
            await using SqliteCommand info = connection.CreateCommand();
            info.CommandText = $"PRAGMA index_info('{index}')";
            await using SqliteDataReader reader = await info.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                names.Add(reader.GetString(reader.GetOrdinal("name")));
            }
            columns.Add([.. names]);
        }
        return columns;
    }

    /// <summary>Every row of a table as SQLite's own literal for each column, in rowid order.</summary>
    private static async Task<string[]> DumpAsync(SqliteConnection connection, string table)
    {
        List<string> columns = [];
        await using (SqliteCommand info = connection.CreateCommand())
        {
            info.CommandText = $"PRAGMA table_info('{table}')";
            await using SqliteDataReader reader = await info.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText =
            $"SELECT {string.Join(" || '|' || ", columns.Select(name => $"'{name}=' || quote(\"{name}\")"))} "
            + $"FROM \"{table}\" ORDER BY 1";
        List<string> rows = [];
        await using SqliteDataReader rowsReader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await rowsReader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(rowsReader.GetString(0));
        }
        return [.. rows];
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(SqliteConnection connection)
    {
        HashSet<string> tables = new(StringComparer.Ordinal);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            tables.Add(reader.GetString(0));
        }
        return tables;
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
