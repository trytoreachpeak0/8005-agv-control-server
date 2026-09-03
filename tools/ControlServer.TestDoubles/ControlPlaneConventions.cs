using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace ControlServer.TestDoubles;

/// <summary>Fields every state-changing control command carries.</summary>
public record CommandEnvelope
{
    public string? RunId { get; init; }
    public string? CommandId { get; init; }
    public long? ExpectedRevision { get; init; }
}

/// <summary>
/// The response envelope and listener rules every double shares, so an orchestrator reads one
/// shape whichever double answered.
/// </summary>
public static class ControlPlaneConventions
{
    public const string SchemaVersion = "1";

    /// <summary>
    /// Wraps a body in the standard envelope: schemaVersion, instanceId, runId, revision,
    /// observedAt. observedAt is when this response was generated, not when state last changed.
    /// </summary>
    public static object Envelope<TState>(CommandEngine<TState> engine, object body)
    {
        ArgumentNullException.ThrowIfNull(engine);
        EngineSnapshot<TState> snapshot = engine.Snapshot();
        return new
        {
            schemaVersion = SchemaVersion,
            instanceId = engine.InstanceId,
            runId = snapshot.RunId,
            revision = snapshot.Revision,
            observedAt = DateTimeOffset.UtcNow,
            body
        };
    }

    public static IResult Respond<TState>(CommandEngine<TState> engine, string commandId, CommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Accepted
            ? Results.Json(Envelope(engine, new
            {
                commandId,
                changed = outcome.Changed,
                replayed = outcome.Replayed,
                appliedRevision = outcome.AppliedRevision
            }))
            : Refused(engine, outcome.ReasonCode!, commandId);
    }

    public static IResult Refused<TState>(CommandEngine<TState> engine, string reasonCode, string? commandId) =>
        Results.Json(
            Envelope(engine, new { commandId, reasonCode }),
            statusCode: reasonCode == ReasonCodes.InvalidArgument
                ? StatusCodes.Status400BadRequest
                : StatusCodes.Status409Conflict);

    /// <summary>
    /// Routes one command through the engine. The envelope fields are deliberately excluded from
    /// the content fingerprint: retrying the same command must match on business content alone,
    /// whatever revision it once expected.
    /// </summary>
    public static IResult Handle<TState, TCommand>(
        CommandEngine<TState> engine,
        string operation,
        TCommand command,
        Func<TState, TState?> mutate)
        where TCommand : CommandEnvelope
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.CommandId) || string.IsNullOrWhiteSpace(command.RunId))
        {
            return Refused(engine, ReasonCodes.InvalidArgument, command.CommandId);
        }
        CommandOutcome outcome = engine.Apply(
            operation,
            command.RunId,
            command.CommandId,
            command.ExpectedRevision,
            command with { RunId = null, CommandId = null, ExpectedRevision = null },
            mutate);
        return Respond(engine, command.CommandId, outcome);
    }

    /// <summary>
    /// Resolves and validates the listener address. A test double that answers questions the plant
    /// asks for real must never be reachable from the plant network -- something there could take
    /// its answers for the real system's. Returning null (and saying why on stderr) is the only
    /// refusal a scenario cannot accidentally skip past.
    /// </summary>
    public static IPEndPoint? ResolveLoopbackListener(
        IConfiguration configuration,
        string sectionName,
        int defaultPort)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string listenAddress = configuration[sectionName + ":listenAddress"] ?? "127.0.0.1";
        int port = int.TryParse(
            configuration[sectionName + ":port"], CultureInfo.InvariantCulture, out int configured)
            ? configured
            : defaultPort;
        IPAddress address = IPAddress.Parse(listenAddress);
        if (!IPAddress.IsLoopback(address) &&
            !configuration.GetValue<bool>(sectionName + ":allowNonLoopbackListen"))
        {
            Console.Error.WriteLine(
                sectionName + " refuses to listen on " + listenAddress + ": set " + sectionName +
                ":allowNonLoopbackListen to override, and understand why first.");
            return null;
        }
        return new IPEndPoint(address, port);
    }

    /// <summary>Serves the double's own machine contract from beside its assembly.</summary>
    public static IResult OpenApiDocument()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "openapi.json");
        return File.Exists(path)
            ? Results.Text(File.ReadAllText(path), "application/json")
            : Results.NotFound();
    }
}
