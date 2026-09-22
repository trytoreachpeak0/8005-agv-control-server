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
/// that never pushes at all, so the next two prove that a push held back during the handshake is on the
/// vehicle afterwards -- a recovery push through the replay that follows the recovery report, a runtime push
/// through the runtime's per-round replay -- and another that pushing works normally once the handshake is
/// done. The rest keep the gate tied to the handshake in progress rather than to one that finished earlier on
/// the same connection, and keep it in the routing table itself rather than in its one caller.
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
    /// The same, one step later: a push attempted just before the recovery report reaches the server.
    /// </summary>
    /// <remarks>
    /// The step-3 push above cannot tell "routable once the recovery report is answered" from "routable once the
    /// alarm snapshot is answered". This one can: it is the last moment of the handshake, so a server that
    /// considered the handshake done any earlier -- <c>HandshakeCompleted</c> set on an earlier message, or an
    /// attach tied to something other than it -- puts the push where the vehicle expects the report's DurableAck.
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task APushJustBeforeTheRecoveryReportDoesNotReachTheVehicle()
    {
        await using Rig rig = await Rig.StartAsync();
        Exception? pushOutcome = null;
        bool pushed = false;
        rig.Relay.BeforeForwarding = async messageType =>
        {
            if (messageType != "RecoveryStateReport" || pushed)
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
        string[] expected =
            ["SessionAccepted", "SnapshotAppliedAck", "SnapshotAppliedAck", "SnapshotAppliedAck", "DurableAck", "SessionReadiness"];
        Assert.Equal(expected, rig.Inbound().Take(expected.Length).Select(item => item.MessageType));
        Assert.IsAssignableFrom<IOException>(pushOutcome);
    }

    /// <summary>
    /// A push built for the session before a reconnect does not reach the session after it, and the replay
    /// delivers it rebound to the new one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of "what was meant for before must not leak into after" (review of control-server#309). The
    /// runtime reads the session as Ready for generation N at the top of a round and pushes a few hundred
    /// milliseconds later; if the vehicle reconnected in between, the new connection is attached for N+1, and a
    /// peer that routes by agvId alone hands it a line stamped N. The onboard rejects that line in its receive
    /// loop (<c>STALE_SESSION_GENERATION</c>, <c>WireToGateProtocol.cs</c>) and the loop ends -- the connection
    /// drops, like the handshake case. The synthetic vehicle does not check generations, so this is asserted on
    /// the wire, where the relay reads every line's <c>sessionGeneration</c>.
    /// </para>
    /// <para>
    /// Refused, the line is what a vehicle that is not connected gets: the outbox row stays unacknowledged, and
    /// the replay rewrites its generation (<c>ReplayPendingForSessionAsync</c> rebinds a row stored for an older
    /// generation) and sends it once.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task APushForThePreviousSessionDoesNotReachTheNextOneAndIsReplayedIntoIt()
    {
        await using Rig rig = await Rig.StartAsync();
        await rig.Onboard.StartAsync(rig.Lifetime.Token).WaitAsync(Guard, TestContext.Current.CancellationToken);
        await rig.SendWhenRoutableAsync(Push("FaultCargoRecoveryCommand", sessionGeneration: 1));
        await rig.Onboard.DisconnectAsync();
        await rig.Onboard.ReconnectAsync().WaitAsync(Guard, TestContext.Current.CancellationToken);
        Assert.Equal(2L, rig.Engine.Snapshot().State.SessionGeneration);
        await rig.SendWhenRoutableAsync(Push("FaultCargoRecoveryCommand", sessionGeneration: 2));

        const string planId = "00000000-0000-4000-8000-000000000262";
        Exception? pushOutcome = null;
        await using (ControlServerDbContext context = rig.NewContext())
        {
            OnboardJourneyPublisher publisher = new(new WireToGateStore(context), rig.Peer, TimeProvider.System);
            try
            {
                // Built for generation 1, as a round that read the session before the reconnect would.
                await publisher.PublishUpcomingStopPlanAsync(
                    planId, AgvId, sessionGeneration: 1, Plan(), TestContext.Current.CancellationToken);
            }
            catch (Exception error)
            {
                pushOutcome = error;
            }
        }

        ServerLine[] stale = [.. rig.Relay.ToVehicle().Where(line => line.Connection == 2 && line.SessionGeneration != 2)];
        Assert.True(stale.Length == 0, "Lines of another session reached the new connection: " + string.Join(", ", stale.Select(line => line.ToString())));
        Assert.IsAssignableFrom<IOException>(pushOutcome);

        await using (ControlServerDbContext context = rig.NewContext())
        {
            OnboardJourneyPublisher publisher = new(new WireToGateStore(context), rig.Peer, TimeProvider.System);
            await publisher.ReplayPendingForSessionAsync(
                AgvId, 2, new HashSet<string>(StringComparer.Ordinal) { planId }, TestContext.Current.CancellationToken);
        }
        await rig.WaitForInboundAsync("UpcomingStopPlanSnapshot");

        ServerLine delivered = Assert.Single(rig.Relay.ToVehicle(), line => line.MessageId == planId);
        Assert.Equal(2, delivered.Connection);
        Assert.Equal(2L, delivered.SessionGeneration);
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

    /// <summary>
    /// The journey runtime's plan push, attempted mid-handshake, is refused and left in the outbox, and the
    /// runtime's per-round replay delivers it once the handshake is done.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shape of control-server#277's sample: <c>PublishPickupDispatchPlanOnceAsync</c> pushed an
    /// <c>UpcomingStopPlanSnapshot</c> into a reconnect handshake. The runtime does that when it read the
    /// session as Ready just before the vehicle's <c>SessionHello</c> started a new generation.
    /// </para>
    /// <para>
    /// The replay is called here by hand, with the arguments <c>JourneyRuntimeEngine</c> passes at the top of
    /// every round whose session is Ready (<c>ReplayPendingForSessionAsync(agvId, generation, RuntimeMessageIds)</c>),
    /// because running a whole journey round would add a RIoT double and a dispatched journey to prove one call.
    /// That the runtime makes this call every round, and that its id set names every message it publishes, is
    /// pinned by <c>Batch7StopDrivenAdvanceWireParityTests.AReconnectReplaysExactlyTheUnacknowledgedLinesOfThisJourney</c>. What this test adds is the part that is new: the
    /// refused push leaves the row unacknowledged, and the replay rebinds and delivers it after the handshake.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task ARuntimePushHeldBackDuringTheHandshakeIsDeliveredByTheNextRoundsReplay()
    {
        await using Rig rig = await Rig.StartAsync();
        const string planId = "00000000-0000-4000-8000-000000000259";
        Exception? pushOutcome = null;
        bool pushed = false;
        rig.Relay.BeforeForwarding = async messageType =>
        {
            if (messageType != "SafetyStateSnapshot" || pushed)
            {
                return;
            }
            pushed = true;
            await using ControlServerDbContext context = rig.NewContext();
            OnboardJourneyPublisher publisher = new(new WireToGateStore(context), rig.Peer, TimeProvider.System);
            try
            {
                await publisher.PublishUpcomingStopPlanAsync(planId, AgvId, sessionGeneration: 1, Plan(), CancellationToken.None);
            }
            catch (Exception error)
            {
                pushOutcome = error;
            }
        };

        await rig.Onboard.StartAsync(rig.Lifetime.Token).WaitAsync(Guard, TestContext.Current.CancellationToken);

        Assert.True(pushed, "The relay never reached the handshake step it pushes at.");
        Assert.IsAssignableFrom<IOException>(pushOutcome);
        await using (ControlServerDbContext context = rig.NewContext())
        {
            ProtocolOutboxRow held = await context.ProtocolOutbox.AsNoTracking()
                .SingleAsync(row => row.MessageId == planId, TestContext.Current.CancellationToken);
            Assert.Null(held.AcknowledgedAt);
        }
        Assert.DoesNotContain(rig.Inbound(), item => item.MessageId == planId);

        await Rig.WhenRoutableAsync(async () =>
        {
            await using ControlServerDbContext context = rig.NewContext();
            OnboardJourneyPublisher publisher = new(new WireToGateStore(context), rig.Peer, TimeProvider.System);
            await publisher.ReplayPendingForSessionAsync(
                AgvId, rig.Engine.Snapshot().State.SessionGeneration,
                new HashSet<string>(StringComparer.Ordinal) { planId }, TestContext.Current.CancellationToken);
        });
        WireEvent[] inbound = await rig.WaitForInboundAsync("UpcomingStopPlanSnapshot");

        string[] types = [.. inbound.Select(item => item.MessageType)];
        WireEvent delivered = Assert.Single(inbound, item => item.MessageType == "UpcomingStopPlanSnapshot");
        Assert.Equal(planId, delivered.MessageId);
        Assert.True(
            Array.IndexOf(types, "UpcomingStopPlanSnapshot") > Array.IndexOf(types, "SessionReadiness"),
            "The held-back plan must arrive after the handshake's last answer: " + string.Join(", ", types));
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

    /// <summary>
    /// The routing table itself refuses a connection whose handshake is not done, so a second caller of
    /// <c>Attach</c> cannot open the gate early by forgetting to check.
    /// </summary>
    [Theory]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [InlineData(false, 1L, AgvId)]
    [InlineData(true, null, AgvId)]
    [InlineData(true, 1L, null)]
    public async Task ThePeerRefusesAConnectionWhoseHandshakeIsNotDone(bool handshakeCompleted, long? generation, string? agvId)
    {
        OnboardPeer peer = new();
        using MemoryStream stream = new();
        await using OnboardPeerConnection connection = new(stream);
        OnboardConnectionState session = new()
        {
            AgvId = agvId,
            SessionGeneration = generation,
            HandshakeCompleted = handshakeCompleted
        };

        Assert.Throws<InvalidOperationException>(() => peer.Attach(session, connection));
        await Assert.ThrowsAsync<IOException>(() =>
            peer.SendAsync(Push("FaultCargoRecoveryCommand"), TestContext.Current.CancellationToken));
        Assert.Empty(stream.ToArray());
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
    private static ReadOnlyMemory<byte> Push(string messageType, long sessionGeneration = 1) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        messageType,
        messageId = Guid.NewGuid().ToString("D"),
        correlationId = (string?)null,
        agvId = AgvId,
        sessionGeneration,
        payload = new { }
    }) + "\n");

    private static UpcomingStopPlanProjection Plan() => new(
        1,
        [new UpcomingMovementLeg(
            "00000000-0000-4000-8000-000000000260",
            "TO_PICKUP",
            "BUSINESS",
            "00000000-0000-4000-8000-000000000261",
            null,
            1,
            "PICKUP-01",
            "26",
            "PLANNED")]);

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

        public Task SendWhenRoutableAsync(ReadOnlyMemory<byte> line) =>
            WhenRoutableAsync(() => Peer.SendAsync(line, CancellationToken.None));

        /// <summary>
        /// Retries a send until the connection is routable. The vehicle reads SessionReadiness a moment before
        /// the server files the connection, so the first attempt may still be refused as not connected.
        /// </summary>
        public static async Task WhenRoutableAsync(Func<Task> send)
        {
            using CancellationTokenSource guard = new(Guard);
            while (true)
            {
                try
                {
                    await send();
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
    /// Forwards the vehicle's connections line by line, one after another, and records every server line it
    /// forwards. A test can act before a vehicle line reaches the server, and can write a line to the server as
    /// if the vehicle had.
    /// </summary>
    private sealed class LineRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _serverPort;
        private readonly CancellationTokenSource _stopping = new();
        private readonly SemaphoreSlim _toServerGate = new(1, 1);
        private readonly ConcurrentQueue<ServerLine> _toVehicle = new();
        private readonly ConcurrentBag<IDisposable> _owned = [];
        private volatile StreamWriter? _toServer;
        private int _connections;
        private Task _running = Task.CompletedTask;

        private LineRelay(TcpListener listener, int serverPort)
        {
            _listener = listener;
            _serverPort = serverPort;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>Runs before each vehicle line is forwarded, with that line's messageType.</summary>
        public Func<string, Task>? BeforeForwarding { get; set; }

        /// <summary>Every line the server wrote, in order, with the relay connection it went out on.</summary>
        public ServerLine[] ToVehicle() => [.. _toVehicle];

        public static LineRelay Start(int serverPort)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            LineRelay relay = new(listener, serverPort);
            relay._running = relay.AcceptAsync();
            return relay;
        }

        public async Task InjectToServerAsync(string line)
        {
            StreamWriter writer = _toServer ?? throw new InvalidOperationException("No vehicle connection is open.");
            await WriteToServerAsync(writer, line, _stopping.Token);
        }

        private async Task AcceptAsync()
        {
            List<Task> serving = [];
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    TcpClient vehicle = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    serving.Add(ServeAsync(vehicle, Interlocked.Increment(ref _connections)));
                }
            }
            finally
            {
                await Task.WhenAll(serving);
            }
        }

        private async Task ServeAsync(TcpClient vehicle, int connection)
        {
            CancellationToken token = _stopping.Token;
            TcpClient server = new();
            _owned.Add(vehicle);
            _owned.Add(server);
            try
            {
                await server.ConnectAsync(IPAddress.Loopback, _serverPort, token);
                await using StreamWriter toServer = NewWriter(server.GetStream());
                await using StreamWriter toVehicle = NewWriter(vehicle.GetStream());
                _toServer = toServer;
                await Task.WhenAny(
                    PumpAsync(vehicle.GetStream(), line => ToServerAsync(toServer, line, token), token),
                    PumpAsync(server.GetStream(), line =>
                    {
                        _toVehicle.Enqueue(ServerLine.Parse(connection, line));
                        return toVehicle.WriteLineAsync(line.AsMemory(), token);
                    }, token));
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Either side closing ends this connection.
            }
            finally
            {
                vehicle.Dispose();
                server.Dispose();
            }
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

    /// <summary>One line the server wrote, as the relay saw it on the wire.</summary>
    private sealed record ServerLine(int Connection, string MessageType, string MessageId, long? SessionGeneration)
    {
        public static ServerLine Parse(int connection, string line)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            return new ServerLine(
                connection,
                root.GetProperty("messageType").GetString() ?? string.Empty,
                root.GetProperty("messageId").GetString() ?? string.Empty,
                root.TryGetProperty("sessionGeneration", out JsonElement generation) &&
                generation.ValueKind == JsonValueKind.Number
                    ? generation.GetInt64()
                    : null);
        }
    }
}
