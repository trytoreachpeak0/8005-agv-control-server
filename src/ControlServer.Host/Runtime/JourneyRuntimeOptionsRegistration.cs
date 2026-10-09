using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// How <see cref="JourneyRuntimeOptions"/> is bound from configuration, in one place so that a test
/// can bind it exactly as the Host does (control-server#535).
/// </summary>
public static class JourneyRuntimeOptionsRegistration
{
    public static OptionsBuilder<JourneyRuntimeOptions> AddJourneyRuntimeOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services.AddOptions<JourneyRuntimeOptions>()
            .Bind(configuration.GetSection(JourneyRuntimeOptions.SectionName));
    }
}
