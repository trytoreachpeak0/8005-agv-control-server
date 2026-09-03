using System.Net;
using ControlServer.TestDoubles;

namespace ControlServer.ClockSkewProxy;

/// <summary>
/// The only state this double owns: how far to move the evidence timestamp, in milliseconds.
/// Positive means the onboard sees evidence stamped in its own future, which is what a slow
/// onboard clock produces.
/// </summary>
public sealed record ClockSkewProxyState
{
    public required int SkewMs { get; init; }
}

public sealed class ClockSkewProxySeed
{
    /// <summary>Starts at zero so a proxy nobody configured changes nothing it forwards.</summary>
    public int SkewMs { get; set; }

    public ClockSkewProxyState BuildInitialState() => new() { SkewMs = SkewMs };
}

/// <summary>
/// Counts forwarded upstream calls and remembers what the last one carried. Outside the command
/// engine for the reason ControlServer.FakeRiot's MapStationReadCounter records: a counter that
/// moved the revision on every poll would make expectedRevision useless for the one command that
/// carries a real change.
/// </summary>
public sealed class ForwardLog
{
    private readonly object gate = new();
    private long forwarded;
    private int lastUpstreamStatus;
    private string? lastUpstreamObservedAt;
    private string? lastForwardedObservedAt;

    public void Record(int upstreamStatus, string? upstreamObservedAt, string? forwardedObservedAt)
    {
        lock (gate)
        {
            forwarded++;
            lastUpstreamStatus = upstreamStatus;
            lastUpstreamObservedAt = upstreamObservedAt;
            lastForwardedObservedAt = forwardedObservedAt;
        }
    }

    public object Snapshot()
    {
        lock (gate)
        {
            return new
            {
                forwardedRequests = forwarded,
                lastUpstreamStatus,
                lastUpstreamObservedAt,
                lastForwardedObservedAt
            };
        }
    }
}

/// <summary>
/// A one-route reverse proxy in front of ControlServer's vehicle-safety projection that moves
/// <c>observedAt</c> by a configurable offset, and nothing else.
///
/// Why this exists. The onboard refuses evidence stamped in its own future
/// (VehicleSafetySignal.IsFresh, and the same check inside ControlServerVehicleSafetySignalProvider),
/// which is what makes a slow onboard clock block a session for good --
/// 8005-agv-onboard-hmi#1, fixed in abb8e73 with a bounded tolerance. In L2 both ends run on one
/// machine and share one clock, and observedAt is stamped by ControlServer's own timeProvider, so
/// the skew never appears on its own and neither the defect nor its fix can be exercised.
///
/// Why this is honest rather than a scripted result. The code under test is the shipped onboard's
/// real freshness logic, and what reaches it is bit-for-bit what a clock N milliseconds slow would
/// produce for that comparison: a server-stamped instant sitting in the onboard's future. What it
/// is *not* is a whole slow clock -- a real one would also move every timestamp the onboard writes
/// itself (journal entries, journeySnapshotMaxAgeMs, sentAt). For #1 that is exactly the comparison
/// at fault, but evidence must say so rather than claim more.
///
/// Two things this deliberately does not do: change the machine clock (it would move everything on
/// the box, including the four other processes and the operator's own session), and pretend by
/// setting maximumEvidenceAgeMs to zero (same symptom, different cause -- it proves nothing about
/// #1).
/// </summary>
public static class ClockSkewProxyHost
{
    public const int DefaultPort = 58090;

    public static WebApplication? TryCreate(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

        // No default target. Every other double answers for a system that does not exist here; this
        // one forwards to a real server, so guessing where would be guessing which server.
        string? target = builder.Configuration["ClockSkewProxy:target"];
        if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? targetUri) ||
            targetUri.Scheme != Uri.UriSchemeHttp)
        {
            Console.Error.WriteLine(
                "ClockSkewProxy needs --ClockSkewProxy:target=http://host:port pointing at the " +
                "ControlServer whose vehicle-safety projection it should forward.");
            return null;
        }

        ClockSkewProxySeed seed = new();
        builder.Configuration.GetSection("ClockSkewProxy:Seed").Bind(seed);
        builder.Services.AddSingleton(seed);
        builder.Services.AddSingleton(new ForwardTarget(targetUri));
        builder.Services.AddSingleton<ForwardLog>();
        builder.Services.AddHttpClient();
        string instanceId = builder.Configuration["ClockSkewProxy:instanceId"] ?? "clock-skew-proxy-1";
        builder.Services.AddSingleton(services => new CommandEngine<ClockSkewProxyState>(
            instanceId, services.GetRequiredService<ClockSkewProxySeed>().BuildInitialState));

        IPEndPoint? listener = ControlPlaneConventions.ResolveLoopbackListener(
            builder.Configuration, "ClockSkewProxy", DefaultPort);
        if (listener is null)
        {
            return null;
        }
        builder.WebHost.ConfigureKestrel(options => options.Listen(listener));

        WebApplication app = builder.Build();
        app.MapSafetyProxyDataPlane();
        app.MapControlPlane();
        return app;
    }

    /// <summary>Resolved once at startup so no request path can be pointed somewhere else.</summary>
    public sealed record ForwardTarget(Uri BaseUri);
}
