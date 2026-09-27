using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ControlServer.FakeOnboard;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using ControlServer.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 车载机断电后重连，不需要任何人去重启服务端（control-server#276）。
/// </summary>
/// <remarks>
/// <para>
/// <b>这一类守的是「断电恢复不需要人」。</b>车载机断电不是正常关机：旧 TCP 连接既收不到 FIN 也收不到 RST，
/// 服务端那一侧的 socket 会一直开着。MVP 线（<c>ControlServer_MVP@670bdd45</c>）上这正是现场 09-21 的故障：
/// 那里一次只服务一条连接、读不设超时，半开的旧连接永远占着接收循环，新连接在 listen backlog 里排队，
/// 只有重启服务端才恢复。
/// </para>
/// <para>
/// <b>v2 上承重的只有一样东西：control-server#234 的静默关闭。</b>v2 并发服务连接，新连接能握手，但
/// <see cref="OnboardPeer"/> 规定一辆车只有一条可路由连接，旧连接还挂着时新连接会在 Attach 时被拒
/// （<c>An Onboard peer is already attached</c>）。把旧连接放掉的，是 <see cref="OnboardTcpServer"/> 读操作上的
/// 静默窗口（ADR-cross-0027，六秒无合法入站即关）。cs#276 的反事实对照把窗口临时调成一小时，新会话 40 秒
/// 内 14 次尝试全部被拒——与 MVP 同样卡死。<b>谁要改那个窗口、或改「一车一条连接」的 Attach 规则，先看这一类。</b>
/// </para>
/// <para>
/// <b>怎么造断电。</b><see cref="PowerCutRelay"/> 夹在合成车载端与服务端之间转发字节；断电时它不再往服务端转发
/// 任何东西，也永远不关服务端那一侧的 socket。服务端写过来的字节，要么读走丢掉（像内核收进发送缓冲区），
/// 要么完全不读、让发送缓冲区堆满（<see cref="ReconnectsWhileTheServerIsStillWritingIntoTheDeadConnection"/>）。
/// 车载端随后按现场的做法重连：失败就隔两秒再试。
/// </para>
/// <para>
/// <b>判据按事件，不按墙钟。</b>一个旁路观察者每 10 ms 读一次路由表，记下旧代次离开它的那一刻。要求的是：
/// 旧代次离开了路由表；此后开始的第一次尝试就成功（新代次可路由，并连续三秒不被关）。「多少秒内」不是判据：
/// 恢复时刻取决于旧连接在第几次尝试之前被关，而那是双峰的（审查实测：第 3 次约 6 秒，或第 4 次约 9 秒），
/// 拿墙钟卡它会在慢机器上假红。墙钟只剩 <see cref="HangGuard"/> 一道挂死保护。
/// </para>
/// <para>
/// <b>前提也按路由表判。</b>第一次尝试之前旧代次必须还在表里，成功之前必须至少有一次尝试「握手答完、但不可
/// 路由」——这两条证明这一次确实造出了「旧连接半开占着这辆车」，而不是旧连接早已被关、测试平白绿了。只看
/// 车载端读到 <c>SessionReadiness</c> 不够：被 Attach 拒掉的那几次握手，车载端也读到了。
/// </para>
/// </remarks>
public sealed class OnboardPowerLossReconnectTests
{
    private const string AgvId = "AGV-POWER-01";
    private const string CredentialVariable = "CONTROL_SERVER_ONBOARD_CREDENTIAL_POWER_LOSS_TESTS";
    private const string Credential = "power-loss-test-credential";

    /// <summary>车载端重连失败后等多久再试，取现场车载端日志里的「将在2秒后重连」。</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>一次重连尝试（连接加五步握手）最多等多久，超过就当这次失败。</summary>
    private static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// 挂死保护：断电之后过了这么久还没恢复，就不再试、判失败。它不是恢复时间的判据。
    /// </summary>
    /// <remarks>
    /// 要比「六秒窗口 + 一次重试 + 一次尝试」大得多，免得慢机器上假红；又要小于六十秒，这样有人把窗口改成一分钟
    /// （反向验证 M2）时旧代次在它之内离不开路由表，这一类照样红。窗口的值本身由
    /// <c>OnboardSilentLivenessLossTests.TheProjectWideLivenessTimeoutIsTheSixSecondsAdrCross0027Fixes</c> 按字面钉住。
    /// 本类实测（cs#276，本机与审查员本机的 Release 构建）：旧代次约 5.5 秒离开路由表，第 3 次尝试、约 6 秒恢复。
    /// cs#276 第一步探针打印的 9.1～9.3 秒含之后 3 秒的稳定观察，恢复时刻同样约 6 秒。
    /// </remarks>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>可路由之后要连续多久不被服务端关掉，才算会话真的建起来了。</summary>
    private static readonly TimeSpan StaysUp = TimeSpan.FromSeconds(3);

    /// <summary>旧连接上什么都不再发生：不推送、服务端写来的都被读走丢掉。</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ReconnectsAfterAPowerCutThatLeftTheOldConnectionHalfOpen()
    {
        await using Rig rig = await Rig.StartAsync();
        long oldGeneration = await rig.ConnectFirstSessionAsync();

        rig.Relay.CutPower(drainServerWrites: true);
        Outcome outcome = await rig.ReconnectAfterPowerCutAsync(oldGeneration);

        outcome.AssertRecovered();
    }

    /// <summary>
    /// 断电后服务端还在往旧会话推送，而那条死连接不再有人读：发送缓冲区堆满，写操作卡住。
    /// </summary>
    /// <remarks>
    /// 这是最可能把接收一侧也拖住的形状（旧连接的写卡在发送门上），所以单列一条：它要证的是推送卡住的只是
    /// 推送本身，旧连接照样在静默窗口到期时被关掉，新会话照样建得起来。推送走 <see cref="OnboardPeer"/>，
    /// 与引擎每一轮下发同一条路。另外断言写确实卡住过至少一秒，否则这一条没造出它要的形状。
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ReconnectsWhileTheServerIsStillWritingIntoTheDeadConnection()
    {
        await using Rig rig = await Rig.StartAsync();
        long oldGeneration = await rig.ConnectFirstSessionAsync();

        rig.Relay.CutPower(drainServerWrites: false);
        using CancellationTokenSource pushing = new();
        Task pusher = rig.PushToSessionUntilCancelledAsync(oldGeneration, payloadBytes: 64 * 1024, pushing.Token);
        Outcome outcome;
        try
        {
            outcome = await rig.ReconnectAfterPowerCutAsync(oldGeneration);
        }
        finally
        {
            await pushing.CancelAsync();
            await pusher;
        }

        outcome.AssertRecovered();
        TestContext.Current.TestOutputHelper?.WriteLine($"断电后最长的一次推送：{rig.LongestPush.TotalMilliseconds:F0} ms");
        Assert.True(
            rig.LongestPush >= TimeSpan.FromSeconds(1),
            $"断电后最长的一次推送只挂了 {rig.LongestPush.TotalMilliseconds:F0} ms：写没有卡住，这一条没造出它要的形状。");
    }

    /// <summary>一次重连尝试的结局。</summary>
    private enum AttemptResult
    {
        /// <summary>连接或握手本身失败了。</summary>
        Failed,

        /// <summary>车载端读完了握手的答复，但服务端没让新连接可路由（被 Attach 拒掉）。</summary>
        AnsweredButNotRoutable,

        /// <summary>新代次可路由。</summary>
        Routable
    }

    private sealed record Attempt(int Number, TimeSpan StartedAfterCut, AttemptResult Result, string Detail);

    /// <summary>一次断电重连的结果，以及判它要用的事件时刻。</summary>
    private sealed record Outcome(
        bool OldGenerationRoutableBeforeFirstAttempt,
        TimeSpan? OldGenerationLeftAfterCut,
        IReadOnlyList<Attempt> Attempts,
        TimeSpan? RoutableAfterCut,
        bool StayedUp)
    {
        public void AssertRecovered()
        {
            string trace = Trace();
            // Written on success too, so a green run's evidence shows when the old connection was let go.
            TestContext.Current.TestOutputHelper?.WriteLine(trace.TrimStart());
            // Premise: the power cut really left the old connection holding this vehicle.
            Assert.True(
                OldGenerationRoutableBeforeFirstAttempt,
                "第一次重连之前旧代次已经不在路由表里：这一次没造出「旧连接半开占着这辆车」，结论不算数。" + trace);
            Assert.True(
                Attempts.Any(attempt => attempt.Result == AttemptResult.AnsweredButNotRoutable),
                "没有一次尝试是「握手答完、但不可路由」：旧连接没挡过新连接，这一次没造出要测的状态。" + trace);

            // The old connection was let go at all. MVP-style stuck, and every mutant that keeps the old
            // connection routable, fails here.
            Assert.True(
                OldGenerationLeftAfterCut is not null,
                $"断电后 {HangGuard.TotalSeconds} 秒内旧代次一直没离开路由表：半开的旧连接没被放掉，" +
                "这正是 MVP 线上要重启服务端才能恢复的故障（control-server#276）。" + trace);

            // Once it was, the very next attempt gets in: nothing else stands between the vehicle and a session.
            Attempt? firstAfterRelease = Attempts.FirstOrDefault(attempt => attempt.StartedAfterCut >= OldGenerationLeftAfterCut);
            Attempt? success = Attempts.FirstOrDefault(attempt => attempt.Result == AttemptResult.Routable);
            Assert.True(
                success is not null && (firstAfterRelease is null || success.Number <= firstAfterRelease.Number),
                $"旧代次在 t={OldGenerationLeftAfterCut!.Value.TotalSeconds:F1}s 离开路由表，但此后开始的第一次尝试没有成功。" + trace);
            Assert.True(
                RoutableAfterCut <= HangGuard,
                $"新会话在 t={RoutableAfterCut!.Value.TotalSeconds:F1}s 才可路由，超过了 {HangGuard.TotalSeconds} 秒的挂死保护。" + trace);
            Assert.True(StayedUp, "新会话可路由之后又被服务端关掉了。" + trace);
        }

        private string Trace() =>
            Environment.NewLine +
            $"旧代次离开路由表：{(OldGenerationLeftAfterCut is { } left ? $"t={left.TotalSeconds:F1}s" : "没有")}" +
            Environment.NewLine +
            string.Join(Environment.NewLine, Attempts.Select(attempt =>
                $"第 {attempt.Number} 次，t={attempt.StartedAfterCut.TotalSeconds:F1}s 开始：{attempt.Result} {attempt.Detail}"));
    }

    /// <summary>
    /// 一台真起在回环上的监听器（生产构造函数，即生产的静默窗口），真的消息处理器与内存库，合成车载端，
    /// 以及夹在中间的断电中继。
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        // Read by reflection rather than through a new accessor on OnboardPeer: this ticket changes no product
        // code. A rename fails loudly here instead of making the class pass.
        private static readonly FieldInfo ConnectionsField = typeof(OnboardPeer).GetField(
            "_connections", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("OnboardPeer._connections 改名了，这一类读路由表的办法要跟着改。");

        private static readonly FieldInfo GateField = typeof(OnboardPeer).GetField(
            "_gate", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("OnboardPeer._gate 改名了，这一类读路由表的办法要跟着改。");

        private readonly SqliteConnection _connection;
        private readonly ControlServerDbContext _context;
        private readonly ServiceProvider _provider;
        private readonly OnboardTcpServer _server;
        private long _longestPushTicks;

        private Rig(
            SqliteConnection connection,
            ControlServerDbContext context,
            ServiceProvider provider,
            OnboardTcpServer server,
            OnboardPeer peer,
            PowerCutRelay relay,
            CommandEngine<FakeOnboardState> engine,
            OnboardPeerSession onboard)
        {
            _connection = connection;
            _context = context;
            _provider = provider;
            _server = server;
            Peer = peer;
            Relay = relay;
            Engine = engine;
            Onboard = onboard;
        }

        public OnboardPeer Peer { get; }

        public PowerCutRelay Relay { get; }

        public CommandEngine<FakeOnboardState> Engine { get; }

        public OnboardPeerSession Onboard { get; }

        /// <summary>断电之后，最长的一次推送挂了多久（成功、被拒、被取消都算）。</summary>
        public TimeSpan LongestPush => TimeSpan.FromTicks(Interlocked.Read(ref _longestPushTicks));

        public static async Task<Rig> StartAsync()
        {
            Environment.SetEnvironmentVariable(CredentialVariable, Credential);
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = CredentialVariable
                })
                .Build();
            OnboardPeer peer = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), TimeProvider.System, configuration, peer);
            ServiceCollection services = new();
            services.AddScoped(_ => processor);
            ServiceProvider provider = services.BuildServiceProvider();

            int serverPort = ReserveFreePort();
            // The composition root's constructor on purpose: the window under test is the one production
            // runs with, not one a test chose.
            OnboardTcpServer server = new(
                Options.Create(new OnboardTransportOptions
                {
                    Enabled = true,
                    ListenAddress = "127.0.0.1",
                    Port = serverPort
                }),
                provider.GetRequiredService<IServiceScopeFactory>(),
                peer,
                NullLogger<OnboardTcpServer>.Instance);
            await server.StartAsync(TestContext.Current.CancellationToken);

            PowerCutRelay relay = PowerCutRelay.Start(serverPort);
            CommandEngine<FakeOnboardState> engine = new("power-loss-test", () => new FakeOnboardState());
            OnboardPeerSession onboard = new(
                engine,
                new FakeOnboardOptions
                {
                    Port = relay.Port,
                    AgvId = AgvId,
                    CredentialEnvironmentVariable = CredentialVariable
                },
                SlotStateSeed.Read(new ConfigurationBuilder().Build()));
            return new Rig(connection, context, provider, server, peer, relay, engine, onboard);
        }

        /// <summary>The first session, routable, with its heartbeats flowing as a live session's do.</summary>
        public async Task<long> ConnectFirstSessionAsync()
        {
            await Onboard.StartAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            long generation = Engine.Snapshot().State.SessionGeneration;
            Assert.True(
                await WaitRoutableAsync(generation, TimeSpan.FromSeconds(5)),
                "第一代会话一直没可路由，断电之前的前提就没成立。");
            // Past the first heartbeat: the connection has been live, not merely handshaken.
            await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);
            return generation;
        }

        /// <summary>The vehicle comes back after a power cut and retries the way the field onboard does.</summary>
        public async Task<Outcome> ReconnectAfterPowerCutAsync(long oldGeneration)
        {
            // The onboard process died with the power: drop its local session. The relay keeps the server's
            // side of that connection open.
            await Onboard.DisconnectAsync();
            Stopwatch sinceCut = Stopwatch.StartNew();

            // The event the verdict hangs on: the moment the old generation stops being routable. Sampled
            // beside the attempts rather than between them, so it is timed to within a poll, not an attempt.
            using CancellationTokenSource watching = new();
            TimeSpan? oldLeftAt = null;
            Task watcher = Task.Run(async () =>
            {
                while (!watching.IsCancellationRequested)
                {
                    if (RoutableGeneration() != oldGeneration)
                    {
                        oldLeftAt = sinceCut.Elapsed;
                        return;
                    }
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(10), watching.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }, CancellationToken.None);

            bool oldRoutableBeforeFirstAttempt = RoutableGeneration() == oldGeneration;
            List<Attempt> attempts = [];
            TimeSpan? routableAfter = null;
            bool stayedUp = false;
            try
            {
                while (sinceCut.Elapsed < HangGuard)
                {
                    int number = attempts.Count + 1;
                    TimeSpan startedAt = sinceCut.Elapsed;
                    (AttemptResult result, string detail) = await AttemptAsync();
                    attempts.Add(new Attempt(number, startedAt, result, detail));
                    if (result == AttemptResult.Routable)
                    {
                        routableAfter = sinceCut.Elapsed;
                        stayedUp = await StaysConnectedAsync(StaysUp);
                        break;
                    }
                    await DisconnectQuietlyAsync();
                    await Task.Delay(RetryInterval, TestContext.Current.CancellationToken);
                }
            }
            finally
            {
                await watching.CancelAsync();
                await watcher;
            }
            return new Outcome(oldRoutableBeforeFirstAttempt, oldLeftAt, attempts, routableAfter, stayedUp);
        }

        /// <summary>One reconnect: connect, the five-step handshake, then whether the server made it routable.</summary>
        private async Task<(AttemptResult Result, string Detail)> AttemptAsync()
        {
            Task handshake = Onboard.ReconnectAsync();
            try
            {
                await handshake.WaitAsync(AttemptBudget, TestContext.Current.CancellationToken);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException
                                              or InvalidOperationException or TimeoutException)
            {
                // WaitAsync only stops waiting; the handshake itself is still running and owns the peer's fields.
                // Let it finish (bounded) before anything tears the session down, or the two race on the same
                // socket and writer and turn a clean verdict into an unreadable exception.
                try
                {
                    await handshake.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                }
                catch (Exception)
                {
                    // Whatever it ended with, it has ended (or the guard gave up); the attempt already failed.
                }
                return (AttemptResult.Failed, $"{error.GetType().Name}: {error.Message}");
            }

            long generation = Engine.Snapshot().State.SessionGeneration;
            return await WaitRoutableAsync(generation, TimeSpan.FromSeconds(1))
                ? (AttemptResult.Routable, $"第 {generation} 代")
                : (AttemptResult.AnsweredButNotRoutable, $"第 {generation} 代");
        }

        private async Task DisconnectQuietlyAsync()
        {
            try
            {
                await Onboard.DisconnectAsync();
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException
                                              or InvalidOperationException)
            {
                // The session is gone either way; the next attempt starts from StartAsync.
            }
        }

        /// <summary>Pushes to the given session through the peer until cancelled, as the runtime's rounds do.</summary>
        public Task PushToSessionUntilCancelledAsync(long generation, int payloadBytes, CancellationToken cancellationToken) =>
            Task.Run(async () =>
            {
                string padding = new('x', payloadBytes);
                while (!cancellationToken.IsCancellationRequested)
                {
                    byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    {
                        messageType = "FaultCargoRecoveryCommand",
                        messageId = Guid.NewGuid().ToString("D"),
                        agvId = AgvId,
                        sessionGeneration = generation,
                        padding
                    }) + "\n");
                    Stopwatch took = Stopwatch.StartNew();
                    try
                    {
                        await Peer.SendAsync(line, cancellationToken);
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException
                                                      or ObjectDisposedException or SocketException)
                    {
                        // Refused once the connection is gone or the session moved on, or stuck and cancelled:
                        // the runtime sees the same and leaves the outbox row for a replay.
                    }
                    RecordPush(took.Elapsed);
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }, CancellationToken.None);

        private void RecordPush(TimeSpan took)
        {
            long seen = Interlocked.Read(ref _longestPushTicks);
            while (took.Ticks > seen)
            {
                long previous = Interlocked.CompareExchange(ref _longestPushTicks, took.Ticks, seen);
                if (previous == seen)
                {
                    return;
                }
                seen = previous;
            }
        }

        private long? RoutableGeneration()
        {
            object connections = ConnectionsField.GetValue(Peer)!;
            lock (GateField.GetValue(Peer)!)
            {
                Dictionary<string, (OnboardPeerConnection Connection, long SessionGeneration)> table =
                    (Dictionary<string, (OnboardPeerConnection Connection, long SessionGeneration)>)connections;
                return table.TryGetValue(AgvId, out (OnboardPeerConnection Connection, long SessionGeneration) entry)
                    ? entry.SessionGeneration
                    : null;
            }
        }

        private async Task<bool> WaitRoutableAsync(long generation, TimeSpan within)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < within)
            {
                if (RoutableGeneration() == generation)
                {
                    return true;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
            }
            return false;
        }

        private async Task<bool> StaysConnectedAsync(TimeSpan forHowLong)
        {
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed < forHowLong)
            {
                if (!Onboard.IsConnected)
                {
                    return false;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            }
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Onboard.DisposeAsync();
            }
            finally
            {
                try
                {
                    await Relay.DisposeAsync();
                }
                finally
                {
                    try
                    {
                        await _server.StopAsync(CancellationToken.None);
                        _server.Dispose();
                    }
                    finally
                    {
                        await _provider.DisposeAsync();
                        await _context.DisposeAsync();
                        await _connection.DisposeAsync();
                    }
                }
            }
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

    /// <summary>
    /// 按字节转发车载端的连接。<see cref="CutPower"/> 把当前那一条冻住：从此不再往服务端转发任何东西，服务端那一侧
    /// 的 socket 也永远不关——没有 FIN，没有 RST，正是断电留下的样子。
    /// </summary>
    private sealed class PowerCutRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _serverPort;
        private readonly CancellationTokenSource _stopping = new();
        private readonly List<IDisposable> _owned = [];
        private readonly List<Task> _links = [];
        private volatile Link? _current;
        private Task _accepting = Task.CompletedTask;

        private PowerCutRelay(TcpListener listener, int serverPort)
        {
            _listener = listener;
            _serverPort = serverPort;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static PowerCutRelay Start(int serverPort)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            PowerCutRelay relay = new(listener, serverPort);
            relay._accepting = relay.AcceptAsync();
            return relay;
        }

        /// <param name="drainServerWrites">
        /// true：服务端写来的字节读走丢掉。false：一概不读，服务端的发送缓冲区会堆满、写操作卡住。
        /// </param>
        public void CutPower(bool drainServerWrites)
        {
            Link link = _current ?? throw new InvalidOperationException("没有可以断电的连接。");
            link.DrainServerWrites = drainServerWrites;
            link.Cut = true;
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    TcpClient vehicle = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    TcpClient server = new();
                    lock (_owned)
                    {
                        _owned.Add(vehicle);
                        _owned.Add(server);
                    }
                    await server.ConnectAsync(IPAddress.Loopback, _serverPort, _stopping.Token);
                    Link link = new(vehicle, server);
                    _current = link;
                    lock (_links)
                    {
                        _links.Add(link.RunAsync(_stopping.Token));
                    }
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Torn down.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            lock (_owned)
            {
                foreach (IDisposable owned in _owned)
                {
                    owned.Dispose();
                }
            }
            await _accepting;
            Task[] links;
            lock (_links)
            {
                links = [.. _links];
            }
            await Task.WhenAll(links);
            _stopping.Dispose();
        }

        private sealed class Link(TcpClient vehicle, TcpClient server)
        {
            public volatile bool Cut;

            public volatile bool DrainServerWrites = true;

            public Task RunAsync(CancellationToken token) => Task.WhenAll(ToServerAsync(token), ToVehicleAsync(token));

            private async Task ToServerAsync(CancellationToken token)
            {
                byte[] buffer = new byte[65536];
                try
                {
                    NetworkStream from = vehicle.GetStream();
                    NetworkStream to = server.GetStream();
                    while (true)
                    {
                        int read = await from.ReadAsync(buffer, token);
                        if (Cut)
                        {
                            // Power is gone: nothing more reaches the server, and its side is never closed.
                            return;
                        }
                        if (read == 0)
                        {
                            // Client is null once the relay's teardown has disposed this socket.
                            server.Client?.Shutdown(SocketShutdown.Send);
                            return;
                        }
                        await to.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException
                                                  or ObjectDisposedException or SocketException)
                {
                    if (!Cut)
                    {
                        server.Dispose();
                    }
                }
            }

            private async Task ToVehicleAsync(CancellationToken token)
            {
                byte[] buffer = new byte[65536];
                try
                {
                    NetworkStream from = server.GetStream();
                    while (true)
                    {
                        while (Cut && !DrainServerWrites)
                        {
                            // Not reading at all: the server's send buffer fills and its writes stall. The
                            // relay's end stays open until the relay itself is disposed.
                            await Task.Delay(TimeSpan.FromMilliseconds(100), token);
                        }
                        int read = await from.ReadAsync(buffer, token);
                        if (read == 0)
                        {
                            if (!Cut)
                            {
                                vehicle.Dispose();
                            }
                            return;
                        }
                        if (Cut)
                        {
                            continue;
                        }
                        await vehicle.GetStream().WriteAsync(buffer.AsMemory(0, read), token);
                    }
                }
                catch (Exception error) when (error is IOException or OperationCanceledException
                                                  or ObjectDisposedException or SocketException)
                {
                    if (!Cut)
                    {
                        vehicle.Dispose();
                    }
                }
            }
        }
    }
}
