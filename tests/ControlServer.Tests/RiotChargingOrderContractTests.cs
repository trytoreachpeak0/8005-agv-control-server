using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Adapters;

namespace ControlServer.Tests;

/// <summary>
/// control-server#401 (batch 9-03) against the fake RIoT of control-server#402, over real HTTP: the production gateway
/// creates the charging order, the fake expands it through the charger's enter/exit station the way RIoT does on maps
/// 25 and 26, and the same gateway reconciles and reads what came back. Nothing here reads the double's internal state.
/// </summary>
public sealed class RiotChargingOrderContractTests
{
    private const string VehicleKey = "BROKERX-TEST-0001";
    private const int Charger = 211;
    private const int EnterExit = 212;

    [Fact]
    public async Task AChargeOrderCreatedByTheGatewayIsExpandedThroughTheEnterExitPointAndReconcilesToTheCharger()
    {
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync();
        await RegisterChargerAsync(fixture);
        HttpRiotMovementGateway gateway = fixture.Gateway();

        RiotOrderObservation created = await gateway.CreateAsync(
            Intent("UPPER-CHARGE", Charger, OrderShapes.Charge), TestContext.Current.CancellationToken);
        await fixture.CommandAsync(HttpMethod.Put, "orders/UPPER-CHARGE", new { orderState = 5, executeVehicleKey = VehicleKey });
        RiotOrderObservation reconciled = await gateway.ReconcileByUpperIdAsync(
            "UPPER-CHARGE", TestContext.Current.CancellationToken);
        RiotOrderMissionFacts facts = await gateway.ReadOrderMissionFactsAsync(
            "UPPER-CHARGE", TestContext.Current.CancellationToken);
        RiotVehicleObservation vehicle = await gateway.ReadVehicleAsync(VehicleKey, TestContext.Current.CancellationToken);

        Assert.Equal(RiotOrderObservationKind.Active, created.Kind);
        Assert.Equal(RiotOrderObservationKind.Terminal, reconciled.Kind);
        Assert.Equal(5, reconciled.OrderState);
        Assert.Equal(VehicleKey, reconciled.VehicleKey);
        Assert.Equal(25, reconciled.MapId);
        Assert.Equal(Charger, reconciled.DestinationStationId);
        Assert.Equal(RiotOrderMissionFactsStatus.Found, facts.Status);
        Assert.Equal(["move:212", "move:211", "act:78,1,0"], Shape(facts));
        Assert.Equal("CHARGING", vehicle.BatteryState);
    }

    [Fact]
    public async Task AChargeOrderThatCannotChargeReadsBackHangAndTheActResultCodeRaw()
    {
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync();
        await RegisterChargerAsync(fixture);
        await fixture.CommandAsync(HttpMethod.Put, "charging/faults", new { vehicleKey = VehicleKey, startOutcome = "CannotCharge" });
        HttpRiotMovementGateway gateway = fixture.Gateway();

        await gateway.CreateAsync(Intent("UPPER-CHARGE", Charger, OrderShapes.Charge), TestContext.Current.CancellationToken);
        await fixture.CommandAsync(HttpMethod.Put, "orders/UPPER-CHARGE", new { orderState = 5, executeVehicleKey = VehicleKey });
        RiotOrderMissionFacts facts = await gateway.ReadOrderMissionFactsAsync(
            "UPPER-CHARGE", TestContext.Current.CancellationToken);
        RiotOrderObservation reconciled = await gateway.ReconcileByUpperIdAsync(
            "UPPER-CHARGE", TestContext.Current.CancellationToken);

        Assert.Equal(9, facts.OrderState);
        RiotOrderMissionFact act = facts.Missions[^1];
        Assert.Equal(("act", 78, 1, 0, 407802), (act.Type, act.ActionId, act.ActionParam1, act.ActionParam2, act.ResultCode));
        // HANG is still an order on the charger, not an unknown one: the #406 decision needs both facts at once.
        Assert.Equal(RiotOrderObservationKind.Active, reconciled.Kind);
        Assert.Equal(Charger, reconciled.DestinationStationId);
    }

    [Fact]
    public async Task TheNextOrderAfterChargingStartsWithLeavingTheChargerAndReconcilesToItsOwnStation()
    {
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync();
        await RegisterChargerAsync(fixture);
        HttpRiotMovementGateway gateway = fixture.Gateway();
        await gateway.CreateAsync(Intent("UPPER-CHARGE", Charger, OrderShapes.Charge), TestContext.Current.CancellationToken);
        await fixture.CommandAsync(HttpMethod.Put, "orders/UPPER-CHARGE", new { orderState = 5, executeVehicleKey = VehicleKey });

        await gateway.CreateAsync(Intent("UPPER-NEXT", 12, OrderShapes.SingleMove), TestContext.Current.CancellationToken);
        RiotOrderMissionFacts facts = await gateway.ReadOrderMissionFactsAsync(
            "UPPER-NEXT", TestContext.Current.CancellationToken);
        RiotOrderObservation reconciled = await gateway.ReconcileByUpperIdAsync(
            "UPPER-NEXT", TestContext.Current.CancellationToken);

        Assert.Equal(["act:78,2,0", "move:12"], Shape(facts));
        Assert.Equal(RiotOrderObservationKind.Active, reconciled.Kind);
        Assert.Equal(12, reconciled.DestinationStationId);
    }

    private static Task<System.Text.Json.JsonElement> RegisterChargerAsync(FakeRiotTests.FakeRiotFixture fixture) =>
        fixture.CommandAsync(HttpMethod.Put, "chargers", new
        {
            chargers = new object[]
            {
                new
                {
                    mapId = 25,
                    stationId = Charger,
                    enterExitStationId = EnterExit,
                    chargeIntervalSeconds = 60,
                    chargePercentPerInterval = 1
                }
            }
        });

    private static string[] Shape(RiotOrderMissionFacts facts) =>
    [
        .. facts.Missions.Select(mission => mission.Type == "act"
            ? FormattableString.Invariant($"act:{mission.ActionId},{mission.ActionParam1},{mission.ActionParam2}")
            : FormattableString.Invariant($"{mission.Type}:{mission.Destination}"))
    ];

    private static OrderIntent Intent(string upperId, int destination, string orderShape) => new(
        "LEG-" + upperId,
        "D-" + upperId,
        upperId,
        "TO_CHARGER",
        FormattableString.Invariant($"ST-{destination}"),
        new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero),
        VehicleKey,
        25,
        destination,
        1,
        1,
        orderShape);
}
