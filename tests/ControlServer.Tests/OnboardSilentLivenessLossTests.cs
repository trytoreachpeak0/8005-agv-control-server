using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 服务端按 ADR-cross-0027 判车载端静默失联：连接没断、报文停了，六秒之后服务端自己关掉这条连接
/// （control-server#234）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这是个缺陷而不是新功能。</b>车载端进程卡死而 TCP 没断时，服务端此前没有任何判定：
/// <c>OnboardMessageProcessor</c> 收到 <c>Heartbeat</c> 只回 <c>HeartbeatAck</c>，
/// <c>WireToGateStore.RecordConnectionLossAsync</c> 一个产品调用方都没有，
/// <c>OnboardTcpServer.HandleClientAsync</c> 的 <c>finally</c> 也只把连接从 <c>OnboardPeer</c> 上摘掉、
/// 不写库。所以会话行一直是 <c>Ready</c>，车静默卡死而看板上什么都没有。
/// </para>
/// <para>
/// <b>两个时钟，同一个阈值。</b>这里的计时是本机单调时钟（ADR-cross-0027 要求），
/// <see cref="SessionLiveness"/> 那一侧是数据库收件时间与当前 UTC 相减；阈值同源，判定时钟不同源，
/// 是有意的。见 <see cref="OnboardConnectionLiveness"/> 的类注释。
/// </para>
/// <para>
/// <b>判据不贴墙钟。</b>阈值两侧（5.9 秒与 6 秒整）在注入的单调时钟上判，一秒真实时间都不花；
/// 真起 TCP 的两条等的是「读到 EOF」这个事实，不是等够多少毫秒，所以机器再慢也只会晚一点通过。
/// </para>
/// </remarks>
public sealed class OnboardSilentLivenessLossTests
{
    private const string AgvId = "AGV-01";
    private const string CredentialVariable = "CONTROL_SERVER_ONBOARD_CREDENTIAL_LIVENESS_TESTS";
    private const string Credential = "liveness-test-credential";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    // --- 阈值本身 ------------------------------------------------------------------------------------

    /// <summary>
    /// ADR-cross-0027 的六秒，写死在这里而不是从 <see cref="SessionLiveness.Timeout"/> 取。
    /// </summary>
    /// <remarks>
    /// 下面两侧的判据如果拿那个常量去算，把常量改成十二秒它们会跟着改、一条都不红——一条只在无人动它
    /// 时才成立的护栏等于没有护栏。所以阈值的值由这一条钉住，两侧的行为由下一条钉住，改任何一个都有
    /// 东西会响。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public void TheProjectWideLivenessTimeoutIsTheSixSecondsAdrCross0027Fixes() =>
        Assert.Equal(TimeSpan.FromSeconds(6), SessionLiveness.Timeout);

    /// <summary>
    /// 引擎那一侧的窗口与会话层这一侧的窗口是同一个值（control-server#234）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>这是早退能放在推进之前的全部依据。</b><c>JourneyRuntimeEngine.NameSilentOnboardSessionAsync</c> 在
    /// 判失联的那一轮直接返回、不再推进，而它敢这么做只因为：到那一刻会话层已经按同一个窗口关掉了连接，
    /// 这一轮本来也只会从 <c>ReplayPendingForSessionAsync</c> 抛出去。**引擎这一侧的窗口一旦长过会话层，
    /// 那个前提就不成立**——下面的分支会对着一个还连着的对端被拦下，其中 <c>ObserveOrderFailureAsync</c>
    /// 判的是 RIoT 事实，不需要车载端在线。
    /// </para>
    /// <para>
    /// 今天两侧都读 <see cref="SessionLiveness.Timeout"/>，所以不会意外分叉；这一条是把「不会意外」变成
    /// 「改了会响」。它断的是两个值相等，不是各自等于六——各自等于六由上面那一条断，两条合起来，
    /// 改任何一处都有东西红。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public void TheEngineAndTheSessionLayerMeasureTheSameWindow()
    {
        using ServiceProvider empty = new ServiceCollection().BuildServiceProvider();
        using OnboardTcpServer server = new(
            Options.Create(new OnboardTransportOptions()),
            empty.GetRequiredService<IServiceScopeFactory>(),
            new OnboardPeer(),
            NullLogger<OnboardTcpServer>.Instance);

        // 会话层这一侧：连接闲置多久就关。
        TimeSpan sessionLayer = server.IdleTimeout;
        // 引擎那一侧：SessionLiveness.HeardFromAsync 判「听不听得到」用的窗口，引擎不另外拿一个值。
        TimeSpan engine = SessionLiveness.Timeout;

        Assert.Equal(engine, sessionLayer);
        Assert.True(
            engine <= sessionLayer,
            "引擎的窗口长过会话层，早退放在推进之前的前提就不成立了。");
    }

    /// <summary>服务端拿的就是那个项目级的值，不另有一份。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public void TheListenerTakesItsIdleTimeoutFromTheProjectWideValue()
    {
        using ServiceProvider empty = new ServiceCollection().BuildServiceProvider();
        using OnboardTcpServer server = new(
            Options.Create(new OnboardTransportOptions()),
            empty.GetRequiredService<IServiceScopeFactory>(),
            new OnboardPeer(),
            NullLogger<OnboardTcpServer>.Instance);

        Assert.Equal(SessionLiveness.Timeout, server.IdleTimeout);
    }

    // --- 计时：阈值两侧、任何入站都刷新 ----------------------------------------------------------------

    /// <summary>五点九秒还没失联，六秒整已经失联。边界取「满」，与看板升级线同一条规矩。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public void SilenceJustUnderTheTimeoutIsNotLostAndSilenceAtTheTimeoutIs()
    {
        MonotonicTestClock clock = new();
        OnboardConnectionLiveness liveness = new(clock, TimeSpan.FromSeconds(6));

        clock.Advance(TimeSpan.FromMilliseconds(5900));
        Assert.False(liveness.Expired);
        Assert.Equal(TimeSpan.FromMilliseconds(100), liveness.Remaining);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(liveness.Expired);
        Assert.Equal(TimeSpan.Zero, liveness.Remaining);

        // 过期之后时间继续走，剩余时间不会绕回一个可用的值。
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(liveness.Expired);
        Assert.Equal(TimeSpan.Zero, liveness.Remaining);
    }

    /// <summary>
    /// 任何合法入站都把计时归零，不只是心跳——ADR-cross-0027 说的是「属于当前会话的合法协议消息」。
    /// </summary>
    /// <remarks>
    /// 刷新之后再等满一个完整的窗口才失联，而不是从最初那一条算起。这一条与上一条合起来，是把到期判据
    /// 改错（比如改成从连接建立起算、或把窗口乘几倍）会红的地方。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public void AnyInboundRefreshesTheDeadlineAndTheWindowStartsOverFromIt()
    {
        MonotonicTestClock clock = new();
        OnboardConnectionLiveness liveness = new(clock, TimeSpan.FromSeconds(6));

        clock.Advance(TimeSpan.FromSeconds(5));
        liveness.Refresh();

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(liveness.Expired);
        Assert.Equal(TimeSpan.FromSeconds(5), liveness.Silence);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(liveness.Expired);
    }

    /// <summary>零或负的窗口是配置错误，不是「永不失联」。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public void ANonPositiveTimeoutIsRefusedRatherThanMeaningNeverExpires()
    {
        MonotonicTestClock clock = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => new OnboardConnectionLiveness(clock, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OnboardConnectionLiveness(clock, TimeSpan.FromSeconds(-1)));
    }

    // --- 数据库口径：单车版与批量版是同一条规则 ----------------------------------------------------------

    /// <summary>
    /// <see cref="SessionLiveness"/> 的单车版与批量版对同一批入站给出一致的答案，四条规则逐条对齐。
    /// </summary>
    /// <remarks>
    /// 单车版是 control-server#234 加的，加它是因为旅程运行时要按车判失联，而在引擎里把判定再写一遍
    /// 正是那个类禁止的「各自再判一次」。两个版本共用同一对谓词，这一条钉住它们不分叉——尤其是
    /// 「收件时间晚于此刻的不算」这种容易在第二份实现里漏掉的细节。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ThePerVehicleLivenessReadAgreesWithTheFleetReadOnEveryRule()
    {
        await using LivenessDatabase database = await LivenessDatabase.CreateAsync();
        database.Context.SessionRecoveries.Add(Session("AGV-IN-WINDOW", generation: 4));
        database.Context.SessionRecoveries.Add(Session("AGV-STALE", generation: 4));
        database.Context.SessionRecoveries.Add(Session("AGV-FUTURE", generation: 4));
        database.Context.SessionRecoveries.Add(Session("AGV-OLD-GENERATION", generation: 9));
        database.Context.SessionRecoveries.Add(Session("AGV-NO-GENERATION", generation: 4));

        // 窗口之内：算听得到。
        database.Context.ProtocolInbox.Add(Inbound("AGV-IN-WINDOW", 4, Now.AddSeconds(-1)));
        // 窗口之外一点点：不算。
        database.Context.ProtocolInbox.Add(
            Inbound("AGV-STALE", 4, Now - SessionLiveness.Timeout - TimeSpan.FromMilliseconds(1)));
        // 收件时间在未来：不算——一个走快的时钟不能让死会话看起来活着。
        database.Context.ProtocolInbox.Add(Inbound("AGV-FUTURE", 4, Now.AddSeconds(1)));
        // 上一代会话的消息再新也不替这一代说话。
        database.Context.ProtocolInbox.Add(Inbound("AGV-OLD-GENERATION", 8, Now));
        // 会话代还没分配的握手第一条，证不了任何一代。
        database.Context.ProtocolInbox.Add(Inbound("AGV-NO-GENERATION", null, Now));
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        HashSet<string> fleet = await SessionLiveness.HeardFromAsync(
            database.Context, Now, TestContext.Current.CancellationToken);

        Assert.Equal(["AGV-IN-WINDOW"], fleet.Order(StringComparer.Ordinal));

        (string AgvId, long Generation)[] vehicles =
        [
            ("AGV-IN-WINDOW", 4L), ("AGV-STALE", 4L), ("AGV-FUTURE", 4L),
            ("AGV-OLD-GENERATION", 9L), ("AGV-NO-GENERATION", 4L)
        ];
        foreach ((string agvId, long generation) in vehicles)
        {
            bool perVehicle = await SessionLiveness.HeardFromAsync(
                database.Context, agvId, generation, Now, TestContext.Current.CancellationToken);
            Assert.Equal(fleet.Contains(agvId), perVehicle);
        }
    }

    /// <summary>
    /// 整点那一下：数据库口径算「还听得到」，单调时钟口径算「已失联」——两侧在边界上判定相反，而这是
    /// 有方向的，不是疏忽（control-server#234）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SessionLiveness.InsideWindow</c> 是 <c>now - ReceivedAt &lt;= Timeout</c>（既有写法，两个看板消费者
    /// 一直用着，本票不动它）；<see cref="OnboardConnectionLiveness.Expired"/> 是 <c>Silence &gt;= Timeout</c>
    /// （ADR-cross-0027 说的是「连续六秒没有合法消息即判失联」，整点就算）。
    /// </para>
    /// <para>
    /// <b>差在哪一边是要紧的。</b>整点这一瞬间，会话层已经判失联、要关连接，而引擎那一侧还认为听得到、
    /// 不会早退。也就是<b>会话层比引擎激进一拍</b>——正是「引擎的窗口不得长过会话层」要的那个方向。
    /// 反过来（引擎先判、会话层还没关）才会让早退拦下一轮本来能跑的推进。
    /// </para>
    /// <para>
    /// 这一条存在的理由是：两侧的边界写法此前都没有整点样本钉着，把任一处的 <c>&lt;=</c> 改成 <c>&lt;</c>、
    /// 或把 <c>&gt;=</c> 改成 <c>&gt;</c>，都不会有东西红，而其中一种改法会把上面那个方向翻过来。
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task AtTheExactBoundaryTheDatabaseSideStillHearsItWhileTheMonotonicSideCallsItLost()
    {
        // 单调时钟这一侧：整点算失联。
        MonotonicTestClock clock = new();
        OnboardConnectionLiveness liveness = new(clock, SessionLiveness.Timeout);
        clock.Advance(SessionLiveness.Timeout);
        Assert.True(liveness.Expired);

        // 数据库这一侧：整点算还听得到。
        await using LivenessDatabase database = await LivenessDatabase.CreateAsync();
        database.Context.SessionRecoveries.Add(Session("AGV-BOUNDARY", generation: 4));
        database.Context.ProtocolInbox.Add(Inbound("AGV-BOUNDARY", 4, Now - SessionLiveness.Timeout));
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(await SessionLiveness.HeardFromAsync(
            database.Context, "AGV-BOUNDARY", 4, Now, TestContext.Current.CancellationToken));
        Assert.Contains(
            "AGV-BOUNDARY",
            await SessionLiveness.HeardFromAsync(database.Context, Now, TestContext.Current.CancellationToken));

        // 再晚一毫秒，数据库这一侧也不认了——证明上面那个 true 是边界本身给的，不是窗口根本没起作用。
        Assert.False(await SessionLiveness.HeardFromAsync(
            database.Context,
            "AGV-BOUNDARY",
            4,
            Now + TimeSpan.FromMilliseconds(1),
            TestContext.Current.CancellationToken));
    }

    // --- 会话层：静默到期就关连接，关掉之后不再读这条连接 ------------------------------------------------

    /// <summary>连接开着、一条报文都不来，窗口一过服务端自己关掉它。</summary>
    /// <remarks>
    /// 等的是客户端那一端读到 EOF 这个事实，不是等够多少毫秒，所以机器负载再高也只是晚一点收到。
    /// 窗口在这里传的是很短的一个值：六秒是项目级的量，由上面那一条钉，这一条要证的是「到期了会关」。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ASilentConnectionIsClosedByTheServerOnceTheWindowPasses()
    {
        await using LivenessRig rig = await LivenessRig.StartAsync(TimeSpan.FromMilliseconds(250));

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, rig.Port, TestContext.Current.CancellationToken);
        using StreamReader reader = new(client.GetStream(), Encoding.UTF8, leaveOpen: true);

        string? afterTheWindow = await ReadWithinGuardAsync(
            reader, "服务端没有关闭这条静默的连接：既没有数据也没有 EOF。");

        Assert.Null(afterTheWindow);
    }

    /// <summary>关掉之后从那条连接上寄来的报文一条都不处理：迟到的报文不复活会话。</summary>
    /// <remarks>
    /// <b>带对照，否则这条判据可能假绿。</b>「收件箱里没有那条 messageId」也可能是因为那一行根本不合法、
    /// 服务端读了也不会记。所以同一条测试里先用一条**一模一样构造**的报文在活着的连接上证明它确实会进
    /// 收件箱，再看静默关闭之后那一条不进去。两条只差「连接还在不在」。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task NothingSentAfterTheCloseIsProcessed()
    {
        await using LivenessRig rig = await LivenessRig.StartAsync(TimeSpan.FromMilliseconds(250));

        // 对照：连接活着的时候，同样构造的一行会进收件箱。
        string acceptedMessageId = Guid.NewGuid().ToString("D");
        using (TcpClient live = new())
        {
            await live.ConnectAsync(IPAddress.Loopback, rig.Port, TestContext.Current.CancellationToken);
            NetworkStream liveStream = live.GetStream();
            StreamWriter writer = new(liveStream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            using StreamReader reader = new(liveStream, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(
                SessionHello(acceptedMessageId).AsMemory(), TestContext.Current.CancellationToken);
            // 等的是服务端答复这个事实，不是等够多少毫秒。
            string? accepted = await ReadWithinGuardAsync(
                reader, "对照那一半没成立：服务端对活着的连接上这一行没有任何答复。");
            Assert.NotNull(accepted);
        }

        // 被静默关闭之后再寄的一行。
        string lateMessageId = Guid.NewGuid().ToString("D");
        using (TcpClient late = new())
        {
            await late.ConnectAsync(IPAddress.Loopback, rig.Port, TestContext.Current.CancellationToken);
            NetworkStream lateStream = late.GetStream();
            using (StreamReader reader = new(lateStream, Encoding.UTF8, leaveOpen: true))
            {
                Assert.Null(await ReadWithinGuardAsync(
                    reader, "服务端没有关闭这条静默的连接，后面那一半无从谈起。"));
            }
            StreamWriter writer = new(lateStream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            try
            {
                await writer.WriteLineAsync(
                    SessionHello(lateMessageId).AsMemory(), TestContext.Current.CancellationToken);
            }
            catch (IOException)
            {
                // 对端已经关了，写不进去也是一种「没被处理」。
            }
        }

        string[] recorded = await rig.Context.ProtocolInbox.AsNoTracking()
            .Select(row => row.MessageId)
            .ToArrayAsync(TestContext.Current.CancellationToken);

        Assert.Contains(acceptedMessageId, recorded);
        Assert.DoesNotContain(lateMessageId, recorded);
    }

    // --- 辅助 ----------------------------------------------------------------------------------------

    /// <summary>
    /// 读一行，带一个防挂死的上限。
    /// </summary>
    /// <remarks>
    /// 这个上限不是判据的一部分：判据是「读到 EOF」这个事实，上限只保证行为缺失时这条测试以一句人话失败，
    /// 而不是挂到作业超时——挂住的测试在 self-hosted runner 上是要卡死会话的（这个仓只有一个 runner）。
    /// 所以它取得远大于场景里那个几百毫秒的窗口，机器再慢也不会因为它假红。
    /// </remarks>
    private static async Task<string?> ReadWithinGuardAsync(StreamReader reader, string whatDidNotHappen)
    {
        using CancellationTokenSource guard =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        guard.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await reader.ReadLineAsync(guard.Token);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            Assert.Fail(whatDidNotHappen + "（三十秒上限到了。）");
            throw;
        }
    }

    private static SessionRecoveryRow Session(string agvId, long generation) => new()
    {
        AgvId = agvId,
        SessionGeneration = generation,
        ProtocolCommit = ProtocolCandidateIdentity.RepositoryCommit,
        ManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
        ProfileId = ProtocolCandidateIdentity.ProfileId,
        ProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
        Readiness = SessionReadiness.Ready,
        ReasonCode = "READY",
        UpdatedAt = Now
    };

    private static ProtocolInboxRow Inbound(string agvId, long? generation, DateTimeOffset receivedAt)
    {
        string messageId = Guid.NewGuid().ToString("D");
        return new ProtocolInboxRow
        {
            MessageId = messageId,
            MessageType = "Heartbeat",
            RequestJson = JsonSerializer.Serialize(new
            {
                messageType = "Heartbeat",
                messageId,
                agvId,
                sessionGeneration = generation,
                payload = new { observedAt = receivedAt }
            }),
            ContentHash = messageId,
            FirstResponseJson = "{}",
            ReceivedAt = receivedAt
        };
    }

    private static string SessionHello(string messageId) => JsonSerializer.Serialize(new
    {
        protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
        profileId = ProtocolCandidateIdentity.ProfileId,
        protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
        protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
        messageType = "SessionHello",
        messageId,
        correlationId = (string?)null,
        agvId = AgvId,
        sessionGeneration = (long?)null,
        sentAt = Now.ToString("O", CultureInfo.InvariantCulture),
        payload = new
        {
            onboardInstanceId = Guid.NewGuid().ToString("D"),
            onboardBuildCommit = "LIVENESS_TEST",
            supportedProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseIdentity = new
            {
                repository = "8005-agv-protocol",
                releaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
                tag = ProtocolCandidateIdentity.Tag,
                commit = ProtocolCandidateIdentity.RepositoryCommit,
                protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
                profileId = ProtocolCandidateIdentity.ProfileId,
                manifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
                schemaBundleSha256 = ProtocolCandidateIdentity.SchemaBundleSha256
            },
            credentialProof = Credential
        }
    });

    /// <summary>单调时钟，只有测试推它才会走。<c>TimestampFrequency</c> 取 tick，换算就是一比一。</summary>
    private sealed class MonotonicTestClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan by) => _timestamp += by.Ticks;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class LivenessDatabase(SqliteConnection connection, ControlServerDbContext context)
        : IAsyncDisposable
    {
        public ControlServerDbContext Context { get; } = context;

        public static async Task<LivenessDatabase> CreateAsync()
        {
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new LivenessDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    /// <summary>一台真起在回环上的监听器，连着一个真的消息处理器与一个内存库。</summary>
    private sealed class LivenessRig(
        SqliteConnection connection,
        ControlServerDbContext context,
        ServiceProvider provider,
        OnboardTcpServer server) : IAsyncDisposable
    {
        public ControlServerDbContext Context { get; } = context;

        public int Port { get; private init; }

        public static async Task<LivenessRig> StartAsync(TimeSpan idleTimeout)
        {
            Environment.SetEnvironmentVariable(CredentialVariable, Credential);
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            TimeProvider clock = new FixedClock();
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = CredentialVariable
                })
                .Build();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), clock, configuration);
            ServiceCollection services = new();
            services.AddScoped(_ => processor);
            ServiceProvider provider = services.BuildServiceProvider();

            int port = ReserveFreePort();
            OnboardTcpServer server = new(
                Options.Create(new OnboardTransportOptions
                {
                    Enabled = true,
                    ListenAddress = "127.0.0.1",
                    Port = port
                }),
                provider.GetRequiredService<IServiceScopeFactory>(),
                new OnboardPeer(),
                NullLogger<OnboardTcpServer>.Instance,
                TimeProvider.System,
                idleTimeout);
            await server.StartAsync(TestContext.Current.CancellationToken);
            return new LivenessRig(connection, context, provider, server) { Port = port };
        }

        public async ValueTask DisposeAsync()
        {
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
            await provider.DisposeAsync();
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static int ReserveFreePort()
        {
            TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }
        }
    }
}
