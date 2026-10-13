using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Dispatch;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 判存活只读存活窗口里的收件箱行，不读收件箱的历史（control-server#563，移植 MVP 的 <c>ef5dad8e</c>；并入 control-server#255）。
/// </summary>
/// <remarks>
/// <para>
/// 收件箱留着车发过的每一条心跳，车在线时一天几万行，从不清理。判存活此前每次都把整张表读回来逐行解析，而它在每轮、每条旅程、
/// 看板每两秒都要判一次——MVP 在 factory01 上量过，车连续在线约 6 天，单这一项就超过两秒的轮询间隔（MVP control-server#29）。
/// 窗口之外的行本来就证不了存活，所以不读它们不改任何判定。
/// </para>
/// <para>
/// 证据是从收件箱读回了多少行（<see cref="InboxRowsRead"/> 数数据库读回的行），不是「没抛错」：<see cref="SessionLiveness"/> 先判收件时间、
/// 后解析正文，窗口外的行整表读回来也不会被解析，所以一条解析即抛的绊线行在改动之前也不响。
/// </para>
/// </remarks>
public sealed class ProtocolInboxReadWindowTests
{
    private const string AgvId = "AGV-01";
    private const long Generation = 3;
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 8, 0, 0, TimeSpan.FromHours(8));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// 两个重载都只读窗口里的行：窗口之前的二十条心跳与收件时间晚于此刻的一条都不读回来，判定与今天一样——窗口里那条心跳让这台车算活。
    /// </summary>
    [Fact]
    public async Task SessionLivenessReadsOnlyTheRowsInsideItsWindow()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        database.Context.SessionRecoveries.Add(Session(AgvId, Generation));
        for (int minutes = 1; minutes <= 20; minutes++)
        {
            database.Context.ProtocolInbox.Add(HeardFrom(AgvId, Generation, Now - TimeSpan.FromMinutes(minutes)));
        }
        database.Context.ProtocolInbox.Add(HeardFrom(AgvId, Generation, Now - TimeSpan.FromSeconds(1)));
        database.Context.ProtocolInbox.Add(HeardFrom(AgvId, Generation, Now + TimeSpan.FromSeconds(1)));
        await database.Context.SaveChangesAsync(Token);

        database.Reads.Reset();
        bool single = await SessionLiveness.HeardFromAsync(database.Context, AgvId, Generation, Now, Token);
        int singleRead = database.Reads.Rows;
        database.Reads.Reset();
        HashSet<string> all = await SessionLiveness.HeardFromAsync(database.Context, Now, Token);
        int allRead = database.Reads.Rows;

        // 窗口里只有一行：一次读到它，一次答「没有了」。
        Assert.Equal((true, 2, AgvId, 2), (single, singleRead, string.Join(",", all), allRead));
    }

    /// <summary>
    /// 存活窗口的边：最后一条入站恰好在 <c>now - Timeout</c> 时两个重载都算活，早一个 tick 就都不算。改动前后都是这样——
    /// 这一条守的是库里那个下界与 <c>InsideWindow</c> 一样是闭的。
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task SessionLivenessCountsTheLastInboundExactlyAtTheEdgeOfItsWindow(long ticksBeyondTheEdge, bool live)
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        database.Context.SessionRecoveries.Add(Session(AgvId, Generation));
        database.Context.ProtocolInbox.Add(HeardFrom(AgvId, Generation, (Now - SessionLiveness.Timeout).AddTicks(-ticksBeyondTheEdge)));
        await database.Context.SaveChangesAsync(Token);

        bool single = await SessionLiveness.HeardFromAsync(database.Context, AgvId, Generation, Now, Token);
        HashSet<string> all = await SessionLiveness.HeardFromAsync(database.Context, Now, Token);

        Assert.Equal((live, live), (single, all.Contains(AgvId)));
    }

    /// <summary>
    /// 每轮每车的派车事实读取（<see cref="OnboardDispatchFactsReader.ReadOnboardFactsAsync"/>）判存活只读证据窗口
    /// （<see cref="JourneyRuntimeOptions.MaximumEvidenceAge"/>）里的行：窗口之前那四十条心跳不读回来，车照样算活、事实照样读出。
    /// </summary>
    /// <remarks>
    /// 读回次数逐条数得出来：能力快照、基线安全快照两处按类型读，各一行加一次「没有了」；当前安全摘要从新到旧读、读到那一行就停
    /// （control-server#597），记 1；判存活只读窗口里那一条心跳，读到它就停。合计 2 + 2 + 1 + 1 = 6。#563 之前判存活整表读回
    /// 43 行再加一次「没有了」，合计 50；#597 之前当前安全摘要也是一行加一次「没有了」，合计 7。
    /// </remarks>
    [Fact]
    public async Task DispatchFactsReadOnlyTheEvidenceWindowToJudgeLiveness()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId, Now - TimeSpan.FromHours(2));
        for (int minutes = 1; minutes <= 40; minutes++)
        {
            database.Context.ProtocolInbox.Add(HeardFrom(AgvId, Generation, Now - TimeSpan.FromMinutes(minutes)));
        }
        database.Context.ProtocolInbox.Add(HeardFrom(AgvId, Generation, Now - TimeSpan.FromSeconds(1)));
        await database.Context.SaveChangesAsync(Token);

        database.Reads.Reset();
        OnboardDispatchFacts? facts = await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token);

        Assert.Equal((Generation, 6), (facts?.SessionGeneration, database.Reads.Rows));
    }

    /// <summary>
    /// 静默车：会话行还是 Ready，车已经不发了，另一台车还在发心跳。派车事实读取判这台车不活，而读回次数不随另一台车的心跳条数增长——
    /// 证据窗口的下界在这里才起作用（审查 S1）：车在线时它的心跳总是最新那一行，从新到旧读第一行就停，有没有下界读回次数一样；
    /// 车静默时没有下界，就要从此刻一路往回扫到它最后那一条，断线一天每轮约 8.6 万行。
    /// </summary>
    /// <remarks>
    /// 读回次数逐条数得出来：能力快照、基线安全快照各 2，当前安全摘要读到即停记 1（control-server#597，此前也是 2）；判存活只读证据窗口里
    /// 另一台车的 15 条心跳（此刻之前 1、3、……、29 秒）加一次「没有了」。合计 5 + 16 = 21，另一台车在窗口之前多发多少条都一样。
    /// </remarks>
    [Theory]
    [InlineData(50)]
    [InlineData(400)]
    public async Task DispatchFactsOfASilentVehicleDoNotReadBackPastTheEvidenceWindow(int otherVehicleHeartbeats)
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId, Now - TimeSpan.FromHours(2));
        database.Context.ProtocolInbox.Add(HeardFrom(AgvId, Generation, Now - TimeSpan.FromHours(1)));
        for (int beat = 0; beat < otherVehicleHeartbeats; beat++)
        {
            database.Context.ProtocolInbox.Add(HeardFrom("AGV-02", Generation, Now - TimeSpan.FromSeconds(1 + (2 * beat))));
        }
        await database.Context.SaveChangesAsync(Token);

        database.Reads.Reset();
        OnboardDispatchFacts? facts = await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token);

        Assert.Equal((null, 21), (facts, database.Reads.Rows));
    }

    /// <summary>
    /// 引擎里另外两处判存活（站点期限到点结束停靠、确定性装货失败，<c>JourneyRuntimeEngine</c> 里传证据窗口下界的那两处）对静默车同样不往回扫：
    /// 车静默一分钟，其间另一台车发了 N 条心跳（都在证据窗口与存活窗口之前），整轮读回收件箱的次数不随 N 变，旅程照旧停着不动——
    /// 与 <c>JourneyRuntimeWorkerTests.TheStationDeadlineDoesNotEndTheStopWhileTheVehicleIsOffAir</c>、
    /// <see cref="JourneyRuntimeWorkerLoadDeadlineTests.ADeterminateLoadFailureIsNotSettledWhileTheVehicleIsOffAir"/> 同一个判定。
    /// </summary>
    [Theory]
    [InlineData(nameof(JourneyRuntimeStage.AwaitingSublot))]
    [InlineData(nameof(JourneyRuntimeStage.AwaitingLoadResult))]
    public async Task AnEngineRoundOverASilentVehicleDoesNotReadBackPastTheEvidenceWindow(string waitingIn)
    {
        JourneyRuntimeStage stage = Enum.Parse<JourneyRuntimeStage>(waitingIn);
        (JourneyRuntimeStage Stage, int Reads) few = await SilentVehicleRoundAsync(stage, otherVehicleHeartbeats: 10);
        (JourneyRuntimeStage Stage, int Reads) many = await SilentVehicleRoundAsync(stage, otherVehicleHeartbeats: 400);

        Assert.Equal((stage, stage, few.Reads), (few.Stage, many.Stage, many.Reads));
    }

    /// <summary>
    /// 一趟停在 <paramref name="stage"/> 的旅程，站点期限（十秒）已过、车静默一分钟、证据窗口五秒；其间另一台车的心跳都在两个窗口之前。
    /// 跑一轮，答旅程停在哪一段、这一轮从收件箱读回几次。
    /// </summary>
    private static async Task<(JourneyRuntimeStage Stage, int Reads)> SilentVehicleRoundAsync(
        JourneyRuntimeStage stage, int otherVehicleHeartbeats)
    {
        InboxRowsRead reads = new();
        await using JourneyRuntimeWorkerTestKit.RuntimeFixture fixture =
            await JourneyRuntimeWorkerTestKit.RuntimeFixture.CreateAsync(commands: reads);
        fixture.Options.MaximumEvidenceAge = TimeSpan.FromSeconds(5);
        if (stage == JourneyRuntimeStage.AwaitingSublot)
        {
            fixture.Options.StationDepartureWaitTimeout = TimeSpan.FromSeconds(10);
            fixture.Catalog.Set(fixture.Demand(
                "10000000-0000-4000-8000-000000000001", "SUBLOT-001", createdAt: JourneyRuntimeWorkerTestKit.Now.AddMinutes(-10)));
            fixture.BoxCounts.Set("SUBLOT-001", 7);
            await fixture.AdvanceToSublotWaitAsync();
            await fixture.ProveSlotDoorsClosedAsync();
        }
        else
        {
            await JourneyRuntimeWorkerLoadDeadlineTests.AdvanceToLoadWithStationDeadlineAsync(fixture);
            fixture.Clock.Advance(TimeSpan.FromSeconds(11));
            await JourneyRuntimeWorkerLoadDeadlineTests.ReportLoadResultAsync(
                fixture, JourneyRuntimeWorkerLoadDeadlineTests.DeterminateFailureSlots(fixture, "OPERATOR_TIMEOUT"));
        }
        DateTimeOffset lastHeard = fixture.Clock.GetUtcNow();
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        for (int beat = 0; beat < otherVehicleHeartbeats; beat++)
        {
            fixture.Context.ProtocolInbox.Add(HeardFrom(
                "AGV-OTHER", Generation, lastHeard + TimeSpan.FromMilliseconds(100 * (beat + 1))));
        }
        await fixture.Context.SaveChangesAsync(Token);
        fixture.Context.ChangeTracker.Clear();

        reads.Reset();
        await fixture.Engine.ExecuteOnceAsync(Token);
        int read = reads.Rows;
        return ((await fixture.RuntimeAsync()).Stage, read);
    }

    /// <summary>
    /// 证据窗口的边：最后一条入站恰好在 <c>now - MaximumEvidenceAge</c> 时仍算活，早一个 tick 就不算；收件时间晚于此刻的不算活。
    /// 改动前后都是这样——这一条守的是下推之后窗口的下界仍是闭的、上界仍判「来自未来」，不是新行为。
    /// </summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task DispatchFactsCountTheLastInboundExactlyAtTheEdgeOfTheEvidenceWindow(long ticksBeyondTheEdge, bool live)
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId, Now - TimeSpan.FromHours(2));
        DateTimeOffset edge = Now - new JourneyRuntimeOptions().MaximumEvidenceAge;
        database.Context.ProtocolInbox.Add(HeardFrom(AgvId, Generation, edge.AddTicks(-ticksBeyondTheEdge)));
        await database.Context.SaveChangesAsync(Token);

        OnboardDispatchFacts? facts = await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token);

        Assert.Equal(live, facts is not null);
    }

    /// <summary>收件时间晚于此刻的入站让派车事实失败关闭，与今天一样：读窗口时不能只留下界、把它当成「最新」。</summary>
    [Fact]
    public async Task DispatchFactsFailClosedOnAnInboundReceivedAfterNow()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        await SeedReadyVehicleAsync(database.Context, AgvId, Now - TimeSpan.FromHours(2));
        database.Context.ProtocolInbox.Add(HeardFrom(AgvId, Generation, Now - TimeSpan.FromSeconds(1)));
        database.Context.ProtocolInbox.Add(HeardFrom(AgvId, Generation, Now + TimeSpan.FromSeconds(1)));
        await database.Context.SaveChangesAsync(Token);

        Assert.Null(await Reader(database.Context).ReadOnboardFactsAsync(AgvId, Token));
    }

    /// <summary>
    /// 不给下界时（会话失联那一刻记日志用），答的仍是这一代会话最后一次入站的收件时间，哪怕它早于证据窗口；而且从新到旧读，
    /// 读到这一代的第一行就停——此后另一台车的十条心跳读了，这台车更早的二十条不读。
    /// </summary>
    [Fact]
    public async Task WithoutALowerBoundTheLastInboundIsStillFoundNewestFirst()
    {
        await using InboxDatabase database = await InboxDatabase.CreateAsync();
        for (int minutes = 6; minutes <= 25; minutes++)
        {
            database.Context.ProtocolInbox.Add(HeardFrom(AgvId, Generation, Now - TimeSpan.FromMinutes(minutes)));
        }
        DateTimeOffset lastHeard = Now - TimeSpan.FromMinutes(5);
        database.Context.ProtocolInbox.Add(HeardFrom(AgvId, Generation, lastHeard));
        for (int minutes = 1; minutes <= 10; minutes++)
        {
            database.Context.ProtocolInbox.Add(HeardFrom("AGV-02", Generation, lastHeard + TimeSpan.FromSeconds(minutes)));
        }
        await database.Context.SaveChangesAsync(Token);

        database.Reads.Reset();
        DateTimeOffset? latest = await Reader(database.Context).LatestInboundAtForSessionAsync(
            AgvId, Generation, notBefore: null, Token);

        Assert.Equal((lastHeard, 11), (latest, database.Reads.Rows));
    }

    private static OnboardDispatchFactsReader Reader(ControlServerDbContext context) =>
        new(context, Options.Create(new JourneyRuntimeOptions()), new MeasurementClock(Now));

    /// <summary>一台就绪的车：会话行（安全版本 1），与它握手时那一条能力快照、一条安全快照，收件时间都是 <paramref name="at"/>。</summary>
    private static async Task SeedReadyVehicleAsync(ControlServerDbContext context, string agvId, DateTimeOffset at)
    {
        SessionRecoveryRow session = Session(agvId, Generation);
        session.SafetyRevision = 1;
        context.SessionRecoveries.Add(session);
        context.ProtocolInbox.Add(CapabilitySnapshot(agvId, at));
        context.ProtocolInbox.Add(Envelope("SafetyStateSnapshot", agvId, at, SafetyPayload(1, at, withSlots: true)));
        await context.SaveChangesAsync(Token);
    }

    /// <summary>
    /// 迁移在有收件箱数据的库上走一个来回：升上来时，存量行的 <see cref="ProtocolInboxRow.ReceivedAtUtcTicks"/> 按 <c>ReceivedAt</c> 的原文
    /// <b>精确</b>回填（与 setter 写的逐行相同），两个索引都在；退回上一个迁移时列与索引都去掉，行一条不少、<c>ReceivedAt</c> 原文不变。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 回填不精确，判定就会在升级那一刻变：MVP 的 <c>ef5dad8e</c> 回填到整秒，存量行读起来比真实收件早不到一秒，于是升级后的
    /// 六秒存活窗口与三十秒证据窗口里，边上的行会被当成窗口外。这里要它逐 tick 相同。
    /// </para>
    /// <para>
    /// 存量行由今天的模型写下再退回上一个迁移得到，不手写 SQL：<c>ReceivedAt</c> 的原文是 EF 写出的那一种（<c>yyyy-MM-dd HH:mm:ss.FFFFFFFzzz</c>），
    /// 回填要解析的正是它。几个时刻覆盖整秒（没有小数点）、七位小数、负时区、UTC，以及 <c>.9999999</c>：SQLite 的日期函数只留毫秒，
    /// <c>julianday</c> 会把它进位到下一秒。回填把小数截下来单独加回去，不交给日期函数，所以这一行今天对不对不取决于那个进位；
    /// 在本仓用的 SQLite 上，<c>strftime('%s')</c> 对带小数的原文也恰好不进位，把截取换回原文这一条并不变红（变异实测）——它守的是回填结果，
    /// 不是那个写法。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task MigratingUpBackfillsEveryStoredRowExactlyAndMigratingDownDropsOnlyTheColumnAndIndexes()
    {
        DateTimeOffset[] receivedAt =
        [
            new(2026, 10, 11, 8, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 10, 11, 8, 0, 1, TimeSpan.FromHours(8)).AddTicks(1_234_567),
            new DateTimeOffset(2026, 10, 10, 19, 30, 2, TimeSpan.FromHours(-5.5)).AddTicks(5_000_000),
            new DateTimeOffset(2026, 10, 11, 0, 0, 3, TimeSpan.Zero).AddTicks(1),
            new DateTimeOffset(2026, 10, 11, 8, 0, 4, TimeSpan.FromHours(8)).AddTicks(9_999_999),
        ];
        await using Batch7JourneyFixture fixture = await Batch7JourneyFixture.CreateMigratedForRealAsync();
        Dictionary<string, long> expected = [];
        foreach (DateTimeOffset at in receivedAt)
        {
            ProtocolInboxRow row = HeardFrom(AgvId, Generation, at);
            fixture.Context.ProtocolInbox.Add(row);
            expected[row.MessageId] = at.UtcTicks;
        }
        await fixture.Context.SaveChangesAsync(Token);
        string[] storedText = await ColumnAsync(fixture, "ReceivedAt");

        await fixture.Context.GetService<IMigrator>().MigrateAsync(MigrationBeforeReadIndexes, Token);
        string[] columnsDown = await ColumnNamesAsync(fixture);
        string[] indexesDown = await IndexNamesAsync(fixture);
        string[] textDown = await ColumnAsync(fixture, "ReceivedAt");

        await fixture.Context.Database.MigrateAsync(Token);
        await fixture.RenewContextAsync();
        Dictionary<string, long> backfilled = await fixture.Context.ProtocolInbox.AsNoTracking()
            .ToDictionaryAsync(row => row.MessageId, row => row.ReceivedAtUtcTicks, Token);

        Assert.DoesNotContain("ReceivedAtUtcTicks", columnsDown);
        Assert.DoesNotContain(indexesDown, name => name.StartsWith("IX_ProtocolInbox_", StringComparison.Ordinal));
        Assert.Equal(storedText, textDown);
        Assert.Equal(expected.OrderBy(pair => pair.Key), backfilled.OrderBy(pair => pair.Key));
        Assert.Equal(
            ["IX_ProtocolInbox_MessageType", "IX_ProtocolInbox_ReceivedAtUtcTicks"],
            (await IndexNamesAsync(fixture)).Where(name => name.StartsWith("IX_ProtocolInbox_", StringComparison.Ordinal)));
    }

    private const string MigrationBeforeReadIndexes = "20261008052643_UnreleasableNotReconciledBlocksBeforeUpgrade";

    private static Task<string[]> ColumnNamesAsync(Batch7JourneyFixture fixture) =>
        QueryAsync(fixture, """SELECT name FROM pragma_table_info('ProtocolInbox') ORDER BY cid""");

    private static Task<string[]> IndexNamesAsync(Batch7JourneyFixture fixture) =>
        QueryAsync(fixture, """SELECT name FROM pragma_index_list('ProtocolInbox') ORDER BY name""");

    private static Task<string[]> ColumnAsync(Batch7JourneyFixture fixture, string column) =>
        QueryAsync(fixture, $"""SELECT "{column}" FROM "ProtocolInbox" ORDER BY "MessageId" """);

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

    /// <summary>
    /// 量测（不进默认运行，<c>Explicit</c>）：三台车各连续在线 <c>CS563_MEASURE_DAYS</c> 天（缺省 3）的收件箱上，四条热路径读法每次读回几行、用多久。
    /// 结果写进 <c>CS563_MEASURE_OUT</c> 指的文件（缺省在临时目录）。改动前后在同一台机器上各跑一次，对照写进 PR。
    /// <c>CS563_MEASURE_SILENT_HOURS</c>（缺省 0）让 AGV-01 在最后这么多小时里不再发心跳、会话行仍是 Ready：断线没人改会话行时的样子，
    /// 证据窗口的下界只在这时才省下读回的行（审查 S1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 造数照现场的形状：心跳每两秒一条（ADR-cross-0027），占收件箱的绝大多数；每车会话开头一条 <c>CapabilitySnapshot</c> 与一条
    /// <c>SafetyStateSnapshot</c>，此后每十分钟一条 <c>SafetyStateChanged</c>；每车每天 50 条 <c>SublotSubmitted</c>。库是磁盘上的
    /// SQLite 文件，不是内存库，与现场同一个存储；建库走真实的迁移。
    /// </para>
    /// <para>
    /// 只经改动前后签名都不变的公开入口量：<see cref="SessionLiveness"/> 两个重载（看板每两秒、每轮每条旅程），
    /// <see cref="OnboardDispatchFactsReader.ReadOnboardFactsAsync"/>（每轮每车，里面有按车判存活与三处按类型读），以及与
    /// <c>JourneyRuntimeEngine.FindMatchingSublotAsync</c> 同一条 LINQ 的按类型读（那个方法是私有的）。
    /// </para>
    /// </remarks>
    [Fact(Explicit = true)]
    public async Task MeasureHotPathInboxReads()
    {
        int days = int.TryParse(Environment.GetEnvironmentVariable("CS563_MEASURE_DAYS"), out int parsed) ? parsed : 3;
        int silentHours = int.TryParse(Environment.GetEnvironmentVariable("CS563_MEASURE_SILENT_HOURS"), out int silent) ? silent : 0;
        string output = Environment.GetEnvironmentVariable("CS563_MEASURE_OUT")
                        ?? Path.Combine(Path.GetTempPath(), "cs563-measure.txt");
        // The database runs to hundreds of MiB; CS563_MEASURE_DIR puts it on a roomier disk than the temp directory's.
        string databasePath = Path.Combine(
            Environment.GetEnvironmentVariable("CS563_MEASURE_DIR") ?? Path.GetTempPath(), $"cs563-measure-{Guid.NewGuid():N}.db");
        string[] vehicles = ["AGV-01", "AGV-02", "AGV-03"];
        InboxRowsRead reads = new();
        DbContextOptions<ControlServerDbContext> options = new DbContextOptionsBuilder<ControlServerDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .AddInterceptors(reads)
            .Options;
        List<string> lines = [];
        try
        {
            Stopwatch seeding = Stopwatch.StartNew();
            long rows = await SeedMeasurementInboxAsync(options, vehicles, days, silentHours);
            seeding.Stop();
            lines.Add(FormattableString.Invariant(
                $"inbox rows={rows} days={days} vehicles={vehicles.Length} agv01SilentHours={silentHours} seed={seeding.Elapsed.TotalSeconds:F1}s db={new FileInfo(databasePath).Length / 1024 / 1024}MiB"));

            await using ControlServerDbContext context = new(options);
            OnboardDispatchFactsReader reader = new(
                context, Options.Create(new JourneyRuntimeOptions()), new MeasurementClock(Now));
            const string operationSessionId = "op-AGV-01-0";
            (string Name, Func<Task<string>> Read)[] paths =
            [
                ("SessionLiveness.HeardFromAsync(all)", async () =>
                    string.Join(",", (await SessionLiveness.HeardFromAsync(context, Now, Token)).Order(StringComparer.Ordinal))),
                ("SessionLiveness.HeardFromAsync(AGV-01)", async () =>
                    (await SessionLiveness.HeardFromAsync(context, "AGV-01", Generation, Now, Token)).ToString()),
                ("OnboardDispatchFactsReader.ReadOnboardFactsAsync(AGV-01)", async () =>
                    (await reader.ReadOnboardFactsAsync("AGV-01", Token))?.ToString() ?? "null"),
                ("SublotSubmitted by type (FindMatchingSublotAsync shape)", async () =>
                    (await context.ProtocolInbox.AsNoTracking()
                        .Where(row => row.MessageType == "SublotSubmitted" && row.RequestJson.Contains(operationSessionId))
                        .ToArrayAsync(Token)).Length.ToString(CultureInfo.InvariantCulture)),
            ];
            foreach ((string name, Func<Task<string>> read) in paths)
            {
                string answer = await read(); // warm-up; its answer is the one every timed round must repeat
                List<double> elapsed = [];
                int readCalls = 0;
                for (int round = 0; round < 7; round++)
                {
                    reads.Reset();
                    Stopwatch watch = Stopwatch.StartNew();
                    Assert.Equal(answer, await read());
                    watch.Stop();
                    elapsed.Add(watch.Elapsed.TotalMilliseconds);
                    readCalls = reads.Rows;
                }
                elapsed.Sort();
                lines.Add(FormattableString.Invariant(
                    $"{name}: answer={answer} inboxReadCalls={readCalls} medianMs={elapsed[elapsed.Count / 2]:F1} minMs={elapsed[0]:F1} maxMs={elapsed[^1]:F1}"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            await File.WriteAllLinesAsync(output, lines, CancellationToken.None);
        }
    }

    private static async Task<long> SeedMeasurementInboxAsync(
        DbContextOptions<ControlServerDbContext> options, string[] vehicles, int days, int silentHours)
    {
        await using ControlServerDbContext context = new(options);
        await context.Database.MigrateAsync(Token);
        long lastSafetyRevision = (days * 24 * 6) + 1;
        DateTimeOffset start = Now - TimeSpan.FromDays(days);
        // A silent AGV-01 sends nothing at all after the cutoff -- any inbound message counts as alive -- and its session row
        // stays Ready on the last safety version it sent before going quiet, as a dropped link leaves it.
        DateTimeOffset silentFrom = Now - TimeSpan.FromHours(silentHours);
        bool Sends(string vehicle, DateTimeOffset at) => vehicle != "AGV-01" || at <= silentFrom;
        static DateTimeOffset SafetyChangedAt(DateTimeOffset start, long revision) =>
            start + TimeSpan.FromMinutes(10 * (revision - 1)) - TimeSpan.FromSeconds(3);
        foreach (string vehicle in vehicles)
        {
            SessionRecoveryRow session = Session(vehicle, Generation);
            long revision = lastSafetyRevision;
            while (revision > 1 && !Sends(vehicle, SafetyChangedAt(start, revision)))
            {
                revision--;
            }
            session.SafetyRevision = revision;
            context.SessionRecoveries.Add(session);
        }
        await context.SaveChangesAsync(Token);

        long count = 0;
        List<ProtocolInboxRow> batch = [];
        async Task FlushAsync()
        {
            context.ProtocolInbox.AddRange(batch);
            await context.SaveChangesAsync(Token);
            context.ChangeTracker.Clear();
            count += batch.Count;
            batch.Clear();
        }

        foreach (string vehicle in vehicles)
        {
            batch.Add(CapabilitySnapshot(vehicle, start));
            batch.Add(Envelope("SafetyStateSnapshot", vehicle, start, SafetyPayload(1, start, withSlots: true)));
            for (long revision = 2; revision <= lastSafetyRevision; revision++)
            {
                DateTimeOffset at = SafetyChangedAt(start, revision);
                if (Sends(vehicle, at))
                {
                    batch.Add(Envelope("SafetyStateChanged", vehicle, at, SafetyPayload(revision, at, withSlots: false)));
                }
            }
            for (int sublot = 0; sublot < days * 50; sublot++)
            {
                DateTimeOffset at = start + TimeSpan.FromMinutes(sublot * 28.8);
                if (!Sends(vehicle, at))
                {
                    continue;
                }
                batch.Add(Envelope(
                    "SublotSubmitted",
                    vehicle,
                    at,
                    new { operationSessionId = FormattableString.Invariant($"op-{vehicle}-{sublot}"), sublotNo = $"S-{sublot}" }));
            }
            await FlushAsync();
        }
        for (DateTimeOffset at = start + TimeSpan.FromSeconds(1); at < Now; at += TimeSpan.FromSeconds(2))
        {
            foreach (string vehicle in vehicles)
            {
                if (!Sends(vehicle, at))
                {
                    continue;
                }
                batch.Add(Envelope("Heartbeat", vehicle, at, new { }));
            }
            if (batch.Count >= 30_000)
            {
                await FlushAsync();
            }
        }
        await FlushAsync();
        return count;
    }

    private static ProtocolInboxRow CapabilitySnapshot(string agvId, DateTimeOffset at) => Envelope("CapabilitySnapshot", agvId, at, new
    {
        capabilityVersion = 1,
        activeSlotConfigurationFingerprint = new string('0', 64),
        observedAt = at,
        slotStates = Enumerable.Range(1, 8).Select(slot => new
        {
            slotNo = slot,
            operability = "OPERABLE",
            administrativeAvailability = "ENABLED",
            physicalState = "EMPTY",
            lockState = "LOCKED",
            unlockOutputState = "RESET",
        }).ToArray(),
    });

    private static object SafetyPayload(long revision, DateTimeOffset observedAt, bool withSlots)
    {
        object safety = new
        {
            departureSafe = true,
            vehicleStopped = true,
            allTargetSlotsLocked = true,
            allUnlockOutputsReset = true,
            unknownPresent = false,
            reasonCodes = Array.Empty<string>(),
        };
        return withSlots
            ? new
            {
                safetyStateVersion = revision,
                observedAt,
                safety,
                slotStates = Enumerable.Range(1, 8).Select(slot => new
                {
                    slotNo = slot,
                    physicalState = "EMPTY",
                    lockState = "LOCKED",
                    unlockOutputState = "RESET",
                }).ToArray(),
            }
            : new { safetyStateVersion = revision, observedAt, safety, affectedSlots = new[] { 1 } };
    }

    private static ProtocolInboxRow Envelope(string messageType, string agvId, DateTimeOffset receivedAt, object payload) => new()
    {
        MessageId = Guid.NewGuid().ToString("D"),
        MessageType = messageType,
        RequestJson = JsonSerializer.Serialize(new
        {
            messageType,
            messageId = Guid.NewGuid().ToString("D"),
            agvId,
            sessionGeneration = Generation,
            sentAt = receivedAt,
            payload,
        }),
        ContentHash = new string('0', 64),
        FirstResponseJson = "{}",
        ReceivedAt = receivedAt
    };

    private sealed class MeasurementClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    internal static SessionRecoveryRow Session(string agvId, long generation) => new()
    {
        AgvId = agvId,
        SessionGeneration = generation,
        ProtocolCommit = "c",
        ManifestSha256 = "m",
        ProfileId = "AGV_FULL_PRODUCT",
        ProtocolVersion = 2,
        Readiness = SessionReadiness.Ready,
        ReasonCode = "READY",
        UpdatedAt = Now
    };

    /// <summary>服务端在 <paramref name="receivedAt"/> 收到的、这台车这一代会话的一条心跳。</summary>
    internal static ProtocolInboxRow HeardFrom(string agvId, long generation, DateTimeOffset receivedAt) => new()
    {
        MessageId = Guid.NewGuid().ToString("D"),
        MessageType = "Heartbeat",
        RequestJson = JsonSerializer.Serialize(new { messageType = "Heartbeat", agvId, sessionGeneration = generation }),
        ContentHash = new string('0', 64),
        FirstResponseJson = "{}",
        ReceivedAt = receivedAt
    };

    /// <summary>
    /// 数从 <c>ProtocolInbox</c> 读回了多少行：每条查询收件箱的命令释放读取器时，加上它的 <c>Read()</c> 调用次数。
    /// </summary>
    /// <remarks>
    /// EF 的 <see cref="DataReaderDisposingEventData.ReadCount"/> 数的是调用次数，读到底的查询还有最后一次答 <see langword="false"/> 的调用，
    /// 所以一条读完 N 行的查询记 N + 1；中途停下的查询记它读过的行数。
    /// </remarks>
    internal sealed class InboxRowsRead : DbCommandInterceptor
    {
        private int rows;

        public int Rows => rows;

        public void Reset() => Interlocked.Exchange(ref rows, 0);

        public override InterceptionResult DataReaderDisposing(
            DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
        {
            if (command.CommandText.Contains("\"ProtocolInbox\"", StringComparison.Ordinal))
            {
                Interlocked.Add(ref rows, eventData.ReadCount);
            }
            return result;
        }
    }

    private sealed class InboxDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private InboxDatabase(SqliteConnection connection, ControlServerDbContext context, InboxRowsRead reads)
        {
            this.connection = connection;
            Context = context;
            Reads = reads;
        }

        public ControlServerDbContext Context { get; }

        public InboxRowsRead Reads { get; }

        public static async Task<InboxDatabase> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(Token);
            InboxRowsRead reads = new();
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).AddInterceptors(reads).Options);
            await context.Database.EnsureCreatedAsync(Token);
            return new InboxDatabase(connection, context, reads);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
