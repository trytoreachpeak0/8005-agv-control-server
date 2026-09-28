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
    /// 卡住的写，和排在它后面的写，都在写超时之内结束；这条连接随之关掉，之后的发送立刻失败（护栏 b 的行为一半）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>排在后面的那一次是 HeartbeatAck 的形状。</b>接收循环要写的应答不是第一个卡住的写，而是排在别的发送方卡住的写后面、等这条连接
    /// 发送闸门的那一次。上限只罩住写本身、不罩住排队，接收循环照样停在那里，静默窗口照样走不到。
    /// </para>
    /// <para>
    /// <b>「之后立刻失败」钉的是关连接。</b>只抛异常、不关流的话，卡住的那次写仍占着发送闸门，这条连接上之后的每一次发送都要再等满一个
    /// 超时——一轮里给这台车发几条，闸门就多占几个超时；而接收循环不在的时候（引擎直接发的那一路），也没有别的东西会把它从路由表摘掉。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStuckWriteAndTheOneQueuedBehindItEndWithinTheTimeoutAndTheConnectionIsClosed()
    {
        TimeSpan timeout = TimeSpan.FromMilliseconds(500);
        await using StuckPeer stuck = await StuckPeer.OpenAsync(timeout);
        ReadOnlyMemory<byte> line = OnboardPeerConnection.Encode("""{"messageType":"Heartbeat"}""");

        Stopwatch took = Stopwatch.StartNew();
        Task first = stuck.Connection.SendAsync(line, CancellationToken.None);
        Task queued = stuck.Connection.SendAsync(line, CancellationToken.None);
        Exception firstFailure = await FailureOf(first);
        Exception queuedFailure = await FailureOf(queued);
        TimeSpan bothEnded = took.Elapsed;

        Stopwatch after = Stopwatch.StartNew();
        Exception laterFailure = await FailureOf(stuck.Connection.SendAsync(line, CancellationToken.None));
        TimeSpan laterEnded = after.Elapsed;

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"both ended after {bothEnded.TotalMilliseconds:F0} ms, a later send after {laterEnded.TotalMilliseconds:F0} ms; " +
            $"{firstFailure.GetType().Name} / {queuedFailure.GetType().Name} / {laterFailure.GetType().Name}");
        IOException timedOut = Assert.IsType<IOException>(firstFailure);
        Assert.Contains("did not finish within", timedOut.Message, StringComparison.Ordinal);
        Assert.True(
            queuedFailure is IOException or ObjectDisposedException,
            $"the queued send ended with {queuedFailure.GetType().Name}, which no sender reads as a lost connection");
        Assert.True(
            bothEnded < timeout * 2 + TimeSpan.FromSeconds(2),
            $"the stuck write and the one behind it took {bothEnded.TotalMilliseconds:F0} ms to end against a {timeout.TotalMilliseconds:F0} ms timeout");
        Assert.True(
            laterEnded < timeout,
            $"a send after the timeout took {laterEnded.TotalMilliseconds:F0} ms to fail: the connection was left open, " +
            "so every further send waits out the timeout again");

        static async Task<Exception> FailureOf(Task send)
        {
            Exception? failure = await EndedWith(send);
            return failure ?? throw new Xunit.Sdk.XunitException("a write into a socket nobody reads succeeded");
        }

        static async Task<Exception?> EndedWith(Task send)
        {
            try
            {
                await send.WaitAsync(TimeSpan.FromSeconds(30), Token);
                return null;
            }
            catch (Exception error) when (error is not TimeoutException)
            {
                return error;
            }
        }
    }

    /// <summary>写超时不能配成零或负数：服务端拒绝启动，而不是把每次写都判超时，或把它读成「永不超时」。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task TheListenerRefusesToStartWithAWriteTimeoutThatIsNotPositive(int seconds)
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
