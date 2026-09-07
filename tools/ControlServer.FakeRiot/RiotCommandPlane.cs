using System.Text.Json;
using ControlServer.TestDoubles;

namespace ControlServer.FakeRiot;

/// <summary>
/// The RIoT order-command and emergency-service surface, recorded and not simulated.
/// </summary>
/// <remarks>
/// <para>
/// <b>These endpoints record the call and change nothing else.</b> An OrderHold does not move the
/// order to HELD here, and triggerEmergency does not stop anything. That is the specification's
/// decision, and it is the right one for what these scenarios prove: the control server's
/// reconciliation must confirm a terminal state by reading it back, so a fake that helpfully
/// applied the consequence would let a server that never reconciles pass.
/// </para>
/// <para>
/// What the recording has to support is three assertions: it was called when it should have been,
/// with these arguments, and exactly this many times. Hence one row per call, in order, with the
/// arguments kept verbatim.
/// </para>
/// <para>
/// Two routes cover six commands, because that is how the real API is shaped: four order commands
/// differ only by <c>commandType</c> in the body, and the two emergency services differ only by a
/// path segment.
/// </para>
/// </remarks>
public static class RiotCommandPlane
{
    public static void MapRiotCommandPlane(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CommandEngine<FakeRiotState> engine = app.Services.GetRequiredService<CommandEngine<FakeRiotState>>();
        TimeProvider timeProvider = app.Services.GetService<TimeProvider>() ?? TimeProvider.System;

        // CMD_ORDER_CANCEL / CMD_ORDER_HELD / CMD_ORDER_CONTINUE_FROM_HELD /
        // CMD_ORDER_CONTINUE_FROM_HANG all arrive here.
        app.MapPost("/api/task/v1/order/command/{orderId}", async (
            string orderId, JsonElement body, CancellationToken cancellationToken) =>
        {
            IResult? fault = await RiotDataPlane.ApplyFaultAsync(engine, cancellationToken).ConfigureAwait(false);
            if (fault is not null) return fault;

            string commandType = body.TryGetProperty("commandType", out JsonElement type)
                ? type.GetString() ?? "UNKNOWN"
                : "UNKNOWN";

            Record(engine, timeProvider, commandType, orderId, body);
            return RiotDataPlane.Ok(null);
        });

        app.MapPost("/api/device/v1/command/sync/service/{deviceKey}/{serviceId}", async (
            string deviceKey, string serviceId, JsonElement body, CancellationToken cancellationToken) =>
        {
            IResult? fault = await RiotDataPlane.ApplyFaultAsync(engine, cancellationToken).ConfigureAwait(false);
            if (fault is not null) return fault;

            // The real service rejects an empty thingsProperties with an NPE rather than a
            // structured error, so a caller that omits it must not pass here either.
            if (!body.TryGetProperty("messageId", out _) ||
                !body.TryGetProperty("thingsProperties", out _))
            {
                return Results.Json(
                    new { code = "00002", message = "java.lang.NullPointerException", result = (object?)null });
            }

            Record(engine, timeProvider, serviceId, deviceKey, body);
            return RiotDataPlane.Ok(null);
        });
    }

    private static void Record(
        CommandEngine<FakeRiotState> engine,
        TimeProvider timeProvider,
        string commandType,
        string target,
        JsonElement body)
    {
        DateTimeOffset at = timeProvider.GetUtcNow();
        string arguments = body.ValueKind == JsonValueKind.Undefined ? "" : body.GetRawText();

        engine.Mutate<object?>(state => (
            state with
            {
                CommandInvocations =
                [
                    .. state.CommandInvocations,
                    new FakeCommandInvocation(commandType, target, arguments, at),
                ],
            },
            null));
    }
}
