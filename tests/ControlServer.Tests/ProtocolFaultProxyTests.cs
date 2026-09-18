using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ControlServer.ProtocolFaultProxy;
using Microsoft.AspNetCore.Builder;

namespace ControlServer.Tests;

/// <summary>
/// Black-box tests over a real relay: a real TCP upstream standing in for ControlServer's onboard listener,
/// a real TCP client standing in for the onboard, and the proxy's HTTP control plane between the two.
/// The four real-onboard scenarios of control-server#88 read their verdicts off this relay's traffic log;
/// a relay that forwarded a line it was told to drop, or kept a link up it was told to close, would leave
/// those scenarios green about a fault that never happened. That is what these pin.
/// </summary>
public sealed class ProtocolFaultProxyTests
{
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task LinesCrossBothWaysByteForByteAndTheLogNamesEachEnvelope()
    {
        await using Upstream upstream = await Upstream.StartAsync();
        await using ProxyFixture proxy = await ProxyFixture.StartAsync(upstream);
        await using Link link = await proxy.ConnectAsync(upstream);

        const string hello = """{"messageType":"SessionHello","messageId":"m-1","sessionGeneration":null,"payload":{"x":1}}""";
        await link.Onboard.SendAsync(hello);
        Assert.Equal(hello, await link.Server.ReceiveAsync());

        const string accepted = """{"messageType":"SessionAccepted","messageId":"m-2","correlationId":"m-1","sessionGeneration":7,"payload":{}}""";
        await link.Server.SendAsync(accepted);
        Assert.Equal(accepted, await link.Onboard.ReceiveAsync());

        JsonElement traffic = await proxy.TrafficAsync();
        JsonElement[] lines = [.. traffic.GetProperty("lines").EnumerateArray()];
        Assert.Equal(2, lines.Length);
        Assert.Equal(("onboard->server", "SessionHello", "m-1"), Describe(lines[0]));
        Assert.Equal(("server->onboard", "SessionAccepted", "m-2"), Describe(lines[1]));
        Assert.Equal(7, lines[1].GetProperty("sessionGeneration").GetInt64());
        Assert.Equal(1, lines[1].GetProperty("connection").GetInt32());
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-00")]
    public async Task ADroppedDurableAckTakesTheLinkDownOnceAndTheNextOneGoesThrough()
    {
        // real-onboard-durable-ack-lost: the server committed the result and wrote the ack; the vehicle
        // never sees it and sees the link go down, which is what makes it replay on the next connection.
        await using Upstream upstream = await Upstream.StartAsync();
        await using ProxyFixture proxy = await ProxyFixture.StartAsync(upstream);
        HttpResponseMessage armed = await proxy.CommandAsync(
            HttpMethod.Put, "/control/v1/drop-durable-ack", new { acceptedMessageType = "OperationResult", count = 1 });
        Assert.True(armed.IsSuccessStatusCode, await armed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        await using (Link first = await proxy.ConnectAsync(upstream))
        {
            // Another type's ack is not the plan's business.
            await first.Server.SendAsync(Ack("a-1", "p-1", "OperationProgress"));
            Assert.Equal(Ack("a-1", "p-1", "OperationProgress"), await first.Onboard.ReceiveAsync());

            await first.Server.SendAsync(Ack("a-2", "r-1", "OperationResult"));
            await first.Server.SendAsync("""{"messageType":"SessionReadiness","messageId":"s-1"}""");
            Assert.True(await first.Onboard.ClosedAsync());
            Assert.True(await first.Server.ClosedAsync());
        }

        await using (Link second = await proxy.ConnectAsync(upstream))
        {
            await second.Server.SendAsync(Ack("a-3", "r-1", "OperationResult"));
            Assert.Equal(Ack("a-3", "r-1", "OperationResult"), await second.Onboard.ReceiveAsync());
        }

        JsonElement traffic = await proxy.TrafficAsync();
        JsonElement drop = Assert.Single(traffic.GetProperty("drops").EnumerateArray());
        Assert.Equal("r-1", drop.GetProperty("acceptedMessageId").GetString());
        Assert.Equal(1, drop.GetProperty("connection").GetInt32());
        Assert.DoesNotContain(
            traffic.GetProperty("lines").EnumerateArray(),
            line => line.GetProperty("messageId").GetString() == "s-1");
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task ADroppedAnswerIsLostAloneAndTheLinkStaysUp()
    {
        // real-onboard-cancellation-authorization-lost: the server authorized the cancellation, the answer
        // never reaches the vehicle, and nothing else about the connection changes.
        await using Upstream upstream = await Upstream.StartAsync();
        await using ProxyFixture proxy = await ProxyFixture.StartAsync(upstream);
        await using Link link = await proxy.ConnectAsync(upstream);
        HttpResponseMessage armed = await proxy.CommandAsync(
            HttpMethod.Put, "/control/v1/drop-message", new { messageType = "LoadCancellationAuthorization", count = 1 });
        Assert.True(armed.IsSuccessStatusCode, await armed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        const string firstAnswer = """{"messageType":"LoadCancellationAuthorization","messageId":"z-1","correlationId":"q-1"}""";
        const string heartbeat = """{"messageType":"HeartbeatAck","messageId":"h-1"}""";
        const string secondAnswer = """{"messageType":"LoadCancellationAuthorization","messageId":"z-2","correlationId":"q-2"}""";
        await link.Server.SendAsync(firstAnswer);
        await link.Server.SendAsync(heartbeat);
        await link.Server.SendAsync(secondAnswer);

        Assert.Equal(heartbeat, await link.Onboard.ReceiveAsync());
        Assert.Equal(secondAnswer, await link.Onboard.ReceiveAsync());
        const string onward = """{"messageType":"Heartbeat","messageId":"h-2"}""";
        await link.Onboard.SendAsync(onward);
        Assert.Equal(onward, await link.Server.ReceiveAsync());

        JsonElement traffic = await proxy.TrafficAsync();
        JsonElement drop = Assert.Single(traffic.GetProperty("drops").EnumerateArray());
        Assert.Equal("q-1", drop.GetProperty("acceptedMessageId").GetString());
        Assert.Equal("LoadCancellationAuthorization", drop.GetProperty("acceptedMessageType").GetString());
        JsonElement connection = Assert.Single(traffic.GetProperty("connections").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, connection.GetProperty("closedAt").ValueKind);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-02")]
    public async Task DroppingADurableAckAsAnAnswerIsRefused()
    {
        // Losing an ack takes the link down with it; a plan that lost one and kept the link up would be a
        // fault no real network produces.
        await using Upstream upstream = await Upstream.StartAsync();
        await using ProxyFixture proxy = await ProxyFixture.StartAsync(upstream);

        HttpResponseMessage refused = await proxy.CommandAsync(
            HttpMethod.Put, "/control/v1/drop-message", new { messageType = "DurableAck", count = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    [Trait("IntegrationSlice", "FP-IS-07")]
    public async Task ADisconnectClosesTheOpenLinkOnceAndARetryClosesNothingNew()
    {
        // real-onboard-compensate-then-reconnect: a clean drop of the link with no line chosen to lose, so
        // what follows is each end's handling of the next session and not a replay the relay provoked.
        await using Upstream upstream = await Upstream.StartAsync();
        await using ProxyFixture proxy = await ProxyFixture.StartAsync(upstream);
        string commandId = Guid.NewGuid().ToString("N");

        await using (Link first = await proxy.ConnectAsync(upstream))
        {
            await first.Onboard.SendAsync("""{"messageType":"Heartbeat","messageId":"h-1"}""");
            _ = await first.Server.ReceiveAsync();

            JsonElement answer = await proxy.DisconnectAsync(commandId);
            Assert.Equal([1], answer.GetProperty("body").GetProperty("connections").EnumerateArray().Select(c => c.GetInt32()));
            Assert.True(await first.Onboard.ClosedAsync());
            Assert.True(await first.Server.ClosedAsync());
        }

        await using Link second = await proxy.ConnectAsync(upstream);
        JsonElement retried = await proxy.DisconnectAsync(commandId);
        Assert.Equal([1], retried.GetProperty("body").GetProperty("connections").EnumerateArray().Select(c => c.GetInt32()));
        const string stillUp = """{"messageType":"Heartbeat","messageId":"h-2"}""";
        await second.Onboard.SendAsync(stillUp);
        Assert.Equal(stillUp, await second.Server.ReceiveAsync());

        JsonElement[] connections = [.. (await proxy.TrafficAsync()).GetProperty("connections").EnumerateArray()];
        Assert.Equal("relay disconnected on request", connections[0].GetProperty("closedBy").GetString());
        Assert.Equal(JsonValueKind.Null, connections[1].GetProperty("closedAt").ValueKind);
    }

    [Theory]
    [Trait("IntegrationSlice", "FP-IS-00")]
    [InlineData(null)]
    [InlineData("10.0.0.5:48405")]
    [InlineData("127.0.0.1:0")]
    public void ARelayThatCanSwallowAcksOnlyEverSitsInFrontOfALoopbackServer(string? target)
    {
        // No default, because guessing where to forward is guessing which server; loopback only, because a
        // relay that loses acknowledgements must never sit in front of a server anything else depends on.
        string[] args = target is null
            ? ["--ProtocolFaultProxy:port=0", "--ProtocolFaultProxy:listenPort=0"]
            : ["--ProtocolFaultProxy:port=0", "--ProtocolFaultProxy:listenPort=0", "--ProtocolFaultProxy:target=" + target];

        Assert.Null(ProtocolFaultProxyHost.TryCreate(args));
    }

    private static string Ack(string messageId, string acceptedMessageId, string acceptedMessageType) =>
        JsonSerializer.Serialize(new
        {
            messageType = "DurableAck",
            messageId,
            correlationId = acceptedMessageId,
            payload = new { acceptedMessageId, acceptedMessageType }
        });

    private static (string, string, string) Describe(JsonElement line) =>
        (line.GetProperty("direction").GetString()!,
         line.GetProperty("messageType").GetString()!,
         line.GetProperty("messageId").GetString()!);

    /// <summary>One end of a line-framed TCP connection, LF-terminated like both real ends.</summary>
    private sealed class LineEnd(TcpClient client) : IDisposable
    {
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
        private readonly NetworkStream stream = client.GetStream();
        private readonly StreamReader reader = new(client.GetStream(), new UTF8Encoding(false), false, 4096, leaveOpen: true);

        public async Task SendAsync(string line) =>
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), TestContext.Current.CancellationToken);

        public async Task<string?> ReceiveAsync()
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(ReadTimeout);
            return await reader.ReadLineAsync(timeout.Token);
        }

        /// <summary>True once the other side has closed: a read ends instead of timing out.</summary>
        public async Task<bool> ClosedAsync()
        {
            try
            {
                return await ReceiveAsync() is null;
            }
            catch (IOException)
            {
                return true;
            }
        }

        public void Dispose()
        {
            reader.Dispose();
            client.Dispose();
        }
    }

    private sealed class Link(LineEnd onboard, LineEnd server) : IAsyncDisposable
    {
        public LineEnd Onboard { get; } = onboard;

        public LineEnd Server { get; } = server;

        public ValueTask DisposeAsync()
        {
            Onboard.Dispose();
            Server.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Stands in for ControlServer's onboard protocol listener; hands out each accepted connection.</summary>
    private sealed class Upstream : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Channel<TcpClient> accepted = Channel.CreateUnbounded<TcpClient>();
        private readonly CancellationTokenSource stop = new();
        private Task loop = Task.CompletedTask;

        public IPEndPoint Endpoint => (IPEndPoint)listener.LocalEndpoint;

        public static Task<Upstream> StartAsync()
        {
            Upstream upstream = new();
            upstream.listener.Start();
            upstream.loop = upstream.AcceptLoopAsync();
            return Task.FromResult(upstream);
        }

        public async Task<LineEnd> NextConnectionAsync()
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return new LineEnd(await accepted.Reader.ReadAsync(timeout.Token));
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    await accepted.Writer.WriteAsync(await listener.AcceptTcpClientAsync(stop.Token), stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            listener.Stop();
            await loop;
            stop.Dispose();
        }
    }

    private sealed class ProxyFixture : IAsyncDisposable
    {
        private WebApplication app = null!;

        public HttpClient Client { get; private set; } = null!;

        public int ListenPort { get; private set; }

        public static async Task<ProxyFixture> StartAsync(Upstream upstream)
        {
            ProxyFixture fixture = new();
            // Port 0 for both faces, so parallel test classes never collide; the relay reports the port
            // it actually bound, which is also what an L2 run reads back.
            WebApplication? app = ProtocolFaultProxyHost.TryCreate(
            [
                "--ProtocolFaultProxy:port=0",
                "--ProtocolFaultProxy:listenPort=0",
                "--ProtocolFaultProxy:instanceId=protocol-fault-proxy-test",
                "--ProtocolFaultProxy:target=" + upstream.Endpoint
            ]);
            Assert.NotNull(app);
            fixture.app = app;
            await app.StartAsync(TestContext.Current.CancellationToken);
            fixture.Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            JsonElement health = await fixture.GetAsync("/control/v1/health");
            fixture.ListenPort = IPEndPoint.Parse(health.GetProperty("body").GetProperty("listen").GetString()!).Port;
            Assert.NotEqual(0, fixture.ListenPort);
            return fixture;
        }

        /// <summary>Connects an onboard through the relay and returns both ends of that one connection.</summary>
        public async Task<Link> ConnectAsync(Upstream upstream)
        {
            TcpClient onboard = new();
            await onboard.ConnectAsync(IPAddress.Loopback, ListenPort, TestContext.Current.CancellationToken);
            return new Link(new LineEnd(onboard), await upstream.NextConnectionAsync());
        }

        public async Task<JsonElement> GetAsync(string path)
        {
            using JsonDocument document = JsonDocument.Parse(
                await Client.GetStringAsync(path, TestContext.Current.CancellationToken));
            return document.RootElement.Clone();
        }

        public async Task<JsonElement> TrafficAsync() =>
            (await GetAsync("/control/v1/snapshot")).GetProperty("body").GetProperty("traffic");

        public async Task<HttpResponseMessage> CommandAsync(HttpMethod method, string path, object body)
        {
            JsonElement snapshot = await GetAsync("/control/v1/snapshot");
            Dictionary<string, object?> envelope = new()
            {
                ["runId"] = snapshot.GetProperty("runId").GetString(),
                ["commandId"] = Guid.NewGuid().ToString("N"),
            };
            foreach (var property in JsonSerializer.SerializeToElement(body).EnumerateObject())
            {
                envelope[property.Name] = property.Value;
            }
            using HttpRequestMessage request = new(method, path) { Content = JsonContent.Create(envelope) };
            return await Client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        public async Task<JsonElement> DisconnectAsync(string commandId)
        {
            HttpResponseMessage response = await Client.PostAsJsonAsync(
                "/control/v1/disconnect", new { commandId }, TestContext.Current.CancellationToken);
            string json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, json);
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync(TestContext.Current.CancellationToken);
            await app.DisposeAsync();
        }
    }
}
