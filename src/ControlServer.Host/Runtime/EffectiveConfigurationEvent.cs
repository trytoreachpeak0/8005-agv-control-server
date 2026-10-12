using System.Globalization;
using ControlServer.Host.Runtime.CreateGate;
using Microsoft.Extensions.Logging;

namespace ControlServer.Host.Runtime;

/// <summary>
/// control-server#535 review M2, extended by control-server#571, control-server#638 and control-server#531: what this process
/// really bound,
/// logged once it has started (options validated).
/// </summary>
/// <remarks>
/// <para>
/// The v2 parallel installer reads this event back from the log (<c>Find-ParallelEffectiveConfiguration</c> in
/// <c>scripts/parallel/ParallelInstance.psm1</c>) and compares it with the instance definition. Reading the definition
/// instead is how #535 M1 passed: a definition and an overlay that both said one work type, and a Host that bound six.
/// </para>
/// <para>
/// <b>The roster</b> (#571) is appended, so the three fields #535 reads keep their names, their place and the event
/// id. Each vehicle is logged with exactly four fields -- AgvId, VehicleKey, AllowedTaskTypes, Zones -- built here
/// field by field rather than by destructuring <see cref="FleetVehicleOptions"/>, so a property added to that class
/// later does not reach the log unless it is added here. A single-vehicle deployment logs an empty roster.
/// </para>
/// <para>
/// <b><see cref="JourneyRuntimeOptions.ClearanceToWaitingPointEnabled"/></b> (#638) is appended after the roster, the same
/// way: on, a vehicle in the clearing loop drives itself to a waiting point (REQ-0178), so the installer has to read what
/// the Host bound, not what the overlay file says. An event without it is from a package older than #638.
/// </para>
/// <para>
/// <b>REQ-0302's two approved values</b> (<see cref="MapStationCatalogOptions"/>, #531) are appended last, as one object with
/// exactly ApprovedSyncPeriod and ApprovedMaxUnconfirmed, each <c>hh:mm:ss</c> (invariant <c>c</c> format) or null when
/// unset. Without them every Map/Station-dependent demand is refused with 2110, so the installer reads back what the Host
/// bound rather than trusting the overlay file -- the .NET layers merge per key, and a value from an earlier layer or an
/// environment variable would otherwise go unseen. An event without it is from a package older than #531.
/// </para>
/// </remarks>
public static class EffectiveConfigurationEvent
{
    public const string Template =
        "EFFECTIVE_CONFIGURATION allowedWorkTypes={AllowedWorkTypes} allowedDispatchZones={AllowedDispatchZones} mesIngestBaseUrl={MesIngestBaseUrl} fleet={Fleet} clearanceToWaitingPointEnabled={ClearanceToWaitingPointEnabled} mapStationCatalog={MapStationCatalog}";

    public static readonly EventId Id = new(5350, "EffectiveConfiguration");

    private static readonly Action<ILogger, string[], string[], string, IReadOnlyList<IReadOnlyDictionary<string, object>>, bool, IReadOnlyDictionary<string, string?>, Exception?> Write =
        LoggerMessage.Define<string[], string[], string, IReadOnlyList<IReadOnlyDictionary<string, object>>, bool, IReadOnlyDictionary<string, string?>>(LogLevel.Information, Id, Template);

    public static void Log(ILogger logger, JourneyRuntimeOptions effective, MapStationCatalogOptions catalog, string mesIngestBaseUrl) =>
        Write(logger, effective.AllowedWorkTypes, effective.AllowedDispatchZones, mesIngestBaseUrl, Fleet(effective),
            effective.ClearanceToWaitingPointEnabled, MapStationCatalog(catalog), null);

    /// <summary>The two approved values as logged: these two keys only, each <c>hh:mm:ss</c> or null when unset.</summary>
    public static IReadOnlyDictionary<string, string?> MapStationCatalog(MapStationCatalogOptions catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ApprovedSyncPeriod"] = catalog.ApprovedSyncPeriod?.ToString("c", CultureInfo.InvariantCulture),
            ["ApprovedMaxUnconfirmed"] = catalog.ApprovedMaxUnconfirmed?.ToString("c", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>The roster as logged: one dictionary per vehicle, in bound order, these four keys only.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object>> Fleet(JourneyRuntimeOptions effective) =>
    [
        .. effective.Fleet.Select(vehicle => (IReadOnlyDictionary<string, object>)new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["AgvId"] = vehicle.AgvId,
            ["VehicleKey"] = vehicle.VehicleKey,
            ["AllowedTaskTypes"] = vehicle.AllowedTaskTypes,
            ["Zones"] = vehicle.Zones,
        }),
    ];
}
