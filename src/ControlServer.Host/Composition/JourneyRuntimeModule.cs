using ControlServer.Application;
using ControlServer.Host.Runtime;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Composition;

/// <summary>
/// Demand intake, journey planning, and the worker that drives a journey forward.
/// </summary>
internal static class JourneyRuntimeModule
{
    internal static IServiceCollection AddJourneyRuntime(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddScoped<DemandIntakeService>();
        services.AddScoped<MovementDispatchService>();
        services.AddScoped<JourneyIntakeCoordinator>();
        services.AddScoped<JourneyRuntimeEngine>();
        services.AddOptions<JourneyRuntimeOptions>()
            .Bind(configuration.GetSection(JourneyRuntimeOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JourneyRuntimeOptions>, JourneyRuntimeOptionsValidator>();
        services.AddHostedService<JourneyRuntimeWorker>();
        return services;
    }
}
