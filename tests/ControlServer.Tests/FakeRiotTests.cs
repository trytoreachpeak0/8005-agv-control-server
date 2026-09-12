using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.FakeRiot;
using ControlServer.Infrastructure.Adapters;
using Microsoft.AspNetCore.Builder;
using RIoT.Sdk.Core;
using RIoT.Sdk.Facade;

namespace ControlServer.Tests;

/// <summary>
/// Black-box tests over a real Kestrel: the production RIoT adapter talks to the fake exactly as
/// it talks to the plant's RCS, and the control plane is driven over HTTP. Anything asserted
/// through a shortcut would prove the shortcut, not the double.
/// </summary>
public sealed class FakeRiotTests
{
    private const string VehicleKey = "BROKERX-TEST-0001";

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task VehicleFactsAndMapCatalogAreServedInTheShapeTheProductionAdapterParses()
    {
        await using FakeRiotFixture fixture = await FakeRiotFixture.StartAsync();
        HttpRiotMovementGateway gateway = fixture.Gateway();

        RiotVehicleObservation vehicle = await gateway.ReadVehicleAsync(
            VehicleKey, TestContext.Current.CancellationToken);
        RiotMapStationCatalogSnapshot map = await gateway.ReadMapStationsAsync(
            25, TestContext.Current.CancellationToken);

        Assert.True(vehicle.Connected);
        Assert.True(vehicle.Enabled);
        Assert.Equal("IDLE", vehicle.ProcState);
        Assert.Equal(210, vehicle.CurrentStationId);
        Assert.Equal(0d, vehicle.Speed);
        Assert.Equal(0, vehicle.LockStatus);
        Assert.Equal(80, vehicle.BatteryPercent);
        Assert.Equal("NO_CHARGE", vehicle.BatteryState);
        Assert.Equal([11, 12, 210, 211], map.Stations.Select(station => station.StationId));
        Assert.Equal(["C15-13", "N1-3_N1-7", "关卡", "充电点1"], map.Stations.Select(station => station.StationName));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task DrivingTheVehicleThroughTheControlPlaneChangesWhatTheAdapterSees()
    {
        // The whole reason the fake exists: two of the 2026-09-03 defects needed the vehicle to
        // move and then stop, and nothing in the test estate could make that happen.
        await using FakeRiotFixture fixture = await FakeRiotFixture.StartAsync();
        HttpRiotMovementGateway gateway = fixture.Gateway();

        await fixture.CommandAsync(HttpMethod.Put, "vehicle", new
        {
            vehicleKey = VehicleKey,
            procState = "RUNNING",
            movementState = "MT_RUNNING",
            speed = 0.8,
            currentPosition = 12
        });
        RiotVehicleSafetyObservation moving = await gateway.ReadVehicleSafetyAsync(
            VehicleKey, TestContext.Current.CancellationToken);

        await fixture.CommandAsync(HttpMethod.Put, "vehicle", new
        {
            vehicleKey = VehicleKey,
            procState = "IDLE",
            movementState = "MT_FINISHED",
            speed = 0d
        });
        RiotVehicleSafetyObservation stopped = await gateway.ReadVehicleSafetyAsync(
            VehicleKey, TestContext.Current.CancellationToken);
        RiotVehicleObservation card = await gateway.ReadVehicleAsync(
            VehicleKey, TestContext.Current.CancellationToken);

        Assert.Equal(RiotVehicleMotionState.Moving, moving.MotionState);
        Assert.Equal(RiotVehicleMotionState.Stopped, stopped.MotionState);
        Assert.Empty(stopped.ReasonCodes);
        Assert.Equal(12, card.CurrentStationId);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-03")]
    public async Task CreatingAnOrderQueuesItAndTheSameUpperIdIsRefusedAsAlreadyExisting()
    {
        await using FakeRiotFixture fixture = await FakeRiotFixture.StartAsync();
        HttpRiotMovementGateway gateway = fixture.Gateway();
        OrderIntent intent = Intent("UPPER-001", 12);

        RiotOrderObservation created = await gateway.CreateAsync(
            intent, TestContext.Current.CancellationToken);
        RiotOrderObservation duplicate = await gateway.CreateAsync(
            intent, TestContext.Current.CancellationToken);
        RiotOrderObservation reconciled = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Active, created.Kind);
        Assert.Equal("ORDER-000001", created.OrderId);
        // BC-ORDER-004 is not an error to the control server: it means the order this intent names
        // is already there, so reconcile owns the answer from here.
        Assert.Equal("0610008", duplicate.Receipt?.BusinessCode);
        Assert.Equal(RiotOrderObservationKind.Active, reconciled.Kind);
        Assert.Equal("ORDER-000001", reconciled.OrderId);
        // A QUEUEING order reports the "--" placeholder in executeVehicleKey, so the appointed key
        // is the only identity available until RIoT binds a vehicle.
        Assert.Equal("AGV-8005-01", reconciled.VehicleKey);
        Assert.Equal(12, reconciled.DestinationStationId);
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task OnlyAnOrderCarryingTheStartChargingActionLeavesTheVehicleCharging()
    {
        // 8005-agv-program#53: scenarios used to write CHARGING on arrival themselves, so a charge order
        // that was only a movement stayed green. Completing a movement leaves the battery alone;
        // completing an order that carries act(78, 1, 0) is what engages the charger (Q-033).
        await using FakeRiotFixture fixture = await FakeRiotFixture.StartAsync();
        HttpRiotMovementGateway gateway = fixture.Gateway();
        RiotOrderObservation move = await gateway.CreateAsync(
            Intent("UPPER-MOVE-001", 12), TestContext.Current.CancellationToken);
        RiotOrderObservation charge = await gateway.CreateAsync(
            Intent("UPPER-CHARGE-001", 211) with { Purpose = "TO_CHARGER" }, TestContext.Current.CancellationToken);

        await CompleteAsync("UPPER-MOVE-001");
        RiotVehicleObservation afterMove = await gateway.ReadVehicleAsync(
            VehicleKey, TestContext.Current.CancellationToken);
        await CompleteAsync("UPPER-CHARGE-001");
        RiotVehicleObservation afterCharge = await gateway.ReadVehicleAsync(
            VehicleKey, TestContext.Current.CancellationToken);
        RiotOrderObservation reconciled = await gateway.ReconcileByUpperIdAsync(
            "UPPER-CHARGE-001", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Active, move.Kind);
        Assert.Equal(RiotOrderObservationKind.Active, charge.Kind);
        Assert.Equal("NO_CHARGE", afterMove.BatteryState);
        Assert.Equal("CHARGING", afterCharge.BatteryState);
        Assert.Equal(RiotOrderObservationKind.Terminal, reconciled.Kind);
        Assert.Equal(211, reconciled.DestinationStationId);

        async Task CompleteAsync(string upperId)
        {
            await fixture.CommandAsync(HttpMethod.Put, $"orders/{upperId}", new
            {
                orderState = 3,
                executeVehicleKey = VehicleKey
            });
            await fixture.CommandAsync(HttpMethod.Put, $"orders/{upperId}", new { orderState = 5 });
        }
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task AnOrderAdvancedToCompleteBecomesATerminalArrivalObservation()
    {
        await using FakeRiotFixture fixture = await FakeRiotFixture.StartAsync();
        HttpRiotMovementGateway gateway = fixture.Gateway();
        await gateway.CreateAsync(Intent("UPPER-001", 12), TestContext.Current.CancellationToken);

        await fixture.CommandAsync(HttpMethod.Put, "orders/UPPER-001", new
        {
            orderState = 5,
            executeVehicleKey = VehicleKey
        });
        RiotOrderObservation result = await gateway.ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Terminal, result.Kind);
        Assert.Equal(5, result.OrderState);
        Assert.Equal(VehicleKey, result.VehicleKey);
        Assert.Equal(12, result.DestinationStationId);
    }

    [Fact]
    public async Task AnUnknownUpperIdIsNotFoundRatherThanUnknown()
    {
        await using FakeRiotFixture fixture = await FakeRiotFixture.StartAsync();

        RiotOrderObservation result = await fixture.Gateway().ReconcileByUpperIdAsync(
            "UPPER-404", TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.NotFound, result.Kind);
    }

    [Fact]
    public async Task AServerErrorFaultLeavesTheAdapterFailClosedRatherThanOptimistic()
    {
        await using FakeRiotFixture fixture = await FakeRiotFixture.StartAsync();
        await fixture.CommandAsync(HttpMethod.Put, "faults/http", new { mode = "ServerError" });

        RiotVehicleObservation vehicle = await fixture.Gateway().ReadVehicleAsync(
            VehicleKey, TestContext.Current.CancellationToken);

        Assert.False(vehicle.Connected);
        Assert.Equal("UNKNOWN", vehicle.ProcState);
        Assert.Null(vehicle.Speed);
    }

    [Fact]
    public async Task TheSameCommandIdReplaysItsFirstResultAndADifferentBodyUnderItConflicts()
    {
        await using FakeRiotFixture fixture = await FakeRiotFixture.StartAsync();

        JsonElement first = await fixture.CommandAsync(
            HttpMethod.Put, "vehicle", new { vehicleKey = VehicleKey, speed = 1.5 }, "cmd-1");
        JsonElement replay = await fixture.CommandAsync(
            HttpMethod.Put, "vehicle", new { vehicleKey = VehicleKey, speed = 1.5 }, "cmd-1");
        HttpResponseMessage conflict = await fixture.SendCommandAsync(
            HttpMethod.Put, "vehicle", new { vehicleKey = VehicleKey, speed = 2.5 }, "cmd-1");
        using JsonDocument conflictBody = JsonDocument.Parse(
            await conflict.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.True(first.GetProperty("body").GetProperty("changed").GetBoolean());
        Assert.False(first.GetProperty("body").GetProperty("replayed").GetBoolean());
        Assert.True(replay.GetProperty("body").GetProperty("replayed").GetBoolean());
        Assert.Equal(
            first.GetProperty("body").GetProperty("appliedRevision").GetInt64(),
            replay.GetProperty("body").GetProperty("appliedRevision").GetInt64());
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "COMMAND_ID_CONFLICT",
            conflictBody.RootElement.GetProperty("body").GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task SettingAValueThatIsAlreadySetSucceedsWithoutMovingTheRevision()
    {
        await using FakeRiotFixture fixture = await FakeRiotFixture.StartAsync();
        long before = await fixture.RevisionAsync();

        JsonElement outcome = await fixture.CommandAsync(
            HttpMethod.Put, "vehicle", new { vehicleKey = VehicleKey, procState = "IDLE" });

        Assert.False(outcome.GetProperty("body").GetProperty("changed").GetBoolean());
        Assert.Equal(before, await fixture.RevisionAsync());
    }

    [Fact]
    public async Task AStaleExpectedRevisionIsRefusedAndChangesNothing()
    {
        await using FakeRiotFixture fixture = await FakeRiotFixture.StartAsync();
        await fixture.CommandAsync(HttpMethod.Put, "vehicle", new { vehicleKey = VehicleKey, speed = 1.0 });
        long current = await fixture.RevisionAsync();

        HttpResponseMessage response = await fixture.SendCommandAsync(
            HttpMethod.Put,
            "vehicle",
            new { vehicleKey = VehicleKey, speed = 2.0 },
            "cmd-stale",
            expectedRevision: current - 1);
        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "REVISION_CONFLICT",
            body.RootElement.GetProperty("body").GetProperty("reasonCode").GetString());
        Assert.Equal(current, await fixture.RevisionAsync());
    }

    [Fact]
    public async Task ResetStartsANewRoundAtRevisionOneAndReplayingItReturnsThatSameRound()
    {
        await using FakeRiotFixture fixture = await FakeRiotFixture.StartAsync();
        await fixture.Gateway().CreateAsync(Intent("UPPER-001", 12), TestContext.Current.CancellationToken);
        string originalRun = await fixture.RunIdAsync();

        JsonElement reset = await fixture.CommandAsync(
            HttpMethod.Post, "reset", new { }, "cmd-reset-1", runId: originalRun);
        string newRun = await fixture.RunIdAsync();
        JsonElement replay = await fixture.CommandAsync(
            HttpMethod.Post, "reset", new { }, "cmd-reset-1", runId: originalRun);

        Assert.NotEqual(originalRun, newRun);
        Assert.Equal(1, reset.GetProperty("revision").GetInt64());
        Assert.True(replay.GetProperty("body").GetProperty("replayed").GetBoolean());
        Assert.Equal(newRun, replay.GetProperty("runId").GetString());
        RiotOrderObservation gone = await fixture.Gateway().ReconcileByUpperIdAsync(
            "UPPER-001", TestContext.Current.CancellationToken);
        Assert.Equal(RiotOrderObservationKind.NotFound, gone.Kind);
    }

    [Fact]
    public async Task ACommandCarryingAStaleRunIdIsRefused()
    {
        await using FakeRiotFixture fixture = await FakeRiotFixture.StartAsync();

        HttpResponseMessage response = await fixture.SendCommandAsync(
            HttpMethod.Put,
            "vehicle",
            new { vehicleKey = VehicleKey, speed = 1.0 },
            "cmd-stale-run",
            runId: "run-not-this-one");
        using JsonDocument body = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "RUN_ID_MISMATCH",
            body.RootElement.GetProperty("body").GetProperty("reasonCode").GetString());
    }

    [Fact]
    public async Task TheDataPlaneOffersNoWayToMoveTheVehicleOrCompleteAnOrder()
    {
        // The boundary slots-simulator draws and this fake keeps: a scenario may say what RIoT
        // observes, never issue an instruction the control server should have issued. Movement
        // reaches RIoT only through the order endpoints the adapter itself calls.
        await using FakeRiotFixture fixture = await FakeRiotFixture.StartAsync();

        HttpResponseMessage[] responses =
        [
            await fixture.Client.PostAsync(
                new Uri("/api/task/v1/order/route/" + VehicleKey, UriKind.Relative),
                JsonContent.Create(new { }),
                TestContext.Current.CancellationToken),
            await fixture.Client.PostAsync(
                new Uri("/api/order/v1/operate", UriKind.Relative),
                JsonContent.Create(new { }),
                TestContext.Current.CancellationToken),
            await fixture.Client.PostAsync(
                new Uri("/control/v1/vehicle/move", UriKind.Relative),
                JsonContent.Create(new { }),
                TestContext.Current.CancellationToken)
        ];

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
    }

    [Fact]
    [Trait("IntegrationSlice", "W2G-IS-01")]
    public async Task MapStationReadsCountEveryIterationWithoutMovingTheStateRevision()
    {
        // JourneyRuntimeEngine.ExecuteOnceAsync reads the Map station catalog first thing on every
        // iteration, including the ones where a Blocked journey makes it do nothing else, so this
        // counter is how an L2 scenario says "the runtime had N more chances and still did not do
        // it". Without it a negative assertion could only be written as a sleep.
        //
        // The revision must not move with it. Counting reads through the command engine would make
        // expectedRevision useless for the commands that carry real changes -- every poll would
        // invalidate the revision a scenario had just read.
        await using FakeRiotFixture fixture = await FakeRiotFixture.StartAsync();
        HttpRiotMovementGateway gateway = fixture.Gateway();
        long revisionBefore = await fixture.RevisionAsync();

        Assert.Equal(0, await fixture.MapStationReadsAsync());
        await gateway.ReadMapStationsAsync(25, TestContext.Current.CancellationToken);
        await gateway.ReadMapStationsAsync(25, TestContext.Current.CancellationToken);

        Assert.Equal(2, await fixture.MapStationReadsAsync());
        Assert.Equal(revisionBefore, await fixture.RevisionAsync());
    }

    private static OrderIntent Intent(string upperId, int destination) => new(
        "LEG-" + upperId,
        "D-" + upperId,
        upperId,
        "TO_PICKUP",
        "ST-" + destination.ToString(System.Globalization.CultureInfo.InvariantCulture),
        new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero),
        "AGV-8005-01",
        25,
        destination,
        1,
        1);

    private sealed class FakeRiotFixture : IAsyncDisposable
    {
        private WebApplication app = null!;

        public HttpClient Client { get; private set; } = null!;

        public static async Task<FakeRiotFixture> StartAsync()
        {
            FakeRiotFixture fixture = new();
            // Port 0 lets the OS pick, so parallel test classes never collide on 58008 and no test
            // depends on the deployed port being free.
            WebApplication? app = FakeRiotHost.TryCreate(
            [
                "--FakeRiot:port=0",
                "--FakeRiot:instanceId=fake-riot-test",
                "--FakeRiot:Seed:vehicleKey=" + VehicleKey,
                "--FakeRiot:Seed:mapIdentity=MAP-TEST"
            ]);
            Assert.NotNull(app);
            fixture.app = app;
            await app.StartAsync(TestContext.Current.CancellationToken);
            string origin = app.Urls.First();
            fixture.Client = new HttpClient { BaseAddress = new Uri(origin) };
            return fixture;
        }

        public HttpRiotMovementGateway Gateway() => new(new RiotSession(new RiotOptions
        {
            BaseUrl = Client.BaseAddress!.ToString().TrimEnd('/'),
            CallApiKey = "fake-riot-test-key"
        }));

        public async Task<long> RevisionAsync() => (await SnapshotAsync()).GetProperty("revision").GetInt64();

        public async Task<long> MapStationReadsAsync() =>
            (await SnapshotAsync()).GetProperty("body").GetProperty("mapStationReads").GetInt64();

        public async Task<string> RunIdAsync() => (await SnapshotAsync()).GetProperty("runId").GetString()!;

        public async Task<JsonElement> CommandAsync(
            HttpMethod method,
            string path,
            object body,
            string? commandId = null,
            string? runId = null,
            long? expectedRevision = null)
        {
            HttpResponseMessage response = await SendCommandAsync(
                method, path, body, commandId, runId, expectedRevision);
            string json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(response.IsSuccessStatusCode, json);
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        public async Task<HttpResponseMessage> SendCommandAsync(
            HttpMethod method,
            string path,
            object body,
            string? commandId = null,
            string? runId = null,
            long? expectedRevision = null)
        {
            Dictionary<string, object?> payload = JsonSerializer
                .Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(body))!;
            payload["runId"] = runId ?? await RunIdAsync();
            payload["commandId"] = commandId ?? Guid.NewGuid().ToString("N");
            if (expectedRevision is { } revision)
            {
                payload["expectedRevision"] = revision;
            }
            using HttpRequestMessage request = new(method, new Uri("/control/v1/" + path, UriKind.Relative))
            {
                Content = JsonContent.Create(payload)
            };
            return await Client.SendAsync(request, TestContext.Current.CancellationToken);
        }

        private async Task<JsonElement> SnapshotAsync()
        {
            string json = await Client.GetStringAsync(
                new Uri("/control/v1/snapshot", UriKind.Relative), TestContext.Current.CancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }
}
