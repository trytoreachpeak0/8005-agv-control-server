using System.Text.Json;
using ControlServer.TestDoubles;
using Microsoft.AspNetCore.Mvc;

namespace ControlServer.FakeRiot;

/// <summary>
/// The six RIoT endpoints ControlServer's SDK actually calls. Response shapes are the ones
/// HttpRiotMovementGatewayTests pins, which are in turn the shapes captured from the real RCS on
/// 2026-09-03. Two of them are irregular in ways worth keeping: the execution-facts endpoint has
/// no <c>code</c> envelope at all, and a QUEUEING order reports "--" in executeVehicleKey.
/// </summary>
public static class RiotDataPlane
{
    public static void MapRiotDataPlane(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeRiotState> engine = app.Services.GetRequiredService<CommandEngine<FakeRiotState>>();
        MapStationReadCounter mapStationReads = app.Services.GetRequiredService<MapStationReadCounter>();
        MapListReadCounter mapListReads = app.Services.GetRequiredService<MapListReadCounter>();
        AbsentOrderReadFaults absentOrderReadFaults = app.Services.GetRequiredService<AbsentOrderReadFaults>();
        TimeProvider clock = app.Services.GetRequiredService<TimeProvider>();

        app.MapGet("/api/task/vehicles/getVehicleInfoByDeviceKey", async (
            [FromQuery] string key, CancellationToken cancellationToken) =>
        {
            IResult? fault = await ApplyFaultAsync(engine, cancellationToken).ConfigureAwait(false);
            if (fault is not null) return fault;
            FakeRiotState state = engine.Snapshot().State;
            if (!state.Vehicles.TryGetValue(key, out FakeVehicle? vehicle))
            {
                // RIoT answers a business success with an empty result rather than 404 here. The
                // gateway turns that into fail-closed UNKNOWN facts, which is what a scenario
                // asking for an unknown vehicle should be able to exercise.
                return Ok(null);
            }
            vehicle = FakeChargingModel.Effective(state, vehicle, clock.GetUtcNow());
            if (state.ChargeByVehicle.TryGetValue(key, out FakeVehicleCharge? charge) &&
                charge.BatteryUnreadable != FakeBatteryUnreadable.None)
            {
                return Ok(CardWithout(vehicle, charge.BatteryUnreadable));
            }
            return Ok(new
            {
                deviceKey = vehicle.DeviceKey,
                enable = vehicle.Enable,
                status = vehicle.Status,
                procState = vehicle.ProcState,
                currentMap = vehicle.CurrentMap,
                currentPosition = vehicle.CurrentPosition,
                battery = vehicle.Battery,
                batteryState = vehicle.BatteryState,
                speed = vehicle.Speed,
                lockStatus = vehicle.LockStatus,
                orderTaskId = vehicle.OrderTaskId
            });
        });

        app.MapGet("/api/task/v1/task/getVehicleInfo/{deviceKey}", async (
            string deviceKey, CancellationToken cancellationToken) =>
        {
            IResult? fault = await ApplyFaultAsync(engine, cancellationToken).ConfigureAwait(false);
            if (fault is not null) return fault;
            FakeRiotState state = engine.Snapshot().State;
            if (!state.Vehicles.TryGetValue(deviceKey, out FakeVehicle? vehicle))
            {
                return Results.NotFound();
            }
            // No code/result envelope on this one. That is not an oversight in the fake: the real
            // endpoint returns the bare object and the SDK parses it that way.
            return Results.Json(new
            {
                vehicle = new
                {
                    movementState = vehicle.MovementState,
                    controlState = vehicle.ControlState,
                    emergencyState = vehicle.EmergencyState,
                    breakSwitchState = vehicle.BreakSwitchState,
                    locationState = vehicle.LocationState,
                    speed = vehicle.Speed
                },
                vehicleTaskInfo = new
                {
                    key = vehicle.DeviceKey,
                    procState = vehicle.ProcState,
                    processingOrder = vehicle.ProcessingOrder,
                    enable = vehicle.Enable,
                    integrationLevel = vehicle.IntegrationLevel
                }
            });
        });

        // control-server#186: the Map list without any mapJson, in the shape the real RIoT answered on 2026-09-28
        // (evidence/field/2026-09-28-cs186-map-list-endpoint-check): id and name, plus the metadata it carries.
        app.MapGet("/api/imap/v1/mapInfo/getALLMapInfoExcludeMapJson", async (CancellationToken cancellationToken) =>
        {
            // Counted before any fault, like the station reads: a scenario proving "the list could not be read and nothing
            // was held" must first prove the list was asked for, and failed, in that window.
            mapListReads.Read();
            IResult? fault = await ApplyFaultAsync(engine, cancellationToken).ConfigureAwait(false);
            if (fault is not null) return fault;
            FakeRiotState state = engine.Snapshot().State;
            if (state.MapListServerError)
            {
                mapListReads.Failed();
                return Results.Json(new { code = "500", message = "失败" }, statusCode: StatusCodes.Status500InternalServerError);
            }
            return Ok(state.MapNamesByMapId.OrderBy(pair => pair.Key).Select(pair => new
            {
                id = pair.Key,
                name = pair.Value,
                description = (string?)null,
                floor = 1,
                mapError = (string?)null,
                source = "upload",
                state = "activated",
                syncState = "synced"
            }).ToArray());
        });

        app.MapGet("/api/imap/v1/mapInfo/stations/{mapId:int}", async (
            int mapId, CancellationToken cancellationToken) =>
        {
            // Counted before the fault gate, because a scenario waiting on iterations wants to know
            // the runtime came round again even when the answer it got was an injected failure.
            mapStationReads.Increment();
            IResult? fault = await ApplyFaultAsync(engine, cancellationToken).ConfigureAwait(false);
            if (fault is not null) return fault;
            FakeRiotState state = engine.Snapshot().State;
            if (!state.StationsByMapId.TryGetValue(mapId, out IReadOnlyList<FakeStation>? stations))
            {
                return Ok(Array.Empty<object>());
            }
            // The real endpoint returns one row carrying both views, and two of this shape's
            // quirks are load bearing: the keys are snake_case, and the position keys contain a
            // literal dot. An anonymous type cannot express "pos.x", hence the dictionary.
            return Ok(stations.Select(station => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = station.Id,
                ["name"] = station.Name,
                ["edge_id"] = station.EdgeId,
                ["pos.x"] = station.PosX,
                ["pos.y"] = station.PosY,
                ["pos.yaw"] = station.PosYaw,
                ["station_offset"] = station.StationOffset,
                ["type"] = station.Type,
                ["desc"] = "",
                ["user_define_properties"] = UserDefineProperties(state, mapId, station.Id),
            }).ToArray());
        });

        app.MapGet("/api/order/v1/orderRecord/detailByUpperId/{upperId}", async (
            string upperId, CancellationToken cancellationToken) =>
        {
            IResult? fault = await ApplyFaultAsync(engine, cancellationToken).ConfigureAwait(false);
            if (fault is not null) return fault;
            FakeRiotState state = engine.Snapshot().State;
            if (state.OrdersByUpperId.TryGetValue(upperId, out FakeOrder? order))
            {
                return Ok(OrderBody(order));
            }
            return absentOrderReadFaults.TryConsume(upperId)
                ? Results.Json(new { }, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.NotFound();
        });

        app.MapGet("/api/order/v1/orderRecord", async (
            HttpRequest request, CancellationToken cancellationToken) =>
        {
            IResult? fault = await ApplyFaultAsync(engine, cancellationToken).ConfigureAwait(false);
            if (fault is not null) return fault;
            int[] states = request.Query["filterByState"]
                .Where(value => int.TryParse(value, out _))
                .Select(value => int.Parse(value!, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            int size = int.TryParse(request.Query["pageSize"], out int parsedSize) && parsedSize > 0
                ? parsedSize
                : 100;
            FakeRiotState state = engine.Snapshot().State;
            object[] records = state.OrdersByUpperId.Values
                .Where(order => states.Length == 0 || states.Contains(order.OrderState))
                .OrderBy(order => order.Id)
                .Select(OrderBody)
                .ToArray();
            // total is what the gateway checks its page coverage against: reporting more than this
            // page carries makes it answer RIOT_NONFINAL_ORDER_COVERAGE_UNKNOWN, so it must be the
            // honest total and not the page length.
            return Ok(new
            {
                current = 1,
                size,
                total = records.Length,
                records = records.Take(size).ToArray()
            });
        });

        app.MapPost("/api/order/v1/add/byDefaultMissions", async (
            HttpRequest request, CancellationToken cancellationToken) =>
        {
            IResult? fault = await ApplyFaultAsync(engine, cancellationToken).ConfigureAwait(false);
            if (fault is not null) return fault;
            using JsonDocument document = await JsonDocument
                .ParseAsync(request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("upperId", out JsonElement upperIdElement) ||
                upperIdElement.GetString() is not { Length: > 0 } upperId)
            {
                return Results.Json(new { code = "0610001", message = "upperId is required" });
            }
            string? appointVehicleKey = root.TryGetProperty("appointVehicleKey", out JsonElement appointed)
                ? appointed.GetString()
                : null;
            // An act mission carries no mapId or destination (control-server#402); RIoT reports both as 0 for it.
            FakeMission[] missions = root.TryGetProperty("mission", out JsonElement missionArray)
                ? missionArray.EnumerateArray().Select(item => new FakeMission(
                    item.GetProperty("type").GetString() ?? "move",
                    OptionalInt(item, "mapId"),
                    OptionalInt(item, "destination"))
                {
                    ActionId = OptionalInt(item, "actionId"),
                    ActionParam1 = OptionalInt(item, "actionParam1"),
                    ActionParam2 = OptionalInt(item, "actionParam2")
                }).ToArray()
                : [];
            FakeOrder? created = CreateOrder(engine, upperId, appointVehicleKey, missions);
            return created is null
                // BC-ORDER-004. The control server treats this exact code as "already exists" and
                // reconciles instead of retrying, so it must be the code and not a 409.
                ? Results.Json(new { code = "0610008", message = "订单已存在" })
                : Ok(new
                {
                    id = created.Id,
                    orderId = created.OrderId,
                    upperId = created.UpperId,
                    orderState = created.OrderState
                });
        });
    }

    /// <summary>
    /// RIoT deduplicates a create on upperId rather than on a caller command id, so this does not
    /// go through the control plane's receipt table. Returns null when the upperId is already taken.
    /// </summary>
    private static FakeOrder? CreateOrder(
        CommandEngine<FakeRiotState> engine,
        string upperId,
        string? appointVehicleKey,
        IReadOnlyList<FakeMission> missions) =>
        engine.Mutate<FakeOrder?>(state =>
        {
            if (state.OrdersByUpperId.ContainsKey(upperId))
            {
                return (null, null);
            }
            long sequence = state.NextOrderSequence;
            IReadOnlyList<FakeMission> recorded = FakeChargingModel.Expand(state, appointVehicleKey, missions);
            FakeOrder order = new()
            {
                Id = 488000 + sequence,
                OrderId = "ORDER-" + sequence.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
                UpperId = upperId,
                OrderState = 1,
                AppointVehicleKey = appointVehicleKey,
                ExecuteVehicleKey = "--",
                // The last move's station: a trailing act has destination 0, and 0 is not where the vehicle is sent.
                EndStationNo = recorded.LastOrDefault(mission => mission.Type == "move")?.Destination,
                Missions = recorded
            };
            Dictionary<string, FakeOrder> orders = new(state.OrdersByUpperId, StringComparer.Ordinal)
            {
                [upperId] = order
            };
            return (state with { OrdersByUpperId = orders, NextOrderSequence = sequence + 1 }, order);
        });

    private static object OrderBody(FakeOrder order) => new
    {
        id = order.Id,
        orderId = order.OrderId,
        upperId = order.UpperId,
        orderState = order.OrderState,
        appointVehicleKey = order.AppointVehicleKey,
        executeVehicleKey = order.ExecuteVehicleKey,
        endStationNo = order.EndStationNo,
        missions = order.Missions
            .Select(mission => mission.Type == "act"
                ? ActBody(mission)
                : (object)new { type = mission.Type, mapId = mission.MapId, destination = mission.Destination })
            .ToArray()
    };

    /// <summary>
    /// An act mission under the field names the real RIoT uses (Round 24 <c>S1b-detail-final.json</c>). A move keeps the
    /// three fields it always had, so an order with no act is answered byte for byte as before control-server#402.
    /// </summary>
    private static object ActBody(FakeMission mission) => new
    {
        type = mission.Type,
        mapId = mission.MapId,
        destination = mission.Destination,
        actionId = mission.ActionId,
        actionParam1 = mission.ActionParam1,
        actionParam2 = mission.ActionParam2,
        resultCode = mission.ResultCode,
        resultStr = mission.ResultStr,
        missionState = mission.MissionState
    };

    /// <summary>
    /// The vehicle card with the battery reading missing (control-server#402, REQ-0287). The keys are left out rather
    /// than sent as null: "the card said nothing" is the loss being simulated.
    /// </summary>
    private static Dictionary<string, object?> CardWithout(FakeVehicle vehicle, FakeBatteryUnreadable unreadable)
    {
        Dictionary<string, object?> card = new(StringComparer.Ordinal)
        {
            ["deviceKey"] = vehicle.DeviceKey,
            ["enable"] = vehicle.Enable,
            ["status"] = vehicle.Status,
            ["procState"] = vehicle.ProcState,
            ["currentMap"] = vehicle.CurrentMap,
            ["currentPosition"] = vehicle.CurrentPosition,
            ["battery"] = vehicle.Battery,
            ["batteryState"] = vehicle.BatteryState,
            ["speed"] = vehicle.Speed,
            ["lockStatus"] = vehicle.LockStatus,
            ["orderTaskId"] = vehicle.OrderTaskId
        };
        if (unreadable is FakeBatteryUnreadable.Battery or FakeBatteryUnreadable.Both)
        {
            card.Remove("battery");
        }
        if (unreadable is FakeBatteryUnreadable.BatteryState or FakeBatteryUnreadable.Both)
        {
            card.Remove("batteryState");
        }
        return card;
    }

    /// <summary>
    /// A registered charger's enter/exit station, in the shape map 26 reports for 211: <c>{"enter_exit":"212"}</c>, the
    /// value a string. Empty for every other station, which is every station until a scenario registers a charger.
    /// </summary>
    private static Dictionary<string, object?> UserDefineProperties(FakeRiotState state, int mapId, int stationId)
    {
        Dictionary<string, object?> properties = new(StringComparer.Ordinal);
        if (FakeChargingModel.FindCharger(state, mapId, stationId) is { EnterExitStationId: int enterExit })
        {
            properties["enter_exit"] = enterExit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return properties;
    }

    private static int OptionalInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    internal static IResult Ok(object? result) => Results.Json(new { code = "0", message = "成功", result });

    /// <summary>
    /// Injects the configured data-plane fault. NoResponse holds the request until the client gives
    /// up, which is how a read timeout is produced without a real network; the control server must
    /// answer RIOT_READ_TIMEOUT and fail closed rather than treat silence as safe.
    /// </summary>
    internal static async Task<IResult?> ApplyFaultAsync(
        CommandEngine<FakeRiotState> engine,
        CancellationToken cancellationToken)
    {
        FakeRiotState state = engine.Snapshot().State;
        switch (state.FaultMode)
        {
            case FakeRiotFaultMode.NoResponse:
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return Results.Empty;
            case FakeRiotFaultMode.ServerError:
                return Results.Json(new { }, statusCode: StatusCodes.Status503ServiceUnavailable);
            case FakeRiotFaultMode.Delay:
                await Task.Delay(state.DelayMs, cancellationToken).ConfigureAwait(false);
                return null;
            case FakeRiotFaultMode.Normal:
            default:
                return null;
        }
    }
}

/// <summary>
/// The next <c>n</c> reads by upperId of an order RIoT does not have answer 503 instead of 404, once each
/// (control-server#375): the read before a create that answers nothing, aimed at that read alone. The global fault mode
/// would fail every read on the data plane at once, the vehicle's included, and on a timing a scenario cannot pin.
/// </summary>
/// <remarks>
/// Outside the command engine for the reason <see cref="MapStationReadCounter"/> is. The upperIds it failed are kept so a
/// scenario can show which read it hit: a budget spent on some other read would otherwise pass for the one it meant.
/// </remarks>
public sealed class AbsentOrderReadFaults
{
    private readonly object gate = new();
    private readonly List<string> failed = [];
    private int remaining;

    public void Arm(int count)
    {
        lock (gate)
        {
            remaining = count;
        }
    }

    public bool TryConsume(string upperId)
    {
        lock (gate)
        {
            if (remaining <= 0)
            {
                return false;
            }

            remaining--;
            failed.Add(upperId);
            return true;
        }
    }

    public object Describe()
    {
        lock (gate)
        {
            return new { remaining, failedUpperIds = failed.ToArray() };
        }
    }
}

/// <summary>
/// Counts reads of the Map station catalog, which JourneyRuntimeEngine.ExecuteOnceAsync performs
/// first thing on every iteration -- including the iterations where a Blocked journey makes it do
/// nothing else. That makes this the one observable a scenario can use to say "the runtime has had
/// N more chances and still did not do it", which is what turns a negative assertion into a
/// predicate with a deadline instead of a sleep.
/// </summary>
/// <remarks>
/// Deliberately outside the command engine: a counter that moved the state revision on every poll
/// would make expectedRevision useless for the commands that carry real changes. The wire log in
/// ControlServer.FakeOnboard sits outside for the same reason.
/// </remarks>
public sealed class MapStationReadCounter
{
    private long count;

    public long Count => Interlocked.Read(ref count);

    public void Increment() => Interlocked.Increment(ref count);
}

/// <summary>
/// Counts Map list reads and the ones answered 500 (control-server#186). Outside the command engine for the same reason as
/// <see cref="MapStationReadCounter"/>: a counter that moved the state revision on every poll would make expectedRevision
/// useless for the commands that carry real changes.
/// </summary>
public sealed class MapListReadCounter
{
    private long reads;
    private long serverErrors;

    /// <summary>Every request for the Map list, answered or not (control-server#186).</summary>
    public long Reads => Interlocked.Read(ref reads);

    /// <summary>The requests <see cref="FakeRiotState.MapListServerError"/> answered with 500.</summary>
    public long ServerErrors => Interlocked.Read(ref serverErrors);

    public void Read() => Interlocked.Increment(ref reads);

    public void Failed() => Interlocked.Increment(ref serverErrors);
}
