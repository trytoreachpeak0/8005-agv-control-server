using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static ControlServer.Tests.JourneyRuntimeWorkerTestKit;
using static ControlServer.Tests.VehicleFaultRecoveryTests;

namespace ControlServer.Tests;

/// <summary>
/// 一台车的 socket 卡住，不许拖住别的车（control-server#334）：持有 <see cref="JourneyMutationGate"/> 时发往车载端的写有上限，
/// 超过上限按连接断开处理，闸门随之放开。
/// </summary>
/// <remarks>
/// <para>
/// <b>怎么造「卡住」。</b>一条真实的回环 TCP 连接，车那一端只连不读；服务端那一端先用非阻塞写把内核的发送与接收缓冲区塞满，
/// 于是之后任何一次写都会停在 socket 上，既不成功也不失败——半开连接、对端进程不读时就是这个样子。没有用一个「永不完成」的
/// 替身流：那只能证明代码会等一个我们自己造出来的等待，证明不了真实的 <see cref="NetworkStream"/> 会这样挂住。
/// </para>
/// <para>
/// <b>为什么不靠 6 秒静默关闭。</b>静默关闭（ADR-cross-0027，control-server#234/#276）只在服务端的接收循环读不到东西时触发。对端完全
/// 不说话时它确实会把连接关掉、顺带让卡住的写失败；但对端还在发心跳时，接收循环读到心跳，要写 HeartbeatAck，而这次写排在卡住的那次
/// 后面（同一条连接的发送闸门），接收循环就停在写上，读的超时永远走不到。这里连接上根本没有接收循环，对应的正是后一种情形：
/// 没有任何东西会替这次写收场。
/// </para>
/// <para>
/// <b>「另一台车」是故障清除的请求。</b>引擎整轮持锁，这一轮停在车 A 的发送上；车 B 的人工清除要拿同一把锁。车 B 没有故障，
/// 所以拿到锁时它得到的是 <c>FAULT_RECOVERY_FAULT_NOT_IN_EFFECT</c>，拿不到才是 <c>FAULT_RECOVERY_RUNTIME_BUSY</c>——两个结果只差
/// 「锁拿没拿到」这一件事。
/// </para>
/// </remarks>
public sealed class GateHeldOnboardSendTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>车 A 的这一轮等这么久还没走完，就算挂住了。远大于任何合理的写超时，远小于测试框架的超时。</summary>
    private static readonly TimeSpan RoundBudget = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task AVehicleWhoseSocketStopsReadingDoesNotHoldTheGateForTheRestOfTheFleet()
    {
        await using RuntimeFixture fixture = await FaultedOnTheWayToPickupAsync();
        await using StuckPeer stuck = await StuckPeer.OpenAsync();
        TaskCompletionSource sendEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Peer.OnMessageSent = async line =>
        {
            sendEntered.TrySetResult();
            // The engine's own token: the worker passes stoppingToken, which only a shutdown cancels.
            await stuck.Connection.SendAsync(OnboardPeerConnection.Encode(line), CancellationToken.None);
        };
        using JourneyMutationGate gate = new();

        // What JourneyRuntimeWorker does every round: take the gate, run the engine, let go.
        Stopwatch held = Stopwatch.StartNew();
        Task round = Task.Run(async () =>
        {
            using IDisposable _ = await gate.EnterAsync(CancellationToken.None);
            fixture.Clock.Advance(TimeSpan.FromSeconds(1));
            try
            {
                await fixture.Engine.ExecuteOnceAsync(CancellationToken.None);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or SocketException)
            {
                // A write that gives up fails the round the way a dropped connection does; that is the fix, not a defect.
            }
        }, Token);

        Assert.True(
            await Completes(sendEntered.Task, TimeSpan.FromSeconds(10)),
            "the round never sent to the vehicle, so nothing here was stuck on its socket");
        Assert.False(
            await Completes(round, TimeSpan.FromSeconds(1)),
            "the round finished within a second of its first send, so the socket was not stuck and this proves nothing");

        VehicleFaultRecoveryDecision other = await Service(
                fixture, new SiteRiot(fixture), gate: gate, gateTimeout: RoundBudget)
            .RecoverAsync(OtherVehicleClear(fixture), Token);
        bool roundEnded = await Completes(round, RoundBudget);
        held.Stop();

        Assert.True(
            roundEnded,
            $"the round was still stuck on one vehicle's socket after {held.Elapsed.TotalSeconds:F1} s, holding the gate; " +
            $"the other vehicle's clearance, which waited {RoundBudget.TotalSeconds:F0} s for it, got [{string.Join(", ", other.Reasons)}]");
        Assert.DoesNotContain("FAULT_RECOVERY_RUNTIME_BUSY", other.Reasons);
        Assert.Contains("FAULT_RECOVERY_FAULT_NOT_IN_EFFECT", other.Reasons);
    }

    /// <summary>
    /// 人工出口的每一条路，持锁期间一次 RIoT 调用、一次车载端发送都没有（control-server#334 护栏 a，接着
    /// <c>VehicleFaultRecoveryTests.NoRiotCallIsMadeWhileTheGateIsHeld</c> 往下写）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么这几条路一样也不许有，引擎那一轮却可以有。</b>引擎整轮持锁、在锁里调 RIoT 是 control-server#299 起的设计，每次调用
    /// 有 <c>RIoT:timeoutSeconds</c> 的上限，车载端写有 <see cref="OnboardTransportOptions.WriteTimeout"/> 的上限。人工出口不同：它是
    /// 一个 HTTP 请求插进来抢这把锁，锁里只该复核本服务端的表、决定、提交。在锁里多做一次 I/O，就是让全车队的轮次多等一次别人的
    /// 网络。cs#334 第一步用探针跑全量实测过：六条路持锁期间零外部调用（evidence/cs334/step1/）。这条用例把那次实测变成护栏。
    /// </para>
    /// <para>
    /// <b>怎么判「在锁里」。</b>与原来那条一样：每次 RIoT 调用、每次车载端发送时试拿一下这把锁，拿不到就说明调用方正持着它。
    /// 用例是单线程走完一个请求的，没有别人会同时拿锁。
    /// </para>
    /// <para>
    /// <b>不许平白绿。</b>每条路都断言走到了它该有的结局（清除、续行、重建、终结、交接）——没走到的路，锁里什么都不会发生。
    /// 发送一侧同理：放弃停车的行程今天会发三条收尾快照，全在放锁之后，所以这条路要求发送次数大于零——检测器看得见发送，
    /// 只是它们不在锁里；别的路今天不发车载端消息，这里是一道防回归的线，谁往锁里加一次发送就红。
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("clear-empty")]
    [InlineData("clear-loaded")]
    [InlineData("resume")]
    [InlineData("rebuild-stopped")]
    [InlineData("give-up-stopped")]
    [InlineData("prepare-cargo-handoff")]
    public async Task NoFaultRecoveryPathCallsRiotOrSendsToAVehicleWhileTheGateIsHeld(string path)
    {
        await using RuntimeFixture fixture = path switch
        {
            "clear-loaded" => await FaultedOnTheWayToGateAsync(),
            "rebuild-stopped" or "give-up-stopped" => await StoppedRebuildExitTests.StoppedByTheThirdGuardAsync(),
            "prepare-cargo-handoff" => await StoppedRebuildExitTests.StoppedWithCargoNotInPlaceAsync(),
            _ => await FaultedOnTheWayToPickupAsync(),
        };
        using JourneyMutationGate gate = new();
        SiteRiot site = new(fixture) { Gate = gate };
        List<string> sendsUnderGate = [];
        int sends = 0;
        fixture.Peer.OnMessageSent = async line =>
        {
            sends++;
            using IDisposable? free = await gate.TryEnterAsync(TimeSpan.Zero, Token);
            if (free is null)
            {
                sendsUnderGate.Add(line);
            }
        };
        (VehicleFaultRecoveryRequest request, VehicleFaultRecoveryOutcome expected) = path switch
        {
            "rebuild-stopped" => (StoppedRebuildExitTests.Rebuild(fixture), VehicleFaultRecoveryOutcome.RebuildRequested),
            "give-up-stopped" => (StoppedRebuildExitTests.GiveUp(fixture), VehicleFaultRecoveryOutcome.TripTerminated),
            "prepare-cargo-handoff" => (StoppedRebuildExitTests.Prepare(fixture), VehicleFaultRecoveryOutcome.HandoffPrepared),
            "resume" => (await HeldForResumeAsync(fixture, site), VehicleFaultRecoveryOutcome.Resumed),
            _ => (Clear(fixture), VehicleFaultRecoveryOutcome.Cleared),
        };

        VehicleFaultRecoveryDecision decision = await Service(fixture, site, gate: gate).RecoverAsync(request, Token);

        Assert.True(
            decision.Outcome == expected,
            $"the {path} path ended {decision.Outcome} [{string.Join(", ", decision.Reasons)}] rather than {expected}, " +
            "so it never reached the part of it that runs under the gate");
        TestContext.Current.TestOutputHelper?.WriteLine($"{path}: {site.RiotCalls} RIoT calls, {sends} onboard sends");
        if (path == "give-up-stopped")
        {
            Assert.True(sends > 0, "giving up sends its closure snapshots; none was seen, so the send detector saw nothing at all");
        }
        Assert.Empty(site.CallsUnderGate);
        Assert.Empty(sendsUnderGate);
    }

    /// <summary>The order held (PAUSED), and a continue that RIoT accepts: what a resumption needs to succeed.</summary>
    private static async Task<VehicleFaultRecoveryRequest> HeldForResumeAsync(RuntimeFixture fixture, SiteRiot site)
    {
        JourneyRuntimeRow faulted = await fixture.RuntimeAsync();
        fixture.Riot.SetOrderState(faulted.PickupUpperId, RiotOrderState.Paused, terminal: false);
        site.OnOrderCommand = (kind, _) =>
        {
            if (kind == RiotOrderCommandKind.ContinueFromHeld)
            {
                fixture.Riot.SetOrderState(faulted.PickupUpperId, RiotOrderState.Executing, terminal: false);
            }
        };
        return Clear(fixture) with { Action = VehicleFaultRecoveryAction.ResumeHeldOrder };
    }

    /// <summary>
    /// 卡住的写，和排在它后面的写，都以「连接不可用」结束，其中至少一次是写超时；这条连接随之关掉，之后的发送不再等超时、直接失败
    /// （护栏 b 的行为一半）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>排在后面的那一次是 HeartbeatAck 的形状。</b>接收循环要写的应答不是第一个卡住的写，而是排在别的发送方卡住的写后面、等这条连接
    /// 发送闸门的那一次。上限只罩住写本身、不罩住排队，接收循环照样停在那里，静默窗口照样走不到。
    /// </para>
    /// <para>
    /// <b>「至少一次」而不是「第一次」</b>（审查必修 3，本机 14 轮红 1 次）：两次发送各有一个 500 ms 计时器，几乎同时到点；排队那一次先到点时，
    /// 是它把流关掉，第一次写随之以普通的 socket 失败结束。哪一次先到点是调度，不是判据（记忆：窗口里「恰好一次」是调度）。
    /// 两次都必须是 <see cref="IOException"/>：流关了之后的那一次以前是 <see cref="ObjectDisposedException"/>，只接
    /// <see cref="IOException"/> 的发送方会把它当成别的错。
    /// </para>
    /// <para>
    /// <b>「之后直接失败」钉的是关连接，按事件判</b>：只抛异常、不关流的话，卡住的那次写仍占着发送闸门，之后那一次要再等满一个超时、
    /// 以写超时结束；关了流，它以「连接已关」结束，里面没有超时的字样。墙钟只剩宽松的挂死保护。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStuckWriteAndTheOneQueuedBehindItEndWithinTheTimeoutAndTheConnectionIsClosed()
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(500);
        await using StuckPeer stuck = await StuckPeer.OpenAsync(timeout);
        ReadOnlyMemory<byte> line = OnboardPeerConnection.Encode("""{"messageType":"Heartbeat"}""");

        Task first = stuck.Connection.SendAsync(line, CancellationToken.None);
        Task queued = stuck.Connection.SendAsync(line, CancellationToken.None);
        Exception firstFailure = await FailureOf(first);
        Exception queuedFailure = await FailureOf(queued);
        Exception laterFailure = await FailureOf(stuck.Connection.SendAsync(line, CancellationToken.None));

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"first: {Describe(firstFailure)}{Environment.NewLine}queued: {Describe(queuedFailure)}{Environment.NewLine}" +
            $"later: {Describe(laterFailure)}");
        Assert.IsAssignableFrom<IOException>(firstFailure);
        Assert.IsAssignableFrom<IOException>(queuedFailure);
        Assert.True(
            TimedOut(firstFailure) || TimedOut(queuedFailure),
            "neither the stuck write nor the one behind it ended by the write timeout");
        Assert.IsAssignableFrom<IOException>(laterFailure);
        Assert.False(
            TimedOut(laterFailure),
            "a send after the timeout waited out the timeout again: the connection was left open");

        static bool TimedOut(Exception failure) => failure.Message.Contains("did not finish within", StringComparison.Ordinal);

        static string Describe(Exception failure) => $"{failure.GetType().Name}: {failure.Message}";

        static async Task<Exception> FailureOf(Task send)
        {
            try
            {
                // A generous guard against a hang, not the verdict: the verdict is how each send ended.
                await send.WaitAsync(TimeSpan.FromSeconds(30), Token);
            }
            catch (Exception error) when (error is not TimeoutException)
            {
                return error;
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException("a send into a socket nobody reads was still pending after 30 s");
            }
            throw new Xunit.Sdk.XunitException("a write into a socket nobody reads succeeded");
        }
    }

    /// <summary>
    /// 写超时要在 (0, 10 s] 之内，否则服务端拒绝启动：零或负数会把每次写都判超时；超过 10 s，三台卡住的车就能吃满故障清除等锁的
    /// 30 s；大到计时器装不下（约 49.7 天以上）时，每次发送都会当场失败、全队停下，而启动却照样通过。
    /// </summary>
    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(10.001d)]
    [InlineData(60d * 60 * 24 * 60)]
    public async Task TheListenerRefusesToStartWithAWriteTimeoutOutsideItsBounds(double seconds)
    {
        using OnboardTcpServer server = new(
            Microsoft.Extensions.Options.Options.Create(new OnboardTransportOptions
            {
                Enabled = true,
                ListenAddress = "127.0.0.1",
                // A valid port: the port is checked first, and the refusal comes before anything is bound.
                Port = 58999,
                WriteTimeout = TimeSpan.FromSeconds(seconds),
            }),
            new NoScopes(),
            new OnboardPeer(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OnboardTcpServer>.Instance);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync(Token));
        Assert.Contains("OnboardTransport:WriteTimeout", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 这条连接只有一处往流里写，而且那一处只经由罩着写超时的那个入口到达（护栏 b 的文本一半，防回归）。
    /// </summary>
    /// <remarks>
    /// 行为那一半（上面两条与 <c>OnboardPowerLossReconnectTests</c> 里只听不读的那条）证明超时有效；这一条防的是有人在
    /// <see cref="OnboardPeerConnection"/> 里另开一个写流的入口，绕过超时。能不能碰到 socket 的其余出口由
    /// <c>OnboardOutboundFunnelArchitectureTests</c> 钉着：它保证车载端的字节只经过这个类。它按文本数，看不见换个名字的写法——
    /// 与那一类一样是防回归线，不是证明。
    /// </remarks>
    [Fact]
    public void TheConnectionWritesItsStreamInOnePlaceAndOnlyBehindTheWriteTimeout()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src/ControlServer.Host/Transport/OnboardPeer.cs"));
        int connection = source.IndexOf("internal sealed class OnboardPeerConnection", StringComparison.Ordinal);
        Assert.True(connection >= 0, "OnboardPeerConnection moved; move this check with it.");
        string body = source[connection..];

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(body, @"_stream\.WriteAsync\("));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(body, @"_stream\.FlushAsync\("));
        Assert.Equal(0, System.Text.RegularExpressions.Regex.Count(body, @"_stream\.Write\(|_stream\.Flush\(\)"));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(body, @"\bSendCoreAsync\(ndjsonLine"));
        Assert.Contains("send.WaitAsync(_writeTimeout", body, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ControlServer.sln")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException("ControlServer.sln was not found above the test assembly.");
    }

    /// <summary>The listener refuses before it opens a connection, so it never asks for a scope.</summary>
    private sealed class NoScopes : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() =>
            throw new InvalidOperationException("the listener opened a connection it should have refused to start for");
    }

    /// <summary>
    /// 录入子批之后读箱数，MesIngest 接了连接却一直不回话：这一次读按读失败处理（拒收这次录入，理由是箱数不可用），这一轮照常走完，
    /// 不是整轮失败（control-server#334 审查必修 2）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要紧。</b>这次读在引擎整轮持锁期间、在某一台车的推进里。修之前 <see cref="HttpClient"/> 超时抛的
    /// <see cref="TaskCanceledException"/> 这里没接，冒出去让这台车的推进失败，整轮随之失败：排在后面的每一台车都不推进，
    /// 急停确认与派车也停着，下一轮再等一个超时、再失败。
    /// </para>
    /// <para>
    /// <b>「挂住」是真的挂住。</b>生产的 <see cref="HttpSublotBoxCountReader"/>，经真实的 <see cref="HttpClient"/> 连一个只接连接、
    /// 从不回话的回环监听（<see cref="HungMesIngest"/>）；超时取 500 ms，抛的就是 <see cref="HttpClient"/> 自己的超时异常，不是
    /// 替身扔出来的一个同名类型。前提断言这次读确实到了 MesIngest（监听接到过连接），否则没读就拒收也会绿。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ABoxCountReadFromAMesIngestThatNeverAnswersRefusesTheEntryAndTheRoundGoesOn()
    {
        await using RuntimeFixture fixture = await JourneyRuntimeWorkerLoadCancellationBeforeSublotTests.ReachSublotWaitAsync();
        await using HungMesIngest mes = HungMesIngest.Start();
        using HttpClient client = mes.Client(TimeSpan.FromMilliseconds(500));
        HttpSublotBoxCountReader reader = new(client, Microsoft.Extensions.Options.Options.Create(fixture.Options), fixture.Clock);
        fixture.BoxCounts.Through = reader.ReadMaxBoxCountAsync;
        await using ControlServerDbContext connection = fixture.OpenConnectionContext();
        OnboardMessageProcessor processor = JourneyRuntimeWorkerLoadCancellationBeforeSublotTests.BeforeSublotProcessor(fixture, connection);
        OnboardConnectionState state = JourneyRuntimeWorkerLoadCancellationBeforeSublotTests.BeforeSublotConnection(fixture, generation: 1);
        JourneyRuntimeRow waiting = await fixture.RuntimeAsync();
        await processor.ProcessAsync(
            JourneyRuntimeWorkerLoadCancellationBeforeSublotTests.SublotEntry(fixture, waiting, "SUBLOT-001"), state, Token);

        await fixture.Engine.ExecuteOnceAsync(Token);

        Assert.True(mes.Accepted > 0, "the box count read never reached MesIngest, so nothing here timed out");
        using System.Text.Json.JsonDocument refusal = System.Text.Json.JsonDocument.Parse(
            (await fixture.Context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(row => row.MessageType == "SublotRejected", Token)).PayloadJson);
        Assert.Equal(
            ServerReasonCodes.SublotBoxCountUnavailable,
            refusal.RootElement.GetProperty("payload").GetProperty("problem").GetProperty("reasonCode").GetString());
        Assert.Equal(JourneyRuntimeStage.AwaitingSublot, (await fixture.RuntimeAsync()).Stage);
    }

    /// <summary>
    /// A loopback MesIngest that accepts every connection and never answers on any of them: what a hung MesIngest process,
    /// or a half-open connection to one, looks like to an <see cref="HttpClient"/>.
    /// </summary>
    internal sealed class HungMesIngest : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly List<TcpClient> _held = [];
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _accepting;
        private int _accepted;

        private HungMesIngest(TcpListener listener)
        {
            _listener = listener;
            _accepting = AcceptAsync();
        }

        /// <summary>How many connections a client opened to it; above zero means a request really reached it.</summary>
        public int Accepted => Volatile.Read(ref _accepted);

        public static HungMesIngest Start()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return new HungMesIngest(listener);
        }

        public HttpClient Client(TimeSpan timeout) => new()
        {
            BaseAddress = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/"),
            Timeout = timeout,
        };

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    lock (_held)
                    {
                        _held.Add(client);
                    }
                    Interlocked.Increment(ref _accepted);
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
            await _accepting;
            lock (_held)
            {
                foreach (TcpClient client in _held)
                {
                    client.Dispose();
                }
            }
            _stopping.Dispose();
        }
    }

    private static VehicleFaultRecoveryRequest OtherVehicleClear(RuntimeFixture fixture) =>
        Clear(fixture) with { Subject = new EmergencyStopSubject("AGV-CS334-OTHER", "BROKERX-CS334-OTHER") };

    private static async Task<bool> Completes(Task task, TimeSpan within) =>
        await Task.WhenAny(task, Task.Delay(within, Token)) == task;

    /// <summary>
    /// A loopback connection whose vehicle end never reads, with every kernel buffer between the two ends already full.
    /// </summary>
    private sealed class StuckPeer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _vehicle;
        private readonly TcpClient _server;
        private readonly NetworkStream _stream;

        private StuckPeer(TcpListener listener, TcpClient vehicle, TcpClient server, TimeSpan? writeTimeout)
        {
            _listener = listener;
            _vehicle = vehicle;
            _server = server;
            _stream = server.GetStream();
            Connection = writeTimeout is { } timeout
                ? new OnboardPeerConnection(_stream, timeout)
                : new OnboardPeerConnection(_stream);
        }

        public OnboardPeerConnection Connection { get; }

        public static async Task<StuckPeer> OpenAsync(TimeSpan? writeTimeout = null)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            TcpClient vehicle = new() { ReceiveBufferSize = 4096 };
            await vehicle.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Token);
            TcpClient server = await listener.AcceptTcpClientAsync(Token);
            server.SendBufferSize = 4096;
            Fill(server.Client);
            return new StuckPeer(listener, vehicle, server, writeTimeout);
        }

        /// <summary>Writes without blocking until the socket refuses more: from here on every write waits for a reader.</summary>
        private static void Fill(Socket socket)
        {
            byte[] chunk = Encoding.ASCII.GetBytes(new string('x', 4095) + "\n");
            socket.Blocking = false;
            try
            {
                long written = 0;
                while (true)
                {
                    try
                    {
                        written += socket.Send(chunk);
                    }
                    catch (SocketException error) when (error.SocketErrorCode == SocketError.WouldBlock)
                    {
                        break;
                    }
                    if (written > 256L * 1024 * 1024)
                    {
                        throw new InvalidOperationException("256 MiB went into a socket nobody reads; it is not filling.");
                    }
                }
            }
            finally
            {
                socket.Blocking = true;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Closing the server end fails whatever write is still pending, so a red run still tears down.
            _server.Dispose();
            _vehicle.Dispose();
            _listener.Stop();
            await Connection.DisposeAsync();
        }
    }
}
