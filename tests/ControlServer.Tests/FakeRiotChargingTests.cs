using System.Net.Http.Json;
using System.Text.Json;
using ControlServer.Application;
using ControlServer.Domain;
using ControlServer.Infrastructure.Adapters;

namespace ControlServer.Tests;

/// <summary>
/// control-server#402: the fake RIoT's charging model, driven over real HTTP the way an L2 scenario drives it. What is
/// asserted is what the control server's side reads -- the vehicle card through the production gateway, the order detail
/// off the RIoT data plane -- never the double's internal state.
/// </summary>
public sealed class FakeRiotChargingTests
{
    private const string VehicleKey = "BROKERX-TEST-0001";
    private const string SecondKey = "BROKERX-TEST-0002";
    private const string ThirdKey = "BROKERX-TEST-0003";
    private const int Charger = 211;
    private const int EnterExit = 212;

    private static readonly DateTimeOffset Start = new(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WithNothingRegisteredASingleMoveOrderIsAnsweredByteForByteAsBefore()
    {
        // Captured from fp/v2-impl@0c0edfa5, before any charging code existed. A scenario that never registers a charger
        // must see every one of these answers unchanged, down to the byte.
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync();
        HttpRiotMovementGateway gateway = fixture.Gateway();
        await gateway.CreateAsync(Intent("UPPER-PIN", 12), TestContext.Current.CancellationToken);
        await fixture.CommandAsync(HttpMethod.Put, "orders/UPPER-PIN", new { orderState = 5, executeVehicleKey = VehicleKey });

        string detail = await GetStringAsync(fixture, "/api/order/v1/orderRecord/detailByUpperId/UPPER-PIN");
        string list = await GetStringAsync(fixture, "/api/order/v1/orderRecord?pageSize=10");
        string card = await GetStringAsync(fixture, "/api/task/vehicles/getVehicleInfoByDeviceKey?key=" + VehicleKey);
        string stations = await GetStringAsync(fixture, "/api/imap/v1/mapInfo/stations/25");

        const string order = """{"id":488001,"orderId":"ORDER-000001","upperId":"UPPER-PIN","orderState":5,"appointVehicleKey":"AGV-8005-01","executeVehicleKey":"BROKERX-TEST-0001","endStationNo":12,"missions":[{"type":"move","mapId":25,"destination":12}]}""";
        Assert.Equal("""{"code":"0","message":"成功","result":""" + order + "}", detail);
        Assert.Equal("""{"code":"0","message":"成功","result":{"current":1,"size":10,"total":1,"records":[""" + order + "]}}", list);
        Assert.Equal(
            """{"code":"0","message":"成功","result":{"deviceKey":"BROKERX-TEST-0001","enable":true,"status":1,"procState":"IDLE","currentMap":"MAP-TEST","currentPosition":210,"battery":80,"batteryState":"NO_CHARGE","speed":0,"lockStatus":0,"orderTaskId":null}}""",
            card);
        Assert.Equal(
            """{"code":"0","message":"成功","result":[{"id":11,"name":"C15-13","edge_id":6,"pos.x":0,"pos.y":0,"pos.yaw":0,"station_offset":0,"type":1,"desc":"","user_define_properties":{}},{"id":12,"name":"N1-3_N1-7","edge_id":2,"pos.x":0,"pos.y":20000,"pos.yaw":0,"station_offset":0,"type":1,"desc":"","user_define_properties":{}},{"id":210,"name":"关卡","edge_id":4,"pos.x":20000,"pos.y":20000,"pos.yaw":0,"station_offset":0,"type":1,"desc":"","user_define_properties":{}}]}""",
            stations);
    }

    [Fact]
    public async Task AnOrderWithAnActMissionIsCreatedAndItsDetailCarriesTheActFieldsAndTheLastMovesStation()
    {
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync();

        JsonElement created = await CreateAsync(fixture, "UPPER-ACT", VehicleKey, Move(12), StartCharge());
        JsonElement detail = await DetailAsync(fixture, "UPPER-ACT");

        Assert.Equal("0", created.GetProperty("code").GetString());
        // No charger registered: nothing is expanded, and the trailing act does not become the end station.
        Assert.Equal(12, detail.GetProperty("endStationNo").GetInt32());
        JsonElement[] missions = [.. detail.GetProperty("missions").EnumerateArray()];
        Assert.Equal(2, missions.Length);
        Assert.Equal("""{"type":"move","mapId":25,"destination":12}""", missions[0].GetRawText());
        JsonElement act = missions[1];
        Assert.Equal("act", act.GetProperty("type").GetString());
        Assert.Equal(0, act.GetProperty("mapId").GetInt32());
        Assert.Equal(0, act.GetProperty("destination").GetInt32());
        Assert.Equal(78, act.GetProperty("actionId").GetInt32());
        Assert.Equal(1, act.GetProperty("actionParam1").GetInt32());
        Assert.Equal(0, act.GetProperty("actionParam2").GetInt32());
        Assert.Equal(JsonValueKind.Null, act.GetProperty("resultCode").ValueKind);
        Assert.Equal(0, act.GetProperty("missionState").GetInt32());
    }

    [Fact]
    public async Task AMoveOnlyOrderToTheChargerCompletingLeavesTheVehicleNotCharging()
    {
        // The MVP's 2026-09-12 third layer: a charge order that was only a movement parked the vehicle on the charger for
        // 13 hours at NO_CHARGE. A fake that reported CHARGING on arrival could never show that, so it must not.
        MovableClock clock = new(Start);
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(clock);
        await RegisterChargerAsync(fixture, enterExit: EnterExit);

        await CreateAsync(fixture, "UPPER-MOVE", VehicleKey, Move(Charger));
        await CompleteAsync(fixture, "UPPER-MOVE");
        clock.Advance(TimeSpan.FromMinutes(30));

        RiotVehicleObservation vehicle = await ReadVehicleAsync(fixture, VehicleKey);
        Assert.Equal("NO_CHARGE", vehicle.BatteryState);
        Assert.Equal(80, vehicle.BatteryPercent);
        Assert.Equal(5, (await DetailAsync(fixture, "UPPER-MOVE")).GetProperty("orderState").GetInt32());
    }

    [Fact]
    public async Task AStartChargingOrderCompletingReportsChargingAndTheBatteryRisesWithTheClockUpTo100()
    {
        MovableClock clock = new(Start);
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(clock);
        await RegisterChargerAsync(fixture, enterExit: EnterExit, intervalSeconds: 60, percent: 2);
        await SetBatteryAsync(fixture, VehicleKey, 20);

        await CreateAsync(fixture, "UPPER-CHARGE", VehicleKey, Move(Charger), StartCharge());
        await fixture.CommandAsync(HttpMethod.Put, "orders/UPPER-CHARGE", new { orderState = 3, executeVehicleKey = VehicleKey });
        RiotVehicleObservation beforeAct = await ReadVehicleAsync(fixture, VehicleKey);
        await CompleteAsync(fixture, "UPPER-CHARGE");
        RiotVehicleObservation atStart = await ReadVehicleAsync(fixture, VehicleKey);
        long revision = await fixture.RevisionAsync();
        clock.Advance(TimeSpan.FromSeconds(59));
        RiotVehicleObservation justBefore = await ReadVehicleAsync(fixture, VehicleKey);
        clock.Advance(TimeSpan.FromSeconds(1));
        RiotVehicleObservation oneStep = await ReadVehicleAsync(fixture, VehicleKey);
        clock.Advance(TimeSpan.FromMinutes(9));
        RiotVehicleObservation tenSteps = await ReadVehicleAsync(fixture, VehicleKey);
        clock.Advance(TimeSpan.FromHours(3));
        RiotVehicleObservation full = await ReadVehicleAsync(fixture, VehicleKey);

        Assert.Equal(("NO_CHARGE", 20), (beforeAct.BatteryState, beforeAct.BatteryPercent));
        Assert.Equal(("CHARGING", 20), (atStart.BatteryState, atStart.BatteryPercent));
        Assert.Equal(("CHARGING", 20), (justBefore.BatteryState, justBefore.BatteryPercent));
        Assert.Equal(("CHARGING", 22), (oneStep.BatteryState, oneStep.BatteryPercent));
        Assert.Equal(("CHARGING", 40), (tenSteps.BatteryState, tenSteps.BatteryPercent));
        Assert.Equal(("CHARGING", 100), (full.BatteryState, full.BatteryPercent));
        // Time passing is not a state change: a scenario's expectedRevision survives it.
        Assert.Equal(revision, await fixture.RevisionAsync());

        JsonElement act = (await DetailAsync(fixture, "UPPER-CHARGE")).GetProperty("missions")[2];
        Assert.Equal(0, act.GetProperty("resultCode").GetInt32());
        Assert.Equal(2, act.GetProperty("missionState").GetInt32());
    }

    /// <summary>
    /// control-server#405（调度 10-01 定）：充电单推到完成、开始充电时，车的当前位置就是那个桩——服务端判到桩看的是 RIoT 车卡上的当前站，
    /// 车仍报在原站时「充满让桩」那一段在 L2 里走不到。只这一处改位置；只有移动、没有开始充电动作的单照旧不动车（上一条）。
    /// </summary>
    [Fact]
    public async Task AChargeOrderThatStartsChargingLeavesTheVehicleStandingOnTheCharger()
    {
        MovableClock clock = new(Start);
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(clock);
        await RegisterChargerAsync(fixture, enterExit: EnterExit);
        RiotVehicleObservation before = await ReadVehicleAsync(fixture, VehicleKey);

        await CreateAsync(fixture, "UPPER-CHARGE-AT", VehicleKey, Move(Charger), StartCharge());
        await CompleteAsync(fixture, "UPPER-CHARGE-AT");
        RiotVehicleObservation after = await ReadVehicleAsync(fixture, VehicleKey);

        Assert.NotEqual(Charger, before.CurrentStationId);
        Assert.Equal(("CHARGING", (int?)Charger), (after.BatteryState, after.CurrentStationId));
    }

    [Fact]
    public async Task AChargerWithAnEnterExitStationExpandsTheChargeOrderIntoThreeMissionsAndOneWithoutDoesNot()
    {
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync();
        await fixture.CommandAsync(HttpMethod.Put, "maps/25/stations", new
        {
            stations = new Dictionary<string, string> { ["210"] = "关卡", ["211"] = "充电点1", ["212"] = "充电准备点1", ["213"] = "充电点2" }
        });
        await fixture.CommandAsync(HttpMethod.Put, "chargers", new
        {
            chargers = new object[]
            {
                ChargerSpec(Charger, EnterExit),
                ChargerSpec(213, null)
            }
        });

        await CreateAsync(fixture, "UPPER-211", VehicleKey, Move(Charger), StartCharge());
        await CreateAsync(fixture, "UPPER-213", VehicleKey, Move(213), StartCharge());
        JsonElement via = await DetailAsync(fixture, "UPPER-211");
        JsonElement direct = await DetailAsync(fixture, "UPPER-213");
        JsonElement stations = JsonDocument.Parse(await GetStringAsync(fixture, "/api/imap/v1/mapInfo/stations/25"))
            .RootElement.GetProperty("result");

        Assert.Equal(["move:212", "move:211", "act:78,1,0"], Shape(via));
        Assert.Equal(Charger, via.GetProperty("endStationNo").GetInt32());
        Assert.Equal(["move:213", "act:78,1,0"], Shape(direct));
        Assert.Equal(213, direct.GetProperty("endStationNo").GetInt32());
        // The station row carries the property the way map 26 does for 211: a string, on the charger only.
        Assert.Equal(
            """{"enter_exit":"212"}""",
            stations.EnumerateArray().Single(row => row.GetProperty("id").GetInt32() == Charger)
                .GetProperty("user_define_properties").GetRawText());
        Assert.All(
            stations.EnumerateArray().Where(row => row.GetProperty("id").GetInt32() != Charger),
            row => Assert.Equal("{}", row.GetProperty("user_define_properties").GetRawText()));
    }

    [Fact]
    public async Task AnOrderForAVehicleOnTheChargerStartsWithStopChargingAndExecutingItEndsCharging()
    {
        MovableClock clock = new(Start);
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(clock);
        await RegisterChargerAsync(fixture, enterExit: EnterExit, intervalSeconds: 60, percent: 1);
        await SetBatteryAsync(fixture, VehicleKey, 50);
        await ChargeAsync(fixture, "UPPER-CHARGE", VehicleKey);
        clock.Advance(TimeSpan.FromMinutes(10));

        await CreateAsync(fixture, "UPPER-LEAVE", VehicleKey, Move(12));
        JsonElement leave = await DetailAsync(fixture, "UPPER-LEAVE");
        RiotVehicleObservation queued = await ReadVehicleAsync(fixture, VehicleKey);
        await fixture.CommandAsync(HttpMethod.Put, "orders/UPPER-LEAVE", new { orderState = 3, executeVehicleKey = VehicleKey });
        RiotVehicleObservation executing = await ReadVehicleAsync(fixture, VehicleKey);
        clock.Advance(TimeSpan.FromMinutes(10));
        RiotVehicleObservation later = await ReadVehicleAsync(fixture, VehicleKey);

        // The departure is not expanded through 212 by default: the real departure order's shape was never measured.
        Assert.Equal(["act:78,2,0", "move:12"], Shape(leave));
        Assert.Equal(12, leave.GetProperty("endStationNo").GetInt32());
        Assert.Equal(("CHARGING", 60), (queued.BatteryState, queued.BatteryPercent));
        Assert.Equal(("NO_CHARGE", 60), (executing.BatteryState, executing.BatteryPercent));
        Assert.Equal(("NO_CHARGE", 60), (later.BatteryState, later.BatteryPercent));

        // Off the charger, the next order has nothing to stop.
        await CreateAsync(fixture, "UPPER-NEXT", VehicleKey, Move(12));
        Assert.Equal(["move:12"], Shape(await DetailAsync(fixture, "UPPER-NEXT")));
    }

    [Fact]
    public async Task ADepartureFromAChargerMarkedExpandDepartureGoesThroughItsEnterExitStation()
    {
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(new MovableClock(Start));
        await fixture.CommandAsync(HttpMethod.Put, "chargers", new
        {
            chargers = new object[] { ChargerSpec(Charger, EnterExit, expandDeparture: true) }
        });
        await ChargeAsync(fixture, "UPPER-CHARGE", VehicleKey);

        await CreateAsync(fixture, "UPPER-LEAVE", VehicleKey, Move(12));

        Assert.Equal(["act:78,2,0", "move:212", "move:12"], Shape(await DetailAsync(fixture, "UPPER-LEAVE")));
    }

    [Fact]
    public async Task CannotChargeHangsTheOrderAtNineWith407802AndTheVehicleNeverReportsCharging()
    {
        MovableClock clock = new(Start);
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(clock);
        await RegisterChargerAsync(fixture, enterExit: EnterExit);
        await SetFaultsAsync(fixture, new { vehicleKey = VehicleKey, startOutcome = "CannotCharge" });

        await CreateAsync(fixture, "UPPER-CHARGE", VehicleKey, Move(Charger), StartCharge());
        await fixture.CommandAsync(HttpMethod.Put, "orders/UPPER-CHARGE", new { orderState = 3, executeVehicleKey = VehicleKey });
        await CompleteAsync(fixture, "UPPER-CHARGE");
        clock.Advance(TimeSpan.FromMinutes(30));

        JsonElement detail = await DetailAsync(fixture, "UPPER-CHARGE");
        JsonElement act = detail.GetProperty("missions")[2];
        RiotVehicleObservation vehicle = await ReadVehicleAsync(fixture, VehicleKey);
        Assert.Equal(9, detail.GetProperty("orderState").GetInt32());
        Assert.Equal(407802, act.GetProperty("resultCode").GetInt32());
        Assert.Equal(1, act.GetProperty("missionState").GetInt32());
        Assert.Contains("407802", act.GetProperty("resultStr").GetString(), StringComparison.Ordinal);
        Assert.Equal(("NO_CHARGE", 80), (vehicle.BatteryState, vehicle.BatteryPercent));
        // Nothing docked, so the next order carries no stop-charging act.
        await CreateAsync(fixture, "UPPER-NEXT", VehicleKey, Move(12));
        Assert.Equal(["move:12"], Shape(await DetailAsync(fixture, "UPPER-NEXT")));
    }

    [Fact]
    public async Task CannotChargeSetOnOneOrderAffectsThatOrderOnly()
    {
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(new MovableClock(Start));
        await RegisterChargerAsync(fixture, enterExit: EnterExit);
        await CreateAsync(fixture, "UPPER-BAD", VehicleKey, Move(Charger), StartCharge());
        await SetFaultsAsync(fixture, new { upperId = "UPPER-BAD", startOutcome = "CannotCharge" });

        await CompleteAsync(fixture, "UPPER-BAD");
        RiotVehicleObservation afterBad = await ReadVehicleAsync(fixture, VehicleKey);
        await ChargeAsync(fixture, "UPPER-GOOD", VehicleKey);
        RiotVehicleObservation afterGood = await ReadVehicleAsync(fixture, VehicleKey);

        Assert.Equal(9, (await DetailAsync(fixture, "UPPER-BAD")).GetProperty("orderState").GetInt32());
        Assert.Equal("NO_CHARGE", afterBad.BatteryState);
        Assert.Equal(5, (await DetailAsync(fixture, "UPPER-GOOD")).GetProperty("orderState").GetInt32());
        Assert.Equal("CHARGING", afterGood.BatteryState);
    }

    [Fact]
    public async Task HangOnlyStopsTheOrderAtNineWithNo407802()
    {
        // REQ-0175: a HANG on its own is not "cannot charge", so L2 has to be able to produce one without the code.
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(new MovableClock(Start));
        await RegisterChargerAsync(fixture, enterExit: EnterExit);
        await SetFaultsAsync(fixture, new { vehicleKey = VehicleKey, startOutcome = "HangOnly" });

        await CreateAsync(fixture, "UPPER-CHARGE", VehicleKey, Move(Charger), StartCharge());
        await CompleteAsync(fixture, "UPPER-CHARGE");

        JsonElement detail = await DetailAsync(fixture, "UPPER-CHARGE");
        JsonElement act = detail.GetProperty("missions")[2];
        Assert.Equal(9, detail.GetProperty("orderState").GetInt32());
        Assert.Equal(JsonValueKind.Null, act.GetProperty("resultCode").ValueKind);
        Assert.DoesNotContain("407802", detail.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("NO_CHARGE", (await ReadVehicleAsync(fixture, VehicleKey)).BatteryState);
    }

    [Fact]
    public async Task AnInterruptionEndsChargingByItselfAtTheSetBatteryWithNoDepartureOrder()
    {
        MovableClock clock = new(Start);
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(clock);
        await RegisterChargerAsync(fixture, enterExit: EnterExit, intervalSeconds: 60, percent: 1);
        await SetBatteryAsync(fixture, VehicleKey, 20);
        await SetFaultsAsync(fixture, new { vehicleKey = VehicleKey, interruptAtPercent = 25 });
        await ChargeAsync(fixture, "UPPER-CHARGE", VehicleKey);

        clock.Advance(TimeSpan.FromSeconds((4 * 60) + 59));
        RiotVehicleObservation before = await ReadVehicleAsync(fixture, VehicleKey);
        clock.Advance(TimeSpan.FromSeconds(1));
        RiotVehicleObservation at = await ReadVehicleAsync(fixture, VehicleKey);
        clock.Advance(TimeSpan.FromMinutes(30));
        RiotVehicleObservation after = await ReadVehicleAsync(fixture, VehicleKey);

        Assert.Equal(("CHARGING", 24), (before.BatteryState, before.BatteryPercent));
        Assert.Equal(("NO_CHARGE", 25), (at.BatteryState, at.BatteryPercent));
        Assert.Equal(("NO_CHARGE", 25), (after.BatteryState, after.BatteryPercent));
        JsonElement orders = (await fixture.SnapshotAsync()).GetProperty("body").GetProperty("orders");
        Assert.Equal(1, orders.GetArrayLength());

        // Interrupted is not departed: the vehicle is still on the charger, so its next order still starts by stopping it.
        await CreateAsync(fixture, "UPPER-NEXT", VehicleKey, Move(12));
        Assert.Equal(["act:78,2,0", "move:12"], Shape(await DetailAsync(fixture, "UPPER-NEXT")));
    }

    [Fact]
    public async Task AVehicleOnAChargerGivenANewChargeOrderPushedStraightToFiveEndsUpCharging()
    {
        // The head act(78,2,0) runs before the order's own act(78,1,0). Applied the other way round, a push from 1 straight
        // to 5 -- what ChargeAsync does -- engaged the charger and then stopped it again.
        MovableClock clock = new(Start);
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(clock);
        await RegisterChargerAsync(fixture, enterExit: EnterExit, intervalSeconds: 60, percent: 1);
        await SetBatteryAsync(fixture, VehicleKey, 40);
        await ChargeAsync(fixture, "UPPER-FIRST", VehicleKey);
        clock.Advance(TimeSpan.FromMinutes(5));

        await CreateAsync(fixture, "UPPER-AGAIN", VehicleKey, Move(Charger), StartCharge());
        await fixture.CommandAsync(HttpMethod.Put, "orders/UPPER-AGAIN", new { orderState = 5, executeVehicleKey = VehicleKey });
        clock.Advance(TimeSpan.FromMinutes(5));

        JsonElement again = await DetailAsync(fixture, "UPPER-AGAIN");
        RiotVehicleObservation vehicle = await ReadVehicleAsync(fixture, VehicleKey);
        Assert.Equal(["act:78,2,0", "move:212", "move:211", "act:78,1,0"], Shape(again));
        Assert.Equal(5, again.GetProperty("orderState").GetInt32());
        Assert.Equal(("CHARGING", 50), (vehicle.BatteryState, vehicle.BatteryPercent));
    }

    [Fact]
    public async Task AVehicleOnAChargerWhoseNewChargeOrderHangsHasLeftTheOldCharger()
    {
        // The hang is at the order's charge act, so its head act(78,2,0) already ran: the vehicle is off the old charger,
        // not charging, and its next order has nothing to stop.
        MovableClock clock = new(Start);
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(clock);
        await RegisterChargerAsync(fixture, enterExit: EnterExit, intervalSeconds: 60, percent: 1);
        await SetBatteryAsync(fixture, VehicleKey, 40);
        await ChargeAsync(fixture, "UPPER-FIRST", VehicleKey);
        clock.Advance(TimeSpan.FromMinutes(5));

        await CreateAsync(fixture, "UPPER-AGAIN", VehicleKey, Move(Charger), StartCharge());
        await SetFaultsAsync(fixture, new { upperId = "UPPER-AGAIN", startOutcome = "CannotCharge" });
        await fixture.CommandAsync(HttpMethod.Put, "orders/UPPER-AGAIN", new { orderState = 5, executeVehicleKey = VehicleKey });
        clock.Advance(TimeSpan.FromMinutes(5));

        JsonElement again = await DetailAsync(fixture, "UPPER-AGAIN");
        RiotVehicleObservation vehicle = await ReadVehicleAsync(fixture, VehicleKey);
        Assert.Equal(9, again.GetProperty("orderState").GetInt32());
        Assert.Equal(2, again.GetProperty("missions")[0].GetProperty("missionState").GetInt32());
        Assert.Equal(407802, again.GetProperty("missions")[3].GetProperty("resultCode").GetInt32());
        Assert.Equal(("NO_CHARGE", 45), (vehicle.BatteryState, vehicle.BatteryPercent));
        await CreateAsync(fixture, "UPPER-NEXT", VehicleKey, Move(12));
        Assert.Equal(["move:12"], Shape(await DetailAsync(fixture, "UPPER-NEXT")));
    }

    [Fact]
    public async Task AVehicleOnAChargerWhoseNewChargeOrderIsPushedStraightToNineHasLeftTheOldCharger()
    {
        // The same, with the scenario writing the HANG itself: the order never passed through 3 or 5, and still got past
        // its head act before hanging at the charge act.
        MovableClock clock = new(Start);
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(clock);
        await RegisterChargerAsync(fixture, enterExit: EnterExit, intervalSeconds: 60, percent: 1);
        await SetBatteryAsync(fixture, VehicleKey, 40);
        await ChargeAsync(fixture, "UPPER-FIRST", VehicleKey);
        clock.Advance(TimeSpan.FromMinutes(5));

        await CreateAsync(fixture, "UPPER-AGAIN", VehicleKey, Move(Charger), StartCharge());
        await fixture.CommandAsync(HttpMethod.Put, "orders/UPPER-AGAIN", new { orderState = 9, executeVehicleKey = VehicleKey });
        clock.Advance(TimeSpan.FromMinutes(5));

        JsonElement again = await DetailAsync(fixture, "UPPER-AGAIN");
        RiotVehicleObservation vehicle = await ReadVehicleAsync(fixture, VehicleKey);
        Assert.Equal(9, again.GetProperty("orderState").GetInt32());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("missions")[3].GetProperty("resultCode").ValueKind);
        Assert.Equal(("NO_CHARGE", 45), (vehicle.BatteryState, vehicle.BatteryPercent));
        await CreateAsync(fixture, "UPPER-NEXT", VehicleKey, Move(12));
        Assert.Equal(["move:12"], Shape(await DetailAsync(fixture, "UPPER-NEXT")));
    }

    [Fact]
    public async Task HangOnlyCanCarryAResultCodeOtherThan407802()
    {
        // B9-08 needs "HANG with some other code" to tell apart from REQ-0174's 407802.
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(
            new MovableClock(Start),
            "--FakeRiot:Seed:AdditionalVehicleKeys:0=" + SecondKey);
        await RegisterChargerAsync(fixture, enterExit: EnterExit);
        await SetFaultsAsync(fixture, new { vehicleKey = VehicleKey, startOutcome = "HangOnly", hangResultCode = 407801 });
        await CreateAsync(fixture, "UPPER-B", SecondKey, Move(Charger), StartCharge());
        await SetFaultsAsync(fixture, new { upperId = "UPPER-B", startOutcome = "HangOnly", hangResultCode = 500 });

        await ChargeAsync(fixture, "UPPER-A", VehicleKey);
        await fixture.CommandAsync(HttpMethod.Put, "orders/UPPER-B", new { orderState = 5, executeVehicleKey = SecondKey });
        HttpResponseMessage cannotBeCannotCharge = await fixture.SendCommandAsync(
            HttpMethod.Put, "charging/faults", new { vehicleKey = VehicleKey, hangResultCode = 407802 });

        JsonElement a = await DetailAsync(fixture, "UPPER-A");
        JsonElement b = await DetailAsync(fixture, "UPPER-B");
        Assert.Equal(9, a.GetProperty("orderState").GetInt32());
        Assert.Equal(407801, a.GetProperty("missions")[2].GetProperty("resultCode").GetInt32());
        Assert.Equal(9, b.GetProperty("orderState").GetInt32());
        Assert.Equal(500, b.GetProperty("missions")[2].GetProperty("resultCode").GetInt32());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, cannotBeCannotCharge.StatusCode);
        Assert.Equal("NO_CHARGE", (await ReadVehicleAsync(fixture, VehicleKey)).BatteryState);
    }

    [Fact]
    public async Task NoProgressReportsChargingWhileTheBatteryStaysWhereItWas()
    {
        MovableClock clock = new(Start);
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(clock);
        await RegisterChargerAsync(fixture, enterExit: EnterExit, intervalSeconds: 60, percent: 1);
        await SetBatteryAsync(fixture, VehicleKey, 30);
        await SetFaultsAsync(fixture, new { vehicleKey = VehicleKey, noProgress = true });
        await ChargeAsync(fixture, "UPPER-CHARGE", VehicleKey);

        clock.Advance(TimeSpan.FromHours(1));

        RiotVehicleObservation vehicle = await ReadVehicleAsync(fixture, VehicleKey);
        Assert.Equal(("CHARGING", 30), (vehicle.BatteryState, vehicle.BatteryPercent));
    }

    [Fact]
    public async Task AnUnreadableBatteryLeavesTheFieldOutOfTheCardAndTheGatewayReadsNothing()
    {
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(new MovableClock(Start));

        await SetFaultsAsync(fixture, new { vehicleKey = VehicleKey, batteryUnreadable = "Battery" });
        RiotVehicleObservation noPercent = await ReadVehicleAsync(fixture, VehicleKey);
        string percentCard = await GetStringAsync(fixture, "/api/task/vehicles/getVehicleInfoByDeviceKey?key=" + VehicleKey);
        await SetFaultsAsync(fixture, new { vehicleKey = VehicleKey, batteryUnreadable = "Both" });
        RiotVehicleObservation neither = await ReadVehicleAsync(fixture, VehicleKey);
        await SetFaultsAsync(fixture, new { vehicleKey = VehicleKey, batteryUnreadable = "None" });
        RiotVehicleObservation restored = await ReadVehicleAsync(fixture, VehicleKey);

        Assert.DoesNotContain("\"battery\"", percentCard, StringComparison.Ordinal);
        Assert.Contains("\"batteryState\":\"NO_CHARGE\"", percentCard, StringComparison.Ordinal);
        Assert.Null(noPercent.BatteryPercent);
        Assert.Equal("NO_CHARGE", noPercent.BatteryState);
        Assert.Null(neither.BatteryPercent);
        Assert.Null(neither.BatteryState);
        Assert.Equal((80, "NO_CHARGE"), (restored.BatteryPercent, restored.BatteryState));
    }

    [Fact]
    public async Task ThreeVehiclesAtOneChargerChargeAndFailIndependently()
    {
        MovableClock clock = new(Start);
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(
            clock,
            "--FakeRiot:Seed:AdditionalVehicleKeys:0=" + SecondKey,
            "--FakeRiot:Seed:AdditionalVehicleKeys:1=" + ThirdKey);
        await RegisterChargerAsync(fixture, enterExit: EnterExit, intervalSeconds: 60, percent: 1);
        foreach (string key in new[] { VehicleKey, SecondKey, ThirdKey })
        {
            await SetBatteryAsync(fixture, key, 20);
        }
        await SetFaultsAsync(fixture, new { vehicleKey = SecondKey, noProgress = true });
        await SetFaultsAsync(fixture, new { vehicleKey = ThirdKey, startOutcome = "CannotCharge" });

        await ChargeAsync(fixture, "UPPER-A", VehicleKey);
        RiotVehicleObservation secondWhileFirstCharges = await ReadVehicleAsync(fixture, SecondKey);
        await ChargeAsync(fixture, "UPPER-B", SecondKey);
        await CreateAsync(fixture, "UPPER-C", ThirdKey, Move(Charger), StartCharge());
        await fixture.CommandAsync(HttpMethod.Put, "orders/UPPER-C", new { orderState = 5, executeVehicleKey = ThirdKey });
        clock.Advance(TimeSpan.FromMinutes(10));
        await CreateAsync(fixture, "UPPER-A-LEAVE", VehicleKey, Move(12));
        await fixture.CommandAsync(HttpMethod.Put, "orders/UPPER-A-LEAVE", new { orderState = 3, executeVehicleKey = VehicleKey });
        clock.Advance(TimeSpan.FromMinutes(10));

        RiotVehicleObservation first = await ReadVehicleAsync(fixture, VehicleKey);
        RiotVehicleObservation second = await ReadVehicleAsync(fixture, SecondKey);
        RiotVehicleObservation third = await ReadVehicleAsync(fixture, ThirdKey);
        Assert.Equal(("NO_CHARGE", 20), (secondWhileFirstCharges.BatteryState, secondWhileFirstCharges.BatteryPercent));
        Assert.Equal(("NO_CHARGE", 30), (first.BatteryState, first.BatteryPercent));
        Assert.Equal(("CHARGING", 20), (second.BatteryState, second.BatteryPercent));
        Assert.Equal(("NO_CHARGE", 20), (third.BatteryState, third.BatteryPercent));
        Assert.Equal(9, (await DetailAsync(fixture, "UPPER-C")).GetProperty("orderState").GetInt32());
        // Only the vehicle that left got a departure order's stop act; the others are still where they were.
        await CreateAsync(fixture, "UPPER-B-NEXT", SecondKey, Move(12));
        await CreateAsync(fixture, "UPPER-C-NEXT", ThirdKey, Move(12));
        Assert.Equal(["act:78,2,0", "move:12"], Shape(await DetailAsync(fixture, "UPPER-B-NEXT")));
        Assert.Equal(["move:12"], Shape(await DetailAsync(fixture, "UPPER-C-NEXT")));
    }

    [Fact]
    public async Task TwoVehiclesCreatingChargeOrdersToOneChargerAtOnceEachGetTheirOwnOrder()
    {
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(
            new MovableClock(Start),
            "--FakeRiot:Seed:AdditionalVehicleKeys:0=" + SecondKey);
        await RegisterChargerAsync(fixture, enterExit: EnterExit);

        for (int round = 0; round < 20; round++)
        {
            string first = "UPPER-A-" + round.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string second = "UPPER-B-" + round.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Task.WhenAll(
                CreateAsync(fixture, first, VehicleKey, Move(Charger), StartCharge()),
                CreateAsync(fixture, second, SecondKey, Move(Charger), StartCharge()));

            JsonElement a = await DetailAsync(fixture, first);
            JsonElement b = await DetailAsync(fixture, second);
            Assert.Equal(VehicleKey, a.GetProperty("appointVehicleKey").GetString());
            Assert.Equal(SecondKey, b.GetProperty("appointVehicleKey").GetString());
            Assert.Equal(["move:212", "move:211", "act:78,1,0"], Shape(a));
            Assert.Equal(["move:212", "move:211", "act:78,1,0"], Shape(b));
            Assert.NotEqual(a.GetProperty("id").GetInt64(), b.GetProperty("id").GetInt64());
        }
        Assert.Equal(40, (await fixture.SnapshotAsync()).GetProperty("body").GetProperty("orders").GetArrayLength());
    }

    [Fact]
    public async Task ADischargeRateLowersEveryVehicleOffTheChargerAndAWrittenBatteryOverridesTheSimulation()
    {
        MovableClock clock = new(Start);
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(clock);
        await fixture.CommandAsync(HttpMethod.Put, "chargers", new
        {
            chargers = new object[] { ChargerSpec(Charger, EnterExit, intervalSeconds: 60, percent: 1) },
            dischargeIntervalSeconds = 120,
            dischargePercentPerInterval = 1
        });

        clock.Advance(TimeSpan.FromMinutes(20));
        RiotVehicleObservation drained = await ReadVehicleAsync(fixture, VehicleKey);
        await SetBatteryAsync(fixture, VehicleKey, 40);
        RiotVehicleObservation written = await ReadVehicleAsync(fixture, VehicleKey);
        await ChargeAsync(fixture, "UPPER-CHARGE", VehicleKey);
        clock.Advance(TimeSpan.FromMinutes(10));
        RiotVehicleObservation charging = await ReadVehicleAsync(fixture, VehicleKey);
        await SetBatteryAsync(fixture, VehicleKey, 70);
        clock.Advance(TimeSpan.FromMinutes(5));
        RiotVehicleObservation overridden = await ReadVehicleAsync(fixture, VehicleKey);

        Assert.Equal(("NO_CHARGE", 70), (drained.BatteryState, drained.BatteryPercent));
        Assert.Equal(("NO_CHARGE", 40), (written.BatteryState, written.BatteryPercent));
        Assert.Equal(("CHARGING", 50), (charging.BatteryState, charging.BatteryPercent));
        Assert.Equal(("CHARGING", 75), (overridden.BatteryState, overridden.BatteryPercent));
    }

    [Fact]
    public async Task TheSnapshotCarriesTheChargersAndEachSimulatedVehiclesChargingState()
    {
        MovableClock clock = new(Start);
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync(clock);
        JsonElement empty = (await fixture.SnapshotAsync()).GetProperty("body").GetProperty("charging");
        await RegisterChargerAsync(fixture, enterExit: EnterExit, intervalSeconds: 60, percent: 1);
        await SetBatteryAsync(fixture, VehicleKey, 20);
        await ChargeAsync(fixture, "UPPER-CHARGE", VehicleKey);
        clock.Advance(TimeSpan.FromMinutes(3));

        JsonElement body = (await fixture.SnapshotAsync()).GetProperty("body");
        JsonElement charging = body.GetProperty("charging");
        JsonElement vehicle = charging.GetProperty("vehicles")[0];

        Assert.Equal(0, empty.GetProperty("chargers").GetArrayLength());
        Assert.Equal(0, empty.GetProperty("vehicles").GetArrayLength());
        Assert.Equal(Charger, charging.GetProperty("chargers")[0].GetProperty("stationId").GetInt32());
        Assert.Equal(EnterExit, charging.GetProperty("chargers")[0].GetProperty("enterExitStationId").GetInt32());
        Assert.Equal(VehicleKey, vehicle.GetProperty("vehicleKey").GetString());
        Assert.Equal("CHARGING", vehicle.GetProperty("batteryState").GetString());
        Assert.Equal(23, vehicle.GetProperty("battery").GetInt32());
        Assert.True(vehicle.GetProperty("docked").GetBoolean());
        Assert.Equal(Charger, vehicle.GetProperty("chargerStationId").GetInt32());
        // The vehicle list reports the same simulated reading as the card does.
        JsonElement listed = body.GetProperty("vehicles")[0];
        Assert.Equal(23, listed.GetProperty("battery").GetInt32());
        Assert.Equal("CHARGING", listed.GetProperty("batteryState").GetString());
    }

    [Fact]
    public async Task ChargingControlCommandsRefuseWhatTheyCannotMean()
    {
        await using FakeRiotTests.FakeRiotFixture fixture = await FakeRiotTests.FakeRiotFixture.StartAsync();

        HttpResponseMessage[] refused =
        [
            await fixture.SendCommandAsync(HttpMethod.Put, "chargers", new { }),
            await fixture.SendCommandAsync(HttpMethod.Put, "chargers", new
            {
                chargers = new object[] { new { mapId = 25, stationId = Charger, chargeIntervalSeconds = 0, chargePercentPerInterval = 1 } }
            }),
            await fixture.SendCommandAsync(HttpMethod.Put, "chargers", new
            {
                chargers = new object[] { ChargerSpec(Charger, EnterExit), ChargerSpec(Charger, null) }
            }),
            await fixture.SendCommandAsync(HttpMethod.Put, "chargers", new
            {
                chargers = Array.Empty<object>(),
                dischargeIntervalSeconds = 60
            }),
            await fixture.SendCommandAsync(HttpMethod.Put, "charging/faults", new { noProgress = true }),
            await fixture.SendCommandAsync(HttpMethod.Put, "charging/faults", new
            {
                vehicleKey = VehicleKey, upperId = "UPPER-X", startOutcome = "CannotCharge"
            }),
            await fixture.SendCommandAsync(HttpMethod.Put, "charging/faults", new { upperId = "UPPER-X", noProgress = true }),
            await fixture.SendCommandAsync(HttpMethod.Put, "charging/faults", new { vehicleKey = VehicleKey, interruptAtPercent = 101 })
        ];
        HttpResponseMessage unknownVehicle = await fixture.SendCommandAsync(
            HttpMethod.Put, "charging/faults", new { vehicleKey = "BROKERX-NOBODY", noProgress = true });

        Assert.All(refused, response => Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode));
        Assert.Equal(System.Net.HttpStatusCode.Conflict, unknownVehicle.StatusCode);
        Assert.Equal(0, (await fixture.SnapshotAsync()).GetProperty("body").GetProperty("charging")
            .GetProperty("vehicles").GetArrayLength());
    }

    private static object Move(int destination) => new { type = "move", mapId = 25, destination };

    private static object StartCharge() => new { type = "act", actionId = 78, actionParam1 = 1, actionParam2 = 0 };

    private static object ChargerSpec(
        int stationId, int? enterExit, int intervalSeconds = 60, int percent = 1, bool expandDeparture = false) => new
        {
            mapId = 25,
            stationId,
            enterExitStationId = enterExit,
            chargeIntervalSeconds = intervalSeconds,
            chargePercentPerInterval = percent,
            expandDeparture
        };

    private static Task<JsonElement> RegisterChargerAsync(
        FakeRiotTests.FakeRiotFixture fixture, int? enterExit, int intervalSeconds = 60, int percent = 1) =>
        fixture.CommandAsync(HttpMethod.Put, "chargers", new
        {
            chargers = new object[] { ChargerSpec(Charger, enterExit, intervalSeconds, percent) }
        });

    private static Task<JsonElement> SetBatteryAsync(FakeRiotTests.FakeRiotFixture fixture, string vehicleKey, int battery) =>
        fixture.CommandAsync(HttpMethod.Put, "vehicle", new { vehicleKey, battery });

    private static Task<JsonElement> SetFaultsAsync(FakeRiotTests.FakeRiotFixture fixture, object body) =>
        fixture.CommandAsync(HttpMethod.Put, "charging/faults", body);

    private static Task<JsonElement> CompleteAsync(FakeRiotTests.FakeRiotFixture fixture, string upperId) =>
        fixture.CommandAsync(HttpMethod.Put, "orders/" + upperId, new { orderState = 5 });

    /// <summary>Creates a charge order to the charger for one vehicle and completes it.</summary>
    private static async Task ChargeAsync(FakeRiotTests.FakeRiotFixture fixture, string upperId, string vehicleKey)
    {
        await CreateAsync(fixture, upperId, vehicleKey, Move(Charger), StartCharge());
        await fixture.CommandAsync(HttpMethod.Put, "orders/" + upperId, new { orderState = 5, executeVehicleKey = vehicleKey });
    }

    private static async Task<JsonElement> CreateAsync(
        FakeRiotTests.FakeRiotFixture fixture, string upperId, string vehicleKey, params object[] missions)
    {
        HttpResponseMessage response = await fixture.Client.PostAsync(
            new Uri("/api/order/v1/add/byDefaultMissions", UriKind.Relative),
            JsonContent.Create(new { upperId, appointVehicleKey = vehicleKey, mission = missions }),
            TestContext.Current.CancellationToken);
        string json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, json);
        JsonElement root = JsonDocument.Parse(json).RootElement.Clone();
        Assert.Equal("0", root.GetProperty("code").GetString());
        return root;
    }

    private static async Task<JsonElement> DetailAsync(FakeRiotTests.FakeRiotFixture fixture, string upperId) =>
        JsonDocument.Parse(await GetStringAsync(fixture, "/api/order/v1/orderRecord/detailByUpperId/" + upperId))
            .RootElement.GetProperty("result").Clone();

    private static Task<string> GetStringAsync(FakeRiotTests.FakeRiotFixture fixture, string path) =>
        fixture.Client.GetStringAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

    private static Task<RiotVehicleObservation> ReadVehicleAsync(FakeRiotTests.FakeRiotFixture fixture, string vehicleKey) =>
        fixture.Gateway().ReadVehicleAsync(vehicleKey, TestContext.Current.CancellationToken);

    private static string[] Shape(JsonElement order) =>
    [
        .. order.GetProperty("missions").EnumerateArray().Select(mission => mission.GetProperty("type").GetString() == "act"
            ? "act:" + mission.GetProperty("actionId").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
              mission.GetProperty("actionParam1").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
              mission.GetProperty("actionParam2").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "move:" + mission.GetProperty("destination").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture))
    ];

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

    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private readonly object gate = new();
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate)
            {
                return now;
            }
        }

        public void Advance(TimeSpan by)
        {
            lock (gate)
            {
                now += by;
            }
        }
    }
}
