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
/// <para>
/// <b>The foreign running order release entry</b> (#617 incremental review S-1) is appended after that, as one object with exactly
/// Enabled and CredentialPresent: an entry that is on while its credential variable is empty answers every request 503, and
/// nothing else says so until someone on site needs it. Logged, not refused at start -- one exit is not worth a server that
/// will not start. For a person reading the log: the installer's read-back does not read this key; what guards a deployment
/// is the credential check before install and warning 2235. An event without it is from a package older than #617.
/// </para>
/// <para>
/// <b>The admission policy and the two waits</b> (#672, PR #670 review L3 and cs#609) are appended last:
/// <see cref="JourneyRuntimeOptions.AdmissionPolicyVersion"/> as a number, <see cref="JourneyRuntimeOptions.AdmissionPolicyDeploymentId"/>,
/// and <see cref="JourneyRuntimeOptions.CargoHoldingTimeout"/> and <see cref="JourneyRuntimeOptions.StationDepartureWaitTimeout"/> as
/// <c>hh:mm:ss</c> (invariant <c>c</c> format). Until then only the overlay file or the database said which version the Host bound,
/// and a hand edit of the holding wait to five minutes was reinstalled back to thirty unseen (2026-10-12). The installer compares
/// them with the definition and prints them for 19-deploy-control-server-parallel.ps1. An event without them is from a package
/// older than #672.
/// </para>
/// <para>
/// More placeholders than the six <c>LoggerMessage.Define</c> takes, so the event hands the logger its state the way
/// <c>Define</c> does -- the template under <c>{OriginalFormat}</c> and each value as it is. Not the logging source generator: its
/// state formats a collection into a string, and the installer reads the roster and the catalog back as JSON structure.
/// </para>
/// </remarks>
public static class EffectiveConfigurationEvent
{
    public const string Template =
        "EFFECTIVE_CONFIGURATION allowedWorkTypes={AllowedWorkTypes} allowedDispatchZones={AllowedDispatchZones} mesIngestBaseUrl={MesIngestBaseUrl} fleet={Fleet} clearanceToWaitingPointEnabled={ClearanceToWaitingPointEnabled} mapStationCatalog={MapStationCatalog} foreignRunningOrderRelease={ForeignRunningOrderRelease} admissionPolicyVersion={AdmissionPolicyVersion} admissionPolicyDeploymentId={AdmissionPolicyDeploymentId} cargoHoldingTimeout={CargoHoldingTimeout} stationDepartureWaitTimeout={StationDepartureWaitTimeout}";

    public static readonly EventId Id = new(5350, "EffectiveConfiguration");

    public static void Log(
        ILogger logger,
        JourneyRuntimeOptions effective,
        MapStationCatalogOptions catalog,
        string mesIngestBaseUrl,
        ForeignOrders.ForeignRunningOrderReleaseState? foreignRunningOrderRelease = null,
        ControlServer.Domain.SlotConfigurationGateMode slotConfigurationGate = ControlServer.Domain.SlotConfigurationGateMode.Off)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(effective);
        _ = slotConfigurationGate;
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }
        KeyValuePair<string, object?>[] state =
        [
            new("AllowedWorkTypes", effective.AllowedWorkTypes),
            new("AllowedDispatchZones", effective.AllowedDispatchZones),
            new("MesIngestBaseUrl", mesIngestBaseUrl),
            new("Fleet", Fleet(effective)),
            new("ClearanceToWaitingPointEnabled", effective.ClearanceToWaitingPointEnabled),
            new("MapStationCatalog", MapStationCatalog(catalog)),
            new("ForeignRunningOrderRelease",
                ForeignRunningOrderRelease(foreignRunningOrderRelease ?? new ForeignOrders.ForeignRunningOrderReleaseState(false, false))),
            new("AdmissionPolicyVersion", effective.AdmissionPolicyVersion),
            new("AdmissionPolicyDeploymentId", effective.AdmissionPolicyDeploymentId),
            new("CargoHoldingTimeout", effective.CargoHoldingTimeout.ToString("c", CultureInfo.InvariantCulture)),
            new("StationDepartureWaitTimeout", effective.StationDepartureWaitTimeout.ToString("c", CultureInfo.InvariantCulture)),
            new("{OriginalFormat}", Template),
        ];
        logger.Log(LogLevel.Information, Id, state, null, static (values, _) => Render(values));
    }

    /// <summary>The message as text, for a provider that wants one: the template with each value in place.</summary>
    private static string Render(KeyValuePair<string, object?>[] values)
    {
        string text = Template;
        foreach ((string key, object? value) in values)
        {
            text = text.Replace("{" + key + "}", Shown(value), StringComparison.Ordinal);
        }
        return text;
    }

    private static string Shown(object? value) => value switch
    {
        null => "null",
        string text => text,
        bool flag => flag ? "True" : "False",
        System.Collections.IEnumerable items => "[" + string.Join(", ", items.Cast<object?>().Select(Shown)) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>The release entry as logged: these two keys only.</summary>
    public static IReadOnlyDictionary<string, bool> ForeignRunningOrderRelease(ForeignOrders.ForeignRunningOrderReleaseState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["Enabled"] = state.Enabled,
            ["CredentialPresent"] = state.CredentialPresent,
        };
    }

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
