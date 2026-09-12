using System.Globalization;
using System.Net;
using ControlServer.TestDoubles;

namespace ControlServer.ProtocolFaultProxy;

/// <summary>
/// The only state this double owns: which DurableAck to swallow, and how many of them.
/// </summary>
public sealed record ProtocolFaultProxyState
{
    /// <summary>The acceptedMessageType whose DurableAck is dropped. Null forwards everything.</summary>
    public string? DropAckForMessageType { get; init; }

    /// <summary>How many such acks the plan drops before it goes back to forwarding them.</summary>
    public int DropCount { get; init; }

    /// <summary>The commandId that armed the plan; drops are counted against the plan that asked for them.</summary>
    public string? PlanId { get; init; }
}

/// <summary>
/// A line-level relay between the onboard and ControlServer's onboard protocol listener that can
/// lose one kind of DurableAck on the way back, or take the link down on request, and nothing else.
///
/// Why this exists. The onboard keeps a durable message in its journal until the DurableAck for it
/// arrives; if the link goes down after the server has committed the message but before the ack
/// reaches the vehicle, the next connection replays it with the same messageId and sentAt, rebound
/// to the new session generation (8005-agv-control-server#30). In L2 both ends run on one machine
/// over loopback, so that window never opens on its own, and the replay path has never run against
/// the real server.
///
/// Why this is honest rather than a scripted result. The server really commits the message and
/// really writes the ack; the vehicle really does not receive it and really sees the connection
/// close -- which is what a dropped link after the server's commit looks like from both ends. What
/// happens next is the shipped code on both sides. What it is *not* is a flaky network: every other
/// line is forwarded byte for byte, and the drop is exactly the one a scenario asked for.
///
/// The disconnect is the same link going down without a chosen line lost: both ends see the
/// connection close, the onboard reconnects, and what is left to watch is how each end treats the new
/// session -- the server's replay of its own unacknowledged messages included (8005-agv-control-server#31).
/// </summary>
public static class ProtocolFaultProxyHost
{
    public const int DefaultControlPort = 58091;
    public const int DefaultListenPort = 58092;

    public static WebApplication? TryCreate(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
            options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

        // No default target, for the reason ClockSkewProxy gives: this forwards to a real server, so
        // guessing where would be guessing which server. Loopback only on top of that -- a relay that
        // can swallow acknowledgements must never sit in front of a server anything else depends on.
        string? target = builder.Configuration["ProtocolFaultProxy:target"];
        if (!IPEndPoint.TryParse(target ?? string.Empty, out IPEndPoint? targetEndpoint) ||
            targetEndpoint.Port == 0 ||
            !IPAddress.IsLoopback(targetEndpoint.Address))
        {
            Console.Error.WriteLine(
                "ProtocolFaultProxy needs --ProtocolFaultProxy:target=127.0.0.1:port pointing at the " +
                "loopback ControlServer onboard protocol listener it should sit in front of.");
            return null;
        }

        IPEndPoint? controlListener = ControlPlaneConventions.ResolveLoopbackListener(
            builder.Configuration, "ProtocolFaultProxy", DefaultControlPort);
        if (controlListener is null)
        {
            return null;
        }
        // The relay listens on the address the control plane was validated for, so the loopback
        // rule covers both faces.
        int listenPort = int.TryParse(
            builder.Configuration["ProtocolFaultProxy:listenPort"], CultureInfo.InvariantCulture, out int configured)
            ? configured
            : DefaultListenPort;

        builder.Services.AddSingleton(new RelayEndpoints(
            new IPEndPoint(controlListener.Address, listenPort), targetEndpoint));
        builder.Services.AddSingleton<TrafficLog>();
        builder.Services.AddSingleton<RelayConnections>();
        string instanceId = builder.Configuration["ProtocolFaultProxy:instanceId"] ?? "protocol-fault-proxy-1";
        builder.Services.AddSingleton(new CommandEngine<ProtocolFaultProxyState>(
            instanceId, () => new ProtocolFaultProxyState()));
        builder.Services.AddHostedService<ProtocolRelay>();
        builder.WebHost.ConfigureKestrel(options => options.Listen(controlListener));

        WebApplication app = builder.Build();
        app.MapControlPlane();
        return app;
    }

    /// <summary>Resolved once at startup so no request can point the relay somewhere else.</summary>
    public sealed record RelayEndpoints(IPEndPoint Listen, IPEndPoint Target);
}
