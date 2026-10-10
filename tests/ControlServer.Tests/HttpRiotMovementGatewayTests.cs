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
    [Trait("IntegrationSlice", "FP-IS-03")]
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
    [Trait("IntegrationSlice", "FP-IS-03")]
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
    [Trait("IntegrationSlice", "FP-IS-03")]
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
    [Trait("IntegrationSlice", "FP-IS-03")]
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
    [Trait("IntegrationSlice", "FP-IS-03")]
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
    [Trait("IntegrationSlice", "FP-IS-03")]
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
    [Trait("IntegrationSlice", "FP-IS-01")]
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

    /// <summary>
    /// The catalog fingerprint, pinned to one station list and one hash.
    /// </summary>
    /// <remarks>
    /// A second implementation is pinned to this exact pair: <c>scripts/l2/L2RouteEvidence.psm1</c>
    /// recomputes the fingerprint on the way to a journey's route evidence id, for the L2 scenarios that
    /// tell the planned route from a swapped one, and <c>scripts/l2/Test-L2RouteEvidence.ps1</c> asserts
    /// these same stations and this same hash against that copy. So when the canonical form changes, this
    /// test going red is also the notice that the pwsh copy has to move with it -- do not simply update
    /// the expected value here. Nothing builds that copy and no test here reads it, so this comment is the
    /// only place the dependency is written down (control-server#203).
    /// </remarks>
    [Fact]
    [Trait("IntegrationSlice", "FP-IS-01")]
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

    /// <summary>
    /// REQ-0356's order check: an order counts for the vehicle when it is appointed to it or
    /// executing on it, and another vehicle's order does not.
    /// </summary>
    [Fact]
    public async Task UnfinishedOrdersCountOrdersAppointedToOrExecutingOnTheVehicle()
    {
        const string orders = """
            {"code":"0","result":{"current":1,"size":100,"total":3,"records":[
              {"id":1,"orderId":"ORDER-APPOINTED","upperId":"UPPER-1","orderState":1,
               "appointVehicleKey":"VEHICLE-KEY-01","executeVehicleKey":"--"},
              {"id":2,"orderId":"ORDER-OTHER","upperId":"UPPER-2","orderState":3,
               "appointVehicleKey":"VEHICLE-KEY-02","executeVehicleKey":"VEHICLE-KEY-02"},
              {"id":3,"orderId":"ORDER-HELD","upperId":"UPPER-3","orderState":7,
               "appointVehicleKey":null,"executeVehicleKey":"VEHICLE-KEY-01"}]}}
            """;
        RecordingHandler handler = new((request, _) =>
            request.RequestUri?.AbsolutePath == "/api/order/v1/orderRecord"
                ? JsonResponse(orders)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotVehicleOrderObservation result = await gateway.ReadUnfinishedOrdersAsync(
            "VEHICLE-KEY-01", TestContext.Current.CancellationToken);

        Assert.True(result.IsKnown);
        Assert.True(result.HasUnfinishedOrder);
        Assert.Equal(["ORDER-APPOINTED", "ORDER-HELD"], result.UnfinishedOrderIds);
        // Each order's state from the same listing (control-server#335 review, item 2): the release decides on it.
        Assert.Equal((int?)RiotOrderState.Paused, result.StateOf("ORDER-HELD"));
        Assert.Equal((int?)RiotOrderState.Queueing, result.StateOf("ORDER-APPOINTED"));
        Assert.Null(result.StateOf("ORDER-OTHER"));
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>
    /// An order listed twice keeps the state of neither (control-server#335 incremental review): the release reads a state it
    /// cannot vouch for as "not shown PAUSED", never as whichever copy came last.
    /// </summary>
    [Fact]
    public async Task AnUnfinishedOrderListedTwiceHasNoState()
    {
        const string orders = """
            {"code":"0","result":{"current":1,"size":100,"total":2,"records":[
              {"id":1,"orderId":"ORDER-HELD","upperId":"UPPER-3","orderState":3,
               "appointVehicleKey":null,"executeVehicleKey":"VEHICLE-KEY-01"},
              {"id":2,"orderId":"ORDER-HELD","upperId":"UPPER-3","orderState":7,
               "appointVehicleKey":null,"executeVehicleKey":"VEHICLE-KEY-01"}]}}
            """;
        RecordingHandler handler = new((request, _) =>
            request.RequestUri?.AbsolutePath == "/api/order/v1/orderRecord"
                ? JsonResponse(orders)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotVehicleOrderObservation result = await gateway.ReadUnfinishedOrdersAsync(
            "VEHICLE-KEY-01", TestContext.Current.CancellationToken);

        Assert.True(result.HasUnfinishedOrder);
        Assert.Null(result.StateOf("ORDER-HELD"));
    }

    [Fact]
    public async Task UnfinishedOrdersAreNoneWhenRiotHoldsNoneForTheVehicle()
    {
        RecordingHandler handler = new((_, _) => JsonResponse(CompleteOrdersJson()));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotVehicleOrderObservation result = await gateway.ReadUnfinishedOrdersAsync(
            "VEHICLE-KEY-01", TestContext.Current.CancellationToken);

        Assert.False(result.HasUnfinishedOrder);
        Assert.Empty(result.UnfinishedOrderIds);
    }

    /// <summary>
    /// A page that does not cover every record, and a failed read, are unknown — never "none".
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnfinishedOrdersAreUnknownWhenRiotDoesNotAnswerCompletely(bool partialPage)
    {
        RecordingHandler handler = new((_, _) => partialPage
            ? JsonResponse("""{"code":"0","result":{"current":1,"size":100,"total":101,"records":[]}}""")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotVehicleOrderObservation result = await gateway.ReadUnfinishedOrdersAsync(
            "VEHICLE-KEY-01", TestContext.Current.CancellationToken);

        Assert.False(result.IsKnown);
        Assert.Null(result.HasUnfinishedOrder);
    }

    /// <summary>
    /// control-server#330: the unfiltered listing carries every unfinished order with the fields ownership is judged on --
    /// orderId, upperId, state, and both vehicle keys as RIoT sent them, the <c>"--"</c> placeholder included.
    /// </summary>
    [Fact]
    public async Task TheUnfinishedOrderListingCarriesEveryOrderWithItsKeys()
    {
        const string orders = """
            {"code":"0","result":{"current":1,"size":100,"total":3,"records":[
              {"id":1,"orderId":"ORDER-QUEUED","upperId":"UPPER-1","orderState":1,
               "appointVehicleKey":"VEHICLE-KEY-01","executeVehicleKey":"--"},
              {"id":2,"orderId":"ORDER-OTHER","upperId":null,"orderState":3,
               "appointVehicleKey":"VEHICLE-KEY-02","executeVehicleKey":"VEHICLE-KEY-02"},
              {"id":3,"orderId":"ORDER-HANG","upperId":"UPPER-3","orderState":9,
               "appointVehicleKey":null,"executeVehicleKey":"VEHICLE-KEY-01"}]}}
            """;
        RecordingHandler handler = new((request, _) =>
            request.RequestUri?.AbsolutePath == "/api/order/v1/orderRecord"
                ? JsonResponse(orders)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotUnfinishedOrderListing result = await gateway.ListUnfinishedOrdersAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsComplete);
        Assert.Equal(
        [
            new RiotListedOrder("ORDER-QUEUED", "UPPER-1", 1, "VEHICLE-KEY-01", "--"),
            new RiotListedOrder("ORDER-OTHER", null, 3, "VEHICLE-KEY-02", "VEHICLE-KEY-02"),
            new RiotListedOrder("ORDER-HANG", "UPPER-3", 9, null, "VEHICLE-KEY-01"),
        ], result.Orders);
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>
    /// 两处未完成订单读（全清单、按车）问的是同一组非终态，不多不少：1 QUEUEING、3 EXECUTING、7 PAUSED、8 SUSPENDED、9 HANG、
    /// 10 QUEUE_PRIORITY（control-server#404 第二轮审查 L-2：原来漏了 8 与 10，带着这两种状态的单的车被读成「名下没有单」）。
    /// 安全读数那一处由 <c>SafetyHandler</c> 核。
    /// </summary>
    [Theory]
    [InlineData("listing")]
    [InlineData("by-vehicle")]
    public async Task TheUnfinishedOrderReadsAskForEveryNonFinalState(string read)
    {
        List<int[]> asked = [];
        RecordingHandler handler = new((request, _) =>
        {
            Assert.Equal("/api/order/v1/orderRecord", request.RequestUri?.AbsolutePath);
            asked.Add(
            [
                .. request.RequestUri!.Query.TrimStart('?').Split('&')
                    .Where(pair => pair.StartsWith("filterByState=", StringComparison.Ordinal))
                    .Select(pair => int.Parse(pair["filterByState=".Length..], System.Globalization.CultureInfo.InvariantCulture))
                    .Order(),
            ]);
            return JsonResponse("""{"code":"0","result":{"current":1,"size":100,"total":0,"records":[]}}""");
        });
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        if (read == "listing")
        {
            Assert.True((await gateway.ListUnfinishedOrdersAsync(TestContext.Current.CancellationToken)).IsComplete);
        }
        else
        {
            Assert.False(
                (await gateway.ReadUnfinishedOrdersAsync("VEHICLE-KEY-01", TestContext.Current.CancellationToken)).HasUnfinishedOrder);
        }

        Assert.Equal(
            [
                RiotOrderState.Queueing, RiotOrderState.Executing, RiotOrderState.Paused, RiotOrderState.Suspended,
                RiotOrderState.Hang, RiotOrderState.QueuePriority,
            ],
            Assert.Single(asked));
    }

    /// <summary>
    /// A page that does not cover every record, a failed read, and a record with no orderId to address it by, all make the
    /// listing incomplete -- never an empty "no foreign order".
    /// </summary>
    [Theory]
    [InlineData("partial-page")]
    [InlineData("read-failed")]
    [InlineData("record-without-order-id")]
    public async Task TheUnfinishedOrderListingIsIncompleteWhenRiotDoesNotAccountForEveryOrder(string answer)
    {
        RecordingHandler handler = new((_, _) => answer switch
        {
            "partial-page" => JsonResponse("""{"code":"0","result":{"current":1,"size":100,"total":101,"records":[]}}"""),
            "record-without-order-id" => JsonResponse("""
                {"code":"0","result":{"current":1,"size":100,"total":1,"records":[
                  {"id":1,"orderId":null,"upperId":"UPPER-1","orderState":3,
                   "appointVehicleKey":"VEHICLE-KEY-01","executeVehicleKey":"VEHICLE-KEY-01"}]}}
                """),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            },
        });
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotUnfinishedOrderListing result = await gateway.ListUnfinishedOrdersAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsComplete);
    }

    /// <summary>
    /// control-server#525：真实 RIoT 10-09 有 252 条非终态单（3 页），几乎全是别的产线的、没有车的单。三处读都要逐页读全；
    /// 没有一条挂在本车上时，安全读数是 STOPPED，按车读是「没有」，全清单是完整的 252 条。修之前三处都只读第 1 页，判读不全。
    /// </summary>
    [Theory]
    [InlineData("safety")]
    [InlineData("by-vehicle")]
    [InlineData("listing")]
    public async Task NonFinalOrdersAcrossSeveralPagesAreReadInFull(string read)
    {
        PagedOrders orders = new(ForeignOrders(252));
        await using RiotSession session = CreateSession(PagedOrdersHandler(orders));
        HttpRiotMovementGateway gateway = new(session);

        switch (read)
        {
            case "safety":
                RiotVehicleSafetyObservation safety = await gateway.ReadVehicleSafetyAsync(
                    "VEHICLE-KEY-01", TestContext.Current.CancellationToken);
                Assert.Equal(RiotVehicleMotionState.Stopped, safety.MotionState);
                Assert.Empty(safety.ReasonCodes);
                break;
            case "by-vehicle":
                RiotVehicleOrderObservation byVehicle = await gateway.ReadUnfinishedOrdersAsync(
                    "VEHICLE-KEY-01", TestContext.Current.CancellationToken);
                Assert.True(byVehicle.IsKnown);
                Assert.False(byVehicle.HasUnfinishedOrder);
                break;
            default:
                RiotUnfinishedOrderListing listing = await gateway.ListUnfinishedOrdersAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(listing.IsComplete);
                Assert.Equal(252, listing.Orders.Count);
                Assert.Equal(252, listing.Orders.Select(order => order.OrderId).Distinct().Count());
                break;
        }
        Assert.Equal([1, 2, 3], orders.PagesAsked);
    }

    /// <summary>
    /// control-server#525：本车的单在第 3 页上也要被看见——安全读数非停稳（<c>RIOT_NONFINAL_ORDER_PRESENT</c>），按车读与全清单都带着它。
    /// </summary>
    [Theory]
    [InlineData("safety")]
    [InlineData("by-vehicle")]
    [InlineData("listing")]
    public async Task AnOrderOnTheVehicleOnALaterPageIsSeen(string read)
    {
        List<string> records = ForeignOrders(251);
        records.Insert(230, OrderRecordJson(9001, "ORDER-MINE", 1, "VEHICLE-KEY-01", "--"));
        PagedOrders orders = new(records);
        await using RiotSession session = CreateSession(PagedOrdersHandler(orders));
        HttpRiotMovementGateway gateway = new(session);

        switch (read)
        {
            case "safety":
                RiotVehicleSafetyObservation safety = await gateway.ReadVehicleSafetyAsync(
                    "VEHICLE-KEY-01", TestContext.Current.CancellationToken);
                Assert.Equal(RiotVehicleMotionState.Unknown, safety.MotionState);
                Assert.Equal(["RIOT_NONFINAL_ORDER_PRESENT"], safety.ReasonCodes);
                break;
            case "by-vehicle":
                RiotVehicleOrderObservation byVehicle = await gateway.ReadUnfinishedOrdersAsync(
                    "VEHICLE-KEY-01", TestContext.Current.CancellationToken);
                Assert.True(byVehicle.HasUnfinishedOrder);
                Assert.Equal(["ORDER-MINE"], byVehicle.UnfinishedOrderIds);
                break;
            default:
                RiotUnfinishedOrderListing listing = await gateway.ListUnfinishedOrdersAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(listing.IsComplete);
                Assert.Contains(new RiotListedOrder("ORDER-MINE", "UPPER-9001", 1, "VEHICLE-KEY-01", "--"), listing.Orders);
                break;
        }
        Assert.Equal([1, 2, 3], orders.PagesAsked);
    }

    /// <summary>
    /// control-server#525：读不全仍按读不全处理，不拼出一个 RIoT 从没有过的快照。
    /// <c>over-cap</c>：2001 条要 21 页，超过 20 页上限，读完第 1 页就停；<c>total-grows</c>／<c>total-shrinks</c>：读第 2 页时总数变了；
    /// <c>page-repeated</c>：RIoT 不认 pageNum、每次都回第 1 页（修之前的替身 RIoT 就是这样）；<c>short-page</c>：中间一页少一条；
    /// <c>record-repeated</c>：同一条记录在两页上都出现（分页期间单子前移）。
    /// </summary>
    [Theory]
    [InlineData("safety", "over-cap")]
    [InlineData("safety", "total-grows")]
    [InlineData("safety", "total-shrinks")]
    [InlineData("safety", "page-repeated")]
    [InlineData("safety", "short-page")]
    [InlineData("safety", "record-repeated")]
    [InlineData("by-vehicle", "over-cap")]
    [InlineData("by-vehicle", "total-grows")]
    [InlineData("by-vehicle", "total-shrinks")]
    [InlineData("by-vehicle", "page-repeated")]
    [InlineData("by-vehicle", "short-page")]
    [InlineData("by-vehicle", "record-repeated")]
    [InlineData("listing", "over-cap")]
    [InlineData("listing", "total-grows")]
    [InlineData("listing", "total-shrinks")]
    [InlineData("listing", "page-repeated")]
    [InlineData("listing", "short-page")]
    [InlineData("listing", "record-repeated")]
    public async Task APagedReadThatDoesNotAddUpIsIncomplete(string read, string fault)
    {
        PagedOrders orders = new(ForeignOrders(fault == "over-cap" ? 2001 : 252));
        switch (fault)
        {
            case "total-grows": orders.TotalOverride = page => page == 1 ? 252 : 253; break;
            case "total-shrinks": orders.TotalOverride = page => page == 1 ? 252 : 251; break;
            case "page-repeated": orders.IgnorePageNum = true; break;
            case "short-page": orders.DropOneRecordOnPage = 2; break;
            case "record-repeated": orders.RepeatLastRecordOfPreviousPageOn = 2; break;
        }
        await using RiotSession session = CreateSession(PagedOrdersHandler(orders));
        HttpRiotMovementGateway gateway = new(session);

        switch (read)
        {
            case "safety":
                RiotVehicleSafetyObservation safety = await gateway.ReadVehicleSafetyAsync(
                    "VEHICLE-KEY-01", TestContext.Current.CancellationToken);
                Assert.Equal(RiotVehicleMotionState.Unknown, safety.MotionState);
                Assert.Equal(["RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN"], safety.ReasonCodes);
                break;
            case "by-vehicle":
                RiotVehicleOrderObservation byVehicle = await gateway.ReadUnfinishedOrdersAsync(
                    "VEHICLE-KEY-01", TestContext.Current.CancellationToken);
                Assert.False(byVehicle.IsKnown);
                Assert.Null(byVehicle.HasUnfinishedOrder);
                break;
            default:
                RiotUnfinishedOrderListing listing = await gateway.ListUnfinishedOrdersAsync(
                    TestContext.Current.CancellationToken);
                Assert.False(listing.IsComplete);
                Assert.Empty(listing.Orders);
                break;
        }
        if (fault == "over-cap")
        {
            Assert.Equal([1], orders.PagesAsked);
        }
    }

    /// <summary>
    /// control-server#330: one order's state by its RIoT orderId through <c>detailByOrderId</c>; a failed read, or an answer
    /// about another order, is no state at all.
    /// </summary>
    [Theory]
    [InlineData("found", 2)]
    [InlineData("another-order", null)]
    [InlineData("read-failed", null)]
    public async Task AnOrderStateIsReadByItsRiotOrderId(string answer, int? expected)
    {
        RecordingHandler handler = new((request, _) =>
        {
            Assert.Equal("/api/order/v1/orderRecord/detailByOrderId/ORDER-001", request.RequestUri?.AbsolutePath);
            return answer switch
            {
                "found" => JsonResponse(FoundOrderJson(orderState: 2)),
                "another-order" => JsonResponse(FoundOrderJson(orderState: 2).Replace("\"ORDER-001\"", "\"ORDER-999\"", StringComparison.Ordinal)),
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                },
            };
        });
        await using RiotSession session = CreateSession(handler);
        HttpRiotMovementGateway gateway = new(session);

        RiotOrderStateReading result = await gateway.ReadOrderStateAsync("ORDER-001", TestContext.Current.CancellationToken);

        Assert.Equal(("ORDER-001", expected), (result.OrderId, result.OrderState));
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
        Assert.Contains("filterByState=8", pairs);
        Assert.Contains("filterByState=9", pairs);
        Assert.Contains("filterByState=10", pairs);
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

    private static string OrderRecordJson(
        long id, string orderId, int orderState, string? appointVehicleKey, string? executeVehicleKey)
    {
        string appointed = appointVehicleKey is null ? "null" : $"\"{appointVehicleKey}\"";
        string execute = executeVehicleKey is null ? "null" : $"\"{executeVehicleKey}\"";
        return $$$"""
            {"id":{{{id}}},"orderId":"{{{orderId}}}","upperId":"UPPER-{{{id}}}","orderState":{{{orderState}}},"appointVehicleKey":{{{appointed}}},"executeVehicleKey":{{{execute}}}}
            """;
    }

    /// <summary>Other lines' orders, as on the real RIoT on 2026-10-09: mostly state 8 with no vehicle, a few running on others.</summary>
    private static List<string> ForeignOrders(int count) =>
    [
        .. Enumerable.Range(1, count).Select(index => index % 20 == 0
            ? OrderRecordJson(index, $"ORDER-{index}", 3, "OTHER-VEHICLE", "OTHER-VEHICLE")
            : OrderRecordJson(index, $"ORDER-{index}", 8, null, "--")),
    ];

    /// <summary>
    /// A RIoT order listing served page by page as <c>pageNum</c> asks, with the faults
    /// <see cref="APagedReadThatDoesNotAddUpIsIncomplete"/> injects.
    /// </summary>
    private sealed class PagedOrders(List<string> records)
    {
        public List<int> PagesAsked { get; } = [];
        public Func<int, int>? TotalOverride { get; set; }
        public bool IgnorePageNum { get; set; }
        public int? DropOneRecordOnPage { get; set; }
        public int? RepeatLastRecordOfPreviousPageOn { get; set; }

        public string Page(int pageNum, int pageSize)
        {
            PagesAsked.Add(pageNum);
            int served = IgnorePageNum ? 1 : pageNum;
            List<string> page = [.. records.Skip((served - 1) * pageSize).Take(pageSize)];
            if (DropOneRecordOnPage == served) page.RemoveAt(0);
            if (RepeatLastRecordOfPreviousPageOn == served)
            {
                page.RemoveAt(page.Count - 1);
                page.Insert(0, records[((served - 1) * pageSize) - 1]);
            }
            int total = TotalOverride?.Invoke(served) ?? records.Count;
            return $$$"""
                {"code":"0","result":{"current":{{{served}}},"size":{{{pageSize}}},"total":{{{total}}},"records":[{{{string.Join(",", page)}}}]}}
                """;
        }
    }

    private static RecordingHandler PagedOrdersHandler(PagedOrders orders) => new((request, _) =>
    {
        if (request.RequestUri?.AbsolutePath == "/api/task/v1/task/getVehicleInfo/VEHICLE-KEY-01")
        {
            return JsonResponse(SafeVehicleJson("MT_FINISHED", 0));
        }

        Assert.Equal("/api/order/v1/orderRecord", request.RequestUri?.AbsolutePath);
        Dictionary<string, string> query = request.RequestUri!.Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair[0] is "pageNum" or "pageSize")
            .ToDictionary(pair => pair[0], pair => pair[1]);
        return JsonResponse(orders.Page(
            int.Parse(query["pageNum"], System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(query["pageSize"], System.Globalization.CultureInfo.InvariantCulture)));
    });

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
