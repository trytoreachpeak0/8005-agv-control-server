using System.Text.Json.Serialization;
using ControlServer.TestDoubles;

namespace ControlServer.FakeRiot;

/// <summary>
/// Drives the vehicle. Every field is optional and only the ones present are applied, so a
/// scenario can say "it is moving now" without restating battery, map and station each time.
/// </summary>
public sealed record VehicleCommand : CommandEnvelope
{
    public string? VehicleKey { get; init; }
    public bool? Enable { get; init; }
    public int? Status { get; init; }
    public string? ProcState { get; init; }
    public string? CurrentMap { get; init; }
    public int? CurrentPosition { get; init; }
    public int? Battery { get; init; }
    public string? BatteryState { get; init; }
    public double? Speed { get; init; }
    public int? LockStatus { get; init; }
    public string? OrderTaskId { get; init; }
    public bool ClearOrderTaskId { get; init; }
    public string? MovementState { get; init; }
    public string? ControlState { get; init; }
    public string? EmergencyState { get; init; }
    public string? BreakSwitchState { get; init; }
    public string? LocationState { get; init; }
    public bool? ProcessingOrder { get; init; }
    public string? IntegrationLevel { get; init; }
}

/// <summary>
/// Moves one order along. <c>ExecuteVehicleKey</c> is how a scenario simulates RIoT binding a
/// vehicle to a QUEUEING order, which is a distinct event from the order starting to execute.
/// </summary>
public sealed record OrderCommand : CommandEnvelope
{
    public int? OrderState { get; init; }
    public string? ExecuteVehicleKey { get; init; }
    public int? EndStationNo { get; init; }
}

public sealed record StationsCommand : CommandEnvelope
{
    public Dictionary<string, string>? Stations { get; init; }
}

public sealed record FaultCommand : CommandEnvelope
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FakeRiotFaultMode? Mode { get; init; }
    public int? DelayMs { get; init; }
}

/// <summary>
/// The loopback control plane. It can only say what RIoT <em>observes</em>; it offers no way to
/// move the vehicle or complete an order, because dispatching is the control server's job and a
/// scenario that could stage the end state would be testing itself.
/// </summary>
public static class ControlPlane
{
    public static void MapControlPlane(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeRiotState> engine = app.Services.GetRequiredService<CommandEngine<FakeRiotState>>();
        RouteGroupBuilder control = app.MapGroup("/control/v1");

        control.MapGet("/openapi.json", ControlPlaneConventions.OpenApiDocument);

        control.MapGet("/health", () => Results.Json(ControlPlaneConventions.Envelope(engine, new
        {
            status = "live",
            faultMode = engine.Snapshot().State.FaultMode.ToString()
        })));

        control.MapGet("/snapshot", () =>
        {
            FakeRiotState state = engine.Snapshot().State;
            return Results.Json(ControlPlaneConventions.Envelope(engine, new
            {
                faultMode = state.FaultMode.ToString(),
                delayMs = state.DelayMs,
                vehicles = state.Vehicles.Values.OrderBy(item => item.DeviceKey, StringComparer.Ordinal),
                orders = state.OrdersByUpperId.Values.OrderBy(item => item.Id),
                maps = state.StationsByMapId.OrderBy(pair => pair.Key)
                    .Select(pair => new { mapId = pair.Key, stations = pair.Value })
            }));
        });

        control.MapPost("/reset", (CommandEnvelope command) =>
        {
            if (string.IsNullOrWhiteSpace(command.CommandId))
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.InvalidArgument, null);
            }
            CommandOutcome outcome = engine.Reset(command.RunId, command.CommandId, command.ExpectedRevision);
            return ControlPlaneConventions.Respond(engine, command.CommandId, outcome);
        });

        control.MapPut("/vehicle", (VehicleCommand command) =>
            ControlPlaneConventions.Handle(engine, "vehicle", command, state =>
            {
                string key = command.VehicleKey ?? state.Vehicles.Keys.FirstOrDefault()
                    ?? throw new CommandRefusedException(ReasonCodes.NotFound);
                if (!state.Vehicles.TryGetValue(key, out FakeVehicle? vehicle))
                {
                    throw new CommandRefusedException(ReasonCodes.NotFound);
                }
                FakeVehicle updated = vehicle with
                {
                    Enable = command.Enable ?? vehicle.Enable,
                    Status = command.Status ?? vehicle.Status,
                    ProcState = command.ProcState ?? vehicle.ProcState,
                    CurrentMap = command.CurrentMap ?? vehicle.CurrentMap,
                    CurrentPosition = command.CurrentPosition ?? vehicle.CurrentPosition,
                    Battery = command.Battery ?? vehicle.Battery,
                    BatteryState = command.BatteryState ?? vehicle.BatteryState,
                    Speed = command.Speed ?? vehicle.Speed,
                    LockStatus = command.LockStatus ?? vehicle.LockStatus,
                    OrderTaskId = command.ClearOrderTaskId ? null : command.OrderTaskId ?? vehicle.OrderTaskId,
                    MovementState = command.MovementState ?? vehicle.MovementState,
                    ControlState = command.ControlState ?? vehicle.ControlState,
                    EmergencyState = command.EmergencyState ?? vehicle.EmergencyState,
                    BreakSwitchState = command.BreakSwitchState ?? vehicle.BreakSwitchState,
                    LocationState = command.LocationState ?? vehicle.LocationState,
                    ProcessingOrder = command.ProcessingOrder ?? vehicle.ProcessingOrder,
                    IntegrationLevel = command.IntegrationLevel ?? vehicle.IntegrationLevel
                };
                if (updated == vehicle)
                {
                    return null;
                }
                Dictionary<string, FakeVehicle> vehicles = new(state.Vehicles, StringComparer.Ordinal)
                {
                    [key] = updated
                };
                return state with { Vehicles = vehicles };
            }));

        control.MapPut("/orders/{upperId}", (string upperId, OrderCommand command) =>
            ControlPlaneConventions.Handle(engine, "order:" + upperId, command, state =>
            {
                if (!state.OrdersByUpperId.TryGetValue(upperId, out FakeOrder? order))
                {
                    throw new CommandRefusedException(ReasonCodes.NotFound);
                }
                if (command.OrderState is { } requested && requested is < 1 or > 9)
                {
                    throw new CommandRefusedException(ReasonCodes.InvalidArgument);
                }
                FakeOrder updated = order with
                {
                    OrderState = command.OrderState ?? order.OrderState,
                    ExecuteVehicleKey = command.ExecuteVehicleKey ?? order.ExecuteVehicleKey,
                    EndStationNo = command.EndStationNo ?? order.EndStationNo
                };
                if (updated == order)
                {
                    return null;
                }
                Dictionary<string, FakeOrder> orders = new(state.OrdersByUpperId, StringComparer.Ordinal)
                {
                    [upperId] = updated
                };
                return state with { OrdersByUpperId = orders };
            }));

        control.MapPut("/maps/{mapId:int}/stations", (int mapId, StationsCommand command) =>
            ControlPlaneConventions.Handle(
                engine,
                "stations:" + mapId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                command,
                state =>
                {
                    if (command.Stations is null)
                    {
                        throw new CommandRefusedException(ReasonCodes.InvalidArgument);
                    }
                    FakeStation[] stations;
                    try
                    {
                        stations = command.Stations
                            .Select(pair => new FakeStation(
                                int.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture),
                                pair.Value))
                            .OrderBy(station => station.Id)
                            .ToArray();
                    }
                    catch (FormatException error)
                    {
                        throw new CommandRefusedException(ReasonCodes.InvalidArgument, error);
                    }
                    if (state.StationsByMapId.TryGetValue(mapId, out IReadOnlyList<FakeStation>? existing) &&
                        existing.SequenceEqual(stations))
                    {
                        return null;
                    }
                    Dictionary<int, IReadOnlyList<FakeStation>> maps = new(state.StationsByMapId)
                    {
                        [mapId] = stations
                    };
                    return state with { StationsByMapId = maps };
                }));

        control.MapPut("/faults/http", (FaultCommand command) =>
            ControlPlaneConventions.Handle(engine, "fault", command, state =>
            {
                FakeRiotFaultMode mode = command.Mode
                    ?? throw new CommandRefusedException(ReasonCodes.InvalidArgument);
                int delay = command.DelayMs ?? state.DelayMs;
                if (mode == FakeRiotFaultMode.Delay && delay is < 1 or > 60000)
                {
                    throw new CommandRefusedException(ReasonCodes.InvalidArgument);
                }
                return state.FaultMode == mode && state.DelayMs == delay
                    ? null
                    : state with { FaultMode = mode, DelayMs = delay };
            }));
    }
}
