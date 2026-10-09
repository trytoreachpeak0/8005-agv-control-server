using System.Globalization;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// How <see cref="JourneyRuntimeOptions"/> is bound from configuration, in one place so that a test
/// can bind it exactly as the Host does (control-server#535).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two lists are taken whole from the last configuration layer that names them</b>:
/// <see cref="JourneyRuntimeOptions.AllowedWorkTypes"/> and <see cref="JourneyRuntimeOptions.AllowedDispatchZones"/>.
/// .NET configuration merges an array by index, so a later layer of one item over an earlier layer of
/// six replaced index 0 and kept the other five. The v2 parallel instance's production MesIngest mode
/// writes <c>["STAGING_TO_WIRE"]</c> over the package's six work types, and the five it kept included
/// WIRE_TO_GATE -- the type the MVP takes from the same catalog (control-server#535 review M1). An
/// allow-list that a later layer can only add to is not one a deployment can narrow.
/// </para>
/// <para>
/// A layer that does not name a list leaves it to the layers before it, as before. A layer cannot
/// empty a list: an empty JSON array names no element, so it reads as "not named".
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
            });
    }

    /// <summary>
    /// The elements of <c>JourneyRuntime:{key}</c> in the last provider that has any, in index order;
    /// <see langword="null"/> when no provider names one, or the configuration has no providers to ask.
    /// </summary>
    internal static string[]? LastLayerList(IConfiguration configuration, string key)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return null;
        }
        string path = ConfigurationPath.Combine(JourneyRuntimeOptions.SectionName, key);
        foreach (IConfigurationProvider provider in root.Providers.Reverse())
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
            if (elements.Count > 0)
            {
                return [.. elements.OrderBy(element => element.Index).Select(element => element.Value)];
            }
        }
        return null;
    }
}
