using System.Net;
using ControlServer.TestDoubles;

namespace ControlServer.FakeRiot;

/// <summary>
/// Builds the fake as one object so the executable and a black-box test start the same thing.
/// A test double whose test path differs from its run path proves nothing about the run path.
/// </summary>
public static class FakeRiotHost
{
    public const int DefaultPort = 58008;

    /// <summary>
    /// Returns null and writes the reason to stderr when the configured listen address is not
    /// loopback and the override was not set.
    /// </summary>
    public static WebApplication? TryCreate(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

        FakeRiotSeed seed = new();
        builder.Configuration.GetSection("FakeRiot:Seed").Bind(seed);
        builder.Services.AddSingleton(seed);
        string instanceId = builder.Configuration["FakeRiot:instanceId"] ?? "fake-riot-1";
        builder.Services.AddSingleton(services => new CommandEngine<FakeRiotState>(
            instanceId, services.GetRequiredService<FakeRiotSeed>().BuildInitialState));
        builder.Services.AddSingleton<MapStationReadCounter>();

        IPEndPoint? listener = ControlPlaneConventions.ResolveLoopbackListener(
            builder.Configuration, "FakeRiot", DefaultPort);
        if (listener is null)
        {
            return null;
        }
        builder.WebHost.ConfigureKestrel(options => options.Listen(listener));

        WebApplication app = builder.Build();
        app.MapRiotDataPlane();
        app.MapRiotRouteGraphPlane();
        app.MapRiotCommandPlane();
        app.MapControlPlane();
        return app;
    }
}
