using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ControlServer.FakeOnboard;
using ControlServer.TestDoubles;
using Microsoft.Extensions.Configuration;

namespace ControlServer.Tests;

/// <summary>
/// The synthetic peer's answer cache: each business request is answered under its own key, and a resend of
/// that same request gets the identical answer back.
/// </summary>
/// <remarks>
/// Found in the review of control-server#75's PR: sublot entry and the pre-departure safety check were cached
/// under the fixed keys <c>sublot</c> and <c>safety-check</c>, so the second trip on one connection was answered
/// with the first trip's message and a single fake vehicle could not finish two trips. The test stands in for
/// ControlServer with a loopback listener that walks the peer through the five-step handshake and then sends
/// requests the way the journey runtime does.
/// </remarks>
public sealed class FakeOnboardRequestAnswerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task TwoSublotEntryRequestsOnOneConnectionGetTheirOwnSublotsAndAResendReplaysTheFirstAnswer()
    {
        await using ServerSide server = await ServerSide.ConnectAsync();

        string firstMessageId = Guid.NewGuid().ToString("D");
        string firstSession = Guid.NewGuid().ToString("D");
        string secondSession = Guid.NewGuid().ToString("D");

        await server.SendAsync(ServerSide.SublotEntryRequested(firstMessageId, firstSession, 3, "N1-3"));
        JsonElement first = await server.ReadAsync("SublotSubmitted");
        await server.SendAsync(ServerSide.SublotEntryRequested(Guid.NewGuid().ToString("D"), secondSession, 7, "N2-5"));
        JsonElement second = await server.ReadAsync("SublotSubmitted");

        Assert.Equal("N1-3", first.GetProperty("payload").GetProperty("sublot").GetString());
        Assert.Equal(firstSession, first.GetProperty("payload").GetProperty("operationSessionId").GetString());
        Assert.Equal("N2-5", second.GetProperty("payload").GetProperty("sublot").GetString());
        Assert.Equal(secondSession, second.GetProperty("payload").GetProperty("operationSessionId").GetString());
        Assert.Equal(7, second.GetProperty("payload").GetProperty("worklistRevision").GetInt64());

        // ControlServer replays an unsettled request under the same messageId and content. The peer must send
        // back the answer it already gave, not a new one.
        await server.SendAsync(ServerSide.SublotEntryRequested(firstMessageId, firstSession, 3, "N1-3"));
        JsonElement replayed = await server.ReadAsync("SublotSubmitted");

        Assert.Equal(first.GetRawText(), replayed.GetRawText());
    }

    [Fact]
    public async Task TwoSafetyChecksOnOneConnectionGetTheirOwnAnswersAndAResendReplaysTheFirstAnswer()
    {
        await using ServerSide server = await ServerSide.ConnectAsync();

        string firstMessageId = Guid.NewGuid().ToString("D");
        string firstCheck = Guid.NewGuid().ToString("D");
        string secondCheck = Guid.NewGuid().ToString("D");

        await server.SendAsync(ServerSide.PreDepartureSafetyCheck(firstMessageId, firstCheck));
        JsonElement first = await server.ReadAsync("PreDepartureSafetyCheckResult");
        await server.SendAsync(ServerSide.PreDepartureSafetyCheck(Guid.NewGuid().ToString("D"), secondCheck));
        JsonElement second = await server.ReadAsync("PreDepartureSafetyCheckResult");

        Assert.Equal(firstCheck, first.GetProperty("payload").GetProperty("preDepartureSafetyCheckId").GetString());
        Assert.Equal(secondCheck, second.GetProperty("payload").GetProperty("preDepartureSafetyCheckId").GetString());
        Assert.Equal(secondCheck, second.GetProperty("correlationId").GetString());

        await server.SendAsync(ServerSide.PreDepartureSafetyCheck(firstMessageId, firstCheck));
        JsonElement replayed = await server.ReadAsync("PreDepartureSafetyCheckResult");

        Assert.Equal(first.GetRawText(), replayed.GetRawText());
    }

    /// <summary>ControlServer's end of the session, just enough of it to get the peer to READY.</summary>
    private sealed class ServerSide : IAsyncDisposable
    {
        private const long Generation = 1;
        private static readonly string[] EntryMethods = ["SCANNER"];
        private static readonly string[] HandshakeSnapshots =
            ["CapabilitySnapshot", "SafetyStateSnapshot", "OnboardAlarmSnapshot"];

        private readonly TcpListener listener;
        private readonly OnboardPeerSession peer;
        private readonly CancellationTokenSource lifetime;
        private TcpClient? accepted;
        private StreamReader? reader;
        private StreamWriter? writer;

        private ServerSide(TcpListener listener, OnboardPeerSession peer, CancellationTokenSource lifetime)
        {
            this.listener = listener;
            this.peer = peer;
            this.lifetime = lifetime;
        }

        public static async Task<ServerSide> ConnectAsync()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            string credentialVariable = "FAKE_ONBOARD_REQUEST_ANSWER_TEST_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(credentialVariable, "test-credential");
            FakeOnboardOptions options = new()
            {
                Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                AgvId = "AGV-FAKE-001",
                CredentialEnvironmentVariable = credentialVariable
            };
            OnboardPeerSession peer = new(
                new CommandEngine<FakeOnboardState>("fake-onboard-test", () => new FakeOnboardState()),
                options,
                SlotStateSeed.Read(new ConfigurationBuilder().Build()));
            CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(60));
            ServerSide server = new(listener, peer, lifetime);

            Task<TcpClient> accepting = listener.AcceptTcpClientAsync(lifetime.Token).AsTask();
            Task starting = peer.StartAsync(lifetime.Token);
            server.accepted = await accepting.WaitAsync(Timeout);
            NetworkStream stream = server.accepted.GetStream();
            server.reader = new StreamReader(stream, new UTF8Encoding(false), false, leaveOpen: true);
            server.writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };

            await server.ReadAsync("SessionHello");
            await server.SendAsync(new
            {
                messageType = "SessionAccepted",
                messageId = Guid.NewGuid().ToString("D"),
                sessionGeneration = Generation
            });
            foreach (string snapshot in HandshakeSnapshots)
            {
                JsonElement message = await server.ReadAsync(snapshot);
                await server.SendAsync(Reply("SnapshotAppliedAck", message.GetProperty("messageId").GetString()));
            }
            JsonElement report = await server.ReadAsync("RecoveryStateReport");
            await server.SendAsync(Reply("DurableAck", report.GetProperty("messageId").GetString()));
            await server.SendAsync(new
            {
                messageType = "SessionReadiness",
                messageId = Guid.NewGuid().ToString("D"),
                payload = new { readiness = "READY" }
            });
            await starting.WaitAsync(Timeout);
            return server;
        }

        public static object SublotEntryRequested(string messageId, string operationSessionId, long worklistRevision, string sublot) =>
            Request("SublotEntryRequested", messageId, new
            {
                operationSessionId,
                stationId = "STATION-MIXED",
                worklistRevision,
                expectedSublots = new[] { sublot },
                entryMethods = EntryMethods,
                expiresOnRevisionChange = true
            });

        public static object PreDepartureSafetyCheck(string messageId, string preDepartureSafetyCheckId) =>
            Request("PreDepartureSafetyCheck", messageId, new
            {
                preDepartureSafetyCheckId,
                demandId = Guid.NewGuid().ToString("D"),
                movementLegId = Guid.NewGuid().ToString("D"),
                expectedSafetyStateVersion = 1L,
                targetStationId = "GATE-1"
            });

        public async Task SendAsync(object message) =>
            await writer!.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), lifetime.Token)
                .WaitAsync(Timeout);

        /// <summary>Reads until the named message arrives, skipping the heartbeats the peer sends meanwhile.</summary>
        public async Task<JsonElement> ReadAsync(string messageType)
        {
            while (true)
            {
                string line = await reader!.ReadLineAsync(lifetime.Token).AsTask().WaitAsync(Timeout)
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

        private static object Request(string messageType, string messageId, object payload) => new
        {
            messageType,
            messageId,
            sessionGeneration = Generation,
            payload
        };

        private static object Reply(string messageType, string? correlationId) => new
        {
            messageType,
            messageId = Guid.NewGuid().ToString("D"),
            correlationId,
            sessionGeneration = Generation,
            payload = new { }
        };

        public async ValueTask DisposeAsync()
        {
            await peer.DisposeAsync();
            reader?.Dispose();
            if (writer is not null)
            {
                try
                {
                    await writer.DisposeAsync();
                }
                catch (IOException)
                {
                    // The peer already closed its end.
                }
            }
            accepted?.Dispose();
            listener.Stop();
            lifetime.Dispose();
        }
    }
}
