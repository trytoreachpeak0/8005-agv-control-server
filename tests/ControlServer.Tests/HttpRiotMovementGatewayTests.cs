using System.Net;
using System.Text;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Adapters;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace ControlServer.Tests;

public sealed class HttpRiotMovementGatewayTests
{
    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task ReconcileUsesSdkUpperIdEndpointAndParsesCompleteActiveOrder()
    {
        RecordingHandler handler = new((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(
                "/api/order/v1/orderRecord/detailByUpperId/UPPER-001",
                request.RequestUri?.AbsolutePath);
            return JsonResponse(FoundOrderJson(orderState: 1));
        });
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Active, result.Kind);
        Assert.Equal("ORDER-001", result.OrderId);
        Assert.Equal(1, result.OrderState);
        Assert.Equal("VEHICLE-KEY-01", result.VehicleKey);
        Assert.Equal(29, result.MapId);
        Assert.Equal(12, result.DestinationStationId);
        Assert.Equal("RECONCILE", result.Receipt?.Operation);
        Assert.Equal("Found", result.Receipt?.Classification);
        Assert.True(result.Receipt?.ResultPresent);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("--")]
    [InlineData(" -- ")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task QueueingOrderWithoutABoundVehicleIsIdentifiedByItsAppointedKey(string? executeVehicle)
    {
        // BC-ORDER-012 / BC-ORDER-013: a QUEUEING order reports "--" in executeVehicleKey until
        // RIoT binds a vehicle. Reading that placeholder as a real key made the observation fail
        // its frozen-intent match right after a successful create.
        RecordingHandler handler = new((_, _) => JsonResponse(FoundOrderJson(
            orderState: 1,
            appointedVehicle: "VEHICLE-KEY-01",
            executeVehicle: executeVehicle)));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Active, result.Kind);
        Assert.Equal("VEHICLE-KEY-01", result.VehicleKey);
        Assert.Equal("ORDER-001", result.OrderId);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task OnceRiotBindsAVehicleTheExecutingKeyWinsOverTheAppointedOne()
    {
        RecordingHandler handler = new((_, _) => JsonResponse(FoundOrderJson(
            orderState: 3,
            appointedVehicle: "APPOINTED-VEHICLE-KEY",
            executeVehicle: "EXECUTING-VEHICLE-KEY")));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal("EXECUTING-VEHICLE-KEY", result.VehicleKey);
    }

    [Theory]
    [InlineData("{\"code\":\"0\",\"message\":\"成功\"}")]
    [InlineData("{\"code\":\"0\",\"message\":\"成功\",\"result\":null}")]
    [InlineData("{\"code\":\"0\",\"message\":\"成功\",\"result\":[]}")]
    [InlineData("{\"code\":\"0\",\"message\":\"成功\",\"result\":{\"upperId\":\"UPPER-001\"}}")]
    [InlineData("{\"code\":\"0\",\"result\":{\"id\":1,\"orderId\":\"ORDER-001\",\"upperId\":\"OTHER\",\"orderState\":1,\"appointVehicleKey\":\"VEHICLE-KEY-01\",\"missions\":[{\"type\":\"move\",\"mapId\":29,\"destination\":12}]}}")]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task ReconcileAbsentOrIndeterminateSdkObservationRemainsUnknownPendingContractConfirmation(
        string body)
    {
        RecordingHandler handler = new((_, _) => JsonResponse(body));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal("UPPER-001", result.UpperId);
        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Null(result.OrderId);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("{\"code\":\"0\",\"message\":\"成功\"}")]
    [InlineData("{\"code\":\"0\",\"message\":\"成功\",\"result\":null}")]
    public async Task ReconcileSdkAbsentProducesExactExperimentalEligibilityReceipt(string body)
    {
        RecordingHandler handler = new((_, _) => JsonResponse(body));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal("UPPER-001", result.UpperId);
        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Null(result.OrderId);
        Assert.NotNull(result.Receipt);
        Assert.Equal("RECONCILE", result.Receipt.Operation);
        Assert.Equal("AbsentAtObservation", result.Receipt.Classification);
        Assert.False(result.Receipt.ResultPresent);
        Assert.Null(result.Receipt.HttpStatusCode);
        Assert.Null(result.Receipt.BusinessCode);
        Assert.Null(result.Receipt.FailureCategory);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("{\"code\":\"0\",\"message\":\"成功\",\"result\":[]}")]
    [InlineData("{\"code\":\"0\",\"message\":\"成功\",\"result\":{\"upperId\":\"UPPER-001\"}}")]
    [InlineData("{\"code\":\"0\",\"result\":{\"id\":1,\"orderId\":\"ORDER-001\",\"upperId\":\"OTHER\",\"orderState\":1,\"appointVehicleKey\":\"VEHICLE-KEY-01\",\"missions\":[{\"type\":\"move\",\"mapId\":29,\"destination\":12}]}}")]
    public async Task ReconcileSdkIndeterminateCannotMasqueradeAsExperimentalAbsence(string body)
    {
        RecordingHandler handler = new((_, _) => JsonResponse(body));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Null(result.OrderId);
        Assert.NotNull(result.Receipt);
        Assert.Equal("RECONCILE", result.Receipt.Operation);
        Assert.Equal("Indeterminate", result.Receipt.Classification);
        Assert.True(result.Receipt.ResultPresent);
        Assert.NotEqual("AbsentAtObservation", result.Receipt.Classification);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task ReconcileHttp404IsConfirmedNotFound()
    {
        RecordingHandler handler = new((request, _) =>
        {
            Assert.Equal("/api/order/v1/orderRecord/detailByUpperId/UPPER-404", request.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-404", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.NotFound, result.Kind);
        Assert.Null(result.OrderId);
        Assert.Equal("NotFound", result.Receipt?.Classification);
        Assert.Equal(404, result.Receipt?.HttpStatusCode);
        Assert.False(result.Receipt?.ResultPresent);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ReconcileHttpFailureReturnsSanitizedReceiptWithoutRetry()
    {
        RecordingHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"secret\":\"TOP-SECRET-MARKER\"}", Encoding.UTF8, "application/json")
        });
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Equal("SdkFailure", result.Receipt?.Classification);
        Assert.Equal(500, result.Receipt?.HttpStatusCode);
        Assert.Null(result.Receipt?.BusinessCode);
        Assert.Equal("RIOT_API_FAILURE", result.Receipt?.FailureCategory);
        Assert.DoesNotContain("TOP-SECRET-MARKER", result.Receipt?.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("{\"code\":\"TOP-SECRET-MARKER\",\"message\":\"failed\",\"result\":null}")]
    [InlineData("{\"code\":{\"secret\":\"TOP-SECRET-MARKER\"},\"message\":\"failed\",\"result\":null}")]
    public async Task ReconcileDropsUntrustedBusinessCodeFromSanitizedReceipt(string body)
    {
        RecordingHandler handler = new((_, _) => JsonResponse(body));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Equal("SdkFailure", result.Receipt?.Classification);
        Assert.Null(result.Receipt?.BusinessCode);
        Assert.DoesNotContain("TOP-SECRET-MARKER", result.Receipt?.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FoundOrderUsesEndStationFallbackForSingleMoveWithoutDestination()
    {
        RecordingHandler handler = new((_, _) => JsonResponse(FoundOrderJson(
            orderState: 5,
            missionsJson: "[{\"type\":\"move\",\"mapId\":29,\"destination\":null}]",
            endStationNo: 12)));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Terminal, result.Kind);
        Assert.Equal(5, result.OrderState);
        Assert.Equal(12, result.DestinationStationId);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("[]", 12, "VEHICLE-KEY-01", null, 1)]
    [InlineData("[{\"type\":\"act\",\"mapId\":29,\"destination\":12}]", 12, "VEHICLE-KEY-01", null, 1)]
    [InlineData("[{\"type\":\"move\",\"mapId\":29,\"destination\":12},{\"type\":\"move\",\"mapId\":29,\"destination\":13}]", 12, "VEHICLE-KEY-01", null, 1)]
    [InlineData("[{\"type\":\"move\",\"mapId\":0,\"destination\":12}]", 12, "VEHICLE-KEY-01", null, 1)]
    [InlineData("[{\"type\":\"move\",\"mapId\":29,\"destination\":null}]", null, "VEHICLE-KEY-01", null, 1)]
    [InlineData("[{\"type\":\"move\",\"mapId\":29,\"destination\":12}]", 12, null, null, 1)]
    [InlineData("[{\"type\":\"move\",\"mapId\":29,\"destination\":12}]", 12, "VEHICLE-KEY-01", null, 99)]
    public async Task FoundOrderWithIncompleteOrAmbiguousEvidenceRemainsUnknown(
        string missionsJson,
        int? endStationNo,
        string? appointedVehicle,
        string? executeVehicle,
        int orderState)
    {
        RecordingHandler handler = new((_, _) => JsonResponse(FoundOrderJson(
            orderState,
            missionsJson,
            endStationNo,
            appointedVehicle,
            executeVehicle)));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Null(result.OrderId);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task CreateUsesSdkFrozenIntentBodyAndReturnsFrozenEvidence()
    {
        RecordingHandler handler = new(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/order/v1/add/byDefaultMissions", request.RequestUri?.AbsolutePath);
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
            return JsonResponse(CreateSuccessJson("UPPER-001"));
        });
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.CreateAsync(
            CreateIntent(), TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Active, result.Kind);
        Assert.Equal("ORDER-001", result.OrderId);
        Assert.Equal("AGV-8005-01", result.VehicleKey);
        Assert.Equal(29, result.MapId);
        Assert.Equal(12, result.DestinationStationId);
        Assert.Equal("CREATE", result.Receipt?.Operation);
        Assert.Equal("SdkAccepted", result.Receipt?.Classification);
        Assert.True(result.Receipt?.ResultPresent);
        Assert.Null(result.Receipt?.HttpStatusCode);
        Assert.Null(result.Receipt?.BusinessCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task ChargeOrderCarriesTheStartChargingActionAfterTheMoveToThePad()
    {
        // 8005-agv-program#53: a charge order that is only a movement parks the vehicle on the pad at
        // NO_CHARGE. RIoT engages the charger for act(78, 1, 0) and for nothing else (Q-033).
        RecordingHandler handler = new(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/order/v1/add/byDefaultMissions", request.RequestUri?.AbsolutePath);
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            Assert.Equal("AGV-8005-01", root.GetProperty("appointVehicleKey").GetString());
            Assert.Equal(1, root.GetProperty("isAppointEnable").GetInt32());
            Assert.Equal(0, root.GetProperty("lockStatus").GetInt32());
            Assert.Equal("UPPER-CHARGE-001", root.GetProperty("upperId").GetString());
            JsonElement[] missions = root.GetProperty("mission").EnumerateArray().ToArray();
            Assert.Equal(2, missions.Length);
            Assert.Equal("move", missions[0].GetProperty("type").GetString());
            Assert.Equal(25, missions[0].GetProperty("mapId").GetInt32());
            Assert.Equal(211, missions[0].GetProperty("destination").GetInt32());
            Assert.Equal("act", missions[1].GetProperty("type").GetString());
            Assert.Equal(78, missions[1].GetProperty("actionId").GetInt32());
            Assert.Equal(1, missions[1].GetProperty("actionParam1").GetInt32());
            Assert.Equal(0, missions[1].GetProperty("actionParam2").GetInt32());
            return JsonResponse(CreateSuccessJson("UPPER-CHARGE-001"));
        });
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.CreateAsync(
            CreateIntent() with
            {
                UpperId = "UPPER-CHARGE-001",
                Purpose = "TO_CHARGER",
                MapId = 25,
                DestinationStationId = 211
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Active, result.Kind);
        Assert.Equal("ORDER-001", result.OrderId);
        Assert.Equal(25, result.MapId);
        Assert.Equal(211, result.DestinationStationId);
        Assert.Equal("SdkAccepted", result.Receipt?.Classification);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ChargeOrderWithIncompleteIdentifiersRemainsUnknownWithoutRetry()
    {
        RecordingHandler handler = new((_, _) => JsonResponse(
            """{"code":"0","message":"成功","result":{"upperId":"UPPER-CHARGE-001"}}"""));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.CreateAsync(
            CreateIntent() with { UpperId = "UPPER-CHARGE-001", Purpose = "TO_CHARGER" },
            TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Null(result.OrderId);
        Assert.Equal("PROTOCOL_FAILURE", result.Receipt?.FailureCategory);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task AnOrderRiotExpandedThroughAnEnterExitPointEndsAtItsLastMove()
    {
        // Map 25 names 212 as the enter_exit point of charger 211, so RIoT runs a charge order as
        // move(212) -> move(211) -> act(78). Requiring exactly one move left every charge order
        // unconfirmable.
        RecordingHandler handler = new((_, _) => JsonResponse(FoundOrderJson(
            orderState: 5,
            missionsJson: """
                [{"type":"move","mapId":25,"destination":212},{"type":"move","mapId":25,"destination":211},
                {"type":"act","mapId":0,"destination":0,"actionId":78,"actionParam1":1,"actionParam2":0}]
                """,
            endStationNo: 211)));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Terminal, result.Kind);
        Assert.Equal(25, result.MapId);
        Assert.Equal(211, result.DestinationStationId);
    }

    [Fact]
    public async Task MovesOnTwoDifferentMapsInOneOrderRemainUnknown()
    {
        RecordingHandler handler = new((_, _) => JsonResponse(FoundOrderJson(
            orderState: 5,
            missionsJson: """[{"type":"move","mapId":24,"destination":212},{"type":"move","mapId":25,"destination":211}]""",
            endStationNo: 211)));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Equal("IDENTITY_INVALID", result.Receipt?.FailureCategory);
    }

    [Fact]
    public async Task CreateNullResultReturnsSanitizedUnknownReceiptWithoutRetry()
    {
        const string response = "{\"code\":\"0\",\"message\":\"TOP-SECRET-MARKER\",\"result\":null}";
        RecordingHandler handler = new((_, _) => JsonResponse(response));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.CreateAsync(
            CreateIntent(), TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Equal("SdkFailure", result.Receipt?.Classification);
        Assert.Equal("order-ref-missing", result.Receipt?.BusinessCode);
        Assert.False(result.Receipt?.ResultPresent);
        Assert.Equal("PROTOCOL_FAILURE", result.Receipt?.FailureCategory);
        Assert.DoesNotContain("TOP-SECRET-MARKER", result.Receipt?.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("{\"code\":\"0\",\"result\":null}")]
    [InlineData("{\"code\":\"0\",\"result\":{\"id\":1,\"upperId\":\"UPPER-001\",\"orderState\":1}}")]
    [InlineData("{\"code\":\"0\",\"result\":{\"id\":1,\"orderId\":\"ORDER-001\",\"upperId\":\"OTHER\",\"orderState\":1}}")]
    [InlineData("{\"code\":\"0\",\"result\":{\"id\":1,\"orderId\":\"ORDER-001\",\"upperId\":\"UPPER-001\",\"orderState\":99}}")]
    public async Task CreateEmptyIncompleteOrMismatchedSdkResultRemainsUnknownWithoutRetry(string body)
    {
        RecordingHandler handler = new((_, _) => JsonResponse(body));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.CreateAsync(
            CreateIntent(), TestContext.Current.CancellationToken);

        Assert.Equal("UPPER-001", result.UpperId);
        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Null(result.OrderId);
        Assert.NotNull(result.Receipt);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task CreateTimeoutRemainsUnknownWithoutRetry()
    {
        RecordingHandler handler = new((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated timeout")));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.CreateAsync(
            CreateIntent(), TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Null(result.OrderId);
        Assert.Equal("SdkFailure", result.Receipt?.Classification);
        Assert.Equal("TIMEOUT", result.Receipt?.FailureCategory);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task CreateSdkHttpFailureRemainsUnknownWithoutRetry()
    {
        RecordingHandler handler = new((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"secret\":\"TOP-SECRET-MARKER\"}", Encoding.UTF8, "application/json")
        });
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderObservation result = await gateway.CreateAsync(
            CreateIntent(), TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Unknown, result.Kind);
        Assert.Null(result.OrderId);
        Assert.Equal("SdkFailure", result.Receipt?.Classification);
        Assert.Equal(503, result.Receipt?.HttpStatusCode);
        Assert.Equal("HTTP_API_FAILURE", result.Receipt?.FailureCategory);
        Assert.DoesNotContain("TOP-SECRET-MARKER", result.Receipt?.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task CreatePropagatesCallerCancellationWithoutRetry()
    {
        using CancellationTokenSource source = new();
        RecordingHandler handler = new((_, cancellationToken) =>
        {
            source.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        });
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gateway.CreateAsync(CreateIntent(), source.Token));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task VehicleFactsAreMappedAndObservedAfterSdkReadCompletes()
    {
        DateTimeOffset before = new(2026, 8, 28, 1, 0, 0, TimeSpan.Zero);
        DateTimeOffset after = before.AddSeconds(1);
        MutableTimeProvider clock = new(before);
        RecordingHandler handler = new((request, _) =>
        {
            Assert.Equal("/api/task/vehicles/getVehicleInfoByDeviceKey", request.RequestUri?.AbsolutePath);
            Assert.Equal("?key=VEHICLE-KEY-01", request.RequestUri?.Query);
            clock.Set(after);
            return JsonResponse(VehicleCardJson("VEHICLE-KEY-01"));
        });
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session, clock);

        RiotVehicleObservation result = await gateway.ReadVehicleAsync(
            "VEHICLE-KEY-01", TestContext.Current.CancellationToken);

        Assert.True(result.Connected);
        Assert.True(result.Enabled);
        Assert.Equal("IDLE", result.ProcState);
        Assert.Equal("MAP-29", result.CurrentMap);
        Assert.Equal(12, result.CurrentStationId);
        Assert.Equal(80, result.BatteryPercent);
        Assert.Equal("NO_CHARGE", result.BatteryState);
        Assert.Equal(0d, result.Speed);
        Assert.Equal(0, result.LockStatus);
        Assert.Null(result.OrderTaskId);
        Assert.Equal(after, result.ObservedAt);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task VehicleIdentityMismatchReturnsFailClosedUnknownFacts()
    {
        RecordingHandler handler = new((_, _) => JsonResponse(VehicleCardJson("OTHER")));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotVehicleObservation result = await gateway.ReadVehicleAsync(
            "VEHICLE-KEY-01", TestContext.Current.CancellationToken);

        Assert.False(result.Connected);
        Assert.False(result.Enabled);
        Assert.Equal("UNKNOWN", result.ProcState);
        Assert.Null(result.CurrentStationId);
        Assert.Null(result.Speed);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task StrictMapCatalogIsCanonicalAndObservedAfterSdkReadCompletes()
    {
        DateTimeOffset before = new(2026, 8, 28, 2, 0, 0, TimeSpan.Zero);
        DateTimeOffset after = before.AddSeconds(1);
        MutableTimeProvider clock = new(before);
        RecordingHandler handler = new((request, _) =>
        {
            Assert.Equal("/api/imap/v1/mapInfo/stations/25", request.RequestUri?.AbsolutePath);
            clock.Set(after);
            return JsonResponse("""
                {"code":"0","result":[
                  {"id":210,"name":"关卡"},{"id":12,"name":"N1-3_N1-7"},{"id":11,"name":"C15-13"}]}
                """);
        });
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session, clock);

        RiotMapStationCatalogSnapshot result = await gateway.ReadMapStationsAsync(
            25, TestContext.Current.CancellationToken);

        Assert.Equal([11, 12, 210], result.Stations.Select(station => station.StationId));
        Assert.Equal(["C15-13", "N1-3_N1-7", "关卡"], result.Stations.Select(station => station.StationName));
        Assert.Equal("8e1df56b366705969098327145588a67612a05b172c8958aa1f0aec64f3e6d30", result.ContentSha256);
        Assert.Equal(after, result.ObservedAt);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("{\"code\":\"0\",\"result\":null}")]
    [InlineData("{\"code\":\"0\",\"result\":[]}")]
    [InlineData("{\"code\":\"0\",\"result\":[{\"id\":0,\"name\":\"BAD\"}]}")]
    [InlineData("{\"code\":\"0\",\"result\":[{\"id\":12,\"name\":\"\"}]}")]
    [InlineData("{\"code\":\"0\",\"result\":[{\"id\":12,\"name\":\"N1-3\"},{\"id\":12,\"name\":\"N1-7\"}]}")]
    public async Task StrictMapCatalogRejectsInvalidCatalogWithoutPublishingPartialResult(string body)
    {
        RecordingHandler handler = new((_, _) => JsonResponse(body));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            gateway.ReadMapStationsAsync(25, TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task VehicleSafetyRequiresCompleteCompositeAndUsesPostSecondReadTime()
    {
        DateTimeOffset before = new(2026, 8, 28, 3, 0, 0, TimeSpan.Zero);
        DateTimeOffset afterVehicle = before.AddSeconds(1);
        DateTimeOffset afterOrders = before.AddSeconds(2);
        MutableTimeProvider clock = new(before);
        RecordingHandler handler = SafetyHandler(
            clock,
            afterVehicle,
            afterOrders,
            SafeVehicleJson("MT_FINISHED", 0),
            CompleteOrdersJson());
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session, clock);

        RiotVehicleSafetyObservation result = await gateway.ReadVehicleSafetyAsync(
            "VEHICLE-KEY-01", TestContext.Current.CancellationToken);

        Assert.Equal(RiotVehicleMotionState.Stopped, result.MotionState);
        Assert.Empty(result.ReasonCodes);
        Assert.Equal("RIOT_BEHAVIOR_LAB_R41", result.Source);
        Assert.Equal(afterOrders, result.ObservedAt);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task VehicleSafetyPreservesEveryFailClosedCompositeReason()
    {
        const string unsafeVehicle = """
            {
              "vehicle":{"movementState":"MT_NA","controlState":"CONTROL_STATE_ERR",
                "emergencyState":"CAN_RECOVER","breakSwitchState":"UNMOVABLE",
                "locationState":"ERROR","speed":null},
              "vehicleTaskInfo":{"key":"VEHICLE-KEY-01","procState":"PROCESSING_ORDER",
                "processingOrder":true,"enable":false,"integrationLevel":"OFF_LINE"}
            }
            """;
        const string matchingOrder = """
            {"code":"0","result":{"current":1,"size":100,"total":1,"records":[
              {"id":1,"orderId":"ORDER-1","upperId":"UPPER-1","orderState":1,
               "appointVehicleKey":"VEHICLE-KEY-01","executeVehicleKey":null}]}}
            """;
        RecordingHandler handler = SafetyHandler(null, null, null, unsafeVehicle, matchingOrder);
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotVehicleSafetyObservation result = await gateway.ReadVehicleSafetyAsync(
            "VEHICLE-KEY-01", TestContext.Current.CancellationToken);

        Assert.Equal(RiotVehicleMotionState.Unknown, result.MotionState);
        Assert.Equal(
        [
            "RIOT_PROC_NOT_IDLE",
            "RIOT_PROCESSING_ORDER_UNKNOWN_OR_ACTIVE",
            "RIOT_VEHICLE_NOT_ENABLED",
            "RIOT_VEHICLE_NOT_ONLINE",
            "RIOT_EMERGENCY_NOT_OK",
            "RIOT_BRAKE_NOT_MOVABLE",
            "RIOT_CONTROL_NOT_OK",
            "RIOT_LOCATION_NOT_RUNNING",
            "RIOT_SPEED_NOT_ZERO",
            "RIOT_MOVEMENT_NOT_FINISHED",
            "RIOT_NONFINAL_ORDER_PRESENT"
        ], result.ReasonCodes);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task VehicleSafetyReturnsReadTimeoutWhenSdkReadTimesOut()
    {
        RecordingHandler handler = new((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("simulated timeout")));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotVehicleSafetyObservation result = await gateway.ReadVehicleSafetyAsync(
            "VEHICLE-KEY-01", TestContext.Current.CancellationToken);

        Assert.Equal(RiotVehicleMotionState.Unknown, result.MotionState);
        Assert.Equal(["RIOT_READ_TIMEOUT"], result.ReasonCodes);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task VehicleSafetyReturnsReadFailedWhenSdkOrderReadFails()
    {
        RecordingHandler handler = new((request, _) =>
            request.RequestUri?.AbsolutePath == "/api/task/v1/task/getVehicleInfo/VEHICLE-KEY-01"
                ? JsonResponse(SafeVehicleJson("MT_FINISHED", 0))
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotVehicleSafetyObservation result = await gateway.ReadVehicleSafetyAsync(
            "VEHICLE-KEY-01", TestContext.Current.CancellationToken);

        Assert.Equal(RiotVehicleMotionState.Unknown, result.MotionState);
        Assert.Equal(["RIOT_READ_FAILED"], result.ReasonCodes);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task VehicleSafetyReturnsUnknownForObservedMtNaEvenWhenEveryOtherFactIsSafe()
    {
        RecordingHandler handler = SafetyHandler(
            clock: null,
            afterVehicle: null,
            afterOrders: null,
            SafeVehicleJson("MT_NA", 0),
            CompleteOrdersJson());
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotVehicleSafetyObservation result = await gateway.ReadVehicleSafetyAsync(
            "VEHICLE-KEY-01", TestContext.Current.CancellationToken);

        Assert.Equal(RiotVehicleMotionState.Unknown, result.MotionState);
        Assert.Equal(["RIOT_MOVEMENT_NOT_FINISHED"], result.ReasonCodes);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task VehicleSafetyReturnsMovingForRunningMotionEvenAtZeroReportedSpeed()
    {
        RecordingHandler handler = SafetyHandler(
            clock: null,
            afterVehicle: null,
            afterOrders: null,
            SafeVehicleJson("MT_RUNNING", 0),
            CompleteOrdersJson());
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotVehicleSafetyObservation result = await gateway.ReadVehicleSafetyAsync(
            "VEHICLE-KEY-01", TestContext.Current.CancellationToken);

        Assert.Equal(RiotVehicleMotionState.Moving, result.MotionState);
        Assert.Equal(["RIOT_MOTION_ACTIVE"], result.ReasonCodes);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task VehicleSafetyReturnsUnknownWhenSdkPageDoesNotCoverAllRecords()
    {
        RecordingHandler handler = SafetyHandler(
            clock: null,
            afterVehicle: null,
            afterOrders: null,
            SafeVehicleJson("MT_FINISHED", 0),
            """{"code":"0","result":{"current":1,"size":100,"total":101,"records":[]}}""");
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotVehicleSafetyObservation result = await gateway.ReadVehicleSafetyAsync(
            "VEHICLE-KEY-01", TestContext.Current.CancellationToken);

        Assert.Equal(RiotVehicleMotionState.Unknown, result.MotionState);
        Assert.Equal(["RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN"], result.ReasonCodes);
        Assert.Equal(2, handler.CallCount);
    }

    private static RecordingHandler SafetyHandler(
        MutableTimeProvider? clock,
        DateTimeOffset? afterVehicle,
        DateTimeOffset? afterOrders,
        string vehicleBody,
        string ordersBody) => new((request, _) =>
    {
        if (request.RequestUri?.AbsolutePath == "/api/task/v1/task/getVehicleInfo/VEHICLE-KEY-01")
        {
            if (afterVehicle.HasValue) clock!.Set(afterVehicle.Value);
            return JsonResponse(vehicleBody);
        }

        Assert.Equal("/api/order/v1/orderRecord", request.RequestUri?.AbsolutePath);
        string[] pairs = request.RequestUri!.Query.TrimStart('?').Split('&');
        Assert.Contains("filterByState=1", pairs);
        Assert.Contains("filterByState=3", pairs);
        Assert.Contains("filterByState=7", pairs);
        Assert.Contains("filterByState=9", pairs);
        if (afterOrders.HasValue) clock!.Set(afterOrders.Value);
        return JsonResponse(ordersBody);
    });

    private static string FoundOrderJson(
        int orderState,
        string missionsJson = "[{\"type\":\"move\",\"mapId\":29,\"destination\":12}]",
        int? endStationNo = 12,
        string? appointedVehicle = "APPOINTED-VEHICLE-KEY",
        string? executeVehicle = "VEHICLE-KEY-01")
    {
        string end = endStationNo.HasValue ? endStationNo.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
        string appointed = appointedVehicle is null ? "null" : $"\"{appointedVehicle}\"";
        string execute = executeVehicle is null ? "null" : $"\"{executeVehicle}\"";
        return $$$"""
            {"code":"0","result":{"id":488004,"orderId":"ORDER-001","upperId":"UPPER-001",
            "orderState":{{{orderState}}},"appointVehicleKey":{{{appointed}}},"executeVehicleKey":{{{execute}}},
            "endStationNo":{{{end}}},"missions":{{{missionsJson}}}}}
            """;
    }

    private static string CreateSuccessJson(string upperId) => $$$"""
        {"code":"0","result":{"id":488004,"orderId":"ORDER-001","upperId":"{{{upperId}}}","orderState":1}}
        """;

    private static string VehicleCardJson(string deviceKey) => $$$"""
        {"code":"0","result":{"deviceKey":"{{{deviceKey}}}","enable":true,"status":1,
        "procState":"IDLE","currentMap":"MAP-29","currentPosition":12,"battery":80,
        "batteryState":"NO_CHARGE","speed":0,"lockStatus":0,"orderTaskId":null}}
        """;

    private static string SafeVehicleJson(string movementState, double speed) => $$$"""
        {
          "vehicle":{"movementState":"{{{movementState}}}","controlState":"CONTROL_STATE_OK",
            "emergencyState":"OK","breakSwitchState":"MOVABLE",
            "locationState":"LOCATION_STATE_RUNNING","speed":{{{speed}}}},
          "vehicleTaskInfo":{"key":"VEHICLE-KEY-01","procState":"IDLE",
            "processingOrder":false,"enable":true,"integrationLevel":"ON_LINE"}
        }
        """;

    private static string CompleteOrdersJson() =>
        """{"code":"0","result":{"current":1,"size":100,"total":0,"records":[]}}""";

    private static OrderIntent CreateIntent() => new(
        "LEG-001", "D-001", "UPPER-001", "TO_PICKUP", "ST-12",
        new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero),
        "AGV-8005-01", 29, 12, 4, 7);

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

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public void Set(DateTimeOffset value) => current = value;

        public override DateTimeOffset GetUtcNow() => current;
    }
}
