namespace ControlServer.Host.Composition;

/// <summary>
/// Services every other module depends on and none of them owns.
/// </summary>
internal static class ControlServerCoreModule
{
    internal static IServiceCollection AddControlServerCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
