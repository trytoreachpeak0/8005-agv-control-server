using ControlServer.Host.Runtime;

namespace ControlServer.Host.Composition;

/// <summary>
/// Everything that talks to RIoT: the create-dispatch gate, the absent-at-observation experiment,
/// the SDK client, and the map station resolver built on top of it.
/// </summary>
internal static class RiotIntegrationModule
{
    internal static IServiceCollection AddRiotIntegration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddRiotCreateDispatchGate(configuration);
        services.AddRiotAbsentAtObservationCreateExperiment(configuration);
        services.AddRiotSdkIntegration(configuration);
        services.AddSingleton<MapStationResolver>();
        return services;
    }
}
