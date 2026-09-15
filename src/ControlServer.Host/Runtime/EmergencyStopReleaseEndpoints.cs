using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Fleet;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// REQ-0356's entry point: a person confirms that a latched vehicle may be released, and the server
/// releases it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything is decided by <see cref="EmergencyStopSupervisor.ReleaseOnConfirmationAsync"/>.</b>
/// This only authenticates the call, resolves the named vehicle and maps the decision onto HTTP. A
/// second copy of the release rules here would be a second answer to the same question.
/// </para>
/// <para>
/// <b>The vehicle is named, never inferred.</b> REQ-0356 asks for an explicitly selected vehicle, so
/// the request carries the <c>agvId</c> and it is resolved through the roster; a vehicle this server
/// does not drive is a 404 rather than a release aimed at whatever RIoT happens to call by that name.
/// </para>
/// <para>
/// <b>200 only when RIoT read the latch back <c>OK</c>.</b> RIoT takes seconds to clear it (3.6 s on
/// agv02 on 2026-09-15), so a release that went out usually comes back 202: issued, not yet seen to
/// work. The evaluation that later sees <c>OK</c> settles it. A refusal is 409 with every reason, so
/// the person at the vehicle learns all of what is missing at once.
/// </para>
/// <para>
/// Authentication is the shared bearer credential described on
/// <see cref="EmergencyStopReleaseOptions"/>, and it is not a login.
/// </para>
/// </remarks>
public static class EmergencyStopReleaseEndpoints
{
    public const string Route = "/api/safety/v1/emergency-stop-releases";

    public static void MapEmergencyStopRelease(this WebApplication app)
    {
        app.MapPost(Route, HandleAsync)
            .WithName("ReleaseEmergencyStopOnConfirmation")
            .WithSummary("Release a latched vehicle on a person's confirmation (REQ-0356)")
            .WithDescription(
                "Checks the three confirmations, the operator identity, a CAN_RECOVER latch raised by this "
                + "server and no unfinished RIoT order on the vehicle, then calls cancelEmergency. Returns 200 "
                + "when RIoT read back OK, 202 when the release went out and OK has not been seen yet.")
            .Produces<EmergencyStopReleaseResponse>(StatusCodes.Status200OK)
            .Produces<EmergencyStopReleaseResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Results<
        Ok<EmergencyStopReleaseResponse>,
        Accepted<EmergencyStopReleaseResponse>,
        UnauthorizedHttpResult,
        ProblemHttpResult>> HandleAsync(
        HttpContext context,
        EmergencyStopReleaseRequest request,
        EmergencyStopSupervisor supervisor,
        VehicleRoster roster,
        IOptions<EmergencyStopReleaseOptions> releaseOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(releaseOptions);

        EmergencyStopReleaseOptions options = releaseOptions.Value;
        context.Response.Headers.CacheControl = "no-store";

        string? expected = Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Emergency stop release unavailable");

        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            !FixedTimeEquals(expected, header.Parameter))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }

        if (request is null ||
            string.IsNullOrWhiteSpace(request.AgvId) ||
            string.IsNullOrWhiteSpace(request.OperatorId))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Incomplete release request",
                detail: "agvId and operatorId are both required.");
        }

        if (roster.ByAgvId(request.AgvId) is not FleetVehicle vehicle)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Unknown vehicle",
                detail: $"'{request.AgvId}' is not a vehicle this server drives.");
        }

        EmergencyStopDecision decision = await supervisor.ReleaseOnConfirmationAsync(
            new EmergencyStopReleaseConfirmation(
                new EmergencyStopSubject(vehicle.AgvId, vehicle.VehicleKey),
                request.OperatorId,
                request.CauseCleared,
                request.VehicleEmpty,
                request.AllDoorsClosed,
                request.Note),
            cancellationToken).ConfigureAwait(false);

        EmergencyStopReleaseResponse body = new(
            vehicle.AgvId,
            decision.Action.ToString(),
            decision.Emergency.EmergencyState,
            decision.Reasons,
            decision.AlarmCode,
            decision.Attempt?.CommandAuditId,
            decision.Attempt?.Outcome.ToString());
        return decision.Action switch
        {
            EmergencyStopAction.Recovered => TypedResults.Ok(body),
            // No Location: there is no resource to poll. The audit row named in the body is where
            // the outcome is settled, by the evaluation that reads OK.
            EmergencyStopAction.RecoveryUnconfirmed or EmergencyStopAction.AwaitingRetry =>
                TypedResults.Accepted((string?)null, body),
            _ => TypedResults.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Emergency stop release refused",
                detail: string.Join(',', decision.Reasons),
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["agvId"] = vehicle.AgvId,
                    ["emergencyState"] = decision.Emergency.EmergencyState,
                    ["reasons"] = decision.Reasons,
                    ["alarmCode"] = decision.AlarmCode,
                })
        };
    }

    private static bool FixedTimeEquals(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(presented));
}

/// <summary>
/// What a person submits to release a latched vehicle. The three confirmations default to false, so
/// a request that leaves one out is refused for it rather than read as confirming it.
/// </summary>
public sealed record EmergencyStopReleaseRequest(
    string AgvId,
    string OperatorId,
    bool CauseCleared,
    bool VehicleEmpty,
    bool AllDoorsClosed,
    string? Note);

/// <summary>What the release did, with the audit row to look it up by.</summary>
public sealed record EmergencyStopReleaseResponse(
    string AgvId,
    string Action,
    string? EmergencyState,
    IReadOnlyList<string> Reasons,
    string? AlarmCode,
    string? CommandAuditId,
    string? Outcome);
