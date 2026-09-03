using System.Net;
using ControlServer.TestDoubles;

namespace ControlServer.FakeMesIngest;

/// <summary>
/// Builds the fake as one object so the executable and a black-box test start the same thing.
/// </summary>
public static class FakeMesIngestHost
{
    public const int DefaultPort = 58088;

    public static WebApplication? TryCreate(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

        FakeMesIngestSeed seed = new();
        builder.Configuration.GetSection("FakeMesIngest:Seed").Bind(seed);
        builder.Services.AddSingleton(seed);
        string instanceId = builder.Configuration["FakeMesIngest:instanceId"] ?? "fake-mes-ingest-1";
        builder.Services.AddSingleton(services => new CommandEngine<FakeMesIngestState>(
            instanceId, services.GetRequiredService<FakeMesIngestSeed>().BuildInitialState));

        IPEndPoint? listener = ControlPlaneConventions.ResolveLoopbackListener(
            builder.Configuration, "FakeMesIngest", DefaultPort);
        if (listener is null)
        {
            return null;
        }
        builder.WebHost.ConfigureKestrel(options => options.Listen(listener));

        WebApplication app = builder.Build();
        app.MapMesIngestDataPlane();
        app.MapControlPlane();
        return app;
    }
}
