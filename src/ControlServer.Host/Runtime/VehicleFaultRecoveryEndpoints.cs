using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Host.Runtime.Commands;
using ControlServer.Host.Runtime.Faults;
using ControlServer.Host.Runtime.Fleet;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>
/// The person's entry point out of a vehicle fault (control-server#299): clear it, or continue a held order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything is decided by <see cref="VehicleFaultRecoveryService"/>.</b> This authenticates the call, resolves the
/// named vehicle and maps the decision onto HTTP, in the shape of <see cref="EmergencyStopReleaseEndpoints"/>: the vehicle
/// is named, never inferred, and a vehicle this server does not drive is a 404.
/// </para>
/// <para>
/// 200 when the fault was cleared or the order continued, and when the same clearance had already been made (the body
/// says which). A refusal is 409 with every reason. 503 when the entry point is not configured, or when the runtime
/// round in progress did not end in time.
/// </para>
/// <para>
/// There is no action for rebuilding an order of this server's that was cancelled in RIoT: by the user's decision of
/// 2026-09-22 control-server#318 rebuilds it on its own, without a person's confirmation, because usually nobody is
/// watching the system. An unknown action is a 422.
/// </para>
/// </remarks>
public static class VehicleFaultRecoveryEndpoints
{
    public const string Route = "/api/safety/v1/vehicle-fault-recoveries";

    /// <summary>The <c>action</c> values the request accepts, and what each asks for.</summary>
    public static IReadOnlyDictionary<string, VehicleFaultRecoveryAction> Actions { get; } =
        new Dictionary<string, VehicleFaultRecoveryAction>(StringComparer.Ordinal)
        {
            ["CLEAR_FAULT"] = VehicleFaultRecoveryAction.ClearFault,
            ["RESUME_HELD_ORDER"] = VehicleFaultRecoveryAction.ResumeHeldOrder,
        };

    public static void MapVehicleFaultRecovery(this WebApplication app)
    {
        app.MapPost(Route, HandleAsync)
            .WithName("RecoverVehicleFault")
            .WithSummary("Clear a vehicle fault, or continue its held order, on a person's confirmation (control-server#299)")
            .WithDescription(
                "Checks the operator identity and the confirmation that the fault was removed, then reads for itself that the "
                + "fault is in effect, the order it stopped on has FAILED (an order cancelled or deleted in RIoT is refused: it "
                + "is rebuilt, not redispatched), RIoT holds no unfinished order for the vehicle and no emergency stop is latched "
                + "or open. Clearing never releases a latch.")
            .Produces<VehicleFaultRecoveryResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Results<
        Ok<VehicleFaultRecoveryResponse>,
        UnauthorizedHttpResult,
        ProblemHttpResult>> HandleAsync(
        HttpContext context,
        VehicleFaultRecoveryHttpRequest request,
        VehicleFaultRecoveryService recovery,
        VehicleRoster roster,
        IOptions<VehicleFaultRecoveryOptions> recoveryOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(recoveryOptions);

        VehicleFaultRecoveryOptions options = recoveryOptions.Value;
        context.Response.Headers.CacheControl = "no-store";

        string? expected = Environment.GetEnvironmentVariable(options.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Vehicle fault recovery unavailable");

        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            !FixedTimeEquals(expected, header.Parameter))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }

        // An unnamed operator is not a malformed request but an unmet criterion, and is listed with the others by the
        // service, so it is not refused here. A missing vehicle or an unknown action is: there is nothing to judge.
        if (request is null ||
            string.IsNullOrWhiteSpace(request.AgvId) ||
            request.Action is null ||
            !Actions.TryGetValue(request.Action, out VehicleFaultRecoveryAction action))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Incomplete fault recovery request",
                detail: $"agvId is required, and action must be one of {string.Join(", ", Actions.Keys)}.");
        }

        if (roster.ByAgvId(request.AgvId) is not FleetVehicle vehicle)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Unknown vehicle",
                detail: $"'{request.AgvId}' is not a vehicle this server drives.");
        }

        VehicleFaultRecoveryDecision decision = await recovery.RecoverAsync(
            new VehicleFaultRecoveryRequest(
                new EmergencyStopSubject(vehicle.AgvId, vehicle.VehicleKey),
                action,
                request.OperatorId,
                request.FaultRemedied,
                request.Note),
            cancellationToken).ConfigureAwait(false);

        VehicleFaultRecoveryResponse body = new(
            vehicle.AgvId,
            request.Action,
            decision.Outcome.ToString(),
            decision.Disposition,
            decision.Reasons,
            decision.FaultGeneration);
        return decision.Outcome switch
        {
            VehicleFaultRecoveryOutcome.Cleared or VehicleFaultRecoveryOutcome.Resumed or VehicleFaultRecoveryOutcome.AlreadyCleared =>
                TypedResults.Ok(body),
            _ when decision.Reasons.Contains("FAULT_RECOVERY_RUNTIME_BUSY") =>
                Problem(StatusCodes.Status503ServiceUnavailable, "Runtime busy, try again", body),
            _ => Problem(StatusCodes.Status409Conflict, "Vehicle fault recovery refused", body),
        };
    }

    private static ProblemHttpResult Problem(int status, string title, VehicleFaultRecoveryResponse body) =>
        TypedResults.Problem(
            statusCode: status,
            title: title,
            detail: string.Join(',', body.Reasons),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["agvId"] = body.AgvId,
                ["action"] = body.Action,
                ["outcome"] = body.Outcome,
                ["reasons"] = body.Reasons,
                ["faultGeneration"] = body.FaultGeneration,
            });

    private static bool FixedTimeEquals(string expected, string presented) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(presented));
}

/// <summary>
/// What a person submits. <c>faultRemedied</c> defaults to false, so a request that leaves it out is refused for it rather
/// than read as confirming it.
/// </summary>
/// <param name="Action"><c>CLEAR_FAULT</c> or <c>RESUME_HELD_ORDER</c>.</param>
public sealed record VehicleFaultRecoveryHttpRequest(
    string AgvId,
    string? OperatorId,
    string? Action,
    bool FaultRemedied,
    string? Note);

/// <summary>What the request came to.</summary>
public sealed record VehicleFaultRecoveryResponse(
    string AgvId,
    string Action,
    string Outcome,
    string Disposition,
    IReadOnlyList<string> Reasons,
    long? FaultGeneration);
