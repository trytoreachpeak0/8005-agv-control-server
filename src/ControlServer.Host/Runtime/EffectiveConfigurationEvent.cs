using Microsoft.Extensions.Logging;

namespace ControlServer.Host.Runtime;

/// <summary>
/// control-server#535 review M2, extended by control-server#571: what this process really bound, logged once it has
/// started (options validated).
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
/// </remarks>
public static class EffectiveConfigurationEvent
{
    public const string Template =
        "EFFECTIVE_CONFIGURATION allowedWorkTypes={AllowedWorkTypes} allowedDispatchZones={AllowedDispatchZones} mesIngestBaseUrl={MesIngestBaseUrl} fleet={Fleet}";

    public static readonly EventId Id = new(5350, "EffectiveConfiguration");

    private static readonly Action<ILogger, string[], string[], string, IReadOnlyList<IReadOnlyDictionary<string, object>>, Exception?> Write =
        LoggerMessage.Define<string[], string[], string, IReadOnlyList<IReadOnlyDictionary<string, object>>>(LogLevel.Information, Id, Template);

    public static void Log(ILogger logger, JourneyRuntimeOptions effective, string mesIngestBaseUrl) =>
        Write(logger, effective.AllowedWorkTypes, effective.AllowedDispatchZones, mesIngestBaseUrl, Fleet(effective), null);

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
