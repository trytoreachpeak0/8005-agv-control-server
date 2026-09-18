using System.Globalization;
using Serilog;

namespace ControlServer.Host.Composition;

/// <summary>
/// Host-level bootstrap: how the process runs, logs and binds. It owns no domain service.
/// </summary>
internal static class HostBootstrapModule
{
    internal static WebApplicationBuilder ConfigureControlServerHost(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Host.UseWindowsService(options => options.ServiceName = "8005 AGV ControlServer");
        // Stated rather than inherited: this is the .NET 8 default, and BackgroundServiceFaultReporting
        // depends on it -- the host must stop so the process exits non-zero and the service recovery
        // policy starts a whole one, instead of running on with a dead listener or journey loop.
        builder.Services.Configure<HostOptions>(options =>
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost);
        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));
        builder.WebHost.UseUrls(builder.Configuration["Health:url"] ?? "http://127.0.0.1:58007");
        return builder;
    }

    internal static WebApplication UseControlServerHost(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseSerilogRequestLogging();
        app.UseBackgroundServiceFaultReporting();
        return app;
    }
}
