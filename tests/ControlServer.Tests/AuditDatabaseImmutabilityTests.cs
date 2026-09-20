using System.Globalization;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ControlServer.Tests;

/// <summary>
/// control-server#199：审计的「写一次就不再改」从 EF 层落到数据库层。<see cref="AuditImmutabilityGuard"/> 拦的是
/// <c>SaveChanges</c> 前的变更跟踪，凡不经跟踪的写法——原始 SQL、<c>ExecuteUpdateAsync</c>、<c>ExecuteDeleteAsync</c>、
/// 另开一条 SQLite 连接——全部绕得过去（control-server#161 审查 O1）。一个迁移给两张审计表建
/// <c>BEFORE UPDATE</c>／<c>BEFORE DELETE</c> 触发器，把这条规矩钉在表上。
/// </summary>
/// <remarks>
/// <para>
/// <b>触发器怎么得到「现在」。</b><c>BEFORE DELETE</c> 读不到配置，也读不到应用进程那个可以被测试伪造的时钟，所以它按
/// <c>julianday('now')</c>——数据库机器的时钟——换算成 .NET ticks，与 <c>RecordedAtUtcTicks</c> 比，只守 REQ-0271 那个
/// <b>180 天下限</b>。配置调长的保留期仍由 EF 层把关：数据库层挡住的是任何人都不许删的那一段，EF 层挡住的是这套部署
/// 自己又加长的那一段。
/// </para>
/// <para>
/// 这里每条测试的时间基准都取<b>真实的现在</b>（<see cref="Now"/>），不是写死的日期：触发器读的就是真实时钟，用伪造时钟
/// 种出来的「200 天前」在真实时钟下未必也是 200 天前。既有那些用 <c>MutableTimeProvider</c> 的治理测试跑在
/// <c>EnsureCreatedAsync</c> 建的库上，没有触发器，不受影响；
/// <see cref="PurgeStillRemovesWhatIsPastTheFloorAndKeepsWhatIsInsideItOnAMigratedDatabase"/> 把它们那个场景在迁移过的库上
/// 又跑了一遍，钉住伪造时钟与触发器不打架。
/// </para>
/// </remarks>
public sealed class AuditDatabaseImmutabilityTests
{
    internal const string MigrationSuffix = "_AuditImmutabilityTriggers";

    private const string MigrationBefore = "20260919200353_AreaEndAdmissionRevokedSince";

    private const string BusinessTable = "BusinessAuditRecords";
    private const string TamperAgain =
        "UPDATE \"BusinessAuditRecords\" SET Action = 'TAMPERED_AGAIN' WHERE Action = 'TAMPERED'";
    private const string AdministratorTable = "AdministratorAuditRecords";

    /// <summary>真实的现在。触发器读数据库机器的时钟，所以种数据也按它来。</summary>
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ======== 迁移纪律 ========

    [Fact]
    public async Task TheTriggersComeInOneMigrationStraightAfterTheAreaEndAdmissionMigration()
    {
        await using MigratedFixture fixture = await MigratedFixture.CreateAsync(migrate: false);
        string[] migrations = [.. fixture.Context.Database.GetMigrations()];

        string migration = Assert.Single(migrations, name => name.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        Assert.Equal(MigrationBefore, migrations[Array.IndexOf(migrations, migration) - 1]);
    }

    [Fact]
    public async Task TheModelSnapshotMatchesTheModel()
    {
        await using MigratedFixture fixture = await MigratedFixture.CreateAsync(migrate: false);

        Assert.False(fixture.Context.Database.HasPendingModelChanges());
    }

    /// <summary>两张审计表各拿到一对触发器，别的表一个都没有。</summary>
    [Fact]
    public async Task BothAuditTablesGetTheirPairOfTriggersAndNoOtherTableGetsOne()
    {
        await using MigratedFixture fixture = await MigratedFixture.CreateAsync();

        Assert.Equal(
            [
                $"TR_{AdministratorTable}_NoDeleteWithinRetentionFloor on {AdministratorTable}",
                $"TR_{AdministratorTable}_NoUpdate on {AdministratorTable}",
                $"TR_{BusinessTable}_NoDeleteWithinRetentionFloor on {BusinessTable}",
                $"TR_{BusinessTable}_NoUpdate on {BusinessTable}",
            ],
            await TriggersAsync(fixture.Connection));
    }

    // ======== 风险一、二：既有行照常可读，新写入照常成功 ========

    /// <summary>
    /// 触发器只管改写与删除。迁移之前写下的审计照常读得出来，迁移之后照常写得进去——两张表都是。
    /// </summary>
    [Fact]
    public async Task AuditWrittenBeforeTheTriggersIsStillReadableAndNewAuditIsStillWritable()
    {
        await using MigratedFixture fixture = await MigratedFixture.CreateAsync(migrate: false);
        await fixture.Context.GetService<IMigrator>().MigrateAsync(MigrationBefore, Token);
        await SeedAsync(fixture, "BEFORE_TRIGGERS");

        await fixture.Context.Database.MigrateAsync(Token);

        await using ControlServerDbContext read = fixture.NewContext();
        Assert.Equal(
            ["BEFORE_TRIGGERS_INSIDE_FLOOR", "BEFORE_TRIGGERS_PAST_FLOOR"],
            await read.Set<BusinessAuditRecordRow>().AsNoTracking()
                .Select(row => row.Action).OrderBy(action => action).ToArrayAsync(Token));
        Assert.Equal(
            ["BEFORE_TRIGGERS_INSIDE_FLOOR", "BEFORE_TRIGGERS_PAST_FLOOR"],
            await read.Set<AdministratorAuditRecordRow>().AsNoTracking()
                .Select(row => row.Action).OrderBy(action => action).ToArrayAsync(Token));

        GovernanceStore store = Store(read);
        string business = await store.WriteBusinessAsync(Entry("AFTER_TRIGGERS"), Now, Token);
        string administrator = await store.WriteAdministratorAsync(Entry("AFTER_TRIGGERS"), Now, Token);

        await using ControlServerDbContext again = fixture.NewContext();
        Assert.Equal(
            "AFTER_TRIGGERS",
            (await again.Set<BusinessAuditRecordRow>().AsNoTracking()
                .SingleAsync(row => row.AuditRecordId == business, Token)).Action);
        Assert.Equal(
            "AFTER_TRIGGERS",
            (await again.Set<AdministratorAuditRecordRow>().AsNoTracking()
                .SingleAsync(row => row.AuditRecordId == administrator, Token)).Action);
    }

    // ======== 风险三：UPDATE／DELETE 被数据库层拒绝 ========

    /// <summary>
    /// 改写一条审计，无论走哪条路都被拒：原始 SQL 的 <c>UPDATE</c>、另开一条连接的 <c>UPDATE</c>、
    /// <c>ExecuteUpdateAsync</c>。三条都不经变更跟踪，EF 层那个守卫一条也拦不到。两张表、保留期内外的行各试一遍——
    /// 更新是<b>一律</b>拒绝，与保留期无关。之后记录逐字不变。
    /// </summary>
    [Fact]
    public async Task NoPathRewritesAnAuditRecordOnAMigratedDatabase()
    {
        await using MigratedFixture fixture = await MigratedFixture.CreateAsync();
        await SeedAsync(fixture, "REWRITE");
        string[] before = await DumpBothTablesAsync(fixture.Connection);

        foreach (string table in (string[])[BusinessTable, AdministratorTable])
        {
            foreach (string action in (string[])["REWRITE_INSIDE_FLOOR", "REWRITE_PAST_FLOOR"])
            {
                string update = $"UPDATE \"{table}\" SET Action = 'TAMPERED' WHERE Action = '{action}'";
                SqliteException raw = await Assert.ThrowsAsync<SqliteException>(() =>
                    fixture.Context.Database.ExecuteSqlRawAsync(update, Token));
                Assert.Contains("write-once", raw.Message, StringComparison.Ordinal);
                Assert.Contains(table, raw.Message, StringComparison.Ordinal);

                // A second connection to the same database: nothing about the guard lives in this process.
                await using SqliteConnection other = new(fixture.ConnectionString);
                await other.OpenAsync(Token);
                await using SqliteCommand command = other.CreateCommand();
                command.CommandText = $"UPDATE \"{table}\" SET DetailJson = '{{}}' WHERE Action = '{action}'";
                Assert.Contains(
                    "write-once",
                    (await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync(Token))).Message,
                    StringComparison.Ordinal);
            }
        }

        await using ControlServerDbContext bulk = fixture.NewContext();
        Assert.Contains(
            "write-once",
            (await Assert.ThrowsAsync<SqliteException>(() => bulk.Set<BusinessAuditRecordRow>()
                .Where(row => row.Action == "REWRITE_INSIDE_FLOOR")
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Action, "TAMPERED"), Token))).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "write-once",
            (await Assert.ThrowsAsync<SqliteException>(() => bulk.Set<AdministratorAuditRecordRow>()
                .Where(row => row.Action == "REWRITE_PAST_FLOOR")
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.Outcome, GovernanceActionOutcome.Failed), Token)))
                .Message,
            StringComparison.Ordinal);

        Assert.Equal(before, await DumpBothTablesAsync(fixture.Connection));
    }

    /// <summary>
    /// 删除一条保留期内的审计，三条路一样被拒；过了 180 天下限的那条则放行——清理要照常工作，见
    /// <see cref="PurgeStillRemovesWhatIsPastTheFloorAndKeepsWhatIsInsideItOnAMigratedDatabase"/>。
    /// </summary>
    [Fact]
    public async Task NoPathDeletesAnAuditRecordInsideTheRetentionFloorOnAMigratedDatabase()
    {
        await using MigratedFixture fixture = await MigratedFixture.CreateAsync();
        await SeedAsync(fixture, "DELETE");
        string[] before = await DumpBothTablesAsync(fixture.Connection);

        foreach (string table in (string[])[BusinessTable, AdministratorTable])
        {
            string delete = $"DELETE FROM \"{table}\" WHERE Action = 'DELETE_INSIDE_FLOOR'";
            SqliteException raw = await Assert.ThrowsAsync<SqliteException>(() =>
                fixture.Context.Database.ExecuteSqlRawAsync(delete, Token));
            Assert.Contains("180", raw.Message, StringComparison.Ordinal);
            Assert.Contains(table, raw.Message, StringComparison.Ordinal);

            await using SqliteConnection other = new(fixture.ConnectionString);
            await other.OpenAsync(Token);
            await using SqliteCommand command = other.CreateCommand();
            command.CommandText = $"DELETE FROM \"{table}\" WHERE Action = 'DELETE_INSIDE_FLOOR'";
            Assert.Contains(
                "180",
                (await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync(Token))).Message,
                StringComparison.Ordinal);
        }

        await using ControlServerDbContext bulk = fixture.NewContext();
        Assert.Contains(
            "180",
            (await Assert.ThrowsAsync<SqliteException>(() => bulk.Set<BusinessAuditRecordRow>()
                .Where(row => row.Action == "DELETE_INSIDE_FLOOR").ExecuteDeleteAsync(Token))).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "180",
            (await Assert.ThrowsAsync<SqliteException>(() => bulk.Set<AdministratorAuditRecordRow>()
                .Where(row => row.Action == "DELETE_INSIDE_FLOOR").ExecuteDeleteAsync(Token))).Message,
            StringComparison.Ordinal);

        Assert.Equal(before, await DumpBothTablesAsync(fixture.Connection));

        // Past the floor: the database lets it go, which is what keeps the purge working.
        await using ControlServerDbContext expired = fixture.NewContext();
        Assert.Equal(
            1,
            await expired.Set<BusinessAuditRecordRow>().Where(row => row.Action == "DELETE_PAST_FLOOR")
                .ExecuteDeleteAsync(Token));
    }

    // ======== 风险三续：保留期清理照常工作 ========

    /// <summary>
    /// <c>GovernanceSnapshotAndAuditTests.RetentionDefaultsTo180DaysAndPurgesOnlyWhatIsPastIt</c> 那个场景，搬到迁移过的
    /// 库上跑一遍：EF 层按<b>伪造的</b>时钟算截止，触发器按<b>真实</b>时钟守下限，两者不打架——过了下限的删得掉，
    /// 保留期内的删不掉。
    /// </summary>
    [Fact]
    public async Task PurgeStillRemovesWhatIsPastTheFloorAndKeepsWhatIsInsideItOnAMigratedDatabase()
    {
        await using MigratedFixture fixture = await MigratedFixture.CreateAsync();
        await SeedAsync(fixture, "PURGE");
        ControlServerDbContext context = fixture.NewContext();
        // The fake clock the EF-layer guard reads. It is 190 days behind the real one, so what it calls "inside
        // retention" and what the trigger calls "inside the floor" are deliberately different questions.
        context.AuditClock = new FixedClock(Now.AddDays(-190));
        GovernanceStore store = Store(context);

        // Nothing is past retention on the fake clock, so the purge selects nothing and issues no DELETE.
        Assert.Equal(0, await store.PurgeExpiredAuditAsync(Now.AddDays(-190), Token));
        Assert.Equal(4, await CountAsync(fixture.Connection));

        // On the real clock the two 200-day-old records are past both the floor and retention.
        context.AuditClock = new FixedClock(Now);
        Assert.Equal(2, await store.PurgeExpiredAuditAsync(Now, Token));

        await using ControlServerDbContext read = fixture.NewContext();
        Assert.Equal(
            ["PURGE_INSIDE_FLOOR"],
            await read.Set<BusinessAuditRecordRow>().AsNoTracking().Select(row => row.Action).ToArrayAsync(Token));
        Assert.Equal(
            ["PURGE_INSIDE_FLOOR"],
            await read.Set<AdministratorAuditRecordRow>().AsNoTracking().Select(row => row.Action).ToArrayAsync(Token));

        // And the EF-layer guard still refuses the one inside retention, before the trigger ever sees it.
        BusinessAuditRecordRow keep = await context.Set<BusinessAuditRecordRow>().SingleAsync(Token);
        context.Set<BusinessAuditRecordRow>().Remove(keep);
        await Assert.ThrowsAsync<AuditRecordImmutabilityException>(() => context.SaveChangesAsync(Token));
        context.ChangeTracker.Clear();
        await context.DisposeAsync();
    }

    // ======== 风险四：审计写失败不留残留（control-server#200 的 SaveOrUntrackAsync 仍成立） ========

    /// <summary>
    /// control-server#200 d 的行为在迁移过的库上没变：一次被数据库拒绝的审计写入，只把它自己那一行从跟踪里摘掉，
    /// 调用方随后在<b>同一个上下文</b>上写的下一条照常落库，被拒的那条不会被补交进去。
    /// </summary>
    /// <remarks>
    /// 这里用本票的 <c>BEFORE UPDATE</c> 触发器造那次失败，而不是像 control-server#200 的测试那样临时加一个
    /// <c>BEFORE INSERT</c> 触发器：跟踪一条读回来的审计行、改它一个字段，EF 层守卫在 <c>SaveChanges</c> 前就抛
    /// <see cref="AuditRecordImmutabilityException"/>，根本到不了数据库——所以改用一条不经变更跟踪、由本票触发器拒绝的
    /// 原始 SQL 放在同一个事务里，再让审计写入器在这个事务上保存。
    /// </remarks>
    [Fact]
    public async Task AnAuditTheDatabaseRefusesLeavesNothingTrackedBehindOnAMigratedDatabase()
    {
        await using MigratedFixture fixture = await MigratedFixture.CreateAsync();
        await SeedAsync(fixture, "UNTRACK");
        ControlServerDbContext context = fixture.NewContext();
        GovernanceStore store = Store(context);
        // A trigger of this test's own: the ticket's triggers refuse UPDATE and DELETE, and what has to fail here is an
        // INSERT -- the audit write itself.
        await fixture.ExecuteAsync(
            """
            CREATE TRIGGER cs199_refuse BEFORE INSERT ON BusinessAuditRecords
            WHEN NEW.Action = 'CS199_REFUSED' BEGIN SELECT RAISE(ABORT, 'cs199 refused'); END;
            """);

        // SaveChanges wraps what the provider threw; the raw-SQL and Execute* paths below do not.
        DbUpdateException refused = await Assert.ThrowsAsync<DbUpdateException>(() =>
            store.WriteBusinessAsync(Entry("CS199_REFUSED"), Now, Token));
        Assert.IsType<SqliteException>(refused.InnerException);
        Assert.DoesNotContain(
            context.ChangeTracker.Entries<BusinessAuditRecordRow>(), entry => entry.State == EntityState.Added);
        string next = await store.WriteBusinessAsync(Entry("CS199_NEXT"), Now, Token);

        await using ControlServerDbContext read = fixture.NewContext();
        Assert.Equal(
            [next + "|CS199_NEXT"],
            await read.Set<BusinessAuditRecordRow>().AsNoTracking()
                .Where(row => row.Action.StartsWith("CS199_"))
                .Select(row => row.AuditRecordId + "|" + row.Action).ToArrayAsync(Token));
        await context.DisposeAsync();
    }

    // ======== Down()：迁下去触发器消失，迁回来触发器回来 ========

    /// <summary>
    /// 迁到本票迁移的上一版，四个触发器全没了，原始 SQL 改得动审计；再迁上来，触发器一字不差地回来，同一条原始 SQL
    /// 又被拒。<c>Down()</c> 只删这四个触发器：整个库的定义与每一行数据在这一趟来回里逐字不变（改写那一条除外，
    /// 它是故意写进去的证据）。
    /// </summary>
    [Fact]
    public async Task MigratingDownDropsTheTriggersAndMigratingUpAgainBringsThemBack()
    {
        await using MigratedFixture fixture = await MigratedFixture.CreateAsync(migrate: false);
        await fixture.Context.GetService<IMigrator>().MigrateAsync(MigrationBefore, Token);
        await SeedAsync(fixture, "ROUND_TRIP");
        await fixture.Context.Database.MigrateAsync(Token);
        string[] schemaAtTip = await ReadSchemaAsync(fixture.Connection);
        Assert.Equal(4, (await TriggersAsync(fixture.Connection)).Length);

        await fixture.Context.GetService<IMigrator>().MigrateAsync(MigrationBefore, Token);

        Assert.Empty(await TriggersAsync(fixture.Connection));
        // Without the triggers the raw UPDATE goes through -- this is exactly the hole the ticket closes.
        string tamper = $"UPDATE \"{BusinessTable}\" SET Action = 'TAMPERED' WHERE Action = 'ROUND_TRIP_INSIDE_FLOOR'";
        Assert.Equal(1, await fixture.Context.Database.ExecuteSqlRawAsync(tamper, Token));
        string[] rowsWhileDown = await DumpBothTablesAsync(fixture.Connection);

        await fixture.Context.Database.MigrateAsync(Token);

        Assert.Equal(schemaAtTip, await ReadSchemaAsync(fixture.Connection));
        Assert.Equal(rowsWhileDown, await DumpBothTablesAsync(fixture.Connection));
        Assert.Contains(
            "write-once",
            (await Assert.ThrowsAsync<SqliteException>(() => fixture.Context.Database.ExecuteSqlRawAsync(
                TamperAgain, Token))).Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 在直接迁到上一版的库上用原始 SQL 种数据、再迁到最新：本票的迁移只加触发器，既有的每一张表、每一个索引、每一行
    /// 逐字不变。基线不是 <c>Down()</c> 退回来的库——那样只证明 <c>Down()</c> 与 <c>Up()</c> 互逆，证不了这个迁移对着
    /// 一个从来没见过它的生产库做了什么。
    /// </summary>
    [Fact]
    public async Task TheMigrationAddsNothingButTriggersToADatabaseThatHasNeverSeenIt()
    {
        await using MigratedFixture fixture = await MigratedFixture.CreateAsync(migrate: false);
        await fixture.Context.GetService<IMigrator>().MigrateAsync(MigrationBefore, Token);
        await SeedAsync(fixture, "UNTOUCHED");
        string[] schemaBefore = await ReadSchemaAsync(fixture.Connection);
        string[] rowsBefore = await DumpBothTablesAsync(fixture.Connection);
        Assert.Empty(await TriggersAsync(fixture.Connection));

        await fixture.Context.Database.MigrateAsync(Token);

        Assert.Equal(rowsBefore, await DumpBothTablesAsync(fixture.Connection));
        Assert.Equal(
            [
                $"trigger TR_{AdministratorTable}_NoDeleteWithinRetentionFloor",
                $"trigger TR_{AdministratorTable}_NoUpdate",
                $"trigger TR_{BusinessTable}_NoDeleteWithinRetentionFloor",
                $"trigger TR_{BusinessTable}_NoUpdate",
            ],
            [.. (await ReadSchemaAsync(fixture.Connection)).Except(schemaBefore, StringComparer.Ordinal)
                .Select(line => line[..line.IndexOf(':', StringComparison.Ordinal)])]);
    }

    // ======== helpers ========

    /// <summary>
    /// 四条审计：两张表各一条保留期内（10 天前）、一条早于 180 天下限（200 天前）。时间按真实时钟算，触发器读的就是它。
    /// </summary>
    /// <remarks>
    /// 用原始 SQL 写，不走 <see cref="GovernanceStore"/>：这些行代表「库在本票迁移之前就有的审计」，它们是哪个版本的写入器
    /// 落下的已经无从追溯，拿今天的写入器去种就等于假设它从没变过。列名与取值照着批次 3 那个迁移建的两张表。
    /// </remarks>
    private static async Task SeedAsync(MigratedFixture fixture, string prefix)
    {
        foreach ((string suffix, int daysAgo) in (ValueTuple<string, int>[])[("_INSIDE_FLOOR", 10), ("_PAST_FLOOR", 200)])
        {
            DateTimeOffset recordedAt = Now.AddDays(-daysAgo);
            await InsertAsync(fixture, BusinessTable, prefix + suffix, recordedAt, administratorRole: null);
            await InsertAsync(fixture, AdministratorTable, prefix + suffix, recordedAt, "fieldops");
        }
    }

    private static async Task InsertAsync(
        MigratedFixture fixture, string table, string action, DateTimeOffset recordedAt, string? administratorRole)
    {
        bool administrator = table == AdministratorTable;
        await using SqliteCommand insert = fixture.Connection.CreateCommand();
        insert.CommandText =
            $"""
             INSERT INTO "{table}"
                 (AuditRecordId, RecordedAt, RecordedAtUtcTicks, ActorIdentity, ActorAttribution,
                  {(administrator ? "ClaimedAdministratorRole, " : "")}Action, ObjectKind, ObjectId, Version, Outcome,
                  SnapshotId, DetailJson)
             VALUES
                 ($id, $recordedAt, $ticks, 'deployment:8005-controlserver@test', 'NOT_ATTRIBUTABLE_TO_NATURAL_PERSON',
                  {(administrator ? "$role, " : "")}$action, 'SlotTemplate', 'slot-template:test', 1, 'Succeeded',
                  NULL, $detail)
             """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$recordedAt", recordedAt.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$ticks", recordedAt.UtcTicks);
        insert.Parameters.AddWithValue("$action", action);
        insert.Parameters.AddWithValue("$detail", DetailJson);
        if (administrator)
        {
            insert.Parameters.AddWithValue("$role", administratorRole ?? (object)DBNull.Value);
        }
        await insert.ExecuteNonQueryAsync(Token);
    }

    private static GovernanceStore Store(ControlServerDbContext context) =>
        new(context, new GovernanceDeploymentIdentity("deployment:8005-controlserver@test"), AuditRetentionPolicy.Default);

    private const string DetailJson = """{"reason":"cs199"}""";

    private static GovernanceAuditEntry Entry(string action) =>
        new(
            action,
            GovernedObjectKind.SlotTemplate,
            "slot-template:test",
            1,
            GovernanceActionOutcome.Succeeded,
            DetailJson);

    private static async Task<int> CountAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT (SELECT count(*) FROM \"{BusinessTable}\") + (SELECT count(*) FROM \"{AdministratorTable}\")";
        return Convert.ToInt32(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);
    }

    /// <summary>每个触发器与它挂的表，名字排序。</summary>
    private static async Task<string[]> TriggersAsync(SqliteConnection connection)
    {
        List<string> triggers = [];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT name || ' on ' || tbl_name FROM sqlite_master WHERE type = 'trigger' ORDER BY name";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            triggers.Add(reader.GetString(0));
        }
        return [.. triggers];
    }

    /// <summary>两张审计表的每一行，每列用 SQLite 自己的字面量写出来，排序。</summary>
    private static async Task<string[]> DumpBothTablesAsync(SqliteConnection connection)
    {
        List<string> rows = [];
        foreach (string table in (string[])[AdministratorTable, BusinessTable])
        {
            List<string> columns = [];
            await using (SqliteCommand info = connection.CreateCommand())
            {
                info.CommandText = $"PRAGMA table_info('{table}')";
                await using SqliteDataReader reader = await info.ExecuteReaderAsync(Token);
                while (await reader.ReadAsync(Token))
                {
                    columns.Add(reader.GetString(1));
                }
            }
            columns.Sort(StringComparer.Ordinal);

            await using SqliteCommand select = connection.CreateCommand();
            select.CommandText =
                $"SELECT '{table}|' || {string.Join(" || '|' || ", columns.Select(name => $"'{name}=' || quote(\"{name}\")"))} "
                + $"FROM \"{table}\"";
            await using SqliteDataReader rowReader = await select.ExecuteReaderAsync(Token);
            while (await rowReader.ReadAsync(Token))
            {
                rows.Add(rowReader.GetString(0));
            }
        }
        rows.Sort(StringComparer.Ordinal);
        return [.. rows];
    }

    /// <summary>每张表的每一列（名、类型、可空、默认值、主键位），以及每个索引与触发器的定义原文。</summary>
    private static async Task<string[]> ReadSchemaAsync(SqliteConnection connection)
    {
        List<string> schema = [];
        List<string> tables = [];
        await using (SqliteCommand names = connection.CreateCommand())
        {
            names.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
            await using SqliteDataReader reader = await names.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                tables.Add(reader.GetString(0));
            }
        }
        foreach (string table in tables)
        {
            await using SqliteCommand columns = connection.CreateCommand();
            columns.CommandText =
                $"SELECT name || ' ' || type || ' notnull=' || \"notnull\" || ' default=' || coalesce(dflt_value, '-') "
                + $"|| ' pk=' || pk FROM pragma_table_info('{table}')";
            await using SqliteDataReader reader = await columns.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                schema.Add($"column {table}.{reader.GetString(0)}");
            }
        }
        await using SqliteCommand definitions = connection.CreateCommand();
        definitions.CommandText =
            "SELECT type || ' ' || name || ': ' || coalesce(sql, '') FROM sqlite_master "
            + "WHERE type IN ('index', 'trigger') AND name NOT LIKE 'sqlite_%'";
        await using SqliteDataReader reader2 = await definitions.ExecuteReaderAsync(Token);
        while (await reader2.ReadAsync(Token))
        {
            schema.Add(reader2.GetString(0));
        }
        schema.Sort(StringComparer.Ordinal);
        return [.. schema];
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// 一个跑过 <c>MigrateAsync</c> 的库——不是 <c>EnsureCreatedAsync</c>：触发器只在迁移里建，用后者建的库一个都没有。
    /// 连接串带一个每次不同的库名，这样第二条连接能连到同一个内存库，验证守卫不在进程里。
    /// </summary>
    private sealed class MigratedFixture : IAsyncDisposable
    {
        private MigratedFixture(string connectionString, SqliteConnection connection, ControlServerDbContext context)
        {
            ConnectionString = connectionString;
            Connection = connection;
            Context = context;
        }

        public string ConnectionString { get; }
        public SqliteConnection Connection { get; }
        public ControlServerDbContext Context { get; }

        public static async Task<MigratedFixture> CreateAsync(bool migrate = true)
        {
            string connectionString =
                $"Data Source=cs199-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            SqliteConnection connection = new(connectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            MigratedFixture fixture = new(connectionString, connection, NewContextOn(connectionString));
            if (migrate)
            {
                await fixture.Context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            }
            return fixture;
        }

        public ControlServerDbContext NewContext() => NewContextOn(ConnectionString);

        public async Task ExecuteAsync(string sql)
        {
            await using SqliteCommand command = Connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        private static ControlServerDbContext NewContextOn(string connectionString) =>
            new(new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connectionString).Options);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
