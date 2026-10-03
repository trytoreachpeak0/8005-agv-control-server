using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ControlServer.Host.Runtime.Fleet;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace ControlServer.Host.Runtime;

/// <summary>A request to settle a waiting-point move whose arrival cannot be proven (control-server#447).</summary>
public sealed record WaitingPointArrivalSettlementHttpRequest(
    string? AgvId,
    string? JourneyId,
    int StationId,
    string? Verdict,
    string? OperatorId,
    string? Reason,
    string? SiteVerification);

/// <summary>The decision, settled or refused; <c>Descriptions</c> carries the Chinese text of every refusal code.</summary>
public sealed record WaitingPointArrivalSettlementResponse(
    string AgvId,
    string? JourneyId,
    int StationId,
    string Outcome,
    string? Ending,
    IReadOnlyList<string> Codes,
    IReadOnlyDictionary<string, string> Descriptions,
    string AuditRecordId);

/// <summary>
/// The server-side entry out of a waiting-point move that RIoT reports SUCCESS and whose arrival the server cannot prove
/// (control-server#447): a named R-11 or R-13 person says whether the vehicle stands on the point.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything is decided by <see cref="WaitingPointArrivalSettlement"/></b>; this authenticates the call, resolves the named
/// vehicle and maps the decision onto HTTP. Same credential and same switch as <see cref="VehicleFaultRecoveryEndpoints"/>: it is
/// the same kind of act, a named person on site deciding about a stuck vehicle, and it loosens. Mapped only when
/// <c>VehicleFaultRecovery:enabled</c>.
/// </para>
/// <para>
/// 200 when settled; 409 with every code (and its Chinese description) when refused; 401 without the credential; 404 for a
/// vehicle this server does not drive; 422 when no vehicle, journey or station is named at all; 503 when the credential variable
/// is empty. Every request that gets past those writes an administrator audit record.
/// </para>
/// </remarks>
public static class WaitingPointArrivalSettlementEndpoints
{
    public const string Route = "/api/field-ops/v1/waiting-point-arrival-settlements";

    public static void MapWaitingPointArrivalSettlement(this WebApplication app)
    {
        app.MapPost(Route, HandleAsync)
            .WithName("SettleWaitingPointArrivalByHand")
            .WithSummary("Settle a waiting-point move RIoT reports SUCCESS whose arrival cannot be proven (control-server#447)")
            .WithDescription(
                "Checks the R-11/R-13 operator, the reason and the site verification, that the journey has carried its "
                + "arrival-not-proven code past JourneyRuntime:OwnOrderRebuildRepeatWindow and still holds the waiting point, that "
                + "RIoT reports the order exactly SUCCESS and the vehicle online, stopped and without an order, and that the verdict "
                + "does not contradict RIoT's station; then closes it through the normal path's bookkeeping.")
            .Produces<WaitingPointArrivalSettlementResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    public static async Task<Results<
        Ok<WaitingPointArrivalSettlementResponse>,
        UnauthorizedHttpResult,
        ProblemHttpResult>> HandleAsync(
        HttpContext context,
        WaitingPointArrivalSettlementHttpRequest request,
        WaitingPointArrivalSettlement settlement,
        VehicleRoster roster,
        IOptions<VehicleFaultRecoveryOptions> recoveryOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(recoveryOptions);

        context.Response.Headers.CacheControl = "no-store";
        string? expected = Environment.GetEnvironmentVariable(recoveryOptions.Value.CredentialEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(expected))
            return TypedResults.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Waiting point arrival settlement unavailable");

        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out AuthenticationHeaderValue? header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header.Parameter)))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return TypedResults.Unauthorized();
        }

        // A missing operator, reason, verdict or site verification is an unmet criterion, audited with the others; only a
        // request that names no vehicle, journey or station has nothing to judge.
        if (request is null || string.IsNullOrWhiteSpace(request.AgvId) || string.IsNullOrWhiteSpace(request.JourneyId) ||
            request.StationId <= 0)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Incomplete waiting point arrival settlement request",
                detail: "agvId, journeyId and a positive stationId are all required.");
        }
        if (roster.ByAgvId(request.AgvId.Trim()) is not FleetVehicle vehicle)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Unknown vehicle",
                detail: $"'{request.AgvId}' is not a vehicle this server drives.");
        }

        WaitingPointArrivalSettlementResult result = await settlement.DecideAsync(
            new WaitingPointArrivalSettlementRequest(
                vehicle.AgvId,
                vehicle.VehicleKey,
                request.JourneyId,
                request.StationId,
                request.Verdict,
                request.OperatorId,
                request.Reason,
                request.SiteVerification),
            cancellationToken).ConfigureAwait(false);

        WaitingPointArrivalSettlementResponse body = new(
            vehicle.AgvId,
            request.JourneyId?.Trim(),
            request.StationId,
            result.Settled ? "SETTLED" : "REJECTED",
            result.Ending,
            result.Codes,
            result.Codes.Distinct(StringComparer.Ordinal).ToDictionary(
                code => code,
                code => WaitingPointArrivalSettlement.Descriptions.GetValueOrDefault(code, code),
                StringComparer.Ordinal),
            result.AuditRecordId);
        if (result.Settled)
        {
            return TypedResults.Ok(body);
        }
        return TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Waiting point arrival settlement refused",
            detail: string.Join(',', result.Codes),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["agvId"] = body.AgvId,
                ["journeyId"] = body.JourneyId,
                ["stationId"] = body.StationId,
                ["outcome"] = body.Outcome,
                ["codes"] = body.Codes,
                ["descriptions"] = body.Descriptions,
                ["auditRecordId"] = body.AuditRecordId,
            });
    }
}
