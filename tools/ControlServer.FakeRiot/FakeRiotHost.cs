using System.Globalization;
using System.Net;

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
    /// loopback and the override was not set. A test double that answers movement questions must
    /// not be reachable from the plant network, where something could take its answers for RIoT's.
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
        builder.Services.AddSingleton(services => new FakeRiotEngine(
            services.GetRequiredService<FakeRiotSeed>(), instanceId));

        string listenAddress = builder.Configuration["FakeRiot:listenAddress"] ?? "127.0.0.1";
        int port = int.TryParse(
            builder.Configuration["FakeRiot:port"], CultureInfo.InvariantCulture, out int configuredPort)
            ? configuredPort
            : DefaultPort;
        IPAddress address = IPAddress.Parse(listenAddress);
        if (!IPAddress.IsLoopback(address) &&
            !builder.Configuration.GetValue<bool>("FakeRiot:allowNonLoopbackListen"))
        {
            Console.Error.WriteLine(
                "FakeRiot refuses to listen on " + listenAddress +
                ": set FakeRiot:allowNonLoopbackListen to override, and understand why first.");
            return null;
        }
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, port));

        WebApplication app = builder.Build();
        app.MapRiotDataPlane();
        app.MapControlPlane();
        return app;
    }
}
