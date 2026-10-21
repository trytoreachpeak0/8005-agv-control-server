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

public sealed class OnboardTcpServerTests
{
    private const string HandshakeAgvId = "AGV-483";
    private const string HandshakeCredentialVariable = "CONTROL_SERVER_ONBOARD_CREDENTIAL_TCP_HANDSHAKE_TESTS";
    private const string HandshakeCredential = "tcp-handshake-test-credential";
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task NonLoopbackListenerStartsAndAcceptsPlaintextConnections()
    {
        int port = ReserveFreePort();
        OnboardTransportOptions options = new()
        {
            Enabled = true,
            ListenAddress = "0.0.0.0",
            Port = port
        };
        await using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        using OnboardTcpServer server = new(
            Options.Create(options),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new OnboardPeer(),
            NullLogger<OnboardTcpServer>.Instance);

        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            Assert.True(client.Connected);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// control-server#483, incremental review: a vehicle in its handshake is not "gone". From the moment its SessionHello is
    /// on the wire until its connection becomes routable, the server's presence says it is handshaking -- not connected, not
    /// absent -- so an administrator cannot close its recovery session while it is about to replay its results. When the
    /// connection ends without finishing the handshake, the mark goes with it.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AVehicleIsHandshakingFromItsSessionHelloUntilItsConnectionEnds()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        Environment.SetEnvironmentVariable(HandshakeCredentialVariable, HandshakeCredential);
        await using SqliteConnection database = new("Data Source=:memory:");
        await database.OpenAsync(token);
        await using ControlServerDbContext context = new(
            new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(database).Options);
        await context.Database.EnsureCreatedAsync(token);
        OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
            context,
            new WireToGateStore(context),
            new FixedClock(),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OnboardTransport:CredentialEnvironmentVariable"] = HandshakeCredentialVariable
                })
                .Build());
        ServiceCollection services = new();
        services.AddScoped(_ => processor);
        await using ServiceProvider provider = services.BuildServiceProvider();
        OnboardPeer peer = new();
        int port = ReserveFreePort();
        using OnboardTcpServer server = new(
            Options.Create(new OnboardTransportOptions { Enabled = true, ListenAddress = "127.0.0.1", Port = port }),
            provider.GetRequiredService<IServiceScopeFactory>(),
            peer,
            NullLogger<OnboardTcpServer>.Instance);
        await server.StartAsync(token);
        try
        {
            Assert.False(peer.IsHandshaking(HandshakeAgvId));
            using (TcpClient client = new())
            {
                await client.ConnectAsync(IPAddress.Loopback, port, token);
                NetworkStream stream = client.GetStream();
                await using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true)
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };
                using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
                await writer.WriteLineAsync(SessionHello().AsMemory(), token);
                string? answer = await reader.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(20), token);
                Assert.Equal("SessionAccepted", MessageTypeOf(answer));

                Assert.True(peer.IsHandshaking(HandshakeAgvId));
                Assert.Null(peer.ConnectedSessionGeneration(HandshakeAgvId));
            }

            // The server reads EOF and ends the connection; the mark ends with it.
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (peer.IsHandshaking(HandshakeAgvId) && DateTime.UtcNow < deadline)
                await Task.Delay(20, token);
            Assert.False(peer.IsHandshaking(HandshakeAgvId), "The handshake mark outlived its connection.");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable(HandshakeCredentialVariable, null);
        }
    }

    /// <summary>
    /// The presence's own bookkeeping: a handshake mark ends when its connection is attached or when it ends, and only for
    /// that connection -- a second connection's handshake on the same vehicle keeps the vehicle handshaking.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task AHandshakeMarkEndsWhenItsConnectionIsAttachedOrEnds()
    {
        OnboardPeer peer = new();
        await using OnboardPeerConnection first = new(new MemoryStream());
        await using OnboardPeerConnection second = new(new MemoryStream());

        peer.BeginHandshake(HandshakeAgvId, first);
        peer.BeginHandshake(HandshakeAgvId, second);
        peer.EndHandshake(HandshakeAgvId, second);
        Assert.True(peer.IsHandshaking(HandshakeAgvId));

        peer.Attach(
            new OnboardConnectionState { AgvId = HandshakeAgvId, SessionGeneration = 7, HandshakeCompleted = true },
            first);
        Assert.False(peer.IsHandshaking(HandshakeAgvId));
        Assert.Equal(7, peer.ConnectedSessionGeneration(HandshakeAgvId));

        peer.Detach(HandshakeAgvId, first);
        Assert.Null(peer.ConnectedSessionGeneration(HandshakeAgvId));
    }

    /// <summary>
    /// control-server#310: a newer session for the same vehicle takes the route over -- the old connection leaves the table at
    /// once and is closed -- instead of the new one finishing its handshake and then being refused at the attach.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ANewerSessionTakesTheVehicleOverAndClosesTheOldConnection()
    {
        OnboardPeer peer = new();
        MemoryStream oldStream = new();
        await using OnboardPeerConnection old = new(oldStream);
        await using OnboardPeerConnection newer = new(new MemoryStream());
        peer.Attach(Session(7), old);

        long? supersededGeneration = peer.TakeOver(HandshakeAgvId, 8, newer);

        Assert.Equal(7, supersededGeneration);
        Assert.True(old.Superseded);
        Assert.False(oldStream.CanWrite, "The superseded connection's stream was not closed.");
        Assert.Null(peer.ConnectedSessionGeneration(HandshakeAgvId));
        // Nothing reaches the old connection: a line for its generation finds no route at all.
        await Assert.ThrowsAsync<OnboardConnectionUnavailableException>(
            () => peer.SendAsync(Line(7), TestContext.Current.CancellationToken));
        peer.Attach(Session(8), newer);
        Assert.Equal(8, peer.ConnectedSessionGeneration(HandshakeAgvId));
        Assert.False(newer.Superseded);
    }

    /// <summary>
    /// The takeover is by generation, and only ever forward: a hello for an older or the same generation, or the attached
    /// connection's own, leaves the route alone.
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task ATakeoverNeverMovesTheRouteBackwards()
    {
        OnboardPeer peer = new();
        MemoryStream attachedStream = new();
        await using OnboardPeerConnection attached = new(attachedStream);
        await using OnboardPeerConnection other = new(new MemoryStream());
        peer.Attach(Session(8), attached);

        Assert.Null(peer.TakeOver(HandshakeAgvId, 7, other));
        Assert.Null(peer.TakeOver(HandshakeAgvId, 8, other));
        Assert.Null(peer.TakeOver(HandshakeAgvId, 9, attached));

        Assert.Equal(8, peer.ConnectedSessionGeneration(HandshakeAgvId));
        Assert.True(attachedStream.CanWrite);
        Assert.False(attached.Superseded);
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => peer.Attach(Session(7), other));
        Assert.Contains("already attached", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The attach takes over too when the route is held by an older generation: an old connection whose handshake finished
    /// after the newer session's hello was accepted must not refuse the newer one (control-server#310).
    /// </summary>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task AnAttachForANewerGenerationTakesOverFromAnOlderOne()
    {
        OnboardPeer peer = new();
        MemoryStream oldStream = new();
        await using OnboardPeerConnection old = new(oldStream);
        await using OnboardPeerConnection newer = new(new MemoryStream());
        peer.Attach(Session(7), old);

        peer.Attach(Session(8), newer);

        Assert.Equal(8, peer.ConnectedSessionGeneration(HandshakeAgvId));
        Assert.True(old.Superseded);
        Assert.False(oldStream.CanWrite, "The superseded connection's stream was not closed.");
    }

    /// <summary>
    /// Through the real listener: a SessionHello the server accepts takes the vehicle over at once, before the new handshake
    /// is anywhere near done; one it refuses takes nothing (control-server#310). A bad credential must not be able to cut a
    /// working vehicle off.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait("IntegrationSlice", "FP-IS-05")]
    public async Task OnlyAnAcceptedSessionHelloTakesTheVehicleOver(bool accepted)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using ListenerRig rig = await ListenerRig.StartAsync();
        MemoryStream oldStream = new();
        await using OnboardPeerConnection old = new(oldStream);
        rig.Peer.Attach(Session(0), old);

        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, rig.Port, token);
        NetworkStream stream = client.GetStream();
        await using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
        await writer.WriteLineAsync(SessionHello(accepted ? HandshakeCredential : "not-the-credential").AsMemory(), token);
        string? answer;
        try
        {
            answer = await reader.ReadLineAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(20), token);
        }
        catch (IOException)
        {
            answer = null;
        }

        if (accepted)
        {
            Assert.Equal("SessionAccepted", MessageTypeOf(answer));
            // The takeover runs before the answer is written, so it has happened by the time the vehicle reads it.
            Assert.Null(rig.Peer.ConnectedSessionGeneration(HandshakeAgvId));
            Assert.True(old.Superseded);
            Assert.False(oldStream.CanWrite, "The superseded connection's stream was not closed.");
        }
        else
        {
            Assert.NotEqual("SessionAccepted", MessageTypeOf(answer));
            Assert.Equal(0, rig.Peer.ConnectedSessionGeneration(HandshakeAgvId));
            Assert.False(old.Superseded);
            Assert.True(oldStream.CanWrite);
        }
    }

    private static OnboardConnectionState Session(long generation) =>
        new() { AgvId = HandshakeAgvId, SessionGeneration = generation, HandshakeCompleted = true };

    private static ReadOnlyMemory<byte> Line(long generation) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        messageType = "FaultCargoRecoveryCommand",
        messageId = Guid.NewGuid().ToString("D"),
        agvId = HandshakeAgvId,
        sessionGeneration = generation
    }) + "\n");

    /// <summary>The real listener over a real processor and an in-memory database, as the handshake test above builds it.</summary>
    private sealed class ListenerRig : IAsyncDisposable
    {
        private readonly SqliteConnection _database;
        private readonly ControlServerDbContext _context;
        private readonly ServiceProvider _provider;
        private readonly OnboardTcpServer _server;

        private ListenerRig(
            SqliteConnection database, ControlServerDbContext context, ServiceProvider provider, OnboardTcpServer server,
            OnboardPeer peer, int port)
        {
            _database = database;
            _context = context;
            _provider = provider;
            _server = server;
            Peer = peer;
            Port = port;
        }

        public OnboardPeer Peer { get; }

        public int Port { get; }

        public static async Task<ListenerRig> StartAsync()
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            Environment.SetEnvironmentVariable(HandshakeCredentialVariable, HandshakeCredential);
            SqliteConnection database = new("Data Source=:memory:");
            await database.OpenAsync(token);
            ControlServerDbContext context = new(
                new DbContextOptionsBuilder<ControlServerDbContext>().UseSqlite(database).Options);
            await context.Database.EnsureCreatedAsync(token);
            OnboardPeer peer = new();
            OnboardMessageProcessor processor = TestOnboardProcessorFactory.Create(
                context,
                new WireToGateStore(context),
                new FixedClock(),
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["OnboardTransport:CredentialEnvironmentVariable"] = HandshakeCredentialVariable
                    })
                    .Build(),
                peer);
            ServiceCollection services = new();
            services.AddScoped(_ => processor);
            ServiceProvider provider = services.BuildServiceProvider();
            int port = ReserveFreePort();
            OnboardTcpServer server = new(
                Options.Create(new OnboardTransportOptions { Enabled = true, ListenAddress = "127.0.0.1", Port = port }),
                provider.GetRequiredService<IServiceScopeFactory>(),
                peer,
                NullLogger<OnboardTcpServer>.Instance);
            await server.StartAsync(token);
            return new ListenerRig(database, context, provider, server, peer, port);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _server.StopAsync(CancellationToken.None);
                _server.Dispose();
            }
            finally
            {
                Environment.SetEnvironmentVariable(HandshakeCredentialVariable, null);
                await _provider.DisposeAsync();
                await _context.DisposeAsync();
                await _database.DisposeAsync();
            }
        }
    }

    private static string? MessageTypeOf(string? line)
    {
        if (line is null) return null;
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("messageType").GetString();
    }

    private static string SessionHello(string credential = HandshakeCredential) => JsonSerializer.Serialize(new
    {
        protocolVersion = ProtocolCandidateIdentity.ProtocolVersion,
        profileId = ProtocolCandidateIdentity.ProfileId,
        protocolReleaseVersion = ProtocolCandidateIdentity.ReleaseVersion,
        protocolReleaseManifestSha256 = ProtocolCandidateIdentity.ManifestSha256,
        messageType = "SessionHello",
        messageId = Guid.NewGuid().ToString("D"),
        correlationId = (string?)null,
        agvId = HandshakeAgvId,
        sessionGeneration = (long?)null,
        sentAt = Now.ToString("O", CultureInfo.InvariantCulture),
        payload = new
        {
            onboardInstanceId = Guid.NewGuid().ToString("D"),
            onboardBuildCommit = "TCP_HANDSHAKE_TEST",
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
            credentialProof = credential
        }
    });

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
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
