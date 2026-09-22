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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ControlServer.Tests;

/// <summary>
/// Nothing the server originates reaches a vehicle while it is still inside the five-step handshake
/// (ADR-cross-0029), and what was held back arrives once the handshake is done (control-server#259).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the handshake is the window.</b> Until its recovery report is answered the vehicle reads the
/// connection one line per request -- it sends a snapshot and takes the very next line as the answer. Only
/// once it has read that answer does it start its receive loop, which matches answers by correlationId and
/// so tolerates a push arriving between a request and its answer. A push that lands inside the handshake is
/// read as the answer it is waiting for, and the vehicle drops the connection
/// (<c>Expected 'SnapshotAppliedAck', received '...'</c>). Real-rig run 35513390399 (a
/// <c>SlotOperationCommand</c>) and synthetic runs 35553615265 and 35651373959 (an
/// <c>UpcomingStopPlanSnapshot</c>) are that failure.
/// </para>
/// <para>
/// <b>How the window is held open.</b> A relay sits between the synthetic vehicle and the server and, when
/// the vehicle sends its <c>SafetyStateSnapshot</c> (handshake step 3), pushes first and forwards the line
/// only after the push returned. Before the fix the server had filed this connection for routing as soon as
/// <c>SessionHello</c> was answered, so by then the push is on the wire ahead of the snapshot's answer --
/// every time, not when a timeline happens to overlap. The vehicle is <c>ControlServer.FakeOnboard</c>, the
/// synthetic L2 peer, which refuses an out-of-order line exactly as the real onboard does.
/// </para>
/// <para>
/// <b>What each test rules out.</b> The first proves the push is held back. It would also pass for a server
/// that never pushes at all, so the second proves that a push held back during the handshake is on the
/// vehicle afterwards, delivered by the server with nobody asking again, and the third that pushing works
/// normally once the handshake is done. The fourth keeps the gate tied to the handshake in progress rather
/// than to one that finished earlier on the same connection.
/// </para>
/// </remarks>
public sealed class OnboardHandshakePushGateTests
{
    private const string AgvId = "AGV-GATE-01";
    private const string CredentialVariable = "CONTROL_SERVER_ONBOARD_CREDENTIAL_PUSH_GATE_TESTS";
    private const string Credential = "push-gate-test-credential";
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A push attempted mid-handshake does not reach the vehicle, and the handshake completes.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task APushDuringTheHandshakeDoesNotReachTheVehicleAndTheHandshakeCompletes()
    {
        await using Rig rig = await Rig.StartAsync();
        Exception? pushOutcome = null;
        bool pushed = false;
        rig.Relay.BeforeForwarding = async messageType =>
        {
            if (messageType != "SafetyStateSnapshot" || pushed)
            {
                return;
            }
            pushed = true;
            try
            {
                await rig.Peer.SendAsync(Push("FaultCargoRecoveryCommand"), CancellationToken.None);
            }
            catch (Exception error)
            {
                pushOutcome = error;
            }
        };

        await rig.Onboard.StartAsync(rig.Lifetime.Token).WaitAsync(Guard, TestContext.Current.CancellationToken);

        Assert.True(pushed, "The relay never reached the handshake step it pushes at.");
        Assert.Equal("READY", rig.Engine.Snapshot().State.Readiness);
        // Every line the vehicle read in its handshake is an answer it asked for, in the order it asked.
        string[] expected =
            ["SessionAccepted", "SnapshotAppliedAck", "SnapshotAppliedAck", "SnapshotAppliedAck", "DurableAck", "SessionReadiness"];
        Assert.Equal(expected, rig.Inbound().Take(expected.Length).Select(item => item.MessageType));
        // Held back the way an unreachable vehicle is: the caller is told nothing was sent, which every
        // outbound path already handles, rather than being told it was sent.
        Assert.IsAssignableFrom<IOException>(pushOutcome);
    }

    /// <summary>
    /// A recovery-session snapshot the server tried to send mid-handshake is on the vehicle once the handshake
    /// is done, sent by the server itself.
    /// </summary>
    /// <remarks>
    /// Nothing in this test sends it a second time. The first attempt fails as it would for a vehicle that is
    /// not connected, the outbox row stays unacknowledged, and the server's own replay after the recovery report
    /// (<c>OnboardRecoveryCoordinator.ReplayPendingCommandsAsync</c>) delivers it. That replay only reaches the
    /// vehicle if the connection is routable by then, which is the ordering the fix has to get right: routable
    /// after the recovery report's answer is written, before the replay runs.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task ARecoveryPushHeldBackDuringTheHandshakeReachesTheVehicleAfterIt()
    {
        await using Rig rig = await Rig.StartAsync();
        string snapshotId = Guid.NewGuid().ToString("D");
        Exception? pushOutcome = null;
        bool pushed = false;
        rig.Relay.BeforeForwarding = async messageType =>
        {
            if (messageType != "SafetyStateSnapshot" || pushed)
            {
                return;
            }
            pushed = true;
            // The server is blocked reading this very line, so its processor is not using the database now.
            await using ControlServerDbContext context = rig.NewContext();
            OnboardJourneyPublisher publisher = new(new WireToGateStore(context), rig.Peer, TimeProvider.System);
            await publisher.QueueExceptionRecoverySessionSnapshotAsync(
                snapshotId, AgvId, sessionGeneration: 1, RecoverySession(), CancellationToken.None);
            try
            {
                await publisher.SendPersistedAsync(snapshotId, CancellationToken.None);
            }
            catch (Exception error)
            {
                pushOutcome = error;
            }
        };

        await rig.Onboard.StartAsync(rig.Lifetime.Token).WaitAsync(Guard, TestContext.Current.CancellationToken);
        WireEvent[] inbound = await rig.WaitForInboundAsync("ExceptionRecoverySessionSnapshot");

        Assert.True(pushed, "The relay never reached the handshake step it pushes at.");
        Assert.IsAssignableFrom<IOException>(pushOutcome);
        Assert.Equal(1L, rig.Engine.Snapshot().State.SessionGeneration);
        string[] types = [.. inbound.Select(item => item.MessageType)];
        int readiness = Array.IndexOf(types, "SessionReadiness");
        WireEvent delivered = Assert.Single(inbound, item => item.MessageType == "ExceptionRecoverySessionSnapshot");
        Assert.Equal(snapshotId, delivered.MessageId);
        Assert.True(
            readiness >= 0 && Array.IndexOf(types, "ExceptionRecoverySessionSnapshot") > readiness,
            "The held-back snapshot must arrive after the handshake's last answer: " + string.Join(", ", types));
    }

    /// <summary>Once the handshake is done a push goes straight through, with no replay involved.</summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task OnceTheHandshakeIsDoneAPushGoesStraightThrough()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.Onboard.StartAsync(rig.Lifetime.Token).WaitAsync(Guard, TestContext.Current.CancellationToken);

        // The vehicle reads SessionReadiness a moment before the server files the connection, so the push is
        // retried until it is accepted. What is asserted is that it is accepted and arrives, not how fast.
        await rig.SendWhenRoutableAsync(Push("FaultCargoRecoveryCommand"));
        WireEvent[] inbound = await rig.WaitForInboundAsync("FaultCargoRecoveryCommand");

        Assert.Single(inbound, item => item.MessageType == "FaultCargoRecoveryCommand");
        Assert.Equal("READY", rig.Engine.Snapshot().State.Readiness);
    }

    /// <summary>
    /// A new <c>SessionHello</c> on a connection whose handshake was done starts a new handshake, and the gate
    /// closes again before its answer goes out.
    /// </summary>
    /// <remarks>
    /// The onboard opens a new socket for every handshake, so no vehicle does this today. It is here because the
    /// gate is "this connection's handshake is done", not "this connection once finished a handshake": the
    /// processor starts a second handshake on the same connection when it is asked to, and a gate that stayed open
    /// through it would be the same defect one step removed. The relay writes the hello straight to the server.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task ASecondHandshakeOnTheSameConnectionClosesTheGateAgain()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.Onboard.StartAsync(rig.Lifetime.Token).WaitAsync(Guard, TestContext.Current.CancellationToken);
        await rig.SendWhenRoutableAsync(Push("FaultCargoRecoveryCommand"));

        await rig.Relay.InjectToServerAsync(SessionHello());
        await rig.WaitForInboundCountAsync("SessionAccepted", 2);

        await Assert.ThrowsAsync<IOException>(() =>
            rig.Peer.SendAsync(Push("FaultCargoRecoveryCommand"), TestContext.Current.CancellationToken));
    }

    private static string SessionHello() => ProtocolEnvelope.Serialize(
        "SessionHello",
        Guid.NewGuid().ToString("D"),
        correlationId: null,
        AgvId,
        sessionGeneration: null,
        DateTimeOffset.UtcNow,
        new
        {
            onboardInstanceId = Guid.NewGuid().ToString("D"),
            onboardBuildCommit = "PUSH_GATE_TEST",
            supportedProtocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
            profileId = ProtocolCandidateIdentity.ProfileId,
            protocolReleaseIdentity = ProtocolEnvelope.ReleaseIdentity(),
            credentialProof = Credential
        });

    /// <summary>
    /// A server-originated line the synthetic vehicle takes no action on, so it can only show up in its wire log.
    /// </summary>
    private static ReadOnlyMemory<byte> Push(string messageType) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        messageType,
        messageId = Guid.NewGuid().ToString("D"),
        correlationId = (string?)null,
        agvId = AgvId,
        sessionGeneration = 1L,
        payload = new { }
    }) + "\n");

    private static ExceptionRecoverySessionProjection RecoverySession() => new(
        ExceptionRecoverySessionId: Guid.NewGuid().ToString("D"),
        RecoverySessionRevision: 1,
        State: "OPEN",
        AdministratorId: "admin-01",
        AdministratorRole: "MAINTENANCE_ADMINISTRATOR",
        EventId: Guid.NewGuid().ToString("D"),
        DemandId: null,
        SlotOperationAttemptId: null,
        Slots: [1],
        SelectedAction: null,
        AllowedActions: ["FORCED_MECHANICAL_RECOVERY"],
        BlockingFacts: []);

    /// <summary>
    /// A real listener and message processor over an in-memory database, the synthetic vehicle, and the relay
    /// between them.
    /// </summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ControlServerDbContext _serverContext;
        private readonly ServiceProvider _provider;
        private readonly OnboardTcpServer _server;

        private Rig(
            SqliteConnection connection,
            ControlServerDbContext serverContext,
            ServiceProvider provider,
            OnboardTcpServer server,
            OnboardPeer peer,
            LineRelay relay,
            CommandEngine<FakeOnboardState> engine,
            OnboardPeerSession onboard)
        {
            _connection = connection;
            _serverContext = serverContext;
            _provider = provider;
            _server = server;
            Peer = peer;
            Relay = relay;
            Engine = engine;
            Onboard = onboard;
        }

        public OnboardPeer Peer { get; }

        public LineRelay Relay { get; }

        public CommandEngine<FakeOnboardState> Engine { get; }

        public OnboardPeerSession Onboard { get; }

        public CancellationTokenSource Lifetime { get; } = new(TimeSpan.FromSeconds(60));

        public static async Task<Rig> StartAsync()
        {
            Environment.SetEnvironmentVariable(CredentialVariable, Credential);
            SqliteConnection connection = new("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            ControlServerDbContext context = NewContext(connection);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = CredentialVariable
                })
                .Build();
            // The one peer the server routes through is also the one the processor's replay sends through,
            // as in the host, where both resolve the same singleton.
            OnboardPeer peer = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context, new WireToGateStore(context), TimeProvider.System, configuration, peer);
            ServiceCollection services = new();
            services.AddScoped(_ => processor);
            ServiceProvider provider = services.BuildServiceProvider();

            int serverPort = ReserveFreePort();
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

            LineRelay relay = LineRelay.Start(serverPort);
            CommandEngine<FakeOnboardState> engine = new("push-gate-test", () => new FakeOnboardState());
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

        public ControlServerDbContext NewContext() => NewContext(_connection);

        /// <summary>What the vehicle has read, in order, heartbeat answers left out.</summary>
        public WireEvent[] Inbound() =>
            [.. Onboard.Wire().Where(item => item.Direction == "in" && item.MessageType != "HeartbeatAck")];

        public async Task<WireEvent[]> WaitForInboundAsync(string messageType)
        {
            using CancellationTokenSource guard = new(Guard);
            while (!Inbound().Any(item => item.MessageType == messageType))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), guard.Token);
            }
            return Inbound();
        }

        public async Task WaitForInboundCountAsync(string messageType, int count)
        {
            using CancellationTokenSource guard = new(Guard);
            while (Inbound().Count(item => item.MessageType == messageType) < count)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), guard.Token);
            }
        }

        public async Task SendWhenRoutableAsync(ReadOnlyMemory<byte> line)
        {
            using CancellationTokenSource guard = new(Guard);
            while (true)
            {
                try
                {
                    await Peer.SendAsync(line, guard.Token);
                    return;
                }
                catch (IOException)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(20), guard.Token);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Onboard.DisposeAsync();
            await Relay.DisposeAsync();
            await _server.StopAsync(CancellationToken.None);
            _server.Dispose();
            await _provider.DisposeAsync();
            await _serverContext.DisposeAsync();
            await _connection.DisposeAsync();
            Lifetime.Dispose();
        }

        private static ControlServerDbContext NewContext(SqliteConnection connection) =>
            new(new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(connection).Options);

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
    /// Forwards one connection line by line, lets a test act before a vehicle line reaches the server, and lets
    /// it write a line to the server as if the vehicle had.
    /// </summary>
    private sealed class LineRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _serverPort;
        private readonly CancellationTokenSource _stopping = new();
        private readonly SemaphoreSlim _toServerGate = new(1, 1);
        private readonly TaskCompletionSource<StreamWriter> _toServer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<IDisposable> _owned = [];
        private Task _running = Task.CompletedTask;

        private LineRelay(TcpListener listener, int serverPort)
        {
            _listener = listener;
            _serverPort = serverPort;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>Runs before each vehicle line is forwarded, with that line's messageType.</summary>
        public Func<string, Task>? BeforeForwarding { get; set; }

        public static LineRelay Start(int serverPort)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            LineRelay relay = new(listener, serverPort);
            relay._running = relay.RunAsync();
            return relay;
        }

        public async Task InjectToServerAsync(string line)
        {
            StreamWriter writer = await _toServer.Task.WaitAsync(Guard);
            await WriteToServerAsync(writer, line, _stopping.Token);
        }

        private async Task RunAsync()
        {
            CancellationToken token = _stopping.Token;
            TcpClient vehicle = await _listener.AcceptTcpClientAsync(token);
            TcpClient server = new();
            _owned.Add(vehicle);
            _owned.Add(server);
            await server.ConnectAsync(IPAddress.Loopback, _serverPort, token);
            await using StreamWriter toServer = NewWriter(server.GetStream());
            await using StreamWriter toVehicle = NewWriter(vehicle.GetStream());
            _toServer.TrySetResult(toServer);
            await Task.WhenAny(
                PumpAsync(vehicle.GetStream(), line => ToServerAsync(toServer, line, token), token),
                PumpAsync(server.GetStream(), line => toVehicle.WriteLineAsync(line.AsMemory(), token), token));
            vehicle.Dispose();
            server.Dispose();
        }

        private async Task ToServerAsync(StreamWriter writer, string line, CancellationToken token)
        {
            if (BeforeForwarding is { } hook)
            {
                using JsonDocument document = JsonDocument.Parse(line);
                await hook(document.RootElement.GetProperty("messageType").GetString() ?? string.Empty);
            }
            await WriteToServerAsync(writer, line, token);
        }

        private async Task WriteToServerAsync(StreamWriter writer, string line, CancellationToken token)
        {
            await _toServerGate.WaitAsync(token);
            try
            {
                await writer.WriteLineAsync(line.AsMemory(), token);
            }
            finally
            {
                _toServerGate.Release();
            }
        }

        private static async Task PumpAsync(Stream from, Func<string, Task> forward, CancellationToken token)
        {
            using StreamReader reader = new(from, new UTF8Encoding(false), false, leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync(token) is { } line)
                {
                    await forward(line);
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Either side closing ends the relay.
            }
        }

        private static StreamWriter NewWriter(Stream stream) =>
            new(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };

        public async ValueTask DisposeAsync()
        {
            await _stopping.CancelAsync();
            _listener.Stop();
            foreach (IDisposable owned in _owned)
            {
                owned.Dispose();
            }
            try
            {
                await _running;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Torn down.
            }
            _stopping.Dispose();
            _toServerGate.Dispose();
        }
    }
}
