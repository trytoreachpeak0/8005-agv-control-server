using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Fleet;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// The class-three entry for a loaded vehicle (REQ-0356 as revised by CP-0011, requirements baseline v1.10.0,
/// control-server#539): a person with the exception-recovery permission releases this server's stop on a vehicle that is, or
/// may be, carrying something.
/// </summary>
/// <remarks>
/// <para>
/// <b>Decided by <see cref="LoadedEmergencyStopRelease"/></b>, which checks the role, writes the immutable audit and asks
/// <see cref="EmergencyStopSupervisor.ReleaseLoadedOnGovernedConfirmationAsync"/> for everything else. This authenticates the
/// call, resolves the named vehicle and maps the result onto HTTP, in the shape of <see cref="EmergencyStopReleaseEndpoints"/>.
/// </para>
/// <para>
/// <b>Every request is audited</b>, the ones turned away before the decision included
/// (<see cref="LoadedEmergencyStopRelease.RecordTurnedAwayAsync"/>).
/// </para>
/// <para>
/// <b>Its own credential.</b> Mounted on the same switch as the empty-vehicle entry (<c>EmergencyStopRelease:enabled</c>), but
/// authenticated by <see cref="EmergencyStopReleaseOptions.LoadedCredentialEnvironmentVariable"/>, so that whoever holds the
/// empty-vehicle credential cannot release a loaded one. Unset, it answers 503: a site turns it on deliberately. It is not a
/// login -- see the gap written on <see cref="LoadedEmergencyStopRelease"/>.
/// </para>
/// </remarks>
public static class LoadedEmergencyStopReleaseEndpoints
{
    public const string Route = "/api/safety/v1/emergency-stop-releases/loaded";

    public static void MapLoadedEmergencyStopRelease(this WebApplication app)
    {
        app.MapPost(Route, HandleAsync)
            .WithName("ReleaseLoadedVehicleEmergencyStop")
            .WithSummary("Release this server's emergency stop on a loaded vehicle (REQ-0356 as revised by CP-0011)")
            .WithDescription(
                "Checks the operator's role, the reason, the confirmations, a CAN_RECOVER latch with this server's stop "
                + "in effect and RIoT's unfinished orders (only this server's own order read PAUSED may stay), records an "
                + "immutable administrator audit for every request, then calls cancelEmergency.")
            .Produces<LoadedEmergencyStopReleaseResponse>(StatusCodes.Status200OK)
            .Produces<LoadedEmergencyStopReleaseResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Results<
        Ok<LoadedEmergencyStopReleaseResponse>,
        Accepted<LoadedEmergencyStopReleaseResponse>,
        UnauthorizedHttpResult,
        ProblemHttpResult>> HandleAsync(
        HttpContext context,
        LoadedEmergencyStopReleaseRequest request,
        LoadedEmergencyStopRelease release,
        VehicleRoster roster,
        IOptions<EmergencyStopReleaseOptions> releaseOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(releaseOptions);

        context.Response.Headers.CacheControl = "no-store";
        string? named = request?.AgvId;
        string? expected = Environment.GetEnvironmentVariable(releaseOptions.Value.LoadedCredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
        {
            await release.RecordTurnedAwayAsync(named, "UNAVAILABLE", cancellationToken).ConfigureAwait(false);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Loaded vehicle emergency stop release unavailable");
        }

        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header.Parameter)))
        {
            await release.RecordTurnedAwayAsync(named, "UNAUTHORIZED", cancellationToken).ConfigureAwait(false);
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.AgvId))
        {
            await release.RecordTurnedAwayAsync(named, "INCOMPLETE", cancellationToken).ConfigureAwait(false);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Incomplete release request",
                detail: "agvId is required.");
        }

        if (roster.ByAgvId(request.AgvId) is not FleetVehicle vehicle)
        {
            await release.RecordTurnedAwayAsync(named, "UNKNOWN_VEHICLE", cancellationToken).ConfigureAwait(false);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Unknown vehicle",
                detail: $"'{request.AgvId}' is not a vehicle this server drives.");
        }

        LoadedEmergencyStopReleaseResult result;
        try
        {
            result = await release.DecideAsync(
                new EmergencyStopSubject(vehicle.AgvId, vehicle.VehicleKey), request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Best effort: the failure may be the database the audit is written to. The original error is what goes out.
            try
            {
                await release.RecordTurnedAwayAsync(vehicle.AgvId, "FAILED", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception auditError) when (auditError is not OperationCanceledException)
            {
                throw new AggregateException(error, auditError);
            }

            throw;
        }

        EmergencyStopDecision? decision = result.Decision;
        LoadedEmergencyStopReleaseResponse body = new(
            vehicle.AgvId,
            decision?.Action.ToString() ?? EmergencyStopAction.RecoveryRefused.ToString(),
            decision?.Emergency.EmergencyState,
            result.Codes,
            decision?.AlarmCode,
            decision?.Attempt?.CommandAuditId,
            decision?.Attempt?.Outcome.ToString(),
            result.AuditRecordId);
        return decision?.Action switch
        {
            EmergencyStopAction.Recovered => TypedResults.Ok(body),
            EmergencyStopAction.RecoveryUnconfirmed or EmergencyStopAction.AwaitingRetry =>
                TypedResults.Accepted((string?)null, body),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Loaded vehicle emergency stop release refused",
                detail: string.Join(',', result.Codes),
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["agvId"] = vehicle.AgvId,
                    ["emergencyState"] = decision?.Emergency.EmergencyState,
                    ["reasons"] = result.Codes,
                    ["descriptions"] = result.Codes
                        .Select(code => LoadedEmergencyStopRelease.Descriptions.TryGetValue(code, out string? text) ? text : code)
                        .ToArray(),
                    ["alarmCode"] = decision?.AlarmCode,
                    ["auditRecordId"] = result.AuditRecordId,
                })
        };
    }
}

/// <summary>What the release did, with the command audit row and the administrator audit record to look it up by.</summary>
public sealed record LoadedEmergencyStopReleaseResponse(
    string AgvId,
    string Action,
    string? EmergencyState,
    IReadOnlyList<string> Reasons,
    string? AlarmCode,
    string? CommandAuditId,
    string? Outcome,
    string AuditRecordId);
