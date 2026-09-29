using ControlServer.Domain;
using ControlServer.Host.Composition;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

/// <summary>
/// Batch 3 creates every table it needs in one migration, and no later ticket in the batch adds a
/// second one.
/// </summary>
/// <remarks>
/// <c>ControlServerDbContextModelSnapshot.cs</c> is a single file EF rewrites in full, so two
/// tickets each adding a migration is a hard conflict -- and resolving such a conflict wrongly drops
/// tables silently rather than failing. This test is the guard: it names the migration batch 3 is
/// allowed to have, and it fails the moment a second one appears. What comes <i>after</i> batch 3 is
/// named too, in <see cref="MigrationsAfterBatch3"/>, so a migration nobody wrote down still fails.
/// </remarks>
public sealed class Batch3MigrationDisciplineTests
{
    private const string Batch3Migration = "20260909124757_Batch3GovernanceAndSlotConfiguration";

    /// <summary>
    /// 批次 3 那一个迁移之后允许存在的迁移，按名字点出来。
    /// </summary>
    /// <remarks>
    /// 原来的断言是「批次 3 的迁移是最后一个」。那句话在批次 3 自己那十三票之内是对的，但它顺带断言了
    /// 「此后永远不许有迁移」——那从来不是这条纪律要守的东西，而且协议 v2 消息面落到这条线上时就成了
    /// 假的：告警快照的采纳判据要按 <c>(会话代, 序号)</c> 比，会话代得有一列。
    ///
    /// 改成点名：批次 3 仍然只有一个迁移，且它之后只有这里列出的那些。比原来更紧——原来只说它在最后，
    /// 现在说清它之后允许有谁。
    /// </remarks>
    private static readonly string[] MigrationsAfterBatch3 =
    [
        "20260910031132_AlarmSnapshotSessionGeneration",
        "20260910063725_ReportedSlotConfigurationFingerprint",
        // 批次 2 收尾：取货点离站等待（REQ-0237 / ADR-cross-0055）要记本轮等待从何时算起，
        // JourneyRuntimes 加一列。docs/defects/20260913-no-pre-departure-correction-window.md。
        "20260913131725_StationDepartureWait",
        // 批次 4 唯一建表票 control-server#66：分区归属表（含开门侧列）、结构性派车阻断表，
        // SlotModelSlots 的 LEFT／RIGHT 改名 FRONT／REAR。批次 4 自己的断言在 Batch4MigrationDisciplineTests。
        "20260915135043_Batch4AreaAssignmentAndStructuralDispatchBlock",
        // 批次 5 唯一建表票 control-server#80：JourneyRuntimes 加 BlockReasonSince，阻断码从何时起挂上。
        // 在 #66 合入之后的 fp/v2-impl 上生成，排在它后面。批次 5 自己的断言在 Batch5MigrationDisciplineTests。
        "20260917015519_Batch5JourneyBlockReasonSince",
        // 批次 6 唯一建表票 control-server#159：任务类型规则、按图绑定集、暂停、目录变化记录，只建新表。
        // 在 #80 合入之后的 fp/v2-impl 上生成，排在它后面。批次 6 自己的断言在 Batch6MigrationDisciplineTests。
        "20260919021150_Batch6TaskTypeStationBindings",
        // 批次 7 唯一建表票 control-server#206：停靠与需求从属、用途占有、按业务键抑制、每区派车参数、按车修订号计数器，
        // JourneyRuntimes 与租约表主键改 JourneyId。在 #159 合入之后的 fp/v2-impl 上生成。批次 7 自己的断言在
        // Batch7MigrationDisciplineTests；迁移通道上排在它后面的是 control-server#199、#186。
        "20260919154546_Batch7MultiDemandJourneyPersistence",
        // 批次 7 迁移通道第一张：control-server#228，JourneyRuntimes 加 AreaEndAdmissionRevokedSince（准入被撤的等待起点）。
        // 在 #206 合入之后的 fp/v2-impl 上生成。自己的断言在 AreaEndAdmissionRevokedSinceMigrationTests。
        "20260919200353_AreaEndAdmissionRevokedSince",
        // 批次 7 迁移通道第二张：control-server#199，两张审计表加 BEFORE UPDATE／BEFORE DELETE 触发器，把「审计写一次
        // 就不再改」从 EF 层落到数据库层。在 #228 合入之后的 fp/v2-impl 上生成。自己的断言在 AuditDatabaseImmutabilityTests。
        "20260920001500_AuditImmutabilityTriggers",
        // 批次 7 迁移通道第三张：control-server#211，**data only, no schema change**——把升级那一刻正在装货的
        // 那条归属回填成 LOADING。本票票面写的是「零 migration」，这一条是 Coordinator 7 于 2026-09-20 松开那条
        // 约束后加的例外；为什么非它不可、谓词为什么无歧义，写在 Batch7MigrationDisciplineTests 的同一条上。
        // 自己的断言在 Batch7LoadingMembershipBackfillMigrationTests。
        "20260920145604_Batch7LoadingMembershipBackfill",
        // control-server#273：JourneyRuntimes 加等人起点 WaitingSince 与三列等人电量记录，WaitingSince 回填正在等人的旅程。自己的断言在 WaitingJourneyWatchMigrationTests。
        "20260922120241_WaitingJourneyWatch",
        // control-server#318：新建 OwnOrderRebuilds 表，本服务端自建单终结后同车同需求重建的记录与审计；不动任何既有表与行。
        "20260923043049_OwnOrderRebuilds",
        // control-server#318 增量审查 B1：OwnOrderRebuilds 加最近一次要快照的时刻 CargoEvidenceRequestedAt（可空，不回填）；不动任何既有行。
        "20260923094511_OwnOrderRebuildCargoEvidenceRequestedAt",
        // control-server#330：新建 ForeignRiotOrders 表，我们车上运行中的外来订单的告警、审计与取消记录；不动任何既有表与行。
        "20260923115051_ForeignRiotOrders",
        // control-server#339：JourneyStops 加 WorklistRefills（本停靠的清单因离站期限重填多发了几版），默认 0；不动任何既有行。
        "20260923152943_JourneyStopWorklistRefills",
        // control-server#357：JourneyRuntimes 加并发令牌 Version（这一行被保存了几次），默认 0；不动任何既有行。自己的断言在 JourneyRuntimeVersionMigrationTests。
        "20260927113122_JourneyRuntimeVersion",
        // control-server#366：OwnOrderRebuilds 加 VehicleHeldAt（有货重建这一次被车况或会话挡住的开始）与 CargoEvidenceNotBefore（快照要晚于它才算数），都可空、不回填；不动任何既有行。
        "20260928060831_OwnOrderRebuildCargoEvidenceNotBefore",
        // control-server#186：新建 MapNameBaselines 表，每个 mapId 一行地图名基线（首次读到的名称、待接受的新名称、最近一次接受）；不动任何既有表与行。自己的断言在 MapNameBaselinesMigrationTests。
        "20260928153736_MapNameBaselines",
        // control-server#386：批次 8 建表迁移——新建用途占有记录、站点独占与经过、等待点登记四组表（记录表建空，回填归 #387），VehiclePurposeClaims 加用途 CHECK；选甲放宽 JourneyRuntimes 锚需求与只属搬运的 14 列、OrderIntents／RiotDispatchAuditEvents／ExperimentalRiotCreateAuthorizations／OwnOrderRebuilds 的 DemandId 必填性（都是保留列序的手写重建）；既有列序与行不变。自己的断言在 Batch8MigrationDisciplineTests。
        "20260929044052_Batch8VehiclePurposePersistence",
        // control-server#387：批次 8 第二次迁移——删前核数据（未结束的旧占用没有对应用途占有即整体拒绝、列出行、什么都不删），从占有行与已释放租约回填占有记录，删租约表与 OrderIntents 的两列订单占用及其过滤唯一索引（原生 DROP COLUMN，其余列序不变）。自己的断言在 Batch8OccupancyRetirementMigrationTests。
        "20260929070322_Batch8RetireOldVehicleOccupancy",
        // control-server#399：批次 9 唯一一次迁移——新建充电桩名册、充电策略版本（含批准与激活）、充电周期、桩与车两类暂停及其恢复、清桩记录、人工充电等待及其经过、两类现场确认请求共 17 张表（建空）；StationExclusivities／StationExclusivityRecords 加 CHARGER 种类与末列可空 ChargerRosterVersion（保留列序的手写重建）；OrderIntents 末列加 OrderShape（缺省即回填 SINGLE_MOVE）、JourneyRuntimes 末列加两列可空列（原生 ADD COLUMN）；既有列序与行不变。自己的断言在 Batch9MigrationDisciplineTests。
        "20260929114754_Batch9ChargingPersistence",
    ];

    private static readonly string[] Batch3Tables =
    [
        "ActiveSlotConfigurations",
        "AdministratorAuditRecords",
        "AgvArchives",
        "AgvLifecycles",
        "AgvRestorationAttempts",
        "BusinessAuditRecords",
        "ConfigurationConsumerBindings",
        "GovernedConfigurationSnapshots",
        "OnboardAlarmSnapshots",
        "SlotConfigurationActivations",
        "SlotConfigurationReadiness",
        "SlotConfigurationVerifications",
        "SlotIoBindings",
        "SlotModelSlots",
        "SlotModelVersions",
        "SlotTemplates",
    ];

    [Fact]
    public async Task Batch3AddsExactlyOneMigrationAndItIsTheLastOne()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        string[] migrations = [.. fixture.Context.Database.GetMigrations()];

        Assert.Single(migrations, name => name.Contains("Batch3", StringComparison.Ordinal));
        Assert.Equal(
            [Batch3Migration, .. MigrationsAfterBatch3],
            migrations[^(1 + MigrationsAfterBatch3.Length)..]);
    }

    [Fact]
    public async Task TheOneMigrationCreatesEverySlotConfigurationGovernanceAndDashboardTableBatch3Needs()
    {
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        HashSet<string> tables = await ReadTableNamesAsync(fixture.Context);
        foreach (string table in Batch3Tables)
        {
            Assert.Contains(table, tables);
        }
    }

    [Fact]
    public async Task EveryBatch3TableIsReachableWithoutADbSetPropertyOnTheSharedContextFile()
    {
        // The point of the batch 3 preparation: a new table is a new configuration file, and the
        // shared ControlServerDbContext.cs is not edited to add one. Set<T>() is how they are read.
        await using MigrationFixture fixture = await MigrationFixture.CreateAsync();
        await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await fixture.Context.Set<SlotTemplateRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.Set<OnboardAlarmSnapshotRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.Context.Set<ConfigurationConsumerBindingRow>()
            .ToArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ConfiguredAuditRetentionBelowTheHundredAndEightyDayFloorIsRefusedAtStartup()
    {
        IConfiguration tooShort = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GovernanceModule.RetentionDaysKey] = "90"
            })
            .Build();
        InvalidDataException failure =
            Assert.Throws<InvalidDataException>(() => GovernanceModule.ResolveRetentionPolicy(tooShort));
        Assert.Contains("REQ-0271", failure.Message, StringComparison.Ordinal);

        IConfiguration longer = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [GovernanceModule.RetentionDaysKey] = "365"
            })
            .Build();
        Assert.Equal(365, GovernanceModule.ResolveRetentionPolicy(longer).RetainFor.TotalDays);

        IConfiguration empty = new ConfigurationBuilder().Build();
        Assert.Equal(
            AuditRetentionPolicy.Default.RetainFor,
            GovernanceModule.ResolveRetentionPolicy(empty).RetainFor);
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(ControlServerDbContext context)
    {
        HashSet<string> tables = new(StringComparer.Ordinal);
        await using SqliteCommand command = (SqliteCommand)context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
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

        private SqliteConnection Connection { get; }
        public ControlServerDbContext Context { get; }

        public static async Task<MigrationFixture> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            DbContextOptions<ControlServerDbContext> options =
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options;
            return new MigrationFixture(connection, new ControlServerDbContext(options));
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
