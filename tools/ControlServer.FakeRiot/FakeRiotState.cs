using System.Text.Json.Serialization;

namespace ControlServer.FakeRiot;

/// <summary>
/// One vehicle as the two RIoT vehicle endpoints report it. The card
/// (<c>getVehicleInfoByDeviceKey</c>) and the execution facts (<c>getVehicleInfo</c>) are two
/// views of the same physical vehicle, so they live in one record: a scenario that moves the
/// vehicle must not be able to leave one view saying IDLE while the other says RUNNING.
/// </summary>
public sealed record FakeVehicle
{
    public required string DeviceKey { get; init; }
    public bool Enable { get; init; } = true;

    /// <summary>RIoT's connectivity code on the card. 1 is online; anything else is not.</summary>
    public int Status { get; init; } = 1;
    public string ProcState { get; init; } = "IDLE";
    public string CurrentMap { get; init; } = "老厂前线new";
    public int? CurrentPosition { get; init; }
    public int Battery { get; init; } = 80;
    public string BatteryState { get; init; } = "NO_CHARGE";
    public double Speed { get; init; }
    public int LockStatus { get; init; }
    public string? OrderTaskId { get; init; }

    /// <summary>MT_FINISHED (at rest), MT_RUNNING (moving) or MT_NA (unknown to RIoT).</summary>
    public string MovementState { get; init; } = "MT_FINISHED";
    public string ControlState { get; init; } = "CONTROL_STATE_OK";
    public string EmergencyState { get; init; } = "OK";
    public string BreakSwitchState { get; init; } = "MOVABLE";
    public string LocationState { get; init; } = "LOCATION_STATE_RUNNING";
    public bool ProcessingOrder { get; init; }
    public string IntegrationLevel { get; init; } = "ON_LINE";
}

/// <summary>
/// One order record. <c>ExecuteVehicleKey</c> is deliberately nullable and starts as RIoT's "--"
/// placeholder for a QUEUEING order: BC-ORDER-012 says RIoT reports that until it binds a vehicle,
/// and reading it as a real key has already broken the control server once.
/// </summary>
public sealed record FakeOrder
{
    public required long Id { get; init; }
    public required string OrderId { get; init; }
    public required string UpperId { get; init; }

    /// <summary>1 QUEUEING, 3 EXECUTING, 5 COMPLETE, 7/9 the other non-final states.</summary>
    public int OrderState { get; init; } = 1;
    public string? AppointVehicleKey { get; init; }
    public string? ExecuteVehicleKey { get; init; } = "--";
    public int? EndStationNo { get; init; }
    public required IReadOnlyList<FakeMission> Missions { get; init; }
}

public sealed record FakeMission(string Type, int MapId, int Destination);

public sealed record FakeStation(int Id, string Name);

/// <summary>How the RIoT data plane misbehaves. The control plane is never affected by it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FakeRiotFaultMode
{
    Normal,
    NoResponse,
    ServerError,
    Delay
}

/// <summary>Everything one test round can observe or drive.</summary>
public sealed record FakeRiotState
{
    public required IReadOnlyDictionary<string, FakeVehicle> Vehicles { get; init; }
    public required IReadOnlyDictionary<string, FakeOrder> OrdersByUpperId { get; init; }
    public required IReadOnlyDictionary<int, IReadOnlyList<FakeStation>> StationsByMapId { get; init; }
    public FakeRiotFaultMode FaultMode { get; init; } = FakeRiotFaultMode.Normal;
    public int DelayMs { get; init; }

    /// <summary>Serial number for the next created order, so orderIds are stable within a round.</summary>
    public required long NextOrderSequence { get; init; }
}
