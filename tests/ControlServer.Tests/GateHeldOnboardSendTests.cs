using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ControlServer.Application;
using ControlServer.Host.Runtime;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Transport;
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

        private StuckPeer(TcpListener listener, TcpClient vehicle, TcpClient server)
        {
            _listener = listener;
            _vehicle = vehicle;
            _server = server;
            _stream = server.GetStream();
            Connection = new OnboardPeerConnection(_stream);
        }

        public OnboardPeerConnection Connection { get; }

        public static async Task<StuckPeer> OpenAsync()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            TcpClient vehicle = new() { ReceiveBufferSize = 4096 };
            await vehicle.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Token);
            TcpClient server = await listener.AcceptTcpClientAsync(Token);
            server.SendBufferSize = 4096;
            Fill(server.Client);
            return new StuckPeer(listener, vehicle, server);
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
