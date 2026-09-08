using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime.Fleet;

/// <summary>One vehicle of the fleet, under both of the names it is known by.</summary>
/// <param name="AgvId">8005's name for the vehicle. Fault facts, sessions and journeys key on it.</param>
/// <param name="VehicleKey">RIoT's name for the same vehicle. Dispatch and orders key on it.</param>
/// <param name="AgvLifecycleGeneration">The lifecycle generation this vehicle's journeys are stamped with.</param>
public sealed record FleetVehicle(string AgvId, string VehicleKey, long AgvLifecycleGeneration);

/// <summary>
/// The fleet this server drives, and the register that maps its two identity spaces onto each other.
/// </summary>
/// <remarks>
/// <para>
/// <b>This replaces the one-pair lookup the criteria used to do against the runtime options.</b>
/// Before there were several vehicles, "is this the configured vehicle key" was a complete
/// identity resolution; with a fleet it silently answers "no" for every vehicle but one, which
/// fails closed but blocks the whole fleet. The roster answers the question the criteria actually
/// ask: which <c>agvId</c> is this <c>vehicleKey</c>.
/// </para>
/// <para>
/// It is built from configuration once and never re-read, so every round of a running server sees
/// the same fleet. A vehicle is added by restarting the server with it configured, which is also
/// the only way the map, gate station and dispatch generation it runs under can change.
/// </para>
/// </remarks>
public sealed class VehicleRoster
{
    private readonly Dictionary<string, FleetVehicle> _byVehicleKey;
    private readonly Dictionary<string, FleetVehicle> _byAgvId;

    public VehicleRoster(IOptions<JourneyRuntimeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        JourneyRuntimeOptions runtime = options.Value;

        // An empty roster is the single-vehicle deployment, not an unconfigured one: the pair, the
        // allowed work types and the dispatch zone are all stated, and this reads them as the one
        // entry they describe. That is what keeps N=1 behaviour identical across the upgrade.
        Vehicles = runtime.Fleet.Length == 0
            ? [new FleetVehicle(runtime.AgvId, runtime.VehicleKey, runtime.AgvLifecycleGeneration)]
            : [.. runtime.Fleet
                .Select(vehicle => new FleetVehicle(
                    vehicle.AgvId, vehicle.VehicleKey, vehicle.AgvLifecycleGeneration))
                .OrderBy(vehicle => vehicle.AgvId, StringComparer.Ordinal)];

        _byVehicleKey = Vehicles.ToDictionary(vehicle => vehicle.VehicleKey, StringComparer.Ordinal);
        _byAgvId = Vehicles.ToDictionary(vehicle => vehicle.AgvId, StringComparer.Ordinal);
    }

    /// <summary>Every vehicle, in a stable order so that a round serves them in the same sequence.</summary>
    public IReadOnlyList<FleetVehicle> Vehicles { get; }

    /// <summary>The vehicle RIoT calls <paramref name="vehicleKey"/>, or null when it is not ours.</summary>
    public FleetVehicle? ByVehicleKey(string vehicleKey) =>
        _byVehicleKey.GetValueOrDefault(vehicleKey);

    /// <summary>The vehicle 8005 calls <paramref name="agvId"/>, or null when it is not ours.</summary>
    public FleetVehicle? ByAgvId(string agvId) => _byAgvId.GetValueOrDefault(agvId);
}
