using ControlServer.TestDoubles;

namespace ControlServer.ClockSkewProxy;

/// <summary>
/// Moves the evidence timestamp by <c>skewMs</c>. Positive puts it in the onboard's future, which
/// is what a slow onboard clock looks like from the wire; negative is a fast one.
/// </summary>
public sealed record SkewCommand : CommandEnvelope
{
    public int? SkewMs { get; init; }
}

public static class ControlPlane
{
    /// <summary>
    /// Wide enough for any tolerance worth testing on either side of the bound, narrow enough that
    /// a typo cannot turn the projection into something no scenario meant to ask for.
    /// </summary>
    private const int MaximumSkewMs = 60_000;

    public static void MapControlPlane(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<ClockSkewProxyState> engine =
            app.Services.GetRequiredService<CommandEngine<ClockSkewProxyState>>();
        ClockSkewProxyHost.ForwardTarget target =
            app.Services.GetRequiredService<ClockSkewProxyHost.ForwardTarget>();
        ForwardLog log = app.Services.GetRequiredService<ForwardLog>();
        RouteGroupBuilder control = app.MapGroup("/control/v1");

        control.MapGet("/openapi.json", ControlPlaneConventions.OpenApiDocument);

        control.MapGet("/health", () => Results.Json(ControlPlaneConventions.Envelope(engine, new
        {
            status = "live",
            target = target.BaseUri.ToString(),
            engine.Snapshot().State.SkewMs
        })));

        // forwardedRequests is what proves the onboard went through here rather than straight to
        // the server: a scenario that saw no readiness could otherwise be reading a broken proxy
        // as a fail-closed onboard.
        control.MapGet("/snapshot", () => Results.Json(ControlPlaneConventions.Envelope(engine, new
        {
            target = target.BaseUri.ToString(),
            engine.Snapshot().State.SkewMs,
            forward = log.Snapshot()
        })));

        control.MapPut("/skew", (SkewCommand command) =>
            ControlPlaneConventions.Handle(engine, "skew", command, state =>
            {
                if (command.SkewMs is not int skewMs || Math.Abs(skewMs) > MaximumSkewMs)
                {
                    throw new CommandRefusedException(ReasonCodes.InvalidArgument);
                }
                return skewMs == state.SkewMs ? null : state with { SkewMs = skewMs };
            }));

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
