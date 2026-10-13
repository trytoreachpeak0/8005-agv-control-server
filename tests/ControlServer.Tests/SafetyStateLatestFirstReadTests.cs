using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 安全状态消息按收件时间从新到旧读、遇到即停（control-server#597）：挑中的那一行必须与改动前「整类读回、内存里稳定排序」挑中的一样。
/// </summary>
/// <remarks>
/// <para>
/// 这里的用例先在改动前的代码上跑绿，钉住今天的挑选结果，改完仍要绿。所以没有「先红」，红证据靠变异取。
/// </para>
/// <para>
/// <b>同一收件时间（同一个 tick）多行时今天挑哪一行。</b>派车事实（<c>LatestSafetySummaryForSessionAsync</c>）把按类型读回的行按收件时间
/// 稳定地倒排，同 tick 的行保持库返回的次序；库走 <c>IX_ProtocolInbox_MessageType</c>，所以同 tick 时 <c>SafetyStateChanged</c> 在
/// <c>SafetyStateSnapshot</c> 前、同类先写入的在前。在途门锁（<see cref="InTransitDoorFacts"/>）不按收件时间排，取库返回次序里第一条匹配的。
/// 生产写入路径存不下两行同车同代同版本号（版本相同而整行哈希不同按 <c>SNAPSHOT_REVISION_CONTENT_CONFLICT</c> 拒收，拒收的消息不入库），
/// 所以下面那几条重复版本的行是直接插库造的：只有这样，挑中哪一行才看得出来。
/// </para>
/// </remarks>
public sealed class SafetyStateLatestFirstReadTests
{
    private const string AgvId = "AGV-01";
    private const string OtherAgvId = "AGV-02";
    private const long Generation = 7;
    private const long Revision = 5;
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(8));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- 派车事实：当前安全摘要 ---------------------------------------------------------------------------------

    /// <summary>
    /// 同一收件时间两行都带会话行的代次与版本号：今天挑 <c>SafetyStateChanged</c>，不论谁先写入。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnTheSameTickTheDispatchSummaryTakesTheChangeOverTheSnapshot(bool snapshotWrittenFirst)
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId);
        DateTimeOffset at = Now - TimeSpan.FromMinutes(1);
        ProtocolInboxRow snapshot = Safety("SafetyStateSnapshot", AgvId, Generation, Revision, at, stopped: false, locked: false);
        ProtocolInboxRow change = Safety("SafetyStateChanged", AgvId, Generation, Revision, at, stopped: true, locked: true);
        await database.AddInOrderAsync(snapshotWrittenFirst ? [snapshot, change] : [change, snapshot]);

        OnboardDispatchFacts? facts = await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token);

        Assert.Equal((true, true), (facts?.VehicleStopped, facts?.AllTargetSlotsLocked));
    }

    /// <summary>同一收件时间、同一类的两行都匹配：今天挑先写入的那一行。</summary>
    [Theory]
    [InlineData("SafetyStateChanged")]
    [InlineData("SafetyStateSnapshot")]
    public async Task OnTheSameTickTheDispatchSummaryTakesTheRowOfTheSameTypeWrittenFirst(string messageType)
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId);
        DateTimeOffset at = Now - TimeSpan.FromMinutes(1);
        await database.AddInOrderAsync(
            Safety(messageType, AgvId, Generation, Revision, at, stopped: true, locked: true),
            Safety(messageType, AgvId, Generation, Revision, at, stopped: false, locked: false));

        OnboardDispatchFacts? facts = await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token);

        Assert.Equal((true, true), (facts?.VehicleStopped, facts?.AllTargetSlotsLocked));
    }

    /// <summary>两行都匹配、收件时间不同：今天挑收得晚的那一行，即便它先写入（rowid 小）。</summary>
    [Fact]
    public async Task TheDispatchSummaryTakesTheLaterReceivedOfTwoMatchingRowsWhicheverWasWrittenFirst()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId);
        await database.AddInOrderAsync(
            Safety("SafetyStateChanged", AgvId, Generation, Revision, Now - TimeSpan.FromMinutes(1), stopped: true, locked: true),
            Safety("SafetyStateChanged", AgvId, Generation, Revision, Now - TimeSpan.FromMinutes(2), stopped: false, locked: false));

        OnboardDispatchFacts? facts = await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token);

        Assert.Equal((true, true), (facts?.VehicleStopped, facts?.AllTargetSlotsLocked));
    }

    /// <summary>
    /// 跨代与不同车交错：比这一行收得更晚的，有上一代同版本号的、有另一台车同代同版本号的、有本车本代别的版本号的，都不算；
    /// 挑中的是本车、会话行那一代、会话行那一版。
    /// </summary>
    [Fact]
    public async Task TheDispatchSummaryIgnoresNewerRowsOfAnotherGenerationAnotherVehicleOrAnotherVersion()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId);
        await database.AddInOrderAsync(
            Safety("SafetyStateChanged", AgvId, Generation, Revision, Now - TimeSpan.FromMinutes(10), stopped: true, locked: true),
            Safety("SafetyStateChanged", AgvId, Generation - 1, Revision, Now - TimeSpan.FromMinutes(9), stopped: false, locked: false),
            Safety("SafetyStateChanged", OtherAgvId, Generation, Revision, Now - TimeSpan.FromMinutes(8), stopped: false, locked: false),
            Safety("SafetyStateChanged", AgvId, Generation, Revision - 1, Now - TimeSpan.FromMinutes(7), stopped: false, locked: false),
            Safety("SafetyStateSnapshot", AgvId, Generation + 1, Revision, Now - TimeSpan.FromMinutes(6), stopped: false, locked: false),
            Safety("SafetyStateChanged", OtherAgvId, Generation, Revision + 1, Now - TimeSpan.FromMinutes(5), stopped: false, locked: false));

        OnboardDispatchFacts? facts = await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token);

        Assert.Equal((Generation, true, true), (facts?.SessionGeneration, facts?.VehicleStopped, facts?.AllTargetSlotsLocked));
    }

    /// <summary>
    /// 窗口外仍是最新：车停了三天没变过安全状态，它唯一那一条当前摘要收于三天前，此后另一台车发了几百条；照样读得到，
    /// 不因为任何窗口把它漏掉。
    /// </summary>
    [Fact]
    public async Task TheDispatchSummaryIsFoundHoweverOldItIs()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId);
        List<ProtocolInboxRow> rows =
        [
            Safety("SafetyStateChanged", AgvId, Generation, Revision, Now - TimeSpan.FromDays(3), stopped: true, locked: true),
        ];
        for (int change = 0; change < 300; change++)
        {
            rows.Add(Safety("SafetyStateChanged", OtherAgvId, Generation, 10 + change,
                Now - TimeSpan.FromDays(3) + TimeSpan.FromMinutes(10 * (change + 1)), stopped: false, locked: false));
        }
        await database.AddInOrderAsync([.. rows]);

        OnboardDispatchFacts? facts = await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token);

        Assert.Equal((true, true), (facts?.VehicleStopped, facts?.AllTargetSlotsLocked));
    }

    /// <summary>没有哪一行带会话行的版本号：会话行与收件箱对不上，失败关闭，与今天一样。</summary>
    [Fact]
    public async Task TheDispatchFactsFailClosedWhenNoRowCarriesTheSessionRevision()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId);
        await database.AddInOrderAsync(
            Safety("SafetyStateChanged", AgvId, Generation, Revision - 1, Now - TimeSpan.FromMinutes(1), stopped: true, locked: true));

        Assert.Null(await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token));
    }

    // ---- 在途门锁 ---------------------------------------------------------------------------------------------

    /// <summary>同一收件时间两行都带会话行的代次与版本号：今天挑 <c>SafetyStateChanged</c>，不论谁先写入。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnTheSameTickTheDoorFactTakesTheChangeOverTheSnapshot(bool snapshotWrittenFirst)
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedSessionAsync(database.Context, AgvId, Revision);
        DateTimeOffset at = Now - TimeSpan.FromMinutes(1);
        ProtocolInboxRow snapshot = Safety("SafetyStateSnapshot", AgvId, Generation, Revision, at, stopped: true, locked: false);
        ProtocolInboxRow change = Safety("SafetyStateChanged", AgvId, Generation, Revision, at, stopped: true, locked: true);
        await database.AddInOrderAsync(snapshotWrittenFirst ? [snapshot, change] : [change, snapshot]);

        InTransitDoorVerdict verdict = await InTransitDoorFacts.ReadAsync(database.Context, AgvId, Now, Token);

        Assert.Equal(new InTransitDoorVerdict(InTransitDoorState.ProvenLocked, Generation, Revision), verdict);
    }

    /// <summary>同一收件时间、同一类的两行都匹配：今天挑先写入的那一行。</summary>
    [Theory]
    [InlineData("SafetyStateChanged")]
    [InlineData("SafetyStateSnapshot")]
    public async Task OnTheSameTickTheDoorFactTakesTheRowOfTheSameTypeWrittenFirst(string messageType)
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedSessionAsync(database.Context, AgvId, Revision);
        DateTimeOffset at = Now - TimeSpan.FromMinutes(1);
        await database.AddInOrderAsync(
            Safety(messageType, AgvId, Generation, Revision, at, stopped: true, locked: true),
            Safety(messageType, AgvId, Generation, Revision, at, stopped: true, locked: false));

        InTransitDoorVerdict verdict = await InTransitDoorFacts.ReadAsync(database.Context, AgvId, Now, Token);

        Assert.Equal(InTransitDoorState.ProvenLocked, verdict.State);
    }

    /// <summary>跨代与不同车交错、唯一那一条当前摘要收于三天前：挑中的仍是本车、会话行那一代、那一版。</summary>
    [Fact]
    public async Task TheDoorFactIgnoresNewerRowsOfAnotherGenerationOrVehicleAndIsFoundHoweverOldItIs()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedSessionAsync(database.Context, AgvId, Revision);
        List<ProtocolInboxRow> rows =
        [
            Safety("SafetyStateChanged", AgvId, Generation, Revision, Now - TimeSpan.FromDays(3), stopped: true, locked: true),
            Safety("SafetyStateChanged", AgvId, Generation - 1, Revision, Now - TimeSpan.FromDays(2), stopped: true, locked: false),
            Safety("SafetyStateChanged", OtherAgvId, Generation, Revision, Now - TimeSpan.FromDays(1), stopped: true, locked: false),
        ];
        for (int change = 0; change < 300; change++)
        {
            rows.Add(Safety("SafetyStateChanged", OtherAgvId, Generation, 10 + change,
                Now - TimeSpan.FromHours(20) + TimeSpan.FromMinutes(change), stopped: true, locked: false));
        }
        await database.AddInOrderAsync([.. rows]);

        InTransitDoorVerdict verdict = await InTransitDoorFacts.ReadAsync(database.Context, AgvId, Now, Token);

        Assert.Equal(new InTransitDoorVerdict(InTransitDoorState.ProvenLocked, Generation, Revision), verdict);
    }

    /// <summary>
    /// 重连后新快照还没到（会话行版本号为空）：取上一代按代次、再按版本号最新的那一条，不看收件时间——服务端时钟回拨过，版本号大的
    /// 那条收件时间反而早。本票不改这一段的挑法。
    /// </summary>
    [Fact]
    public async Task BetweenAReconnectAndItsSnapshotTheDoorFactIsThePreviousGenerationsHighestVersionNotItsLatestReceived()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedSessionAsync(database.Context, AgvId, revision: null);
        await database.AddInOrderAsync(
            Safety("SafetyStateChanged", AgvId, Generation - 1, 9, Now - TimeSpan.FromMinutes(3), stopped: true, locked: true),
            Safety("SafetyStateChanged", AgvId, Generation - 1, 8, Now - TimeSpan.FromMinutes(1), stopped: true, locked: false),
            Safety("SafetyStateChanged", AgvId, Generation - 2, 20, Now - TimeSpan.FromSeconds(30), stopped: true, locked: false),
            Safety("SafetyStateChanged", OtherAgvId, Generation - 1, 30, Now - TimeSpan.FromSeconds(20), stopped: true, locked: false));
        database.Context.ProtocolInbox.Add(HeardFrom(AgvId, Generation - 1, Now - TimeSpan.FromSeconds(1)));
        await database.Context.SaveChangesAsync(Token);

        InTransitDoorVerdict verdict = await InTransitDoorFacts.ReadAsync(database.Context, AgvId, Now, Token);

        Assert.Equal(new InTransitDoorVerdict(InTransitDoorState.ProvenLocked, Generation - 1, 9), verdict);
    }

    // ---- 索引与迁移 -------------------------------------------------------------------------------------------

    /// <summary>
    /// 从新到旧的读法在迁移建出的库上真能边读边停：两类各一臂合并（<c>MERGE (UNION ALL)</c>），每臂走复合索引，只在同 tick 的行之间排序，
    /// 不在吐第一行之前把整类排一遍（<c>USE TEMP B-TREE FOR ORDER BY</c>）。删了复合索引，这一条就红——那时读法仍然对，只是又随条数线性了。
    /// </summary>
    /// <remarks>
    /// 断的是计划文本，计划文本由 SQLite 版本决定（随 <c>SQLitePCLRaw</c> 包钉住）。升级那个包时这一条可能因措辞变化而红：先看新计划
    /// 还是不是两臂各走复合索引、合并输出，再改断言（审查 L1）。
    /// </remarks>
    [Fact]
    public async Task TheNewestFirstReadWalksTheCompositeIndexWithoutSortingAWholeType()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateMigratedForRealAsync();
        await using SqliteCommand command = fixture.Connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + SafetyMessagesNewestFirst.Sql;
        List<string> plan = [];
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                plan.Add(reader.GetString(3));
            }
        }

        Assert.Contains("MERGE (UNION ALL)", plan);
        Assert.Equal(2, plan.Count(step => step.Contains("USING INDEX IX_ProtocolInbox_MessageType_ReceivedAtUtcTicks", StringComparison.Ordinal)));
        Assert.DoesNotContain(plan, step => step.Contains("TEMP B-TREE FOR ORDER BY", StringComparison.Ordinal));
    }

    /// <summary>
    /// 迁移走一个来回：退回上一个迁移只删复合索引，#563 的两个单列索引与每一行原样都在；再升上来复合索引回来，读法挑中的行不变。
    /// </summary>
    [Fact]
    public async Task MigratingDownDropsOnlyTheCompositeIndexAndMigratingUpBringsItBack()
    {
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateMigratedForRealAsync();
        SessionRecoveryRow session = ProtocolInboxReadWindowTests.Session(AgvId, Generation);
        session.SafetyRevision = Revision;
        fixture.Context.SessionRecoveries.Add(session);
        fixture.Context.ProtocolInbox.AddRange(
            HeardFrom(AgvId, Generation, Now - TimeSpan.FromSeconds(1)),
            Safety("SafetyStateChanged", AgvId, Generation, Revision, Now - TimeSpan.FromMinutes(1), stopped: true, locked: true),
            Safety("SafetyStateChanged", AgvId, Generation, Revision - 1, Now - TimeSpan.FromMinutes(2), stopped: true, locked: false));
        await fixture.Context.SaveChangesAsync(Token);
        string[] rowsBefore = await QueryAsync(fixture, """SELECT "MessageId" || '|' || "ReceivedAtUtcTicks" FROM "ProtocolInbox" ORDER BY "MessageId" """);

        await fixture.Context.GetService<IMigrator>().MigrateAsync("20261011005905_ProtocolInboxReadIndexes", Token);
        string[] indexesDown = await ProtocolInboxIndexesAsync(fixture);
        string[] rowsDown = await QueryAsync(fixture, """SELECT "MessageId" || '|' || "ReceivedAtUtcTicks" FROM "ProtocolInbox" ORDER BY "MessageId" """);
        await fixture.Context.Database.MigrateAsync(Token);
        await fixture.RenewContextAsync();
        string[] indexesUp = await ProtocolInboxIndexesAsync(fixture);
        InTransitDoorVerdict verdict = await InTransitDoorFacts.ReadAsync(fixture.Context, AgvId, Now, Token);

        Assert.Equal(["IX_ProtocolInbox_MessageType", "IX_ProtocolInbox_ReceivedAtUtcTicks"], indexesDown);
        Assert.Equal(rowsBefore, rowsDown);
        Assert.Equal(
            ["IX_ProtocolInbox_MessageType", "IX_ProtocolInbox_MessageType_ReceivedAtUtcTicks", "IX_ProtocolInbox_ReceivedAtUtcTicks"],
            indexesUp);
        Assert.Equal(new InTransitDoorVerdict(InTransitDoorState.ProvenLocked, Generation, Revision), verdict);
    }

    private static async Task<string[]> ProtocolInboxIndexesAsync(Batch7JourneyFixture fixture) =>
        [.. (await QueryAsync(fixture, """SELECT name FROM pragma_index_list('ProtocolInbox') ORDER BY name"""))
            .Where(name => name.StartsWith("IX_ProtocolInbox_", StringComparison.Ordinal))];

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

    // ---- 量测 -------------------------------------------------------------------------------------------------

    /// <summary>
    /// 量测（不进默认运行，<c>Explicit</c>）：三台车各连续在线 <c>CS597_MEASURE_DAYS</c> 天（缺省 3），每台每
    /// <c>CS597_MEASURE_CHANGE_SECONDS</c> 秒（缺省 600，#563 的造数）一条 <c>SafetyStateChanged</c> 的收件箱上，四处读安全状态的
    /// 热路径每次从收件箱读回几行、用多久。结果写进 <c>CS597_MEASURE_OUT</c>；库放在 <c>CS597_MEASURE_DIR</c>（缺省临时目录）。
    /// 改动前后在同一台机器上各跑一次，答案逐条相同才算行为不变。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 造数同 #563（<see cref="ProtocolInboxReadWindowTests.MeasureHotPathInboxReads"/>）：心跳每两秒一条，每车每天 50 条
    /// <c>SublotSubmitted</c>，会话开头一条能力快照、一条安全快照，此后按间隔发安全变化；三台车错开几秒。另加 AGV-01 十分钟前一条
    /// 期待动作超时告警（3 号仓），以及它为此回的一份会话中途安全快照（占那一刻的版本号），看板那一处才有东西可读。
    /// </para>
    /// <para>
    /// 真车频率的依据：agv01 真车、真 RIoT 的端到端闭环（<c>evidence/g3/20260829-closed-loop-gen3/SUMMARY.md</c>）全程 1 分 58 秒，
    /// 车发了 17 条 <c>SafetyStateChanged</c>，约 7 秒一条。拿 7 秒当全天的频率是上界：车不会全天在作业。
    /// </para>
    /// <para>
    /// <c>CS597_MEASURE_SILENT_HOURS</c>（缺省 0）让 AGV-01 最后这么多小时不再发任何消息、会话行仍 Ready：派车摘要这时要从此刻一路读回到它
    /// 最后那一条，是新读法最坏的形状。造数先停在 #563 的迁移，造完再 <c>MigrateAsync</c>，记建索引耗时；另记绕过文件缓存顺序读一遍库文件的
    /// 耗时，供估算冷缓存下的建索引时间；再记几条未改动的单类型读法在这一版索引下的计划。
    /// </para>
    /// </remarks>
    [Fact(Explicit = true)]
    public async Task MeasureSafetyStateReads()
    {
        int days = int.TryParse(Environment.GetEnvironmentVariable("CS597_MEASURE_DAYS"), out int parsedDays) ? parsedDays : 3;
        int changeSeconds = int.TryParse(Environment.GetEnvironmentVariable("CS597_MEASURE_CHANGE_SECONDS"), out int parsedSeconds)
            ? parsedSeconds
            : 600;
        int silentHours = int.TryParse(Environment.GetEnvironmentVariable("CS597_MEASURE_SILENT_HOURS"), out int parsedSilent)
            ? parsedSilent
            : 0;
        string output = Environment.GetEnvironmentVariable("CS597_MEASURE_OUT") ?? Path.Combine(Path.GetTempPath(), "cs597-measure.txt");
        string databasePath = Path.Combine(
            Environment.GetEnvironmentVariable("CS597_MEASURE_DIR") ?? Path.GetTempPath(), $"cs597-measure-{Guid.NewGuid():N}.db");
        ProtocolInboxReadWindowTests.InboxRowsRead reads = new();
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .AddInterceptors(reads)
            .Options;
        List<string> lines = [];
        try
        {
            System.Diagnostics.Stopwatch seeding = System.Diagnostics.Stopwatch.StartNew();
            (long rows, long safetyRows, Dictionary<string, long> revisions) = await SeedMeasurementInboxAsync(options, days, changeSeconds, silentHours);
            seeding.Stop();
            lines.Add(FormattableString.Invariant(
                $"inbox rows={rows} safetyRows={safetyRows} days={days} changeSeconds={changeSeconds} agv01SilentHours={silentHours} vehicles=3 seed={seeding.Elapsed.TotalSeconds:F1}s db={new FileInfo(databasePath).Length / 1024 / 1024}MiB"));
            // The inbox was seeded on the schema #563 left; what the build under test adds on top is migrated now, on the full
            // inbox, the way the Host does it as it starts (control-server#597 adds an index there; before it nothing is pending).
            await using (ControlServerDbContext migrating = new(options))
            {
                string[] pending = [.. await migrating.Database.GetPendingMigrationsAsync(Token)];
                System.Diagnostics.Stopwatch migration = System.Diagnostics.Stopwatch.StartNew();
                await migrating.Database.MigrateAsync(Token);
                migration.Stop();
                lines.Add(FormattableString.Invariant(
                    $"migrate up on the seeded inbox (file cache warm: the seed just wrote it): pending=[{string.Join(",", pending)}] took={migration.Elapsed.TotalSeconds:F2}s"));
            }
            SqliteConnection.ClearAllPools();
            // Building the index reads the whole table once. What a cold cache adds on top of the warm build is about reading the
            // file from disk, so read it once bypassing the file cache and report the rate (review M1).
            (long bytes, double seconds) = ReadBypassingFileCache(databasePath);
            lines.Add(FormattableString.Invariant(
                $"uncached sequential read of the database file: {bytes / 1024 / 1024}MiB in {seconds:F2}s ({bytes / 1024d / 1024d / seconds:F0}MiB/s)"));
            await using (ControlServerDbContext probing = new(options))
            {
                // Plans of reads by one type that this ticket does not touch, on this build's indexes (review L4).
                (string Label, string Sql)[] probes =
                [
                    ("CapabilitySnapshot by type",
                        probing.ProtocolInbox.Where(row => row.MessageType == "CapabilitySnapshot").ToQueryString()),
                    ("SafetyStateSnapshot by type",
                        probing.ProtocolInbox.Where(row => row.MessageType == "SafetyStateSnapshot").ToQueryString()),
                    ("SublotSubmitted by type and text", probing.ProtocolInbox
                        .Where(row => row.MessageType == "SublotSubmitted" && row.RequestJson.Contains("op-AGV-01-0")).ToQueryString()),
                    ("both safety types, no order (the read before this ticket)", probing.ProtocolInbox
                        .Where(row => row.MessageType == "SafetyStateSnapshot" || row.MessageType == "SafetyStateChanged").ToQueryString()),
                ];
                await probing.Database.OpenConnectionAsync(Token);
                foreach ((string label, string sql) in probes)
                {
                    await using System.Data.Common.DbCommand explain = probing.Database.GetDbConnection().CreateCommand();
                    // ToQueryString prefixes parameter declarations (".param set ..."); the plan wants the statement alone.
                    explain.CommandText = "EXPLAIN QUERY PLAN " + string.Join(
                        "\n", sql.Split('\n').Where(line => !line.StartsWith(".param", StringComparison.Ordinal)));
                    List<string> plan = [];
                    await using (System.Data.Common.DbDataReader planReader = await explain.ExecuteReaderAsync(Token))
                    {
                        while (await planReader.ReadAsync(Token))
                        {
                            plan.Add(planReader.GetString(3));
                        }
                    }
                    lines.Add($"plan {label}: {string.Join(" | ", plan)}");
                }
            }

            await using ControlServerDbContext context = new(options);
            OnboardDispatchFactsReader reader = new(context, Options.Create(new JourneyRuntimeOptions()), new FixedClock(Now));
            (string Name, Func<Task<string>> Read)[] paths =
            [
                ("OnboardDispatchFactsReader.ReadOnboardFactsAsync(AGV-01)", async () =>
                    (await reader.ReadOnboardFactsAsync(AgvId, Token)) is { } facts
                        ? $"{facts with { AvailableSlots = [] }} slots={string.Join(",", facts.AvailableSlots)}"
                        : "null"),
                ("OnboardDispatchFactsReader.ReadDepartureSafetyGapsAsync(AGV-01)", async () =>
                    "[" + string.Join(",", await reader.ReadDepartureSafetyGapsAsync(
                        AgvId, MeasurementGeneration, revisions[AgvId], Token)) + "]"),
                ("InTransitDoorFacts.ReadAsync(AGV-01)", async () =>
                    (await InTransitDoorFacts.ReadAsync(context, AgvId, Now, Token)).ToString()),
                ("ExpectedActionOverdueQueryEndpoint.ReadAsync", async () =>
                    JsonSerializer.Serialize(await new ControlServer.Host.Dashboard.ExpectedActionOverdueQueryEndpoint(
                        ControlServer.Host.Dashboard.ExpectedActionOverdueOptions.Default, new FixedClock(Now)).ReadAsync(context, Token))),
            ];
            foreach ((string name, Func<Task<string>> read) in paths)
            {
                string answer = await read(); // warm-up; its answer is the one every timed round must repeat
                List<double> elapsed = [];
                int readCalls = 0;
                for (int round = 0; round < 7; round++)
                {
                    reads.Reset();
                    System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                    Assert.Equal(answer, await read());
                    watch.Stop();
                    elapsed.Add(watch.Elapsed.TotalMilliseconds);
                    readCalls = reads.Rows;
                }
                elapsed.Sort();
                lines.Add(FormattableString.Invariant(
                    $"{name}: inboxReadCalls={readCalls} medianMs={elapsed[elapsed.Count / 2]:F1} minMs={elapsed[0]:F1} maxMs={elapsed[^1]:F1} answer={answer}"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            await File.WriteAllLinesAsync(output, lines, CancellationToken.None);
        }
    }

    private const long MeasurementGeneration = 3;

    /// <summary>Reads the whole file once with <c>FILE_FLAG_NO_BUFFERING</c>, so from disk rather than from the file cache.</summary>
    private static (long Bytes, double Seconds) ReadBypassingFileCache(string path)
    {
        const FileOptions noBuffering = (FileOptions)0x20000000;
        const int chunk = 4 * 1024 * 1024;
        byte[] buffer = GC.AllocateUninitializedArray<byte>(chunk + 4096, pinned: true);
        long address = System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(buffer, 0).ToInt64();
        int offset = (int)((4096 - (address % 4096)) % 4096);
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle =
            File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, noBuffering);
        long total = 0;
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            int read = RandomAccess.Read(handle, buffer.AsSpan(offset, chunk), total);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        watch.Stop();
        return (total, watch.Elapsed.TotalSeconds);
    }

    private static async Task<(long Rows, long SafetyRows, Dictionary<string, long> Revisions)> SeedMeasurementInboxAsync(
        DbContextOptions<ControlServerDbContext> options, int days, int changeSeconds, int silentHours)
    {
        await using ControlServerDbContext context = new(options);
        await context.GetService<IMigrator>().MigrateAsync("20261011005905_ProtocolInboxReadIndexes", Token);
        string[] vehicles = [AgvId, OtherAgvId, "AGV-03"];
        DateTimeOffset start = Now - TimeSpan.FromDays(days);
        DateTimeOffset overdueAnswerAt = Now - TimeSpan.FromMinutes(10);
        // A silent AGV-01 sends nothing after the cutoff and its session row stays Ready on the last version it sent, as a
        // dropped link leaves it: the dispatch summary then reads back through every other vehicle's safety messages since.
        DateTimeOffset silentFrom = Now - TimeSpan.FromHours(silentHours);
        bool Sends(string vehicle, DateTimeOffset at) => vehicle != AgvId || silentHours == 0 || at <= silentFrom;
        long count = 0;
        long safetyCount = 0;
        Dictionary<string, long> revisions = [];
        List<ProtocolInboxRow> batch = [];
        async Task FlushAsync()
        {
            context.ProtocolInbox.AddRange(batch);
            await context.SaveChangesAsync(Token);
            context.ChangeTracker.Clear();
            count += batch.Count;
            batch.Clear();
        }

        for (int index = 0; index < vehicles.Length; index++)
        {
            string vehicle = vehicles[index];
            batch.Add(Envelope("CapabilitySnapshot", vehicle, MeasurementGeneration, start, new
            {
                capabilityVersion = 1,
                activeSlotConfigurationFingerprint = new string('0', 64),
                observedAt = start,
                slotStates = Enumerable.Range(1, 8).Select(slot => new
                {
                    slotNo = slot,
                    operability = "OPERABLE",
                    administrativeAvailability = "ENABLED",
                    physicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                }).ToArray(),
            }));
            batch.Add(Safety("SafetyStateSnapshot", vehicle, MeasurementGeneration, 1, start, stopped: true, locked: true));
            long version = 1;
            bool answered = vehicle != AgvId;
            for (DateTimeOffset at = start + TimeSpan.FromSeconds(changeSeconds - index); at < Now; at += TimeSpan.FromSeconds(changeSeconds))
            {
                if (!Sends(vehicle, at))
                {
                    break;
                }
                version++;
                string type = "SafetyStateChanged";
                if (!answered && at >= overdueAnswerAt)
                {
                    type = "SafetyStateSnapshot";
                    answered = true;
                }
                batch.Add(Safety(type, vehicle, MeasurementGeneration, version, at, stopped: true, locked: true));
                if (batch.Count >= 30_000)
                {
                    safetyCount += batch.Count(row => row.MessageType.StartsWith("SafetyState", StringComparison.Ordinal));
                    await FlushAsync();
                }
            }
            revisions[vehicle] = version;
            for (int sublot = 0; sublot < days * 50; sublot++)
            {
                DateTimeOffset at = start + TimeSpan.FromMinutes(sublot * 28.8);
                batch.Add(Envelope("SublotSubmitted", vehicle, MeasurementGeneration, at,
                    new { operationSessionId = FormattableString.Invariant($"op-{vehicle}-{sublot}"), sublotNo = $"S-{sublot}" }));
            }
            safetyCount += batch.Count(row => row.MessageType.StartsWith("SafetyState", StringComparison.Ordinal));
            await FlushAsync();
            SessionRecoveryRow session = ProtocolInboxReadWindowTests.Session(vehicle, MeasurementGeneration);
            session.SafetyRevision = version;
            context.SessionRecoveries.Add(session);
            await context.SaveChangesAsync(Token);
        }
        for (DateTimeOffset at = start + TimeSpan.FromSeconds(1); at < Now; at += TimeSpan.FromSeconds(2))
        {
            foreach (string vehicle in vehicles.Where(vehicle => Sends(vehicle, at)))
            {
                batch.Add(ProtocolInboxReadWindowTests.HeardFrom(vehicle, MeasurementGeneration, at));
            }
            if (batch.Count >= 30_000)
            {
                await FlushAsync();
            }
        }
        await FlushAsync();
        await new OnboardAlarmProjectionStore(context, new FixedClock(Now)).RecordSnapshotAsync(
            new OnboardAlarmSnapshotView(AgvId, 1, overdueAnswerAt,
            [
                new OnboardAlarmEntry(
                    OnboardAlarmCodes.SlotExpectedActionOverdue, "WARNING", overdueAnswerAt - TimeSpan.FromSeconds(1),
                    OnboardAlarmScope.CurrentOperation, "关好3号仓门", PhysicalSlotNumber: 3,
                    AlarmId: "00000000-0000-4000-8000-00000000a003"),
            ]),
            MeasurementGeneration,
            overdueAnswerAt - TimeSpan.FromSeconds(1),
            Token);
        return (count, safetyCount, revisions);
    }

    // ---- 造数 -------------------------------------------------------------------------------------------------

    private static OnboardDispatchFactsReader Reader(ControlServerDbContext context) =>
        new(context, Options.Create(new JourneyRuntimeOptions()), new FixedClock(Now));

    /// <summary>
    /// 一台就绪的车：会话行（代次 <see cref="Generation"/>、安全版本 <see cref="Revision"/>），握手时的能力快照与基线安全快照（版本 1），
    /// 一小时前收到；以及一秒前的一条心跳，让它在证据窗口里算活。当前摘要由各用例自己加。
    /// </summary>
    private static async Task SeedReadyVehicleAsync(ControlServerDbContext context, string agvId)
    {
        await SeedSessionAsync(context, agvId, Revision);
        DateTimeOffset handshake = Now - TimeSpan.FromHours(1);
        context.ProtocolInbox.Add(Envelope("CapabilitySnapshot", agvId, Generation, handshake, new
        {
            capabilityVersion = 1,
            activeSlotConfigurationFingerprint = new string('0', 64),
            observedAt = handshake,
            slotStates = Enumerable.Range(1, 8).Select(slot => new
            {
                slotNo = slot,
                operability = "OPERABLE",
                administrativeAvailability = "ENABLED",
                physicalState = "EMPTY",
                lockState = "LOCKED",
                unlockOutputState = "RESET",
            }).ToArray(),
        }));
        await context.SaveChangesAsync(Token);
        context.ProtocolInbox.Add(Safety("SafetyStateSnapshot", agvId, Generation, 1, handshake, stopped: false, locked: false));
        await context.SaveChangesAsync(Token);
        context.ProtocolInbox.Add(HeardFrom(agvId, Generation, Now - TimeSpan.FromSeconds(1)));
        await context.SaveChangesAsync(Token);
    }

    /// <summary>会话行，与一秒前的一条心跳（在途门锁要这一代在存活窗口里听得到）。</summary>
    private static async Task SeedSessionAsync(ControlServerDbContext context, string agvId, long? revision)
    {
        SessionRecoveryRow session = ProtocolInboxReadWindowTests.Session(agvId, Generation);
        session.SafetyRevision = revision;
        context.SessionRecoveries.Add(session);
        context.ProtocolInbox.Add(HeardFrom(agvId, Generation, Now - TimeSpan.FromSeconds(1)));
        await context.SaveChangesAsync(Token);
    }

    private static ProtocolInboxRow HeardFrom(string agvId, long generation, DateTimeOffset at) =>
        ProtocolInboxReadWindowTests.HeardFrom(agvId, generation, at);

    /// <summary>一条安全状态消息。快照带八个仓位都锁着的 <c>slotStates</c>（基线要），变化带 <c>affectedSlots</c>。</summary>
    internal static ProtocolInboxRow Safety(
        string messageType, string agvId, long generation, long version, DateTimeOffset receivedAt, bool stopped, bool locked)
    {
        object safety = new
        {
            departureSafe = stopped && locked,
            vehicleStopped = stopped,
            allTargetSlotsLocked = locked,
            allUnlockOutputsReset = true,
            unknownPresent = false,
            reasonCodes = locked ? Array.Empty<string>() : ["LOCK_NOT_CLOSED"],
        };
        object payload = messageType == "SafetyStateSnapshot"
            ? new
            {
                safetyStateVersion = version,
                observedAt = receivedAt,
                safety,
                slotStates = Enumerable.Range(1, 8).Select(slot => new
                {
                    slotNo = slot,
                    physicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                }).ToArray(),
            }
            : new { safetyStateVersion = version, observedAt = receivedAt, safety, affectedSlots = new[] { 1 } };
        return Envelope(messageType, agvId, generation, receivedAt, payload);
    }

    private static ProtocolInboxRow Envelope(
        string messageType, string agvId, long generation, DateTimeOffset receivedAt, object payload) => new()
    {
        MessageId = Guid.NewGuid().ToString("D"),
        MessageType = messageType,
        RequestJson = JsonSerializer.Serialize(new
        {
            messageType,
            messageId = Guid.NewGuid().ToString("D"),
            agvId,
            sessionGeneration = generation,
            sentAt = receivedAt,
            payload,
        }),
        ContentHash = new string('0', 64),
        FirstResponseJson = "{}",
        ReceivedAt = receivedAt
    };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    /// <summary>内存 SQLite，建库与生产同一个模型（含 #563 的两个索引）。</summary>
    internal sealed class InboxDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private InboxDatabase(SqliteConnection connection, ControlServerDbContext context)
        {
            this.connection = connection;
            Context = context;
        }

        public ControlServerDbContext Context { get; }

        public static async Task<InboxDatabase> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(Token);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(Token);
            return new InboxDatabase(connection, context);
        }

        /// <summary>一行一次保存，写入次序（rowid）就是参数次序。</summary>
        public async Task AddInOrderAsync(params ProtocolInboxRow[] rows)
        {
            foreach (ProtocolInboxRow row in rows)
            {
                Context.ProtocolInbox.Add(row);
                await Context.SaveChangesAsync(Token);
            }
            Context.ChangeTracker.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
