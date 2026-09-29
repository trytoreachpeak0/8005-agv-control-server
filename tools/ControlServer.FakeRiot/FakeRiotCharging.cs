using System.Text.Json.Serialization;

namespace ControlServer.FakeRiot;

/// <summary>
/// One charger a scenario registered (control-server#402). The real RIoT has no charger station type -- every station on
/// map 25 and map 26 is <c>type=1</c> -- so a charger exists here only because a scenario said so, exactly as it exists on
/// the server only because the charger roster says so.
/// </summary>
/// <param name="EnterExitStationId">
/// The station's <c>user_define_properties.enter_exit</c>. On map 26, station 211 「充电点1」 carries <c>"212"</c>, and RIoT
/// then expands a charge order into <c>move(212) → move(211) → act(78)</c> (MVP defect record 2026-09-12, Round 25).
/// </param>
/// <param name="ChargeIntervalSeconds">Battery rises by <paramref name="ChargePercentPerInterval"/> once per this many seconds.</param>
/// <param name="ExpandDeparture">
/// Whether the order that takes a vehicle off this charger is also routed through the enter/exit station. Off by default:
/// nobody has measured the shape of the real departure order, only that RIoT puts <c>act(78,2,0)</c> at its head.
/// </param>
public sealed record FakeCharger(
    int MapId,
    int StationId,
    int? EnterExitStationId,
    int ChargeIntervalSeconds,
    int ChargePercentPerInterval,
    bool ExpandDeparture);

/// <summary>What a start-charging act turns into when its order is pushed to 5 (control-server#402).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FakeChargeStartOutcome
{
    /// <summary>The act completes, the order reaches 5 and the vehicle reports CHARGING.</summary>
    Normal,

    /// <summary>
    /// REQ-0174: the act answers <c>resultCode = 407802</c> and the order stays at 9 (HANG), never CHARGING -- the shape
    /// Round 24 recorded for a charger that would not engage.
    /// </summary>
    CannotCharge,

    /// <summary>REQ-0175's negative: the order stays at 9 and the act carries no 407802.</summary>
    HangOnly
}

/// <summary>A start-charging outcome set on one order, taking precedence over its vehicle's (control-server#402).</summary>
public sealed record FakeOrderChargeFault(FakeChargeStartOutcome StartOutcome, int? HangResultCode);

/// <summary>Which battery field the vehicle card leaves out (REQ-0287, telemetry lost).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FakeBatteryUnreadable
{
    None,
    Battery,
    BatteryState,
    Both
}

/// <summary>
/// One vehicle's simulated battery (control-server#402). The percentage itself stays in <see cref="FakeVehicle.Battery"/>,
/// read as the value at <see cref="AnchorAt"/>; what is served is computed from it and the clock on every read, so time
/// passing never moves the state revision.
/// </summary>
public sealed record FakeVehicleCharge
{
    public required DateTimeOffset AnchorAt { get; init; }

    /// <summary>A start-charging act completed and the vehicle has not been taken off the charger since.</summary>
    public bool Docked { get; init; }

    /// <summary>The charger is feeding the battery. Only ever true while <see cref="Docked"/>.</summary>
    public bool Charging { get; init; }
    public int? ChargerMapId { get; init; }
    public int? ChargerStationId { get; init; }

    public FakeChargeStartOutcome StartOutcome { get; init; }

    /// <summary>
    /// The act's result code when a start-charging order hangs without 407802 -- under
    /// <see cref="FakeChargeStartOutcome.HangOnly"/>, or pushed to 9 by the scenario. Null by default. Never 407802.
    /// </summary>
    public int? HangResultCode { get; init; }

    /// <summary>Charging stops by itself once the battery reaches this, with no departure order (REQ-0285).</summary>
    public int? InterruptAtPercent { get; init; }

    /// <summary>CHARGING is reported and the battery does not move (REQ-0285, confirmed no progress).</summary>
    public bool NoProgress { get; init; }
    public FakeBatteryUnreadable BatteryUnreadable { get; init; }
}

/// <summary>The charging rules. Pure: every function takes the clock reading it needs.</summary>
public static class FakeChargingModel
{
    public const int ChargeActionId = 78;
    public const int CannotChargeResultCode = 407802;

    public static bool IsStartCharge(FakeMission mission) =>
        mission.Type == "act" && mission is { ActionId: ChargeActionId, ActionParam1: 1, ActionParam2: 0 };

    public static bool IsStopCharge(FakeMission mission) =>
        mission.Type == "act" && mission is { ActionId: ChargeActionId, ActionParam1: 2, ActionParam2: 0 };

    public static FakeMission StopChargeAct() => new("act", 0, 0)
    {
        ActionId = ChargeActionId,
        ActionParam1 = 2,
        ActionParam2 = 0
    };

    /// <summary>RIoT's own wording: the text says 挂起 even on success, and Round 25 notes resultCode is what counts.</summary>
    public static string ActResultText(int code) =>
        "未知类型错误,导致订单挂起:错误编码为:" + code.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static FakeCharger? FindCharger(FakeRiotState state, int mapId, int stationId) =>
        state.Chargers.FirstOrDefault(charger => charger.MapId == mapId && charger.StationId == stationId);

    /// <summary>The vehicle as RIoT reports it at <paramref name="now"/>: battery and battery state simulated, rest as stored.</summary>
    public static FakeVehicle Effective(FakeRiotState state, FakeVehicle vehicle, DateTimeOffset now)
    {
        if (!state.ChargeByVehicle.TryGetValue(vehicle.DeviceKey, out FakeVehicleCharge? charge))
        {
            return vehicle;
        }
        (int battery, bool charging) = Simulate(state, vehicle, charge, now);
        return vehicle with
        {
            Battery = battery,
            BatteryState = charging ? "CHARGING" : charge.Charging ? "NO_CHARGE" : vehicle.BatteryState
        };
    }

    /// <summary>Makes the simulated values at <paramref name="now"/> the stored ones and restarts the clock there.</summary>
    public static FakeRiotState Settle(FakeRiotState state, string vehicleKey, DateTimeOffset now)
    {
        if (!state.Vehicles.TryGetValue(vehicleKey, out FakeVehicle? vehicle))
        {
            return state;
        }
        FakeVehicleCharge? charge = state.ChargeByVehicle.GetValueOrDefault(vehicleKey);
        FakeVehicle settledVehicle = vehicle;
        FakeVehicleCharge settled;
        if (charge is null)
        {
            settled = new FakeVehicleCharge { AnchorAt = now };
        }
        else
        {
            (int battery, bool charging) = Simulate(state, vehicle, charge, now);
            settledVehicle = vehicle with
            {
                Battery = battery,
                BatteryState = charge.Charging && !charging ? "NO_CHARGE" : vehicle.BatteryState
            };
            settled = charge with { AnchorAt = now, Charging = charging };
        }
        return WithVehicle(state, settledVehicle, settled);
    }

    public static FakeRiotState WithVehicle(FakeRiotState state, FakeVehicle vehicle, FakeVehicleCharge charge)
    {
        Dictionary<string, FakeVehicle> vehicles = new(state.Vehicles, StringComparer.Ordinal) { [vehicle.DeviceKey] = vehicle };
        Dictionary<string, FakeVehicleCharge> charges = new(state.ChargeByVehicle, StringComparer.Ordinal)
        {
            [vehicle.DeviceKey] = charge
        };
        return state with { Vehicles = vehicles, ChargeByVehicle = charges };
    }

    /// <summary>
    /// The missions RIoT records for a new order: a move to a charger with an enter/exit station goes through it first,
    /// and a vehicle still on a charger gets <c>act(78,2,0)</c> at the head.
    /// </summary>
    public static IReadOnlyList<FakeMission> Expand(
        FakeRiotState state, string? appointVehicleKey, IReadOnlyList<FakeMission> missions)
    {
        if (state.Chargers.Count == 0 && state.ChargeByVehicle.Count == 0)
        {
            return missions;
        }
        List<FakeMission> expanded = [];
        foreach (FakeMission mission in missions)
        {
            // A caller that already routed through the enter/exit station is not routed through it twice.
            if (mission.Type == "move" &&
                FindCharger(state, mission.MapId, mission.Destination) is { EnterExitStationId: int enterExit } &&
                !(expanded.Count > 0 && expanded[^1] is { Type: "move" } previous &&
                  previous.MapId == mission.MapId && previous.Destination == enterExit))
            {
                expanded.Add(new FakeMission("move", mission.MapId, enterExit));
            }
            expanded.Add(mission);
        }

        if (appointVehicleKey is not null &&
            state.ChargeByVehicle.TryGetValue(appointVehicleKey, out FakeVehicleCharge? charge) &&
            charge.Docked)
        {
            List<FakeMission> head = [StopChargeAct()];
            if (charge is { ChargerMapId: int departureMap, ChargerStationId: int departureStation } &&
                FindCharger(state, departureMap, departureStation) is { ExpandDeparture: true, EnterExitStationId: int departureExit } &&
                !(expanded.FirstOrDefault(mission => mission.Type == "move") is { } firstMove &&
                  firstMove.MapId == departureMap && firstMove.Destination == departureExit))
            {
                head.Add(new FakeMission("move", departureMap, departureExit));
            }
            expanded.InsertRange(0, head);
        }
        return expanded;
    }

    /// <summary>
    /// Applies what an order-state change means for charging: a departure order's leading <c>act(78,2,0)</c> takes the
    /// vehicle off its charger once the order executes, and a start-charging order reaching 5 engages the charger (or, under
    /// an injected fault, hangs at 9 instead).
    /// </summary>
    /// <remarks>
    /// Departure is applied first because it comes first in the order: a vehicle on a charger given a new charge order
    /// leaves the old charger before it engages the new one. The other way round, an order pushed straight from 1 to 5
    /// engaged the charger and then its own head act stopped it again. An order that hangs at its charge act got past its
    /// head act first, so it has left the old charger too.
    /// </remarks>
    public static (FakeRiotState State, FakeOrder Order) ApplyOrderTransition(
        FakeRiotState state, FakeOrder before, FakeOrder after, DateTimeOffset now)
    {
        if (before.OrderState == after.OrderState)
        {
            return (state, after);
        }
        string? vehicleKey = BoundVehicle(after);
        FakeOrder order = after;

        int start = IndexOf(after.Missions, IsStartCharge);
        FakeChargeStartOutcome outcome = FakeChargeStartOutcome.Normal;
        int? hangResultCode = null;
        if (state.ChargeStartByUpperId.TryGetValue(after.UpperId, out FakeOrderChargeFault? byOrder))
        {
            (outcome, hangResultCode) = (byOrder.StartOutcome, byOrder.HangResultCode);
        }
        else if (vehicleKey is not null && state.ChargeByVehicle.TryGetValue(vehicleKey, out FakeVehicleCharge? byVehicle))
        {
            (outcome, hangResultCode) = (byVehicle.StartOutcome, byVehicle.HangResultCode);
        }
        bool resolves = start >= 0 && after.OrderState is 5 or 9;
        bool engages = resolves && after.OrderState == 5 && outcome == FakeChargeStartOutcome.Normal;
        bool hangs = resolves && !engages;

        if (after.Missions.Count > 0 && IsStopCharge(after.Missions[0]) &&
            before.OrderState is not (3 or 5) &&
            (after.OrderState is 3 or 5 || (hangs && start > 0)))
        {
            order = WithMission(order, 0, order.Missions[0] with
            {
                MissionState = 2,
                ResultCode = 0,
                ResultStr = ActResultText(0)
            });
            if (vehicleKey is not null && state.ChargeByVehicle.ContainsKey(vehicleKey))
            {
                state = StopCharging(state, vehicleKey, now);
            }
        }

        if (engages)
        {
            order = WithMission(order, start, order.Missions[start] with
            {
                MissionState = 2,
                ResultCode = 0,
                ResultStr = ActResultText(0)
            });
            if (vehicleKey is not null && state.Vehicles.ContainsKey(vehicleKey))
            {
                FakeMission target = order.Missions.Take(start).LastOrDefault(mission => mission.Type == "move")
                    ?? new FakeMission("move", 0, 0);
                state = StartCharging(state, vehicleKey, target.MapId, target.Destination, now);
            }
        }
        else if (hangs)
        {
            int? code = outcome == FakeChargeStartOutcome.CannotCharge ? CannotChargeResultCode : hangResultCode;
            order = WithMission(order with { OrderState = 9 }, start, order.Missions[start] with
            {
                MissionState = 1,
                ResultCode = code,
                ResultStr = code is int value ? ActResultText(value) : null
            });
        }
        return (state, order);
    }

    private static FakeRiotState StartCharging(FakeRiotState state, string vehicleKey, int mapId, int stationId, DateTimeOffset now)
    {
        state = Settle(state, vehicleKey, now);
        FakeVehicleCharge charge = state.ChargeByVehicle[vehicleKey];
        // Charging needs a charger: an act(78,1,0) completed anywhere else leaves the vehicle as it was. The real RIoT's
        // answer to that has never been observed, and a fake that invented one would be claiming it had.
        if (FindCharger(state, mapId, stationId) is null)
        {
            return state;
        }
        return WithVehicle(state, state.Vehicles[vehicleKey], charge with
        {
            Docked = true,
            Charging = true,
            ChargerMapId = mapId,
            ChargerStationId = stationId
        });
    }

    private static FakeRiotState StopCharging(FakeRiotState state, string vehicleKey, DateTimeOffset now)
    {
        state = Settle(state, vehicleKey, now);
        FakeVehicle vehicle = state.Vehicles[vehicleKey];
        return WithVehicle(state, vehicle with { BatteryState = "NO_CHARGE" }, state.ChargeByVehicle[vehicleKey] with
        {
            Docked = false,
            Charging = false,
            ChargerMapId = null,
            ChargerStationId = null
        });
    }

    /// <summary>
    /// Battery and whether the charger is still feeding it at <paramref name="now"/>. Rising at the docked charger's rate,
    /// capped at 100; falling at the discharge rate otherwise, floored at 0; flat when neither applies.
    /// </summary>
    private static (int Battery, bool Charging) Simulate(
        FakeRiotState state, FakeVehicle vehicle, FakeVehicleCharge charge, DateTimeOffset now)
    {
        int anchor = vehicle.Battery;
        if (!charge.Charging)
        {
            return (Discharge(state, anchor, charge.AnchorAt, now), false);
        }
        if (charge.NoProgress)
        {
            return (anchor, true);
        }
        FakeCharger? charger = charge is { ChargerMapId: int mapId, ChargerStationId: int stationId }
            ? FindCharger(state, mapId, stationId)
            : null;
        int interval = charger?.ChargeIntervalSeconds ?? 0;
        int percent = charger?.ChargePercentPerInterval ?? 0;

        if (charge.InterruptAtPercent is int stopAt)
        {
            DateTimeOffset? interruptedAt = null;
            int interruptedBattery = anchor;
            if (anchor >= stopAt)
            {
                interruptedAt = charge.AnchorAt;
            }
            else if (interval > 0 && percent > 0)
            {
                int steps = (stopAt - anchor + percent - 1) / percent;
                interruptedAt = charge.AnchorAt.AddSeconds((double)steps * interval);
                interruptedBattery = Math.Min(100, anchor + (steps * percent));
            }
            if (interruptedAt is DateTimeOffset at && now >= at)
            {
                return (Discharge(state, interruptedBattery, at, now), false);
            }
        }

        long risen = Steps(interval, charge.AnchorAt, now) * percent;
        return ((int)Math.Min(100, anchor + risen), true);
    }

    private static int Discharge(FakeRiotState state, int battery, DateTimeOffset from, DateTimeOffset now)
    {
        long fallen = Steps(state.DischargeIntervalSeconds, from, now) * state.DischargePercentPerInterval;
        return (int)Math.Max(0, battery - fallen);
    }

    private static long Steps(int intervalSeconds, DateTimeOffset from, DateTimeOffset now) =>
        intervalSeconds <= 0 || now <= from ? 0 : (long)Math.Floor((now - from).TotalSeconds / intervalSeconds);

    private static string? BoundVehicle(FakeOrder order) =>
        string.IsNullOrWhiteSpace(order.ExecuteVehicleKey) || order.ExecuteVehicleKey.Trim() == "--"
            ? order.AppointVehicleKey
            : order.ExecuteVehicleKey;

    private static int IndexOf(IReadOnlyList<FakeMission> missions, Func<FakeMission, bool> predicate)
    {
        for (int index = 0; index < missions.Count; index++)
        {
            if (predicate(missions[index]))
            {
                return index;
            }
        }
        return -1;
    }

    private static FakeOrder WithMission(FakeOrder order, int index, FakeMission mission)
    {
        FakeMission[] missions = [.. order.Missions];
        missions[index] = mission;
        return order with { Missions = missions };
    }
}
