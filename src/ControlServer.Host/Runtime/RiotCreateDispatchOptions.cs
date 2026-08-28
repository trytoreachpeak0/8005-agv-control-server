using ControlServer.Application;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// Operational gate for RIoT create dispatch, separate from <c>JourneyRuntime</c> so that
/// enabling the runtime is not by itself an authorization to place a real order. It is closed
/// unless the deployed configuration explicitly opens it, so a missing section fails closed.
/// </summary>
public sealed class RiotCreateDispatchOptions
{
    public const string SectionName = "RiotCreateDispatch";

    public bool Enabled { get; set; }
}

public static class RiotCreateDispatchServiceCollectionExtensions
{
    public static IServiceCollection AddRiotCreateDispatchGate(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RiotCreateDispatchOptions>()
            .Bind(configuration.GetSection(RiotCreateDispatchOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton(serviceProvider => new RiotCreateDispatchPolicy(
            serviceProvider.GetRequiredService<IOptions<RiotCreateDispatchOptions>>().Value.Enabled));
        return services;
    }
}
