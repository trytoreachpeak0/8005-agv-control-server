using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ControlServer.FakeOnboard;
using ControlServer.TestDoubles;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

/// <summary>
/// The synthetic peer after a handshake that failed part-way: it reports itself disconnected, closes the socket
/// it opened, and a later reconnect opens a fresh session.
/// </summary>
/// <remarks>
/// control-server#277. <c>load-cancelled-before-sublot</c> failed when the server pushed an
/// <c>UpcomingStopPlanSnapshot</c> into a reconnect handshake (the server half is control-server#259). The peer
/// threw, as it should, but left its client, writer and lifetime assigned: <c>IsConnected</c> reads only the
/// client and whether the server closed it, so the peer went on reporting a session that never reached READY as
/// connected, and <c>PUT /connection {connected:true}</c> -- which acts only when the requested state differs
/// from <c>IsConnected</c> -- would answer "no change" and never connect again. The loopback listener here plays
/// the server and breaks the handshake at the same step the sample did.
/// </remarks>
public sealed class FakeOnboardFailedHandshakeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task AFailedHandshakeRethrowsAndLeavesThePeerDisconnected()
    {
        await using Rig rig = Rig.Create();
        Task starting = rig.Peer.StartAsync(rig.Lifetime.Token);
        await using ServerConnection first = await rig.AcceptAsync();

        await first.BreakHandshakeAfterCapabilitySnapshotAsync();

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => starting.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Contains("received 'UpcomingStopPlanSnapshot'", error.Message, StringComparison.Ordinal);
        // The control plane's PUT /connection connects only when this differs from what is asked for, so this is
        // what decides whether a retry after the failure does anything at all.
        Assert.False(rig.Peer.IsConnected);
        FakeOnboardState state = rig.Engine.Snapshot().State;
        Assert.Equal("DISCONNECTED", state.Readiness);
        Assert.Equal("HANDSHAKE_FAILED", state.ReadinessReasonCode);
    }

    [Fact]
    public async Task AFailedHandshakeClosesTheSocketItOpened()
    {
        await using Rig rig = Rig.Create();
        Task starting = rig.Peer.StartAsync(rig.Lifetime.Token);
        await using ServerConnection first = await rig.AcceptAsync();

        await first.BreakHandshakeAfterCapabilitySnapshotAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => starting.WaitAsync(Timeout, TestContext.Current.CancellationToken));

        // Left open, the socket was never read again while the server kept writing into it; the server logged
        // that as a reset only once the process was torn down, which is what made the red look like a transport
        // fault. A closed socket reads as end of stream here, or as a reset if bytes were still unread on the
        // peer's side -- both mean the peer let go of it. A socket still held open times out.
        Assert.True(await first.ReadEndAsync(), "The fake onboard kept the failed session's socket open.");
    }

    [Fact]
    public async Task AReconnectAfterAFailedHandshakeReachesReadyOnANewConnection()
    {
        // Not the red for the fix: ReconnectAsync does not consult IsConnected, so it reconnected before the fix
        // too (leaking the old socket). It is here so that tearing the failed session down cannot break the next
        // one -- disposing the host-stopping token or leaving a field the next StartAsync trips over would.
        await using Rig rig = Rig.Create();
        Task starting = rig.Peer.StartAsync(rig.Lifetime.Token);
        await using ServerConnection first = await rig.AcceptAsync();
        await first.BreakHandshakeAfterCapabilitySnapshotAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => starting.WaitAsync(Timeout, TestContext.Current.CancellationToken));

        Task reconnecting = rig.Peer.ReconnectAsync();
        await using ServerConnection second = await rig.AcceptAsync();
        await second.CompleteHandshakeAsync(generation: 2);
        await reconnecting.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.True(rig.Peer.IsConnected);
        FakeOnboardState state = rig.Engine.Snapshot().State;
        Assert.Equal("READY", state.Readiness);
        Assert.Equal(2L, state.SessionGeneration);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly TcpListener listener;

        private Rig(TcpListener listener, CommandEngine<FakeOnboardState> engine, OnboardPeerSession peer)
        {
            this.listener = listener;
            Engine = engine;
            Peer = peer;
        }

        public CommandEngine<FakeOnboardState> Engine { get; }

        public OnboardPeerSession Peer { get; }

        public CancellationTokenSource Lifetime { get; } = new(TimeSpan.FromSeconds(60));

        public static Rig Create()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            string credentialVariable = "FAKE_ONBOARD_FAILED_HANDSHAKE_TEST_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(credentialVariable, "test-credential");
            FakeOnboardOptions options = new()
            {
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                AgvId = "AGV-FAKE-001",
                CredentialEnvironmentVariable = credentialVariable
            };
            CommandEngine<FakeOnboardState> engine = new("fake-onboard-test", () => new FakeOnboardState());
            OnboardPeerSession peer = new(engine, options, SlotStateSeed.Read(new ConfigurationBuilder().Build()));
            return new Rig(listener, engine, peer);
        }

        public async Task<ServerConnection> AcceptAsync() =>
            new(await listener.AcceptTcpClientAsync(Lifetime.Token).AsTask().WaitAsync(Timeout), Lifetime.Token);

        public async ValueTask DisposeAsync()
        {
            await Peer.DisposeAsync();
            listener.Stop();
            Lifetime.Dispose();
        }
    }

    /// <summary>ControlServer's end of one connection, just enough of the five-step handshake.</summary>
    private sealed class ServerConnection : IAsyncDisposable
    {
        private static readonly string[] HandshakeSnapshots =
            ["CapabilitySnapshot", "SafetyStateSnapshot", "OnboardAlarmSnapshot"];

        private readonly TcpClient client;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private readonly CancellationToken cancellationToken;

        public ServerConnection(TcpClient client, CancellationToken cancellationToken)
        {
            this.client = client;
            this.cancellationToken = cancellationToken;
            NetworkStream stream = client.GetStream();
            reader = new StreamReader(stream, new UTF8Encoding(false), false, leaveOpen: true);
            writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        }

        /// <summary>
        /// The shape of control-server#277's sample: accepted, the capability snapshot sent, and then a business
        /// snapshot where the acknowledgement belongs.
        /// </summary>
        public async Task BreakHandshakeAfterCapabilitySnapshotAsync()
        {
            await ReadAsync("SessionHello");
            await SendAsync(new { messageType = "SessionAccepted", messageId = NewId(), sessionGeneration = 1L });
            await ReadAsync("CapabilitySnapshot");
            await SendAsync(new
            {
                messageType = "UpcomingStopPlanSnapshot",
                messageId = NewId(),
                sessionGeneration = 1L,
                payload = new { }
            });
        }

        public async Task CompleteHandshakeAsync(long generation)
        {
            await ReadAsync("SessionHello");
            await SendAsync(new { messageType = "SessionAccepted", messageId = NewId(), sessionGeneration = generation });
            foreach (string snapshot in HandshakeSnapshots)
            {
                JsonElement message = await ReadAsync(snapshot);
                await SendAsync(Reply("SnapshotAppliedAck", message.GetProperty("messageId").GetString(), generation));
            }
            JsonElement report = await ReadAsync("RecoveryStateReport");
            await SendAsync(Reply("DurableAck", report.GetProperty("messageId").GetString(), generation));
            await SendAsync(new
            {
                messageType = "SessionReadiness",
                messageId = NewId(),
                sessionGeneration = generation,
                payload = new { readiness = "READY" }
            });
        }

        /// <summary>True once the peer has let go of this connection; false if it is still held open.</summary>
        public async Task<bool> ReadEndAsync()
        {
            try
            {
                while (true)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    if (line is null)
                    {
                        return true;
                    }
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        private async Task SendAsync(object message) =>
            await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), cancellationToken).WaitAsync(Timeout);

        private async Task<JsonElement> ReadAsync(string messageType)
        {
            while (true)
            {
                string line = await reader.ReadLineAsync(cancellationToken).AsTask().WaitAsync(Timeout)
                    ?? throw new EndOfStreamException("The fake onboard closed the connection.");
                using JsonDocument document = JsonDocument.Parse(line);
                string? actual = document.RootElement.GetProperty("messageType").GetString();
                if (actual == "Heartbeat")
                {
                    continue;
                }
                Assert.Equal(messageType, actual);
                return document.RootElement.Clone();
            }
        }

        private static object Reply(string messageType, string? correlationId, long generation) => new
        {
            messageType,
            messageId = NewId(),
            correlationId,
            sessionGeneration = generation,
            payload = new { }
        };

        private static string NewId() => Guid.NewGuid().ToString("D");

        public async ValueTask DisposeAsync()
        {
            reader.Dispose();
            try
            {
                await writer.DisposeAsync();
            }
            catch (IOException)
            {
                // The peer already closed its end.
            }
            client.Dispose();
        }
    }
}
