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
        // The safety summary the handshake's SafetyStateSnapshot carries. It has to be settable
        // before the peer connects, not after: a session established while the vehicle is still
        // moving is the shape the 2026-09-03 field defect had, and PUT /safety can only report a
        // change to a session that already exists.
        SafetySummary seedSafety = ReadSeedSafety(builder.Configuration);
        builder.Services.AddSingleton(_ => new CommandEngine<FakeOnboardState>(
            instanceId, () => new FakeOnboardState { Safety = seedSafety }));
        // Set by the L2 runner, which validates the file against the protocol schemas afterwards.
        if (builder.Configuration["FakeOnboard:SchemaRecordPath"] is { Length: > 0 } schemaRecordPath)
        {
            SchemaRecorder.Install(schemaRecordPath);
        }

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
    /// Reads FakeOnboard:Seed:* into the initial safety summary. Each flag is read by name rather
    /// than bound, because SafetySummary's properties are init-only and a binder that silently
    /// applied none of them would leave a scenario proving the opposite of what it says it proves.
    /// </summary>
    private static SafetySummary ReadSeedSafety(ConfigurationManager configuration)
    {
        SafetySummary defaults = new();
        return new SafetySummary
        {
            DepartureSafe = configuration.GetValue("FakeOnboard:Seed:departureSafe", defaults.DepartureSafe),
            VehicleStopped = configuration.GetValue("FakeOnboard:Seed:vehicleStopped", defaults.VehicleStopped),
            AllTargetSlotsLocked =
                configuration.GetValue("FakeOnboard:Seed:allTargetSlotsLocked", defaults.AllTargetSlotsLocked),
            AllUnlockOutputsReset =
                configuration.GetValue("FakeOnboard:Seed:allUnlockOutputsReset", defaults.AllUnlockOutputsReset),
            UnknownPresent = configuration.GetValue("FakeOnboard:Seed:unknownPresent", defaults.UnknownPresent),
            ReasonCodes = configuration.GetSection("FakeOnboard:Seed:reasonCodes").Get<string[]>()
                ?? defaults.ReasonCodes
        };
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
