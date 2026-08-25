using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Adapters;

namespace ControlServer.Tests;

public sealed class HttpRiotMovementGatewayTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task ReconcileUsesOnlyUpperIdEndpointAndParsesActiveOrder()
    {
        RecordingHandler handler = new((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "/api/order/v1/orderRecord/detailByUpperId/UPPER-001",
                request.RequestUri?.AbsolutePath);
            return JsonResponse("""
                {"code":"0","message":"成功","result":{"id":488004,"orderId":"ORDER-001","upperId":"UPPER-001","orderState":1}}
                """);
        });
        HttpClient client = CreateClient(handler);
        HttpRiotMovementGateway gateway = new(client);

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Active, result.Kind);
        Assert.Equal("ORDER-001", result.OrderId);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task CreateUsesFrozenIntentExactBodyAndBearerCredential()
    {
        RecordingHandler handler = new(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/order/v1/add/byDefaultMissions", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-call-api-key", request.Headers.Authorization?.Parameter);
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            Assert.Equal("AGV-8005-01", root.GetProperty("appointVehicleKey").GetString());
            Assert.Equal(1, root.GetProperty("isAppointEnable").GetInt32());
            Assert.Equal(0, root.GetProperty("lockStatus").GetInt32());
            Assert.Equal("UPPER-001", root.GetProperty("orderName").GetString());
            Assert.Equal("UPPER-001", root.GetProperty("upperId").GetString());
            JsonElement mission = root.GetProperty("mission").EnumerateArray().Single();
            Assert.Equal("move", mission.GetProperty("type").GetString());
            Assert.Equal(29, mission.GetProperty("mapId").GetInt32());
            Assert.Equal(12, mission.GetProperty("destination").GetInt32());
            return JsonResponse("""
                {"code":"0","message":"成功","result":{"id":488004,"orderId":"ORDER-001","upperId":"UPPER-001","orderState":1}}
                """);
        });
        HttpClient client = CreateClient(handler);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-call-api-key");
        HttpRiotMovementGateway gateway = new(client);
        OrderIntent intent = new(
            "LEG-001", "D-001", "UPPER-001", "TO_PICKUP", "ST-12",
            new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero),
            "AGV-8005-01", 29, 12, 4, 7);

        RiotOrderObservation result = await gateway.CreateAsync(intent, TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Active, result.Kind);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task CreateTimeoutRemainsUnknownWithoutRetry()
    {
        RecordingHandler handler = new((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated timeout")));
        HttpRiotMovementGateway gateway = new(CreateClient(handler));
        OrderIntent intent = new(
            "LEG-001", "D-001", "UPPER-001", "TO_PICKUP", "ST-12",
            DateTimeOffset.UtcNow, "AGV-8005-01", 29, 12, 4, 7);

        RiotOrderObservation result = await gateway.CreateAsync(intent, TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task OnlyHttp404IsConfirmedNotFound()
    {
        RecordingHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        HttpRiotMovementGateway gateway = new(CreateClient(handler));

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-404", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.NotFound, result.Kind);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("http://riot.test")
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responseFactory)
            : this((request, cancellationToken) => Task.FromResult(responseFactory(request, cancellationToken)))
        {
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return responseFactory(request, cancellationToken);
        }
    }
}
