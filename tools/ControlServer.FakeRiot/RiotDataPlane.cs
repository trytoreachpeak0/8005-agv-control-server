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
            return Ok(stations.Select(station => new { id = station.Id, name = station.Name }).ToArray());
        });

        app.MapGet("/api/order/v1/orderRecord/detailByUpperId/{upperId}", async (
            string upperId, CancellationToken cancellationToken) =>
        {
            IResult? fault = await ApplyFaultAsync(engine, cancellationToken).ConfigureAwait(false);
            if (fault is not null) return fault;
            FakeRiotState state = engine.Snapshot().State;
            return state.OrdersByUpperId.TryGetValue(upperId, out FakeOrder? order)
                ? Ok(OrderBody(order))
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
            FakeMission[] missions = root.TryGetProperty("mission", out JsonElement missionArray)
                ? missionArray.EnumerateArray().Select(item => new FakeMission(
                    item.GetProperty("type").GetString() ?? "move",
                    item.GetProperty("mapId").GetInt32(),
                    item.GetProperty("destination").GetInt32())).ToArray()
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
            FakeOrder order = new()
            {
                Id = 488000 + sequence,
                OrderId = "ORDER-" + sequence.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
                UpperId = upperId,
                OrderState = 1,
                AppointVehicleKey = appointVehicleKey,
                ExecuteVehicleKey = "--",
                EndStationNo = missions.Count > 0 ? missions[missions.Count - 1].Destination : null,
                Missions = missions
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
            .Select(mission => new { type = mission.Type, mapId = mission.MapId, destination = mission.Destination })
            .ToArray()
    };

    private static IResult Ok(object? result) => Results.Json(new { code = "0", message = "成功", result });

    /// <summary>
    /// Injects the configured data-plane fault. NoResponse holds the request until the client gives
    /// up, which is how a read timeout is produced without a real network; the control server must
    /// answer RIOT_READ_TIMEOUT and fail closed rather than treat silence as safe.
    /// </summary>
    private static async Task<IResult?> ApplyFaultAsync(
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
