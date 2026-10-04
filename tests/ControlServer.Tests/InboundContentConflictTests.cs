using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ControlServer.Domain;
using ControlServer.FakeOnboard;
using ControlServer.Host.Transport;
using ControlServer.Infrastructure.Persistence;
using ControlServer.TestDoubles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// 入站内容冲突回协议错误帧、不断连接（control-server#478）。
/// </summary>
/// <remarks>
/// <para>
/// <b>守的是什么。</b>同一 messageId 第二次带不同内容到达（<c>MESSAGE_ID_CONTENT_CONFLICT</c>），或同一业务号换了
/// messageId 带不同内容到达（<c>BUSINESS_ID_CONTENT_CONFLICT</c>），服务端要回一条与那条消息关联的
/// <c>ProtocolProblem</c>，连接照常可用，已收的那一份不被改写（CV-RELIABLE-RETRY-DIFFERENT-CONTENT：
/// <c>REJECT_MESSAGE_ID_CONTENT_CONFLICT</c>、<c>NEVER_APPLY_CONFLICTING_RETRY</c>）。在这张票之前，
/// <c>ProtocolContentConflictException</c> 一路冒到 <see cref="OnboardTcpServer"/> 的 <c>catch (Exception)</c>，
/// 只记一条日志就关连接。
/// </para>
/// <para>
/// <b>为什么握手里那一条单列。</b>真车载端在 <c>SessionAccepted</c> 之后、<c>CapabilitySnapshot</c> 之前，逐条补发
/// 发件箱里没被确认的持久报文（<c>WireToGateSessionClient.ConnectAndRecoverAsync</c>）。一条内容冲突的报文因此会在
/// 每一次重连的同一个位置出现：服务端断一次，车就永远握不完手。<see cref="ConflictRelay"/> 在那个位置替车补发，
/// 并把服务端对它的回答截下来，不交给合成车载端，与真车载端「补发一条、读一条回答」的节奏一致。
/// </para>
/// <para>
/// <b>本类只证服务端这一侧。</b>真车载端今天收到补发的 <c>ProtocolProblem</c> 会自己抛出、断开、下次重连再补发
/// （<c>ReplayDurableOutgoingAsync</c> 里的 <c>ThrowIfProtocolProblem</c>），那一半的出口不在这个仓库。
/// </para>
/// </remarks>
public sealed class InboundContentConflictTests
{
    private const string AgvId = "AGV-CONFLICT-01";
    private const string CredentialVariable = "CONTROL_SERVER_ONBOARD_CREDENTIAL_CONTENT_CONFLICT_TESTS";
    private const string Credential = "content-conflict-test-credential";

    /// <summary>等服务端回答一条注入的报文最多多久。本机回环上是毫秒级，这是挂死保护。</summary>
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(5);

    /// <summary>冲突之后连接要连续多久不被关，才算没断。比一个心跳周期（两秒）长。</summary>
    private static readonly TimeSpan StaysUp = TimeSpan.FromSeconds(3);

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task SameMessageIdWithDifferentContentIsAnsweredWithAProblemAndTheConnectionStays()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.ConnectAsync();
        long generation = rig.Generation;

        string messageId = Guid.NewGuid().ToString("D");
        string requestId = Guid.NewGuid().ToString("D");
        Answer first = await rig.Relay.InjectAsync(
            ManualChargingReturn(messageId, requestId, generation, "first"), AnswerWithin);
        Assert.Equal("ManualChargingReturnToServiceResult", first.MessageType);
        string inboxHashBefore = await rig.InboxContentHashAsync(messageId);

        Answer conflicting = await rig.Relay.InjectAsync(
            ManualChargingReturn(messageId, requestId, generation, "second, different content"), AnswerWithin);

        conflicting.AssertProblem("MESSAGE_ID_CONTENT_CONFLICT", messageId, "ManualChargingReturnToServiceRequested");
        await rig.AssertConnectionStaysAsync();
        Assert.Equal(inboxHashBefore, await rig.InboxContentHashAsync(messageId));
        await AssertNextMessageIsTakenAsync(rig, generation);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task SameBusinessIdWithDifferentContentIsAnsweredWithAProblemAndTheConnectionStays()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.ConnectAsync();
        long generation = rig.Generation;

        string requestId = Guid.NewGuid().ToString("D");
        Answer first = await rig.Relay.InjectAsync(
            ManualChargingReturn(Guid.NewGuid().ToString("D"), requestId, generation, "first"), AnswerWithin);
        Assert.Equal("ManualChargingReturnToServiceResult", first.MessageType);

        string secondMessageId = Guid.NewGuid().ToString("D");
        Answer conflicting = await rig.Relay.InjectAsync(
            ManualChargingReturn(secondMessageId, requestId, generation, "second, different content"), AnswerWithin);

        conflicting.AssertProblem("BUSINESS_ID_CONTENT_CONFLICT", secondMessageId, "ManualChargingReturnToServiceRequested");
        await rig.AssertConnectionStaysAsync();
        Assert.False(
            await rig.InboxHasAsync(secondMessageId),
            "被拒的冲突报文进了 ProtocolInbox：下一次同 messageId 的补发会被当成已收、直接回放那份拒绝之外的答案。");
        await AssertNextMessageIsTakenAsync(rig, generation);
    }

    /// <summary>
    /// 车载端发件箱里留着一条与服务端已收内容不一致的报文，每次重连都在握手里补发它。服务端每次都要回同一个
    /// 错误码、让握手照常走完，而且三次之后已收的那一份仍然原样。
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-06")]
    [Trait("ProtocolVector", "CV-RELIABLE-RETRY-DIFFERENT-CONTENT")]
    public async Task AConflictingMessageReplayedInEveryHandshakeDoesNotEndAnyOfThem()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.ConnectAsync();

        string messageId = Guid.NewGuid().ToString("D");
        string requestId = Guid.NewGuid().ToString("D");
        Answer first = await rig.Relay.InjectAsync(
            ManualChargingReturn(messageId, requestId, rig.Generation, "first"), AnswerWithin);
        Assert.Equal("ManualChargingReturnToServiceResult", first.MessageType);
        string inboxHashBefore = await rig.InboxContentHashAsync(messageId);
        await rig.DisconnectAsync();

        // What the onboard outbox holds: the same messageId, other content. Rebound to each new generation as the
        // real onboard rebinds a pending row (RebindDurableMessageForSessionAsync).
        rig.Relay.ReplayOnAccept = acceptedGeneration =>
            ManualChargingReturn(messageId, requestId, acceptedGeneration, "second, different content");

        for (int reconnect = 1; reconnect <= 3; reconnect++)
        {
            Exception? handshakeFailure = await Record.ExceptionAsync(rig.ConnectAsync);
            Answer? replayAnswer = rig.Relay.LastReplayAnswer;
            Assert.True(
                handshakeFailure is null,
                $"第 {reconnect} 次重连的握手没走完：{handshakeFailure?.GetType().Name}: {handshakeFailure?.Message}；" +
                $"补发那条得到的回答：{replayAnswer?.Describe() ?? "（没有回答，连接被关）"}；" +
                $"服务端关过的连接数：{rig.Relay.ServerClosedConnections}；服务端记下的断连原因：{rig.Relay.ServerSays()}。");
            Assert.NotNull(replayAnswer);
            replayAnswer.AssertProblem("MESSAGE_ID_CONTENT_CONFLICT", messageId, "ManualChargingReturnToServiceRequested");
            await rig.AssertConnectionStaysAsync();
            await rig.DisconnectAsync();
        }

        Assert.Equal(inboxHashBefore, await rig.InboxContentHashAsync(messageId));
    }

    /// <summary>
    /// 冲突之后同一条连接上的下一条正常报文照常处理、照常入库：被拒那一条没留下任何东西挡住它。
    /// </summary>
    private static async Task AssertNextMessageIsTakenAsync(Rig rig, long generation)
    {
        string messageId = Guid.NewGuid().ToString("D");
        Answer next = await rig.Relay.InjectAsync(
            ManualChargingReturn(messageId, Guid.NewGuid().ToString("D"), generation, "a new request after the conflict"),
            AnswerWithin);
        Assert.True(next.MessageType == "ManualChargingReturnToServiceResult", $"冲突之后的下一条没被照常处理：{next.Describe()}");
        Assert.True(await rig.InboxHasAsync(messageId), "冲突之后的下一条没有入 ProtocolInbox。");
    }

    private static string ManualChargingReturn(string messageId, string requestId, long generation, string reason) =>
        ProtocolEnvelope.Serialize(
            "ManualChargingReturnToServiceRequested",
            messageId,
            null,
            AgvId,
            generation,
            DateTimeOffset.UtcNow,
            new
            {
                requestId,
                administrator = new
                {
                    operatorId = "OP-4478",
                    verificationMethod = "BADGE",
                    verifiedAt = "2026-10-04T08:59:00Z"
                },
                administratorRole = "MAINTENANCE_ADMINISTRATOR",
                reason,
                observedBatteryPercent = 84.5
            });

    /// <summary>服务端对一条注入报文的回答：整行，与它的类型。</summary>
    private sealed record Answer(string MessageType, string Line)
    {
        public string Describe() => MessageType + " " + Line;

        public void AssertProblem(string reasonCode, string rejectedMessageId, string rejectedMessageType)
        {
            Assert.True(MessageType == "ProtocolProblem", $"期望 ProtocolProblem，收到 {Describe()}");
            using JsonDocument document = JsonDocument.Parse(Line);
            JsonElement root = document.RootElement;
            Assert.Equal(rejectedMessageId, root.GetProperty("correlationId").GetString());
            JsonElement payload = root.GetProperty("payload");
            Assert.Equal(rejectedMessageId, payload.GetProperty("rejectedMessageId").GetString());
            Assert.Equal(rejectedMessageType, payload.GetProperty("rejectedMessageType").GetString());
            Assert.Equal(reasonCode, payload.GetProperty("problem").GetProperty("reasonCode").GetString());
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ControlServerDbContext _context;
        private readonly ServiceProvider _provider;
        private readonly OnboardTcpServer _server;

        private Rig(
            SqliteConnection connection,
            ControlServerDbContext context,
            ServiceProvider provider,
            OnboardTcpServer server,
            ConflictRelay relay,
            CommandEngine<FakeOnboardState> engine,
            OnboardPeerSession onboard)
        {
            _connection = connection;
            _context = context;
            _provider = provider;
            _server = server;
            Relay = relay;
            Engine = engine;
            Onboard = onboard;
        }

        public ConflictRelay Relay { get; }

        public CommandEngine<FakeOnboardState> Engine { get; }

        public OnboardPeerSession Onboard { get; }

        public long Generation => Engine.Snapshot().State.SessionGeneration;

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
            EventRecordingLogger<OnboardTcpServer> serverLog = new();
            OnboardTcpServer server = new(
                Options.Create(new OnboardTransportOptions { Enabled = true, ListenAddress = "127.0.0.1", Port = serverPort }),
                provider.GetRequiredService<IServiceScopeFactory>(),
                peer,
                serverLog);
            await server.StartAsync(TestContext.Current.CancellationToken);

            ConflictRelay relay = ConflictRelay.Start(serverPort);
            // Why the server ended each connection, as it logged it: the failure messages name the exception.
            relay.ServerSays = () =>
            {
                lock (serverLog.Entries)
                {
                    return string.Join(" | ", serverLog.Entries
                        .Where(entry => entry.Error is not null)
                        .Select(entry => $"{entry.Error!.GetType().Name}: {entry.Error.Message}"));
                }
            };
            CommandEngine<FakeOnboardState> engine = new("content-conflict-test", () => new FakeOnboardState());
            OnboardPeerSession onboard = new(
                engine,
                new FakeOnboardOptions
                {
                    Port = relay.Port,
                    AgvId = AgvId,
                    CredentialEnvironmentVariable = CredentialVariable
                },
                SlotStateSeed.Read(new ConfigurationBuilder().Build()));
            return new Rig(connection, context, provider, server, relay, engine, onboard);
        }

        /// <summary>A fresh connection and the full handshake, through the relay.</summary>
        public async Task ConnectAsync()
        {
            Relay.LastReplayAnswer = null;
            await Onboard.ReconnectAsync().WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            Assert.Equal("READY", Engine.Snapshot().State.Readiness);
        }

        public async Task DisconnectAsync()
        {
            try
            {
                await Onboard.DisconnectAsync();
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException
                                              or InvalidOperationException)
            {
                // Gone either way.
            }
        }

        public async Task AssertConnectionStaysAsync()
        {
            int closedBefore = Relay.ServerClosedConnections;
            DateTime until = DateTime.UtcNow + StaysUp;
            while (DateTime.UtcNow < until)
            {
                Assert.True(Onboard.IsConnected, "冲突之后服务端把连接关了。");
                await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
            }
            Assert.Equal(closedBefore, Relay.ServerClosedConnections);
        }

        public async Task<string> InboxContentHashAsync(string messageId)
        {
            _context.ChangeTracker.Clear();
            return (await _context.ProtocolInbox.AsNoTracking()
                    .SingleAsync(row => row.MessageId == messageId, TestContext.Current.CancellationToken))
                .ContentHash;
        }

        public async Task<bool> InboxHasAsync(string messageId)
        {
            _context.ChangeTracker.Clear();
            return await _context.ProtocolInbox.AsNoTracking()
                .AnyAsync(row => row.MessageId == messageId, TestContext.Current.CancellationToken);
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
                    await _server.StopAsync(CancellationToken.None);
                    _server.Dispose();
                    await _provider.DisposeAsync();
                    await _context.DisposeAsync();
                    await _connection.DisposeAsync();
                    Environment.SetEnvironmentVariable(CredentialVariable, null);
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
    /// 夹在合成车载端与服务端之间、按行转发的中继，替车载端补发它发件箱里的报文。
    /// </summary>
    /// <remarks>
    /// 注入的报文由中继自己写给服务端，服务端对它的回答（按 correlationId 认）由中继截下、不转给合成车载端：
    /// 合成车载端在握手里一次读一行，多出来的一行会被它当成下一步的回答。<see cref="ReplayOnAccept"/> 设了时，
    /// 中继在转发 <c>SessionAccepted</c> 之前先补发那一条、等到它的回答，与真车载端补发发件箱的位置和节奏一致。
    /// 服务端关连接时中继也关车载端那一侧，合成车载端因此读到 EOF，与直连时一样。
    /// </remarks>
    private sealed class ConflictRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _serverPort;
        private readonly CancellationTokenSource _stopping = new();
        private readonly List<Task> _links = [];
        private readonly Task _accepting;
        private Link? _current;
        private int _serverClosed;

        private ConflictRelay(TcpListener listener, int serverPort)
        {
            _listener = listener;
            _serverPort = serverPort;
            _accepting = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int ServerClosedConnections => Volatile.Read(ref _serverClosed);

        /// <summary>Builds the line to replay right after SessionAccepted, given the accepted generation. Null replays nothing.</summary>
        public Func<long, string>? ReplayOnAccept { get; set; }

        /// <summary>The exceptions the server logged ending connections, for failure messages.</summary>
        public Func<string> ServerSays { get; set; } = () => string.Empty;

        /// <summary>What the server answered the last handshake replay with; null when it answered nothing.</summary>
        public Answer? LastReplayAnswer { get; set; }

        public static ConflictRelay Start(int serverPort)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return new ConflictRelay(listener, serverPort);
        }

        /// <summary>Writes one line to the server on the current connection and returns its answer.</summary>
        public async Task<Answer> InjectAsync(string line, TimeSpan within)
        {
            Link link = _current ?? throw new InvalidOperationException("中继上没有连接。");
            Answer? answer = await link.InjectAsync(line, within);
            return answer ?? throw new Xunit.Sdk.XunitException(
                $"服务端没有回答注入的报文，而是关了连接（服务端关过的连接数：{ServerClosedConnections}；" +
                $"服务端记下的断连原因：{ServerSays()}）。");
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    TcpClient vehicle = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    TcpClient server = new();
                    await server.ConnectAsync(IPAddress.Loopback, _serverPort, _stopping.Token);
                    Link link = new(this, vehicle, server);
                    _current = link;
                    lock (_links)
                    {
                        _links.Add(RunLinkAsync(link, _stopping.Token));
                    }
                }
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopping.
            }
        }

        private static async Task RunLinkAsync(Link link, CancellationToken stopping)
        {
            using (link)
            {
                await link.RunAsync(stopping);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            await _accepting;
            Task[] links;
            lock (_links)
            {
                links = [.. _links];
            }
            await Task.WhenAll(links);
            _stopping.Dispose();
        }

        private sealed class Link(ConflictRelay relay, TcpClient vehicle, TcpClient server) : IDisposable
        {
            private readonly SemaphoreSlim _toServer = new(1, 1);
            private readonly ConcurrentDictionary<string, TaskCompletionSource<Answer?>> _awaiting = new(StringComparer.Ordinal);
            private StreamWriter? _serverWriter;

            public async Task<Answer?> InjectAsync(string line, TimeSpan within)
            {
                string messageId;
                using (JsonDocument document = JsonDocument.Parse(line))
                {
                    messageId = document.RootElement.GetProperty("messageId").GetString()!;
                }
                TaskCompletionSource<Answer?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _awaiting[messageId] = answer;
                await WriteToServerAsync(line, CancellationToken.None);
                try
                {
                    return await answer.Task.WaitAsync(within);
                }
                catch (TimeoutException)
                {
                    throw new Xunit.Sdk.XunitException($"服务端 {within.TotalSeconds:0} 秒内没有回答注入的报文 {messageId}。");
                }
            }

            public async Task RunAsync(CancellationToken stopping)
            {
                using (vehicle)
                using (server)
                {
                    NetworkStream vehicleStream = vehicle.GetStream();
                    NetworkStream serverStream = server.GetStream();
                    _serverWriter = new StreamWriter(serverStream, new UTF8Encoding(false), leaveOpen: true)
                    {
                        AutoFlush = true,
                        NewLine = "\n"
                    };
                    using CancellationTokenSource linkEnded = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                    Task up = PumpAsync(
                        new StreamReader(vehicleStream, Encoding.UTF8, false, leaveOpen: true),
                        line => WriteToServerAsync(line, linkEnded.Token),
                        linkEnded.Token);
                    Task down = ServerToVehicleAsync(
                        new StreamReader(serverStream, Encoding.UTF8, false, leaveOpen: true),
                        new StreamWriter(vehicleStream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" },
                        linkEnded.Token);
                    await Task.WhenAny(up, down);
                    await linkEnded.CancelAsync();
                    vehicle.Close();
                    server.Close();
                    await Task.WhenAll(Quietly(up), Quietly(down));
                    foreach (TaskCompletionSource<Answer?> pending in _awaiting.Values)
                    {
                        pending.TrySetResult(null);
                    }
                }
            }

            private async Task ServerToVehicleAsync(StreamReader fromServer, StreamWriter toVehicle, CancellationToken token)
            {
                while (!token.IsCancellationRequested)
                {
                    string? line = await fromServer.ReadLineAsync(token);
                    if (line is null)
                    {
                        Interlocked.Increment(ref relay._serverClosed);
                        return;
                    }
                    string messageType;
                    string? correlationId;
                    long? generation;
                    using (JsonDocument document = JsonDocument.Parse(line))
                    {
                        JsonElement root = document.RootElement;
                        messageType = root.GetProperty("messageType").GetString() ?? string.Empty;
                        correlationId = root.TryGetProperty("correlationId", out JsonElement correlation) &&
                                        correlation.ValueKind == JsonValueKind.String
                            ? correlation.GetString()
                            : null;
                        generation = messageType == "SessionAccepted"
                            ? root.GetProperty("payload").GetProperty("sessionGeneration").GetInt64()
                            : null;
                    }
                    if (correlationId is not null && _awaiting.TryRemove(correlationId, out TaskCompletionSource<Answer?>? waiter))
                    {
                        waiter.TrySetResult(new Answer(messageType, line));
                        continue;
                    }
                    if (generation is long accepted && relay.ReplayOnAccept is { } replay)
                    {
                        // The real onboard replays its outbox here, one line and one answer at a time, before it
                        // sends its CapabilitySnapshot, and reads exactly one line as the answer. A connection the
                        // server closes over the replay ends here too.
                        await WriteToServerAsync(replay(accepted), token);
                        string? answerLine = await fromServer.ReadLineAsync(token)
                            .AsTask().WaitAsync(AnswerWithin, token);
                        if (answerLine is null)
                        {
                            Interlocked.Increment(ref relay._serverClosed);
                            relay.LastReplayAnswer = null;
                            return;
                        }
                        using (JsonDocument answer = JsonDocument.Parse(answerLine))
                        {
                            relay.LastReplayAnswer = new Answer(
                                answer.RootElement.GetProperty("messageType").GetString() ?? string.Empty, answerLine);
                        }
                    }
                    await toVehicle.WriteLineAsync(line.AsMemory(), token);
                }
            }

            private async Task WriteToServerAsync(string line, CancellationToken token)
            {
                await _toServer.WaitAsync(token);
                try
                {
                    await _serverWriter!.WriteLineAsync(line.AsMemory(), token);
                }
                finally
                {
                    _toServer.Release();
                }
            }

            private static async Task PumpAsync(StreamReader from, Func<string, Task> forward, CancellationToken token)
            {
                while (!token.IsCancellationRequested)
                {
                    string? line = await from.ReadLineAsync(token);
                    if (line is null)
                    {
                        return;
                    }
                    await forward(line);
                }
            }

            public void Dispose()
            {
                _toServer.Dispose();
                try
                {
                    _serverWriter?.Dispose();
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    // The socket under it is already closed; nothing is buffered (AutoFlush).
                }
            }

            private static async Task Quietly(Task task)
            {
                try
                {
                    await task;
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException
                                                  or SocketException)
                {
                    // The link is being torn down.
                }
            }
        }
    }
}
