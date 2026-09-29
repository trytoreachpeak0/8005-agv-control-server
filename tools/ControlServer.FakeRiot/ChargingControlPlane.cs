using System.Text.Json.Serialization;
using ControlServer.TestDoubles;

namespace ControlServer.FakeRiot;

/// <summary>One charger in <see cref="ChargersCommand"/>. See <see cref="FakeCharger"/>.</summary>
public sealed record ChargerSpec
{
    public int? MapId { get; init; }
    public int? StationId { get; init; }
    public int? EnterExitStationId { get; init; }
    public int? ChargeIntervalSeconds { get; init; }
    public int? ChargePercentPerInterval { get; init; }
    public bool ExpandDeparture { get; init; }
}

/// <summary>
/// Replaces the registered chargers and the discharge rate (control-server#402). A replacement, like the edge groups: a
/// scenario that could only add chargers could never take one away.
/// </summary>
public sealed record ChargersCommand : CommandEnvelope
{
    public List<ChargerSpec>? Chargers { get; init; }

    /// <summary>With <see cref="DischargePercentPerInterval"/>: both 0 (the default) or both positive.</summary>
    public int? DischargeIntervalSeconds { get; init; }
    public int? DischargePercentPerInterval { get; init; }
}

/// <summary>
/// Sets charging faults on one vehicle or one order (control-server#402). Only the fields present are applied; an order
/// takes only <see cref="StartOutcome"/>, because the other faults are the vehicle's, not the order's.
/// </summary>
public sealed record ChargingFaultCommand : CommandEnvelope
{
    public string? VehicleKey { get; init; }
    public string? UpperId { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FakeChargeStartOutcome? StartOutcome { get; init; }
    public int? InterruptAtPercent { get; init; }
    public bool ClearInterrupt { get; init; }
    public bool? NoProgress { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FakeBatteryUnreadable? BatteryUnreadable { get; init; }
}

internal static class FakeChargingControl
{
    public static FakeRiotState? RegisterChargers(FakeRiotState state, ChargersCommand command, DateTimeOffset now)
    {
        if (command.Chargers is null)
        {
            throw new CommandRefusedException(ReasonCodes.InvalidArgument);
        }
        List<FakeCharger> chargers = [];
        foreach (ChargerSpec spec in command.Chargers)
        {
            if (spec is not
                {
                    MapId: int mapId,
                    StationId: int stationId,
                    ChargeIntervalSeconds: int interval and >= 1 and <= 3600,
                    ChargePercentPerInterval: int percent and >= 1 and <= 100
                } ||
                chargers.Any(existing => existing.MapId == mapId && existing.StationId == stationId) ||
                spec.EnterExitStationId == stationId)
            {
                throw new CommandRefusedException(ReasonCodes.InvalidArgument);
            }
            chargers.Add(new FakeCharger(mapId, stationId, spec.EnterExitStationId, interval, percent, spec.ExpandDeparture));
        }
        int dischargeInterval = command.DischargeIntervalSeconds ?? 0;
        int dischargePercent = command.DischargePercentPerInterval ?? 0;
        bool flat = dischargeInterval == 0 && dischargePercent == 0;
        bool falling = dischargeInterval is >= 1 and <= 3600 && dischargePercent is >= 1 and <= 100;
        if (!flat && !falling)
        {
            throw new CommandRefusedException(ReasonCodes.InvalidArgument);
        }
        if (state.Chargers.SequenceEqual(chargers) &&
            state.DischargeIntervalSeconds == dischargeInterval &&
            state.DischargePercentPerInterval == dischargePercent)
        {
            return null;
        }

        // Every simulated battery is brought up to now under the old rates before the new ones apply, so a rate change
        // is never applied backwards over time that already passed.
        foreach (string key in state.ChargeByVehicle.Keys.ToArray())
        {
            state = FakeChargingModel.Settle(state, key, now);
        }
        state = state with
        {
            Chargers = chargers,
            DischargeIntervalSeconds = dischargeInterval,
            DischargePercentPerInterval = dischargePercent
        };
        if (falling)
        {
            // Discharge applies to every vehicle, so every vehicle needs a simulation to discharge from.
            foreach (string key in state.Vehicles.Keys.Where(key => !state.ChargeByVehicle.ContainsKey(key)).ToArray())
            {
                state = FakeChargingModel.Settle(state, key, now);
            }
        }
        return state;
    }

    public static FakeRiotState? SetFaults(FakeRiotState state, ChargingFaultCommand command, DateTimeOffset now)
    {
        bool byVehicle = !string.IsNullOrWhiteSpace(command.VehicleKey);
        bool byOrder = !string.IsNullOrWhiteSpace(command.UpperId);
        if (byVehicle == byOrder || command.InterruptAtPercent is < 0 or > 100)
        {
            throw new CommandRefusedException(ReasonCodes.InvalidArgument);
        }

        if (byOrder)
        {
            if (command.StartOutcome is not FakeChargeStartOutcome outcome ||
                command.InterruptAtPercent is not null || command.ClearInterrupt ||
                command.NoProgress is not null || command.BatteryUnreadable is not null)
            {
                throw new CommandRefusedException(ReasonCodes.InvalidArgument);
            }
            if (state.ChargeStartOutcomeByUpperId.TryGetValue(command.UpperId!, out FakeChargeStartOutcome current) &&
                current == outcome)
            {
                return null;
            }
            Dictionary<string, FakeChargeStartOutcome> outcomes = new(state.ChargeStartOutcomeByUpperId, StringComparer.Ordinal)
            {
                [command.UpperId!] = outcome
            };
            return state with { ChargeStartOutcomeByUpperId = outcomes };
        }

        string key = command.VehicleKey!;
        if (!state.Vehicles.ContainsKey(key))
        {
            throw new CommandRefusedException(ReasonCodes.NotFound);
        }
        FakeVehicleCharge? before = state.ChargeByVehicle.GetValueOrDefault(key);
        FakeVehicleCharge basis = before ?? new FakeVehicleCharge { AnchorAt = now };
        FakeVehicleCharge wanted = basis with
        {
            StartOutcome = command.StartOutcome ?? basis.StartOutcome,
            InterruptAtPercent = command.ClearInterrupt ? null : command.InterruptAtPercent ?? basis.InterruptAtPercent,
            NoProgress = command.NoProgress ?? basis.NoProgress,
            BatteryUnreadable = command.BatteryUnreadable ?? basis.BatteryUnreadable
        };
        if (before is not null && wanted == before)
        {
            return null;
        }

        // A fault takes effect from now: settle first so it is never applied to time that already passed.
        state = FakeChargingModel.Settle(state, key, now);
        FakeVehicleCharge settled = state.ChargeByVehicle[key];
        return FakeChargingModel.WithVehicle(state, state.Vehicles[key], settled with
        {
            StartOutcome = wanted.StartOutcome,
            InterruptAtPercent = wanted.InterruptAtPercent,
            NoProgress = wanted.NoProgress,
            BatteryUnreadable = wanted.BatteryUnreadable
        });
    }
}
