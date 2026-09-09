using ControlServer.Host.Runtime;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Composition;

/// <summary>
/// The fail-closed vehicle motion projection Onboard reads before departing. Its endpoint is
/// configuration-gated, so the module owns both halves of that decision.
/// </summary>
internal static class OnboardSafetyProjectionModule
{
    internal static IServiceCollection AddOnboardSafetyProjection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<OnboardSafetyProjectionOptions>()
            .Bind(configuration.GetSection(OnboardSafetyProjectionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<OnboardSafetyProjectionOptions>,
            OnboardSafetyProjectionOptionsValidator>();
        return services;
    }

    internal static WebApplication MapOnboardSafetyProjection(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.Configuration.GetValue<bool>("OnboardSafetyProjection:enabled"))
        {
            app.MapOnboardVehicleSafety();
        }
        return app;
    }
}
