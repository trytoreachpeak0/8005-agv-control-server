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

    private static string? MessageTypeOf(string? line)
    {
        if (line is null) return null;
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("messageType").GetString();
    }

    private static string SessionHello() => JsonSerializer.Serialize(new
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
            credentialProof = HandshakeCredential
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
