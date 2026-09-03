using System.Net;
using ControlServer.TestDoubles;

namespace ControlServer.FakeOnboard;

/// <summary>
/// Builds the fake as one object so the executable and a black-box test start the same thing.
/// </summary>
public static class FakeOnboardHost
{
    public const int DefaultControlPort = 58009;

    public static WebApplication? TryCreate(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

        FakeOnboardOptions peerOptions = new();
        builder.Configuration.GetSection("FakeOnboard:Peer").Bind(peerOptions);
        builder.Services.AddSingleton(peerOptions);
        builder.Services.AddSingleton<OnboardPeerHolder>();
        string instanceId = builder.Configuration["FakeOnboard:instanceId"] ?? "fake-onboard-1";
        builder.Services.AddSingleton(_ => new CommandEngine<FakeOnboardState>(
            instanceId, () => new FakeOnboardState()));

        IPEndPoint? listener = ControlPlaneConventions.ResolveLoopbackListener(
            builder.Configuration, "FakeOnboard", DefaultControlPort);
        if (listener is null)
        {
            return null;
        }
        builder.WebHost.ConfigureKestrel(options => options.Listen(listener));

        WebApplication app = builder.Build();
        app.MapControlPlane();
        return app;
    }

    /// <summary>
    /// Connects the peer to ControlServer and completes the handshake. Kept separate from
    /// <see cref="TryCreate"/> so the control plane is already answering while the peer is still
    /// connecting: an orchestrator polls /control/v1/health for readiness rather than sleeping,
    /// and it can only do that if the endpoint exists before the session does.
    /// </summary>
    public static async Task<OnboardPeerSession> ConnectAsync(
        WebApplication app,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        OnboardPeerSession peer = new(
            app.Services.GetRequiredService<CommandEngine<FakeOnboardState>>(),
            app.Services.GetRequiredService<FakeOnboardOptions>());
        await peer.StartAsync(cancellationToken).ConfigureAwait(false);
        app.Services.GetRequiredService<OnboardPeerHolder>().Peer = peer;
        return peer;
    }
}
