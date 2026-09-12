using ControlServer.TestDoubles;

namespace ControlServer.ProtocolFaultProxy;

/// <summary>
/// Arms the relay to swallow the next <c>count</c> DurableAcks that acknowledge
/// <c>acceptedMessageType</c>, closing the connection each time. A count of zero disarms it.
/// </summary>
public sealed record DropDurableAckCommand : CommandEnvelope
{
    public string? AcceptedMessageType { get; init; }
    public int? Count { get; init; }
}

public static class ControlPlane
{
    /// <summary>A handful covers every lost-ack scenario worth writing; more than that is a typo or a loop.</summary>
    private const int MaximumDrops = 10;

    public static void MapControlPlane(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<ProtocolFaultProxyState> engine =
            app.Services.GetRequiredService<CommandEngine<ProtocolFaultProxyState>>();
        ProtocolFaultProxyHost.RelayEndpoints endpoints =
            app.Services.GetRequiredService<ProtocolFaultProxyHost.RelayEndpoints>();
        TrafficLog log = app.Services.GetRequiredService<TrafficLog>();
        RouteGroupBuilder control = app.MapGroup("/control/v1");

        control.MapGet("/openapi.json", ControlPlaneConventions.OpenApiDocument);

        control.MapGet("/health", () => Results.Json(ControlPlaneConventions.Envelope(engine, new
        {
            status = "live",
            listen = endpoints.Listen.ToString(),
            target = endpoints.Target.ToString()
        })));

        // The traffic is what proves the onboard went through here at all: a scenario that saw no
        // drop could otherwise be reading an onboard wired straight to the server as a lucky run.
        control.MapGet("/snapshot", () => Results.Json(ControlPlaneConventions.Envelope(engine, new
        {
            listen = endpoints.Listen.ToString(),
            target = endpoints.Target.ToString(),
            plan = engine.Snapshot().State,
            traffic = log.Snapshot()
        })));

        control.MapPut("/drop-durable-ack", (DropDurableAckCommand command) =>
            ControlPlaneConventions.Handle(engine, "drop-durable-ack", command, state =>
            {
                if (command.Count is not int count || count < 0 || count > MaximumDrops ||
                    (count > 0 && string.IsNullOrWhiteSpace(command.AcceptedMessageType)))
                {
                    throw new CommandRefusedException(ReasonCodes.InvalidArgument);
                }
                if (count == 0)
                {
                    return state.PlanId is null ? null : new ProtocolFaultProxyState();
                }
                // Every arming is a new plan, even one identical to the last: drops are counted per
                // plan, so arming again after a drop has to be able to drop again.
                return new ProtocolFaultProxyState
                {
                    DropAckFor = command.AcceptedMessageType,
                    DropCount = count,
                    PlanId = command.CommandId
                };
            }));

        // Resets the plan, not the traffic: what already crossed the relay stays evidence.
        control.MapPost("/reset", (CommandEnvelope command) =>
        {
            if (string.IsNullOrWhiteSpace(command.CommandId))
            {
                return ControlPlaneConventions.Refused(engine, ReasonCodes.InvalidArgument, null);
            }
            CommandOutcome outcome = engine.Reset(command.RunId, command.CommandId, command.ExpectedRevision);
            return ControlPlaneConventions.Respond(engine, command.CommandId, outcome);
        });
    }
}
