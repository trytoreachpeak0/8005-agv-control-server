using System.Net;
using System.Text;
using ControlServer.Application;
using ControlServer.Infrastructure.Adapters;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace ControlServer.Tests;

/// <summary>
/// control-server#401 (batch 9-03): reconciling orders RIoT expanded through an enter_exit point, and reading the raw
/// order facts the unable-to-charge decision (#406) is built on.
/// </summary>
public sealed class HttpRiotChargingOrderGatewayTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AChargingOrderExpandedThroughTheEnterExitPointReconcilesToTheCharger()
    {
        // Maps 25 and 26: station 211 has enter_exit = 212, so move(211) + act(78,1,0) reads back as three missions.
        RiotOrderObservation result = await ReconcileAsync(
            """[{"type":"move","mapId":26,"destination":212},{"type":"move","mapId":26,"destination":211},{"type":"act","mapId":0,"destination":0,"actionId":78,"actionParam1":1,"actionParam2":0}]""",
            endStationNo: 211);

        Assert.Equal(RiotOrderObservationKind.Active, result.Kind);
        Assert.Equal("ORDER-001", result.OrderId);
        Assert.Equal(26, result.MapId);
        Assert.Equal(211, result.DestinationStationId);
    }

    [Fact]
    public async Task AnOrderRiotPrefixedWithLeavingTheChargerReconcilesToItsLastMove()
    {
        // The order after a charge: RIoT puts act(78,2,0) first; with an enter_exit point the moves may be expanded too.
        RiotOrderObservation result = await ReconcileAsync(
            """[{"type":"act","mapId":0,"destination":0,"actionId":78,"actionParam1":2,"actionParam2":0},{"type":"move","mapId":26,"destination":212},{"type":"move","mapId":26,"destination":15}]""",
            endStationNo: 15);

        Assert.Equal(RiotOrderObservationKind.Active, result.Kind);
        Assert.Equal(26, result.MapId);
        Assert.Equal(15, result.DestinationStationId);
    }

    [Fact]
    public async Task ASingleMoveFollowedByTheChargingActReconcilesToTheMove()
    {
        RiotOrderObservation result = await ReconcileAsync(
            """[{"type":"move","mapId":26,"destination":211},{"type":"act","mapId":0,"destination":0,"actionId":78,"actionParam1":1,"actionParam2":0}]""",
            endStationNo: 211);

        Assert.Equal(RiotOrderObservationKind.Active, result.Kind);
        Assert.Equal(211, result.DestinationStationId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task SeveralMovesWithoutAnEndStationRemainUnknown(int? endStationNo)
    {
        RiotOrderObservation result = await ReconcileAsync(
            """[{"type":"move","mapId":26,"destination":212},{"type":"move","mapId":26,"destination":211},{"type":"act","actionId":78,"actionParam1":1,"actionParam2":0}]""",
            endStationNo);

        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Null(result.OrderId);
        Assert.Equal("IDENTITY_INVALID", result.Receipt?.FailureCategory);
    }

    [Theory]
    [InlineData("""[{"type":"move","mapId":26,"destination":212},{"type":"move","mapId":26,"destination":211}]""", 212)]
    [InlineData("""[{"type":"move","mapId":26,"destination":212},{"type":"move","mapId":26,"destination":null}]""", 211)]
    [InlineData("""[{"type":"move","mapId":26,"destination":212},{"type":"move","mapId":0,"destination":211}]""", 211)]
    public async Task SeveralMovesWhoseLastMoveIsNotTheEndStationRemainUnknown(string missionsJson, int endStationNo)
    {
        RiotOrderObservation result = await ReconcileAsync(missionsJson, endStationNo);

        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Null(result.OrderId);
        Assert.Equal("IDENTITY_INVALID", result.Receipt?.FailureCategory);
    }

    [Fact]
    public async Task TheRound24HangReadsBackItsRawOrderStateAndActResult()
    {
        // Trimmed from program repo rcs/riot-behavior-lab/evidence/rounds/2026-07-21-round-24/runs/S1b-hang-detail.json.
        const string body =
            """
            {"code":"0","message":"成功","result":{"id":488650,"orderId":"order-2079374239101747200","upperId":"UPPER-CHARGE","orderState":9,"appointVehicleKey":"BROKERX-aee2f93d717546cf9510c98c854fe83e","executeVehicleKey":"BROKERX-aee2f93d717546cf9510c98c854fe83e","endStationNo":6,"missions":[{"actionId":0,"actionParam1":0,"actionParam2":0,"destination":8,"mapId":30,"resultCode":900,"resultStr":"订单完成","type":"move"},{"actionId":0,"actionParam1":0,"actionParam2":0,"destination":6,"mapId":30,"resultCode":900,"resultStr":"订单完成","type":"move"},{"actionId":78,"actionParam1":1,"actionParam2":0,"destination":0,"mapId":0,"resultCode":407802,"resultStr":"未知类型错误,导致订单挂起:错误编码为:407802","type":"act"}]}}
            """;
        (RiotOrderMissionFacts facts, RecordingHandler handler) = await ReadFactsAsync("UPPER-CHARGE", body);

        Assert.Equal(RiotOrderMissionFactsStatus.Found, facts.Status);
        Assert.Equal("UPPER-CHARGE", facts.UpperId);
        Assert.Equal("order-2079374239101747200", facts.OrderId);
        Assert.Equal(9, facts.OrderState);
        Assert.Equal(ReadAt, facts.ObservedAt);
        Assert.Equal(
            [
                new RiotOrderMissionFact("move", 30, 8, 0, 0, 0, 900),
                new RiotOrderMissionFact("move", 30, 6, 0, 0, 0, 900),
                new RiotOrderMissionFact("act", 0, 0, 78, 1, 0, 407802),
            ],
            facts.Missions);
        Assert.Equal("/api/order/v1/orderRecord/detailByUpperId/UPPER-CHARGE", handler.LastPath);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task AnActWithoutAResultCodeReadsBackNullNotZero()
    {
        const string body =
            """{"code":"0","result":{"id":7,"orderId":"ORDER-7","upperId":"UPPER-7","orderState":3,"endStationNo":211,"missions":[{"type":"move","mapId":26,"destination":211},{"type":"act","actionId":78,"actionParam1":1}]}}""";
        (RiotOrderMissionFacts facts, _) = await ReadFactsAsync("UPPER-7", body);

        Assert.Equal(RiotOrderMissionFactsStatus.Found, facts.Status);
        Assert.Equal(3, facts.OrderState);
        Assert.Equal(new RiotOrderMissionFact("move", 26, 211, null, null, null, null), facts.Missions[0]);
        Assert.Equal(new RiotOrderMissionFact("act", null, null, 78, 1, null, null), facts.Missions[1]);
    }

    [Fact]
    public async Task A404ReadsAsNotFound()
    {
        RecordingHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        (RiotOrderMissionFacts facts, _) = await ReadFactsAsync("UPPER-404", handler);

        Assert.Equal(RiotOrderMissionFactsStatus.NotFound, facts.Status);
        Assert.Null(facts.OrderState);
        Assert.Empty(facts.Missions);
    }

    [Theory]
    [InlineData("""{"code":"0","result":null}""")]
    [InlineData("""{"code":"0","result":{"id":7,"orderId":"ORDER-7","upperId":"OTHER-UPPER","orderState":9,"missions":[]}}""")]
    [InlineData("""{"code":"0","result":{"upperId":"UPPER-X","orderState":9}}""")]
    [InlineData("""{"code":"E500","message":"failed","result":null}""")]
    public async Task AnAnswerThatIsNotThisOrderReadsAsUnknown(string body)
    {
        (RiotOrderMissionFacts facts, RecordingHandler handler) = await ReadFactsAsync("UPPER-X", body);

        Assert.Equal(RiotOrderMissionFactsStatus.Unknown, facts.Status);
        Assert.Null(facts.OrderId);
        Assert.Null(facts.OrderState);
        Assert.Empty(facts.Missions);
        Assert.Equal(ReadAt, facts.ObservedAt);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ATransportFailureReadsAsUnknownWithoutRetry()
    {
        RecordingHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        (RiotOrderMissionFacts facts, _) = await ReadFactsAsync("UPPER-503", handler);

        Assert.Equal(RiotOrderMissionFactsStatus.Unknown, facts.Status);
        Assert.Equal(1, handler.CallCount);
    }

    private static async Task<RiotOrderObservation> ReconcileAsync(string missionsJson, int? endStationNo)
    {
        string end = endStationNo?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        string body = $$$"""
            {"code":"0","result":{"id":488004,"orderId":"ORDER-001","upperId":"UPPER-001","orderState":3,
            "appointVehicleKey":"VEHICLE-KEY-01","executeVehicleKey":"VEHICLE-KEY-01",
            "endStationNo":{{{end}}},"missions":{{{missionsJson}}}}}
            """;
        RecordingHandler handler = new((_, _) => JsonResponse(body));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);
        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.CallCount);
        return result;
    }

    private static Task<(RiotOrderMissionFacts, RecordingHandler)> ReadFactsAsync(string upperId, string body) =>
        ReadFactsAsync(upperId, new RecordingHandler((_, _) => JsonResponse(body)));

    private static async Task<(RiotOrderMissionFacts, RecordingHandler)> ReadFactsAsync(
        string upperId,
        RecordingHandler handler)
    {
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session, new FixedTimeProvider(ReadAt));
        RiotOrderMissionFacts facts = await gateway.ReadOrderMissionFactsAsync(
            upperId, TestContext.Current.CancellationToken);
        return (facts, handler);
    }

    private static RiotSession CreateSession(HttpMessageHandler handler) => new(
        new RiotOptions
        {
            BaseUrl = "http://riot.test",
            CallApiKey = "test-call-api-key"
        },
        handler);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(responseFactory(request, cancellationToken));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
