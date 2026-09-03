namespace ControlServer.FakeRiot;

/// <summary>
/// The safe initial state every round starts from, bound from configuration. Defaults reproduce
/// the 2026-09-03 field vehicle and map so a scenario that says nothing still faces the shape the
/// control server was configured against: map 25, gate station 210, one bound vehicle at rest.
/// </summary>
public sealed class FakeRiotSeed
{
    public string VehicleKey { get; set; } = "BROKERX-0c20ff0600d644869a6a80c186065d85";
    public string MapIdentity { get; set; } = "老厂前线new";
    public int MapId { get; set; } = 25;
    public int StartStationId { get; set; } = 210;
    public int BatteryPercent { get; set; } = 80;
    public string BatteryState { get; set; } = "NO_CHARGE";

    /// <summary>
    /// Station id to name, as <c>/api/imap/v1/mapInfo/stations/{mapId}</c> reports it. The names
    /// are load bearing: MapStationResolver picks the pickup station by parsing the area out of
    /// them, and requires the gate station's name to match GateStationId exactly.
    /// </summary>
    public Dictionary<string, string> Stations { get; set; } = new(StringComparer.Ordinal)
    {
        ["210"] = "关卡",
        ["12"] = "N1-3_N1-7",
        ["11"] = "C15-13"
    };

    public FakeRiotState BuildInitialState(string runId)
    {
        FakeVehicle vehicle = new()
        {
            DeviceKey = VehicleKey,
            CurrentMap = MapIdentity,
            CurrentPosition = StartStationId,
            Battery = BatteryPercent,
            BatteryState = BatteryState
        };
        FakeStation[] stations = Stations
            .Select(pair => new FakeStation(
                int.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture),
                pair.Value))
            .OrderBy(station => station.Id)
            .ToArray();
        return new FakeRiotState
        {
            RunId = runId,
            // A fresh round starts at 1, the same floor slots-simulator uses, so an orchestrator
            // can assert on an absolute revision after a reset rather than a relative one.
            Revision = 1,
            Vehicles = new Dictionary<string, FakeVehicle>(StringComparer.Ordinal)
            {
                [VehicleKey] = vehicle
            },
            OrdersByUpperId = new Dictionary<string, FakeOrder>(StringComparer.Ordinal),
            StationsByMapId = new Dictionary<int, IReadOnlyList<FakeStation>>
            {
                [MapId] = stations
            },
            NextOrderSequence = 1
        };
    }
}
