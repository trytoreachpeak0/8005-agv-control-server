using System.Globalization;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// How <see cref="JourneyRuntimeOptions"/> is bound from configuration, in one place so that a test
/// can bind it exactly as the Host does (control-server#535).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two lists and the roster are taken whole from the last configuration layer that names them</b>:
/// <see cref="JourneyRuntimeOptions.AllowedWorkTypes"/>, <see cref="JourneyRuntimeOptions.AllowedDispatchZones"/>
/// and <see cref="JourneyRuntimeOptions.Fleet"/>. .NET configuration merges an array by index, so a later layer of
/// one item over an earlier layer of six replaced index 0 and kept the other five. The v2 parallel instance's
/// production MesIngest mode writes <c>["STAGING_TO_WIRE"]</c> over the package's six work types, and the five it
/// kept included WIRE_TO_GATE -- the type the MVP takes from the same catalog (control-server#535 review M1). An
/// allow-list that a later layer can only add to is not one a deployment can narrow.
/// </para>
/// <para>
/// The roster is an array of objects, each holding two arrays of its own (control-server#578). Merged by index, a
/// later layer with fewer vehicles left the earlier layer's extra vehicles in force, and a vehicle whose
/// <see cref="FleetVehicleOptions.AllowedTaskTypes"/> was shorter kept the earlier layer's extra task types -- and
/// per-vehicle task types are exactly what keeps each car to its own work. So the roster is taken whole from its
/// last layer, every vehicle with every field of it: a field that layer leaves out is the field's default, not an
/// earlier layer's value.
/// </para>
/// <para>
/// A layer that does not name a list or the roster leaves it to the layers before it, as before. A layer cannot
/// empty one: an empty JSON array names no element, so it reads as "not named". For the roster that means a layer
/// writing <c>"fleet": []</c> to say "one vehicle, the primary pair" does not remove an earlier layer's roster; the
/// parallel instance's single-car overlay writes exactly that, and is safe today only because the package's
/// <c>appsettings.json</c> has no roster and the installer's read-back refuses a roster the definition did not
/// write (control-server#571). It is a known limitation, kept on purpose (control-server#578, the coordinator's
/// ruling): <b>the day the package's <c>appsettings.json</c> gains a roster, this rule must change first</b>, so that a
/// layer writing <c>"fleet": []</c> counts as naming the roster. <c>PackageAppSettingsRosterArchitectureTests</c> turns
/// red on that day.
/// </para>
/// </remarks>
public static class JourneyRuntimeOptionsRegistration
{
    public static OptionsBuilder<JourneyRuntimeOptions> AddJourneyRuntimeOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services.AddOptions<JourneyRuntimeOptions>()
            .Bind(configuration.GetSection(JourneyRuntimeOptions.SectionName))
            .PostConfigure(options =>
            {
                options.AllowedWorkTypes = LastLayerList(configuration, "allowedWorkTypes") ?? options.AllowedWorkTypes;
                options.AllowedDispatchZones = LastLayerList(configuration, "allowedDispatchZones") ?? options.AllowedDispatchZones;
                options.Fleet = LastLayerRoster(configuration) ?? options.Fleet;
            });
    }

    /// <summary>
    /// The elements of <c>JourneyRuntime:{key}</c> in the last provider that has any, in index order;
    /// <see langword="null"/> when no provider names one.
    /// </summary>
    internal static string[]? LastLayerList(IConfiguration configuration, string key) =>
        FromLastLayer(configuration, key, ReadList);

    /// <summary>
    /// <c>JourneyRuntime:fleet</c> bound from the last provider that names a vehicle, and from that provider alone;
    /// <see langword="null"/> when no provider names one.
    /// </summary>
    internal static FleetVehicleOptions[]? LastLayerRoster(IConfiguration configuration) =>
        FromLastLayer(configuration, "fleet", ReadRoster);

    /// <remarks>
    /// Never falls back to the index merge in silence (control-server#535 re-review S4): a provider that
    /// wraps another configuration (<see cref="ChainedConfigurationProvider"/>, what AddConfiguration adds) is
    /// searched layer by layer in turn, and a configuration whose layers cannot be asked at all -- not an
    /// <see cref="IConfigurationRoot"/> -- is refused when it names the key.
    /// </remarks>
    private static T? FromLastLayer<T>(IConfiguration configuration, string key, Func<IConfigurationProvider, string, T?> read)
        where T : class
    {
        string path = ConfigurationPath.Combine(JourneyRuntimeOptions.SectionName, key);
        if (configuration is not IConfigurationRoot root)
        {
            if (!configuration.GetSection(path).GetChildren().Any())
            {
                return null;
            }
            throw new InvalidOperationException(
                $"{path} is set, but the configuration given ({configuration.GetType().Name}) has no layers to ask, so it " +
                "cannot be taken whole from its last layer and would be merged by index. Bind JourneyRuntime from the configuration root.");
        }
        foreach (IConfigurationProvider provider in root.Providers.Reverse())
        {
            T? found = provider is ChainedConfigurationProvider chained
                ? FromLastLayer(chained.Configuration, key, read)
                : read(provider, path);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private static string[]? ReadList(IConfigurationProvider provider, string path)
    {
        List<(int Index, string Value)> elements = [];
        foreach (string child in provider.GetChildKeys([], path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (int.TryParse(child, NumberStyles.None, CultureInfo.InvariantCulture, out int index) &&
                provider.TryGet(ConfigurationPath.Combine(path, child), out string? value) && value is not null)
            {
                elements.Add((index, value));
            }
        }
        return elements.Count > 0
            ? [.. elements.OrderBy(element => element.Index).Select(element => element.Value)]
            : null;
    }

    private static FleetVehicleOptions[]? ReadRoster(IConfigurationProvider provider, string path)
    {
        Dictionary<string, string?> layer = new(StringComparer.OrdinalIgnoreCase);
        foreach (string child in provider.GetChildKeys([], path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (int.TryParse(child, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                Collect(provider, ConfigurationPath.Combine(path, child), layer);
            }
        }
        if (layer.Count == 0)
        {
            return null;
        }
        IConfigurationRoot alone = new ConfigurationBuilder().AddInMemoryCollection(layer).Build();
        return alone.GetSection(path).Get<FleetVehicleOptions[]>() ?? [];
    }

    private static void Collect(IConfigurationProvider provider, string path, Dictionary<string, string?> into)
    {
        if (provider.TryGet(path, out string? value) && value is not null)
        {
            into[path] = value;
        }
        foreach (string child in provider.GetChildKeys([], path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Collect(provider, ConfigurationPath.Combine(path, child), into);
        }
    }
}
