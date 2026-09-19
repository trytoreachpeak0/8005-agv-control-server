namespace ControlServer.Dashboard;

/// <summary>The one place the dashboard maps its write routes (control-server#162).</summary>
public static class DashboardActionRoutes
{
    public static IServiceCollection AddDashboardActions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }

    public static WebApplication MapDashboardActions(this WebApplication app) => app;
}
