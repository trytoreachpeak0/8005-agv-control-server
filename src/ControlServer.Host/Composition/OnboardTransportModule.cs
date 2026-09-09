using ControlServer.Application;
using ControlServer.Host.Transport;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Composition;

/// <summary>
/// The NDJSON peer link to the onboard HMI, and everything that speaks over it.
/// </summary>
internal static class OnboardTransportModule
{
    internal static IServiceCollection AddOnboardTransport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OnboardTransportOptions>()
            .Bind(configuration.GetSection(OnboardTransportOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OnboardTransportOptions>, OnboardTransportOptionsValidator>();
        services.AddScoped<OnboardMessageProcessor>();
        services.AddScoped<OnboardJourneyPublisher>();
        services.AddScoped<OnboardRecoveryCoordinator>();
        services.AddSingleton<OnboardPeer>();
        services.AddSingleton<IOnboardPeer>(sp => sp.GetRequiredService<OnboardPeer>());
        services.AddHostedService<OnboardTcpServer>();
        return services;
    }
}
